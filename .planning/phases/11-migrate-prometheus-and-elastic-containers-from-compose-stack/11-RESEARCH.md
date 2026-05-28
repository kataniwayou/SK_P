# Phase 11: Migrate Prometheus and Elastic containers from compose stack sk2_1 to sk_p - Research

**Researched:** 2026-05-28
**Domain:** Docker Compose multi-service observability migration (Elasticsearch 8.15.5 + Prometheus v3.11.3 + OTel Collector contrib 0.95.0 → 0.152.0) + xUnit v3 round-trip E2E testing against real backends
**Confidence:** HIGH on stack pins, schema shapes, and pitfalls; MEDIUM on ES index naming reconciliation (one canonical-reference inconsistency identified — see Open Q1); HIGH on existing sk_p observability surface.

## Summary

Phase 11 swaps sk_p's Phase 5 file-exporter-based observability stack for a real backend pair (Elasticsearch + Prometheus), mirroring the verbatim posture used in the sibling sk2_1 repo. The OTel collector image jumps from 0.95.0 to 0.152.0 (15-month leap; the elasticsearch + prometheus exporters in the contrib distribution have evolved significantly — most notably the elasticsearch exporter's default `mapping.mode` flipped from older defaults to `otel` in v0.122.0, and the prometheus exporter gained a `translation_strategy` knob). The traces pipeline is dropped entirely (Program.cs `.WithTracing()` stripped; collector config + 2 trace-export facts deleted). The file exporter + position-marker fixture pattern from Phase 5 D-11 becomes obsolete; replaced by `ElasticsearchTestClient` + `PrometheusTestClient` polling helpers and a round-trip E2E fact set that drives a real HTTP request and asserts both backends see the resulting telemetry.

**Primary recommendation:** Mirror sk2_1's compose + collector + prometheus.yml posture **verbatim** as CONTEXT D-06..D-13 lock in. BUT before writing assertion code: explicitly choose **one** of the two valid ES index names (`logs-generic-default` for `mapping.mode: none` per the spec, OR `logs-generic.otel-default` for `mapping.mode: otel`) and propagate that choice consistently into both the collector config AND the test assertion. The sk2_1 reference repo's YAML file says one thing (`mode: none`) but its live ES + test file consume the other (`logs-generic.otel-default`) — see Open Q1. The planner MUST resolve this before any test code lands. Recommended default: honor CONTEXT D-06 (`mapping.mode: none` → `logs-generic-default`) and adapt the test query string accordingly; this is the cleaner, less-magical path.

## User Constraints (from CONTEXT.md)

### Locked Decisions

> The following are copied verbatim from `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-CONTEXT.md`. The planner MUST honor every D-01..D-19 statement; research treats them as canonical inputs.

**Collector Pipeline Shape**
- **D-01:** OTel collector exports logs **only** to Elasticsearch via the native `elasticsearch` exporter. No file fan-out. Single sink for logs.
- **D-02:** OTel collector exports metrics **only** to Prometheus via the `prometheus` exporter on port `8889`. No file fan-out. Single sink for metrics.
- **D-03:** Traces pipeline is **dropped entirely** from the collector config. Remove `.WithTracing()` and any tracing-related instrumentation registrations from `Program.cs` / `AddBaseApiObservability`. OBSERV-12 is superseded.
- **D-04:** `filter/health_metrics` processor (Phase 5 Plan 05-02 OBSERV-08 fix-forward) is **kept and re-pointed** at the Prometheus exporter pipeline. Health-route metric data points must still be dropped before reaching Prom.
- **D-05:** No file exporter anywhere in the new collector config. `tests/.otel-out/` directory and its bind-mount in `compose.yaml` are removed. `.gitignore` entry for `tests/.otel-out/` is removed. Phase 5 D-11 cleanup discipline (`OtelEndOfSuiteCleanup` AssemblyFixture) becomes obsolete.

**Backend Wiring**
- **D-06:** Elasticsearch exporter config follows sk2_1 verbatim: `endpoints: [http://elasticsearch:9200]`, `mapping.mode: none` (preserves OTLP field structure → index `logs-generic-default`), no auth, no TLS.
- **D-07:** Prometheus exporter config follows sk2_1 verbatim: `endpoint: 0.0.0.0:8889`, `resource_to_telemetry_conversion: { enabled: true }` (`service.name` → `service_name` label is load-bearing for tests), `send_timestamps: true`.
- **D-08:** `prometheus.yml` (new file at repo root) follows sk2_1 verbatim: single `job_name: 'otel-collector'`, single static target `otel-collector:8889`, 15s scrape interval, 10s timeout, no relabel configs.

**Image Pins**
- **D-09:** `otel/opentelemetry-collector-contrib:0.152.0` (upgrade from current 0.95.0).
- **D-10:** `docker.elastic.co/elasticsearch/elasticsearch:8.15.5`.
- **D-11:** `prom/prometheus:v3.11.3`.

**ES + Prom Service Shape (dev posture per sk2_1)**
- **D-12:** Elasticsearch — `discovery.type=single-node`, `xpack.security.enabled=false`, `xpack.security.enrollment.enabled=false`, `ES_JAVA_OPTS=-Xms512m -Xmx512m`. Ports: `9200:9200`. Healthcheck: `curl -fs 'http://localhost:9200/_cluster/health?wait_for_status=yellow&timeout=5s' || exit 1` with `start_period: 60s`. No volume.
- **D-13:** Prometheus — command `--config.file=/etc/prometheus/prometheus.yml --web.enable-lifecycle`. Bind-mount `./prometheus.yml:/etc/prometheus/prometheus.yml:ro`. Ports: `9090:9090`. Healthcheck: `wget --spider http://localhost:9090/-/healthy`. Depends on `otel-collector: service_started`. No volume.
- **D-14:** OTel collector — image 0.152.0 + add port `8889:8889`. Drop `./tests/.otel-out:/var/otel-out` bind-mount + `user: "0:0"` override.
- **D-15:** `baseapi-service` `depends_on` gains `elasticsearch: condition: service_healthy` + `prometheus: condition: service_healthy` alongside existing `postgres: service_healthy` + `otel-collector: service_started`. ~60s cold-start acceptable for dev + CI.

**Test Migration**
- **D-16:** Migrate `LogExportTests.cs` + `LogLevelFilterTests.cs` to poll ES. Migrate `MetricsExportTests.cs` to poll Prom. Delete `TraceExportTests.cs`. Restructure or delete `OtelCollectorFixture.cs`. Delete `OtelEndOfSuiteCleanup.cs`. Keep `HealthEndpointsTests.cs`. Keep `TestObservabilityController.cs`.
- **D-17:** New automated E2E smoke fact(s) verify round-trip: HTTP → ES log with `correlation.id`; HTTP → Prom metric data point with route + status + `service_name="sk-api"`.

**REQUIREMENTS.md Amendments**
- **D-19:** Phase 11 owns: OBSERV-12 → Out of Scope; OBSERV-03/04/08 unchanged in intent; INFRA-06 extended; new REQ-IDs for ES log landing + Prom metric scrape + E2E round-trip (planner's call on IDs). Footer dated 2026-05-28; doc-first commit precedent honored if planner chooses to split.

### Claude's Discretion

- Exact REQ-ID naming/numbering for new requirements added by this phase.
- Which existing sk_p entity is used as the traffic source for the round-trip E2E test(s).
- HTTP client choice and polling shape for E2E tests (D-18).
- Whether `OtelCollectorFixture` is replaced wholesale or evolved.
- Whether E2E tests carry `[Trait("Category","E2E")]`.
- Whether the doc-first REQUIREMENTS.md amendment is split into its own commit.
- Compose service ordering inside `compose.yaml` (alphabetical vs depends-on order).
- Whether `tests/.otel-out/` is preserved as `.gitkeep`-only or fully removed.
- Whether `compose/otel-collector-config.yaml` is edited in place or replaced wholesale.

### Deferred Ideas (OUT OF SCOPE)

- Production-grade ES posture (TLS, auth, persistence volume, multi-node).
- Production-grade Prom posture (persistence volume, remote_write, alerting/recording rules, federation).
- Kibana + Grafana dashboards.
- Traces backend (Jaeger / Tempo / Zipkin / Elastic APM).
- APM agent / auto-instrumentation.
- Log shipping fallback (disk buffer / file-as-WAL).
- Prom high-cardinality protection (recording rules / metric_relabel_configs).
- Inbox unifier (logs+metrics in one backend).

## Phase Requirements

Phase 11 introduces no REQ-IDs at research time — D-19 grants the planner the authority to finalize REQ-ID shape during planning. The table below maps the **proposed semantic anchors** that any final REQ-IDs should cover; the planner will name them.

| Proposed Anchor | Description | Research Support |
|-----------------|-------------|------------------|
| INFRA-08 (or similar) | compose.yaml declares elasticsearch + prometheus services with healthchecks; baseapi-service depends_on extends to both with `service_healthy` | sk2_1 docker-compose.yml + Compose v5.1.1 multi-service-healthy verification (Environment Availability + Common Pitfalls below) |
| OBSERV-13 (or similar) | OTel logs land in ES at index `logs-generic-default` (or `logs-generic.otel-default` — see Open Q1) with `Attributes.correlation.id` preserved | sk2_1 otel-collector-config.yaml + ES exporter v0.152.0 README + `mapping.mode` spec |
| OBSERV-14 (or similar) | OTel metrics are scraped by Prometheus from `otel-collector:8889` with `service_name="sk-api"` label | sk2_1 prometheus.yml + Prom exporter `resource_to_telemetry_conversion: true` spec |
| OBSERV-12 (amend) | Traces pipeline removed; OBSERV-12 moved to Out of Scope with rationale | CONTEXT D-03 |
| TEST-07 (or similar) | E2E round-trip fact(s) verify HTTP → ES log + HTTP → Prom metric within bounded poll budget | D-17 + D-18 + sk2_1 SchemasLogsE2ETests/SchemasMetricsE2ETests pattern |

## Project Constraints (from CLAUDE.md)

No `./CLAUDE.md` exists in the sk_p repo root. Project conventions are derived instead from `.planning/PROJECT.md` Key Decisions table + accumulated decision log in `.planning/STATE.md`. The Phase 11-relevant directives from those sources:

- **Tech stack pins:** .NET 8 (INFRA-02), Postgres 17-alpine (Phase 2 D-12), OTel SDK 1.15.x (OBSERV-01).
- **Image pinning discipline:** Phase 1 D-05 + Phase 2 D-12 + Phase 5 D-10 — every container image carries an exact-version tag, never `latest`. New ES + Prom pins follow the same posture.
- **Compose v2 + healthcheck-driven `depends_on`:** established Phase 2; carry forward.
- **Distroless collector image stance:** Plan 05-01 D-deviation #1 — the contrib image has no shell/wget/curl; no in-container healthcheck declarable. Phase 11 inherits this for the 0.152.0 image (still distroless per OpenTelemetry release process). Host-side `curl http://localhost:13133/` is the smoke probe.
- **Phase 3 D-15 byte-identical `psql \l` cleanup proof:** unaffected by this phase but Phase 11 must not introduce ES/Prom-side test leakage that would invalidate the analogous stance for those backends (see Pitfall 5 below).
- **Doc-first REQUIREMENTS.md amendment precedent:** Phase 10 Plan 10-01 — splitting REQUIREMENTS.md edits into a dedicated commit before any code touches keeps the spec/code split bisect-friendly.
- **No emoji in code or commit messages** (general project hygiene from Phase 1+).

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Emit OTLP telemetry (logs + metrics) | API / Backend (BaseApi.Service) | — | OTel SDK lives in-process; `Program.cs` `AddBaseApiObservability(cfg)` registers MEL bridge + meters |
| OTLP transport (gRPC) | API → Collector network | — | `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317` env var; intra-compose-network only in dev |
| Telemetry shaping (filter, route) | Collector (otel-collector service) | — | `filter/health_metrics` processor drops `/health/*` metric data points before egress to Prom (D-04) |
| Logs persistence | Database / Storage (elasticsearch service) | — | Ephemeral single-node dev container; `_search` API queried by tests |
| Metrics persistence | Database / Storage (prometheus service) | — | Ephemeral container; scrapes collector `:8889`; `/api/v1/query` queried by tests |
| Health probes | API / Backend (BaseApi.Service `/health/*`) | — | Unchanged from Phase 5; still excluded from metrics via D-04 filter |
| Test orchestration | Build/CI + dev workstation | — | `dotnet test` drives `WebApplicationFactory<Program>`; collector + ES + Prom must be up before the test process starts (compose-up gate) |

## Standard Stack

### Core

| Library / Image | Version | Purpose | Why Standard |
|-----------------|---------|---------|--------------|
| `otel/opentelemetry-collector-contrib` | `0.152.0` `[VERIFIED: docker hub tag list 2026-05-28 — pulled locally]` | Telemetry ingest + routing; native `elasticsearch` + `prometheus` exporters live in the contrib distro | sk2_1 lock-in; smallest-image contrib distro that ships both target exporters; required minimum is v0.122.0+ for current `mapping.mode` semantics |
| `docker.elastic.co/elasticsearch/elasticsearch` | `8.15.5` `[VERIFIED: docker hub + image pulled locally]` | Logs backend; queried via REST `_search` API | sk2_1 lock-in; 8.15.x is the last 8.x line with stable single-node dev posture; 8.x is required by the collector's elasticsearch exporter (7.x rejected) |
| `prom/prometheus` | `v3.11.3` `[VERIFIED: docker hub + image pulled locally]` | Metrics backend; pulls `/metrics` from collector; queried via `/api/v1/query` | sk2_1 lock-in; v3.x line; v2.x deprecated by Prometheus team |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `xUnit v3` | already pinned in `BaseApi.Tests.csproj` `[VERIFIED: codebase]` | Test framework; `IClassFixture` + `IAsyncLifetime` + `[Trait]` | All test classes |
| `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory<Program>`) | already pinned `[VERIFIED: codebase]` | In-process Kestrel for round-trip E2E | E2E facts drive a real HTTP request through the SDK |
| `System.Net.Http.Json` | BCL `[VERIFIED: codebase]` | `PostAsJsonAsync` / `ReadFromJsonAsync` for sk_p HTTP body marshalling + ES query body | All E2E HTTP I/O |
| `System.Text.Json.JsonDocument` | BCL | Parse ES + Prom JSON responses | Polling helpers |
| `System.Diagnostics.Stopwatch` | BCL | Wall-clock budget for poll loops | E2E budget enforcement (D-18) |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Native `elasticsearch` collector exporter | Logstash / Filebeat sidecar | Adds a 3rd container + a config; sk2_1 explicitly rejected; deferred per Phase 11 Out of Scope |
| `prometheus` (pull) exporter | `prometheusremotewrite` (push) exporter | Push removes the need for a Prom scrape config but adds Prom-side `remote_write` config; sk2_1 picked pull; dev posture; deferred |
| `mapping.mode: otel` (new default since v0.122.0) | (D-06 locks `none`) | OTel-mode index = `logs-generic.otel-default` with field shape `body.text`, `severity_text`, `scope.name`, `resource.attributes."service.name"`. CONTEXT D-06 chose `none` which preserves the raw OTLP field structure under index `logs-generic-default`. Open Q1 surfaces a consistency issue between sk2_1's YAML and its actual test query string. |
| In-process `WebApplicationFactory<Program>` E2E driver | `curl` / `HttpClient` against `localhost:8080` (the compose-built sk_p container) | In-process keeps the test self-contained and avoids needing the sk_p image rebuilt before tests; matches Phase 5/7/8 fixture pattern; sk2_1 also uses in-process. **Recommended**. |

**Installation:** No NuGet changes required this phase — all C# dependencies are already pinned. Image pulls:

```bash
docker pull otel/opentelemetry-collector-contrib:0.152.0
docker pull docker.elastic.co/elasticsearch/elasticsearch:8.15.5
docker pull prom/prometheus:v3.11.3
```

**Verification:** All three images are already present locally `[VERIFIED: docker image ls 2026-05-28]`.

## Architecture Patterns

### System Architecture Diagram

```
                         [dotnet test process]
                                  │
                                  │ (WebApplicationFactory<Program> spawns in-process Kestrel)
                                  ▼
                   ┌──────────────────────────────────────┐
                   │  BaseApi.Service (in-process Kestrel) │
                   │  ─ ILogger<T>.LogInformation          │
                   │  ─ AspNetCoreInstrumentation (metrics)│
                   │  ─ HttpClientInstrumentation          │
                   │  ─ RuntimeInstrumentation             │
                   │  ─ (NO traces — D-03)                 │
                   └──────────────────────────────────────┘
                                  │
                                  │ OTLP gRPC → localhost:4317
                                  ▼
                   ┌──────────────────────────────────────┐
                   │  otel-collector :4317 / :4318 / :8889 │
                   │  ┌──────────────────────────────────┐ │
                   │  │ pipelines.logs                   │ │
                   │  │   receivers: [otlp]              │ │
                   │  │   exporters: [elasticsearch]     │ │
                   │  ├──────────────────────────────────┤ │
                   │  │ pipelines.metrics                │ │
                   │  │   receivers: [otlp]              │ │
                   │  │   processors:                    │ │
                   │  │     [filter/health_metrics] ◄── D-04│
                   │  │   exporters: [prometheus]        │ │
                   │  └──────────────────────────────────┘ │
                   └──────────────────────────────────────┘
                            │                      │
                            │ HTTP POST            │ HTTP GET /metrics (Prom scrape; 15s interval)
                            ▼                      ▼
                ┌──────────────────────┐  ┌──────────────────────┐
                │ elasticsearch :9200  │  │ prometheus :9090     │
                │ (single-node dev)    │  │ (no persistence)     │
                │ index:               │  │ scrape job:          │
                │   logs-generic-default│  │   otel-collector     │
                │   (D-06 / Open Q1)   │  │   target :8889       │
                └──────────────────────┘  └──────────────────────┘
                            ▲                      ▲
                            │ GET /_search         │ GET /api/v1/query?...
                            │ (test poll loop)     │ (test poll loop)
                            └──────────────────────┘
                                       │
                                  [test assertions]
```

### Component Responsibilities

| Component | sk_p path | Phase 11 mutation |
|-----------|-----------|-------------------|
| `compose.yaml` | repo root | Add `elasticsearch` + `prometheus` services (D-12 / D-13); mutate `otel-collector` service (D-14: image bump, port 8889, drop file bind-mount); extend `baseapi-service.depends_on` (D-15) |
| `prometheus.yml` | repo root (NEW) | Verbatim sk2_1 content (D-08) |
| `compose/otel-collector-config.yaml` | (rename in place per Discretion) | Drop `file` exporter + traces pipeline; add `elasticsearch` exporter (D-06) + `prometheus` exporter (D-07); keep `filter/health_metrics` re-pointed to Prom pipeline (D-04); drop `health_check` extension OR keep it if planner wants the host-side smoke probe preserved |
| `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` | unchanged path | Strip `.WithTracing(...)` block (D-03); keep MEL-bridge logs + metrics blocks byte-identical |
| `src/BaseApi.Service/Program.cs` | unchanged path | No edit needed (OBSERV registration centralizes in the extension method) |
| `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` | restructure or delete | Per D-16 — file-exporter assumptions invalid; planner picks evolve vs replace |
| `tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs` | DELETE | D-05 / D-16 — telemetry.jsonl cleanup obsolete |
| `tests/BaseApi.Tests/Observability/LogExportTests.cs` | rewrite | Poll ES `/_search` instead of reading telemetry.jsonl |
| `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` | rewrite | Poll ES `/_search`; assert filtered Information records absent |
| `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` | rewrite | Poll Prom `/api/v1/query` |
| `tests/BaseApi.Tests/Observability/TraceExportTests.cs` | DELETE | D-16 |
| `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` | KEEP | independent of file exporter |
| `tests/BaseApi.Tests/Observability/TestObservabilityController.cs` | KEEP | reusable as E2E traffic source |
| `tests/BaseApi.Tests/Observability/CollectionDefinitions.cs` | KEEP or rename | `[Collection("Observability")]` serialization remains useful with backend tests too |
| `tests/BaseApi.Tests/Observability/ElasticsearchTestClient.cs` | NEW | poll helper; encapsulates `_search` request + retry/backoff |
| `tests/BaseApi.Tests/Observability/PrometheusTestClient.cs` | NEW | poll helper; encapsulates `/api/v1/query` request + initial-sleep-then-poll pattern |
| `tests/BaseApi.Tests/E2E/RoundTripE2ETests.cs` (or similar) | NEW | D-17 round-trip fact(s); `[Trait("Phase","11")]` + optional `[Trait("Category","E2E")]` |
| `.gitignore` | edit | Remove `tests/.otel-out/*` + `!tests/.otel-out/.gitkeep` entries (D-05) |
| `tests/.otel-out/.gitkeep` | DELETE (or keep per Discretion) | If keeping the directory for forensic continuity, planner can choose |
| `appsettings.json` / `appsettings.Development.json` | likely unchanged | `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317` already wired; no metric-export-interval override needed unless planner picks E2E approach that requires it (see Pitfall 7) |

### Pattern 1: Compose-up gate + multi-service-healthy depends_on

**What:** Pre-`dotnet test` requires all backends to be healthy. Compose v2 handles this via `depends_on: { service_healthy }` chains for service-to-service ordering, but the host-side test process is OUTSIDE the compose graph and must wait independently.

**When to use:** Any E2E test that hits a backend via host port; not just Phase 11.

**Example (Compose v5.1.1 / Compose-spec):**
```yaml
# Source: sk2_1 docker-compose.yml + Compose-spec
services:
  baseapi-service:
    depends_on:
      postgres:    { condition: service_healthy }
      otel-collector: { condition: service_started }  # distroless — no healthcheck declarable
      elasticsearch:   { condition: service_healthy }
      prometheus:      { condition: service_healthy }
```

**Host-side gate (developer + CI; ad-hoc, not in compose):**
```bash
# Bring up the stack; --wait blocks until all services with healthchecks report healthy
docker compose up -d --wait
# When --wait succeeds: postgres + elasticsearch + prometheus are healthy; otel-collector is started.
# dotnet test can now proceed.
dotnet test SK_P.sln --no-restore -c Release
```

`[VERIFIED: docker compose v5.1.1 supports --wait; existing sk_p workflow already uses docker compose up -d for Postgres]`.

### Pattern 2: ElasticsearchTestClient — poll loop with HTTP 404 + empty-hits tolerance

**What:** ES indexing lag is 1–3 s under load; the data stream for `logs-generic-default` is created lazily on first write. The poll loop must tolerate (a) HTTP 404 (index does not exist yet) and (b) empty hits (`hits.hits.length == 0` — doc not indexed yet).

**When to use:** Every test that asserts on logs landing in ES.

**Example (verbatim adaptation of sk2_1 SchemasLogsE2ETests.PollEsForLog):**
```csharp
// Source: C:/Users/UserL/source/repos/sk2_1/tests/SK.WebApi.Schemas.Tests/SchemasLogsE2ETests.cs lines 153-200 + WR-01/WR-02/WR-04 refinements
private const int InitialDelayMs = 200;
private const int MaxDelayMs     = 3_200;

private async Task<JsonElement?> PollEsForLog(string query, int timeoutMs)
{
    var sw    = Stopwatch.StartNew();
    var delay = InitialDelayMs;
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Post, "logs-generic-default/_search")
            {
                Content = new StringContent(query, Encoding.UTF8, "application/json")
            };
            using var resp = await _es.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("hits", out var outer)
                    && outer.TryGetProperty("hits", out var hits)
                    && hits.ValueKind == JsonValueKind.Array
                    && hits.GetArrayLength() > 0)
                {
                    using var inner = JsonDocument.Parse(hits[0].GetRawText());
                    return inner.RootElement.Clone();
                }
            }
            // Else: 404 (index not yet created), empty hits, or malformed envelope — keep polling.
        }
        catch (HttpRequestException) { /* ES briefly unreachable — retry. */ }

        var remaining = (int)(timeoutMs - sw.ElapsedMilliseconds);
        if (remaining <= 0) break;
        await Task.Delay(Math.Min(delay, remaining));
        delay = Math.Min(delay * 2, MaxDelayMs);
    }
    return null;
}
```

**Recommended timeout:** `30_000` ms (sk2_1 default). Stays well under the per-fact-minute budget D-18 implies.

### Pattern 3: PrometheusTestClient — sleep-then-poll (NOT poll-from-zero)

**What:** Prometheus is pull-based with a 15 s scrape interval (D-08). The metric sample does NOT exist until the **next** scrape cycle completes. A naive "poll every 100 ms" loop returns empty until the first scrape and then has to wait one more scrape cycle for the cumulative value to include the test's HTTP requests. The correct pattern is: (1) sleep one full scrape cycle (15–20 s), then (2) poll every 2–3 s until the cumulative sample meets/exceeds the expected threshold OR the budget expires.

**When to use:** Every test that asserts on Prom metrics.

**Example (verbatim adaptation of sk2_1 SchemasMetricsE2ETests):**
```csharp
// Source: C:/Users/UserL/source/repos/sk2_1/tests/SK.WebApi.Schemas.Tests/SchemasMetricsE2ETests.cs lines 38-58, 168-234
private const int InitialSleepMs = 15_000;     // one Prom scrape_interval
private const int PollTimeoutMs  = 60_000;
private const int PollIntervalMs = 3_000;

private async Task<List<JsonElement>> PollPrometheusUntilSumAtLeast(string promql, double threshold)
{
    await Task.Delay(InitialSleepMs);
    var lastSamples = await QueryPrometheus(promql);
    var elapsed     = InitialSleepMs;
    while (elapsed < PollTimeoutMs)
    {
        if (lastSamples.Count > 0 && SumSampleValues(lastSamples) >= threshold)
            return lastSamples;
        await Task.Delay(PollIntervalMs);
        elapsed += PollIntervalMs;
        lastSamples = await QueryPrometheus(promql);
    }
    return lastSamples;
}

private async Task<List<JsonElement>> QueryPrometheus(string promql)
{
    var url   = $"api/v1/query?query={Uri.EscapeDataString(promql)}";  // EscapeDataString mandatory — PromQL contains { } " =
    using var resp = await _prom.GetAsync(url);
    resp.EnsureSuccessStatusCode();
    var json = await resp.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(json);
    if (!doc.RootElement.TryGetProperty("status", out var statusEl)
        || statusEl.GetString() != "success"
        || !doc.RootElement.TryGetProperty("data", out var dataEl)
        || !dataEl.TryGetProperty("result", out var results))
    {
        Assert.Fail($"Prometheus query failed. Query: {promql}. Response: {json}");
        return new List<JsonElement>();
    }
    var list = new List<JsonElement>();
    foreach (var r in results.EnumerateArray())
    {
        using var inner = JsonDocument.Parse(r.GetRawText());
        list.Add(inner.RootElement.Clone());
    }
    return list;
}
```

### Pattern 4: OTel SDK metric-export-interval override (60 s → 1 s) for E2E

**What:** The OTel .NET SDK's `PeriodicExportingMetricReader` defaults to a **60-second** export interval `[VERIFIED: opentelemetry-dotnet docs + sk2_1 RESEARCH.md Pitfall 2 + sk2_1 SchemasE2EWebApplicationFactory.cs lines 67-77]`. Under that default, a 30-second E2E fact will see ZERO exported metrics — the SDK has not exported yet. For E2E facts the SDK metric export interval must be shortened to ~1 s.

**When to use:** Round-trip metrics E2E facts using `WebApplicationFactory<Program>`. Skip for production code — 60 s is fine in real ops.

**Example:**
```csharp
// Source: C:/Users/UserL/source/repos/sk2_1/tests/SK.WebApi.Schemas.Tests/Fixtures/SchemasE2EWebApplicationFactory.cs lines 71-77
builder.ConfigureTestServices(services =>
{
    services.Configure<PeriodicExportingMetricReaderOptions>(opts =>
    {
        opts.ExportIntervalMilliseconds = 1_000;
    });
});
```

### Pattern 5: Compose-internal-DNS vs host-DNS endpoints

**What:** The collector + sk_p container both run inside the compose default network — they reach ES and Prom via the service-DNS names `elasticsearch` and `prometheus`. The test process runs OUTSIDE compose, on the host, and must reach ES and Prom via `localhost:9200` / `localhost:9090` (the published ports).

**When to use:** Always. This is the load-bearing reason that:
- collector config uses `endpoints: [http://elasticsearch:9200]` (D-06) and `endpoint: 0.0.0.0:8889` (D-07).
- prometheus.yml uses `targets: ['otel-collector:8889']` (D-08).
- E2E test code uses `new HttpClient { BaseAddress = new Uri("http://localhost:9200/") }` and `http://localhost:9090/`.

### Anti-Patterns to Avoid

- **Polling Prometheus from t=0 with no initial sleep.** Returns empty samples for the entire first 15-second window. Use Pattern 3.
- **Querying ES with a fixed sleep instead of poll-with-tolerance.** ES indexing lag varies under load (1–3 s typical, up to 10 s on cold container); fixed sleep is brittle. Use Pattern 2.
- **Letting the SDK default 60 s metric export interval bleed into E2E tests.** Phase 5 tests bypassed this because the file exporter writes synchronously via `Simple` processor; the Prom path is pull-based and the SDK→collector hop is the bottleneck. See Pitfall 7.
- **Asserting on Prometheus metric names without applying the OTel → Prom translation.** E.g., `http.server.request.duration` (OTLP name) becomes `http_server_request_duration_seconds_count` / `_sum` / `_bucket` (Prom name) — see Pitfall 1.
- **Using `mapping.mode: otel` without changing every downstream assertion path.** It is the new collector-side default but produces a different index name and field shape; D-06 locks `mode: none`. See Pitfall 2 + Open Q1.
- **Letting two stacks fight over the same container name.** sk-elasticsearch, sk-prometheus, sk-otel-collector container names are already taken by the sibling sk2_1 stack on this host. See Pitfall 4.
- **Asserting on `health_check` extension after dropping it.** Phase 5 plan retains the `health_check` extension at `:13133` for host-side smoke probes; if planner drops it (since the file exporter is gone) the smoke probe needs an alternative path.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| OTLP → ES log shipping | A C# direct-write log sink (e.g., NEST/Elastic.Clients) | The collector's `elasticsearch` exporter | Handles batching, retries, mapping mode, data stream routing, bulk action semantics; spec-driven |
| OTLP → Prom metric pull | A `Prometheus.Client` .NET in-process exporter | The collector's `prometheus` exporter | sk2_1 lock-in (D-07); centralizes filter + naming translation in one config; in-process exporter would bypass the collector pipeline + filter/health_metrics |
| `/health/*` exclusion from metrics | A per-request SDK Filter callback | `filter/health_metrics` OTTL processor in collector (D-04) | OpenTelemetry.Instrumentation.AspNetCore 1.15.0 metrics-side `AddAspNetCoreInstrumentation` is **parameterless** — no Filter callback overload exists on `MeterProviderBuilder`. The Collector-side processor was a Phase 5 fix-forward; carry forward. `[VERIFIED: src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs lines 67-72]` |
| ES poll backoff | Fixed `Thread.Sleep(5000)` | Exponential backoff with budget cap (Pattern 2) | Faster on green path; bounded on slow path; sk2_1 precedent |
| Prom result-vector summation | Single-sample assertion | `SumSampleValues` across multiple label combinations (Pattern 3) | The result vector may contain multiple samples for different method/status_code labels; summing keeps the assertion robust |
| ES `_search` JSON construction | Manual string concatenation with `$"..."` over user input | `JsonSerializer.Serialize(new { ... })` OR a verified-static template (Pattern 2 example uses `$$"""..."""` raw string literals with double-brace interpolation) | sk2_1's verified static template is sufficient; nothing in test code reads user input |
| Prom URL parameter escape | `query={literal}` | `Uri.EscapeDataString(promql)` | PromQL contains `{}`, `"`, `=` which break unencoded URL queries |
| `WebApplicationFactory<Program>` for E2E | A fresh `HttpClient` against `localhost:8080` (compose-built sk_p container) | In-process factory | Avoids requiring the sk_p Docker image to be rebuilt before tests; matches Phase 5/7/8 conventions |

**Key insight:** The collector is the central nervous system. Phase 11's job is to wire it correctly between SDK (in-process) and backends (containers) — every other shape is downstream of that wiring. Don't build a parallel telemetry path in C#.

## Runtime State Inventory

This phase is a multi-service refactor with backend wiring change; it touches stored data (ES indices), live service config (collector YAML), and build artifacts (test binaries). The inventory:

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| **Stored data** | ES data stream `logs-generic.otel-default` already exists on the local Docker daemon (sk2_1 leftovers, 31 651 docs `[VERIFIED: curl http://localhost:9200/_cat/indices]`). Phase 11 will produce a new data stream / index based on D-06 `mapping.mode: none`. | If keeping sk2_1's prior data: leave it alone (sk_p tests target a different index). If clearing: `curl -XDELETE http://localhost:9200/logs-generic.otel-default` from a planner-owned cleanup step or per-test `_delete_by_query`. **Per-test cleanup discipline (Claude's Discretion):** prefer unique-correlation-id-per-test over drop-index — drops are slower (~1–2 s) + force a data stream re-create that races subsequent test setup; unique correlation IDs scale to N parallel facts cleanly. |
| **Live service config** | `docker inspect sk-elasticsearch / sk-prometheus / sk-otel-collector` `[VERIFIED 2026-05-28]` shows the **container names are already taken** by sibling stack sk2_1's containers (running). sk_p's compose stack uses `sk-otel-collector` for its own collector; if sk2_1 + sk_p both run simultaneously the Docker daemon will reject the second compose-up with a name collision error. | Either (a) bring sk2_1 stack down before bringing sk_p stack up (developer hygiene), OR (b) rename sk_p's container_names to `skp-otel-collector` / `skp-elasticsearch` / `skp-prometheus` to allow co-existence. **Recommend (a) verbatim sk2_1 naming + an explicit README note that sk2_1 must be stopped first** — fewer mutations, matches "verbatim sk2_1 mirror" spirit of D-06..D-13. |
| **OS-registered state** | None — no Windows Task Scheduler, no pm2, no systemd unit references to ES / Prom / collector. `[VERIFIED: not present in repo grep]` | None. |
| **Secrets/env vars** | Existing `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317` in `compose.yaml` `baseapi-service` block (unchanged). No new secrets introduced (dev posture, no auth). `appsettings.Development.json` carries `OpenTelemetry.Endpoint=http://localhost:4317` (used by `dotnet run` outside compose) — unchanged. `[VERIFIED: codebase grep]` | None — but the planner should confirm that no developer has set a host-level `OTEL_EXPORTER_OTLP_ENDPOINT` env var that would collide with the in-process WebApplicationFactory test wiring. |
| **Build artifacts / installed packages** | `tests/.otel-out/telemetry.jsonl` may exist on disk from prior Phase 5 runs. The fixture's position-marker design preserved the file as forensic artifact. | `git rm -r tests/.otel-out/` (planner) — both the dir + `.gitkeep` + `.gitignore` entry per D-05. Optional: keep `.gitkeep` as forensic placeholder (Claude's Discretion). |

**The canonical question — *after every file in the repo is updated, what runtime systems still have the old string cached, stored, or registered?*** Three:
1. **ES data stream `logs-generic.otel-default`** holds sk2_1's 31k logs (different index name from D-06's `logs-generic-default`; no collision with sk_p tests, but the dev's host disk now carries two unrelated log streams).
2. **The running `sk-otel-collector` container** is hot — Phase 11 will `docker compose down && docker compose up` to swap the image. No state stored in the collector itself.
3. **Any prior `dotnet test` background processes** may still hold OTLP gRPC connections to `localhost:4317`. Phase 11 should advise developers to close `dotnet run` instances before stack swap.

## Common Pitfalls

### Pitfall 1: OTel → Prometheus name translation is non-obvious

**What goes wrong:** Test code asserts on `http.server.request.duration` (the OTLP metric name) and finds no samples in Prometheus.

**Why it happens:** The Prom exporter translates names per the [OpenTelemetry → Prometheus specification](https://opentelemetry.io/docs/specs/otel/compatibility/prometheus_and_openmetrics/) `[CITED: opentelemetry.io spec]`:
- dot (`.`) → underscore (`_`)
- monotonic Sum types gain a `_total` suffix (e.g., `db.client.operations` → `db_client_operations_total`)
- Histogram instruments expand to a triplet: `_count` + `_sum` + `_bucket`
- Unit annotations expand: `s` → `_seconds`, `By` → `_bytes`, etc.
- Composite example: `http.server.request.duration` (Histogram, unit `s`) → `http_server_request_duration_seconds_count` / `_sum` / `_bucket`.

**How to avoid:**
- Test code references **only** the Prom-form names. Phase 11 specifically: `http_server_request_duration_seconds_count{service_name="sk-api",http_route="<route>"}`.
- The `service_name="sk-api"` label only surfaces because `resource_to_telemetry_conversion: { enabled: true }` is set on the prometheus exporter (D-07). If that flag is removed, ALL service-scoped queries return empty vectors.
- `http_route` is the ASP.NET Core template string WITHOUT a leading slash (it's `IRoutePattern.RawText`, not `request.path`) — e.g., `api/v1/schemas`, not `/api/v1/schemas` `[CITED: sk2_1 SchemasMetricsE2ETests.cs line 95]`.

**Warning signs:** Empty result vectors on `curl 'http://localhost:9090/api/v1/query?query=http_server_request_duration_seconds_count'` after multiple HTTP requests + ≥30 s.

### Pitfall 2: `mapping.mode` default flip in collector v0.122.0+

**What goes wrong:** Plan-as-written copies sk2_1's YAML verbatim (says `mapping.mode: none`) but the live ES index is `logs-generic.otel-default` — which is the OTel-mode form. Test queries against `logs-generic-default` (per D-06 spec) return 404.

**Why it happens:** Per the [v0.152.0 ES exporter README](https://github.com/open-telemetry/opentelemetry-collector-contrib/blob/v0.152.0/exporter/elasticsearchexporter/README.md) `[CITED]`, the default `mapping.mode` flipped to `otel` in v0.122.0, AND the config option is now deprecated in favor of `X-Elastic-Mapping-Mode` client metadata. CONTEXT D-06 still uses the YAML-form explicit `mapping.mode: none` which is still supported but deprecated. The live sk2_1 ES doc shape (`body.text`, `severity_text`, `scope.name`, `resource.attributes."service.name"`, `data_stream.dataset=generic.otel`) `[VERIFIED 2026-05-28 via curl]` is unambiguously OTel-mode output — meaning **either** the sk2_1 YAML was changed after the index was created, OR the YAML is being ignored.

**How to avoid:**
- Pick ONE mode and propagate consistently. D-06 says `mode: none` → index `logs-generic-default` per spec.
- If keeping `mode: none`: the test query path is `POST http://localhost:9200/logs-generic-default/_search` with body `{ "query": { "term": { "Attributes.correlation.id": "<id>" } } }` — note CAPITAL `Attributes` because `none` mode preserves raw OTLP field names.
- If switching to `mode: otel` (NOT what D-06 locks): the test query path is `POST http://localhost:9200/logs-generic.otel-default/_search` with body `{ "query": { "term": { "attributes.correlation.id": "<id>" } } }` — lowercase `attributes`, dotted-key access for `correlation.id`.

**Warning signs:** ES `/_search` returns HTTP 404 or empty hits despite the collector logs showing `successfully exported logs`. Sanity check: `curl http://localhost:9200/_cat/indices` to see what index actually exists.

See **Open Q1** for the recommended resolution path.

### Pitfall 3: Distroless collector image has no in-container healthcheck path

**What goes wrong:** Plan writes a `healthcheck: test: ["CMD", "wget", ...]` on the otel-collector service. Docker exec fails with "wget: executable file not found in $PATH". Container stays Up but never marked healthy. `depends_on: { condition: service_healthy }` chains stall forever.

**Why it happens:** otel-collector-contrib 0.152.0 is still a distroless image (no /bin/sh, no wget, no curl). Established in Phase 5 Plan 05-01 deviation #1 for v0.95.0 — invariant carries forward.

**How to avoid:**
- Do NOT declare a `healthcheck:` on the otel-collector service. CONTEXT D-15 implicitly accepts this — `otel-collector: condition: service_started` (not `service_healthy`) is the locked posture.
- Host-side smoke probe options:
  - Keep the `health_check` extension at `:13133` in the collector YAML (carry forward Phase 5 pattern) and probe via host `curl http://localhost:13133/`.
  - OR rely on the prometheus + elasticsearch services' own healthchecks PLUS the visible behavior of the test suite (if collector is dead, all logs/metrics tests fail loudly).
- **Recommended:** keep the `health_check` extension — it's free, costs nothing in production, and provides a single host-side liveness probe.

**Warning signs:** `docker compose up -d --wait` exits non-zero with `dependency failed to start: container ... is unhealthy` even though the container shows `Up X seconds`.

### Pitfall 4: Container-name collision with sibling sk2_1 stack

**What goes wrong:** Developer has sk2_1 stack running (its `sk-elasticsearch` + `sk-prometheus` containers are already bound to ports 9200/9090). Brings sk_p stack up. Compose fails: "container name is already in use" OR "Bind for 0.0.0.0:9200 failed: port is already allocated".

**Why it happens:** Both repos use the same `container_name:` literals + the same host port mappings.

**How to avoid:**
- Document in the Phase 11 SUMMARY (and ideally the repo README) that sk_p + sk2_1 stacks are mutually exclusive — only one can run at a time on a given Docker daemon.
- Alternative (NOT recommended — conflicts with verbatim sk2_1 mirror spirit): rename containers to `skp-elasticsearch` etc.
- Verify before compose-up: `docker ps --filter "name=sk-elasticsearch" --format '{{.Names}}'` — if non-empty, prompt the developer to `docker compose -f /path/to/sk2_1/docker-compose.yml down` first.

**Warning signs:** First-pass `docker compose up -d` exits non-zero with port-collision messages even though the local images are pulled.

### Pitfall 5: ES doc cleanup discipline — analog to Phase 3 D-15 psql \l proof

**What goes wrong:** Test N+1 sees Test N's logs (cumulative since data stream persists across test classes). Test that expects "0 results for this correlation ID" returns N results because prior tests with the same correlation ID accumulated.

**Why it happens:** ES data stream is shared across all tests in the suite. Unique-correlation-id-per-test is the clean answer; drop-and-recreate-index is the heavy answer.

**How to avoid:**
- Generate a unique correlation ID per [Fact] (`$"{Guid.NewGuid():N}"`) and assert on that specific ID in the ES query (`term: { "Attributes.correlation.id": "<id>" }`).
- Avoid asserting on metric COUNTS that depend on prior history; assert "≥ N" not "== N" (sk2_1 SchemasMetricsE2ETests uses `Assert.True(totalCount >= SchemasToPost)` for exactly this reason).
- Do NOT drop the index between tests — the next test would then collide with the SDK's first-write data-stream creation race.

**Warning signs:** Test passes alone, fails in suite. Test passes on fresh ES, fails on second `dotnet test` run without a `down -v` in between.

### Pitfall 6: ES cold-start ~30 s; default `start_period: 0s` causes false-negative compose-up

**What goes wrong:** `docker compose up -d --wait` fails after 60 s saying ES is unhealthy. Actually ES is starting normally; `start_period` was too short and Docker started counting healthcheck failures during the bootstrap window.

**Why it happens:** ES 8.x cold-start on a warm dev machine is ~30 s. The default `start_period: 0s` means Docker counts every failed healthcheck attempt toward the `retries: 5` limit; with `interval: 10s`, that exhausts the budget around 50 s — JUST short of ES being ready.

**How to avoid:** D-12 already locks `start_period: 60s` and `retries: 5` and `interval: 10s` — preserves a 110 s effective window. Do not drop this.

**Warning signs:** Compose-up takes ~1 min; if `--wait` exits with "dependency failed" within 60 s, suspect `start_period` was reduced.

### Pitfall 7: Default OTel SDK metric export interval = 60 s

**What goes wrong:** E2E metrics fact times out at 30 s with zero samples in Prom. Sanity check: `curl http://localhost:9090/api/v1/query?query=http_server_request_duration_seconds_count` returns `{"status":"success","data":{"resultType":"vector","result":[]}}`. The SDK never exported.

**Why it happens:** `PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds` defaults to **60 000** ms `[VERIFIED: opentelemetry.io spec + .NET SDK source]`. The in-process WebApplicationFactory boots, the test fires N HTTP requests, the test polls for ≤30 s, the SDK hasn't exported yet.

**How to avoid:** In E2E fixture, override to 1000 ms via Pattern 4 above. Phase 5 file-exporter tests didn't hit this because `ExportProcessorType.Simple` on the OTLP exporter forces synchronous flush per record — but Phase 11 keeps the default Batch processor (the `Simple` setting in `OtelCollectorFixture` was for the file exporter path, not the production posture).

**Warning signs:** ES logs land within seconds; Prom metric queries return empty for the full test budget. Adding `await Task.Delay(70_000);` makes them appear — that's the 60 s default + scrape window + safety margin showing.

### Pitfall 8: appsettings.Development.json overrides OTLP endpoint to localhost

**What goes wrong:** When `dotnet test` boots the in-process WebApplicationFactory under the default `ASPNETCORE_ENVIRONMENT=Development`, it loads `appsettings.Development.json` which sets `OpenTelemetry.Endpoint=http://localhost:4317`. This works (the host has port 4317 mapped from the collector). But if a developer overrides `OTEL_EXPORTER_OTLP_ENDPOINT` to `http://otel-collector:4317` (compose-internal DNS, only resolves inside containers), the in-process test sends OTLP to a name that doesn't resolve, retries fail silently, and no telemetry reaches the collector.

**Why it happens:** appsettings has compose-internal DNS in `appsettings.json` (`http://otel-collector:4317`); appsettings.Development.json corrects to host-DNS. Test environment uses Development by default.

**How to avoid:**
- Preserve the current split: base file = compose DNS, Development file = host DNS. `[VERIFIED: appsettings.json + appsettings.Development.json on disk 2026-05-28]`.
- If the planner wants to set `OTEL_EXPORTER_OTLP_ENDPOINT` defensively in the fixture (the existing Phase 5 `OtelCollectorFixture` does), keep the value `http://localhost:4317`.

**Warning signs:** Collector logs are silent; `docker logs sk-otel-collector` shows no incoming OTLP. Sanity check: `curl -v http://localhost:4317` from the host should refuse-or-connect cleanly (gRPC won't speak over `curl -v` but the TCP probe will pass).

### Pitfall 9: Test parallelism + shared backends

**What goes wrong:** Two test classes annotated `[Collection("Observability")]` run sequentially per the `DisableParallelization = true` definition. But test classes WITHOUT that collection attribute run in parallel — they may post conflicting docs to ES or interleave with the metrics scrape window.

**Why it happens:** xUnit parallel-by-default for separate classes; `[Collection]` is the explicit serialization knob.

**How to avoid:**
- Phase 11 should keep `[Collection("Observability")]` on EVERY new test class touching ES or Prom: the existing `CollectionDefinitions.cs` with `[CollectionDefinition("Observability", DisableParallelization = true)]` is reusable verbatim.
- For round-trip E2E facts, planner should consider whether they need their own `[Collection("E2E")]` to serialize against the existing Phase 5/8/10 integration tests (which use `IClassFixture<Phase8WebAppFactory>` and don't touch ES/Prom but DO exercise the same in-process Kestrel + Postgres).

**Warning signs:** Tests pass alone, flake in suite. Different correlation IDs from different facts show up in a query that expected only one.

## Code Examples

Verified patterns from official + sk2_1 + sk_p sources.

### compose.yaml — full new shape (proposed)

```yaml
# Source: synthesis of sk2_1 docker-compose.yml + sk_p compose.yaml + CONTEXT D-12..D-15.
# Planner picks compose-service ordering (alphabetical vs depends-on) per Discretion.
services:
  postgres:
    image: postgres:17-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: ${POSTGRES_DB}
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    ports:
      - "5433:5432"
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U $$POSTGRES_USER -d $$POSTGRES_DB"]
      interval: 5s
      timeout: 5s
      retries: 10
      start_period: 5s

  elasticsearch:
    # CONTEXT D-10 + D-12.
    image: docker.elastic.co/elasticsearch/elasticsearch:8.15.5
    container_name: sk-elasticsearch
    environment:
      - discovery.type=single-node
      - xpack.security.enabled=false
      - xpack.security.enrollment.enabled=false
      - ES_JAVA_OPTS=-Xms512m -Xmx512m
    ports:
      - "9200:9200"
    healthcheck:
      # Single-quote the URL so the shell does not treat & as a background operator.
      test: ["CMD-SHELL", "curl -fs 'http://localhost:9200/_cluster/health?wait_for_status=yellow&timeout=5s' || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 5
      start_period: 60s

  otel-collector:
    # CONTEXT D-09 + D-14. Image bump 0.95.0 -> 0.152.0; port 8889 added; file bind-mount + user override removed.
    image: otel/opentelemetry-collector-contrib:0.152.0
    container_name: sk-otel-collector
    restart: unless-stopped
    command: ["--config=/etc/otel-collector-config.yaml"]
    volumes:
      - ./compose/otel-collector-config.yaml:/etc/otel-collector-config.yaml:ro
    ports:
      - "4317:4317"
      - "4318:4318"
      - "8889:8889"
      - "13133:13133"   # keep host-side smoke probe path
    # NO healthcheck declared — distroless image (Pitfall 3).

  prometheus:
    # CONTEXT D-11 + D-13.
    image: prom/prometheus:v3.11.3
    container_name: sk-prometheus
    command:
      - "--config.file=/etc/prometheus/prometheus.yml"
      - "--web.enable-lifecycle"
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml:ro
    ports:
      - "9090:9090"
    depends_on:
      otel-collector:
        condition: service_started
    healthcheck:
      test: ["CMD", "wget", "--no-verbose", "--tries=1", "--spider", "http://localhost:9090/-/healthy"]
      interval: 10s
      timeout: 5s
      retries: 3
      start_period: 10s

  baseapi-service:
    build:
      context: .
      dockerfile: Dockerfile
    restart: unless-stopped
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ConnectionStrings__Postgres: "Host=postgres;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
    ports:
      - "8080:8080"
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:8080/health/ready"]
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 30s
    depends_on:
      postgres:        { condition: service_healthy }
      otel-collector:  { condition: service_started }
      elasticsearch:   { condition: service_healthy }
      prometheus:      { condition: service_healthy }

volumes:
  pgdata:
```

### prometheus.yml — verbatim sk2_1

```yaml
# Source: C:/Users/UserL/source/repos/sk2_1/prometheus.yml (verbatim per CONTEXT D-08).
global:
  scrape_interval: 15s
  evaluation_interval: 15s
  scrape_timeout: 10s

scrape_configs:
  - job_name: 'otel-collector'
    static_configs:
      - targets:
          - 'otel-collector:8889'
    metrics_path: '/metrics'
```

### compose/otel-collector-config.yaml — full new shape (proposed)

```yaml
# Source: synthesis of sk2_1 otel-collector-config.yaml + CONTEXT D-01..D-07.
# Planner picks edit-in-place vs full-replace (Discretion).
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318

processors:
  # Phase 5 fix-forward — re-pointed at Prom exporter pipeline (CONTEXT D-04).
  filter/health_metrics:
    error_mode: ignore
    metrics:
      datapoint:
        - 'metric.name == "http.server.request.duration" and IsMatch(attributes["http.route"], "^/health/.*")'

exporters:
  elasticsearch:
    # CONTEXT D-06. mode: none -> index logs-generic-default (per spec; sk2_1 reference also uses none but live ES shows otel-mode output — Open Q1).
    endpoints:
      - http://elasticsearch:9200
    mapping:
      mode: none

  prometheus:
    # CONTEXT D-07. resource_to_telemetry_conversion: true makes service.name a Prom label (load-bearing for D-17 assertions).
    endpoint: 0.0.0.0:8889
    resource_to_telemetry_conversion:
      enabled: true
    send_timestamps: true

extensions:
  health_check:
    endpoint: 0.0.0.0:13133

service:
  extensions: [health_check]
  pipelines:
    logs:
      receivers: [otlp]
      exporters: [elasticsearch]
    metrics:
      receivers: [otlp]
      processors: [filter/health_metrics]
      exporters: [prometheus]
    # No traces pipeline — CONTEXT D-03.
```

### Stripping `.WithTracing()` from ObservabilityServiceCollectionExtensions.cs

```csharp
// Source: src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs lines 60-82.
// CONTEXT D-03 — delete the entire .WithTracing(t => ...) block; keep MEL bridge + .WithMetrics(...).

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: serviceName,
        serviceVersion: serviceVersion))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());
    // .WithTracing(...) block REMOVED — CONTEXT D-03.
```

**Sub-decisions for the planner:**
- The `using Npgsql;` directive at file top can be removed (only used by `.AddNpgsql()` inside `.WithTracing()`).
- `using OpenTelemetry.Trace;` can be removed.
- `AlwaysOnSampler` reference removed automatically.
- No NuGet pin changes required — `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime`, `Npgsql.OpenTelemetry` package references remain pinned in `Directory.Packages.props`. Whether to also drop `Npgsql.OpenTelemetry` (unused after this change) is Claude's Discretion — recommend KEEP for minimum-diff posture; the package is a 30 KB DLL and removing it is a separate refactor.

### Round-trip E2E fact — recommended shape

```csharp
// Source: synthesis of sk2_1 SchemasLogsE2ETests + sk_p Phase 5/7/8 fixture conventions.
[Trait("Phase", "11")]
[Trait("Category", "E2E")]   // optional per Claude's Discretion — enables `dotnet test --filter "Category!=E2E"`
[Collection("Observability")]
public sealed class RoundTripE2ETests : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;
    private readonly HttpClient _es   = new() { BaseAddress = new Uri("http://localhost:9200/") };
    private readonly HttpClient _prom = new() { BaseAddress = new Uri("http://localhost:9090/") };

    public RoundTripE2ETests(Phase8WebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task PostSchema_Surfaces_Created_LogRecord_In_Elasticsearch_With_CorrelationId()
    {
        var ct = TestContext.Current.CancellationToken;
        var corrId = $"{Guid.NewGuid():N}";

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", corrId);

        // Drive a real business endpoint — Schema POST per D-17 example.
        var dto = new SchemaCreateDto(
            Name: $"E2E-Logs-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Definition: "{ \"type\": \"object\" }",
            Description: null);
        var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct);
        resp.EnsureSuccessStatusCode();

        // Poll ES for a log record with this test's correlation ID.
        // Note: D-06 specifies mapping.mode: none -> index "logs-generic-default"; under "none"
        // OTLP fields are preserved with capital "Attributes". See Open Q1 for the alternative
        // path if the planner reverses to mode: otel.
        var hit = await PollEsForLog(
            indexPath: "logs-generic-default",
            queryBody: $$"""
                {
                  "size": 10,
                  "query": {
                    "bool": {
                      "must": [
                        { "term": { "Attributes.CorrelationId": "{{corrId}}" } }
                      ]
                    }
                  }
                }
                """,
            timeoutMs: 30_000);

        Assert.NotNull(hit);
        // Inspect the record's resource attributes to prove service.name=sk-api came through.
        var resourceAttrs = hit!.Value
            .GetProperty("_source")
            .GetProperty("Resource");  // capital "Resource" because mapping.mode=none
        // ... (planner adapts to the exact field shape under `mode: none`; see Open Q1)
    }

    [Fact]
    public async Task PostSchema_Increments_Http_Server_Request_Duration_Counter_In_Prometheus()
    {
        var ct = TestContext.Current.CancellationToken;
        const int RequestCount = 3;

        using var client = _factory.CreateClient();
        for (var i = 0; i < RequestCount; i++)
        {
            var dto = new SchemaCreateDto(
                Name: $"E2E-Metrics-{Guid.NewGuid():N}",
                Version: "1.0.0",
                Definition: "{ \"type\": \"object\" }",
                Description: null);
            var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct);
            resp.EnsureSuccessStatusCode();
        }

        // OTel name "http.server.request.duration" (Histogram, unit "s") becomes Prom name
        // "http_server_request_duration_seconds_count" + _sum + _bucket (Pitfall 1).
        // service_name label surfaces because resource_to_telemetry_conversion: true (D-07).
        // http_route is the ASP.NET template WITHOUT a leading slash.
        const string query =
            """http_server_request_duration_seconds_count{service_name="sk-api",http_route="api/v1/schemas"}""";
        var samples = await PollPrometheusUntilSumAtLeast(query, threshold: RequestCount);

        Assert.NotEmpty(samples);
        var totalCount = SumSampleValues(samples);
        Assert.True(totalCount >= RequestCount,
            $"Expected http_server_request_duration_seconds_count >= {RequestCount}, got {totalCount}.");
    }

    // PollEsForLog / PollPrometheusUntilSumAtLeast / QueryPrometheus / SumSampleValues
    // helpers exactly as Pattern 2 + Pattern 3 above (verbatim from sk2_1).
}
```

**Sub-decisions for the planner:**
- Use existing `Phase8WebAppFactory` as the in-process Kestrel host (per CONTEXT code_context section). It already wires per-class throwaway Postgres + AppDbContext. Override `PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds=1000` in a Phase11-specific subclass per Pattern 4. Claude's Discretion: introduce `Phase11WebAppFactory` (preferred for clean trait + filter posture) OR teach `Phase8WebAppFactory` to take the override (cheaper but mutates a Phase 8 asset).
- Use `Schema` entity for the round-trip traffic source — has the simplest body (single jsonb field) and exercises full HTTP → Service → Repo → AuditInterceptor → SaveChanges pipeline.
- Drop the trailing `/health/*` E2E assertion (already covered by `HealthEndpointsTests` and `MetricsExportTests` under D-04 filter coverage).

### Drop tests/.otel-out + .gitignore entries

```bash
# Source: CONTEXT D-05.
git rm -r tests/.otel-out/
# Edit .gitignore: remove the "tests/.otel-out/*" + "!tests/.otel-out/.gitkeep" stanza (lines 414-421).
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| file exporter + JSON-lines + position-marker fixture | native `elasticsearch` + `prometheus` collector exporters | Phase 11 | Tests now talk to real backends; cleanup discipline changes (unique-corr-id per test instead of file truncation) |
| ES `mapping.mode` default = older (varies) | ES `mapping.mode` default = `otel` (v0.122.0+) | Collector v0.122.0 release | Default produces `logs-generic.otel-default` data stream; D-06 explicitly sets `mode: none` to override |
| `mapping.mode: <value>` config key | `X-Elastic-Mapping-Mode` client metadata key OR `elastic.mapping.mode` scope attribute | Collector v0.122.0+ | The YAML key is deprecated but still supported; D-06's YAML form continues to work |
| `OpenTelemetry.Instrumentation.AspNetCore.AddAspNetCoreInstrumentation(...).Filter = ...` (metrics-side) | parameterless `AddAspNetCoreInstrumentation()` + Collector-side `filter/health_metrics` processor | Phase 5 fix-forward; carry forward to Phase 11 (D-04) | All `/health/*` metric exclusion happens in the collector; SDK emits everything |
| OTel traces pipeline + spans | Traces removed entirely | Phase 11 D-03 | OBSERV-12 supersedes itself to Out of Scope |
| Phase 5 OtelEndOfSuiteCleanup AssemblyFixture (docker compose stop/start to release file handle) | Direct ES query — no file handle issue | Phase 11 D-05 / D-16 | Fixture deleted; ~750 ms post-restart guard window removed |

**Deprecated/outdated:**
- The `mapping.mode` YAML field is **deprecated** in collector v0.152.0 (still accepted; future-warned) — `X-Elastic-Mapping-Mode` header is the new path. Phase 11 stays on the YAML form per D-06; planner may want to leave a future-cleanup note in SUMMARY.
- The OTel-mode index naming convention `.ds-logs-generic.otel-default-NNNN` adds a `.otel` suffix to the dataset; `none` mode does NOT. Test queries must align with the chosen mode.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `mapping.mode: none` produces an index named exactly `logs-generic-default` (data stream form `.ds-logs-generic-default-NNNN`) under collector v0.152.0 + ES 8.15.5. | Pattern 2, Pitfall 2, Open Q1 | Test queries would 404; mitigation already drafted in Open Q1 (verify on first compose-up via `curl /_cat/indices`) |
| A2 | The `http_route` label for `api/v1/schemas` is exactly `api/v1/schemas` (no leading slash) under sk_p's ASP.NET Core route template. | Code Examples, Pitfall 1 | Wrong shape would cause empty Prom result vectors; mitigation: use `curl 'http://localhost:9090/api/v1/query?query=http_server_request_duration_seconds_count'` to discover actual labels |
| A3 | `Phase8WebAppFactory` can host the E2E facts without modification beyond a `PeriodicExportingMetricReaderOptions` override. | Code Examples | If breakage: minor — introduce `Phase11WebAppFactory` subclass |
| A4 | The collector 0.152.0 `prometheusexporter` still defaults `translation_strategy` to `UnderscoreEscapingWithSuffixes` (i.e., the spec-mandated `_total` / `_seconds` / `_count` suffix behavior preserved). | Pitfall 1 | If translation strategy changed default: test names wouldn't match; verifiable empirically by listing `/metrics` once after first request |
| A5 | sk2_1's `Attributes.correlation.id` lowercase-dotted-key access path works because `mapping.mode: otel` was actually live, NOT `mode: none` as the YAML claims. Under `mode: none`, the key would be capital `Attributes.CorrelationId` (preserving OTLP raw shape including the .NET-style `CorrelationId` scope name). | Pattern 2, Code Examples | Mismatched query → no hits; the Open Q1 resolution path mitigates by verifying live index shape after first compose-up |
| A6 | The `health_check` extension on `:13133` is worth keeping in the new collector config (Phase 5 inheritance). | Code Examples, Pitfall 3 | If dropped: no host-side smoke probe; verifiable workaround is `curl http://localhost:8889/metrics` returning 200 |
| A7 | Compose v5.1.1 `--wait` semantics align with the documented `service_healthy` behavior — i.e., it waits until ES + Prom + Postgres all report healthy + the otel-collector is started. | Pattern 1 | If --wait races a dependency: developer mitigation is `docker compose ps` post-up to verify; CI mitigation is `docker compose up -d --wait --timeout 120` |

## Open Questions

1. **ES index name: `logs-generic-default` (D-06 spec-literal) vs `logs-generic.otel-default` (sk2_1 live test reference)?**
   - **What we know:** CONTEXT D-06 locks `mapping.mode: none` which per the elasticsearch exporter v0.152.0 spec produces the data stream `logs-generic-default` (with backing index `.ds-logs-generic-default-NNNN`). sk2_1's YAML file also locks `mapping.mode: none` but its live `SchemasLogsE2ETests.cs` queries `logs-generic.otel-default` AND the running ES has only `.ds-logs-generic.otel-default-NNNN` indices `[VERIFIED 2026-05-28]`. Either (a) sk2_1's live ES was created under a different (earlier?) collector config that used `mode: otel`, OR (b) the live YAML on disk differs from what the live container is actually using, OR (c) there's a subtle exporter behavior we don't fully understand.
   - **What's unclear:** Which index name is canonical for `mapping.mode: none` under collector v0.152.0 + ES 8.15.5 on Windows Docker Desktop.
   - **Recommendation:** Plan 11 should include a Wave-0 verification task: after the first `docker compose up -d --wait` of the new stack, run a single test traffic generator + `curl http://localhost:9200/_cat/indices` and DOCUMENT the actual data stream name; THEN bake that name verbatim into the test assertion code. The Wave-0 verification result lands in the SUMMARY (and possibly in a Plan 11 deviation note if it differs from D-06's predicted name). This is preferable to writing the code blind against either name.

2. **`OtelCollectorFixture` evolution vs wholesale replacement (Claude's Discretion per D-16)?**
   - **What we know:** Existing fixture is heavily file-exporter-centric: position marker, file-share read, `ReadAllExportedRecords`, `FlushAsync` via in-process MeterProvider/TracerProvider/LoggerProvider, `ExportProcessorType.Simple` override. Almost none of this is reusable for ES + Prom polling.
   - **What's unclear:** Whether the planner wants the `WebApplicationFactory<Program>` wrapping logic retained (env-var pinning, `ConfigureTestServices` discovery of `TestObservabilityController`) — that IS reusable.
   - **Recommendation:** Replace wholesale. Create `Phase11WebAppFactory : WebApplicationFactory<Program>` that (a) pins `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`, (b) overrides `PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds=1000`, (c) `AddApplicationPart(typeof(TestObservabilityController).Assembly)`. ~30 lines, no file I/O. The old `OtelCollectorFixture` gets deleted with the file-exporter assumption.

3. **Single round-trip fact vs separate logs-E2E + metrics-E2E facts (Claude's Discretion per D-17 / D-18)?**
   - **What we know:** sk2_1 has separate classes (SchemasLogsE2ETests + SchemasMetricsE2ETests) because they have very different timing budgets (logs ~3 s, metrics ~30–60 s).
   - **What's unclear:** Whether sk_p prefers cohesion (one class proving both halves work for one HTTP request) or separation (faster targeted debugging when one half fails).
   - **Recommendation:** Mirror sk2_1 — separate classes. Faster debug; clearer failure attribution; cleaner trait filtering (`Category=E2E-Logs` vs `Category=E2E-Metrics` if planner wants finer-grained `--filter` control).

4. **Health_check extension retention vs removal in the new collector YAML?**
   - **What we know:** Phase 5 retained it for host-side curl probe (`curl http://localhost:13133/`) since the distroless image has no in-container probe. With ES + Prom both healthchecked, the test suite has multiple natural smoke paths.
   - **What's unclear:** Whether developer ergonomics warrant keeping it.
   - **Recommendation:** Keep it. Free. Zero runtime cost. Useful for the `docker compose up -d && curl localhost:13133/` smoke pattern that's documented in Phase 5 SUMMARY.

5. **Should the planner introduce a `[Trait("Category","E2E")]` marker (Claude's Discretion per D-18)?**
   - **What we know:** sk2_1 uses `[Trait("Category", "E2E")]` to allow `dotnet test --filter "Category!=E2E"` for fast local runs.
   - **What's unclear:** Whether sk_p's existing test taxonomy uses Category for anything else (Phase 9 D-19 used `[Trait("Phase","9")]` exclusively).
   - **Recommendation:** Add `[Trait("Category","E2E")]` to the new round-trip facts. Cheap, future-proof; gives the developer a `--filter "Category!=E2E"` knob for ~60 s of suite time savings during the inner dev loop.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Docker Desktop (Windows) | Compose stack | yes | Docker 29.3.1 | — |
| Docker Compose v2 | Compose orchestration | yes | v5.1.1 | — |
| curl (Windows) | Host-side ES/Prom probing + healthcheck `CMD-SHELL` in compose | yes | 8.11.0 (Schannel) | wget (already used) |
| .NET 8 SDK | Build + test | yes | 8.0.421 | — |
| `otel/opentelemetry-collector-contrib:0.152.0` | Collector service | already pulled | 0.152.0 | — |
| `docker.elastic.co/elasticsearch/elasticsearch:8.15.5` | Elastic service | already pulled | 8.15.5 | — |
| `prom/prometheus:v3.11.3` | Prometheus service | already pulled | v3.11.3 | — |
| Postgres 17-alpine | Existing — unchanged | already pulled | 17-alpine | — |
| Host ports 9200, 9090, 8889 | ES, Prom, collector Prom-exporter | **currently bound by sibling sk2_1 stack** `[VERIFIED 2026-05-28]` | — | Bring sk2_1 stack down before sk_p up (Pitfall 4) |
| WSL2 backend for Docker Desktop | Performance | assumed yes (project running on Windows 11 Pro, modern Docker Desktop) | — | — |

**Missing dependencies with no fallback:** None — every required tool + image is locally present.

**Missing dependencies with fallback:** None.

**Pre-flight host-port-collision check (recommended in Plan 11 Wave-0):**
```bash
# Detect prior sk2_1 container instances binding the ports sk_p Phase 11 wants.
docker ps --filter "publish=9200" --format "{{.Names}}\t{{.Image}}"
docker ps --filter "publish=9090" --format "{{.Names}}\t{{.Image}}"
docker ps --filter "publish=8889" --format "{{.Names}}\t{{.Image}}"
# If any output is non-empty AND not a sk_p container — bring that stack down first.
```

## Validation Architecture

> Nyquist validation is enabled in `.planning/config.json` (`workflow.nyquist_validation: true`). All Phase 11 testable behaviors must map to a sample-rate-bearing test.

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xUnit v3 (already pinned in `tests/BaseApi.Tests/BaseApi.Tests.csproj`; runner = Microsoft.Testing.Platform per Phase 1 D-deviation #3/#4) |
| Config file | none — relies on xUnit conventions + `[Trait]` filtering |
| Quick run command | `dotnet test SK_P.sln --no-restore -c Release --filter "Category!=E2E"` (after Phase 11 lands the E2E Category trait) |
| Full suite command | `dotnet test SK_P.sln --no-restore -c Release` (~ 30 s pre-Phase-11; will rise to ~80–100 s post-Phase-11 because E2E facts add poll-budget time) |
| Pre-test invariant | `docker compose up -d --wait` must succeed (Postgres + ES + Prom all healthy, otel-collector started) before `dotnet test` runs |

### Phase Requirements → Test Map

| Req ID (proposed) | Behavior | Test Type | Automated Command | File Exists? |
|-------------------|----------|-----------|-------------------|--------------|
| OBSERV-13-LIKE | Logs land in ES `logs-generic-default` with `Attributes.CorrelationId` field | E2E (round-trip) | `dotnet test --filter "FullyQualifiedName~RoundTripE2ETests.PostSchema_Surfaces_Created_LogRecord_In_Elasticsearch_With_CorrelationId"` | Wave 0 |
| OBSERV-13-LIKE | Information-level logs filtered when `Logging:LogLevel:Default=Warning` (still proves single-MEL-filter path) | E2E (negative) | `dotnet test --filter "FullyQualifiedName~LogLevelFilterTests"` (migrated; pollers replace file readers) | Wave 0 |
| OBSERV-14-LIKE | HTTP server metrics appear in Prom with `service_name="sk-api"` label | E2E (round-trip) | `dotnet test --filter "FullyQualifiedName~RoundTripE2ETests.PostSchema_Increments_Http_Server_Request_Duration_Counter_In_Prometheus"` | Wave 0 |
| OBSERV-14-LIKE | `/health/*` requests do NOT produce Prom data points | Integration (negative) | `dotnet test --filter "FullyQualifiedName~MetricsExportTests.Test_HealthPath_Absent_From_HttpServerMetrics"` (migrated) | Wave 0 |
| OBSERV-12 (amend) | Traces pipeline removed; no traces exported | Indirect (build-time absence + `TraceExportTests.cs` deletion is the proof) | none — file deletion is the only sample-rate signal | Wave 0 |
| INFRA-08-LIKE | Compose up brings ES + Prom healthy within 90 s | Manual / Wave-0 verification | `docker compose up -d --wait --timeout 120 && docker compose ps` | Wave 0 |
| INFRA-08-LIKE | baseapi-service `depends_on` chain blocks until ES + Prom healthy | Manual | `docker compose config` then inspect resolved dependency graph | Wave 0 |
| TEST-07-LIKE | E2E suite runs under 90 s total wall-clock | CI / `time dotnet test ...` | `time dotnet test SK_P.sln --filter "Category=E2E"` | Wave 0 |

### Sampling Rate

- **Per task commit:** `dotnet build SK_P.sln -c Release` (already gated zero-warning per Phase 1 D-02) + `dotnet test SK_P.sln --filter "Category!=E2E"` (fast suite, excludes E2E poll budget).
- **Per wave merge:** `docker compose up -d --wait` + full `dotnet test SK_P.sln --no-restore -c Release` + 3 consecutive GREEN per the Phase 3 D-18 cadence.
- **Phase gate (`/gsd-verify-work`):** Full suite green 3 times + byte-identical `psql \l` BEFORE/AFTER (Phase 3 D-15) + `docker compose ps` shows all 5 services healthy/started + ES index actually exists at the predicted name (resolves Open Q1).

### Wave 0 Gaps

- [ ] `tests/BaseApi.Tests/Observability/ElasticsearchTestClient.cs` — covers OBSERV-13-LIKE polling
- [ ] `tests/BaseApi.Tests/Observability/PrometheusTestClient.cs` — covers OBSERV-14-LIKE polling
- [ ] `tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` (or evolve Phase8 — Claude's Discretion) — covers fixture restructure per D-16
- [ ] `tests/BaseApi.Tests/E2E/RoundTripE2ETests.cs` (or `tests/BaseApi.Tests/Observability/RoundTripE2ETests.cs`) — covers D-17
- [ ] (Replace) `tests/BaseApi.Tests/Observability/LogExportTests.cs` (rewrite from file → ES polling)
- [ ] (Replace) `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` (rewrite from file → ES polling)
- [ ] (Replace) `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` (rewrite from file → Prom polling)
- [ ] (Delete) `tests/BaseApi.Tests/Observability/TraceExportTests.cs`
- [ ] (Delete) `tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs`
- [ ] (Replace or delete) `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs`
- [ ] `prometheus.yml` at repo root
- [ ] (Mutate) `compose.yaml` — add 2 services + extend depends_on + image bump
- [ ] (Mutate) `compose/otel-collector-config.yaml` — rewire pipelines per D-01..D-07
- [ ] (Mutate) `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — strip .WithTracing
- [ ] (Edit) `.gitignore` — drop tests/.otel-out stanza
- [ ] (Edit) `.planning/REQUIREMENTS.md` — D-19 amendments

## Security Domain

> Project config carries no explicit `security_enforcement: false`; treat security enforcement as enabled.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Phase 11 dev-mode posture; sk2_1 + sk_p both lock no-auth (CONTEXT Out of Scope: "no TLS, no auth, no persistence" for ES + Prom). Production deployment owns auth (separate repo). |
| V3 Session Management | no | No new sessions introduced. |
| V4 Access Control | no | ES + Prom run open on dev host ports; CONTEXT Deferred items capture this as Out of Scope. |
| V5 Input Validation | yes (limited) | Test-side: `Uri.EscapeDataString` on PromQL (Don't Hand-Roll table); JSON query bodies constructed via verified-static template (Pattern 2). |
| V6 Cryptography | no | No TLS; no hashing introduced. |
| V7 Error Handling and Logging | yes | Logs flow to ES; sensitive data (correlation ID, route, method, status) — no PII; T-05-PII (Phase 5) regression preserved by D-03 trace removal removing the only path that touched parameter values; not regressed. |
| V8 Data Protection | partial | Ephemeral storage (no volume) means logs + metrics are wiped on `docker compose down -v`. Dev posture acceptable; production migration to TLS + auth + persistence is a separate phase (Out of Scope). |
| V9 Communication Security | no | All compose-internal traffic over plain HTTP. Dev posture. |
| V10 Malicious Code | n/a | — |
| V11 Business Logic | n/a | — |
| V12 Files and Resources | partial | `prometheus.yml` bind-mounted read-only (`:ro` per D-13) — best practice. |
| V13 API and Web Service | partial | Test code calls ES + Prom HTTP APIs from host; no auth required by design (dev posture). |
| V14 Configuration | yes | All image pins are exact-version (D-09/10/11) — Phase 1 D-05 + Phase 5 D-10 supply-chain discipline carries forward. |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Image substitution at pull time (typosquatting or registry compromise) | Tampering | Exact-version pins (D-09/10/11); future hardening = pin to sha256 digest (deferred per project Discretion) |
| OTLP exfiltration to attacker-controlled collector | Information Disclosure | `OTEL_EXPORTER_OTLP_ENDPOINT` defensively pinned to `http://localhost:4317` in the fixture per Phase 5 T-05-OTLP-EXFIL; carry forward |
| Logs carrying PII or secrets to ES | Information Disclosure | Phase 4 CorrelationIdMiddleware sanitization (Phase 4 D-02 IsValid guard); Phase 5 T-05-PII (Npgsql default-secure parameter capture) preserved despite traces removal because the only path was via traces (D-03 removes that) |
| Prom remote_write / federation enabling exfil | Information Disclosure | No `remote_write` declared (sk2_1 Out of Scope); no federation enabled |
| ES open admin endpoints (`/_cluster/settings`, `/_security/user`) accessible without auth | Elevation of Privilege | `xpack.security.enabled=false` is locked-in dev posture; access available only via host loopback (`9200:9200`) — not exposed to host network; documented Out of Scope for production |
| Prom open admin endpoints (`/-/reload` via `--web.enable-lifecycle`) | Tampering | Same locked-in dev posture rationale; only on `9090:9090` host-loopback bind |
| Container-name collision causing accidental cross-tenant data mix (sk2_1 logs landing in sk_p ES) | Tampering | Pitfall 4: mutually-exclusive run policy; bring one stack down before the other up |

## Sources

### Primary (HIGH confidence)
- `C:/Users/UserL/source/repos/sk2_1/docker-compose.yml` — verbatim D-12/D-13 source `[VERIFIED: read]`
- `C:/Users/UserL/source/repos/sk2_1/otel-collector-config.yaml` — verbatim D-06/D-07 source `[VERIFIED: read]`
- `C:/Users/UserL/source/repos/sk2_1/prometheus.yml` — verbatim D-08 source `[VERIFIED: read]`
- `C:/Users/UserL/source/repos/sk2_1/tests/SK.WebApi.Schemas.Tests/SchemasLogsE2ETests.cs` — round-trip log E2E pattern `[VERIFIED: read]`
- `C:/Users/UserL/source/repos/sk2_1/tests/SK.WebApi.Schemas.Tests/SchemasMetricsE2ETests.cs` — round-trip metric E2E pattern `[VERIFIED: read]`
- `C:/Users/UserL/source/repos/sk2_1/tests/SK.WebApi.Schemas.Tests/Fixtures/SchemasE2EWebApplicationFactory.cs` — SDK metric-export-interval override pattern `[VERIFIED: read]`
- Phase 5 SUMMARY entries in `.planning/STATE.md` — file exporter rationale, OtelEndOfSuiteCleanup pattern history `[VERIFIED: read]`
- Phase 11 CONTEXT.md D-01..D-19 — locked decisions `[VERIFIED: read]`
- [opentelemetry-collector-contrib v0.152.0 release notes](https://github.com/open-telemetry/opentelemetry-collector-contrib/releases/tag/v0.152.0) `[CITED]`
- [elasticsearch exporter v0.152.0 README](https://github.com/open-telemetry/opentelemetry-collector-contrib/blob/v0.152.0/exporter/elasticsearchexporter/README.md) `[CITED]`
- [prometheus exporter v0.152.0 README](https://github.com/open-telemetry/opentelemetry-collector-contrib/blob/v0.152.0/exporter/prometheusexporter/README.md) `[CITED]`
- [filterprocessor v0.152.0 README](https://github.com/open-telemetry/opentelemetry-collector-contrib/blob/v0.152.0/processor/filterprocessor/README.md) `[CITED]`
- Live empirical probes on Docker daemon 2026-05-28: `docker image ls`, `docker ps`, `curl http://localhost:9200/_cat/indices`, `curl http://localhost:9200/_resolve/index/logs*`, `curl http://localhost:9090/-/healthy`, `docker compose version` `[VERIFIED]`

### Secondary (MEDIUM confidence)
- [OpenTelemetry → Prometheus name translation spec](https://opentelemetry.io/docs/specs/otel/compatibility/prometheus_and_openmetrics/) `[CITED]`
- [OpenTelemetry .NET SDK PeriodicExportingMetricReader default = 60 000 ms](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md) `[CITED]`
- Docker Hub tag verification for the 3 image pins `[VERIFIED: WebFetch 2026-05-28]`

### Tertiary (LOW confidence)
- None. All findings cross-verified against either spec docs or live empirical probes.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — 3 image pins confirmed against Docker Hub + already pulled locally; collector v0.152.0 release notes confirm no breaking changes between 0.95.0 and 0.152.0 affecting the exporters in use beyond the well-known `mapping.mode` default flip.
- Architecture: HIGH — sk2_1 reference repo is locked-in source of truth and was read verbatim; CONTEXT D-01..D-19 prescribe the rewiring shape.
- Pitfalls: HIGH — most pitfalls are inheritance from Phase 5 (distroless image, Prom pull cadence) or sk2_1 lessons-learned (Pitfall 7 SDK metric-export interval, Pitfall 6 ES start_period). Pitfall 2 (mapping.mode index name) carries one open question (Open Q1) but the resolution path is a single curl probe in Wave 0.
- Validation Architecture: HIGH — sample-rate map covers every D-01..D-19 testable claim; one Wave-0 manual probe + N automated facts cover the rest.

**Research date:** 2026-05-28
**Valid until:** 2026-06-27 (30-day window; the collector + ES + Prom landscape is stable on 30-day cadence)
