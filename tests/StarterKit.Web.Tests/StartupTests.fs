namespace StarterKit.Web.Tests

open System
open System.IO
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Xunit
open StarterKit.Web
open OperationalFixture

type StartupTests() =
    let testHost (host: IWebHostBuilder) = host.UseTestServer() |> ignore

    [<Theory>]
    [<InlineData("unspecified")>]
    [<InlineData("relative")>]
    [<InlineData("missing")>]
    [<InlineData("empty")>]
    [<InlineData("empty-allowed")>]
    [<InlineData("legacy")>]
    [<InlineData("uninitialized-sqlite")>]
    member _.``production refuses unspecified relative missing empty or legacy databases before hosting``
        (kind: string)
        =
        use fixture = new DirectoryFixture()
        let path = fixture.File "production.db"

        if kind.StartsWith("empty") then
            File.WriteAllBytes(path, [||])

        if kind = "legacy" then
            execute path "CREATE TABLE old_schema(value TEXT);" |> ignore

        if kind = "uninitialized-sqlite" then
            execute path "VACUUM;" |> ignore

        let args =
            [ "--environment"
              "Production"
              "--contentRoot"
              fixture.Path
              if kind <> "unspecified" then
                  "--DATABASE_PATH"
                  if kind = "relative" then "relative.db" else path
              if kind = "empty-allowed" then
                  "--ALLOW_DATABASE_CREATE"
                  "true" ]
            |> List.toArray

        let before =
            if File.Exists path then
                Some(File.ReadAllBytes path)
            else
                None

        Assert.Throws<StartupException>(fun () ->
            use app = Program.buildApp args testHost None
            ())
        |> ignore

        match before with
        | None -> Assert.False(File.Exists path)
        | Some bytes -> Assert.Equal<byte>(bytes, File.ReadAllBytes path)

        Assert.False(Directory.Exists(fixture.File "data"))

    [<Fact>]
    member _.``explicit first production creation starts only after migrations and ordinary restart validates history``
        ()
        =
        task {
            use fixture = new DirectoryFixture()
            let path = fixture.File "production;safe.db"
            let args = [| "--environment"; "Production"; "--DATABASE_PATH"; path |]

            use first =
                Program.buildApp (Array.append args [| "--ALLOW_DATABASE_CREATE"; "true" |]) testHost None

            Assert.Equal(1L, execute path "SELECT COUNT(*) FROM __migration_history;" :?> int64)
            do! first.StartAsync()
            use client = first.GetTestClient()
            let! response = client.GetStringAsync("/")
            Assert.Contains("work-items", response)
            do! first.StopAsync()
            use second = Program.buildApp args testHost None
            Assert.Equal(1L, execute path "SELECT COUNT(*) FROM __migration_history;" :?> int64)
        }

    [<Fact>]
    member _.``real startup failure returns nonzero with redacted actionable error and no listener``() =
        use fixture = new DirectoryFixture()
        let path = fixture.File "private-missing.db"

        let code, stdout, stderr =
            cli fixture.Path [ "--environment"; "Production"; "--DATABASE_PATH"; path ]

        Assert.Equal(1, code)
        Assert.Equal("", stdout)
        Assert.Contains("ALLOW_DATABASE_CREATE", stderr)
        Assert.DoesNotContain(path, stderr)
        Assert.False(File.Exists path)

    [<Fact>]
    member _.``development retains the sample database path``() =
        use fixture = new DirectoryFixture()

        use app =
            Program.buildApp [| "--environment"; "Development"; "--contentRoot"; fixture.Path |] testHost None

        Assert.True(File.Exists(fixture.File(Path.Combine("data", "worktracking.db"))))

    [<Fact>]
    member _.``changed migration history prevents TestHost creation without changing the database``() =
        use fixture = new DirectoryFixture()
        let path = fixture.File "history.db"
        let args = [| "--environment"; "Development" |]

        do
            use first = Program.buildApp args testHost (Some path)
            ()

        execute path "UPDATE __migration_history SET sha256 = 'changed';" |> ignore
        let before = File.ReadAllBytes path

        Assert.Throws<StartupException>(fun () ->
            use app = Program.buildApp args testHost (Some path)
            ())
        |> ignore

        Assert.Equal<byte>(before, File.ReadAllBytes path)
