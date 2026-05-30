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

1. **Task 1: D-13 Stop seam fix + locked assertion + D-07 publish log** - `dee1414` (fix)
2. **Task 2: RabbitMq:Port read in AddBaseApiMessaging** - `94d78b7` (feat)
3. **Task 3: HarnessWebAppFactory + Dockerfile wget layer** - `a1c05d4` (feat)

**Plan metadata:** `f98b9f3` (docs: complete plan)

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

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Updated OrchestrationService DI factory registration for the new ILogger ctor arg**
- **Found during:** Task 1 (D-07 ctor change), surfaced by the full-solution Release build.
- **Issue:** The plan stated "DI in Program.cs resolves ILogger automatically — no Program.cs change needed." That is true for typed registrations, but `OrchestrationService` is registered via an **explicit factory** (`AddScoped<OrchestrationService>(sp => new OrchestrationService(...))`) in `OrchestrationServiceCollectionExtensions.cs:57` because its ctor is `internal`. Adding the `ILogger<OrchestrationService>` ctor parameter therefore broke the positional factory call (`CS7036: no argument given for 'options'`). The plan's `<interfaces>` block did not flag this factory caller (it named only the test BuildService caller).
- **Fix:** Added `sp.GetRequiredService<ILogger<OrchestrationService>>()` as the second-to-last factory argument (matching the new ctor parameter order, before `IOptions<RedisProjectionOptions>`), plus `using Microsoft.Extensions.Logging;` to the file.
- **Files modified:** `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs`
- **Verification:** `dotnet build SK_P.sln -c Release` exits 0, zero warnings.
- **Committed in:** `4c… (Task-1 amend / follow-up build-fix commit — see Task Commits)`

---

**Total deviations:** 1 auto-fixed (1 blocking).
**Impact on plan:** Necessary for the solution to compile under the D-07 ctor change. The plan's "no Program.cs change" note held literally (Program.cs untouched); the missed caller was the per-feature DI factory, not Program.cs. No scope creep.

## Issues Encountered

- The per-task acceptance grep checks for Task 1/2/3 all passed, but the full-solution Release build was initially red because of the DI factory caller above (the test-project-only build the plan specified for Task 3 had linked against a previously-built BaseApi.Service.dll and masked the source break). Fixed by updating the factory registration; the full `dotnet build SK_P.sln -c Release` is the authoritative gate and is now GREEN.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Plan 20-02 (hermetic in-memory tests) can now build on `HarnessWebAppFactory` + the `RabbitMq:Port` read.
- Plan 20-03 (real-stack ES proof) has the `RabbitMq:Port` seam, the D-07 publish log, and the corrected D-13 Stop string.
- Plan 20-04 (close gate) has the Dockerfile wget layer so `baseapi-service` reports healthy (image must be rebuilt before the gate).

## Self-Check: PASSED

- Created files verified on disk: `HarnessWebAppFactory.cs`, `20-01-SUMMARY.md`.
- Commits verified in git log: `dee1414`, `94d78b7`, `a1c05d4`, `f98b9f3`.

---
*Phase: 20-correlation-propagation-proof-synthetic-harness-triple-sha-c*
*Completed: 2026-05-30*
