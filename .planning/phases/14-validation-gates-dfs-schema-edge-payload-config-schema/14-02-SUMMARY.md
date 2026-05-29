---
phase: 14-validation-gates-dfs-schema-edge-payload-config-schema
plan: 02
subsystem: orchestration-validation-gates
tags: [cycle-detection, dfs, two-set, missing-step, 422, no-recursion]
requires:
  - 14-01 OrchestrationValidationException.Cycle/MissingStep factories
  - Phase 13 WorkflowGraphSnapshot (Steps/Workflows dicts) + OrchestrationService pipeline (CycleDetector seam at step 3)
  - Phase 8 Phase8WebAppFactory + InternalsVisibleTo("BaseApi.Tests")
provides:
  - CycleDetector.Validate filled — two-set iterative DFS cycle gate (L1-VALIDATE-03) + missing-step gate (L1-VALIDATE-04)
  - CycleDetectionFacts (integration — true-cycle 422 + diamond-DAG 204)
  - MissingStepFacts (white-box — crafted dangling NextStepId + terminal-null)
affects:
  - 14-03 (schema-edge gate runs at pipeline step 4, after this cycle gate)
tech-stack:
  added: []
  patterns:
    - "Two-set iterative DFS (onStack + fullyVisited) — diamond/fan-in DAG is NOT a cycle (D-14)"
    - "Explicit Stack<(Guid, IEnumerator<Guid>)> frames — NO recursion (StackOverflowException uncatchable by IExceptionHandler)"
    - "White-box gate test: construct internal WorkflowGraphSnapshot via InternalsVisibleTo for FK-impossible states (dangling NextStepId)"
key-files:
  created:
    - tests/BaseApi.Tests/Features/Orchestration/CycleDetectionFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/MissingStepFacts.cs
  modified:
    - src/BaseApi.Service/Features/Orchestration/Validation/CycleDetector.cs
decisions:
  - "D-14 two-set DFS (NOT the single-visited D-07 sketch): onStack detects back-edges; fullyVisited admits shared/fan-in subgraphs so diamonds pass."
  - "EntryStepId that does not resolve throws MissingStep(Guid.Empty, entryId) — Guid.Empty is the parent sentinel for an entry-seed miss (no parent step exists)."
  - "Offending stepChain reconstructed as path slice from first occurrence of the repeated node to end, then the node appended to close the loop."
metrics:
  duration: ~10min
  completed: 2026-05-29
  tasks: 3
  files: 3
---

# Phase 14 Plan 02: Cycle + Missing-Step Validation Gate Summary

One-liner: Filled the `CycleDetector.Validate` seam with a two-set (onStack + fullyVisited) iterative-stack DFS over `snapshot.Steps[*].NextStepIds` seeded from `Workflow.EntryStepIds[*]` — throws `Cycle(stepChain)` on a true cycle and `MissingStep(parent, child)` on a dangling NextStepId, passes diamonds and terminal-null steps, with NO recursion (StackOverflowException is uncatchable by IExceptionHandler). Suite 185/185 GREEN.

## What Was Built

- **Task 1 (feat) — CycleDetector body:** Replaced the P13 no-op with a two-set iterative DFS. `Validate` seeds from every workflow's `EntryStepIds`, sharing a single `fullyVisited` set across seeds; an unresolved entry seed throws `MissingStep(Guid.Empty, entryId)`. `RunDfs` uses an explicit `Stack<(Guid, IEnumerator<Guid>)>` plus a parallel `onStack` HashSet (cycle detection) and an ordered `path` List (offending-chain reconstruction). On peek: a child absent from `Steps` → `MissingStep(currentStep, child)`; a child in `onStack` → `Cycle(pathSlice + child)`; a child not yet `fullyVisited` → push; a `fullyVisited` child → skip (the two-set discriminator that admits diamond/fan-in DAGs). Frame exhaustion pops the node from stack/onStack/path and adds it to `fullyVisited`. Null/empty `NextStepIds` yields an empty enumerator → immediate pop → terminal pass.
- **Task 2 (test) — CycleDetectionFacts (integration):** Drives the full HTTP `/api/v1/orchestration/start` pipeline through the real seam over real Postgres. `Cycle_Returns422_WithStepChain` seeds A→B→C via forward Step POSTs then adds the C→A back-edge via Step PUT (FK requires both ends to exist first), asserts 422 + `application/problem+json` + `errors.gate=="cycle"` + non-empty `errors.offending.stepChain` containing the repeated node. `DiamondDag_Passes_NoFalsePositiveCycle` seeds A→B, A→C, B→D, C→D and asserts 204 (D-14 guard against Pitfall 2 single-set false-positive). All-schema-FKs-null Processor isolates the cycle gate from the schema-edge gate.
- **Task 3 (test) — MissingStepFacts (white-box):** Constructs a crafted in-memory `WorkflowGraphSnapshot` (via `InternalsVisibleTo` + `NullLogger`) because FK-Restrict on `StepNextSteps` makes a dangling NextStepId impossible to seed through the DB. `MissingNextStep_Throws_WithParentAndMissingChild` asserts `gate=="missingStep"` and serializes `ErrorsExtension` to confirm `offending.parentStepId` + `offending.missingChildId`. `TerminalNullNextStepIds_Passes` confirms both null and empty `NextStepIds` are terminal (no throw via `Record.Exception` == null).

## Verification

- `dotnet build` Debug + Release: exit 0, **0 warnings** (TreatWarningsAsErrors).
- Full suite: **185/185 GREEN** (181 prior 14-01 baseline + 4 new facts; 2m55s). The MTP runner ignores `--filter` (warning MTP0001) and runs the whole assembly, so the targeted run is a strict superset that includes Phase 9 existence/happy-path facts unchanged.
- No-recursion confirmed: `Validate` calls `RunDfs`, `RunDfs` calls `Push`, `Push` calls nothing — no method in the file calls itself. The only `Push(` self-name collision is `stack.Push(...)` (the BCL `Stack<T>` method, not the file's helper).
- Acceptance greps: `OrchestrationValidationException.Cycle` + `.MissingStep`, `Stack<`, `HashSet<Guid> onStack`, `fullyVisited` all present in CycleDetector.cs.

## Deviations from Plan

None — plan executed exactly as written.

## TDD Gate Compliance

Task 1 is `tdd="true"`. The plan's task ordering authors the implementation (Task 1) BEFORE the test files (Tasks 2-3), so the RED/GREEN gate is collapsed: the implementation and its tests landed as a feat commit followed by two test commits, all GREEN at first run. This matches the plan's explicit `<read_first>`/`<action>` sequencing (Task 1 fills the seam; Tasks 2-3 add the integration + white-box tests). No separate failing-test (RED) commit was authored because the plan front-loads the implementation; the GREEN gate is the 185/185 suite run after all three commits. Cycle/missingStep behavior is fully covered by the two new test files (4 facts).

## Threat Surface

No new threat surface beyond the plan's `<threat_model>`. T-14-04 (StackOverflow DoS) mitigated by the explicit-stack iterative DFS (grep-confirmed no self-call). T-14-05 (diamond false-reject) mitigated by the two-set discriminator (`DiamondDag_Passes` is the guard). T-14-06 (info-disclosure) accepted — offending carries only stepId Guids.

## Commits

- 18ce1aa `feat(14-02): fill CycleDetector with two-set iterative DFS + missing-step gate (D-07/D-08/D-14)`
- c2c345f `test(14-02): CycleDetectionFacts integration (true-cycle 422 + diamond-DAG passes)`
- c24b0ec `test(14-02): MissingStepFacts white-box (crafted snapshot + terminal-null)`

## Self-Check: PASSED

All 3 files present; all 3 task commits (18ce1aa, c2c345f, c24b0ec) present in git log.
