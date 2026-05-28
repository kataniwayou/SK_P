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
| 11-01-* | 01 (doc-first) | 1 | OBSERV-* + INFRA-06 amendments | — | REQUIREMENTS.md states logs→ES + metrics→Prom contract | doc-grep | `grep -E 'logs-generic|otel-collector:8889' .planning/REQUIREMENTS.md` | ✅ | ⬜ pending |
| 11-02-* | 02 (compose + collector + prom config) | 2 | INFRA-06 (extended) | — | Compose v2 healthcheck-gated `depends_on` for ES + Prom; collector config validates | structural | `docker compose -f compose.yaml config --quiet && docker compose up -d --wait && curl -fs http://localhost:9200/_cluster/health && curl -fs http://localhost:9090/-/ready` | ❌ W0 (new prometheus.yml) | ⬜ pending |
| 11-03-* | 03 (strip traces + remove file-exporter scaffolding) | 3 | OBSERV-12 → out-of-scope; OBSERV-03/04 unchanged | T-11-01 (over-sharing traces) | No `.WithTracing()` registration; no file exporter; `tests/.otel-out/` removed | unit + grep | `! grep -r '\.WithTracing' src/ && ! grep -r 'tests/\.otel-out' . && dotnet build` | ✅ | ⬜ pending |
| 11-04-* | 04 (test fixtures + client helpers) | 4 | (new) E2E-INFRA-01 — `ElasticsearchTestClient` + `PrometheusTestClient` + `Phase11WebAppFactory` | T-11-02 (test-only credentials leak) | Helpers fail closed on missing backends; no hard-coded prod URLs; no auth headers leak | unit | `dotnet test --filter "FullyQualifiedName~ElasticsearchTestClient\|FullyQualifiedName~PrometheusTestClient"` | ❌ W0 (new helper classes) | ⬜ pending |
| 11-05-* | 05 (migrate existing observability facts) | 4 | OBSERV-03 (metrics→Prom), (new) OBSERV-LOG-* (logs→ES) | — | LogExportTests, LogLevelFilterTests, MetricsExportTests pass against live backends | integration | `dotnet test --filter "FullyQualifiedName~LogExportTests\|FullyQualifiedName~LogLevelFilterTests\|FullyQualifiedName~MetricsExportTests"` | ✅ (existing files migrated in place) | ⬜ pending |
| 11-06-* | 06 (round-trip E2E) | 5 | (new) E2E-ROUNDTRIP-* — HTTP request → ES log w/ correlation.id; HTTP request → Prom metric data point | T-11-03 (correlation.id collision across concurrent tests) | Per-test unique correlation.id; polling timeout < 60s; ES + Prom both queried | integration | `dotnet test --filter "Category=E2E&FullyQualifiedName~RoundTripE2ETests"` | ❌ W0 (new test class) | ⬜ pending |
| 11-07-* | 07 (Wave 0 verification — ES index name) | 0 (Wave 0 task — runs first) | (new) E2E-ROUNDTRIP-01 prerequisite | — | Curl `/_cat/indices` against running stack; record actual index name (`logs-generic-default` vs `logs-generic.otel-default`); bake into test constants | structural | `docker compose up -d --wait && curl -s http://localhost:9200/_cat/indices?v \| grep logs-generic` | ✅ (manual verification recorded in CONTEXT.md updates) | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] **ES index-name verification** (RESEARCH Open Q1) — bring up the new compose stack, curl `_cat/indices`, record the actual index name produced by `mapping.mode: none` against ES 8.15.5 + collector 0.152.0. Bake the correct constant into the test helper.
- [ ] **`ElasticsearchTestClient`** stub (`tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs`) — polling helper with 404 + empty-hits tolerance + exponential backoff (verbatim sk2_1 pattern).
- [ ] **`PrometheusTestClient`** stub (`tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs`) — `/api/v1/query` helper with mandatory initial sleep then poll (verbatim sk2_1 pattern).
- [ ] **`Phase11WebAppFactory`** stub (`tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs`) — `WebApplicationFactory<Program>` subclass with `PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds=1000` override (Pitfall 7).
- [ ] **`RoundTripE2ETests`** class (`tests/BaseApi.Tests/Observability/RoundTripE2ETests.cs`) — `[Trait("Phase","11")] [Trait("Category","E2E")]` tagged smoke test class.
- [ ] **`prometheus.yml`** (new file at repo root) — single scrape job, verbatim from sk2_1.

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
