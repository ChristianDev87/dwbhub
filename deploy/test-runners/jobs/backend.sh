#!/bin/sh
# Plan 0.8.1 — backend job: lint + unit + integration + security tests.
# Mirrors what the existing .github/workflows/ci.yml `backend-lint-and-test` job does,
# but inside the test-runner container.
#
# Exit codes:
#   0 = all tests pass
#   1 = any test fails OR format-check fails OR build fails
#   2 = unexpected infrastructure error (handled by `set -e` + final trap)

set -e

RESULTS=/results/backend
mkdir -p "$RESULTS"

echo "=== backend.sh: dotnet tool restore ==="
cd /workspace
dotnet tool restore

echo "=== backend.sh: dotnet restore (api/DwbHub.sln) ==="
dotnet restore api/DwbHub.sln

echo "=== backend.sh: dotnet format --verify-no-changes ==="
dotnet format api/DwbHub.sln --verify-no-changes --no-restore

echo "=== backend.sh: dotnet build -c Release ==="
dotnet build api/DwbHub.sln --no-restore -c Release

echo "=== backend.sh: Unit tests ==="
dotnet test api/tests/DwbHub.Tests.Unit/DwbHub.Tests.Unit.csproj \
    --no-build -c Release \
    --logger "trx;LogFileName=$RESULTS/unit.trx" \
    --logger "console;verbosity=minimal"

echo "=== backend.sh: Integration tests ==="
dotnet test api/tests/DwbHub.Tests.Integration/DwbHub.Tests.Integration.csproj \
    --no-build -c Release \
    --logger "trx;LogFileName=$RESULTS/integration.trx" \
    --logger "console;verbosity=minimal" \
    --filter "Category!=DiscordLive"

echo "=== backend.sh: Security tests ==="
dotnet test api/tests/DwbHub.Tests.Security/DwbHub.Tests.Security.csproj \
    --no-build -c Release \
    --logger "trx;LogFileName=$RESULTS/security.trx" \
    --logger "console;verbosity=minimal"

echo "=== backend.sh: complete ==="
