namespace StarterKit.Architecture.Tests

open System
open System.Reflection
open Xunit
open StarterKit.WorkTracking.Application
open StarterKit.WorkTracking.Domain
open StarterKit.WorkTracking.Infrastructure
open StarterKit.Web

module private DependencyRules =
    let references (assembly: Assembly) =
        assembly.GetReferencedAssemblies() |> Array.map _.Name |> Set.ofArray

    let assertDoesNotReference forbidden (assembly: Assembly) =
        let actual = references assembly
        let violations = Set.intersect forbidden actual
        Assert.True(Set.isEmpty violations, $"{assembly.GetName().Name} has forbidden references: {violations}")

type DependencyTests() =
    let domain: Assembly = typeof<WorkItem>.Assembly
    let application: Assembly = typeof<WorkItemStore>.Assembly

    let infrastructure: Assembly =
        Assembly.Load("StarterKit.WorkTracking.Infrastructure")

    let web: Assembly = typeof<RouteDependencies>.Assembly

    [<Fact>]
    member _.``Domain has no framework or outer-layer dependencies``() =
        DependencyRules.assertDoesNotReference
            (set
                [ "Microsoft.AspNetCore.Http.Abstractions"
                  "Microsoft.Data.Sqlite"
                  "Oxpecker"
                  "Oxpecker.ViewEngine"
                  "StarFederation.Datastar.FSharp"
                  application.GetName().Name
                  infrastructure.GetName().Name
                  web.GetName().Name ])
            domain

    [<Fact>]
    member _.``Application depends on Domain but not Infrastructure or Web``() =
        let references = DependencyRules.references application
        Assert.Contains(domain.GetName().Name, references)

        DependencyRules.assertDoesNotReference
            (set
                [ "Microsoft.AspNetCore.Http.Abstractions"
                  "Microsoft.Data.Sqlite"
                  "Oxpecker"
                  "Oxpecker.ViewEngine"
                  "StarFederation.Datastar.FSharp"
                  infrastructure.GetName().Name
                  web.GetName().Name ])
            application

    [<Fact>]
    member _.``Infrastructure does not depend on Web``() =
        let references = DependencyRules.references infrastructure
        Assert.Contains(application.GetName().Name, references)
        Assert.Contains(domain.GetName().Name, references)
        Assert.DoesNotContain(web.GetName().Name, references)
