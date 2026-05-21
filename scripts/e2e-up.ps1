[CmdletBinding()]
param(
    [switch] $NoBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

# Host that resolves to whoever publishes the compose ports. Locally this is `localhost`;
# in CI the runner is itself a container, so it needs `host-gateway` (added by the watcher
# via --add-host=host-gateway:host-gateway) to reach the host's published ports.
$e2eHost = if ($env:E2E_HOST) { $env:E2E_HOST } else { "localhost" }
$apiPort = if ($env:API_PORT) { $env:API_PORT } else { "5080" }
$webPort = if ($env:WEB_PORT) { $env:WEB_PORT } else { "5173" }

Push-Location (Join-Path $repoRoot "deploy/compose")
if (-not (Test-Path ".env")) { Copy-Item ".env.example" ".env" }
$composeFiles = @("-f", "docker-compose.yml", "-f", "docker-compose.dev.yml")

# Pre-clean: a previous run that was hard-cancelled (SIGKILL on the runner-container)
# can leave the compose stack up, holding host ports. Always nuke the stack before bring-up.
Write-Host "Pre-cleaning any leftover compose stack ..." -ForegroundColor DarkYellow
docker compose @composeFiles down -v --remove-orphans 2>$null | Out-Null

# Build only the api service. The web service in the dev override uses an upstream
# node:22-bookworm-slim image (compose pulls it); never let `compose build` touch
# web here — its inherited build directive from the base compose.yml would tag the
# nginx-runtime build result as `node:22-bookworm-slim`, poisoning the cached tag.
if (-not $NoBuild.IsPresent) {
    docker compose @composeFiles build api
}
docker compose @composeFiles up -d
Pop-Location

function Wait-Url([string] $url, [int] $timeoutSec = 120) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5
            if ($r.StatusCode -eq 200) { return }
        } catch { Start-Sleep -Seconds 2 }
    }
    throw "Timeout waiting for $url"
}

$apiUrl = "http://${e2eHost}:${apiPort}/api/health"
$webUrl = "http://${e2eHost}:${webPort}/"
Write-Host "Waiting for $apiUrl ..." -ForegroundColor Cyan
try { Wait-Url $apiUrl } catch {
    Write-Host "API never came up — dumping logs for diagnostics" -ForegroundColor Red
    Push-Location (Join-Path $repoRoot "deploy/compose")
    docker compose @composeFiles logs --tail 100 api
    docker compose @composeFiles down -v --remove-orphans
    Pop-Location
    throw
}
Write-Host "Waiting for $webUrl ..." -ForegroundColor Cyan
try { Wait-Url $webUrl } catch {
    Write-Host "Web never came up — dumping logs for diagnostics" -ForegroundColor Red
    Push-Location (Join-Path $repoRoot "deploy/compose")
    docker compose @composeFiles logs --tail 100 web
    docker compose @composeFiles down -v --remove-orphans
    Pop-Location
    throw
}

Write-Host "Stack ready. Running Playwright ..." -ForegroundColor Cyan
Push-Location (Join-Path $repoRoot "web")
try {
    $env:E2E_BASE_URL = "http://${e2eHost}:${webPort}"
    pnpm exec playwright install --with-deps chromium firefox
    pnpm exec playwright test
} finally {
    Pop-Location
    # --remove-orphans + -v: full teardown even if the container set has drifted from the
    # compose file (e.g. an interrupted previous run added/removed services).
    Push-Location (Join-Path $repoRoot "deploy/compose")
    docker compose @composeFiles down -v --remove-orphans
    Pop-Location
}
