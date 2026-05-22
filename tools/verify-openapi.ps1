[CmdletBinding()]
param(
    [switch] $Update # When set, overwrite shared/openapi.yaml with the freshly dumped spec
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$apiCsproj      = "api/src/DwbHub.Api/DwbHub.Api.csproj"
$committedSpec  = "shared/openapi.yaml"
$tempSpec       = Join-Path ([IO.Path]::GetTempPath()) "dwbhub-openapi-$([guid]::NewGuid()).yaml"

Write-Host "Building DwbHub.Api ..." -ForegroundColor Cyan
dotnet build $apiCsproj -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$dllRelative = "api/src/DwbHub.Api/bin/Release/net10.0/DwbHub.Api.dll"
if (-not (Test-Path $dllRelative)) { throw "Build artifact not found: $dllRelative" }

Write-Host "Dumping OpenAPI document v1 ..." -ForegroundColor Cyan
# ASPNETCORE_ENVIRONMENT=Development is required so that AddSwaggerGen / SwaggerDoc("v1")
# is registered; the flag is gated on IsDevelopment() in Program.cs.
#
# `swagger tofile` loads DwbHub.Api.dll, calls builder.Build(), and runs the
# boot-time MigrateUp. That needs a real Postgres — Plan 0.2 onwards. We spin
# up a throw-away one here so the real boot path runs (no skip flags).
$originalEnv        = $env:ASPNETCORE_ENVIRONMENT
$originalConnStr    = $env:DWBHUB_DB_CONNECTION
$originalJwtSecret  = $env:DWBHUB_JWT_SECRET
$pgContainer        = "dwbhub-openapi-pg-" + ([guid]::NewGuid().ToString("N").Substring(0, 8))
$env:ASPNETCORE_ENVIRONMENT = "Development"

# DWBHUB_JWT_SECRET is required by Program.cs since Plan 0.3a (auth wiring).
# `swagger tofile` boots the API host, so the env var must be set; the actual
# value doesn't matter for schema extraction (no JWTs are minted here).
# 32 zero bytes Base64-encoded — never reused in production.
$env:DWBHUB_JWT_SECRET = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="

Write-Host "Starting temporary Postgres ($pgContainer) ..." -ForegroundColor Cyan
docker run -d --name $pgContainer -p 0:5432 `
    -e POSTGRES_DB=dwbhub `
    -e POSTGRES_USER=dwbhub `
    -e POSTGRES_PASSWORD=dwbhub_openapi `
    postgres:17-alpine | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Failed to start Postgres container." }

# Wait for readiness (~5-10 s on cold cache)
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    docker exec $pgContainer pg_isready -U dwbhub -d dwbhub 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { $ready = $true; break }
    Start-Sleep -Seconds 1
}
if (-not $ready) {
    docker logs $pgContainer
    docker rm -f $pgContainer | Out-Null
    throw "Postgres did not become ready within 30 s."
}

# Discover the random host port docker assigned
$pgPort = (docker inspect $pgContainer --format '{{ (index (index .NetworkSettings.Ports "5432/tcp") 0).HostPort }}').Trim()

# Host resolution depends on where the swagger tofile process runs:
#   - Local dev (Windows/macOS Docker Desktop, Linux host): localhost reaches host-published ports.
#   - CI on the self-hosted runner: the runner is itself a docker container. Its loopback has
#     nothing on $pgPort. The watcher adds `host-gateway` -> docker bridge gateway, which
#     forwards to host-published ports — that's the address we need.
$pgHostForTools = if ($env:GITHUB_ACTIONS -eq "true") { "host-gateway" } else { "localhost" }
$env:DWBHUB_DB_CONNECTION = "Host=$pgHostForTools;Port=$pgPort;Database=dwbhub;Username=dwbhub;Password=dwbhub_openapi"
Write-Host "Postgres reachable at ${pgHostForTools}:${pgPort}" -ForegroundColor Green

# --- Mailpit sidecar (Plan 0.3c) ------------------------------------------
# Program.cs fails-fast on missing DWBHUB_SMTP_* vars (MailKitEmailSender requires
# a reachable SMTP endpoint at host registration time). Spin up a throw-away
# Mailpit to satisfy the boot path; no mail is actually sent during schema dump.
$mailpitContainer = "dwbhub-openapi-mp-" + ([guid]::NewGuid().ToString("N").Substring(0, 8))
$originalSmtpHost = $env:DWBHUB_SMTP_HOST
$originalSmtpPort = $env:DWBHUB_SMTP_PORT
$originalSmtpFrom = $env:DWBHUB_SMTP_FROM

Write-Host "Starting temporary Mailpit ($mailpitContainer) ..." -ForegroundColor Cyan
docker run -d --name $mailpitContainer -p 0:1025 axllent/mailpit:v1.21 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Failed to start Mailpit container." }

# Mailpit SMTP listener comes up in <1s; brief wait suffices.
Start-Sleep -Seconds 2

$mpSmtpPort = (docker inspect $mailpitContainer --format '{{ (index (index .NetworkSettings.Ports "1025/tcp") 0).HostPort }}').Trim()
$env:DWBHUB_SMTP_HOST = $pgHostForTools  # same host-detection logic as Postgres
$env:DWBHUB_SMTP_PORT = $mpSmtpPort
$env:DWBHUB_SMTP_FROM = "DwbHub <noreply@openapi.local>"
Write-Host "Mailpit SMTP reachable at ${pgHostForTools}:${mpSmtpPort}" -ForegroundColor Green

try {
    dotnet tool run swagger tofile --yaml --output $tempSpec $dllRelative v1
    if ($LASTEXITCODE -ne 0) { throw "swagger tofile failed." }

    # Normalize the dumped spec to LF so comparisons are deterministic across
    # Windows (swagger tofile may emit CRLF) and Linux (LF).  The committed file
    # is stored with eol=lf per .gitattributes, so we must compare LF-only.
    $dumpedContent    = (Get-Content $tempSpec -Raw) -replace "`r`n", "`n"
    $committedContent = (Get-Content $committedSpec -Raw) -replace "`r`n", "`n"

    if ($Update.IsPresent) {
        # Write raw LF bytes; Copy-Item would preserve whatever line endings swagger produced.
        [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($committedSpec), $dumpedContent)
        Write-Host "[UPDATED] $committedSpec" -ForegroundColor Yellow
        exit 0
    }

    if ($dumpedContent -eq $committedContent) {
        Write-Host "[OK] shared/openapi.yaml matches." -ForegroundColor Green
        exit 0
    }

    # Write normalized copies to temp files so Diff-Files.ps1 shows a readable diff.
    $tempNormDump      = Join-Path ([IO.Path]::GetTempPath()) "dwbhub-openapi-norm-dump-$([guid]::NewGuid()).yaml"
    $tempNormCommitted = Join-Path ([IO.Path]::GetTempPath()) "dwbhub-openapi-norm-committed-$([guid]::NewGuid()).yaml"
    [System.IO.File]::WriteAllText($tempNormDump,      $dumpedContent)
    [System.IO.File]::WriteAllText($tempNormCommitted, $committedContent)
    & (Join-Path $PSScriptRoot "lib/Diff-Files.ps1") `
        -Expected $tempNormCommitted -Actual $tempNormDump -Label "shared/openapi.yaml"
    $exitCode = $LASTEXITCODE
    Remove-Item $tempNormDump      -ErrorAction SilentlyContinue
    Remove-Item $tempNormCommitted -ErrorAction SilentlyContinue
    exit $exitCode
}
finally {
    $env:ASPNETCORE_ENVIRONMENT = $originalEnv
    $env:DWBHUB_DB_CONNECTION   = $originalConnStr
    $env:DWBHUB_JWT_SECRET      = $originalJwtSecret
    $env:DWBHUB_SMTP_HOST       = $originalSmtpHost
    $env:DWBHUB_SMTP_PORT       = $originalSmtpPort
    $env:DWBHUB_SMTP_FROM       = $originalSmtpFrom
    Write-Host "Removing temporary Mailpit ($mailpitContainer) ..." -ForegroundColor DarkGray
    docker rm -f $mailpitContainer 2>&1 | Out-Null
    Write-Host "Removing temporary Postgres ($pgContainer) ..." -ForegroundColor DarkGray
    docker rm -f $pgContainer 2>&1 | Out-Null
    Remove-Item $tempSpec -ErrorAction SilentlyContinue
}
