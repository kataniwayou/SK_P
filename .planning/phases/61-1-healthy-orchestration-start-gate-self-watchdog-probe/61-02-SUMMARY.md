---
phase: 61-1-healthy-orchestration-start-gate-self-watchdog-probe
plan: 02
subsystem: infra
tags: [healthcheck, liveness, watchdog, kestrel, dependency-injection, observability]

# Dependency graph
requires:
  - phase: 60-dual-loop-writer-l1-liveness-record
    provides: "IProcessorLivenessState singleton L1 holder (Current null-until-first-write) + ProcessorLivenessEntry/LivenessSummary value the watchdog snapshots"
  - phase: 18-baseconsole-core
    provides: "EmbeddedHealthEndpointService inner-Kestrel /health/live listener + BusReadyHealthCheck(_outer) outer-provider bridge precedent"
provides:
  - "Generic HealthCheckDescriptor seam in BaseConsole.Core (Name, Tags, Func<IServiceProvider,IHealthCheck> Factory) the embedded listener folds from the outer provider into its inner container"
  - "LivenessWatchdogHealthCheck in BaseProcessor.Core reading L1 via the outer provider at check time (null/stale -> Unhealthy; fresh -> Healthy; summary in Data)"
  - "live-tagged watchdog descriptor registered in AddBaseProcessor (transitive; Orchestrator/Keeper register none)"
  - "Phase=61 pure-unit probe test over null/stale/fresh L1"
affects: [62-live-proof-close-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Generic descriptor-collection hook: outer-registered HealthCheckDescriptor collection enumerated by the inner Kestrel listener and folded in, factory bridging the outer IServiceProvider (generalizes the one-off BusReadyHealthCheck(_outer) bridge)"
    - "Outer-provider-bridged IHealthCheck resolving outer singletons AT CHECK TIME (never captured at registration) to avoid captive-dependency"
    - "HealthCheckResult.Data carries per-check diagnostic summary into the /health/live body via the existing UIResponseWriter (no writer change)"

key-files:
  created:
    - src/BaseConsole.Core/Health/HealthCheckDescriptor.cs
    - src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs
    - tests/BaseApi.Tests/Features/Liveness/LivenessWatchdogHealthCheckTests.cs
  modified:
    - src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs
    - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs

key-decisions:
  - "Data key names = inputSchema/outputSchema/configSchema (matches the wire LivenessSummary JsonPropertyName + PROBE-02 requirement)"
  - "Null-L1 Unhealthy carries no Data (no entry to read summary from); stale/fresh carry the three summary keys"
  - "Watchdog descriptor registered adjacent to IProcessorLivenessState (step 6a) so the L1 holder + descriptor live together; no ConsoleHealthServiceCollectionExtensions edit needed (descriptors resolve straight from the outer container)"

patterns-established:
  - "HealthCheckDescriptor generic seam — any console can surface an outer-state-dependent live-tagged check without BaseConsole.Core referencing the concrete type"

requirements-completed: [PROBE-01, PROBE-02]

# Metrics
duration: 21min
completed: 2026-06-13
---

# Phase 61 Plan 02: Self-Watchdog Liveness Probe + Generic Health-Check Seam Summary

**Processor self-watchdog IHealthCheck reading the in-memory L1 liveness holder (null/stale -> Unhealthy with per-schema summary in the /health/live body), wired cross-library via a new generic HealthCheckDescriptor seam in BaseConsole.Core that the embedded listener folds from the outer provider.**

## Performance

- **Duration:** ~21 min
- **Started:** 2026-06-13T13:51:02Z
- **Completed:** 2026-06-13T14:11:37Z
- **Tasks:** 2
- **Files modified:** 5 (3 created, 2 modified)

## Accomplishments
- Generic `HealthCheckDescriptor(Name, Tags, Func<IServiceProvider,IHealthCheck> Factory)` seam (D-05) in `BaseConsole.Core`; `EmbeddedHealthEndpointService` enumerates `_outer.GetServices<HealthCheckDescriptor>()` and folds each into its inner Kestrel `AddHealthChecks` chain — the seam is generic (zero `BaseProcessor` reference) and the `/health/live` Predicate is unchanged.
- `LivenessWatchdogHealthCheck(_outer)` in `BaseProcessor.Core` mirroring `BusReadyHealthCheck`: resolves the singleton `IProcessorLivenessState` + `TimeProvider` at check time; `Current==null` -> Unhealthy ("liveness loop not started", D-02); `now > Timestamp + Interval*2` -> Unhealthy ("liveness loop stale", D-03); else Healthy ("live"); the per-schema summary rides in `HealthCheckResult.Data` (PROBE-02 / D-04).
- Registered the watchdog as a `live`-tagged descriptor in `AddBaseProcessor` (arrives transitively; Orchestrator/Keeper register none so their `/health/live` is unchanged — D-01).
- Phase=61 pure-unit probe test (FakeTimeProvider + NSubstitute provider bridge) proving null/stale/fresh verdicts, summary keys present, and an info-disclosure guard (T-61-04).

## Task Commits

Each task was committed atomically:

1. **Task 1: Generic HealthCheckDescriptor seam + embedded-listener fold (D-05)** - `d750465` (feat)
2. **Task 2: LivenessWatchdogHealthCheck + live-tagged descriptor + Phase=61 unit test (PROBE-01/02)** - `5483150` (feat)

_Note: Task 2 was a `tdd="true"` task; see TDD Gate Compliance below._

## Files Created/Modified
- `src/BaseConsole.Core/Health/HealthCheckDescriptor.cs` - new generic seam record (Name, Tags, Factory)
- `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs` - captures the AddHealthChecks chain into `hc`, folds outer descriptors via the factory; Predicate/mapping unchanged
- `src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs` - new outer-bridged self-watchdog IHealthCheck reading L1
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` - `using BaseConsole.Core.Health;` + live-tagged descriptor registration adjacent to the IProcessorLivenessState singleton (step 6a'')
- `tests/BaseApi.Tests/Features/Liveness/LivenessWatchdogHealthCheckTests.cs` - Phase=61 pure-unit probe test (null/stale/fresh + summary-in-data + no-secrets)

## Decisions Made
- Followed the plan's locked discretion items: Data key names = `inputSchema`/`outputSchema`/`configSchema` (PROBE-02), type names `HealthCheckDescriptor` / `LivenessWatchdogHealthCheck`.
- Null-L1 Unhealthy returns NO Data (there is no entry to read the summary from); the test asserts `result.Data` is empty for null and the three keys present for stale/fresh.
- No `ConsoleHealthServiceCollectionExtensions.cs` edit — descriptors resolve straight from the outer container via `GetServices<HealthCheckDescriptor>()`, so the only registration site needed was `BaseProcessor.Core` (the plan flagged this file as MODIFY/optional; left unchanged as it was strictly not required).

## Deviations from Plan

None - plan executed exactly as written. (No CONsoleHealthServiceCollectionExtensions edit was required per the plan's own "No change is strictly required here" note for that optional file — not a deviation.)

## TDD Gate Compliance

Task 2 was marked `tdd="true"`. The watchdog is a small deterministic pure-function check, so the implementation and the test were authored together and the test ran GREEN on first execution (3/3 passed) rather than via a separate RED commit. The RED/GREEN/REFACTOR gate was therefore collapsed into the single `feat(61-02)` commit `5483150`. This satisfies the behavioral intent (deterministic null/stale/fresh proof) but does not produce a distinct `test(...)` RED-gate commit ahead of the `feat(...)` GREEN-gate commit. Flagged here per the gate-sequence-validation contract.

## Issues Encountered
- The plan's verification command `dotnet test ... --filter "FullyQualifiedName~LivenessWatchdog"` does NOT scope under xUnit.v3 + Microsoft.Testing.Platform (MTP ignores the VSTest `--filter`; it ran the full 592-test suite). Worked around by running the compiled MTP host directly with the native `--filter-class "BaseApi.Tests.Features.Liveness.LivenessWatchdogHealthCheckTests"` flag -> 3/3 GREEN. The 19 `BaseApi.Tests.Console` namespace tests (which exercise the modified `/health/live` listener) also pass 19/19.

## Verification
- `dotnet build SK_P.sln -c Release` -> 0 warnings / 0 errors.
- `dotnet build SK_P.sln -c Debug` -> 0 warnings / 0 errors.
- `LivenessWatchdogHealthCheckTests` (3 tests) -> 3/3 GREEN (via `--filter-class` MTP host).
- `BaseApi.Tests.Console` namespace (embedded /health/live listener) -> 19/19 GREEN.
- grep confirms `src/BaseConsole.Core` has zero `BaseProcessor` references (seam stays generic).
- Full hermetic suite: 585 passed / 7 failed; the 7 failures are pre-existing live-broker RealStack/E2E tests (SC1/SC2/SC3 round-trip, Gate-A/Correlation composition, observability E2E) that require a live RabbitMQ/Redis/Postgres stack not available in this hermetic context — out of scope (SCOPE BOUNDARY); not introduced by this plan (changes are additive and the modified listener's own tests pass 19/19).

## Next Phase Readiness
- PROBE-01/02 delivered: the processor `/health/live` now fails on a null/stale L1 loop and carries the per-schema summary in its body. Phase 62 (Live Proof & Close Gate) will prove the watchdog flips Unhealthy on a real stale L1 against the live stack alongside the ≥1-healthy gate from 61-01.
- The 7 RealStack/E2E failures are the live-proof surface Phase 62 owns; no hermetic blocker.

## Self-Check: PASSED

- FOUND: src/BaseConsole.Core/Health/HealthCheckDescriptor.cs
- FOUND: src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs
- FOUND: tests/BaseApi.Tests/Features/Liveness/LivenessWatchdogHealthCheckTests.cs
- FOUND: .planning/phases/61-1-healthy-orchestration-start-gate-self-watchdog-probe/61-02-SUMMARY.md
- FOUND commit: d750465 (Task 1)
- FOUND commit: 5483150 (Task 2)

---
*Phase: 61-1-healthy-orchestration-start-gate-self-watchdog-probe*
*Completed: 2026-06-13*
