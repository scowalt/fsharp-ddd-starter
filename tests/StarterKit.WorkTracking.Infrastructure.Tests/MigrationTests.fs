namespace StarterKit.WorkTracking.Infrastructure.Tests

open System
open System.IO
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open System.Text
open Microsoft.Data.Sqlite
open Xunit
open StarterKit.WorkTracking.Infrastructure

module MigrationFixture =
    type Database() =
        let path = Path.Combine(Path.GetTempPath(), $"migration-{Guid.NewGuid():N}.db")
        member _.Path = path
        member _.ConnectionString = $"Data Source={path};Pooling=False;Foreign Keys=True"

        member this.Query(sql: string) =
            use connection = new SqliteConnection(this.ConnectionString)
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- sql
            command.ExecuteScalar()

        interface IDisposable with
            member _.Dispose() =
                for suffix in [ ""; "-wal"; "-shm"; "-journal" ] do
                    File.Delete(path + suffix)

    let script id text =
        { Id = id
          Bytes = Encoding.UTF8.GetBytes(text: string) }

    let first =
        script
            "worktracking/001_initial.sql"
            "CREATE TABLE sample(value TEXT); INSERT INTO sample VALUES ('preserved');\n"

    let run (db: Database) scripts =
        SqliteMigrations.run db.ConnectionString (Scripts scripts)

    let success result =
        match result with
        | Ok report -> report
        | Error error -> failwithf "Expected migration success, got %A" error

open MigrationFixture

type MigrationTests() =
    [<Fact>]
    member _.``empty database receives schema and checksum history once``() =
        use db = new Database()
        let report = run db [ first ] |> success
        Assert.Equal<string list>([ first.Id ], report.Applied)
        Assert.Equal("preserved", db.Query("SELECT value FROM sample;") :?> string)
        Assert.Equal(1L, db.Query("SELECT COUNT(*) FROM __migration_history;") :?> int64)
        Assert.Equal<string list>([], (run db [ first ] |> success).Applied)

        Assert.Equal(
            "53db2829ac83a55801a19a6e56f37fca264259ac165fcfb858abbd18d4272c9b",
            db.Query("SELECT sha256 FROM __migration_history;") :?> string
        )

    [<Fact>]
    member _.``execution preserves SQL literals instead of applying DbUp preprocessors or splitting text``() =
        use db = new Database()

        let sql =
            "CREATE TABLE sample(value TEXT); INSERT INTO sample VALUES ('nvarchar(max) $schema$. $missing$\n;\n');\n"

        run db [ script first.Id sql ] |> success |> ignore
        Assert.Equal("nvarchar(max) $schema$. $missing$\n;\n", db.Query("SELECT value FROM sample;") :?> string)

    [<Fact>]
    member _.``a late catalogue materialization failure happens before opening the database``() =
        use db = new Database()

        let scripts =
            seq {
                yield first
                failwith "simulated loading or preprocessing failure"
            }

        Assert.Equal(Error InvalidCatalogue, run db scripts)
        Assert.False(File.Exists db.Path)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``legacy schema is refused even with an empty replacement journal``(emptyJournal: bool) =
        use db = new Database()

        let legacy =
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy_v1.sql"))

        db.Query legacy |> ignore

        if emptyJournal then
            db.Query
                "CREATE TABLE __migration_history (sequence INTEGER PRIMARY KEY NOT NULL, id TEXT NOT NULL UNIQUE, sha256 TEXT NOT NULL, applied_at TEXT NOT NULL) STRICT;"
            |> ignore

        let before = File.ReadAllBytes db.Path
        Assert.Equal(Error LegacyDatabase, run db [ first ])
        Assert.Equal<byte>(before, File.ReadAllBytes db.Path)

    [<Theory>]
    [<InlineData("duplicate")>]
    [<InlineData("same-number")>]
    [<InlineData("invalid-id")>]
    [<InlineData("other-context")>]
    [<InlineData("bom")>]
    [<InlineData("crlf")>]
    [<InlineData("utf8")>]
    [<InlineData("nul")>]
    [<InlineData("empty")>]
    member _.``invalid release catalogues fail before database changes``(kind: string) =
        use db = new Database()
        let next = script "worktracking/002_next.sql" "SELECT 1;\n"

        let scripts =
            match kind with
            | "duplicate" -> [ first; first ]
            | "same-number" ->
                [ first
                  { next with
                      Id = "worktracking/001_other.sql" } ]
            | "invalid-id" -> [ first; { next with Id = "namespace.002.sql" } ]
            | "other-context" ->
                [ first
                  { next with
                      Id = "unrelated/002_next.sql" } ]
            | "bom" ->
                [ first
                  { next with
                      Bytes = Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] next.Bytes } ]
            | "crlf" -> [ first; script next.Id "SELECT 1;\r\n" ]
            | "utf8" -> [ first; { next with Bytes = [| 0xFFuy |] } ]
            | "nul" -> [ first; script next.Id "SELECT 1;\u0000" ]
            | _ -> []

        Assert.Equal(Error InvalidCatalogue, run db scripts)
        Assert.False(File.Exists db.Path)

    [<Fact>]
    member _.``journaled upgrade preserves application data and accepts an unordered input catalogue``() =
        use db = new Database()
        run db [ first ] |> success |> ignore

        let next =
            script "worktracking/003_next.sql" "ALTER TABLE sample ADD COLUMN extra TEXT;\n"

        Assert.Equal<string list>([ next.Id ], (run db [ next; first ] |> success).Applied)
        Assert.Equal("preserved", db.Query("SELECT value FROM sample;") :?> string)

    [<Theory>]
    [<InlineData("changed")>]
    [<InlineData("deleted")>]
    [<InlineData("unknown")>]
    [<InlineData("inserted-earlier")>]
    [<InlineData("bad-hash")>]
    [<InlineData("bad-time")>]
    [<InlineData("bad-sequence")>]
    [<InlineData("bad-table")>]
    [<InlineData("duplicate-row")>]
    member _.``complete history is validated even with no pending migration``(kind: string) =
        use db = new Database()

        let next =
            script "worktracking/003_next.sql" "ALTER TABLE sample ADD COLUMN extra TEXT;\n"

        run db [ first; next ] |> success |> ignore

        let scripts =
            match kind with
            | "changed" ->
                [ { first with
                      Bytes = Encoding.UTF8.GetBytes("SELECT 'changed';\n") }
                  next ]
            | "deleted" -> [ first ]
            | "inserted-earlier" -> [ first; script "worktracking/002_inserted.sql" "SELECT 1;\n"; next ]
            | _ -> [ first; next ]

        let alteration =
            match kind with
            | "unknown" -> "UPDATE __migration_history SET id = 'worktracking/009_unknown.sql' WHERE sequence = 2;"
            | "bad-hash" -> "UPDATE __migration_history SET sha256 = 'broken' WHERE sequence = 1;"
            | "bad-time" -> "UPDATE __migration_history SET applied_at = 'yesterday' WHERE sequence = 1;"
            | "bad-sequence" -> "UPDATE __migration_history SET sequence = 7 WHERE sequence = 2;"
            | "bad-table" -> "ALTER TABLE __migration_history RENAME COLUMN sha256 TO checksum;"
            | "duplicate-row" ->
                "ALTER TABLE __migration_history RENAME TO old_history; CREATE TABLE __migration_history AS SELECT * FROM old_history; INSERT INTO __migration_history SELECT * FROM old_history; DROP TABLE old_history;"
            | _ -> "SELECT 1;"

        db.Query alteration |> ignore
        let before = File.ReadAllBytes db.Path
        Assert.Equal(Error InvalidHistory, run db scripts)
        Assert.Equal<byte>(before, File.ReadAllBytes db.Path)

    [<Theory>]
    [<InlineData(false, false)>]
    [<InlineData(false, true)>]
    [<InlineData(true, false)>]
    [<InlineData(true, true)>]
    member _.``later SQL or journal failure rolls back the whole batch including first journal creation``
        (existing: bool, journalFailure: bool)
        =
        use db = new Database()

        if existing then
            run db [ first ] |> success |> ignore

        let second =
            script
                "worktracking/002_change.sql"
                "UPDATE sample SET value = 'must roll back'; CREATE TABLE earlier_batch(value TEXT);\n"

        let thirdSql =
            if journalFailure then
                "CREATE TRIGGER reject_journal BEFORE INSERT ON __migration_history WHEN NEW.sequence = 3 BEGIN SELECT RAISE(FAIL, 'fixture journal failure'); END;\n"
            else
                "INSERT INTO nonexistent_table VALUES (1);\n"

        let third = script "worktracking/003_fail.sql" thirdSql
        let result = run db [ first; second; third ]
        Assert.Equal(Error(MigrationFailed(Some third.Id)), result)
        Assert.Equal(0L, db.Query("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'earlier_batch';") :?> int64)

        if existing then
            Assert.Equal("preserved", db.Query("SELECT value FROM sample;") :?> string)
            Assert.Equal(1L, db.Query("SELECT COUNT(*) FROM __migration_history;") :?> int64)
        else
            Assert.Equal(0L, db.Query("SELECT COUNT(*) FROM sqlite_schema;") :?> int64)

    [<Fact>]
    member _.``late preprocessing validation failure preserves an existing committed database``() =
        use db = new Database()
        run db [ first ] |> success |> ignore
        let before = File.ReadAllBytes db.Path

        let scripts =
            seq {
                yield first
                yield script "worktracking/002_change.sql" "DELETE FROM sample;\n"
                failwith "fixture preprocessing failure"
            }

        Assert.Equal(Error InvalidCatalogue, run db scripts)
        Assert.Equal<byte>(before, File.ReadAllBytes db.Path)

    [<Theory>]
    [<InlineData("COMMIT;")>]
    [<InlineData("ROLLBACK;")>]
    [<InlineData("SAVEPOINT escape;")>]
    [<InlineData("PRAGMA writable_schema=ON;")>]
    [<InlineData("ATTACH DATABASE ':memory:' AS outside;")>]
    member _.``migration SQL cannot escape the owning transaction``(sql: string) =
        use db = new Database()
        let next = script "worktracking/002_escape.sql" sql
        Assert.Equal(Error(MigrationFailed(Some next.Id)), run db [ first; next ])
        Assert.Equal(0L, db.Query("SELECT COUNT(*) FROM sqlite_schema;") :?> int64)

    [<Fact>]
    member _.``sqlite-like user object names are not mistaken for internal objects``() =
        use db = new Database()
        db.Query "CREATE TABLE sqliteXdata(value TEXT);" |> ignore
        let before = File.ReadAllBytes db.Path
        Assert.Equal(Error LegacyDatabase, run db [ first ])
        Assert.Equal<byte>(before, File.ReadAllBytes db.Path)

    [<Theory>]
    [<InlineData("DELETE FROM __migration_history;")>]
    [<InlineData("UPDATE __migration_history SET sha256 = 'changed';")>]
    member _.``a script cannot commit a damaged history and postpone rejection until next startup``(damage: string) =
        use db = new Database()
        run db [ first ] |> success |> ignore
        let before = File.ReadAllBytes db.Path

        let next =
            script "worktracking/002_damage.sql" ($"UPDATE sample SET value = 'must roll back'; {damage}\n")

        Assert.Equal(Error InvalidHistory, run db [ first; next ])
        Assert.Equal<byte>(before, File.ReadAllBytes db.Path)

    [<Theory>]
    [<InlineData(true)>]
    [<InlineData(false)>]
    member _.``temporary journal names cannot divert durable history or cause migrations to rerun``
        (matchingColumns: bool)
        =
        use db = new Database()
        run db [ first ] |> success |> ignore

        let temporaryJournal =
            if matchingColumns then
                "CREATE TEMP TABLE __migration_history (sequence INTEGER PRIMARY KEY NOT NULL, id TEXT NOT NULL UNIQUE, sha256 TEXT NOT NULL, applied_at TEXT NOT NULL) STRICT; INSERT INTO temp.__migration_history SELECT * FROM main.__migration_history;"
            else
                "CREATE TEMP TABLE __migration_history (unrelated TEXT);"

        let next =
            script
                "worktracking/002_temporary.sql"
                ($"{temporaryJournal} INSERT INTO main.sample VALUES ('applied once');\n")

        Assert.Equal<string list>([ next.Id ], (run db [ first; next ] |> success).Applied)

        let historyBeforeRestart =
            db.Query("SELECT COUNT(*) FROM main.__migration_history;") :?> int64

        let restarted = run db [ first; next ] |> success

        let historyAfterRestart =
            db.Query("SELECT COUNT(*) FROM main.__migration_history;") :?> int64

        let applications =
            db.Query("SELECT COUNT(*) FROM main.sample WHERE value = 'applied once';") :?> int64

        Assert.Equal((2L, 2L, 1L, []), (historyBeforeRestart, historyAfterRestart, applications, restarted.Applied))

    [<Fact>]
    member _.``concurrent migration attempts acquire the write lock before reading history``() =
        use db = new Database()
        use ready = new CountdownEvent(2)
        use start = new ManualResetEventSlim(false)

        let attempt () =
            Task.Run(fun () ->
                ready.Signal() |> ignore
                start.Wait()
                run db [ first ] |> success)

        let left, right = attempt (), attempt ()
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5.0)))
        start.Set()
        Assert.True(Task.WaitAll([| left :> Task; right :> Task |], TimeSpan.FromSeconds(10.0)))
        Assert.Equal(1, left.Result.Applied.Length + right.Result.Applied.Length)
        Assert.Equal(1L, db.Query("SELECT COUNT(*) FROM sample;") :?> int64)
        Assert.Equal(1L, db.Query("SELECT COUNT(*) FROM __migration_history;") :?> int64)

    [<Fact>]
    member _.``missing embedded catalogue is refused before database creation``() =
        use db = new Database()

        Assert.Equal(
            Error InvalidCatalogue,
            SqliteMigrations.run db.ConnectionString (Embedded typeof<MigrationTests>.Assembly)
        )

        Assert.False(File.Exists db.Path)

    [<Fact>]
    member _.``commit failure is reported as uncertain rather than claiming successful rollback``() =
        use db = new Database()
        run db [ first ] |> success |> ignore

        let next =
            script
                "worktracking/002_deferred.sql"
                "CREATE TABLE parent(id INTEGER PRIMARY KEY); CREATE TABLE child(parent_id INTEGER REFERENCES parent(id) DEFERRABLE INITIALLY DEFERRED); INSERT INTO child VALUES (99);\n"

        Assert.Equal(Error TransactionOutcomeUncertain, run db [ first; next ])
        Assert.Equal(0L, db.Query("SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('parent', 'child');") :?> int64)
        Assert.Equal(1L, db.Query("SELECT COUNT(*) FROM __migration_history;") :?> int64)
        Assert.Equal("preserved", db.Query("SELECT value FROM sample;") :?> string)
        run db [ first ] |> success |> ignore

    [<Fact>]
    member _.``write lock timeout is bounded and does not poison the next migration attempt``() =
        use db = new Database()
        use blocker = new SqliteConnection(db.ConnectionString)
        blocker.Open()
        use transaction = blocker.BeginTransaction()
        let timer = Stopwatch.StartNew()
        Assert.Equal(Error DatabaseBusy, run db [ first ])
        Assert.InRange(timer.Elapsed.TotalSeconds, 1.0, 8.0)
        transaction.Rollback()
        run db [ first ] |> success |> ignore

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``killing a migration process restores committed schema data and history``(existing: bool) =
        use db = new Database()

        if existing then
            run db [ first ] |> success |> ignore

        let start =
            ProcessStartInfo("dotnet", RedirectStandardOutput = true, RedirectStandardError = true)

        start.ArgumentList.Add(typeof<MigrationTests>.Assembly.Location)
        start.ArgumentList.Add("--interrupt-fixture")
        start.ArgumentList.Add(db.Path)
        use child = Process.Start start

        try
            let timer = Stopwatch.StartNew()

            while not child.HasExited
                  && timer.Elapsed < TimeSpan.FromSeconds(15.0)
                  && not (File.Exists(db.Path + "-journal") && FileInfo(db.Path).Length > 1048576L) do
                Thread.Sleep 20

            Assert.False(child.HasExited, "The interruption fixture exited before pending work was observed.")
            Assert.True(File.Exists(db.Path + "-journal") && FileInfo(db.Path).Length > 1048576L)
            child.Kill(true)
            Assert.True(child.WaitForExit(5000))
            Assert.Equal(0L, db.Query("SELECT COUNT(*) FROM sqlite_schema WHERE name = 'pending_blob';") :?> int64)

            if existing then
                Assert.Equal("preserved", db.Query("SELECT value FROM sample;") :?> string)
                Assert.Equal(1L, db.Query("SELECT COUNT(*) FROM __migration_history;") :?> int64)
            else
                Assert.Equal(0L, db.Query("SELECT COUNT(*) FROM sqlite_schema;") :?> int64)

            run db [ first ] |> success |> ignore
        finally
            if not child.HasExited then
                child.Kill(true)
                child.WaitForExit()
