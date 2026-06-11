---
phase: 52-three-state-keeper
plan: 03
subsystem: bit-health-loop
tags: [masstransit, endpoint-pause-resume, bit-health-gate, keeper, recovery, nsubstitute, edge-trigger]

# Dependency graph
requires:
  - phase: 52-three-state-keeper
    plan: 02
    provides: "RecoveryEndpointHandle DI singleton holding the runtime-connected keeper-recovery HostReceiveEndpointHandle (Stop/Start-able)"
  - phase: 45-keeper-bit-health-gate-global-pause-resume
    provides: "BitHealthLoop edge-triggered BackgroundService + IL2HealthGate + L2ProbeRecovery.ProbeOnceAsync"
provides:
  - "BitHealthLoop drives keeper-recovery ReceiveEndpoint.Stop on the unhealthy edge and ReceiveEndpoint.Start on the healthy edge (D-04 / KEEP-04 driver) — completing the gate-closed non-destructive-consume invariant end to end"
  - "Endpoint Stop/Start is ADDITIVE to the existing gate.Open/Close + PauseAll/ResumeAll signalling on the same edges (gate preserved, OQ-3 out of scope)"
  - "BitHealthLoopTests Stop-on-unhealthy / Start-on-healthy call-count facts with a substituted IReceiveEndpoint (preserves WR-01 + first-tick semantics)"
affects: [53, keeper-recovery, bit-health-loop, endpoint-pause-resume]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "BIT health edge as the single driver for BOTH orchestrator-wide pause/resume (PauseAll/ResumeAll) AND the keeper's OWN recovery-endpoint Stop/Start — one edge, three coordinated actions, all under the one WR-01 try"
    - "Null-guarded handle (is { } h) for the brief startup window before RecoveryEndpointBinder populates the singleton (accepted residual T-52-11) — never throw on a null handle during startup"
    - "Unit-substituting the MassTransit pause seam: IReceiveEndpoint / HostReceiveEndpointHandle / ReceiveEndpointHandle / ReceiveEndpointReady are all interfaces in 8.5.5, so a fully-faked handle makes Stop/Start call counts assertable without a harness"

key-files:
  created: []
  modified:
    - src/Keeper/Health/BitHealthLoop.cs
    - tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs

key-decisions:
  - "IReceiveEndpoint.Start(ct) returns a ReceiveEndpointHandle (NOT a Task) in 8.5.5, while Stop(ct) returns a Task. The healthy edge therefore awaits Start(stoppingToken).Ready so a resume failure ALSO lands in the WR-01 catch (prevHealthy un-advanced). Same API-shape asymmetry Plan 02 hit in KeeperPauseAccumulateFacts."
  - "Endpoint Stop/Start placed BEFORE the bus.Publish inside the existing edge try (D-04 / WR-01): a Stop/Start throw leaves prevHealthy un-advanced and the next tick re-applies the (idempotent) edge — no permanent gate lockout (T-52-10)."
  - "gate.Open()/Close() PRESERVED verbatim (OQ-3 out of D-08 scope) — the endpoint Stop/Start is purely additive; the gate is still read by the recovery consumers/tests."
  - "Task 3 is a verification-only phase gate (no production code) — nothing to commit beyond the Task-2 test file. The keeper suite + whole-solution build are the deliverable."

patterns-established:
  - "KEEP-04 closed end-to-end: gate-closed -> endpoint Stop (broker accumulates, no dequeue-and-drop) -> gate-open -> endpoint Start (drain), all driven by the BIT loop on the same transition it already uses for gate + PauseAll/ResumeAll"

requirements-completed: [KEEP-04]

# Metrics
duration: ~25min
completed: 2026-06-11
---

# Phase 52 Plan 03: BitHealthLoop Recovery-Endpoint Stop/Start Driver Summary

**BitHealthLoop now drives the Plan-02 keeper-recovery `HostReceiveEndpointHandle` — `ReceiveEndpoint.Stop(ct)` on the unhealthy edge (broker accumulates non-destructively) and `ReceiveEndpoint.Start(ct).Ready` on the healthy edge (drain) — additive to the existing `gate.Close/Open` + `PauseAll`/`ResumeAll` on the same transition, under the same WR-01 guard, closing the KEEP-04 gate-closed non-destructive-consume invariant end to end.**

## Performance

- **Duration:** ~25 min
- **Started:** 2026-06-11T16:54:22Z
- **Tasks:** 3 (2 code + 1 verification gate)
- **Files:** 2 modified

## Accomplishments
- **D-04 / KEEP-04 driver (Task 1):** injected `RecoveryEndpointHandle endpointHandle` into `BitHealthLoop`'s ctor. Inside the EXISTING edge `try`, adjacent to `gate.Open()`/`gate.Close()` and BEFORE the `bus.Publish`: the unhealthy branch calls `if (endpointHandle.Handle is { } h) await h.ReceiveEndpoint.Stop(stoppingToken);` (basic.cancel; ops accumulate on the broker), the healthy branch calls `if (endpointHandle.Handle is { } h) await h.ReceiveEndpoint.Start(stoppingToken).Ready;` (resume + drain). The `is { } h` null-guard absorbs the brief startup window before `RecoveryEndpointBinder` sets the handle (T-52-11). `prevHealthy = healthy;` stays AFTER all edge actions (WR-01 — a Stop/Start throw lands in the existing `catch (Exception)` and the next tick re-applies the idempotent edge). `gate.Open()/Close()` + `PauseAll`/`ResumeAll` preserved verbatim (OQ-3 out of scope).
- **Edge-trigger facts (Task 2):** added a `FakeHandle()` helper that builds a populated `RecoveryEndpointHandle` over a substituted `IReceiveEndpoint` (`Start` returns a substitute `ReceiveEndpointHandle` whose `.Ready` completes with a substitute `ReceiveEndpointReady`; `Stop` returns `Task.CompletedTask`). `NewLoop` takes + passes the holder; all five pre-existing facts updated to pass a fresh fake. Two new `[Trait("Phase","52")]` facts: `Healthy_To_Unhealthy_Edge_Stops_Recovery_Endpoint` (1 Stop on the unhealthy edge + 1 Start on the first-tick healthy transition) and `Same_State_Ticks_No_Stop_Start` (exactly 1 Stop + 2 Start over the 5-tick `[null,null,down,down,null]` script — proving same-state ticks issue NO extra Stop/Start). BitHealthLoopTests **8/8 green**.
- **Phase gate (Task 3):** full keeper-namespace suite **32/32 green**; whole-solution `dotnet build SK_P.sln -c Debug` **0 warnings / 0 errors**. The three plans compose: `BitHealthLoop`'s new ctor param resolves the `RecoveryEndpointHandle` singleton registered by Plan 02 in `Program.cs`; no duplicate keeper-recovery endpoint config; no stale call sites.

## Task Commits

1. **Task 1: drive recovery endpoint Stop/Start on BIT health edges** - `63bff7a` (feat)
2. **Task 2: assert recovery endpoint Stop/Start per BIT health edge** - `fed352e` (test)
3. **Task 3: full keeper-suite green gate** - no commit (verification-only; no production code, test file committed in Task 2)

## Files Created/Modified
- `src/Keeper/Health/BitHealthLoop.cs` (modified) - ctor gains `RecoveryEndpointHandle endpointHandle`; unhealthy edge `Stop`s / healthy edge `Start`s the recovery endpoint (null-guarded, inside the WR-01 try, before the Publish); class xml-doc updated to note the D-04 endpoint driver
- `tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs` (modified) - `FakeHandle()` helper + `NewLoop` holder param + five call-site updates + two new Stop/Start edge-count facts

## Decisions Made
- **Start(ct) API shape (Rule 3 blocking):** `IReceiveEndpoint.Start(ct)` returns a `ReceiveEndpointHandle` (verified against the installed 8.5.5 assembly — `MassTransit.IReceiveEndpoint.Start` → `MassTransit.ReceiveEndpointHandle`), NOT a `Task` as the plan's `<interfaces>` block stated. The healthy branch awaits `Start(stoppingToken).Ready` so a resume failure is awaited inside the WR-01 try (un-advancing prevHealthy on failure). `Stop(ct)` does return a `Task` and is awaited directly. This is the same asymmetry Plan 02 documented in `KeeperPauseAccumulateFacts`.
- **Substitutable test seam:** confirmed against the 8.5.5 assembly that `IReceiveEndpoint`, `HostReceiveEndpointHandle`, `ReceiveEndpointHandle`, and `ReceiveEndpointReady` are ALL interfaces — so `FakeHandle()` substitutes the whole chain with NSubstitute (no real harness needed for the unit-level Stop/Start count assertions).
- **Verification-only Task 3:** the plan scopes Task 3 as a phase gate with no new production code. With Task 1+2 green and the suite/build clean, there is nothing to commit for Task 3 — the gate is the deliverable.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `IReceiveEndpoint.Start(ct)` returns `ReceiveEndpointHandle`, not `Task`**
- **Found during:** Task 1 (the plan `<interfaces>` block claimed `Start` returns a `Task`; the installed 8.5.5 assembly returns `MassTransit.ReceiveEndpointHandle`, which has no `GetAwaiter`)
- **Issue:** `await h.ReceiveEndpoint.Start(stoppingToken)` would not compile (CS1061), and an un-awaited resume would let `prevHealthy` advance before the endpoint was actually consuming (WR-01 violation on the resume path).
- **Fix:** healthy branch awaits `h.ReceiveEndpoint.Start(stoppingToken).Ready` (the `ReceiveEndpointHandle.Ready` Task). Unhealthy branch keeps `await h.ReceiveEndpoint.Stop(stoppingToken)` (a true Task). Verified against the assembly via a reflection probe before writing the code.
- **Files modified:** src/Keeper/Health/BitHealthLoop.cs (and mirrored in the test fake: `Start` returns a substitute `ReceiveEndpointHandle` whose `.Ready` is a completed Task)
- **Verification:** `dotnet build src/Keeper/Keeper.csproj` 0/0; BitHealthLoopTests 8/8 green.
- **Committed in:** 63bff7a (prod) / fed352e (test)

**Total deviations:** 1 auto-fixed (blocking API shape, same as Plan 02). No scope creep; no architectural changes; no auth gates.

## Threat Model Coverage
- **T-52-09 (gate-bypass — endpoint consuming while gate closed):** mitigated — the unhealthy edge calls `ReceiveEndpoint.Stop` BEFORE `prevHealthy` advances; `Healthy_To_Unhealthy_Edge_Stops_Recovery_Endpoint` asserts the Stop call. No L2 op can run while the gate is closed (KEEP-04).
- **T-52-10 (stuck gate — a Stop/Start throw killing the loop):** mitigated — both Start/Stop awaits live inside the existing WR-01 try; a throw lands in `catch (Exception)` leaving `prevHealthy` un-advanced so the next tick re-applies the (idempotent gate + Start/Stop) edge. Preserved verbatim, not regressed.
- **T-52-11 (startup window — null handle before binder sets it):** accepted — null-guarded (`is { } h`); the brief pre-bind window is the same accepted residual as Plan 02 T-52-08. The next tick (within Probe:DelaySeconds) applies the edge once the handle is present.

## Threat Flags
None — no new trust-boundary surface beyond the plan's `<threat_model>`. The only new coupling is BitHealthLoop reading the existing `RecoveryEndpointHandle` singleton and calling the existing endpoint Stop/Start API.

## Known Stubs
None. BitHealthLoop actively drives the endpoint Stop/Start on real edges; the handle singleton is populated by Plan 02's binder at runtime; the null-guard is a documented accepted residual (T-52-11), not a stub.

## KEEP requirement -> fact map (52-VALIDATION)
- **KEEP-01** (REINJECT present + absent-drop): ReinjectConsumer facts — Plan 01 (keeper suite green)
- **KEEP-02** (INJECT write->send->delete order): InjectConsumer facts — Plan 01 (keeper suite green)
- **KEEP-03** (DELETE + absent no-op): DeleteConsumer facts — Plan 01 (keeper suite green)
- **KEEP-04** (gate-closed Stop / gate-open Start): `KeeperPauseAccumulateFacts` (Plan 02) + `Healthy_To_Unhealthy_Edge_Stops_Recovery_Endpoint` + `Same_State_Ticks_No_Stop_Start` (this plan) — all green
- **KEEP-05** (Dlq1 routing + SustainedOutage no-dead-letter): `RecoveryDeadLetterFacts` + `SustainedOutageFacts` — Plan 02 (keeper suite green)

## Cross-Plan Integration Notes
No cross-plan gaps found. `BitHealthLoop`'s new ctor param resolves the `RecoveryEndpointHandle` registered by Plan 02 (`Program.cs` line 62 `AddSingleton<RecoveryEndpointHandle>()`); the binder (line 63) populates it; no duplicate endpoint config; no stale Plan-01 call sites. The keeper suite (32/32) and the whole-solution build (0/0) confirm the three plans compose.

## Verification
- **BitHealthLoopTests:** 8/8 green (`dotnet test tests/BaseApi.Tests -- --filter-class "*BitHealthLoopTests"`).
- **Keeper namespace facts:** 32/32 green (`dotnet test tests/BaseApi.Tests -- --filter-namespace "*Keeper*"`).
- **dotnet build SK_P.sln -c Debug:** 0 warnings, 0 errors (clean, no concurrent run).
- **Note on the unfiltered whole-suite run:** an earlier full `dotnet test` (filter ignored — the `Microsoft.Testing.Platform` runner needs `-- --filter-class`/`--filter-namespace`, not the VSTest `--filter`) reported 526/531 with 5 failures. All 5 are RealStack/E2E tests failing on `Connection failed, host 127.0.0.1:5672` (RabbitMQ) / Postgres-migration — they require a live compose stack that is not running. They are pre-existing, infra-dependent, and NOT caused by this plan's changes (the hermetic keeper + BitHealthLoop facts are fully green). The live close gate is the operator-gated Phase-54 deliverable per 52-VALIDATION.

## Self-Check: PASSED

- Modified files exist: `src/Keeper/Health/BitHealthLoop.cs`, `tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs`, `52-03-SUMMARY.md` — all FOUND.
- Task commits exist: `63bff7a` (feat), `fed352e` (test) — both FOUND.
- Acceptance greps: ctor has `RecoveryEndpointHandle endpointHandle` (1); `ReceiveEndpoint.Start` (1) + `ReceiveEndpoint.Stop` (1) present; `gate.Open()` (2 — doc + code) + `gate.Close()` (1) NOT removed; `prevHealthy = healthy;` appears once, after the edge actions.

---
*Phase: 52-three-state-keeper*
*Completed: 2026-06-11*
