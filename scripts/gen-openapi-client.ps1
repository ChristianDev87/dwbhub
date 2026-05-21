[CmdletBinding()]
param(
    [switch] $Check # When set, fail on drift instead of overwriting
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$spec      = "shared/openapi.yaml"
$outFile   = "web/src/lib/api/generated/schema.d.ts"
$tempFile  = Join-Path ([IO.Path]::GetTempPath()) "dwbhub-schema-$([guid]::NewGuid()).d.ts"

if (-not (Test-Path $spec)) {
    throw "OpenAPI spec missing: $spec. Run tools/verify-openapi.ps1 -Update first."
}

New-Item -ItemType Directory -Force (Split-Path $outFile) | Out-Null

Write-Host "Generating TypeScript types from $spec ..." -ForegroundColor Cyan
pnpm --filter dwbhub-web exec openapi-typescript $spec --output $tempFile
if ($LASTEXITCODE -ne 0) { throw "openapi-typescript failed." }

# Prepend a banner so the generated file is obvious in code review.
$banner = @"
/**
 * AUTO-GENERATED FILE — do not edit by hand.
 *
 * Source:    shared/openapi.yaml
 * Generator: openapi-typescript
 * Update:    pwsh ./scripts/gen-openapi-client.ps1
 * Verify:    pwsh ./scripts/gen-openapi-client.ps1 -Check
 */
"@
$content = "$banner`r`n" + (Get-Content $tempFile -Raw)

if ($Check.IsPresent) {
    if (-not (Test-Path $outFile)) {
        Write-Host "[DRIFT] Committed schema.d.ts is missing." -ForegroundColor Red
        Remove-Item $tempFile -ErrorAction SilentlyContinue
        exit 1
    }
    $committed = Get-Content $outFile -Raw
    if ($committed -ne $content) {
        Write-Host "[DRIFT] web/src/lib/api/generated/schema.d.ts is out of sync." -ForegroundColor Red
        & (Join-Path $repoRoot "tools/lib/Diff-Files.ps1") -Expected $outFile -Actual $tempFile -Label "schema.d.ts" | Out-Null
        Remove-Item $tempFile -ErrorAction SilentlyContinue
        exit 1
    }
    Write-Host "[OK] schema.d.ts is up to date." -ForegroundColor Green
    Remove-Item $tempFile -ErrorAction SilentlyContinue
    exit 0
}

$content | Set-Content $outFile -NoNewline
Remove-Item $tempFile -ErrorAction SilentlyContinue
Write-Host "[UPDATED] $outFile" -ForegroundColor Yellow
