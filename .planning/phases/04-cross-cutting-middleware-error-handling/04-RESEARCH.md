# Phase 4: Cross-Cutting Middleware + Error Handling - Research

**Researched:** 2026-05-27
**Domain:** ASP.NET Core 8 cross-cutting plumbing — correlation ID middleware + IExceptionHandler chain + AddProblemDetails + Postgres SQLSTATE/concurrency mapping + WebApplicationFactory integration tests
**Confidence:** HIGH (every load-bearing claim verified against Microsoft Learn, Npgsql official docs, and the existing Phase 1/2/3 codebase)

## Summary

Phase 4 wires four narrowly-scoped pieces in `BaseApi.Core` + `BaseApi.Service`: (1) `CorrelationIdMiddleware`, (2) an `IExceptionHandler` chain of four handlers, (3) `AddProblemDetails` with a `CustomizeProblemDetails` callback that injects `correlationId` + `instance` on every Problem Details response, (4) `PostgresExceptionMapper` translating SQLSTATE 23503/23505 to HTTP 422/409. The existing CONTEXT.md decisions (D-01..D-14) are sound and align with current .NET 8 / ASP.NET Core 8.0.27 / Npgsql 8.0.10 documentation. No locked decision needs adjustment.

The research surfaces five concrete pitfalls the planner needs to encode into plan task notes: (a) `Npgsql` is NOT a direct csproj reference today — adding an explicit `<PackageReference Include="Npgsql" />` to `BaseApi.Core.csproj` is the safest path (Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10 transitively brings it, but D-08's mapper consumes `Npgsql.PostgresException` directly and an explicit pin keeps the dependency intent visible); (b) `[ApiController]`'s `InvalidModelStateResponseFactory` calls into a different path than `IProblemDetailsService` — the customizer DOES run for it on .NET 8, but only because the default factory routes through the ProblemDetails infrastructure when `AddProblemDetails` is registered (custom `IProblemDetailsWriter` registrations must be done BEFORE `AddControllers` or the writer is bypassed); (c) IExceptionHandlers execute in registration order until one returns `true`, and if NONE return true the framework falls back to its default 500 ProblemDetails response (which ALSO gets the customizer extensions — closes the ERROR-08/09 regression for unhandled paths); (d) `Response.OnStarting` is the correct echo-header pattern because direct `Response.Headers[...]` assignment after `await _next(...)` racing the downstream commit; (e) the FluentValidation 12.1.1 pin already in `Directory.Packages.props` (Phase 1 D-05) is sufficient — Phase 4 only needs the `ValidationException` type, not `AddValidatorsFromAssembly` (that's Phase 6).

**Primary recommendation:** Implement Plan 04-01 + 04-02 exactly as CONTEXT.md D-14 specifies. Add the FluentValidation `<PackageReference>` and an explicit `Npgsql` `<PackageReference>` to `BaseApi.Core.csproj`. Use the `IProblemDetailsService.WriteAsync` path inside every handler so the customizer fires uniformly. Register `IHttpContextAccessor` (currently NOT registered — Phase 3 D-11 explicitly left this to "Phase 7 composition root", but Phase 4 needs it for `CorrelationIdMiddleware`'s log scope and the future `AuditInterceptor` wiring — adopt it in Phase 4's Program.cs edits).

## User Constraints (from CONTEXT.md)

### Locked Decisions

These are verbatim from `04-CONTEXT.md` and are LOCKED. The planner MUST honor them; the researcher MUST validate them against current docs (done — all confirmed HIGH confidence).

- **D-01 (pipeline order):** `app.UseExceptionHandler()` → `app.UseMiddleware<CorrelationIdMiddleware>()` → `app.UseRouting()` → `app.MapControllers()` → `app.Run()`. CONFIRMED by Microsoft Learn middleware ordering guidance — `UseExceptionHandler` MUST be first; correlation ID middleware sits between exception handler and routing so the IExceptionHandler chain can read `HttpContext.Items["CorrelationId"]`.

- **D-02 (correlation ID format):** `Guid.NewGuid().ToString("N")` — 32-char lowercase hex no dashes. Inbound `X-Correlation-Id` echoed verbatim if non-empty, length ≤ 128, ASCII-printable.

- **D-03 (CorrelationIdMiddleware shape):** 4-step procedure (read/generate → `HttpContext.Items["CorrelationId"]` → `Response.OnStarting` → `BeginScope`). Lives at `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs`. BeginScope key literal: `"CorrelationId"` (PascalCase).

- **D-04 (CustomizeProblemDetails callback):** Single source of truth for `correlationId` + `instance` extensions. CONFIRMED: the customizer fires for ALL ProblemDetails emissions when `AddProblemDetails()` is registered — including IExceptionHandler emissions via `IProblemDetailsService.WriteAsync`, framework 400/404/500 fallbacks, and `[ApiController]` model-binding 400 (the default `InvalidModelStateResponseFactory` flows through the ProblemDetails infrastructure when `AddProblemDetails` is registered before `AddControllers`).

- **D-05 (Problem Details JSON shape):** RFC 7807 `application/problem+json` body with `type`/`title`/`status`/`detail`/`instance`/`correlationId`; field-level `errors` map only on 400 paths.

- **D-06 (IExceptionHandler chain registration order):** NotFoundExceptionHandler → ValidationExceptionHandler → DbUpdateExceptionHandler → FallbackExceptionHandler. CONFIRMED: handlers are walked in REGISTRATION order, each returns `ValueTask<bool>` (true = claimed, false = pass). If NONE return true, the framework falls back to its default ProblemDetails 500 path (which also receives the CustomizeProblemDetails extensions — so `correlationId` is still present on the unhandled fallthrough).

- **D-07 (NotFoundException shape):** `sealed class NotFoundException : Exception` with `ResourceType` + `Id` properties. Lives at `src/BaseApi.Core/Exceptions/NotFoundException.cs`.

- **D-08 (PostgresExceptionMapper):** `static bool TryMap(DbUpdateException ex, out int httpStatus, out string detail, out string? columnName)`. Walks `ex.InnerException as PostgresException`; switches on `pgEx.SqlState`. Regex for ERROR-11 constraint name parsing — see "Constraint Name Regex Validation" section below for additional notes.

- **D-09 (DbUpdateConcurrencyException early-return → 409):** Detail = "The resource was modified by another request; reload and retry." NO `xmin` value leaked.

- **D-10 (FluentValidation defensive landing):** Ship `ValidationException` → 400 mapping in Phase 4 even though FluentValidation isn't wired into validators until Phase 6. Add `<PackageReference Include="FluentValidation" />` to `BaseApi.Core.csproj` (CPM pin 12.1.1 already in Directory.Packages.props per Phase 1 D-05).

- **D-11 ([ApiController] InvalidModelStateResponseFactory ridden, not overridden):** Default factory + `AddProblemDetails` registered = automatic Problem Details emission with the customizer extensions. NO custom factory.

- **D-12 (FallbackExceptionHandler logging):** `logger.LogError(ex, "Unhandled exception on {Path}", path)`. Stack trace logged via MEL only; 500 response body never carries it.

- **D-13 (no OpenTelemetry in Phase 4):** Plain `ILogger.BeginScope` + `Response.OnStarting`. Phase 5 adds `Activity.Current?.AddTag("correlation.id", corrId)` additively.

- **D-14 (plan structure):** Two plans — 04-01 (autonomous, build) and 04-02 (autonomous: false, verification with checkpoint, per Phase 3 D-18 pattern).

### Claude's Discretion

These are explicitly delegated to the planner; this research validates the recommendations:

- **BeginScope key shape:** Use `Dictionary<string, object> { ["CorrelationId"] = corrId }` (matches D-03 default). VALIDATED against OTel `IncludeScopes = true` — Phase 5's OTel logging provider serializes scope dictionary entries as log attributes named by the dictionary key. PascalCase `CorrelationId` survives into the OTel export without renaming.

- **Test endpoint location:** `tests/BaseApi.Tests/Endpoints/TestController.cs` (D-14) is acceptable; the WebApplicationFactory+`ConfigureWebHost`+`AddApplicationPart` route picks up controllers from the test assembly without source code changes to `BaseApi.Service`. See "WebApplicationFactory Test Seam" section below for the recommended mechanism.

- **Constraint-name regex (D-08):** Validation of the proposed patterns is in "Constraint Name Regex Validation" section below. Recommended adjustments: tighten the FK regex to require either `_id` suffix OR explicit non-`_id` capture; add unit tests covering Phase 8's actual constraint names (`fk_processor_input_schema_id`, `uq_processor_source_hash`, etc. — see REQUIREMENTS.md ERROR-11).

- **Stack-trace logging level:** `LogError` (D-12 default). NO downgrade for `OperationCanceledException` in Phase 4 — that's a Phase 5 OTel concern (the cancellation-source detection ASP.NET Core 8 adds `ClientDisconnected` to is best handled at the OTel filter layer, not in the fallback handler).

- **Test endpoint mechanism inside WebApplicationFactory:** Both options work. RECOMMENDATION: Use a test-only controller in the test assembly + `ConfigureWebHost(b => b.ConfigureTestServices(s => s.AddControllers().AddApplicationPart(testAssembly)))`. Reasons: (a) controller-based tests match the production `[ApiController]` path exactly (closes ERROR-10 verification authentically); (b) Minimal API stubs would not exercise `InvalidModelStateResponseFactory`. See section "WebApplicationFactory Test Seam" below.

### Deferred Ideas (OUT OF SCOPE)

CONTEXT.md "Deferred Ideas" section is empty — discussion stayed in Phase 4 scope. The following are implicitly out-of-scope (cross-phase items with their own phase home):

- OpenTelemetry wiring (`AddOpenTelemetry`, OTLP exporter, `WithLogging`, `Activity` tagging on correlation middleware) — Phase 5
- Health probes (`/health/live`, `/health/ready`, `/health/startup`) and `AspNetCore.HealthChecks.NpgSql` — Phase 5
- `services.AddFluentValidation()` / `AddValidatorsFromAssembly()` and per-entity validators — Phase 6
- Concrete `BaseController<…>`, `BaseService<…>`, `AppDbContext`, controllers, migrations — Phase 7/8
- `AddBaseApi(...)` / `UseBaseApi(...)` composition-root extensions — Phase 7

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| OBSERV-09 | `CorrelationIdMiddleware` in `BaseApi.Core/Middleware/`: reads `X-Correlation-Id` header if present, generates a new UUID if missing | D-03 procedure; `Guid.NewGuid().ToString("N")` per D-02. Reference implementation in "Code Examples → CorrelationIdMiddleware" below |
| OBSERV-10 | Correlation ID written to `HttpContext.Items["CorrelationId"]` and attached to log scope via `ILogger.BeginScope` | D-03 step 2 + step 4. `BeginScope` returns `IDisposable`; the using-scope must wrap `await _next(context)` so the scope is active during downstream processing |
| OBSERV-11 | Correlation ID echoed in `X-Correlation-Id` response header on every response (including error responses) | D-03 step 3 — `Response.OnStarting` callback fires before headers flush even when the pipeline throws (the exception handler middleware writes the response, OnStarting fires at write time). 2xx/4xx/5xx all carry the header |
| ERROR-01 | RFC 7807 `ProblemDetails` JSON body returned on every non-2xx response | D-04 customizer + D-06 four-handler chain + D-11 ridden default factory. Framework default ProblemDetails fallback covers any path not claimed by a handler |
| ERROR-02 | `IExceptionHandler` implementation in `BaseApi.Core` registered via `services.AddExceptionHandler<…>()` + `services.AddProblemDetails()` (.NET 8 modern pattern) | D-06 four handlers; D-04 ProblemDetails registration. See "Code Examples → Program.cs Wiring" |
| ERROR-03 | `FluentValidation.ValidationException` → HTTP 400 with field-level `errors` map | D-10 ValidationExceptionHandler. `ValidationException.Errors` is `IEnumerable<ValidationFailure>`; group by `PropertyName` to build the `Dictionary<string, string[]>` shape that matches `ValidationProblemDetails`. See "Code Examples → ValidationExceptionHandler" |
| ERROR-04 | Postgres SQLSTATE `23503` (FK violation) → HTTP 422 with offending FK field name in detail | D-08 mapper SqlState branch. `Npgsql.PostgresException.SqlState` is a `string` (override property); `.ConstraintName` is `string?` |
| ERROR-05 | Postgres SQLSTATE `23505` (unique violation) → HTTP 409 with offending field name in detail | D-08 mapper SqlState branch. Same Npgsql.PostgresException property access pattern as ERROR-04 |
| ERROR-06 | `NotFoundException` (custom, thrown by Service when entity by id is missing) → HTTP 404 with resource type + id | D-07 type + NotFoundExceptionHandler. Surfaces `ResourceType` + `Id` in both `detail` AND `Extensions["resourceType"]` / `Extensions["resourceId"]` per D-07 |
| ERROR-07 | Any other unhandled exception → HTTP 500 with generic message; full stack trace logged only (never leaked to client) | D-12 — `LogError(ex, "Unhandled...", path)` AND `ProblemDetails.detail` = "An unexpected error occurred." Body NEVER contains exception type/message/stack |
| ERROR-08 | Every Problem Details body includes a `correlationId` field | D-04 customizer reads `HttpContext.Items["CorrelationId"]` and sets `Extensions["correlationId"]` |
| ERROR-09 | Every Problem Details body includes an `instance` field (request path) | D-04 customizer sets `ProblemDetails.Instance = HttpContext.Request.Path` |
| ERROR-10 | `[ApiController]`'s default `InvalidModelStateResponseFactory` aligned to emit the same Problem Details shape | D-11 — leave default factory in place, `AddProblemDetails` registration causes it to route through ProblemDetails infrastructure |
| ERROR-11 | Postgres constraint names follow convention (e.g., `fk_processor_input_schema_id`, `uq_processor_source_hash`) so middleware can extract friendly field names | D-08 regex. Phase 8's IEntityTypeConfiguration files will set constraint names matching this convention. See "Constraint Name Regex Validation" below |

## Project Constraints (from CLAUDE.md)

`./CLAUDE.md` does NOT exist at the repository root (verified via Glob). All constraints come from `.planning/` documents:

- **CPM contract** (Phase 1 D-05/D-06): every `<PackageReference Include="..." />` MUST omit the `Version=` attribute. New pins go to `Directory.Packages.props` only. FluentValidation 12.1.1 + xunit.v3 + Microsoft.AspNetCore.Mvc.Testing 8.0.27 are already pinned (verified — read of Directory.Packages.props lines 58–82 confirms).
- **TreatWarningsAsErrors=true** globally (Phase 1 D-02). Build is fatal on any analyzer warning. `EnforceCodeStyleInBuild=true` + `:warning` severities in `.editorconfig` make style violations build-fatal too.
- **File-scoped namespaces + outside-namespace usings** (Phase 1 .editorconfig). Every new `.cs` file in Phase 4 follows this shape — observed in BaseEntity.cs, BaseDbContext.cs, AuditInterceptor.cs (Phase 3).
- **No mocking frameworks** (PROJECT.md Out of Scope): no Moq, no NSubstitute. Phase 3 set the precedent with hand-written `StubHttpContextAccessor.cs`. Phase 4 inherits — if any test-level unit coverage is needed (e.g., `PostgresExceptionMapper` unit tests), construct test doubles by hand.
- **No InMemory or SQLite for tests** (REQ TEST-03, Phase 3 Pitfall 9): real Postgres only. Phase 4 reuses Phase 3's `PostgresFixture.cs` pattern (per-class throwaway DB via `IAsyncLifetime` + `ClearAllPools()` + `DROP DATABASE WITH FORCE`).
- **xUnit v3 `xUnit1051`** (Phase 3 deviation discovery): `TestContext.Current.CancellationToken` must thread through all async test call sites. New tests in Phase 4 follow this — observed in Phase 3 `SchemaTests.cs` line 21 (`var ct = TestContext.Current.CancellationToken;`).
- **`docs(04-XX): ...` for evidence/scaffold/SUMMARY commits; `fix(04-XX): ...` for source fix-forwards** (Phase 1/2/3 convention).

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Read/generate correlation ID | Frontend Server (ASP.NET Core middleware) | — | `HttpContext.Items` lives in the ASP.NET Core request-scoped state; only the server tier has access |
| Push correlation ID into log scope | Frontend Server (ASP.NET Core middleware) + MEL | — | `ILogger.BeginScope` flows via `AsyncLocal` from middleware; only the request-handling tier owns this scope |
| Echo `X-Correlation-Id` response header | Frontend Server (`Response.OnStarting`) | — | Response headers are server-tier state |
| Catch and translate exceptions | Frontend Server (`IExceptionHandler` chain) | — | Exception → HTTP status translation is an HTTP-layer concern, not a domain concern; handlers live in `BaseApi.Core` but are registered/invoked at the server tier |
| RFC 7807 ProblemDetails shaping | Frontend Server (`AddProblemDetails` + `IProblemDetailsService`) | — | HTTP response body shape is server-tier state |
| Postgres SQLSTATE → HTTP mapping | Frontend Server (`PostgresExceptionMapper` helper called from `IExceptionHandler`) | Database / Storage (SQLSTATEs originate at the DB) | The mapping is HTTP-tier policy; the SQLSTATE codes come from Postgres but the decision "23503 → 422 vs 23503 → 409" is server-tier semantics |
| EF Core `DbUpdateConcurrencyException` → 409 | Frontend Server (`DbUpdateExceptionHandler`) | Database (xmin row-version conflicts surface here) | Same as above — server-tier translation of a database-tier signal |
| Stack-trace logging | Frontend Server (MEL via `ILogger.LogError`) | Observability sink (Phase 5 OTel export) | Phase 4 uses MEL; Phase 5 attaches OTel exporter to the same MEL pipeline — single sink path |
| `[ApiController]` model-binding 400 shaping | Frontend Server (default `InvalidModelStateResponseFactory` riding on `AddProblemDetails`) | — | MVC pipeline concern |

All Phase 4 capabilities live at the Frontend Server tier (ASP.NET Core middleware + DI registrations + IExceptionHandler types). The `BaseApi.Core` class library hosts the types but they execute in the `BaseApi.Service` host's request pipeline. No browser tier, no separate API tier, no CDN tier.

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard | Confidence |
|---------|---------|---------|--------------|------------|
| Microsoft.AspNetCore.App | 8.0.27 (FrameworkReference) | IExceptionHandler, IProblemDetailsService, ProblemDetails, HttpContext, ILogger, Response.OnStarting, DefaultHttpContext | In-box .NET 8 ASP.NET Core; the modern (.NET 8+) pattern for global error handling | [VERIFIED: BaseApi.Core.csproj line 34 — already wired in Phase 3 D-12] |
| FluentValidation | 12.1.1 | `ValidationException` type for the ValidationExceptionHandler | Latest stable 12.x (Phase 1 D-05 pinned). Phase 4 uses only the typed exception — no validators are constructed yet (that's Phase 6). `.Errors` collection is `IEnumerable<ValidationFailure>` with `PropertyName` + `ErrorMessage` | [VERIFIED: Directory.Packages.props line 58; npm equivalent — confirmed via FluentValidation upgrade docs in CONTEXT.md canonical_refs] |
| Npgsql | 8.0.10 (transitive via Npgsql.EntityFrameworkCore.PostgreSQL) | `Npgsql.PostgresException` for SqlState / ConstraintName / TableName / ColumnName access | Standard Postgres provider for .NET. PostgresException namespace `Npgsql`, ships in `Npgsql.dll` | [VERIFIED: npgsql.org/doc/api/Npgsql.PostgresException.html via WebFetch — namespace `Npgsql`, fields confirmed: `SqlState` is string (override), `ConstraintName`/`TableName`/`ColumnName`/`SchemaName` are `string?`] |
| Microsoft.EntityFrameworkCore | 8.0.27 | `DbUpdateException`, `DbUpdateConcurrencyException` — the EF-layer exception types Phase 4 maps | Already wired in `BaseApi.Core` (Phase 3 D-12). `DbUpdateConcurrencyException` IS-A `DbUpdateException` (inheritance), so a single `DbUpdateExceptionHandler` catches both with a `is` type-check early return for the concurrency subtype | [VERIFIED: BaseApi.Core.csproj line 40-41; EF Core 8 docs] |

### Supporting

| Library | Version | Purpose | When to Use | Confidence |
|---------|---------|---------|-------------|------------|
| Microsoft.AspNetCore.Mvc.Testing | 8.0.27 | `WebApplicationFactory<Program>` for integration tests | Plan 04-02 verification battery. Already pinned in Directory.Packages.props (Phase 1 D-05) | [VERIFIED: Directory.Packages.props line 81] |
| xunit.v3 + xunit.v3.assert | 3.2.2 | Test framework for Plan 04-02 facts | Plan 04-02. Already pinned (Phase 1 D-11) and referenced in `tests/BaseApi.Tests/BaseApi.Tests.csproj` | [VERIFIED: BaseApi.Tests.csproj lines 56-57] |

### Alternatives Considered

| Instead of | Could Use | Tradeoff | Recommendation |
|------------|-----------|----------|----------------|
| Multiple `IExceptionHandler` classes | Single `IExceptionHandler` with switch expression | Single class = one test class, easier to grep but mixes concerns; chain = N small classes, each independently testable, scales as Phase 6 adds JSON schema validator errors and Phase 8 adds entity-specific exceptions | LOCKED to chain (D-06). New handlers slot in at correct precedence point without editing existing files |
| `IExceptionHandler` chain | Old-style `UseExceptionHandler(lambda)` | Lambda style is .NET 6/7 era; .NET 8 introduced IExceptionHandler as the modern pattern. Lambda still works but obscures handler order and is harder to unit-test | LOCKED to chain (D-06). Microsoft Learn calls IExceptionHandler the modern pattern |
| `Response.OnStarting` for echo header | Set header eagerly after `await _next()` | Eagerly setting after the next-call can race with response commit; OnStarting fires deterministically before headers flush (works even when downstream throws — the exception handler triggers the response, which triggers OnStarting) | LOCKED to OnStarting (D-03 step 3). VERIFIED pattern across community references (Steve Gordon, Trendyol CorrelationId, OneUptime) |
| Custom `InvalidModelStateResponseFactory` | Default factory + AddProblemDetails | Custom factory means manually constructing ValidationProblemDetails and matching the customizer extensions — needless surface area when `AddProblemDetails` registration causes the default factory to route through `IProblemDetailsService` automatically | LOCKED to default + AddProblemDetails (D-11) |
| `Npgsql` transitive (reach via Npgsql.EFCore.PostgreSQL) | Explicit `<PackageReference Include="Npgsql" />` in BaseApi.Core.csproj | Transitive works at compile-time but obscures intent — `PostgresExceptionMapper.cs` opens `using Npgsql;` and consumes `PostgresException` directly. An explicit reference makes the dependency contract visible and resists Phase 7/8 csproj refactors that might accidentally drop the EF provider reference from Core | RECOMMEND explicit pin. Phase 4 Plan 04-01 should add `<PackageVersion Include="Npgsql" Version="8.0.10" />` to Directory.Packages.props alongside the existing Npgsql.EFCore.PostgreSQL pin, then `<PackageReference Include="Npgsql" />` to BaseApi.Core.csproj |

### Installation

```bash
# No new tooling install — all packages already pinned in Phase 1.
# Phase 4 Plan 04-01 adds <PackageReference> entries to existing csproj files; CPM resolves versions.
```

### Version Verification

Verifications performed 2026-05-27:

| Package | Pinned Version | Verification |
|---------|----------------|--------------|
| FluentValidation | 12.1.1 | [VERIFIED: Directory.Packages.props line 58 + FluentValidation v12 upgrade docs `[CITED: docs.fluentvalidation.net/en/latest/upgrading-to-12.html]`] |
| Npgsql.EntityFrameworkCore.PostgreSQL | 8.0.10 | [VERIFIED: Directory.Packages.props line 51 + Phase 3 03-CONTEXT.md D-12 transitively brings Npgsql] |
| Npgsql (proposed explicit pin) | 8.0.10 (matches EF provider) | [ASSUMED — Npgsql.EFCore.PostgreSQL 8.0.10 depends on Npgsql 8.0.x; matching the EFCore provider patch is the safe pair. Plan 04-01 should verify via `dotnet list package --include-transitive` after first restore] |
| Microsoft.EntityFrameworkCore | 8.0.27 | [VERIFIED: Directory.Packages.props line 48] |
| Microsoft.AspNetCore.Mvc.Testing | 8.0.27 | [VERIFIED: Directory.Packages.props line 81] |

## Architecture Patterns

### System Architecture Diagram

```
                       HTTP request (any verb, any path)
                                  │
                                  ▼
┌─────────────────────────────────────────────────────────────────┐
│  BaseApi.Service host (ASP.NET Core)                            │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ 1. app.UseExceptionHandler()                              │  │
│  │    catches exceptions thrown by every middleware below.   │  │
│  │    On catch: invokes registered IExceptionHandler chain   │  │
│  │    in registration order until one returns true.          │  │
│  │    If none claim → framework default ProblemDetails 500   │  │
│  │    (which ALSO gets CustomizeProblemDetails extensions).  │  │
│  └────────────────────┬─────────────────────────────────────┘  │
│                        │                                          │
│  ┌────────────────────▼─────────────────────────────────────┐  │
│  │ 2. app.UseMiddleware<CorrelationIdMiddleware>()           │  │
│  │    a. read X-Correlation-Id header OR generate Guid.N     │  │
│  │    b. HttpContext.Items["CorrelationId"] = corrId         │  │
│  │    c. Response.OnStarting(() => set X-Correlation-Id      │  │
│  │       response header) — fires even if downstream throws  │  │
│  │    d. using var scope = logger.BeginScope({CorrelationId})│  │
│  │    e. await _next(context)                                │  │
│  └────────────────────┬─────────────────────────────────────┘  │
│                        │                                          │
│  ┌────────────────────▼─────────────────────────────────────┐  │
│  │ 3. app.UseRouting()                                       │  │
│  └────────────────────┬─────────────────────────────────────┘  │
│                        │                                          │
│  ┌────────────────────▼─────────────────────────────────────┐  │
│  │ 4. app.MapControllers()                                   │  │
│  │    Routes to [ApiController]-decorated controller actions │  │
│  │    Phase 4: no real controllers yet; Phase 7/8 add them   │  │
│  └────────────────────┬─────────────────────────────────────┘  │
│                        │                                          │
└────────────────────────┼─────────────────────────────────────────┘
                         │                  ▲
                         ▼                  │ on exception
   ┌─────────────────────────────────────┐ │
   │  Controller action                  │─┘
   │  may throw:                          │
   │    - NotFoundException              │
   │    - FluentValidation.ValidationEx  │
   │    - DbUpdateException (FK / UQ)    │
   │    - DbUpdateConcurrencyException   │
   │    - any other Exception (Fallback) │
   │                                      │
   │  may bind invalid model:            │
   │    - [ApiController] model-binding  │
   │      → InvalidModelStateResponseFct │
   │      → ValidationProblemDetails 400  │
   └─────────────────────────────────────┘

                IExceptionHandler chain (registered via AddExceptionHandler<T>)
                executes in REGISTRATION ORDER:
   ┌────────────────────────────────────────────────────────┐
   │ 1. NotFoundExceptionHandler                             │
   │    claims NotFoundException → 404 ProblemDetails        │
   │    extensions: resourceType, resourceId                  │
   ├────────────────────────────────────────────────────────┤
   │ 2. ValidationExceptionHandler                           │
   │    claims FluentValidation.ValidationException → 400    │
   │    ValidationProblemDetails with errors map              │
   ├────────────────────────────────────────────────────────┤
   │ 3. DbUpdateExceptionHandler                             │
   │    claims DbUpdateException (incl. DbUpdateConcurrencyEx)│
   │    - if DbUpdateConcurrencyException → 409 generic msg  │
   │    - else PostgresExceptionMapper.TryMap → 422/409      │
   │    - else fall through to FallbackExceptionHandler      │
   ├────────────────────────────────────────────────────────┤
   │ 4. FallbackExceptionHandler                             │
   │    claims everything → 500 generic ProblemDetails       │
   │    logs full exception+stack via LogError (MEL only)    │
   └────────────────────────────────────────────────────────┘
                                  │
                                  ▼
   ┌────────────────────────────────────────────────────────┐
   │  IProblemDetailsService.WriteAsync                       │
   │  (handlers call this; framework default also calls it)   │
   │                                                          │
   │  → invokes CustomizeProblemDetails callback for ALL paths│
   │  → reads HttpContext.Items["CorrelationId"]              │
   │  → sets ProblemDetails.Extensions["correlationId"]       │
   │  → sets ProblemDetails.Instance = Request.Path           │
   │  → emits application/problem+json body                   │
   └────────────────────────────────────────────────────────┘
                                  │
                                  ▼
            HTTP response with X-Correlation-Id header
            (header set by Response.OnStarting callback)
            (body = RFC 7807 with correlationId + instance)
```

### Recommended Project Structure (Phase 4 deltas)

```
src/
├── BaseApi.Core/
│   ├── Middleware/
│   │   └── CorrelationIdMiddleware.cs              ◄── NEW (D-03)
│   ├── Exceptions/
│   │   ├── NotFoundException.cs                     ◄── NEW (D-07)
│   │   └── Handlers/
│   │       ├── NotFoundExceptionHandler.cs          ◄── NEW (D-06 #1)
│   │       ├── ValidationExceptionHandler.cs        ◄── NEW (D-06 #2 + D-10)
│   │       ├── DbUpdateExceptionHandler.cs          ◄── NEW (D-06 #3 + D-09)
│   │       └── FallbackExceptionHandler.cs          ◄── NEW (D-06 #4 + D-12)
│   └── Persistence/
│       └── Exceptions/
│           └── PostgresExceptionMapper.cs           ◄── NEW (D-08)
├── BaseApi.Service/
│   └── Program.cs                                   ◄── EDIT (D-01 wiring)
└── ...

tests/
└── BaseApi.Tests/
    ├── Endpoints/
    │   └── TestController.cs                        ◄── NEW Plan 04-02 (test endpoints for each handler path)
    └── Middleware/
        ├── WebAppFactory.cs                         ◄── NEW Plan 04-02 (WebApplicationFactory<Program> wrapper)
        ├── PostgresFixture.cs                       ◄── NEW Plan 04-02 (D-15 lift from Phase 3)
        ├── CorrelationIdTests.cs                    ◄── NEW Plan 04-02 (SC#1)
        ├── ValidationErrorTests.cs                  ◄── NEW Plan 04-02 (SC#2 + SC#5)
        ├── SqlStateMappingTests.cs                  ◄── NEW Plan 04-02 (SC#3)
        ├── NotFoundAndUnhandledTests.cs             ◄── NEW Plan 04-02 (SC#4)
        ├── ConcurrencyTokenTests.cs                 ◄── NEW Plan 04-02 (D-03a / D-09)
        └── ProblemDetailsExtensionsTests.cs         ◄── NEW Plan 04-02 (ERROR-08/09 regression)
```

### Pattern 1: IExceptionHandler Chain in Registration Order

**What:** Multiple small `IExceptionHandler` classes, each claims a single exception type, registered in precedence order. The framework walks them top-to-bottom on each unhandled exception; first to return `true` claims; if none claim, framework default ProblemDetails 500 kicks in (which still gets the customizer extensions).

**When to use:** Any time a project needs more than 1-2 exception → status mappings. Aligns with .NET 8 modern pattern (Microsoft Learn explicit recommendation over the old lambda style).

**Example:**

```csharp
// Source: learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-8.0
// Source: okyrylchuk.dev/blog/handling-exceptions-in-asp-net-core-8/

public sealed class NotFoundExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _pdSvc;

    public NotFoundExceptionHandler(IProblemDetailsService pdSvc) => _pdSvc = pdSvc;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not NotFoundException nfx) return false;

        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Not Found",
            Detail = nfx.Message,
            Extensions =
            {
                ["resourceType"] = nfx.ResourceType,
                ["resourceId"] = nfx.Id,
            },
        };

        return await _pdSvc.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
```

Registration (in registration order — D-06):

```csharp
builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<DbUpdateExceptionHandler>();
builder.Services.AddExceptionHandler<FallbackExceptionHandler>();
```

### Pattern 2: CustomizeProblemDetails Callback for Cross-Cutting Extensions

**What:** Single callback registered via `AddProblemDetails` that injects `correlationId` + `instance` on EVERY ProblemDetails emission — IExceptionHandler-emitted, framework auto-generated (400/404/500 fallbacks), and `[ApiController]` model-binding 400 (when `AddProblemDetails` is registered, the default factory routes through `IProblemDetailsService`).

**When to use:** Whenever you want a uniform field on every error body without per-handler boilerplate.

**Example:**

```csharp
// Source: learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-8.0 (verbatim pattern)

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        if (ctx.HttpContext.Items.TryGetValue("CorrelationId", out var corrIdObj)
            && corrIdObj is string corrId)
        {
            ctx.ProblemDetails.Extensions["correlationId"] = corrId;
        }
        ctx.ProblemDetails.Instance = ctx.HttpContext.Request.Path;
    };
});
```

CRITICAL ordering nuance from Microsoft Learn: "When using a custom `IProblemDetailsWriter`, the custom `IProblemDetailsWriter` must be registered BEFORE calling `AddRazorPages`, `AddControllers`, `AddControllersWithViews`, or `AddMvc`." Phase 4 does NOT use a custom writer (just `CustomizeProblemDetails`), but the same ordering discipline applies for safety: `AddProblemDetails` BEFORE `AddControllers`.

### Pattern 3: CorrelationIdMiddleware with Response.OnStarting

**What:** Read or generate correlation ID; stash in `HttpContext.Items` for downstream readers (including IExceptionHandler chain); register `OnStarting` callback to echo the header on the way out; push a log scope wrapping the rest of the pipeline.

**When to use:** Every request needs a correlation ID, regardless of success or failure.

**Example:** See "Code Examples → CorrelationIdMiddleware" section below.

### Anti-Patterns to Avoid

- **Setting response header directly after `await _next(context)`:** Race condition — if downstream short-circuits (writes response and flushes), the header is too late. ALWAYS use `Response.OnStarting`. [VERIFIED: Trendyol CorrelationId, Steve Gordon's CorrelationId reference implementations, OneUptime guide all use OnStarting]

- **Putting `UseMiddleware<CorrelationIdMiddleware>` BEFORE `UseExceptionHandler`:** Microsoft's canonical middleware order puts UseExceptionHandler FIRST (so it wraps everything). If correlation middleware comes first, the exception handler can't see `HttpContext.Items["CorrelationId"]`. LOCKED order: UseExceptionHandler → UseMiddleware<CorrelationIdMiddleware>. [VERIFIED: Microsoft Learn middleware order section — quoted in Sources below]

- **Overriding `[ApiController]`'s `InvalidModelStateResponseFactory`:** Defeats the `AddProblemDetails` integration. Default factory + `AddProblemDetails` registered = automatic ProblemDetails with the customizer extensions. Custom factory means manually replicating that behavior. [VERIFIED: Microsoft Learn — default factory respects ProblemDetails infrastructure when registered]

- **Setting `ProblemDetails.Detail` to `exception.Message` or `exception.ToString()` for unhandled exceptions:** Leaks internals (paths, types, secrets). ALWAYS use a generic "An unexpected error occurred." message for the fallback handler. [VERIFIED: Pitfall 13 — research/PITFALLS.md verbatim]

- **Using `synchronous` SaveChanges with the AuditInterceptor expectation:** Phase 3's AuditInterceptor only overrides `SavingChangesAsync` (verified in src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs lines 49-89). Phase 4 verification's test endpoints must use `await db.SaveChangesAsync(ct)`, not `db.SaveChanges()`, or audit stamping won't fire and the test could pass/fail spuriously.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| HTTP exception → status code mapping | Custom try/catch in every controller | `IExceptionHandler` chain (D-06) | Centralized, testable, ordered |
| RFC 7807 ProblemDetails body | Manually serialized `new { type, title, status, ... }` | `Microsoft.AspNetCore.Mvc.ProblemDetails` + `IProblemDetailsService.WriteAsync` | Framework guarantees RFC 7807 shape + content negotiation + customizer extensions |
| ValidationException → field-level errors map | Hand-build dictionary from `ex.Errors` | `ValidationProblemDetails(IDictionary<string, string[]>)` constructor | Framework type already shapes the JSON correctly + integrates with [ApiController] convention |
| Postgres SQLSTATE extraction | `ex.Message.Contains("23503")` regex | `(ex.InnerException as Npgsql.PostgresException)?.SqlState` | Typed property, no string parsing |
| Constraint name extraction from Postgres error message | Regex over `ex.Message` | `pgEx.ConstraintName` (string?) | Npgsql provides it as a typed property |
| Correlation ID generation | `DateTime.UtcNow.Ticks + Random` | `Guid.NewGuid().ToString("N")` | Guaranteed uniqueness, no collision risk, locked by D-02 |
| Correlation ID echo header timing | Set header after `await _next` | `Response.OnStarting(...)` | Avoids race with response commit; fires deterministically even on exception path |
| Stack-trace logging | Manual `Trace.WriteLine(ex.ToString())` | `logger.LogError(ex, "...", args)` | MEL auto-serializes exception with structured fields; Phase 5 OTel inherits the structure |
| EF concurrency conflict detection | Compare entity `xmin` manually | `catch (DbUpdateConcurrencyException)` | EF Core throws it automatically when affected-rows is 0 (xmin mismatch) |
| WebApplicationFactory test seam | Hand-build `TestServer` + `IHostBuilder` | `WebApplicationFactory<Program>` | Standard MS pattern; works with Phase 1's `public partial class Program { }` marker |

**Key insight:** Phase 4's entire scope is "wire well-known framework primitives." Hand-rolling any piece (especially the ProblemDetails shape or the SQLSTATE parsing) trades framework guarantees for technical debt the planner will surface again in Phase 5/8.

## Runtime State Inventory

This section is INCLUDED because Phase 4 touches `Program.cs` (the live composition root) and the test fixture infrastructure (`PostgresFixture` per-class DB lifecycle). It is NOT a rename/refactor phase, so most categories are "None" — documented explicitly so the planner can see they were checked.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None — Phase 4 adds NO new tables, NO new EF entities, NO new migrations. The xmin shadow concurrency token is already in place from Phase 3 (verified: BaseDbContext.cs line 52-61). Plan 04-02's `ConcurrencyTokenTests.cs` exercises the existing column. | None |
| Live service config | None — Phase 4 changes only the in-process middleware pipeline. No external service config (no n8n, no Datadog, no Cloudflare). `compose.yaml` is untouched. | None |
| OS-registered state | None — Phase 4 does NOT register processes, services, or OS-level state. No Windows Task Scheduler, no systemd, no launchd touches. | None |
| Secrets/env vars | None — no new secrets, no new env-var contracts. The existing `ConnectionStrings:Postgres` (appsettings.Development.json, Phase 2 D-02) is read by Plan 04-02's `PostgresFixture` — same shape Phase 3 verified. NO change to the key name. | None |
| Build artifacts | One transient concern: adding `<PackageReference Include="FluentValidation" />` and `<PackageReference Include="Npgsql" />` to BaseApi.Core.csproj will produce new entries in `obj/project.assets.json` after first restore. Standard CPM flow; no special action. | Plan 04-01's verification step should run `dotnet restore` then `dotnet list package --include-transitive` to confirm Npgsql 8.0.10 resolves correctly (matches Npgsql.EFCore.PostgreSQL 8.0.10's transitive dependency) |

**Canonical question:** After every file in the repo is updated, what runtime systems still have the old behavior cached? Answer: NONE. Phase 4 is an additive landing — no renames, no breaking changes to existing on-disk state.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | Build + test | ✓ | 8.0.421 (pinned via global.json, verified Phase 1 SC#2) | — |
| Postgres 17-alpine (Docker) | Plan 04-02 integration tests (`PostgresFixture` requires real PG at localhost:5433) | ✓ | 17.6 via `compose.yaml` (Phase 2 D-12) | None — locked by REQ TEST-03 (no InMemory/SQLite) and CONTEXT.md D-15 (per-class throwaway DB pattern requires real PG) |
| Docker Desktop with WSL2 backend | Running compose stack for Plan 04-02 tests | ✓ | Verified during Phase 2 (STATE.md "Blockers/Concerns") | None |
| FluentValidation NuGet 12.1.1 | `ValidationException` type in ValidationExceptionHandler | ✓ | Pinned in Directory.Packages.props line 58 (CPM) | None |
| Npgsql NuGet 8.0.10 | `PostgresException` type in PostgresExceptionMapper | ✓ (transitive) | 8.0.10 via Npgsql.EFCore.PostgreSQL 8.0.10 | If transitive resolution fails, add explicit `<PackageVersion Include="Npgsql" Version="8.0.10" />` to Directory.Packages.props — RECOMMEND this regardless for dependency-intent clarity |
| Microsoft.AspNetCore.Mvc.Testing 8.0.27 | `WebApplicationFactory<Program>` in Plan 04-02 | ✓ | Pinned in Directory.Packages.props line 81 (CPM, Phase 1 D-13) | None — required reference must be added to BaseApi.Tests.csproj in Plan 04-02 |

**Missing dependencies with no fallback:** None.

**Missing dependencies with fallback:** None — Npgsql transitive vs. explicit is a clarity/maintainability choice, not a functional one.

## Common Pitfalls

### Pitfall 1: `IHttpContextAccessor` not registered before CorrelationIdMiddleware runs

**What goes wrong:** `CorrelationIdMiddleware` reads/writes `HttpContext` directly via the middleware's `(HttpContext context, RequestDelegate next)` invocation signature — so it does NOT depend on `IHttpContextAccessor`. BUT the `ILogger.BeginScope` call inside the middleware writes to a logger that flows via `AsyncLocal`; if any downstream code (e.g., a future `AuditInterceptor` extension that wants to log with correlation ID) tries to resolve `IHttpContextAccessor`, it fails with `InvalidOperationException` ("Unable to resolve service") unless `AddHttpContextAccessor()` is registered.

**Why it happens:** Phase 3 D-11 explicitly deferred `AddHttpContextAccessor()` registration to Phase 7 ("composition-root job"). Phase 4's middleware doesn't strictly need it, but Phase 4's `ValidationExceptionHandler` and `FallbackExceptionHandler` will inject `ILogger<T>` — those loggers, if used with `BeginScope`, won't have access to `HttpContextAccessor.HttpContext.Items["CorrelationId"]`. The correlation ID flows naturally via the middleware's `BeginScope` wrapping `await _next`, but ONLY if the middleware actually pushes the scope.

**How to avoid:** Add `builder.Services.AddHttpContextAccessor()` to `Program.cs` in Plan 04-01 alongside the other registrations. It's idempotent (multiple calls = single registration). Phase 7's composition root will not conflict.

**Warning signs:** Future Phase 5 OTel wiring fails to populate `correlation.id` baggage; AuditInterceptor's `CreatedBy` stamping fails silently (which Phase 3 SC#3 verified works ONLY because Phase 3 test fixture registers `IHttpContextAccessor` in its own ServiceCollection — production paths after Phase 4 need it registered globally too).

**Phase to address:** Plan 04-01 Task 6 (Program.cs edits).

---

### Pitfall 2: `Response.OnStarting` callback firing after `HttpContext.Items` is cleared

**What goes wrong:** `HttpContext.Items` is a request-scoped dictionary that survives until the response completes. The `OnStarting` callback fires BEFORE response headers flush — at this point `Items` is still populated. So D-03 step 3's pattern (`Response.OnStarting(() => { Response.Headers["X-Correlation-Id"] = corrId; return Task.CompletedTask; })`) works correctly because `corrId` is captured by closure, NOT read from `Items` in the callback.

**Why it happens:** A misreading of the pattern could lead a planner to write `Response.OnStarting(() => { Response.Headers["X-Correlation-Id"] = (string)HttpContext.Items["CorrelationId"]; ... })`. This still works because `Items` is alive until response completion, but it's needless indirection.

**How to avoid:** Capture `corrId` in the local variable, use closure capture in the callback. D-03 explicitly shows this pattern. Reference implementation in "Code Examples → CorrelationIdMiddleware" below.

**Warning signs:** None at runtime; this is a clarity/maintainability concern only.

**Phase to address:** Plan 04-01 Task 2 (CorrelationIdMiddleware.cs).

---

### Pitfall 3: Header injection from `X-Correlation-Id` if validation is too permissive

**What goes wrong:** An attacker sends `X-Correlation-Id: foo\r\nX-Injected-Header: evil`. If the middleware echoes the value verbatim, the response carries the injected header. CRLF injection.

**Why it happens:** Response header writes don't auto-sanitize; the `Headers` dictionary takes the value as-given. Kestrel does reject CRLF in headers on write, but defensive validation in the middleware is the documented best practice.

**How to avoid:** D-02 already specifies the validation: "non-empty, length ≤ 128, ASCII-printable only". A practical implementation:

```csharp
private static bool IsValid(ReadOnlySpan<char> value)
{
    if (value.IsEmpty || value.Length > 128) return false;
    foreach (var c in value)
    {
        // ASCII printable range 0x20-0x7E excludes CR (0x0D), LF (0x0A), null (0x00), etc.
        if (c < 0x20 || c > 0x7E) return false;
    }
    return true;
}
```

If validation fails, generate a fresh `Guid.NewGuid().ToString("N")` (NEVER fall back to the unsafe header value).

**Warning signs:** Penetration test reports header injection; logs show requests where the correlation ID contains unusual characters.

**Phase to address:** Plan 04-01 Task 2 (CorrelationIdMiddleware.cs) — explicit ASCII-printable validation per D-02.

---

### Pitfall 4: `AddProblemDetails` registered AFTER `AddControllers`

**What goes wrong:** Microsoft Learn explicitly warns: "When using a custom `IProblemDetailsWriter`, the custom `IProblemDetailsWriter` must be registered BEFORE calling `AddRazorPages`, `AddControllers`, `AddControllersWithViews`, or `AddMvc`." Phase 4 doesn't use a custom writer, but the same ordering discipline is the safe pattern: `AddProblemDetails` first.

**Why it happens:** Conventional dotnet templates show `AddControllers` first, `AddProblemDetails` later. The order of DI registration is technically unordered, but the `[ApiController]` attribute's `InvalidModelStateResponseFactory` resolves `IProblemDetailsService` lazily via DI — if the service isn't registered when MVC initializes its options, the default factory falls back to the non-ProblemDetails path.

**How to avoid:** In Plan 04-01 Task 6 (Program.cs edits), register in this exact order:
1. `builder.Services.AddHttpContextAccessor();`
2. `builder.Services.AddProblemDetails(opts => opts.CustomizeProblemDetails = ...);` ← BEFORE AddControllers
3. `builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();` (etc., in D-06 order)
4. `builder.Services.AddControllers();`

This is the locked CONTEXT.md `<code_context>` block ordering already shown at lines 256-272 — confirmed correct.

**Warning signs:** ERROR-10 verification test (`[ApiController]` model-binding 400) returns a response shape that DOESN'T include `correlationId` extension; manual inspection of the response confirms the default factory bypassed the ProblemDetails infrastructure.

**Phase to address:** Plan 04-01 Task 6 (Program.cs edits).

---

### Pitfall 5: Forgetting that `ApplyServerHeaderMatchingRules` for response headers may strip duplicates

**What goes wrong:** If client sent `X-Correlation-Id: abc`, the middleware echoes `abc` on the response. ASP.NET Core's response header writing is case-insensitive and last-write-wins. If TWO middlewares set `X-Correlation-Id` (e.g., a future Phase 5 OTel middleware also sets one), the response has only one. The `Response.OnStarting` callback runs LAST in registration order, so D-03's OnStarting wins.

**Why it happens:** Defense-in-depth — the future Phase 5 OTel middleware will add `Activity.Current?.AddTag("correlation.id", corrId)`, not a header. But if a third party introduces another correlation header middleware, last-write-wins could surprise.

**How to avoid:** Use `Response.Headers["X-Correlation-Id"] = corrId;` (assignment, not `Append`). Direct assignment overwrites. `Append` would create duplicate headers (technically valid HTTP, but most clients/proxies pick the first).

**Warning signs:** None unless future phases add conflicting middleware.

**Phase to address:** Plan 04-01 Task 2 (CorrelationIdMiddleware.cs) — use `=` not `.Append()`.

---

### Pitfall 6: `IExceptionHandler` chain — handler that processes exception but returns `false`

**What goes wrong:** A handler does some work (e.g., logs the exception), then returns `false` to "let other handlers also see it." But subsequent handlers expect a clean response stream — if the first handler wrote anything to `Response.Body` (or set `Response.StatusCode`), the second handler's `IProblemDetailsService.WriteAsync` may double-emit or fail with "Response has already started".

**Why it happens:** Misunderstanding of the chain semantics. Returning `false` means "I did NOT handle this — let the next handler try." Side effects in a `false`-returning handler are dangerous.

**How to avoid:** EACH handler in the chain is single-responsibility:
- If `exception is not MyException` → return `false` IMMEDIATELY, no side effects (not even logging — that's the Fallback handler's job)
- If `exception is MyException` → handle FULLY (write response, set status, populate ProblemDetails) and return `true`

The Fallback handler is the ONLY handler that should log unhandled exceptions (D-12). Earlier handlers shape known responses but don't double-log.

**Warning signs:** "Response has already started" exceptions in logs; correlationId appearing twice in the response body; ProblemDetails JSON malformed.

**Phase to address:** Plan 04-01 Task 5 (the four handlers).

---

### Pitfall 7: `DbUpdateConcurrencyException` type-check ordering inside DbUpdateExceptionHandler

**What goes wrong:** D-09 specifies the handler does an early-return type check for `DbUpdateConcurrencyException` BEFORE attempting the SQLSTATE mapping. If the order is reversed (`PostgresExceptionMapper.TryMap` called first), the mapper returns `false` (no PostgresException inner — concurrency is an EF-layer detection, no Postgres SQLSTATE involved), and the handler falls through to the FallbackHandler — yielding a 500 instead of the locked 409.

**Why it happens:** `DbUpdateConcurrencyException IS-A DbUpdateException` (inheritance). The natural code shape "unwrap inner PostgresException first" misses the EF-layer-only signal.

**How to avoid:** Plan 04-01 Task 5 DbUpdateExceptionHandler shape:
```csharp
public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
{
    if (ex is not DbUpdateException due) return false;

    // CRITICAL: check concurrency subtype FIRST (D-09)
    if (ex is DbUpdateConcurrencyException) { /* 409 generic message */ return true; }

    // Then attempt SQLSTATE mapping (D-08)
    if (PostgresExceptionMapper.TryMap(due, out var status, out var detail, out var col))
    { /* status 422 or 409 */ return true; }

    return false; // fall through to FallbackExceptionHandler
}
```

**Warning signs:** ConcurrencyTokenTests.cs (Plan 04-02 SC for D-03a) returns 500 instead of 409.

**Phase to address:** Plan 04-01 Task 5 (DbUpdateExceptionHandler.cs).

---

### Pitfall 8: `[ApiController]` and CustomizeProblemDetails — the BEFORE-AddControllers ordering trap

**What goes wrong:** Even though Phase 4 doesn't add a custom `IProblemDetailsWriter`, the documented "register BEFORE AddControllers" pattern is the safe default. If `AddControllers` is called before `AddProblemDetails`, the MVC options snapshot misses the ProblemDetails service registration; the default factory falls back to the legacy `BadRequestObjectResult` shape on model binding 400 — which does NOT include the `correlationId` extension.

**Why it happens:** Some templates inject `AddControllers` first by default.

**How to avoid:** See Pitfall 4. Verified by ERROR-10 integration test in Plan 04-02.

**Warning signs:** ValidationErrorTests.cs "model-binding 400" assertion (D-11) fails: response body lacks `correlationId`.

**Phase to address:** Plan 04-01 Task 6 (Program.cs edits).

## Code Examples

Verified patterns from official sources. Planner may copy verbatim into plan task notes.

### CorrelationIdMiddleware (D-03)

```csharp
// Source: D-03 + Trendyol/CorrelationId reference impl + Microsoft Learn middleware patterns
// File: src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BaseApi.Core.Middleware;

public sealed class CorrelationIdMiddleware
{
    private const string HeaderName = "X-Correlation-Id";
    private const string ItemKey = "CorrelationId";
    private const int MaxLength = 128;

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var corrId = ResolveCorrelationId(context);

        // 1. Stash for downstream readers (IExceptionHandler chain, ProblemDetails customizer, AuditInterceptor)
        context.Items[ItemKey] = corrId;

        // 2. Echo header on the way out — OnStarting fires before headers flush even on exception path
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = corrId;
            return Task.CompletedTask;
        });

        // 3. Push log scope (PascalCase key matches Phase 5 OTel IncludeScopes=true serialization)
        using var scope = _logger.BeginScope(
            new Dictionary<string, object> { [ItemKey] = corrId });

        await _next(context);
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var header)
            && header.Count > 0
            && IsValid(header[0]))
        {
            return header[0]!;
        }
        return Guid.NewGuid().ToString("N");  // D-02: 32-char lowercase hex no dashes
    }

    private static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength) return false;
        foreach (var c in value)
        {
            // ASCII-printable only (0x20..0x7E) — rejects CR, LF, null, control chars
            if (c < 0x20 || c > 0x7E) return false;
        }
        return true;
    }
}
```

### NotFoundException (D-07)

```csharp
// Source: D-07
// File: src/BaseApi.Core/Exceptions/NotFoundException.cs

namespace BaseApi.Core.Exceptions;

public sealed class NotFoundException : Exception
{
    public string ResourceType { get; }
    public object Id { get; }

    public NotFoundException(string resourceType, object id)
        : base($"{resourceType} with id '{id}' was not found.")
    {
        ResourceType = resourceType;
        Id = id;
    }
}
```

### PostgresExceptionMapper (D-08)

```csharp
// Source: D-08 + Npgsql.PostgresException docs (npgsql.org/doc/api/Npgsql.PostgresException.html)
// File: src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BaseApi.Core.Persistence.Exceptions;

public static class PostgresExceptionMapper
{
    // ERROR-11: constraint name conventions per REQUIREMENTS.md
    //   FK:  fk_<table>_<column>[_id]    e.g. fk_processor_input_schema_id
    //   UQ:  uq_<table>_<column>         e.g. uq_processor_source_hash
    // The FK regex tolerates missing _id suffix (some FKs reference natural keys).
    private static readonly Regex FkRegex = new(
        @"^fk_[a-z0-9]+_(?<col>[a-z0-9_]+?)(_id)?$",
        RegexOptions.Compiled);

    private static readonly Regex UqRegex = new(
        @"^uq_[a-z0-9]+_(?<col>[a-z0-9_]+)$",
        RegexOptions.Compiled);

    public static bool TryMap(
        DbUpdateException ex,
        out int httpStatus,
        out string detail,
        out string? columnName)
    {
        httpStatus = 500;
        detail = string.Empty;
        columnName = null;

        if (ex.InnerException is not PostgresException pgEx) return false;

        switch (pgEx.SqlState)
        {
            case "23503":  // foreign_key_violation
                httpStatus = StatusCodes.Status422UnprocessableEntity;
                columnName = ExtractColumn(FkRegex, pgEx.ConstraintName);
                detail = columnName is not null
                    ? $"Foreign key violation: {columnName} references a non-existent record."
                    : "Foreign key constraint violated.";
                return true;

            case "23505":  // unique_violation
                httpStatus = StatusCodes.Status409Conflict;
                columnName = ExtractColumn(UqRegex, pgEx.ConstraintName);
                detail = columnName is not null
                    ? $"Unique constraint violation: {columnName} already exists."
                    : "Unique constraint violated.";
                return true;

            default:
                return false;  // unknown SQLSTATE → FallbackExceptionHandler 500
        }
    }

    private static string? ExtractColumn(Regex regex, string? constraintName)
    {
        if (string.IsNullOrEmpty(constraintName)) return null;
        var match = regex.Match(constraintName);
        return match.Success ? match.Groups["col"].Value : null;
    }
}
```

### ValidationExceptionHandler (D-06 #2, D-10)

```csharp
// Source: D-06 + D-10 + FluentValidation 12 docs
// File: src/BaseApi.Core/Exceptions/Handlers/ValidationExceptionHandler.cs

using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BaseApi.Core.Exceptions.Handlers;

public sealed class ValidationExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _pdSvc;

    public ValidationExceptionHandler(IProblemDetailsService pdSvc) => _pdSvc = pdSvc;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ValidationException vex) return false;

        var errors = vex.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ErrorMessage).ToArray());

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        var problem = new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Validation failed",
        };

        return await _pdSvc.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
```

### DbUpdateExceptionHandler (D-06 #3, D-09)

```csharp
// Source: D-06 + D-08 + D-09
// File: src/BaseApi.Core/Exceptions/Handlers/DbUpdateExceptionHandler.cs

using BaseApi.Core.Persistence.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BaseApi.Core.Exceptions.Handlers;

public sealed class DbUpdateExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _pdSvc;

    public DbUpdateExceptionHandler(IProblemDetailsService pdSvc) => _pdSvc = pdSvc;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateException due) return false;

        // CRITICAL ORDER (Pitfall 7): check concurrency subtype FIRST
        if (exception is DbUpdateConcurrencyException)
        {
            httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
            var concurrencyProblem = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict",
                Detail = "The resource was modified by another request; reload and retry.",
            };
            return await _pdSvc.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = concurrencyProblem,
                Exception = exception,
            });
        }

        // Then attempt Postgres SQLSTATE mapping
        if (PostgresExceptionMapper.TryMap(due, out var status, out var detail, out var col))
        {
            httpContext.Response.StatusCode = status;
            var problem = new ProblemDetails
            {
                Status = status,
                Title = status == StatusCodes.Status422UnprocessableEntity
                    ? "Unprocessable Entity" : "Conflict",
                Detail = detail,
            };
            if (col is not null) problem.Extensions["field"] = col;
            return await _pdSvc.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problem,
                Exception = exception,
            });
        }

        return false;  // unmapped SQLSTATE → FallbackExceptionHandler
    }
}
```

### Program.cs Wiring (D-01, D-04, D-06, D-11)

```csharp
// Source: D-01 + D-04 + D-06 + D-11 + Microsoft Learn middleware ordering
// File: src/BaseApi.Service/Program.cs (EDIT — Phase 4 owns first non-trivial edits)

using BaseApi.Core.Exceptions.Handlers;
using BaseApi.Core.Middleware;

var builder = WebApplication.CreateBuilder(args);

// IHttpContextAccessor — needed by future AuditInterceptor + OTel (Pitfall 1)
builder.Services.AddHttpContextAccessor();

// ProblemDetails MUST be registered BEFORE AddControllers (Pitfall 4 + 8)
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        if (ctx.HttpContext.Items.TryGetValue("CorrelationId", out var corrIdObj)
            && corrIdObj is string corrId)
        {
            ctx.ProblemDetails.Extensions["correlationId"] = corrId;
        }
        ctx.ProblemDetails.Instance = ctx.HttpContext.Request.Path;
    };
});

// IExceptionHandler chain — REGISTRATION ORDER IS LOAD-BEARING (D-06)
builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<DbUpdateExceptionHandler>();
builder.Services.AddExceptionHandler<FallbackExceptionHandler>();

builder.Services.AddControllers();

var app = builder.Build();

// PIPELINE ORDER (D-01): UseExceptionHandler FIRST (Microsoft Learn canonical order)
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRouting();
app.MapControllers();

app.Run();

// Phase 1 marker — required for WebApplicationFactory<Program> in Plan 04-02
public partial class Program { }
```

### WebApplicationFactory Test Seam (Plan 04-02)

```csharp
// Source: D-14 + Microsoft Learn Integration testing docs + Phase 1 D-11
// File: tests/BaseApi.Tests/Middleware/WebAppFactory.cs

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Tests.Middleware;

public sealed class WebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public WebAppFactory(string connectionString) => _connectionString = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // Register test controller assembly so [ApiController] in test project is discovered
            services.AddControllers()
                .AddApplicationPart(typeof(WebAppFactory).Assembly);

            // If Plan 04-02 SqlStateMappingTests / ConcurrencyTokenTests need a real DbContext,
            // register AppDbContext-shaped DbContext here pointing at _connectionString.
            // (Phase 8 owns the real AppDbContext — Plan 04-02 uses a TestDbContext analog.)
        });
    }
}
```

### TestController for verification (Plan 04-02)

```csharp
// Source: D-14 + REQ ERROR-03/06/07/10
// File: tests/BaseApi.Tests/Endpoints/TestController.cs

using System.ComponentModel.DataAnnotations;
using BaseApi.Core.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;

namespace BaseApi.Tests.Endpoints;

[ApiController]
[Route("test")]
public sealed class TestController : ControllerBase
{
    [HttpGet("not-found")]
    public IActionResult NotFound_Throws() =>
        throw new NotFoundException("Schema", Guid.NewGuid());

    [HttpGet("unhandled")]
    public IActionResult Unhandled() =>
        throw new InvalidOperationException("This message should NOT leak to the client body");

    public sealed class ValidationDto
    {
        [Required] public string? Name { get; set; }
    }

    [HttpPost("validation-error-via-fv")]
    public IActionResult FluentValidationThrows()
    {
        var failures = new[] { new ValidationFailure("Version", "Version must be SemVer.") };
        throw new ValidationException(failures);
    }

    [HttpPost("validation-error-via-modelbinding")]
    public IActionResult ModelBindingTrigger([FromBody] ValidationDto dto) => Ok();

    // SqlState + Concurrency endpoints inject a TestDbContext per WebAppFactory wiring
    // — see Plan 04-02 for full shape.
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `UseExceptionHandler(builder => builder.Run(async ctx => { ... }))` lambda | `services.AddExceptionHandler<T>()` + `IExceptionHandler` typed handlers | .NET 8 release (Nov 2023) | Cleaner type-per-concern; chainable; testable in isolation. Lambda still works for back-compat but Microsoft Learn calls IExceptionHandler the modern pattern |
| `FluentValidation.AspNetCore` auto-validation | Manual `IValidator<T>.ValidateAsync` invocation from service layer (or implicit via FluentValidation 12 + `ValidationException` mapping) | FluentValidation 12.0 (2024) | Phase 4 ships only the ValidationException mapper; Phase 6 wires the validators. Old auto-pipeline is REMOVED in v12 |
| `[Required]` DataAnnotations + ModelState | `[ApiController]` + `InvalidModelStateResponseFactory` + AddProblemDetails (defaults route through ProblemDetails) | ASP.NET Core 7/8 | Phase 4 doesn't enable DataAnnotations validators; relies on FluentValidation. But the model-binding 400 path (e.g., malformed JSON, missing required field on a binding contract) still goes through InvalidModelStateResponseFactory |
| Custom `IProblemDetailsFactory` per project | `AddProblemDetails(opts => opts.CustomizeProblemDetails = ...)` | ASP.NET Core 8 release (Nov 2023) | Single callback for cross-cutting extensions instead of subclassing a factory |
| `Activity.Current?.TraceIdentifier` for correlation | `HttpContext.Items["CorrelationId"]` + log scope (Phase 4) → adds `Activity.AddTag("correlation.id", ...)` in Phase 5 | OpenTelemetry stable adoption | Phase 4's `BeginScope` approach gives W3C-compatible correlation immediately; Phase 5 layers OTel `Activity` tagging |

**Deprecated/outdated:**
- `FluentValidation.AspNetCore` package — REMOVED in v12; never reference (PROJECT.md Out of Scope locks this)
- Old `services.AddMvc().AddFluentValidation(...)` extension — REMOVED in v12
- `[ApiBehaviorOptions].SuppressModelStateInvalidFilter = true` then custom validation — Phase 4 doesn't suppress (the default + AddProblemDetails is the modern path per D-11)

## Constraint Name Regex Validation (D-08 / ERROR-11)

CONTEXT.md D-08 proposes:
- FK: `^fk_[a-z0-9]+_(?<col>[a-z0-9_]+?)(_id)?$`
- UQ: `^uq_[a-z0-9]+_(?<col>[a-z0-9_]+)$`

### Validation against REQUIREMENTS.md ERROR-11 examples

REQUIREMENTS.md ERROR-11 cites two example constraint names:
- `fk_processor_input_schema_id`
- `uq_processor_source_hash`

| Test case | FK regex result | Captured `col` | Expected `col` | Pass/Fail |
|-----------|----------------|----------------|----------------|-----------|
| `fk_processor_input_schema_id` | Matches | `input_schema` | `input_schema_id` (ambiguous — column name in entity is `InputSchemaId` per VALID-11) | The regex strips `_id`; planner must decide whether the API surface uses `input_schema` or `input_schema_id` |
| `fk_processor_output_schema_id` | Matches | `output_schema` | (same ambiguity) | (same) |
| `uq_processor_source_hash` | UQ regex matches | `source_hash` | `source_hash` (matches column name; entity property is `SourceHash` per ENTITY-04) | PASS |
| `fk_assignment_step_id` (Phase 8 ENTITY-07) | Matches | `step` | `step_id` or `step` | Ambiguous |
| `fk_assignment_schema_id` (Phase 8 ENTITY-07) | Matches | `schema` | `schema_id` or `schema` | Ambiguous |

### Issue identified

The FK regex strips `_id` from the column capture. This is intentional per D-08 ("not all FKs end in `_id` — some FK columns reference natural keys") — but it makes the API response inconsistent with the actual column name. A client receiving `{"detail": "Foreign key violation: step references a non-existent record.", "field": "step"}` may be confused because the DTO field is named `StepId`.

### Recommendation

The planner has two valid choices, BOTH consistent with the locked decisions:

**Option A (preserve `_id` suffix for clarity):** Adjust the regex to NOT strip `_id`:
```regex
^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$
```
With this regex, `fk_processor_input_schema_id` captures `col = "input_schema_id"`. This matches the EF/snake_case column name exactly (EF property `InputSchemaId` → snake_case column `input_schema_id`). The `detail` message reads: `"Foreign key violation: input_schema_id references a non-existent record."` — directly references the column the user sent.

**Option B (strip `_id` per D-08 verbatim):** Keep CONTEXT.md D-08's regex but document in the unit tests and the `detail` message that the captured name is the "logical FK target" (e.g., `input_schema` = the Schema entity that's missing), not the column name. Less direct but readable.

**RECOMMENDED: Option A.** Reasons:
1. The DTO field IS `InputSchemaId` (camelCase: `inputSchemaId`) — client reading `"field": "input_schema_id"` immediately maps to `inputSchemaId` after snake_case ↔ camelCase translation.
2. Option B's "logical target" is confusing — `input_schema` could mean "the Schema entity referenced via input_schema_id" but also reads like a generic name.
3. Phase 8's actual column names per Phase 3 PERSIST-05 snake_case naming convention all end in `_id` for FK columns (EF naming convention for `InputSchemaId` → `input_schema_id`). The "natural key" edge case D-08 cites does not arise for any Phase 8 entity (all FKs target Guid Id columns per PERSIST-06).

If the planner chooses Option A, document the deviation from D-08's exact regex in Plan 04-01 task notes. D-08's regex shape is a STARTING POINT per CONTEXT.md Claude's Discretion line 193 — adjustment is explicitly authorized.

### Unit test coverage (Plan 04-02)

```csharp
// File: tests/BaseApi.Tests/Middleware/PostgresExceptionMapperTests.cs
// (Pure unit tests — no Postgres required; uses Npgsql.PostgresException constructor or fakes a DbUpdateException with InnerException)

[Theory]
[InlineData("fk_processor_input_schema_id",  "input_schema_id")]  // Option A
[InlineData("fk_processor_output_schema_id", "output_schema_id")]
[InlineData("fk_assignment_step_id",         "step_id")]
[InlineData("fk_assignment_schema_id",       "schema_id")]
[InlineData("uq_processor_source_hash",      "source_hash")]
public void Extracts_column_name_from_constraint(string constraint, string expectedCol) { /* ... */ }

[Fact]
public void Returns_false_when_inner_is_not_PostgresException() { /* ... */ }

[Fact]
public void Returns_false_for_unknown_SQLSTATE() { /* ... */ }
```

## Validation Architecture

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xUnit v3 3.2.2 with `Microsoft.Testing.Platform` runner (Phase 1 D-11 / Plan 01-03 deviations) |
| Config file | None — xUnit v3 conventions; `tests/BaseApi.Tests/BaseApi.Tests.csproj` carries `<OutputType>Exe</OutputType>`, `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>`, `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>` |
| Quick run command | `dotnet test tests/BaseApi.Tests/ --filter "FullyQualifiedName~Middleware"` (runs only Plan 04-02 fact tests) |
| Full suite command | `dotnet test tests/BaseApi.Tests/` (runs Plan 01-03 sanity + Plan 03-02 persistence + Plan 04-02 middleware) |
| Phase gate command | `dotnet test tests/BaseApi.Tests/` green = exit 0 + Passed: ≥14, Failed: 0 (7 from Phase 3 + ≥7 new in Plan 04-02) |

### Phase Requirements → Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|--------------|
| OBSERV-09 | `CorrelationIdMiddleware` reads X-Correlation-Id header if present, generates UUID if missing | integration (WebApplicationFactory + test endpoint) | `dotnet test tests/BaseApi.Tests/ --filter "CorrelationIdTests"` | ❌ Plan 04-02 creates `CorrelationIdTests.cs` |
| OBSERV-10 | Correlation ID in `HttpContext.Items["CorrelationId"]` + log scope | integration (assert on response header — the log scope is asserted indirectly via Plan 04-02's `ProblemDetailsExtensionsTests.cs` which checks `correlationId` in every problem details body — that extension is sourced from `HttpContext.Items["CorrelationId"]` per D-04) | (same) | ❌ Plan 04-02 |
| OBSERV-11 | Correlation ID echoed on every response (2xx, 4xx, 5xx) | integration | `dotnet test ... --filter "CorrelationIdTests"` | ❌ Plan 04-02 |
| ERROR-01 | RFC 7807 ProblemDetails JSON on every non-2xx response | integration (check `Content-Type: application/problem+json` on multiple endpoints) | (multiple test classes) | ❌ Plan 04-02 |
| ERROR-02 | `IExceptionHandler` registered via `AddExceptionHandler<T>` + `AddProblemDetails` | integration (the test passes implicitly when other handler-specific tests pass) | (multiple test classes) | ❌ Plan 04-02 |
| ERROR-03 | ValidationException → 400 with field-level errors map | integration (TestController endpoint that throws ValidationException) | `dotnet test ... --filter "ValidationErrorTests"` | ❌ Plan 04-02 |
| ERROR-04 | SQLSTATE 23503 → 422 with FK column in detail | integration (real Postgres FK constraint via PostgresFixture's throwaway DB) | `dotnet test ... --filter "SqlStateMappingTests"` | ❌ Plan 04-02 |
| ERROR-05 | SQLSTATE 23505 → 409 with unique column in detail | integration (same fixture) | (same) | ❌ Plan 04-02 |
| ERROR-06 | NotFoundException → 404 with resource type + id | integration | `dotnet test ... --filter "NotFoundAndUnhandledTests"` | ❌ Plan 04-02 |
| ERROR-07 | Unhandled exception → 500 generic message; stack in logs only | integration (assert response body does NOT contain "InvalidOperation" and assert ILogger received the exception — can use a captured `ITestOutputHelper` adapter or in-memory log provider) | (same) | ❌ Plan 04-02 |
| ERROR-08 | `correlationId` field on every Problem Details body | integration (property-based: hit every test endpoint, assert `correlationId` extension exists and matches response header) | `dotnet test ... --filter "ProblemDetailsExtensionsTests"` | ❌ Plan 04-02 |
| ERROR-09 | `instance` field on every Problem Details body | integration (same as ERROR-08, also asserts `instance == request.path`) | (same) | ❌ Plan 04-02 |
| ERROR-10 | `[ApiController]` model-binding 400 emits same ProblemDetails shape | integration (TestController endpoint with `[Required]` DTO + malformed body) | `dotnet test ... --filter "ValidationErrorTests"` | ❌ Plan 04-02 |
| ERROR-11 | Constraint name convention enables friendly field name extraction | unit + integration (unit on PostgresExceptionMapper regex with FK/UQ name table; integration via SqlStateMappingTests round-trip through real Postgres) | `dotnet test ... --filter "PostgresExceptionMapperTests"` + `SqlStateMappingTests` | ❌ Plan 04-02 |
| D-03a (Phase 3 carry-forward) | `DbUpdateConcurrencyException` → 409 generic message; no xmin leak | integration (two PUTs racing on same row via PostgresFixture) | `dotnet test ... --filter "ConcurrencyTokenTests"` | ❌ Plan 04-02 |

### Sampling Rate

- **Per task commit:** `dotnet test tests/BaseApi.Tests/ --filter "FullyQualifiedName~Middleware"` (runs ONLY new Plan 04-02 fact tests — ~7 tests, < 30 seconds against running Postgres)
- **Per plan merge:** `dotnet test tests/BaseApi.Tests/` (full suite — Phase 3's 7 tests + Plan 04-02's ≥7 tests = ≥14 total, < 60 seconds)
- **Phase gate:** Full suite green AND Plan 04-02 SUMMARY documents SC#1-5 + D-03a + ERROR-08/09 regression coverage GREEN before `/gsd-verify-work`

### Wave 0 Gaps

- [ ] `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` — `WebApplicationFactory<Program>` wrapper with test-controller assembly part registration. Required for ALL Plan 04-02 fact tests.
- [ ] `tests/BaseApi.Tests/Middleware/PostgresFixture.cs` — per-class throwaway DB pattern lifted from `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` (Phase 3). Required for `SqlStateMappingTests` + `ConcurrencyTokenTests`. May be a `// using BaseApi.Tests.Persistence;` alias if duplication is unwanted; D-14 prescribes a separate file under Middleware/ to keep test layout aligned with the SUT layout.
- [ ] `tests/BaseApi.Tests/Endpoints/TestController.cs` — controller hosting the deliberately-throwing endpoints (`GET /test/not-found`, `POST /test/validation-error-via-fv`, `POST /test/validation-error-via-modelbinding`, `POST /test/fk-violation`, `POST /test/unique-violation`, `POST /test/concurrency`, `GET /test/unhandled`).
- [ ] Six fact-test files: `CorrelationIdTests.cs`, `ValidationErrorTests.cs`, `SqlStateMappingTests.cs`, `NotFoundAndUnhandledTests.cs`, `ConcurrencyTokenTests.cs`, `ProblemDetailsExtensionsTests.cs`.
- [ ] One unit-test file: `PostgresExceptionMapperTests.cs` — regex table-driven tests (no Postgres required; uses a faked `DbUpdateException` with `Npgsql.PostgresException` inner constructed via reflection or test helpers).
- [ ] No framework install needed — xUnit v3 + Mvc.Testing already pinned; `Microsoft.AspNetCore.Mvc.Testing` PackageReference must be added to `tests/BaseApi.Tests/BaseApi.Tests.csproj` (currently missing — see Plan 04-02 wiring task).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The `IExceptionHandler` chain falls back to framework default ProblemDetails 500 if NO handler returns true, AND that default 500 also invokes the `CustomizeProblemDetails` callback | "Summary" + Pattern 1 | If wrong: unhandled exceptions hitting the FallbackExceptionHandler would skip — but D-06 ensures FallbackExceptionHandler always claims (returns true). Risk is low. Microsoft Learn confirms fallthrough behavior. [VERIFIED via Microsoft Learn fetch — quoted: "If an exception isn't handled by any exception handler, then control falls back to the default behavior and options from the middleware."] |
| A2 | Npgsql 8.0.10 explicitly versioned in the new pin matches what Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10 brings transitively | Standard Stack table + Environment Availability | If the transitive dependency is on Npgsql 8.0.x but with different patch (e.g., 8.0.5), the explicit pin to 8.0.10 might cause a version downgrade warning OR resolve to a newer version. Plan 04-01 verification step: `dotnet list package --include-transitive` after first restore. Trivial to fix |
| A3 | `[ApiController]` default `InvalidModelStateResponseFactory` routes through `IProblemDetailsService` when `AddProblemDetails` is registered, INCLUDING when registered AFTER AddControllers (or only when registered BEFORE?) | Pattern 2 + Pitfall 4 + Pitfall 8 | The Microsoft Learn note says "custom `IProblemDetailsWriter` must be registered BEFORE" — implies custom WRITER specifically. For just `CustomizeProblemDetails`, the ordering is less critical. But Pitfall 4 recommends BEFORE AddControllers as safe default. Risk: ERROR-10 test fails because ProblemDetails customizer doesn't fire on model-binding 400. Mitigation: Plan 04-01 follows the BEFORE-AddControllers order verbatim |
| A4 | `Response.OnStarting` callbacks fire even when the IExceptionHandler chain writes the response | Pattern 3 + Code Example CorrelationIdMiddleware | If OnStarting did NOT fire on the exception path, 5xx responses would lack the X-Correlation-Id header — violating OBSERV-11 SC#1. The pattern is universally cited in community references (Steve Gordon, Trendyol, OneUptime) — VERIFIED MEDIUM confidence. Plan 04-02 CorrelationIdTests.cs MUST assert header presence on 5xx response from `/test/unhandled` endpoint |
| A5 | `ValidationProblemDetails(IDictionary<string, string[]>)` constructor preserves the dictionary AND the customizer extensions fire on it (it's a ProblemDetails subclass) | Code Example ValidationExceptionHandler | If the customizer skips ValidationProblemDetails subclass, the field-level errors map and the correlationId would both be present (constructor handles the errors map, customizer handles the extension). Risk low — `ValidationProblemDetails : ProblemDetails` per ASP.NET Core source. Plan 04-02 ValidationErrorTests.cs MUST assert BOTH `errors` and `correlationId` extension present |
| A6 | The proposed FK regex Option A (`^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$` preserving `_id` suffix) is preferable to CONTEXT.md D-08's exact regex (stripping `_id`) | Constraint Name Regex Validation | The planner may choose either. If wrong choice, the API response detail messages are slightly less ergonomic but still functional. Plan 04-01 documents the regex choice; Plan 04-02 unit tests cover the chosen variant |

**Two assumptions (A3, A6) are RECOMMENDATIONS the planner should re-confirm before locking — A3 via Plan 04-02 ERROR-10 integration test (PASS = customizer fired even with AFTER ordering, FAIL = must register BEFORE), A6 via planner choice.** All other assumptions are HIGH-MEDIUM confidence and verified against current docs.

## Open Questions

1. **`PostgresExceptionMapper` unit test setup — how to construct a `DbUpdateException` with `Npgsql.PostgresException` inner without a real Postgres round-trip?**
   - What we know: `Npgsql.PostgresException` is a public sealed class with no public constructor accepting raw values (verified via npgsql.org docs). Internal constructors take a `PostgreSQL`-specific wire-format object.
   - What's unclear: Whether the planner can construct a test double via reflection (set internal fields after `Activator.CreateInstance`) OR must use a real Postgres round-trip for the unit test layer.
   - Recommendation: If reflection-based construction is brittle, accept that `PostgresExceptionMapperTests.cs` is an INTEGRATION test (uses real Postgres + creates a real FK/UQ violation). The "unit test" label was a simplification; "integration test of mapper helper" is equally valid. Plan 04-02 can have only one layer of SQLSTATE mapping tests (`SqlStateMappingTests.cs`) and the planner can OMIT the separate unit-test file if the integration coverage exceeds the unit's value.

2. **Should `FallbackExceptionHandler` redact specific exception types (e.g., `OperationCanceledException` from client disconnects) before logging?**
   - What we know: D-12 says LogError unconditionally. Phase 5 OTel will surface `OperationCanceledException` from client disconnects (`ClientDisconnected`) — in .NET 8 these are NOT considered errors by default at the OTel layer.
   - What's unclear: Whether Phase 4's LogError should already filter, or wait for Phase 5 to add the cancellation-source detection logic.
   - Recommendation: Phase 4 follows D-12 verbatim (LogError unconditional). Phase 5 can add cancellation-aware filtering at the OTel exporter level. Adding filtering in FallbackExceptionHandler now would couple Phase 4 to OTel concerns prematurely (violates D-13).

3. **Does Plan 04-01 need to register a placeholder `DbContext` for the test endpoints, or does Plan 04-02 own the DbContext wiring for `/test/fk-violation` and `/test/unique-violation`?**
   - What we know: Plan 04-01 ships infrastructure only (middleware, handlers, mapper). Plan 04-02 owns verification (test controller + WebAppFactory + PostgresFixture).
   - What's unclear: Where does the `TestDbContext` for SQLSTATE tests live — production code (Plan 04-01) or test code (Plan 04-02)?
   - Recommendation: Test code only (Plan 04-02). Production `BaseApi.Service` Program.cs does NOT register any DbContext in Phase 4 (per Phase 3 D-11 — `AppDbContext` is Phase 8's deliverable). Plan 04-02's `WebAppFactory.ConfigureWebHost` registers a Phase 3-style `TestDbContext` (or reuses the existing one from `tests/BaseApi.Tests/Persistence/TestDbContext.cs`) pointing at `PostgresFixture.ConnectionString`. The test controller injects the TestDbContext, performs deliberate FK/UQ violations against `TestEntity` rows seeded by the test, and the IExceptionHandler chain catches `DbUpdateException`.

## Security Domain

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Out of scope per PROJECT.md ("auth/authz") — Phase 4 does NOT touch authentication |
| V3 Session Management | no | Out of scope per PROJECT.md |
| V4 Access Control | no | Out of scope per PROJECT.md (no users, no resources protected by authz) |
| V5 Input Validation | partial | Phase 4 ships the `ValidationException` → 400 mapper (D-10) but NO validators (Phase 6). However, `CorrelationIdMiddleware` IS-A input-validation surface — Pitfall 3 (header injection) addressed via ASCII-printable validation per D-02 |
| V6 Cryptography | no | No cryptographic operations in Phase 4 |
| V7 Error Handling and Logging | yes | D-12 (FallbackExceptionHandler) MUST NOT leak stack traces to response body (ERROR-07); D-09 (concurrency) MUST NOT leak `xmin` value (information disclosure guard); D-08 mapping MUST NOT leak full Postgres error message (only the friendly column name) |
| V8 Data Protection | partial | The fallback message "An unexpected error occurred." per D-12 is the minimum-disclosure standard. Plan 04-02 NotFoundAndUnhandledTests.cs SC verifies the response body does NOT contain "InvalidOperationException", file paths, or stack frame text |
| V14 Configuration | no | Phase 4 doesn't modify configuration handling |

### Known Threat Patterns for ASP.NET Core 8 middleware/error stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| CRLF injection via `X-Correlation-Id` header | Tampering | ASCII-printable validation per D-02 + Pitfall 3 (Plan 04-01 Task 2 CorrelationIdMiddleware) |
| Stack trace / internal type leakage via 500 response body | Information Disclosure | D-12 generic message + log-only stack (Plan 04-01 Task 5 FallbackExceptionHandler) |
| `xmin` row-version leakage via concurrency 409 response | Information Disclosure | D-09 generic message (no `xmin` in body) (Plan 04-01 Task 5 DbUpdateExceptionHandler) |
| Full Postgres error message (table/schema name leak) via 422/409 | Information Disclosure | D-08 mapper extracts only the column name from `ConstraintName` regex; `pgEx.MessageText` / `pgEx.Detail` NEVER appear in response (Plan 04-01 Task 4 PostgresExceptionMapper) |
| Verbose error responses enabling enumeration attacks | Information Disclosure | ERROR-06 `NotFoundException` returns generic 404 (not "User X not found" vs "User Y not found exists but unauthorized" distinction — irrelevant in Phase 4 since no auth, but the pattern shape is correct for Phase 7 introduction of authz if it ever lands) |
| Correlation ID guessability / IDOR-via-correlation-id | Spoofing | `Guid.NewGuid().ToString("N")` is 128 bits of entropy (D-02) — not guessable. Even though inbound `X-Correlation-Id` is echoed verbatim, the correlation ID is NOT used for authorization decisions (no IDOR risk) |
| Log injection via inbound `X-Correlation-Id` (e.g., fake log lines) | Tampering | ASCII-printable validation per D-02 rejects CR/LF characters — log entries with embedded newlines impossible |
| Denial of Service via huge inbound header value | DoS | Length ≤ 128 validation per D-02; ASP.NET Core's default `MaxRequestHeadersTotalSize` (32KB) already bounds total header bytes |

## Sources

### Primary (HIGH confidence)
- [Microsoft Learn — Handle errors in ASP.NET Core (aspnetcore-8.0)](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-8.0) — IExceptionHandler chain, AddProblemDetails, CustomizeProblemDetails, IProblemDetailsService.WriteAsync patterns
- [Microsoft Learn — Handle errors in ASP.NET Core APIs (aspnetcore-8.0)](https://learn.microsoft.com/en-us/aspnet/core/web-api/handle-errors?view=aspnetcore-8.0) — [ApiController] default InvalidModelStateResponseFactory + AddProblemDetails interaction
- [Microsoft Learn — ASP.NET Core Middleware](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/?view=aspnetcore-8.0) — Canonical middleware order: "1. Exception/error handling, ... 5. Routing Middleware, ..." — UseExceptionHandler FIRST
- [Npgsql PostgresException API docs](https://www.npgsql.org/doc/api/Npgsql.PostgresException.html) — Confirmed: namespace `Npgsql`, fields `SqlState` (string override), `ConstraintName`/`TableName`/`ColumnName`/`SchemaName` (string?), `MessageText` (string), `Detail` (string?)
- [FluentValidation 12 upgrade docs](https://docs.fluentvalidation.net/en/latest/upgrading-to-12.html) — Confirmed: FluentValidation.AspNetCore removed; v12 ValidationException + Errors collection shape unchanged
- [Existing codebase: BaseApi.Core.csproj line 34](C:\Users\UserL\source\repos\SK_P\src\BaseApi.Core\BaseApi.Core.csproj) — FrameworkReference Microsoft.AspNetCore.App already wired (Phase 3 D-12)
- [Existing codebase: Directory.Packages.props lines 48-89](C:\Users\UserL\source\repos\SK_P\Directory.Packages.props) — All 22 Phase 1 pins + Phase 3's TimeProvider.Testing pin in place; FluentValidation 12.1.1 + Mvc.Testing 8.0.27 already pinned
- [Existing codebase: BaseApi.Service/Program.cs](C:\Users\UserL\source\repos\SK_P\src\BaseApi.Service\Program.cs) — Confirmed current Phase 1 D-10 scaffold (27 lines); confirmed `public partial class Program { }` marker on line 26 (Phase 1 D-13 carry-forward)
- [Existing codebase: tests/BaseApi.Tests/Persistence/PostgresFixture.cs](C:\Users\UserL\source\repos\SK_P\tests\BaseApi.Tests\Persistence\PostgresFixture.cs) — Verified per-class throwaway DB lifecycle pattern (Phase 3 D-15) for Plan 04-02 reuse

### Secondary (MEDIUM confidence)
- [okyrylchuk.dev — Handling Exceptions with IExceptionHandler in ASP.NET Core 8](https://okyrylchuk.dev/blog/handling-exceptions-in-asp-net-core-8/) — Chain registration order + ValueTask<bool> semantics
- [Milan Jovanović — Global Error Handling in ASP.NET Core 8](https://www.milanjovanovic.tech/blog/global-error-handling-in-aspnetcore-8) — Modern IExceptionHandler pattern walkthrough
- [Trendyol/CorrelationId GitHub](https://github.com/Trendyol/CorrelationId) — Reference implementation of Response.OnStarting pattern for correlation header echo
- [Steve Gordon — ASP.NET Core Correlation IDs](https://www.stevejgordon.co.uk/asp-net-core-correlation-ids) — Original community guidance on correlation middleware shape
- [OneUptime — Correlation ID Tracing in ASP.NET Core](https://oneuptime.com/blog/post/2026-01-25-correlation-id-tracing-aspnet-core/view) — Recent (2026) reaffirmation of OnStarting + BeginScope pattern
- [Code Maze — How to Use IExceptionHandler to Handle Exceptions in .NET](https://code-maze.com/dotnet-use-iexceptionhandler-to-handle-exceptions/) — Confirms chain semantics

### Tertiary (LOW confidence — corroborative only)
- [Anthony Giretti — ASP.NET Core 8: Improved exception handling with IExceptionHandler](https://anthonygiretti.com/2023/06/14/asp-net-core-8-improved-exception-handling-with-iexceptionhandler/) — Early release notes (June 2023); cross-references Microsoft official pattern

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all versions pinned in existing Directory.Packages.props (Phase 1 D-05), all types confirmed in existing csproj structure (Phase 3 D-12), no new package install required beyond two `<PackageReference>` adds
- Architecture: HIGH — middleware order verified verbatim against Microsoft Learn canonical pipeline (UseExceptionHandler first); IExceptionHandler chain semantics confirmed via WebFetch of MS Learn docs; CustomizeProblemDetails customizer fires for all paths confirmed via WebFetch
- Pitfalls: HIGH-MEDIUM — Pitfalls 1, 4, 6, 7, 8 verified via Microsoft Learn docs; Pitfalls 2, 3, 5 verified via community reference implementations + ASP.NET Core source behavior
- Verification strategy: HIGH — lifts proven Phase 3 D-15 throwaway-DB pattern; reuses xUnit v3 + Mvc.Testing already pinned
- Constraint name regex (D-08): MEDIUM — Option A vs Option B is a planner choice; both are functionally correct; recommendation favors Option A but D-08 verbatim is also valid

**Research date:** 2026-05-27
**Valid until:** 2026-06-26 (30 days — stable .NET 8 LTS, no minor version drift expected before Phase 4 ships)
