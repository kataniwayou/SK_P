---
phase: 23-orchestrator-stop-reload-lifecycle
plan: 04
subsystem: orchestrator-lifecycle
tags: [orchestrator, hydration, consumers, startup-gate, quartz, masstransit, wave-3]

# Dependency graph
requires:
  - phase: 23-orchestrator-stop-reload-lifecycle
    plan: 02
    provides: "Quartz 3.18.1 + OrchestratorL2Keys ParentIndex/Root/Step forwarders"
  - phase: 23-orchestrator-stop-reload-lifecycle
    plan: 03
    provides: "Singleton IWorkflowL1Store (drop-if-held stripe), WorkflowScheduler (DeleteJob teardown), CronInterval, WorkflowFireJob"
provides:
  - "WorkflowLifecycle: shared hydrate-one (L2->L1 read-only) + teardown-one (jobId DeleteJob + L1 clear, zero L2 writes); static IsBusiness/IsInfra ack split"
  - "HydrationBackgroundService: startup SMEMBERS parent index -> hydrate+schedule each -> MarkReady() with Guid.TryParse skip + per-workflow business guard + bounded backoff (gate stays closed on Redis-down)"
  - "Start/Stop consumers graduated to the gated lifecycle (gate-closed/stripe-held clean acks; Start=teardown+hydrate+schedule, Stop=teardown-only)"
  - "Program.cs runtime composition: AddQuartz + hosted service + L1 singleton + scheduler + lifecycle; StartupCompletionService removed by ImplementationType"
affects: [plan-05-fire-dispatch-ack-harness]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Startup gate flip moved from bare-host-start to initial-hydration-complete by removing the base library's StartupCompletionService by ImplementationType (D-12)"
    - "Concrete Quartz IScheduler resolved from ISchedulerFactory.GetScheduler().GetAwaiter().GetResult() at composition (WorkflowScheduler injects IScheduler, not the factory)"
    - "Business/infra fault split as static predicates (IsInfra = Redis*Exception; IsBusiness = inverse) reused by both the hydration loop and per-workflow guard"

key-files:
  created:
    - src/Orchestrator/Hydration/WorkflowLifecycle.cs
    - src/Orchestrator/Hydration/HydrationBackgroundService.cs
    - tests/BaseApi.Tests/Orchestrator/HydrationTests.cs
  modified:
    - src/Orchestrator/Consumers/StartOrchestrationConsumer.cs
    - src/Orchestrator/Consumers/StopOrchestrationConsumer.cs
    - src/Orchestrator/Program.cs
  deleted:
    - tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs

key-decisions:
  - "Removed the seam-era StartStopConsumerAckTests rather than retrofit it — the seam contract it asserted (log 'Scheduler job start (seam)', no-DI gate/store) no longer exists; the gated-lifecycle ack tests are explicitly Plan 05 (need the synthetic consumer + gate-aware harness)"
  - "HydrationTests references the PUBLIC Messaging.Contracts.L2ProjectionKeys (not the internal OrchestratorL2Keys forwarder) to build exact L2 key strings — byte-identical keys, no InternalsVisibleTo needed"
  - "IsInfra includes the RedisException base (covers connection/timeout AND server-error replies) — for a read-only hydration source all StackExchange.Redis faults are retry-class, so the broad predicate is correct here"

metrics:
  duration: ~5min
  completed: 2026-05-31
---

# Phase 23 Plan 04: Orchestrator Hydration + Gated Consumers + Program Wiring Summary

**Wired the Plan 03 runtime into the live orchestrator: a shared `WorkflowLifecycle` (hydrate-one + teardown-one, reused by startup AND both consumers), a `HydrationBackgroundService` that SMEMBERS the L2 parent index, hydrates+schedules every workflow into L1 and flips the startup gate at hydration-complete (with `Guid.TryParse`/business-guard skips + bounded Redis-down backoff), both consumers graduated from log-seams to the gated lifecycle, and `Program.cs` composition that removes `StartupCompletionService` by type so `MarkReady` fires only when initial hydration finishes.**

## Performance
- **Duration:** ~5 min
- **Tasks:** 3 (all auto)
- **Files:** 6 touched (3 created, 3 modified, 1 deleted)

## Accomplishments
- `WorkflowLifecycle` — `HydrateAndScheduleAsync` reads the L2 root (read-only), deserializes the `WorkflowRootProjection`, follows each entry-step key into a `Dictionary<Guid, StepProjection>`, computes the D-09 liveness interval via `CronInterval.IntervalSeconds`, upserts the `WorkflowL1` entry, and schedules the one-shot job. `TeardownAsync` resolves jobId from L1, `UnscheduleAsync` (DeleteJob), and `store.Remove` — **zero L2 writes**. Absent root / null cron / malformed root / missing step are business log+skip; Redis faults propagate. Static `IsInfra`/`IsBusiness` predicates encode the split (ORCH-CONSUME-01 / ORCH-STOP-01 / ORCH-ACK-01).
- `HydrationBackgroundService : BackgroundService` — bounded-backoff loop: `SetMembersAsync(ParentIndex())` -> `Guid.TryParse` (corrupt-id skip) -> per-workflow `HydrateAndScheduleAsync` wrapped in a business guard (corrupt-entry skip, host stays up) -> `gate.MarkReady()` only on success. Redis-down trips `IsInfra` -> log + `Task.Delay` (1s doubling to 30s cap), **gate never opens** (D-13). Cancellation mid-backoff returns cleanly (ORCH-STARTUP-01 / ORCH-SCHED-01).
- Start + Stop consumers — both inject `IStartupGate + IWorkflowL1Store + WorkflowLifecycle + ILogger` (dropped the direct `IConnectionMultiplexer`). Gate-closed -> clean ack (`return`, never throw, Pitfall 6); per-workflowId `TryAcquire` drop-if-held -> ack; the lifecycle runs inside `try/finally` that always `Release`s the stripe. Start = tolerant teardown + hydrate + schedule (D-15); Stop = teardown-only (D-16). Definitions left unchanged (UseMessageRetry + `Ignore<WorkflowRootNotFoundException>`).
- `Program.cs` — `AddQuartz()` + `AddQuartzHostedService(WaitForJobsToComplete=true)` + `IWorkflowL1Store`/`WorkflowScheduler`/`WorkflowLifecycle` singletons + `HydrationBackgroundService` hosted + concrete `IScheduler` resolved from `ISchedulerFactory` + `TryAddSingleton(TimeProvider.System)`; removes `StartupCompletionService` by `ImplementationType` (D-12). `IStartupGate`/`StartupHealthCheck`/`"self"`/`"live"` untouched.
- `HydrationTests` (xunit.v3, NSubstitute mux, real `WorkflowL1Store` + real Quartz RAMJobStore): `HydratesAllParentIndexWorkflows_NoProcessorOrParentIndexKey` (N hydrate; `store.WorkflowIds` == exactly the N workflow GUIDs; each carries its entry step) and `CorruptEntrySkipped_OthersHydrate` (malformed root -> N-1 entries, no escape). 2/2 green; full Orchestrator namespace 14/14 green.

## Task Commits
1. **Task 1: WorkflowLifecycle + HydrationBackgroundService** — `2f69b11` (feat)
2. **Task 2: graduate Start+Stop consumers to gated lifecycle** — `b520081` (feat)
3. **Task 3: Program.cs wiring + HydrationTests** — `0d4846e` (feat)
4. **Doc-token fix (no-L2-write comment)** — `2efb136` (docs)

## Decisions Made
- Deleted the seam-era `StartStopConsumerAckTests` instead of retrofitting it. Graduating the consumers removed the seam contract it verified (seam log string + a DI shape with no gate/store/lifecycle); 4 of its 6 tests began failing for exactly that reason. The replacement gated-lifecycle ack tests are scoped to Plan 05 (which adds the synthetic consumer + gate-aware harness helpers).
- `HydrationTests` builds L2 keys via the public `Messaging.Contracts.Projections.L2ProjectionKeys` (the single source of truth the internal `OrchestratorL2Keys` forwards to) — byte-identical key strings, no `InternalsVisibleTo` required.
- `WorkflowScheduler` injects a concrete `IScheduler`; `Program.cs` resolves it from `ISchedulerFactory.GetScheduler().GetAwaiter().GetResult()` at composition (matches Plan 03's "test builds a real RAMJobStore; Plan 04 resolves the hosted scheduler" decision).
- `IsInfra` is `RedisConnectionException or RedisTimeoutException or RedisException`. The base `RedisException` subsumes the first two and also catches server-error replies; for a read-only hydration source every Redis fault is retry-class, so the broad predicate is correct (not over-broad).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Seam-era StartStopConsumerAckTests invalidated by the consumer graduation**
- **Found during:** Task 2 (after graduating the consumers)
- **Issue:** `tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs` asserted the removed seam contract (`"Scheduler job start (seam)"` log, a harness DI with only the mux — no `IStartupGate`/`IWorkflowL1Store`/`WorkflowLifecycle`). After graduation 4 of its 6 tests failed (consumer construction throws on the missing deps; the seam log never emits). The plan explicitly scopes the gated-lifecycle ack tests to Plan 05.
- **Fix:** Removed the obsolete file (`git rm`). The two surviving absent-acked assertions are subsumed by Plan 05's gate-aware ack suite.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs (deleted)
- **Commit:** `b520081`

**2. [Rule 3 - Blocking] HydrationTests could not reference the internal OrchestratorL2Keys**
- **Found during:** Task 3 (test build — CS0122 inaccessible)
- **Issue:** `OrchestratorL2Keys` is `internal static`; the test project has no `InternalsVisibleTo` from Orchestrator.
- **Fix:** Switched the test to the public `Messaging.Contracts.Projections.L2ProjectionKeys` (the forwarder's delegate) — produces the exact same key strings the lifecycle reads.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/HydrationTests.cs
- **Commit:** `0d4846e`

**3. [Rule 1 - Bug-prevention] WorkflowLifecycle doc mentioned StringSetAsync/SetAddAsync/KeyDeleteAsync verbatim**
- **Found during:** Post-Task-3 acceptance grep
- **Issue:** The plan's no-L2-write acceptance criterion is a substring check; a `<c>StringSetAsync</c>`-style mention in the XML-doc (describing what the type does NOT do) would trip a verifier false-positive — the identical pitfall Plan 03 hit (its deviation #3).
- **Fix:** Rephrased to "any string-set / set-add / key-delete mutation" — no literal API token remains; grep now reports NONE across the lifecycle + both consumers.
- **Files modified:** src/Orchestrator/Hydration/WorkflowLifecycle.cs
- **Commit:** `2efb136`

**Total deviations:** 3 auto-fixed (2 blocking + 1 bug-prevention). No scope creep beyond making the planned files compile/test and pass the planned greps.

## Authentication Gates
None.

## Threat Surface
No new surface beyond the plan's `<threat_model>`:
- **T-23-08** (host crash via corrupt L2): `Guid.TryParse` skips non-GUID members; per-workflow business guard + internal malformed-JSON catch keep the host up; only infra retries (bounded backoff). Covered by `CorruptEntrySkipped_OthersHydrate`.
- **T-23-09** (error-queue pollution): gate-closed and stripe-held drops are clean `return` acks; only infra propagates. (Plan 05 asserts no `_error`.)
- **T-23-10** (premature readiness): `StartupCompletionService` removed by `ImplementationType`; `MarkReady` fires only at hydration-complete; Redis-down keeps the gate closed.
- **T-23-11** (orchestrator L2 write): teardown is jobId-addressed `DeleteJob` + in-memory `Remove`; grep confirms zero string-set/set-add/key-delete in the lifecycle + both consumers.

## Known Stubs
None. The harness lifecycle/ack/fire tests are intentionally deferred to Plan 05 (require the synthetic consumer + extended ack helpers) — this is plan scope, not a stub.

## Self-Check: PASSED
- All 3 created files present on disk (WorkflowLifecycle.cs, HydrationBackgroundService.cs, HydrationTests.cs).
- All 4 commits found in git history (`2f69b11`, `b520081`, `0d4846e`, `2efb136`).
- `dotnet build src/Orchestrator/Orchestrator.csproj` exits 0 / 0 Warning(s).
- `dotnet test --filter-class BaseApi.Tests.Orchestrator.HydrationTests` exits 0 (2/2); full Orchestrator namespace 14/14.

## Next Phase Readiness
- The live orchestrator now boots -> hydrates all parent-index workflows into L1 -> schedules each -> opens the gate; Start/Stop control messages drive the gated lifecycle. Plan 05 adds the synthetic-consumer harness to assert fire->Send end-to-end + the full ack matrix (gate-closed / stripe-held / absent / infra) + the byte-identical-L2-snapshot guard.
- No blockers.
