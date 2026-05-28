# Phase 12: Redis infra + composition + healthcheck + DI registration - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-29
**Phase:** 12-redis-infra-composition-healthcheck-di-registration
**Areas discussed:** Compose posture (port + container_name + persistence), StartupCompletionService Redis ping, RedisFixture connection lifetime, RedisProjectionOptions surface + AddBaseApiRedis form

---

## Compose posture (port + container_name + persistence)

### Redis compose host port mapping

| Option | Description | Selected |
|--------|-------------|----------|
| 6380:6379 | Mirrors Phase 2 D-01 Postgres 5433:5432 collision-avoidance; auto-locks HealthDeadRedisFixture dead-port to 6379 | ✓ |
| 6379:6379 | Research SUMMARY default and StackExchange.Redis quickstart; requires inventing a separate dead port for HealthDeadRedisFixture | |

**User's choice:** 6380:6379 (Recommended)
**Notes:** Locks the HealthDeadRedisFixture dead-port choice (D-13). Defends against developer's local Redis on 6379.

### Container name posture for the redis service

| Option | Description | Selected |
|--------|-------------|----------|
| container_name: sk-redis | Mirrors Phase 11 D-10/D-13 sk-elasticsearch / sk-prometheus mutually-exclusive-with-sk2_1 | ✓ |
| No container_name | Mirrors Postgres service in compose.yaml; allows sk_p + sk2_1 to run simultaneously (no current dev workflow needs that) | |

**User's choice:** container_name: sk-redis (Recommended)
**Notes:** Add inline comment block citing Phase 11 precedent.

### Redis persistence in the compose service

| Option | Description | Selected |
|--------|-------------|----------|
| Explicit disable: --save '' --appendonly no, no volume | Encodes "L2 rebuildable from L3" inline; defends against future operator re-enabling persistence | ✓ |
| Default RDB + named volume | Mirrors Postgres pgdata pattern; introduces stale-key risk across baseapi-service restarts | |
| Default RDB, no volume | Ephemeral; less explicit than option 1 | |

**User's choice:** Explicit disable: --save '' --appendonly no, no volume (Recommended)
**Notes:** Document intent inline so absence is intentional rather than a forgotten gap.

### Connection string posture across appsettings.json / appsettings.Development.json / compose env block

| Option | Description | Selected |
|--------|-------------|----------|
| Mirror Phase 2 D-07: base = Docker-internal, Dev = host | appsettings.json: redis:6379,... ; appsettings.Development.json: localhost:6380,... | ✓ |
| Single env-var-only (no appsettings split) | Both appsettings carry empty placeholders; compose env + Phase8WebAppFactory inject via AddInMemoryCollection | |

**User's choice:** Mirror Phase 2 D-07 (Recommended)
**Notes:** Preserves Plan 02-01 D-02 / D-07 established pattern verbatim.

---

## StartupCompletionService Redis ping

### Should StartupCompletionService probe Redis before MarkReady()?

| Option | Description | Selected |
|--------|-------------|----------|
| No Redis touch at startup | Preserves PERSIST-10 contract verbatim; soft-dep INFRA-REDIS-06 stays clean | ✓ |
| Ping + log-but-don't-fail | One boot-time log line for Redis state; adds ~5ms (reachable) / ~5000ms (timeout) | |
| Ping only in Development env | Env-conditional branching to PERSIST-10 contract | |

**User's choice:** No Redis touch at startup (Recommended)
**Notes:** Explicitly overrides PITFALLS.md P31 because it directly conflicts with locked soft-dep contract. Locked REQUIREMENTS win.

### Should a dedicated /health endpoint surface Redis status?

| Option | Description | Selected |
|--------|-------------|----------|
| No Redis-specific health endpoint | Phase 5 HEALTH-01..05 contracts byte-for-byte identical | ✓ |
| Add /health/redis (untagged from /live, /ready, /startup) | Risks ops auto-wiring it into k8s probe and re-introducing hard-dep posture | |
| Add custom Meter gauge (orchestration.redis.connected) | Future-headroom for deferred OBSERV-REDIS-04 | |

**User's choice:** No Redis-specific health endpoint (Recommended)
**Notes:** TEST-REDIS-05 HealthDeadRedisFixture verifies both /live and /ready stay 200 — that's the net-new acceptance fact.

### Phase8WebAppFactory successor (or rename) for v3.3.0?

| Option | Description | Selected |
|--------|-------------|----------|
| Extend Phase8WebAppFactory in place | Avoids ~30-fact rename churn; class name remains historical-anchor | ✓ |
| Introduce Phase12WebAppFactory subclass | Cleaner phase-attribution; adds one more factory type to choose between | |
| Rename to IntegrationWebAppFactory | Highest long-term clarity; largest churn | |

**User's choice:** Extend Phase8WebAppFactory in place (Recommended)
**Notes:** Mirrors Phase 5 → Phase 8 swap pattern (StartupCompletionService body-swapped, type kept).

### Connection string env-var override pattern in Phase8WebAppFactory

| Option | Description | Selected |
|--------|-------------|----------|
| AddInMemoryCollection | Mirror Plan 05-02 Pattern C verbatim; already proven across 30+ Phase 8 facts | ✓ |
| Skip — defer to Claude's discretion | Mechanical mirroring; planner picks AddInMemoryCollection by reading Phase8WebAppFactory.cs anyway | |
| Environment.SetEnvironmentVariable | Plan 05-02 alt pattern; only needed if Program.cs captures conn-string by value | |

**User's choice:** Lock Option 1: AddInMemoryCollection (Recommended)
**Notes:** User initially uncertain; clarified that AddBaseApiRedis reads via IConfiguration at DI registration time, so the env-var workaround does not apply.

---

## RedisFixture connection lifetime

### RedisFixture IConnectionMultiplexer lifetime

| Option | Description | Selected |
|--------|-------------|----------|
| Per-fixture-instance | Mirror PostgresFixture per-class pattern; ~50 multiplexers across suite, well within Redis maxclients 10000 | ✓ |
| Assembly-shared via [assembly: AssemblyFixture] | Plan 05-02 Pattern E precedent; single TCP conn for whole suite; risk: hung command from one class blocks others | |
| Lazy static singleton | Same TCP-conn footprint as assembly-shared but no AssemblyFixture ceremony | |

**User's choice:** Per-fixture-instance (Recommended)
**Notes:** Mirrors verbatim PostgresFixture pattern. KeyPrefix isolation happens at IDatabase op time.

### RedisFixture.DisposeAsync verification level (TEST-REDIS-03 contract)

| Option | Description | Selected |
|--------|-------------|----------|
| SCAN MATCH prefix* → KeyDeleteAsync → re-SCAN → assert count==0 | Verbatim TEST-REDIS-03; fail-loud Phase 3 D-15 analogue | ✓ |
| SCAN + delete only, no re-SCAN assert | Lower fixture-teardown cost; loses per-class signal | |
| SCAN + UNLINK + re-SCAN assert | UNLINK async background deletion; marginally faster on large key sets | |

**User's choice:** SCAN MATCH prefix* → KeyDeleteAsync → re-SCAN → assert count==0 (Recommended)
**Notes:** Cursor-based SCAN preserves the L2-PROJECT-07 forbidden-IServer.Keys()/KEYS invariant from day one.

### RedisFixture host:port resolution

| Option | Description | Selected |
|--------|-------------|----------|
| Fixed: localhost:6380 hardcoded | Mirrors PostgresFixture's hardcoded localhost:5433; zero env-var coupling; fail-loud on first PING | ✓ |
| Env-var REDIS_TEST_CONN with localhost:6380 default | Overridable for CI matrices; adds one more config knob | |

**User's choice:** Fixed: localhost:6380 hardcoded (Recommended)
**Notes:** No CI matrix driver currently anticipates a non-default port.

### HealthDeadRedisFixture dead-port choice (TEST-REDIS-05)

| Option | Description | Selected |
|--------|-------------|----------|
| 6379 (the unbound host port) | Since compose maps 6380:6379, host's 6379 is guaranteed unbound; free dead-port choice | ✓ |
| 16379 (arbitrary high port) | Same fail-fast ECONNREFUSED behavior; slightly more explicit at call site | |
| 0 (OS-allocated ephemeral, then close) | Guaranteed dead at construction time but adds port-discovery dance | |

**User's choice:** 6379 (the unbound host port) (Recommended)
**Notes:** Locked by D-01 — port choice naturally follows from compose 6380:6379 mapping.

---

## RedisProjectionOptions surface + AddBaseApiRedis form

### AddBaseApiRedis IConnectionMultiplexer registration form

| Option | Description | Selected |
|--------|-------------|----------|
| AddSingleton(_ => ConnectionMultiplexer.Connect(connStr)) | Canonical StackExchange.Redis maintainer pattern; sync Connect runs at first resolution | ✓ |
| AddSingleton via ConfigurationOptions builder | More knobs visible in C# than conn-string; risk: divergence between code and appsettings | |
| Lazy<IConnectionMultiplexer> wrapper | Defers Connect to first .Value access; equivalent to Option A since DI is already lazy | |

**User's choice:** AddSingleton(_ => ConnectionMultiplexer.Connect(connStr)) (Recommended)
**Notes:** Safe with abortConnect=false (INFRA-REDIS-04); mitigates PITFALLS P1/P2 verbatim.

### RedisProjectionOptions surface scope

| Option | Description | Selected |
|--------|-------------|----------|
| Minimum locked by REQ INFRA-COMP-04 only | KeyPrefix + Serialization.JsonOptions; nothing else; YAGNI | ✓ |
| Minimum + Database number (0–15 selector) | Future-proofs multi-DB isolation; small future-headroom | |
| Minimum + Database + CommandFlags default | Useful only when Cluster/replica reads enter scope; dead config in v3.3.0 single-node | |

**User's choice:** Minimum locked by REQ INFRA-COMP-04 only (Recommended)
**Notes:** Phase 15 writer reads exactly the fields it needs. Database / CommandFlags can be added in v3.4 when scale drivers appear.

### Connection string source-of-truth for Connect()

| Option | Description | Selected |
|--------|-------------|----------|
| IConfiguration.GetConnectionString("Redis") inside AddBaseApiRedis | Mirrors Phase 3 AddNpgsql pattern; single source of truth = appsettings ConnectionStrings:Redis | ✓ |
| Bind through RedisProjectionOptions.ConnectionString | Adds indirection that Phase 15 writer doesn't need | |

**User's choice:** IConfiguration.GetConnectionString("Redis") inside AddBaseApiRedis (Recommended)
**Notes:** ConnectionStrings is a separate IConfiguration section from arbitrary :Section keys.

### When does ConnectionMultiplexer.Connect() actually fire (cold-start)?

| Option | Description | Selected |
|--------|-------------|----------|
| First IDatabase resolution at request time | DI lazy-resolves Singleton; Connect runs on first POST /api/v1/orchestration/start; sub-second on localhost | ✓ |
| Pre-warm via no-op IHostedService | Trades ~50ms cold-start for first-request latency improvement; adds hosted service | |

**User's choice:** First IDatabase resolution at request time (Recommended)
**Notes:** With abortConnect=false even a dead Redis lets the Singleton materialize; failures surface at SetAsync/KeyExistsAsync time → 500 + RFC 7807 (ORCH-START-04 / ORCH-STOP-07 paths).

---

## Claude's Discretion

Areas where Claude has flexibility (not user-locked):

- Exact text of compose.yaml inline comments documenting D-02 / D-03 intent
- Whether RedisServiceCollectionExtensions.cs introduces a SectionName const or inlines the string literal
- xUnit fixture trait names and test class names for smoke facts
- Bisect-friendly N-commit sequence — Phase 11 fan-out vs Phase 10 tight 5-commit cycle (both acceptable)

## Deferred Ideas

Ideas surfaced during discussion that were noted for future phases / milestones:

- Redis-specific observability surface (/health/redis, custom Meter gauge) — re-open under OBSERV-REDIS-04 if real ops need arises
- Pre-warm IHostedService for Redis Connect() — re-open if cold-start first-Start latency matters in some environment
- RedisProjectionOptions.Database / .CommandFlags — re-open when Redis Cluster or replica reads enter scope
- Environment.SetEnvironmentVariable fixture override — re-open only if Program.cs starts capturing Redis conn-string by value
- Assembly-shared IConnectionMultiplexer — re-open if test suite TCP-connection count becomes a bottleneck
- Phase12WebAppFactory subclass / rename to IntegrationWebAppFactory — re-open at next milestone boundary if phase-numbered naming has accumulated drag
