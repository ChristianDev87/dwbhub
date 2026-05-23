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

echo "=== frontend.sh: pnpm install --frozen-lockfile ==="
pnpm install --frozen-lockfile

echo "=== frontend.sh: pnpm lint (eslint) ==="
pnpm --filter dwbhub-web lint 2>&1 | tee "$RESULTS/eslint.log"

echo "=== frontend.sh: prettier --check ==="
pnpm --filter dwbhub-web format:check 2>&1 | tee "$RESULTS/prettier.log"

echo "=== frontend.sh: tsc --noEmit ==="
pnpm --filter dwbhub-web typecheck 2>&1 | tee "$RESULTS/tsc.log"

echo "=== frontend.sh: vitest --run --reporter=json ==="
# Vitest's --reporter=json emits to stdout; redirect into a file alongside the human log.
pnpm --filter dwbhub-web exec vitest run --reporter=json --outputFile="$RESULTS/vitest.json"

echo "=== frontend.sh: vite build (production bundle) ==="
pnpm --filter dwbhub-web build

echo "=== frontend.sh: complete ==="
