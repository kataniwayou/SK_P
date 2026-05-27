# Feature Research

**Domain:** .NET 8 Web API base library + service (controller-based, CRUD over 5 entities, modular monolith)
**Researched:** 2026-05-26
**Confidence:** HIGH (cross-checked against PROJECT.md locked decisions + current .NET 8/9 ecosystem patterns)

---

## How to Read This Document

Three categories drive v1 requirements writing:

- TABLE STAKES — must ship in v1. Missing = operators/consumers reject the service. Complexity rated LOW/MEDIUM/HIGH.
- DIFFERENTIATOR — adds material value, may include in v1 if cheap; otherwise v1.x.
- ANTI-FEATURE — appears valuable but is explicitly out-of-scope for v1, either by PROJECT.md decision or by ecosystem evidence that it costs more than it returns at this stage.

Anti-feature anchors are cited as `[OOS:<topic>]` when PROJECT.md's "Out of Scope" section is the source.

---

## 1. Infrastructure Features

### Table Stakes

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| `AddBaseApi(IServiceCollection, IConfiguration)` extension as composition root | Whole point of `BaseApi.Core` is to be wired in one call from `BaseApi.Service/Program.cs`; the consumer should not know which middlewares/services exist | LOW | One extension method + one `UseBaseApi(IApplicationBuilder)` for middleware pipeline. Mirrors `AddControllers`, `AddHealthChecks` idiom. Internal subgroupings: `AddBaseApiPersistence`, `AddBaseApiObservability`, `AddBaseApiErrorHandling`. |
| Configuration via `IOptions<T>` pattern (Options pattern) | Idiomatic .NET 8; testable; supports per-section binding with `ValidateDataAnnotations` / `ValidateOnStart` | LOW | Use `services.AddOptions<TOpts>().BindConfiguration("Section").ValidateOnStart()`. One `BaseApiOptions` (DB conn string section, service name/version, OTel endpoint). |
| `IOptionsSnapshot<T>` for per-request scoped config | Required for any options consumed inside request scope that might change (logging level reads happen via MEL, not this) | LOW | Default to `IOptions<T>` (singleton); use `IOptionsSnapshot<T>` only where rebinding matters. None of our cases truly need it in v1. |
| Connection string from configuration with environment override | Postgres conn string lives in `appsettings.json` and `ConnectionStrings:DefaultConnection`; overridable by `ConnectionStrings__DefaultConnection` env var | LOW | Standard .NET config provider chain (json → env → CLI). Document in README. |
| EF Core migrations applied on startup | PROJECT.md explicitly locks this: "Migrations owned by the service, applied on startup" | MEDIUM | Apply inside `IHostedService` (`MigrationHostedService`) or right before `app.Run()` via scoped `serviceProvider.GetRequiredService<AppDbContext>().Database.Migrate()`. **Pitfall flag (see PITFALLS.md):** multi-instance deployments risk concurrent migration; v1 runs single instance per Docker Compose, so deferred — but call out the limitation explicitly. |
| Database health check on readiness probe | PROJECT.md: "Readiness probe — service can reach Postgres" | LOW | `AspNetCore.HealthChecks.NpgSql` package + `MapHealthChecks("/health/ready", new { Predicate = check => check.Tags.Contains("ready") })`. |
| Startup / Liveness / Readiness probes (3 separate endpoints) | PROJECT.md explicit. K8s/Docker convention: liveness for restart, readiness for traffic, startup for slow boot | LOW | `/health/live`, `/health/ready`, `/health/startup`. Liveness = `() => Healthy()`. Startup = "migrations done + DI built" (flip a flag in a hosted service). Readiness = liveness + Postgres reachable. |
| Graceful shutdown (default host behavior + drain time) | Standard .NET Generic Host gives this; need only to ensure no `Environment.Exit` and no long-running tasks block shutdown | LOW | Set `HostOptions.ShutdownTimeout` (default 30s) explicitly to e.g. 15s. EF Core `DbContext` disposed via DI scope. |
| Single shared `AppDbContext` registered as `AddDbContext<AppDbContext>` (scoped lifetime) | PROJECT.md locked: "Single shared database, single shared AppDbContext" | LOW | `services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(connStr).AddInterceptors(auditInterceptor))`. Resolve `AuditInterceptor` from DI. |
| `BaseDbContext` in `BaseApi.Core`, `AppDbContext : BaseDbContext` in `BaseApi.Service` | Base owns audit interceptor wire-up + common conventions; service owns entity registrations | LOW | Base class only wires conventions and interceptors; doesn't know about entities. |

### Differentiators

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| `IOptionsMonitor<T>` for hot-reload of `appsettings.json` | Operators can change log levels without redeploy (already free via MEL `LoggerFilterOptions`, but extending to custom options is the differentiator) | LOW | Free in MEL for `Logging:LogLevel`. **Do not** add custom hot-reload for other options in v1 — adds reasoning overhead. |
| Composition root validation: fail fast on missing config | `ValidateOnStart()` + DataAnnotations on options classes prevents "service starts then 500s on first request" | LOW | Cheap, high-value. Include in v1. |
| Migration locking (distributed lock or `pg_advisory_lock`) | Safety net for the day someone scales to N>1 replicas | MEDIUM | Defer to v1.x — single replica today. Note in PITFALLS.md. |

### Anti-Features (v1)

| Feature | Why Tempting | Why Out of Scope | Alternative |
|---------|--------------|------------------|-------------|
| Migrations via external CLI / init container / Flyway / DbUp | "Production-grade" guidance says don't migrate on startup | PROJECT.md explicitly chose startup migration. Single deployable service, single replica, milestone speed prioritized. Re-evaluate when scaling to multi-replica. | Document the limitation; ensure migrations are short and idempotent; ship `dotnet ef migrations script --idempotent` as a fallback in repo. |
| Secrets Manager / Vault / Azure Key Vault integration | Production secret hygiene | No auth/secrets boundary defined in v1 [OOS: Authentication] | Use environment variables + .gitignored `appsettings.Development.json`. Document hand-off to a secrets provider as a v2 concern. |
| Multiple `DbContext`s per bounded context | "Clean architecture" textbook guidance | PROJECT.md locked: single `AppDbContext` (cross-entity FKs force it). | n/a |
| Feature flags / LaunchDarkly / `Microsoft.FeatureManagement` | Toggling features in flight | No multi-tenant or experimentation surface in v1 | Defer. |

---

## 2. HTTP / CRUD Features

### Table Stakes

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| Abstract generic `BaseController<TEntity, TCreateDto, TUpdateDto, TReadDto>` | PROJECT.md locked: "One controller per entity, each derived from abstract generic `BaseController<...>`" | MEDIUM | Generic on 4 type params; protected virtual methods for `MapToEntity`, `MapToRead`, `MapPatchOntoEntity` (or via injected `IEntityMapper<T,C,U,R>` interface from Mapperly partials). Public CRUD verbs: `[HttpGet]`, `[HttpGet("{id:guid}")]`, `[HttpPost]`, `[HttpPut("{id:guid}")]`, `[HttpDelete("{id:guid}")]`. |
| Three DTOs per entity (Create / Update / Read) | PROJECT.md locked. Separates server-controlled fields (`Id`, `CreatedAt`, `CreatedBy`) from client input | LOW | Just record types or POCOs. Enforce by base controller signature. |
| `GET` (list-all) returns all rows | PROJECT.md: "basic GET returns all" [OOS: pagination] | LOW | `Repository.ListAsync(CancellationToken)` → `IReadOnlyList<TEntity>`. Add hard cap (e.g., 1000) to prevent accidental table scan blowups — document the cap. |
| `GET /{id:guid}` → 200 or 404 | RFC convention; PROJECT.md error mapping | LOW | Use `id:guid` route constraint so malformed IDs return 400 from MVC binding, not 404. |
| `POST` → 201 Created with `Location` header | Standard REST; clients (Orchestrator/Scheduler) likely follow `Location` | LOW | `return CreatedAtAction(nameof(GetById), new { id = entity.Id }, readDto);` |
| `PUT /{id:guid}` → 204 NoContent on success, 404 if missing | PROJECT.md: standard CRUD verbs. Full-replace semantics on PUT (matches DTO shape) | LOW | DTO shape ensures PUT is replace, not patch. No PATCH in v1 per PROJECT.md verb list. |
| `DELETE /{id:guid}` → 204 NoContent (hard delete) | PROJECT.md: "CRUD DELETE is a hard delete" [OOS: soft delete] | LOW | DB-level FK constraints will surface dependent rows as 422 via SQLSTATE `23503` mapping — that's intentional and correct behavior. |
| Generic `Repository<TEntity>` in `BaseApi.Core` | PROJECT.md locked: 3-tier layering, generic repository | MEDIUM | `IRepository<T> where T : BaseEntity` with `GetByIdAsync`, `ListAsync`, `AddAsync`, `UpdateAsync`, `DeleteAsync`, `SaveChangesAsync`. **Don't** add `IQueryable` leakage — keep the surface tight. |
| Service layer per entity (e.g., `ProcessorService`) | PROJECT.md: "service holds entity-specific logic + M2M sync" | MEDIUM | Base has no service abstraction (services are concrete); only repository + controller are generic. Service is the seam for junction-table sync (`StepNextSteps`, `WorkflowEntrySteps`, `WorkflowAssignments`). |
| OpenAPI / Swagger UI | Operators & external consumers need a contract; Swagger UI also serves as smoke-test surface | LOW | **Swashbuckle.AspNetCore** (more mature ecosystem, FluentValidation integration, versioning integration). Alternative `Microsoft.AspNetCore.OpenApi` (new in .NET 9) is the future but the FluentValidation/versioning integration story isn't as mature in .NET 8. Stick with Swashbuckle for v1. |
| `[ApiController]` attribute on concrete controllers | Auto model validation → 400 ValidationProblemDetails (compatible with our error contract); attribute routing required | LOW | Apply once on the concrete classes (or on the base; the attribute is inherited). |
| Route convention `[Route("api/[controller]")]` | Standard, predictable URL shape | LOW | Apply on base controller; `[controller]` resolves to derived class name ("Schemas", "Processors", etc.). |
| Content-type negotiation: JSON in/out | Default in ASP.NET Core; no XML | LOW | Free out of the box. `System.Text.Json` not Newtonsoft. |
| `DateTime` / `Guid` JSON conventions | `Guid` round-trips fine; `DateTime` should be UTC and ISO 8601 | LOW | Use `DateTime.UtcNow` in `AuditInterceptor`. JSON serializer treats DateTime as ISO 8601 by default. |
| CORS — permissive policy for local dev, configurable in prod | Browser-based consumers (admin UIs eventually) need it; Orchestrator/Scheduler are server-to-server but a permissive dev policy avoids friction | LOW | `AddCors` with named policy "BaseApi" reading allowed origins from config. Empty list = no CORS headers; `["*"]` = AllowAnyOrigin. |
| Cancellation token propagation | All async controller actions and repository methods accept `CancellationToken` and pass it through to EF Core | LOW | Standard hygiene. Skipping causes cancel-on-disconnect to leak DB connections under load. |
| `id:guid` route constraint everywhere | Prevents string-vs-Guid route ambiguity, returns 400 for non-Guid input | LOW | Apply uniformly in `BaseController`. |

### Differentiators

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| OpenAPI examples + descriptions populated from XML doc comments | Better Swagger UX; documents intent for consumers | LOW | Enable `<GenerateDocumentationFile>true` in `.csproj`, point Swashbuckle at the XML. Cheap. Include in v1. |
| Swagger UI grouped by entity (default tag = controller name) | Already free with Swashbuckle | LOW | Free. |
| FluentValidation → Swagger schema rules via `MicroElements.Swashbuckle.FluentValidation` | Surfaces validation constraints in OpenAPI without re-declaring them on DTOs | MEDIUM | Cheap win if Swashbuckle is in. Include in v1 if integration is one package + one line. |
| Idempotency-Key header on POST | Safe retries for clients | MEDIUM | Skip v1. Add when consumers demonstrate need. |
| API versioning via `Asp.Versioning.Http` | Future-proof URL shape (`api/v1/processors`) | LOW-MEDIUM | **Recommended for v1** — adding versioning later requires URL changes that break consumers. Use URL-segment versioning (`api/v{version:apiVersion}/[controller]`), default version `1.0`. Single one-liner per concrete controller: `[ApiVersion(1.0)]`. |

### Anti-Features (v1)

| Feature | Why Tempting | Why Out of Scope | Alternative |
|---------|--------------|------------------|-------------|
| Pagination on list endpoints (Sieve / Gridify / OData / custom skip-take) | "Production REST" guidance | PROJECT.md: "basic GET returns all; complex query is out of v1. Defer until proven needed." [OOS: pagination/filtering/sorting] | Hard cap row count (e.g., 1000) in `Repository.ListAsync` to bound worst-case memory; document the cap. |
| Filtering/sorting on list endpoints | Same as above | Same [OOS] | Same |
| OData (`Microsoft.AspNetCore.OData`) | Powerful filtering | Heavy framework; couples API shape to a query language; harder to reason about [OOS] | If pagination is ever added, prefer Sieve or Gridify (lightweight) over OData. |
| Soft delete (`IsDeleted` flag + global query filter) | "Just in case" recovery | PROJECT.md: "CRUD DELETE is a hard delete" [OOS: soft delete] | Reintroduce as a base concern in `BaseEntity` later if a use case appears. Note that DB-FK constraint on hard delete naturally surfaces dependents (422). |
| Bulk operations (`POST /array`, `PATCH /array`) | Reduce round trips | Not in PROJECT.md's verb list; adds transaction-scope and partial-failure semantics complexity | Single-item CRUD only. Clients loop. |
| PATCH (`HttpPatch` with JSON Patch or JSON Merge Patch) | Partial updates | Not in PROJECT.md's verb list ("GET, GET/{id}, POST, PUT /{id}, DELETE /{id}") | PUT does full-replace via UpdateDto. Add PATCH only when consumers prove they need it. |
| ETags / `If-Match` optimistic concurrency | Concurrent-edit safety | Not in PROJECT.md; no concurrent-edit signal from user; adds version-token plumbing on every entity | EF Core has `[Timestamp]` / `xmin` concurrency tokens if needed later. Postgres `xmin` system column maps cleanly. |
| HATEOAS / hypermedia links | "RESTful purity" | Massive complexity, low consumer adoption (Orchestrator/Scheduler unlikely to follow links) | Plain JSON resource representations. |
| Rate limiting (`Microsoft.AspNetCore.RateLimiting`) | DDoS / runaway-client protection | No auth boundary, no public exposure stated, no client-id signal to key on [OOS: authentication implies] | Add when service is internet-facing. |
| Authentication (JWT bearer / API keys) | Production hygiene | PROJECT.md explicit [OOS: authentication] | `CreatedBy`/`UpdatedBy` populated only when `HttpContext.User.Identity.Name` is available; defaults to null. |
| Authorization (policies / roles) | Same | Same [OOS] | Same |
| GraphQL endpoint (HotChocolate) | "Modern API" | Adds parallel transport surface, query-cost reasoning, N+1 risk against EF Core | REST only. |
| gRPC endpoints alongside HTTP | Server-to-server perf | Not requested; doubles surface area | REST only. |

---

## 3. Validation Features

### Table Stakes

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| FluentValidation registered + auto-validating | PROJECT.md locked tech stack | LOW | `services.AddFluentValidationAutoValidation()` (from `SharpGrip.FluentValidation.AutoValidation.Mvc` — the maintained successor to the deprecated `FluentValidation.AspNetCore`) + `services.AddValidatorsFromAssemblyContaining<TMarker>()`. Or wire explicitly via filter / middleware. **Decide explicitly** (see PITFALLS.md). |
| `BaseEntityValidator<T> where T : BaseEntity` with `Name`, `Version`, `Description` rules | PROJECT.md: "Per-entity validators inherit base rules" | LOW | `RuleFor(x => x.Name).NotEmpty().MaximumLength(200)`, `RuleFor(x => x.Version).NotEmpty().Matches("^\\d+\\.\\d+\\.\\d+$")`, `RuleFor(x => x.Description).MaximumLength(2000).When(x => x.Description is not null)`. Concrete validators inherit and add. |
| Validator inheritance: concrete validator constructor calls base | Standard FluentValidation pattern | LOW | `public ProcessorCreateDtoValidator() : base() { RuleFor(x => x.SourceHash)...; }` — requires Create/Update DTOs to share base shape OR use composition via `Include(new BaseValidator())`. **Decide explicitly** which pattern. |
| Per-entity custom rules: SemVer regex, SHA-256 hex regex, JSON syntactic validity, JSON Schema validity | PROJECT.md locked: per-entity rules listed | LOW-MEDIUM | SemVer: regex on base. SHA-256: regex on `ProcessorCreateDto.SourceHash` (`^[a-f0-9]{64}$`, case-insensitive). JSON validity: `System.Text.Json.JsonDocument.Parse` in a `Must` rule. JSON Schema validity: **Json.Schema (NJsonSchema or JsonSchema.Net)** — pick **JsonSchema.Net** (modern, AOT-friendly, MIT). |
| Validation failure → 400 ValidationProblemDetails with field-keyed errors | PROJECT.md: "FluentValidation failures → 400 with field-level errors map" | LOW | If using `[ApiController]` + auto-validation, this is automatic via ASP.NET Core's `InvalidModelStateResponseFactory`. Customize to emit RFC 7807 shape with `correlationId`. |
| Cross-field validation in custom rules | E.g., for future scenarios — minimal in v1 (Processor's `InputSchemaId` != `OutputSchemaId`? Not stated. Defer.) | LOW | Use `RuleFor(x => x).Must(...)` for cross-field; not heavily needed in v1. |

### Differentiators

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| Custom JSON Schema validator rule that's reusable across entities (`MustBeValidJsonSchema()` extension) | Encapsulates third-party library in one place; testable independently | LOW | Add as `RuleBuilderExtensions` in `BaseApi.Core/Validation/`. Include in v1 — the rule lives somewhere; better in one named extension. |
| Cron expression validity rule (for `Workflow.CronExpression`) | Service can refuse malformed cron at write time vs. surfacing later in the external scheduler | LOW | `Cronos` library (https://github.com/HangfireIO/Cronos) — actively maintained, supports 5- and 6-field cron. `RuleFor(x => x.CronExpression).Must(BeValidCron).When(x => x.CronExpression is not null)`. Include in v1 — cheap and prevents bad data in `WorkflowEntity`. |
| Validation pipeline behavior via MediatR | Decoupled validation/handler separation | MEDIUM | **No.** PROJECT.md doesn't include MediatR. Don't introduce CQRS in v1 — three-tier layering is already locked in. |

### Anti-Features (v1)

| Feature | Why Tempting | Why Out of Scope | Alternative |
|---------|--------------|------------------|-------------|
| Dynamic `Assignment.Payload` validation against the referenced `Schema.Definition` | "Naturally" expected | PROJECT.md explicit: N2 = No [OOS: payload-vs-schema conformance] | Payload validated as syntactic JSON only. Document the gap; downstream consumers may enforce. |
| Pre-validation that FK targets exist (HTTP GET to verify before insert) | Faster error feedback for clients | PROJECT.md: "no upfront EntityBApi GET" (Option 1 chosen) [OOS: FK pre-validation] | Rely on Postgres FK constraint → SQLSTATE 23503 → 422 mapping. |
| DataAnnotations on DTOs alongside FluentValidation | Belt-and-suspenders | Double source of truth; confuses readers; one wins silently | FluentValidation only. Strip DataAnnotations from DTOs. |
| MediatR + validation behavior + CQRS | "Clean architecture" pattern | Adds dispatcher + handler classes for trivial CRUD; PROJECT.md locks 3-tier | Direct service calls from controller. |
| Validators auto-registered from assembly scan and then conflicting validators present | Convenience | Two validators for the same type silently overlap; debug hours | Single named validator per DTO. Assembly scan from one marker type only. |

---

## 4. Observability Features

### Table Stakes

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| OpenTelemetry SDK wired for logs + HTTP server metrics | PROJECT.md locked | MEDIUM | `OpenTelemetry.Extensions.Hosting` + `OpenTelemetry.Exporter.OpenTelemetryProtocol` + `OpenTelemetry.Instrumentation.AspNetCore`. **Note:** PROJECT.md mentions logs + HTTP metrics. **Traces are not explicitly listed.** Recommend including traces too (minimal extra cost; instrumentation is the same package set) — surface as differentiator below. |
| OTLP exporter to external Collector with `OTEL_EXPORTER_OTLP_ENDPOINT` honored | PROJECT.md locked | LOW | OTLP exporter respects standard env vars (`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`) out of the box. Default protocol `grpc`; can be `http/protobuf`. Document both. |
| Service resource attributes from config (`service.name`, `service.version`) | PROJECT.md locked: `sk-api` / `3.2.0` | LOW | `ResourceBuilder.CreateDefault().AddService(serviceName: cfg["Service:Name"], serviceVersion: cfg["Service:Version"])`. |
| Single `Logging:LogLevel` source of truth for console + OTel sinks | PROJECT.md locked | LOW | Both console (`AddConsole()`) and OTel (`AddOpenTelemetry(o => o.AddOtlpExporter())`) hang off MEL's `ILoggerFactory`, so `Logging:LogLevel` filters apply uniformly **before** the sink. Confirm by writing a smoke test that flips a category to `Warning` and verifies neither sink emits `Information`. |
| `X-Correlation-Id` middleware: read header or generate UUID; attach to log scope; echo on every response (success and error) | PROJECT.md locked | LOW | Custom middleware (~30 LOC). `ILogger.BeginScope(new Dictionary<string,object> { ["CorrelationId"] = id })` propagates to all logs in that request. Stash id in `HttpContext.Items` for the error middleware and ProblemDetails customizer to read. |
| Default ASP.NET Core HTTP request logging at `Information` | Standard `Microsoft.AspNetCore.HttpLogging` or built-in default | LOW | The default `Microsoft.AspNetCore.Hosting.Diagnostics` logger emits request started/completed events. Don't add `HttpLogging` middleware unless body logging needed (it's not). |

### Differentiators

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| OpenTelemetry traces (in addition to logs + metrics) | Full three-pillar observability for negligible extra config | LOW | Add `WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddNpgsql().AddOtlpExporter())`. **Recommend including in v1** — same packages, same Collector. PROJECT.md only mandates logs + HTTP metrics, but traces are cheap and immediately useful for debugging cross-call latency. |
| EF Core / Npgsql tracing via `Npgsql.OpenTelemetry` | DB call spans nested under HTTP spans → instantly see slow queries | LOW | `Npgsql.OpenTelemetry` package, one line: `.AddNpgsql()`. Include in v1 alongside traces. |
| Custom metrics via `System.Diagnostics.Metrics.Meter` | Domain-specific counters (e.g., `entity.created` by entity type) | MEDIUM | Defer to v1.x. ASP.NET's built-in HTTP server metrics + EF Core metrics cover the v1 needs. |
| Correlation ID propagation to outgoing HTTP via `HttpClient` `DelegatingHandler` | Distributed tracing across services | LOW | Defer — service makes no outgoing HTTP calls in v1 (Postgres is the only dependency, and EF Core spans cover it). |
| `DiagnosticSource` events for domain events | Decoupled observability | MEDIUM | Defer — over-engineering for CRUD. |
| Structured logging conventions document (which fields to log on which events) | Lower cognitive load for new logs | LOW | Write a one-page doc in `docs/observability.md` once base library is in. Include in v1. |

### Anti-Features (v1)

| Feature | Why Tempting | Why Out of Scope | Alternative |
|---------|--------------|------------------|-------------|
| Serilog as the logging library | Familiar, popular | PROJECT.md doesn't require it; .NET 8's MEL + OTel are sufficient; one fewer dependency | Use `ILogger<T>` (MEL) + `AddOpenTelemetry()`. If structured-logging UX of Serilog is wanted, can layer Serilog **as a provider** behind MEL, but unnecessary in v1. |
| Direct vendor SDK (Datadog/New Relic/Application Insights) | Vendor's "easy" SDK | PROJECT.md: "OTLP exporter to external OTel Collector; Collector handles backend fan-out" — locked | Collector is the single egress point; vendor SDKs would bypass it. |
| Body logging on requests/responses | "I want to see everything" | High-cardinality, PII risk, log volume explosion | Log validation errors and exceptions; trust the framework to log status + path + latency. |
| Custom log levels / non-standard severity | Familiarity from other ecosystems | MEL has Trace/Debug/Info/Warning/Error/Critical — sufficient | Stay with MEL defaults. |
| Prometheus scrape endpoint via `/metrics` | "Standard" Prometheus pattern | Collector is the egress; Prometheus can pull from Collector or Collector can push remote-write | OTLP → Collector → wherever. |

---

## 5. Error Handling Features

### Table Stakes

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| Global exception-handling middleware | PROJECT.md: "Unhandled exception → 500 with generic message + correlationId; full stack to logs only" | LOW-MEDIUM | Prefer ASP.NET Core 8's `IExceptionHandler` interface (new in .NET 8) over the older `UseExceptionHandler` lambda — registered via `services.AddExceptionHandler<TBaseApiExceptionHandler>()` and `app.UseExceptionHandler()`. Chainable. |
| RFC 7807 ProblemDetails on every error response | PROJECT.md: "All failures return RFC 7807 Problem Details JSON" | LOW | `services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx => { ctx.ProblemDetails.Extensions["correlationId"] = ctx.HttpContext.Items["CorrelationId"]; })`. .NET 8 first-class support. |
| FluentValidation failures → 400 ValidationProblemDetails with `errors` field-map | PROJECT.md locked | LOW | `[ApiController]` auto-emits `ValidationProblemDetails` for ModelState failures. Customize `InvalidModelStateResponseFactory` to inject `correlationId`. |
| Postgres SQLSTATE → HTTP status mapping | PROJECT.md locked: 23503→422 (FK), 23505→409 (unique) | LOW | Catch `DbUpdateException` whose inner is `PostgresException`, switch on `SqlState`. Offending column extractable from `PostgresException.ColumnName` (sometimes null — fall back to constraint name parsing). |
| Domain exceptions: `NotFoundException`, `ConflictException`, `ValidationException` (FluentValidation has its own) | Lets service layer throw semantically; mapping centralized in middleware | LOW | Define in `BaseApi.Core/Exceptions/`. `IExceptionHandler` maps each to status + ProblemDetails. |
| 404 with id detail for resource-not-found | PROJECT.md locked | LOW | `NotFoundException(typeof(ProcessorEntity), id)` → 404 with `detail: "Processor with id {id} not found"`. |
| Every error response carries `correlationId` | PROJECT.md locked | LOW | Hooked via `CustomizeProblemDetails` callback reading from `HttpContext.Items`. |
| Generic 500 message that doesn't leak internals; full stack to logs | PROJECT.md locked | LOW | ProblemDetails `detail` = `"An unexpected error occurred. Reference correlationId for support."`. `_logger.LogError(ex, "Unhandled exception")` writes the full exception. |

### Differentiators

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| `Activity.Current?.TraceId` also embedded in error body | Lets you find the OTel trace for a failed request directly | LOW | Single line in `CustomizeProblemDetails`. Include in v1 if OTel traces are on (we recommend they are). |
| `type` URIs in ProblemDetails point to a docs page per error type | Self-describing API | MEDIUM | Defer — docs pages don't exist yet. Use the default `type` URIs (`https://tools.ietf.org/html/rfc7231#section-6.5.4` etc.) for v1. |
| Postgres exception → user-friendly field name (not the raw column name) | Better client UX | LOW | If we standardize FK / unique constraint names (`fk_processor_input_schema_id`, `uq_processor_source_hash`), the middleware can derive a friendly field. Include in v1 if naming convention is adopted. |

### Anti-Features (v1)

| Feature | Why Tempting | Why Out of Scope | Alternative |
|---------|--------------|------------------|-------------|
| Return raw exception messages or stack traces in 500 responses | "Easier debugging" | Leaks internals; security smell; consumers couple to them | Generic message + correlationId; full detail in logs only. |
| Throwing `HttpResponseException` from controllers | "Convenient" | Type doesn't exist in ASP.NET Core; relics from Web API 2 | Throw domain exceptions; let middleware map. |
| `IActionResult` returns of `Problem(...)` scattered through services / repositories | "Inline" error responses | Couples non-HTTP layers to HTTP types | Throw domain exceptions; HTTP shape only at the middleware boundary. |
| HTML error pages on 500 | Old MVC convention | API consumers want JSON | ProblemDetails only. |
| Different error envelope (e.g., `{ "error": { "code", "message" } }` instead of ProblemDetails) | Familiar from other ecosystems | PROJECT.md locks RFC 7807 | n/a |

---

## 6. Mapping Features

### Table Stakes

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| Mapperly partial mapper class per entity | PROJECT.md locked | LOW | `[Mapper] public partial class ProcessorMapper { public partial ProcessorEntity ToEntity(ProcessorCreateDto dto); public partial ProcessorReadDto ToRead(ProcessorEntity entity); public partial void Apply(ProcessorUpdateDto dto, ProcessorEntity entity); }`. Registered as singleton via `services.AddSingleton<ProcessorMapper>()`. Mappers are stateless. |
| Mapperly excludes audit fields on Create (`Id`, `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`) | These are server-controlled; CreateDto must not carry them | LOW | Use `[MapperIgnoreTarget(nameof(BaseEntity.CreatedAt))]` etc., or just don't include them on `CreateDto`/`UpdateDto`. Cleaner: structure DTOs to omit the fields — then no ignore needed. |
| Nullability flows through mappers (nullable reference types enabled project-wide) | Catches null mismatches at compile time | LOW | `<Nullable>enable</Nullable>` in `Directory.Build.props`. Mapperly respects nullability annotations. |

### Differentiators

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| Generic `IEntityMapper<TEntity, TCreate, TUpdate, TRead>` interface implemented by each Mapperly partial | Lets `BaseController` resolve mapper generically without knowing the concrete type | LOW | Implement the interface explicitly in each `partial class`. Then `BaseController` does `private readonly IEntityMapper<TEntity, TCreate, TUpdate, TRead> _mapper`. **Recommend in v1** — otherwise every concrete controller has to override mapping methods. |
| Mapperly verification in tests (mappers don't drop fields silently) | Catches "added a property, forgot to map" | LOW | Round-trip test: `Read(ToEntity(create))` and assert no nulls in expected fields. |

### Anti-Features (v1)

| Feature | Why Tempting | Why Out of Scope | Alternative |
|---------|--------------|------------------|-------------|
| AutoMapper | Familiar | Runtime reflection; not AOT-safe; PROJECT.md explicitly chose Mapperly | Mapperly. |
| Manual mapping in controllers / services | "Don't add a library" | Mapperly is source-gen → zero runtime cost; manual mapping is just bugs waiting | Mapperly. |
| Generic auto-mapping via reflection in `BaseController` | "DRY" | Defeats the AOT-safe / source-gen goal; mappers are entity-specific anyway (M2M sync, junction tables) | Per-entity Mapperly mappers behind `IEntityMapper<,,,>` interface. |

---

## 7. Testing Features

### Table Stakes

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| `xUnit` as test runner | De facto .NET standard | LOW | `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`. |
| `WebApplicationFactory<Program>` for integration tests | Standard ASP.NET Core integration test harness | LOW | Requires `Program.cs` to use top-level statements + `public partial class Program`. Trivial. |
| Testcontainers for Postgres in integration tests | Real DB calls without polluting dev env; tests run on every CI | MEDIUM | `Testcontainers.PostgreSql` package. One `IAsyncLifetime` fixture per test collection; container reused across tests in the same collection. Requires Docker available on CI. |
| Unit tests for validators (FluentValidation has a test extension) | Validators are pure logic; cheap to test | LOW | `FluentValidation.TestHelper` package: `validator.TestValidate(dto).ShouldHaveValidationErrorFor(x => x.Name)`. |
| Unit tests for mappers | Catch silent field drops | LOW | Hand-rolled assertions over each `ToEntity`/`ToRead`/`Apply`. |

### Differentiators

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| Integration test: `POST /api/v1/processors` round-trip with Testcontainers Postgres | High-fidelity smoke test for every CRUD path | MEDIUM | One golden-path test per entity is enough to catch wiring regressions. Include in v1. |
| Integration test: Postgres FK violation → 422 mapping | Verifies the SQLSTATE mapping end-to-end | MEDIUM | One test per error class (FK violation, unique violation, not-found). Include in v1 — these are the contract. |
| Snapshot tests for ProblemDetails JSON shape | Locks the error contract | LOW | `Verify.Xunit` or similar. Defer — v1's contract is small enough to assert per field. |
| Architecture tests (`NetArchTest` or `ArchUnitNET`) | Enforces layering rules (controllers don't reference repositories directly, etc.) | MEDIUM | Defer to v1.x. |

### Anti-Features (v1)

| Feature | Why Tempting | Why Out of Scope | Alternative |
|---------|--------------|------------------|-------------|
| `InMemoryDatabase` provider for tests | Fast, no Docker required | Doesn't enforce FKs the same way Postgres does; doesn't surface SQLSTATE; PROJECT.md's error contract depends on real Postgres behavior | Testcontainers Postgres. |
| `SQLite` in-memory for tests | Same | Same; also lacks `jsonb` | Testcontainers Postgres. |
| Mocks for `DbContext` / `IRepository<T>` in integration tests | "Unit-test all the things" | Tests the mock, not the system | Real Postgres via Testcontainers for integration; pure-function unit tests for validators and mappers. |
| 100% code coverage gate | "Quality" | Forces tests against trivial code; doesn't catch wiring bugs | Coverage as a signal, not a gate. Focus on golden-path + error-mapping tests. |

---

## Feature Dependencies

```
[Correlation ID Middleware] (Observability)
    └── feeds ──> [Log Scope] (Observability)
    └── feeds ──> [ProblemDetails CustomizeProblemDetails] (Error Handling)
    └── feeds ──> [Response Header echo on every response] (HTTP)

[ProblemDetails (services.AddProblemDetails)] (Error Handling)
    └── required by ──> [Global IExceptionHandler] (Error Handling)
    └── required by ──> [FluentValidation auto-validation → 400] (Validation)
    └── required by ──> [Postgres SQLSTATE mapping → 409/422] (Error Handling)

[FluentValidation registration] (Validation)
    └── required by ──> [BaseEntityValidator<T>] (Validation)
    └── required by ──> [Concrete validators per entity] (Validation)
    └── enhances ──> [Swagger via MicroElements.Swashbuckle.FluentValidation] (HTTP/Diff.)

[AppDbContext (single, shared)] (Infrastructure)
    └── required by ──> [Repository<T>] (HTTP/CRUD)
    └── required by ──> [AuditInterceptor] (Infrastructure)
    └── required by ──> [Postgres health check] (Infrastructure)
    └── required by ──> [EF Core migrations on startup] (Infrastructure)

[OpenTelemetry SDK wiring] (Observability)
    └── required by ──> [OTLP exporter to Collector] (Observability)
    └── required by ──> [HTTP server metrics] (Observability)
    └── enhances ──> [EF Core / Npgsql tracing] (Observability/Diff.)
    └── enhances ──> [Activity.Current.TraceId in error body] (Error Handling/Diff.)

[Mapperly partial mapper per entity] (Mapping)
    └── implements ──> [IEntityMapper<TEntity,TCreate,TUpdate,TRead>] (Mapping/Diff.)
        └── consumed by ──> [BaseController<TEntity,TCreate,TUpdate,TRead>] (HTTP/CRUD)

[Asp.Versioning.Http] (HTTP/Diff., recommended for v1)
    └── feeds ──> [Swagger version grouping] (HTTP)
    └── feeds ──> [Route prefix /api/v{version}/] (HTTP)

[WebApplicationFactory<Program>] (Testing)
    └── required by ──> [Integration tests] (Testing)
    └── consumes ──> [Testcontainers Postgres] (Testing)

[CONFLICT] [Pagination] vs [GET returns all]
    → PROJECT.md locks "GET returns all"; pagination is anti-feature for v1.

[CONFLICT] [Soft delete] vs [DB FK constraints on hard delete]
    → Soft delete would mean `IsDeleted = true` doesn't trigger FK cascade; hard delete + FK surfaces dependents naturally; PROJECT.md locks hard delete.

[CONFLICT] [DataAnnotations on DTOs] vs [FluentValidation]
    → Pick one; PROJECT.md says FluentValidation.
```

### Critical Dependency Notes

- **Correlation ID is upstream of error responses.** The error middleware and ProblemDetails customizer read `correlationId` from `HttpContext.Items`. Therefore correlation-ID middleware **must** be registered **before** `UseExceptionHandler` in the pipeline.
- **`services.AddProblemDetails()` is required for `IExceptionHandler` to produce ProblemDetails responses** (otherwise the framework falls back to text/html or empty body on some error paths).
- **`[ApiController]` controllers automatically produce `ValidationProblemDetails` on ModelState invalid** — but only if `services.AddProblemDetails()` is called.
- **EF Core migrations must complete before readiness probe returns Healthy.** Apply migrations in a hosted service that flips a "startup complete" flag the readiness probe reads, OR apply synchronously before `app.Run()` so the listener doesn't open until done.
- **API versioning, if added, must be in v1.** Retrofitting versioning after consumers exist requires URL changes — breaks consumers.

---

## MVP Definition

### Launch With (v1)

Per PROJECT.md's Active requirements plus our recommended differentiators:

**Infrastructure (Table Stakes)**
- [ ] `AddBaseApi(...)` extension + `UseBaseApi(...)` pipeline extension
- [ ] Options pattern + `ValidateOnStart()`
- [ ] EF Core migrations applied on startup (single-replica caveat documented)
- [ ] Three health probes (live / ready / startup)
- [ ] Postgres reachability check on readiness
- [ ] Single `AppDbContext` with `AuditInterceptor` wired

**HTTP/CRUD (Table Stakes)**
- [ ] `BaseController<TEntity, TCreate, TUpdate, TRead>` with 5 verbs
- [ ] Generic `Repository<T>` with hard row cap on `ListAsync`
- [ ] Per-entity Service class for M2M sync
- [ ] Swashbuckle OpenAPI + Swagger UI (XML docs enabled)
- [ ] CORS configurable via options
- [ ] `id:guid` route constraint
- [ ] CancellationToken plumbed end-to-end

**HTTP/CRUD (Differentiators recommended for v1)**
- [ ] Asp.Versioning.Http with `/api/v1/...` (avoids breaking URL change later)
- [ ] FluentValidation rules surfaced in OpenAPI (`MicroElements.Swashbuckle.FluentValidation`)
- [ ] `IEntityMapper<,,,>` interface implemented by Mapperly partials

**Validation (Table Stakes)**
- [ ] FluentValidation registered + auto-validating
- [ ] `BaseEntityValidator<T>` with Name/Version/Description rules
- [ ] Per-entity validators inheriting base
- [ ] JSON validity + JSON Schema validity custom rules (JsonSchema.Net)
- [ ] SHA-256 regex rule
- [ ] Cron expression validity rule (Cronos) **[differentiator, recommended]**

**Observability (Table Stakes)**
- [ ] OTel SDK + OTLP exporter to Collector via env var
- [ ] Service resource attributes from config
- [ ] Single `Logging:LogLevel` source for console + OTel
- [ ] `X-Correlation-Id` middleware → log scope → response header
- [ ] OTel traces + Npgsql instrumentation **[differentiator, recommended — same packages, immediately useful]**

**Error Handling (Table Stakes)**
- [ ] Global `IExceptionHandler` (.NET 8 style)
- [ ] `services.AddProblemDetails()` with `correlationId` extension
- [ ] FluentValidation → 400 ValidationProblemDetails
- [ ] SQLSTATE 23503 → 422, 23505 → 409
- [ ] Domain exceptions (`NotFoundException`, `ConflictException`)
- [ ] Generic 500 + correlation id; full stack to logs only
- [ ] `traceId` in error body when OTel traces on **[differentiator, recommended]**

**Mapping (Table Stakes)**
- [ ] Mapperly per-entity partial mapper
- [ ] DTOs structured to omit server-controlled fields (no `MapperIgnoreTarget` gymnastics)

**Testing (Table Stakes for v1)**
- [ ] xUnit test projects
- [ ] WebApplicationFactory integration host
- [ ] Testcontainers Postgres fixture
- [ ] One golden-path integration test per entity (POST → GET → PUT → DELETE)
- [ ] One error-mapping test per error class (404, 409 unique, 422 FK, 400 validation)
- [ ] Validator unit tests (FluentValidation.TestHelper)
- [ ] Mapper unit tests

### Add After Validation (v1.x)

- [ ] Pagination/filtering/sorting on list endpoints (Sieve or Gridify) — when a consumer demonstrates the table is large enough to need it
- [ ] PATCH endpoints with JSON Merge Patch — when consumers ask for partial updates
- [ ] Custom metrics via `Meter` (e.g., per-entity create/update/delete counters) — when dashboards need them
- [ ] ETag / `If-Match` concurrency — when concurrent edits become a real concern
- [ ] Architecture tests (NetArchTest) — when team grows beyond 1-2 contributors
- [ ] Snapshot tests for ProblemDetails — when error contract stabilizes
- [ ] Migration distributed lock — when scaling beyond 1 replica
- [ ] Idempotency-Key header on POST — when clients implement retries
- [ ] `type` URI documentation pages per error type — when public docs exist

### Future Consideration (v2+)

- [ ] Soft delete as a base concern (revisit if recovery needs emerge) [OOS today]
- [ ] Authentication / authorization (when an auth boundary is defined) [OOS today]
- [ ] Multi-tenant support
- [ ] Bulk operations (POST/PATCH arrays) [OOS today]
- [ ] HATEOAS / hypermedia (unlikely to be needed)
- [ ] Rate limiting (when service becomes internet-facing)
- [ ] GraphQL or gRPC surfaces (if consumer demand emerges)
- [ ] CQRS / MediatR pipeline (if CRUD outgrows three-tier layering)
- [ ] Multiple deployable services / NuGet packaging of `BaseApi.Core` [OOS today]
- [ ] `WorkflowRunEntity` / execution result tracking [OOS — external responsibility]

---

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---------|------------|---------------------|----------|
| `AddBaseApi(...)` composition root | HIGH | LOW | P1 |
| `BaseController<,,,>` generic CRUD | HIGH | MEDIUM | P1 |
| Generic `Repository<T>` | HIGH | MEDIUM | P1 |
| EF migrations on startup | HIGH | MEDIUM | P1 |
| 3 Health probes (live/ready/startup) | HIGH | LOW | P1 |
| `AuditInterceptor` | HIGH | LOW | P1 |
| FluentValidation + `BaseEntityValidator<T>` | HIGH | LOW | P1 |
| JSON Schema validation rule (JsonSchema.Net) | HIGH | LOW | P1 |
| Cron validation rule (Cronos) | MEDIUM | LOW | P1 |
| Correlation ID middleware | HIGH | LOW | P1 |
| OTel SDK + OTLP exporter (logs + metrics) | HIGH | MEDIUM | P1 |
| OTel traces + Npgsql instrumentation | MEDIUM | LOW | P1 |
| Global `IExceptionHandler` + ProblemDetails | HIGH | LOW | P1 |
| SQLSTATE → HTTP mapping | HIGH | LOW | P1 |
| Domain exceptions (NotFound/Conflict) | HIGH | LOW | P1 |
| Mapperly per-entity mappers | HIGH | LOW | P1 |
| `IEntityMapper<,,,>` interface | MEDIUM | LOW | P1 |
| Swashbuckle + XML docs | HIGH | LOW | P1 |
| FluentValidation → Swagger schemas | MEDIUM | LOW | P1 |
| Asp.Versioning.Http (v1 URL shape) | MEDIUM | LOW | P1 |
| CORS configurable | MEDIUM | LOW | P1 |
| Testcontainers Postgres integration tests | HIGH | MEDIUM | P1 |
| Validator/mapper unit tests | MEDIUM | LOW | P1 |
| Pagination/filtering/sorting | MEDIUM | MEDIUM | P3 |
| ETag concurrency | MEDIUM | MEDIUM | P3 |
| Custom metrics (Meter) | LOW | MEDIUM | P3 |
| Authentication/Authorization | HIGH (future) | HIGH | P3 |
| Soft delete | LOW | MEDIUM | P3 |
| Bulk operations | LOW | HIGH | P3 |
| HATEOAS | LOW | HIGH | P3 |
| Rate limiting | LOW (today) | LOW | P3 |
| Idempotency-Key | LOW | MEDIUM | P3 |
| Architecture tests | LOW | MEDIUM | P3 |

**Priority key:**
- **P1** — Must have for v1
- **P2** — Should have, add when possible
- **P3** — Nice to have / future / OOS today

---

## Reference Comparison: Modern .NET 8 Base API Templates

| Feature | clean-architecture-template (jasontaylordev) | FastEndpoints templates | Our Approach (BaseApi.Core) |
|---------|--------------|--------------|--------------|
| CRUD pattern | MediatR CQRS handlers | Endpoint classes (no controllers) | Generic abstract `BaseController<,,,>` (PROJECT.md locked) |
| Validation | FluentValidation pipeline behavior (MediatR) | Built-in FluentValidation | FluentValidation auto-validation, no MediatR |
| Mapping | AutoMapper | Manual or AutoMapper | Mapperly (source-gen, AOT-safe) |
| Error handling | Behavior-based + ProblemDetails | Built-in ProblemDetails | `IExceptionHandler` + `services.AddProblemDetails()` (.NET 8 native) |
| Observability | Not opinionated | Not opinionated | OTel SDK + OTLP to Collector (PROJECT.md locked) |
| Migrations | Init container or design-time | App startup | App startup (PROJECT.md locked) |
| Auth | Identity included | Pluggable | Out of scope v1 |
| Versioning | Asp.Versioning | Built-in versioning | Asp.Versioning recommended for v1 |

The conclusion: our stack is intentionally **leaner than Clean-Architecture and more controller-centric than FastEndpoints**. Three-tier layering + generic controllers is appropriate for CRUD-only scope; CQRS would be over-engineering.

---

## Sources

Validated against current .NET 8/9 ecosystem evidence:

- [Microsoft Learn — Create a controller-based web API with ASP.NET Core 8](https://learn.microsoft.com/en-us/aspnet/core/tutorials/first-web-api?view=aspnetcore-8.0) — controller + DTO patterns (HIGH confidence)
- [Microsoft Learn — Applying EF Core Migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying) — startup migration patterns and multi-instance caveats (HIGH confidence)
- [The Reformed Programmer — How to safely apply an EF Core migrate on startup](https://www.thereformedprogrammer.net/how-to-safely-apply-an-ef-core-migrate-on-asp-net-core-startup/) — single-replica caveat (HIGH confidence — author Jon P Smith is canonical on EF Core)
- [Code-Maze — ASP.NET Core Web API Best Practices](https://code-maze.com/aspnetcore-webapi-best-practices/) — DAL/IoC/error handling conventions (MEDIUM confidence)
- [Mastering .NET API Versioning (2025 Guide)](https://developersvoice.com/blog/architecture/api-versioning-pattern/) — Asp.Versioning patterns (MEDIUM confidence)
- [GitHub — Swashbuckle.AspNetCore](https://github.com/domaindrivendev/Swashbuckle.AspNetCore) — current Swagger generator status (HIGH confidence)
- [GitHub — MicroElements.Swashbuckle.FluentValidation](https://github.com/micro-elements/MicroElements.Swashbuckle.FluentValidation) — FluentValidation → OpenAPI schema integration (HIGH confidence)
- [GitHub — dotnet/aspnet-api-versioning](https://github.com/dotnet/aspnet-api-versioning/wiki/Swashbuckle-Integration) — versioning + Swagger integration patterns (HIGH confidence)
- [Petabridge — The Easiest Way to Do OpenTelemetry in .NET: OTLP + Collector](https://petabridge.com/blog/easiest-opentelemetry-dotnet-otlp-collector/) — confirms OTLP-to-Collector as the canonical .NET 8 pattern (HIGH confidence)
- [Cronos (HangfireIO/Cronos)](https://github.com/HangfireIO/Cronos) — cron parsing library (HIGH confidence)
- [JsonSchema.Net (gregsdennis/json-everything)](https://github.com/gregsdennis/json-everything) — modern AOT-safe JSON Schema validator (HIGH confidence)
- PROJECT.md — locked decisions and Out of Scope list (authoritative for this project)

---
*Feature research for: .NET 8 Web API base library (BaseApi.Core) + service (BaseApi.Service)*
*Researched: 2026-05-26*
