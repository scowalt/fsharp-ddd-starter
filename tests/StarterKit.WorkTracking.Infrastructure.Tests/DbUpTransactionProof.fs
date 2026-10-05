namespace StarterKit.WorkTracking.Infrastructure.Tests

open System
open System.IO
open DbUp
open DbUp.Engine
open Microsoft.Data.Sqlite
open Xunit

type DbUpTransactionProof() =
    [<Fact>]
    member _.``vendor whole-run transaction commits earlier work when later preprocessing throws``() =
        let path = Path.Combine(Path.GetTempPath(), $"dbup-proof-{Guid.NewGuid():N}.db")

        try
            let builder =
                DeployChanges.To
                    .SqliteDatabase($"Data Source={path};Pooling=False")
                    .WithTransaction()
                    .WithScripts(
                        [| SqlScript(
                               "001",
                               "CREATE TABLE earlier(value TEXT); INSERT INTO earlier VALUES ('committed');"
                           )
                           SqlScript("002", "SELECT 2;") |]
                    )
                    .WithPreprocessor(
                        { new IScriptPreprocessor with
                            member _.Process contents =
                                if contents = "SELECT 2;" then
                                    failwith "preprocessing failure"

                                contents }
                    )

            let result = builder.Build().PerformUpgrade()
            Assert.False(result.Successful)
            use connection = new SqliteConnection($"Data Source={path};Pooling=False")
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT value FROM earlier;"
            Assert.Equal("committed", command.ExecuteScalar() :?> string)
            command.CommandText <- "SELECT COUNT(*) FROM SchemaVersions;"
            Assert.Equal(1L, command.ExecuteScalar() :?> int64)
        finally
            File.Delete path
