---
status: complete
phase: 14-validation-gates-dfs-schema-edge-payload-config-schema
source: [14-VERIFICATION.md]
started: 2026-05-29
updated: 2026-05-29
---

## Current Test

[testing complete]

## Tests

### 1. Full integration suite passes against live services
expected: `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` with the Postgres + Redis docker compose stack up passes 194/194 — including all Phase 14 gate facts (cycle, missing-step, schema-edge, payload-config-schema, validation-order), the Phase 9 baseline, the SSRF `<500ms` guard, and the Assignment valid-JSON-only invariant.
result: pass
note: |
  Verified against the live stack (Postgres + Redis + Elasticsearch + Prometheus + OTel, all healthy).
  Full suite confirmed 194/194 GREEN on a clean re-run (Release, ~3min).

  Observed a one-off NON-BLOCKING timing flake on the FIRST full run: `ErrorMappingFacts.Create_Schema_Invalid_JsonSchema_Returns400_NoOutboundCall`
  failed its `<500ms` SSRF wall-clock assertion at 957ms. Investigated and ruled out as a defect:
    - Functionally correct in the failing run (HTTP 400, no seconds-long network timeout → SSRF Fetch=null lockdown intact).
    - Passed deterministically on 2 isolated re-runs and on a full-suite re-run (194/194).
    - JsonSchemaConfig static-ctor/JIT cost was already amortized by earlier valid-schema creates before this test ran.
    - Phase 14 changed PayloadConfigSchemaValidator/CycleDetector, NOT the create-side SchemaDtoValidator path this test exercises.
  Root cause: the `<500ms` assertion measures wall-clock of a full HTTP round-trip and is sensitive to CPU/scheduling
  contention during the 16-worker parallel run on a loaded machine. Pre-existing test-robustness issue, not a Phase 14 regression.
  Optional future hardening (not required for this phase): add a warmup call, retry-once, or a more generous threshold to that assertion.

## Summary

total: 1
passed: 1
issues: 0
pending: 0
skipped: 0
blocked: 0

## Gaps

[none]
