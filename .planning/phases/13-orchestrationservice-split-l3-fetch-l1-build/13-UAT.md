---
status: complete
phase: 13-orchestrationservice-split-l3-fetch-l1-build
source: [13-01-SUMMARY.md, 13-02-SUMMARY.md, 13-03-SUMMARY.md]
started: 2026-05-29T09:35:38Z
updated: 2026-05-29T09:38:00Z
---

## Current Test

[testing complete]

## Tests

### 1. Cold Start Smoke Test
expected: After the OrchestrationService DI refactor (internal ctor + factory-lambda registration), the API boots cleanly from cold start — no ValidateOnBuild/DI activation exception, migrations complete, GET /health/ready returns 200 healthy.
result: pass

### 2. Start with valid workflow ids → 204
expected: POST /api/v1/orchestration/start with a body of one or more EXISTING workflow GUIDs returns 204 No Content with an empty body. (Internally an L1 snapshot is built from Postgres and immediately disposed — there is no Redis write or other external side effect yet; that lands in Phase 15. So 204 + empty body is the full observable outcome.)
result: skipped
reason: Manual human verification waived by user. Covered by automated integration fact `Start_Returns204_AndEmptyBody_WhenWorkflowIdsValid` (green, 181/181).

### 3. Start with unknown workflow id → 404
expected: POST /api/v1/orchestration/start with a body containing a well-formed but non-existent workflow GUID returns 404 with an RFC 7807 problem+json body, a correlationId, and the missing id(s) echoed (comma-joined if multiple). No DB internals or PII in the body.
result: skipped
reason: Manual human verification waived by user. Covered by automated facts `Start_Returns404_WhenAnyWorkflowIdMissing` + `Start_Returns404_WithCommaJoinedIds_WhenMultipleWorkflowIdsMissing` (green).

### 4. Start with invalid body → 400
expected: POST /api/v1/orchestration/start with a malformed body — empty list, a duplicate GUID, or Guid.Empty (all-zeros) — returns 400 (validation failure from WorkflowIdsValidator) with an RFC 7807 body and correlationId. This contract is unchanged from Phase 9.
result: skipped
reason: Manual human verification waived by user. Covered by automated facts `Start_Returns400_When{WorkflowIdsEmpty,WorkflowIdsContainDuplicate,WorkflowIdsContainsGuidEmpty}` (green).

### 5. Stop existence check → 204 / 404
expected: POST /api/v1/orchestration/stop with EXISTING workflow GUIDs returns 204 No Content; with any non-existent GUID returns 404 with the missing id(s) in the RFC 7807 body. No DELETE/eviction occurs (v3.3.0 Stop is existence-check only).
result: skipped
reason: Manual human verification waived by user. Covered by automated facts `Stop_Returns204_AndEmptyBody_WhenWorkflowIdsValid` + `Stop_Returns404_...` (green).

## Summary

total: 5
passed: 1
issues: 0
pending: 0
skipped: 4
blocked: 0

## Gaps

[none yet]
