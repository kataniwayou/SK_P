---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
plan: 08b
subsystem: testing
tags: [logexporttests-migration, loglevelfiltertests-migration, metricsexporttests-migration, otelcollectorfixture-orphaning, es-polling, prom-polling, bool-must-query, rule-1-fix-forward-xunit1051, rule-1-fix-forward-multi-log-records, phase-11-wave-6]

# Dependency graph
requires:
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-06 Phase11WebAppFactory + ElasticsearchTestClient + PrometheusTestClient + EsIndexNames (commit 765b3fc) — consumed verbatim by all 3 migrated classes via IClassFixture<Phase11WebAppFactory>, new ElasticsearchTestClient(), new PrometheusTestClient(), and EsIndexNames.CorrelationIdFieldPath / EsIndexNames.LogsDataStream
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-07 SchemasLogsE2ETests + SchemasMetricsE2ETests (commit e3016e2) — established the per-test-unique correlation id + cumulative-cleanliness (>=) + route-template-preservation patterns inherited verbatim by this plan; the http_route empirical discovery applies here too but is not exercised because Plan 11-08b's metrics fact 1 uses the simpler /test-obs/ok route (template == path), and fact 2 uses regex match (which is route-template-shape-agnostic)
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-08a HealthEndpointsTests rebase (commit 481a607) — established the rebase-onto-shared-fixture pattern + Rule 1 Phase8 InMemoryCollection ConnectionStrings precedent (NOT needed here because none of the 3 migrated classes need a custom ConnectionStrings:Postgres value; Phase11WebAppFactory's default throwaway-DB wiring suffices)
  - phase: 05-observability-and-health-probes
    provides: OtelCollectorFixture file-exporter API (FlushAsync, ReadExportedLogs, ReadExportedMetrics, EnumerateMetricNames, EnumerateMetricNodes, GetAllDataPointAttributes) — the file API being REPLACED by ES/Prom polling helpers in this plan
provides:
  - tests/BaseApi.Tests/Observability/LogExportTests.cs — rebased entirely off OtelCollectorFixture. 2 facts using Phase11WebAppFactory + ElasticsearchTestClient + EsIndexNames.CorrelationIdFieldPath; bool/must ES query (corrId term + body.text match_phrase) per Rule 1 fix-forward (single request emits multiple log records sharing the corrId scope); version assertion DROPPED per checker WARNING #7. Both facts GREEN.
  - tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs — rebased entirely off OtelCollectorFixture. 2 facts using direct new Phase11WebAppFactory(logLevelDefaultOverride: "Warning") (internal 1-arg ctor). Positive control fact 30s budget + negative-assertion fact 8s budget — asymmetric polling shape per PATTERNS option a + Plan 11-08a precedent. Both facts GREEN.
  - tests/BaseApi.Tests/Observability/MetricsExportTests.cs — rebased entirely off OtelCollectorFixture. 3 facts using Phase11WebAppFactory + PrometheusTestClient + Prom-form metric names per RESEARCH Pitfall 1. OBSERV-08 + D-04 health-filter invariant preserved via http_route=~".*health.*" PromQL regex match (strict empty assertion); runtime metric fact accepts either dotnet_* or process_runtime_dotnet_* family. All 3 facts GREEN.
  - Single atomic commit `c40d062` — commit #10 of the Phase 11 sequence — modifies exactly 3 files; 209 insertions / 270 deletions; net -61 lines (the new ES/Prom helper consumption is shorter than the prior file-exporter JSON traversal helpers).
  - Empirical proof that all 3 Phase 5 file-exporter-coupled fact classes work end-to-end via Phase 11 backend polling; closes OBSERV-13 + OBSERV-14 behaviorally for the LogExportTests + LogLevelFilterTests + MetricsExportTests subset (Plan 11-07 already closed them for the new SchemasLogsE2ETests + SchemasMetricsE2ETests subset).
  - OtelCollectorFixture.cs PRESERVED at this commit's HEAD but now fully orphaned (zero consumers across LogExportTests + LogLevelFilterTests + MetricsExportTests + HealthEndpointsTests — the 4 historical consumers from Phase 5). Plan 11-08c performs the final git rm.
affects: [11-08c (Phase 11 close — the final git rm tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs becomes a clean defensive-grep-then-delete operation after this plan; 3-consecutive-GREEN cadence + psql \l SHA-256 BEFORE/AFTER + SUMMARY narrative all become the closing work; Plan 11-08c also responsible for OtelEndOfSuiteCleanup deletion if not already done by Plan 11-05 — verified earlier this commit it was already cleaned up by Plan 11-05)]

# Tech tracking
tech-stack:
  added: [none — all dependencies already present from Plan 11-06 (Phase11WebAppFactory, ElasticsearchTestClient, PrometheusTestClient, EsIndexNames) + xUnit v3 + System.Text.Json + Microsoft.AspNetCore.Mvc.Testing]
  patterns:
    - "Bool/must ES query for multi-log-record-per-request disambiguation pattern (Rule 1 empirical discovery) — a single sk_p HTTP request emits MULTIPLE log records sharing the same X-Correlation-Id scope: the controller's LogInformation(\"test-obs ok ran\") PLUS the Microsoft.AspNetCore.Hosting.Diagnostics framework logs (\"Request started\" + \"Request finished\" — both at Information severity by default). The ES `term` filter on `attributes.CorrelationId` returns ALL of them; PollEsForLog returns hits[0] which may be a framework log NOT carrying the controller's body text. Fix: bool/must query combining (a) corrId term filter for per-test isolation AND (b) body.text match_phrase for record disambiguation. Pattern is reusable for any future ES-polling fact whose assertion targets a specific log message within a scope-shared cluster of records."
    - "xUnit1051 CancellationToken propagation pattern (Rule 1 — TreatWarningsAsErrors escalation) — under `TreatWarningsAsErrors=true` (Directory.Build.props global), xUnit v3 3.2.2's xUnit1051 analyzer escalates to ERROR for any async call site that accepts a CancellationToken parameter without passing `TestContext.Current.CancellationToken`. Specifically: `await Task.Delay(15_000)` is build-fatal; the fix is `await Task.Delay(15_000, ct)` where ct = TestContext.Current.CancellationToken. Same pattern as Plan 03-02 deviation (SchemaTests + AuditInterceptorTests). Reusable across the codebase for any future xUnit v3 test that issues async waits or HTTP calls."
    - "Educational-rephrase pattern for doc-comment historical references (Plan 06-01 / 08-01 / 10-02 / 11-04 / 11-07 precedent extended) — plan-as-written body for LogLevelFilterTests doc comment contained the literal string `<c>OtelCollectorFixture</c>` (XML cref-form reference) which would have tripped the plan's own verification gate `! grep \"OtelCollectorFixture\" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs`. Resolved by rephrasing the doc comment to convey the same migration narrative without the cref-form reference: 'Migration: was Phase 5 file-exporter + per-test fixture instances (deleted by Plan 11-05 / 11-08c).' Educational content preserved; literal grep gate satisfied; semantic must_haves invariant satisfied (no CODE references to OtelCollectorFixture)."
    - "Multi-record-tolerant negative-assertion pattern — LogLevelFilterTests.Test_Information_Log_Suppressed_When_Default_Warning uses corrId-only term filter (no body.text match_phrase) because the negative assertion is `Assert.Null(hit)`. Under Logging:LogLevel:Default=Warning, ALL Information logs are filtered (controller's LogInformation + framework's Hosting.Diagnostics Request started/finished — both at Information severity). Any hit at all (whether controller or framework) would surface a regression in the MEL filter discipline. The bool/must body filter would NARROW the result set (making null more likely even on regression); corrId-only is the safer shape for a negative assertion."
    - "PromQL regex match for health-route exclusion pattern (D-04 invariant) — MetricsExportTests.Test_HealthPath_Absent_From_HttpServerMetrics uses `http_route=~\".*health.*\"` (PromQL regex match operator) instead of an exact label value, so the assertion is robust to whatever the live ASP.NET Core route template emits for /health/* (`health/live`, `/health/live`, `{health-route}/live`, etc.). Combined with strict empty assertion + 15s pre-wait for Prom scrape cycle, this satisfies the D-04 contract that the collector's filter/health_metrics processor drops health-route data points BEFORE the Prom exporter."
    - "Runtime-metric naming-era tolerance pattern (D-16 generalized) — MetricsExportTests.Test_RuntimeMetric_ProcessRuntimeDotnet_Exported queries both `dotnet_*` (newer System.Runtime semantic conventions) AND `process_runtime_dotnet_*` (older OpenTelemetry.Instrumentation.Runtime 1.15.0 names) regex families because the SDK's runtime metric naming has changed across versions. Either family present satisfies the test. Reusable for any future OTel SDK fact whose metric names may drift across instrumentation library version bumps."
    - "Atomic-commit-per-plan pattern (Phase 11 convention) — Plan 11-08b ships as ONE atomic commit modifying exactly 3 files. Matches Plans 11-01 / 11-02 / 11-03 / 11-04 / 11-05 / 11-06 / 11-07 / 11-08a precedent (each Phase 11 commit independently revertable). The 4-task plan structure (Tasks 1-3 = file rewrites, Task 4 = commit) collapses to a single commit point at Task 4."

key-files:
  created: []
  modified:
    - "tests/BaseApi.Tests/Observability/LogExportTests.cs — wholesale rewrite (139 lines → 102 lines net). Replaces IClassFixture<OtelCollectorFixture> + 4 helper-method-coupled traversals (GetLogRecords + manual attribute iteration + resourceLogs walk) with IClassFixture<Phase11WebAppFactory> + 2 ElasticsearchTestClient.PollEsForLog calls. Each [Fact] body uses bool/must ES query (corrId term + body.text match_phrase) per Rule 1 fix-forward; assertions on raw _source JSON contain (test-obs ok ran + corrId + sk-api); service.version assertion dropped per checker WARNING #7. T-05-PII-INJECT regression assertions preserved (no \\\\r, no injected substring). Adds [Trait(\"Phase\", \"11\")]."
    - "tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs — wholesale rewrite (105 lines → 85 lines net). Replaces `new OtelCollectorFixture(connectionString: null, logLevelDefaultOverride: \"Warning\")` with `new Phase11WebAppFactory(logLevelDefaultOverride: \"Warning\")` (internal 1-arg ctor — same assembly so accessible). Replaces 2 helper-method-coupled telemetry.jsonl traversals with 2 ElasticsearchTestClient.PollEsForLog calls. Positive control uses 30s budget; negative-assertion uses 8s budget (PATTERNS option a). Adds [Trait(\"Phase\", \"11\")]. Doc-comment educational-rephrase: `per-test <c>OtelCollectorFixture</c>` → `per-test fixture instances` so plan's literal grep gate passes."
    - "tests/BaseApi.Tests/Observability/MetricsExportTests.cs — wholesale rewrite (149 lines → 115 lines net). Replaces IClassFixture<OtelCollectorFixture> + 3 helper-method-coupled OTLP metric traversals (EnumerateMetricNames + EnumerateMetricNodes + GetAllDataPointAttributes) with IClassFixture<Phase11WebAppFactory> + PrometheusTestClient. Metric names translated from OTLP form to Prom form per Pitfall 1: `http.server.request.duration` → `http_server_request_duration_seconds_count`. D-04 health-filter invariant preserved via `http_route=~\".*health.*\"` regex with strict empty assertion (15s pre-wait for Prom scrape cycle). Runtime metric fact accepts EITHER `dotnet_*` OR `process_runtime_dotnet_*` family per D-16 generalization. Adds [Trait(\"Phase\", \"11\")]. Rule 1 fix-forward: 2x `Task.Delay(15_000)` calls now pass `TestContext.Current.CancellationToken` per xUnit1051 analyzer escalation."

key-decisions:
  - "Single atomic commit (commit #10 of Phase 11) — matches the Phase 11 Wave 1-5 atomic-commit precedent. Subject verbatim: `test(observability): migrate LogExportTests + LogLevelFilterTests + MetricsExportTests to ES/Prom polling`. The commit is independently revertable: `git revert c40d062` restores the prior OtelCollectorFixture-based shape without affecting subsequent Phase 11 commits (Plan 11-08c follows this one)."
  - "Rule 1 fix-forward on LogExportTests bool/must ES query (DISCOVERED at execution time) — plan-as-written used a simple term-only ES query on `attributes.CorrelationId`; first verification run failed with `Assert.Contains(\"test-obs ok ran\", rawJson)` because PollEsForLog returned a Microsoft.AspNetCore.Hosting.Diagnostics framework log (Request started/finished) that shares the same corrId scope but carries a different body.text. Fix: bool/must query combining the corrId term filter AND a `body.text` match_phrase for record disambiguation. Verified empirically: 2/2 facts GREEN post-fix. Same pattern applied defensively to the second fact (sanitization) for consistency. The plan's behavioral invariant (corrId surfaces through OTel + log doc lands in ES) is preserved; only the query shape was tightened."
  - "Rule 1 fix-forward on MetricsExportTests xUnit1051 — 2x `await Task.Delay(15_000)` calls in fact 2 + fact 3 tripped xUnit1051 analyzer escalated to ERROR via Directory.Build.props TreatWarningsAsErrors=true. Fix: pass `ct = TestContext.Current.CancellationToken` to both. Same pattern as Plan 03-02 deviation. Plan-as-written body did not include this knob; build was RED until the fix landed. Classified Rule 1 (bug — the build fatally rejects the plan-as-written code under the project's enforced analyzer ruleset)."
  - "Educational-rephrase on LogLevelFilterTests doc comment (Plan 06-01 / 08-01 / 10-02 / 11-04 / 11-07 precedent) — plan-as-written line 287 contained the literal `per-test <c>OtelCollectorFixture</c>` XML cref reference, which would have tripped the plan's own verification gate `! grep \"OtelCollectorFixture\" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` (1 match instead of 0). Rephrased the doc comment to convey the same migration narrative without the cref-form reference. Educational content preserved; literal grep gate satisfied; semantic must_haves invariant satisfied (no CODE references to OtelCollectorFixture)."
  - "Negative-assertion fact (LogLevelFilterTests.Test_Information_Log_Suppressed_When_Default_Warning) intentionally uses corrId-only term filter — NO body.text match_phrase. Reasoning: under Logging:LogLevel:Default=Warning, ALL Information logs are MEL-filtered (controller's + framework's Hosting.Diagnostics) BEFORE reaching OTel. Any leaked hit at all (whether controller or framework) would surface a regression. Adding the body filter would NARROW the result set, making `Assert.Null(hit)` more likely to pass even on regression — defeats the test's purpose. Different shape from the positive control + LogExportTests bool/must pattern by design."
  - "service.version assertion DROPPED in LogExportTests per checker WARNING #7 (Plan 11-07 precedent) — Assert.Contains(\"sk-api\") is the load-bearing service.name probe (D-07 resource_to_telemetry_conversion: true makes service.name the load-bearing label); hardcoding `service.version=3.2.0` would couple the fact to appsettings.json Service.Version and break the test on any future version bump without any observability-behavior change. service.name is load-bearing; version is incidental. Same reasoning Plan 11-07 SchemasLogsE2ETests applied verbatim."
  - "OtelCollectorFixture.cs intentionally PRESERVED at this commit's HEAD — Plan 11-08c performs the final `git rm` after this plan ships. The file becomes ORPHANED at this commit (zero consumers across LogExportTests + LogLevelFilterTests + MetricsExportTests + HealthEndpointsTests — the 4 historical consumers from Phase 5). Verified post-commit: `grep -rn \"OtelCollectorFixture\" tests/BaseApi.Tests/Observability/*Tests.cs` returns 0 matches; `ls tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` exits 0. Phase 11 build-green invariant between plans preserved end-to-end."

patterns-established:
  - "Bool/must ES query pattern for multi-log-record-per-request disambiguation — when an ES polling fact asserts on a specific controller log message inside a scope-shared cluster of records (controller + framework Hosting.Diagnostics + middleware), use a bool/must query combining the corrId term filter AND a body.text match_phrase for the controller's specific message. Reusable across any future ES-polling fact whose assertion targets a specific log message within a scope-shared cluster."
  - "xUnit1051 CancellationToken propagation pattern — under TreatWarningsAsErrors=true, every async call site with a CancellationToken parameter MUST pass TestContext.Current.CancellationToken. Specifically Task.Delay(N) → Task.Delay(N, ct). Same pattern as Plan 03-02. Reusable across the codebase."
  - "Educational-rephrase pattern for doc-comment historical references (extending Plan 06-01 / 08-01 / 10-02 / 11-04 / 11-07 precedent to migration-narrative doc comments) — when a plan body's prescribed doc comment includes a cref-form reference to a class that the plan's own grep-empty verification gate forbids, rephrase the comment to convey the same migration narrative without the literal cref form. Educational content preserved; literal grep gate satisfied."
  - "Multi-record-tolerant negative-assertion pattern — when a negative-assertion fact (Assert.Null(hit) / Assert.Empty(samples)) is used for a MEL-filter or backend-filter regression test, use the BROADEST query that the regression would trip — adding more filters narrows the result set + makes the negative assertion pass for the wrong reason. Reusable for any future absence-of-X negative-assertion fact."
  - "PromQL regex match for D-04 health-filter invariant — `http_route=~\".*health.*\"` (regex match) is shape-agnostic to whatever the live ASP.NET Core route template emits for /health/*. Combined with strict empty assertion + 15s pre-wait for Prom scrape cycle, this satisfies the D-04 contract that the collector's filter/health_metrics processor drops health-route data points BEFORE the Prom exporter. Reusable for any future Phase 11 fact asserting filter-processor coverage."
  - "Runtime-metric naming-era tolerance pattern (D-16 generalized) — query both `dotnet_*` AND `process_runtime_dotnet_*` regex families with PromQL `{__name__=~\"pattern\"}` shape and accept either presence. SDK runtime metric naming has changed across instrumentation library versions; testing for ANY runtime metric satisfies the OBSERV-03 invariant without coupling to a specific naming era."

requirements-completed: [OBSERV-03, OBSERV-06, OBSERV-08, OBSERV-13, OBSERV-14]
# OBSERV-03 (HTTP server + client metrics via OTel SDK) — closed behaviorally by MetricsExportTests.Test_HttpServerRequestDuration_Present_For_App_Endpoint: drives 5 GETs to /test-obs/ok, polls Prom via PollPrometheusUntilSumAtLeast for http_server_request_duration_seconds_count{service_name="sk-api",http_route="test-obs/ok"} with threshold 5; asserts samples non-empty AND SumSampleValues >= 5. The D-07 resource_to_telemetry_conversion: true setting makes service_name appear; the http_route label uses /test-obs/ok which (per Plan 11-07 discovery) is the route-template literal (template == path for non-versioned controllers without [controller] token).
# OBSERV-06 (Logging:LogLevel:Default filters BOTH console + OTel sinks identically) — closed behaviorally by LogLevelFilterTests both facts: positive control (default Information) sees the hit in ES; negative (override to Warning) sees NO hit in ES within 8s budget. Proves the single MEL filter path (Pitfall 9) — filter runs BEFORE both sinks, so both behave identically.
# OBSERV-08 (health endpoints excluded from metrics via filter/health_metrics processor) — closed behaviorally by MetricsExportTests.Test_HealthPath_Absent_From_HttpServerMetrics: 10 probe hits to /health/live, then strict empty Prom query for http_server_request_duration_seconds_count{service_name="sk-api",http_route=~".*health.*"} after 15s pre-wait. The collector's filter/health_metrics processor drops health-route data points BEFORE the Prom exporter; empty result vector confirms the filter is active. Also closed by Plan 11-08a's HealthEndpointsTests.Test_HealthEndpoints_Absent_From_OTLP_Logs (logs-side analog).
# OBSERV-13 (logs land in ES at the verified data-stream alias with OTLP field shape) — re-closed by LogExportTests both facts via ElasticsearchTestClient.PollEsForLog against EsIndexNames.LogsDataStream + EsIndexNames.CorrelationIdFieldPath; the bool/must body.text match_phrase confirms the OTLP-otel field shape is preserved end-to-end. Originally closed by Plan 11-07 SchemasLogsE2ETests; this plan re-verifies via the Phase 5 baseline test surface.
# OBSERV-14 (HTTP server metrics scraped by Prometheus from otel-collector:8889 with service_name="sk-api" label) — re-closed by MetricsExportTests via PrometheusTestClient consumption (BaseAddress http://localhost:9090/, EscapeDataString-protected PromQL, mandatory 15s pre-wait). Originally closed by Plan 11-07 SchemasMetricsE2ETests; this plan re-verifies via the Phase 5 baseline test surface.

# Metrics
duration: ~11min
completed: 2026-05-28
---

# Phase 11 Plan 08b: LogExportTests + LogLevelFilterTests + MetricsExportTests Migration Summary

**Single atomic commit `c40d062` migrates the 3 Phase 5 file-exporter-coupled fact classes off OtelCollectorFixture: LogExportTests (2 facts) + LogLevelFilterTests (2 facts) → ES polling via Phase11WebAppFactory + ElasticsearchTestClient; MetricsExportTests (3 facts) → Prom polling via Phase11WebAppFactory + PrometheusTestClient. Two Rule 1 fix-forwards at execution time: (a) bool/must ES query for multi-log-record-per-request disambiguation (controller's "test-obs ok ran" log shares corrId scope with framework Hosting.Diagnostics Request started/finished logs; term-only filter returned the wrong hit); (b) xUnit1051 CancellationToken propagation on 2x `Task.Delay(15_000)` calls (TreatWarningsAsErrors escalates analyzer to ERROR). One educational-rephrase to LogLevelFilterTests doc comment to satisfy the plan's own literal grep gate. All 7 facts (2+2+3) GREEN against the live stack in a combined run (~1m 44s). OtelCollectorFixture.cs PRESERVED on disk but now fully ORPHANED (zero consumers across the 4 historical Phase 5 test classes); Plan 11-08c performs the final git rm. Closes OBSERV-03 + OBSERV-06 + OBSERV-08 + OBSERV-13 + OBSERV-14 behaviorally for the migrated subset.**

## Performance

- **Duration:** ~11 min (includes 3 wholesale rewrites + 2 Rule 1 fix-forward iterations + 1 educational-rephrase + 3 build cycles + 5 test runs + commit)
- **Started:** 2026-05-28T13:38:10Z
- **Completed:** 2026-05-28T13:48:52Z
- **Tasks:** 4 (Task 1 LogExport + Task 2 LogLevelFilter + Task 3 Metrics + Task 4 atomic commit)
- **Files changed:** 3 (all modified in place)

## Accomplishments

- **Task 1 LogExportTests.cs migrated** as `public sealed class : IClassFixture<Phase11WebAppFactory>` with `[Trait("Phase", "11")]` + `[Collection("Observability")]`:
  - **Fact 1 (Test_LogRecord_Has_CorrelationId_And_ServiceResource):** GETs `/test-obs/ok`, captures corrId from response header (Phase 4 OBSERV-11 echo), builds bool/must ES query (corrId term + body.text match_phrase for "test-obs ok ran"), polls via PollEsForLog 30s budget; asserts hit non-null + raw JSON contains "test-obs ok ran" + corrId + "sk-api". Version assertion DROPPED per checker WARNING #7.
  - **Fact 2 (Test_LogRecord_CorrelationId_Survives_Sanitization):** GETs `/test-obs/ok` with malformed `X-Correlation-Id: invalid\rinjected` via TryAddWithoutValidation; captures SANITIZED 32-hex from response header; same bool/must ES query pattern; T-05-PII-INJECT regression assertions preserved (no `\\r`, no "injected" anywhere in doc).
- **Task 2 LogLevelFilterTests.cs migrated** with same trait/collection decorations (no IClassFixture — each fact instantiates its own factory via internal 1-arg ctor):
  - **Fact 1 (Test_Information_Log_Suppressed_When_Default_Warning):** `await using var factory = new Phase11WebAppFactory(logLevelDefaultOverride: "Warning")`; per-test unique Guid:N corrId; GETs `/test-obs/ok`; polls ES via 8s budget; `Assert.Null(hit)` proves MEL filter suppresses Information logs before OTel.
  - **Fact 2 (Test_Information_Log_Present_When_Default_Information):** `new Phase11WebAppFactory()` (parameterless ctor — default Information); same GET + per-test corrId pattern; polls ES via 30s budget; `Assert.NotNull(hit)` proves positive control. Asymmetric 30s/8s polling shape per PATTERNS option a + Plan 11-08a precedent.
- **Task 3 MetricsExportTests.cs migrated** as `public sealed class : IClassFixture<Phase11WebAppFactory>` with same trait/collection decorations:
  - **Fact 1 (Test_HttpServerRequestDuration_Present_For_App_Endpoint):** 5 GETs to `/test-obs/ok`; PromQL `http_server_request_duration_seconds_count{service_name="sk-api",http_route="test-obs/ok"}`; PollPrometheusUntilSumAtLeast threshold 5; asserts samples non-empty + SumSampleValues >= 5.
  - **Fact 2 (Test_HealthPath_Absent_From_HttpServerMetrics):** 10 GETs to `/health/live`; 15s pre-wait for Prom scrape cycle; QueryPrometheus with `http_server_request_duration_seconds_count{service_name="sk-api",http_route=~".*health.*"}` regex match; `Assert.Empty(samples)` proves D-04 invariant (collector's filter/health_metrics processor drops health-route data points BEFORE Prom exporter).
  - **Fact 3 (Test_RuntimeMetric_ProcessRuntimeDotnet_Exported):** 1 warmup request; 15s pre-wait; queries both `{__name__=~"dotnet_.*"}` AND `{__name__=~"process_runtime_dotnet_.*"}`; asserts at least ONE family non-empty (D-16 naming-era tolerance).
- **2 Rule 1 fix-forwards applied at execution time** (see Deviations from Plan below).
- **1 educational-rephrase** to LogLevelFilterTests doc comment (`per-test <c>OtelCollectorFixture</c>` → `per-test fixture instances`) so the plan's own literal grep gate passes (Plan 06-01 / 08-01 / 10-02 / 11-04 / 11-07 precedent extended to migration-narrative doc comments).
- **Build verification — GREEN end-to-end** (post-Rule-1-fix iteration):
  - `dotnet build SK_P.sln -c Release --no-restore` → 0 Warning(s) / 0 Error(s) (1.21s).
  - `dotnet build SK_P.sln -c Debug --no-restore` → 0 Warning(s) / 0 Error(s) (1.65s).
- **All 7 migrated facts GREEN against the live stack** (combined run via `BaseApi.Tests.exe --filter-class LogExportTests --filter-class LogLevelFilterTests --filter-class MetricsExportTests`):
  - Combined wall-clock: ~1m 44s. Per-class durations: LogExportTests 2/2 in ~34s; LogLevelFilterTests 2/2 in ~26s; MetricsExportTests 3/3 in ~47s.
  - Live stack at execution time: Postgres :5433 (healthy), ES :9200 (green), Prom :9090 (healthy), Collector :8889/:13133 (up).
- **Single atomic commit `c40d062`** with verbatim subject `test(observability): migrate LogExportTests + LogLevelFilterTests + MetricsExportTests to ES/Prom polling` modifying exactly 3 files (209 insertions / 270 deletions; net -61 lines). `git diff --diff-filter=D HEAD~1 HEAD` empty (no accidental deletions); working tree clean for tracked files post-commit; OtelCollectorFixture.cs preserved on disk.

## Task Commits

Per Plan 11-08b's atomic-commit contract (Task 4 prescribes ONE atomic commit modifying exactly 3 files), this plan ships as ONE commit:

1. **Task 1: Migrate LogExportTests.cs to ES polling (+ Rule 1 bool/must fix-forward)** — staged at task boundary (rolled into Task 4 commit)
2. **Task 2: Migrate LogLevelFilterTests.cs (+ educational-rephrase to satisfy literal grep gate)** — staged at task boundary (rolled into Task 4 commit)
3. **Task 3: Migrate MetricsExportTests.cs to Prom polling (+ Rule 1 xUnit1051 fix-forward)** — staged at task boundary (rolled into Task 4 commit)
4. **Task 4: Single atomic commit** — `c40d062` (test)

**Plan metadata:** TBD — committed by execute-plan agent after SUMMARY + STATE updates.

_Note: Plan 11-08b deliberately ships as ONE atomic commit per Task 4's prescribed message + 3-file scope. Same atomic-commit pattern as Plans 11-01 through 11-08a (the established Phase 11 convention)._

## Files Created/Modified

- `tests/BaseApi.Tests/Observability/LogExportTests.cs` — wholesale rewrite (139 → 102 lines net). 2 facts using Phase11WebAppFactory + ElasticsearchTestClient + bool/must ES query (corrId term + body.text match_phrase). Adds [Trait("Phase", "11")]; drops version assertion per checker WARNING #7.
- `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` — wholesale rewrite (105 → 85 lines net). 2 facts using direct `new Phase11WebAppFactory(logLevelDefaultOverride:)` (internal 1-arg ctor). Asymmetric 30s positive / 8s negative budget. Adds [Trait("Phase", "11")]; doc-comment educational-rephrase.
- `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` — wholesale rewrite (149 → 115 lines net). 3 facts using Phase11WebAppFactory + PrometheusTestClient. Prom-form metric names per Pitfall 1. D-04 invariant preserved via PromQL regex match + strict empty assertion. Adds [Trait("Phase", "11")]; 2x Rule 1 fix-forward `Task.Delay(N)` → `Task.Delay(N, ct)`.

## Decisions Made

Execution-time judgment calls (captured in `key-decisions` frontmatter):

- **Single atomic commit (commit #10 of Phase 11)** — matches Phase 11 Wave 1-5 atomic-commit precedent.
- **Rule 1 fix-forward bool/must ES query** — multi-log-record-per-request disambiguation pattern discovered empirically.
- **Rule 1 fix-forward xUnit1051 CancellationToken** — same pattern as Plan 03-02; build was RED until fix landed.
- **Educational-rephrase on LogLevelFilterTests doc comment** — Plan 06-01 / 08-01 / 10-02 / 11-04 / 11-07 precedent extended.
- **Negative-assertion fact uses corrId-only filter (no body filter)** — bool/must would narrow result set + mask regressions.
- **Version assertion DROPPED in LogExportTests per checker WARNING #7** — service.name is load-bearing; version is incidental.
- **OtelCollectorFixture.cs PRESERVED** — Plan 11-08c performs the final git rm after this plan + Plan 11-08a + the new test classes are all in place.

## Deviations from Plan

**2 auto-fix Rule 1 fix-forwards applied at execution time + 1 educational-rephrase.**

### Auto-fixed Issues

**1. [Rule 1 - Bug] LogExportTests bool/must ES query for multi-log-record-per-request disambiguation**

- **Found during:** Task 1 verification cycle — first verification run of LogExportTests failed with `Assert.Contains("test-obs ok ran", rawJson)` because PollEsForLog returned a `Microsoft.AspNetCore.Hosting.Diagnostics` framework log (Request started/finished) that shares the same corrId scope but carries a different body.text.
- **Issue:** Plan-as-written used a simple `{"query":{"term":{"attributes.CorrelationId":"<corrId>"}}}` ES query. Empirical verification against the live stack (`curl http://localhost:9200/logs-generic.otel-default/_search`) confirmed that a single sk_p HTTP request emits MULTIPLE log records sharing the same X-Correlation-Id scope:
  - Controller's `LogInformation("test-obs ok ran")` → body.text="test-obs ok ran", scope.name="BaseApi.Tests.Observability.TestObservabilityController"
  - Framework Hosting.Diagnostics → body.text="Request starting HTTP/1.1 GET ..." + "Request finished HTTP/1.1 GET ... - 200 ..."
  PollEsForLog returns `hits[0]`, which is order-of-arrival dependent and may be ANY of those records.
- **Fix:** Combine the corrId term filter with a `body.text` match_phrase clause via `bool/must` query:
  ```json
  {"size":10,"query":{"bool":{"must":[
    {"term":{"attributes.CorrelationId":"<corrId>"}},
    {"match_phrase":{"body.text":"test-obs ok ran"}}
  ]}}}
  ```
  Applied to BOTH facts (defensive consistency — both target the controller's specific log message).
- **Files modified:** `tests/BaseApi.Tests/Observability/LogExportTests.cs` (queryBody construction in both [Fact] methods + inline comments documenting the multi-record-per-request discovery)
- **Verification:** Post-fix `tests/BaseApi.Tests/bin/Release/net8.0/BaseApi.Tests.exe --filter-class "BaseApi.Tests.Observability.LogExportTests"` → 2/2 GREEN in 33.6s.
- **Plan authorization:** The plan's `<behavior>` for fact 1 explicitly states "hit body contains 'test-obs ok ran' verbatim" — meaning the controller's specific log was the intended target. The plan's query shape was simply too narrow to disambiguate among the multi-record cluster. The semantic must_haves invariant is preserved exactly; only the query shape was tightened to guarantee the correct hit. Pattern documented in `patterns-established` frontmatter for reuse.
- **Committed in:** `c40d062` (Task 4 atomic commit)

**2. [Rule 1 - Bug] MetricsExportTests xUnit1051 CancellationToken propagation on 2x Task.Delay calls**

- **Found during:** Task 3 first build verification — `dotnet build SK_P.sln -c Release --no-restore` failed with 2 errors:
  ```
  MetricsExportTests.cs(84,15): error xUnit1051: Calls to methods which accept CancellationToken
  should use TestContext.Current.CancellationToken
  MetricsExportTests.cs(107,15): error xUnit1051: ...
  ```
- **Issue:** Plan-as-written body for fact 2 + fact 3 contained `await Task.Delay(15_000)` calls (no CancellationToken passed). Under `TreatWarningsAsErrors=true` (Directory.Build.props global) + xUnit v3 3.2.2's xUnit1051 analyzer at Warning severity, these become BUILD-FATAL errors. Same pattern as Plan 03-02 deviation (which threaded `TestContext.Current.CancellationToken` through 10 async call sites in SchemaTests + AuditInterceptorTests).
- **Fix:** Pass `ct = TestContext.Current.CancellationToken` to both `Task.Delay` calls:
  - Fact 2 line 84: `await Task.Delay(15_000, ct);`
  - Fact 3 line 107: `await Task.Delay(15_000, ct);`
  The `ct` local is already in scope (declared at the top of each fact body).
- **Files modified:** `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` (2 Edit calls)
- **Verification:** Post-fix `dotnet build SK_P.sln -c Release --no-restore` → 0 Warning(s) / 0 Error(s) (1.21s). Test run: 3/3 GREEN in 46.8s.
- **Plan authorization:** xUnit1051 escalation under TreatWarningsAsErrors is project-wide and was documented in STATE.md decisions log (Plan 03-02 entry: "xUnit v3 3.2.2 xUnit1051 analyzer escalation under TreatWarningsAsErrors=true required threading TestContext.Current.CancellationToken through 10 async call sites"). The plan body's `Task.Delay(15_000)` shape would have always been BUILD-FATAL on this codebase; the fix is the project's established idiom for this analyzer.
- **Committed in:** `c40d062` (Task 4 atomic commit)

**3. [Rule 1 - Plan/Live Discrepancy / Educational-rephrase precedent] LogLevelFilterTests doc-comment cref reference**

- **Found during:** Task 2 verification gate cycle — after Task 2 file write + Task 1 + Task 3 successful runs, ran the plan's negation grep `! grep "OtelCollectorFixture" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` which returned 1 match instead of 0.
- **Issue:** Plan-as-written body for LogLevelFilterTests doc comment (Plan line 287) contained the literal `Migration: was Phase 5 file-exporter + per-test <c>OtelCollectorFixture</c>` XML cref-form reference. The plan's own verification gate (line 367 of the plan body: `! grep "OtelCollectorFixture" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs (negation)`) requires 0 matches.
- **Fix:** Rephrased the doc comment to convey the same migration narrative without the cref-form reference: `Migration: was Phase 5 file-exporter + per-test fixture instances (deleted by Plan 11-05 / 11-08c)`. The educational content (what this code USED to be + the migration history) is preserved; the literal grep gate is satisfied; the semantic must_haves invariant (`no references to OtelCollectorFixture or file-exporter API`) is satisfied SEMANTICALLY (no CODE references — the file is fully decoupled from the about-to-be-deleted fixture).
- **Files modified:** `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` (XML doc comment only — 2 lines changed; no behavior change)
- **Verification:** Post-rephrase `grep -c "OtelCollectorFixture" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` returns 0. Re-ran the test class to confirm no behavior change: 2/2 GREEN in 26.0s.
- **Plan authorization:** Plan 06-01 (MP-code rephrase), Plan 08-01 (EnsureCreatedAsync rephrase), Plan 10-02 (VALID-21 rephrase), Plan 11-04 (educational rephrase), Plan 11-07 (PromQL label rephrase) all extended the precedent that when the plan body contains a literal string conflicting with the plan's own grep gate, the executor may rephrase the educational content (doc comments) without changing observable behavior. The semantic must_haves invariant always wins over the literal grep gate; this is the established Phase 6-11 contract.
- **Committed in:** `c40d062` (Task 4 atomic commit)

---

**Total deviations:** 2 auto-fixed Rule 1 fix-forwards + 1 educational-rephrase (Plan 06-01 / 08-01 / 10-02 / 11-04 / 11-07 precedent)
**Impact on plan:** Plan's intent (migrate the 3 file-exporter-coupled fact classes to ES/Prom polling against the live stack; preserve OBSERV-03/06/08/13/14 behavioral assertions; preserve T-05-PII-INJECT regression; preserve D-04 health-filter invariant) is preserved exactly. The 2 Rule 1 fix-forwards address real Build-RED conditions that the plan-as-written code would have produced (xUnit1051 + multi-record-per-request); the educational-rephrase keeps the literal grep gate satisfied without weakening the semantic invariant. No scope creep; all 7 facts (2+2+3) GREEN against the live stack post-fix.

## Issues Encountered

- **First LogExportTests verification cycle revealed the multi-log-record-per-request shape** — see Rule 1 fix-forward #1 above. Diagnosis took one `curl http://localhost:9200/logs-generic.otel-default/_search?size=5 -d '{"query":{"match_phrase":{"body.text":"test-obs ok ran"}}}'` invocation to confirm the controller's log was distinct from the framework Hosting.Diagnostics logs sharing the same corrId scope.
- **First MetricsExportTests build attempt failed on xUnit1051** — see Rule 1 fix-forward #2 above. The fix is the project's established idiom for this analyzer (Plan 03-02 precedent).
- **Plan body's own literal grep gate conflicted with plan body's prescribed doc comment for LogLevelFilterTests** — see educational-rephrase #3 above. Same precedent as Plans 06-01 / 08-01 / 10-02 / 11-04 / 11-07.
- **Live observability stack (ES + Prom + collector + Postgres) needed to remain UP for all 3 facts** — verified pre-plan via `docker compose ps`: all 4 services Up + 3 healthy (collector has no compose-level healthcheck per Phase 5 D-distroless-image-no-healthcheck stance). No stack re-bootstrap needed during plan execution.

## Self-Check: PASSED

**File existence verification:**
- FOUND: `tests/BaseApi.Tests/Observability/LogExportTests.cs` (modified — wholesale rewrite, 102 lines)
- FOUND: `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` (modified — wholesale rewrite, 85 lines)
- FOUND: `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` (modified — wholesale rewrite, 115 lines)
- FOUND: `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` (PRESERVED — Plan 11-08c will delete it)
- FOUND: `tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` (Plan 11-06 — consumed as IClassFixture base by LogExportTests + MetricsExportTests; direct `new` by both LogLevelFilterTests facts)
- FOUND: `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` (Plan 11-06 — consumed by LogExportTests + LogLevelFilterTests)
- FOUND: `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` (Plan 11-06 — consumed by MetricsExportTests)
- FOUND: `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` (Plan 11-06 — CorrelationIdFieldPath consumed by LogExport + LogLevelFilter; LogsDataStream consumed transitively via PollEsForLog default)
- FOUND: `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-08b-SUMMARY.md` (this file)

**Commit verification:**
- FOUND: `c40d062` (subject: `test(observability): migrate LogExportTests + LogLevelFilterTests + MetricsExportTests to ES/Prom polling`)
- `git show --stat HEAD` lists exactly 3 files modified (209 insertions / 270 deletions; net -61 lines)
- `git diff --diff-filter=D HEAD~1 HEAD` empty (no accidental file deletions)
- `git status --porcelain` for tracked files — empty (pre-existing untracked planning + .claude + Service/Properties paths outside this plan's scope remain)

**Plan-level verification gates (all PASS at commit c40d062):**
- `! grep "OtelCollectorFixture" tests/BaseApi.Tests/Observability/LogExportTests.cs` — 0 matches ✓
- `! grep "ReadExportedLogs\|FlushAsync\|GetLogRecords" tests/BaseApi.Tests/Observability/LogExportTests.cs` — 0 matches ✓
- `grep "IClassFixture<Phase11WebAppFactory>" tests/BaseApi.Tests/Observability/LogExportTests.cs` — 1 match ✓
- `grep -c "ElasticsearchTestClient" tests/BaseApi.Tests/Observability/LogExportTests.cs` — 3 matches (>=1 required) ✓
- `grep -c "EsIndexNames.CorrelationIdFieldPath" tests/BaseApi.Tests/Observability/LogExportTests.cs` — 2 matches (>=2 required: 2 facts) ✓
- `grep -c "PollEsForLog" tests/BaseApi.Tests/Observability/LogExportTests.cs` — 4 matches (>=2 required: 2 calls + 2 doc-comment refs) ✓
- `grep -cE "\[Trait\(\"Phase\", \"11\"\)\]" tests/BaseApi.Tests/Observability/LogExportTests.cs` — 1 match ✓
- `! grep "3.2.0" tests/BaseApi.Tests/Observability/LogExportTests.cs` — 0 matches (version assertion dropped per WARNING #7) ✓
- `! grep "OtelCollectorFixture" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` — 0 matches ✓ (educational-rephrase applied)
- `grep -c "Phase11WebAppFactory" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` — 4 matches (>=2 required: 2 facts each construct one + doc-comment refs) ✓
- `grep -c "new Phase11WebAppFactory(logLevelDefaultOverride:" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` — 1 match ✓
- `grep -c "ElasticsearchTestClient" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` — 2 matches (>=1 required: 2 facts each construct one) ✓
- `grep -c "Assert.Null(hit)" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` — 1 match ✓
- `grep -c "Assert.NotNull(hit)" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` — 1 match ✓
- `grep -c "timeoutMs: 8_000" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` — 1 match ✓
- `grep -c "timeoutMs: 30_000" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` — 1 match ✓
- `! grep "OtelCollectorFixture\|ReadExportedMetrics\|FlushAsync\|EnumerateMetricNames\|EnumerateMetricNodes\|GetAllDataPointAttributes" tests/BaseApi.Tests/Observability/MetricsExportTests.cs` — 0 matches ✓
- `grep -c "IClassFixture<Phase11WebAppFactory>" tests/BaseApi.Tests/Observability/MetricsExportTests.cs` — 1 match ✓
- `grep -c "PrometheusTestClient" tests/BaseApi.Tests/Observability/MetricsExportTests.cs` — 5 matches (>=3 required: 3 facts) ✓
- `grep -c "http_server_request_duration_seconds_count" tests/BaseApi.Tests/Observability/MetricsExportTests.cs` — 5 matches (>=2 required: facts 1+2 PromQL + comments) ✓
- `grep -c 'http_route=~".*health.*"' tests/BaseApi.Tests/Observability/MetricsExportTests.cs` — 1 match ✓ (fact 2 regex)
- `grep -cE 'process_runtime_dotnet_\.\*|dotnet_\.\*' tests/BaseApi.Tests/Observability/MetricsExportTests.cs` — 2 matches (>=1 required: fact 3 either-name accept) ✓
- `test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` exits 0 — file PRESERVED ✓
- `! grep -rn "OtelCollectorFixture" tests/BaseApi.Tests/Observability/LogExportTests.cs tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs tests/BaseApi.Tests/Observability/MetricsExportTests.cs tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — 0 matches ✓ (all 4 historical consumers now have ZERO references — file is fully orphaned)
- `dotnet build SK_P.sln -c Release --no-restore` — 0 Warning(s) / 0 Error(s) (1.21s) ✓
- `dotnet build SK_P.sln -c Debug --no-restore` — 0 Warning(s) / 0 Error(s) (1.65s) ✓
- Combined run `BaseApi.Tests.exe --filter-class LogExportTests --filter-class LogLevelFilterTests --filter-class MetricsExportTests` — 7/7 GREEN in 1m 44s ✓
- `git log -1 --format=%s` — matches `test(observability): migrate LogExportTests + LogLevelFilterTests + MetricsExportTests to ES/Prom polling` ✓
- `git show --stat HEAD` — 3 files changed; 209 insertions / 270 deletions ✓
- `git status --porcelain` for tracked files — empty ✓

**Plan success_criteria coverage (all 7 criteria PASS at commit c40d062):**
- #1 LogExportTests 2 facts migrated to Phase11WebAppFactory + ElasticsearchTestClient + EsIndexNames.CorrelationIdFieldPath; no references to OtelCollectorFixture; version assertion dropped per WARNING #7 ✓
- #2 LogLevelFilterTests 2 facts migrated; positive 30s budget + negative 8s budget asymmetric polling shape (PATTERNS option a) ✓
- #3 MetricsExportTests 3 facts migrated; D-04 health-filter invariant preserved via strict empty regex match; runtime metric fact accepts either naming era ✓
- #4 OtelCollectorFixture.cs PRESERVED at this commit's HEAD (Plan 11-08c performs the final deletion) ✓
- #5 Solution builds zero-warning Release+Debug ✓
- #6 All 7 migrated facts GREEN against the live stack (2+2+3); HealthEndpointsTests 7 facts unaffected (Plan 11-08a's commit still HEAD-2) ✓
- #7 Single git commit `test(observability): migrate LogExportTests + LogLevelFilterTests + MetricsExportTests to ES/Prom polling` exists at HEAD; modifies exactly 3 files; working tree clean post-commit ✓

**Threat model coverage (all 4 STRIDE entries verified):**
- T-11-08b-T1 (negative-assertion fact false-positive in LogLevelFilterTests — Information log actually present but 8s budget too short to see it) — MITIGATED: 8s budget exceeds typical ES indexing lag (1-3s per RESEARCH Pattern 2 + Pitfall 5). Test passed GREEN, confirming MEL filter suppression. Positive control fact (30s budget, default Information) PASSES with hit found, proving the budget shape works asymmetrically. ✓
- T-11-08b-T2 (D-04 health filter regression — health-route data points leak to Prom) — MITIGATED: Strict empty assertion on `http_route=~".*health.*"` PromQL match. If the collector's `filter/health_metrics` processor regressed, this fact fails loudly. Test passed GREEN with `Assert.Empty(samples)`. ✓
- T-11-08b-T3 (LogExportTests body-content match false-positive — "sk-api" appears in unrelated doc) — MITIGATED: ES query filters on `EsIndexNames.CorrelationIdFieldPath` term match AND body.text match_phrase (Rule 1 fix-forward) — only docs with the specific 32-hex corrId AND the controller's specific body.text are returned. Probability of false positive is negligible (128-bit corrId collision space + body.text disambiguation). ✓
- T-11-08b-T4 (orphan OtelCollectorFixture.cs causes build warnings if it has unused-class detection enabled) — MITIGATED: OtelCollectorFixture is `public class` — not flagged as unused by C# compiler regardless of usage count. The file stays as dead-but-compilable code until Plan 11-08c's `git rm`. `dotnet build SK_P.sln -c Release+Debug --no-restore` confirmed 0 Warning(s) / 0 Error(s) post-commit. ✓

## User Setup Required

None — this is a test-only refactor commit. No external service configuration required. The Phase 11 observability backend (collector + ES + Prom + Postgres) remains healthy.

## Next Phase Readiness

**Plan 11-08c (final OtelCollectorFixture.cs deletion + Phase 11 close)** is now fully unblocked:
- After this plan's commit (c40d062), `grep -rn "OtelCollectorFixture" tests/BaseApi.Tests/Observability/*Tests.cs` returns 0 matches across all 4 historical consumer files (LogExportTests, LogLevelFilterTests, MetricsExportTests, HealthEndpointsTests).
- The final `git rm tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` becomes a clean defensive-grep-then-delete operation.
- Plan 11-08c's remaining work: (a) final OtelCollectorFixture.cs deletion + verify zero remaining consumers via `! grep -rn "OtelCollectorFixture" tests/ src/`; (b) 3-consecutive-GREEN closing cadence (Phase 3 D-18 precedent) — run the full test suite 3 times and confirm consistent GREEN; (c) psql `\l` SHA-256 BEFORE/AFTER snapshot (Phase 3 D-15 carry-forward) — confirms no test-DB leaks across the full Phase 11 work; (d) Phase 11 closing commit narrative + SUMMARY documenting the full 10-commit sequence (765b3fc → c40d062 → 11-08c's commit).
- The forensic property holds: Plan 11-08b's atomic commit (c40d062) is independently revertable. `git revert c40d062` restores the prior OtelCollectorFixture-based shape for the 3 fact classes without affecting subsequent Phase 11 commits.

**Phase 11 closure roadmap:** With Plan 11-08b complete, the only remaining work for Phase 11 closure is Plan 11-08c (1 plan, est. 5-10 min) consisting of orphan file deletion + closing-cadence verification + final SUMMARY. After 11-08c lands, Phase 11 ships with all 41 REQ-IDs covered, the entire Phase 5 observability surface migrated to Phase 11 ES + Prom backends, and zero remaining file-exporter coupling anywhere in the test suite.

---
*Phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack*
*Plan: 08b*
*Completed: 2026-05-28*
