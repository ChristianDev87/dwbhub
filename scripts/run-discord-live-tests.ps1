[CmdletBinding()]
param()

# Plan 0.8: Discord live-test runner.
#
# Sources DISCORD_DEV_BOT_TOKEN + DISCORD_DEV_GUILD_ID from
# deploy/compose/discord.dev.env (gitignored, never committed) and runs the
# `Category=DiscordLive` xUnit tests against the real Discord gateway.
#
# SECURITY:
#   * The env file is gitignored — see .gitignore lines for `discord.dev.env`
#     and `.env.*`.
#   * Env vars are set ONLY for the dotnet test process via PowerShell
#     ChildProcess scope, then unset in the finally block.
#   * Any token value that leaks into stdout/stderr is redacted with
#     `<REDACTED-TOKEN>` before being written to the host.
#
# USAGE:
#   pwsh scripts/run-discord-live-tests.ps1
#
# CI:
#   GitHub Actions uses `secrets.DISCORD_DEV_BOT_TOKEN` and
#   `secrets.DISCORD_DEV_GUILD_ID` directly — see
#   .github/workflows/ci.yml → `discord-live-tests` job.

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$envFile = Join-Path $repoRoot "deploy/compose/discord.dev.env"
if (-not (Test-Path $envFile)) {
    Write-Host "[ERROR] $envFile not found." -ForegroundColor Red
    Write-Host "Create it with two lines:" -ForegroundColor Yellow
    Write-Host "  DISCORD_DEV_BOT_TOKEN=<your-dev-bot-token>" -ForegroundColor Yellow
    Write-Host "  DISCORD_DEV_GUILD_ID=<your-dev-guild-snowflake>" -ForegroundColor Yellow
    Write-Host "The file is gitignored — it will never be committed." -ForegroundColor Yellow
    exit 1
}

# Parse the env file without echoing any value.
Get-Content $envFile | ForEach-Object {
    if ($_ -match '^\s*([A-Z_][A-Z0-9_]*)\s*=\s*(.+)\s*$') {
        $key = $matches[1].Trim()
        $value = $matches[2].Trim()
        if ($key -eq 'DISCORD_DEV_BOT_TOKEN' -or $key -eq 'DISCORD_DEV_GUILD_ID') {
            Set-Item -Path "Env:$key" -Value $value
        }
    }
}

if ([string]::IsNullOrWhiteSpace($env:DISCORD_DEV_BOT_TOKEN)) {
    Write-Host "[ERROR] DISCORD_DEV_BOT_TOKEN missing or empty in $envFile" -ForegroundColor Red
    exit 1
}
if ([string]::IsNullOrWhiteSpace($env:DISCORD_DEV_GUILD_ID)) {
    Write-Host "[ERROR] DISCORD_DEV_GUILD_ID missing or empty in $envFile" -ForegroundColor Red
    exit 1
}

# Log only meta-info (length, guild ID is a public snowflake — not secret).
Write-Host ("[run-discord-live-tests] env loaded — token chars: {0}, guild_id: {1}" -f `
    $env:DISCORD_DEV_BOT_TOKEN.Length, $env:DISCORD_DEV_GUILD_ID) -ForegroundColor Cyan

$tokenForRedact = $env:DISCORD_DEV_BOT_TOKEN
$exit = 1

try {
    Write-Host "[run-discord-live-tests] running dotnet test --filter `"Category=DiscordLive`" ..." -ForegroundColor Cyan
    # Capture output as strings, redact any token occurrence defensively, then print.
    $output = dotnet test api/tests/DwbHub.Tests.Integration/DwbHub.Tests.Integration.csproj `
        --filter 'Category=DiscordLive' --no-build --nologo -v minimal 2>&1
    $exit = $LASTEXITCODE
    foreach ($line in $output) {
        $safe = ($line -as [string]) -replace [Regex]::Escape($tokenForRedact), '<REDACTED-TOKEN>'
        Write-Host $safe
    }
}
finally {
    Remove-Item Env:DISCORD_DEV_BOT_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:DISCORD_DEV_GUILD_ID -ErrorAction SilentlyContinue
    Write-Host ("[run-discord-live-tests] env cleared. exit: {0}" -f $exit) -ForegroundColor Cyan
}

exit $exit
