# Phase 24: Orchestrator Result-Consume & Step Advancement — Research

**Researched:** 2026-05-31
**Domain:** .NET 8 / MassTransit 8.5.5 (RabbitMQ) / Quartz 3.18.1 / StackExchange.Redis 2.13.1 — backend messaging
**Confidence:** HIGH (codebase shapes VERIFIED by read; MassTransit redelivery API CITED from official docs + cross-verified)

## Summary

This phase adds a result-consume → L1-lookup → entry-condition-match → continuation-dispatch path to the orchestrator, plus a lifecycle gating redesign. Almost everything the planner needs is already in the repo and was read directly: the `EntryStepDispatch` dispatch block to extract (`WorkflowFireJob` lines 66–73), the L1 read surface (`IWorkflowL1Store.TryGet` + `WorkflowL1.Steps`), the gated Start/Stop consumers to convert to conditionless, the `WorkflowLifecycle` hydrate/teardown unit, the contracts (`StepProjection`, `EntryStepDispatch`, `IExecutionCorrelated`), and the Phase 23 test harness (`AddMassTransitTestHarness` in-memory bus + `OrchestratorTestStubs` Redis-mux stubs + `CapturingDispatchConsumer`).

The one genuine gray area — **gate-closed never-drop via delayed redelivery (D-06 / ORCH-GATE-01)** — resolves to a concrete, verified constraint: MassTransit's `UseDelayedRedelivery` requires the **RabbitMQ delayed-message-exchange plugin**, which is **NOT installed** in this repo's broker (`rabbitmq:4.1.8-management-alpine`, no plugin enablement anywhere). `UseScheduledRedelivery` is the plugin-free alternative but needs a registered message scheduler. The cleanest plugin-free fit is MassTransit's **in-memory message scheduler** (`AddInMemoryMessageScheduler` + `UseInMemoryScheduler`) with `UseScheduledRedelivery`, which is in-process, lossy-on-restart, and perfectly adequate for the short single-replica hydration window this phase targets.

**Primary recommendation:** Convert the Start/Stop consumers to conditionless gate-open logic; on a closed gate, **throw** (do not ack-return) so a second-level redelivery policy reschedules the message past hydration. Wire delayed redelivery with the **in-memory scheduler** (`UseScheduledRedelivery` + `UseInMemoryScheduler`, no broker plugin) — escalating intervals like `5s, 15s, 30s, 60s` that comfortably outlast a typical hydration window, after which a true Redis-down outage exhausts to `_error`. The redelivery + retry split, the L1-only `StepAdvancement` helper, the `IStepDispatcher` extraction, and the WebApi first-win change are all otherwise unambiguous given the locked decisions.

## User Constraints (from CONTEXT.md)

### Locked Decisions (D-01..D-08)
- **D-01 (IStepDispatcher):** Extract a shared `IStepDispatcher` (build `EntryStepDispatch` → `GetSendEndpoint(new Uri($"queue:{processorId:D}"))` → `Send`). Both `WorkflowFireJob` and the new result consumer use it. Refactor `WorkflowFireJob` lines ~66–73 to call it.
- **D-02 (StepAdvancement):** Outcome→entry-condition match + `NextStepIds` traversal in a small unit-testable `StepAdvancement` helper (pure function: incoming `StepOutcome` + completed step projection + L1 step map → next steps to dispatch). Match is **int equality** on `StepProjection.EntryCondition`: select iff `EntryCondition == (int)outcome || EntryCondition == Always`. `Always = 4` / `Never = 5` are named int constants ON the orchestrator side (orchestrator does NOT reference `BaseApi.Service.StepEntryCondition`). `StepOutcome` ints (0–3) are the source of truth for the `Previous*` subset.
- **D-03 (result queue):** Shared result queue named `queue:orchestrator-result`; bound as a shared competing-consumer `ReceiveEndpoint` (NOT instance-unique fan-out). Name = a single named constant alongside the contracts/keys (one source of truth for processor + orchestrator).
- **D-04 (WebApi = single dedup point):** Duplicate-suppression lives in the WebApi as first-win on the L2 root — Start creates the root-parent `workflowId` only if absent (else skip), Stop deletes only if present (else skip). Orchestrator needs NO idempotency logic.
- **D-05 (conditionless consumers):** Gate-open Start = unconditionally hydrate L2→L1 + (re)schedule (no existence skip, no stripe gating on existence). Gate-open Stop = unconditionally delete the Quartz job(s). Drop Phase 23 teardown+skip and stripe-held-skip.
- **D-06 (gate-closed never-drop):** While `!gate.IsReady`, Start/Stop/result consumers throw/nack to trigger **delayed redelivery** (NOT clean-ack-drop). Requires a delayed/second-level redelivery policy sized to outlast hydration (existing fast `Immediate(3)` alone exhausts before `MarkReady`). A true Redis-down outage eventually routes to `_error`.
- **D-07 (stop keeps L1 — drain):** Stop deletes jobs but KEEPS the L1 entry, so late `ExecutionResult` for a stopped workflow still resolves in L1 and dispatches. No L1 clear this phase.
- **D-08 (no stripe on result path):** Result consumer only READS L1 (lock-free `ConcurrentDictionary` via `TryGet`) and never mutates it → no per-workflow stripe. A `TryGet` miss (gate open) = graceful business-ack.

### Claude's Discretion
- Exact delayed-redelivery numbers (attempt count / spacing) — choose values comfortably outlasting typical hydration; surface the chosen policy. **(This research recommends `5s, 15s, 30s, 60s` — see Pitfall 1 / Code Examples.)**
- Whether `IStepDispatcher` lives in `Orchestrator/Scheduling` or a new `Orchestrator/Dispatch` folder — planner's call.
- Test-harness shape — reuse Phase 23's in-memory-bus + Redis-mux-stub pattern + `CapturingDispatchConsumer`.

### Deferred Ideas (OUT OF SCOPE)
- **Per-`workflowId` safe L1 eviction (`FUTURE-STOP-EVICTION`)** — stopped workflows linger in L1 this phase (accepted; bounded population). Do NOT ship the static-leaf-count form (unsafe under fan-in/diamonds/pruning/concurrency); the analyzed robust future mechanism is a per-`correlationId` pending-dispatch reference counter.
- Duplicate-delivery dedup / idempotency tracking — accepted duplicates this phase (`ORCH-SCALE-01`).
- Cross-replica duplicate-dispatch coordination — deferred (`ORCH-SCALE-01`).
- `FUT-REQRESP-01` processor self-id request/response — separate, still deferred.
- Multiple-results-per-step / streaming progress — locked to one-result-per-step.
- Any L2/Redis read or write on the **result** path; processor-output payload forwarding; cron/fire changes.

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| ORCH-RESULT-01 | `StepOutcome` enum + `ExecutionResult` record in `Messaging.Contracts` | Contracts surface mapped: `IExecutionCorrelated` shape (`ExecutionId/WorkflowId/StepId/ProcessorId/EntryId` + `CorrelationId` from `ICorrelated`); `EntryStepDispatch` is the positional-record precedent; STJ serialization is int-for-enum (no string converter registered — `StepProjection.EntryCondition` is already a bare `int`). See Contracts section. |
| ORCH-RESULT-02 | Load-balanced shared result consume endpoint (`IConsumer<ExecutionResult>`) | Current orchestrator binds ONLY instance-unique fan-out (`InstanceId` + `Temporary=true`, shared `EndpointName="orchestrator"`). The new endpoint must be a STABLE shared name (`queue:orchestrator-result`) with NO `InstanceId`/`Temporary` so replicas compete. See Architecture Patterns. |
| ORCH-ADVANCE-01 | L1-only edge traversal + entry-condition match | `IWorkflowL1Store.TryGet` → `WorkflowL1.Steps[stepId]` → `StepProjection.NextStepIds`/`.EntryCondition` is the full read surface; `StepAdvancement` is a pure function over it. Match = `EntryCondition == (int)outcome || EntryCondition == 4`. |
| ORCH-ADVANCE-02 | Continuation dispatch reusing `EntryStepDispatch` to `queue:{nextStep.ProcessorId}` | `IStepDispatcher` (D-01) extracted from `WorkflowFireJob` 66–73; copy `CorrelationId/EntryId/ExecutionId/WorkflowId` from the result, take `StepId/ProcessorId/Payload` from the next-step projection. |
| ORCH-RESULT-ACK-01 | Business-ack vs infra-throw on the result path | Mirror `WorkflowLifecycle.IsInfra`/`IsBusiness` + `AckSemanticsTests` precedent: unknown `(wf,step)` / no-match / corrupt-projection = `return` (ack); infra fault propagates to bounded retry → `_error`. |
| ORCH-GATE-01 | Gate-closed never-drop (delayed redelivery) for Start/Stop/result | **TOP gray area resolved** — `UseScheduledRedelivery` + in-memory scheduler (no RabbitMQ plugin); throw on `!gate.IsReady`. See Pitfall 1 + Code Examples + Don't-Hand-Roll. |
| ORCH-START-RELOAD-01 | Conditionless Start (hydrate + reschedule, no skip) | Strip the `gate-closed return`, `TryAcquire` stripe, and stripe-held skip from `StartOrchestrationConsumer`; keep `TeardownAsync` + `HydrateAndScheduleAsync`. Gate-closed becomes a THROW. |
| ORCH-STOP-DRAIN-01 | Conditionless Stop (delete jobs, KEEP L1) | `WorkflowLifecycle.TeardownAsync` currently `UnscheduleAsync` + `store.Remove` — Stop must NOT call `store.Remove` (keep L1). Either split teardown into "unschedule-only" or pass a keep-L1 flag. See Architecture Patterns. |
| WEBAPI-SUPPRESS-01 | WebApi first-win idempotent L2-root create/delete | **VERIFIED current behavior is NOT first-win** — Start unconditionally `StringSetAsync`-overwrites (via `RedisProjectionWriter.UpsertAsync` after a delete-then-write pre-clean); Stop uses an EXISTS-gate that 422s on a second stop. See WebApi section for the exact minimal change. |

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| `StepOutcome` enum + `ExecutionResult` record | Messaging.Contracts (shared leaf) | — | Both processor (future) and orchestrator must agree on one wire shape; orchestrator references ONLY Contracts |
| Shared result consume endpoint | Orchestrator (consumer) | — | The orchestrator owns DAG advancement; the result queue is its competing-consumer endpoint |
| Edge traversal + entry-condition match | Orchestrator (in-memory L1) | — | L1-only by constraint; pure in-process logic, no I/O |
| Continuation dispatch (`Send`) | Orchestrator → RabbitMQ (`queue:{processorId}`) | — | Dispatch is the orchestrator→processor handoff; `Send` not `Publish` |
| Gate-closed redelivery | RabbitMQ transport + MassTransit middleware | Orchestrator endpoint config | Redelivery is a transport/middleware concern; consumer only throws |
| Duplicate start/stop suppression | WebApi (L2 root create/delete) | Redis (L2 store) | D-04 locks the WebApi as the SINGLE dedup point; orchestrator stays conditionless |
| L1 store (root + steps + stripes) | Orchestrator (in-memory singleton) | — | Per-replica `ConcurrentDictionary`; no L2 mutation anywhere in the orchestrator |

## Standard Stack

### Core (all VERIFIED present — Directory.Packages.props)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | **8.5.5** | Bus, consumers, retry/redelivery middleware | `[VERIFIED: Directory.Packages.props:137]`. **8.5.5 is the last Apache-2.0 line; v9+ is COMMERCIAL** — do NOT propose upgrading. |
| MassTransit.RabbitMQ | 8.5.5 | RabbitMQ transport + delayed-exchange support (if plugin present) | `[VERIFIED: Directory.Packages.props:138]` |
| Quartz / Quartz.Extensions.Hosting | 3.18.1 | Per-workflow cron jobs (already wired in orchestrator) | `[VERIFIED: Directory.Packages.props:96-97]` |
| StackExchange.Redis | 2.13.1 | L2 reads (hydration) + WebApi L2 writes | `[VERIFIED: Directory.Packages.props:131]` — `When.NotExists` / `KeyDeleteAsync` available |

### Supporting (test stack — VERIFIED in tests)
| Library | Purpose | When to Use |
|---------|---------|-------------|
| `MassTransit.Testing` (`AddMassTransitTestHarness`, `ITestHarness`) | In-memory bus harness | All dispatch-capture + consume tests (Phase 23 precedent) |
| NSubstitute | `IConnectionMultiplexer`/`IDatabase`/`ConsumeContext` stubs | Redis-mux stubs (`OrchestratorTestStubs`) + consume-context fakes |
| `Microsoft.Extensions.Time.Testing.FakeTimeProvider` | Deterministic clock | Liveness/cron tests |
| xUnit (`TestContext.Current.CancellationToken`) | Test runner | All tests |

### Alternatives Considered (for gate-closed redelivery — ORCH-GATE-01)
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| In-memory scheduler + `UseScheduledRedelivery` (**RECOMMENDED**) | `UseDelayedRedelivery` + delayed-exchange plugin | Plugin requires enabling `rabbitmq_delayed_message_exchange` in the broker image (new infra dependency + Dockerfile/compose change). In-memory needs NO broker change but is in-process + lossy-on-restart — acceptable since the gate is only closed during a short startup window and redelivery only matters in-process. `[CITED: masstransit docs /configuration/middleware/redelivery]` |
| In-memory scheduler | Quartz-backed `UseScheduledRedelivery` (orchestrator already has Quartz) | Quartz adds durability but overkill for short redelivery windows; docs note in-memory/transport preferred for "short redelivery times". `[CITED: masstransit /configuration/scheduling]` |

**Installation:** No new packages required — all dependencies already present.

**Version verification:** `[VERIFIED: Directory.Packages.props]` MassTransit 8.5.5, MassTransit.RabbitMQ 8.5.5, Quartz 3.18.1, StackExchange.Redis 2.13.1. **Constraint: MassTransit MUST stay ≤ 8.5.5** (commercial license above that).

## Architecture Patterns

### System Architecture Diagram

```
                 ┌──────────── WebApi (BaseApi.Service / OrchestrationService) ────────────┐
  HTTP Start ───▶│ ExistenceCheck → first-win L2-root create-if-absent → Publish Start ─────┼──┐
  HTTP Stop  ───▶│ rule-validate → delete-if-present L2-root        → Publish Stop  ────────┼──┤
                 └──────────────────────────────────────────────────────────────────────────┘  │
                                                                                                 │ (RabbitMQ control msgs)
                                                                                                 ▼
   ┌──────────────────────────────── Orchestrator (BaseConsole) ─────────────────────────────────────┐
   │                                                                                                   │
   │  StartOrchestration ─▶ [gate open?] ──no──▶ THROW ─▶ ScheduledRedelivery (5s,15s,30s,60s) ─┐      │
   │                          │ yes                                                            (re-deliver after MarkReady)
   │                          ▼                                                                  │      │
   │           WorkflowLifecycle.Teardown + HydrateAndSchedule (L2 READ → L1 upsert + Quartz)    │      │
   │                                                                                             │      │
   │  StopOrchestration  ─▶ [gate open?] ──no──▶ THROW ─▶ (same redelivery) ─────────────────────┘      │
   │                          │ yes                                                                     │
   │                          ▼                                                                          │
   │           Unschedule Quartz job  (KEEP L1 entry — D-07 drain)                                       │
   │                                                                                                     │
   │  Quartz cron fire ─▶ WorkflowFireJob ─▶ IStepDispatcher.DispatchAsync ──┐                            │
   │                                                                          │                           │
   │  ExecutionResult ─▶ [gate open?] ─no─▶ THROW ─▶ redelivery               │                           │
   │  (queue:orchestrator-     │ yes                                          │                           │
   │   result, competing)      ▼                                             │                           │
   │     L1.TryGet(wf,step) ─miss─▶ ACK (business)                            │                           │
   │            │ hit                                                         │                           │
   │            ▼                                                             ▼                           │
   │   StepAdvancement(outcome, completedStep, L1.Steps) ─▶ next steps ─▶ IStepDispatcher.DispatchAsync ──┼──▶ queue:{processorId}
   │   (match EntryCondition == (int)outcome || == 4; Never=5 never)         (Send EntryStepDispatch)     │      (to processors)
   │                                                                                                       │
   │   infra fault on any path ─▶ propagate ─▶ UseMessageRetry Immediate(3) ─▶ _error                      │
   └───────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### Component Responsibilities
| File (current/new) | Responsibility | Phase 24 change |
|--------------------|----------------|-----------------|
| `Messaging.Contracts/ExecutionResult.cs` (NEW) | Result wire shape | Create record implementing `IExecutionCorrelated` |
| `Messaging.Contracts/StepOutcome.cs` (NEW) | Outcome enum (0–3) | Create enum |
| `Messaging.Contracts/<constant>` (NEW) | `queue:orchestrator-result` name constant | One source of truth (D-03) |
| `Orchestrator/Dispatch/IStepDispatcher.cs` + impl (NEW) | Build + Send `EntryStepDispatch` | Extract from `WorkflowFireJob` 66–73 (D-01) |
| `Orchestrator/<...>/StepAdvancement.cs` (NEW) | Pure match + traversal | D-02 |
| `Orchestrator/Consumers/ResultConsumer.cs` + Definition (NEW) | `IConsumer<ExecutionResult>` on shared endpoint | D-03/D-08; gate-closed throw |
| `Orchestrator/Scheduling/WorkflowFireJob.cs` | Cron fire | Refactor 66–73 to call `IStepDispatcher` |
| `Orchestrator/Consumers/StartOrchestrationConsumer.cs` | Start | Conditionless + gate-closed throw (D-05/D-06) |
| `Orchestrator/Consumers/StopOrchestrationConsumer.cs` | Stop | Conditionless + KEEP L1 + gate-closed throw (D-05/D-06/D-07) |
| `Orchestrator/Hydration/WorkflowLifecycle.cs` | Hydrate/teardown | Split teardown so Stop unschedules WITHOUT `store.Remove` (D-07) |
| `Orchestrator/Program.cs` | Bus wiring | Add shared result endpoint + scheduler + redelivery |
| `BaseApi.Service/Features/Orchestration/OrchestrationService.cs` | WebApi Start/Stop | First-win create-if-absent / delete-if-present (WEBAPI-SUPPRESS-01) |

### Pattern 1: Competing-consumer (shared) ReceiveEndpoint vs. instance-unique fan-out
**What:** The current Start/Stop endpoints are deliberately fan-out (each replica gets its own copy): `Program.cs:31-33` sets `e.InstanceId = instanceId; e.Temporary = true;` on a shared `EndpointName="orchestrator"`. The result endpoint must be the OPPOSITE — a single stable named queue with NO `InstanceId`/`Temporary` so exactly one replica consumes each result.
**When to use:** Result consume (load-balanced, ORCH-RESULT-02).
**Example:**
```csharp
// Source: VERIFIED current Program.cs pattern; result endpoint is the competing-consumer inverse.
x.AddConsumer<ResultConsumer, ResultConsumerDefinition>();
// In ResultConsumerDefinition: EndpointName = OrchestratorQueues.Result;  // "orchestrator-result"
// NO InstanceId, NO Temporary  → durable shared competing-consumer queue
```
Note: a `Send` to `queue:orchestrator-result` lands on a `ReceiveEndpoint("orchestrator-result")` — the `queue:` URI scheme maps to the short endpoint name (VERIFIED by `CapturingDispatchConsumer` test using `ReceiveEndpoint($"{processorId:D}")` to capture `Send`s to `queue:{processorId:D}`).

### Pattern 2: IStepDispatcher extraction (D-01)
**What:** Lift the build-and-send block so both the fire job and result consumer share one dispatch shape.
**Example:**
```csharp
// Source: VERIFIED WorkflowFireJob.cs:66-73 (the block to extract)
public sealed class StepDispatcher(ISendEndpointProvider sendProvider) : IStepDispatcher
{
    public async Task DispatchAsync(Guid workflowId, Guid stepId, Guid processorId, string payload,
        Guid correlationId, Guid executionId, Guid entryId, CancellationToken ct)
    {
        var msg = new EntryStepDispatch(workflowId, stepId, processorId, payload)
        {
            CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId,
        };
        var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{processorId:D}"));
        await endpoint.Send(msg, ct);
    }
}
```
The fire job passes `executionId = entryId = Guid.Empty` (initial fire); the result consumer copies them from the `ExecutionResult`.

### Pattern 3: Conditionless Stop that KEEPS L1 (D-07)
**What:** `WorkflowLifecycle.TeardownAsync` (VERIFIED lines 114–124) currently does `UnscheduleAsync` + `store.Remove`. Stop must unschedule WITHOUT removing L1.
**Recommended:** Split into `UnscheduleOnlyAsync(workflowId)` (no `store.Remove`) used by Stop, vs. the full teardown (unschedule + remove) used by Start's reload pre-clean. Start's reload re-hydrates anyway (Upsert overwrites), so Start can keep using full teardown OR just `HydrateAndScheduleAsync` (Upsert replaces the L1 entry; the old Quartz job must still be unscheduled first to avoid duplicate jobKeys — Pitfall 4 below).

### Anti-Patterns to Avoid
- **Ack-return on a closed gate (the Phase 23 behavior).** For results this silently stalls the DAG (a one-time event is lost). D-06 supersedes it — THROW so redelivery reschedules. The existing `StartOrchestrationConsumer.cs:32-37` / `StopOrchestrationConsumer.cs:29-34` gate-closed `return` must become a throw.
- **Removing L1 on Stop.** Breaks drain (D-07). Do NOT call `store.Remove` from the Stop path.
- **Reading L2 on the result path.** Constraint violation — result path is L1-only (`TryGet` only). No `redis.GetDatabase()` in the result consumer.
- **`Publish` instead of `Send` for continuations.** Convention is `Send` to `queue:{processorId:D}` (VERIFIED `WorkflowFireJob:71-73`).
- **Referencing `BaseApi.Service.StepEntryCondition` from the orchestrator.** Forbidden — orchestrator references ONLY `Messaging.Contracts`. Use the named int constants `Always=4`/`Never=5`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Re-process a message after the startup gate opens | A custom in-memory "pending message" buffer + replay loop | MassTransit `UseScheduledRedelivery` (second-level retry) | The transport/middleware already removes-and-reschedules the message with backoff; a hand-rolled buffer loses messages on crash and duplicates the retry pipeline. `[CITED: masstransit /configuration/middleware/redelivery]` |
| Delay before redelivery | `Task.Delay` + manual re-publish | `UseScheduledRedelivery(r => r.Intervals(...))` + a registered scheduler | Manual delay blocks the consumer thread and doesn't survive shutdown; the scheduler offloads the wait. |
| Bounded infra-fault retry → dead-letter | try/catch with a counter | `UseMessageRetry(r => r.Immediate(3))` (already in the consumer definitions) + MassTransit `_error` queue | Already the project idiom (VERIFIED `*ConsumerDefinition.cs`). |
| First-win create on Redis | GET-then-SET (race window) | `StringSetAsync(key, val, when: When.NotExists)` | Atomic SET NX; GET-then-SET has a TOCTOU race between two concurrent Starts. `[CITED: StackExchange.Redis When enum]` |

**Key insight:** The gate-closed-never-drop requirement is a textbook second-level-retry use case. MassTransit ships it; the only project-specific decision is the scheduler backend (in-memory vs. plugin) and the interval schedule.

## Common Pitfalls

### Pitfall 1: `UseDelayedRedelivery` silently needs the RabbitMQ delayed-exchange plugin — which is NOT installed
**What goes wrong:** Wiring `UseDelayedRedelivery` against this repo's `rabbitmq:4.1.8-management-alpine` broker will fail to actually delay/redeliver because the `rabbitmq_delayed_message_exchange` plugin is not enabled anywhere (VERIFIED: grep for `delayed`/`plugin` across compose/Dockerfiles found nothing; the broker image has no `rabbitmq-plugins enable` step).
**Why it happens:** `UseDelayedRedelivery` uses transport-native delayed scheduling; on RabbitMQ that means the plugin. `[CITED: masstransit /configuration/middleware/redelivery — "UseDelayedRedelivery on RabbitMQ: Requires the delayed-exchange plugin"]`
**How to avoid:** Use `UseScheduledRedelivery` with a registered message scheduler. Two viable backends:
  1. **In-memory scheduler (RECOMMENDED, no infra change):** `x.AddInMemoryMessageScheduler()` + `cfg.UseInMemoryScheduler()` in the bus factory. In-process, lossy-on-restart — fine because the gate is only closed briefly at startup and redelivery is an in-process concern. `[CITED: masstransit /configuration/scheduling — in-memory/transport preferred for short redelivery times]`
  2. **Enable the plugin (if durability across restart is wanted):** add `rabbitmq-plugins enable rabbitmq_delayed_message_exchange` to the broker image + `x.AddDelayedMessageScheduler()` / `cfg.UseDelayedMessageScheduler()`, then `UseDelayedRedelivery`. More infra surface.
**Warning signs:** Messages disappearing instead of redelivering; or an exception about a missing exchange type `x-delayed-message` at bus start.
**Recommended interval policy:** `r.Intervals(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60))` — 4 scheduled attempts spanning ~110s, comfortably outlasting a single-replica hydration window (hydration is a SMEMBERS + per-workflow L2 GETs, typically sub-second to a few seconds). After exhaustion (e.g. a genuine Redis-down outage that never lets hydration complete), the message routes to `_error` as required by D-06. `[ASSUMED: interval values — hydration duration not benchmarked; surfaced as Claude's-discretion per CONTEXT.]`

### Pitfall 2: Retry/redelivery configuration ORDER
**What goes wrong:** If `UseMessageRetry` is configured before `UseScheduledRedelivery` (or vice-versa, depending on version) the redelivery wraps the wrong layer and may not redeliver.
**Why it happens:** Middleware is a pipeline; the outer filter must be the redelivery (it removes-and-reschedules), the inner must be the immediate retry.
**How to avoid:** Configure **`UseScheduledRedelivery` (or `UseDelayedRedelivery`) FIRST, then `UseMessageRetry`** on the endpoint/consumer. `[CITED: masstransit redelivery docs + GitHub #1575 "If UseScheduledRedelivery is executed before UseMessageRetry, it works as expected"]`. The existing `Immediate(3)` stays as the inner immediate retry.
**Warning signs:** Gate-closed message goes straight to `_error` after 3 immediate retries instead of waiting for the redelivery interval.

### Pitfall 3: Scheduler registration lives in the bus FACTORY, but the base library owns `UsingRabbitMq`
**What goes wrong:** `UseInMemoryScheduler()` / `UseDelayedMessageScheduler()` are bus-factory-level calls inside `UsingRabbitMq((ctx, c) => ...)`, but that lambda is OWNED by `BaseConsole.Core.AddBaseConsoleMessaging` (VERIFIED: it only exposes `Action<IBusRegistrationConfigurator> configureConsumers` — the consumer-registration seam — NOT the bus-factory callback). The orchestrator cannot reach `c.UseInMemoryScheduler()` through the current seam.
**Why it happens:** The base library's signature `AddBaseConsoleMessaging(cfg, configureConsumers)` deliberately hides the RabbitMQ factory.
**How to avoid (planner decision):**
  - `x.AddInMemoryMessageScheduler()` (the `AddXxx` half) CAN be called via the existing `configureConsumers` seam (it operates on `IBusRegistrationConfigurator`).
  - The `c.UseInMemoryScheduler()` (the bus-factory `UseXxx` half) needs either (a) a new optional `Action<IRabbitMqBusFactoryConfigurator>` parameter on `AddBaseConsoleMessaging`, or (b) an `x.AddConfigureEndpointsCallback(...)` (operates per-endpoint, can add retry/redelivery but NOT the scheduler registration), or (c) the scheduler `UseXxx` may be unnecessary if `AddInMemoryMessageScheduler` self-registers the in-memory pipeline. **Verify against MassTransit 8.5.5 source whether `UseInMemoryScheduler()` is required when `AddInMemoryMessageScheduler()` is registered.** `[ASSUMED: the base-library seam needs extension — confirm exact 8.5.5 API surface during planning.]`
**Warning signs:** Compile error reaching `UseInMemoryScheduler` from the orchestrator; or redelivery scheduled but never fires (scheduler not wired into the consume pipeline).

### Pitfall 4: Conditionless Start must still unschedule the old Quartz job before rescheduling
**What goes wrong:** A reload Start that calls `HydrateAndScheduleAsync` without first deleting the existing Quartz job risks a duplicate `JobKey` (`ScheduleJob` throws `ObjectAlreadyExistsException`).
**Why it happens:** `WorkflowScheduler.ScheduleAsync` (VERIFIED) builds a job with `WithIdentity(JobKey(jobId))`; the jobId comes from the L2 root and `RedisProjectionWriter` mints a FRESH `jobId` on every Start write (VERIFIED `RedisProjectionWriter.cs:71 JobId: Guid.NewGuid()`). So a reload gets a NEW jobId → no collision IF the old job is gone. But if the same jobId somehow persists, schedule throws. Safest: Start does teardown (unschedule old) then hydrate+schedule (with the new jobId from the re-read root).
**How to avoid:** Keep Start's `TeardownAsync` (unschedule + remove L1) before `HydrateAndScheduleAsync` — the re-hydrate immediately re-Upserts L1, so the transient remove is harmless. (Stop is the one that must NOT remove L1.)
**Warning signs:** `ObjectAlreadyExistsException` on re-Start.

### Pitfall 5: STJ serialization of `StepOutcome` / `ExecutionResult`
**What goes wrong:** If `StepOutcome` serialized as a string but `StepProjection.EntryCondition` is an int, the equality match still works (different fields) — but a string-enum converter on the bus envelope could surprise a future processor.
**Why it happens:** No `JsonStringEnumConverter` is registered anywhere (VERIFIED note in `StepProjection.cs:11-13` — "no string-enum converter is registered anywhere, so the enum serializes as its underlying int"). MassTransit envelope serialization is separate from the Redis JSON projection.
**How to avoid:** Keep `StepOutcome` a plain `enum : int` with explicit values `Processing=0, Completed=1, Failed=2, Cancelled=3`. The match in `StepAdvancement` casts `(int)outcome` and compares to `StepProjection.EntryCondition` (int). No converter needed. `ExecutionResult` is a positional record (mirror `EntryStepDispatch.cs`) — NO `[JsonPropertyName]` (it's a bus envelope, not a Redis projection — VERIFIED `EntryStepDispatch.cs:6-8` comment).

### Pitfall 6: WebApi Stop EXISTS-gate is non-idempotent by design (current) and conflicts with first-win
**What goes wrong:** The CURRENT `StopAsync` (VERIFIED `OrchestrationService.cs:199-263`) does a batch `KeyExistsAsync` on every root and throws `OrchestrationValidationException.MissingRoots` (→ 422) if ANY root is missing — "Repeated Stop of an already-cleaned workflow re-fails the gate (422 — non-idempotent by design)". WEBAPI-SUPPRESS-01 wants a second Stop of an absent workflow to be a **no-op**, not a 422.
**Why it happens:** Phase 22 chose strict 422 semantics; Phase 24 redesigns toward first-win idempotency.
**How to avoid:** This is a behavioral change the planner must reconcile against existing facts. The minimal first-win change:
  - **Start:** today unconditionally overwrites (pre-clean delete-then-write + `StringSetAsync` with no `When` — VERIFIED `RedisProjectionWriter.cs:80`). First-win means: only write the root if absent. Cleanest = `StringSetAsync(Root(wf), rootJson, when: When.NotExists)` and skip the publish if the SET returned false (already present). NOTE this changes the pre-clean/overwrite contract (ORCH-START-05 shrunk-graph GC) — the planner must decide whether first-win replaces or guards the existing write path.
  - **Stop:** delete-if-present, no-op if absent (replace the 422 gate with a tolerant skip). `KeyDeleteAsync` already returns a bool (was-present); gate the publish on it.
  - **Existing tests to reconcile:** `StartOrchestrationFacts`, `StopOrchestrationFacts`, `StopScanFacts`, `OrchestrationServicePublishTests` assert the CURRENT 422/overwrite behavior — they will need updating. `[VERIFIED: grep found these fact files.]`
**Warning signs:** A second Start re-publishes to the orchestrator (should be suppressed); a second Stop 422s (should be a no-op).

## Code Examples

### Result consumer (gate-closed throw + L1-only + business-ack split)
```csharp
// Source: composed from VERIFIED AckSemanticsTests + StartOrchestrationConsumer + WorkflowLifecycle.IsInfra patterns
public sealed class ResultConsumer(
    IStartupGate gate,
    IWorkflowL1Store store,
    StepAdvancement advancement,
    IStepDispatcher dispatcher,
    ILogger<ResultConsumer> logger) : IConsumer<ExecutionResult>
{
    public async Task Consume(ConsumeContext<ExecutionResult> context)
    {
        if (!gate.IsReady)
            throw new GateClosedException(); // D-06: THROW -> scheduled redelivery, NOT ack-return

        var m = context.Message;
        if (!store.TryGet(m.WorkflowId, out var wf) || !wf.Steps.TryGetValue(m.StepId, out var completed))
            return; // business ack — unknown (wf,step) or drained (D-08)

        var nextSteps = advancement.SelectNext(m.Outcome, completed, wf.Steps); // pure, L1-only
        foreach (var (stepId, step) in nextSteps)
            await dispatcher.DispatchAsync(
                m.WorkflowId, stepId, step.ProcessorId, step.Payload,
                m.CorrelationId, m.ExecutionId, m.EntryId, context.CancellationToken);
        // infra fault from Send propagates -> Immediate(3) -> _error
    }
}
```

### StepAdvancement match (D-02, pure)
```csharp
// Source: D-02 / SPEC req 3. Always=4, Never=5 named locally — NO BaseApi.Service reference.
public sealed class StepAdvancement
{
    private const int Always = 4; // private const int Never = 5; (never selected — falls out of the predicate)
    public IEnumerable<(Guid stepId, StepProjection step)> SelectNext(
        StepOutcome outcome, StepProjection completed, IReadOnlyDictionary<Guid, StepProjection> steps)
    {
        foreach (var nextId in completed.NextStepIds)
            if (steps.TryGetValue(nextId, out var next) &&
                (next.EntryCondition == (int)outcome || next.EntryCondition == Always))
                yield return (nextId, next);
    }
}
```

### Endpoint redelivery + retry (correct order)
```csharp
// Source: CITED masstransit redelivery docs + GitHub #1575. In a ConsumerDefinition.ConfigureConsumer
// (the existing seam) or AddConfigureEndpointsCallback.
endpointConfigurator.UseScheduledRedelivery(r =>
    r.Intervals(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));
endpointConfigurator.UseMessageRetry(r => { r.Immediate(3); r.Ignore<WorkflowRootNotFoundException>(); });
```

### WebApi first-win create (SET NX)
```csharp
// Source: CITED StackExchange.Redis When enum (2.13.1). Replaces unconditional StringSetAsync.
var created = await db.StringSetAsync(RedisProjectionKeys.Root(wf.Id), rootJson, when: When.NotExists);
if (!created) { /* already present -> first-win skip; do NOT publish StartOrchestration for this id */ }
```

## Runtime State Inventory

> This is a feature/refactor phase touching messaging topology + Redis write semantics. Inventory below.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | L1 is in-memory only (per-replica `ConcurrentDictionary`, VERIFIED `WorkflowL1Store`). L2 Redis stores `skp:{workflowId}` root + `skp:{wf}:{step}` step keys + `skp:` parent-index SET. Phase 24 changes the WebApi write semantics (first-win) but NOT the key SHAPES. | Code edit only (WebApi write path). No data migration — existing keys remain valid. |
| Live service config | RabbitMQ broker (`sk-rabbitmq`, `rabbitmq:4.1.8-management-alpine`). A NEW durable competing-consumer queue `orchestrator-result` will be auto-declared by MassTransit at orchestrator start. **If the delayed-exchange plugin path is chosen, the broker image must enable `rabbitmq_delayed_message_exchange`** — a live broker config change (not in git today). | If in-memory scheduler chosen: none. If plugin chosen: broker image/Dockerfile change + re-deploy. |
| OS-registered state | None — no OS-level task/service registrations involve the changed strings. Quartz uses RAMJobStore (in-memory, VERIFIED `Program.cs:37`). | None — verified by reading Program.cs (RAMJobStore, no persistent job store). |
| Secrets/env vars | RabbitMq__Host/Username/Password + ConnectionStrings__Redis (VERIFIED compose.yaml). No new secrets; `queue:orchestrator-result` is a code constant, not env. | None. |
| Build artifacts | New files compile into existing assemblies (`Messaging.Contracts`, `Orchestrator`). No package/version bumps. | None — `dotnet build` picks up new files. |

## Validation Architecture

> nyquist_validation is enabled (config.json `workflow.nyquist_validation: true`).

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (v3 — `TestContext.Current.CancellationToken` VERIFIED) + MassTransit.Testing + NSubstitute + FakeTimeProvider |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (single test project; orchestrator tests under `tests/BaseApi.Tests/Orchestrator/`) |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Orchestrator"` |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| ORCH-RESULT-01 | `ExecutionResult` round-trips all fields; no payload field; `StepOutcome` ints 0–3 | unit | `dotnet test --filter ExecutionResultContract` | ❌ Wave 0 |
| ORCH-RESULT-02 | Result consumed once on shared queue (competing, not fan-out) | harness | `dotnet test --filter ResultConsume` | ❌ Wave 0 |
| ORCH-ADVANCE-01 | Full outcome×entry-condition match table; Never never; no L2 read | unit | `dotnet test --filter StepAdvancement` | ❌ Wave 0 |
| ORCH-ADVANCE-02 | Continuation dispatch field-copy (ids from result, step data from L1) | harness (`CapturingDispatchConsumer`) | `dotnet test --filter ContinuationDispatch` | ❌ Wave 0 (reuse `CapturingDispatchConsumer`) |
| ORCH-RESULT-ACK-01 | Unknown/no-match/corrupt = ack; infra propagates; redeliver re-dispatches | unit + stub | `dotnet test --filter ResultAck` | ❌ Wave 0 (reuse `OrchestratorTestStubs`) |
| ORCH-GATE-01 | Gate-closed message redelivered (not acked), processed after MarkReady | harness | `dotnet test --filter GateClosedRedeliver` | ❌ Wave 0 |
| ORCH-START-RELOAD-01 | Start already-in-L1 re-hydrates + reschedules (no skip); stop→start revives | unit | extend `StartConsumerLifecycleTests` | ✅ extend existing |
| ORCH-STOP-DRAIN-01 | Stop deletes job, KEEPS L1; late result still dispatches | unit + harness | extend `StopConsumerLifecycleTests` | ✅ extend existing |
| WEBAPI-SUPPRESS-01 | 2nd Start no overwrite/republish; 2nd Stop no-op | unit | extend `StartOrchestrationFacts`/`StopOrchestrationFacts` | ✅ extend (reconcile current 422 facts) |

### Sampling Rate
- **Per task commit:** `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Orchestrator"` (+ `~Orchestration` for the WebApi facts).
- **Per wave merge:** full `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj`.
- **Phase gate:** full suite green before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Orchestrator/ExecutionResultContractTests.cs` — ORCH-RESULT-01
- [ ] `tests/BaseApi.Tests/Orchestrator/StepAdvancementTests.cs` — ORCH-ADVANCE-01 (full match table; pure, no harness)
- [ ] `tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs` — ORCH-RESULT-02 / ORCH-ADVANCE-02 (reuse `CapturingDispatchConsumer` + `AddMassTransitTestHarness`)
- [ ] `tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs` — ORCH-RESULT-ACK-01 (reuse `OrchestratorTestStubs`)
- [ ] `tests/BaseApi.Tests/Orchestrator/GateClosedRedeliverTests.cs` — ORCH-GATE-01 (harness; assert message survives a closed→open gate transition)
- [ ] No framework install needed — all test deps present.
- [ ] Reconcile/update existing facts that assert Phase 22/23 behavior: `StartOrchestrationFacts`, `StopOrchestrationFacts`, `StopScanFacts`, `OrchestrationServicePublishTests`, `StartConsumerLifecycleTests`, `StopConsumerLifecycleTests` (the conditionless + first-win redesign changes their expectations).

## Security Domain

> security_enforcement not present in config.json → treat as enabled. This is an internal backend messaging phase with no new external trust boundary.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No new auth surface; bus creds unchanged (RabbitMq guest dev-only, prod via secrets — VERIFIED compose comment T-19-broker-creds) |
| V3 Session Management | no | Stateless message processing |
| V4 Access Control | no | No new endpoints exposed externally; result queue is internal broker topology |
| V5 Input Validation | yes | `ExecutionResult` is deserialized off the bus — a corrupt projection / malformed result is a BUSINESS ack (graceful skip), NOT a crash (mirror `WorkflowLifecycle.IsBusiness`). No payload field reduces injection surface (SPEC). |
| V6 Cryptography | no | None hand-rolled |

### Known Threat Patterns for .NET/MassTransit/RabbitMQ
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Poison message on the result queue stalls the consumer | Denial of Service | Bounded `Immediate(3)` retry → `_error` (VERIFIED existing definition idiom); business faults ack-skip and never retry-storm |
| Redelivery storm during a long Redis outage | Denial of Service | Finite scheduled-redelivery interval set → exhausts to `_error` (D-06); intervals bounded (~110s total recommended) |
| Duplicate/replayed result causes duplicate dispatch | Tampering (limited) | Accepted this phase (`ORCH-SCALE-01` dedup deferred); not a security boundary (internal trusted bus) |
| Untrusted L1 projection (corrupt JSON) | Tampering | L1 is hydrated only from L2 written by the trusted WebApi; corrupt = business skip |

## State of the Art

| Old Approach (Phase 23) | Current Approach (Phase 24) | When Changed | Impact |
|--------------------------|------------------------------|--------------|--------|
| Gate-closed ack-DROP (D-12) | Gate-closed THROW → delayed redelivery | This phase (D-06) | One-time results no longer lost during hydration |
| Stop CLEARS L1 (ORCH-STOP-01) | Stop KEEPS L1 (drain) | This phase (D-07) | Late results for stopped workflows still advance |
| Start gated teardown + stripe + skip (ORCH-CONSUME-01) | Conditionless hydrate+reschedule | This phase (D-05) | Simpler; dedup moves to WebApi |
| WebApi unconditional overwrite / 422 Stop gate | First-win create-if-absent / delete-if-present | This phase (WEBAPI-SUPPRESS-01) | Idempotent start/stop; reconciles existing Phase 22 facts |
| Orchestrator only fires entry steps (never advances) | Consumes results, advances DAG | This phase | The processor→orchestrator round-trip half |

**Deprecated/outdated:**
- `MassTransit ≥ 9.x` — COMMERCIAL license (VERIFIED Directory.Packages.props comment). The redelivery/scheduler APIs cited here are the 8.5.5 surface; do NOT assume v9 docs apply.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Recommended redelivery intervals `5s/15s/30s/60s` outlast hydration | Pitfall 1 / Code Examples | If hydration is slower (large workflow population, slow Redis), the message could exhaust to `_error` before the gate opens. Mitigate: measure hydration time or pad the last interval. Surfaced as Claude's-discretion in CONTEXT. |
| A2 | The `BaseConsole.Core.AddBaseConsoleMessaging` seam needs extension to wire `UseInMemoryScheduler()` (the bus-factory `UseXxx` half) | Pitfall 3 | If MassTransit 8.5.5 auto-wires the in-memory scheduler from `AddInMemoryMessageScheduler()` alone, the base-library change is unnecessary. Verify the exact 8.5.5 API before planning the seam change. |
| A3 | `Send` to `queue:orchestrator-result` reaches a `ReceiveEndpoint("orchestrator-result")` (short-name mapping) | Architecture / Pattern 1 | Strongly supported by the VERIFIED `CapturingDispatchConsumer` precedent for `queue:{processorId:D}`, but the result endpoint is a consumer-bound endpoint (not a raw Send target) — the processor (future) Sends to it. Confirm endpoint-name vs. queue-URI mapping in the result-consume test. |
| A4 | Changing WebApi Start to `When.NotExists` does not break the ORCH-START-05 shrunk-graph GC contract | Pitfall 6 | First-win-create may conflict with the existing delete-then-write pre-clean (which exists to GC orphan step keys on re-Start of a shrunk graph). The planner must decide whether first-win GUARDS the whole write (skip everything if root present) or only the root SET. This is a genuine design fork. |

## Open Questions

1. **Scheduler backend for redelivery (in-memory vs. plugin)**
   - What we know: `UseDelayedRedelivery` needs the RabbitMQ plugin (not installed); `UseScheduledRedelivery` + in-memory scheduler needs no infra change but is in-process/lossy-on-restart.
   - What's unclear: whether the team wants redelivery to survive an orchestrator restart (would favor the plugin or a durable scheduler).
   - Recommendation: in-memory scheduler — the gate is only closed at startup, and a restart re-opens the gate anyway, so in-process redelivery is sufficient. Surface to discuss-phase if durability matters.

2. **WebApi first-win vs. existing ORCH-START-05 pre-clean / 422 Stop gate (A4 / Pitfall 6)**
   - What we know: current Start overwrites + GCs; current Stop 422s on a missing root. WEBAPI-SUPPRESS-01 wants first-win idempotency.
   - What's unclear: whether first-win replaces or wraps the existing write/GC path, and whether the 422-on-repeat-Stop facts are intentionally being dropped.
   - Recommendation: planner reconciles against `StartOrchestrationFacts`/`StopOrchestrationFacts`/`StopScanFacts`; treat the behavioral change as deliberate (SPEC supersedes Phase 22 here) and update those facts.

3. **`UseInMemoryScheduler` wiring through the base library seam (A2 / Pitfall 3)**
   - Recommendation: confirm the minimal 8.5.5 API (does `AddInMemoryMessageScheduler` suffice, or is `UseInMemoryScheduler` in the bus factory also required?) and, if required, add an optional bus-factory callback to `AddBaseConsoleMessaging` — keeping the dependency-firewall (base = infra) intact.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| RabbitMQ broker | Bus + result queue + redelivery transport | ✓ (compose) | rabbitmq 4.1.8-management-alpine | — |
| RabbitMQ delayed-message-exchange plugin | `UseDelayedRedelivery` (only if chosen) | ✗ | — | `UseScheduledRedelivery` + in-memory scheduler (no plugin) |
| Redis | L2 read (hydration) + WebApi L2 write | ✓ (compose) | redis (alpine) | — |
| Quartz | Per-workflow cron (existing) | ✓ | 3.18.1 (RAMJobStore) | — |
| .NET 8 SDK | Build/test | ✓ (assumed — repo builds) | net8.0 | — |

**Missing dependencies with no fallback:** None.
**Missing dependencies with fallback:** RabbitMQ delayed-exchange plugin → in-memory message scheduler (recommended path avoids the plugin entirely).

## Sources

### Primary (HIGH confidence)
- VERIFIED codebase reads (all paths absolute under `C:\Users\UserL\source\repos\SK_P\`):
  - `src/Orchestrator/Scheduling/WorkflowFireJob.cs` (dispatch block 66–73), `WorkflowScheduler.cs`
  - `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs`, `StopOrchestrationConsumer.cs`, `StartOrchestrationConsumerDefinition.cs`, `StopOrchestrationConsumerDefinition.cs`
  - `src/Orchestrator/Hydration/WorkflowLifecycle.cs`, `src/Orchestrator/L1/IWorkflowL1Store.cs`, `WorkflowL1Store.cs`, `WorkflowL1.cs`, `src/Orchestrator/Program.cs`
  - `src/Messaging.Contracts/Projections/StepProjection.cs`, `L2ProjectionKeys.cs`, `EntryStepDispatch.cs`, `IExecutionCorrelated.cs`, `ICorrelated.cs`, `StartOrchestration.cs`
  - `src/BaseApi.Service/Features/Step/StepEntryCondition.cs`, `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs`, `Projection/RedisProjectionWriter.cs`, `Projection/RedisL2Cleanup.cs`, `Projection/RedisProjectionKeys.cs`
  - `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs`, `Health/IStartupGate.cs`
  - `tests/BaseApi.Tests/Orchestrator/CapturingDispatchConsumer.cs`, `FireDispatchTests.cs`, `AckSemanticsTests.cs`, `OrchestratorTestStubs.cs`
  - `Directory.Packages.props` (versions), `compose.yaml` (broker image + no plugin), `.planning/config.json`

### Secondary (MEDIUM confidence — official docs, cross-verified)
- [MassTransit Message Redelivery Configuration](https://masstransit.massient.com/configuration/middleware/redelivery) — `UseDelayedRedelivery` (plugin) vs `UseScheduledRedelivery` (scheduler); intervals; order
- [MassTransit Message Scheduler Configuration](https://masstransit.massient.com/documentation/configuration/scheduling) — `AddDelayedMessageScheduler`/`UseDelayedMessageScheduler`; plugin requirement; short-redelivery guidance
- [MassTransit Exceptions concept](https://masstransit.massient.com/documentation/concepts/exceptions) — retry vs redelivery (second-level retry)
- [GitHub #1575 — UseMessageRetry/UseScheduledRedelivery ordering](https://github.com/MassTransit/MassTransit/issues/1575) — configuration order
- [MassTransit Bus Configuration / AddConfigureEndpointsCallback](https://masstransit.io/documentation/configuration) — per-endpoint callback to apply retry/redelivery

### Tertiary (LOW confidence — community, flagged)
- [DevelopKerr — MassTransit scheduled redelivery](https://developkerr.com/blog/masstransit-scheduled-redelivery/) — pattern confirmation only

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all versions VERIFIED in Directory.Packages.props
- Current code shapes (dispatch, consumers, lifecycle, L1, contracts, WebApi, harness): HIGH — read directly
- Redelivery API (plugin requirement, scheduler options, ordering): MEDIUM-HIGH — CITED official docs + cross-verified across 3 sources; exact 8.5.5 `UseInMemoryScheduler` wiring through the base-library seam flagged (A2)
- WebApi first-win minimal change: HIGH on current behavior (read), MEDIUM on the chosen reconciliation (design fork — A4 / Open Q2)
- Redelivery interval values: LOW-MEDIUM — ASSUMED (A1), surfaced as Claude's-discretion

**Research date:** 2026-05-31
**Valid until:** 2026-06-30 (stable — pinned dependency versions; MassTransit 8.5.5 frozen by license constraint)
