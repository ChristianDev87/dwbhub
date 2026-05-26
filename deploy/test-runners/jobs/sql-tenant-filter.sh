#!/bin/bash
# Plan 0.8.1 — sql-tenant-filter job.
# Runs check-tenant-filter.ps1 lint then Pester tests via pwsh.
#
# Exit codes:
#   0 = all pass
#   1 = any check fails
#   2 = infrastructure error

set -eo pipefail

RESULTS=/results/sql-tenant-filter
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
        echo "=== sql-tenant-filter.sh: ${LOG_NAME} FAILED with exit ${EXIT} ==="
        exit "$EXIT"
    fi
}

# NOTE on live-streaming: Pester 5's hierarchical renderer batches output
# per Describe/Context block and flushes only when each block finishes.
# Neither [Console]::Out AutoFlush nor `script(1)` PTY allocation defeats
# this — the buffering is inside Pester's own Format module, not in the
# shell/.NET stdio layer. We accept this for sql-tenant-filter because
# the whole job runs in ~20s (low operator impact). entrypoint.sh still
# emits live "=== step ===" banners between steps so progress is
# observable at the granularity that matters.

echo "=== sql-tenant-filter.sh: check-tenant-filter.ps1 ==="
run_step check-lint pwsh -NoLogo -NonInteractive -File tools/check-tenant-filter.ps1

echo "=== sql-tenant-filter.sh: Pester tests ==="
# Pester via pwsh: run with -Output Detailed for human-readable progress to stderr,
# then write the PassThru JSON to a dedicated file via Set-Content (bypasses stdout).
# This keeps the two streams separate: verbose progress -> stderr, JSON -> file.
pwsh -NonInteractive -Command "
\$PSStyle.OutputRendering = 'PlainText'
\$r = Invoke-Pester -Path tools/tests/ -Output Detailed -PassThru
\$r | ConvertTo-Json -Depth 5 | Set-Content -Path '$RESULTS/pester.json' -Encoding UTF8
" 2>&1
FAILED=$(pwsh -NonInteractive -Command "(Get-Content '$RESULTS/pester.json' -Raw | ConvertFrom-Json).FailedCount")
if [ "$FAILED" != "0" ]; then
    echo "Pester reported $FAILED failed tests"
    exit 1
fi

echo "=== sql-tenant-filter.sh: complete ==="
