---
phase: 07-generic-http-base-composition-root
reviewed: 2026-05-27T00:00:00Z
depth: standard
files_reviewed: 33
files_reviewed_list:
  - src/BaseApi.Core/Contracts/IHasId.cs
  - src/BaseApi.Core/Controllers/BaseController.cs
  - src/BaseApi.Core/Services/BaseService.cs
  - src/BaseApi.Core/Swagger/CorrelationIdHeaderOperationFilter.cs
  - src/BaseApi.Core/Swagger/HideHealthEndpointsDocumentFilter.cs
  - src/BaseApi.Core/Swagger/ConfigureSwaggerOptions.cs
  - src/BaseApi.Core/DependencyInjection/PersistenceServiceCollectionExtensions.cs
  - src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs
  - src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs
  - src/BaseApi.Core/DependencyInjection/ErrorHandlingServiceCollectionExtensions.cs
  - src/BaseApi.Core/DependencyInjection/HttpServiceCollectionExtensions.cs
  - src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs
  - src/BaseApi.Core/DependencyInjection/BaseApiApplicationBuilderExtensions.cs
  - src/BaseApi.Service/AppDbContext.cs
  - src/BaseApi.Service/Program.cs
  - Directory.Packages.props
  - src/BaseApi.Core/BaseApi.Core.csproj
  - tests/BaseApi.Tests/BaseApi.Tests.csproj
  - tests/BaseApi.Tests/Validation/TestCreateDtoValidator.cs
  - tests/BaseApi.Tests/Validation/TestDtos.cs
  - tests/BaseApi.Tests/Composition/Phase7TestDbContext.cs
  - tests/BaseApi.Tests/Composition/TestsController.cs
  - tests/BaseApi.Tests/Composition/RecordingTestService.cs
  - tests/BaseApi.Tests/Composition/Phase7WebAppFactory.cs
  - tests/BaseApi.Tests/Composition/ProductionWebAppFactory.cs
  - tests/BaseApi.Tests/Composition/AddBaseApiFacts.cs
  - tests/BaseApi.Tests/Composition/UseBaseApiPipelineFacts.cs
  - tests/BaseApi.Tests/Composition/ProgramMinimalityFacts.cs
  - tests/BaseApi.Tests/Controllers/BaseControllerRoutesFacts.cs
  - tests/BaseApi.Tests/Services/BaseServiceOrderingFacts.cs
  - tests/BaseApi.Tests/Services/NotFoundFacts.cs
  - tests/BaseApi.Tests/Versioning/VersioningFacts.cs
  - tests/BaseApi.Tests/Swagger/SwaggerEnvironmentFacts.cs
findings:
  critical: 0
  warning: 5
  info: 9
  total: 14
status: issues_found
---

# Phase 7: Code Review Report

**Reviewed:** 2026-05-27
**Depth:** standard
**Files Reviewed:** 33
**Status:** issues_found

## Summary

Phase 7 lands a clean, well-documented composition root: the 5 IServiceCollection sub-extensions chain cleanly into the public `AddBaseApi<TDbContext>()` entry, `AddBaseApiObservability` is appropriately split onto `IHostApplicationBuilder` per the D-13 amendment, and `UseBaseApi` keeps the pipeline order locked (UseExceptionHandler first, then CorrelationIdMiddleware, then UseRouting). The abstract generic `BaseController` / `BaseService` pair correctly works around the CS0416 trap by avoiding generic types in `[ProducesResponseType]` attributes, the 6-step `CreateAsync` order is preserved, and the `IHasId` contract elegantly avoids reflection in `CreatedAtAction`. The intentional singleton AuditInterceptor and the scoped `BaseDbContext` alias both check out and are not flagged.

The findings below are mostly defensive-hardening issues plus one latent test-only bug (`BaseServiceOrderingFacts` depends on Npgsql writing a `DateTime.Kind=Unspecified` audit timestamp without the AuditInterceptor wired into the test DbContext — flagged because the project's own PITFALLS.md warns this is the exact failure mode that the legacy-timestamp-switch escape hatch was explicitly forbidden for). Several inconsistencies between `cfg["Service:Name"]!` (NRE-prone) and `cfg["Service:Name"] ?? "fallback"` (defensive) suggest a project-wide pattern decision is overdue. No security vulnerabilities, no DI lifetime captives, no async-over-sync hazards, and no broken generic constraints were identified.

## Warnings

### WR-01: `BaseService._logger` is assigned but never read

**File:** `src/BaseApi.Core/Services/BaseService.cs:33,70`
**Issue:** The `private readonly ILogger _logger` field is injected via the ctor (with a null guard), but it is never referenced anywhere in the class body. The locked 6-step `CreateAsync` order — which the class doc explicitly tracks as load-bearing — has zero observability of which step is running, so a production failure inside `SyncJunctionsAsync` or `SaveChangesAsync` cannot be correlated to a step boundary from logs alone (only from the OTel span hierarchy, which Phase 5 wired). Because the ctor demands the logger, every concrete subclass and every test mock now has to thread `ILogger<BaseService<...>>` through DI even though it is dead state.

This is also a `dotnet build` warning candidate under `CA1823` (unused private field) — the file may only escape it because the analyzer counts the ctor assignment as "use".

**Fix:** Pick one of two paths.

Path A — add minimal structured logging at step boundaries (preferred, justifies the injection):
```csharp
public async Task<TRead> CreateAsync(TCreate dto, CancellationToken ct)
{
    _logger.LogDebug("CreateAsync<{Entity}> step 1: validate", typeof(TEntity).Name);
    await _createValidator.ValidateAndThrowAsync(dto, ct);
    _logger.LogDebug("CreateAsync<{Entity}> step 2-3: map+add", typeof(TEntity).Name);
    var entity = _mapper.ToEntity(dto);
    await _repo.AddAsync(entity, ct);
    _logger.LogDebug("CreateAsync<{Entity}> step 4: sync junctions", typeof(TEntity).Name);
    await SyncJunctionsAsync(entity, dto, default, ct);
    _logger.LogDebug("CreateAsync<{Entity}> step 5: save", typeof(TEntity).Name);
    await DbContext.SaveChangesAsync(ct);
    return _mapper.ToRead(entity);
}
```

Path B — drop the field and the ctor parameter entirely. Concrete services that need logging can take their own typed logger in their own ctor.

---

### WR-02: `BaseServiceOrderingFacts` SaveChanges path writes a `DateTime.Kind=Unspecified` timestamp without the AuditInterceptor wired — Npgsql 8 will throw `InvalidCastException`

**File:** `tests/BaseApi.Tests/Services/BaseServiceOrderingFacts.cs:33-48,57-119`
**Issue:** `InitializeAsync` constructs `Phase7TestDbContext` directly via `new DbContextOptionsBuilder<...>().UseNpgsql(...).UseSnakeCaseNamingConvention().Options` — note the absence of `.AddInterceptors(auditInterceptor)`. The test then drives `service.CreateAsync(dto, ct)` end-to-end. Step 5 (`DbContext.SaveChangesAsync(ct)`) is real (not mocked), and the entity it persists is `mappedEntity = new TestEntity { Id = ..., Name = "x", Version = "1.0.0", Note = "" }` — `CreatedAt` and `UpdatedAt` are `default(DateTime)`, i.e. `Kind=Unspecified`.

`.planning/research/PITFALLS.md:34` explicitly warns: "Do NOT flip the legacy switch — it papers over the bug." The "bug" is exactly this: Npgsql 8 rejects non-UTC writes to `timestamp with time zone` columns. `EnsureCreatedAsync` generates `timestamptz` for `DateTime` properties by default under the Npgsql provider. The Phase 3 `AuditInterceptor` exists precisely to stamp `Kind=Utc` before SaveChanges — and this test runs SaveChanges with no interceptor.

The test reportedly passes today, which means one of: (a) the `default(DateTime)` zero-value triggers a special path inside Npgsql that bypasses the kind check, (b) Postgres silently accepts it because the column has no NOT NULL constraint declared in `EnsureCreated`, or (c) the test silently fails the SaveChanges and the assertions about ordering still happen to evaluate first. None of those are contracts you want a verification test relying on; an upgrade to Npgsql 8.0.10+ or a `timestamptz` schema tightening will break this test in a way that is unrelated to the 6-step ordering it actually proves.

**Fix:** Wire the AuditInterceptor (or a stub that sets `Kind=Utc`) into the test DbContext, OR pre-stamp the entity in the `mapper.ToEntity` substitute:
```csharp
// Option A: pre-stamp in the mock entity so SaveChanges is happy.
var now = DateTime.UtcNow;
var mappedEntity = new TestEntity
{
    Id = Guid.NewGuid(),
    Name = "x", Version = "1.0.0", Note = "",
    CreatedAt = now, UpdatedAt = now,  // Kind=Utc explicitly
};

// Option B: wire the interceptor in InitializeAsync.
var auditInterceptor = new AuditInterceptor(
    TimeProvider.System,
    new StubHttpContextAccessor());
var opts = new DbContextOptionsBuilder<Phase7TestDbContext>()
    .UseNpgsql(_fixture.ConnectionString)
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(auditInterceptor)
    .Options;
```

Option A is the minimum-blast-radius fix — it isolates the SC#2 ordering proof from the audit-stamping contract, which has its own Phase 3 tests.

---

### WR-03: `Service:Name` / `Service:Version` configuration access is inconsistent (some sites NRE on missing key, others fall back)

**File:**
- `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs:41-42` (`cfg["Service:Name"]!`)
- `src/BaseApi.Core/Swagger/ConfigureSwaggerOptions.cs:33` (`cfg["Service:Name"] ?? "sk-api"`)
- `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs:23` (`cfg.GetConnectionString("Postgres")!`)

**Issue:** Three different patterns are in use for "configuration key the boot path needs":

1. Observability — `cfg["Service:Name"]!` + `cfg["Service:Version"]!`. If either key is missing, the null-forgiving operator silences the compiler but the value is `null`. `ResourceBuilder.AddService(serviceName: null, serviceVersion: null)` then throws `ArgumentNullException` inside the OTel SDK with a stack trace that points at the OTel internals, not at the misconfiguration. Boot fails with a non-actionable error.

2. Swagger — graceful fallback to `"sk-api"`.

3. Health — `cfg.GetConnectionString("Postgres")!` passed straight into `AddNpgSql(...)`. Same shape as #1: null-forgiving hides the diagnostic; a missing connection string throws inside the health-check library, not at the boundary.

For an operations-friendly composition root, the boundary should fail fast with a clear message ("required config key 'Service:Name' missing"). Today, the three sites diverge — which one is canonical?

**Fix:** Standardize on a small helper used at all three call sites:
```csharp
// New: src/BaseApi.Core/Configuration/RequiredConfig.cs
internal static class RequiredConfig
{
    public static string Require(this IConfiguration cfg, string key)
        => cfg[key] ?? throw new InvalidOperationException(
            $"Required configuration key '{key}' is missing. Set it via appsettings.json, " +
            $"environment variables, or user secrets. See README.md.");

    public static string RequireConnectionString(this IConfiguration cfg, string name)
        => cfg.GetConnectionString(name) ?? throw new InvalidOperationException(
            $"Required connection string 'ConnectionStrings:{name}' is missing.");
}

// Then in each site:
var serviceName    = cfg.Require("Service:Name");
var serviceVersion = cfg.Require("Service:Version");
// ...
.AddNpgSql(cfg.RequireConnectionString("Postgres"), tags: new[] { "ready" });
```

Apply the same change in `PersistenceServiceCollectionExtensions.AddBaseApiPersistence` (line 28 — `cfg.GetConnectionString("Postgres")` with no `!` or fallback either, so the null propagates into `UseNpgsql` which throws a less-clear error).

---

### WR-04: `ProgramMinimalityFacts.ProgramCsContent()` uses brittle relative path traversal that breaks on alternative build outputs

**File:** `tests/BaseApi.Tests/Composition/ProgramMinimalityFacts.cs:11-17,62-71`
**Issue:** Both helpers compose the source path from `AppContext.BaseDirectory` walked up five segments:
```csharp
Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
             "src", "BaseApi.Service", "Program.cs");
```

This depends on the test assembly running from exactly `tests/BaseApi.Tests/bin/{Configuration}/net8.0/`. Any of the following breaks it silently — the test reports "Program.cs not found at {path}" and fails:

- Multi-targeting (`net8.0;net9.0` adds another path segment under the TFM)
- `AppendTargetFrameworkToOutputPath=false`
- `BaseOutputPath`/`OutputPath` overrides via CI
- Publishing the test as a self-contained executable
- `dotnet test --no-build` from a different cwd (resolves to the same dir, but the file write order is different)

The hardcoded count `..` x 5 is a magic-number layer count.

**Fix:** Anchor on the solution root using a marker file that's guaranteed to be present:
```csharp
private static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SK_P.sln")))
        dir = dir.Parent;
    return dir?.FullName
        ?? throw new InvalidOperationException("Could not locate SK_P.sln walking up from " + AppContext.BaseDirectory);
}

private static string ProgramCsContent()
{
    var path = Path.Combine(FindRepoRoot(), "src", "BaseApi.Service", "Program.cs");
    Assert.True(File.Exists(path), $"Program.cs not found at {path}");
    return File.ReadAllText(path);
}
```

Apply the same change to `ProgramCs_BodyLines_LessThan_OrEqualTo_Ten` (lines 64-66) — it duplicates the same five-level traversal inline.

---

### WR-05: `Phase7WebAppFactory.DisposeAsync` disposes the base WebApplicationFactory before the PostgresFixture, but `await base.DisposeAsync()` may not have fully released DB connections by then — and `DROP DATABASE WITH (FORCE)` only mitigates one failure mode

**File:** `tests/BaseApi.Tests/Composition/Phase7WebAppFactory.cs:67-74`
**Issue:**
```csharp
public override async ValueTask DisposeAsync()
{
    await base.DisposeAsync();
    if (_fixture is not null) await _fixture.DisposeAsync();
}
```

The intent is correct (release DI scopes / DbContexts before dropping the DB), but the ordering interacts poorly with Npgsql's connection pool:

1. `base.DisposeAsync()` disposes the `WebApplication`, which disposes singletons and triggers `IHostedService.StopAsync`. The scoped DbContexts are gone, but Npgsql's `ConnectionPool` keeps physical connections open for reuse (TCP-level connections to localhost:5433).
2. `_fixture.DisposeAsync()` runs `NpgsqlConnection.ClearAllPools()` then `DROP DATABASE ... WITH (FORCE)`. `ClearAllPools` is process-global, so it also clobbers any OTHER test class's pool that happens to be running in parallel (xUnit v3 parallelizes classes by default).

This is the same hazard the Phase 3 `PostgresFixture.DisposeAsync` comment warns about — but at the WebAppFactory level it's worse, because the WebApp's hosted services may still be in their grace-period for shutdown when ClearAllPools fires.

**Fix:** Two options:

Option A (cheapest) — accept the cross-class pool clobber; xUnit's per-class isolation already accepts some flakiness. Add a comment documenting the trade-off.

Option B (correct) — give Phase7WebAppFactory its own connection-string-suffixed application name and only clear THAT app's pool:
```csharp
public override async ValueTask DisposeAsync()
{
    await base.DisposeAsync();
    if (_fixture is not null)
    {
        // Clear only THIS factory's connection pool, not every Npgsql pool in-process,
        // so parallel test classes don't lose their pooled connections.
        var b = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString);
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
            NpgsqlConnection.ClearPool(conn);
        await _fixture.DisposeAsync();
    }
}
```

(Note: `PostgresFixture.DisposeAsync` still calls `ClearAllPools` — that's a Phase 3 concern, not Phase 7. Flagging here because Phase7WebAppFactory's lifecycle compounds the problem.)

## Info

### IN-01: `BaseService.CreateAsync` / `UpdateAsync` pass `default` for the nullable DTO parameters to `SyncJunctionsAsync` — no runtime enforcement that exactly one is non-null

**File:** `src/BaseApi.Core/Services/BaseService.cs:99,111,131-133`
**Issue:** The XML doc on `SyncJunctionsAsync` says "Exactly one of `createDto` or `updateDto` is non-null." This is enforced by convention (CreateAsync passes `(entity, dto, default, ct)`; UpdateAsync passes `(entity, default, dto, ct)`), but a Phase 8 override that accidentally trusts the wrong parameter could ship a silent bug. With record DTOs this is benign (`default` is null), but if a Phase 8 DTO ever becomes a struct, `default` is a real zero-value struct, NOT null — and `createDto is null` checks in overrides would silently misfire.

**Fix:** Either (a) split into two abstract hooks `SyncJunctionsOnCreateAsync(entity, createDto, ct)` and `SyncJunctionsOnUpdateAsync(entity, updateDto, ct)` so the type system enforces which side is in play, or (b) add a class-level `where TCreate : class where TUpdate : class` constraint so `default` is guaranteed to be null. Option (b) is one line and matches today's DTO conventions.

---

### IN-02: `Repository<TEntity>.AddAsync` is `async` for an EF Core operation that is synchronous unless a HiLo/sequence value generator is configured

**File:** `src/BaseApi.Core/Persistence/Repositories/Repository.cs:25-26`
**Issue:** `_set.AddAsync(entity, ct)` only becomes truly async if a HiLo/sequence value generator is in play. Otherwise it completes synchronously, and the `async` wrapper adds a state machine allocation per call. The repository is hot-path (every CREATE goes through it).

**Fix:** EF Core docs recommend `_set.Add(entity)` for non-sequence keys; switch to the sync variant and keep the `Task` return for interface compatibility:
```csharp
public Task AddAsync(TEntity entity, CancellationToken cancellationToken)
{
    _set.Add(entity);
    return Task.CompletedTask;
}
```
(Performance is out of v1 scope, but flagging because the call is per-request hot path.)

---

### IN-03: `AddBaseApiFacts.BuildServices` constructs a temporary ServiceProvider mid-build just to read IConfiguration back

**File:** `tests/BaseApi.Tests/Composition/AddBaseApiFacts.cs:21-31`
**Issue:**
```csharp
services.AddSingleton<IConfiguration>(new ConfigurationBuilder()...Build());
var cfg = services.BuildServiceProvider().GetRequiredService<IConfiguration>();
services.AddBaseApi<AppDbContext>(cfg);
```

The intermediate `BuildServiceProvider()` creates a throwaway scope that allocates every singleton already registered (today: just the configuration), but on future expansion that side effect grows. The cfg variable is identical to the `IConfiguration` instance just registered — there's no need to round-trip through DI.

**Fix:**
```csharp
var cfg = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ... })
    .Build();
services.AddSingleton<IConfiguration>(cfg);
services.AddBaseApi<AppDbContext>(cfg);
```

---

### IN-04: Hardcoded Postgres credentials in `AddBaseApiFacts`

**File:** `tests/BaseApi.Tests/Composition/AddBaseApiFacts.cs:26`
**Issue:** `"Host=localhost;Port=5433;Database=stepsdb_addbaseapi;Username=postgres;Password=postgres"` is a literal string in source. The credentials are throwaway dev creds matching `compose.yaml`, and no test in this file actually opens a DB connection (all assertions are DI graph shape), but the pattern leaks the convention into source-control history and into IDE search results. Phase 8 may grep for these.

**Fix:** Either share a single `TestConnectionStrings.cs` constant file, or generate the string via `NpgsqlConnectionStringBuilder` so the password isn't a literal:
```csharp
private static string TestConnectionString { get; } = new NpgsqlConnectionStringBuilder
{
    Host     = "localhost", Port = 5433,
    Database = "stepsdb_addbaseapi",
    Username = Environment.GetEnvironmentVariable("POSTGRES_USER")     ?? "postgres",
    Password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres",
}.ConnectionString;
```

---

### IN-05: Per-test factory construction creates a fresh PostgresFixture per fact across `UseBaseApiPipelineFacts`, `SwaggerEnvironmentFacts`, `VersioningFacts`, `NotFoundFacts`

**File:** All four files (e.g. `tests/BaseApi.Tests/Versioning/VersioningFacts.cs:14-17,28-31`)
**Issue:** Every `[Fact]` does:
```csharp
await using var factory = new Phase7WebAppFactory();
await factory.InitializeAsync();
using var client = factory.CreateClient();
```

Each instantiation spins up a new throwaway Postgres database (CREATE DATABASE + EnsureCreatedAsync + DROP on dispose) and a fresh in-process TestServer. Across the four files plus their facts, that's ~7-8 Postgres DBs created/dropped per test run. With xUnit v3 parallelizing classes, this stacks load on the Phase 2 container.

**Fix:** Hoist `Phase7WebAppFactory` into a class fixture (`IClassFixture<Phase7WebAppFactory>`) per test class. Each class still gets isolation; facts within a class share the factory. Phase 8's integration tests will hit this pattern at scale — fixing now is cheaper.

---

### IN-06: `SwaggerEnvironmentFacts` uses `using` instead of `await using` for `ProductionWebAppFactory`

**File:** `tests/BaseApi.Tests/Swagger/SwaggerEnvironmentFacts.cs:45,49,60,64`
**Issue:** `using var factory = new ProductionWebAppFactory();` — `WebApplicationFactory<T>` implements both `IDisposable` and `IAsyncDisposable`. `using` invokes the synchronous Dispose, which can deadlock or skip async cleanup paths (e.g., async hosted service stop). The two `Phase7WebAppFactory` consumers in sibling files correctly use `await using`. Consistency matters.

**Fix:**
```csharp
await using var factory = new ProductionWebAppFactory();
```

---

### IN-07: `Phase7WebAppFactory.ConfigureWebHost` calls `services.AddControllers().AddApplicationPart(...)` after the base class already registered the same app part

**File:** `tests/BaseApi.Tests/Composition/Phase7WebAppFactory.cs:95`
**Issue:** `WebAppFactory.ConfigureWebHost` (the base class, `tests/BaseApi.Tests/Middleware/WebAppFactory.cs:37-52`) already calls `services.AddControllers().AddApplicationPart(typeof(WebAppFactory).Assembly)`. `Phase7WebAppFactory.ConfigureWebHost` first calls `base.ConfigureWebHost(builder)` then registers the SAME assembly part again at line 95. ApplicationPartManager dedupes assembly parts by reference equality, so this is harmless — but the doc on the line says "so TestsController is discovered", which misleads the next reader into thinking this call is load-bearing. It's a no-op.

**Fix:** Drop the duplicate line, or change the comment to: "redundant — base class already registers this assembly; kept for documentation."

---

### IN-08: `BaseService` exception messages for missing validators reference `typeof(TCreate).Name` and `typeof(TUpdate).Name` — closed generic at runtime works, but the assertion that `AddValidatorsFromAssembly` would discover a class named "$Name + Validator" is implicit

**File:** `src/BaseApi.Core/Services/BaseService.cs:57-66`
**Issue:** The error messages instruct the consumer to "inherit `AbstractValidator<TCreate>`" — good — but `typeof(TCreate).Name` returns the simple name (e.g. `TestCreateDto`), not the namespace. A consumer with name collisions across assemblies (which the test project already has: `BaseApi.Tests.Persistence.TestEntity` vs `BaseApi.Tests.Validation.TestEntity`) will read this error and grep for the wrong type.

**Fix:** Use `typeof(TCreate).FullName` (or the C#-style readable name via `typeof(TCreate)` only):
```csharp
$"No IValidator<{typeof(TCreate).FullName}> registered. ..."
```

---

### IN-09: `Phase7WebAppFactory` is `public class` (not `sealed`), unlike every other test-side type in the same directory

**File:** `tests/BaseApi.Tests/Composition/Phase7WebAppFactory.cs:47`
**Issue:** `TestsController`, `RecordingTestService`, `Phase7TestDbContext`, `ProductionWebAppFactory` are all `sealed`. `Phase7WebAppFactory` is `public class`. The class is never extended (no subclasses in the repo) — sealing tightens the contract and matches the convention.

**Fix:** `public sealed class Phase7WebAppFactory : WebAppFactory, IAsyncLifetime`.

---

_Reviewed: 2026-05-27_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
