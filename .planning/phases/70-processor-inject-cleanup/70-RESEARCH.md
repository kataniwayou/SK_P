# Phase 70: Processor INJECT Cleanup - Research

**Researched:** 2026-06-16
**Domain:** .NET 8 / C# backend refactor — MassTransit keeper recovery state machine, NSubstitute behavioral facts, xunit.v3/MTP
**Confidence:** HIGH (every CONTEXT.md file/line reference verified against live code this session)

## Summary

This is a mechanical, fully-pinned cleanup. The keeper INJECT path drops its trailing source-delete so it matches the canonical recovery spec (§8: DELETE is the only state that deletes) and the already-non-destructive REINJECT path. The change is one deleted Redis op (`InjectConsumer.cs:40`) plus one deleted contract field (`KeeperInject.DeleteEntryId`), but it inverts delete-assertions across four test files and adds one dedicated invariant fact.

I verified all 7 source/test touch-points and every line number in CONTEXT.md against the live tree — **zero drift**. A repo-wide `DeleteEntryId` grep returns exactly the 6 code files CONTEXT.md names (KeeperInject.cs, ProcessorPipeline.cs, InjectConsumer.cs, InjectConsumerFacts.cs, KeeperContractTests.cs, PipelineForwardFacts.cs) plus SC2RecoveryPathsE2ETests.cs; everything else is `.planning/` docs. A `KeyDeleteAsync` grep across `src/Keeper` confirms exactly three call sites: DeleteConsumer (multi-key, the invariant's positive half), InjectConsumer:40 (the op being removed), and **L2ProbeRecovery:35 (the carve-out — scratch self-delete, NOT a keeper state)**.

**Primary recommendation:** Execute D-01 through D-10 verbatim. No re-derivation needed. The only real risk surface is the NSubstitute overload-binding quirk (the existing test already documents and handles it — copy that exact matcher shape) and ensuring the negative-guard fact does NOT accidentally assert against L2ProbeRecovery's scratch delete (it can't — L2ProbeRecovery isn't a keeper consumer and is never instantiated in the new fact).

## Verification Result: CONTEXT.md vs Live Code

| Reference in CONTEXT.md | Live code | Status |
|--------------------------|-----------|--------|
| `InjectConsumer.cs:39-40` delete op (op 3) | `:40` `Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.DeleteEntryId)), ct)`; `:39` is the comment | ✅ VERIFIED (op is one line `:40`; its `// 3) delete...` comment is `:39`) |
| InjectConsumer XML doc lines 10-16, Pitfall-5 "source delete is the tail" | doc spans `:10-16`, says "the source delete is the tail AFTER the confirmed send" + "(3) delete `L2[m.DeleteEntryId]`" | ✅ VERIFIED |
| `KeeperInject.cs:14` `DeleteEntryId` property | `:14 public Guid DeleteEntryId { get; init; }` | ✅ VERIFIED |
| KeeperInject XML doc lines 4-7 mentions DeleteEntryId | doc `:4-7` names `<see cref="DeleteEntryId"/> (source entryId deleted after the send)` | ✅ VERIFIED |
| `ProcessorPipeline.cs:430-438` BuildInject; drop line 437; comment 428-429 | `BuildInject` is `:430-438`; `DeleteEntryId = d.EntryId` is `:437`; INFRA-02 comment `:428-429` carries "`DeleteEntryId` = the source entryId" | ✅ VERIFIED |
| `InjectConsumerFacts.cs` line 35 `DeleteEntryId = Guid.NewGuid()`; delete-order chain `:57-74` | `:35` exactly; `Received.InOrder` `:57-63`, belt `:68`, `KeyDeleteAsync` Received `:73-74` | ✅ VERIFIED |
| `KeeperContractTests.cs:67-81` INJECT fact; positive name; Assert.Null negative | fact `:67-81`; method name `KeeperInject_carries_the_A18_id_set_EntryId_Data_DeleteEntryId` `:67`; class doc `:12-13`; deleteEntryId assert `:78-80` | ✅ VERIFIED |
| `PipelineForwardFacts.cs:149` `Assert.Equal(d.EntryId, inj.DeleteEntryId)` | `:149` exactly; NODROP-01 doc-comment naming `DeleteEntryId` is `:19` (not :19 in `<list>` — confirmed present) | ✅ VERIFIED |
| `SC2RecoveryPathsE2ETests.cs:42/179/204` | `:204` `DeleteEntryId = deleteEntryId`; doc comments `:42` and `:178-179`; seeding `deleteEntryId`/`deleteKey` `:188,193-196,212-214` | ✅ VERIFIED (the deletion assertion + seeding is `:188-214`, broader than the 3 cited lines — see Pitfall 4) |
| RecoveryTestKit `Db()`, `Mux()`, `Retry()`, `CapturingSendProvider` | `RecoveryTestKit.cs:58/49/35/79` — all present, `internal static` | ✅ VERIFIED |
| `L2ProbeRecovery.cs:35` scratch delete (carve-out) | `:35 await db.KeyDeleteAsync(scratch)` on `Guid.Empty`/`"bit"` scratch; not a `RecoveryConsumerBase` consumer | ✅ VERIFIED — correctly OUT of scope |
| spec §8 DELETE = only deleting state; INJECT = write + send | spec `:143-147` table confirms it | ✅ VERIFIED |

**Drift flags:** None. Two notes (not drift): (a) the delete op is a single source line `:40` with its comment at `:39` — CONTEXT.md's "delete the entire op-3 block at `:39-40`" is correct (delete comment + op together). (b) The SC2 E2E delete-assertion footprint is wider than the 3 cited lines — see Pitfall 4.

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| KINJ-01 | INJECT non-destructive — writes data key + sends result, deletes NO key (`delete L2[DeleteEntryId]` removed from InjectConsumer) | D-01: delete `InjectConsumer.cs:40` + its `:39` comment. The two surviving Guarded effects (`StringSetAsync` `:25`, `GetSendEndpoint`+`Send` `:36-37`) are confirmed present and unchanged. Enforced by D-04/D-05 negative-guard fact. |
| KINJ-02 | `KeeperInject.DeleteEntryId` removed; `BuildInject` no longer supplies it; `InjectConsumerFacts` + Phase-50 golden tests updated; 0-warning build | D-02 (`KeeperInject.cs:14`), D-03 (`ProcessorPipeline.cs:437`), D-06 (reflection guard `Assert.Null(GetProperty("DeleteEntryId"))`), D-07/D-08/D-09 (test updates), D-10 (Release+Debug 0-warning). Reflection guard makes re-adding the field a build failure. |
| KINJ-03 | DELETE is the ONLY keeper state that deletes — REINJECT and INJECT non-destructive — enforced by a negative-guard fact | D-04 (behavioral `DidNotReceive().KeyDeleteAsync`), D-05 (one dedicated invariant fact: DeleteConsumer DOES delete via `RedisKey[]` overload; InjectConsumer + ReinjectConsumer do NOT). `KeyDeleteAsync` grep confirms only DeleteConsumer + (removed) InjectConsumer:40 + (carved-out) L2ProbeRecovery:35 exist in `src/Keeper`. |
</phase_requirements>

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** `InjectConsumer.HandleAsync` keeps exactly two Guarded effects: (1) `Db.StringSetAsync(ExecutionData(m.EntryId), m.Data)`, then (2) resolve `queue:orchestrator-result` via `Guard` + `Send` the reconstructed `StepCompleted` carrying `m.EntryId`. Delete the entire op-3 block at `InjectConsumer.cs:39-40`. Update class XML doc (lines 10-16) to the two-effect body; drop the Pitfall-5 "source delete is the tail" rationale.
- **D-02:** Remove `DeleteEntryId` from `KeeperInject` (`:14`) + its XML-doc mention (lines 4-7). Record then carries 5-id base + `EntryId` (Guid) + `Data` (string).
- **D-03:** `ProcessorPipeline.BuildInject` (`:430-438`) constructs `KeeperInject` without `DeleteEntryId` (drop `:437`); update the INFRA-02/Pitfall-1 comment (`:428-429`) to drop the `DeleteEntryId = the source entryId` clause.
- **No wire migration:** `KeeperInject` is a default-STJ bus envelope (no `[JsonPropertyName]`). STJ ignores unknown properties on deserialize; in-flight pre-deploy messages deserialize harmlessly. Planner MUST NOT invent a versioning/migration step.
- **D-04 (guard style):** Prove "never deletes" behaviorally with NSubstitute — `db.DidNotReceive().KeyDeleteAsync(...)` against a substitute `IDatabase`. Not a reflection/IL scan, not a source-text scan.
- **D-05 (test layout):** Add one dedicated invariant fact file (name is Claude's discretion, e.g. `KeeperDeleteInvariantFacts.cs`): DeleteConsumer DOES delete (`db.Received(...).KeyDeleteAsync(<multi-key>)`, the `RedisKey[]` overload at `DeleteConsumer.cs:20`); InjectConsumer does NOT (`DidNotReceive().KeyDeleteAsync(...)` for every delete overload); ReinjectConsumer does NOT (same). Reuse `RecoveryTestKit` (`Db()`, `Mux()`, `Retry()`, `CapturingSendProvider`).
- **D-06:** Reshape the `KeeperContractTests` reflection test, keeping all-three coverage: rename `KeeperInject_carries_the_A18_id_set_EntryId_Data_DeleteEntryId` → reduced-id-set name (e.g. `KeeperInject_carries_the_reduced_id_set_EntryId_Data`); assert `EntryId` (Guid) + `Data` (string) present AND add `Assert.Null(typeof(KeeperInject).GetProperty("DeleteEntryId"))`; update class XML doc (lines 12-13) to "INJECT carries EntryId + Data"; leave REINJECT/DELETE facts unchanged.
- **D-07:** `InjectConsumerFacts.cs` — remove `DeleteEntryId = Guid.NewGuid()` (line 35); drop the `Received.InOrder(write < KeyDelete)` chain and the `db.Received(1).KeyDeleteAsync(...)` assertion (lines 57-74); the order check now locks write-then-send only; add a `db.DidNotReceive().KeyDeleteAsync(...)` belt. Update class XML doc.
- **D-08:** `PipelineForwardFacts.cs` — delete `Assert.Equal(d.EntryId, inj.DeleteEntryId)` (line 149); adjust the NODROP-01 doc comment (line 19) to drop `DeleteEntryId`.
- **D-09:** `SC2RecoveryPathsE2ETests.cs` — remove `DeleteEntryId = deleteEntryId` (line 204), remove the "delete `L2[m.DeleteEntryId]`" assertion and any `deleteEntryId` seeding, update doc comments (lines 42, 179). The E2E now asserts INJECT writes the data key + sends `StepCompleted` and deletes nothing.
- **D-10:** Solution builds 0-warning in both Release and Debug; full `BaseApi.Tests` suite stays green.

### Claude's Discretion
- Exact filename/namespace of the new dedicated invariant fact (D-05).
- Exact wording of rewritten XML doc comments.
- How to express the surviving write-then-send order in `InjectConsumerFacts` now that the third op is gone.

### Deferred Ideas (OUT OF SCOPE)
- **INJECT index-slot write (spec §8 divergence):** canonical §8 says INJECT also writes the index slot `L2[messageId][x]=outputEntryId` atomically. Current `InjectConsumer` writes only the data key; ROADMAP scopes Phase 70 to "exactly two effects." Note as an observed gap; do NOT implement here.
- **`L2ProbeRecovery.cs:35` scratch delete:** a net-zero probe scratch `KeyDeleteAsync`, NOT a keeper recovery state. Explicitly OUTSIDE the "DELETE-only-deletes" invariant; must not be touched or asserted against by the guard.
</user_constraints>

## Project Constraints (from CLAUDE.md + skills)

No root `CLAUDE.md` exists (verified). Constraints come from auto-memory + project conventions:

- **xunit.v3 / MTP (Microsoft.Testing.Platform):** `BaseApi.Tests` uses xunit.v3. `dotnet test --filter` is **silently ignored** (runs all ~638). To scope a run use `-- --filter-method`. Example: `dotnet test tests/BaseApi.Tests -- --filter-method "*KeeperDeleteInvariant*"`. (auto-memory: MTP filter syntax)
- **0-warning gate (D-10):** the solution must build 0-warning in **both** Release and Debug. Run both configs.
- **No new scope:** user owns the spec; analyze + refactor, never add scope (auto-memory: design-iteration-style). Phase 70 adds no capability — INJECT loses one op, the contract loses one field.
- **RealStack tests are excluded hermetically:** `SC2RecoveryPathsE2ETests` is `[Trait("Category","RealStack")]` and excluded by the `Category!=RealStack` hermetic filter. Its source must still **compile** (D-09 keeps it building) but it is NOT exercised in the hermetic gate — it runs only in the operator-gated live run.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Keeper INJECT recovery state | Keeper console (out-of-band recovery worker) | Redis L2 / RabbitMQ | INJECT writes the recovered data key + sends the completed result; it is a recovery consumer, not part of the synchronous processor path. |
| KeeperInject contract shape | Messaging.Contracts (shared bus envelope) | — | The record is the wire contract between processor (producer, `BuildInject`) and keeper (consumer, `InjectConsumer`). |
| INJECT escalation construction | BaseProcessor.Core pipeline (`BuildInject`) | — | The processor mints the `KeeperInject` on atomic-write exhaust; it must stop supplying the dropped field. |
| Delete invariant enforcement | Test tier (`BaseApi.Tests`) | — | A behavioral NSubstitute fact + a reflection guard; no runtime code owns the invariant — it's locked by tests. |

## Standard Stack

This is a refactor inside an established codebase. No new dependencies. Verified in-tree:

| Library | Version | Purpose | Notes |
|---------|---------|---------|-------|
| StackExchange.Redis | 2.13.1 | `IDatabase` ops (`StringSetAsync`, `KeyDeleteAsync`) | The overload-binding quirk below is version-specific to 2.13.1 |
| NSubstitute | (in-tree) | `Substitute.For<IDatabase>()`, `Received`/`DidNotReceive`/`Received.InOrder` | Behavioral assertion idiom (D-04) |
| xunit.v3 + Microsoft.Testing.Platform | (in-tree) | `[Fact]`, `TestContext.Current.CancellationToken`, `--filter-method` | NOT classic xunit — see Project Constraints |
| MassTransit | (in-tree) | `ConsumeContext<T>`, `ISendEndpointProvider` | Faked by `RecoveryTestKit.CapturingSendProvider` |

**Installation:** none. Do not add packages.

### Reusable Assets (verified present)
| Asset | Location | Role in Phase 70 |
|-------|----------|------------------|
| `RecoveryTestKit.Db()` | `RecoveryTestKit.cs:58` | Substitute `IDatabase`; stubs both `KeyDeleteAsync(RedisKey,...)` (`:71`) and `KeyDeleteAsync(RedisKey[],...)` (`:72`) so `Received`/`DidNotReceive` work for both delete overloads |
| `RecoveryTestKit.Mux(db)` | `RecoveryTestKit.cs:49` | Substitute `IConnectionMultiplexer` returning the db |
| `RecoveryTestKit.Retry()` | `RecoveryTestKit.cs:35` | `IOptions<RetryOptions>` (Limit=3) |
| `RecoveryTestKit.CapturingSendProvider` | `RecoveryTestKit.cs:79` | Records `(Uri, Message)` sends; captures `StepCompleted` + `EntryStepDispatch` |
| `DispatchTestKit.CapturingSendProvider` / `.SentKeeper` | `DispatchTestKit.cs` (used by `PipelineForwardFacts`) | Captures `IKeeperRecoverable` sends to `SentKeeper` — used by D-08 fact (unchanged, only the `inj.DeleteEntryId` assertion is removed) |

## Architecture Patterns

### The INJECT consumer body (after D-01)
```csharp
// Source: InjectConsumer.cs (verified live), after removing op 3
protected override async Task HandleAsync(KeeperInject m, CancellationToken ct)
{
    // 1) write L2[entryId] = data (data in-hand — forward-only, NO presence read)
    await Guard(() => Db.StringSetAsync(L2ProjectionKeys.ExecutionData(m.EntryId), m.Data), ct);

    // 2) send StepCompleted → orchestrator result queue (A15)
    var completed = new StepCompleted(m.WorkflowId, m.StepId, m.ProcessorId)
    {
        CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId, EntryId = m.EntryId,
    };
    var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}")), ct);
    await Guard(() => ep.Send(completed, CancellationToken.None), ct);
    // op 3 (KeyDeleteAsync) REMOVED — INJECT is now non-destructive (KINJ-01)
}
```
The Guarded structure of ops 1-2 is untouched (CONTEXT.md established pattern). Removing op 3 must not disturb it.

### Pattern: behavioral negative-guard (D-04/D-05)
```csharp
// Source: idiom established in InjectConsumerFacts.cs:73 (Received) + RecoveryTestKit
var db = RecoveryTestKit.Db();
var consumer = new InjectConsumer(RecoveryTestKit.Mux(db), new RecoveryTestKit.CapturingSendProvider(), RecoveryTestKit.Retry());
// ... build KeeperInject, fake ConsumeContext, Consume ...

// assert NO delete on EITHER overload (single-key and multi-key):
await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(),   Arg.Any<CommandFlags>());
await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
```

### Pattern: positive half — DELETE DOES delete
```csharp
// Source: DeleteConsumer.cs:20 uses the RedisKey[] (multi-key) overload
await db.Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
```

### Anti-Patterns to Avoid
- **Reflection/IL/source-text scan for the "never deletes" proof.** D-04 explicitly chose behavioral NSubstitute for consistency with the existing `*ConsumerFacts`. Do not introduce a Roslyn/regex scan.
- **Instantiating or asserting against `L2ProbeRecovery` in the invariant fact.** It is not a `RecoveryConsumerBase` keeper state and is explicitly carved out. The new fact only constructs `DeleteConsumer`, `InjectConsumer`, `ReinjectConsumer`.
- **Adding a wire-version/migration step for the dropped field.** Default-STJ tolerates the unknown property; D-03 note forbids it.
- **Asserting an index-slot write in INJECT.** That's the deferred §8 divergence — out of scope.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Substitute Redis db | A custom fake | `RecoveryTestKit.Db()` | Already stubs both delete overloads + StringGet/Set so Received works |
| Capture sends | A custom `ISendEndpointProvider` | `RecoveryTestKit.CapturingSendProvider` | Captures `StepCompleted`/`EntryStepDispatch` with URIs |
| Multiplexer fake | Hand-rolled mux | `RecoveryTestKit.Mux(db)` | One-liner; returns db for any GetDatabase call |
| "Never deletes" proof | IL/source scanner | NSubstitute `DidNotReceive()` | D-04 locked behavioral; runs the real consumer body |

## Runtime State Inventory

Rename/refactor phase. Field-drop + op-removal — but the change is **wire-tolerant and stateless**, so most categories are empty by design.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None affecting this phase. The L2 keys INJECT touches (`skp:data:{entryId}`) are unchanged in shape; only the *behavior* of deleting `L2[deleteEntryId]` stops. No stored value encodes `DeleteEntryId`. | None — verified by inspecting `L2ProjectionKeys.ExecutionData` usage. |
| Live service config | None. No queue/endpoint/partitioner change (KeeperInject still binds the same `keeper-recovery` endpoint). | None. |
| OS-registered state | None — pure code change. | None. |
| Secrets/env vars | None reference `DeleteEntryId`. | None. |
| Build artifacts | Standard `bin/obj` rebuild on next build; no egg-info-style stale artifact. | Rebuild (implicit in D-10 dual-config build). |
| **In-flight bus messages** | A pre-deploy `KeeperInject` carrying `DeleteEntryId` may be on the broker at deploy. | **None (by design):** default-STJ drops the unknown property on deserialize; INJECT no longer needs it. D-03 forbids a migration step. |

**Canonical question — "after every file is updated, what runtime systems still have the old string?":** Only in-flight broker envelopes, which are handled transparently by STJ unknown-property tolerance. Nothing else persists `DeleteEntryId`.

## Common Pitfalls

### Pitfall 1: NSubstitute overload binding — SE.Redis 2.13.1 `StringSetAsync` (HIGH relevance)
**What goes wrong:** A naive `db.Received(1).StringSetAsync(key, value)` won't match. SE.Redis 2.13.1 binds the consumer's **2-arg** `StringSetAsync(key, value)` call to the **`Expiration`/`ValueCondition` overload**, not the `TimeSpan?`/`When` overload that `RecoveryTestKit.Db()` stubs.
**Why it happens:** Overload resolution at the substitute boundary; documented inline in `InjectConsumerFacts.cs:54-56` and `:70-72`.
**How to avoid:** Match the existing exact shape:
```csharp
await db.Received(1).StringSetAsync(
    (RedisKey)L2ProjectionKeys.ExecutionData(m.EntryId), (RedisValue)m.Data,
    Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
```
**Warning signs:** A `Received(1)` on `StringSetAsync` fails with "actually received 0 matching calls" while the consumer clearly wrote the key.
**Carry-over:** In `InjectConsumerFacts` after D-07, if you keep a write-Received check, reuse this 5-arg shape. In the new invariant fact, you likely only assert deletes — so this quirk only bites if you also assert the write there.

### Pitfall 2: KeyDeleteAsync has TWO overloads — assert BOTH in the negative guard (MEDIUM)
**What goes wrong:** `db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), ...)` only covers the single-key overload. A future refactor that reintroduces a multi-key delete in InjectConsumer would slip past.
**How to avoid:** In the InjectConsumer/ReinjectConsumer negative assertions, cover **both**: `KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())` **and** `KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())`. `RecoveryTestKit.Db()` stubs both (`:71`/`:72`), so both are assertable.
**Warning signs:** The guard passes even though only one overload was checked.

### Pitfall 3: Carve-out — do NOT let the invariant catch L2ProbeRecovery's scratch delete (MEDIUM)
**What goes wrong:** A too-broad invariant ("nothing in Keeper deletes except DeleteConsumer") implemented as a type/IL scan would flag `L2ProbeRecovery.cs:35`.
**Why it's a non-issue with D-04/D-05:** The behavioral fact only **instantiates and Consumes** the three `RecoveryConsumerBase` keeper states. `L2ProbeRecovery` is never constructed in the fact, so its scratch delete is structurally outside the assertion. This is exactly why D-04 chose behavioral over IL-scan.
**How to avoid:** Keep the fact behavioral and scoped to the three consumers. Do NOT add any cross-type "no KeyDelete anywhere in Keeper.dll" assertion.
**Warning signs:** Any reference to `L2ProbeRecovery` or `Keeper.dll` type enumeration appearing in the new fact file.

### Pitfall 4: SC2 E2E delete footprint is wider than the 3 cited lines (MEDIUM)
**What goes wrong:** D-09 cites lines 42/179/204, but the INJECT block (`SC2RecoveryPathsE2ETests.cs:181-215`) seeds a `deleteEntryId` (`:188`), builds `deleteKey` and registers it (`:193,196`), sets `DeleteEntryId = deleteEntryId` (`:204`), and **asserts the source key is deleted** (`:212-214 PollForKeyAbsentAsync(db, deleteKey)`). Removing only line 204 leaves a dangling `deleteEntryId`/`deleteKey` and a now-false delete assertion.
**How to avoid:** Remove the whole delete half of that block: the `deleteEntryId` local (`:188`), `deleteKey` (`:193`), its `L2KeysToCleanup.Add(deleteKey)` (`:196`), the `db.StringSetAsync(deleteKey, ...)` seed (`:194`), `DeleteEntryId = deleteEntryId` (`:204`), and the `sourceDeleted` assertion (`:212-214`). Keep the write assertion (`:209-211`). Update doc comments `:42` and `:178-179` to "writes the data key + sends StepCompleted, deletes nothing." This is consistent with D-09's intent ("remove ... any `deleteEntryId` seeding") — the planner must treat D-09 as covering the full block, not literally three lines.
**Warning signs:** Compile errors on unused `deleteEntryId`, or a still-present `PollForKeyAbsentAsync(db, deleteKey, ...)` that can never go true (nothing deletes it now).

### Pitfall 5: InjectConsumerFacts order-check collapses to write-then-send (LOW)
**What goes wrong:** The current `Received.InOrder` chains `StringSetAsync < KeyDeleteAsync` (`:57-63`). With the delete gone there are only Redis-write + a captured send. NSubstitute `Received.InOrder` only sees the **db substitute's** calls, so a two-element InOrder is no longer meaningful (only one db call: the write).
**How to avoid (D's discretion):** Drop the InOrder entirely; assert (a) `db.Received(1).StringSetAsync(...)` (5-arg shape, Pitfall 1), (b) `Assert.Single(send.Sent)` is the `StepCompleted`, and (c) the `db.DidNotReceive().KeyDeleteAsync(...)` belt (both overloads). The write-before-send ordering is implicit in the consumer body; the captured single send + the write-Received is sufficient.
**Warning signs:** A surviving single-element `Received.InOrder` (no longer guards anything).

### Pitfall 6: PipelineForwardFacts doc comment also names DeleteEntryId (LOW)
`PipelineForwardFacts.cs:19` (the `<list>` item for NODROP-01) reads "ONE `KeeperInject` carrying Data/DeleteEntryId/EntryId". D-08 says adjust line 19. Remove `/DeleteEntryId` from that sentence so the XML doc compiles cleanly and stays truthful. The assertion removal is `:149`.

## Validation Architecture

> nyquist_validation = true (config.json:19). Section REQUIRED.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 + Microsoft.Testing.Platform (MTP) |
| Config file | per-project (`tests/BaseApi.Tests`); MTP runner |
| Quick run command | `dotnet test tests/BaseApi.Tests -- --filter-method "*KeeperDeleteInvariant*"` (scope to the new fact; `--filter-method`, NOT `--filter`) |
| Scoped INJECT/contract run | `dotnet test tests/BaseApi.Tests -- --filter-method "*Inject*"` plus `"*KeeperInject*"` |
| Full suite command | `dotnet test tests/BaseApi.Tests` (hermetic; `Category!=RealStack` excludes E2E automatically) |
| Build gate (D-10) | `dotnet build -c Release -warnaserror` AND `dotnet build -c Debug -warnaserror` (0-warning both) |

### What each success criterion asserts + its sampling strategy

| Req | Behavior asserted | Test type | Automated command | Negative-guard / false-positive sampling |
|-----|-------------------|-----------|-------------------|-------------------------------------------|
| KINJ-01 | InjectConsumer Consumes → writes `L2[EntryId]`, sends one `StepCompleted` to `queue:orchestrator-result`, deletes **nothing** | unit (behavioral NSub) | `... --filter-method "*InjectConsumer*"` + the new invariant fact | **Negative guard:** `db.DidNotReceive().KeyDeleteAsync(...)` on **both** overloads. **False-positive risk:** the test passes because the consumer threw before reaching any op — mitigate by also asserting `db.Received(1).StringSetAsync(...)` and `Assert.Single(send.Sent)` so the body provably ran to completion. |
| KINJ-02 | `KeeperInject` has no `DeleteEntryId`; `BuildInject` omits it; build is 0-warning | unit (reflection) + compile + build | `... --filter-method "*KeeperInject*"` then dual-config `-warnaserror` build | **Negative guard:** `Assert.Null(typeof(KeeperInject).GetProperty("DeleteEntryId"))` — re-adding the field fails the test AND every consumer/test reference fails to compile. **False-positive risk:** build/types pass but a stray `DeleteEntryId` lingers in a doc/comment (harmless) — the reflection Assert.Null + the repo grep (now 0 code hits) is the belt. |
| KINJ-03 | DELETE deletes (multi-key); INJECT + REINJECT do not | unit (behavioral NSub, one fact file) | `... --filter-method "*KeeperDeleteInvariant*"` | **Positive sample:** `db.Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), ...)` after running DeleteConsumer. **Negative samples:** `DidNotReceive().KeyDeleteAsync(...)` (both overloads) after running InjectConsumer and ReinjectConsumer separately. **False-positive risk:** the invariant fact "passes" because a consumer silently no-op'd (e.g. gate not open). Mitigate by asserting the **positive side-effect** each consumer DOES produce (InjectConsumer: a captured send; DeleteConsumer: the Received delete) so a no-op consumer fails. **Carve-out guard:** L2ProbeRecovery is never instantiated in this fact, so its `:35` scratch delete cannot register a false `Received`. |

### The "build/types green while a delete still occurs" false-positive — the load-bearing concern
The objective flags this explicitly. Defenses, in layers:
1. **Behavioral, not structural:** D-04/D-05 run the *actual* `HandleAsync` body against a substitute and assert on the calls it made. A delete that still occurs at runtime → `DidNotReceive` fails. (A type/build check alone would miss this — hence behavioral.)
2. **Both delete overloads** asserted (Pitfall 2) so a reintroduced multi-key delete can't slip through.
3. **Positive-effect co-assertion** so a short-circuited (no-op) consumer doesn't trivially satisfy `DidNotReceive`.
4. **Reflection Assert.Null** so re-adding the field breaks compilation across producers/consumers, not just one test.

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| KINJ-01 | INJECT non-destructive | unit | `dotnet test tests/BaseApi.Tests -- --filter-method "*InjectConsumer*"` | ✅ `InjectConsumerFacts.cs` (D-07 edits) + ❌ new invariant fact (Wave 0, D-05) |
| KINJ-02 | field dropped, 0-warning | unit + build | `... --filter-method "*KeeperInject*"`; `dotnet build -c Release -warnaserror`; `-c Debug` | ✅ `KeeperContractTests.cs` (D-06 edits) |
| KINJ-03 | DELETE-only-deletes invariant | unit | `... --filter-method "*KeeperDeleteInvariant*"` | ❌ new fact (Wave 0, D-05) |

### Sampling Rate
- **Per task commit:** the scoped `--filter-method` run for the file touched.
- **Per wave merge:** full `dotnet test tests/BaseApi.Tests` (hermetic) + dual-config `-warnaserror` build.
- **Phase gate:** full suite green + 0-warning Release & Debug before `/gsd-verify-work`. The RealStack `SC2RecoveryPathsE2ETests` is NOT run hermetically — it only needs to **compile** (D-09); live proof is operator-gated.

### Wave 0 Gaps
- [ ] New dedicated invariant fact file (D-05), e.g. `tests/BaseApi.Tests/Keeper/KeeperDeleteInvariantFacts.cs` — covers KINJ-03 (DeleteConsumer deletes; InjectConsumer + ReinjectConsumer do not). Reuses `RecoveryTestKit`; no new fixtures.
- No framework install needed — xunit.v3/MTP + NSubstitute already present.
- No `conftest`-style shared fixture needed — `RecoveryTestKit` is the shared harness.

## Security Domain

> security_enforcement not set to false; included for completeness. This is an internal recovery-worker refactor with no auth/session/input-validation surface change.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | unchanged — no auth surface touched |
| V3 Session Management | no | n/a |
| V4 Access Control | no | n/a |
| V5 Input Validation | no | `KeeperInject` is an internal bus envelope; dropping a field reduces, not expands, the deserialized surface. STJ unknown-property tolerance is the existing posture. |
| V6 Cryptography | no | none |

### Known Threat Patterns for this stack
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Result loss by deleting source before send | Tampering / Repudiation (data loss) | **This phase removes the destructive op entirely** — the historical Pitfall-5 "delete after confirmed send" rationale is moot once nothing deletes. DELETE remains the sole, out-of-band, atomic two-key reclaimer (DeleteConsumer). |
| Stale field re-introduced silently | Tampering | Reflection `Assert.Null` guard (D-06) fails the build if `DeleteEntryId` returns. |

**Net security effect:** strictly non-negative — one destructive Redis op is removed, narrowing the data-loss window the original Pitfall-5 ordering existed to defend.

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| INJECT = write + send + delete-source (3 ops, strict order) | INJECT = write + send (2 ops, non-destructive) | Phase 70 | Source-delete redundant since Phase 69 made the processor skip its cleanup tail when an item escalates (spec §4.3 final ¶). |
| KeeperInject carries DeleteEntryId | KeeperInject carries EntryId + Data only | Phase 70 | Contract narrows; wire-tolerant via default STJ. |
| Delete-invariant documented only | Delete-invariant enforced by a behavioral fact | Phase 70 | "DELETE is the only deleting keeper state" becomes a build-breaking guarantee. |

**Deprecated/outdated:** the `InjectConsumer` op-3 source-delete and the `KeeperInject.DeleteEntryId` field — both removed this phase.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The SC2 E2E delete-half (`:188-214`) should be removed in full under D-09, not just literal line 204 | Pitfall 4 / D-09 | If planner removes only `:204`, the file won't compile (unused `deleteEntryId`) and `PollForKeyAbsentAsync(deleteKey)` becomes a permanently-failing assertion. **This is a code-grounded inference, not a CONTEXT.md verbatim — flag to planner.** Low risk: the intent ("remove any deleteEntryId seeding") clearly covers it. |
| A2 | `DispatchTestKit.SentKeeper`/`CapturingSendProvider` are unchanged by Phase 70 (only the `inj.DeleteEntryId` assertion is dropped) | Don't Hand-Roll / D-08 | None expected — the harness captures `IKeeperRecoverable`; the field drop doesn't affect capture. |

**Everything else verified or cited.** No `[ASSUMED]` claims about library behavior — the NSubstitute overload quirk and STJ tolerance are both confirmed in-tree.

## Open Questions

1. **Exact name/namespace of the new invariant fact (D-05, Claude's discretion).**
   - What we know: it lives in `tests/BaseApi.Tests/Keeper/`, reuses `RecoveryTestKit`, namespace `BaseApi.Tests.Keeper`.
   - Recommendation: `KeeperDeleteInvariantFacts.cs`, class `KeeperDeleteInvariantFacts`, three `[Fact]`s (DeleteConsumer_deletes_both_keys, InjectConsumer_never_deletes, ReinjectConsumer_never_deletes). `[Trait("Phase","70")]`.

2. **Does the planner treat D-09's "lines 42, 179, 204" literally or as "the INJECT delete-half block"?**
   - What we know: a literal 3-line removal won't compile (Pitfall 4 / A1).
   - Recommendation: plan the full block removal `:188,193,194,196,204,212-214` + doc comments `:42,178-179`.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK 8 | build + hermetic tests | assumed ✓ (existing project builds) | — | — |
| docker / RealStack (RMQ, Redis, Postgres) | `SC2RecoveryPathsE2ETests` ONLY | not required for Phase 70 hermetic gate | — | E2E excluded by `Category!=RealStack`; it only needs to compile (D-09). Live proof is operator-gated, out of this phase's automated gate. |

**Missing dependencies with no fallback:** none — the phase's automated gate is hermetic (NSubstitute, no live Redis/broker).
**Missing dependencies with fallback:** RealStack — not needed; E2E compile-only here.

## Sources

### Primary (HIGH confidence — verified in-tree this session)
- `src/Keeper/Recovery/InjectConsumer.cs` (op `:40`, doc `:10-16`), `ReinjectConsumer.cs`, `DeleteConsumer.cs:20`, `L2ProbeRecovery.cs:35`
- `src/Messaging.Contracts/KeeperInject.cs:14`
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:428-438`
- `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` (`:35,:54-74`), `RecoveryTestKit.cs` (`:35,49,58,71-72,79`)
- `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs:67-81`
- `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs:19,149` (+ `DispatchTestKit.SentKeeper`)
- `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs:42,178-215`
- `docs/design/processor-keeper-recovery-spec.md:73-101 (§4.3), :137-147 (§8)`
- `.planning/config.json` (nyquist_validation=true), `.planning/REQUIREMENTS.md:17-19`
- Repo-wide grep: `DeleteEntryId` → 7 code files (all CONTEXT.md-named); `KeyDeleteAsync` in `src/Keeper` → DeleteConsumer + InjectConsumer:40 + L2ProbeRecovery:35

### Secondary (MEDIUM)
- Auto-memory: MTP filter syntax (`-- --filter-method`), close-gate net-zero protocol, design-iteration-style.
- `.planning/phases/69-.../69-RESEARCH.md` (DispatchTestKit/SentKeeper context, NODROP-01 lineage).

### Tertiary (LOW)
- None required — all claims verified or cited.

## Metadata

**Confidence breakdown:**
- CONTEXT.md verification: HIGH — every line reference confirmed against live source.
- Standard stack: HIGH — no new deps; in-tree versions read directly.
- Architecture / patterns: HIGH — copied from verified existing facts.
- Pitfalls: HIGH (Pitfalls 1-3) / MEDIUM (Pitfall 4, an inference flagged in Assumptions Log A1).
- Validation architecture: HIGH — grounded in the existing test files + config.

**Research date:** 2026-06-16
**Valid until:** stable — internal refactor; valid until the touched files change (re-verify line numbers if Phase 70 is deferred past further edits to these 7 files).

## RESEARCH COMPLETE

**Phase:** 70 - Processor INJECT Cleanup
**Confidence:** HIGH

### Key Findings
1. **Zero drift** — all 7 code touch-points and every CONTEXT.md line reference verified against live source. Decisions D-01..D-10 are executable verbatim.
2. **Carve-out is structurally safe** — the behavioral D-04/D-05 fact never instantiates `L2ProbeRecovery`, so its `:35` scratch delete cannot trigger a false `Received`. Keep the fact behavioral (no IL/type scan) and it stays clean.
3. **NSubstitute overload quirk is real and already solved in-tree** — SE.Redis 2.13.1 binds 2-arg `StringSetAsync` to the `Expiration/ValueCondition` overload; reuse the existing 5-arg matcher shape. And `KeyDeleteAsync` has two overloads — assert `DidNotReceive` on both.
4. **One inference flagged (A1/Pitfall 4):** D-09's "lines 42/179/204" must be read as the whole INJECT delete-half block (`:188-214`) or the E2E won't compile. Planner should plan the full block removal.
5. **False-positive defense for KINJ-03:** layer behavioral negative-guard + both delete overloads + positive side-effect co-assertion + reflection `Assert.Null`, so "build green while a delete still occurs" cannot pass.

### File Created
`C:\Users\UserL\source\repos\SK_P4\.planning\phases\70-processor-inject-cleanup\70-RESEARCH.md`

### Ready for Planning
Research complete. The planner can author plans directly from D-01..D-10 with the Validation Architecture and Pitfall 4 (full E2E block removal) as the two attention points.
