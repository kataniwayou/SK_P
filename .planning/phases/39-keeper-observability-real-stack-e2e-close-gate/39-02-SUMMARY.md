---
phase: 39-keeper-observability-real-stack-e2e-close-gate
plan: 02
subsystem: infra
tags: [opentelemetry, metrics, keeper, instrumentation, meterlistener, histogram, updowncounter, fault-consumer, l2-probe]

# Dependency graph
requires:
  - phase: 39-keeper-observability-real-stack-e2e-close-gate (Plan 01)
    provides: KeeperMetrics meter ("Keeper", 8 instruments) + KeeperMetricTags/FaultTags interned-label helper + Program.cs const-to-AddMeter registration (advice/Route A, histogram unit "s")
  - phase: 36-l2-health-probe-recovery-loop-dlqs
    provides: the two fault consumers + L2ProbeRecovery probe loop whose existing branches this plan instruments
provides:
  - "FaultEntryStepDispatchConsumer + FaultExecutionResultConsumer now emit keeper_fault_consumed (intake), keeper_workflow_paused (after Pause), keeper_workflow_resumed + keeper_recovered + keeper_recovery_duration{outcome=recovered} (Recovered branch), keeper_dlq_pushed + keeper_recovery_duration{outcome=gave_up} (give-up branch) — fault_type=dispatch / result"
  - "L2ProbeRecovery emits keeper_in_flight +1/entry -1/finally and keeper_l2_probe_failed per RedisException; RunAsync signature now (string entryId, string h, string procId, CancellationToken)"
  - "keeper_in_flight AND keeper_l2_probe_failed are BOTH tagged {ProcessorId} (OPEN QUESTION OQ-1 resolved: threaded procId, not the unlabelled fallback)"
  - "Hermetic KeeperMetricsFacts faked-flow proof (MeterListener) of every instrument + exact tag set + in_flight +1/-1 + no workflowId/correlationId label"
affects: [39-03-prometheus-scrape, keeper-observability]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Drive the REAL increment code (consumers via substituted ConsumeContext + probe loop direct) over a System.Diagnostics.Metrics.MeterListener that enables instruments where instrument.Meter.Name == \"Keeper\" and captures (name, value, tags) — hermetic, no real stack, no RealStack trait"
    - "Increment sites threaded into existing branches with NO control-flow change — increments only; recovery_duration recorded as sw.Elapsed.TotalSeconds (seconds, never ElapsedMilliseconds)"
    - "procId threaded through RunAsync so the probe-loop instruments carry the same bounded ProcessorId label the consumers use (cardinality consistency)"

key-files:
  created: []
  modified:
    - src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs
    - src/Keeper/Consumers/FaultExecutionResultConsumer.cs
    - src/Keeper/Recovery/L2ProbeRecovery.cs
    - tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs
    - tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs
    - tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs

key-decisions:
  - "OPEN QUESTION OQ-1 RESOLVED by threading: RunAsync(string entryId, string h, string procId, CancellationToken ct). BOTH keeper_in_flight and keeper_l2_probe_failed are tagged {ProcessorId=procId}. The unlabelled in_flight fallback was NOT used — threading only touched the two consumer call sites as predicted."
  - "Final RunAsync signature for Plan 03 scrape assertions: keeper_in_flight is a {ProcessorId}-labelled gauge (transient → 0 after the loop); keeper_l2_probe_failed_total carries {ProcessorId}."
  - "recovery_duration recorded in seconds (sw.Elapsed.TotalSeconds) matching the unit \"s\" buckets — Plan 03 should expect keeper_recovery_duration_seconds_{bucket,sum,count} with {outcome,ProcessorId}."

patterns-established:
  - "MeterListener faked-flow hermetic assertion: capture long+double measurements from the Keeper meter, assert exact instrument name + tag set + value (in_flight +1/-1) + absence of workflowId/correlationId across ALL measurements (T-39-03 cardinality ban)"

requirements-completed: [KMET-01, KMET-02, KMET-03]

# Metrics
duration: 9min
completed: 2026-06-06
---

# Phase 39 Plan 02: Keeper Metric Instrumentation Summary

**Threaded the eight `KeeperMetrics` instruments into the two fault consumers + the L2 probe loop at the exact existing branches (no control-flow change) — `fault_type`-tagged counters, a second-scale `recovery_duration` histogram, and `in_flight`/`l2_probe_failed` from the probe loop — and proved every instrument fires with the right tags and no `workflowId` leak via a hermetic `MeterListener` faked-flow test.**

## Performance

- **Duration:** ~9 min
- **Started:** 2026-06-06T10:36Z
- **Completed:** 2026-06-06T10:45Z
- **Tasks:** 2 (both TDD)
- **Files modified:** 6 (3 source MODIFY, 3 test MODIFY)

## Accomplishments
- **Both fault consumers instrumented** at the four existing branches with NO control-flow change: `keeper_fault_consumed`{fault_type,ProcessorId} + `Stopwatch.StartNew()` at intake; `keeper_workflow_paused`{ProcessorId} (no fault_type — workflow-scoped) after `Publish(PauseWorkflow)`; `keeper_workflow_resumed`{ProcessorId} + `keeper_recovered`{fault_type,ProcessorId} + `keeper_recovery_duration`{outcome=recovered,ProcessorId} in the Recovered branch; `keeper_dlq_pushed`{reason=probe_exhausted,fault_type,ProcessorId} + `keeper_recovery_duration`{outcome=gave_up,ProcessorId} in the give-up branch. `fault_type=dispatch` / `fault_type=result`. Duration recorded as `sw.Elapsed.TotalSeconds`.
- **`L2ProbeRecovery` instrumented:** `keeper_in_flight.Add(1)` on entry / `Add(-1)` in a `finally` wrapping the whole loop, and `keeper_l2_probe_failed.Add(1)` inside each `catch (RedisException)`. The probe body (READ + WRITE/DEL) and the RedisException-only catch semantics are unchanged.
- **OPEN QUESTION (OQ-1) resolved by threading:** `RunAsync(string entryId, string h, string procId, CancellationToken ct)` — both consumer call sites pass `inner.ProcessorId.ToString("D")`. Both `keeper_in_flight` and `keeper_l2_probe_failed` carry `{ProcessorId}`.
- **Hermetic `KeeperMetricsFacts` extended** with three faked-flow facts that drive the REAL increment code over a `MeterListener` ("Keeper" meter) and assert: the recovered flow fires all five recovered-path instruments with exact tags; the give-up flow fires `dlq_pushed`/`recovery_duration{gave_up}`/2×`l2_probe_failed`; `in_flight` measures +1 then -1 across `RunAsync` (both tagged ProcessorId); and NO measurement carries `workflowId`/`correlationId`.

## Task Commits

1. **Task 1 (TDD RED): instrumentation-site wiring facts** - `047531d` (test)
2. **Task 1 (TDD GREEN): thread KeeperMetrics into consumers + probe loop** - `7fe4db4` (feat)
3. **Task 2 (TDD): hermetic faked-flow MeterListener proof** - `f347dc8` (test)

**Plan metadata:** _(this SUMMARY + STATE/ROADMAP commit)_

_Task 1's RED gate (`047531d`) genuinely failed before implementation (the consumers/probe did not take `KeeperMetrics` / lacked `procId`). Task 2's deep hermetic flow assertions were authored after the increments (Task 1 GREEN already threaded them, since the consumers could not compile without the `procId`-bearing `RunAsync`), so they passed on first run — see TDD Gate Compliance below. No REFACTOR commit needed._

## Files Created/Modified
- `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` (MODIFY) - `KeeperMetrics metrics` 3rd ctor param + 6 increment sites (fault_type=dispatch) + Stopwatch + `using System.Diagnostics;`/`Keeper.Observability;`
- `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` (MODIFY) - same six increment sites, `fault_type=result`
- `src/Keeper/Recovery/L2ProbeRecovery.cs` (MODIFY) - `KeeperMetrics metrics` ctor param + `procId` param on `RunAsync` + in_flight ++/finally-- + l2_probe_failed per catch
- `tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs` (MODIFY) - 2 wiring facts (Task 1) + 3 hermetic faked-flow facts via `MeterListener` (Task 2)
- `tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs` (MODIFY) - updated `new L2ProbeRecovery(...)`/`RunAsync(...)` call sites + harness DI (`AddMetrics()` + `AddSingleton<KeeperMetrics>()`) for the new signatures
- `tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs` (MODIFY) - updated `new L2ProbeRecovery(...)`/`new FaultEntryStepDispatchConsumer(...)` call sites with a test-local `KeeperMetrics`

## Decisions Made
- **OQ-1 → thread `procId`.** Threading `procId` into `RunAsync` only touched the two consumer call sites (as the plan predicted), so the unlabelled-`in_flight` fallback was unnecessary. `keeper_in_flight` and `keeper_l2_probe_failed` both carry `{ProcessorId}` for cardinality consistency with the consumers.
- **Final RunAsync signature (for Plan 03):** `RunAsync(string entryId, string h, string procId, CancellationToken ct)`. Plan 03's scrape assertions should expect `keeper_in_flight{ProcessorId=...}` (transient gauge → 0 after the loop, assert PRESENCE best-effort, not a value) and `keeper_l2_probe_failed_total{ProcessorId=...}`.
- **Hermetic over harness for the faked flow.** The flow facts drive the consumer via a substituted `ConsumeContext` (mirroring `KeeperPausePublishTests`) + the real `L2ProbeRecovery`/`FakeRedis`, the lightest way to exercise the REAL increment code; the load-bearing assertion is instrument-name + tag-shape + no-workflowId, captured by the `MeterListener`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Updated existing Keeper test call sites for the new signatures**
- **Found during:** Task 1 (GREEN — building the test project after the ctor/RunAsync signature changes)
- **Issue:** `KeeperProbeLoopTests.cs` and `KeeperPausePublishTests.cs` constructed `L2ProbeRecovery`/`FaultEntryStepDispatchConsumer` and called `RunAsync` with the OLD signatures (no `KeeperMetrics`, no `procId`) → 10 CS7036 compile errors blocking the test build.
- **Fix:** Added a test-local `KeeperMetrics` factory (`AddMetrics().GetRequiredService<IMeterFactory>()`), passed `metrics` to each ctor, passed `inner.ProcessorId.ToString("D")` to each `RunAsync`, and registered `AddMetrics()` + `AddSingleton<KeeperMetrics>()` in the in-memory harness DI.
- **Files modified:** tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs, tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs
- **Verification:** Both classes rebuild 0/0 and pass (KeeperProbeLoopTests 6/6, KeeperPausePublishTests 2/2).
- **Committed in:** `7fe4db4` (Task 1 GREEN commit)

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** The signature change (planned in Task 2, but required at Task 1 build time because the consumers cannot compile without the `procId`-bearing `RunAsync`) rippled to two pre-existing Keeper test files. Updating their call sites was necessary for the test project to build. No scope creep — pure mechanical call-site adaptation, no behavioral test change.

## Issues Encountered
- **Two `using` fix-ups during the test build:** `KeeperProbeLoopTests.cs` needed `using System.Diagnostics.Metrics;` (for `IMeterFactory`) and `KeeperMetricsFacts.cs` needed `using Keeper;` (for `ProbeOptions`). Both caught at first Release build and folded into their respective commits (not runtime deviations).

## TDD Gate Compliance
- **Task 1 RED gate:** `047531d` (`test(39-02)`) — 2 failing structural wiring facts BEFORE implementation (consumers/probe did not take `KeeperMetrics`; `RunAsync` lacked `procId`). Verified RED (8 total, 2 failed). ✓
- **Task 1 GREEN gate:** `7fe4db4` (`feat(39-02)`) — implementation makes the wiring facts pass. ✓
- **Task 2:** `f347dc8` (`test(39-02)`) — the deep hermetic faked-flow assertions. **Honest note:** these were authored AFTER the Task-1 GREEN increments (the consumers could not compile without the `procId`-bearing `RunAsync`, so the source threading necessarily preceded the flow test). They are a falsifiable contract test (assert exact tags / values / no-workflowId), not a no-op, and ran GREEN against the already-threaded increments. The structural RED for the wiring did fire first in Task 1.

## Verification
- **Build:** `dotnet build src/Keeper/Keeper.csproj -c Release` → **0 Warning / 0 Error** (TreatWarningsAsErrors).
- **Hermetic test:** `BaseApi.Tests.exe --filter-not-trait "Category=RealStack" --filter-class "*KeeperMetricsFacts*"` → **11/11 passed, 0 failed** (1.4s). Touched classes also green: KeeperProbeLoopTests 6/6, KeeperPausePublishTests 2/2.
- **Grep gates:** both consumers contain `KeeperMetrics metrics` in the primary ctor; `FaultEntryStepDispatchConsumer.cs` contains `"dispatch"`, `FaultExecutionResultConsumer.cs` contains `"result"`; each consumer contains FaultConsumed/WorkflowPaused/WorkflowResumed/Recovered/DlqPushed `.Add` + `RecoveryDuration.Record`; `sw.Elapsed.TotalSeconds` present, NO `ElapsedMilliseconds` anywhere in src/Keeper; the only `workflowId` matches in src/Keeper are doc-comment prose explaining its deliberate absence (no `workflowId`-keyed tag in code). `L2ProbeRecovery.cs` contains `metrics.InFlight.Add(1`, `metrics.InFlight.Add(-1` (inside `finally`), `metrics.L2ProbeFailed.Add(1`; `RunAsync` has a `procId` param. Existing PauseWorkflow/ResumeWorkflow/dlq.Send/GetSendEndpoint control flow intact.
- **No file deletions** (`git diff --diff-filter=D HEAD~3 HEAD` empty); pre-existing untracked items (`.claude/`, `27-PATTERNS.md`, `psql-*.txt`, `launchSettings.json`) left untouched.

## Known Stubs
None — all eight instruments are now incremented at live emission sites and hermetically proven. No placeholder/empty-data paths introduced.

## Next Phase Readiness
- Plan 03 (Prometheus scrape) can assert the suffixed live series: `keeper_fault_consumed_total`/`keeper_recovered_total`/`keeper_dlq_pushed_total`/`keeper_workflow_paused_total`/`keeper_workflow_resumed_total`/`keeper_l2_probe_failed_total` (counters, all with the tags above), `keeper_in_flight` (bare gauge, `{ProcessorId}`, transient → assert presence best-effort), and `keeper_recovery_duration_seconds_{bucket,sum,count}` (`{outcome,ProcessorId}`).
- The locked `RunAsync` signature (`procId` 3rd param) and the `{ProcessorId}`-on-both-probe-instruments decision are recorded for Plan 03's query strings.

## Self-Check: PASSED
- FOUND: src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs
- FOUND: src/Keeper/Consumers/FaultExecutionResultConsumer.cs
- FOUND: src/Keeper/Recovery/L2ProbeRecovery.cs
- FOUND: tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs
- FOUND: .planning/phases/39-keeper-observability-real-stack-e2e-close-gate/39-02-SUMMARY.md
- FOUND commit: 047531d (test RED)
- FOUND commit: 7fe4db4 (feat GREEN)
- FOUND commit: f347dc8 (test hermetic)

---
*Phase: 39-keeper-observability-real-stack-e2e-close-gate*
*Completed: 2026-06-06*
