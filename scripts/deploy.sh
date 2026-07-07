#!/usr/bin/env bash
# deploy.sh — build, test, bump version, redeploy
# Usage: ./scripts/deploy.sh [--skip-tests] [--dry-run]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$REPO_ROOT/src/StrongLink.Worker/StrongLink.Worker.csproj"
SERVICE="stronglink-bot"

DRY_RUN=false
SKIP_TESTS=false
for arg in "$@"; do
  case $arg in
    --dry-run)    DRY_RUN=true ;;
    --skip-tests) SKIP_TESTS=true ;;
  esac
done

log()  { echo "[deploy] $*"; }
run()  { if $DRY_RUN; then echo "[dry-run] $*"; else "$@"; fi; }

# ── 1. Run tests ─────────────────────────────────────────────────────────────
if $SKIP_TESTS; then
  log "Skipping tests (--skip-tests)"
else
  log "Running tests..."
  TEST_STATUS=0
  TEST_OUTPUT=$(dotnet test "$REPO_ROOT/StrongLink.sln" --no-restore --verbosity minimal 2>&1) || TEST_STATUS=$?
  echo "$TEST_OUTPUT"
  # Honor the real exit code: a non-zero status means a test failed or the host crashed —
  # either way the deploy must abort. (The historical stack-overflow on test-host teardown was a
  # real bug in the game loop, now fixed; we no longer mask the exit code to work around it.)
  if [ "$TEST_STATUS" -ne 0 ]; then
    log "ERROR: test run exited with status $TEST_STATUS. Aborting deploy."
    exit 1
  fi
  PASSED_COUNT=$(echo "$TEST_OUTPUT" | grep -oE 'Passed:\s+[0-9]+' | grep -oE '[0-9]+' | tail -1)
  if [ -z "$PASSED_COUNT" ] || [ "$PASSED_COUNT" -eq 0 ]; then
    log "ERROR: No tests reported as passed — cannot confirm test suite ran. Aborting deploy."
    exit 1
  fi
  log "Tests passed ($PASSED_COUNT tests)."
fi

# ── 2. Bump minor version ─────────────────────────────────────────────────────
CURRENT_VERSION=$(grep -o '<Version>[^<]*</Version>' "$CSPROJ" | head -1 | sed 's|<Version>\(.*\)</Version>|\1|')
IFS='.' read -r MAJOR MINOR PATCH <<< "$CURRENT_VERSION"
NEW_MINOR=$((MINOR + 1))
NEW_VERSION="$MAJOR.$NEW_MINOR.0"

log "Version: $CURRENT_VERSION → $NEW_VERSION"

if ! $DRY_RUN; then
  sed -i "s|<Version>$CURRENT_VERSION</Version>|<Version>$NEW_VERSION</Version>|g" "$CSPROJ"
  sed -i "s|<AssemblyVersion>$CURRENT_VERSION</AssemblyVersion>|<AssemblyVersion>$NEW_VERSION</AssemblyVersion>|g" "$CSPROJ"
  sed -i "s|<FileVersion>$CURRENT_VERSION</FileVersion>|<FileVersion>$NEW_VERSION</FileVersion>|g" "$CSPROJ"
fi

# ── 3. Rebuild and restart container ─────────────────────────────────────────
log "Stopping container..."
run docker compose -f "$REPO_ROOT/docker-compose.yml" stop "$SERVICE"

log "Building image..."
run docker compose -f "$REPO_ROOT/docker-compose.yml" build "$SERVICE"

log "Starting container..."
run docker compose -f "$REPO_ROOT/docker-compose.yml" up -d "$SERVICE"

# ── 4. Tail logs briefly to confirm startup ───────────────────────────────────
log "Waiting for container to start..."
sleep 3
log "Container status:"
docker compose -f "$REPO_ROOT/docker-compose.yml" ps "$SERVICE"
echo ""
log "Recent logs:"
docker compose -f "$REPO_ROOT/docker-compose.yml" logs --tail=30 "$SERVICE"

log "Deploy complete. Version: $NEW_VERSION"
