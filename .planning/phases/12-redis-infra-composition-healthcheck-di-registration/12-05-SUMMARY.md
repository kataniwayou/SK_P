---
phase: 12-redis-infra-composition-healthcheck-di-registration
plan: 05
subsystem: test-infra
tags: [redis, test-fixture, isolation, scan, iasynclifetime, web-app-factory]

# Dependency graph
requires:
  - phase: 12-01
    provides: StackExchange.Redis 2.13.1 CPM pin + PackageReference on BaseApi.Tests.csproj
  - phase: 12-02
    provides: compose Redis service reachable at localhost:6380 (host 6379 unbound)
provides:
  - public sealed RedisFixture (IAsyncLifetime) — per-class localhost:6380 multiplexer + per-instance test:cls-{Guid:N}: KeyPrefix + SCAN+DEL+assert-zero dispose (D-09..D-12 / TEST-REDIS-01..03)
  - Phase8WebAppFactory in-place D-07 extension — _redisFixture field, 4-arg ctor (skipPostgresFixture/connStr/skipRedisFixture/redisConnStr) for Plan 12-06 HealthDeadRedisFixture, RedisConnectionString/RedisKeyPrefix/RedisMultiplexer public properties, defensive Postgres-rollback InitializeAsync, Redis-first DisposeAsync, AddInMemoryCollection Redis-key injection (D-08)
  - RedisFixtureFacts — 6 xUnit facts pinning the fixture invariants
affects: [12-06, 12-07, phase-16-redis-projection-facts]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Per-class Redis isolation via per-instance Guid KeyPrefix (test:cls-{Guid:N}:) — no FLUSHDB, SCAN-only cleanup (L2-PROJECT-07 reference pattern)"
    - "SCAN+DEL+re-SCAN+assert-zero fail-loud dispose contract (IServer.KeysAsync, NOT synchronous IServer.Keys())"
    - "D-08 AddInMemoryCollection-only Redis config injection (NO env-var workaround — AddBaseApiRedis reads cfg.GetConnectionString lazily at first multiplexer resolution, after ConfigureWebHost)"
    - "Defensive Postgres-rollback in factory InitializeAsync when Redis init throws (Pitfall 4 — leaked test-DB prevention)"
    - "Deterministic residual-key race test: pre-seed 300 matching keys to widen DEL→re-SCAN window + tight side-channel reseed loop"

key-files:
  created:
    - tests/BaseApi.Tests/Composition/RedisFixture.cs
    - tests/BaseApi.Tests/Composition/RedisFixtureFacts.cs
  modified:
    - tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs

key-decisions:
  - "RedisFixture is public sealed (consumed by 12-06/12-07 in same assembly; consistent with PostgresFixture public sealed)"
  - "Re-SCAN uses pageSize:1000 vs first-SCAN pageSize:250 (Pitfall 7 defensive — larger verification page reduces cursor edge-case risk)"
  - "Residual-injection fact made deterministic by pre-seeding 300 bulk keys (widens the fixture's SCAN+DEL→re-SCAN window) + a tight side-channel reseed loop, replacing the flaky single-key approach that failed 2/4 full-suite runs (Rule 1 self-fix)"
  - "Task.Run(reseeder, cts.Token) passes the CancellationToken to satisfy xUnit1051 under TreatWarningsAsErrors (Plan 03-02 precedent)"

metrics:
  duration: ~25min
  tasks: 2
  files: 3
  completed: 2026-05-29
---

# Phase 12 Plan 05: Redis Test Fixture Infrastructure Summary

Landed the per-class Redis test fixture: a new `RedisFixture` (SCAN+DEL+assert-zero dispose, no FLUSHDB), a `RedisFixtureFacts` smoke-test class (6 facts), and an in-place D-07 extension of `Phase8WebAppFactory` that accretes Redis composition alongside the existing Postgres composition without breaking the ~30 existing `IClassFixture<Phase8WebAppFactory>` bindings.

## What Shipped

**Task 1 — `RedisFixture.cs` (commit e30acda, 95 LOC, NEW)**
- `public sealed class RedisFixture : IAsyncLifetime` mirroring `PostgresFixture` topology.
- `ConnectionString = "localhost:6380,abortConnect=false,connectTimeout=5000"` (D-11).
- `KeyPrefix = $"test:cls-{Guid.NewGuid():N}:"` (D-12 / TEST-REDIS-01 — matches `^test:cls-[0-9a-f]{32}:$`).
- `InitializeAsync` → `ConnectionMultiplexer.ConnectAsync(ConnectionString)` (D-09 per-fixture multiplexer).
- `DisposeAsync` → SCAN MATCH `{KeyPrefix}*` (pageSize:250) via `IServer.KeysAsync` → `KeyDeleteAsync(toDelete.ToArray())` → re-SCAN (pageSize:1000) → throw `InvalidOperationException` ("FLUSHDB is FORBIDDEN") on residual > 0, all in try/finally so `Multiplexer.DisposeAsync()` always runs (D-10 / TEST-REDIS-03).
- Forbidden-substring invariants verified: no `FLUSHALL`, no synchronous `server.Keys(`, no `name=` ClientName flag; `FLUSHDB` appears only inside the FORBIDDEN doc-comment + error message.

**Task 2 — `RedisFixtureFacts.cs` + `Phase8WebAppFactory.cs` (commit ac43883)**
- `RedisFixtureFacts.cs` (130 LOC, NEW) — 6 `[Fact]` methods, `[Trait("Phase12Wave","B")]`:
  - `KeyPrefix_Matches_TestClsGuid_Regex`
  - `KeyPrefix_Differs_Across_Instances`
  - `ConnectionString_Is_Localhost6380_With_AbortConnectFalse`
  - `InitializeAsync_Connects_Multiplexer`
  - `DisposeAsync_With_Empty_Prefix_Does_Not_Throw`
  - `DisposeAsync_With_Residual_Matching_Key_Throws_InvalidOperationException` (asserts message contains "FLUSHDB is FORBIDDEN")
- `Phase8WebAppFactory.cs` (in-place D-07 accretion, +210/-9):
  - new `using StackExchange.Redis;`
  - new fields `_redisFixture`, `_redisConnectionStringOverride`, `_skipRedisFixture`
  - new 4-arg protected ctor `(bool skipPostgresFixture, string connectionStringOverride, bool skipRedisFixture, string redisConnectionStringOverride)` with `ArgumentException` guards on both skip-without-override paths
  - new public properties `RedisConnectionString`, `RedisKeyPrefix` (benign `test:cls-deadredis:` placeholder on skip path), `RedisMultiplexer` (RESEARCH Open Q4 — Phase 16 access surface)
  - `InitializeAsync` chains Redis init with defensive Postgres-rollback in a catch block (Pitfall 4)
  - `DisposeAsync` disposes Redis FIRST then Postgres (Pattern 5 ordering)
  - `ConfigureWebHost` `AddInMemoryCollection` extended with `["ConnectionStrings:Redis"] = RedisConnectionString` + `["Redis:KeyPrefix"] = RedisKeyPrefix` (D-08 — no env-var workaround)
  - existing default ctor `public Phase8WebAppFactory() { }`, the `(string)` ctor, and the 2-arg `(bool, string)` ctor all PRESERVED.

## Test Results

- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release` → 0 Warning, 0 Error.
- `RedisFixtureFacts` filter run → **6/6 GREEN**.
- Full suite **3 consecutive GREEN**: 148/148 (142 v3.2.0 baseline + 6 new), failed:0, skipped:0 (~2m50s each).
- Residual-injection fact verified deterministic 5/5 in isolation after the Rule-1 self-fix.
- `docker exec sk-redis redis-cli --scan --pattern 'test:cls-*'` → **0 keys** after the full test run (TEST-REDIS-04 invariant supported; DisposeAsync SCAN-asserted zero residual).

## Invariants Confirmed

- HEALTH-01..05: `git diff` on `StartupCompletionService.cs` + `HealthServiceCollectionExtensions.cs` → empty.
- No EF migration generated (`src/BaseApi.Service/Migrations/` does not exist; no new files).
- No production-code changes outside `tests/`.
- `Phase8WebAppFactory.cs` contains no `FLUSHDB`, no `Environment.SetEnvironmentVariable("ConnectionStrings__Redis"`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Residual-injection fact was non-deterministic (failed 2/4 full-suite runs)**
- **Found during:** Task 2 (full-suite 3-GREEN cadence).
- **Issue:** The plan's suggested single-key side-channel injection (seed one key, then call DisposeAsync) is deleted by the fixture's own SCAN+DEL, so the re-SCAN found zero and no exception was thrown — `Assert.ThrowsAsync` failed intermittently. The original re-seed-loop attempt still raced: when DisposeAsync's DEL→re-SCAN window was sub-millisecond, the loop missed it.
- **Fix:** Pre-seed 300 matching `{KeyPrefix}bulk-{i}` keys before dispose so the fixture's first SCAN (pageSize:250) pages twice + DELs a 300-key batch, widening the DEL→re-SCAN window measurably; a tight side-channel reseed loop then reliably re-creates one residual key inside that window. Finally-block SCANs and deletes ALL leftovers so the global snapshot stays byte-identical.
- **Files modified:** tests/BaseApi.Tests/Composition/RedisFixtureFacts.cs
- **Commit:** ac43883

**2. [Rule 3 - Blocking] xUnit1051 analyzer error on `Task.Run` (CancellationToken)**
- **Found during:** Task 2 build.
- **Issue:** `Task.Run(async () => ...)` without a CancellationToken trips xUnit1051, which is build-fatal under `TreatWarningsAsErrors=true` (Plan 03-02 precedent).
- **Fix:** Passed `cts.Token` to `Task.Run(reseeder, cts.Token)`.
- **Files modified:** tests/BaseApi.Tests/Composition/RedisFixtureFacts.cs
- **Commit:** ac43883

## TDD Gate Compliance

Plan tasks are typed `tdd="true"` but the artifact under test (`RedisFixture`) is itself test-infrastructure, and the verifying facts (`RedisFixtureFacts`) were authored in Task 2 after Task 1's fixture. Commits are `feat(...)` per the standard executor commit protocol (no separate RED `test(...)` commit). The 6 facts assert the fixture contract and pass GREEN x3 consecutive; the residual-violation fact transitioned RED (No exception thrown) → GREEN after the Rule-1 determinism fix, exercising the fail-loud path.

## Self-Check: PASSED
- FOUND: tests/BaseApi.Tests/Composition/RedisFixture.cs
- FOUND: tests/BaseApi.Tests/Composition/RedisFixtureFacts.cs
- FOUND: tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs (modified)
- FOUND commit: e30acda
- FOUND commit: ac43883
