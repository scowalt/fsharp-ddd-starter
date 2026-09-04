namespace StarterKit.Web

open System
open System.Net.Mime
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.ViewEngine
open StarFederation.Datastar.FSharp
open StarterKit.WorkTracking.Application
open StarterKit.WorkTracking.Domain

type RouteDependencies =
    { Runtime: Runtime
      Store: WorkItemStore }

[<CLIMutable>]
type CreateWorkItemSignals = { title: string }

[<RequireQualifiedAccess>]
module Routes =
    let private isDatastarRequest (ctx: HttpContext) =
        ctx.Request.Headers.Accept
            .ToString()
            .Contains(MediaTypeNames.Text.EventStream, StringComparison.OrdinalIgnoreCase)

    let private errorMessage error =
        match error with
        | InvalidWorkItem WorkItemIdMustNotBeEmpty -> "The work item identifier is invalid."
        | InvalidWorkItem WorkItemTitleMustNotBeEmpty -> "Enter a title."
        | InvalidWorkItem(WorkItemTitleTooLong maximum) -> $"The title must be {maximum} characters or fewer."
        | InvalidWorkItem CompletionBeforeCreation -> "A work item cannot be completed before it is created."
        | InvalidWorkItem WorkItemAlreadyCompleted -> "That work item is already complete."
        | WorkItemNotFound -> "That work item does not exist."
        | PersistenceFailure _ -> "The work item store is unavailable."

    let private loadViewModels dependencies cancellationToken =
        task {
            let! result = ListWorkItems.execute dependencies.Store cancellationToken
            return result |> Result.map (List.map Views.toViewModel)
        }

    let private patchRegion (ctx: HttpContext) items (error: string option) =
        task {
            let html = Views.workItemsRegion items error |> Render.toString
            do! ServerSentEventGenerator.StartServerEventStreamAsync(ctx.Response)
            do! ServerSentEventGenerator.PatchElementsAsync(ctx.Response, html)
        }

    let private writePage (ctx: HttpContext) statusCode items (error: string option) =
        ctx.Response.StatusCode <- statusCode
        ctx.WriteHtmlView(Views.page items error)

    let private redirectHome (ctx: HttpContext) =
        ctx.Response.StatusCode <- StatusCodes.Status303SeeOther
        ctx.Response.Headers.Location <- "/"
        Task.CompletedTask

    let private respond dependencies (ctx: HttpContext) statusCode (error: string option) =
        task {
            let! loaded = loadViewModels dependencies ctx.RequestAborted

            let items, finalError =
                match loaded with
                | Ok items -> items, error
                | Error loadError -> [], Some(errorMessage loadError)

            if isDatastarRequest ctx then
                return! patchRegion ctx items finalError
            else
                return! writePage ctx statusCode items finalError
        }

    let private home dependencies : EndpointHandler =
        fun ctx ->
            task {
                let! loaded = loadViewModels dependencies ctx.RequestAborted

                match loaded with
                | Ok items -> return! writePage ctx StatusCodes.Status200OK items None
                | Error error ->
                    return! writePage ctx StatusCodes.Status503ServiceUnavailable [] (Some(errorMessage error))
            }

    let private readTitle (ctx: HttpContext) =
        task {
            if isDatastarRequest ctx then
                let! signals = ServerSentEventGenerator.ReadSignalsAsync<CreateWorkItemSignals>(ctx.Request)
                return signals |> ValueOption.map _.title |> ValueOption.defaultValue String.Empty
            else
                let! form = ctx.Request.ReadFormAsync(ctx.RequestAborted)
                return form["Title"].ToString()
        }

    let private createWorkItem dependencies : EndpointHandler =
        fun ctx ->
            task {
                let! title = readTitle ctx

                let! result =
                    CreateWorkItem.execute dependencies.Runtime dependencies.Store { Title = title } ctx.RequestAborted

                match result with
                | Ok _ when isDatastarRequest ctx -> return! respond dependencies ctx StatusCodes.Status200OK None
                | Ok _ -> return! redirectHome ctx
                | Error error ->
                    return! respond dependencies ctx StatusCodes.Status400BadRequest (Some(errorMessage error))
            }

    let private completeWorkItem dependencies (rawId: string) : EndpointHandler =
        fun ctx ->
            task {
                let id =
                    match Guid.TryParse rawId with
                    | true, value -> value
                    | false, _ -> Guid.Empty

                let! result =
                    CompleteWorkItem.execute
                        dependencies.Runtime
                        dependencies.Store
                        { WorkItemId = id }
                        ctx.RequestAborted

                match result with
                | Ok _ when isDatastarRequest ctx -> return! respond dependencies ctx StatusCodes.Status200OK None
                | Ok _ -> return! redirectHome ctx
                | Error WorkItemNotFound ->
                    return! respond dependencies ctx StatusCodes.Status404NotFound (Some(errorMessage WorkItemNotFound))
                | Error error ->
                    return! respond dependencies ctx StatusCodes.Status409Conflict (Some(errorMessage error))
            }

    let endpoints dependencies =
        [ GET [ route "/" (home dependencies) ]
          POST
              [ route "/work-items" (createWorkItem dependencies)
                routef "/work-items/{%s}/complete" (completeWorkItem dependencies) ] ]
