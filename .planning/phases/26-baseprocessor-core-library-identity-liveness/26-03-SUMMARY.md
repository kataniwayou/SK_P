---
phase: 26-baseprocessor-core-library-identity-liveness
plan: 03
subsystem: infra
tags: [dotnet, redis, processor, liveness, heartbeat, backgroundservice, di]

# Dependency graph
requires:
  - phase: 26-baseprocessor-core-library-identity-liveness
    plan: 02
    provides: "AddBaseProcessor composition root + ProcessorStartupOrchestrator that populates IProcessorContext (Id + definitions) and marks it Healthy"
  - phase: 26-baseprocessor-core-library-identity-liveness
    plan: 01
    provides: "IProcessorContext / ProcessorLivenessOptions contracts + frozen ProcessorProjection/LivenessProjection/L2ProjectionKeys/LivenessStatus shared types (D-09)"
provides:
  - "ProcessorLivenessHeartbeat BackgroundService (LIVE-01..06): only-when-Healthy sliding-SET liveness writer to skp:{id} reusing the frozen ProcessorProjection records + L2ProjectionKeys.Processor builder + LivenessStatus.Healthy const, registered by AddBaseProcessor"
  - "CLOSED LOOP proof: the heartbeat's written L2 value deserializes and passes the real, UNCHANGED v3.4.0 ProcessorLivenessValidator as live, and ages to stale past interval*2 (LIVE-05)"
affects: [phase-27-execution-round-trip, phase-28-sourcehash-sample-e2e]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Only-when-Healthy gate before every beat: if (context.IsHealthy && context.Id is { } id) — a not-yet-Healthy replica no-ops the tick (writes nothing), absent to the reader (LIVE-04)"
    - "Sliding SET..EX liveness write: blind whole-value StringSetAsync(key, json, expiry: Ttl) — last-write-wins, no lock/RMW (LIVE-06); interval written in SECONDS to satisfy the reader's timestamp+interval*2 math (LIVE-03)"
    - "D-11 log-and-continue in the beat loop: StringSetAsync wrapped in try/catch that LogWarning + continues (never throw/return) so a dead soft-dep Redis never crashes the host"
    - "Closed writer<->reader round-trip test: drive ONE heartbeat write to real Redis, then construct the REAL internal ProcessorLivenessValidator (InternalsVisibleTo) over a one-processor WorkflowGraphSnapshot, assert live then stale across a FakeTimeProvider clock advance"

key-files:
  created:
    - src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs
    - tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs
    - tests/BaseApi.Tests/Processor/LivenessReaderRoundTripFacts.cs
    - tests/BaseApi.Tests/Processor/LivenessResilienceFacts.cs
    - tests/BaseApi.Tests/Processor/FakeProcessorContext.cs
  modified:
    - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs

key-decisions:
  - "Reused the frozen ProcessorProjection/LivenessProjection records + L2ProjectionKeys.Processor builder + LivenessStatus.Healthy const verbatim (D-09) so the writer and the unchanged reader cannot desync on JSON field names, key bytes, or status value"
  - "Tracked the skp:{testProcessorId} key via the RedisFixture.Track builder call in each real-Redis fact (D-23 net-zero teardown) so the triple-SHA close gate sees BEFORE==AFTER"
  - "Added a hand-rolled FakeProcessorContext (settable IsHealthy/Id/definitions) instead of NSubstitute for the read-only context the heartbeat consumes — clearer per-fact shape configuration"
  - "Resilience test uses a CapturingLogger<T> (no FakeLogger package in the suite) to assert the D-11 warning naming the processor id; worker non-fault proven via heartbeat.ExecuteTask not Faulted"

requirements-completed: [LIVE-01, LIVE-02, LIVE-03, LIVE-04, LIVE-05, LIVE-06]

# Metrics
duration: 11min
completed: 2026-06-01
---

# Phase 26 Plan 03: Only-When-Healthy Liveness Heartbeat + Closed Reader Round-Trip Summary

**Built the `ProcessorLivenessHeartbeat` BackgroundService — the only-when-Healthy sliding-SET liveness writer (LIVE-01..06) that reuses the frozen `ProcessorProjection`/`LivenessProjection` records, the `L2ProjectionKeys.Processor` builder, and the `LivenessStatus.Healthy` const so the writer cannot desync from the reader — registered it in the `AddBaseProcessor` composition root, and CLOSED THE LOOP: a fact drives one heartbeat write to real Redis, deserializes it via the frozen record, and proves the real UNCHANGED v3.4.0 `ProcessorLivenessValidator` reads it as live then stale past interval*2. 4 new facts GREEN; full Processor slice 32/32 GREEN, no regression.**

## Performance

- **Duration:** ~11 min
- **Started:** 2026-06-01T19:33:00Z (approx)
- **Completed:** 2026-06-01
- **Tasks:** 2
- **Files created:** 5 (1 source + 4 test); 1 modified (composition root)

## Accomplishments
- `ProcessorLivenessHeartbeat : BackgroundService` (LIVE-01..06): injects `IConnectionMultiplexer` (soft-dep), `IProcessorContext`, `IOptions<ProcessorLivenessOptions>`, `TimeProvider`, `ILogger`. Each beat: a Healthy gate (`if (context.IsHealthy && context.Id is { } id)` — LIVE-04), then INSIDE a try it reads `clock.GetUtcNow().UtcDateTime` (SAME clock as the reader), builds the frozen `ProcessorProjection(InputDefinition, OutputDefinition, new LivenessProjection(now, IntervalSeconds, LivenessStatus.Healthy))` with interval in SECONDS (LIVE-03), serializes, and does a blind whole-value `StringSetAsync(L2ProjectionKeys.Processor(id), json, expiry: TtlSeconds)` (sliding SET..EX, LIVE-02/06); a Redis fault is `LogWarning`-and-continued (D-11). The loop delays on the injected clock via `Task.Delay(period, clock, ct)`.
- Registered the heartbeat in `AddBaseProcessor` via `services.AddHostedService<ProcessorLivenessHeartbeat>()` immediately after the startup orchestrator line — no other registration disturbed.
- Three fact slices (4 fact methods): `LivenessHeartbeatFacts` (not-Healthy writes no key + Healthy applies sliding TTL), `LivenessReaderRoundTripFacts` (the closed loop — deserialize, real-validator live, real-validator stale past interval*2), `LivenessResilienceFacts` (dead-Redis beat log-and-continued, worker non-fault, warning logged).

## Task Commits

1. **Task 1: ProcessorLivenessHeartbeat worker + register it in AddBaseProcessor** - `c3e55c7` (feat)
2. **Task 2: Closed writer-reader round-trip + only-when-Healthy + Redis-fault resilience tests** - `4ced004` (test)

_Plan metadata commit follows this SUMMARY._

## Files Created/Modified
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` - The only-when-Healthy sliding-SET liveness writer (LIVE-01..06)
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` - Added `AddHostedService<ProcessorLivenessHeartbeat>()` + the Liveness using
- `tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs` - not-Healthy no-write + Healthy sliding-TTL (real Redis, FakeTimeProvider, tracked key)
- `tests/BaseApi.Tests/Processor/LivenessReaderRoundTripFacts.cs` - the closed loop: deserialize + real ProcessorLivenessValidator live then stale
- `tests/BaseApi.Tests/Processor/LivenessResilienceFacts.cs` - dead-Redis log-and-continue + worker non-fault (CapturingLogger)
- `tests/BaseApi.Tests/Processor/FakeProcessorContext.cs` - settable IProcessorContext test double

## Decisions Made
- **Frozen-record reuse (D-09).** The writer constructs the exact `ProcessorProjection`/`LivenessProjection` records the reader deserializes, uses the `L2ProjectionKeys.Processor` builder (never a `"skp:"` literal), and the `LivenessStatus.Healthy` const (never a `"Healthy"` literal) — the writer/reader shapes cannot desync. Proven by the closed round-trip (T-26-08 mitigation).
- **Net-zero teardown.** Each real-Redis fact calls `_redis.Track(L2ProjectionKeys.Processor(testProcessorId))` so the per-class `RedisFixture` deletes exactly its own key on dispose (D-23) — the triple-SHA close gate sees BEFORE==AFTER.
- **Hand-rolled context double + capturing logger.** A `FakeProcessorContext` with settable `IsHealthy`/`Id`/definitions makes each fact's not-yet-Healthy vs Healthy shape explicit; a `CapturingLogger<T>` (the suite has no FakeLogger package) captures the D-11 warning to assert it names the processor id.
- **Fixed writer clock for the round-trip.** The round-trip fact writes at a fixed `FakeTimeProvider` instant, then constructs two readers with their own `FakeTimeProvider` clocks set just-inside and just-past `timestamp + interval*2` — proving the live/stale boundary deterministically without real sleeping.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `Track(L2ProjectionKeys.Processor(...))` literal grep criterion not met by a local-variable Track**
- **Found during:** Task 2 (acceptance-criteria grep checks)
- **Issue:** The acceptance criteria grep for `Track(...)` of `L2ProjectionKeys.Processor`; the first draft tracked a local `key` variable (`_redis.Track(key)`) so the literal grep returned 0 even though the tracked key was byte-identical.
- **Fix:** Inlined the builder into the Track call (`_redis.Track(L2ProjectionKeys.Processor(testProcessorId))`) in all three real-Redis facts so the criterion is satisfied literally; behavior unchanged.
- **Files modified:** LivenessHeartbeatFacts.cs, LivenessReaderRoundTripFacts.cs
- **Verification:** Re-built clean; the three slices re-ran 4/4 GREEN.
- **Committed in:** `4ced004` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (a blocking grep-criteria reconciliation; no behavioral change). No scope creep, no architectural changes. The reader (`ProcessorLivenessValidator`) was NOT modified — the closed loop satisfies the real, unchanged v3.4.0 validator.

## Verification Evidence
- Task 1 build: `dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj -c Debug` = 0 Warning / 0 Error.
- Task 1 greps: `L2ProjectionKeys.Processor` present, `"skp:"` literal = 0, `LivenessStatus.Healthy` present, `"Healthy"` literal = 0, `opts.IntervalSeconds` in the projection, `expiry: TimeSpan.FromSeconds(opts.TtlSeconds)` present, `context.IsHealthy` gate present, `LogWarning` inside the non-rethrowing catch, `AddHostedService<ProcessorLivenessHeartbeat>` registered.
- New slices: `*LivenessHeartbeatFacts* + *LivenessReaderRoundTripFacts* + *LivenessResilienceFacts*` = **Passed 4, Failed 0** (against real localhost:6380 Redis).
- Full Processor slice (`*Processor*`): **Passed 32, Failed 0** (28 prior Plans 01+02 + 4 new), no regression.
- Round-trip fact constructs the REAL `new ProcessorLivenessValidator(...)` and asserts BOTH live (no throw) AND stale (`OrchestrationValidationException`, gate `processorLiveness`, reason `stale`) after the clock advances past interval*2; asserts `Liveness.Interval == IntervalSeconds` (LIVE-03).

> **Wave-merge full-suite no-regression** (`dotnet test SK_P.sln` against the live Postgres/Redis/RabbitMQ stack including real-stack E2E) is the phase-close gate, run after this final Wave-3 plan at phase close — not this plan's per-plan verification.

## Known Stubs
None. The heartbeat writes real data sourced from the populated `IProcessorContext` (Id + input/output definitions + the live clock timestamp); no hardcoded empty values flow to the L2 sink. The only-when-Healthy gate intentionally writes nothing for a not-yet-Healthy replica — that is the LIVE-04 contract, not a stub.

## Issues Encountered
- None beyond the single auto-fixed grep-criteria reconciliation above.

## User Setup Required
None for unit/integration — the facts run against the host-side compose Redis at localhost:6380 (already required by the existing RedisFixture suite). No new external configuration.

## Next Phase Readiness
- Phase 27 (execution round-trip) can rely on the processor self-registering as live in L2 (the orchestrator's admission gate now passes for a Healthy processor) and on `IProcessorContext.WhenHealthy` for queue-bind-after-Healthy.
- No blockers.

## Self-Check: PASSED

All 5 created files verified present on disk; both task commits (c3e55c7, 4ced004) verified in git log; last-2-commit deletion check returned zero deletions.

---
*Phase: 26-baseprocessor-core-library-identity-liveness*
*Completed: 2026-06-01*
