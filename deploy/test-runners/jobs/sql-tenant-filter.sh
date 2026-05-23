#!/bin/sh
# Plan 0.8.1 — sql-tenant-filter job.
# Runs check-tenant-filter.ps1 lint then Pester tests via pwsh.
#
# Exit codes:
#   0 = all pass
#   1 = any check fails
#   2 = infrastructure error

set -e

RESULTS=/results/sql-tenant-filter
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
        echo "=== sql-tenant-filter.sh: ${LOG_NAME} FAILED with exit ${EXIT} ==="
        exit "$EXIT"
    fi
}

echo "=== sql-tenant-filter.sh: check-tenant-filter.ps1 ==="
run_step check-lint pwsh tools/check-tenant-filter.ps1

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
