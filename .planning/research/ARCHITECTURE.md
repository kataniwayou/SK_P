# Architecture Research

**Domain:** .NET 8 Web API modular monolith with reusable base library (`BaseApi.Core`) + runnable service (`BaseApi.Service`)
**Researched:** 2026-05-26
**Confidence:** HIGH

## Standard Architecture

### System Overview

```
                            HTTP request (PUT /api/processors/{id})
                                          │
┌─────────────────────────────────────────┼────────────────────────────────────┐
│  BaseApi.Service.exe (ASP.NET Core)     │                                    │
├─────────────────────────────────────────┼────────────────────────────────────┤
│                              MIDDLEWARE PIPELINE                              │
│  ┌──────────────────────────────────────▼──────────────────────────────────┐ │
│  │ UseSerilogRequest? → UseExceptionHandler → UseCorrelationId →            │ │
│  │ UseRouting → UseCors → UseAuthorization → MapControllers + MapHealth     │ │
│  └──────────────────────────────────────┬──────────────────────────────────┘ │
│                                          │                                    │
├──────────────────────────────────────────┼────────────────────────────────────┤
│                          PRESENTATION LAYER (HTTP shells)                     │
│  ┌────────────────┐  ┌────────────────┐  ▼──────────────────┐  ┌───────────┐ │
│  │ SchemasCtrl    │  │ ProcessorsCtrl │  │ StepsCtrl         │  │ ...etc    │ │
│  │ : BaseCtrl<…>  │  │ : BaseCtrl<…>  │  │ : BaseCtrl<…>     │  │           │ │
│  └────────┬───────┘  └────────┬───────┘  └────────┬──────────┘  └─────┬─────┘ │
│           │                   │                   │                   │       │
├───────────┼───────────────────┼───────────────────┼───────────────────┼───────┤
│           │     SERVICE LAYER (entity-specific logic + M2M sync)      │       │
│  ┌────────▼───────┐  ┌────────▼───────┐  ┌────────▼──────────┐ ┌─────▼─────┐ │
│  │ SchemaService  │  │ ProcessorSvc   │  │ StepService       │ │ ...       │ │
│  │ : Service<…>   │  │ : Service<…>   │  │ : Service<…>      │ │           │ │
│  └────────┬───────┘  └────────┬───────┘  └────────┬──────────┘ └─────┬─────┘ │
│           │                   │                   │                   │       │
│           ├─ Validator<T>  ───┤   ├─ Mapper       │                   │       │
│           ├─ Mapper        ───┤   ├─ M2M sync     │                   │       │
│           ▼                   ▼                   ▼                   ▼       │
├───────────────────────────────────────────────────────────────────────────────┤
│                        PERSISTENCE LAYER (generic)                            │
│  ┌─────────────────────────────────────────────────────────────────────────┐ │
│  │   Repository<TEntity> : IRepository<TEntity>                             │ │
│  │   ── Get / List / Add / Update / Delete on AppDbContext.Set<TEntity>()  │ │
│  └────────────────────────────────────┬────────────────────────────────────┘ │
│                                        │                                      │
│  ┌────────────────────────────────────▼────────────────────────────────────┐ │
│  │   AppDbContext : BaseDbContext                                           │ │
│  │   ── DbSet<Schema>, DbSet<Processor>, DbSet<Step>, ...                  │ │
│  │   ── ApplyConfigurationsFromAssembly  → IEntityTypeConfiguration<T>     │ │
│  │   ── AuditInterceptor (ISaveChangesInterceptor)                         │ │
│  └────────────────────────────────────┬────────────────────────────────────┘ │
│                                        │                                      │
├────────────────────────────────────────┼──────────────────────────────────────┤
│                CROSS-CUTTING (lives in BaseApi.Core, wired by AddBaseApi)     │
│  CorrelationId mw │ Problem-Details exc handler │ OTel logs+metrics+traces   │
│  Health checks    │ Swagger                     │ Logging filters (single)   │
└────────────────────────────────────────┼──────────────────────────────────────┘
                                          ▼
                              ┌────────────────────────────┐
                              │  PostgreSQL  (Docker)       │
                              │  schema: public             │
                              │  jsonb cols, uuid PKs       │
                              │  FKs enforced               │
                              └────────────────────────────┘
                                          ▲
                                          │ OTLP/gRPC
                              ┌───────────┴────────────────┐
                              │  External OTel Collector    │
                              └────────────────────────────┘
```

### Component Responsibilities

| Component | Lives in | Responsibility |
|-----------|----------|----------------|
| `BaseEntity` (abstract) | `BaseApi.Core` | Id, Name, Version, CreatedAt, UpdatedAt, CreatedBy?, UpdatedBy?, Description? — no `[Table]`, not in any DbSet |
| Concrete entities (`SchemaEntity`, `ProcessorEntity`, ...) | `BaseApi.Core/Entities/` (locked decision) | Per-PROJECT.md, entities live in `Core`. Each derives from `BaseEntity` and adds entity-specific scalar/FK fields |
| `BaseDbContext` (abstract) | `BaseApi.Core` | Generic helpers (e.g., `OnConfiguring` defaults, `ApplyAuditInterceptor`, `ApplyBaseConventions`) — no DbSets |
| `AppDbContext` | `BaseApi.Service` | Concrete `DbSet<>` for every entity + every junction; calls `modelBuilder.ApplyConfigurationsFromAssembly(...)` for per-entity `IEntityTypeConfiguration<T>` files |
| `AuditInterceptor : ISaveChangesInterceptor` | `BaseApi.Core` | Stamps `CreatedAt`/`UpdatedAt`/`CreatedBy`/`UpdatedBy` on `Added`/`Modified` state |
| `Repository<TEntity> : IRepository<TEntity>` | `BaseApi.Core` | `GetAsync(id, ct)`, `ListAsync(ct)`, `AddAsync(entity, ct)`, `UpdateAsync(entity, ct)`, `DeleteAsync(id, ct)`. No `SaveChangesAsync` on individual ops — caller (service) decides commit boundary |
| `BaseController<TEntity, TCreate, TUpdate, TRead>` | `BaseApi.Core` | Abstract `[ApiController]` with `[Route("api/[controller]")]`. Calls into `IService<…>`. Returns `ActionResult<TRead>` with ProblemDetails on failure |
| `BaseService<TEntity, TCreate, TUpdate, TRead>` (abstract or default) | `BaseApi.Core` | Validates → maps DTO→Entity → repository.Add/Update → SaveChanges (audit fires) → maps Entity→ReadDto. Virtual `SyncManyToManyAsync` hook (override for entities with M2M) |
| Per-entity controllers, services, validators, mappers, DTOs | `BaseApi.Service` | Plug-in points; one folder per entity (vertical slice) |
| `CorrelationIdMiddleware` | `BaseApi.Core` | Reads `X-Correlation-Id` or generates GUID; pushes to log scope; sets header on response |
| `ProblemDetails` exception handler (`IExceptionHandler`) | `BaseApi.Core` | Catches everything; maps SQLSTATE 23503→422, 23505→409, `NotFoundException`→404, `ValidationException`→400, otherwise 500. Adds `correlationId` extension |
| Health check endpoints | `BaseApi.Core` (registration in `AddBaseApi`) | `/health/live`, `/health/ready` (incl. `AddDbContextCheck<TDbContext>`), `/health/startup` |
| OTel wiring | `BaseApi.Core` extension method | `AddOpenTelemetry()` with `ResourceBuilder.AddService(name, version)`, `AddAspNetCoreInstrumentation`, `AddOtlpExporter`, MEL logging provider |

## Recommended Project Structure

**Decision: feature folders inside `BaseApi.Service`, layer folders inside `BaseApi.Core`.** Rationale: the base library is genuinely layered infrastructure (controllers, repos, middleware) and there are no features in it. The service has features (entities) and each feature touches every layer — feature folders eliminate the "where does the validator live" hunt.

```
SK_P/                                            # repo root, no Visual Studio convention dir
├── SK_P.sln                                     # solution at repo root
├── docker-compose.yml                           # postgres + service
├── docker-compose.override.yml                  # local-dev only (volumes, ports)
├── .planning/                                   # GSD artifacts (already exists)
│
├── src/
│   ├── BaseApi.Core/                            # reusable class library, no entry point
│   │   ├── BaseApi.Core.csproj                  # <TargetFramework>net8.0</TargetFramework>
│   │   ├── Entities/                            # LOCKED: entities live in Core per PROJECT.md
│   │   │   ├── BaseEntity.cs                    # abstract, no [Table]
│   │   │   ├── SchemaEntity.cs
│   │   │   ├── ProcessorEntity.cs
│   │   │   ├── StepEntity.cs
│   │   │   ├── AssignmentEntity.cs
│   │   │   ├── WorkflowEntity.cs
│   │   │   └── Junctions/
│   │   │       ├── StepNextStep.cs              # explicit join entity (see Pattern 4)
│   │   │       ├── WorkflowEntryStep.cs
│   │   │       └── WorkflowAssignment.cs
│   │   ├── Persistence/
│   │   │   ├── BaseDbContext.cs                 # abstract; OnConfiguring defaults, no DbSets
│   │   │   ├── Interceptors/
│   │   │   │   └── AuditInterceptor.cs          # ISaveChangesInterceptor
│   │   │   └── Repositories/
│   │   │       ├── IRepository.cs               # generic CRUD contract
│   │   │       └── Repository.cs                # EF Core impl over TDbContext + TEntity
│   │   ├── Services/
│   │   │   ├── IService.cs                      # generic service contract
│   │   │   └── BaseService.cs                   # default impl; virtual SyncManyToManyAsync
│   │   ├── Controllers/
│   │   │   └── BaseController.cs                # abstract [ApiController] generic
│   │   ├── Validation/
│   │   │   └── BaseEntityValidator.cs           # FluentValidation rules for BaseEntity
│   │   ├── Middleware/
│   │   │   ├── CorrelationIdMiddleware.cs
│   │   │   └── CorrelationIdOptions.cs
│   │   ├── ErrorHandling/
│   │   │   ├── GlobalExceptionHandler.cs        # IExceptionHandler
│   │   │   ├── PostgresErrorMapper.cs           # SQLSTATE → status
│   │   │   ├── NotFoundException.cs
│   │   │   ├── ConflictException.cs
│   │   │   └── ProblemDetailsExtensions.cs      # adds correlationId
│   │   ├── Health/
│   │   │   └── HealthCheckExtensions.cs         # MapBaseHealthChecks → /health/live, /health/ready, /health/startup
│   │   ├── Telemetry/
│   │   │   ├── TelemetryExtensions.cs           # AddBaseTelemetry<TDbContext>
│   │   │   └── ActivitySources.cs               # static ActivitySource("steps-api")
│   │   └── DependencyInjection/
│   │       ├── BaseApiOptions.cs                # bound from "BaseApi" config section
│   │       └── BaseApiServiceCollectionExtensions.cs  # AddBaseApi<TDbContext>(this IServiceCollection, IConfiguration)
│   │
│   └── BaseApi.Service/                         # the runnable webapi
│       ├── BaseApi.Service.csproj               # <Sdk>Microsoft.NET.Sdk.Web</Sdk>, refs BaseApi.Core
│       ├── Program.cs                           # ~30 lines: builder.Services.AddBaseApi<AppDbContext>(...)
│       ├── appsettings.json
│       ├── appsettings.Development.json
│       ├── Dockerfile
│       │
│       ├── Persistence/
│       │   ├── AppDbContext.cs                  # DbSets for every entity + junction
│       │   └── Configurations/                  # IEntityTypeConfiguration<T> per entity + per junction
│       │       ├── SchemaConfiguration.cs       # jsonb mapping, index on Name
│       │       ├── ProcessorConfiguration.cs    # unique index on SourceHash
│       │       ├── StepConfiguration.cs         # self-ref M2M via StepNextStep
│       │       ├── AssignmentConfiguration.cs   # jsonb for Payload
│       │       ├── WorkflowConfiguration.cs     # M2M to Step + Assignment
│       │       ├── StepNextStepConfiguration.cs
│       │       ├── WorkflowEntryStepConfiguration.cs
│       │       └── WorkflowAssignmentConfiguration.cs
│       │
│       ├── Migrations/                          # generated by dotnet ef migrations add
│       │
│       └── Features/                            # FEATURE folder per entity
│           ├── Schemas/
│           │   ├── SchemasController.cs         # : BaseController<SchemaEntity, CreateSchemaDto, UpdateSchemaDto, ReadSchemaDto>
│           │   ├── SchemaService.cs             # : BaseService<…>
│           │   ├── SchemaMapper.cs              # [Mapper] partial class — Mapperly
│           │   ├── SchemaValidator.cs           # : BaseEntityValidator<SchemaEntity>
│           │   └── Dtos/
│           │       ├── CreateSchemaDto.cs
│           │       ├── UpdateSchemaDto.cs
│           │       └── ReadSchemaDto.cs
│           ├── Processors/                      # same shape
│           ├── Steps/
│           ├── Assignments/
│           └── Workflows/
│
└── tests/
    ├── BaseApi.Core.Tests/                      # xUnit unit tests for base components
    │   ├── BaseApi.Core.Tests.csproj
    │   └── Persistence/AuditInterceptorTests.cs
    │   └── ErrorHandling/PostgresErrorMapperTests.cs
    │   └── Middleware/CorrelationIdMiddlewareTests.cs
    └── BaseApi.Service.Tests/                   # integration tests using WebApplicationFactory + Testcontainers.PostgreSql
        ├── BaseApi.Service.Tests.csproj
        ├── Fixtures/PostgresFixture.cs          # Testcontainers
        └── Features/Schemas/SchemasIntegrationTests.cs
```

### Structure Rationale

- **`BaseApi.Core/Entities/`** — locked by PROJECT.md. Entities live in Core so the base controller's `TEntity : BaseEntity` constraint and `Repository<T>` can be authored against them. AppDbContext (which knows all DbSets) still lives in Service.
- **`BaseApi.Service/Features/<Entity>/`** — feature folders. Everything entity-specific (controller, service, validator, mapper, DTOs) co-locates. Touch one feature = open one folder.
- **`BaseApi.Service/Persistence/Configurations/`** — `IEntityTypeConfiguration<T>` per entity, picked up by `ApplyConfigurationsFromAssembly`. Keeps `AppDbContext.OnModelCreating` to a one-line scan call.
- **`tests/` outside `src/`** — standard .NET solution convention.
- **No `Domain/Infrastructure/Application/Web` Clean-Architecture split** — overkill for a CRUD-only service. The Core ↔ Service two-project split is sufficient and matches the locked decision.

## Architectural Patterns

### Pattern 1: Composition Root via `AddBaseApi<TDbContext>(...)`

**What:** One extension method in `BaseApi.Core` that registers every cross-cutting concern. `Program.cs` becomes ~30 lines because it just composes.

**Why:** Single source of registration order — middleware order, DI lifetimes, OTel pipeline, problem details, health checks. Consumer just supplies the concrete `TDbContext`.

**Shape:**

```csharp
// BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs
public static IServiceCollection AddBaseApi<TDbContext>(
    this IServiceCollection services,
    IConfiguration configuration)
    where TDbContext : BaseDbContext
{
    // 1. Options
    services.Configure<BaseApiOptions>(configuration.GetSection("BaseApi"));

    // 2. DbContext + interceptor
    services.AddSingleton<AuditInterceptor>();
    services.AddDbContext<TDbContext>((sp, opts) =>
    {
        opts.UseNpgsql(configuration.GetConnectionString("Postgres"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
    });

    // 3. Generic repository — open generic registration
    services.AddScoped(typeof(IRepository<>), typeof(Repository<>));

    // 4. ProblemDetails + exception handler
    services.AddProblemDetails(opts => opts.CustomizeProblemDetails = ctx =>
        ctx.ProblemDetails.Extensions["correlationId"] = ctx.HttpContext.GetCorrelationId());
    services.AddExceptionHandler<GlobalExceptionHandler>();

    // 5. Controllers + FluentValidation (validators auto-registered from the calling assembly)
    services.AddControllers();

    // 6. Health checks
    services.AddHealthChecks()
        .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
        .AddDbContextCheck<TDbContext>("postgres", tags: new[] { "ready" });

    // 7. OTel
    services.AddBaseTelemetry(configuration);

    return services;
}

public static IApplicationBuilder UseBaseApi(this WebApplication app)
{
    app.UseExceptionHandler();           // (1) catch first
    app.UseCorrelationId();              // (2) attach correlationId to log scope + response
    app.UseRouting();                    // (3)
    app.UseCors();                       // (4) (no policy yet, placeholder)
    // (no UseAuthentication/UseAuthorization in v1 — no auth)
    app.MapControllers();
    app.MapBaseHealthChecks();           // /health/live, /health/ready, /health/startup
    return app;
}
```

Consumer (`BaseApi.Service/Program.cs`):

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBaseApi<AppDbContext>(builder.Configuration);
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);   // Service's validators

// Wire per-entity Services (a one-liner each, or scanned via convention)
builder.Services.AddScoped<IService<SchemaEntity, CreateSchemaDto, UpdateSchemaDto, ReadSchemaDto>, SchemaService>();
// ... or use Scrutor for assembly scanning

var app = builder.Build();

// Apply migrations on startup (PROJECT.md requirement)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseBaseApi();
app.Run();
```

**Trade-offs:** Convention-heavy. Concrete services and controllers still need per-entity registration unless you add assembly scanning (Scrutor). For 5 entities, manual registration is fine.

### Pattern 2: Generic Base Controller (abstract `BaseController<TEntity, TCreate, TUpdate, TRead>`)

**What:** One abstract controller in `BaseApi.Core` handles all CRUD verbs. Concrete controllers in `BaseApi.Service` are empty derived classes that supply the four generic parameters and inherit the route.

**Exact signature:**

```csharp
// BaseApi.Core/Controllers/BaseController.cs
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public abstract class BaseController<TEntity, TCreate, TUpdate, TRead> : ControllerBase
    where TEntity : BaseEntity
{
    protected IService<TEntity, TCreate, TUpdate, TRead> Service { get; }

    protected BaseController(IService<TEntity, TCreate, TUpdate, TRead> service) => Service = service;

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TRead>), StatusCodes.Status200OK)]
    public virtual async Task<ActionResult<IEnumerable<TRead>>> List(CancellationToken ct)
        => Ok(await Service.ListAsync(ct));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TRead), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<TRead>> Get(Guid id, CancellationToken ct)
        => Ok(await Service.GetAsync(id, ct));   // NotFoundException → 404 ProblemDetails via handler

    [HttpPost]
    [ProducesResponseType(typeof(TRead), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public virtual async Task<ActionResult<TRead>> Create([FromBody] TCreate dto, CancellationToken ct)
    {
        var created = await Service.CreateAsync(dto, ct);
        return CreatedAtAction(nameof(Get), new { id = created!.GetType().GetProperty("Id")!.GetValue(created) }, created);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(TRead), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public virtual async Task<ActionResult<TRead>> Update(Guid id, [FromBody] TUpdate dto, CancellationToken ct)
        => Ok(await Service.UpdateAsync(id, dto, ct));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public virtual async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await Service.DeleteAsync(id, ct);
        return NoContent();
    }
}
```

Concrete controller in `BaseApi.Service`:

```csharp
// BaseApi.Service/Features/Processors/ProcessorsController.cs
public sealed class ProcessorsController
    : BaseController<ProcessorEntity, CreateProcessorDto, UpdateProcessorDto, ReadProcessorDto>
{
    public ProcessorsController(
        IService<ProcessorEntity, CreateProcessorDto, UpdateProcessorDto, ReadProcessorDto> service)
        : base(service) { }
}
```

**Routing convention:** `[Route("api/[controller]")]` on the base, ASP.NET Core token replacement automatically picks up the concrete name (`ProcessorsController` → `api/processors`). Lowercase URLs enabled in `Program.cs` via `app.UseRouting()` defaults / `services.AddRouting(o => o.LowercaseUrls = true)`.

**Why virtual not non-virtual:** Lets the rare entity override (e.g., a custom GET that filters by hash) replace one verb without rewriting the whole controller.

**Trade-offs:** No `[HttpHead]`, no pagination/filtering on List (PROJECT.md out-of-scope for v1). When added, extend base — every concrete picks it up free.

### Pattern 3: Generic Service with Virtual M2M Hook

**What:** A default `BaseService<…>` in `BaseApi.Core` handles the validate → map → repository → save flow. Entities with junction tables override one hook.

**Shape:**

```csharp
// BaseApi.Core/Services/IService.cs
public interface IService<TEntity, TCreate, TUpdate, TRead> where TEntity : BaseEntity
{
    Task<IReadOnlyList<TRead>> ListAsync(CancellationToken ct);
    Task<TRead> GetAsync(Guid id, CancellationToken ct);
    Task<TRead> CreateAsync(TCreate dto, CancellationToken ct);
    Task<TRead> UpdateAsync(Guid id, TUpdate dto, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

// BaseApi.Core/Services/BaseService.cs
public abstract class BaseService<TEntity, TCreate, TUpdate, TRead> : IService<…>
    where TEntity : BaseEntity, new()
{
    protected IRepository<TEntity> Repo { get; }
    protected IValidator<TCreate> CreateValidator { get; }
    protected IValidator<TUpdate> UpdateValidator { get; }
    protected DbContext Db { get; }

    protected BaseService(IRepository<TEntity> repo, IValidator<TCreate> cv, IValidator<TUpdate> uv, DbContext db)
        => (Repo, CreateValidator, UpdateValidator, Db) = (repo, cv, uv, db);

    public virtual async Task<TRead> CreateAsync(TCreate dto, CancellationToken ct)
    {
        await CreateValidator.ValidateAndThrowAsync(dto, ct);
        var entity = MapToEntity(dto);
        await Repo.AddAsync(entity, ct);
        await SyncJunctionsAsync(entity, dto, ct);     // virtual no-op by default
        await Db.SaveChangesAsync(ct);                 // audit interceptor fires here
        return MapToRead(entity);
    }

    public virtual async Task<TRead> UpdateAsync(Guid id, TUpdate dto, CancellationToken ct)
    {
        await UpdateValidator.ValidateAndThrowAsync(dto, ct);
        var entity = await Repo.GetAsync(id, ct) ?? throw new NotFoundException(typeof(TEntity).Name, id);
        ApplyUpdate(entity, dto);
        await SyncJunctionsAsync(entity, dto, ct);     // M2M replace semantics
        await Db.SaveChangesAsync(ct);
        return MapToRead(entity);
    }

    // Mapperly hooks — abstract because per-entity mappers are source-generated
    protected abstract TEntity MapToEntity(TCreate dto);
    protected abstract void ApplyUpdate(TEntity entity, TUpdate dto);
    protected abstract TRead MapToRead(TEntity entity);

    // M2M sync — virtual no-op; Workflow + Step override
    protected virtual Task SyncJunctionsAsync(TEntity entity, object dto, CancellationToken ct) => Task.CompletedTask;
}
```

Concrete `WorkflowService` overrides `SyncJunctionsAsync` to replace `WorkflowEntrySteps` + `WorkflowAssignments` rows on Create/Update.

**Why service holds validation+mapping, not controller:** Keeps the controller a one-line forwarder; tests can call service directly; cancellation tokens flow naturally end-to-end. The model-binding pipeline still rejects malformed JSON before the controller (400 from ASP.NET Core).

### Pattern 4: Per-Entity `IEntityTypeConfiguration<T>` + Explicit Junction Entities

**What:** Each entity has its own configuration class in `BaseApi.Service/Persistence/Configurations/`. `AppDbContext.OnModelCreating` is one line: `modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly)`.

For M2M (including self-ref `StepEntity.NextStepIds`), use **explicit join entities** (not implicit skip-nav-only). PROJECT.md mandates "scalar Guid FKs, junction tables, no navigation properties between bounded contexts."

```csharp
// BaseApi.Core/Entities/Junctions/StepNextStep.cs
public sealed class StepNextStep
{
    public Guid StepId { get; set; }
    public Guid NextStepId { get; set; }
}

// BaseApi.Service/Persistence/Configurations/StepNextStepConfiguration.cs
public sealed class StepNextStepConfiguration : IEntityTypeConfiguration<StepNextStep>
{
    public void Configure(EntityTypeBuilder<StepNextStep> b)
    {
        b.ToTable("step_next_steps");                       // snake_case via plugin
        b.HasKey(x => new { x.StepId, x.NextStepId });
        b.HasOne<StepEntity>().WithMany().HasForeignKey(x => x.StepId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<StepEntity>().WithMany().HasForeignKey(x => x.NextStepId).OnDelete(DeleteBehavior.Restrict);
    }
}

// BaseApi.Service/Persistence/Configurations/SchemaConfiguration.cs
public sealed class SchemaConfiguration : IEntityTypeConfiguration<SchemaEntity>
{
    public void Configure(EntityTypeBuilder<SchemaEntity> b)
    {
        b.ToTable("schemas");
        b.HasKey(x => x.Id);
        b.Property(x => x.Definition).HasColumnType("jsonb");   // PostgreSQL jsonb
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Version).HasMaxLength(50).IsRequired();
        b.HasIndex(x => x.Name);                                // (Name, Version) NOT unique per PROJECT.md
    }
}
```

**Why explicit join entity over skip-nav-only:** PROJECT.md mandates "no navigation properties between bounded contexts." Explicit join entities give DB-enforced FKs without forcing nav properties on `StepEntity` (which is also helpful for the service-layer `SyncJunctionsAsync` — you `Add`/`Remove` join rows directly through `DbContext.Set<StepNextStep>()`).

### Pattern 5: Audit Interceptor (`ISaveChangesInterceptor`)

**What:** A singleton interceptor that stamps `CreatedAt`/`UpdatedAt`/`CreatedBy`/`UpdatedBy` on every `Added`/`Modified` entity that derives from `BaseEntity`. Registered with the DbContext in `AddBaseApi`.

```csharp
// BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs
public sealed class AuditInterceptor : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor _http;
    public AuditInterceptor(IHttpContextAccessor http) => _http = http;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData ed, InterceptionResult<int> result, CancellationToken ct = default)
    {
        var ctx = ed.Context ?? throw new InvalidOperationException();
        var now = DateTime.UtcNow;
        var user = _http.HttpContext?.User?.Identity?.IsAuthenticated == true
            ? _http.HttpContext.User.Identity!.Name : null;

        foreach (var entry in ctx.ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.CreatedBy = user;
                    entry.Entity.UpdatedBy = user;
                    break;
                case EntityState.Modified:
                    entry.Property(nameof(BaseEntity.CreatedAt)).IsModified = false;
                    entry.Property(nameof(BaseEntity.CreatedBy)).IsModified = false;
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = user;
                    break;
            }
        }
        return base.SavingChangesAsync(ed, result, ct);
    }
}
```

**Why interceptor, not in `BaseService`:** Centralized — works for direct repository writes, future bulk operations, and any path that calls `SaveChangesAsync` (incl. M2M junction inserts that aren't `BaseEntity`).

### Pattern 6: Global `IExceptionHandler` → ProblemDetails with SQLSTATE mapping

**What:** Single `IExceptionHandler` registered via `AddExceptionHandler<GlobalExceptionHandler>` + `UseExceptionHandler()`. Maps domain exceptions and Postgres SQLSTATE codes to RFC 7807/9457 ProblemDetails. Adds `correlationId` extension.

```csharp
// BaseApi.Core/ErrorHandling/GlobalExceptionHandler.cs
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var pd = ex switch
        {
            ValidationException ve => new ValidationProblemDetails(ve.ToDict()) { Status = 400, Title = "Validation failed" },
            NotFoundException nf   => new ProblemDetails { Status = 404, Title = "Not found", Detail = nf.Message },
            ConflictException cf   => new ProblemDetails { Status = 409, Title = "Conflict",  Detail = cf.Message },
            DbUpdateException due  => PostgresErrorMapper.Map(due),   // 23503 → 422, 23505 → 409
            _                      => new ProblemDetails { Status = 500, Title = "Unexpected error" }
        };
        pd.Extensions["correlationId"] = ctx.GetCorrelationId();
        ctx.Response.StatusCode = pd.Status ?? 500;
        await ctx.Response.WriteAsJsonAsync(pd, ct);
        return true;
    }
}
```

## Data Flow

### Request Flow (POST /api/processors end-to-end)

```
1. HTTP POST /api/processors {body: CreateProcessorDto}
        │
        ▼
2. Middleware pipeline (in order)
   ├── UseExceptionHandler          (wraps everything below; catches downstream throws)
   ├── UseCorrelationId             (reads X-Correlation-Id or generates GUID; pushes
   │                                 to ILogger.BeginScope and Response.Headers)
   ├── UseRouting                   (matches route → ProcessorsController.Create)
   ├── UseCors                      (placeholder; no policy in v1)
   └── MapControllers
        │
        ▼
3. ASP.NET model binding
   ├── Reads body → deserializes JSON → CreateProcessorDto
   └── Fails fast on malformed JSON → ProblemDetails 400 (built-in)
        │
        ▼
4. ProcessorsController.Create(dto, ct)
   └── Inherited from BaseController; one-line forward → Service.CreateAsync(dto, ct)
        │
        ▼
5. ProcessorService.CreateAsync(dto, ct) → BaseService.CreateAsync
   ├── CreateProcessorValidator.ValidateAndThrowAsync(dto, ct)
   │       (FluentValidation: SHA-256 regex on SourceHash, Name max 200, SemVer Version)
   │       throws ValidationException → handler → 400 ValidationProblemDetails
   ├── ProcessorMapper.ToEntity(dto)           (Mapperly source-generated, compile-time)
   ├── repository.AddAsync(entity, ct)         (sets entity on AppDbContext.Set<>().Add)
   ├── SyncJunctionsAsync(...)                 (no-op for Processor)
   └── AppDbContext.SaveChangesAsync(ct)
            │
            ▼
6. EF Core save path
   ├── AuditInterceptor.SavingChangesAsync(...)  (stamps CreatedAt, UpdatedAt, CreatedBy?, UpdatedBy?)
   └── Npgsql executes INSERT INTO processors (...)
            │
            ├─ success → entity now has DB-assigned defaults
            ├─ SQLSTATE 23505 (unique violation on SourceHash) → DbUpdateException
            │       → handler → 409 ProblemDetails with offending field
            └─ SQLSTATE 23503 (FK violation on input_schema_id / output_schema_id) → DbUpdateException
                    → handler → 422 ProblemDetails with offending field
        │
        ▼
7. BaseService.MapToRead(entity)               (Mapperly entity → ReadProcessorDto)
        │
        ▼
8. BaseController returns CreatedAtAction → 201
   ├── Location: /api/processors/{id}
   ├── Body: ReadProcessorDto JSON
   └── X-Correlation-Id response header (set by CorrelationIdMiddleware on the way out)
```

### Telemetry flow (concurrent with request)

```
Request enters
    │
    ├──► ASP.NET Core auto-creates an Activity (root span, name "POST api/processors")
    │       via AddAspNetCoreInstrumentation()
    │
    ├──► ILogger scope includes correlationId + trace_id + span_id (MEL → OTel logging provider)
    │
    └──► HTTP server metrics (http.server.request.duration, http.server.request.count)
            via AddAspNetCoreInstrumentation() (metrics)

On SaveChangesAsync
    └──► No EF Core instrumentation in v1 (omit AddEntityFrameworkCoreInstrumentation; can add later)

On response
    └──► OTLP exporter pushes:
         ├── logs   (server-side filtered by Logging:LogLevel — single source)
         ├── metrics
         └── traces (in v1 traces are auto; custom ActivitySource("steps-api") declared but
                     not used until a feature needs it)
    Target: OTEL_EXPORTER_OTLP_ENDPOINT (gRPC :4317) → external OTel Collector
```

## Middleware Ordering (precise)

ASP.NET Core middleware runs in the order it is added; the first registered is the outermost. Build the pipeline outside-in:

```csharp
app.UseExceptionHandler();        // 1. outermost; catches every downstream throw → ProblemDetails
app.UseCorrelationId();           // 2. before logging scope so log lines carry the id; before
                                  //    error response writes so the id is in the body. Must run
                                  //    INSIDE the exception handler (the handler will read
                                  //    the id back via HttpContext.Items["correlationId"]).
app.UseRouting();                 // 3. routing must be before authz and endpoints
app.UseCors();                    // 4. after routing, before endpoints (no policy v1; placeholder)
// app.UseAuthentication();       // -- OMITTED v1
// app.UseAuthorization();        // -- OMITTED v1
app.MapControllers();             // 5. endpoint resolution
app.MapBaseHealthChecks();        //    /health/live, /health/ready, /health/startup
```

**Critical:** `UseExceptionHandler` must come before `UseCorrelationId`. Microsoft's documented pipeline puts exception handling first. However, the exception handler needs access to the correlationId; achieve this by:
- Having `CorrelationIdMiddleware` push the id into `HttpContext.Items["correlationId"]` as the very first thing it does.
- Having `GlobalExceptionHandler` read `HttpContext.Items["correlationId"]` (which is still there because the handler runs on the same `HttpContext`).

This is the well-known pattern: outermost exception handler, second correlation middleware, but both have access to `HttpContext.Items`.

## Telemetry Integration

**Order of operations in `AddBaseApi` → `AddBaseTelemetry`:**

```csharp
public static IServiceCollection AddBaseTelemetry(this IServiceCollection services, IConfiguration cfg)
{
    var serviceName    = cfg["Service:Name"]    ?? "base-api";       // "steps-api"
    var serviceVersion = cfg["Service:Version"] ?? "0.0.0";          // "3.2.0"

    var resource = ResourceBuilder.CreateDefault()
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion)
        .AddAttributes(new[] { new KeyValuePair<string, object>("deployment.environment", cfg["ASPNETCORE_ENVIRONMENT"] ?? "production") });

    services.AddOpenTelemetry()
        .ConfigureResource(_ => _.AddService(serviceName, serviceVersion))
        .WithMetrics(m => m
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter())
        .WithTracing(t => t
            .AddSource("steps-api")                      // custom ActivitySource for future use
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter());

    // Logs: MEL → OTel logging provider. Logging:LogLevel filters apply because
    // they bind at the MEL pipeline; both Console + OTel sinks read the same filtered stream.
    services.AddLogging(b =>
    {
        b.AddOpenTelemetry(o =>
        {
            o.SetResourceBuilder(resource);
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;                       // pulls correlationId scope into logs
            o.AddOtlpExporter();
        });
    });

    return services;
}
```

**Why this hits PROJECT.md:**
- `Service:Name`/`Service:Version` from appsettings → `service.name`/`service.version` resource attrs (REQ).
- `Logging:LogLevel` single source: both Console and OTel sinks register on the MEL `ILoggingBuilder`, so the filter applies before either sink writes (REQ).
- OTLP target via `OTEL_EXPORTER_OTLP_ENDPOINT`: `AddOtlpExporter()` with no options uses env vars (REQ).
- Custom `ActivitySource("steps-api")` declared in `ActivitySources.cs` and registered via `AddSource("steps-api")` — ready to use the moment any feature wants custom spans.

## Persistence Layer Specifics

- **`AppDbContext.OnModelCreating`:** single line `modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);`. Per-entity config classes implement `IEntityTypeConfiguration<T>`. Order is undefined by design — entity configs must not depend on each other.
- **Naming convention:** `EFCore.NamingConventions` NuGet → `optionsBuilder.UseSnakeCaseNamingConvention()`. `SchemaEntity` → table `schemas`, `CreatedAt` → column `created_at`, junction `StepNextStep` → table `step_next_steps`. PostgreSQL convention without quoting headaches.
- **`Guid` PKs:** Npgsql maps `Guid` → PostgreSQL `uuid` natively. Default value strategy: do **not** use `HasDefaultValueSql("gen_random_uuid()")`; instead set in `BaseService.CreateAsync` (`entity.Id = Guid.NewGuid()`) so the audit interceptor and the DB see the same value before save (also keeps Mapperly mappers explicit).
- **jsonb columns:** `Schema.Definition` and `Assignment.Payload` use `HasColumnType("jsonb")`. Type in C# is `JsonDocument` (or `string` if validation will work on the raw text — PROJECT.md says `Schema.Definition` is validated as valid JSON + valid JSON Schema; using `string` keeps the FluentValidation rule simple and avoids `JsonDocument` disposal concerns). **Recommendation: `string`** stored as `jsonb` (PostgreSQL still validates that the value is well-formed JSON at insert time as a backstop).
- **Migration naming:** `dotnet ef migrations add InitialCreate`, then per-change descriptive names (`Add_StepNextSteps_Junction`, `Add_Processor_SourceHash_UniqueIndex`). Run from `BaseApi.Service` project, output dir `Migrations/`.
- **Migration application on startup:** in `Program.cs` after `app.Build()`, before middleware: `scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();`. Acceptable for single-instance deployment per PROJECT.md scope. **Mitigation note:** wrap in try/catch with `app.Lifetime.StopApplication()` on failure so the readiness probe never reports healthy if migrations failed.

## Naming Conventions (lock in)

| Surface | Convention | Example |
|---------|-----------|---------|
| C# class/type names | PascalCase | `ProcessorEntity`, `BaseController` |
| C# property/method names | PascalCase | `SourceHash`, `CreateAsync` |
| C# local/parameter names | camelCase | `processorId`, `cancellationToken` |
| Async method names | `…Async` suffix | `GetAsync`, `SaveChangesAsync` |
| Folders | PascalCase | `Persistence/Configurations/` |
| HTTP routes | lowercase | `/api/processors`, `/health/live` |
| PostgreSQL tables | snake_case (plugin) | `processors`, `step_next_steps` |
| PostgreSQL columns | snake_case (plugin) | `source_hash`, `created_at` |
| Migration files | descriptive | `20260526_AddProcessorSourceHashIndex.cs` |
| Configuration keys (appsettings) | PascalCase | `Service:Name`, `BaseApi:Cors:AllowedOrigins` |
| Environment variables (overrides) | UPPER_SNAKE | `OTEL_EXPORTER_OTLP_ENDPOINT`, `ConnectionStrings__Postgres` |

## Docker & Compose

**`BaseApi.Service/Dockerfile`** (multistage; SDK builds, runtime runs):

```dockerfile
# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 1. Restore (cacheable on .csproj-only changes)
COPY src/BaseApi.Core/BaseApi.Core.csproj      src/BaseApi.Core/
COPY src/BaseApi.Service/BaseApi.Service.csproj src/BaseApi.Service/
RUN dotnet restore src/BaseApi.Service/BaseApi.Service.csproj

# 2. Build + publish
COPY src/ src/
RUN dotnet publish src/BaseApi.Service/BaseApi.Service.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# 3. Runtime stage — aspnet, NOT sdk (~210MB vs ~900MB)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
COPY --from=build /app/publish .
USER $APP_UID                              # non-root user shipped by the aspnet image
ENTRYPOINT ["dotnet", "BaseApi.Service.dll"]
```

**`docker-compose.yml`** (postgres + service, with healthcheck-gated start):

```yaml
services:
  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: baseapi
      POSTGRES_USER: baseapi
      POSTGRES_PASSWORD: baseapi
    ports:
      - "5432:5432"
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U baseapi -d baseapi"]
      interval: 5s
      timeout: 3s
      retries: 10
      start_period: 10s

  steps-api:
    build:
      context: .
      dockerfile: src/BaseApi.Service/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ConnectionStrings__Postgres: Host=postgres;Database=baseapi;Username=baseapi;Password=baseapi
      OTEL_EXPORTER_OTLP_ENDPOINT: http://otel-collector:4317
      Service__Name: steps-api
      Service__Version: 3.2.0
    ports:
      - "8080:8080"
    depends_on:
      postgres:
        condition: service_healthy

volumes:
  pgdata:
```

## Build Order for the Milestone (suggested phase decomposition)

These dependencies are forced by the code — building in this order means every later phase compiles against finished, working earlier phases.

```
Phase A: Solution skeleton + Core scaffolding
   1. SK_P.sln, src/BaseApi.Core/, src/BaseApi.Service/, tests/ projects + refs
   2. BaseEntity (no deps)
   3. NotFoundException, ConflictException, ProblemDetailsExtensions (no deps)
   4. BaseApiOptions + appsettings.json skeleton

Phase B: Persistence base (depends A)
   5. BaseDbContext (abstract; no DbSets)
   6. AuditInterceptor                       ◄── ISaveChangesInterceptor, uses IHttpContextAccessor
   7. IRepository<T>, Repository<T>          ◄── uses TDbContext from generic; takes DbContext via DI

Phase C: Cross-cutting (depends A)
   8. CorrelationIdMiddleware + Options + extensions
   9. GlobalExceptionHandler + PostgresErrorMapper
  10. Health check extensions (MapBaseHealthChecks: live/ready/startup)
  11. TelemetryExtensions (OTel logs/metrics/traces wiring) + ActivitySources

Phase D: Generic HTTP base (depends B, C)
  12. IService<…>, BaseService<…>           ◄── uses IRepository, IValidator, Mapperly hooks
  13. BaseController<…>                      ◄── uses IService
  14. BaseEntityValidator<T>                 ◄── FluentValidation rules for shared fields

Phase E: Composition root (depends B, C, D)
  15. AddBaseApi<TDbContext> + UseBaseApi    ◄── pulls everything together

Phase F: Concrete entities + AppDbContext (depends Phase A entity files)
  16. The 5 concrete entity classes (Schema, Processor, Step, Assignment, Workflow)
      + 3 junction classes (StepNextStep, WorkflowEntryStep, WorkflowAssignment)
      (live in BaseApi.Core/Entities/ per locked decision)
  17. AppDbContext in BaseApi.Service (DbSets, single-line OnModelCreating)
  18. 8x IEntityTypeConfiguration<T> in BaseApi.Service/Persistence/Configurations/

Phase G: Features (parallelizable across the 5 entities — depends D, F)
  For each entity:
     19. CreateDto, UpdateDto, ReadDto
     20. Mapperly [Mapper] partial class
     21. FluentValidation validator (: BaseEntityValidator<TEntity>)
     22. Service (: BaseService<…>, overrides SyncJunctionsAsync if M2M)
     23. Controller (: BaseController<…>, empty body)
     24. DI registration

Phase H: Migrations + Docker + Compose (depends F, G)
  25. dotnet ef migrations add InitialCreate
  26. Database.Migrate() call in Program.cs
  27. Dockerfile (multistage, aspnet:8.0)
  28. docker-compose.yml with healthcheck + depends_on
  29. Bring it up; smoke-test CRUD against all 5 entities
```

**Critical "X before Y" couplings:**
- `BaseEntity` before `AuditInterceptor` (interceptor scans `BaseEntity` entries)
- `AuditInterceptor` before `AppDbContext` registration (wired as DbContext option)
- `IRepository<T>` before `BaseService<…>` (service injects repo)
- `IService<…>` before `BaseController<…>` (controller injects service)
- `CorrelationIdMiddleware` (in pipeline) before `GlobalExceptionHandler` reads correlation id from `HttpContext.Items` — but `UseExceptionHandler()` is registered **first** in DI; the middleware just sets `HttpContext.Items["correlationId"]` before downstream code, and the handler reads it
- All entities + AppDbContext before `dotnet ef migrations add` (EF needs the model to emit DDL)
- AppDbContext + migrations before `Database.Migrate()` on startup

## Scaling Considerations

| Scale | Architecture Adjustments |
|-------|--------------------------|
| 0-1k req/day | Single container, single Postgres instance. v1 design is exactly this. |
| 1k-100k req/day | Add response caching for GET list endpoints (out of v1). Tune Npgsql connection pool. Consider read replica if list endpoints dominate. |
| 100k+ req/day | At this scale you re-evaluate single-DbContext: per-entity bounded contexts may need their own DBs. Out of v1. |

### Scaling Priorities

1. **First bottleneck:** Postgres connections. Npgsql pool default 100 — fine for single-instance, watch on horizontal scale.
2. **Second bottleneck:** `List` endpoints with no pagination — already flagged in PROJECT.md as out-of-scope-v1; add cursor pagination to `BaseController.List` when needed (every concrete controller picks it up free).
3. **Migration-on-startup conflict** if you ever run multiple replicas: switch to migrate-as-init-container or a separate migration step.

## Anti-Patterns

### Anti-Pattern 1: Putting per-entity bits in BaseApi.Core

**What people do:** "Just one Mapper here in Core, since it's reusable" — except it isn't.
**Why it's wrong:** Couples the base library to concrete entities; every new entity now needs a Core release.
**Do this instead:** Entities live in Core (locked PROJECT.md decision because the abstract controller's type parameter must resolve), but everything entity-specific (DTOs, mappers, validators, services, controllers, configurations) lives in `BaseApi.Service/Features/<Entity>/`.

### Anti-Pattern 2: Putting validation in the controller

**What people do:** `[FromBody] CreateDto dto` + `if (!ModelState.IsValid) return BadRequest(...)`.
**Why it's wrong:** Half the validation lives at the HTTP edge, half in the service; tests of the service path don't catch the same errors as integration tests.
**Do this instead:** Validation lives in the service (`BaseService.CreateAsync` calls `CreateValidator.ValidateAndThrowAsync`). FluentValidation throws `ValidationException`, the global handler converts to 400 `ValidationProblemDetails`. Controllers are forwarders.

### Anti-Pattern 3: Repository with `SaveChangesAsync` inside each method

**What people do:** `Add → SaveChangesAsync` inside `Repository.AddAsync`.
**Why it's wrong:** Service can't compose multiple writes in a single transaction; M2M sync becomes "add header, save; add joins, save" (two transactions, partial-fail risk).
**Do this instead:** `Repository` operations stage changes; the service calls `DbContext.SaveChangesAsync` once at the end (and the audit interceptor stamps everything in one go). For the rare unit-of-work need, inject `DbContext` directly into the service.

### Anti-Pattern 4: Skip-nav-only M2M when PROJECT mandates explicit FKs

**What people do:** `HasMany(...).WithMany(...)` without `UsingEntity<T>(...)`.
**Why it's wrong:** PROJECT.md mandates "no navigation properties between bounded contexts" + "DB-level FK constraints." Skip-nav-only generates a shadow join entity you can't reference from the service for M2M sync, and adds navigation properties to `StepEntity`/`WorkflowEntity` that leak coupling.
**Do this instead:** Explicit join entity classes (`StepNextStep`, `WorkflowEntryStep`, `WorkflowAssignment`) configured as their own `IEntityTypeConfiguration<T>` with composite keys + explicit FKs + `DeleteBehavior`. Service queries `DbContext.Set<StepNextStep>()` directly for M2M sync.

### Anti-Pattern 5: `Logging:LogLevel` filters duplicated for console + OTel

**What people do:** Set `Logging:LogLevel:Default` for console and a separate filter inside `AddOpenTelemetry().WithLogging(o => o.IncludeFormattedMessage = ...)`.
**Why it's wrong:** Two sources of truth; you'll silently disagree.
**Do this instead:** Register OTel logging on the **same** `ILoggingBuilder` (`b.AddOpenTelemetry(...)`). MEL filters apply *before* any provider runs — Console and OTel see the same filtered stream. PROJECT.md REQ.

## Integration Points

### External Services

| Service | Integration Pattern | Notes |
|---------|---------------------|-------|
| PostgreSQL 16+ | Npgsql + EF Core 8 | Single DB, single DbContext; connection string from `ConnectionStrings:Postgres`; FK constraints enforced at DB level (PROJECT.md REQ) |
| OTel Collector | OTLP/gRPC over `OTEL_EXPORTER_OTLP_ENDPOINT` env var | External Collector handles fan-out; gRPC port 4317; `Grpc.Net.Client` package required for .NET 8 (npgsql/efcore.pg note: needed for OTLP gRPC) |

### Internal Boundaries

| Boundary | Communication | Notes |
|----------|---------------|-------|
| `BaseApi.Core` ↔ `BaseApi.Service` | project reference (Core → no refs; Service → refs Core) | Core never references Service. Service supplies the concrete `TDbContext` and per-entity files. |
| Controller ↔ Service | constructor-injected `IService<…>` | Both generic; the four type parameters propagate up |
| Service ↔ Repository | constructor-injected `IRepository<TEntity>` | Open-generic DI (`AddScoped(typeof(IRepository<>), typeof(Repository<>))`) — register once, every entity gets one |
| Service ↔ DbContext | constructor-injected `DbContext` (or `TDbContext` if the service needs entity-specific DbSets for M2M sync) | The M2M-sync overrides need `DbContext.Set<StepNextStep>()` etc. |
| Junction sync | service writes directly to `DbContext.Set<TJunction>()` | No "JunctionRepository"; junctions are not aggregates |

## Sources

- [Modular Monolith Architecture in .NET - Complete Guide (2026) - Milan Jovanović](https://www.milanjovanovic.tech/blog/modular-monolith-architecture-dotnet)
- [Modular Architecture in ASP.NET Core - codewithmukesh](https://codewithmukesh.com/blog/modular-architecture-in-aspnet-core/)
- [Building Modular Monoliths with .NET 8 - Asma's Blog](https://www.asmak9.com/2025/09/building-modular-monoliths-with-net-8.html)
- [EF Core Interceptors: SaveChangesInterceptor for Auditing - Mehmet Ozkaya](https://mehmetozkaya.medium.com/ef-core-interceptors-savechangesinterceptor-for-auditing-entities-in-net-8-microservices-6923190a03b9)
- [EF Core Interceptors - Microsoft Learn](https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors)
- [Base Entities and Base Controllers in C# - Eliezer Kibet](https://eliezerkibet.dev/blog/base-entities-and-base-controllers-csharp-dotnet)
- [Build a Generic CRUD API with ASP.NET Core - DEV](https://dev.to/guivern/build-a-generic-crud-api-with-asp-net-core-adf)
- [Generic Repository with EF Core in .NET Core 8 - Jaimin Shethiya](https://medium.com/@jaimin_99136/generic-repository-with-ef-core-in-net-core-8-3e7a249b439a)
- [Custom NET8 Entity Framework Core Generic Repository - DEV](https://dev.to/angelodotnet/custom-net8-entity-framework-core-generic-repository-35mn)
- [Error handling in ASP.NET Core Web API (.NET 8) with FluentValidation and RFC 7807/9457 - Mykola Aleksandrov](https://www.mykolaaleksandrov.dev/posts/2025/08/error-handling-webapi-dotnet8/)
- [Handling Exceptions with IExceptionHandler in ASP.NET Core 8 - okyrylchuk](https://okyrylchuk.dev/blog/handling-exceptions-in-asp-net-core-8/)
- [Handle errors in ASP.NET Core - Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-8.0)
- [How to Implement Correlation ID Tracing in ASP.NET Core - OneUptime](https://oneuptime.com/blog/post/2026-01-25-correlation-id-tracing-aspnet-core/view)
- [OTLP Exporter for OpenTelemetry .NET - GitHub](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md)
- [.NET Observability with OpenTelemetry - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel)
- [Resources in OpenTelemetry .NET - OpenTelemetry](https://opentelemetry.io/docs/languages/dotnet/resources/)
- [Add distributed tracing instrumentation - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs)
- [ModelBuilder.ApplyConfigurationsFromAssembly - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.modelbuilder.applyconfigurationsfromassembly?view=efcore-8.0)
- [Many-to-many relationships - EF Core - Microsoft Learn](https://learn.microsoft.com/en-us/ef/core/modeling/relationships/many-to-many)
- [EFCore.NamingConventions - GitHub](https://github.com/efcore/EFCore.NamingConventions)
- [JSON Mapping - Npgsql Documentation](https://www.npgsql.org/efcore/mapping/json.html)
- [Health checks in ASP.NET Core - Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0)
- [Mapperly - GitHub](https://github.com/riok/mapperly)
- [Dependency Injection - FluentValidation documentation](https://docs.fluentvalidation.net/en/latest/di.html)
- [Docker Multi-Stage Builds for .NET - Steve Bang](https://www.steve-bang.com/blog/docker-multi-stage-builds-dotnet)
- [Control startup and shutdown order in Compose - Docker Docs](https://docs.docker.com/compose/how-tos/startup-order/)
- [Applying Migrations - EF Core - Microsoft Learn](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying)

---
*Architecture research for: .NET 8 Web API modular monolith + reusable base library*
*Researched: 2026-05-26*
