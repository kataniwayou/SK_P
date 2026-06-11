---
phase: 54-terminal-index-delete
verified: 2026-06-12T02:20:00Z
status: passed
score: 8/8 must-haves verified
overrides_applied: 0
re_verification: false
---

# Phase 54: Terminal Index Delete Verification Report

**Phase Goal:** The processor actively reclaims the `L2[messageId]` allocation index at end-of-message — the happy-path tail (forward + recovery all-clear) deletes BOTH the source `L2[entryId]` and the `L2[messageId]` index in one atomic multi-key `DEL`, escalating a delete exhaustion to a keeper `DELETE` that now carries `{messageId, entryId}` and deletes both keys atomically (persisting the index's TTL on escalate). Restores v4 CLEANUP-grade deterministic net-zero.
**Verified:** 2026-06-12T02:20:00Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | GC-01 atomicity (production): ONE shared `DeleteTerminalAsync` issues a single `db.KeyDeleteAsync(new RedisKey[]{ExecutionData(entryId), MessageIndex(messageId)})` — no two-scalar path | VERIFIED | `ProcessorPipeline.cs:307-321`: `private async Task DeleteTerminalAsync(...)` issues the array overload. `DeleteSourceTail` local function is absent (no match in source). Both the forward tail (`:300`) and recovery all-clear tail (`:182`) call `DeleteTerminalAsync`. |
| 2 | GC-01 atomicity (facts): delete facts assert `Received(1)` on the array overload AND `DidNotReceive()` on both scalar overloads | VERIFIED | `PipelineEndDeleteFacts.cs:46-53` (happy path), `PipelineRecoveryFacts.cs:137-143` (all-clear), `PipelineEndDeleteFacts.cs:130-137` (source-step). `PipelineForwardFacts.cs:191-197` (HappyTail_DeletesSource — deviation fix). All use the `Arg.Is<RedisKey[]>(ks => ks.Length == 2 && ks.Contains(...) && ks.Contains(...))` predicate matcher + dual-scalar `DidNotReceive()`. |
| 3 | GC-02 REINJECT exclusion: REINJECT paths return before the terminal delete; facts assert NO delete + MessageIndex survives | VERIFIED | `ProcessorPipeline.cs:174-178`: `anyInfra` gate returns before `DeleteTerminalAsync`. Forward read-exhaust returns at `:212`. `PipelineEndDeleteFacts.cs:108-111` and `PipelineRecoveryFacts.cs:62`, `:93-94`: `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())` on all REINJECT paths. |
| 4 | GC-03 persist-on-escalate: `KeyPersistAsync(MessageIndex)` called THEN `SendKeeper(BuildDelete(d, messageId))` UNCONDITIONALLY (no short-circuit on persist failure) | VERIFIED | `ProcessorPipeline.cs:319-320`: `await RetryLoop.ExecuteAsync(() => db.KeyPersistAsync(L2ProjectionKeys.MessageIndex(messageId)), limit, ct); await SendKeeper(BuildDelete(d, messageId), limit, ct);` — sequential, unconditional. `EndDelete_PersistExhaust_StillSendsKeeper` fact uses `ReadOkDeleteAndPersistFaultL2` (both array DEL and persist throw) and asserts the keeper IS still sent. |
| 5 | GC-03 KeeperDelete carries `MessageId`; `BuildDelete` stamps it; `DeleteConsumer` issues the both-key array DEL | VERIFIED | `KeeperDelete.cs:13`: `public Guid MessageId { get; init; }   // A19`. `ProcessorPipeline.cs:378-379`: `BuildDelete(EntryStepDispatch d, Guid messageId)` stamps `MessageId = messageId`. `DeleteConsumer.cs:20-24`: `Guard(() => Db.KeyDeleteAsync(new RedisKey[]{ExecutionData(m.EntryId), MessageIndex(m.MessageId)}), ct)`. |
| 6 | D-06 source-step: no early-return skipping the delete for `Guid.Empty` entryId | VERIFIED | `DeleteTerminalAsync` body contains no `if (SourceStep.IsSource(...)) return` guard. `EndDelete_Skipped_OnSourceStep` fact inverted: now asserts `Received(1)` array containing `ExecutionData(Guid.Empty)` + `MessageIndex(messageId)` and completes without throw (drop-on-absent proof). |
| 7 | D-07 regression guard: per-slot random-TTL `KeyExpireAsync(MessageIndex(...), SlotTtl())` writes remain present | VERIFIED | `ProcessorPipeline.cs:165`: recovery retire refresh; `:266`: forward alloc. Both present byte-identical. `ResentCompleted_CarriesFreshExec` and `SendBeforeRetire_SendFail_LeavesSlot` AC-8 guards untouched. |
| 8 | D-08 and build/suite gate: no new metric series; Release+Debug 0-warning; hermetic suite green | VERIFIED | Only `metrics.ResultSent.Add` exists (pre-existing, `:335`). `dotnet build -c Release`: 0 Warning(s), 0 Error(s). `dotnet build -c Debug`: 0 Warning(s), 0 Error(s). Hermetic suite 0 failures. Full suite (all 536 tests) shows Debug: 4 failures, Release: 5 failures — all in `Category=RealStack` tests that require a live RabbitMQ/Redis/Postgres stack (confirmed by `RabbitMQ connection refused` and `No such host is known` errors). Out of scope for Phase 54; live proof is Phase 55. |

**Score:** 8/8 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` | `DeleteTerminalAsync` with array DEL + persist-on-escalate + both tails converging on it | VERIFIED | Lines 182, 219, 238, 244, 300 all call `DeleteTerminalAsync`. Method at 307-321 issues array DEL, best-effort persist, unconditional keeper send. `DeleteSourceTail` local function absent. |
| `src/Keeper/Recovery/DeleteConsumer.cs` | Both-key array DEL via Guard | VERIFIED | Lines 20-24: `Guard(() => Db.KeyDeleteAsync(new RedisKey[]{ExecutionData(m.EntryId), MessageIndex(m.MessageId)}), ct)` |
| `src/Messaging.Contracts/KeeperDelete.cs` | `MessageId` init property | VERIFIED | Line 13: `public Guid MessageId { get; init; }   // A19: the origin index id, for the keeper both-key DEL` |
| `tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs` | Inverted facts with array matcher + `EndDelete_PersistExhaust_StillSendsKeeper` | VERIFIED | All facts use array `Received(1)` + dual-scalar `DidNotReceive()`. New persist-exhaust fact at lines 163-179 uses `ReadOkDeleteAndPersistFaultL2`. |
| `tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs` | Inverted all-clear + hardened REINJECT facts | VERIFIED | `AllClear_DeletesSource` uses array-contents matcher. REINJECT facts add array `DidNotReceive()`. AC-8 guards untouched. |
| `tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs` | Both-key array DEL assert + drop-on-absent | VERIFIED | `NewDelete()` stamps `MessageId`. `Delete_deletes_execution_data_key` uses array-contents matcher with `MessageIndex(m.MessageId)`. `Delete_absent_key_no_throws` stubs array overload `.Returns(0L)`. |
| `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` | Array `KeyDeleteAsync` + `KeyPersistAsync` stubs on tail muxes + `ReadOkDeleteAndPersistFaultL2` | VERIFIED | `PresentReadWriteDeleteOkL2`, `ForwardOkL2`, `RecoveryL2`, `RecoveryAllCompletedL2` all stub `KeyDeleteAsync(Arg.Any<RedisKey[]>(), ...).Returns(2L)` and `KeyPersistAsync(...).Returns(true)`. `ReadOkDeleteFaultL2` and `ForwardDeleteFaultL2` throw on the array overload. New `ReadOkDeleteAndPersistFaultL2` throws on both array DEL and persist. |
| `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs` | Array `KeyDeleteAsync` stub in `Db()` | VERIFIED | Line 76: `db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);` |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `ProcessorPipeline.DeleteTerminalAsync` | `db.KeyDeleteAsync(new RedisKey[]{...})` | inline array in RetryLoop | WIRED | `ProcessorPipeline.cs:311-315`: exact array construction inside RetryLoop.ExecuteAsync |
| `DeleteTerminalAsync DEL exhaust` | `KeyPersistAsync(MessageIndex)` then `SendKeeper(BuildDelete(d, messageId))` | sequential unconditional calls | WIRED | `:319-320`: no conditional between persist and keeper send |
| `BuildDelete` | `KeeperDelete.MessageId = messageId` | init property stamp | WIRED | `:379`: `MessageId = messageId` in the initializer |
| `DeleteConsumer.HandleAsync` | `Db.KeyDeleteAsync(new RedisKey[]{ExecutionData(m.EntryId), MessageIndex(m.MessageId)})` | Guard over array overload | WIRED | `DeleteConsumer.cs:20-24` |
| Forward happy tail | `DeleteTerminalAsync` | direct call | WIRED | `ProcessorPipeline.cs:300` |
| Recovery all-clear tail | `DeleteTerminalAsync` | direct call under `!anyInfra` | WIRED | `ProcessorPipeline.cs:182` |
| Forward business-fail / in-exception / unexpected exits | `DeleteTerminalAsync` | direct call before return | WIRED | `:219`, `:238`, `:244` |
| REINJECT paths (anyInfra, forward read-exhaust) | early return | return before DeleteTerminalAsync | WIRED (exclusion) | `:177-178`, `:211-212` |

### Data-Flow Trace (Level 4)

Not applicable — this phase introduces no UI rendering or dynamic data display components. All artifacts are backend processing logic and hermetic tests.

### Behavioral Spot-Checks

| Behavior | Evidence | Status |
|----------|----------|--------|
| `DeleteTerminalAsync` issues one `KeyDeleteAsync(RedisKey[], ...)` | Source read confirmed; no other delete call in method body | PASS |
| Both tails converge on `DeleteTerminalAsync` | Lines 182 and 300 both call `await DeleteTerminalAsync(d, messageId, db, limit, ct)` | PASS |
| Persist is called before keeper send with no short-circuit | Lines 319-320 are sequential with no conditional | PASS |
| `DeleteSourceTail` local function removed | Grep for `DeleteSourceTail` in ProcessorPipeline.cs: no matches | PASS |
| Per-slot TTL writes present (AC-8) | `KeyExpireAsync(L2ProjectionKeys.MessageIndex(messageId), SlotTtl())` at lines 165 and 266 | PASS |
| No new metric series (AC-9) | Only `metrics.ResultSent.Add` at `:335` (pre-existing) | PASS |
| Release build 0-warning | `dotnet build -c Release`: 0 Warning(s), 0 Error(s) | PASS |
| Debug build 0-warning | `dotnet build -c Debug`: 0 Warning(s), 0 Error(s) | PASS |
| Hermetic suite green (Debug) | 536 total: 532 passed / 4 failed — all 4 failures are `Category=RealStack` (RabbitMQ/Redis/Postgres connection refused) | PASS (hermetic) |
| Hermetic suite green (Release) | 536 total: 531 passed / 5 failed — all 5 failures are `Category=RealStack` (same live-stack connection errors) | PASS (hermetic) |

### Requirements Coverage

| Requirement | Source Plans | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| GC-01 | 54-01, 54-03, 54-04 | Atomic two-key terminal delete (one multi-key DEL) | SATISFIED | `DeleteTerminalAsync` issues single array DEL; facts assert `Received(1)` on array + `DidNotReceive()` on scalars; recovery all-clear and forward tails both covered (AC-1, AC-2, AC-3) |
| GC-02 | 54-03, 54-04 | REINJECT mutual exclusion preserved | SATISFIED | `anyInfra` gate returns before `DeleteTerminalAsync`; forward read-exhaust returns early; facts assert `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), ...)` on all REINJECT paths (AC-4) |
| GC-03 | 54-02, 54-03, 54-04 | Persist-on-escalate + both-key keeper DELETE | SATISFIED | `KeeperDelete.MessageId` present; `BuildDelete` stamps it; `DeleteConsumer` issues both-key array DEL; `EndDelete_Exhaust_Delete` asserts persist + MessageId (AC-5/AC-6); `Delete_deletes_execution_data_key` asserts both-key array DEL (AC-7); `EndDelete_PersistExhaust_StillSendsKeeper` proves D-03 fall-through |

### Anti-Patterns Found

No blockers or warnings found in the Phase 54 production or test files.

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| None | — | — | — | — |

Note: The code review (54-REVIEW.md) found 3 advisory warnings (some fault muxes outside the named set missing the array-overload stub, stale test name) and 1 info item — all in the test layer, all advisory, none goal-blocking. Not flagged as gaps.

### Human Verification Required

None. This phase is fully verified by hermetic facts + build gates. Live/real-stack proof is explicitly deferred to Phase 55 per the SPEC boundary definition.

### Gaps Summary

No gaps. All 8 must-haves are verified in the actual source code. The 4-5 test failures observed across Debug/Release configs are all `Category=RealStack` integration tests requiring a live RabbitMQ/Redis/Postgres stack — confirmed by the RabbitMQ channel errors and host resolution failures in the test output. These are by design excluded from Phase 54's hermetic verification bar and will be addressed in Phase 55.

---

_Verified: 2026-06-12T02:20:00Z_
_Verifier: Claude (gsd-verifier)_
