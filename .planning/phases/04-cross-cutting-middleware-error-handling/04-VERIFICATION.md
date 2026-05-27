---
phase: 04-cross-cutting-middleware-error-handling
verified: 2026-05-27T00:00:00Z
status: passed
score: 5/5 must-haves verified
overrides_applied: 0
---

# Phase 4: Cross-Cutting Middleware + Error Handling Verification Report

**Phase Goal:** Wire correlation-ID middleware, the global IExceptionHandler, and Postgres SQLSTATE -> HTTP mapping so any HTTP error path produced by later phases already returns RFC 7807 with a correlation ID.
**Verified:** 2026-05-27
**Status:** passed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | A request without X-Correlation-Id receives a generated 32-char hex in the response header on 2xx, 4xx, and 5xx paths; a valid supplied header is echoed verbatim | ✓ VERIFIED | `CorrelationIdTests.cs` 9 facts GREEN — 3x missing-header on 2xx/4xx/5xx + 1x valid-header echo + 5x invalid-header rejection; `dotnet test` Passed: 31 |
| 2 | FluentValidation.ValidationException produces HTTP 400 with Content-Type application/problem+json, field-level errors map, and correlationId matching response header | ✓ VERIFIED | `ValidationErrorTests.Test_FluentValidation_Exception_Produces_400_WithErrorsMap_AndCorrelationId` GREEN; `ValidationExceptionHandler.cs` confirmed: groups errors by PropertyName, writes ValidationProblemDetails via IProblemDetailsService |
| 3 | EF SaveChanges FK violation (SQLSTATE 23503) surfaces as HTTP 422 with FK field name in detail; unique violation (23505) as HTTP 409 with field name; both include correlationId and instance | ✓ VERIFIED | `SqlStateMappingTests` 2 facts GREEN; `PostgresExceptionMapperTests` 4 facts GREEN; `PostgresExceptionMapper.cs` confirmed: FkRegex `^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$` → 422, UqRegex → 409 |
| 4 | NotFoundException produces HTTP 404 with resource type + id in detail; unhandled exception produces HTTP 500 with generic message and no stack trace in body (stack logged only) | ✓ VERIFIED | `NotFoundAndUnhandledTests` 2 facts GREEN; T-04-LEAK: `Assert.DoesNotContain` on 5 stack-frame substrings; `FallbackExceptionHandler.cs` confirmed: body uses only Title/Status/Detail literals |
| 5 | `[ApiController]` model-binding failures produce the same Problem Details shape as FluentValidation failures, including correlationId | ✓ VERIFIED | `ValidationErrorTests.Test_ModelBinding_400_ProducesSameShape_AsFluentValidation400` GREEN; D-11 confirmed in `Program.cs`: `AddProblemDetails` registered before `AddControllers` (lines 30, 51) |

**Score:** 5/5 truths verified

---

### ROADMAP Success Criteria Coverage

| SC | Description | Status | Test Evidence |
|----|-------------|--------|---------------|
| SC#1 | Correlation echo 2xx/4xx/5xx + verbatim echo + malformed rejection (OBSERV-09/10/11) | GREEN | CorrelationIdTests 9 facts |
| SC#2 | FluentValidation 400 + errors map + correlationId (ERROR-03) | GREEN | ValidationErrorTests fact 1 |
| SC#3 | SQLSTATE 23503→422 + 23505→409 + column in detail (ERROR-04/05/11) | GREEN | SqlStateMappingTests 2 facts + PostgresExceptionMapperTests 4 facts |
| SC#4 | NotFound 404 + unhandled 500 NO stack in body (ERROR-01/02/06/07 + T-04-LEAK) | GREEN | NotFoundAndUnhandledTests 2 facts |
| SC#5 | Model-binding 400 shape parity (ERROR-10) | GREEN | ValidationErrorTests fact 2 |

All 5 ROADMAP success criteria are GREEN.

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` | Sealed middleware with D-03 4-step procedure | ✓ VERIFIED | File exists, sealed class, `Response.OnStarting`, `BeginScope`, `Guid.NewGuid().ToString("N")`, `IsValid` with 0x20/0x7E guard |
| `src/BaseApi.Core/Exceptions/NotFoundException.cs` | Sealed exception with ResourceType + Id (D-07) | ✓ VERIFIED | File exists, `public sealed class NotFoundException : Exception`, `ResourceType`, `Id`, base message with "was not found" |
| `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs` | Static helper TryMap with SQLSTATE switch (D-08, Option A regex) | ✓ VERIFIED | File exists, `public static class PostgresExceptionMapper`, `TryMap(DbUpdateException, out int, out string, out string?)`, SQLSTATE 23503/23505, Option A regexes |
| `src/BaseApi.Core/Exceptions/Handlers/NotFoundExceptionHandler.cs` | IExceptionHandler #1 — NotFoundException → 404 + Extensions (D-06 #1) | ✓ VERIFIED | File exists, claims NotFoundException, sets 404, Extensions["resourceType"] + ["resourceId"] |
| `src/BaseApi.Core/Exceptions/Handlers/ValidationExceptionHandler.cs` | IExceptionHandler #2 — ValidationException → 400 ValidationProblemDetails (D-06 #2) | ✓ VERIFIED | File exists, claims FluentValidation.ValidationException, sets 400, builds errors map |
| `src/BaseApi.Core/Exceptions/Handlers/DbUpdateExceptionHandler.cs` | IExceptionHandler #3 — concurrency-first then SQLSTATE (D-06 #3, Pitfall 7) | ✓ VERIFIED | File exists, `DbUpdateConcurrencyException` check at line 49 BEFORE `PostgresExceptionMapper.TryMap` at line 67; D-09 generic message confirmed |
| `src/BaseApi.Core/Exceptions/Handlers/FallbackExceptionHandler.cs` | IExceptionHandler #4 — 500 generic body, LogError (D-06 #4, D-12) | ✓ VERIFIED | File exists, `_logger.LogError(exception, ...)`, body uses only Title/Status/Detail literals — no exception.Message/Type/Stack |
| `src/BaseApi.Service/Program.cs` | Composition root with full D-01/D-04/D-06/D-11 wiring | ✓ VERIFIED | `AddHttpContextAccessor` (line 23), `AddProblemDetails` with customizer (line 30) BEFORE `AddControllers` (line 51), 4x `AddExceptionHandler` in D-06 order (lines 46-49), pipeline `UseExceptionHandler` → `UseMiddleware<CorrelationIdMiddleware>` → `UseRouting` → `MapControllers` (lines 62-65), `public partial class Program { }` at line 72 |
| `Directory.Packages.props` | Npgsql pin (8.0.9 after two fix-forwards) + FluentValidation 12.1.1 preserved | ✓ VERIFIED | `<PackageVersion Include="Npgsql" Version="8.0.9" />` present; FluentValidation 12.1.1 pin preserved |
| `src/BaseApi.Core/BaseApi.Core.csproj` | Npgsql + FluentValidation PackageReferences, no Version= attributes | ✓ VERIFIED | `<PackageReference Include="Npgsql" />` and `<PackageReference Include="FluentValidation" />` present; zero Version= attributes |
| `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` | WebApplicationFactory<Program> wrapper with assembly part | ✓ VERIFIED | File exists, `WebApplicationFactory<Program>`, `ConfigureTestServices`, `AddApplicationPart` |
| `tests/BaseApi.Tests/Middleware/PostgresFixture.cs` | Verbatim Phase 3 lift, namespace BaseApi.Tests.Middleware, D-15 cleanup | ✓ VERIFIED | File exists, namespace correct, `IAsyncLifetime`, `ClearAllPools`, `DROP DATABASE IF EXISTS ... WITH (FORCE)` |
| `tests/BaseApi.Tests/Endpoints/TestController.cs` | 8 deliberately-throwing endpoints | ✓ VERIFIED | File exists, `[ApiController][Route("test")]`, all 8 endpoints present |
| `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` | SC#1 + T-04-INJECT (OBSERV-09/10/11) | ✓ VERIFIED | File exists, 9 facts, X-Correlation-Id assertions |
| `tests/BaseApi.Tests/Middleware/ValidationErrorTests.cs` | SC#2 + SC#5 (ERROR-03 + ERROR-10) | ✓ VERIFIED | File exists, ValidationProblemDetails assertions |
| `tests/BaseApi.Tests/Middleware/SqlStateMappingTests.cs` | SC#3 (ERROR-04/05/11) | ✓ VERIFIED | File exists, fk_ constraint tests |
| `tests/BaseApi.Tests/Middleware/NotFoundAndUnhandledTests.cs` | SC#4 (ERROR-01/02/06/07 + T-04-LEAK) | ✓ VERIFIED | File exists, NotFoundException + stack-leak guard |
| `tests/BaseApi.Tests/Middleware/ConcurrencyTokenTests.cs` | D-03a carry-forward (T-04-XMIN) | ✓ VERIFIED | File exists, DbUpdateConcurrencyException assertion |
| `tests/BaseApi.Tests/Middleware/ProblemDetailsExtensionsTests.cs` | ERROR-08/09 regression guard | ✓ VERIFIED | File exists, correlationId + instance assertions |
| `tests/BaseApi.Tests/Persistence/PostgresExceptionMapperTests.cs` | ERROR-11 unit coverage (Option A regex) | ✓ VERIFIED | File exists, InlineData theory tests |
| `.planning/phases/04-cross-cutting-middleware-error-handling/04-02-SUMMARY.md` | GREEN/RED grid + test evidence | ✓ VERIFIED | File exists, 31/31 GREEN grid documented |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `CorrelationIdMiddleware` | `HttpContext.Items["CorrelationId"]` | `context.Items[ItemKey] = corrId;` (D-03 step 2) | ✓ WIRED | Line 67 in CorrelationIdMiddleware.cs confirmed |
| `Program.cs AddProblemDetails CustomizeProblemDetails` | `HttpContext.Items["CorrelationId"]` | `ctx.HttpContext.Items.TryGetValue("CorrelationId", out var corrIdObj)` (D-04) | ✓ WIRED | Lines 34-38 in Program.cs confirmed |
| `DbUpdateExceptionHandler` | `PostgresExceptionMapper.TryMap` | After `DbUpdateConcurrencyException` early-return (Pitfall 7) | ✓ WIRED | Line 67 in DbUpdateExceptionHandler.cs: `PostgresExceptionMapper.TryMap(due, ...)` |
| `Program.cs pipeline` | `UseExceptionHandler` THEN `UseMiddleware<CorrelationIdMiddleware>` (D-01) | `app.UseExceptionHandler(); app.UseMiddleware<CorrelationIdMiddleware>();` | ✓ WIRED | Lines 62-63 in Program.cs; order confirmed |
| `Program.cs DI` | `AddProblemDetails` BEFORE `AddControllers` (Pitfall 4/8) | Registration order | ✓ WIRED | AddProblemDetails line 30, AddControllers line 51; order confirmed |
| `WebAppFactory` | `public partial class Program { }` marker | `WebApplicationFactory<Program>` via ProjectReference to BaseApi.Service | ✓ WIRED | Marker at line 72 of Program.cs; ProjectReference confirmed in BaseApi.Tests.csproj |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `CorrelationIdMiddleware` | `corrId` | `ResolveCorrelationId(context)` → `TryGetValue` + `IsValid` → `Guid.NewGuid().ToString("N")` | Yes — either valid inbound header or fresh Guid | ✓ FLOWING |
| `Program.cs CustomizeProblemDetails` | `corrId` from `HttpContext.Items` | Set by `CorrelationIdMiddleware` earlier in request | Yes — reads live Items dict populated by middleware | ✓ FLOWING |
| `NotFoundExceptionHandler` | `nfx.ResourceType`, `nfx.Id` | `NotFoundException` constructor args passed by service caller | Yes — actual exception data | ✓ FLOWING |
| `DbUpdateExceptionHandler` → `PostgresExceptionMapper` | `pgEx.SqlState`, `pgEx.ConstraintName` | Npgsql `PostgresException` from real Postgres connection | Yes — live DB error | ✓ FLOWING |
| `FallbackExceptionHandler` | Generic body literals only | No dynamic data — `"An unexpected error occurred."` is hardcoded (D-12 information-disclosure guard) | N/A — intentionally static | ✓ VERIFIED (by design) |

---

### Behavioral Spot-Checks

Behavioral verification was completed by `dotnet test` as part of Plan 04-02 (autonomous: false).

| Behavior | Method | Result | Status |
|----------|--------|--------|--------|
| 31 xUnit v3 facts pass against real Postgres 17 + in-process TestServer | `dotnet test` | `Passed: 31, Failed: 0, Skipped: 0` across 3 consecutive runs (1.5-2.9s each) | ✓ PASS |
| Release + Debug builds zero warnings | `dotnet build -c Release/Debug` | `0 Warning(s) 0 Error(s)` | ✓ PASS |
| D-15 cleanup — no leaked test DBs | `psql \l` BEFORE/AFTER diff | Empty diff — byte-identical snapshots | ✓ PASS |

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| OBSERV-09 | 04-01 + 04-02 | CorrelationIdMiddleware reads/generates X-Correlation-Id | ✓ SATISFIED | CorrelationIdMiddleware.cs confirmed; CorrelationIdTests GREEN |
| OBSERV-10 | 04-01 + 04-02 | Correlation ID written to HttpContext.Items + log scope | ✓ SATISFIED | Items["CorrelationId"] at middleware line 67; BeginScope at line 78 |
| OBSERV-11 | 04-01 + 04-02 | Correlation ID echoed in X-Correlation-Id response header | ✓ SATISFIED | Response.OnStarting at lines 71-75; CorrelationIdTests SC#1 GREEN |
| ERROR-01 | 04-01 + 04-02 | RFC 7807 ProblemDetails JSON on every non-2xx response | ✓ SATISFIED | AddProblemDetails + 4 IExceptionHandler chain; ProblemDetailsExtensionsTests GREEN |
| ERROR-02 | 04-01 + 04-02 | IExceptionHandler via AddExceptionHandler + AddProblemDetails (.NET 8) | ✓ SATISFIED | Program.cs lines 46-49: 4x AddExceptionHandler; line 30: AddProblemDetails |
| ERROR-03 | 04-01 + 04-02 | ValidationException → HTTP 400 with field-level errors map | ✓ SATISFIED | ValidationExceptionHandler.cs + ValidationErrorTests SC#2 GREEN |
| ERROR-04 | 04-01 + 04-02 | SQLSTATE 23503 (FK) → HTTP 422 with FK field name | ✓ SATISFIED | PostgresExceptionMapper case "23503" + SqlStateMappingTests GREEN |
| ERROR-05 | 04-01 + 04-02 | SQLSTATE 23505 (UQ) → HTTP 409 with field name | ✓ SATISFIED | PostgresExceptionMapper case "23505" + SqlStateMappingTests GREEN |
| ERROR-06 | 04-01 + 04-02 | NotFoundException → HTTP 404 with resourceType + id | ✓ SATISFIED | NotFoundExceptionHandler.cs + NotFoundAndUnhandledTests SC#4 GREEN |
| ERROR-07 | 04-01 + 04-02 | Unhandled exception → HTTP 500 generic; stack logged only | ✓ SATISFIED | FallbackExceptionHandler.cs (no stack in body) + T-04-LEAK GREEN |
| ERROR-08 | 04-01 + 04-02 | Every ProblemDetails includes correlationId field | ✓ SATISFIED | CustomizeProblemDetails callback in Program.cs; ProblemDetailsExtensionsTests GREEN |
| ERROR-09 | 04-01 + 04-02 | Every ProblemDetails includes instance field (request path) | ✓ SATISFIED | `ctx.ProblemDetails.Instance = ctx.HttpContext.Request.Path;` in Program.cs line 39; ProblemDetailsExtensionsTests GREEN |
| ERROR-10 | 04-01 + 04-02 | [ApiController] model-binding 400 same shape as FluentValidation 400 | ✓ SATISFIED | AddProblemDetails BEFORE AddControllers (D-11); ValidationErrorTests SC#5 GREEN |
| ERROR-11 | 04-01 + 04-02 | Postgres constraint names follow convention (fk_/uq_ prefixes) for field extraction | ✓ SATISFIED | PostgresExceptionMapper Option A regex; PostgresExceptionMapperTests 4 facts GREEN |

All 14 Phase 4 REQ-IDs satisfied.

**Note on REQUIREMENTS.md traceability table:** The per-requirement checkboxes in REQUIREMENTS.md show `[x]` for all 14 IDs (OBSERV-09/10/11, ERROR-01..11), correctly reflecting completion. The traceability table at the bottom of that file still shows "Pending" for these 14 entries — this is a documentation inconsistency (the table was not updated when the checkboxes were). The checkboxes are the authoritative completion signal; the table is stale. No functional gap.

---

### Locked Context Decisions (D-01 through D-14)

| Decision | Requirement | Code Evidence | Status |
|----------|-------------|---------------|--------|
| D-01: Pipeline order UseExceptionHandler → UseMiddleware<CorrelationIdMiddleware> → UseRouting → MapControllers | Program.cs pipeline | Lines 62-65 in Program.cs in exact order | ✓ HONORED |
| D-02: Correlation format Guid.NewGuid().ToString("N") (32-char hex) | CorrelationIdMiddleware.cs | Line 92: `Guid.NewGuid().ToString("N")` | ✓ HONORED |
| D-03: 4-step procedure (resolve, stash Items, OnStarting echo, BeginScope wrap) | CorrelationIdMiddleware.cs | Steps 1-4 verified at lines 64, 67, 71-75, 78-82 | ✓ HONORED |
| D-04: CustomizeProblemDetails injects correlationId + instance | Program.cs | Lines 32-41: reads Items["CorrelationId"], sets Instance | ✓ HONORED |
| D-05: ProblemDetails JSON shape | All handlers + customizer | Extensions["correlationId"] + Instance set; no per-handler boilerplate | ✓ HONORED |
| D-06: Handler registration order NotFound → Validation → DbUpdate → Fallback | Program.cs | Lines 46-49 in order | ✓ HONORED |
| D-07: NotFoundException shape (ResourceType, Id, base message format) | NotFoundException.cs | `public string ResourceType { get; }`, `public object Id { get; }`, base message "was not found" | ✓ HONORED |
| D-08: PostgresExceptionMapper TryMap signature + Option A regex (Claude's Discretion) | PostgresExceptionMapper.cs | `public static bool TryMap(DbUpdateException, out int, out string, out string?)`, Option A FK regex preserving _id suffix | ✓ HONORED |
| D-09: DbUpdateConcurrencyException → 409 with generic message, no xmin leak | DbUpdateExceptionHandler.cs | Concurrency check at line 49; detail = "The resource was modified by another request; reload and retry." | ✓ HONORED |
| D-10: FluentValidation PackageReference defensive landing | BaseApi.Core.csproj | `<PackageReference Include="FluentValidation" />` present | ✓ HONORED |
| D-11: AddProblemDetails BEFORE AddControllers; default InvalidModelStateResponseFactory rides through same IProblemDetailsService | Program.cs | Line 30 (AddProblemDetails) before line 51 (AddControllers) | ✓ HONORED |
| D-12: FallbackExceptionHandler logs full exception via MEL; body never carries exception type/message/stack | FallbackExceptionHandler.cs | `_logger.LogError(exception, ...)` at line 39; body uses only Title/Status/Detail literals; no exception property reads in body | ✓ HONORED |
| D-13: No OpenTelemetry in Phase 4; deferred to Phase 5 | CorrelationIdMiddleware.cs, Program.cs | No Activity, no ActivitySource, no OTel package in Phase 4 | ✓ HONORED |
| D-14: Two-plan structure (04-01 build, 04-02 verify); PostgresFixture lifted as separate file under Middleware/ | Phase directory | 04-01-PLAN.md (autonomous: true) + 04-02-PLAN.md (autonomous: false); Middleware/PostgresFixture.cs is a separate file (not alias) | ✓ HONORED |

All 14 locked decisions honored.

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `src/BaseApi.Core/Exceptions/Handlers/FallbackExceptionHandler.cs` | 50 | Returns `TryWriteAsync` result instead of always `true` — if response has started, catch-all returns `false` | ⚠️ Warning | Edge case only: if response headers are already committed (e.g., streaming endpoints), FallbackExceptionHandler will not claim the exception, potentially causing blank 500 with no body. No current streaming endpoints in Phase 4; risk is for future phases. Documented as WR-01 in 04-REVIEW.md |
| `tests/BaseApi.Tests/Endpoints/TestController.cs` | 71, 90, 110 | `EnsureCreatedAsync` called per-request in FK/UQ/concurrency endpoints — race risk under concurrent tests | ⚠️ Warning | Under xUnit v3 class-parallelism, `ConcurrencyTokenTests.Task.WhenAll` sends two simultaneous POSTs; both call `EnsureCreatedAsync` on the same DB. EF's idempotent create is generally safe, but concurrent catalog reads can produce spurious DDL errors on a fresh DB. Documented as WR-02 in 04-REVIEW.md. Tests pass reliably across 3 runs; monitored |
| `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs` | 51-57 | FK/UQ regex allows table names with underscores to produce wrong column extraction | ⚠️ Warning | For `fk_test_child_parent_id` (underscore in table name), regex extracts `child_parent_id` instead of `parent_id`. Current test fixtures use `fk_testchild_parent_id` (no underscore) specifically to avoid this. Phase 8 entity tables must follow single-word naming convention or the regex will return incorrect column names. Documented as WR-03 in 04-REVIEW.md |
| `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` | 12-15 | XML doc says "AFTER UseExceptionHandler" but should say "INSIDE UseExceptionHandler wrapper" | ℹ️ Info | Imprecise comment could mislead developers about exception-path availability; no code impact. WR-01 annotation in 04-REVIEW.md IN-01 |
| `src/BaseApi.Core/Exceptions/Handlers/NotFoundExceptionHandler.cs` | 36 | `Detail = nfx.Message` echoes Id verbatim — caller must pass safe identifiers | ℹ️ Info | Accepted risk per D-07/ERROR-06; documented for Phase 8 service-layer audit. 04-REVIEW.md IN-02 |
| `tests/BaseApi.Tests/Endpoints/TestController.cs` | 86 | `[FromQuery] string name` with no length constraint | ℹ️ Info | Test-only code; no production risk. 04-REVIEW.md IN-03 |
| `Directory.Packages.props` | 95 | `Testcontainers.PostgreSql` pinned but no csproj references it | ℹ️ Info | Dangling entry; Phase 8 will consume it. 04-REVIEW.md IN-04 |

No blockers found. Three warnings are advisory from the code review (04-REVIEW.md); none prevent goal achievement. All 31 tests pass.

---

### Human Verification Required

None. All ROADMAP success criteria are verified by automated integration tests (`dotnet test` 31/31 GREEN) and the code evidence directly confirms D-01 through D-14 ordering invariants. No visual UI, real-time behavior, or external-service assertions require human inspection beyond the checkpoint already completed in Plan 04-02 (autonomous: false, human-supervised).

---

## Gaps Summary

No gaps. All five ROADMAP success criteria are GREEN. All 14 required REQ-IDs (OBSERV-09/10/11, ERROR-01..11) are implemented, tested, and confirmed in the codebase. All 14 locked CONTEXT decisions (D-01..D-14) are honored in the source. The three advisory warnings from 04-REVIEW.md are tracked above and do not block the phase goal.

One housekeeping item noted: the REQUIREMENTS.md traceability table rows for all 14 Phase 4 REQ-IDs still show "Pending" — this is stale documentation only. The authoritative `[x]` checkboxes above those rows show completion. No functional work is blocked.

---

_Verified: 2026-05-27_
_Verifier: Claude (gsd-verifier)_
