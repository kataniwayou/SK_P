---
status: partial
phase: 36-l2-health-probe-recovery-loop-dlqs
source: [36-VERIFICATION.md]
started: 2026-06-06T01:00:00Z
updated: 2026-06-06T01:00:00Z
---

## Current Test

[awaiting human testing]

## Tests

### 1. Live recover-both-paths (KeeperRecovery_RecoversBothPaths)
expected: Rebuild all containers (keeper + processor-sample + orchestrator + baseapi-service), then `dotnet test tests/BaseApi.Tests -- --filter "Category=RealStack&FullyQualifiedName~KeeperRecovery_RecoversBothPaths"` is GREEN — `CountEsHitsAsync == 1` (exactly-once for the dispatch re-inject) and the verbatim `ExecutionResult` re-inject is caught on `queue:orchestrator-result` with matching `H` + `CorrelationId`.
result: [pending]

### 2. Live give-up park (KeeperRecovery_GivesUp_ParksToDlq)
expected: `dotnet test tests/BaseApi.Tests -- --filter "Category=RealStack&FullyQualifiedName~KeeperRecovery_GivesUp"` (optionally `Probe__MaxAttempts=2` on the keeper container to shorten the 60s loop) is GREEN — the ORIGINAL `Fault<ExecutionResult>` envelope (not the bare inner) arrives on `queue:keeper-dlq`, is ack-drained by the in-test probe, and `keeper-dlq` + `skp:keeper:probe:*` are net-zero post-test.
result: [pending]

### 3. (Optional / Manual-Only) Kill-mid-loop crash redelivery (PROBE-05)
expected: Trip a fault, `docker kill keeper` while the probe loop is mid-await, observe redelivery + loop restart in logs/ES — the redelivered `Fault<T>` is re-processed by a new Keeper replica, the loop restarts from the beginning, no message is lost, and the downstream effect fires exactly once (at-least-once delivery + idempotent effect).
result: [pending]

## Summary

total: 3
passed: 0
issues: 0
pending: 3
skipped: 0
blocked: 0

## Gaps
