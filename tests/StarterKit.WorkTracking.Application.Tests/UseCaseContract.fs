namespace StarterKit.WorkTracking.Testing

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open StarterKit.WorkTracking.Application
open StarterKit.WorkTracking.Domain

[<RequireQualifiedAccess>]
module UseCaseContract =
    let private get =
        function
        | Ok value -> value
        | Error error -> failwith $"Unexpected error: {error}"

    let run store =
        task {
            let expectedId = Guid.Parse "13a6607e-117a-47da-80a4-16b1703ec4d0"
            let createdAt = DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero)
            let completedAt = createdAt.AddMinutes 15
            let mutable now = createdAt

            let runtime =
                { NewWorkItemId = fun () -> expectedId
                  Now = fun () -> now }

            let! created =
                CreateWorkItem.execute runtime store { Title = "  Review the context  " } CancellationToken.None

            let createdItem = created |> get
            Assert.Equal(expectedId, createdItem |> WorkItem.id |> WorkItemId.value)
            Assert.Equal("Review the context", createdItem |> WorkItem.title |> WorkItemTitle.value)
            Assert.Equal(Open, createdItem |> WorkItem.state)

            now <- completedAt

            let! completed = CompleteWorkItem.execute runtime store { WorkItemId = expectedId } CancellationToken.None

            let completedItem = completed |> get
            Assert.Equal(Completed completedAt, completedItem |> WorkItem.state)

            let! listed = ListWorkItems.execute store CancellationToken.None
            Assert.Single<WorkItem>(listed |> get) |> ignore
        }
