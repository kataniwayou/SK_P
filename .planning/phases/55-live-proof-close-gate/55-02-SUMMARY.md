---
phase: 55-live-proof-close-gate
plan: 02
subsystem: testing
tags: [realstack-e2e, redis, slot-array, messageId-index, net-zero, A19, xunit, traits]

# Dependency graph
requires:
  - phase: 51-processor-forward-recovery-pipeline
    provides: "allocation-before-data forward pass (skp:msg index FIRST, skp:data SECOND)"
  - phase: 54-terminal-index-delete
    provides: "A19 two-key DeleteTerminalAsync (atomic DEL [skp:data, skp:msg]) + KeeperDelete.MessageId"
provides:
  - "SC1RoundTripE2ETests adapted: asserts a fresh skp:msg:{messageId} HASH appears (allocation-before-data) AND the A19 two-key net-zero (BOTH skp:data:{entryId} AND skp:msg:{messageId} absent at end-of-message)"
  - "SC3PauseResumeOutageE2ETests retagged Phase 55 (A14 BIT-gate pause/resume behavior unchanged)"
  - "Dead v4 composite-sweep teardown block (GAP-49-8, skp:*:{wfId}:*) removed from both SC1 and SC3 factories"
affects: [55-03-close-script, 55-04-human-uat, close-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "ScanMessageIndexKeys() sibling of ScanExecutionDataKeys() — enumerate skp:msg:* HASH family (messageId server-minted, unknown a priori)"
    - "A19 net-zero assertion: PollForKeyAbsentAsync on BOTH key families proves active two-key DEL (not a TTL race)"

key-files:
  created: []
  modified:
    - tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs
    - tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs

key-decisions:
  - "newDataKey/newMsgKey are the live-scanned key strings — fed directly into PollForKeyAbsentAsync for the A19 net-zero (no need to reconstruct from entryId/messageId, which the test never learns)"
  - "PollForNewMessageIndexKeyAsync mirrors the existing PollForNewExecutionDataKeyAsync shape (snapshot-before / poll-for-new) — both poll on OutputPollTimeoutMs"
  - "PollForKeyAbsentAsync cloned into SC1 (not extracted to a shared base) — matches the per-file self-contained SC idiom and SC2's existing copy"

patterns-established:
  - "Allocation-before-data assertion: snapshot skp:msg:* before Start, poll for a fresh index HASH, register into L2KeysToCleanup (D-07)"

requirements-completed: [TEST-01]

# Metrics
duration: 13min
completed: 2026-06-12
---

# Phase 55 Plan 02: SC1 slot-array + A19 net-zero adaptation, SC3 retag Summary

**SC1 now proves the v5 forward pass writes the slot-array allocation index `skp:msg:{messageId}` (allocation-before-data) and that the A19 two-key DELETE reclaims BOTH `skp:data` AND `skp:msg` at end-of-message; SC3 retagged Phase 55 with A14 behavior verbatim; dead v4 composite sweep deleted from both factories.**

## Performance

- **Duration:** 13 min
- **Started:** 2026-06-12T07:56:58Z
- **Completed:** 2026-06-12T08:10:04Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- **SC1 (TEST-01):** added `ScanMessageIndexKeys()` + `PollForNewMessageIndexKeyAsync()` over `skp:msg:*`; snapshots `msgKeysBefore` before Start and asserts a fresh index HASH appears (allocation-before-data, production order `HashSetAsync` index at `ProcessorPipeline.cs:262` before `StringSetAsync` data at `:275`); registers the minted index key into `L2KeysToCleanup` (D-07); cloned SC2's `PollForKeyAbsentAsync` and asserts the A19 two-key net-zero on BOTH the minted `skp:data` AND `skp:msg` keys after the orchestrator-advance ES proof; retagged `[Trait("Phase","55")]`; renamed the fact `LiveSampleProcessor_ForwardRoundTrip_AllocBeforeData_TwoKeyNetZero_Phase55`.
- **SC3 (TEST-02 support):** retagged `[Trait("Phase","55")]` + XML doc mentions; A14 BIT-gate `Global PauseAll`/`Global ResumeAll` assertions and `RedisOutageSerial`/`DisableParallelization=true` isolation left verbatim (Landmine 4 / Pitfall 3).
- **Both factories:** deleted the dead v4 composite-sweep teardown block (`GAP-49-8`, scans `skp:*:{wfId}:*`) — Model-B is retired (Landmine 3); the `L2KeysToCleanup` drain + `ParentIndexMembersToSrem` SREM + `Restore()`/`base.DisposeAsync()` tail kept.

## Task Commits

Each task was committed atomically:

1. **Task 1: Adapt SC1 — skp:msg alloc-index + A19 two-key net-zero, retag Phase 55, delete composite sweep** - `d4c379e` (test)
2. **Task 2: Retag SC3 to Phase 55 + delete composite-sweep block (no behavior change)** - `2d7829a` (test)

_Note: this is a `type: tdd` Task 1 by frontmatter, but the "test" IS the adapted artifact (a RealStack E2E that runs live in the operator gate, not autonomously) — there is no separate RED/GREEN production split. The autonomous gate is the Release build; see TDD Gate Compliance below._

## Files Created/Modified
- `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs` - Forward round-trip + slot-array index assertion (allocation-before-data) + A19 two-key net-zero; retagged Phase 55; composite sweep removed
- `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs` - Retagged Phase 55 (A14 pause/resume unchanged); composite sweep removed

## Decisions Made
- The test never learns the raw `entryId`/`messageId` (both are server-minted); it scans for the fresh `skp:data:*`/`skp:msg:*` key strings and feeds those directly into `PollForKeyAbsentAsync` for the A19 net-zero — simpler and matches what the test can actually observe.
- `PollForKeyAbsentAsync` was cloned into SC1 (not extracted to a shared base) to match the self-contained per-SC-file idiom (SC2 has its own copy).

## Deviations from Plan

None - plan executed exactly as written. The five sub-edits of Task 1 and the two of Task 2 were applied verbatim against the cited line ranges; the live line numbers had drifted slightly from the plan's `~:` references but the anchor text was unambiguous.

## TDD Gate Compliance

Task 1 carries `tdd="true"`, but its `<verify>` is the autonomous **Release build** (the RealStack fact is `Category=RealStack` — excluded from the hermetic run, executed live in the Phase-55 operator close gate, not autonomously). There is therefore no RED→GREEN production-code cycle to gate: the adapted file IS the test artifact. The autonomous gate — `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` — exits 0 (0 warnings) after both tasks, and the hermetic suite (`--filter-not-trait Category=RealStack`) is **529 GREEN**, confirming the two RealStack facts compile and are correctly excluded. The genuine RED/GREEN cycle for this milestone is the operator's live close-gate run (TEST-01/02 stay unticked until then, by design — D-03 across every prior milestone close).

## Issues Encountered
- Initial attempts to read the test summary from the MTP runner via grep picked up log noise (`rabbitmq` background-bus connection warnings are expected — the in-proc WebApi tries the default `rabbitmq://rabbitmq/` host before the host-override env vars take effect in unrelated hermetic tests). Resolved by filtering for `Test run summary:` — confirmed `Passed! ... succeeded: 529`.

## Verification

- **Autonomous (per plan):** `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` → **exit 0, 0 Warning(s), 0 Error(s)** (both adapted RealStack tests COMPILE).
- **Hermetic suite:** `dotnet run --project tests/BaseApi.Tests -c Release -- --filter-not-trait Category=RealStack` → **Passed! — 529 succeeded** (RealStack facts excluded; matches Phase-54 baseline).
- **Acceptance grep:** SC1 contains `[Trait("Phase", "55")]`, `ScanMessageIndexKeys` (over `Prefix}msg:*`), `PollForKeyAbsentAsync` on BOTH a `skp:data` and a `skp:msg` key, `L2KeysToCleanup.Add` for the minted index; SC3 contains `[Trait("Phase", "55")]`, `RedisOutageSerial`, `DisableParallelization = true`, `Global PauseAll`/`Global ResumeAll`. Neither file contains `[Trait("Phase","49")]`, `skp:*:`, or `GAP-49-8`.

## Next Phase Readiness
- SC1 + SC3 are ready for the Phase-55 close gate (`55-03` close script + `55-04` HUMAN-UAT runbook). Plan 55-02 deliberately does NOT touch SC2 (the 3-state rewrite + organic recovery test) or the close script — those are sibling plans.
- The live N×GREEN close run remains operator-gated (per the milestone's D-03 convention); TEST-01/TEST-02 tick only after the operator's GREEN run against the rebuilt v5 stack.

## Self-Check: PASSED

- FOUND: `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs`
- FOUND: `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs`
- FOUND: `.planning/phases/55-live-proof-close-gate/55-02-SUMMARY.md`
- FOUND commit: `d4c379e` (Task 1)
- FOUND commit: `2d7829a` (Task 2)

---
*Phase: 55-live-proof-close-gate*
*Completed: 2026-06-12*
