# Compare two files line-by-line. Exit 0 on identical, 1 on diff.
param(
    [Parameter(Mandatory)] [string] $Expected,
    [Parameter(Mandatory)] [string] $Actual,
    [string] $Label = "file"
)

if (-not (Test-Path $Expected)) {
    Write-Error "Expected file not found: $Expected"
    exit 2
}
if (-not (Test-Path $Actual)) {
    Write-Error "Actual file not found: $Actual"
    exit 2
}

$expectedContent = Get-Content $Expected -Raw
$actualContent   = Get-Content $Actual   -Raw

if ($expectedContent -eq $actualContent) {
    Write-Host "[OK] $Label matches." -ForegroundColor Green
    exit 0
}

Write-Host "[DRIFT] $Label differs." -ForegroundColor Red
Write-Host "Expected: $Expected"
Write-Host "Actual:   $Actual"

# Show a compact diff using Compare-Object on lines.
$expectedLines = Get-Content $Expected
$actualLines   = Get-Content $Actual
Compare-Object -ReferenceObject $expectedLines -DifferenceObject $actualLines |
    Select-Object -First 80 |
    ForEach-Object {
        $prefix = if ($_.SideIndicator -eq "<=") { "- (expected only)" } else { "+ (actual only)  " }
        Write-Host "$prefix $($_.InputObject)"
    }
exit 1
