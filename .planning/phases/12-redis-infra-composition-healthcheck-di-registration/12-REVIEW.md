---
phase: 12-redis-infra-composition-healthcheck-di-registration
reviewed: 2026-05-29T00:00:00Z
depth: standard
files_reviewed: 21
files_reviewed_list:
  - Directory.Packages.props
  - compose.yaml
  - scripts/phase-12-close.ps1
  - scripts/phase-12-close.sh
  - src/BaseApi.Core/BaseApi.Core.csproj
  - src/BaseApi.Core/Configuration/RedisProjectionOptions.cs
  - src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs
  - src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs
  - src/BaseApi.Service/BaseApi.Service.csproj
  - src/BaseApi.Service/appsettings.Development.json
  - src/BaseApi.Service/appsettings.json
  - tests/BaseApi.Tests/BaseApi.Tests.csproj
  - tests/BaseApi.Tests/Composition/AddBaseApiFacts.cs
  - tests/BaseApi.Tests/Composition/AppsettingsFacts.cs
  - tests/BaseApi.Tests/Composition/BaseApiCompositionFacts.cs
  - tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs
  - tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs
  - tests/BaseApi.Tests/Composition/RedisFixture.cs
  - tests/BaseApi.Tests/Composition/RedisFixtureFacts.cs
  - tests/BaseApi.Tests/Composition/RedisProjectionOptionsBindingFacts.cs
  - tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs
findings:
  critical: 0
  warning: 2
  info: 5
  total: 7
status: issues_found
---

# Phase 12: Code Review Report

**Reviewed:** 2026-05-29
**Depth:** standard
**Files Reviewed:** 21
**Status:** issues_found

## Summary

Phase 12 wires Redis L2 infrastructure: a compose `redis` service block, a Singleton
`IConnectionMultiplexer` DI registration, `RedisProjectionOptions` binding, the host-side /
Docker-internal connection-string split, and a substantial test battery (compose-yaml regex
guards, appsettings guards, DI-graph shape facts, a per-class `RedisFixture` with SCAN+DEL
cleanup, and two dead-Redis soft-dependency acceptance facts). The implementation is small,
well-commented, and follows the established Phase 3/5/7 patterns (fail-fast `RequireConnectionString`,
Singleton multiplexer, options binding). No security issues (no hardcoded prod secrets — dev
postgres/postgres credentials are an established dev-only convention; Redis is plaintext-by-design
for local compose).

The findings below are about (1) a documentation-vs-behavior discrepancy in the eager
connection-string capture inside `AddBaseApiRedis`, which has a latent correctness implication
for the test fixture's connection-string override path, and (2) a Windows/PowerShell portability
gap in the close-gate script. The rest are quality/consistency items.

## Warnings

### WR-01: `AddBaseApiRedis` captures the Redis connection string eagerly, contradicting the "lazy first-resolution" documentation

**File:** `src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs:50-58`
**Issue:**
The connection string is resolved **eagerly at registration time**:

```csharp
var connStr = cfg.RequireConnectionString("Redis");          // line 50 — runs when AddBaseApi is called
...
services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(connStr));                  // closure captures the pre-resolved local
```

Only the `ConnectionMultiplexer.Connect(...)` *call* is lazy (deferred to first resolution); the
*connection string value* is captured by value at registration time. This contradicts:

- The XML doc on this same file (D-16 / D-17) which says the value is read "inside the Singleton
  factory closure at first `IConnectionMultiplexer` resolution."
- `Phase8WebAppFactory.cs:188-191`, which asserts "AddBaseApiRedis reads
  `cfg.GetConnectionString("Redis")` inside the Singleton factory closure ... which is AFTER
  ConfigureWebHost runs. No value-capture race."

In `Program.cs`, `AddBaseApi<AppDbContext>(builder.Configuration)` (line 7) runs BEFORE
`builder.Build()`, but the `Phase8WebAppFactory.ConfigureAppConfiguration` callback that injects
`["ConnectionStrings:Redis"] = RedisConnectionString` runs DURING `builder.Build()`. This is the
exact same ordering trap that `HealthDeadPostgresFixture` documents at length
(`HealthEndpointsTests.cs:229-249`) for `AddNpgSql` and works around with an env-var-in-ctor.

The reason this does not currently break: `RedisConnectionString` for the live-fixture path
resolves to `localhost:6380,...` (same as `appsettings.Development.json`), and the dead-Redis
path uses `skipRedisFixture=true` and never resolves `IConnectionMultiplexer` (so `Connect` never
fires, and `abortConnect=false` would keep it non-fatal anyway). So the captured value being the
appsettings value rather than the fixture-injected value is masked by them being equal. But the
moment a fixture injects a *different* Redis connection string and then resolves the multiplexer,
it will silently connect to the appsettings value, not the injected one — the same class of bug
the Postgres side already hit.

**Fix:** Either (a) correct the doc comments to state the value is captured at registration time
(matching `AddBaseApiPersistence`/`AddBaseApiHealth` precedent), or (b) if late binding is
actually intended, resolve inside the factory so the override flows:

```csharp
services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var cfgAtResolve = sp.GetRequiredService<IConfiguration>();
    return ConnectionMultiplexer.Connect(cfgAtResolve.RequireConnectionString("Redis"));
});
```

Option (a) is the lower-risk choice given the soft-dependency design; the doc is the thing that
is wrong, not the code. But the false "no value-capture race" claim in two places should not stand.

### WR-02: Close-gate scripts diverge — Bash pre-flight skips `otel-collector`, PowerShell special-cases it; Bash `jq` parse path differs from PS

**File:** `scripts/phase-12-close.sh:29` and `scripts/phase-12-close.ps1:30-37`
**Issue:**
The two close-gate scripts are documented as equivalents but diverge in the pre-flight health loop:

- PowerShell iterates `@('postgres','redis','otel-collector','elasticsearch','prometheus')` and
  special-cases `otel-collector` (`-and $svc -ne 'otel-collector'`) because the distroless collector
  has no healthcheck and reports no `Health` field.
- Bash iterates `postgres redis elasticsearch prometheus` (no `otel-collector`) and requires
  `"$health" == "healthy"` for every service in the list.

The net behavior is the same (collector excluded from the health gate), but the divergence is a
maintenance hazard: a future edit that adds collector back to the Bash list will hard-fail the
gate (collector never reports `healthy`), while the PS version remains correct. The Bash `jq -r
'.[].Health'` also assumes `docker compose ps --format json` emits an array; on some Docker Compose
versions it emits newline-delimited objects (not an array), which makes `jq '.[]'` return nothing
and silently fall through to the `|| echo "missing"` → abort. This is a latent
environment-portability bug.

**Fix:** Make the two service lists and the collector-exclusion logic structurally identical
(exclude `otel-collector` from the array in BOTH, or special-case it in BOTH). For the `jq` parse,
defend against both JSON shapes, e.g. `docker compose ps "$svc" --format json | jq -rs '.[-1].Health
// (.[0].Health) // "missing"'` or normalize with `--format '{{.Health}}'` (template form) instead
of JSON to avoid the array-vs-object ambiguity entirely.

## Info

### IN-01: Pre-existing dev credentials present in `appsettings.json` / `appsettings.Development.json` (acceptable, noted for completeness)

**File:** `src/BaseApi.Service/appsettings.json:14`, `appsettings.Development.json:10`
**Issue:** `Username=postgres;Password=postgres` is committed in both files. This is an established
dev-only convention in this repo (compose uses the same defaults, `AddBaseApiFacts` deliberately
builds its conn string from env vars to avoid literal credentials), and `UserSecretsId` is
explicitly deferred to v2 per the csproj comment. Not a Phase 12 regression — Redis carries no
credentials. Flagged only so the secret-scan line is on record.
**Fix:** None for v3.3.0. When the auth/secrets boundary lands (v2), move these to user-secrets /
env per the existing `UserSecretsId deferred` note in `BaseApi.Service.csproj:29`.

### IN-02: `appsettings.json` `Service:Version` (`3.2.0`) lags the Phase 12 `v3.3.0` artifact version

**File:** `src/BaseApi.Service/appsettings.json:11`
**Issue:** The `Service.Version` field reads `"3.2.0"`, but Phase 12 artifacts (csproj comments,
`RedisProjectionOptions` doc, close-gate header) describe this as the `v3.3.0` release. If
`Service:Version` is meant to track the release line it is now one minor behind; if it tracks
something else, the relationship is undocumented.
**Fix:** Confirm intent. If it tracks the release line, bump to `3.3.0`. If it intentionally lags
(e.g., last API-contract change), add a one-line comment so the next reviewer does not re-flag it.

### IN-03: `RedisFixture.Multiplexer` initialized with `default!` — NRE risk if a property is read before `InitializeAsync`

**File:** `tests/BaseApi.Tests/Composition/RedisFixture.cs:42`
**Issue:** `public IConnectionMultiplexer Multiplexer { get; private set; } = default!;` suppresses
the nullable warning but offers no runtime guard. `RedisFixtureFacts` constructs a `RedisFixture`
and reads `KeyPrefix` / `ConnectionString` (synchronous, safe) without `InitializeAsync` in three
facts — those are fine because they don't touch `Multiplexer`. But `DisposeAsync` calls
`Multiplexer.GetDatabase()` unconditionally (line 56); if a caller does `new RedisFixture()` then
`await using`/dispose without `InitializeAsync` (not currently done, but the `default!` invites it),
this NREs inside the `try` and the `finally` then NREs again on `Multiplexer.DisposeAsync()`. The
sibling `PostgresFixture` pattern is referenced but this guard is weaker.
**Fix:** Guard `DisposeAsync` against an uninitialized multiplexer:
```csharp
if (Multiplexer is null) return;
```
(change the field to nullable `IConnectionMultiplexer? Multiplexer`) — or document that
`InitializeAsync` is a hard precondition of `DisposeAsync`.

### IN-04: `RedisFixtureFacts.DisposeAsync_With_Residual_..._Throws` is timing-dependent (potential flake)

**File:** `tests/BaseApi.Tests/Composition/RedisFixtureFacts.cs:63-129`
**Issue:** The test deliberately widens the SCAN+DEL→re-SCAN window with 300 pre-seeded keys and a
tight-loop re-seeder so the injected key is "reliably" present at re-SCAN. This is inherently a
race the test is trying to win by probability, not determinism. On a fast machine / small keyspace
the fixture's re-SCAN could complete before the re-seeder writes, producing a spurious GREEN→no-throw
and an `Assert.ThrowsAsync` failure. The close-gate's own 3-GREEN cadence (and the Phase 8 known-flake
retry precedent baked into both scripts) suggests the suite already tolerates flakes, which raises
the cost of adding another timing-sensitive one.
**Fix:** Prefer a deterministic injection: subclass/seam `RedisFixture` so a test hook fires between
the DEL batch and the re-SCAN (e.g., a `protected virtual Task OnBeforeReScanAsync()` the test
overrides to write `residualKey` exactly once). That removes the probabilistic loop entirely. If the
seam is out of scope for v3.3.0, add a `[Trait]` marking it as a known-timing-sensitive fact so the
gate's flake-retry policy explicitly covers it.

### IN-05: `AssertDeadRedisPortIsUnbound` uses blocking `task.Wait(...)` and may leak the connect attempt on timeout

**File:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs:350-377`
**Issue:** The probe does `client.ConnectAsync(...)` then `task.Wait(TimeSpan.FromMilliseconds(500))`.
If the 500ms elapses without a refusal (e.g., a host firewall drops the SYN rather than refusing),
`task.Wait` returns `false`, `client.Connected` is `false`, no exception is thrown, and the method
returns "port unbound" — a soft false-negative that lets the test proceed (acceptable). But the
in-flight `ConnectAsync` Task is abandoned (the `TcpClient` is disposed via `using`, which cancels
it, so no hard leak). The bigger issue is the blocking `.Wait()` inside an otherwise async test
file; on a constrained CI thread pool this can briefly block a pool thread. Minor.
**Fix:** Make the helper async and await with a `CancellationTokenSource` timeout:
`await client.ConnectAsync("localhost", 6379, cts.Token)` inside a `try/catch (OperationCanceledException)`
+ `catch (SocketException ...)`. Keeps the test fully async and removes the `.Wait()` thread-block.

---

_Reviewed: 2026-05-29_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
