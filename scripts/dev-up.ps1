[CmdletBinding()]
param(
    [switch] $Rebuild,
    [switch] $Logs
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location (Join-Path $repoRoot "deploy/compose")

if (-not (Test-Path ".env")) {
    Copy-Item ".env.example" ".env"
    Write-Host "Created deploy/compose/.env from .env.example. Adjust secrets before any non-local use." -ForegroundColor Yellow
}

$composeFiles = @(
    "-f", "docker-compose.yml",
    "-f", "docker-compose.dev.yml"
)

if ($Rebuild.IsPresent) {
    Write-Host "Rebuilding images ..." -ForegroundColor Cyan
    docker compose @composeFiles build --pull
}

Write-Host "Bringing up dev stack ..." -ForegroundColor Cyan
docker compose @composeFiles up -d

Write-Host ""
Write-Host "API     -> http://localhost:5080/api/health" -ForegroundColor Green
Write-Host "Web     -> http://localhost:5173" -ForegroundColor Green
Write-Host "MailHog -> http://localhost:8025" -ForegroundColor Green
Write-Host ""

if ($Logs.IsPresent) {
    docker compose @composeFiles logs -f
}
