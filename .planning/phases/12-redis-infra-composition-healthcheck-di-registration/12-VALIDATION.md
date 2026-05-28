---
phase: 12
slug: redis-infra-composition-healthcheck-di-registration
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-29
---

# Phase 12 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `12-RESEARCH.md` §Validation Architecture (lines 1020–1107).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 3.2.2 + xunit.v3.assert + xunit.runner.visualstudio 3.1.5 (MTP-mode) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (`<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` + `<OutputType>Exe</OutputType>` + `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>`) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Phase12" --no-build` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Existing baseline** | 142 facts GREEN × 3 consecutive runs at v3.2.0 close (Phase 11 close) |
| **Estimated runtime** | ~5–10s (Phase 12 facts in isolation); ~30–60s full suite after Phase 12 lands (~5–10% suite-time increase from new facts) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test --filter "FullyQualifiedName~Phase12" --no-build` (~5–10s).
- **After every plan wave:** Run `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (~30–60s).
- **Before `/gsd-verify-work`:** Full suite must be green 3 consecutive runs (Phase 3 D-18 cadence).
- **Phase gate (additive to v3.2.0):** `psql \l` SHA-256 BEFORE = AFTER (must match `0d98b0de…0aac127`) + `LC_ALL=C; docker exec sk-redis redis-cli --scan | sort | sha256sum` BEFORE = AFTER (TEST-REDIS-04).
- **Max feedback latency:** 60 seconds.

---

## Per-Task Verification Map

> Populated by the planner. Each plan task must either point to a Wave-0 test artifact below OR include an automated verify command in its own `<acceptance_criteria>`. The planner enriches this table with concrete Task IDs once PLAN.md files exist.

| Req ID | Behavior | Test Type | Automated Command | File Exists |
|--------|----------|-----------|-------------------|-------------|
| INFRA-REDIS-01 | `docker compose ps` shows `sk-redis` healthy with image `redis:7.4.x-alpine` | Smoke + xUnit fact | `docker compose ps redis --format json \| jq '.Health'` → `"healthy"` AND `ComposeYamlFacts.ComposeRedisHealthyFact` | ❌ W0 |
| INFRA-REDIS-02 | Healthcheck `redis-cli ping`, interval 5s, retries 10, start_period 5s; baseapi-service `depends_on: redis service_healthy` | Compose YAML parse fact | `ComposeYamlFacts.RedisHealthcheckShapeMatches` | ❌ W0 |
| INFRA-REDIS-03 | `StackExchange.Redis 2.13.1` pinned in Directory.Packages.props + referenced from `BaseApi.Service.csproj` | Build verification | `dotnet list src/BaseApi.Service/BaseApi.Service.csproj package \| grep "StackExchange.Redis 2.13.1"` | ❌ W0 |
| INFRA-REDIS-04 | `ConnectionStrings:Redis` contains `abortConnect=false,connectTimeout=5000` in BOTH `appsettings.json` (`redis:6379`) AND `appsettings.Development.json` (`localhost:6380`) | xUnit fact | `AppsettingsFacts.RedisConnStringHasAbortConnectFalse` (regex assert in both files) | ❌ W0 |
| INFRA-REDIS-05 | Default `RedisProjectionOptions.KeyPrefix == "skp:"`; binding from `cfg.GetSection("Redis")` works | Integration | `RedisProjectionOptionsBindingFacts.DefaultsBindEndToEnd` | ❌ W0 |
| INFRA-REDIS-06 | Redis down ⇒ both `/health/live` AND `/health/ready` return 200 (soft-dep) | Integration | `HealthDeadRedisFixture` + `HealthDeadRedisFacts` (live + ready) | ❌ W0 |
| INFRA-COMP-01 | `AddBaseApiRedis` chained as call #7 in `AddBaseApi<TDbContext>` after `AddBaseApiMapping` | DI descriptor assert | `BaseApiCompositionFacts.AddBaseApiChainsAddBaseApiRedisAsCallSeven` | ❌ W0 |
| INFRA-COMP-02 | `IConnectionMultiplexer` lifetime = Singleton; reference-equal across scopes | DI descriptor assert | Same fact above + `Assert.Same(rootMux, scopedMux)` fact | ❌ W0 |
| INFRA-COMP-03 | `IDatabase` NOT registered as DI service (consumer resolves via `_multiplexer.GetDatabase()`) | DI negative assert | `BaseApiCompositionFacts.IDatabaseIsNotRegistered` | ❌ W0 |
| INFRA-COMP-04 | `RedisProjectionOptions` bound to `Redis:*` section via `services.Configure<>` | DI binding assert | `RedisProjectionOptionsBindingFacts.InjectedOverrideReflectsInIOptions` | ❌ W0 |
| TEST-REDIS-01 | `RedisFixture.KeyPrefix` matches `^test:cls-[0-9a-f]{32}:$`; per-fixture-instance Guid | Unit | `RedisFixtureFacts.KeyPrefixIsGuidPerInstance` | ❌ W0 |
| TEST-REDIS-02 | `Phase8WebAppFactory` boots with `RedisFixture`; `IConfiguration` shows `ConnectionStrings:Redis = localhost:6380...` | Integration | `RedisFixturePhase8FactoryIntegration.FactoryBootsWithRedisFixture` | ❌ W0 |
| TEST-REDIS-03 | `RedisFixture.DisposeAsync` SCAN-asserts zero residual keys with matching prefix; throws on violation; FLUSHDB never called | Unit + cleanup discipline | `RedisFixtureDisposalFacts` (3 facts: clean-dispose, non-matching-prefix-preserved, residual-injected-throws) | ❌ W0 |
| TEST-REDIS-04 | `LC_ALL=C; docker exec sk-redis redis-cli --scan \| sort \| sha256sum` BEFORE = AFTER full integration suite | Phase-close shell ritual | `pwsh -File scripts/phase-12-close.ps1` (or `bash scripts/phase-12-close.sh`) | ❌ W0 |
| TEST-REDIS-05 | `HealthDeadRedisFixture` proves `/health/live` AND `/health/ready` both return 200 with Redis down | Integration | Same as INFRA-REDIS-06 (`HealthDeadRedis*` facts in `HealthEndpointsTests.cs`) | ❌ W0 |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Composition/RedisFixture.cs` — NEW (Code Example 3 of RESEARCH.md) — covers TEST-REDIS-01, TEST-REDIS-03
- [ ] `tests/BaseApi.Tests/Composition/RedisFixtureFacts.cs` — NEW — unit facts for `KeyPrefix` Guid uniqueness + `DisposeAsync` residual assertion
- [ ] `tests/BaseApi.Tests/Composition/RedisProjectionOptionsBindingFacts.cs` — NEW — covers INFRA-REDIS-05, INFRA-COMP-04
- [ ] `tests/BaseApi.Tests/Composition/BaseApiCompositionFacts.cs` — NEW or EXTEND — covers INFRA-COMP-01, INFRA-COMP-02, INFRA-COMP-03
- [ ] `tests/BaseApi.Tests/Configuration/AppsettingsFacts.cs` — NEW or EXTEND — covers INFRA-REDIS-04
- [ ] `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` — NEW or EXTEND — covers INFRA-REDIS-01, INFRA-REDIS-02
- [ ] Extend `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — add `HealthDeadRedisFixture` + 2 new facts — covers INFRA-REDIS-06, TEST-REDIS-05
- [ ] Extend `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` — Pattern 5 in-place modifications (D-07, D-08) — covers TEST-REDIS-02
- [ ] `scripts/phase-12-close.ps1` AND `scripts/phase-12-close.sh` — NEW — phase-close gate ritual per Code Example 6 — covers TEST-REDIS-04 (planner picks one or both based on cross-platform need)
- [ ] Framework install: NONE — xUnit v3 + StackExchange.Redis 2.13.1 already wired or pinned via CPM in Plan 12-02

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| `docker compose up -d` brings `sk-redis` to `healthy` state in CLI | INFRA-REDIS-01 | The xUnit `ComposeRedisHealthyFact` exercises the YAML schema, but the live-Docker `healthy` transition is a runtime check that benefits from a manual spot-check before phase close | After `docker compose up -d`, run `docker compose ps redis` and verify status `Up X seconds (healthy)`; capture output for STATE.md plan close |
| Phase-close 3-GREEN cadence | TEST-REDIS-04 (Phase 3 D-18 inheritance) | Run sequencing is operator-driven (the script asserts; the operator runs three full-suite passes serially) | Run `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` three times in succession with no source edits between runs; all three must exit 0 with identical fact counts |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify OR a Wave 0 dependency in the table above
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all 9 MISSING references in the requirements map
- [ ] No watch-mode flags (`--watch`) anywhere — Phase 11 ban inherited
- [ ] Feedback latency < 60s (per-task), < 60s (per-wave on isolated Phase 12 facts)
- [ ] `psql \l` SHA-256 BEFORE = AFTER captured per task that touches `appsettings*.json` or DI chain
- [ ] `redis-cli --scan | sort | sha256sum` BEFORE = AFTER captured per Phase 12 plan that runs integration tests
- [ ] `nyquist_compliant: true` set in frontmatter once the planner finalizes per-task Task IDs

**Approval:** pending
