---
phase: 23-orchestrator-stop-reload-lifecycle
plan: 05
subsystem: testing
tags: [masstransit, quartz, redis, nsubstitute, xunit, orchestrator, lifecycle, send]

# Dependency graph
requires:
  - phase: 23-03-ef-core-persistence-base (23-03)
    provides: WorkflowL1Store, WorkflowScheduler, WorkflowFireJob, CronInterval (L1 + scheduling under test)
  - phase: 23-04 (hydration + consumers)
    provides: HydrationBackgroundService, gated StartOrchestrationConsumer/StopOrchestrationConsumer, WorkflowLifecycle, StartupGate (the lifecycle under test)
provides:
  - Synthetic MassTransit consumer (CapturingDispatchConsumer) bound to ReceiveEndpoint({processorId:D}) proving queue:{processorId} <-> short-name endpoint mapping
  - Goal-backward acceptance proofs for the full Phase 23 lifecycle (fire-dispatch, start/stop, ack semantics, no-global-lock)
  - Continuously-enforced zero-L2-write invariant via extended DidNotReceive guards + byte-identical L2 snapshot
  - Reflection guard against static lock/mutex/semaphore on the four lifecycle production types (ORCH-SCALE-01 automatable half)
affects: [phase-24, processor-milestone, orchestrator-replica-coordination]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Synthetic in-memory MassTransit harness: AddMassTransitTestHarness + ReceiveEndpoint($\"{processorId:D}\") + Consumed.Select<EntryStepDispatch> to assert Send field correctness without a real broker"
    - "Per-scheduler Quartz instance-name isolation (quartz.scheduler.instanceName=test-{guid:N}) so parallel scheduler-using test classes do not collide in StdSchedulerFactory's process-wide repository"
    - "Zero-L2-write proof: DidNotReceive().StringSetAsync/SetAddAsync/KeyDeleteAsync on any skp: key + byte-identical L2 snapshot before/after"
    - "Reflection lifecycle guard: enumerate Static|NonPublic|Public fields and assert no SemaphoreSlim/Mutex/static lock-object gates the lifecycle"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/CapturingDispatchConsumer.cs
    - tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs
    - tests/BaseApi.Tests/Orchestrator/StartConsumerLifecycleTests.cs
    - tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs
    - tests/BaseApi.Tests/Orchestrator/AckSemanticsTests.cs
    - tests/BaseApi.Tests/Orchestrator/NoGlobalLockTests.cs
  modified:
    - tests/BaseApi.Tests/Orchestrator/HydrationTests.cs (Quartz instance-name isolation)
    - tests/BaseApi.Tests/Orchestrator/SchedulingTests.cs (Quartz instance-name isolation)

key-decisions:
  - "Drive the fire by constructing WorkflowFireJob directly against the harness ISendEndpointProvider + seeded L1 + RAMJobStore scheduler + FakeTimeProvider, then Execute with a substituted IJobExecutionContext carrying workflowId — exercises the real Send path through the in-memory bus"
  - "Split ORCH-SCALE-01 into an automatable reflection guard (no static lock/mutex) + a documented manual design review for the non-reflectable process-uniqueness half, per 23-VALIDATION.md Manual-Only Verifications"
  - "Per-scheduler unique Quartz instanceName to fix cross-class scheduler-repository collision (Rule 1 deviation, commit 376c9db)"

patterns-established:
  - "Synthetic-consumer harness for asserting Send dispatch field correctness end-to-end without a real broker"
  - "Zero-L2-write continuous enforcement via NSubstitute DidNotReceive on all write/delete mux methods"

requirements-completed: [ORCH-FIRE-01, ORCH-CONSUME-01, ORCH-STOP-01, ORCH-SCALE-01, ORCH-ACK-01]

# Metrics
duration: ~30min (Tasks 1-3 impl + Rule 1 fix) + blocking checkpoint (operator-approved)
completed: 2026-05-31
---

# Phase 23 Plan 05: Lifecycle Harness + Review Tests Summary

**Goal-backward acceptance suite — synthetic MassTransit consumer proving fire->dispatch field correctness, start/stop lifecycle, ack semantics, and a no-global-lock reflection guard — closing the verification gap for the full Phase 23 orchestrator lifecycle.**

## Performance

- **Duration:** ~30 min (Tasks 1-3 implementation + Rule 1 isolation fix) followed by the blocking human-verify checkpoint (operator-approved)
- **Started:** 2026-05-31
- **Completed:** 2026-05-31
- **Tasks:** 4 (3 auto + 1 blocking human-verify gate)
- **Files modified:** 8 (6 created + 2 modified)

## Accomplishments

- **ORCH-FIRE-01:** CapturingDispatchConsumer + FireDispatchTests prove one EntryStepDispatch per entry step per fire with correct StepId/ProcessorId/Payload and Guid.Empty execution/entry ids; correlationId is non-empty and differs across two consecutive fires; L1 liveness timestamp advances to FakeTimeProvider-now on fire with zero L2 writes. The short-name `queue:{processorId}` <-> `ReceiveEndpoint({processorId:D})` mapping (RESEARCH assumption A2) is verified.
- **ORCH-CONSUME-01 / ORCH-STOP-01:** Start/StopConsumerLifecycleTests prove Start hydrates+schedules ONLY the consumed workflow (gate-closed drops), and Stop resolves the jobId from L1, deletes the Quartz job (CheckExists false), clears L1, and leaves L2 byte-identical (zero StringSetAsync/SetAddAsync/KeyDeleteAsync on any skp: key).
- **ORCH-ACK-01:** AckSemanticsTests prove absent-workflow start/stop acks without throwing (no _error), a Redis-unreachable consume propagates (Assert.ThrowsAsync), and a corrupt startup entry is skipped while the rest hydrate (host stays up, store Count == 1).
- **ORCH-SCALE-01:** NoGlobalLockTests reflection guard confirms no static SemaphoreSlim/Mutex/lock-object gates WorkflowL1Store / WorkflowScheduler / both consumers / HydrationBackgroundService; the non-reflectable process-uniqueness half passed the manual design review (Task 4).

## Task Commits

Each task was committed atomically:

1. **Task 1: CapturingDispatchConsumer + FireDispatchTests (ORCH-FIRE-01)** - `478116b` (test)
2. **Task 2: Start/Stop consumer lifecycle tests + shared mux stubs (ORCH-CONSUME-01, ORCH-STOP-01)** - `10459de` (test)
3. **Task 3: AckSemanticsTests + NoGlobalLockTests (ORCH-ACK-01, ORCH-SCALE-01)** - `bca48d2` (test)
4. **Rule 1 fix: per-scheduler Quartz instance-name isolation** - `376c9db` (fix)
5. **Task 4: checkpoint evidence recorded** - `043dbc5` (docs) — blocking human-verify gate, operator-approved

**Plan metadata:** this commit (docs: complete plan)

## Files Created/Modified

- `tests/BaseApi.Tests/Orchestrator/CapturingDispatchConsumer.cs` - Synthetic `IConsumer<EntryStepDispatch>` (Consume returns Task.CompletedTask; the harness Consumed filter captures), reusable across FIRE + CONSUME tests
- `tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs` - Fire->dispatch field correctness + correlationId-differs-across-fires + liveness-advance / zero-L2-write
- `tests/BaseApi.Tests/Orchestrator/StartConsumerLifecycleTests.cs` - Start hydrate+schedule of only the consumed workflow; gate-closed drop
- `tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs` - Stop teardown (job deleted, L1 cleared) + byte-identical L2 + DidNotReceive StringSetAsync/SetAddAsync/KeyDeleteAsync
- `tests/BaseApi.Tests/Orchestrator/AckSemanticsTests.cs` - Absent ack / Redis-unreachable propagate / corrupt-startup-entry skip-and-stay-up
- `tests/BaseApi.Tests/Orchestrator/NoGlobalLockTests.cs` - Reflection guard: no static lock/mutex/semaphore on the four lifecycle types
- `tests/BaseApi.Tests/Orchestrator/HydrationTests.cs` - Added per-scheduler Quartz instance-name isolation (Rule 1)
- `tests/BaseApi.Tests/Orchestrator/SchedulingTests.cs` - Added per-scheduler Quartz instance-name isolation (Rule 1)

## Verification Evidence

- **Orchestrator slice:** `dotnet test ... -- --filter-class BaseApi.Tests.Orchestrator.*` = Passed: 31, Failed: 0.
- **Full suite:** `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` = Passed: 295, Failed: 0 (~3m30s; real-stack E2E ran live against the full v3.4.0 stack).
- **Zero-warning build:** Release = 0 Warning(s) / 0 Error(s).
- **ORCH-SCALE-01 design review:** PASSED — no static/global lock or process-uniqueness assumption on WorkflowL1Store / WorkflowScheduler / both consumers / HydrationBackgroundService. All state is per-instance (L1 ConcurrentDictionary, per-process RAMJobStore, per-workflowId stripe). A 2nd replica N×-dispatches (accepted/deferred), not crashes.
- **Operator confirmation:** checkpoint APPROVED (orchestrator re-verified the recorded evidence).

## Decisions Made

- Drive the fire via a directly-constructed `WorkflowFireJob` over the harness `ISendEndpointProvider` (real Send path through the in-memory bus) rather than mocking the dispatch — keeps the field-correctness assertion end-to-end.
- Split ORCH-SCALE-01 into an automatable reflection guard plus a documented manual design review for the non-reflectable process-uniqueness half (per 23-VALIDATION.md Manual-Only Verifications).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Per-scheduler Quartz instance-name isolation**
- **Found during:** Tasks 1-3 (scheduler-using test classes run in parallel)
- **Issue:** `StdSchedulerFactory` registers each scheduler in a shared process-wide repository keyed by instance name; the default instance name collided across the parallel scheduler-using test classes, causing intermittent cross-class interference.
- **Fix:** Set `quartz.scheduler.instanceName=test-{guid:N}` per scheduler across the 4 new + 2 pre-existing (HydrationTests, SchedulingTests) scheduler-using classes.
- **Files modified:** the scheduler-constructing test classes incl. HydrationTests.cs, SchedulingTests.cs
- **Verification:** orchestrator slice (31/0) + full suite (295/0) green and stable.
- **Committed in:** `376c9db`

---

**Total deviations:** 1 auto-fixed (1 Rule 1 bug).
**Impact on plan:** The isolation fix is a test-correctness requirement (deterministic parallel runs); no production code changed; no scope creep.

## Issues Encountered

None beyond the Rule 1 scheduler-isolation deviation documented above.

## User Setup Required

None - no external service configuration required.

## TDD Gate Compliance

This is an `execute`-type plan (tests-only, sampling already-built behavior), not a `tdd`-type plan. Task commits use `test(...)` because the artifacts are test files; there is no RED->GREEN feature cycle to gate. The behavior under test was built in Plans 03/04.

## Next Phase Readiness

- Phase 23 orchestrator lifecycle is fully proven from the user-observable signals named in 23-VALIDATION.md; the phase is ready for `/gsd-verify-work`.
- The cross-replica duplicate-dispatch coordination remains explicitly deferred (ORCH-SCALE-01 disposition); a single active replica is assumed at runtime.

## Self-Check: PASSED

All 6 created test files exist on disk; all 5 prior commits (478116b, 10459de, bca48d2, 376c9db, 043dbc5) present in git history.

---
*Phase: 23-orchestrator-stop-reload-lifecycle*
*Completed: 2026-05-31*
