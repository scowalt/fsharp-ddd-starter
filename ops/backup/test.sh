#!/usr/bin/env bash
# Run after the application's Release build, inside the pinned mise environment.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
if [[ -z ${APP_DLL:-} ]]; then
  APP_DLL="$root/src/StarterKit.Web/bin/Release/net10.0/StarterKit.Web.dll"
fi
[[ $APP_DLL == /* && -f $APP_DLL ]] || { echo 'Build the Release application or set absolute APP_DLL before running backup tests.' >&2; exit 1; }
export APP_DLL
# Tests use only disposable local repositories and literal test credentials.
# Do not allow a caller's password command/profile/configuration into raw tools.
for name in $(compgen -e); do
  case "$name" in RESTIC_*|RCLONE_*|AWS_*) unset "$name" ;; esac
done
bash -n "$here/backup-helper" "$here/test.sh" "$here/tests/"*.sh "$here/tests/dotnet-double"
if command -v shellcheck >/dev/null; then
  shellcheck "$here/backup-helper" "$here/test.sh" "$here/tests/"*.sh "$here/tests/dotnet-double"
fi
bash "$here/tests/failures.sh"
bash "$here/tests/remote-contract.sh"
bash "$here/tests/integration.sh"
