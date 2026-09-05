namespace StarterKit.WorkTracking.Infrastructure.Tests

open System
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open Xunit
open StarterKit.WorkTracking.Application
open StarterKit.WorkTracking.Infrastructure
open StarterKit.WorkTracking.Testing

type private TemporaryDatabase() =
    let path = Path.Combine(Path.GetTempPath(), $"starterkit-{Guid.NewGuid():N}.db")

    member _.ConnectionString = $"Data Source={path};Foreign Keys=True"

    interface IDisposable with
        member _.Dispose() =
            for suffix in [ ""; "-shm"; "-wal" ] do
                let file = path + suffix

                if File.Exists file then
                    File.Delete file

type SqliteWorkItemStoreTests() =
    [<Fact>]
    member _.``application contract passes with the real SQLite adapter``() =
        task {
            use database = new TemporaryDatabase()

            let initialized =
                SqliteMigrations.run database.ConnectionString (Embedded typeof<MigrationScript>.Assembly)
                |> Result.map ignore

            Assert.Equal(Ok(), initialized)
            let store = SqliteWorkItemStore.create database.ConnectionString
            do! UseCaseContract.run store
        }

    [<Fact>]
    member _.``embedded migration replaces user version with stable checksum history``() =
        task {
            use database = new TemporaryDatabase()

            let initialized =
                SqliteMigrations.run database.ConnectionString (Embedded typeof<MigrationScript>.Assembly)
                |> Result.map ignore

            Assert.Equal(Ok(), initialized)

            use connection = new SqliteConnection(database.ConnectionString)
            do! connection.OpenAsync()
            use command = connection.CreateCommand()
            command.CommandText <- "PRAGMA user_version;"
            let! version = command.ExecuteScalarAsync()
            Assert.Equal(0L, Convert.ToInt64 version)
            command.CommandText <- "SELECT id FROM __migration_history;"
            Assert.Equal("worktracking/001_initial.sql", command.ExecuteScalar() :?> string)
        }

    [<Fact>]
    member _.``invalid persisted timestamps are reported as corrupt data``() =
        task {
            use database = new TemporaryDatabase()

            let initialized =
                SqliteMigrations.run database.ConnectionString (Embedded typeof<MigrationScript>.Assembly)
                |> Result.map ignore

            Assert.Equal(Ok(), initialized)

            use connection = new SqliteConnection(database.ConnectionString)
            do! connection.OpenAsync()
            use command = connection.CreateCommand()

            command.CommandText <-
                """
                INSERT INTO work_items (id, title, created_at, state, completed_at)
                VALUES ('5c9c036c-4521-49bd-9053-89a3c8a51b85', 'Corrupt row', 'not-a-date', 'open', NULL);
                """

            let! _ = command.ExecuteNonQueryAsync()

            let store = SqliteWorkItemStore.create database.ConnectionString
            let! result = store.List CancellationToken.None

            match result with
            | Error(CorruptStoredWorkItem message) -> Assert.Contains("created_at", message)
            | other -> Assert.Fail($"Expected a corrupt-data error but got {other}")
        }
