namespace StarterKit.WorkTracking.Infrastructure.Tests

open StarterKit.WorkTracking.Infrastructure

// A test-only executable exercising the SAME public migration operation in a killable
// process. No test callbacks or interruption switches enter the production executable.
module MigrationProcess =
    [<EntryPoint>]
    let main args =
        match args with
        | [| "--interrupt-fixture"; path |] ->
            let scripts =
                [ MigrationFixture.first
                  MigrationFixture.script
                      "worktracking/002_pending.sql"
                      "CREATE TABLE pending_blob(value BLOB); INSERT INTO pending_blob VALUES (zeroblob(8388608));\n"
                  MigrationFixture.script
                      "worktracking/003_wait.sql"
                      "WITH RECURSIVE counter(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM counter WHERE n<1000000000) SELECT sum(n) FROM counter;\n" ]

            match SqliteMigrations.run $"Data Source={path};Pooling=False" (Scripts scripts) with
            | Ok _ -> 0
            | Error _ -> 1
        | _ -> 64
