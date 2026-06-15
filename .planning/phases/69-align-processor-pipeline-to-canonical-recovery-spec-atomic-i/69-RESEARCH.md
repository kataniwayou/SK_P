# Phase 69: Align processor pipeline to canonical recovery spec — Research

**Researched:** 2026-06-16
**Domain:** StackExchange.Redis atomic multi-key writes (Lua), processor Post-Process pipeline, escalation routing, hermetic NSubstitute fault-injection tests
**Confidence:** HIGH (every claim grounded in a read file:line in this repo; spec quoted verbatim)

## Summary

Phase 69 closes three divergences between `docs/design/processor-keeper-recovery-spec.md` and `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs`, exactly as the spec's own §10 "Relationship to current implementation" enumerates them. The work is surgical and almost entirely contained in the forward-Post loop (`ProcessorPipeline.cs:257-310`) plus its test doubles in `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` and `PipelineForwardFacts.cs`.

The three changes:
1. **Collapse the 3-op forward write into ONE atomic write** so an exhausted index write no longer DROPs (INFRA-01). Today the loop does `HashSetAsync` (slot) → `KeyExpireAsync` (index TTL) → `StringSetAsync` (data) as three independently-retried ops (`ProcessorPipeline.cs:270-291`); the slot-write exhaust path `continue`s with no escalation (`:280`). The spec §4.3 step 3 wants index-slot + index-TTL + data-key + data-TTL in "one atomic operation," and §10 says "this spec's atomic write makes both failure modes one `INJECT`, so no drop path exists."
2. **Gate the forward cleanup tail** (`DeleteTerminalAsync`, `:309`/`:316-330`) on "no item escalated to the keeper this pass" (spec §4.3 final paragraph). Today it runs unconditionally.
3. **Reconcile the In-Process per-item contract** — spec §10 itself notes the code already carries `executionId` and threads it through the seam (confirmed: `ProcessorPipeline.cs:235` passes `d.ExecutionId`; `ProcessItem` carries `ExecutionId`). The remaining divergence is descriptive only (the code's `ProcessOutcome` enum is `{Completed, Failed}` and `processing`/`cancelled` arrive via thrown status) and is **not** a code change this phase — see Decision point D-3.

**Primary recommendation:** Implement the atomic write as a **Lua `ScriptEvaluateAsync`** — it is the only mechanism that gets all four sub-ops (HSET slot, PEXPIRE index, SET data, PEXPIRE data) atomic on the **Redis 6.x / RESP-2 / SE.Redis 2.13.1** baseline this repo runs, and it is a pattern the codebase has shipped before (Phase 40 `KeeperRecoveryHandler`) and the test harness already supports (`FakeRedis.ScriptEvaluateAsync`, `FakeRedis.cs:177`). `CreateTransaction` (MULTI/EXEC) and `CreateBatch` (pipelining) both give weaker guarantees and neither solves hash-field-TTL; see §"Don't Hand-Roll" and Decision point D-1.

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Atomic index+data L2 write | Processor (BaseProcessor.Core) | Redis (Lua script execution) | The write is processor-owned forward-Post logic; Redis executes the atomic script server-side. |
| INFRA-01 escalation routing | Processor pipeline | Keeper (INJECT consumer) | Processor decides drop-vs-escalate; keeper completes the escalated item. |
| Gated cleanup decision | Processor pipeline | — | Pure local control-flow in `RunForwardAsync`; no other tier involved. |
| Skipped-cleanup recovery safety | Keeper (DELETE) + Recovery pass | Redis TTL | When cleanup is skipped, keys are reclaimed by a later Recovery pass or TTL, per spec §4.3. |

---

<phase_requirements>
## Phase Requirements

No requirement IDs are mapped (ROADMAP §Phase 69 "Requirements: TBD"). Must-haves derived from the spec + phase goal:

| Derived ID | Description | Research Support |
|----|-------------|------------------|
| ATOMIC-01 | Forward-Post writes index-slot + index-TTL + data-key + data-TTL in ONE atomic op (spec §4.3 step 3) | Lua `ScriptEvaluateAsync` — §"Standard Stack", Code Example 1; precedent `40-REVIEW.md:56-64` |
| NODROP-01 | An exhausted forward write escalates as a single `INJECT` — no DROP path (spec §10 bullet 1, closes INFRA-01) | Current drop at `ProcessorPipeline.cs:280`; INJECT builder ready at `:392-400` |
| GATE-01 | Forward cleanup tail runs only if no item escalated to the keeper this pass (spec §4.3 final ¶) | `DeleteTerminalAsync` call site `ProcessorPipeline.cs:309`; escalation site `:288` |
| GATE-02 | When cleanup is skipped, a later Recovery pass / TTL safely reclaims index + input keys (spec §4.3, §5) | `RunRecoveryAsync` `:129-192` already redelivers from the slot array idempotently |
</phase_requirements>

## User Constraints

**No CONTEXT.md exists for this phase.** The design contract is `docs/design/processor-keeper-recovery-spec.md`, which the user OWNS and which is **locked/normative**. Do NOT propose scope changes to the spec. The phase scope is exactly: atomic write + gated cleanup (+ confirm the In-Process contract, which §10 says is already aligned).

**Project facts (from project memory / CLAUDE-equivalent):**
- `tests/BaseApi.Tests` is **xunit.v3 / Microsoft.Testing.Platform (MTP)**. `dotnet test --filter` is **silently ignored** (runs all ~638 tests). Use `-- --filter-method` (see §"Build/verify commands").
- Platform is Windows / PowerShell.
- No `./CLAUDE.md` exists in the working directory (checked — file not found). No `.claude/skills/` or `.agents/skills/` directories exist.

---

## The exact code being changed (forward-Post loop)

`src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:257-310` — the three separate ops + the DROP + the unconditional tail:

```csharp
// ---- POST (forward, per item, in order) ----
var slot = 0;
foreach (var item in items)
{
    var outcome = item.Result;
    if (outcome == ProcessOutcome.Completed
        && !ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, item.Data, out _))
        outcome = ProcessOutcome.Failed;

    if (outcome == ProcessOutcome.Completed)
    {
        var entryId = NewId.NextGuid();                     // (1) allocate

        var alloc = await RetryLoop.ExecuteAsync(           // (2) ALLOCATION INDEX FIRST  ── OP A
            () => db.HashSetAsync(L2ProjectionKeys.MessageIndex(messageId), slot, entryId.ToString("D")), limit, ct);
        if (alloc.Succeeded)
        {
            await RetryLoop.ExecuteAsync(                   // whole-HASH EXPIRE            ── OP B
                () => db.KeyExpireAsync(L2ProjectionKeys.MessageIndex(messageId), SlotTtl()), limit, ct);
        }
        else
        {
            // INFRA-01: allocation exhausted → infra_messageId → DROP (no data write, no send, no slot).
            continue;                                       // ◄── THE DROP (spec §10 bullet 1)
        }

        var write = await RetryLoop.ExecuteAsync(           // (3) DATA SECOND             ── OP C
            () => db.StringSetAsync(L2ProjectionKeys.ExecutionData(entryId), item.Data, executionDataTtl), limit, ct);
        if (!write.Succeeded)
        {
            // INFRA-02: data-write exhausted → keeper INJECT (data in-hand); the slot WAS allocated → consume it.
            await SendKeeper(BuildInject(d, item, entryId), limit, ct);   // ◄── escalation site (:288)
            slot++;
            continue;
        }
        ...
        await SendResult(BuildCompleted(d, item.ExecutionId, entryId), limit, ct);
        slot++;
    }
    else { await SendResult(BuildFailed(d, "output failed schema validation"), limit, ct); }
}

await DeleteTerminalAsync(d, messageId, db, limit, ct);     // ◄── unconditional tail (:309) — gate this
```

**The three ops (OP A/B/C) become ONE atomic Lua call.** The slot ordinal (`slot++`) is a local counter, not part of the atomicity. After the change, an exhaust of the single atomic write routes to `BuildInject(...)` for BOTH the index-failure and data-failure modes (today only the data failure escalates; the index failure at `:280` drops).

---

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| StackExchange.Redis | 2.13.1 | L2 client; `IDatabase.ScriptEvaluateAsync` is the atomic-write vehicle | Pinned in `Directory.Packages.props` (CPM); already the only Redis client in the tree |
| Lua (Redis EVAL) | server-side | Atomic HSET-slot + PEXPIRE-index + SET-data + PEXPIRE-data in one round trip | Only mechanism giving all four sub-ops atomic on RESP-2 / Redis 6.x; codebase precedent (Phase 40) |

**Version verification:** `Directory.Packages.props` declares `StackExchange.Redis 2.13.1` `[VERIFIED: grep of *.csproj + Directory.Packages.props comment "Plan 12-01 D / CPM"]`. All 6 referencing csproj files use the CPM `<PackageReference Include="StackExchange.Redis" />` form (no per-project version) — so the single CPM pin governs.

### Supporting (already present — no new packages)
| Component | Location | Role this phase |
|-----------|----------|-----------------|
| `RetryLoop.ExecuteAsync<T>` | `src/BaseConsole.Core/Resilience/RetryLoop.cs:10-21` | Wraps the single atomic `ScriptEvaluateAsync` call; exhaust → route to INJECT |
| `BuildInject(d, item, entryId)` | `ProcessorPipeline.cs:392-400` | Builds `KeeperInject` — already carries the full id-set (see §INJECT below) |
| `L2ProjectionKeys.MessageIndex` / `.ExecutionData` | `Messaging.Contracts/Projections/L2ProjectionKeys.cs:55,61` | The two keys the script touches |
| `FakeRedis.ScriptEvaluateAsync` | `tests/BaseApi.Tests/Keeper/FakeRedis.cs:177-191` | Existing harness support for stubbing a Lua eval (precedent only — pipeline tests use NSubstitute `IDatabase`, see §Test Surface) |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Lua `ScriptEvaluateAsync` | `db.CreateTransaction()` (MULTI/EXEC) | Gives all-or-nothing execution BUT cannot express "set TTL on a hash field"; would still need the whole-HASH PEXPIRE as a queued command (works), yet MULTI/EXEC has no conditional logic and SE.Redis transactions are awkward (each queued op returns a `Task` you await AFTER `Execute()`). No codebase precedent in the processor path. |
| Lua `ScriptEvaluateAsync` | `db.CreateBatch()` | **NOT atomic** — batch only pipelines for one network flush; ops can interleave/partially-apply. Used elsewhere (`RedisProjectionWriter.cs:76`, `RedisL2Cleanup.cs:103`) explicitly as "batch ≠ transaction, no MULTI/EXEC" (`15-RESEARCH.md:42`). Wrong tool for an atomicity requirement. |
| Server-side Lua | RESP3 hash-field TTL (`HEXPIRE`, Redis 7.4+) | Spec note acknowledges TTL-on-hash-field is RESP3/7.4+; the repo's index TTL is a **whole-HASH** EXPIRE (`SlotTtl()` applied to `MessageIndex` via `KeyExpireAsync`, `:274-275`), NOT a per-field TTL — so 7.4 is NOT required and Lua with `PEXPIRE` on the whole key is sufficient and version-safe. |

**Installation:** None. No new packages.

---

## Architecture Patterns

### Data flow (forward-Post, after the change)

```
items[] ──► per item ──► output-schema validate
                              │ not completed ──► SendResult(StepFailed) ──► next item
                              │ completed
                              ▼
                       entryId = NewId.NextGuid()
                              ▼
              ┌─────────── ONE atomic Lua ScriptEvaluateAsync ───────────┐
              │ HSET  L2[messageId] slot entryId                          │
              │ PEXPIRE L2[messageId] indexTtlMs   (SlotTtl, whole-hash)  │
              │ SET   L2[entryId] data                                    │
              │ PEXPIRE L2[entryId] dataTtlMs                             │
              └───────────────────────────┬───────────────────────────────┘
                          RetryLoop wraps the single call
                   success │                 │ exhausted
                           ▼                 ▼
                 SendResult(StepCompleted)   SendKeeper(BuildInject(d,item,entryId))
                 slot++                      escalated=true ; slot++  (consume the slot)
                           │                 │
                           └────────┬────────┘
                                    ▼  (after the foreach)
                         escalated ?  ── yes ──► SKIP DeleteTerminalAsync (leave keys for keeper/Recovery/TTL)
                                       ── no  ──► DeleteTerminalAsync(d, messageId, ...)  (atomic 2-key DEL)
```

### Pattern 1: Atomic multi-op L2 write via Lua (the closest existing analog)
**What:** A single `ScriptEvaluateAsync(script, keys[], args[])` whose Lua body issues every sub-op so Redis runs them atomically (single-threaded, no interleave).
**When to use:** Whenever ≥2 L2 writes must succeed/fail together. This is the exact INFRA-01 fix.
**Closest existing analog** — Phase 40 `KeeperRecoveryHandler` (file since deleted in v4/v5 teardown; pattern preserved in `40-REVIEW.md:56-64`):

```csharp
// Source: .planning/phases/40-keeper-recovery-hardening/40-REVIEW.md:56-64
const string IncrWithTtl = @"
    local n = redis.call('INCR', KEYS[1])
    if n == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end
    return n";
var n = (long)await db.ScriptEvaluateAsync(
    IncrWithTtl, new[] { key }, new RedisValue[] { (long)TimeSpan.FromSeconds(300).TotalMilliseconds });
```

A Phase-69 adaptation (illustrative — the planner specifies the exact text):

```csharp
// Atomic index-slot + whole-hash TTL + data-key + data TTL.
// KEYS[1] = L2[messageId] (index hash)   KEYS[2] = L2[entryId] (data key)
// ARGV[1] = slot   ARGV[2] = entryId(string)   ARGV[3] = data
// ARGV[4] = indexTtlMs (SlotTtl)   ARGV[5] = dataTtlMs (executionDataTtl)
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
if (!write.Succeeded) { await SendKeeper(BuildInject(d, item, entryId), limit, ct); escalated = true; slot++; continue; }
```

> NOTE the whole-hash `PEXPIRE` semantics match today's behavior: `SlotTtl()` is currently applied to the WHOLE index hash via `KeyExpireAsync(MessageIndex(messageId), ...)` (`:274-275`), not to a single field. The Lua `PEXPIRE KEYS[1]` preserves that exactly. (Confirm the per-call random `SlotTtl()` is computed once in C# and passed as `ARGV[4]` — see Pitfall 3.)

### Pattern 2: Gated cleanup via a local flag
**What:** A `bool escalated = false;` declared before the `foreach`, set `true` at the INJECT escalation site, checked before the tail.
**When to use:** This phase's GATE-01.
```csharp
var escalated = false;
// ... in the exhaust branch: await SendKeeper(BuildInject(...)); escalated = true; slot++; continue;
// after the loop:
if (!escalated)
    await DeleteTerminalAsync(d, messageId, db, limit, ct);
// else: leave L2[messageId] + L2[inputEntryId] for the keeper + later Recovery/TTL (spec §4.3)
```

### Anti-Patterns to Avoid
- **Using `CreateBatch` for the atomic write.** Batch is pipelining, not atomicity (`15-RESEARCH.md:42` — "batch ≠ transaction, no MULTI/EXEC"). It would re-introduce the very partial-apply window the spec closes.
- **Computing `SlotTtl()` inside the Lua script.** `SlotTtl()` uses `Random.Shared` in C# (`ProcessorPipeline.cs:87-91`). Compute it once in C#, pass the ms as an ARGV. Do not try to randomize inside Lua.
- **Retrying individual sub-ops.** The whole point is ONE retried unit. Wrap the single `ScriptEvaluateAsync` in `RetryLoop`, not the sub-ops.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Atomic multi-key write | A C# sequence of `HashSet`+`KeyExpire`+`StringSet` with manual rollback | One Lua `ScriptEvaluateAsync` | Manual rollback is exactly the non-atomic 3-op shape being removed; rollback itself can fail mid-way. Redis Lua is atomic by construction. |
| Bounded retry on the atomic write | A hand-written try/catch loop | `RetryLoop.ExecuteAsync` (`RetryLoop.cs:10`) | One place for the A3 retry semantics; exhaust returns `RetryOutcome.Exhausted` so the caller routes to INJECT (`RetryLoop.cs:26-30`). |
| INJECT message shape | A new contract or extra fields | Existing `KeeperInject` + `BuildInject` | Already carries everything (see §INFRA-01 — INJECT). No contract change needed. |
| Lua test stub | A live Redis in unit tests | NSubstitute `IDatabase.ScriptEvaluateAsync` stub (mirror `FakeRedis.cs:177`) | The whole processor suite is hermetic NSubstitute fakes; keep it that way. |

**Key insight:** The atomic-write requirement is a solved Redis problem (server-side Lua) and this repo already shipped it once. The risk is choosing a *weaker* primitive (batch/transaction) that looks atomic but isn't.

---

## INFRA-01 — the drop, and the INJECT contract (Research Q1 + Q2)

### Where the drop is
`ProcessorPipeline.cs:277-281`: when `alloc.Succeeded == false` (index HSET exhausted) the code `continue`s with **no `SendKeeper`** — the item is silently lost. This is INFRA-01. The existing test even asserts the drop (`PipelineForwardFacts.cs:176-195` `SlotWriteFault_Drop`: `Assert.Empty(send.SentKeeper)`). **That test must be inverted by this phase** (drop → single INJECT). The fault mux `ForwardSlotFaultL2` (`DispatchTestKit.cs:195-213`) currently models the drop; after the change a slot/atomic-write fault must produce a `KeeperInject`.

### What `BuildInject` carries — does the contract need to change? **NO.**
`BuildInject` (`ProcessorPipeline.cs:392-400`) populates the full id-set:

```csharp
private static KeeperInject BuildInject(EntryStepDispatch d, ProcessItem item, Guid entryId) =>
    new(d.WorkflowId, d.StepId, d.ProcessorId)
    {
        CorrelationId = d.CorrelationId,
        ExecutionId   = item.ExecutionId,   // author-minted item exec
        EntryId       = entryId,            // the allocation
        Data          = item.Data,          // raw-JSON output, in-hand
        DeleteEntryId = d.EntryId,          // source entryId
    };
```

`KeeperInject` (`Messaging.Contracts/KeeperInject.cs:8-15`) fields: `WorkflowId, StepId, ProcessorId, CorrelationId, ExecutionId, EntryId, Data, DeleteEntryId`.

The keeper INJECT consumer (`Keeper/Recovery/InjectConsumer.cs:22-41`) uses `m.EntryId` (writes `L2[entryId]=m.Data`), `m.Data`, `m.DeleteEntryId` (source delete), and sends `StepCompleted`. **It does NOT use `messageId` or `slot`** — the keeper's INJECT writes only the data key, not the index slot.

> SPEC vs CODE divergence on INJECT payload (FLAG for the planner — Decision point D-2). Spec §4.3 step 3 / §8 say INJECT carries `(messageId, x, outputEntryId, data, inputEntryId)` and the keeper "atomically write[s] the index slot `L2[messageId][x]=outputEntryId` AND `L2[outputEntryId]=data`." The **current** `KeeperInject` carries NO `messageId` and NO slot `x`, and the current `InjectConsumer` writes ONLY the data key (`InjectConsumer.cs:25`), not the index slot. So the existing code's keeper does LESS than the spec's INJECT. **For the no-drop fix alone, the existing `BuildInject` is sufficient** to escalate an index-write exhaust (data is in-hand; keeper writes the data key and sends StepCompleted; the index slot is whatever it was). Whether to extend INJECT to also re-write the index slot per spec §8 is a scope question — the phase name is "atomic write + gated cleanup," and extending the keeper INJECT to write the index slot is arguably a 4th change. Flagged, not silently expanded.

---

## Gated cleanup (Research Q3) — and why skipping is safe

**Gate mechanism:** a local `bool escalated` flag (Pattern 2). Set at `ProcessorPipeline.cs:288` (the only INJECT site in the forward loop), checked before `:309`.

**What happens to keys when cleanup is skipped:** per spec §4.3 final ¶, the index `L2[messageId]` and input `L2[inputEntryId]` are LEFT INTACT for the keeper to complete the escalated item and for a later Recovery pass or TTL to reclaim them. This "eliminates the processor/keeper race on the index key."

**Is the Recovery path safe to rely on? YES.** `RunRecoveryAsync` (`ProcessorPipeline.cs:129-192`) is reached when `L2[messageId]` EXISTS (a redelivery, `:110-111`). It HGETALLs the slot array (`:133`), and for each non-empty/non-retired slot whose data key still exists, **re-sends idempotently** (send-before-retire, `:165-169`). Skipping cleanup leaves the index alive → a redelivery takes Recovery → re-emits the completed items → then cleans up. This is the spec §5 path and it is already implemented and tested (`PipelineRecoveryFacts.cs`, recovery muxes `DispatchTestKit.cs:399-495`). The index's TTL (`SlotTtl()` = random[dataTtl, 2×dataTtl], `:87-91`) is the backstop if no redelivery ever comes.

> One subtlety the planner must preserve: when cleanup is skipped the index key keeps whatever TTL the atomic write set (the index is born with a TTL inside the atomic op). Today the non-escalated tail's `DeleteTerminalAsync` deletes the index; skipping it just lets the TTL run. No `KeyPersist` is needed on the skip path (that's only for the DELETE-exhaust escalation, `:328`).

---

## Slot retirement divergence (Research Q4) — DECISION POINT, do NOT silently expand

**Spec §4.3 step 5:** "Retire the slot: `L2[messageId][x] = guid.empty`" after the send, on the forward happy path.
**Code:** the forward loop does NOT retire to `guid.empty`. It uses an ordinal `slot++` counter (`:258, :289, :301`) and leaves each completed slot holding its `entryId`. Retirement to `Guid.Empty` happens ONLY in the **Recovery** pass (`:169` writes `RetiredSlot = Guid.Empty.ToString()`, `:77`).

**Is this an intentional codebase choice or a real divergence?** It is the **intentional Phase 50-52 (v5.0.0) model**: forward writes the slot with its `entryId` and leaves it; Recovery is what retires slots to `Guid.Empty` after re-sending (send-before-retire, `:159-180`). The Recovery pass treats a non-empty slot as "re-send if data exists, then retire" and a `Guid.Empty` slot as inert (`:146-147`). The forward path deliberately does NOT retire because the slot value (`entryId`) IS the recovery breadcrumb — retiring it forward would erase what Recovery needs.

**Recommendation:** **Do NOT change forward retirement this phase.** The phase scope is atomic write + gated cleanup. Forward-retire-to-`guid.empty` would (a) break the Recovery contract that reads `entryId` from the slot, and (b) exceed the phase name. Spec §9.1 even self-flags this as an "under-specified point ... idempotent-by-design only if the orchestrator deduplicates re-sends" — i.e. the spec acknowledges the un-retired-after-INJECT slot is acceptable. **FLAG as Decision point D-3** so the planner records it explicitly rather than acting on §4.3 step 5 literally.

---

## In-Process contract reconciliation (Research Q5 / phase goal item 3)

Spec §10 bullet 3 itself says the code "carries an `executionId`" and "`executionId` is threaded through the processor seam for multi-execution." **Confirmed in code:**
- `ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId)` — `src/BaseProcessor.Core/Processing/ProcessItem.cs:7`.
- `ProcessOutcome { Completed, Failed }` — `ProcessOutcome.cs:5` (only two values; `processing`/`cancelled` arrive via thrown `ProcessStatusException`, handled at `ProcessorPipeline.cs:236-249`).
- `executionId` threaded through the seam: `processor.ExecuteAsync(validatedData, d.Payload, d.ExecutionId, ct)` — `ProcessorPipeline.cs:235`; the fake records `LastExecutionId` (`DispatchTestKit.cs:49,66`).

**Conclusion:** there is NO code change required for the In-Process contract this phase — it already matches the spec's §10 description. The only "divergence" is descriptive (enum has 2 values, not 4; record carries `executionId` not `error_message`), and §10 frames these as the code's chosen shape, not a defect. **No action** (Decision point D-3 covers the documentation-only note).

---

## Test Surface (Research Q5/Q6)

### Test project & files
| Item | Path |
|------|------|
| Test project | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (xunit.v3 / MTP) |
| Forward-pass facts (INFRA-01/02, tail) | `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs` |
| Post facts (write/inject/business-fail) | `tests/BaseApi.Tests/Processor/PipelinePostFacts.cs` |
| Recovery facts | `tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs` |
| End-delete facts | `tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs` |
| Shared fault-injection kit | `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` |
| Keeper INJECT consumer facts | `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` |

### Fault-injection pattern (the one to reuse)
The pipeline facts use **NSubstitute `IDatabase` fakes**, NOT the `FakeRedis` class (that's keeper-side). The pattern: a factory in `DispatchTestKit` builds an `IConnectionMultiplexer` whose `GetDatabase` returns a `Substitute.For<IDatabase>()` with selected ops stubbed to succeed and ONE op stubbed to throw `RedisConnectionException` (forcing `RetryLoop` exhaustion). Faults are injected with `db.When(x => x.Op(...)).Do(_ => throw boom)` across every overload the compiler might bind (`DispatchTestKit.cs:101-119` shows the multi-overload guard for `StringSetAsync`).

The directly relevant fakes:
- `ForwardSlotFaultL2` (`DispatchTestKit.cs:195-213`) — HSET throws → **currently models the DROP**. After the change, the atomic write is `ScriptEvaluateAsync`, so the new fault mux must stub `db.When(x => x.ScriptEvaluateAsync(...)).Do(_ => throw boom)` and the assertion flips from `Assert.Empty(send.SentKeeper)` to `Assert.Single(send.SentKeeper.OfType<KeeperInject>())`.
- `ForwardDataFaultL2` (`:220-252`) — data SET throws → INJECT. After the change there is ONE write (the script), so the two muxes (`ForwardSlotFaultL2` + `ForwardDataFaultL2`) **collapse into one "atomic-write-fault" mux** that throws on `ScriptEvaluateAsync` and expects a single INJECT.
- `ForwardOkL2` (`:172-188`) — happy path; after the change it must stub `db.ScriptEvaluateAsync(...).Returns(RedisResult.Create(1))` instead of (or in addition to) the HSET/Expire/SET stubs.

### The two NEW tests the phase must add
1. **No-drop on atomic-write exhaustion → single INJECT** (`PipelineForwardFacts`): atomic-write fault mux → assert `Assert.Single(send.SentKeeper.OfType<KeeperInject>())`, `Assert.Empty(send.Sent.OfType<StepCompleted>())`, and the INJECT carries the id-set (mirror `DataWriteFault_Inject_WithIdSet`, `:197-216`). This **replaces** the current `SlotWriteFault_Drop` assertion (`:176-195`).
2. **Skipped cleanup when any item escalated** (`PipelineForwardFacts`): a batch with at least one item whose atomic write faults (→ INJECT) → assert the tail `DeleteTerminalAsync` did NOT run: `await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())` and `Assert.Empty(send.SentKeeper.OfType<KeeperDelete>())`. Contrast with the happy-path `HappyTail_DeletesSource` (`:240-262`) which asserts the 2-key DEL DOES run when nothing escalated.

### Lua stubbing reference
`FakeRedis.cs:177-191` is the in-repo precedent for stubbing `ScriptEvaluateAsync(string, RedisKey[], RedisValue[], CommandFlags)` and returning `RedisResult.Create(n)`. For the NSubstitute pipeline fakes, the equivalent is:
```csharp
db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
  .Returns(RedisResult.Create(1));                          // happy
// fault:
db.When(x => x.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>()))
  .Do(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: Lua write unreachable"));
```
Watch the overload: `IDatabase.ScriptEvaluateAsync` has multiple signatures (`string`+keys+values, `LuaScript`, `byte[]` hash). Stub the one the production code calls (`string` overload, per the Phase-40 precedent) and guard the other bindable overloads with additional `When/Do` as `DispatchTestKit` already does for `StringSetAsync`/`KeyDeleteAsync`.

---

## Build / verify commands (Research Q6)

```powershell
# Build (Release, 0-warning posture is the repo standard)
dotnet build SK_P.sln -c Release

# Run ONLY the affected processor pipeline facts (MTP — --filter is IGNORED; use -- --filter-method)
dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -- --filter-method "*PipelineForwardFacts*"
dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -- --filter-method "*PipelinePostFacts*"
dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -- --filter-method "*PipelineRecoveryFacts*"
dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -- --filter-method "*InjectConsumerFacts*"

# Full hermetic suite (phase gate — must stay green)
dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj
```

> CRITICAL (project memory): `dotnet test --filter ...` is **silently ignored** by MTP and runs all ~638 tests. The `-- --filter-method "*Pattern*"` form (note the `--` separator passing args to the test platform) is the working selector. `[CITED: project memory MTP filter syntax]`

---

## Common Pitfalls

### Pitfall 1: Choosing a non-atomic primitive
**What goes wrong:** Using `CreateBatch` (pipelining) believing it's atomic. It is not — ops can partially apply, re-opening the INFRA-01 window.
**How to avoid:** Lua `ScriptEvaluateAsync` only. `CreateTransaction` is atomic but clumsy and unprecedented here.
**Warning sign:** Any `db.CreateBatch()` or separate awaited `HashSetAsync`/`StringSetAsync` calls in the new write.

### Pitfall 2: Inverting the wrong existing test
**What goes wrong:** Leaving `SlotWriteFault_Drop` (`PipelineForwardFacts.cs:176`) asserting `Assert.Empty(send.SentKeeper)` after the fix — the suite would either falsely pass (if the mux still drops) or fail confusingly.
**How to avoid:** Replace it with the no-drop→INJECT assertion; update/merge the `ForwardSlotFaultL2` mux to throw on `ScriptEvaluateAsync`.

### Pitfall 3: Randomizing TTL inside Lua / desyncing the index TTL
**What goes wrong:** `SlotTtl()` uses `Random.Shared` (`ProcessorPipeline.cs:87-91`) and is derived from the SAME `ExecutionDataTtlSeconds` as the data TTL (the Phase-68 TEST-06 desync guard, asserted in `PipelineForwardFacts.cs:113-173`). If the atomic write computes TTLs differently, that invariant breaks.
**How to avoid:** Compute `SlotTtl()` (index) and `executionDataTtl` (data) in C# exactly as today, pass both as ARGV ms. Keep the regression test `IndexTtl_IsRandom_BetweenDataTtl_And_2x_AndOutlivesData` green — it inspects `KeyExpireAsync`/`StringSetAsync` calls today; after the change it must inspect the ARGV passed to `ScriptEvaluateAsync` (the planner must update this test's assertion vehicle from call-args to script-args).

### Pitfall 4: Breaking the SLOT-01/02 ordering test
**What goes wrong:** `Completed_AllocationBeforeData` (`PipelineForwardFacts.cs:64-111`) asserts HSET-before-SET ordering by inspecting `db.ReceivedCalls()`. The atomic Lua makes ordering internal to the script — the test's "allocation index written before data key" premise no longer maps to two distinct C# calls.
**How to avoid:** Rework this test to assert the script issues HSET then SET (assert the script body contains the ops in order, or that exactly one `ScriptEvaluateAsync` call happens with the right keys[]). The ordering guarantee moves from "two ordered C# ops" to "one atomic op" — which is strictly stronger.

### Pitfall 5: Gating cleanup on the wrong condition
**What goes wrong:** Gating on "any keeper send" would also skip cleanup when a REINJECT/DELETE happened — but those paths already `return` before the tail (`:106-108`, `:220-221`). The gate is specifically "did a forward-Post INJECT happen this pass."
**How to avoid:** Set `escalated = true` ONLY at the INJECT site (`:288`), nowhere else.

---

## State of the Art

| Old (current code) | New (this phase) | Why |
|--------------------|------------------|-----|
| 3 separate L2 ops (HSET / PEXPIRE / SET), each retried | 1 atomic Lua `ScriptEvaluateAsync`, retried as a unit | Closes the INFRA-01 partial-apply / drop window |
| Index-write exhaust → DROP (`:280`) | Index-write exhaust → single INJECT | No-drop invariant (spec §10) |
| Unconditional `DeleteTerminalAsync` (`:309`) | Gated on `!escalated` | Removes processor/keeper race on the index key |

**No new dependencies, no contract changes (for the no-drop fix).** All machinery (RetryLoop, KeeperInject, BuildInject, Recovery pass, Lua-capable test harness) already exists.

---

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK / `dotnet` | build + test | ✓ (repo builds today) | net8.0 target | — |
| StackExchange.Redis | atomic Lua write | ✓ | 2.13.1 (CPM) | — |
| Redis server (live) | live proof only | not needed for this phase | — | Hermetic NSubstitute fakes cover all new tests; no live Redis required |

This phase is unit-testable with hermetic fakes only — no live Redis, broker, or external service needed for the two new tests or the regression suite.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 + Microsoft.Testing.Platform (MTP) |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -- --filter-method "*PipelineForwardFacts*"` |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |

### Phase Requirements → Test Map
| Req | Behavior | Test Type | Automated Command | File Exists? |
|-----|----------|-----------|-------------------|-------------|
| ATOMIC-01 | Forward-Post issues one atomic write | unit | `dotnet test ... -- --filter-method "*PipelineForwardFacts*"` | ✅ (rework existing ordering/TTL facts) |
| NODROP-01 | Atomic-write exhaust → single INJECT, no drop | unit | same | ❌ Wave 0 (replaces `SlotWriteFault_Drop`) |
| GATE-01 | Cleanup skipped when an item escalated | unit | same | ❌ Wave 0 (new fact) |
| GATE-02 | Recovery redelivers safely after skipped cleanup | unit | `... -- --filter-method "*PipelineRecoveryFacts*"` | ✅ (existing recovery facts already prove this) |

### Sampling Rate
- **Per task commit:** the targeted `--filter-method` run for the file touched.
- **Per wave merge:** `*Pipeline*Facts*` + `*InjectConsumerFacts*`.
- **Phase gate:** full suite green before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] New/updated fault mux in `DispatchTestKit.cs`: `ScriptEvaluateAsync`-throwing mux replacing/merging `ForwardSlotFaultL2` + `ForwardDataFaultL2`; happy mux `ForwardOkL2` stubs `ScriptEvaluateAsync → RedisResult.Create(1)`.
- [ ] `PipelineForwardFacts.cs`: invert `SlotWriteFault_Drop` → `AtomicWriteFault_Inject`; add `EscalatedItem_SkipsCleanup`; rework `Completed_AllocationBeforeData` and `IndexTtl_*` to inspect script ARGV instead of separate call args.

## Security Domain

`security_enforcement` config not located (no `.planning/config.json` security key surfaced). This phase introduces no auth/session/access-control surface; it changes an internal L2 write mechanism. ASVS V5 (Input Validation) is already handled upstream (`ProcessorJsonSchemaValidator`, `:225,:263`). No crypto, no new external input. **No security-relevant change.** The one note: the Lua script body is a compile-time constant string with parameterized KEYS/ARGV (never string-concatenated user data) — the correct, injection-safe pattern (mirrors `40-REVIEW.md:56`).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The index TTL is a WHOLE-HASH EXPIRE (not per-field), so Lua `PEXPIRE KEYS[1]` suffices and Redis 7.4 / RESP3 is NOT required | Standard Stack / Pattern 1 | LOW — verified: `KeyExpireAsync(MessageIndex(...), ...)` at `:274-275` is a whole-key expire, not `HEXPIRE`. |
| A2 | Redis server is 6.x+ supporting `EVAL`/`ScriptEvaluateAsync` (Lua) | Standard Stack | LOW — Phase 40 shipped Lua against this same Redis; `EVAL` is Redis 2.6+. |
| A3 | Extending the keeper INJECT to also write the index slot per spec §8 is OUT of this phase's "atomic write + gated cleanup" scope | Q2 / Decision D-2 | MEDIUM — if the planner/user reads spec §8 as binding, INJECT contract + InjectConsumer would change too. Flagged as D-2. |
| A4 | Forward slot retirement to `guid.empty` is intentionally NOT done (Phase 50-52 model); spec §4.3 step 5 is satisfied by Recovery-side retirement | Q4 / Decision D-3 | MEDIUM — taking §4.3 step 5 literally would break the Recovery contract. Flagged as D-3. |

## Decision points for the planner

1. **D-1 — Atomic primitive: Lua vs Transaction.** Recommendation: **Lua `ScriptEvaluateAsync`** (codebase precedent Phase 40; only option with all-four-ops atomic on RESP-2; test harness already supports it). `CreateTransaction` is a viable fallback but unprecedented and clumsier. `CreateBatch` is NOT a candidate (not atomic). **HIGH confidence in Lua.**
2. **D-2 — INJECT payload scope.** The spec §8 INJECT carries `(messageId, x, outputEntryId, data, inputEntryId)` and re-writes the index slot; the CURRENT `KeeperInject`/`InjectConsumer` carry no `messageId`/slot and write only the data key. The no-drop fix works with the EXISTING contract (data in-hand → keeper writes data key → StepCompleted). **Decide:** keep the existing INJECT (recommended — stays within "atomic write + gated cleanup"), OR extend the contract + consumer to re-write the index slot per spec §8 (a larger, separable change). Recommendation: keep existing; flag the §8 gap for a follow-up phase.
3. **D-3 — Forward slot retirement.** Spec §4.3 step 5 says retire to `guid.empty` after the send; the code intentionally does NOT (the slot's `entryId` is the Recovery breadcrumb; Recovery retires). **Recommendation: do NOT change forward retirement** — it would break `RunRecoveryAsync` and exceeds the phase scope; spec §9.1 itself accepts the un-retired slot as idempotent-by-design. Record as a documented divergence, no code change.
4. **D-4 — Test rework surface.** Three existing forward facts (`SlotWriteFault_Drop`, `Completed_AllocationBeforeData`, `IndexTtl_IsRandom_*`) embed the 3-separate-ops shape and MUST be reworked to the single-`ScriptEvaluateAsync` shape (inspect script ARGV, not separate call args). Confirm the planner budgets a task for this test migration, not just the two new tests.

## Sources

### Primary (HIGH — read this session)
- `docs/design/processor-keeper-recovery-spec.md` (full) — the locked design contract; §4.3, §5, §6, §8, §9, §10.
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (full) — the file being changed.
- `src/Messaging.Contracts/KeeperInject.cs`, `Keeper/Recovery/InjectConsumer.cs` — INJECT contract + handler.
- `src/BaseConsole.Core/Resilience/RetryLoop.cs`, `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — retry + key builders.
- `src/BaseProcessor.Core/Processing/ProcessItem.cs`, `ProcessOutcome.cs` — In-Process contract.
- `tests/BaseApi.Tests/Processor/{DispatchTestKit,PipelineForwardFacts,PipelinePostFacts}.cs` — test + fault-injection patterns.
- `tests/BaseApi.Tests/Keeper/FakeRedis.cs:177-191` — Lua `ScriptEvaluateAsync` stub precedent.
- `.planning/phases/40-keeper-recovery-hardening/40-REVIEW.md:56-64` — the codebase's atomic Lua pattern.
- `Directory.Packages.props` (via grep) — SE.Redis 2.13.1 CPM pin.

### Secondary (MEDIUM)
- `.planning/phases/15-*/15-RESEARCH.md:42`, `31-*/31-RESEARCH.md:418` — "batch ≠ transaction, no Lua in codebase" (historical; Lua since added/removed in Phase 40).
- `.planning/ROADMAP.md` §Phase 69 — phase goal, Requirements TBD, depends on Phase 68.

## Metadata

**Confidence breakdown:**
- Standard stack (Lua): HIGH — exact in-repo precedent + version-pinned client.
- Architecture / change site: HIGH — every line cited from the read file.
- INJECT contract sufficiency: HIGH for no-drop; the §8 gap is flagged (D-2).
- Slot retirement: HIGH that it's intentional (Recovery-side retirement read directly).
- Test surface: HIGH — fault muxes and the to-invert assertions read directly.

**Research date:** 2026-06-16
**Valid until:** stable (internal code; ~30 days) — re-verify only if the forward-Post loop or `KeeperInject` contract changes before planning.
