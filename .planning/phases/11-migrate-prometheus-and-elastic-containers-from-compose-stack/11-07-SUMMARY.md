---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
plan: 07
subsystem: testing
tags: [phase11-e2e, round-trip-tests, schemas-logs-e2e, schemas-metrics-e2e, elasticsearch-polling, prometheus-polling, correlation-id-isolation, route-template-preservation, rule-1-fix-forward, phase-11-wave-5]

# Dependency graph
requires:
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-06 helpers (commit 765b3fc) — Phase11WebAppFactory + ElasticsearchTestClient + PrometheusTestClient + EsIndexNames; consumed verbatim by both E2E facts via IClassFixture<Phase11WebAppFactory>, new ElasticsearchTestClient(), new PrometheusTestClient(), and EsIndexNames.CorrelationIdFieldPath.
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-02 compose-stack mutation (commit a3c0b20) — ES :9200 + Prom :9090 + collector :8889/:4317 reachable on host-DNS; E2E facts drive in-process Kestrel POSTs whose telemetry flows through this live stack.
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-03 collector config rewire (commit 1f8eb69) — elasticsearch exporter (mapping.mode none, OTel-otel-default fallback per Wave 0) + prometheus exporter on :8889 with resource_to_telemetry_conversion: true (D-07 — service_name label load-bearing for the metrics fact's PromQL).
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-04 prometheus.yml (commit b40299c) — Prom scrapes collector :8889 every 15s; PrometheusTestClient.InitialSleepMs=15_000 matches this discipline.
  - phase: 08-entity-build-out-migrations-docker-runtime-tests
    provides: SchemaCreateDto positional record (Name, Version, Description, Definition) consumed by both facts' POST body construction; SchemasIntegrationTests pattern for Schema POST.
  - phase: 04-cross-cutting-middleware-and-error-handling
    provides: CorrelationIdMiddleware — accepts 32-char hex correlation IDs supplied via X-Correlation-Id header; echoes them in response header (OBSERV-11 invariant sanity-checked by the logs fact).
provides:
  - tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs — 112-line public sealed class (1 [Fact]) driving Schema POST with per-test Guid.NewGuid():N correlation id, then PollEsForLog(queryBody, 30_000) for a hit with that id; asserts service.name=sk-api + correlation-id round-trip in the hit body
  - tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs — 113-line public sealed class (1 [Fact]) driving 3x Schema POST then PollPrometheusUntilSumAtLeast(promql, threshold: 3) for cumulative http_server_request_duration_seconds_count >= 3 on labels service_name="sk-api" + http_route="api/v{version:apiVersion}/Schemas"
  - Single atomic commit (e3016e2) — commit #7 of the Phase 11 sequence — adds exactly 2 new files; 225 insertions / 0 deletions
  - Empirical proof that the OTLP -> collector -> elasticsearch logs pipeline AND the OTLP -> collector -> prometheus metrics pipeline are both live end-to-end through the sk_p in-process Kestrel host; closes Phase 11 D-17 round-trip contract behaviorally
  - Empirical OTel HTTP instrumentation discovery — ASP.NET Core preserves the route-TEMPLATE literal as http_route label value (NOT the resolved URL path); the {version:apiVersion} Asp.Versioning route constraint + [controller] PascalCase token both flow through verbatim to Prom
affects: [11-08a (HealthEndpointsTests rebase — same Phase11WebAppFactory + ElasticsearchTestClient + PrometheusTestClient consumption pattern); 11-08b (LogExportTests + LogLevelFilterTests + MetricsExportTests migration — will reuse the per-test-unique correlation-id pattern + the Wave-0-verified EsIndexNames constants + the same PromQL label semantics including the route-template-literal discovery); 11-08c (Phase 11 close — these 2 E2E facts ARE the load-bearing round-trip evidence that the new observability stack works end-to-end)]

# Tech tracking
tech-stack:
  added: [none — all required libraries (Microsoft.AspNetCore.Mvc.Testing + Xunit v3 + System.Net.Http.Json) already pinned via CPM from Phase 5 / Phase 8 / Phase 11-06]
  patterns:
    - "Per-test-unique correlation-id isolation pattern (RESEARCH Pitfall 5 / T-11-03) — every E2E [Fact] generates `$\"{Guid.NewGuid():N}\"` as the X-Correlation-Id header value AND the ES `term` query parameter. The probability of a 128-bit GUID collision within a single suite run is negligible; combined with [Collection(\"Observability\")] DisableParallelization, the ES cumulative data stream stays cleanly partitionable across tests + suite runs without per-test ES cleanup."
    - "Cumulative-cleanliness assertion pattern (RESEARCH Pitfall 5 analog for Prom) — metrics fact asserts `totalCount >= RequestCount` (NOT `== RequestCount`) because Prom data is cumulative within the retention window. Suite re-runs may have left samples for the same label combination in Prometheus's TSDB; the >= guard makes the assertion robust to that state without forcing a Prom reset between runs."
    - "Route-template-preservation pattern (Rule 1 empirical discovery) — ASP.NET Core HTTP instrumentation emits the literal route TEMPLATE as the `http_route` label, NOT the resolved request URL. For `[Route(\"api/v{version:apiVersion}/[controller]\")]` decoration with `Asp.Versioning` constraint, the live label value is `\"api/v{version:apiVersion}/Schemas\"` (constraint syntax + PascalCase [controller] token both preserved). Plan-assumed URL form `\"api/v1/schemas\"` produces an empty result vector + 60s polling timeout. Reusable knowledge for any future PromQL assertion that filters on `http_route` for a versioned/decorated controller."
    - "Two-class-per-pipeline E2E pattern (RESEARCH Open Q3 / sk2_1 precedent) — separate `SchemasLogsE2ETests` + `SchemasMetricsE2ETests` classes instead of one combined class. Failure attribution is clean: a RED on the logs class points exactly at the ES pipeline (collector ES exporter + ES ingestion + field-shape constants), a RED on the metrics class points exactly at the Prom pipeline (collector Prom exporter + Prom scrape + PromQL labels). The cost of the second class (one extra `IClassFixture<>` activation + a few duplicate lines) is much smaller than the diagnostic noise saved on failure."
    - "Live-empirics-over-spec pattern (extension of Plan 11-06 Wave 0 probe pattern) — when a plan-as-written assertion (e.g., the `http_route=\"api/v1/schemas\"` PromQL label) fails empirically against the live stack, the executor probes the actual live shape (`curl http://localhost:8889/metrics | grep schemas`) and updates the assertion to match. The plan's literal grep gate (`grep 'http_route=\"api/v1/schemas\"' returns 1`) is documented as superseded; the must_haves invariant is satisfied SEMANTICALLY (the test correctly polls the actual emitted label and passes). Plan 06-01 / 08-01 / 10-02 / 11-04 educational-rephrase precedent extended to PromQL label semantics."

key-files:
  created:
    - "tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs — 112 lines: public sealed class : IClassFixture<Phase11WebAppFactory>. 1 [Fact] PostSchema_Surfaces_Created_LogRecord_In_Elasticsearch_With_CorrelationId. Generates per-test Guid:N correlation id; uses Phase11WebAppFactory.CreateClient() with X-Correlation-Id default header; POSTs valid SchemaCreateDto to /api/v1/schemas; asserts HTTP 201 + header echo; constructs term-query body against EsIndexNames.CorrelationIdFieldPath; calls ElasticsearchTestClient.PollEsForLog(queryBody, 30_000); asserts hit != null; asserts hit body contains 'sk-api' AND the correlation id."
    - "tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs — 113 lines: public sealed class : IClassFixture<Phase11WebAppFactory>. 1 [Fact] PostSchema_Increments_HttpServerRequestDurationCount_In_Prometheus. POSTs 3 valid SchemaCreateDto bodies to /api/v1/schemas; asserts each HTTP 201; constructs PromQL `http_server_request_duration_seconds_count{service_name=\"sk-api\",http_route=\"api/v{version:apiVersion}/Schemas\"}` (route-TEMPLATE literal per Rule 1 fix-forward); calls PrometheusTestClient.PollPrometheusUntilSumAtLeast(query, threshold: 3); asserts samples non-empty + SumSampleValues >= 3."
  modified: []

key-decisions:
  - "Rule 1 fix-forward: http_route label value corrected from URL form 'api/v1/schemas' (plan-as-written) to route-template literal 'api/v{version:apiVersion}/Schemas' (live empirical shape). The ASP.NET Core HTTP instrumentation passes the route template verbatim — preserving both the Asp.Versioning route constraint syntax ({version:apiVersion}) AND the [controller] token's PascalCase resolution (Schemas). Verified empirically against curl http://localhost:8889/metrics 2026-05-28. The plan's literal grep gate at <verify> line 377 (grep 'http_route=\"api/v1/schemas\"' returns 1) is superseded; the must_haves invariant at frontmatter line 21 is satisfied SEMANTICALLY — the test correctly polls the actual emitted label and passes. Task 3 troubleshooting step 5 explicitly anticipated this discrepancy as a possible diagnostic."
  - "Single atomic commit (commit #7 of Phase 11) — matches the Phase 11 Wave 1-4 + 5 + 6 atomic-commit precedent (Plans 11-01 / 11-02 / 11-03 / 11-04 / 11-05 / 11-06 all single atomic commits). 2 new files; no modifications to existing files; 225 insertions / 0 deletions. Forensic property: revert e3016e2 restores the prior state without affecting subsequent Phase 11 commits."
  - "Version-string assertion intentionally DROPPED in SchemasLogsE2ETests per checker WARNING #7 — Assert.Contains('sk-api') is the load-bearing service.name probe (D-07 resource_to_telemetry_conversion: true makes service.name the load-bearing label); hardcoding 'service.version=3.2.0' would couple the E2E test to appsettings.json Service.Version and break the test on any future version bump without any observability-behavior change. service.name is load-bearing; version is incidental."
  - "Cumulative-cleanliness assertion (>=, not ==) in SchemasMetricsE2ETests — the metrics test makes 3 POSTs and asserts totalCount >= 3. Prom data within the retention window is cumulative; suite re-runs MAY have left samples for the same label combination. The >= guard makes the assertion robust to that state without requiring a Prom TSDB reset between runs (which would defeat the value of Prom's cumulative semantics)."
  - "Schema POST as the traffic source (D-17 + planner's choice) — chosen over TestObservabilityController (Phase 5 helper) because (a) Schema is a real business endpoint that exercises the full HTTP-01..16 pipeline including DB write + xmin shadow concurrency token + correlation-id flow, and (b) the round-trip proof is stronger when the traffic source is a real business endpoint (the same path production users will exercise) rather than a test-only debug controller."

patterns-established:
  - "Per-test-unique correlation-id isolation pattern — every E2E fact generates $\"{Guid.NewGuid():N}\" as both header value AND ES query parameter; ES cumulative data stream stays cleanly partitionable across tests + suite runs."
  - "Cumulative-cleanliness assertion pattern (Prom analog of Pitfall 5) — assert >=, not ==, for any Prom counter sample because Prom data is cumulative within the retention window."
  - "Route-template-preservation pattern (Rule 1 empirical discovery) — ASP.NET Core HTTP instrumentation passes the route TEMPLATE literal as http_route label; any future PromQL with http_route= filter on a versioned/decorated controller must use the route template, NOT the resolved URL."
  - "Two-class-per-pipeline E2E pattern — separate logs vs metrics E2E classes give clean failure attribution at the cost of one duplicate IClassFixture activation."
  - "Live-empirics-over-spec pattern — when a plan-as-written assertion fails empirically against the live stack, executor probes the actual live shape and updates the assertion; plan's literal grep gate is documented as superseded; must_haves invariant satisfied semantically (Plan 06-01 / 08-01 / 10-02 / 11-04 educational-rephrase precedent extended to PromQL label semantics)."

requirements-completed: [OBSERV-13, OBSERV-14, TEST-07]
# OBSERV-13 (logs land in ES with OTLP field shape; Attributes.CorrelationId matches X-Correlation-Id; service.name=sk-api in resource attributes) — closed behaviorally by SchemasLogsE2ETests: drives a Schema POST with a unique correlation id, polls ES via the Wave-0-verified field path, asserts the hit body contains both 'sk-api' and the correlation id. The Plan 11-06 Wave 0 probe resolved the field-shape ambiguity (live = 'otel'); this plan closes the behavioral assertion.
# OBSERV-14 (HTTP server metrics scraped by Prometheus from otel-collector:8889 with service_name="sk-api" label) — closed behaviorally by SchemasMetricsE2ETests: drives 3 Schema POSTs, polls Prom for cumulative http_server_request_duration_seconds_count with the service_name="sk-api" label AND the route-template-literal http_route, asserts count >= 3. The D-07 resource_to_telemetry_conversion: true setting makes service_name appear; the Rule 1 fix-forward documents the route-template label-value behavior.
# TEST-07 (round-trip E2E test class(es) — drive a real HTTP request + poll BOTH backends within bounded budgets) — closed by both fact classes combined: SchemasLogsE2ETests for the ES half (30s budget); SchemasMetricsE2ETests for the Prom half (60s budget). Per-test unique correlation IDs (T-11-03 mitigation); [Trait("Phase","11")] + [Trait("Category","E2E")] + [Collection("Observability")] + IClassFixture<Phase11WebAppFactory> on both classes per success criteria #3.

# Metrics
duration: ~10min
completed: 2026-05-28
---

# Phase 11 Plan 07: SchemasLogsE2ETests + SchemasMetricsE2ETests Summary

**Phase 11 D-17 round-trip contract closed behaviorally — 2 new E2E test classes (SchemasLogsE2ETests + SchemasMetricsE2ETests) drive a real POST /api/v1/schemas through the in-process Kestrel host and assert both backends ingested the resulting telemetry within bounded budgets (ES 30s, Prom 60s); Rule 1 fix-forward corrects the http_route PromQL label from URL form to route-TEMPLATE literal per empirical OTel HTTP instrumentation discovery; single atomic commit e3016e2 (225 insertions / 0 deletions); 2/2 GREEN across 2 consecutive runs (~33s combined).**

## Performance

- **Duration:** ~10 min (includes Task 1 + Task 2 file creation + Rule 1 fix-forward iteration + Task 3 verification cycles + Task 4 atomic commit)
- **Started:** 2026-05-28T~14:50Z
- **Completed:** 2026-05-28T~15:00Z
- **Tasks:** 4 (Task 1 + Task 2 file creation; Task 3 checkpoint approved; Task 4 atomic commit)
- **Files modified:** 2 (both new)

## Accomplishments

- **Task 1 SchemasLogsE2ETests.cs created** as `public sealed class : IClassFixture<Phase11WebAppFactory>` with `[Trait("Phase","11")]` + `[Trait("Category","E2E")]` + `[Collection("Observability")]` decorations. The single `[Fact]` PostSchema_Surfaces_Created_LogRecord_In_Elasticsearch_With_CorrelationId:
  - Generates a per-test unique correlation id `$"{Guid.NewGuid():N}"` (Pitfall 5 isolation; T-11-03 mitigation).
  - Uses `_factory.CreateClient()` with `X-Correlation-Id` default header pinned.
  - POSTs a valid `SchemaCreateDto(Name: "E2E-Logs-{Guid:N}", Version: "1.0.0", Description: null, Definition: "{ \"$schema\": \"https://json-schema.org/draft/2020-12/schema\", \"type\": \"object\" }")` to `/api/v1/schemas`.
  - Asserts `HttpStatusCode.Created` + the OBSERV-11 X-Correlation-Id echo invariant.
  - Constructs a term-query body against `EsIndexNames.CorrelationIdFieldPath` (Wave-0-verified to `attributes.CorrelationId`).
  - Calls `ElasticsearchTestClient.PollEsForLog(queryBody, timeoutMs: 30_000)`; asserts the returned hit is non-null.
  - Asserts the hit's `_source` body contains both `"sk-api"` (load-bearing service.name probe per D-07) AND the correlation id (defensive round-trip check). Version-string assertion intentionally DROPPED per checker WARNING #7 (couples to appsettings.json Service.Version without observability-behavior gain).
- **Task 2 SchemasMetricsE2ETests.cs created** with same trait/collection/fixture decorations. The single `[Fact]` PostSchema_Increments_HttpServerRequestDurationCount_In_Prometheus:
  - POSTs `RequestCount=3` Schema bodies to `/api/v1/schemas`; asserts each `HttpStatusCode.Created`.
  - Constructs PromQL `http_server_request_duration_seconds_count{service_name="sk-api",http_route="api/v{version:apiVersion}/Schemas"}` (route-TEMPLATE literal per Rule 1 fix-forward; see Deviations below).
  - Calls `PrometheusTestClient.PollPrometheusUntilSumAtLeast(query, threshold: RequestCount)`.
  - Asserts samples non-empty + `SumSampleValues(samples) >= RequestCount` (cumulative-cleanliness pattern; T-11-07-T2 mitigation).
- **Task 3 smoke-run verification checkpoint** — approved by user 2026-05-28 after 2 consecutive 2/2 GREEN runs post-Rule-1-fix:
  - Run 1: logs ~15s, metrics ~16s, combined ~33s.
  - Run 2: logs ~15-16s, metrics ~15-17s, combined ~33s (per user resume-signal: "approved — logs: ~15-16s, metrics: ~15-17s, combined ~33s, 2 consecutive 2/2 greens post Rule 1 fix-forward").
  - Both timings well within the plan's < 90s budget; both facts independently GREEN.
- **Task 4 single atomic commit `e3016e2`** with verbatim subject `test(observability): add SchemasLogsE2ETests + SchemasMetricsE2ETests (Phase 11 D-17 round-trip)`. `git show --stat HEAD` lists exactly 2 new files; 225 insertions / 0 deletions; `git diff --diff-filter=D HEAD~1 HEAD` empty (no accidental deletions); working tree clean for tracked files post-commit (pre-existing planning/.claude/Properties untracked paths outside this plan's scope remain).

## Task Commits

Per Plan 11-07's atomic-commit contract (Task 4 prescribes a SINGLE commit creating exactly 2 files), this plan ships as ONE atomic commit:

1. **Task 1: Create SchemasLogsE2ETests.cs** — staged at task boundary (rolled into Task 4 commit)
2. **Task 2: Create SchemasMetricsE2ETests.cs (+ Rule 1 route-template fix-forward)** — staged at task boundary (rolled into Task 4 commit)
3. **Task 3: Smoke-run verification** — checkpoint, no commit (user-approved 2026-05-28 after 2 consecutive 2/2 greens post Rule 1 fix-forward)
4. **Task 4: Single atomic commit** — `e3016e2` (test)

**Plan metadata:** TBD — committed by execute-plan agent after SUMMARY + STATE updates.

_Note: Plan 11-07 deliberately ships as ONE atomic commit per Task 4's prescribed message + 2-file scope. Same atomic-commit pattern as Plans 11-01 through 11-06 (the established Phase 11 convention)._

## Files Created/Modified

- `tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs` — created (112 lines). Per-test-unique-correlation-id Schema POST + ES poll via Wave-0-verified field path; asserts service.name=sk-api + correlation-id round-trip in the hit body.
- `tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs` — created (113 lines). 3x Schema POST + Prom poll for cumulative http_server_request_duration_seconds_count on service_name="sk-api" + route-template-literal http_route labels; cumulative-cleanliness assertion (>=, not ==).

## Decisions Made

Execution-time judgment calls (captured in `key-decisions` frontmatter):

- **Rule 1 fix-forward — http_route label value** — corrected from URL form `"api/v1/schemas"` (plan-as-written) to route-TEMPLATE literal `"api/v{version:apiVersion}/Schemas"` (live empirical shape).
- **Single atomic commit** — matches Phase 11 Wave 1-6 atomic-commit precedent.
- **Version-string assertion dropped per checker WARNING #7** — Assert.Contains("sk-api") is load-bearing; version is incidental.
- **Cumulative-cleanliness assertion (>=, not ==)** — robust to Prom retention-window suite-rerun state.
- **Schema POST as the traffic source (D-17 + planner's choice)** — real business endpoint exercises full HTTP-01..16 pipeline.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Plan/Live Discrepancy] PromQL http_route label value corrected from URL form to route-template literal**

- **Found during:** Task 2 (SchemasMetricsE2ETests creation) + Task 3 (smoke-run verification — first attempt with plan-as-written `"api/v1/schemas"` produced an empty result vector and the poll timed out at 60s)
- **Issue:** Plan-as-written (verify line 377 + must_haves frontmatter line 21 + threat model T-11-07-T3) assumed the `http_route` Prom label value would be the URL-path form `"api/v1/schemas"` (lowercase, with `v1` resolved from `{version:apiVersion}`). Empirical verification against the live collector's Prometheus exporter (`curl http://localhost:8889/metrics | grep schemas`) revealed that ASP.NET Core HTTP instrumentation actually emits the route TEMPLATE literal verbatim — preserving both the `Asp.Versioning` route constraint syntax (`{version:apiVersion}`) AND the `[controller]` token's PascalCase resolution (`Schemas`). The live label value is `"api/v{version:apiVersion}/Schemas"`, NOT `"api/v1/schemas"`. The plan's PromQL with the URL form produces an empty result vector and times out the 60s polling budget.
- **Fix:** Updated `SchemasMetricsE2ETests.cs` to use the route-template-literal label value: `http_server_request_duration_seconds_count{service_name="sk-api",http_route="api/v{version:apiVersion}/Schemas"}`. Updated the XML doc + inline comment to document the route-template-preservation behavior + cite the 2026-05-28 empirical verification + reference Task 3 troubleshooting step 5's explicit anticipation of this diagnostic. Updated the Assert.True failure message to match the new label value.
- **Files modified:** `tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs` (the PromQL string + 2 doc/comment edits + assertion message)
- **Verification:** Post-fix `dotnet test --filter-class "BaseApi.Tests.Observability.SchemasMetricsE2ETests"` GREEN in ~15-17s across 2 consecutive runs; combined with SchemasLogsE2ETests (~15-16s) total ~33s. User approved at Task 3 checkpoint with resume-signal "approved — logs: ~15-16s, metrics: ~15-17s, combined ~33s, 2 consecutive 2/2 greens post Rule 1 fix-forward".
- **Plan authorization:** Plan body's Task 3 troubleshooting step 5 EXPLICITLY anticipated this discrepancy as a possible diagnostic — "If both show data but the assertion fails on count: check the `http_route` label value (Prom may have labeled the route differently — e.g., `/api/v1/schemas` with a leading slash if a newer instrumentation version changed semantics)." The plan's literal grep gate (line 377: `grep 'http_route="api/v1/schemas"' returns 1`) is documented as SUPERSEDED; the must_haves invariant (frontmatter line 21) is satisfied SEMANTICALLY — the test correctly polls the actual emitted label and passes empirically. Plan 06-01 / 08-01 / 10-02 / 11-04 educational-rephrase precedent applies (semantic invariant overrides literal grep gate when the plan-as-written assumes a wrong runtime shape).
- **Committed in:** `e3016e2` (Task 4 atomic commit — the fix landed as part of the SchemasMetricsE2ETests.cs file's initial creation)

---

**Total deviations:** 1 auto-fixed (Rule 1 - plan-vs-live discrepancy, plan-authorized via Task 3 troubleshooting step 5)
**Impact on plan:** Plan's intent (close OBSERV-14 behaviorally via a PromQL assertion on the http_server_request_duration_seconds_count counter filtered to sk-api + the /api/v1/schemas route) is preserved exactly; only the literal label string in the PromQL changed to match the live emission shape. No scope creep; no behavioral assertion weakened; both facts GREEN on first user-approved smoke gate.

## Issues Encountered

- **First Task 3 verification attempt produced an empty Prom result vector + 60s polling timeout** — see Rule 1 fix-forward above. Diagnosis took one `curl http://localhost:8889/metrics | grep schemas` invocation to confirm the route-template literal was being emitted (not the URL form). Plan body's Task 3 troubleshooting step 5 anticipated this exact scenario.
- **Live observability stack (ES + Prom + collector) needed to remain UP between Task 3 verification cycles** — the previous-agent context confirmed this was the case post-Plan-11-06 Task 5 regression smoke (Postgres + ES + collector + Prom all healthy). No re-bootstrap needed; the 2 Task 3 verification runs used the same long-lived stack instance.

## Self-Check: PASSED

**File existence verification:**
- FOUND: `tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs` (created — 112 lines)
- FOUND: `tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs` (created — 113 lines)
- FOUND: `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-07-SUMMARY.md` (this file)

**Commit verification:**
- FOUND: `e3016e2` (subject: `test(observability): add SchemasLogsE2ETests + SchemasMetricsE2ETests (Phase 11 D-17 round-trip)`)
- `git show --stat HEAD` lists exactly 2 new files (225 insertions / 0 deletions)
- `git diff --diff-filter=D HEAD~1 HEAD` empty (no accidental file deletions)
- `git status --porcelain` for tracked files in this plan's scope — empty (pre-existing untracked planning + .claude + Properties paths outside this plan's scope remain)

**Plan-level verification gates (all PASS at commit e3016e2, modulo the Rule 1 PromQL label fix-forward):**
- `test -f tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs` ✓
- `test -f tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs` ✓
- `grep "[Trait(\"Phase\", \"11\")]" tests/BaseApi.Tests/Observability/Schemas*E2ETests.cs` returns 2 ✓
- `grep "[Trait(\"Category\", \"E2E\")]" tests/BaseApi.Tests/Observability/Schemas*E2ETests.cs` returns 2 ✓
- `grep "[Collection(\"Observability\")]" tests/BaseApi.Tests/Observability/Schemas*E2ETests.cs` returns 2 ✓
- `grep "IClassFixture<Phase11WebAppFactory>" tests/BaseApi.Tests/Observability/Schemas*E2ETests.cs` returns 2 ✓
- `grep "EsIndexNames.CorrelationIdFieldPath" tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs` returns 1 ✓
- `grep "PollEsForLog" tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs` returns 1 ✓
- `grep "new ElasticsearchTestClient" tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs` returns 1 ✓
- `grep "Guid.NewGuid():N" tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs` returns 2 (corrId + DTO name) ✓
- `grep "PollPrometheusUntilSumAtLeast" tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs` returns 1 ✓
- `grep "new PrometheusTestClient" tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs` returns 1 ✓
- `grep "http_server_request_duration_seconds_count" tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs` returns 1 ✓
- `grep 'service_name="sk-api"' tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs` returns 1 ✓
- `grep 'http_route="api/v1/schemas"' tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs` returns 0 — SUPERSEDED by Rule 1 fix-forward; the live label is `http_route="api/v{version:apiVersion}/Schemas"` per the empirical OTel HTTP instrumentation discovery. Plan's literal grep gate documented as superseded in Deviations section above; must_haves invariant satisfied semantically (the test correctly polls the actual emitted label and passes).
- `dotnet test --filter-class "BaseApi.Tests.Observability.SchemasLogsE2ETests"` + `dotnet test --filter-class "BaseApi.Tests.Observability.SchemasMetricsE2ETests"` — 2/2 GREEN across 2 consecutive runs (~15-17s each fact; ~33s combined) per user-approved Task 3 checkpoint ✓
- `git log -1 --format=%s` — matches `test(observability): add SchemasLogsE2ETests + SchemasMetricsE2ETests (Phase 11 D-17 round-trip)` ✓
- `git show --stat HEAD` — 2 new files added ✓
- `git status --porcelain` for tracked files in this plan's scope — empty ✓

**Threat model coverage (all 5 STRIDE entries verified):**
- T-11-07-T1 (correlation.id collision causes false-positive assertion across concurrent tests) — per-test unique `$"{Guid.NewGuid():N}"` correlation id; `grep "Guid.NewGuid():N" tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs` returns 2 (corrId + DTO Name). [Collection("Observability")] DisableParallelization serializes against sibling facts. ✓
- T-11-07-T2 (Prom assertion on `==` instead of `>=` causes flakes after suite re-runs) — `grep "totalCount >= RequestCount" tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs` returns 1. ✓
- T-11-07-T3 (over-broad PromQL match yields wrong samples) — PromQL filters on BOTH `service_name="sk-api"` AND `http_route="api/v{version:apiVersion}/Schemas"` (route-template literal per Rule 1 fix-forward, exact match). Only sk-api's POST /api/v1/schemas traffic produces this exact label pair. Phase 9 added POST /api/v1/orchestration/start etc. but those have different `http_route` values (`api/v{version:apiVersion}/Orchestration`). ✓
- T-11-07-T4 (Schema POST body invalid JSON Schema, fails 400, no metric emitted) — Definition uses minimal valid Draft-2020-12 schema; `grep 'json-schema.org/draft/2020-12/schema' tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs` returns 1, same for SchemasMetricsE2ETests. POST returns 201 (verified by assertion on every request). ✓
- T-11-07-T5 (test uses host-DNS while in-process SDK uses compose-DNS) — Phase11WebAppFactory ctor pins `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` per Plan 11-06 Task 2 (verified at that plan). Helpers use host-DNS (http://localhost:9200/, http://localhost:9090/) because the test process is OUTSIDE the compose network. ✓

**Plan success_criteria coverage (all 8 criteria PASS at commit e3016e2, modulo Rule 1 fix-forward for SC#5's http_route label value):**
- #1 SchemasLogsE2ETests.cs exists with 1 [Fact] driving Schema POST + polling ES via PollEsForLog against EsIndexNames.CorrelationIdFieldPath ✓
- #2 SchemasMetricsE2ETests.cs exists with 1 [Fact] driving 3 Schema POSTs + polling Prom via PollPrometheusUntilSumAtLeast for http_server_request_duration_seconds_count{service_name="sk-api",...} with threshold 3 — http_route label value is route-template literal per Rule 1 fix-forward (live empirical shape) ✓
- #3 Both classes carry [Trait("Phase","11")] + [Trait("Category","E2E")] + [Collection("Observability")] + IClassFixture<Phase11WebAppFactory> ✓
- #4 Per-test unique correlation IDs (`$"{Guid.NewGuid():N}"`) — Pitfall 5 / T-11-03 mitigation ✓
- #5 Metrics assertion uses `>=` not `==` (Pitfall 5 cleanliness) ✓
- #6 Solution builds zero-warning Release (verified during Task 3 smoke-run cycles; both facts compiled before being run) ✓
- #7 `dotnet test --filter "Category=E2E"` exits 0; both facts GREEN against live stack (Task 3 checkpoint user-approved with 2/2 GREEN across 2 consecutive runs) ✓
- #8 Single git commit `e3016e2` matching the exact prescribed subject; adds exactly 2 new files; working tree clean for tracked files post-commit ✓

## User Setup Required

None — test-only commit. The Phase 11 observability backend (ES :9200 + Prom :9090 + collector :8889/:4317/:13133) remains healthy from Plans 11-04 + 11-06 Task 5. Postgres remains up. No external service configuration required.

## Next Phase Readiness

**Plan 11-08a (HealthEndpointsTests rebase off OtelCollectorFixture)** is unblocked: Phase11WebAppFactory + ElasticsearchTestClient + PrometheusTestClient + EsIndexNames are all in place + empirically verified GREEN through this plan's 2 E2E facts. The HealthFilterEnabledFixture nested class can rebase to Phase11WebAppFactory; the 3 other nested fixtures can rebase to Phase8WebAppFactory; the migration of `Test_HealthEndpoints_Absent_From_OTLP_Logs` from `factory.ReadExportedLogs()` to `new ElasticsearchTestClient().PollEsForLog(queryBody, timeoutMs: 8_000)` with negative assertion is a direct application of this plan's same polling primitive (with a tighter budget per the RESEARCH PATTERNS option-a discipline).

**Plan 11-08b (LogExportTests + LogLevelFilterTests + MetricsExportTests migration)** is unblocked: all 3 fact classes can switch from OtelCollectorFixture to Phase11WebAppFactory + the new helpers. The per-test-unique-correlation-id pattern + the EsIndexNames constants + the http_route route-template-literal discovery are all reusable for the LogExportTests + LogLevelFilterTests + MetricsExportTests migrations. The `internal Phase11WebAppFactory(string? logLevelDefaultOverride)` ctor specifically serves the LogLevelFilterTests migration.

**Plan 11-08c (OtelCollectorFixture deletion + Phase 11 close)** is unblocked at the contract level — these 2 E2E facts are the load-bearing round-trip proof that OBSERV-13 + OBSERV-14 are behaviorally closed. After 11-08a + 11-08b move HealthEndpointsTests + Log/LogLevel/Metrics facts off OtelCollectorFixture, the final `git rm tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` becomes a clean defensive-grep-then-delete operation.

The forensic property holds: Plan 11-07's atomic commit (e3016e2) is independently revertable. The Rule 1 fix-forward (http_route route-template-literal label value) is empirically locked into the SchemasMetricsE2ETests assertion + XML doc + inline comment; the discovery is reusable across all future PromQL assertions on http_route for versioned/decorated controllers. The 2/2 GREEN smoke-gate evidence (logs ~15-16s, metrics ~15-17s, combined ~33s) is documented in the Task 3 resume-signal and informs the budget envelope for Plan 11-08's 3-consecutive-green cadence.

---
*Phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack*
*Plan: 07*
*Completed: 2026-05-28*
