# CI

## Contracts

Woodpecker loads both files in `.woodpecker/`. They are independent workflows;
policy failure does not silently remove validation coverage.

| Workflow | Events | Checks |
| --- | --- | --- |
| `policy` | All branch pushes, tag pushes, and pull requests | Original comment checker and 13 regression tests |
| `validate` | Every pull request; pushes to `main` only | Complete `./scripts/validate.sh`, then original Trivy filesystem scan |

There are no path filters, publishers, deployment jobs, services, job secrets, or
new application gates. The original Actions files remain unchanged for rollback.
Local hooks remain separate and unchanged.

The original policy job specifies ten minutes; Validate omits its job deadline
and therefore has GitHub's effective 360-minute default. Under the owner's
maximum-timeout policy, **set and read back Woodpecker repository timeout 360**.
This deliberately relaxes policy's individual deadline. There is no YAML timeout
or watchdog. Neither original workflow requests cancel-in-progress.

## Runtime and commands

Images are pinned multi-platform manifests containing Linux ARM64 and AMD64. The policy
image supplies uv 0.11.32 and Python 3.14; its five parser requirements remain
unchanged. Both original `uv run --no-project --python 3.14
--with-requirements .comment-policy/requirements.txt` commands are preserved in
`policy.yaml`.

Validation uses Ubuntu 24.04 with the ordinary shell, Git, archive utilities,
ripgrep, and .NET runtime libraries supplied explicitly inside the disposable
job. The official mise 2026.9.3 executable is selected for x64/ARM64 and checked
against its published SHA-256 before execution. No tool is installed on the
agent host. The original action's installation selection is retained:

```sh
mise install asdf:mise-plugins/mise-dotnet-core restic rclone jq opentofu
```

`.mise.toml` retains SDK 10.0.400, restic 0.19.1, rclone 1.75.1, jq 1.8.2, and
OpenTofu 1.12.6. SDK roll-forward remains disabled. Fantomas 7.0.6, all NuGet
locks, package pins, migration bytes/identities, and vendored asset hashes remain
unchanged. Gitleaks 8.30.1 and Lefthook 2.1.12 remain local-hook tools, not new
hosted gates.

The same-version FSharp.Core packages bundled by different SDK architectures and
published on NuGet have different identities. To preserve the committed lock hash,
the job verifies Microsoft's exact SDK 10.0.400 x64 archive as **data**, extracts
only its verified FSharp.Core 10.1.400 NUPKG into a fresh private package-only feed,
and selects it via `_FSharpCoreLibraryPacksFolder`. All 28 packaged DLLs were
statically verified as IL-only AnyCPU with no RID/native assets. No x64 runtime or
SDK is installed/executed, no SDK file is replaced, and locked restore, reference
versions and graph remain unchanged. Independent acceptance on 2026-10-15 observed
ARM64 PR11 pipeline 3 and main pipeline 5, each with all 107 .NET tests, 26 local
backup PASS records, three mocked Tofu tests and 13 policy regressions passing.
These dated results are not AMD64 acceptance. Static inspection and local x64
success alone do not establish hosted execution.

The unchanged validation script restores tools, checks formatting/assets and
architecture restrictions, performs locked solution restore and Release build,
runs all five .NET test projects, runs real **local disposable** encrypted-restic
roundtrip/failure/retention/corruption/replacement-copy fixtures, then performs:

```sh
mise exec -- tofu -chdir=examples/opentofu/backup-storage fmt -check -recursive
mise exec -- tofu -chdir=examples/opentofu/backup-storage init -backend=false -input=false -lockfile=readonly
mise exec -- tofu -chdir=examples/opentofu/backup-storage validate
mise exec -- tofu -chdir=examples/opentofu/backup-storage test -no-color -filter=tests/privacy.tofutest.hcl
```

The Cloudflare 5.18.0 provider lock and mocked plan tests remain intact. No live
backend, bucket, credentials, resources, application listener, or production data
is required or authorized. Non-serving CLI subprocesses and in-process TestHost
are authored test fixtures. Keep temporary paths short enough for provider Unix
sockets; the job uses the container's ordinary `/tmp`.

The pinned Trivy action selects 0.70.0 by default. Its native equivalent follows
the successful validation step in the same workspace, including build outputs:

```sh
TRIVY_SCANNERS=vuln,misconfig TRIVY_SEVERITY=HIGH,CRITICAL TRIVY_IGNORE_UNFIXED=true TRIVY_EXIT_CODE=1 TRIVY_CACHE_DIR=.cache/trivy trivy fs .
```

Do not suppress findings, skip tests, change pins, replace real local backup
checks with mocks, or treat scanner warnings as justification to upgrade source.

## Clone and Git inputs

Both workflows use official plugin-git 2.10.1, pinned by manifest digest.
`ref: ${CI_COMMIT_SHA}` fetches the captured head exactly; on PR events,
`merge-pull-request: true` fetches and merges the native target branch. Git user
metadata is non-credential fixture metadata. Conflicts fail the clone. Branch
and tag pushes retain the captured commit without a PR merge.

Full head/target ancestry is available (`partial: false`, `depth: 0`); neither
original check requires an all-ref scan. LFS/submodule loading is disabled as in
the original checkout defaults. The policy checker enumerates the entire Git
checkout/index, never only changed paths. The locally produced merge commit need
not equal GitHub's generated event merge SHA: record the captured head, fetched
base, resulting tree and parents, and associate native feedback with the actual
PR head. Each workflow logs the captured head, fetched `origin` refs, and actual
HEAD commit, parents and tree before checks (after Git installation in Validate).
These are read-only diagnostics, not a new gate or a GitHub event-merge-SHA
requirement; they print no remote URLs or configuration. Local synthetic clone
tests do not prove a hosted PR execution.

## Architecture migration

The workflows already select native x64/ARM64 mise binaries and native SDK/tools;
no bootstrap, package or lock change is needed for AMD64. The canonical
FSharp.Core dependency-data feed remains identical on both architectures. Preserve
its exact archive/package hashes and locked identity rather than substituting the
ARM SDK's differently hashed package.

Both workflows select `labels: {ci_os: linux}`. The parent must first deploy the
reviewed `WOODPECKER_AGENT_LABELS: ci_os=linux` setting to the existing Linux ARM64
agent and verify its real OS/CPU and advertised custom label. Do not merge these
label-dependent workflows before that readback: otherwise valid jobs would queue
without an eligible agent. The native AMD64 replacement must advertise the same
label while retaining its real `linux/amd64` platform; the old platform remains
`linux/arm64`. Keep unsupported OS/CPU hosts out of this Linux CI pool.

Docker selects the native child of each pinned Linux manifest index. Woodpecker
3.19 workflow labels use exact matching, not a task-side `linux/*` glob. The custom
label is routing metadata, not a security boundary or hardware attestation. Do
not spoof the agent platform, add emulation or duplicate matrix workflows.

The following delivery sequence documents the original accepted replacement.
Do not replay activation or Actions disablement for the architecture migration.
Preserve existing settings, capacity one, 2 GiB job limits, fork approval and no
job secrets. Observe fresh migration PR/main jobs on the current ARM64 host, then
native AMD64 jobs after the separately authorized host migration. Local AMD64
checks do not establish hosted AMD64 acceptance or authorize draining, backup,
restore, cloud or service operations.

## Original parent-owned delivery

1. Review and integrate the source separately. Before activation, read back the
   configuration path, timeout **360**, fork approval, empty
   `approval_allowed_users`, all trust capabilities false, existing supported
   clone-plugin allowlisting, and no job secrets. Keep the existing single ARM64
   agent, 2 GiB job limit and capacity; do not add privileges, host/socket mounts,
   public exposure, credentials or runners.
2. Deliver one ordinary reviewed PR. Observe the exact head's native clone,
   workflows, original commands and nonzero test counts, ARM64 execution, and
   matching exact-head GitHub feedback. No separate legacy-Actions-green gate,
   artificial failure/marker PR, or replay of MC26 is required.
3. Merge using the normal `--match-head-commit` guard. Record the actual resulting
   `main` SHA, tree and ordered parents. Require that revision's native push
   workflows and matching GitHub results before replacing corresponding required
   checks. Preserve unrelated protections; empty requirements are a no-op.
4. Only then disable Actions for this repository. A webhook 2xx acknowledges
   transport, not job execution. Obtain separate fresh GET-only acceptance with
   bounded observation of Actions absence; do not manufacture a new post-disable
   event or claim an indefinite guarantee.

If proof fails, keep or re-enable Actions using the byte-identical original
workflows and retain existing protections until a verified replacement exists.
No live activation, settings change, PR, push, merge, publishing, or cutover is
performed by preparing this source. Local x86 checks and manifest/lint results
are not hosted ARM64 acceptance or proof of R2 compatibility.
