---
phase: 45-keeper-bit-health-gate-global-pause-resume
plan: "01"
subsystem: keeper-health
tags: [keeper, bit-health, async-reset-event, taskcompletionsource, backgroundservice, edge-trigger, masstransit, redis, pause-all, resume-all]

# Dependency graph
requires:
  - phase: 45-00
    provides: PauseAll/ResumeAll no-H broadcast contracts + Keeper Health RED test stubs
  - phase: 36 (Keeper L2 probe loop)
    provides: L2ProbeRecovery.RunAsync (read + write-then-delete probe; RedisException-only catch)
provides:
  - Keeper.Health.IL2HealthGate            # KEEP-03 gate interface (Open/Close/WaitForOpenAsync(CT))
  - Keeper.Health.L2HealthGate             # swappable-TCS AsyncManualResetEvent, starts CLOSED (D-12)
  - Keeper.Health.BitHealthLoop            # KEEP-01/02 edge-triggered BIT probe BackgroundService
  - Keeper.Recovery.L2ProbeRecovery.ProbeOnceAsync  # extracted sentinel-parameterized single-probe core
  - Keeper DI registration of gate (AddSingleton) + loop (AddHostedService)
affects:
  - 46  # Phase-46 recovery consumer is the gate's only reader (WaitForOpenAsync); consumes the gate this plan writes
  - 45-02  # Orchestrator PauseAll/ResumeAll consumers consume the broadcasts this loop publishes

# Tech tracking
tech-stack:
  added: []   # no new NuGet packages
  patterns:
    - "Stephen Toub AsyncManualResetEvent: swappable TaskCompletionSource<bool> (RunContinuationsAsynchronously on EVERY construction), Interlocked.CompareExchange swap, Task.WaitAsync(ct) cancel-aware wait, no polling"
    - "Edge-triggered BackgroundService: bool? prevHealthy (null=first tick is a transition), publish only on prevHealthy != healthy, OCE-on-Task.Delay = graceful shutdown"
    - "RedisException-only probe (no catch(Exception)): a genuine bug propagates, never relabeled L2-down (Pitfall 5)"
    - "Sentinel-parameterized extraction: ProbeOnceAsync(ct, entryId?, h?) — RunAsync passes real fault-context values, the standing BIT loop passes Guid.Empty/\"bit\" sentinels"

key-files:
  created:
    - src/Keeper/Health/IL2HealthGate.cs
    - src/Keeper/Health/L2HealthGate.cs
    - src/Keeper/Health/BitHealthLoop.cs
  modified:
    - src/Keeper/Recovery/L2ProbeRecovery.cs
    - src/Keeper/Program.cs
    - tests/BaseApi.Tests/Keeper/Health/L2HealthGateTests.cs
    - tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs

key-decisions:
  - "OQ-1: ProbeOnceAsync is sentinel-parameterized (Guid? entryId=null, string? h=null) so RunAsync stays byte-identical and the BIT loop needs no inbound message (BitProbeEntryId=Guid.Empty, BitProbeH=\"bit\")."
  - "OQ-2: the BIT loop reuses Probe:DelaySeconds for its tick cadence — no new cadence knob owned here; ProbeOnceAsync does exactly one probe."
  - "ProbeOnceAsync owns the RedisException catch; RunAsync's for-loop now branches on the bool return (behavior-preserving: same metric increments, same delay, same ProbeOutcome)."

patterns-established:
  - "IL2HealthGate is registered AddSingleton (one writer = BitHealthLoop; reader = Phase-46 consumer) and BitHealthLoop is AddHostedService (proactive, no inbound trigger)."
  - "BitHealthLoopTests drive the loop with a scripted IDatabase double (per-tick health sequence) + a parked exhausted-read released before StopAsync, and assert PauseAll/ResumeAll Publish counts via an NSubstitute IBus."

requirements-completed: [KEEP-01, KEEP-02, KEEP-03]

# Metrics
duration: 27min
completed: 2026-06-08
---

# Phase 45 Plan 01: Keeper BIT Health Gate + Global Pause/Resume Summary

**The Keeper proactive health engine: a swappable-TCS `IL2HealthGate` (starts CLOSED, cancel-aware wait), an edge-triggered `BitHealthLoop` BackgroundService that probes L2 each tick and Publishes `PauseAll`/`ResumeAll` once per transition, and a sentinel-parameterized `ProbeOnceAsync` extracted from `L2ProbeRecovery` — turning the Wave-0 Keeper RED stubs GREEN.**

## Performance

- **Duration:** ~27 min
- **Started:** 2026-06-08T17:28:38Z
- **Completed:** 2026-06-08T17:56:00Z
- **Tasks:** 3
- **Files modified:** 7 (3 created, 4 modified)

## Accomplishments
- `IL2HealthGate` + `L2HealthGate`: Stephen Toub AsyncManualResetEvent — starts CLOSED (D-12, fail-safe), `Open()`/`Close()` idempotent, `RunContinuationsAsynchronously` on every TCS construction (no inline-continuation deadlock), `Interlocked.CompareExchange` atomic swap, `Task.WaitAsync(ct)` cancel-aware wait, zero polling. All 6 `L2HealthGateTests` GREEN.
- `ProbeOnceAsync` extracted from `L2ProbeRecovery.RunAsync`: a public sentinel-parameterized single L2 probe (READ + WRITE-then-DELETE) returning `true`/`false`, `RedisException`-only (a non-Redis bug propagates). `RunAsync` rewired to call it once per attempt — metrics/delay/outcome byte-identical. The 6 `KeeperProbeLoopTests` stay GREEN.
- `BitHealthLoop` BackgroundService: edge-triggered (`bool? prevHealthy`) — probes L2 each `Probe:DelaySeconds` tick, `gate.Close()` + `bus.Publish(PauseAll)` once on healthy→unhealthy, `gate.Open()` + `bus.Publish(ResumeAll)` once on unhealthy→healthy, nothing on same-state ticks; survives `RedisException`, propagates non-Redis bugs, `OCE`-on-`Task.Delay` = graceful shutdown; `bus.Publish` (never `Send`). Gate + loop DI-registered in `Keeper/Program.cs`. All 6 `BitHealthLoopTests` GREEN.

## Task Commits

Each task was committed atomically:

1. **Task 1: IL2HealthGate + L2HealthGate (swappable-TCS gate, KEEP-03)** - `7b4173f` (feat)
2. **Task 2: Extract ProbeOnceAsync from L2ProbeRecovery (OQ-1/OQ-2)** - `9b58f9f` (refactor)
3. **Task 3: BitHealthLoop BackgroundService (edge-trigger) + DI register gate & loop (KEEP-01/02)** - `39cbaa2` (feat)

_TDD note: the Wave-0 RED stubs already existed from Plan 45-00; each task's commit replaces the stub bodies with real assertions against the just-built production type (the GREEN gate), so each task is a single test+impl GREEN commit rather than a separate RED commit._

## Files Created/Modified
- `src/Keeper/Health/IL2HealthGate.cs` - KEEP-03 gate interface: `Open()`/`Close()`/`WaitForOpenAsync(CancellationToken)`.
- `src/Keeper/Health/L2HealthGate.cs` - swappable-TCS AsyncManualResetEvent; starts CLOSED; idempotent open/close; cancel-aware wait; no polling.
- `src/Keeper/Health/BitHealthLoop.cs` - edge-triggered BIT probe BackgroundService driving the gate + `PauseAll`/`ResumeAll` broadcasts.
- `src/Keeper/Recovery/L2ProbeRecovery.cs` - added public `ProbeOnceAsync(ct, entryId?, h?)`; `RunAsync` rewired to call it per attempt (behavior-preserving).
- `src/Keeper/Program.cs` - `AddSingleton<IL2HealthGate, L2HealthGate>` + `AddHostedService<BitHealthLoop>`.
- `tests/BaseApi.Tests/Keeper/Health/L2HealthGateTests.cs` - 6 real KEEP-03 gate assertions (CLOSED start, open-completes-wait, close-re-blocks, cancel-throws-OCE, open/close idempotent).
- `tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs` - 6 real KEEP-01/02 assertions via a scripted per-tick IDatabase double + NSubstitute `IBus` Publish-count assertions.

## Decisions Made
- **OQ-1 (sentinel extraction):** `ProbeOnceAsync(CancellationToken ct, Guid? entryId = null, string? h = null)`. `RunAsync` passes its real `entryId`/`h`; the BIT loop passes nothing, defaulting to `BitProbeEntryId = Guid.Empty` and `BitProbeH = "bit"`. The read target need not exist — a present/absent read still proves L2 reachability.
- **OQ-2 (cadence):** the BIT loop reuses `Probe:DelaySeconds` for its tick interval; no new cadence option is introduced. `ProbeOnceAsync` does exactly one probe.
- **Catch ownership:** `ProbeOnceAsync` owns the `catch (RedisException) => false`; `RunAsync` now branches on the bool. Verified behavior-preserving (the `KeeperProbeLoopTests` cover RunAsync's metrics/outcome and stay GREEN).
- **First-tick semantics:** `prevHealthy = null` makes the first probe always a transition, so the first healthy tick broadcasts `ResumeAll` and the first unhealthy tick broadcasts `PauseAll` without special-casing (satisfies D-12 gate-starts-CLOSED-opens-on-first-success).

## Deviations from Plan

None - plan executed exactly as written. The two open questions (OQ-1 sentinel shape, OQ-2 cadence reuse) were resolved exactly as the plan's `<action>` blocks directed.

## Issues Encountered
- **Test harness — deterministic loop control:** driving an edge-triggered `BackgroundService` to exactly N ticks needed a scripted `IDatabase` double whose READ returns a per-tick health sequence, then PARKS on an internal CTS once the script is exhausted (signalling an `Exhausted` TCS) so no phantom tick runs; the test releases the park and calls `StopAsync` for a clean shutdown. The non-Redis-propagation test additionally had to drain both `StartAsync` and `ExecuteTask` because `BackgroundService.StartAsync` surfaces a synchronously-faulting `ExecuteAsync` directly. All resolved within Task 3; no production-code change required.
- **Pre-existing test-infra flake (out of scope):** `KeeperMetricsFacts.RecoveredFlow_*` intermittently reports "Failed to stop bus … (Not Started)" under parallel load (a MassTransit bus-harness teardown race needing a live RabbitMQ); it passes in isolation and passed in the clean Release run. Unrelated to the `ProbeOnceAsync` extraction (the reflection/FakeRedis RunAsync coverage is GREEN). Not modified.

## Verification

- `dotnet test --filter-query "/*/BaseApi.Tests.Keeper.Health/*/*"` (gate + loop) — **12/12 GREEN** (6 `L2HealthGateTests` + 6 `BitHealthLoopTests`).
- `KeeperProbeLoopTests` (RunAsync regression guard) — **6/6 GREEN** after the `ProbeOnceAsync` extraction.
- `dotnet build src/Keeper/Keeper.csproj -c Debug` — 0 warnings / 0 errors.
- `dotnet build SK_P.sln -c Release` — **0 warnings / 0 errors**.
- Full hermetic suite (`--filter-not-trait Category=RealStack`, Release) — **506 total, 500 passed, 6 failed**. The 6 failures are EXACTLY the Plan-45-02 Wave-0 Orchestrator RED stubs (`PauseAllConsumerTests`, `ResumeAllConsumerTests`, `ResumeNoBurstTests`) which 45-02 turns GREEN — they are deliberately RED at this point, not regressions. Every Keeper-side test is GREEN.
- Acceptance greps confirmed: `RunContinuationsAsynchronously` on every TCS; no `volatile bool`/`SpinWait`/`while (` in the gate; no `catch (Exception)` in `L2ProbeRecovery`; `: BackgroundService` + `bool? prevHealthy` + edge check + `gate.Open()`/`gate.Close()` + `bus.Publish(new ResumeAll`/`PauseAll` and NO `Send` in `BitHealthLoop`; both DI registrations in `Program.cs`.

## Known Stubs

None. The three production classes are complete and behavior-verified. (The remaining RED tests in the suite are Plan-45-02's Orchestrator-side stubs, not stubs introduced by this plan.)

## Threat Flags

None beyond the plan's `<threat_model>`. T-45-03 (false "L2 down" → false pause storm) is mitigated: `ProbeOnceAsync` catches `RedisException` only (grep-verified no `catch (Exception)`), so a genuine bug propagates out of `ExecuteAsync`. T-45-04 (per-tick broadcast spam) is mitigated by the edge-trigger (`Same_State_Ticks_Publish_Nothing` GREEN). T-45-05/T-45-06 (log leakage / injection) mitigated: the two log lines are constant template strings, no interpolation, no Redis internals. No new network endpoints, auth paths, or schema changes.

## Next Phase Readiness
- The `IL2HealthGate` singleton is in place for the Phase-46 recovery consumer (its only reader) — `WaitForOpenAsync(ct)` is the gate-open-only precondition.
- The `PauseAll`/`ResumeAll` broadcasts are live on the bus; Plan 45-02 wires the Orchestrator per-replica fan-out consumers that turn the 6 still-RED Orchestrator stubs GREEN.

## Self-Check: PASSED

- FOUND: src/Keeper/Health/IL2HealthGate.cs
- FOUND: src/Keeper/Health/L2HealthGate.cs
- FOUND: src/Keeper/Health/BitHealthLoop.cs
- FOUND: src/Keeper/Recovery/L2ProbeRecovery.cs (modified)
- FOUND commit: 7b4173f (feat 45-01 gate)
- FOUND commit: 9b58f9f (refactor 45-01 ProbeOnceAsync)
- FOUND commit: 39cbaa2 (feat 45-01 BitHealthLoop + DI)

---
*Phase: 45-keeper-bit-health-gate-global-pause-resume*
*Completed: 2026-06-08*
