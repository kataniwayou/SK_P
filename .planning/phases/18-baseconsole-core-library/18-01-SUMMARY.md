---
phase: 18-baseconsole-core-library
plan: 01
subsystem: infra
tags: [dotnet8, masstransit, rabbitmq, stackexchange-redis, opentelemetry, generic-host, framework-reference, healthchecks, asynclocal]

# Dependency graph
requires:
  - phase: 17-messaging-contracts
    provides: Messaging.Contracts leaf assembly (ICorrelated vocabulary, Start/Stop contracts, L2 root read-shape) + CPM pins for MassTransit 8.5.5
provides:
  - "src/BaseConsole.Core class library (Sdk=Microsoft.NET.Sdk, FrameworkReference Microsoft.AspNetCore.App, net8.0, warning-clean) wired into SK_P.sln"
  - "RequiredConfig (public) fail-fast config accessors duplicated for the console side (D-08)"
  - "IStartupGate/StartupGate one-shot latch + StartupHealthCheck + Phase-5 StartupCompletionService (MarkReady-on-StartAsync, no DB/EF)"
  - "ConsoleRedisServiceCollectionExtensions.AddBaseConsoleRedis — soft-dep Singleton IConnectionMultiplexer (no options binding, no probe, no health check)"
  - "ICorrelationAccessor + AsyncLocalCorrelationAccessor (AsyncLocal<string?>)"
affects: [18-02 correlation filters, 18-03 health listener + AddBaseConsole composition root, 19 Orchestrator console]

# Tech tracking
tech-stack:
  added: [MassTransit 8.5.5, MassTransit.RabbitMQ 8.5.5 (console consumer), StackExchange.Redis 2.13.1 (console), OpenTelemetry 1.15.x console-flavored]
  patterns:
    - "Console-flavored OTel csproj: MEL-bridge logs + runtime instrumentation only; NO AspNetCore/Http instrumentation (worker host has no inbound HTTP request surface beyond minimal probes)"
    - "FrameworkReference Microsoft.AspNetCore.App on a Microsoft.NET.Sdk (non-Web) library to obtain Kestrel + HealthChecks without the Web SDK (CONSOLE-05)"
    - "D-08 dependency firewall: console base library duplicates the API base primitives (RequiredConfig, startup gate trio) rather than referencing BaseApi.Core; only ProjectReference is the leaf Messaging.Contracts"
    - "Phase-5 StartupCompletionService variant (gate.MarkReady on StartAsync, no EF/DbContext/scope-factory) — the console has no migration step"

key-files:
  created:
    - src/BaseConsole.Core/BaseConsole.Core.csproj
    - src/BaseConsole.Core/Configuration/RequiredConfig.cs
    - src/BaseConsole.Core/Health/IStartupGate.cs
    - src/BaseConsole.Core/Health/StartupHealthCheck.cs
    - src/BaseConsole.Core/Health/StartupCompletionService.cs
    - src/BaseConsole.Core/DependencyInjection/ConsoleRedisServiceCollectionExtensions.cs
    - src/BaseConsole.Core/Messaging/ICorrelationAccessor.cs
    - src/BaseConsole.Core/Messaging/AsyncLocalCorrelationAccessor.cs
  modified:
    - SK_P.sln

key-decisions:
  - "RequiredConfig made public (not internal as in BaseApi.Core) so the Plan 02/03 composition-root extensions can reach RequireConnectionString without InternalsVisibleTo"
  - "StartupHealthCheck Unhealthy message dropped the '(migrations pending)' suffix — console has no DB"
  - "AsyncLocalCorrelationAccessor stores string? (not Guid) to preserve arbitrary inbound HTTP correlation ids verbatim; outbound filter (Plan 02) does Guid.TryParse for the envelope"
  - "abortConnect=false is supplied by the caller's appsettings connection string, NOT hardcoded in AddBaseConsoleRedis — soft-dep boot resilience"
  - "Rephrased XML-doc/comment prose to avoid the literal forbidden tokens (BaseApi.Core, migrations pending, BaseDbContext, MigrateAsync, IServiceScopeFactory, RedisProjectionOptions) so the plan's strict grep verification gate passes against the working tree, not just code references"

patterns-established:
  - "Console base library mirrors the API base library by duplication behind the D-08 firewall (no cross-reference)"
  - "Soft-dep Redis registration with no startup probe and no health check (liveness independence enforced later in Plan 03)"

requirements-completed: [CONSOLE-03, CONSOLE-05, CONSOLE-HEALTH-04]

# Metrics
duration: 3min
completed: 2026-05-30
---

# Phase 18 Plan 01: BaseConsole.Core Foundation Summary

**New `src/BaseConsole.Core` Generic-Host class library (net8.0, FrameworkReference Microsoft.AspNetCore.App, no BaseApi.Core/EF/MVC) carrying the duplicated fail-fast config helper, the startup-gate trio with a Phase-5 completion service, a soft-dependency Redis Singleton, and an AsyncLocal<string?> correlation accessor — all warning-clean in Release + Debug.**

## Performance

- **Duration:** 3 min
- **Started:** 2026-05-30T08:37:31Z
- **Completed:** 2026-05-30T08:40:16Z
- **Tasks:** 3
- **Files modified:** 9 (8 created under src/BaseConsole.Core/ + SK_P.sln)

## Accomplishments
- Stood up the Wave-1 foundation library: `Sdk="Microsoft.NET.Sdk"` (not `.Web`), `FrameworkReference Microsoft.AspNetCore.App` (CONSOLE-05), console-flavored OTel (no AspNetCore/Http instrumentation), MassTransit/RabbitMQ + StackExchange.Redis, and the lone `Messaging.Contracts` ProjectReference (D-08 firewall held — zero BaseApi.Core/EF/MVC references).
- Landed the full startup-gate trio + Phase-5 `StartupCompletionService` (MarkReady-on-StartAsync, no DbContext) + public `RequiredConfig` (CONSOLE-HEALTH-04 primitives).
- Landed the soft-dependency Redis Singleton extension (no options binding, no probe, no health check — CONSOLE-03) and the `string?` AsyncLocal correlation accessor consumed by Plan 02's filters.
- Full solution build stays warning-clean (no regression to existing projects).

## Task Commits

Each task was committed atomically:

1. **Task 1: Create BaseConsole.Core.csproj + add to SK_P.sln** - `5c7e44c` (feat)
2. **Task 2: Duplicate RequiredConfig + startup gate trio (gate, health check, Phase-5 completion service)** - `72c7811` (feat)
3. **Task 3: Console Redis soft-dep extension + ICorrelationAccessor/AsyncLocalCorrelationAccessor** - `6a0b2e3` (feat)

**Plan metadata:** _(docs commit appended after STATE/ROADMAP updates)_

## Files Created/Modified
- `src/BaseConsole.Core/BaseConsole.Core.csproj` - The library project: net8.0 (inherited), FrameworkReference + MassTransit/RabbitMQ/Redis/OTel PackageReferences + Messaging.Contracts ProjectReference
- `src/BaseConsole.Core/Configuration/RequiredConfig.cs` - public Require / RequireConnectionString fail-fast accessors (D-08 duplicate)
- `src/BaseConsole.Core/Health/IStartupGate.cs` - IStartupGate + StartupGate (Volatile.Read / Interlocked.Exchange one-shot latch)
- `src/BaseConsole.Core/Health/StartupHealthCheck.cs` - IHealthCheck over IStartupGate (no migrations-pending suffix)
- `src/BaseConsole.Core/Health/StartupCompletionService.cs` - Phase-5 MarkReady-on-StartAsync IHostedService (no EF/DbContext)
- `src/BaseConsole.Core/DependencyInjection/ConsoleRedisServiceCollectionExtensions.cs` - AddBaseConsoleRedis Singleton IConnectionMultiplexer (soft dep)
- `src/BaseConsole.Core/Messaging/ICorrelationAccessor.cs` - string? Get()/Set(string?) contract
- `src/BaseConsole.Core/Messaging/AsyncLocalCorrelationAccessor.cs` - AsyncLocal<string?> implementation
- `SK_P.sln` - added the BaseConsole.Core project entry (via `dotnet sln add`)

## Decisions Made
- `RequiredConfig` is `public` here (vs `internal` in BaseApi.Core) so the Plan 02/03 composition-root extensions can call `RequireConnectionString` without `InternalsVisibleTo`.
- `StartupHealthCheck` Unhealthy message is "Startup not complete" (dropped the "(migrations pending)" suffix) — the console has no database.
- `AsyncLocalCorrelationAccessor` stores `string?` (not `Guid`) to preserve arbitrary inbound HTTP correlation ids verbatim for the log scope (Open-Q 2).
- `abortConnect=false` is left to the caller's connection string rather than hardcoded — preserves soft-dep boot resilience and keeps the extension config-driven.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Rephrased XML-doc prose to satisfy the strict grep verification gate**
- **Found during:** Tasks 1, 2, 3 (all three commits)
- **Issue:** The plan's `<verification>` includes "grep confirms no `BaseApi.Core` token anywhere under `src/BaseConsole.Core/`" and several acceptance criteria assert files do NOT contain the strings `migrations pending`, `BaseDbContext`, `MigrateAsync`, `IServiceScopeFactory`, `RedisProjectionOptions`. My first-pass explanatory comments referenced these tokens in prose (e.g., "mirror of BaseApi.Core", "does NOT call MigrateAsync"), which would trip a literal grep gate even though they were not code references.
- **Fix:** Rephrased the offending comments/XML-doc to convey the same intent without the literal tokens ("the API base library", "no database migration call", "a projection-options type"). The plan's stated intent for the BaseApi.Core check is "no namespace/using leak" — no code symbol ever referenced these; only prose did.
- **Files modified:** BaseConsole.Core.csproj, RequiredConfig.cs, StartupHealthCheck.cs, StartupCompletionService.cs
- **Verification:** `Grep` over `src/BaseConsole.Core/` for all forbidden tokens returns "No matches found"; build remains warning-clean.
- **Committed in:** `5c7e44c` / `72c7811` (folded into the respective task commits)

---

**Total deviations:** 1 auto-fixed (1 blocking — verification-gate compliance)
**Impact on plan:** Cosmetic comment edits only; no behavioral or structural change. No scope creep.

## Issues Encountered
None — all three `dotnet build` verifications (Release per task, plus Debug and full-solution at plan close) passed with zero warnings on the first attempt.

## User Setup Required
None - no external service configuration required (this plan registers types/primitives only; no endpoints, no message handling, no live Redis/RabbitMQ connection at build time).

## Next Phase Readiness
- Wave-1 foundation complete. `ICorrelationAccessor`/`AsyncLocalCorrelationAccessor` ready for Plan 02's inbound/outbound correlation filters (CORR-01/02). `IStartupGate`/`StartupHealthCheck`/`StartupCompletionService` ready for Plan 03's minimal-Kestrel health listener and the `AddBaseConsole` composition root. `AddBaseConsoleRedis` ready to be invoked from `AddBaseConsole` (Plan 03).
- No blockers. The library is intentionally composition-root-empty: DI wiring (`AddBaseConsole`/`AddBaseConsoleMessaging`/health endpoints) lands in Waves 2/3.

## Self-Check: PASSED
- All 8 created files verified present on disk (`git ls-files src/BaseConsole.Core/**` = 8).
- All 3 task commits verified in `git log`: `5c7e44c`, `72c7811`, `6a0b2e3`.

---
*Phase: 18-baseconsole-core-library*
*Completed: 2026-05-30*
