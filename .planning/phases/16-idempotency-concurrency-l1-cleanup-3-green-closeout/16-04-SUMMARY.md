---
phase: 16-idempotency-concurrency-l1-cleanup-3-green-closeout
plan: 04
subsystem: testing
tags: [redis, idempotency, concurrency, orchestration, stackexchange-redis, xunit]

# Dependency graph
requires:
  - phase: 15-l2-redis-projection-write-stop-existence-check
    provides: "Per-workflow Start loop (delete-then-write, jobId=Guid.NewGuid() per Start), Redis-EXISTS Stop gate + tolerant root/step cleanup (processor keys never deleted)"
  - phase: 16-idempotency-concurrency-l1-cleanup-3-green-closeout (plan 01)
    provides: "Phase8WebAppFactory RedisMultiplexer/RedisKeyPrefix access surface; REQUIREMENTS/ROADMAP inverted-Stop wording"
provides:
  - "TEST-REDIS-08 idempotency regression: sequential second-write-reflected (jobId CHANGED, D-02) + concurrent observational both-204-structurally-valid (D-01, no winner)"
  - "TEST-REDIS-09 thin confirmatory inverted post-Stop key-state fact (root+step gone, processor retained)"
affects: [phase-16-closeout, 3-green-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "jobId-changed overwrite proof — assert second != first (positive change), not != Guid.Empty, to defeat no-op aliasing (D-02)"
    - "Concurrent observational fact — Task.WhenAll two clients, assert both 204 + final root round-trips, NO deterministic-winner assertion (non-flaky under last-write-wins)"
    - "Two-client concurrency to avoid single-HttpClient send serialization (A1)"

key-files:
  created:
    - tests/BaseApi.Tests/Features/Orchestration/IdempotencyFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/StopScanFacts.cs
  modified: []

key-decisions:
  - "Single test(...) commit per fact class (not RED→GREEN) — production behavior already shipped in Phase 15; these facts LOCK existing contract, so they pass on first run against real code (no failing-RED to manufacture)"
  - "Two HttpClient instances for the concurrent fact (A1 caution) — genuine race rather than relying on a single client to parallelize its sends"
  - "ReStart fact reuses the StartLoopFacts orphan-removal graph (A→B shrink) to additionally prove delete-then-write GC alongside the jobId-changed overwrite proof"

patterns-established:
  - "Idempotency overwrite proof via fresh jobId delta (positive-change assertion)"
  - "Last-write-wins concurrency documented (not asserted) to stay non-flaky across the 3-GREEN gate"

requirements-completed: [TEST-REDIS-08, TEST-REDIS-09]

# Metrics
duration: ~9min
completed: 2026-05-29
---

# Phase 16 Plan 04: Idempotency + Concurrency + thin Stop-confirmatory facts Summary

**Two new Phase-16 test classes locking the no-Redis-lock idempotency/concurrency contract: IdempotencyFacts proves a second Start overwrites L2 (jobId CHANGED, not just non-empty) plus a non-flaky concurrent both-204 observational fact; StopScanFacts confirms the inverted post-Stop key state (root+step gone, processor retained).**

## Performance

- **Duration:** ~9 min
- **Started:** 2026-05-29T17:45:00Z (approx)
- **Completed:** 2026-05-29
- **Tasks:** 2
- **Files modified:** 2 (both created)

## Accomplishments
- **TEST-REDIS-08 (D-02 sequential):** `ReStart_SameWorkflow_ReflectsSecondWrite` — start-twice with the SAME workflowIds; asserts the root `jobId` CHANGED between Starts (`Assert.NotEqual(firstJobId, secondRoot.JobId)`), proving the second write is reflected (overwrite, not no-op — Aliasing Risk D-02). Also proves delete-then-write GC: the orphaned per-step key for the removed `A→B` edge is gone while A survives.
- **TEST-REDIS-08 (D-01 concurrent):** `ConcurrentStart_SameWorkflow_BothSucceed_FinalStructurallyValid` — two parallel `POST /start` for the same wfId via `Task.WhenAll` (two clients); asserts both 204 and the final root round-trips. NO deterministic-winner assertion (last-write-wins by design; a genuine interleave can transiently wipe the other writer — D-01 tolerates this to stay non-flaky).
- **TEST-REDIS-09 (thin confirmatory):** `Stop_AfterStart_RemovesRootAndStep_KeepsProcessor` — inverted contract: post-Stop the root + per-step keys are GONE while the processor key is PRESENT (TTL'd). Additive class; the Phase-15 Stop facts are untouched.

## Task Commits

Each task was committed atomically:

1. **Task 1: Create IdempotencyFacts (sequential D-02 + concurrent D-01)** - `612b692` (test)
2. **Task 2: Create StopScanFacts (thin confirmatory TEST-REDIS-09)** - `2baf725` (test)

**Plan metadata:** (this commit) (docs: complete plan)

_Note: both tasks are test-only and lock pre-existing Phase-15 production behavior, so each is a single `test(...)` commit (no RED→GREEN cycle — see Decisions)._

## Files Created/Modified
- `tests/BaseApi.Tests/Features/Orchestration/IdempotencyFacts.cs` - TEST-REDIS-08: sequential second-write-reflected (jobId changed + orphan GC) + concurrent observational (both 204, root round-trips, no winner)
- `tests/BaseApi.Tests/Features/Orchestration/StopScanFacts.cs` - TEST-REDIS-09: thin confirmatory inverted post-Stop key-state (root+step gone, processor retained)

## Decisions Made
- **Single `test(...)` commit per fact class instead of a RED→GREEN cycle.** The Start loop, jobId-per-Start semantics, last-write-wins concurrency, and inverted-Stop cleanup are all production behavior shipped in Phase 15. These facts lock that existing contract into a stable regression, so they pass on the first run against real code. There is no failing-RED to manufacture — the TDD fail-fast rule's intent (don't skip RED on a test that should fail) does not apply when the contract is pre-existing and the test's job is to pin it. Mirrors how the sibling Phase-15 `StartLoopFacts`/`StopGateFacts` were authored.
- **Two `HttpClient` instances for the concurrent fact (A1 caution).** Ensures the two `POST /start` calls genuinely race rather than relying on a single client to parallelize its sends.
- **ReStart fact reuses the `StartLoopFacts` orphan-removal graph (A→B shrink).** Lets one fact prove both the jobId-changed overwrite (D-02) and delete-then-write GC in a single seed.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- The `dotnet test --filter` flag is ignored by the Microsoft.Testing.Platform runner (emits `MTP0001`; runs the whole assembly). Not a problem — the full suite ran GREEN both times (234 → 235 passed, 0 failed), which is strictly stronger evidence: it confirms the new facts pass AND that the Phase-15 Orchestration/Stop facts remain intact and untouched.

## User Setup Required
None - no external service configuration required. (Requires the local compose stack — Postgres + Redis — which is already running and healthy.)

## Next Phase Readiness
- TEST-REDIS-08 + TEST-REDIS-09 are GREEN and non-flaky; both new facts are the wave-2 deliverable for the Phase-16 3-GREEN closeout.
- No existing fact modified (`StopGateFacts`/`StopOrchestrationFacts`/`StopCleanupFacts` unchanged). No production code touched. No FLUSHDB; SCAN/EXISTS only via the per-class `RedisFixture` teardown.
- Ready for the Phase-16 close gate (3× Release run + dual-SHA Postgres/Redis snapshot invariant).

## Self-Check: PASSED

- FOUND: tests/BaseApi.Tests/Features/Orchestration/IdempotencyFacts.cs
- FOUND: tests/BaseApi.Tests/Features/Orchestration/StopScanFacts.cs
- FOUND: .planning/phases/16-idempotency-concurrency-l1-cleanup-3-green-closeout/16-04-SUMMARY.md
- FOUND commit: 612b692 (Task 1 IdempotencyFacts)
- FOUND commit: 2baf725 (Task 2 StopScanFacts)

---
*Phase: 16-idempotency-concurrency-l1-cleanup-3-green-closeout*
*Completed: 2026-05-29*
