# Phase 54: Terminal Index Delete + Atomic Keeper GC — Research

**Researched:** 2026-06-12
**Domain:** C#/.NET backend — StackExchange.Redis L2 projection GC; xUnit + NSubstitute hermetic facts
**Confidence:** HIGH (every code claim grounded in a file:line read this session; every Redis API claim verified against the pinned 2.13.1 assembly XML)

## Summary

Phase 54 implements design amendment **A19**: the processor's end-of-message tail actively reclaims the `L2[messageId]` allocation index instead of leaving it to expire by random TTL. The happy-path tails (forward `:309` + recovery all-clear `:180-185`) currently delete only the source `L2[entryId]` via a single-key `KeyDeleteAsync(RedisKey)`. A19 unifies both into ONE shared private method that issues a single atomic multi-key `DEL` of `[ExecutionData(entryId), MessageIndex(messageId)]`, escalates a delete exhaustion by `PERSIST`ing the index (best-effort) then sending a `KeeperDelete` that now carries `MessageId`, and the keeper's `DeleteConsumer` deletes both keys in one multi-key `DEL`.

This is a pure behavior change inside a fully hermetic test surface. There is NO frontend dimension. All production code touched is four files (`ProcessorPipeline.cs`, `KeeperDelete.cs`, `DeleteConsumer.cs`, and — only if a discretion helper is chosen — `L2ProjectionKeys.cs`, which D-02 explicitly says to leave alone). Tests are three existing fact files edited in place plus the `DispatchTestKit` mock extended with the array-delete overload.

**Primary recommendation:** Extract `DeleteTerminalAsync(d, messageId, db, limit, ct)` per D-01, build the `RedisKey[]` inline per D-02, wrap both the multi-key `DEL` and the `KeyPersistAsync` in the existing `RetryLoop`, and invert the existing facts to assert exactly ONE `Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), ...)` call (the array overload) — NOT the single-key `KeyDeleteAsync(RedisKey)` overload that the current facts assert.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Atomic two-key terminal delete (GC-01) | Processor (`ProcessorPipeline`) | Redis (single `DEL key1 key2`) | The processor owns the message lifecycle; atomicity is delegated to Redis single-threaded execution |
| REINJECT mutual exclusion (GC-02) | Processor (`ProcessorPipeline`) | — | Gating lives in the recovery/forward control flow; no other tier sees the gate |
| Persist-on-escalate + both-key keeper DEL (GC-03) | Processor (escalate) + Keeper (`DeleteConsumer`) | Contract (`KeeperDelete.MessageId`) | Escalation crosses the processor→keeper bus boundary; the contract carries the new id |

## User Constraints (from CONTEXT.md)

### Locked Decisions

- **D-01 (Tail structure):** Extract ONE shared private method `DeleteTerminalAsync(EntryStepDispatch d, Guid messageId, IDatabase db, int limit, CancellationToken ct)` on `ProcessorPipeline`. Both the forward happy-path tail (replacing `DeleteSourceTail()` local fn, called at `:228`/`:247`/`:253`/`:309`) and the recovery all-clear tail (replacing the inline delete at `:180-185`) call it. The two-key `DEL` + persist-on-escalate + `KeeperDelete` handoff lives in ONE place.
- **D-02 (Key-array shape):** Build the RedisKey array INLINE at the call site: `db.KeyDeleteAsync(new RedisKey[]{ L2ProjectionKeys.ExecutionData(d.EntryId), L2ProjectionKeys.MessageIndex(messageId) })`, wrapped in `RetryLoop`. `L2ProjectionKeys` stays returning pure `string` shapes — NO new array-returning member.
- **D-03 (Persist-on-escalate failure handling):** On terminal-delete exhaustion: `RetryLoop`-wrap `db.KeyPersistAsync(MessageIndex(messageId))`, then `SendKeeper(BuildDelete(d, messageId))` — **best-effort persist**. If `KeyPersistAsync` itself exhausts, proceed to send the `KeeperDelete` regardless. A persist failure must NEVER block the keeper handoff.
- **D-04 (Fact placement):** Edit hermetic facts IN PLACE per behavior area — do NOT create a new grouped A19 fact file. (Detailed per-fact map in `## Validation Architecture` below.)
- **D-05:** `KeeperDelete` gains `MessageId` as an `init` property (mirrors the existing `EntryId` init-property pattern; 3 base ids stay positional ctor params). `BuildDelete(d, messageId)` populates it from the inbound `messageId`.
- **D-06:** The source-step guard inverts: the current `if (SourceStep.IsSource(d.EntryId)) return;` early-skip in the tail is removed — for a source step the index `DEL` still runs; the `ExecutionData(Guid.Empty)` operand is a harmless absent no-op.
- **D-07:** Per-slot random-TTL writes (`ProcessorPipeline.cs:165`/`:275`) remain present, unchanged — the crash-before-terminal-delete backstop.
- **D-08:** No new metric series. `UseMessageRetry = none` Phase-53 end-state preserved (send-exhaustion still throws → broker redelivery; no `_error` routing).

### Claude's Discretion

- The NSubstitute test-kit mock shape for the multi-key `KeyDeleteAsync(RedisKey[], CommandFlags)` overload (extend `DispatchTestKit.PresentReadWriteDeleteOkL2` / `ReadOkDeleteFaultL2` to mock the array overload alongside the single-key one) — mechanical. See de-risking guidance in `## NSubstitute Mock Shape` below.
- Exact internal control flow of `DeleteTerminalAsync` (parameter ordering, how the source-step no-op is expressed) provided it satisfies D-01/D-02/D-03/D-06.

### Deferred Ideas (OUT OF SCOPE)

- None — discussion stayed within phase scope. (Live proof + close-gate net-zero is the already-planned **Phase 55**, not a deferral.)

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| GC-01 | Atomic two-key terminal delete — no-REINJECT happy-path tail deletes source data key + origin index key in one atomic Redis multi-key `DEL` | `KeyDeleteAsync(RedisKey[], CommandFlags)` verified `Task<long>` (count removed), single `DEL key1 key2` command — see `## Standard Stack`. Unified tail per D-01; inline array per D-02. Forward tail anchor `:195-201`→`:309`; recovery anchor `:180-185`. |
| GC-02 | REINJECT mutual exclusion preserved — two-key delete runs ONLY on no-REINJECT path; any REINJECT leaves both keys intact | Recovery gate `if (!SourceStep.IsSource(d.EntryId))` sits under `if (anyInfra) { …return; }` at `:174-185`. Forward REINJECT paths return BEFORE the tail (`:98`/`:129`/`:220`). New index-delete inherits the same gate by living in the unified method. |
| GC-03 | Persist-on-escalate + both-key keeper DELETE — terminal-delete exhaustion cancels index TTL, hands both-key delete to keeper | `KeyPersistAsync(RedisKey, CommandFlags)` verified `Task<bool>`. `KeeperDelete` gains `MessageId` (D-05). `DeleteConsumer.HandleAsync` (`:19-20`) → both-key `Db.KeyDeleteAsync(new RedisKey[]{…})` via `Guard`. |

## Code Anchors (verified against current source)

Every SPEC/CONTEXT-cited line number was checked against the live file. **All cited anchors are accurate — no drift detected.**

### `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs`

| Element | SPEC/CONTEXT cite | Verified line(s) | Notes |
|---------|-------------------|------------------|-------|
| `DeleteSourceTail()` local fn (forward tail) | `:195-201` | **`:195-201`** ✓ | Local async fn inside `RunForwardAsync`. Body: source-step early-return (`:197`), then single-key `KeyDeleteAsync(ExecutionData(d.EntryId))` in `RetryLoop` (`:198-199`), exhaust → `SendKeeper(BuildDelete(d))` (`:200`). |
| Forward-tail call sites | `:228`/`:247`/`:253`/`:309` | **`:228`, `:247`, `:253`, `:309`** ✓ | `:228` business-fail; `:247` ProcessStatusException; `:253` unexpected exception; `:309` happy-path Post tail. |
| Recovery all-clear tail (inline delete) | `:180-185` / `:182-185` | **`:180-185`** ✓ | `if (!SourceStep.IsSource(d.EntryId))` (`:180`) → single-key `KeyDeleteAsync(ExecutionData(d.EntryId))` in `RetryLoop` (`:182-183`), exhaust → `SendKeeper(BuildDelete(d))` (`:184`). |
| Recovery REINJECT gate (anyInfra) | `:174`/`:177` | **`:174-178`** ✓ | `var anyInfra = temp.Any(t => t.Infra);` (`:174`); `if (anyInfra) { SendKeeper(BuildReinject(d)…); return; }` (`:175-178`). The all-clear delete at `:180-185` runs only when `!anyInfra`. |
| Per-slot random-TTL writes (D-07, UNCHANGED) | `:165`/`:275` | **`:165`** (recovery retire refresh), **`:275`** (forward alloc) ✓ | Both are `KeyExpireAsync(MessageIndex(messageId), SlotTtl())` in `RetryLoop`. MUST remain. |
| `BuildReinject` REINJECT escalations | `:98`/`:129`/`:220` | **`:98`** (exist-check exhaust), **`:129`** (HGETALL exhaust), **`:220`** (forward Pre read exhaust) ✓ | All three `return` WITHOUT reaching any tail. |
| `SendKeeper` | `:340` | **`:340-346`** ✓ | Sends to `KeeperQueues.Recovery`; throws on send-exhaust (propagate → broker redelivery). |
| `BuildDelete` | `:367-368` | **`:367-368`** ✓ | `new KeeperDelete(d.WorkflowId, d.StepId, d.ProcessorId){ CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId }`. Gains `MessageId = messageId` and a `messageId` param (D-05). |
| `SlotTtl()` helper | — | `:82-83` | `Random.Shared.Next(min, max+1)` seconds. Untouched. |

### Other files

| Element | CONTEXT cite | Verified | Notes |
|---------|--------------|----------|-------|
| `KeeperDelete` `EntryId` init prop | `KeeperDelete.cs:11` | **`:11`** ✓ | `public Guid EntryId { get; init; }`. Add `public Guid MessageId { get; init; }` directly below. |
| `DeleteConsumer.HandleAsync` | `DeleteConsumer.cs:19-20` | **`:19-20`** ✓ | `=> await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.EntryId)), ct);` — single-key. Becomes both-key array. |
| `L2ProjectionKeys.ExecutionData` | `:42` | **`:42`** ✓ | `=> $"{Prefix}data:{entryId:D}"`. Returns `string`. |
| `L2ProjectionKeys.MessageIndex` | `:48` | **`:48`** ✓ | `=> $"{Prefix}msg:{messageId:D}"`. Returns `string`. |
| `SourceStep.IsSource` | — | `SourceStep.cs:8` | `=> entryId == Guid.Empty`. |
| `RetryLoop.ExecuteAsync` | — | `RetryLoop.cs:10-21` | `static Task<RetryOutcome<T>> ExecuteAsync<T>(Func<Task<T>> op, int limit, CancellationToken ct)`. Surfaces exhaustion as `RetryOutcome` (`.Succeeded`/`.Value`/`.Error`), never throws on op failure. |
| `RecoveryConsumerBase.Guard` | — | `RecoveryConsumerBase.cs:48-57` | `Guard<T>(Func<Task<T>>, ct)` and void `Guard(Func<Task>, ct)`; re-throws `.Error` on exhaustion → skp-dlq-1. The both-key `DEL` slots straight in (it returns `Task<long>`, so the `Guard<T>` generic overload binds). |

## Standard Stack

### Core (already in the solution — no new packages)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| StackExchange.Redis | 2.13.1 | L2 projection key ops (`KeyDeleteAsync`, `KeyPersistAsync`, `KeyExpireAsync`, …) | Pinned in `Directory.Packages.props:131` (CPM); already the sole Redis client. `[VERIFIED: Directory.Packages.props]` |
| xUnit | (solution-pinned) | Hermetic fact framework | All existing facts use `[Fact]` + `TestContext.Current.CancellationToken`. `[VERIFIED: test files]` |
| NSubstitute | (solution-pinned) | Redis `IDatabase` / send-provider fakes | `DispatchTestKit` + `RecoveryTestKit` are built on `Substitute.For<IDatabase>()`. `[VERIFIED: test kits]` |

**Installation:** None. No new dependency. **Do NOT add packages.**

### Verified Redis API signatures (StackExchange.Redis 2.13.1)

Confirmed directly from `~/.nuget/packages/stackexchange.redis/2.13.1/lib/net8.0/StackExchange.Redis.xml`:

| Member | Signature | Returns | Source |
|--------|-----------|---------|--------|
| Single-key delete (current) | `KeyDeleteAsync(RedisKey key, CommandFlags flags = None)` | `Task<bool>` (true if removed) | `[VERIFIED: SE.Redis 2.13.1 XML :10634]` |
| **Multi-key delete (GC-01 target)** | `KeyDeleteAsync(RedisKey[] keys, CommandFlags flags = None)` | `Task<long>` (count of keys removed) | `[VERIFIED: SE.Redis 2.13.1 XML :10637]` |
| **Persist (GC-03 target)** | `KeyPersistAsync(RedisKey key, CommandFlags flags = None)` | `Task<bool>` (true if TTL removed) | `[VERIFIED: SE.Redis 2.13.1 XML :10676]` |

**Atomicity claim** `[VERIFIED: SE.Redis 2.13.1 XML + Redis DEL semantics]`: the `RedisKey[]` overload issues a **single `DEL key1 key2 …` command** (one round trip, one server-side command). On single-instance `sk-redis` this executes atomically by Redis single-threaded execution — exactly the SPEC's "ONE `DEL key1 key2` command". (Cluster multi-slot atomicity is explicitly out of scope — SPEC Constraints.)

**Drop-on-absent** `[VERIFIED: SE.Redis 2.13.1 XML + Redis DEL semantics]`: Redis `DEL` of an absent key is a no-op that does not error; it simply contributes 0 to the returned count. So the source-step `ExecutionData(Guid.Empty)` operand (GC-01) and either absent operand in the keeper delete (GC-03) no-op without throwing — the array DEL just removes fewer keys.

### How the existing single-key call is wrapped (drop-in confirmation)

Forward tail (`:198-199`):
```csharp
var del = await RetryLoop.ExecuteAsync(
    () => db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(d.EntryId)), limit, ct);
if (!del.Succeeded) await SendKeeper(BuildDelete(d), limit, ct);
```
The array form drops in identically — `RetryLoop.ExecuteAsync<long>` infers `T = long` from `Task<long>`; `.Succeeded` is the only field read (the `long` count is ignored). `[VERIFIED: RetryLoop.cs:10-21, ProcessorPipeline.cs:198-199]`

## Architecture Patterns

### Unified terminal-delete control flow (D-01/D-02/D-03/D-06)

```
RunForwardAsync (happy/business-fail/in-exception exits)  ─┐
                                                           ├─► DeleteTerminalAsync(d, messageId, db, limit, ct)
RunRecoveryAsync (all-clear tail, !anyInfra && reached)   ─┘        │
                                                                   ├─ multi-key DEL [ExecutionData(d.EntryId), MessageIndex(messageId)]  (RetryLoop)
                                                                   │     • source step: ExecutionData(Guid.Empty) operand no-ops (D-06)
                                                                   │     • success → done
                                                                   └─ on DEL exhaust:
                                                                         KeyPersistAsync(MessageIndex(messageId))  (RetryLoop, BEST-EFFORT — D-03)
                                                                         └─ regardless of persist outcome:
                                                                            SendKeeper(BuildDelete(d, messageId))   (carries MessageId — D-05)

REINJECT paths (forward :220, recovery :129/:177, dispatch :98) ──► return BEFORE DeleteTerminalAsync is ever called (GC-02)
```

### Recommended shape of `DeleteTerminalAsync` (satisfies the locks; control flow is Claude's discretion)

```csharp
// Source: synthesized from ProcessorPipeline.cs:195-201 + :182-184 + D-01/D-02/D-03/D-06
private async Task DeleteTerminalAsync(
    EntryStepDispatch d, Guid messageId, IDatabase db, int limit, CancellationToken ct)
{
    // D-06: NO source-step early-return — the index DEL runs even for a source step;
    // ExecutionData(Guid.Empty) is a harmless absent operand (drop-on-absent).
    var del = await RetryLoop.ExecuteAsync(
        () => db.KeyDeleteAsync(new RedisKey[]
        {
            L2ProjectionKeys.ExecutionData(d.EntryId),     // operand 1 (Guid.Empty → no-op on source step)
            L2ProjectionKeys.MessageIndex(messageId),      // operand 2 (the index — actively reclaimed)
        }), limit, ct);
    if (del.Succeeded) return;

    // D-03: best-effort persist (cancel the random TTL) then escalate REGARDLESS of persist outcome.
    await RetryLoop.ExecuteAsync(() => db.KeyPersistAsync(L2ProjectionKeys.MessageIndex(messageId)), limit, ct);
    await SendKeeper(BuildDelete(d, messageId), limit, ct);
}
```

### `KeeperDelete` contract change (D-05)

```csharp
// Source: KeeperDelete.cs:7-12 (add the MessageId init prop)
public sealed record KeeperDelete(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }
    public Guid MessageId     { get; init; }   // A19: the origin index id, for the keeper both-key DEL
}
```

### `DeleteConsumer` both-key delete (GC-03)

```csharp
// Source: DeleteConsumer.cs:19-20 (array overload; Guard<long> binds the generic overload)
protected override async Task HandleAsync(KeeperDelete m, CancellationToken ct)
    => await Guard(() => Db.KeyDeleteAsync(new RedisKey[]
    {
        L2ProjectionKeys.ExecutionData(m.EntryId),
        L2ProjectionKeys.MessageIndex(m.MessageId),
    }), ct);
```

### Anti-Patterns to Avoid

- **Two single-key deletes instead of one array DEL.** Issuing `KeyDeleteAsync(ExecutionData)` then `KeyDeleteAsync(MessageIndex)` defeats the atomicity guarantee and FAILS the GC-01 acceptance ("ONE multi-key call, not two single-key calls"). The fact must assert the array overload received exactly once AND the scalar overload received zero index/source deletes.
- **Adding an `L2ProjectionKeys.TerminalKeys(...)` array member.** D-02 rejects this — the array is a call-local SE.Redis concept; keep `L2ProjectionKeys` string-only.
- **Throwing on persist-exhaust.** D-03 rejects this — persist is best-effort; a throw would abandon the in-hand keeper escalation and force a slower replay self-heal.
- **Deleting the per-slot TTL writes.** D-07: `:165`/`:275` are the crash-before-terminal-delete backstop. Untouched.
- **Re-introducing a source-step early-return in the tail.** D-06 inverts this — the source step now DELETEs the index.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Atomic multi-key delete | A MULTI/EXEC transaction or a Lua script | `KeyDeleteAsync(RedisKey[])` | One `DEL key1 key2` is already atomic on single-instance Redis; a transaction is heavier and unnecessary `[VERIFIED: SE.Redis 2.13.1]` |
| Bounded retry around the DEL / PERSIST | A hand-rolled loop | `RetryLoop.ExecuteAsync` | The whole codebase wraps every L2 op in it; surfaces exhaustion as `RetryOutcome` `[VERIFIED: RetryLoop.cs]` |
| Keeper-side retry + DLQ escalation | New try/catch in `DeleteConsumer` | inherited `Guard()` | `RecoveryConsumerBase.Guard` already does RetryLoop + re-throw → skp-dlq-1 `[VERIFIED: RecoveryConsumerBase.cs:48-57]` |
| Source-step sentinel check | `entryId == Guid.Empty` inline | `SourceStep.IsSource(entryId)` | The single canonical predicate (D-07 anti-pattern note) `[VERIFIED: SourceStep.cs:8]` |

**Key insight:** A19 is deliberately small — every primitive it needs (atomic DEL, RetryLoop, Guard, SourceStep, SendKeeper, BuildDelete) already exists. The phase is composition + fact inversion, not new infrastructure.

## NSubstitute Mock Shape (Claude's Discretion — de-risked)

### Current single-key mock (what exists)

`DispatchTestKit` stubs the **scalar** overload only:
```csharp
db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);   // PresentReadWriteDeleteOkL2:147
```
and the fault muxes throw on both scalar overloads via `When/Do` (`ReadOkDeleteFaultL2:328-331`, `ForwardDeleteFaultL2:256-259`).

`RecoveryTestKit.Db()` stubs the scalar overload (`:75`).

### Array-overload mock to add

The multi-key overload is a **distinct method** (`KeyDeleteAsync(RedisKey[], CommandFlags)` → `Task<long>`). NSubstitute treats it independently of the scalar overload, so a new stub is required:

```csharp
// success muxes (PresentReadWriteDeleteOkL2, ForwardOkL2, RecoveryL2, RecoveryAllCompletedL2, RecoveryTestKit.Db):
db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);   // count removed (value ignored by code)

// fault muxes (ReadOkDeleteFaultL2, ForwardDeleteFaultL2): throw on the array overload too
db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())).Do(_ => throw boom);
// keep the existing scalar When/Do as well (defensive — code now uses the array form, but harmless)

// persist (D-03): stub so the best-effort persist resolves; a fault mux may leave it throwing to
// prove fall-through still sends the keeper:
db.KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
```

### Gotchas (flagged)

1. **Array-matcher binding.** Use `Arg.Any<RedisKey[]>()` — NOT `Arg.Any<RedisKey>()` — for the array overload. Mixing them silently leaves the array overload unstubbed (returns `default(Task<long>)` → `0L`, which `del.Succeeded` reads as success since the op didn't throw; a fault mux that forgets the array `When/Do` would NOT fault). This is the single highest-risk mistake.
2. **Asserting "ONE multi-key call, not two scalar calls"** (the GC-01 heart):
   ```csharp
   await db.Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
   await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // no scalar delete
   await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());                              // no scalar (no-flags) delete
   ```
3. **Asserting array CONTENTS** (both operands present). NSubstitute argument matchers over arrays need a predicate matcher because array equality is reference-based:
   ```csharp
   await db.Received(1).KeyDeleteAsync(
       Arg.Is<RedisKey[]>(ks =>
           ks.Length == 2 &&
           ks.Contains((RedisKey)L2ProjectionKeys.ExecutionData(entryId)) &&
           ks.Contains((RedisKey)L2ProjectionKeys.MessageIndex(messageId))),
       Arg.Any<CommandFlags>());
   ```
   (`RedisKey` is a value type with `==`/`Equals` over the underlying key bytes, so `Contains` works. Cast the `string` from `L2ProjectionKeys` to `RedisKey` explicitly — there is an implicit conversion, but inside the lambda an explicit cast avoids ambiguity.)
4. **Default `Task<long>` is non-null in recent NSubstitute** (returns a completed `Task` wrapping `0L`), so an unstubbed array overload will NOT NRE — it will silently "succeed". This is why gotcha #1 matters: a forgotten fault stub yields a false green.
5. The pipeline calls `db.KeyDeleteAsync(new RedisKey[]{…})` with the **default `CommandFlags`** (no explicit flag arg). The compiler binds the `(RedisKey[], CommandFlags = None)` overload — the only array overload — so `Arg.Any<CommandFlags>()` matches.

## Validation Architecture

> The downstream Nyquist step greps for this exact `## Validation Architecture` heading. This section is the heart of Phase 54.

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xUnit (solution-pinned) + NSubstitute |
| Config file | none — facts are plain `[Fact]` classes under `tests/BaseApi.Tests/` |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~PipelineEndDeleteFacts|FullyQualifiedName~PipelineRecoveryFacts|FullyQualifiedName~DeleteConsumerFacts"` |
| Full suite command | `dotnet test` (solution root) |
| Build gate | `dotnet build -c Release` AND `dotnet build -c Debug` — 0 warnings (SPEC Constraints) |

### Requirement → Fact Map (GC-01/02/03 + 10 acceptance criteria)

| Req / AC | Behavior | File · Fact (action) | Observable signal asserted | Edge covered |
|----------|----------|----------------------|-----------------------------|--------------|
| GC-01 / AC-1 | Forward happy tail = ONE multi-key DEL | `PipelineEndDeleteFacts.EndDelete_RunsOnHappyPath` (**INVERT**) | `Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(ks => contains ExecutionData(entryId) && MessageIndex(messageId)))` AND `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), …)` AND `Empty(SentKeeper.OfType<KeeperDelete>())` | happy path |
| GC-01 | Business-fail tail = same multi-key DEL | `PipelineEndDeleteFacts.EndDelete_RunsOnBusinessFail` (**UPDATE delete assertion**) | `Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` (array, both operands) | business-fail exit (`:228`) |
| GC-01 | In-exception tail = same multi-key DEL | `PipelineEndDeleteFacts.EndDelete_RunsOnInException` (**UPDATE delete assertion**) | `Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` | ProcessStatusException exit (`:247`) |
| GC-01 / AC-2 | Recovery all-clear tail = same multi-key DEL | `PipelineRecoveryFacts.AllClear_DeletesSource` (**INVERT** scalar→array) | `Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(ks => ExecutionData(d.EntryId) && MessageIndex(messageId)))` AND `Empty(SentKeeper.OfType<KeeperReinject>())` | recovery all-clear (`!anyInfra`, `:180-185`) |
| GC-01 / AC-3 | Source step: index deleted, Guid.Empty data operand no-ops, no throw | `PipelineEndDeleteFacts.EndDelete_Skipped_OnSourceStep` (**INVERT** → rename intent: now DELETES the index) | `Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(ks => contains MessageIndex(messageId) && ExecutionData(Guid.Empty)))` AND no exception (test completes) | source step `entryId == Guid.Empty` (D-06) |
| GC-02 / AC-4 | Forward Pre-read-exhaust REINJECT: NEITHER key deleted | `PipelineEndDeleteFacts.EndDelete_Skipped_OnReinject` (**KEEP**, add array DidNotReceive) | `Single(SentKeeper.OfType<KeeperReinject>())` AND `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` AND existing scalar `DidNotReceive` | forward read-exhaust REINJECT (`:220`) |
| GC-02 / AC-4 | Recovery anyInfra REINJECT: NEITHER key deleted, index survives | `PipelineRecoveryFacts.MixedSlots_…NoSourceDelete` (**KEEP**, add array DidNotReceive) | `Single(SentKeeper.OfType<KeeperReinject>())` AND `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` (+ scalar). "Index survives" = no DEL of `MessageIndex` issued | recovery anyInfra (`:174-178`) |
| GC-02 | HGETALL-exhaust REINJECT: no delete | `PipelineRecoveryFacts.HGetAllFault_Reinject_NoSourceDelete` (**KEEP**, add array DidNotReceive) | `Single(OfType<KeeperReinject>())` AND `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` | recovery HGETALL exhaust (`:129`) |
| GC-03 / AC-5 | Tail DEL exhaust → `KeyPersistAsync(MessageIndex)` THEN `KeeperDelete` carrying MessageId | `PipelineEndDeleteFacts.EndDelete_Exhaust_Delete` (**UPDATE**: add persist + MessageId asserts) | `Received(1).KeyPersistAsync((RedisKey)MessageIndex(messageId), …)` AND `Single(SentKeeper.OfType<KeeperDelete>())` whose `.MessageId == messageId` (and ordering: persist before send — assert via `Received.InOrder` or capture index) | tail DEL exhaust (`ReadOkDeleteFaultL2`) |
| GC-03 | Persist-exhaust still escalates (best-effort fall-through, D-03) | **ADD** `PipelineEndDeleteFacts.EndDelete_PersistExhaust_StillSendsKeeper` (NEW) | with a mux where BOTH the array DEL and `KeyPersistAsync` throw: `Single(SentKeeper.OfType<KeeperDelete>())` (keeper sent despite persist failure) | persist-exhaust fall-through |
| GC-03 / AC-6 | `KeeperDelete` exposes `MessageId`; `BuildDelete` stamps it | covered by AC-5 fact's `.MessageId == messageId` assertion (+ compile-time: contract has the prop) | `KeeperDelete.MessageId` populated from inbound messageId | contract shape |
| GC-03 / AC-7 | `DeleteConsumer` = ONE multi-key DEL of both keys | `DeleteConsumerFacts.Delete_deletes_execution_data_key` (**INVERT** scalar→array) | `Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(ks => ExecutionData(m.EntryId) && MessageIndex(m.MessageId)), …)` | keeper both-key delete |
| GC-03 / AC-7 | Keeper drop-on-absent (no throw) | `DeleteConsumerFacts.Delete_absent_key_no_throws` (**UPDATE**: stub array overload `.Returns(0L)`) | array DEL returns 0; `Consume` completes; `Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` | absent operands |
| AC-8 | Per-slot TTL writes remain | (regression guard) existing forward/recovery facts still pass with `KeyExpireAsync` stubbed; optionally add `Received().KeyExpireAsync(MessageIndex(messageId), …)` in a forward-happy fact | `:165`/`:275` unchanged | crash backstop preserved |
| AC-9 | No new metric series | (negative) no new `metrics.*.Add` site introduced; no test asserts a new counter | — | observability frozen (D-08) |
| AC-10 | 0-warning Release+Debug; full suite green | build gate + `dotnet test` | — | — |

### Sampling Rate

- **Per task commit:** quick run command (the 3 fact files above).
- **Per wave merge:** `dotnet test` (full hermetic suite).
- **Phase gate:** `dotnet build -c Release` + `-c Debug` 0-warning AND full suite green before `/gsd-verify-work`.

### Edge-coverage rationale (which Nyquist edges are sampled)

| Edge | Sampled by |
|------|-----------|
| source-step `Guid.Empty` operand | AC-3 fact (`EndDelete_Skipped_OnSourceStep` inverted) |
| REINJECT path (3 variants: forward read, recovery anyInfra, recovery HGETALL) | AC-4 + the two recovery REINJECT facts |
| persist-exhaust fall-through | NEW `EndDelete_PersistExhaust_StillSendsKeeper` |
| drop-on-absent (keeper) | `Delete_absent_key_no_throws` |
| atomicity (one array vs two scalar) | every GC-01/GC-03 fact pairs `Received(1)` array with `DidNotReceive()` scalar |

### Wave 0 Gaps

- [ ] `DispatchTestKit` — add `KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` stub to every success mux (`PresentReadWriteDeleteOkL2`, `ForwardOkL2`, `RecoveryL2`, `RecoveryAllCompletedL2`) and `When/Do` throw to fault muxes (`ReadOkDeleteFaultL2`, `ForwardDeleteFaultL2`); add `KeyPersistAsync` stub.
- [ ] `RecoveryTestKit.Db()` — add `KeyDeleteAsync(Arg.Any<RedisKey[]>(), …).Returns(2L)` (and a `.Returns(0L)` variant for the absent fact).
- [ ] NEW fact `EndDelete_PersistExhaust_StillSendsKeeper` needs a mux where the array DEL AND `KeyPersistAsync` both throw (extend `ReadOkDeleteFaultL2` or add a sibling).

*(No new fact FILE — D-04 mandates in-place edits.)*

## Common Pitfalls

### Pitfall 1: False-green from an unstubbed array overload
**What goes wrong:** A fault mux throws on the scalar `KeyDeleteAsync(RedisKey)` but not the array overload; the code now calls the array form, which returns a default `0L` → `del.Succeeded == true` → the exhaust/escalation branch never runs and the fact passes for the wrong reason.
**Why:** NSubstitute treats overloads independently; default `Task<long>` is non-null.
**How to avoid:** Every fault mux MUST add `When(x => x.KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)).Do(throw)`.
**Warning sign:** an "exhaust" fact passes but `SentKeeper.OfType<KeeperDelete>()` is empty.

### Pitfall 2: Asserting two scalar calls instead of one array call
**What goes wrong:** Updating a fact to `Received(2).KeyDeleteAsync(Arg.Any<RedisKey>())` to "cover both keys" — this is the exact behavior GC-01 forbids.
**How to avoid:** Assert `Received(1)` on the **array** overload + `DidNotReceive()` on both scalar overloads.

### Pitfall 3: Persist-exhaust blocking the keeper send
**What goes wrong:** Wrapping persist + send so a persist throw short-circuits the send (e.g. `if (persist.Succeeded) SendKeeper(...)`).
**How to avoid:** D-03 — call `SendKeeper` unconditionally after the persist attempt; ignore the persist outcome.

### Pitfall 4: Deleting the per-slot TTL writes
**What goes wrong:** A plan "cleans up" the `KeyExpireAsync` at `:165`/`:275` thinking the active delete replaces them.
**How to avoid:** D-07 — they are the crash-before-terminal-delete backstop and MUST remain. AC-8 guards this.

### Pitfall 5: `RedisKey[]` array argument matching
**What goes wrong:** `Received(1).KeyDeleteAsync(new RedisKey[]{…}, flags)` with a literal array never matches (reference inequality).
**How to avoid:** Use `Arg.Is<RedisKey[]>(ks => …Contains…)` predicate matchers (see NSubstitute Mock Shape #3).

## Runtime State Inventory

This phase changes the processor's terminal behavior; it does NOT rename or migrate stored data, and it operates entirely on transient L2 projection keys. The relevant runtime consideration is data *left behind by prior code*, not data this phase must migrate.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | `skp:msg:*` index HASHes currently linger 300–600s post-message (A18 TTL-only reclaim). After A19 they are actively deleted at end-of-message. **No migration needed** — existing lingering keys still self-expire by their TTL; A19 only changes go-forward behavior. | None — TTL backstop (D-07) handles the transition |
| Live service config | None — no n8n/Datadog/Tailscale config references the processor tail. | None — verified by scope (pure code change) |
| OS-registered state | None — no Task Scheduler / systemd registration touches this code. | None |
| Secrets/env vars | None — no key/secret name changes; `Retry:Limit`, `SlotArray:Ttl*` configs unchanged. | None |
| Build artifacts | None — no project rename; `KeeperDelete` gaining a field is a source change recompiled normally. | None — verified, no `*.egg-info`/binary rename equivalent in .NET here |

**Canonical question (post-edit):** After the code ships, no runtime system holds a stale string — the only "state" is in-flight `skp:msg:*` keys, which the retained random TTL (D-07) reclaims for any pre-A19 message; A19 messages are reclaimed deterministically by the new DEL. No data migration task is required.

## Environment Availability

Pure hermetic phase — no external dependency at test time (NSubstitute fakes Redis). The only toolchain needs:

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build + `dotnet test` | ✓ (assumed — solution builds today) | net10.0 target present in SE.Redis lib set | — |
| StackExchange.Redis | code (compile) | ✓ | 2.13.1 (pinned, cached in `~/.nuget`) | — |
| Live Redis (`sk-redis`) | NOT required this phase | n/a (hermetic) | — | Phase 55 owns the live proof |

**Missing dependencies with no fallback:** None.

## State of the Art

| Old Approach (≤ Phase 53 / A18) | Current Approach (A19, this phase) | When Changed | Impact |
|--------------------------------|-------------------------------------|--------------|--------|
| Index reclaimed passively by random TTL (300/600s) | Index actively deleted at end-of-message via atomic two-key DEL | A19 locked 2026-06-12 | Close-gate net-zero becomes a deterministic production property, not a TTL race (proven live in Phase 55) |
| Tails delete source-only (`KeyDeleteAsync(RedisKey)`) | Tails delete `[source, index]` (`KeyDeleteAsync(RedisKey[])`) | A19 | One round trip, atomic |
| `KeeperDelete{…, entryId}` source-only | `KeeperDelete{…, entryId, messageId}` both-key | A19 (D-05) | Keeper GC reclaims the index too |

**Deprecated/outdated:** Nothing removed — the random-TTL write is *retained* as a backstop (D-07), not deprecated.

## Project Constraints (from CLAUDE.md)

No `./CLAUDE.md` exists in the working directory (verified via Glob — no match). Constraints are sourced from the SPEC instead:
- 0-warning build in **both** Release and Debug.
- Full hermetic suite green.
- Fact naming follows the existing `*Facts` class / behavior-named `[Fact]` convention (e.g. `EndDelete_RunsOnHappyPath`).
- No new metric series (D-08); `UseMessageRetry = none` preserved.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The .NET SDK targeted by the solution is available in CI/local (the solution builds today). | Environment Availability | Low — phase can't build/test without it, but this is the existing baseline, not new. |
| A2 | `RetryLoop.ExecuteAsync<long>` infers `T=long` cleanly from `Task<long>` (the array DEL) exactly as it does for `Task<bool>` today. | Standard Stack | Low — generic inference is standard C#; verified the method is fully generic (`RetryLoop.cs:10`). |
| A3 | Recent NSubstitute returns a non-null completed `Task<long>` (wrapping `0L`) for an unstubbed `Task<long>` member. | NSubstitute Mock Shape / Pitfall 1 | Medium — if an old NSubstitute returns `null`, an unstubbed array overload would NRE (loud) rather than false-green (silent). Either way the mitigation (always stub the array overload) is the same. Verify against the pinned NSubstitute version during Wave 0 if a false-green is suspected. |

**All other claims are `[VERIFIED]` against source/assembly read this session.**

## Open Questions

1. **Persist-before-send ordering assertion mechanism.**
   - What we know: AC-5 requires `KeyPersistAsync` to be called *then* `KeeperDelete` sent.
   - What's unclear: whether to assert strict ordering (`Received.InOrder`) or just both-occurred. The persist hits the `db` mock and the send hits the `CapturingSendProvider` (different objects), so `Received.InOrder` across two substitutes is awkward.
   - Recommendation: assert `Received(1).KeyPersistAsync(...)` on `db` AND `Single(SentKeeper.OfType<KeeperDelete>())` on the provider, plus the `.MessageId` value. Strict cross-substitute ordering is low-value here (D-03 makes persist best-effort anyway); the planner can treat strict ordering as optional.

2. **Whether `EndDelete_PersistExhaust_StillSendsKeeper` needs a new mux or can extend `ReadOkDeleteFaultL2`.**
   - What we know: it needs both the array DEL and `KeyPersistAsync` to throw.
   - What's unclear: cleanest placement (extend the existing fault mux vs add a sibling `ReadOkDeleteAndPersistFaultL2`).
   - Recommendation: add a sibling mux to keep `ReadOkDeleteFaultL2`'s existing semantics (persist succeeds there) intact for the AC-5 fact; this is Claude's-discretion test-kit shaping.

## Sources

### Primary (HIGH confidence)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — all tail/escalation/TTL anchors (read in full).
- `src/Messaging.Contracts/KeeperDelete.cs`, `src/Keeper/Recovery/DeleteConsumer.cs`, `src/Keeper/Recovery/RecoveryConsumerBase.cs`, `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs`, `src/Messaging.Contracts/SourceStep.cs`, `src/BaseConsole.Core/Resilience/RetryLoop.cs`.
- `tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs`, `PipelineRecoveryFacts.cs`, `DispatchTestKit.cs`; `tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs`, `RecoveryTestKit.cs`.
- `~/.nuget/packages/stackexchange.redis/2.13.1/lib/net8.0/StackExchange.Redis.xml` — `KeyDeleteAsync(RedisKey[],CommandFlags)`, `KeyDeleteAsync(RedisKey,CommandFlags)`, `KeyPersistAsync(RedisKey,CommandFlags)` signatures (lines 10634/10637/10676).
- `Directory.Packages.props:131` — StackExchange.Redis 2.13.1 pin.
- `.planning/phases/54-terminal-index-delete/54-SPEC.md`, `54-CONTEXT.md`.

### Secondary (MEDIUM confidence)
- None required — every claim resolved against primary sources.

### Tertiary (LOW confidence)
- A3 (NSubstitute default `Task<long>` non-null) — training knowledge, flagged for Wave 0 verification if a false-green appears.

## Metadata

**Confidence breakdown:**
- Code anchors: HIGH — every line verified against current source; zero drift from SPEC/CONTEXT cites.
- Redis API (multi-key DEL, persist): HIGH — verified against the pinned 2.13.1 assembly XML, not training data.
- NSubstitute mock shape: MEDIUM-HIGH — the array-overload independence and matcher mechanics are standard; the only soft spot is A3 (default `Task<long>` nullability), which has the same mitigation either way.
- Validation Architecture: HIGH — each requirement and all 10 ACs mapped to a named existing fact + observable signal.

**Research date:** 2026-06-12
**Valid until:** 2026-07-12 (stable — no fast-moving dependency; pinned Redis client, locked SPEC/decisions)

## RESEARCH COMPLETE
