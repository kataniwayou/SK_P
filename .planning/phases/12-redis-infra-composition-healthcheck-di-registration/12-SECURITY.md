---
phase: 12
slug: redis-infra-composition-healthcheck-di-registration
status: verified
threats_total: 44
threats_closed: 44
threats_open: 0
asvs_level: 1
created: 2026-05-29
---

# Phase 12 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| Host ↔ compose network | Docker compose internal bridge; Redis accessible only on `redis:6379` (internal) and `localhost:6380` (host-side) | Redis key/value data; no secrets in transit |
| Test runner ↔ compose Redis | xUnit integration tests connect to `localhost:6380`; host port 6379 intentionally unbound (dead-Redis test invariant) | Ephemeral per-test-class keyspaces with `test:cls-{Guid:N}:` prefix |
| App ↔ IConnectionMultiplexer | Singleton registered via `AddBaseApiRedis`; connection string read from `IConfiguration` at boot | Redis conn string (no password in dev) |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-12-01-01 | Tampering | Directory.Packages.props | mitigate | `StackExchange.Redis Version="2.13.1"` literal pin — no floating ranges | closed |
| T-12-01-02 | Info Disclosure | csproj/props | mitigate | Negative grep across all csproj/props: `OpenTelemetry.Instrumentation.StackExchangeRedis` absent | closed |
| T-12-01-03 | EoP | CPM / client | accept | No connection string composed at CPM layer | closed |
| T-12-01-04 | DoS | csproj | mitigate | `Microsoft.Extensions.Caching.StackExchangeRedis` absent from all csproj files | closed |
| T-12-02-01 | Info Disclosure | redis service | accept | Local-dev scope; accepted per plan | closed |
| T-12-02-02 | Tampering | compose command directive | mitigate | Verbatim `["redis-server","--save","","--appendonly","no"]`; no `--requirepass`, `--protected-mode no`, or `--bind 0.0.0.0` | closed |
| T-12-02-03 | DoS | compose healthcheck | mitigate | `interval:5s / timeout:3s / retries:10 / start_period:5s` + `depends_on: redis: condition: service_healthy` | closed |
| T-12-02-04 | EoP | compose env ConnectionStrings__Redis | mitigate | `ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"` — no `allowAdmin=true` | closed |
| T-12-02-05 | Info Disclosure | compose env | mitigate | No OTel Redis env vars (`OTEL_EXPORTER_OTLP_ENDPOINT` present for base service only; no Redis-specific OTel) | closed |
| T-12-02-06 | Tampering | compose.yaml mutation | mitigate | Only redis + baseapi-service additions; 4 existing service blocks (postgres, elasticsearch, otel-collector, prometheus) unchanged | closed |
| T-12-03-01 | Spoofing | appsettings hostname | accept | Local+compose-DNS; prod overrides via env vars | closed |
| T-12-03-02 | Info Disclosure | appsettings.json ready body | mitigate | `HealthServiceCollectionExtensions` exposes only `npgsql` on `ready` tag; Redis not on any health check; no password in conn string | closed |
| T-12-03-03 | EoP | appsettings | mitigate | `allowAdmin=true` absent from both `appsettings.json` and `appsettings.Development.json` | closed |
| T-12-03-04 | Tampering | appsettings boot crash | mitigate | `abortConnect=false,connectTimeout=5000` in both appsettings files | closed |
| T-12-03-05 | Info Disclosure | TLS-off plaintext | accept | Local plaintext; prod TLS overrides deferred | closed |
| T-12-04-01 | Tampering | RedisServiceCollectionExtensions lifetime | mitigate | `AddSingleton<IConnectionMultiplexer>` only; no `AddScoped` or `AddTransient` present | closed |
| T-12-04-02 | DoS | boot conn-string read | mitigate | `cfg.RequireConnectionString("Redis")` fails fast with clear error before DI wiring | closed |
| T-12-04-03 | Info Disclosure | RedisConnectionException leak | accept | Deferred to Phase 15 RFC 7807 mapping; no error surface added in Phase 12 | closed |
| T-12-04-04 | EoP | RedisServiceCollectionExtensions | mitigate | `allowAdmin=true` absent from `RedisServiceCollectionExtensions.cs` | closed |
| T-12-04-05 | Info Disclosure | OTel Redis registration | mitigate | No `AddInstrumentation`/OTel Redis call in `RedisServiceCollectionExtensions.cs` | closed |
| T-12-04-06 | Tampering | HEALTH regression | mitigate | `git diff` on `StartupCompletionService.cs` + `HealthServiceCollectionExtensions.cs` returns empty; last Phase 12 touch was Phase 8 (commit 89d2028) | closed |
| T-12-05-01 | Tampering | RedisFixture.DisposeAsync FLUSHDB | mitigate | `FLUSHDB`/`FLUSHALL` appear only in doc-comment prose and error-message string (lines 22, 86); no actual Redis FLUSHDB/FLUSHALL command issued | closed |
| T-12-05-02 | Info Disclosure | RedisFixture ConnectionString | mitigate | `ConnectionString = "localhost:6380,abortConnect=false,connectTimeout=5000"` — no `name=` fragment | closed |
| T-12-05-03 | DoS | Phase8WebAppFactory multiplexer leak | mitigate | `InitializeAsync` catches Redis init failure and calls `_fixture.DisposeAsync()` before rethrowing | closed |
| T-12-05-04 | Tampering | synchronous KEYS | mitigate | `server.Keys(` absent; only `server.KeysAsync` (SCAN-based) used in `DisposeAsync` | closed |
| T-12-05-05 | Info Disclosure | HEALTH regression via fixture | accept | HEALTH source unchanged; factory injects connection strings via `IConfiguration` only | closed |
| T-12-05-06 | Info Disclosure | OTel Redis in tests csproj | mitigate | `BaseApi.Tests.csproj` contains `StackExchange.Redis` only; no `OpenTelemetry.Instrumentation.StackExchangeRedis` | closed |
| T-12-06-01 | Tampering | HealthDeadRedisFixture dead-port | mitigate | `AssertDeadRedisPortIsUnbound()` TCP-probes `localhost:6379`; throws `InvalidOperationException` if port is bound | closed |
| T-12-06-02 | Info Disclosure | HEALTH regression via dead-Redis test | mitigate | `git diff` on HEALTH source empty; facts in `HealthEndpointsTests` pre-date Phase 12 and remain unmodified | closed |
| T-12-06-03 | DoS | boot timeout under dead Redis | mitigate | `DeadRedisConnectionString = "localhost:6379,abortConnect=false,connectTimeout=2000"` caps boot wait | closed |
| T-12-06-04 | Spoofing | loopback hostname | accept | Localhost test trust boundary; accepted for test infrastructure | closed |
| T-12-06-05 | Info Disclosure | conn-string leak via ready body | mitigate | `HealthLive_200_When_Redis_Unreachable` and `HealthReady_200_When_Redis_Unreachable` assert `HttpStatusCode.OK` only; no body inspection | closed |
| T-12-07-01 | Tampering | OBSERV-REDIS-01 regression | mitigate | `Solution_Csproj_Does_NOT_Reference_OpenTelemetry_StackExchangeRedis` fact in `BaseApiCompositionFacts` walks all csproj/props excluding bin/obj | closed |
| T-12-07-02 | Tampering | lifetime swap | mitigate | `AddBaseApi_Registers_IConnectionMultiplexer_Singleton` + `AddBaseApi_Singleton_Multiplexer_Is_ReferenceEqual_Across_Scopes` facts | closed |
| T-12-07-03 | Tampering | CMD-SHELL form | mitigate | `ComposeYaml_Does_NOT_Use_CmdShell_For_Redis_Healthcheck` fact matches `test:` directive; excludes comment prose | closed |
| T-12-07-04 | Tampering | abortConnect regression | mitigate | `Both_AppsettingsFiles_Contain_abortConnect_false` fact asserts both files independently | closed |
| T-12-07-05 | Tampering | Redis 8.x AGPLv3 | mitigate | `ComposeYaml_Does_NOT_Pin_Redis_8x` fact asserts `image:\s*redis:8\.` absent | closed |
| T-12-07-06 | EoP | allowAdmin appsettings | mitigate | `Neither_AppsettingsFile_Contains_allowAdmin_true` fact asserts both files | closed |
| T-12-08-01 | Tampering | SHA-256 locale manipulation | mitigate | Bash: `LC_ALL=C sort` on `docker exec sk-redis redis-cli --scan`; PS: `Sort-Object -CaseSensitive`. Postgres BEFORE/AFTER uses `docker compose exec -T postgres` (corrected in d5d97e3; `sk-redis` retains explicit `container_name` so `docker exec sk-redis` remains correct for Redis) | closed |
| T-12-08-02 | Tampering | silent fact regression | mitigate | Bash: 3-element array equality check `${PASSED_COUNTS[0]}==${PASSED_COUNTS[1]}==${PASSED_COUNTS[2]}`; PS: `@(...) | Select-Object -Unique` count == 1 | closed |
| T-12-08-03 | Info Disclosure | HEALTH regression slipped through | mitigate | Both scripts: `git diff StartupCompletionService.cs HealthServiceCollectionExtensions.cs`; non-empty exits 1 | closed |
| T-12-08-04 | Tampering | EF migration | mitigate | Both scripts: `git status --porcelain src/BaseApi.Service/Migrations/`; non-empty exits 1 | closed |
| T-12-08-05 | DoS | 3-GREEN skipped | mitigate | Both scripts loop `for i in 1 2 3` / `for ($i = 1; $i -le 3; $i++)` unconditionally | closed |
| T-12-08-06 | Spoofing | operator approves without running | accept | Human-verify gate; SHA-256 hashes required in STATE.md before close approval | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-12-01 | T-12-01-03 | CPM Plan 12-01 composes no Redis connection string — `allowAdmin` cannot appear at the CPM layer by construction. The risk surface is at the consumer csproj level, not here. | GSD security auditor | 2026-05-29 |
| AR-12-02 | T-12-02-01 | Redis service is local-dev only (compose stack, single machine). No external network exposure. Production deployments override connection strings via environment variables and apply TLS + auth at the infra layer. | GSD security auditor | 2026-05-29 |
| AR-12-03 | T-12-03-01 | `redis` hostname resolved via Docker compose internal DNS. Local dev posture only; production supplies overrides via `ConnectionStrings__Redis` env var. Risk scope: developer workstations. | GSD security auditor | 2026-05-29 |
| AR-12-04 | T-12-03-05 | Plaintext Redis for local dev. TLS deliberately deferred; no secrets traverse the connection in Phase 12 (L2 projection values, not auth tokens or PII). Production MUST add `ssl=true` at Phase 15 prod-hardening. | GSD security auditor | 2026-05-29 |
| AR-12-05 | T-12-04-03 | `RedisConnectionException` may surface the Redis hostname in the 500 response body before Phase 15 RFC 7807 mapping lands. Acceptable in Phase 12 because: (a) Redis hostname is not a secret in dev; (b) Phase 15 wires the error mapping with explicit RFC 7807 problem details that will suppress raw exception text. | GSD security auditor | 2026-05-29 |
| AR-12-06 | T-12-05-05 | HEALTH source (`StartupCompletionService.cs`, `HealthServiceCollectionExtensions.cs`) is byte-immutable in Phase 12. Factory injects Redis only via `IConfiguration`; the HEALTH check chain (`AddNpgSql` + `startup` + `self`) is unchanged. No regression risk. | GSD security auditor | 2026-05-29 |
| AR-12-07 | T-12-06-04 | `HealthDeadRedisFixture` trusts `localhost` as the test isolation boundary. `AssertDeadRedisPortIsUnbound()` provides a loud fail if a developer's host Redis is on 6379. Acceptable for test infrastructure. | GSD security auditor | 2026-05-29 |
| AR-12-08 | T-12-08-06 | The operator human-verify checkpoint (Task 3) is a process control, not a code control. Mitigation: scripts produce explicit SHA-256 output that must be pasted into STATE.md; the gate loop is unconditional (T-12-08-05 closed). Bypassing requires deliberate falsification of STATE.md, outside the automated control surface. | GSD security auditor | 2026-05-29 |

*Accepted risks do not resurface in future audit runs.*

---

## Unregistered Threat Flags

None. No `## Threat Flags` sections were found in any Phase 12 SUMMARY.md file. All threats are registered in the plan threat model.

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-05-29 | 44 | 44 | 0 | gsd-security-auditor (claude-sonnet-4-6) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-05-29
