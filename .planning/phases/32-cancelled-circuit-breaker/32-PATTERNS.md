# Phase 32: Cancelled Circuit-Breaker - Pattern Map

**Mapped:** 2026-06-04
**Files analyzed:** 8 production touch-points + 11 test files + 1 close script = 20
**Analogs found:** 20 / 20 (every new/modified file has a live in-repo analog)

> All analogs were read against the LIVE source (not the planning docs). Line numbers are current as
> of 2026-06-04; re-grep at plan-time if another phase lands first (RESEARCH "valid until" 2026-07-04).
> Three drift notes against the planning docs are flagged inline with **[DRIFT]**.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (MODIFY: add `Cancelled`) | config / contract | key-builder (transform) | `Flag(string)` / `ExecutionData(string)` builders in same file (`:41`, `:51`) | exact |
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (MODIFY) | consumer | event-driven / request-response | self (the existing dedup gate `:76`, effect-first writes `:189-216`, business catches `:123-133`) | exact (self-extension) |
| `src/Orchestrator/Consumers/ResultConsumer.cs` (MODIFY: gate + counter) | consumer | event-driven | self (dedup gate `:65`) | exact (self-extension) |
| `src/Orchestrator/Consumers/FaultUnscheduleConsumer.cs` (NEW) | consumer | event-driven (pub-sub fanout) | `StopOrchestrationConsumer.cs` | exact |
| `src/Orchestrator/Consumers/FaultUnscheduleConsumerDefinition.cs` (NEW) | config (consumer-def) | request-response (endpoint wiring) | `StopOrchestrationConsumerDefinition.cs` | exact |
| `src/Orchestrator/Program.cs` (MODIFY: register fault consumer) | config (composition root) | request-response | the Start/Stop `.Endpoint(InstanceId/Temporary)` block `:40-43` | exact |
| `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` (MODIFY: +2 counters) | observability holder | transform | self (Phase-30 `Counter<long>` ctor `:36-41`) | exact |
| `src/Orchestrator/Observability/OrchestratorMetrics.cs` (MODIFY: +1 counter) | observability holder | transform | self (Phase-30 `Counter<long>` ctor `:33-38`) | exact |
| `src/BaseApi.Service/Features/Step/StepEntryCondition.cs` (MODIFY: delete `:19`) | model (enum) | n/a | self | exact |
| `src/Messaging.Contracts/StepOutcome.cs` (MODIFY: comment `:20`) | model (enum) | n/a | self | exact |
| `tests/.../Processor/BreakerTriggerFacts.cs` + 6 sibling Facts (NEW) | test (hermetic) | event-driven | `Processor/EffectFirstDedupFacts.cs` + `DispatchTestKit.cs` | exact |
| `tests/.../Orchestrator/FaultUnscheduleFacts.cs` / `FaultConsumerBindingFacts.cs` / `FaultIdempotencyFacts.cs` (NEW) | test (hermetic) | event-driven | `Orchestrator/ResultAckTests.cs` + `OrchestratorTestStubs.cs` | exact |
| `tests/.../Orchestrator/BreakerMetricsFacts.cs` (NEW) | test (hermetic) | transform | `Orchestrator/OrchestratorMetricsFacts.cs` | exact |
| `tests/.../Orchestrator/CancelledCircuitBreakerE2ETests.cs` (NEW) | test (real-stack E2E) | event-driven | `Orchestrator/IdempotentExactlyOnceE2ETests.cs` (cloned from `SampleRoundTripE2ETests`) | exact |
| `scripts/phase-32-close.ps1` (NEW) | config (gate script) | batch | `scripts/phase-31-close.ps1` | exact (byte-faithful clone) |

---

## Pattern Assignments

### `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (config / contract, key-builder)

**Analog:** same file — the `Flag(string)` and `ExecutionData(string)` builders.

**Existing builder pattern** (`L2ProjectionKeys.cs:41`, `:51`):
```csharp
public static string ExecutionData(string entryId) => $"{Prefix}data:{entryId}";

/// <summary>D-05: effect-first dedup flag key — <c>skp:flag:{64hex}</c>.</summary>
public static string Flag(string h) => $"{Prefix}flag:{h}";
```

**New builder to add** (mirror exactly; flat `skp:` convention, `:D` GUID format per the file's `Root` precedent at `:34`):
```csharp
/// <summary>Phase 32 (D-02/D-07): the no-TTL in-flight cancellation marker — <c>skp:cancelled:{workflowId:D}</c>.</summary>
public static string Cancelled(Guid workflowId) => $"{Prefix}cancelled:{workflowId:D}";
```

**Divergence:** keyed on `Guid workflowId` (like `Root(Guid)` at `:34`, NOT the 64-hex string keys). Render `:D` (hyphenated) to match `Root`. Consider a co-located `const string CancelledMarkerValue = "true";` so the set site (processor) and check sites (both consumers) share ONE sentinel literal (RESEARCH Unknown-1 sub-decision; "single source — consider a const next to the key builder"). Add the new builder to the doc-comment `<list>` block at `:19-26`.

---

### `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (consumer, event-driven) — 4 edits

**Analog:** self. This consumer already carries the dedup gate, the effect-first write idiom, the business catches, and the `ProcessorId`-tagged metric increment.

**Edit 1 — D-10 dedup counter at the existing `flag[H]=="Ack"` gate** (`:76-77`):

Existing gate:
```csharp
if ((string?)await db.StringGetAsync(L2ProjectionKeys.Flag(dispatch.H)) == "Ack")
    return;
```
Add the counter increment before `return` using the exact `ProcessorId`-tag idiom already at `:62-63`:
```csharp
metrics.DispatchConsumed.Add(1,
    new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")));
```
(use the NEW `metrics.DispatchDeduped` counter — see ProcessorMetrics below; `context.Id!.Value.ToString("D")` bang is justified per the `:59-60` comment.)

**Edit 2 — D-05 check-and-drop gate, placed AFTER the flag[H] gate** (RESEARCH Unknown-3: cheaper for the common dedup case, keeps Phase-31 gate visually first):
```csharp
// NEW (req-3 / D-05): drop in-flight work for a cancelled workflow. INFRA read (no catch) → propagates.
if ((string?)await db.StringGetAsync(L2ProjectionKeys.Cancelled(dispatch.WorkflowId)) == "true")
    return;   // ack-and-discard: no advancement, no rollback, no dead-letter, NO dedup counter
```
`dispatch.WorkflowId` is available — confirmed on the `EntryStepDispatch` record (`Messaging.Contracts/EntryStepDispatch.cs:12`).

**Edit 3 — D-01/D-02 final-attempt marker-set (the primary new logic).** Wrap the body's infra ops (the L2 read `:86`, output write `:169`, manifest write `:189`, outbound Pending seed `:201`, the `Send` inside `SendResult` `:238`) in an OUTERMOST try whose catch is gated ONLY on `ctx.GetRetryAttempt() == retryOptions.Value.Limit`. The existing business catches at `:123` / `:129` already convert ALL business outcomes to acked sends, so anything reaching this outer catch is infra by construction (RESEARCH A3 / Pitfall 2 — drop the `IsInfra` predicate). Effect-first: set marker → re-throw.

Effect-first write idiom to copy (the existing seed at `:201-203`), but with `expiry: null` (NO TTL, D-07 — Pitfall 3):
```csharp
catch (Exception) when (ctx.GetRetryAttempt() == retryOptions.Value.Limit)   // ONLY the final exhausted attempt
{
    await db.StringSetAsync(
        L2ProjectionKeys.Cancelled(dispatch.WorkflowId), "true", expiry: null);   // NO TTL (D-07)
    metrics.WorkflowCancelled.Add(1,
        new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")));
    logger.LogWarning(
        "Breaker tripped — workflow {WorkflowId} cancelled on infra-exhaustion (step {StepId}, processor {ProcessorId}, H {H})",
        dispatch.WorkflowId, dispatch.StepId, dispatch.ProcessorId, dispatch.H);
    throw;   // re-throw → MassTransit publishes Fault<EntryStepDispatch> + dead-letters to _error (D-03)
}
```
The structured-log idiom mirrors the existing `BeginScope` + `LogInformation` execution-scope logging at `:92`, `:161-165`, `:176` (ids as template fields, never tainted templates — T-18-04).

**Edit 4 — ctor dependency.** Add `IOptions<RetryOptions> retryOptions` to the primary ctor (`:42-49`). **[DRIFT]** the processor consumer does NOT currently inject `IOptions<RetryOptions>` — only `ProcessorStartupOrchestrator.cs:58` does (grep-confirmed). The injection idiom + the `using Microsoft.Extensions.Options;` (already imported at `:10`) and `using Messaging.Contracts.Configuration;` come from `ResultConsumerDefinition.cs:24-30`. `RetryOptions` is already bound in `BaseProcessorServiceCollectionExtensions.cs:88` (`services.Configure<RetryOptions>(cfg.GetSection("Retry"))`), so DI resolution is already wired — only the ctor param is new.

> **[DRIFT] — Risk R1, blocking:** No `GetRetryAttempt()` call exists anywhere in `src/` today (grep-confirmed: the only `RetryOptions` references are bind/inject sites). The exact attempt-number at exhaustion for this project's single endpoint-level `Immediate(Limit)` policy MUST be pinned by the Wave-0 `RetryAttemptNumberingFacts` BEFORE this catch is trusted. The SPEC locks `== Limit`; do not silently change it (escalate if the pinned value differs).

---

### `src/Orchestrator/Consumers/ResultConsumer.cs` (consumer, event-driven) — 2 edits

**Analog:** self.

**Edit 1 — D-10 dedup counter at the existing `flag[H]=="Ack"` gate** (`:65-66`):
```csharp
if ((string?)await db.StringGetAsync(L2ProjectionKeys.Flag(m.H)) == "Ack")
    return;
```
Add before `return`, using the exact `ProcessorId`-tag idiom already at `:55` (note: `m.ProcessorId` is a non-nullable `Guid` here — NO bang, unlike the processor):
```csharp
metrics.ResultDeduped.Add(1, new KeyValuePair<string, object?>("ProcessorId", m.ProcessorId.ToString("D")));
```

**Edit 2 — D-05 check-and-drop gate**, placed AFTER the flag[H] gate (`:66`) and BEFORE the L1 `store.TryGet` (`:69`):
```csharp
// NEW (req-3 / D-05): drop in-flight result for a cancelled workflow. INFRA read (no catch) → propagates.
if ((string?)await db.StringGetAsync(L2ProjectionKeys.Cancelled(m.WorkflowId)) == "true")
    return;   // ack-and-discard, NO dedup counter
```
`m.WorkflowId` is available on `ExecutionResult` (via `IExecutionCorrelated`, per the `:55`/`:74` usages).

**Divergence:** ResultConsumer does NOT set the marker or trip the breaker (the trip is processor-side only — D-01); it only checks-and-drops.

---

### `src/Orchestrator/Consumers/FaultUnscheduleConsumer.cs` (NEW consumer, pub-sub fanout)

**Analog:** `StopOrchestrationConsumer.cs` (full file — same DI shape: `WorkflowLifecycle` + `ILogger`, calls `UnscheduleOnlyAsync`).

**Analog body** (`StopOrchestrationConsumer.cs:23-37`):
```csharp
public sealed class StopOrchestrationConsumer(
    WorkflowLifecycle lifecycle,
    ILogger<StopOrchestrationConsumer> logger) : IConsumer<StopOrchestration>
{
    public async Task Consume(ConsumeContext<StopOrchestration> context)
    {
        foreach (var workflowId in context.Message.WorkflowIds)
        {
            logger.LogInformation("Stop drain for WorkflowId={WorkflowId}", workflowId);
            await lifecycle.UnscheduleOnlyAsync(workflowId, context.CancellationToken);
        }
    }
}
```

**Reused idempotent unschedule** (`WorkflowLifecycle.cs:149-158` — keep-L1, absent-from-L1 = business no-op, jobId-addressed `DeleteJob`):
```csharp
public async Task UnscheduleOnlyAsync(Guid workflowId, CancellationToken ct)
{
    if (!store.TryGet(workflowId, out var wf)) return;   // BUSINESS no-op — nothing to unschedule
    await scheduler.UnscheduleAsync(wf.JobId, ct);       // jobId-addressed DeleteJob — NO store.Remove (keep L1)
}
```

**Divergences from the StopOrchestration analog:**
- `IConsumer<Fault<EntryStepDispatch>>` (not `IConsumer<StopOrchestration>`). `Fault<T>.Message` IS the original message instance → extract `var workflowId = context.Message.Message.WorkflowId;` (double `.Message` — outer is the `Fault<T>`, inner is the `EntryStepDispatch`). **[VERIFIED]** `EntryStepDispatch.WorkflowId` exists (`EntryStepDispatch.cs:12`).
- Single `workflowId` (no `foreach` over `WorkflowIds` — a Fault carries one message).
- **D-13: read/write NO `flag[H]` key, NO L2 marker write** (the processor already set the marker). Pitfall 4 — do NOT pattern-match the `flag[H]=Pending` pre-write onto this path.
- Log at WARN ("Fault halt — unscheduling workflow {WorkflowId}").

---

### `src/Orchestrator/Consumers/FaultUnscheduleConsumerDefinition.cs` (NEW config)

**Analog:** `StopOrchestrationConsumerDefinition.cs` (full file).

**Analog** (`StopOrchestrationConsumerDefinition.cs:15-39`):
```csharp
public sealed class StopOrchestrationConsumerDefinition : ConsumerDefinition<StopOrchestrationConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;
    public StopOrchestrationConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = "orchestrator";   // SHARED base name (both defs)
    }
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<StopOrchestrationConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r =>
        {
            r.Immediate(_retryOptions.Value.Limit);
            r.Ignore<WorkflowRootNotFoundException>();
        });
    }
}
```

**Divergences:**
- Typed to `FaultUnscheduleConsumer`.
- **`EndpointName = "orchestrator"`** (the SAME shared base name as Start/Stop) — this is the load-bearing line (Pitfall 5). It groups the fault consumer onto the per-replica `orchestrator-{instanceId}` fan-out queue with Start/Stop, NOT the shared `orchestrator-result` competing-consumer queue.
- `UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))` to bound the unschedule's own Send/Quartz infra faults. Likely OMIT the `r.Ignore<WorkflowRootNotFoundException>()` line (the fault path's `UnscheduleOnlyAsync` returns a no-op on absent-L1, never throws that exception) — confirm at plan-time.

---

### `src/Orchestrator/Program.cs` (MODIFY composition root)

**Analog:** the Start/Stop registration block in the same file (`Program.cs:40-43`):
```csharp
x.AddConsumer<StartOrchestrationConsumer, StartOrchestrationConsumerDefinition>()
    .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
x.AddConsumer<StopOrchestrationConsumer, StopOrchestrationConsumerDefinition>()
    .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
```
**Add (mirror exactly, inside the same `AddBaseConsoleMessaging` lambda, sharing the same `instanceId` closure from `:35`):**
```csharp
x.AddConsumer<FaultUnscheduleConsumer, FaultUnscheduleConsumerDefinition>()
    .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });   // per-replica fan-out (D-06)
```
**Divergence:** none in shape — add NOTHING to the `OrchestratorMetrics`/Quartz wiring. The `Configure<RetryOptions>` bind (`:29`) and `instanceId` closure (`:35`) already exist; the new definition's `IOptions<RetryOptions>` ctor resolves from them.

---

### `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` (MODIFY: +2 counters)

**Analog:** self — the Phase-30 ctor (`ProcessorMetrics.cs:36-41`):
```csharp
public ProcessorMetrics(IMeterFactory meterFactory)
{
    var meter = meterFactory.Create(MeterName);
    DispatchConsumed = meter.CreateCounter<long>("processor_dispatch_consumed");   // D-03 — collector appends the suffix
    ResultSent       = meter.CreateCounter<long>("processor_result_sent");         // D-03 — collector appends the suffix
}
```
**Add two counters** (snake_case, NO `_total` suffix — the collector's `add_metric_suffixes` appends it; Risk R3 / the file's own `:18-22` doc comment):
```csharp
public Counter<long> DispatchDeduped  { get; }   // processor_dispatch_deduped (D-10)
public Counter<long> WorkflowCancelled { get; }  // workflow_cancelled (D-11, trip is processor-side)
// ...in ctor:
DispatchDeduped   = meter.CreateCounter<long>("processor_dispatch_deduped");
WorkflowCancelled = meter.CreateCounter<long>("workflow_cancelled");
```
**Divergence:** none — additive only. Add matching `<summary>` doc-comment properties mirroring `:30-34`.

---

### `src/Orchestrator/Observability/OrchestratorMetrics.cs` (MODIFY: +1 counter)

**Analog:** self — the Phase-30 ctor (`OrchestratorMetrics.cs:33-38`):
```csharp
public OrchestratorMetrics(IMeterFactory meterFactory)
{
    var meter = meterFactory.Create(MeterName);
    DispatchSent   = meter.CreateCounter<long>("orchestrator_dispatch_sent");
    ResultConsumed = meter.CreateCounter<long>("orchestrator_result_consumed");
}
```
**Add one counter:**
```csharp
public Counter<long> ResultDeduped { get; }   // orchestrator_result_deduped (D-10)
// ...in ctor:
ResultDeduped = meter.CreateCounter<long>("orchestrator_result_deduped");
```
**Divergence:** none — additive only.

---

### `src/BaseApi.Service/Features/Step/StepEntryCondition.cs` + `src/Messaging.Contracts/StepOutcome.cs` (MODIFY enums)

**Analog:** self.

`StepEntryCondition.cs` current (`:14-22`):
```csharp
public enum StepEntryCondition
{
    PreviousProcessing = 0,
    PreviousCompleted = 1,
    PreviousFailed = 2,
    PreviousCancelled = 3,   // DELETE this line (D-12) — leave 3 as a numeric gap, do NOT renumber 0/1/2/4/5
    Always = 4,
    Never = 5,
}
```
**Action:** delete only `:19`. `StepDtoValidator.cs:37-38` and `:68-69` (`RuleFor(x => x.EntryCondition).IsInEnum()`) then auto-reject `EntryCondition == 3` — NO validator change needed (behavior falls out of `IsInEnum`).

`StepOutcome.cs` current (`:20`):
```csharp
Cancelled = 3, // == StepEntryCondition.PreviousCancelled
```
**Action:** KEEP the member (`StepOutcome.Cancelled = 3` is live — `EntryStepDispatchConsumer.cs:257` maps it, `BuildCancelled` `:289` emits it for the EXEC-08 token-cancellation outcome, unchanged). Update ONLY the comment per D-12 (e.g. `// special-cased by the consumer (token-cancellation, EXEC-08); not matched by SelectNext`). Consider updating the enum-level remark at `StepOutcome.cs:5-6` ("Int values mirror StepEntryCondition.Previous* (0-3)") since `Previous*(3)` no longer exists.

**Divergence:** none — `StepAdvancement.SelectNext` never names `PreviousCancelled` (RESEARCH Unknown-5, grep-confirmed 2 source touch-points only).

---

## Shared Patterns

### Effect-first INFRA-throw discipline (no-catch propagation)
**Source:** `EntryStepDispatchConsumer.cs:68-70` / `ResultConsumer.cs:57-60`
**Apply to:** the new check-and-drop `StringGetAsync` reads (INFRA, no catch → propagates to `Immediate(Limit)` retry) and the marker `StringSetAsync` (INFRA).
```csharp
// A Redis fault on GetDatabase / StringGetAsync / StringSetAsync is INFRA and propagates (no catch)
var db = redis.GetDatabase();
```

### `ProcessorId`-tagged counter increment (Phase-30 pinned convention)
**Source:** `EntryStepDispatchConsumer.cs:62-63` (processor, `context.Id!.Value.ToString("D")`) and `ResultConsumer.cs:55` (orchestrator, `m.ProcessorId.ToString("D")`, non-nullable, no bang)
**Apply to:** all three new counters. PascalCase `"ProcessorId"` tag key; value `.ToString("D")`; ambient `service_instance_id`; **NO `workflowId` label** (cardinality, T-30-04).

### No-TTL marker write (Pitfall 3 — diverge from the ubiquitous TTL'd write)
**Source (the TTL'd idiom to AVOID copying):** `EntryStepDispatchConsumer.cs:169-172, :189-192, :201-203` all use `expiry: TimeSpan.FromSeconds(opts.ExecutionDataTtlSeconds)`.
**Apply to:** the marker SET MUST use `expiry: null` (or a no-expiry overload). `TTL skp:cancelled:{id}` must return `-1`.

### Reused idempotent unschedule (don't hand-roll DeleteJob)
**Source:** `WorkflowLifecycle.UnscheduleOnlyAsync` (`WorkflowLifecycle.cs:149-158`) — keep-L1, absent-from-L1 no-op, jobId-addressed `DeleteJob`. `WorkflowLifecycle` is documented READ-ONLY against L2 (`:14-16`) — keep it that way (Pitfall 6: never add a marker-clear to Stop/Teardown).
**Apply to:** the fault consumer.

### Per-replica fan-out endpoint (shared `EndpointName="orchestrator"` + `InstanceId`+`Temporary`)
**Source:** `Program.cs:40-43` + `StopOrchestrationConsumerDefinition.cs:22` (`EndpointName = "orchestrator"`).
**Apply to:** the new fault consumer + its definition (Pitfall 5 — the load-bearing multi-replica-correctness line).

### `IOptions<RetryOptions>.Value.Limit` single source
**Source:** `ResultConsumerDefinition.cs:24-30` (definition ctor inject) + `RetryOptions.cs:9` (`Limit` default 3). Bound at `BaseProcessorServiceCollectionExtensions.cs:88` and `Program.cs:29`.
**Apply to:** the breaker's `GetRetryAttempt() == retryOptions.Value.Limit` (processor consumer ctor) AND the new fault-consumer definition's `UseMessageRetry`. No second hard-coded `3`, no new config key.

---

## Test Pattern Assignments

### Hermetic processor Facts (`BreakerTriggerFacts`, `RetryAttemptNumberingFacts`, `CancelledMarkerFacts`, `CheckAndDropFacts`, `StepEntryConditionEnumFacts`)

**Analog:** `tests/BaseApi.Tests/Processor/EffectFirstDedupFacts.cs` + `DispatchTestKit.cs`.

**Idioms to copy** (`EffectFirstDedupFacts.cs`):
- NSubstitute `IDatabase` + `IConnectionMultiplexer` wiring (`:72-78`): `mux.GetDatabase(...).Returns(db)`.
- Per-key `StringGetAsync` stub (`:74-76`): `db.StringGetAsync(...).Returns(ci => ((RedisKey)ci[0]).ToString() == L2ProjectionKeys.Cancelled(...) ? (RedisValue)"true" : RedisValue.Null)`.
- Asserting a SET happened/not (`:90-95`): `db.Received(1)/DidNotReceive().StringSetAsync(Arg.Is<RedisKey>(k => k.ToString() == L2ProjectionKeys.Cancelled(wfId)), Arg.Is<RedisValue>(v => v == "true"), Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>())`. **Note the overload split** (`:36-50`): the `expiry:` TimeSpan write binds the `(RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags)` overload; the `expiry: null` no-TTL marker write will likely bind a different overload — match `Arg.Any<TimeSpan?>()`/the null-expiry overload at plan-time.
- Building the consumer + driving it: `DispatchTestKit.Build(mux, context, processor, send)` + `consumer.Consume(OrchestratorTestStubs.Context(dispatch, ct))` (`:83-85`).

**Divergence:** the breaker tests need a ConsumeContext whose `GetRetryAttempt()` returns a controlled value — `OrchestratorTestStubs.Context` (a plain `Substitute.For<ConsumeContext<T>>`) does NOT stub `GetRetryAttempt()` (it's a MassTransit extension over headers). `RetryAttemptNumberingFacts` (Risk R1) must drive a REAL in-memory `Immediate(Limit)` endpoint with a deterministically-throwing infra op — closer to a MassTransit `InMemoryTestHarness` than the NSubstitute stub. Flag this as the one test that cannot reuse the substitute-context kit.

### Hermetic orchestrator Facts (`FaultUnscheduleFacts`, `FaultConsumerBindingFacts`, `FaultIdempotencyFacts`)

**Analog:** `tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs` + `OrchestratorTestStubs.cs`.

**Idioms to copy** (`OrchestratorTestStubs.cs`):
- `Context<T>(message, ct)` is GENERIC (`:129-136`) → directly supports `ConsumeContext<Fault<EntryStepDispatch>>` with a NSubstitute `Fault<EntryStepDispatch>` whose `.Message` returns the `EntryStepDispatch` (for the `context.Message.Message.WorkflowId` extraction proof in `FaultConsumerBindingFacts`).
- Fake L1 store via the existing store stubs; `UnscheduleOnlyAsync` idempotency (absent-L1 no-op, duplicate-delivery no-op) asserted against a fake/substitute scheduler.

**Divergence:** `FaultConsumerBindingFacts` (Risk R2) should use a MassTransit in-memory harness to prove `Fault<EntryStepDispatch>.Message.WorkflowId` actually round-trips through real fault publication (the NSubstitute stub proves the consumer code, not the MT binding).

### Hermetic metrics Facts (`BreakerMetricsFacts`)

**Analog:** `tests/BaseApi.Tests/Orchestrator/OrchestratorMetricsFacts.cs` (`:24-35`) — real `IMeterFactory` via `new ServiceCollection().AddMetrics().BuildServiceProvider()`, assert counters non-null + meter-name const. Extend with a `MeterListener` to assert the new counters increment once per drop/trip and carry NO `workflowId` tag.

### Real-stack E2E (`CancelledCircuitBreakerE2ETests`)

**Analog:** `tests/BaseApi.Tests/Orchestrator/IdempotentExactlyOnceE2ETests.cs` (itself cloned from `SampleRoundTripE2ETests`). Reuse: genuine embedded-SourceHash reflection, `PollForHealthyLivenessAsync`, `PollEsForLog`, `RealStackWebAppFactory`, and the net-zero teardown that registers run keys into `L2KeysToCleanup`. Traits `[Trait("Category","E2E")]` + `[Trait("Category","RealStack")]` + `[Collection("Observability")]` (`:64-66`).

**Divergence:** the teardown MUST scan-clean the NEW `skp:cancelled:*` namespace — these keys have **NO TTL** and will NOT self-expire (unlike Phase-31's `skp:flag:*`/`skp:data:*` which the 330s settle-drain handles). Register `skp:cancelled:*` into `L2KeysToCleanup` explicitly.

### Close script (`scripts/phase-32-close.ps1`)

**Analog:** `scripts/phase-31-close.ps1` (byte-faithful clone). Relabel `31 → 32` / `v3.6.0 → v3.x` throughout.

**Divergence (the one substantive change — MEMORY `reference_close_gate_container_rebuild_and_flag_churn`):** the redis triple-SHA settle-drain at `phase-31-close.ps1:236-257` waits for `skp:flag:*`/`skp:data:*` to drain via TTL. The new `skp:cancelled:*` marker has **NO TTL** — it will NEVER drain on its own and would break the `BEFORE==AFTER` redis SHA invariant every run. The phase-32 teardown must **explicitly DELETE `skp:cancelled:*` keys** before the AFTER snapshot (e.g. `docker exec sk-redis redis-cli --scan --pattern 'skp:cancelled:*' | xargs redis-cli del`, or rely on the E2E `L2KeysToCleanup` deletion + assert drained). Rebuild processor/orchestrator/baseapi containers first (embedded SourceHash must match).

---

## No Analog Found

None. Every new/modified file has a direct in-repo analog (this phase is "almost entirely reuse + wiring" — RESEARCH key insight). The only genuinely new logic (the final-attempt catch and the one-line check-and-drop) extends existing consumers whose surrounding idioms are the analog.

---

## Metadata

**Analog search scope:** `src/Messaging.Contracts/`, `src/BaseProcessor.Core/`, `src/Orchestrator/`, `src/BaseApi.Service/Features/Step/`, `tests/BaseApi.Tests/Processor/`, `tests/BaseApi.Tests/Orchestrator/`, `scripts/`.
**Files scanned (read in full or targeted):** 16 source + test files + the 3 planning docs.
**Live-source cross-checks (drift flags):** (1) processor consumer does NOT yet inject `IOptions<RetryOptions>` — ctor param is new; (2) `GetRetryAttempt()` is used nowhere in `src/` today — Wave-0 pin required (Risk R1); (3) `skp:cancelled:*` is no-TTL → close-gate net-zero needs an explicit delete (not the Phase-31 settle-drain).
**No project `CLAUDE.md` / `.claude/skills/` present** (project guidance lives in user MEMORY.md, already loaded).
**Pattern extraction date:** 2026-06-04

## PATTERN MAPPING COMPLETE
