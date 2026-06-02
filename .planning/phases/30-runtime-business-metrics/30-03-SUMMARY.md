---
phase: 30-runtime-business-metrics
plan: 03
subsystem: observability
tags: [opentelemetry, prometheus, metrics, processor, counters, baseprocessor, observability]

# Dependency graph
requires:
  - phase: 30-runtime-business-metrics
    plan: 01
    provides: service.instance.id resource attribute on the metrics resource in BaseConsole.Core (every processor counter inherits the ambient service_instance_id Prometheus label)
  - phase: 26-baseprocessor-core
    provides: AddBaseProcessor composition root + the Phase-29 ConfigureOpenTelemetryLoggerProvider seam to mirror; EntryStepDispatchConsumer (the framework consumer + its two send paths)
provides:
  - BaseProcessor.Core code-owned "BaseProcessor" Meter (DI-singleton ProcessorMetrics built via IMeterFactory) with two Counter<long> instruments processor_dispatch_consumed + processor_result_sent (snake_case, no _total), inherited by every Processor.*
  - processor_dispatch_consumed increment at the top of EntryStepDispatchConsumer.Consume tagged ProcessorId
  - processor_result_sent increment after Send via a single SendResult(ExecutionResult) owner covering both send paths, tagged ProcessorId + outcome
affects: [30-04-metrics-e2e]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Code-owned Meter as a sealed DI-singleton built from IMeterFactory.Create(MeterName) (NEVER a static Meter — cross-test instrument leak); the MeterName const referenced by BOTH Create and AddMeter (D-02 symmetry)"
    - "Register the meter via ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(...)) inside AddBaseProcessor — the meter-provider analog of the Phase-29 ConfigureOpenTelemetryLoggerProvider seam — NOT in BaseConsoleObservabilityExtensions (the one-way BaseProcessor.Core -> BaseConsole.Core compile firewall, Landmine 1)"
    - "Single private SendResult(ExecutionResult) owner so EVERY send path (early Failed/Cancelled + the per-result loop) is counted exactly once, incrementing AFTER the confirmed Send (D-04)"

key-files:
  created:
    - src/BaseProcessor.Core/Observability/ProcessorMetrics.cs
    - tests/BaseApi.Tests/Processor/ProcessorMetricsFacts.cs
  modified:
    - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
    - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
    - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
    - tests/BaseApi.Tests/Processor/FakeProcessorContext.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs

key-decisions:
  - "ProcessorMetrics is a sealed DI-singleton built from IMeterFactory (mirrors the Plan 30-02 OrchestratorMetrics shape exactly); MeterName const \"BaseProcessor\" referenced by both Create and AddMeter (D-02)"
  - "Meter registered inside AddBaseProcessor via ConfigureOpenTelemetryMeterProvider (firewall-correct seam, Landmine 1) — NOT in BaseConsoleObservabilityExtensions, which has no reference to BaseProcessor.Core and cannot see ProcessorMetrics.MeterName"
  - "processor_result_sent routes through a single SendResult owner so no send path is uncounted; outcome tag is StepOutcome.ToString().ToLowerInvariant() ∈ {completed,failed,cancelled} (build paths never emit Processing)"
  - "context.Id!.Value bang is justified — Consume runs only post-MarkHealthy (the runtime binds queue:{id:D} after Healthy), so identity is resolved; documented inline so reviewers do not flag a NRE (Landmine 2)"

patterns-established:
  - "Code-owned framework Meter inherited by every concrete via the composition root + the ConfigureOpenTelemetry*Provider seam (the metrics analog of the Phase-29 log-enricher seam)"

requirements-completed: [METRIC-05]

# Metrics
duration: 21min
completed: 2026-06-02
---

# Phase 30 Plan 03: Processor Business Counters (METRIC-05) Summary

**`BaseProcessor.Core` now owns a DI-singleton `ProcessorMetrics` (built via `IMeterFactory.Create("BaseProcessor")`) exposing two `Counter<long>` instruments — `processor_dispatch_consumed` and `processor_result_sent` — inherited by every `Processor.*`; `EntryStepDispatchConsumer` increments dispatch_consumed at the top of `Consume` (tagged `ProcessorId`) and result_sent after every `Send` via a single `SendResult` owner (tagged `ProcessorId` + `outcome`), the meter registered via the firewall-correct `ConfigureOpenTelemetryMeterProvider` seam inside `AddBaseProcessor` — the processor half of the bottleneck PromQL.**

## Performance

- **Duration:** ~21 min
- **Started:** 2026-06-02T20:19:09Z
- **Completed:** 2026-06-02T20:40:10Z
- **Tasks:** 2
- **Files:** 8 (2 created, 6 modified)

## Accomplishments

- Added `ProcessorMetrics` — a `sealed` DI-singleton built from `IMeterFactory.Create("BaseProcessor")` (NEVER a `static Meter`, so no cross-test instrument leak in the shared hermetic process), with two `Counter<long>` named `processor_dispatch_consumed` + `processor_result_sent` (snake_case, NO `_total` — the collector's `add_metric_suffixes` default appends it). Owned by `BaseProcessor.Core` and inherited by every concrete `Processor.*`.
- Registered the `"BaseProcessor"` meter inside `AddBaseProcessor` (step 6c) via `AddSingleton<ProcessorMetrics>()` + `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(ProcessorMetrics.MeterName))` — the exact meter-provider analog of the Phase-29 `ConfigureOpenTelemetryLoggerProvider` log-enricher seam (both from `OpenTelemetry.Extensions.Hosting`). It attaches additively to the MeterProvider that the shared `AddBaseConsoleObservability` built. This is the Landmine-1 compile-firewall fix: it is NOT in `BaseConsoleObservabilityExtensions`, which has no project reference to `BaseProcessor.Core` and therefore cannot see `ProcessorMetrics.MeterName`.
- `EntryStepDispatchConsumer.Consume` increments `DispatchConsumed.Add(1, ProcessorId)` at the very top (D-07), counting every consumed dispatch (an `Immediate(3)` retry re-increments — accepted rate noise). Tag key is the literal PascalCase `"ProcessorId"` (the collector preserves tag-key case), value `.ToString("D")` (matches `queue:{id:D}`). No `workflowId`.
- Introduced a single private `SendResult(ExecutionResult)` owner that increments `ResultSent.Add(1, ProcessorId, outcome)` AFTER the confirmed `Send` (D-04/D-08). Renamed the prior `SendOne` to `SendResult` and routed BOTH send paths through it — all four early `Failed`/`Cancelled` returns AND the per-result Completed loop — so no send path is uncounted. The `outcome` tag is `result.Outcome.ToString().ToLowerInvariant()` ∈ {completed, failed, cancelled} (the build paths never emit `Processing`, Pitfall 3). `CancellationToken.None` and the infra-Send-fault-propagates contract (D-15) are preserved; the counter increments only after a confirmed Send.
- Added the hermetic `ProcessorMetricsFacts` (real `IMeterFactory`; non-null counters + `MeterName == "BaseProcessor"`), mirroring `OrchestratorMetricsFacts`.

## Task Commits

Each task was committed atomically (scoped paths only — the in-progress `.planning/` archive deletions were left untouched, NOT staged, NOT reverted):

1. **Task 1: ProcessorMetrics holder + register the BaseProcessor meter (firewall-correct seam)** — `bb77699` (feat)
2. **Task 2: increment processor_dispatch_consumed at consume top + route both send paths through SendResult{outcome}** — `7a4d0d4` (feat)

## Files Created/Modified

- `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` (NEW) — IMeterFactory holder, `const MeterName = "BaseProcessor"`, two snake_case no-`_total` counters.
- `tests/BaseApi.Tests/Processor/ProcessorMetricsFacts.cs` (NEW) — hermetic holder fact (D-02 const-mismatch guard).
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` — step 6c: `AddSingleton<ProcessorMetrics>` + `ConfigureOpenTelemetryMeterProvider AddMeter`; added `using OpenTelemetry.Metrics;`.
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` — `ProcessorMetrics metrics` ctor param; top-of-Consume `DispatchConsumed.Add`; `SendResult` single owner with `ResultSent.Add(ProcessorId, outcome)`; both send paths routed through it.
- `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` — `Metrics()` real-IMeterFactory helper threaded through `Build`.
- `tests/BaseApi.Tests/Processor/FakeProcessorContext.cs` — `Id` defaults to a fresh Guid (the post-Healthy invariant the consumer relies on; the not-yet-Healthy liveness facts still override `Id = null`).
- `tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs` — threaded `DispatchTestKit.Metrics()` into the two direct consumer constructions.
- `tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs` — registered `ProcessorMetrics` in the runtime ConnectReceiveEndpoint harness DI.

## Decisions Made

- **DI-singleton via IMeterFactory, MeterName const referenced twice (D-01/D-02/D-03).** Identical shape to Plan 30-02's `OrchestratorMetrics`. No `static Meter` field (cross-test leak). The collector appends `_total`, so the instrument names carry none.
- **Firewall-correct seam (Landmine 1).** The meter is registered inside `AddBaseProcessor` via `ConfigureOpenTelemetryMeterProvider`. Putting `AddMeter("BaseProcessor")` in `BaseConsoleObservabilityExtensions` would be a compile error — `BaseConsole.Core` has no reference to `BaseProcessor.Core`. Verified: `grep` finds no `BaseProcessor` token in `BaseConsoleObservabilityExtensions.cs`.
- **Single SendResult owner (D-08).** Every result — early `Failed`/`Cancelled` and per-result Completed — flows through one method that increments after Send, so the count is exhaustive and never double-routed. `outcome` is derived from the framework-owned `StepOutcome` enum (never user input), bounded to 3 values (T-30-05).
- **The `context.Id!` bang is justified (Landmine 2).** `Consume` runs only post-`MarkHealthy` (the runtime binds `queue:{id:D}` after Healthy via `ProcessorStartupOrchestrator`), so identity is resolved; documented inline so the bang isn't mis-flagged as a NRE.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Threaded a real-IMeterFactory ProcessorMetrics through the hermetic consumer fixtures**
- **Found during:** Task 2
- **Issue:** The new required `ProcessorMetrics metrics` ctor param broke the hermetic facts that construct `EntryStepDispatchConsumer` — `DispatchTestKit.Build` (used by all the `Dispatch*Facts`) and the two direct constructions in `EntryStepDispatchScopeTests`, plus the DI-resolved runtime harness in `EntryStepDispatchRuntimeScopeTests` (which `ConfigureConsumer<EntryStepDispatchConsumer>` and so needs `ProcessorMetrics` in its container).
- **Fix:** Added `DispatchTestKit.Metrics()` (real `IMeterFactory` → `ProcessorMetrics`, no-op increments in-test — mirrors the existing `OrchestratorTestStubs.Metrics()` precedent from 30-02), threaded it through `Build`, into the two `EntryStepDispatchScopeTests` ctors, and registered it via `.AddSingleton(DispatchTestKit.Metrics())` in the runtime-scope harness.
- **Files modified:** DispatchTestKit.cs, EntryStepDispatchScopeTests.cs, EntryStepDispatchRuntimeScopeTests.cs
- **Verification:** the 20 dispatch-consumer facts pass in isolation; full hermetic suite 409/409.
- **Committed in:** 7a4d0d4 (Task 2 commit)

**2. [Rule 3 - Blocking] FakeProcessorContext.Id defaults to a resolved Guid (post-Healthy invariant)**
- **Found during:** Task 2
- **Issue:** The top-of-Consume increment dereferences `context.Id!.Value`. Every consumer fact built `new FakeProcessorContext { ... }` WITHOUT setting `Id` (it defaulted to `null`), so the new increment would NRE in every consumer fact — even though at runtime `Consume` only runs post-Healthy with a resolved Id.
- **Fix:** Defaulted `FakeProcessorContext.Id` to `Guid.NewGuid()` so the fixtures reflect the real post-Healthy invariant. The liveness facts that exercise the not-yet-Healthy "no Id → no write" path already override this explicitly with `Id = null`, so they are unaffected.
- **Files modified:** FakeProcessorContext.cs
- **Verification:** full hermetic suite 409/409 (all liveness facts still GREEN — they set `Id` explicitly).
- **Committed in:** 7a4d0d4 (Task 2 commit)

---

**Total deviations:** 2 (both Rule 3 blocking-resolved test-fixture adjustments; no production-behavior change beyond the plan).
**Impact on plan:** None on the production contract — the interfaces and acceptance criteria are all satisfied as written. The fixtures are now honest about the post-Healthy identity invariant.

## Verification Evidence

- `dotnet build src/BaseProcessor.Core -c Debug` = 0 Warning / 0 Error (exit 0).
- `dotnet build SK_P.sln -c Release` = 0 Warning / 0 Error (no nullable-ref warning on the documented `context.Id!` bang).
- Hermetic suite `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack"` = **Passed 409 / Failed 0** (was 410 pre-plan → the count reconciles: +2 new `ProcessorMetricsFacts`, and this MTP-native filter correctly excludes the 3 `Category=RealStack` E2Es; zero regression). Targeted `--filter-class "*ProcessorMetricsFacts"` = 2/2; the 20 dispatch-consumer facts (incl. the runtime-connected scope test) = 20/20.
- `grep` firewall held: no `BaseProcessor` token in `BaseConsoleObservabilityExtensions.cs`. `grep` of `ProcessorMetrics.cs` finds no `_total`. `grep` of the consumer confirms the metric `.Add(` tags carry only `ProcessorId` (+ `outcome`), no `workflowId`, no `"processing"` literal, and no raw `endpoint.Send(er)` loop bypassing `SendResult`.

### Note on the VSTest filter vs the MTP runner

The plan's `<verify>` blocks use `dotnet test ... --filter "Category!=RealStack"`. This MTP-based test project IGNORES the VSTest-style `--filter` (it emits `MTP0001: VSTest-specific properties are set but will be ignored`), so that invocation runs the FULL suite INCLUDING the `Category=RealStack` E2Es — and `SampleRoundTripE2ETests.LiveSampleProcessor_RoundTrip_...` fails purely because the live compose stack (RabbitMQ/Postgres) is not running in this hermetic environment (a pre-existing environment dependency, out of scope per the SCOPE BOUNDARY rule — not caused by this plan). The MTP-native `-- --filter-not-trait "Category=RealStack"` (the form the close-gate scripts use) correctly excludes them and yields 409/409 GREEN. The real-stack processor counters will be exercised live by Plan 30-04's `MetricsRoundTripE2ETests`.

## Issues Encountered

None beyond the two Rule-3 fixture adjustments above. The production code built clean on the first attempt with no nullable-ref warning.

## Next Phase Readiness

- Plan 30-04 (`MetricsRoundTripE2ETests`) can now assert the full processor-side bottleneck series live: `processor_dispatch_consumed_total` and `processor_result_sent_total{outcome}` (the collector appends `_total`), keyed by `ProcessorId`, alongside the orchestrator counters from 30-02 — both carrying the ambient `service_instance_id` from 30-01.
- No blockers. Phase 30 = 3/4 plans; 30-04 (metrics E2E, METRIC-06) is the remaining plan.

## Self-Check: PASSED

- Files: ProcessorMetrics.cs, ProcessorMetricsFacts.cs, BaseProcessorServiceCollectionExtensions.cs, EntryStepDispatchConsumer.cs, DispatchTestKit.cs, FakeProcessorContext.cs, EntryStepDispatchScopeTests.cs, EntryStepDispatchRuntimeScopeTests.cs — all present.
- Commits: bb77699 (Task 1, feat), 7a4d0d4 (Task 2, feat) — both in git log.

---
*Phase: 30-runtime-business-metrics*
*Completed: 2026-06-02*
