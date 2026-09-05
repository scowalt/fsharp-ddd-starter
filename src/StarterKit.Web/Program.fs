namespace StarterKit.Web

open System
open System.IO
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Data.Sqlite
open Oxpecker
open StarterKit.WorkTracking.Application
open StarterKit.WorkTracking.Infrastructure

type StartupException(message: string) =
    inherit Exception(message)

module Program =
    let buildApp
        (args: string array)
        (configureWebHost: IWebHostBuilder -> unit)
        (databasePath: string option)
        : WebApplication =
        let builder = WebApplication.CreateBuilder(args)
        configureWebHost builder.WebHost
        builder.Services.AddRouting().AddOxpecker() |> ignore

        let development = builder.Environment.IsDevelopment()

        let configured =
            databasePath
            |> Option.orElse (Option.ofObj builder.Configuration["DATABASE_PATH"])

        let dataFile =
            match configured with
            | Some path when not (String.IsNullOrWhiteSpace path) -> path
            | _ when development -> Path.Combine(builder.Environment.ContentRootPath, "data", "worktracking.db")
            | _ -> raise (StartupException("DATABASE_PATH must name an absolute database path outside Development."))

        if not development && not (Path.IsPathFullyQualified dataFile) then
            raise (StartupException("DATABASE_PATH must be absolute outside Development."))

        let exists = File.Exists dataFile

        let allowCreate =
            String.Equals(builder.Configuration["ALLOW_DATABASE_CREATE"], "true", StringComparison.OrdinalIgnoreCase)

        if not development then
            if not exists && not allowCreate then
                raise (
                    StartupException(
                        "Production database is missing. Only a first deployment may set ALLOW_DATABASE_CREATE=true."
                    )
                )

            if exists then
                match SqliteDatabaseOperations.validate dataFile with
                | Ok report when report.MigrationHistory.Length > 0 -> ()
                | _ ->
                    raise (
                        StartupException(
                            "Existing production database is invalid or has no migration history. Empty replacements and legacy adoption are refused."
                        )
                    )

        let dataDirectory = Path.GetDirectoryName dataFile

        if not (String.IsNullOrWhiteSpace dataDirectory) then
            Directory.CreateDirectory dataDirectory |> ignore

        let settings =
            SqliteConnectionStringBuilder(DataSource = dataFile, ForeignKeys = Nullable true)
        // Ordinary production opens cannot recreate a file removed after preflight.
        settings.Mode <-
            if development || (not exists && allowCreate) then
                SqliteOpenMode.ReadWriteCreate
            else
                SqliteOpenMode.ReadWrite

        let connectionString = settings.ConnectionString

        match SqliteMigrations.run connectionString (Embedded typeof<MigrationScript>.Assembly) with
        | Error error ->
            raise (
                StartupException(
                    $"Database migration failed: {error}. Startup refused; no automatic restore is attempted."
                )
            )
        | Ok _ -> ()

        let app = builder.Build()
        let store = SqliteWorkItemStore.create connectionString

        let runtime =
            { NewWorkItemId = Guid.NewGuid
              Now = fun () -> DateTimeOffset.UtcNow }

        let dependencies = { Runtime = runtime; Store = store }

        app.UseStaticFiles().UseRouting().UseOxpecker(Routes.endpoints dependencies)
        |> ignore

        app

    let private operationResult operation result =
        match result with
        | Ok report ->
            let output =
                {| Operation = operation
                   Integrity = report.Integrity
                   ForeignKeys = report.ForeignKeys
                   SizeBytes = report.SizeBytes
                   Sha256 = report.Sha256
                   MigrationHistory = report.MigrationHistory |}

            printfn
                "%s"
                (JsonSerializer.Serialize(
                    output,
                    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
                ))

            0
        | Error error ->
            eprintfn "Database operation failed: %A. No database contents or paths are logged." error
            1

    [<EntryPoint>]
    let main args =
        // Dispatch before CreateBuilder: operations cannot bind listeners or run migrations.
        match args with
        | [| "--snapshot-db"; source; destination |] ->
            SqliteDatabaseOperations.snapshot source destination
            |> operationResult "snapshot"
        | [| "--validate-db"; path |] -> SqliteDatabaseOperations.validate path |> operationResult "validate"
        | args when
            args
            |> Array.exists (fun arg ->
                arg.StartsWith("--snapshot-db", StringComparison.Ordinal)
                || arg.StartsWith("--validate-db", StringComparison.Ordinal))
            ->
            eprintfn "Usage: --snapshot-db SOURCE DESTINATION | --validate-db PATH"
            64
        | _ ->
            try
                use app = buildApp args ignore None
                app.Run()
                0
            with
            | :? StartupException as error ->
                eprintfn "%s" error.Message
                1
            | _ ->
                eprintfn "Application startup failed. Database paths, SQL and contents are not logged."
                1
