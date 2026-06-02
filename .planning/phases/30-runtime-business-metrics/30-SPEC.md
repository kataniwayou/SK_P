# Phase 30: Runtime & Business Metrics — Specification

**Created:** 2026-06-02
**Ambiguity score:** 0.142 (gate: ≤ 0.20)
**Requirements:** 7 locked

## Goal

Every service emits code-defined metrics carrying a per-replica `service_instance_id` label, and the orchestrator + processor gain send/consume counters (keyed by `ProcessorId`, processor adds `outcome`) so PromQL can measure the orchestrator→processor dispatch send-vs-consume rate diff (the per-processor bottleneck) and per-processor outcome rates across multiple replicas — without high-cardinality `workflowId` labels and without changing the OTel collector's metrics config.

## Background

All services already export metrics through OTLP → `otel-collector` → Prometheus exporter (`:8889`) → Prometheus scrape. `BaseApi.Core.ObservabilityServiceCollectionExtensions` wires `AddAspNetCoreInstrumentation` + `AddHttpClientInstrumentation` + `AddRuntimeInstrumentation`; `BaseConsole.Core.BaseConsoleObservabilityExtensions` wires the `MassTransit` meter + `AddRuntimeInstrumentation` (inherited by `Orchestrator` and every `Processor.*` via `BaseProcessor.Core`). The collector's `resource_to_telemetry_conversion: true` turns resource attributes into Prometheus labels (that is how `service_name` becomes a label today).

Gaps this phase closes:
- **No per-replica identity on any signal.** The OTel resource is built with `ResourceBuilder.AddService(serviceName, serviceVersion)` only — no `service.instance.id`. There is an `Orchestrator:InstanceId` config but it only names a MassTransit endpoint (`Program.cs:27`), not a telemetry attribute.
- **Zero custom business metrics.** No `System.Diagnostics.Metrics.Meter`/`Counter` exists anywhere in `src`. The dispatch-send sites (`StepDispatcher`/`WorkflowFireJob`), the orchestrator result-consume (`ResultConsumer`), and the processor consume/send (`EntryStepDispatchConsumer`) are uninstrumented.
- `StepOutcome` today = `Completed` / `Failed` / `Cancelled` (no in-flight "processing").

The primary new artifacts that do NOT exist yet: a `service.instance.id` resource attribute in both base libs; two `Meter`s (one in `Orchestrator`, one in `BaseProcessor.Core`) with four counters; and a Prometheus-HTTP-API query test path (analog to the existing Elasticsearch `PollEsForLog`).

## Requirements

1. **Instance-id resource attribute (all metrics AND all logs)**: A code-set `service.instance.id` resource attribute gives every signal a uniform per-replica label, with no collector config change.
   - Current: Both base libs build the OTel resource via `AddService(serviceName, serviceVersion)` only — no `service.instance.id`; no metric or log carries a per-replica identity.
   - Target: `BaseApi.Core.ObservabilityServiceCollectionExtensions` and `BaseConsole.Core.BaseConsoleObservabilityExtensions` set `service.instance.id` on the **shared** OTel resource used by BOTH the logs provider and the metrics provider, resolved in code from env precedence `POD_NAME` → `HOSTNAME` → `Environment.MachineName` (final GUID fallback). Result: every metric carries the Prometheus label `service_instance_id`, and every log carries `resource.attributes.service.instance.id` in Elasticsearch — uniformly, **no exceptions**.
   - Acceptance: A Prometheus HTTP query for a runtime metric returns series with a non-empty `service_instance_id`; an Elasticsearch query for logs from each service returns `resource.attributes.service.instance.id` populated; `git diff` of `compose/otel-collector-config.yaml` shows no change to the metrics pipeline.

2. **Runtime metrics carry the instance label (all three process types)**: The existing runtime instrumentation gains the per-replica label.
   - Current: `AddRuntimeInstrumentation` emits .NET runtime metrics in both base libs, but with no instance label.
   - Target: WebApi, Orchestrator, and every `Processor.*` emit runtime metrics carrying `service_instance_id` (a free consequence of Requirement 1; no per-instrument plumbing).
   - Acceptance: A Prometheus query for a `process_runtime_dotnet_*` metric returns series for each `service_name` with `service_instance_id` present.

3. **WebApi HTTP server metrics carry the instance label**: The existing ASP.NET Core HTTP metrics gain the per-replica label.
   - Current: `AddAspNetCoreInstrumentation` emits `http_server_request_duration_seconds_*` with no instance label (health routes collector-filtered).
   - Target: WebApi HTTP server metrics carry `service_instance_id`; the existing `/health/*` filtering is preserved.
   - Acceptance: A Prometheus query for `http_server_request_duration_seconds_count` for the WebApi returns series with `service_instance_id`; `/health/*` routes remain absent from the result.

4. **Orchestrator business counters**: Code-owned send/consume counters keyed by `ProcessorId`.
   - Current: No custom `Meter`/`Counter` in `Orchestrator`; dispatch-send and result-consume are uninstrumented.
   - Target: `Orchestrator` defines a code-owned `Meter` (registered via `AddMeter` on its MeterProvider) with two monotonic counters — `orchestrator_dispatch_sent_total` (incremented where an `EntryStepDispatch` is sent to `queue:{processorId}`) and `orchestrator_result_consumed_total` (incremented in `ResultConsumer`) — each tagged `ProcessorId` (plus the ambient `service_instance_id`). **No `workflowId` tag.**
   - Acceptance: After a live round-trip, a Prometheus query returns both counters with `ProcessorId` + `service_instance_id` labels and NO `workflowId` label, value ≥ 1 for the exercised `ProcessorId`.

5. **Processor business counters (in `BaseProcessor.Core`)**: Code-owned consume/send counters keyed by `ProcessorId`, send tagged by terminal `outcome`.
   - Current: No custom `Meter` in `BaseProcessor.Core`; consume + `ExecutionResult` send are uninstrumented.
   - Target: `BaseProcessor.Core` defines a code-owned `Meter` (registered via `AddMeter`) with `processor_dispatch_consumed_total` (incremented on consuming an `EntryStepDispatch`) and `processor_result_sent_total` (incremented per `ExecutionResult` sent, tagged `outcome` ∈ {`completed`, `failed`, `cancelled`}) — both tagged `ProcessorId` (plus ambient `service_instance_id`); inherited by every `Processor.*`. **No `workflowId` tag; no "processing" outcome.**
   - Acceptance: After a live round-trip, a Prometheus query returns both counters with `ProcessorId` + `service_instance_id`; `processor_result_sent_total` has `outcome` ∈ the three terminal values; NO `workflowId` label; value ≥ 1 for the exercised `ProcessorId`.

6. **Bottleneck measurability (the goal)**: The four counters align so PromQL quantifies per-processor backlog across replicas.
   - Current: No way to measure the orchestrator→processor dispatch send-vs-consume diff, per processor, across replicas.
   - Target: The counters share the `ProcessorId` label so `sum by (ProcessorId)(rate(orchestrator_dispatch_sent_total[w])) − sum by (ProcessorId)(rate(processor_dispatch_consumed_total[w]))` yields per-processor dispatch backlog (instance summed out for multi-replica), and `rate(processor_result_sent_total{outcome=…})` gives per-processor outcome rates.
   - Acceptance: A real-stack E2E queries Prometheus's HTTP API (`/api/v1/query`) after a live round-trip and asserts (a) both the sent and consumed series exist for the exercised `ProcessorId`, and (b) a by-`ProcessorId` PromQL bottleneck expression combining them evaluates to a numeric result — mirroring the existing ES `PollEsForLog` pattern with a `PollPromForSeries`/`PollPromForQuery` analog (poll-with-timeout for scrape/export latency).

7. **Code-owned metric shape (no collector metric config)**: All new instruments and labels are defined in app code; the collector stays a generic bridge.
   - Current: Metric labels are shaped partly by collector config (`resource_to_telemetry_conversion`, `filter/health_metrics`).
   - Target: All NEW instrument names, types, and labels are defined in application code (`System.Diagnostics.Metrics` + code-set resource attributes). The `otel-collector` metrics pipeline is NOT modified to inject labels or define instruments — the existing generic `resource_to_telemetry_conversion` + health filter are untouched.
   - Acceptance: `git diff` of `compose/otel-collector-config.yaml` across the phase shows no change to the `metrics` pipeline (no new processors, no instrument/label injection).

## Boundaries

**In scope:**
- A code-set `service.instance.id` resource attribute in BOTH base libs (env precedence `POD_NAME`→`HOSTNAME`→`MachineName`/GUID), yielding a uniform `service_instance_id` label on every metric AND every log.
- Two code-owned `Meter`s with four counters: orchestrator `dispatch_sent` / `result_consumed`; processor `dispatch_consumed` / `result_sent{outcome}` — all tagged `ProcessorId`, registered via `AddMeter`.
- A real-stack E2E that queries Prometheus's HTTP API to prove the series, labels, and the by-`ProcessorId` PromQL bottleneck expression.
- A Prometheus-query test helper (analog to the Elasticsearch `PollEsForLog`).

**Out of scope:**
- k8s manifests / Downward-API env injection / verifying `service_instance_id` == pod name in-cluster — no cluster in this repo; the code reads the env, deployment wiring is an ops concern (k8s is past this phase's horizon, confirmed Q2).
- `workflowId` (or any per-execution id) as a metric label — Prometheus high-cardinality risk (D3).
- An in-flight "processing" outcome and any in-flight gauge — deferred (D4).
- Histograms / latency distributions for dispatch (counters/rates only this phase).
- Dashboards, Grafana panels, alerting/recording rules — consumption layer, separate.
- Any change to the collector metrics pipeline or new collector processors (D1 / METRIC-07).
- Renaming or altering existing auto-instrumented metric names.

## Constraints

- `workflowId` (or any unbounded id) MUST NOT be a metric label — Prometheus series-cardinality safety (D3).
- `service.instance.id` MUST be a **resource attribute** (not a per-instrument tag) so it covers auto-instrumented runtime + HTTP metrics and all logs uniformly with no per-call plumbing.
- New instruments registered via `AddMeter` on the existing MeterProvider in the observability wiring; counters monotonic (`…_total`); no collector-side metric config (D1).
- Metrics reach Prometheus over the existing OTLP→collector→Prometheus path (collector unchanged); the E2E MUST tolerate scrape/export latency (poll-with-timeout, like the ES poll) — a single immediate query may miss the sample.
- Logs continue to flow to Elasticsearch unchanged; `service_instance_id` is additive (no log-shape regression — preserves Phase 29's scoped logging).

## Acceptance Criteria

- [ ] Both base libs set `service.instance.id` on the shared OTel resource from `POD_NAME`→`HOSTNAME`→`MachineName`/GUID.
- [ ] A Prometheus query shows a runtime metric with a non-empty `service_instance_id` label for WebApi, Orchestrator, and `Processor.Sample`.
- [ ] An Elasticsearch query shows `resource.attributes.service.instance.id` populated on logs from each service (all logs, no exceptions).
- [ ] WebApi `http_server_request_duration_seconds_*` carries `service_instance_id`; `/health/*` remains filtered out.
- [ ] `orchestrator_dispatch_sent_total` and `orchestrator_result_consumed_total` exist in Prometheus with `ProcessorId` + `service_instance_id` labels and NO `workflowId` label.
- [ ] `processor_dispatch_consumed_total` and `processor_result_sent_total` exist with `ProcessorId` + `service_instance_id`; `processor_result_sent_total` carries `outcome` ∈ {completed, failed, cancelled}; NO `workflowId`.
- [ ] A real-stack E2E queries Prometheus's HTTP API after a round-trip and asserts the sent+consumed series for the exercised `ProcessorId` AND that a by-`ProcessorId` PromQL bottleneck expression evaluates to a numeric result.
- [ ] `git diff` of `compose/otel-collector-config.yaml` shows the `metrics` pipeline unchanged.
- [ ] Full hermetic + real-stack suite GREEN; the close-gate triple-SHA discipline is unaffected (metrics are append-only telemetry, not part of the triple-SHA).

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                        |
|--------------------|-------|------|--------|--------------------------------------------------------------|
| Goal Clarity       | 0.88  | 0.75 | ✓      | Instrument names, sites, labels, and the bottleneck query specified |
| Boundary Clarity   | 0.88  | 0.70 | ✓      | k8s out of horizon; no workflowId; no "processing"; counters not histograms |
| Constraint Clarity | 0.80  | 0.65 | ✓      | Env precedence + cardinality + resource-attribute mechanism locked |
| Acceptance Criteria | 0.85 | 0.70 | ✓      | Proof = live Prometheus-HTTP-API query, mirroring the ES E2E |
| **Ambiguity**      | 0.142 | ≤0.20| ✓      |                                                              |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

Pre-spec design decisions (D1–D6, confirmed before the round): D1 keep collector as a dumb OTLP→Prom bridge / code owns metric shape · D2 `service.instance.id` from pod name (`POD_NAME`/`HOSTNAME`) · D3 drop `workflowId` from labels (cardinality) · D4 defer "processing"/in-flight outcome · D5 placement (processor counters in `BaseProcessor.Core`, orchestrator counters in `Orchestrator`, instance attr in both base libs) · D6 Phase 30, v3.5.0 follow-up, `METRIC-*`.

| Round | Perspective                         | Question summary                                            | Decision locked                                                                 |
|-------|-------------------------------------|-------------------------------------------------------------|---------------------------------------------------------------------------------|
| 1     | Failure Analyst / Seed Closer       | How do we *prove* business metrics reached Prometheus?      | Real-stack E2E queries Prometheus's HTTP API (`/api/v1/query`), like the ES E2E |
| 1     | Boundary Keeper                     | How far into k8s does this phase go?                        | k8s manifest / Downward-API wiring + pod-name verification OUT of scope         |
| 1     | Seed Closer                         | Does the instance label also land on logs?                  | Yes — on ALL logs, no exceptions (shared resource attribute), folded into METRIC-01 |

---

*Phase: 30-runtime-business-metrics*
*Spec created: 2026-06-02*
*Next step: /gsd-discuss-phase 30 — implementation decisions (Meter names, increment call-sites, the Prometheus-query test helper, env-resolution helper)*
