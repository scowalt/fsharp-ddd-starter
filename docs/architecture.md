# Architecture

The starter is a modular monolith with one real bounded context. Projects are named for the context before their technical responsibility.

```text
StarterKit.Web ─────────────► StarterKit.WorkTracking.Application ─────► StarterKit.WorkTracking.Domain
      │                                  ▲
      └────► StarterKit.WorkTracking.Infrastructure
```

- **Domain** is the pure model. It has no project references or third-party packages.
- **Application** owns use cases and the `WorkItemStore` outbound port. It depends only on Domain.
- **Infrastructure** supplies the state-based SQLite adapter, explicit row mappings, DbUp migrations, and SQLite snapshot/validation operations. It depends on Application and Domain. Migration identifiers, checksum history, and transaction behavior stay here.
- **Web** is the composition root. It selects the SQLite adapter and system clock/identifier functions, maps HTTP input, renders Oxpecker.ViewEngine HTML, and sends Datastar responses.

Tests cross the same seams as callers. Application tests use an in-memory adapter; integration tests run the same use-case contract against a temporary real SQLite database. Lightweight assembly-reference tests enforce dependency direction.

## Composition root

`src/StarterKit.Web/Program.fs` is the only place that chooses concrete production adapters. Domain and Application do not know that SQLite, DbUp, Oxpecker, Datastar, or 0build exist.

Normal startup runs migrations before accepting requests. Non-serving database commands select snapshot or validation operations before creating the web host, without running migrations.

The Linux backup helper invokes these operations and pinned restic. Its configuration, storage inventory, encryption credentials, retention, and recovery behavior do not become Application ports. The deployment controller remains responsible for service control and whole-release serialization.

See [persistence](persistence.md) and [backup and restore](backup-and-restore.md) for these operational contracts.

## Adding a second bounded context

Add a context only after the language or rules require an independent model:

1. Establish its language and scenarios.
2. Add context-first Domain/Application/Infrastructure projects only for responsibilities it actually needs.
3. Create `CONTEXT-MAP.md` at the repository root.
4. Give each context its own `CONTEXT.md` and record responsibility, upstream/downstream relationships, contract ownership, translation, and communication mode in the map.
5. Translate across context contracts. Do not share mutable entities or add a generic `SharedKernel`.

A bounded-context boundary does not require a process or microservice boundary. Keep contexts in the modular monolith unless an operational requirement justifies extraction.
