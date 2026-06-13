---
phase: 61-1-healthy-orchestration-start-gate-self-watchdog-probe
plan: 01
subsystem: api
tags: [redis, stackexchange-redis, orchestration, liveness, rfc7807, smembers, nsubstitute, xunit]

# Dependency graph
requires:
  - phase: 59-per-instance-l2-keyspace
    provides: ProcessorLivenessEntry, L2ProjectionKeys.PerInstance/InstanceIndex, LivenessStatus consts
  - phase: 60-dual-loop-writer
    provides: per-instance L2 writer (SADD instanceId into the index + SET ProcessorLivenessEntry), the keyspace the gate reads
provides:
  - "Per-replica orchestration-start gate: SMEMBERS skp:proc:{procId} -> GET-each per-instance ProcessorLivenessEntry -> admit iff >=1 replica Healthy+fresh"
  - "Aggregate count-only 422 RFC 7807 reason ('no healthy replica (N checked: A absent, U unhealthy, S stale, M malformed)')"
  - "Absent-only fire-and-forget lazy SREM index hygiene (D-09)"
  - "D-11 teardown complete: L2ProjectionKeys.Processor, RedisProjectionKeys.Processor forwarder, ProcessorProjection record all deleted; SHARED LivenessProjection untouched"
  - "Phase=61 pure-unit gate test (NSubstitute IDatabase) + re-pointed real-Redis integration facts"
affects: [62-live-proof-close-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Two-level Redis discovery loop (SMEMBERS index -> GET-each member) generalizing a shipped single-key read"
    - "422-vs-500 split preserved through the CALLER's RedisException catch (validator throws only OrchestrationValidationException for data states)"
    - "Per-replica liveness seeding in tests via SADD index + SET ProcessorLivenessEntry.Create(...)"

key-files:
  created:
    - tests/BaseApi.Tests/Features/Orchestration/ProcessorLivenessGateUnitTests.cs
  modified:
    - src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs
    - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs
    - tests/BaseApi.Tests/Features/Orchestration/ProcessorLivenessFacts.cs

key-decisions:
  - "Aggregate 422 reason is a count-only string (info-disclosure-safe: counts + procId Guid only, never instanceIds) — kept the existing ProcessorLivenessOffending(procId, reason) shape per D-08 discretion"
  - "RedisProjectionKeys does NOT gain PerInstance/InstanceIndex forwarders — the gate calls L2ProjectionKeys.* directly (61-PATTERNS writer-side convention)"
  - "AbsentProcessor_Returns422 uses the empty-index path (zero replicas) as the simplest 'no replica qualifies' case"
  - "Many more compile-break callers than the plan's table listed (15 test files) — all re-pointed to the per-replica keyspace (Rule 3)"

patterns-established:
  - "First-qualifier-wins short-circuit: a single Healthy+fresh replica admits even with stale/unhealthy/absent/malformed siblings"
  - "Test liveness poll for RealStack E2E now SMEMBERS->GET-each (mirrors the gate) instead of reading the retired flat key"

requirements-completed: [GATE-01, GATE-02, GATE-03]

# Metrics
duration: 37min
completed: 2026-06-13
---

# Phase 61 Plan 01: >=1-Healthy Orchestration-Start Gate Summary

**Swapped ProcessorLivenessValidator from the single last-write-wins `GET skp:{procId}` (ProcessorProjection) read to a `SMEMBERS skp:proc:{procId}` -> GET-each per-instance ProcessorLivenessEntry >=1-healthy-and-fresh gate with an aggregate count 422 reason and absent-only lazy SREM, deleted the now-dead legacy contract (D-11), and re-pointed every compile-break caller + added a Phase=61 pure-unit gate test.**

## Performance

- **Duration:** ~37 min
- **Started:** 2026-06-13T13:10:05Z
- **Completed:** 2026-06-13T13:47:05Z
- **Tasks:** 3
- **Files modified:** 24 (3 production, 1 deleted, 20 test)

## Accomplishments
- Per-replica orchestration-start gate (GATE-01/02/03): discovers replicas via SMEMBERS with no prior instanceId knowledge, admits on >=1 Healthy+fresh, blocks 422 + RFC 7807 otherwise, fires absent-only lazy SREM, preserves the load-bearing 422-vs-500 split (no RedisException catch in the validator).
- D-11 teardown: deleted `L2ProjectionKeys.Processor(Guid)`, the `RedisProjectionKeys.Processor` forwarder, and the `ProcessorProjection` record; the SHARED `LivenessProjection` is untouched.
- New Phase=61 pure-unit gate test (NSubstitute IDatabase) covering >=1-healthy admit, first-qualifier-wins over stale/absent siblings, no-qualifier 422 aggregate reason, empty-index/malformed 422, and absent-only `SetRemoveAsync(..., FireAndForget)` Received(1).
- Re-pointed real-Redis integration facts (ProcessorLivenessFacts) + 14 other compile-break test files onto the per-replica keyspace.
- Debug AND Release solution builds at 0 warnings; ProcessorLiveness (25), Stop (6), Projection (32) hermetic tests all green.

## Task Commits

1. **Task 1: Swap the validator to SMEMBERS->GET-each >=1-healthy gate + aggregate 422 reason** - `35dec7e` (feat)
2. **Task 2: D-11 teardown — delete legacy Processor key builder/forwarder/record + golden/round-trip pins** - `6b62128` (refactor)
3. **Task 3: Re-point real-Redis gate facts + add pure-unit gate test** - `222cc35` (test)

## Files Created/Modified
- `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` - Two-level SMEMBERS->GET-each loop; aggregate reason; absent-only lazy SREM; class summary + WR-01 comment re-authored for the per-replica world.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs` - ProcessorNotLive/ProcessorLivenessOffending docs updated to the aggregate count breakdown (signature unchanged).
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` - Deleted `Processor(Guid)`; updated class note + bullet.
- `src/Messaging.Contracts/Projections/ProcessorProjection.cs` - **DELETED** (D-11).
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs` - Deleted `Processor` forwarder.
- `tests/BaseApi.Tests/Features/Orchestration/ProcessorLivenessGateUnitTests.cs` - **CREATED** pure-unit gate test (Phase=61).
- `tests/BaseApi.Tests/Features/Orchestration/ProcessorLivenessFacts.cs` - Re-pointed to per-instance keyspace; +[Trait Phase 61]; aggregate-reason assertions.
- `tests/.../Projection/{L2ProjectionKeysTests,RedisProjectionKeysTests,ProjectionRecordRoundTripTests}.cs` - Deleted Processor/byte-identity pins + ProcessorProjection round-trip facts.
- `tests/.../Composition/Phase8WebAppFactory.cs`, `SchemaEdgeFacts.cs`, `HappyPathE2EFacts.cs`, `RedisProjectionWriterFacts.cs`, `StopCleanupFacts.cs`, `StopScanFacts.cs`, `StopGateFacts.cs`, `Processor/StartupUnhealthyWriteFacts.cs`, and 6 `Orchestrator/*E2ETests.cs` - Re-pointed onto the per-replica keyspace / inlined the retired flat-key shape where only absence is asserted (Rule 3).

## Decisions Made
- Aggregate 422 reason kept as a count-only formatted string (info-disclosure-safe), reusing the unchanged `ProcessorLivenessOffending(procId, reason)` shape (D-08 discretion).
- No `PerInstance`/`InstanceIndex` forwarders added to `RedisProjectionKeys` — the gate imports and calls `L2ProjectionKeys.*` directly (61-PATTERNS).
- Absent case in the integration fact uses the empty-index path (zero replicas) — simplest mapping of "no replica qualifies".

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Re-pointed 15 compile-break caller files the plan's table omitted**
- **Found during:** Task 2 (D-11 teardown — the `dotnet build SK_P.sln` verify could not pass).
- **Issue:** The plan/RESEARCH "Compile-break callers" table listed only the validator + 3 test classes. A repo-wide grep found `ProcessorProjection` / `L2ProjectionKeys.Processor` used by 11 additional test files: `Phase8WebAppFactory.SeedLiveProcessorAsync` (shared helper), `SchemaEdgeFacts`, `HappyPathE2EFacts`, `RedisProjectionWriterFacts`, `StopCleanupFacts`, `StartupUnhealthyWriteFacts`, and 6 `Orchestrator/*E2ETests.cs` RealStack files. The solution would not compile (Task 2's verify) until all were re-pointed.
- **Fix:** Re-pointed the shared seed helper + integration seeds onto SADD-index + SET-ProcessorLivenessEntry; re-pointed RealStack liveness polls to SMEMBERS->GET-each (matching what the Phase-60 writer actually writes); inlined the legacy flat-key string (`skp:{procId}`) where a test only asserts the OLD key's absence; re-pointed the GateA "no Healthy replica" inverse assertion.
- **Files modified:** the 15 test files listed above.
- **Verification:** `dotnet build SK_P.sln -c Debug` and `-c Release` both 0 warnings; hermetic ProcessorLiveness/Stop/Projection tests green.
- **Committed in:** `6b62128` (Task 2).

**2. [Rule 1 - Bug] Re-pointed StopScanFacts/StopGateFacts liveness-retained assertions**
- **Found during:** Task 3 (full-suite test run surfaced 2 hermetic failures after the SeedLiveProcessorAsync re-point).
- **Issue:** Both tests seed via `SeedLiveProcessorAsync` then assert the flat `skp:{procId}` key is retained after Stop. With the helper now seeding the per-instance keyspace, the flat-key assertions failed.
- **Fix:** Re-pointed the "processor liveness retained post-Stop" assertions to `L2ProjectionKeys.InstanceIndex(procId)`.
- **Files modified:** `StopScanFacts.cs`, `StopGateFacts.cs` (+ `Messaging.Contracts.Projections` using).
- **Verification:** Both classes green (6 tests).
- **Committed in:** `222cc35` (Task 3).

---

**Total deviations:** 2 auto-fixed (1 blocking, 1 bug).
**Impact on plan:** Both were necessary to keep the solution compiling and the hermetic suite green after the D-11 deletion; the additional caller re-points are the same per-replica reshape the plan mandates, applied to the wider call surface. No scope creep beyond re-pointing dead-symbol references onto the live keyspace.

## Issues Encountered
- The 7 remaining full-suite failures are all `[Trait("Category","RealStack")]` E2E tests (`SampleRoundTripE2ETests`, `SC1/SC2/SC3*E2ETests`, `MetricsRoundTripE2ETests`, `GateACompositionE2ETests`) that require a live Docker compose stack (Postgres@5433 + a live processor-sample container) — they fail with Npgsql/connection errors in this hermetic environment, NOT a regression. They compile 0-warning (the plan's deliverable for them) and their live execution is Phase 62's scope.
- MTP (xUnit.v3) filter syntax: `--filter "FQN~..."` is not supported; used `--filter-class "*ProcessorLiveness*"` instead.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The reader half of the v7.0.0 per-replica liveness reshape is complete and hermetically proven. Phase 62 (Live Proof & Close Gate) can now exercise the gate + probe against the real two-replica stack; the RealStack liveness polls and seeds are already re-pointed to the per-instance keyspace.
- Note: the self-watchdog probe (PROBE-01/02) is a separate deliverable in this phase's domain but is NOT part of this plan (61-01 is GATE-only) — it belongs to a later plan in the phase.

## Self-Check: PASSED

- FOUND: tests/BaseApi.Tests/Features/Orchestration/ProcessorLivenessGateUnitTests.cs
- FOUND: .planning/phases/61-1-.../61-01-SUMMARY.md
- CONFIRMED-DELETED: src/Messaging.Contracts/Projections/ProcessorProjection.cs
- FOUND commits: 35dec7e (Task 1), 6b62128 (Task 2), 222cc35 (Task 3)

---
*Phase: 61-1-healthy-orchestration-start-gate-self-watchdog-probe*
*Completed: 2026-06-13*
