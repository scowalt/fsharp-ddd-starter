# SQLite persistence and DbUp migrations

Infrastructure uses `Microsoft.Data.Sqlite` for explicit row mappings and DbUp for handwritten SQL migrations.
Domain, Application, and the `WorkItemStore` port do not depend on either library.
Changing the persistence implementation must not change business types or use cases.

## Why these tools

| Choice | Role and trade-off |
|---|---|
| `Microsoft.Data.Sqlite` | Direct SQL and visible mappings, without an ORM or change tracker |
| DbUp | Ordered migration execution with an explicit whole-batch transaction |
| Small checksum journal | Stable migration identities and rejection of changed or missing applied SQL |
| Donald | A possible future row-reader convenience, not a migration system |
| EF Core | Additional model/query tooling, not justified solely to obtain migrations |

Keep the adapter replaceable rather than introducing a generic repository or migration framework.
Local SQLite support does not prove remote Turso compatibility.

## Startup behavior

Web invokes the Infrastructure migration operation before accepting HTTP requests.
The migration operation validates all recorded history even when no scripts are pending.
It applies the complete pending batch and journal entries in one explicit transaction.
A migration failure prevents startup.

An empty database can receive the initial schema.
An existing application database without the expected DbUp history is refused.
The old starter's `PRAGMA user_version = 1` does not establish safe adoption.
Do not delete data, create an empty replacement journal, or guess a baseline to bypass this refusal.

Normal production startup requires an explicit database path and refuses an unexpectedly missing database.
First creation is an explicit initialization operation, not the default response to an incorrect path.
See the [README](../README.md) for environment configuration.

## Adding a migration

1. Describe the intended schema or data change and its effect on existing data.
2. Add a real SQLite upgrade test through the migration interface.
3. Add a new numbered SQL script after the applied history.
4. Register its stable logical identity with the migration catalogue.
5. Keep SQL as UTF-8 without a byte-order mark and with LF line endings.
6. Run the full validation suite, including failure and generated-application tests.

Applied scripts and identifiers are immutable.
Use a new forward migration to correct mistakes.
Do not edit a script merely to make an existing database match a new source checksum.

The catalogue separates logical identifiers from assembly-qualified resource names.
Application generation sets the logical context identity before the first database exists.
After deployment, preserve that identity when changing namespaces, assemblies, folders, or application names.

Keep the baseline to plain SQL without variable substitution or user-defined preprocessing.
Migration SQL cannot issue `PRAGMA`, explicit transaction/savepoint commands, `ATTACH`, or `DETACH`.
The SQLite authorizer rejects those operations so a script cannot commit early or change a database outside the migration transaction.
A change requiring those operations needs a separate reviewed operating procedure, not a guard bypass.

Script resources, identifiers, encoding, order, and checksums must be validated before database changes.
New scripts inserted before applied history, duplicate identifiers, and changed or missing scripts are errors.

## Transaction safety

DbUp transactions are off by default, so explicit transaction configuration is mandatory.
The checksum journal must use the transaction's connection and command factory.
Qualify journal operations and schema inspection with `main` so temporary tables cannot redirect persistent history.
Temporary tables remain available for migration work.
SQLite schema, data, and history are all part of the migration operation's persistent output.

The pinned DbUp implementation has a preprocessing failure path that can return failure after earlier work commits.
The starter must guard that path and retain separate vendor-characterization and adapter-atomicity tests.
Do not simplify execution back to an unverified `WithTransaction()` call alone.

A failed transaction is different from reversing a successfully committed migration.
Do not automatically run down migrations or restore a database because the new application fails after a successful migration.
If transaction completion is uncertain, stop and investigate.

## Deployment and rollback

The baseline uses one production application instance and planned downtime.
The deployment controller stops the old instance, completes an encrypted backup, and then launches the new instance.
Startup migrates before serving requests.
The controller, not DbUp, owns release serialization and service control.

A release without pending SQL still has backup and stop/start downtime.
Downtime-free deployment and multiple application instances need a separate design.

An older application artifact missing a newly applied migration fails history validation even if its business code tolerates the changed schema.
Code-only rollback works with compatible migration history.
After schema changes, prefer a forward release containing the complete catalogue.

See [backup and restore](backup-and-restore.md) for private R2 storage, retention, budgets, and the operator-controlled recovery procedure.
