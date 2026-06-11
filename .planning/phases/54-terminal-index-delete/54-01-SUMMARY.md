---
phase: 54-terminal-index-delete
plan: 01
subsystem: testing
tags: [nsubstitute, xunit, redis, stackexchange-redis, test-kit, mock]

# Dependency graph
requires:
  - phase: 51-processor-forward-recovery-pipeline
    provides: DispatchTestKit forward/recovery muxes (scalar KeyDeleteAsync tail stubs) reshaped here for the A19 array overload
  - phase: 52-three-state-keeper
    provides: RecoveryTestKit.Db() (DeleteConsumer fake db) extended here with the array overload
provides:
  - "DispatchTestKit array KeyDeleteAsync(RedisKey[], CommandFlags) + KeyPersistAsync mocks on every tail-reaching success mux"
  - "DispatchTestKit array-DEL When/Do throw on every delete-fault mux (Pitfall-1 false-green guard)"
  - "New ReadOkDeleteAndPersistFaultL2 sibling mux where BOTH array DEL and KeyPersistAsync throw"
  - "RecoveryTestKit.Db() array KeyDeleteAsync overload stub returning a non-zero count"
affects: [54-02, 54-03, 54-04, terminal-index-delete, A19, processor-pipeline-tail, keeper-delete-consumer]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Array-overload mock siblings: every scalar KeyDeleteAsync stub/When-Do is mirrored with an Arg.Any<RedisKey[]>() sibling — NSubstitute treats the array overload as a distinct method"
    - "Pitfall-1 false-green guard: a fault mux MUST throw on the array overload (not just the scalar), else an unstubbed Task<long>->0L reads as .Succeeded=true and silently skips the exhaust branch"

key-files:
  created: []
  modified:
    - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
    - tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs

key-decisions:
  - "Kept every existing scalar KeyDeleteAsync stub/When-Do alongside the new array sibling (defensive, harmless) — additive-only, no removals"
  - "Added KeyPersistAsync success stub only to tail-reaching muxes; ReadOkDeleteFaultL2 succeeds on persist (AC-5 persist-then-escalate), the new ReadOkDeleteAndPersistFaultL2 throws on both DEL and persist (D-03 fall-through)"
  - "Left the scalar-only success muxes (PresentReadWriteFaultL2, ForwardSlotFaultL2, ForwardDataFaultL2, AbsentReadL2, ReadFaultL2) unchanged — they never reach the terminal array DEL in their scenarios"

patterns-established:
  - "Array-overload mock sibling pattern for SE.Redis multi-key calls in NSubstitute test kits"
  - "Persist-exhaust fault mux (both array DEL and KeyPersistAsync throw) as a named sibling of the delete-fault mux"

requirements-completed: [GC-01, GC-02, GC-03]

# Metrics
duration: 14min
completed: 2026-06-11
---

# Phase 54 Plan 01: Terminal-Index-Delete Test-Kit Mock Surface Summary

**NSubstitute mock surface for the A19 multi-key array `KeyDeleteAsync(RedisKey[], CommandFlags)` + best-effort `KeyPersistAsync` overloads added to both test kits — array DEL stubbed on every tail-reaching success mux, thrown on every delete-fault mux (Pitfall-1 false-green guard), plus a new `ReadOkDeleteAndPersistFaultL2` sibling where both the array DEL and persist exhaust.**

## Performance

- **Duration:** ~14 min
- **Started:** 2026-06-11T22:01Z
- **Completed:** 2026-06-11T22:15Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- Mirrored every scalar `KeyDeleteAsync` stub with its array-overload sibling on the four tail-reaching success muxes in `DispatchTestKit` (`PresentReadWriteDeleteOkL2`, `ForwardOkL2`, `RecoveryL2`, `RecoveryAllCompletedL2`), each returning `2L` and stubbing best-effort `KeyPersistAsync` to `true`.
- Added the array-overload `When/Do throw` to both delete-fault muxes (`ForwardDeleteFaultL2`, `ReadOkDeleteFaultL2`) — the Pitfall-1 guard that prevents an unstubbed `Task<long>->0L` from silently false-greening the exhaust branch. `ReadOkDeleteFaultL2` also got a `KeyPersistAsync->true` success stub for the AC-5 persist-then-escalate path.
- Added the new `ReadOkDeleteAndPersistFaultL2` sibling mux where BOTH the array DEL and `KeyPersistAsync` throw — backs the upcoming `EndDelete_PersistExhaust_StillSendsKeeper` fact (Plan 04, D-03 best-effort fall-through).
- Extended `RecoveryTestKit.Db()` with the array `KeyDeleteAsync(RedisKey[], CommandFlags)->2L` stub so the keeper `DeleteConsumer` both-key DEL resolves by default.
- Verified the full hermetic suite stays green (535 passed / 0 failed) and the Release build is 0-warning — the kit additions are inert until Plan 03's production call sites use them.

## Task Commits

Each task was committed atomically:

1. **Task 1: Extend DispatchTestKit — array KeyDeleteAsync + KeyPersistAsync on tail muxes, array When/Do on fault muxes, new persist-exhaust sibling** - `5724a45` (test)
2. **Task 2: Extend RecoveryTestKit.Db() with the array KeyDeleteAsync overload stub** - `c20b0d9` (test)

## Files Created/Modified
- `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` - Array `KeyDeleteAsync(RedisKey[])` + `KeyPersistAsync` stubs added to the four tail muxes; array-DEL `When/Do` throw added to the two delete-fault muxes; new `ReadOkDeleteAndPersistFaultL2` sibling (both array DEL and persist throw). Scalar stubs retained.
- `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs` - Array `KeyDeleteAsync(RedisKey[])->2L` stub added to `Db()`. Scalar `.Returns(true)` retained; per-test drop-on-absent override path unchanged.

## Decisions Made
- Additive-only: every existing scalar stub/When-Do retained (defensive and harmless), as the plan directs.
- `KeyPersistAsync` success stub added only to tail-reaching muxes plus `ReadOkDeleteFaultL2`; the new `ReadOkDeleteAndPersistFaultL2` is the only mux where persist throws — cleanly separating the AC-5 (persist-then-escalate) and D-03 (persist-exhaust fall-through) paths.
- Left the five scalar-only success muxes unchanged (they never reach the terminal array DEL); `ReadFaultL2` deliberately keeps NO delete stub so its `DidNotReceive` assertions stay meaningful.

## Deviations from Plan

Plan code/test execution: None - both tasks executed exactly as written.

**State-update deviation (process, not code):** The plan frontmatter lists `requirements: [GC-01, GC-02, GC-03]`, but those requirements describe the *production* terminal-index-delete behavior (the atomic multi-key DEL, REINJECT mutual-exclusion, and PERSIST-then-keeper-DELETE escalation) that ships in **Plan 03** and is asserted in **Plan 04** — this Plan 01 delivers only the inert test-kit mock prerequisite (the kit edits are "inert until Plan 03"; objective, line 37). The `requirements.mark-complete` handler also corrupted the REQUIREMENTS.md markdown (split `**GC-01**:` across a line break). Both the premature check-off and the formatting corruption were reverted (`git checkout -- .planning/REQUIREMENTS.md`); GC-01/02/03 remain open and will be marked complete by Plan 03/04. This matches the project's known GSD scoping-drift caveat and avoids misrepresenting requirement satisfaction.

## Issues Encountered
- The targeted `--filter "FullyQualifiedName~..."` regression run was silently ignored by the Microsoft.Testing.Platform runner (advisory `MTP0001`, not a build warning), so the runner executed the FULL hermetic suite instead — a strictly stronger result: 535 passed / 0 failed / 0 skipped. The intended affected suites (`PipelineEndDeleteFacts`, `PipelineRecoveryFacts`, `DeleteConsumerFacts`) are a subset and are green within that total. No action needed.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The mock surface the A19 production array DEL + persist escalation rely on is in place on both kits; Plan 03 (production change) and Plan 04 (fact edits) can now call the array overload without false-greening.
- No production code touched; no scalar stub removed; Release build 0-warning; hermetic suite 535/535 green.

---
*Phase: 54-terminal-index-delete*
*Completed: 2026-06-11*

## Self-Check: PASSED

- FOUND: tests/BaseApi.Tests/Processor/DispatchTestKit.cs
- FOUND: tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs
- FOUND: .planning/phases/54-terminal-index-delete/54-01-SUMMARY.md
- FOUND commit: 5724a45 (Task 1)
- FOUND commit: c20b0d9 (Task 2)
