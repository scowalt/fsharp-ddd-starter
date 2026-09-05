namespace StarterKit.WorkTracking.Infrastructure

open System
open System.Data
open System.Globalization
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open DbUp
open DbUp.Engine
open DbUp.Engine.Transactions
open Microsoft.Data.Sqlite

type MigrationScript = { Id: string; Bytes: byte array }

/// Embedded logical names are migration.<fixed-context>.<number>_<description>.sql.
/// Scripts is the explicit release-catalogue seam for tooling and disposable failure fixtures.
type MigrationSource =
    | Embedded of Assembly
    | Scripts of MigrationScript seq

type MigrationError =
    | InvalidCatalogue
    | LegacyDatabase
    | InvalidHistory
    | DatabaseBusy
    | MigrationFailed of scriptId: string option
    | TransactionOutcomeUncertain

type MigrationReport = { Applied: string list }

exception private MigrationRejected of MigrationError

type private PreparedScript = { Script: SqlScript; Hash: string }

module private MigrationJournal =
    let execute (factory: Func<IDbCommand>) sql =
        use command = factory.Invoke()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

    let scalar (factory: Func<IDbCommand>) sql =
        use command = factory.Invoke()
        command.CommandText <- sql
        command.ExecuteScalar()

    let parameter (command: IDbCommand) name value =
        let parameter = command.CreateParameter()
        parameter.ParameterName <- name
        parameter.Value <- value
        command.Parameters.Add parameter |> ignore

    let guard (failure: exn option ref) action =
        try
            action ()
        with ex ->
            failure.Value <- Some ex
            reraise ()

    type Journal(manager: Func<IConnectionManager>, catalogue: PreparedScript array, failure) =
        interface IJournal with
            member _.GetExecutedScripts() =
                manager
                    .Invoke()
                    .ExecuteCommandsWithManagedConnection(fun factory ->
                        guard failure (fun () ->
                            // DbUp begins Microsoft's non-deferred write transaction before this callback.
                            // Never read history using a separate connection or before acquiring that lock.
                            // TEMP objects must never shadow the persistent journal or its schema.
                            let version = scalar factory "PRAGMA main.user_version;" :?> int64

                            let journalExists =
                                scalar
                                    factory
                                    "SELECT COUNT(*) FROM main.sqlite_schema WHERE name = '__migration_history' AND type = 'table';"
                                :?> int64 = 1L

                            let hasSchema =
                                scalar
                                    factory
                                    "SELECT COUNT(*) FROM main.sqlite_schema WHERE name NOT GLOB 'sqlite_*' AND name <> '__migration_history';"
                                :?> int64 > 0L

                            if version <> 0L || (not journalExists && hasSchema) then
                                raise (MigrationRejected LegacyDatabase)

                            if not journalExists then
                                execute
                                    factory
                                    "CREATE TABLE main.__migration_history (sequence INTEGER PRIMARY KEY NOT NULL, id TEXT NOT NULL UNIQUE, sha256 TEXT NOT NULL, applied_at TEXT NOT NULL) STRICT;"

                            if
                                hasSchema
                                && (scalar factory "SELECT COUNT(*) FROM main.__migration_history;" :?> int64 = 0L)
                            then
                                raise (MigrationRejected LegacyDatabase)

                            try
                                use schema = factory.Invoke()
                                schema.CommandText <- "PRAGMA main.table_info('__migration_history');"
                                let columns = ResizeArray<_>()

                                do
                                    use reader = schema.ExecuteReader()

                                    while reader.Read() do
                                        columns.Add(
                                            reader.GetString(1),
                                            reader.GetString(2),
                                            reader.GetInt32(3),
                                            reader.GetInt32(5)
                                        )

                                if
                                    List.ofSeq columns
                                    <> [ ("sequence", "INTEGER", 1, 1)
                                         ("id", "TEXT", 1, 0)
                                         ("sha256", "TEXT", 1, 0)
                                         ("applied_at", "TEXT", 1, 0) ]
                                then
                                    raise (MigrationRejected InvalidHistory)

                                use command = factory.Invoke()

                                command.CommandText <-
                                    "SELECT sequence, id, sha256, applied_at FROM main.__migration_history ORDER BY sequence;"

                                use reader = command.ExecuteReader()
                                let ids = ResizeArray<string>()

                                while reader.Read() do
                                    let index = ids.Count

                                    if index >= catalogue.Length || reader.GetValue(0) <> box (int64 (index + 1)) then
                                        raise (MigrationRejected InvalidHistory)

                                    let expected = catalogue[index]

                                    if
                                        reader.GetString(1) <> expected.Script.Name
                                        || reader.GetString(2) <> expected.Hash
                                    then
                                        raise (MigrationRejected InvalidHistory)

                                    match
                                        DateTimeOffset.TryParseExact(
                                            reader.GetString(3),
                                            "O",
                                            CultureInfo.InvariantCulture,
                                            DateTimeStyles.None
                                        )
                                    with
                                    | true, at when at.Offset = TimeSpan.Zero -> ()
                                    | _ -> raise (MigrationRejected InvalidHistory)

                                    ids.Add expected.Script.Name

                                ids.ToArray()
                            with _ ->
                                raise (MigrationRejected InvalidHistory)))

            member _.EnsureTableExistsAndIsLatestVersion _ = ()

            member _.StoreExecutedScript(script, factory) =
                let index =
                    catalogue |> Array.findIndex (fun item -> item.Script.Name = script.Name)

                use command = factory.Invoke()

                command.CommandText <-
                    "INSERT INTO main.__migration_history(sequence, id, sha256, applied_at) VALUES ($sequence, $id, $hash, $at);"

                parameter command "$sequence" (box (index + 1))
                parameter command "$id" script.Name
                parameter command "$hash" catalogue[index].Hash
                parameter command "$at" (DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                command.ExecuteNonQuery() |> ignore

// Supported provider overrides, not a migration engine. Microsoft.Data.Sqlite accepts
// the complete SQL text. DbUp's regex splitter would even split inside string literals.
type private ExactSqliteConnectionManager(shared) =
    inherit DbUp.Sqlite.SqliteConnectionManager(shared: DbUp.Sqlite.Helpers.SharedConnection)
    override _.SplitScriptIntoCommands contents = [| contents |]

type private ExactSqliteExecutor(configuration: DbUp.Builder.UpgradeConfiguration) =
    inherit
        DbUp.Sqlite.SqliteScriptExecutor(
            (fun () -> configuration.ConnectionManager),
            (fun () -> configuration.Log),
            null,
            (fun () -> false),
            Seq.empty,
            (fun () -> configuration.Journal)
        )
    // Also removes the implicit StripSchemaPreprocessor; disabling variables alone does NOT.
    override _.PreprocessScriptContents(script, _) = script.Contents

[<RequireQualifiedAccess>]
module SqliteMigrations =
    let private prepare source =
        try
            let scripts =
                match source with
                | Scripts scripts -> scripts |> Seq.toArray
                | Embedded assembly ->
                    assembly.GetManifestResourceNames()
                    |> Array.filter (fun name -> name.StartsWith("migration.", StringComparison.Ordinal))
                    |> Array.map (fun name ->
                        let parts = name.Split('.')

                        if parts.Length <> 4 then
                            raise (MigrationRejected InvalidCatalogue)

                        use stream = assembly.GetManifestResourceStream name

                        if isNull stream then
                            raise (MigrationRejected InvalidCatalogue)

                        use bytes = new MemoryStream()
                        stream.CopyTo bytes

                        { Id = $"{parts[1]}/{parts[2]}.{parts[3]}"
                          Bytes = bytes.ToArray() })

            if scripts.Length = 0 then
                raise (MigrationRejected InvalidCatalogue)

            let identities = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            let contexts = Collections.Generic.HashSet<string>(StringComparer.Ordinal)

            scripts
            |> Array.map (fun script ->
                if
                    isNull script.Id
                    || not (Regex.IsMatch(script.Id, "\\A[a-z][a-z0-9]*/[0-9]{3}_[a-z][a-z0-9_]*\\.sql\\z"))
                then
                    raise (MigrationRejected InvalidCatalogue)

                let parts = script.Id.Split('/')
                contexts.Add parts[0] |> ignore

                if
                    not (identities.Add(parts[1].Substring(0, 3)))
                    || contexts.Count <> 1
                    || parts[1].StartsWith("000_")
                then
                    raise (MigrationRejected InvalidCatalogue)

                let bytes = Array.copy script.Bytes
                let text = UTF8Encoding(false, true).GetString bytes

                if
                    String.IsNullOrWhiteSpace text
                    || text.Contains('\r')
                    || text.Contains('\u0000')
                    || text.Contains('\uFEFF')
                then
                    raise (MigrationRejected InvalidCatalogue)

                { Script = SqlScript(script.Id, text)
                  Hash = SHA256.HashData bytes |> Convert.ToHexStringLower })
            |> Array.sortWith (fun left right -> StringComparer.Ordinal.Compare(left.Script.Name, right.Script.Name))
        with _ ->
            raise (MigrationRejected InvalidCatalogue)

    /// Applies a complete immutable catalogue, including history validation on a no-op run.
    /// Owns a bounded two-second SQLite write-lock wait. No SQL/data/connection details are returned on failure.
    let run connectionString source : Result<MigrationReport, MigrationError> =
        try
            let catalogue = prepare source
            let settings = SqliteConnectionStringBuilder(connectionString)
            settings.Pooling <- false
            settings.ForeignKeys <- Nullable true
            settings.DefaultTimeout <- 2
            use connection = new SqliteConnection(settings.ConnectionString)
            use shared = new DbUp.Sqlite.Helpers.SharedConnection(connection)
            let managedFailure = ref None

            let builder =
                DeployChanges.To
                    .SqliteDatabase(shared)
                    .WithTransaction()
                    .WithScripts(catalogue |> Array.map _.Script)
                    .WithVariablesDisabled()
                    .JournalTo(fun manager _ ->
                        MigrationJournal.Journal(manager, catalogue, managedFailure) :> IJournal)

            builder.Configure(fun configuration ->
                configuration.ConnectionManager <- ExactSqliteConnectionManager(shared)
                configuration.ConnectionManager.TransactionMode <- TransactionMode.SingleTransaction
                configuration.ScriptPreprocessors.Clear()
                let executor = ExactSqliteExecutor(configuration)
                // Catch all executor failures inside DbUp's transaction strategy, including
                // any future preprocessing/splitting failure. See DbUpTransactionProof.fs.
                let execute script variables =
                    configuration.ConnectionManager.ExecuteCommandsWithManagedConnection(fun _ ->
                        MigrationJournal.guard managedFailure (fun () ->
                            // Use SQLite's parser, not a home-grown SQL parser, to prohibit commands
                            // that could commit the batch or affect databases outside its transaction.
                            let authorize =
                                SQLitePCL.strdelegate_authorizer (fun _ operation _ _ _ _ ->
                                    if
                                        operation = SQLitePCL.raw.SQLITE_TRANSACTION
                                        || operation = SQLitePCL.raw.SQLITE_SAVEPOINT
                                        || operation = SQLitePCL.raw.SQLITE_ATTACH
                                        || operation = SQLitePCL.raw.SQLITE_DETACH
                                        || operation = SQLitePCL.raw.SQLITE_PRAGMA
                                    then
                                        SQLitePCL.raw.SQLITE_DENY
                                    else
                                        SQLitePCL.raw.SQLITE_OK)

                            if
                                SQLitePCL.raw.sqlite3_set_authorizer (connection.Handle, authorize, null)
                                <> SQLitePCL.raw.SQLITE_OK
                            then
                                raise (InvalidOperationException("Could not guard migration SQL."))

                            try
                                executor.Execute(script, variables)
                            finally
                                SQLitePCL.raw.sqlite3_set_authorizer (
                                    connection.Handle,
                                    Unchecked.defaultof<SQLitePCL.strdelegate_authorizer>,
                                    null
                                )
                                |> ignore
                            // Handwritten SQL can alter the journal. Revalidate the COMPLETE
                            // expected catalogue after the last journal write, before DbUp commits.
                            if script.Name = catalogue[catalogue.Length - 1].Script.Name then
                                let recorded = configuration.Journal.GetExecutedScripts()

                                if recorded.Length <> catalogue.Length then
                                    raise (MigrationRejected InvalidHistory)))

                configuration.ScriptExecutor <-
                    { new IScriptExecutor with
                        member _.ExecutionTimeoutSeconds
                            with get () = executor.ExecutionTimeoutSeconds
                            and set value = executor.ExecutionTimeoutSeconds <- value

                        member _.Execute script = execute script null
                        member _.Execute(script, variables) = execute script variables
                        member _.VerifySchema() = executor.VerifySchema() })

            let result = builder.Build().PerformUpgrade()

            if result.Successful then
                Ok { Applied = result.Scripts |> Seq.map _.Name |> Seq.toList }
            else
                match managedFailure.Value, result.Error with
                | Some captured, error when Object.ReferenceEquals(captured, error) ->
                    match error with
                    | MigrationRejected rejection -> Error rejection
                    | _ -> Error(MigrationFailed(Option.ofObj result.ErrorScript |> Option.map _.Name))
                | None, (:? SqliteException as error) when
                    (error.SqliteErrorCode = 5 || error.SqliteErrorCode = 6)
                    && isNull result.ErrorScript
                    ->
                    Error DatabaseBusy
                | _ -> Error TransactionOutcomeUncertain
        with
        | MigrationRejected error -> Error error
        | _ -> Error TransactionOutcomeUncertain
