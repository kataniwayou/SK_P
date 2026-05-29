---
phase: 12
plan: 01
subsystem: build-dependencies
tags: [redis, cpm, csproj, packages, phase-12]
requires: []
provides:
  - "StackExchange.Redis 2.13.1 CPM pin (Directory.Packages.props)"
  - "Version-less PackageReference in BaseApi.Service.csproj"
  - "Version-less PackageReference in BaseApi.Tests.csproj"
affects:
  - "Plan 12-04 (IConnectionMultiplexer DI registration — package now available)"
  - "Plan 12-05 (RedisFixture per-class multiplexer — package now available)"
tech-stack:
  added:
    - "StackExchange.Redis 2.13.1"
  patterns:
    - "CPM pin (PackageVersion in Directory.Packages.props; Version-less PackageReference in consumers)"
key-files:
  created: []
  modified:
    - Directory.Packages.props
    - src/BaseApi.Service/BaseApi.Service.csproj
    - tests/BaseApi.Tests/BaseApi.Tests.csproj
decisions:
  - "StackExchange.Redis pinned at exactly 2.13.1 (INFRA-REDIS-03 D-lock; not bumped to 2.13.10/2.13.17)"
  - "OBSERV-REDIS-01 enforced by negative-grep: OpenTelemetry.Instrumentation.StackExchangeRedis absent from all csproj/props"
  - "Test reference placed in the runtime PackageReference ItemGroup (alongside NSubstitute), not the analyzer/source-gen group — no PrivateAssets/ExcludeAssets (Singleton client library, not a generator)"
metrics:
  duration: ~6min
  completed: 2026-05-29
---

# Phase 12 Plan 01: StackExchange.Redis CPM Pin & Consumption Summary

StackExchange.Redis 2.13.1 pinned exactly once in Central Package Management and consumed Version-less by both BaseApi.Service (production) and BaseApi.Tests (RedisFixture), unblocking Plans 12-04 and 12-05 at the package-availability level. Zero net-new code; pure dependency wiring. OBSERV-REDIS-01 (no trace-side Redis instrumentation) confirmed across the entire repository.

## What Was Built

### Task 1 — CPM pin (commit 13bdc9d)
Added to `Directory.Packages.props` inside the existing `<ItemGroup>`, before the closing tag:

```xml
    <!-- v3.3.0 / Phase 12 — Redis L2 projection client (CPM pin, INFRA-REDIS-03). -->
    <PackageVersion Include="StackExchange.Redis" Version="2.13.1" />
```

`git diff` showed only this additive change (one comment + one PackageVersion line).

### Task 2 — Version-less consumers (commit 1e316b9)
`src/BaseApi.Service/BaseApi.Service.csproj` (after the `Cronos` reference):

```xml
    <!-- Phase 12 — Redis L2 client. Version pinned in Directory.Packages.props (CPM). -->
    <PackageReference Include="StackExchange.Redis" />
```

`tests/BaseApi.Tests/BaseApi.Tests.csproj` (in the runtime PackageReference ItemGroup, after `NSubstitute`):

```xml
    <!-- Phase 12 — RedisFixture per-class IConnectionMultiplexer + KeyPrefix isolation. -->
    <PackageReference Include="StackExchange.Redis" />
```

Neither carries a `Version=` attribute (CPM enforcement). Regex `Include="StackExchange.Redis".*Version=` returns no match in either file.

### Task 3 — Verification (no file changes; verification-only task)

**`dotnet restore SK_P.sln`** — exit 0; all 3 projects restored; no NU1101/NU1102/NU1605.

**`dotnet list package` resolution proof (2.13.1 at exactly the pinned version):**

```
BaseApi.Service:  > StackExchange.Redis   2.13.1   2.13.1
BaseApi.Tests:    > StackExchange.Redis   2.13.1   2.13.1
```

**`dotnet build SK_P.sln --configuration Release --no-restore`** — `Build succeeded. 0 Warning(s) 0 Error(s)` (TreatWarningsAsErrors=true inherited from Plan 01-01).

**OBSERV-REDIS-01 negative-grep** (across all `*.csproj` + `*.props` in repo) — ZERO matches for:
- `OpenTelemetry.Instrumentation.StackExchangeRedis` (OBSERV-REDIS-01 / Phase 11 D-03 invariant)
- `Microsoft.Extensions.Caching.StackExchangeRedis`
- `AspNetCore.HealthChecks.Redis`
- `Testcontainers.Redis`
- `RedisJSON`
- `NRedisStack`

**HEALTH-01..05 invariant proof** — `git diff src/BaseApi.Core/Health/StartupCompletionService.cs src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` returned EMPTY (byte-immutable per D-05/D-06).

**No EF migration generated** — `src/BaseApi.Service/Migrations/` does not exist (negative schema-migration assertion holds).

## Deviations from Plan

None — plan executed exactly as written. (Task 3's per-task `findstr` verify command returned a non-zero exit on the redirected-output check due to PowerShell/CMD quote handling of `"StackExchange.Redis 2.13.1"`; resolution was confirmed via `dotnet list package` tabular output instead, which is the acceptance-criteria source of truth. No code or content change resulted.)

## Threat Surface

No new security-relevant surface introduced. This plan only pins a package; it composes no connection string (allowAdmin / credential discipline lives in Plans 12-02..12-05). Threat register dispositions T-12-01-01 (Tampering — literal version pin, no floating range) and T-12-01-02/-04 (forbidden-package negative-greps) are all mitigated as planned.

## Self-Check: PASSED

- FOUND: Directory.Packages.props (contains `StackExchange.Redis Version="2.13.1"`)
- FOUND: src/BaseApi.Service/BaseApi.Service.csproj (contains Version-less reference)
- FOUND: tests/BaseApi.Tests/BaseApi.Tests.csproj (contains Version-less reference)
- FOUND commit: 13bdc9d (Task 1)
- FOUND commit: 1e316b9 (Task 2)
