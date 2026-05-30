#!/usr/bin/env bash
# Phase 20 close gate — v3.4.0 (triple-SHA) — Bash equivalent of scripts/phase-20-close.ps1
# Correlation Propagation Proof + Synthetic Harness + Triple-SHA Closeout
# ---------------------------------------------------------------------------
# Honors:
#   - Phase 3 D-15 byte-identical psql \l SHA-256 invariant
#   - TEST-REDIS-04: docker exec sk-redis redis-cli --scan | LC_ALL=C sort | sha256sum
#     BEFORE = AFTER across the full suite (locale-locked per RESEARCH Pitfall 6)
#   - TEST-RMQ-04/05 (D-09): docker exec sk-rabbitmq rabbitmqctl -q list_queues name
#     | LC_ALL=C sort | sha256sum, BEFORE = AFTER (net-zero queue leak — temporary/
#     auto-delete per-class test queues + the stable orchestrator-{InstanceId} queue)
#   - Phase 3 D-18 3-consecutive-GREEN cadence
#
# This is the TRIPLE-snapshot gate (psql \l + redis-cli --scan + rabbitmqctl list_queues).
# The full v3.4.0 stack MUST be up so TEST-RMQ-02 (CorrelationPropagationE2ETests,
# Category=RealStack) runs live in the suite.
#
# Three deferred-at-v3.3.0 smells are FIXED here:
#   A. unparseable fact-count is a HARD failure (no `|| echo "-1"` false-green aliasing)
#   B. compose ps --format json read tolerates object-OR-array
#   C. one canonical service list (no PS1/SH divergence), including rabbitmq +
#      orchestrator + baseapi-service (D-12 wget fix makes baseapi-service healthy)
#
# The Phase-16-specific EF-migration + HEALTH-immutable assertion arms are NOT carried
# (Phase 20 is a proof/closeout phase — no schema, no Health change).
#
# Usage:
#   bash scripts/phase-20-close.sh
#
# Exit codes:
#   0  — both build configs zero-warning, all three runs GREEN, all three SHA-256 invariants held
#   1  — invariant violation OR any build/test run RED OR unparseable fact count (Smell A)
#   2  — environment misconfigured (compose stack not healthy)
# ---------------------------------------------------------------------------

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

echo "Phase 20 close gate (triple-SHA) starting from $REPO_ROOT"

# ---- Pre-flight: compose stack healthy (Smell C — ONE canonical Phase-20 list) ----
echo "Pre-flight: compose stack health check..."
for svc in postgres redis rabbitmq otel-collector elasticsearch prometheus orchestrator baseapi-service; do
    # Smell B — tolerate object-OR-array compose ps --format json output (shape varies by compose version).
    health=$(docker compose ps "$svc" --format json 2>/dev/null | jq -r 'if type=="array" then .[0].Health else .Health end' 2>/dev/null || echo "missing")
    # otel-collector has no in-image healthcheck (distroless) — allowed non-healthy.
    if [[ "$health" != "healthy" && "$svc" != "otel-collector" ]]; then
        echo "Service '$svc' is not healthy (Health=$health). Aborting." >&2
        exit 2
    fi
done

# ---- BEFORE snapshots (triple) ----
echo "Capturing BEFORE snapshots..."

# (a) Phase 3 D-15 psql \l invariant
BEFORE_PG=$(docker compose exec -T postgres psql -U postgres -lqt)
BEFORE_PG_HASH=$(printf '%s' "$BEFORE_PG" | sha256sum | awk '{print $1}')
echo "  psql \\l SHA-256 BEFORE = $BEFORE_PG_HASH"

# (b) TEST-REDIS-04 redis-cli --scan invariant — locale-locked per Pitfall 6.
BEFORE_REDIS=$(docker exec sk-redis redis-cli --scan | LC_ALL=C sort)
BEFORE_REDIS_HASH=$(printf '%s' "$BEFORE_REDIS" | sha256sum | awk '{print $1}')
echo "  redis-cli --scan SHA-256 BEFORE = $BEFORE_REDIS_HASH"

# (c) TEST-RMQ-04/05 D-09 rabbitmqctl list_queues invariant — locale-locked. -q per Pitfall 3.
BEFORE_RMQ=$(docker exec sk-rabbitmq rabbitmqctl -q list_queues name | LC_ALL=C sort)
BEFORE_RMQ_HASH=$(printf '%s' "$BEFORE_RMQ" | sha256sum | awk '{print $1}')
echo "  rabbitmqctl list_queues SHA-256 BEFORE = $BEFORE_RMQ_HASH"

# ---- Zero-warning build gate, BOTH configs ----
echo "dotnet clean SK_P.sln..."
dotnet clean SK_P.sln >/dev/null

for cfg in Release Debug; do
    echo "dotnet build SK_P.sln -c $cfg..."
    if ! BUILD_OUT=$(dotnet build SK_P.sln -c "$cfg" 2>&1); then
        echo "Build ($cfg) FAILED (TreatWarningsAsErrors makes a warning fatal)." >&2
        echo "$BUILD_OUT" >&2
        exit 1
    fi
    echo "  Build ($cfg) zero-warning, exit 0."
done

# ---- 3-GREEN cadence (Phase 3 D-18) ----
# FULL suite, no Category filter — TEST-RMQ-02 (Category=RealStack) MUST run with the stack up.
PASSED_COUNTS=()
for i in 1 2 3; do
    echo "Run $i of 3: dotnet test (full suite)..."
    START=$(date +%s)
    if ! OUTPUT=$(dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build 2>&1); then
        echo "Run $i FAILED." >&2
        echo "$OUTPUT" >&2
        exit 1
    fi
    END=$(date +%s)
    # Smell A — an unparseable fact count is a HARD failure (never coerce to -1; three failed
    # parses would alias -1==-1==-1 and false-green the 3-GREEN equality check below).
    PASSED=$(echo "$OUTPUT" | grep -oE 'Passed:\s+[0-9]+' | head -1 | grep -oE '[0-9]+' || true)
    if [[ -z "$PASSED" ]]; then
        echo "Run $i — could not parse 'Passed: <n>' from dotnet test output. Aborting (Smell A guard)." >&2
        echo "$OUTPUT" >&2
        exit 1
    fi
    PASSED_COUNTS+=("$PASSED")
    echo "  Run $i — Passed=$PASSED, Duration=$((END - START))s"
done

# All three runs must have the same Passed count
if [[ "${PASSED_COUNTS[0]}" != "${PASSED_COUNTS[1]}" || "${PASSED_COUNTS[1]}" != "${PASSED_COUNTS[2]}" ]]; then
    echo "3-GREEN cadence violation — fact counts diverge: ${PASSED_COUNTS[*]}" >&2
    exit 1
fi
PASSED_TOTAL="${PASSED_COUNTS[0]}"
echo "3-GREEN cadence passed — $PASSED_TOTAL facts GREEN across 3 runs."
echo "  Observed Phase 20 fact count: $PASSED_TOTAL."

# ---- AFTER snapshots (triple) ----
echo "Capturing AFTER snapshots..."
AFTER_PG=$(docker compose exec -T postgres psql -U postgres -lqt)
AFTER_PG_HASH=$(printf '%s' "$AFTER_PG" | sha256sum | awk '{print $1}')
echo "  psql \\l SHA-256 AFTER  = $AFTER_PG_HASH"

AFTER_REDIS=$(docker exec sk-redis redis-cli --scan | LC_ALL=C sort)
AFTER_REDIS_HASH=$(printf '%s' "$AFTER_REDIS" | sha256sum | awk '{print $1}')
echo "  redis-cli --scan SHA-256 AFTER  = $AFTER_REDIS_HASH"

AFTER_RMQ=$(docker exec sk-rabbitmq rabbitmqctl -q list_queues name | LC_ALL=C sort)
AFTER_RMQ_HASH=$(printf '%s' "$AFTER_RMQ" | sha256sum | awk '{print $1}')
echo "  rabbitmqctl list_queues SHA-256 AFTER  = $AFTER_RMQ_HASH"

# ---- Invariant assertions (triple) ----
ALL_GOOD=1

if [[ "$BEFORE_PG_HASH" != "$AFTER_PG_HASH" ]]; then
    echo "Phase 3 D-15 INVARIANT VIOLATED: psql \\l SHA-256 BEFORE != AFTER" >&2
    echo "  BEFORE = $BEFORE_PG_HASH" >&2
    echo "  AFTER  = $AFTER_PG_HASH" >&2
    ALL_GOOD=0
else
    echo "Phase 3 D-15 invariant HELD: psql \\l SHA-256 = $AFTER_PG_HASH"
fi

if [[ "$BEFORE_REDIS_HASH" != "$AFTER_REDIS_HASH" ]]; then
    echo "TEST-REDIS-04 INVARIANT VIOLATED: redis-cli --scan SHA-256 BEFORE != AFTER" >&2
    echo "  BEFORE = $BEFORE_REDIS_HASH" >&2
    echo "  AFTER  = $AFTER_REDIS_HASH" >&2
    echo "  Investigate: 'docker exec sk-redis redis-cli --scan | grep test:cls-'" >&2
    ALL_GOOD=0
else
    echo "TEST-REDIS-04 invariant HELD: redis-cli --scan SHA-256 = $AFTER_REDIS_HASH"
fi

if [[ "$BEFORE_RMQ_HASH" != "$AFTER_RMQ_HASH" ]]; then
    echo "TEST-RMQ-04/05 INVARIANT VIOLATED: rabbitmqctl list_queues SHA-256 BEFORE != AFTER" >&2
    echo "  BEFORE = $BEFORE_RMQ_HASH" >&2
    echo "  AFTER  = $AFTER_RMQ_HASH" >&2
    echo "  Investigate a leaked queue: 'docker exec sk-rabbitmq rabbitmqctl -q list_queues name'" >&2
    ALL_GOOD=0
else
    echo "TEST-RMQ-04/05 invariant HELD: rabbitmqctl list_queues SHA-256 = $AFTER_RMQ_HASH"
fi

if [[ "$ALL_GOOD" != "1" ]]; then
    echo "Phase 20 close gate FAILED. Resolve violations and re-run." >&2
    exit 1
fi

echo "Phase 20 close gate PASSED."
echo "  Total facts GREEN: $PASSED_TOTAL"
echo "  psql \\l SHA-256:              $AFTER_PG_HASH"
echo "  redis-cli --scan SHA-256:     $AFTER_REDIS_HASH"
echo "  rabbitmqctl list_queues SHA-256: $AFTER_RMQ_HASH"
echo "Operator: append these three SHA values + the Passed count to .planning/STATE.md Phase 20 P04 close entry."

exit 0
