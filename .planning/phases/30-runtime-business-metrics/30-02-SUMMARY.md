---
phase: 30-runtime-business-metrics
plan: 02
subsystem: orchestrator
tags: [opentelemetry, prometheus, metrics, counter, imeterfactory, observability, dispatch-bottleneck]

# Dependency graph
requires:
  - phase: 30-runtime-business-metrics
    provides: "service.instance.id resource attribute on the metrics OTel resource (30-01) — every counter inherits the ambient service_instance_id Prometheus label for free"
  - phase: 18-baseconsole-core
    provides: "AddBaseConsoleObservability — the shared metrics-only MeterProvider this plan attaches the Orchestrator meter to"
provides:
  - "Orchestrator.Observability.OrchestratorMetrics — DI-singleton IMeterFactory holder exposing two Counter<long>: DispatchSent (orchestrator_dispatch_sent) + ResultConsumed (orchestrator_result_consumed), tagged ProcessorId"
  - "the \"Orchestrator\" meter registered on the shared MeterProvider via ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(OrchestratorMetrics.MeterName))"
  - "orchestrator_dispatch_sent_total + orchestrator_result_consumed_total Prometheus series (collector appends _total), keyed by ProcessorId — the orchestrator half of the per-processor dispatch-bottleneck PromQL"
  - "OrchestratorTestStubs.Metrics() — a real-IMeterFactory OrchestratorMetrics builder for hermetic ctor wiring"
affects: [30-04-metrics-e2e]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Code-owned business Counter<long> via IMeterFactory.Create(const MeterName) — never a static Meter field (cross-test static leak in the shared hermetic process)"
    - "ConfigureOpenTelemetryMeterProvider AddMeter(const) seam (the meter-provider analog of the Phase-29 ConfigureOpenTelemetryLoggerProvider) — additive attach to the base-lib MeterProvider, preserving the D-02 MeterName const symmetry"
    - "SENT counted AFTER endpoint.Send (an infra throw skips the increment); CONSUMED counted at the TOP of Consume before the L1 read (every consumed result counts, incl. the graceful L1-miss ack)"
    - "Counter tag = literal PascalCase ProcessorId (collector preserves tag-key case) + .ToString(\"D\"); NO workflowId/per-execution id (cardinality)"

key-files:
  created:
    - src/Orchestrator/Observability/OrchestratorMetrics.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorMetricsFacts.cs
  modified:
    - src/Orchestrator/Program.cs
    - src/Orchestrator/Dispatch/StepDispatcher.cs
    - src/Orchestrator/Consumers/ResultConsumer.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorTestStubs.cs
    - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
    - tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs
    - tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs
    - tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs
    - tests/BaseApi.Tests/Orchestrator/WorkflowFireJobScopeTests.cs

key-decisions:
  - "Meter registered via ConfigureOpenTelemetryMeterProvider in Program.cs (not a literal AddMeter string inside the base-lib WithMetrics) — preserves D-02's single const referenced by both AddMeter and Create, mirrors the Phase-29 logger-provider seam"
  - "IMeterFactory holder, never a static Meter field (D-01) — avoids cross-test static leak in the shared hermetic process"
  - "Counters named without the _total suffix (D-03); the collector add_metric_suffixes default appends it (embedding it here would double it)"
  - "DispatchSent counts AFTER endpoint.Send (D-04: an infra Send-throw correctly skips it); ResultConsumed counts at the TOP of Consume before the L1 read (D-06: every consumed result, incl. the graceful L1-miss ack)"
  - "ProcessorId is the ONLY tag (bounded Guid) — no workflowId/per-execution id (T-30-03 cardinality DoS mitigation); service_instance_id is ambient from Plan 01"

patterns-established:
  - "Per-console code-owned business metric: IMeterFactory holder + ConfigureOpenTelemetryMeterProvider AddMeter(const) + Counter.Add(1, PascalCase tag) at the confirmed site"

requirements-completed: [METRIC-04]

# Metrics
duration: 9min
completed: 2026-06-02
---

# Phase 30 Plan 02: Orchestrator Dispatch/Consume Counters Summary

**The Orchestrator's code-owned `"Orchestrator"` meter (`IMeterFactory` holder, registered via `ConfigureOpenTelemetryMeterProvider`) with two monotonic `Counter<long>` — `orchestrator_dispatch_sent` (incremented AFTER `endpoint.Send` in `StepDispatcher`) and `orchestrator_result_consumed` (incremented at the TOP of `ResultConsumer.Consume`) — each tagged `ProcessorId` only (no `workflowId`), inheriting the ambient `service_instance_id` from Plan 01: the orchestrator half of the per-processor dispatch-bottleneck PromQL (METRIC-04).**

## Performance

- **Duration:** ~9 min
- **Started:** 2026-06-02T20:05:58Z
- **Completed:** 2026-06-02T20:14:57Z
- **Tasks:** 2
- **Files modified:** 11 (2 created, 9 modified)

## Accomplishments
- Added `Orchestrator.Observability.OrchestratorMetrics` — a `sealed` DI-singleton built from `IMeterFactory.Create("Orchestrator")` exposing `DispatchSent` (`orchestrator_dispatch_sent`) and `ResultConsumed` (`orchestrator_result_consumed`), both snake_case with NO `_total` suffix (the collector appends it). `MeterName` is the single const referenced by both `Create` and `AddMeter` (D-02).
- Registered the meter on the shared (base-lib) `MeterProvider` via `builder.Services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(OrchestratorMetrics.MeterName))` plus `AddSingleton<OrchestratorMetrics>()` in `Program.cs` — the meter-provider analog of the Phase-29 logger seam, attaching additively to what `AddBaseConsoleObservability` already built.
- `StepDispatcher` increments `DispatchSent.Add(1, ProcessorId)` AFTER `endpoint.Send` returns (D-04 — an infra Send-throw correctly skips the count); this single dispatch owner covers BOTH the `WorkflowFireJob` entry fire and the `ResultConsumer` continuation (D-05).
- `ResultConsumer` increments `ResultConsumed.Add(1, ProcessorId)` at the TOP of `Consume`, BEFORE the L1 `store.TryGet`, so the graceful L1-miss ack is ALSO counted (D-06).
- Both tags use the literal PascalCase key `"ProcessorId"` with `.ToString("D")`; NO `workflowId` tag on either counter (T-30-03 cardinality mitigation, grep-verified).
- Added the hermetic `OrchestratorMetricsFacts` (2 facts): `MeterName == "Orchestrator"` and non-null counters from a real `IMeterFactory` — catches the D-02 const-mismatch bug without the full stack.

## Task Commits

Each task was committed atomically (scoped paths only — the in-progress `.planning/` archive deletions were left untouched):

1. **Task 1: OrchestratorMetrics holder + register the "Orchestrator" meter** - `e79f3e8` (feat) — holder + Program.cs registration + OrchestratorMetricsFacts (TDD: test written first, then holder; both facts GREEN).
2. **Task 2: increment dispatch_sent (after Send) + result_consumed (consume entry)** - `a1488f0` (feat) — the two increment sites + the 5 hermetic ctor-site updates + a doc-comment reword.

## Files Created/Modified
- `src/Orchestrator/Observability/OrchestratorMetrics.cs` (NEW) — `sealed` `IMeterFactory` holder; `const string MeterName = "Orchestrator"`; two `Counter<long>` (no `_total`).
- `tests/BaseApi.Tests/Orchestrator/OrchestratorMetricsFacts.cs` (NEW) — hermetic D-02 const + non-null-counter proof from a real `IMeterFactory`.
- `src/Orchestrator/Program.cs` — `AddSingleton<OrchestratorMetrics>()` + `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(OrchestratorMetrics.MeterName))` (next to the dispatch singletons).
- `src/Orchestrator/Dispatch/StepDispatcher.cs` — ctor `OrchestratorMetrics metrics`; `DispatchSent.Add(1, ProcessorId)` after `endpoint.Send`.
- `src/Orchestrator/Consumers/ResultConsumer.cs` — ctor `OrchestratorMetrics metrics` (between `IStepDispatcher` and `ILogger`); `ResultConsumed.Add(1, ProcessorId)` at the top of `Consume`.
- `tests/BaseApi.Tests/Orchestrator/OrchestratorTestStubs.cs` — added the `Metrics()` helper (real-`IMeterFactory` `OrchestratorMetrics` builder).
- `tests/BaseApi.Tests/Orchestrator/{ResultAckTests,ResultConsumeTests,StopConsumerLifecycleTests,FireDispatchTests,WorkflowFireJobScopeTests}.cs` — threaded `OrchestratorTestStubs.Metrics()` through every direct `new StepDispatcher(...)` / `new ResultConsumer(...)` ctor call (the new required param).

## Decisions Made
- **`ConfigureOpenTelemetryMeterProvider` seam, not a literal base-lib `AddMeter` string.** Preserves the D-02 const symmetry (one `MeterName` referenced by both `Create` and `AddMeter`) and mirrors the proven Phase-29 `ConfigureOpenTelemetryLoggerProvider` pattern. The method ships in `OpenTelemetry.Extensions.Hosting`, which flows transitively via `BaseConsole.Core`.
- **`IMeterFactory`, never a `static Meter`.** The .NET 8 blessed DI pattern auto-registered by the generic host; a static field would leak across tests in the shared hermetic process.
- **No `_total` in the instrument names (D-03).** The collector's prometheus exporter `add_metric_suffixes` default appends it.
- **`ProcessorId` is the only tag.** Bounded Guid; `service_instance_id` is ambient (Plan 01). No `workflowId`/per-execution id — the T-30-03 cardinality-DoS mitigation.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Threaded a metrics instance through 5 hermetic ctor call-sites**
- **Found during:** Task 2
- **Issue:** Adding the required `OrchestratorMetrics metrics` ctor param to `StepDispatcher` and `ResultConsumer` broke compilation in five existing hermetic tests that construct those types directly (`ResultAckTests`, `ResultConsumeTests`, `StopConsumerLifecycleTests`, `FireDispatchTests` ×3, `WorkflowFireJobScopeTests`) — none go through DI.
- **Fix:** Added a shared `OrchestratorTestStubs.Metrics()` helper that builds a real `OrchestratorMetrics` from a minimal `new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()`, and passed it at every call-site (no collector wired in-test, so the increments are no-op but the non-null dependency is satisfied).
- **Files modified:** OrchestratorTestStubs.cs + the 5 test files above.
- **Verification:** `dotnet build src/Orchestrator -c Debug` 0/0; full hermetic suite 410/410 GREEN.
- **Committed in:** a1488f0 (Task 2 commit)

**2. [Rule 1 - Acceptance-criterion compliance] Reworded OrchestratorMetrics doc-comments off the literal `_total` token**
- **Found during:** Task 2 verification
- **Issue:** Task 1's acceptance criterion "`grep` of OrchestratorMetrics.cs does NOT find `_total`" was being violated by explanatory doc-comments that *referenced* the `_total` suffix (the counter NAMES never carried it). A literal grep would falsely flag the file.
- **Fix:** Reworded the doc-comment and inline comments to describe "the Prometheus counter suffix" / "the collector appends the suffix" instead of the literal token. The counter names were always correct (`orchestrator_dispatch_sent` / `orchestrator_result_consumed`, no suffix).
- **Files modified:** src/Orchestrator/Observability/OrchestratorMetrics.cs (comments only — no behavior change; rebuilt 0/0).
- **Verification:** `grep _total OrchestratorMetrics.cs` = 0 matches.
- **Committed in:** a1488f0 (Task 2 commit — the file was created in e79f3e8; the comment refinement landed with Task 2).

---

**Total deviations:** 2 (1 blocking-resolved, 1 acceptance-criterion compliance — both comment/test-wiring only, no metric-behavior change)
**Impact on plan:** Both adjustments are mechanical (test ctor wiring + comment wording); the plan's interfaces, increment sites, tags, and all acceptance greps are satisfied exactly.

## Threat Surface
- T-30-03 (counter-label cardinality DoS) MITIGATED as planned: both counters tag ONLY `ProcessorId` (bounded Guid) + the ambient `service_instance_id` (bounded by replicas). grep confirms NO `workflowId`/`WorkflowId` in any `.Add(` metric tag on either file.
- No new security-relevant surface introduced (no network endpoints, auth paths, file access, or schema changes).

## Known Stubs
None — both counters are wired to live increment sites in the real dispatch/consume code paths. The in-test no-op (`OrchestratorTestStubs.Metrics()` with no collector) is the standard hermetic pattern, not a product stub.

## Issues Encountered
None — Orchestrator built 0/0 on the first attempt for both tasks; the only follow-up was the mechanical test-ctor threading (Rule 3) the new required param forced. Hermetic suite went 408 → 410 (+2 OrchestratorMetricsFacts), zero regression. Release solution build 0 Warning / 0 Error.

## Next Phase Readiness
- Plan 30-03 (processor-side counters in `BaseProcessor.Core`) is independent (different lib) and unblocked.
- Plan 30-04 (`MetricsRoundTripE2ETests`) can now assert `orchestrator_dispatch_sent_total` + `orchestrator_result_consumed_total` series existence and the per-`ProcessorId` bottleneck PromQL against `localhost:9090` via the Plan-01 `PollPromForQuery` helper.
- No blockers.

## Self-Check: PASSED

- Files: OrchestratorMetrics.cs, OrchestratorMetricsFacts.cs, Program.cs, StepDispatcher.cs, ResultConsumer.cs, OrchestratorTestStubs.cs, ResultAckTests.cs, ResultConsumeTests.cs, StopConsumerLifecycleTests.cs, FireDispatchTests.cs, WorkflowFireJobScopeTests.cs — all FOUND (verified below).
- Commits: e79f3e8 (Task 1), a1488f0 (Task 2) — both FOUND in git log.

---
*Phase: 30-runtime-business-metrics*
*Completed: 2026-06-02*
