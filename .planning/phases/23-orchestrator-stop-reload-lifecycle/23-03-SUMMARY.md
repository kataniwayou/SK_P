---
phase: 23-orchestrator-stop-reload-lifecycle
plan: 03
subsystem: orchestrator-runtime
tags: [orchestrator, l1-store, quartz, cronos, fire-job, masstransit-send, scaling, wave-2]

# Dependency graph
requires:
  - phase: 23-orchestrator-stop-reload-lifecycle
    plan: 01
    provides: "Messaging.Contracts StepProjection (int EntryCondition) + EntryStepDispatch + IExecutionCorrelated + LivenessProjection/WorkflowRootProjection read-shapes"
  - phase: 23-orchestrator-stop-reload-lifecycle
    plan: 02
    provides: "Quartz 3.18.1 (CPM-pinned + referenced by Orchestrator) + OrchestratorL2Keys ParentIndex/Step forwarders"
provides:
  - "Singleton IWorkflowL1Store (ConcurrentDictionary entries + per-workflowId Wait(0) drop-if-held SemaphoreSlim stripe) — ORCH-SCALE-01"
  - "CronInterval: Cronos CronFormat.Standard next-occurrence + delta-seconds (L1 liveness interval) — D-08/D-09"
  - "WorkflowScheduler: schedule/reschedule/unschedule a self-rescheduling one-shot keyed by JobKey(jobId) — ORCH-SCHED-01"
  - "WorkflowFireJob (IJob, DisallowConcurrentExecution): fire -> Send EntryStepDispatch per entry step + L1 liveness refresh + self-reschedule — ORCH-FIRE-01"
affects: [plan-04-hydration-consumers-program, plan-05-fire-dispatch-harness]

# Tech tracking
tech-stack:
  added:
    - "Cronos PackageReference on src/Orchestrator (already CPM-pinned 0.13.0 in Phase 23 P02; first Orchestrator consumer)"
  patterns:
    - "Per-workflowId lock-striping via ConcurrentDictionary<Guid,SemaphoreSlim> with Wait(0) drop-if-held (never blocking, never disposed — Pitfall 5)"
    - "Self-rescheduling one-shot Quartz job: ScheduleJob(job, trigger) on first schedule, ScheduleJob(trigger) for the existing job on reschedule; DeleteJob(JobKey) for atomic teardown"
    - "Fire path mints a fresh per-fire correlationId on the message body (D-01 body-source-of-truth) and Sends (NOT Publishes) to queue:{processorId:D}"

key-files:
  created:
    - src/Orchestrator/L1/WorkflowL1.cs
    - src/Orchestrator/L1/IWorkflowL1Store.cs
    - src/Orchestrator/L1/WorkflowL1Store.cs
    - src/Orchestrator/Scheduling/CronInterval.cs
    - src/Orchestrator/Scheduling/WorkflowScheduler.cs
    - src/Orchestrator/Scheduling/WorkflowFireJob.cs
    - tests/BaseApi.Tests/Orchestrator/CronIntervalTests.cs
    - tests/BaseApi.Tests/Orchestrator/SchedulingTests.cs
  modified:
    - src/Orchestrator/Orchestrator.csproj

key-decisions:
  - "WorkflowScheduler injects a concrete Quartz IScheduler (not ISchedulerFactory) — the test builds a real RAMJobStore IScheduler directly and Plan 04 resolves the hosted IScheduler from DI; one fewer indirection and the SUT is trivially constructable in-test"
  - "WorkflowFireJob liveness refresh guards against a null Liveness (LivenessProjection is a reference-type record initialized via default!): if null it constructs a fresh active LivenessProjection, otherwise it uses `with { Timestamp = nowUtc }` to preserve interval/status — zero L2 writes either way"
  - "Self-reschedule only fires when CronInterval.NextOccurrence is non-null (no future occurrence = business skip, mirrors ScheduleAsync's skip)"

patterns-established:
  - "Tests calling Quartz IScheduler methods that accept a CancellationToken must pass TestContext.Current.CancellationToken (xUnit1051 is error-level in this repo) — established the ct-local convention for the Orchestrator scheduling slice"

requirements-completed: [ORCH-SCHED-01, ORCH-FIRE-01, ORCH-SCALE-01]

# Metrics
duration: ~5min
completed: 2026-05-31
---

# Phase 23 Plan 03: Orchestrator Runtime Core (L1 Store + Scheduling + Fire Job) Summary

**The in-memory lifecycle heart: a singleton `IWorkflowL1Store` (ConcurrentDictionary + per-workflowId `Wait(0)` drop-if-held stripe), the `CronInterval` Cronos helper, the `WorkflowScheduler` (self-rescheduling one-shot keyed by `JobKey(jobId)`), and `WorkflowFireJob` (fire -> `Send` one `EntryStepDispatch` per entry step + in-memory liveness refresh + self-reschedule) — no static/global lock anywhere.**

## Performance

- **Duration:** ~5 min
- **Tasks:** 3 (Task 2 TDD)
- **Files:** 9 (8 created, 1 modified)

## Accomplishments
- `WorkflowL1` / `IWorkflowL1Store` / `WorkflowL1Store` — singleton store backed by `ConcurrentDictionary<Guid, WorkflowL1>` for entries and `ConcurrentDictionary<Guid, SemaphoreSlim>` for the per-workflowId stripe; `TryAcquire` is `Wait(0)` (drop-if-held, never blocking), stripes are never disposed (Pitfall 5), and there is no static/global lock (ORCH-SCALE-01).
- `CronInterval` — Cronos `CronFormat.Standard` `NextOccurrence` (strictly-future UTC) + `IntervalSeconds` (delta between the next two occurrences; `*/5` -> 300s; impossible cron -> null/0) (D-08/D-09).
- `WorkflowScheduler` — `ScheduleAsync` builds a one-shot `SimpleTrigger` (`WithMisfireHandlingInstructionFireNow`) for `JobBuilder.Create<WorkflowFireJob>()` keyed by `JobKey(jobId.ToString("D"))`; `RescheduleAsync` adds a new trigger to the existing job (Pitfall 4b); `UnscheduleAsync` calls `DeleteJob(JobKey)` (atomic — Pitfall 4c) (ORCH-SCHED-01).
- `WorkflowFireJob` — `[DisallowConcurrentExecution] IJob` that mints a fresh `NewId.NextGuid()` correlationId per fire (D-05), `Send`s one `EntryStepDispatch` per entry step to `queue:{processorId:D}` (Send NOT Publish — D-10), refreshes L1 liveness timestamp in-memory (zero L2 writes), and self-reschedules off the next Cronos occurrence (ORCH-FIRE-01).
- Two unit-test classes green: `CronIntervalTests` 3/3 (interval math + UTC future + impossible-cron skip), `SchedulingTests` 2/2 (one started job per workflow keyed by jobId with Normal-state triggers; unschedule removes job+triggers).

## Task Commits

1. **Task 1: WorkflowL1 + IWorkflowL1Store + WorkflowL1Store** — `a2a0392` (feat)
2. **Task 2 RED: failing CronInterval + Scheduler tests** — `643b306` (test)
3. **Task 2 GREEN: CronInterval + WorkflowScheduler (+ FireJob stub + Cronos ref)** — `1246426` (feat)
4. **Task 3: WorkflowFireJob fire -> Send + liveness + reschedule** — `873d4b5` (feat)

## Decisions Made
- `WorkflowScheduler` injects a concrete `IScheduler` rather than `ISchedulerFactory` — the test constructs a real RAMJobStore scheduler directly and Plan 04 resolves the Quartz-hosted `IScheduler` from DI. One fewer indirection; trivially testable.
- Fire-path liveness refresh guards a null `Liveness` (the record is initialized `default!`): null -> new `LivenessProjection(nowUtc, 0, "active")`; non-null -> `with { Timestamp = nowUtc }` preserving interval/status. No L2 write in either branch.
- Self-reschedule only when `CronInterval.NextOccurrence` is non-null (no future occurrence = business skip, mirroring `ScheduleAsync`).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Cronos not referenced by the Orchestrator project**
- **Found during:** Task 2 (CronInterval build)
- **Issue:** `CronInterval` uses `Cronos.CronExpression`, but `src/Orchestrator/Orchestrator.csproj` had no Cronos reference (Cronos was only consumed by BaseApi.Service). Build failed `CS0246: Cronos could not be found`.
- **Fix:** Added `<PackageReference Include="Cronos" />` (no `Version=` — CPM; already pinned 0.13.0 in Phase 23 P02's props). Build returned 0/0.
- **Files modified:** src/Orchestrator/Orchestrator.csproj
- **Committed in:** `1246426` (Task 2 GREEN commit)

**2. [Rule 3 - Blocking] xUnit1051 (CancellationToken) is error-level — SchedulingTests used CancellationToken.None**
- **Found during:** Task 2 (running SchedulingTests)
- **Issue:** The repo's xUnit analyzer treats xUnit1051 as an error: every Quartz call accepting a `CancellationToken` (`ScheduleAsync`, `UnscheduleAsync`, `GetJobKeys`, `GetTriggersOfJob`, `GetTriggerState`, `Shutdown`) must use `TestContext.Current.CancellationToken`. The test failed to build.
- **Fix:** Added a `var ct = TestContext.Current.CancellationToken;` local per test and threaded it through all token-accepting calls (matches the established convention in `StartStopConsumerAckTests`).
- **Files modified:** tests/BaseApi.Tests/Orchestrator/SchedulingTests.cs
- **Committed in:** `1246426` (Task 2 GREEN commit)

**3. [Rule 1 - Bug-prevention] CronInterval XML-doc contained the literal `DateTime.UtcNow`/`DateTime.Now` tokens**
- **Found during:** Task 2 (post-GREEN invariant check)
- **Issue:** The acceptance criterion "does NOT contain 'DateTime.UtcNow' or 'DateTime.Now'" is a substring check; a `<see cref="DateTime.UtcNow"/>` in the doc comment (which warned NEVER to use them) would trip a verifier false-positive even though no such code exists.
- **Fix:** Rephrased the comment to "ambient wall-clock statics" — no literal token remains; the file genuinely uses only `timeProvider.GetUtcNow().UtcDateTime`.
- **Files modified:** src/Orchestrator/Scheduling/CronInterval.cs
- **Committed in:** `1246426` (Task 2 GREEN commit)

**Total deviations:** 3 auto-fixed (2 blocking + 1 bug-prevention). No scope creep — all confined to making the planned files compile/test and pass the planned greps.

## Authentication Gates
None.

## Threat Surface
No new surface beyond the plan's `<threat_model>`. T-23-05 (queue:{processorId} address): processorId read from L1 (L2-author-supplied, trusted at write time), `:D`-formatted — no new trust boundary. T-23-06 (semaphore DoS): `Wait(0)` only, never disposed, bounded population — acceptance confirms no bare blocking `Wait()`. T-23-07 (double-dispatch): `[DisallowConcurrentExecution]` + reschedule-adds-trigger + `DeleteJob`-atomic.

## TDD Gate Compliance
Task 2 followed RED (`643b306` test) -> GREEN (`1246426` feat). Plan type is `execute` (per-task TDD, not plan-level); gate sequence satisfied for the TDD task.

## Known Stubs
None. (The Task-2 `WorkflowFireJob` stub created so `WorkflowScheduler` could compile was fully implemented in Task 3 — commit `873d4b5` — and is not a residual stub.)

## Next Phase Readiness
- Runtime core is in place. Plan 04 wires `IWorkflowL1Store` (singleton), `WorkflowScheduler`, and the Quartz `IScheduler` into Program.cs + the hydration/consumer path; Plan 05 asserts the fire->Send end-to-end via the synthetic consumer helper.
- No blockers.

## Self-Check: PASSED
- All 8 created files verified present on disk.
- All 4 task commits verified in git history (`a2a0392`, `643b306`, `1246426`, `873d4b5`).
