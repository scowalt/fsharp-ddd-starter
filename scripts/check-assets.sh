#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

if command -v sha256sum >/dev/null 2>&1; then
  sha256sum -c scripts/assets.sha256
elif command -v shasum >/dev/null 2>&1; then
  while read -r expected file; do
    actual=$(shasum -a 256 "$file" | awk '{print $1}')
    if [[ "$actual" != "$expected" ]]; then
      echo "$file: checksum mismatch" >&2
      exit 1
    fi
    echo "$file: OK"
  done < scripts/assets.sha256
else
  echo "sha256sum or shasum is required" >&2
  exit 1
fi
