# Phase 46: Keeper 5-State Recovery + Orchestrator Per-Item Consume - Pattern Map

**Mapped:** 2026-06-08
**Files analyzed:** 22 (adds/edits/moves/replaces + tests)
**Analogs found:** 22 / 22 (every primitive already exists in Phase 43/44/45 code)

> This phase is an **assembly job over shipped primitives** (RESEARCH §Summary). Two reference
> implementations carry almost everything: `ProcessorPipeline.cs` (the inverse of the five Keeper
> bodies — every L2 op + `RetryLoop` call shape + `Build*` reconstruction) and `ResultConsumer.cs`
> (the literal body to lift into `TypedResultConsumer<T>`). Definitions copy
> `FaultEntryStepDispatchConsumerDefinition` (single-owner endpoint config) and
> `ResultConsumerDefinition` (shared competing-consumer endpoint). The await-inside-`Consume`
> gate-wait copies `L2ProbeRecovery` / `FaultEntryStepDispatchConsumer`.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Keeper/Recovery/RecoveryConsumerBase.cs` | consumer (abstract base) | event-driven (gate-wait + L2) | `ProcessorPipeline.cs` (L2 ops) + `L2ProbeRecovery.cs` (await-in-Consume) | role+flow synthesis |
| `src/Keeper/Recovery/UpdateConsumer.cs` | consumer | file-I/O (L2 write w/ TTL) | `ProcessorPipeline.cs:129-132` (write) | exact (inverse SendKeeper) |
| `src/Keeper/Recovery/ReinjectConsumer.cs` | consumer | file-I/O + request-response (L2 read → Send dispatch) | `ProcessorPipeline.cs:75-88` (read) + `StepDispatcher.cs:34-35` (Send) | exact |
| `src/Keeper/Recovery/InjectConsumer.cs` | consumer | file-I/O + request-response (read→write→Send→delete) | `ProcessorPipeline.cs:128-160` + RESEARCH §Code Examples KEEP-06 | exact |
| `src/Keeper/Recovery/DeleteConsumer.cs` | consumer | file-I/O (L2 delete) | `ProcessorPipeline.cs:159-160` (delete) | exact |
| `src/Keeper/Recovery/CleanupConsumer.cs` | consumer | file-I/O (L2 delete composite) | `ProcessorPipeline.cs:159-160` (delete) | exact |
| `src/Keeper/Recovery/RecoveryDataGoneException.cs` | utility (marker exception) | n/a | `BaseProcessor.Core.Resilience.KeyAbsentException` (Pre-read sentinel) | role-match |
| `src/Keeper/Recovery/{Update,Reinject,Inject,Delete,Cleanup}ConsumerDefinition.cs` (5) | config (endpoint def) | n/a | `FaultEntryStepDispatchConsumerDefinition.cs` (single-owner) + `ResultConsumerDefinition.cs` | exact |
| `src/Keeper/RecoveryOptions.cs` | config (options) | n/a | `ProbeOptions.cs` / `BackupOptions.cs` | exact |
| `src/Orchestrator/Consumers/TypedResultConsumer.cs` | consumer (abstract base) | event-driven (L1 advance) | `ResultConsumer.cs:46-77` | exact (lift to generic) |
| `src/Orchestrator/Consumers/StepCompletedConsumer.cs` (replaces `ResultConsumer.cs`) | consumer | event-driven | `ResultConsumer.cs` | exact |
| `src/Orchestrator/Consumers/{StepFailed,StepCancelled,StepProcessing}Consumer.cs` (3) | consumer | event-driven | `ResultConsumer.cs` (Outcome knob varies) | exact |
| `src/Orchestrator/Consumers/{four}ConsumerDefinition.cs` | config (endpoint def) | n/a | `ResultConsumerDefinition.cs` | exact |
| `src/Messaging.Contracts/KeeperReinject.cs` | edited contract | n/a | `KeeperUpdate.cs:11` (ValidatedData shape) | exact |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (`BuildReinject`) | edited send-site | n/a | self, line 201-202 | exact |
| `src/BaseConsole.Core/Resilience/RetryLoop.cs` | relocated helper | n/a | self (`BaseProcessor.Core/Resilience/RetryLoop.cs`) | move-in-place |
| `src/Keeper/Program.cs` | config (composition root) | n/a | self (lines 25-68) + RESEARCH Pattern 1 | edit |
| `src/Orchestrator/Program.cs` | config (composition root) | n/a | self (line 62) | edit |
| `tests/.../KeeperContractTests.cs` (extend) | test (contract pin) | n/a | self, line 60-64 | exact |
| `tests/.../Keeper/*ConsumerFacts.cs` (new) | test (consumer facts) | n/a | `DispatchTestKit.cs` + `PipelinePreFacts.cs` | exact |
| `tests/.../Orchestrator/TypedResultConsumerFacts.cs` (new) | test | n/a | existing ResultConsumer fixtures + `OrchestratorTestStubs` | role-match |
| `tests/.../Processor/RetryLoopFacts.cs` (using update) | test | n/a | self, line 1 | trivial |

---

## Shared Patterns

### A. `RetryLoop.ExecuteAsync` — every L2 op + every Send (D-05)
**Source:** `src/BaseProcessor.Core/Resilience/RetryLoop.cs:10-21` (relocating to `BaseConsole.Core/Resilience/`)
**Apply to:** all five Keeper bodies + the REINJECT/INJECT sends.

Surfaces exhaustion as `RetryOutcome<T>` (does NOT throw). The body checks `.Succeeded` and re-throws `.Error` to hit the D-04 error route:
```csharp
public static async Task<RetryOutcome<T>> ExecuteAsync<T>(Func<Task<T>> op, int limit, CancellationToken ct)
{
    Exception? last = null;
    for (var attempt = 0; attempt < Math.Max(1, limit); attempt++)
    {
        ct.ThrowIfCancellationRequested();
        try { return RetryOutcome<T>.Ok(await op().ConfigureAwait(false)); }
        catch (Exception ex) { last = ex; }   // immediate retry, no delay (A3)
    }
    return RetryOutcome<T>.Exhausted(last!);
}
```
Caller idiom (the D-04 re-throw — `ProcessorPipeline.cs:174,182`): `if (!outcome.Succeeded) throw outcome.Error!;`
**Relocation note (Pitfall 6):** namespace becomes `BaseConsole.Core.Resilience`. Update the two real `using` sites — `ProcessorPipeline.cs:3` and `tests/BaseApi.Tests/Processor/RetryLoopFacts.cs:1`. `KeyAbsentException.cs` stays in `BaseProcessor.Core` (processor-specific; Keeper uses its own `RecoveryDataGoneException`).

### B. The L2-access singleton + `RedisException`-only discipline
**Source:** `src/Keeper/Recovery/L2ProbeRecovery.cs:20,58,67-70`
**Apply to:** the recovery base / every Keeper body.
```csharp
public sealed class L2ProbeRecovery(IConnectionMultiplexer redis, IOptions<ProbeOptions> opts, KeeperMetrics metrics)
// ...
var db = redis.GetDatabase();
// natural Redis faults bubble into RetryLoop; do NOT catch(Exception) — a genuine bug must propagate
```
`IConnectionMultiplexer` is **already a DI singleton** via `AddBaseConsole` (`Keeper/Program.cs:28` comment "do NOT add it again"). Ctor-inject it; do not register a new one.

### C. L2 key builders + the TTL-at-call-site rule
**Source:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:42,46-47`
**Apply to:** UPDATE/REINJECT/INJECT/DELETE/CLEANUP.
```csharp
public static string ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}";                 // data key, NO TTL
public static string CompositeBackup(Guid corr, Guid wf, Guid proc, Guid exec)
    => $"{Prefix}{corr:D}:{wf:D}:{proc:D}:{exec:D}";                                              // composite, NO TTL
```
TTL is the **caller's** concern — applied only at the UPDATE `StringSetAsync(..., expiry: TimeSpan.FromDays(opts.TtlDays))` (KEEP-04). NEVER bake it into the builder (Anti-Pattern, L2ProjectionKeys.cs:45).

### D. Terminal give-up = throw (auto-routes to `skp-dlq-1`) (D-04 / Pitfall 3)
**Source:** `ProcessorPipeline.cs:174,182` (`throw sent.Error!`). The consolidated error transport is inherited from `BaseConsole.Core` — **do NOT** add per-consumer `ConfigureError`. A thrown exception (Redis fault re-thrown from `RetryLoop`, OR the deliberate `RecoveryDataGoneException`) lands in `skp-dlq-1` automatically.

### E. Single-owner endpoint config (D-02 / Pitfalls 1 & 4)
**Source:** `src/Keeper/Consumers/FaultEntryStepDispatchConsumerDefinition.cs:16-19,43`
> "the retry middleware is PER-ENDPOINT (not per-consumer) … only this definition may register it — the sibling's ConfigureConsumer is an intentional no-op."

ONE of the five recovery `ConsumerDefinition`s owns the endpoint-level `UseMessageRetry` **and** the five `UsePartitioner<T>` calls; the other four `ConfigureConsumer` are intentional no-ops. Bind the retry limit from `RetryOptions` exactly as the analog does:
```csharp
endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));
```

### F. Options-bound knobs (D-03 / D-06)
**Source:** `src/Keeper/ProbeOptions.cs` + `BackupOptions.cs:6-9`; bound in `Program.cs:25,29,33`.
New `RecoveryOptions { PartitionCount = 8; GateWaitSeconds = 300; }` bound via `builder.Services.Configure<RecoveryOptions>(builder.Configuration.GetSection("Recovery"));`. The **load-bearing** ProbeOptions doc-comment is the gate-wait precedent: "MUST stay well under RabbitMQ's default 30-min consumer_timeout — the loop is awaited INSIDE Consume, holding the broker delivery un-acked."

---

## Pattern Assignments

### `src/Keeper/Recovery/RecoveryConsumerBase.cs` (consumer base; D-02/D-03)

**Analogs:** `L2ProbeRecovery.cs` (await-inside-`Consume`), `FaultEntryStepDispatchConsumer.cs` (one-line delegation), `RetryLoop.cs`, `IL2HealthGate.cs`.

**Gate primitive** (`src/Keeper/Health/IL2HealthGate.cs:4-9`):
```csharp
public interface IL2HealthGate { void Open(); void Close(); Task WaitForOpenAsync(CancellationToken ct); }
```

**Await-inside-Consume precedent** (`L2ProbeRecovery.cs:25,33-41`, `ProbeOptions.cs:4-7`): the existing Keeper model holds the delivery un-acked while looping/awaiting under a bound well under `consumer_timeout`. Base should: build a linked CTS bounded at `GateWaitSeconds` (~300s), `await gate.WaitForOpenAsync(linkedCt)` ONCE at entry (before any L2 op), then dispatch to the subclass body. On bound exhaustion throw a **transient** marker so the endpoint `UseMessageRetry` re-attempts (D-03 Resolution / Pitfall 2 Pattern A). Per-key ordering survives retries because the partitioner re-slots each redelivery (RESEARCH §D-03 Resolution).

**Per-type delegation shape to mirror** (`FaultEntryStepDispatchConsumer.cs:17-23`):
```csharp
public sealed class FaultEntryStepDispatchConsumer(KeeperRecoveryHandler handler) : IConsumer<Fault<EntryStepDispatch>>
{
    public Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context) =>
        handler.HandleAsync(context, KeeperMetricTags.FaultTypeDispatch, inner => new Uri($"queue:{inner.ProcessorId:D}"), context.CancellationToken);
}
```
Base owns gate-wait + `RetryLoop` wrapping; each subclass overrides an `abstract Task HandleAsync(...)` with its state body. (D-02 chose five sealed `IConsumer<T>` subclasses over one multi-`IConsumer` class.)

---

### `src/Keeper/Recovery/UpdateConsumer.cs` (KEEP-04, file-I/O write w/ TTL)

**Analog:** `ProcessorPipeline.cs:129-132` (the no-TTL processor write — ADD the TTL). Key from `L2ProjectionKeys.CompositeBackup` (Pattern C), TTL from `BackupOptions.TtlDays`.
```csharp
var key = L2ProjectionKeys.CompositeBackup(m.CorrelationId, m.WorkflowId, m.ProcessorId, m.ExecutionId);
var write = await RetryLoop.ExecuteAsync(
    () => db.StringSetAsync(key, m.ValidatedData, expiry: TimeSpan.FromDays(backupOpts.TtlDays)), limit, ct);
if (!write.Succeeded) throw write.Error!;   // → skp-dlq-1 (Pattern D)
```
`KeeperUpdate.ValidatedData` source: `KeeperUpdate.cs:11`.

---

### `src/Keeper/Recovery/ReinjectConsumer.cs` (KEEP-05, read → Send dispatch; needs D-01 Payload)

**Analogs:** `ProcessorPipeline.cs:75-88` (the bounded read with absent/empty → throw), `StepDispatcher.cs:26-35` (the `EntryStepDispatch` ctor + `queue:{proc:D}` Send idiom), `KeeperReinject.cs` (the source message — gains `Payload` per D-01).

**Read** (mirror Pre-read; absent/empty → the data-gone marker, NOT `KeyAbsentException`):
```csharp
var read = await RetryLoop.ExecuteAsync(async () => {
    var raw = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(m.EntryId));
    if (raw.IsNullOrEmpty) throw new RecoveryDataGoneException();   // D-04 deliberate terminal
    return raw.ToString();
}, limit, ct);
if (!read.Succeeded) throw read.Error!;   // Redis fault OR data-gone → skp-dlq-1
```
**Reconstruct + Send** (the `EntryStepDispatch` ctor is `(WorkflowId, StepId, ProcessorId, Payload)` per `EntryStepDispatch.cs:11-16`; the Send idiom is `StepDispatcher.cs:34-35`):
```csharp
var dispatch = new EntryStepDispatch(m.WorkflowId, m.StepId, m.ProcessorId, m.Payload /* D-01 */)
    { CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId, EntryId = m.EntryId };
var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{m.ProcessorId:D}"));
var sent = await RetryLoop.ExecuteAsync(async () => { await ep.Send(dispatch, ct); return true; }, limit, ct);
if (!sent.Succeeded) throw sent.Error!;
```

---

### `src/Keeper/Recovery/InjectConsumer.cs` (KEEP-06, read composite → new entryId → write → Send StepCompleted → delete composite)

**Analogs:** `ProcessorPipeline.cs:128-160` (read/write/Send/delete idioms), `StepCompleted.cs:7-12` (ctor + id carriage), `ResultConsumerDefinition.cs:29` (`OrchestratorQueues.Result` target), RESEARCH §Code Examples KEEP-06 (the full worked sequence).

Ordered ops (assert order via `Received.InOrder` per RESEARCH §Validation): read composite (`RecoveryDataGoneException` if absent) → `NewId.NextGuid()` entryId → `StringSetAsync(ExecutionData(entryId), data)` **NO expiry** → reconstruct `StepCompleted(m.WorkflowId, m.StepId, m.ProcessorId) { CorrelationId, ExecutionId = m.ExecutionId, EntryId = entryId }` → Send to `queue:{OrchestratorQueues.Result}` → `KeyDeleteAsync(composite)`. Each step wrapped in `RetryLoop`; `if (!x.Succeeded) throw x.Error!`. The INJECT'd `StepCompleted` must be byte-indistinguishable from a direct completion (ORCH-01) — same type, same `OrchestratorQueues.Result` target, processed by the same `StepCompletedConsumer`.

---

### `src/Keeper/Recovery/DeleteConsumer.cs` (KEEP-07) & `CleanupConsumer.cs` (KEEP-08)

**Analog:** `ProcessorPipeline.cs:159-160` (the end-delete).
```csharp
var del = await RetryLoop.ExecuteAsync(() => db.KeyDeleteAsync(key), limit, ct);
if (!del.Succeeded) throw del.Error!;
```
DELETE key = `L2ProjectionKeys.ExecutionData(m.EntryId)` (`KeeperDelete.EntryId`). CLEANUP key = `L2ProjectionKeys.CompositeBackup(4-tuple)` (`KeeperCleanup` carries only the 4-tuple).

---

### `src/Keeper/Recovery/RecoveryDataGoneException.cs` (D-04 marker)

**Analog:** `BaseProcessor.Core.Resilience.KeyAbsentException` (the Pre-read absent/empty sentinel used at `ProcessorPipeline.cs:78`). A trivial sealed `Exception` subtype thrown inside the REINJECT/INJECT read closure when `raw.IsNullOrEmpty`, so the deliberate data-gone terminal forces the same dead-letter route as a natural Redis fault (instead of silently acking). Place in `Keeper.Recovery`. Distinct from `KeyAbsentException` (which stays in `BaseProcessor.Core` — Keeper does not reference that project).

---

### `src/Keeper/Recovery/*ConsumerDefinition.cs` (five; D-02/D-06 — single-owner)

**Analog:** `FaultEntryStepDispatchConsumerDefinition.cs:21-44` (single-owner endpoint config + `RetryOptions` ctor inject) and `ResultConsumerDefinition.cs:22-43` (shared competing-consumer `EndpointName`).

All five set `EndpointName = KeeperQueues.Recovery` ("keeper-recovery"). ONE definition (e.g. `UpdateConsumerDefinition`) owns `ConfigureConsumer` registering `UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))` **and** the five `UsePartitioner<T>` calls; the other four `ConfigureConsumer` are no-ops (Pitfall 4). Partitioner (RESEARCH Pattern 1):
```csharp
var partition = new Partitioner(opts.Value.PartitionCount, new Murmur3UnsafeHashGenerator());
e.UsePartitioner<KeeperUpdate>  (partition, p => PartitionKey(p.Message));
// ...one per Keeper message type...
static string PartitionKey(IKeeperRecoverable m) => $"{m.CorrelationId:D}:{m.WorkflowId:D}:{m.ProcessorId:D}:{m.ExecutionId:D}";
```
Partition key = the `IKeeperRecoverable` 4-tuple (`IKeeperRecoverable.cs:8-14`), **excluding StepId** (D-12). Verify the 8.5.5 `UsePartitioner`/`Partitioner`/`Murmur3UnsafeHashGenerator` signatures in Wave 0 (RESEARCH A1/A2).

---

### `src/Keeper/RecoveryOptions.cs` (D-03/D-06) — see Shared Pattern F.

---

### `src/Orchestrator/Consumers/TypedResultConsumer.cs` + four subclasses (D-07 / ORCH-01)

**Analog:** `ResultConsumer.cs:39-77` — the literal blueprint. Lift the body into a generic base; the ONLY change is replacing the hardcoded `StepOutcome.Completed` (line 72) with `protected abstract StepOutcome Outcome { get; }`.

**Body to generalize** (`ResultConsumer.cs:48-76`):
```csharp
var m = context.Message;
metrics.ResultConsumed.Add(1, new KeyValuePair<string, object?>("ProcessorId", m.ProcessorId.ToString("D")));
if (!store.TryGet(m.WorkflowId, out var wf) || !wf.Steps.TryGetValue(m.StepId, out var completed))
{ logger.LogInformation("No L1 entry for ({W},{S}) — acking (business)", m.WorkflowId, m.StepId); return; }   // L1-miss = business-ack
foreach (var (stepId, step) in advancement.SelectNext(Outcome, completed, wf.Steps))   // Outcome was StepOutcome.Completed
    await dispatcher.DispatchAsync(m.WorkflowId, stepId, step.ProcessorId, step.Payload,
        m.CorrelationId, NewId.NextGuid(), m.EntryId, context.CancellationToken);   // seed entryId = m.EntryId
```
Constraint `where TMessage : class, IStepResult`. Deps (ctor-inject, copy `ResultConsumer.cs:39-44`): `IWorkflowL1Store store, StepAdvancement advancement, IStepDispatcher dispatcher, OrchestratorMetrics metrics, ILogger<...>`. Use `ILogger<TMessage>` (the existing consumer used `ILogger<ResultConsumer>` — minor; RESEARCH Pattern 4 caveat).

**`SelectNext` is unchanged** (`StepAdvancement.cs:36-43`): matches `next.EntryCondition == (int)outcome` or `Always(4)`; `Never(5)` falls out. **`StepOutcome` int mapping** (`StepOutcome.cs:15-21`, VERIFIED): `Processing=0, Completed=1, Failed=2, Cancelled=3`. Four one-line subclasses set the `Outcome` knob — **no if/switch anywhere** (D-07):
```csharp
public sealed class StepCompletedConsumer(/*same deps*/) : TypedResultConsumer<StepCompleted>(/*…*/)
{ protected override StepOutcome Outcome => StepOutcome.Completed; }
// StepFailedConsumer→Failed, StepCancelledConsumer→Cancelled, StepProcessingConsumer→Processing
```
`StepCompletedConsumer` **replaces** `ResultConsumer.cs`. `StepProjection.ProcessorId`/`.Payload`/`.NextStepIds`/`.EntryCondition` (`src/Messaging.Contracts/Projections/StepProjection.cs`) are consumed verbatim — already exercised by the analog loop, no change.

---

### `src/Orchestrator/Consumers/*ConsumerDefinition.cs` (four; D-07)

**Analog:** `ResultConsumerDefinition.cs:22-43`. All four set `EndpointName = OrchestratorQueues.Result` ("orchestrator-result") and `UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))`. Four co-located consumers on one shared endpoint — apply the single-owner retry pattern (Shared Pattern E) if retry must not double-register; confirm in Wave 0 whether MassTransit dedups identical `UseMessageRetry` across same-endpoint definitions (Pitfall 4 applies to the partitioner; plain retry may be benign but follow the established single-owner convention).

---

### `src/Messaging.Contracts/KeeperReinject.cs` (EDIT, D-01)

**Analog:** `KeeperUpdate.cs:11` — `public string ValidatedData { get; init; } = "";`. Add the mirror field to `KeeperReinject` (currently `KeeperReinject.cs:7-12`):
```csharp
public string Payload { get; init; } = "";   // D-01: REINJECT carries the step config for faithful EntryStepDispatch reconstruction
```
`init`-only, `string`, `= ""` default — follow the record convention exactly. No other Keeper record changes.

---

### `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (EDIT `BuildReinject`, D-01)

**Self, line 201-202:** stamp the inbound dispatch's `Payload` onto the new field:
```csharp
private static KeeperReinject BuildReinject(EntryStepDispatch d) =>
    new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId, Payload = d.Payload };
```
Atomic with the contract edit + the test pin (Pitfall 5 — all three change together).

---

### `src/Keeper/Program.cs` (EDIT — register five consumers + partitioner + options)

**Self, lines 25/29/33** (options-bind precedent): add `Configure<RecoveryOptions>(...GetSection("Recovery"))`. **Self, lines 65-68** (`AddBaseConsoleMessaging` consumer registration): add the five `x.AddConsumer<XxxConsumer, XxxConsumerDefinition>()`. Keeper's firewall (`Keeper.csproj`: only `BaseConsole.Core` + `Messaging.Contracts`) is what forces the `RetryLoop` relocation (D-05).

---

### `src/Orchestrator/Program.cs` (EDIT — replace one registration with four)

**Self, line 62** currently `x.AddConsumer<ResultConsumer, ResultConsumerDefinition>();` — replace with the four typed `AddConsumer<Step*Consumer, Step*ConsumerDefinition>()`. Existing DI for `IStepDispatcher`/`StepAdvancement`/`IWorkflowL1Store`/`OrchestratorMetrics` (lines 74-75 + meter) already covers all four (RESEARCH §Reusable Assets).

---

### Tests

| Test file | Analog | What to copy |
|-----------|--------|--------------|
| `tests/.../Contracts/KeeperContractTests.cs` (extend) | self, line 60-64 | Add `Assert.NotNull(typeof(KeeperReinject).GetProperty("Payload"))` to `KeeperReinject_carries_EntryId_*`; `[Trait("Phase","43")]` reflection-pin style |
| `tests/.../Keeper/{Update,Reinject,Inject,Delete,Cleanup}ConsumerFacts.cs` (new) | `DispatchTestKit.cs` + `PipelinePreFacts.cs` | substituted `IConnectionMultiplexer.GetDatabase()`→fake `IDatabase` (the `PresentReadWriteDeleteOkL2`/`ReadFaultL2`/`AbsentReadL2` fakes), `CapturingSendProvider` (DispatchTestKit.cs:253-275) capturing `IStepResult`/`IKeeperRecoverable`, `Retry(limit)`/`Metrics()` helpers; assert exact ops + args (key shape, TTL, ids) + INJECT op-order via `Received.InOrder`; fake `IL2HealthGate` that blocks until released for the gate-wait fact |
| `tests/.../Orchestrator/TypedResultConsumerFacts.cs` (new) | existing `ResultConsumer` fixtures / `OrchestratorTestStubs` | per-subclass `Outcome` assertion; `SelectNext` called with that outcome; L1-miss acks (no throw); `DispatchAsync` preserves corr/wf/exec ids + seeds `entryId = m.EntryId`; ORCH-01 indistinguishability fact (INJECT-reconstructed vs direct `StepCompleted`) |
| `tests/.../Processor/RetryLoopFacts.cs` (using update) | self, line 1 | change `using BaseProcessor.Core.Resilience;` → `using BaseConsole.Core.Resilience;` (D-05) |

---

## No Analog Found

None. Every file maps to an existing analog (RESEARCH §Summary — "every primitive this phase needs already exists and was exercised in Phase 44/45; the risk is in composition, not any new algorithm").

**Composition-only risks the planner must verify (NOT missing analogs):**
| Concern | Why no exact analog | Mitigation source |
|---------|---------------------|-------------------|
| `UsePartitioner` on a multi-consumer endpoint | First use in codebase | RESEARCH Pattern 1 + Pitfall 1; verify 8.5.5 signatures in Wave 0 (A1/A2) |
| Gate-wait bound + redelivery-on-timeout | No prior gate reader | `L2ProbeRecovery` await-inside-Consume precedent (Pattern A, D-03 Resolution) |

## Metadata

**Analog search scope:** `src/Keeper/**`, `src/Orchestrator/**`, `src/BaseProcessor.Core/**`, `src/BaseConsole.Core/**`, `src/Messaging.Contracts/**`, `tests/BaseApi.Tests/**`
**Files read for excerpts:** 19 (ProcessorPipeline, ResultConsumer, KeeperReinject, KeeperUpdate, IKeeperRecoverable, L2ProbeRecovery, IL2HealthGate, RetryLoop, StepDispatcher, StepAdvancement, FaultEntryStepDispatchConsumerDefinition, FaultEntryStepDispatchConsumer, ResultConsumerDefinition, Keeper/Program, Orchestrator/Program, L2ProjectionKeys, StepCompleted, StepOutcome, EntryStepDispatch, BackupOptions, ProbeOptions, KeeperContractTests, DispatchTestKit, RetryLoopFacts)
**Pattern extraction date:** 2026-06-08
