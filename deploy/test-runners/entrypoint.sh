#!/bin/bash
# Generic dispatcher for all test-runner jobs.
# Usage: entrypoint.sh <job-name> [extra-args...]
# - Reads the job name from $1.
# - Runs /opt/test-runners/jobs/<job>.sh inside the container.
# - LIVE-streams combined stdout+stderr to /results/<job>/run.log via `tee -a`
#   so operators can `tail -f` the log while a long-running job (e.g. e2e)
#   is in progress, instead of waiting for the final flush.
# - Final line of run.log is always:
#     [entrypoint] job=<name> exit=<N> duration=<S>s
#   so callers can `tail -1 /results/<job>/run.log` to learn the result.
#
# Why bash (not sh)?
#   Plan 0.8.1 originally used `sh` + a temp-file buffer to capture the exit
#   code, because `PIPESTATUS` is a bash extension and dash (Debian's POSIX
#   sh) would mask the wrapped script's exit when piped through `tee`. The
#   temp-file pattern worked but BUFFERED all output until the job exited,
#   making long-running jobs invisible until completion (Plan 1.0 lesson).
#   The bash shebang + `PIPESTATUS[0]` + `set -o pipefail` gives both
#   live-streaming AND correct exit-code propagation. All four test-runner
#   images install bash via their apt/apk recipes.
#
# Exit-code convention:
#   0 = all assertions passed AND no server-side errors detected
#   1 = at least one test failed (legitimate test failure)
#   2 = infrastructure failure (missing job script, missing required env, etc.)
#   3 = tests passed BUT server-side errors detected during the run
#       (job-script writes /results/<job>/.scan-exit3 marker; we elevate exit
#       from 0 to 3). This is the "Playwright was green but api/postgres
#       logs contained [ERR]/PostgresException/etc." case — added in Plan 1.0
#       Task 14.5 to catch issues like the duplicate-key UNIQUE violation
#       that lived in api logs for hours during Task 14 development.

set -eo pipefail

JOB="${1:-}"
if [ -z "$JOB" ]; then
    echo "[entrypoint] ERROR: no job name supplied" >&2
    exit 2
fi
shift

RESULTS_DIR="/results/${JOB}"
mkdir -p "$RESULTS_DIR"
LOG="${RESULTS_DIR}/run.log"

START_EPOCH=$(date +%s)
START_ISO=$(date -u +%FT%TZ)
echo "[entrypoint] job=${JOB} start=${START_ISO}" | tee "$LOG"

JOB_SCRIPT="/opt/test-runners/jobs/${JOB}.sh"
if [ ! -x "$JOB_SCRIPT" ]; then
    echo "[entrypoint] ERROR: job script not executable: ${JOB_SCRIPT}" | tee -a "$LOG"
    EXIT=2
else
    # Live-stream: stdout+stderr → tee → run.log (and to container stdout).
    # PIPESTATUS[0] captures the wrapped script's exit code, NOT tee's.
    # pipefail is already set at the top so any pipeline-internal failure
    # surfaces here too.
    set +e
    "$JOB_SCRIPT" "$@" 2>&1 | tee -a "$LOG"
    EXIT=${PIPESTATUS[0]}
    set -e

    # Plan 1.0 Task 14.5: if the job script produced no test failures (EXIT=0)
    # but its server-side error scan flagged issues (sentinel file present),
    # elevate the entrypoint exit to 3. CI treats this as a failure; locally
    # the run.log already contains the human-readable count + first 10 lines.
    # The marker file is created by the job script after its log scan.
    if [ "$EXIT" -eq 0 ] && [ -f "${RESULTS_DIR}/.scan-exit3" ]; then
        EXIT=3
    fi
    rm -f "${RESULTS_DIR}/.scan-exit3"
fi

DURATION=$(( $(date +%s) - START_EPOCH ))
echo "[entrypoint] job=${JOB} exit=${EXIT} duration=${DURATION}s" | tee -a "$LOG"
exit $EXIT
