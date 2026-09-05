namespace StarterKit.WorkTracking.Infrastructure

open System
open System.Data
open System.Globalization
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open StarterKit.WorkTracking.Application
open StarterKit.WorkTracking.Domain

type private WorkItemRow =
    { Id: string
      Title: string
      CreatedAt: string
      State: string
      CompletedAt: string option }

[<RequireQualifiedAccess>]
module SqliteWorkItemStore =
    let private unavailable (ex: exn) = StoreUnavailable ex.Message
    let private corrupt message = CorruptStoredWorkItem message

    let private parseDate fieldName (value: string) =
        match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
        | true, parsed -> Ok parsed
        | false, _ -> Error(corrupt $"Invalid {fieldName}: {value}")

    let private toDomain row =
        match Guid.TryParse row.Id with
        | false, _ -> Error(corrupt $"Invalid work item id: {row.Id}")
        | true, rawId ->
            match WorkItemId.create rawId, WorkItemTitle.create row.Title, parseDate "created_at" row.CreatedAt with
            | Error error, _, _
            | _, Error error, _ -> Error(corrupt $"Invalid stored work item: {error}")
            | _, _, Error error -> Error error
            | Ok id, Ok title, Ok createdAt ->
                let stateResult =
                    match row.State, row.CompletedAt with
                    | "open", None -> Ok Open
                    | "completed", Some completedAt -> parseDate "completed_at" completedAt |> Result.map Completed
                    | state, completedAt ->
                        Error(corrupt $"Invalid state/completed_at combination: {state}/{completedAt}")

                match stateResult with
                | Error error -> Error error
                | Ok state ->
                    WorkItem.restore id title createdAt state
                    |> Result.mapError (fun error -> corrupt $"Invalid stored work item: {error}")

    let private fromDomain workItem =
        let state, completedAt =
            match WorkItem.state workItem with
            | Open -> "open", None
            | Completed value -> "completed", Some(value.ToString("O", CultureInfo.InvariantCulture))

        { Id = WorkItem.id workItem |> WorkItemId.value |> string
          Title = WorkItem.title workItem |> WorkItemTitle.value
          CreatedAt = WorkItem.createdAt workItem |> _.ToString("O", CultureInfo.InvariantCulture)
          State = state
          CompletedAt = completedAt }

    let private readRow (reader: SqliteDataReader) =
        { Id = reader.GetString(0)
          Title = reader.GetString(1)
          CreatedAt = reader.GetString(2)
          State = reader.GetString(3)
          CompletedAt =
            if reader.IsDBNull(4) then
                None
            else
                Some(reader.GetString(4)) }

    let private openConnection connectionString cancellationToken =
        task {
            let connection = new SqliteConnection(connectionString)
            do! connection.OpenAsync(cancellationToken)
            return connection
        }

    let create connectionString : WorkItemStore =
        let load workItemId cancellationToken =
            task {
                try
                    use! connection = openConnection connectionString cancellationToken
                    use command = connection.CreateCommand()

                    command.CommandText <-
                        "SELECT id, title, created_at, state, completed_at FROM work_items WHERE id = $id;"

                    command.Parameters.AddWithValue("$id", workItemId |> WorkItemId.value |> string)
                    |> ignore

                    use! reader = command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)

                    let! hasRow = reader.ReadAsync(cancellationToken)

                    if hasRow then
                        return readRow reader |> toDomain |> Result.map Some
                    else
                        return Ok None
                with ex ->
                    return Error(unavailable ex)
            }

        let list cancellationToken =
            task {
                try
                    use! connection = openConnection connectionString cancellationToken
                    use command = connection.CreateCommand()

                    command.CommandText <-
                        "SELECT id, title, created_at, state, completed_at FROM work_items ORDER BY created_at, id;"

                    use! reader = command.ExecuteReaderAsync(cancellationToken)
                    let rows = ResizeArray<WorkItem>()
                    let mutable failure = None
                    let mutable reading = true

                    while reading && failure.IsNone do
                        let! hasRow = reader.ReadAsync(cancellationToken)

                        if not hasRow then
                            reading <- false
                        else
                            match readRow reader |> toDomain with
                            | Ok workItem -> rows.Add workItem
                            | Error error -> failure <- Some error

                    return
                        match failure with
                        | Some error -> Error error
                        | None -> Ok(List.ofSeq rows)
                with ex ->
                    return Error(unavailable ex)
            }

        let save workItem cancellationToken =
            task {
                try
                    let row = fromDomain workItem
                    use! connection = openConnection connectionString cancellationToken
                    use transaction = connection.BeginTransaction()
                    use command = connection.CreateCommand()
                    command.Transaction <- transaction

                    command.CommandText <-
                        """
                        INSERT INTO work_items (id, title, created_at, state, completed_at)
                        VALUES ($id, $title, $createdAt, $state, $completedAt)
                        ON CONFLICT(id) DO UPDATE SET
                            title = excluded.title,
                            created_at = excluded.created_at,
                            state = excluded.state,
                            completed_at = excluded.completed_at;
                        """

                    command.Parameters.AddWithValue("$id", row.Id) |> ignore
                    command.Parameters.AddWithValue("$title", row.Title) |> ignore
                    command.Parameters.AddWithValue("$createdAt", row.CreatedAt) |> ignore
                    command.Parameters.AddWithValue("$state", row.State) |> ignore

                    command.Parameters.AddWithValue(
                        "$completedAt",
                        row.CompletedAt |> Option.map box |> Option.defaultValue DBNull.Value
                    )
                    |> ignore

                    let! _ = command.ExecuteNonQueryAsync(cancellationToken)
                    transaction.Commit()
                    return Ok()
                with ex ->
                    return Error(unavailable ex)
            }

        { Load = load
          List = list
          Save = save }
