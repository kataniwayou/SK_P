# ARCHITECTURE — v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline

**Domain:** Redis-backed materialized projection layer added to an existing .NET 8 modular monolith
**Researched:** 2026-05-28
**Verification posture:** Composition-root decisions and Phase 7 D-13 precedent verified against actual source files at `src/BaseApi.Core/DependencyInjection/*.cs` (HIGH); Redis-specific recommendations verified against StackExchange.Redis maintainer guidance + AspNetCore.Diagnostics.HealthChecks Xabaril package + Testcontainers.Redis NuGet (HIGH for stack choice, MEDIUM for sequencing tactics).

---

## 1. Where the Redis ConnectionMultiplexer lives in the DI graph

### Recommendation: **NEW `AddBaseApiRedis` extension on `IServiceCollection`, chained inside `AddBaseApi<TDbContext>` as call #7** — *not* a separate `IHostApplicationBuilder` overload.

**Rationale — the Phase 7 D-13 precedent does NOT apply here.**

The Phase 7 D-13 split (`AddBaseApiObservability` on `IHostApplicationBuilder`, everything else on `IServiceCollection`) exists for one specific reason documented verbatim at `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs:23-26` and again at `BaseApiServiceCollectionExtensions.cs:34-35`:

> "`builder.Logging.AddOpenTelemetry` requires the `ILoggingBuilder` surface (not `IServiceCollection`). The host builder gives access to both `.Logging` and `.Services`. This is the engineering necessity behind the CONTEXT D-13 amendment."

`StackExchange.Redis.ConnectionMultiplexer.Connect(...)` registration needs **only `IServiceCollection`** — there is no MEL bridge equivalent, no `builder.Logging` surface, no `builder.Configuration` surface that isn't already reachable via the `IConfiguration` parameter that `AddBaseApi<TDbContext>` already accepts. Mirroring the observability split "for symmetry" would invent a discipline the engineering doesn't require, and would force the `Program.cs` body cap (7 non-trivial lines today, 10 ceiling) upward without justification.

**Registration shape** (mirrors `PersistenceServiceCollectionExtensions.cs:31` `cfg.RequireConnectionString("Postgres")` pattern):

```csharp
// New file: src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs
internal static class RedisServiceCollectionExtensions
{
    internal static IServiceCollection AddBaseApiRedis(
        this IServiceCollection services, IConfiguration cfg)
    {
        var connStr = cfg.RequireConnectionString("Redis");   // WR-03 fail-fast (Phase 5 precedent)
        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(connStr));
        services.AddSingleton(sp =>
            sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase());
        services.Configure<RedisProjectionOptions>(cfg.GetSection("Redis"));
        return services;
    }
}
```

`IConnectionMultiplexer` as singleton is the StackExchange.Redis maintainer-blessed pattern — thread-safe, expensive to construct, designed for long-lived reuse. `IDatabase` (resolved per-call from the singleton) is the consumer surface; it is cheap and stateless.

**Chained into `AddBaseApi<TDbContext>` as call #7** (file `BaseApiServiceCollectionExtensions.cs:27-33`):

```csharp
=> services
    .AddBaseApiPersistence<TDbContext>(cfg)        // 1 — Phase 3
    .AddBaseApiHealth(cfg)                         // 2 — Phase 5 (now includes Redis check — see §3)
    .AddBaseApiErrorHandling()                     // 3 — Phase 4
    .AddBaseApiHttp(cfg)                           // 4 — Phase 7
    .AddBaseApiValidation(typeof(TDbContext).Assembly)  // 5 — Phase 6
    .AddBaseApiMapping(typeof(TDbContext).Assembly)     // 6 — Phase 6
    .AddBaseApiRedis(cfg);                         // 7 — Phase 12 NEW
```

**Trade-offs vs alternatives:**

| Option | Verdict | Reason |
|---|---|---|
| **A. `AddBaseApiRedis(IServiceCollection)` chained into `AddBaseApi`** ✅ | Recommended | Honors Plan 07-01 6-extensions-chained convention; one `Program.cs` line unchanged |
| B. `AddBaseApiRedis(IHostApplicationBuilder)` separate from `AddBaseApi` | Rejected | No engineering reason (no `.Logging` need); introduces a precedent that future infra additions also need a separate hook |
| C. Inline `services.AddSingleton<IConnectionMultiplexer>(...)` in `AppFeatures.AddAppFeatures` | Rejected | Couples a `BaseApi.Core` infrastructure concern to `BaseApi.Service`'s feature aggregator |

Confidence: **HIGH**.

---

## 2. Where the L1 build logic lives

### Recommendation: **`OrchestrationService.StartAsync` directly orchestrates, but L1 build is extracted to NEW seams: `IWorkflowGraphLoader` + `IWorkflowGraphValidator` + `IRedisProjectionWriter`.**

The current `OrchestrationService` (`OrchestrationService.cs:37-129`) is intentionally NOT `BaseService`-derived. It already pre-injects all 5 entity mappers "for v2 ctor stability" per CONTEXT D-05. v3.3.0 is that future phase.

The orchestration logic is **not** "one method that does everything" — it's a 6-stage pipeline (existence → cycles → schema-edge → payload-schema → L1 build → L2 write → cleanup), and the existing single-method `ValidateWorkflowIdsAsync` is exactly the wrong shape for that.

### Proposed decomposition

```
OrchestrationService.StartAsync(ids)             // orchestrates, does NOT do logic
  ├─ ValidateWorkflowIdsAsync (existing)         // step 1 (existence, REUSED)
  ├─ IWorkflowGraphLoader.LoadL1Async(ids)       // step 2 (L3 fetch + L1 build)
  │     returns transient WorkflowGraphSnapshot
  ├─ IWorkflowGraphValidator.Validate(snapshot)  // step 3 (DFS + cycles + edges + payload)
  ├─ IRedisProjectionWriter.UpsertAsync(snapshot)// step 4 (L2 write)
  └─ snapshot.Dispose()                          // step 5 (L1 cleanup contract)
```

| Seam | Lives in | Lifetime | Why this boundary |
|---|---|---|---|
| `IWorkflowGraphLoader` | `Features/Orchestration/Loading/` (NEW) | Scoped | Reusable for non-Workflow graph types in future; testable without Redis |
| `IWorkflowGraphValidator` (composite) | `Features/Orchestration/Validation/` | Singleton (stateless) | Pure function over a snapshot; no DB/Redis dependencies |
| `IRedisProjectionWriter` | `Features/Orchestration/Projection/` (NEW) | Scoped | Owns the 3 keyspaces; injects `IDatabase` + `RedisProjectionOptions` |

**Should L3-fetch reuse `Repository<TEntity>`?** **No** — use direct `BaseDbContext` access matching the existing `OrchestrationService.cs:115-119` pattern. The 5-method generic Repository surface doesn't include batch-load-many-with-projection, and Phase 3 explicitly rejected exposing `IQueryable`.

**Refinement** (v3.3.0 decision): keep seams as `internal` types in the Orchestration feature folder. Promote to `BaseApi.Core` only when a second consumer surfaces (matches Phase 7 D-13 precedent).

Confidence: **HIGH** for seam topology; **MEDIUM** for naming.

---

## 3. Health probe integration

### Recommendation: **`/health/ready` MUST include the Redis check; `/health/live` MUST NOT; tags exactly mirror the existing Postgres check.**

`HealthServiceCollectionExtensions.cs:20-30` shows the current shape:

```csharp
services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup", "ready" })
    .AddNpgSql(cfg.RequireConnectionString("Postgres"), tags: new[] { "ready" });
```

Phase 5 Pitfall 15: *"live never touches DB"*. Redis is a downstream dependency exactly like Postgres.

**Add a 4th check** via `AspNetCore.HealthChecks.Redis` (Xabaril 9.0.0):

```csharp
.AddRedis(cfg.RequireConnectionString("Redis"), tags: new[] { "ready" });
```

**What happens if Redis is down mid-Start:**
- `/health/live` returns 200 (HEALTH-01 contract preserved).
- `/health/ready` returns 503 within the next poll interval.
- An in-flight Start fails when `IRedisProjectionWriter.UpsertAsync` throws `RedisConnectionException`. Per Phase 4 fallback handler chain → 500 with RFC 7807 + `X-Correlation-Id`. L1 cleanup (snapshot.Dispose in `finally`) still runs.

**HEALTH-01..05 preserved:** live=process only ✓ · ready=DB+Redis ✓ · startup=migration-gated (unchanged — Redis has no schema) ✓ · tag discipline strict ✓ · UI response writer aggregates over all checks ✓.

**Test obligation:** Extend `HealthEndpointsTests` with a Redis-dead variant analogous to `HealthDeadPostgresFixture`. Use a dead Redis port (e.g., `localhost:6380`).

Note: STACK.md (sibling research) suggests an alternative: **don't** add a Redis ready check, so CRUD continues to work when Redis is down. This is a genuine tradeoff. ARCHITECTURE.md leans toward "hard dependency, mirror Postgres" for consistency; the requirements phase should pick one.

Confidence: **HIGH** on tag-discipline rules; **MEDIUM** on hard-vs-soft dependency choice (genuine tradeoff to resolve in requirements).

---

## 4. Composition root + appsettings.json shape

### Recommendation: **Single config section `Redis:*`; one new connection string `Redis`; minimal additions.**

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=postgres;Port=5432;...",
    "Redis":    "redis:6379,abortConnect=false,connectTimeout=5000"
  },
  "Redis": {
    "KeyPrefix": "skp:",
    "Serialization": {
      "JsonOptions": "Default"
    }
  }
}
```

**Why these choices:**
- **`abortConnect=false`** — without it, a transient network blip during boot permanently kills the multiplexer. Production-must-have.
- **`KeyPrefix`** — enables (a) prod/staging isolation when sharing a Redis cluster and (b) test-class isolation in the test suite.
- **No `Redis:Endpoint` separate from `ConnectionStrings:Redis`** — `ConnectionStrings:Redis` is the canonical .NET location.

**`compose.yaml` addition** (pin `redis:7-alpine` per Phase 11 D-09 discipline — but see STACK.md for licensing-driven `redis:7.4.x-alpine` refinement):

```yaml
redis:
  image: redis:7-alpine
  container_name: sk-redis
  restart: unless-stopped
  ports:
    - "6379:6379"
  healthcheck:
    test: ["CMD", "redis-cli", "ping"]
    interval: 5s
    timeout: 3s
    retries: 10
    start_period: 5s

baseapi-service:
  environment:
    ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"
  depends_on:
    redis:
      condition: service_healthy
```

Confidence: **HIGH**.

---

## 5. Test infrastructure: per-class Redis isolation

### Recommendation: **Single shared Redis container + per-class key-prefix isolation with prefix-scoped DEL in `DisposeAsync`.**

Per-class testcontainers were rejected for Postgres at v1 for cost reasons (boot dominates wall-clock). The same applies to Redis: ~1-2s boot × N classes → ~30-60s added per run; violates the 3-consecutive-GREEN cadence efficiency demonstrated at v3.2.0 close (163s/161s/162s for 142 facts).

Redis logical DB indices (`SELECT 0..15`) are deprecated in Cluster mode — caps parallelism, diverges from prod posture.

**Implementation:**

```csharp
// New: tests/BaseApi.Tests/Composition/RedisFixture.cs  (parallels PostgresFixture)
public class RedisFixture : IAsyncLifetime
{
    public string ConnectionString { get; } = "localhost:6379,abortConnect=false";
    public string KeyPrefix { get; } = $"test:cls-{Guid.NewGuid():N}:";
    public async ValueTask DisposeAsync() => await DeleteByPrefixAsync();
}
```

The `RedisFixture` is encapsulated inside `Phase8WebAppFactory` (same Plan 05-02 Pattern C reason: *"xUnit cannot order fixture instantiation between two IClassFixture<> slots"*).

**Test infrastructure pitfall to avoid:** never use `FLUSHDB` without prefix scoping — it nukes ALL keys including parallel-running classes. Always `SCAN MATCH "{prefix}*"` + `DEL`.

Note: STACK.md recommends `Testcontainers.Redis 4.12.0` for full Ryuk-managed lifecycle. Either approach (host-side Redis with prefix scoping, OR Testcontainers per-suite) works — the requirements phase should pick.

Confidence: **HIGH** for option choice; **MEDIUM** for exact tactic.

---

## 6. Redis analogue of Phase 3 D-15 byte-identical `psql \l` no-leak invariant

### Recommendation: **Per-prefix orphan-key assertion in `DisposeAsync` + cross-session global `redis-cli --scan | sort | sha256sum` snapshot.**

| Phase 3 invariant | Redis analogue |
|---|---|
| `psql \l` enumerates all DBs | `KEYS *` / `DBSIZE` enumerates all keys |
| SHA-256 BEFORE = AFTER 142 facts | SHA-256 of `KEYS * \| sort` BEFORE = AFTER full run |
| 4 baseline DBs | 0 baseline keys (Redis container starts empty) |
| No `stepsdb_test_*` leak | No `test:cls-*` leak |

**Implementation:**

1. **Per-class teardown** — `SCAN MATCH "{KeyPrefix}*"` + `KeyDeleteAsync(keys)`.
2. **Per-class assertion** — re-`SCAN` and assert count == 0; throw if violated (fail-loud).
3. **Cross-session proof** — append `redis-cli --scan | sort | sha256sum` to phase-close ritual alongside `psql \l` SHA-256.

**Why not `FLUSHDB`:** destroys keys from parallel classes; hides genuine leaks; zero diagnostic value.

Confidence: **HIGH**.

---

## 7. When does OrchestrationService warrant splitting?

### Recommendation: **Split NOW (at the start of v3.3.0 — Phase 13, before any L1/L2 work lands).**

### Current state (v3.2.0)
`OrchestrationService.cs:37-129` is **92 lines**, 1 public method, 7 ctor params (5 mappers deliberately unused per CONTEXT D-05).

### Projected state without split
v3.3.0 adds: L3 batch-fetch + DFS + 3 validation gates + L1 dictionary build + L2 write (3 keyspaces) + cleanup. Naive growth → ~600-800 LOC with 6 public methods and ~12 ctor dependencies.

### Inflection point already crossed
1. **Pre-injected dependency count > 7** — Future phases adding Redis client + 3 validators + 1 graph builder → 12+.
2. **Multiple unrelated responsibilities** — L3-fetch / cycle-detect / Redis-write are three different change axes.
3. **Test surface area** — Monolithic StartAsync tests must mock 5 entity DBs + Redis + 3 validators simultaneously.

### Recommended layout

```
src/BaseApi.Service/Features/Orchestration/
├── OrchestrationController.cs                    (UNCHANGED)
├── OrchestrationService.cs                       (SHRINK — orchestrator only; ~80 LOC)
├── OrchestrationServiceCollectionExtensions.cs   (EXTEND — registers 4 services + 3 validators)
├── WorkflowIdsValidator.cs                       (UNCHANGED)
├── Loading/
│   └── WorkflowGraphLoader.cs                    (NEW)
├── Validation/
│   ├── CycleDetector.cs                          (NEW)
│   ├── SchemaEdgeValidator.cs                    (NEW)
│   └── PayloadConfigSchemaValidator.cs           (NEW)
└── Projection/
    ├── RedisProjectionWriter.cs                  (NEW — owns 3 keyspaces)
    └── RedisProjectionKeys.cs                    (NEW — key-format constants)
```

### Why split at Phase 13 (early), not later
- Splitting a 92-LOC service is a 1-hour refactor with zero behavior change.
- Splitting a 600-LOC service after L1+L2 lands is a multi-day refactor that risks regression.
- Phase 10 already validated the "bisect-friendly 5-commit sequence" for canonical revisions.

Confidence: **HIGH**.

---

## 8. Integration points

### Files TOUCHED (modified)

| File | Change | Reason |
|---|---|---|
| `src/BaseApi.Service/Program.cs` | None | `AddBaseApiRedis` chained internally |
| `src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs` | +1 line | Composition root |
| `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` | +1 line | HEALTH-01..05 extension |
| `src/BaseApi.Service/appsettings.json` | +1 conn string, +1 section | Configuration |
| `src/BaseApi.Service/appsettings.Development.json` | Same shape | Dev overrides |
| `compose.yaml` | +1 service, +1 depends_on | Local dev stack |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs` | Possibly extend [ProducesResponseType] for 422 + 500 | New error paths |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` | Body rewrite — orchestrator role | Pre-injected mappers NOW USED |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` | +4 service registrations | DI for split services |
| `src/BaseApi.Service/AppDbContext.cs` | None | Redis has no DbSet |
| `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` | +RedisFixture wiring | Per-class isolation |

### Files ADDED (new)

14 new files (4 in `BaseApi.Core/DependencyInjection` + Configuration; 10 in `Features/Orchestration/` and `tests/Composition`). See the §7 layout for the Orchestration feature folder; in `BaseApi.Core`:
- `RedisServiceCollectionExtensions.cs`
- `Configuration/RedisProjectionOptions.cs`

Plus L2 DTOs (`WorkflowL2Dto`, `StepL2Dto`, `ProcessorL2Dto` with `inputDefinition`/`outputDefinition` field names per PROJECT.md locked constraint) and `RedisFixture.cs`.

### Data flow changes

```
v3.2.0 (read path):
    Client → Controller → BaseService.GetByIdAsync → Repository → Postgres

v3.3.0 (Start path):
    Client → OrchestrationController.Start
        → OrchestrationService.StartAsync
            → ValidateWorkflowIdsAsync (existence) ──→ Postgres
            → WorkflowGraphLoader.LoadL1Async   ──→ Postgres (5 batch SELECTs)
            → CycleDetector.Validate           ──→ in-memory only
            → SchemaEdgeValidator.Validate     ──→ in-memory only
            → PayloadConfigSchemaValidator     ──→ in-memory only
            → RedisProjectionWriter.UpsertAsync──→ Redis (3 keyspaces)
            → snapshot.Dispose()               ──→ L1 cleanup contract
        ← 204 No Content

v3.3.0 (read path — UNCHANGED):
    Client → Controller → BaseService.GetByIdAsync → Repository → Postgres
    (Redis is WRITE-ONLY from BaseApi.Service in v3.3.0; reads are by external Orchestrator)
```

**Critical: v3.3.0 only WRITES to Redis.** No IDistributedCache integration; no cache-invalidation on PUT/DELETE.

---

## 9. Suggested build order — phase decomposition

### Recommendation: **5 phases (Phase 12–16), strictly serialized by dependency graph.**

| Phase | Title | What lands |
|---|---|---|
| **12** | **Redis infra + composition + healthcheck + DI registration** | `compose.yaml` redis service · `RedisServiceCollectionExtensions.AddBaseApiRedis` · `AspNetCore.HealthChecks.Redis` wired · `appsettings.{json,Development.json}` entries · `RedisFixture` test infra · `RedisProjectionOptions` · Redis-dead health test |
| **13** | **OrchestrationService split + L3 fetch + L1 build (no validation yet, no Redis write)** | `WorkflowGraphSnapshot` · `WorkflowGraphLoader.LoadL1Async` · `OrchestrationService.cs` body shrunk · DI wiring · L1-snapshot-shape tests |
| **14** | **Validation gates — DFS cycle detect + schema-edge + payload-config-schema** | `CycleDetector` · `SchemaEdgeValidator` · `PayloadConfigSchemaValidator` · DI wiring · `StartAsync` chains 3 validators · 422 error path · ~30 unit tests for cycle detection · ~10 integration tests for schema-edge + payload-schema. Closes deferred v2 REQ VALID-21. |
| **15** | **L2 (Redis) projection write + Stop endpoint divergence** | `RedisProjectionKeys` · 3 L2 DTOs (`WorkflowL2Dto`, `StepL2Dto`, `ProcessorL2Dto` with `inputDefinition`/`outputDefinition`) · `RedisProjectionWriter.UpsertAsync` (MULTI/EXEC pipeline) · `DeleteAsync` (scan + DEL) · `OrchestrationService.StartAsync` final step · NEW `OrchestrationService.StopAsync` (split per CONTEXT D-09) · 3-keyspace assertion tests |
| **16** | **Idempotency + concurrency + L1 cleanup contract + 3-GREEN closeout** | Start-twice-same-ids regression test · Concurrent Start regression test · Stop-without-prior-Start regression test · IDisposable Dispose / finally-block L1 cleanup verification · End-to-end happy path with real Postgres + Redis · 3-consecutive-GREEN gate · psql \l SHA-256 + redis-cli --scan SHA-256 snapshots |

### Phase-close gate (mandatory)

Each phase 12-16 closes with:
- 3-consecutive-GREEN integration test runs (Phase 3 D-18)
- Byte-identical `psql \l` SHA-256 BEFORE = AFTER (Phase 3 D-15)
- **NEW** Byte-identical `redis-cli --scan | sort | sha256sum` BEFORE = AFTER (v3.3.0 analogue)
- Bisect-friendly N-commit sequence per Phase 10 precedent

Confidence: **HIGH** for ordering; **MEDIUM** for exact LOC estimates.

---

## Summary of recommendations

1. **DI** — `AddBaseApiRedis(IServiceCollection)` chained into `AddBaseApi<TDbContext>` as call #7.
2. **L1 build** — extract `IWorkflowGraphLoader` + `IWorkflowGraphValidator` + `IRedisProjectionWriter` seams in `Features/Orchestration/`; do NOT reuse `Repository<TEntity>`.
3. **Health** — Redis joins `ready` tag only (tradeoff with STACK.md flagged — soft-vs-hard dependency).
4. **Composition** — `ConnectionStrings:Redis` + `Redis:KeyPrefix` config section; `compose.yaml` adds redis:7-alpine.
5. **Tests** — single shared Redis container + per-class GUID-prefix isolation + SCAN/DEL teardown.
6. **No-leak invariant** — per-class prefix-scoped DEL+assert + cross-session `redis-cli --scan` SHA-256 BEFORE=AFTER snapshot.
7. **OrchestrationService split** — split NOW (Phase 13 start); 4 internal feature-folder seams.
8. **Integration points** — 11 files modified, 14 files new; Redis is WRITE-ONLY in v3.3.0.
9. **Build order** — 5 phases: 12 (infra) → 13 (split+L1) → 14 (validation) → 15 (L2 write+Stop) → 16 (idempotency+close).

---

## Open questions for requirements / phase planning

1. **L1 snapshot disposal contract** — `IDisposable` (sync) vs `IAsyncDisposable` (async)? Recommend `IDisposable` + `using`; cleanup is pure sync work.
2. **Redis MULTI/EXEC vs pipeline batch** for 3-keyspace UpsertAsync — both work; MULTI/EXEC adds atomic guarantee. Phase 15 picks based on workflow-size projections.
3. **Stop deletion key-scan strategy** — `SCAN MATCH` vs maintaining a key index. Recommend SCAN until benchmarked. (FEATURES.md proposes: compute key set at L1 build time + UNLINK — non-blocking DEL — that's the better mental model.)
4. **Health-check hard vs soft Redis dependency** — ARCHITECTURE says hard (mirror Postgres), STACK says soft (CRUD survives Redis outage). Genuine tradeoff for requirements.
5. **Testcontainers.Redis vs host-side Redis with prefix scoping** — both work; pick based on CI environment.
