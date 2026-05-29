---
phase: 12
plan: 06
subsystem: redis-health-acceptance
tags: [redis, health, soft-dep, fixture, phase-12, test]
requires: [12-04, 12-05]
provides:
  - "HealthDeadRedisFixture (nested in HealthEndpointsTests.cs)"
  - "HealthLive_200_When_Redis_Unreachable fact"
  - "HealthReady_200_When_Redis_Unreachable fact"
  - "INFRA-REDIS-06 + TEST-REDIS-05 closed at xUnit-facts level"
affects:
  - tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs
tech-stack:
  added: []
  patterns:
    - "Pitfall-3 pre-flight TCP probe (TcpClient → localhost:6379, expect ConnectionRefused) as a dead-port regression guard inside a test fixture ctor"
    - "4-arg Phase8WebAppFactory ctor (skipRedisFixture=true) to keep live Postgres while injecting a dead Redis connection string via AddInMemoryCollection (D-08, no env-var workaround)"
key-files:
  created: []
  modified:
    - tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs
decisions:
  - "HealthDeadRedisFixture co-located as nested private sealed class in HealthEndpointsTests.cs (planner discretion; mirrors existing HealthDeadPostgresFixture analog) — D-07 in-place extension"
  - "Dead-Redis port = host 6379 (D-13) — guaranteed unbound by Plan 12-02 compose 6380:6379 mapping; pre-flight TCP probe enforces D-01"
metrics:
  duration: ~5min
  completed: 2026-05-29
---

# Phase 12 Plan 06: Dead-Redis Health Acceptance Facts Summary

JWT-free soft-dependency proof: with Redis unreachable on host port 6379, both `/health/live` AND `/health/ready` return 200 — `HealthDeadRedisFixture` + 2 acceptance facts close INFRA-REDIS-06 + TEST-REDIS-05 while Phase 5 HEALTH-01..05 contracts stay byte-immutable.

## What Shipped

Single modified file: `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — **97 insertions, 0 deletions** (additions only; existing `HealthDeadPostgresFixture` block byte-identical).

1. **`using System.Net.Sockets;`** added (TcpClient for the Pitfall 3 probe).
2. **`HealthDeadRedisFixture`** — nested `private sealed class : Phase8WebAppFactory`, sibling of `HealthDeadPostgresFixture`:
   - 4-arg base ctor: `skipPostgresFixture: false, connectionStringOverride: null!, skipRedisFixture: true, redisConnectionStringOverride: "localhost:6379,abortConnect=false,connectTimeout=2000"` (D-13 dead port; D-08 AddInMemoryCollection-only — no env-var mutation).
   - `AssertDeadRedisPortIsUnbound()` — RESEARCH Pitfall 3 pre-flight TCP probe to `localhost:6379`; throws `InvalidOperationException` containing "compose D-01 regression detected" if the port is bound, surfacing a developer-host-side Redis daemon loudly.
3. **`HealthLive_200_When_Redis_Unreachable`** + **`HealthReady_200_When_Redis_Unreachable`** — both use `TestContext.Current.CancellationToken` (xUnit1051 compliance), `await using var factory = new HealthDeadRedisFixture()`, assert HTTP 200.

## Verification Evidence

- **Build:** `dotnet build … --configuration Release` → Build succeeded, **0 Warnings / 0 Errors** (TreatWarningsAsErrors=true).
- **Targeted dead-Redis facts (MTP `--filter-method`):** `total: 2, succeeded: 2, failed: 0` in **3.0s** — confirms the `connectTimeout=2000` fast-boot path (no 30s hang on unreachable Redis).
- **Full suite (`dotnet test --no-build`):** `Passed! - Failed: 0, Passed: 150, Skipped: 0, Total: 150, Duration: 2m 52s` — exactly 142 baseline + 6 Plan 12-05 RedisFixtureFacts + 2 Plan 12-06 facts = **150 GREEN**. (The `--filter` argument mapped to VSTestTestCaseFilter which MTP ignores, so the run executed the entire suite — a stronger result than the targeted-only filter intended.)
- **Pitfall 3 pre-flight:** `Test-NetConnection localhost -Port 6379 → TcpTestSucceeded: False` BEFORE the run (port unbound; `sk-redis` confirmed mapped 6380:6379 in `docker ps`).
- **HEALTH-01..05 byte-immutable:** `git diff` empty for `StartupCompletionService.cs` AND `HealthServiceCollectionExtensions.cs` (D-05/D-06).
- **No EF migration:** `git status -- "*Migrations*"` empty.
- **No residual Redis keys:** `docker exec sk-redis redis-cli --scan --pattern "test:cls-*"` returns zero (HealthDeadRedisFixture skips RedisFixture; no keys seeded — TEST-REDIS-04 preserved).

## Acceptance-Criteria Substring Audit

All 9 required substrings present (`private sealed class HealthDeadRedisFixture : Phase8WebAppFactory`, dead-port literal, `skipRedisFixture: true`, `compose D-01 regression detected`, `SocketError.ConnectionRefused`, both fact names, `TestContext.Current.CancellationToken`, `await using var factory = new HealthDeadRedisFixture();` — 20 total occurrences). All 5 forbidden patterns absent (`Environment.SetEnvironmentVariable("ConnectionStrings__Redis"`, `AddRedis(`, `AddHealthChecks().AddRedis`, `[Trait("ReadyTag", "redis")]`, `WithTags("redis"` — 0 occurrences).

## Deviations from Plan

None — plan executed exactly as written. The verbatim fixture body and fact bodies were applied as specified. (Note: the plan's TDD framing has no RED gate because the production dependencies — DI registration from Plan 12-04 and the `Phase8WebAppFactory` 4-arg ctor from Plan 12-05 — already existed; the 2 new facts therefore pass on first run. This is the expected behavior for a test-acceptance plan landing on an already-built soft-dep contract.)

## Commits

- `6a9d0b7` — test(12-06): add HealthDeadRedisFixture + 2 soft-dep acceptance facts

## Self-Check: PASSED

- FOUND: tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs (97 insertions)
- FOUND: commit 6a9d0b7
