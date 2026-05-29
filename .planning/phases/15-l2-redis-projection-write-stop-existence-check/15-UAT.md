---
status: complete
phase: 15-l2-redis-projection-write-stop-existence-check
source:
  - 15-01-SUMMARY.md
  - 15-02-SUMMARY.md
  - 15-03-SUMMARY.md
  - 15-04-SUMMARY.md
  - 15-05-SUMMARY.md
started: "2026-05-29T15:40:00Z"
updated: "2026-05-29T15:45:00Z"
closed_reason: "User declined manual conversational UAT — all deliverables covered by the automated suite (227/227 GREEN against the live compose stack) and the earlier human-signoff items in 15-HUMAN-UAT.md (status: passed)."
---

## Current Test

[testing complete — user declined manual UAT; deliverables covered by automated suite]

## Tests

### 1. Cold Start Smoke Test
expected: real service boots with no DI-resolution errors; readiness green; primary call works.
result: skipped
reason: User declined manual UAT. DI graph (IConnectionMultiplexer / IHttpContextAccessor / IRedisL2Cleanup singletons) is resolved on every integration-test webapp boot (227/227 GREEN).

### 2. Start projects a workflow into Redis (L2)
expected: POST /start → 204; 3 camelCase keyspaces (root/step/processor) populated.
result: skipped
reason: Covered by RedisProjectionWriterFacts + StartLoopFacts (GREEN).

### 3. Processor key has a TTL; root/step keys do not
expected: processor key ~100-day TTL; root/step TTL -1.
result: skipped
reason: Covered by RedisProjectionWriterFacts (TTL assertions, GREEN).

### 4. Re-Start is idempotent and shrink-safe
expected: re-Start → 204; no orphan per-step keys after a shrink.
result: skipped
reason: Covered by StartCleanupFacts / StartLoopFacts (ReStart_Removes_Orphan_Step, GREEN).

### 5. Stop deletes root + step keys, keeps processor keys
expected: Stop (all exist) → 204; root+step gone, processor retained.
result: skipped
reason: Covered by StopGateFacts.Stop_AllExist_204 + StopCleanupFacts (GREEN).

### 6. Stop on a missing workflow → 422 with the full missing list
expected: 422 problem+json listing all missing ids; no deletion.
result: skipped
reason: Covered by StopGateFacts.Stop_Missing_422_NoDelete (GREEN).

### 7. Repeated Stop → 422 (non-idempotent by design)
expected: second Stop of same id → 422.
result: skipped
reason: Covered by StopGateFacts.Stop_Repeat_422 (GREEN).

### 8. Redis-down → 500 with op name, no connection-string leak
expected: 500 problem+json with redisOp + correlationId, no connection string/stack.
result: skipped
reason: Covered by StartLoopFacts.Start_RedisDown_500, StopGateFacts.Stop_RedisDown_500 + Stop_RedisDown_OnPostGateCleanup_500_KeyExistsAsync (GREEN).

### 9. Correlation id flows to Elasticsearch
expected: X-Correlation-Id on a Start appears in an ES log doc.
result: skipped
reason: Covered by OrchestrationLogsE2ETests (live OTLP→collector→ES round-trip, GREEN).

### 10. Invalid workflow graph → 422 (per-workflow first failure)
expected: cycle/schema-edge/payload violation → 422 naming the gate; per-workflow first-failure.
result: skipped
reason: Covered by ValidationOrderFacts + Phase 14 CycleDetection/SchemaEdge/PayloadConfigSchema facts (GREEN).

## Summary

total: 10
passed: 0
issues: 0
pending: 0
skipped: 10
blocked: 0

## Gaps

[none — no issues reported; manual UAT declined, automated coverage stands in]
