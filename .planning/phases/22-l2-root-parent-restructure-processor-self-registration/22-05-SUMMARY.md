---
phase: 22-l2-root-parent-restructure-processor-self-registration
plan: 05
subsystem: orchestration-tests
tags: [redis, l2-projection, test-isolation, processor-liveness, parent-index, close-gate]
status: complete

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
    - scripts/phase-22-close.ps1
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
    # Happy-path regression fix (operator-authorized Rule 4 scope expansion):
    - tests/BaseApi.Tests/Features/Orchestration/HappyPathE2EFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/StartLoopFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/IdempotencyFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/StartCleanupFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/StopScanFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/ValidationOrderFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/StopGateFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/CycleDetectionFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/PayloadConfigSchemaFacts.cs
    - tests/BaseApi.Tests/Observability/OrchestrationLogsE2ETests.cs
    - tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs

key-decisions:
  - "RedisFixture cleanup is now known-key (TrackedKeys + Track(key) + KeyDeleteAsync on dispose), NOT a skp:* SCAN — a wildcard SCAN on the now-shared prefix would catch/delete sibling classes' keys (D-23 / T-22-14)."
  - "Phase8WebAppFactory.RedisKeyPrefix repointed to L2ProjectionKeys.Prefix (not removed) to keep writer/cleanup/gate call sites compiling with minimal churn; the [Redis:KeyPrefix] config injection is deleted."
  - "AppsettingsFacts swaps the KeyPrefix-present fact for a KeyPrefix-ABSENT negative assertion (L2PREFIX-01 — the prefix is a compile-time const, not a Redis section setting)."
  - "All four parent-index-touching classes plus SchemaEdgeFacts join one [CollectionDefinition(DisableParallelization=true)] ParentIndex collection (D-22); each SREMs its own wf id so the shared SET is empty between tests."

patterns-established:
  - "Liveness seed unit is SECONDS (LOCKED Plan 04 / D-16): live = LivenessProjection(now, 300, \"Live\"); stale = LivenessProjection(now.AddDays(-1), 0, \"Live\")."

requirements-completed: [L2IDX-01, L2PREFIX-01, PROC-NOCREATE-01, PROC-LIVE-01, PROC-EDGE-01]

# Metrics
duration: ~13min (Tasks 1-4) + continuation (regression fix + close gate)
completed: 2026-05-31
---

# Phase 22 Plan 05: Test Surface + Close Gate Summary (COMPLETE)

**All five tasks complete and the triple-SHA close gate exits 0. The four autonomous test tasks rewrote the test-isolation seam to known-key cleanup on the shared `skp:` keyspace (no `skp:*` SCAN), de-configured the `Redis:KeyPrefix` injection, moved the golden/appsettings/writer/cleanup/gate facts to the new no-prefix + SADD/SREM/zero-processor-key shapes, added `ProcessorLivenessFacts` (204-all-live / 422-absent / 422-stale), and serialized every parent-index-touching class into one non-parallel `ParentIndex` collection. The continuation then resolved the operator-authorized cross-plan regression — Plan 04's processor-liveness gate (PROC-LIVE-01) + Plan 03's processor-no-create boundary (PROC-NOCREATE-01) broke 14 happy-path `/start` test classes the plan never listed — via the operator's chosen "centralize seed + rewrite asserts" strategy: a shared `SeedLiveProcessorAsync`/`SremParentIndexAsync` on `Phase8WebAppFactory` wired into the whole happy-path family, with `HappyPathE2EFacts` rewritten to treat the processor key as external self-registration (PROC-NOCREATE-01). Close gate: 3×271 GREEN + triple-SHA BEFORE==AFTER (psql/redis/rabbitmq) + zero-warning both configs, exit 0.**

## Status: COMPLETE — close gate exit 0

All five tasks done; the operator authorized both the fix strategy and the gate run.

## Performance
- **Duration:** ~13 min (Tasks 1-4) + continuation (regression fix across 14 classes + 3 close-gate runs)
- **Tasks complete:** 5 of 5

## Task Commits
1. **Task 1 — RedisFixture known-key cleanup + Phase8WebAppFactory no prefix injection** — `3680ada` (test)
2. **Task 2 — golden/appsettings/writer/cleanup/gate updates (D-24)** — `63067c4` (test)
3. **Tasks 3 + 4 — ProcessorLivenessFacts + ParentIndex collection (+ SchemaEdge liveness fix)** — `9c77b07` (test)
4. **Regression fix (batch 1) — seed live + rewrite happy-path asserts** — `889b34f` (fix)
5. **Regression fix (batch 2) — remaining Start/Stop classes + close-gate script** — `209bdd8` (fix)
6. **Regression fix (batch 3) — E2E parent-index SREM teardown** — `cc26688` (fix)
7. **Task 5 — triple-SHA close gate, exit 0** (gate evidence below)

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

## Deviations from Plan (continuation — operator-authorized Rule 4 scope expansion)

Executing the in-scope tasks surfaced a cross-plan regression: **Plan 04's processor-liveness gate** (every participating processor needs a live `skp:{procId}` self-registration entry before Start succeeds) + **Plan 03's processor-no-create boundary** (the writer no longer writes processor keys) broke the happy-path `/start` test family OUTSIDE this plan's 9-file scope. The prior executor paused at the Task 5 checkpoint (Rule 4). The operator approved **both**: (1) FIX = "Centralize seed + rewrite asserts"; (2) GATE = "Authorize — run the gate".

### Fix applied (commits `889b34f`, `209bdd8`, `cc26688`)

**1. [Rule 4 — operator-authorized] Centralized live-processor seed + happy-path family wiring**
- **Centralization:** added `Phase8WebAppFactory.SeedLiveProcessorAsync(procId, ct)` — seeds the processor's self-registered `skp:{procId}` entry LIVE (serialized `ProcessorProjection`, `LivenessProjection(now, 300, "Live")`, interval SECONDS) and tracks the key for known-key cleanup — and `SremParentIndexAsync(wfId)` for shared parent-index hygiene. Inherited by every happy-path factory (`Harness`/`Phase11`/`RealStack`).
- **Wired into the happy-path family (14 classes):** `StartLoopFacts`, `IdempotencyFacts`, `StartCleanupFacts`, `StopScanFacts`, `StartOrchestrationFacts`, `ValidationOrderFacts`, `StopGateFacts`, `StopOrchestrationFacts`, `CycleDetectionFacts`, `PayloadConfigSchemaFacts`, `HappyPathE2EFacts`, plus the real-stack `OrchestrationLogsE2ETests` + `CorrelationPropagationE2ETests`. Each seeds live before a happy Start, tracks its keys, and SREMs its wf id; the in-process classes join the non-parallel `ParentIndex` collection.
- **Assertion rewrite (`HappyPathE2EFacts`):** stopped asserting the writer-created processor key (PROC-NOCREATE-01) — the `skp:{procId}` entry is now the EXTERNALLY-seeded self-registration (null defs, `"Live"`), not a writer `"Pending"` one; asserts root + per-step keyspaces only.
- **Dead-config cleanup (`CorrelationPropagationE2ETests`):** removed the now-dead `["Redis:KeyPrefix"]="skp:"` in-memory override (Plan 03 removed the config key; the prefix is a compile-time const).
- **Net-zero teardown fix (`CorrelationPropagationE2ETests`):** the real-stack Start SADDs `wfId` into the shared `skp:` SET on the HOST Redis; added a targeted `SetRemoveAsync` (NOT a `KeyDelete` of `skp:` — that would wipe sibling members) in the factory's `DisposeAsync` so the close-gate `redis-cli --scan` SHA returns to its empty BEFORE state. (This orphan was the cause of the first gate run's redis SHA mismatch.)
- **Note — the prior deferred-items list was incomplete.** It enumerated 7 classes and left E2E + others un-enumerated; the full close-gate run revealed 4 additional happy-path classes (`StopGateFacts`, `StopOrchestrationFacts`, `CycleDetectionFacts`, `PayloadConfigSchemaFacts`) needing the same seed.

**Not a regression:** `ConcurrencyTokenTests.Test_RacingWrites...` failed once under full-parallel load (gate run 1) but is GREEN in isolation — a pre-existing parallel-resource flake (migration-timing), not a liveness issue. It passed clean in all three runs of the passing gate.

## Task 5 — Close Gate Evidence (operator-authorized, exit 0)

- **3-consecutive GREEN:** Run 1/2/3 each = **Passed: 271, Failed: 0** (durations 3m27s / 3m25s / 3m38s). Full suite, NO Category filter — both real-stack E2E (`CorrelationPropagationE2ETests`, `OrchestrationLogsE2ETests`) ran live against the full v3.4.0 stack up healthy.
- **Triple-SHA (psql \l + redis-cli --scan + rabbitmqctl list_queues), BEFORE == AFTER:**
  - psql \l SHA-256:                  `94ac978c670a1dd11ea3d0ad03cb57d50032dc0c3ee670d0d7e14dce6acb0240` — HELD
  - redis-cli --scan SHA-256:         `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` (0 keys — net-zero) — HELD
  - rabbitmqctl list_queues SHA-256:  `cca7a68b6141ae1e4c958f9b834370ebdd4870fcca22e582196cab5314c73be1` — HELD
- **Zero-warning build:** Release = 0 Warning(s) / 0 Error(s); Debug = 0 Warning(s) / 0 Error(s).
- **Gate script:** `scripts/phase-22-close.ps1` (mirrors `phase-21-close.ps1`), exit 0. Operator-authorized run.

## Known Stubs
None.

## Self-Check: PASSED
All created/modified files present on disk; all task + fix commits (`3680ada`, `63067c4`, `9c77b07`, `889b34f`, `209bdd8`, `cc26688`) in git history; close gate exited 0 with all three SHA invariants held.

---
*Phase: 22-l2-root-parent-restructure-processor-self-registration*
*Completed: 2026-05-31*
