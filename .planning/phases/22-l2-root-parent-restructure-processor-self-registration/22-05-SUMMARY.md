---
phase: 22-l2-root-parent-restructure-processor-self-registration
plan: 05
subsystem: orchestration-tests
tags: [redis, l2-projection, test-isolation, processor-liveness, parent-index, close-gate]
status: checkpoint-paused

# Dependency graph
requires:
  - phase: 22-l2-root-parent-restructure-processor-self-registration
    provides: "L2ProjectionKeys.Prefix const + no-prefix builders (Plan 01); writer SADD/no-processor-create + cleanup SREM (Plan 03); ProcessorLivenessValidator wired into StartAsync (Plan 04)"
provides:
  - "RedisFixture known-key cleanup (TrackedKeys/Track) on the shared skp: keyspace — no skp:* SCAN (D-23, T-22-14)"
  - "Phase8WebAppFactory: no Redis:KeyPrefix config injection; RedisKeyPrefix == L2ProjectionKeys.Prefix; TrackRedisKey passthrough"
  - "ProcessorLivenessFacts — 204 all-live / 422 absent / 422 stale (PROC-LIVE-01 acceptance)"
  - "RedisProjectionWriterFacts: SMEMBERS(ParentIndex) contains wf.Id:D (L2IDX-01) + zero processor keys (PROC-NOCREATE-01)"
  - "StopCleanupFacts: SADD-seed + SREM-after-Stop assertion (D-10)"
  - "GateNoWriteFacts: processorLiveness 422 + zero-keys arm"
  - "ParentIndexCollection — single non-parallel xUnit collection for parent-index-touching classes (D-22)"
affects: [22-close-gate, orchestration-tests]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Known-key cleanup on a shared prod keyspace: track the specific GUID keys a test created, delete only those on dispose — never a wildcard SCAN that could catch sibling classes' keys (T-22-14)"
    - "Direct L2 self-registration seed: db.StringSetAsync(L2ProjectionKeys.Processor(id), JsonSerializer.Serialize(new ProcessorProjection(...))) to exercise the liveness gate"
    - "Single non-parallel xUnit collection serializes every class touching the shared skp: parent-index SET; per-test SREM keeps it empty between tests (D-22)"

key-files:
  created:
    - tests/BaseApi.Tests/Features/Orchestration/ProcessorLivenessFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/ParentIndexCollection.cs
  modified:
    - tests/BaseApi.Tests/Composition/RedisFixture.cs
    - tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs
    - tests/BaseApi.Tests/Composition/RedisFixtureFacts.cs
    - tests/BaseApi.Tests/Composition/AppsettingsFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/RedisProjectionWriterFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/StopCleanupFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/GateNoWriteFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/SchemaEdgeFacts.cs
    - tests/BaseApi.Tests/Orchestration/OrchestrationServicePublishTests.cs

key-decisions:
  - "RedisFixture cleanup is now known-key (TrackedKeys + Track(key) + KeyDeleteAsync on dispose), NOT a skp:* SCAN — a wildcard SCAN on the now-shared prefix would catch/delete sibling classes' keys (D-23 / T-22-14)."
  - "Phase8WebAppFactory.RedisKeyPrefix repointed to L2ProjectionKeys.Prefix (not removed) to keep writer/cleanup/gate call sites compiling with minimal churn; the [Redis:KeyPrefix] config injection is deleted."
  - "AppsettingsFacts swaps the KeyPrefix-present fact for a KeyPrefix-ABSENT negative assertion (L2PREFIX-01 — the prefix is a compile-time const, not a Redis section setting)."
  - "All four parent-index-touching classes plus SchemaEdgeFacts join one [CollectionDefinition(DisableParallelization=true)] ParentIndex collection (D-22); each SREMs its own wf id so the shared SET is empty between tests."

patterns-established:
  - "Liveness seed unit is SECONDS (LOCKED Plan 04 / D-16): live = LivenessProjection(now, 300, \"Live\"); stale = LivenessProjection(now.AddDays(-1), 0, \"Live\")."

requirements-completed: [L2IDX-01, L2PREFIX-01, PROC-NOCREATE-01, PROC-LIVE-01, PROC-EDGE-01]

# Metrics
duration: ~13min (paused at Task 5 checkpoint)
completed: 2026-05-31
---

# Phase 22 Plan 05: Test Surface + Close Gate (PAUSED AT CHECKPOINT) Summary

**The four autonomous test tasks are complete and GREEN — `RedisFixture` rewritten to known-key cleanup on the shared `skp:` keyspace (no `skp:*` SCAN), `Phase8WebAppFactory` de-configures the `Redis:KeyPrefix` injection, the golden/appsettings/writer/cleanup/gate facts moved to the new no-prefix + SADD/SREM/zero-processor-key shapes, new `ProcessorLivenessFacts` proves 204-all-live / 422-absent / 422-stale, and a single non-parallel `ParentIndex` collection serializes every class touching the shared parent-index SET — BUT execution PAUSED at the Task 5 blocking close-gate checkpoint because executing the in-scope tasks surfaced a BROAD regression OUTSIDE this plan's scope: Plan 04's processor-liveness gate (and Plan 03's processor-no-create writer boundary) breaks ~10 happy-path `/start` tests across 8+ files the plan never listed, which must be resolved before the full-suite-GREEN×3 close gate can pass.**

## Status: CHECKPOINT — Task 5 awaiting operator decision

Tasks 1-4 (all autonomous, in the plan's 9 listed files) are complete, committed, and GREEN. Task 5 is the blocking `checkpoint:human-verify` close gate AND is blocked by a scope-expansion decision (Rule 4). The close gate was NOT self-authorized.

## Performance
- **Duration (so far):** ~13 min
- **Tasks complete:** 4 of 5 (Task 5 = blocking checkpoint)

## Task Commits
1. **Task 1 — RedisFixture known-key cleanup + Phase8WebAppFactory no prefix injection** — `3680ada` (test)
2. **Task 2 — golden/appsettings/writer/cleanup/gate updates (D-24)** — `63067c4` (test)
3. **Tasks 3 + 4 — ProcessorLivenessFacts + ParentIndex collection (+ SchemaEdge liveness fix)** — `9c77b07` (test)

## Accomplishments (Tasks 1-4, all GREEN)
- **Task 1 (D-23):** `RedisFixture` dropped the per-class unique prefix + `skp:*` SCAN-MATCH cleanup; added `ConcurrentBag<RedisKey> TrackedKeys` + `Track(key)`; `DisposeAsync` now `KeyDeleteAsync(TrackedKeys)` only (no wildcard SCAN, T-22-14). `Phase8WebAppFactory` deleted the `["Redis:KeyPrefix"]` injection, repointed `RedisKeyPrefix => L2ProjectionKeys.Prefix`, and added a `TrackRedisKey` passthrough. `RedisFixtureFacts` rewritten to the known-key model (proves the tracked key is deleted and an untracked sibling survives). `grep "test:cls-\|Redis:KeyPrefix" Composition/` = 0. **GREEN: RedisFixtureFacts 6/6.**
- **Task 2 (D-24):** `AppsettingsFacts` swapped the KeyPrefix-present fact for a KeyPrefix-absent negative. `RedisProjectionWriterFacts` rewritten to no-prefix builders; dropped `ProcessorProjection_Ttl`; asserts `SMEMBERS(ParentIndex)` contains `wf.Id:D` (L2IDX-01) + zero processor keys (PROC-NOCREATE-01); tracks keys + SREMs its wf id. `StopCleanupFacts` switched to no-prefix builders, seeds the parent index then asserts SREM-after-Stop (D-10). `GateNoWriteFacts` gained a `processorLiveness` 422 + zero-keys arm. `RedisProjectionOptionsBindingFacts` already had no KeyPrefix facts (Plan 03). **GREEN: writer 2/2, cleanup 4/4, gate 5/5, appsettings 7/7, binding 2/2.**
- **Task 3 (PROC-LIVE-01):** new `ProcessorLivenessFacts` (HarnessWebAppFactory HTTP-seed + direct `skp:{procId}` seed via serialized `ProcessorProjection`): 204 all-live, 422 absent (`gate=="processorLiveness"`, `offending.reason=="absent"`, `offending.procId` == unseeded id), 422 stale (`reason=="stale"`). Interval SECONDS. **GREEN 3/3.**
- **Task 4 (D-22):** `ParentIndexCollection` = `[CollectionDefinition("ParentIndex", DisableParallelization=true)]`; `RedisProjectionWriterFacts`, `StopCleanupFacts`, `GateNoWriteFacts`, `ProcessorLivenessFacts` (and `SchemaEdgeFacts`, see deviation) carry `[Collection("ParentIndex")]`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] OrchestrationServicePublishTests broken by Plan 04's new ctor param**
- **Found during:** Task 1 build verification.
- **Issue:** Plan 04 added the `ProcessorLivenessValidator` ctor param to `OrchestrationService` but had NO test files, so `OrchestrationServicePublishTests.BuildService` (which directly `new`s the service) failed to compile (CS7036). Blocked the test-project build.
- **Fix:** Inserted `new ProcessorLivenessValidator(liveMux, TimeProvider.System)` into `BuildService` (the EmptySnapshotLoader yields zero processors, so the gate's GET loop never runs — mux behavior is irrelevant).
- **Files:** `tests/BaseApi.Tests/Orchestration/OrchestrationServicePublishTests.cs` — **Commit:** `3680ada`

**2. [Rule 1 - Bug] SchemaEdgeFacts.SchemaEdgeNullSide_Passes broke on the new liveness gate**
- **Found during:** Task 4 (PROC-EDGE-01 regression check — the plan's `<verification>` requires SchemaEdgeFacts GREEN).
- **Issue:** The previously-204 null-side path now returns 422 because Plan 04's liveness gate runs AFTER schema-edge / BEFORE Upsert and the participating processors' `skp:{procId}` entries are never seeded → "absent".
- **Fix:** Seed both participating processors live before `/start`; join the `ParentIndex` collection + SREM the wf id in cleanup. **GREEN 2/2.**
- **Files:** `tests/BaseApi.Tests/Features/Orchestration/SchemaEdgeFacts.cs` — **Commit:** `9c77b07`

## CHECKPOINT BLOCKER — broad out-of-scope regression (Rule 4 surfaced)

Executing the in-scope tasks revealed that **Plan 04's processor-liveness gate** (every participating processor must have a live `skp:{procId}` self-registration entry before Start succeeds) and **Plan 03's processor-no-create boundary** (the writer no longer writes processor keys) break a large set of happy-path `/start` tests that this plan never listed in `files_modified`. Confirmed failing (Debug, live stack up):

| Class | Failed/Total | Cause |
|-------|-------------|-------|
| HappyPathE2EFacts | 1/1 | 422 liveness (no live seed) AND asserts now-removed processor-key write (PROC-NOCREATE-01) |
| StartLoopFacts | 3/3 | 422 liveness on happy Start |
| IdempotencyFacts | 2/2 | 422 liveness on happy Start |
| StartCleanupFacts | 1/1 | 422 liveness on happy Start |
| StopScanFacts | 1/1 | 422 liveness on happy Start |
| StartOrchestrationFacts | 1/6 | 422 liveness on the one happy-Start fact |
| ValidationOrderFacts | 1/5 | 422 liveness on the all-gates-pass fact |

Plus likely the real-stack `CorrelationPropagationE2ETests` + `OrchestrationLogsE2ETests` (both POST `/start` to a happy path; not yet enumerated to avoid long real-stack runs before the decision).

**Why this is a checkpoint, not a Rule 1/3 auto-fix:** (a) it spans 8+ files OUTSIDE the plan's stated 9-file scope; (b) some fixes are not mechanical seeds but behavioral-assertion rewrites (e.g. HappyPathE2EFacts asserts a processor key the writer no longer creates — it must be rewritten to seed the processor key as external self-registration and/or drop the write-assertion); (c) there is a design choice — seed live processors per-test vs. centralize a `SeedLiveProcessor` helper / auto-seed in `HarnessWebAppFactory` — that affects the whole happy-path test family and should be an explicit decision; (d) the Task 5 close gate (full suite GREEN ×3) cannot pass until this is resolved.

## Deferred Issues
The out-of-scope regression above is logged to the phase `deferred-items.md`. It MUST be resolved before the Task 5 close gate can pass.

## Known Stubs
None.

## Self-Check: PASSED
All created/modified files present on disk; all three task commits (`3680ada`, `63067c4`, `9c77b07`) in git history.

---
*Phase: 22-l2-root-parent-restructure-processor-self-registration*
*Paused at Task 5 checkpoint: 2026-05-31*
