namespace StarterKit.WorkTracking.Infrastructure

open System
open System.IO
open System.Globalization
open System.Text.RegularExpressions
open System.Security.Cryptography
open Microsoft.Data.Sqlite

type DatabaseHistoryEntry =
    { Id: string
      Sha256: string
      AppliedAt: string }

type DatabaseValidation =
    { Integrity: string
      ForeignKeys: string
      SizeBytes: int64
      Sha256: string
      MigrationHistory: DatabaseHistoryEntry array }

type DatabaseOperationError =
    | MissingDatabase
    | InvalidDatabase
    | DestinationExists
    | PrivatePermissionsUnavailable
    | DatabaseOperationFailed

exception private DatabaseOperationRejected of DatabaseOperationError

/// Non-serving operations. Neither function loads a migration catalogue or initializes a schema.
[<RequireQualifiedAccess>]
module SqliteDatabaseOperations =
    let private openReadOnly path =
        if not (File.Exists path) then
            raise (DatabaseOperationRejected MissingDatabase)
        // SQLite treats a zero-byte file as a new database, even in read-only mode.
        // These commands validate existing database files, never initialization placeholders.
        do
            use file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            let header = Array.zeroCreate<byte> 16

            if
                file.Length < 100L
                || file.Read(header, 0, header.Length) <> header.Length
                || System.Text.Encoding.ASCII.GetString(header) <> "SQLite format 3\u0000"
            then
                raise (DatabaseOperationRejected InvalidDatabase)

        let settings =
            SqliteConnectionStringBuilder(
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 2
            )

        let connection = new SqliteConnection(settings.ConnectionString)

        try
            connection.Open()
            connection
        with _ ->
            connection.Dispose()
            raise (DatabaseOperationRejected InvalidDatabase)

    let private check (connection: SqliteConnection) (transaction: SqliteTransaction) =
        use command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandText <- "PRAGMA integrity_check;"

        do
            use reader = command.ExecuteReader()

            if not (reader.Read()) || reader.GetString(0) <> "ok" || reader.Read() then
                raise (DatabaseOperationRejected InvalidDatabase)

        command.CommandText <- "PRAGMA foreign_key_check;"

        do
            use reader = command.ExecuteReader()

            if reader.Read() then
                raise (DatabaseOperationRejected InvalidDatabase)

        command.CommandText <-
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = '__migration_history';"

        if command.ExecuteScalar() :?> int64 = 0L then
            [||]
        else
            command.CommandText <- "SELECT sequence, id, sha256, applied_at FROM __migration_history ORDER BY sequence;"
            use reader = command.ExecuteReader()
            let history = ResizeArray<DatabaseHistoryEntry>()

            while reader.Read() do
                let entry =
                    { Id = reader.GetString(1)
                      Sha256 = reader.GetString(2)
                      AppliedAt = reader.GetString(3) }
                // Only bounded migration metadata may reach stdout. A malformed journal
                // must not turn application data or arbitrary stored text into a log line.
                if
                    reader.GetValue(0) <> box (int64 (history.Count + 1))
                    || not (Regex.IsMatch(entry.Id, "\\A[a-z][a-z0-9]*/[0-9]{3}_[a-z][a-z0-9_]*\\.sql\\z"))
                    || not (Regex.IsMatch(entry.Sha256, "\\A[0-9a-f]{64}\\z"))
                then
                    raise (DatabaseOperationRejected InvalidDatabase)

                match
                    DateTimeOffset.TryParseExact(
                        entry.AppliedAt,
                        "O",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None
                    )
                with
                | true, at when at.Offset = TimeSpan.Zero -> ()
                | _ -> raise (DatabaseOperationRejected InvalidDatabase)

                if history.Count > 0 then
                    let previous = history[history.Count - 1].Id

                    if
                        StringComparer.Ordinal.Compare(previous, entry.Id) >= 0
                        || previous.Split('/')[0] <> entry.Id.Split('/')[0]
                    then
                        raise (DatabaseOperationRejected InvalidDatabase)

                history.Add entry

            history.ToArray()

    let private report path history =
        use file = File.OpenRead path

        { Integrity = "ok"
          ForeignKeys = "ok"
          SizeBytes = file.Length
          Sha256 = SHA256.HashData file |> Convert.ToHexStringLower
          MigrationHistory = history }

    let validate path =
        try
            use connection = openReadOnly path
            use transaction = connection.BeginTransaction(deferred = true)
            let history = check connection transaction
            Ok(report path history)
        with
        | DatabaseOperationRejected error -> Error error
        | :? SqliteException -> Error InvalidDatabase
        | _ -> Error DatabaseOperationFailed

    let snapshot source destination =
        let mutable created = false

        try
            if File.Exists destination || Directory.Exists destination then
                raise (DatabaseOperationRejected DestinationExists)

            use connection = openReadOnly source
            use transaction = connection.BeginTransaction(deferred = true)
            check connection transaction |> ignore

            if OperatingSystem.IsWindows() then
                raise (DatabaseOperationRejected PrivatePermissionsUnavailable)

            let options =
                FileStreamOptions(
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                )

            do
                use reserved = new FileStream(destination, options)
                created <- true

            do
                let settings =
                    SqliteConnectionStringBuilder(
                        DataSource = destination,
                        Mode = SqliteOpenMode.ReadWrite,
                        Pooling = false,
                        DefaultTimeout = 2
                    )

                use target = new SqliteConnection(settings.ConnectionString)
                target.Open()
                connection.BackupDatabase target
                // BackupDatabase preserves the source's WAL header. Normalize ONLY the
                // isolated destination so read-only restore checks need no sidecars and
                // its main-file hash describes the complete recovery snapshot.
                use standalone = target.CreateCommand()
                standalone.CommandText <- "PRAGMA journal_mode=DELETE;"

                if standalone.ExecuteScalar() :?> string <> "delete" then
                    raise (DatabaseOperationRejected InvalidDatabase)

            match validate destination with
            | Ok report -> Ok report
            | Error error -> raise (DatabaseOperationRejected error)
        with ex ->
            if created then
                for suffix in [ ""; "-journal"; "-wal"; "-shm" ] do
                    try
                        File.Delete(destination + suffix)
                    with _ ->
                        ()

            match ex with
            | DatabaseOperationRejected error -> Error error
            | :? SqliteException -> Error InvalidDatabase
            | _ -> Error DatabaseOperationFailed
