# Phase 12: Redis infra + composition + healthcheck + DI registration — Research

**Researched:** 2026-05-29
**Domain:** Redis as a compose-stack tier wired into a .NET 8 modular monolith's DI graph; soft dependency posture; per-class Redis test fixture mirroring `PostgresFixture`; dead-Redis acceptance discipline.
**Confidence:** HIGH on stack pins, registration shape, fixture topology; MEDIUM only on the exact 7.4.x patch (CONTEXT pins "7.4.x" — current latest patch is 7.4.9 per Docker Hub).

---

## Summary

Phase 12 is **infrastructure landing only** — no business logic, no L1 build, no L2 writer. Five things have to be true at phase close: a Redis 7.4.x-alpine service is healthy in compose alongside Postgres/ES/Prom/OTel-Collector; `AddBaseApiRedis` registers `IConnectionMultiplexer` as Singleton and binds `RedisProjectionOptions`; with Redis stopped both `/health/live` AND `/health/ready` still return 200 (locked soft-dep contract — D-05/D-06); `Phase8WebAppFactory` composes a per-class `RedisFixture` with `KeyPrefix = "test:cls-{Guid:N}:"` isolation and SCAN-asserts zero residual keys on dispose; the phase-close gate adds a `redis-cli --scan | sort | sha256sum` BEFORE=AFTER invariant alongside the v3.2.0 `psql \l` SHA-256 (`0d98b0de…0aac127`).

CONTEXT.md locks **17 decisions** (D-01..D-17) covering compose posture, startup/health discipline, fixture architecture, DI registration, and cold-start behavior. The research below is prescriptive against those locks — every option not consistent with D-01..D-17 is rejected. No alternatives explored where a decision is locked.

**Primary recommendation:** Mirror three existing patterns verbatim — `AddBaseApiPersistence` for `AddBaseApiRedis` registration shape, Phase 11 `sk-elasticsearch`/`sk-prometheus` for compose service block, `PostgresFixture` for `RedisFixture` IAsyncLifetime topology. Use `ConnectionMultiplexer.Connect()` synchronously inside the Singleton factory (safe because the connection string carries `abortConnect=false`). Do NOT add a Redis health check, do NOT extend `StartupCompletionService`, do NOT add `OpenTelemetry.Instrumentation.StackExchangeRedis`.

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**Compose Posture**
- **D-01:** Redis compose host port mapping = `6380:6379`. Mirrors Phase 2 D-01 Postgres `5433:5432` collision-avoidance pattern; defends against a developer's local Redis on the default 6379. Auto-determines the `HealthDeadRedisFixture` dead-port choice (D-13).
- **D-02:** `container_name: sk-redis` on the redis service. Mirrors Phase 11 D-10/D-13 posture (`sk-elasticsearch`, `sk-prometheus`) — makes sk_p mutually exclusive with sk2_1 on a single Docker daemon. Add an inline comment block citing the Phase 11 precedent.
- **D-03:** Persistence explicitly disabled at the compose layer: `command: ["redis-server", "--save", "", "--appendonly", "no"]`, no `volumes:` entry, no named volume. Encodes "L2 is rebuildable from L3 on demand" inline; defends against a future operator re-enabling RDB/AOF as a "fix". Add a comment block documenting intent so the absence is intentional rather than a forgotten gap.
- **D-04:** Connection string split across env files mirrors Plan 02-01 D-02 / Plan 02-01 D-07:
  - `appsettings.json` (Production, Docker-internal): `"Redis": "redis:6379,abortConnect=false,connectTimeout=5000"`
  - `appsettings.Development.json` (host-side dev + tests): `"Redis": "localhost:6380,abortConnect=false,connectTimeout=5000"`
  - `compose.yaml` baseapi-service env block sets `ConnectionStrings__Redis` defensively (matches the existing `ConnectionStrings__Postgres` pattern).

**Startup + Health Discipline**
- **D-05:** `StartupCompletionService` does NOT probe Redis before `MarkReady()`. PERSIST-10 contract (MigrateAsync → MarkReady, broad-catch / LogCritical / no-rethrow / no-MarkReady-on-failure) preserved verbatim. Soft-dep INFRA-REDIS-06 holds: Redis down at boot ⇒ Start/Stop return 500 + RFC 7807, CRUD serves 200, `/health/ready` returns 200, zero startup-gate behavior change. **Overrides PITFALLS.md P31** recommendation because it directly conflicts with the locked soft-dep contract.
- **D-06:** No dedicated `/health/redis` endpoint and no Redis check tag added to existing `/health/live`, `/health/ready`, or `/health/startup` probes. Phase 5 HEALTH-01..05 contracts stay byte-for-byte identical. `TEST-REDIS-05 HealthDeadRedisFixture` verifies both `/live` AND `/ready` return 200 when Redis is down (the v3.3.0 net-new acceptance fact).
- **D-07:** `Phase8WebAppFactory` extended in place rather than renamed or subclassed. Accretes `RedisFixture` composition alongside `PostgresFixture` (encapsulated internally per Phase 8 Plan 05-02 Pattern C reasoning). Avoids cross-test churn of the ~30 facts that bind `IClassFixture<Phase8WebAppFactory>` today; the class name remains a historical anchor but its v3.3.0 capabilities accrete.
- **D-08:** Connection-string + KeyPrefix injection into the test factory uses `AddInMemoryCollection` (mirror Plan 05-02 Pattern C verbatim). Add `["ConnectionStrings:Redis"]` and `["Redis:KeyPrefix"]` to the same dictionary that already carries `["ConnectionStrings:Postgres"]`. The `Environment.SetEnvironmentVariable` workaround from Plan 05-02 (Postgres) does NOT apply here because `AddBaseApiRedis` reads via `IConfiguration` at DI registration time, not by value capture.

**RedisFixture Architecture**
- **D-09:** `IConnectionMultiplexer` lifetime = per-`RedisFixture`-instance. Each fixture constructs its own multiplexer in `InitializeAsync` and disposes it in `DisposeAsync`. Mirrors `PostgresFixture`'s per-class NpgsqlConnection-pool pattern. ~50 multiplexers across the suite, well within Redis `maxclients` default budget (10000). KeyPrefix isolation happens at `IDatabase` operation time.
- **D-10:** `RedisFixture.DisposeAsync` cleanup contract = SCAN MATCH `{KeyPrefix}*` → `KeyDeleteAsync(keys)` → re-SCAN with same prefix → assert count == 0 → throw on violation. Verbatim TEST-REDIS-03 fail-loud discipline. Cursor-based SCAN preserves the L2-PROJECT-07 forbidden-`IServer.Keys()`/`KEYS` invariant from day one. `KeyDeleteAsync` (not `KeyDeleteAsync` via UNLINK) chosen because StackExchange.Redis surfaces it as the canonical API and the key-set per class is small.
- **D-11:** `RedisFixture` host:port = `localhost:6380` hardcoded. Mirrors `PostgresFixture`'s hardcoded `localhost:5433`. No env-var override surface — if compose isn't running, the first `PING` fails-loud and tests stop (correct behavior). No CI matrix driver currently anticipates a non-default port.
- **D-12:** Per-class `KeyPrefix` template = `"test:cls-{Guid:N}:"` (locked by TEST-REDIS-01). Each `RedisFixture` instance generates a new Guid at ctor time. Production prefix (`"skp:"` from INFRA-REDIS-05) and the test prefix family `"test:cls-*"` share no overlap, so a residual test-key leak surfaces immediately in the `redis-cli --scan` phase-close gate (TEST-REDIS-04).
- **D-13:** `HealthDeadRedisFixture` dead-port = `6379`. Locked by D-01 — since compose maps `6380:6379`, the host's `6379` is guaranteed unbound; connection attempts fail-fast with ECONNREFUSED, surfaced by StackExchange.Redis as `RedisConnectionException`. Zero additional reservation cost.

**DI Registration + Options**
- **D-14:** `AddBaseApiRedis(IServiceCollection, IConfiguration)` registers `IConnectionMultiplexer` via `services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(cfg.GetConnectionString("Redis")))`. Canonical StackExchange.Redis maintainer pattern. Synchronous `Connect()` is safe because the locked connection string carries `abortConnect=false,connectTimeout=5000` (INFRA-REDIS-04) — boot never crashes on a dead Redis (mitigates PITFALLS P2). Lifetime = Singleton (mitigates PITFALLS P1). `IDatabase` resolved per-call via `_multiplexer.GetDatabase()` rather than registered (INFRA-COMP-03).
- **D-15:** `RedisProjectionOptions` surface stays at the REQ INFRA-COMP-04 minimum: `KeyPrefix` (string, default `"skp:"`) + `Serialization` nested object with `JsonOptions` (string discriminator, e.g., `"default"`). No `Database` int, no `CommandFlags`, no `ConnectionString` property. YAGNI — Database / Cluster / replica knobs can be added in v3.4 when a real scale driver appears. Phase 15 writer reads exactly the fields it needs.
- **D-16:** Connection-string source-of-truth = `IConfiguration.GetConnectionString("Redis")` consumed inside the `AddBaseApiRedis(IServiceCollection, IConfiguration)` factory closure. Mirrors the Phase 3 `AddBaseApiPersistence` / `AddNpgsql` reading pattern. Single source of truth = `appsettings.json` / `appsettings.Development.json` `ConnectionStrings:Redis`. NOT bound through `RedisProjectionOptions.ConnectionString` (avoids the indirection that Phase 15 writer doesn't need).

**Cold-Start Behavior**
- **D-17:** `ConnectionMultiplexer.Connect()` fires lazily at first `IConnectionMultiplexer` resolution — i.e., at the first `POST /api/v1/orchestration/start` request after process boot. No pre-warm `IHostedService`. Cold-start path stays minimal; first-request latency cost is sub-second on localhost; with `abortConnect=false`, even a dead Redis lets the Singleton materialize so subsequent failures surface as `RedisConnectionException` at `SetAsync`/`KeyExistsAsync` time → 500 + RFC 7807 (ORCH-START-04 / ORCH-STOP-07 paths).

### Claude's Discretion

The following are not user-locked; planner/researcher may choose the simplest fit:
- Exact text of compose.yaml inline comments documenting D-02 / D-03 intent (planner copies the Phase 11 D-09/D-10 style)
- Whether `RedisServiceCollectionExtensions.cs` introduces a `private const string SectionName = "Redis"` helper or inlines the string literal twice (configuration binding + GetConnectionString)
- xUnit fixture trait names (`[Trait("Phase12Wave", "B")]` etc.) and the test class names for the smoke facts that prove D-05 / D-13 / D-14 behaviors
- Whether the bisect-friendly N-commit sequence for Phase 12 mirrors the Phase 11 10-commit fan-out or the Phase 10 5-commit cadence — planner picks based on plan decomposition; both are acceptable per the established discipline

### Deferred Ideas (OUT OF SCOPE)

- **Redis-specific observability surface** — `/health/redis` endpoint (rejected D-06), Prometheus gauge for `orchestration.redis.connected` (rejected D-06 third option). Future-headroom candidate per OBSERV-REDIS-04 if real ops need arises. Track alongside FUTURE-OTEL-REDIS reactivation.
- **Pre-warm `IHostedService` for Redis Connect()** — rejected D-17 in favor of lazy first-request connect. Re-open if cold-start first-Start latency becomes a problem in any environment where it matters.
- **`RedisProjectionOptions.Database` / `.CommandFlags`** — rejected D-15. Re-open when Redis Cluster or replica reads enter scope (no v3.3.0 driver).
- **Environment.SetEnvironmentVariable fixture override** — rejected D-08 (no need). Re-open only if `Program.cs` starts capturing the Redis connection string by value before `ConfigureAppConfiguration` runs.
- **Assembly-shared `IConnectionMultiplexer`** — rejected D-09 in favor of per-fixture lifetime. Re-open if the test suite's TCP-connection count becomes a real bottleneck (currently ~50, vs Redis `maxclients` 10000).
- **`Phase12WebAppFactory` subclass / rename to `IntegrationWebAppFactory`** — rejected D-07 in favor of in-place extension. Re-open at the next milestone boundary when phase-numbered naming has accumulated enough drag.
- **OrchestrationService body changes** — Phase 13.
- **L1 snapshot loader / validators** — Phases 13–14.
- **L2 RedisProjectionWriter implementation + RedisProjectionKeys** — Phase 15.
- **Start/Stop endpoint semantics** — Phases 15–16.
- **OTel Redis instrumentation** — deferred milestone (OBSERV-REDIS-01). `OpenTelemetry.Instrumentation.StackExchangeRedis` MUST NOT be referenced anywhere in v3.3.0.

</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| INFRA-REDIS-01 | `compose.yaml` runs Redis alongside Postgres/ES/Prom/OTel-Collector pinned to Redis 7.4.x-alpine (RSALv2/SSPLv1, NOT 8.0+ AGPLv3). | Compose Service Block (below); STACK.md licensing analysis; Docker Hub tag listing — current 7.4.x patch is **7.4.9-alpine**. |
| INFRA-REDIS-02 | `redis-cli ping` healthcheck on Redis service with `start_period: 5s` + `interval: 5s` + `retries: 10`; baseapi-service declares `depends_on: redis: condition: service_healthy`. | Healthcheck block in Compose Service Block (below); mirrors Phase 11 `sk-prometheus` healthcheck wget pattern. |
| INFRA-REDIS-03 | `StackExchange.Redis 2.13.1` in `Directory.Packages.props` (CPM) + `<PackageReference>` in `BaseApi.Service.csproj`. | Standard Stack table; CPM pattern verified in existing `Directory.Packages.props` lines 47-117. |
| INFRA-REDIS-04 | `ConnectionStrings:Redis` includes `abortConnect=false,connectTimeout=5000`. | D-04 verbatim strings; mitigates PITFALLS P2 (boot-time RedisConnectionException). |
| INFRA-REDIS-05 | `Redis:KeyPrefix` config section (default `"skp:"`). | D-15 + `RedisProjectionOptions` shape (below). |
| INFRA-REDIS-06 | Soft Redis dependency: `/health/ready` does NOT include Redis check; CRUD endpoints serve 200 even with Redis down. | D-06 (no `AddRedis(...)` call); `HealthDeadRedisFixture` proves both `/live` AND `/ready` return 200. |
| INFRA-COMP-01 | New `AddBaseApiRedis(IServiceCollection, IConfiguration)` in `src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs`; chained inside `AddBaseApi<TDbContext>` as call #7 after Mapping. | Composition Root Pattern (below); see existing `BaseApiServiceCollectionExtensions.cs:24-33` for the 6-call chain to extend. |
| INFRA-COMP-02 | `IConnectionMultiplexer` registered Singleton. | D-14 + Code Example 1; verified against StackExchange.Redis docs (Context7 `/websites/stackexchange_github_io_stackexchange_redis` — "store and re-use this!!!"). |
| INFRA-COMP-03 | `IDatabase` resolved from the singleton multiplexer per-call (not registered as a DI service). | D-14 closing clause; verified against StackExchange.Redis docs ("IDatabase is a lightweight pass-thru and does not need to be stored"). |
| INFRA-COMP-04 | `RedisProjectionOptions` POCO bound via `services.Configure<>(cfg.GetSection("Redis"))`; exposes `KeyPrefix` + `Serialization.JsonOptions`. | D-15 + Code Example 2 (`RedisProjectionOptions` shape). |
| TEST-REDIS-01 | `tests/BaseApi.Tests/Composition/RedisFixture.cs` parallels `PostgresFixture`; per-instance `KeyPrefix = "test:cls-{Guid:N}:"`. | D-09, D-11, D-12; mirrors `PostgresFixture` IAsyncLifetime topology (Code Example 3). |
| TEST-REDIS-02 | `Phase8WebAppFactory` (in-place extension per D-07) encapsulates `RedisFixture` alongside `PostgresFixture`; `ConfigureWebHost` injects via `AddInMemoryCollection`. | D-07 + D-08; Composition Root Pattern; existing `Phase8WebAppFactory.cs:81-133` shape to extend (private `_redisFixture` field + chained Init/Dispose + extended ConfigureWebHost dictionary). |
| TEST-REDIS-03 | `RedisFixture.DisposeAsync` SCANs by `{KeyPrefix}*`, `KeyDeleteAsync` matching keys, re-SCANs, asserts count == 0, throws on violation. `FLUSHDB` is FORBIDDEN. | D-10; Code Example 3 (RedisFixture skeleton); L2-PROJECT-07 SCAN-only invariant locked from day one. |
| TEST-REDIS-04 | Phase-close gate adds `redis-cli --scan \| sort \| sha256sum` BEFORE=AFTER snapshot alongside `psql \l` SHA-256. | Phase-close gate section (below); analogue of v3.2.0 Phase 3 D-15 `0d98b0de…0aac127` SHA-256 invariant. |
| TEST-REDIS-05 | `HealthDeadRedisFixture` extends `Phase8WebAppFactory` with dead Redis port `6379`; proves `/health/live` AND `/health/ready` both return 200 when Redis is unreachable. | D-13 + D-06; mirrors `HealthDeadPostgresFixture` pattern at `HealthEndpointsTests.cs:226-281` (Code Example 5). |

</phase_requirements>

## Project Constraints (from CLAUDE.md)

`./CLAUDE.md` does NOT exist in this repository (verified via Read). No project-level directive file to honor beyond the planning artifacts (REQUIREMENTS.md, ROADMAP.md, STATE.md, CONTEXT.md). Implicit project rules surfaced through 45 prior-plan commits in `STATE.md`:

- **CPM enforcement:** All NuGet versions in `Directory.Packages.props`; csproj `<PackageReference>` carries NO `Version=` attribute (D-05/D-06 from Phase 1; Plan 06-02 build error if violated).
- **TreatWarningsAsErrors=true globally** (Directory.Build.props from Plan 01-01 D-02). Mapperly RMG007/RMG012/RMG020/RMG089 promoted to errors. xUnit1051 catches missing CancellationToken on async test methods.
- **`internal static class XxxServiceCollectionExtensions`** in `BaseApi.Core/DependencyInjection/` for infrastructure DI extensions (`AddBaseApiPersistence`, `AddBaseApiHealth`, etc.) — `internal` because they are chained from `BaseApiServiceCollectionExtensions` in the same assembly. Phase 12 `AddBaseApiRedis` follows this — see Architecture Patterns / Composition Root Pattern.
- **Connection-string fail-fast helper:** `IConfiguration.RequireConnectionString("Redis")` (from `BaseApi.Core/Configuration/RequiredConfig.cs:28-31`) replaces `cfg.GetConnectionString("Redis")!` — produces a clear `InvalidOperationException` message at registration time. Phase 12 MUST use this helper (matches `AddBaseApiHealth` / `AddBaseApiPersistence`).
- **HEALTH-01..05 contracts are byte-for-byte locked** (Phase 5 D-locks); Phase 12 MUST NOT modify `HealthServiceCollectionExtensions.cs:17-30` or `StartupCompletionService.cs:54-72` (D-05, D-06).
- **`psql \l` SHA-256 invariant `0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127`** must hold across the full suite (Phase 3 D-15, honored 11× through Phase 11). Phase 12 extends it with the Redis SCAN SHA-256 analogue (TEST-REDIS-04).
- **3-consecutive-GREEN phase-close cadence** (Phase 3 D-18, honored 11×). Phase 12 inherits.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Redis container lifecycle | Compose / Docker daemon | — | Local dev stack tier; production deployment uses an externally managed Redis (env-var override of `ConnectionStrings__Redis` per D-04 third bullet). |
| `IConnectionMultiplexer` DI registration | API / Backend (BaseApi.Core) | — | Singleton registration belongs in the same composition root as Postgres/Health/Mapping. `BaseApi.Core` infrastructure layer per Phase 7 D-13 precedent. |
| Connection-string source-of-truth | API / Backend (IConfiguration) | Compose env block | `cfg.GetConnectionString("Redis")` read at DI registration time (D-16); compose `ConnectionStrings__Redis` env var overrides appsettings.json defensively. |
| `RedisProjectionOptions` binding | API / Backend (BaseApi.Core) | — | `services.Configure<>(cfg.GetSection("Redis"))` lives in same `AddBaseApiRedis` body as the multiplexer registration. |
| Test fixture per-class isolation | Test infrastructure (tests/BaseApi.Tests) | — | `RedisFixture` mirrors `PostgresFixture` per-class IAsyncLifetime pattern; not a production concern. |
| Dead-Redis acceptance | Test infrastructure | API / Backend (health endpoints) | `HealthDeadRedisFixture` extends `Phase8WebAppFactory`; verifies API tier's HEALTH-01/02/03 tag discipline holds when Redis is unreachable. |
| Phase-close `redis-cli --scan` invariant | Test infrastructure (CLI ritual) | — | Same as `psql \l` SHA-256 — host-side `redis-cli` shell command snapshotted BEFORE/AFTER full test suite. |

**Why this matters:** All Phase 12 capabilities sit cleanly in one of two tiers (API/Backend for DI + options; Test infrastructure for fixtures + gate). No browser-side, no SSR, no CDN. Misassignment risk is low because the locked decisions (D-05, D-06, D-07, D-14) explicitly forbid the only plausible cross-tier mistakes (Redis check in `/health/live` or `/health/ready`; pre-warm IHostedService; subclassed factory).

## Standard Stack

### Core (NEW for Phase 12)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `StackExchange.Redis` | **2.13.1** | Redis client — `IConnectionMultiplexer` Singleton + `IDatabase` per-call | [VERIFIED: NuGet API 2026-05-29 — package exists at 2.13.1; newer 2.13.10 / 2.13.17 also available but D-locked to 2.13.1 by CONTEXT canonical_refs / Plan 01-01 CPM discipline]. De facto standard for .NET since 2014; thread-safe multiplexed connection model; Context7-verified maintainer guidance: *"This object is thread-safe and should be reused across the application"* and *"IDatabase is a lightweight pass-thru and does not need to be stored"* [CITED: https://stackexchange.github.io/StackExchange.Redis/Basics]. |
| `redis:7.4.x-alpine` (Docker image) | **7.4.9-alpine** (current latest 7.4 patch) | Redis server for local dev compose stack | [VERIFIED: Docker Hub tag listing 2026-05-29 — `7.4.9-alpine`, `7.4.9-alpine3.21`, `7.4-alpine` all extant; 7.4.9 is the current 7.4 patch]. 7.4 line dual-licensed RSALv2/SSPLv1; deliberately NOT 8.0+ which adds AGPLv3 (network-distribution copyleft). Alpine variant ~30MB vs ~110MB Debian. |

### Supporting (NO NEW additions for Phase 12 — uses in-box .NET 8 + existing v3.2.0 deps)

| Library | Already Pinned | Purpose | Phase 12 Use |
|---------|----------------|---------|--------------|
| `Microsoft.Extensions.Configuration.Binder` | in-box w/ .NET 8 | `cfg.GetSection("Redis").Bind` or `services.Configure<>` | Bind `RedisProjectionOptions` per INFRA-COMP-04. |
| `Microsoft.Extensions.Options` | in-box w/ .NET 8 | `IOptions<RedisProjectionOptions>` consumption surface | Phase 15 writer reads via `IOptions<RedisProjectionOptions>`; Phase 12 only registers. |
| `Microsoft.AspNetCore.Mvc.Testing 8.0.27` | Directory.Packages.props:106 | `WebApplicationFactory<Program>` | `RedisFixture` composes into existing `Phase8WebAppFactory` per D-07. |
| `xunit.v3 3.2.2` + `xunit.v3.assert` | Directory.Packages.props:110-111 | xUnit v3 IAsyncLifetime | `RedisFixture : IAsyncLifetime` per D-09. |

### NuGet Packages Explicitly NOT Added (FORBIDDEN)

| Package | Reason | Source |
|---------|--------|--------|
| `OpenTelemetry.Instrumentation.StackExchangeRedis` (any version) | Phase 11 D-03 stripped `.WithTracing()`; this package re-enables traces collection. OBSERV-REDIS-01 locks. | [VERIFIED: CONTEXT.md canonical_refs + REQUIREMENTS.md OBSERV-REDIS-01]; PITFALLS.md P13 details the dropped-span / duplicate-span hazards. |
| `Microsoft.Extensions.Caching.StackExchangeRedis` | Wraps as `IDistributedCache` — L2 projection is not an opaque byte-array cache; consumers read typed JSON. | [CITED: STACK.md "What NOT to Use"]. |
| `Testcontainers.Redis` | CONTEXT D-09 chose **host-side compose Redis + per-class key-prefix** over Testcontainers; ARCHITECTURE.md §5 rejected per-class containers (~30-60s suite slowdown). Mentioned in STACK.md as a deferred option but NOT pinned for v3.3.0. | [VERIFIED: ARCHITECTURE.md §5 + CONTEXT.md D-11 hardcoded `localhost:6380`]. |
| `AspNetCore.HealthChecks.Redis` | D-06 — no Redis health check; Phase 5 HEALTH-01..05 contracts unchanged. | [VERIFIED: CONTEXT.md D-06]. |
| `RedisJSON` / `NRedisStack` | L2 DTOs serialized via System.Text.Json strings on `RedisString`. | [CITED: STACK.md Alternatives Considered]. |

### Installation

**`Directory.Packages.props`** — append to the existing `<ItemGroup>` after the v3.2.0 pins (insert near line 117, after the `Microsoft.Extensions.TimeProvider.Testing` line):

```xml
<!-- v3.3.0 / Phase 12 — Redis L2 projection client (CPM pin) -->
<PackageVersion Include="StackExchange.Redis" Version="2.13.1" />
```

**`src/BaseApi.Service/BaseApi.Service.csproj`** — append to the existing `<ItemGroup>` of PackageReferences (lines 38-49):

```xml
<!-- Phase 12 — Redis L2 client. Version pinned in Directory.Packages.props (CPM). -->
<PackageReference Include="StackExchange.Redis" />
```

**`tests/BaseApi.Tests/BaseApi.Tests.csproj`** — append a `<PackageReference>` so `RedisFixture` and `HealthDeadRedisFixture` compile:

```xml
<!-- Phase 12 — RedisFixture per-class IConnectionMultiplexer + KeyPrefix isolation. -->
<PackageReference Include="StackExchange.Redis" />
```

**Version verification (per RESEARCH.md template requirement):** `Directory.Packages.props` line 47-117 lists CPM-managed pins; the planner should NOT bump `StackExchange.Redis` beyond 2.13.1 without revisiting CONTEXT D-locks (the CONTEXT canonical_refs cite "2.13.1 (or current 2.13.x at commit time)" per INFRA-REDIS-03 — 2.13.10 and 2.13.17 are wire-compatible 2.13.x successors but D-locked baseline is 2.13.1).

**Redis image pin verification:** STACK.md mentions `redis:7.4.2-alpine` but that was authored 2026-05-28; Docker Hub tag listing as of 2026-05-29 shows `7.4.9-alpine` is the current 7.4-line patch. CONTEXT D-locks "7.4.x-alpine" as a placeholder. **Recommendation to planner:** pin to the exact patch in effect at compose-edit time (likely `redis:7.4.9-alpine`); confirm via `docker pull redis:7.4-alpine && docker inspect --format '{{.RepoDigests}}' redis:7.4-alpine` and record the digest in the compose comment block.

## Architecture Patterns

### System Architecture Diagram

```
                  ┌──────────────────────────────────────────────────────────┐
                  │                    docker compose (sk_p)                  │
                  │                                                            │
                  │  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────┐  │
                  │  │ postgres │  │ es 8.15  │  │ prom 3.11│  │ otel-coll│  │
                  │  │  17-alp  │  │ (sk-es)  │  │ (sk-prom)│  │ 0.152.0  │  │
                  │  │5433:5432 │  │9200:9200 │  │9090:9090 │  │4317/4318 │  │
                  │  └────┬─────┘  └────┬─────┘  └────┬─────┘  └────┬─────┘  │
                  │       │             │             │             │        │
                  │  ┌────┴─────────────┴─────────────┴─────────────┴─────┐  │
                  │  │              baseapi-service (8080:8080)           │  │
                  │  │   depends_on: postgres, es, prom (healthy)         │  │
                  │  │              + otel-collector (started)            │  │
                  │  │              + redis (healthy) ←── NEW Phase 12   │  │
                  │  └─────────────────────────────┬──────────────────────┘  │
                  │                                │                          │
                  │  ┌─────────────────────────────┴──────────────────────┐  │
                  │  │   sk-redis  ←── NEW Phase 12                       │  │
                  │  │   redis:7.4.x-alpine                               │  │
                  │  │   host port 6380:6379  (D-01 — collision-avoid)    │  │
                  │  │   --save "" --appendonly no  (D-03 — no persist)   │  │
                  │  │   HC: redis-cli ping, 5s/5s/10 retries, start 5s   │  │
                  │  └────────────────────────────────────────────────────┘  │
                  └──────────────────────────────────────────────────────────┘

  ┌──────────────────────────────────────────────────────────────────────┐
  │           BaseApi.Service composition root (Program.cs)              │
  │                                                                       │
  │   builder.AddBaseApiObservability(cfg)                                │
  │   builder.Services.AddBaseApi<AppDbContext>(cfg)                      │
  │   builder.Services.AddAppFeatures()                                   │
  │                                                                       │
  │             AddBaseApi<TDbContext> CHAIN (BaseApi.Core)                │
  │   ┌───────────────────────────────────────────────────────────────┐  │
  │   │ 1. AddBaseApiPersistence<TDbContext>(cfg)  Phase 3             │  │
  │   │ 2. AddBaseApiHealth(cfg)                   Phase 5 (unchanged) │  │
  │   │ 3. AddBaseApiErrorHandling()               Phase 4             │  │
  │   │ 4. AddBaseApiHttp(cfg)                     Phase 7             │  │
  │   │ 5. AddBaseApiValidation(asm)               Phase 6             │  │
  │   │ 6. AddBaseApiMapping(asm)                  Phase 6             │  │
  │   │ 7. AddBaseApiRedis(cfg)              ←── NEW Phase 12           │  │
  │   │      services.AddSingleton<IConnectionMultiplexer>(_ =>        │  │
  │   │        ConnectionMultiplexer.Connect(                          │  │
  │   │          cfg.RequireConnectionString("Redis")))                │  │
  │   │      services.Configure<RedisProjectionOptions>(               │  │
  │   │        cfg.GetSection("Redis"))                                │  │
  │   └───────────────────────────────────────────────────────────────┘  │
  └──────────────────────────────────────────────────────────────────────┘

  ┌──────────────────────────────────────────────────────────────────────┐
  │  HEALTH endpoints (UNCHANGED — Phase 5 HEALTH-01..05 byte-identical)  │
  │  /health/live      → tags: live          (process-only, no Redis)    │
  │  /health/ready     → tags: ready         (Postgres only, NO Redis)   │
  │  /health/startup   → tags: startup,ready (StartupGate, NO Redis)     │
  │  D-05: StartupCompletionService does NOT probe Redis                  │
  │  D-06: No /health/redis; no Redis ready-tag                           │
  └──────────────────────────────────────────────────────────────────────┘

  ┌──────────────────────────────────────────────────────────────────────┐
  │           Test composition (tests/BaseApi.Tests — Phase 12)           │
  │                                                                       │
  │   Phase8WebAppFactory  (EXTENDED in-place — D-07)                     │
  │   ├─ private PostgresFixture? _fixture       (existing)               │
  │   ├─ private RedisFixture? _redisFixture     (NEW)                    │
  │   ├─ InitializeAsync()                                                │
  │   │     await _fixture.InitializeAsync()    (existing — unless skip) │
  │   │     await _redisFixture.InitializeAsync() (NEW — unless skip)    │
  │   ├─ ConfigureWebHost(IWebHostBuilder)                                │
  │   │     AddInMemoryCollection:                                        │
  │   │       ["ConnectionStrings:Postgres"] = ConnectionString (exist)  │
  │   │       ["ConnectionStrings:Redis"]    = _redisFixture.ConnStr     │
  │   │       ["Redis:KeyPrefix"]            = _redisFixture.KeyPrefix   │
  │   └─ DisposeAsync()                                                    │
  │         await _redisFixture.DisposeAsync()  (NEW — SCAN+DEL+assert)  │
  │         await _fixture.DisposeAsync()       (existing)                │
  │                                                                       │
  │   RedisFixture (NEW — mirrors PostgresFixture per-class topology)     │
  │   ├─ ConnectionString = "localhost:6380,abortConnect=false"           │
  │   ├─ KeyPrefix        = $"test:cls-{Guid.NewGuid():N}:"               │
  │   ├─ Multiplexer = ConnectionMultiplexer.Connect(ConnectionString)    │
  │   └─ DisposeAsync: SCAN MATCH {KeyPrefix}* → DEL → re-SCAN → assert 0│
  │                                                                       │
  │   HealthDeadRedisFixture (NEW — mirrors HealthDeadPostgresFixture)    │
  │   ├─ skipPostgresFixture: true / skipRedisFixture: true (new flag)    │
  │   ├─ connectionStringOverride: "Host=...5433;..." (live Postgres)    │
  │   ├─ redisConnectionStringOverride: "localhost:6379,abort=false"     │
  │   └─ Proves both /health/live AND /health/ready return 200            │
  └──────────────────────────────────────────────────────────────────────┘
```

### Recommended Project Structure (NEW files only — modifications listed separately)

```
src/BaseApi.Core/
├── DependencyInjection/
│   ├── BaseApiServiceCollectionExtensions.cs    (MODIFY — +1 chained call to AddBaseApiRedis)
│   └── RedisServiceCollectionExtensions.cs      (NEW — Phase 12)
└── Configuration/
    └── RedisProjectionOptions.cs                (NEW — Phase 12)

src/BaseApi.Service/
├── appsettings.json                             (MODIFY — +ConnectionStrings:Redis docker-internal + Redis section)
├── appsettings.Development.json                 (MODIFY — +ConnectionStrings:Redis host-side localhost:6380)
└── BaseApi.Service.csproj                       (MODIFY — +StackExchange.Redis PackageReference)

(repo root)
├── compose.yaml                                 (MODIFY — +redis service + baseapi-service depends_on + env block)
└── Directory.Packages.props                     (MODIFY — +StackExchange.Redis 2.13.1 PackageVersion)

tests/BaseApi.Tests/
├── BaseApi.Tests.csproj                         (MODIFY — +StackExchange.Redis PackageReference)
├── Composition/
│   ├── Phase8WebAppFactory.cs                   (MODIFY — in-place D-07 extension)
│   └── RedisFixture.cs                          (NEW — Phase 12)
└── Observability/
    └── HealthEndpointsTests.cs                  (MODIFY — add HealthDeadRedisFixture nested + 1 new fact OR factor into separate file per planner discretion)
```

### Pattern 1: AddBaseApiRedis — Mirror AddBaseApiPersistence Verbatim

**What:** Internal static class with single `internal static IServiceCollection AddBaseApiRedis(this IServiceCollection, IConfiguration)` method, chained as call #7 inside `AddBaseApi<TDbContext>`.

**When to use:** This is the ONLY pattern for Phase 12. Mirrors `AddBaseApiPersistence` shape verbatim per D-14 / D-16 + ARCHITECTURE.md §1.

**Why mirror Persistence (not the Phase 7 D-13 Observability split):** Per ARCHITECTURE.md §1 verbatim — *"`StackExchange.Redis.ConnectionMultiplexer.Connect(...)` registration needs **only `IServiceCollection`** — there is no MEL bridge equivalent, no `builder.Logging` surface, no `builder.Configuration` surface that isn't already reachable via the `IConfiguration` parameter that `AddBaseApi<TDbContext>` already accepts."* Phase 7 D-13 split exists for `ILoggingBuilder`; Redis has no such need. [CITED: ARCHITECTURE.md §1 lines 11-19].

**Example (verbatim copyable skeleton):**

```csharp
// src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs
using BaseApi.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Phase 12 Redis L2 client wiring: IConnectionMultiplexer Singleton (INFRA-COMP-02)
/// + RedisProjectionOptions binding (INFRA-COMP-04). IDatabase is NOT registered —
/// consumers (Phase 15 RedisProjectionWriter) call _multiplexer.GetDatabase() per
/// operation per INFRA-COMP-03 (canonical StackExchange.Redis pattern; IDatabase is
/// a lightweight pass-thru that does not need to be stored).
///
/// <para>
/// D-14: Synchronous ConnectionMultiplexer.Connect() is safe inside the Singleton
/// factory closure because the locked connection string carries abortConnect=false
/// (INFRA-REDIS-04) — boot never crashes on a dead Redis. The multiplexer
/// materializes even if Redis is unreachable; subsequent operations throw
/// RedisConnectionException at SetAsync/KeyExistsAsync call sites (ORCH-START-04
/// / ORCH-STOP-07).
/// </para>
///
/// <para>
/// D-05: This extension does NOT probe Redis at startup. PERSIST-10 + HEALTH-01..05
/// contracts preserved verbatim; StartupCompletionService is not extended.
/// </para>
///
/// <para>
/// D-06: This extension does NOT register a Redis health check. INFRA-REDIS-06 soft
/// dependency: Redis down ⇒ /health/live AND /health/ready both return 200; only
/// /api/v1/orchestration/{start,stop} fail with 500 + RFC 7807.
/// </para>
/// </summary>
internal static class RedisServiceCollectionExtensions
{
    internal static IServiceCollection AddBaseApiRedis(
        this IServiceCollection services, IConfiguration cfg)
    {
        // WR-03 fail-fast helper (RequireConnectionString from BaseApi.Core/Configuration/
        // RequiredConfig.cs) — mirrors AddBaseApiPersistence and AddBaseApiHealth.
        var connStr = cfg.RequireConnectionString("Redis");

        // D-14 / INFRA-COMP-02: Singleton lifetime is the StackExchange.Redis
        // maintainer-blessed pattern. ConnectionMultiplexer is thread-safe and designed
        // for long-lived reuse; per-request construction defeats the multiplexing model.
        // D-17: Lazy first-resolution — no pre-warm IHostedService.
        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(connStr));

        // D-16 / INFRA-COMP-04: bind Redis:* section to RedisProjectionOptions.
        // KeyPrefix and Serialization.JsonOptions are the only fields; YAGNI-pruned
        // per D-15 (no Database int, no CommandFlags, no ConnectionString property).
        services.Configure<RedisProjectionOptions>(cfg.GetSection("Redis"));

        return services;
    }
}
```

**Source:** ARCHITECTURE.md §1 Code Example (lines 22-39), tightened per CONTEXT D-14/D-15/D-16. The `internal` visibility matches `AddBaseApiPersistence` and `AddBaseApiHealth` — the extension is chained from `AddBaseApi<TDbContext>` in the same assembly. [VERIFIED: existing `PersistenceServiceCollectionExtensions.cs:17` uses `internal static class`].

### Pattern 2: Composition Root — Insert as Call #7

**What:** Single new line in `BaseApiServiceCollectionExtensions.cs` after the current call #6 (`AddBaseApiMapping`).

**Where:** Existing file `src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs` lines 24-33. The 6-call chain is preserved order; Redis is appended as the new tail.

**Example (the single line change):**

```csharp
public static IServiceCollection AddBaseApi<TDbContext>(
    this IServiceCollection services, IConfiguration cfg)
    where TDbContext : BaseDbContext
    => services
        .AddBaseApiPersistence<TDbContext>(cfg)                              // 1 — Phase 3
        .AddBaseApiHealth(cfg)                                               // 2 — Phase 5 (unchanged — D-06)
        .AddBaseApiErrorHandling()                                           // 3 — Phase 4
        .AddBaseApiHttp(cfg)                                                 // 4 — Phase 7
        .AddBaseApiValidation(typeof(TDbContext).Assembly)                   // 5 — Phase 6
        .AddBaseApiMapping(typeof(TDbContext).Assembly)                      // 6 — Phase 6
        .AddBaseApiRedis(cfg);                                               // 7 — Phase 12 NEW
```

**Why this slot and not after AddBaseApiHealth:** No ordering dependency — `AddBaseApiRedis` only writes to `IServiceCollection` and reads from `IConfiguration`. Appending after `AddBaseApiMapping` minimizes diff and respects the "infrastructure now, features next" mental ordering (Persistence/Health/ErrorHandling/Http/Validation/Mapping are all base infra; Redis joins as new infra). CONTEXT plan-decomposition hint line 161 explicitly says "after current call #6 Mapping". [CITED: CONTEXT.md `<canonical_refs>` Existing v3.2.0 source files bullet citing `BaseApiServiceCollectionExtensions.cs`].

**Source:** ARCHITECTURE.md §1 Code Example lines 43-54; existing file verified at `src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs:24-33`.

### Pattern 3: Compose Service Block — Mirror Phase 11 sk-prometheus Healthcheck Discipline

**What:** New top-level `services.redis:` block + `baseapi-service.depends_on.redis: service_healthy` + `baseapi-service.environment.ConnectionStrings__Redis` env var.

**Example (CompletedCompose.yaml addition — verbatim):**

```yaml
  # Phase 12 D-02 — sk-redis container_name mirrors sk-elasticsearch (Phase 11 D-10)
  # and sk-prometheus (Phase 11 D-13) for mutual exclusion with sk2_1 on a single
  # Docker daemon. The sk-* prefix is the cross-stack uniqueness convention.
  #
  # Phase 12 D-01 — host port 6380:6379 mirrors Phase 2 D-01 Postgres 5433:5432
  # collision-avoidance. Defends against a developer's local Redis on the default 6379
  # AND defines the HealthDeadRedisFixture dead-port (the unbound host 6379) for
  # TEST-REDIS-05 zero-allocation dead-Redis acceptance facts.
  #
  # Phase 12 D-03 — persistence intentionally disabled. L2 is rebuildable from L3
  # (Postgres) on demand; RDB snapshots + AOF are dead weight and slow boot. The
  # absence of a `volumes:` entry + the explicit `--save "" --appendonly no` command
  # encode this intent so a future operator does NOT re-enable persistence as a
  # "fix" for some other problem.
  #
  # INFRA-REDIS-01 — Redis 7.4.x-alpine pinned deliberately to the 7.4 line
  # (RSALv2/SSPLv1 dual-license, NOT the 8.0+ AGPLv3 tri-license). 7.4.x's licensing
  # is restrictive but does not have the network-distribution copyleft clause that
  # would force legal review.
  redis:
    image: redis:7.4.9-alpine   # planner: confirm latest 7.4 patch at edit time
    container_name: sk-redis
    restart: unless-stopped
    ports:
      - "6380:6379"
    command: ["redis-server", "--save", "", "--appendonly", "no"]
    healthcheck:
      # INFRA-REDIS-02 — redis-cli ping with exit code 0 on PONG, non-zero on
      # connection failure. The alpine image ships redis-cli in /usr/local/bin.
      # Interval/timeout/retries/start_period mirror the sk-prometheus pattern at
      # ~lines 105-114 above (10s interval / 5s timeout / 3 retries / 10s start)
      # but tightened to 5s/3s/10/5s per CONTEXT D-locked INFRA-REDIS-02 acceptance
      # (start_period: 5s, interval: 5s, retries: 10).
      test: ["CMD", "redis-cli", "ping"]
      interval: 5s
      timeout: 3s
      retries: 10
      start_period: 5s
```

**`baseapi-service` block modifications (compose.yaml lines 116-141):**

Add to the existing `environment:` block (alongside `ConnectionStrings__Postgres`):

```yaml
      # Phase 12 D-04 — defensive override mirrors the existing ConnectionStrings__Postgres
      # convention. Docker-internal hostname `redis` resolves via compose service DNS.
      ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"
```

Add to the existing `depends_on:` block (alongside postgres/otel-collector/elasticsearch/prometheus):

```yaml
      redis:
        condition: service_healthy
```

**Sources:** ARCHITECTURE.md §4 Code Example (compose snippet) tightened for CONTEXT D-locks; existing compose.yaml lines 1-145 verified; Phase 11 sk-prometheus pattern at compose.yaml lines 86-114 as the verbatim template for healthcheck cadence; STACK.md "Docker Compose addition" section at lines 192-208 as the secondary reference. [VERIFIED: Docker Hub tag listing 2026-05-29 confirms `7.4.9-alpine` extant; `redis-cli` ships in the alpine image by default — `docker run --rm redis:7.4-alpine which redis-cli` returns `/usr/local/bin/redis-cli`].

### Pattern 4: RedisFixture — Mirror PostgresFixture IAsyncLifetime Topology

**What:** Sealed class implementing `IAsyncLifetime`; constructs `IConnectionMultiplexer` in `InitializeAsync`; runs SCAN+DEL+assert cleanup in `DisposeAsync`; exposes `ConnectionString` + `KeyPrefix` + `Multiplexer` for `Phase8WebAppFactory` to compose.

**Example (verbatim skeleton):**

```csharp
// tests/BaseApi.Tests/Composition/RedisFixture.cs
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Composition;

/// <summary>
/// Per-test-class Redis fixture mirroring <see cref="BaseApi.Tests.Persistence.PostgresFixture"/>.
/// Connects to the host-side compose Redis at localhost:6380 (D-11) and isolates each
/// test class via a unique KeyPrefix = "test:cls-{Guid:N}:" (D-12 / TEST-REDIS-01).
///
/// <para>
/// <b>D-09 lifetime:</b> One IConnectionMultiplexer per fixture instance. With ~50
/// test classes that consume Phase8WebAppFactory, peak concurrent multiplexer count
/// is well within Redis maxclients default budget (10000). Per-class disposal
/// guarantees TCP-connection accounting under xUnit v3 parallel class execution.
/// </para>
///
/// <para>
/// <b>D-10 cleanup contract:</b> DisposeAsync SCANs MATCH "{KeyPrefix}*", issues
/// KeyDeleteAsync(keys), re-SCANs with the same prefix, and ASSERTS count == 0.
/// Throws on violation (fail-loud — TEST-REDIS-03 verbatim). FLUSHDB is FORBIDDEN
/// (would destroy keys from parallel-running test classes; hides genuine leaks).
/// Cursor-based SCAN preserves the L2-PROJECT-07 forbidden-IServer.Keys()/KEYS
/// invariant from day one (production code uses SCAN-only — this fixture sets the
/// reference pattern).
/// </para>
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    // D-11 — hardcoded localhost:6380. Mirrors PostgresFixture's hardcoded
    // localhost:5433 (PostgresFixture.cs:28). No env-var override; if compose isn't
    // running, the first PING fails-loud and tests stop (correct behavior).
    public string ConnectionString { get; } =
        "localhost:6380,abortConnect=false,connectTimeout=5000";

    // D-12 / TEST-REDIS-01 — per-instance Guid prefix. Production prefix "skp:"
    // (INFRA-REDIS-05) and test prefix family "test:cls-*" share no overlap, so
    // a residual test-key leak surfaces immediately in the redis-cli --scan
    // phase-close gate (TEST-REDIS-04).
    public string KeyPrefix { get; } = $"test:cls-{Guid.NewGuid():N}:";

    public IConnectionMultiplexer Multiplexer { get; private set; } = default!;

    public async ValueTask InitializeAsync()
    {
        // D-09 — per-fixture multiplexer. Synchronous Connect() is safe because the
        // connection string carries abortConnect=false; even a dead Redis lets
        // Connect() return (PITFALLS P2 mitigation).
        Multiplexer = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var db = Multiplexer.GetDatabase();
            var server = Multiplexer.GetServer(Multiplexer.GetEndPoints()[0]);

            // SCAN MATCH "{KeyPrefix}*" — cursor-based, non-blocking.
            // server.KeysAsync uses SCAN under the hood (NOT KEYS — L2-PROJECT-07).
            var toDelete = new List<RedisKey>();
            await foreach (var key in server.KeysAsync(pattern: $"{KeyPrefix}*", pageSize: 250))
            {
                toDelete.Add(key);
            }

            if (toDelete.Count > 0)
            {
                await db.KeyDeleteAsync(toDelete.ToArray());
            }

            // Re-SCAN and assert count == 0 — TEST-REDIS-03 fail-loud discipline.
            var residualCount = 0;
            await foreach (var _ in server.KeysAsync(pattern: $"{KeyPrefix}*", pageSize: 250))
            {
                residualCount++;
            }

            if (residualCount > 0)
            {
                throw new InvalidOperationException(
                    $"RedisFixture cleanup violation: {residualCount} residual keys matching " +
                    $"pattern '{KeyPrefix}*' after SCAN+DEL. This indicates a leaked-key bug " +
                    $"in a Phase 12+ test. FLUSHDB is FORBIDDEN — investigate the offending test.");
            }
        }
        finally
        {
            // Always dispose the multiplexer even if the SCAN/DEL assertion threw.
            await Multiplexer.DisposeAsync();
        }
    }
}
```

**Source:** PostgresFixture.cs:22-56 verbatim topology + ARCHITECTURE.md §5 (lines 198-216) Implementation snippet + CONTEXT D-09/D-10/D-11/D-12. [VERIFIED: existing `tests/BaseApi.Tests/Persistence/PostgresFixture.cs:30-55` for shape; `server.KeysAsync` is SCAN-backed per StackExchange.Redis docs — Context7 confirmed].

### Pattern 5: Phase8WebAppFactory In-Place Extension (D-07)

**What:** Add `RedisFixture? _redisFixture` field + extend InitializeAsync/DisposeAsync chains + extend `ConfigureWebHost` AddInMemoryCollection dictionary with `["ConnectionStrings:Redis"]` and `["Redis:KeyPrefix"]`.

**Why in-place not subclassed:** D-07 verbatim — *"Phase8WebAppFactory extended in place rather than renamed or subclassed. Accretes RedisFixture composition alongside PostgresFixture (encapsulated internally per Phase 8 Plan 05-02 Pattern C reasoning). Avoids cross-test churn of the ~30 facts that bind IClassFixture<Phase8WebAppFactory> today."*

**Example (interior modifications — minimal diff against existing 134-line file):**

```csharp
// tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs

// === Existing fields (UNCHANGED) ===
private PostgresFixture? _fixture;
private readonly string? _connectionStringOverride;
private readonly bool _skipPostgresFixture;

// === NEW Phase 12 fields ===
private RedisFixture? _redisFixture;
private readonly string? _redisConnectionStringOverride;   // for HealthDeadRedisFixture
private readonly bool _skipRedisFixture;                    // mirror skipPostgresFixture

// === Existing ConnectionString property (UNCHANGED) ===
public string ConnectionString => _connectionStringOverride
    ?? _fixture?.ConnectionString
    ?? throw new InvalidOperationException("InitializeAsync has not run yet.");

// === NEW Phase 12 properties ===
public string RedisConnectionString => _redisConnectionStringOverride
    ?? _redisFixture?.ConnectionString
    ?? throw new InvalidOperationException("InitializeAsync has not run yet.");

public string RedisKeyPrefix => _redisFixture?.KeyPrefix
    ?? "test:cls-deadredis:";   // benign placeholder for skipRedisFixture path

// === Modified InitializeAsync ===
public async ValueTask InitializeAsync()
{
    if (!_skipPostgresFixture && _connectionStringOverride is null)
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();
    }
    if (!_skipRedisFixture)
    {
        _redisFixture = new RedisFixture();
        await _redisFixture.InitializeAsync();
    }
}

// === Modified DisposeAsync ===
public override async ValueTask DisposeAsync()
{
    await base.DisposeAsync();
    // Order: Redis first (releases multiplexer + asserts cleanup), then Postgres
    // (NpgsqlConnection.ClearPool + DROP DATABASE WITH FORCE).
    if (_redisFixture is not null)
    {
        await _redisFixture.DisposeAsync();
    }
    if (_fixture is not null)
    {
        await using (var conn = new Npgsql.NpgsqlConnection(_fixture.ConnectionString))
        {
            Npgsql.NpgsqlConnection.ClearPool(conn);
        }
        await _fixture.DisposeAsync();
    }
}

// === Modified ConfigureWebHost ===
protected override void ConfigureWebHost(IWebHostBuilder builder)
{
    builder.ConfigureAppConfiguration((_, cfg) =>
    {
        cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = ConnectionString,
            // D-08 — RedisFixture injection via AddInMemoryCollection. AddBaseApiRedis
            // reads cfg.GetConnectionString("Redis") at DI registration time, so the
            // env-var-in-ctor workaround (Plan 05-02 Pattern C for Postgres) does NOT
            // apply here.
            ["ConnectionStrings:Redis"] = RedisConnectionString,
            ["Redis:KeyPrefix"] = RedisKeyPrefix,
        });
    });

    base.ConfigureWebHost(builder);
}
```

**New constructor overload (for `HealthDeadRedisFixture`):**

```csharp
/// <summary>
/// Phase 12 TEST-REDIS-05 — variant for HealthDeadRedisFixture that injects a
/// dead Redis port (D-13: host's :6379 is unbound by D-01 compose mapping).
/// May ALSO skip Postgres if the test wants live Postgres reachable instead
/// (TEST-REDIS-05 wants Postgres reachable so /health/ready can return 200 from
/// the Postgres-only ready-tag side).
/// </summary>
protected Phase8WebAppFactory(
    bool skipPostgresFixture, string connectionStringOverride,
    bool skipRedisFixture, string redisConnectionStringOverride)
{
    // (Existing validation for skipPostgresFixture+connectionStringOverride pair)
    if (skipPostgresFixture && string.IsNullOrEmpty(connectionStringOverride))
        throw new ArgumentException(...);
    // NEW: same validation for Redis side
    if (skipRedisFixture && string.IsNullOrEmpty(redisConnectionStringOverride))
        throw new ArgumentException(
            "skipRedisFixture=true requires a non-empty redisConnectionStringOverride.",
            nameof(redisConnectionStringOverride));

    _skipPostgresFixture = skipPostgresFixture;
    _connectionStringOverride = connectionStringOverride;
    _skipRedisFixture = skipRedisFixture;
    _redisConnectionStringOverride = redisConnectionStringOverride;
}
```

**Source:** Existing `Phase8WebAppFactory.cs:26-134` line-by-line + CONTEXT D-07/D-08; mirrors the existing `HealthDeadPostgresFixture` constructor pattern at `HealthEndpointsTests.cs:226-281`. [VERIFIED: existing file shape; existing dead-Postgres pattern at lines 32-75 per CONTEXT line 133].

### Anti-Patterns to Avoid

- **Per-request `IConnectionMultiplexer`** (Scoped/Transient): one TCP handshake per HTTP request → Redis `maxclients` exhaustion. D-14 locks Singleton; PITFALLS P1 mitigation. [VERIFIED: Context7 maintainer guidance: *"store and re-use this!!!"*].
- **Synchronous `Connect()` without `abortConnect=false`**: process crashes if Redis briefly unreachable at boot. D-04 / INFRA-REDIS-04 lock `abortConnect=false,connectTimeout=5000` in the connection string. PITFALLS P2 mitigation.
- **`FLUSHDB` for test cleanup**: destroys keys from parallel-running test classes; hides genuine leaks. TEST-REDIS-03 explicitly forbids; D-10 cleanup contract uses SCAN+DEL+assert. [CITED: REQUIREMENTS.md Out of Scope line 130: "`FLUSHDB` for test cleanup — explicitly forbidden"].
- **`IServer.Keys()` synchronous (KEYS-backed) enumeration**: O(N) blocking command over entire keyspace. L2-PROJECT-07 locks SCAN-only; `RedisFixture.DisposeAsync` uses `server.KeysAsync` (SCAN-backed) per D-10. [CITED: REQUIREMENTS.md L2-PROJECT-07].
- **`OpenTelemetry.Instrumentation.StackExchangeRedis`**: trace-side instrumentation; re-enables `.WithTracing()` pipeline stripped by Phase 11 D-03. OBSERV-REDIS-01 + PITFALLS P13. **FORBIDDEN in v3.3.0** — planner MUST verify no csproj references this package.
- **Pre-warm `IHostedService` for Redis Connect()**: rejected D-17 in favor of lazy first-request connect. Adding a hosted service to probe Redis at startup also re-introduces the failure mode D-05 explicitly avoided (StartupCompletionService probing external state). [CITED: CONTEXT D-17 + D-05].
- **`RedisProjectionOptions.ConnectionString`**: avoid the indirection. Source-of-truth is `IConfiguration.GetConnectionString("Redis")` (D-16). [CITED: CONTEXT D-16].
- **Adding Redis to `AddBaseApiHealth`**: D-06 — no `/health/redis`, no `AddRedis(...).WithTags("ready")`. Phase 5 HEALTH-01..05 contracts byte-identical. Planner MUST NOT touch `HealthServiceCollectionExtensions.cs:17-30`.
- **Renaming `Phase8WebAppFactory` to `Phase12WebAppFactory` or `IntegrationWebAppFactory`**: rejected D-07. Would force ~30 existing test classes to update `IClassFixture<>` bindings. In-place extension preferred.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Connection pooling / TCP-multiplexing | Custom `Socket` + `RESP` framing | `StackExchange.Redis.ConnectionMultiplexer` | Multiplexed connection model; pipelining; auto-reconnect; thread-safe; battle-tested at Stack Overflow scale since 2014. [VERIFIED: Context7 / official docs]. |
| Redis health check | Custom `IHealthCheck` implementing PING | **None — D-06 forbids ANY Redis health check** | Phase 5 HEALTH-01..05 contracts locked; soft-dep INFRA-REDIS-06; `AspNetCore.HealthChecks.Redis` Xabaril package explicitly NOT pinned. |
| Redis cursor-based key enumeration | Manual `SCAN 0 MATCH ... COUNT ...` loop | `IServer.KeysAsync(pattern: ..., pageSize: 250)` | StackExchange.Redis wraps SCAN as an `IAsyncEnumerable<RedisKey>` — exactly what RedisFixture.DisposeAsync needs per D-10. [CITED: STACK.md / ARCHITECTURE.md §6]. |
| Multiplexer reconnect backoff | Custom retry policy | `ConfigurationOptions.ReconnectRetryPolicy` (default exponential) | StackExchange.Redis has built-in `ExponentialRetry(5000)` reconnect policy. [CITED: Context7 docs — Configuration page]. |
| Connection-string parsing | Custom `string.Split(',')` | `ConfigurationOptions.Parse(connStr)` | Handles `abortConnect=false,connectTimeout=5000,ssl=true,name=...` syntax; planner can use the raw string overload `ConnectionMultiplexer.Connect(connStr)` (D-14) since CONTEXT does NOT require programmatic option mutation. |
| Per-class test isolation | Custom test-database-per-class plumbing | `RedisFixture : IAsyncLifetime` mirroring `PostgresFixture` | xUnit v3 IClassFixture<> provides per-class lifetime; existing PostgresFixture (Phase 3) is the proven template. |
| Cleanup-correctness assertion | Custom DBSIZE check | SCAN-then-assert-count==0 in DisposeAsync | D-10 / TEST-REDIS-03 verbatim contract; DBSIZE would conflate parallel-class data. |

**Key insight:** Phase 12 is **infrastructure plumbing**; almost every concern has a canonical library or established project pattern. The only hand-rolled code is the SCAN+DEL+assert loop in `RedisFixture.DisposeAsync` (~20 LOC) and the `RedisProjectionOptions` POCO (~10 LOC). Everything else is wire-up.

## Common Pitfalls

### Pitfall 1: ConnectionMultiplexer registered Scoped/Transient — connection storm
**What goes wrong:** Per-request TCP handshake exhausts Redis `maxclients`; 500 cascade after ~10 minutes; restart "fixes" for 30 seconds.
**Why it happens:** Path of least resistance — every other infra dep in `BaseApi.Core` is Scoped. Junior dev intuition picks AddScoped.
**How to avoid:** D-14 locks `AddSingleton<IConnectionMultiplexer>`. Reference-equality assertion fact in Wave-C verification — resolve from root scope + from a created scope, `Assert.Same(rootMux, scopedMux)`.
**Warning signs:** `services.AddScoped<IConnectionMultiplexer>` or `services.AddTransient<IConnectionMultiplexer>` in any source file; `CLIENT LIST` against the compose Redis showing ≥1 connection per request.
**Source:** PITFALLS.md P1; Context7 maintainer guidance.

### Pitfall 2: `abortConnect=false` accidentally dropped from connection string — startup crash on dead Redis
**What goes wrong:** `appsettings.Development.json` PR omits the `,abortConnect=false` suffix; `ConnectionMultiplexer.Connect()` throws `RedisConnectionException`; process exits; restart loop; `/health/live` flaps. Soft-dep contract (INFRA-REDIS-06 / D-05) broken.
**Why it happens:** Connection-string strings are easy to mistype; PR review may miss the suffix.
**How to avoid:** Acceptance fact in Phase 12 close — string-match `abortConnect=false` in BOTH appsettings.json AND appsettings.Development.json (one regex over both files). The fail-loud message from `RequireConnectionString` does NOT catch this; only an in-string assertion does.
**Warning signs:** `RedisConnectionException: It was not possible to connect ...` in startup logs; baseapi-service container restart-loops while Redis healthcheck is `starting`.
**Source:** PITFALLS.md P2; CONTEXT D-04.

### Pitfall 3: HealthDeadRedisFixture accidentally probes live Redis port
**What goes wrong:** Dead-Redis fact passes for the wrong reason (Redis happens to be DOWN at test time, not because the fixture overrode the connection string). Test stays green even when soft-dep contract regresses.
**Why it happens:** D-13 dead-port is `6379`; if the compose redis service ever maps `6379:6379` directly (regression of D-01's `6380:6379` collision-avoidance), 6379 becomes LIVE on the host and the dead-port assumption breaks silently.
**How to avoid:** `HealthDeadRedisFixture` MUST also assert that its dead-port `6379` is unbound BEFORE running the health check probe (TCP-connect to localhost:6379, expect `SocketException` with `ConnectionRefused`). If 6379 is bound, throw immediately with "compose D-01 regression — Redis exposed on default port 6379; refusing to run dead-Redis acceptance fact" message.
**Warning signs:** `HealthDeadRedisFixture` test always green even on local dev machines that have a host-side Redis daemon running on 6379.
**Source:** Author analysis of D-13 + D-01 interaction.

### Pitfall 4: Phase8WebAppFactory chained Init/Dispose order breaks under exception in InitializeAsync
**What goes wrong:** `_fixture.InitializeAsync()` succeeds, `_redisFixture.InitializeAsync()` throws — `DisposeAsync` is called (if caller uses `await using`), but `_fixture` is non-null while `_redisFixture` is non-null-but-half-initialized. Leaked Postgres DB.
**Why it happens:** xUnit v3 calls DisposeAsync on InitializeAsync-throw only with `await using`; a non-`await using` caller leaks.
**How to avoid:** Catch in `InitializeAsync` after `_fixture` init succeeds — if `_redisFixture` init throws, run `_fixture.DisposeAsync()` defensively and rethrow. Mirror `HealthDeadPostgresFixture`'s try/catch-restore pattern at `HealthEndpointsTests.cs:259-268`.
**Warning signs:** Leaked `stepsdb_test_*` databases visible in `psql \l` snapshots BUT no test failure reported (the failed InitializeAsync raised an exception that the suite never observed).
**Source:** Author analysis of D-07 + existing fixture init-order discipline.

### Pitfall 5: `redis-cli ping` healthcheck flaps under Alpine's BusyBox shell
**What goes wrong:** `["CMD-SHELL", "redis-cli ping || exit 1"]` may quote-escape oddly under Alpine BusyBox; `["CMD", "redis-cli", "ping"]` (no shell) is the safe form per CONTEXT INFRA-REDIS-02. Verify via `docker exec sk-redis redis-cli ping` returns `PONG`.
**Why it happens:** Alpine BusyBox `sh` interprets quoting differently from Bash/POSIX `sh`.
**How to avoid:** Use `["CMD", "redis-cli", "ping"]` (CMD form — no shell invocation). The Redis image's PID 1 is `redis-server`, and `redis-cli` is on `PATH` per the official image Dockerfile.
**Warning signs:** Healthcheck reports `unhealthy` after `start_period` window even though `docker exec sk-redis redis-cli ping` works manually from host.
**Source:** Phase 11 compose.yaml shows the same `CMD` (not `CMD-SHELL`) pattern for ES `curl` healthcheck (line 45) — actually uses CMD-SHELL for ES due to wait_for_status query-string parsing; pure ping does NOT need shell.

### Pitfall 6: Phase-close gate `redis-cli --scan` pipes through `sort | sha256sum` — `sort` ordering varies by locale
**What goes wrong:** BEFORE snapshot taken on `LC_ALL=C` shell; AFTER snapshot taken on `LC_ALL=en_US.UTF-8` shell; same key set produces different SHA-256 due to sort-order divergence.
**Why it happens:** GNU `sort` honors locale by default; key bytes that sort identically under C-locale may sort differently under UTF-8 locale.
**How to avoid:** Lock the pipe to `LC_ALL=C redis-cli --scan | LC_ALL=C sort | sha256sum`. Document in the phase-close ritual that the SHA-256 invariant requires the C-locale sort.
**Warning signs:** `psql \l` SHA-256 holds (it uses internal database name list — no sort) but `redis-cli --scan` SHA-256 flaps across runs even when key set is byte-identical.
**Source:** Author analysis of `sort` locale dependence; analogue of the Phase 3 D-15 verification methodology.

### Pitfall 7: `RedisFixture.DisposeAsync` assertion succeeds when SCAN page boundary skips a residual key
**What goes wrong:** Edge case — a test inserts 251 keys, deletes 250, the re-SCAN with `pageSize: 250` returns the residual on a second page that is iterated. Should still be caught, but if the cursor is malformed (rare bug in StackExchange.Redis pre-2.5) the second page is skipped.
**Why it happens:** Historical bug in older StackExchange.Redis versions (resolved in 2.5+); 2.13.1 is well past the fix.
**How to avoid:** Already covered by 2.13.1 pin (D-14). Defensive: enumerate the entire `IAsyncEnumerable<RedisKey>` in DisposeAsync (don't early-break), and prefer `pageSize: 1000` for the second SCAN to reduce page count.
**Warning signs:** `redis-cli --scan | sort | sha256sum` BEFORE ≠ AFTER across the suite, with the diff showing `test:cls-*` residuals despite DisposeAsync asserting zero.
**Source:** StackExchange.Redis changelog (resolved in 2.5.x); current pin 2.13.1 is past the fix.

### Pitfall 8: Connection-string format includes `name=...` (ClientName) override that leaks test class identity
**What goes wrong:** A test PR adds `,name=test-{className}` to the RedisFixture connection string for ops debugging convenience; `redis-cli CLIENT LIST` now exposes test class names to anyone with shell access to the Redis container. Minor info leak; defensive depth matters.
**Why it happens:** ClientName is a useful debugging knob; the STACK.md research mentions it.
**How to avoid:** Resist adding `name=` until there's a real operational driver. `ConfigurationOptions.ClientName` is NOT in the CONTEXT D-locks — Claude's Discretion area but recommended NOT TO add.
**Warning signs:** `CLIENT LIST` against sk-redis shows multiple client names matching test-class patterns.
**Source:** STACK.md "Recommended ASP.NET Core registration" lines 234-247 mentions `opts.ClientName = "sk-api"` — author flags it as an unnecessary surface for v3.3.0.

## Code Examples

### Code Example 1: `AddBaseApiRedis` registration (D-14 / D-16 verbatim)

See **Pattern 1** above — the complete `RedisServiceCollectionExtensions.cs` body. Lifted from ARCHITECTURE.md §1 Code Example, tightened for CONTEXT D-locks.

```csharp
internal static IServiceCollection AddBaseApiRedis(
    this IServiceCollection services, IConfiguration cfg)
{
    var connStr = cfg.RequireConnectionString("Redis");
    services.AddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect(connStr));
    services.Configure<RedisProjectionOptions>(cfg.GetSection("Redis"));
    return services;
}
```

**Source:** [CITED: ARCHITECTURE.md §1 lines 22-39] + [CITED: Context7 `/websites/stackexchange_github_io_stackexchange_redis` — "Connect to Redis" basics] + [VERIFIED: existing `PersistenceServiceCollectionExtensions.cs:17-43` for `internal static class` + `RequireConnectionString` mirror].

### Code Example 2: `RedisProjectionOptions` POCO (D-15 + INFRA-COMP-04 verbatim)

```csharp
// src/BaseApi.Core/Configuration/RedisProjectionOptions.cs
namespace BaseApi.Core.Configuration;

/// <summary>
/// Phase 12 Redis projection options bound to the "Redis:*" config section
/// (INFRA-COMP-04). YAGNI-pruned per D-15: KeyPrefix + Serialization.JsonOptions
/// only. No Database int, no CommandFlags, no ConnectionString property.
/// Database / Cluster / replica knobs can be added in v3.4 when a real scale
/// driver appears. Phase 15 writer reads exactly the fields it needs.
/// </summary>
public sealed class RedisProjectionOptions
{
    /// <summary>
    /// Prefix prepended to all L2 keys (INFRA-REDIS-05 — default "skp:").
    /// Production = "skp:"; tests override per-class to "test:cls-{Guid:N}:"
    /// via Phase8WebAppFactory's AddInMemoryCollection (D-08 / D-12).
    /// </summary>
    public string KeyPrefix { get; set; } = "skp:";

    /// <summary>
    /// Nested serialization options. v3.3.0 ships a single string discriminator
    /// "default"; future revisions may add JsonOptions = "snake_case" etc.
    /// Phase 15 writer wires the actual System.Text.Json options factory.
    /// </summary>
    public SerializationOptions Serialization { get; set; } = new();

    public sealed class SerializationOptions
    {
        public string JsonOptions { get; set; } = "default";
    }
}
```

**Source:** [CITED: REQUIREMENTS.md INFRA-COMP-04] + [CITED: CONTEXT D-15] — minimum surface to bind successfully.

### Code Example 3: `RedisFixture` per-class isolation (D-09/D-10/D-11/D-12 verbatim)

See **Pattern 4** above — complete file body.

**Source:** [VERIFIED: existing `PostgresFixture.cs:22-56`] + [CITED: ARCHITECTURE.md §5 lines 198-216] + [CITED: CONTEXT D-09..D-12].

### Code Example 4: `Phase8WebAppFactory` in-place D-07 extension

See **Pattern 5** above — interior modifications only (~30-LOC diff against existing 134-LOC file).

**Source:** [VERIFIED: existing `Phase8WebAppFactory.cs:26-134`] + [CITED: CONTEXT D-07 + D-08].

### Code Example 5: `HealthDeadRedisFixture` (TEST-REDIS-05 / D-13)

```csharp
// tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs (or moved to its own file)
namespace BaseApi.Tests.Observability;

/// <summary>
/// TEST-REDIS-05 + D-13: variant that proves /health/live AND /health/ready BOTH
/// return 200 when Redis is unreachable (soft-dep contract INFRA-REDIS-06).
/// Dead-Redis port = host's 6379 — guaranteed unbound by D-01 compose mapping
/// (6380:6379 means the host 6379 is never exposed by compose).
/// <para>
/// Uses live Postgres so /health/ready can pass on the Postgres-only ready tag.
/// </para>
/// </summary>
private sealed class HealthDeadRedisFixture : Phase8WebAppFactory
{
    // D-13: 6379 is unbound on host since compose maps 6380:6379.
    private const string DeadRedisConnectionString =
        "localhost:6379,abortConnect=false,connectTimeout=2000";

    public HealthDeadRedisFixture()
        : base(
            skipPostgresFixture: false,                          // live Postgres
            connectionStringOverride: null!,                      // throwaway DB via _fixture
            skipRedisFixture: true,                              // dead Redis
            redisConnectionStringOverride: DeadRedisConnectionString)
    { }
}

[Fact]
public async Task Test_HealthLive_200_When_Redis_Unreachable()  // INFRA-REDIS-06
{
    var ct = TestContext.Current.CancellationToken;
    await using var factory = new HealthDeadRedisFixture();
    await factory.InitializeAsync();
    using var client = factory.CreateClient();

    var response = await client.GetAsync("/health/live", ct);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}

[Fact]
public async Task Test_HealthReady_200_When_Redis_Unreachable()  // INFRA-REDIS-06
{
    var ct = TestContext.Current.CancellationToken;
    await using var factory = new HealthDeadRedisFixture();
    await factory.InitializeAsync();
    using var client = factory.CreateClient();

    var response = await client.GetAsync("/health/ready", ct);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}
```

**Source:** [VERIFIED: existing `HealthDeadPostgresFixture` at `HealthEndpointsTests.cs:226-281`] + [CITED: CONTEXT D-13 + D-06] + [CITED: REQUIREMENTS.md TEST-REDIS-05].

### Code Example 6: Phase-close gate shell ritual (TEST-REDIS-04)

```powershell
# PowerShell — invoked at phase close (Plan 12-NN final task)
# BEFORE the full integration suite runs
$beforeRedis = (docker exec sk-redis redis-cli --scan | Sort-Object | Out-String).Trim()
$beforeRedisHash = [BitConverter]::ToString(
    [Security.Cryptography.SHA256]::Create().ComputeHash(
        [Text.Encoding]::UTF8.GetBytes($beforeRedis))).Replace("-", "").ToLower()

# Existing Phase 3 D-15 invariant — Postgres
$beforePg = (docker exec sk-postgres psql -U postgres -lqt | Out-String).Trim()
$beforePgHash = [BitConverter]::ToString(
    [Security.Cryptography.SHA256]::Create().ComputeHash(
        [Text.Encoding]::UTF8.GetBytes($beforePg))).Replace("-", "").ToLower()

dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj

# AFTER
$afterRedis = (docker exec sk-redis redis-cli --scan | Sort-Object | Out-String).Trim()
$afterRedisHash = ...

# Assert both invariants
if ($beforeRedisHash -ne $afterRedisHash) { throw "TEST-REDIS-04 violated" }
if ($beforePgHash -ne $afterPgHash)       { throw "Phase 3 D-15 violated" }
```

```bash
# Bash equivalent (locale-locked per Pitfall 6)
beforeRedis=$(docker exec sk-redis redis-cli --scan | LC_ALL=C sort | sha256sum)
beforePg=$(docker exec sk-postgres psql -U postgres -lqt | sha256sum)

dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj

afterRedis=$(docker exec sk-redis redis-cli --scan | LC_ALL=C sort | sha256sum)
afterPg=$(docker exec sk-postgres psql -U postgres -lqt | sha256sum)

[ "$beforeRedis" = "$afterRedis" ] || { echo "TEST-REDIS-04 violated"; exit 1; }
[ "$beforePg"    = "$afterPg"    ] || { echo "Phase 3 D-15 violated"; exit 1; }
```

**Source:** [VERIFIED: existing Phase 3 D-15 / Plan 03-02 / Plan 08-08 ritual that produced `0d98b0de…0aac127` SHA-256] + [CITED: CONTEXT TEST-REDIS-04 verbatim] + author note on locale (Pitfall 6).

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `ServiceStack.Redis` | `StackExchange.Redis` | 2014 onward | Commercial-license quota on ServiceStack.Redis vs MIT on StackExchange.Redis; StackExchange.Redis is the de facto standard. |
| `BookSleeve` (predecessor) | `StackExchange.Redis` | 2014 (abandoned) | BookSleeve marked obsolete by its own author Marc Gravell — superseded by StackExchange.Redis. |
| Redis 8.0+ tri-license (incl. AGPLv3) | **Pin to Redis 7.4.x line (RSALv2/SSPLv1 only)** | 2025-05 | AGPLv3 introduces network-distribution copyleft; CONTEXT INFRA-REDIS-01 + STACK.md pin 7.4.x deliberately. Valkey BSD fork is a future fallback if Redis licensing escalates. |
| Recursive DFS for graph traversal | Iterative DFS (Phase 14 concern) | Phase 14 (out of Phase 12 scope) | StackOverflowException is uncatchable; flagged in PITFALLS P8 — not Phase 12 work. |
| `FLUSHDB` / DB-index isolation for Redis tests | Per-class GUID KeyPrefix + SCAN+DEL+assert | CONTEXT D-10 / D-12 | DB indices deprecated in Cluster; FLUSHDB destroys parallel-test state. |

**Deprecated / outdated (do NOT use in Phase 12):**
- `KEYS pattern` synchronous command (use `IServer.KeysAsync` which is SCAN-backed)
- `FLUSHDB` / `FLUSHALL` in test cleanup
- `SELECT n` DB-index for test isolation (Cluster-incompatible; per CONTEXT D-locks irrelevant — sk_p uses single-node Redis)
- `ConnectionMultiplexer.Connect(string)` raw overload without `abortConnect=false` (D-04 lock)

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `redis-cli` ships in `/usr/local/bin` on the `redis:7.4.x-alpine` image (so `["CMD", "redis-cli", "ping"]` finds it without shell `PATH` resolution) | Pattern 3 (Compose Service Block) | Healthcheck reports `unhealthy` indefinitely; planner verifies via `docker run --rm redis:7.4-alpine which redis-cli` before merging compose change. **Mitigation:** Author confirmed shell-side `docker run --rm redis:7.4-alpine which redis-cli` returns `/usr/local/bin/redis-cli` — verified at research time but listed here for planner re-verification at the patch-pin step (since 7.4.9 vs 7.4.x is fluid). [ASSUMED] |
| A2 | StackExchange.Redis 2.13.1's `server.KeysAsync(pattern, pageSize)` correctly enumerates the full keyspace under SCAN semantics with prefix matching | Code Example 3 (RedisFixture.DisposeAsync) | If a known bug in 2.13.x KeysAsync drops keys on page boundaries, the assert-count==0 in DisposeAsync would pass while leaving keys, breaking TEST-REDIS-04 silently. Author searched StackExchange.Redis changelog 2.13.x — no known bug. [ASSUMED] |
| A3 | The host port `6379` will remain unbound at all integration-test runtime environments (CI + local dev) given D-01's `6380:6379` mapping | Pattern 3 + HealthDeadRedisFixture (Code Example 5) | A developer running a host-side Redis daemon on default `6379` would make the dead-Redis fixture LIVE; HealthDeadRedisFixture would silently pass for the wrong reason. **Mitigation:** Pitfall 3 explicitly recommends adding a pre-fixture TCP-connect probe to assert `6379` is `ConnectionRefused`. [ASSUMED — mitigated in Pitfall 3] |
| A4 | The current `IConfiguration` snapshot captured by `AddBaseApiRedis(cfg)` reads `ConnectionStrings:Redis` AFTER `Phase8WebAppFactory.ConfigureWebHost`'s AddInMemoryCollection has been applied | D-08 + Pattern 5 | If WebApplicationFactory captures the connection string by VALUE before AddInMemoryCollection runs (the same gotcha that bit Plan 05-02 Pattern C for `AddNpgSql`), tests would resolve the appsettings.json `redis:6379` instead of `localhost:6380`, contacting the wrong endpoint. CONTEXT D-08 verbatim: *"The `Environment.SetEnvironmentVariable` workaround from Plan 05-02 (Postgres) does NOT apply here because `AddBaseApiRedis` reads via `IConfiguration` at DI registration time, not by value capture."* — author verified by reading the Singleton factory closure shape in D-14: `services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(cfg.GetConnectionString("Redis")))`. The closure captures `cfg` by reference; `cfg.GetConnectionString("Redis")` is evaluated at first IConnectionMultiplexer resolution (lazy per D-17), which is AFTER ConfigureWebHost runs. **HIGH confidence — but worth a smoke fact at Phase 12 close.** [ASSUMED — confidence boosted by closure-evaluation timing analysis] |
| A5 | The Phase 5 HEALTH-01..05 acceptance facts pass byte-identically when Phase 12 changes are merged | Success Criterion #3 | If `AddBaseApiRedis` chained into the composition root inadvertently mutates the `IConfiguration` snapshot used by `AddBaseApiHealth` (e.g., via a side effect in `cfg.GetSection("Redis")` that EF rebinding might trip), HEALTH-01..05 could regress without an obvious cause. **Mitigation:** Regression run of the 142/142 v3.2.0 baseline test suite is part of the phase-close gate; `services.Configure<>(cfg.GetSection("Redis"))` does not mutate `cfg` — only registers an `IConfigureOptions<T>` factory. [ASSUMED — confidence is HIGH; flagged for transparency only] |
| A6 | StackExchange.Redis 2.13.1 is binary-compatible with .NET 8.0.27 runtime (no target-framework conflicts) | Standard Stack table | If StackExchange.Redis 2.13.1's nuspec declares a min .NET version > 8.0 or requires a transitive package conflict with v3.2.0 pins (Npgsql 8.0.9, EF Core 8.0.27, OTel 1.15.3), `dotnet restore` would fail. NuGet verifies StackExchange.Redis targets `netstandard2.0` (works on .NET 8 transparently per STACK.md). [VERIFIED: STACK.md "Version Compatibility" table line 371] |

**If this table is empty:** N/A — six assumptions surfaced. Five are MEDIUM-LOW risk (A1, A2, A3, A4, A5) with explicit mitigations; A6 is verified. The planner should add a Wave-0 verification step for A1 (docker run `which redis-cli`) and a Wave-final smoke fact for A4 (assert `IConnectionMultiplexer` resolves with the AddInMemoryCollection-overridden connection string in `Phase8WebAppFactory`).

## Open Questions

1. **Exact 7.4.x patch pin: 7.4.9 vs `7.4-alpine` (floating)?**
   - What we know: CONTEXT D-01 / INFRA-REDIS-01 lock "7.4.x-alpine" as a placeholder; current latest is 7.4.9 (Docker Hub 2026-05-29).
   - What's unclear: Whether to pin the exact patch `7.4.9-alpine` or use the floating `7.4-alpine` tag.
   - Recommendation: Pin exact patch `7.4.9-alpine` for v3.2.0 discipline consistency (Postgres 17-alpine, ES 8.15.5, Prom v3.11.3, OTel-Collector 0.152.0 — all exact). Record SHA digest in compose comment for true immutability if desired. Planner-discretion item.

2. **Wave/commit decomposition cadence — Phase 11 fan-out (10 commits) vs Phase 10 (5 commits)?**
   - What we know: CONTEXT plan-decomposition hint at lines 156-166 suggests 7 commits (docs / chore / feat(infra) / feat(core) / test(infra) / test(health) / test(close)); CONTEXT "Claude's Discretion" line 74 explicitly leaves this open.
   - What's unclear: The user has not picked between the two cadences.
   - Recommendation: Phase 12 spans 3 layers (compose, DI, tests) which aligns more naturally with the Phase 11 fan-out (10 commits). Suggested decomposition: (1) docs REQUIREMENTS amendment (no-op if already locked); (2) chore CPM pins + csproj edits; (3) feat compose.yaml redis service + baseapi env; (4) feat appsettings split; (5) feat RedisProjectionOptions + AddBaseApiRedis; (6) feat composition-root call #7; (7) test RedisFixture; (8) test Phase8WebAppFactory in-place extension; (9) test HealthDeadRedisFixture + 2 facts; (10) close phase-close gate extension + 3-GREEN cadence harness. Planner picks final cadence based on plan-decomposition mechanics.

3. **`private const string SectionName = "Redis"` helper in `RedisServiceCollectionExtensions.cs` or inline string literal twice?**
   - What we know: CONTEXT "Claude's Discretion" line 72 leaves open.
   - What's unclear: Which the planner prefers stylistically.
   - Recommendation: Inline twice. Two uses (`RequireConnectionString("Redis")` and `GetSection("Redis")`) is below the "rule of three" threshold for extracting a named constant; symmetrical with `AddBaseApiHealth` (inlines `"Postgres"` once at `HealthServiceCollectionExtensions.cs:26`) and `AddBaseApiPersistence` (inlines `"Postgres"` once at `PersistenceServiceCollectionExtensions.cs:31`).

4. **Should `Phase8WebAppFactory`'s D-07 extension expose `RedisFixture.Multiplexer` directly, or only the connection string?**
   - What we know: TEST-REDIS-06..09 (Phase 16) will need direct multiplexer access for 3-keyspace assertion. Phase 12 only needs connection string + KeyPrefix via AddInMemoryCollection.
   - What's unclear: Whether to expose now (future-headroom) or later.
   - Recommendation: Expose now via a `public IConnectionMultiplexer RedisMultiplexer => _redisFixture?.Multiplexer ?? throw ...` property. Phase 16 facts will need it; surface cost is one property; zero risk to the soft-dep contract. Mirrors how `ConnectionString` is already exposed on the existing factory.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Docker daemon | Compose stack incl. new redis service | ✓ (verified in compose.yaml mtimes) | Docker Compose v2 | — |
| .NET SDK 8.0.421 | Build + test | ✓ (pinned in global.json + verified in Plan 01-03) | 8.0.421 | — |
| Postgres 17-alpine (compose) | Phase8WebAppFactory live Postgres | ✓ (Phase 2) | 17-alpine | — |
| Redis 7.4.x-alpine image | New compose service | ✓ pullable | 7.4.9-alpine current | If Docker Hub blocks 7.4.x pull, fallback to Valkey BSD fork (RESP-compatible — `StackExchange.Redis` connects natively per STACK.md Alternatives row 13). |
| `redis-cli` in alpine image | Compose healthcheck `["CMD", "redis-cli", "ping"]` | ✓ (ships in image at /usr/local/bin/redis-cli) | bundled with redis-server | — |
| `redis-cli` on HOST (CI / dev workstation) | Phase-close gate `redis-cli --scan` ritual | ⚠ MAY require install | host-bin | Use `docker exec sk-redis redis-cli --scan` instead (no host install required — works in both PowerShell and Bash shells). **Recommended.** Phase-close shell ritual in Code Example 6 already uses `docker exec`. |
| StackExchange.Redis 2.13.1 NuGet | csproj PackageReference | ✓ (NuGet API verified 2026-05-29) | 2.13.1 | — |
| OpenTelemetry.Instrumentation.StackExchangeRedis | **EXPLICITLY FORBIDDEN** | n/a | n/a | n/a |

**Missing dependencies with no fallback:** None.

**Missing dependencies with fallback:** Host-side `redis-cli` for the phase-close gate is the only "soft missing" — `docker exec sk-redis redis-cli --scan` is the recommended portable alternative and is what Code Example 6 uses.

## Validation Architecture

`nyquist_validation` is `true` in `.planning/config.json`. This section is REQUIRED.

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xUnit v3 3.2.2 + xunit.v3.assert + xunit.runner.visualstudio 3.1.5 |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (MTP-mode: `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` + `<OutputType>Exe</OutputType>` + `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>`) |
| Quick run command (single-fact) | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Phase12"` |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| Existing test count baseline | 142 facts GREEN × 3 consecutive runs at v3.2.0 close (Phase 11 close) |

### Phase Requirements → Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|--------------|
| INFRA-REDIS-01 | `docker compose ps` shows `sk-redis` healthy with image `redis:7.4.9-alpine` (or current 7.4.x patch) | Smoke / Wave-0 manual + automated | `docker compose ps redis --format json \| jq '.Health'` → `"healthy"`; also `docker inspect sk-redis --format '{{.Config.Image}}'` → `"redis:7.4.9-alpine"` | ❌ Wave 0 (shell ritual + xUnit smoke fact `ComposeRedisHealthyFact`) |
| INFRA-REDIS-02 | Healthcheck = `redis-cli ping`, interval 5s, retries 10, start_period 5s; baseapi-service depends_on redis service_healthy | Compose YAML smoke fact | xUnit fact `ComposeYamlFacts.RedisHealthcheckShapeMatches` parses compose.yaml YAML and asserts the keys | ❌ Wave 0 (new fact in `tests/BaseApi.Tests/Composition/` or extend existing compose-snapshot test) |
| INFRA-REDIS-03 | `StackExchange.Redis` pinned at 2.13.1 in Directory.Packages.props + referenced from BaseApi.Service.csproj | Build verification | `dotnet list src/BaseApi.Service/BaseApi.Service.csproj package \| grep "StackExchange.Redis 2.13.1"` | ❌ Wave 0 (`dotnet list package` shell assert OR xUnit fact reading the csproj) |
| INFRA-REDIS-04 | `ConnectionStrings:Redis` contains `abortConnect=false,connectTimeout=5000` in BOTH appsettings.json (with `redis:6379` host) AND appsettings.Development.json (with `localhost:6380` host) | Build verification | xUnit fact `AppsettingsFacts.RedisConnStringHasAbortConnectFalse` reads both files; regex-assert `abortConnect=false` | ❌ Wave 0 (`tests/BaseApi.Tests/Configuration/AppsettingsFacts.cs` — already exists if Phase 7/8 has one; planner verifies) |
| INFRA-REDIS-05 | Default `RedisProjectionOptions.KeyPrefix == "skp:"`; binding from `cfg.GetSection("Redis")` works end-to-end | Unit + integration | `dotnet test --filter "FullyQualifiedName~RedisProjectionOptionsBindingFacts"` — instantiate `IServiceCollection`, call `AddBaseApiRedis(cfg)`, resolve `IOptions<RedisProjectionOptions>`, assert `KeyPrefix` value | ❌ Wave 0 (`tests/BaseApi.Tests/Composition/RedisProjectionOptionsBindingFacts.cs`) |
| INFRA-REDIS-06 | Redis down ⇒ both `/health/live` AND `/health/ready` return 200 (soft-dep) | Integration | `dotnet test --filter "FullyQualifiedName~HealthDeadRedis"` — `HealthDeadRedisFixture` + 2 facts | ❌ Wave 0 (Code Example 5) |
| INFRA-COMP-01 | `AddBaseApiRedis` chained as call #7 in `AddBaseApi<TDbContext>` after `AddBaseApiMapping` | Composition verification | xUnit fact `BaseApiCompositionFacts.AddBaseApiChainsAddBaseApiRedisAsCallSeven` resolves the chain by re-running `AddBaseApi<TestDbContext>` against a fresh `ServiceCollection`, then asserts `IConnectionMultiplexer` descriptor exists with `Singleton` lifetime | ❌ Wave 0 (extend existing Phase 7 `BaseApiCompositionFacts` if present; else new file) |
| INFRA-COMP-02 | `IConnectionMultiplexer` lifetime = Singleton | DI descriptor assert | Same fact as above — `Assert.Equal(ServiceLifetime.Singleton, services.GetServiceDescriptor(typeof(IConnectionMultiplexer)).Lifetime)`; PLUS reference-equality fact resolving from root + scope and `Assert.Same(rootMux, scopedMux)` | ❌ Wave 0 |
| INFRA-COMP-03 | `IDatabase` NOT registered as DI service; consumers resolve via `_multiplexer.GetDatabase()` | DI descriptor negative assert | `Assert.Null(services.FirstOrDefault(d => d.ServiceType == typeof(IDatabase)))` | ❌ Wave 0 |
| INFRA-COMP-04 | `RedisProjectionOptions` bound to `Redis:*` section via `services.Configure<>` | DI binding assert | Resolve `IOptions<RedisProjectionOptions>`, assert `.Value.KeyPrefix` reflects an injected override (e.g., `"test:override:"`) | ❌ Wave 0 |
| TEST-REDIS-01 | `RedisFixture.KeyPrefix` matches `^test:cls-[0-9a-f]{32}:$` regex; per-fixture-instance Guid | Unit | `dotnet test --filter "FullyQualifiedName~RedisFixtureFacts.KeyPrefixIsGuidPerInstance"` | ❌ Wave 0 (`tests/BaseApi.Tests/Composition/RedisFixtureFacts.cs`) |
| TEST-REDIS-02 | `Phase8WebAppFactory` boots with RedisFixture; `IConfiguration` snapshot in the running webhost shows `ConnectionStrings:Redis` = `localhost:6380...` | Integration | `dotnet test --filter "FullyQualifiedName~RedisFixturePhase8FactoryIntegration"` — boot factory, resolve `IConfiguration`, assert connection string | ❌ Wave 0 |
| TEST-REDIS-03 | `RedisFixture.DisposeAsync` SCAN-asserts zero residual keys; throws on violation; FLUSHDB never called | Unit + cleanup discipline | `dotnet test --filter "FullyQualifiedName~RedisFixtureDisposalFacts"` — seed keys with matching prefix, await DisposeAsync, assert no throw; then second fact seeds keys with NON-matching prefix (must remain after Dispose); third fact verifies the throw-on-residual path by injecting a key AFTER SCAN+DEL but BEFORE re-SCAN | ❌ Wave 0 |
| TEST-REDIS-04 | `redis-cli --scan \| sort \| sha256sum` BEFORE=AFTER across full integration suite | Phase-close shell ritual | `pwsh -File scripts/phase-12-close.ps1` (or `bash scripts/phase-12-close.sh`) running the Code Example 6 ritual | ❌ Wave 0 (new shell script under `scripts/` or inline in Plan 12-N final task) |
| TEST-REDIS-05 | `HealthDeadRedisFixture` proves `/health/live` AND `/health/ready` both return 200 with Redis down | Integration | Same as INFRA-REDIS-06 — `HealthDeadRedis*` facts in `HealthEndpointsTests.cs` | ❌ Wave 0 (Code Example 5) |

### Sampling Rate

- **Per task commit:** `dotnet test --filter "FullyQualifiedName~Phase12" --no-build` (~5-10s for the new Phase 12 facts in isolation)
- **Per wave merge:** Full integration suite — `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (current 142 facts + new Phase 12 facts; expected ~30-60s based on Phase 11 close cadence of 163s for 142 facts — Phase 12 adds ~10-20 facts → ~5-10% suite-time increase if any).
- **Phase gate:** Three consecutive GREEN runs of full integration suite (Phase 3 D-18 cadence) + `psql \l` SHA-256 BEFORE=AFTER + `redis-cli --scan \| sort \| sha256sum` BEFORE=AFTER (TEST-REDIS-04).

### Wave 0 Gaps

- [ ] `tests/BaseApi.Tests/Composition/RedisFixture.cs` — NEW (Code Example 3) — covers TEST-REDIS-01, TEST-REDIS-03
- [ ] `tests/BaseApi.Tests/Composition/RedisFixtureFacts.cs` — NEW — unit facts for KeyPrefix uniqueness + DisposeAsync residual assertion
- [ ] `tests/BaseApi.Tests/Composition/RedisProjectionOptionsBindingFacts.cs` — NEW — covers INFRA-REDIS-05, INFRA-COMP-04
- [ ] `tests/BaseApi.Tests/Composition/BaseApiCompositionFacts.cs` — NEW or EXTEND — covers INFRA-COMP-01, INFRA-COMP-02, INFRA-COMP-03 (chain order, Singleton lifetime, IDatabase-not-registered)
- [ ] `tests/BaseApi.Tests/Configuration/AppsettingsFacts.cs` — NEW or EXTEND — covers INFRA-REDIS-04 (regex match `abortConnect=false` in both appsettings files)
- [ ] `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` — NEW or EXTEND — covers INFRA-REDIS-01, INFRA-REDIS-02 (parse compose.yaml, assert sk-redis service + healthcheck + depends_on)
- [ ] Extend `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — add `HealthDeadRedisFixture` (Code Example 5) + 2 new facts — covers INFRA-REDIS-06, TEST-REDIS-05
- [ ] Extend `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` — Pattern 5 in-place modifications — covers TEST-REDIS-02
- [ ] `scripts/phase-12-close.{ps1,sh}` — NEW — phase-close gate ritual per Code Example 6 — covers TEST-REDIS-04
- [ ] Framework install: NONE — xUnit v3 + StackExchange.Redis 2.13.1 are all that's needed; CPM-pinned

Wave 0 builds the test scaffolding. Wave 1+ wires the source code (RedisServiceCollectionExtensions, composition-root call #7, RedisProjectionOptions, compose.yaml redis service, appsettings split). Wave-final runs the 3-GREEN cadence + phase-close ritual.

## Security Domain

`security_enforcement` is not explicitly set in `.planning/config.json` — treated as **enabled** per RESEARCH.md template default ("absent = enabled").

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|------------------|
| V2 Authentication | no | `/api/v1/orchestration/{start,stop}` is unauthenticated per v3.2.0 Out of Scope (auth boundary TBD project-wide). Redis itself runs no AUTH in compose (D-04 plaintext localhost). Phase 12 does NOT change this. |
| V3 Session Management | no | Stateless API; no sessions. |
| V4 Access Control | no | No authorization rules added in Phase 12. |
| V5 Input Validation | partial | INFRA-COMP-04 binds `RedisProjectionOptions` via `services.Configure<>` — Phase 12 introduces no user-input surface. Phase 14 adds Payload↔ConfigSchema validation. |
| V6 Cryptography | no | No crypto in Phase 12; TLS to Redis is out of scope for local compose (PITFALLS P3 documents TLS for non-localhost; deferred until production deploy). |
| V7 Error Handling & Logging | yes | RFC 7807 + correlationId on Redis-side 500s (deferred to Phase 15 / ORCH-START-04 / ORCH-STOP-07; Phase 12 only registers the multiplexer). PITFALLS P15 calls out correlation-id propagation through Redis async ops — Phase 12 sets the IConnectionMultiplexer Singleton; Phase 15 wires the actual ops + log scopes. |
| V8 Data Protection | partial | L2 keys carry `correlationId` + workflow IDs (Phase 15 concern). Phase 12 only registers the multiplexer; no data-flow added. |
| V9 Communication | no | Local compose Redis is plaintext; TLS deferred (PITFALLS P3). |
| V12 Files & Resources | no | No file operations in Phase 12. |
| V13 API & Web Service | partial | New `/api/v1/orchestration/{start,stop}` 500 + RFC 7807 surface added in Phase 15. Phase 12 does NOT touch the controller. |

### Known Threat Patterns for .NET 8 + StackExchange.Redis

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Redis traffic intercepted on the wire | Information Disclosure | TLS via `,ssl=true` in connection string (production deploy concern — NOT v3.3.0 for local compose; PITFALLS P3 documents). |
| Connection-string credential leak via `/health/ready` body | Information Disclosure | D-06 — no Redis check on `/health/ready`; INFRA-REDIS-06 — body unchanged. Phase 5 T-05-READY-DB-EXPOSE acceptance test already asserts no `Password=` substring in ready body — Phase 12 inherits. |
| `RedisConnectionException` message exposed in error response with internal hostname | Information Disclosure | RFC 7807 mapping in Phase 15 redacts internal Redis hostnames; Phase 4 PostgresExceptionMapper precedent. Phase 12 does NOT add any error-mapping surface — the multiplexer's exceptions fire at consumer call sites (Phase 15). |
| Singleton multiplexer races on `Configuration.Parse` during DI bootstrap | Tampering / DoS | Use `ConnectionMultiplexer.Connect(connStr)` raw-string overload (D-14) — single-threaded inside the Singleton factory closure. |
| Connection-string with `allowAdmin=true` enables FLUSHDB / CONFIG operations from app code | Elevation of Privilege | D-04 connection string does NOT include `allowAdmin=true`. Production code MUST NOT add this flag without explicit decision. PITFALLS P32 hints at this in passing. |
| Test fixture `FLUSHDB` destroys parallel-class data | Tampering (test integrity) | TEST-REDIS-03 / D-10 — FLUSHDB FORBIDDEN; SCAN+DEL+assert-zero only. |
| Trace data exfiltration via OTel Redis instrumentation | Information Disclosure | OBSERV-REDIS-01 — `OpenTelemetry.Instrumentation.StackExchangeRedis` FORBIDDEN in v3.3.0. Build-guard fact in `NoTracesBackendFacts` (Phase 11) extended to assert no StackExchange.Redis OTel package reference. |

**Phase 12 security posture summary:** This is an infrastructure-landing phase with NO user-input surface and NO data-flow changes. The single security-relevant artifact is the Singleton `IConnectionMultiplexer` registration; the locked CONTEXT decisions (D-04 connection string, D-06 no health check, D-14 Singleton, OBSERV-REDIS-01 no OTel instrumentation) collectively prevent the canonical Redis security pitfalls (connection-string leak via health check, FLUSHDB anti-pattern, trace exfiltration, allowAdmin elevation). All deferred security concerns (TLS, auth) are documented as out-of-scope per PROJECT.md.

## Sources

### Primary (HIGH confidence)

- **Context7 `/websites/stackexchange_github_io_stackexchange_redis`** (resolved 2026-05-29 via `mcp__context7__resolve-library-id` fallback `npx ctx7@latest`) — verified canonical patterns for:
  - ConnectionMultiplexer Singleton: *"is intended to be shared and reused throughout an application rather than created per operation. It is fully thread-safe."* — Basics page
  - IDatabase per-call: *"The returned object is a lightweight pass-thru and does not need to be stored."* — Basics page
  - Configuration options: parse syntax, `ReconnectRetryPolicy`, events — Configuration page
- **NuGet flat-container API** (queried 2026-05-29) — confirmed `StackExchange.Redis` versions extant: `..., 2.13.1, 2.13.10, 2.13.17, 3.0.2-preview, ...`. CONTEXT pins 2.13.1.
- **Docker Hub `library/redis` tag listing** (queried 2026-05-29) — confirmed `7.4.9-alpine` current; `7.4` line extant.
- **Existing v3.2.0 source files** (verified line-by-line at research time):
  - `src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs` lines 24-33 — 6-call chain to extend
  - `src/BaseApi.Core/DependencyInjection/PersistenceServiceCollectionExtensions.cs` lines 17-43 — template for `AddBaseApiRedis`
  - `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` lines 17-30 — UNCHANGED per D-06
  - `src/BaseApi.Core/Health/StartupCompletionService.cs` lines 54-72 — UNCHANGED per D-05
  - `src/BaseApi.Core/Configuration/RequiredConfig.cs` lines 23-31 — `RequireConnectionString` fail-fast helper
  - `src/BaseApi.Service/Program.cs` lines 1-18 — composition root (no change beyond chained AddBaseApiRedis transparent extension)
  - `src/BaseApi.Service/appsettings.json` + `appsettings.Development.json` — Phase 2 D-02/D-07 host-vs-Docker split target
  - `src/BaseApi.Service/BaseApi.Service.csproj` — PackageReference target
  - `src/BaseApi.Service/Composition/AppFeatures.cs` — UNCHANGED (Phase 13+ orchestration concern)
  - `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` — UNCHANGED (Phase 13+ concern)
  - `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` lines 26-134 — extension target
  - `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` lines 22-56 — template for `RedisFixture`
  - `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` lines 226-281 — template for `HealthDeadRedisFixture`
  - `tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` lines 47-132 — env-var capture/restore pattern (referenced; not directly applied per D-08)
  - `Directory.Packages.props` lines 1-120 — CPM pinning conventions
  - `compose.yaml` lines 1-145 — Phase 11 `sk-elasticsearch` / `sk-prometheus` healthcheck templates
- **`.planning/research/`** — internal authoritative research:
  - SUMMARY.md — Top 10 pitfalls + 4 cross-file contradictions (3 already resolved by REQUIREMENTS.md / CONTEXT D-locks)
  - STACK.md — StackExchange.Redis 2.13.1 + Redis 7.4.x-alpine licensing + connection-string discipline
  - ARCHITECTURE.md §1 (AddBaseApiRedis chain) + §3 (health-check posture, overridden by CONTEXT D-06) + §5 (test isolation) + §7 (folder layout)
  - PITFALLS.md P1 (Singleton) + P2 (abortConnect) + P3 (TLS — out of v3.3.0 scope) + P4 (MULTI/EXEC — Phase 15 concern) + P13 (OTel instrumentation — FORBIDDEN) + P14 (health-check tag discipline — overridden by D-06) + P15 (X-Correlation-Id — Phase 15 concern) + P16 (FLUSHDB — FORBIDDEN) + P31 (StartupCompletionService — OVERRIDDEN by D-05) + P32 (host port — overridden by D-01 6380:6379)
- **`.planning/REQUIREMENTS.md`** §INFRA-REDIS-01..06, §INFRA-COMP-01..04, §TEST-REDIS-01..05, §OBSERV-REDIS-01 — locked requirements
- **`.planning/STATE.md`** — 45-plan history surfacing CPM discipline + 3-GREEN cadence + `0d98b0de…0aac127` SHA-256 invariant

### Secondary (MEDIUM confidence)

- Author analysis of:
  - Closure-evaluation timing of `services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(cfg.GetConnectionString("Redis")))` vs AddInMemoryCollection injection order (Assumption A4 — confidence boosted to HIGH-MEDIUM)
  - Alpine BusyBox `sh` vs POSIX `sh` quoting interaction with `CMD-SHELL` vs `CMD` healthcheck forms (Pitfall 5)
  - `sort` locale-dependence on the `redis-cli --scan | sort | sha256sum` pipe (Pitfall 6)
  - D-13 + D-01 interaction risk if a developer runs host-side Redis on 6379 (Pitfall 3)
- ARCHITECTURE.md §3 — verified against existing `HealthServiceCollectionExtensions.cs` shape; recommendation OVERRIDDEN by CONTEXT D-06 (soft dependency)

### Tertiary (LOW confidence — flagged for validation)

- 7.4.x patch level — STACK.md mentions `7.4.2-alpine` but Docker Hub on 2026-05-29 shows `7.4.9-alpine` as current. CONTEXT pins "7.4.x" placeholder; planner should re-check at compose-edit time.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — StackExchange.Redis 2.13.1 NuGet-verified; Redis 7.4.9-alpine Docker-Hub-verified; CPM patterns match existing 22 pins exactly.
- Architecture: HIGH — `AddBaseApiRedis` shape mirrors `AddBaseApiPersistence` line-for-line; composition-root insertion verified against existing 6-call chain; `RedisFixture` mirrors `PostgresFixture` IAsyncLifetime topology with documented divergence (D-09 per-fixture multiplexer vs PostgresFixture's per-class throwaway DB — different but analogous).
- Pitfalls: HIGH — 8 Phase-12-specific pitfalls drawn from PITFALLS.md P1/P2/P13/P14/P15/P16 (all v3.2.0-baseline-related) + 2 author-discovered (Pitfall 3: D-13 + D-01 interaction; Pitfall 6: `sort` locale dependence). Each has a concrete prevention strategy and warning signs.
- Test architecture: HIGH — Wave-0 gap list is complete; phase-close ritual matches Phase 3 D-15 + Phase 11 close cadence; 14 of 15 phase REQ-IDs have direct test mapping (INFRA-REDIS-01 is the one shell-ritual smoke that the planner may choose to automate via a YAML-parse fact OR keep as a CLI assertion).
- Soft-dep contract (D-05 / D-06): HIGH — verified against `StartupCompletionService.cs:54-72` and `HealthServiceCollectionExtensions.cs:17-30`; both files remain UNCHANGED in Phase 12; `HealthDeadRedisFixture` (Code Example 5) proves both `/live` and `/ready` return 200 with Redis down.
- 7.4.x patch pin: MEDIUM — STACK.md says 7.4.2 (stale by ~1 day); Docker Hub current is 7.4.9. Planner picks at edit time.

**Research date:** 2026-05-29
**Valid until:** 2026-06-29 (30 days for stable Phase 12 infrastructure decisions; StackExchange.Redis 2.13.1 pinned; Redis 7.4.x-alpine licensing decision will not change; Docker Hub may publish 7.4.10+ patches — re-verify the exact tag at compose-edit time).
