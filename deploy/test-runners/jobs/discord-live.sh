#!/bin/sh
# Plan 0.8.1 — discord-live job.
# Runs xUnit tests filtered by Category=DiscordLive against the real Discord gateway.
#
# Exit codes:
#   0 = tests passed OR secret file absent / empty vars (SKIPPED path — CI parity)
#   1 = tests failed
#   2 = infrastructure error (token leaked into test-result artifacts)
#
# Secret mount: /run/secrets/discord_token
#   Expected contents (two lines, no quotes):
#     DISCORD_DEV_BOT_TOKEN=<value>
#     DISCORD_DEV_GUILD_ID=<value>
#
# SECURITY:
#   * The secret file is NEVER sourced — only two specific keys are extracted via awk.
#   * The token value is NEVER printed; only its character-length is logged.
#   * Any literal occurrence of the token in dotnet test output is redacted with
#     <REDACTED-TOKEN> by a sed pipe before it reaches stdout.
#   * After the test run, a self-check greps /results/discord-live/ for the first
#     12 chars of the token; any match is a leak — exit 2.

set -e

SECRET_FILE="/run/secrets/discord_token"
RESULTS=/results/discord-live
mkdir -p "$RESULTS"

cd /workspace

# ---------------------------------------------------------------------------
# 1. Parse secret file — strict awk extraction, never source the file
# ---------------------------------------------------------------------------
if [ ! -f "$SECRET_FILE" ]; then
    echo "[discord-live] SKIPPED: secret file not found at ${SECRET_FILE}"
    echo "[discord-live] To enable: supply discord_token secret (deploy/compose/discord.dev.env)"
    exit 0
fi

BOT_TOKEN=$(awk -F= '/^DISCORD_DEV_BOT_TOKEN=/{print substr($0,index($0,"=")+1)}' "$SECRET_FILE")
GUILD_ID=$(awk -F= '/^DISCORD_DEV_GUILD_ID=/{print substr($0,index($0,"=")+1)}' "$SECRET_FILE")

if [ -z "$BOT_TOKEN" ] || [ -z "$GUILD_ID" ]; then
    echo "[discord-live] SKIPPED: DISCORD_DEV_BOT_TOKEN or DISCORD_DEV_GUILD_ID missing or empty in ${SECRET_FILE}"
    echo "[discord-live] Ensure the file contains both keys on separate lines."
    exit 0
fi

# ---------------------------------------------------------------------------
# 2. Log meta-info (length only for token; guild ID is a public snowflake)
# ---------------------------------------------------------------------------
TOKEN_LEN=$(printf '%s' "$BOT_TOKEN" | wc -c | tr -d ' ')
echo "[discord-live] token length: ${TOKEN_LEN}, guild_id: ${GUILD_ID}"

# ---------------------------------------------------------------------------
# 3. Export env vars for the dotnet child process
# ---------------------------------------------------------------------------
export DISCORD_DEV_BOT_TOKEN="$BOT_TOKEN"
export DISCORD_DEV_GUILD_ID="$GUILD_ID"

# ---------------------------------------------------------------------------
# 4. Run dotnet test with token-redaction.
#    Strategy: capture raw output to a temp file (preserves dotnet exit code
#    via the set +e / capture-exit pattern), then cat through sed to redact.
#    $BOT_TOKEN is substituted by the shell before sed sees the script string,
#    so the literal token value is replaced before any output is printed.
# ---------------------------------------------------------------------------
SED_SCRIPT="s/${BOT_TOKEN}/<REDACTED-TOKEN>/g"
RAW_OUT="$RESULTS/.dotnet-raw.tmp"

set +e
dotnet test api/tests/DwbHub.Tests.Integration/DwbHub.Tests.Integration.csproj \
    --filter "Category=DiscordLive" \
    --logger "trx;LogFileName=discord-live.trx" \
    --results-directory /results/discord-live \
    --nologo \
    -v minimal \
    > "$RAW_OUT" 2>&1
DOTNET_EXIT=$?
set -e

# Redact token from captured output, then print (entrypoint tees to run.log).
sed -E "$SED_SCRIPT" "$RAW_OUT"
rm -f "$RAW_OUT"

echo "[discord-live] dotnet test exit: ${DOTNET_EXIT}"

# ---------------------------------------------------------------------------
# 5. Token-leak self-check: grep result artifacts for the first 12 chars.
#    The .trx file is XML and may contain env-var names but not values;
#    this check guards against any unexpected serialisation path.
# ---------------------------------------------------------------------------
TOKEN_PREFIX=$(printf '%s' "$BOT_TOKEN" | cut -c1-12)
if grep -rqF "$TOKEN_PREFIX" "$RESULTS/" 2>/dev/null; then
    echo "[discord-live] FATAL: token prefix found in test-results — leak detected"
    exit 2
fi

echo "[discord-live] token-leak check passed"
exit "$DOTNET_EXIT"
