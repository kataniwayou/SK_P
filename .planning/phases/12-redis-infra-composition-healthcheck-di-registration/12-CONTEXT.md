# Phase 12: Redis infra + composition + healthcheck + DI registration - Context

**Gathered:** 2026-05-29
**Status:** Ready for planning

<domain>
## Phase Boundary

Land Redis as a healthy compose-stack tier wired into the API's DI graph with test fixtures and dead-Redis resilience proven, while the Phase 5 HEALTH-01..05 contracts and Phase 11 D-03 no-traces-backend posture remain byte-for-byte untouched.

**In scope (Phase 12 only):**
- compose.yaml redis service (image pin, healthcheck, command flags, persistence posture, container_name, depends_on wiring on baseapi-service)
- StackExchange.Redis 2.13.1 CPM pin in Directory.Packages.props + BaseApi.Service consumption
- `ConnectionStrings:Redis` + `Redis:KeyPrefix` in appsettings.json + appsettings.Development.json (host-vs-Docker split per Phase 2 D-07)
- `AddBaseApiRedis` extension landing in BaseApi.Core/DependencyInjection/ (chained as call #7 inside `AddBaseApi<TDbContext>`)
- `RedisProjectionOptions` POCO + `services.Configure<>` binding
- `RedisFixture` in tests/BaseApi.Tests/Composition/ + Phase8WebAppFactory extension to compose it alongside PostgresFixture
- `HealthDeadRedisFixture` proving both `/health/live` AND `/health/ready` stay 200 when Redis is down
- Phase-close gate extended with `redis-cli --scan | sort | sha256sum` BEFORE=AFTER

**Out of scope (deferred to later phases):**
- OrchestrationService body changes (Phase 13)
- L1 snapshot loader / validators (Phases 13–14)
- L2 RedisProjectionWriter implementation + RedisProjectionKeys (Phase 15)
- Start/Stop endpoint semantics (Phases 15–16)
- OTel Redis instrumentation (deferred milestone; OBSERV-REDIS-01)

</domain>

<decisions>
## Implementation Decisions

### Compose Posture

- **D-01:** Redis compose host port mapping = `6380:6379`. Mirrors Phase 2 D-01 Postgres `5433:5432` collision-avoidance pattern; defends against a developer's local Redis on the default 6379. Auto-determines the `HealthDeadRedisFixture` dead-port choice (D-13).
- **D-02:** `container_name: sk-redis` on the redis service. Mirrors Phase 11 D-10/D-13 posture (`sk-elasticsearch`, `sk-prometheus`) — makes sk_p mutually exclusive with sk2_1 on a single Docker daemon. Add an inline comment block citing the Phase 11 precedent.
- **D-03:** Persistence explicitly disabled at the compose layer: `command: ["redis-server", "--save", "", "--appendonly", "no"]`, no `volumes:` entry, no named volume. Encodes "L2 is rebuildable from L3 on demand" inline; defends against a future operator re-enabling RDB/AOF as a "fix". Add a comment block documenting intent so the absence is intentional rather than a forgotten gap.
- **D-04:** Connection string split across env files mirrors Plan 02-01 D-02 / Plan 02-01 D-07:
  - `appsettings.json` (Production, Docker-internal): `"Redis": "redis:6379,abortConnect=false,connectTimeout=5000"`
  - `appsettings.Development.json` (host-side dev + tests): `"Redis": "localhost:6380,abortConnect=false,connectTimeout=5000"`
  - `compose.yaml` baseapi-service env block sets `ConnectionStrings__Redis` defensively (matches the existing `ConnectionStrings__Postgres` pattern).

### Startup + Health Discipline

- **D-05:** `StartupCompletionService` does NOT probe Redis before `MarkReady()`. PERSIST-10 contract (MigrateAsync → MarkReady, broad-catch / LogCritical / no-rethrow / no-MarkReady-on-failure) preserved verbatim. Soft-dep INFRA-REDIS-06 holds: Redis down at boot ⇒ Start/Stop return 500 + RFC 7807, CRUD serves 200, `/health/ready` returns 200, zero startup-gate behavior change. Overrides PITFALLS.md P31 recommendation because it directly conflicts with the locked soft-dep contract.
- **D-06:** No dedicated `/health/redis` endpoint and no Redis check tag added to existing `/health/live`, `/health/ready`, or `/health/startup` probes. Phase 5 HEALTH-01..05 contracts stay byte-for-byte identical. `TEST-REDIS-05 HealthDeadRedisFixture` verifies both `/live` AND `/ready` return 200 when Redis is down (the v3.3.0 net-new acceptance fact).
- **D-07:** `Phase8WebAppFactory` extended in place rather than renamed or subclassed. Accretes `RedisFixture` composition alongside `PostgresFixture` (encapsulated internally per Phase 8 Plan 05-02 Pattern C reasoning). Avoids cross-test churn of the ~30 facts that bind `IClassFixture<Phase8WebAppFactory>` today; the class name remains a historical anchor but its v3.3.0 capabilities accrete.
- **D-08:** Connection-string + KeyPrefix injection into the test factory uses `AddInMemoryCollection` (mirror Plan 05-02 Pattern C verbatim). Add `["ConnectionStrings:Redis"]` and `["Redis:KeyPrefix"]` to the same dictionary that already carries `["ConnectionStrings:Postgres"]`. The `Environment.SetEnvironmentVariable` workaround from Plan 05-02 (Postgres) does NOT apply here because `AddBaseApiRedis` reads via `IConfiguration` at DI registration time, not by value capture.

### RedisFixture Architecture

- **D-09:** `IConnectionMultiplexer` lifetime = per-`RedisFixture`-instance. Each fixture constructs its own multiplexer in `InitializeAsync` and disposes it in `DisposeAsync`. Mirrors `PostgresFixture`'s per-class NpgsqlConnection-pool pattern. ~50 multiplexers across the suite, well within Redis `maxclients` default budget (10000). KeyPrefix isolation happens at `IDatabase` operation time.
- **D-10:** `RedisFixture.DisposeAsync` cleanup contract = SCAN MATCH `{KeyPrefix}*` → `KeyDeleteAsync(keys)` → re-SCAN with same prefix → assert count == 0 → throw on violation. Verbatim TEST-REDIS-03 fail-loud discipline. Cursor-based SCAN preserves the L2-PROJECT-07 forbidden-`IServer.Keys()`/`KEYS` invariant from day one. `KeyDeleteAsync` (not `KeyDeleteAsync` via UNLINK) chosen because StackExchange.Redis surfaces it as the canonical API and the key-set per class is small.
- **D-11:** `RedisFixture` host:port = `localhost:6380` hardcoded. Mirrors `PostgresFixture`'s hardcoded `localhost:5433`. No env-var override surface — if compose isn't running, the first `PING` fails-loud and tests stop (correct behavior). No CI matrix driver currently anticipates a non-default port.
- **D-12:** Per-class `KeyPrefix` template = `"test:cls-{Guid:N}:"` (locked by TEST-REDIS-01). Each `RedisFixture` instance generates a new Guid at ctor time. Production prefix (`"skp:"` from INFRA-REDIS-05) and the test prefix family `"test:cls-*"` share no overlap, so a residual test-key leak surfaces immediately in the `redis-cli --scan` phase-close gate (TEST-REDIS-04).
- **D-13:** `HealthDeadRedisFixture` dead-port = `6379`. Locked by D-01 — since compose maps `6380:6379`, the host's `6379` is guaranteed unbound; connection attempts fail-fast with ECONNREFUSED, surfaced by StackExchange.Redis as `RedisConnectionException`. Zero additional reservation cost.

### DI Registration + Options

- **D-14:** `AddBaseApiRedis(IServiceCollection, IConfiguration)` registers `IConnectionMultiplexer` via `services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(cfg.GetConnectionString("Redis")))`. Canonical StackExchange.Redis maintainer pattern. Synchronous `Connect()` is safe because the locked connection string carries `abortConnect=false,connectTimeout=5000` (INFRA-REDIS-04) — boot never crashes on a dead Redis (mitigates PITFALLS P2). Lifetime = Singleton (mitigates PITFALLS P1). `IDatabase` resolved per-call via `_multiplexer.GetDatabase()` rather than registered (INFRA-COMP-03).
- **D-15:** `RedisProjectionOptions` surface stays at the REQ INFRA-COMP-04 minimum: `KeyPrefix` (string, default `"skp:"`) + `Serialization` nested object with `JsonOptions` (string discriminator, e.g., `"default"`). No `Database` int, no `CommandFlags`, no `ConnectionString` property. YAGNI — Database / Cluster / replica knobs can be added in v3.4 when a real scale driver appears. Phase 15 writer reads exactly the fields it needs.
- **D-16:** Connection-string source-of-truth = `IConfiguration.GetConnectionString("Redis")` consumed inside the `AddBaseApiRedis(IServiceCollection, IConfiguration)` factory closure. Mirrors the Phase 3 `AddBaseApiPersistence` / `AddNpgsql` reading pattern. Single source of truth = `appsettings.json` / `appsettings.Development.json` `ConnectionStrings:Redis`. NOT bound through `RedisProjectionOptions.ConnectionString` (avoids the indirection that Phase 15 writer doesn't need).

### Cold-Start Behavior

- **D-17:** `ConnectionMultiplexer.Connect()` fires lazily at first `IConnectionMultiplexer` resolution — i.e., at the first `POST /api/v1/orchestration/start` request after process boot. No pre-warm `IHostedService`. Cold-start path stays minimal; first-request latency cost is sub-second on localhost; with `abortConnect=false`, even a dead Redis lets the Singleton materialize so subsequent failures surface as `RedisConnectionException` at `SetAsync`/`KeyExistsAsync` time → 500 + RFC 7807 (ORCH-START-04 / ORCH-STOP-07 paths).

### Claude's Discretion

The following are not user-locked; planner/researcher may choose the simplest fit:
- Exact text of compose.yaml inline comments documenting D-02 / D-03 intent (planner copies the Phase 11 D-09/D-10 style)
- Whether `RedisServiceCollectionExtensions.cs` introduces a `private const string SectionName = "Redis"` helper or inlines the string literal twice (configuration binding + GetConnectionString)
- xUnit fixture trait names (`[Trait("Phase12Wave", "B")]` etc.) and the test class names for the smoke facts that prove D-05 / D-13 / D-14 behaviors
- Whether the bisect-friendly N-commit sequence for Phase 12 mirrors the Phase 11 10-commit fan-out or the Phase 10 5-commit cadence — planner picks based on plan decomposition; both are acceptable per the established discipline

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase 12 requirements + roadmap
- `.planning/ROADMAP.md` (Phase 12 entry + Success Criteria 1–5 + v3.2.0 invariants list)
- `.planning/REQUIREMENTS.md` §INFRA-REDIS-01..06, §INFRA-COMP-01..04, §TEST-REDIS-01..05, §OBSERV-REDIS-01 — the 16 locked requirements covered by this phase
- `.planning/PROJECT.md` §"Current Milestone" + §"Out of Scope" — locked constraints
- `.planning/MILESTONES.md` (v3.3.0 milestone framing)

### Research outputs (pre-roadmap)
- `.planning/research/SUMMARY.md` — top 10 pitfalls + 4 cross-file contradictions (3 already resolved by REQUIREMENTS.md locks: soft dep, host-side prefix isolation, no OTel Redis)
- `.planning/research/STACK.md` — StackExchange.Redis 2.13.1 + Redis 7.4.x-alpine licensing + connection-string discipline
- `.planning/research/ARCHITECTURE.md` §3 (health-check posture) + §5 (test isolation) + §7 (folder layout)
- `.planning/research/PITFALLS.md` P1–P4 (DI + healthcheck), P31 (StartupCompletionService — overridden by D-05), P32 (host port)
- `.planning/research/FEATURES.md` — P1 table stakes + P2 deferred differentiators

### Existing v3.2.0 source files (load-bearing for Phase 12)
- `compose.yaml` — Phase 2 D-01 / D-07 patterns (Postgres 5433:5432, conn-string split), Phase 11 D-09/D-10/D-13 (sk-elasticsearch + sk-prometheus container_name + healthcheck templates)
- `src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs` — `AddBaseApi<TDbContext>` chain (Redis = call #7, inserted between current call #6 Mapping and Phase 8 AddAppFeatures)
- `src/BaseApi.Core/DependencyInjection/PersistenceServiceCollectionExtensions.cs` — `AddNpgsql` registration pattern (template for `AddBaseApiRedis`)
- `src/BaseApi.Core/Health/StartupCompletionService.cs` — PERSIST-10 contract (NOT modified by Phase 12; D-05)
- `src/BaseApi.Core/Health/StartupHealthCheck.cs` + `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` — Phase 5 HEALTH-01..05 tag discipline (NOT modified by Phase 12; D-06)
- `src/BaseApi.Service/Program.cs` — composition root call site (no change beyond what AddBaseApiRedis-as-call-#7 transparently adds)
- `src/BaseApi.Service/appsettings.json` + `appsettings.Development.json` — env-file split target (D-04)
- `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` — extension target (D-07, D-08)
- `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` — `RedisFixture` mirrors this per-class pattern (D-09, D-10, D-11)

### Phase-attribution decisions referenced in this CONTEXT
- Phase 2 D-01: host-port collision-avoidance (Postgres 5433:5432) — Plan 02-01 / 02-02 STATE notes
- Phase 2 D-02 / D-07: connection string split base = Docker-internal, Development = host
- Phase 3 D-15: byte-identical `psql \l` BEFORE=AFTER discipline (analogue: `redis-cli --scan | sort | sha256sum` BEFORE=AFTER per TEST-REDIS-04)
- Phase 5 HEALTH-01: `/health/live` MUST NOT touch external state
- Phase 5 Plan 05-02 Pattern C: env-var-in-ctor / AddInMemoryCollection for fixture connection-string overrides
- Phase 5 Plan 05-02 Pattern E: `[assembly: AssemblyFixture]` — rejected for Redis multiplexer per D-09
- Phase 7 D-13: `AddBaseApi<TDbContext>` IServiceCollection chain vs IHostApplicationBuilder split for Observability — D-14 explicitly DOES NOT mirror the split (Redis has no ILoggingBuilder need)
- Phase 8 PERSIST-10: StartupCompletionService LogCritical / no-rethrow / no-MarkReady-on-failure contract — D-05 preserves
- Phase 11 D-03: no traces backend, `.WithTracing()` stripped, `OpenTelemetry.Instrumentation.StackExchangeRedis` is FORBIDDEN — OBSERV-REDIS-01 locks
- Phase 11 D-09 / D-10 / D-13: `sk-*` container_name posture for mutual exclusion with sk2_1 — D-02 mirrors

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets

- **`compose.yaml`** — extend with a 5th service. Patterns to reuse:
  - Phase 2 healthcheck shape (CMD-SHELL + interval/timeout/retries/start_period)
  - Phase 11 `container_name: sk-*` + restart: unless-stopped + inline comment block explaining licensing/version choice
  - `depends_on: condition: service_healthy` cascade (Phase 2 D-08; baseapi-service already depends on postgres+otel-collector+elasticsearch+prometheus, so adding redis is purely additive)
- **`src/BaseApi.Core/DependencyInjection/PersistenceServiceCollectionExtensions.cs`** — template for `RedisServiceCollectionExtensions.cs` (same `(IServiceCollection, IConfiguration)` signature + connection-string read + Singleton registration shape).
- **`src/BaseApi.Core/Health/StartupCompletionService.cs`** — explicitly NOT extended for Redis (D-05). Reuse is "leave alone."
- **`tests/BaseApi.Tests/Persistence/PostgresFixture.cs`** — template for `RedisFixture.cs` (same `IAsyncLifetime` + InitializeAsync/DisposeAsync shape; same per-class isolation discipline; same hardcoded `localhost:{port}`).
- **`tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs`** — extend in place per D-07 to add `RedisFixture? _redisFixture` field + InitializeAsync chained boot + DisposeAsync chained teardown + ConfigureWebHost `AddInMemoryCollection` extension per D-08. Constructor surface (lines 32–75) already supports the dead-Postgres test pattern that `HealthDeadRedisFixture` can mirror.
- **`Directory.Packages.props`** — add `<PackageVersion Include="StackExchange.Redis" Version="2.13.1" />` per CPM discipline (Plan 01-01 D-05).
- **`src/BaseApi.Service/BaseApi.Service.csproj`** — add `<PackageReference Include="StackExchange.Redis" />` (zero `Version=` per CPM enforcement).

### Established Patterns

- **Connection-string split** (Plan 02-01 D-02 / D-07): base appsettings carries Docker-internal hostname; appsettings.Development.json carries host-side; tests AddInMemoryCollection override.
- **Per-class fixture with hardcoded host:port** (Phase 3 / 5 / 7 / 8): no env-var coupling; fail-loud at first connect attempt.
- **Composition-root chained call sequence** (Phase 7 D-13): `AddBaseApi<TDbContext>` chains 6 `IServiceCollection` extensions in a locked order. Phase 12 inserts Redis as call #7 — between Mapping (call #6, currently last) and any future feature aggregator. Composition root grows by exactly one line.
- **`internal static class XxxServiceCollectionExtensions`** (Phase 8 Plan 08-02): single `AddXxxFeature()` registering concrete + abstract base. `RedisServiceCollectionExtensions` follows this but in `BaseApi.Core` (public static, like Validation/Mapping) because it's infrastructure, not a feature.
- **PERSIST-10 contract** (Phase 8): StartupCompletionService is the single hosted-service that swaps in startup work. D-05 preserves it.
- **Phase-close 3-GREEN cadence + byte-identical state snapshot** (Phase 3 D-18 / D-15): TEST-REDIS-04 extends this with `redis-cli --scan` analogue.

### Integration Points

- **`compose.yaml`** — new top-level `services.redis:` block + addition to `services.baseapi-service.depends_on`.
- **`src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs`** — single new chained call: `.AddBaseApiRedis(cfg)` after `.AddBaseApiMapping(...)`.
- **`src/BaseApi.Service/appsettings.json`** + **`appsettings.Development.json`** — add `ConnectionStrings:Redis` (+ optional `Redis:KeyPrefix`).
- **`tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs`** — interior extension (private field + InitializeAsync hook + DisposeAsync hook + ConfigureWebHost dictionary extension).
- **`tests/BaseApi.Tests/Composition/`** — net-new `RedisFixture.cs` + new `HealthDeadRedisFixture.cs` (or its successor in the `Observability` test namespace per Phase 5/8 pattern).
- **Plan-close gate** — extend the existing `psql \l` SHA-256 capture step with a `redis-cli --scan | sort | sha256sum` capture per TEST-REDIS-04. Each plan that asserts state must record both.

### Plan-decomposition hint

Suggested Phase 12 commit-sequence cadence (Phase 11 fan-out pattern fits better than Phase 10's tight 5-commit cycle because Phase 12 spans 3 layers: compose, DI, tests):
1. `docs(12-01)`: REQUIREMENTS amendment (already locked — likely no-op if REQUIREMENTS.md is authoritative)
2. `chore(12-02)`: Directory.Packages.props + BaseApi.Service.csproj + appsettings split
3. `feat(infra)`: compose.yaml redis service + baseapi-service depends_on
4. `feat(core)`: RedisProjectionOptions + RedisServiceCollectionExtensions.AddBaseApiRedis + BaseApi.Service composition root call #7
5. `test(infra)`: RedisFixture + Phase8WebAppFactory extension
6. `test(health)`: HealthDeadRedisFixture + soft-dep acceptance facts
7. `test(close)`: phase-close gate extension (`redis-cli --scan` SHA-256 + 3-GREEN cadence harness)

Planner has final say on wave/commit decomposition.

</code_context>

<specifics>
## Specific Ideas

- The user repeatedly preferred mirror-existing-pattern choices over "introduce a new pattern" (Phase 2 D-01/D-07, Phase 8 Plan 05-02 Pattern C, Phase 11 sk-* container_name, PostgresFixture per-class) — Phase 12 should resist any urge to invent new fixture/composition idioms here.
- The user accepted the soft-dep contract as the final word even where PITFALLS recommended a startup ping (D-05 explicitly overrides PITFALLS P31). When PITFALLS guidance and locked REQUIREMENTS conflict, locked REQUIREMENTS win unless explicitly revisited.
- "Future-headroom" knobs (`Database` int, `CommandFlags`) were rejected under YAGNI (D-15). Phase 12 ships exactly what's needed; v3.4 adds what's needed when scale drivers appear.

</specifics>

<deferred>
## Deferred Ideas

- **Redis-specific observability surface** — `/health/redis` endpoint (rejected D-06), Prometheus gauge for `orchestration.redis.connected` (rejected D-06 third option). Future-headroom candidate per OBSERV-REDIS-04 if real ops need arises. Track alongside FUTURE-OTEL-REDIS reactivation.
- **Pre-warm `IHostedService` for Redis Connect()** — rejected D-17 in favor of lazy first-request connect. Re-open if cold-start first-Start latency becomes a problem in any environment where it matters.
- **`RedisProjectionOptions.Database` / `.CommandFlags`** — rejected D-15. Re-open when Redis Cluster or replica reads enter scope (no v3.3.0 driver).
- **Environment.SetEnvironmentVariable fixture override** — rejected D-08 (no need). Re-open only if `Program.cs` starts capturing the Redis connection string by value before `ConfigureAppConfiguration` runs.
- **Assembly-shared `IConnectionMultiplexer`** — rejected D-09 in favor of per-fixture lifetime. Re-open if the test suite's TCP-connection count becomes a real bottleneck (currently ~50, vs Redis `maxclients` 10000).
- **`Phase12WebAppFactory` subclass / rename to `IntegrationWebAppFactory`** — rejected D-07 in favor of in-place extension. Re-open at the next milestone boundary when phase-numbered naming has accumulated enough drag.

</deferred>

---

*Phase: 12-redis-infra-composition-healthcheck-di-registration*
*Context gathered: 2026-05-29*
