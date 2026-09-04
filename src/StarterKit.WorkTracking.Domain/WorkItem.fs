namespace StarterKit.WorkTracking.Domain

open System

type WorkItemError =
    | WorkItemIdMustNotBeEmpty
    | WorkItemTitleMustNotBeEmpty
    | WorkItemTitleTooLong of maximumLength: int
    | CompletionBeforeCreation
    | WorkItemAlreadyCompleted

type WorkItemId = private WorkItemId of Guid

[<RequireQualifiedAccess>]
module WorkItemId =
    let create value =
        if value = Guid.Empty then
            Error WorkItemIdMustNotBeEmpty
        else
            Ok(WorkItemId value)

    let value (WorkItemId value) = value

type WorkItemTitle = private WorkItemTitle of string

[<RequireQualifiedAccess>]
module WorkItemTitle =
    [<Literal>]
    let MaximumLength = 100

    let create (value: string) =
        let normalized = if isNull value then String.Empty else value.Trim()

        if String.IsNullOrWhiteSpace normalized then
            Error WorkItemTitleMustNotBeEmpty
        elif normalized.Length > MaximumLength then
            Error(WorkItemTitleTooLong MaximumLength)
        else
            Ok(WorkItemTitle normalized)

    let value (WorkItemTitle value) = value

type WorkItemState =
    | Open
    | Completed of completedAt: DateTimeOffset

type WorkItem =
    private
        { Id: WorkItemId
          Title: WorkItemTitle
          CreatedAt: DateTimeOffset
          State: WorkItemState }

[<RequireQualifiedAccess>]
module WorkItem =
    let create id title createdAt =
        { Id = id
          Title = title
          CreatedAt = createdAt
          State = Open }

    let restore id title createdAt state =
        match state with
        | Completed completedAt when completedAt < createdAt -> Error CompletionBeforeCreation
        | _ ->
            Ok
                { Id = id
                  Title = title
                  CreatedAt = createdAt
                  State = state }

    let complete completedAt workItem =
        match workItem.State with
        | Completed _ -> Error WorkItemAlreadyCompleted
        | Open when completedAt < workItem.CreatedAt -> Error CompletionBeforeCreation
        | Open ->
            Ok
                { workItem with
                    State = Completed completedAt }

    let id workItem = workItem.Id
    let title workItem = workItem.Title
    let createdAt workItem = workItem.CreatedAt
    let state workItem = workItem.State
