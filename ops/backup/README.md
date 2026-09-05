# Linux database backup helper

This helper creates encrypted deployment recovery snapshots with restic.
It does not control services, initialize repositories, apply migrations, or replace live databases.
R2 support still requires a separately authorized remote restore drill.
Local tests are not proof of live R2 compatibility.

## Tools

Use these exact versions through mise:

```bash
mise install restic@0.19.1 rclone@1.75.1 jq@1.8.2
mise exec restic@0.19.1 rclone@1.75.1 jq@1.8.2 -- ops/backup/test.sh
```

The supported registry sources are `aqua:restic/restic`, `aqua:rclone/rclone`, and `aqua:jqlang/jq`.
The helper refuses other versions.
The application needs its matching .NET runtime and built operational DLL.
The local integration test also needs the existing .NET SDK, but no additional NuGet packages or sqlite3 executable.

Linux dependencies are Bash, GNU coreutils, util-linux `flock`, and standard shell utilities.
Implementation testing used Bash 5.2.21, coreutils 9.4, util-linux 2.41.3, and .NET SDK 10.0.400.
ShellCheck 0.9.0 is optional for the regression entrypoint.
No Python, cloud SDK, HTTP server, public tunnel, or infrastructure provisioner is required.

## Configuration

Use one private bucket and one dedicated repository per application/environment.
The R2 repository is always `s3:https://ACCOUNT.r2.cloudflarestorage.com/BUCKET/database`.
The inventory covers the entire bucket, not only the `database` prefix.
The local repository form exists for local drills and automated tests.

Create a JSON file with every field below.
The byte values in this example are for disposable local tests, not a production recommendation.
Choose explicit production values from the database size and available recovery capacity.

```json
{
  "app": "my-app",
  "environment": "production",
  "database": "/srv/my-app/data/application.db",
  "staging_directory": "/srv/my-app/backup-staging",
  "state_directory": "/srv/my-app/backup-state",
  "repository": {
    "type": "r2",
    "account_id": "REPLACE_WITH_32_LOWERCASE_HEX_CHARACTERS",
    "bucket": "my-app-production-backups"
  },
  "repository_id": "REPLACE_WITH_64_LOWERCASE_HEX_RESTIC_CONFIG_ID",
  "remote_budget_bytes": 1073741824,
  "local_budget_bytes": 1073741824,
  "lock_wait_seconds": 30,
  "operation_timeout_seconds": 900
}
```

For a local repository, replace only `repository`:

```json
{"type": "local", "path": "/absolute/path/to/already-initialized-repository"}
```

Unknown fields, missing fields, nonpositive budgets, and malformed values cause failure.
Budgets accept integer bytes up to 1 PiB.
Lock waits accept 1–3600 seconds.
Each external operation accepts a timeout of 1–86400 seconds.
Application/environment names accept lowercase letters, digits, and hyphens, with a maximum length of 63 characters.

Before use:

1. Create separate staging and state directories owned by the helper user, with mode `0700`.
2. Use canonical absolute paths without symlink components.
3. Keep the database and local repository outside those directories.
4. Keep `state_directory` stable across releases and staging-directory changes.
5. Restrict write access to the configuration, application artifacts, executable search path, and parent directories.
6. Use the same state directory for every helper invocation for this database/repository on this host.
7. Keep the repository exclusive to this application/environment and this helper snapshot format.

The state directory contains only the persistent workflow lock file.
Do not delete or replace that directory or lock file while a workflow can still run.
Other restic writers and operators must also respect deployment serialization.
The helper has no distributed workflow lock.

### Credentials and repository initialization

Inject these environment variables through Doppler or another approved secret injector:

| Variable | Requirement |
|---|---|
| `RESTIC_PASSWORD` | Nonempty recovery password |
| `AWS_ACCESS_KEY_ID` | Bucket-scoped R2 object-access credential, required for R2 |
| `AWS_SECRET_ACCESS_KEY` | Matching R2 secret, required for R2 |
| `APP_DLL` | Canonical absolute path to the built application DLL, required for backup/restore |

Never put secrets in command arguments, this JSON file, committed files, or shell tracing output.
The helper clears inherited restic/rclone configuration, password commands, proxy overrides, and AWS profiles before external operations.
It uses the injected credentials and system TLS trust only.
It never offers insecure TLS, public endpoints, or anonymous access.

For first-time repository setup, use a separately authorized operator session.
Set `RESTIC_REPOSITORY` to the dedicated repository location through the environment.
After credential injection, run:

```bash
restic init
restic cat config | jq -r .id
```

Record that ID as `repository_id`.
Do not run `init` to repair a missing or inaccessible expected repository.
The helper never runs `init` or `unlock`.
R2 bucket privacy, Standard storage, and the one-day multipart-abort rule are infrastructure requirements.
The helper cannot prove those requirements from an object inventory.

## Commands

Run commands inside the pinned mise environment after credential injection.
Success returns JSON on stdout with exit code zero.
Failure returns a redacted JSON error on stderr with nonzero exit.
Only zero exit authorizes the deployment controller to continue.
A timeout, signal, partial result, missing response, or lost connection is not success.

```bash
ops/backup/backup-helper check-storage /absolute/backup.json
ops/backup/backup-helper backup /absolute/backup.json RELEASE_ID
ops/backup/backup-helper verify /absolute/backup.json
ops/backup/backup-helper restore /absolute/backup.json FULL_SNAPSHOT_ID /srv/my-app/backup-staging/drill-2026-09-05
```

`check-storage` authenticates the expected repository and reports actual repository bytes plus local staging bytes.
It refuses unknown usage, existing overruns, or insufficient minimum local space.
It does not capture the database or prove that the next backup fits.
The deployment controller must also make sure that its database path, application DLL, and release artifact are correct before downtime.

`backup` requires an existing database and an explicit release identifier.
It captures SQLite through `dotnet "$APP_DLL" --snapshot-db SOURCE DEST`.
It then calls `dotnet "$APP_DLL" --validate-db DEST`.
Both operations must return matching integrity, foreign-key, size, SHA-256, and migration-history metadata.
The helper rejects a captured file with a nonempty WAL or rollback journal.
It uploads only `database.sqlite3` and `metadata.json`.

A successful backup returns the full promoted `snapshot_id`, release, integrity metadata, retention summary, and observed repository bytes.
The SHA-256 identifies the standalone snapshot, not an active source main file without its WAL.
The backup repository check covers repository structure, not every stored data byte.
Restic also authenticates and encrypts data before upload with its normal upload safeguards.

`verify` runs `restic check --read-data`, which reads all stored data.
This costs download time and remote requests.
It does not run a SQLite restore or replace a recurring restore drill.

`restore` accepts only a full 64-character completed snapshot ID for this application/environment.
It rejects `latest`, pending snapshots, foreign snapshots, and existing destinations.
The destination must be a new named direct child of the private staging directory.
The helper inspects the tree, requires exactly the expected regular files, and runs `restic restore --verify`.
It then makes sure that SQLite integrity, foreign keys, SHA-256, migration history, and release metadata match.
Failure removes only its new isolated restore directory and temporary work directory.
Success leaves the isolated database and metadata for operator review.

### Deployment and retention

The deployment controller owns the wider deployment lock and service lifecycle.
Its deployment lock must be separate from the helper's `workflow.lock`.

1. Prepare the release before downtime.
2. Run the storage preflight and other controller preflight steps.
3. Acquire the deployment lock.
4. Stop the application and wait for writes to finish.
5. If a database exists, run `backup` and require zero exit.
6. For an explicit first deployment, handle the absent database as initialization instead.
7. Start the new release only after successful backup, retention, cleanup, and budget checks.
8. Keep the deployment lock through startup migrations and the application health check.

The helper serializes its entire multi-command workflow with `flock`.
Each restic operation also respects restic repository locks and uses a bounded lock wait.
Each external process has a timeout, followed by a five-second termination grace period.
The rclone retry count is one, with bounded connection and idle timeouts.
The total workflow can exceed one operation timeout because it contains several commands.
Local filesystem operations still depend on a responsive kernel/filesystem.

Uploads start with a `pending` tag.
Only exit zero, repository structure validation, and the post-upload budget checks permit promotion to `completed`.
Restic tag changes produce a new snapshot ID, which the helper records in its success result.
Exit 3 is incomplete and never promotes a snapshot.

Retention selects the latest two `completed` snapshots using explicit application/environment tags and `--group-by ''`.
It never uses default host/path grouping or a unique-release retention group.
After retention and pruning, the helper makes sure that the replacement still exists and the completed count is correct.
Clock rollback that causes removal of the replacement therefore refuses deployment.

At the next backup attempt, the helper removes pending snapshots and runs standalone `prune`, even when no snapshot was forgotten.
It also runs standalone `prune` after retention.
Pruning uses `--max-unused 0 --max-repack-size 0` to avoid data-pack repacking overlap.
Unreferenced packs are removed, but unused bytes inside still-referenced packs can remain.
Cleanup failure refuses deployment and leaves retained recovery points referenced.
A failure after promotion can leave a valid extra completed snapshot until later maintenance.

Two snapshots can share encrypted packs.
They are not two independent repository copies.
Never age-expire restic objects or delete the last recovery points when deployment stops.

## Storage safeguards and limits

These checks are not a provider-enforced quota or a guaranteed billing ceiling.
Inventory is a point-in-time observation.
Concurrent external writers, file growth, metadata growth, request charges, and provider accounting can exceed an estimate.
The helper never increases budgets automatically.

Let `U` be actual whole-repository object bytes, `S` the captured payload bytes, and `H` a fixed 64 MiB allowance.
Before capture, the estimate uses main-file bytes plus existing WAL bytes in place of `S`.
Before upload, the required remote headroom is `2*U + 2*S + H <= remote_budget_bytes`.
Before retention, it requires `2*U + H <= remote_budget_bytes` again.
The duplicate allowance covers upload/maintenance overlap conservatively, without assuming compression or deduplication savings.
Existing orphan packs count in `U`.

Remote accounting uses `rclone size --json`, not restic logical snapshot statistics.
Failed listings, malformed numbers, and sizeless objects mean unknown usage and cause refusal.
For R2, `rclone backend list-multipart-uploads` must report the exact bucket with an empty list.
Multipart part sizes are not known from that listing, so any unfinished upload causes refusal.
The one-day lifecycle rule removes abandoned multipart uploads independently of restic pruning.
The helper does not automatically abort uncertain uploads.

Local accounting uses `du -sb` over the entire staging directory and `df` for available space.
Existing isolated restores and abandoned temporary files count against the separate local budget.
Before capture, local headroom includes twice the source estimate, twice `U`, and `H`.
Before upload, it includes the existing staged payload, another payload allowance, twice `U`, and `H`.
Before restore, it uses twice restic's uncompressed restore size, twice `U`, and `H`.
Restic has no persistent local cache because the helper uses `--no-cache`.
All external temporary files use a private workflow directory inside staging.

Post-upload, post-cleanup, and post-restore observations refuse overruns.
Budget errors report observed byte values when known.
A remote overrun can block helper cleanup too.
An authorized operator must then investigate usage and choose a safe restic maintenance action without deleting recovery points arbitrarily.

Normal success, failure, and caught signals remove private temporary files.
SIGKILL, machine loss, or an unresponsive filesystem can leave temporary files or uncertain repository locks.
An operator must make sure that no workflow is active before removing abandoned local files or resolving restic locks.
The helper never treats an uncertain lock as stale automatically.

## Recovery from a replacement machine

Recovery requires the intact repository, including its encrypted key files, the recovery password, and bucket access credentials.
It also requires compatible tooling, the application operational DLL with its runtime files, and the documented repository ID/configuration.
Nothing in the original helper state directory is a recovery credential.

1. Install the pinned tools and the matching .NET runtime on the replacement machine.
2. Obtain the application artifact and credentials from their approved independent stores.
3. Create new private staging/state directories and configure the same repository identity.
4. Run `verify`.
5. Select an explicit completed snapshot ID from the deployment record or an authenticated `restic snapshots --json` listing.
6. Run `restore` into a new isolated directory.
7. Review the reported release, migration history, and application data.
8. Stop the application before any live-file replacement.
9. Preserve the current database and all associated WAL/SHM files as a separate recovery set.
10. Require explicit operator confirmation before replacing live data through the application's documented recovery procedure.

The helper intentionally supplies no live-file replacement command.
After a schema change, use a compatible forward release with the complete migration catalogue.
Do not automatically restore data or launch an older incompatible artifact after a committed migration.
Each application owner must schedule recurring restore drills.

## Local regression tests

From a repository with a Release build:

```bash
mise exec restic@0.19.1 rclone@1.75.1 jq@1.8.2 -- ops/backup/test.sh
```

For an artifact from another worktree or a published release directory:

```bash
APP_DLL=/absolute/release/StarterKit.Web.dll \
  mise exec restic@0.19.1 rclone@1.75.1 jq@1.8.2 -- ops/backup/test.sh
```

The tests create only disposable local encrypted repositories and SQLite files.
They inject a literal test password, never use production credentials, and remove their temporary data.
The F# fixture runs outside tracked source and loads existing application libraries through the installed SDK.
It saves and reads the work item through the real store interface.
An open SQLite read transaction keeps committed work-item data in the source WAL during capture.

`tests/failures.sh` uses real restic with a documented SQLite process double and deterministic process-failure injection.
It is not a SQLite integration test.
`tests/remote-contract.sh` intercepts both storage processes before any S3 request.
It proves only argument/environment handling and unknown-usage refusal.
`tests/integration.sh` uses the actual non-serving DLL, real SQLite, real encrypted restic, and a replacement-machine repository copy.
It covers WAL capture, store reads after restore, matching integrity/history metadata, wrong passwords, missing keys, and corrupt packs.

### Remaining authorized remote verification

Before production adoption, use a separately authorized temporary R2 bucket for:

- Bucket-scoped credentials, private access, Standard storage, and the multipart-abort lifecycle
- Restic initialization, upload, structure validation, promotion, two-snapshot retention, and standalone pruning
- Paginated whole-bucket inventory and multipart listings, including permissions and unknown-usage failures
- Actual upload/prune overlap, request costs, and observed budget headroom
- Wrong credentials, interrupted uploads, leftover cleanup, repository locks, and network timeouts
- Full download verification and an isolated SQLite restore on a replacement machine

No live R2 test, real bucket access, or infrastructure provisioning formed part of helper implementation.
