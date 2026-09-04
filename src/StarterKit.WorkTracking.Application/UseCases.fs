namespace StarterKit.WorkTracking.Application

open System
open System.Threading
open System.Threading.Tasks
open StarterKit.WorkTracking.Domain

type ApplicationError =
    | InvalidWorkItem of WorkItemError
    | WorkItemNotFound
    | PersistenceFailure of StoreError

type CreateWorkItemCommand = { Title: string }
type CompleteWorkItemCommand = { WorkItemId: Guid }

[<RequireQualifiedAccess>]
module CreateWorkItem =
    let execute runtime store command cancellationToken : Task<Result<WorkItem, ApplicationError>> =
        task {
            match WorkItemId.create (runtime.NewWorkItemId()), WorkItemTitle.create command.Title with
            | Error error, _
            | _, Error error -> return Error(InvalidWorkItem error)
            | Ok workItemId, Ok title ->
                let workItem = WorkItem.create workItemId title (runtime.Now())
                let! saved = store.Save workItem cancellationToken

                return
                    match saved with
                    | Ok() -> Ok workItem
                    | Error error -> Error(PersistenceFailure error)
        }

[<RequireQualifiedAccess>]
module CompleteWorkItem =
    let execute runtime store command cancellationToken : Task<Result<WorkItem, ApplicationError>> =
        task {
            match WorkItemId.create command.WorkItemId with
            | Error error -> return Error(InvalidWorkItem error)
            | Ok workItemId ->
                let! loaded = store.Load workItemId cancellationToken

                match loaded with
                | Error error -> return Error(PersistenceFailure error)
                | Ok None -> return Error WorkItemNotFound
                | Ok(Some workItem) ->
                    match WorkItem.complete (runtime.Now()) workItem with
                    | Error error -> return Error(InvalidWorkItem error)
                    | Ok completed ->
                        let! saved = store.Save completed cancellationToken

                        return
                            match saved with
                            | Ok() -> Ok completed
                            | Error error -> Error(PersistenceFailure error)
        }

[<RequireQualifiedAccess>]
module ListWorkItems =
    let execute store cancellationToken : Task<Result<WorkItem list, ApplicationError>> =
        task {
            let! workItems = store.List cancellationToken
            return workItems |> Result.mapError PersistenceFailure
        }
