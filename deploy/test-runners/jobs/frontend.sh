#!/bin/sh
# Plan 0.8.1 — frontend job: lint + typecheck + vitest + production build.
# Mirrors the .github/workflows/ci.yml `frontend-lint-typecheck-test` job.
#
# Exit codes:
#   0 = all pass
#   1 = any check fails
#   2 = infrastructure error

set -e

RESULTS=/results/frontend
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
        echo "=== frontend.sh: ${LOG_NAME} FAILED with exit ${EXIT} ==="
        exit "$EXIT"
    fi
}

echo "=== frontend.sh: pnpm install --frozen-lockfile ==="
pnpm install --frozen-lockfile

echo "=== frontend.sh: pnpm lint (eslint) ==="
run_step eslint pnpm --filter dwbhub-web lint

echo "=== frontend.sh: prettier --check ==="
run_step prettier pnpm --filter dwbhub-web format:check

echo "=== frontend.sh: tsc --noEmit ==="
run_step tsc pnpm --filter dwbhub-web typecheck

echo "=== frontend.sh: vitest --run --reporter=json ==="
# Vitest's --reporter=json with --outputFile bypasses stdout; no tee needed.
pnpm --filter dwbhub-web exec vitest run --reporter=json --outputFile="$RESULTS/vitest.json"

echo "=== frontend.sh: vite build (production bundle) ==="
pnpm --filter dwbhub-web build

echo "=== frontend.sh: complete ==="
