#!/bin/sh
# DwbHub dev stack launcher — pure docker compose, no PowerShell.
#
# Usage:
#   ./dev.sh up -d              # start (detached)
#   ./dev.sh up -d --build      # rebuild + start
#   ./dev.sh down               # stop (keep volumes)
#   ./dev.sh down -v            # stop + remove volumes (fresh DB next time)
#   ./dev.sh logs -f            # tail logs
#   ./dev.sh ps                 # status
#   ./dev.sh exec api bash      # shell into api container
#
# Works on Git Bash (Windows), native Linux shell, macOS — anywhere docker is installed.

set -e

# Always run from the repo root regardless of where the user invoked from.
REPO_ROOT=$(cd "$(dirname "$0")" && pwd)
cd "$REPO_ROOT"

COMPOSE_DIR="deploy/compose"
ENV_FILE="$COMPOSE_DIR/.env"
ENV_EXAMPLE="$COMPOSE_DIR/.env.example"

# Bootstrap deploy/compose/.env on first run if missing — copy from .env.example.
if [ ! -f "$ENV_FILE" ]; then
    if [ ! -f "$ENV_EXAMPLE" ]; then
        echo "ERROR: neither $ENV_FILE nor $ENV_EXAMPLE exist." >&2
        exit 1
    fi
    cp "$ENV_EXAMPLE" "$ENV_FILE"
    echo "[dev.sh] Created $ENV_FILE from .env.example. Adjust secrets before any non-local use." >&2
fi

# Forward all args to docker compose with the dev stack files chained.
docker compose \
    -f "$COMPOSE_DIR/docker-compose.yml" \
    -f "$COMPOSE_DIR/docker-compose.dev.yml" \
    "$@"
EXIT=$?

# Helpful URL banner only after `up` succeeded (and only when first arg starts with "up").
case "${1:-}" in
    up)
        if [ "$EXIT" -eq 0 ]; then
            cat <<'EOF'

  Web      → http://localhost:5173
  API      → http://localhost:5080/api/health
  MailHog  → http://localhost:8025

  Logs:  ./dev.sh logs -f
  Stop:  ./dev.sh down
EOF
        fi
        ;;
esac

exit "$EXIT"
