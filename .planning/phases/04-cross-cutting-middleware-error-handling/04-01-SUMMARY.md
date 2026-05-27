---
phase: 04-cross-cutting-middleware-error-handling
plan: 01
subsystem: api
tags: [aspnetcore8, middleware, problemdetails, iexceptionhandler, fluentvalidation, npgsql, postgres-sqlstate, correlation-id, rfc7807]

# Dependency graph
requires:
  - phase: 03-ef-core-persistence-base
    provides: BaseApi.Core class library, FrameworkReference Microsoft.AspNetCore.App, xmin shadow concurrency token (BaseDbContext.OnModelCreating)
  - phase: 01-repository-scaffold
    provides: CPM contract via Directory.Packages.props, public partial class Program marker, FluentValidation 12.1.1 pin
provides:
  - CorrelationIdMiddleware (sealed, D-03 4-step procedure, Pitfall 3 ASCII-printable input validation)
  - NotFoundException (sealed, ResourceType + Id properties per D-07)
  - PostgresExceptionMapper (static helper, SQLSTATE 23503/23505 → HTTP 422/409, Option A FK regex preserving _id suffix)
  - 4 IExceptionHandler classes (NotFound/Validation/DbUpdate/Fallback) in D-06 chain order
  - Program.cs composition root with AddHttpContextAccessor + AddProblemDetails customizer + 4 AddExceptionHandler + pipeline UseExceptionHandler → UseMiddleware<CorrelationIdMiddleware> → UseRouting → MapControllers
  - Explicit Npgsql 9.0.0 pin in Directory.Packages.props (fix-forward from RESEARCH A2's 8.0.10 assumption)
affects: [04-02 verification battery, 05-observability OTel wiring, 06-validation FluentValidation validators, 07-composition-root AddBaseApi/UseBaseApi extensions, 08-entities concrete controllers]

# Tech tracking
tech-stack:
  added:
    - Npgsql 9.0.0 (explicit pin for PostgresExceptionMapper's direct Npgsql.PostgresException consumption)
    - FluentValidation 12.1.1 PackageReference to BaseApi.Core (defensive landing per D-10; validators wired in Phase 6)
  patterns:
    - "IExceptionHandler chain in registration order (D-06) via 4× services.AddExceptionHandler<T>"
    - "CustomizeProblemDetails single-source-of-truth callback (D-04) for correlationId + instance extensions"
    - "AddProblemDetails BEFORE AddControllers (Pitfall 4/8) so default InvalidModelStateResponseFactory routes through IProblemDetailsService"
    - "Concurrency-FIRST ordering in DbUpdateExceptionHandler (Pitfall 7) — DbUpdateConcurrencyException check BEFORE PostgresExceptionMapper.TryMap"
    - "Response.OnStarting for header echo (Pitfall 5 / D-03 step 3) — fires deterministically before headers flush including on exception path"
    - "ASCII-printable correlation ID validation (Pitfall 3 / D-02) — rejects CR/LF/null/control chars to prevent CRLF injection"
    - "Information-disclosure guard pattern: log full exception via MEL, never leak Type/Message/Stack to response body (D-12, T-04-LEAK)"

key-files:
  created:
    - src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs
    - src/BaseApi.Core/Exceptions/NotFoundException.cs
    - src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs
    - src/BaseApi.Core/Exceptions/Handlers/NotFoundExceptionHandler.cs
    - src/BaseApi.Core/Exceptions/Handlers/ValidationExceptionHandler.cs
    - src/BaseApi.Core/Exceptions/Handlers/DbUpdateExceptionHandler.cs
    - src/BaseApi.Core/Exceptions/Handlers/FallbackExceptionHandler.cs
  modified:
    - Directory.Packages.props
    - src/BaseApi.Core/BaseApi.Core.csproj
    - src/BaseApi.Service/Program.cs

key-decisions:
  - "Option A FK regex (^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$) preserving _id suffix per RESEARCH lines 1059-1072 recommendation; deviation from D-08 verbatim explicitly authorized by Claude's Discretion at CONTEXT line 193"
  - "Npgsql pin = 9.0.0 (NOT 8.0.10 as RESEARCH A2 assumed) — Npgsql 8.0.10 does not exist on nuget.org and the Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10 transitive resolves to Npgsql 9.0.0; aligning the explicit pin avoids the NU1603 build error under TreatWarningsAsErrors=true"
  - "Phase 4 takes ownership of AddHttpContextAccessor (Phase 3 D-11 had deferred it to Phase 7) — needed by AddProblemDetails customizer (D-04) and future AuditInterceptor wiring; idempotent so Phase 7 won't conflict"

patterns-established:
  - "IExceptionHandler chain over UseExceptionHandler lambda: .NET 8 modern pattern; each handler single-responsibility; Pitfall 6 fast-bail discipline (return false IMMEDIATELY with no side effects if not the claimed type)"
  - "Static helper for SQLSTATE→HTTP mapping (PostgresExceptionMapper): pure, no DI, unit-testable in isolation; Option A regex preserves snake_case column name for direct ↔ camelCase DTO alignment"
  - "Phase 1 partial class Program marker preserved across all phase edits to maintain WebApplicationFactory<Program> contract for integration tests"

requirements-completed:
  - OBSERV-09
  - OBSERV-10
  - OBSERV-11
  - ERROR-01
  - ERROR-02
  - ERROR-03
  - ERROR-04
  - ERROR-05
  - ERROR-06
  - ERROR-07
  - ERROR-08
  - ERROR-09
  - ERROR-10
  - ERROR-11

# Metrics
duration: ~25min
completed: 2026-05-27
---

# Phase 04 Plan 01: Cross-Cutting Middleware + Error Handling (Build) Summary

**ASP.NET Core 8 IExceptionHandler chain (NotFound/Validation/DbUpdate/Fallback) + CorrelationIdMiddleware + PostgresExceptionMapper + RFC 7807 ProblemDetails wiring landed in BaseApi.Core + BaseApi.Service composition root; zero-warning Release+Debug builds green under TreatWarningsAsErrors=true.**

## Performance

- **Duration:** ~25 min
- **Started:** 2026-05-27T05:44Z (estimated)
- **Completed:** 2026-05-27T06:09Z
- **Tasks:** 8 (7 atomic feat/fix commits + 1 verification-only)
- **Files modified:** 10 (1 props edit, 1 csproj edit, 7 new C# files, 1 Program.cs edit)

## Accomplishments

- 7 new C# files in BaseApi.Core covering the full Phase 4 production scope (1 middleware + 1 exception type + 1 static helper + 4 IExceptionHandler classes)
- Program.cs composition root wired with the D-01/D-04/D-06/D-11 invariants in exact source order (AddProblemDetails BEFORE AddControllers; chain order NotFound→Validation→DbUpdate→Fallback; pipeline UseExceptionHandler→UseMiddleware<CorrelationIdMiddleware>→UseRouting→MapControllers)
- CPM contract preserved: zero `Version=` attributes on `<PackageReference>` across BaseApi.Core.csproj (6 PackageReferences total — 4 existing EF + 1 new Npgsql + 1 new FluentValidation)
- Pitfall 7 ordering verified by grep (concurrency check at line 49 BEFORE PostgresExceptionMapper.TryMap at line 67 in DbUpdateExceptionHandler.cs)
- FallbackExceptionHandler verified leak-free (grep for `exception.Message|exception.ToString|exception.StackTrace|exception.GetType` returns 0 matches)
- Phase 1 `public partial class Program { }` marker preserved verbatim (load-bearing for Plan 04-02 WebApplicationFactory<Program>)
- All 14 Phase 4 requirement IDs (OBSERV-09/10/11 + ERROR-01..11) have their production-side implementation landed; verification is Plan 04-02 territory

## Task Commits

Each task was committed atomically (Task 8 is verification-only, no commit):

1. **Task 1: Add Npgsql pin to Directory.Packages.props** - `c10a248` (feat) — added explicit `<PackageVersion Include="Npgsql" Version="8.0.10" />` (later corrected by `6393595`)
2. **Task 1 fix-forward: align Npgsql pin to transitive** - `6393595` (fix) — corrected 8.0.10 → 9.0.0 after NU1603 surfaced; RESEARCH A2 explicitly anticipated this fix-forward path
3. **Task 2: Add Npgsql + FluentValidation PackageReferences** - `5264fa5` (feat) — 2 new `<PackageReference>` entries in BaseApi.Core.csproj; zero `Version=` attributes
4. **Task 3: Create CorrelationIdMiddleware** - `19b6069` (feat) — sealed middleware with D-03 4-step procedure + Pitfall 3 ASCII-printable IsValid guard
5. **Task 4: Create NotFoundException** - `2f7b605` (feat) — sealed type with ResourceType + Id properties per D-07
6. **Task 5: Create PostgresExceptionMapper** - `ecf75fb` (feat) — static helper with Option A regex (deviation from D-08 verbatim, authorized by Claude's Discretion)
7. **Task 6: Create 4 IExceptionHandler classes** - `574fc24` (feat) — all 4 handlers in single commit (single conceptual unit: D-06 chain); Pitfall 7 ordering enforced
8. **Task 7: Edit Program.cs** - `010510e` (feat) — DI + pipeline wiring with all load-bearing invariants

**Plan metadata commit:** _Pending — SUMMARY.md + STATE.md + ROADMAP.md committed as final step._

## Files Created/Modified

### Created (7 files)

- `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` — sealed middleware reading/generating correlation ID, stashing in HttpContext.Items["CorrelationId"], echoing on response header via OnStarting, pushing onto MEL log scope. Pitfall 3 IsValid guard rejects CR/LF/non-printable.
- `src/BaseApi.Core/Exceptions/NotFoundException.cs` — sealed exception type with public ResourceType (string) + Id (object) properties; base message `"{ResourceType} with id '{Id}' was not found."`
- `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs` — static helper `TryMap(DbUpdateException, out int, out string, out string?)`; SQLSTATE 23503→422, 23505→409; Option A FK regex preserves `_id` suffix
- `src/BaseApi.Core/Exceptions/Handlers/NotFoundExceptionHandler.cs` — claims `NotFoundException` → 404; Extensions[resourceType]+[resourceId]
- `src/BaseApi.Core/Exceptions/Handlers/ValidationExceptionHandler.cs` — claims `FluentValidation.ValidationException` → 400 ValidationProblemDetails; errors map via `Errors.GroupBy(PropertyName).ToDictionary(g => g.Key, g => g.Select(e.ErrorMessage).ToArray())`
- `src/BaseApi.Core/Exceptions/Handlers/DbUpdateExceptionHandler.cs` — claims `DbUpdateException` + subtypes; concurrency check FIRST (Pitfall 7) with D-09 generic message (no xmin leak) → 409; THEN PostgresExceptionMapper.TryMap; returns false on unmapped SQLSTATE so Fallback claims 500
- `src/BaseApi.Core/Exceptions/Handlers/FallbackExceptionHandler.cs` — catch-all; `_logger.LogError(exception, "Unhandled exception on {Path}", path)` via MEL; body has only Title/Status/generic Detail — exception type/message/stack NEVER leak (T-04-LEAK)

### Modified (3 files)

- `Directory.Packages.props` — added `<PackageVersion Include="Npgsql" Version="9.0.0" />` (25 pins total, was 24); FluentValidation 12.1.1 pin preserved (Phase 1 D-05)
- `src/BaseApi.Core/BaseApi.Core.csproj` — added `<PackageReference Include="Npgsql" />` to existing EF ItemGroup + new `<ItemGroup>` for `<PackageReference Include="FluentValidation" />`; zero Version= attributes
- `src/BaseApi.Service/Program.cs` — expanded from 27 lines (Phase 1 scaffold) to 72 lines with full D-01/D-04/D-06/D-11 wiring; header comment block + `public partial class Program { }` marker preserved verbatim

## must_have GREEN/RED Verification

| must_have (from PLAN.md frontmatter `truths:`) | Verified by | Status |
|------------------------------------------------|-------------|--------|
| Directory.Packages.props pins Npgsql (NEW line) AND FluentValidation 12.1.1 (Phase 1 unchanged) | grep `PackageVersion Include="Npgsql"` returns 1 match (line 52); FluentValidation 12.1.1 line 58 untouched; total `<PackageVersion ` count = 25 | GREEN (note: pin is 9.0.0 not 8.0.10 — see Deviations) |
| BaseApi.Core.csproj declares 2 NEW PackageReferences (Npgsql + FluentValidation), zero Version= | grep shows 6 `PackageReference Include=` entries; grep for `Version=` on PackageReference returns 0 matches | GREEN |
| CorrelationIdMiddleware: sealed, file-scoped ns, D-03 4-step procedure, ASCII-printable IsValid | grep returns 12 matches for the D-02/D-03/Pitfall-3 signature patterns (Response.OnStarting, BeginScope, Guid.NewGuid().ToString("N"), private static bool IsValid, 0x20, 0x7E) | GREEN |
| NotFoundException: sealed, ResourceType + Id, formatted base message | grep `public sealed class NotFoundException : Exception` + `public string ResourceType` + `public object Id` + `was not found` | GREEN |
| PostgresExceptionMapper: static class, TryMap signature, FK regex Option A, UQ regex, SQLSTATE 23503/23505 | grep `public static class PostgresExceptionMapper` + `public static bool TryMap` + `^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$` + `^uq_...$` + `"23503"`/`"23505"` + `Status422UnprocessableEntity`/`Status409Conflict` | GREEN |
| 4 IExceptionHandler classes (NotFound/Validation/DbUpdate/Fallback) with correct status codes + extensions + ordering | All 4 files exist with `IExceptionHandler` + `TryHandleAsync` + correct exception type checks; Pitfall 7 verified by line-number grep (concurrency at line 49 BEFORE TryMap at line 67) | GREEN |
| Program.cs retains Phase 1 header + partial class marker; ADDS AddHttpContextAccessor + AddProblemDetails (BEFORE AddControllers) + 4 AddExceptionHandler (D-06 order); pipeline UseExceptionHandler→UseMiddleware<CorrelationIdMiddleware>→UseRouting→MapControllers | Line-number grep: AddProblemDetails (L30) < AddControllers (L51); AddExceptionHandler order NotFound(L46)<Validation(L47)<DbUpdate(L48)<Fallback(L49); pipeline UseExceptionHandler(L62)<UseMiddleware<CorrIdMw>(L63)<UseRouting(L64)<MapControllers(L65); partial class at L72 | GREEN |
| `dotnet restore` exits 0 AND `dotnet list package --include-transitive` reports Npgsql resolved version | restore exit 0; list shows Npgsql 9.0.0 resolved (no NU1605 downgrade warning) | GREEN (RESEARCH A2 corrected — see Deviations) |
| `dotnet build -c Release --no-restore` exits 0 with 0 Warning(s) 0 Error(s); `dotnet build -c Debug --no-restore` likewise | Release build output: `0 Warning(s)` `0 Error(s)`; Debug build output: `0 Warning(s)` `0 Error(s)` | GREEN |

All 9 must_haves GREEN. None RED.

## Decisions Made

1. **Option A FK regex (preserves `_id` suffix).** RESEARCH.md lines 1059-1072 recommended Option A over D-08's verbatim Option B for DTO-field ↔ snake_case-column alignment. CONTEXT.md line 193 (D-08 Claude's Discretion) explicitly authorizes regex adjustment. The chosen regex: `^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$`. Result: `fk_processor_input_schema_id` captures `col = "input_schema_id"`, which directly maps to camelCase `inputSchemaId` DTO field per the Phase 8 entity surface.

2. **Phase 4 takes ownership of AddHttpContextAccessor.** Phase 3 D-11 deferred this to "Phase 7 composition root", but Pitfall 1 (RESEARCH lines 452-460) recommends Phase 4 take it early because the AddProblemDetails customizer needs HttpContext access and future AuditInterceptor wiring in Phase 7/8 will resolve IHttpContextAccessor. `AddHttpContextAccessor()` is idempotent — Phase 7 will not conflict.

3. **All 4 IExceptionHandler classes committed in a single feat commit.** While the plan task notes split each handler conceptually, they form a single chain unit (D-06 ordering is load-bearing across all four). Single commit makes the chain shape easier to review as one diff.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Corrected Npgsql pin from 8.0.10 to 9.0.0**

- **Found during:** Task 3 build verification (between Tasks 2 and 3 — first time the new pin was exercised by `dotnet build`)
- **Issue:** RESEARCH assumption A2 expected Npgsql 8.0.10 transitive via Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10. Actual transitive resolution: Npgsql 9.0.0. The explicit pin to Npgsql 8.0.10 triggered `error NU1603: Warning As Error: BaseApi.Core depends on Npgsql (>= 8.0.10) but Npgsql 8.0.10 was not found. An approximate best match of Npgsql 9.0.0 was resolved.` Build failed under `TreatWarningsAsErrors=true`. Npgsql 8.0.10 simply does not exist on nuget.org — Npgsql's release cadence went directly from 8.0.x patches < 8.0.10 to the 9.x line.
- **Fix:** Edited `Directory.Packages.props` to pin Npgsql 9.0.0 (matches the actual transitive that Npgsql.EFCore.PostgreSQL 8.0.10 brings).
- **Files modified:** `Directory.Packages.props` (line 52)
- **Verification:** `dotnet restore --force --no-cache` clean; `dotnet list package --include-transitive` reports Npgsql 9.0.0 resolved (no NU1605 downgrade); Release + Debug builds both report 0 Warning(s) 0 Error(s).
- **Committed in:** `6393595` (`fix(04-01): correct Npgsql pin from 8.0.10 to 9.0.0 (transitive match)`)
- **Anticipated:** RESEARCH.md A2 explicitly listed this exact contingency: _"If the transitive dependency is on Npgsql 8.0.x but with different patch (e.g., 8.0.5), the explicit pin to 8.0.10 might cause a version downgrade warning OR resolve to a newer version. Plan 04-01 verification step: `dotnet list package --include-transitive` after first restore. Trivial to fix."_ The fix-forward path was pre-authorized.

**2. [Documented — D-08 Claude's Discretion] FK regex Option A (preserves `_id` suffix) instead of D-08 verbatim Option B**

- **Found during:** Task 5 (PostgresExceptionMapper creation)
- **Issue:** D-08's verbatim regex strips `_id` from the captured column name, making the response detail message inconsistent with the actual snake_case column name (and by extension, the camelCase DTO field name after snake-case→camel-case translation).
- **Fix:** Used Option A regex `^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$` per RESEARCH.md lines 1059-1072 RECOMMENDATION.
- **Files modified:** `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs` (line 49 — FkRegex)
- **Verification:** Build green; documented in the file's XML doc under `<b>FK regex — Option A...</b>` paragraph; Plan 04-02 unit tests will verify the regex against the 5 Phase 8 constraint names enumerated in RESEARCH lines 1080-1086.
- **Committed in:** `ecf75fb` (`feat(04-01): add PostgresExceptionMapper (D-08, Option A regex)`)
- **Authorization:** CONTEXT.md line 193 (D-08 Claude's Discretion): _"The recommended patterns above are a starting point. Researcher should validate against the EXACT Phase 8 constraint names locked in REQUIREMENTS.md PERSIST and ENTITY sections; adjust the regex if Phase 8's conventions differ."_ This was a planned deviation, not an unplanned auto-fix — but documented here for traceability.

---

**Total deviations:** 1 auto-fixed (Rule 3 blocking) + 1 documented design choice (D-08 discretion). Both anticipated by RESEARCH.md.
**Impact on plan:** Zero scope creep. The Npgsql pin correction is a 1-character version bump; the FK regex choice was explicitly authorized.

## Issues Encountered

- **Pre-existing untracked file `src/BaseApi.Service/Properties/launchSettings.json`** — appeared during the session as a Visual Studio-generated dev-launch profile with developer-specific ports (51538/51539). NOT part of Plan 04-01 scope. Left alone per scope-boundary rule. Flagged for future plan/phase consideration (likely should be added to `.gitignore` as part of Phase 7 composition-root work, since launchSettings.json is conventionally per-developer).

## Threat Flags

No new security surface introduced beyond what Plan 04-01 was scoped to add. The plan's `<threat_model>` covered all 5 STRIDE threats (T-04-LEAK, T-04-XMIN, T-04-INJECT, T-04-FK, T-04-UQ) and each has its mitigation landed in code:

- **T-04-LEAK (FallbackExceptionHandler):** grep confirms no `exception.Message|exception.ToString|exception.StackTrace|exception.GetType` reads in `FallbackExceptionHandler.cs`. Body uses literal `"An unexpected error occurred."`; full stack logged via `_logger.LogError(exception, ...)`.
- **T-04-XMIN (DbUpdateExceptionHandler concurrency branch):** detail string is verbatim `"The resource was modified by another request; reload and retry."` with no `xmin` substring; Pitfall 7 ordering enforced (concurrency check line 49 BEFORE TryMap line 67).
- **T-04-INJECT (CorrelationIdMiddleware.IsValid):** ASCII-printable guard rejects bytes < 0x20 or > 0x7E (CR/LF/null/control); invalid input triggers fresh Guid.NewGuid, never falls back to unsafe value.
- **T-04-FK / T-04-UQ (PostgresExceptionMapper):** only regex-extracted column name reaches response; `pgEx.MessageText`, `.Detail`, `.TableName`, `.SchemaName` never appear in response body (verified by code review — no field access beyond `SqlState` + `ConstraintName`).

All mitigations are behavioral (require runtime test to fully verify) — Plan 04-02 verification battery owns the behavioral assertions.

## User Setup Required

None — no external service configuration required. All Phase 4 wiring is in-process.

## Next Phase Readiness

- **Plan 04-02 (verification battery)** is unblocked. The production shapes for all 14 REQ-IDs exist; Plan 04-02 adds `WebApplicationFactory<Program>` wrapper + per-class throwaway DB `PostgresFixture` (lifted from Phase 3 verbatim) + 1 test `TestController.cs` + 6 fact tests + 1 unit test for PostgresExceptionMapper.
- **Plan 04-02 commit prefix:** `docs(04-02): ...` for evidence/SUMMARY commits; `fix(04-01): ...` for any source defect surfaced by verification (matches Phase 1/2/3 D-18 convention).
- **Phase 5 (Observability + Health Probes) inherits cleanly:** `CorrelationIdMiddleware`'s `BeginScope` uses the literal `"CorrelationId"` key (PascalCase) which OTel's `IncludeScopes=true` will serialize without renaming. Phase 5 will add `Activity.Current?.AddTag("correlation.id", corrId)` additively inside the same middleware.
- **Phase 6 (Validation) inherits cleanly:** `FluentValidation` PackageReference is wired; `ValidationExceptionHandler` is the live mapper. Phase 6 just needs to call `services.AddValidatorsFromAssembly(...)` and per-entity validators.

## Self-Check: PASSED

Created files verified to exist on disk:
- `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` — FOUND
- `src/BaseApi.Core/Exceptions/NotFoundException.cs` — FOUND
- `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs` — FOUND
- `src/BaseApi.Core/Exceptions/Handlers/NotFoundExceptionHandler.cs` — FOUND
- `src/BaseApi.Core/Exceptions/Handlers/ValidationExceptionHandler.cs` — FOUND
- `src/BaseApi.Core/Exceptions/Handlers/DbUpdateExceptionHandler.cs` — FOUND
- `src/BaseApi.Core/Exceptions/Handlers/FallbackExceptionHandler.cs` — FOUND

Commits verified in `git log --oneline`:
- `c10a248` (Task 1 initial pin) — FOUND
- `6393595` (Task 1 fix-forward) — FOUND
- `5264fa5` (Task 2 csproj refs) — FOUND
- `19b6069` (Task 3 middleware) — FOUND
- `2f7b605` (Task 4 NotFoundException) — FOUND
- `ecf75fb` (Task 5 PostgresExceptionMapper) — FOUND
- `574fc24` (Task 6 4 handlers) — FOUND
- `010510e` (Task 7 Program.cs) — FOUND

Build verification:
- `dotnet build --configuration Release --no-restore` → `0 Warning(s)` `0 Error(s)` PASSED
- `dotnet build --configuration Debug --no-restore` → `0 Warning(s)` `0 Error(s)` PASSED

---
*Phase: 04-cross-cutting-middleware-error-handling*
*Plan: 01*
*Completed: 2026-05-27*
