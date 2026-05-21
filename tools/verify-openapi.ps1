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
$originalEnv = $env:ASPNETCORE_ENVIRONMENT
$env:ASPNETCORE_ENVIRONMENT = "Development"
try {
    dotnet tool run swagger tofile --yaml --output $tempSpec $dllRelative v1
    if ($LASTEXITCODE -ne 0) { throw "swagger tofile failed." }

    if ($Update.IsPresent) {
        Copy-Item $tempSpec $committedSpec -Force
        Write-Host "[UPDATED] $committedSpec" -ForegroundColor Yellow
        exit 0
    }

    & (Join-Path $PSScriptRoot "lib/Diff-Files.ps1") `
        -Expected $committedSpec -Actual $tempSpec -Label "shared/openapi.yaml"
    exit $LASTEXITCODE
}
finally {
    $env:ASPNETCORE_ENVIRONMENT = $originalEnv
    Remove-Item $tempSpec -ErrorAction SilentlyContinue
}
