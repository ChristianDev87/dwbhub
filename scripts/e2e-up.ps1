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

# The compose file pins images to dwbhub-{api,web}:local which don't exist in any registry.
# Always build unless the caller explicitly opts out.
if (-not $NoBuild.IsPresent) {
    docker compose @composeFiles build
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
Wait-Url $apiUrl
Write-Host "Waiting for $webUrl ..." -ForegroundColor Cyan
Wait-Url $webUrl

Write-Host "Stack ready. Running Playwright ..." -ForegroundColor Cyan
Push-Location (Join-Path $repoRoot "web")
try {
    $env:E2E_BASE_URL = "http://${e2eHost}:${webPort}"
    pnpm exec playwright install --with-deps chromium firefox
    pnpm exec playwright test
} finally {
    Pop-Location
    Push-Location (Join-Path $repoRoot "deploy/compose")
    docker compose @composeFiles down
    Pop-Location
}
