# Private R2 backup storage

This is a project-owned OpenTofu example, not a deployed resource or shared Mission Control bucket.
Copy it into a generated application's infrastructure only when that application is ready for an authorized production deployment.
Use one dedicated bucket and restic repository per application/environment.

## What it configures

- R2 Standard storage, with no public custom hostname
- Explicitly disabled public `r2.dev` access
- Protection against accidental bucket destruction while the resource remains in configuration
- Cleanup of abandoned multipart uploads after one day
- No age-based deletion of restic objects and no Infrequent Access transition

Recent restic snapshots can reference old objects.
Do not add object-expiry rules to a restic repository.

## Validate without cloud access

From the starter root:

```bash
mise exec -- tofu -chdir=examples/opentofu/backup-storage init -backend=false -input=false
mise exec -- tofu -chdir=examples/opentofu/backup-storage fmt -check -recursive
mise exec -- tofu -chdir=examples/opentofu/backup-storage validate
mise exec -- tofu -chdir=examples/opentofu/backup-storage test -filter=tests/privacy.tofutest.hcl
```

These commands install and validate the provider. The selected test file uses a fully mocked provider and plan-only assertions.
They do not contact a bucket or create resources.
No Cloudflare credentials are needed for this validation.
Provider installation needs access to the OpenTofu registry and distribution endpoints.

## Project adoption

1. Copy these resources into the application's existing OpenTofu configuration.
2. Use the application's protected state backend and review resource names before migration or import.
3. Supply `cloudflare_account_id` and a dedicated `bucket_name` through the application's inputs.
4. Supply infrastructure administration credentials through `CLOUDFLARE_API_TOKEN`, preferably with Doppler.
5. Review the resulting plan and obtain authorization before applying it.
6. Create application object credentials restricted to this bucket.
7. Store object credentials and the restic recovery password in the application's Doppler configuration.
8. Initialize the repository explicitly and complete an encrypted remote backup/restore drill before production adoption.

The helper needs object read/write/delete/list access for snapshots, locks, inventory, and pruning.
Use a bucket-scoped R2 Object Read & Write token, not the infrastructure administration token.
Cloudflare's R2 token creation interface supplies an Access Key ID and Secret Access Key for the S3-compatible endpoint.
Credential creation is an explicit operator step here: this example does not claim to automate secret issuance through the pinned provider.
Never paste secret values into HCL, `.tfvars`, command arguments, or source control.

Use `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY` for object access, with region `auto`.
Use the `restic_repository` output for the repository location.
Doppler supplies `RESTIC_PASSWORD`; intact restic key files in the repository are also required for recovery.
Keep infrastructure-administration credentials separate from backup runtime credentials.

See [backup and restore](../../../docs/backup-and-restore.md) for helper configuration, budgets, rotation, and recovery.

## Costs and lifecycle limits

R2 Standard has an account-wide free allowance, not an allowance per bucket.
Two successful snapshots plus repository metadata and temporary cleanup space still consume storage.
The helper's checks are operational budgets, not a hard R2 quota or billing ceiling.
See [current R2 pricing](https://developers.cloudflare.com/r2/pricing/).

Cloudflare provider `5.18.0` expresses lifecycle ages in seconds, so one day is `86400`.
In this version, deleting the lifecycle resource from configuration does not delete the remote rules.
Modify the rules explicitly and review provider behavior before changing or removing them.
Lifecycle expiry is asynchronous, so abandoned-upload cleanup is not an instantaneous storage cap.

`prevent_destroy` protects this resource while its lifecycle configuration is present.
It is not protection against account administrators or deleting the resource block itself.
Do not remove resources or destroy the bucket as an automatic application cleanup action.
