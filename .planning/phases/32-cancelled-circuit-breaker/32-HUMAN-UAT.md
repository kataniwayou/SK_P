---
status: resolved
phase: 32-cancelled-circuit-breaker
source: [32-VERIFICATION.md, 32-07-SUMMARY.md]
started: 2026-06-04
updated: 2026-06-05
resolution: superseded-by-32.1
---

## Resolution

**Superseded by Phase 32.1 (dead-letter-on-exhaustion) — 2026-06-05.** Phase 32.1 reverted the
cancelled circuit-breaker entirely (32-REVIEW.md WR-01: the breaker was judged not worth its surface
area). Both operator tests below verified breaker behavior that no longer exists in the codebase:
the breaker catch, the no-TTL `skp:cancelled:*` marker, and the Fault-driven unschedule are all
removed, and `scripts/phase-32-close.ps1` was deleted. These items are closed-by-supersession (not
run). The replacement gate is `scripts/phase-32.1-close.ps1` (a 3×GREEN + triple-SHA close gate with
no `skp:cancelled:*` scan-clean — none are written anymore), tracked under Phase 32.1's UAT.

## Current Test

[none — superseded by Phase 32.1 revert]

## Tests

### 1. Live breaker-trip → halt → resume E2E
expected: With the full v3.6.0 compose stack up (rebuilt containers), `CancelledCircuitBreakerE2ETests` drives a workflow to infra-exhaustion; the breaker trips (no-TTL `skp:cancelled:{workflowId:D}` marker set, TTL == -1), the Quartz job is unscheduled (no new `skp:data:*` across a >1-cron-minute window), NO `Cancelled` ExecutionResult lands on `orchestrator-result` (halt is via the Fault fan-out, not a Cancelled result), `_error` still receives the dead-letter; then resume (clear marker + remove poison + re-`POST /orchestration/start`) re-fires the workflow to a fresh output key.
result: [superseded by Phase 32.1 — breaker reverted; CancelledCircuitBreakerE2ETests deleted, this test no longer applies]

### 2. phase-32-close.ps1 triple-SHA close gate (GATE_EXIT=0)
expected: `pwsh -NoProfile -File ./scripts/phase-32-close.ps1` runs 3×GREEN + Release/Debug 0-warning builds + triple-SHA BEFORE==AFTER. The explicit `skp:cancelled:*` scan-clean between settle-drain and the AFTER snapshot keeps the no-TTL marker from drifting the redis SHA. Read GATE_*_EXIT from the gate output (NOT the bg-task wrapper exit).
result: [superseded by Phase 32.1 — phase-32-close.ps1 deleted; replaced by phase-32.1-close.ps1 (no skp:cancelled:* scan-clean)]

## Operator runbook

1. `docker compose up -d --build processor-sample orchestrator baseapi-service`  (embedded SourceHash must match — rebuild required)
2. `dotnet test tests/BaseApi.Tests -- --filter-class "*CancelledCircuitBreakerE2ETests"`  → expect GREEN
3. `pwsh -NoProfile -File ./scripts/phase-32-close.ps1`  → expect GATE_EXIT=0 (triple-SHA BEFORE==AFTER incl. skp:flag:* / skp:data:* / skp:cancelled:* settled)

On GATE_EXIT=0: req-5 / req-8-live / req-6-data flip to complete; run `/gsd-execute-phase 32` (re-discovers, finds nothing incomplete) or mark the phase complete to close it.

## Summary

total: 2
passed: 0
issues: 0
pending: 0
skipped: 2
blocked: 0

## Gaps
