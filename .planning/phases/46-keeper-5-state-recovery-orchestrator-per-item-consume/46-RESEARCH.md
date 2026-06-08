# Phase 46: Keeper 5-State Recovery + Orchestrator Per-Item Consume - Research

**Researched:** 2026-06-08
**Domain:** .NET 8 / C# / MassTransit 8.5.5 / RabbitMQ / Redis (StackExchange.Redis) backend
**Confidence:** HIGH (the codebase is the primary source; every contract, helper, and send-site this phase builds on already exists and was read at file:line. The one genuinely external unknown — MassTransit `UsePartitioner` + un-acked-redelivery semantics — is documented MEDIUM with a flagged landmine.)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01 — Add `Payload` to `KeeperReinject`.** Processor (`BuildReinject`) stamps the inbound dispatch's `Payload` onto `KeeperReinject`; Keeper reconstructs a full `EntryStepDispatch(WorkflowId, StepId, ProcessorId, Payload) { CorrelationId, ExecutionId, EntryId }`. Additive field on a Phase-43 shipped contract + edit to the Phase-44 processor send site + update the `KeeperReinject` golden/contract test. No other Keeper record changes. Design-doc amendment warranted (user-owned).
- **D-02 — Five sealed per-state consumer classes + a shared gate/retry base**, each with its own `ConsumerDefinition` bound to `queue:keeper-recovery`. Base owns: await `IL2HealthGate.WaitForOpenAsync` once at entry (D-03) + the `RetryLoop` wrapper for every L2 op + send. Each subclass carries its distinct state body. (Rejected: one class implementing all five `IConsumer<T>`.)
- **D-03 — Await the gate ONCE at `Consume` entry** (before any L2 op), via a linked `CancellationTokenSource` bounded at ~5 minutes (well under RabbitMQ's 30-min `consumer_timeout`). On timeout the delivery is left un-acked → redelivered (the partitioner re-orders it back into its key group). Exact seconds value is Claude's under the 30-min constraint; bind from config alongside the other Keeper options if convenient. (Rejected: re-awaiting before each individual L2 op.)
- **D-04 — Defer `_DLQ1` to Phase 47 — throw to the existing bus error queue.** On retry-loop exhaustion of any Keeper L2 op or send, let the exception propagate so MassTransit dead-letters via the existing error path. For the REINJECT-data-gone case (deliberate terminal, not a natural Redis exception), throw a marker exception to force the same dead-letter route rather than silently acking.
- **D-05 — Relocate `RetryLoop` from `BaseProcessor.Core` to `BaseConsole.Core`** (which both `BaseProcessor.Core` and `Keeper` reference) so there is ONE A3 `Retry:Limit` implementation; update `BaseProcessor.Core`'s `using`. Keeper binds `Retry:Limit` from its existing `RetryOptions` section. (Rejected: a duplicate Keeper-local helper.)
- **D-06 — `UsePartitioner(N)` count is a config knob, default 8**, bound from appsettings (mirrors `Probe`/`Backup`/`Retry`). Partition key is the `IKeeperRecoverable` 4-tuple (`corr:wf:ProcessorId:executionId`) per Phase-43 D-12.
- **D-07 — Build `TypedResultConsumer<TMessage> where TMessage : class, IStepResult` base + four sealed one-line subclasses** (`StepCompletedConsumer`/`StepFailedConsumer`/`StepCancelledConsumer`/`StepProcessingConsumer`), all co-located on `OrchestratorQueues.Result` via four thin `ConsumerDefinition`s, exactly per 43-CONTEXT D-06e. One `protected abstract StepOutcome Outcome { get; }` knob per type — no status if/switch anywhere. Body = retained `ResultConsumed` metric → L1-miss business-ack → `StepAdvancement.SelectNext(Outcome, …)` → `DispatchAsync` per matched successor (preserving `correlationId`/`workflowId`/`executionId`, resolving new `ProcessorId`/`payload`/`stepId` from L1, seeding `entryId = m.EntryId`). The current `ResultConsumer.cs` stub is replaced. Park-on-send-exhaustion routes to the existing error queue.

### Claude's Discretion
- Exact namespace/placement of the recovery-consumer base + the marker give-up exception (D-02/D-04).
- The precise gate-wait timeout seconds under the 30-min bound, and whether it rides an existing or new options key (D-03).
- `RetryLoop` target namespace within `BaseConsole.Core` and how exhaustion is surfaced to the Keeper bodies (D-05) — keep parity with the Phase-44 surfacing contract.
- `KeeperReinject` `Payload` member name/position (follow the existing record convention; `init`-only) (D-01).
- The reconstructed-`EntryStepDispatch` send mechanism for REINJECT (reuse the `ISendEndpointProvider` → `queue:{ProcessorId:D}` idiom from `StepDispatcher`).

### Deferred Ideas (OUT OF SCOPE)
- `_DLQ1` consolidation + at-least-once-no-dedup statement — Phase 47 (RESIL-02/03). Phase 46 throws give-ups to the existing error queue (D-04).
- Removal of the dark reactive `Fault<T>` recovery path + `keeper-fault-recovery` + per-workflow `PauseWorkflow`/`ResumeWorkflow` + `keeper-dlq` — Phase 48 (RETIRE-03). Phase 46 coexists additively.
- Design-doc amendment recording `Payload`-on-`KeeperReinject` (D-01) — user-owned doc update.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| KEEP-04 | `UPDATE` writes validated data to `L2[CompositeBackup]` with configurable TTL (default 2 days, crash-backstop). | `L2ProjectionKeys.CompositeBackup(corr,wf,proc,exec)` [VERIFIED: src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:46] + `BackupOptions.TtlDays` (default 2) [VERIFIED: src/Keeper/BackupOptions.cs:8], already bound in `Keeper/Program.cs:33`. Write = `db.StringSetAsync(key, KeeperUpdate.ValidatedData, expiry: TimeSpan.FromDays(opts.TtlDays))`. |
| KEEP-05 | `REINJECT` reads `L2[entryId]`, re-injects dispatch to `queue:{ProcessorId}`; data gone → terminal. | `L2ProjectionKeys.ExecutionData(entryId)` read [VERIFIED: L2ProjectionKeys.cs:42] + `KeeperReinject` carries `EntryId` [VERIFIED: src/Messaging.Contracts/KeeperReinject.cs:11]. Reconstruct `EntryStepDispatch` (needs `Payload` per D-01) and `Send` to `queue:{ProcessorId:D}` (StepDispatcher idiom, StepDispatcher.cs:34). Data-gone marker exception per D-04. |
| KEEP-06 | `INJECT` reads composite → new `entryId` → writes `L2[entryId]` (no TTL) → injects `StepCompleted` to orchestrator → deletes composite. | Composite read + `NewId.NextGuid()` for entryId + `StringSetAsync` (no expiry) + reconstruct `StepCompleted{EntryId, ExecutionId}` and `Send` to `queue:orchestrator-result` + `KeyDeleteAsync(composite)`. StepCompleted shape [VERIFIED: src/Messaging.Contracts/StepCompleted.cs:7]. |
| KEEP-07 | `DELETE` deletes `L2[entryId]`. | `db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(KeeperDelete.EntryId))`. KeeperDelete carries `EntryId` [VERIFIED: KeeperDelete.cs:11]. |
| KEEP-08 | `CLEANUP` deletes the redundant composite copy on happy path. | `db.KeyDeleteAsync(L2ProjectionKeys.CompositeBackup(...))`. KeeperCleanup carries only the 4-tuple (no extra) [VERIFIED: KeeperCleanup.cs]. |
| KEEP-09 | Recovery consumer partitioned by `corr:wf:ProcessorId:executionId` (per-key ordering); `UPDATE` precedes that exec's `CLEANUP`/`INJECT`; different execs parallel. | MassTransit `UsePartitioner<T>(sharedPartitioner, p => key)` per message type on the shared `queue:keeper-recovery` endpoint — see Architecture Pattern 1. Partition key = `IKeeperRecoverable` 4-tuple [VERIFIED: src/Messaging.Contracts/IKeeperRecoverable.cs]. |
| ORCH-01 | Orchestrator consumes per-item results (no manifest fan-out), advances steps; Keeper-INJECT'd completion indistinguishable from a direct one. | `TypedResultConsumer<T>` base + four subclasses (D-07). `StepAdvancement.SelectNext` [VERIFIED: src/Orchestrator/Dispatch/StepAdvancement.cs:36] + `StepDispatcher.DispatchAsync` [VERIFIED: StepDispatcher.cs:23] reused unchanged. Existing `ResultConsumer.cs` body is the blueprint. |
</phase_requirements>

## Summary

This phase is overwhelmingly an **assembly job over already-shipped primitives**, not a greenfield build. Phase 43 shipped every wire contract (`KeeperUpdate/Reinject/Inject/Delete/Cleanup`, `IKeeperRecoverable`, the four `Step*` result records, `IStepResult`, `KeeperQueues.Recovery`), every L2 key builder (`ExecutionData`, `CompositeBackup`), and `BackupOptions`. Phase 44 shipped `RetryLoop`/`RetryOutcome<T>`, the `SendKeeper`/`SendResult` send idioms, and the exact `Build*` reconstruction helpers. Phase 45 shipped `IL2HealthGate.WaitForOpenAsync`. Phase 46 wires these into seven new consumers (five Keeper + reshaping the orchestrator's one into a base + four) and configures the partitioner. The Phase-44 `ProcessorPipeline` is, in effect, a complete worked example of every L2 op, every `RetryLoop` call shape, and every `Build*` reconstruction this phase needs on the Keeper side — read it as the canonical reference implementation.

There are exactly **three genuinely novel/risky elements**, and the planner should concentrate verification there: (1) **`UsePartitioner` on a multi-consumer endpoint** — first use in the codebase; the correct idiom is a single shared `Partitioner` instance with one `UsePartitioner<T>` call per message type on the receive-endpoint configurator, which makes ordering apply endpoint-wide exactly as D-02 assumes. (2) **The bounded gate-wait + redelivery-on-timeout (D-03)** — this carries a **real semantic landmine**: in MassTransit v8 a thrown exception (including `OperationCanceledException`) does **not** leave the message un-acked for natural broker redelivery — it moves to the error queue. The existing `L2ProbeRecovery` already establishes the correct "await inside Consume, hold the delivery un-acked while looping" pattern, and `ProbeOptions` (5s × 12 = 60s, 30× margin) is the precedent the gate-wait bound should mirror. (3) **D-01's additive `Payload` field**, which is mechanically simple but ripples across a shipped contract, a shipped golden test, and the Phase-44 `BuildReinject` send site.

**Primary recommendation:** Treat `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` and `src/Orchestrator/Consumers/ResultConsumer.cs` as the two reference implementations. The five Keeper bodies are the inverse of the pipeline's five `SendKeeper` sites; the four typed consumers are the existing `ResultConsumer.Consume` body lifted into a generic base with the `StepOutcome.Completed` constant replaced by a `protected abstract StepOutcome Outcome`. Configure the partitioner with a single shared `Partitioner(N, new Murmur3UnsafeHashGenerator())` and five `UsePartitioner<T>` calls in `ConfigureConsumer` / an endpoint-configure callback. Resolve the redelivery-vs-throw question (Open Question 1) before locking the gate-wait task.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Five Keeper recovery states (UPDATE/REINJECT/INJECT/DELETE/CLEANUP) | Keeper console (worker) | Redis (L2 data/composite) | Keeper owns the composite copy (design Model B); all five are L2 ops + bus sends — no L1, no Postgres. |
| Per-key ordering of recovery messages | RabbitMQ transport + MassTransit middleware | — | Ordering is a transport/middleware concern (`UsePartitioner`), not application logic. |
| Gate-open-only execution | Keeper console (in-process `IL2HealthGate`) | — | The gate is an in-process async reset-event written by the BIT loop; consumed in-process. No external coordination. |
| REINJECT re-dispatch target | Processor queue (`queue:{ProcessorId:D}`) | — | REINJECT puts work back on the same per-processor queue a direct dispatch uses. |
| INJECT completion target | Orchestrator result queue (`queue:orchestrator-result`) | — | An INJECT'd `StepCompleted` must land where direct completions land (ORCH-01 indistinguishability). |
| Per-item step advancement | Orchestrator console (L1-only) | — | `SelectNext` + L1 store; explicitly no Redis on the result path (D-06e). |
| Terminal give-up routing | RabbitMQ error transport (MassTransit `ConfigureError`) | — | Already consolidated to `skp-dlq-1` by `BaseConsole.Core` (see Pitfall 3). |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 | Bus, consumers, `UsePartitioner`, retry, error transport | [VERIFIED: Directory.Packages.props:137]. Last Apache-2.0 line; do NOT bump to 9.x (commercial) without a license decision (per CPM comment lines 133-136). |
| MassTransit.RabbitMQ | 8.5.5 | RabbitMQ transport (`IRabbitMqBusFactoryConfigurator`, `IReceiveEndpointConfigurator`) | [VERIFIED: Directory.Packages.props:138]. |
| StackExchange.Redis | 2.13.1 | L2 ops (`StringGetAsync`/`StringSetAsync`/`KeyDeleteAsync`) | [VERIFIED: Directory.Packages.props:131]. `IConnectionMultiplexer` is already a DI singleton via `AddBaseConsole` (Keeper/Program.cs:29 comment; L2ProbeRecovery.cs:20 ctor-injects it). |
| Microsoft.Extensions.Options | (framework) | `IOptions<RetryOptions>` / `IOptions<BackupOptions>` / partition-count option | [VERIFIED: existing usage in L2ProbeRecovery.cs:20, ProbeOptions binding Program.cs:29]. |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| MassTransit `NewId` | (in MassTransit) | `NewId.NextGuid()` for fresh `entryId`/`executionId` | INJECT mints a new `entryId`; matches the processor's `NewId.NextGuid()` in ProcessorPipeline.cs:130 and ResultConsumer.cs:75. |
| xunit.v3 / NSubstitute | 3.2.2 / 5.3.0 | Unit tests (consumer harness, contract pins) | [VERIFIED: Directory.Packages.props:120-121]. The existing `KeeperContractTests` + `RetryLoopFacts` are the test-style precedent. |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `UsePartitioner` middleware | RabbitMQ single-active-consumer / consistent-hash exchange | Rejected by design + D-06: the design names `UsePartitioner` explicitly; SAC serializes the *whole* queue (kills cross-exec parallelism), consistent-hash needs an extra exchange + plugin. |
| Five sealed consumers (D-02) | One consumer implementing five `IConsumer<T>` | Rejected in D-02 — five unrelated bodies in one file, harder to isolate-test. |

**Installation:** No new packages. All required packages are already CPM-pinned and referenced by `Keeper.csproj` (MassTransit + MassTransit.RabbitMQ) and `Orchestrator.csproj`. `Murmur3UnsafeHashGenerator` and `Partitioner` ship inside MassTransit 8.5.5 (namespace `MassTransit` / `MassTransit.Configuration`) — no extra reference.

**Version verification:** `MassTransit 8.5.5` [VERIFIED: Directory.Packages.props:137, current pinned solution version]. `StackExchange.Redis 2.13.1` [VERIFIED: Directory.Packages.props:131]. No registry lookup needed — versions are repo-locked under CPM and must not change in this phase.

## Architecture Patterns

### System Architecture Diagram

```
                        PROCESSOR (Phase 44, existing)
   EntryStepDispatch ──► ProcessorPipeline ──► (per item) ──┐
                                                            │ SendKeeper(...) → queue:keeper-recovery
        SendResult(...) → queue:orchestrator-result         │ (UPDATE / REINJECT / INJECT / DELETE / CLEANUP)
                 │                                           │
                 ▼                                           ▼
   ┌─────────────────────────────┐        ┌──────────────────────────────────────────────────┐
   │ ORCHESTRATOR result endpoint │        │ KEEPER  queue:keeper-recovery  (NEW this phase)     │
   │  "orchestrator-result"       │        │  UsePartitioner(N) keyed on corr:wf:proc:exec       │
   │                              │        │  ┌──────────────────────────────────────────────┐  │
   │  TypedResultConsumer<T> base │        │  │ RecoveryConsumerBase (gate + RetryLoop)        │  │
   │   ├ StepCompletedConsumer    │        │  │   await IL2HealthGate.WaitForOpenAsync (bound) │  │
   │   ├ StepFailedConsumer       │        │  │   then dispatch to the per-state body:         │  │
   │   ├ StepCancelledConsumer    │        │  │     UPDATE  → write composite (TTL)            │  │
   │   └ StepProcessingConsumer   │        │  │     REINJECT→ read L2[entryId]                 │  │
   │                              │        │  │        present → Send EntryStepDispatch ───────┼──┘ (back to queue:{proc})
   │  Consume(m):                 │        │  │        absent  → throw DataGone marker → DLQ   │
   │   metric ResultConsumed      │        │  │     INJECT  → read composite → new entryId →   │
   │   L1 TryGet (miss → ack)     │◄───────┼──┼─────── write L2[entryId] → Send StepCompleted ─┼──┐
   │   SelectNext(Outcome,…)      │        │  │        → delete composite                       │  │ (to orchestrator-result;
   │   DispatchAsync per match ───┼───┐    │  │     DELETE  → delete L2[entryId]               │  │  indistinguishable from
   │     → queue:{nextProc}       │   │    │  │     CLEANUP → delete composite                 │  │  a direct completion)
   └──────────────────────────────┘   │    │  └──────────────────────────────────────────────┘  │
                                       │    │  any L2/send exhaustion → throw → error transport    │
                                       │    └──────────────────────────────────────────────────┘ │
                                       └────────────────────► queue:{ProcessorId}  ◄───────────────┘
```

The two arrows that close the loop: REINJECT re-Sends an `EntryStepDispatch` to `queue:{ProcessorId}` (same target a fresh dispatch uses), and INJECT Sends a reconstructed `StepCompleted` to `queue:orchestrator-result` (same target a direct completion uses, processed by the same `StepCompletedConsumer`).

### Recommended Project Structure
```
src/Keeper/
├── Recovery/
│   ├── RecoveryConsumerBase.cs        # NEW — gate-wait + RetryLoop; abstract HandleAsync per body (D-02)
│   ├── UpdateConsumer.cs              # NEW — IConsumer<KeeperUpdate>   : RecoveryConsumerBase
│   ├── ReinjectConsumer.cs           # NEW — IConsumer<KeeperReinject>
│   ├── InjectConsumer.cs             # NEW — IConsumer<KeeperInject>
│   ├── DeleteConsumer.cs             # NEW — IConsumer<KeeperDelete>
│   ├── CleanupConsumer.cs            # NEW — IConsumer<KeeperCleanup>
│   ├── RecoveryDataGoneException.cs  # NEW — the REINJECT data-gone marker (D-04)
│   ├── L2ProbeRecovery.cs            # existing — L2-op pattern to mirror
│   └── (five ConsumerDefinitions — co-located or in Consumers/)
├── RecoveryOptions.cs                # NEW (optional) — PartitionCount (default 8) + GateWaitSeconds (D-03/D-06)
src/BaseConsole.Core/
└── Resilience/RetryLoop.cs           # MOVED from BaseProcessor.Core (D-05)
src/Orchestrator/
├── Consumers/
│   ├── TypedResultConsumer.cs        # NEW — abstract base (D-07)
│   ├── StepCompletedConsumer.cs      # NEW — replaces ResultConsumer.cs
│   ├── StepFailedConsumer.cs         # NEW
│   ├── StepCancelledConsumer.cs      # NEW
│   ├── StepProcessingConsumer.cs     # NEW
│   └── (four ConsumerDefinitions on OrchestratorQueues.Result)
src/Messaging.Contracts/
└── KeeperReinject.cs                 # EDIT — add Payload (D-01)
```

### Pattern 1: `UsePartitioner` on a multi-consumer endpoint (KEEP-09, D-02/D-06) — FIRST USE IN CODEBASE

A single shared `Partitioner` instance with one `UsePartitioner<T>` call per message type, configured on the **receive-endpoint configurator**. Because the partitioner is applied per endpoint (not per consumer — see Pitfall 1), ordering applies endpoint-wide across all five co-located consumers exactly as D-02 assumes. A shared `Partitioner` instance guarantees the same key hashes to the same partition slot across the five message types, so `UPDATE`/`CLEANUP`/`INJECT` for one exec serialize together.

```csharp
// Source: https://masstransit.massient.com/configuration/middleware/partitioner (CITED)
// Adapted to the four-GUID Keeper partition key. The partition key for each message type is the
// IKeeperRecoverable 4-tuple — compose it to a stable string (matches the composite-backup key shape).
var partition = new Partitioner(partitionCount /* default 8, D-06 */, new Murmur3UnsafeHashGenerator());

e.UsePartitioner<KeeperUpdate>  (partition, p => PartitionKey(p.Message));
e.UsePartitioner<KeeperReinject>(partition, p => PartitionKey(p.Message));
e.UsePartitioner<KeeperInject>  (partition, p => PartitionKey(p.Message));
e.UsePartitioner<KeeperDelete>  (partition, p => PartitionKey(p.Message));
e.UsePartitioner<KeeperCleanup> (partition, p => PartitionKey(p.Message));

// PartitionKey == the composite-backup 4-tuple (NOT stepId — D-12). A string overload exists; a
// byte[]/Guid overload also exists. Simplest: reuse the existing key shape for an exact match.
static string PartitionKey(IKeeperRecoverable m) =>
    $"{m.CorrelationId:D}:{m.WorkflowId:D}:{m.ProcessorId:D}:{m.ExecutionId:D}";
```
- **API surface (CITED, masstransit docs):** `UsePartitioner<T>(int partitionCount, Func<ConsumeContext<T>, TKey> keyProvider)` and `UsePartitioner<T>(Partitioner partitioner, Func<ConsumeContext<T>, TKey> keyProvider)`. `TKey` overloads accept `Guid`, `string` (with optional `Encoding`), or `byte[]`. The recommended hash generator is `Murmur3UnsafeHashGenerator`.
- **Where to call it:** on the `IReceiveEndpointConfigurator`. With this codebase's definition-based pattern, the cleanest seam is a single `ConsumerDefinition.ConfigureConsumer(endpointConfigurator, …)` that owns the partitioner (one definition registers it; the other four leave `ConfigureConsumer` a no-op — exactly the per-endpoint ownership pattern the existing `FaultEntryStepDispatchConsumerDefinition` doc-comment describes for `UseMessageRetry`, FaultEntryStepDispatchConsumerDefinition.cs:18-19,43). Alternatively use `x.AddConfigureEndpointsCallback` filtered to the `keeper-recovery` endpoint name.

### Pattern 2: The five Keeper state bodies = the inverse of the processor's five `SendKeeper` sites

The Phase-44 `ProcessorPipeline` is a complete worked example of every op these bodies perform. Read these as the canonical reference for the L2-op + `RetryLoop` call shape:

```csharp
// Source: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs (VERIFIED, existing)
// UPDATE write (KEEP-04): mirror line 129-132, but ADD the TTL (the processor write was no-TTL data;
// the Keeper composite write carries BackupOptions.TtlDays):
var write = await RetryLoop.ExecuteAsync(
    () => db.StringSetAsync(
        L2ProjectionKeys.CompositeBackup(m.CorrelationId, m.WorkflowId, m.ProcessorId, m.ExecutionId),
        m.ValidatedData,
        expiry: TimeSpan.FromDays(backupOpts.TtlDays)), limit, ct);   // KEEP-04 TTL crash-backstop
if (!write.Succeeded) throw write.Error!;   // D-04: propagate → error transport

// REINJECT read (KEEP-05): mirror the Pre-read closure lines 75-80 (absent/empty → throw → exhausted):
var read = await RetryLoop.ExecuteAsync(async () => {
    var raw = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(m.EntryId));
    if (raw.IsNullOrEmpty) throw new RecoveryDataGoneException();   // D-04 marker (data truly gone)
    return raw.ToString();
}, limit, ct);
if (!read.Succeeded) throw read.Error!;   // exhausted (Redis fault OR data-gone) → error transport (D-04)
// present → reconstruct EntryStepDispatch (needs Payload, D-01) and Send to queue:{ProcessorId:D}

// DELETE (KEEP-07) / CLEANUP (KEEP-08): mirror end-delete lines 159-160:
var del = await RetryLoop.ExecuteAsync(() => db.KeyDeleteAsync(key), limit, ct);
if (!del.Succeeded) throw del.Error!;
```
**RetryLoop semantics (VERIFIED, RetryLoop.cs):** `ExecuteAsync<T>(Func<Task<T>> op, int limit, CancellationToken ct)` returns `RetryOutcome<T>` — it **surfaces** exhaustion (`.Succeeded == false`, `.Error` = last exception) rather than throwing. The Keeper bodies must check `.Succeeded` and **re-throw** `.Error` to hit the D-04 error-transport route, exactly as the processor's `SendResult`/`SendKeeper` do (`if (!sent.Succeeded) throw sent.Error!;` ProcessorPipeline.cs:174,182).

### Pattern 3: REINJECT re-dispatch + INJECT result injection (reuse the existing Send idioms)

```csharp
// Source: src/Orchestrator/Dispatch/StepDispatcher.cs:34 + src/BaseProcessor.Core/.../ProcessorPipeline.cs:171,179 (VERIFIED)
// REINJECT → queue:{ProcessorId:D} (same target as a direct dispatch):
var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{m.ProcessorId:D}"));
var dispatch = new EntryStepDispatch(m.WorkflowId, m.StepId, m.ProcessorId, m.Payload /* D-01 */) {
    CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId, EntryId = m.EntryId };
// wrap the Send in RetryLoop; throw on exhaustion (D-04)

// INJECT → queue:orchestrator-result (same target + same type as a direct completion → ORCH-01):
var rep = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
var completed = new StepCompleted(m.WorkflowId, m.StepId, m.ProcessorId) {
    CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId, EntryId = newEntryId };
```
Note the existing send idiom Sends `(object)msg` to defeat MassTransit's generic `Send<T>` type inference when the static type is an interface (`IStepResult`/`IKeeperRecoverable`) — ProcessorPipeline.cs:173,181. For REINJECT/INJECT the static type is the concrete record, so `(object)` is not strictly required, but mirror the existing convention for consistency.

### Pattern 4: `TypedResultConsumer<T>` base (D-07) = the existing `ResultConsumer.Consume` body lifted into a generic

The current `ResultConsumer` (ResultConsumer.cs:46-77) is the literal blueprint. The only change is replacing the hardcoded `StepOutcome.Completed` (line 72) with `protected abstract StepOutcome Outcome { get; }`:

```csharp
// Source: src/Orchestrator/Consumers/ResultConsumer.cs (VERIFIED, existing — to be generalized)
public abstract class TypedResultConsumer<TMessage>(
    IWorkflowL1Store store, StepAdvancement advancement, IStepDispatcher dispatcher,
    OrchestratorMetrics metrics, ILogger logger) : IConsumer<TMessage>
    where TMessage : class, IStepResult
{
    protected abstract StepOutcome Outcome { get; }   // the ONLY per-type knob (D-07; no if/switch)

    public async Task Consume(ConsumeContext<TMessage> context)
    {
        var m = context.Message;
        metrics.ResultConsumed.Add(1, new KeyValuePair<string, object?>("ProcessorId", m.ProcessorId.ToString("D")));
        if (!store.TryGet(m.WorkflowId, out var wf) || !wf.Steps.TryGetValue(m.StepId, out var completed))
        { logger.LogInformation("No L1 entry for ({W},{S}) — acking (business)", m.WorkflowId, m.StepId); return; }
        foreach (var (stepId, step) in advancement.SelectNext(Outcome, completed, wf.Steps))
            await dispatcher.DispatchAsync(m.WorkflowId, stepId, step.ProcessorId, step.Payload,
                m.CorrelationId, NewId.NextGuid(), m.EntryId, context.CancellationToken);
    }
}

public sealed class StepCompletedConsumer(/* same deps */) : TypedResultConsumer<StepCompleted>(/*…*/)
{ protected override StepOutcome Outcome => StepOutcome.Completed; }
// + StepFailedConsumer (Failed), StepCancelledConsumer (Cancelled), StepProcessingConsumer (Processing)
```
- **`SelectNext` already handles all four outcomes** — it matches `next.EntryCondition == (int)outcome` or `Always(4)`, `Never(5)` never auto-advances (StepAdvancement.cs:39-42). No change needed.
- **`StepOutcome` int alignment** (43-CONTEXT D-06d): `Processing/Completed/Failed/Cancelled` are int-aligned to `StepEntryCondition.Previous*`. The four subclass `Outcome` values must use the existing enum members [VERIFY enum member-to-int mapping in `src/Messaging.Contracts/StepOutcome.cs` during planning].
- **Logger generic-type caveat:** the base needs an `ILogger` (non-generic) or `ILogger<TMessage>`; the existing consumer used `ILogger<ResultConsumer>`. Use `ILogger<TMessage>` or pass the concrete subclass's logger — minor planning detail.

### Anti-Patterns to Avoid
- **Inlining `== Guid.Empty`** — use `SourceStep.IsSource(Guid)` [VERIFIED: src/Messaging.Contracts/SourceStep.cs:8]. (REINJECT/DELETE carry a real `entryId`; if any body needs a source-step guard, route through this helper.)
- **Re-awaiting the gate before each L2 op** — explicitly rejected by D-03; await once at entry.
- **A `switch`/`if` on result status in the orchestrator** — D-07 forbids it; routing is by message type via the `Outcome` knob.
- **Baking the TTL into the key builder** — `L2ProjectionKeys.CompositeBackup` is TTL-free by contract (L2ProjectionKeys.cs:45 "No TTL baked in — caller concern"); the TTL is applied at the `StringSetAsync` call only (KEEP-04).
- **A per-consumer `UsePartitioner`** — it must be endpoint-level (Pitfall 1).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Per-key ordering | A custom in-memory queue/lock keyed by exec | MassTransit `UsePartitioner` + shared `Partitioner` | Design-mandated; handles concurrency, redelivery re-slotting, hashing (`Murmur3`). |
| Bounded retry | A hand-rolled for-loop in each body | The relocated `RetryLoop.ExecuteAsync` (D-05) | One A3 implementation; surfaces exhaustion as `RetryOutcome<T>` so the body controls the terminal route. |
| Gate / async wait | A `volatile` flag + `Task.Delay` poll | `IL2HealthGate.WaitForOpenAsync` (Phase 45) | Already a `TaskCompletionSource` reset-event, no busy-wait (L2HealthGate.cs). |
| Result reconstruction | Manual record construction scattered in bodies | Mirror the Phase-44 `Build*` static helpers (ProcessorPipeline.cs:189-214) | Consistent id carriage (A1: which exec rides which message). |
| Source-step sentinel | `entryId == Guid.Empty` | `SourceStep.IsSource` | Single canonical predicate (43-D-07). |
| L2 access / multiplexer | A new `IConnectionMultiplexer` | The DI singleton from `AddBaseConsole` | Already registered (L2ProbeRecovery.cs:20 ctor-injects it; Program.cs:29 comment "do NOT add it again"). |

**Key insight:** every "primitive" this phase needs already exists and was exercised in Phase 44/45. The risk is in *composition* (partitioner config, gate-wait bound, the D-01 ripple), not in any new algorithm.

## Common Pitfalls

### Pitfall 1: `UsePartitioner` does NOT work at the consumer level — it is endpoint-scoped
**What goes wrong:** Configuring `UsePartitioner` inside a per-consumer scope (or expecting each of the five definitions to independently partition) does not give endpoint-wide ordering; different message types for the same exec could process out of order.
**Why it happens:** MassTransit issue #2855 ("UsePartitioner doesn't work on Consumer level") documents that the partitioner filter sits on the receive endpoint pipeline, not the consumer pipeline.
**How to avoid:** Configure all five `UsePartitioner<T>` calls against the **single shared receive endpoint** (`queue:keeper-recovery`), using **one shared `Partitioner` instance** so the same key collides into the same slot across types. With this codebase's definition pattern, have ONE definition own the partitioner registration (mirroring how `FaultEntryStepDispatchConsumerDefinition` solely owns the endpoint's `UseMessageRetry`, FaultEntryStepDispatchConsumerDefinition.cs:18-19), or use `AddConfigureEndpointsCallback` filtered to the endpoint name.
**Warning signs:** A test that interleaves `UPDATE` then `CLEANUP` for one exec across different consumer instances and sometimes sees `CLEANUP` run first.

### Pitfall 2 (LANDMINE): Throwing does NOT leave the message un-acked for natural broker redelivery (D-03)
**What goes wrong:** D-03 says "on timeout the delivery is left un-acked → redelivered." But in MassTransit v8, when a consumer throws — *including* `OperationCanceledException` from the linked CTS firing — the message is **moved to the error queue** (here, the consolidated `skp-dlq-1`), NOT requeued for redelivery. So a naive "throw on gate-wait timeout" would dead-letter the recovery message instead of redelivering it. [CITED: masstransit.io/documentation/concepts/exceptions; masstransit.io/documentation/concepts/consumers]
**Why it happens:** MassTransit's default error pipeline catches the exception and invokes the error transport (move-to-error); it does not rely on RabbitMQ `basic.nack(requeue:true)` for ordinary consumer exceptions.
**How to avoid (resolve in Open Question 1 before locking the gate-wait task):** The codebase already has the correct precedent — `L2ProbeRecovery` **awaits inside `Consume`, holding the delivery un-acked while it loops** (ProbeOptions: 5s × 12 = 60s, "30× margin under the 30-min consumer_timeout", ProbeOptions.cs:5-6). Two viable patterns:
  - **(A) Bounded await-then-throw-into-retry:** await the gate with a linked CTS bounded under `consumer_timeout`; on timeout throw a *transient* marker that the endpoint's `UseMessageRetry` (Immediate/Interval) re-attempts. This keeps the message moving but is bounded by the retry policy, and after retries it dead-letters — which is acceptable transient behavior under a healthy-soon assumption.
  - **(B) Keep waiting inside Consume up to a bound that stays under `consumer_timeout`, then return without acking only by letting the broker's `consumer_timeout` itself force a channel-level redelivery.** This is closest to D-03's literal wording but relies on RabbitMQ killing the channel at 30 min — heavy-handed and shared with all consumers on the channel; NOT recommended.
  - **Recommendation:** Pattern (A) with the gate-wait bound mirroring `ProbeOptions` (e.g. ~5 min, well under 30 min). This is consistent with the existing Keeper "await-inside-Consume" model and the D-03 intent ("bounded under the broker consumer timeout") without depending on a broker-level kill. Confirm the requeue-vs-dead-letter expectation with the user, since D-03's literal "redelivered" wording presumes broker requeue.
**Warning signs:** Recovery messages landing in `skp-dlq-1` during a transient gate-closed window instead of being retried after the gate opens.

### Pitfall 3: The "existing bus error queue" is already `skp-dlq-1`, not a per-queue `_error`
**What goes wrong:** D-04 says "throw → existing `_error` / `keeper-dlq`." But `BaseConsole.Core` **already consolidated** the post-exhaustion move target to a single `skp-dlq-1` via `ConsolidatedErrorTransportFilter` in the once-per-endpoint `AddConfigureEndpointsCallback` (MessagingServiceCollectionExtensions.cs:56-63,79-82). The per-queue `{queue}_error` default is replaced. So a Keeper throw already lands in `skp-dlq-1`, and `keeper-dlq` (`KeeperQueues.DeadLetter`) is a *separate* probe-give-up queue, not the consumer's error target.
**Why it happens:** The DLQ-04 consolidation shipped in an earlier phase; D-04 describes the design intent generically.
**How to avoid:** Do nothing special — throwing from a recovery consumer automatically routes to `skp-dlq-1` via the inherited error filter. The planner should NOT add per-consumer error-queue config. Phase 47's `_DLQ1` work is naming/semantics, not the move-target wiring (which exists). Note this so the verification asserts "message lands in `skp-dlq-1`" not "`keeper-recovery_error`".
**Warning signs:** A plan task that tries to configure `ConfigureError`/`SetQueueArgument` per recovery consumer — that would double-register the per-endpoint error filter (Pitfall the base library's comment at line 52 explicitly warns about).

### Pitfall 4: `UseMessageRetry` ownership is per-endpoint, not per-consumer
**What goes wrong:** Five definitions co-located on `queue:keeper-recovery` each calling `UseMessageRetry` would conflict — retry middleware is per-endpoint.
**Why it happens:** Same root cause as Pitfall 1 — endpoint-level middleware.
**How to avoid:** Exactly one of the five `ConsumerDefinition`s owns the endpoint-level config (`UseMessageRetry` + `UsePartitioner`); the other four `ConfigureConsumer` are intentional no-ops. This is the *established* codebase pattern — see `FaultEntryStepDispatchConsumerDefinition` doc-comment lines 16-19: "only this definition may register it — the sibling's ConfigureConsumer is an intentional no-op."
**Warning signs:** Duplicate retry/partitioner registration warnings or double-applied filters at bus start.

### Pitfall 5: The D-01 `Payload` ripple touches a shipped contract, a shipped golden test, and the Phase-44 send site — all three must change together
**What goes wrong:** Adding `Payload` to `KeeperReinject` without updating `ProcessorPipeline.BuildReinject` (ProcessorPipeline.cs:201-202) means the field is always empty on the wire; without updating `KeeperContractTests` the golden test does not pin it (and may need a "no `ValidatedData`" style assertion for the new field).
**Why it happens:** The contract, the producer, and the test live in three projects.
**How to avoid:** Plan the three edits as one atomic task: (1) `KeeperReinject` gets `public string Payload { get; init; } = "";` (mirror `KeeperUpdate.ValidatedData`, KeeperUpdate.cs:11 — `string`, `init`-only, `= ""` default); (2) `BuildReinject` stamps `Payload = d.Payload` (ProcessorPipeline.cs:202); (3) `KeeperContractTests.KeeperReinject_carries_EntryId_and_no_ValidatedData` (KeeperContractTests.cs:60-64) gains a `Assert.NotNull(typeof(KeeperReinject).GetProperty("Payload"))`.
**Warning signs:** A REINJECT'd `EntryStepDispatch` arriving at the processor with `Payload == ""` (recovered run silently loses author config — the exact failure D-01 prevents).

### Pitfall 6: `RetryLoop` relocation must keep all three call sites + the test compiling
**What goes wrong:** Moving `RetryLoop.cs` (and `RetryOutcome<T>`) to `BaseConsole.Core` without updating `using BaseProcessor.Core.Resilience;` breaks `ProcessorPipeline` and `RetryLoopFacts`.
**Why it happens:** Namespace change `BaseProcessor.Core.Resilience` → (new) `BaseConsole.Core.Resilience`.
**How to avoid:** Confirmed dependents [VERIFIED via Grep]: `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (real use), `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` (doc-comment mention only, line 174 — no code dependency), `tests/BaseApi.Tests/Processor/RetryLoopFacts.cs:1` (`using BaseProcessor.Core.Resilience;`). `KeyAbsentException.cs` lives in the same `BaseProcessor.Core.Resilience` namespace but is a processor-specific Pre-read sentinel — it does NOT need to move (Keeper uses its own `RecoveryDataGoneException` marker, D-04). Update the two real `using` sites. `BaseProcessor.Core` references `BaseConsole.Core` [VERIFIED: csproj Grep], so the move is mechanically safe (no circular ref, no new dependency).
**Warning signs:** CS0246 (type not found) on `RetryLoop`/`RetryOutcome` after the move.

## Code Examples

### KEEP-04 UPDATE write with TTL
```csharp
// Source: synthesized from ProcessorPipeline.cs:129-132 (write pattern) + BackupOptions.cs:8 (TTL) (VERIFIED)
var key = L2ProjectionKeys.CompositeBackup(m.CorrelationId, m.WorkflowId, m.ProcessorId, m.ExecutionId);
var write = await RetryLoop.ExecuteAsync(
    () => db.StringSetAsync(key, m.ValidatedData, expiry: TimeSpan.FromDays(backupOpts.TtlDays)), limit, ct);
if (!write.Succeeded) throw write.Error!;   // → skp-dlq-1 (D-04 / Pitfall 3)
```

### KEEP-06 INJECT (read composite → new entryId → write → Send StepCompleted → delete composite)
```csharp
// Source: synthesized from ProcessorPipeline.cs (read/write/Send/delete patterns) (VERIFIED idioms)
var composite = L2ProjectionKeys.CompositeBackup(m.CorrelationId, m.WorkflowId, m.ProcessorId, m.ExecutionId);
var read = await RetryLoop.ExecuteAsync(async () => {
    var raw = await db.StringGetAsync(composite);
    if (raw.IsNullOrEmpty) throw new RecoveryDataGoneException();   // composite gone → terminal (D-04)
    return raw.ToString();
}, limit, ct);
if (!read.Succeeded) throw read.Error!;

var entryId = NewId.NextGuid();
var write = await RetryLoop.ExecuteAsync(
    () => db.StringSetAsync(L2ProjectionKeys.ExecutionData(entryId), read.Value!), limit, ct);  // NO TTL
if (!write.Succeeded) throw write.Error!;

var completed = new StepCompleted(m.WorkflowId, m.StepId, m.ProcessorId)
    { CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId, EntryId = entryId };
var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
var sent = await RetryLoop.ExecuteAsync(async () => { await ep.Send(completed, ct); return true; }, limit, ct);
if (!sent.Succeeded) throw sent.Error!;

var del = await RetryLoop.ExecuteAsync(() => db.KeyDeleteAsync(composite), limit, ct);  // composite now redundant
if (!del.Succeeded) throw del.Error!;
```

### Partition-count / gate-wait options (D-03/D-06)
```csharp
// Source: mirror ProbeOptions.cs (VERIFIED precedent); bind in Keeper/Program.cs like Backup/Probe/Retry
public sealed class RecoveryOptions   // bound from a new "Recovery" appsettings section
{
    public int PartitionCount   { get; set; } = 8;    // D-06 default
    public int GateWaitSeconds  { get; set; } = 300;  // D-03 — well under RabbitMQ 30-min consumer_timeout
}
// Keeper/Program.cs:  builder.Services.Configure<RecoveryOptions>(builder.Configuration.GetSection("Recovery"));
// appsettings.json:   "Recovery": { "PartitionCount": 8, "GateWaitSeconds": 300 }
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Reactive `Fault<T>` Keeper recovery (`keeper-fault-recovery`) | Proactive 5-state recovery on `keeper-recovery` (this phase) | v4.0.0 (Phase 43-46) | Phase 46 is additive; the dark `FaultEntryStepDispatchConsumer` coexists, retired in Phase 48. |
| Single `ExecutionResult(Outcome)` + manifest fan-out | Four typed `Step*` records + per-item, type-routed consumers | v4.0.0 (Phase 43 D-06) | This phase builds the typed consumers (ORCH-01); no status `if`/`switch`. |
| Per-queue `{queue}_error` dead-letter | Consolidated `skp-dlq-1` (DLQ-04) | Earlier phase (already shipped) | D-04 throws route here automatically (Pitfall 3). |
| `entryId` as 64-hex content-addressed string + `H` dedup | `entryId` GUID, no `H`, at-least-once, duplicates tolerated | v4.0.0 (Phase 43) | INJECT mints a fresh GUID `entryId` freely; no dedup gate to satisfy. |

**Deprecated/outdated:**
- MassTransit 9.x — commercial; do NOT use (CPM comment Directory.Packages.props:133-136). All APIs in this research are 8.5.5.
- The design doc's `ExecutionResult(Completed, …)` phrasing for INJECT — read as `StepCompleted` per Amendment A15 (design doc line 4, 23-38) and 43-CONTEXT D-06.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The `UsePartitioner<T>` overload set in 8.5.5 includes a `string`/`Guid`/`byte[]` key provider and a shared-`Partitioner` constructor — confirmed from current MassTransit docs but not version-pinned to 8.5.5 specifically. | Pattern 1 | If 8.5.5's signature differs, the partition-key composition needs adjustment; verify against the installed assembly during Wave 0 (a one-line compile check). MEDIUM confidence. |
| A2 | `Murmur3UnsafeHashGenerator` and `Partitioner` are public types in MassTransit 8.5.5. | Pattern 1 | If renamed/internal in 8.5.5, use the `UsePartitioner<T>(int count, keyProvider)` overload (no explicit hash generator). LOW risk — the count overload is the documented default. |
| A3 | D-03's "un-acked → redelivered on timeout" is achievable; the recommended realization is await-inside-Consume + bounded retry, not broker-level requeue. | Pitfall 2 / OQ-1 | If the user requires literal broker requeue (no dead-letter ever during a gate-closed window), neither MassTransit default applies cleanly — needs custom middleware or a `consumer_timeout`-driven channel kill. HIGH-impact — flagged as Open Question 1. |
| A4 | `StepOutcome` enum members map to the ints `SelectNext` expects (`(int)Outcome`), so each subclass's `Outcome` knob selects correctly. | Pattern 4 | If the enum int values are not the assumed `Processing=0/Completed=1/Failed=2/Cancelled=3` (or whatever `StepEntryCondition.Previous*` dictates), the wrong successors advance. VERIFY `StepOutcome.cs` member values during planning. |
| A5 | Throwing from a recovery consumer routes to `skp-dlq-1` (inherited `ConsolidatedErrorTransportFilter`), with no per-consumer error config needed. | Pitfall 3 | If a future phase changed the consolidation, the error target differs. LOW risk — verified at MessagingServiceCollectionExtensions.cs:56-82. |

## Open Questions

1. **D-03 redelivery semantics — broker requeue vs bounded retry (HIGH PRIORITY).**
   - What we know: D-03 wants the gate-closed delivery "left un-acked → redelivered, partitioner re-slots it." The existing `L2ProbeRecovery` awaits inside `Consume` holding the delivery un-acked (ProbeOptions 60s, 30× margin). MassTransit v8 throwing → error queue, not requeue (Pitfall 2).
   - What's unclear: whether the user accepts "bounded await + retry policy, dead-letter after exhaustion" (Pattern A) as the realization of D-03, vs requiring literal broker requeue with no dead-letter during transient gate-closed windows.
   - Recommendation: adopt Pattern A (await inside Consume with a ~5-min bound mirroring `ProbeOptions`; on timeout throw a *transient* marker that the endpoint `UseMessageRetry` re-attempts). Surface to the user/discuss-phase before locking the gate-wait task. This is the single decision that most shapes the recovery base.

2. **`StepOutcome` enum int values (MEDIUM).**
   - What we know: 43-CONTEXT D-06d says int-aligned to `StepEntryCondition.Previous*`; `SelectNext` uses `(int)outcome`.
   - What's unclear: the exact member-to-int mapping (read `src/Messaging.Contracts/StepOutcome.cs` — not yet read in this session).
   - Recommendation: read it in Wave 0; assert each subclass `Outcome` against the expected `EntryCondition` int in a unit test.

3. **Where exactly to register `UsePartitioner` given the definition pattern (LOW).**
   - What we know: it must be endpoint-level; one definition can own it (FaultEntryStepDispatchConsumerDefinition precedent) or `AddConfigureEndpointsCallback` can.
   - What's unclear: whether `AddConfigureEndpointsCallback` (already used by the base for `ConfigureError`) is cleaner than a single owning definition for five co-located consumers.
   - Recommendation: single owning `ConsumerDefinition` (e.g. `UpdateConsumerDefinition`) owns both `UseMessageRetry` and the five `UsePartitioner` calls, the other four no-op — directly mirrors the established Keeper pattern.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| RabbitMQ (broker) | All consumers, `UsePartitioner`, error transport | Assumed (compose stack) | — | — (integration tests need it; unit tests use the MassTransit in-memory test harness) |
| Redis | All five Keeper L2 ops | Assumed (compose stack) | — | Unit tests stub `IConnectionMultiplexer`/`IDatabase` (NSubstitute) per the existing test style |
| MassTransit test harness | Consumer unit tests | ✓ (in MassTransit 8.5.5) | 8.5.5 | — |
| `consumer_timeout` (RabbitMQ default 30 min) | D-03 gate-wait bound | Broker default | 30 min | The gate-wait bound (~5 min) must stay under it; no config change needed |

No new external dependencies. All wiring is code + appsettings within the existing compose topology.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 + NSubstitute 5.3.0 [VERIFIED: Directory.Packages.props:120-121] |
| Config file | (solution-standard; tests in `tests/BaseApi.Tests/`) |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "Phase=46"` |
| Full suite command | `dotnet test` (solution) |

The established test seams (from Phase 43/44): contract pins via reflection (`KeeperContractTests`), helper facts driving the unit directly (`RetryLoopFacts`), and consumer-level facts with a substituted `IDatabase`/`ISendEndpointProvider` (`DispatchTestKit`, `PipelinePreFacts`). The MassTransit `InMemoryTestHarness` is the standard seam for consumer + partitioner behavior.

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| KEEP-04 | UPDATE writes composite with TTL = `BackupOptions.TtlDays` | unit | `dotnet test --filter "FullyQualifiedName~UpdateConsumer"` | ❌ Wave 0 |
| KEEP-05 | REINJECT present → Send `EntryStepDispatch` to `queue:{proc}` carrying `Payload`; absent → throw `RecoveryDataGoneException` → error route | unit | `--filter "FullyQualifiedName~ReinjectConsumer"` | ❌ Wave 0 |
| KEEP-06 | INJECT read→new entryId→write(no TTL)→Send `StepCompleted`→delete composite (assert all four ops + correct order) | unit | `--filter "FullyQualifiedName~InjectConsumer"` | ❌ Wave 0 |
| KEEP-07 | DELETE deletes `L2[entryId]` | unit | `--filter "FullyQualifiedName~DeleteConsumer"` | ❌ Wave 0 |
| KEEP-08 | CLEANUP deletes composite | unit | `--filter "FullyQualifiedName~CleanupConsumer"` | ❌ Wave 0 |
| KEEP-09 | Endpoint partitioned: same-exec messages serialize; partition key = 4-tuple not stepId | unit (harness) + assert key fn | `--filter "FullyQualifiedName~Partition"` | ❌ Wave 0 |
| KEEP-03/D-03 | Gate-closed → consumer waits (bounded); gate-open → proceeds; timeout behavior per OQ-1 | unit (fake gate) | `--filter "FullyQualifiedName~GateWait"` | ❌ Wave 0 |
| ORCH-01 | Each typed consumer advances via `SelectNext(Outcome)`; INJECT'd `StepCompleted` == direct (same consumer) | unit | `--filter "FullyQualifiedName~TypedResultConsumer"` | ❌ Wave 0 |
| D-01 | `KeeperReinject` carries `Payload`; `BuildReinject` stamps it | contract + unit | `--filter "FullyQualifiedName~KeeperContractTests"` (extend) + a BuildReinject fact | ⚠️ extend existing `KeeperContractTests.cs` |
| D-04 | L2/send exhaustion + data-gone marker → message reaches `skp-dlq-1` | integration (harness) | `--filter "FullyQualifiedName~RecoveryDeadLetter"` | ❌ Wave 0 |
| D-05 | `RetryLoop` compiles from `BaseConsole.Core`; `RetryLoopFacts` green after `using` update | unit (existing) | `--filter "FullyQualifiedName~RetryLoopFacts"` | ✅ exists (update `using`) |

**What to assert (key seams):**
- **Keeper bodies:** substitute `IDatabase` (via `IConnectionMultiplexer.GetDatabase()`), `ISendEndpointProvider` → substituted `ISendEndpoint`; assert exact `StringSetAsync`/`KeyDeleteAsync`/`Send` calls, args (key shape, TTL, reconstructed-message ids), and **order** (e.g. INJECT: write-before-Send-before-delete) via NSubstitute `Received.InOrder` (the codebase's documented ordering-proof technique, Directory.Packages.props:118-119 comment).
- **Gate:** inject a fake `IL2HealthGate` whose `WaitForOpenAsync` blocks until released; assert no L2 op happens before release; assert the linked-CTS bound (a `WaitForOpenAsync` that never opens hits the bound and takes the OQ-1 route).
- **Partitioner:** assert the `PartitionKey` function equals the composite-backup 4-tuple shape and excludes `StepId` (mirror `KeeperContractTests` discipline); a harness test interleaving UPDATE→CLEANUP for one exec proves ordering.
- **Typed consumers:** assert each subclass's `Outcome` value and that `SelectNext` is called with it; assert L1-miss acks (no throw); assert `DispatchAsync` preserves correlation/workflow/execution ids and seeds `entryId = m.EntryId`. Reuse the existing `ResultConsumer` test fixtures as the template.
- **Indistinguishability (ORCH-01):** a unit test that feeds a Keeper-`INJECT`-reconstructed `StepCompleted` and a direct-processor `StepCompleted` into `StepCompletedConsumer` and asserts identical advancement effects.

### Sampling Rate
- **Per task commit:** `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "Phase=46"`
- **Per wave merge:** `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (full Keeper+Orchestrator+contract suite — catches the D-01 contract ripple and the D-05 relocation breaking other consumers)
- **Phase gate:** Full solution `dotnet test` green before `/gsd-verify-work`. (Real-stack E2E of all recovery paths is TEST-01, Phase 49 — not this phase.)

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Keeper/UpdateConsumerFacts.cs` — KEEP-04
- [ ] `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs` — KEEP-05 (present + data-gone)
- [ ] `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` — KEEP-06 (ordered ops)
- [ ] `tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs` — KEEP-07
- [ ] `tests/BaseApi.Tests/Keeper/CleanupConsumerFacts.cs` — KEEP-08
- [ ] `tests/BaseApi.Tests/Keeper/RecoveryGateWaitFacts.cs` — KEEP-03/D-03 (fake gate + bound)
- [ ] `tests/BaseApi.Tests/Keeper/RecoveryPartitionFacts.cs` — KEEP-09 (key fn + harness ordering)
- [ ] `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs` — ORCH-01 (four subclasses)
- [ ] Extend `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs` — D-01 `Payload` pin
- [ ] Update `using` in `tests/BaseApi.Tests/Processor/RetryLoopFacts.cs` — D-05 relocation
- [ ] Shared Keeper consumer test fixture (substituted `IDatabase`/`ISendEndpoint`/fake `IL2HealthGate`) — reuse `DispatchTestKit` style

## Security Domain

`security_enforcement` config was not located in this session, and the project carries a documented project-wide exclusion: **"Authentication / authorization — unchanged from v1; service is open"** (REQUIREMENTS.md:75). This is an internal backend message-recovery phase with no new external surface.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Carried project-wide exclusion (open service) |
| V3 Session Management | no | No sessions |
| V4 Access Control | no | No user-facing surface |
| V5 Input Validation | partial | Wire messages are internal bus envelopes (trusted producers = own processors). Payload schema validation is the processor's concern (Phase 44), not the Keeper's. The data-gone marker (D-04) is the one validation-like branch. |
| V6 Cryptography | no | No new crypto; `Murmur3` is a *non-cryptographic* hash used only for partition distribution (not a security control) |

### Known Threat Patterns for this stack
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Poison message looping on the recovery queue | DoS | Bounded retry (`RetryLoop` + endpoint `UseMessageRetry`) → `skp-dlq-1`; the gate-wait bound prevents indefinite un-acked holds (Pitfall 2). |
| Duplicate recovery effects (at-least-once) | — | Explicitly tolerated by design (REQUIREMENTS.md:54,72 — no dedup); INJECT/REINJECT are idempotent-enough (write-then-delete, re-dispatch). |
| Composite key collision via partition hash | Tampering | `Murmur3` collisions only co-schedule unrelated execs (no correctness impact — partitioning is an ordering optimization, not an identity); the actual identity is the full 4-tuple key. |

## Sources

### Primary (HIGH confidence)
- The codebase itself (read at file:line this session): `ProcessorPipeline.cs`, `ResultConsumer.cs`, `StepDispatcher.cs`, `StepAdvancement.cs`, `RetryLoop.cs`, `L2HealthGate.cs`/`IL2HealthGate.cs`, `L2ProbeRecovery.cs`, `L2ProjectionKeys.cs`, `BackupOptions.cs`/`ProbeOptions.cs`, all five `Keeper*` contracts, `IKeeperRecoverable.cs`, `StepCompleted.cs`, `EntryStepDispatch.cs`, `SourceStep.cs`, `KeeperQueues.cs`/`OrchestratorQueues.cs`, `FaultEntryStepDispatchConsumerDefinition.cs`, `ResultConsumerDefinition.cs`, `MessagingServiceCollectionExtensions.cs`, `KeeperContractTests.cs`, `Keeper/Program.cs`, `Orchestrator/Program.cs` (result registration), `Directory.Packages.props`, Keeper `appsettings.json`.
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` (LOCKED) — five states, partition, A15 result contract.
- `.planning/phases/46-CONTEXT.md`, `43-CONTEXT.md` (D-06e blueprint), `45-CONTEXT.md` (D-11 deferral), `REQUIREMENTS.md`.

### Secondary (MEDIUM confidence)
- MassTransit Partitioner docs — https://masstransit.massient.com/configuration/middleware/partitioner (shared `Partitioner` + per-type `UsePartitioner` + `Murmur3UnsafeHashGenerator`; verified across two fetches).
- MassTransit Exceptions / Consumers docs — https://masstransit.io/documentation/concepts/exceptions , https://masstransit.io/documentation/concepts/consumers (throw → error queue, not requeue).

### Tertiary (LOW confidence — flagged for validation)
- GitHub MassTransit issue #2855 (UsePartitioner is endpoint-level not consumer-level) — title-confirmed, body not fully fetched; corroborated by the docs' endpoint-configurator examples.
- 8.5.5-exact `UsePartitioner` overload signatures (A1/A2) — confirm against the installed assembly in Wave 0.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all packages CPM-pinned and read; no new dependencies.
- Architecture (Keeper bodies, typed consumers): HIGH — direct worked examples exist in Phase 44/45 code.
- Partitioner config: MEDIUM — pattern confirmed from current docs; 8.5.5-exact signatures to verify in Wave 0 (A1/A2).
- Gate-wait redelivery (D-03): MEDIUM-with-landmine — the throw-≠-requeue semantics are HIGH-confidence; the *correct realization* of D-03 is an Open Question requiring user confirmation (OQ-1).
- Pitfalls: HIGH — Pitfalls 1/3/4/5/6 are codebase-verified; Pitfall 2 is docs-verified.

**Research date:** 2026-06-08
**Valid until:** 2026-07-08 (stable — MassTransit 8.5.5 is version-locked; the codebase is the source of truth)
