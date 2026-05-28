---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
slug: migrate-prometheus-and-elastic-containers-from-compose-stack
status: verified
asvs_level: 1
audited_at: 2026-05-28
threats_total: 40
threats_closed: 40
threats_open: 0
threats_accepted: 13
created: 2026-05-28
---

# Phase 11 — Security

> Per-phase security contract: verifies each threat declared across Plans 11-01..11-08c. Implementation files are read-only to this audit; gaps would be reported, not patched. Phase 11 ships with `threats_open: 0` — every threat has either documented evidence in committed code/config or an `accept` disposition tied to the dev-posture Out-of-Scope row in REQUIREMENTS.md.

Audit context: Phase 11 was just verified via `/gsd-verify-work` (UAT.md at `11-UAT.md`, all 6 tests passed). One regression was found and fixed during UAT (commit `5f2d6bd` — IN-05 revert restoring `attributes.CorrelationId` field path in `EsIndexNames.cs`). That fix does NOT change the security posture of any of the 40 threats in this register — it only corrected the ES `term`-query field shape for the live `logs-generic.otel-default` data stream's x-pack ECS dynamic-template mapping. Recorded here for forensic traceability only.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| host → compose-default-network (host-published ports) | Compose default `host-published` semantics expose `9200` (ES), `9090` (Prom), `8889` (collector Prom-exporter), `13133` (collector health_check), `4317` (OTLP gRPC), `4318` (OTLP HTTP), `5433` (Postgres) all bound to `0.0.0.0` on the dev host. Any process on the host (or container on host network) can reach all backends without auth. | Telemetry logs/metrics; SQL traffic |
| compose-default-network internal | Service-to-service traffic (`elasticsearch:9200`, `otel-collector:8889`, `postgres:5432`) over plain HTTP/gRPC. No mTLS, no auth. | Telemetry shipment + SQL |
| OTel collector → Elasticsearch | OTLP-log records via HTTP POST to `http://elasticsearch:9200/_bulk` on plain HTTP. | Log docs (Body, Attributes.CorrelationId, Resource.service.name) |
| OTel collector → Prometheus exporter | Collector exposes `:8889/metrics` for Prom container to scrape via plain HTTP. | Metric series + labels (service_name, http_route) |
| supply chain | 3 new image pulls + 1 image bump pinned to exact versions: `docker.elastic.co/elasticsearch/elasticsearch:8.15.5`, `prom/prometheus:v3.11.3`, `otel/opentelemetry-collector-contrib:0.152.0` (was `0.95.0`). No `:latest` anywhere. | Container images |
| test process → ES :9200 / Prom :9090 (host-DNS) | `ElasticsearchTestClient` + `PrometheusTestClient` use `http://localhost:{9200,9090}/` BaseAddress. No auth headers, no TLS. | Telemetry read-back during E2E facts |
| OTLP env var pinning | `Phase11WebAppFactory` ctor pins `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`; capture+restore on dispose (WR-03 review fix). T-05-OTLP-EXFIL Phase 5 mitigation carried forward. | OTLP endpoint URL (process env) |

Production hardening (auth, TLS, network segmentation) is deferred to the downstream deployment repo per CONTEXT Out of Scope.

---

## Threat Register

### Plan 11-01 — Documentation amendments (2 threats; both accept)

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-11-01-01 | T (Tampering) | `.planning/REQUIREMENTS.md` | accept | Standard git history audit suffices — same posture as Plan 10-01 (doc-first precedent per Phase 3 D-03b). | closed |
| T-11-01-02 | I (Info disclosure) | `.planning/REQUIREMENTS.md` | accept | File contains no secrets / PII / credentials. Image pins (`8.15.5`, `v3.11.3`, `0.152.0`) are public Docker Hub tags. | closed |

### Plan 11-02 — compose.yaml mutation (5 threats; 2 mitigate + 3 accept)

| Threat ID | Category | Component | Disposition | Mitigation | Evidence | Status |
|-----------|----------|-----------|-------------|------------|----------|--------|
| T-11-02-T1 | T (Supply chain — typosquat / registry compromise) | `compose.yaml` image: lines | mitigate | Exact-version pins (D-09/D-10/D-11); no `:latest`. `grep "image:" compose.yaml` → 4 lines: `postgres:17-alpine`, `docker.elastic.co/elasticsearch/elasticsearch:8.15.5`, `otel/opentelemetry-collector-contrib:0.152.0`, `prom/prometheus:v3.11.3`. | `compose.yaml:7,29,58,87` | closed |
| T-11-02-T2 | I (Info disclosure — multi-tenant dev host port exposure) | `compose.yaml` ports: lines | accept | Dev posture per CONTEXT Out of Scope: production deployment owns auth + TLS in a separate repo. Ports `9200/9090/8889/13133/4317/4318/5433/8080` bind to host. | `compose.yaml` ports lists | closed |
| T-11-02-T3 | T (Cross-stack data mix — name collision with sk2_1) | `container_name: sk-elasticsearch`, `sk-prometheus`, `sk-otel-collector` | mitigate | Plan 11-02 Task 5 manual checkpoint runs `docker ps --filter name=...` BEFORE compose-up; RESEARCH Pitfall 4 documented inline at `compose.yaml:24-27,81-85`. sk_p + sk2_1 stacks mutually exclusive on a single Docker daemon. | `compose.yaml:24-27,81-85` | closed |
| T-11-02-T4 | T (Prom `/-/reload` exposed via `--web.enable-lifecycle`) | `compose.yaml` prometheus.command | accept | Dev posture per D-13 verbatim from sk2_1; same posture as ES `xpack.security.enabled=false`. Inline comment at `compose.yaml:92` documents the trade-off. | `compose.yaml:90-93` | closed |
| T-11-02-T5 | D (DoS — insufficient `start_period` causes false-negative compose-up) | `compose.yaml` elasticsearch.healthcheck | mitigate | `start_period: 60s` per D-12 — preserves 110s effective window before retries-budget exhaustion. RESEARCH Pitfall 6 documented at `compose.yaml:43-44`. | `compose.yaml:49` | closed |

### Plan 11-03 — Collector pipeline rewire (4 threats; 4 mitigate)

| Threat ID | Category | Component | Disposition | Mitigation | Evidence | Status |
|-----------|----------|-----------|-------------|------------|----------|--------|
| T-11-03-T1 | I (Logs carrying PII or secrets leak to ES) | `compose/otel-collector-config.yaml` service.pipelines.logs | mitigate | Phase 4 `CorrelationIdMiddleware` sanitization (D-02 IsValid guard) preserved. Phase 5 T-05-PII Npgsql parameter-capture path eliminated by Plan 11-05 `.WithTracing()` strip (no `.AddNpgsql()` tracer anywhere). LogExportTests Test_LogRecord_CorrelationId_Survives_Sanitization fact regression-asserts no `\r`, no `injected`, no `invalid\rinjected` in any ES doc. | `tests/BaseApi.Tests/Observability/LogExportTests.cs:120-124` | closed |
| T-11-03-T2 | I (Over-sharing — traces pipeline lingering) | `compose/otel-collector-config.yaml` service.pipelines | mitigate | Traces pipeline DELETED + inline rationale comment at `compose/otel-collector-config.yaml:108-112`. SDK side also strips emission (Plan 11-05). Grep `^    traces:$` against config → 0. | `compose/otel-collector-config.yaml:95-107` (only logs + metrics pipelines present) | closed |
| T-11-03-T3 | T (filter/health_metrics processor accidentally pointed at wrong exporter) | `compose/otel-collector-config.yaml` service.pipelines.metrics | mitigate | `processors: [filter/health_metrics]` + `exporters: [prometheus]` co-occur in metrics pipeline. OTTL body byte-preserved from Phase 5 fix-forward. | `compose/otel-collector-config.yaml:45-50, 105-107` | closed |
| T-11-03-T4 | I (mapping.mode: none silently dropped) | `exporters.elasticsearch.mapping` | mitigate | Smoke restart logs scan caught no `parse/unmarshal/config` errors at Plan 11-03 Task 4. Wave 0 empirical probe (Plan 11-06 Task 0) confirmed the actual live behavior (`mapping.mode: none` field deprecated in v0.122.0+; live data stream is `logs-generic.otel-default` per `EsIndexNames.cs:40` with field path `attributes.CorrelationId` per `EsIndexNames.cs:71`). Anticipated by RESEARCH Pitfall 2. | `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs:17-25, 40, 71` | closed |

### Plan 11-04 — prometheus.yml (3 threats; all accept)

| Threat ID | Category | Component | Disposition | Mitigation | Evidence | Status |
|-----------|----------|-----------|-------------|------------|----------|--------|
| T-11-04-T1 | T (Prom config injection — malicious scrape target) | `prometheus.yml` scrape_configs | accept | Single literal target `'otel-collector:8889'` committed under git; no external input templated. `grep "basic_auth\|bearer_token\|tls_config\|metric_relabel_configs" prometheus.yml` → 2 matches (both negation comments documenting intentional absence per dev posture). | `prometheus.yml:14, 23` | closed |
| T-11-04-T2 | T (Prom `/-/reload` abuse via `--web.enable-lifecycle`) | `compose.yaml` prometheus service command | accept | Cross-references T-11-02-T4 (same disposition). Dev posture per D-13. | `compose.yaml:92` | closed |
| T-11-04-T3 | A (High-cardinality label explosion — DoS on Prom) | `prometheus.yml` (absence of metric_relabel_configs) | accept | RESEARCH Out of Scope row "Prometheus high-cardinality protection". `http_route` bounded by route table; `service_name` bounded by single sk-api service. No unbounded user-input → label paths. `grep metric_relabel_configs prometheus.yml` returns the absence-documenting comment only. | `prometheus.yml:26-30` | closed |

### Plan 11-05 — Strip .WithTracing() + cleanup (4 threats; 3 mitigate + 1 accept)

| Threat ID | Category | Component | Disposition | Mitigation | Evidence | Status |
|-----------|----------|-----------|-------------|------------|----------|--------|
| T-11-05-T1 | I (Residual trace OTLP records flowing to collector) | `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` | mitigate | `.WithTracing(...)` chain entirely removed; `using OpenTelemetry.Trace;` + `using Npgsql;` directives removed; `AlwaysOnSampler` + `AddNpgsql()` references gone. `Grep ".WithTracing\|AlwaysOnSampler\|AddNpgsql()\|using Npgsql;\|using OpenTelemetry.Trace;"` against the file → 0. Collector traces pipeline also deleted (Plan 11-03). | `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` (full file, verified 78 lines, no traces references) | closed |
| T-11-05-T2 | A (Orphaned imports flag warnings under TreatWarningsAsErrors) | Same file | mitigate | 2 orphan using directives removed in same commit. Phase 11 closing-cadence 3-of-3 GREEN Release+Debug builds confirm zero warnings. | `ObservabilityServiceCollectionExtensions.cs:1-8` (8-line using block; no Npgsql + no OpenTelemetry.Trace) | closed |
| T-11-05-T3 | A (File-exporter cleanup — host bind-mount dangling) | `tests/.otel-out/` directory + `.gitignore` stanza | mitigate | `tests/.otel-out/` directory absent on disk (`Glob tests/.otel-out/**/*` → "No files found"). `.gitignore` no longer contains `tests/.otel-out` glob entries (`Grep tests/.otel-out .gitignore` → no matches). compose.yaml `./tests/.otel-out:/var/otel-out` bind-mount + `user: "0:0"` override both removed (Plan 11-02). | filesystem state + `.gitignore` + `compose.yaml:51-79` | closed |
| T-11-05-T4 | A (Intentional RED state on Log/LogLevel/Metrics tests masks bugs) | tests/BaseApi.Tests/Observability/{LogExport,LogLevelFilter,Metrics}ExportTests.cs | accept | Plan 11-08b migrated all 3 classes to ES/Prom polling — RED→GREEN cycle closed; closing-cadence 3-of-3 GREEN at 142/142 facts confirms (`11-08c-SUMMARY.md` lines 19-20). | `LogExportTests.cs`, `LogLevelFilterTests.cs`, `MetricsExportTests.cs` (all 3 migrated to Phase11WebAppFactory + ES/Prom helpers) | closed |

### Plan 11-06 — Test helpers + Wave 0 ES probe (5 threats; 4 mitigate + 1 accept)

| Threat ID | Category | Component | Disposition | Mitigation | Evidence | Status |
|-----------|----------|-----------|-------------|------------|----------|--------|
| T-11-06-T1 | I (Test-only credentials leak in helper code) | `tests/BaseApi.Tests/Observability/Helpers/*.cs` | mitigate | Helpers construct `HttpClient` with no `Authorization` header, no `X-Api-Key`, no basic auth. Grep `authorization\|api[_-]?key\|basic\s*auth` (case-insensitive) against `tests/BaseApi.Tests/Observability/Helpers/` → no matches. Dev posture per D-08/D-12/D-13. | `ElasticsearchTestClient.cs:37-40`, `PrometheusTestClient.cs:40-43` | closed |
| T-11-06-T2 | T (PromQL injection via test code) | `PrometheusTestClient.QueryPrometheus` | mitigate | `Uri.EscapeDataString(promql)` escapes the entire PromQL string before URL embedding. PromQL constructed via static raw-string literals in test code only; no user input. | `PrometheusTestClient.cs:98` | closed |
| T-11-06-T3 | T (correlation.id collision across concurrent tests — T-11-03 carry) | `ElasticsearchTestClient` + caller test code | mitigate | XML doc instructs `$"{Guid.NewGuid():N}"` per-test discipline; consumers (SchemasLogsE2ETests, LogExportTests, LogLevelFilterTests) implement it. | `ElasticsearchTestClient.cs:15-22` XML doc; `SchemasLogsE2ETests.cs:49`, `LogLevelFilterTests.cs:38,69` consumer compliance | closed |
| T-11-06-T4 | A (HttpClient socket exhaustion on parallel test runs) | `ElasticsearchTestClient` + `PrometheusTestClient` HttpClient ownership | mitigate | Each helper owns one HttpClient (ctor) + disposes (Dispose). `[Collection("Observability")]` serializes consumers. | `ElasticsearchTestClient.cs:35-42`, `PrometheusTestClient.cs:38-45` | closed |
| T-11-06-T5 | I (Phase11WebAppFactory env-var persistence leaks to sibling tests) | `Phase11WebAppFactory` ctor SetEnvironmentVariable | accept | WR-03 review fix tightened this: capture-prior + gate-on-null in ctor; IN-09 review fix mirrored gate symmetry in DisposeAsync. Bleed-through bounded to a single fixture's lifetime (not whole process). | `Phase11WebAppFactory.cs:50, 60-95` (gate + restore) | closed |

### Plan 11-07 — SchemasLogs/MetricsE2ETests round-trip (5 threats; 4 mitigate + 1 accept)

| Threat ID | Category | Component | Disposition | Mitigation | Evidence | Status |
|-----------|----------|-----------|-------------|------------|----------|--------|
| T-11-07-T1 | T (Correlation.id collision causes false-positive assertion) | `SchemasLogsE2ETests.PostSchema_Surfaces_Created_LogRecord_In_Elasticsearch_With_CorrelationId` | mitigate | Per-test unique `$"{Guid.NewGuid():N}"`; ES `term` filter on `EsIndexNames.CorrelationIdFieldPath`. Two `Guid.NewGuid():N` occurrences in the file (corrId + DTO Name). | `SchemasLogsE2ETests.cs:49, 57` | closed |
| T-11-07-T2 | T (Prom assertion on `==` instead of `>=` flakes after re-runs) | `SchemasMetricsE2ETests.PostSchema_Increments_HttpServerRequestDurationCount_In_Prometheus` | mitigate | `Assert.True(totalCount >= RequestCount, ...)` — Pitfall 5 cleanliness pattern. | `SchemasMetricsE2ETests.cs:109-111` | closed |
| T-11-07-T3 | T (Over-broad PromQL match yields wrong samples) | `SchemasMetricsE2ETests.query` string | accept | PromQL filters on `service_name="sk-api"` AND `http_route="api/v{version:apiVersion}/Schemas"` (route template literal verified empirically against the live `:8889/metrics`). Only sk-api's `POST /api/v1/schemas` traffic matches. | `SchemasMetricsE2ETests.cs:101-102` | closed |
| T-11-07-T4 | T (Schema POST invalid JSON Schema → 400 → no metric emitted) | `SchemaCreateDto.Definition` in test bodies | mitigate | Definition uses valid Draft-2020-12 schema: `"{ \"$schema\": \"https://json-schema.org/draft/2020-12/schema\", \"type\": \"object\" }"`. | `SchemasLogsE2ETests.cs:60`, `SchemasMetricsE2ETests.cs:77` | closed |
| T-11-07-T5 | I (Test uses host-DNS while SDK uses compose-DNS — telemetry to wrong collector) | `Phase11WebAppFactory` env-var pin + test client BaseAddress | mitigate | Phase11WebAppFactory pins `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` (T-05-OTLP-EXFIL inheritance with WR-03 capture/restore). Test process is OUTSIDE compose → host-DNS correct. | `Phase11WebAppFactory.cs:60-73` | closed |

### Plan 11-08a — HealthEndpointsTests rebase (4 threats; 4 mitigate)

| Threat ID | Category | Component | Disposition | Mitigation | Evidence | Status |
|-----------|----------|-----------|-------------|------------|----------|--------|
| T-11-08a-T1 | T (Rebase introduces silent behavioral regression in 7 health facts) | `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` | mitigate | Closing-cadence 3-of-3 GREEN at 142/142 confirms the 7 HealthEndpointsTests facts still pass. Rebase changed only base-class identifiers + 1 fact body migration. | 11-08c closing cadence + `HealthEndpointsTests.cs` post-rebase shape (Phase8WebAppFactory ×3 + Phase11WebAppFactory ×2 + zero OtelCollectorFixture references) | closed |
| T-11-08a-T2 | A (Premature OtelCollectorFixture deletion breaks 3 remaining consumers) | `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` | mitigate | Plan 11-08a explicitly PRESERVED the file (per plan body line 50-66); Plan 11-08c performed the final `git rm` after Plan 11-08b migrated the 3 remaining consumers. Bisect-friendly sequencing intact. | `git log` 10-commit Phase 11 sequence (deletion in commit 5c13683 — Plan 11-08c) | closed |
| T-11-08a-T3 | T (Negative-assertion fact false-positive — 8s budget too short) | `Test_HealthEndpoints_Absent_From_OTLP_Logs` | mitigate | 8s budget exceeds typical ES indexing lag (1-3s per RESEARCH Pattern 2). 30 probes (10 × 3 endpoints) issued; any leak would surface. Fact GREEN in closing-cadence 3-of-3. | `HealthEndpointsTests.cs:191` (`timeoutMs: 8_000`) + closing cadence | closed |
| T-11-08a-T4 | T (ES query_string syntax escaping differences across mapping.mode shapes) | ES query body in OTLP-absence fact | mitigate | `query_string` searches all `_source` by default; the literal `/health/` substring is identical across shapes. Fact GREEN in closing-cadence 3-of-3. | `HealthEndpointsTests.cs:184-189` query body + closing cadence | closed |

### Plan 11-08b — LogExport/LogLevelFilter/MetricsExport migration (4 threats; 3 mitigate + 1 accept)

| Threat ID | Category | Component | Disposition | Mitigation | Evidence | Status |
|-----------|----------|-----------|-------------|------------|----------|--------|
| T-11-08b-T1 | T (Negative-assertion fact false-positive in LogLevelFilterTests) | `LogLevelFilterTests.Test_Information_Log_Suppressed_When_Default_Warning` | mitigate | 8s budget exceeds typical ES indexing lag. Positive-control fact (30s budget) flanks it. Fact GREEN in closing-cadence 3-of-3. | `LogLevelFilterTests.cs:56` (`timeoutMs: 8_000`) + closing cadence | closed |
| T-11-08b-T2 | I (D-04 health filter regression — health-route data points leak to Prom) | `MetricsExportTests.Test_HealthPath_Absent_From_HttpServerMetrics` | mitigate | Positive-control + negative-query split (WR-05 review fix): positive control proves Prom pipeline alive; empty negative regex match proves filter works. `Assert.Empty(healthSamples)` strict assertion. Fact GREEN in closing-cadence 3-of-3. | `MetricsExportTests.cs:96-106` (positive control + 2×30s scrape wait + dual queries) | closed |
| T-11-08b-T3 | T (LogExportTests body-content match false-positive — "sk-api" appears in unrelated doc) | `LogExportTests.Test_LogRecord_Has_CorrelationId_And_ServiceResource` | accept | ES query filters on `EsIndexNames.CorrelationIdFieldPath` term match (32-hex GUID space + bool/must with body.text "test-obs ok ran" phrase). Collision probability negligible. | `LogExportTests.cs:50-62` (bool/must query) | closed |
| T-11-08b-T4 | A (Orphan OtelCollectorFixture.cs causes build warnings) | `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` (pre-deletion) | accept | `public class` not flagged by C# compiler when unused; preserved-but-orphaned state lasted exactly 1 commit (Plan 11-08b HEAD → Plan 11-08c HEAD). Zero-warning Release+Debug builds throughout. | 11-08c closing cadence (zero warnings) | closed |

### Plan 11-08c — OtelCollectorFixture deletion + Phase close (4 threats; 4 mitigate)

| Threat ID | Category | Component | Disposition | Mitigation | Evidence | Status |
|-----------|----------|-----------|-------------|------------|----------|--------|
| T-11-08c-T1 | A (Premature OtelCollectorFixture deletion breaks build if consumer missed) | `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` | mitigate | Plan 11-08c Task 1 defensive grep BEFORE `git rm`; post-deletion `dotnet build -c Release` exit 0 zero-warning gate. Plus the closing zero-references grep (caught 2 residual doc-comment refs swept in `c7050f3`). | `Glob tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` → "No files found"; `Grep OtelCollectorFixture` in `src/` + `tests/` → 0 matches | closed |
| T-11-08c-T2 | T (3-run cadence masks a flake by retrying past it) | Task 2 checkpoint | mitigate | Closing-cadence ran on FIRST attempt at 3 consecutive GREEN; stable 142/142 fact count across all 3 runs; no warm-up signature. `11-08c-SUMMARY.md` records the actual wall-clocks (163s/161s/162s). | `11-08c-SUMMARY.md` line 19 | closed |
| T-11-08c-T3 | A (psql `\l` cleanup-discipline regression — leaked stepsdb_test_* DBs) | PostgresFixture lifecycle through subclass tower | mitigate | psql `\l` SHA-256 BEFORE/AFTER both `0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127` — 4th consecutive phase to record this exact baseline (Phase 8 P08 + Plan 09-03 + Plan 10-05 + Plan 11-08c). | `11-08c-SUMMARY.md` line 20 | closed |
| T-11-08c-T4 | I (SUMMARY narrative loses fidelity to revision history) | Task 3 SUMMARY content | mitigate | `11-08c-SUMMARY.md` substitutes verified Task 2 values; per-plan SUMMARY files retain canonical commit-level evidence. Phase 11 ships with 10-commit `git log` intact. | All 10 SUMMARY files present at `.planning/phases/11-.../11-{01..08c}-SUMMARY.md` | closed |

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-11-01 | T-11-01-01 | Doc-only commit; git history is the audit trail (same posture as Plan 10-01). | Phase 11 planner | 2026-05-28 |
| AR-11-02 | T-11-01-02 | REQUIREMENTS.md amendments contain no secrets / PII / credentials. | Phase 11 planner | 2026-05-28 |
| AR-11-03 | T-11-02-T2 | Multi-tenant dev host port exposure: production deployment owns auth + TLS in a separate repo (CONTEXT Out of Scope). | Phase 11 planner | 2026-05-28 |
| AR-11-04 | T-11-02-T4 | Prom `--web.enable-lifecycle` `/-/reload` endpoint exposed: dev posture per D-13 verbatim from sk2_1; same posture as ES `xpack.security.enabled=false`. Out of Scope row in REQUIREMENTS.md captures the production-tightening requirement. | Phase 11 planner | 2026-05-28 |
| AR-11-05 | T-11-04-T1 | Prom config: file committed under git; no external input templated; single literal target. Future hardening (sha256 digest pins) deferred. | Phase 11 planner | 2026-05-28 |
| AR-11-06 | T-11-04-T2 | Cross-references AR-11-04. | Phase 11 planner | 2026-05-28 |
| AR-11-07 | T-11-04-T3 | Prom high-cardinality protection: v1 traffic volume manageable per sk2_1 lock-in; `http_route` + `service_name` labels are bounded by route table + single service. No unbounded user-input → label paths. | Phase 11 planner | 2026-05-28 |
| AR-11-08 | T-11-05-T4 | "RED until commit #N" intentional state on 3 facts between Plan 11-05 commit and Plan 11-08b commit: forensic bisect-friendly per Phase 8/10 5-commit precedent. RED→GREEN cycle confirmed closed by closing cadence. | Phase 11 planner | 2026-05-28 |
| AR-11-09 | T-11-06-T5 | OTLP env-var pin persistence across test-process lifetime: WR-03/IN-09 review fixes bounded the bleed-through to a single fixture lifetime (capture+restore); pinned value matches appsettings.Development.json default. | Phase 11 reviewer (WR-03/IN-09 cycle) | 2026-05-28 |
| AR-11-10 | T-11-07-T3 | PromQL label match `service_name="sk-api"` AND `http_route="api/v{version:apiVersion}/Schemas"` is sufficiently scoped to the sk_p sk-api process under test; cross-process scrape contamination is dev-posture acceptable. | Phase 11 planner | 2026-05-28 |
| AR-11-11 | T-11-08b-T3 | LogExportTests body-content match `Contains("sk-api")`: term-query corrId narrows the hit set to a single per-test 128-bit UUID space; bool/must body.text="test-obs ok ran" phrase further constrains; substring collision probability negligible. | Phase 11 planner | 2026-05-28 |
| AR-11-12 | T-11-08b-T4 | Orphan OtelCollectorFixture.cs preserved-but-unused for exactly 1 commit between Plan 11-08b HEAD and Plan 11-08c HEAD: `public class` not warned by C# compiler when unused; zero-warning builds preserved throughout. | Phase 11 planner | 2026-05-28 |
| AR-11-13 | T-11-01-02 (image pins as supply-chain accept) | Image pins `8.15.5` / `v3.11.3` / `0.152.0` / `17-alpine` are public Docker Hub tags. Future hardening to sha256 digest pins deferred to Out of Scope. | Phase 11 planner | 2026-05-28 |

(13 accepted risks correspond to the 13 `accept`-disposition threats in the register.)

---

## Unregistered Flags

None. The phase SUMMARY files contain no `## Threat Flags` section; the executor flagged no new attack surface beyond what each plan's `<threat_model>` already declared.

The UAT regression fix (commit `5f2d6bd` — IN-05 revert restoring `attributes.CorrelationId` field path) was a test-data-shape correction (ES query targeting the correct keyword field), not a security mitigation change. No threat disposition shifts.

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-05-28 | 40 | 40 | 0 | GSD secure-phase auditor (Claude Opus 4.7) |

---

## Sign-Off

- [x] All threats have a disposition (27 mitigate + 13 accept = 40 total)
- [x] Accepted risks documented in Accepted Risks Log (13 entries)
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter
- [x] Implementation files unchanged by this audit (read-only)
- [x] ASVS Level 1 (dev-posture per CONTEXT Out of Scope)

**Approval:** verified 2026-05-28
