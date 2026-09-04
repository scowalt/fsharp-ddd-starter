namespace StarterKit.Web

open System
open System.IO
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Oxpecker
open StarterKit.WorkTracking.Application
open StarterKit.WorkTracking.Infrastructure

module Program =
    let buildApp
        (args: string array)
        (configureWebHost: IWebHostBuilder -> unit)
        (databasePath: string option)
        : WebApplication =
        let builder = WebApplication.CreateBuilder(args)
        configureWebHost builder.WebHost
        builder.Services.AddRouting().AddOxpecker() |> ignore

        let app = builder.Build()

        let dataFile =
            databasePath
            |> Option.defaultWith (fun () -> Path.Combine(app.Environment.ContentRootPath, "data", "worktracking.db"))

        let dataDirectory = Path.GetDirectoryName dataFile

        if not (String.IsNullOrWhiteSpace dataDirectory) then
            Directory.CreateDirectory dataDirectory |> ignore

        let connectionString = $"Data Source={dataFile};Foreign Keys=True"

        match
            SqliteWorkItemStore.initialize connectionString app.Lifetime.ApplicationStopping
            |> Async.AwaitTask
            |> Async.RunSynchronously
        with
        | Error error -> failwith $"Could not initialize the work item store: {error}"
        | Ok() -> ()

        let store = SqliteWorkItemStore.create connectionString

        let runtime =
            { NewWorkItemId = Guid.NewGuid
              Now = fun () -> DateTimeOffset.UtcNow }

        let dependencies = { Runtime = runtime; Store = store }

        app.UseStaticFiles().UseRouting().UseOxpecker(Routes.endpoints dependencies)
        |> ignore

        app

    [<EntryPoint>]
    let main args =
        let app = buildApp args ignore None
        app.Run()
        0
