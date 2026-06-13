---
phase: 60-dual-loop-writer-in-memory-l1-liveness-record
plan: 03
subsystem: infra
tags: [liveness, processor, heartbeat, redis, ttl, sadd, l1, dotnet, stackexchange-redis]

# Dependency graph
requires:
  - phase: 60-02
    provides: "ProcessorLivenessWriter — the single shared write path (L2 SET(perInstance, derived TTL) + idempotent index SADD + unconditional L1 Update + log-and-continue)"
  - phase: 60-01
    provides: "IProcessorLivenessState (L1 holder) + ProcessorLivenessOptions.{Interval=10, StartupInterval=30, Ttl=30}"
  - phase: 59-per-instance-l2-keyspace-two-state-liveness-value
    provides: "ProcessorLivenessEntry.Create + L2ProjectionKeys.{PerInstance,InstanceIndex} + LivenessStatus/SchemaOutcome consts + InstanceId.Resolve()"
provides:
  - "ProcessorLivenessHeartbeat swapped onto the per-instance contract: post-Healthy beats build a frozen-healthy ProcessorLivenessEntry (all-SUCCESS => Healthy, interval 10) and route through the shared writer — L2 perInstance SET + index SADD + L1 Update + TTL=30 come for free"
  - "Old flat ProcessorProjection / L2ProjectionKeys.Processor(id) heartbeat write removed ENTIRELY (no dual-write, D-05/Pitfall 5)"
  - "ProcessorLivenessHeartbeat ctor now takes a caller-resolved instanceId (DI passes InstanceId.Resolve(); tests pin a deterministic one)"
affects: [60-04-di-registration, 61-gate-and-self-watchdog-probe]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Both background loops (startup + heartbeat) funnel through ONE ProcessorLivenessWriter so L2-SET + index-SADD + L1-Update + derived-TTL cannot drift between them"
    - "Frozen-healthy beat (D-14): a fixed all-SUCCESS summary fed to ProcessorLivenessEntry.Create — the heartbeat never re-reads context definition props on its own thread (WR-03 / no cross-thread stale read)"
    - "Caller-resolved instanceId injected into the heartbeat ctor (deterministic-in-test, InstanceId.Resolve() at the DI site)"

key-files:
  created: []
  modified:
    - src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs
    - tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs
    - tests/BaseApi.Tests/Processor/LivenessResilienceFacts.cs
  deleted:
    - tests/BaseApi.Tests/Processor/LivenessReaderRoundTripFacts.cs

key-decisions:
  - "Heartbeat ctor accepts instanceId as a parameter (caller-resolved) rather than calling InstanceId.Resolve() internally — required so Task-2 facts can pin a deterministic instanceId ('pod-hb') and assert the exact PerInstance key (the plan's Task-2 explicitly passes a resolved instanceId string to the ctor)"
  - "Removed LivenessReaderRoundTripFacts: it asserted the heartbeat->flat-key->old-ProcessorLivenessValidator closed loop that D-05 severs (no dual-write). The validator keeps independent mock-based coverage (ProcessorLivenessFacts); the new per-instance round-trip lands with the Phase-61 reader swap (D-06/D-07)"
  - "LivenessResilienceFacts captures the Redis-fault warning on the WRITER's logger: the shared writer swallows the fault before the heartbeat's belt-and-braces catch can fire, so the warning now originates there"

patterns-established:
  - "Frozen-healthy heartbeat beat routed through the shared writer — identical L2/index/L1/TTL disciplines to the startup writer"

requirements-completed: [LOOP-02, LOOP-04]

# Metrics
duration: 9min
completed: 2026-06-13
---

# Phase 60 Plan 03: Heartbeat Swap to Per-Instance Frozen-Healthy via Shared Writer Summary

**Swapped `ProcessorLivenessHeartbeat` off the old flat `ProcessorProjection`/`skp:{id}` write onto the v7.0.0 per-instance contract: each post-Healthy beat builds a frozen-healthy `ProcessorLivenessEntry` (all-SUCCESS => Healthy, interval 10) and routes it through the shared `ProcessorLivenessWriter`, so the L2 per-instance SET (TTL=30), the idempotent index SADD, and the L1 Update all come for free and match the startup writer — old dual-write removed entirely.**

## Performance

- **Duration:** ~9 min
- **Started:** 2026-06-13T11:24:18Z
- **Completed:** 2026-06-13T11:33:53Z
- **Tasks:** 2
- **Files modified:** 3 (+1 deleted)

## Accomplishments
- `ProcessorLivenessHeartbeat` now, inside the unchanged `IsHealthy && Id is { } id` gate (D-14/T-60-08), builds `ProcessorLivenessEntry.Create(SUCCESS, SUCCESS, SUCCESS, now, IntervalSeconds)` and calls `_writer.WriteAsync(id, _instanceId, entry)` — the shared writer (Plan 02) owns L2 perInstance SET (TTL=max(20,30)=30, D-13/T-60-09), idempotent index SADD (D-15), and L1 Update (D-09).
- The old `ProcessorProjection` construction + `StringSetAsync(L2ProjectionKeys.Processor(id), ...)` and the now-unused `_redis`/`IConnectionMultiplexer` field are GONE — no dual-write (D-05/Pitfall 5/T-60-10). The writer owns Redis.
- Kept the belt-and-braces `catch -> LogWarning -> continue` (D-11), the `Task.Delay(period, _clock, stoppingToken)` loop tail, and frozen-healthy semantics (no re-read of context definition props on the heartbeat thread — WR-03/T-60-11).
- `LivenessHeartbeatFacts` re-pointed: `PerInstance(id, instanceId)` key, `Deserialize<ProcessorLivenessEntry>` with `Status==Healthy` + `Interval==10` + TTL band (25,30], `SMEMBERS InstanceIndex` contains the instanceId, L1 `Current` mirrors L2, and each subsequent beat advances the timestamp (frozen Healthy). Net-zero tracks both the perInstance key and the index SET key.
- All 15 `Phase=60` trait facts green (12 prior + 2 new heartbeat + 1 re-traited resilience); Release AND Debug builds 0-warning under `-warnaserror`.

## Task Commits

Each task was committed atomically:

1. **Task 1: Swap heartbeat write to PerInstance + frozen-healthy entry via shared writer; remove old write** - `348bf89` (feat)
2. **Task 2: Re-point LivenessHeartbeatFacts to PerInstance + ProcessorLivenessEntry + L1 + SADD** - `421823f` (test)

_Note: Task 1 (`tdd="true"`) is the implementation; its behaviors are asserted by Task 2's RedisFixture facts per the plan's writer-then-facts split (same shape as Plan 02). The Task-1 src file received one refinement during Task 2 — the instanceId ctor param — folded into the Task-2 commit since it exists to satisfy the fact's deterministic-instanceId assertion._

## Files Created/Modified
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` - Frozen-healthy beat built via `ProcessorLivenessEntry.Create` and routed through `ProcessorLivenessWriter.WriteAsync`; old flat-key dual-write + `_redis` field removed; ctor now takes `ProcessorLivenessWriter writer` + `string instanceId`.
- `tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs` - Two facts re-pointed to the per-instance contract (PerInstance key, ProcessorLivenessEntry, index SADD, L1 mirror, multi-beat timestamp refresh); `[Trait("Phase","60")]` added.
- `tests/BaseApi.Tests/Processor/LivenessResilienceFacts.cs` - Constructs over the shared writer; captures the Redis-fault warning on the writer's logger; `[Trait("Phase","60")]` added.
- `tests/BaseApi.Tests/Processor/LivenessReaderRoundTripFacts.cs` - **Deleted** (obsolete: tested the heartbeat->flat-key->old-validator loop D-05 severs).

## Decisions Made
- **Heartbeat ctor takes a caller-resolved `instanceId`** (vs. internal `InstanceId.Resolve()`): the plan's Task-2 explicitly passes a resolved instanceId string to the grown ctor, and a test must pin a deterministic instanceId (`"pod-hb"`) to assert the exact `PerInstance` key. The Plan-04 DI site will pass `InstanceId.Resolve()`. (See Deviation #1.)
- **Removed the obsolete `LivenessReaderRoundTripFacts`** rather than re-point it: its premise (heartbeat write -> old `ProcessorLivenessValidator` sees LIVE) is permanently severed by D-05; the validator is untouched (D-07) and retains independent mock-based coverage in `ProcessorLivenessFacts`. (See Deviation #2.)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Heartbeat ctor must accept the instanceId as a parameter**
- **Found during:** Task 2 (re-pointing the facts)
- **Issue:** Task 1 (per its action text) resolved the instanceId internally via `InstanceId.Resolve()` (machine name). But Task-2 asserts the exact `PerInstance(testProcessorId, "pod-hb")` key — with an internally-resolved instanceId the key written (machine name) never matched the asserted key, so `KeyExistsAsync` was false and the fact failed at the first assertion. The plan's Task-2 text itself says the test passes "a resolved instanceId string (e.g. `"pod-hb"`)" to the grown ctor — so the ctor must accept it.
- **Fix:** Added `string instanceId` ctor parameter (null-checked), dropped the internal `InstanceId.Resolve()` call (and its `Messaging.Contracts.Identity` using). DI (Plan 04) will resolve and pass it.
- **Files modified:** src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs, tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs, tests/BaseApi.Tests/Processor/LivenessResilienceFacts.cs
- **Verification:** Both heartbeat facts green; 0-warning build.
- **Committed in:** 421823f (Task 2 commit)

**2. [Rule 3 - Blocking] Two sibling test files `new`'d the heartbeat with the old ctor**
- **Found during:** Task 2 (first build of the test project)
- **Issue:** `LivenessResilienceFacts` and `LivenessReaderRoundTripFacts` (not named in the plan's `<read_first>`) construct `ProcessorLivenessHeartbeat` with the old `(IConnectionMultiplexer, ...)` ctor — the Task-1 ctor change broke their compilation (CS1503).
- **Fix:** `LivenessResilienceFacts` re-pointed to construct over the shared `ProcessorLivenessWriter` and capture the warning on the writer's logger (the writer swallows the fault first), then re-traited `Phase=60`. `LivenessReaderRoundTripFacts` removed — its heartbeat->flat-key->old-validator closed loop is permanently severed by D-05/D-06 (no dual-write); the validator keeps independent mock-based coverage in `ProcessorLivenessFacts`, and the new per-instance round-trip belongs to the Phase-61 reader swap.
- **Files modified:** tests/BaseApi.Tests/Processor/LivenessResilienceFacts.cs; tests/BaseApi.Tests/Processor/LivenessReaderRoundTripFacts.cs (deleted)
- **Verification:** Test project 0-warning build; resilience fact green; full Phase=60 suite 15/15 green.
- **Committed in:** 421823f (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (both Rule 3 - blocking compilation/semantic breaks caused directly by the Task-1 ctor change and the D-05 hard-swap).
**Impact on plan:** Both were unavoidable consequences of the planned heartbeat ctor change + D-05 dual-write removal — the plan's `<read_first>` named only `LivenessHeartbeatFacts.cs`, missing the two sibling files that also construct the heartbeat. No scope creep: no production behavior beyond the plan's stated swap; the deleted round-trip fact tested a loop the milestone explicitly retires.

## Issues Encountered

**Tooling — test runner invocation (no code impact, carried from Plans 01/02).** `tests/BaseApi.Tests` is xUnit v3 on Microsoft.Testing.Platform (MTP), not VSTest; the plan's `<verify>` VSTest `--filter "FullyQualifiedName~..."` form is rejected. Ran via the MTP-native flags: `dotnet test ... -- --filter-class "*LivenessHeartbeatFacts"` and `-- --filter-trait "Phase=60"`. Same target facts, no code impact.

**Multi-beat timing under FakeTimeProvider.** Asserting "each beat advances the timestamp" required re-arming the loop's `Task.Delay` reliably: the fix polls the L1 holder (which advances synchronously inside `WriteAsync`, network-independent) while repeatedly `Advance`-ing the fake clock until the timestamp moves past the first beat's — robust against the loop not yet being parked in `Task.Delay` when the first `Advance` runs.

## User Setup Required
None - no external service configuration required (integration facts use the existing compose Redis at localhost:6380).

## Verification
- `dotnet test ... -- --filter-class "*LivenessHeartbeatFacts"` -> Passed! 2/2.
- `dotnet test ... -- --filter-class "*LivenessResilienceFacts"` -> Passed! 1/1.
- `dotnet test ... -- --filter-trait "Phase=60"` -> Passed! 15/15.
- `dotnet build src/BaseProcessor.Core -c Release -warnaserror` -> 0 Warning(s), 0 Error(s).
- `dotnet build tests/BaseApi.Tests -c Release -warnaserror` -> 0 Warning(s).
- `dotnet build tests/BaseApi.Tests -c Debug -warnaserror` -> 0 Warning(s).
- Acceptance greps — Task 1 (src): `_writer.WriteAsync(`=1, `L2ProjectionKeys.Processor(`=0, `ProcessorProjection`=0, `_options.IntervalSeconds`=2. Task 2 (test): `L2ProjectionKeys.Processor(`=0, `PerInstance`=6, `Deserialize<ProcessorLivenessEntry>`=1, `ProcessorProjection`=0.

## Next Phase Readiness
- Plan 04 (DI registration) must register `ProcessorLivenessWriter` (AddSingleton) + `IProcessorLivenessState`/`ProcessorLivenessState` (AddSingleton) AND supply the `instanceId` string to the `ProcessorLivenessHeartbeat` registration (resolve `InstanceId.Resolve()` at the registration site). NOTE: `AddHostedService<ProcessorLivenessHeartbeat>()` is ALREADY registered (BaseProcessorServiceCollectionExtensions.cs:182) but its `ProcessorLivenessWriter`/`instanceId` dependencies are NOT yet in the container — this is the expected cross-plan gap (DI deferred to Plan 04 per the plan); the app is not run between plans and tests construct the heartbeat directly.
- Phase 61 (reader swap) can now build the per-instance `SMEMBERS`->`GET`-each gate over the index SET + per-instance keys the heartbeat (and startup loop) write, and delete the now-orphaned `L2ProjectionKeys.Processor(Guid)` + `ProcessorProjection` (their last writer caller is gone; reader is the last remaining caller). It re-establishes a per-instance round-trip fact to replace the deleted `LivenessReaderRoundTripFacts`.
- No blockers.

## Known Stubs
None - the heartbeat is fully wired to the shared writer; frozen-healthy is the intended fixed all-SUCCESS summary (not a stub — D-14, mid-life re-validation is out of scope for the milestone).

## Threat Flags
None - no new security surface beyond the plan's `<threat_model>` (T-60-08..11). The `IsHealthy` gate (unchanged) remains the sole authorization for the healthy write; keys are built only via `L2ProjectionKeys`; the frozen-healthy summary carries no secrets; the Redis fault is contained by the shared writer's log-and-continue.

## Self-Check: PASSED

Both modified src/test files exist on disk; the obsolete round-trip fact is removed; both task commits (`348bf89`, `421823f`) present in git history; Phase=60 suite 15/15 green; 0-warning Release + Debug builds.

---
*Phase: 60-dual-loop-writer-in-memory-l1-liveness-record*
*Completed: 2026-06-13*
