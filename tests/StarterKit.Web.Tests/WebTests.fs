namespace StarterKit.Web.Tests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open Microsoft.AspNetCore.TestHost
open Xunit
open StarterKit.Web

module private TestApp =
    let run test =
        task {
            let databasePath =
                Path.Combine(Path.GetTempPath(), $"starterkit-web-{Guid.NewGuid():N}.db")

            try
                use app =
                    Program.buildApp
                        [| "--environment"; "Development" |]
                        (fun webHost -> webHost.UseTestServer() |> ignore)
                        (Some databasePath)

                do! app.StartAsync()
                use client = app.GetTestClient()
                do! test client
                do! app.StopAsync()
            finally
                for suffix in [ ""; "-shm"; "-wal" ] do
                    let file = databasePath + suffix

                    if File.Exists file then
                        File.Delete file
        }

type WebTests() =
    [<Fact>]
    member _.``normal HTML form works without JavaScript``() =
        TestApp.run (fun client ->
            task {
                let! initial = client.GetStringAsync("/")
                Assert.Contains("method=\"post\" action=\"/work-items\"", initial)
                Assert.Contains("data-on:submit__prevent", initial)

                use form =
                    new FormUrlEncodedContent([ KeyValuePair<string, string>("Title", "Test the no-JavaScript path") ])

                let! response = client.PostAsync("/work-items", form)
                Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode)
                Assert.Equal("/", response.Headers.Location.OriginalString)

                let! rendered = client.GetStringAsync("/")
                Assert.Contains("Test the no-JavaScript path", rendered)
                Assert.Contains("Open", rendered)
            })

    [<Fact>]
    member _.``Datastar request receives an SSE element patch``() =
        TestApp.run (fun client ->
            task {
                use request = new HttpRequestMessage(HttpMethod.Post, "/work-items")
                request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("text/event-stream"))

                request.Content <-
                    new StringContent("{\"title\":\"Progressively enhanced\"}", Encoding.UTF8, "application/json")

                let! response = client.SendAsync(request)
                let! body = response.Content.ReadAsStringAsync()

                Assert.Equal(HttpStatusCode.OK, response.StatusCode)
                Assert.Equal("text/event-stream", response.Content.Headers.ContentType.MediaType)
                Assert.Contains("event: datastar-patch-elements", body)
                Assert.True(body.Contains("Progressively enhanced"), body)
                Assert.Contains("id=\"work-items\"", body)
            })
