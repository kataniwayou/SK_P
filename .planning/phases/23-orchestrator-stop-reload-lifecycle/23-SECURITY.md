---
phase: 23
slug: orchestrator-stop-reload-lifecycle
status: secured
threats_open: 0
threats_closed: 14
threats_total: 14
asvs_level: 1
block_on: critical
created: 2026-05-31
---

# SECURITY.md — Phase 23: orchestrator-stop-reload-lifecycle

**Audited:** 2026-05-31
**ASVS Level:** 1
**Block-on:** critical
**Threats Closed:** 14/14
**Threats Open:** 0/14

---

## Threat Verification

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-23-01 | Tampering — Reader StepProjection deserialization | mitigate | CLOSED | `src/Messaging.Contracts/Projections/StepProjection.cs:17` — `int EntryCondition` (not the enum). `tests/BaseApi.Tests/Orchestrator/StepProjectionReaderTests.cs:38` — asserts `EntryCondition == 1` on writer-produced JSON. |
| T-23-02 | Information Disclosure — EntryStepDispatch fields | accept | CLOSED (accepted risk — logged) | Pure POCO record confirmed at `src/Messaging.Contracts/EntryStepDispatch.cs`. No secrets. Disposition is `accept`; the acceptance is now recorded in the Accepted Risks Log below, which closes the threat per the secure-phase disposition rule. |
| T-23-03 | Tampering (supply chain) — Quartz NuGet pin | mitigate | CLOSED | `Directory.Packages.props:96-97` — `<PackageVersion Include="Quartz" Version="3.18.1" />` and `<PackageVersion Include="Quartz.Extensions.Hosting" Version="3.18.1" />`. No floating range. |
| T-23-04 | Spoofing/Elevation (key collision) — OrchestratorL2Keys forwarders | mitigate | CLOSED | `src/Orchestrator/Messaging/OrchestratorL2Keys.cs:16,18` — `ParentIndex()` and `Step()` delegate to `L2ProjectionKeys`; no inline format string present. |
| T-23-05 | Elevation/Spoofing — queue:{processorId} dispatch address | mitigate | CLOSED | `src/Orchestrator/Scheduling/WorkflowFireJob.cs:72` — `new Uri($"queue:{step.ProcessorId:D}")`. `processorId` is read from L1 which is hydrated read-only from L2 (`WorkflowLifecycle.cs` contains no StringSetAsync/SetAddAsync/KeyDeleteAsync). `:D` GUID format consistent. |
| T-23-06 | Denial of Service (resource exhaustion) — per-workflowId SemaphoreSlim stripe | mitigate | CLOSED | `src/Orchestrator/L1/WorkflowL1Store.cs:46` — `Wait(0)` (drop-if-held, never blocking). `Release` in `finally` at `StartOrchestrationConsumer.cs:54` and `StopOrchestrationConsumer.cs:50`. No bare `Wait()` without argument in store. Advisory warnings WR-01/WR-03 from 23-REVIEW.md touch Quartz reschedule races but do not negate the semaphore-stripe DoS mitigation — the stripe correctly prevents consumers from parking threads. |
| T-23-07 | Tampering (double-dispatch) — Quartz self-rescheduling one-shot | mitigate | CLOSED | `src/Orchestrator/Scheduling/WorkflowFireJob.cs:28` — `[DisallowConcurrentExecution]`. `WorkflowScheduler.cs:77` — `UnscheduleAsync` calls `scheduler.DeleteJob(KeyFor(jobId), ct)` (job+triggers atomic). `RescheduleAsync` adds trigger to existing job (Pitfall 4b). Advisory WR-01 flags a race between fire reschedule and concurrent DeleteJob but does not fully negate the declared mitigation: DisallowConcurrentExecution prevents double-fire from the same trigger; the race is a stop-during-fire edge case, not a double-dispatch path. See advisory notes below. |
| T-23-08 | Denial of Service (host crash via corrupt L2) — HydrationBackgroundService | mitigate | CLOSED | `src/Orchestrator/Hydration/HydrationBackgroundService.cs:46` — `Guid.TryParse` skips non-GUID members. `HydrationBackgroundService.cs:57` — per-workflow `catch when (WorkflowLifecycle.IsBusiness(ex))` skips corrupt entries. `Task.Delay` bounded backoff present at line 74. Test: `AckSemanticsTests.StartupCorruptEntry_HydratesRest_HostStaysUp` asserts `store.Count == 1` and no throw. |
| T-23-09 | Denial of Service (error-queue pollution) — gated consumers | mitigate | CLOSED | `StartOrchestrationConsumer.cs:32-36` — gate-closed is `return` (clean ack, no throw). `StopOrchestrationConsumer.cs:29-33` — same. Stripe-held drops are `continue` (not throw) at `StartOrchestrationConsumer.cs:44` and `StopOrchestrationConsumer.cs:40`. `AckSemanticsTests` asserts these paths do not throw. |
| T-23-10 | Tampering (premature readiness) — StartupCompletionService removal | mitigate | CLOSED | `src/Orchestrator/Program.cs:53-57` — removes by `ImplementationType == typeof(BaseConsole.Core.Health.StartupCompletionService)`. `HydrationBackgroundService.cs:64` — `gate.MarkReady()` called only at initial-hydration-complete. Redis-down backoff loop keeps gate closed (lines 68-82). |
| T-23-11 | Repudiation/Integrity (orchestrator L2 write) — both consumers + lifecycle | mitigate | CLOSED | `src/Orchestrator/Hydration/WorkflowLifecycle.cs` class doc states "never issues any string-set / set-add / key-delete mutation." `TeardownAsync` at lines 114-124 contains only `UnscheduleAsync` + `store.Remove`. No `StringSetAsync`, `SetAddAsync`, or `KeyDeleteAsync` in consumers or lifecycle (confirmed by read of all four files). |
| T-23-12 | Repudiation/Integrity (silent L2 write regression) — StopConsumerLifecycleTests + FireDispatchTests | mitigate | CLOSED | `tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs:55-58` — `DidNotReceive().StringSetAsync`, `DidNotReceive().SetAddAsync`, `DidNotReceive().KeyDeleteAsync`. `FireDispatchTests.cs:276-279` — same three guards (noted in 23-REVIEW.md IN-05 as structurally vacuous for the fire path specifically, but the Stop test guard is meaningful; the structural absence of a Redis dependency in WorkflowFireJob is itself evidence). |
| T-23-13 | Denial of Service (untested crash path) — AckSemanticsTests corrupt-entry case | mitigate | CLOSED | `tests/BaseApi.Tests/Orchestrator/AckSemanticsTests.cs:133-180` — `StartupCorruptEntry_HydratesRest_HostStaysUp` asserts `store.Count == 1` (good hydrated), `gate.IsReady == true`, and no exception escapes. |
| T-23-14 | Elevation (cross-replica process-uniqueness regression) — NoGlobalLockTests + manual review | mitigate | CLOSED | `tests/BaseApi.Tests/Orchestrator/NoGlobalLockTests.cs:37-70` — reflection over five types (WorkflowL1Store, WorkflowScheduler, StartOrchestrationConsumer, StopOrchestrationConsumer, HydrationBackgroundService) asserts no static SemaphoreSlim/Mutex/object field. `L1Store_StripeIsInstanceState_NotStatic` explicitly asserts the ConcurrentDictionary<Guid, SemaphoreSlim> is an INSTANCE field and that WorkflowL1Store has no static fields at all. Manual design review (Plan 05 Task 4 blocking checkpoint) documented in 23-05-SUMMARY.md. |

---

## Accepted Risks Log

| Threat ID | Category | Risk Statement | Acceptance Rationale | Accepted By |
|-----------|----------|---------------|---------------------|-------------|
| T-23-02 | Information Disclosure — EntryStepDispatch fields | EntryStepDispatch is a pure POCO record; fields (WorkflowId, StepId, ProcessorId, Payload) are workflow-author-supplied data trusted at L2 write time (WebApi owns L2). No credentials, PII, or secrets present. The message body travels on the internal RabbitMQ bus — the same trust zone as all other orchestrator messages. | No new disclosure surface beyond the existing bus trust model. No consumer-facing API. | Phase 23 design (23-01-PLAN.md T-23-02) |

---

## Advisory Notes (from 23-REVIEW.md — non-blocking)

The prior code review (23-REVIEW.md) raised four advisory warnings. Assessment against the threat register:

**WR-01 (Self-reschedule races teardown):** Overlaps T-23-07. The race between `WorkflowFireJob.RescheduleAsync` and a concurrent `TeardownAsync -> DeleteJob` can cause an unhandled `SchedulerException` when the fire job attempts to reschedule a job that was just deleted. This is a correctness edge case (the workflow silently stops rescheduling), not a double-dispatch. It does NOT fully negate T-23-07's declared mitigation: `[DisallowConcurrentExecution]` still prevents double-fire within a single trigger chain. T-23-07 is CLOSED; WR-01 is logged as an advisory implementation gap for a future fix (catch `SchedulerException` on reschedule as a clean no-op).

**WR-02 (Quartz SchedulerException misclassified by IsInfra/IsBusiness):** Overlaps T-23-08. A `SchedulerException` during startup hydration is caught by `IsBusiness` (which is the inverse of IsInfra, which only knows Redis exceptions), so a scheduler fault is silently skipped rather than retried. The gate can open reporting healthy while a workflow never scheduled. T-23-08 is CLOSED for the declared mitigation (corrupt JSON/non-GUID skipped, host stays up). WR-02 is an advisory accuracy gap in the business/infra classification, not a threat-negating gap.

**WR-03 (RescheduleAsync trigger accumulation):** Overlaps T-23-07. Auto-generated trigger keys could accumulate multiple live triggers on one job on misfire boundary. Does not negate `[DisallowConcurrentExecution]`; may cause drift from one-shot semantics over time. Advisory.

**WR-04 (Partial fan-out on broker blip):** Overlaps T-23-06 tangentially. At-least-once partial dispatch on broker flap is explicitly acknowledged as accepted deferred behavior in the design. A broker blip could stall the reschedule chain for a workflow. Advisory.

None of WR-01 through WR-04 fully negate the declared mitigations for T-23-06 or T-23-07. They are pre-classified as advisory/non-blocking in the constraints.

---

## Unregistered Threat Flags

No unregistered flags from SUMMARY.md — no `## Threat Flags` section was present in any of the five SUMMARY files for this phase.

---

_Audited: 2026-05-31_
_Auditor: gsd-security-auditor (claude-sonnet-4-6)_
_ASVS Level: 1_
