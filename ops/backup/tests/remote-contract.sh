#!/usr/bin/env bash
# OFFLINE process-contract tests only. Both storage tools are intercepted. No S3 API.
# shellcheck disable=SC2016
set -euo pipefail
helper=$(realpath "$(dirname "$0")/../backup-helper")
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }
mkdir -m 700 "$tmp/stage" "$tmp/state" "$tmp/bin"
real_restic=$(command -v restic)
real_rclone=$(command -v rclone)
export RESTIC_PASSWORD=offline-test-password AWS_ACCESS_KEY_ID=offline-test-id AWS_SECRET_ACCESS_KEY=offline-test-secret
jq -n --arg root "$tmp" '{app:"offline",environment:"test",database:($root+"/live.db"),staging_directory:($root+"/stage"),state_directory:($root+"/state"),repository:{type:"r2",account_id:("a"*32),bucket:"offline-test-bucket"},repository_id:("b"*64),remote_budget_bytes:1073741824,local_budget_bytes:1073741824,lock_wait_seconds:1,operation_timeout_seconds:10}' >"$tmp/config.json"
{
  printf '#!/usr/bin/env bash\nif [[ $1 == version ]]; then exec %q version; fi\n' "$real_restic"
  printf '%s\n' '[[ $RESTIC_REPOSITORY == "s3:https://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.r2.cloudflarestorage.com/offline-test-bucket/database" ]] || exit 1' '[[ -z ${RESTIC_PASSWORD_COMMAND:-} && -z ${AWS_PROFILE:-} ]] || exit 1' '[[ $* != *offline-test-secret* && $* != *offline-test-password* ]] || exit 1' 'echo '\''{"id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","version":2}'\'
} >"$tmp/bin/restic"
{
  printf '#!/usr/bin/env bash\nif [[ $1 == version ]]; then exec %q version; fi\n' "$real_rclone"
  printf '%s\n' '[[ $RCLONE_CONFIG_R2_ENDPOINT == "https://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.r2.cloudflarestorage.com" && $RCLONE_CONFIG_R2_PROVIDER == Cloudflare && $RCLONE_CONFIG_R2_ENV_AUTH == true ]] || exit 1' '[[ $* == *r2:offline-test-bucket* && $* != *offline-test-secret* ]] || exit 1' 'for arg in "$@"; do if [[ $arg == size ]]; then echo '\''{"bytes":100,"count":1,"sizeless":0}'\''; exit 0; fi; done'
  printf 'exec /bin/cat %q\n' "$tmp/multipart.json"
} >"$tmp/bin/rclone"
chmod +x "$tmp/bin/"*
printf '{"offline-test-bucket":[]}\n' >"$tmp/multipart.json"
env PATH="$tmp/bin:$PATH" RESTIC_PASSWORD_COMMAND='must never execute' AWS_PROFILE='must-not-use' "$helper" check-storage "$tmp/config.json" >"$tmp/out"
jq -e '.ok == true and .repository_bytes == 100' "$tmp/out" >/dev/null || fail 'offline R2 mapping failed'
for response in '{"offline-test-bucket":[{"UploadId":"unfinished"}]}' '{}' 'null' 'credentials-not-JSON'; do
  printf '%s\n' "$response" >"$tmp/multipart.json"
  if env PATH="$tmp/bin:$PATH" "$helper" check-storage "$tmp/config.json" >"$tmp/out" 2>"$tmp/err"; then fail 'unknown multipart usage accepted'; fi
  jq -e '.ok == false' "$tmp/err" >/dev/null || fail 'error not redacted JSON'
  ! grep -q credentials "$tmp/err" || fail 'raw tool output leaked'
done
echo 'PASS: offline R2 endpoint/whole-bucket inventory mapping, environment-only secrets, and unknown multipart refusal (NOT remote integration)'
