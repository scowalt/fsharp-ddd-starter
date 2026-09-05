# Deployment backups and recovery

Use the [backup helper](../ops/backup/README.md) to capture SQLite and store encrypted restic snapshots in a dedicated repository.
Production storage is a private R2 Standard bucket owned by the deployed application's OpenTofu.
The starter provisions no bucket and does not control application services.

## Operating limits

The baseline supports one application instance with planned deployment downtime.
Back up before every deployment with an existing database, including releases without migrations.
An explicit first deployment has no existing database to back up.
A missing database during an ordinary deployment is an error.

A deployment controller must serialize the entire release and stop the old instance before backup.
The helper's lock protects its own multi-command operation, not an external stop/start sequence.
Restic also uses repository locks, but those locks do not replace release coordination.
Do not bypass locks or automatically remove a lock whose owner is uncertain.

Prepare and download the release before the maintenance window.
Database capture, upload, cleanup, and application startup still take time when no migrations are pending.
This design provides no downtime-duration guarantee and no zero-downtime rollout.

## Tools and configuration

Run `mise install` for the pinned restic, rclone, jq, OpenTofu, and .NET tools.
The helper uses Bash on Linux and invokes the compiled Web artifact through `APP_DLL`.
The [helper command reference](../ops/backup/README.md) defines the exact JSON configuration and all required fields.

Configuration includes:

- Stable application and environment identifiers
- Explicit database, staging, and helper-state directories
- The dedicated repository location and its expected repository identifier
- Positive remote-storage and local-staging budgets
- Finite lock and operation timeouts

Use paths on private persistent storage, outside the application's temporary container filesystem.
Do not point staging or restore commands at the live database directory.
Use the same application/environment identity and helper-state directory across releases.

For R2, provision the [OpenTofu example](../examples/opentofu/backup-storage/README.md) through the application's approved infrastructure process.
The repository uses the dedicated bucket's `database` prefix.
Inventory covers the whole bucket, so unrelated objects also count against the budget.

Initialize the repository explicitly before deploying an application.
Record its repository identifier from restic's configuration and require it in the helper configuration.
A missing repository or changed identifier is an error, not permission to initialize replacement storage.

## Credentials

Doppler supplies the following process environment values:

- `RESTIC_PASSWORD`: recovery password for the restic repository
- `AWS_ACCESS_KEY_ID`: bucket-scoped R2 object access key
- `AWS_SECRET_ACCESS_KEY`: corresponding secret

Generate a unique high-entropy recovery password for each repository and store it in Doppler.
Use region `auto` for R2's S3-compatible endpoint.
Keep infrastructure administration credentials separate from backup runtime credentials.
Do not put secret values into helper JSON, shell arguments, logs, or repository files.
Do not enable shell tracing around secret injection.

Restic encrypts and authenticates database data and metadata before upload.
Recovery needs both the Doppler password and intact repository key files.
Record how to obtain those dependencies from a replacement machine, without files stored only on the original VPS.

Encryption does not prevent an authorized bucket credential from deleting objects.
These backups protect against losing the VPS, not against every compromise or deletion of the R2 account.

## Deployment sequence

1. Prepare the release and preflight required configuration.
2. Acquire the application's deployment lock.
3. Stop the old instance and wait for writes to finish.
4. Run the helper's `backup` command for the current release identifier.
5. If backup or maintenance fails, stop the deployment before migrations.
6. Start the new instance only after backup success.
7. Let startup validate migration history and apply the pending batch.
8. Check application health and record the release and backup snapshot identifiers.
9. Release the deployment lock.

The backup's release identifier names the source application version compatible with the database before migration, not automatically the incoming release.
Record the incoming release separately in deployment logs.

The helper uses the existing SQLite library to create a consistent snapshot.
It does not copy an active database while ignoring its write-ahead log.
Non-serving snapshot and validation entry points never run migrations or open an HTTP listener.
Snapshot capture refuses existing destination files or SQLite sidecars, including dangling links.
It writes into an exclusively created private sibling directory, validates the standalone database, then moves only that database into place without overwrite.
Failure cleanup removes only that invocation's staging directory, never unrelated destination sidecars.

If backup fails before database changes, the deployment controller can restart the old application.
If a migration fails, startup must fail and the complete pending batch must roll back.
If transaction outcome is uncertain, stop and investigate rather than assuming the old release can restart.

After a successful schema migration, do not automatically restore data or start an older incompatible artifact.
An older artifact missing a newly applied migration fails history validation even when its business code tolerates the changed schema.
Prefer a forward release carrying the complete migration catalogue.

## Rotation and storage budgets

Keep two successful deployment recovery snapshots in the selected repository.
The helper must not retain extra groups merely because hostnames or staging paths change.
Incomplete snapshots must not displace successful recovery points.

A new successful backup is required before an older recovery point is retired.
Prune unreferenced data deliberately, including leftovers from failed runs.
If pruning fails, report the failure and stop before migrations rather than hiding retained storage.

Do not expire restic objects by age.
Recent snapshots can reference old packs, and deleting those packs can make the snapshots unusable.
Use the R2 lifecycle rule only to abort abandoned multipart uploads after one day.
Lifecycle cleanup is asynchronous.

Two snapshots are not two independent file copies.
The repository also contains metadata and can contain failed-run leftovers or temporary repacking data.
The helper's remote estimate includes the current bucket contents and conservative upload/cleanup headroom.
Unknown multipart usage must fail closed rather than be counted as zero.

Set explicit remote and local budgets for each application.
Budget rejection is a deployment failure, not permission to increase a limit or buy more storage.
Check the usage report after backup and cleanup, and route failures through the application's existing deployment monitoring.
The `check-storage` command provides an explicit operating check without scheduling another background process.

These checks are operational safeguards, not a provider-enforced quota or guaranteed billing ceiling.
R2 Standard's free allowance is account-wide, not per bucket.
See [current R2 pricing](https://developers.cloudflare.com/r2/pricing/) when choosing a budget.

## Restore procedure

CAUTION: Do not restore directly over a running application's database.
An interrupted in-place restore can leave partial files or lose newer data.

1. Select an explicit completed snapshot and its release identifier.
2. Run the helper's `restore` command into a new private staging directory.
3. Require successful content verification and SQLite integrity/foreign-key validation.
4. Identify the backup's migration history and a compatible application artifact.
5. Obtain explicit operator approval for the data replacement.
6. Stop the application and move its current database and SQLite sidecars together into a private recovery directory.
7. Replace the live database only with the validated standalone restored file.
8. Start the compatible application and inspect its behavior before admitting normal traffic.

Do not leave an old `-wal`, `-shm`, or `-journal` file at the replacement database path.
Old sidecars do not belong to the restored standalone database.
Service control and final replacement are deliberate operator actions, not helper side effects.
The helper never chooses a release or replaces the live database.
If restore fails or is interrupted, keep the live database unchanged.

## Verification and production gate

The normal validation suite tests real encrypted local repositories and SQLite snapshots without cloud credentials.
It covers successful recovery, failure paths, retention, locking, and budget checks.
These tests do not establish live R2 compatibility.

Before first production adoption:

1. Obtain authorization for a dedicated temporary test bucket and test credentials.
2. Apply the reviewed project-owned infrastructure configuration.
3. Initialize an isolated repository with a new recovery password.
4. Exercise encrypted upload, two-snapshot rotation, failure reporting, and restore from R2.
5. Validate the restored SQLite database and representative application data.
6. Record the exact commands, tool versions, snapshot IDs, and results without secrets.
7. Review cleanup of test resources separately, preserving any required evidence.

Do not treat a repository structure check as proof that every stored byte was downloaded and verified.
Use the helper's explicit verification operation and a real restore drill for that evidence.
Repeat restore drills according to the deployed application's operating schedule.
A full download-and-restore is not required before every ordinary deployment.
