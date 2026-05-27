#!/bin/bash
# Plan 0.8.1 — e2e: spin up dev stack via compose, wait, run Playwright.
#
# Exit codes:
#   0 = all Playwright tests pass
#   1 = Playwright test failures
#   2 = infrastructure error (stack never ready, etc.)

set -e

RESULTS=/results/e2e
mkdir -p "$RESULTS"

cd /workspace

# Ensure `docker compose` (V2 plugin) is available. Dockerfile.e2e installs docker.io
# but not the compose plugin — install it at runtime if absent (ephemeral container FS).
if ! docker compose version >/dev/null 2>&1; then
    echo "docker compose plugin not found — installing via Docker's official apt repo"
    apt-get update -qq >/dev/null 2>&1
    apt-get install -y --no-install-recommends curl ca-certificates >/dev/null 2>&1
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
    chmod a+r /etc/apt/keyrings/docker.asc
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu \
        $(. /etc/os-release && echo "$UBUNTU_CODENAME") stable" \
        > /etc/apt/sources.list.d/docker.list
    apt-get update -qq >/dev/null 2>&1
    apt-get install -y --no-install-recommends docker-compose-plugin >/dev/null 2>&1
    echo "docker compose installed: $(docker compose version)"
fi

# High ports to avoid clash with co-tenant services on the runner host.
export API_PORT=15080
export WEB_PORT=15173
export POSTGRES_PORT=15432
export MAILHOG_SMTP_PORT=11025
export MAILHOG_UI_PORT=18025

# dev stack: base compose.yml merged with dev overrides + e2e DinD path overlay.
# Both base files are required — dev.yml is a partial override that extends base.yml.
# The overlay translates the dev stack's bind-mount paths from compose-CLI-relative
# (which would resolve to /workspace inside THIS container, invisible to the host
# docker daemon) to absolute HOST paths (via $HOST_REPO_ROOT).
#
# Without HOST_REPO_ROOT, the host docker daemon mounts an empty directory into
# the web container's /repo, pnpm install fails with ERR_PNPM_NO_PKG_MANIFEST,
# and web1 restarts forever. Fail-fast here so the diagnosis is obvious.
if [ -z "$HOST_REPO_ROOT" ]; then
    echo "[e2e.sh] FATAL: HOST_REPO_ROOT not set. The docker-compose.test.yml e2e-tests"
    echo "[e2e.sh]        service must export this from the user's \$PWD at compose-run"
    echo "[e2e.sh]        time. Without it the dev-stack bind mounts can't be resolved"
    echo "[e2e.sh]        to host-side paths and the web container restart-loops on a"
    echo "[e2e.sh]        missing package.json."
    exit 2
fi

COMPOSE_BASE="deploy/compose/docker-compose.yml"
COMPOSE_DEV="deploy/compose/docker-compose.dev.yml"
COMPOSE_E2E_OVERLAY="deploy/compose/docker-compose.e2e-overlay.yml"

# The API requires non-empty JWT secret + encryption key even in dev mode.
# Provide 32-zero-byte Base64 placeholders — valid shape, never used in production.
export DWBHUB_JWT_SECRET="${DWBHUB_JWT_SECRET:-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=}"
export DWBHUB_ENCRYPTION_KEY="${DWBHUB_ENCRYPTION_KEY:-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=}"

# Dockerfile.api uses `ARG TARGETARCH` for `dotnet restore/publish --arch $TARGETARCH`.
# When compose builds without BuildKit, the ARG is empty unless we export TARGETARCH.
# Derive the .NET arch convention from the runner's uname.
export TARGETARCH="${TARGETARCH:-$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/;s/armv7l/arm/')}"

cleanup() {
    echo "=== e2e: cleanup ==="
    docker compose -f "$COMPOSE_BASE" -f "$COMPOSE_DEV" -f "$COMPOSE_E2E_OVERLAY" down -v 2>&1 | tee -a "$RESULTS/teardown.log" || true
}
trap cleanup EXIT

echo "=== e2e: pre-build api image (with explicit TARGETARCH build-arg) ==="
# docker compose up --build passes no --build-arg for ARGs without a default;
# Dockerfile.api requires TARGETARCH for `dotnet restore/publish --arch`. Pre-build
# the image with the correct arg, then start the stack with --no-build to reuse it.
# `--progress=plain` forces BuildKit to stream each step live. Dockerfile.e2e
# ships docker-ce + docker-buildx-plugin so this flag is supported.
export BUILDKIT_PROGRESS=plain
docker build --progress=plain \
    -f deploy/docker/Dockerfile.api \
    --build-arg "TARGETARCH=$TARGETARCH" \
    -t dwbhub-api:local \
    . 2>&1 | tee "$RESULTS/build-api.log"

echo "=== e2e: docker compose up -d (dev stack) ==="
docker compose -f "$COMPOSE_BASE" -f "$COMPOSE_DEV" -f "$COMPOSE_E2E_OVERLAY" up -d --no-build 2>&1 | tee "$RESULTS/up.log"

echo "=== e2e: wait for API health (up to 90s) ==="
# The api service has depends_on: postgres (healthcheck) so startup takes ~30-40s.
# Health endpoint is GET /api/health (returns {"status":"ok",...}).
for i in $(seq 1 90); do
    if curl -sf "http://host.docker.internal:${API_PORT}/api/health" >/dev/null 2>&1; then
        echo "API ready after ${i}s"; break
    fi
    sleep 1
done
curl -sf "http://host.docker.internal:${API_PORT}/api/health" || { echo "API never ready after 90s"; exit 2; }

echo "=== e2e: pnpm install ==="
pnpm install --frozen-lockfile

echo "=== e2e: playwright install (sync browser binaries to installed @playwright/test version) ==="
# The base image (mcr.microsoft.com/playwright:v1.49.1-noble) ships browsers for v1.49.x.
# pnpm-lock.yaml resolves @playwright/test to 1.60.0 — the binary paths change on minor
# bumps. Re-run `playwright install` after pnpm install so the correct executables are
# downloaded into /ms-playwright/ before the tests run.
pnpm --filter dwbhub-web exec playwright install --with-deps chromium firefox

echo "=== e2e: wait for web service (up to 300s) ==="
# The dev web server container runs `npm install -g pnpm && pnpm install` before
# starting Vite. First-run startup can take 60-180s depending on host I/O speed
# (npm + pnpm cold caches). 300s leaves margin for slower CI runners.
WEB_READY=0
for i in $(seq 1 300); do
    if curl -sf "http://host.docker.internal:${WEB_PORT}/" >/dev/null 2>&1; then
        echo "Web ready after ${i}s"
        WEB_READY=1
        break
    fi
    [ $((i % 30)) -eq 0 ] && echo "  still waiting for web... ${i}s elapsed"
    sleep 1
done
if [ "$WEB_READY" = "0" ]; then
    echo "Web service never ready after 300s — dumping compose logs"
    docker compose -f "$COMPOSE_BASE" -f "$COMPOSE_DEV" -f "$COMPOSE_E2E_OVERLAY" logs web 2>&1 | tail -30 || true
    exit 2
fi

echo "=== e2e: starting live dev-stack log tail (api / web / postgres / mailhog) ==="
# Tail dev-stack containers in the background so operators see api/web/postgres
# output live during the Playwright run (instead of only after the run via the
# post-run scan below). Each line is prefixed with the service name by `docker
# compose logs`, so `api  | [INF] ...`, `web  | vite v...`, etc. The tail is
# killed in cleanup() on EXIT so it never outlives the job.
# Service names match docker-compose.yml: postgres, mailhog (legacy name —
# axllent/mailpit image but service is still called mailhog), api, web.
docker compose -f "$COMPOSE_BASE" -f "$COMPOSE_DEV" -f "$COMPOSE_E2E_OVERLAY" \
    logs --follow --no-color --timestamps --tail=0 api web postgres mailhog 2>&1 \
    | sed 's/^/[devstack] /' &
DEVSTACK_TAIL_PID=$!
# Re-define cleanup() to ALSO stop the live tail before tearing down the stack.
# (Replaces the original cleanup() defined above; same `trap cleanup EXIT`
# registration still applies — bash resolves the function name at trap-fire time.)
cleanup() {
    # `|| true` guards: trap fires on EXIT regardless of set -e state, but the
    # commands inside still need explicit guards or they themselves will leak
    # non-zero exits up the chain.
    if [ -n "$DEVSTACK_TAIL_PID" ]; then
        kill "$DEVSTACK_TAIL_PID" 2>/dev/null || true
        wait "$DEVSTACK_TAIL_PID" 2>/dev/null || true
    fi
    echo "=== e2e: cleanup ==="
    docker compose -f "$COMPOSE_BASE" -f "$COMPOSE_DEV" -f "$COMPOSE_E2E_OVERLAY" down -v 2>&1 | tee -a "$RESULTS/teardown.log" || true
}

echo "=== e2e: playwright test ==="
# Two reporters in one invocation:
#   - junit emits an XML file dorny/test-reporter + EnricoMi can consume for
#     inline failed-test annotations and PR-comment counts (Plan 0.8.3 follow-up)
#   - json stays for the existing artefact archive path
# Playwright's CLI takes --reporter as a comma-separated list and the
# corresponding output paths via PLAYWRIGHT_JUNIT_OUTPUT_NAME + the json
# reporter's --output=… argument.
PLAYWRIGHT_EXIT=0
E2E_BASE_URL="http://host.docker.internal:${WEB_PORT}" \
PLAYWRIGHT_JUNIT_OUTPUT_NAME="$RESULTS/playwright.xml" \
    pnpm --filter dwbhub-web exec playwright test \
        --reporter=junit,json \
        --output="$RESULTS/traces" \
        > "$RESULTS/playwright.json" || PLAYWRIGHT_EXIT=$?

# Always print a human-readable failure summary so Docker run output shows
# which tests failed (playwright.json lives inside the container and is not
# accessible after cleanup). Uses python3 (included in playwright base image).
if [ "$PLAYWRIGHT_EXIT" -ne 0 ]; then
    echo "[e2e] Playwright exited with code $PLAYWRIGHT_EXIT — failing tests:"
    python3 - <<'PYEOF'
import json, sys
try:
    data = json.load(open('/results/e2e/playwright.json'))
    def walk(suite, prefix=''):
        title = suite.get('title', '')
        full = (prefix + ' > ' + title).strip(' >')
        for spec in suite.get('specs', []):
            for test in spec.get('tests', []):
                status = test.get('status', '')
                if status in ('unexpected', 'failed', 'timedOut'):
                    proj = test.get('projectName', '?')
                    print(f'  FAIL [{proj}] {full} > {spec.get("title","?")}')
                    for r in test.get('results', []):
                        err = r.get('error', {})
                        msg = err.get('message', '')
                        if msg:
                            print(f'       {msg[:400]}')
        for child in suite.get('suites', []):
            walk(child, full)
    for s in data.get('suites', []):
        walk(s)
except Exception as e:
    print(f'Could not parse playwright.json: {e}')
PYEOF
fi

# Propagate Playwright's exit code so the job fails when tests fail.
exit $PLAYWRIGHT_EXIT

# Stop the live dev-stack tail now that Playwright has finished.
# `|| true` is essential: `set -e` would otherwise abort here because
#   * kill returns 1 if the PID already exited (race with docker compose down)
#   * wait returns 128+SIGTERM (=143) when the bg process was killed by us
# Either of these would mask a successful Playwright run with an unrelated
# cleanup-exit-code (observed: e2e exit=1 while all 48 tests passed).
kill "$DEVSTACK_TAIL_PID" 2>/dev/null || true
wait "$DEVSTACK_TAIL_PID" 2>/dev/null || true
DEVSTACK_TAIL_PID=

# Plan 1.0 Task 14.5: capture dev-stack docker logs as ephemeral runner-side
# files + scan for server-side errors that Playwright assertions might not
# catch (e.g. the Plan 1.0 Task 14 duplicate-key UNIQUE violation that lived
# only in api logs for hours before being noticed).
#
# Privacy:
#   - Logs stay on the runner (ephemeral — runner cleans up later).
#   - We NEVER print log CONTENT in CI mode. Counts only.
#   - Locally (no CI env var) we print first 10 lines per category for
#     developer convenience. Test-results dir is overwritten next run.
#   - No artifacts uploaded anywhere.
echo "=== e2e: capturing dev-stack logs ==="
docker compose -f "$COMPOSE_BASE" -f "$COMPOSE_DEV" -f "$COMPOSE_E2E_OVERLAY" \
    logs api      > "$RESULTS/api.log"      2>&1 || true
docker compose -f "$COMPOSE_BASE" -f "$COMPOSE_DEV" -f "$COMPOSE_E2E_OVERLAY" \
    logs postgres > "$RESULTS/postgres.log" 2>&1 || true

# Patterns:
#   API (Serilog): [ERR] / [FATAL] / "Unhandled exception" / *Exception types
# Note: the previous "bot-401-with-fake-mode" allowlist is no longer needed.
# DWBHUB_DISCORD_TEST_MODE=fake-rest now also swaps the bot-connection
# factory for FakeBotConnection, so no real Discord 401 ever fires in e2e.
API_ERRS=$(grep -E '\[ERR\]|\[FATAL\]|Unhandled exception|PostgresException|NpgsqlException' "$RESULTS/api.log" 2>/dev/null \
    | wc -l | tr -d ' ')

# Postgres ERROR/FATAL with hangfire-schema-first-boot allowlisted.
PG_ERRS=$(grep -E 'ERROR:|FATAL:' "$RESULTS/postgres.log" 2>/dev/null \
    | grep -vE 'hangfire\.schema.*does not exist|"hangfire"\."schema"' \
    | wc -l | tr -d ' ')

if [ "$API_ERRS" -gt 0 ] || [ "$PG_ERRS" -gt 0 ]; then
    echo "[e2e] SERVER-SIDE ERRORS DETECTED (api=$API_ERRS pg=$PG_ERRS)"

    # Local-only convenience output. In CI (env CI or GITHUB_ACTIONS set)
    # we print NO content to keep job logs clean of any sensitive data.
    if [ -z "$CI" ] && [ -z "$GITHUB_ACTIONS" ]; then
        echo "--- api: first 10 matching lines ---"
        grep -E '\[ERR\]|\[FATAL\]|Unhandled exception|PostgresException|NpgsqlException' "$RESULTS/api.log" 2>/dev/null \
            | grep -vE 'Bot disconnected.*401: Unauthorized' \
            | head -10
        echo "--- postgres: first 10 matching lines ---"
        grep -E 'ERROR:|FATAL:' "$RESULTS/postgres.log" 2>/dev/null \
            | grep -vE 'hangfire\.schema.*does not exist|"hangfire"\."schema"' \
            | head -10
    fi

    # Sentinel — entrypoint.sh upgrades exit 0 → 3 when this file exists.
    # Naming the marker conveys the new exit-code convention without needing
    # the job script itself to know how to exit 3.
    touch "$RESULTS/.scan-exit3"
fi

echo "=== e2e.sh: complete ==="
