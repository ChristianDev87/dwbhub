#!/bin/sh
# Generic dispatcher for all test-runner jobs.
# Usage: entrypoint.sh <job-name> [extra-args...]
# - Reads the job name from $1.
# - Runs /opt/test-runners/jobs/<job>.sh inside the container.
# - Captures combined stdout+stderr to /results/<job>/run.log via tee.
# - Final line of run.log is always:
#     [entrypoint] job=<name> exit=<N> duration=<S>s
#   so callers can `tail -1 /results/<job>/run.log` to learn the result.
#
# Exit-code convention (spec 0.8.1 §5):
#   0 = all assertions passed
#   1 = at least one test failed (legitimate test failure)
#   2 = infrastructure failure (missing job script, missing required env, etc.)

set -e

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
    # Run the job; capture combined output to the log via tee.
    # PIPESTATUS preserves the script's exit code despite the tee in the pipeline.
    set +e
    "$JOB_SCRIPT" "$@" 2>&1 | tee -a "$LOG"
    EXIT=${PIPESTATUS:-$?}
    set -e
fi

DURATION=$(( $(date +%s) - START_EPOCH ))
echo "[entrypoint] job=${JOB} exit=${EXIT} duration=${DURATION}s" | tee -a "$LOG"
exit $EXIT
