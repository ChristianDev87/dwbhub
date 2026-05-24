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

echo "=== frontend.sh: pnpm lint (eslint --format=json -> github annotations) ==="
# ESLint 9 has no built-in github formatter; emit ::error directives by
# converting JSON output via a node one-liner so GitHub Actions can annotate
# PRs inline.
ESLINT_JSON="$RESULTS/.eslint.json"
ESLINT_LOG="$RESULTS/eslint.log"
set +e
pnpm --filter dwbhub-web exec eslint . --format=json --output-file="$ESLINT_JSON"
ESLINT_EXIT=$?
set -e
# Convert JSON to ::error/::warning directives and tee to log file
node - "$ESLINT_JSON" <<'NODE_EOF' | tee "$ESLINT_LOG"
const fs = require('fs');
// When invoked as `node - <path>`, argv[0]=node argv[1]='-' argv[2]=path
const path = process.argv[2];
if (!path || !fs.existsSync(path)) process.exit(0);
const data = JSON.parse(fs.readFileSync(path, 'utf8'));
for (const file of data) {
    const rel = file.filePath.replace(/^\/workspace\//, '');
    for (const msg of file.messages) {
        const sev = msg.severity === 2 ? 'error' : 'warning';
        const loc = `file=${rel},line=${msg.line},col=${msg.column},endLine=${msg.endLine||msg.line},endColumn=${msg.endColumn||msg.column}`;
        console.log(`::${sev} ${loc}::${msg.message} (${msg.ruleId||'eslint'})`);
    }
}
NODE_EOF
rm -f "$ESLINT_JSON"
if [ "$ESLINT_EXIT" -ne 0 ]; then
    echo "=== frontend.sh: eslint FAILED with exit ${ESLINT_EXIT} ==="
    exit "$ESLINT_EXIT"
fi

echo "=== frontend.sh: prettier --check ==="
run_step prettier pnpm --filter dwbhub-web format:check

echo "=== frontend.sh: tsc --noEmit ==="
run_step tsc pnpm --filter dwbhub-web typecheck

echo "=== frontend.sh: vitest --run --reporter=junit,json ==="
# JUnit for dorny/test-reporter CI annotations; JSON preserved for artefacts.
# Vitest 3 dot-notation: --outputFile.<reporter>=<path>
pnpm --filter dwbhub-web exec vitest run \
    --reporter=junit --reporter=json \
    "--outputFile.junit=$RESULTS/vitest.xml" \
    "--outputFile.json=$RESULTS/vitest.json"

echo "=== frontend.sh: vite build (production bundle) ==="
pnpm --filter dwbhub-web build

echo "=== frontend.sh: complete ==="
