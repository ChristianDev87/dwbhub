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
#   Expected contents (no quotes):
#     DISCORD_DEV_BOT_TOKEN=<value>          # REQUIRED
#     DISCORD_DEV_GUILD_ID=<value>           # REQUIRED
#     DISCORD_DEV_CHANNEL_ID=<value>         # REQUIRED for round-trip tests
#     DISCORD_DEV_HELPER_BOT_TOKEN=<value>   # OPTIONAL — Phase 2+ tests only
#
# SECURITY:
#   * The secret file is NEVER sourced — only specific keys are extracted via awk.
#   * Token values are NEVER printed; only character-lengths are logged.
#   * Any literal occurrence of either token in dotnet test output is redacted with
#     <REDACTED-TOKEN> / <REDACTED-HELPER-TOKEN> by a sed pipe before reaching stdout.
#   * After the test run, a self-check greps /results/discord-roundtrip/ for the
#     first 12 chars of BOTH tokens AND the full guild-id snowflake; any match
#     is a leak — exit 2. We check both because we don't want either bot's
#     identity appearing in logs or uploaded artefacts.
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
# HELPER_BOT_TOKEN is optional — absent locally or on runners without the Phase 2
# secret. When empty, Phase 2+ tests will Skip.If; existing tests are unaffected.
HELPER_BOT_TOKEN=$(awk -F= '/^DISCORD_DEV_HELPER_BOT_TOKEN=/{print substr($0,index($0,"=")+1)}' "$SECRET_FILE" | tr -d '\r')

if [ -z "$BOT_TOKEN" ] || [ -z "$GUILD_ID" ] || [ -z "$CHANNEL_ID" ]; then
    echo "[discord-roundtrip] SKIPPED: DISCORD_DEV_BOT_TOKEN / DISCORD_DEV_GUILD_ID / DISCORD_DEV_CHANNEL_ID must all be set for round-trip tests"
    echo "[discord-roundtrip] Ensure the file contains all three keys on separate lines."
    exit 0
fi

# ---------------------------------------------------------------------------
# 2. Log meta-info (length only — per project rule, the entire discord.dev.env
#    contents are credential-grade; treat all fields as opaque).
# ---------------------------------------------------------------------------
TOKEN_LEN=$(printf '%s' "$BOT_TOKEN" | wc -c | tr -d ' ')
GUILD_LEN=$(printf '%s' "$GUILD_ID" | wc -c | tr -d ' ')
CHANNEL_LEN=$(printf '%s' "$CHANNEL_ID" | wc -c | tr -d ' ')
HELPER_LEN=$(printf '%s' "$HELPER_BOT_TOKEN" | wc -c | tr -d ' ')
if [ "$HELPER_LEN" -gt 0 ]; then
    echo "[discord-roundtrip] secret-file ok: token_len=${TOKEN_LEN} guild_id_len=${GUILD_LEN} channel_id_len=${CHANNEL_LEN} helper_token_len=${HELPER_LEN}"
else
    echo "[discord-roundtrip] secret-file ok: token_len=${TOKEN_LEN} guild_id_len=${GUILD_LEN} channel_id_len=${CHANNEL_LEN} (no helper token — Phase 2 tests will skip)"
fi

# ---------------------------------------------------------------------------
# 3. Export env vars for the dotnet child process
# ---------------------------------------------------------------------------
export DISCORD_DEV_BOT_TOKEN="$BOT_TOKEN"
export DISCORD_DEV_GUILD_ID="$GUILD_ID"
export DISCORD_DEV_CHANNEL_ID="$CHANNEL_ID"
# Only export helper token when present — keeps the SkippableFact's Skip.If
# check meaningful when running without the Phase 2 helper secret.
if [ -n "$HELPER_BOT_TOKEN" ]; then
    export DISCORD_DEV_HELPER_BOT_TOKEN="$HELPER_BOT_TOKEN"
fi

# Testcontainers settings — same as backend-tests / discord-live-tests.
export TESTCONTAINERS_HOST_OVERRIDE="${TESTCONTAINERS_HOST_OVERRIDE:-host.docker.internal}"
export TESTCONTAINERS_RYUK_DISABLED="true"

# ---------------------------------------------------------------------------
# 4. Run dotnet test with token-redaction.
# ---------------------------------------------------------------------------
# Build a sed script that redacts both tokens in one pass. The helper-token
# redaction step is a no-op when HELPER_BOT_TOKEN is empty (the pattern
# becomes "s//..." which sed treats as a match-nothing substitution).
SED_SCRIPT="s/${BOT_TOKEN}/<REDACTED-TOKEN>/g"
if [ -n "$HELPER_BOT_TOKEN" ]; then
    SED_SCRIPT="${SED_SCRIPT};s/${HELPER_BOT_TOKEN}/<REDACTED-HELPER-TOKEN>/g"
fi
RAW_OUT="$RESULTS/.dotnet-raw.tmp"

DOTNET_EXIT_FILE="$RESULTS/.dotnet-exit.tmp"

set +e
# Pipe through sed for live token-redaction so that step-logs from Console.WriteLine
# appear in the container stdout immediately (not buffered until the end).
# We write the dotnet exit code to a temp file because PIPESTATUS is bash-only.
# tee writes the redacted stream to $RAW_OUT so the post-run leak-check can grep it.
(dotnet test api/tests/DwbHub.Tests.Integration/DwbHub.Tests.Integration.csproj \
    --filter "Category=DiscordRoundTrip" \
    --logger "trx;LogFileName=discord-roundtrip.trx" \
    --logger "console;verbosity=normal" \
    --results-directory /results/discord-roundtrip \
    --nologo \
    -v minimal \
    2>&1; echo $? > "$DOTNET_EXIT_FILE") | sed -E "$SED_SCRIPT" | tee "$RAW_OUT"
set -e

DOTNET_EXIT=$(cat "$DOTNET_EXIT_FILE" 2>/dev/null || echo 1)
rm -f "$DOTNET_EXIT_FILE" "$RAW_OUT"
echo "[discord-roundtrip] dotnet test exit: ${DOTNET_EXIT}"

# ---------------------------------------------------------------------------
# 5. Credential-leak self-check.
#    We grep for the first 12 chars of BOTH bot tokens (Bot A and Bot B) so
#    that neither bot's identity ever ships in CI logs or uploaded artefacts.
#    The helper-token check is conditional: when HELPER_BOT_TOKEN is absent
#    (local run without Phase 2 secret), we simply skip it — idempotent.
# ---------------------------------------------------------------------------
TOKEN_PREFIX=$(printf '%s' "$BOT_TOKEN" | cut -c1-12)
if grep -rqF "$TOKEN_PREFIX" "$RESULTS/" 2>/dev/null; then
    echo "[discord-roundtrip] FATAL: bot-A token prefix found in test-results — leak detected"
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
if [ -n "$HELPER_BOT_TOKEN" ]; then
    HELPER_PREFIX=$(printf '%s' "$HELPER_BOT_TOKEN" | cut -c1-12)
    if grep -rqF "$HELPER_PREFIX" "$RESULTS/" 2>/dev/null; then
        echo "[discord-roundtrip] FATAL: bot-B (helper) token prefix found in test-results — leak detected"
        exit 2
    fi
fi

if [ -n "$HELPER_BOT_TOKEN" ]; then
    echo "[discord-roundtrip] credential-leak check passed (bot-A token + bot-B token + guild_id + channel_id)"
else
    echo "[discord-roundtrip] credential-leak check passed (bot-A token + guild_id + channel_id)"
fi
exit "$DOTNET_EXIT"
