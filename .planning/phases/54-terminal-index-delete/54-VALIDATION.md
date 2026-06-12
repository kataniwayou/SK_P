---
phase: 54
slug: terminal-index-delete
status: verified
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-12
validated: 2026-06-12
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
| GC-01 | AC-1 | Forward happy tail = ONE multi-key DEL | `PipelineEndDeleteFacts.EndDelete_RunsOnHappyPath` (INVERT) | `Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(ks => ExecutionData(entryId) && MessageIndex(messageId)))` AND `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), …)` AND no `KeeperDelete` sent | unit/fact | quick run | ✅ green |
| GC-01 | — | Business-fail tail = same multi-key DEL | `PipelineEndDeleteFacts.EndDelete_RunsOnBusinessFail` (UPDATE) | `Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` | unit/fact | quick run | ✅ green |
| GC-01 | — | In-exception tail = same multi-key DEL | `PipelineEndDeleteFacts.EndDelete_RunsOnInException` (UPDATE) | `Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` | unit/fact | quick run | ✅ green |
| GC-01 | AC-2 | Recovery all-clear tail = same multi-key DEL | `PipelineRecoveryFacts.AllClear_DeletesSource` (INVERT scalar→array) | `Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(ks => ExecutionData(d.EntryId) && MessageIndex(messageId)))` AND no `KeeperReinject` sent | unit/fact | quick run | ✅ green |
| GC-01 | AC-3 | Source step: index deleted, `Guid.Empty` data operand no-ops, no throw | `PipelineEndDeleteFacts.EndDelete_Skipped_OnSourceStep` (INVERT → now DELETES index) | `Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(ks => MessageIndex(messageId) && ExecutionData(Guid.Empty)))` AND test completes (no exception) | unit/fact | quick run | ✅ green |
| GC-02 | AC-4 | Forward read-exhaust REINJECT: NEITHER key deleted | `PipelineEndDeleteFacts.EndDelete_Skipped_OnReinject` (KEEP + add array `DidNotReceive`) | `Single(SentKeeper.OfType<KeeperReinject>())` AND `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` + scalar `DidNotReceive` | unit/fact | quick run | ✅ green |
| GC-02 | AC-4 | Recovery anyInfra REINJECT: NEITHER key deleted, index survives | `PipelineRecoveryFacts.MixedSlots_…NoSourceDelete` (KEEP + add array `DidNotReceive`) | `Single(OfType<KeeperReinject>())` AND `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` (index survives = no DEL of `MessageIndex`) | unit/fact | quick run | ✅ green |
| GC-02 | — | HGETALL-exhaust REINJECT: no delete | `PipelineRecoveryFacts.HGetAllFault_Reinject_NoSourceDelete` (KEEP + add array `DidNotReceive`) | `Single(OfType<KeeperReinject>())` AND `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` | unit/fact | quick run | ✅ green |
| GC-03 | AC-5 | Tail DEL exhaust → `KeyPersistAsync(MessageIndex)` THEN `KeeperDelete` carrying MessageId | `PipelineEndDeleteFacts.EndDelete_Exhaust_Delete` (UPDATE: add persist + MessageId asserts) | `Received(1).KeyPersistAsync((RedisKey)MessageIndex(messageId), …)` AND `Single(OfType<KeeperDelete>())` whose `.MessageId == messageId` | unit/fact | quick run | ✅ green |
| GC-03 | — | Persist-exhaust still escalates (best-effort fall-through, D-03) | `PipelineEndDeleteFacts.EndDelete_PersistExhaust_StillSendsKeeper` (NEW) | with array DEL **and** `KeyPersistAsync` both throwing: `Single(OfType<KeeperDelete>())` (keeper sent despite persist failure) | unit/fact | quick run | ✅ green |
| GC-03 | AC-6 | `KeeperDelete` exposes `MessageId`; `BuildDelete` stamps it | covered by AC-5 fact's `.MessageId == messageId` (+ compile-time: contract has the prop) | `KeeperDelete.MessageId` populated from inbound messageId | unit/fact + compile | quick run / build | ✅ green |
| GC-03 | AC-7 | `DeleteConsumer` = ONE multi-key DEL of both keys | `DeleteConsumerFacts.Delete_deletes_execution_data_key` (INVERT scalar→array) | `Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(ks => ExecutionData(m.EntryId) && MessageIndex(m.MessageId)), …)` | unit/fact | quick run | ✅ green |
| GC-03 | AC-7 | Keeper drop-on-absent (no throw) | `DeleteConsumerFacts.Delete_absent_key_no_throws` (UPDATE: stub array overload `.Returns(0L)`) | array DEL returns 0; `Consume` completes; `Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` | unit/fact | quick run | ✅ green |
| (regression) | AC-8 | Per-slot TTL writes remain (`:165`/`:275`) | existing forward/recovery facts still pass with `KeyExpireAsync` stubbed; optional `Received().KeyExpireAsync(MessageIndex(messageId), …)` in a forward-happy fact | crash backstop preserved (D-07) | unit/fact | quick run | ✅ green |
| (negative) | AC-9 | No new metric series | no new `metrics.*.Add` site; no test asserts a new counter | observability frozen (D-08) | review | n/a | ✅ green |
| (gate) | AC-10 | 0-warning Release+Debug; full suite green | build + `dotnet test` | — | build + suite | build gate | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

The hermetic infrastructure exists; the only NEW test scaffolding is the array-overload mock and one new fault mux (RESEARCH "Wave 0 Gaps"):

- [x] `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` — array `KeyDeleteAsync(Arg.Any<RedisKey[]>(), …)` stub added to every success mux; `When/Do` throw on the **array** overload added to every fault mux (`ReadOkDeleteFaultL2`, `ForwardDeleteFaultL2`, `PresentReadWriteFaultL2`, `ForwardDataFaultL2`, `ForwardSlotFaultL2` — the last three closed by `/gsd-code-review-fix 54 --all`, WR-01/02 + IN-01); `KeyPersistAsync(…).Returns(true)` stub added. *Done in 54-01 + the code-review-fix sweep.*
- [x] `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs` — array `KeyDeleteAsync(Arg.Any<RedisKey[]>(), …).Returns(2L)` (+ `.Returns(0L)` drop-on-absent variant) added. *Done in 54-01.*
- [x] NEW fault mux `ReadOkDeleteAndPersistFaultL2` (sibling of `ReadOkDeleteFaultL2`) where BOTH the array DEL and `KeyPersistAsync` throw — backs `EndDelete_PersistExhaust_StillSendsKeeper`. *Done in 54-01.*

*No new fact FILE — D-04 mandates in-place edits.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live close-gate net-zero (`skp:msg:*` count returns to baseline) | TEST-01/02 | Requires real `sk-redis` + full stack; out of scope this phase | Deferred to **Phase 55** (build-before-proof split) |

*All Phase-54 (A19 behavior) requirements have automated hermetic verification.*

---

## Validation Sign-Off

- [x] All requirement rows have a named fact with an automated quick-run command
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers the array-overload mock + persist-exhaust fault mux (MISSING references)
- [x] No watch-mode flags
- [x] Atomicity asserted as ONE array call + `DidNotReceive()` scalar (GC-01 heart) on every delete fact
- [x] Feedback latency < quick-run seconds
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** verified 2026-06-12

---

## Validation Audit 2026-06-12

State A audit (`/gsd-validate-phase 54`). Every requirement row's mapped fact was cross-referenced
against the live test files and confirmed present + green (run this session).

| Metric | Count |
|--------|-------|
| Requirements / ACs audited | GC-01/02/03 + AC-1..10 |
| Gaps found (MISSING) | 0 |
| Gaps found (PARTIAL) | 0 |
| COVERED | all |
| Resolved | 0 (none needed) |
| Escalated to manual-only | 0 (live close-gate net-zero is Phase 55, already noted) |

**Fact existence cross-reference (all green):**

| Mapped behavior | Live fact | Result |
|-----------------|-----------|--------|
| GC-01 forward happy/business/in-exc | `PipelineEndDeleteFacts.EndDelete_RunsOnHappyPath/_RunsOnBusinessFail/_RunsOnInException` | ✅ 7/7 class green |
| GC-01 recovery all-clear | `PipelineRecoveryFacts.AllClear_DeletesSource` | ✅ 5/5 class green |
| GC-01 source-step array DEL | `PipelineEndDeleteFacts.EndDelete_Skipped_OnSourceStep` + `PipelinePreFacts.SourceStep_EmptyData_ArrayDeleteRuns` | ✅ green |
| GC-02 REINJECT exclusion | `EndDelete_Skipped_OnReinject`, `MixedSlots_OneResent_OneRetire_FaultLeavesSlot_NoSourceDelete`, `HGetAllFault_Reinject_NoSourceDelete` | ✅ green |
| GC-03 persist-on-escalate + keeper | `EndDelete_Exhaust_Delete`, `EndDelete_PersistExhaust_StillSendsKeeper`, `DeleteConsumerFacts.Delete_deletes_execution_data_key`, `Delete_absent_key_no_throws` | ✅ green |
| AC-8 TTL backstop guard | `ResentCompleted_CarriesFreshExec`, `SendBeforeRetire_SendFail_LeavesSlot` (untouched) + verifier-confirmed `KeyExpireAsync` present | ✅ green |
| AC-9 no new metric | review/negative check — verifier-confirmed | ✅ |
| AC-10 build + suite | `dotnet build` 0-warning Release+Debug; hermetic suite 529/0/529/0 | ✅ |

Note: the original (pre-execution) map cited a couple of placeholder fact names that were renamed during
execution (`EndDelete_Skipped_OnSourceStep` retained, but the positive source-step assertion also lives in
the renamed `PipelinePreFacts.SourceStep_EmptyData_ArrayDeleteRuns`; the recovery REINJECT fact shipped as
`MixedSlots_OneResent_OneRetire_FaultLeavesSlot_NoSourceDelete`). All behaviors are covered by a live green fact.
