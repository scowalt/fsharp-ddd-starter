# StarterKit

Mission Control's F# starter for domain-bearing server-rendered web applications. It makes a proportional Domain-Driven Design workflow and a bounded-context-first modular monolith easier to follow than to bypass.

The teaching slice models open and completed work items through pure F#, an Application-owned port, state-based SQLite persistence, Oxpecker.ViewEngine HTML, Datastar progressive enhancement, and pinned self-hosted 0build CSS. JavaScript is optional for the normal form flow.

## Generate an application

Do not clone this repository's history as an application.

1. Select **Use this template** on GitHub and create a new private repository.
2. Clone the generated repository.
3. Install [mise](https://mise.jdx.dev/).
4. Run `mise install`.
5. Run `./scripts/init.sh <ApplicationName> <BoundedContextName>` with PascalCase .NET identifiers, for example:

   ```bash
   ./scripts/init.sh Workshop Enrollment
   ```

6. Review the staged initialization, run `./scripts/validate.sh`, and commit it as the generated application's history boundary.

The initializer refuses missing, malformed, equal, or non-PascalCase names. It renames projects, namespaces, solution entries, paths, and documentation, then removes template-only instructions and itself.

## Work in the generated application

Read these before changing business behavior:

- `CONTEXT.md` — ubiquitous-language glossary only;
- `docs/domain/scenarios.md` — examples, edge cases, and invariants;
- `AGENTS.md` — domain-first contributor workflow;
- `docs/architecture.md` — dependency rules and composition root.

Install hooks and validate:

```bash
mise run setup
mise run validate
```

The local pre-commit path runs Fantomas and Gitleaks. CI runs formatting, asset-integrity, locked restore, build, all tests, architecture checks, and Trivy.

## Run the sample

```bash
dotnet run --project src/StarterKit.Web
```

The SQLite file is created at `src/StarterKit.Web/data/worktracking.db`. Set normal ASP.NET Core URL configuration as needed for your environment.

## Architecture

```text
StarterKit.Web ─────────────► StarterKit.WorkTracking.Application ─────► StarterKit.WorkTracking.Domain
      │                                  ▲
      └────► StarterKit.WorkTracking.Infrastructure
```

Domain and Application compile and test without Infrastructure. The SQLite adapter and web transport map explicitly at their seams. See `docs/architecture.md` and `docs/persistence.md`.

## Deliberately absent

The baseline has no event store, CQRS framework, mediator, message bus, generic DDD base-class package, SPA, TypeScript, frontend package manager, client build pipeline, or Dev Container. Optional event-sourcing decision guidance is documentation only in `docs/recipes/event-sourcing.md`.
