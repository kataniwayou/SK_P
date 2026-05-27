# Phase 7: Generic HTTP Base + Composition Root - Pattern Map

**Mapped:** 2026-05-27
**Files analyzed:** 23 (12 new in `BaseApi.Core`, 2 modified in `BaseApi.Service`, 1 modified at repo root, 8 new in `tests/BaseApi.Tests`)
**Analogs found:** 21 / 23 (2 net-new file types — `IApplicationBuilder` extension + `IOperationFilter`/`IDocumentFilter` — have no existing analog in repo)

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseApi.Core/Controllers/BaseController.cs` (NEW) | controller (abstract generic) | request-response | `tests/BaseApi.Tests/Endpoints/TestController.cs` | role-match (only `[ApiController]` in repo) |
| `src/BaseApi.Core/Services/BaseService.cs` (NEW) | service (abstract generic orchestrator) | CRUD + transform | `tests/BaseApi.Tests/Validation/TestValidationService.cs` + `src/BaseApi.Core/Persistence/Repositories/Repository.cs` | role-match (composition of validate + map + repo + save) |
| `src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs` (NEW) | DI extension (public top-level) | composition | `src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs` | exact (public `Add*` extension) |
| `src/BaseApi.Core/DependencyInjection/BaseApiApplicationBuilderExtensions.cs` (NEW) | DI extension (public `IApplicationBuilder`) | middleware composition | NONE — first `IApplicationBuilder` ext in repo; pipeline source is current `Program.cs` lines 175-197 | no analog (use Program.cs as source) |
| `src/BaseApi.Core/DependencyInjection/PersistenceServiceCollectionExtensions.cs` (NEW) | DI extension (internal sub) | composition | `src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs` + Program.cs persistence block (would-be — Phase 3 wired in test fixtures, not Program.cs) | role-match |
| `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` (NEW) | DI extension (internal sub) | composition | `src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs` + Program.cs lines 96-144 (OTel block) | role-match (extension shape) + exact (OTel block is source material) |
| `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` (NEW) | DI extension (internal sub) | composition | `src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs` + Program.cs lines 154-162 (health block) | role-match + exact (health block is source material) |
| `src/BaseApi.Core/DependencyInjection/ErrorHandlingServiceCollectionExtensions.cs` (NEW) | DI extension (internal sub) | composition | `src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs` + Program.cs lines 58-87 (ProblemDetails + 4 IExceptionHandler) | role-match + exact (error block is source material) |
| `src/BaseApi.Core/DependencyInjection/HttpServiceCollectionExtensions.cs` (NEW) | DI extension (internal sub) | composition (NEW wiring) | `src/BaseApi.Core/DependencyInjection/MappingServiceCollectionExtensions.cs` (extension shape only — content is greenfield) | role-match (shape); content from RESEARCH.md Pattern 4 |
| `src/BaseApi.Core/Swagger/CorrelationIdHeaderOperationFilter.cs` (NEW) | filter (`IOperationFilter`) | metadata transform | NONE in repo; header constants from `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` | no analog (use RESEARCH Pattern 5 + middleware constants) |
| `src/BaseApi.Core/Swagger/HideHealthEndpointsDocumentFilter.cs` (NEW) | filter (`IDocumentFilter`) | metadata transform | NONE in repo; path constants from Program.cs MapHealthChecks block | no analog (use RESEARCH Pattern 6 + Program.cs paths) |
| `src/BaseApi.Service/Program.cs` (REWRITE) | host entry | composition root | current `src/BaseApi.Service/Program.cs` (BEFORE/AFTER shape) | exact (same file, complete rewrite) |
| `src/BaseApi.Service/AppDbContext.cs` (NEW placeholder) | DbContext | EF model | `tests/BaseApi.Tests/Persistence/TestDbContext.cs` | exact (concrete `BaseDbContext` subclass) |
| `Directory.Packages.props` (MODIFIED) | config | package management | existing entries lines 93-95 + lines 97-103 | exact (same file, additive) |
| `tests/BaseApi.Tests/Composition/TestsController.cs` (NEW) | test controller (concrete derived) | request-response | `tests/BaseApi.Tests/Endpoints/TestController.cs` | role-match (test-only controller via `AddApplicationPart`) |
| `tests/BaseApi.Tests/Composition/RecordingTestService.cs` (NEW) | test service (concrete derived) | CRUD + recording | `tests/BaseApi.Tests/Validation/TestValidationService.cs` + `tests/BaseApi.Tests/Validation/TestEntityMapper.cs` (sealed test-only class pattern) | role-match (no `BaseService` analog exists yet) |
| `tests/BaseApi.Tests/Composition/AddBaseApiFacts.cs` (NEW) | test (DI extension facts) | service resolution | `tests/BaseApi.Tests/Validation/ValidatorAutoDiscoveryTests.cs` + `tests/BaseApi.Tests/Validation/MapperRegistrationTests.cs` | exact (DI registration verification) |
| `tests/BaseApi.Tests/Composition/UseBaseApiPipelineFacts.cs` (NEW) | test (middleware pipeline) | integration (HTTP) | `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` | exact (middleware order via WAF HTTP probes) |
| `tests/BaseApi.Tests/Controllers/BaseControllerRoutesFacts.cs` (NEW) | test (route table inspection) | integration (DI + reflection) | `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` (WAF pattern only) | role-match (route table inspection is new pattern; WAF host is the shared seam) |
| `tests/BaseApi.Tests/Services/BaseServiceOrderingFacts.cs` (NEW) | test (6-step order via mocks) | unit (recording) | `tests/BaseApi.Tests/Validation/MapperRegistrationTests.cs` + RESEARCH Code Example 1 | role-match (NSubstitute is new pattern; xUnit v3 fact shape exists) |
| `tests/BaseApi.Tests/Services/NotFoundFacts.cs` (NEW) | test (NotFoundException → 404) | integration (HTTP) | `tests/BaseApi.Tests/Middleware/NotFoundAndUnhandledTests.cs` | exact (same exception type + same handler under test) |
| `tests/BaseApi.Tests/Versioning/VersioningFacts.cs` (NEW) | test (URL segment substitution) | integration (HTTP) | `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` (WAF + HTTP probe shape) | role-match (no existing routing facts; same HTTP probe shape) |
| `tests/BaseApi.Tests/Swagger/SwaggerEnvironmentFacts.cs` (NEW) | test (Dev 200 / Prod 404) | integration (HTTP) | `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` (nested fixture subclasses for env override) | exact (sub-fixture environment override) |

## Pattern Assignments

### `src/BaseApi.Core/Controllers/BaseController.cs` (controller, request-response)

**Analog:** `tests/BaseApi.Tests/Endpoints/TestController.cs` (only `[ApiController]` in repo — the controller folder in `BaseApi.Core` is empty)

**Imports pattern** (TestController.cs lines 1-10 + RESEARCH Pattern 1):

```csharp
// FROM TestController.cs (existing project import style):
using BaseApi.Core.Exceptions;            // NotFoundException reference
using Microsoft.AspNetCore.Mvc;           // [ApiController], [Route], ControllerBase

// ADD for BaseController (from RESEARCH Pattern 1):
using Asp.Versioning;                     // [ApiVersion("1.0")]
using BaseApi.Core.Entities;              // BaseEntity (TEntity constraint)
using BaseApi.Core.Services;              // BaseService<...> dependency
```

**Namespace / file-scoped style** (TestController.cs line 10):

```csharp
namespace BaseApi.Core.Controllers;       // file-scoped, one type per file
```

**Class-level attribute set** (TestController.cs lines 37-39 — analog only has `[ApiController]` + `[Route]`):

```csharp
[ApiController]
[Route("test")]
public sealed class TestController : ControllerBase
```

**Phase 7 BaseController DEVIATES** (per CONTEXT D-17 + RESEARCH Pattern 1) to:

```csharp
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public abstract class BaseController<TEntity, TCreate, TUpdate, TRead> : ControllerBase
    where TEntity : BaseEntity
```

**Action signature pattern** (TestController.cs lines 41-42 — simplest GET):

```csharp
[HttpGet("ok")]
public IActionResult Ok2xx() => Ok(new { ok = true });
```

**BaseController action pattern** (per CONTEXT D-01..D-04 + RESEARCH Pattern 1 lines 380-419):

```csharp
[HttpGet]
[ProducesResponseType(typeof(IReadOnlyList<TRead>), StatusCodes.Status200OK)]
public async Task<ActionResult<IReadOnlyList<TRead>>> List(CancellationToken ct)
    => Ok(await _service.ListAsync(ct));

[HttpPost]
[ProducesResponseType(typeof(TRead), StatusCodes.Status201Created)]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
public async Task<ActionResult<TRead>> Create([FromBody] TCreate dto, CancellationToken ct)
{
    var read = await _service.CreateAsync(dto, ct);
    return CreatedAtAction(nameof(GetById), new { id = ((dynamic)read!).Id }, read);
}
```

**Sealed-vs-abstract convention** (from `Repository.cs` line 13 + `NotFoundExceptionHandler.cs` line 19 + Phase 7 CONTEXT.md line 165):
- BaseEntity-style abstract base (CONTEXT HTTP-02): `public abstract class BaseController<...>`
- DO NOT seal the base — Phase 8's 5 concrete controllers MUST be able to inherit.

**XML doc style** (TestController.cs lines 12-36): triple-slash `<summary>` with `<para>` blocks; pattern is mandatory because `<GenerateDocumentationFile>true</GenerateDocumentationFile>` is enabled in `BaseApi.Core.csproj` line 26 (CS1591 is suppressed via `NoWarn` but documenting public types is project-wide convention).

---

### `src/BaseApi.Core/Services/BaseService.cs` (service, CRUD orchestrator)

**Analog:** `tests/BaseApi.Tests/Validation/TestValidationService.cs` (shape only — validation-only) + `src/BaseApi.Core/Persistence/Repositories/Repository.cs` (5-method CT-flowing surface)

**Imports pattern** (combining both analogs + RESEARCH Pattern 2):

```csharp
using BaseApi.Core.Entities;                   // BaseEntity constraint
using BaseApi.Core.Exceptions;                 // NotFoundException
using BaseApi.Core.Mapping;                    // IEntityMapper<,,,>
using BaseApi.Core.Persistence;                // BaseDbContext
using BaseApi.Core.Persistence.Repositories;   // IRepository<>
using FluentValidation;                        // IValidator<T>, ValidateAndThrowAsync
using Microsoft.EntityFrameworkCore;           // SaveChangesAsync
using Microsoft.Extensions.Logging;            // ILogger<>
```

**Constructor pattern** (from `Repository.cs` line 17 + `TestValidationService.cs` line 15 — both use primary-style ctor that ASSIGNS injected field):

```csharp
// Repository.cs line 17 (concise sealed style):
public Repository(BaseDbContext db) => _set = db.Set<TEntity>();

// TestValidationService.cs line 15:
public TestValidationService(IValidator<TestUpdateDto> validator) => _validator = validator;
```

**Phase 7 BaseService ctor** — 6 injectees (CONTEXT Discretion bullet 1 — planner picks `InvalidOperationException` path):

```csharp
protected BaseService(
    IValidator<TCreate> createValidator,
    IValidator<TUpdate> updateValidator,
    IEntityMapper<TEntity, TCreate, TUpdate, TRead> mapper,
    IRepository<TEntity> repo,
    BaseDbContext dbContext,
    ILogger<BaseService<TEntity, TCreate, TUpdate, TRead>> logger)
{
    _createValidator = createValidator ?? throw new InvalidOperationException(...);
    // ... assign all 6 fields
}
```

**Note (Pitfall 3):** `_dbContext` field MUST be `protected` (or expose `protected BaseDbContext DbContext { get; }` property) so `RecordingTestService` (SC#2) can read `ChangeTracker.Entries<TEntity>().Single().State` inside the override.

**Core validation pattern** (TestValidationService.cs lines 17-22 verbatim — the exact step 1 of CONTEXT D-11 6-step order):

```csharp
public async Task ValidateAsync(TestUpdateDto dto, CancellationToken ct)
{
    var result = await _validator.ValidateAsync(dto, ct);
    if (!result.IsValid)
        throw new FluentValidation.ValidationException(result.Errors);
}
```

**BaseService inlines this via FluentValidation's `ValidateAndThrowAsync` extension** (RESEARCH Pattern 2 line 499):

```csharp
await _createValidator.ValidateAndThrowAsync(dto, ct);  // step 1 of the 6
```

**Core CRUD pattern** (RESEARCH Pattern 2 lines 496-505 — the locked 6-step order):

```csharp
public async Task<TRead> CreateAsync(TCreate dto, CancellationToken ct)
{
    await _createValidator.ValidateAndThrowAsync(dto, ct);     // 1 — VALID-03 explicit
    var entity = _mapper.ToEntity(dto);                         // 2 — Mapperly
    await _repo.AddAsync(entity, ct);                           // 3 — tracker:Added
    await SyncJunctionsAsync(entity, dto, default, ct);         // 4 — virtual hook
    await _dbContext.SaveChangesAsync(ct);                      // 5 — AuditInterceptor fires
    return _mapper.ToRead(entity);                              // 6 — audit fields visible
}
```

**NotFound pattern** (from `NotFoundException.cs` line 32):

```csharp
// In Get/Update/Delete:
if (entity is null) throw new NotFoundException(typeof(TEntity).Name, id);
```

**Hook pattern** (CONTEXT D-09/D-10 — virtual no-op default):

```csharp
protected virtual Task SyncJunctionsAsync(
    TEntity entity, TCreate? createDto, TUpdate? updateDto, CancellationToken ct)
    => Task.CompletedTask;
```

**CancellationToken plumbing** (from `Repository.cs` lines 19-35 — every method flows CT through):

```csharp
public Task<TEntity?> GetAsync(Guid id, CancellationToken cancellationToken)
    => _set.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
```

BaseService follows the same shape — every public method has `CancellationToken ct` as the last parameter (CONTEXT D-05).

---

### `src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs` (public DI extension)

**Analog:** `src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs`

**Imports pattern** (ValidationServiceCollectionExtensions.cs lines 1-3):

```csharp
using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Core.DependencyInjection;
```

**Phase 7 imports (per RESEARCH Pattern 3):**

```csharp
using BaseApi.Core.Persistence;                  // BaseDbContext constraint
using Microsoft.EntityFrameworkCore;             // DbContext type
using Microsoft.Extensions.Configuration;        // IConfiguration
using Microsoft.Extensions.DependencyInjection;  // IServiceCollection
```

**Class-level structure** (ValidationServiceCollectionExtensions.cs lines 25-40):

```csharp
public static class ValidationServiceCollectionExtensions
{
    public static IServiceCollection AddBaseApiValidation(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            services.AddValidatorsFromAssembly(
                assembly,
                lifetime: ServiceLifetime.Scoped,
                includeInternalTypes: false);
        }
        return services;
    }
}
```

**Phase 7 `AddBaseApi<TDbContext>` pattern** (CONTEXT D-13 verbatim — fluent chain shape):

```csharp
public static class BaseApiServiceCollectionExtensions
{
    public static IServiceCollection AddBaseApi<TDbContext>(
        this IServiceCollection services, IConfiguration cfg)
        where TDbContext : BaseDbContext   // RESEARCH note: stronger than CONTEXT D-13's `DbContext`
        => services
            .AddBaseApiPersistence<TDbContext>(cfg)
            .AddBaseApiObservability(cfg)
            .AddBaseApiHealth(cfg)         // RESEARCH-corrected: needs cfg for AddNpgSql
            .AddBaseApiErrorHandling()
            .AddBaseApiHttp(cfg)
            .AddBaseApiValidation(typeof(TDbContext).Assembly)
            .AddBaseApiMapping(typeof(TDbContext).Assembly);
}
```

**XML doc style** (ValidationServiceCollectionExtensions.cs lines 7-24): triple-slash `<summary>` with `<para>` blocks naming Phase boundaries — copy exactly.

---

### `src/BaseApi.Core/DependencyInjection/PersistenceServiceCollectionExtensions.cs` (internal sub-extension)

**Analog:** `src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs` (shape) + `src/BaseApi.Service/Program.cs` lines 51, 154 + RESEARCH Pattern 3 sub-extension code

**Method-visibility pattern** (deviates from ValidationServiceCollectionExtensions.cs's `public static` to `internal static` per CONTEXT D-13):

```csharp
internal static class PersistenceServiceCollectionExtensions
{
    internal static IServiceCollection AddBaseApiPersistence<TDbContext>(
        this IServiceCollection services, IConfiguration cfg)
        where TDbContext : BaseDbContext
    {
        services.AddHttpContextAccessor();      // idempotent (Program.cs line 51 already does this)
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuditInterceptor>();  // RESEARCH Pitfall 4: Singleton (NOT Scoped per CONTEXT D-14 — Phase 3 D-06 is canonical)
        services.AddDbContext<TDbContext>((sp, opts) =>
        {
            opts.UseNpgsql(cfg.GetConnectionString("Postgres"))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
        });
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<BaseDbContext>(sp => sp.GetRequiredService<TDbContext>());  // RESEARCH Pitfall 5: Scoped alias
        return services;
    }
}
```

**AuditInterceptor reference** — `src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs` lines 38-47 (ctor takes `IHttpContextAccessor` + `TimeProvider` — both Singleton-safe → Singleton interceptor is correct per RESEARCH Pitfall 4).

---

### `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` (internal sub-extension)

**Analog:** ValidationServiceCollectionExtensions.cs (shape) + Program.cs lines 96-144 (verbatim OTel block — paste into the extension body)

**Source material — Program.cs lines 96-144 (OTel logs + metrics + traces):**

```csharp
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes           = true;
    o.ParseStateValues        = true;
    o.SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion));
    o.AddOtlpExporter();
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName: serviceName, serviceVersion: serviceVersion))
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation().AddOtlpExporter())
    .WithTracing(t => t
        .SetSampler(new AlwaysOnSampler())
        .AddAspNetCoreInstrumentation(opts =>
            opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
        .AddHttpClientInstrumentation()
        .AddNpgsql()
        .AddOtlpExporter());
```

**Phase 7 wrapping** (paste verbatim inside `internal static AddBaseApiObservability`):

```csharp
internal static class ObservabilityServiceCollectionExtensions
{
    internal static IServiceCollection AddBaseApiObservability(
        this IServiceCollection services, IConfiguration cfg)
    {
        // PROBLEM: AddOpenTelemetry for logs is on builder.Logging (ILoggingBuilder),
        // not services. Planner picks: (a) take ILoggingBuilder + IServiceCollection (b) take
        // WebApplicationBuilder, OR (c) split into two extensions. Recommendation: take
        // (IHostApplicationBuilder builder) on the extension since AddBaseApi<TDb> chain
        // already has access via the calling Program.cs.
        // ... see RESEARCH Pattern 3 + lines 270-298 ...
    }
}
```

**Service:Name lookup pattern** (Program.cs lines 43-44):

```csharp
var serviceName    = cfg["Service:Name"]!;
var serviceVersion = cfg["Service:Version"]!;
```

Phase 7 reads the same keys inside the extension; null-bang is acceptable per existing project convention.

---

### `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` (internal sub-extension)

**Analog:** Program.cs lines 153-162 (verbatim health-checks block)

**Source material — Program.cs lines 153-162:**

```csharp
builder.Services.AddSingleton<IStartupGate, StartupGate>();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup", "ready" })
    .AddNpgSql(cfg.GetConnectionString("Postgres")!, tags: new[] { "ready" });

builder.Services.AddHostedService<StartupCompletionService>();
```

**Imports to add** (from Program.cs lines 30, 33-34):

```csharp
using BaseApi.Core.Health;                                  // IStartupGate, StartupHealthCheck, StartupCompletionService
using Microsoft.Extensions.Diagnostics.HealthChecks;        // HealthCheckResult
```

**Sub-extension shape** (same `internal static` pattern as Persistence):

```csharp
internal static class HealthServiceCollectionExtensions
{
    internal static IServiceCollection AddBaseApiHealth(
        this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton<IStartupGate, StartupGate>();
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
            .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup", "ready" })
            .AddNpgSql(cfg.GetConnectionString("Postgres")!, tags: new[] { "ready" });
        services.AddHostedService<StartupCompletionService>();
        return services;
    }
}
```

**CRITICAL — `StartupCompletionService` removability pattern** (Pitfall already verified in `HealthEndpointsTests.cs` lines 286-304):

```csharp
// HealthNoStartupCompletionFixture pattern — tests can still REMOVE the IHostedService
// via ConfigureTestServices:
var toRemove = services
    .Where(d => d.ImplementationType == typeof(StartupCompletionService))
    .ToList();
foreach (var d in toRemove) services.Remove(d);
```

This pattern carries through Phase 7 unchanged — `AddBaseApiHealth` registers the IHostedService; tests can still remove it by `ImplementationType` match.

---

### `src/BaseApi.Core/DependencyInjection/ErrorHandlingServiceCollectionExtensions.cs` (internal sub-extension)

**Analog:** Program.cs lines 58-87 (verbatim ProblemDetails customizer + 4 IExceptionHandler)

**Source material — Program.cs lines 58-87:**

```csharp
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

builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<DbUpdateExceptionHandler>();
builder.Services.AddExceptionHandler<FallbackExceptionHandler>();
```

**Imports** (Program.cs lines 29):

```csharp
using BaseApi.Core.Exceptions.Handlers;  // NotFoundExceptionHandler, ValidationExceptionHandler, DbUpdateExceptionHandler, FallbackExceptionHandler
```

**Sub-extension shape:**

```csharp
internal static class ErrorHandlingServiceCollectionExtensions
{
    internal static IServiceCollection AddBaseApiErrorHandling(this IServiceCollection services)
    {
        services.AddProblemDetails(options => { /* customizer — paste verbatim */ });
        services.AddExceptionHandler<NotFoundExceptionHandler>();
        services.AddExceptionHandler<ValidationExceptionHandler>();
        services.AddExceptionHandler<DbUpdateExceptionHandler>();
        services.AddExceptionHandler<FallbackExceptionHandler>();
        return services;
    }
}
```

**ORDER IS LOAD-BEARING** (Program.cs lines 81-87 comment, NotFoundExceptionHandler.cs line 28 + FallbackExceptionHandler.cs line 60 — Phase 4 D-06 chain — `NotFound → Validation → DbUpdate → Fallback`). The Program.cs comment block (lines 81-83) MUST be preserved as XML doc / inline comment inside the extension.

---

### `src/BaseApi.Core/DependencyInjection/HttpServiceCollectionExtensions.cs` (internal sub-extension — NEW wiring)

**Analog:** ValidationServiceCollectionExtensions.cs (shape only) + RESEARCH Pattern 4 (content — first time these libs are wired in repo)

**Source material — RESEARCH Pattern 4 lines 671-697:**

```csharp
internal static class HttpServiceCollectionExtensions
{
    internal static IServiceCollection AddBaseApiHttp(
        this IServiceCollection services, IConfiguration cfg)
    {
        services.AddControllers();

        services.AddApiVersioning(opts =>
        {
            opts.DefaultApiVersion = new ApiVersion(1, 0);
            opts.AssumeDefaultVersionWhenUnspecified = true;
            opts.ReportApiVersions = true;
        })
        .AddMvc()                                  // CRITICAL: requires Asp.Versioning.Mvc package (RESEARCH A-01)
        .AddApiExplorer(opts =>
        {
            opts.GroupNameFormat = "'v'VVV";
            opts.SubstituteApiVersionInUrl = true;
        });

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
        services.AddTransient<IConfigureOptions<SwaggerGenOptions>, ConfigureSwaggerOptions>();

        return services;
    }
}
```

**CRITICAL — order constraint (RESEARCH Pitfall 2):** `AddApiVersioning().AddMvc().AddApiExplorer()` MUST run BEFORE `AddSwaggerGen()`. Preserve this sequence inside the method body.

**Existing `AddControllers()` call** (Program.cs line 164) gets MOVED into this sub-extension — no duplicate registration needed.

---

### `src/BaseApi.Core/DependencyInjection/BaseApiApplicationBuilderExtensions.cs` (public `IApplicationBuilder` extension — NEW pattern)

**Analog:** NONE — first `IApplicationBuilder` extension in repo. Source material is Program.cs lines 175-201 (pipeline construction).

**Source material — Program.cs lines 175-201:**

```csharp
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRouting();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate      = c => c.Tags.Contains("live"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions { /* tag "ready" */ });
app.MapHealthChecks("/health/startup", new HealthCheckOptions { /* tag "startup" */ });

app.MapControllers();
```

**Phase 7 extension shape** (per CONTEXT D-19 + RESEARCH Pattern 4 UI wiring):

```csharp
public static class BaseApiApplicationBuilderExtensions
{
    public static WebApplication UseBaseApi(this WebApplication app)
    {
        app.UseExceptionHandler();                          // FIRST (Phase 4 D-01)
        app.UseMiddleware<CorrelationIdMiddleware>();       // Phase 4 — preserves correlationId in error path
        app.UseRouting();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(opts =>
            {
                foreach (var description in app.DescribeApiVersions())
                {
                    opts.SwaggerEndpoint(
                        $"/swagger/{description.GroupName}/swagger.json",
                        description.GroupName.ToUpperInvariant());
                }
            });
        }

        app.MapHealthChecks("/health/live",    new HealthCheckOptions { Predicate = c => c.Tags.Contains("live"),    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
        app.MapHealthChecks("/health/ready",   new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"),   ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
        app.MapHealthChecks("/health/startup", new HealthCheckOptions { Predicate = c => c.Tags.Contains("startup"), ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });

        // MapControllers stays in Program.cs (CONTEXT D-19 comment) — tests can MapControllers
        // independently of UseBaseApi.
        return app;
    }
}
```

**Imports** (Program.cs lines 30-34):

```csharp
using BaseApi.Core.Middleware;                      // CorrelationIdMiddleware
using HealthChecks.UI.Client;                       // UIResponseWriter
using Microsoft.AspNetCore.Diagnostics.HealthChecks; // HealthCheckOptions
```

**Method visibility:** `public static` (CONTEXT D-13 — top-level `UseBaseApi` is the public surface; the internal sub-extensions don't apply here — pipeline is a single linear sequence).

---

### `src/BaseApi.Core/Swagger/CorrelationIdHeaderOperationFilter.cs` (IOperationFilter)

**Analog:** NONE in repo. Pattern source: RESEARCH Pattern 5. Constants source: `CorrelationIdMiddleware.cs` lines 51-53.

**Source — `CorrelationIdMiddleware.cs` lines 51-53 (header name + max length pattern):**

```csharp
private const string HeaderName = "X-Correlation-Id";
private const string ItemKey = "CorrelationId";
private const int MaxLength = 128;
```

**Phase 7 filter** (per RESEARCH Pattern 5 lines 771-786 — replicate header name + max length verbatim):

```csharp
internal sealed class CorrelationIdHeaderOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Parameters ??= new List<OpenApiParameter>();
        operation.Parameters.Add(new OpenApiParameter
        {
            Name        = "X-Correlation-Id",
            In          = ParameterLocation.Header,
            Required    = false,
            Description = "Optional correlation ID for request tracking. If absent, the server generates a new 32-char hex value and echoes it on the response header.",
            Schema      = new OpenApiSchema { Type = "string", MaxLength = 128 },
        });
    }
}
```

**Naming/sealed convention** (matches `NotFoundExceptionHandler.cs` line 19 + `ValidationExceptionHandler.cs` line 34 — all handler-style types are `internal sealed class`).

---

### `src/BaseApi.Core/Swagger/HideHealthEndpointsDocumentFilter.cs` (IDocumentFilter)

**Analog:** NONE in repo. Pattern source: RESEARCH Pattern 6. Path constants source: Program.cs lines 183, 188, 193 (the three `MapHealthChecks` paths).

**Source — Program.cs MapHealthChecks paths:**

```csharp
app.MapHealthChecks("/health/live", ...);
app.MapHealthChecks("/health/ready", ...);
app.MapHealthChecks("/health/startup", ...);
```

**Phase 7 filter** (per RESEARCH Pattern 6 lines 801-815 — uses `/health` prefix to match all three):

```csharp
internal sealed class HideHealthEndpointsDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        var pathsToRemove = swaggerDoc.Paths
            .Where(kv => kv.Key.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var path in pathsToRemove)
        {
            swaggerDoc.Paths.Remove(path);
        }
    }
}
```

---

### `src/BaseApi.Service/Program.cs` (REWRITTEN)

**Analog:** current Program.cs (BEFORE — lines 1-207) becomes the AFTER target (~10 lines).

**Source material — current Program.cs lines 28-39, 41-44, 166-201:** the imports + variable block + pipeline are all DELETED (absorbed by AddBaseApi sub-extensions). The `public partial class Program { }` marker (line 206) is LOAD-BEARING — `WebApplicationFactory<Program>` in `WebAppFactory.cs` line 29 depends on it.

**Target shape** (CONTEXT D-15 + RESEARCH Pattern 7):

```csharp
using BaseApi.Core.DependencyInjection;
using BaseApi.Service;   // AppDbContext

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBaseApi<AppDbContext>(builder.Configuration);
var app = builder.Build();
app.UseBaseApi();
app.MapControllers();
app.Run();

public partial class Program { }   // load-bearing for WebApplicationFactory<Program>
```

**Negative-assertion patterns for SC#3** (RESEARCH Code Example 4 lines 1166-1176 — the planner picks for `ProgramMinimalityTests.cs`):

```csharp
Assert.Contains("AddBaseApi<", fileContent);
Assert.Contains("UseBaseApi(", fileContent);
Assert.Contains("MapControllers", fileContent);
Assert.DoesNotContain("AddOpenTelemetry", fileContent);
Assert.DoesNotContain("AddHealthChecks", fileContent);
Assert.DoesNotContain("AddExceptionHandler<", fileContent);
Assert.DoesNotContain("AddDbContext<", fileContent);
Assert.DoesNotContain("AddSwaggerGen", fileContent);
Assert.DoesNotContain("AddApiVersioning", fileContent);
```

---

### `src/BaseApi.Service/AppDbContext.cs` (NEW placeholder per RESEARCH Path B)

**Analog:** `tests/BaseApi.Tests/Persistence/TestDbContext.cs` — exact shape for an empty concrete `BaseDbContext` subclass.

**Source — TestDbContext.cs lines 12-17 (verbatim minimal pattern, minus the DbSet):**

```csharp
public sealed class TestDbContext : BaseDbContext
{
    public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }

    public DbSet<TestEntity> TestEntities => Set<TestEntity>();
}
```

**Phase 7 placeholder** (per RESEARCH A5 — empty model is OK for Phase 7; Phase 8 adds DbSets):

```csharp
using Microsoft.EntityFrameworkCore;
using BaseApi.Core.Persistence;

namespace BaseApi.Service;

public sealed class AppDbContext : BaseDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // Phase 8 adds: DbSet<SchemaEntity>, DbSet<ProcessorEntity>, DbSet<StepEntity>,
    // DbSet<AssignmentEntity>, DbSet<WorkflowEntity> + 3 junction DbSets.
}
```

`BaseDbContext.OnModelCreating` (lines 44-62) iterates `modelBuilder.Model.GetEntityTypes().Where(...BaseEntity...)` — an empty collection produces zero xmin shadow properties and zero side effects, so the empty class is safe.

---

### `Directory.Packages.props` (MODIFIED — additive)

**Analog:** existing pin block lines 93-95 (HTTP — Asp.Versioning.Http + Swashbuckle.AspNetCore) and lines 97-103 (Tests).

**Source — existing lines 93-95:**

```xml
<!-- HTTP — API versioning + Swagger (REQ HTTP-15, HTTP-16) -->
<PackageVersion Include="Asp.Versioning.Http" Version="8.1.0" />
<PackageVersion Include="Swashbuckle.AspNetCore" Version="6.9.0" />
```

**Phase 7 additions** (RESEARCH Standard Stack + A-01 + Pitfall 9):

```xml
<!-- Phase 7 RESEARCH A-01: Asp.Versioning.Mvc is the controllers-correct package
     (CONTEXT D-17's "Asp.Versioning.Http" was a brand-name miscite). Mvc transitively
     pulls Http; ApiExplorer is required for SubstituteApiVersionInUrl + GroupNameFormat. -->
<PackageVersion Include="Asp.Versioning.Mvc" Version="8.1.0" />
<PackageVersion Include="Asp.Versioning.Mvc.ApiExplorer" Version="8.1.0" />

<!-- Phase 7 RESEARCH Pitfall 9: NSubstitute (post-Moq SponsorLink). For SC#2 ordering proof. -->
<PackageVersion Include="NSubstitute" Version="5.3.0" />
```

**Naming convention** (Directory.Packages.props lines 47-48, 65, 68): `<PackageVersion Include="..." Version="..." />` on one line; group with comment header explaining the phase.

---

### `tests/BaseApi.Tests/Composition/TestsController.cs` (test controller — empty derived)

**Analog:** `tests/BaseApi.Tests/Endpoints/TestController.cs` (test-only controller, lives in test assembly, discovered via `AddApplicationPart`).

**Source — TestController.cs lines 37-39 + WebAppFactory.cs lines 42-43 (`AddApplicationPart` mechanism):**

```csharp
// TestController.cs:
[ApiController]
[Route("test")]
public sealed class TestController : ControllerBase

// WebAppFactory.cs lines 42-43:
services.AddControllers()
    .AddApplicationPart(typeof(WebAppFactory).Assembly);
```

**Phase 7 `TestsController` shape** (CONTEXT specifics: "empty body" — matches `[controller]` token resolving to "tests"):

```csharp
namespace BaseApi.Tests.Composition;

public sealed class TestsController : BaseController<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>
{
    public TestsController(RecordingTestService service) : base(service) { }
    // EMPTY body — 5 verbs inherited from BaseController.
}
```

**Notes:**
- Reuses `TestEntity` + 3 DTOs from `tests/BaseApi.Tests/Validation/` (already shipped Phase 6).
- Class name `TestsController` (plural) — the `[controller]` route token strips "Controller" → "tests" → URL `/api/v1/tests`.
- `sealed` per project convention (TestController.cs line 39, `Repository.cs` line 13, all handlers).

---

### `tests/BaseApi.Tests/Composition/RecordingTestService.cs` (test BaseService subclass)

**Analog:** `tests/BaseApi.Tests/Validation/TestValidationService.cs` (shape: sealed test class with ctor injection) + RESEARCH Pattern 2 test-seam example (lines 542-570).

**Source — TestValidationService.cs lines 11-23:**

```csharp
public sealed class TestValidationService
{
    private readonly IValidator<TestUpdateDto> _validator;

    public TestValidationService(IValidator<TestUpdateDto> validator) => _validator = validator;

    public async Task ValidateAsync(TestUpdateDto dto, CancellationToken ct)
    {
        var result = await _validator.ValidateAsync(dto, ct);
        if (!result.IsValid)
            throw new FluentValidation.ValidationException(result.Errors);
    }
}
```

**Phase 7 RecordingTestService** (RESEARCH Pattern 2 lines 542-570 — override `SyncJunctionsAsync` to record state + timestamp):

```csharp
public sealed class RecordingTestService : BaseService<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>
{
    public List<(DateTime Timestamp, EntityState ChangeTrackerState)> JunctionRecords { get; } = new();

    public RecordingTestService(
        IValidator<TestCreateDto> createValidator,
        IValidator<TestUpdateDto> updateValidator,
        IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto> mapper,
        IRepository<TestEntity> repo,
        BaseDbContext dbContext,
        ILogger<BaseService<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>> logger)
        : base(createValidator, updateValidator, mapper, repo, dbContext, logger) { }

    protected override Task SyncJunctionsAsync(
        TestEntity entity, TestCreateDto? createDto, TestUpdateDto? updateDto, CancellationToken ct)
    {
        // Reads DbContext via the protected property (Pitfall 3 — BaseService must expose).
        var state = DbContext.ChangeTracker.Entries<TestEntity>().Single().State;
        JunctionRecords.Add((Timestamp: DateTime.UtcNow, State: state));
        return Task.CompletedTask;
    }
}
```

---

### `tests/BaseApi.Tests/Composition/AddBaseApiFacts.cs` (DI extension facts)

**Analog:** `tests/BaseApi.Tests/Validation/ValidatorAutoDiscoveryTests.cs` + `tests/BaseApi.Tests/Validation/MapperRegistrationTests.cs` (both exercise `Add*` extensions against a fresh `ServiceCollection`).

**Source — ValidatorAutoDiscoveryTests.cs lines 13-52 (Arrange / Act / Assert pattern):**

```csharp
public sealed class ValidatorAutoDiscoveryTests
{
    [Fact]
    public void Test_AddBaseApiValidation_DiscoversTestDtoValidator()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddBaseApiValidation(typeof(TestDtoValidator).Assembly);
        using var provider = services.BuildServiceProvider();

        // Act
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetService<IValidator<TestUpdateDto>>();

        // Assert
        Assert.NotNull(validator);
        Assert.IsType<TestDtoValidator>(validator);
    }

    [Fact]
    public void Test_AddBaseApiValidation_DefaultLifetime_IsScoped()
    {
        // ... two-scope same-instance assertion pattern
    }
}
```

**Phase 7 adapt** — facts assert that `AddBaseApi<TDbContext>(cfg)` registers each concern:

```csharp
public sealed class AddBaseApiFacts
{
    [Fact]
    public void Test_AddBaseApi_Registers_DbContext()
    {
        var services = new ServiceCollection();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Port=5433;..."
            })
            .Build();
        services.AddBaseApi<AppDbContext>(cfg);
        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetService<AppDbContext>();
        Assert.NotNull(db);
    }
    // ... similar facts for IExceptionHandler, IStartupGate, IValidator<>, IEntityMapper<>
}
```

**Naming convention** (file ends in `Facts.cs` per Phase 7 context "fact" terminology; analog uses `Tests.cs` — planner picks based on Phase 7 RESEARCH naming `RouteExposureTests.cs` etc., but project convention is mixed; recommend `Facts.cs` per CONTEXT.md test seam wording).

---

### `tests/BaseApi.Tests/Composition/UseBaseApiPipelineFacts.cs` (middleware pipeline order)

**Analog:** `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` (full HTTP probe via `WebAppFactory` → assert response headers).

**Source — CorrelationIdTests.cs lines 14-27 (canonical WAF + HTTP probe):**

```csharp
[Fact]
public async Task Test_Missing_Header_Generates_32CharHex_On2xx()
{
    var ct = TestContext.Current.CancellationToken;
    using var factory = new WebAppFactory();
    using var client = factory.CreateClient();

    var response = await client.GetAsync("/test/ok", ct);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var values));
    var corr = values!.First();
    Assert.Matches("^[a-f0-9]{32}$", corr);
}
```

**Phase 7 adapts the same shape** — probe `/api/v1/tests` (the BaseController routes) and assert `X-Correlation-Id` header echoes (proves CorrelationIdMiddleware is in the pipeline AFTER UseBaseApi).

**CT-flow convention** (CorrelationIdTests.cs line 17): `var ct = TestContext.Current.CancellationToken;` MUST be the first line of every async fact. Phase 3 D-18 + Phase 7 inheritance — non-negotiable per `xUnit1051` analyzer (TreatWarningsAsErrors=true).

---

### `tests/BaseApi.Tests/Controllers/BaseControllerRoutesFacts.cs` (route table inspection — SC#1)

**Analog:** `tests/BaseApi.Tests/Validation/ValidatorAutoDiscoveryTests.cs` (DI provider inspection) + `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` (WAF factory).

**Source — RESEARCH Code Example 3 lines 1110-1132 (the exact route-inspection pattern):**

```csharp
[Fact]
public void TestsController_Exposes_FiveCrudRoutes_UnderApiV1()
{
    using var factory = new WebAppFactory();
    using var scope = factory.Services.CreateScope();
    var actionDescriptorCollection = scope.ServiceProvider
        .GetRequiredService<IActionDescriptorCollectionProvider>();

    var tests = actionDescriptorCollection.ActionDescriptors.Items
        .OfType<ControllerActionDescriptor>()
        .Where(a => a.ControllerTypeInfo.AsType() == typeof(TestsController))
        .ToList();

    Assert.Equal(5, tests.Count);
    // ... assert each route template matches expected verb + path
}
```

---

### `tests/BaseApi.Tests/Services/BaseServiceOrderingFacts.cs` (6-step order — SC#2)

**Analog:** `tests/BaseApi.Tests/Validation/MapperRegistrationTests.cs` (xUnit v3 fact + ServiceCollection wiring) + RESEARCH Code Example 1 (the full NSubstitute timestamp-recording pattern lines 985-1064).

**Pattern source — RESEARCH Code Example 1 lines 1003-1064 (NSubstitute Received.InOrder for ordered-call grammar):**

```csharp
var createValidator = Substitute.For<IValidator<TestCreateDto>>();
var mapper = Substitute.For<IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>>();
var repo = Substitute.For<IRepository<TestEntity>>();

createValidator.ValidateAsync(Arg.Any<TestCreateDto>(), Arg.Any<CancellationToken>())
    .Returns(call => { validateTime = DateTime.UtcNow; return new ValidationResult(); });

// ... wire mapper.ToEntity, repo.AddAsync, mapper.ToRead with capturing closures

var service = new RecordingTestService(createValidator, updateValidator, mapper, repo,
                                       fixture.DbContext, NullLogger<...>.Instance);
var result = await service.CreateAsync(dto, ct);

// Assert: 6-step order via timestamps + tracker state
Assert.True(addTime < junctionTime, "SyncJunctionsAsync runs AFTER repo.AddAsync");
Assert.Equal(EntityState.Added, trackerState);

Received.InOrder(() =>
{
    createValidator.ValidateAsync(Arg.Any<TestCreateDto>(), Arg.Any<CancellationToken>());
    mapper.ToEntity(Arg.Any<TestCreateDto>());
    repo.AddAsync(Arg.Any<TestEntity>(), Arg.Any<CancellationToken>());
    mapper.ToRead(Arg.Any<TestEntity>());
});
```

**Postgres fixture pattern** (`PostgresFixture.cs` is shared by Phase 3/4 tests — `RecordingTestService` needs a real `BaseDbContext` because the override reads `ChangeTracker.Entries<TestEntity>().Single().State`; in-memory provider does NOT track entities the same way).

**No analog in repo for NSubstitute usage** — this is greenfield (RESEARCH Pitfall 9). Pattern is fully captured in RESEARCH Code Example 1.

---

### `tests/BaseApi.Tests/Services/NotFoundFacts.cs` (NotFoundException → 404)

**Analog:** `tests/BaseApi.Tests/Middleware/NotFoundAndUnhandledTests.cs` (exact same exception type tested through the exact same handler).

**Source — NotFoundAndUnhandledTests.cs lines 16-37:**

```csharp
[Fact]
public async Task Test_NotFoundException_Produces_404_With_ResourceType_And_Id()
{
    var ct = TestContext.Current.CancellationToken;
    using var factory = new WebAppFactory();
    using var client = factory.CreateClient();

    var response = await client.GetAsync("/test/not-found", ct);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

    var body = await response.Content.ReadAsStringAsync(ct);
    using var doc = JsonDocument.Parse(body);
    Assert.Equal(404, doc.RootElement.GetProperty("status").GetInt32());

    var detail = doc.RootElement.GetProperty("detail").GetString()!;
    Assert.Contains("Schema", detail);
    Assert.Contains("was not found", detail);

    Assert.Equal("Schema", doc.RootElement.GetProperty("resourceType").GetString());
    Assert.True(doc.RootElement.TryGetProperty("resourceId", out _));
}
```

**Phase 7 adaptation:** probe `GET /api/v1/tests/{nonexistent-guid}` and assert the same 404 ProblemDetails shape. The BaseService `GetByIdAsync` should throw `NotFoundException(typeof(TestEntity).Name, id)` (per RESEARCH Pattern 2 line 492), which Phase 4 `NotFoundExceptionHandler` (already shipped) maps to 404. Phase 7 wave 2 fact verifies the wiring still works through `AddBaseApi` composition.

---

### `tests/BaseApi.Tests/Versioning/VersioningFacts.cs` (URL segment substitution)

**Analog:** `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` (HTTP probe shape only — no existing routing facts in repo).

**Source — CorrelationIdTests.cs lines 14-27** (same HTTP probe shape; replace path with `/api/v1/tests`).

**Phase 7 fact shape:**

```csharp
[Fact]
public async Task Test_VersionedRoute_Matches_ApiV1Tests()
{
    var ct = TestContext.Current.CancellationToken;
    using var factory = new WebAppFactory();   // or Phase7WebAppFactory if validator/mapper scan required
    using var client = factory.CreateClient();

    var response = await client.GetAsync("/api/v1/tests", ct);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // empty list returns 200 IReadOnlyList<TRead>
}

[Fact]
public async Task Test_UnsupportedVersion_Returns_400_ProblemDetails()
{
    // /api/v99/tests should produce 400 from Asp.Versioning with correlationId injection (Pitfall 7)
    var ct = TestContext.Current.CancellationToken;
    using var factory = new WebAppFactory();
    using var client = factory.CreateClient();

    var response = await client.GetAsync("/api/v99/tests", ct);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    var body = await response.Content.ReadAsStringAsync(ct);
    Assert.Contains("correlationId", body);   // RESEARCH Pitfall 7 verification
}
```

---

### `tests/BaseApi.Tests/Swagger/SwaggerEnvironmentFacts.cs` (Dev 200 / Prod 404 — SC#4)

**Analog:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` (lines 215-303) — nested-class fixture subclassing pattern for per-test environment / config override.

**Source — HealthEndpointsTests.cs nested fixture pattern (lines 215-231 + 286-304):**

```csharp
private sealed class HealthDeadPostgresFixture : OtelCollectorFixture
{
    private readonly string? _priorEnvValue;
    public HealthDeadPostgresFixture()
    {
        _priorEnvValue = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", "...");
    }
    public override async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _priorEnvValue);
        await base.DisposeAsync();
    }
}
```

**Phase 7 production-env subclass** (per RESEARCH Code Example 2 lines 1072-1091):

```csharp
internal sealed class ProductionWebAppFactory : WebAppFactory
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        return base.CreateHost(builder);
    }
}

[Fact]
public async Task Test_SwaggerEndpoint_Returns404_InProduction()
{
    var ct = TestContext.Current.CancellationToken;
    using var factory = new ProductionWebAppFactory();
    using var client = factory.CreateClient();

    var response = await client.GetAsync("/swagger", ct);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}

[Fact]
public async Task Test_SwaggerEndpoint_Returns200_InDevelopment()
{
    var ct = TestContext.Current.CancellationToken;
    using var factory = new WebAppFactory();   // default is Development
    using var client = factory.CreateClient();

    var response = await client.GetAsync("/swagger/v1/swagger.json", ct);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}
```

**WebAppFactory subclassability** (`WebAppFactory.cs` line 29): `public class WebAppFactory : WebApplicationFactory<Program>` — already unsealed by Phase 6 D-16 to support Phase 7 environment overrides.

---

## Shared Patterns

### Authentication / Authorization

**N/A in Phase 7** — no auth backend in v1 (CONTEXT Deferred Ideas + RESEARCH user constraints). No `[Authorize]` attributes, no security middleware, no JWT validation.

### Error Handling

**Source:** Phase 4 `IExceptionHandler` chain already shipped — `src/BaseApi.Core/Exceptions/Handlers/{NotFound,Validation,DbUpdate,Fallback}ExceptionHandler.cs`.

**Apply to:** All `BaseService` methods + `BaseController` actions.

**Pattern:** Services THROW domain exceptions; controllers stay branch-free; Phase 4 chain catches and maps:

```csharp
// In BaseService — throw, never branch:
if (entity is null) throw new NotFoundException(typeof(TEntity).Name, id);
// In BaseController — let it flow:
return Ok(await _service.GetByIdAsync(id, ct));
```

**Concrete excerpt** — `NotFoundExceptionHandler.cs` lines 25-50 already maps `NotFoundException → 404` with `resourceType` + `resourceId` extensions; Phase 7 BaseService throws into the same handler.

**Validation 400 path** (`ValidationExceptionHandler.cs` lines 40-66): BaseService uses `_createValidator.ValidateAndThrowAsync(dto, ct)` (FluentValidation extension) which throws `FluentValidation.ValidationException(result.Errors)` — handler maps to 400 `ValidationProblemDetails` with `errors` map. Verified in `tests/BaseApi.Tests/Validation/ValidationEndpointTests.cs` lines 17-45.

### Validation

**Source:** Phase 6 `AddBaseApiValidation` already shipped — `src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs` (auto-discovers `AbstractValidator<T>` via `AddValidatorsFromAssembly`).

**Apply to:** All `BaseService.CreateAsync` and `UpdateAsync` calls.

**Pattern** (CONTEXT D-07 + RESEARCH Pattern 2 line 499):

```csharp
await _createValidator.ValidateAndThrowAsync(dto, ct);  // step 1 of 6 — VALID-03 explicit
```

`ValidateAndThrowAsync` is the FluentValidation extension that throws `ValidationException` on failure — preferred over manual `ValidateAsync(...) + if (!Valid) throw` shown in `TestValidationService.cs` (analog uses the manual form because it's a deliberately-thin demonstrator). BaseService uses the extension for conciseness.

### Logging

**Source:** Phase 5 OTel logs via MEL bridge already shipped — Program.cs lines 96-106. No new logging in Phase 7 — BaseService receives `ILogger<BaseService<...>>` via DI but Phase 7 BaseService body does NOT call `_logger.LogX(...)` directly (deferred to Phase 8 when concrete services may add diagnostic logging).

**Pattern** (existing — `FallbackExceptionHandler.cs` line 39):

```csharp
_logger.LogError(exception, "Unhandled exception on {Path}", httpContext.Request.Path);
```

Phase 7 inherits this — no new pattern.

### CancellationToken Flow

**Source:** `src/BaseApi.Core/Persistence/Repositories/IRepository.cs` lines 22-43 (5 methods, all take `CancellationToken`) + `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` line 17 (`TestContext.Current.CancellationToken` as first line of every async fact).

**Apply to:** All `BaseService` methods + `BaseController` action signatures + every test fact.

**Pattern** (CONTEXT D-05):

```csharp
// Controller binds HttpContext.RequestAborted via parameter:
public async Task<ActionResult<TRead>> List(CancellationToken ct)
    => Ok(await _service.ListAsync(ct));

// Service threads CT through every awaitable:
public async Task<TRead> CreateAsync(TCreate dto, CancellationToken ct)
{
    await _createValidator.ValidateAndThrowAsync(dto, ct);
    await _repo.AddAsync(entity, ct);
    await _dbContext.SaveChangesAsync(ct);
    // ...
}

// Test fact first line:
var ct = TestContext.Current.CancellationToken;
```

**Non-negotiable** — `xUnit1051` analyzer is build-fatal under `TreatWarningsAsErrors=true` (Directory.Build.props line 35).

### XML Documentation

**Source:** all existing public types in `BaseApi.Core` carry triple-slash `<summary>` + `<para>` blocks. `<GenerateDocumentationFile>true</GenerateDocumentationFile>` is enabled on both `BaseApi.Core.csproj` (line 26) and `BaseApi.Service.csproj` (line 30); `<NoWarn>$(NoWarn);CS1591</NoWarn>` suppresses the missing-doc warning so undocumented types compile, BUT all sibling examples (`Repository.cs`, `BaseDbContext.cs`, `AuditInterceptor.cs`, `NotFoundExceptionHandler.cs`, `CorrelationIdMiddleware.cs`) document every public surface.

**Apply to:** Every new Phase 7 public type (BaseController, BaseService, all DI extensions, both Swagger filters). Internal helpers may skip but the project convention is to document.

**Pattern** — see `ValidationServiceCollectionExtensions.cs` lines 7-24 for the canonical multi-`<para>` style.

### File / Type Layout

**Source:** all existing source files — one type per file; namespace matches folder path; `sealed` on concrete classes by default (Repository.cs:13, NotFoundExceptionHandler.cs:19, StartupGate.cs:47, CorrelationIdMiddleware.cs:49, etc.); `abstract` only on intentional bases (BaseDbContext.cs:32, BaseEntity.cs:21).

**Apply to:** Every new Phase 7 file.

| Type | Default access modifier |
|------|-------------------------|
| BaseController | `public abstract` (HTTP-02) |
| BaseService | `public abstract` (HTTP-02) |
| Public DI extensions | `public static` |
| Internal DI extensions | `internal static` (CONTEXT D-13) |
| Swagger filters | `internal sealed` |
| Test fixtures | `public class` (subclassable per Phase 6 D-16) or `internal sealed` for one-off env overrides (matches `HealthEndpointsTests.HealthDeadPostgresFixture` pattern) |

## No Analog Found

| File | Role | Data Flow | Reason | Source to use instead |
|------|------|-----------|--------|------------------------|
| `src/BaseApi.Core/DependencyInjection/BaseApiApplicationBuilderExtensions.cs` | `IApplicationBuilder` ext | middleware composition | First-of-its-kind in repo | Program.cs lines 175-201 (pipeline source) + RESEARCH Pattern 4 (Swagger UI loop) + CONTEXT D-19 (locked order) |
| `src/BaseApi.Core/Swagger/CorrelationIdHeaderOperationFilter.cs` | IOperationFilter | metadata transform | No Swashbuckle filters in repo | RESEARCH Pattern 5 (verbatim) + CorrelationIdMiddleware.cs lines 51-53 (header name constant) |
| `src/BaseApi.Core/Swagger/HideHealthEndpointsDocumentFilter.cs` | IDocumentFilter | metadata transform | No Swashbuckle filters in repo | RESEARCH Pattern 6 (verbatim) + Program.cs MapHealthChecks paths |
| `tests/BaseApi.Tests/Services/BaseServiceOrderingFacts.cs` | NSubstitute mocking | unit (recording) | First mocking library use in repo | RESEARCH Code Example 1 (full NSubstitute + Received.InOrder pattern) + RESEARCH Pitfall 9 (NSubstitute version 5.3.0 vs Moq) |

## Metadata

**Analog search scope:**
- `src/BaseApi.Core/` — all subfolders read (Controllers/, Services/, DependencyInjection/, Persistence/, Exceptions/, Middleware/, Health/, Validation/, Mapping/, Entities/)
- `src/BaseApi.Service/` — Program.cs, csproj
- `tests/BaseApi.Tests/` — Endpoints/, Validation/, Middleware/, Persistence/, Observability/
- Repo root — Directory.Build.props, Directory.Packages.props

**Files scanned:** ~50 source files + 3 csproj + 2 props (full hand-read of the 25 most-relevant analogs)

**Pattern extraction date:** 2026-05-27

**Cross-references back to upstream docs:**
- CONTEXT D-01..D-04 → BaseController action assignments
- CONTEXT D-05..D-12 → BaseService 6-step + SyncJunctionsAsync hook
- CONTEXT D-13..D-16 → AddBaseApi composition + Program.cs migration
- CONTEXT D-17..D-19 → Asp.Versioning + Swagger + UseBaseApi pipeline
- RESEARCH A-01 → `Asp.Versioning.Mvc 8.1.0` pin (NOT `.Http`)
- RESEARCH Pitfall 3 → BaseService `_dbContext` must be `protected`
- RESEARCH Pitfall 4 → AuditInterceptor lifetime = **Singleton** (Phase 3 D-06 canonical), overriding CONTEXT D-14's Scoped snippet
- RESEARCH Pitfall 9 → NSubstitute 5.3.0 (NOT Moq)
- RESEARCH Pitfall 10 → BaseService is abstract — register concrete subclasses, not the open generic
- RESEARCH Path B → empty `AppDbContext` placeholder satisfies Program.cs ~3-line target
