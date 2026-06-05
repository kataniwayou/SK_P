# Phase 33: Fault-Recovery Spike (De-Risk) - Pattern Map

**Mapped:** 2026-06-05
**Files analyzed:** 2 new (1 dominant test + 1 optional close-gate)
**Analogs found:** 2 / 2 (both exact)

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` | test (RealStack E2E) | event-driven (pub/sub `Fault<T>` capture) + request-response (re-inject `Send`) | `tests/BaseApi.Tests/Orchestrator/IdempotentExactlyOnceE2ETests.cs` | exact (clone source, ~80% rig reused verbatim) |
| Two nested `IConsumer<Fault<T>>` probe classes (inside the test file) | consumer (test-local) | event-driven (fanout-exchange consume) | `FaultConsumerBindingFacts.FaultProbeConsumer` (git `3aca386`) | exact (double-`.Message` shape) |
| `scripts/phase-33-close.ps1` (OPTIONAL — only if roadmap calls for a close gate) | config (close-gate script) | batch (triple-SHA snapshot/compare) | `scripts/phase-32.1-close.ps1` | exact (proven triple-SHA protocol) |

> The spike's deliverable is essentially ONE new test file. The two `IConsumer<Fault<T>>` probes live nested/file-local inside it; the close-gate script is contingent (see "No Analog Found / Contingent" below).

## Pattern Assignments

### `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` (test, event-driven + request-response)

**Analog:** `tests/BaseApi.Tests/Orchestrator/IdempotentExactlyOnceE2ETests.cs` (the clone source — copy whole-file, then graft the 5 fault-specific patterns below)

**`<read_first>` for the planner (read before authoring; no new code needed from these):**
- `tests/BaseApi.Tests/Orchestrator/IdempotentExactlyOnceE2ETests.cs` — clone source (whole file)
- `src/Orchestrator/Consumers/ResultConsumer.cs` — result-path trip surface (`flag[m.H]` `StringGetAsync` at `:65`)
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` — dispatch-path trip surface (`StringSetAsync` output write `:178`), `flag[H]` gate (`:76-84`), result-H derivation (`:196-216`)
- `src/Messaging.Contracts/Hashing/MessageIdentity.cs` — `HashBlob` / `HashManifest` / `ComputeH` (a-priori H computation)
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `ExecutionData(string)` / `Flag(string)` (poison targets + net-zero scan)
- `src/Messaging.Contracts/{EntryStepDispatch,ExecutionResult,IExecutionCorrelated}.cs` — the inner messages + 6-id tuple unwrapped from `Fault<T>.Message`
- `src/Messaging.Contracts/{StartOrchestration,StopOrchestration}.cs` — the command messages whose faults must NOT be delivered (D-09 negative proof)
- `src/Messaging.Contracts/OrchestratorQueues.cs` — `Result = "orchestrator-result"` (result re-inject endpoint name)

---

**Imports pattern** (clone `IdempotentExactlyOnceE2ETests.cs:1-15`, verbatim — `MassTransit`, `Messaging.Contracts`, `Messaging.Contracts.Hashing`, `Messaging.Contracts.Projections`, `StackExchange.Redis`, `Xunit` are all already in scope):
```csharp
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Observability.Helpers;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Hashing;
using Messaging.Contracts.Projections;
using StackExchange.Redis;
using Xunit;
```

**Trait/collection + fixture pattern** (clone `:64-67` verbatim — RealStack-gated, excluded from hermetic suite):
```csharp
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class FaultRecoverySpikeE2ETests
```

**Rig copied 1:1 from the clone (DO NOT re-author):**
- `RealStackWebAppFactory` private nested class — clone `:510-588` (env-var host overrides RMQ 5673 / Redis 6380 / PG 5433 / OTLP 4317, `L2KeysToCleanup`, `ParentIndexMembersToSrem`, net-zero `DisposeAsync`).
- `SeedProcessorAsync` / `SeedStepAsync` / `SeedWorkflowAsync` — clone `:442-499` (GET-or-create Processor via `/by-source-hash`, Steps, Workflow).
- Embedded SourceHash reflection — clone `:95-97`.
- `PollForHealthyLivenessAsync` — clone `:345-380`.
- `ScanKeys` / `PollForNewKeyAsync` — clone `:384-438`.
- `PrewriteFlagPendingAsync` / `PollForFlagAckAsync` — clone `:227-260` (the once-only Pending seed + Ack-wait between the two re-injects).
- `BuildEffectQuery` / `CountEsHitsAsync` (+ `ElasticsearchTestClient.PollEsForLog`) — clone `:283-341` (one-effect ES proof with 8s settle window).
- `HostRedis` const + `DownstreamEffectMessage = "step output written content-addressed"` — clone `:71,501`.

---

#### GRAFT 1 — Short-lived in-test `IBusControl` adapted from Send-only to dual `Fault<T>` capture (D-02 / INTAKE-01)

**Clone-source Send-only bus** (`IdempotentExactlyOnceE2ETests.cs:265-279`) — the lifecycle bracket to preserve verbatim (`IBusControl` is NOT `IAsyncDisposable`):
```csharp
var bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
    cfg.Host("localhost", 5673, "/", h => { h.Username("guest"); h.Password("guest"); }));
await bus.StartAsync(ct);
try
{
    var endpoint = await bus.GetSendEndpoint(new Uri($"queue:{procId:D}"));
    await endpoint.Send(dispatch, ct);
}
finally
{
    await bus.StopAsync(ct);
}
```

**Adapt to** (add two `ReceiveEndpoint`s, each binding one `IConsumer<Fault<T>>`; MassTransit auto-declares a temp queue bound to the durable fanout `MassTransit:Fault--{T}` exchange — catches faults from ALL producer replicas):
```csharp
var captured = new ConcurrentBag<(string h, Guid corr, Guid wf, Guid step,
    Guid proc, Guid entry, Guid exec, object inner)>();

var bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
{
    cfg.Host("localhost", 5673, "/", h => { h.Username("guest"); h.Password("guest"); });
    cfg.ReceiveEndpoint("spike-fault-dispatch", e =>
        e.Consumer(() => new FaultDispatchProbe(captured)));   // IConsumer<Fault<EntryStepDispatch>>
    cfg.ReceiveEndpoint("spike-fault-result", e =>
        e.Consumer(() => new FaultResultProbe(captured)));     // IConsumer<Fault<ExecutionResult>>
});
await bus.StartAsync(ct);
try { /* drive trip, await capture, re-inject ×2 */ }
finally { await bus.StopAsync(ct); }
```

#### GRAFT 2 — `Fault<T>` consumer body: double-`.Message` unwrap + 6-id tuple + H (INTAKE-02)

**Source:** git `3aca386` `FaultConsumerBindingFacts.cs:42-52` (proven `context.Message.Message.WorkflowId` round-trip, no fallback). Both `EntryStepDispatch` and `ExecutionResult` implement `IExecutionCorrelated` (`EntryStepDispatch.cs:12`, `ExecutionResult.cs:11`) → the inner message carries `CorrelationId, WorkflowId, StepId, ProcessorId, EntryId, ExecutionId` + `H` (`IExecutionCorrelated.cs:11-17`; `EntryStepDispatch.cs:14-17`; `ExecutionResult.cs:13-18`).
```csharp
public Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context)
{
    var m = context.Message.Message;   // double .Message — the VERBATIM original instance
    _captured.Add((m.H, m.CorrelationId, m.WorkflowId, m.StepId, m.ProcessorId, m.EntryId, m.ExecutionId, m));
    return Task.CompletedTask;
}
```
(`FaultResultProbe` is identical with `IConsumer<Fault<ExecutionResult>>` / inner `ExecutionResult`.)

#### GRAFT 3 — WRONGTYPE deterministic live trip (D-05 / Pattern 2 & 3)

**Poison helper** (git `a6c6825` `CancelledCircuitBreakerE2ETests.cs:251-257`, verbatim — a LIST key makes the consumer's `StringSetAsync`/`StringGetAsync` throw WRONGTYPE on every attempt):
```csharp
private static async Task ArmWrongTypePoisonAsync(string key, CancellationToken ct)
{
    await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
    var db = mux.GetDatabase();
    await db.KeyDeleteAsync(key);                 // start clean (leftover string would not throw)
    await db.ListRightPushAsync(key, "poison");   // LIST type -> subsequent String op throws WRONGTYPE
}
```

**Dispatch trip target** (`EntryStepDispatchConsumer.cs:178` output write is INFRA, no catch): poison `L2ProjectionKeys.ExecutionData(MessageIdentity.HashBlob(payload))`. SampleProcessor echoes its payload, so the output content address is `HashBlob(payload)`, computable a-priori. Mirror the trip-send from git `a6c6825:142-160`:
```csharp
const string TripPayload = "spike-dispatch-trip";
var poisonedKey = L2ProjectionKeys.ExecutionData(MessageIdentity.HashBlob(TripPayload));
await ArmWrongTypePoisonAsync(poisonedKey, ct);
factory.L2KeysToCleanup.Add(poisonedKey);
var corr = NewId.NextGuid();
var entryId = MessageIdentity.EntryEntryId(corr, stepId);
var dispatch = new EntryStepDispatch(wfId, stepId, procId, Payload: JsonSerializer.Serialize(TripPayload))
{
    CorrelationId = corr, ExecutionId = Guid.Empty, EntryId = entryId,
    H = MessageIdentity.ComputeH(corr, wfId, stepId, procId, entryId),
};
// Send to queue:{procId:D} -> StringSetAsync(:178) hits WRONGTYPE every attempt -> Immediate(N)
// exhausts -> MassTransit publishes Fault<EntryStepDispatch>.
```

**Result trip target** (`ResultConsumer.cs:65` `StringGetAsync(Flag(m.H))` is the FIRST Redis op, INFRA, no catch): poison `L2ProjectionKeys.Flag(resultH)` where `resultH` is computed per GRAFT 4. **Pitfall 1 (timing):** the PROCESSOR pre-writes `flag[resultH]="Pending"` (`EntryStepDispatchConsumer.cs:210-212`) BEFORE the result is sent — poisoning up-front trips a processor-side fault instead. Plan the poison in the window after the processor pre-write but before the orchestrator consumes, OR fall to the D-06 synthetic `Fault<ExecutionResult>` publish.

#### GRAFT 4 — A-priori `resultH` computation (D-06 RESOLVED / Pattern 4)

**Source:** `MessageIdentity.cs:36-47` + `EntryStepDispatchConsumer.cs:162,196-209`. The full result-path hash chain (SampleProcessor echoes payload):
```csharp
var payload         = "spike-result-trip";
var blobHash        = MessageIdentity.HashBlob(payload);                       // output content address
var manifestJson    = JsonSerializer.Serialize(new[] { blobHash });           // ["<64hex>"]
var manifestEntryId = MessageIdentity.HashManifest(manifestJson);             // EntryStepDispatchConsumer:197
var resultH         = MessageIdentity.ComputeH(corr, wfId, stepId, procId, manifestEntryId); // :208-209
// skp:flag:{resultH} is then addressable a-priori via L2ProjectionKeys.Flag(resultH).
```
Use this same `ComputeH` result to ASSERT the captured `Fault<ExecutionResult>.Message.H == resultH` (INTAKE-02 H-match). For the dispatch path, the captured H must equal `ComputeH(corr, wfId, stepId, procId, EntryEntryId(corr, stepId))`.

#### GRAFT 5 — Re-inject verbatim by type via `GetSendEndpoint` + `Send`, twice (D-07/D-08 / INTAKE-04, PROBE-06)

**Source:** clone `:272-273` (dispatch endpoint URI), `OrchestratorQueues.cs:16` (`Result = "orchestrator-result"`). Forward the EXTRACTED `Fault<T>.Message` instance VERBATIM (same H — NOT `Publish`, no orchestrator round-trip):
```csharp
// dispatch:
var inner = (EntryStepDispatch)cap.inner;
var ep = await bus.GetSendEndpoint(new Uri($"queue:{inner.ProcessorId:D}"));
// result:
var ep2 = await bus.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}")); // queue:orchestrator-result

// Duplicate-collapse proof (D-08): one Pending seed, Send, wait for Ack, Send again.
await PrewriteFlagPendingAsync(cap.h);   // clone :227-232 — ONCE only (re-arming leaks the dup)
await ep.Send(inner, ct);                 // delivery 1 -> effect, flips flag[H] Pending->Ack
await PollForFlagAckAsync(cap.h, ct);     // clone :237-260
await ep.Send(inner, ct);                 // delivery 2 -> flag==Ack -> dropped by receiver gate
```
Then assert ONE downstream effect via `BuildEffectQuery` + `PollEsForLog` + `CountEsHitsAsync == 1` (clone `:283-341`). The receiver gate is the SURVIVING Phase-31 `flag[H]` CAS — dispatch side `EntryStepDispatchConsumer.cs:76-84`, result side `ResultConsumer.cs:65-72` — the spike/Keeper adds nothing (PROBE-06).

#### GRAFT 6 — Negative command-fault proof (D-09 / INTAKE-01 negative)

Publish `Fault<StartOrchestration>` + `Fault<StopOrchestration>` to the broker; assert the spike's two execution-fault consumers record ZERO captures over a settle window. `StartOrchestration` / `StopOrchestration` carry only `WorkflowIds` + `CorrelationId` (`StartOrchestration.cs:4-6`, `StopOrchestration.cs:4-6`) — they are `ICorrelated`, NOT `IExecutionCorrelated`, and the spike binds only `Fault--EntryStepDispatch` / `Fault--ExecutionResult` exchanges, so structurally these never route to the spike. Make it observable: `captured.Count == 0` after a settle delay (mirror the `CountEsHitsAsync` 8s window idiom).

---

## Shared Patterns

### Net-zero teardown (close-gate triple-SHA must hold)
**Source:** `IdempotentExactlyOnceE2ETests.cs:203-218` (+ `RealStackWebAppFactory.DisposeAsync` `:570-587`)
**Apply to:** the spike test (every poison/data/flag key + the workflow stop)
```csharp
try { await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct); }
catch { /* best-effort net-zero teardown */ }
foreach (var key in ScanKeys("data:*")) if (!dataKeysBefore.Contains(key)) factory.L2KeysToCleanup.Add(key);
foreach (var key in ScanKeys("flag:*")) if (!flagKeysBefore.Contains(key)) factory.L2KeysToCleanup.Add(key);
// plus: factory.L2KeysToCleanup.Add(poisonedKey) for each WRONGTYPE LIST armed.
```
All poison keys here are TTL'd or content-addressed string/flag families captured by the unfiltered `redis-cli --scan` SHA — register them all or the gate's BEFORE==AFTER invariant breaks.

### Identity hashing (single canonical path)
**Source:** `src/Messaging.Contracts/Hashing/MessageIdentity.cs:36-51`
**Apply to:** every H/EntryId/content-address the spike computes test-side (`ComputeH`, `HashBlob`, `HashManifest`, `EntryEntryId`) — NEVER re-implement SHA-256; any second canonicalization desyncs H from the receiver gate (the whole collapse proof rides identical H).

### Receiver dedup gate (ride, do not add)
**Source:** processor `EntryStepDispatchConsumer.cs:76-84,218-225`; orchestrator `ResultConsumer.cs:65-72`
**Apply to:** the duplicate-collapse assertion — both `flag[H]=="Ack"` CAS gates SURVIVED the 32.1 revert byte-intact. The spike relies ONLY on these; Keeper/spike adds no dedup of its own (PROBE-06).

---

## No Analog Found / Contingent

| File | Role | Data Flow | Reason / Disposition |
|------|------|-----------|----------------------|
| `scripts/phase-33-close.ps1` | config (close-gate) | batch | OPTIONAL — only if the roadmap calls for a phase-33 close gate. If so, clone `scripts/phase-32.1-close.ps1` (337 lines, the proven v3.6.0 triple-SHA gate): zero-warning Debug+Release build, idempotent GET-or-create Processor seed, 3×GREEN full-suite cadence, unfiltered `redis-cli --scan` + `rabbitmqctl list_queues` + `psql \l` SHA-256 BEFORE==AFTER. Header note: the spike's poison keys are TTL'd/scan-cleaned by the test's `L2KeysToCleanup` net-zero teardown, NOT the gate; rebuild `processor-sample orchestrator baseapi-service` before the live run (embedded SourceHash must match). The live half is `autonomous: false` operator runbook (Phase-31/32 precedent). |

> The two `IConsumer<Fault<T>>` probe classes are NOT a "no analog" gap — their exact shape is git `3aca386` `FaultConsumerBindingFacts.FaultProbeConsumer` (GRAFT 2), nested file-local in the new test.

## Metadata

**Analog search scope:** `tests/BaseApi.Tests/Orchestrator/` (clone source + git-recovered reverted tests), `src/Messaging.Contracts/` (wire contracts + keys + hashing), `src/Orchestrator/Consumers/` + `src/BaseProcessor.Core/Processing/` (trip surfaces + receiver gates), `scripts/` (close-gate analog)
**Files scanned:** 12 read (1 clone source, 2 git-recovered, 9 reference) + 2 git `git show` recoveries
**Git refs recovered:** `3aca386:FaultConsumerBindingFacts.cs` (double-`.Message`), `a6c6825:CancelledCircuitBreakerE2ETests.cs` (`ArmWrongTypePoisonAsync` + WRONGTYPE recipe)
**Pattern extraction date:** 2026-06-05
