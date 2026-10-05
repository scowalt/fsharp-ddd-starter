#!/usr/bin/env bash
# shellcheck disable=SC2016
set -euo pipefail
helper=$(realpath "$(dirname "$0")/../backup-helper")
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }
refuses() {
  if "$@" >"$tmp/out" 2>"$tmp/err"; then fail 'command unexpectedly succeeded'; fi
  jq -e '.ok == false and (.error | type == "string")' "$tmp/err" >/dev/null || fail 'missing redacted JSON error'
}
printf '{}\n' >"$tmp/config.json"
refuses "$helper" check-storage "$tmp/config.json"
grep -q configuration "$tmp/err" || fail 'invalid configuration not identified'
echo 'PASS: missing explicit configuration fails closed'

export RESTIC_PASSWORD='local-test-only-not-a-real-secret'
mkdir -m 700 "$tmp/stage" "$tmp/state" "$tmp/repo" "$tmp/bin"
restic -r "$tmp/repo" init >/dev/null
repo_id=$(restic -r "$tmp/repo" cat config | jq -r .id)
jq -n --arg root "$tmp" --arg id "$repo_id" '{app:"test",environment:"local",database:($root+"/live.db"),staging_directory:($root+"/stage"),state_directory:($root+"/state"),repository:{type:"local",path:($root+"/repo")},repository_id:$id,remote_budget_bytes:1073741824,local_budget_bytes:1073741824,lock_wait_seconds:1,operation_timeout_seconds:30}' >"$tmp/config.json"
"$helper" check-storage "$tmp/config.json" >"$tmp/out"
jq -e '.ok == true and .repository_bytes > 0 and .hard_quota == false' "$tmp/out" >/dev/null || fail 'missing actual storage accounting'
echo 'PASS: initialized encrypted repository storage is measured'

real_rclone=$(command -v rclone)
printf '#!/usr/bin/env bash\nif [[ $1 == version ]]; then exec %q "$@"; fi\necho "credentials should never be printed" >&2\nexit 1\n' "$real_rclone" >"$tmp/bin/rclone"
chmod +x "$tmp/bin/rclone"
refuses env PATH="$tmp/bin:$PATH" "$helper" check-storage "$tmp/config.json"
grep -q 'inventory failed' "$tmp/err" || fail 'unknown usage not refused'
! grep -q credentials "$tmp/err" || fail 'process error leaked'
echo 'PASS: unknown inventory fails closed with redacted process errors'
rm "$tmp/bin/rclone"

touch "$tmp/app.dll" "$tmp/live.db"
export APP_DLL="$tmp/app.dll"
printf '#!/usr/bin/env bash\necho "secret source and credentials" >&2\nexit 1\n' >"$tmp/bin/dotnet"
chmod +x "$tmp/bin/dotnet"
refuses env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/config.json" release-1
grep -q 'SQLite capture failed' "$tmp/err" || fail 'capture failure not propagated'
[[ -z $(find "$tmp/stage" -mindepth 1 -print -quit) ]] || fail 'temporary snapshot files survived failure'
[[ $(restic -r "$tmp/repo" snapshots --json | jq length) == 0 ]] || fail 'capture failure uploaded data'
echo 'PASS: failed SQLite capture stops upload and removes temporary files'
cp "$(dirname "$0")/dotnet-double" "$tmp/bin/dotnet"
chmod +x "$tmp/bin/dotnet"
printf 'process-fixture-not-SQLite' >"$tmp/live.db"
env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/config.json" release-1 >"$tmp/out"
jq -e '.ok == true and (.snapshot_id | test("^[a-f0-9]{64}$")) and .release == "release-1"' "$tmp/out" >/dev/null || fail 'successful backup not reported'
restic -r "$tmp/repo" snapshots --json >"$tmp/snapshots.json"
jq -e 'length == 1 and (.[0].tags | index("completed") != null)' "$tmp/snapshots.json" >/dev/null || fail 'successful snapshot not selected as completed'
echo 'PASS: completed encrypted upload reported after repository validation (SQLite process double)'

real_restic=$(command -v restic)
printf '#!/usr/bin/env bash\nfor arg in "$@"; do if [[ $arg == backup ]]; then exec %q "$@" --host previous-vps; fi; done\nexec %q "$@"\n' "$real_restic" "$real_restic" >"$tmp/bin/restic"
chmod +x "$tmp/bin/restic"
env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/config.json" release-2 >"$tmp/out"
rm "$tmp/bin/restic"
mkdir -m 700 "$tmp/stage-2"
jq --arg stage "$tmp/stage-2" '.staging_directory = $stage' "$tmp/config.json" >"$tmp/config-2.json"
env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/config-2.json" release-3 >"$tmp/out"
restic -r "$tmp/repo" snapshots --json >"$tmp/snapshots.json"
jq -e 'length == 2 and ([.[].hostname] | unique | length == 2) and ([.[].paths] | unique | length == 2)' "$tmp/snapshots.json" >/dev/null || fail 'hostname/path grouping retained extra snapshots'
old_ids=$(jq -c '[.[].id] | sort' "$tmp/snapshots.json")
echo 'PASS: latest two completed snapshots across host and staging-path changes'

printf '#!/usr/bin/env bash\nfor arg in "$@"; do if [[ $arg == backup ]]; then %q "$@"; exit 3; fi; done\nexec %q "$@"\n' "$real_restic" "$real_restic" >"$tmp/bin/restic"
chmod +x "$tmp/bin/restic"
refuses env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/config.json" release-partial
grep -q incomplete "$tmp/err" || fail 'exit 3 not reported'
[[ $(restic -r "$tmp/repo" snapshots --json --tag completed | jq -c '[.[].id] | sort') == "$old_ids" ]] || fail 'partial upload displaced recovery points'
[[ $(restic -r "$tmp/repo" snapshots --json --tag pending | jq length) == 1 ]] || fail 'partial fixture did not leave a pending snapshot'
rm "$tmp/bin/restic"
printf '#!/usr/bin/env bash\nexit 1\n' >"$tmp/bin/dotnet"
refuses env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/config.json" release-next
[[ $(restic -r "$tmp/repo" snapshots --json --tag pending | jq length) == 0 ]] || fail 'failed snapshot not cleaned'
[[ $(restic -r "$tmp/repo" snapshots --json --tag completed | jq -c '[.[].id] | sort') == "$old_ids" ]] || fail 'cleanup displaced recovery points'
echo 'PASS: exit 3 never promotes or displaces; next attempt cleans failed snapshots'
cp "$(dirname "$0")/dotnet-double" "$tmp/bin/dotnet"
"$helper" verify "$tmp/config.json" >"$tmp/out"
jq -e '.ok == true and .repository_verification == "all stored data"' "$tmp/out" >/dev/null || fail 'full verification not reported'
snapshot=$(restic -r "$tmp/repo" snapshots --json --tag completed | jq -r '.[-1].id')
env PATH="$tmp/bin:$PATH" "$helper" restore "$tmp/config.json" "$snapshot" "$tmp/stage/recovered" >"$tmp/out"
cmp "$tmp/live.db" "$tmp/stage/recovered/database.sqlite3" || fail 'restored bytes differ'
[[ $(stat -c %a "$tmp/stage/recovered") == 700 ]] || fail 'restore directory not private'
refuses env PATH="$tmp/bin:$PATH" "$helper" restore "$tmp/config.json" "$snapshot" "$tmp/stage/recovered"
refuses env PATH="$tmp/bin:$PATH" "$helper" restore "$tmp/config.json" latest "$tmp/stage/implicit"
refuses env PATH="$tmp/bin:$PATH" "$helper" restore "$tmp/config.json" "$snapshot" "$tmp/unsafe"
echo 'PASS: verified restore is private, isolated, explicit and never overwrites an existing destination'

printf '#!/usr/bin/env bash\nif [[ $1 == version ]]; then exec %q "$@"; fi\necho '\''{"count":1,"bytes":0,"sizeless":1}'\''\n' "$real_rclone" >"$tmp/bin/rclone"
chmod +x "$tmp/bin/rclone"
refuses env PATH="$tmp/bin:$PATH" "$helper" check-storage "$tmp/config.json"
echo 'PASS: sizeless objects are unknown usage, not zero-byte storage'
rm "$tmp/bin/rclone"

for field in remote_budget_bytes local_budget_bytes; do
  jq --arg field "$field" '.[$field] = 1' "$tmp/config.json" >"$tmp/low.json"
  refuses env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/low.json" release-low
  grep -q budget "$tmp/err" || fail 'low budget was not identified'
done
printf '#!/usr/bin/env bash\nprintf "Avail\\n0\\n"\n' >"$tmp/bin/df"
chmod +x "$tmp/bin/df"
refuses env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/config.json" release-no-space
grep -q 'free space' "$tmp/err" || fail 'low physical space was not refused'
rm "$tmp/bin/df"
echo 'PASS: distinct remote/local budgets and low physical staging space refuse work'

refuses env RESTIC_PASSWORD=wrong "$helper" restore "$tmp/config.json" "$snapshot" "$tmp/stage/wrong-password"
[[ ! -e "$tmp/stage/wrong-password" ]] || fail 'wrong password left restore files'
jq '.repository_id = ("0" * 64)' "$tmp/config.json" >"$tmp/wrong-id.json"
refuses "$helper" check-storage "$tmp/wrong-id.json"
echo 'PASS: credentials and expected repository identity fail closed'

printf '#!/usr/bin/env bash\ntouch %q\nsleep 5\nexit 1\n' "$tmp/capture-started" >"$tmp/bin/dotnet"
env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/config.json" release-lock >"$tmp/holder.out" 2>"$tmp/holder.err" &
holder=$!
for ((i=0; i<200; i++)); do [[ -e "$tmp/capture-started" ]] && break; sleep 0.1; done
[[ -e "$tmp/capture-started" ]] || fail 'first invocation did not reach capture'
refuses "$helper" check-storage "$tmp/config.json"
grep -q 'workflow lock wait expired' "$tmp/err" || fail 'second invocation overlapped workflow'
wait "$holder" && fail 'capture failure succeeded'
cp "$(dirname "$0")/dotnet-double" "$tmp/bin/dotnet"
echo 'PASS: concurrent helper workflows do not overlap and waits are bounded'

printf '#!/usr/bin/env bash\nfor arg in "$@"; do if [[ $arg == prune ]]; then exit 1; fi; done\nexec %q "$@"\n' "$real_restic" >"$tmp/bin/restic"
chmod +x "$tmp/bin/restic"
refuses env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/config.json" release-prune
grep -q 'prune failed' "$tmp/err" || fail 'prune failure not identified'
[[ $(restic -r "$tmp/repo" snapshots --json --tag completed | jq -c '[.[].id] | sort') == "$old_ids" ]] || fail 'prune failure lost recovery points'
rm "$tmp/bin/restic"
echo 'PASS: prune failure refuses deployment without displacing recovery points'

printf '#!/usr/bin/env bash\nfor arg in "$@"; do if [[ $arg == backup ]]; then exit 1; fi; done\nexec %q "$@"\n' "$real_restic" >"$tmp/bin/restic"
chmod +x "$tmp/bin/restic"
refuses env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/config.json" release-upload
[[ $(restic -r "$tmp/repo" snapshots --json --tag completed | jq -c '[.[].id] | sort') == "$old_ids" ]] || fail 'upload failure lost recovery points'
rm "$tmp/bin/restic"
echo 'PASS: failed upload preserves both recovery points'

mkdir "$tmp/orphan-payload"
head -c 1048576 /dev/urandom >"$tmp/orphan-payload/random"
restic -r "$tmp/repo" list packs | sort >"$tmp/packs-before"
restic -r "$tmp/repo" backup --json "$tmp/orphan-payload" >"$tmp/orphan-upload"
orphan=$(jq -r 'select(.message_type == "summary") | .snapshot_id' "$tmp/orphan-upload")
restic -r "$tmp/repo" list packs | sort >"$tmp/packs-after"
comm -13 "$tmp/packs-before" "$tmp/packs-after" >"$tmp/orphan-packs"
[[ -s "$tmp/orphan-packs" ]] || fail 'orphan fixture uploaded no packs'
restic -r "$tmp/repo" forget "$orphan" >/dev/null
printf '#!/usr/bin/env bash\nexit 1\n' >"$tmp/bin/dotnet"
refuses env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/config.json" release-cleanup
restic -r "$tmp/repo" list packs | sort >"$tmp/packs-cleaned"
[[ -z $(comm -12 "$tmp/orphan-packs" "$tmp/packs-cleaned") ]] || fail 'orphan packs were not pruned independently'
cp "$(dirname "$0")/dotnet-double" "$tmp/bin/dotnet"
echo 'PASS: standalone prune removes failed-run orphan packs without snapshot deletion'

printf '#!/usr/bin/env bash\nfor arg in "$@"; do if [[ $arg == restore ]]; then sleep 10; exit 1; fi; done\nexec %q "$@"\n' "$real_restic" >"$tmp/bin/restic"
chmod +x "$tmp/bin/restic"
jq '.operation_timeout_seconds = 2' "$tmp/config.json" >"$tmp/timeout.json"
refuses env PATH="$tmp/bin:$PATH" "$helper" restore "$tmp/timeout.json" "$snapshot" "$tmp/stage/interrupted"
[[ ! -e "$tmp/stage/interrupted" ]] || fail 'interrupted restore not removed'
[[ $(<"$tmp/live.db") == process-fixture-not-SQLite ]] || fail 'live data changed'
rm "$tmp/bin/restic"
echo 'PASS: interrupted restore cleans isolated files and leaves live database untouched'

printf '#!/usr/bin/env bash\nfor arg in "$@"; do if [[ $arg == backup ]]; then exec %q "$@" --time "2000-01-01 00:00:00"; fi; done\nexec %q "$@"\n' "$real_restic" "$real_restic" >"$tmp/bin/restic"
chmod +x "$tmp/bin/restic"
refuses env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/config.json" release-clock-rollback
grep -q 'retention postcondition' "$tmp/err" || fail 'forgotten replacement was not identified'
[[ $(restic -r "$tmp/repo" snapshots --json --tag completed | jq -c '[.[].id] | sort') == "$old_ids" ]] || fail 'clock rollback displaced newer recovery points'
rm "$tmp/bin/restic"
echo 'PASS: retention must leave the promoted recovery point, including after clock rollback'

printf '#!/usr/bin/env bash\nfor arg in "$@"; do if [[ $arg == backup ]]; then %q "$@"; result=$?; touch %q; exit "$result"; fi; done\nexec %q "$@"\n' "$real_restic" "$tmp/uploaded" "$real_restic" >"$tmp/bin/restic"
printf '#!/usr/bin/env bash\nif [[ $1 != version && -e %q ]]; then echo '\''{"bytes":1073741825,"count":10,"sizeless":0}'\''; exit 0; fi\nexec %q "$@"\n' "$tmp/uploaded" "$real_rclone" >"$tmp/bin/rclone"
chmod +x "$tmp/bin/restic" "$tmp/bin/rclone"
refuses env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/config.json" release-overrun
grep -q 'observed=1073741825' "$tmp/err" || fail 'post-upload overrun not reported with observed bytes'
[[ $(restic -r "$tmp/repo" snapshots --json --tag completed | jq -c '[.[].id] | sort') == "$old_ids" ]] || fail 'overrun displaced recovery points'
rm "$tmp/bin/restic" "$tmp/bin/rclone"
echo 'PASS: post-upload inventory overrun reports observed bytes and prevents retention/promotion'

printf '#!/usr/bin/env bash\nfor arg in "$@"; do if [[ $arg == --keep-last ]]; then exit 0; fi; done\nexec %q "$@"\n' "$real_restic" >"$tmp/bin/restic"
chmod +x "$tmp/bin/restic"
refuses env PATH="$tmp/bin:$PATH" "$helper" backup "$tmp/config.json" release-forget-noop
grep -q 'retention postcondition' "$tmp/err" || fail 'excess completed snapshots not refused'
[[ $(restic -r "$tmp/repo" snapshots --json --tag completed | jq length) == 3 ]] || fail 'no-op retention fixture did not leave three completed points'
rm "$tmp/bin/restic"
echo 'PASS: retention exit zero is insufficient when the completed count is wrong'

printf '#!/usr/bin/env bash\ntouch %q\nsleep 6\necho repository-lock-fixture\n' "$tmp/repository-lock-ready" >"$tmp/lock-holder"
restic -r "$tmp/repo" backup --stdin-from-command bash "$tmp/lock-holder" >"$tmp/repository-holder.out" 2>"$tmp/repository-holder.err" &
holder=$!
for ((i=0; i<100; i++)); do [[ -e "$tmp/repository-lock-ready" ]] && break; sleep .1; done
[[ -e "$tmp/repository-lock-ready" ]] || fail 'real restic lock fixture not ready'
refuses "$helper" verify "$tmp/config.json"
[[ $(restic -r "$tmp/repo" list locks | wc -l) -ge 1 ]] || fail 'helper removed another restic lock'
wait "$holder"
echo 'PASS: real restic repository lock is respected, with no automatic unlock'

rm "$tmp/state/workflow.lock"
mkfifo "$tmp/state/workflow.lock"
refuses timeout 3 "$helper" check-storage "$tmp/config.json"
grep -q 'unsafe workflow lock file' "$tmp/err" || fail 'non-regular lock file blocked instead of failing closed'
echo 'PASS: non-regular workflow lock files are rejected before opening'
