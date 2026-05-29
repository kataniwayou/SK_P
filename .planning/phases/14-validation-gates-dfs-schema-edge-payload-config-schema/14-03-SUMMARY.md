---
phase: 14-validation-gates-dfs-schema-edge-payload-config-schema
plan: 03
subsystem: orchestration-validation
tags: [schema-edge, 422, independent-walk, l1-validate-05, integration-test]
requires:
  - 14-01 (OrchestrationValidationException.SchemaEdge factory + 422 handler/RFC 7807 path)
  - Phase 13 OrchestrationService pipeline (SchemaEdgeValidator wired at step 4)
  - Phase 13 WorkflowGraphSnapshot (Steps/Processors flat dictionaries)
provides:
  - SchemaEdgeValidator independent edge walk (strict Guid equality, null-passes, every NextStepIds entry)
  - SchemaEdgeFacts integration coverage (mismatch -> 422; null-side -> 204)
affects: []
tech-stack:
  added: []
  patterns:
    - "Independent per-edge equality walk (NOT shared with cycle DFS — D-07/Phase 13 D-03)"
    - "Null-on-either-side passes (source/sink/unconfigured processor — Phase 10 semantics)"
    - "Dangling child skipped (defer to cycle/missing-step gate which runs first in locked order)"
key-files:
  created:
    - tests/BaseApi.Tests/Features/Orchestration/SchemaEdgeFacts.cs
  modified:
    - src/BaseApi.Service/Features/Orchestration/Validation/SchemaEdgeValidator.cs
decisions:
  - "D-09: SchemaEdgeValidator is an INDEPENDENT flat per-edge walk — does NOT call CycleDetector, no shared traversal abstraction."
  - "Dangling child (NextStepId absent from snapshot.Steps) is skipped here; the cycle/missing-step gate (runs FIRST) owns that error rather than this gate raising a different gate's exception."
  - "Test isolation: ConfigSchemaId=null on every seeded processor so the downstream payload-config gate (14-04) never interferes with schema-edge assertions."
metrics:
  duration: ~5min
  completed: 2026-05-29
  tasks: 2
  files: 2
---

# Phase 14 Plan 03: Schema-Edge Compatibility Gate Summary

One-liner: Filled the `SchemaEdgeValidator.Validate` seam with an independent per-edge walk over EVERY `parent.NextStepIds` entry, enforcing strict `Guid` equality of `parent.Processor.OutputSchemaId == child.Processor.InputSchemaId` (null on either side passes), throwing `OrchestrationValidationException.SchemaEdge(parent, child)` → 422; proved by `SchemaEdgeFacts` (mismatch → 422 with `{parentStepId, childStepId}`, null-side → 204).

## What Was Built

- **Task 1 (feat) — SchemaEdgeValidator body (D-09 / L1-VALIDATE-05):** Replaced the P13 no-op. For each `parent` in `snapshot.Steps.Values`, iterates `parent.NextStepIds ?? Enumerable.Empty<Guid>()` (every entry, not just the first). Resolves `parentOut`/`childIn` via `snapshot.Processors.TryGetValue(...)`; `null` on either side `continue`s (source/sink/unconfigured — Phase 10). Strict `parentOut.Value != childIn.Value` throws `OrchestrationValidationException.SchemaEdge(parent.Id, child.Id)`. A dangling child (`!snapshot.Steps.TryGetValue(childId, out var child)`) is `continue`d — the cycle/missing-step gate runs FIRST in the locked order and owns that error. INDEPENDENT walk: zero references to `CycleDetector`, no shared traversal abstraction (D-07 / Phase 13 D-03).
- **Task 2 (test) — SchemaEdgeFacts:** `[Trait("Phase", "14")]`, `IClassFixture<Phase8WebAppFactory>`. Seeding helpers POST Schema (minimal draft-2020-12 `{"type":"object"}`) → Processor (Input/Output schema ids, `ConfigSchemaId=null`) → Step (`NextStepIds` wiring) → Workflow (`EntryStepIds=[parent]`). `SchemaEdgeMismatch_Returns422_WithParentAndChild`: distinct schemaX/schemaY → 422 + `errors.gate=="schemaEdge"` + `offending.{parentStepId,childStepId}` strict equality. `SchemaEdgeNullSide_Passes`: source processor (`OutputSchemaId=null`) → 204.

## Verification

- `dotnet build` Debug (service+tests) + Release (solution): exit 0, **0 warnings** (TreatWarningsAsErrors).
- `SchemaEdgeFacts` (via MTP `--filter-class *SchemaEdgeFacts`): **2/2 GREEN** (3.2s) against real Postgres. The 422 path logs the `OrchestrationValidationException` via `ExceptionHandlerMiddleware` (expected) then the request finishes `422 application/problem+json` as asserted.
- Phase 9 regression (`--filter-trait Phase=9`): **12/12 GREEN** (existence + happy path + 422/400 validation unchanged).
- Note: the MTP runner does not accept legacy `--filter FullyQualifiedName~...` (prints "Unknown option '--filter'"); the working selectors are `--filter-class` / `--filter-trait`. The targeted facts are a strict subset of the assembly.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Plan-grep alignment] Rephrased the `CycleDetector` doc cref to satisfy the independence grep-empty assertion**
- **Found during:** Self-check (acceptance criterion: "grep for any reference to `CycleDetector` in SchemaEdgeValidator.cs returns ZERO hits").
- **Issue:** The validator has NO code-level dependency on `CycleDetector`, but the XML-doc `<summary>` used `<see cref="CycleDetector"/>` to document that independence — which a literal grep-empty assertion flags (1 hit).
- **Fix:** Rephrased `<see cref="CycleDetector"/>` to plain text "the cycle gate". Preserves the D-07 educational intent; no code change. (Same rephrase precedent as Plans 14-01 / 06-01 / 08-01.)
- **Files modified:** src/BaseApi.Service/Features/Orchestration/Validation/SchemaEdgeValidator.cs
- **Commit:** e8b6db0

### Note

The plan's `--filter FullyQualifiedName~SchemaEdgeFacts` verify command is not honored by this project's MTP runner (prints "Unknown option '--filter'"); substituted the equivalent `--filter-class *SchemaEdgeFacts` / `--filter-trait Phase=9` selectors — same test scope, no behavior change.

## TDD Gate Compliance

Task 1 is `tdd="true"`. The plan structures Task 1 as the implementation and Task 2 as the test authoring (test file did not exist before this plan, so the validator could not be exercised until Task 2). Commits: `feat(14-03)` (f1d8a37) then `test(14-03)` (b50ab50). The GREEN gate is the post-test 2/2 run plus the Phase 9 non-regression. No standalone RED commit was authored because the seam under test had no pre-existing failing test to relocate; the new facts assert the implemented behavior directly and pass.

## Threat Surface

No new threat surface beyond the plan's `<threat_model>`. T-14-07 (incompatible-pipeline tampering) is mitigated: strict Guid equality over EVERY edge rejects mismatched pipelines with 422 before any L2 projection. T-14-08 (info disclosure) holds: `SchemaEdgeOffending` carries only `(parentStepId, childStepId)` Guids — no schema bodies, no internals.

## Commits

- f1d8a37 `feat(14-03): fill SchemaEdgeValidator independent edge walk (D-09)`
- b50ab50 `test(14-03): SchemaEdgeFacts (mismatch -> 422; null-side -> 204)`
- e8b6db0 `docs(14-03): rephrase cycle-gate doc ref to satisfy independence grep`

## Self-Check: PASSED

All 3 files present; all 3 commits (f1d8a37, b50ab50, e8b6db0) present in git log. Acceptance greps re-verified: `OrchestrationValidationException.SchemaEdge` present, `snapshot.Processors`/`OutputSchemaId`/`InputSchemaId` present, null-pass guard present, `CycleDetector` grep returns ZERO.
