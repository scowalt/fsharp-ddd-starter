namespace StarterKit.Web

open System
open System.Globalization
open Oxpecker.ViewEngine
open StarterKit.WorkTracking.Domain

type WorkItemViewModel =
    { Id: string
      Title: string
      CreatedAt: string
      StateLabel: string
      CanComplete: bool }

[<RequireQualifiedAccess>]
module Views =
    let private formatTimestamp (value: DateTimeOffset) =
        value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"

    let toViewModel workItem =
        let stateLabel, canComplete =
            match WorkItem.state workItem with
            | Open -> "Open", true
            | Completed completedAt -> $"Completed {formatTimestamp completedAt}", false

        { Id = WorkItem.id workItem |> WorkItemId.value |> string
          Title = WorkItem.title workItem |> WorkItemTitle.value
          CreatedAt = WorkItem.createdAt workItem |> formatTimestamp
          StateLabel = stateLabel
          CanComplete = canComplete }

    let workItemsRegion (items: WorkItemViewModel list) (errorMessage: string option) =
        main (id = "work-items", class' = "z-layout-small") {
            header () {
                h1 () { "Work items" }
                p () { "A small state machine carried through explicit application and persistence boundaries." }
            }

            match errorMessage with
            | Some message -> div(class' = "z-alert z-alert-danger").attr ("role", "alert") { message }
            | None -> ()

            section (class' = "z-card") {
                div (class' = "z-card-header") { h2 (class' = "z-card-title") { "Add work item" } }

                div (class' = "z-card-body") {
                    form(method = "post", action = "/work-items")
                        .attr ("data-on:submit__prevent", "@post('/work-items')") {
                        label (for' = "work-item-title") { "Title" }

                        input(
                            id = "work-item-title",
                            name = "Title",
                            type' = "text",
                            maxlength = WorkItemTitle.MaximumLength,
                            required = true,
                            autocomplete = "off"
                        )
                            .attr ("data-bind:title", "")

                        button (type' = "submit", class' = "z-button z-button-primary") { "Create" }
                    }
                }
            }

            section () {
                h2 () { "Current work" }

                if List.isEmpty items then
                    p (id = "empty-state") { "No work items yet." }
                else
                    ul (class' = "z-list") {
                        for item in items do
                            li (class' = "z-card") {
                                div (class' = "z-card-body") {
                                    h3 (class' = "z-card-title") { item.Title }
                                    p () { $"Created {item.CreatedAt}" }
                                    p () { item.StateLabel }

                                    if item.CanComplete then
                                        form(method = "post", action = $"/work-items/{item.Id}/complete")
                                            .attr (
                                                "data-on:submit__prevent",
                                                $"@post('/work-items/{item.Id}/complete')"
                                            ) {
                                            button (type' = "submit", class' = "z-button z-button-default") {
                                                "Complete"
                                            }
                                        }
                                }
                            }
                    }
            }
        }

    let page items (errorMessage: string option) =
        html (lang = "en", class' = "z-not-ready") {
            head () {
                meta (charset = "utf-8")
                meta (name = "viewport", content = "width=device-width, initial-scale=1")
                title () { "StarterKit WorkTracking" }
                link (rel = "stylesheet", href = "/vendor/0build/v0.6.9/kit.min.css")
                script (type' = "module", src = "/vendor/datastar/v1.0.3/datastar.js") { }
            }

            body(class' = "bg color").attr ("style", "--bg: var(--z-bg); --color: var(--z-bg-f)") {
                workItemsRegion items errorMessage
            }
        }
