# Phase 4: Cross-Cutting Middleware + Error Handling - Pattern Map

**Mapped:** 2026-05-27
**Files analyzed:** 19 (8 new src/, 1 modified Program.cs, 2 modified csproj/props, 10 new tests/)
**Analogs found:** 17 / 19 (2 files — `WebAppFactory.cs` and `TestController.cs` — have no in-repo analog; fall back to RESEARCH.md Code Examples)

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` | middleware (ASP.NET pipeline) | request-response | `src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs` (sealed Core type with DI ctor, file-scoped ns, XML doc) | role-partial (interceptor not middleware, but same Core layout shape) |
| `src/BaseApi.Core/Exceptions/NotFoundException.cs` | exception type (domain) | none (data carrier) | `src/BaseApi.Core/Entities/BaseEntity.cs` (Core layout, file-scoped ns, public props) | role-partial (entity-shaped POCO) |
| `src/BaseApi.Core/Exceptions/Handlers/NotFoundExceptionHandler.cs` | service (IExceptionHandler) | request-response (catch) | `src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs` (sealed Core handler with single-method override) | role-partial |
| `src/BaseApi.Core/Exceptions/Handlers/ValidationExceptionHandler.cs` | service (IExceptionHandler) | request-response | (same as above) | role-partial |
| `src/BaseApi.Core/Exceptions/Handlers/DbUpdateExceptionHandler.cs` | service (IExceptionHandler) | request-response | (same as above) | role-partial |
| `src/BaseApi.Core/Exceptions/Handlers/FallbackExceptionHandler.cs` | service (IExceptionHandler) | request-response | (same as above) | role-partial |
| `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs` | utility (static helper) | transform | `src/BaseApi.Core/Persistence/Repositories/Repository.cs` (Persistence subtree, sealed, EF + Npgsql consumer) | role-partial (utility not repo) |
| `src/BaseApi.Service/Program.cs` | composition root | DI + middleware wiring | self (Phase 1 scaffold) | exact (edit-in-place) |
| `Directory.Packages.props` | config (CPM pin table) | none | self (existing 22-pin table) | exact (add 2 new `<PackageVersion>` lines) |
| `src/BaseApi.Core/BaseApi.Core.csproj` | config (project file) | none | self (existing 5-PackageReference layout) | exact (add 2 new `<PackageReference>` lines) |
| `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` | test fixture (WebApplicationFactory<Program> seam) | request-response | RESEARCH.md Code Example only — NO in-repo analog | none (use RESEARCH.md template) |
| `tests/BaseApi.Tests/Middleware/PostgresFixture.cs` | test fixture (IAsyncLifetime throwaway DB) | file-I/O (DB lifecycle) | `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` (Phase 3 03-02) | exact (verbatim lift; only namespace changes) |
| `tests/BaseApi.Tests/Endpoints/TestController.cs` | controller (`[ApiController]` test endpoints) | request-response | RESEARCH.md Code Example only — NO in-repo analog | none (use RESEARCH.md template) |
| `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` | test (integration via WebAppFactory) | request-response | `tests/BaseApi.Tests/Persistence/SchemaTests.cs` (IClassFixture + xUnit v3 `TestContext.Current.CancellationToken` cadence) | role-match |
| `tests/BaseApi.Tests/Middleware/ValidationErrorTests.cs` | test (integration) | request-response | (same) | role-match |
| `tests/BaseApi.Tests/Middleware/SqlStateMappingTests.cs` | test (integration, real Postgres) | request-response + DB | `tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs` (IClassFixture<PostgresFixture> + real PG round-trip + `EnsureCreatedAsync`) | exact (same fixture pattern) |
| `tests/BaseApi.Tests/Middleware/NotFoundAndUnhandledTests.cs` | test (integration) | request-response | `tests/BaseApi.Tests/Persistence/SchemaTests.cs` | role-match |
| `tests/BaseApi.Tests/Middleware/ConcurrencyTokenTests.cs` | test (integration, real Postgres) | request-response + DB | `tests/BaseApi.Tests/Persistence/XminConcurrencyTokenTests.cs` (same IClassFixture<PostgresFixture> shape; same xmin concern) | exact |
| `tests/BaseApi.Tests/Middleware/ProblemDetailsExtensionsTests.cs` | test (integration regression) | request-response | `tests/BaseApi.Tests/Persistence/SchemaTests.cs` | role-match |
| `tests/BaseApi.Tests/Persistence/PostgresExceptionMapperTests.cs` | test (unit — pure static mapper) | transform | `tests/BaseApi.Tests/Persistence/RepositorySurfaceTests.cs` (sealed class, no fixture, reflection-style assertions) | role-match (pure unit, no fixture) |

---

## Pattern Assignments

### `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` (middleware, request-response)

**Analog:** `src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs` (closest Core sealed type with DI ctor + Async invocation + XML docs)

**File-scoped namespace + outside-namespace usings pattern** (from AuditInterceptor.cs lines 1-7):
```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using BaseApi.Core.Entities;

namespace BaseApi.Core.Persistence.Interceptors;
```
Apply same shape: usings before namespace, file-scoped `namespace BaseApi.Core.Middleware;`.

**Sealed-class + DI constructor pattern** (AuditInterceptor.cs lines 38-47):
```csharp
public sealed class AuditInterceptor : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TimeProvider _clock;

    public AuditInterceptor(IHttpContextAccessor httpContextAccessor, TimeProvider clock)
    {
        _httpContextAccessor = httpContextAccessor;
        _clock = clock;
    }
```
Apply same shape: `public sealed class CorrelationIdMiddleware` with `RequestDelegate _next` + `ILogger<CorrelationIdMiddleware> _logger` private readonly fields, prefix `_` per .editorconfig lines 134-136 (`camel_case_with_underscore_prefix`).

**XML doc shape with `<para>` paragraph blocks** (AuditInterceptor.cs lines 9-37): copy this style for CorrelationIdMiddleware — `<summary>` + multiple `<para>` blocks for behavior contracts, pitfall callouts, and decision references (D-02, D-03).

**Concrete body** — pull from RESEARCH.md "Code Examples → CorrelationIdMiddleware" (lines 606-674); the 4-step procedure (read/generate → `HttpContext.Items` → `Response.OnStarting` → `BeginScope` + `await _next`) is the locked D-03 shape.

---

### `src/BaseApi.Core/Exceptions/NotFoundException.cs` (exception type)

**Analog:** `src/BaseApi.Core/Entities/BaseEntity.cs` (closest Core POCO-shaped type with file-scoped ns + XML doc + public props)

**Shape pattern** (BaseEntity.cs lines 1-23):
```csharp
namespace BaseApi.Core.Entities;

/// <summary>
/// Abstract base for all audit-stamped domain entities.
/// ...
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; }
    ...
}
```
Apply: file-scoped `namespace BaseApi.Core.Exceptions;`, XML `<summary>` referencing D-07, `public sealed class NotFoundException : Exception` with `ResourceType` (string, get-only) + `Id` (object, get-only) props. Constructor calls base with formatted message per D-07.

**Concrete body** — RESEARCH.md "Code Examples → NotFoundException" (lines 679-697) is the verbatim D-07 shape; copy directly.

---

### `src/BaseApi.Core/Exceptions/Handlers/{NotFound,Validation,DbUpdate,Fallback}ExceptionHandler.cs` (4 handlers)

**Analog:** `src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs` (sealed Core type, DI ctor with single dependency, single async override)

**Common pattern across all 4 handlers** — apply this skeleton derived from AuditInterceptor.cs lines 38-89 + RESEARCH.md Pattern 1:

```csharp
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
// + handler-specific (e.g., BaseApi.Core.Exceptions; FluentValidation; etc.)

namespace BaseApi.Core.Exceptions.Handlers;

public sealed class XxxExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _pdSvc;
    // optionally: private readonly ILogger<XxxExceptionHandler> _logger;

    public XxxExceptionHandler(IProblemDetailsService pdSvc) => _pdSvc = pdSvc;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not XxxException xx) return false;   // Pitfall 6: bail FAST, no side effects

        httpContext.Response.StatusCode = StatusCodes.Status___;
        var problem = new ProblemDetails { Status = ..., Title = ..., Detail = ... };
        // (optionally) problem.Extensions["foo"] = ...

        return await _pdSvc.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
```

**Per-handler concrete bodies** (from RESEARCH.md Code Examples — load-bearing handler-specific logic):

- **NotFoundExceptionHandler**: RESEARCH.md lines 309-345 — extensions `["resourceType"] = nfx.ResourceType` + `["resourceId"] = nfx.Id`; status 404 / title "Not Found"; detail from `nfx.Message`.
- **ValidationExceptionHandler**: RESEARCH.md lines 771-815 — `vex.Errors.GroupBy(e => e.PropertyName).ToDictionary(...)`, then `new ValidationProblemDetails(errors)`; status 400 / title "Validation failed". D-10: covers both FluentValidation.ValidationException AND model-binding via `[ApiController]` (no separate handler — the default `InvalidModelStateResponseFactory` rides on `AddProblemDetails` per D-11).
- **DbUpdateExceptionHandler**: RESEARCH.md lines 819-883 — **CRITICAL ORDER (Pitfall 7)**: `if (ex is not DbUpdateException due) return false;` then `if (ex is DbUpdateConcurrencyException) { /* 409 generic */ return true; }` BEFORE `if (PostgresExceptionMapper.TryMap(...))`. Concurrency body has NO `xmin` leak (D-09).
- **FallbackExceptionHandler**: per D-12 — `_logger.LogError(exception, "Unhandled exception on {Path}", httpContext.Request.Path);` then status 500 / title "Internal Server Error" / detail "An unexpected error occurred." Body NEVER carries exception type/message/stack. Inject `ILogger<FallbackExceptionHandler>` alongside `IProblemDetailsService`.

**Sealed + private readonly + underscore prefix** — confirmed style requirement from .editorconfig line 134 (`private_fields_should_be_camel_case_with_underscore`) — match AuditInterceptor.cs lines 40-41.

---

### `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs` (utility, static helper)

**Analog:** `src/BaseApi.Core/Persistence/Repositories/Repository.cs` (closest Persistence-subtree sealed type consuming EF Core + Npgsql)

**Subtree layout pattern** (Repository.cs lines 1-6):
```csharp
using Microsoft.EntityFrameworkCore;
using BaseApi.Core.Entities;
using BaseApi.Core.Persistence;

namespace BaseApi.Core.Persistence.Repositories;
```
Apply: file lands at `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs`; namespace `BaseApi.Core.Persistence.Exceptions;` (matches folder layout — Phase 1 D-10 implicit-create-on-first-file rule).

**Static class shape** — Repository.cs is `sealed`, not `static`; PostgresExceptionMapper is a pure helper with no state, so use `public static class`. Pattern from RESEARCH.md "Code Examples → PostgresExceptionMapper" (lines 700-767) — copy verbatim. Key choices:

- `System.Text.RegularExpressions.Regex` with `RegexOptions.Compiled` (precomputed singletons at class level).
- `TryMap` signature: `public static bool TryMap(DbUpdateException ex, out int httpStatus, out string detail, out string? columnName)`.
- SQLSTATE switch: `"23503"` → 422 + FK regex; `"23505"` → 409 + UQ regex; default → `return false`.
- `ExtractColumn` private static helper for shared `regex.Match(constraint).Groups["col"].Value` extraction.

**Regex choice (planner discretion per CONTEXT D-08 + RESEARCH lines 1031-1072)**: Option A (`^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$` — preserves `_id` suffix) is RECOMMENDED for client ergonomics (DTO field `InputSchemaId` ↔ column `input_schema_id`). Document choice in plan task notes.

**Using `Npgsql;` namespace** — confirms why D-10/D-14 require explicit `<PackageReference Include="Npgsql" />` in BaseApi.Core.csproj (transitive-only resolution is brittle when type is named verbatim in source).

---

### `src/BaseApi.Service/Program.cs` (composition root — EDIT)

**Analog:** SELF — Phase 1 current scaffold is the baseline for edit instructions.

**Current content (Program.cs lines 1-26 verbatim — what edits MUST preserve / replace):**
```csharp
// BaseApi.Service — application entry point.
//
// Phase 1 scaffold per CONTEXT.md D-10. The host boots, registers controllers
// (none exist yet — Phase 8 adds the 5 concrete controllers), and runs. Every
// HTTP path returns 404 until later phases register routes.
//
// Phase 7 will replace the body with:
//   builder.Services.AddBaseApi<AppDbContext>(builder.Configuration);
//   app.UseBaseApi();
//   app.MapControllers();
// (See .planning/research/ARCHITECTURE.md Pattern 1 — Composition Root.)

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();

// Marker type for WebApplicationFactory<Program> in Phase 8 integration tests.
// (Top-level statements generate an internal Program class by default; partial
// class declaration here promotes it to public so the test project can target it.)
public partial class Program { }
```

**Edit instructions** (planner translates to Phase 4 Plan 04-01 Task 6):

1. **PRESERVE**: opening file-header comment block (lines 1-11) — update Phase 7 forward-reference to acknowledge Phase 4 took the first real edits, OR leave intact as forward-looking.
2. **PRESERVE**: `public partial class Program { }` marker at end (lines 23-26) — load-bearing for WebApplicationFactory<Program> in Plan 04-02 (and Phase 8). Comment text may stay verbatim.
3. **ADD before `var app = builder.Build();`** (after current line 15):
   - `using BaseApi.Core.Exceptions.Handlers;` and `using BaseApi.Core.Middleware;` at TOP of file (above `var builder =` — Top-level statement file, usings go FIRST per C# spec).
   - `builder.Services.AddHttpContextAccessor();` (Pitfall 1)
   - `builder.Services.AddProblemDetails(opts => opts.CustomizeProblemDetails = ctx => { ... });` (D-04) — BEFORE `AddControllers()` (Pitfall 4/8)
   - 4× `builder.Services.AddExceptionHandler<XxxHandler>();` in D-06 order: NotFound → Validation → DbUpdate → Fallback.
   - Existing `builder.Services.AddControllers();` stays AFTER ProblemDetails + ExceptionHandler registrations.
4. **ADD between `var app = builder.Build();` and `app.MapControllers();`** (after current line 17, before line 19):
   - `app.UseExceptionHandler();` (D-01 step 1)
   - `app.UseMiddleware<CorrelationIdMiddleware>();` (D-01 step 2)
   - `app.UseRouting();` (D-01 step 3 — explicit before MapControllers for clarity, per CONTEXT lines 256-272 illustrative layout)
5. Existing `app.MapControllers();` and `app.Run();` stay as-is.

**Target shape after edit** — RESEARCH.md "Code Examples → Program.cs Wiring" (lines 887-933) is the verbatim target.

---

### `Directory.Packages.props` (config — EDIT)

**Analog:** SELF — existing 22-pin table is the layout baseline.

**Current content** (Directory.Packages.props lines 57-59 — Validation section):
```xml
<!-- Validation -->
<PackageVersion Include="FluentValidation" Version="12.1.1" />
<PackageVersion Include="FluentValidation.DependencyInjectionExtensions" Version="12.1.1" />
```
`FluentValidation` 12.1.1 IS ALREADY PINNED. Plan 04-01 does NOT need to add it to this file — only needs to add `<PackageReference Include="FluentValidation" />` to BaseApi.Core.csproj (no Version= per CPM contract D-05/D-06).

**Current content** (Directory.Packages.props line 51 — EF Core section):
```xml
<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="8.0.10" />
```
`Npgsql` standalone is NOT YET PINNED. Add this NEW line alongside the EF section (after line 51 or grouped logically):
```xml
<PackageVersion Include="Npgsql" Version="8.0.10" />
```
Version `8.0.10` matches Npgsql.EFCore.PostgreSQL 8.0.10's transitive — verified by RESEARCH.md Standard Stack table line 128 + Environment Availability line 443 (assumption A2).

**Edit rule** — preserve all comment blocks (lines 1-44) intact. Preserve all 22 existing pins. Add ONE new `<PackageVersion>` line for `Npgsql` 8.0.10. FluentValidation requires NO change here.

---

### `src/BaseApi.Core/BaseApi.Core.csproj` (config — EDIT)

**Analog:** SELF — existing 5-PackageReference layout (lines 30-44) is the pattern.

**Current content** (BaseApi.Core.csproj lines 37-44):
```xml
<ItemGroup>
  <!-- EF Core 8 + Npgsql + snake_case convention (Phase 3 CONTEXT.md D-12).
       Versions resolve from Directory.Packages.props (CPM — no Version= per Phase 1 D-05/D-06). -->
  <PackageReference Include="Microsoft.EntityFrameworkCore" />
  <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
  <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
  <PackageReference Include="EFCore.NamingConventions" />
</ItemGroup>
```

**Edit instructions:**
1. Add `<PackageReference Include="Npgsql" />` inside the existing EF Core `<ItemGroup>` (or a new sibling group). NO `Version=` attribute (CPM contract).
2. Add a new `<ItemGroup>` for Validation:
   ```xml
   <ItemGroup>
     <!-- FluentValidation 12 — Phase 4 ships ValidationException → 400 mapping defensively
          (D-10); Phase 6 wires validators. Version pinned in Directory.Packages.props. -->
     <PackageReference Include="FluentValidation" />
   </ItemGroup>
   ```
3. PRESERVE the existing FrameworkReference ItemGroup (lines 30-35) and PropertyGroup (lines 23-28) verbatim.
4. PRESERVE the file-header comment block (lines 3-21) — optionally update to reference Phase 4 (D-10) alongside the existing Phase 3 D-12 callout.

---

### `tests/BaseApi.Tests/Middleware/PostgresFixture.cs` (test fixture — verbatim lift)

**Analog:** `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` (Phase 3 03-02 — EXACT MATCH)

**Verbatim source body** (Persistence/PostgresFixture.cs lines 1-56 — copy entire file content):
```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BaseApi.Tests.Persistence;

/// <summary>
/// Per-test-class fixture: creates a throwaway logical database inside the running
/// Phase 2 Postgres container (localhost:5433), runs EnsureCreatedAsync against it,
/// and DROPs it on dispose. xUnit v3 parallelizes test CLASSES by default, so each
/// class gets an isolated DB — no name collisions.
/// ...
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public string DatabaseName { get; } = $"stepsdb_test_{Guid.NewGuid():N}";
    public string ConnectionString { get; private set; } = default!;

    private const string AdminConnectionString =
        "Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres";

    public async ValueTask InitializeAsync()
    {
        await using var adminConn = new NpgsqlConnection(AdminConnectionString);
        await adminConn.OpenAsync();
        await using var createCmd = adminConn.CreateCommand();
        createCmd.CommandText = $"CREATE DATABASE \"{DatabaseName}\"";
        await createCmd.ExecuteNonQueryAsync();

        ConnectionString =
            $"Host=localhost;Port=5433;Database={DatabaseName};Username=postgres;Password=postgres";
    }

    public async ValueTask DisposeAsync()
    {
        // Clear connection pools BEFORE the DROP — otherwise the DROP fails with
        // "database is being accessed by other users" even though tests have disposed
        // their DbContexts (Npgsql keeps pooled connections around for reuse).
        NpgsqlConnection.ClearAllPools();

        await using var adminConn = new NpgsqlConnection(AdminConnectionString);
        await adminConn.OpenAsync();
        await using var dropCmd = adminConn.CreateCommand();
        // WITH (FORCE) is PG 13+; Phase 2 uses postgres:17-alpine (D-12).
        dropCmd.CommandText = $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)";
        await dropCmd.ExecuteNonQueryAsync();
    }
}
```

**Single-edit delta for Plan 04-02:**
- Change namespace from `BaseApi.Tests.Persistence` → `BaseApi.Tests.Middleware` (D-14 directive: keep test layout aligned with SUT layout).
- Optionally bump `DatabaseName` prefix (e.g., `stepsdb_test_mw_{Guid:N}`) to make BEFORE/AFTER `psql \l` snapshot diffs easier to attribute to Plan 04-02 vs Phase 3 leak.
- All connection-pool clearing + `DROP DATABASE WITH FORCE` discipline (D-15) MUST be preserved exactly — the BEFORE/AFTER `psql \l` snapshot evidence (Phase 3 D-15) is the proof of cleanup correctness.

**Alternative:** D-14 mentions planner MAY `// using BaseApi.Tests.Persistence;` alias instead of duplicating; the verbatim file-under-Middleware layout is the stated preference.

---

### `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` (test fixture — NO in-repo analog)

**Analog:** RESEARCH.md "Code Examples → WebApplicationFactory Test Seam" (lines 937-967) — NO Phase 1/2/3 file uses `WebApplicationFactory<Program>` yet (this is Phase 4's first introduction).

**Body source** — RESEARCH.md lines 937-967 verbatim:
```csharp
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

            // For SqlStateMappingTests / ConcurrencyTokenTests: register a TestDbContext
            // (Phase 3 analog already exists at tests/BaseApi.Tests/Persistence/TestDbContext.cs)
            // pointing at _connectionString. Optional per test — those tests construct their
            // own DbContext via DbContextOptionsBuilder; this DI registration is for any
            // test endpoint in TestController.cs that requires constructor injection.
        });
    }
}
```

**Style constraints** (inherited from .editorconfig):
- `sealed` class (matches all other test classes in `tests/BaseApi.Tests/Persistence/`).
- File-scoped namespace `BaseApi.Tests.Middleware;`.
- Outside-namespace usings (line 119 of .editorconfig — `outside_namespace:warning`).
- Private field `_connectionString` with underscore prefix (line 134 of .editorconfig).

**Wiring concerns from RESEARCH.md** (planner must encode in Plan 04-02 task notes):
- `BaseApi.Tests.csproj` MUST add `<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />` (already pinned in Directory.Packages.props line 81 since Phase 1 D-13; not yet referenced in the .csproj per current line 67-72 review — required for `WebApplicationFactory<Program>` to compile).
- `ProjectReference` to `BaseApi.Service` is required if absent — Phase 1 D-13 anticipated this; current BaseApi.Tests.csproj line 81 references BaseApi.Core only. Plan 04-02 must add `<ProjectReference Include="..\..\src\BaseApi.Service\BaseApi.Service.csproj" />` so `WebApplicationFactory<Program>` resolves the `Program` partial class.

---

### `tests/BaseApi.Tests/Endpoints/TestController.cs` (controller — NO in-repo analog)

**Analog:** RESEARCH.md "Code Examples → TestController for verification" (lines 970-1014). NO existing `[ApiController]`-decorated class exists in this repo.

**Body source** — RESEARCH.md lines 970-1014 verbatim (deliberately-throwing endpoints for each handler path: `GET /test/not-found`, `GET /test/unhandled`, `POST /test/validation-error-via-fv`, `POST /test/validation-error-via-modelbinding`, plus Plan 04-02 additions for `POST /test/fk-violation`, `POST /test/unique-violation`, `POST /test/concurrency`).

**Style constraints** (inherited from .editorconfig + Phase 3 test layout):
- `sealed` class.
- File-scoped namespace `BaseApi.Tests.Endpoints;`.
- `[ApiController]` + `[Route("test")]`.
- Nested DTO classes inside the controller (e.g., `public sealed class ValidationDto`) for endpoint-local model-binding contracts.

**Concrete extension over RESEARCH.md skeleton**: add `[HttpPost("fk-violation")]` + `[HttpPost("unique-violation")]` + `[HttpPost("concurrency")]` endpoints that inject `TestDbContext` (or a Phase 4 analog) via constructor and deliberately trigger FK/UQ violations and concurrency conflicts against `TestEntity` seeded by the test method. RESEARCH.md Open Question 3 (lines 1167-1170) recommends Test code only — the production `BaseApi.Service` Program.cs does NOT register any DbContext in Phase 4.

---

### `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` (integration test)

**Analog:** `tests/BaseApi.Tests/Persistence/SchemaTests.cs` — closest xUnit v3 + fixture cadence in the repo.

**Test class skeleton pattern** (SchemaTests.cs lines 1-22):
```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BaseApi.Tests.Persistence;

public sealed class SchemaTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public SchemaTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Test_TestDbContext_EnsureCreated_ProducesSnakeCaseSchema()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var ct = TestContext.Current.CancellationToken;
        ...
```

**Apply to CorrelationIdTests.cs:**
- `public sealed class CorrelationIdTests` — no fixture for pure correlation tests (no DB needed; just `WebAppFactory` constructed inline OR via a separate `IClassFixture<WebAppFactory>` if the cost of factory construction matters).
- xUnit v3 `var ct = TestContext.Current.CancellationToken;` — REQUIRED at top of every async test (xUnit1051 lesson from Phase 3 Plan 03-02 deviation discovery).
- `[Fact]` per test, method name pattern `Test_<Behavior>_<Outcome>` matches Phase 3 convention (SchemaTests.cs line 14: `Test_TestDbContext_EnsureCreated_ProducesSnakeCaseSchema`).
- `Assert.Contains` / `Assert.Equal` / `Assert.NotNull` / `Assert.DoesNotContain` — pattern from SchemaTests.cs lines 35-44.

**Test concerns per CONTEXT D-14 + RESEARCH ERROR-08/09/10 + OBSERV-09/10/11:**
- supplied `X-Correlation-Id` header echoed verbatim (2xx, 4xx, 5xx response paths)
- missing header generates valid 32-char hex (regex `^[a-f0-9]{32}$`)
- response header is present on `/test/not-found` (404 path), `/test/unhandled` (500 path), and a 2xx path (e.g., a stub `GET /test/ok` if added, OR rely on `[ApiController]`'s implicit OPTIONS / GET behavior)

---

### `tests/BaseApi.Tests/Middleware/SqlStateMappingTests.cs` (integration test, real PG)

**Analog:** `tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs` — closest IClassFixture<PostgresFixture> + EnsureCreatedAsync + real PG round-trip.

**Test setup pattern** (AuditInterceptorTests.cs lines 14-37):
```csharp
[Fact]
public async Task Test_AuditInterceptor_StampsUtcTimestamps_OnInsert()
{
    var ct = TestContext.Current.CancellationToken;
    var clock = new FakeTimeProvider();
    clock.SetUtcNow(new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero));

    var stub = new StubHttpContextAccessor();
    stub.SetUser("alice");

    var interceptor = new AuditInterceptor(stub, clock);

    var options = new DbContextOptionsBuilder<TestDbContext>()
        .UseNpgsql(_fixture.ConnectionString)
        .UseSnakeCaseNamingConvention()
        .AddInterceptors(interceptor)
        .Options;

    await using var db = new TestDbContext(options);
    await db.Database.EnsureCreatedAsync(ct);

    var entity = new TestEntity();
    await db.TestEntities.AddAsync(entity, ct);
    await db.SaveChangesAsync(ct);
    ...
```

**Apply to SqlStateMappingTests.cs:**
- `IClassFixture<PostgresFixture>` (the new Middleware/PostgresFixture.cs above).
- `var ct = TestContext.Current.CancellationToken;` first line of every async test.
- Construct `WebAppFactory(_fixture.ConnectionString)` to get an HttpClient.
- Seed `TestEntity` rows via a `TestDbContext` against `_fixture.ConnectionString`.
- POST to `/test/fk-violation` and `/test/unique-violation` endpoints (TestController.cs).
- Assert response status (422 / 409), `Content-Type: application/problem+json`, `detail` contains column name (per Option A regex: `input_schema_id`, `source_hash`), `Extensions["correlationId"]` present.

---

### `tests/BaseApi.Tests/Middleware/ConcurrencyTokenTests.cs` (integration test, real PG)

**Analog:** `tests/BaseApi.Tests/Persistence/XminConcurrencyTokenTests.cs` — same fixture pattern + same xmin concern (D-03a Phase 3 carry-forward).

**Skeleton pattern** (XminConcurrencyTokenTests.cs lines 1-30):
```csharp
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseApi.Tests.Persistence;

public sealed class XminConcurrencyTokenTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public XminConcurrencyTokenTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public void Test_BaseEntity_HasXminShadowProperty() { ... }
}
```

**Apply to ConcurrencyTokenTests.cs:**
- Same `IClassFixture<PostgresFixture>` shape (Middleware/PostgresFixture.cs).
- Test scenario per CONTEXT D-14 SC: two PUTs racing on the same row → second returns 409 with D-09 detail `"The resource was modified by another request; reload and retry."` and NO `xmin` value leaked.
- Mechanism: seed row, open two `TestDbContext` instances against `_fixture.ConnectionString`, load same entity in both, mutate-and-SaveChangesAsync the first (xmin advances), mutate-and-SaveChangesAsync the second (raises `DbUpdateConcurrencyException`) — assert that the API endpoint wrapping this (e.g., `POST /test/concurrency`) returns 409 + correct detail + correlationId.

---

### `tests/BaseApi.Tests/Persistence/PostgresExceptionMapperTests.cs` (unit test, pure mapper)

**Analog:** `tests/BaseApi.Tests/Persistence/RepositorySurfaceTests.cs` — closest pure-unit test (no fixture, no PG, no async).

**Skeleton pattern** (RepositorySurfaceTests.cs lines 1-29):
```csharp
using System.Linq;
using System.Reflection;
using BaseApi.Core.Persistence.Repositories;
using Xunit;

namespace BaseApi.Tests.Persistence;

public sealed class RepositorySurfaceTests
{
    [Fact]
    public void Test_IRepository_ExposesExactlyFiveMethods()
    {
        var methods = typeof(IRepository<>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .ToList();

        Assert.Equal(5, methods.Count);
        ...
    }
}
```

**Apply to PostgresExceptionMapperTests.cs:**
- `public sealed class PostgresExceptionMapperTests` — NO fixture (planner MAY relocate file to `tests/BaseApi.Tests/Middleware/` if D-14's "test layout aligned with SUT" rule applies; `src/BaseApi.Core/Persistence/Exceptions/` SUT location maps cleanly to `tests/BaseApi.Tests/Persistence/` analog).
- `[Theory]` + `[InlineData]` for regex table-driven coverage (per RESEARCH.md lines 1080-1086 verbatim test cases: `fk_processor_input_schema_id` → `input_schema_id`, `fk_processor_output_schema_id` → `output_schema_id`, `fk_assignment_step_id` → `step_id`, `fk_assignment_schema_id` → `schema_id`, `uq_processor_source_hash` → `source_hash`).
- RESEARCH.md Open Question 1 (lines 1157-1160) flags `Npgsql.PostgresException` is a public sealed class with no public constructor — planner may EITHER use reflection to construct a test double OR omit the unit-test file and rely on `SqlStateMappingTests.cs` (real PG) for full SQLSTATE coverage. Document choice in Plan 04-02 task notes.

---

## Shared Patterns

### File-scoped namespace + outside-namespace usings
**Source:** `.editorconfig` lines 76 (`csharp_style_namespace_declarations = file_scoped:warning`) + line 119 (`csharp_using_directive_placement = outside_namespace:warning`).
**Apply to:** Every new .cs file (8 src/, 10 tests/).
**Concrete shape (from AuditInterceptor.cs lines 1-7):**
```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using BaseApi.Core.Entities;

namespace BaseApi.Core.Persistence.Interceptors;

public sealed class ...
```

### Sealed types + private readonly underscore-prefix fields
**Source:** `.editorconfig` lines 134-136 + every existing sealed type (Repository.cs, AuditInterceptor.cs, all Persistence/ test classes).
**Apply to:** All 6 new src/ Exception types/handlers, all 10 new tests/ classes.
**Concrete shape (from AuditInterceptor.cs lines 38-47):**
```csharp
public sealed class XxxYyy : IInterface
{
    private readonly IService _svc;
    private readonly ILogger<XxxYyy> _logger;

    public XxxYyy(IService svc, ILogger<XxxYyy> logger)
    {
        _svc = svc;
        _logger = logger;
    }
```

### CPM contract — zero `Version=` on `<PackageReference>`
**Source:** Phase 1 D-05/D-06 (Directory.Packages.props comment lines 7-15) + every existing csproj.
**Apply to:** BaseApi.Core.csproj (2 new PackageReference adds), Directory.Packages.props (1 new PackageVersion add for `Npgsql`).
**Concrete shape (from BaseApi.Core.csproj lines 40-43):**
```xml
<PackageReference Include="Microsoft.EntityFrameworkCore" />
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
```
Versions resolve from Directory.Packages.props. Adding `Version="..."` to a `<PackageReference>` is a build-fatal CPM violation (MSBuild emits NU1008 with `<ManagePackageVersionsCentrally>true</>`).

### xUnit v3 cancellation token threading (xUnit1051)
**Source:** `tests/BaseApi.Tests/Persistence/SchemaTests.cs` line 21, `AuditInterceptorTests.cs` line 17, `DiLifetimeTests.cs` (implicit). Phase 3 Plan 03-02 deviation discovery.
**Apply to:** Every new async test method in Plan 04-02 (all integration test files: CorrelationIdTests, ValidationErrorTests, SqlStateMappingTests, NotFoundAndUnhandledTests, ConcurrencyTokenTests, ProblemDetailsExtensionsTests).
**Concrete shape (from SchemaTests.cs lines 14-21):**
```csharp
[Fact]
public async Task Test_X_Y()
{
    var ct = TestContext.Current.CancellationToken;
    ...
    await db.Database.EnsureCreatedAsync(ct);
    ...
}
```
Omitting `ct` propagation triggers xUnit1051 analyzer warning → build-fatal under `TreatWarningsAsErrors=true`.

### IClassFixture<PostgresFixture> + per-class throwaway DB (D-15)
**Source:** All Phase 3 test classes: `SchemaTests`, `AuditInterceptorTests`, `XminConcurrencyTokenTests`, `DiLifetimeTests` (lines 7-12 each).
**Apply to:** Plan 04-02's SqlStateMappingTests.cs + ConcurrencyTokenTests.cs (the two tests requiring real PG).
**Concrete shape (from SchemaTests.cs lines 7-11):**
```csharp
public sealed class SqlStateMappingTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    public SqlStateMappingTests(PostgresFixture fixture) => _fixture = fixture;
```
Fixture wraps `CREATE DATABASE stepsdb_test_{Guid:N}` → tests run → `DROP DATABASE WITH (FORCE)` → BEFORE/AFTER `psql \l` snapshot byte-identical (D-15 cleanup discipline).

### XML doc style (`<summary>` + `<para>` paragraphs + decision references)
**Source:** Every existing Core source file (BaseEntity.cs, BaseDbContext.cs, Repository.cs, IRepository.cs, AuditInterceptor.cs) and verbose test fixture (PostgresFixture.cs lines 7-21, StubHttpContextAccessor.cs lines 6-17).
**Apply to:** Every new src/ file (8 files) and every non-trivial test fixture (WebAppFactory.cs, PostgresFixture.cs, TestController.cs).
**Concrete shape (from BaseDbContext.cs lines 7-31):**
```csharp
/// <summary>
/// One-sentence what.
///
/// <para>
/// Behavior contract paragraph. Reference decisions inline (e.g., D-03).
/// </para>
///
/// <para>
/// <b>Pitfall name (Pitfall N):</b> mitigation description.
/// </para>
/// </summary>
```
Reference `<c>Type</c>` and `<see cref="..."/>` for cross-references. Inline `D-XX` and `Pitfall XX` callouts in body comments are the Phase 1/2/3 convention.

### `docs(04-XX): ...` commit prefix for evidence; `fix(04-XX): ...` for source fix-forward
**Source:** Phase 1 Plan 01-03, Phase 2 Plan 02-02, Phase 3 Plan 03-02 — all SUMMARY commits use this exact prefix (CONTEXT.md line 252).
**Apply to:** Every commit in Plan 04-02 (verification). Source defect fix-forwards in Plan 04-01 use `fix(04-01): ...`.

---

## No Analog Found

| File | Role | Data Flow | Reason | Fallback |
|------|------|-----------|--------|----------|
| `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` | test fixture (WebApplicationFactory<Program>) | request-response | Phase 4 is the first phase to consume `WebApplicationFactory<Program>` (Phase 1 D-13 anticipated this for Phase 8 but Phase 4 adopts early per D-14). NO in-repo file uses `Microsoft.AspNetCore.Mvc.Testing` yet. | RESEARCH.md Code Example lines 937-967 verbatim. |
| `tests/BaseApi.Tests/Endpoints/TestController.cs` | controller (`[ApiController]`) | request-response | NO `[ApiController]`-decorated class exists in the repo yet (Phase 7/8 ship the real controllers). Phase 4 verification ships a test-only controller in the test assembly. | RESEARCH.md Code Example lines 970-1014 verbatim. |

---

## Metadata

**Analog search scope:** `src/BaseApi.Core/**`, `src/BaseApi.Service/**`, `tests/BaseApi.Tests/**`, `.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, `**/appsettings*.json`.

**Files scanned (key analogs Read in full):**
- `src/BaseApi.Service/Program.cs` (27 lines — current scaffold)
- `src/BaseApi.Core/BaseApi.Core.csproj` (47 lines)
- `src/BaseApi.Core/Entities/BaseEntity.cs` (41 lines)
- `src/BaseApi.Core/Persistence/BaseDbContext.cs` (64 lines)
- `src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs` (90 lines)
- `src/BaseApi.Core/Persistence/Repositories/Repository.cs` (37 lines)
- `src/BaseApi.Core/Persistence/Repositories/IRepository.cs` (44 lines)
- `tests/BaseApi.Tests/BaseApi.Tests.csproj` (85 lines)
- `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` (57 lines)
- `tests/BaseApi.Tests/Persistence/SchemaTests.cs` (47 lines)
- `tests/BaseApi.Tests/Persistence/XminConcurrencyTokenTests.cs` (31 lines)
- `tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs` (70 lines)
- `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs` (34 lines)
- `tests/BaseApi.Tests/Persistence/RepositorySurfaceTests.cs` (30 lines)
- `tests/BaseApi.Tests/Persistence/TestDbContext.cs` (18 lines)
- `tests/BaseApi.Tests/Persistence/TestEntity.cs` (13 lines)
- `tests/BaseApi.Tests/Persistence/StubHttpContextAccessor.cs` (44 lines)
- `tests/BaseApi.Tests/MetaTest.cs` (24 lines)
- `Directory.Packages.props` (92 lines)
- `Directory.Build.props` (37 lines)
- `.editorconfig` (185 lines)
- `src/BaseApi.Service/appsettings.Development.json` (17 lines)
- `src/BaseApi.Service/BaseApi.Service.csproj` (39 lines)

**Project skills check:** `./CLAUDE.md` does NOT exist. `.claude/skills/` does NOT exist. `.agents/skills/` does NOT exist. No project-skill rules to apply beyond `.planning/` documents and existing codebase conventions.

**Pattern extraction date:** 2026-05-27
