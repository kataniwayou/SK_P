---
phase: 14-validation-gates-dfs-schema-edge-payload-config-schema
plan: 04
subsystem: orchestration-validation-gates
tags: [json-schema, payload-config, 422, ssrf, per-start-cache, validation-gate]
requires:
  - 14-01 (OrchestrationValidationException.PayloadConfigSchema factory + JsonSchemaConfig.DefaultOptions)
  - Phase 13 OrchestrationService pipeline (already invokes PayloadConfigSchemaValidator at step 5)
  - Phase 13 WorkflowGraphSnapshot (Assignments/Steps/Processors/Schemas dictionaries)
provides:
  - PayloadConfigSchemaValidator.Validate filled (was no-op stub) — closes deferred VALID-21 at orchestration-start
  - Payload↔ConfigSchema 422 gate (gate="payloadConfigSchema", offending { assignmentId, errors[] })
  - per-Start LOCAL JsonSchema parse cache (L1-VALIDATE-08)
affects:
  - 14-05 (phase-close gate run — this validator now active in the pipeline)
tech-stack:
  added: []
  patterns:
    - "Per-Start LOCAL Dictionary<Guid,JsonSchema> cache (never an instance field — DI-Scoped seam, no cross-request leak)"
    - "JsonSchema.Net evaluation via the single SSRF source of truth JsonSchemaConfig.DefaultOptions (cctor-fired on reference)"
    - "Nullable results.Details guarded with Enumerable.Empty fallback (JsonSchema.Net 9.2.1)"
key-files:
  created:
    - tests/BaseApi.Tests/Features/Orchestration/PayloadConfigSchemaFacts.cs
  modified:
    - src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs
decisions:
  - "D-10 implemented: cache is a LOCAL inside Validate (not an instance field) keyed by Schema.Id; null ConfigSchemaId passes; missing step/processor/schema skipped defensively (prior gates own those)."
  - "Cache-parse-once asserted behaviorally (two same-schema assignments both validate in one Start → 204) per plan guidance — no production instrumentation added solely for the test."
metrics:
  duration: ~6min
  completed: 2026-05-29
  tasks: 2
  files: 2
---

# Phase 14 Plan 04: Payload↔ConfigSchema Validation Gate Summary

One-liner: Filled the `PayloadConfigSchemaValidator.Validate` seam to evaluate every Assignment's Payload against its resolved `StepId→ProcessorId→ConfigSchemaId→Schema.Definition` JSON Schema via JsonSchema.Net + the SSRF-locked `JsonSchemaConfig.DefaultOptions`, caching each parse once per-Start (LOCAL dict, no cross-request leak), null ConfigSchemaId passing, and throwing `PayloadConfigSchema(assignmentId, flattenedErrors)` → HTTP 422 on a conformance failure — closing the deferred VALID-21 at orchestration-start.

## What Was Built

- **Task 1 (feat) — validator body:** Replaced the no-op `Validate(WorkflowGraphSnapshot)` with: a per-Start LOCAL `var schemaCache = new Dictionary<Guid, JsonSchema>()`; a `foreach` over `snapshot.Assignments.Values` resolving `StepId → ProcessorId → ConfigSchemaId`; defensive `TryGetValue` skips for missing step/processor/schema (those are prior gates' concern); `cfgId is null → continue` (null ConfigSchemaId passes); cache-keyed `JsonSchema.FromText(schemaDto.Definition)` (parsed once per Schema.Id); `JsonDocument.Parse(payload)` in try/finally-Dispose mirroring `SchemaDtoValidator`; `schema.Evaluate(payloadDoc.RootElement, JsonSchemaConfig.DefaultOptions)` (SSRF-locked, OutputFormat.List); flatten `results.Details` (guarded nullable) then fallback to `results.Errors`; `throw OrchestrationValidationException.PayloadConfigSchema(assignment.Id, errorStrings)` on `!IsValid`. Class/signature unchanged (`internal sealed`, `public void Validate`).
- **Task 2 (test) — PayloadConfigSchemaFacts:** New integration test (`[Trait("Phase","14")]`, `IClassFixture<Phase8WebAppFactory>`) mirroring `SchemaEdgeFacts` seeding idiom (Schema → Processor → Step → Assignment → Workflow via the public HTTP API). Three facts: `BadPayload_Returns422_WithAssignmentIdAndErrors` (`{"foo":123}` vs string-`foo` schema → 422, `gate="payloadConfigSchema"`, `offending.assignmentId`, non-empty `offending.errors[]`); `NullConfigSchemaId_Passes` (ConfigSchemaId=null → 204); `SameSchema_TwoAssignments_BothValidated_Returns204` (one schema, two steps, two valid-payload assignments → 204, exercising the cache hit path).

## Verification

- `dotnet build` Debug + Release: exit 0, **0 warnings** (TreatWarningsAsErrors).
- Whole-assembly test run: **190/190 GREEN** (3m05s). MTP ignores `--filter` (warning MTP0001) and runs the full assembly, so the run is a strict superset covering the plan's required filters — `PayloadConfigSchemaFacts` (3 new), `ErrorMappingFacts` (SSRF `<500ms` guard — JsonSchemaConfig lockdown still holds), and `Phase=9` orchestration facts — all GREEN.
- Acceptance-criteria greps confirmed in `PayloadConfigSchemaValidator.cs`: `JsonSchema.FromText`, `JsonSchemaConfig.DefaultOptions`, `var schemaCache = new Dictionary<Guid, JsonSchema>()` (local), `OrchestrationValidationException.PayloadConfigSchema`, `cfgId is null` all present; **zero** class-scope `private ... Dictionary<Guid, JsonSchema>` field (cache is a local only).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Guard nullable `results.Details` (JsonSchema.Net 9.2.1)**
- **Found during:** Task 1 (Debug build CS8604 — `Possible null reference argument for parameter 'source'` on `results.Details.Where(...)`).
- **Issue:** In JsonSchema.Net 9.2.1 `EvaluationResults.Details` is a nullable list; under `TreatWarningsAsErrors=true` the `.Where(...)` call on a possibly-null receiver is build-fatal. The plan's flatten snippet (RESEARCH lines 177-190) assumed a non-null `Details`.
- **Fix:** Wrapped the receiver with `(results.Details ?? Enumerable.Empty<EvaluationResults>())` before `.Where(...)`. Behavior is identical when `Details` is populated; when null the flatten falls through to the existing `results.Errors` fallback branch (the plan already guards `results.Errors` as nullable).
- **Files modified:** src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs
- **Commit:** e4bff78

## TDD Gate Compliance

Task 1 is marked `tdd="true"` but the plan orders the test as a separate Task 2 (the validator's `<verify>` references the Task-2 facts). Executed as: validator implemented first (feat commit e4bff78, build-verified zero-warning), then the facts authored (test commit b728be0) and the full assembly run GREEN. The pipeline seam (`OrchestrationService` step 5) already invoked the validator from Phase 13, so the facts drive the live HTTP path end-to-end. No standalone RED commit was authored because the plan's task structure places implementation and test in distinct tasks with the GREEN gate being the post-implementation 190/190 run; this matches the plan-as-written ordering.

## Threat Surface

No new threat surface beyond the plan's `<threat_model>`. T-14-09 (SSRF) mitigated — evaluation routes through `JsonSchemaConfig.DefaultOptions` (global no-op fetcher, no outbound `$ref`). T-14-10 (unbounded re-parse) mitigated — per-Start LOCAL `Dictionary<Guid,JsonSchema>` parses each schema once, never an instance field (no cross-request leak). T-14-11 (422 info-disclosure) accepted as planned — `offending` carries only `assignmentId` + flattened JsonSchema.Net conformance messages (instance-location + keyword text), no raw payload/internals.

## Commits

- e4bff78 `feat(14-04): fill PayloadConfigSchemaValidator (JsonSchema.Net evaluate + per-Start cache)`
- b728be0 `test(14-04): PayloadConfigSchemaFacts (bad payload 422; null passes; same-schema two assignments 204)`

## Self-Check: PASSED

Created file `tests/BaseApi.Tests/Features/Orchestration/PayloadConfigSchemaFacts.cs` present; modified file `PayloadConfigSchemaValidator.cs` present with all acceptance-criteria tokens; commits e4bff78 + b728be0 present in git log.
