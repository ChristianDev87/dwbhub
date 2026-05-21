[CmdletBinding()]
param(
    [switch] $Rebuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

# Boot the dev stack
Push-Location (Join-Path $repoRoot "deploy/compose")
if (-not (Test-Path ".env")) { Copy-Item ".env.example" ".env" }
$composeFiles = @("-f", "docker-compose.yml", "-f", "docker-compose.dev.yml")
if ($Rebuild.IsPresent) {
    docker compose @composeFiles build --pull
}
docker compose @composeFiles up -d
Pop-Location

# Wait for both endpoints
function Wait-Url([string] $url, [int] $timeoutSec = 90) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5
            if ($r.StatusCode -eq 200) { return }
        } catch { Start-Sleep -Seconds 2 }
    }
    throw "Timeout waiting for $url"
}

Wait-Url "http://localhost:5080/api/health"
Wait-Url "http://localhost:5173/"

Write-Host "Stack ready. Running Playwright ..." -ForegroundColor Cyan
Push-Location (Join-Path $repoRoot "web")
try {
    pnpm exec playwright install --with-deps chromium firefox
    pnpm exec playwright test
} finally {
    Pop-Location
    Push-Location (Join-Path $repoRoot "deploy/compose")
    docker compose @composeFiles down
    Pop-Location
}
