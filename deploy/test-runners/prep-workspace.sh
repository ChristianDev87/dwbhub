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
