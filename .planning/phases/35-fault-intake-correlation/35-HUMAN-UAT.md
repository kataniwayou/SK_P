---
status: partial
phase: 35-fault-intake-correlation
source: [35-VERIFICATION.md]
started: 2026-06-05T00:00:00Z
updated: 2026-06-05T00:00:00Z
---

## Current Test

[awaiting human testing — SC3 live operator run]

## Tests

### 1. SC3 — Running Keeper container emits a correlated Elasticsearch log end-to-end
expected: A live WRONGTYPE trip publishes a real `Fault<EntryStepDispatch>`; the REBUILT Keeper container consumes it off `keeper-fault-recovery` and `PollEsForLog` returns a hit with `resource.attributes.service.name = "keeper"`, `attributes.CorrelationId` == the tripped dispatch's correlationId, `attributes.StepId` == the tripped step, and `body.text` matching "keeper fault intake". Net-zero `skp:*` after teardown.

run:
1. `docker compose up -d --build keeper processor-sample orchestrator baseapi-service` (Keeper MUST be rebuilt — a stale container runs the Phase-34 placeholder and emits no intake log — Pitfall 5)
2. Wait for all four services Healthy
3. `dotnet test tests/BaseApi.Tests -- --filter-class "*KeeperFaultIntakeE2ETests"`

result: [pending]

## Summary

total: 1
passed: 0
issues: 0
pending: 1
skipped: 0
blocked: 0

## Gaps
