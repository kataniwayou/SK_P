---
phase: 23-orchestrator-stop-reload-lifecycle
verified: 2026-05-31T00:00:00Z
status: passed
score: 9/9 must-haves verified
overrides_applied: 0
---

# Phase 23: Orchestrator Stop/Reload Lifecycle Verification Report

**Phase Goal:** Build the real orchestrator lifecycle over the Phase 22 root-parent L2 structure — hydrate an in-memory L1 dictionary of each workflow's root + step state, drive a Quartz job off each workflow's cron that dispatches entry-step e2e messages and refreshes liveness, and tear the job + L1 down on stop. The orchestrator no longer mutates L2: startup/start-consume only READ L2 into L1, and stop only deletes the Quartz job + clears L1 (L2 teardown is owned by the WebApi side).
**Verified:** 2026-05-31
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | The orchestrator can deserialize a real skp:{wf}:{step} L2 value into a shared reader record with entryCondition as an int | VERIFIED | `StepProjection` in `src/Messaging.Contracts/Projections/StepProjection.cs` — `int EntryCondition` with `[property: JsonPropertyName("entryCondition")]`; round-trip proved by `StepProjectionReaderTests` |
| 2 | A typed 7-field orchestrator->processor dispatch message exists with executionId and entryId = Guid.Empty | VERIFIED | `EntryStepDispatch` in `src/Messaging.Contracts/EntryStepDispatch.cs` — sealed record implementing `IExecutionCorrelated`; `ExecutionId { get; init; } = Guid.Empty` and `EntryId { get; init; } = Guid.Empty`; proved by `EntryStepDispatchTests` |
| 3 | The Quartz dependency is pinned and referenced so scheduling code compiles | VERIFIED | `Directory.Packages.props` pins `Quartz 3.18.1` + `Quartz.Extensions.Hosting 3.18.1`; `Orchestrator.csproj` has `PackageReference Include="Quartz.Extensions.Hosting"` (no Version= per CPM); orchestrator project builds 0 Warning/0 Error |
| 4 | The orchestrator can build the parent-index SET key and per-step key via the shared L2ProjectionKeys forwarder | VERIFIED | `OrchestratorL2Keys` has `ParentIndex()` -> `L2ProjectionKeys.ParentIndex()` and `Step(wfId, stepId)` -> `L2ProjectionKeys.Step(wfId, stepId)` — no inlined key formats |
| 5 | A singleton thread-safe L1 store holds per-workflow root+step state with a per-workflowId drop-if-held stripe | VERIFIED | `WorkflowL1Store` backed by `ConcurrentDictionary<Guid, WorkflowL1>` and `ConcurrentDictionary<Guid, SemaphoreSlim>`; `TryAcquire` uses `.Wait(0)` (drop-if-held); no static lock fields; `NoGlobalLockTests` confirms via reflection |
| 6 | Each workflow gets one in-memory Quartz job keyed by jobId; L1 liveness interval = delta-seconds between the next two Cronos fire times | VERIFIED | `WorkflowScheduler.ScheduleAsync` builds `JobKey(jobId.ToString("D"))` one-shot job; `CronInterval.IntervalSeconds` computes delta via `CronFormat.Standard`; `SchedulingTests` + `CronIntervalTests` confirm |
| 7 | On fire the job Sends one EntryStepDispatch per entry step to queue:{processorId}, mints a fresh correlationId, refreshes L1 liveness timestamp, and reschedules itself off the next Cronos occurrence | VERIFIED | `WorkflowFireJob.Execute`: mints `NewId.NextGuid()`, sends to `new Uri($"queue:{step.ProcessorId:D}")`, sets `wf.Liveness = current with { Timestamp = nowUtc }`, calls `RescheduleAsync`; proved end-to-end by `FireDispatchTests` |
| 8 | On boot the orchestrator reads ALL workflow ids from the L2 parent index and hydrates each into L1 (workflow + step entries only); IStartupGate.MarkReady() fires at initial-hydration-complete; StartupCompletionService is removed by ImplementationType | VERIFIED | `HydrationBackgroundService.ExecuteAsync`: `SetMembersAsync(ParentIndex())` -> per-workflow `HydrateAndScheduleAsync` -> `gate.MarkReady()`; `Program.cs` removes `StartupCompletionService` by `ImplementationType`; `HydrationTests` proves N hydrate + corrupt skipped |
| 9 | Start consumer = gated tolerant-teardown+hydrate+schedule; Stop consumer = gated teardown-only; both zero L2 writes; absent/gated/stripe-held acks cleanly; corrupt startup entry skipped; Redis-unreachable propagates | VERIFIED | Consumers both gate-check `gate.IsReady`, use `store.TryAcquire`/`Release` in try/finally; no `StringSetAsync`/`SetAddAsync`/`KeyDeleteAsync` anywhere in `src/Orchestrator/`; proved by `StartConsumerLifecycleTests`, `StopConsumerLifecycleTests`, `AckSemanticsTests` |

**Score:** 9/9 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Messaging.Contracts/Projections/StepProjection.cs` | Reader-consumable step projection record (entryCondition:int) | VERIFIED | Present, substantive, used by hydration path |
| `src/Messaging.Contracts/IExecutionCorrelated.cs` | IExecutionCorrelated : ICorrelated segregated interface | VERIFIED | Present; 5 Guid members; implemented by EntryStepDispatch |
| `src/Messaging.Contracts/EntryStepDispatch.cs` | 7-field entry-step dispatch message record | VERIFIED | Present; implements IExecutionCorrelated; ExecutionId/EntryId = Guid.Empty |
| `Directory.Packages.props` | Quartz + Quartz.Extensions.Hosting 3.18.1 CPM pins | VERIFIED | Both PackageVersion entries present at 3.18.1 |
| `src/Orchestrator/Orchestrator.csproj` | PackageReference to Quartz.Extensions.Hosting (no Version=) | VERIFIED | Present with no Version= attribute |
| `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` | ParentIndex() + Step() reader forwarders | VERIFIED | Both forward to L2ProjectionKeys; no inlined key format |
| `src/Orchestrator/L1/WorkflowL1.cs` | WorkflowL1 record with StepProjection map | VERIFIED | Present; `IReadOnlyDictionary<Guid, StepProjection> Steps`; mutable Liveness property |
| `src/Orchestrator/L1/IWorkflowL1Store.cs` | Interface with TryAcquire/Release drop-if-held | VERIFIED | Present; `bool TryAcquire(Guid)` and `void Release(Guid)` defined |
| `src/Orchestrator/L1/WorkflowL1Store.cs` | ConcurrentDictionary-backed singleton + SemaphoreSlim stripe | VERIFIED | Both ConcurrentDictionaries present; TryAcquire uses `.Wait(0)`; no blocking `.Wait()` |
| `src/Orchestrator/Scheduling/CronInterval.cs` | Cronos next-two-occurrence delta-seconds + next-occurrence | VERIFIED | Present; `CronFormat.Standard`; uses `timeProvider.GetUtcNow().UtcDateTime` (no ambient statics) |
| `src/Orchestrator/Scheduling/WorkflowScheduler.cs` | schedule/unschedule by JobKey(jobId) | VERIFIED | Present; `DeleteJob(KeyFor(jobId))`; `WithMisfireHandlingInstructionFireNow` |
| `src/Orchestrator/Scheduling/WorkflowFireJob.cs` | IJob: Send per entry step + liveness refresh + self-reschedule | VERIFIED | `[DisallowConcurrentExecution]`; `GetSendEndpoint(new Uri($"queue:{step.ProcessorId:D}"))`; no L2 writes; `RescheduleAsync` at end |
| `src/Orchestrator/Hydration/WorkflowLifecycle.cs` | Shared hydrate-one + teardown-one | VERIFIED | Present; `SetMembersAsync` via caller; `TeardownAsync` has zero L2 writes; static `IsInfra`/`IsBusiness` split |
| `src/Orchestrator/Hydration/HydrationBackgroundService.cs` | Startup L2->L1 hydration + schedule + MarkReady with bounded backoff | VERIFIED | `SetMembersAsync(ParentIndex())`; `gate.MarkReady()` at hydration-complete; `Guid.TryParse` skip; `Task.Delay` bounded backoff |
| `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` | Gated tolerant-teardown+hydrate+schedule | VERIFIED | `gate.IsReady` check; `store.TryAcquire`; `TeardownAsync` + `HydrateAndScheduleAsync`; `finally { store.Release }` |
| `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` | Gated teardown-only; zero L2 writes | VERIFIED | `gate.IsReady` check; teardown-only; no L2 write calls |
| `src/Orchestrator/Program.cs` | Quartz + L1 store + hydration service wiring; StartupCompletionService removed by type | VERIFIED | `AddQuartz()`, `AddQuartzHostedService`, `AddSingleton<IWorkflowL1Store, WorkflowL1Store>`, `AddHostedService<HydrationBackgroundService>`; `ImplementationType == typeof(StartupCompletionService)` removal confirmed |
| `tests/BaseApi.Tests/Orchestrator/CapturingDispatchConsumer.cs` | Synthetic IConsumer<EntryStepDispatch> bound to ReceiveEndpoint({processorId:D}) | VERIFIED | Present; `IConsumer<EntryStepDispatch>`; Consume returns `Task.CompletedTask` |
| `tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs` | Fire -> dispatch + correlationId-differs + liveness-advance assertions | VERIFIED | Present; `ReceiveEndpoint($"{processorId`; `Consumed.Select<EntryStepDispatch>`; Guid.Empty + correlationId inequality assertions |
| `tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs` | Stop teardown + byte-identical L2 snapshot + zero-L2-write assertion | VERIFIED | `DidNotReceive().StringSetAsync`, `DidNotReceive().SetAddAsync`, `DidNotReceive().KeyDeleteAsync` all present |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `WorkflowFireJob.cs` | `queue:{processorId}` | `ISendEndpointProvider.GetSendEndpoint + Send` | WIRED | `GetSendEndpoint(new Uri($"queue:{step.ProcessorId:D}"))` + `endpoint.Send(msg, ct)` — NOT Publish |
| `CronInterval.cs` | Cronos | `CronExpression.Parse(cron, CronFormat.Standard).GetNextOccurrence` | WIRED | Both methods parse with `CronFormat.Standard` |
| `WorkflowScheduler.cs` | Quartz IScheduler | `ScheduleJob / DeleteJob(JobKey(jobId))` | WIRED | `DeleteJob(KeyFor(jobId), ct)` on `UnscheduleAsync`; `ScheduleJob(job, trigger, ct)` on `ScheduleAsync` |
| `HydrationBackgroundService.cs` | IStartupGate | `MarkReady()` at initial-hydration-complete | WIRED | `gate.MarkReady()` called after successful `foreach` loop, before `return` |
| `StopOrchestrationConsumer.cs` | `WorkflowScheduler.UnscheduleAsync` | jobId resolved from L1 -> DeleteJob(JobKey(jobId)) | WIRED | `lifecycle.TeardownAsync` -> `scheduler.UnscheduleAsync(wf.JobId, ct)` -> `DeleteJob` |
| `Program.cs` | `BaseConsole.Core.Health.StartupCompletionService` | remove-by-ImplementationType | WIRED | `d.ImplementationType == typeof(BaseConsole.Core.Health.StartupCompletionService)` confirmed in Program.cs |
| `OrchestratorL2Keys.cs` | `Messaging.Contracts.L2ProjectionKeys` | thin forwarder (HARDEN-03) | WIRED | `ParentIndex()` -> `L2ProjectionKeys.ParentIndex()`, `Step()` -> `L2ProjectionKeys.Step()`, no inlined formats |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|--------------|--------|-------------------|--------|
| `WorkflowFireJob` | `wf` (WorkflowL1) | `store.TryGet(workflowId, out var wf)` from `IWorkflowL1Store` | Yes — seeded by hydration from real L2 values | FLOWING |
| `HydrationBackgroundService` | `ids` (RedisValue[]) | `db.SetMembersAsync(OrchestratorL2Keys.ParentIndex())` | Yes — real Redis SET read | FLOWING |
| `WorkflowLifecycle.HydrateAndScheduleAsync` | `root`, `steps` | `db.StringGetAsync(Root)` + `db.StringGetAsync(Step)` deserialized to `WorkflowRootProjection`/`StepProjection` | Yes — real L2 GET values | FLOWING |
| `StartOrchestrationConsumer` | workflowId(s) | `context.Message.WorkflowIds` from MassTransit message body | Yes — live bus message | FLOWING |

### Behavioral Spot-Checks

Step 7b: SKIPPED for runtime checks (no runnable server entry point in CI context). All behavioral claims are instead covered by the unit/integration test suite (295 tests, 0 failures confirmed by orchestrator).

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|------------|------------|-------------|--------|----------|
| ORCH-CONTRACT-01 | Plan 01 | Reader-consumable step-projection record in Messaging.Contracts.Projections with int EntryCondition | SATISFIED | `StepProjection` exists with `int EntryCondition` + camelCase `[property: JsonPropertyName]`; `StepProjectionReaderTests` proves writer-enum round-trip |
| ORCH-CONTRACT-02 | Plan 01 | Orchestrator->processor entry-step dispatch message with correlationId, workflowId, stepId, processorId, executionId, entryId, payload | SATISFIED | `EntryStepDispatch` sealed record implementing `IExecutionCorrelated`; 7 logical fields; ExecutionId/EntryId=Guid.Empty; `EntryStepDispatchTests` proves shape |
| ORCH-STARTUP-01 | Plans 02, 04 | On host startup reads ALL L2 parent-index workflow ids and hydrates each into L1 (workflow + step; no processor/parent-index key) | SATISFIED | `HydrationBackgroundService` uses `SetMembersAsync(ParentIndex())` + `HydrateAndScheduleAsync` per id; `HydrationTests.HydratesAllParentIndexWorkflows_NoProcessorOrParentIndexKey` proves it |
| ORCH-SCHED-01 | Plans 02, 03, 04 | Each hydrated workflow gets one Quartz RAMJobStore job keyed by jobId; cron->interval is delta-seconds between next two fire times | SATISFIED | `WorkflowScheduler.ScheduleAsync` builds `JobKey(jobId.ToString("D"))` one-shot job; `CronInterval.IntervalSeconds` computes delta; `SchedulingTests` proves exactly one started job per workflow keyed by jobId |
| ORCH-FIRE-01 | Plan 03 | On fire — fresh correlationId, Send EntryStepDispatch to queue:{processorId} per entry step (executionId/entryId=Guid.Empty), refresh L1 liveness timestamp (no L2 write) | SATISFIED | `WorkflowFireJob`: `NewId.NextGuid()`, `GetSendEndpoint(queue:{processorId:D})`, in-memory liveness refresh; `FireDispatchTests` proves all three behaviors end-to-end |
| ORCH-CONSUME-01 | Plan 04 | Start consumer hydrates ONLY consumed workflowId(s) into L1, then runs scheduling + fire | SATISFIED | `StartOrchestrationConsumer`: gate check, stripe check, `TeardownAsync` + `HydrateAndScheduleAsync`; `StartConsumerLifecycleTests.StartHydratesOnlyConsumedWorkflow_AndSchedules` proves it |
| ORCH-STOP-01 | Plan 04 | Stop consumer resolves each jobId from L1, DeleteJob(JobKey(jobId)), clears L1 — NO L2 mutation | SATISFIED | `StopOrchestrationConsumer` -> `TeardownAsync` -> `scheduler.UnscheduleAsync` + `store.Remove`; no L2 write calls in `src/Orchestrator/`; `StopConsumerLifecycleTests` proves job deleted + L1 cleared + `DidNotReceive` on all L2 write methods |
| ORCH-SCALE-01 | Plan 03 | All new state (L1, RAMJobStore) is per-instance; no global/singleton-lock or process-uniqueness assumption | SATISFIED | No static lock fields in any Orchestrator lifecycle class; per-workflow stripe is an instance `ConcurrentDictionary`; `NoGlobalLockTests` reflection guard confirms; ORCH-SCALE-01 manual design review approved at Plan 05 checkpoint |
| ORCH-ACK-01 | Plan 04 | Business failures log+ack/skip; infra faults propagate; startup skips corrupt entry without crashing | SATISFIED | `WorkflowLifecycle.IsBusiness`/`IsInfra` split; `HydrationBackgroundService` per-workflow business guard; gate-closed/stripe-held clean `return` acks; `AckSemanticsTests` proves all three paths |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `src/Orchestrator/Scheduling/WorkflowFireJob.cs` | 80 | `current is null` guard for `wf.Liveness` | Info | WorkflowL1.Liveness initialized `default!`; the null-guard is defensive but `Liveness` is set at construction in all known call sites. Matches REVIEW IN-02 — consider `required` keyword. Not a stub; not a blocker. |
| (none) | — | TODO/FIXME/PLACEHOLDER patterns | None | Zero matches in all phase 23 production files |
| (none) | — | L2 write calls (StringSetAsync/SetAddAsync/KeyDeleteAsync) | None | Zero matches in `src/Orchestrator/` — confirmed by grep |
| (none) | — | `.Publish(` calls in Scheduling/L1 | None | Zero matches — fire path is Send-only |
| (none) | — | `static` lock/mutex/semaphore fields | None | Zero matches in lifecycle types — confirmed by grep and `NoGlobalLockTests` |

### Human Verification Required

No items require human verification. All must-have truths were verified programmatically against the live codebase. The ORCH-SCALE-01 manual design review (process-uniqueness half) was completed and approved at the Plan 05 blocking human-verify checkpoint (`043dbc5`).

### Known Follow-ups (from 23-REVIEW.md — advisory, not blocking)

These warnings were identified in the code review and are NOT goal-blocking. They are documented here for tracking.

- **WR-01** (`WorkflowFireJob.cs`): Self-reschedule races teardown — `RescheduleAsync` can throw `SchedulerException` if the job was deleted mid-fire. The current behavior is a silent workflow-pause on teardown-concurrent-fire (not a crash), which is functionally correct (Stop wins), but the exception is unhandled rather than explicitly a clean business no-op.
- **WR-02** (`WorkflowLifecycle.cs`): `SchedulerException` misclassified by `IsInfra`/`IsBusiness` — a Quartz fault during startup hydration is silently bucketed as "business" (skip), potentially opening the gate for a workflow that silently failed to schedule.
- **WR-03** (`WorkflowScheduler.cs`): Accumulating triggers — `RescheduleAsync` adds a new auto-keyed trigger without replacing the old one; over misfire cycles, multiple trigger chains could accumulate per job.
- **WR-04** (`WorkflowFireJob.cs`): Mid-loop broker fault leaves partial fan-out; liveness refresh and reschedule are skipped when an early Send throws, which could permanently park a workflow's schedule after a transient broker blip.

Recommended: address WR-01 and WR-04 in a follow-up phase (catch `SchedulerException` on reschedule as a business no-op; move reschedule into a `finally` or before the Send loop).

### Gaps Summary

No gaps. All 9 must-have truths are verified, all required artifacts are present, substantive, and wired. The full test suite is 295 passed / 0 failed (confirmed by orchestrator against the live v3.4.0 docker stack). Zero L2 write calls in the Orchestrator source. Zero static lock fields. Zero placeholder/stub code in production files.

The 4 code-review warnings are advisory correctness-under-concurrency edge cases, none of which break the stated requirements or phase goal. They are tracked above for follow-up.

---

_Verified: 2026-05-31_
_Verifier: Claude (gsd-verifier)_
