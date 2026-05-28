# Phase 11: Migrate Prometheus and Elastic containers from compose stack sk2_1 to sk_p - Context

**Gathered:** 2026-05-28
**Status:** Ready for planning

<domain>
## Phase Boundary

Add Elasticsearch + Prometheus containers to the sk_p Docker Compose stack (migrating their shape from the sibling repo `C:/Users/UserL/source/repos/sk2_1` Phase 12), reconfigure the existing OTel collector to ship logs to Elastic and metrics to Prometheus only (drop file exporter, drop traces pipeline), and replace the Phase 5 file-based observability test classes with round-trip E2E tests that drive a real HTTP request and then poll each backend for the resulting telemetry.

**In scope:**
- New compose services: `elasticsearch` (single-node dev mode), `prometheus` (scrape config), plus mutations to existing `otel-collector` service.
- New collector config: drop `file` exporter + `filter/health_metrics` (re-evaluate against Prom exporter), add native `elasticsearch` exporter (`mapping.mode: none`) for logs and `prometheus` exporter (port 8889, `resource_to_telemetry_conversion: true`) for metrics.
- New `prometheus.yml` scrape config targeting `otel-collector:8889`.
- OTel SDK changes in `Program.cs`: strip `.WithTracing()` / traces pipeline.
- Migrate 6 of 7 Phase 5 observability fact classes (log + log-level-filter → ES; metrics → Prom). Delete the 2 TraceExportTests facts. `OtelCollectorFixture` + `OtelEndOfSuiteCleanup` are restructured/replaced because they assume the file exporter.
- New automated E2E smoke fact(s) verifying round-trip: HTTP request → log in ES with matching `correlation.id`; HTTP request → metric data point in Prom.
- Image upgrades: `otel-collector-contrib 0.95.0 → 0.152.0`, plus new pins `elasticsearch 8.15.5` + `prom/prometheus v3.11.3`.
- REQUIREMENTS.md amendments for traces removal + new logs-ES / metrics-Prom REQ-IDs (planner's call on exact REQ-ID shape).

**Out of scope:**
- Production-grade ES/Prom posture (TLS, auth, persistence volumes, multi-node clusters) — Claude's Discretion to default to sk2_1's dev posture (no auth, no TLS, ephemeral storage) unless planner identifies a concrete need.
- A traces backend (Jaeger/Tempo/Zipkin/APM data streams) — explicitly dropped this phase.
- Kibana / Grafana / dashboards — backends are queryable via REST API only this phase.
- Logstash / Filebeat / Metricbeat — collector talks directly to ES + Prom.
- Production deployment artifacts (k8s manifests, helm charts) — this stack is local dev + CI only, matching sk2_1's stance.

</domain>

<decisions>
## Implementation Decisions

### Collector Pipeline Shape

- **D-01:** OTel collector exports logs **only** to Elasticsearch via the native `elasticsearch` exporter. No file fan-out. Single sink for logs.
- **D-02:** OTel collector exports metrics **only** to Prometheus via the `prometheus` exporter on port `8889`. No file fan-out. Single sink for metrics.
- **D-03:** Traces pipeline is **dropped entirely** from the collector config. Remove `.WithTracing()` and any tracing-related instrumentation registrations from `Program.cs` / `AddBaseApiObservability`. OBSERV-12 is superseded.
- **D-04:** `filter/health_metrics` processor (Phase 5 Plan 05-02 OBSERV-08 fix-forward) is **kept and re-pointed** at the Prometheus exporter pipeline. Health-route metric data points must still be dropped before reaching Prom.
- **D-05:** No file exporter anywhere in the new collector config. `tests/.otel-out/` directory and its bind-mount in `compose.yaml` are removed. `.gitignore` entry for `tests/.otel-out/` is removed. Phase 5 D-11 cleanup discipline (`OtelEndOfSuiteCleanup` AssemblyFixture) becomes obsolete.

### Backend Wiring

- **D-06:** Elasticsearch exporter config follows sk2_1 verbatim: `endpoints: [http://elasticsearch:9200]`, `mapping.mode: none` (preserves OTLP field structure → index `logs-generic-default`), no auth, no TLS.
- **D-07:** Prometheus exporter config follows sk2_1 verbatim: `endpoint: 0.0.0.0:8889`, `resource_to_telemetry_conversion: { enabled: true }` (`service.name` → `service_name` label is load-bearing for tests), `send_timestamps: true`.
- **D-08:** `prometheus.yml` (new file at repo root) follows sk2_1 verbatim: single `job_name: 'otel-collector'`, single static target `otel-collector:8889`, 15s scrape interval, 10s timeout, no relabel configs.

### Image Pins

- **D-09:** `otel/opentelemetry-collector-contrib:0.152.0` (upgrade from current 0.95.0 — 0.95.0 predates `mapping.mode` on the ES exporter).
- **D-10:** `docker.elastic.co/elasticsearch/elasticsearch:8.15.5` (matches sk2_1 verbatim).
- **D-11:** `prom/prometheus:v3.11.3` (matches sk2_1 verbatim).

### ES + Prom Service Shape (dev posture per sk2_1)

- **D-12:** Elasticsearch service env: `discovery.type=single-node`, `xpack.security.enabled=false`, `xpack.security.enrollment.enabled=false`, `ES_JAVA_OPTS=-Xms512m -Xmx512m`. Ports: `9200:9200` only (9300 not exposed). Healthcheck: `curl -fs 'http://localhost:9200/_cluster/health?wait_for_status=yellow&timeout=5s' || exit 1` with `start_period: 60s`. No volume (ephemeral).
- **D-13:** Prometheus service: command `--config.file=/etc/prometheus/prometheus.yml --web.enable-lifecycle`. Bind-mount `./prometheus.yml:/etc/prometheus/prometheus.yml:ro`. Ports: `9090:9090`. Healthcheck: `wget --spider http://localhost:9090/-/healthy`. Depends on `otel-collector: service_started`. No volume (ephemeral).
- **D-14:** OTel collector service mutations: image bump to 0.152.0 + add port `8889:8889` to ports list (for Prom scrape endpoint exposed to host). Drop the `./tests/.otel-out:/var/otel-out` bind-mount and the `user: "0:0"` override (no longer needed without file exporter; revert to image default UID).
- **D-15:** `baseapi-service` `depends_on` gains `elasticsearch: condition: service_healthy` and `prometheus: condition: service_healthy` alongside existing `postgres: service_healthy` + `otel-collector: service_started`. Compose-up therefore blocks for ~60s on ES cold-start; acceptable for dev + CI (sk2_1 precedent).

### Test Migration

- **D-16:** Existing 7 Phase 5 fact classes in `tests/BaseApi.Tests/Observability/`:
  - **Migrate** `LogExportTests.cs` + `LogLevelFilterTests.cs` to poll Elasticsearch (`GET http://localhost:9200/logs-generic-default/_search`) instead of reading `telemetry.jsonl`.
  - **Migrate** `MetricsExportTests.cs` to poll Prometheus (`GET http://localhost:9090/api/v1/query?query=...`).
  - **Delete** `TraceExportTests.cs` (no backend; traces pipeline dropped per D-03).
  - **Restructure or delete** `OtelCollectorFixture.cs` — current shape assumes file exporter / position-marker pattern. Replace with `ElasticsearchTestClient` + `PrometheusTestClient` helpers (or fold into per-test fixtures); planner's call on the exact shape.
  - **Delete** `OtelEndOfSuiteCleanup.cs` AssemblyFixture — Phase 5 D-11 telemetry.jsonl cleanup is obsolete.
  - **Keep** `HealthEndpointsTests.cs` (independent of file exporter; verifies `/health/*` endpoints directly).
  - **Keep** `TestObservabilityController.cs` (drives telemetry traffic; reusable).
- **D-17:** New automated E2E smoke fact(s) verify round-trip: test issues a real HTTP request against a sk_p business endpoint (planner picks which entity — likely `POST /api/v1/schemas` or similar), then:
  - Polls Elasticsearch for a structured log document with matching `Attributes.correlation.id` (or equivalent OTLP-field name under `mapping.mode: none`).
  - Polls Prometheus for an `http_server_request_duration_seconds_*` data point tagged with the request's route + status code + `service_name="sk-api"`.
- **D-18:** HTTP client choice (curl vs `HttpClient` vs `WebApplicationFactory`) and polling shape (timeout / interval / retry budget) for the new E2E tests are **Claude's Discretion**. Planner should pick a pragmatic shape that handles ES ingestion lag (~1-3s) + Prom scrape interval (15s); a single test invocation should still complete in well under a minute.

### REQUIREMENTS.md Amendments

- **D-19:** Phase 11 owns these REQUIREMENTS.md edits (planner finalizes wording):
  - OBSERV-12 (traces) → moved to Out of Scope with reason "deferred — no traces backend in v1; pipeline removed Phase 11" (mirrors sk2_1 CLAUDE.md non-negotiable #2).
  - OBSERV-03 (HTTP server + client metrics via OTel SDK) → unchanged; pipeline now lands in Prom not file.
  - OBSERV-04 (OTLP exporter targets external collector) → unchanged.
  - OBSERV-08 (health endpoints excluded from metrics via filter/health_metrics processor) → unchanged in intent; processor now feeds Prom exporter instead of file.
  - INFRA-06 (compose declares postgres + service depends_on healthy) → extended: ES + Prom now in compose, both with healthchecks, both in baseapi-service depends_on chain (per D-15).
  - **New REQ-IDs** (planner's call on exact IDs): logs land in ES `logs-generic-default` index with OTLP field shape; metrics scraped from `otel-collector:8889` by Prom job; E2E round-trip test class(es) verifying both backends.
  - Footer dated 2026-05-28 with Phase 11 amendment marker; doc-first commit precedent from Phase 10 Plan 10-01 (D-01/D-02) honored if the planner decides to split the doc edit into its own atomic commit.

### Claude's Discretion

- Exact REQ-ID naming/numbering for new requirements added by this phase.
- Which existing sk_p entity is used as the traffic source for the round-trip E2E test(s).
- HTTP client choice and polling shape for E2E tests.
- Whether `OtelCollectorFixture` is replaced wholesale or evolved.
- Whether E2E tests carry a `[Trait("Category","E2E")]` tag to allow filtering them out of fast runs.
- Whether the doc-first REQUIREMENTS.md amendment is split into its own commit (Phase 10 D-01/D-02 precedent) or bundled with the wiring change.
- Compose service ordering inside `compose.yaml` (alphabetical vs depends-on order) — current file has postgres → otel-collector → baseapi-service; new services slot in logically.
- Whether `tests/.otel-out/` directory is preserved as a tracked-empty `.gitkeep` (for forensic continuity) or fully removed alongside its `.gitignore` entry.
- Whether `compose/otel-collector-config.yaml` is edited in place or replaced wholesale.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Project state + locked decisions
- `.planning/PROJECT.md` — Steps API project doc; locked tech stack, constraints, Key Decisions table.
- `.planning/REQUIREMENTS.md` — current REQ-IDs; OBSERV/HEALTH/INFRA categories will be amended this phase.
- `.planning/STATE.md` — current project state; Phase 10 completion summary; Phase 11 added 2026-05-28.
- `.planning/ROADMAP.md` — Phase 11 entry (currently "Goal: [To be planned]"; planner finalizes Goal + Success Criteria + Requirements list).

### Current sk_p observability surface (must read before mutating)
- `compose.yaml` — current postgres + otel-collector + baseapi-service shape; gets ES + Prom services added + otel-collector service mutated.
- `compose/otel-collector-config.yaml` — current collector pipelines (logs+metrics+traces → file + logging); gets re-shaped per D-01..D-05.
- `src/BaseApi.Service/Program.cs` (or Phase 7's `AddBaseApiObservability` extension in `src/BaseApi.Core/Extensions/`) — current `.WithTracing()` registration; gets stripped per D-03.
- `tests/BaseApi.Tests/Observability/` — 7 fact classes + fixtures; gets migrated/deleted per D-16.
- `.gitignore` — current `tests/.otel-out/` entry; gets removed per D-05.
- `.planning/phases/05-observability-and-health-probes/` — Phase 5 CONTEXT + SUMMARY for D-11 cleanup discipline context and `filter/health_metrics` processor history (Plan 05-02 fix-forward).

### sk2_1 migration source (read to mirror posture verbatim)
- `C:/Users/UserL/source/repos/sk2_1/docker-compose.yml` — verbatim source for new elasticsearch + prometheus service definitions (D-12 / D-13).
- `C:/Users/UserL/source/repos/sk2_1/otel-collector-config.yaml` — verbatim source for new elasticsearch + prometheus exporter blocks (D-06 / D-07).
- `C:/Users/UserL/source/repos/sk2_1/prometheus.yml` — verbatim source for new `prometheus.yml` at sk_p repo root (D-08).
- sk2_1's `SchemasLogsE2ETests` / `SchemasMetricsE2ETests` test classes (if locatable under sk2_1's tests folder) — pattern source for D-17 round-trip E2E tests; planner can grep sk2_1 for `Trait("Category", "E2E")` to find them.

### sk_p prior-phase canonical refs (carry forward)
- Phase 5 Plan 05-02 SUMMARY (in `.planning/phases/05-observability-and-health-probes/`) — `filter/health_metrics` processor rationale, telemetry.jsonl cleanup pattern history.
- Phase 4 PostgresExceptionMapper Option A regex contract — unaffected by this phase but worth noting that error-mapping path stays intact.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`TestObservabilityController.cs`** — Phase 5 helper controller that emits log/metric/trace traffic on demand. Reusable as the traffic source for new E2E tests (or planner may pick a real business endpoint instead per D-17).
- **`HealthEndpointsTests.cs`** — independent of file exporter, verifies `/health/live/ready/startup` directly; remains green through this phase.
- **`PostgresFixture` + per-class throwaway DB pattern** — proven across Phases 3-10; new E2E tests inherit this if they need DB writes during the round-trip.
- **`Phase8WebAppFactory`** — current `IClassFixture` base for integration tests; reusable for E2E round-trips (or planner adds a `Phase11WebAppFactory` if backend bring-up needs to be fixture-scoped).
- **xUnit v3 `[assembly: AssemblyFixture]` pattern** — used by `OtelEndOfSuiteCleanup`; the pattern itself is reusable for any new whole-suite setup/teardown (e.g., ES index cleanup, Prom server reload).

### Established Patterns
- **Compose v2 + named-volume + healthcheck-driven depends_on** — pattern from Phase 2 carries forward; ES + Prom slot into the same structure.
- **Verbatim image pins** — Phase 1 D-05 + Phase 2 D-12 + Phase 5 D-10 all use exact-version pins; new ES + Prom + collector-0.152.0 pins follow the same discipline.
- **Distroless collector image + no in-container healthcheck** — Plan 05-01 deviation #1 established that the contrib image has no shell/wget. The 0.152.0 image is still distroless; the same "no compose-level healthcheck on otel-collector" stance applies. Host-side `curl http://localhost:13133/` remains the smoke path.
- **`[Trait("Phase","N")]` test tagging** — used in Phase 9/10; new E2E tests should be `[Trait("Phase","11")]` (and optionally `[Trait("Category","E2E")]` — Claude's Discretion).
- **Phase 3 D-15 byte-identical `psql \l` cleanup proof** — unaffected by this phase but worth preserving: don't introduce ES/Prom-side test leakage that would invalidate the equivalent stance for those backends. Planner should consider whether per-test ES index cleanup or unique-correlation-id-per-test is the right isolation pattern.

### Integration Points
- **`Program.cs` OTel registration site** — `.WithLogging()` (MEL bridge), `.WithMetrics()`, `.WithTracing()` calls live here (or in `AddBaseApiObservability` in BaseApi.Core/Extensions). Phase 11 strips traces; the metrics + logs registrations stay byte-identical (the only change is downstream collector wiring).
- **`OTEL_EXPORTER_OTLP_ENDPOINT` env var** — already wired in `compose.yaml` baseapi-service block. Unchanged this phase (still `http://otel-collector:4317`).
- **`compose.yaml` `volumes:` block** — only `pgdata` currently declared. Phase 11 adds no new named volumes (ES + Prom ephemeral per D-12/D-13).
- **`appsettings.json` `Service.Name` + `Service.Version`** — already `sk-api` / `3.2.0` per Phase 1. The Prom exporter's `resource_to_telemetry_conversion: true` (D-07) means these become `service_name="sk-api"` + `service_version="3.2.0"` Prometheus labels — load-bearing for D-17 metrics assertions.

</code_context>

<specifics>
## Specific Ideas

- The sk2_1 docker-compose.yml + otel-collector-config.yaml + prometheus.yml represent a working reference implementation. The planner SHOULD mirror them as closely as feasible, deviating only where sk_p's existing structure (compose.yaml at repo root + `compose/` subfolder for collector config) differs from sk2_1's flat layout.
- The OTel→Prometheus naming convention (per sk2_1 collector config comment) is load-bearing for test assertions: dot-form (`db.client.operations`) becomes underscore-form `_total` (`db_client_operations_total`); histograms become `_count`/`_sum`/`_bucket` triplets with `_seconds` suffix when `unit="s"` (`http_server_request_duration_seconds_*`). New E2E tests must use the Prometheus form of metric names.
- Elasticsearch `mapping.mode: none` produces the index `logs-generic-default` (NOT `logs-generic.otel-default` which `mapping.mode: otel` would produce). Test assertions key off the former.
- Sole purpose of the otel-collector port `4318` (HTTP) is `curl`-from-laptop debugging — port `4317` (gRPC) is the production-traffic port for the SDK. Unchanged this phase but documented for completeness.

</specifics>

<deferred>
## Deferred Ideas

- **Production-grade ES posture** — TLS, basic auth or token auth (`xpack.security.enabled=true` + credentials), persistence volume, multi-node cluster, JVM heap tuning. Deferred: sk_p is dev/CI only this phase; production deployment lives in a downstream service repo per the sk2_1 CLAUDE.md non-negotiable #3 precedent.
- **Production-grade Prom posture** — persistence volume, `remote_write` to long-term storage (Thanos/Mimir/Cortex), alerting rules, recording rules, federation. Deferred for the same reason.
- **Kibana + Grafana dashboards** — query the backends from `curl` or test code this phase; UI lives in a separate phase / repo.
- **Traces backend** — Jaeger / Tempo / Zipkin / Elastic APM data streams. Mirrors sk2_1's stance (no traces in v1). Could be revived as a future milestone if request-flow debugging becomes painful.
- **APM agent / auto-instrumentation** — manual OTel SDK wiring is the locked path (Phase 5). APM agent injection is a future possibility but not in scope.
- **Log shipping fallback** — if collector → ES network hiccups, OTLP logs are dropped after the in-memory queue fills. A persistent disk buffer / file-as-WAL pattern is deferred; sk2_1 didn't ship one either.
- **Prometheus high-cardinality protection** — recording rules / metric_relabel_configs to drop noisy labels. Deferred; sk2_1's `prometheus.yml` ships no relabels and the assumption is that v1 traffic volume is manageable.
- **Inbox unifier (telemetry to a single backend)** — if downstream consumers want logs+metrics in one place, ES via the OTel ES exporter for metrics is technically possible (ES 8.x supports OTel metrics under APM data streams). Not in scope this phase per the user's "logs to ES, metrics to Prom, nothing else" lock-in.

</deferred>

---

*Phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack*
*Context gathered: 2026-05-28*
