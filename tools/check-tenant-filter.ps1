[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments)]
    [string[]] $Path
)

$ErrorActionPreference = "Stop"

if (-not $Path -or $Path.Count -eq 0) {
    # Default: scan migrations + data project sources.
    $Path = @()
    foreach ($candidate in @("api/src/DwbHub.Data", "api/migrations")) {
        if (Test-Path $candidate) { $Path += $candidate }
    }
    if ($Path.Count -eq 0) {
        Write-Host "[OK] check-tenant-filter: no SQL inputs to scan yet." -ForegroundColor Green
        exit 0
    }
}

$files = foreach ($p in $Path) {
    if (Test-Path $p -PathType Container) {
        Get-ChildItem $p -Recurse -Include *.sql,*.cs
    }
    elseif (Test-Path $p -PathType Leaf) {
        Get-Item $p
    }
}

$mutationPattern = '(?im)\b(SELECT|UPDATE|DELETE\s+FROM|INSERT\s+INTO)\b'
$tenantPattern   = '(?i)tenant_id'
$optOutPattern   = '(?i)DWBHUB-NO-TENANT-FILTER'
$tableAllowlist  = @('tenants', 'system_bootstrap_lock', 'feature_flags', 'captcha_challenges', 'login_attempt_log')

$failures = New-Object System.Collections.Generic.List[string]

foreach ($file in $files) {
    $text = Get-Content $file.FullName -Raw
    if ($text -match $optOutPattern) { continue }

    # Crude statement splitter: strip single-quoted string literals, then split on semicolons.
    # SQL escapes single quotes by doubling them (''); the regex below collapses any
    # 'string with ;' into empty quotes before the split so embedded semicolons don't break statements.
    $stripped = $text -replace "'(?:[^']|'')*'", "''"
    $statements = $stripped -split ';'
    foreach ($stmt in $statements) {
        if ($stmt -notmatch $mutationPattern) { continue }
        if ($stmt -match $tenantPattern) { continue }

        # Allow statements that only touch allowlisted tables.
        $touchesAllowlistOnly = $true
        foreach ($table in [regex]::Matches($stmt, '(?i)\bFROM\s+([\w.]+)|\bINTO\s+([\w.]+)|\bUPDATE\s+([\w.]+)')) {
            $name = ($table.Groups[1].Value, $table.Groups[2].Value, $table.Groups[3].Value | Where-Object { $_ })[0]
            if ($name) {
                # Strip a schema-qualifier prefix so allowlist lookups work for `public.tenants` etc.
                $name = $name -replace '^.*\.', ''
            }
            if ($name -and ($tableAllowlist -notcontains $name.ToLower())) {
                $touchesAllowlistOnly = $false
                break
            }
        }
        if ($touchesAllowlistOnly) { continue }

        $snippet = ($stmt.Trim() -split "`n")[0]
        $failures.Add("$($file.FullName): missing tenant_id near: $snippet")
    }
}

if ($failures.Count -gt 0) {
    foreach ($f in $failures) { Write-Host $f -ForegroundColor Red }
    Write-Host "[FAIL] check-tenant-filter: $($failures.Count) statement(s) missing tenant_id." -ForegroundColor Red
    exit 1
}

Write-Host "[OK] check-tenant-filter: all scanned statements include tenant_id (or are allowlisted/opted-out)." -ForegroundColor Green
exit 0
