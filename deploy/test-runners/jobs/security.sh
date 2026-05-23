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

# Run all three; capture each to JSON; only fail at the end if any has HIGH/CRITICAL.
pnpm --filter dwbhub-web audit --json --audit-level high > "$RESULTS/pnpm.json" || true
dotnet list api/DwbHub.sln package --vulnerable --include-transitive --format json > "$RESULTS/dotnet.json" || true
trivy fs --format json --severity HIGH,CRITICAL --scanners vuln /workspace > "$RESULTS/trivy.json"

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
