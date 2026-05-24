#!/bin/sh
# Plan 0.8.1 — openapi-drift job. Spec dump + diff via existing pwsh helpers.
#
# Exit codes:
#   0 = all pass
#   1 = any check fails
#   2 = infrastructure error

set -e

RESULTS=/results/openapi
mkdir -p "$RESULTS"

cd /workspace

# Run a command, capture output to a temp file, print it, save to a named log,
# preserve the command's exit code (POSIX-portable; tee always exits 0 in dash/busybox).
run_step() {
    LOG_NAME="$1"
    shift
    TMP="$RESULTS/.${LOG_NAME}.tmp"
    set +e
    "$@" > "$TMP" 2>&1
    EXIT=$?
    set -e
    cat "$TMP" | tee "$RESULTS/${LOG_NAME}.log"
    rm -f "$TMP"
    if [ "$EXIT" -ne 0 ]; then
        echo "=== openapi.sh: ${LOG_NAME} FAILED with exit ${EXIT} ==="
        exit "$EXIT"
    fi
}

echo "=== openapi.sh: dotnet tool restore (installs swagger CLI) ==="
dotnet tool restore

echo "=== openapi.sh: verify-openapi.ps1 (boots temp pg+mailpit, dumps spec, diffs) ==="
run_step verify-openapi pwsh -NoLogo -NonInteractive -File tools/verify-openapi.ps1

echo "=== openapi.sh: gen-openapi-client.ps1 -Check (TS client drift) ==="
# gen-openapi-client.ps1 requires pnpm. Dockerfile.backend ships .NET+pwsh but not Node.
# Install Node 22 + pnpm at runtime if absent (one-time; uses ephemeral container FS).
if ! command -v pnpm >/dev/null 2>&1; then
    echo "pnpm not found — installing Node 22 + pnpm@11.1.3 at runtime"
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash - >/dev/null 2>&1
    apt-get install -y --no-install-recommends nodejs >/dev/null 2>&1
    npm install -g pnpm@11.1.3 >/dev/null 2>&1
    echo "pnpm installed: $(pnpm --version)"
fi
run_step gen-client pwsh -NoLogo -NonInteractive -File tools/gen-openapi-client.ps1 -Check

echo "=== openapi.sh: complete ==="
