# Phase 30: Runtime & Business Metrics - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-02
**Phase:** 30-runtime-business-metrics
**Areas discussed:** Instrument ownership & creation, Increment semantics (timing & retry), Instance-id helper & shared resource, Prometheus query test harness

---

## Area selection

| Option | Description | Selected |
|--------|-------------|----------|
| Instrument ownership & creation | Meter/Counter creation pattern + naming | ✓ |
| Increment semantics (timing & retry) | Where/when counters increment, retry double-count | ✓ |
| Instance-id helper & shared resource | Env-resolution helper sharing + one shared OTel resource | ✓ |
| Prometheus query test harness | PollEsForLog analog placement/shape/host-port | ✓ |

**User's choice:** All four areas.

---

## Instrument ownership & creation

| Option | Description | Selected |
|--------|-------------|----------|
| DI holders via IMeterFactory | `OrchestratorMetrics`/`ProcessorMetrics` singletons, `meterFactory.Create(name)`, Counter fields injected into call-sites | ✓ |
| Static Meter + Counter fields | Classic OTel-docs pattern, simplest | |
| Instrument naming: snake_case literal (collector appends `_total`) | `orchestrator_dispatch_sent` etc.; collector's prometheus exporter adds `_total` | ✓ |
| Instrument naming: OTel-dotted | `orchestrator.dispatch.sent`; normalizes identically | |

**User's choice:** DI holders via IMeterFactory; snake_case literal names (D-01, D-02, D-03).
**Notes:** Researcher must verify the collector's `_total` suffix + `.`→`_` normalization so the SPEC's `..._total` acceptance names land with no collector config change.

---

## Increment semantics (timing & retry)

| Option | Description | Selected |
|--------|-------------|----------|
| Symmetric rule: SENT after Send, CONSUMED at consume-entry | Confirmed-delivery vs confirmed-receipt | ✓ |
| Count attempts before Send | Counts sends that may infra-throw | |
| Route both processor send paths through one `SendResult` | Early `SendOne` + final loop both counted (outcome lowercased) | ✓ |
| Count only the final-loop sends | Would miss the early Failed/Cancelled sends | |

**User's choice:** Symmetric rule (D-04–D-08); single `SendResult` increment point for the processor.
**Notes:** Accepted tradeoff — `Immediate(3)` retry re-runs the consume and re-increments the consumed counters; acceptable rate-over-window noise reflecting real redelivery (D-07). `orchestrator_dispatch_sent` single owner in `StepDispatcher` covers both fire and continuation (D-05).

---

## Instance-id helper & shared resource

| Option | Description | Selected |
|--------|-------------|----------|
| Duplicate `ResolveInstanceId()` per base lib | ~6 lines in each of BaseApi.Core + BaseConsole.Core | ✓ |
| Put helper in Messaging.Contracts | Only shared leaf, but a pure message-contract assembly | |
| New shared lib | Overkill for ~6 lines | |
| Resolve id once + share one resource across logs+metrics | Single local id applied to both providers via AddService+AddAttributes | ✓ |

**User's choice:** Duplicate the helper; resolve id once per process; apply one shared resource to both providers (D-09, D-10).
**Notes:** Correctness — resolving twice risks the GUID fallback differing between the logs and metrics resources. `BaseConsole.Core` is hard-forbidden from referencing `BaseApi.Core`, ruling out a shared non-leaf home.

---

## Prometheus query test harness

| Option | Description | Selected |
|--------|-------------|----------|
| New `PrometheusTestClient` (sibling to `ElasticsearchTestClient`) | Same backoff-poll discipline | ✓ |
| Single `PollPromForQuery(promQL, predicate, timeoutMs)` | Covers series-existence AND bottleneck-expression clauses | ✓ |
| Separate PollPromForSeries + PollPromForQuery | Redundant — one query method suffices | |
| Query Prometheus server `:9090` (PromQL engine) | Needed for the bottleneck expression | ✓ |
| Query collector `:8889` | No PromQL engine | |
| New `MetricsRoundTripE2ETests` reusing `RealStackWebAppFactory` | Keeps the log-focused E2E uncoupled | ✓ |
| Extend `SampleRoundTripE2ETests` | Couples metrics assertions to the log E2E | |

**User's choice:** New `PrometheusTestClient` + single `PollPromForQuery` + query the `:9090` Prometheus server + new `MetricsRoundTripE2ETests` reusing the real-stack factory (D-11–D-14).
**Notes:** Planner must confirm the host-published port for the prometheus container in compose.

## Claude's Discretion

- Exact backoff constants / poll timeout budgets in `PrometheusTestClient`.
- Internal field/const naming within the two metrics holders.
- Whether `RealStackWebAppFactory` is promoted to a shared fixture or duplicated.

## Deferred Ideas

None — discussion stayed within phase scope.
