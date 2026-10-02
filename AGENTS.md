# Agent instructions

This repository uses Domain-Driven Design as a development discipline. Do not treat the folders as sufficient evidence that a change fits the model.

## Required workflow

1. Read the relevant `GLOSSARY.md` before changing domain code.
2. Describe concrete scenarios and edge cases before selecting tactical patterns.
3. Challenge ambiguous terms instead of silently introducing synonyms.
4. Update the glossary as soon as terminology is resolved. Keep it free of implementation details.
5. Write or update domain tests before implementing a changed business rule.
6. Implement the pure domain transition before adapters and endpoints.
7. Re-check context ownership, dependency direction, and explicit boundary mappings.
8. Update `GLOSSARY-MAP.md` only when relationships between two or more real bounded contexts change. Do not create it preemptively.
9. Create an ADR only when the decision is costly to reverse, surprising without context, and based on a genuine trade-off.
10. Run `./scripts/validate.sh` before signoff.

If a request says to "archive" a work item, stop before editing. The WorkTracking glossary defines **Completion** and explicitly rejects **Archive**. Ask whether the request means ordinary completion or a new lifecycle concept, and propose a domain scenario that distinguishes them.

## Dependency rules

- Domain is pure F# and has no project or third-party package references.
- Application depends only on Domain and owns use cases and outbound ports.
- Infrastructure implements Application ports and explicitly maps persistence records.
- Web is the composition root and owns HTTP, Oxpecker, Datastar, 0build, configuration, and concrete adapter selection.
- Do not add a cross-context `SharedKernel`, generic DDD base classes, mediator dispatch, CQRS, event sourcing, messaging, or a microservice without a current demonstrated need.
- Adding an event store requires an ADR. See `docs/recipes/event-sourcing.md`.

## Database and backup changes

- Read `docs/persistence.md` and `docs/backup-and-restore.md` before changing migration or recovery behavior.
- Keep SQL scripts and logical migration identifiers immutable after application. Add forward migrations rather than editing history.
- Preserve logical identifiers during namespace or assembly renames. Require UTF-8 without a byte-order mark and LF line endings.
- Test schema, data, and journal rollback against real SQLite. Do not assume DbUp's default transaction configuration is safe.
- Keep vendor-characterization tests separate from the starter's stronger atomicity guarantees.
- Never adopt a legacy database, delete recovery points, replace live data, or access real R2 storage as an incidental test step.
- Keep production database paths explicit. Only an authorized first deployment can create a missing production database.
- Run real local restic round-trips and failure tests. Do not describe local tests or storage mocks as verified R2 integration.
- Preserve successful recovery points, explicit storage budgets, and redacted failures. Budget checks are not a hard billing cap.
- Infrastructure examples do not authorize provisioning. Production access and live R2 tests require separate approval.
- Do not introduce multiple application instances or downtime-free deployment under an unrelated migration change.

## Review questions

Use `docs/review-checklist.md` and answer its questions in the pull request. In particular: which bounded context owns the change, which scenario changed, and did a transport or persistence shape leak into Domain?
