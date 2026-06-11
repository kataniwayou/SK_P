---
phase: 54-terminal-index-delete
plan: 04
subsystem: testing
tags: [nsubstitute, xunit, redis, stackexchange-redis, processor-pipeline, keeper, atomicity, a19]

# Dependency graph
requires:
  - phase: 54-terminal-index-delete
    provides: 54-01 array-DEL + persist + ReadOkDeleteAndPersistFaultL2 mock surface; 54-03 unified DeleteTerminalAsync + DeleteConsumer both-key DEL + KeeperDelete.MessageId (the production A19 shape these facts now assert)
  - phase: 51-processor-forward-recovery-pipeline
    provides: the PipelineEndDeleteFacts / PipelineRecoveryFacts / PipelinePreFacts / PipelineForwardFacts scaffolding inverted here
  - phase: 52-three-state-keeper
    provides: DeleteConsumerFacts + RecoveryTestKit.Db() the both-key keeper DEL asserts against
provides:
  - "Every processor + keeper delete fact asserts the GC-01 atomicity heart: ONE Received(1) on the array KeyDeleteAsync overload + DidNotReceive() on BOTH scalar overloads"
  - "Forward-happy + recovery-all-clear facts assert the array contains BOTH ExecutionData(entryId) and MessageIndex(messageId) via an Arg.Is<RedisKey[]>(ks => Contains...) predicate matcher"
  - "Source-step fact inverted (D-06): the index IS deleted (array contains MessageIndex + ExecutionData(Guid.Empty)), no throw"
  - "REINJECT facts add an array DidNotReceive() (index survives — GC-02)"
  - "Exhaust fact asserts KeyPersistAsync(MessageIndex) + KeeperDelete.MessageId == messageId (AC-5/AC-6)"
  - "New EndDelete_PersistExhaust_StillSendsKeeper fact proves the keeper is sent even when persist also throws (D-03)"
  - "DeleteConsumer facts assert the both-key array DEL + drop-on-absent (array .Returns(0L))"
affects: [54-terminal-index-delete, A19, GC-01, GC-02, GC-03, processor-pipeline-tail, keeper-delete-consumer]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Atomicity-heart assertion: Received(1) on the array KeyDeleteAsync(RedisKey[], …) overload paired with DidNotReceive() on BOTH scalar overloads — a regression to two scalar DELs fails the suite (T-54-07 mitigation)"
    - "Array-contents predicate matcher Arg.Is<RedisKey[]>(ks => ks.Length == 2 && ks.Contains((RedisKey)key1) && ks.Contains((RedisKey)key2)) — literal arrays never match by reference (RESEARCH Pitfall 5)"

key-files:
  created: []
  modified:
    - tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs
    - tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs
    - tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs
    - tests/BaseApi.Tests/Processor/PipelinePreFacts.cs
    - tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs

key-decisions:
  - "Every delete fact pairs Received(1) on the array overload with DidNotReceive() on both scalar overloads (GC-01 atomicity heart) — never asserts two scalar deletes"
  - "Array operands asserted with the Arg.Is<RedisKey[]>(ks => Contains...) predicate matcher, casting L2ProjectionKeys strings to (RedisKey) inside the lambda (Pitfall 5)"
  - "Bound messageId to a local in each fact that asserts array contents and passed it as RunAsync's messageId arg"
  - "Source-step fact inverted to assert the index DEL (ExecutionData(Guid.Empty) is a harmless absent operand — D-06); completing without throw proves drop-on-absent"
  - "AC-8 regression guards (ResentCompleted_CarriesFreshExec, SendBeforeRetire_SendFail_LeavesSlot) left untouched"
  - "Two out-of-named-set facts (PipelinePreFacts.InputInvalid_Failed, PipelineForwardFacts.HappyTail_DeletesSource) re-greened to the array shape — they were red since the 54-03 reshape and block the AC-10 full-suite gate this plan must close (Rule 1/3 deviation)"

patterns-established:
  - "GC-01 atomicity-heart assertion shape reused across all 5 fact files: array Received(1) + dual-scalar DidNotReceive"

requirements-completed: [GC-01, GC-02, GC-03]

# Metrics
duration: 11min
completed: 2026-06-11
---

# Phase 54 Plan 04: Invert + Harden the Three Delete Fact Files to the A19 Array Shape Summary

**Inverted and hardened every processor + keeper delete fact to assert the A19 two-key atomic DEL shipped in Plan 03: each fact now asserts ONE `Received(1)` on the array `KeyDeleteAsync(RedisKey[], …)` overload (operands carrying BOTH `ExecutionData(entryId)` and `MessageIndex(messageId)` via an `Arg.Is<RedisKey[]>(ks => Contains…)` predicate matcher) AND `DidNotReceive()` on BOTH scalar overloads (the GC-01 atomicity heart). Added the new `EndDelete_PersistExhaust_StillSendsKeeper` fact (D-03 fall-through), inverted the source-step fact to assert the index reclaim (D-06), hardened the REINJECT facts with an array `DidNotReceive` (GC-02), and re-greened two out-of-named-set facts that the 54-03 production reshape had left red — closing AC-10: full hermetic suite 529/0, Release + Debug both 0-warning.**

## Performance

- **Duration:** ~11 min
- **Started:** 2026-06-11T22:43Z
- **Completed:** 2026-06-11T22:54Z
- **Tasks:** 2 (+1 deviation fix)
- **Files modified:** 5

## Accomplishments

- **GC-01 atomicity heart (every delete fact):** `EndDelete_RunsOnHappyPath`, `EndDelete_Skipped_OnSourceStep`, `AllClear_DeletesSource`, `Delete_deletes_execution_data_key`, and `HappyTail_DeletesSource` each assert ONE `Received(1)` on `KeyDeleteAsync(Arg.Is<RedisKey[]>(ks => ks.Length == 2 && ks.Contains(ExecutionData(...)) && ks.Contains(MessageIndex(...))), Arg.Any<CommandFlags>())` AND `DidNotReceive()` on BOTH scalar overloads — never two scalar deletes (RESEARCH Pitfall 2 / T-54-07).
- **GC-01 (light) on business-fail / in-exception:** `EndDelete_RunsOnBusinessFail`, `EndDelete_RunsOnInException`, `InputInvalid_Failed` swapped the scalar DEL to `Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())`.
- **D-06 source-step inversion:** `EndDelete_Skipped_OnSourceStep` now asserts the index IS deleted — the array contains `MessageIndex(messageId)` + `ExecutionData(Guid.Empty)` and the test completes without throwing (the `Guid.Empty` data operand drop-on-absent proof).
- **GC-02 REINJECT exclusion:** `EndDelete_Skipped_OnReinject`, `HGetAllFault_Reinject_NoSourceDelete`, and `MixedSlots_…NoSourceDelete` add `DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())` (neither key deleted; index survives).
- **AC-5/AC-6 persist-on-escalate:** `EndDelete_Exhaust_Delete` binds `messageId`, switched `out _ → out var db`, and asserts `Received(1).KeyPersistAsync((RedisKey)MessageIndex(messageId), Arg.Any<CommandFlags>())` then `Assert.Equal(messageId, kd.MessageId)` on the single `KeeperDelete`.
- **D-03 new fact:** `EndDelete_PersistExhaust_StillSendsKeeper` uses `ReadOkDeleteAndPersistFaultL2` (both array DEL and persist throw) and asserts a single `KeeperDelete` is STILL sent — the best-effort fall-through.
- **AC-2 recovery all-clear:** `AllClear_DeletesSource` inverted to the array-contents matcher (`ExecutionData(d.EntryId)` + `MessageIndex(messageId)`) + dual-scalar `DidNotReceive`; `Assert.Empty(KeeperReinject)` kept.
- **AC-7 keeper both-key DEL:** `NewDelete()` stamps a distinct `MessageId`; `Delete_deletes_execution_data_key` asserts the both-key array DEL; `Delete_absent_key_no_throws` stubs the array overload `.Returns(0L)` and asserts the array DEL (drop-on-absent).
- **AC-8 guards untouched:** `ResentCompleted_CarriesFreshExec` + `SendBeforeRetire_SendFail_LeavesSlot` left byte-identical (TTL/retire backstop).
- **AC-10 closed:** full hermetic suite **529 passed / 0 failed**; `dotnet build SK_P.sln` **0 Warning(s) / 0 Error(s)** in BOTH Release and Debug.

## Task Commits

Each task was committed atomically:

1. **Task 1: Invert + harden PipelineEndDeleteFacts (forward tail) + PipelineRecoveryFacts (recovery tail)** - `3ceffad` (test)
2. **Task 2: Invert DeleteConsumerFacts (both-key keeper DEL)** - `956dc76` (test)
3. **Deviation (Rule 1/3): re-green two out-of-named-set delete facts (PipelinePreFacts + PipelineForwardFacts)** - `323b80f` (test)

## Files Created/Modified

- `tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs` - happy/source/business-fail/in-exception/REINJECT/exhaust facts inverted to the array shape + atomicity heart; persist+MessageId asserts; new `EndDelete_PersistExhaust_StillSendsKeeper` fact.
- `tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs` - `AllClear_DeletesSource` inverted to the array-contents matcher; two REINJECT facts hardened with array `DidNotReceive`; AC-8 guards untouched.
- `tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs` - `NewDelete()` stamps `MessageId`; both-key array DEL assert; drop-on-absent via array `.Returns(0L)`.
- `tests/BaseApi.Tests/Processor/PipelinePreFacts.cs` - `InputInvalid_Failed` end-delete assert swapped scalar → array overload (deviation).
- `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs` - `HappyTail_DeletesSource` inverted to the full atomicity-heart matcher (deviation).

## Decisions Made

- All per-fact decisions followed the plan + phase guardrails exactly (atomicity heart everywhere, predicate matcher with `(RedisKey)` casts, `messageId` bound locals, AC-8 guards untouched).
- The two deviation-fix facts were re-greened to the same array shape rather than left red — see Deviations.

## Deviations from Plan

### [Rule 1 / Rule 3] Re-greened two delete facts outside the plan's named file set

- **Found during:** Task 2 (the AC-10 full-suite gate run).
- **Issue:** `PipelinePreFacts.InputInvalid_Failed` (line 106) and `PipelineForwardFacts.HappyTail_DeletesSource` (line 190) still asserted the retired scalar `KeyDeleteAsync((RedisKey)ExecutionData(...), …)` overload. They had been red since the Plan 54-03 production reshape (scalar → array DEL) — part of the broader red set the 54-03 SUMMARY noted ("8 failing facts"), but they live OUTSIDE the plan's three named files. They blocked the AC-10 full-suite-green gate that is this plan's explicit exit bar.
- **Fix:** inverted both to the A19 array shape — `InputInvalid_Failed` to `Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())` (mirroring `EndDelete_RunsOnBusinessFail`); `HappyTail_DeletesSource` to the full atomicity-heart matcher (array both-operand `Received(1)` + dual-scalar `DidNotReceive`).
- **Files modified:** `tests/BaseApi.Tests/Processor/PipelinePreFacts.cs`, `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs`
- **Commit:** `323b80f`
- **Rationale:** directly caused by the 54-03 production shape this plan exists to re-green; required to satisfy the plan's own AC-10 (full suite 0-failed). No production code touched.

**Total deviations:** 1 (two-file scope extension to close AC-10). No code deviations in the production tree, no architectural changes, no auth gates, no stubs, no new threat surface (hermetic test doubles only).

## Issues Encountered

- The MTP runner's `--filter-not-trait Category=RealStack` surfaced the two out-of-named-set red facts above; they were live-broker-independent assertion regressions (not RealStack), fixed inline. No other failures.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- GC-01/GC-02/GC-03 are now fully proven hermetically (the atomicity heart, REINJECT exclusion, persist-on-escalate + MessageId, both-key keeper DEL + drop-on-absent). Marked complete this plan.
- Phase 54 (terminal-index-delete) is feature-complete and at its AC-10 exit bar (full suite green, dual-config 0-warning). Phase 55 (live-proof + close gate, renumbered 54→55) is next.

---
*Phase: 54-terminal-index-delete*
*Completed: 2026-06-11*

## Self-Check: PASSED

- FOUND: tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs
- FOUND: tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs
- FOUND: tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs
- FOUND: tests/BaseApi.Tests/Processor/PipelinePreFacts.cs
- FOUND: tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs
- FOUND: .planning/phases/54-terminal-index-delete/54-04-SUMMARY.md
- FOUND commit: 3ceffad (Task 1)
- FOUND commit: 956dc76 (Task 2)
- FOUND commit: 323b80f (Deviation fix)
- Full hermetic suite 529 passed / 0 failed; Release + Debug both 0-warning (AC-10)
