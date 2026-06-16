# Phase 71 — Deferred Items (out-of-scope discoveries)

## Pre-existing parallel-run flakiness in `BaseApi.Tests.Orchestrator` (13 tests)

**Discovered during:** 71-03 Task 2 wave gate (full-suite run).

**Observation:** Running the `BaseApi.Tests.Orchestrator` namespace under the configured
`maxParallelThreads: 6` (xunit.runner.json) fails **13 tests** — but the SAME 13 fail at the
pre-71-03 baseline (`4102e44`), proving they are NOT a regression from the Phase-71-03 seam change.

- Baseline `4102e44`: 78 passed / **13 failed** / 91 total (3m00s).
- After 71-03 (`ca21104`): 87 passed / **13 failed** / 100 total (3m01s) — the +9 are the new
  `OrchestratorResultPipeline*` facts; the failing count is identical.

Every Phase-71-03 deliverable test class passes in isolation:
- `OrchestratorResultPipelineForwardFacts` + `OrchestratorResultPipelineRecoveryFacts` — 9/9.
- `TypedResultConsumerFacts` 8/8, `ResultAckTests` 5/5, `ResultConsumeTests` 2/2,
  `StopConsumerLifecycleTests` 2/2 (the migrated result-consume facts).

**Likely cause:** timing/harness-sensitive Orchestrator tests (in-memory MassTransit harness +
Quartz scheduler classes) racing under 6-way parallelism — consistent with the project-memory note
that the orchestrator suite is parallel-load sensitive. Each class passes when run alone.

**Disposition:** OUT OF SCOPE for Phase 71 (pre-existing, unrelated to the result pipeline). Tracked
as an operator follow-up — a future quick task should pin the flaky Orchestrator harness/Quartz tests
(or lower their parallel collection affinity) so the full namespace is green under parallel run.
