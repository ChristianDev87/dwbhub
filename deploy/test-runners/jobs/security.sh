#!/bin/sh
# Plan 0.8.1 — security audit job: pnpm audit + dotnet vulnerable + trivy fs.
#
# Exit codes:
#   0 = no HIGH/CRITICAL vulnerabilities found
#   1 = HIGH or CRITICAL vulnerabilities detected
#   2 = infrastructure error

set -e

RESULTS=/results/security
mkdir -p "$RESULTS"

cd /workspace

# Wrap a long-running command with a heartbeat that emits a "still working"
# line to STDERR every 30s. trivy's internal scan phase (between "Vulnerability
# scanning is enabled" and the WARN flood for the dotnet deps walk) is ~3 min
# of complete silence with no native progress output. Without the heartbeat,
# operators can't tell if the process is hung or scanning.
#
# Heartbeat to stderr keeps the wrapped command's stdout clean for the JSON
# file redirect. The entrypoint.sh '2>&1 | tee' pipeline still surfaces it
# live in run.log + container stdout.
with_heartbeat() {
    local label="$1"
    shift
    ( while sleep 30; do echo "  ... ${label} still running ($(date -u +%H:%M:%S))" >&2; done ) &
    local hb_pid=$!
    "$@"
    local rc=$?
    kill "$hb_pid" 2>/dev/null
    wait "$hb_pid" 2>/dev/null
    return $rc
}

# Run all three; capture each to JSON; only fail at the end if any has HIGH/CRITICAL.
# Each step echoes a banner so operators see live progress while the long-running
# tools (especially trivy's first-run DB download, ~3 min, ~178 MB) are silent.
echo "=== security.sh: pnpm audit (HIGH/CRITICAL) ==="
pnpm --filter dwbhub-web audit --json --audit-level high > "$RESULTS/pnpm.json" || true

echo "=== security.sh: dotnet list package --vulnerable ==="
dotnet list api/DwbHub.sln package --vulnerable --include-transitive --format json > "$RESULTS/dotnet.json" || true

echo "=== security.sh: trivy fs --format json (first run downloads ~178 MB vuln DB; takes 2-3 min) ==="
with_heartbeat "trivy json scan" trivy fs --format json --severity HIGH,CRITICAL --scanners vuln /workspace > "$RESULTS/trivy.json"

echo "=== security.sh: trivy fs --format sarif (for GitHub Code Scanning — Plan 0.8.3 Task 7) ==="
with_heartbeat "trivy sarif scan" trivy fs --format sarif --severity HIGH,CRITICAL --scanners vuln /workspace --output "$RESULTS/trivy.sarif"

echo "=== security.sh: parsing results ==="

FAIL=0
# Parse via node (Dockerfile.security has node)
if [ -s "$RESULTS/pnpm.json" ]; then
    HIGH=$(node -e "const j=require('$RESULTS/pnpm.json'); console.log((j.metadata?.vulnerabilities?.high || 0) + (j.metadata?.vulnerabilities?.critical || 0))" 2>/dev/null || echo "0")
    if [ "$HIGH" != "0" ]; then
        echo "pnpm audit: $HIGH high/critical vulnerabilities"
        FAIL=1
    fi
fi
DOTNET_VULNS=$(node -e "const j=require('$RESULTS/dotnet.json'); const projs=j.projects||[]; let n=0; projs.forEach(p=>(p.frameworks||[]).forEach(f=>(f.topLevelPackages||[]).concat(f.transitivePackages||[]).forEach(pkg=>n+=(pkg.vulnerabilities||[]).length))); console.log(n)" 2>/dev/null || echo "0")
if [ "$DOTNET_VULNS" != "0" ]; then echo "dotnet: $DOTNET_VULNS vulnerable packages"; FAIL=1; fi
TRIVY_VULNS=$(node -e "const j=require('$RESULTS/trivy.json'); console.log((j.Results||[]).reduce((s,r)=>s+(r.Vulnerabilities||[]).length,0))" 2>/dev/null || echo "0")
if [ "$TRIVY_VULNS" != "0" ]; then echo "trivy: $TRIVY_VULNS HIGH/CRITICAL vulnerabilities"; FAIL=1; fi

echo "=== security.sh: complete, exit=$FAIL ==="
exit $FAIL
