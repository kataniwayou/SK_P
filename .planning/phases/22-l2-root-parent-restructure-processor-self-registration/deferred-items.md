# Phase 22 Deferred / Out-of-Scope Items

## [22-05] Happy-path `/start` test regression from the Plan 04 liveness gate + Plan 03 no-create boundary

**Discovered:** 2026-05-31 during Plan 05 execution (Task 4 PROC-EDGE-01 regression check).

**Scope:** Plan 04 added the `ProcessorLivenessValidator` (every participating processor must have a
live `skp:{procId}` self-registration entry before Start succeeds), and Plan 03 removed the writer's
processor-key writes (PROC-NOCREATE-01). Together these break happy-path `/start` integration tests
that (a) never seed live processor entries, and/or (b) assert the now-removed processor-key write.
These test files are OUTSIDE Plan 05's stated `files_modified` (9 files), so they are NOT auto-fixed
under the executor scope boundary — they are surfaced for an explicit decision.

**Confirmed failing (Debug, full stack up healthy):**

| Class | Failed/Total | Cause |
|-------|-------------|-------|
| HappyPathE2EFacts | 1/1 | 422 liveness (no live seed) + asserts removed processor-key write |
| StartLoopFacts | 3/3 | 422 liveness on happy Start |
| IdempotencyFacts | 2/2 | 422 liveness on happy Start |
| StartCleanupFacts | 1/1 | 422 liveness on happy Start |
| StopScanFacts | 1/1 | 422 liveness on happy Start |
| StartOrchestrationFacts | 1/6 | 422 liveness on the one happy-Start fact |
| ValidationOrderFacts | 1/5 | 422 liveness on the all-gates-pass fact |

Likely also: `CorrelationPropagationE2ETests`, `OrchestrationLogsE2ETests` (both POST a happy `/start`;
not yet enumerated to avoid long real-stack runs before the decision).

**Resolution options (operator decision):**
1. Per-test seed: add a live-processor seed before each happy `/start` (consistent with Plan 05's
   `SchemaEdgeFacts.SchemaEdgeNullSide_Passes` fix). Simple, explicit, more boilerplate.
2. Centralize: add a shared `SeedLiveProcessor`/`SeedGraphLive` helper or an opt-in auto-seed on
   `HarnessWebAppFactory` so the happy-path family seeds liveness uniformly. Less churn, one design point.
3. Rewrite behavioral assertions where needed (e.g. `HappyPathE2EFacts` must stop asserting a
   writer-created processor key — PROC-NOCREATE-01 — and instead treat the processor key as
   externally seeded liveness).

**Blocks:** Plan 05 Task 5 close gate (full suite GREEN ×3) cannot pass until resolved.
