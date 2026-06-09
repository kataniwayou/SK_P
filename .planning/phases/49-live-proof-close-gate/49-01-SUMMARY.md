---
phase: 49-live-proof-close-gate
plan: 01
subsystem: testing
tags: [realstack-e2e, round-trip, l2-projection, net-zero-teardown, redis, masstransit, close-gate]

# Dependency graph
requires:
  - phase: 43-48 (v4.0.0 Pre/In/Post + Keeper recovery rebuild)
    provides: the shipped Pre->In->Post pipeline, L2 ExecutionData GUID key, orchestrator per-item result advance
provides:
  - SC1 RealStack round-trip proof file (tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs)
  - Phase-49-tagged, RealStack-tagged live E2E that proves dispatch->skp:data:{entryId}->orchestrator advance
  - net-zero teardown registering every minted L2 key (close-gate redis-SHA discipline)
affects: [49-02 SC2 recovery-paths E2E, 49-03 SC3 outage E2E, 49-04 close gate, 49-HUMAN-UAT]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "One-concern-per-file RealStack E2E with self-registering net-zero teardown (cloned from SampleRoundTripE2ETests)"
    - "Authored-hermetic + operator-gated-live: file compiles + hermetic-green now; live N-GREEN run deferred to operator (49-HUMAN-UAT)"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs
  modified: []

key-decisions:
  - "Kept the Elasticsearch 'Start reload for WorkflowId=' seam-log clause as the orchestrator-advance proof (the proven precedent in the analog), per plan instruction — not a new OrchestratorQueues.Result consume assertion."
  - "Re-tagged the analog under [Trait(\"Phase\",\"49\")] in addition to the existing [Trait(\"Category\",\"RealStack\")] so the hermetic filter still excludes it and the close gate includes it."

patterns-established:
  - "Pattern: net-zero L2KeysToCleanup registration for every minted key (skp:{wfId}, skp:{wfId}:{stepId}, skp:data:*) so a leak surfaces as a close-gate redis --scan SHA mismatch."

requirements-completed: []  # TEST-01 stays UNTICKED — operator-gated live run (D-03), tracked in 49-HUMAN-UAT.md

# Metrics
duration: ~8min
completed: 2026-06-09
---

# Phase 49 Plan 01: SC1 RealStack Round-Trip Proof Summary

**Authored SC1RoundTripE2ETests.cs — a Phase-49-tagged RealStack E2E that proves the full v4 Pre->In->Post round trip (dispatch consumed -> output written to skp:data:{entryId} -> orchestrator advances) with net-zero teardown; compiles 0-warning at Release+Debug and is excluded from the GREEN 507-fact hermetic suite.**

## Performance

- **Duration:** ~8 min
- **Tasks:** 1
- **Files modified:** 1 (created)

## Accomplishments

- Created `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs` (441 lines) as a near-wholesale clone of `SampleRoundTripE2ETests.cs`, with the class renamed `SC1RoundTripE2ETests` and the test method `LiveSampleProcessor_PreInPostRoundTrip_AdvancesOrchestrator_Phase49`.
- Tagged `[Trait("Category","E2E")]` + `[Trait("Category","RealStack")]` + `[Trait("Phase","49")]` + `[Collection("Observability")]` — the RealStack trait excludes it from the hermetic suite; the Phase-49 trait scopes it for the close gate's live run.
- Preserved the load-bearing proof clauses verbatim: genuine embedded SourceHash read via `AssemblyMetadataAttribute`, truthful liveness-gated Start (`PollForHealthyLivenessAsync`), the output-write proof (`PollForNewExecutionDataKeyAsync` -> fresh `skp:data:*` key), and the orchestrator-advance ES seam-log clause ("Start reload for WorkflowId=").
- Net-zero teardown registers every minted key: `ParentIndexMembersToSrem.Add(wfId.ToString("D"))`, `L2KeysToCleanup.Add($"skp:{wfId}")`, `.Add($"skp:{wfId}:{stepId}")`, and `.Add(newDataKey!.Value)`, drained in `RealStackWebAppFactory.DisposeAsync` via `KeyDeleteAsync` + `SetRemoveAsync`.
- Copied the best-effort `/api/v1/orchestration/stop` teardown (stops the cron so it stops minting per-fire keys that would churn the close-gate scan).

## Task Commits

1. **Task 1: Clone SampleRoundTripE2ETests into SC1RoundTripE2ETests with Phase-49 trait + net-zero teardown** - `d291e08` (test)

**Plan metadata:** (final docs commit — this SUMMARY + STATE + ROADMAP)

## Files Created/Modified

- `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs` - SC1 RealStack round-trip proof: Pre->In->Post, output-to-L2 (`skp:data:{entryId}`), orchestrator-advance, net-zero teardown.

## Verification Results

- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` => **0 Warning / 0 Error** (TreatWarningsAsErrors).
- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug` => **0 Warning / 0 Error**.
- `dotnet run --project tests/BaseApi.Tests -c Release -- --filter-not-trait Category=RealStack` => **Passed! total: 507, failed: 0, skipped: 0** — the new RealStack fact is EXCLUDED, not run.
- Acceptance greps: all 8 patterns present (Phase-49 trait, RealStack trait, class decl, `PollForNewExecutionDataKeyAsync`, `L2KeysToCleanup.Add(newDataKey`, `ParentIndexMembersToSrem.Add(wfId.ToString("D"))`, `GetCustomAttributes<AssemblyMetadataAttribute>`, `"/api/v1/orchestration/start"`).
- File written BOM-less UTF-8 (no EF BB BF prefix); the 107 non-ASCII bytes are the UTF-8 `->`-arrow / `x`-multiply glyphs in comments (identical to the analog) — no mojibake.
- No production-code file changed: `git status` shows the only new tracked file is `SC1RoundTripE2ETests.cs`. No file deletions in the commit (`git diff --diff-filter=D HEAD~1 HEAD` clean — 1 file, 441 insertions only).

## Decisions Made

- Kept the Elasticsearch "Start reload for WorkflowId=" seam-log clause as the orchestrator-advance proof (the proven precedent in the analog), as the plan instructed — not a new `OrchestratorQueues.Result` consume assertion.
- Retained the analog's processor-side scope-proof ES clause verbatim (proves WorkflowId reached ES from a scope on a processor-sample log), since it is part of the proven round-trip harness and costs nothing to keep.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None. The hermetic test run emitted expected MassTransit "Connection Failed: rabbitmq://..." log noise from hermetic tests that exercise transport-failure paths — these are log warnings, not test failures (final summary: 0 failed).

## Known Stubs

None. This is an authored proof file, not a stubbed feature. The file is a complete, compiling RealStack E2E.

## Live-Run Note (D-03 — operator-gated)

This file carries `[Trait("Category","RealStack")]` and **cannot be run live in this session** (the rebuilt v4 stack is operator-gated, not up). Per the plan's definition-of-done and CONTEXT D-03, the deliverable is **AUTHORED + COMPILES 0-warning (Release AND Debug) + hermetic suite GREEN** — all satisfied. The actual live round-trip run is deferred to the operator and tracked in `49-HUMAN-UAT.md`. **TEST-01 stays UNTICKED** until the operator's GREEN live run; no `requirements mark-complete` was issued.

## Next Phase Readiness

- SC1 round-trip proof authored + hermetically green. Ready for 49-02 (SC2 recovery-paths E2E), 49-03 (SC3 outage E2E), and 49-04 (phase-49-close.ps1 + 49-HUMAN-UAT.md).
- The net-zero `L2KeysToCleanup`/`ParentIndexMembersToSrem` discipline established here is the template the sibling SC2/SC3 files must mirror so the close-gate redis SHA holds.

## Self-Check: PASSED

- FOUND: `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs`
- FOUND: `.planning/phases/49-live-proof-close-gate/49-01-SUMMARY.md`
- FOUND: commit `d291e08`

---
*Phase: 49-live-proof-close-gate*
*Completed: 2026-06-09*
