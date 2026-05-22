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
# DWBHUB_SKIP_MIGRATIONS=1 makes Program.cs accept a missing DWBHUB_DB_CONNECTION
# and skip the boot-time MigrateUp call — both required because `swagger tofile`
# reflects over the built DLL and triggers builder.Build() without a real DB.
$originalEnv          = $env:ASPNETCORE_ENVIRONMENT
$originalSkipMig      = $env:DWBHUB_SKIP_MIGRATIONS
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:DWBHUB_SKIP_MIGRATIONS = "1"
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
    $env:DWBHUB_SKIP_MIGRATIONS = $originalSkipMig
    Remove-Item $tempSpec -ErrorAction SilentlyContinue
}
