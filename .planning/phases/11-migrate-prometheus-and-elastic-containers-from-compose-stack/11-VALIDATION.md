---
phase: 11
slug: migrate-prometheus-and-elastic-containers-from-compose-stack
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-28
---

# Phase 11 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (.NET 8 / .NET 9) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Observability&Category!=E2E"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Estimated runtime** | ~60–120 seconds (full suite incl. compose-up + ES cold-start ~60s + E2E polling) |

---

## Sampling Rate

- **After every task commit:** Run quick command (observability subset, excludes E2E)
- **After every plan wave:** Run full suite (incl. round-trip E2E facts)
- **Before `/gsd-verify-work`:** Full suite must be green for 3 consecutive runs (Phase 5/10 precedent)
- **Max feedback latency:** 120 seconds

---

## Per-Task Verification Map

> Planner finalizes Task IDs in step 8. Below is the canonical contract — every Phase 11 task must map to one row.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 11-01-* | 01 (REQUIREMENTS.md doc-first amendment) | 1 | OBSERV-12 (supersede), INFRA-06 (extend), OBSERV-13/14 + INFRA-08 + TEST-07 (NEW) | — | REQUIREMENTS.md states logs→ES + metrics→Prom contract; OBSERV-12 moved to Out of Scope; 4 new REQ-IDs documented before any code lands | doc-grep | `grep -E 'logs-generic\|otel-collector:8889' .planning/REQUIREMENTS.md && grep 'SUPERSEDED.*Phase 11 D-03' .planning/REQUIREMENTS.md` | ✅ | ⬜ pending |
| 11-02-* | 02 (compose.yaml — ES + Prom services + collector mutations) | 2 | INFRA-06 (extended), INFRA-08 (NEW) | T-11-02 (image supply chain), T-11-02 (port exposure), T-11-02 (cross-stack name collision) | Compose v2 healthcheck-gated `depends_on` for ES + Prom + collector image pinned to exact version | structural | `docker compose -f compose.yaml config --quiet && docker compose up -d --wait && curl -fs 'http://localhost:9200/_cluster/health?wait_for_status=yellow&timeout=5s' && curl -fs http://localhost:9090/-/healthy` | ✅ (compose.yaml exists; mutation) | ⬜ pending |
| 11-03-* | 03 (collector config rewire) | 2 | OBSERV-03 (unchanged in intent), OBSERV-04 (unchanged), OBSERV-08 (preserved), OBSERV-13 (NEW), OBSERV-14 (NEW) | T-11-03 (logs PII leak), T-11-03 (over-sharing traces), T-11-03 (D-04 filter regression) | logs→[elasticsearch] single sink; metrics→[filter/health_metrics]→[prometheus]; NO traces pipeline; NO file/logging/debug exporters | structural | `docker compose -f compose.yaml restart otel-collector && docker logs --tail 80 sk-otel-collector 2>&1 \| grep 'Started' && curl -fs http://localhost:8889/metrics \| head -5` | ✅ (collector config exists; mutation) | ⬜ pending |
| 11-04-* | 04 (prometheus.yml — repo root scrape config) | 3 | INFRA-08 (NEW), OBSERV-14 (NEW) | T-11-04 (Prom config injection — accept), T-11-04 (high-cardinality — accept) | Single static scrape job pointing at otel-collector:8889 with verbatim sk2_1 timing knobs | structural | `test -f prometheus.yml && docker compose restart prometheus && curl -fs 'http://localhost:9090/api/v1/query?query=up{job=\"otel-collector\"}'` | ❌ W0 (NEW file at repo root) | ⬜ pending |
| 11-05-* | 05 (strip .WithTracing + delete TraceExportTests + OtelEndOfSuiteCleanup + tests/.otel-out + .gitignore) | 3 | OBSERV-12 (consummation) | T-11-05-T1 (residual traces), T-11-05-T2 (orphaned imports), T-11-05-T3 (file-exporter cleanup regression) | No `.WithTracing()` anywhere in src; no telemetry.jsonl directory; .gitignore stanza removed by content-match | unit + grep | `! grep -r '\.WithTracing' src/ && ! test -d tests/.otel-out && ! grep -F '# Phase 5 (CONTEXT.md D-10)' .gitignore && dotnet build` | ✅ (in-place modify + 4 deletions) | ⬜ pending |
| 11-06-* | 06 (Phase11WebAppFactory + ES/Prom test clients + EsIndexNames Wave 0) | 4 | OBSERV-13 (NEW), OBSERV-14 (NEW), TEST-07 (NEW) | T-11-06-T1 (test-only credentials leak), T-11-06-T2 (PromQL injection), T-11-06-T3 (correlation.id collision), T-11-06-T4 (HttpClient socket exhaustion) | Helpers expose no auth headers; EscapeDataString on PromQL; constants populated from Wave 0 probe (NO placeholders left); HttpClient bounded by Dispose | unit + structural | `test -f tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs && test -f tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs && ! grep -E '<[A-Z_]+_FROM_TASK_0>' tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs && dotnet build` | ❌ W0 (4 NEW files) | ⬜ pending |
| 11-07-* | 07 (SchemasLogsE2ETests + SchemasMetricsE2ETests round-trip) | 5 | OBSERV-13 (NEW), OBSERV-14 (NEW), TEST-07 (NEW) | T-11-07-T1 (correlation.id collision), T-11-07-T2 (Prom == vs ≥), T-11-07-T3 (over-broad PromQL match), T-11-07-T5 (OTLP env-var pin) | Per-test unique correlation.id; `>=` not `==` on cumulative samples; service.name="sk-api" asserted (version asserts dropped per WARNING #7) | integration | `dotnet test SK_P.sln --no-restore -c Release --filter "Category=E2E"` | ❌ W0 (2 NEW test classes) | ⬜ pending |
| 11-08a-* | 08a (HealthEndpointsTests rebase + OTLP-absence migration) | 6 | OBSERV-08 (preserved), HEALTH-05 (preserved), OBSERV-13 (NEW) | T-11-08a-T1 (rebase regression), T-11-08a-T2 (premature fixture delete), T-11-08a-T3 (negative-assertion false positive), T-11-08a-T4 (ES query_string escaping) | 5 OtelCollectorFixture references replaced; OTLP-absence fact uses ES poll with 8s negative-budget; OtelCollectorFixture.cs PRESERVED for Plan 11-08b consumers | integration | `! grep 'OtelCollectorFixture' tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs && test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs && dotnet test SK_P.sln --no-restore -c Release --filter "FullyQualifiedName~HealthEndpointsTests"` | ✅ (in-place modify) | ⬜ pending |
| 11-08b-* | 08b (LogExport + LogLevelFilter + MetricsExport migration) | 6 | OBSERV-03 (metrics→Prom), OBSERV-06 (preserved), OBSERV-08 (preserved), OBSERV-13 (NEW), OBSERV-14 (NEW), TEST-07 (NEW) | T-11-08b-T1 (negative-assertion false-positive), T-11-08b-T2 (D-04 filter regression), T-11-08b-T3 (body-content match false-positive), T-11-08b-T4 (orphan fixture) | 3 classes migrated to Phase11WebAppFactory + ES/Prom helpers; D-04 strict-empty health-route invariant preserved; version assertion dropped per WARNING #7 | integration | `! grep 'OtelCollectorFixture' tests/BaseApi.Tests/Observability/{LogExportTests,LogLevelFilterTests,MetricsExportTests}.cs && dotnet test SK_P.sln --no-restore -c Release --filter "FullyQualifiedName~LogExportTests\|FullyQualifiedName~LogLevelFilterTests\|FullyQualifiedName~MetricsExportTests"` | ✅ (in-place modify 3 files) | ⬜ pending |
| 11-08c-* | 08c (OtelCollectorFixture deletion + Phase close) | 6 | OBSERV-13 (NEW), OBSERV-14 (NEW), TEST-07 (NEW) | T-11-08c-T1 (premature deletion breaks build), T-11-08c-T2 (3-run cadence masks flake), T-11-08c-T3 (psql cleanup-discipline regression), T-11-08c-T4 (SUMMARY narrative drift) | OtelCollectorFixture.cs deleted (defensive grep first); 3 consecutive GREEN dotnet test runs at stable count; byte-identical psql `\l` SHA-256 BEFORE/AFTER; Phase 11 SUMMARY narrative committed | integration + manual | `! test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs && ! grep -rn 'OtelCollectorFixture' tests/ src/ --include='*.cs' && dotnet test SK_P.sln --no-restore -c Release` (Task 2 checkpoint covers cadence + psql verification) | ✅ (deletion + 3 doc files) | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] **ES index-name verification** (RESEARCH Open Q1) — Plan 11-06 Task 0 manual checkpoint: bring up the compose stack (Wave 2 / Plan 11-02), drive a single test log through the SDK→collector→ES path, curl `_cat/indices`, record the actual index name produced by `mapping.mode: none` against ES 8.15.5 + collector 0.152.0. Bake IndexAlias + FieldShape + CorrelationIdFieldPath + ResourceAttributesFieldPath into `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs`.
- [ ] **`ElasticsearchTestClient`** (`tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs`) — Plan 11-06 Task 3: polling helper with 404 + empty-hits tolerance + exponential backoff (verbatim sk2_1 Pattern 2).
- [ ] **`PrometheusTestClient`** (`tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs`) — Plan 11-06 Task 4: `/api/v1/query` helper with mandatory initial sleep then poll (verbatim sk2_1 Pattern 3); EscapeDataString on PromQL.
- [ ] **`Phase11WebAppFactory`** (`tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs`) — Plan 11-06 Task 2: `WebApplicationFactory<Program>` subclass with `PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds=1000` override (Pitfall 7); env-var pin; optional logLevelDefaultOverride internal ctor.
- [ ] **`SchemasLogsE2ETests` + `SchemasMetricsE2ETests`** (`tests/BaseApi.Tests/Observability/Schemas*E2ETests.cs`) — Plan 11-07: separated logs/metrics classes per RESEARCH Open Q3 + sk2_1 precedent. `[Trait("Phase","11")] [Trait("Category","E2E")] [Collection("Observability")]`.
- [ ] **`prometheus.yml`** (new file at sk_p repo root) — Plan 11-04: single scrape job, verbatim from sk2_1; bind-mounted by Plan 11-02's prometheus service block.

**Revised iteration 1 note:** Plan 11-08 split into 11-08a/b/c (Wave 6) per checker WARNING #3. No Wave 0 requirements were added by the split — the helpers + Wave 0 ES probe still belong to Plan 11-06 (Wave 4).

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Container-name collision detection | INFRA-06 (extended) | Cannot be automated without conflicting with developer's sibling sk2_1 stack | `docker ps --format '{{.Names}}' \| grep -E '^sk-(elasticsearch\|prometheus\|otel-collector)$'` should be empty before `docker compose up`. Document in PLAN.md as a developer-runtime check. |
| ES startup time on cold daemon | INFRA-06 (extended) | Wall-clock measurement; CI may behave differently than dev laptop | `time docker compose up -d --wait` — confirm < 90s wall-clock with `start_period: 60s` healthcheck. |
| `/_cat/indices` post-traffic | E2E-ROUNDTRIP-* | Visual confirmation that the OTel collector created the expected ES index | After E2E test run: `curl -s http://localhost:9200/_cat/indices?v \| grep logs-generic` — single index/data-stream row expected. |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references (Open Q1 ES index name + 4 new test scaffolds + new prometheus.yml)
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
