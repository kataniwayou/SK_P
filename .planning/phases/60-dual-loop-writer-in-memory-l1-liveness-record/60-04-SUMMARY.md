---
phase: 60-dual-loop-writer-in-memory-l1-liveness-record
plan: 04
subsystem: infra
tags: [liveness, processor, startup, orchestrator, redis, di, l1, dotnet, stackexchange-redis]

# Dependency graph
requires:
  - phase: 60-02
    provides: "ProcessorLivenessWriter — the single shared write path (L2 SET(perInstance, derived TTL) + idempotent index SADD + unconditional L1 Update + log-and-continue)"
  - phase: 60-01
    provides: "IProcessorLivenessState (L1 holder) + ProcessorLivenessOptions.StartupIntervalSeconds (=30)"
  - phase: 60-03
    provides: "ProcessorLivenessHeartbeat ctor now takes (ProcessorLivenessWriter, instanceId) — the DI gap this plan closes"
  - phase: 59-per-instance-l2-keyspace-two-state-liveness-value
    provides: "ProcessorLivenessEntry.Create + L2ProjectionKeys.{PerInstance,InstanceIndex,Processor} + LivenessStatus/SchemaOutcome consts + InstanceId.Resolve()"
provides:
  - "ProcessorStartupOrchestrator writes an inline `unhealthy` ProcessorLivenessEntry at EACH resolution iteration (Loop A post-identity, Loop B per-definition, Gate-A clash) through the shared writer — a starting/restarting/clashed replica is visible in L2 from the first post-identity iteration (STATE-03/LOOP-01)"
  - "DI composition root registers the L1 holder (IProcessorLivenessState→ProcessorLivenessState) + the shared ProcessorLivenessWriter as singletons; the orchestrator + heartbeat are concrete singletons via ActivatorUtilities factory (instanceId injected once) surfaced as IHostedService — the cross-plan writer + instanceId DI gap is closed (container resolves both)"
  - "The dual-loop writer is complete: both background loops funnel through ONE ProcessorLivenessWriter"
affects: [61-gate-and-self-watchdog-probe, 62-live-proof-close-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Inline single-threaded `unhealthy` write per resolution iteration (orchestrator owns its resolution state — WR-03-safe to read context definition props ONLY here)"
    - "Gate-A clash forces configOutcome=Fail so an all-resolved-but-clashed replica is still published Unhealthy (Create derives status from the summary)"
    - "Plain-string ctor dependency (instanceId) injected via ActivatorUtilities.CreateInstance factory + concrete-type singleton, surfaced as IHostedService — keeps the registration observable as a concrete-type singleton descriptor"

key-files:
  created:
    - tests/BaseApi.Tests/Processor/StartupUnhealthyWriteFacts.cs
  modified:
    - src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
    - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
    - tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs
    - tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs
    - tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs

key-decisions:
  - "Dropped the pass/skip pre-bind `unhealthy` write (Rule 1 bug-fix): with all schemas resolved the naive Outcome helper yields all-Success ⇒ Create derives Healthy, which would publish Healthy BEFORE the queue bind (EXEC-01 violation) and contradict D-04 'status stays Unhealthy until MarkHealthy'. The handoff to the heartbeat at MarkHealthy owns the first Healthy write (D-01)."
  - "Gate-A clash write passes configOutcome=Fail explicitly: definitions are resolved at the clash point, so the naive Outcome would be all-Success ⇒ Healthy; the Gate-A outcome IS configSchema (D-04), so a Fail is the only way Create keeps the terminal-clash replica Unhealthy."
  - "Orchestrator + heartbeat registered as concrete singletons via ActivatorUtilities factory (instanceId is a plain string, not container-resolvable) + AddHostedService(sp=>GetRequiredService<T>): keeps a concrete-type singleton descriptor AddBaseProcessorFacts can assert (the IHostedService factory descriptor carries no ImplementationType)."
  - "instanceId resolved ONCE (InstanceId.Resolve()) and passed to BOTH loops so they write the IDENTICAL per-instance key (one replica identity)."

patterns-established:
  - "The orchestrator's inline unhealthy write mirrors the heartbeat's write disciplines (same clock, same shared writer, same log-and-continue) — the two loops cannot drift"
  - "Plain-string ctor dep via ActivatorUtilities factory + concrete-singleton + hosted-service indirection"

requirements-completed: [STATE-03, LOOP-01]

# Metrics
duration: 28min
completed: 2026-06-13
---

# Phase 60 Plan 04: Startup Inline Unhealthy Writer + DI Wiring Summary

**Wired the `ProcessorStartupOrchestrator` to write an inline `unhealthy` `ProcessorLivenessEntry` at each resolution iteration (Loop A post-identity, Loop B per-definition, Gate-A clash) through the shared `ProcessorLivenessWriter`, and closed the dual-loop DI gap — the L1 holder + shared writer are now singletons and both background loops are wired with `InstanceId.Resolve()` so a starting/restarting/clashed replica is visible in L2 as `unhealthy` from the first post-identity iteration (STATE-03/LOOP-01), completing the v7.0.0 dual-loop writer.**

## Performance

- **Duration:** ~28 min
- **Started:** 2026-06-13T11:38:34Z
- **Completed:** 2026-06-13T12:06:27Z
- **Tasks:** 3
- **Files created:** 1 (+6 modified)

## Accomplishments
- `ProcessorStartupOrchestrator` gained a private `WriteUnhealthyAsync(configOutcomeOverride)` helper (D-02 guarded by `context.Id` non-null) called at the first post-identity iteration (Loop A), at each Loop-B per-definition iteration, and on the Gate-A clash path — building the per-schema `summary` from its OWN single-threaded resolution state (WR-03-safe only here), interval 30 (`StartupIntervalSeconds`), routed through the shared `ProcessorLivenessWriter` (L2 SET ttl=max(60,30)=60 + idempotent index SADD + L1 Update + log-and-continue).
- DI: `AddSingleton<IProcessorLivenessState, ProcessorLivenessState>()` + `AddSingleton<ProcessorLivenessWriter>()`; the orchestrator + heartbeat are registered as concrete singletons via `ActivatorUtilities.CreateInstance(...)` factories (passing the once-resolved `InstanceId.Resolve()`) and surfaced as `IHostedService` — the writer + instanceId dependencies deferred by Plans 02/03 now resolve.
- All 3 direct-`new` orchestrator ctor sites fixed (Pitfall 1) via a shared `StubLivenessWriter()` seam in `IdentityResolutionFacts` + an instanceId string; the 7 ctor-site facts compile + green.
- New `StartupUnhealthyWriteFacts` (RedisFixture, `[Trait("Phase","60")]`, 2 facts): parks the orchestrator in Loop B (input schema never resolves) and proves present+Unhealthy+interval-30, summary input=Fail / output+config=Success, index SADD carries the instanceId, the OLD flat `Processor(id)` key is absent, L1 mirrors L2; plus a dead-Redis variant that still reaches Healthy (resilience).
- `AddBaseProcessorFacts` asserts the L1-holder + writer singleton descriptors + both concrete hosted-service singletons.
- Full Phase=60 trait suite 17/17 green; 582/582 non-E2E hermetic facts green; 0-warning Release AND Debug (`-warnaserror`) for src + tests.

## Task Commits

Each task was committed atomically:

1. **Task 1: Inline unhealthy write + DI registration; fix 3 ctor-site facts** - `6f9270b` (feat)
2. **Task 2: StartupUnhealthyWriteFacts (Redis)** - `48f2024` (test)
3. **Task 3: AddBaseProcessorFacts descriptor asserts + concrete-singleton hosted-service DI** - `199d2da` (test)

_Note: Task 1 (`tdd="true"`) is the implementation; its behaviors are asserted by Task 2's RedisFixture facts per the plan's writer-then-facts split. The DI hosted-service registration was refined in Task 3 (concrete-singleton via factory) to keep the descriptor observable — see Deviation #2._

## Files Created/Modified
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` - Two new ctor params (`ProcessorLivenessWriter writer`, `string instanceId`); `WriteUnhealthyAsync` helper called at Loop A (post-SetIdentity), each Loop-B iteration, and the Gate-A clash (configOutcome=Fail). Pass/skip pre-bind write deliberately omitted (Deviation #1).
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` - L1 holder + writer singletons; orchestrator + heartbeat as concrete singletons via ActivatorUtilities factory (instanceId resolved once) surfaced as IHostedService (DI gap closed).
- `tests/BaseApi.Tests/Processor/StartupUnhealthyWriteFacts.cs` - NEW: 5 observable behaviors across 2 facts (RedisFixture + dead-Redis stub).
- `tests/BaseApi.Tests/Processor/{IdentityResolutionFacts,SchemaResolutionFacts,DispatchBindSequenceFacts}.cs` - grown ctor + `StubLivenessWriter()` seam + instanceId.
- `tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs` - asserts the L1-holder + writer singleton descriptors + both concrete hosted-service singletons.

## Decisions Made
- **Dropped the pass/skip pre-bind unhealthy write** — see Deviation #1.
- **Gate-A clash forces `configOutcome=Fail`** — see Deviation #1 (same root cause).
- **Concrete-singleton + factory hosted-service registration** — see Deviation #2.
- **instanceId resolved once for both loops** — guarantees one replica identity / one per-instance key.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Removed the pass/skip pre-bind `unhealthy` write; force configOutcome=Fail on the Gate-A clash**
- **Found during:** Task 1 (orchestrator wiring)
- **Issue:** The plan's `WriteUnhealthyAsync` action text said to call the helper "after Gate A on both the clash-return and the pass/skip path". But its naive `Outcome()` helper yields all-`Success` once every non-null schema is resolved (which is the state at BOTH post-Gate-A points) ⇒ `ProcessorLivenessEntry.Create` derives `Healthy`. On the pass/skip path that publishes a Healthy entry on the per-instance key BEFORE the queue bind completes — violating EXEC-01 (the WebAPI gate admits only Healthy replicas and could Send to a not-yet-bound queue) and contradicting the must-have / D-04 "status stays Unhealthy throughout startup until MarkHealthy". On the clash path it would publish a terminal-but-Healthy entry — wrong for a replica that never serves.
- **Fix:** (a) Omitted the pass/skip pre-bind write entirely — the heartbeat owns the first Healthy write after MarkHealthy (the D-01 "clean handoff at MarkHealthy"). (b) The Gate-A clash write passes `configOutcomeOverride: SchemaOutcome.Fail` (the Gate-A outcome IS `configSchema`, D-04), the only way `Create` keeps the all-resolved-but-clashed replica `Unhealthy`. The inline Loop A / Loop B writes (where schemas are still unresolved ⇒ a Fail is present) carry the load-bearing "never absent + Unhealthy from the first post-identity iteration" behavior.
- **Files modified:** src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
- **Verification:** StartupUnhealthyWriteFacts proves Unhealthy during the resolution window; DispatchBindSequenceFacts (Gate-A clash + pass + bind-ordering) all green; full Phase=60 17/17.
- **Committed in:** `6f9270b` (Task 1 commit)

**2. [Rule 3 - Blocking] Hosted-service registration reshaped to a concrete singleton + factory (instanceId injection broke the descriptor assert)**
- **Found during:** Task 3 (AddBaseProcessorFacts)
- **Issue:** Both grown ctors take a plain `string instanceId` that is NOT container-resolvable, so the hosted services must be registered via a factory (`ActivatorUtilities.CreateInstance(sp, instanceId)`). A bare `AddHostedService(factory)` descriptor carries an `ImplementationFactory` but NO `ImplementationType`, so the existing `AddBaseProcessorFacts` assert (`d.ImplementationType == typeof(ProcessorStartupOrchestrator)`) failed. An attempted fix that resolved all `IHostedService` instances faulted on an unrelated `DefaultHealthCheckService` needing `ILogger<>` not registered in the minimal descriptor-test container.
- **Fix:** Register each background service as a CONCRETE singleton via the ActivatorUtilities factory, then `AddHostedService(sp => sp.GetRequiredService<T>())`. This exposes an observable concrete-type singleton descriptor (`ServiceType == typeof(ProcessorStartupOrchestrator)` / `...Heartbeat`, Singleton) the fact asserts WITHOUT building the provider or resolving hosted services. The plan's note "the hosted-service descriptors did not change" was incorrect — the instanceId injection forced the shape change.
- **Files modified:** src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs, tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs
- **Verification:** AddBaseProcessorFacts 4/4 green; full Phase=60 17/17 green; 0-warning build.
- **Committed in:** `199d2da` (Task 3 commit)

---

**Total deviations:** 2 auto-fixed (1 Rule 1 correctness bug, 1 Rule 3 blocking DI/descriptor break).
**Impact on plan:** Both were necessary for correctness — #1 prevents a premature-Healthy EXEC-01/D-04 violation, #2 is the unavoidable consequence of the plan's own plain-string instanceId ctor dependency. No scope creep: the production behavior is exactly the plan's "inline unhealthy write per iteration via the shared writer" plus the registrations it specified.

## Issues Encountered

**Tooling — test runner invocation (no code impact, carried from Plans 01/02/03).** `tests/BaseApi.Tests` is xUnit v3 on Microsoft.Testing.Platform (MTP), not VSTest; the plan's `<verify>` VSTest `--filter "FullyQualifiedName~..."` form is rejected. Ran via MTP-native flags: `dotnet test ... -- --filter-class "*StartupUnhealthyWriteFacts"` / `--filter-class "*AddBaseProcessorFacts"` / `-- --filter-trait "Phase=60"`. Same target facts, no code impact.

**Full-suite E2E failures — the accepted D-06 stale window (NOT a regression from this plan).** 7 `*E2ETests` fail because they poll the OLD flat `skp:{processorId}` key that Phase 60 deliberately stopped writing (D-05 hard-swap; the old flat-key WRITE was removed in Plan 60-03, commit `348bf89`). This is the LOCKED, accepted **D-06** decision (60-CONTEXT.md): the reader swaps to the per-instance `SMEMBERS`→`GET`-each gate in **Phase 61** and the RealStack/triple-SHA live proof lands in **Phase 62**. Several also need the full compose stack on the `rabbitmq` compose hostname (the `Failed to stop bus rabbitmq://rabbitmq/...` "Not Started" log noise) — an environment dependency. Logged to `deferred-items.md`. The hermetic suite is GREEN (582/582 non-E2E + 17/17 Phase=60).

## User Setup Required
None - no external service configuration required (integration facts use the existing compose Redis at localhost:6380).

## Next Phase Readiness
- **Phase 61 (reader swap + self-watchdog probe)** can now: build the `SMEMBERS skp:proc:{id}` → `GET`-each ≥1-healthy-and-fresh gate over the per-instance keys + index SET both loops write; read the L1 holder (`IProcessorLivenessState.Current`) for the staleness watchdog (PROBE-01/02); delete the now-orphaned `L2ProjectionKeys.Processor(Guid)` + `ProcessorProjection` (the writer's last caller is gone — the reader is the last remaining caller); and re-point the 7 live E2E pollers off the retired flat key.
- **Phase 62 (live proof + close gate)** proves the reshaped per-replica liveness end-to-end with the triple-SHA net-zero N=3 gate (TEST-01/02/03).
- No blockers. The dual-loop writer (startup `unhealthy` + heartbeat `healthy`) is complete and DI-resolvable.

## Known Stubs
None - the orchestrator is fully wired to the shared writer; `StubLivenessWriter()` / the dead-`IConnectionMultiplexer` are intentional test doubles inside the facts (not production code). The pass/skip pre-bind write was intentionally omitted (Deviation #1), not stubbed — the heartbeat owns the first Healthy write by design (D-01).

## Threat Flags
None - no new security surface beyond the plan's `<threat_model>` (T-60-12..16). Keys are built only via `L2ProjectionKeys`; `procId` is D-02-guarded non-null; status is always built through `ProcessorLivenessEntry.Create` (the clash path forces a Fail rather than fabricating a Healthy); the Redis fault is contained by the shared writer's log-and-continue; the orchestrator never writes the old flat key.

## Self-Check: PASSED

Created/modified files exist on disk; all 3 task commits (`6f9270b`, `48f2024`, `199d2da`) present in git history; Phase=60 suite 17/17 green; 0-warning Release + Debug builds.

---
*Phase: 60-dual-loop-writer-in-memory-l1-liveness-record*
*Completed: 2026-06-13*
