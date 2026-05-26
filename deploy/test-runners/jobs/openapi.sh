#!/bin/bash
# Plan 0.8.1 — openapi-drift job. Spec dump + diff via existing pwsh helpers.
#
# Exit codes:
#   0 = all pass
#   1 = any check fails
#   2 = infrastructure error

set -eo pipefail

RESULTS=/results/openapi
mkdir -p "$RESULTS"

cd /workspace

# Run a command, LIVE-stream combined stdout+stderr to a named log AND to our
# own stdout (so entrypoint.sh's tee sees it incrementally). Uses bash
# PIPESTATUS to preserve the wrapped command's exit code through the pipe.
# (The previous POSIX-sh implementation buffered all output to a tmp file
# and only flushed on step exit — invisible until completion.)
run_step() {
    LOG_NAME="$1"
    shift
    set +e
    "$@" 2>&1 | tee "$RESULTS/${LOG_NAME}.log"
    EXIT=${PIPESTATUS[0]}
    set -e
    if [ "$EXIT" -ne 0 ]; then
        echo "=== openapi.sh: ${LOG_NAME} FAILED with exit ${EXIT} ==="
        exit "$EXIT"
    fi
}

# NOTE on live-streaming: pwsh tools (verify-openapi.ps1, gen-openapi-client.ps1)
# can batch their own output internally. We tried Console.Out AutoFlush + script(1)
# PTY allocation — neither defeats Pester-style internal batching when present.
# entrypoint.sh still emits live "=== step ===" banners between steps so progress
# is observable at step granularity; full output flushes when each step completes.

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
