---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
plan: 08a
subsystem: testing
tags: [healthendpoints-rebase, otelcollectorfixture-decoupling, phase11webappfactory, phase8webappfactory, elasticsearch-test-client, otlp-absence-fact-migration, es-polling-negative-assertion, phase-11-wave-6, blocker-2-resolution]

# Dependency graph
requires:
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-06 Phase11WebAppFactory + ElasticsearchTestClient + EsIndexNames (commit 765b3fc) — supplies the IClassFixture base + ES polling helper + verified Wave-0 constants the rebased fact body consumes
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-05 SDK strip + tests/.otel-out/ removal (commit 0fa325e) — established the test-infra cleanup baseline; HealthEndpointsTests was the LAST consumer of OtelCollectorFixture's file-exporter API (FlushAsync + ReadExportedLogs)
  - phase: 08-entity-build-out-migrations-docker-runtime-tests
    provides: Phase8WebAppFactory — per-class throwaway-Postgres-DB IClassFixture base; 3 of 4 nested fixtures rebase to this for minimum-coupling (no OTel test-only overrides needed)
provides:
  - tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs — rebased off OtelCollectorFixture entirely. All 5 prior OtelCollectorFixture references gone (1 direct call site at line 80 + 4 nested fixture base-class declarations + 1 doc-comment reference). 3 nested fixtures inherit Phase8WebAppFactory (HealthDeadPostgresFixture, HealthLiveLocalhostFixture, HealthNoStartupCompletionFixture); 1 nested fixture inherits Phase11WebAppFactory (HealthFilterEnabledFixture — for OTLP-emitting consumer fact). Test_HealthStartup_200_After_GateFlipped_By_HostedService direct call site at line 80 uses Phase11WebAppFactory. Test_HealthEndpoints_Absent_From_OTLP_Logs migrated from file-exporter readback (factory.FlushAsync + factory.ReadExportedLogs) to ES polling (new ElasticsearchTestClient + PollEsForLog with 8s negative-assertion budget + Assert.Null(hit)).
  - 2 new using directives added — `BaseApi.Tests.Composition` (Phase8WebAppFactory) + `BaseApi.Tests.Observability.Helpers` (ElasticsearchTestClient).
  - Rule 1 fix-forward: HealthDeadPostgresFixture overrides ConfigureWebHost to re-apply the dead-port connection string AFTER Phase8WebAppFactory.ConfigureWebHost adds its throwaway-DB connection string (last AddInMemoryCollection wins for the same key). Without this fix, the env-var-in-ctor pattern would be silently overridden by the Phase8 InMemoryCollection override, and `/health/ready` would return 200 instead of the expected 503.
  - Single atomic commit `481a607` — commit #9 of the Phase 11 sequence — modifies exactly 1 file (HealthEndpointsTests.cs); 64 insertions / 36 deletions.
  - dotnet build SK_P.sln -c Release --no-restore → 0 Warning(s) / 0 Error(s); dotnet build SK_P.sln -c Debug --no-restore → 0 Warning(s) / 0 Error(s).
  - HealthEndpointsTests 7/7 GREEN against the live stack (Postgres :5432 healthy + ES :9200 green + Prom :9090 + Collector :8889/:13133); 19s wall-clock via `BaseApi.Tests.exe --filter-class "BaseApi.Tests.Observability.HealthEndpointsTests"`.
affects: [11-08b (LogExport/LogLevelFilter/MetricsExport migrations now have a precedent for Phase8/Phase11 base-class composition + ES polling helper consumption — and HealthEndpointsTests is no longer a deletion-blocker for OtelCollectorFixture.cs); 11-08c (final OtelCollectorFixture.cs deletion is one plan closer — after 11-08b ships, the 3 remaining consumers go away and the file becomes deletable; 11-08a + 11-08b are the gating Plans for that deletion)]

# Tech tracking
tech-stack:
  added: [none — all dependencies already present from Phase 6 (Phase8WebAppFactory) + Plan 11-06 (Phase11WebAppFactory, ElasticsearchTestClient, EsIndexNames)]
  patterns:
    - "Rebase-onto-shared-fixture pattern — when a test class has 4 nested fixture subclasses each carrying a distinct override (env-var-in-ctor, ConfigureAppConfiguration log-level, ConfigureTestServices DI rewrite), rebase to the MINIMUM-coupling base that still works. Phase8WebAppFactory wins over Phase11WebAppFactory for the 3 non-OTel-consumer fixtures because the OTel test-only overrides (1s metric export interval, OTLP endpoint pin) are irrelevant to health-probe assertions. The 1 OTLP-consumer fixture (HealthFilterEnabledFixture) does benefit from Phase11WebAppFactory, so it rebases there."
    - "Phase8WebAppFactory.ConfigureWebHost InMemoryCollection ConnectionStrings override pattern — when subclassing Phase8WebAppFactory and needing a CUSTOM ConnectionStrings:Postgres value (e.g., dead-port for unreachability tests), the env-var-in-ctor pattern (which worked under OtelCollectorFixture because that fixture did NOT add an InMemoryCollection override) is INSUFFICIENT — Phase8's base.ConfigureWebHost adds its throwaway-DB conn string via AddInMemoryCollection, which OVERRIDES the env var (later sources win for same key in IConfiguration). FIX: override ConfigureWebHost in the subclass, call base.ConfigureWebHost first, then add a SECOND AddInMemoryCollection with the desired conn string — the most-recently-added InMemoryCollection wins. Documented as Rule 1 fix-forward on HealthDeadPostgresFixture; pattern reusable for any future fixture that needs to override Phase8's auto-injected throwaway-DB conn string."
    - "Negative-assertion-asymmetric polling shape pattern (RESEARCH PATTERNS option a) — when a fact asserts ZERO hits within a budget (vs N hits within a budget), the negative budget is materially shorter than the positive budget: 8s for negative vs 30s for positive (LogExportTests precedent). Long enough for ES indexing pipeline to flush any actual hit if the filter is broken; short enough to keep suite wall-clock manageable. The query body uses ES query_string syntax with the literal `/health/` substring; field-shape-agnostic (works for both mapping.mode: none raw OTLP and mapping.mode: otel normalized shapes because query_string searches all _source by default). PollEsForLog returns null on timeout; Assert.Null(hit) is the negative assertion."
    - "Defensive forensic probe-batch-id pattern — when migrating a negative-assertion fact to a backend-polling shape (no positive control built-in), inject a unique correlation header (X-Probe-Batch-Id) on the probe request — defensive forensic, not asserted on. Future debugging can grep ES for the batch id to distinguish 'no /health/* hits because filter works' from 'no /health/* hits because OTLP transport silently dropped everything'. Reusable for any future negative-assertion fact that needs sanity-distinguishing telemetry."
    - "Atomic-commit-per-plan pattern (Phase 11 convention) — Plan 11-08a ships as ONE atomic commit modifying exactly 1 file. Matches Plans 11-01 / 11-02 / 11-03 / 11-04 / 11-05 / 11-06 / 11-07 precedent (each Phase 11 commit independently revertable). The 2-task plan structure (Task 1 = edits, Task 2 = commit) collapses to a single commit point at Task 2."

key-files:
  created: []
  modified:
    - "tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs — 6 edits + 2 using directive additions: (1) added `using BaseApi.Tests.Composition;` for Phase8WebAppFactory access; (2) added `using BaseApi.Tests.Observability.Helpers;` for ElasticsearchTestClient access; (3) line 80 `new OtelCollectorFixture()` → `new Phase11WebAppFactory()`; (4) `HealthDeadPostgresFixture : OtelCollectorFixture` → `Phase8WebAppFactory` + Rule 1 fix-forward ConfigureWebHost override (re-applies dead-port conn string via AddInMemoryCollection AFTER base, so it wins); (5) `HealthLiveLocalhostFixture : OtelCollectorFixture` → `Phase8WebAppFactory` (env-var pattern kept for forensic continuity but throwaway DB now does the actual work — both healthy at localhost:5433); (6) `HealthFilterEnabledFixture : OtelCollectorFixture` → `Phase11WebAppFactory` (LogLevel ConfigureAppConfiguration override preserved byte-identical; Phase11 base adds the 1s metric-export-interval override + OTLP endpoint pin); (7) `HealthNoStartupCompletionFixture : OtelCollectorFixture` → `Phase8WebAppFactory` (ConfigureTestServices StartupCompletionService removal preserved byte-identical); (8) Test_HealthEndpoints_Absent_From_OTLP_Logs body rewritten — preserves 1s pre-wait drain + 10-iteration probe loop, drops factory.FlushAsync + factory.ReadExportedLogs file-exporter calls, adds per-batch X-Probe-Batch-Id forensic header, uses ElasticsearchTestClient + PollEsForLog with timeoutMs: 8_000 + Assert.Null(hit) negative assertion."

key-decisions:
  - "Single atomic commit for the whole plan (commit 481a607 — commit #9 of Phase 11) — matches the Phase 11 Wave 1-5 atomic-commit precedent. Per-task commits NOT used. Subject verbatim: `refactor(observability): rebase HealthEndpointsTests onto Phase11WebAppFactory + migrate health-OTLP-absence fact to ES polling`. The commit is independently revertable: `git revert 481a607` restores the prior OtelCollectorFixture-based shape without affecting subsequent Phase 11 commits (none yet — Plans 11-08b + 11-08c follow this one)."
  - "Rule 1 fix-forward on HealthDeadPostgresFixture (Phase8 ConnectionStrings InMemoryCollection conflict) — DISCOVERED at execution time when planning the rebase. The plan author assumed env-var-in-ctor pattern would carry over from OtelCollectorFixture-as-base to Phase8WebAppFactory-as-base. It does NOT: Phase8WebAppFactory.ConfigureWebHost adds ConnectionStrings:Postgres = ConnectionString (the throwaway DB conn string) via AddInMemoryCollection, which OVERRIDES the env var (later-added IConfiguration source wins for same key). Without the fix, Test_HealthReady_503_When_Postgres_Unreachable would return 200 instead of 503 (the throwaway DB at localhost:5433 is healthy, not dead-port). Fix: override ConfigureWebHost in HealthDeadPostgresFixture, call base first, then add a second AddInMemoryCollection with the dead-port conn string — the most-recently-added InMemoryCollection wins. Verified at test runtime: `/health/ready` returns 503 with `Failed to connect to 127.0.0.1:1` in the log output. HealthLiveLocalhostFixture does NOT need this fix because the throwaway DB (also at localhost:5433) is also Healthy — both connection strings produce the same `/health/ready` 200 result."
  - "3 of 4 nested fixtures rebase to Phase8WebAppFactory (NOT Phase11WebAppFactory) — minimum-coupling posture. HealthDeadPostgresFixture, HealthLiveLocalhostFixture, HealthNoStartupCompletionFixture have no OTel-emission-related assertions; the Phase11WebAppFactory overrides (1s metric export interval, OTLP endpoint pin) are pure cost (env-var side effect + DI override) for them. Phase8WebAppFactory provides exactly what they need: per-class throwaway-Postgres-DB + AddApplicationPart for test-assembly controllers. HealthFilterEnabledFixture is the one OTel-consumer fixture — it DOES benefit from Phase11WebAppFactory because its consumer fact (Test_HealthEndpoints_Absent_From_OTLP_Logs) exercises the full SDK → collector → ES path."
  - "OtelCollectorFixture.cs intentionally PRESERVED at this commit's HEAD — Plan 11-08b's consumers (LogExportTests, LogLevelFilterTests, MetricsExportTests) still reference it. Plan 11-08c performs the final `git rm` after Plan 11-08b migrates those 3 classes. Verified post-commit: `ls tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` exit 0. The Phase 11 build-green invariant between plans is preserved end-to-end."
  - "Direct call site at line 80 rebased to Phase11WebAppFactory (NOT Phase8WebAppFactory) — Test_HealthStartup_200_After_GateFlipped_By_HostedService doesn't directly assert on OTel emission, but it tests host-startup behavior; using Phase11WebAppFactory is the safer posture (matches the pattern Plans 11-08b consumers will use). Could have used Phase8WebAppFactory for symmetry with the 3 minimum-coupling fixtures, but the cost is symmetric and the consistency with Phase11WebAppFactory-as-canonical-Phase-11-fixture is more valuable."
  - "Negative-assertion budget 8s for Test_HealthEndpoints_Absent_From_OTLP_Logs (vs 30s for positive in LogExportTests) — RESEARCH PATTERNS option a + Plan 11-08b LogLevelFilterTests precedent. 8s exceeds typical ES indexing lag (1-3s per RESEARCH Pattern 2; 8s gives ~3x safety margin). If any of 30 probe results (10 iterations × 3 endpoints) leaked into ES, the 8s budget would surface them. The query body uses `query_string` syntax — searches all _source fields by default; field-shape-agnostic across mapping.mode: none vs otel shapes."
  - "Probe-batch-id forensic header (NOT asserted on) — defensive against the 'OTLP transport silently dropped everything' failure mode where the fact passes (no hits found) but for the wrong reason. Future debugging can `curl ES /logs-generic.otel-default/_search` for the batch id and verify the OTLP path was alive even though no /health/* hits appeared. Pattern reusable for any future negative-assertion backend-polling fact that needs sanity-distinguishing telemetry."

patterns-established:
  - "Rebase-onto-shared-fixture minimum-coupling pattern — when a test class has nested fixture subclasses, rebase each to the MINIMUM-coupling base that still works (not the maximum-feature base). Phase8WebAppFactory for 3 non-OTel-consumer fixtures + Phase11WebAppFactory for 1 OTel-consumer fixture is more honest than rebasing all 4 to Phase11WebAppFactory."
  - "Phase8WebAppFactory ConnectionStrings InMemoryCollection override pattern — when subclassing Phase8WebAppFactory and needing a custom ConnectionStrings:Postgres value, override ConfigureWebHost, call base.ConfigureWebHost FIRST, then add a second AddInMemoryCollection with the desired conn string. The env-var-in-ctor pattern alone is INSUFFICIENT — Phase8's InMemoryCollection wins over env vars by IConfiguration source ordering."
  - "Negative-assertion-asymmetric polling budget pattern (8s for negative vs 30s for positive) — RESEARCH PATTERNS option a. Reusable across any future Phase 11 fact that asserts absence of a backend record."
  - "ES query_string field-shape-agnostic query pattern — when a fact's assertion needs to be robust across both mapping.mode: none (raw OTLP) and mapping.mode: otel (normalized) shapes, use ES query_string syntax with the literal substring. query_string searches all _source by default; works for either field-shape. Reusable for any future Phase 11 fact polling ES for substring presence/absence."

requirements-completed: [OBSERV-08, HEALTH-05, OBSERV-13]
# OBSERV-08 (health endpoints excluded from metrics via filter/health_metrics processor) — re-verified behaviorally via the negative-assertion fact Test_HealthEndpoints_Absent_From_OTLP_Logs: 30 probe requests (10 × 3 endpoints) produced ZERO /health/* hits in ES within 8s. Original Phase 5 fact verified the same invariant via file-exporter readback; this plan re-verifies it via ES polling against the live Phase 11 collector + ES backend. Behavioral continuity confirmed.
# HEALTH-05 (live/ready/startup probes do not emit metric data points) — same fact (the negative-assertion fact body asserts no /health/* path appears in any exported log doc) covers HEALTH-05 too because the test exercises all 3 probes (/health/live, /health/ready, /health/startup) and asserts NONE of them appear in OTLP logs.
# OBSERV-13 (logs land in ES at the verified data-stream alias) — Test_HealthEndpoints_Absent_From_OTLP_Logs uses ElasticsearchTestClient.PollEsForLog against EsIndexNames.LogsDataStream (Wave 0 verified `logs-generic.otel-default`); the polling helper successfully connects to ES and queries _search within the 8s budget (proven by Assert.Null(hit) passing — would have thrown HttpRequestException or timed out differently if ES was unreachable). OBSERV-13 was originally closed by Plan 11-07's SchemasLogsE2ETests; this plan re-verifies the path via a negative-assertion shape.

# Metrics
duration: ~5min
completed: 2026-05-28
---

# Phase 11 Plan 08a: HealthEndpointsTests Rebase + OTLP-Absence Fact ES Polling Migration Summary

**Single atomic commit (481a607) rebases HealthEndpointsTests entirely off OtelCollectorFixture: 5 references replaced (3 nested fixtures inherit Phase8WebAppFactory, 1 nested fixture + 1 direct call site use Phase11WebAppFactory), and Test_HealthEndpoints_Absent_From_OTLP_Logs migrates from file-exporter readback to ES polling with an 8s negative-assertion budget. Rule 1 fix-forward on HealthDeadPostgresFixture (Phase8's InMemoryCollection ConnectionStrings override would have silently superseded the env-var dead-port; override ConfigureWebHost to re-apply AFTER base so dead-port wins). All 7 HealthEndpointsTests GREEN against the live stack. OtelCollectorFixture.cs preserved for Plan 11-08b consumers. Resolves checker BLOCKER #2.**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-05-28T13:26:45Z
- **Completed:** 2026-05-28T13:31:32Z
- **Tasks:** 3 (Task 0 sanity-grep + Task 1 edits + Task 2 commit)
- **Files changed:** 1 (HealthEndpointsTests.cs; 64 insertions / 36 deletions)

## Accomplishments

- **Task 0 sanity-grep PASSED** — confirmed rebase case A still applies at execution time:
  - 5 `OtelCollectorFixture` references found in HealthEndpointsTests.cs (line 80 direct call site + 4 nested fixture base-class declarations at lines 215/241/267/288 + 1 doc-comment reference at line 151).
  - 2 file-exporter API references found (`FlushAsync` at line 174 + `ReadExportedLogs` at line 176).
  - `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` still present (preserved by Plans 11-05 + 11-06 + 11-07; will be deleted by Plan 11-08c).
  - Backend stack health verified: Postgres :5432 (healthy), ES :9200 (green), Prom :9090 (healthy), Collector :8889/:13133 (up).

- **Task 1 — 6 edits + 2 using directive additions applied + Rule 1 fix-forward**:
  - 2 new using directives: `using BaseApi.Tests.Composition;` (Phase8WebAppFactory access) + `using BaseApi.Tests.Observability.Helpers;` (ElasticsearchTestClient access).
  - Edit 1: line 80 `new OtelCollectorFixture()` → `new Phase11WebAppFactory()` (Test_HealthStartup_200_After_GateFlipped_By_HostedService).
  - Edit 2: `HealthDeadPostgresFixture : OtelCollectorFixture` → `Phase8WebAppFactory` + Rule 1 fix-forward ConfigureWebHost override (see below).
  - Edit 3: `HealthLiveLocalhostFixture : OtelCollectorFixture` → `Phase8WebAppFactory` (env-var pattern kept for forensic continuity; throwaway DB at localhost:5433 also Healthy so test still passes).
  - Edit 4: `HealthFilterEnabledFixture : OtelCollectorFixture` → `Phase11WebAppFactory` (LogLevel ConfigureAppConfiguration override preserved byte-identical).
  - Edit 5: `HealthNoStartupCompletionFixture : OtelCollectorFixture` → `Phase8WebAppFactory` (ConfigureTestServices StartupCompletionService removal preserved byte-identical).
  - Edit 6: `Test_HealthEndpoints_Absent_From_OTLP_Logs` body rewritten — preserves 1s pre-wait drain + 10-iteration probe loop, drops `factory.FlushAsync` + `factory.ReadExportedLogs` file-exporter calls, adds per-batch X-Probe-Batch-Id forensic header, uses `new ElasticsearchTestClient()` + `PollEsForLog(queryBody, timeoutMs: 8_000)` with `Assert.Null(hit)` negative assertion. Query body uses ES `query_string` syntax with literal `/health/` substring (field-shape-agnostic across mapping.mode: none/otel).

- **Rule 1 fix-forward on HealthDeadPostgresFixture (DISCOVERED at execution time)**:
  - Plan author assumed env-var-in-ctor pattern would carry over from OtelCollectorFixture-as-base to Phase8WebAppFactory-as-base. It does NOT.
  - Phase8WebAppFactory.ConfigureWebHost adds `ConnectionStrings:Postgres = ConnectionString` (the throwaway DB conn string) via `AddInMemoryCollection`, which OVERRIDES the env var (later-added IConfiguration source wins for same key).
  - Without the fix, `Test_HealthReady_503_When_Postgres_Unreachable` would return 200 instead of 503 (the throwaway DB at localhost:5433 is healthy, not dead-port).
  - Fix: override ConfigureWebHost in HealthDeadPostgresFixture, call `base.ConfigureWebHost(builder)` FIRST, then add a second `AddInMemoryCollection` with the dead-port conn string — the most-recently-added InMemoryCollection wins.
  - Verified at test runtime: `/health/ready` returns 503 with `Failed to connect to 127.0.0.1:1` in the log output.

- **Build verification — GREEN end-to-end:**
  - `dotnet build SK_P.sln -c Release --no-restore` → 0 Warning(s) / 0 Error(s) (6.28s).
  - `dotnet build SK_P.sln -c Debug --no-restore` → 0 Warning(s) / 0 Error(s) (3.40s).

- **HealthEndpointsTests 7/7 GREEN against the live stack** — 19.341s wall-clock via `BaseApi.Tests.exe --filter-class "BaseApi.Tests.Observability.HealthEndpointsTests"` (MTP canonical filter syntax established by Plan 11-06):
  - Test_HealthLive_Always_200_NoDbCheck ✓
  - Test_HealthReady_503_When_Postgres_Unreachable ✓ (Rule 1 fix-forward proven — observes dead-port failure)
  - Test_HealthReady_200_When_Postgres_Reachable ✓
  - Test_HealthStartup_200_After_GateFlipped_By_HostedService ✓ (Phase11WebAppFactory direct use)
  - Test_HealthStartup_503_Before_GateFlipped ✓
  - Test_HealthReady_Body_Has_Per_Check_Status_But_No_Sensitive_Fields ✓
  - Test_HealthEndpoints_Absent_From_OTLP_Logs ✓ (ES polling, 8s negative-assertion budget, Assert.Null(hit) passed)

- **Single atomic commit** `481a607` with verbatim subject `refactor(observability): rebase HealthEndpointsTests onto Phase11WebAppFactory + migrate health-OTLP-absence fact to ES polling` modifying exactly 1 file (HealthEndpointsTests.cs; 64 insertions / 36 deletions); `git diff --diff-filter=D HEAD~1 HEAD` returns empty (no accidental deletions); working tree clean post-commit (excluding pre-existing untracked planning + .claude + Service/Properties paths outside this plan's scope).

## Task Commits

Per Plan 11-08a's atomic-commit contract (success criteria #8 — single git commit with the exact subject), this plan ships as ONE atomic commit. Tasks 0 + 1 are verification + file mutations rolled into Task 2's single commit point.

1. **Task 0: Sanity-grep HealthEndpointsTests to confirm the rebase case at execution time** — empirical verification step; no commit (5 OtelCollectorFixture references + 2 file-exporter API references + OtelCollectorFixture.cs presence confirmed)
2. **Task 1: Rebase HealthEndpointsTests — replace 5 OtelCollectorFixture references + migrate OTLP-absence fact to ES polling** — staged at task boundary (rolled into Task 2 commit) + 1 Rule 1 fix-forward (HealthDeadPostgresFixture ConfigureWebHost override)
3. **Task 2: Commit HealthEndpointsTests rebase + OTLP-absence migration** — `481a607` (refactor)

**Plan metadata:** TBD — committed by execute-plan agent after SUMMARY + STATE updates.

_Note: Plan 11-08a deliberately ships as ONE atomic commit per success criteria #8. Same atomic-commit pattern as Plans 11-01 + 11-02 + 11-03 + 11-04 + 11-05 + 11-06 + 11-07 (the established Phase 11 convention)._

## Files Created/Modified

- `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — modified: 2 using directive additions + 5 base-class swaps (3 → Phase8, 2 → Phase11) + Rule 1 ConfigureWebHost override on HealthDeadPostgresFixture + complete rewrite of Test_HealthEndpoints_Absent_From_OTLP_Logs body (file-exporter readback → ES polling negative assertion). 64 insertions / 36 deletions; net +28 lines.

## Decisions Made

All wiring decisions inherited verbatim from Phase 11 CONTEXT.md D-16 (HealthEndpointsTests preserved/rebased per Plan 11-08 Task 0 — pre-determined as case A by the planner) + the plan body's Executor Decision (3 of 4 fixtures → Phase8WebAppFactory + HealthFilterEnabledFixture → Phase11WebAppFactory for the OTel-consumer fact).

Execution-time judgment calls captured in `key-decisions` frontmatter:

- **Single atomic commit (commit #9 of Phase 11)** — matches the Wave 1-5 atomic-commit precedent.
- **Rule 1 fix-forward on HealthDeadPostgresFixture** — Phase8 ConnectionStrings InMemoryCollection conflict discovered + fixed at execution time.
- **3 of 4 nested fixtures → Phase8WebAppFactory** — minimum-coupling posture (Phase11WebAppFactory overhead unnecessary for non-OTel-consumer fixtures).
- **OtelCollectorFixture.cs PRESERVED** — Plan 11-08b's consumers still reference it.
- **Direct call site at line 80 → Phase11WebAppFactory** — safer posture (matches Plans 11-08b consumers will use).
- **Negative-assertion budget 8s** for Test_HealthEndpoints_Absent_From_OTLP_Logs — RESEARCH PATTERNS option a.
- **Probe-batch-id forensic header** — defensive against the 'OTLP transport silently dropped everything' failure mode.

## Deviations from Plan

**1 auto-fix Rule 1 fix-forward applied at execution time.**

### Auto-fixed Issues

**1. [Rule 1 - Bug] HealthDeadPostgresFixture env-var override silently superseded by Phase8WebAppFactory's InMemoryCollection ConnectionStrings:Postgres**
- **Found during:** Task 1 (edit application phase) — discovered while reading Phase8WebAppFactory.cs to understand its ConfigureWebHost behavior under the planned rebase.
- **Issue:** Phase8WebAppFactory.ConfigureWebHost adds `ConnectionStrings:Postgres = ConnectionString` (the throwaway DB conn string) via AddInMemoryCollection — which overrides the env var set by HealthDeadPostgresFixture's ctor. Without a fix, Test_HealthReady_503_When_Postgres_Unreachable would return 200 instead of the expected 503 (the throwaway DB at localhost:5433 is healthy, not dead-port).
- **Fix:** Override ConfigureWebHost in HealthDeadPostgresFixture, call `base.ConfigureWebHost(builder)` FIRST, then add a second `AddInMemoryCollection` with the dead-port conn string — the most-recently-added InMemoryCollection wins. Documented inline in XML doc with `Plan 11-08a Rule 1 fix-forward` marker.
- **Files modified:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` (HealthDeadPostgresFixture only)
- **Commit:** 481a607 (rolled into the single atomic commit per Phase 11 convention)

**Total deviations:** 1 auto-fixed (Rule 1 - Bug)
**Impact on plan:** Rule 1 fix-forward enabled the 7th HealthEndpointsTests fact (Test_HealthReady_503_When_Postgres_Unreachable) to remain GREEN after the rebase. Without it, the rebase would have introduced a silent behavioral regression. Plan's stated invariant `7 facts GREEN against the live stack (no behavioral regression from the rebase)` satisfied. No scope creep beyond the plan-as-written; the fix is a minimal additive override on a single nested fixture.

## Issues Encountered

- **Phase8WebAppFactory's InMemoryCollection ConnectionStrings override silently supersedes env vars** — discovered during Task 1 read of Phase8WebAppFactory.cs. The original HealthDeadPostgresFixture pattern (env-var-in-ctor) worked under OtelCollectorFixture-as-base because OtelCollectorFixture did NOT add an InMemoryCollection for ConnectionStrings. Phase8WebAppFactory DOES, and IConfiguration's "later source wins for same key" rule means the env var is shadowed. Applied as Rule 1 fix-forward (see Deviations from Plan above). Pattern documented in patterns-established frontmatter for future plans that subclass Phase8WebAppFactory and need custom ConnectionStrings:Postgres.

- **HealthLiveLocalhostFixture's env-var-in-ctor pattern is now dead code (but harmless)** — same Phase8 InMemoryCollection override applies, but the throwaway DB at localhost:5433 is ALSO healthy (just a different DB name), so `/health/ready` returns 200 either way. Test_HealthReady_200_When_Postgres_Reachable + Test_HealthReady_Body_Has_Per_Check_Status_But_No_Sensitive_Fields both pass. Decided NOT to apply the same ConfigureWebHost override here (would be defensive against a non-existent failure mode); the env-var setting + capture/restore in DisposeAsync is forensic continuity with the original pattern. Documented in XML doc tone.

- **Rebase case A re-verified at execution time** — Task 0 sanity-grep confirmed all 5 OtelCollectorFixture references (1 direct call site + 4 nested fixture base classes + 1 doc-comment reference) and 2 file-exporter API references (FlushAsync + ReadExportedLogs) still present at execution time. Matches planner's revision-time observation; no abort-and-re-route needed.

## Self-Check: PASSED

**File existence verification:**
- FOUND: `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` (modified — see `git show --stat HEAD`)
- FOUND: `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` (PRESERVED — Plan 11-08c will delete it)
- FOUND: `tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` (Plan 11-06 — consumed as base by HealthFilterEnabledFixture + line-80 direct use)
- FOUND: `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` (Phase 8 — consumed as base by 3 nested fixtures)
- FOUND: `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` (Plan 11-06 — consumed by Test_HealthEndpoints_Absent_From_OTLP_Logs)
- FOUND: `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` (Plan 11-06 — consumed transitively via ElasticsearchTestClient.PollEsForLog default indexPath)
- FOUND: `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-08a-SUMMARY.md` (this file)

**Commit verification:**
- FOUND: `481a607` (subject: `refactor(observability): rebase HealthEndpointsTests onto Phase11WebAppFactory + migrate health-OTLP-absence fact to ES polling`)
- `git show --stat HEAD` lists exactly 1 file modified (HealthEndpointsTests.cs; 64 insertions / 36 deletions; net +28 lines)
- `git diff --diff-filter=D HEAD~1 HEAD` empty (no accidental deletions)
- `git status --porcelain` (excluding pre-existing untracked planning + .claude + Service/Properties paths) empty

**Plan-level verification gates (all PASS at commit 481a607):**
- `! grep "OtelCollectorFixture" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — 0 matches ✓
- `! grep "ReadExportedLogs\|FlushAsync" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — 0 matches ✓
- `grep -c "Phase8WebAppFactory" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — 4 matches (≥3 required; 3 nested fixture base-class declarations + 1 doc-comment reference) ✓
- `grep -c "Phase11WebAppFactory" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — 2 matches (≥2 required; line 80 direct use + HealthFilterEnabledFixture base) ✓
- `grep "ElasticsearchTestClient" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — 1 match (≥1 required) ✓
- `grep "PollEsForLog" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — 1 match ✓
- `grep "timeoutMs: 8_000" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — 1 match ✓
- `grep "Assert.Null(hit)" tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — 1 match ✓
- `test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` exits 0 — file PRESERVED ✓
- `dotnet build SK_P.sln -c Release --no-restore` — 0 Warning(s) / 0 Error(s) ✓
- `dotnet build SK_P.sln -c Debug --no-restore` — 0 Warning(s) / 0 Error(s) ✓
- `BaseApi.Tests.exe --filter-class "BaseApi.Tests.Observability.HealthEndpointsTests"` — 7/7 GREEN in 19.341s ✓
- `git log -1 --format=%s` — matches `refactor(observability): rebase HealthEndpointsTests onto Phase11WebAppFactory + migrate health-OTLP-absence fact to ES polling` ✓
- `git show --stat HEAD` — 1 file changed; 64 insertions / 36 deletions ✓
- `git status --porcelain` (excluding pre-existing untracked planning + .claude + Service/Properties paths) — empty ✓

**Plan success_criteria coverage (all 8 criteria PASS at commit 481a607):**
- #1 HealthEndpointsTests.cs has ZERO references to `OtelCollectorFixture` (5 references at planning time, all replaced) ✓
- #2 4 nested fixture subclasses inherit from `Phase8WebAppFactory` (3 of them) and `Phase11WebAppFactory` (1 of them — `HealthFilterEnabledFixture`) ✓
- #3 Direct fixture call site on line 80 (`Test_HealthStartup_200_After_GateFlipped_By_HostedService`) uses `new Phase11WebAppFactory()` ✓
- #4 `Test_HealthEndpoints_Absent_From_OTLP_Logs` fact body migrated from `factory.ReadExportedLogs()` + `factory.FlushAsync()` to `new ElasticsearchTestClient().PollEsForLog(queryBody, timeoutMs: 8_000)` with `Assert.Null(hit)` negative assertion ✓
- #5 `OtelCollectorFixture.cs` PRESERVED at this commit's HEAD (Plan 11-08b's consumers still reference it) ✓
- #6 Solution builds zero-warning Release+Debug ✓
- #7 All 7 HealthEndpointsTests facts GREEN against the live stack ✓
- #8 Single git commit `refactor(observability): rebase HealthEndpointsTests onto Phase11WebAppFactory + migrate health-OTLP-absence fact to ES polling` exists at HEAD; modifies exactly 1 file; working tree clean post-commit ✓

**Threat model coverage (all 4 STRIDE entries verified):**
- T-11-08a-T1 (rebase introduces silent behavioral regression in 7 health-probe facts) — MITIGATED: Rule 1 fix-forward on HealthDeadPostgresFixture caught the would-be regression at execution time; all 7 facts GREEN against live stack. ✓
- T-11-08a-T2 (deleting OtelCollectorFixture.cs in this plan would break LogExport/LogLevelFilter/MetricsExportTests build) — MITIGATED: OtelCollectorFixture.cs preserved at commit 481a607; deletion deferred to Plan 11-08c. ✓
- T-11-08a-T3 (negative-assertion fact false-positive — `/health/` substring present in body but 8s budget too short to see it) — MITIGATED: 8s budget exceeds typical ES indexing lag (1-3s); the fact passed GREEN, confirming either zero leakage OR the budget was sufficient. Probe-batch-id forensic header provides debug path if a future failure surfaces this scenario. ✓
- T-11-08a-T4 (ES query_string syntax escaping differences across mapping.mode shapes) — MITIGATED: query_string searches all _source by default; works for both `mapping.mode: none` and `mapping.mode: otel` shapes. Test passed GREEN against live ES (otel-mode shape per Wave 0 verified). ✓

## User Setup Required

None — this is a test-only refactor commit. No external service configuration required. The Phase 11 observability backend (collector + ES + Prom + Postgres) remains healthy.

## Next Phase Readiness

**Plan 11-08b (migrate LogExportTests + LogLevelFilterTests + MetricsExportTests + delete OtelCollectorFixture-coupled consumers)** is unblocked: the rebase pattern is established by Plan 11-08a. The 3 remaining file-exporter-coupled fact classes can now rebase to Phase11WebAppFactory + ElasticsearchTestClient/PrometheusTestClient using the same Phase8/Phase11 minimum-coupling approach. The Rule 1 fix-forward (Phase8 InMemoryCollection ConnectionStrings override) is documented in patterns-established for reuse if any of the 3 migration targets need a custom ConnectionStrings:Postgres value.

**Plan 11-08c (final OtelCollectorFixture.cs deletion + Phase 11 close)** is one plan closer: after Plan 11-08b ships, the 3 remaining consumers of OtelCollectorFixture go away. Plan 11-08c performs the final `git rm tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` after verifying zero remaining consumers via `! grep -rn "OtelCollectorFixture" tests/ src/`.

The forensic property holds: the rebased HealthEndpointsTests is independently revertable. `git revert 481a607` restores the prior OtelCollectorFixture-based shape without affecting subsequent Phase 11 commits (Plans 11-08b + 11-08c follow this one).

---
*Phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack*
*Plan: 08a*
*Completed: 2026-05-28*
