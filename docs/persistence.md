# Persistence checkpoint

The sample uses direct `Microsoft.Data.Sqlite` with versioned SQL migrations. This is a local adapter choice, not part of the Domain or Application interface, and it did not meet the threshold for an ADR because it is small and replaceable.

| Criterion | Microsoft.Data.Sqlite | Donald | EF Core |
|---|---|---|---|
| Explicit mapping to private F# types | Direct and fully visible | Direct, with row-reader helpers | Possible, but private unions and immutable shapes need additional conversion/configuration |
| Transactions | Native commands and transactions | Uses ADO.NET transactions | Rich unit-of-work transaction support |
| Migrations | No built-in runner; starter applies a small versioned embedded SQL migration | No built-in migration system | Full migration tooling |
| Cancellation and async | Native async ADO.NET methods accept cancellation | Async support, with a smaller abstraction surface | Rich async/cancellation support |
| Test ergonomics | Excellent with temporary real SQLite files | Good with temporary real SQLite files | Good, but provider behavior and change tracking add test concerns |
| SQLite / likely Turso portability | SQLite SQL is visible; a remote Turso adapter would still be separate | SQLite-compatible through the selected ADO.NET provider | SQLite provider works locally; remote Turso needs separate evaluation |
| Dependency/configuration weight | Lowest | Small | Highest |

## Decision

Direct `Microsoft.Data.Sqlite` earns the baseline because one teaching slice needs only four SQL operations, explicit mapping is a core lesson, and a tiny `PRAGMA user_version` migration runner is sufficient. Donald becomes attractive when repeated command/reader boilerplate appears. EF Core becomes attractive when model breadth and migration/query tooling outweigh its mapping and tracking weight.

Changing this adapter must not change `WorkItem`, the use cases, or the `WorkItemStore` port. Integration tests use a temporary real database and reject corrupt row mappings.
