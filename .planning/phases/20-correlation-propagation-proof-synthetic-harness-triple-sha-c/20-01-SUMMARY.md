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
  - "D-01 HarnessWebAppFactory: in-memory MassTransit harness swap (manual Remove of bus descriptors + MassTransit IHostedService, then AddMassTransitTestHarness)"
  - "D-12 Dockerfile wget layer before USER app for the baseapi-service healthcheck"
  - "A1 resolved: RemoveMassTransit() is NOT public in MassTransit 8.5.5 (CS1061) — used the manual RemoveAll fallback (remove bus descriptors + MassTransit IHostedService before AddMassTransitTestHarness)"
affects: [20-02, 20-03, 20-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "In-memory MassTransit harness swap on a WebApplicationFactory via ConfigureTestServices (manual Remove of bus descriptors + MassTransit IHostedService → AddMassTransitTestHarness)"
    - "Optional config port read with safe default (cfg.GetValue<ushort>('RabbitMq:Port', 5672)) preserving compose-internal behavior"

key-files:
  created:
    - tests/BaseApi.Tests/Composition/HarnessWebAppFactory.cs
  modified:
    - src/Orchestrator/Consumers/StopOrchestrationConsumer.cs
    - tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs
    - tests/BaseApi.Tests/Orchestration/OrchestrationServicePublishTests.cs
    - src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
    - Dockerfile

key-decisions:
  - "A1 resolution: RemoveMassTransit() does NOT compile against MassTransit 8.5.5 (CS1061; absent from MassTransit.dll) — used the manual fallback: Remove the bus descriptors (IBusControl/IBus/IPublishEndpoint/ISendEndpointProvider) + the MassTransit IHostedService before AddMassTransitTestHarness. (Generic RemoveAll<T>() collided with MassTransit's overload — CS0305 — so a descriptor-filter Remove loop is used.)"
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
- **Tasks:** 3 (+ 1 build-fix follow-up for the 2 auto-fixed deviations)
- **Files modified:** 8 (1 created, 7 modified)

## Accomplishments

- **D-13:** `StopOrchestrationConsumer` now logs the distinct `"Scheduler job stop (seam)"` string (was copy-pasting the Start string), and its locked assertion at `StartStopConsumerAckTests.cs:217` tracks `"Scheduler job stop"`. The `Start_Present_...` test (line 156) was left untouched.
- **D-07:** `OrchestrationService` injects `ILogger<OrchestrationService>` and logs `"Published StartOrchestration CorrelationId={CorrelationId}"` (Information level) with the minted body Guid captured into `startCorr`. The direct-ctor test caller passes `NullLogger<OrchestrationService>.Instance`.
- **OQ#1 / A3:** `AddBaseApiMessaging` reads `RabbitMq:Port` (default 5672) and binds host+port via the 4-arg `Host(host, port, "/", ...)` overload; the compose-internal `rabbitmq:5672` path is byte-unaffected; the in-process test WebApi has a config seam to reach host port 5673.
- **D-01 / A1:** New `HarnessWebAppFactory` swaps the real bus for the in-memory MassTransit harness via a manual `Remove` of the bus descriptors (IBusControl/IBus/IPublishEndpoint/ISendEndpointProvider) + the MassTransit `IHostedService`, then `AddMassTransitTestHarness()`. **A1 resolved: `RemoveMassTransit()` is NOT public in MassTransit 8.5.5 (CS1061) — the documented manual `RemoveAll` fallback was required.**
- **D-12:** Root `Dockerfile` installs `wget` (apt layer with `--no-install-recommends` + cache cleanup) before `USER app` so `baseapi-service`'s `wget --spider` healthcheck can execute.

## Task Commits

1. **Task 1: D-13 Stop seam fix + locked assertion + D-07 publish log** - `dee1414` (fix)
2. **Task 2: RabbitMq:Port read in AddBaseApiMessaging** - `94d78b7` (feat)
3. **Task 3: HarnessWebAppFactory + Dockerfile wget layer** - `a1c05d4` (feat)
4. **Build-fix: A1 manual RemoveAll fallback + DI logger arg** - `6f2cb2e` (fix) — see Deviations

**Plan metadata:** `f98b9f3` (docs: complete plan) + `2f9d3f1` (docs: A1 resolution + ROADMAP progress)

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
- `*StartStopConsumerAckTests` (MTP exe `--filter-class`) → 6/6 passed (D-13 corrected Stop assertion GREEN).
- `*OrchestrationServicePublishTests` → 4/4 passed (D-07 publish-log + NullLogger caller GREEN).
- `*HealthEndpointsTests` → NOT runnable in this hermetic session: 9/13 fail with `Npgsql … Failed to connect to 127.0.0.1:5433` and `Broker unreachable rabbitmq:5672` — these are real-stack integration tests requiring the live Postgres (host 5433) + RabbitMQ compose containers, which are NOT up in this hermetic Plan-20-01 environment. Environmental (absent DB/broker), NOT caused by the RabbitMq:Port change: the default-5672 path is byte-identical and the errors are connection failures, not config-binding errors. Gate deferred to the real-stack plans (20-03/20-04). Task 2 acceptance is satisfied by the zero-warning build + unchanged default path.
- Tests project compiles within `SK_P.sln` Release/Debug (HarnessWebAppFactory harness API surface resolves; A1 manual fallback path confirmed).

All task `<acceptance_criteria>` re-verified via grep: stop(seam)=1, start(seam) in Stop consumer=0, stop-assert=1, start-assert=1, publish-log=1, ctor logger=1, test logger arg=1; RabbitMq:Port=1, `Host(host, port`=1, 5672 present=1; HarnessWebAppFactory class + AddMassTransitTestHarness present + the manual RemoveAll fallback (A1); Dockerfile wget=1 with apt-line(16) < USER-app-line(19).

## Decisions Made

- **A1 (RemoveMassTransit path):** `services.RemoveMassTransit()` does NOT exist in MassTransit 8.5.5 (CS1061). Used the plan's documented manual fallback: `Remove` the bus descriptors (`IBusControl`/`IBus`/`IPublishEndpoint`/`ISendEndpointProvider`) + the MassTransit `IHostedService` descriptor, then `AddMassTransitTestHarness()`. (See Deviations for the CS0305 disambiguation detail.)
- **RabbitMq:Port read form:** Used the explicit-generic `cfg.GetValue<ushort>("RabbitMq:Port", 5672)` form (resolves cleanly; `Microsoft.Extensions.Configuration.Binder` is available transitively). Host bind uses `Host(host, port, "/", h => {...})`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Updated OrchestrationService DI factory registration for the new ILogger ctor arg**
- **Found during:** Task 1 (D-07 ctor change), surfaced by the full-solution Release build.
- **Issue:** The plan stated "DI in Program.cs resolves ILogger automatically — no Program.cs change needed." That is true for typed registrations, but `OrchestrationService` is registered via an **explicit factory** (`AddScoped<OrchestrationService>(sp => new OrchestrationService(...))`) in `OrchestrationServiceCollectionExtensions.cs:57` because its ctor is `internal`. Adding the `ILogger<OrchestrationService>` ctor parameter therefore broke the positional factory call (`CS7036: no argument given for 'options'`). The plan's `<interfaces>` block did not flag this factory caller (it named only the test BuildService caller).
- **Fix:** Added `sp.GetRequiredService<ILogger<OrchestrationService>>()` as the second-to-last factory argument (matching the new ctor parameter order, before `IOptions<RedisProjectionOptions>`), plus `using Microsoft.Extensions.Logging;` to the file.
- **Files modified:** `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs`
- **Verification:** `dotnet build SK_P.sln -c Release` exits 0, zero warnings.
- **Committed in:** `6f2cb2e` (build-fix commit)

**2. [Rule 1 - Bug / A1 resolution] HarnessWebAppFactory: RemoveMassTransit() not in MassTransit 8.5.5 → manual RemoveAll fallback**
- **Found during:** Task 3, surfaced by the full-solution Release build (CS1061).
- **Issue:** The plan's primary path used `services.RemoveMassTransit()`. That extension is NOT part of MassTransit 8.5.5's public IServiceCollection surface — it fails to compile (CS1061), and the symbol is absent from `MassTransit.dll`. (An earlier binary string-grep gave a false positive; the compiler is authoritative.)
- **Fix:** Applied the plan's documented A1 fallback — `Remove` the bus service descriptors (`IBusControl`, `IBus`, `IPublishEndpoint`, `ISendEndpointProvider`) plus the MassTransit `IHostedService` descriptor before `AddMassTransitTestHarness()`. Used a single `.Where(...).ToList()` + `services.Remove(d)` loop (the generic `RemoveAll<T>()` collided with MassTransit's own overload — CS0305 — so the descriptor-filter form is used to disambiguate).
- **Files modified:** `tests/BaseApi.Tests/Composition/HarnessWebAppFactory.cs`
- **Verification:** `dotnet build SK_P.sln -c Release` and `-c Debug` both exit 0, zero warnings; tests project compiles (harness API surface resolves).
- **Committed in:** `6f2cb2e` (build-fix commit)

---

**Total deviations:** 2 auto-fixed (2 blocking — 1 DI factory caller, 1 A1 fallback path).
**Impact on plan:** Both necessary for the solution to compile under the D-07 ctor change and the D-01 harness swap. The plan's "no Program.cs change" note held literally; the missed caller was the per-feature DI factory. A1 resolved to the manual fallback the plan itself anticipated. No scope creep.

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
