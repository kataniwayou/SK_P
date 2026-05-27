---
phase: 08-entity-build-out-migrations-docker-runtime-tests
plan: 01
subsystem: infra
tags: [docker, dotnet-ef, cpm, mapperly, jsonschema, cronos, postgres, webapplicationfactory]

# Dependency graph
requires:
  - phase: 01-repository-scaffold
    provides: CPM contract (Directory.Packages.props), TreatWarningsAsErrors, .NET 8.0.421 pin
  - phase: 02-postgres-docker-compose
    provides: compose.yaml postgres + otel-collector services; .env tokens; depends_on service_healthy
  - phase: 05-observability-health-probes
    provides: /health/ready endpoint; IStartupGate one-shot latch; OTel Collector service
  - phase: 07-generic-http-base-composition-root
    provides: AppDbContext shell, Phase7WebAppFactory analog, AddBaseApi<TDbContext> composition root
provides:
  - Multistage Dockerfile (sdk:8.0-bookworm-slim -> aspnet:8.0-bookworm-slim) + .dockerignore
  - compose.yaml baseapi-service block running under default profile (build:., env, ports, healthcheck)
  - dotnet-ef 8.0.27 pinned as local tool via .config/dotnet-tools.json
  - 4 PackageReferences on BaseApi.Service.csproj (Riok.Mapperly, EFCore.Design, JsonSchema.Net, Cronos)
  - Phase8WebAppFactory (encapsulated PostgresFixture, public-class-not-sealed for 08-08 subclass)
  - REQUIREMENTS.md amended: TEST-03 + TEST-04 deferred to v2 with verbatim D-05/D-06 reasoning
affects:
  - Wave B plans 08-02 .. 08-06 (5 entity smoke tests consume Phase8WebAppFactory)
  - Wave C plan 08-07 (dotnet ef migrations add InitialCreate requires local tool + design package)
  - Wave C plan 08-08 (MigrationFailureWebAppFactory subclasses Phase8WebAppFactory via protected ctor)

# Tech tracking
tech-stack:
  added:
    - Docker multistage build (mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim + aspnet:8.0-bookworm-slim)
    - Riok.Mapperly 4.3.1 (CPM) — source-gen mapping for entity mappers
    - Microsoft.EntityFrameworkCore.Design 8.0.27 (CPM) — design-time tooling for migrations
    - JsonSchema.Net 9.2.1 (CPM) — Schema.Definition meta-validation
    - Cronos 0.13.0 (CPM) — Workflow.CronExpression parse
    - dotnet-ef 8.0.27 local tool (.config/dotnet-tools.json)
  patterns:
    - "USER app (uid 1654) non-root container runtime"
    - "Docker double-underscore env-var convention (ConnectionStrings__Postgres) for ASP.NET Core config keys with colons"
    - "Test fixture encapsulates PostgresFixture internally to avoid IClassFixture<> ordering (Plan 05-02 Pattern C reinforced)"
    - "Public-class-not-sealed pattern for forward-looking subclassing (Phase 6 WebAppFactory precedent extended)"
    - "Protected ctor overload for deliberately-bad-input subclasses (08-08 MigrationFailureWebAppFactory)"
    - ".dockerignore excludes tests/, .planning/, .env*, *.md, compose.yaml — minimizes T-08-01-SECRET-LEAK-IMAGE surface"

key-files:
  created:
    - Dockerfile
    - .dockerignore
    - .config/dotnet-tools.json
    - tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs
  modified:
    - compose.yaml
    - src/BaseApi.Service/BaseApi.Service.csproj
    - .planning/REQUIREMENTS.md

key-decisions:
  - "Multistage Dockerfile lands at repo root (NOT under src/BaseApi.Service/) so .dockerignore at the same level governs context — Phase 8 D-13 verbatim"
  - "Drop phase-8 profile gate; baseapi-service runs under default docker compose up — Phase 8 D-14"
  - "Docker env-var convention ConnectionStrings__Postgres (double-underscore) because env-vars cannot contain colons on Windows; ASP.NET Core normalizes __ -> : at read time"
  - "otel-collector depends_on uses service_started (not service_healthy) because collector container declares no healthcheck"
  - "Phase8WebAppFactory does NOT pre-create schema — migration runs at first webapp boot via D-15 swap (Wave C 08-07); model-shortcut create produces a schema DIFFERENT from migrations"
  - "TEST-03 (Testcontainers) + TEST-04 (Respawn) deferred to v2 — PostgresFixture pattern is proven across 98 facts; Respawn would invalidate Phase 3 D-15 byte-identical psql l no-leak proof"
  - "All 4 new PackageReferences omit Version= attributes (CPM contract per Phase 1 D-05 preserved)"
  - "dotnet-tools.json created manually (NOT via dotnet new tool-manifest) for deterministic byte content across hosts"

patterns-established:
  - "Pattern: Docker build context root layout — Dockerfile at repo root, .dockerignore at repo root, COPY src/ src/ from build stage; restore-then-publish layered to maximize cache hits on csproj-only changes"
  - "Pattern: Phase8WebAppFactory env-var-in-ctor analog — ConnectionStrings:Postgres injected via AddInMemoryCollection in ConfigureWebHost before base.ConfigureWebHost; PostgresFixture initialized in IAsyncLifetime.InitializeAsync (runs BEFORE first CreateClient)"
  - "Pattern: NpgsqlConnection.ClearPool(specific-conn) on DisposeAsync — NOT process-wide ClearAllPools (WR-05 mitigation, parallel xUnit v3 class isolation)"
  - "Pattern: v1 -> v2 deferral with reasoning paragraph + traceability table update + per-phase coverage count adjustment + total-mapped split (100 v1 + 2 v2 = 102)"

requirements-completed:
  - INFRA-05
  - TEST-01
  - TEST-02

# Metrics
duration: 7min
completed: 2026-05-27
---

# Phase 8 Plan 01: Wave A Foundation Summary

**Multistage Dockerfile + compose.yaml mutation + 4 PackageReferences + dotnet-ef 8.0.27 + Phase8WebAppFactory + REQUIREMENTS.md TEST-03/04 v2 deferral — Wave A foundation that unblocks all Wave B entity plans and Wave C migration + cross-entity tests.**

## Performance

- **Duration:** ~7 min
- **Started:** 2026-05-27T19:07:02Z
- **Completed:** 2026-05-27T19:13:54Z
- **Tasks:** 5
- **Files created:** 4 (Dockerfile, .dockerignore, .config/dotnet-tools.json, Phase8WebAppFactory.cs)
- **Files modified:** 3 (compose.yaml, BaseApi.Service.csproj, REQUIREMENTS.md)

## Accomplishments

- **Runtime image build path live:** Multistage Dockerfile (sdk:8.0-bookworm-slim build stage -> aspnet:8.0-bookworm-slim runtime), USER app (non-root uid 1654), /p:UseAppHost=false (no platform-specific apphost binary), ENTRYPOINT `dotnet BaseApi.Service.dll`. `.dockerignore` shrinks context (excludes `tests/`, `**/bin`, `**/obj`, `.git`, `.planning/`, `*.md`, `compose.yaml`, `.env*`, `.vs`, `.vscode/`).
- **compose.yaml mutated to run baseapi-service under default profile:** dropped phase-8 profile gate + placeholder image; added `build: { context: ., dockerfile: Dockerfile }`, env block (ASPNETCORE_ENVIRONMENT, ConnectionStrings__Postgres with double-underscore Docker convention, OTEL_EXPORTER_OTLP_ENDPOINT), ports 8080:8080, wget healthcheck against /health/ready (start_period 30s for migration window), depends_on postgres service_healthy (preserved) + otel-collector service_started (added). `docker compose config` resolves cleanly with env-var substitution from `.env`.
- **CPM additions land deterministically:** 4 PackageReferences on `BaseApi.Service.csproj` — Riok.Mapperly (PrivateAssets=all ExcludeAssets=runtime), Microsoft.EntityFrameworkCore.Design (PrivateAssets=all), JsonSchema.Net, Cronos. All 4 omit `Version=` attributes; versions resolve from existing Directory.Packages.props CPM pins. `grep "Version=" src/BaseApi.Service/BaseApi.Service.csproj` confirms zero leaks on the new packages.
- **dotnet-ef 8.0.27 pinned as local tool:** `.config/dotnet-tools.json` created with `"version": "8.0.27"` matching CPM EF Core pin. `dotnet tool restore` succeeds; `dotnet ef --version` reports `8.0.27`. Wave C 08-07 unblocked for `dotnet ef migrations add InitialCreate`.
- **Phase8WebAppFactory ready for Wave B consumption:** `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` — public class (NOT sealed) inheriting `WebAppFactory, IAsyncLifetime`, encapsulates `PostgresFixture` internally (no second `IClassFixture<>` slot ordering issue), injects `ConnectionStrings:Postgres` via AddInMemoryCollection (Plan 05-02 Pattern C reinforced), `protected Phase8WebAppFactory(string connectionStringOverride)` ctor exposed for Plan 08-08 `MigrationFailureWebAppFactory` subclass, `NpgsqlConnection.ClearPool(scoped-conn)` on DisposeAsync (WR-05 mitigation), does NOT pre-create schema — migration runs at first webapp boot via D-15 swap.
- **REQUIREMENTS.md honest v1 -> v2 deferral:** TEST-03 (Testcontainers) and TEST-04 (Respawn) removed from v1 Testing section; added under v2 Hardening with verbatim D-05/D-06 reasoning paragraphs; traceability table rows updated to `v2 | Deferred`; Phase 8 per-phase coverage line dropped from 41 -> 39 active REQ-IDs; total-mapped summary now `100 v1 + 2 v2 = 102 (100%)`.

## Task Commits

Each task was committed atomically:

1. **Task 1: Create Dockerfile + .dockerignore** — `aeb4fe5` (feat)
2. **Task 2: Add 4 PackageReferences + .config/dotnet-tools.json** — `8960606` (feat)
3. **Task 3: Mutate compose.yaml — drop phase-8 profile, switch to build:** — `fba0dac` (feat)
4. **Task 4: Create Phase8WebAppFactory.cs** — `26994be` (feat)
5. **Task 5: Amend REQUIREMENTS.md — TEST-03 + TEST-04 to v2** — `7932326` (docs)

**Plan metadata commit:** to follow this SUMMARY.md write (docs(08-01): complete Wave A foundation).

## Files Created/Modified

### Created
- `Dockerfile` — Multistage build: sdk:8.0-bookworm-slim -> aspnet:8.0-bookworm-slim; USER app; ENTRYPOINT dotnet BaseApi.Service.dll
- `.dockerignore` — Excludes tests/, **/bin, **/obj, .git, .planning/, *.md, compose.yaml, .env* — minimizes T-08-01-SECRET-LEAK-IMAGE surface
- `.config/dotnet-tools.json` — dotnet-ef 8.0.27 local tool manifest (matches CPM EF Core pin)
- `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` — Public-class-not-sealed test fixture; encapsulates PostgresFixture; protected ctor for 08-08 subclass

### Modified
- `compose.yaml` — baseapi-service block: drop phase-8 profile + placeholder image; add build:., env, ports, /health/ready healthcheck, otel-collector service_started depends_on
- `src/BaseApi.Service/BaseApi.Service.csproj` — 4 new PackageReferences (Riok.Mapperly, EFCore.Design, JsonSchema.Net, Cronos); all CPM (no Version= attributes)
- `.planning/REQUIREMENTS.md` — TEST-03 + TEST-04 moved from v1 Testing to v2 Hardening; traceability + coverage counts updated

## Decisions Made

- **Use `mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim` (NOT alpine) as runtime base.** RESEARCH §"Docker .NET 8 Multistage Verification" confirms bookworm-slim ships `wget` (used by compose healthcheck) and `dotnet`. Alpine would require `apk add curl` or a separate healthcheck strategy.
- **Phase8WebAppFactory does NOT rewire AppDbContext.** Unlike Phase7WebAppFactory (which had to substitute Phase7TestDbContext because AppDbContext is a Phase 7 placeholder with no DbSets), Wave C 08-07 will populate AppDbContext with real DbSets — Phase 8 entity tests use the production composition.
- **CPM contract preserved on all 4 new PackageReferences.** Plan 01-01 D-05 mandates zero `Version=` attributes outside Directory.Packages.props. All 4 new PackageReferences resolve from existing pins (Riok.Mapperly 4.3.1, EFCore.Design 8.0.27, JsonSchema.Net 9.2.1, Cronos 0.13.0).
- **Dropping phase-8 profile is safe.** Plan 02-02 added the profile as a defensive gate so `docker compose up` would skip the placeholder. Now that `build: .` resolves a real Dockerfile, the profile gate adds friction without value — default-profile inclusion matches Phase 8 D-14 spec.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Phase8WebAppFactory.cs missing `using Xunit;` for IAsyncLifetime**
- **Found during:** Task 4 (initial `dotnet build` failure)
- **Issue:** Plan body verbatim code omitted `using Xunit;`; build error CS0246 "type or namespace IAsyncLifetime could not be found"
- **Fix:** Added `using Xunit;` to file header
- **Files modified:** `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs`
- **Verification:** `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` succeeds with 0 warnings
- **Committed in:** `26994be` (part of Task 4 commit)

**2. [Rule 1 - Bug] Phase8WebAppFactory.cs PostgresFixture name ambiguous between Middleware and Persistence namespaces**
- **Found during:** Task 4 (initial `dotnet build` failure)
- **Issue:** Build error CS0104 "PostgresFixture is an ambiguous reference between BaseApi.Tests.Middleware.PostgresFixture and BaseApi.Tests.Persistence.PostgresFixture"
- **Fix:** Added type alias `using PostgresFixture = BaseApi.Tests.Persistence.PostgresFixture;` (matches Phase7WebAppFactory.cs pattern verbatim)
- **Files modified:** `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs`
- **Verification:** Tests project builds clean with 0 warnings; type resolves to Persistence-flavored fixture (the `stepsdb_test_*` DB-name prefix Phase 3 D-15 snapshot discipline tracks)
- **Committed in:** `26994be` (part of Task 4 commit)

**3. [Rule 1 - Internal plan inconsistency] Plan verify grep forbids "EnsureCreatedAsync" but plan body comment uses the literal token**
- **Found during:** Task 4 (after compile fix, while running verify acceptance criteria)
- **Issue:** Plan `<verify>` automated check required `! grep -q "EnsureCreatedAsync"` while plan `<action>` verbatim code instructed a comment containing the literal string "EnsureCreated would produce a schema DIFFERENT from migrations". Both cannot simultaneously hold.
- **Fix:** Rephrased the educational comment from "Phase 8 deliberately does NOT call EnsureCreatedAsync..." to "Phase 8 deliberately does NOT pre-create the schema here... the model-shortcut create-from-model API would produce a schema DIFFERENT from migrations..." Preserves educational intent (explains why we don't pre-create) without using the literal forbidden token. Mirrors the Plan 06-01 mitigation (MP-code rephrase to satisfy grep-empty assertion).
- **Files modified:** `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs`
- **Verification:** `grep -q "EnsureCreatedAsync" tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` returns no matches; build still 0-warning
- **Committed in:** `26994be` (part of Task 4 commit)

---

**Total deviations:** 3 auto-fixed (3× Rule 1 — 2 build-blocking + 1 plan internal inconsistency)
**Impact on plan:** All auto-fixes were build/plan-correctness necessities. The 2 build fixes mirror Phase7WebAppFactory's working pattern verbatim. The comment rephrase preserves educational intent. No scope creep; no architectural changes.

## Issues Encountered

**Phase 5 OTel Collector warmup flake on first regression run.** First `dotnet test SK_P.sln --filter "FullyQualifiedName!~Phase8"` run after Phase8WebAppFactory.cs landed reported 7 failures (almost certainly the documented Phase 5 LogExport/LogLevelFilter/MetricsExport/TraceExport pattern). Second run (no changes) reported 98/98 PASSED in 18.18s. Third confirmation also 98/98 PASSED. This matches the documented Plan 06-02 SUMMARY note ("Run 1 of 3 had 7 Phase 5 Observability test failures — OTel Collector had been up only 28s when Run 1 started, telemetry hadn't fully batched/scraped. Runs 2/3/4 all 76/76 GREEN.") and Plan 05-02's known fixture-lifecycle robustness items. No Phase 8 code change needed; documented for posterity.

## Threat Model Compliance

All Wave A mitigate-disposition threats addressed:

| Threat ID | Disposition | Status | Verification |
|-----------|-------------|--------|--------------|
| T-08-01-CONTAINER-ROOT | mitigate | DONE | `grep -q "USER app" Dockerfile` confirms non-root uid 1654 |
| T-08-01-SECRET-LEAK-IMAGE | mitigate | DONE | `.dockerignore` excludes `tests/`, `.git`, `.env*`, `.planning/`, `*.md`, `compose.yaml`, `.vscode/` |
| T-08-01-TEST-DB-LEAK | mitigate | DONE | Phase8WebAppFactory.DisposeAsync calls `NpgsqlConnection.ClearPool(scoped-conn)` + `_fixture.DisposeAsync()` (which DROPs `stepsdb_test_<guid>` WITH FORCE) |

Accept-disposition threats (CS-LEAK-LOG, HOST-PORT-EXPOSURE, DOS-LARGE-BUILD-CONTEXT) require no Wave A action.

## User Setup Required

None — no external service configuration required for Wave A. compose.yaml uses existing `.env` tokens; dotnet-ef restores from existing CPM table.

## Next Phase Readiness

**Wave B (5 parallel entity plans 08-02 .. 08-06) can now begin.** Each Wave B plan takes `IClassFixture<Phase8WebAppFactory>` and calls `_factory.CreateClient()` for HTTP-level smoke + error-mapping facts.

**Wave C 08-07 (InitialCreate migration) unblocked.** `dotnet ef migrations add InitialCreate -p src/BaseApi.Service` will run via the local tool pinned to 8.0.27 against the Microsoft.EntityFrameworkCore.Design design-time package now PackageReferenced on BaseApi.Service.csproj.

**Wave C 08-08 (MigrationFailureWebAppFactory) unblocked.** The `protected Phase8WebAppFactory(string connectionStringOverride)` ctor accepts a deliberately bad connection string (e.g., port 5434 instead of 5433); subclass overrides ConnectionString surface so MigrateAsync throws at startup and D-16 behavior (failing readiness probe, process does not crash) can be asserted.

**Phase 1-7 regression preserved.** 98/98 facts GREEN across 2 confirmation runs (one Phase 5 OTel-warmup flake on first run, documented in Issues Encountered).

## Self-Check

Verification of claims before final commit:

**Created files:**
- `Dockerfile` — FOUND
- `.dockerignore` — FOUND
- `.config/dotnet-tools.json` — FOUND
- `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` — FOUND

**Modified files:**
- `compose.yaml` — Modified (phase-8 profile dropped; build:. + env + ports + healthcheck added)
- `src/BaseApi.Service/BaseApi.Service.csproj` — Modified (4 new PackageReferences, no Version= leak)
- `.planning/REQUIREMENTS.md` — Modified (TEST-03 + TEST-04 v2 deferral; 39 not 41)

**Task commits (verified via `git log --oneline -7`):**
- `aeb4fe5` Task 1 — FOUND
- `8960606` Task 2 — FOUND
- `fba0dac` Task 3 — FOUND
- `26994be` Task 4 — FOUND
- `7932326` Task 5 — FOUND

**Quality gates (full plan-level verify):**
- CPM contract preserved (zero Version= leak on 4 new PackageReferences) — PASSED
- `dotnet build SK_P.sln -c Release` exits 0 with 0 warnings — PASSED
- `dotnet build SK_P.sln -c Debug` exits 0 with 0 warnings — PASSED
- `docker compose config` exits 0 — PASSED
- `dotnet ef --version` reports `8.0.27` — PASSED
- Phase 1-7 regression: 98/98 GREEN (run 2 + run 3 both 18.1s, identical Passed count) — PASSED
- REQUIREMENTS.md: `39 requirements` for Phase 8, `| TEST-03 | v2 | Deferred |` present — PASSED

## Self-Check: PASSED

---
*Phase: 08-entity-build-out-migrations-docker-runtime-tests*
*Plan: 01*
*Completed: 2026-05-27*
