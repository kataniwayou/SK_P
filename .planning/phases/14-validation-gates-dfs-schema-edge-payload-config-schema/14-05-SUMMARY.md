---
phase: 14-validation-gates-dfs-schema-edge-payload-config-schema
plan: 05
subsystem: orchestration-validation-integration
tags: [gate-order, short-circuit, l1-cleanup, regression, traceability, integration-test]
requires:
  - 14-01 (OrchestrationValidationException + 422 handler + split-Fallback ordering)
  - 14-02 (CycleDetector gate body — runs at pipeline step 3)
  - 14-03 (SchemaEdgeValidator gate body — runs at pipeline step 4)
  - 14-04 (PayloadConfigSchemaValidator gate body — runs at pipeline step 5)
  - Phase 13 OrchestrationService locked pipeline + WorkflowGraphSnapshot (using var snapshot)
  - Phase 13 StartCleanupFacts RecordingWorkflowGraphLoader + ConfigureTestServices pattern
provides:
  - ValidationOrderFacts (gate-order short-circuit proof + L1-cleanup-on-422 proof)
  - STATE.md traceability for L1-VALIDATE-02/09/10 + D-13 + gate order
affects: []
tech-stack:
  added: []
  patterns:
    - "Multi-failure workflow asserts the FIRST gate in the locked order (existence 404 → cycle → schemaEdge → payload)"
    - "RecordingWorkflowGraphLoader (copied local) proves L1 Dispose runs on the DOMAIN-VALIDATION (422) path, not just the 500 path"
    - "Integration/order plan runs LAST — needs all three gate bodies active to prove short-circuit"
key-files:
  created:
    - tests/BaseApi.Tests/Features/Orchestration/ValidationOrderFacts.cs
  modified:
    - .planning/STATE.md
decisions:
  - "L1-VALIDATE-02 satisfied by re-using the existence path (D-13): existence stays 404, NOT 422; only the three NEW structural gates throw OrchestrationValidationException → 422."
  - "L1-VALIDATE-09: VALID-21 closes ONLY at orchestration-start; Assignment PUT/POST remain valid-JSON-only (AssignmentsIntegrationTests unchanged + GREEN)."
  - "L1-VALIDATE-10: TEST-03 (Testcontainers) + TEST-04 (Respawn) remain DEFERRED; v3.3.0 does not close them."
  - "Gate order proven end-to-end by multi-failure workflows; L1 cleanup proven on the 422 path via RecordingWorkflowGraphLoader."
metrics:
  duration: ~7min
  completed: 2026-05-29
  tasks: 2
  files: 2
---

# Phase 14 Plan 05: Validation Gate-Order + L1-Cleanup Integration Summary

One-liner: Proved the LOCKED gate order short-circuits at the FIRST failure (existence 404 → cycle → schema-edge → payload) and that the `using var snapshot` L1 cleanup runs on the 422 domain-validation path — via four multi-failure `ValidationOrderFacts`, then re-ran the full suite (194/194 GREEN, v3.2.0 invariants intact) and recorded the L1-VALIDATE-02/09/10 + D-13 traceability in STATE.md.

## What Was Built

- **Task 1 (test) — `ValidationOrderFacts`** (`[Trait("Phase", "14")]`, `IClassFixture<Phase8WebAppFactory>`). Four facts, each driving the real `POST /api/v1/orchestration/start` pipeline over real Postgres/Redis with multi-failure workflows that fail more than one gate at once:
  - `ExistenceBeforeCycle_MissingWorkflowId_Returns404_NotCycle` — a `Guid.NewGuid()` that does not exist returns **404** (existence runs at step 1 BEFORE the snapshot is even loaded; the 422 gates never run). Asserts `NotFound` and `NotEqual(UnprocessableEntity)`.
  - `CycleBeforeSchemaEdge_WorkflowFailingBoth_Returns422Cycle` — a back-edge cycle A→B→A where A.Output (X) ≠ B.Input (Y) fails BOTH cycle and schema-edge; the response is `errors.gate == "cycle"` (step 3 wins over step 4).
  - `SchemaEdgeBeforePayload_WorkflowFailingBoth_Returns422SchemaEdge` — an ACYCLIC A→B where A.Output (X) ≠ B.Input (Y) AND B carries a ConfigSchema its Assignment payload (`{"foo":123}`) violates; the response is `errors.gate == "schemaEdge"` (step 4 wins over step 5).
  - `L1Cleanup_RunsOnValidationFailurePath` — a `RecordingWorkflowGraphLoader` (copied from `StartCleanupFacts`, wired via `ConfigureTestServices`) captures the snapshot; a real back-edge cycle forces a **422**; the fact asserts `recorder.Captured.IsDisposed == true` and all 5 dictionaries `Count == 0`, proving the `using var snapshot` declaration runs `Dispose()` on the domain-validation failure path (not just the 500 path that `StartCleanupFacts` already covers).
  - Seeding helpers (Schema/Processor/Step/back-edge PUT/Assignment/Workflow) duplicated locally from the 14-02/03/04 fact files; no production code added.
- **Task 2 (docs) — full-suite regression sweep + STATE.md traceability.** No production code. Ran the full `dotnet test` (194/194 GREEN). Appended four `Plan 14-05` bullets to STATE.md Accumulated Context > Decisions recording L1-VALIDATE-02 (existence stays 404 — D-13), L1-VALIDATE-09 (VALID-21 closes only at orchestration-start; Assignment PUT/POST valid-JSON-only), L1-VALIDATE-10 (TEST-03/TEST-04 still deferred), and the gate-order/L1-cleanup proof.

## Verification

- `dotnet build` (test project, Debug): exit 0, **0 warnings** (TreatWarningsAsErrors).
- Targeted `--filter-class *ValidationOrderFacts`: **4/4 GREEN** (3.7s).
- Full suite `dotnet test`: **194/194 GREEN** (2m55s) — 190 prior (14-04 baseline) + 4 new. The MTP runner runs the whole assembly, so this run is a strict superset that includes:
  - `StartOrchestrationFacts.Start_Returns404_*` (existence 404 — D-13 / L1-VALIDATE-02) — GREEN.
  - `ErrorMappingFacts` (SSRF `<500ms` + draft-2020-12 — D-06 / L1-VALIDATE-07) — GREEN.
  - `AssignmentsIntegrationTests` (Assignment PUT/POST valid-JSON-only — L1-VALIDATE-09) — GREEN.
  - `StartCleanupFacts` (Fallback last-walked → 500 cleanup) and all three gate fact files (14-02/03/04) — GREEN.
- No production `.cs` file changed in this plan: `git diff --name-only HEAD -- 'src/**/*.cs'` is empty. Only the test file (Task 1) + STATE.md (Task 2) were touched.

## Deviations from Plan

None — plan executed exactly as written.

The plan's `<verify>` commands use `dotnet test --filter "FullyQualifiedName~..."`; this project's MTP runner does not accept the legacy `--filter` option (prior 14-03 documented this). The equivalent `--filter-class *ValidationOrderFacts` selector was used for the targeted run, and the full `dotnet test` (which the MTP runner executes as the whole assembly anyway) is the authoritative GREEN gate covering every plan-listed filter as a strict subset. Same test scope, no behavior change.

## TDD Gate Compliance

This plan is `type: execute` (not a `tdd` plan). Task 1 is a test-only plan (`test(...)` commit) that proves already-implemented gate behavior end-to-end; Task 2 is a docs/regression task with no code. No RED/GREEN/REFACTOR gate sequence applies — the gates under assertion were implemented in 14-02/03/04 and the facts assert their composed ordering.

## Threat Surface

No new threat surface beyond the plan's `<threat_model>`. T-14-12 (gate-bypass tampering) is mitigated — `ValidationOrderFacts` asserts the FIRST failing gate's discriminator on multi-failure workflows, so a reordering regression flips an asserted gate value and fails the build. T-14-13 (L1 resource leak on failure path) is mitigated — `L1Cleanup_RunsOnValidationFailurePath` asserts `snapshot.IsDisposed` on the 422 path. T-14-14 (error-body-shape regression) is accepted — the existing `ErrorMappingFacts` + `StartOrchestrationFacts` re-ran GREEN as the guard.

## Commits

- 400df1a `test(14-05): ValidationOrderFacts — gate-order short-circuit + L1 cleanup on 422`
- a5fa058 `docs(14-05): record L1-VALIDATE-02/09/10 + D-13 + gate-order traceability`

## Self-Check: PASSED

Created file `tests/BaseApi.Tests/Features/Orchestration/ValidationOrderFacts.cs` present; modified file `.planning/STATE.md` present with 4 `Plan 14-05` bullets. Both task commits (400df1a, a5fa058) present in git log.
