#!/bin/sh
# Plan 0.8.1 — pre-create mountpoint directories so the test-runner containers
# can apply their tmpfs / named-volume overlays on top of the :ro workspace bind.
#
# Why this exists:
#   docker-compose.test.yml mounts the repo as `:/workspace:ro` and then layers
#   tmpfs / named volumes at subpaths like /workspace/api/src/DwbHub.Core/obj,
#   /workspace/node_modules, etc. The bind happens BEFORE the overlay mounts,
#   and if a subpath doesn't exist in the bound directory (e.g. `obj/`, `bin/`,
#   `node_modules/` are gitignored and missing in a fresh clone), the docker
#   daemon tries to `mkdirat` inside the :ro bind to create the mountpoint —
#   which fails with "read-only file system".
#
#   On Docker Desktop (Windows/macOS) the filesystem translation layer hides
#   this; on a Linux host with bare bind mounts it fails. CI runs on Linux.
#
# Usage:
#   Run from the repo root before `docker compose -f deploy/compose/docker-compose.test.yml run ...`:
#     sh deploy/test-runners/prep-workspace.sh
#
#   Idempotent: existing directories are left alone (mkdir -p is a no-op).

set -e

# .NET project obj/ + bin/ — backend-tests, openapi-drift, container-smoke use these tmpfs paths
for proj in \
    api/src/DwbHub.Api \
    api/src/DwbHub.Application \
    api/src/DwbHub.Bot \
    api/src/DwbHub.Core \
    api/src/DwbHub.Data \
    api/src/DwbHub.Infrastructure \
    api/tests/DwbHub.Tests.Unit \
    api/tests/DwbHub.Tests.Integration \
    api/tests/DwbHub.Tests.Security
do
    mkdir -p "$proj/obj" "$proj/bin"
done

# Frontend named-volume mountpoints
mkdir -p node_modules web/node_modules web/dist

# Per-start log dir for the API (openapi-drift's verify-openapi.ps1 boots DwbHub.Api)
mkdir -p logs

# --- Plan 0.8.4: stale .js/.d.ts shadow guard ---
# Recurring issue: tsc -b emits .js next to .tsx sources, which Vite/Playwright
# resolve first. This guard fails CI when any non-allowlisted .js or .d.ts file
# is found beside a .ts(x) sibling in web/src or web/tests.
shadow_violations=$(
    cd web && find src tests \
        \( -name '*.js' -o -name '*.d.ts' \) \
        -not -path '*/node_modules/*' \
        -not -path '*/dist/*' \
        -not -path '*/coverage/*' \
        -not -path '*/playwright-report/*' \
        -not -path '*/test-results/*' \
        -not -name 'vite-env.d.ts' \
        -not -name 'schema.d.ts' \
        2>/dev/null \
        | while read -r f; do
            base="${f%.js}"
            base="${base%.d.ts}"
            if [ -f "${base}.ts" ] || [ -f "${base}.tsx" ]; then
                echo "$f"
            fi
        done
)

if [ -n "$shadow_violations" ]; then
    echo "[prep-workspace] FATAL: stale tsc shadow file(s) detected — Vite/Playwright"
    echo "[prep-workspace]        would resolve these before the .tsx sources:"
    echo "$shadow_violations" | sed 's|^|  web/|'
    echo "[prep-workspace]        Run: find web/src web/tests \( -name '*.js' -o -name '*.d.ts' \) -delete"
    echo "[prep-workspace]        (review allowlist in .gitignore for legitimate exceptions)"
    exit 2
fi
echo "[prep-workspace] no stale .js/.d.ts shadows detected"
