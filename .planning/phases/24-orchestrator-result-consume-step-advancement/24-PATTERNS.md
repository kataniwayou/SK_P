# Phase 24: Orchestrator Result-Consume & Step Advancement - Pattern Map

**Mapped:** 2026-05-31
**Files analyzed:** 18 (8 new, 6 modified, 4 new test files + 6 to-reconcile)
**Analogs found:** 18 / 18 (every new/modified file has a verified in-repo analog)

All excerpts below were read directly from the cited files. Orchestrator code references ONLY
`Messaging.Contracts` (never `BaseApi.Service`). All paths absolute under `C:\Users\UserL\source\repos\SK_P\`.

## File Classification

### New files

| New File | Role | Data Flow | Closest Analog | Match Quality |
|----------|------|-----------|----------------|---------------|
| `src/Messaging.Contracts/ExecutionResult.cs` | model (contract record) | event-driven (bus message) | `src/Messaging.Contracts/EntryStepDispatch.cs` | exact (positional record + `IExecutionCorrelated`) |
| `src/Messaging.Contracts/StepOutcome.cs` | model (enum) | transform (int match) | `src/BaseApi.Service/Features/Step/StepEntryCondition.cs` (int values to mirror) | role-match (mirror ints 0–3) |
| `src/Messaging.Contracts/OrchestratorQueues.cs` (queue-name const) | config (constant) | — | `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | exact (static-class single-source-of-truth) |
| `src/Orchestrator/Dispatch/IStepDispatcher.cs` + `StepDispatcher.cs` | service | request-response (Send) | `src/Orchestrator/Scheduling/WorkflowFireJob.cs` lines 52–73 | exact (this is the block to extract) |
| `src/Orchestrator/.../StepAdvancement.cs` | utility (pure helper) | transform (match + traverse) | (no existing pure helper) — shape from D-02/RESEARCH Code Examples | role-new (see No Analog) |
| `src/Orchestrator/Consumers/ResultConsumer.cs` | controller (consumer) | event-driven | `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` | exact (consumer + gate + ack-split) |
| `src/Orchestrator/Consumers/ResultConsumerDefinition.cs` | config (endpoint def) | event-driven | `src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs` | exact (ConsumerDefinition + retry) |
| `src/Orchestrator/Consumers/GateClosedException.cs` (optional) | model (exception) | — | `src/Orchestrator/Consumers/WorkflowRootNotFoundException.cs` | exact (sealed Exception subclass) |

### Modified files

| Modified File | Role | Data Flow | Pattern Source (in-file) | Match Quality |
|---------------|------|-----------|--------------------------|---------------|
| `src/Orchestrator/Scheduling/WorkflowFireJob.cs` | service (Quartz job) | event-driven | self (lines 66–73 → `IStepDispatcher`) | exact |
| `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` | controller (consumer) | event-driven | self + `AckSemanticsTests` | exact |
| `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` | controller (consumer) | event-driven | self | exact |
| `src/Orchestrator/Hydration/WorkflowLifecycle.cs` | service | request-response (Redis read) | self (lines 110–124 split) | exact |
| `src/Orchestrator/Program.cs` | config (composition root) | — | self (lines 28–34) + `MessagingServiceCollectionExtensions` | role-match (seam extension) |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` | service (WebApi) | CRUD (Redis write) | self (`StartAsync`/`StopAsync`) + `RedisProjectionWriter` | exact |
| `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` | config (DI seam) | — | self (the `AddBaseConsoleMessaging` signature) | exact |

### New / reconciled test files

| Test File | Analog | Match Quality |
|-----------|--------|---------------|
| `tests/.../ExecutionResultContractTests.cs` (NEW) | `tests/.../EntryStepDispatchTests.cs` | role-match (contract round-trip) |
| `tests/.../StepAdvancementTests.cs` (NEW, pure) | `tests/.../CronIntervalTests.cs` (pure-helper table test) | role-match |
| `tests/.../ResultConsumeTests.cs` (NEW, harness) | `tests/.../FireDispatchTests.cs` + `CapturingDispatchConsumer.cs` | exact (reuse harness + capture) |
| `tests/.../ResultAckTests.cs` (NEW) | `tests/.../AckSemanticsTests.cs` + `OrchestratorTestStubs.cs` | exact |
| `tests/.../GateClosedRedeliverTests.cs` (NEW, harness) | `tests/.../AckSemanticsTests.cs` (gate) + `FireDispatchTests.cs` (harness) | role-match |
| `tests/.../StartConsumerLifecycleTests.cs` (RECONCILE) | self | extend |
| `tests/.../StopConsumerLifecycleTests.cs` (RECONCILE) | self | extend |
| `tests/.../StartOrchestrationFacts.cs` / `StopOrchestrationFacts.cs` / `StopScanFacts.cs` / `OrchestrationServicePublishTests.cs` (RECONCILE) | self | reconcile to first-win |

---

## Pattern Assignments

### `src/Messaging.Contracts/ExecutionResult.cs` (NEW — model, event-driven)

**Analog:** `src/Messaging.Contracts/EntryStepDispatch.cs` (entire file, 9 lines)

**Positional-record-implementing-`IExecutionCorrelated` pattern** (`EntryStepDispatch.cs:9-15`):
```csharp
public sealed record EntryStepDispatch(
    Guid WorkflowId, Guid StepId, Guid ProcessorId, string Payload) : IExecutionCorrelated
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId  { get; init; } = Guid.Empty;
    public Guid EntryId      { get; init; } = Guid.Empty;
}
```

**Interface to satisfy** (`IExecutionCorrelated.cs:9-16`) — `ExecutionResult` must expose
`CorrelationId` (from `ICorrelated`), `ExecutionId`, `WorkflowId`, `StepId`, `ProcessorId`, `EntryId`:
```csharp
public interface IExecutionCorrelated : ICorrelated
{
    Guid ExecutionId { get; }
    Guid WorkflowId  { get; }
    Guid StepId      { get; }
    Guid ProcessorId { get; }
    Guid EntryId     { get; }
}
```

**Copy:** the exact `sealed record … : IExecutionCorrelated` shape with `{ get; init; }` members.
**Differs from analog:** add `StepOutcome Outcome`, nullable `string? ErrorMessage`, nullable
`string? CancellationMessage` (SPEC req 1 / D specifics). NO output/payload field.
**Critical (RESEARCH Pitfall 5):** NO `[JsonPropertyName]` — this is a bus envelope, not a Redis
projection (verified `EntryStepDispatch.cs:6-8` comment). Unlike `StepProjection`, contracts on the
bus serialize with default STJ. Keep `ExecutionId`/`EntryId` real (copied from result), NOT
forced `Guid.Empty`.

---

### `src/Messaging.Contracts/StepOutcome.cs` (NEW — model enum, transform)

**Analog:** `src/BaseApi.Service/Features/Step/StepEntryCondition.cs` (the int values to MIRROR — do
NOT reference this file from Contracts/Orchestrator).

**Enum-with-explicit-int-values pattern** (`StepEntryCondition.cs:14-22`):
```csharp
public enum StepEntryCondition
{
    PreviousProcessing = 0,
    PreviousCompleted = 1,
    PreviousFailed = 2,
    PreviousCancelled = 3,
    Always = 4,
    Never = 5,
}
```

**Copy:** the `Previous*` int subset (0–3) AS the `StepOutcome` values:
```csharp
public enum StepOutcome
{
    Processing = 0,   // == StepEntryCondition.PreviousProcessing
    Completed  = 1,   // == PreviousCompleted
    Failed     = 2,   // == PreviousFailed
    Cancelled  = 3,   // == PreviousCancelled
}
```
**Critical (RESEARCH Pitfall 5):** plain `enum : int` with explicit values — NO
`JsonStringEnumConverter` (none registered anywhere). The match casts `(int)outcome` against
`StepProjection.EntryCondition` (already an `int`). `Always=4`/`Never=5` are NOT on this enum — they
live as orchestrator-side int constants on `StepAdvancement` (D-02).

---

### `src/Messaging.Contracts/OrchestratorQueues.cs` (NEW — config constant)

**Analog:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (static-class single-source-of-truth)

**Static-class-of-name-constants pattern** (`L2ProjectionKeys.cs:26-37`):
```csharp
public static class L2ProjectionKeys
{
    public const string Prefix = "skp:";
    public static string ParentIndex() => Prefix;
    public static string Root(Guid workflowId) => $"{Prefix}{workflowId:D}";
    public static string Step(Guid workflowId, Guid stepId) => $"{Prefix}{workflowId}:{stepId}";
    public static string Processor(Guid processorId) => $"{Prefix}{processorId}";
}
```

**Copy:** the `public static class` + `public const string` shape, one source of truth shared by
processor (future) + orchestrator (D-03).
```csharp
public static class OrchestratorQueues
{
    public const string Result = "orchestrator-result";   // queue:orchestrator-result
}
```
**Note (RESEARCH Pattern 1 / Architecture A3):** the `queue:` URI scheme maps to the bare endpoint
short-name — a `Send` to `queue:orchestrator-result` lands on `ReceiveEndpoint("orchestrator-result")`
(verified by `CapturingDispatchConsumer` using `ReceiveEndpoint($"{processorId:D}")` for
`queue:{processorId:D}`). Define the const WITHOUT the `queue:` prefix; the `Send` side adds it.

---

### `src/Orchestrator/Dispatch/IStepDispatcher.cs` + `StepDispatcher.cs` (NEW — service, request-response)

**Analog:** `src/Orchestrator/Scheduling/WorkflowFireJob.cs` lines 52–73 (the exact block to extract, D-01)

**Build-and-Send pattern** (`WorkflowFireJob.cs:66-73`):
```csharp
var msg = new EntryStepDispatch(workflowId, entryStepId, step.ProcessorId, step.Payload)
{
    CorrelationId = correlationId, // entry condition is irrelevant for entry steps
};

// D-10: Send (NOT Publish) to the per-processor queue. An infra fault here propagates.
var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{step.ProcessorId:D}"));
await endpoint.Send(msg, context.CancellationToken);
```

**DI / primary-ctor convention** (`WorkflowFireJob.cs:29-34`) — `ISendEndpointProvider` is the
injected dependency:
```csharp
public sealed class WorkflowFireJob(
    IWorkflowL1Store store,
    ISendEndpointProvider sendProvider,
    ...
```

**Target shape** (D-01 / RESEARCH Pattern 2 — `executionId`/`entryId` parameterized, NOT forced empty):
```csharp
public sealed class StepDispatcher(ISendEndpointProvider sendProvider) : IStepDispatcher
{
    public async Task DispatchAsync(Guid workflowId, Guid stepId, Guid processorId, string payload,
        Guid correlationId, Guid executionId, Guid entryId, CancellationToken ct)
    {
        var msg = new EntryStepDispatch(workflowId, stepId, processorId, payload)
        { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId };
        var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{processorId:D}"));
        await endpoint.Send(msg, ct);
    }
}
```
**Refactor `WorkflowFireJob`:** replace lines 66–73 with a `dispatcher.DispatchAsync(workflowId,
entryStepId, step.ProcessorId, step.Payload, correlationId, Guid.Empty, Guid.Empty, ct)` call;
inject `IStepDispatcher` instead of `ISendEndpointProvider`. Register `IStepDispatcher` as a
singleton in `Program.cs` (alongside line 39–41 singletons). Folder: `Orchestrator/Dispatch/`
(Claude's discretion per D-01).

---

### `src/Orchestrator/.../StepAdvancement.cs` (NEW — pure utility, transform)

**Analog:** none exists in-repo (see No Analog section). Shape from D-02 / RESEARCH Code Examples;
test analog is the table-driven pure-helper test `CronIntervalTests.cs`.

**Read surface** — `IWorkflowL1Store.TryGet` → `WorkflowL1.Steps` (a
`IReadOnlyDictionary<Guid, StepProjection>`) and `StepProjection.NextStepIds`/`.EntryCondition`
(`StepProjection.cs:16-20`):
```csharp
public sealed record StepProjection(
    [property: JsonPropertyName("entryCondition")] int EntryCondition,
    [property: JsonPropertyName("processorId")]    Guid ProcessorId,
    [property: JsonPropertyName("payload")]        string Payload,
    [property: JsonPropertyName("nextStepIds")]    List<Guid> NextStepIds);
```

**Target pure-match (D-02):** `Always=4`/`Never=5` are PRIVATE int consts here — orchestrator does
NOT reference `BaseApi.Service.StepEntryCondition`:
```csharp
public sealed class StepAdvancement
{
    private const int Always = 4;   // private const int Never = 5; (never matches the predicate)
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
**Pure / no I/O:** no Redis, no L1-store reference — takes the step map as an argument so it is
unit-testable without a harness (mirror the `CronInterval` static-helper testability). Match rule is
SPEC req 3 / D-02 verbatim. Register as singleton in `Program.cs`.

---

### `src/Orchestrator/Consumers/ResultConsumer.cs` (NEW — controller/consumer, event-driven)

**Analog:** `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` (primary-ctor consumer +
gate check + ack-split) and `WorkflowLifecycle.IsInfra`/`IsBusiness`.

**Primary-ctor consumer + gate-check pattern** (`StartOrchestrationConsumer.cs:24-37`):
```csharp
public sealed class StartOrchestrationConsumer(
    IStartupGate gate,
    IWorkflowL1Store store,
    WorkflowLifecycle lifecycle,
    ILogger<StartOrchestrationConsumer> logger) : IConsumer<StartOrchestration>
{
    public async Task Consume(ConsumeContext<StartOrchestration> context)
    {
        if (!gate.IsReady)
        {
            // D-12: gate closed ... ACK + drop, NEVER throw (Pitfall 6).
            logger.LogInformation("Gate closed — dropping Start (ack)");
            return;
        }
```

**L1 read surface** — `IWorkflowL1Store.TryGet` (`IWorkflowL1Store.cs:14`) returns `WorkflowL1` whose
`.Steps` is the `stepId → StepProjection` map (`WorkflowL1.cs:17-21`). Use `TryGet` only — NO `Upsert`,
NO `Remove`, NO stripe (`TryAcquire`) on the result path (D-08).

**Target (RESEARCH Code Examples — INVERT the gate-closed ack to a THROW per D-06):**
```csharp
public sealed class ResultConsumer(
    IStartupGate gate, IWorkflowL1Store store, StepAdvancement advancement,
    IStepDispatcher dispatcher, ILogger<ResultConsumer> logger) : IConsumer<ExecutionResult>
{
    public async Task Consume(ConsumeContext<ExecutionResult> context)
    {
        if (!gate.IsReady)
            throw new GateClosedException();   // D-06: THROW → scheduled redelivery (NOT ack-return)

        var m = context.Message;
        if (!store.TryGet(m.WorkflowId, out var wf) || !wf.Steps.TryGetValue(m.StepId, out var completed))
            return;   // business ack — unknown (wf,step) / drained (D-08, SPEC req 5)

        foreach (var (stepId, step) in advancement.SelectNext(m.Outcome, completed, wf.Steps))
            await dispatcher.DispatchAsync(
                m.WorkflowId, stepId, step.ProcessorId, step.Payload,
                m.CorrelationId, m.ExecutionId, m.EntryId, context.CancellationToken);
        // infra fault from Send propagates → Immediate(3) → _error
    }
}
```
**Key inversions from the Start analog:** (1) gate-closed = THROW, not `return` (D-06 supersedes
Phase 23 Pitfall-6 ack-drop); (2) NO `TryAcquire`/`Release` stripe (D-08 — read-only path);
(3) NO `WorkflowLifecycle` call (no L2 read on result path — SPEC constraint).

---

### `src/Orchestrator/Consumers/ResultConsumerDefinition.cs` (NEW — config endpoint def)

**Analog:** `src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs` (entire file)

**ConsumerDefinition + retry pattern** (`StartOrchestrationConsumerDefinition.cs:12-27`):
```csharp
public sealed class StartOrchestrationConsumerDefinition : ConsumerDefinition<StartOrchestrationConsumer>
{
    public StartOrchestrationConsumerDefinition() => EndpointName = "orchestrator";   // SHARED base name

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<StartOrchestrationConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r =>
        {
            r.Immediate(3);
            r.Ignore<WorkflowRootNotFoundException>();
        });
    }
}
```

**Target differences (D-03 / D-06 / RESEARCH Pattern 1 + Pitfall 2):**
- `EndpointName = OrchestratorQueues.Result` (`"orchestrator-result"`) — a STABLE shared name,
  NOT `"orchestrator"`, and the Program.cs registration must NOT set `InstanceId`/`Temporary`
  (competing-consumer, NOT fan-out — the inverse of Start/Stop).
- **Middleware ORDER is load-bearing (Pitfall 2 / GitHub #1575):** `UseScheduledRedelivery` FIRST
  (outer), then `UseMessageRetry` (inner):
  ```csharp
  endpointConfigurator.UseScheduledRedelivery(r =>
      r.Intervals(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
                  TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));   // Claude's-discretion values
  endpointConfigurator.UseMessageRetry(r => r.Immediate(3));
  ```
- The same redelivery policy must ALSO be added to the Start + Stop definitions (D-06 applies to all
  three). Apply via each `ConsumerDefinition.ConfigureConsumer` (the existing seam) or a shared
  `AddConfigureEndpointsCallback`.

---

### `src/Orchestrator/Consumers/GateClosedException.cs` (NEW — model exception, optional)

**Analog:** `src/Orchestrator/Consumers/WorkflowRootNotFoundException.cs` (entire file)

**Sealed-Exception-subclass pattern** (`WorkflowRootNotFoundException.cs:8-13`):
```csharp
public sealed class WorkflowRootNotFoundException(Guid workflowId)
    : Exception($"Workflow root {workflowId:D} absent from L2.")
{
    public Guid WorkflowId { get; } = workflowId;
}
```
**Copy:** the `sealed class … : Exception(message)` shape. The gate-closed throw may use a dedicated
`GateClosedException` (must NOT be `Ignore<>`d — unlike `WorkflowRootNotFoundException`, it MUST flow
to redelivery, so do NOT add it to the `r.Ignore<>` list).

---

## Modified-file pattern notes

### `src/Orchestrator/Hydration/WorkflowLifecycle.cs` (split teardown, D-07)

**Current teardown** (`WorkflowLifecycle.cs:114-124`) does BOTH unschedule + L1 remove:
```csharp
public async Task TeardownAsync(Guid workflowId, CancellationToken ct)
{
    if (!store.TryGet(workflowId, out var wf)) return;   // BUSINESS no-op
    await scheduler.UnscheduleAsync(wf.JobId, ct);        // jobId-addressed DeleteJob
    store.Remove(workflowId);                             // NO L2 mutation
}
```
**Change (D-07):** split into `UnscheduleOnlyAsync` (unschedule, do NOT call `store.Remove`) used by
Stop, vs. keep the full `TeardownAsync` (unschedule + remove) for Start's reload pre-clean
(RESEARCH Pattern 3 + Pitfall 4 — Start must still unschedule the old Quartz job before re-scheduling).
`store.Remove` (`IWorkflowL1Store.cs:17`) is the call Stop must AVOID.

### `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` / `StopOrchestrationConsumer.cs` (conditionless + gate-throw)

**Strip from Start** (`StartOrchestrationConsumer.cs:41-56`): the `TryAcquire` stripe + stripe-held
`continue` + `finally Release` (D-05). Keep `TeardownAsync` + `HydrateAndScheduleAsync`.
**Strip from Stop** (`StopOrchestrationConsumer.cs:38-52`): the stripe; switch `TeardownAsync` →
`UnscheduleOnlyAsync` (D-07 keep-L1).
**Both:** invert the gate-closed `return` (Start `:32-37`, Stop `:29-34`) to a THROW (D-06) — the same
`GateClosedException` the result consumer throws. The `WorkflowLifecycle.IsInfra`/`IsBusiness` split
(`WorkflowLifecycle.cs:130-137`) is unchanged.

### `src/Orchestrator/Program.cs` (endpoint + scheduler wiring)

**Current endpoint registration** (`Program.cs:28-34`) — the fan-out pattern to NOT copy for the
result endpoint:
```csharp
builder.Services.AddBaseConsoleMessaging(builder.Configuration, x =>
{
    x.AddConsumer<StartOrchestrationConsumer, StartOrchestrationConsumerDefinition>()
        .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
    x.AddConsumer<StopOrchestrationConsumer, StopOrchestrationConsumerDefinition>()
        .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
});
```
**Add:** `x.AddConsumer<ResultConsumer, ResultConsumerDefinition>()` with NO `.Endpoint(InstanceId/
Temporary)` (competing-consumer); `x.AddInMemoryMessageScheduler()` inside this `configureConsumers`
lambda (operates on `IBusRegistrationConfigurator`, RESEARCH Pitfall 3); register
`IStepDispatcher`/`StepDispatcher` + `StepAdvancement` as singletons (alongside lines 39–41). The
bus-factory `UseInMemoryScheduler()` half needs the base-library seam extension (next file).

### `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` (seam for scheduler)

**Current signature** (`MessagingServiceCollectionExtensions.cs:25-46`) exposes ONLY
`Action<IBusRegistrationConfigurator> configureConsumers` — NOT the `UsingRabbitMq((ctx,c)=>...)`
bus-factory lambda where `c.UseInMemoryScheduler()` must go (RESEARCH Pitfall 3 / A2):
```csharp
public static IServiceCollection AddBaseConsoleMessaging(
    this IServiceCollection services, IConfiguration cfg,
    Action<IBusRegistrationConfigurator> configureConsumers)
{
    ...
    services.AddMassTransit(x =>
    {
        configureConsumers(x);
        x.UsingRabbitMq((ctx, c) =>
        {
            c.Host(rabbitHost, h => { h.Username(rabbitUser); h.Password(rabbitPass); });
            c.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), ctx);
            c.UseSendFilter(typeof(OutboundCorrelationSendFilter<>), ctx);
            c.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx);
            c.ConfigureEndpoints(ctx);
        });
    });
    return services;
}
```
**Change (planner decision, RESEARCH Pitfall 3 / Open Q3):** add an OPTIONAL
`Action<IRabbitMqBusFactoryConfigurator>? configureBus = null` parameter, invoked inside the
`UsingRabbitMq` lambda BEFORE `c.ConfigureEndpoints(ctx)`, so the orchestrator can wire
`c.UseInMemoryScheduler()`. Keep the dependency firewall intact (base = infra). VERIFY against
MassTransit 8.5.5 whether `AddInMemoryMessageScheduler()` alone suffices (then this change may be
unnecessary — A2).

### `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` (WebApi first-win, WEBAPI-SUPPRESS-01)

**Current Start publish** (`OrchestrationService.cs:174-185`) unconditionally writes-then-publishes;
**current Stop gate** (`OrchestrationService.cs:214-235`) 422s when ANY root is missing (non-idempotent).
The root write itself is `RedisProjectionWriter.UpsertAsync` (`RedisProjectionWriter.cs:80`):
```csharp
tasks.Add(batch.StringSetAsync(RedisProjectionKeys.Root(wf.Id), rootJson));   // no When → overwrite
```
**Change (RESEARCH Pitfall 6 + "WebApi first-win create" Code Example):**
- **Start:** make the root SET first-win via `When.NotExists`; skip the publish for ids that were
  already present:
  ```csharp
  var created = await db.StringSetAsync(RedisProjectionKeys.Root(wf.Id), rootJson, when: When.NotExists);
  if (!created) { /* first-win skip — do NOT publish StartOrchestration for this id */ }
  ```
  Planner must reconcile this against the existing delete-then-write pre-clean (ORCH-START-05 shrunk-
  graph GC, `OrchestrationService.cs:125-133`) — design fork A4: first-win GUARDS the whole write vs.
  guards only the root SET.
- **Stop:** replace the 422 missing-roots gate (`:214-235`) with delete-if-present using the
  `KeyDeleteAsync` bool return; a second Stop of an absent root is a no-op (not 422). Gate the
  `Publish(StopOrchestration…)` (`:260-262`) on at-least-one-deleted.
- **Key shape unchanged** — `RedisProjectionKeys.Root` forwards to `L2ProjectionKeys.Root`
  (`RedisProjectionKeys.cs:15`).
- **Reconcile facts:** `StartOrchestrationFacts`, `StopOrchestrationFacts`, `StopScanFacts`,
  `OrchestrationServicePublishTests` assert the current 422/overwrite behavior (RESEARCH Wave 0 Gaps).

---

## Shared Patterns

### Gate-closed THROW (D-06 — applies to Start, Stop, Result consumers)
**Source:** invert `StartOrchestrationConsumer.cs:32-37` (the `return`) to a throw; exception shape
from `WorkflowRootNotFoundException.cs:8-13`.
**Apply to:** `ResultConsumer`, `StartOrchestrationConsumer`, `StopOrchestrationConsumer`.
**Critical:** the gate-closed exception MUST NOT be in any `r.Ignore<>` list (unlike
`WorkflowRootNotFoundException`) — it must reach the redelivery middleware.

### Business-ack vs infra-throw split (SPEC req 5 / ORCH-ACK-01)
**Source:** `WorkflowLifecycle.IsInfra`/`IsBusiness` (`WorkflowLifecycle.cs:130-137`):
```csharp
public static bool IsInfra(Exception ex) =>
    ex is RedisConnectionException or RedisTimeoutException or RedisException;
public static bool IsBusiness(Exception ex) => !IsInfra(ex);
```
**Apply to:** `ResultConsumer` — unknown `(wf,step)`, no-match, corrupt-projection = `return` (ack);
infra (Send/broker) fault propagates to `Immediate(3)` → `_error`. On the result path there is NO
Redis read, so "infra" is effectively a broker `Send` fault.

### Send (NOT Publish) to `queue:{processorId:D}`
**Source:** `WorkflowFireJob.cs:71-73`. **Apply to:** `StepDispatcher` (the single owner after D-01
extraction). Convention: `GetSendEndpoint(new Uri($"queue:{processorId:D}"))` then `Send`.

### Bounded retry → `_error` via ConsumerDefinition
**Source:** `StartOrchestrationConsumerDefinition.cs:21-25` (`UseMessageRetry(r => r.Immediate(3))`).
**Apply to:** `ResultConsumerDefinition` (plus `UseScheduledRedelivery` outer — Pitfall 2 order).

### Static-class single-source-of-truth name constant
**Source:** `L2ProjectionKeys.cs:26-37`. **Apply to:** `OrchestratorQueues` (the
`orchestrator-result` name shared by processor + orchestrator, D-03).

---

## Test Patterns

### In-memory harness + short-name capture (ResultConsumeTests, ContinuationDispatch)
**Source:** `FireDispatchTests.cs:53-73` (`AddMassTransitTestHarness` + per-processorId
`ReceiveEndpoint($"{processorId:D}")`) and `CapturingDispatchConsumer.cs` (no-op
`IConsumer<EntryStepDispatch>`; the harness `Consumed` filter captures). REUSE both verbatim —
seed L1 via `WorkflowL1Store` + `SeedEntry` (`FireDispatchTests.cs:75-97`), drive the result through
`ResultConsumer`, assert captured `EntryStepDispatch` field-copy (SPEC req 4 acceptance).
Field assertions pattern: `FireDispatchTests.cs:152-164`.

### Redis-mux stub + ConsumeContext fake (ResultAckTests)
**Source:** `OrchestratorTestStubs.cs` — `AbsentL2`/`PresentL2`/`InfraFaultL2`/`Context<T>`
(`:42-105`), and `AckSemanticsTests.cs:42-54` `Build(...)` helper (constructs the consumer with a
`StartupGate().MarkReady()`). For the result path, the consumer reads L1 only, so a `WorkflowL1Store`
seeded directly is enough; the mux stub asserts NO L2 read (`db.DidNotReceive().StringGetAsync(...)`,
pattern at `FireDispatchTests.cs:276-279`). Ack-no-throw assertion pattern: `AckSemanticsTests.cs:71-73`;
infra-propagates pattern: `AckSemanticsTests.cs:121-122` (`Assert.ThrowsAsync<...>`).

### Gate-closed redeliver (GateClosedRedeliverTests)
**Source:** gate construction from `AckSemanticsTests.cs:45-46` (`new StartupGate()` left CLOSED, then
`MarkReady()` to open). Assert the gate-closed `Consume` THROWS (not acks), then after `MarkReady`
re-`Consume` succeeds and dispatches. Harness-level redelivery survival uses the `FireDispatchTests`
harness shape.

### Pure table-driven test (StepAdvancementTests)
**Source:** `tests/.../CronIntervalTests.cs` (pure-helper, no harness — instantiate `StepAdvancement`
directly, feed a `StepProjection` map, assert selected ids per the full outcome×entry-condition
matrix incl. `Never` never selected, SPEC req 3 acceptance).

### Contract round-trip (ExecutionResultContractTests)
**Source:** `tests/.../EntryStepDispatchTests.cs` — serialize→deserialize an `ExecutionResult`,
assert all fields preserved, `IExecutionCorrelated` implemented, no output/payload field, `StepOutcome`
ints 0–3 (SPEC req 1 acceptance).

---

## No Analog Found

| File | Role | Data Flow | Reason / Guidance |
|------|------|-----------|-------------------|
| `src/Orchestrator/.../StepAdvancement.cs` | utility (pure) | transform | No existing pure DAG-traversal/match helper in Orchestrator. Use D-02 / RESEARCH Code Example shape (above); test like the pure `CronInterval` helper. Folder: `Orchestrator/Dispatch/` or a new `Orchestrator/Advancement/` — planner's call (no folder convention to violate). |
| `UseInMemoryScheduler()` wiring | config | — | No precedent in the repo for a bus-factory `UseXxx` through the base seam (RESEARCH Pitfall 3 / A2). The base library deliberately hides `UsingRabbitMq`. Planner must extend the seam OR confirm `AddInMemoryMessageScheduler()` self-wires in MassTransit 8.5.5. |

---

## Metadata

**Analog search scope:** `src/Messaging.Contracts/`, `src/Orchestrator/` (Consumers, Scheduling,
Hydration, L1, Dispatch, Messaging), `src/BaseApi.Service/Features/Orchestration/` (+ Projection,
Step), `src/BaseConsole.Core/` (DependencyInjection, Health), `tests/BaseApi.Tests/Orchestrator/`.
**Files read (verified):** EntryStepDispatch, IExecutionCorrelated, ICorrelated, StepProjection,
L2ProjectionKeys, StepEntryCondition, WorkflowFireJob, StartOrchestrationConsumer,
StopOrchestrationConsumer, Start/StopOrchestrationConsumerDefinition, WorkflowRootNotFoundException,
WorkflowLifecycle, IWorkflowL1Store, WorkflowL1, Program.cs, MessagingServiceCollectionExtensions,
StartOrchestration, StopOrchestration, OrchestrationService, RedisProjectionWriter,
RedisProjectionKeys, OrchestratorL2Keys, WorkflowScheduler, IStartupGate, CapturingDispatchConsumer,
FireDispatchTests, OrchestratorTestStubs, AckSemanticsTests.
**Pattern extraction date:** 2026-05-31
