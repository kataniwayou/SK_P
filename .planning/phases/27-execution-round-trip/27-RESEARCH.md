# Phase 27: Execution Round-Trip - Research

**Researched:** 2026-06-01
**Domain:** .NET 8 / MassTransit 8.5.5 / RabbitMQ / StackExchange.Redis processor-side message round-trip (brownfield mirror)
**Confidence:** HIGH (every API verified against repo source + MassTransit 8.x docs)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

All D-01..D-17 from 27-CONTEXT.md are LOCKED. The load-bearing ones:

- **D-01:** Dispatch consumer registered WITHOUT a static auto-bound receive endpoint (suppress default endpoint auto-config for this consumer — the `queue:{Id:D}` name is unknown until Loop A resolves).
- **D-02:** In the startup orchestrator, after Loop A+B resolve, dynamically bind via `IBus`/`IReceiveEndpointConnector.ConnectReceiveEndpoint("queue:{Id:D}", …)` — durable, plain-named (competing-consumer), `UseMessageRetry(r => r.Immediate(3))`, dispatch consumer attached, `await handle.Ready`.
- **D-03 (load-bearing order):** `ConnectReceiveEndpoint` + `await Ready` THEN `context.MarkHealthy()`. Healthy→L2 write happens structurally after the bind because the heartbeat gates on `IsHealthy`.
- **D-04:** Restarting/unhealthy replica does not bind → does not consume → durable queue holds dispatches.
- **D-05:** Port a minimal SSRF-locked `Json.Schema` validator INTO `BaseProcessor.Core` (firewall — cannot reference `BaseApi.Service`). Mirror `JsonSchemaConfig.DefaultOptions` + flat-error flattening + parse-guard.
- **D-06:** null OR whitespace definition → skip validation. Non-empty-but-unparseable → `Failed` result via parse-guard, never a host crash.
- **D-07:** Input read from `L2[data(entryId)]` (existence-checked first) via the soft-dep `IConnectionMultiplexer`. `Payload` = config, never input. Non-empty `inputDefinition` + missing/empty entryId → `Failed` before `ProcessAsync`.
- **D-08:** `public sealed record ProcessResult(string OutputData)` — output-data string only, no outcome.
- **D-09:** Framework owns ALL outcomes (output-validate→mint entryId→write L2→mint executionId→`Completed`; fail→`Failed`+nothing-written; empty list→ack-only; `OperationCanceledException`→`Cancelled`; other exception→`Failed`+message).
- **D-10:** Concrete cannot emit business outcomes; per-item business-failure deferred.
- **D-11:** `ExecutionResult` field map — WorkflowId/StepId/ProcessorId inherited; CorrelationId copied from dispatch BODY; ExecutionId minted per-result `NewId.NextGuid()`; EntryId = newly-minted output entryId on success.
- **D-12:** Minted output entryId → `ExecutionResult.EntryId` closes the step-to-step chain through L2 (no output on the wire).
- **D-13:** Failed/Cancelled → `EntryId = Guid.Empty` (executionId still minted).
- **D-14:** Results sent to `OrchestratorQueues.Result` (`"orchestrator-result"`) ONE-BY-ONE via `Send` to the named queue.
- **D-15:** Ack-after-send; business-ack / infra-throw mirror of `ResultConsumer`; `Immediate(3)`. L2-output-write business-vs-infra line = research call (see Pitfall 5 / recommendation below).
- **D-16:** Body CorrelationId inherited from BaseConsole correlation filters + set explicitly per D-11.
- **D-17:** Execution-data L2 TTL is its OWN configurable seconds value in the `"Processor"` section, distinct from liveness `Ttl`, applied on every `L2[data(newEntryId)]` write.

### Claude's Discretion
- Exact file/class layout under `src/BaseProcessor.Core/` (mirror existing `Processing`/`Liveness`/`Startup`/`Configuration` folders).
- HOW the dispatch consumer is registered to avoid static auto-binding (`ExcludeFromConfigureEndpoints` vs manual consumer factory) — confirm against MassTransit semantics. **See Decision A below: `ExcludeFromConfigureEndpoints` is the verified-correct choice.**
- The precise business-vs-infra classification of an L2 output-write fault (mirror `WorkflowLifecycle.IsBusiness`). **See Pitfall 5 recommendation.**
- CONFIG-02 TTL key name, default, and whether on `ProcessorLivenessOptions` or a sibling. **See CONFIG-02 section: recommend `ExecutionDataTtlSeconds` on the existing `ProcessorLivenessOptions`.**
- Test strategy (in-memory harness + real/fake Redis). **See Validation Architecture.**

### Deferred Ideas (OUT OF SCOPE)
- Per-item business-failure within a batch (a throwing `ProcessAsync` fails the whole dispatch as one `Failed`).
- Shared `Schema.Validation` library extraction (port, don't share, this milestone).
- Echoing inbound entryId onto Failed/Cancelled (use `Guid.Empty`).
- SourceHash embed target, `Processor.Sample`, Dockerfile/compose, real-stack E2E + close gate — **Phase 28**.
- Config re-validation in the processor; cleanup-on-read of execution-data keys; output-data on the wire; real transform logic.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| EXEC-01 | Consume `EntryStepDispatch` on durable `queue:{processorId:D}` competing-consumer, bound only once Healthy, Healthy→L2 after bind | Decision A (runtime bind), Pattern 1 (sequencing). `IReceiveEndpointConnector.ConnectReceiveEndpoint` + `await handle.Ready` then `MarkHealthy()`. RabbitMQ queues durable-by-default; plain name = competing-consumer. |
| EXEC-02 | Input read from `L2[data(entryId)]` (existence-checked); `Payload`=config | Pattern 2. `db.StringGetAsync(L2ProjectionKeys.ExecutionData(entryId))` + `RedisValue.IsNullOrEmpty` mirror of `WorkflowLifecycle`/`ProcessorLivenessHeartbeat`. |
| EXEC-03 | Validate input vs `inputDefinition` when present; empty skips; non-empty def + missing entryId → Failed | Decision B (ported validator), D-06/D-07 semantics. |
| EXEC-04 | Sole `abstract Task<IReadOnlyList<ProcessResult>> ProcessAsync(string inputData, string config, CancellationToken ct)` seam; `config`=Payload | Seam already declared in `BaseProcessor.cs`; firm `ProcessResult` to `(string OutputData)`. |
| EXEC-05 | Per-result: output-validate vs `outputDefinition` (empty=valid); success→mint entryId + write `L2[data(newEntryId)]` w/ TTL; fail→`Failed`, nothing written | Pattern 3 (result builder), Decision B, CONFIG-02 TTL. |
| EXEC-06 | Mint per-result executionId, stamp shared ids, build one `ExecutionResult`; concrete writes no id/L2/bus code | D-11 field map, `NewId.NextGuid()`. |
| EXEC-07 | Send to `queue:orchestrator-result` one-by-one (loop, never batched list) | Pattern 4. `ISendEndpointProvider.GetSendEndpoint(new Uri("queue:orchestrator-result"))` mirror of `StepDispatcher`. |
| EXEC-08 | Empty list + no exception/cancel → ack-only (no message); Failed (incl caught exception) + Cancelled always sent | D-09 outcome matrix. |
| EXEC-09 | Ack only after all sends; infra fault (e.g. send failure) throws → `Immediate(3)`, mirror business-ack/infra-throw | Pattern 5, Pitfall 5. |
| EXEC-10 | Correlation inherited from BaseConsole — body CorrelationId flows from dispatch into log scope + onto every `ExecutionResult` | Decision C (correlation flow). Envelope auto via outbound filter; body field set explicitly. |
| CONFIG-02 | Execution-data L2 keys have own configurable TTL (seconds) | CONFIG-02 section: `ExecutionDataTtlSeconds` on `ProcessorLivenessOptions`. |
</phase_requirements>

## Summary

This is a brownfield phase: every piece has a verified in-repo precedent. The processor-half mirrors the orchestrator-half almost line-for-line. The single genuinely new mechanism is **runtime dynamic receive-endpoint binding** (EXEC-01) — the `queue:{Id:D}` name is only known after identity resolves, so the dispatch consumer must be registered WITHOUT a static endpoint and bound after the bus is running. MassTransit 8.5.5 supports this exactly via `IReceiveEndpointConnector.ConnectReceiveEndpoint(queueName, (ctx, cfg) => cfg.ConfigureConsumer<T>(ctx))` returning a `HostReceiveEndpointHandle` whose `.Ready` task is awaited. The consumer is registered with `.AddConsumer<DispatchConsumer>().ExcludeFromConfigureEndpoints()` so the unconditional `c.ConfigureEndpoints(ctx)` already inside `AddBaseConsoleMessaging` does NOT auto-create a wrong-named queue at bus start. [VERIFIED: ctx7 masstransit_massient docs + repo MessagingServiceCollectionExtensions.cs:58]

The remaining four open questions resolve cleanly to existing patterns: L2 I/O mirrors `ProcessorLivenessHeartbeat` (`IConnectionMultiplexer.GetDatabase()` + `StringGetAsync`/`StringSetAsync` with `RedisValue.IsNullOrEmpty` existence check); JSON Schema validation is a near-verbatim port of `JsonSchemaConfig` + `PayloadConfigSchemaValidator` (JsonSchema.Net 9.2.1, already CPM-pinned) with the SSRF static-ctor side-effect preserved; outcome/`ExecutionResult` construction follows `StepDispatcher`'s field-threading + `NewId.NextGuid()` minting; ack/retry mirrors `ResultConsumer`/`ResultConsumerDefinition` (`UseMessageRetry(Immediate(3))`, business-ack/infra-throw split per `WorkflowLifecycle.IsBusiness`).

**Primary recommendation:** Build a `BaseProcessor.Core/Processing/EntryStepDispatchConsumer` (the framework's `IConsumer<EntryStepDispatch>`) that orchestrates input-read → validate → invoke `ProcessAsync` → per-result output-validate/mint/write → build → one-by-one send; register it with `.AddConsumer<...>().ExcludeFromConfigureEndpoints()`; insert the `ConnectReceiveEndpoint` bind step into `ProcessorStartupOrchestrator` between Loop B and `MarkHealthy()`; port a `BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator` static helper; add `ExecutionDataTtlSeconds` to `ProcessorLivenessOptions`.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Dispatch consume | Processor (BaseProcessor.Core consumer) | RabbitMQ (durable queue) | The processor owns the `IConsumer<EntryStepDispatch>`; RabbitMQ owns durability/competing-consumer delivery. |
| Runtime endpoint bind | Processor startup orchestrator | MassTransit bus | The bind is a startup-sequencing concern (after Healthy), executed against the running bus. |
| Input/output L2 I/O | Processor (soft-dep Redis) | Redis L2 | Same `IConnectionMultiplexer` the heartbeat uses; Redis is the data plane. |
| Schema validation | Processor (ported validator) | — | Firewalled from BaseApi.Service; validation runs in-process. |
| Id minting | Processor framework | MassTransit `NewId` | `NewId.NextGuid()` is the sequential-GUID source. |
| Result send | Processor (`ISendEndpointProvider`) | RabbitMQ (orchestrator-result queue) | Mirror of `StepDispatcher` Send-to-named-queue. |
| Outcome ownership | Processor framework | — | Concrete only produces `ProcessResult(OutputData)`; framework determines `Completed`/`Failed`/`Cancelled`. |
| Correlation | BaseConsole.Core filters | Processor (explicit body copy) | Envelope auto-stamped by outbound filter; body field set explicitly per D-11. |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 | Bus, consumer, `IReceiveEndpointConnector`, `ISendEndpointProvider`, `NewId` | CPM-pinned; last Apache-2.0 line (do NOT bump to 9.x — commercial). [VERIFIED: Directory.Packages.props:137] |
| MassTransit.RabbitMQ | 8.5.5 | RabbitMQ transport; durable queue defaults | CPM-pinned. [VERIFIED: Directory.Packages.props:138] |
| StackExchange.Redis | 2.13.1 | `IConnectionMultiplexer` L2 I/O (soft-dep) | CPM-pinned; already used by heartbeat. [VERIFIED: Directory.Packages.props:131] |
| JsonSchema.Net (`Json.Schema`) | 9.2.1 | SSRF-locked schema validation (PORT into BaseProcessor.Core) | CPM-pinned; already used by BaseApi.Service. [VERIFIED: Directory.Packages.props:90] |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Microsoft.Extensions.Options | (framework) | `IOptions<ProcessorLivenessOptions>` binding | CONFIG-02 TTL knob. |
| Microsoft.Extensions.TimeProvider.Testing | 8.10.0 | `FakeTimeProvider` for tests | Validation Architecture. |
| xunit.v3 / xunit.v3.assert | 3.2.2 | Test framework (MTP) | All tests. [VERIFIED: Directory.Packages.props:121] |
| NSubstitute | 5.3.0 | `IConnectionMultiplexer`/`IDatabase`/`ConsumeContext` fakes | Mirror `OrchestratorTestStubs`. [VERIFIED: Directory.Packages.props:120] |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `ExcludeFromConfigureEndpoints()` | Omit `AddConsumer`, construct consumer manually in `ConnectReceiveEndpoint` via `cfg.Consumer<T>(() => new T(...))` | Manual factory loses DI; `ExcludeFromConfigureEndpoints` keeps `AddConsumer` DI registration so `cfg.ConfigureConsumer<T>(registrationContext)` resolves the consumer + dependencies from the container at bind time. **Prefer ExcludeFromConfigureEndpoints.** |
| Port `Json.Schema` validator | Shared `Schema.Validation` lib | Deferred (D-05) — port keeps the firewall and avoids new-project churn this milestone. |

**Installation (BaseProcessor.Core.csproj — add ONE PackageReference, version from CPM):**
```xml
<PackageReference Include="JsonSchema.Net" />
```
(MassTransit + StackExchange.Redis already referenced — see BaseProcessor.Core.csproj:35-36. NO `Version=` per CPM. [VERIFIED: BaseProcessor.Core.csproj])

**Version verification:** All four core packages are already CPM-pinned in `Directory.Packages.props`; no `npm view`/registry check applies (.NET CPM). Versions read directly from the repo: MassTransit 8.5.5, StackExchange.Redis 2.13.1, JsonSchema.Net 9.2.1. [VERIFIED: Directory.Packages.props]

## Architecture Patterns

### System Architecture Diagram

```
  orchestrator (Send, NOT Publish)
        │  EntryStepDispatch  { WorkflowId, StepId, ProcessorId, Payload, CorrelationId, ExecutionId(empty), EntryId }
        ▼
  RabbitMQ  queue:{processorId:D}  (DURABLE, competing-consumer, bound ONLY when Healthy)
        │
        ▼
  ┌─────────────────────────── EntryStepDispatchConsumer (BaseProcessor.Core) ───────────────────────────┐
  │                                                                                                       │
  │  1. inputDefinition present?                                                                          │
  │       ├─ null/whitespace → skip input validation, inputData = "" (or L2 read if entryId present)      │
  │       └─ present → read L2[data(EntryId)] ──existence-check──▶ missing/empty? → Failed (no ProcessAsync)│
  │                          │ present                                                                    │
  │                          ▼ validate inputData vs inputDefinition ── invalid? → Failed (no ProcessAsync)│
  │                          │ valid                                                                      │
  │  2. invoke abstract ProcessAsync(inputData, config=Payload, ct)  ── throws? ─┐                        │
  │       │ returns IReadOnlyList<ProcessResult(OutputData)>                      │ OCE→Cancelled          │
  │       │                                                                       │ other→Failed(message) │
  │       ▼ empty list → ack-only (NO message)                                    │                        │
  │  3. FOREACH result:                                                           ▼                        │
  │       outputDefinition present? validate OutputData ── invalid? → Failed (EntryId=Empty, nothing written)│
  │            │ valid/empty-def                                                                          │
  │            ▼ mint newEntryId (NewId) → write L2[data(newEntryId)] = OutputData (TTL = ExecutionDataTtl)│
  │              mint executionId (NewId) → build ExecutionResult(Completed, EntryId=newEntryId, …)        │
  │  4. FOREACH built ExecutionResult: Send to queue:orchestrator-result (ONE-BY-ONE)                     │
  │  5. all sends complete → return (ACK). Send/L2-infra fault → THROW → Immediate(3) → _error            │
  └───────────────────────────────────────────────────────────────────────────────────────────────────────┘
        │  ExecutionResult { …, Outcome, CorrelationId(=dispatch body), ExecutionId(minted), EntryId(=newEntryId) }
        ▼  (Send, NOT Publish)
  RabbitMQ  queue:orchestrator-result  →  orchestrator ResultConsumer  →  StepDispatcher dispatches NEXT step
                                                                            pointing at L2[data(newEntryId)] as ITS input
```

### Recommended Project Structure
```
src/BaseProcessor.Core/
├── Processing/
│   ├── BaseProcessor.cs              # EXISTS — abstract ProcessAsync seam (unchanged)
│   ├── ProcessResult.cs             # FIRM UP to: public sealed record ProcessResult(string OutputData);
│   └── EntryStepDispatchConsumer.cs # NEW — IConsumer<EntryStepDispatch>; the round-trip orchestration
├── Validation/                       # NEW folder
│   └── ProcessorJsonSchemaValidator.cs  # NEW — ported SSRF-locked Json.Schema helper
├── Startup/
│   └── ProcessorStartupOrchestrator.cs  # EXTEND — insert ConnectReceiveEndpoint bind before MarkHealthy
├── Configuration/
│   └── ProcessorLivenessOptions.cs  # EXTEND — add ExecutionDataTtlSeconds
├── Identity/                         # unchanged (IProcessorContext / ProcessorContext)
├── Liveness/                         # unchanged (ProcessorLivenessHeartbeat — the L2 I/O mirror)
└── DependencyInjection/
    └── BaseProcessorServiceCollectionExtensions.cs  # EXTEND — AddConsumer(...).ExcludeFromConfigureEndpoints()
                                                      #          + register BaseProcessor (abstract) resolution seam
```

### Pattern 1: Runtime endpoint bind, then MarkHealthy (EXEC-01, D-02/D-03 — the load-bearing order)

**What:** After Loop A+B resolve identity+definitions, connect the dispatch receive endpoint to the running bus, await its readiness, THEN mark Healthy. Because the heartbeat writes L2 only when `IsHealthy` (see `ProcessorLivenessHeartbeat.cs:70`), "Healthy in L2 after bind" is structurally guaranteed.

**Where:** In `ProcessorStartupOrchestrator.ExecuteAsync`, replace the completion block at lines 136-139.

**Example:**
```csharp
// Source: ctx7 masstransit_massient /configuration "Dynamically Connect a Receive Endpoint"
//         + repo ProcessorStartupOrchestrator.cs:136-139 (the MarkHealthy seam this extends)

// --- Completion (D-02/D-03): bind the dispatch endpoint BEFORE MarkHealthy ---
var queueName = $"{context.Id!.Value:D}";   // plain name → competing-consumer; Send target is queue:{id:D}
var handle = endpointConnector.ConnectReceiveEndpoint(queueName, (ctx, cfg) =>
{
    // RabbitMQ receive endpoints are DURABLE + AutoDelete=false BY DEFAULT (verified) — explicit for clarity:
    if (cfg is MassTransit.RabbitMqReceiveEndpointConfigurator rmq)
    {
        rmq.Durable = true;        // survive broker restart (EXEC-01 durable)
        rmq.AutoDelete = false;    // NOT temporary — dispatches persist across processor restart (D-04)
    }
    cfg.UseMessageRetry(r => r.Immediate(3));      // mirror ResultConsumerDefinition (D-02/D-15)
    cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx);   // attach the framework consumer (DI-resolved)
});

await handle.Ready;   // queue declared + consumer attached BEFORE Healthy (D-03)

context.MarkHealthy(); // now the heartbeat's IsHealthy gate opens → "Healthy" lands in L2 AFTER the bind
gate.MarkReady();
```

**Notes / version-specific:**
- `endpointConnector` is `IReceiveEndpointConnector`, resolvable from DI; `IBus` also implements it. The `ctx` passed to the lambda is the `IReceiveEndpointConnector`-supplied registration context that `ConfigureConsumer<T>(ctx)` needs — it is provided by the API, not separately resolved. [VERIFIED: ctx7 masstransit_massient — `connector.ConnectReceiveEndpoint("queue-name", (context, cfg) => cfg.ConfigureConsumer<MyConsumer>(context))`]
- The `queue:` scheme prefix is for the SENDER (`Send` URI). The receive endpoint binds the BARE name `{id:D}` — confirmed by `FireDispatchTests.cs:67` ("`queue:{processorId:D}` ↔ `ReceiveEndpoint(\"{processorId:D}\")`") and `OrchestratorQueues.cs:13` ("a sender prepends the `queue:` URI scheme … stored WITHOUT the scheme prefix").
- `await handle.Ready` returns when the queue is declared and the consumer attached. [VERIFIED: ctx7 + WebSearch GitHub discussions]
- The cast to `RabbitMqReceiveEndpointConfigurator` is optional defensive clarity — durable/auto-delete defaults already satisfy the requirement. If the cast proves brittle across the in-memory test harness (in-memory has no RabbitMQ configurator), guard it with `is … rmq` (shown) so it no-ops under the harness. **Flag for planner:** confirm the exact RabbitMQ configurator type name at implementation time (`RabbitMqReceiveEndpointConfigurator` vs interface `IRabbitMqReceiveEndpointConfigurator`) — defaults make the cast non-load-bearing.

### Pattern 2: L2 input read + output write (EXEC-02/05, CONFIG-02)

**What:** Mirror `ProcessorLivenessHeartbeat`'s `IConnectionMultiplexer` usage exactly: `GetDatabase()` then `StringGetAsync`/`StringSetAsync`. Existence-check with `RedisValue.IsNullOrEmpty` (the same idiom `WorkflowLifecycle` uses).

**Example:**
```csharp
// Source: repo ProcessorLivenessHeartbeat.cs:86-100 (write) + WorkflowLifecycle.cs:40-41 (read+existence-check)
var db = redis.GetDatabase();   // soft-dep IConnectionMultiplexer; a Redis fault THROWS here (infra)

// INPUT read (EXEC-02) — existence-check first (mirror WorkflowLifecycle.cs:41)
var raw = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(dispatch.EntryId));
if (raw.IsNullOrEmpty)
{
    // D-07: non-empty inputDefinition + missing/empty entryId → Failed BEFORE ProcessAsync
    return Failed("Input data not found in L2 for entryId.");
}
var inputData = raw.ToString();

// OUTPUT write (EXEC-05 / CONFIG-02) — raw OutputData string verbatim, NO JSON wrapping (D-08/D-09)
var newEntryId = NewId.NextGuid();
await db.StringSetAsync(
    L2ProjectionKeys.ExecutionData(newEntryId),
    result.OutputData,                                       // raw string — orchestrator never reads it (D-12)
    expiry: TimeSpan.FromSeconds(options.ExecutionDataTtlSeconds));   // CONFIG-02
```

**Confirmations:**
- Existence check: `RedisValue.IsNullOrEmpty` is the verified idiom (`WorkflowLifecycle.cs:41` uses `rootRaw.IsNullOrEmpty`; `HasValue` is the inverse). [VERIFIED: repo]
- Output written as the raw `ProcessResult.OutputData` string verbatim, NO wrapper. `ExecutionResult` has NO output field (`ExecutionResult.cs:7-9` comment: "deliberately NO output/payload field"); the orchestrator's `ResultConsumer` never reads `L2[data]` — it forwards `EntryId` onto the NEXT dispatch whose processor reads it as input. So nothing in the orchestrator/contracts expects a wrapper. [VERIFIED: ExecutionResult.cs + ResultConsumer.cs + StepDispatcher.cs]
- `StringSetAsync(key, value, expiry: TimeSpan)` is the exact signature the heartbeat uses (`ProcessorLivenessHeartbeat.cs:97-100`). [VERIFIED: repo]

### Pattern 3: Result builder + id minting (EXEC-05/06/10, D-11)

**Example:**
```csharp
// Source: repo StepDispatcher.cs:17-22 (field threading) + EntryStepDispatch.cs (NewId minting) + D-11
var executionResult = new ExecutionResult(
        dispatch.WorkflowId,    // inherited
        dispatch.StepId,        // inherited
        dispatch.ProcessorId,   // inherited
        StepOutcome.Completed)  // framework-determined
{
    CorrelationId = dispatch.CorrelationId,   // D-11: copied from dispatch BODY (also re-stamped on envelope by filter)
    ExecutionId   = NewId.NextGuid(),         // D-11: minted per-result (sequential GUID)
    EntryId       = newEntryId,               // D-11: the L2[data(newEntryId)] write key (D-12 chain linkage)
};
// Failed/Cancelled variant (D-13): EntryId = Guid.Empty, ExecutionId still minted, ErrorMessage/CancellationMessage set.
```

### Pattern 4: One-by-one Send to orchestrator-result (EXEC-07, D-14)

**Example:**
```csharp
// Source: repo StepDispatcher.cs:25-26 (GetSendEndpoint + Send) + OrchestratorQueues.cs:16 + D-14
var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
foreach (var executionResult in builtResults)   // ONE-BY-ONE — individual messages, never a batched list
{
    await endpoint.Send(executionResult, context.CancellationToken);   // infra fault here THROWS (EXEC-09)
}
// only after ALL sends complete does Consume return → ACK (D-15)
```
- `OrchestratorQueues.Result` == `"orchestrator-result"`; `new Uri("queue:orchestrator-result")` is the Send target. [VERIFIED: OrchestratorQueues.cs:16]
- `ISendEndpointProvider` is injected (the consumer can take `ConsumeContext` which IS an `ISendEndpointProvider`, OR inject `ISendEndpointProvider` directly — `StepDispatcher` injects it as a ctor dependency). **Recommend:** use `context` (the `ConsumeContext`) as the send-endpoint provider inside `Consume`, matching the orchestrator's per-consume scope — but injecting `ISendEndpointProvider` is equally valid and what `StepDispatcher` does. Either resolves the same endpoint.

### Pattern 5: Consume ack/retry + business-vs-infra split (EXEC-08/09, D-15)

**What:** Returning normally from `Consume` = ack; throwing = nack → `Immediate(3)` retry (configured on the runtime endpoint, Pattern 1) → `_error`. Business outcomes (input-missing, validation-fail, empty-list, caught transform exception) are handled IN-consumer and acked (the `Failed`/`Cancelled`/no-message IS the business signal). Only genuine infra faults throw.

**Mirror:** `ResultConsumer.cs:43-64` (returns normally = ack; an infra `Send` fault propagates) + `ResultConsumerDefinition.cs:31` (`UseMessageRetry(r => r.Immediate(3))`) + `WorkflowLifecycle.IsBusiness`/`IsInfra` (`WorkflowLifecycle.cs:154-161`).

### Anti-Patterns to Avoid
- **Publishing instead of Sending.** Use `Send` to the named queue, never `Publish` (mirror `StepDispatcher` D-10). Publish would fan-out, breaking competing-consumer-once delivery.
- **Leaving the dispatch consumer in `ConfigureEndpoints`.** `AddBaseConsoleMessaging` calls `c.ConfigureEndpoints(ctx)` UNCONDITIONALLY (`MessagingServiceCollectionExtensions.cs:58`). A plain `AddConsumer<EntryStepDispatchConsumer>()` would auto-create a `entry-step-dispatch` (kebab-cased type-name) queue at bus start — the WRONG name. MUST chain `.ExcludeFromConfigureEndpoints()`.
- **JSON-wrapping the output.** Write the raw `OutputData` string (D-08/D-09). A wrapper would desync the L2 input read of the next step.
- **Catching infra faults and acking.** A `Send` failure or a load-bearing L2-write fault must THROW so `Immediate(3)` retries (never-lose-output goal). Only malformed-definition / missing-input / transform-exception are business-acked.
- **Mutating the `ProcessResult` to carry an outcome.** D-08/D-10: `ProcessResult(string OutputData)` only; the framework owns all outcomes.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Runtime endpoint binding | A custom queue-declare + consumer-loop | `IReceiveEndpointConnector.ConnectReceiveEndpoint` + `await handle.Ready` | MassTransit owns queue topology, consumer pipeline, retry middleware, shutdown. |
| Sequential id minting | `Guid.NewGuid()` ad-hoc | `NewId.NextGuid()` | Sequential GUIDs (index-friendly); matches the dispatch's own minting (`EntryStepDispatch.cs` comment). |
| JSON Schema evaluation | A hand-rolled validator | Ported `Json.Schema` (`JsonSchema.FromText` + `Evaluate`) | Edge cases (dialects, `$ref`, formats); SSRF lockdown is non-negotiable. |
| Ack/retry semantics | Manual nack + redelivery counting | `UseMessageRetry(r => r.Immediate(3))` + return/throw | MassTransit owns the retry filter + `_error` move. |
| Correlation propagation | Manual envelope stamping | BaseConsole.Core outbound filter (already bus-wide) | `OutboundCorrelationSendFilter` already stamps the envelope from the ambient (set inbound). |

**Key insight:** Every "framework-owned" responsibility in this phase already has a battle-tested implementation in the orchestrator or BaseConsole.Core — the work is wiring/mirroring, not inventing.

## Common Pitfalls

### Pitfall 1: Wrong-named auto-bound queue at bus start
**What goes wrong:** A plain `AddConsumer<EntryStepDispatchConsumer>()` + the unconditional `ConfigureEndpoints(ctx)` inside `AddBaseConsoleMessaging` creates a static `entry-step-dispatch` queue at bus start — the orchestrator Sends to `queue:{id:D}`, so dispatches land nowhere.
**Why it happens:** `MessagingServiceCollectionExtensions.cs:58` always calls `c.ConfigureEndpoints(ctx)`.
**How to avoid:** Register with `.AddConsumer<EntryStepDispatchConsumer>().ExcludeFromConfigureEndpoints()`. The runtime `ConnectReceiveEndpoint` binds the correct `{id:D}` name.
**Warning signs:** A queue named `entry-step-dispatch` (or similar kebab) appearing in `rabbitmqctl list_queues`; dispatches accumulating unconsumed in `queue:{id:D}`.

### Pitfall 2: SSRF static-ctor side-effect lost on port
**What goes wrong:** Porting `DefaultOptions` without the static ctor (or never referencing it) silently regresses the SSRF lockdown — external `$ref` tokens could trigger outbound fetches.
**Why it happens:** The lockdown lives in a STATIC CTOR (`JsonSchemaConfig.cs:22-28`) that runs only on first member access. If no member is ever touched, the cctor never fires.
**How to avoid:** Preserve the exact shape — a static `ProcessorJsonSchemaValidator` class with `static ctor { Dialect.Default = Dialect.Draft202012; SchemaRegistry.Global.Fetch = (_,_) => null; }` and a `public static EvaluationOptions DefaultOptions { get; } = new() { OutputFormat = OutputFormat.List };`. The validate method MUST reference `DefaultOptions` so the cctor fires before any `Evaluate`. [VERIFIED: JsonSchemaConfig.cs + PayloadConfigSchemaValidator.cs:82-84]
**Warning signs:** A schema with an `http://` `$ref` evaluating without error/timeout (lockdown would null-fetch and fail closed).

### Pitfall 3: `Dialect.Default`/`SchemaRegistry.Global` are PROCESS-WIDE
**What goes wrong:** Both `JsonSchemaConfig` (in BaseApi.Service) and the new ported validator set the SAME global statics. In a test process that loads both assemblies, last-cctor-wins — but both set IDENTICAL values (`Draft202012` + no-op fetch), so it's safe. The risk is only if the ported validator sets DIFFERENT values.
**How to avoid:** Set EXACTLY the same two globals. Do not diverge the dialect or fetcher.
**Warning signs:** Cross-assembly dialect flakiness in the combined `BaseApi.Tests` process (it references both BaseApi.Service AND BaseProcessor.Core).

### Pitfall 4: Empty-definition vs unparseable-definition conflation (D-06)
**What goes wrong:** Treating an empty `inputDefinition` as a parse error (host crash) or treating an unparseable one as "skip" (silent pass).
**Why it happens:** Two distinct branches: null/whitespace → SKIP; non-empty-unparseable → `Failed` via parse-guard.
**How to avoid:** Mirror `PayloadConfigSchemaValidator`'s try/catch (`JsonSchema.FromText` wrapped in `catch (Exception ex) when (ex is JsonException or JsonSchemaException)`). Guard the empty case with `string.IsNullOrWhiteSpace(definition)` BEFORE `FromText`. [VERIFIED: PayloadConfigSchemaValidator.cs:53-62]

### Pitfall 5: L2 OUTPUT-write fault — business or infra? (D-15, the research call)
**What goes wrong:** Misclassifying a failed L2 output write. If treated as business-`Failed`, the output is lost (the chain breaks — the next step has no input). If treated as infra-throw, the WHOLE dispatch retries (re-running `ProcessAsync`, re-sending earlier results).
**Recommendation (grounded in `WorkflowLifecycle.IsBusiness` + the never-lose-output goal):** **An L2 output-write fault is INFRA — it THROWS.** Rationale:
- `WorkflowLifecycle.IsInfra` (`WorkflowLifecycle.cs:154`) classifies `RedisConnectionException`/`RedisTimeoutException`/`RedisException` as infra-that-propagates. An output write failing on those is the same class of fault.
- D-15 + CONTEXT code_context (`ProcessorLivenessHeartbeat` line note) explicitly state "an L2 **output write** fault on the execution path is load-bearing (an un-written output breaks the chain)" — distinct from the heartbeat's log-and-continue (where a missed beat self-heals next tick).
- The never-lose-output goal means a transient Redis blip should RETRY (Immediate(3)) the whole dispatch, not silently emit a `Failed` that drops the output.
**Important nuance for the planner:** Throwing mid-batch (after some results already Sent) means those earlier `ExecutionResult`s are re-sent on retry. This is acceptable under at-least-once delivery (the orchestrator's `ResultConsumer` is L1-idempotent for advancement — an already-advanced step re-dispatches deterministically). Document this as the accepted at-least-once tradeoff. **The output VALIDATION failure (D-09) remains business-`Failed`** — only the WRITE infra-fault throws.
**Warning signs:** Dispatches stuck in `_error` after a Redis restart (correct — they retry); OR outputs silently missing from L2 with a `Completed` result (the bug — write fault was wrongly business-acked).

### Pitfall 6: `ConnectReceiveEndpoint` not exercisable under the in-memory harness
**What goes wrong:** Trying to assert the full runtime-bind sequence under `AddMassTransitTestHarness` + `UsingInMemory` — the in-memory transport's runtime-connect semantics differ and the RabbitMQ-specific `Durable`/`AutoDelete` cast is null.
**How to avoid:** Make the consumer's `Consume` logic testable INDEPENDENTLY of the bind mechanism. Test `EntryStepDispatchConsumer.Consume` directly (constructed with fakes, driven via `OrchestratorTestStubs.Context`) OR via the harness with a STATIC `cfg.ReceiveEndpoint("{id:D}", e => e.ConfigureConsumer<...>(ctx))` (mirror `ResultConsumeTests.cs:44-52`). Defer the actual `ConnectReceiveEndpoint`-after-Healthy sequencing proof to the Phase 28 real-stack E2E (TEST-01). Unit-test the SEQUENCING (`MarkHealthy` called after `await handle.Ready`) by asserting ordering with a fake connector, not against a real broker. [VERIFIED: ResultConsumeTests.cs harness pattern + CONTEXT D-15/specifics]

## Code Examples

### Firm up ProcessResult (EXEC-04, D-08)
```csharp
// Source: repo ProcessResult.cs:9 (current) — firm to carry output data
namespace BaseProcessor.Core.Processing;
public sealed record ProcessResult(string OutputData);
```

### Ported SSRF-locked validator (EXEC-03/05, D-05/D-06)
```csharp
// Source: PORT of JsonSchemaConfig.cs + PayloadConfigSchemaValidator.cs (firewall — copy the pattern)
using System.Text.Json;
using Json.Schema;

namespace BaseProcessor.Core.Validation;

public static class ProcessorJsonSchemaValidator
{
    static ProcessorJsonSchemaValidator()
    {
        Dialect.Default = Dialect.Draft202012;            // pin dialect (library default is V1)
        SchemaRegistry.Global.Fetch = (_, _) => null;     // SSRF lockdown — no outbound $ref fetch
    }

    // Referencing DefaultOptions fires the SSRF-locking cctor (Pitfall 2/3).
    public static EvaluationOptions DefaultOptions { get; } = new() { OutputFormat = OutputFormat.List };

    /// <summary>null/whitespace definition → valid (skip, D-06). Unparseable → invalid (D-06).
    /// Returns false + flattened errors on failure.</summary>
    public static bool TryValidate(string? definition, string data, out IReadOnlyList<string> errors)
    {
        errors = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(definition))
            return true;   // empty definition skips validation (D-06)

        JsonSchema schema;
        try { schema = JsonSchema.FromText(definition); }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException)
        {
            errors = new[] { "Schema definition is not valid JSON Schema." };
            return false;  // unparseable → Failed (D-06), never a crash
        }

        using var doc = JsonDocument.Parse(data);   // a malformed data string → JsonException (caller guards)
        var results = schema.Evaluate(doc.RootElement, DefaultOptions);
        if (results.IsValid) return true;

        // flatten — mirror PayloadConfigSchemaValidator.cs:87-92
        var flat = (results.Details ?? Enumerable.Empty<EvaluationResults>())
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(kv => $"{d.InstanceLocation}: {kv.Value}"))
            .ToList();
        if (flat.Count == 0 && results.Errors is { Count: > 0 })
            flat = results.Errors.Select(kv => $"{results.InstanceLocation}: {kv.Value}").ToList();
        errors = flat;
        return false;
    }
}
```
[Source shape VERIFIED against JsonSchemaConfig.cs:22-34 + PayloadConfigSchemaValidator.cs:55-92]

### Consumer registration with endpoint suppression (EXEC-01, D-01)
```csharp
// Source: ctx7 masstransit_massient "Exclude Consumer from Automatic Endpoint Configuration"
//         + repo BaseProcessorServiceCollectionExtensions.cs:54-59 (the AddBaseConsoleMessaging call)
services.AddBaseConsoleMessaging(cfg, x =>
{
    x.AddRequestClient<GetProcessorBySourceHash>(new Uri("exchange:" + ProcessorQueues.IdentityQuery));
    x.AddRequestClient<GetSchemaDefinition>(new Uri("exchange:" + ProcessorQueues.SchemaQuery));

    // EXEC-01 / D-01: register for DI (so ConnectReceiveEndpoint can ConfigureConsumer<T> at runtime),
    // but EXCLUDE from the unconditional ConfigureEndpoints(ctx) so no wrong-named static queue is created.
    x.AddConsumer<EntryStepDispatchConsumer>().ExcludeFromConfigureEndpoints();
});
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Static `ReceiveEndpoint` at bus build | Runtime `ConnectReceiveEndpoint` for dynamically-named queues | MassTransit 7+ | Enables the after-Healthy bind (EXEC-01). |
| `bus.ConnectReceiveEndpoint(...)` directly | `IReceiveEndpointConnector` (DI-resolved) + `ConfigureConsumer<T>(ctx)` | MassTransit 8 | DI-aware consumer attachment; `IBus` still implements the connector interface. |

**Deprecated/outdated:**
- `masstransit.io` / `masstransit-project.com` URLs now 307-redirect to `masstransit.massient.com` (the v8 docs home). Old `masstransit-v6.netlify.app` pages describe v6 `ConnectReceiveEndpoint` (similar shape, but verify against v8).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | L2 output-write fault should be INFRA-throw (not business-Failed) | Pitfall 5 | If wrong direction, either outputs silently lost (business-ack) or excessive retries. Mitigated: grounded in `WorkflowLifecycle.IsInfra` + explicit CONTEXT note; recommend planner lock it. |
| A2 | RabbitMQ runtime receive endpoint inherits Durable=true/AutoDelete=false by default (cast optional) | Pattern 1 | If runtime-connected endpoints default differently than static ones, an explicit cast is REQUIRED. Mitigated: docs confirm "queues are durable by default"; cast shown defensively. Planner: verify at impl time. |
| A3 | The `(ctx, cfg)` registration context from `ConnectReceiveEndpoint` is sufficient for `ConfigureConsumer<T>(ctx)` without separately resolving `IRegistrationContext` | Pattern 1 | If a separate `IBusRegistrationContext` is needed, inject it into the orchestrator. Mitigated: ctx7 example passes the lambda's `context` directly to `ConfigureConsumer`. |
| A4 | `ISendEndpointProvider` (or `ConsumeContext`) resolves `queue:orchestrator-result` identically to `StepDispatcher` | Pattern 4 | Low — `StepDispatcher` is the proven precedent on the same broker. |

## Open Questions (RESOLVED)

> Both resolved during planning and locked in the plan decision blocks: Q1 → 27-03 ("rely on durable/AutoDelete defaults; do NOT add the RabbitMq configurator cast"); Q2 → 27-02 ("inputDefinition null/whitespace AND entryId Guid.Empty → do not read L2, pass inputData=\"\", skip validation").

1. **Exact RabbitMQ configurator type for the runtime endpoint cast.**
   - What we know: `Durable`/`AutoDelete` are the property names; defaults are durable/non-auto-delete.
   - What's unclear: whether the runtime `cfg` is castable to `RabbitMqReceiveEndpointConfigurator` (class) or only `IRabbitMqReceiveEndpointConfigurator` (interface), and whether the cast is null under the in-memory harness.
   - Recommendation: rely on defaults (no cast needed for correctness); if explicit, use `is IRabbitMqReceiveEndpointConfigurator rmq` guard so it no-ops under in-memory. Verify at implementation.

2. **Does `ProcessAsync` get an empty-string `inputData` when `inputDefinition` is null but `entryId` is present?**
   - What we know: D-07 says `inputDefinition` empty → skip VALIDATION; input is still read from L2 if entryId present.
   - What's unclear: behavior when inputDefinition is null AND entryId is empty (a source processor with no input). Likely `inputData = ""` (or the L2 value if any).
   - Recommendation: planner decides — `inputData = ""` when no entryId, validation skipped. Source processors legitimately have no input.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| RabbitMQ (compose) | EXEC-01/07 dispatch consume + result send | ✓ (compose stack) | — | In-memory harness for unit tests |
| Redis (localhost:6380) | EXEC-02/05 L2 I/O | ✓ (compose, `RedisFixture`) | 2.13.1 client | NSubstitute `IConnectionMultiplexer` (`OrchestratorTestStubs`) |
| MassTransit 8.5.5 | bus/consumer/connector | ✓ (CPM) | 8.5.5 | — |
| JsonSchema.Net 9.2.1 | EXEC-03/05 validation | ✓ (CPM-pinned, NOT yet referenced by BaseProcessor.Core) | 9.2.1 | — (must add PackageReference) |

**Missing dependencies with no fallback:** None — all required packages are CPM-pinned; only the `JsonSchema.Net` `PackageReference` line must be added to `BaseProcessor.Core.csproj` (version already pinned).

**Missing dependencies with fallback:** Real RabbitMQ/Redis (deferred to Phase 28 E2E) — Phase 27 unit/harness tests use the in-memory harness + `RedisFixture`/NSubstitute, mirroring P26.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 3.2.2 (Microsoft.Testing.Platform / MTP) |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (`UseMicrosoftTestingPlatformRunner=true`, `OutputType=Exe`) |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Processor"` |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |

(All tests live in the SINGLE `tests/BaseApi.Tests` project; the P26 processor slice is `tests/BaseApi.Tests/Processor/*`. New P27 tests land there too. [VERIFIED: Glob — only one .Tests.csproj])

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| EXEC-01 | bind sequencing: `MarkHealthy` after `await handle.Ready` | unit (fake connector, ordering) | `dotnet test … --filter "FullyQualifiedName~Processor.DispatchBindSequenceFacts"` | ❌ Wave 0 |
| EXEC-01 | competing-consumer dispatch consumed once | harness (static `ReceiveEndpoint("{id:D}")` mirror of ResultConsumeTests) | `… --filter "~DispatchConsumeFacts"` | ❌ Wave 0 |
| EXEC-02/03 | input read + existence-check + Failed-on-missing | unit (NSubstitute IConnectionMultiplexer) | `… --filter "~DispatchInputFacts"` | ❌ Wave 0 |
| EXEC-03/05 | schema validation skip/fail/pass | unit (ported validator) | `… --filter "~ProcessorJsonSchemaValidatorFacts"` | ❌ Wave 0 |
| EXEC-04/06 | `ProcessAsync` invoked with (inputData, config=Payload); ids minted | unit (test-double processor) | `… --filter "~DispatchInvokeFacts"` | ❌ Wave 0 |
| EXEC-05 | output-validate→mint→write L2 w/ TTL; fail→nothing-written | integration (RedisFixture localhost:6380) | `… --filter "~DispatchOutputWriteFacts"` | ❌ Wave 0 |
| EXEC-07/08 | one-by-one Send; empty list=ack-only; Failed/Cancelled always sent | harness (capture ExecutionResult) | `… --filter "~DispatchResultSendFacts"` | ❌ Wave 0 |
| EXEC-09 | ack-after-send; infra-throw on send/L2-write fault → propagates | unit (NSubstitute throwing endpoint/db) | `… --filter "~DispatchAckSemanticsFacts"` | ❌ Wave 0 |
| EXEC-10 | body CorrelationId == dispatch body; envelope stamped by filter | harness | `… --filter "~DispatchCorrelationFacts"` | ❌ Wave 0 |
| CONFIG-02 | `ExecutionDataTtlSeconds` binds from `Processor` section; applied on write | unit (options binding, mirror ProcessorOptionsBindingFacts) | `… --filter "~ProcessorOptionsBindingFacts"` | ⚠️ EXTEND existing |

### Sampling Rate
- **Per task commit:** `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Processor"`
- **Per wave merge:** `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (full suite)
- **Phase gate:** Full suite green before `/gsd-verify-work`. (3-GREEN/triple-SHA close gate is TEST-02 — Phase 28.)

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Processor/ProcessorJsonSchemaValidatorFacts.cs` — EXEC-03/05 (skip/fail/pass + SSRF lockdown)
- [ ] `tests/BaseApi.Tests/Processor/DispatchInputFacts.cs` — EXEC-02/03 (read + existence + Failed-on-missing)
- [ ] `tests/BaseApi.Tests/Processor/DispatchInvokeFacts.cs` — EXEC-04/06 (ProcessAsync seam + minting)
- [ ] `tests/BaseApi.Tests/Processor/DispatchOutputWriteFacts.cs` — EXEC-05 (RedisFixture write + TTL; needs `RedisFixture.Track` net-zero teardown)
- [ ] `tests/BaseApi.Tests/Processor/DispatchResultSendFacts.cs` — EXEC-07/08 (one-by-one, empty=ack, Failed/Cancelled)
- [ ] `tests/BaseApi.Tests/Processor/DispatchAckSemanticsFacts.cs` — EXEC-09 (infra-throw vs business-ack; mirror AckSemanticsTests)
- [ ] `tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs` — EXEC-01 (MarkHealthy-after-Ready ordering, fake connector)
- [ ] `tests/BaseApi.Tests/Processor/DispatchCorrelationFacts.cs` — EXEC-10
- [ ] EXTEND `tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs` — CONFIG-02 `ExecutionDataTtlSeconds`
- [ ] (Reuse `ProcessorTestHarness`, `OrchestratorTestStubs`, `RedisFixture`, `FakeProcessorContext` — all exist.)

**Existing reusable test infra:** `Processor/ProcessorTestHarness.cs` (in-memory harness + named ReceiveEndpoints), `Composition/RedisFixture.cs` (real localhost:6380 + `Track` known-key teardown), `Orchestrator/OrchestratorTestStubs.cs` (NSubstitute `IConnectionMultiplexer` Absent/Present/InfraFault + `ConsumeContext` stub — usable cross-namespace), `Processor/FakeProcessorContext.cs`. Mirror `ResultConsumeTests.cs`/`FireDispatchTests.cs` for the harness Send-to-`{id:D}` capture, `AckSemanticsTests.cs` for the business-ack/infra-throw split, `LivenessHeartbeatFacts.cs` for the RedisFixture write+TTL assertion.

## Security Domain

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Internal bus/broker; no end-user auth in this phase (broker creds via `cfg.Require`). |
| V3 Session Management | no | Stateless message consume. |
| V4 Access Control | no | No user-facing surface. |
| V5 Input Validation | yes | JsonSchema.Net schema validation of L2 input + transform output (EXEC-03/05); untrusted dispatch body. |
| V6 Cryptography | no | No crypto in this phase. |

### Known Threat Patterns for .NET / MassTransit / Json.Schema
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| SSRF via external `$ref` in a schema definition | Information Disclosure / Tampering | Ported `SchemaRegistry.Global.Fetch = (_,_) => null` + pinned `Dialect.Draft202012` (Pitfall 2/3) — defense-in-depth, never fetch. |
| Malformed/unparseable schema or data string crashing the host | Denial of Service | Parse-guards (`catch JsonException/JsonSchemaException` → business `Failed`, never crash — D-06). |
| Unbounded poison-message retry loop | Denial of Service | `UseMessageRetry(r => r.Immediate(3))` (bounded) → `_error` (mirror `ResultConsumerDefinition`). |
| Correlation id treated as trusted/interpolated | Injection (log) | Inbound filter places it as a scope VALUE only, never in a template (`InboundCorrelationConsumeFilter.cs` T-18-04 note); fresh-Guid fallback. |
| Untrusted output written to shared L2 keyspace | Tampering | Unique per-result `NewId.NextGuid()` entryId key (no contention/overwrite — LIVE-06 lock-free model); TTL-bounded (CONFIG-02). |

## Sources

### Primary (HIGH confidence)
- **Repo source (VERIFIED):** `ResultConsumer.cs`, `ResultConsumerDefinition.cs`, `StepDispatcher.cs`, `WorkflowLifecycle.cs`, `EntryStepDispatch.cs`, `ExecutionResult.cs`, `StepOutcome.cs`, `OrchestratorQueues.cs`, `L2ProjectionKeys.cs`, `ProcessorStartupOrchestrator.cs`, `IProcessorContext.cs`, `ProcessorContext.cs`, `BaseProcessorServiceCollectionExtensions.cs`, `ProcessorLivenessHeartbeat.cs`, `BaseProcessor.cs`, `ProcessResult.cs`, `MessagingServiceCollectionExtensions.cs`, `JsonSchemaConfig.cs`, `PayloadConfigSchemaValidator.cs`, `ProcessorLivenessOptions.cs`, `Directory.Packages.props`, all `.csproj`, `Orchestrator/Program.cs`.
- **Repo tests (VERIFIED):** `ProcessorTestHarness.cs`, `ResultConsumeTests.cs`, `AckSemanticsTests.cs`, `FireDispatchTests.cs`, `OrchestratorTestStubs.cs`, `LivenessHeartbeatFacts.cs`, `BaseProcessorSeamFacts.cs`, `AddBaseProcessorFacts.cs`, `RedisFixture.cs`, `BaseApi.Tests.csproj`.
- **Context7 `/websites/masstransit_massient` (CITED):** "Dynamically Connect a Receive Endpoint" (`IReceiveEndpointConnector.ConnectReceiveEndpoint` + `await handle.Ready`), "Exclude Consumer from Automatic Endpoint Configuration" (`ExcludeFromConfigureEndpoints`), "Configure Immediate Retry for a Specific Receive Endpoint" (`UseMessageRetry` inside endpoint), RabbitMQ "queues are durable by default", `Durable`/`AutoDelete`/`PrefetchCount` receive-endpoint options.

### Secondary (MEDIUM confidence)
- [Bus Configuration | MassTransit](https://masstransit.massient.com/configuration) — runtime endpoint connection + exclude patterns.
- [Connect Endpoint | MassTransit v6](https://masstransit-v6.netlify.app/advanced/connect-endpoint) — older but same-shape `ConnectReceiveEndpoint`.
- [GitHub Discussion #4530 — Register deferred consumers](https://github.com/MassTransit/MassTransit/discussions/4530) — confirms `IReceiveEndpointConnector` + `ConfigureConsumer` runtime pattern.

### Tertiary (LOW confidence)
- None — all load-bearing claims verified against repo source or Context7.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all versions read directly from CPM `Directory.Packages.props`.
- Architecture (runtime bind, L2 I/O, send, validation): HIGH — runtime-bind API verified via Context7; all other patterns are verbatim repo mirrors.
- Pitfalls: HIGH — each grounded in a cited repo file or Context7 doc.
- L2-write business-vs-infra (Pitfall 5/A1): MEDIUM — a reasoned recommendation grounded in `WorkflowLifecycle.IsInfra` + CONTEXT notes; planner should lock it as a decision.

**Research date:** 2026-06-01
**Valid until:** 2026-07-01 (stable — MassTransit 8.5.5 + repo are pinned/frozen; brownfield mirror).

Sources:
- [Bus Configuration | MassTransit](https://masstransit.massient.com/configuration)
- [Connect Endpoint | MassTransit v6](https://masstransit-v6.netlify.app/advanced/connect-endpoint)
- [Register deferred consumers — MassTransit Discussion #4530](https://github.com/MassTransit/MassTransit/discussions/4530)
