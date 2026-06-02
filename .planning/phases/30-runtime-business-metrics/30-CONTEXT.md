# Phase 30: Runtime & Business Metrics - Context

**Gathered:** 2026-06-02
**Status:** Ready for planning

<domain>
## Phase Boundary

Code-defined runtime + business metrics. Every service emits metrics carrying a per-replica
`service_instance_id` label (set as a **resource attribute**, so it lands on auto-instrumented
runtime/HTTP metrics AND on all logs). The orchestrator and processor gain four `ProcessorId`-keyed
send/consume counters (processor adds an `outcome` tag) so PromQL can quantify the
orchestrator→processor dispatch send-vs-consume backlog per processor across replicas. A real-stack
E2E queries Prometheus's HTTP API to prove the series, labels, and the bottleneck expression.

Scope clarifies HOW to implement; WHAT is locked by `30-SPEC.md` (no `workflowId` label, no
"processing" outcome, no collector metric-pipeline change). New capabilities (dashboards, alerting,
histograms, k8s wiring) belong to other phases.

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**7 requirements are locked.** See `30-SPEC.md` for full requirements, boundaries, constraints, and
acceptance criteria. Downstream agents MUST read `30-SPEC.md` before planning or implementing —
requirements are not duplicated here.

**In scope (from SPEC.md):**
- Code-set `service.instance.id` resource attribute in BOTH base libs (env precedence
  `POD_NAME→HOSTNAME→MachineName`/GUID) → uniform `service_instance_id` label on every metric AND log.
- Two code-owned `Meter`s with four counters: orchestrator `dispatch_sent` / `result_consumed`;
  processor `dispatch_consumed` / `result_sent{outcome}` — all tagged `ProcessorId`, registered via `AddMeter`.
- A real-stack E2E querying Prometheus's HTTP API for the series, labels, and the by-`ProcessorId`
  PromQL bottleneck expression.
- A Prometheus-query test helper (analog to the Elasticsearch `PollEsForLog`).

**Out of scope (from SPEC.md):**
- k8s manifests / Downward-API env injection / in-cluster pod-name verification.
- `workflowId` (or any per-execution id) as a metric label (cardinality, D3).
- An in-flight "processing" outcome and any in-flight gauge (deferred, D4).
- Histograms / latency distributions (counters/rates only this phase).
- Dashboards, Grafana panels, alerting/recording rules.
- Any change to the collector metrics pipeline or new collector processors (D1 / METRIC-07).
- Renaming or altering existing auto-instrumented metric names.

</spec_lock>

<decisions>
## Implementation Decisions

### Instrument ownership & creation
- **D-01:** Two DI-registered singleton holders — `OrchestratorMetrics` (in `Orchestrator`) and
  `ProcessorMetrics` (in `BaseProcessor.Core`) — each constructed via **`IMeterFactory`**
  (`meterFactory.Create(meterName)`), exposing the `Counter<long>` instruments as fields. Injected
  into the call-sites (`StepDispatcher`/`ResultConsumer`; `EntryStepDispatchConsumer`). Chosen over
  static `Meter`/`Counter` fields: matches the DI-heavy codebase, avoids static state across the
  shared hermetic-test process, and `IMeterFactory` is the .NET 8+ recommended pattern.
- **D-02:** Meter names = `"Orchestrator"` and `"BaseProcessor"` — a const each, referenced by both
  `AddMeter(...)` in the observability wiring and the holder's `Create(...)` (names must match).
- **D-03:** Instruments declared **without** the `_total` suffix in code —
  `orchestrator_dispatch_sent`, `orchestrator_result_consumed`, `processor_dispatch_consumed`,
  `processor_result_sent` — snake_case literal (chosen over OTel-dotted for readability next to
  PromQL; both normalize identically). The **collector's** Prometheus exporter appends `_total` (its
  `add_metric_suffixes` default) so the SPEC's `..._total` acceptance names land. **No collector config
  change** (REQ-07). Researcher MUST verify the collector's `_total` + `.`→`_` behavior.

### Increment semantics (timing & retry)
- **D-04:** Symmetric rule — *sent* counters increment **after** the broker `Send` returns (confirmed
  delivery); *consumed* counters increment **at the top** of `Consume` (confirmed receipt).
- **D-05:** `orchestrator_dispatch_sent` increments in `StepDispatcher.DispatchAsync` after
  `endpoint.Send`, tagged `ProcessorId`. Single owner covers BOTH the `WorkflowFireJob` entry fire and
  the `ResultConsumer` continuation. An infra-throw on `Send` correctly skips the increment.
- **D-06:** `orchestrator_result_consumed` increments at the top of `ResultConsumer.Consume`, keyed by
  `m.ProcessorId`, counting **every** consumed result (both the L1-hit advance and the graceful
  L1-miss ack — it was consumed either way).
- **D-07:** `processor_dispatch_consumed` increments at the top of `EntryStepDispatchConsumer.Consume`,
  keyed by `context.Id`. **Accepted tradeoff:** the `Immediate(3)` retry re-runs the consume and
  re-increments — acceptable rate-over-window noise that reflects real redelivery.
- **D-08:** `processor_result_sent{outcome}` — route **both** send paths (the early `SendOne` and the
  final one-by-one loop) through a single private `SendResult(ExecutionResult)` that increments after
  `Send`, tag `outcome = er.Outcome.ToString().ToLowerInvariant()` ∈ {completed, failed, cancelled}
  plus `ProcessorId`. Refactor `SendOne` + the loop to call it so no send path goes uncounted.

### Instance-id helper & shared resource
- **D-09:** **Duplicate** a tiny `static string ResolveInstanceId()` helper in EACH base lib
  (`BaseApi.Core` + `BaseConsole.Core`). The only shared project ref is `Messaging.Contracts` (a pure
  message leaf — wrong home for an env/OTel helper), and `BaseConsole.Core` is hard-forbidden from
  referencing `BaseApi.Core`. A new shared lib is overkill for ~6 lines.
- **D-10:** Resolve the id **once per process** into a local (`POD_NAME → HOSTNAME →
  Environment.MachineName → Guid.NewGuid()`), then apply `.AddService(name, version)` **plus**
  `.AddAttributes([new("service.instance.id", id)])` to BOTH the logs `SetResourceBuilder(...)` and the
  metrics `ConfigureResource(...)` in each base lib. **Correctness:** resolve ONCE — calling the
  resolver twice risks the GUID fallback differing between the logs and metrics resources. Unifies
  today's two separate `AddService` calls.

### Prometheus query test harness
- **D-11:** New `PrometheusTestClient` in `tests/BaseApi.Tests/Observability/Helpers/` (sibling to
  `ElasticsearchTestClient`), same exponential-backoff poll discipline (200ms→3.2s cap, timeout budget
  generous enough for scrape/export latency).
- **D-12:** One method `PollPromForQuery(promQL, predicate, timeoutMs)` hitting
  `GET /api/v1/query?query=…` — covers BOTH acceptance clauses: series-existence (query a counter name
  with a label filter, assert a non-empty result vector) AND the by-`ProcessorId` bottleneck
  expression (assert a numeric result). No separate `PollPromForSeries` needed.
- **D-13:** Query the **Prometheus server** at `localhost:9090` (it has the PromQL engine the
  bottleneck expression needs), NOT the collector's `:8889`. Planner MUST confirm the host-published
  port for the prometheus container in compose.
- **D-14:** A NEW `MetricsRoundTripE2ETests` class (`[Trait("Category","RealStack")]`) reusing the
  `RealStackWebAppFactory` pattern from `SampleRoundTripE2ETests` (promote/share the factory) — drives
  the same seed→Start→round-trip, then queries Prometheus. Keeps the existing log-focused E2E
  uncoupled.

### Claude's Discretion
- Exact backoff constants / poll timeout budgets in `PrometheusTestClient` (mirror
  `ElasticsearchTestClient`'s pattern).
- Internal field/const naming within the two metrics holders.
- Whether `RealStackWebAppFactory` is promoted to a shared fixture or duplicated minimally.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements
- `.planning/phases/30-runtime-business-metrics/30-SPEC.md` — the 7 locked requirements, boundaries,
  constraints, and acceptance criteria. **Read first.**

### Collector contract (MUST NOT change)
- `compose/otel-collector-config.yaml` — the metrics pipeline (`resource_to_telemetry_conversion`,
  `filter/health_metrics`, prometheus exporter on `:8889`) MUST remain unchanged (REQ-07 / D1). The
  `_total` suffix + `.`→`_` normalization are produced by this exporter — verify, do not modify.

No external ADRs — design decisions in this repo are recorded inline as `D-NN` within phase plans, not
as standalone ADR files.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` — the `PollEsForLog`
  poll-with-backoff template that `PrometheusTestClient.PollPromForQuery` mirrors (D-11/D-12).
- `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — the real-stack round-trip harness
  (`RealStackWebAppFactory`, host-port env overrides, net-zero teardown) the new metrics E2E reuses (D-14).
- `src/BaseProcessor.Core/Identity/IProcessorContext.cs` — `context.Id` (the resolved `ProcessorId`)
  is the tag source for the processor counters; safe to read on the consume path (post-Healthy).

### Established Patterns
- **Single dispatch-send owner:** `src/Orchestrator/Dispatch/StepDispatcher.cs` is the sole
  build-and-`Send` site, called from both `WorkflowFireJob` and `ResultConsumer` — one increment there
  (D-05) covers both dispatch origins.
- **Two processor send paths:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` sends
  results from the early `SendOne` (pre-process Failed/Cancelled) AND the final one-by-one loop — both
  must be counted (D-08 routes them through one `SendResult`).
- **Business-ack / infra-throw + `Immediate(3)`:** retries re-run the whole consume — the source of
  the accepted consumed-counter double-count (D-07).
- **OTel wiring seams:** `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs`
  and `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` each build two
  separate `AddService` resources (logs via `SetResourceBuilder`, metrics via `ConfigureResource`) —
  the unify-into-one-shared-resource + `AddMeter` work lands here (D-02/D-09/D-10).

### Integration Points
- `AddMeter("Orchestrator")` in the Orchestrator's metrics wiring; `AddMeter("BaseProcessor")` in
  `BaseConsoleObservabilityExtensions` / `BaseProcessor.Core` wiring (inherited by every `Processor.*`).
- The two new metrics holders are DI-registered singletons resolved into the existing consumers/dispatcher.
- `StepOutcome` enum (`src/Messaging.Contracts/StepOutcome.cs`: Completed/Failed/Cancelled) → lowercased
  `outcome` tag values (D-08).
- Prometheus server HTTP API host port (compose) — the new test client's base address (D-13).

</code_context>

<specifics>
## Specific Ideas

- The four counters must share the `ProcessorId` label so the SPEC's bottleneck PromQL works:
  `sum by (ProcessorId)(rate(orchestrator_dispatch_sent_total[w])) −
   sum by (ProcessorId)(rate(processor_dispatch_consumed_total[w]))` (instance summed out for
  multi-replica). The E2E asserts this evaluates to a numeric result (D-12).
- Symmetry is the mnemonic: SENT = count-after-Send, CONSUMED = count-at-entry (D-04).

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope. (Histograms, an in-flight "processing" gauge, dashboards,
alerting, and k8s pod-name wiring are explicitly out of scope per `30-SPEC.md`.)

</deferred>

---

*Phase: 30-runtime-business-metrics*
*Context gathered: 2026-06-02*
