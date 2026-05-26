# Stack Research

**Domain:** .NET 8 Web API — modular monolith CRUD service over PostgreSQL with shared base library, OTel observability, RFC 7807 errors
**Researched:** 2026-05-26
**Confidence:** HIGH (versions verified against NuGet.org and vendor docs within last 30 days)

## Executive Summary

The locked decisions (.NET 8 + EF Core 8 + Npgsql + Mapperly + FluentValidation + OTel + Postgres) are all on **current, fully-supported, post-stable** versions as of May 2026. Every pin below has been verified against NuGet.org or the official vendor docs. Two pivots are required vs. older code patterns:

1. **FluentValidation.AspNetCore is dead.** Use `FluentValidation` + `FluentValidation.DependencyInjectionExtensions` v12.1.1. Auto-validation is gone — inject `IValidator<T>` and call `ValidateAsync` from the service layer (or use an action filter in the base library that does the same).
2. **EF Core 8.x patch must track .NET 8.x patch.** Both shipped 8.0.27 on 2026-05-12 — pin them together. Npgsql's EF provider lives on its own cadence (latest 8.0.10).

PostgreSQL 17 is the right Docker pin: 18 is GA (May 2026) but the Npgsql 8.x provider was developed against 14–17. PostgreSQL 17-alpine is the production-safe pick; 18-alpine is acceptable but Npgsql 8.x has had no public testing against 18.

## Recommended Stack

### Core Technologies

| Technology | Version | Purpose | Why Recommended |
|------------|---------|---------|-----------------|
| .NET SDK | **8.0.421** (Runtime **8.0.27**) | Build + runtime | Latest LTS patch (2026-05-12). LTS support runs through November 2026. SDK 8.0.421 ships .NET Runtime 8.0.27. Pin via `global.json` to avoid surprise floats. |
| ASP.NET Core | **8.0.27** (in-box with runtime) | HTTP host, controllers, model binding | User-locked. Use controllers (not Minimal APIs) because abstract `BaseController<TEntity,TCreateDto,TUpdateDto,TReadDto>` requires class inheritance — Minimal APIs are function-based and cannot inherit. |
| EF Core | **Microsoft.EntityFrameworkCore 8.0.27** + `Microsoft.EntityFrameworkCore.Design 8.0.27` + `Microsoft.EntityFrameworkCore.Relational 8.0.27` | ORM, migrations | LTS aligned to runtime patch (both shipped 2026-05-12). `Design` package needed only for `dotnet ef` tooling — pin to same patch. |
| Npgsql EF Provider | **Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10** | PostgreSQL ↔ EF Core 8 bridge | Latest 8.0.x. Native `Guid ↔ uuid` mapping (locked decision), native `jsonb` mapping (needed for `SchemaEntity.Definition` and `AssignmentEntity.Payload`), audit-interceptor hooks via `ISaveChangesInterceptor`. Targets EF Core 8.x explicitly; do not cross-pin to EF Core 9/10. |
| PostgreSQL | **`postgres:17-alpine`** (current floating tag → 17.6 as of May 2026) | Data store | PostgreSQL 17 is in the 5-year support window (until Nov 2029). Npgsql 8.x targets PG 14–17. 18 is GA but provider-side validation lags. Alpine variant is ~80MB vs ~430MB Debian and is the standard dev/CI pick. |
| Mapperly | **Riok.Mapperly 4.3.1** | Entity ↔ DTO mapping (source-gen) | Latest stable. Source generator → zero runtime reflection, AOT-safe, faster than AutoMapper, no DI registration of mappers. One `[Mapper] partial` class per entity (matches locked decision). |
| FluentValidation | **FluentValidation 12.1.1** + **FluentValidation.DependencyInjectionExtensions 12.1.1** | Validation | Latest stable. v12 is the line that **removed** the deprecated `AddFluentValidation` auto-pipeline. The DI extensions package provides `AddValidatorsFromAssembly(...)`. See "FluentValidation 12 Wiring Pattern" section below. |
| OpenTelemetry SDK | **OpenTelemetry 1.15.3** + **OpenTelemetry.Extensions.Hosting 1.15.3** | OTel core + IHostedService integration | Logs + Metrics + Tracing all stable in this line (Logs went stable in 2023). Hosting extension wires up `MeterProvider` / `LoggerProvider` / `TracerProvider` to ASP.NET Core `IHost`. |
| OTLP Exporter | **OpenTelemetry.Exporter.OpenTelemetryProtocol 1.15.3** | Export to external OTel Collector via gRPC/HTTP | User-locked exporter target. Reads `OTEL_EXPORTER_OTLP_ENDPOINT` automatically. Default protocol is gRPC; switch to HTTP/protobuf via `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` if Collector is HTTP-only. |
| AspNetCore Instrumentation | **OpenTelemetry.Instrumentation.AspNetCore 1.15.0** | HTTP server metrics (`http.server.request.duration`, etc.) | Locked requirement — HTTP metrics. Stable, recommended by OTel project. |
| HttpClient Instrumentation | **OpenTelemetry.Instrumentation.Http 1.15.0** | Outbound HTTP metrics | Pair with AspNetCore instrumentation. Required even if not directly used today — junction-table sync may eventually involve outbound calls; cheaper to wire now. |

### Supporting Libraries

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| **JsonSchema.Net** | **9.2.1** | Validate `SchemaEntity.Definition` is a valid JSON Schema document | Always — used by `SchemaEntityValidator`. Owned by Greg Dennis (json-everything). System.Text.Json native (matches ASP.NET Core 8 default), supports JSON Schema drafts 6, 7, 2019-09, and **2020-12** (the current spec). Pick this over NJsonSchema. |
| **Cronos** | **0.13.0** | Validate `WorkflowEntity.CronExpression` parses | Always — used by `WorkflowEntityValidator`. By HangfireIO. Proper timezone + DST handling. Standard 5-field cron with optional seconds. Pick this over NCrontab. |
| **AspNetCore.HealthChecks.NpgSql** | **9.0.0** (Xabaril) | Readiness probe — verify Postgres reachability | Always — required by locked readiness-probe decision. Wraps Npgsql with a `SELECT 1` round-trip. Note: v9.0.0 takes a hard dep on `Microsoft.Extensions.Diagnostics.HealthChecks >= 8.0.11`, which is satisfied automatically when running on .NET 8.0.27. |
| **Microsoft.Extensions.Diagnostics.HealthChecks** | (in-box with .NET 8) | Liveness/Startup/Readiness probe framework | Always. `AddHealthChecks()` + `MapHealthChecks("/health/live", ...)` etc. No NuGet ref needed — included by `Microsoft.NET.Sdk.Web`. |
| **Microsoft.AspNetCore.Mvc.Testing** | **8.0.27** | `WebApplicationFactory<TProgram>` for integration tests | When writing integration tests. Pin to the same patch as the runtime. |
| **xUnit v3** | **xunit.v3 3.2.2** + `xunit.v3.assert` + `xunit.runner.visualstudio` | Test framework | xUnit v3 is stable as of late 2024 and the recommended line for new projects in 2026. Native Microsoft.Testing.Platform support (faster than VSTest). Targets .NET 8+. |
| **Testcontainers.PostgreSql** | **4.11.0** | Spin up real Postgres for integration tests | When the test needs DB. Manages container lifecycle via `IAsyncLifetime`. Targets .NET 8. Optional companion: `Testcontainers.XunitV3` (shared-context fixture wrapper) — only useful if you have many test classes; raw `IAsyncLifetime` is fine for fewer. |
| **Npgsql.OpenTelemetry** | **8.0.4** (paired to Npgsql 8.x) | DB-tracing instrumentation for Npgsql | Optional in v1 — locked requirement is logs + HTTP metrics, not DB traces. Add later if Postgres latency becomes a concern. Listed here to document the right version-pair if/when adopted. |

### Development Tools

| Tool | Purpose | Notes |
|------|---------|-------|
| `dotnet-ef` global tool | EF Core migration CLI | Install: `dotnet tool install --global dotnet-ef --version 8.0.27`. Pin to match `Microsoft.EntityFrameworkCore.Design` patch. |
| `global.json` | SDK pin | Required to prevent floating to .NET 9/10 SDKs on dev machines. Pin to `8.0.421` with `rollForward: latestFeature`. |
| `.editorconfig` | Style + nullable enforcement | Enable nullable reference types project-wide (`<Nullable>enable</Nullable>` in csproj). |
| `Directory.Packages.props` | Central package management | Recommended for monorepo with `BaseApi.Core` + `BaseApi.Service` to keep versions identical across projects. |
| `docker-compose.yml` | Local dev | Postgres + service. Use `postgres:17-alpine`, mount data at `/var/lib/postgresql/data`. |

## Installation

`global.json` (root of repo):

```json
{
  "sdk": {
    "version": "8.0.421",
    "rollForward": "latestFeature"
  }
}
```

`Directory.Packages.props` (root of repo, central package management):

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <!-- EF Core (keep .NET runtime + EF Core in lockstep) -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="8.0.27" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="8.0.27" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="8.0.27" />
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="8.0.10" />

    <!-- Mapping (source-gen) -->
    <PackageVersion Include="Riok.Mapperly" Version="4.3.1" />

    <!-- Validation -->
    <PackageVersion Include="FluentValidation" Version="12.1.1" />
    <PackageVersion Include="FluentValidation.DependencyInjectionExtensions" Version="12.1.1" />

    <!-- Observability -->
    <PackageVersion Include="OpenTelemetry" Version="1.15.3" />
    <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.15.3" />
    <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.15.3" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.15.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.15.0" />

    <!-- Domain validators -->
    <PackageVersion Include="JsonSchema.Net" Version="9.2.1" />
    <PackageVersion Include="Cronos" Version="0.13.0" />

    <!-- Health -->
    <PackageVersion Include="AspNetCore.HealthChecks.NpgSql" Version="9.0.0" />

    <!-- Tests -->
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="8.0.27" />
    <PackageVersion Include="xunit.v3" Version="3.2.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.7" />
    <PackageVersion Include="Testcontainers.PostgreSql" Version="4.11.0" />
  </ItemGroup>
</Project>
```

`src/BaseApi.Core/BaseApi.Core.csproj` — relevant fragments:

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <LangVersion>12</LangVersion>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Microsoft.EntityFrameworkCore" />
  <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
  <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
  <PackageReference Include="FluentValidation" />
  <PackageReference Include="FluentValidation.DependencyInjectionExtensions" />
  <PackageReference Include="OpenTelemetry" />
  <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
  <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
  <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
  <PackageReference Include="OpenTelemetry.Instrumentation.Http" />
  <PackageReference Include="AspNetCore.HealthChecks.NpgSql" />
</ItemGroup>
```

`src/BaseApi.Service/BaseApi.Service.csproj` — relevant fragments (the service adds Mapperly, JsonSchema.Net, Cronos, and the EF Core Design package for migrations):

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <LangVersion>12</LangVersion>
</PropertyGroup>

<ItemGroup>
  <ProjectReference Include="..\BaseApi.Core\BaseApi.Core.csproj" />

  <!-- Mapperly: source generator — exclude runtime + mark private -->
  <PackageReference Include="Riok.Mapperly" PrivateAssets="all" ExcludeAssets="runtime" />

  <!-- Domain validators -->
  <PackageReference Include="JsonSchema.Net" />
  <PackageReference Include="Cronos" />

  <!-- Migrations tooling (Design package — for dotnet ef) -->
  <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
</ItemGroup>
```

Install `dotnet-ef`:

```powershell
dotnet tool install --global dotnet-ef --version 8.0.27
```

## FluentValidation 12 Wiring Pattern (modern, non-deprecated)

This is the highest-risk drift from training-data muscle memory. The legacy pattern from the .NET 6/7 era is **removed** in v12.

**Do this in `BaseApi.Core` (in `AddBaseApi(IServiceCollection, ...)` extension):**

```csharp
using FluentValidation;

services.AddValidatorsFromAssembly(
    typeof(BaseEntityValidator<>).Assembly,
    lifetime: ServiceLifetime.Scoped); // scans BaseApi.Core

// In BaseApi.Service Program.cs, also scan the service assembly:
services.AddValidatorsFromAssembly(typeof(Program).Assembly, ServiceLifetime.Scoped);
```

**Do NOT do this (all removed/deprecated in v12):**

```csharp
// REMOVED — package FluentValidation.AspNetCore is deprecated and no longer maintained
services.AddFluentValidation(...);

// REMOVED — the entire FluentValidation.AspNetCore auto-validation pipeline is gone
// (it used to integrate with ModelState automatically)
```

**Invocation pattern — call validators explicitly from the Service layer (or a base controller filter):**

```csharp
// Option A — in BaseService<TEntity, TCreate, TUpdate, TRead>.CreateAsync:
public async Task<TRead> CreateAsync(TCreate dto, CancellationToken ct)
{
    var validationResult = await _createValidator.ValidateAsync(dto, ct);
    if (!validationResult.IsValid)
        throw new ValidationException(validationResult.Errors); // mapped by RFC 7807 middleware to 400

    var entity = _mapper.ToEntity(dto);
    await _repository.AddAsync(entity, ct);
    return _mapper.ToRead(entity);
}
```

```csharp
// Option B — IAsyncActionFilter in BaseApi.Core that runs before BaseController action methods
// resolves IValidator<TDto> from DI and validates the bound model. Equivalent behavior to the
// old auto-pipeline but explicit and under your control.
```

Either pattern is correct for v12. Option A is simpler and lives in the service layer where the locked layering specifies entity-specific logic belongs.

## Alternatives Considered

| Recommended | Alternative | When to Use Alternative |
|-------------|-------------|-------------------------|
| Controllers (`BaseController<...>` inheritance) | Minimal APIs | Never for this project — Minimal APIs are functions, not classes; cannot inherit from a generic base. Reconsider only if the inheritance model is abandoned. |
| EF Core 8 + Npgsql 8.x | EF Core 9 / 10 | If/when migrating to .NET 9/10 LTS. .NET 8 LTS ends Nov 2026, so this becomes the natural migration once SK_P is on the path to .NET 10 LTS (Nov 2025 release, Nov 2028 EOL). |
| Mapperly | AutoMapper | **Never** for this project — explicitly locked out. AutoMapper has been licensed/commercial since late 2024 and remains slower (runtime reflection) and not AOT-safe. |
| Mapperly | Manual `ToDto()` extension methods | Acceptable for entities with <5 properties or one-off DTOs. For the 5 entities here, Mapperly's source-gen is lower-friction than 15 hand-written methods. |
| FluentValidation | DataAnnotations attributes | **Never** for this project — locked out. DataAnnotations cannot inherit cleanly into a `BaseEntityValidator<T>` pattern, and the SemVer regex / per-entity rules compose poorly with attributes. |
| FluentValidation | MiniValidation (Minimal API helper) | Never — coupled to Minimal APIs. |
| `postgres:17-alpine` | `postgres:18-alpine` | If the team wants async I/O and OAuth 2.0 features new in PG 18, and is willing to be the early adopter of Npgsql-on-PG18. Wait one Npgsql minor release before adopting. |
| `postgres:17-alpine` | `postgres:16-alpine` | If staging/prod already runs PG 16 (its support extends to Nov 2028). Behavior is identical for this CRUD-only workload. |
| JsonSchema.Net | NJsonSchema | If you also need C#/TypeScript code generation from a JSON Schema. NJsonSchema has wider draft v4+ support but ships with more dependencies and is slower at validation. |
| JsonSchema.Net | Newtonsoft.Json.Schema | **Never** — commercial license over 10K validations/hour and ties you to Newtonsoft.Json. ASP.NET Core 8 is System.Text.Json by default. |
| Cronos | NCrontab | If you only need the simplest 5-field cron without DST awareness. Cronos is strictly more capable. |
| xUnit v3 | NUnit 4 | Both viable in 2026. Pick xUnit v3 for consistency with the ASP.NET Core / Microsoft.Testing.Platform default. |
| xUnit v3 | xUnit v2 (2.x) | If a corporate template forces v2. v3 is the recommended line; v2 still receives critical fixes but no new features. |
| Testcontainers | In-memory provider (`UseInMemoryDatabase`) | **Never** for integration tests — the in-memory provider doesn't enforce FK constraints, doesn't emit SQLSTATE 23503/23505 errors, and won't catch the locked decisions about Postgres-driven validation. Use Testcontainers OR an ephemeral local container. |
| Testcontainers | `Microsoft.EntityFrameworkCore.Sqlite` for tests | Same problem — different dialect, no jsonb, no SQLSTATEs. |

## What NOT to Use

| Avoid | Why | Use Instead |
|-------|-----|-------------|
| `AutoMapper` | Locked out by Decision (runtime reflection, slower, licensed since late 2024, not AOT-safe). | Riok.Mapperly 4.3.1 |
| `FluentValidation.AspNetCore` (any version) | **Deprecated and unmaintained.** The auto-validation pipeline was removed. Continuing to use it ties you to FluentValidation 11.x and blocks the v12 upgrade. | `FluentValidation.DependencyInjectionExtensions` 12.1.1 + manual `IValidator<T>` invocation |
| `services.AddFluentValidation(...)` extension | Removed in v12. Was the entry point of the deprecated package. | `services.AddValidatorsFromAssembly(...)` |
| `[Required]` / DataAnnotations attributes | Locked out by Decision. Doesn't compose with `BaseEntityValidator<T>` inheritance pattern. | FluentValidation per-entity validator classes that inherit from base validator |
| `Microsoft.EntityFrameworkCore.InMemory` | Doesn't enforce FK constraints, no SQLSTATE behavior, no jsonb. Locked decisions depend on Postgres-specific error codes 23503/23505. | Testcontainers.PostgreSql 4.11.0 |
| `Microsoft.EntityFrameworkCore.Sqlite` (for tests) | Different dialect, no jsonb, no SQLSTATE. Tests will pass but miss the error-mapping behavior the spec requires. | Testcontainers.PostgreSql 4.11.0 |
| `Newtonsoft.Json` | ASP.NET Core 8 defaults to System.Text.Json. Adds a second JSON stack, hurts performance, blocks AOT. Required only if you adopt `Newtonsoft.Json.Schema` (don't). | System.Text.Json (in-box) + JsonSchema.Net for schema validation |
| `Newtonsoft.Json.Schema` | Commercial license (>10K validations/hour). | JsonSchema.Net 9.2.1 |
| `NCrontab` | Weaker than Cronos: no DST awareness, no L/W/# chars. Not actively maintained (last release older than Cronos's). | Cronos 0.13.0 |
| `Hangfire` for cron scheduling | Out of scope — Workflow execution is external (orchestrator + scheduler). The API only validates the `CronExpression` string format. | Cronos for *parsing*; do not schedule. |
| `Polly` HTTP resilience | No outbound HTTP in v1; only DB. Premature dependency. | Add when v2 introduces an outbound HTTP client. |
| `MediatR` | 3-tier layering is already Controller → Service → Repository. MediatR adds a 4th hop with no benefit at this scope; also went commercial-license in late 2024. | Direct service-class injection into controllers. |
| `MassTransit` / `RabbitMQ.Client` | No messaging in scope. CRUD only. | Add when async fan-out becomes a requirement. |
| `Serilog` (as primary log sink) | OTel SDK already covers structured logging + console + OTLP. Adding Serilog duplicates pipelines and the locked decision says `Logging:LogLevel` is the single source of truth via MEL. | `Microsoft.Extensions.Logging` (in-box) + `OpenTelemetry.Extensions.Hosting` log provider. |
| `Microsoft.AspNetCore.Diagnostics.HealthChecks` (legacy package) | Replaced by in-box `Microsoft.Extensions.Diagnostics.HealthChecks` since .NET 6. | Use the in-box namespace + Xabaril's `AspNetCore.HealthChecks.NpgSql` for the Postgres probe. |
| `HealthChecks.UI` (Xabaril) | Adds a UI dashboard the locked spec doesn't ask for. Extra surface area. | Plain JSON `/health/live`, `/health/ready`, `/health/startup` endpoints. |
| `Swashbuckle.AspNetCore` (Swagger) | Not in locked requirements. If documentation is added later, `Microsoft.AspNetCore.OpenApi` (in-box) is the .NET 8+ direction. | Defer; if added, prefer `Microsoft.AspNetCore.OpenApi`. |

## Stack Patterns by Variant

**If the project later splits `BaseApi.Core` into a NuGet package (currently locked out as Option B):**
- Add `<IsPackable>true</IsPackable>` to `BaseApi.Core.csproj`
- Add `PackageId`, `PackageVersion`, `Authors`, `RepositoryUrl`
- Move `Microsoft.EntityFrameworkCore.Design` reference out of Core and only into Service (it's tooling-only)
- Mark Mapperly reference in Core as `PrivateAssets="all"` so consumers don't transitively get the analyzer

**If the project upgrades to .NET 10 LTS (Nov 2025 → Nov 2028):**
- Bump all `8.0.x` packages to `10.0.x` in lockstep (EF Core, Npgsql.EntityFrameworkCore.PostgreSQL, Microsoft.AspNetCore.Mvc.Testing)
- Bump OpenTelemetry instrumentation packages to their then-current versions
- Bump Mapperly / FluentValidation / JsonSchema.Net / Cronos / Testcontainers independently — they version on their own cadence
- `<TargetFramework>` → `net10.0`

**If integration tests grow to >20 test classes:**
- Add `Testcontainers.XunitV3` as a shared class fixture wrapper to reuse one container across multiple classes
- Otherwise raw `IAsyncLifetime` per test class is fine

**If outbound HTTP is introduced:**
- The `OpenTelemetry.Instrumentation.Http 1.15.0` reference is already in place — it will auto-instrument `HttpClient`
- Add `Microsoft.Extensions.Http.Resilience` for retries/circuit-breaker (in-box modern replacement for Polly)

## Version Compatibility

| Package A | Compatible With | Notes |
|-----------|-----------------|-------|
| `.NET 8.0.27` runtime | `Microsoft.EntityFrameworkCore 8.0.27` | Always pin EF Core patch to the matching .NET 8 patch — both ship together monthly. |
| `Microsoft.EntityFrameworkCore 8.0.27` | `Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10` | Npgsql provider 8.x targets EF Core 8.x. Do NOT pair EF Core 8 with Npgsql.EF 9 or 10. |
| `Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10` | PostgreSQL **14, 15, 16, 17** | 14 is the documented minimum. 17 is the recommended pin. 18 is GA but not yet validated against Npgsql 8.x. |
| `OpenTelemetry 1.15.3` | `OpenTelemetry.Exporter.OpenTelemetryProtocol 1.15.3`, `OpenTelemetry.Extensions.Hosting 1.15.3` | Keep these three on identical versions. Instrumentation packages version independently (currently 1.15.0). |
| `FluentValidation 12.1.1` | `FluentValidation.DependencyInjectionExtensions 12.1.1` | Always match major+minor between these two. |
| `FluentValidation 12.x` | `FluentValidation.AspNetCore 11.3.1` | **INCOMPATIBLE** — `FluentValidation.AspNetCore` is pinned to FluentValidation 11.x and will not load against 12.x. This is the failure mode when migrating from older code. |
| `Riok.Mapperly 4.3.1` | C# 9+, Roslyn 4.0+, .NET 5+ | All satisfied by .NET 8 / C# 12. Source generator runs at build time. |
| `xunit.v3 3.2.2` | .NET 8.0+ | Compatible with `Microsoft.AspNetCore.Mvc.Testing 8.0.27` and `Testcontainers.PostgreSql 4.11.0`. |
| `AspNetCore.HealthChecks.NpgSql 9.0.0` | `Microsoft.Extensions.Diagnostics.HealthChecks >= 8.0.11` | Satisfied transitively by .NET 8.0.27. Despite the "9.0.0" version number, the package runs fine on .NET 8 — Xabaril packages version on their own cadence. |
| `JsonSchema.Net 9.2.1` | System.Text.Json (in-box .NET 8) | No conflict with ASP.NET Core 8's JSON stack. |

## Sources

### Official / NuGet (HIGH confidence)
- [NuGet: Microsoft.EntityFrameworkCore](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore) — verified 8.0.27 latest 8.0.x (2026-05-12)
- [NuGet: Microsoft.AspNetCore.Mvc.Testing](https://www.nuget.org/packages/Microsoft.AspNetCore.Mvc.Testing) — verified 8.0.27 latest 8.0.x (2026-05-12)
- [NuGet: Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/8.0.10)
- [NuGet: Riok.Mapperly 4.3.1](https://www.nuget.org/packages/Riok.Mapperly)
- [NuGet: FluentValidation 12.1.1](https://www.nuget.org/packages/fluentvalidation/)
- [NuGet: FluentValidation.DependencyInjectionExtensions 12.1.1](https://www.nuget.org/packages/fluentvalidation.dependencyinjectionextensions/)
- [NuGet: OpenTelemetry 1.15.3](https://www.nuget.org/packages/OpenTelemetry)
- [NuGet: AspNetCore.HealthChecks.NpgSql 9.0.0](https://www.nuget.org/packages/AspNetCore.HealthChecks.NpgSql/)
- [NuGet: JsonSchema.Net 9.2.1](https://www.nuget.org/packages/JsonSchema.Net)
- [NuGet: Cronos 0.13.0](https://www.nuget.org/packages/Cronos)
- [NuGet: Testcontainers.PostgreSql 4.11.0](https://www.nuget.org/packages/Testcontainers.PostgreSql)
- [NuGet: xunit.v3 3.2.2](https://www.nuget.org/packages/xunit.v3)
- [.NET 8.0 SDK 8.0.421 / Runtime 8.0.27 (May 12, 2026)](https://github.com/dotnet/core/blob/main/release-notes/8.0/8.0.26/8.0.126.md)
- [.NET 8 LTS support policy (through Nov 2026)](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [FluentValidation 12 upgrade guide — confirms .AspNetCore deprecation](https://docs.fluentvalidation.net/en/latest/upgrading-to-12.html)
- [FluentValidation ASP.NET Core docs — confirms manual IValidator<T> pattern](https://docs.fluentvalidation.net/en/latest/aspnet.html)
- [Mapperly installation docs — confirms PrivateAssets/ExcludeAssets pattern](https://mapperly.riok.app/docs/getting-started/installation/)
- [Npgsql 8.0 release notes — confirms PG 14 minimum](https://www.npgsql.org/efcore/release-notes/8.0.html)
- [Npgsql compatibility docs](https://www.npgsql.org/doc/compatibility.html)
- [postgres official Docker image](https://hub.docker.com/_/postgres) — confirms 17-alpine tag availability
- [Cronos GitHub releases](https://github.com/HangfireIO/Cronos/releases) — confirms 0.13.0 (2026-04-29) and DST/timezone advantage over NCrontab
- [PostgreSQL 18 release announcement (May 2026)](https://www.postgresql.org/about/news/postgresql-18-released-3142/)
- [Testcontainers for .NET — PostgreSQL module](https://dotnet.testcontainers.org/modules/postgres/)
- [xUnit v3 NuGet packages guide](https://xunit.net/docs/nuget-packages-v3)

### Comparative / community (MEDIUM confidence)
- [Cronos vs NCrontab — LibHunt comparison](https://dotnet.libhunt.com/compare-cronos-vs-ncrontab) — confirms Cronos's DST/timezone superiority
- [JSON Schema implementations performance comparison (.NET, 2025)](https://medium.com/@lateapexearlyspeed/performance-comparison-of-json-schema-implementations-for-net-ead3d092a473) — informs JsonSchema.Net vs NJsonSchema pick

---
*Stack research for: .NET 8 Web API modular monolith (BaseApi.Core + BaseApi.Service) on PostgreSQL + OTel*
*Researched: 2026-05-26*
