namespace StarterKit.WorkTracking.Domain.Tests

open System
open global.FsCheck
open global.Xunit
open StarterKit.WorkTracking.Domain

module private Fixture =
    let get =
        function
        | Ok value -> value
        | Error error -> failwith $"Unexpected error: {error}"

    let id =
        WorkItemId.create (Guid.Parse "cc979894-b1ea-4a67-afc6-d82c0bdebd30") |> get

    let title = WorkItemTitle.create "Write the domain scenario" |> get
    let createdAt = DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero)
    let openItem () = WorkItem.create id title createdAt

type WorkItemExamples() =
    [<Theory>]
    [<InlineData("")>]
    [<InlineData("   ")>]
    member _.``blank titles are rejected``(value: string) =
        Assert.Equal(Error WorkItemTitleMustNotBeEmpty, WorkItemTitle.create value)

    [<Fact>]
    member _.``a title is normalized at construction``() =
        let result = WorkItemTitle.create "  Name the invariant  "
        Assert.Equal("Name the invariant", result |> Result.map WorkItemTitle.value |> Fixture.get)

    [<Fact>]
    member _.``an empty identifier is rejected``() =
        Assert.Equal(Error WorkItemIdMustNotBeEmpty, WorkItemId.create Guid.Empty)

    [<Fact>]
    member _.``an open work item can be completed``() =
        let completedAt = Fixture.createdAt.AddMinutes 5
        let result = Fixture.openItem () |> WorkItem.complete completedAt
        Assert.Equal(Ok(Completed completedAt), result |> Result.map WorkItem.state)

    [<Fact>]
    member _.``a completed work item cannot be completed again``() =
        let firstCompletion = Fixture.createdAt.AddMinutes 5
        let secondCompletion = firstCompletion.AddMinutes 1

        let result =
            Fixture.openItem ()
            |> WorkItem.complete firstCompletion
            |> Result.bind (WorkItem.complete secondCompletion)

        Assert.Equal(Error WorkItemAlreadyCompleted, result)

    [<Fact>]
    member _.``a work item cannot be completed before it was created``() =
        let result =
            Fixture.openItem () |> WorkItem.complete (Fixture.createdAt.AddTicks -1L)

        Assert.Equal(Error CompletionBeforeCreation, result)

type WorkItemProperties() =
    [<global.FsCheck.Xunit.Property(Replay = "12345,67891")>]
    member _.``every title longer than the maximum is rejected``(NonNegativeInt extraLength) =
        let title = String('x', WorkItemTitle.MaximumLength + 1 + extraLength % 500)
        WorkItemTitle.create title = Error(WorkItemTitleTooLong WorkItemTitle.MaximumLength)

    [<global.FsCheck.Xunit.Property(Replay = "54321,9877")>]
    member _.``completion preserves identity and title``(PositiveInt elapsedSeconds) =
        let workItem = Fixture.openItem ()
        let completedAt = Fixture.createdAt.AddSeconds(float (elapsedSeconds % 100000))

        match WorkItem.complete completedAt workItem with
        | Error _ -> false
        | Ok completed ->
            WorkItem.id completed = WorkItem.id workItem
            && WorkItem.title completed = WorkItem.title workItem
            && WorkItem.state completed = Completed completedAt
