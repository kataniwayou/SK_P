# Phase 23: Orchestrator Lifecycle — L1 Hydration, Quartz Scheduling, Entry-Step Dispatch & Stop Teardown - Pattern Map

**Mapped:** 2026-05-31
**Files analyzed:** 15 (3 NEW contracts, 9 NEW orchestrator runtime, 2 EDIT consumers, 1 EDIT keys, 1 EDIT Program.cs) + 6 NEW/EXTEND test files
**Analogs found:** 14 / 15 (every NEW file has an in-repo analog; only the Quartz job/scheduler glue has no local analog — external Quartz API, covered by RESEARCH Patterns 1/2)

> All analogs below were verified present with `git ls-files` and read this session. The one genuinely-new dependency (Quartz) has no in-repo analog; for `WorkflowFireJob`/`WorkflowScheduler`/`CronInterval` the planner should use RESEARCH.md Pattern 1/2 + Code Examples directly (cited there against official docs), and copy the *DI/injection* shape from the consumer analogs below.

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Messaging.Contracts/Projections/StepProjection.cs` (NEW) | contract record (projection reader) | transform (Redis JSON → record) | `src/BaseApi.Service/Features/Orchestration/Projection/StepProjection.cs` (writer) + `Messaging.Contracts/Projections/WorkflowRootProjection.cs` | exact (hoist + enum→int) |
| `src/Messaging.Contracts/IExecutionCorrelated.cs` (NEW) | interface (segregation) | n/a | `src/Messaging.Contracts/ICorrelated.cs` | exact (derive) |
| `src/Messaging.Contracts/EntryStepDispatch.cs` (NEW) | contract record (bus message) | request-response (outbound Send) | `src/Messaging.Contracts/StopOrchestration.cs` (ICorrelated record w/ init CorrelationId) | role-match |
| `src/Orchestrator/L1/IWorkflowL1Store.cs` + `WorkflowL1Store.cs` + `WorkflowL1.cs` (NEW) | singleton store + per-key stripe | event-driven (in-memory state) | `src/BaseConsole.Core/Health/IStartupGate.cs`/`StartupGate.cs` (singleton thread-safe latch w/ Interlocked/Volatile) + RESEARCH "Lock striping" | role-match (concurrency primitive) |
| `src/Orchestrator/Scheduling/WorkflowFireJob.cs` (NEW) | Quartz job (IJob) | request-response (fire → Send) | consumer ctor-injection shape (below); **dispatch math** = RESEARCH Pattern 1/2 | role-match (DI) / **no Quartz analog** |
| `src/Orchestrator/Scheduling/WorkflowScheduler.cs` (NEW) | scheduler wrapper | event-driven | **no in-repo Quartz analog** → RESEARCH Pattern 2 | none (external API) |
| `src/Orchestrator/Scheduling/CronInterval.cs` (NEW) | utility (cron math) | transform | `src/BaseApi.Service/Features/Workflow/WorkflowDtoValidator.cs` (Cronos `CronFormat.Standard` parse) + RESEARCH Pattern 1 | role-match |
| `src/Orchestrator/Hydration/HydrationBackgroundService.cs` (NEW) | hosted service (BackgroundService) | batch (L2→L1 bulk read) | `StartOrchestrationConsumer.Consume` (Redis GET-and-deserialize + ack split) + `RedisL2Cleanup` (BFS GET-and-follow) + RESEARCH "Hydration BackgroundService" | role-match |
| `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` (EDIT) | consumer | request-response | itself (current seam) | exact (in-place graduate) |
| `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` (EDIT) | consumer | request-response | itself (current seam) | exact (in-place graduate) |
| `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` (EDIT) | forwarder | n/a | itself (`Root` forwarder) + `L2ProjectionKeys.ParentIndex()/Step()` | exact |
| `src/Orchestrator/Program.cs` (EDIT) | composition root | n/a | itself + RESEARCH "Quartz wiring" + `ConsoleStartupGateTests.NoStartupCompletionConsoleFixture` (remove-by-type) | exact |
| `tests/.../StepProjectionReaderTests.cs` (NEW) | test (unit round-trip) | transform | `StartStopConsumerAckTests.PresentL2` JSON build pattern | role-match |
| `tests/.../FireDispatchTests.cs` + synthetic consumer (NEW) | test (harness) | request-response | `OutboundFilterSyntheticTests` (`AddMassTransitTestHarness` + `Published.Select<T>`) + RESEARCH "Synthetic test consumer" | role-match |
| `tests/.../AckSemanticsTests.cs` (EXTEND) | test (harness) | request-response | `StartStopConsumerAckTests` (Absent/Present/InfraFault stubs, `DidNotReceive().StringSetAsync`) | exact (extend file) |

---

## Pattern Assignments

### `src/Messaging.Contracts/Projections/StepProjection.cs` (NEW — hoisted reader record)

**read_first:** `src/BaseApi.Service/Features/Orchestration/Projection/StepProjection.cs` (writer), `src/Messaging.Contracts/Projections/WorkflowRootProjection.cs`, `src/Messaging.Contracts/Projections/LivenessProjection.cs`

**Analog (writer, to mirror) — `BaseApi.Service/.../Projection/StepProjection.cs:13-17`:**
```csharp
internal sealed record StepProjection(
    [property: JsonPropertyName("entryCondition")] StepEntryCondition EntryCondition,   // ENUM in writer
    [property: JsonPropertyName("processorId")]    Guid ProcessorId,
    [property: JsonPropertyName("payload")]        string Payload,
    [property: JsonPropertyName("nextStepIds")]    List<Guid> NextStepIds);
```

**Action:** Create a NEW `public sealed record` in `namespace Messaging.Contracts.Projections` that is byte-identical on the wire but uses `int EntryCondition` (NOT the writer's `StepEntryCondition` enum — the enum serializes as its underlying int; see Pitfall 7 below). The writer stays as-is and is NOT refactored to consume this record (out of scope). Mirror the exact `[property: JsonPropertyName("...")]` positional-record convention from `WorkflowRootProjection.cs` (the `[property:]` target is load-bearing — Pitfall 1). Final shape is in RESEARCH "Hoisted reader StepProjection". `using System.Text.Json.Serialization;`.

---

### `src/Messaging.Contracts/IExecutionCorrelated.cs` + `EntryStepDispatch.cs` (NEW)

**read_first:** `src/Messaging.Contracts/ICorrelated.cs`, `src/Messaging.Contracts/StopOrchestration.cs`

**Base interface analog — `ICorrelated.cs:4-7`:**
```csharp
public interface ICorrelated
{
    Guid CorrelationId { get; }
}
```

**Message-record analog (ICorrelated impl with init CorrelationId) — `StopOrchestration.cs:4-7`:**
```csharp
public sealed record StopOrchestration(Guid[] WorkflowIds) : ICorrelated
{
    public Guid CorrelationId { get; init; }
}
```

**Action:** `IExecutionCorrelated : ICorrelated` adds `{ ExecutionId, WorkflowId, StepId, ProcessorId, EntryId }` (all `Guid`, get-only) in `namespace Messaging.Contracts`. `EntryStepDispatch` is a `public sealed record` implementing `IExecutionCorrelated`; positional ctor carries the live fields (`WorkflowId, StepId, ProcessorId, Payload`), with `CorrelationId`/`ExecutionId`/`EntryId` as `{ get; init; }` (`ExecutionId`/`EntryId` defaulting to `Guid.Empty` per SPEC). **No `[JsonPropertyName]`** on the message record (MassTransit serializes the envelope, not a Redis JSON projection — only projections need camelCase targets). Final shape: RESEARCH "IExecutionCorrelated + dispatch message".

---

### `src/Orchestrator/L1/{IWorkflowL1Store,WorkflowL1Store,WorkflowL1}.cs` (NEW)

**read_first:** `src/BaseConsole.Core/Health/IStartupGate.cs` (for the thread-safe singleton latch idiom), RESEARCH "Lock striping" + Pitfall 5

**Thread-safe-singleton analog — `IStartupGate.cs:36-45` (`StartupGate`):**
```csharp
public sealed class StartupGate : IStartupGate
{
    private int _isReady;
    public bool IsReady => Volatile.Read(ref _isReady) == 1;
    public void MarkReady() => Interlocked.Exchange(ref _isReady, 1);
}
```

**Stripe pattern (from RESEARCH, no local analog):**
```csharp
private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _stripes = new();
public bool TryAcquire(Guid wfId)
    => _stripes.GetOrAdd(wfId, _ => new SemaphoreSlim(1, 1)).Wait(0);   // drop-if-held: false = held
public void Release(Guid wfId) { if (_stripes.TryGetValue(wfId, out var s)) s.Release(); }
```

**Action:** `IWorkflowL1Store` is a singleton (registered `AddSingleton<IWorkflowL1Store, WorkflowL1Store>()`). Back the store with `ConcurrentDictionary<Guid, WorkflowL1>` keyed by workflowId. Expose `TryGet/Upsert/Remove` + the per-wf stripe `TryAcquire/Release` (Open Question 3 → stripe lives ON the store). `WorkflowL1` is a record `{ List<Guid> EntryStepIds, string Cron, Guid JobId, LivenessProjection Liveness, IReadOnlyDictionary<Guid,StepProjection> Steps }` (reuse the hoisted `Messaging.Contracts.Projections.StepProjection` + `LivenessProjection` — do NOT redefine). Use `Wait(0)`/`WaitAsync(0)` (D-14 drop-if-held — never blocking `Wait()`). Couple semaphore disposal to L1-entry removal OR never-dispose (Pitfall 5). L1 holds NO processor key and NOT the parent-index key (D-06).

---

### `src/Orchestrator/Hydration/HydrationBackgroundService.cs` (NEW)

**read_first:** `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` (Redis GET-and-deserialize + ack split), `src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs` (BFS GET-and-follow + `SetMembersAsync`/SET handling), `src/BaseConsole.Core/Health/IStartupGate.cs`, RESEARCH "Hydration BackgroundService"

**Soft-dep Redis read analog — `StartOrchestrationConsumer.cs:29-42`:**
```csharp
var db = redis.GetDatabase();   // infra fault here THROWS → retry → _error (D-08 / MSG-ACK-02)
var raw = await db.StringGetAsync(OrchestratorL2Keys.Root(workflowId));
if (raw.IsNullOrEmpty) { logger.LogWarning("Workflow {WorkflowId} absent from L2 — business failure, acking", workflowId); continue; }
_ = JsonSerializer.Deserialize<WorkflowRootProjection>(raw!);
```

**Parent-index SET read analog — `RedisL2Cleanup.cs:45` (writer SREMs; reader SMEMBERS):**
```csharp
await db.SetRemoveAsync(RedisProjectionKeys.ParentIndex(), workflowId.ToString("D"));   // SET is real; reader uses SetMembersAsync
```

**Action:** `BackgroundService.ExecuteAsync` against the singleton `IConnectionMultiplexer` (soft-dep, `abortConnect=false`). `SetMembersAsync(OrchestratorL2Keys.ParentIndex())` → per-wf `StringGetAsync(Root)` + per-step `StringGetAsync(Step)`, deserialize via `JsonSerializer.Deserialize<WorkflowRootProjection>` / `<StepProjection>`, populate L1, schedule the Quartz job, then **`gate.MarkReady()`** (the gate flips HERE — D-12, not on bare host start). Corrupt entry → skip (business, ORCH-ACK-01). Redis unreachable → bounded-backoff retry loop, gate NEVER opens (D-13). Mirror the GET-and-follow termination idea from `RedisL2Cleanup` but **read into L1, never delete, never write L2**.

---

### `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` (EDIT) + `StopOrchestrationConsumer.cs` (EDIT)

**read_first:** both consumer files (current seam), `StartOrchestrationConsumerDefinition.cs`, `WorkflowRootNotFoundException.cs`, RESEARCH "Consumer gate-drop" / "Stop teardown"

**Current ack-split seam (the shape to graduate) — `StartOrchestrationConsumer.cs:25-48`:**
```csharp
public async Task Consume(ConsumeContext<StartOrchestration> context)
{
    var db = redis.GetDatabase();   // infra fault here THROWS → retry → _error
    foreach (var workflowId in context.Message.WorkflowIds)
    {
        var raw = await db.StringGetAsync(OrchestratorL2Keys.Root(workflowId));
        if (raw.IsNullOrEmpty) { logger.LogWarning("... absent from L2 — business failure, acking", workflowId); continue; }
        _ = JsonSerializer.Deserialize<WorkflowRootProjection>(raw!);
        logger.LogInformation("Scheduler job start (seam) for {WorkflowId}", workflowId);   // ← seam to replace
    }
}
```

**Action (Start):** Inject `IStartupGate` + `IWorkflowL1Store` + the hydration/scheduling unit. At top: `if (!_gate.IsReady) { log; return; }` (D-12 — clean ACK, NEVER throw — Pitfall 6). Per wfId: `if (!_store.TryAcquire(wfId)) { log; continue; }` (D-14 drop-if-held), then `try { Teardown(wfId); HydrateAndSchedule(wfId); } finally { _store.Release(wfId); }` (D-15 — reuses Stop's teardown for DRY). Keep the existing ack split verbatim: absent root → `WorkflowRootNotFoundException`-style business path (logged + continue), infra fault propagates (retry → `_error`). **Action (Stop):** same gate + stripe preamble; teardown only (`DeleteJob(JobKey(jobId))` + `_store.Remove(wfId)`); absent → business no-op (D-16). Zero L2 writes in either consumer.

**Definition stays as-is — `StartOrchestrationConsumerDefinition.cs:21-25` (already correct for ORCH-ACK-01):**
```csharp
endpointConfigurator.UseMessageRetry(r => { r.Immediate(3); r.Ignore<WorkflowRootNotFoundException>(); });
```

---

### `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` (EDIT)

**read_first:** `src/Orchestrator/Messaging/OrchestratorL2Keys.cs`, `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs`

**Existing forwarder — `OrchestratorL2Keys.cs:12-15`:**
```csharp
internal static class OrchestratorL2Keys
{
    public static string Root(Guid workflowId) => L2ProjectionKeys.Root(workflowId);
}
```

**Source forwarders to add — `L2ProjectionKeys.cs:30,34`:**
```csharp
public static string ParentIndex() => Prefix;                                        // skp: SET key
public static string Step(Guid workflowId, Guid stepId) => $"{Prefix}{workflowId}:{stepId}";
```

**Action:** Add two thin reader forwarders next to `Root`: `ParentIndex() => L2ProjectionKeys.ParentIndex();` and `Step(Guid workflowId, Guid stepId) => L2ProjectionKeys.Step(workflowId, stepId);` (D-03). Do NOT inline the key formats — forward to `L2ProjectionKeys` (single source of truth, HARDEN-03).

---

### `src/Orchestrator/Program.cs` (EDIT)

**read_first:** `src/Orchestrator/Program.cs`, `src/BaseConsole.Core/DependencyInjection/ConsoleHealthServiceCollectionExtensions.cs` (to know `StartupCompletionService` is registered inside `AddBaseConsoleHealth`), `tests/.../Console/ConsoleStartupGateTests.cs` (remove-by-type idiom), RESEARCH "Quartz wiring"

**Current composition — `Program.cs:12-32`:**
```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.AddBaseConsoleObservability(builder.Configuration);
builder.Services.AddBaseConsole(builder.Configuration);
var instanceId = builder.Configuration["Orchestrator:InstanceId"] ?? Guid.NewGuid().ToString("N");
builder.Services.AddBaseConsoleMessaging(builder.Configuration, x => { /* Start/Stop consumers, Temporary fan-out */ });
var host = builder.Build();
await host.RunAsync();
```

**Remove-by-type analog — `ConsoleStartupGateTests.cs:58-61`:**
```csharp
var toRemove = builder.Services.Where(d => d.ImplementationType == typeof(StartupCompletionService)).ToList();
foreach (var d in toRemove) builder.Services.Remove(d);
```

**Action:** After `AddBaseConsole`/`AddBaseConsoleMessaging`, add: `AddQuartz()` + `AddQuartzHostedService(o => o.WaitForJobsToComplete = true)`; `AddSingleton<IWorkflowL1Store, WorkflowL1Store>()`; `AddHostedService<HydrationBackgroundService>()`; the scheduler wrapper. Then **remove `BaseConsole.Core.Health.StartupCompletionService` by `ImplementationType`** (it's registered inside `AddBaseConsoleHealth` — `ConsoleHealthServiceCollectionExtensions.cs:40`) using the exact remove-by-type idiom above — so `MarkReady()` fires from hydration-complete, not bare host start (D-12). `IStartupGate` singleton + `StartupHealthCheck` (tagged `"startup","ready"`) and the `"self"`/`"live"` check stay untouched (`ConsoleHealthServiceCollectionExtensions.cs:33-36`).

---

### `tests/BaseApi.Tests/Orchestrator/*` (NEW + EXTEND)

**read_first:** `tests/.../Orchestrator/StartStopConsumerAckTests.cs`, `tests/.../Orchestrator/OutboundFilterSyntheticTests.cs`, `tests/.../Orchestrator/FanOutBroadcastTests.cs`, RESEARCH "Synthetic test consumer"

**Mux-stub helpers to reuse/copy — `StartStopConsumerAckTests.cs:50-84`:** `AbsentL2(out IDatabase db)` (StringGetAsync → `RedisValue.Null`), `PresentL2(out IDatabase db)` (StringGetAsync → serialized `WorkflowRootProjection`), `InfraFaultL2()` (StringGetAsync → `.Throws(new RedisConnectionException(...))`). For hydration tests, add a `SetMembersAsync`-returning variant of `PresentL2`.

**Zero-L2-write assertion to extend — `StartStopConsumerAckTests.cs:152-154`:**
```csharp
await db.DidNotReceive().StringSetAsync(
    Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
```
> Extend per RESEARCH §"Observable signals": also assert `db.DidNotReceive().SetAddAsync(...)` and `.KeyDeleteAsync(...)` on any `skp:` key for the orchestrator zero-L2-write invariant.

**Harness + synthetic-consumer analog — `OutboundFilterSyntheticTests.cs:33-58`:**
```csharp
await using var provider = new ServiceCollection()
    .AddSingleton<ICorrelationAccessor>(ambient)
    .AddMassTransitTestHarness(x => x.UsingInMemory((c, cfg) => { ...; cfg.ConfigureEndpoints(c); }))
    .BuildServiceProvider(true);
var harness = provider.GetRequiredService<ITestHarness>();
await harness.Start();
// ... publish ...
var pub = harness.Published.Select<StartOrchestration>(ct).Single();
```

**Action (FireDispatchTests):** Build an `AddMassTransitTestHarness` provider that registers a synthetic `CapturingDispatchConsumer` bound to `cfg.ReceiveEndpoint($"{processorId:D}", e => e.ConfigureConsumer<...>(ctx))` (the short-name `queue:{id}` ↔ endpoint-`{id}` mapping — RESEARCH "Synthetic test consumer" + assumption A2), drive a fire, then `Assert.True(await harness.Consumed.Any<EntryStepDispatch>(ct))` and `harness.Consumed.Select<EntryStepDispatch>(ct).ToList()` to assert one msg per entry step, `ExecutionId == Guid.Empty`, and two fires' `CorrelationId` differ. Use `FakeTimeProvider` for the cron clock (D-04). **Action (StepProjectionReaderTests):** mirror `PresentL2`'s `JsonSerializer.Serialize(...)` round-trip — serialize a writer-produced value (enum `StepEntryCondition` → int) and assert it deserializes into the reader `StepProjection` with `EntryCondition` as the matching int (Pitfall 7). **Action (AckSemanticsTests):** EXTEND `StartStopConsumerAckTests` (same file/namespace) for gate-drop, corrupt-entry-skip, and Redis-unreachable cases.

---

## Shared Patterns

### Ack split (business=log+ack / infra=throw→retry→_error) — ORCH-ACK-01
**Source:** `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs:29-42` + `StartOrchestrationConsumerDefinition.cs:21-25` + `WorkflowRootNotFoundException.cs`
**Apply to:** both EDITed consumers AND `HydrationBackgroundService` (corrupt entry skip)
```csharp
// consumer: business absent → log + continue (ack); infra → propagate (retry → _error)
if (raw.IsNullOrEmpty) { logger.LogWarning("... business failure, acking", workflowId); continue; }
// definition: UseMessageRetry(r => { r.Immediate(3); r.Ignore<WorkflowRootNotFoundException>(); });
```
> Gate-closed drop (D-12) and stripe-held drop (D-14) are ALSO clean `return`/`continue`, NEVER a throw (Pitfall 6).

### Body-carried correlation, fresh per stage, NewId minting — D-05
**Source:** `src/BaseConsole.Core/Messaging/OutboundCorrelationSendFilter.cs:15-20` (stamps envelope from ambient; body untouched) + `StopOrchestration.cs:4-7` (init CorrelationId)
**Apply to:** `EntryStepDispatch` construction in `WorkflowFireJob`
```csharp
// body is source of truth (D-01); per-fire: new EntryStepDispatch(...) { CorrelationId = NewId.NextGuid() }
// the bus-wide OutboundCorrelationSendFilter<T> stamps context.CorrelationId from the ambient accessor — envelope, not body
```

### Thread-safe singleton latch — IStartupGate (the ONE gate, D-12)
**Source:** `src/BaseConsole.Core/Health/IStartupGate.cs:36-45` (`Volatile.Read` / `Interlocked.Exchange`) + `StartupHealthCheck.cs:18-25` (tagged `"startup","ready"`)
**Apply to:** `HydrationBackgroundService` (calls `MarkReady()`), both consumers (`if (!_gate.IsReady) return;`). Do NOT build a second gate.

### `queue:{processorId:D}` Send addressing — D-10
**Source:** RESEARCH Pattern 3 (no in-repo `GetSendEndpoint` call site — `OutboundCorrelationSendFilter<T>` is the closest, handling `SendContext<T>`)
**Apply to:** `WorkflowFireJob`
```csharp
var endpoint = await _sendProvider.GetSendEndpoint(new Uri($"queue:{processorId:D}"));
await endpoint.Send(message, ct);   // Send, NOT Publish (D-10)
```

### Cronos `CronFormat.Standard` fire-time math — D-08/D-09
**Source:** `src/BaseApi.Service/Features/Workflow/WorkflowDtoValidator.cs` (VALID-19 parses the stored 5-field cron identically) + RESEARCH Pattern 1
**Apply to:** `CronInterval` + `WorkflowFireJob` reschedule. Always feed `TimeProvider.GetUtcNow().UtcDateTime` (Kind=Utc — Pitfall 3). NEVER feed the stored cron into a Quartz `CronTrigger`.

### `[property: JsonPropertyName]` positional-record camelCase — Pitfall 1
**Source:** `src/Messaging.Contracts/Projections/WorkflowRootProjection.cs:11-16`, `LivenessProjection.cs:11-14`
**Apply to:** the NEW hoisted `StepProjection` reader record ONLY (the bus message `EntryStepDispatch` needs NO `[JsonPropertyName]`).

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `src/Orchestrator/Scheduling/WorkflowScheduler.cs` | scheduler wrapper | event-driven | Quartz is a NEW dependency — no in-repo `IScheduler`/`ScheduleJob`/`DeleteJob` call site exists. Use RESEARCH Pattern 2 (cited against quartz-scheduler.net). Copy only the DI/ctor-injection shape from the consumer analogs. |
| `src/Orchestrator/Scheduling/WorkflowFireJob.cs` | Quartz IJob | request-response | `IJob.Execute` has no local analog; the *dispatch* half (`Send` + `NewId` + liveness refresh) and the *reschedule* half come from RESEARCH Patterns 1/2/3. DI ctor-injection (singleton L1 store + `ISendEndpointProvider` + `TimeProvider`) mirrors the existing consumers' primary-ctor injection. |

> No SemaphoreSlim-stripe call site exists in-repo either (the closest concurrency primitive is `StartupGate`'s `Interlocked`/`Volatile`); use RESEARCH "Lock striping" verbatim, with Pitfall 5 disposal coupling.

## Metadata

**Analog search scope:** `src/Messaging.Contracts/`, `src/Orchestrator/`, `src/BaseConsole.Core/`, `src/BaseApi.Service/Features/Orchestration/`, `src/BaseApi.Service/Features/Workflow/`, `tests/BaseApi.Tests/Orchestrator/`, `tests/BaseApi.Tests/Console/`
**Files scanned (git ls-files):** ~50; **files read this session:** 19 (all confirmed present)
**Pattern extraction date:** 2026-05-31
