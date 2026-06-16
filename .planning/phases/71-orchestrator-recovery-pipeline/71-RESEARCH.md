# Phase 71: Orchestrator Recovery Pipeline - Research

**Researched:** 2026-06-16
**Domain:** .NET / C# backend — MassTransit 8.5.5 + StackExchange.Redis 2.13.1 (Lua `ScriptEvaluateAsync`), recovery-pipeline mirror of the shipped processor pipeline
**Confidence:** HIGH (every claim below is grounded in current repo source read this session; only the few `[ASSUMED]`/recommendation items are marked)

## Summary

Phase 71 gives the **orchestrator result-consume path** the same `messageId`-indexed
forward/recovery/keeper pipeline the processor already ships. The canonical template is
`src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — gate-once on `exist L2[messageId]` →
FORWARD or RECOVERY → gated atomic two-key cleanup tail → keeper escalation on exhaust. The phase
also splits the keeper recovery contracts by origin: a mechanical rename of `KeeperInject`/`KeeperReinject`
(+ their consumers) to `Processor*`, then the addition of `Orchestrator*` contracts + consumers that
bind on the **existing** `keeper-recovery` endpoint.

The single highest-risk integration unknown — **where the new `OrchestratorResultPipeline` plugs in** —
is now resolved (Research Q2 below). Today the orchestrator-result queue (`orchestrator-result`) binds
**four** competing consumers (`StepCompletedConsumer`, `StepFailedConsumer`, `StepCancelledConsumer`,
`StepProcessingConsumer`), all subclasses of `TypedResultConsumer<TMessage>`, whose `Consume` is **L1-only**
(no Redis) — it reads L1, calls `StepAdvancement.SelectNext(Outcome, completed, wf.Steps)` to find next
steps, and dispatches each via `IStepDispatcher.DispatchAsync`. Phase 71 reverses the 24.1 L1-only posture
by inserting the L2-gated pipeline into this exact path: the pipeline consumes the `IStepResult`, runs the
gate, and on FORWARD iterates `StepAdvancement.SelectNext` (unchanged — still the source of "next steps")
to produce the per-slot dispatch tuples.

**Primary recommendation:** Build `OrchestratorResultPipeline` as a structural clone of `ProcessorPipeline`
(NOT a shared base — D-01), keep `StepAdvancement`/`StepOutcome` as the unchanged next-step + outcome knob,
and invoke the pipeline from **inside `TypedResultConsumer<TMessage>.Consume`** (the one shared base for all
four typed consumers) so the gate runs before advancement. Sequence the keeper-contract rename as an
isolated FIRST plan (D-06), then add the `Orchestrator*` contracts/consumers, then wire the pipeline.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Result-consume gate + FORWARD/RECOVERY/cleanup | Orchestrator (`src/Orchestrator/...`) | — | The new `OrchestratorResultPipeline` is invoked from the orchestrator result consumers (D-01) |
| Next-step selection (DAG advance) | Orchestrator (`StepAdvancement`) | — | Unchanged L1-only pure match; FORWARD iterates it for "next steps" |
| Atomic index+data Lua write | Redis (server-side Lua) | Orchestrator (computes TTL ARGV) | ONE script; TTLs computed in C# (no RNG in Lua) — Phase-68 TEST-06 guard |
| Keeper escalation (Inject/Reinject/Delete) | Keeper (`keeper-recovery` endpoint) | — | The two new `Orchestrator*` consumers bind on the SAME endpoint (D-08); no new queue |
| Heterogeneous slot encoding (JSON tuple) | Messaging.Contracts (key fmt) + Orchestrator (JSON ser/de) | — | `L2ProjectionKeys.MessageIndex` HASH shape is shared; the JSON value lives orchestrator-side (D-02) |
| Downstream dispatch (`EntryStepDispatch`) | Orchestrator (`IStepDispatcher` / pipeline send-owner) | Keeper (`OrchestratorInject` completes the copy + dispatches) | FORWARD sends to `queue:{nextProcessorId}` then retires the slot (ORCV-04) |

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01 — Pipeline organization: MIRROR + ADAPT.** A **new** `OrchestratorResultPipeline` class mirrors
  `ProcessorPipeline`'s gate → forward/recovery → cleanup-tail skeleton; **NOT** a shared base (lowest coupling,
  zero risk to the shipped processor). Reuse processor building blocks verbatim where identical: `RetryLoop`,
  the `exist L2[messageId]` gate-once, the gated atomic two-key cleanup tail (`DeleteTerminalAsync` analog),
  `SendKeeper`/`SendResult` send-owners (exhaust → throw → broker redelivery, no `_error`).
  - **Gate (ORCV-01):** `exist L2[messageId]` once (messageId = inbound result's broker message id). absent →
    FORWARD; present → RECOVERY; gate L2-op exhaustion → `OrchestratorReinject`, end round trip, **no cleanup**.
  - Lives orchestrator-side (new file under `src/Orchestrator/...`; exact namespace Claude's discretion).
- **D-02 — Heterogeneous slot encoding: JSON object per slot.** Same `L2ProjectionKeys.MessageIndex(messageId)`
  HASH-of-`int slot → value` structure as the processor; the HASH field **value** is a small **JSON object**
  `{ nextStepId, nextProcessorId, payload, newEntryId }`. RECOVERY parses the JSON to reconstruct the per-slot
  `EntryStepDispatch`.
- **D-03 — Atomic FORWARD op: ONE Lua script.** Mirror `AtomicForwardWrite`. Per next step, ONE Lua op over
  `KEYS = { MessageIndex(messageId), ExecutionData(newEntryId) }`: HSET the index slot = D-02 JSON tuple,
  whole-hash PEXPIRE (index TTL), copy `L2[origin entryId] → L2[newEntryId]` with the data TTL. TTLs computed in
  C#, passed as ARGV (NO RNG in Lua). Exhaust → single `OrchestratorInject` (no silent drop). After the write:
  send `EntryStepDispatch` to `queue:{nextProcessorId}`, then retire the slot to `guid.empty`.
  - Copy semantics (Claude's discretion, inside the one script): `COPY src dst REPLACE` then `PEXPIRE dst`, OR
    `GET`+`SET dst PX`. Either, as long as it is ONE atomic script and the dest carries the data TTL.
- **D-04 — Cleanup tail + delete invariant.** Gated atomic two-key `DEL` of `L2[messageId]` + `L2[origin entryId]`,
  run only if no slot escalated to the keeper this pass (forward) / at the end of a recovery pass. Delete exhaust →
  out-of-band `KeeperDelete` (best-effort `PERSIST` index then escalate). **`OrchestratorInject` and
  `OrchestratorReinject` NEVER delete a key.** `KeeperDelete` stays the ONLY deleting keeper state.
- **D-05 — RECOVERY.** Mirror `RunRecoveryAsync`: read index slots; per slot 3-way — data exists → re-send the
  reconstructed dispatch; clean not-exist → drop, no retire; L2 fault → leave the slot intact. Tail `OrchestratorReinject`s
  if any slot faulted, else runs the D-04 two-key delete. A redelivery re-sends stable persisted entryIds, skips
  retired (`guid.empty`) slots.
- **D-06 — Keeper contract split by origin, RENAME contracts + consumers, dedicated FIRST plan.** Route-by-type,
  no discriminator switch. Rename (isolated first plan, before any `Orchestrator*`): `KeeperInject` → `ProcessorInject`,
  `KeeperReinject` → `ProcessorReinject` (file + type); `KeeperDelete` stays. `InjectConsumer` → `ProcessorInjectConsumer`,
  `ReinjectConsumer` → `ProcessorReinjectConsumer`; `DeleteConsumer` stays. Update ALL ~25 `.cs` reference sites.
  Then add `Orchestrator*` (later wave): `OrchestratorInject`, `OrchestratorReinject` (both implement
  `IKeeperRecoverable`), `OrchestratorInjectConsumer`, `OrchestratorReinjectConsumer` (extend `RecoveryConsumerBase<T>`).
- **D-07 — OrchestratorReinject contract shape: outcome enum + union fields.** Carries the base 5-id + `EntryId` +
  a `StepOutcome` discriminator + the result-field superset (`ErrorMessage`, `CancellationMessage`). A factory maps
  outcome → the right `IStepResult` subtype and re-injects to `queue:orchestrator-result`. No status if/switch beyond
  the factory. (Reuse `StepOutcome` directly OR mint a parallel discriminator: Claude's discretion.) `OrchestratorInject`
  completes the index+data copy and sends `EntryStepDispatch` downstream.
- **D-08 — Bind the two new consumers on the existing endpoint.** Add to `RecoveryEndpointBinder`'s
  `ConnectReceiveEndpoint` callback: `UsePartitioner<OrchestratorInject>` and `UsePartitioner<OrchestratorReinject>`
  on the SAME `Partitioner` instance with the SAME `ReinjectConsumerDefinition.PartitionGuid` 4-tuple key selector,
  plus `ConfigureConsumer` for both. Register both in Keeper `Program.cs` with `ExcludeFromConfigureEndpoints()`. No
  new queue; same health gate / partitioner / exhaustion posture.
- **D-09 — Negative-guard facts, behavioral.** Prove `OrchestratorInject`/`OrchestratorReinject` never delete via
  behavioral NSubstitute `DidNotReceive()` on BOTH `KeyDeleteAsync` overloads, each co-asserted with a positive
  side-effect. (Extend the existing invariant fact vs a parallel `Orchestrator*` fact: Claude's discretion.)
- **D-10 — Fold in the Phase-70 code-review WR-01 fix.** While renaming touches `RecoveryTestKit.cs`, add the missing
  5-arg `StringSetAsync(RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags)` stub to `RecoveryTestKit.Db()`.

### Claude's Discretion
- Exact namespaces/filenames/class names for the new pipeline, contracts, consumers, facts.
- COPY-vs-GET/SET inside the single atomic Lua script (D-03).
- Reuse `StepOutcome` vs a parallel discriminator for `OrchestratorReinject` (D-07).
- Exact JSON property names of the slot tuple (D-02).
- Extend vs duplicate the delete-invariant fact (D-09).

### Deferred Ideas (OUT OF SCOPE)
- The processor's INJECT index-slot-write divergence from spec §8 (deferred in Phase 70) remains deferred — NOT reopened.
- A stricter keeper-recovery endpoint startup posture (connect-stopped) remains a future option, untouched.
- New queues/endpoints; a status discriminator `switch`; any change to the processor's runtime behavior beyond the rename.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| ORCV-01 | Result-consume path gates on `exist L2[messageId]`; absent→FORWARD, present→RECOVERY; gate exhaust→REINJECT, end (no cleanup) | `ProcessorPipeline.RunAsync` lines 128-141 is the exact template; messageId = inbound result's broker `MessageId`. Pipeline invoked from `TypedResultConsumer.Consume` (Q2) |
| ORCV-02 | FORWARD per next step: ONE atomic op (HSET slot + whole-hash PEXPIRE + copy `L2[origin]→L2[new]` w/ data TTL); exhaust→single `OrchestratorInject` | `AtomicForwardWrite` const (lines 96-100) + the FORWARD-Post loop (lines 287-340). Copy adapts the SET-data leg (Q3) |
| ORCV-03 | Each slot value carries `(nextStepId, nextProcessorId, payload, newEntryId)` JSON tuple for heterogeneous reconstruction | `MessageIndex` HASH (`L2ProjectionKeys.cs:61`); slot fields map 1:1 to `StepAdvancement.SelectNext` yield `(stepId, step.ProcessorId, step.Payload)` + minted `newEntryId` (Q4) |
| ORCV-04 | FORWARD sends `EntryStepDispatch` to `queue:{nextProcessorId}`, retires slot to `guid.empty`; cleanup tail (atomic 2-key DEL) runs only if no escalation; delete exhaust→DELETE | `DeleteTerminalAsync` (lines 354-368) + GATE-01 `escalated` flag (lines 286, 346-347); dispatch via `StepDispatcher`/inline send-owner |
| ORCV-05 | RECOVERY 3-way per-slot (exists→re-send; clean not-exist→drop no retire; L2 fault→leave slot); tail REINJECTs if any faulted else 2-key delete; redelivery re-sends stable entryIds, skips retired | `RunRecoveryAsync` (lines 156-219) is the exact template; adapt re-send to reconstruct `IStepResult` from the JSON tuple instead of a fresh `StepCompleted` |
| ORCV-06 | Split contracts by origin (rename `Keeper*`→`Processor*`, add `Orchestrator*`, `KeeperDelete` shared); 2 new consumers on same `keeper-recovery` endpoint; `OrchestratorReinject` rebuilds result + re-injects to `queue:orchestrator-result`; `OrchestratorInject` completes copy + dispatches | Rename blast radius (Q1); bind symmetry (Q5, D-08); factory shape (D-07) maps outcome→`IStepResult` subtype |
| ORCV-07 | Delete invariant orchestrator-side: keys deleted ONLY in cleanup tail / out-of-band DELETE; `OrchestratorInject`/`OrchestratorReinject` never delete | `KeeperDeleteInvariantFacts` behavioral pattern (Q6, D-09) to mirror for `Orchestrator*` |
</phase_requirements>

## Standard Stack

This phase adds NO new packages — it mirrors existing infrastructure. The relevant pinned versions
(verified in `Directory.Packages.props` this session):

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| StackExchange.Redis | 2.13.1 | L2 ops + `ScriptEvaluateAsync` Lua | `[VERIFIED: Directory.Packages.props:131]` — the version the consumers bind against; drives the D-10 overload fix |
| MassTransit | 8.5.5 | Bus, `UsePartitioner`, `ConnectReceiveEndpoint`, `IConsumer<T>` | `[CITED: RecoveryEndpointBinder.cs comment "8.5.5"]` — `Partitioner` + `Murmur3UnsafeHashGenerator` live in `MassTransit.Middleware` |
| xunit.v3 | 3.2.2 (MTP) | Test framework (BaseApi.Tests) | `[VERIFIED: BaseApi.Tests.csproj]` — `OutputType=Exe` + `UseMicrosoftTestingPlatformRunner=true` |
| NSubstitute | (pinned) | Behavioral mocks (`Received`/`DidNotReceive`) | `[VERIFIED: KeeperDeleteInvariantFacts.cs]` — the established negative-guard idiom |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Shared pipeline base (`ProcessorPipeline` ⊇ `OrchestratorResultPipeline`) | New independent `OrchestratorResultPipeline` (D-01) | **LOCKED to independent class** — the two diverge enough (heterogeneous slots, copy-not-write, `IStepResult` reconstruction) that a shared base over-abstracts and risks the shipped processor path |
| Redis `COPY src dst REPLACE` + `PEXPIRE` | `GET` + `SET dst PX` | Both acceptable inside ONE atomic script (D-03 discretion). See Q3 recommendation |

**Installation:** none — no `dotnet add package` needed.

**Version verification:** `[VERIFIED]` StackExchange.Redis 2.13.1 and MassTransit 8.5.5 are the pinned
versions in this repo as of this session; no upgrade is in scope.

## Architecture Patterns

### System Architecture Diagram

```
                        ┌─────────────────────── orchestrator-result queue ───────────────────────┐
 processor / keeper ──► │  IStepResult (StepCompleted | StepFailed | StepCancelled | StepProcessing) │
   sends a result       └──────────────────────────────────┬──────────────────────────────────────┘
                                                            │  4 competing consumers
                                                            ▼  (all extend TypedResultConsumer<T>)
                         ┌──────────────────────────────────────────────────────────────┐
                         │  TypedResultConsumer<TMessage>.Consume  (the integration seam) │
                         │      ▼  NEW: invoke OrchestratorResultPipeline.RunAsync(...)   │
                         └───────────────────────────────┬──────────────────────────────┘
                                                         ▼
                          exist L2[msg:messageId] ?  (gate-once, bounded RetryLoop)
                            │ exhaust → OrchestratorReinject → END (no cleanup)
            ┌───────────────┴───────────────┐
       absent (FORWARD)              present (RECOVERY)
            ▼                                ▼
  L1 read + StepAdvancement       HGETALL skp:msg:{messageId}
  .SelectNext(Outcome,…)          per slot, parse JSON tuple:
  per next step:                    data exists → re-send reconstructed IStepResult
   (1) mint newEntryId             clean not-exist → drop (no retire)
   (2) ONE atomic Lua:             L2 fault → leave slot intact
       HSET slot = JSON tuple        ▼ send-before-retire (re-dispatch downstream)
       PEXPIRE hash (index TTL)    tail:
       COPY L2[origin]→L2[new]       any fault → OrchestratorReinject (no delete)
       (or GET+SET) w/ data TTL      else → atomic 2-key DEL
     exhaust → OrchestratorInject
   (3) Send EntryStepDispatch → queue:{nextProcessorId}
   (4) retire slot = guid.empty
   tail (GATE-01): if no escalation → atomic 2-key DEL(L2[messageId], L2[origin entryId])
                   delete exhaust → KeeperDelete (persist index, then escalate)
            │
            ▼
   ┌──────────────────────── keeper-recovery queue (SAME endpoint) ────────────────────────┐
   │ ProcessorInject | ProcessorReinject | KeeperDelete | OrchestratorInject | OrchestratorReinject │
   │  partitioned on IKeeperRecoverable 4-tuple (corr:wf:proc:exec) — origin-agnostic         │
   └──────────────────────────────────────────┬────────────────────────────────────────────┘
       OrchestratorInject → complete index+data copy, send EntryStepDispatch downstream
       OrchestratorReinject → factory(outcome)→IStepResult subtype, re-inject to queue:orchestrator-result
```

### Recommended Project Structure
```
src/Orchestrator/
├── Consumers/
│   └── TypedResultConsumer.cs        # MODIFY: invoke the pipeline from Consume (the gate seam)
├── Recovery/                          # NEW folder (recommended) for the orchestrator-side pipeline
│   └── OrchestratorResultPipeline.cs  # NEW: the ProcessorPipeline mirror (gate→fwd/recov→tail)
src/Messaging.Contracts/
├── ProcessorInject.cs                 # RENAMED from KeeperInject.cs
├── ProcessorReinject.cs               # RENAMED from KeeperReinject.cs
├── OrchestratorInject.cs              # NEW (implements IKeeperRecoverable)
├── OrchestratorReinject.cs            # NEW (implements IKeeperRecoverable; + StepOutcome + msg superset)
└── KeeperDelete.cs                    # UNCHANGED (shared)
src/Keeper/Recovery/
├── ProcessorInjectConsumer.cs         # RENAMED from InjectConsumer.cs
├── ProcessorReinjectConsumer.cs       # RENAMED from ReinjectConsumer.cs
├── OrchestratorInjectConsumer.cs      # NEW (extends RecoveryConsumerBase<OrchestratorInject>)
├── OrchestratorReinjectConsumer.cs    # NEW (extends RecoveryConsumerBase<OrchestratorReinject>)
├── DeleteConsumer.cs                  # UNCHANGED
└── RecoveryEndpointBinder.cs          # MODIFY: +2 UsePartitioner + +2 ConfigureConsumer
```

### Pattern 1: Gate-once dispatcher (ORCV-01)
**What:** Branch on `exist L2[messageId]` via one bounded-retry existence check; exhaust → REINJECT + end.
**When to use:** the entry point of `OrchestratorResultPipeline.RunAsync`.
**Example:**
```csharp
// Source: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:128-141 (mirror verbatim)
var exists = await RetryLoop.ExecuteAsync(
    () => db.KeyExistsAsync(L2ProjectionKeys.MessageIndex(messageId)), limit, ct);
if (!exists.Succeeded) { await SendKeeper(BuildReinject(...), limit, ct); return; }   // no cleanup
if (exists.Value) await RunRecoveryAsync(...); else await RunForwardAsync(...);
```
**messageId source:** the inbound result's broker `MessageId`. In `TypedResultConsumer.Consume`,
read `context.MessageId` (a `Guid?` on `ConsumeContext`); it is the same id space the processor uses
for its inbound `EntryStepDispatch`. `[ASSUMED]` — confirm `context.MessageId` is non-null on the result
envelope (MassTransit always stamps a MessageId on `Send`; A1 below).

### Pattern 2: The single atomic Lua write (ORCV-02, ORCV-03)
**What:** ONE `ScriptEvaluateAsync` over `KEYS={MessageIndex, ExecutionData(newEntryId)}`; TTLs in ARGV.
**When to use:** FORWARD-Post, per next step.
**Example:**
```csharp
// Source: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:96-100 (processor constant — the template)
private const string AtomicForwardWrite = @"
    redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
    redis.call('PEXPIRE', KEYS[1], ARGV[4])
    redis.call('SET', KEYS[2], ARGV[3], 'PX', ARGV[5])
    return 1";
// Orchestrator variant (D-03): ARGV[2] is the JSON tuple; the data leg COPIES the origin key (see Q3).
```
The C# call (lines 298-309) passes `slot, value, data, (long)indexTtlMs, (long)dataTtlMs` as `RedisValue[]`.
Keep TTL computation in C# (`SlotTtl()` lines 110-118) — **never** call a Redis RNG inside Lua (TEST-06 guard).

### Pattern 3: Gated atomic two-key cleanup tail (ORCV-04, ORCV-07)
**What:** Atomic `KeyDeleteAsync(new RedisKey[]{ ExecutionData(origin), MessageIndex(messageId) })`; on
exhaust best-effort `PERSIST` the index then `SendKeeper(BuildDelete(...))`. Gated on `!escalated`.
**Example:**
```csharp
// Source: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:354-368
var del = await RetryLoop.ExecuteAsync(() => db.KeyDeleteAsync(new RedisKey[]
    { L2ProjectionKeys.ExecutionData(originEntryId), L2ProjectionKeys.MessageIndex(messageId) }), limit, ct);
if (del.Succeeded) return;
await RetryLoop.ExecuteAsync(() => db.KeyPersistAsync(L2ProjectionKeys.MessageIndex(messageId)), limit, ct);
await SendKeeper(BuildDelete(...), limit, ct);
```
GATE-01: set `escalated=true` on any FORWARD-Post `OrchestratorInject`; run the tail only if `!escalated`
(processor lines 286, 320-321, 346-347).

### Pattern 4: Recovery 3-way per-slot (ORCV-05)
**What:** HGETALL the index; per slot classify {completed / clean-not-exist / infra}; send-before-retire;
tail REINJECT ⊻ delete.
**Example:** `ProcessorPipeline.RunRecoveryAsync` lines 156-219 — the exact structure. The orchestrator
divergence: instead of `BuildCompleted(d, NewId.NextGuid(), entryId)`, **parse the slot's JSON tuple** and
reconstruct the right `IStepResult` (here every recovered slot is a completed FORWARD output, so the re-send
is a `StepCompleted` carrying `newEntryId`). The slot's data key for the existence check is `newEntryId`
(the slot's `newEntryId` field), not the origin.

### Pattern 5: Recovery consumer + Guard base (ORCV-06)
**What:** New `OrchestratorInjectConsumer`/`OrchestratorReinjectConsumer` extend `RecoveryConsumerBase<T>`
(constructor: `IConnectionMultiplexer, ISendEndpointProvider, IOptions<RetryOptions>` [+ metrics/logger as needed]).
Every L2 op + Send goes through `Guard`/`Guard<T>`; gating is at the endpoint.
**Example:** `InjectConsumer.cs` (write-then-send, no delete) and `ReinjectConsumer.cs` (STRLEN-present check,
then reconstruct + send) are the two structural templates.

### Anti-Patterns to Avoid
- **Status `if`/`switch` for routing:** routing is by message type (MassTransit dispatch) + the `StepOutcome`
  knob. The ONLY allowed branch is the D-07 outcome→`IStepResult` factory.
- **RNG inside Lua:** breaks the TEST-06 index/data-desync guard. TTLs are computed in C# and passed as ARGV.
- **Deleting from `OrchestratorInject`/`OrchestratorReinject`:** DELETE (via `KeeperDelete`) is the only deleter.
- **A new queue/endpoint:** the two new consumers bind on the existing `keeper-recovery` endpoint (D-08).
- **Shared pipeline base:** D-01 locks an independent `OrchestratorResultPipeline`.
- **Bare `[JsonPropertyName]` on a positional record param:** STJ ignores it — use `[property: JsonPropertyName]`
  (the established convention in `StepProjection.cs`). Relevant if the D-02 slot tuple is a positional record.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Bounded retry around L2/Send | A custom loop | `RetryLoop.ExecuteAsync` (`BaseConsole.Core.Resilience`) / `RecoveryConsumerBase.Guard` | Established exhaust→throw/escalate semantics; the whole resilience posture depends on it |
| Atomic multi-key write | Sequential HSET+EXPIRE+SET | ONE Lua `ScriptEvaluateAsync` | Prevents partial index-without-data state (spec §4.3 step 3) |
| Partition-key derivation | Custom hashing | `ReinjectConsumerDefinition.PartitionGuid` (the 4-tuple SHA→Guid) | Origin-agnostic; pinned by `RecoveryPartitionFacts` |
| Next-step selection | Re-walk the DAG | `StepAdvancement.SelectNext(Outcome, completed, wf.Steps)` | Pure, tested, unchanged; the source of FORWARD's "next steps" |
| Outcome→result-type mapping | Polymorphic blob deserialize | The D-07 factory (outcome enum → subtype) | Consistent with `ProcessorReinject` reconstruction; no serialized polymorphism on the wire |
| L2 key formats | String interpolation in the pipeline | `L2ProjectionKeys.MessageIndex` / `.ExecutionData` | Single source of truth (`skp:msg:{id:D}` / `skp:data:{id:D}`) |

**Key insight:** every building block this phase needs already ships and is test-pinned. The work is
*composition and mirroring*, not invention — which is exactly the "consistent with processor" governing principle.

## Common Pitfalls

### Pitfall 1: messageId provenance on the result envelope
**What goes wrong:** Using the result's `EntryId` or `ExecutionId` as the gate key instead of the broker `MessageId`.
**Why it happens:** the processor pipeline receives `messageId` as an explicit `RunAsync` param; the orchestrator
pipeline must source it from `ConsumeContext.MessageId`.
**How to avoid:** thread `context.MessageId!.Value` (the inbound result's broker id) into `RunAsync`. The gate key
is `L2ProjectionKeys.MessageIndex(messageId)` — the SAME `skp:msg:{messageId:D}` format the processor writes.
**Warning signs:** RECOVERY never triggers on redelivery (wrong key never pre-exists), or FORWARD double-processes.

### Pitfall 2: Origin entryId vs slot newEntryId in RECOVERY
**What goes wrong:** RECOVERY checks existence of the *origin* entryId instead of the slot's *newEntryId*.
**Why it happens:** the processor stores a bare entryId per slot (the output it minted); the orchestrator stores a
JSON tuple where `newEntryId` is the copied-output key. RECOVERY's existence check must target `newEntryId`.
**How to avoid:** parse the JSON tuple; check `ExecutionData(tuple.newEntryId)`; the two-key cleanup tail deletes
`ExecutionData(originEntryId)` + `MessageIndex(messageId)` (origin = the inbound result's `EntryId`).
**Warning signs:** RECOVERY drops slots that should re-send, or the cleanup leaks the origin key.

### Pitfall 3: COPY does not carry TTL
**What goes wrong:** `COPY src dst REPLACE` leaves `dst` with no TTL → an immortal data key.
**Why it happens:** Redis `COPY` does not copy the source TTL.
**How to avoid:** if using COPY, follow with `PEXPIRE dst <dataTtlMs>` **inside the same script** (D-03 explicitly
requires the dest carry the data TTL). The `GET`+`SET dst PX` variant sets the TTL inline.
**Warning signs:** orphaned `skp:data:*` keys that never expire after a copy.

### Pitfall 4: The 5-arg `StringSetAsync` overload (D-10 / WR-01)
**What goes wrong:** `RecoveryTestKit.Db()` stubs the legacy 6-arg `StringSetAsync`; SE.Redis 2.13.1 binds the
2-arg production call to a NEW 5-arg `StringSetAsync(RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags)`,
leaving the stub dead and tests relying on NSubstitute default-return.
**How to avoid:** add the 5-arg stub (D-10). Exact fix from `70-REVIEW.md` WR-01:
```csharp
db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
    Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>()).Returns(true);
```
**Warning signs:** an `Inject`-shaped fact passes even when the write throws on retry exhaustion.

### Pitfall 5: `dotnet test --filter` is silently ignored (MTP)
**What goes wrong:** targeted test runs execute the full suite (638 tests) because xunit.v3/MTP ignores `--filter`.
**How to avoid:** use the MTP arg passthrough — `dotnet test ... -- --filter-method "*KeeperDeleteInvariant*"`.
**Warning signs:** a "targeted" run takes minutes and reports hundreds of tests.

### Pitfall 6: Static + connect endpoint collision
**What goes wrong:** registering the new consumers WITHOUT `ExcludeFromConfigureEndpoints()` collides with the
binder's `ConnectReceiveEndpoint` (two sources configure `keeper-recovery`).
**How to avoid:** register both new consumers in Keeper `Program.cs` with `.ExcludeFromConfigureEndpoints()`
(exactly as the existing three; `Program.cs` lines for the `AddBaseConsoleMessaging` block).
**Warning signs:** startup throws / a shadowed handle / duplicate consumer attach.

## Runtime State Inventory

This is a code + contract change (a rename plus new code) on the result-consume path. No data migration,
no live-service reconfiguration, no OS-registered state, no secret/env renames are implied. The rename
changes **message type names on the bus**, which has a runtime consideration:

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None — the L2 key formats (`skp:msg:`, `skp:data:`) are unchanged by the rename; the JSON slot tuple is NEW state but greenfield (no existing records to migrate). Verified: `L2ProjectionKeys.cs` builders unchanged. | None |
| Live service config | **MassTransit message-type URN changes.** Renaming `KeeperInject`→`ProcessorInject` changes the message contract's type name; **in-flight messages of the old type on `keeper-recovery` at deploy time would not bind to the renamed consumer.** | Deploy on a **drained** `keeper-recovery` queue (the keeper gate + clean-state stack precedent). The rename is a same-deploy contract+consumer change, so no version skew within a deploy; cross-deploy in-flight messages are the only window. `[ASSUMED — see A2]` |
| OS-registered state | None — no Task Scheduler / pm2 / systemd references to these types. Verified by grep (no non-`.cs` references). | None |
| Secrets/env vars | None — no env var or SOPS key references `KeeperInject`/`KeeperReinject`. | None |
| Build artifacts | Test project (`BaseApi.Tests`) and three service projects (`Keeper`, `Orchestrator`, `BaseProcessor.Core`) recompile; `Messaging.Contracts` is the shared leaf. No egg-info / compiled-binary equivalents. Verified: all references are `.cs`. | Rebuild all (standard `dotnet build`) |

**The canonical question — after every file is updated, what runtime systems still carry the old string?**
Only the broker: any `KeeperInject`/`KeeperReinject` message already enqueued on `keeper-recovery` before the
deploy. Because the keeper drains on a clean-state stack and the rename is atomic within a deploy, this window
is the standard contract-rename consideration, not a migration task. Flag for the planner: **plan the rename
deploy against a drained recovery queue** (consistent with the close-gate net-zero protocol in project memory).

## Code Examples

### FORWARD slot tuple construction (ORCV-03)
```csharp
// The next-step source is StepAdvancement.SelectNext, which yields (Guid stepId, StepProjection step).
// StepProjection (Messaging.Contracts.Projections) exposes: ProcessorId, Payload, NextStepIds, EntryCondition.
// Source: src/Messaging.Contracts/Projections/StepProjection.cs:16-20
foreach (var (nextStepId, step) in advancement.SelectNext(outcome, completed, wf.Steps))
{
    var newEntryId = NewId.NextGuid();
    var tuple = JsonSerializer.Serialize(new {                 // D-02 JSON value (property names: discretion)
        nextStepId, nextProcessorId = step.ProcessorId, payload = step.Payload, newEntryId });
    // ONE atomic Lua: HSET slot=tuple ; PEXPIRE hash ; COPY/SET data(newEntryId)=copy(data(originEntryId)) PX
    // ... ScriptEvaluateAsync(OrchestratorForwardWrite, KEYS={MessageIndex(messageId), ExecutionData(newEntryId)}, ARGV=[slot, tuple, originEntryId-or-data, indexTtlMs, dataTtlMs])
}
```

### Outcome → IStepResult factory (D-07, ORCV-06)
```csharp
// OrchestratorReinjectConsumer reconstructs the right subtype from the carried StepOutcome (no polymorphic blob).
// Source pattern: ProcessorPipeline.cs:265-271 (the e switch → Step* builder); StepOutcome enum values 0-3.
IStepResult result = m.Outcome switch
{
    StepOutcome.Completed  => new StepCompleted(m.WorkflowId, m.StepId, m.ProcessorId)
                                  { CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId, EntryId = m.EntryId },
    StepOutcome.Failed     => new StepFailed(m.WorkflowId, m.StepId, m.ProcessorId)
                                  { CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId, ErrorMessage = m.ErrorMessage },
    StepOutcome.Cancelled  => new StepCancelled(m.WorkflowId, m.StepId, m.ProcessorId)
                                  { CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId, CancellationMessage = m.CancellationMessage },
    StepOutcome.Processing => new StepProcessing(m.WorkflowId, m.StepId, m.ProcessorId)
                                  { CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId },
    _ => new StepFailed(m.WorkflowId, m.StepId, m.ProcessorId) { CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId },
};
var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}")), ct);
await Guard(() => ep.Send((object)result, CancellationToken.None), ct);   // re-inject to orchestrator-result
```
Note: `StepCompleted`/`StepProcessing` have NO diagnostic field; `StepFailed.ErrorMessage` /
`StepCancelled.CancellationMessage` are the only union fields (verified in the four contract files).

### Binder addition (D-08, ORCV-06)
```csharp
// Source: src/Keeper/Recovery/RecoveryEndpointBinder.cs:60-66 — add two lines in each block.
cfg.UsePartitioner<OrchestratorInject>(partition,   p => ReinjectConsumerDefinition.PartitionGuid(p.Message));
cfg.UsePartitioner<OrchestratorReinject>(partition, p => ReinjectConsumerDefinition.PartitionGuid(p.Message));
cfg.ConfigureConsumer<OrchestratorInjectConsumer>(ctx);
cfg.ConfigureConsumer<OrchestratorReinjectConsumer>(ctx);
// And in Keeper/Program.cs AddBaseConsoleMessaging block:
x.AddConsumer<OrchestratorInjectConsumer>().ExcludeFromConfigureEndpoints();
x.AddConsumer<OrchestratorReinjectConsumer>().ExcludeFromConfigureEndpoints();
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| L1-only straight-through result advancer (`TypedResultConsumer`, no Redis) | L2-gated result pipeline (this phase reverses 24.1) | Phase 71 | The result path regains idempotent forward/recovery via the index key |
| Monolithic `KeeperInject`/`KeeperReinject` (one origin) | Split by origin: `Processor*` + `Orchestrator*` (route-by-type) | Phase 71 | Two origins escalate distinctly with no discriminator switch |
| Three keeper recovery consumers on `keeper-recovery` | Five consumers on the SAME endpoint | Phase 71 | No new queue; same partitioner/gate/posture |

**Deprecated/outdated:**
- The processor's 6-arg `StringSetAsync` test stub (D-10/WR-01) — SE.Redis 2.13.1 binds the 5-arg
  `Expiration/ValueCondition` overload; the 6-arg stub is dead.

## Validation Architecture

> nyquist_validation: treated as enabled (no `.planning/config.json` opt-out found this session).

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 under Microsoft.Testing.Platform (MTP) |
| Config file | `tests/BaseApi.Tests/xunit.runner.json` (maxParallelThreads cap, copied next to the assembly) |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -- --filter-method "*<Name>*"` |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (~638 tests) |

**CRITICAL (project memory):** `dotnet test --filter` is **silently ignored** by xunit.v3/MTP — it runs all 638.
Targeted runs MUST use the MTP passthrough `-- --filter-method "*Pattern*"`.

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| ORCV-01 | gate exist→FORWARD/RECOVERY; gate exhaust→REINJECT (no cleanup) | unit (pipeline fact, NSubstitute db) | `dotnet test ... -- --filter-method "*OrchestratorResultPipeline*Gate*"` | ❌ Wave 0 |
| ORCV-02 | ONE atomic Lua write; exhaust→single OrchestratorInject | unit (assert single `ScriptEvaluateAsync`; on stubbed-throw → one Inject send) | `... -- --filter-method "*OrchestratorResultPipeline*Forward*"` | ❌ Wave 0 |
| ORCV-03 | slot value = JSON tuple `(nextStepId,nextProcessorId,payload,newEntryId)` | unit (assert HSET ARGV is the expected JSON) | `... -- --filter-method "*Forward*Tuple*"` | ❌ Wave 0 |
| ORCV-04 | dispatch then retire; gated 2-key DEL; delete exhaust→DELETE | unit (capture `EntryStepDispatch` send to `queue:{procId}`; assert retire HSET=guid.empty; assert/Skip DEL by escalation) | `... -- --filter-method "*Forward*Cleanup*"` | ❌ Wave 0 |
| ORCV-05 | recovery 3-way per-slot; tail REINJECT⊻delete; skip retired | unit (mirror `PipelineRecoveryFacts` shape) | `... -- --filter-method "*OrchestratorResultPipeline*Recovery*"` | ❌ Wave 0 |
| ORCV-06 (rename) | `Keeper*`→`Processor*` rename compiles green; contract tests updated | compile + contract (`KeeperContractTests` renamed) | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (full, post-rename) | partial (existing facts to update) |
| ORCV-06 (new) | 2 new consumers bind on `keeper-recovery`; reinject factory→`orchestrator-result`; inject→copy+dispatch | unit (consumer facts mirroring `InjectConsumerFacts`/`ReinjectConsumerFacts`) + partition fact | `... -- --filter-method "*OrchestratorInjectConsumer*"` / `"*OrchestratorReinjectConsumer*"` | ❌ Wave 0 |
| ORCV-07 | Orchestrator* never delete (both `KeyDeleteAsync` overloads) + positive co-assert | unit (mirror `KeeperDeleteInvariantFacts`) | `... -- --filter-method "*DeleteInvariant*"` | partial (extend or duplicate, D-09) |

### Sampling Rate
- **Per task commit:** the targeted `-- --filter-method "*<area>*"` quick run for the touched behavior.
- **Per wave merge:** the rename wave runs the FULL suite (rename must compile + all existing facts green);
  pipeline waves run the `*OrchestratorResultPipeline*` + `*Orchestrator*Consumer*` + `*DeleteInvariant*` subsets.
- **Phase gate:** full suite green before `/gsd-verify-work`. The live-stack `SC2RecoveryPathsE2ETests`
  (`[Trait("Phase", ...)]`) is the authoritative end-to-end proof and must be updated for the renamed types.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Orchestrator/OrchestratorResultPipelineForwardFacts.cs` — ORCV-02/03/04
- [ ] `tests/BaseApi.Tests/Orchestrator/OrchestratorResultPipelineRecoveryFacts.cs` — ORCV-01/05
- [ ] `tests/BaseApi.Tests/Keeper/OrchestratorInjectConsumerFacts.cs` — ORCV-06
- [ ] `tests/BaseApi.Tests/Keeper/OrchestratorReinjectConsumerFacts.cs` — ORCV-06 (the D-07 factory)
- [ ] Extend `KeeperDeleteInvariantFacts` (or a parallel `OrchestratorDeleteInvariantFacts`) — ORCV-07 (D-09)
- [ ] `RecoveryTestKit.Db()` 5-arg `StringSetAsync` stub added (D-10) — unblocks the new consumer facts
- [ ] Rename-update existing facts: `KeeperContractTests`, `InjectConsumerFacts`, `ReinjectConsumerFacts`,
      `RecoveryPartitionFacts`, `RecoveryDeadLetterFacts`, `KeeperDeleteInvariantFacts`, the `Processor/Pipeline*Facts`,
      `DispatchTestKit`, `SC2RecoveryPathsE2ETests`, `ModelBContractsRetiredFacts`, analyzer refs (see Q1 list)

*(Framework already installed — no install step needed.)*

## Research Question Answers

### Q1 — Rename blast radius (D-06): the ACTUAL `.cs` reference sites
`[VERIFIED: ripgrep this session]` — 129 total occurrences of the four symbols across **27 files**.
Per-symbol counts (whole-word matches):

**`KeeperInject` — 30 refs / 12 files:**
| File | Refs | Prod/Test |
|------|------|-----------|
| `src/Messaging.Contracts/KeeperInject.cs` | 1 | Prod (RENAME file→`ProcessorInject.cs`) |
| `src/Keeper/Recovery/InjectConsumer.cs` | 2 | Prod |
| `src/Keeper/Recovery/RecoveryEndpointBinder.cs` | 1 | Prod |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` | 2 | Prod (`BuildInject` return type) |
| `tests/.../Contracts/KeeperContractTests.cs` | 5 | Test |
| `tests/.../Keeper/InjectConsumerFacts.cs` | 2 | Test |
| `tests/.../Keeper/KeeperDeleteInvariantFacts.cs` | 2 | Test |
| `tests/.../Resilience/ModelBContractsRetiredFacts.cs` | 4 | Test |
| `tests/.../Processor/PipelineForwardFacts.cs` | 3 | Test |
| `tests/.../Processor/DispatchTestKit.cs` | 2 | Test |
| `tests/.../Processor/PipelinePostFacts.cs` | 4 | Test |
| `tests/.../Orchestrator/SC2RecoveryPathsE2ETests.cs` | 2 | Test |

**`KeeperReinject` — 47 refs / 17 files:** `KeeperReinject.cs`(1,Prod RENAME), `ReinjectConsumer.cs`(3,Prod),
`RecoveryEndpointBinder.cs`(1,Prod), `ProcessorPipeline.cs`(4,Prod), `KeyAbsentException.cs`(1,Prod — doc/xml ref),
`ReinjectConsumerFacts.cs`(4), `RecoveryPartitionFacts.cs`(1), `RecoveryDeadLetterFacts.cs`(5),
`KeeperDeleteInvariantFacts.cs`(2), `ModelBContractsRetiredFacts.cs`(3), `KeeperContractTests.cs`(4),
`PipelineRecoveryFacts.cs`(4), `PipelinePreFacts.cs`(4), `PipelineForwardFacts.cs`(2), `PipelineEndDeleteFacts.cs`(1),
`DispatchTestKit.cs`(3), `SC2RecoveryPathsE2ETests.cs`(4).

**`InjectConsumer` — 7 refs / 6 files:** `RecoveryEndpointBinder.cs`(1,Prod), `InjectConsumer.cs`(1,Prod RENAME file),
`Keeper/Program.cs`(1,Prod), `KeeperDeleteInvariantFacts.cs`(2), `InjectConsumerFacts.cs`(1),
`SC2RecoveryPathsE2ETests.cs`(1). *(Note: the `InjectConsumer` matches also appear inside `ReinjectConsumer`
whole-word boundaries — handle the rename carefully; `ProcessorReinjectConsumer` must not become `ProcessorReProcessorInjectConsumer`.)*

**`ReinjectConsumer` — 22 refs / 7 files:** `ReinjectConsumer.cs`(2,Prod RENAME file), `RecoveryEndpointBinder.cs`(1,Prod),
`Keeper/Program.cs`(2,Prod), `ReinjectConsumerFacts.cs`(4), `RecoveryDeadLetterFacts.cs`(5),
`KeeperDeleteInvariantFacts.cs`(4), `SC2RecoveryPathsE2ETests.cs`(4).
**Watch-out:** `ReinjectConsumerDefinition` (the surviving partition-helper static class) contains the substring
`ReinjectConsumer` but is NOT renamed by D-06 — it stays `ReinjectConsumerDefinition` (it holds `PartitionKey`/
`PartitionGuid`, origin-agnostic). A naive find-replace would wrongly rename it. The planner must scope the
rename to the **type names** `InjectConsumer`/`ReinjectConsumer` (and the contracts), NOT the `...Definition`.

**Files NOT to touch by rename:** `KeeperDelete.cs`, `DeleteConsumer.cs`, `KeeperQueues.cs`, `IKeeperRecoverable.cs`,
`ReinjectConsumerDefinition.cs` (class name survives), `L2ProjectionKeys.cs`. Two analyzer/observability refs
(`AnalyzerE2ETests`(3), `PromCounterSnapshot`(1), `PassFailEngineFacts`(1)) matched on the broader pattern but
are string/metric-name refs — confirm during the rename whether they reference the *type* or a *metric label*;
the keeper metrics (`keeper_reinject_dropped`) are NOT renamed by D-06.

### Q2 — Result-consume integration (D-01): WHERE the pipeline plugs in `[VERIFIED]`
Traced end-to-end:
1. Processor/keeper `Send` an `IStepResult` to `queue:orchestrator-result` (`OrchestratorQueues.Result =
   "orchestrator-result"`).
2. `Orchestrator/Program.cs:60-63` binds **four competing consumers** on that endpoint:
   `StepCompletedConsumer`, `StepFailedConsumer`, `StepCancelledConsumer`, `StepProcessingConsumer` — each a
   tiny subclass of `TypedResultConsumer<TMessage>` that only overrides `protected abstract StepOutcome Outcome`.
3. `TypedResultConsumer<TMessage>.Consume` (lines 56-89) is **L1-only today**: metrics → `store.TryGet` →
   `advancement.SelectNext(Outcome, completed, wf.Steps)` → `dispatcher.DispatchAsync(...)` per match. **No Redis.**
4. The orchestrator-result endpoint registers **no bus retry** (`StepCompletedConsumerDefinition`,
   Phase-53 D-01) — send-exhaust throws → broker redelivery. Symmetric with the processor dispatch endpoint.

**Where `OrchestratorResultPipeline` plugs in:** inside `TypedResultConsumer<TMessage>.Consume` — the single
shared base for all four typed consumers. The pipeline's gate runs FIRST (using `context.MessageId`), then
FORWARD iterates `advancement.SelectNext(Outcome, completed, wf.Steps)` (the SAME call that exists today, now
producing the per-slot dispatch tuples instead of a direct dispatch). `StepAdvancement` and the `Outcome` knob
stay unchanged. This is a **minimal, single-seam** integration: the four typed consumers don't change; only the
shared base body gains the gate → FORWARD/RECOVERY → cleanup flow. (Alternative — a new dedicated consumer — was
considered but rejected: it would split routing across two consumer families and lose the per-type `Outcome` knob
that FORWARD still needs.) **Recommendation, flagged:** route the pipeline through the existing
`TypedResultConsumer<T>` base, injecting `OrchestratorResultPipeline` (which itself takes
`IConnectionMultiplexer`, `IStepDispatcher`/`ISendEndpointProvider`, `StepAdvancement` is passed per-call from the
consumer's existing dependency). `[ASSUMED — A3]` on the exact DI seam (constructor-inject the pipeline vs.
build per-consume); confirm in planning.

The **origin entryId** for the copy/cleanup is the inbound result's `EntryId` (`m.EntryId` — a real L2 data key
on a `StepCompleted`, `Guid.Empty` on Failed/Cancelled/Processing). FORWARD copies `L2[m.EntryId] → L2[newEntryId]`.

### Q3 — AtomicForwardWrite reuse (D-03) `[VERIFIED]`
Processor constant (lines 96-100) and the C# ARGV marshaling (lines 298-309) are quoted in Pattern 2 above.
TTLs: `SlotTtl()` (C#, `Random.Shared.Next(ttl, 2*ttl+1)`, floored at 1s — lines 110-118) → ARGV[4] index TTL;
`executionDataTtl` (`Math.Max(1, ExecutionDataTtlSeconds)` — line 126) → ARGV[5] data TTL. Both in C# (TEST-06).

**What changes for the orchestrator's COPY-existing-key semantics:** the processor's data leg is
`SET KEYS[2] ARGV[3] PX ARGV[5]` where `ARGV[3]` is the in-hand `item.Data`. The orchestrator has NO data in
hand — it must **copy an existing key**. Two acceptable single-script forms (D-03 discretion):
- **Option A (COPY):** `redis.call('COPY', ARGV[6], KEYS[2], 'REPLACE'); redis.call('PEXPIRE', KEYS[2], ARGV[5])`
  where `ARGV[6]` is the origin key name (`ExecutionData(originEntryId)`). **Must** follow COPY with PEXPIRE
  (COPY drops the TTL — Pitfall 3). Pass the origin key as a KEY (cleaner: `KEYS[3]`) so it is script-analyzed.
- **Option B (GET+SET):** `local v = redis.call('GET', KEYS[3]); if v then redis.call('SET', KEYS[2], v, 'PX', ARGV[5]) end`
  — explicit, no COPY edge cases, mirrors the processor's `SET ... PX` leg most closely.

**Recommendation (flagged, A4):** **Option B (GET+SET)** — it is byte-closest to the processor's existing
`SET KEYS[2] ARGV[3] PX ARGV[5]` leg (the only change is sourcing the value via `GET KEYS[3]` instead of `ARGV[3]`),
keeps the "consistent with processor" through-line, and sidesteps the COPY-TTL footgun. Pass the origin key as
`KEYS[3]` (3 KEYS: index, dest, origin) so Redis Cluster slot-analysis sees all keys. Keep `KEYS={MessageIndex,
ExecutionData(newEntryId), ExecutionData(originEntryId)}`.

### Q4 — Slot encoding (D-02) `[VERIFIED]`
`MessageIndex(messageId)` = `skp:msg:{messageId:D}`, a HASH of `int slot → value` (`L2ProjectionKeys.cs:61`,
doc lines 26, 57-61). The processor writes a bare entryId string per slot (`entryId.ToString("D")`, line 305) and
reads via `HashGetAllAsync` + `Guid.TryParse` (lines 161, 173). The orchestrator keeps the SAME HASH structure but
the value is a JSON object. **Where JSON ser/de lives:** orchestrator-side, inside `OrchestratorResultPipeline`
(NOT in `Messaging.Contracts` — the key *format* is shared, the *value schema* is orchestrator-private). The four
tuple fields map directly: `nextStepId` + `nextProcessorId` (= `step.ProcessorId`) + `payload` (= `step.Payload`)
come from the `StepAdvancement.SelectNext` yield `(Guid stepId, StepProjection step)`; `newEntryId` is minted with
`NewId.NextGuid()`. Use System.Text.Json (default STJ, no custom converter — consistent with the codebase). If the
tuple is modeled as a positional record, use `[property: JsonPropertyName]` not bare `[JsonPropertyName]`
(Pitfall — `StepProjection.cs:12-14` documents this exact STJ footgun).

### Q5 — Consumer binding symmetry (D-08) `[VERIFIED]`
`RecoveryEndpointBinder.cs:53-67`: ONE shared `Partitioner(recovery.Value.PartitionCount,
new Murmur3UnsafeHashGenerator())`; three `UsePartitioner<T>(partition, p =>
ReinjectConsumerDefinition.PartitionGuid(p.Message))` + three `ConfigureConsumer<T>(ctx)`. The two new consumers
add `UsePartitioner<OrchestratorInject>` / `UsePartitioner<OrchestratorReinject>` (same partitioner, same selector)
+ `ConfigureConsumer<OrchestratorInjectConsumer>` / `<OrchestratorReinjectConsumer>`. `ReinjectConsumerDefinition.
PartitionGuid` (lines 38-42) is `SHA256` over the 4-tuple `corr:wf:proc:exec` → first 128 bits → Guid — works for
any `IKeeperRecoverable`, so the `Orchestrator*` contracts (which implement `IKeeperRecoverable`) partition
correctly with NO change to the helper. Keeper `Program.cs` registers the three existing consumers with
`x.AddConsumer<T>().ExcludeFromConfigureEndpoints()` — add the two new ones the same way (Pitfall 6). No new queue:
all five share `KeeperQueues.Recovery = "keeper-recovery"`.

### Q6 — Negative-guard fact pattern (D-09) `[VERIFIED]`
`KeeperDeleteInvariantFacts.cs` (read in full) — three facts: `DeleteConsumer_deletes_both_keys` (positive),
`InjectConsumer_never_deletes`, `ReinjectConsumer_never_deletes`. Each negative fact:
1. constructs the REAL consumer over `RecoveryTestKit.Db()` + `CapturingSendProvider`;
2. runs `Consume`;
3. **positive co-assertion** — `Assert.Single(send.Sent)` + `Assert.IsType<StepCompleted/EntryStepDispatch>(msg)`
   (proves the body ran, so a silent no-op can't pass);
4. **negative guard** — `await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())`
   AND `...KeyDeleteAsync(Arg.Any<RedisKey[]>(), ...)` (BOTH overloads, lines 91-92, 130-131).
For `Orchestrator*`: mirror exactly — `OrchestratorInjectConsumer` co-asserts its `EntryStepDispatch` send (it
completes the copy + dispatches downstream); `OrchestratorReinjectConsumer` co-asserts its re-injected
`IStepResult` send to `orchestrator-result`. **Recommendation (flagged, A5):** **extend** `KeeperDeleteInvariantFacts`
with two new facts (one per new consumer) rather than a parallel file — keeps the cross-consumer invariant in one
place, consistent with how the file already covers all three keeper states.

### Q7 — Test infrastructure `[VERIFIED]`
- **Projects touched:** `tests/BaseApi.Tests/BaseApi.Tests.csproj` (the single xunit.v3/MTP project). Facts live
  under `tests/BaseApi.Tests/{Keeper,Processor,Orchestrator,Contracts,Resilience}/`.
- **`RecoveryTestKit` shape** (`tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs`): `OpenGate()`, `Retry(limit)`,
  `Metrics()`, `Mux(db)`, `Db(values?)`, and `CapturingSendProvider` (records `(Uri, object)` for
  `EntryStepDispatch` + `StepCompleted` sends). **D-10 gap:** `Db()` stubs the legacy 6-arg `StringSetAsync`
  (lines 67-70) but NOT the 5-arg `Expiration/ValueCondition` overload the production 2-arg call binds to in
  SE.Redis 2.13.1. The new `Orchestrator*` consumer facts (and any reconstructed-result send capture) will need:
  (a) the 5-arg `StringSetAsync` stub (D-10), and (b) `CapturingSendProvider` extended to capture
  `StepFailed`/`StepCancelled`/`StepProcessing` and `OrchestratorInject`/`OrchestratorReinject` sends (it currently
  only captures `EntryStepDispatch` + `StepCompleted`). `[Recommendation, A6]` extend `CapturingSendProvider` to
  capture the boxed `object` Send overload generically so any new message type is recorded without per-type stubs.
- **Invocation:** `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -- --filter-method "*Pattern*"` for
  targeted runs (`--filter` is silently ignored — full 638). `[VERIFIED: project memory + .csproj MTP settings]`

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `ConsumeContext.MessageId` is non-null on the inbound `IStepResult` envelope (MassTransit always stamps a MessageId on `Send`) | Q2 / Pitfall 1 | If null, the gate key is undefined — must source messageId another way; planner should add a null-guard / verify with a harness test |
| A2 | The keeper-recovery rename is deployed against a drained queue, so cross-deploy in-flight `KeeperInject`/`KeeperReinject` messages are not a migration concern | Runtime State Inventory | If the queue is hot at deploy, old-type messages fail to bind to the renamed consumer (lost recovery work) — confirm deploy runs on clean-state stack |
| A3 | The pipeline is invoked from inside `TypedResultConsumer<T>.Consume` (the shared base), constructor- or factory-injected | Q2 | If a different seam is chosen (new consumer), routing splits and the `Outcome` knob reuse breaks — confirm in planning |
| A4 | GET+SET (Option B) is the recommended copy form inside the atomic script | Q3 | COPY+PEXPIRE (Option A) is equally valid (D-03 discretion); choice is non-blocking |
| A5 | Extending `KeeperDeleteInvariantFacts` (vs a parallel file) is the chosen D-09 form | Q6 | Pure test-org preference; either satisfies D-09 |
| A6 | `CapturingSendProvider` should be generalized to capture any boxed `object` Send | Q7 | Without it, new facts need per-type stubs; non-blocking but affects test ergonomics |

**These are recommendations, not new requirements.** Where CONTEXT.md grants discretion (A4/A5/A6), the
recommendation is offered with rationale; the user/planner may choose the alternative freely.

## Open Questions (RESOLVED)

> Both questions were resolved during planning (the Phase 71 PLAN set). Resolutions noted inline.

1. **messageId on the result envelope (A1).** — **RESOLVED in `71-03-PLAN.md` Task 2.**
   - What we know: the processor gate keys on the inbound dispatch's broker MessageId; the orchestrator must key
     on the inbound *result's* broker MessageId. MassTransit stamps a MessageId on every `Send`.
   - What's unclear: whether the orchestrator-result consumers can reliably read `context.MessageId` (non-null) —
     not asserted in any existing test.
   - Recommendation: add an early-wave harness/unit check that `context.MessageId` is present on a result; thread
     `context.MessageId!.Value` into `RunAsync`. Treat a null as a hard infra error (throw → redelivery), not a drop.
   - **Resolution:** Plan 03 Task 2 threads `context.MessageId` into `RunAsync` behind an explicit
     `if (context.MessageId is null) throw new InvalidOperationException(...)` null-guard (null → hard infra throw →
     broker redelivery, never a silent drop), exactly as recommended.

2. **Analyzer/observability refs (Q1 tail).** — **RESOLVED in `71-01-PLAN.md` Task 1/2 (TRAP 2).**
   - What we know: `AnalyzerE2ETests`, `PromCounterSnapshot`, `PassFailEngineFacts` matched the broad rename pattern.
   - What's unclear: whether they reference the *type* `KeeperInject`/`KeeperReinject` or a *metric label* string
     (keeper metrics like `keeper_reinject_dropped` are NOT renamed by D-06).
   - Recommendation: during the rename wave, inspect each of these three to confirm they are type refs before
     touching; leave metric-label strings alone.
   - **Resolution:** Plan 01 Task 1/2 bake in TRAP 2 as an inspect-before-touch instruction — confirm each ref is a
     *type* ref before renaming; metric-label strings (`keeper_reinject_dropped_total`, etc.) are left unchanged.

## Environment Availability

No new external dependencies. The phase is code + contracts + tests over already-present infrastructure
(Redis, RabbitMQ via MassTransit, the xunit.v3/MTP test host). Live-stack E2E (`SC2RecoveryPathsE2ETests`)
requires the running compose stack — the standard close-gate/clean-state stack already used by the project
(see project memory: ~50min/run close-gate protocol). No tool is missing.

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK 8 | build/test | ✓ (assumed; repo targets net8.0) | 8.x | — |
| Redis | L2 ops (unit via NSubstitute; E2E via stack) | ✓ (stack) | — | unit tests stub `IDatabase` |
| RabbitMQ (MassTransit) | bus (E2E via stack) | ✓ (stack) | — | unit tests stub `ISendEndpointProvider` |

## Security Domain

> security_enforcement: not found as explicitly `false`; treated as enabled but **narrow** for this phase.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V5 Input Validation | yes (narrow) | The slot JSON tuple is internal (orchestrator-written, orchestrator-read) — not user input. RECOVERY must tolerate an unparsable/retired slot gracefully (skip, mirror `Guid.TryParse` guard at `ProcessorPipeline.cs:173`) rather than throw |
| V6 Cryptography | no (hashing only) | `PartitionGuid` uses `SHA256` for partition-key derivation (not security) — reused as-is, never hand-rolled |
| V2/V3/V4 Auth/Session/Access | no | No auth/session/access-control surface in this phase |

### Known Threat Patterns for this stack
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Lua injection via concatenated user data into the script | Tampering | The script is a compile-time `const` with parameterized KEYS/ARGV — NO user data concatenated into the script text (processor T-69-04 precedent; mirror it) |
| Unbounded data-key TTL after copy | Availability (resource leak) | The dest key MUST carry the data TTL inside the atomic script (D-03 / Pitfall 3) |
| Malformed slot JSON crashing RECOVERY | DoS (own state) | Tolerant parse + skip (mirror the retired/unparsable-slot skip at `ProcessorPipeline.cs:173`) |

## Sources

### Primary (HIGH confidence — read in full this session)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — the structural template (gate, AtomicForwardWrite,
  RunForward/RunRecovery, DeleteTerminalAsync, SendKeeper/SendResult, builders)
- `src/Orchestrator/Consumers/TypedResultConsumer.cs` + `StepCompletedConsumer.cs` + `StepCompletedConsumerDefinition.cs`
  + `src/Orchestrator/Program.cs` — the result-consume wiring (Q2)
- `src/Orchestrator/Dispatch/{StepDispatcher,StepAdvancement,IStepDispatcher}.cs` — next-step + dispatch
- `src/Keeper/Recovery/{InjectConsumer,ReinjectConsumer,DeleteConsumer,RecoveryConsumerBase,RecoveryEndpointBinder,ReinjectConsumerDefinition}.cs`
  + `src/Keeper/Program.cs` — the consumers/bind point (Q5)
- `src/Messaging.Contracts/{KeeperInject,KeeperReinject,KeeperDelete,IKeeperRecoverable,IStepResult,StepCompleted,StepFailed,StepCancelled,StepProcessing,StepOutcome,EntryStepDispatch,OrchestratorQueues,KeeperQueues}.cs`
  + `Projections/{L2ProjectionKeys,StepProjection}.cs` — contracts + keys
- `tests/BaseApi.Tests/Keeper/{RecoveryTestKit,KeeperDeleteInvariantFacts,InjectConsumerFacts}.cs` — test infra (Q6/Q7)
- `docs/design/processor-keeper-recovery-spec.md` §3-§10 — the normative spec
- `.planning/phases/70-processor-inject-cleanup/70-REVIEW.md` — WR-01 (D-10)
- `.planning/REQUIREMENTS.md` lines 26-32 — ORCV-01..07
- `Directory.Packages.props:131` — StackExchange.Redis 2.13.1
- ripgrep counts (the rename blast radius, Q1)

### Secondary (MEDIUM confidence)
- Project memory: MTP `--filter` silently-ignored; close-gate net-zero protocol; planning-docs-bloat

### Tertiary (LOW confidence)
- None.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — versions verified in `Directory.Packages.props`; no new packages
- Architecture / integration: HIGH — the result-consume wiring traced end-to-end in source (Q2); pipeline template read in full
- Rename blast radius: HIGH — exact ripgrep counts per symbol/file (Q1); the `...Definition` and metric-label watch-outs surfaced
- Atomic write / copy semantics: HIGH (mechanism) / recommendation (A4 COPY-vs-SET, D-03 discretion)
- messageId provenance: MEDIUM — A1 needs a one-line confirmation (`context.MessageId` non-null) in planning
- Pitfalls / tests: HIGH — D-10/WR-01 and the invariant-fact pattern read directly

**Research date:** 2026-06-16
**Valid until:** 2026-07-16 (stable — internal codebase, no fast-moving external deps)
