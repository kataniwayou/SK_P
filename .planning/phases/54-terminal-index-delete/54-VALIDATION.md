---
phase: 54
slug: terminal-index-delete
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-12
---

# Phase 54 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `54-RESEARCH.md` → "## Validation Architecture". Hermetic-only phase
> (NSubstitute fakes Redis); the live/real-stack proof + close-gate net-zero is Phase 55.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (solution-pinned) + NSubstitute |
| **Config file** | none — facts are plain `[Fact]` classes under `tests/BaseApi.Tests/` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~PipelineEndDeleteFacts|FullyQualifiedName~PipelineRecoveryFacts|FullyQualifiedName~DeleteConsumerFacts"` |
| **Full suite command** | `dotnet test` (solution root) |
| **Build gate** | `dotnet build -c Release` AND `dotnet build -c Debug` — 0 warnings (SPEC Constraints) |
| **Estimated runtime** | ~quick: a few seconds (3 fact files) · full suite: project-dependent |

---

## Sampling Rate

- **After every task commit:** Run the **quick run command** (the 3 fact files above).
- **After every plan wave:** Run the **full suite command** (`dotnet test`).
- **Before `/gsd-verify-work`:** Full suite green AND `dotnet build -c Release` + `-c Debug` both 0-warning.
- **Max feedback latency:** ~quick-run seconds per commit.

---

## Per-Requirement Verification Map

> Task IDs are bound to plans by the planner (this strategy predates the plan). The
> authoritative signal per requirement is the named fact + observable assertion below;
> the plan-checker enforces that every fact has an `<automated>` verify hook.

| Requirement | AC | Behavior | File · Fact (action) | Observable signal asserted | Test Type | Automated Command | Status |
|-------------|----|----------|----------------------|-----------------------------|-----------|-------------------|--------|
| GC-01 | AC-1 | Forward happy tail = ONE multi-key DEL | `PipelineEndDeleteFacts.EndDelete_RunsOnHappyPath` (INVERT) | `Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(ks => ExecutionData(entryId) && MessageIndex(messageId)))` AND `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), …)` AND no `KeeperDelete` sent | unit/fact | quick run | ⬜ pending |
| GC-01 | — | Business-fail tail = same multi-key DEL | `PipelineEndDeleteFacts.EndDelete_RunsOnBusinessFail` (UPDATE) | `Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` | unit/fact | quick run | ⬜ pending |
| GC-01 | — | In-exception tail = same multi-key DEL | `PipelineEndDeleteFacts.EndDelete_RunsOnInException` (UPDATE) | `Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` | unit/fact | quick run | ⬜ pending |
| GC-01 | AC-2 | Recovery all-clear tail = same multi-key DEL | `PipelineRecoveryFacts.AllClear_DeletesSource` (INVERT scalar→array) | `Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(ks => ExecutionData(d.EntryId) && MessageIndex(messageId)))` AND no `KeeperReinject` sent | unit/fact | quick run | ⬜ pending |
| GC-01 | AC-3 | Source step: index deleted, `Guid.Empty` data operand no-ops, no throw | `PipelineEndDeleteFacts.EndDelete_Skipped_OnSourceStep` (INVERT → now DELETES index) | `Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(ks => MessageIndex(messageId) && ExecutionData(Guid.Empty)))` AND test completes (no exception) | unit/fact | quick run | ⬜ pending |
| GC-02 | AC-4 | Forward read-exhaust REINJECT: NEITHER key deleted | `PipelineEndDeleteFacts.EndDelete_Skipped_OnReinject` (KEEP + add array `DidNotReceive`) | `Single(SentKeeper.OfType<KeeperReinject>())` AND `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` + scalar `DidNotReceive` | unit/fact | quick run | ⬜ pending |
| GC-02 | AC-4 | Recovery anyInfra REINJECT: NEITHER key deleted, index survives | `PipelineRecoveryFacts.MixedSlots_…NoSourceDelete` (KEEP + add array `DidNotReceive`) | `Single(OfType<KeeperReinject>())` AND `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` (index survives = no DEL of `MessageIndex`) | unit/fact | quick run | ⬜ pending |
| GC-02 | — | HGETALL-exhaust REINJECT: no delete | `PipelineRecoveryFacts.HGetAllFault_Reinject_NoSourceDelete` (KEEP + add array `DidNotReceive`) | `Single(OfType<KeeperReinject>())` AND `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` | unit/fact | quick run | ⬜ pending |
| GC-03 | AC-5 | Tail DEL exhaust → `KeyPersistAsync(MessageIndex)` THEN `KeeperDelete` carrying MessageId | `PipelineEndDeleteFacts.EndDelete_Exhaust_Delete` (UPDATE: add persist + MessageId asserts) | `Received(1).KeyPersistAsync((RedisKey)MessageIndex(messageId), …)` AND `Single(OfType<KeeperDelete>())` whose `.MessageId == messageId` | unit/fact | quick run | ⬜ pending |
| GC-03 | — | Persist-exhaust still escalates (best-effort fall-through, D-03) | `PipelineEndDeleteFacts.EndDelete_PersistExhaust_StillSendsKeeper` (NEW) | with array DEL **and** `KeyPersistAsync` both throwing: `Single(OfType<KeeperDelete>())` (keeper sent despite persist failure) | unit/fact | quick run | ⬜ pending |
| GC-03 | AC-6 | `KeeperDelete` exposes `MessageId`; `BuildDelete` stamps it | covered by AC-5 fact's `.MessageId == messageId` (+ compile-time: contract has the prop) | `KeeperDelete.MessageId` populated from inbound messageId | unit/fact + compile | quick run / build | ⬜ pending |
| GC-03 | AC-7 | `DeleteConsumer` = ONE multi-key DEL of both keys | `DeleteConsumerFacts.Delete_deletes_execution_data_key` (INVERT scalar→array) | `Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(ks => ExecutionData(m.EntryId) && MessageIndex(m.MessageId)), …)` | unit/fact | quick run | ⬜ pending |
| GC-03 | AC-7 | Keeper drop-on-absent (no throw) | `DeleteConsumerFacts.Delete_absent_key_no_throws` (UPDATE: stub array overload `.Returns(0L)`) | array DEL returns 0; `Consume` completes; `Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` | unit/fact | quick run | ⬜ pending |
| (regression) | AC-8 | Per-slot TTL writes remain (`:165`/`:275`) | existing forward/recovery facts still pass with `KeyExpireAsync` stubbed; optional `Received().KeyExpireAsync(MessageIndex(messageId), …)` in a forward-happy fact | crash backstop preserved (D-07) | unit/fact | quick run | ⬜ pending |
| (negative) | AC-9 | No new metric series | no new `metrics.*.Add` site; no test asserts a new counter | observability frozen (D-08) | review | n/a | ⬜ pending |
| (gate) | AC-10 | 0-warning Release+Debug; full suite green | build + `dotnet test` | — | build + suite | build gate | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

The hermetic infrastructure exists; the only NEW test scaffolding is the array-overload mock and one new fault mux (RESEARCH "Wave 0 Gaps"):

- [ ] `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` — add `KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())` stub to every success mux (`PresentReadWriteDeleteOkL2`, `ForwardOkL2`, `RecoveryL2`, `RecoveryAllCompletedL2`); add a `When/Do` throw on the **array** overload to fault muxes (`ReadOkDeleteFaultL2`, `ForwardDeleteFaultL2`); add a `KeyPersistAsync(Arg.Any<RedisKey>(), …).Returns(true)` stub.
- [ ] `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs` — add `KeyDeleteAsync(Arg.Any<RedisKey[]>(), …).Returns(2L)` (and a `.Returns(0L)` variant for the drop-on-absent fact).
- [ ] NEW fault mux (sibling of `ReadOkDeleteFaultL2`, e.g. `ReadOkDeleteAndPersistFaultL2`) where BOTH the array DEL and `KeyPersistAsync` throw — backs `EndDelete_PersistExhaust_StillSendsKeeper`.

*No new fact FILE — D-04 mandates in-place edits.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live close-gate net-zero (`skp:msg:*` count returns to baseline) | TEST-01/02 | Requires real `sk-redis` + full stack; out of scope this phase | Deferred to **Phase 55** (build-before-proof split) |

*All Phase-54 (A19 behavior) requirements have automated hermetic verification.*

---

## Validation Sign-Off

- [ ] All requirement rows have a named fact with an automated quick-run command
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers the array-overload mock + persist-exhaust fault mux (MISSING references)
- [ ] No watch-mode flags
- [ ] Atomicity asserted as ONE array call + `DidNotReceive()` scalar (GC-01 heart) on every delete fact
- [ ] Feedback latency < quick-run seconds
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
