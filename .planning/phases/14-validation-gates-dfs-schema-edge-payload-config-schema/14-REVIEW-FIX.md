---
phase: 14-validation-gates-dfs-schema-edge-payload-config-schema
fixed_at: 2026-05-29T00:00:00Z
review_path: .planning/phases/14-validation-gates-dfs-schema-edge-payload-config-schema/14-REVIEW.md
iteration: 1
findings_in_scope: 3
fixed: 3
skipped: 0
status: all_fixed
---

# Phase 14: Code Review Fix Report

**Fixed at:** 2026-05-29T00:00:00Z
**Source review:** .planning/phases/14-validation-gates-dfs-schema-edge-payload-config-schema/14-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 3 (all Warnings — critical_warning scope; 0 Critical, 4 Info out of scope)
- Fixed: 3
- Skipped: 0

All fixes verified with `dotnet build` Debug AND Release at 0 warnings / 0 errors
(project enforces `TreatWarningsAsErrors`). Full solution Release build (including
BaseApi.Tests) is green. Integration tests require live Postgres/Redis and were
not run, per phase constraints; changes are minimal and behavior-preserving.

## Fixed Issues

### WR-01: `JsonSchema.FromText` parse is unguarded — non-schema Definition would surface as HTTP 500, not domain 422

**Files modified:** `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs`
**Commit:** d6faf1b
**Applied fix:** Wrapped `JsonSchema.FromText(schemaDto.Definition)` in a
`try/catch (Exception ex) when (ex is JsonException or JsonSchemaException)` that
re-throws via the existing `OrchestrationValidationException.PayloadConfigSchema`
factory — so a corrupt/un-parseable stored Definition surfaces as the gate's 422
envelope instead of falling through to `FallbackExceptionHandler` → HTTP 500.
Matches the create-side `SchemaDtoValidator` parse-guard pattern. Added a comment
explaining the cross-file invariant and why the guard exists.

### WR-02: `JsonDocument.Parse(assignment.Payload)` failure not translated — only disposed

**Files modified:** `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs`
**Commit:** e944b50
**Applied fix:** Wrapped the payload parse in an inner
`try/catch (JsonException)` that re-throws via
`OrchestrationValidationException.PayloadConfigSchema(assignment.Id, new[] { "Payload is not valid JSON." })`,
producing the same 422 envelope as a schema-mismatch. The thrown
`OrchestrationValidationException` propagates out through the pre-existing outer
`try/finally`, whose `payloadDoc?.Dispose()` remains safe (payloadDoc is still
null at that point). Behavior for valid payloads is unchanged.

### WR-03: Cycle gate (entry-reachable) vs schema-edge gate (all steps) scope divergence

**Files modified:** `src/BaseApi.Service/Features/Orchestration/WorkflowGraphSnapshot.cs`, `src/BaseApi.Service/Features/Orchestration/Validation/CycleDetector.cs`
**Commit:** 8b1a61f
**Applied fix:** Took review option (a) / the phase-constraint-preferred path:
documented the intentional scope contract rather than altering gate behavior.
Added a "Validation-gate scope contract" `<para>` to `WorkflowGraphSnapshot`'s XML
docs spelling out that the cycle/missing-step gate is deliberately seeded only from
`EntryStepIds` (entry-reachable subgraph — an unreachable step cannot execute, so
cannot contribute a runtime cycle), while the schema-edge and payload↔config-schema
gates walk the full `Steps`/`Assignments` sets, and recording how to extend the
cycle gate to orphan subgraphs if ever required. Added a matching cross-reference
note at the entry-seed site in `CycleDetector.Validate`'s doc comment. No runtime
behavior changed — documentation only, so no logic-verification flag needed.

---

_Fixed: 2026-05-29T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
