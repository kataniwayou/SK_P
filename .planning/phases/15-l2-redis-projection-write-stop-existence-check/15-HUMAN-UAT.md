---
status: passed
phase: 15-l2-redis-projection-write-stop-existence-check
source: [15-VERIFICATION.md]
started: "2026-05-29T14:30:00Z"
updated: "2026-05-29T15:10:00Z"
---

## Current Test

[all items verified against live stack]

## Tests

### 1. Full integration test suite (227/227 GREEN against live stack)
expected: All tests pass against the live docker-compose stack (redis, postgres, elasticsearch, otel-collector).
result: PASS — `dotnet test SK_P.sln -c Release` ran 3× this session against the live compose stack; final run **227 passed, 0 failed, 0 skipped** (3m15s). (227 = prior 226 + the new WR-01 locking fact.)

### 2. OBSERV-REDIS-02 E2E (X-Correlation-Id round-trips to Elasticsearch)
expected: `OrchestrationLogsE2ETests` passes — a real Start round-trips X-Correlation-Id to Elasticsearch in under 30s.
result: PASS — included in the 227/227 suite runs above.

### 3. redis-cli scan BEFORE=AFTER (zero residual test keys)
expected: Zero residual `test:cls-*` keys after the suite.
result: PASS — the 9 residual `test:cls-deadredis:*` keys originally found were stale debris from the abandoned dead-port experiment (replaced with in-memory doubles in 15-04). After deleting them, the full suite was run and re-scanned: **0 residual before AND 0 after**. The committed suite does not leak — `RedisFixture` SCAN+DEL teardown sweeps Guid-prefixed keys; dead-Redis faults use hand-rolled stubs that never touch live Redis. No committed-code teardown bug.

## Summary

total: 3
passed: 3
issues: 0
pending: 0
skipped: 0
blocked: 0

## Gaps

(none)

## Notes

- WR-01 (Stop post-gate cleanup missing `redisOp` tag) was fixed in commit 44dd29a with a locking fact; see 15-REVIEW.md.
- WR-02 (CancellationToken not observed in BFS) and WR-03 (`.Result` pattern) remain open as advisory low-severity items in 15-REVIEW.md.
