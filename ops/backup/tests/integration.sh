#!/usr/bin/env bash
# REAL SQLite + encrypted restic integration. No database/process doubles here.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
helper="$here/../backup-helper"
: "${APP_DLL:?Set APP_DLL to the built application with non-serving SQLite operations}"
artifact=$(dirname "$APP_DLL")
tmp=$(mktemp -d)
fixture_pid=''
cleanup() {
  touch "$tmp/release-wal"
  if [[ -n $fixture_pid ]]; then wait "$fixture_pid" || true; fi
  rm -rf "$tmp"
}
trap cleanup EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }
refuses() {
  if "$@" >"$tmp/out" 2>"$tmp/err"; then fail 'unsafe operation succeeded'; fi
  jq -e '.ok == false' "$tmp/err" >/dev/null || fail 'nonzero command did not return helper failure'
}
case $(uname -m) in
  x86_64) runtime=linux-x64 ;;
  aarch64) runtime=linux-arm64 ;;
  *) fail 'integration fixture native SQLite supports Linux x64/arm64 only' ;;
esac
export LD_LIBRARY_PATH="$artifact/runtimes/$runtime/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
cp "$here/store-fixture.fsx.in" "$tmp/fixture.fsx"
fixture() { dotnet fsi --lib:"$artifact" "$tmp/fixture.fsx" "$@"; }
fixture create-wal "$tmp/live.db" "$tmp/wal-ready" "$tmp/release-wal" >"$tmp/fixture.out" 2>"$tmp/fixture.err" &
fixture_pid=$!
for ((i=0; i<300; i++)); do
  [[ -f "$tmp/wal-ready" ]] && break
  kill -0 "$fixture_pid" 2>/dev/null || { read -r message <"$tmp/fixture.err"; fail "SQLite fixture failed: $message"; }
  sleep 0.1
done
[[ -f "$tmp/wal-ready" && -s "$tmp/live.db-wal" ]] || fail 'live WAL fixture not ready'
mkdir -m 700 "$tmp/stage" "$tmp/state" "$tmp/repo"
export RESTIC_PASSWORD='local-integration-test-password-not-a-real-secret'
restic -r "$tmp/repo" init >/dev/null
repo_id=$(restic -r "$tmp/repo" cat config | jq -r .id)
jq -n --arg root "$tmp" --arg id "$repo_id" '{app:"roundtrip",environment:"local",database:($root+"/live.db"),staging_directory:($root+"/stage"),state_directory:($root+"/state"),repository:{type:"local",path:($root+"/repo")},repository_id:$id,remote_budget_bytes:1073741824,local_budget_bytes:1073741824,lock_wait_seconds:1,operation_timeout_seconds:60}' >"$tmp/config.json"
# Keep the WAL connection open: raw main-file copies would miss the saved item.
sha256sum "$tmp/live.db" "$tmp/live.db-wal" >"$tmp/source-before"
"$helper" backup "$tmp/config.json" release-real-wal >"$tmp/backup.json"
sha256sum -c "$tmp/source-before" >/dev/null || fail 'backup changed the live database/WAL'
jq -e '.ok == true and .validation.integrity == "ok" and .validation.foreignKeys == "ok" and (.validation.migrationHistory | length == 1)' "$tmp/backup.json" >/dev/null || fail 'real integrity or migration history missing'
snapshot=$(jq -r .snapshot_id "$tmp/backup.json")
touch "$tmp/release-wal"
wait "$fixture_pid"
fixture_pid=''
echo 'PASS: real non-serving DLL captured a standalone SQLite snapshot including live WAL data'

# Replacement-machine drill: a repository copy, injected password and application
# artifact suffice. Remove the original repository and all original helper state.
cp -a "$tmp/repo" "$tmp/replacement-repo"
rm -rf "$tmp/repo" "$tmp/stage" "$tmp/state"
mkdir -m 700 "$tmp/replacement-stage" "$tmp/replacement-state"
jq --arg root "$tmp" '.repository.path=($root+"/replacement-repo") | .staging_directory=($root+"/replacement-stage") | .state_directory=($root+"/replacement-state")' "$tmp/config.json" >"$tmp/replacement.json"
"$helper" verify "$tmp/replacement.json" >"$tmp/verified.json"
"$helper" restore "$tmp/replacement.json" "$snapshot" "$tmp/replacement-stage/recovered" >"$tmp/restored.json"
fixture read "$tmp/replacement-stage/recovered/database.sqlite3"
jq -e --slurpfile backup "$tmp/backup.json" '.validation == $backup[0].validation and .release == "release-real-wal" and .live_database_replaced == false' "$tmp/restored.json" >/dev/null || fail 'restored hash/history/release differs'
[[ $(stat -c %a "$tmp/replacement-stage/recovered/database.sqlite3") == 600 ]] || fail 'restored SQLite file is not owner-only'
echo 'PASS: replacement-machine restore verifies encrypted bytes, SQLite, hash and migration history'

live_hash=$(sha256sum "$tmp/live.db")
refuses env RESTIC_PASSWORD=wrong "$helper" restore "$tmp/replacement.json" "$snapshot" "$tmp/replacement-stage/wrong"
mv "$tmp/replacement-repo/keys" "$tmp/saved-keys"
refuses "$helper" restore "$tmp/replacement.json" "$snapshot" "$tmp/replacement-stage/no-keys"
mv "$tmp/saved-keys" "$tmp/replacement-repo/keys"
pack=$(find "$tmp/replacement-repo/data" -type f -print -quit)
chmod u+w "$pack"
printf 'deliberately corrupted encrypted pack' >"$pack"
refuses "$helper" verify "$tmp/replacement.json"
refuses "$helper" restore "$tmp/replacement.json" "$snapshot" "$tmp/replacement-stage/corrupt"
[[ ! -e "$tmp/replacement-stage/corrupt" && ! -e "$tmp/replacement-stage/wrong" && ! -e "$tmp/replacement-stage/no-keys" ]] || fail 'failed restore left files behind'
[[ $(sha256sum "$tmp/live.db") == "$live_hash" ]] || fail 'failure paths overwrote live database'
echo 'PASS: real wrong password, missing keys and corrupt repository never overwrite live data'
