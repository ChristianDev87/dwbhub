#!/bin/sh
# Plan 0.8.1 — container-smoke: build Dockerfile.api + Dockerfile.web, verify images create.
#
# Exit codes:
#   0 = all images built and instantiated successfully
#   1 = build or create failed
#   2 = infrastructure error

set -e

RESULTS=/results/container-smoke
mkdir -p "$RESULTS"

cd /workspace

CLEANUP_IMAGES=""
CLEANUP_CONTAINERS=""
cleanup() {
    [ -n "$CLEANUP_CONTAINERS" ] && docker rm -f $CLEANUP_CONTAINERS 2>/dev/null || true
    [ -n "$CLEANUP_IMAGES" ] && docker rmi $CLEANUP_IMAGES 2>/dev/null || true
}
trap cleanup EXIT

echo "=== container-smoke: build api ==="
# Resolve TARGETARCH in the .NET convention (x64 / arm64) from the runner's uname.
# Dockerfile.api passes this to `dotnet restore --arch $TARGETARCH`; without it
# the ARG is empty and dotnet restore fails with "Required argument missing".
DOTNET_ARCH=$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/;s/armv7l/arm/')
docker build -f deploy/docker/Dockerfile.api --build-arg TARGETARCH="$DOTNET_ARCH" -t dwbhub-api:smoke .
CLEANUP_IMAGES="$CLEANUP_IMAGES dwbhub-api:smoke"

echo "=== container-smoke: build web ==="
docker build -f deploy/docker/Dockerfile.web -t dwbhub-web:smoke .
CLEANUP_IMAGES="$CLEANUP_IMAGES dwbhub-web:smoke"

echo "=== container-smoke: verify images instantiate ==="
API_ID=$(docker create dwbhub-api:smoke)
CLEANUP_CONTAINERS="$CLEANUP_CONTAINERS $API_ID"
WEB_ID=$(docker create dwbhub-web:smoke)
CLEANUP_CONTAINERS="$CLEANUP_CONTAINERS $WEB_ID"

echo "=== container-smoke.sh: complete ==="
