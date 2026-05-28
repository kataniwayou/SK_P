---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
plan: 06
subsystem: testing
tags: [phase11-webappfactory, elasticsearch-test-client, prometheus-test-client, es-index-names, wave-0-probe, polling-helpers, metric-export-interval-override, otlp-endpoint-pin, phase-11-wave-4]

# Dependency graph
requires:
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-02 compose-stack mutation (commit a3c0b20) — ES :9200 + Prom :9090 + collector :8889 reachable on host-DNS; helper HttpClients consume these endpoints
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-03 collector config rewire (commit 1f8eb69) — elasticsearch exporter (mapping.mode none) + prometheus exporter (resource_to_telemetry_conversion true) wired; Wave 0 probe verified the live ES index name + field shape this produces
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-04 prometheus.yml (commit b40299c) — Prom scrapes collector :8889 with 15s scrape_interval; PrometheusTestClient.InitialSleepMs = 15_000 matches this discipline
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-05 SDK strip (commit 0fa325e) — .WithTracing removed + tests/.otel-out/ removed; helper files land into a clean tree without file-exporter remnants
  - phase: 08-entity-build-out-migrations-docker-runtime-tests
    provides: Phase8WebAppFactory — per-class throwaway-Postgres-DB IClassFixture base; Phase11WebAppFactory subclasses for test composition
provides:
  - tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs — 4 verified-from-Wave-0 string constants (LogsDataStream, CorrelationIdFieldPath, ResourceAttributesFieldPath, FieldShape) resolving RESEARCH Open Q1 empirically against the live sk_p stack
  - tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs — Phase8WebAppFactory subclass with (a) OTEL_EXPORTER_OTLP_ENDPOINT env-var pin, (b) PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 1_000 override, (c) optional Logging:LogLevel:Default override ctor for LogLevelFilterTests migration parity
  - tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs — async PollEsForLog helper with exponential backoff (200ms → 3200ms cap), HTTP 404 + empty-hits tolerance, IDisposable HttpClient pinned to http://localhost:9200/
  - tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs — async PollPrometheusUntilSumAtLeast + QueryPrometheus (Uri.EscapeDataString-protected) + SumSampleValues, with mandatory 15s initial sleep + 3s poll interval per RESEARCH Pattern 3
  - Single atomic commit (765b3fc) — commit #6 of the Phase 11 sequence — adds exactly 4 new files; 410 insertions / 0 deletions
  - dotnet build SK_P.sln zero-warning Release+Debug; HealthEndpointsTests regression suite GREEN (7/7) confirming the new factory + helpers don't break the Phase 5 baseline
affects: [11-07 (round-trip E2E test bodies will consume PollEsForLog + PollPrometheusUntilSumAtLeast against EsIndexNames.LogsDataStream + the verified resource.attributes / attributes.CorrelationId field paths); 11-08 (Log/LogLevel/Metrics fact migration will subclass Phase11WebAppFactory + use the helpers; OtelCollectorFixture.cs deletion will follow this plan since helpers + factory replace it functionally)]

# Tech tracking
tech-stack:
  added: [none — all required libraries (Microsoft.AspNetCore.Mvc.Testing + OpenTelemetry.Metrics + System.Text.Json + Xunit) already pinned via CPM from Phase 5 / Phase 8]
  patterns:
    - "Wave-0-empirical-probe pattern — when CONTEXT/RESEARCH offers two candidate answers for a runtime-shape question (here: ES index name + field shape under mapping.mode: none), the planner injects a one-time live probe BEFORE the helper code bakes a constant. The probe drives a real log record through SDK → collector → ES, observes the resulting index + _source shape, and the verified value gets baked into a constants file (EsIndexNames.cs). Resolves spec-vs-live ambiguity with empirical evidence rather than reasoning."
    - "Constants-file-with-Wave-0-rationale pattern — EsIndexNames.cs carries XML doc comments explaining (a) why the file exists (RESEARCH Pitfall 2 ambiguity), (b) what the Wave 0 probe found (live shape was 'otel' despite mapping.mode: none config — elasticsearchexporter@v0.152.0 emits the 'deprecated and ignored' warning + falls back to current default), (c) how to refresh the values if a future ES/collector upgrade changes the live shape. Future readers can trace the constant origin without re-running the probe."
    - "WebAppFactory subclass composition over fixture evolution (RESEARCH Open Q2) — Phase11WebAppFactory : Phase8WebAppFactory : WebAppFactory : WebApplicationFactory<Program>. Each layer adds one knob (Phase8 = Postgres DB; WebAppFactory = controller AddApplicationPart; Phase11 = OTel test-only overrides + log-level ctor). Fewer mutations to Phase 5/8 assets; cleaner trait + filter posture; replacement for OtelCollectorFixture (which Plan 11-08 deletes)."
    - "Test-only metric-export-interval override pattern (RESEARCH Pattern 4 / Pitfall 7) — Phase11WebAppFactory.ConfigureWebHost calls services.Configure<PeriodicExportingMetricReaderOptions>(opts => opts.ExportIntervalMilliseconds = 1_000) to override the SDK's 60s production default down to 1s for E2E determinism. Production posture (60s) is unaffected; this is a test-only knob that lives ONLY in the test-side factory."
    - "JsonElement.Clone-after-doc-disposal pattern (lifted from OtelCollectorFixture line 211) — both polling helpers wrap each parsed element in a using-var JsonDocument scope and call .Clone() on the RootElement before the using block disposes the parent doc. The returned element is safe to retain across method boundaries. Reusable for any future JSON-from-HTTP helpers that return a single hit/result element."
    - "Exponential backoff polling pattern (RESEARCH Pattern 2) — ElasticsearchTestClient.PollEsForLog uses InitialDelayMs=200, MaxDelayMs=3_200, delay doubling per iteration capped at the max, with Stopwatch.Elapsed remaining-budget check on every iteration so the loop never overshoots the timeout. Reusable for any future eventual-consistency polling against a backend with unbounded ingestion lag."
    - "Mandatory initial sleep polling pattern (RESEARCH Pattern 3 / Pitfall 7) — PrometheusTestClient.PollPrometheusUntilSumAtLeast awaits Task.Delay(15_000) BEFORE the first query because Prom is pull-based with a 15s scrape interval (D-08). A naive poll-from-t=0 loop wastes the entire scrape cycle on empty result vectors. Reusable for any future test that polls a pull-based backend whose scrape cadence is the dominant latency."
    - "Uri.EscapeDataString on PromQL pattern (RESEARCH Don't Hand-Roll table) — PrometheusTestClient.QueryPrometheus wraps the user-supplied PromQL in Uri.EscapeDataString before embedding it in the URL query string. PromQL contains { } \" = which break unencoded URL queries. Reusable for any future HTTP client that embeds a structured-DSL string in a GET URL."
    - "Multi-label result-vector summation pattern — PrometheusTestClient.SumSampleValues iterates the result vector and sums numeric values across all label combinations (method × status_code × etc.). Keeps the assertion shape robust to the cardinality of label values (1 sample vs 10 samples for the same metric name) and the order of arrival across scrape cycles."

key-files:
  created:
    - "tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs — 66 lines: public static class with 4 const strings (LogsDataStream='logs-generic.otel-default', CorrelationIdFieldPath='attributes.CorrelationId', ResourceAttributesFieldPath='resource.attributes', FieldShape='otel'). XML doc explains why the file exists, what Wave 0 found, and how to refresh on future ES/collector upgrades."
    - "tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs — 103 lines: public class Phase11WebAppFactory : Phase8WebAppFactory. Parameterless ctor for IClassFixture + internal ctor accepting logLevelDefaultOverride. ctor pins OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 (T-05-OTLP-EXFIL defensive). ConfigureWebHost: optional Logging:LogLevel:Default in-memory config + base.ConfigureWebHost (Phase 8 Postgres wiring) + Configure<PeriodicExportingMetricReaderOptions>(opts => opts.ExportIntervalMilliseconds=1_000) + AddControllers().AddApplicationPart(assembly)."
    - "tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs — 108 lines: public sealed class ElasticsearchTestClient : IDisposable. ctor constructs HttpClient with BaseAddress http://localhost:9200/. PollEsForLog(string queryBody, int timeoutMs, string? indexPath = null) returns Task<JsonElement?> — exponential backoff (200ms → 3200ms cap), HTTP 404 + empty-hits tolerance, Stopwatch-bounded loop, JsonElement.Clone-after-doc-disposal pattern. Default indexPath is EsIndexNames.LogsDataStream (Wave 0 verified)."
    - "tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs — 133 lines: public sealed class PrometheusTestClient : IDisposable. ctor constructs HttpClient with BaseAddress http://localhost:9090/. PollPrometheusUntilSumAtLeast(string promql, double threshold) returns Task<List<JsonElement>> — mandatory 15s initial sleep + 3s poll interval, terminates when SumSampleValues >= threshold OR PollTimeoutMs (60s) elapsed. QueryPrometheus(string promql) single-shot with Uri.EscapeDataString. SumSampleValues static helper iterates samples + extracts value[1] as double + sums total."
  modified: []

key-decisions:
  - "Wave 0 probe revealed live ES shape is 'otel' DESPITE collector config mapping.mode: none (RESEARCH Open Q1 resolved) — elasticsearchexporter@v0.152.0 emits the 'mapping::mode config option is deprecated and ignored' warning (anticipated by RESEARCH Pitfall 2) and silently falls back to its current default behavior. The live index is logs-generic.otel-default (NOT the spec-predicted logs-generic-default) with lowercase top-level keys (body, severity_text, attributes, resource) + nested attributes map containing .NET MEL bridge BeginScope keys verbatim (attributes.CorrelationId — capital C). Same shape sk2_1 observed live; the spec-vs-live ambiguity is now empirically closed."
  - "EsIndexNames.cs constants file at tests/BaseApi.Tests/Observability/Helpers/ chosen over inline string literals in helpers — a constants file gives the verified values one canonical home with XML doc explaining the Wave 0 origin; if a future collector/ES upgrade changes the live shape, the refresh is a single file edit + test re-run. Inline literals would scatter the truth across multiple files."
  - "Single atomic commit (commit #6 of Phase 11) — matches the Phase 11 Wave 1-3 + 5 atomic-commit precedent (Plans 11-01 / 11-02 / 11-03 / 11-04 / 11-05 all single atomic commits). 4 new files; no modifications to existing files; 410 insertions / 0 deletions. Forensic property: revert 765b3fc restores the prior state without affecting subsequent Phase 11 commits."
  - "MTP filter syntax — `--filter-class \"BaseApi.Tests.Observability.HealthEndpointsTests\"` is the canonical Microsoft.Testing.Platform argument shape for class-filtered runs (NOT VSTest's `--filter FullyQualifiedName~ClassName` which the Phase 8 test project intentionally moved off; using the legacy syntax surfaces the MTP0001 warning + silently runs the entire suite). Future test-filtered runs on this codebase should use --filter-class / --filter-method / --filter-namespace per BaseApi.Tests.exe --help output."
  - "Phase11WebAppFactory composition over OtelCollectorFixture evolution (RESEARCH Open Q2 + plan inheritance design) — preserves Phase 5/8 assets (OtelCollectorFixture.cs still in tree, still functional for the unmigrated facts; deletion deferred to Plan 11-08 after Log/LogLevel/Metrics facts move to the new factory). Cleaner trait + filter posture: Plan 11-07/11-08 facts inherit the Phase 8 Postgres + the Phase 11 OTel knobs in one IClassFixture activation."
  - "JsonElement.Clone-after-doc-disposal pattern carried verbatim from OtelCollectorFixture line 211 (lifted via XML doc reference) — both new helpers wrap the parsed element in a using-var JsonDocument scope and call .Clone() on the relevant element. The returned element is safe to retain across method/await boundaries. Cross-fixture consistency: Phase 5 OtelCollectorFixture + Phase 11 ElasticsearchTestClient + PrometheusTestClient all use the identical pattern."

patterns-established:
  - "Wave-0-empirical-probe pattern — when CONTEXT/RESEARCH offers two candidate answers for a runtime-shape question, the planner injects a one-time live probe BEFORE helper code bakes a constant. Reusable for any future phase facing spec-vs-live ambiguity."
  - "Constants-file-with-Wave-0-rationale pattern — verified runtime constants live in a single .cs file with XML doc explaining origin + refresh procedure. Future ES/collector upgrades become a one-file edit + test re-run."
  - "WebAppFactory subclass composition pattern — Phase11WebAppFactory : Phase8WebAppFactory : WebAppFactory chain composes per-class throwaway-Postgres-DB + OTel test-only overrides without mutating the lower-layer fixtures. Reusable for any future phase that adds backend-test knobs."
  - "MTP filter-class canonical syntax pattern — Microsoft.Testing.Platform uses --filter-class (NOT VSTest's --filter FullyQualifiedName~). Future test-filtered runs on this codebase document the MTP shape in their plan body so executors don't get tricked by the silent VSTest fallback."

requirements-completed: [OBSERV-13, OBSERV-14, TEST-07]
# OBSERV-13 (logs land in ES at the verified data-stream alias with OTLP field shape) — the Wave 0 probe empirically confirmed the live shape; EsIndexNames.cs bakes the verified constants; ElasticsearchTestClient consumes them. Test facts in Plans 11-07/11-08 will close the requirement behaviorally.
# OBSERV-14 (HTTP server metrics scraped by Prometheus from otel-collector:8889 with service_name="sk-api" label) — PrometheusTestClient encapsulates the polling discipline; the SumSampleValues + 15s initial sleep + Uri.EscapeDataString patterns are reusable across all Phase 11 metric assertions. Test facts in Plans 11-07/11-08 will close the requirement behaviorally.
# TEST-07 (E2E round-trip test class(es) verifying both backends) — Phase11WebAppFactory provides the IClassFixture base; ElasticsearchTestClient + PrometheusTestClient provide the polling primitives; Wave 0 verified constants resolve the spec-vs-live ambiguity. Plans 11-07 + 11-08 author the actual test bodies on top of this scaffolding.

# Metrics
duration: ~10min
completed: 2026-05-28
---

# Phase 11 Plan 06: Wave 0 ES probe + Phase11WebAppFactory + ElasticsearchTestClient + PrometheusTestClient + EsIndexNames Summary

**Wave 0 probe empirically resolved RESEARCH Open Q1 (live ES shape is `otel` despite `mapping.mode: none` — collector v0.152.0 deprecation warning falls back to current default); 4 new test-helper files land in a single atomic commit (765b3fc): Phase11WebAppFactory composes Phase8WebAppFactory's Postgres + adds OTel test-only knobs (env-var pin + 1s metric export interval override + log-level ctor); ElasticsearchTestClient/PrometheusTestClient encapsulate the polling discipline that Plans 11-07/11-08 will consume; EsIndexNames.cs bakes the Wave 0 verified constants.**

## Performance

- **Duration:** ~10 min (includes Wave 0 probe + 4 file creations + builds + regression smoke + commit)
- **Started:** 2026-05-28T~12:50Z
- **Completed:** 2026-05-28T~13:00Z
- **Tasks:** 6 (Task 0 Wave 0 probe + Tasks 1-5)
- **Files modified:** 4 (all new)

## Accomplishments

- **Task 0 Wave 0 probe — empirically resolved RESEARCH Open Q1** by:
  - Sanity check: ES :9200 cluster health = green (single-node, 0 active shards = clean slate).
  - Snapshot ES indices BEFORE traffic — `_cat/indices?v` shows zero indices (fresh ES post-Plan-11-04 stack startup).
  - Drove a single log record via `dotnet run --project src/BaseApi.Service` on Kestrel-assigned ports (the service started successfully despite the Postgres connection failure — the OTel logging pipeline is wired BEFORE the migration runs, so Application Started logs flowed to the collector regardless).
  - Snapshot ES indices AFTER traffic — `_cat/indices?v` revealed `.ds-logs-generic.otel-default-2026.05.28-000001` (the backing index for the data-stream alias `logs-generic.otel-default` — yellow because single-node with 1 replica unassigned, which is the expected dev-posture shape per Phase 11 D-12).
  - Inspected `_source` shape on a representative hit — confirmed lowercase OTLP-normalized keys (`body.text`, `severity_text`, `severity_number`, `attributes`, `resource.attributes`, `@timestamp`, `trace_id`, `span_id`, `scope.name`, `data_stream.dataset`) which is the `mapping.mode: otel` semantic shape DESPITE the collector config setting `mapping.mode: none`. The deprecation warning from elasticsearchexporter@v0.152.0 (`mapping::mode config option is deprecated and ignored`) — anticipated by RESEARCH Pitfall 2 — explains the discrepancy: the exporter silently falls back to its current default behavior, which is the otel-mode shape.
  - Located the correlation id at `attributes.CorrelationId` (capital `C`, flat under the lowercase `attributes` map) — the .NET MEL bridge preserves the `ILogger.BeginScope` key name verbatim, even when the surrounding shape is OTLP-normalized lowercase. Resource attributes at `resource.attributes` (with dotted keys like `service.name`, `service.version`, `telemetry.sdk.language`). Wave 0 verified values: IndexAlias=`logs-generic.otel-default`, FieldShape=`otel`, CorrelationFieldPath=`attributes.CorrelationId`, ResourceFieldPath=`resource.attributes`.
- **Task 1 EsIndexNames.cs created** with the 4 Wave-0-verified `public const string` values; XML doc explains origin + refresh procedure; placeholders fully substituted (no `<..._FROM_TASK_0>` patterns remain).
- **Task 2 Phase11WebAppFactory.cs created** subclassing `Phase8WebAppFactory`. Public parameterless ctor for `IClassFixture<>` activation; internal `Phase11WebAppFactory(string? logLevelDefaultOverride)` overload for LogLevelFilterTests migration in Plan 11-08. Constructor pins `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` (T-05-OTLP-EXFIL defensive). `ConfigureWebHost`: optional `Logging:LogLevel:Default` in-memory config + `base.ConfigureWebHost(builder)` (Phase 8 Postgres connection-string wiring) + `services.Configure<PeriodicExportingMetricReaderOptions>(opts => opts.ExportIntervalMilliseconds = 1_000)` (RESEARCH Pattern 4 / Pitfall 7) + `AddControllers().AddApplicationPart(typeof(Phase11WebAppFactory).Assembly)` (idempotent — base also calls this).
- **Task 3 ElasticsearchTestClient.cs created** as `public sealed class : IDisposable`. ctor constructs `HttpClient { BaseAddress = new Uri("http://localhost:9200/") }`. `PollEsForLog(string queryBody, int timeoutMs, string? indexPath = null)` — exponential backoff 200ms → 3200ms cap, `Stopwatch`-bounded loop with remaining-budget delay, HTTP 404 + empty-hits tolerance (RESEARCH Pitfall 5), `HttpRequestException` retry-on-network-blip, `JsonElement.Clone()`-after-doc-disposal pattern (lifted verbatim from OtelCollectorFixture line 211). Default `indexPath` is `EsIndexNames.LogsDataStream` (Wave 0 verified).
- **Task 4 PrometheusTestClient.cs created** as `public sealed class : IDisposable`. ctor constructs `HttpClient { BaseAddress = new Uri("http://localhost:9090/") }`. `PollPrometheusUntilSumAtLeast(string promql, double threshold)` — MANDATORY 15s `Task.Delay(InitialSleepMs)` before the first query (RESEARCH Pattern 3 / Pitfall 7 — Prom 15s scrape interval per D-08), then 3s `PollIntervalMs` until `SumSampleValues(samples) >= threshold` OR `PollTimeoutMs` (60s) elapsed. `QueryPrometheus(string promql)` — single-shot with `Uri.EscapeDataString(promql)` URL encoding (RESEARCH Don't Hand-Roll table); calls `Assert.Fail` on non-success Prom envelopes. `SumSampleValues(List<JsonElement>)` — static helper iterates result vector + extracts numeric `sample.value[1]` strings via `double.TryParse(..., NumberStyles.Float, InvariantCulture)`.
- **Task 5 build verification + regression + atomic commit:**
  - `dotnet build SK_P.sln -c Release --no-restore` → 0 Warning(s) / 0 Error(s) (1.5s).
  - `dotnet build SK_P.sln -c Debug --no-restore` → 0 Warning(s) / 0 Error(s) (3.4s).
  - HealthEndpointsTests regression — 7/7 GREEN in 10.2s via `BaseApi.Tests.exe --filter-class "BaseApi.Tests.Observability.HealthEndpointsTests"` (canonical Microsoft.Testing.Platform syntax; the legacy `--filter "FullyQualifiedName~..."` VSTest shape surfaced `MTP0001` warning + silently ran the entire suite).
  - Single atomic commit `765b3fc` with verbatim subject `test(observability): add Phase11WebAppFactory + ElasticsearchTestClient + PrometheusTestClient + EsIndexNames (Wave 0)`. `git show --stat HEAD` lists exactly 4 new files; 410 insertions / 0 deletions; `git diff --diff-filter=D HEAD~1 HEAD` empty (no accidental deletions); working tree clean post-commit (excluding pre-existing `.planning/`, `.claude/`, `src/BaseApi.Service/Properties/` untracked paths outside this plan's scope).

## Task Commits

Per Plan 11-06's atomic-commit contract (success criteria #7 — single git commit with subject `test(observability): ...`), this plan ships as ONE atomic commit. Wave 0 probe (Task 0) was an empirical verification step that produced no commit; Tasks 1-4 file creations rolled into Task 5's single commit point.

1. **Task 0: Wave 0 ES index name probe** — empirical verification step; no commit (output values consumed by Task 1)
2. **Task 1: Create EsIndexNames.cs constants file** — staged at task boundary (rolled into Task 5 commit)
3. **Task 2: Create Phase11WebAppFactory.cs subclass** — staged at task boundary (rolled into Task 5 commit)
4. **Task 3: Create ElasticsearchTestClient.cs polling helper** — staged at task boundary (rolled into Task 5 commit)
5. **Task 4: Create PrometheusTestClient.cs polling helper** — staged at task boundary (rolled into Task 5 commit)
6. **Task 5: Build verification + regression smoke + commit Wave 4 helpers** — `765b3fc` (test)

**Plan metadata:** TBD — committed by execute-plan agent after SUMMARY + STATE updates.

_Note: Plan 11-06 deliberately ships as ONE atomic commit per success criteria #7. Same atomic-commit pattern as Plans 11-01 + 11-02 + 11-03 + 11-04 + 11-05 (the established Phase 11 convention)._

## Files Created/Modified

- `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` — created (66 lines). 4 verified-from-Wave-0 `public const string` values + XML doc explaining the probe origin + refresh procedure.
- `tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` — created (103 lines). `Phase8WebAppFactory` subclass with OTEL_EXPORTER_OTLP_ENDPOINT env-var pin + 1s metric export interval override + optional log-level ctor.
- `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` — created (108 lines). Async polling helper with exponential backoff + 404/empty-hits tolerance + IDisposable HttpClient.
- `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` — created (133 lines). Async polling helper with mandatory 15s initial sleep + 3s poll interval + Uri.EscapeDataString + multi-label result-vector summation.

## Decisions Made

All design decisions inherited verbatim from Phase 11 CONTEXT.md D-17 (round-trip E2E pattern) + D-18 (HTTP client choice + polling shape are Claude's Discretion).

Execution-time judgment calls captured in `key-decisions` frontmatter:

- **Wave 0 revealed live ES shape is `otel` despite `mapping.mode: none`** — RESEARCH Open Q1 empirically resolved; constants baked accordingly.
- **EsIndexNames.cs constants file as single source of truth** — chosen over inline string literals so a future ES/collector upgrade is a one-file edit.
- **Single atomic commit (commit #6 of Phase 11)** — matches the Wave 1-3 + 5 atomic-commit precedent.
- **MTP `--filter-class` canonical syntax** — surfaced + documented during regression smoke; future test-filtered runs should use this rather than the silent-fallback VSTest shape.
- **Phase11WebAppFactory composition over OtelCollectorFixture evolution** — preserves the unmigrated Phase 5 facts until Plan 11-08 deletes the old fixture.
- **JsonElement.Clone-after-doc-disposal pattern carried verbatim** from OtelCollectorFixture line 211 — cross-fixture consistency for the JSON-detach idiom.

## Deviations from Plan

**None — plan executed exactly as written for all 6 tasks + single atomic commit.**

One minor execution-time discovery (not a Rule 1/2/3/4 deviation): the regression smoke command-line in the plan body used VSTest syntax (`--filter "FullyQualifiedName~HealthEndpointsTests"`) which Microsoft.Testing.Platform silently ignores (MTP0001 warning) + runs the entire suite. Switched to the canonical MTP shape `BaseApi.Tests.exe --filter-class "BaseApi.Tests.Observability.HealthEndpointsTests"` per the runner's `--help` output. Functionally equivalent for the verification intent (HealthEndpointsTests still ran + all 7 facts GREEN); the divergence is a documentation update for future test-filtered runs.

---

**Total deviations:** 0 auto-fixed
**Impact on plan:** All Wave 0 probe + file creations + the atomic commit landed per plan spec; build verification gates passed Release + Debug zero-warning; HealthEndpointsTests regression suite GREEN 7/7. No scope creep; no file content deviates from plan-as-written.

## Issues Encountered

- **Service `dotnet run` started on Kestrel-assigned ports 51538/51539 (NOT the plan-suggested :8080)** — `appsettings.Development.json` doesn't pin a Kestrel port, so on a fresh `dotnet run` ASP.NET Core assigned a random pair. Did not block the Wave 0 probe — the probe doesn't depend on the listener port (it only needs the SDK to flush logs to the collector, which the service does as soon as the OTel logger provider is built, which happens before `app.Run()`). The actual log record that triggered the data-stream creation was the `Microsoft.AspNetCore.Hosting.Diagnostics` "Application started" + "Request starting" pair emitted as soon as the host bound to the assigned ports. Plan body's reference to `curl http://localhost:8080/api/v1/schemas` was a suggested driver; the actual driver was `curl http://localhost:51539/api/v1/schemas` (returned 500 because Postgres was down, but the request itself flowed through the MEL bridge → OTel → collector → ES correctly, producing the `attributes.CorrelationId` field the probe verified).
- **Postgres was not running at probe start** — the post-Plan-11-04 stack state had ES + collector + Prom up but NOT Postgres (Plan 11-02 deferred + Plan 11-04 only brought up the prometheus service). The service's `StartupCompletionService` logged `Database migration failed on startup; readiness probe will remain unhealthy.` and the `/api/v1/schemas` request returned 500 because the NpgsqlHealthCheck failed. None of this blocked the Wave 0 probe — the OTel pipeline was wired before the migration ran, so the failure logs themselves became part of the probe's signal. For the HealthEndpointsTests regression smoke (Task 5), Postgres was brought up via `docker compose -f compose.yaml up -d postgres` (healthy in ~10s) before the test invocation.
- **MTP filter syntax discovery** — the plan body used `--filter "FullyQualifiedName~HealthEndpointsTests"` (VSTest) which produces `warning MTP0001: VSTest-specific properties are set but will be ignored when using Microsoft.Testing.Platform` and silently runs the entire test suite (203 tests, 135 failures because Postgres-dependent tests were running against the partially-up stack). Switched to the canonical MTP syntax `BaseApi.Tests.exe --filter-class "BaseApi.Tests.Observability.HealthEndpointsTests"` per `BaseApi.Tests.exe --help` output. Future test-filtered runs on this codebase should use `--filter-class` / `--filter-method` / `--filter-namespace` per the MTP help, NOT the legacy VSTest `--filter` predicate. Documented in `key-decisions` for forensic continuity.

## Self-Check: PASSED

**File existence verification:**
- FOUND: `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` (created — 66 lines)
- FOUND: `tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` (created — 103 lines)
- FOUND: `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` (created — 108 lines)
- FOUND: `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` (created — 133 lines)
- FOUND: `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-06-SUMMARY.md` (this file)

**Commit verification:**
- FOUND: `765b3fc` (subject: `test(observability): add Phase11WebAppFactory + ElasticsearchTestClient + PrometheusTestClient + EsIndexNames (Wave 0)`)
- `git show --stat HEAD` lists exactly 4 new files (410 insertions / 0 deletions)
- `git diff --diff-filter=D HEAD~1 HEAD` empty (no accidental file deletions)
- `git status --porcelain` (excluding pre-existing untracked planning + .claude + Properties paths outside this plan's scope) empty

**Plan-level verification gates (all PASS at commit 765b3fc):**
- `test -f tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` ✓
- `test -f tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` ✓
- `test -f tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` ✓
- `test -f tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` ✓
- `grep "public static class EsIndexNames" tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` returns 1 ✓
- `grep -E "^    public const string (LogsDataStream|CorrelationIdFieldPath|ResourceAttributesFieldPath|FieldShape)" tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` returns 4 ✓
- `! grep -E "<[A-Z_]+_FROM_TASK_0>" tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` (placeholder substitution) 0 matches ✓
- `! grep -iE "(PLACEHOLDER|TODO|FIXME|XXX)" tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` 0 matches ✓
- `grep "public class Phase11WebAppFactory : Phase8WebAppFactory" tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` returns 1 ✓
- `grep "ExportIntervalMilliseconds = 1_000" tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` returns 1 ✓
- `grep "OTEL_EXPORTER_OTLP_ENDPOINT" tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` returns 1 ✓
- `grep "http://localhost:4317" tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` returns 1 ✓
- `grep 'public Phase11WebAppFactory() : this(null)' tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` returns 1 (IClassFixture parameterless ctor) ✓
- `grep "internal Phase11WebAppFactory(string?" tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` returns 1 (LogLevel overload) ✓
- `grep "public sealed class ElasticsearchTestClient : IDisposable" tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` returns 1 ✓
- `grep "PollEsForLog" tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` returns >=2 (declaration + XML doc) ✓
- `grep "InitialDelayMs = 200" tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` returns 1 ✓
- `grep "MaxDelayMs     = 3_200" tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` returns 1 ✓
- `grep "http://localhost:9200" tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` returns 1 ✓
- `grep "EsIndexNames.LogsDataStream" tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` returns 1 ✓
- `grep "public sealed class PrometheusTestClient : IDisposable" tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` returns 1 ✓
- `grep "PollPrometheusUntilSumAtLeast" tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` returns >=1 ✓
- `grep "QueryPrometheus" tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` returns >=1 ✓
- `grep "SumSampleValues" tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` returns >=1 ✓
- `grep "InitialSleepMs = 15_000" tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` returns 1 ✓
- `grep "Uri.EscapeDataString(promql)" tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` returns 1 ✓
- `grep "http://localhost:9090" tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` returns 1 ✓
- `dotnet build SK_P.sln -c Release --no-restore` — 0 Warning(s) / 0 Error(s) ✓
- `dotnet build SK_P.sln -c Debug --no-restore` — 0 Warning(s) / 0 Error(s) ✓
- HealthEndpointsTests regression — 7/7 GREEN in 10.2s ✓
- `git log -1 --format=%s` — matches `test(observability): add Phase11WebAppFactory + ElasticsearchTestClient + PrometheusTestClient + EsIndexNames (Wave 0)` ✓
- `git show --stat HEAD` — 4 new files added ✓
- `git status --porcelain` (excluding pre-existing planning + .claude untracked paths) — empty ✓

**Threat model coverage (all 5 STRIDE entries verified):**
- T-11-06-T1 (test-only credentials leak in helper code) — both HTTP clients carry NO Authorization header, NO X-Api-Key, NO basic-auth credentials. `grep -i "authorization\|api[_-]?key\|basic\s*auth" tests/BaseApi.Tests/Observability/Helpers/` returns 0 matches. ✓
- T-11-06-T2 (PromQL injection via test code) — `Uri.EscapeDataString(promql)` present in PrometheusTestClient.QueryPrometheus. ✓
- T-11-06-T3 (correlation.id collision across concurrent tests) — XML doc paragraph in ElasticsearchTestClient documents the per-test unique-id discipline (caller's responsibility). ✓
- T-11-06-T4 (HttpClient socket exhaustion) — each helper has a single long-lived HttpClient owned in ctor + disposed in Dispose; both `_es.Dispose()` and `_prom.Dispose()` lines present. ✓
- T-11-06-T5 (Phase11WebAppFactory env-var persistence — ACCEPT disposition) — XML doc paragraph in internal ctor documents the process-wide side-effect pattern. ✓

**Plan success_criteria coverage (all 7 criteria PASS at commit 765b3fc):**
- #1 Wave 0 ES index name verification completed; verified constants baked into EsIndexNames.cs ✓
- #2 Phase11WebAppFactory exists; subclasses Phase8WebAppFactory; pins OTEL_EXPORTER_OTLP_ENDPOINT; overrides PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 1_000; optional log-level override ctor present ✓
- #3 ElasticsearchTestClient exists; exposes PollEsForLog with all required behaviors ✓
- #4 PrometheusTestClient exists; exposes PollPrometheusUntilSumAtLeast + QueryPrometheus + SumSampleValues with all required behaviors ✓
- #5 Solution builds zero-warning Release+Debug ✓
- #6 HealthEndpointsTests regression suite still GREEN (7/7) ✓
- #7 Single git commit `765b3fc` exists at HEAD; modifies/adds exactly 4 new files; working tree clean post-commit ✓

## User Setup Required

None — test-helper-only commit. No external service configuration required. The Phase 11 observability backend (ES :9200 + Prom :9090 + collector :8889/:13133) remains healthy from Plan 11-04. Postgres was brought up during Task 5 regression smoke and remains up for Plans 11-07/11-08 consumption.

## Next Phase Readiness

**Plan 11-07 (round-trip E2E test class)** is unblocked: the helpers + factory + verified constants are all in place. Plan 11-07 facts can `IClassFixture<Phase11WebAppFactory>`, instantiate `ElasticsearchTestClient` + `PrometheusTestClient` per-fact, drive an HTTP request via `Factory.CreateClient()`, and poll:
- `EsIndexNames.LogsDataStream` for a log document with matching `attributes.CorrelationId` (the Wave 0 verified field path).
- Prometheus for an `http_server_request_duration_seconds_*` data point with `service_name="sk-api"` label (the D-07 resource_to_telemetry_conversion target).

**Plan 11-08 (migrate Log/LogLevel/Metrics facts + delete OtelCollectorFixture)** is unblocked: `LogExportTests` / `LogLevelFilterTests` / `MetricsExportTests` can switch from `OtelCollectorFixture` to `Phase11WebAppFactory` + the new helpers; the `OtelCollectorFixture.cs` file becomes deletable after the migration. The `internal Phase11WebAppFactory(string? logLevelDefaultOverride)` ctor specifically serves the `LogLevelFilterTests` migration — its 2-arg `OtelCollectorFixture(connectionString, logLevelDefaultOverride)` call sites collapse to the cleaner 1-arg `new Phase11WebAppFactory(logLevelDefaultOverride)` shape.

The forensic property holds: Plan 11-06's atomic commit (765b3fc) is independently revertable. The Wave 0 probe value (live index = `logs-generic.otel-default`, field shape = `otel`) is now empirically locked into the test-helper constants and can be re-verified at any time by re-running the probe sequence documented in Task 0 against the live stack. The collector's deprecation warning (`mapping::mode config option is deprecated and ignored`) is acknowledged + benign; a future plan may switch the collector config to `X-Elastic-Mapping-Mode` client-metadata or `elastic.mapping.mode` scope-attribute syntax to silence the warning, but the underlying behavior + Phase 11 wire-shape is already in steady state.

---
*Phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack*
*Plan: 06*
*Completed: 2026-05-28*
