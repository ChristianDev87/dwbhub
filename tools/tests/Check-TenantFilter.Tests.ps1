BeforeAll {
    $script:scriptPath = Join-Path $PSScriptRoot ".." "check-tenant-filter.ps1"
    $script:fixturesDir = Join-Path $PSScriptRoot "fixtures"
}

Describe "check-tenant-filter" {
    It "accepts SQL with tenant_id on every mutation" {
        $output = & pwsh -NoLogo -NoProfile -File $script:scriptPath -Path (Join-Path $script:fixturesDir "has-tenant-filter.sql") *>&1
        $LASTEXITCODE | Should -Be 0
    }

    It "rejects SQL missing tenant_id" {
        $output = & pwsh -NoLogo -NoProfile -File $script:scriptPath -Path (Join-Path $script:fixturesDir "missing-tenant-filter.sql") *>&1
        $LASTEXITCODE | Should -Be 1
        ($output -join "`n") | Should -Match "missing tenant_id"
    }

    It "respects the DWBHUB-NO-TENANT-FILTER opt-out comment" {
        $output = & pwsh -NoLogo -NoProfile -File $script:scriptPath -Path (Join-Path $script:fixturesDir "no-tenant-filter-comment.sql") *>&1
        $LASTEXITCODE | Should -Be 0
    }

    It "accepts SQL with semicolons inside string literals" {
        $null = & pwsh -NoLogo -NoProfile -File $script:scriptPath -Path (Join-Path $script:fixturesDir "string-literal-semicolon.sql") *>&1
        $LASTEXITCODE | Should -Be 0
    }

    It "accepts schema-qualified allowlisted tables" {
        $null = & pwsh -NoLogo -NoProfile -File $script:scriptPath -Path (Join-Path $script:fixturesDir "schema-qualified-allowlist.sql") *>&1
        $LASTEXITCODE | Should -Be 0
    }

    It "passes on the real Plan-0.2 sources (api/migrations + api/src/DwbHub.Data)" {
        $repoRoot = Resolve-Path (Join-Path $PSScriptRoot ".." "..")
        Push-Location $repoRoot
        try {
            $output = & pwsh -NoLogo -NoProfile -File $script:scriptPath *>&1
            $LASTEXITCODE | Should -Be 0 -Because "Plan 0.2 migrations should pass the lint; output was:`n$($output -join "`n")"
        }
        finally {
            Pop-Location
        }
    }
}
