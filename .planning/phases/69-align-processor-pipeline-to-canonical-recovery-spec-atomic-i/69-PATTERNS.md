# Phase 69: Align processor pipeline to canonical recovery spec — Pattern Map

**Mapped:** 2026-06-16
**Files analyzed:** 3 artifacts (1 production change, 1 embedded Lua write, test rework across 2 files)
**Analogs found:** 3 / 3 (all in-repo, all exact or in-file)

> This map is the planner's "copy-from" index. It does **not** re-derive the research — see `69-RESEARCH.md` for the decision points (D-1..D-4), the spec citations, and the full change rationale. Here: each changed/new artifact → its closest existing analog → a real excerpt → the deltas the planner must encode.

---

## File Classification

| Changed / New artifact | Role | Data Flow | Closest Analog | Match Quality |
|------------------------|------|-----------|----------------|---------------|
| `ProcessorPipeline.cs` forward-Post atomic write (replaces 3 ops `:270-291`) | production change (service / L2 writer) | transform → atomic multi-key write | `FakeRedis.cs:172-191` Lua `ScriptEvaluateAsync` (Phase 40 KeeperRecoveryHandler precedent) | exact-pattern (different call site) |
| `ProcessorPipeline.cs` exhaustion→`SendKeeper(BuildInject)` + gated tail | production change (escalation control-flow) | event-driven escalation | `ProcessorPipeline.cs:316-330` `DeleteTerminalAsync` (in-file: atomic multi-key DEL + exhaust→`SendKeeper(BuildDelete)`) | exact (same file, sibling op) |
| New / merged fault mux (`ScriptEvaluateAsync`-throwing) + happy mux | test (NSubstitute IDatabase fake) | request-response fault injection | `DispatchTestKit.cs:195-252` (`ForwardSlotFaultL2` + `ForwardDataFaultL2`) | exact (same kit, sibling muxes to merge) |
| New fact `AtomicWriteFault_Inject` (inverts drop→INJECT) | test (fact) | request-response assertion | `PipelineForwardFacts.cs:197-216` `DataWriteFault_Inject_WithIdSet` | exact |
| New fact `EscalatedItem_SkipsCleanup` | test (fact) | request-response assertion | `PipelineForwardFacts.cs:239-262` `HappyTail_DeletesSource` (inverted) + `:60-61` DidNotReceive idiom | exact |
| Reworked facts `Completed_AllocationBeforeData`, `IndexTtl_IsRandom_*`, `SlotWriteFault_Drop` | test (rework) | request-response assertion | `PipelineForwardFacts.cs:64-111`, `:113-173`, `:176-195` (themselves) | self (migrate assertion vehicle) |

---

## Pattern Assignments

### Artifact 1 — Atomic index+data L2 write via Lua `ScriptEvaluateAsync` (production)

**Role:** production change · **Data flow:** transform → one atomic server-side multi-op write
**Replaces:** `ProcessorPipeline.cs:268-291` (mint entryId → `HashSetAsync` slot `:270-271` → `KeyExpireAsync` index TTL `:274-275` → drop on alloc-exhaust `:277-281` → `StringSetAsync` data `:283-284`).

**Closest analog — the Lua eval shape the repo already ships** (`tests/BaseApi.Tests/Keeper/FakeRedis.cs:172-191`, the harness model of the Phase-40 `KeeperRecoveryHandler` INCR+PEXPIRE-NX script). This is the canonical call signature production must call and tests must stub:

```csharp
// FakeRedis.cs:177-191 — the ScriptEvaluateAsync(string, RedisKey[], RedisValue[], flags) contract.
// KEYS[1] = the key, ARGV[1] = the PEXPIRE ms. Returns RedisResult.Create(n).
db.ScriptEvaluateAsync(
        Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
    .Returns(ci =>
    {
        var keys = (RedisKey[])ci[1];
        var args = (RedisValue[])ci[2];
        // ... script semantics ...
        return Task.FromResult(RedisResult.Create(n));
    });
```

**The Lua-author + KEYS/ARGV-wiring + RetryLoop-wrap template** (from `69-RESEARCH.md:184-201`, illustrative — planner pins exact text):

```csharp
// Atomic: index slot HSET + whole-hash PEXPIRE + data SET-with-PX. ONE retried unit.
const string AtomicWrite = @"
    redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
    redis.call('PEXPIRE', KEYS[1], ARGV[4])
    redis.call('SET', KEYS[2], ARGV[3], 'PX', ARGV[5])
    return 1";
var write = await RetryLoop.ExecuteAsync(
    () => db.ScriptEvaluateAsync(AtomicWrite,
        new RedisKey[] { L2ProjectionKeys.MessageIndex(messageId), L2ProjectionKeys.ExecutionData(entryId) },
        new RedisValue[] { slot, entryId.ToString("D"), item.Data,
                           (long)SlotTtl().TotalMilliseconds, (long)executionDataTtl.TotalMilliseconds }),
    limit, ct);
```

**Deltas the planner must encode:**
- **Script body is a compile-time `const string`** with parameterized `KEYS`/`ARGV` — never string-concatenated data (injection-safe; mirrors the Phase-40 precedent). The script's existence is the only genuinely *new* code artifact in this phase.
- **TTLs are computed in C#, passed as ARGV ms.** `SlotTtl()` (`ProcessorPipeline.cs:87-91`) uses `Random.Shared` — it MUST stay in C# (research Pitfall 3). `PEXPIRE KEYS[1] ARGV[4]` is a **whole-hash** expire, byte-for-byte matching today's `KeyExpireAsync(MessageIndex(...), SlotTtl())` at `:274-275` (Assumption A1 — NOT a per-field `HEXPIRE`, so Redis 7.4 is not required).
- **The single call is wrapped in `RetryLoop.ExecuteAsync`** (`src/BaseConsole.Core/Resilience/RetryLoop.cs:10`), not the sub-ops. This is the same `RetryLoop` already wrapping every op in this file (e.g. `:102`, `:270`, `:319`).
- **`slot` stays a local C# ordinal** (`:258`, `:289`, `:301`) — it is an ARGV input, not part of atomicity.

---

### Artifact 2 — Exhaustion→`SendKeeper(BuildInject)` + gated cleanup tail (production)

**Role:** production change · **Data flow:** event-driven escalation + local control-flow gate
**Closest analog is IN-FILE:** `DeleteTerminalAsync` (`ProcessorPipeline.cs:316-330`) — the existing "one atomic L2 op, exhaust → escalate to keeper" shape. The new atomic write's exhaust path is the same skeleton with `BuildInject` swapped for `BuildDelete`:

```csharp
// ProcessorPipeline.cs:316-330 — the template: atomic multi-key op in RetryLoop; on exhaust, escalate to keeper.
private async Task DeleteTerminalAsync(EntryStepDispatch d, Guid messageId, IDatabase db, int limit, CancellationToken ct)
{
    var del = await RetryLoop.ExecuteAsync(
        () => db.KeyDeleteAsync(new RedisKey[] {                 // ◄── ONE atomic multi-key op
            L2ProjectionKeys.ExecutionData(d.EntryId),
            L2ProjectionKeys.MessageIndex(messageId),
        }), limit, ct);
    if (del.Succeeded) return;
    // exhaust → best-effort persist, THEN escalate to the keeper REGARDLESS of persist outcome.
    await RetryLoop.ExecuteAsync(() => db.KeyPersistAsync(L2ProjectionKeys.MessageIndex(messageId)), limit, ct);
    await SendKeeper(BuildDelete(d, messageId), limit, ct);      // ◄── escalation-on-exhaustion
}
```

**The existing in-loop INJECT escalation site** (`ProcessorPipeline.cs:285-291`) is the literal shape to KEEP for the no-drop fix — only its *trigger* changes (any atomic-write exhaust, not just data-write exhaust):

```csharp
// ProcessorPipeline.cs:285-291 (today the DATA exhaust; after the change the ATOMIC-WRITE exhaust)
if (!write.Succeeded)
{
    await SendKeeper(BuildInject(d, item, entryId), limit, ct);  // INJECT — data in-hand
    slot++;                                                       // consume the slot
    continue;
}
```

`BuildInject` (`ProcessorPipeline.cs:392-400`) already carries the full id-set (`EntryId`, `Data`, `DeleteEntryId`) — **no contract change** for the no-drop fix (research D-2: extending INJECT to re-write the index slot per spec §8 is OUT of scope).

**Gated-tail pattern (GATE-01)** — a local flag, set ONLY at the INJECT site, checked before the unconditional tail at `:309` (research Pattern 2 / Pitfall 5):

```csharp
var escalated = false;                                  // before the foreach
// ... in the exhaust branch: await SendKeeper(BuildInject(...)); escalated = true; slot++; continue;
if (!escalated)
    await DeleteTerminalAsync(d, messageId, db, limit, ct);   // was unconditional at :309
// else: leave L2[messageId] + L2[inputEntryId] for keeper / Recovery / index-TTL (spec §4.3 final ¶)
```

**Deltas the planner must encode:**
- **Eliminate the DROP** at `:277-281` (the `else { continue; }` after a failed alloc) — it disappears entirely; both index- and data-failure now route to the single `SendKeeper(BuildInject(...))`.
- **The two old escalation branches collapse to one.** Today: alloc-exhaust→drop (`:280`), data-exhaust→INJECT (`:288`). After: one atomic-write exhaust→INJECT.
- **`escalated` flag set ONLY at the forward-Post INJECT site** — never on REINJECT/DELETE paths (they already `return` before the tail: `:106-108`, `:220-221`, `:247-248`). Gating on "any keeper send" is the Pitfall-5 trap.
- **No `KeyPersist` on the skip path.** The skip just lets the index TTL (born inside the atomic write) run; `KeyPersist` is only for the DELETE-exhaust escalation (`:328`), unchanged.

---

### Artifact 3 — Test doubles & facts (test rework)

**Role:** test · **Data flow:** request-response fault injection (NSubstitute `IDatabase`)
**Closest analog:** the sibling muxes `ForwardSlotFaultL2` (`DispatchTestKit.cs:195-213`) + `ForwardDataFaultL2` (`:220-252`), which **merge into one** `ScriptEvaluateAsync`-throwing mux. The multi-overload `When/Do` guard idiom they use (`:100-119`, `:230-246`) is the exact pattern to replicate for the single `ScriptEvaluateAsync` overload:

```csharp
// Fault-mux idiom (DispatchTestKit.cs:202-207, ForwardSlotFaultL2) — stub OK, throw on the faulted op,
// guard every bindable overload with a When/Do. Apply the SAME shape to ScriptEvaluateAsync.
db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);   // forward branch
var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: ...");
db.When(x => x.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()))
    .Do(_ => throw boom);
```

After the change (research §"Lua stubbing reference", `69-RESEARCH.md:326-333`):

```csharp
// HAPPY mux (ForwardOkL2 :172-188 gains this):
db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
  .Returns(RedisResult.Create(1));
// FAULT mux (merged ForwardSlotFaultL2 + ForwardDataFaultL2):
db.When(x => x.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>()))
  .Do(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: Lua write unreachable"));
```

**The two NEW facts and their analogs:**

| New fact | Closest analog (copy from) | What changes |
|----------|----------------------------|--------------|
| `AtomicWriteFault_Inject` (no-drop → single INJECT) | `DataWriteFault_Inject_WithIdSet` (`PipelineForwardFacts.cs:197-216`) | Same body & id-set assertions (`Assert.Single(...OfType<KeeperInject>())`, `inj.DeleteEntryId`, `inj.EntryId`, `Assert.Empty(...StepCompleted)`); swap the mux to the merged `ScriptEvaluateAsync`-fault mux. **This REPLACES `SlotWriteFault_Drop` (`:176-195`)** — invert `Assert.Empty(send.SentKeeper)` → `Assert.Single(...KeeperInject)`. |
| `EscalatedItem_SkipsCleanup` (GATE-01) | `HappyTail_DeletesSource` (`:239-262`), inverted, + the DidNotReceive idiom at `:60-61` | Batch with ≥1 atomic-write-faulting item → assert `await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())` and `Assert.Empty(send.SentKeeper.OfType<KeeperDelete>())`. Contrast: `HappyTail_DeletesSource` asserts the 2-key DEL **does** run when nothing escalated. |

**The three facts to REWORK** (research D-4 — migrate the assertion vehicle from separate-call-args to script-args):

| Fact | Today inspects | After |
|------|----------------|-------|
| `SlotWriteFault_Drop` (`:176-195`) | `Assert.Empty(send.SentKeeper)` + `DidNotReceive().StringSetAsync(...)` | **becomes** `AtomicWriteFault_Inject` (invert to single INJECT) |
| `Completed_AllocationBeforeData` (`:64-111`) | `db.ReceivedCalls()` ordering of `HashSetAsync` before `StringSetAsync` (`:82-99`) | one `ScriptEvaluateAsync` call with the right `KEYS[]`; ordering is now internal to the script (strictly stronger) — Pitfall 4 |
| `IndexTtl_IsRandom_BetweenDataTtl_And_2x_AndOutlivesData` (`:113-173`) | `TtlOf(KeyExpireAsync call)` for index, `TtlEquals(StringSetAsync call)` for data (`:134-172`) | inspect the **ARGV** passed to the single `ScriptEvaluateAsync` (index ms = ARGV[4] ∈ [300k,600k], data ms = ARGV[5] == 300k) — Pitfall 3; keep the `[dataTtl, 2×dataTtl]` invariant green |

**Deltas the planner must encode:**
- `RedisResult.Create(1)` is the happy return (matches `FakeRedis.cs:190`). The `ScriptEvaluateAsync(string, RedisKey[], RedisValue[], CommandFlags)` overload is the one production calls and tests stub (Phase-40 precedent) — guard other bindable overloads if the compiler binds them.
- The fault provider / capture is unchanged: `CapturingSendProvider` (`DispatchTestKit.cs:568-590`) already captures `IStepResult` → `Sent` and `IKeeperRecoverable` → `SentKeeper`. Reuse as-is.
- `ForwardSlotFaultL2` + `ForwardDataFaultL2` are DELETED/merged; `ForwardOkL2`, `PresentReadWriteDeleteOkL2`, `ReadOkDeleteFaultL2`, and the recovery muxes (`:399-495`) are UNCHANGED (recovery is untouched this phase).

---

## Shared Patterns

### Bounded retry — wrap the single op, route on exhaust
**Source:** `src/BaseConsole.Core/Resilience/RetryLoop.cs:10` (`RetryLoop.ExecuteAsync<T>`); used throughout `ProcessorPipeline.cs` (`:102`, `:133`, `:270`, `:319`).
**Apply to:** the new atomic `ScriptEvaluateAsync` call (one retried unit) and any new test that forces exhaustion (throw inside the stub so the loop exhausts and routes to INJECT).
```csharp
var r = await RetryLoop.ExecuteAsync(() => db.<op>(...), limit, ct);
if (!r.Succeeded) { /* route to keeper */ }
```

### Keeper escalation builder — reuse, do not extend
**Source:** `ProcessorPipeline.cs:392-400` (`BuildInject`) — already carries `EntryId`/`Data`/`DeleteEntryId`.
**Apply to:** the atomic-write exhaust escalation (Artifact 2). No `KeeperInject` contract change (research D-2; the §8 index-slot re-write is a flagged follow-up, NOT this phase).

### Lua stub semantics
**Source:** `tests/BaseApi.Tests/Keeper/FakeRedis.cs:177-191` — the in-repo precedent for stubbing `ScriptEvaluateAsync` and returning `RedisResult.Create(n)`.
**Apply to:** every pipeline mux that must let the atomic write succeed (happy) or throw (fault).

---

## No Analog Found

None. Every changed/new artifact has an in-repo analog (the Lua eval, the in-file atomic-DEL escalation, the sibling fault muxes, the sibling facts). The only genuinely new code is the `const string` Lua body itself — and even that has a structural precedent in the Phase-40 INCR+PEXPIRE script (`FakeRedis.cs:172-191`).

## Out-of-Scope Divergences (flagged, NOT code changes this phase)

These spec points exist but the research recommends NO action — recorded so the planner does not act on them literally:
- **D-2 (spec §8 INJECT payload):** current `KeeperInject`/`InjectConsumer` write only the data key; extending to re-write the index slot is a separable phase. Keep existing.
- **D-3 (spec §4.3 step 5 forward slot retirement):** the code intentionally does NOT retire forward (the `entryId` is the Recovery breadcrumb; Recovery retires at `:169`). Taking §4.3 step 5 literally would break `RunRecoveryAsync`. No code change; documented divergence.
- **In-Process contract (spec §10 bullet 3):** already aligned (`ProcessItem` carries `ExecutionId`; seam threads it at `:235`). No change.

## Metadata

**Analog search scope:** `src/BaseProcessor.Core/Processing/`, `tests/BaseApi.Tests/Processor/`, `tests/BaseApi.Tests/Keeper/`, `src/BaseConsole.Core/Resilience/`, `src/Messaging.Contracts/`.
**Files read this session:** `processor-keeper-recovery-spec.md`, `69-RESEARCH.md`, `ProcessorPipeline.cs`, `DispatchTestKit.cs`, `FakeRedis.cs:160-204`, `PipelineForwardFacts.cs:1-265`.
**Pattern extraction date:** 2026-06-16
