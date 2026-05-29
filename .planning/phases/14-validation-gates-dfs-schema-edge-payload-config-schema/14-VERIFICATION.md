---
phase: 14-validation-gates-dfs-schema-edge-payload-config-schema
verified: 2026-05-29T00:00:00Z
status: verified
score: 5/5 must-haves verified
overrides_applied: 0
human_verification:
  - test: "Run the full integration test suite (dotnet test) with Postgres + Redis compose stack up"
    expected: "194/194 tests pass — the SUMMARY claims this result from the live execution run"
    why_human: "Cannot re-run the suite without live Docker services; test evidence is the 14-05 SUMMARY's 194/194 claim, which has not been independently reproduced in this verification session"
    resolution: "RESOLVED — human verification completed in 14-HUMAN-UAT.md (status: complete): full suite ran 194/194 GREEN on a clean Release re-run against the live stack; a one-off <500ms SSRF timing flake was investigated and ruled out as a non-defect. Reconfirmed by the Phase 16 close gate (3× consecutive GREEN at 235 passed, dual-SHA Postgres/Redis BEFORE==AFTER held)."
---

# Phase 14: Validation Gates (DFS + Schema-Edge + Payload-Config-Schema) Verification Report

**Phase Goal:** A broken workflow Start request returns a deterministic 422 + RFC 7807 at the first failed gate, with the offending entity ids in the error body and L1 cleanup guaranteed even on validation failure. Closes v2-deferred VALID-21 at orchestration-start scope.
**Verified:** 2026-05-29T00:00:00Z
**Status:** human_needed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #   | Truth | Status | Evidence |
| --- | ----- | ------ | -------- |
| 1 | A cycle-containing workflow returns 422 with offending stepId chain; cycle detection uses iterative DFS with explicit stack (no recursion) | ✓ VERIFIED | `CycleDetector.cs` uses `Stack<(Guid, IEnumerator<Guid>)>` + `onStack` HashSet + `fullyVisited` HashSet; `OrchestrationValidationException.Cycle(cycleChain)` thrown on back-edge; no recursive call anywhere; `CycleDetectionFacts` (integration) + `MissingStepFacts` (white-box) cover it |
| 2 | A workflow whose Step references a missing NextStepId returns 422 with (parentStepId, missingChildId); null NextStepId passes as terminal | ✓ VERIFIED | `CycleDetector.cs` throws `MissingStep(currentStep, child)` when `!snapshot.Steps.ContainsKey(child)`; null/empty NextStepIds yields empty enumerator → immediate pop → no throw; `MissingStepFacts.TerminalNullNextStepIds_Passes` covers null+empty |
| 3 | A schema-edge mismatch (parent.OutputSchemaId != child.InputSchemaId) returns 422 with (parentStepId, childStepId); null on either side passes; every NextStepIds entry checked | ✓ VERIFIED | `SchemaEdgeValidator.cs` iterates all `parent.NextStepIds ?? Enumerable.Empty<Guid>()`; strict `parentOut.Value != childIn.Value` throws `SchemaEdge(parent.Id, child.Id)`; `parentOut is null || childIn is null` continues; `SchemaEdgeFacts` (both facts) verify |
| 4 | An Assignment whose Payload fails its resolved ConfigSchema returns 422 with assignmentId + errors; per-Start Dictionary<Guid,JsonSchema> cache parses each schema at most once; SSRF-locked options used | ✓ VERIFIED | `PayloadConfigSchemaValidator.cs` has `var schemaCache = new Dictionary<Guid, JsonSchema>()` declared LOCAL inside `Validate`; `JsonSchema.FromText` + `JsonSchemaConfig.DefaultOptions`; throws `PayloadConfigSchema(assignment.Id, errorStrings)`; `PayloadConfigSchemaFacts` (3 facts) verify; SSRF lockdown confirmed by `JsonSchemaConfig.cs` static ctor owning `SchemaRegistry.Global.Fetch = (_,_) => null` |
| 5 | Validation runs in the exact order existence → cycles → schema-edge → Payload↔ConfigSchema; first-gate short-circuit proven by multi-failure workflows; L1 cleanup (snapshot.Dispose) runs in finally on every validation failure | ✓ VERIFIED | `OrchestrationService.StartAsync` calls gates at steps 3/4/5 in order; `ValidationOrderFacts` provides 4 facts: `ExistenceBeforeCycle_MissingWorkflowId_Returns404_NotCycle`, `CycleBeforeSchemaEdge_WorkflowFailingBoth_Returns422Cycle`, `SchemaEdgeBeforePayload_WorkflowFailingBoth_Returns422SchemaEdge`, `L1Cleanup_RunsOnValidationFailurePath` (asserts `recorder.Captured.IsDisposed == true` + all 5 dicts empty on 422 path) |

**Score:** 5/5 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
| -------- | -------- | ------ | ------- |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs` | Single domain exception, 4 static factories, gate discriminators, ErrorsExtension | ✓ VERIFIED | 87 lines; `sealed class` with private ctor, 4 factories (`Cycle`/`MissingStep`/`SchemaEdge`/`PayloadConfigSchema`), co-located `sealed record` offending types, `ErrorsExtension => new { gate, offending }` |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationExceptionHandler.cs` | IExceptionHandler → 422, fast-bail, no correlationId/instance setting | ✓ VERIFIED | 56 lines; fast-bail `if (exception is not OrchestrationValidationException ex) return false;` as first statement; `Status422UnprocessableEntity`; sets only Status/Title/Detail/errors; no correlationId/instance |
| `src/BaseApi.Service/Features/Schema/JsonSchemaConfig.cs` | Shared SSRF-locked static config + DefaultOptions | ✓ VERIFIED | 35 lines; static ctor sets `Dialect.Default = Dialect.Draft202012` and `SchemaRegistry.Global.Fetch = (_, _) => null`; `DefaultOptions = new() { OutputFormat = OutputFormat.List }` |
| `src/BaseApi.Core/DependencyInjection/ErrorHandlingServiceCollectionExtensions.cs` | Split-Fallback: AddBaseApiFallbackHandler() as separate public method; FallbackExceptionHandler removed from AddBaseApiErrorHandling | ✓ VERIFIED | `AddBaseApiErrorHandling` registers only NotFound/Validation/DbUpdate; `public static AddBaseApiFallbackHandler` registers FallbackExceptionHandler separately; class promoted to `public static` for cross-assembly visibility |
| `src/BaseApi.Service/Program.cs` | AddBaseApiFallbackHandler called LAST after AddAppFeatures | ✓ VERIFIED | Line 9: `builder.Services.AddBaseApiFallbackHandler();` placed after `AddAppFeatures()` and before `builder.Build()` |
| `src/BaseApi.Service/Features/Orchestration/Validation/CycleDetector.cs` | Two-set iterative DFS, no recursion, throws Cycle/MissingStep | ✓ VERIFIED | 131 lines; `Stack<(Guid Step, IEnumerator<Guid> Children)>`, `onStack HashSet<Guid>`, `fullyVisited HashSet<Guid>`; `RunDfs` and `Push` helper methods — neither calls itself; no recursive self-calls |
| `src/BaseApi.Service/Features/Orchestration/Validation/SchemaEdgeValidator.cs` | Independent edge walk, strict Guid equality, null-passes | ✓ VERIFIED | 62 lines; iterates all `parent.NextStepIds`; `parentOut is null || childIn is null → continue`; `parentOut.Value != childIn.Value → throw SchemaEdge`; zero references to CycleDetector |
| `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs` | JsonSchema.Net evaluate + per-Start local cache + SSRF-locked options | ✓ VERIFIED | 75 lines; `var schemaCache = new Dictionary<Guid, JsonSchema>()` LOCAL; `JsonSchema.FromText`; `JsonSchemaConfig.DefaultOptions` reference; `throw PayloadConfigSchema`; no class-scope cache field |
| `tests/BaseApi.Tests/Features/Orchestration/CycleDetectionFacts.cs` | True-cycle → 422; diamond DAG → 204 (no false-positive) | ✓ VERIFIED | File present; `[Trait("Phase", "14")]`; `Cycle_Returns422_WithStepChain` and `DiamondDag_Passes_NoFalsePositiveCycle` |
| `tests/BaseApi.Tests/Features/Orchestration/MissingStepFacts.cs` | White-box crafted snapshot, missing-step throws, terminal-null passes | ✓ VERIFIED | File present; `MissingNextStep_Throws_WithParentAndMissingChild` and `TerminalNullNextStepIds_Passes` |
| `tests/BaseApi.Tests/Features/Orchestration/SchemaEdgeFacts.cs` | Mismatch → 422; null-side → 204 | ✓ VERIFIED | File present; `SchemaEdgeMismatch_Returns422_WithParentAndChild` and `SchemaEdgeNullSide_Passes` |
| `tests/BaseApi.Tests/Features/Orchestration/PayloadConfigSchemaFacts.cs` | Bad payload → 422; null ConfigSchemaId → 204; two same-schema assignments → 204 | ✓ VERIFIED | File present; all 3 facts present including `SameSchema_TwoAssignments_BothValidated_Returns204` |
| `tests/BaseApi.Tests/Features/Orchestration/ValidationOrderFacts.cs` | Gate-order short-circuit proof + L1 cleanup on 422 path | ✓ VERIFIED | File present; all 4 facts present; `RecordingWorkflowGraphLoader` inner wrapper + `ConfigureTestServices` pattern for L1 disposal assertion |

### Key Link Verification

| From | To | Via | Status | Details |
| ---- | -- | --- | ------ | ------- |
| `OrchestrationServiceCollectionExtensions.cs` | `OrchestrationValidationExceptionHandler` | `AddExceptionHandler<OrchestrationValidationExceptionHandler>()` registered before split-Fallback | ✓ WIRED | Line 69: `services.AddExceptionHandler<OrchestrationValidationExceptionHandler>()` inside `AddOrchestrationFeature` with D-04 ordering comment |
| `Program.cs` | `AddBaseApiFallbackHandler` | Called last after `AddAppFeatures()` | ✓ WIRED | Line 9: `builder.Services.AddBaseApiFallbackHandler()` after `AddAppFeatures()` |
| `SchemaDtoValidator.cs` | `JsonSchemaConfig` | `JsonSchemaConfig.DefaultOptions` reference fires static ctor (SSRF lockdown) | ✓ WIRED | grep confirms 2 hits of `JsonSchemaConfig.DefaultOptions` in `SchemaDtoValidator.cs`; ZERO hits of `SchemaRegistry.Global.Fetch` or `Dialect.Default` (lockdown moved out) |
| `CycleDetector.cs` | `OrchestrationValidationException.Cycle` / `.MissingStep` | `throw` on cycle / missing-step | ✓ WIRED | Both `OrchestrationValidationException.Cycle(cycleChain)` and `OrchestrationValidationException.MissingStep(...)` throws present |
| `SchemaEdgeValidator.cs` | `snapshot.Processors` | Resolves parent/child ProcessorId → Output/InputSchemaId | ✓ WIRED | `snapshot.Processors.TryGetValue(parent.ProcessorId, ...)` present; `OrchestrationValidationException.SchemaEdge` throw present |
| `PayloadConfigSchemaValidator.cs` | `JsonSchemaConfig.DefaultOptions` | `schema.Evaluate(payloadDoc.RootElement, JsonSchemaConfig.DefaultOptions)` | ✓ WIRED | Direct reference on line 57; SSRF cctor fires on this reference |
| `OrchestrationService.StartAsync` | All 3 validators + L1 cleanup | `using var snapshot = await _loader.LoadL1Async(...)` + 3 `Validate(snapshot)` calls | ✓ WIRED | Steps 3/4/5 wired in order; `using` declaration guarantees `Dispose()` on success and throw |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
| -------- | ------------- | ------ | ------------------ | ------ |
| `CycleDetector.Validate` | `snapshot.Workflows`, `snapshot.Steps` | L1 snapshot populated by `WorkflowGraphLoader.LoadL1Async` (Phase 13) from real Postgres via `BaseDbContext.Set<>().AsNoTracking()` batch queries | Yes — real Postgres rows | ✓ FLOWING |
| `SchemaEdgeValidator.Validate` | `snapshot.Steps`, `snapshot.Processors` | Same L1 snapshot from Postgres | Yes | ✓ FLOWING |
| `PayloadConfigSchemaValidator.Validate` | `snapshot.Assignments`, `snapshot.Steps`, `snapshot.Processors`, `snapshot.Schemas` | Same L1 snapshot from Postgres | Yes | ✓ FLOWING |

### Behavioral Spot-Checks

Step 7b skipped — requires live Postgres + Redis compose stack to run the HTTP integration tests (the test suite is not runnable without running services). Test pass evidence taken from SUMMARY claims (194/194 GREEN) and code-level verification.

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
| ----------- | ----------- | ----------- | ------ | -------- |
| L1-VALIDATE-01 | 14-01, 14-05 | Validation runs in exact order: existence → cycles → schema-edge → Payload↔ConfigSchema; 422 on any gate failure; L1 cleanup in finally | ✓ SATISFIED | Gate order locked in `OrchestrationService.StartAsync` steps 1-5; `using var snapshot` guarantees cleanup; `ValidationOrderFacts` proves order and cleanup end-to-end |
| L1-VALIDATE-02 | 14-05 | Existence gate: re-uses ValidateWorkflowIdsAsync; missing WorkflowIds → 422 (implementation decision: stays 404 per D-13) | ✓ SATISFIED | `ExistenceCheckAsync` in `OrchestrationService` throws `NotFoundException → 404`; STATE.md documents D-13 decision that existence stays 404; `ValidationOrderFacts.ExistenceBeforeCycle_MissingWorkflowId_Returns404_NotCycle` verifies |
| L1-VALIDATE-03 | 14-02 | Cycle detection gate: iterative DFS with explicit stack, no recursion; cycle → 422 with stepId chain | ✓ SATISFIED | `CycleDetector.cs` full implementation with `Stack<(Guid, IEnumerator<Guid>)>`, two-set algorithm; no recursive calls; `CycleDetectionFacts.Cycle_Returns422_WithStepChain` verifies |
| L1-VALIDATE-04 | 14-02 | Missing next-step gate: NextStepId absent → 422 with (parentStepId, missingChildId); null NextStepId passes | ✓ SATISFIED | `CycleDetector.cs` throws `MissingStep(currentStep, child)` on absent child; null/empty enumerator pops immediately; `MissingStepFacts` verifies both cases |
| L1-VALIDATE-05 | 14-03 | Schema-edge compatibility: every NextStepIds entry checked, strict Guid equality, null-on-either-side passes, mismatch → 422 | ✓ SATISFIED | `SchemaEdgeValidator.cs` iterates all `parent.NextStepIds`; both null-pass and strict-equality-mismatch handled; independent of CycleDetector; `SchemaEdgeFacts` verifies both facts |
| L1-VALIDATE-06 | 14-04 | Payload↔ConfigSchema gate: validate payload against resolved ConfigSchema via JsonSchema.Net; failure → 422 with assignmentId + errors; null ConfigSchemaId passes | ✓ SATISFIED | `PayloadConfigSchemaValidator.cs` full implementation; `cfgId is null → continue`; `JsonSchema.FromText` + evaluate; `PayloadConfigSchemaFacts.BadPayload_Returns422_WithAssignmentIdAndErrors` and `NullConfigSchemaId_Passes` verify |
| L1-VALIDATE-07 | 14-01 | SSRF lockdown: new Payload validator uses `JsonSchemaConfig.DefaultOptions`; v3.2.0 SSRF defense must not regress | ✓ SATISFIED | `JsonSchemaConfig.cs` owns `SchemaRegistry.Global.Fetch = (_,_) => null` and `Dialect.Default = Draft202012`; `PayloadConfigSchemaValidator` references `JsonSchemaConfig.DefaultOptions`; `SchemaDtoValidator.cs` refactored to use `JsonSchemaConfig.DefaultOptions` (SSRF tokens removed from it); `ErrorMappingFacts` (<500ms) passed per SUMMARY |
| L1-VALIDATE-08 | 14-04 | Schema caching: per-Start `Dictionary<Guid, JsonSchema>` cache; each schema parsed at most once | ✓ SATISFIED | `var schemaCache = new Dictionary<Guid, JsonSchema>()` LOCAL in `Validate`; cache-keyed `JsonSchema.FromText`; no instance-level field; `SameSchema_TwoAssignments_BothValidated_Returns204` exercises the cache code path |
| L1-VALIDATE-09 | 14-05 | VALID-21 closes only at orchestration-start; Assignment PUT/POST remain valid-JSON-only | ✓ SATISFIED | No payload validation added to Assignment endpoints; STATE.md records decision; `AssignmentsIntegrationTests` passed per SUMMARY |
| L1-VALIDATE-10 | 14-05 | TEST-03/TEST-04 remain deferred; documented for traceability | ✓ SATISFIED | STATE.md has explicit "Plan 14-05: L1-VALIDATE-10 — TEST-03 (Testcontainers.PostgreSql) + TEST-04 (Respawn) remain DEFERRED" entry |
| ORCH-START-03 | 14-01, 14-05 | On any validation failure: 422 with RFC 7807 Problem Details, correlationId, instance, structured errors | ✓ SATISFIED | `OrchestrationValidationExceptionHandler` emits 422; sets `Title`/`Detail`/`errors`; Phase 4 `CustomizeProblemDetails` injects `correlationId` + `instance`; handler fast-bails on foreign exceptions |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
| ---- | ---- | ------- | -------- | ------ |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` | 79-82 | Comments say `// 3. no-op P13` / `// 4. no-op P13` / `// 5. no-op P13` for validators that are now fully implemented | ℹ️ Info | Stale documentation only — validator bodies are substantive (confirmed by reading the actual `.cs` files); does not affect runtime behavior |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs` | 43-46, 60-62 | `[ProducesResponseType]` attributes are missing the 422 entry for the Start/Stop endpoints | ⚠️ Warning | Swagger/OpenAPI will not document the 422 response shape; runtime behavior is correct (422 is emitted by the handler chain); ORCH-START-08 requirement (which would close this) is scoped to Phase 15, not Phase 14 |

**Code Review findings carried forward (from 14-REVIEW.md):**

| Finding | Severity | Impact |
| ------- | -------- | ------ |
| WR-01: `JsonSchema.FromText` unguarded — a malformed persisted Schema.Definition would surface as HTTP 500 instead of a domain 422 | ⚠️ Warning | Reachable only if a Schema row bypasses the create-time `SchemaCreateDtoValidator`; acceptable invariant assumption but undocumented in the validator |
| WR-02: `JsonDocument.Parse(assignment.Payload)` parse failure not translated — a non-JSON Payload would surface as HTTP 500 | ⚠️ Warning | Reachable only if Assignment create-side does not enforce "Payload must be valid JSON"; the reviewer notes this invariant is not visible from the reviewed files |
| WR-03: CycleDetector seeds only from EntryStepIds; SchemaEdgeValidator walks ALL snapshot.Steps — scope asymmetry | ⚠️ Warning | An orphaned subgraph with a cycle would pass the cycle gate but still be edge-walked by SchemaEdgeValidator; low impact as the FK-Restrict junction prevents dangling edges via the API |

None of WR-01 / WR-02 / WR-03 are blockers for the phase goal — the intended scenarios (workflow built via the standard API) work correctly.

### Human Verification Required

#### 1. Full integration suite GREEN (194/194)

**Test:** With Postgres + Redis compose stack running, execute `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj`
**Expected:** 194 tests pass, 0 failures; includes all Phase 14 gate facts (CycleDetectionFacts, MissingStepFacts, SchemaEdgeFacts, PayloadConfigSchemaFacts, ValidationOrderFacts), all Phase 9 orchestration baseline facts, ErrorMappingFacts (SSRF <500ms), and AssignmentsIntegrationTests (VALID-21 HTTP-write untouched)
**Why human:** Cannot re-run the integration test suite without live Docker services (Postgres + Redis). The 194/194 GREEN claim comes from the 14-05 SUMMARY and is plausible given all artifacts are verified substantive and wired, but independent reproduction requires human access to a running compose stack

---

## Gaps Summary

No gaps identified. All 5 roadmap success criteria are verified at the code level. The `human_needed` status is set solely because the 194-test suite cannot be reproduced programmatically without live Docker services, not because any artifact is missing, stubbed, or unwired.

The three code-review warnings (WR-01, WR-02, WR-03) and one stale comment are noted but do not block the phase goal. The 422 error path, iterative DFS cycle detection, schema-edge compatibility check, payload-config-schema validation, locked gate order, and L1-cleanup-on-failure contract are all present in the codebase and wired correctly.

---

_Verified: 2026-05-29T00:00:00Z_
_Verifier: Claude (gsd-verifier)_
