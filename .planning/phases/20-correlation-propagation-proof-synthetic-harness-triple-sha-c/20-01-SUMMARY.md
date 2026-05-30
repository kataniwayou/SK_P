---
phase: 20-correlation-propagation-proof-synthetic-harness-triple-sha-c
plan: 01
subsystem: testing
tags: [masstransit, rabbitmq, in-memory-harness, correlation, dockerfile, logging]

# Dependency graph
requires:
  - phase: 19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier
    provides: "AddBaseApiMessaging publish-only bus, StopOrchestrationConsumer seam, OrchestrationService body-correlation publish, root Dockerfile"
provides:
  - "Corrected D-13 Stop seam log string ('Scheduler job stop (seam)') distinct from the Start seam"
  - "D-07 publish-side correlation log in OrchestrationService ('Published StartOrchestration CorrelationId={guid}') + ILogger ctor injection"
  - "OQ#1/A3 RabbitMq:Port config read (default 5672) + 4-arg Host bind so the in-process test WebApi can target host port 5673"
  - "D-01 HarnessWebAppFactory: in-memory MassTransit harness swap (RemoveMassTransit then AddMassTransitTestHarness)"
  - "D-12 Dockerfile wget layer before USER app for the baseapi-service healthcheck"
  - "A1 resolved: RemoveMassTransit() is public in MassTransit 8.5.5 (no manual RemoveAll fallback needed)"
affects: [20-02, 20-03, 20-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "In-memory MassTransit harness swap on a WebApplicationFactory via ConfigureTestServices (RemoveMassTransit → AddMassTransitTestHarness)"
    - "Optional config port read with safe default (cfg.GetValue<ushort>('RabbitMq:Port', 5672)) preserving compose-internal behavior"

key-files:
  created:
    - tests/BaseApi.Tests/Composition/HarnessWebAppFactory.cs
  modified:
    - src/Orchestrator/Consumers/StopOrchestrationConsumer.cs
    - tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
    - tests/BaseApi.Tests/Orchestration/OrchestrationServicePublishTests.cs
    - src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
    - Dockerfile

key-decisions:
  - "A1 resolution: RemoveMassTransit() compiles against MassTransit 8.5.5's public IServiceCollection surface — the manual RemoveAll(IBusControl/IBus/IHostedService) fallback was NOT required"
  - "RabbitMq:Port read uses cfg.GetValue<ushort>('RabbitMq:Port', 5672) (explicit ushort generic form); default 5672 keeps the compose-internal rabbitmq:5672 path byte-unaffected"
  - "Host bind switched to the 4-arg Host(host, port, '/', ...) overload (vhost '/' preserved)"
  - "D-07 publish log added for Start only (the plan names only Start); the minted Guid is captured into a local 'startCorr' and logged at Information level after publish"

patterns-established:
  - "HarnessWebAppFactory inherits Phase8WebAppFactory (keeps Postgres/Redis fixture wiring) and swaps only the bus in ConfigureTestServices"

requirements-completed: [CORR-04, TEST-RMQ-02]

# Metrics
duration: 13min
completed: 2026-05-30
---

# Phase 20 Plan 01: Phase 20 Source + Test-Infrastructure Prerequisites Summary

**Landed the five Phase-20 prerequisites — D-13 Stop seam mislog fix, D-07 publish-side correlation log + ILogger injection, OQ#1 RabbitMq:Port config read, the D-01 in-memory HarnessWebAppFactory bus swap, and the D-12 Dockerfile wget layer — all under a maintained zero-warning build (Debug + Release).**

## Performance

- **Duration:** ~13 min
- **Started:** 2026-05-30T09:33:01Z
- **Completed:** 2026-05-30T09:46:13Z
- **Tasks:** 3
- **Files modified:** 7 (1 created, 6 modified)

## Accomplishments

- **D-13:** `StopOrchestrationConsumer` now logs the distinct `"Scheduler job stop (seam)"` string (was copy-pasting the Start string), and its locked assertion at `StartStopConsumerAckTests.cs:217` tracks `"Scheduler job stop"`. The `Start_Present_...` test (line 156) was left untouched.
- **D-07:** `OrchestrationService` injects `ILogger<OrchestrationService>` and logs `"Published StartOrchestration CorrelationId={CorrelationId}"` (Information level) with the minted body Guid captured into `startCorr`. The direct-ctor test caller passes `NullLogger<OrchestrationService>.Instance`.
- **OQ#1 / A3:** `AddBaseApiMessaging` reads `RabbitMq:Port` (default 5672) and binds host+port via the 4-arg `Host(host, port, "/", ...)` overload; the compose-internal `rabbitmq:5672` path is byte-unaffected; the in-process test WebApi has a config seam to reach host port 5673.
- **D-01 / A1:** New `HarnessWebAppFactory` swaps the real bus for the in-memory MassTransit harness via `RemoveMassTransit()` → `AddMassTransitTestHarness()`. **A1 resolved: `RemoveMassTransit()` is public in MassTransit 8.5.5 — the manual `RemoveAll` fallback was not needed.**
- **D-12:** Root `Dockerfile` installs `wget` (apt layer with `--no-install-recommends` + cache cleanup) before `USER app` so `baseapi-service`'s `wget --spider` healthcheck can execute.

## Task Commits

1. **Task 1: D-13 Stop seam fix + locked assertion + D-07 publish log** - `1696b96` (fix)
2. **Task 2: RabbitMq:Port read in AddBaseApiMessaging** - `8b6f8e7` (feat)
3. **Task 3: HarnessWebAppFactory + Dockerfile wget layer** - `4e9b9d8` (feat)

## Files Created/Modified

- `tests/BaseApi.Tests/Composition/HarnessWebAppFactory.cs` (created) - In-memory MassTransit harness swap on the integration host (D-01).
- `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` - Corrected Stop seam log string (D-13).
- `tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs` - Line-217 Stop assertion tracks the corrected string (D-13).
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` - ILogger field + ctor param + publish-side correlation log (D-07).
- `tests/BaseApi.Tests/Orchestration/OrchestrationServicePublishTests.cs` - BuildService passes NullLogger (D-07 caller fix).
- `src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` - RabbitMq:Port read + 4-arg Host bind (OQ#1).
- `Dockerfile` - wget apt layer before USER app (D-12).

## Verification Results

- `dotnet build SK_P.sln -c Release` → exit 0, 0 warnings / 0 errors.
- `dotnet build SK_P.sln -c Debug` → exit 0, 0 warnings / 0 errors.
- `dotnet test --filter-class *StartStopConsumerAckTests` → 6/6 passed.
- `dotnet test --filter-class *OrchestrationServicePublishTests` → 4/4 passed.
- `dotnet test --filter-class *HealthEndpointsTests` → 18/18 passed (port default-5672 path unaffected).
- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` → exit 0 (HarnessWebAppFactory harness API surface resolves; A1 confirmed).

All task `<acceptance_criteria>` re-verified via grep: stop(seam)=1, start(seam) in Stop consumer=0, stop-assert=1, start-assert=1, publish-log=1, ctor logger=1, test logger arg=1; RabbitMq:Port=1, `Host(host, port`=1, 5672 present=1; HarnessWebAppFactory class + AddMassTransitTestHarness + RemoveMassTransit() all present; Dockerfile wget=1 with apt-line(15) < USER-app-line(18).

## Decisions Made

- **A1 (RemoveMassTransit path):** Used `services.RemoveMassTransit()` — it is part of MassTransit 8.5.5's public IServiceCollection surface and compiles cleanly. The manual `RemoveAll(IBusControl/IBus/IPublishEndpoint/ISendEndpointProvider)` + hosted-service-removal fallback documented in the plan was NOT required.
- **RabbitMq:Port read form:** Used the explicit-generic `cfg.GetValue<ushort>("RabbitMq:Port", 5672)` form (resolves cleanly; `Microsoft.Extensions.Configuration.Binder` is available transitively). Host bind uses `Host(host, port, "/", h => {...})`.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Plan 20-02 (hermetic in-memory tests) can now build on `HarnessWebAppFactory` + the `RabbitMq:Port` read.
- Plan 20-03 (real-stack ES proof) has the `RabbitMq:Port` seam, the D-07 publish log, and the corrected D-13 Stop string.
- Plan 20-04 (close gate) has the Dockerfile wget layer so `baseapi-service` reports healthy (image must be rebuilt before the gate).

## Self-Check: PASSED

- Created files verified on disk: `HarnessWebAppFactory.cs`, `20-01-SUMMARY.md`.
- Commits verified in git log: `1696b96`, `8b6f8e7`, `4e9b9d8`.

---
*Phase: 20-correlation-propagation-proof-synthetic-harness-triple-sha-c*
*Completed: 2026-05-30*
