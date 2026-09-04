namespace StarterKit.WorkTracking.Application.Tests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Xunit
open StarterKit.WorkTracking.Application
open StarterKit.WorkTracking.Domain
open StarterKit.WorkTracking.Testing

module private TestResult =
    let get =
        function
        | Ok value -> value
        | Error error -> failwith $"Unexpected error: {error}"

module private InMemoryStore =
    type State =
        { Items: Dictionary<WorkItemId, WorkItem>
          mutable SaveCount: int }

    let create () =
        let state =
            { Items = Dictionary<WorkItemId, WorkItem>()
              SaveCount = 0 }

        let store =
            { Load =
                fun id _ ->
                    Task.FromResult(
                        Ok(
                            state.Items.TryGetValue id
                            |> function
                                | true, item -> Some item
                                | _ -> None
                        )
                    )
              List = fun _ -> Task.FromResult(Ok(state.Items.Values |> Seq.toList))
              Save =
                fun item _ ->
                    state.SaveCount <- state.SaveCount + 1
                    state.Items[WorkItem.id item] <- item
                    Task.FromResult(Ok()) }

        state, store

type UseCaseTests() =
    [<Fact>]
    member _.``application contract passes with an in-memory port``() =
        task {
            let _, store = InMemoryStore.create ()
            do! UseCaseContract.run store
        }

    [<Fact>]
    member _.``an invalid transition does not save``() =
        task {
            let state, store = InMemoryStore.create ()
            let id = Guid.Parse "0960c111-b861-4785-bac6-e5ef5477bcca"
            let workItemId = WorkItemId.create id |> TestResult.get
            let title = WorkItemTitle.create "Already done" |> TestResult.get
            let createdAt = DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero)
            let completedAt = createdAt.AddMinutes 1

            let completed =
                WorkItem.create workItemId title createdAt
                |> WorkItem.complete completedAt
                |> TestResult.get

            state.Items[workItemId] <- completed
            let savesBefore = state.SaveCount

            let runtime =
                { NewWorkItemId = Guid.NewGuid
                  Now = fun () -> completedAt.AddMinutes 1 }

            let! result =
                CompleteWorkItem.execute runtime store { WorkItemId = id } System.Threading.CancellationToken.None

            Assert.Equal(Error(InvalidWorkItem WorkItemAlreadyCompleted), result)
            Assert.Equal(savesBefore, state.SaveCount)
        }
