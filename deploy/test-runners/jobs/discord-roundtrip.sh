#!/bin/sh
# Discord round-trip job.
# Runs xUnit tests filtered by Category=DiscordRoundTrip against the real Discord
# API via the full ASP.NET production stack (DI + AES encryption + BotConnectionManager
# + DiscordRestChannelClient). This closes the gap between:
#   - Integration tests (app stack + fake Discord)
#   - Discord-live tests (real Discord, no app stack)
#
# Exit codes:
#   0 = tests passed OR secret file absent / empty vars (SKIPPED path — CI parity)
#   1 = tests failed
#   2 = infrastructure error (token leaked into test-result artifacts)
#
# Secret mount: /run/secrets/discord_token
#   Expected contents (three lines, no quotes):
#     DISCORD_DEV_BOT_TOKEN=<value>
#     DISCORD_DEV_GUILD_ID=<value>
#     DISCORD_DEV_CHANNEL_ID=<value>   # REQUIRED for round-trip tests
#
# SECURITY:
#   * The secret file is NEVER sourced — only specific keys are extracted via awk.
#   * The token value is NEVER printed; only its character-length is logged.
#   * Any literal occurrence of the token in dotnet test output is redacted with
#     <REDACTED-TOKEN> by a sed pipe before it reaches stdout.
#   * After the test run, a self-check greps /results/discord-roundtrip/ for the
#     first 12 chars of the token AND the full guild-id snowflake; either
#     appearing is a leak — exit 2.
#   * Extracted values are trimmed of any trailing \r so that CRLF-corrupted
#     secret files do not slip a stray byte into Discord.Net's connection string.

set -e

SECRET_FILE="/run/secrets/discord_token"
RESULTS=/results/discord-roundtrip
mkdir -p "$RESULTS"

cd /workspace

# ---------------------------------------------------------------------------
# 1. Parse secret file — strict awk extraction, never source the file
# ---------------------------------------------------------------------------
if [ ! -f "$SECRET_FILE" ]; then
    echo "[discord-roundtrip] SKIPPED: secret file not found at ${SECRET_FILE}"
    echo "[discord-roundtrip] To enable: supply discord_token secret (deploy/compose/discord.dev.env)"
    exit 0
fi

BOT_TOKEN=$(awk -F= '/^DISCORD_DEV_BOT_TOKEN=/{print substr($0,index($0,"=")+1)}' "$SECRET_FILE" | tr -d '\r')
GUILD_ID=$(awk -F= '/^DISCORD_DEV_GUILD_ID=/{print substr($0,index($0,"=")+1)}' "$SECRET_FILE" | tr -d '\r')
CHANNEL_ID=$(awk -F= '/^DISCORD_DEV_CHANNEL_ID=/{print substr($0,index($0,"=")+1)}' "$SECRET_FILE" | tr -d '\r')

if [ -z "$BOT_TOKEN" ] || [ -z "$GUILD_ID" ] || [ -z "$CHANNEL_ID" ]; then
    echo "[discord-roundtrip] SKIPPED: DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID must all be set for round-trip tests"
    echo "[discord-roundtrip] Ensure the file contains all three keys on separate lines."
    exit 0
fi

# ---------------------------------------------------------------------------
# 2. Log meta-info (length only — per project rule, the entire discord.dev.env
#    contents are credential-grade; treat all three fields as opaque).
# ---------------------------------------------------------------------------
TOKEN_LEN=$(printf '%s' "$BOT_TOKEN" | wc -c | tr -d ' ')
GUILD_LEN=$(printf '%s' "$GUILD_ID" | wc -c | tr -d ' ')
CHANNEL_LEN=$(printf '%s' "$CHANNEL_ID" | wc -c | tr -d ' ')
echo "[discord-roundtrip] secret-file ok: token_len=${TOKEN_LEN} guild_id_len=${GUILD_LEN} channel_id_len=${CHANNEL_LEN}"

# ---------------------------------------------------------------------------
# 3. Export env vars for the dotnet child process
# ---------------------------------------------------------------------------
export DISCORD_DEV_BOT_TOKEN="$BOT_TOKEN"
export DISCORD_DEV_GUILD_ID="$GUILD_ID"
export DISCORD_DEV_CHANNEL_ID="$CHANNEL_ID"

# Testcontainers settings — same as backend-tests / discord-live-tests.
export TESTCONTAINERS_HOST_OVERRIDE="${TESTCONTAINERS_HOST_OVERRIDE:-host.docker.internal}"
export TESTCONTAINERS_RYUK_DISABLED="true"

# ---------------------------------------------------------------------------
# 4. Run dotnet test with token-redaction.
# ---------------------------------------------------------------------------
SED_SCRIPT="s/${BOT_TOKEN}/<REDACTED-TOKEN>/g"
RAW_OUT="$RESULTS/.dotnet-raw.tmp"

set +e
dotnet test api/tests/DwbHub.Tests.Integration/DwbHub.Tests.Integration.csproj \
    --filter "Category=DiscordRoundTrip" \
    --logger "trx;LogFileName=discord-roundtrip.trx" \
    --results-directory /results/discord-roundtrip \
    --nologo \
    -v minimal \
    > "$RAW_OUT" 2>&1
DOTNET_EXIT=$?
set -e

# Redact token from captured output, then print.
sed -E "$SED_SCRIPT" "$RAW_OUT"
rm -f "$RAW_OUT"

echo "[discord-roundtrip] dotnet test exit: ${DOTNET_EXIT}"

# ---------------------------------------------------------------------------
# 5. Credential-leak self-check.
# ---------------------------------------------------------------------------
TOKEN_PREFIX=$(printf '%s' "$BOT_TOKEN" | cut -c1-12)
if grep -rqF "$TOKEN_PREFIX" "$RESULTS/" 2>/dev/null; then
    echo "[discord-roundtrip] FATAL: token prefix found in test-results — leak detected"
    exit 2
fi
if grep -rqF "$GUILD_ID" "$RESULTS/" 2>/dev/null; then
    echo "[discord-roundtrip] FATAL: guild_id found in test-results — leak detected"
    exit 2
fi
if grep -rqF "$CHANNEL_ID" "$RESULTS/" 2>/dev/null; then
    echo "[discord-roundtrip] FATAL: channel_id found in test-results — leak detected"
    exit 2
fi

echo "[discord-roundtrip] credential-leak check passed (token + guild_id + channel_id)"
exit "$DOTNET_EXIT"
