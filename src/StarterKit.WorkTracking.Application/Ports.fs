namespace StarterKit.WorkTracking.Application

open System
open System.Threading
open System.Threading.Tasks
open StarterKit.WorkTracking.Domain

type StoreError =
    | StoreUnavailable of message: string
    | CorruptStoredWorkItem of message: string

type WorkItemStore =
    { Load: WorkItemId -> CancellationToken -> Task<Result<WorkItem option, StoreError>>
      List: CancellationToken -> Task<Result<WorkItem list, StoreError>>
      Save: WorkItem -> CancellationToken -> Task<Result<unit, StoreError>> }

type Runtime =
    { NewWorkItemId: unit -> Guid
      Now: unit -> DateTimeOffset }
