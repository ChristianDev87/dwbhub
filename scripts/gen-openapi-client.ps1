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

# pnpm --filter changes cwd to web/ before running the binary, so paths must be
# resolved to absolute form against the repo root or they'll be interpreted as
# web/shared/openapi.yaml (which doesn't exist).
$specAbs = (Resolve-Path $spec).Path

Write-Host "Generating TypeScript types from $spec ..." -ForegroundColor Cyan
pnpm --filter dwbhub-web exec openapi-typescript $specAbs --output $tempFile
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
# Normalize to LF so the committed file (eol=lf per .gitattributes) always matches
# regardless of whether pwsh is running on Windows (CRLF here-strings) or Linux (LF).
$content = $content -replace "`r`n", "`n"

if ($Check.IsPresent) {
    if (-not (Test-Path $outFile)) {
        Write-Host "[DRIFT] Committed schema.d.ts is missing." -ForegroundColor Red
        Remove-Item $tempFile -ErrorAction SilentlyContinue
        exit 1
    }
    # Also normalize the committed file in case core.autocrlf injected CRLFs on checkout.
    $committed = (Get-Content $outFile -Raw) -replace "`r`n", "`n"
    if ($committed -ne $content) {
        Write-Host "[DRIFT] web/src/lib/api/generated/schema.d.ts is out of sync." -ForegroundColor Red
        # Write LF-normalized content to a temp file so Diff-Files.ps1 shows a meaningful diff.
        $tempNorm = Join-Path ([IO.Path]::GetTempPath()) "dwbhub-schema-norm-$([guid]::NewGuid()).d.ts"
        [System.IO.File]::WriteAllText($tempNorm, $content)
        & (Join-Path $repoRoot "tools/lib/Diff-Files.ps1") -Expected $outFile -Actual $tempNorm -Label "schema.d.ts" | Out-Null
        Remove-Item $tempNorm  -ErrorAction SilentlyContinue
        Remove-Item $tempFile  -ErrorAction SilentlyContinue
        exit 1
    }
    Write-Host "[OK] schema.d.ts is up to date." -ForegroundColor Green
    Remove-Item $tempFile -ErrorAction SilentlyContinue
    exit 0
}

# Use WriteAllText to write raw bytes; Set-Content can append system line endings.
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($outFile), $content)
Remove-Item $tempFile -ErrorAction SilentlyContinue
Write-Host "[UPDATED] $outFile" -ForegroundColor Yellow
