#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

solution=$(find . -maxdepth 1 -name '*.slnx' -print -quit)
if [[ -z "$solution" ]]; then
  echo "No .slnx solution found" >&2
  exit 1
fi

echo "==> Restore pinned .NET tools"
dotnet tool restore

echo "==> Check F# formatting"
dotnet fantomas --check .

echo "==> Check vendored asset integrity"
./scripts/check-assets.sh

echo "==> Reject non-baseline architecture packages"
if rg -n -i '<PackageReference Include="(Equinox|Marten|MediatR|MassTransit|NServiceBus|EventStore|ArchUnitNET)' --glob '*.fsproj' .; then
  echo "A non-baseline event-sourcing, mediator, messaging, or architecture package was found." >&2
  exit 1
fi

if rg -n 'type (Entity|ValueObject|AggregateRoot)(<|\b)' --glob '*.fs' src; then
  echo "A generic DDD base type was found in the baseline." >&2
  exit 1
fi

echo "==> Restore locked dependencies"
dotnet restore "$solution" --locked-mode

echo "==> Build"
dotnet build "$solution" --configuration Release --no-restore

echo "==> Run domain, application, SQLite integration, web, property, and architecture tests"
dotnet test "$solution" --configuration Release --no-build --no-restore

echo "==> Run encrypted local backup/restore and failure tests (no cloud access)"
mise exec -- ./ops/backup/test.sh

echo "==> Validate private R2 infrastructure example (mocked provider, no cloud access)"
mise exec -- tofu -chdir=examples/opentofu/backup-storage fmt -check -recursive
mise exec -- tofu -chdir=examples/opentofu/backup-storage init -backend=false -input=false -lockfile=readonly
mise exec -- tofu -chdir=examples/opentofu/backup-storage validate
mise exec -- tofu -chdir=examples/opentofu/backup-storage test -no-color -filter=tests/privacy.tofutest.hcl
