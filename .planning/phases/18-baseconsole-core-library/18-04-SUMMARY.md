---
phase: 18-baseconsole-core-library
plan: 04
subsystem: testing
tags: [masstransit, test-harness, generic-host, opentelemetry, health-checks, correlation, xunit-v3, baseconsole]

# Dependency graph
requires:
  - phase: 18-01
    provides: BaseConsole.Core project, RequiredConfig, startup gate trio, StartupCompletionService, soft-dep Redis, AsyncLocalCorrelationAccessor
  - phase: 18-02
    provides: Console OTel (metrics-only), the three correlation filters, AddBaseConsoleMessaging bus skeleton
  - phase: 18-03
    provides: Embedded minimal-Kestrel health listener (/live|ready|startup), BusReadyHealthCheck, non-generic AddBaseConsole root
provides:
  - ConsoleTestHostFixture — the in-memory Generic-Host (D-02) validation vehicle composing all three AddBaseConsole* calls with dead Redis + unreachable RabbitMQ
  - Five Console validation test classes proving the six D-02 proof points (boot, /health/live, startup gate 200/503, no-TracerProvider, harness-based correlation filters)
  - Phase 18 close gate evidence — full suite GREEN 3-consecutive + dual-SHA BEFORE=AFTER, zero-warning Release+Debug
affects: [19-orchestrator-console, 20-correlation-proof, masstransit-test-harness, baseconsole-validation]

# Tech tracking
tech-stack:
  added: [AddMassTransitTestHarness (MassTransit 8.5.5 in-memory transport — no extra NuGet)]
  patterns:
    - "In-memory Generic-Host fixture (Host.CreateApplicationBuilder) as the D-02 validation vehicle — NOT WebApplicationFactory"
    - "Dead-port soft-dependency boot proof (DEAD Redis 127.0.0.1:6399 + unreachable RabbitMQ) — host boots without throwing"
    - "Ephemeral free-port health listener per fixture to avoid 8081 collisions across parallel tests"
    - "BusReadyHealthCheck unit-tested directly (null IBusHealth path) as the POSITIVE broker-unreachable proof — independent of fixture timing"
    - "AddMassTransitTestHarness in-memory transport exercises the correlation filters without RabbitMQ"

key-files:
  created:
    - tests/BaseApi.Tests/Console/ConsoleTestHostFixture.cs
    - tests/BaseApi.Tests/Console/ConsoleHostBootTests.cs
    - tests/BaseApi.Tests/Console/ConsoleHealthLiveTests.cs
    - tests/BaseApi.Tests/Console/ConsoleBusReadyHealthCheckTests.cs
    - tests/BaseApi.Tests/Console/ConsoleStartupGateTests.cs
    - tests/BaseApi.Tests/Console/ConsoleObservabilityTests.cs
    - tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs
  modified:
    - tests/BaseApi.Tests/BaseApi.Tests.csproj
    - src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs

key-decisions:
  - "D-02 vehicle is the in-memory Host.CreateApplicationBuilder fixture + AddMassTransitTestHarness — no concrete console and no real broker ship this phase (D-03)"
  - "Close gate is dual-SHA (psql \\l + redis-cli --scan), NOT triple-SHA — no real broker resources exist this phase; rabbitmqctl line dropped"
  - "CONSOLE-HEALTH-03 broker-unreachable is positively proven by the committed BusReadyHealthCheck Unhealthy unit test (not an optional timing-sensitive fixture assertion)"

patterns-established:
  - "Generic-Host (not WebApplicationFactory) in-memory fixture for console-library validation"
  - "Open-generic scoped consume filter registration: UseConsumeFilter(typeof(Filter<>), ctx) requires a generic type definition"

requirements-completed: [CONSOLE-01, CONSOLE-02, CONSOLE-03, CONSOLE-04, CONSOLE-05, CONSOLE-HEALTH-01, CONSOLE-HEALTH-02, CONSOLE-HEALTH-03, CONSOLE-HEALTH-04, CORR-01, CORR-02]

# Metrics
duration: ~35min (Tasks 1-3) + ~10min gate run (3×245 GREEN) + finalization
completed: 2026-05-30
---

# Phase 18 Plan 04: BaseConsole.Core Standalone Validation + Close Gate Summary

**In-memory Generic-Host fixture + five Console test classes prove all six D-02 proof points (boot under dead deps, /health/live 200, startup 200/503, no-TracerProvider, harness-stamped correlation), closing Phase 18 at a dual-SHA 3-consecutive-GREEN gate (245/245).**

## Performance

- **Duration:** ~35 min implementation (Tasks 1-3) + ~10 min gate run + finalization
- **Completed:** 2026-05-30
- **Tasks:** 4 (3 auto + 1 operator-approved checkpoint:human-verify)
- **Files modified:** 9 (7 new test files + 1 test csproj + 1 src filter fix)

## Accomplishments

- **ConsoleTestHostFixture (D-02 vehicle):** in-memory `Host.CreateApplicationBuilder` composing `AddBaseConsoleObservability` + `AddBaseConsole` + `AddBaseConsoleMessaging` with DEAD Redis (`127.0.0.1:6399,abortConnect=false`) + unreachable RabbitMQ + an ephemeral free health port + HttpClient. Host boots without throwing; `IBusControl` resolvable (CONSOLE-01/03/04/05 boot path).
- **Health validation:** `/health/live` returns 200 + `"status":"Healthy"` with both deps dead (CONSOLE-HEALTH-02 / T-18-09); no-secrets body, no `Password=` leak (T-18-08); `/health/startup` 200 after init and 503 (`"status":"Unhealthy"`) when `StartupCompletionService` is removed by type identity (CONSOLE-HEALTH-04).
- **CONSOLE-HEALTH-03 positively proven:** `BusReadyHealthCheck_Returns_Unhealthy_When_Bus_Not_Healthy` constructs `BusReadyHealthCheck` against an empty provider (null `IBusHealth`) and asserts `HealthStatus.Unhealthy` — the broker-unreachable proof, independent of fixture timing or a real broker.
- **Observability shape:** no `TracerProvider` resolvable (T-18-07 / Pitfall 1), `MeterProvider` present, no AspNetCore/HttpClient instrumentation registered (CONSOLE-02).
- **Correlation filters via harness:** `AddMassTransitTestHarness` (in-memory transport, no RabbitMQ) wires all three filters bus-wide; inbound populates the `ICorrelationAccessor` from the envelope before the body runs (CORR-01), outbound stamps the published envelope `CorrelationId` with the ambient Guid (CORR-02 / D-01).
- **Phase 18 close gate (D-03):** full `dotnet test` GREEN 3-consecutive (245/245 each run), dual-SHA BEFORE=AFTER, zero-warning Release + Debug — operator-approved.

## Task Commits

1. **Task 1: Test ProjectReference + ConsoleTestHostFixture (D-02 vehicle)** - `ba6c665` (test)
2. **Task 2: Health + observability validation tests** - `fa9df74` (test)
3. **Task 3: Correlation filter behavior via AddMassTransitTestHarness** - `8a3a799` (test)
4. **Task 4: Phase close gate (checkpoint:human-verify)** - automation position recorded `399c08e` (docs); operator approved 2026-05-30

**Pre-task fix (surfaced by this plan's fixture):** `af953ea` (fix(18-02))

_Plan metadata commit follows this SUMMARY._

## Gate Evidence (Task 4 — operator-approved)

- **3-consecutive GREEN:** Run 1/2/3 each = Passed: 245, Failed: 0, Total: 245 (durations 3m18s / 3m17s / 3m17s). Console subset (10 tests) GREEN every run.
- **Dual-SHA (D-03, no rabbitmqctl — no real broker this phase):**
  - `psql \l` SHA-256: BEFORE = `b202692d34f71ca254b71b9468435735c8c1a5f3b048f78bb971c55eadf40d55` ; AFTER = (same) — **MATCH**
  - `redis-cli --scan` SHA-256: BEFORE = `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` (0 keys) ; AFTER = (same) — **MATCH**
- **Zero-warning build:** Release = 0 Warning(s) / 0 Error(s); Debug = 0 Warning(s) / 0 Error(s).
- **Operator confirmation:** "approved" at the `checkpoint:human-verify` gate (not self-approved).

## Files Created/Modified

- `tests/BaseApi.Tests/BaseApi.Tests.csproj` - added `..\..\src\BaseConsole.Core\BaseConsole.Core.csproj` ProjectReference
- `tests/BaseApi.Tests/Console/ConsoleTestHostFixture.cs` - in-memory Generic-Host D-02 vehicle (dead deps, ephemeral port, HttpClient)
- `tests/BaseApi.Tests/Console/ConsoleHostBootTests.cs` - boot-under-dead-deps + IBusControl resolvable
- `tests/BaseApi.Tests/Console/ConsoleHealthLiveTests.cs` - /health/live 200+Healthy with deps dead; no-secrets body
- `tests/BaseApi.Tests/Console/ConsoleBusReadyHealthCheckTests.cs` - POSITIVE broker-unreachable Unhealthy proof (CONSOLE-HEALTH-03)
- `tests/BaseApi.Tests/Console/ConsoleStartupGateTests.cs` - /health/startup 200 + 503 (StartupCompletionService removed by type identity)
- `tests/BaseApi.Tests/Console/ConsoleObservabilityTests.cs` - no TracerProvider; MeterProvider present; no AspNetCore/Http instrumentation
- `tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs` - harness-based inbound accessor + outbound envelope CorrelationId proof
- `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs` - made open-generic (deviation 1)

## Decisions Made

- D-02 validation runs entirely in-memory (Generic-Host fixture + `AddMassTransitTestHarness`); no concrete console or real broker ships this phase.
- Close gate is dual-SHA, not triple-SHA — no real broker resource is created this phase (D-03), so the `rabbitmqctl` snapshot line is correctly dropped.
- CONSOLE-HEALTH-03 is proven by the committed `BusReadyHealthCheck` Unhealthy unit test rather than a timing-sensitive in-memory `/health/ready` fixture assertion.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Made inbound correlation filter open-generic for scoped bus-wide registration**
- **Found during:** Task 1 (ConsoleTestHostFixture first `Host.StartAsync()`)
- **Issue:** `InboundCorrelationConsumeFilter` was non-generic (`IFilter<ConsumeContext>`). The first time the fixture built the bus, MassTransit 8.5.5 threw `ConfigurationException: 'The scoped filter must be a generic type definition'` — `UseConsumeFilter(typeof(...), ctx)` (the scoped overload) requires a generic type definition.
- **Fix:** Changed to `InboundCorrelationConsumeFilter<T> : IFilter<ConsumeContext<T>>` and registered via `UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), ctx)`, mirroring the two outbound open-generic filters. MEL-scope + accessor logic unchanged.
- **Files modified:** src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs
- **Verification:** Host starts without throwing; CORR-01 inbound test GREEN; full suite 245/245.
- **Committed in:** `af953ea` (classified `fix(18-02)` — production code owned by Plan 18-02)

**2. [Rule 2 - Missing Critical] Committed the CONSOLE-HEALTH-03 broker-unreachable Unhealthy test as a non-optional unit test**
- **Found during:** Task 2 (health validation)
- **Issue:** The end-to-end `/health/ready` Unhealthy assertion against the booted fixture is timing-sensitive in-memory and would not reliably and positively prove CONSOLE-HEALTH-03 (SC-3, Unhealthy-while-broker-unreachable). Leaving it optional would have left a milestone success criterion unproven.
- **Fix:** Added `ConsoleBusReadyHealthCheckTests.BusReadyHealthCheck_Returns_Unhealthy_When_Bus_Not_Healthy` — constructs `BusReadyHealthCheck` directly against a provider whose `IBusHealth` is null and asserts `HealthStatus.Unhealthy`. This is the deterministic positive proof, independent of broker or fixture timing.
- **Files modified:** tests/BaseApi.Tests/Console/ConsoleBusReadyHealthCheckTests.cs
- **Verification:** Test GREEN every run; CONSOLE-HEALTH-03 positively proven.
- **Committed in:** `fa9df74` (Task 2 commit) + plan-checker revision `e47d629`

---

**Total deviations:** 2 auto-fixed (1 bug fix-forward, 1 missing-critical test hardening)
**Impact on plan:** Both auto-fixes necessary for correctness and to positively prove milestone success criteria. No scope creep — no production surface added, no real broker introduced.

## Issues Encountered

- Existing Observability tests can require the OTel Collector warmed; per the documented fixture-lifecycle robustness, re-runs converged GREEN. The Console subset (10 tests) was GREEN on every run.

## User Setup Required

None - no external service configuration required (in-memory transport + dead Redis port; no real broker).

## Next Phase Readiness

- Phase 18 complete (4/4 plans). `BaseConsole.Core` is validated standalone — boot, health, observability shape, and both correlation filters are proven.
- Ready for Phase 19: the `Orchestrator` console inherits `BaseConsole.Core`; WebApi joins the bus as publisher; RabbitMQ compose tier goes live. The triple-SHA close gate (adding `rabbitmqctl list_queues name`) returns in Phase 20 once real broker resources exist.

## Self-Check: PASSED

- Created files verified present: 18-04-SUMMARY.md, ConsoleTestHostFixture.cs, ConsoleCorrelationFilterTests.cs (+ 4 sibling test classes from Tasks 2-3).
- Commits verified present: af953ea (filter fix), ba6c665 (Task 1), fa9df74 (Task 2), 8a3a799 (Task 3), 399c08e (Task 4 checkpoint position).

---
*Phase: 18-baseconsole-core-library*
*Completed: 2026-05-30*
