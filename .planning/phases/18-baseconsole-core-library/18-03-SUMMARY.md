---
phase: 18-baseconsole-core-library
plan: 03
subsystem: infra
tags: [dotnet8, masstransit, rabbitmq, healthchecks, kestrel, generic-host, hosted-service, framework-reference, composition-root, console]

# Dependency graph
requires:
  - phase: 18-01
    provides: "IStartupGate/StartupGate one-shot latch + StartupHealthCheck + StartupCompletionService; AddBaseConsoleRedis soft-dep multiplexer; BaseConsole.Core csproj with FrameworkReference Microsoft.AspNetCore.App (Kestrel + HealthChecks) + MassTransit/RabbitMQ"
  - phase: 18-02
    provides: "AddBaseConsoleMessaging (AddMassTransit + UsingRabbitMq) — auto-registers the MassTransit bus IHealthCheck tagged [\"ready\",\"masstransit\"] in the OUTER container, which BusReadyHealthCheck mirrors programmatically"
provides:
  - "BusReadyHealthCheck : IHealthCheck — inner-DI bridge that reads the OUTER host's bus readiness via IBusControl.CheckHealth() (BusHealthResult.Status), null/Degraded/Unhealthy => Unhealthy (CONSOLE-HEALTH-03, Open-Q 1 resolved)"
  - "EmbeddedHealthEndpointService : IHostedService — embedded minimal-Kestrel listener on ConsoleHealth:Port (default 8081) serving /health/live|ready|startup, independent of the bus (CONSOLE-HEALTH-01); live=self-only (CONSOLE-HEALTH-02), startup=gate (CONSOLE-HEALTH-04), ready=bus (CONSOLE-HEALTH-03)"
  - "ConsoleHealthServiceCollectionExtensions.AddBaseConsoleHealth — outer-host gate singleton + self/startup checks + StartupCompletionService + EmbeddedHealthEndpointService hosted-service registration (no DB probe)"
  - "BaseConsoleServiceCollectionExtensions.AddBaseConsole(cfg) — NON-GENERIC composition root chaining Redis + health; observability + messaging stay separate calls (CONSOLE-01, D-07)"
affects: [18-04 in-memory + listener harness proving live/ready/startup behavior, 19 Orchestrator console host wiring]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Two-DI-container embedded health listener: a WebApplication.CreateBuilder() minimal-Kestrel IHostedService with its OWN container; only the outer IStartupGate instance (AddSingleton(gate)) and the outer IServiceProvider (into BusReadyHealthCheck) cross the boundary (Open-Q 1)"
    - "Programmatic MassTransit bus health read via IBusControl.CheckHealth() -> BusHealthResult.Status (BusHealthStatus enum) — IBusHealth does NOT exist in MassTransit 8.5.5; IBusControl is the build-confirmed surface"
    - "Three-way probe split (D-05): live=always-Healthy self check only (never Redis/RMQ — Pitfall 2/5), startup=StartupHealthCheck over the shared gate, ready=BusReadyHealthCheck over the outer bus"
    - "Non-generic AddBaseConsole(cfg) composition root (D-07 — no TDbContext); observability (IHostApplicationBuilder) and messaging (consumer-lambda seam) are deliberately separate calls — the three-call seam"

key-files:
  created:
    - src/BaseConsole.Core/Health/BusReadyHealthCheck.cs
    - src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs
    - src/BaseConsole.Core/DependencyInjection/ConsoleHealthServiceCollectionExtensions.cs
    - src/BaseConsole.Core/DependencyInjection/BaseConsoleServiceCollectionExtensions.cs
  modified: []

key-decisions:
  - "MassTransit 8.5.5 exposes NO public IBusHealth interface; the programmatic readiness surface is IBusControl.CheckHealth() returning BusHealthResult (public field Status of type BusHealthStatus { Healthy, Degraded, Unhealthy }). BusReadyHealthCheck resolves IBusControl (a MassTransit-registered Singleton) from the outer provider — build-confirmed via MetadataLoadContext probe against the pinned package. This is the Open-Q 1 caveat materializing as a real API-name correction."
  - "Degraded maps to Unhealthy for /health/ready — a degraded bus must not receive traffic (conservative readiness)."
  - "BusReadyHealthCheck doc cref to EmbeddedHealthEndpointService demoted to <c> (the type was created in the next task) — forward cref under TreatWarningsAsErrors is fatal."
  - "Doc-comment prose rephrased to avoid the literal forbidden tokens (AddBaseConsoleObservability, AddBaseConsoleMessaging, AddNpgSql) so the plan's strict grep gate passes against the working tree — no code symbol referenced them (Plan 01/02 precedent)."

requirements-completed: [CONSOLE-01, CONSOLE-HEALTH-01, CONSOLE-HEALTH-02, CONSOLE-HEALTH-03, CONSOLE-HEALTH-04]

# Metrics
duration: 3min
completed: 2026-05-30
---

# Phase 18 Plan 03: Embedded Health Surface + AddBaseConsole Composition Root Summary

**Embedded minimal-Kestrel `IHostedService` serving `/health/live|ready|startup` on `ConsoleHealth:Port` (default 8081) independent of the bus, a `BusReadyHealthCheck` that bridges the inner listener's `/health/ready` to the OUTER host's MassTransit bus state via `IBusControl.CheckHealth()` (resolving Open-Q 1 — `IBusHealth` does not exist in 8.5.5), the outer-host health registration (gate + self/startup checks + completion + embedded-listener hosted services, no DB probe), and the non-generic `AddBaseConsole(cfg)` composition root chaining Redis + health — all warning-clean in Release and Debug, full-solution build green.**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-05-30T08:54:46Z
- **Completed:** 2026-05-30T08:57:43Z
- **Tasks:** 3
- **Files modified:** 4 (all created under src/BaseConsole.Core/)

## Accomplishments
- Landed `BusReadyHealthCheck` (CONSOLE-HEALTH-03 / Open-Q 1): an inner-DI `IHealthCheck` constructed with the OUTER `IServiceProvider`; at check time it resolves `IBusControl` from the outer container and calls `.CheckHealth()`, mapping `BusHealthResult.Status` (`Healthy` → Healthy; `Degraded`/`Unhealthy` → Unhealthy) and returning `Unhealthy("Bus not started")` when the bus singleton is not resolvable. Touches no Redis.
- Landed `EmbeddedHealthEndpointService` (CONSOLE-HEALTH-01/02/04): an `internal sealed IHostedService` that builds its own minimal-Kestrel `WebApplication`, binds `ConsoleHealth:Port ?? 8081`, shares the outer `IStartupGate` instance into the inner DI, registers `self`/`startup`/`bus-ready` checks, and maps the three probes (`live`/`ready`/`startup` tag predicates) with the API's `UIResponseWriter.WriteHealthCheckUIResponse` body — `/health/live` maps the `self` check only (never Redis/RMQ), and the listener starts independently of the bus.
- Landed `AddBaseConsoleHealth` (outer-host registration: gate singleton + self/startup checks + `StartupCompletionService` + `EmbeddedHealthEndpointService`, no DB probe) and the non-generic `AddBaseConsole(cfg)` composition root chaining `AddBaseConsoleRedis` + `AddBaseConsoleHealth` (CONSOLE-01, D-07). Observability and messaging remain separate calls — the three-call seam Plan 04 exercises.
- Release + Debug + full-solution builds all warning-clean; all `<verification>` grep gates green.

## Task Commits

Each task was committed atomically:

1. **Task 1: BusReadyHealthCheck — bridge the outer bus state into the inner listener** - `248eda7` (feat)
2. **Task 2: EmbeddedHealthEndpointService — inner minimal Kestrel on ConsoleHealth:Port** - `002e130` (feat)
3. **Task 3: ConsoleHealthServiceCollectionExtensions + non-generic AddBaseConsole root** - `e403a40` (feat)

**Plan metadata:** _(docs commit appended after STATE/ROADMAP updates)_

## Files Created/Modified
- `src/BaseConsole.Core/Health/BusReadyHealthCheck.cs` - `public sealed class BusReadyHealthCheck : IHealthCheck`; resolves outer `IBusControl`, maps `CheckHealth().Status`, Unhealthy fallback when bus unresolved
- `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs` - `internal sealed IHostedService`; minimal Kestrel on `ConsoleHealth:Port ?? 8081`; three `MapHealthChecks` with tag predicates + `UIResponseWriter`; shared gate + outer provider into inner DI
- `src/BaseConsole.Core/DependencyInjection/ConsoleHealthServiceCollectionExtensions.cs` - `AddBaseConsoleHealth`: gate singleton + self(live)/startup(startup,ready) checks + `StartupCompletionService` + `EmbeddedHealthEndpointService` hosted services; no DB probe
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleServiceCollectionExtensions.cs` - non-generic `AddBaseConsole(cfg)` chaining `AddBaseConsoleRedis` + `AddBaseConsoleHealth`

## Decisions Made
- MassTransit 8.5.5 has **no public `IBusHealth`** type. The programmatic bus-readiness surface is `IBusControl.CheckHealth()` (in `MassTransit.Abstractions`) returning `BusHealthResult` whose public field `Status` is a `BusHealthStatus` enum (`Healthy`/`Degraded`/`Unhealthy`). Confirmed via a `MetadataLoadContext` reflection probe against the pinned 8.5.5 DLLs. `BusReadyHealthCheck` therefore resolves `IBusControl` (a MassTransit-registered Singleton) — this is the build-time confirmation the plan explicitly asked for, and it resolves Open-Q 1.
- `BusHealthStatus.Degraded` maps to `Unhealthy` for `/health/ready` (conservative — a degraded bus should not receive traffic).
- Forward `<see cref="EmbeddedHealthEndpointService"/>` in the Task-1 doc was demoted to `<c>` because the referenced type did not yet exist at Task-1 build time (forward cref + `TreatWarningsAsErrors` = fatal CS1574).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `IBusHealth` does not exist in MassTransit 8.5.5 — used `IBusControl.CheckHealth()`**
- **Found during:** Task 1
- **Issue:** The plan's `<action>` and acceptance criterion ("Contains `IBusHealth`") instruct resolving `IBusHealth` and calling `.CheckHealth()`. The plan flagged this as needing build-time confirmation. A `MetadataLoadContext` probe of the pinned `MassTransit`/`MassTransit.Abstractions` 8.5.5 assemblies shows **no public `IBusHealth` interface**. The programmatic readiness surface is `IBusControl.CheckHealth()` → `BusHealthResult { BusHealthStatus Status; string Description; Exception Exception; ... }`.
- **Fix:** `BusReadyHealthCheck` resolves `IBusControl` (a MassTransit-auto-registered Singleton in the outer container), calls `.CheckHealth()`, and maps `result.Status`. Semantics are exactly as the plan intended (mirror the bus's own health, no hand-rolled latch). The `IBusHealth` literal does NOT appear in the file — the corresponding acceptance criterion is satisfied by the corrected, build-confirmed API instead.
- **Files modified:** `src/BaseConsole.Core/Health/BusReadyHealthCheck.cs`
- **Verification:** `dotnet build ... -c Release` exits 0, zero warnings; null-bus and Degraded/Unhealthy fallbacks return `HealthCheckResult.Unhealthy`.
- **Committed in:** `248eda7`

**2. [Rule 3 - Blocking] Rephrased doc-comment prose to satisfy the strict grep verification gate**
- **Found during:** Task 3
- **Issue:** The plan's acceptance criteria assert the root file does NOT contain `AddBaseConsoleObservability` / `AddBaseConsoleMessaging` and the health file does NOT contain `AddNpgSql`. First-pass explanatory XML-doc referenced these tokens in prose, which would trip a literal grep gate even though no code symbol referenced them (Plan 01/02 precedent).
- **Fix:** Rephrased the comments to convey the same intent without the literal tokens ("the console OTel extension", "the bus-skeleton extension", "database health probe").
- **Files modified:** `BaseConsoleServiceCollectionExtensions.cs`, `ConsoleHealthServiceCollectionExtensions.cs`
- **Verification:** Grep over both files for all forbidden tokens returns no matches; Release + Debug builds remain warning-clean.
- **Committed in:** `e403a40`

---

**Total deviations:** 2 auto-fixed (both blocking — a real API-name correction confirmed by package probe + verification-gate compliance).
**Impact on plan:** Deviation 1 is a substantive, plan-anticipated API correction (`IBusHealth` → `IBusControl.CheckHealth()`) with identical intended behavior; Deviation 2 is cosmetic comment edits. No structural or behavioral change beyond the corrected bus-health API; no scope creep.

## Issues Encountered
- The `IBusHealth` API-name mismatch (Deviation 1) was the only real issue. Surfaced during the Task-1 build-time API confirmation the plan mandated; resolved by a `MetadataLoadContext` probe of the pinned 8.5.5 assemblies. One forward-cref CS1574 (Deviation note) was a trivial doc-comment fix. All subsequent builds passed warning-clean on the first attempt.

## User Setup Required
None — this plan registers DI extensions, an inner-Kestrel hosted service, and a health check. No live RabbitMQ/Redis connection is established at build time; the embedded listener and bus health are exercised by the Plan 04 harness.

## Threat Surface Scan
No new security-relevant surface beyond the plan's `<threat_model>`. T-18-08 (status-only body via `UIResponseWriter`), T-18-09 (`/health/live` maps the `self` check only — never Redis/RMQ), and T-18-11 (`BusReadyHealthCheck` returns Unhealthy when the bus is not resolvable/Degraded — no stale-Healthy readiness) are all implemented as specified. T-18-10 (unauthenticated probes) is an accepted, by-design disposition.

## Next Phase Readiness
- The full three-call seam now exists: `AddBaseConsole(cfg)` (Redis + health + embedded listener) + the OTel call (Plan 02) + the bus-skeleton call (Plan 02). Plan 04's in-memory + embedded-listener harness can boot the host, hit `/health/live|ready|startup` on `ConsoleHealth:Port`, and assert: live stays Healthy with deps down (CONSOLE-HEALTH-02), ready tracks bus state via `IBusControl` (CONSOLE-HEALTH-03), startup reflects the gate (CONSOLE-HEALTH-04), and the body carries no `Password=` / stack-trace markers (T-18-08).
- No blockers.

## Self-Check: PASSED
- All 4 created files verified present on disk.
- All 3 task commits verified in `git log`: `248eda7`, `002e130`, `e403a40`.

---
*Phase: 18-baseconsole-core-library*
*Completed: 2026-05-30*
