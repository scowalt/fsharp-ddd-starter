namespace StarterKit.Web.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Microsoft.Data.Sqlite
open Xunit
open StarterKit.Web

module OperationalFixture =
    type DirectoryFixture() =
        let path = Path.Combine(Path.GetTempPath(), $"sqlite-ops-{Guid.NewGuid():N}")
        do Directory.CreateDirectory path |> ignore
        member _.Path = path
        member _.File name = Path.Combine(path, name)

        interface IDisposable with
            member _.Dispose() = Directory.Delete(path, true)

    let execute path sql =
        use connection =
            new SqliteConnection(SqliteConnectionStringBuilder(DataSource = path, Pooling = false).ConnectionString)

        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteScalar()

    let cliWithInspection cwd args inspect =
        let start =
            ProcessStartInfo(
                "dotnet",
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            )

        start.ArgumentList.Add(typeof<RouteDependencies>.Assembly.Location)

        for arg in args do
            start.ArgumentList.Add arg
        // If an operational command accidentally reaches hosting, fail instead of listening.
        start.Environment["ASPNETCORE_URLS"] <- "invalid://must-not-start"
        use child = Process.Start start
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()

        try
            inspect child

            if not (child.WaitForExit(15000)) then
                failwith "Database CLI exceeded its test deadline."

            child.ExitCode, stdout.Result, stderr.Result
        finally
            if not child.HasExited then
                child.Kill(true)
                child.WaitForExit()

    let cli cwd args = cliWithInspection cwd args ignore

open OperationalFixture

type OperationalTests() =
    [<Fact>]
    member _.``real CLI snapshot includes WAL commits without migrations or hosting and creates a private validated file``
        ()
        =
        use fixture = new DirectoryFixture()
        let source = fixture.File "source;private.db"
        let destination = fixture.File "snapshot.db"

        use writer =
            new SqliteConnection(SqliteConnectionStringBuilder(DataSource = source, Pooling = false).ConnectionString)

        writer.Open()
        use command = writer.CreateCommand()

        command.CommandText <-
            "PRAGMA journal_mode=WAL; CREATE TABLE evidence(value TEXT); INSERT INTO evidence VALUES ('committed WAL data');"

        command.ExecuteNonQuery() |> ignore
        Assert.True(File.Exists(source + "-wal"))
        let sourceBefore = File.ReadAllBytes source
        let walBefore = File.ReadAllBytes(source + "-wal")
        let code, stdout, stderr = cli fixture.Path [ "--snapshot-db"; source; destination ]
        Assert.True((code = 0), $"exit={code}; stderr={stderr}")
        Assert.Equal("", stderr)
        use json = JsonDocument.Parse stdout
        Assert.Equal("snapshot", json.RootElement.GetProperty("operation").GetString())
        Assert.Equal("ok", json.RootElement.GetProperty("integrity").GetString())
        Assert.Equal(0, json.RootElement.GetProperty("migrationHistory").GetArrayLength())
        Assert.Equal(64, json.RootElement.GetProperty("sha256").GetString().Length)
        Assert.DoesNotContain("committed WAL data", stdout)
        // A recovery snapshot is a standalone rollback-journal database, not a WAL database.
        let header = File.ReadAllBytes destination
        Assert.Equal(1uy, header[18])
        Assert.Equal(1uy, header[19])
        Assert.False(File.Exists(destination + "-wal"))
        Assert.False(File.Exists(destination + "-shm"))
        Assert.Equal("committed WAL data", execute destination "SELECT value FROM evidence;" :?> string)

        Assert.Equal(
            0L,
            execute
                destination
                "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('work_items', '__migration_history');"
            :?> int64
        )

        if not (OperatingSystem.IsWindows()) then
            Assert.Equal(UnixFileMode.UserRead ||| UnixFileMode.UserWrite, File.GetUnixFileMode destination)

        let validateCode, validated, validateError =
            cli fixture.Path [ "--validate-db"; destination ]

        Assert.True((validateCode = 0), validateError)
        use report = JsonDocument.Parse validated
        Assert.Equal("validate", report.RootElement.GetProperty("operation").GetString())
        Assert.False(File.Exists(destination + "-wal"))
        Assert.False(File.Exists(destination + "-shm"))

        Assert.Equal(
            json.RootElement.GetProperty("sha256").GetString(),
            report.RootElement.GetProperty("sha256").GetString()
        )

        Assert.False(Directory.Exists(fixture.File "data"))
        Assert.Equal<byte>(sourceBefore, File.ReadAllBytes source)
        Assert.Equal<byte>(walBefore, File.ReadAllBytes(source + "-wal"))

    [<Theory>]
    [<InlineData("missing")>]
    [<InlineData("corrupt")>]
    [<InlineData("empty")>]
    [<InlineData("foreign-key")>]
    member _.``real operations refuse missing or invalid source without creating database or snapshot``(kind: string) =
        use fixture = new DirectoryFixture()
        let source = fixture.File "sensitive-source.db"
        let destination = fixture.File "destination.db"

        match kind with
        | "corrupt" -> File.WriteAllText(source, "secret corrupt database bytes")
        | "empty" -> File.WriteAllBytes(source, [||])
        | "foreign-key" ->
            execute
                source
                "PRAGMA foreign_keys=OFF; CREATE TABLE parent(id INTEGER PRIMARY KEY); CREATE TABLE child(parent_id INTEGER REFERENCES parent(id)); INSERT INTO child VALUES (99);"
            |> ignore
        | _ -> ()

        let before =
            if File.Exists source then
                Some(File.ReadAllBytes source)
            else
                None

        for args in [ [ "--validate-db"; source ]; [ "--snapshot-db"; source; destination ] ] do
            let code, stdout, stderr = cli fixture.Path args
            Assert.NotEqual(0, code)
            Assert.Equal("", stdout)
            Assert.DoesNotContain(source, stderr)
            Assert.DoesNotContain("secret", stderr)
            Assert.False(File.Exists destination)

        match before with
        | None -> Assert.False(File.Exists source)
        | Some bytes -> Assert.Equal<byte>(bytes, File.ReadAllBytes source)

    [<Fact>]
    member _.``snapshot refuses an existing destination without overwriting it``() =
        use fixture = new DirectoryFixture()
        let source = fixture.File "source.db"
        let destination = fixture.File "existing.db"
        execute source "CREATE TABLE sample(value TEXT);" |> ignore
        File.WriteAllText(destination, "must survive")
        let code, stdout, _ = cli fixture.Path [ "--snapshot-db"; source; destination ]
        Assert.NotEqual(0, code)
        Assert.Equal("", stdout)
        Assert.Equal("must survive", File.ReadAllText destination)

    [<Theory>]
    [<InlineData("-journal", "file")>]
    [<InlineData("-journal", "directory")>]
    [<InlineData("-journal", "symlink")>]
    [<InlineData("-wal", "file")>]
    [<InlineData("-wal", "directory")>]
    [<InlineData("-wal", "symlink")>]
    [<InlineData("-shm", "file")>]
    [<InlineData("-shm", "directory")>]
    [<InlineData("-shm", "symlink")>]
    [<InlineData("", "directory")>]
    [<InlineData("", "symlink")>]
    member _.``snapshot refuses existing destination entries including dangling companion symlinks``
        (suffix: string, kind: string)
        =
        use fixture = new DirectoryFixture()
        let source = fixture.File "source.db"
        let destination = fixture.File "snapshot.db"
        let occupied = destination + suffix
        let missingTarget = fixture.File "must-stay-missing"
        execute source "CREATE TABLE sample(value TEXT);" |> ignore
        let sourceBefore = File.ReadAllBytes source

        match kind with
        | "directory" ->
            Directory.CreateDirectory occupied |> ignore
            File.WriteAllText(Path.Combine(occupied, "existing"), "must survive")
        | "symlink" -> File.CreateSymbolicLink(occupied, missingTarget) |> ignore
        | _ -> File.WriteAllText(occupied, "must survive")

        let code, stdout, stderr = cli fixture.Path [ "--snapshot-db"; source; destination ]

        match kind with
        | "directory" -> Assert.Equal("must survive", File.ReadAllText(Path.Combine(occupied, "existing")))
        | "symlink" ->
            Assert.Equal(missingTarget, FileInfo(occupied).LinkTarget)
            Assert.False(File.Exists missingTarget)
        | _ -> Assert.Equal("must survive", File.ReadAllText occupied)

        Assert.NotEqual(0, code)
        Assert.Equal("", stdout)
        Assert.Contains("DestinationExists", stderr)

        if suffix <> "" then
            Assert.False(File.Exists destination)

        Assert.Equal<byte>(sourceBefore, File.ReadAllBytes source)

    [<Fact>]
    member _.``snapshot failure never deletes pre-existing companions it does not own``() =
        use fixture = new DirectoryFixture()
        let source = fixture.File "source.db"
        let destination = fixture.File "snapshot.db"
        execute source "CREATE TABLE sample(value TEXT);" |> ignore
        // On the old path the journal directory makes SQLite fail after creating the
        // destination, then cleanup deletes the unrelated WAL and SHM files.
        Directory.CreateDirectory(destination + "-journal") |> ignore
        File.WriteAllText(destination + "-wal", "existing WAL")
        File.WriteAllText(destination + "-shm", "existing SHM")
        let code, stdout, _ = cli fixture.Path [ "--snapshot-db"; source; destination ]
        Assert.NotEqual(0, code)
        Assert.Equal("", stdout)
        Assert.Equal("existing WAL", File.ReadAllText(destination + "-wal"))
        Assert.Equal("existing SHM", File.ReadAllText(destination + "-shm"))
        Assert.True(Directory.Exists(destination + "-journal"))
        Assert.False(File.Exists destination)

    [<Theory>]
    [<InlineData("-journal", "file", true)>]
    [<InlineData("-wal", "directory", true)>]
    [<InlineData("-shm", "symlink", true)>]
    [<InlineData("", "file", true)>]
    [<InlineData("-wal", "file", false)>]
    member _.``snapshot owns failure cleanup and rechecks paths appearing during capture``
        (suffix: string, kind: string, finishCapture: bool)
        =
        use fixture = new DirectoryFixture()
        let source = fixture.File "source.db"
        let outputDirectory = fixture.File "output"
        Directory.CreateDirectory outputDirectory |> ignore
        let destination = Path.Combine(outputDirectory, "snapshot.db")
        let occupied = destination + suffix
        let missingTarget = fixture.File "must-stay-missing"

        execute source "CREATE TABLE sample(value TEXT); INSERT INTO sample VALUES ('unchanged');"
        |> ignore

        let sourceBefore = File.ReadAllBytes source

        use blocker =
            new SqliteConnection(SqliteConnectionStringBuilder(DataSource = source, Pooling = false).ConnectionString)

        blocker.Open()
        use command = blocker.CreateCommand()
        command.CommandText <- "BEGIN EXCLUSIVE;"
        command.ExecuteNonQuery() |> ignore

        let inspect (child: Process) =
            // An actual SQLite lock pauses capture after its initial path check. Observe
            // owned staging, then inject filesystem changes through the public CLI seam.
            let timer = Stopwatch.StartNew()

            while not child.HasExited
                  && timer.Elapsed < TimeSpan.FromSeconds(5.0)
                  && Directory.GetDirectories(outputDirectory).Length = 0 do
                System.Threading.Thread.Sleep 5

            Assert.False(child.HasExited, "Capture exited before owned staging could be observed.")
            let staging = Assert.Single(Directory.GetDirectories(outputDirectory))

            Assert.Equal(
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute,
                File.GetUnixFileMode staging
            )

            Assert.False(File.Exists destination)

            match kind with
            | "directory" ->
                Directory.CreateDirectory occupied |> ignore
                File.WriteAllText(Path.Combine(occupied, "existing"), "must survive")
            | "symlink" -> File.CreateSymbolicLink(occupied, missingTarget) |> ignore
            | _ -> File.WriteAllText(occupied, "must survive")

            if finishCapture then
                command.CommandText <- "ROLLBACK;"
                command.ExecuteNonQuery() |> ignore

        let code, stdout, stderr =
            cliWithInspection fixture.Path [ "--snapshot-db"; source; destination ] inspect

        match kind with
        | "directory" -> Assert.Equal("must survive", File.ReadAllText(Path.Combine(occupied, "existing")))
        | "symlink" ->
            Assert.Equal(missingTarget, FileInfo(occupied).LinkTarget)
            Assert.False(File.Exists missingTarget)
        | _ -> Assert.Equal("must survive", File.ReadAllText occupied)

        Assert.NotEqual(0, code)
        Assert.Equal("", stdout)

        if finishCapture then
            Assert.Contains("DestinationExists", stderr)

        if suffix <> "" then
            Assert.False(File.Exists destination)

        Assert.Equal<string>([| occupied |], Directory.GetFileSystemEntries(outputDirectory))
        Assert.Equal<byte>(sourceBefore, File.ReadAllBytes source)

    [<Fact>]
    member _.``snapshot metadata identifies journal history without exposing work item data``() =
        use fixture = new DirectoryFixture()
        let source = fixture.File "source.db"
        let destination = fixture.File "snapshot.db"

        let connectionString =
            SqliteConnectionStringBuilder(DataSource = source, Pooling = false).ConnectionString

        let migrated =
            StarterKit.WorkTracking.Infrastructure.SqliteMigrations.run
                connectionString
                (StarterKit.WorkTracking.Infrastructure.Embedded
                    typeof<StarterKit.WorkTracking.Infrastructure.MigrationScript>.Assembly)

        Assert.True(Result.isOk migrated)
        let code, stdout, stderr = cli fixture.Path [ "--snapshot-db"; source; destination ]
        Assert.True((code = 0), stderr)
        use json = JsonDocument.Parse stdout
        let history = json.RootElement.GetProperty("migrationHistory")
        Assert.Equal(1, history.GetArrayLength())
        Assert.Equal("worktracking/001_initial.sql", history[0].GetProperty("id").GetString())
        Assert.Equal(64, history[0].GetProperty("sha256").GetString().Length)

    [<Fact>]
    member _.``malformed journal metadata is refused instead of printed as successful operational output``() =
        use fixture = new DirectoryFixture()
        let source = fixture.File "metadata.db"

        execute
            source
            "CREATE TABLE __migration_history(sequence INTEGER, id TEXT, sha256 TEXT, applied_at TEXT); INSERT INTO __migration_history VALUES (1, 'sensitive application data', 'invalid', 'invalid');"
        |> ignore

        for args in
            [ [ "--validate-db"; source ]
              [ "--snapshot-db"; source; fixture.File "out.db" ] ] do
            let code, stdout, stderr = cli fixture.Path args
            Assert.NotEqual(0, code)
            Assert.Equal("", stdout)
            Assert.DoesNotContain("sensitive", stderr)

    [<Theory>]
    [<InlineData("--snapshot-db")>]
    [<InlineData("--validate-db")>]
    [<InlineData("--validate-db=wrong")>]
    member _.``malformed operation syntax fails without attempting hosting``(argument: string) =
        use fixture = new DirectoryFixture()
        let code, stdout, stderr = cli fixture.Path [ argument ]
        Assert.Equal(64, code)
        Assert.Equal("", stdout)
        Assert.Contains("Usage:", stderr)
        Assert.False(Directory.Exists(fixture.File "data"))
