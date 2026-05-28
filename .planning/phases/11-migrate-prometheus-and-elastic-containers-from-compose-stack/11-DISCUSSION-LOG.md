# Phase 11: Migrate Prometheus and Elastic containers from compose stack sk2_1 to sk_p - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-28
**Phase:** 11-migrate-prometheus-and-elastic-containers-from-compose-stack
**Areas discussed:** File exporter coexistence, Smoke test scope + automation, Image versions + pins, Traces pipeline fate

---

## Area Selection

| Option | Description | Selected |
|--------|-------------|----------|
| File exporter coexistence | Existing `tests/.otel-out/telemetry.jsonl` exporter fate; 7 Phase 5 fact classes affected | ✓ |
| Smoke test scope + automation | Manual vs automated round-trip verification | ✓ |
| Image versions + pins | Collector/ES/Prom version pinning strategy | ✓ |
| Traces pipeline fate | Keep / drop / repurpose traces alongside logs+metrics migration | ✓ |

**User's choice:** All 4 areas selected.

---

## File exporter coexistence

| Option | Description | Selected |
|--------|-------------|----------|
| Keep alongside backends (Recommended) | Fan out: logs → {file, ES}; metrics → {file, Prom}; traces → file only. Existing Phase 5 facts unchanged. | |
| Replace with backends only | Drop file exporter entirely. Logs only to ES, metrics only to Prom. Existing 7 facts migrated or deleted. | ✓ |
| Test-only override | Production stack ships ES + Prom only; test-time collector config re-enables file exporter. | |

**User's clarification:** "Otel export logs to ES, metric to PROm. nothing else"
**Locked decision:** File exporter dropped. Logs flow exclusively to Elasticsearch. Metrics flow exclusively to Prometheus.
**Notes:** Implicit follow-on: traces pipeline also has no destination (confirmed under Traces area).

---

## Phase 5 fact classes fate (sub-question under File exporter)

| Option | Description | Selected |
|--------|-------------|----------|
| Migrate to backend queries (Recommended) | LogExportTests → poll ES; MetricsExportTests → poll Prom; TraceExportTests deleted. Follows sk2_1 E2E pattern. | |
| Delete all | Drop all 7 Phase 5 fact classes. | |
| Quarantine behind [Trait] | Mark them `[Trait("Category","v1-Legacy")]` and skip in default runs. | |

**User's clarification:** "from now all tests from ES only" → clarified to "metrics tests from prometheus only same as logs from ES oly. clear"
**Locked decision:** Per-backend test split — log/log-level-filter tests query Elasticsearch only; metrics tests query Prometheus only; trace tests deleted (no backend).

---

## Traces pipeline fate

| Option | Description | Selected |
|--------|-------------|----------|
| Drop traces entirely (Recommended) | Remove traces pipeline from collector + strip `.WithTracing()` from SDK + delete TraceExportTests. Matches sk2_1. | ✓ |
| Keep traces in SDK; no collector pipeline | SDK still emits via `.WithTracing()`; collector has no traces pipeline (silent drop at collector). | |
| Keep traces, ship to ES too | Add traces pipeline routing to ES exporter (APM data streams). | |

**User's choice:** Drop traces entirely.
**Notes:** OBSERV-12 must be moved to Out of Scope in REQUIREMENTS.md.

---

## Image versions + pins

| Option | Description | Selected |
|--------|-------------|----------|
| Inherit sk2_1's pins verbatim (Recommended) | otel-collector-contrib 0.152.0, elasticsearch 8.15.5, prom/prometheus v3.11.3. Battle-tested. | ✓ |
| Latest stable | Today's latest stable for each component. | |
| Conservative — collector 0.115.0 | Smallest upgrade from current 0.95.0. | |

**User's choice:** Inherit sk2_1's pins verbatim.
**Notes:** Collector upgrade is forced anyway — 0.95.0 predates `mapping.mode` on the ES exporter.

---

## Smoke test scope + automation

| Option | Description | Selected |
|--------|-------------|----------|
| Round-trip via HTTP request (Recommended) | Test issues `POST /api/v1/...`, polls ES for log + Prom for metric. Mirrors sk2_1 SchemasLogsE2ETests/MetricsE2ETests. | ✓ |
| Health-probe traffic only | Hit `/health/ready` and verify probe behavior. | |
| Backend reachability only | Test only `/_cluster/health` + `/-/healthy` reachability. | |

**User's clarification:** "round trip or e2e use curl what ever you decide"
**Locked decision:** Round-trip E2E. HTTP client choice (curl vs `HttpClient` vs `WebApplicationFactory`) is Claude's Discretion.

---

## Done check-in

| Option | Description | Selected |
|--------|-------------|----------|
| Ready for context | Write CONTEXT.md; remaining choices become Claude's Discretion. | ✓ |
| Explore more gray areas | Lock more decisions (auth/persistence posture, REQ-ID amendments). | |

**User's choice:** Ready for context.

---

## Claude's Discretion (deferred to planner)

- Exact REQ-ID naming/numbering for new requirements added by this phase.
- Which existing sk_p entity is used as traffic source for the round-trip E2E test(s).
- HTTP client choice (curl vs `HttpClient` vs `WebApplicationFactory`) for E2E tests.
- Polling shape (timeout / interval / retry budget) for E2E backend assertions.
- Whether `OtelCollectorFixture.cs` is replaced wholesale or evolved.
- Whether E2E tests carry a `[Trait("Category","E2E")]` tag.
- Whether the doc-first REQUIREMENTS.md amendment is split into its own commit (Phase 10 D-01/D-02 precedent) or bundled.
- Compose service ordering inside `compose.yaml`.
- Fate of `tests/.otel-out/` directory (delete vs preserve as `.gitkeep`).
- Whether `compose/otel-collector-config.yaml` is edited in place or replaced wholesale.

## Deferred Ideas

- Production-grade ES posture (TLS, auth, persistence volume, multi-node cluster, JVM tuning).
- Production-grade Prom posture (persistence volume, remote_write, alerting/recording rules, federation).
- Kibana + Grafana dashboards.
- Traces backend (Jaeger / Tempo / Zipkin / Elastic APM data streams).
- APM agent / auto-instrumentation.
- Log shipping fallback (persistent disk buffer / WAL).
- Prometheus high-cardinality protection (relabel configs).
- Unified backend (e.g., metrics also to ES via APM data streams).
