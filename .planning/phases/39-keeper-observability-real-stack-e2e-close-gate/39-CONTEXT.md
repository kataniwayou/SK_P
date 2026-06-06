# Phase 39: Keeper Observability + Real-Stack E2E + Close Gate - Context

**Gathered:** 2026-06-06
**Status:** Ready for planning

<domain>
## Phase Boundary

Close out the v3.7.0 Keeper milestone with three deliverables, all instrumenting/verifying code that
already exists — **no new Keeper behavior is built here**:

1. **KMET-01/02/03** — a code-defined `Keeper` `Meter` + throughput/outcome counters + bottleneck
   signals (UpDownCounter + histogram), instrumenting the two **existing** fault consumers
   (`FaultEntryStepDispatchConsumer`, `FaultExecutionResultConsumer`) and the **existing** bounded
   probe loop (`L2ProbeRecovery.RunAsync`), following the locked house meter pattern.
2. **TEST-01/02** — real-stack E2E proof of recover-both-paths + give-up-to-DLQ, delivered by
   **extending the two Phase-36 RealStack facts** with `keeper_*` Prometheus scrape assertions.
3. **TEST-03** — `scripts/phase-39-close.ps1`: 3×GREEN + triple-SHA net-zero close gate, now also
   asserting **both DLQs** are message-empty + the probe scratch-key family is scan-clean.

This discussion settled **HOW**. The six requirements (KMET-01/02/03, TEST-01/02/03) act as the
locked scope anchor — there is no separate `39-SPEC.md`; the ROADMAP/REQUIREMENTS requirements ARE
the contract. New capabilities belong in other phases.

</domain>

<decisions>
## Implementation Decisions

### KMET — `Keeper` meter & instrumentation (house pattern)
- **D-01:** A `KeeperMetrics` class built via **`IMeterFactory`** (the .NET 8 DI pattern — **never** a
  `static Meter` field, which leaks across the shared hermetic test process). `MeterName` const =
  `"Keeper"`, referenced at BOTH `meterFactory.Create(MeterName)` AND
  `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(KeeperMetrics.MeterName))` in
  `src/Keeper/Program.cs` (D-02 symmetry from `OrchestratorMetrics`). Registered as a singleton and
  injected into `FaultEntryStepDispatchConsumer`, `FaultExecutionResultConsumer`, and
  `L2ProbeRecovery`. Instruments are **snake_case with NO `_total`/counter suffix** (the collector's
  prometheus exporter appends it — embedding it doubles it). Inherits the ambient
  `service_instance_id` + combined `service_name={name}_{version}` resource labels for free
  (Phase 30 / Phase 38, resource level) — **no Keeper-specific resource work**.

### Counter tagging (GA-2, fork 2a)
- **D-02:** `keeper_dlq_pushed{reason, fault_type, ProcessorId}`. `reason` is a **forward-looking
  closed enum with a single value today: `"probe_exhausted"`** (only the probe give-up branch reaches
  `keeper-dlq` in code) — structured so a future second cause (e.g. `reinject_failed`) is addable
  without a label-shape change. `fault_type ∈ {dispatch, result}` distinguishes which consumer parked
  it (the two consumers differ only by inner `Fault<T>` type).
- **D-03:** Uniform tag scheme across the throughput/outcome counters:
  - `keeper_fault_consumed{fault_type, ProcessorId}` — top of each consumer's `Consume`.
  - `keeper_recovered{fault_type, ProcessorId}` — in the `Recovered` branch (after re-inject + resume).
  - `keeper_workflow_paused{ProcessorId}` — after `context.Publish(PauseWorkflow)`.
  - `keeper_workflow_resumed{ProcessorId}` — in the `Recovered` branch, after `Publish(ResumeWorkflow)`.
  - `keeper_l2_probe_failed{ProcessorId}` — per `RedisException` catch inside `L2ProbeRecovery`.
  - `ProcessorId` = `inner.ProcessorId` (present on both inner messages — KMET-02 "where meaningful").
  - **No `workflowId` label anywhere** (KMET-02 — high-cardinality forbidden). `paused`/`resumed` are
    workflow-scoped events so they carry no `fault_type` (the pause/resume signal is type-agnostic).

### Bottleneck signals (GA-3, fork 3a)
- **D-04:** `keeper_recovery_duration` histogram records **both outcomes, tagged
  `outcome ∈ {recovered, gave_up}`** — the ~60s give-up tail (`MaxAttempts × DelaySeconds` = 12×5) is
  exactly the saturation signal worth capturing, so it is NOT recorded for recoveries only. Use
  **custom second-scale bucket boundaries via `Advice<double>`** (≈ `{1, 5, 10, 30, 60, 120}` seconds,
  tuned to the 0–60s probe window) — OTel's default millisecond buckets are useless here. Measured in
  the **consumer** (a `Stopwatch` spanning the whole intake→terminal: `Publish(Pause)` → probe loop →
  re-inject+`Publish(Resume)` *or* `dlq.Send`) — the probe helper cannot see the pause/reinject span.
- **D-05:** `keeper_in_flight` is an **`UpDownCounter`** measured **inside `L2ProbeRecovery.RunAsync`**
  (`+1` on entry, `-1` in a `finally`) — KMET-03 literally scopes it to "messages currently held in
  probe loops," which is the `RunAsync` body. (Reflects the awaited-inside-Consume design: the broker
  delivery is un-acked for the whole probe window.)

### TEST — real-stack E2E (GA-1, fork 1a — extend, don't rebuild)
- **D-06:** **Extend the two existing Phase-36 RealStack facts** —
  `KeeperRecovery_RecoversBothPaths` and `KeeperRecovery_GivesUp_ParksToDlq` in
  `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` — with a `keeper_*` Prometheus
  scrape-assertion block (mirroring how `MetricsRoundTripE2ETests` asserts series after a live
  round-trip). Do **NOT** author a separate `KeeperMetricsRoundTripE2ETests`: the two facts already
  induce the exact outages TEST-01/02 describe; a second live round-trip would only be slower/flakier.
- **D-07:** **Keep the WRONGTYPE LIST-poison-on-a-GET-key outage recipe** (`ArmWrongTypePoisonAsync`,
  the verbatim spike recipe a6c6825) — deterministic and keeps the rest of the stack Redis-healthy.
  **`docker stop sk-redis` is REJECTED** (it would break every console's Redis soft-dep and make the
  run flaky). TEST-01's "dead-letters BOTH a dispatch and a result" stays as Phase 36 built it: the
  **dispatch path is a genuine fault** (poison `flag[dispatchH]`, the processor's first GET) + the
  **result path is a synthetic `Fault<ExecutionResult>`** (the result hop needs orchestration started
  or a synthetic fault — proven mechanic).
- **D-08:** Assert the new `keeper_*` series appear in Prometheus with the expected
  `fault_type` / `outcome` / `reason` / `ProcessorId` + non-empty `service_instance_id` labels after
  the live recover (FACT 1) and give-up (FACT 2) flows. Reuse the existing `PrometheusTestClient` /
  scrape helpers.

### Close gate (GA-4, fork 4a)
- **D-09:** `scripts/phase-39-close.ps1` = **clone of `scripts/phase-33-close.ps1`** (the proven
  triple-SHA protocol): 3×consecutive-GREEN full suite (no `Category` filter — RealStack runs live) +
  triple-SHA (`psql \l` / redis `--scan` / `rabbitmqctl list_queues name`) BEFORE==AFTER. **Add a
  message-depth==0 assertion on BOTH `keeper-dlq` (DLQ-2) and the `*_error` DLQ-1** via
  `rabbitmqctl list_queues name messages` — the existing name-SHA cannot see message depth. The
  unfiltered redis `--scan` SHA already catches any leaked `skp:keeper:probe:*` scratch key.
- **D-10:** Net-zero DLQ drain stays in the **E2E test teardown** (Phase 36 FACT 2 already ACK-drains
  the `keeper-dlq` parked message + registers all poison + `skp:data/flag/keeper:probe` keys into
  `L2KeysToCleanup`) — **no gate-side `purge_queue`** that could mask a real leak. The gate's job is
  snapshot-and-compare; a teardown regression surfaces as a gate failure (depth>0) rather than passing
  silently. Inherit the pre-flight stable-processor-row seed + container-rebuild discipline from
  `phase-33-close.ps1` (rebuild `baseapi-service orchestrator processor-sample keeper` so the embedded
  SourceHash + the new `Keeper` meter are in the live images).

### Claude's Discretion
- Exact `KeeperMetrics` instrument wiring, the precise `Advice<double>` bucket values within the
  agreed second-scale family, the `reason`/`fault_type`/`outcome` enum constant naming and where they
  live (likely a small `KeeperMetricTags` static or inline consts), `keeper_in_flight` numeric type
  (`long` vs `int`), and the exact `Stopwatch` placement inside each consumer.
- Whether `KeeperMetrics` carries a single shared tag-builder helper to avoid `KeyValuePair` repetition
  across the two consumers + probe helper.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Requirements (the locked scope anchor — read first; no separate SPEC.md)
- `.planning/REQUIREMENTS.md` — **KMET-01/02/03** (meter + counters + bottleneck signals; exact
  instrument names spelled out) and **TEST-01/02/03** (recover-both E2E, give-up E2E, triple-SHA
  close gate incl. both DLQs + scratch scan-clean).
- `.planning/ROADMAP.md` — Phase 39 summary line + the v3.7.0 Keeper milestone goal/build-order.

### House meter pattern (copy this shape for `KeeperMetrics`)
- `src/Orchestrator/Observability/OrchestratorMetrics.cs` — the canonical `IMeterFactory`-built meter:
  `MeterName` const ↔ `AddMeter` symmetry, snake_case no-suffix instruments, per-increment tagging.
- `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` — the second house example (`{outcome}`
  label precedent for the give-up/recover outcome tag).

### Instrumentation sites (where the increments live — already-built code)
- `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` — `fault_consumed` (top), `workflow_paused`
  (after Publish Pause), `recovered`+`workflow_resumed` (Recovered branch), `dlq_pushed` (else branch),
  `recovery_duration` Stopwatch (whole `Consume`). `inner.ProcessorId` = the `ProcessorId` tag.
- `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` — identical increment map, `fault_type=result`.
- `src/Keeper/Recovery/L2ProbeRecovery.cs` — `l2_probe_failed` (per `RedisException` catch),
  `in_flight` UpDownCounter (++ entry / -- finally) around the `for (attempt…)` loop.
- `src/Keeper/Program.cs` — register `KeeperMetrics` singleton + `mp.AddMeter("Keeper")` (symmetry).
- `src/Keeper/ProbeOptions.cs` — `DelaySeconds=5`, `MaxAttempts=12` → the histogram bucket window.

### Observability wiring (resource labels inherited — do NOT re-add per-Keeper)
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` — the shared
  metrics-only OTel path Keeper already calls (`AddBaseConsoleObservability`); supplies
  `service.instance.id` + combined `service.name`. Keeper's meter inherits these at the resource level.
- `compose/otel-collector-config.yaml` — `resource_to_telemetry_conversion: true` promotes resource
  attrs to Prom labels + `add_metric_suffixes` appends the counter suffix (why D-01 omits `_total`).

### E2E to EXTEND (do not duplicate)
- `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` — the Phase-36 RealStack facts
  `KeeperRecovery_RecoversBothPaths` + `KeeperRecovery_GivesUp_ParksToDlq`; the `ArmWrongTypePoisonAsync`
  / `ClearPoisonAsync` recipe, the `L2KeysToCleanup` net-zero teardown, and the `keeper-dlq` ACK-drain.
- `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` — the Prometheus scrape-assertion
  pattern to mirror (Phase 30/38).
- `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` — the scrape client to reuse.

### Close gate to CLONE
- `scripts/phase-33-close.ps1` — the v3.7.0 triple-SHA protocol + pre-flight stable-processor seed +
  container-rebuild + net-zero notes (the direct clone source for `phase-39-close.ps1`).
- `src/Messaging.Contracts/KeeperQueues.cs` — `keeper-dlq` (DLQ-2, no-TTL) + `keeper-fault-recovery`
  queue names the gate's `list_queues` depth assertion targets.

### Prior context that governs this phase
- `.planning/phases/38-metrics-service-instance-labels/38-CONTEXT.md` — the inherited
  `service_name={name}_{version}` + `service_instance_id` labeling Keeper's meter rides on for free.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`OrchestratorMetrics` / `ProcessorMetrics`** — copy-shape for `KeeperMetrics` (IMeterFactory, const
  ↔ AddMeter symmetry, snake_case no-suffix, per-increment tags, `{outcome}` precedent).
- **The two fault consumers + `L2ProbeRecovery`** — every increment site already exists as a labeled
  branch; this phase only threads `KeeperMetrics` in and increments. No control-flow change.
- **`KeeperRecoveryE2ETests`** (RealStack) — already induces both outages, drains `keeper-dlq`, and
  net-zeros scratch keys; extend its two facts with scrape assertions rather than writing new E2E.
- **`PrometheusTestClient` + `MetricsRoundTripE2ETests`** — the proven live-scrape assertion pattern.
- **`phase-33-close.ps1`** — the triple-SHA gate clone source (pre-flight seed + rebuild already coded).

### Established Patterns
- **IMeterFactory, never static Meter** (test-isolation invariant).
- **snake_case, no `_total` suffix** — collector appends it (`add_metric_suffixes`).
- **No `workflowId` label** anywhere (high-cardinality ban, KMET-02).
- **WRONGTYPE LIST-poison-on-a-GET-key** is the house live-fault-trip recipe (a GET key, not SET/output).
- **Net-zero teardown via `L2KeysToCleanup`** (test owns the drain; the gate only snapshots).
- **Container rebuild before any live close gate** (embedded SourceHash + new meter must be in the image).

### Integration Points
- `KeeperMetrics` singleton → injected into both consumers + `L2ProbeRecovery`; `AddMeter("Keeper")`
  in `Program.cs` connects it to the existing metrics-only OTel pipeline → collector → Prometheus.
- The close gate extends the existing `rabbitmqctl list_queues` snapshot from `name` to
  `name messages`, asserting depth==0 on `keeper-dlq` + the `*_error` DLQ-1 (additive to the name-SHA).

</code_context>

<specifics>
## Specific Ideas

- Histogram buckets are tuned to the live probe window `MaxAttempts × DelaySeconds` = 12×5 = 60s, so a
  give-up lands in the top bucket and a fast recover in the bottom — the histogram's shape IS the
  saturation/latency story PromQL reads.
- `reason="probe_exhausted"` is deliberately a single-value enum today, kept as a label (not omitted)
  so adding a second give-up cause later is a value change, not a metric-shape change.
- The two consumers are near-identical (`fault_type` is the only difference) — a shared tag-builder on
  `KeeperMetrics` keeps the two increment sites DRY.

</specifics>

<deferred>
## Deferred Ideas

- **`reinject_failed` as a second `keeper_dlq_pushed{reason}` value** — not emitted today (only the
  probe give-up branch parks to `keeper-dlq`); the enum is shaped to accept it if a future phase makes
  re-inject failure a distinct terminal.
- **An `in_flight`/recovery-rate Grafana dashboard or Prometheus alert rule** — out of scope; this
  phase only emits the series + asserts they scrape. No committed dashboards/rules exist yet.
- **A `keeper_processing`-style intermediate outcome** — the milestone's in-flight signal is the
  UpDownCounter (D-05); finer-grained per-phase timing is not in scope.

None of these are in Phase 39 scope.

</deferred>

---

*Phase: 39-keeper-observability-real-stack-e2e-close-gate*
*Context gathered: 2026-06-06*
