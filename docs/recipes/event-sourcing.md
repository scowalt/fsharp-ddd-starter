# Optional event-sourcing recipe

Event sourcing is not part of the starter baseline and Domain-Driven Design does not imply it. Prefer ordinary state persistence and audit records until a concrete requirement earns the operational cost.

## Adoption checklist

Consider an event store only when at least one demonstrated need exists:

- the complete decision history is business data;
- temporal queries or deterministic replay are required;
- ordinary state plus audit records cannot safely reconstruct required history;
- domain behavior naturally revolves around facts over time; or
- rebuilding projections provides measured value worth the cost.

Require an ADR before implementation. The ADR must explain why state persistence and audit records are insufficient and who will operate replay and recovery tooling.

## Costs to accept

- immutable event schema evolution and backward compatibility;
- projection definitions, rebuild time, and failure recovery;
- replay-safe side effects and idempotency;
- optimistic-concurrency conflict handling;
- separation of domain events from externally published integration events;
- production inspection, repair, backup, and monitoring tools; and
- additional tests for old event versions and projection rebuilds.

## F# shape

Keep the model pure:

```fsharp
let decide command state : Result<DomainEvent list, DomainError> = ...
let evolve state event : State = ...
```

`decide` validates a command against current state. `evolve` folds accepted facts into new state. Storage codecs, stream names, checkpoints, and projections remain outside Domain.

Evaluate Equinox first because it is a composable F# library rather than a domain framework. Do not add Equinox, Marten, EventStoreDB, a broker, or containers until the ADR is accepted.

## Examples

**Adopt**: A regulated ledger must reproduce exactly which accepted facts produced the balance at any past instant. The history is business data, and replay supports a required temporal query.

**Reject**: A work-item screen needs created/completed timestamps and an audit user. State columns plus an audit record satisfy the requirement with less schema-evolution and operational work.

**Reject**: A team wants CQRS because commands and queries have different names. Separate functions or read SQL are enough; no independent scaling, consistency, or history need has been shown.
