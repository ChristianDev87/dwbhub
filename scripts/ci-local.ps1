<#
.SYNOPSIS
    Run the full CI pipeline locally — mirrors .github/workflows/ci.yml.

.DESCRIPTION
    Executes the same checks the GitHub workflow runs, in the same order, so a green
    run here means a green run on CI (modulo the trivy + container-smoke gates which
    need extra tooling/docker).

    Jobs run in dependency order; the script fails fast on the first error.

.PARAMETER Fast
    Only run the four cheap jobs (backend, frontend, openapi-drift, sql-tenant-filter-lint).
    Skips security-audit, container-smoke, and e2e-smoke.

.PARAMETER SkipBackend
    Skip backend (lint + 3 test suites).
.PARAMETER SkipFrontend
    Skip frontend (lint + typecheck + vitest + build).
.PARAMETER SkipOpenApi
    Skip the OpenAPI + generated-client drift check.
.PARAMETER SkipSqlLint
    Skip the SQL tenant_id lint + Pester tests.
.PARAMETER SkipSecurity
    Skip security audit (pnpm audit + dotnet vulnerable + optional trivy).
.PARAMETER SkipContainer
    Skip docker container-smoke build/run.
.PARAMETER SkipE2E
    Skip Playwright e2e via docker compose.

.PARAMETER SkipTrivy
    Within security-audit, skip the trivy filesystem scan (e.g. if trivy is not installed).

.EXAMPLE
    ./scripts/ci-local.ps1
    Run the whole pipeline.

.EXAMPLE
    ./scripts/ci-local.ps1 -Fast
    Run only the four cheap PR-gating jobs.

.EXAMPLE
    ./scripts/ci-local.ps1 -SkipE2E -SkipContainer
    Run everything except the docker-heavy bits.
#>
[CmdletBinding()]
param(
    [switch] $Fast,
    [switch] $SkipBackend,
    [switch] $SkipFrontend,
    [switch] $SkipOpenApi,
    [switch] $SkipSqlLint,
    [switch] $SkipSecurity,
    [switch] $SkipContainer,
    [switch] $SkipE2E,
    [switch] $SkipTrivy
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

if ($Fast.IsPresent) {
    $SkipSecurity = $true
    $SkipContainer = $true
    $SkipE2E = $true
}

$results = [System.Collections.Generic.List[pscustomobject]]::new()

function Invoke-Section {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Body,
        [switch] $Skip
    )
    if ($Skip.IsPresent) {
        Write-Host ""
        Write-Host "── $Name ──────────────── SKIPPED" -ForegroundColor DarkYellow
        $results.Add([pscustomobject]@{ Name = $Name; Status = "SKIPPED"; Duration = "—" })
        return
    }

    Write-Host ""
    Write-Host "── $Name ──────────────────────────────" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Body
        $sw.Stop()
        $dur = "{0:mm\:ss}" -f $sw.Elapsed
        Write-Host "✔ $Name passed ($dur)" -ForegroundColor Green
        $results.Add([pscustomobject]@{ Name = $Name; Status = "PASS"; Duration = $dur })
    }
    catch {
        $sw.Stop()
        $dur = "{0:mm\:ss}" -f $sw.Elapsed
        Write-Host "✘ $Name FAILED ($dur)" -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
        $results.Add([pscustomobject]@{ Name = $Name; Status = "FAIL"; Duration = $dur })
        Write-Summary
        exit 1
    }
}

function Write-Summary {
    Write-Host ""
    Write-Host "════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  CI-Local Summary" -ForegroundColor Cyan
    Write-Host "════════════════════════════════════════" -ForegroundColor Cyan
    foreach ($r in $results) {
        $colour = switch ($r.Status) {
            "PASS"    { "Green" }
            "FAIL"    { "Red" }
            "SKIPPED" { "DarkYellow" }
        }
        Write-Host ("  {0,-50} {1,-8} {2}" -f $r.Name, $r.Status, $r.Duration) -ForegroundColor $colour
    }
    Write-Host ""
}

# ─────────────────────────────────────────────────────────────────────────────
# Backend (lint + unit + integration + security)
# ─────────────────────────────────────────────────────────────────────────────
Invoke-Section -Name "backend (lint + unit + integration + security)" -Skip:$SkipBackend.IsPresent -Body {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }
    dotnet restore api/DwbHub.sln
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }
    dotnet format api/DwbHub.sln --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet format check failed" }
    dotnet build api/DwbHub.sln --no-restore -c Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
    dotnet test api/tests/DwbHub.Tests.Unit/DwbHub.Tests.Unit.csproj --no-build -c Release
    if ($LASTEXITCODE -ne 0) { throw "unit tests failed" }
    dotnet test api/tests/DwbHub.Tests.Integration/DwbHub.Tests.Integration.csproj --no-build -c Release
    if ($LASTEXITCODE -ne 0) { throw "integration tests failed" }
    dotnet test api/tests/DwbHub.Tests.Security/DwbHub.Tests.Security.csproj --no-build -c Release
    if ($LASTEXITCODE -ne 0) { throw "security tests failed" }
}

# ─────────────────────────────────────────────────────────────────────────────
# Frontend (lint + typecheck + vitest + build)
# ─────────────────────────────────────────────────────────────────────────────
Invoke-Section -Name "frontend (lint + typecheck + vitest + build)" -Skip:$SkipFrontend.IsPresent -Body {
    pnpm install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw "pnpm install failed" }
    pnpm --filter dwbhub-web lint
    if ($LASTEXITCODE -ne 0) { throw "eslint failed" }
    pnpm --filter dwbhub-web format:check
    if ($LASTEXITCODE -ne 0) { throw "prettier format check failed" }
    pnpm --filter dwbhub-web typecheck
    if ($LASTEXITCODE -ne 0) { throw "tsc typecheck failed" }
    pnpm --filter dwbhub-web test
    if ($LASTEXITCODE -ne 0) { throw "vitest failed" }
    pnpm --filter dwbhub-web build
    if ($LASTEXITCODE -ne 0) { throw "vite build failed" }
}

# ─────────────────────────────────────────────────────────────────────────────
# OpenAPI + generated client drift
# ─────────────────────────────────────────────────────────────────────────────
Invoke-Section -Name "OpenAPI + generated client drift" -Skip:$SkipOpenApi.IsPresent -Body {
    pwsh ./tools/verify-openapi.ps1
    if ($LASTEXITCODE -ne 0) { throw "openapi.yaml drift detected" }
    pwsh ./scripts/gen-openapi-client.ps1 -Check
    if ($LASTEXITCODE -ne 0) { throw "generated client drift detected" }
}

# ─────────────────────────────────────────────────────────────────────────────
# SQL tenant_id lint + Pester
# ─────────────────────────────────────────────────────────────────────────────
Invoke-Section -Name "SQL tenant_id lint" -Skip:$SkipSqlLint.IsPresent -Body {
    pwsh ./tools/check-tenant-filter.ps1
    if ($LASTEXITCODE -ne 0) { throw "tenant_id lint failed" }
    pwsh -NoLogo -NoProfile -Command "Invoke-Pester -Path tools/tests -Output Detailed -CI"
    if ($LASTEXITCODE -ne 0) { throw "Pester tests failed" }
}

# ─────────────────────────────────────────────────────────────────────────────
# Security (pnpm audit + dotnet vulnerable + optional trivy)
# ─────────────────────────────────────────────────────────────────────────────
Invoke-Section -Name "security (pnpm audit + dotnet vulnerable + trivy)" -Skip:$SkipSecurity.IsPresent -Body {
    pnpm audit --audit-level=high
    if ($LASTEXITCODE -ne 0) { throw "pnpm audit found high/critical issues" }
    $output = dotnet list api/DwbHub.sln package --vulnerable --include-transitive
    $output | ForEach-Object { Write-Host $_ }
    if ($output -match "(High|Critical)") { throw "Vulnerable .NET packages detected (High/Critical)" }

    if ($SkipTrivy.IsPresent) {
        Write-Host "trivy scan skipped (-SkipTrivy)" -ForegroundColor DarkYellow
    }
    elseif (Get-Command trivy -ErrorAction SilentlyContinue) {
        trivy fs --severity CRITICAL,HIGH --exit-code 1 --ignore-unfixed .
        if ($LASTEXITCODE -ne 0) { throw "trivy found HIGH/CRITICAL findings" }
    }
    else {
        Write-Host "trivy not installed locally — skipping (CI uses the aquasecurity/trivy-action)" -ForegroundColor DarkYellow
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# Container smoke (docker build api + web, smoke-run health)
# ─────────────────────────────────────────────────────────────────────────────
Invoke-Section -Name "container build smoke (api + web)" -Skip:$SkipContainer.IsPresent -Body {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "docker is required for container-smoke" }

    docker build -f deploy/docker/Dockerfile.api -t dwbhub-api:smoke .
    if ($LASTEXITCODE -ne 0) { throw "API image build failed" }
    docker build -f deploy/docker/Dockerfile.web -t dwbhub-web:smoke .
    if ($LASTEXITCODE -ne 0) { throw "Web image build failed" }

    docker rm -f dwbhub-api-smoke dwbhub-web-smoke 2>$null | Out-Null
    docker run -d --name dwbhub-api-smoke -p 15080:8080 `
        -e DWBHUB_JWT_SECRET=ZGV2X2p3dF9zZWNyZXRfZG9fbm90X3VzZV9pbl9wcm9k `
        -e DWBHUB_ENCRYPTION_KEY=ZGV2X2VuY3J5cHRpb25fa2V5XzMyYnl0ZXNfdGVzdF9vbg== `
        dwbhub-api:smoke | Out-Null
    docker run -d --name dwbhub-web-smoke -p 15173:8080 dwbhub-web:smoke | Out-Null

    try {
        $apiOk = $false
        for ($i = 0; $i -lt 30; $i++) {
            try {
                $r = Invoke-WebRequest http://localhost:15080/api/health -UseBasicParsing -TimeoutSec 5
                if ($r.StatusCode -eq 200) { $apiOk = $true; break }
            } catch { Start-Sleep -Seconds 1 }
        }
        if (-not $apiOk) { docker logs dwbhub-api-smoke; throw "API health check timed out" }
        Write-Host "API healthy" -ForegroundColor Green

        $webOk = $false
        for ($i = 0; $i -lt 15; $i++) {
            try {
                $r = Invoke-WebRequest http://localhost:15173/ -UseBasicParsing -TimeoutSec 5
                if ($r.StatusCode -eq 200) { $webOk = $true; break }
            } catch { Start-Sleep -Seconds 1 }
        }
        if (-not $webOk) { docker logs dwbhub-web-smoke; throw "Web smoke check timed out" }
        Write-Host "Web reachable" -ForegroundColor Green
    }
    finally {
        docker rm -f dwbhub-api-smoke dwbhub-web-smoke 2>$null | Out-Null
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# E2E smoke (Playwright via docker compose)
# ─────────────────────────────────────────────────────────────────────────────
Invoke-Section -Name "e2e smoke (Playwright via compose)" -Skip:$SkipE2E.IsPresent -Body {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "docker is required for e2e-smoke" }
    pwsh ./scripts/e2e-up.ps1
    if ($LASTEXITCODE -ne 0) { throw "e2e tests failed" }
}

Write-Summary
Write-Host "All requested checks passed." -ForegroundColor Green
