# Stack Research

**Domain:** .NET 8 Web API — modular monolith CRUD service over PostgreSQL with shared base library, OTel observability, RFC 7807 errors. **v3.3.0 adds Redis (L2 materialized projection) and graph-traversal logic inside `OrchestrationService`.**
**Researched (v3.2.0 baseline):** 2026-05-26
**v3.3.0 additions researched:** 2026-05-28
**Confidence:** HIGH (versions verified against NuGet.org and vendor docs within last 30 days)

---

# Part A — v3.2.0 Baseline (LOCKED — preserved for traceability)

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

(See full v3.2.0 baseline below in **Appendix A — v3.2.0 Locked Wiring Patterns** for `Directory.Packages.props`, csproj fragments, `global.json`, and FluentValidation 12 wiring.)

---

# Part B — v3.3.0 ADDITIONS (Redis L2 Projection)

## Executive Summary (v3.3.0)

v3.3.0 adds three NuGet packages, one Docker Compose service, and zero changes to the v3.2.0 stack. The Redis projection is **whole-document overwrite via MULTI/EXEC** — not partial-field updates, not Redis modules, not Lua. The choices below optimize for: (a) matching the v3.2.0 "no NuGet packaging gap" discipline, (b) mirroring the Phase 3 D-15 per-class throwaway-DB invariant for Redis, and (c) preserving the Phase 11 D-03 "no traces backend" posture insofar as possible.

**Three NuGet pins:**
1. `StackExchange.Redis 2.13.1` — client (de facto standard, RESP-compatible across servers).
2. `OpenTelemetry.Instrumentation.StackExchangeRedis 1.15.1-beta.1` — OTel instrumentation (beta-pinned by upstream awaiting semantic-convention stabilization; this is the only OTel-blessed option).
3. `Testcontainers.Redis 4.12.0` — per-class throwaway Redis containers for xUnit v3 integration tests.

**One Docker Compose service:** `redis:7.4.2-alpine` — pinned to the 7.4.x line *deliberately* to avoid the AGPLv3 clause in Redis 8.0+. RSALv2/SSPLv1-only is acceptable for an internal service; AGPLv3's network-distribution copyleft would force legal review.

**Zero NuGet additions** for graph cycle detection — hand-roll DFS in `OrchestrationService` (~40 LOC) rather than pull `CycleDetection` or `QuickGraph`. Matches the v3.2.0 minimal-dependency posture.

**One open question** flagged as PITFALL: the OpenTelemetry Redis instrumentation is trace-side, but Phase 11 D-03 stripped `.WithTracing()`. Three options (re-add `.WithTracing()` no-exporter / wait for upstream Meter / defer Redis observability) need a roadmap-author decision.

## Recommended Additions

### Redis Client Library (Core)

| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| `StackExchange.Redis` | **2.13.1** | Redis client for the L2 projection | De facto standard for .NET, used by Stack Overflow at scale, multiplexed connection model, async-first. Underpins `Microsoft.Extensions.Caching.StackExchangeRedis`. Compatible with Redis server, Microsoft Garnet, Valkey, Azure Managed Redis (all RESP wire protocol). Last updated 2026-05-12, actively maintained. Context7-verified usage pattern (singleton `IConnectionMultiplexer`, thread-safe, `GetDatabase()` per-operation). |

### Observability (OpenTelemetry)

| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| `OpenTelemetry.Instrumentation.StackExchangeRedis` | **1.15.1-beta.1** | Auto-instruments Redis client calls into OTel pipeline | Only OTel-blessed Redis instrumentation. Beta pin is **upstream-imposed** (semantic conventions for DB clients are formally `Experimental`); the API surface itself is stable — all 1.x betas are wire-compatible. Aligns with the existing `OpenTelemetry 1.15.x` versioning the v3.2.0 stack pins. **PITFALL: trace-side only — see "OTel wiring" notes below.** |

### Test Infrastructure

| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| `Testcontainers.Redis` | **4.12.0** | Ephemeral Redis containers for xUnit integration tests | Mirrors the Phase 3 D-15 throwaway-Postgres pattern at the per-class granularity. Targets .NET 8. Same `Testcontainers` core as `Testcontainers.PostgreSql 4.11.0` (already on the v3.2.0 stack) — consistent fixture authoring style. Last updated 2026 — current and active. Ryuk-managed cleanup means the byte-identical psql-style snapshot invariant has a direct analog: `docker ps -a --filter "label=org.testcontainers" --format "{{.Names}}" | sort | sha256sum` BEFORE = AFTER. |

### Local Dev Infrastructure

| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| `redis:7.4.2-alpine` (Docker image) | **7.4.2-alpine** | Redis server for `compose.yaml` local dev | **7.4.x deliberately chosen over 8.0+** — 7.4.x is dual-licensed RSALv2/SSPLv1 (no AGPLv3 viral-network-distribution clause); 8.0+ adds AGPLv3 to the tri-license, which forces a copyleft posture for any networked modifications. The sk_p service has no need for 8.0 features (no JSON/Search modules used; vector search not in scope). 7.4.2 is a recent patch in the 7.4 line. Alpine variant matches the `postgres:17-alpine` / `elasticsearch:8.15.5` (full image, ES has no alpine) / `prom/prometheus:v3.11.3` pinning discipline. Resource footprint: ~30MB image vs ~110MB Debian-based. |

### Graph Algorithms

| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| **(none — hand-roll)** | n/a | DFS cycle detection in `OrchestrationService` | The traversal is ~40 LOC: `HashSet<Guid> visited`, `HashSet<Guid> recursionStack`, recursive DFS from each `Workflow.EntryStepIds[*]` via `StepEntity.NextStepIds`. Existing NuGet options (`CycleDetection 2.0.0`, `QuickGraph`) are heavyweight for this micro-use; both pull abstractions (general graph types, edge weighting, dependency-sort) the codebase will not reuse. Adding a dependency for ~40 LOC violates the "no NuGet packaging gap" discipline that held across v3.2.0's 11 phases. Hand-roll inside `OrchestrationService` as a private method; unit-test in isolation. |

## v3.2.0 Locked Stack (DO NOT TOUCH — cross-reference only)

These are **NOT** under review for v3.3.0 — listed only so roadmap authors can verify integration assumptions.

| Category | Locked Tech | Version |
|----------|-------------|---------|
| Runtime | .NET | 8.0.421 (global.json) |
| ORM | EF Core + Npgsql | EF Core 8.0.27 + Npgsql 8.0.10 (provider) / Npgsql 8.0.9 (client driver) |
| Database | PostgreSQL | 17-alpine |
| Validation | FluentValidation | 12.1.1 |
| Mapping | Mapperly | 4.3.1 (source-generated) |
| Observability SDK | OpenTelemetry .NET SDK | 1.15.3 |
| HTTP Versioning | Asp.Versioning.Http | 8.1.0 |
| JSON Schema | JsonSchema.Net | 9.2.1 (draft 2020-12, SSRF-disabled) |
| Cron | Cronos | 0.13.0 (5-field) |
| Test framework | xUnit v3 + WebApplicationFactory | 3.2.2 + 8.0.27 (per-class throwaway Postgres) |
| Compose stack | Postgres 17-alpine + Elasticsearch 8.15.5 + Prometheus v3.11.3 + OTel Collector contrib 0.152.0 | (Redis 7.4.2-alpine joins this stack) |

## Alternatives Considered (v3.3.0)

| Category | Recommended | Alternative | Why Not |
|----------|-------------|-------------|---------|
| Client library | `StackExchange.Redis 2.13.1` | **Microsoft Garnet (server only)** | Garnet is a *server*, not a client. It speaks RESP, so `StackExchange.Redis` works against it. Garnet on the *server* side is interesting (3-10× throughput, .NET native, sub-300μs p99.9) but introduces a non-standard server image. Defer — revisit if/when L2 throughput proves to be a bottleneck. Swap is zero code change because both speak RESP. |
| Client library | `StackExchange.Redis 2.13.1` | `StackExchange.Redis.Extensions.Core 12.2.0` | Higher-level wrapper adding pooling, complex-object helpers, compression. Adds an abstraction layer that hides the raw `IDatabase` API the orchestration code needs for `ITransaction`. Base library already does what v3.3.0 needs; the extensions package solves problems sk_p doesn't have (large object serialization, pub/sub patterns, geospatial). |
| Client library | `StackExchange.Redis 2.13.1` | `NRedisStack 1.3.0` (official Redis client) | Targets Redis Stack server (modules: RedisJSON, RediSearch, RedisTimeSeries, RedisBloom). sk_p uses none of these modules. Adds infrastructure complexity (different Docker image: `redis/redis-stack:latest`) and a wrapper dependency on top of `StackExchange.Redis`. |
| Serialization | **System.Text.Json strings on RedisString** | `RedisHash` (HSET per-field) | RedisHash gives field-level updates but the L2 DTOs are written **whole** in each Start call (idempotent overwrite, not field-level mutation). HSET adds complexity (multiple commands per record, no native nested-object support — `liveness{timestamp,interval,status}` would need flattening, manual field-typing) for zero benefit when the write pattern is "replace entire JSON document." |
| Serialization | **System.Text.Json strings on RedisString** | RedisJSON module (`NRedisStack`) | RedisJSON requires the Redis Stack server build (or the JSON module loaded into vanilla Redis). Adds infrastructure complexity (different Docker image), adds a NuGet dep, and provides features sk_p doesn't need (JSONPath queries, atomic field updates, indexed search). Whole-document overwrite is exactly what RedisString does best with `SET`. **Also keeps the alpine image at ~30MB vs Redis Stack at ~400MB+.** |
| Atomic write | **`ITransaction` (MULTI/EXEC)** | Pipelining via `IBatch` | `IBatch` reduces round-trips but provides **no atomicity** between commands. v3.3.0 needs all-or-nothing for a Start call's projection set so a crash mid-write doesn't leave half-projected workflows. `ITransaction` extends `IBatch` (inherits batching benefit) and adds MULTI/EXEC atomicity. |
| Atomic write | **`ITransaction` (MULTI/EXEC)** | Lua scripts (`ScriptEvaluate`) | Lua wins when conditional read-then-write is needed (MULTI/EXEC cannot read intermediate results in the same transaction). v3.3.0 Start is unconditional overwrite — no read-then-decide logic — so Lua is over-engineered. Revisit if Stop ever needs "delete only if JobId matches" (CAS pattern). |
| Atomic write | **`ITransaction` (MULTI/EXEC)** | RENAME-based atomic swap | Pattern: write to temp keys, RENAME atomically. Works for single-key replacement but breaks for the v3.3.0 multi-key projection — one Start creates N `{workflowId}` + M `{workflowId:stepId}` + K `{processorId}` keys; RENAME is per-key, not group. Would still need a transaction to make the renames atomic, defeating the simplification. |
| Atomic write | **`ITransaction` (MULTI/EXEC)** | WATCH/MULTI/EXEC (optimistic concurrency) | The spec explicitly says "last-write-wins, no Redis lock" for concurrent Starts. WATCH adds retry-on-conflict logic that contradicts the requirement. |
| Test isolation | **Per-class ephemeral container (Testcontainers.Redis)** | Shared container + `FLUSHDB` between tests | Shared+flush is faster but fragile: any test that forgets to flush leaks state into the next. The Phase 3 D-15 invariant (byte-identical psql `\l` snapshot proves zero leaked DBs) is the locked discipline; mirror it for Redis with ephemeral containers, not shared+flush. |
| Test isolation | **Per-class ephemeral container** | Shared container + Redis DB index (SELECT 0..15) | Redis docs explicitly call multi-DB **an anti-pattern** for application data separation. All 16 indices share memory/CPU/connection pool. Test failure isolation is worse than ephemeral containers (a hung connection on DB 3 affects DB 4). Also: doesn't scale — 17th test class breaks. |
| Test isolation | **Per-class ephemeral container** | Shared container + keyspace prefixing (`test_{guid}:*`) | Requires every test-touched code path to honor the prefix — high-friction discipline that's easy to violate accidentally (a missing prefix in the projection writer would silently corrupt other test runs). Ephemeral container forces isolation at the network level. |
| Test isolation | **Per-class ephemeral container** | In-memory Redis emulator (e.g., `FakeItEasy` Redis mock, Microsoft.Extensions.Caching.Memory) | Doesn't test actual Redis behavior (MULTI/EXEC semantics, RESP wire format, network errors). The Phase 3 "real Postgres in tests" discipline applies here too — fake the I/O, fake the bugs. |
| Graph cycle detection | **Hand-roll DFS in OrchestrationService** | `CycleDetection 2.0.0` NuGet | Tarjan's algorithm wrapped for general dependency-sort use cases. Pulls graph abstractions sk_p will not reuse. ~40 LOC of `OrchestrationService` private code is bisect-friendlier than a black-box dependency for a one-call use case. |
| Graph cycle detection | **Hand-roll DFS** | `QuickGraph` | Mature general-purpose graph library; way over-scoped for one-DFS-per-workflow. ~500KB DLL for ~40 LOC of logic. |
| Redis server version | **7.4.2-alpine** | 8.0+-alpine | Tri-license including AGPLv3 (8.0+) introduces a copyleft network-distribution clause. 7.4.x's RSALv2/SSPLv1 is restrictive but does not have the viral network-distribution clause. sk_p uses none of the 8.0+ new features (Vector Sets, time-series improvements, etc.). |
| Redis server version | **7.4.2-alpine** | 7.2-alpine | Older, no licensing benefit, missing 7.4.x fixes/improvements (notably client-side caching v2, hash field expiration). |
| Redis server version | **7.4.2-alpine** | **Valkey (BSD fork, Linux Foundation governed)** | Valkey is BSD-licensed (cleaner license posture than even 7.4.x) and Linux Foundation governed. Strong candidate **but** introduces an unfamiliar server image; the existing compose stack pins are all "official upstream" projects (Postgres official, ES official, Prom official, Collector contrib). Valkey breaks that "official upstream" pattern. **Flag as PITFALL/future-revisit** — if Redis licensing becomes a procurement blocker, swap is one-line in `compose.yaml` and zero code change (Valkey is RESP-compatible — `StackExchange.Redis` connects natively). |

## What NOT to Use (v3.3.0)

| Avoid | Why | Use Instead |
|-------|-----|-------------|
| `Microsoft.Extensions.Caching.StackExchangeRedis` | Wraps `StackExchange.Redis` as `IDistributedCache` — opaque byte-array interface. The L2 projection is not an opaque cache; consumers read typed JSON keys with specific schemas. | Direct `IConnectionMultiplexer` / `IDatabase` from `StackExchange.Redis` |
| `ServiceStack.Redis` | Commercial license over the free quota (6000 requests/hour or 20 ops/hour limits depending on tier). | `StackExchange.Redis` (MIT) |
| `BookSleeve` | Predecessor to `StackExchange.Redis` — abandoned in 2014. | `StackExchange.Redis` |
| Redis Stack (`redis/redis-stack:*`) image | Adds RedisJSON / RediSearch / RedisTimeSeries / RedisBloom modules that v3.3.0 doesn't use. 10× the image size. | `redis:7.4.2-alpine` |
| `redis:latest` floating tag | Violates the v3.2.0 pinning discipline (Postgres 17-alpine, ES 8.15.5, Prom v3.11.3, Collector 0.152.0 all explicitly pinned). Floats to 8.x and inherits AGPLv3. | `redis:7.4.2-alpine` explicit pin |
| `FLUSHALL` / `FLUSHDB` in test teardown | Fragile across parallel test runs; doesn't isolate from concurrent test classes. | Per-class `Testcontainers.Redis` (one container per fixture; ephemeral) |
| In-memory mock (e.g., `Moq<IConnectionMultiplexer>` returning canned `IDatabase`) | Doesn't exercise MULTI/EXEC semantics, RESP wire format, or actual reconnection behavior. Same anti-pattern as `UseInMemoryDatabase` was for EF Core. | Testcontainers.Redis (real Redis) |
| `redis-cli MONITOR` for test assertions | Production-impacting in real Redis (blocks server); fragile and slow for tests. | Assert via `IDatabase.KeyExistsAsync` / `StringGetAsync` against the per-class container. |
| `KEYS pattern` for Stop's "delete by prefix" | O(N) blocking command — explicitly forbidden by Redis docs for production. | `SCAN` cursor-based iteration + `KeyDelete` (in a transaction if atomicity needed) |
| `WAIT` command for "wait for replication" | No replicas in v3.3.0 (single-node compose Redis). | Don't use; revisit if replication is added. |

## Installation

### NuGet packages — add to `Directory.Packages.props` (extend the existing CPM file)

Append these three entries to the existing `<ItemGroup>` in `Directory.Packages.props`:

```xml
<!-- v3.3.0 — Redis L2 projection -->
<PackageVersion Include="StackExchange.Redis" Version="2.13.1" />
<PackageVersion Include="OpenTelemetry.Instrumentation.StackExchangeRedis" Version="1.15.1-beta.1" />
<PackageVersion Include="Testcontainers.Redis" Version="4.12.0" />
```

### csproj additions

`src/BaseApi.Service/BaseApi.Service.csproj` (the OrchestrationService lives here per Phase 9):

```xml
<PackageReference Include="StackExchange.Redis" />
<PackageReference Include="OpenTelemetry.Instrumentation.StackExchangeRedis" />
```

`tests/BaseApi.Tests/BaseApi.Tests.csproj`:

```xml
<PackageReference Include="Testcontainers.Redis" />
```

**Note:** `StackExchange.Redis` MAY also belong in `BaseApi.Core` if you anticipate the Redis-client lifetime extension (`AddBaseApiRedis(...)`) being a reusable base concern alongside `AddBaseApiObservability`. Reasonable either way; recommendation is **start in Service**, lift to Core only if a second service is ever spun up (which Option B locked out).

### Docker Compose addition (`compose.yaml`)

Add this service alongside Postgres / Elasticsearch / Prometheus / OTel Collector:

```yaml
redis:
  image: redis:7.4.2-alpine
  container_name: sk-redis
  ports:
    - "6379:6379"
  healthcheck:
    test: ["CMD", "redis-cli", "ping"]
    interval: 5s
    timeout: 3s
    retries: 5
    start_period: 5s
  restart: unless-stopped
  # Note: no volume mount — L2 is a materialized projection, not a source of truth.
  # Restart from L3 (Postgres) is the recovery path; persistence is intentional waste.
  command: ["redis-server", "--save", "", "--appendonly", "no"]
```

The `--save "" --appendonly no` disables RDB snapshots and AOF — the L2 projection is rebuildable from L3 (Postgres) on demand, so persistence is dead weight and slows startup.

### appsettings.json / appsettings.Development.json addition

```json
{
  "ConnectionStrings": {
    "Redis": "localhost:6379,abortConnect=false,connectTimeout=5000,syncTimeout=5000"
  }
}
```

In Docker Compose / Production, override via env var: `ConnectionStrings__Redis=redis:6379,abortConnect=false`.

## Integration Notes for Roadmap Authors

### ConnectionMultiplexer DI lifetime (singleton, lazy-initialized)

The `ConnectionMultiplexer` is the central thread-safe, multiplexed connection object — Context7-confirmed against `https://stackexchange.github.io/StackExchange.Redis/Basics`: *"is intended to be shared and reused throughout an application rather than created per operation. It is fully thread-safe."*

**MUST be a singleton** — per-request construction defeats the multiplexing model, exhausts the file descriptor budget under load, and causes the "Multiple Connection Instances" pitfall reported by `StackExchange.Redis` issue #507.

Recommended ASP.NET Core registration (mirrors the existing `AddBaseApi<TDbContext>` composition root style — add to a new `AddBaseApiRedis(...)` extension or inline in `Program.cs` under the existing 10-line cap):

```csharp
services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var connectionString = sp.GetRequiredService<IConfiguration>()
        .GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis is required");

    var options = ConfigurationOptions.Parse(connectionString);
    options.AbortOnConnectFail = false;   // resilient — let it reconnect in background
    options.ClientName = "sk-api";         // shows up in CLIENT LIST for ops debugging
    return ConnectionMultiplexer.Connect(options);
});
```

Then inject `IConnectionMultiplexer` (or a thin wrapper like `IL2ProjectionStore`) into `OrchestrationService`. Call `_multiplexer.GetDatabase()` per operation — `IDatabase` is a lightweight wrapper over the multiplexer and should NOT be cached as a field.

**Pitfalls to flag in PITFALLS.md:**
1. **Synchronous `Connect()` in `ConfigureServices` blocks startup if Redis is unreachable.** `AbortOnConnectFail = false` fixes this (Connect returns even on failure; client retries in background). Without this flag, a Redis outage at boot kills the API.
2. **Injecting `IDatabase` directly is OK but mildly fragile** — if you ever switch to RedisCluster (multiple multiplexers), an injected `IDatabase` will route to the wrong endpoint. Inject `IConnectionMultiplexer` and call `GetDatabase()` for future-proofing.
3. **Singleton `OrchestrationService`?** Currently scoped (per the v3.2.0 service-layer convention). Scoped is fine — `IConnectionMultiplexer` is captured by reference, singleton-lifetimed underneath. Don't change to Singleton without thinking about other scoped deps (DbContext is scoped — Singleton would break the DbContext lifetime).
4. **Liveness probe should NOT touch Redis** (Phase 5 D-15 / Pitfall 15 generalization — live never touches dependencies). Readiness probe MAY add a Redis ping; **recommendation: defer Redis readiness check** to avoid coupling readiness to a non-source-of-truth dependency. The system is correct without Redis (graceful degradation: Start fails 503, CRUD still works).

### Atomic write primitive choice for v3.3.0

For the "idempotent overwrite of N workflow projections in one Start call":

```csharp
var db = _multiplexer.GetDatabase();
var tx = db.CreateTransaction();

// Fire-and-don't-await — the inner Tasks complete after ExecuteAsync returns.
// StackExchange.Redis idiom: discard return; values are queued, executed atomically on EXEC.
foreach (var (workflowId, projection) in workflowProjections)
{
    _ = tx.StringSetAsync(workflowId.ToString(), JsonSerializer.Serialize(projection));
}
foreach (var (compoundKey, stepProj) in stepProjections)
{
    _ = tx.StringSetAsync(compoundKey, JsonSerializer.Serialize(stepProj));
}
foreach (var (processorId, procProj) in processorProjections)
{
    _ = tx.StringSetAsync(processorId.ToString(), JsonSerializer.Serialize(procProj));
}

bool committed = await tx.ExecuteAsync();
if (!committed) throw new RedisException("L2 projection commit failed (transaction aborted)");
```

For Stop's delete-all-keys-for-workflow:

```csharp
var db = _multiplexer.GetDatabase();
var server = _multiplexer.GetServer(_multiplexer.GetEndPoints().First());
var keys = server.Keys(pattern: $"{workflowId}*").ToArray();  // SCAN-based, NOT KEYS
if (keys.Length > 0)
{
    var tx = db.CreateTransaction();
    _ = tx.KeyDeleteAsync(keys);
    await tx.ExecuteAsync();
}
```

**Idempotent overwrite semantics fall out naturally:** `StringSet` (the SET command) is unconditional replace by default. Re-running Start with the same WorkflowIds simply replaces the keys with identical-or-updated content. No need for SETNX / SETEX / EXISTS-checks.

### OpenTelemetry wiring — PITFALL flag

`OpenTelemetry.Instrumentation.StackExchangeRedis` 1.15.1-beta.1 adds instrumentation to the **`.WithTracing()`** pipeline — but Phase 11 D-03 **stripped `.WithTracing()` from `AddBaseApiObservability`** (no traces backend in v1).

**Three options, must be decided in roadmap:**

- **Option A (recommended for v3.3.0):** Re-add `.WithTracing()` to `AddBaseApiObservability` with **no exporter** (or `ConsoleExporter` in Dev only) purely to enable the Redis instrumentation hook. The instrumentation also surfaces some metrics under `db.client.*` that flow through the existing Prometheus exporter. **Cost:** revisits a locked Phase 11 decision; requires updating the rationale text in `REQUIREMENTS.md` (OBSERV-13/14 area).
- **Option B (deferral):** Ship v3.3.0 without Redis observability. Add a `// TODO(v3.4): wire Redis OTel instrumentation` marker in `AddBaseApiObservability`. Acceptable because L2 is internal-only and the existing Postgres / HTTP server / collector metrics provide most observability needs.
- **Option C (replace with custom):** Add `Activity` manually around L2 read/write code paths in `OrchestrationService` (no instrumentation package). Hand-roll OTel spans using `ActivitySource.StartActivity`. Heaviest manual lift but doesn't reopen Phase 11 D-03.

**Recommendation:** Option B for v3.3.0 ship — observability is not a stated v3.3.0 requirement, and re-opening Phase 11 D-03 mid-milestone is a context-switch tax. Plan Option A for v3.4 explicitly.

### Test infrastructure (Testcontainers.Redis fixture)

Mirror the existing `Phase8WebAppFactory` pattern. Sketch (per-class fixture, xUnit v3 syntax):

```csharp
public sealed class RedisPerClassFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder()
        .WithImage("redis:7.4.2-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();
    public async ValueTask DisposeAsync()    => await _container.DisposeAsync();
}

[Collection(...)]  // or [Class] fixture in xUnit v3
public sealed class StartOrchestrationL2Facts(RedisPerClassFixture redisFx, /* PostgresFx, */ ...)
    : IClassFixture<RedisPerClassFixture>
{
    // tests use redisFx.ConnectionString to override DI in the WebAppFactory
}
```

A `Phase12WebAppFactory : Phase8WebAppFactory` overrides `IConnectionMultiplexer` registration with `ConnectionMultiplexer.Connect(redisFixture.ConnectionString)` in `ConfigureWebHost`.

**Snapshot invariant for the close gate** (analog of psql `\l` SHA-256):
```powershell
docker ps -a --filter "label=org.testcontainers" --format "{{.Names}}" | Sort-Object | Out-String | %{ [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($_))) }
```
Run BEFORE and AFTER the suite — should be byte-identical (Testcontainers self-cleans via Ryuk container, mirroring how the Postgres test class self-cleans).

### Mapperly compatibility

Mapperly is source-generated and operates on **C# type-shape mapping**. It will map your **L1 in-memory types → L2 DTOs** (the Redis-bound shapes) as usual. The L2 DTOs are then serialized via `System.Text.Json` (separate concern from Mapperly).

**No new Mapperly attributes needed.** The existing `IEntityMapper<TEntity,TCreate,TUpdate,TRead>` contract is for **EF entities** and doesn't apply to L2 projection shapes. Write a separate Mapperly mapper (e.g., `IL2ProjectionMapper` with methods `ToWorkflowProjection(WorkflowL1 src)`, `ToStepProjection(StepL1 src)`, `ToProcessorProjection(ProcessorL1 src)`) following the same per-feature-folder convention. Promote the same RMG007/RMG012/RMG020/RMG089 build-error discipline.

### System.Text.Json configuration

Use the application's default `JsonSerializerOptions` from DI if possible (matches the ASP.NET Core HTTP-layer serialization for consistency). Recommended options:

```csharp
new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,   // matches HTTP API surface
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,  // L2 consumers want explicit nulls
    WriteIndented = false                                 // compact storage
}
```

**Naming critical:** the L2 DTO field names are locked to `inputDefinition` / `outputDefinition` (per CONTEXT — "NOT `definitionIn` / `definitionOut`"). Verify via a Mapperly attribute or test that the serialized output matches the spec.

## Version Compatibility (v3.3.0 additions)

| Package A | Compatible With | Notes |
|-----------|-----------------|-------|
| `StackExchange.Redis 2.13.1` | .NET Standard 2.0, .NET Framework 4.6.1+ | Targets .NET Standard 2.0 → works on .NET 8 transparently. No conflict with EF Core 8 / Npgsql 8.x. |
| `StackExchange.Redis 2.13.1` | `redis:7.x` and `redis:8.x` servers (RESP2/RESP3) | Connects to anything that speaks RESP. Negotiates RESP3 on Redis 6+ for richer types. |
| `OpenTelemetry.Instrumentation.StackExchangeRedis 1.15.1-beta.1` | `OpenTelemetry 1.15.x` | Matches the v3.2.0 OTel pin. Targets `StackExchange.Redis 2.x`. |
| `OpenTelemetry.Instrumentation.StackExchangeRedis 1.15.1-beta.1` | OpenTelemetry SDK trace pipeline | **Requires `.WithTracing()` configured** — see Pitfall above. |
| `Testcontainers.Redis 4.12.0` | .NET 8.0+, .NET Standard 2.0 | Same `Testcontainers` core as the v3.2.0 `Testcontainers.PostgreSql 4.11.0`. Docker daemon required (already required by v3.2.0). |
| `redis:7.4.2-alpine` server | `StackExchange.Redis 2.x` client | Full RESP3 support, MULTI/EXEC, SCAN, all primitives v3.3.0 uses. |

## Sources

### Authoritative (HIGH confidence) — verified via Context7 or vendor docs

- [StackExchange.Redis 2.13.1 — NuGet](https://www.nuget.org/packages/StackExchange.Redis/) — current version, .NET Standard 2.0, last updated 2026-05-12
- [StackExchange.Redis Basics — official docs (Context7-verified `/websites/stackexchange_github_io_stackexchange_redis`)](https://stackexchange.github.io/StackExchange.Redis/Basics) — singleton pattern, thread-safety contract
- [StackExchange.Redis Transactions — official docs](https://stackexchange.github.io/StackExchange.Redis/Transactions.html) — `CreateTransaction()` MULTI/EXEC semantics
- [StackExchange.Redis Configuration — official docs](https://stackexchange.github.io/StackExchange.Redis/Configuration.html) — `AbortOnConnectFail`, `ClientName`, parse syntax
- [OpenTelemetry.Instrumentation.StackExchangeRedis 1.15.1-beta.1 — NuGet](https://www.nuget.org/packages/OpenTelemetry.Instrumentation.StackExchangeRedis/) — current version, semantic conventions Experimental
- [OpenTelemetry .NET Contrib — StackExchangeRedis CHANGELOG](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.StackExchangeRedis/CHANGELOG.md) — beta versioning rationale
- [Testcontainers.Redis 4.12.0 — NuGet](https://www.nuget.org/packages/Testcontainers.Redis) — current version
- [Testcontainers for .NET — xUnit integration](https://dotnet.testcontainers.org/test_frameworks/xunit_net/) — `IAsyncLifetime` fixture pattern (per-class isolation)
- [Redis Official Docker Image — Docker Hub](https://hub.docker.com/_/redis) — 7.4-alpine tag chain
- [Redis 8.0 tri-license announcement — Redis blog](https://redis.io/blog/agplv3/) — AGPLv3 addition in 8.0
- [Redis license change — Percona analysis](https://www.percona.com/blog/the-redis-license-has-changed-what-you-need-to-know/) — RSALv2/SSPLv1 vs AGPLv3 implications
- [Redis Returns to Open Source under AGPL — InfoQ](https://www.infoq.com/news/2025/05/redis-agpl-license/) — community context for license change

### Verified secondary (MEDIUM confidence)

- [Microsoft Garnet (server) — GitHub](https://github.com/microsoft/garnet) — RESP-compatible server, deferred alternative
- [Garnet performance benchmarks](https://microsoft.github.io/garnet/docs/benchmarking/results-resp-bench) — informs "wait for bottleneck" deferral rationale
- [NRedisStack 1.3.0 — NuGet](https://www.nuget.org/packages/NRedisStack/) — RedisJSON client (rejected for this milestone)
- [Redis databases anti-pattern — SudoAll](https://sudoall.com/redis-databases-antipattern/) — rationale for rejecting SELECT-based test isolation
- [Redis Best Practices for Connection Resilience — Azure docs](https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/cache-best-practices-connection) — guidance applies regardless of hosting
- [Connection Singleton Instance issue — StackExchange.Redis #507](https://github.com/StackExchange/StackExchange.Redis/issues/507) — DI pitfall documentation
- [Redis Pipelining, Transactions, and Lua Scripts comparison](https://rafaeleyng.github.io/redis-pipelining-transactions-and-lua-scripts) — atomic-write primitive tradeoff analysis

### Comparative / community (informs alternatives sections)

- [Valkey (BSD fork) — valkey.io](https://valkey.io/) — Linux-Foundation-governed BSD alternative if licensing escalates
- [Redis Stack Docker image](https://hub.docker.com/r/redis/redis-stack) — RedisJSON server alternative (rejected: 10× size, modules unused)
- [Testcontainers Best Practices for .NET](https://www.milanjovanovic.tech/blog/testcontainers-best-practices-dotnet-integration-testing) — per-class fixture lifetime rationale
- [Sharing a Redis Container with Testcontainers (Medium)](https://omerugi.medium.com/boost-your-integration-tests-sharing-a-redis-container-with-testcontainers-for-net-8fe8c01d98ec) — counterpoint (shared+flush — rejected for sk_p discipline)

### Confidence assessment (v3.3.0 additions)

| Item | Confidence | Notes |
|------|------------|-------|
| `StackExchange.Redis` 2.13.1 choice | HIGH | Context7-verified, NuGet-verified, de facto standard since 2014 |
| `OpenTelemetry.Instrumentation.StackExchangeRedis` 1.15.1-beta.1 | MEDIUM-HIGH | Beta pin is upstream-imposed (semantic-convention experimental status), not API-stability concern. Only OTel-blessed option. |
| `Testcontainers.Redis` 4.12.0 | HIGH | NuGet-verified, mirrors proven Postgres pattern with identical `IAsyncLifetime` shape |
| `redis:7.4.2-alpine` server pin | MEDIUM-HIGH | 7.4 line confirmed extant on Docker Hub; verify exact 7.4.x patch (2/3/4) at implementation time — minor patch level is fluid |
| System.Text.Json on RedisString choice | HIGH | Native to .NET 8, no dep, fits whole-document overwrite pattern exactly |
| `ITransaction` (MULTI/EXEC) for atomic writes | HIGH | StackExchange.Redis docs explicit on this idiom; matches "idempotent overwrite of N keys" requirement |
| Hand-roll DFS over NuGet | HIGH | Pragmatic call; aligns with v3.2.0 "no NuGet packaging gap" discipline; ~40 LOC is well under the bar for a dependency |
| OTel `.WithTracing()` revival decision | LOW | Genuinely needs roadmap-author decision; recommendation is Option B (defer Redis observability to v3.4) |

---

# Appendix A — v3.2.0 Locked Wiring Patterns (preserved for reference)

## Installation (v3.2.0 baseline — already in place)

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

    <!-- v3.3.0 — Redis L2 projection (NEW) -->
    <PackageVersion Include="StackExchange.Redis" Version="2.13.1" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.StackExchangeRedis" Version="1.15.1-beta.1" />
    <PackageVersion Include="Testcontainers.Redis" Version="4.12.0" />
  </ItemGroup>
</Project>
```

## FluentValidation 12 Wiring Pattern (v3.2.0 locked)

`services.AddValidatorsFromAssembly(typeof(BaseEntityValidator<>).Assembly, lifetime: ServiceLifetime.Scoped)` — scoped lifetime, no `AddFluentValidation`, manual `IValidator<T>.ValidateAsync` invocation in `BaseService<...>`. See v3.2.0 PROJECT.md for the locked pattern; v3.3.0 adds `WorkflowIdsValidator` (Phase 9) and may add new orchestration-related validators following the same pattern.

## Sources (v3.2.0 baseline)

### Official / NuGet (HIGH confidence) — verified 2026-05-26 for v3.2.0 baseline
- [NuGet: Microsoft.EntityFrameworkCore](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore) — 8.0.27 latest 8.0.x (2026-05-12)
- [NuGet: Microsoft.AspNetCore.Mvc.Testing](https://www.nuget.org/packages/Microsoft.AspNetCore.Mvc.Testing) — 8.0.27 latest 8.0.x (2026-05-12)
- [NuGet: Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/8.0.10)
- [NuGet: Riok.Mapperly 4.3.1](https://www.nuget.org/packages/Riok.Mapperly)
- [NuGet: FluentValidation 12.1.1](https://www.nuget.org/packages/fluentvalidation/)
- [NuGet: OpenTelemetry 1.15.3](https://www.nuget.org/packages/OpenTelemetry)
- [NuGet: JsonSchema.Net 9.2.1](https://www.nuget.org/packages/JsonSchema.Net)
- [NuGet: Cronos 0.13.0](https://www.nuget.org/packages/Cronos)
- [NuGet: Testcontainers.PostgreSql 4.11.0](https://www.nuget.org/packages/Testcontainers.PostgreSql)
- [NuGet: xunit.v3 3.2.2](https://www.nuget.org/packages/xunit.v3)
- [postgres official Docker image](https://hub.docker.com/_/postgres) — 17-alpine tag chain
- [FluentValidation 12 upgrade guide](https://docs.fluentvalidation.net/en/latest/upgrading-to-12.html)

---
*v3.2.0 baseline researched: 2026-05-26 — preserved verbatim for traceability*
*v3.3.0 additions researched: 2026-05-28 — Redis L2 projection (3 NuGet pins + 1 compose service + 0 graph deps)*
