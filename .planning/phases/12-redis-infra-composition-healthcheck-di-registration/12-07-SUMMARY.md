---
phase: 12-redis-infra-composition-healthcheck-di-registration
plan: 07
subsystem: testing
tags: [redis, xunit, composition-facts, yaml-facts, appsettings-facts, di-graph]

# Dependency graph
requires:
  - phase: 12-02
    provides: compose.yaml redis service block (image pin, ports, persistence-disable command, healthcheck)
  - phase: 12-03
    provides: appsettings.json + appsettings.Development.json Redis connection strings + Redis defaults section
  - phase: 12-04
    provides: RedisServiceCollectionExtensions.AddBaseApiRedis (Singleton IConnectionMultiplexer + RedisProjectionOptions binding), AddBaseApi chain call #7
provides:
  - BaseApiCompositionFacts (5 DI-graph facts — Singleton multiplexer, reference-equality, IDatabase-not-registered, chain order, OBSERV-REDIS-01 solution-wide negative-grep)
  - RedisProjectionOptionsBindingFacts (4 IOptions binding facts — KeyPrefix + Serialization.JsonOptions defaults + override flow)
  - ComposeYamlFacts (11 file-content regex facts locking compose.yaml redis block + healthcheck + baseapi-service wiring)
  - AppsettingsFacts (7 file-content regex facts locking the docker-internal/host-side conn-str split + Redis defaults + abortConnect=false PR-review-proof guard)
affects: [12-08-phase-close-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "FindRepoRoot() walk-up to SK_P.sln marker for file-content facts (WR-04 / Plan 03-01 pattern) reused across both new file-fact classes"
    - "Anchor regex on the actual `test:` healthcheck directive (not the service `image:` line) to immunize cadence/CMD-SHELL facts against verbose adjacent comment blocks"

key-files:
  created:
    - tests/BaseApi.Tests/Composition/BaseApiCompositionFacts.cs
    - tests/BaseApi.Tests/Composition/RedisProjectionOptionsBindingFacts.cs
    - tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs
    - tests/BaseApi.Tests/Composition/AppsettingsFacts.cs
  modified: []

key-decisions:
  - "Cadence + CMD-SHELL ComposeYamlFacts re-anchored on the redis-cli ping `test:` array directive instead of an image:-anchored window scan (Rule 1 test-regex fix — the verbose D-01..D-03 comment block + the literal prose 'CMD form (NOT CMD-SHELL)' broke the window-scan approach)"
  - "RedisProjectionOptionsBindingFacts.BuildProvider returns concrete ServiceProvider (not IServiceProvider) so `using var` disposal compiles cleanly"

patterns-established:
  - "File-content fact classes anchor on the unique functional directive, not on surrounding service/section headers, to survive comment churn"

requirements-completed: [INFRA-REDIS-01, INFRA-REDIS-02, INFRA-REDIS-04, INFRA-REDIS-05, INFRA-COMP-01, INFRA-COMP-02, INFRA-COMP-03, INFRA-COMP-04]

# Metrics
duration: ~18min
completed: 2026-05-29
---

# Phase 12 Plan 07: Redis Composition + YAML + Appsettings Facts Summary

**27 new xUnit facts (5 DI-graph + 4 IOptions-binding + 11 compose.yaml regex + 7 appsettings regex) lock the Redis composition surface — Singleton IConnectionMultiplexer, IDatabase-not-registered, chain order, OBSERV-REDIS-01 solution-wide guard, and PR-review-proof compose/appsettings content shapes — at CI level; full suite 177 GREEN.**

## Performance

- **Duration:** ~18 min
- **Started:** 2026-05-29T08:06Z
- **Completed:** 2026-05-29T08:30Z
- **Tasks:** 2
- **Files created:** 4

## Accomplishments
- `BaseApiCompositionFacts` (5 facts): IConnectionMultiplexer Singleton descriptor + reference-equality across scopes (D-14 / Pitfall 1), IDatabase NOT registered (INFRA-COMP-03), AddBaseApiRedis chained after AddBaseApiMapping (INFRA-COMP-01 — literal-aware regex on the composition-root source), solution-wide OBSERV-REDIS-01 negative-grep (zero `OpenTelemetry.Instrumentation.StackExchangeRedis` across all csproj/props, bin/obj excluded).
- `RedisProjectionOptionsBindingFacts` (4 facts): default `KeyPrefix == "skp:"` + `Serialization.JsonOptions == "default"` (D-15) and injected-override flow for both fields through `IOptions<RedisProjectionOptions>`.
- `ComposeYamlFacts` (11 facts): redis 7.4.x-alpine pin, `6380:6379` mapping, `--save ""`/`--appendonly no`, CMD-form `redis-cli ping` healthcheck, cadence 5s/3s/10/5s, `depends_on redis: condition: service_healthy`, `ConnectionStrings__Redis` env, no redisdata volume, no `redis:8.` image, no CMD-SHELL redis-cli healthcheck.
- `AppsettingsFacts` (7 facts): docker-internal `redis:6379` conn str in appsettings.json + host-side `localhost:6380` in appsettings.Development.json, `KeyPrefix: skp:`, `JsonOptions: default`, `abortConnect=false` in BOTH files (PITFALLS P2), no `allowAdmin=true`, no `ssl=true` adjacent to Redis.
- Full suite: **177 GREEN** (150 baseline post-12-06 + 9 Task 1 + 18 Task 2), 0 failed, 0 skipped, zero build warnings.

## Task Commits

1. **Task 1: BaseApiCompositionFacts + RedisProjectionOptionsBindingFacts** - `ceaa724` (test)
2. **Task 2: ComposeYamlFacts + AppsettingsFacts** - `1940acf` (test)

_TDD note: source under test (Plans 12-02/03/04) already shipped, so these fact classes landed GREEN on first run (Task 1) / after a Rule-1 regex fix (Task 2) — RED-by-omission was the prior absence of CI enforcement._

## Files Created/Modified
- `tests/BaseApi.Tests/Composition/BaseApiCompositionFacts.cs` - 5 DI-graph + OBSERV-REDIS-01 facts
- `tests/BaseApi.Tests/Composition/RedisProjectionOptionsBindingFacts.cs` - 4 IOptions<RedisProjectionOptions> binding facts
- `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` - 11 compose.yaml file-content regex facts
- `tests/BaseApi.Tests/Composition/AppsettingsFacts.cs` - 7 appsettings file-content regex facts

## Decisions Made
- `RedisProjectionOptionsBindingFacts.BuildProvider` returns concrete `ServiceProvider` rather than `IServiceProvider` so `using var provider = BuildProvider();` compiles (the plan body used `using var` over an `IServiceProvider`-typed helper, which does not implement `IDisposable`).
- ComposeYamlFacts cadence + CMD-SHELL facts anchor on the redis-cli ping `test:` healthcheck directive instead of the redis service `image:` line (see Deviations).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] ComposeYamlFacts cadence regex window too small (anchored on wrong line)**
- **Found during:** Task 2 (ComposeYamlFacts)
- **Issue:** The plan's `ComposeYaml_Healthcheck_Cadence_Matches_INFRA_REDIS_02` anchored on `redis:\s+image:\s*redis:[\s\S]{0,500}?interval:\s*5s`. The actual compose.yaml carries a ~300-char D-01..D-03 explanatory comment block between the `image:` line and the cadence keys, pushing `interval: 5s` past the 500-char window — `Assert.Matches` failed ("Pattern not found").
- **Fix:** Re-anchored on the unique `test: ["CMD", "redis-cli", "ping"]` directive and walked forward 300 chars to assert `interval: 5s`, `timeout: 3s`, `retries: 10`, `start_period: 5s` (full INFRA-REDIS-02 cadence, now including the timeout assertion).
- **Files modified:** tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs
- **Verification:** Full-suite run 177 GREEN.
- **Committed in:** `1940acf` (Task 2 commit)

**2. [Rule 1 - Bug] ComposeYamlFacts CMD-SHELL negative matched comment prose**
- **Found during:** Task 2 (ComposeYamlFacts)
- **Issue:** The plan's `ComposeYaml_Does_NOT_Use_CmdShell_For_Redis_Healthcheck` used `redis:\s+image:[\s\S]{0,500}?CMD-SHELL[\s\S]{0,200}?redis-cli`. The redis healthcheck comment literally reads `# CMD form (NOT CMD-SHELL) avoids Alpine BusyBox sh quoting hazards` adjacent to `redis-cli` prose, so the window scan matched the comment — `Assert.DoesNotMatch` failed ("Match found") on a false positive.
- **Fix:** Re-targeted the negative to the actual `test:` array directive: `test:\s*\[\s*"CMD-SHELL"[^\]]*redis-cli` — asserts no CMD-SHELL-form healthcheck references redis-cli, ignoring comment prose. The positive CMD-form fact (`ComposeYaml_Has_Redis_PING_Healthcheck_CMD_Form`) still pins the correct form.
- **Files modified:** tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs
- **Verification:** Full-suite run 177 GREEN.
- **Committed in:** `1940acf` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (2 Rule 1 test-regex bugs)
**Impact on plan:** Both fixes preserve the exact INFRA-REDIS-02 / Pitfall-5 invariants the plan intended; they make the regexes robust against the verbose comment block in compose.yaml. No scope creep, no production-code change.

## Issues Encountered
- `dotnet test --filter "FullyQualifiedName~..."` is ignored under Microsoft.Testing.Platform (MTP) — the SDK emits `MTP0001: VSTest-specific properties are set but will be ignored` and runs the whole suite. Verification therefore ran the full 177-fact suite each time rather than a filtered subset; harmless, since the full suite is the phase-close gate anyway.

## HEALTH-01..05 Invariant
- `git diff src/BaseApi.Core/Health/StartupCompletionService.cs` → empty (byte-immutable).
- `git diff src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` → empty (byte-immutable).

## EF Migration
- No EF migration generated (`git status --short src/` shows no migration changes).

## Next Phase Readiness
- Plan 12-08 phase-close gate now has 177 facts (142 baseline + 6 Plan 12-05 RedisFixtureFacts + 2 Plan 12-06 HealthDeadRedis facts + 27 Plan 12-07) to drive the 3-GREEN cadence + SHA-256 BEFORE=AFTER ritual.
- All Phase-12 verification surfaces (DI graph, options binding, compose.yaml content, appsettings content, OBSERV-REDIS-01) are CI-enforced.

## Self-Check: PASSED

- All 4 fact files + SUMMARY.md exist on disk.
- Both task commits (`ceaa724`, `1940acf`) exist in git history.

---
*Phase: 12-redis-infra-composition-healthcheck-di-registration*
*Completed: 2026-05-29*
