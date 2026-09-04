#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: ./scripts/init.sh <ApplicationName> <BoundedContextName>" >&2
  echo "Names must be PascalCase .NET identifiers, for example: Workshop Enrollment" >&2
}

if [[ $# -ne 2 ]]; then
  usage
  exit 64
fi

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"

if git remote get-url origin 2>/dev/null | grep -Eq '(^|[:/])scowalt/fsharp-ddd-starter(\.git)?$'; then
  echo "Refusing to initialize the canonical starter repository. Use GitHub's 'Use this template' action first." >&2
  exit 65
fi

dotnet fsi scripts/init.fsx -- "$1" "$2"
dotnet tool restore
dotnet fantomas .

rm -f docs/template-usage.md .template-manifest scripts/init.fsx
rm -f -- "$0"

solution="$1.slnx"
dotnet restore "$solution"

if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  git add -A
fi

cat <<EOF
Initialized $1 with the $2 bounded context.

The template-only files were removed and all generated changes were staged.
Review them, run ./scripts/validate.sh, and commit the initialization as the generated application's history boundary.
EOF
