# Phase 70: Processor INJECT Cleanup - Pattern Map

**Mapped:** 2026-06-16
**Files analyzed:** 8 (1 net-new test fact + 7 edits)
**Analogs found:** 8 / 8 (every edit is its own current state; the net-new file has 2 strong sibling analogs + the shared harness)

This is a mechanical refactor. Seven of the eight files are EDITS — for those the "analog" is the file's own current state (the pattern to preserve while surgically removing the delete op / `DeleteEntryId` field). The ONE net-new file (D-05 invariant fact) copies its structure from the existing `*ConsumerFacts.cs` siblings and the `RecoveryTestKit` harness.

## File Classification

| File | New/Edit | Role | Data Flow | Closest Analog | Match Quality |
|------|----------|------|-----------|----------------|---------------|
| `tests/BaseApi.Tests/Keeper/KeeperDeleteInvariantFacts.cs` | **NEW** | test (invariant fact) | event-driven (consume→assert) | `InjectConsumerFacts.cs` + `DeleteConsumerFacts.cs` + `ReinjectConsumerFacts.cs` (shared `RecoveryTestKit`) | exact (sibling fact pattern) |
| `src/Keeper/Recovery/InjectConsumer.cs` | edit | consumer (recovery state) | event-driven | self (current state) + `ReinjectConsumer.cs` (already 2-effect non-destructive) | self |
| `src/Messaging.Contracts/KeeperInject.cs` | edit | model (bus envelope record) | request-response (wire) | self + sibling records `KeeperReinject`/`KeeperDelete` | self |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (`BuildInject` 430-438) | edit | service (pipeline builder) | transform | self + sibling builders `BuildReinject` (422-423) / `BuildDelete` (425-426) | self |
| `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` | edit | test | event-driven | self (current state) | self |
| `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs` (67-81) | edit | test (reflection contract) | request-response | self + sibling facts `KeeperReinject_carries…`/`KeeperDelete_carries…` (50-64) | self |
| `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs` (19, 149) | edit | test | transform | self | self |
| `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` (40-43, 178-215) | edit | test (E2E, RealStack) | event-driven | self | self |

---

## Pattern Assignments

### `tests/BaseApi.Tests/Keeper/KeeperDeleteInvariantFacts.cs` (NEW — test, event-driven)  ← PRIMARY MAPPING

**Analogs:** `DeleteConsumerFacts.cs` (positive `Received` delete), `InjectConsumerFacts.cs` (consumer construction + SE.Redis overload note), `ReinjectConsumerFacts.cs` (the per-consumer `Ctx()` helper + multi-dependency ctor), all over `RecoveryTestKit`.

**Recommended shape (Research Open Q1):** class `KeeperDeleteInvariantFacts`, namespace `BaseApi.Tests.Keeper`, `[Trait("Phase","70")]`, three `[Fact]`s:
`DeleteConsumer_deletes_both_keys`, `InjectConsumer_never_deletes`, `ReinjectConsumer_never_deletes`.

**Imports pattern** — copy from `DeleteConsumerFacts.cs:1-7` (note the `global::Keeper.Recovery` alias; `ReinjectConsumerFacts` adds `global::Keeper.Observability` + `Microsoft.Extensions.Logging.Abstractions` for the metrics/logger ctor args):
```csharp
using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;
// for the ReinjectConsumer arm only:
using global::Keeper.Observability;                       // KeeperMetrics
using Microsoft.Extensions.Logging.Abstractions;         // NullLogger<ReinjectConsumer>
```

**Per-consumer `Ctx()` + `ConsumeContext` fake** — copy idiom from `DeleteConsumerFacts.cs:17-23` (generalize per message type, or write three small Ctx helpers):
```csharp
var ctx = Substitute.For<ConsumeContext<KeeperInject>>();
ctx.Message.Returns(m);
ctx.CancellationToken.Returns(ct);
await consumer.Consume(ctx);
```

**Consumer construction (3 distinct ctor shapes — DO NOT copy one for all three):**
- `DeleteConsumer` / `InjectConsumer` (3 args) — `DeleteConsumerFacts.cs:42-44`, `InjectConsumerFacts.cs:25-27`:
```csharp
new DeleteConsumer(RecoveryTestKit.Mux(db), send, RecoveryTestKit.Retry());
new InjectConsumer(RecoveryTestKit.Mux(db), send, RecoveryTestKit.Retry());
```
- `ReinjectConsumer` (5 args — adds metrics + logger) — `ReinjectConsumerFacts.cs:48-51`. Its `HandleAsync` reads `StringLengthAsync` and **drops on STRLEN==0**, so to make it run its send path (and prove it didn't no-op) **stub present**, copy `ReinjectConsumerFacts.cs:44-45`:
```csharp
db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId), Arg.Any<CommandFlags>()).Returns(10L); // present
var consumer = new ReinjectConsumer(
    RecoveryTestKit.Mux(db), send, RecoveryTestKit.Retry(),
    RecoveryTestKit.Metrics(), NullLogger<ReinjectConsumer>.Instance);
```

**POSITIVE assertion — DELETE DOES delete the multi-key (`RedisKey[]`) overload** — copy from `DeleteConsumerFacts.cs:51-55` (this is the exact NSubstitute `db.Received(...).KeyDeleteAsync(...)` idiom requested):
```csharp
await db.Received(1).KeyDeleteAsync(
    Arg.Is<RedisKey[]>(ks => ks.Length == 2
        && ks.Contains((RedisKey)L2ProjectionKeys.ExecutionData(m.EntryId))
        && ks.Contains((RedisKey)L2ProjectionKeys.MessageIndex(m.MessageId))),
    Arg.Any<CommandFlags>());
```
(`DeleteConsumer.cs:20-24` uses the `RedisKey[]` overload — both data key + message index.)

**NEGATIVE assertion — INJECT / REINJECT do NOT delete (BOTH overloads, Pitfall 2)** — this is the new `db.DidNotReceive()` idiom (modeled in RESEARCH §"behavioral negative-guard", grounded in `RecoveryTestKit.Db():71-72` which stubs both overloads):
```csharp
await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(),   Arg.Any<CommandFlags>());  // single-key overload
await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());  // multi-key overload
```

**POSITIVE side-effect co-assertion (false-positive defense, Validation Arch layer 3)** — prove the consumer body actually ran, so `DidNotReceive` can't pass on a silent no-op:
- InjectConsumer arm: assert one captured send — `var (_, msg) = Assert.Single(send.Sent); Assert.IsType<StepCompleted>(msg);` (pattern: `InjectConsumerFacts.cs:44-46`).
- ReinjectConsumer arm: assert one captured `EntryStepDispatch` — `Assert.Single(send.Sent)` (pattern: `ReinjectConsumerFacts.cs:55-57`).

**SE.Redis 2.13.1 overload-binding note (verbatim from `InjectConsumerFacts.cs:54-56`)** — relevant ONLY if this fact also asserts the `StringSetAsync` write (it does not need to; deletes + captured-send suffice). If you do add a write-Received, use the 5-arg shape from `InjectConsumerFacts.cs:70-72`:
```
// SE.Redis 2.13.1 binds the consumer's 2-arg StringSetAsync to the Expiration/ValueCondition
// overload, so the InOrder matcher targets that overload.   (InjectConsumerFacts.cs:54-56)
await db.Received(1).StringSetAsync(
    (RedisKey)L2ProjectionKeys.ExecutionData(m.EntryId), (RedisValue)m.Data,
    Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
```

**CARVE-OUT (Pitfall 3):** Construct ONLY `DeleteConsumer`, `InjectConsumer`, `ReinjectConsumer`. Never reference/instantiate `L2ProbeRecovery` and never enumerate `Keeper.dll` types — behavioral scoping is what keeps the probe's scratch `:35` delete out of the invariant.

---

### `src/Keeper/Recovery/InjectConsumer.cs` (EDIT — consumer, event-driven)  [D-01]

**Analog:** self (preserve the Guarded structure of ops 1-2) + `ReinjectConsumer.cs` as the already-non-destructive reference.

**Preserve verbatim** (`InjectConsumer.cs:24-37`): op 1 `Guard(() => Db.StringSetAsync(L2ProjectionKeys.ExecutionData(m.EntryId), m.Data), ct)` and op 2 the `StepCompleted` build + `Guard(GetSendEndpoint)` + `Guard(ep.Send)`. RESEARCH §"INJECT consumer body (after D-01)" shows the exact surviving body.

**DELETE** (`InjectConsumer.cs:39-40` — comment + op together):
```csharp
// 3) delete L2[deleteEntryId] (source cleanup tail — AFTER the confirmed send)
await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.DeleteEntryId)), ct);
```

**XML doc rewrite** (`:10-16`): drop the "three ops in STRICT order / Pitfall 5 / source delete is the tail" rationale and the "(3) delete `L2[m.DeleteEntryId]`" clause. New doc describes the two-effect non-destructive body (write data + send StepCompleted). Keep the "Every op goes through the RetryLoop Guard; gating at the endpoint (D-04)" sentence.

---

### `src/Messaging.Contracts/KeeperInject.cs` (EDIT — model, wire envelope)  [D-02]

**Analog:** self + sibling records. Record stays a default-STJ envelope (comment `:3` "NO [JsonPropertyName]" — KEEP; it is the wire-tolerance rationale, no migration step).

**DELETE** (`KeeperInject.cs:14`):
```csharp
public Guid DeleteEntryId { get; init; }   // D-08: source entryId deleted after the orchestrator send (A18 literal `deleteEntryId`)
```
Record then carries the 5-id base (ctor `WorkflowId, StepId, ProcessorId` + `CorrelationId`/`ExecutionId`) + `EntryId` (Guid, `:12`) + `Data` (string, `:13`).

**XML doc rewrite** (`:4-7`): drop the `<see cref="DeleteEntryId"/> (source entryId deleted after the send)` clause from the id-set sentence → "INJECT id-set: `EntryId` (allocation to write) + `Data` (raw-JSON output, in-hand)".

---

### `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — `BuildInject` (EDIT — service, transform)  [D-03]

**Analog:** self + sibling builders `BuildReinject` (`:422-423`) and `BuildDelete` (`:425-426`) — same `new(d.WorkflowId, d.StepId, d.ProcessorId) { ... }` initializer pattern; `BuildInject` simply drops one initializer line.

**DELETE** (`:437`):
```csharp
DeleteEntryId = d.EntryId,         // source entryId (A18 literal deleteEntryId)
```

**Comment rewrite** (`:428-429`, INFRA-02/Pitfall-1): drop the `, DeleteEntryId = the source entryId` clause → keep "BuildInject populates EntryId = the allocation just written, Data = the raw-JSON output in-hand."

---

### `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` (EDIT — test)  [D-07]

**Analog:** self. Keep the construction (`:22-39`), the captured-send assertions (`:44-49`), and the 5-arg `StringSetAsync` Received check (`:70-72`).

**Changes:**
- Remove `DeleteEntryId = Guid.NewGuid(),` from the test message (`:35`).
- Drop the `Received.InOrder` chain (`:57-63`) — collapses to a single db call (Pitfall 5). Keep the write-then-send order implicitly via (a) `db.Received(1).StringSetAsync(...)` 5-arg shape (`:70-72`) and (b) `Assert.Single(send.Sent)` is the `StepCompleted` (`:44-49`, `:68`).
- Drop the positive delete assertion (`:73-74` `db.Received(1).KeyDeleteAsync((RedisKey)…ExecutionData(m.DeleteEntryId), …)`).
- ADD a belt: `await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());` and the `RedisKey[]` overload too.
- Rewrite class XML doc (`:11-15`) + method name (`:20` `Inject_writes_sends_completed_deletes_source_in_order`) to "write-then-send, deletes nothing."

---

### `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs` (EDIT — reflection contract test)  [D-06]

**Analog:** self + sibling reflection facts `KeeperReinject_carries_EntryId_and_Payload` (`:50-58`) and `KeeperDelete_carries_EntryId` (`:61-64`) — the established positive-`GetProperty`/`Assert.NotNull` shape. The new negative guard mirrors the existing `Assert.Null(typeof(IKeeperRecoverable).GetProperty("StepId"))` at `:37`.

**Changes to the INJECT fact (`:66-81`):**
- Rename `KeeperInject_carries_the_A18_id_set_EntryId_Data_DeleteEntryId` → e.g. `KeeperInject_carries_the_reduced_id_set_EntryId_Data`.
- Keep the `EntryId` (Guid) + `Data` (string) positive asserts (`:70-76`).
- REPLACE the `deleteEntryId` positive block (`:78-80`) with the negative guard:
```csharp
Assert.Null(typeof(KeeperInject).GetProperty("DeleteEntryId"));   // re-adding the field breaks the build (KINJ-02)
```
- Class XML doc (`:12-13`): change "INJECT carries the A18 id-set EntryId + Data + DeleteEntryId (D-08)" → "INJECT carries EntryId + Data". Leave REINJECT/DELETE sentences and facts unchanged.

---

### `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs` (EDIT — test, transform)  [D-08]

**Analog:** self. Keep the `KeeperInject` capture (`:147`) and the `inj.Data`/`inj.EntryId` asserts (`:148`, `:150`).

**Changes:**
- Delete `Assert.Equal(d.EntryId, inj.DeleteEntryId);` (`:149`).
- Doc comment (`:19`, NODROP-01 `<list>` item): remove `/DeleteEntryId` → "ONE `KeeperInject` carrying Data/EntryId" (Pitfall 6).

---

### `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` (EDIT — E2E test, RealStack)  [D-09]

**Analog:** self. `[Trait("Category","RealStack")]` → compile-only in the hermetic gate (must build, not run).

**Remove the WHOLE INJECT delete-half block (Pitfall 4 / A1 — NOT just the 3 cited lines):**
- `var deleteEntryId = Guid.NewGuid();` (`:188`)
- `var deleteKey = L2ProjectionKeys.ExecutionData(deleteEntryId);` (`:193`)
- `await db.StringSetAsync(deleteKey, "source-to-delete");` (`:194`)
- `factory.L2KeysToCleanup.Add(deleteKey);` (`:196`)
- `DeleteEntryId = deleteEntryId,` (`:204`)
- the `sourceDeleted` assertion `PollForKeyAbsentAsync(db, deleteKey, ct)` + `Assert.True(sourceDeleted, …)` (`:212-214`)

**KEEP:** the write assertion `PollForKeyValueAsync(db, entryKey, "inject-payload", ct)` (`:209-211`) and `factory.L2KeysToCleanup.Add(entryKey)` (`:195`).

**Doc comments:** `:40-43` and `:178-179` — change "write …, send …, delete `m.DeleteEntryId`. Asserted on … the source key being deleted." → "write the data key + send `StepCompleted`, deletes nothing. Asserted on the data key being written."

---

## Shared Patterns

### Recovery test harness (the single shared fixture)
**Source:** `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs`
**Apply to:** the new invariant fact + all `*ConsumerFacts` edits. Do NOT hand-roll fakes.
- `RecoveryTestKit.Db()` (`:58`) — substitute `IDatabase`; **stubs BOTH delete overloads** (`:71` single `RedisKey`, `:72` `RedisKey[]`→2L) so `Received`/`DidNotReceive` work for both — this is what makes Pitfall-2's two-overload negative guard assertable.
- `RecoveryTestKit.Mux(db)` (`:49`), `RecoveryTestKit.Retry()` (`:35`), `RecoveryTestKit.Metrics()` (`:41`, ReinjectConsumer arm only).
- `RecoveryTestKit.CapturingSendProvider` (`:79`) — records `(Uri, Message)`; captures both `EntryStepDispatch` (`:86`) and `StepCompleted` (`:88`).

### Guarded recovery-consumer body (preserve, never disturb)
**Source:** `RecoveryConsumerBase<TMessage>` (`src/Keeper/Recovery/RecoveryConsumerBase.cs:43-52` — `Guard<T>` / `Guard` void overload; `:30` `Db`, `:31` `Send`).
**Apply to:** the InjectConsumer edit. Removing op 3 must leave ops 1-2's `Guard(...)` wrapping intact. `Consume` (`:34-35`) dispatches straight to `HandleAsync` — unchanged.

### NSubstitute behavioral assertion idiom (the KINJ-03 proof style — D-04)
**Positive (DELETE deletes):** `DeleteConsumerFacts.cs:51-55` — `db.Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(...), Arg.Any<CommandFlags>())`.
**Negative (INJECT/REINJECT do not):** `db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>()/RedisKey[](), Arg.Any<CommandFlags>())` — both overloads.
**Reflection negative guard:** `Assert.Null(typeof(T).GetProperty("X"))` — established at `KeeperContractTests.cs:37`.

### Wire-tolerance (no migration step — D-03 forbids one)
**Source:** `KeeperInject.cs:3` comment ("bus envelope — NO [JsonPropertyName], default STJ"). Default STJ drops the now-unknown `DeleteEntryId` on in-flight pre-deploy messages. Planner MUST NOT add versioning.

## No Analog Found

None. Every file maps to its own current state or to an in-tree sibling. The single net-new file has two strong sibling fact analogs plus the shared `RecoveryTestKit`.

## Metadata

**Analog search scope:** `src/Keeper/Recovery/`, `src/Messaging.Contracts/`, `src/BaseProcessor.Core/Processing/`, `tests/BaseApi.Tests/{Keeper,Contracts,Processor,Orchestrator}/`
**Files scanned:** 13 (5 source + 7 test + harness), all read this session — zero re-reads, line numbers confirmed against live tree (matches RESEARCH.md zero-drift verification).
**Pattern extraction date:** 2026-06-16
