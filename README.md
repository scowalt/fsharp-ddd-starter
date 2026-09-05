# StarterKit

> **Status: pre-stable.** Generated-app validation passes, but a real domain-bearing pilot is still required before this template is declared stable.

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

The local pre-commit path runs Fantomas and Gitleaks. CI runs formatting, asset integrity, locked restore, build, .NET and local backup tests, architecture checks, OpenTofu example validation, and Trivy.

`mise install` supplies the pinned restic, rclone, jq, and OpenTofu tools used by the backup helper and validation suite. Local tests do not access R2.

## Run the sample

Use Portless with private Tailscale sharing on a shared development machine:

```bash
portless run --tailscale sh -c 'ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/StarterKit.Web -- --urls "http://127.0.0.1:$PORT"'
```

In another terminal, run `portless list` and use its reported local and private Tailscale URLs.
Do not select a shared backend port or use a public tunnel if private sharing is unavailable.

Development creates the SQLite sample at `src/StarterKit.Web/data/worktracking.db`.
Startup validates migration checksums and runs pending DbUp SQL before serving requests.
The old starter's databases without DbUp history are refused rather than silently adopted or deleted.

## Production and database operations

Use an explicit absolute `DATABASE_PATH` in production.
For a first deployment only, set `ALLOW_DATABASE_CREATE=true` to authorize creating a missing database.
Remove that flag for ordinary deployments so an incorrect path cannot create a replacement database.

The deployment controller stops the old instance, completes a backup, and starts the new instance.
The new instance applies migrations in one transaction before accepting requests.
Deployments without migrations still have backup and stop/start downtime.
Downtime-free deployment is future work.

The backup helper invokes non-serving SQLite operations in the same artifact:

```bash
dotnet src/StarterKit.Web/bin/Release/net10.0/StarterKit.Web.dll --snapshot-db /absolute/live.db /private/staging/snapshot.db
dotnet src/StarterKit.Web/bin/Release/net10.0/StarterKit.Web.dll --validate-db /private/staging/snapshot.db
```

These commands do not run migrations or start HTTP.
Snapshot capture refuses an existing destination, and validation does not create missing files.

See [persistence](docs/persistence.md), [backup and restore](docs/backup-and-restore.md), and the [helper command reference](ops/backup/README.md).
The [private R2 OpenTofu example](examples/opentofu/backup-storage/README.md) creates no resources until an authorized application adopts it.
A live R2 backup/restore drill is required before production adoption; local tests alone do not prove remote compatibility.

## Architecture

```text
StarterKit.Web ─────────────► StarterKit.WorkTracking.Application ─────► StarterKit.WorkTracking.Domain
      │                                  ▲
      └────► StarterKit.WorkTracking.Infrastructure
```

Domain and Application compile and test without Infrastructure. The SQLite adapter and web transport map explicitly at their seams. See `docs/architecture.md` and `docs/persistence.md`.

## Deliberately absent

The baseline has no event store, CQRS framework, mediator, message bus, generic DDD base-class package, SPA, TypeScript, frontend package manager, client build pipeline, or Dev Container. Optional event-sourcing decision guidance is documentation only in `docs/recipes/event-sourcing.md`.
