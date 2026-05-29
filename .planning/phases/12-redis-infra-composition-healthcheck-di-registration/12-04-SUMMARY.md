---
phase: 12-redis-infra-composition-healthcheck-di-registration
plan: 04
subsystem: infra
tags: [redis, stackexchange-redis, di, composition-root, ioptions, singleton, connection-multiplexer]

# Dependency graph
requires:
  - phase: 12-01
    provides: StackExchange.Redis 2.13.1 CPM pin in Directory.Packages.props
  - phase: 12-03
    provides: ConnectionStrings:Redis (abortConnect=false) + Redis:* section in appsettings.json
provides:
  - public sealed RedisProjectionOptions POCO bound to Redis:* (KeyPrefix + Serialization.JsonOptions)
  - internal AddBaseApiRedis extension registering Singleton IConnectionMultiplexer + Configure<RedisProjectionOptions>
  - composition root AddBaseApi<TDbContext> extended to a 7-call chain (.AddBaseApiRedis(cfg) as call #7)
affects: [12-05, 12-06, 12-07, phase-15-redis-projection-writer]

# Tech tracking
tech-stack:
  added: [StackExchange.Redis (PackageReference added to BaseApi.Core.csproj; CPM pin existed from 12-01)]
  patterns:
    - "Singleton IConnectionMultiplexer via factory closure capturing RequireConnectionString(\"Redis\") (D-14)"
    - "IDatabase NOT registered as DI service — consumers call multiplexer.GetDatabase() per-call (INFRA-COMP-03)"
    - "services.Configure<TOptions>(cfg.GetSection(\"X\")) options-binding mirrors Phase 5/7 extension shape"

key-files:
  created:
    - src/BaseApi.Core/Configuration/RedisProjectionOptions.cs
    - src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs
  modified:
    - src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs
    - src/BaseApi.Core/BaseApi.Core.csproj
    - tests/BaseApi.Tests/Composition/AddBaseApiFacts.cs

key-decisions:
  - "RedisProjectionOptions is public sealed (Phase 15 writer reads IOptions<> from a different assembly); RedisServiceCollectionExtensions is internal static (same-assembly chain)"
  - "ConnectionMultiplexer.Connect synchronous inside Singleton factory is safe — conn string carries abortConnect=false (12-03); boot never crashes on dead Redis"
  - "XML <see cref> for the overloaded ConnectionMultiplexer.Connect uses the fully-qualified (string, TextWriter) overload signature to avoid CS1574 under TreatWarningsAsErrors"

patterns-established:
  - "Redis L2 client wiring mirrors AddBaseApiPersistence/AddBaseApiHealth internal-static + RequireConnectionString fail-fast discipline"
  - "Composition-root growth = exactly one chained line appended after AddBaseApiMapping; prior 6-call order preserved verbatim"

requirements-completed: [INFRA-COMP-01, INFRA-COMP-02, INFRA-COMP-03, INFRA-COMP-04]

# Metrics
duration: ~12min
completed: 2026-05-29
---

# Phase 12 Plan 04: Redis DI Registration Surface Summary

**Singleton `IConnectionMultiplexer` + `RedisProjectionOptions` binding landed via internal `AddBaseApiRedis`, chained as composition-root call #7 after `AddBaseApiMapping`; `IDatabase` deliberately not registered.**

## Performance

- **Duration:** ~12 min
- **Started:** 2026-05-29T04:10Z (approx)
- **Completed:** 2026-05-29
- **Tasks:** 1
- **Files modified:** 5 (2 created, 3 modified)

## Accomplishments
- `RedisProjectionOptions` POCO (public sealed) bound to `Redis:*` with `KeyPrefix="skp:"` + `Serialization.JsonOptions="default"` — D-15 YAGNI surface only (no `Database`/`CommandFlags`/`ConnectionString`).
- `RedisServiceCollectionExtensions.AddBaseApiRedis` (internal static): registers `IConnectionMultiplexer` as Singleton via `ConnectionMultiplexer.Connect(cfg.RequireConnectionString("Redis"))` + `services.Configure<RedisProjectionOptions>(cfg.GetSection("Redis"))`. `IDatabase` not registered (INFRA-COMP-03).
- `AddBaseApi<TDbContext>` extended to a 7-call fluent chain — `.AddBaseApiRedis(cfg)` appended immediately after `.AddBaseApiMapping(...)`; existing 6-call order (Persistence→Health→ErrorHandling→Http→Validation→Mapping) preserved verbatim.
- D-05/D-06 byte-immutable invariant verified: `git diff` empty for `StartupCompletionService.cs` and `HealthServiceCollectionExtensions.cs`.
- 0-warning Debug + Release build (TreatWarningsAsErrors=true); 142/142 v3.2.0 facts GREEN under `dotnet test --no-build`.
- No EF migration generated (negative schema-push assertion: `src/BaseApi.Service/Persistence/Migrations/` has no working-tree churn).

## Task Commits

1. **Task 1: Create RedisProjectionOptions POCO + RedisServiceCollectionExtensions, chain into composition root** - `ab4308c` (feat)

**Plan metadata:** (final docs commit — this SUMMARY + STATE/ROADMAP/REQUIREMENTS)

_Task 1 carried `tdd="true"`, but per the plan the RED/GREEN xUnit facts (BaseApiCompositionFacts + RedisProjectionOptionsBindingFacts) live in Plan 12-07. This plan ships production code only; the source-level acceptance was verified by build + grep + the existing 142-fact regression suite. Single feat commit accordingly._

## Files Created/Modified
- `src/BaseApi.Core/Configuration/RedisProjectionOptions.cs` - public sealed options POCO (KeyPrefix + nested Serialization.JsonOptions)
- `src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs` - internal AddBaseApiRedis (Singleton multiplexer + options binding)
- `src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs` - chain extended by one line (call #7)
- `src/BaseApi.Core/BaseApi.Core.csproj` - StackExchange.Redis PackageReference added (CPM, no Version=)
- `tests/BaseApi.Tests/Composition/AddBaseApiFacts.cs` - in-memory config now supplies ConnectionStrings:Redis

## Decisions Made
- `public sealed` for the options POCO (cross-assembly read by Phase 15) vs `internal static` for the extension (same-assembly chain) — matches Persistence/Health extension conventions.
- Used the fully-qualified `ConnectionMultiplexer.Connect(string, TextWriter)` overload in the XML `<see cref>` doc comments because `Connect` is overloaded; a single-arg cref risks CS1574, which is build-fatal under TreatWarningsAsErrors=true. Functionally identical to the plan's verbatim body; only the doc-comment cref target differs.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Added StackExchange.Redis PackageReference to BaseApi.Core.csproj**
- **Found during:** Task 1 (first Release build)
- **Issue:** `RedisServiceCollectionExtensions.cs` failed to compile — CS0246 "namespace StackExchange could not be found". Plan 12-01 added the StackExchange.Redis 2.13.1 pin to `Directory.Packages.props` (CPM) but did NOT add a `<PackageReference>` to the consuming `BaseApi.Core.csproj`; this plan is the first consumer.
- **Fix:** Added `<PackageReference Include="StackExchange.Redis" />` (no `Version=` per CPM / Phase 1 D-05/D-06) in a new ItemGroup with a Phase-12 provenance comment.
- **Files modified:** `src/BaseApi.Core/BaseApi.Core.csproj`
- **Verification:** `dotnet build SK_P.sln` succeeds 0-warning in both Release and Debug.
- **Committed in:** `ab4308c` (Task 1 commit)

**2. [Rule 1 - Regression] Added ConnectionStrings:Redis to AddBaseApiFacts in-memory config**
- **Found during:** Task 1 (first `dotnet test --no-build`)
- **Issue:** 6 existing `AddBaseApiFacts` composition tests failed. They build `AddBaseApi<AppDbContext>(cfg)` from a pure in-memory `IConfiguration` that supplies only `ConnectionStrings:Postgres`. The new call #7 `AddBaseApiRedis` fail-fasts via `RequireConnectionString("Redis")` → `InvalidOperationException` at DI-graph construction. Regression directly caused by this plan's chain extension. (WebAppFactory-based tests were unaffected because Plan 12-03 already added `ConnectionStrings:Redis` to `appsettings.json`.)
- **Fix:** Added `["ConnectionStrings:Redis"] = "localhost:6379,abortConnect=false"` to the test's `AddInMemoryCollection` dictionary, mirroring the existing Postgres entry. No test resolves `IConnectionMultiplexer`, so the lazy `Connect` (D-17) never fires; `abortConnect=false` keeps even an eager probe non-fatal.
- **Files modified:** `tests/BaseApi.Tests/Composition/AddBaseApiFacts.cs`
- **Verification:** Test project rebuilt 0-warning; full suite 142/142 GREEN.
- **Committed in:** `ab4308c` (Task 1 commit)

---

**Total deviations:** 2 auto-fixed (1 blocking dependency wiring, 1 regression in existing test config)
**Impact on plan:** Both auto-fixes were strictly necessary to compile and to keep the existing 142-fact suite GREEN. No scope creep — the package reference is the missing half of Plan 12-01's CPM pin, and the test-config addition is the natural consequence of a fail-fast chain call. No production-behavior change beyond the plan's intent.

## Issues Encountered
- The 6 initial test failures looked at first glance like the known-flaky OTel observability E2E tests (Plan 06-02 precedent), but log inspection (UTF-16 decode) confirmed they were `AddBaseApiFacts` failing on the new Redis fail-fast guard — a genuine regression, fixed as deviation #2 rather than re-run.

## Verification Evidence
- `dotnet build SK_P.sln --configuration Release` → Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet build SK_P.sln --configuration Debug` → Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test tests/BaseApi.Tests --no-build --configuration Release` → Passed: 142, Failed: 0, Skipped: 0 (Duration 2m 50s).
- `git diff src/BaseApi.Core/Health/StartupCompletionService.cs` → empty (D-05).
- `git diff src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` → empty (D-06).
- `git status src/BaseApi.Service/Persistence/Migrations/` → no changes (no EF migration generated).
- Positive greps (all = 1 match): `public sealed class RedisProjectionOptions`, `KeyPrefix { get; set; } = "skp:"`, `JsonOptions { get; set; } = "default"`, `internal static class RedisServiceCollectionExtensions`, `services.AddSingleton<IConnectionMultiplexer>`, `ConnectionMultiplexer.Connect(connStr)`, `services.Configure<RedisProjectionOptions>(cfg.GetSection("Redis"))`, `cfg.RequireConnectionString("Redis")`.
- Negative greps (all empty in RedisServiceCollectionExtensions.cs): `AddScoped/AddTransient<IConnectionMultiplexer>`, `AddSingleton/AddScoped<IDatabase>`, `AddHostedService`, `allowAdmin=true`.
- Chain-order: `.AddBaseApiRedis(cfg)` confirmed immediately after `.AddBaseApiMapping(...)` (multiline grep).

## Next Phase Readiness
- Plan 12-05 unblocked: `RedisProjectionOptions` is the bind target for `Phase8WebAppFactory.AddInMemoryCollection["Redis:KeyPrefix"]` per-class overrides.
- Plan 12-07 unblocked: `BaseApiCompositionFacts` can assert the DI chain order, Singleton lifetime (reference-equality across scopes), `IDatabase`-not-registered, and the `RedisProjectionOptions` binding/default behaviors against this production code.
- No blockers.

---
*Phase: 12-redis-infra-composition-healthcheck-di-registration*
*Completed: 2026-05-29*

## Self-Check: PASSED
- FOUND: src/BaseApi.Core/Configuration/RedisProjectionOptions.cs
- FOUND: src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs
- FOUND: .planning/phases/12-redis-infra-composition-healthcheck-di-registration/12-04-SUMMARY.md
- FOUND: commit ab4308c
