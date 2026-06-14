# Phase 66: Prometheus + ES Analyzer & PASS/FAIL Engine - Context

**Gathered:** 2026-06-14
**Status:** Ready for planning

<domain>
## Phase Boundary

A **read-only analyzer** that scores a single observation run's correctness **solely** from Elasticsearch logs + Prometheus counters. It aggregates ES logs by `correlationId` into per-run traces, decides per run whether all 9 steps and both sinks (F1, F2) completed, detects **MISSING** runs/steps and **DUPLICATE** step effects against the trigger count, cross-checks the named Prometheus counters, and emits a per-test smoke report + an automated **PASS/FAIL** verdict.

**In scope:** the analyzer artifact (a C# RealStack E2E fixture), its ES per-`correlationId` aggregation + completeness/MISSING/DUPLICATE logic, the Prometheus counter cross-check/reconciliation, the duplicate-vs-redelivery classifier, the PASS/FAIL engine, and the per-test report (JSON + human-readable). Satisfies OBS-01/02/03/04.

**Out of scope (other phases / locked):**
- The fault-injection harness that drives clean → seed → activate → inject → observe → **analyze** → tear down (Phase 67) — it *invokes* this analyzer.
- The live 5-minute / 7-scenario resilience runs (Phase 68).
- The seeder + clean-state reset + minimal-stack bring-up (Phase 65, complete).
- The processor work + `Step_<label>` log shape (Phase 64, locked) and the seconds-cron (Phase 63, locked).
- **No new recovery logic and no new product log/metric** — v8.0.0 scope discipline limits product changes to seconds-cron (Phase 63) + the processor payload/logging (Phase 64). The analyzer consumes ONLY existing telemetry.

</domain>

<decisions>
## Implementation Decisions

### Artifact form & invocation (OBS-04)
- **D-01:** The analyzer is a **C# RealStack E2E fixture** (in `tests/BaseApi.Tests`, likely under `Observability/`), `[Trait("Category","RealStack")]`, run via `dotnet test --filter`. It **reuses `ElasticsearchTestClient` + `PrometheusTestClient` + `EsIndexNames`** directly. Rationale: matches the Phase 65 seeder (D-01/D-02 — C# fixture for contract-bearing logic to avoid writer↔reader desync); the existing clients already encode the verified ES index/field shape (`logs-generic.otel-default`, `attributes.*`) and PromQL escaping; the 67/68 harness already invokes `dotnet test --filter` for the seeder, so the analyzer slots into the same harness step with zero new tooling. (PowerShell/Python rejected: both re-derive the field-shape + counter knowledge the C# clients already prove.)
- **D-02:** Verdict surfacing = **test exit code + always-written report artifact**. The fixture **xUnit-asserts the pass bar** (a FAIL run → failing test → non-zero `dotnet test` exit the harness reads) AND **always writes the report first, then asserts** (write-then-assert ordering) so the report artifact exists even on a failing/red run. Self-verifying like the Phase 65 seeder (D-03).

### Completeness & MISSING/DUPLICATE model (OBS-01, OBS-02)
- **D-03:** The **per-step COMPLETED-effect signal = the Phase-64 processor `Step_<label>` Information log** for a given `(correlationId, StepLabel)`. Phase 64 (D-08) emits exactly one such log per execution and explicitly shaped it as the analyzer's parse target. A run is **COMPLETE** iff the set of distinct `StepLabel` values for its `correlationId` equals the full 9-label set `{Step_A, Step_B, Step_C, Step_D1, Step_E1, Step_F1, Step_D2, Step_E2, Step_F2}` — which necessarily includes both sinks `Step_F1` + `Step_F2` (the sinks are reported explicitly in the trace).
- **D-04:** The authoritative **TRIGGER-COUNT denominator** = the count of **distinct per-fire `correlationId`s from the orchestrator's EXISTING dispatch telemetry** (the orchestrator mints a fresh `correlationId` per cron fire and dispatches entry step A). This is the only source that can see a run that *fired but produced zero downstream step logs* (e.g. processor down at fire — exactly a MISSING run during TEST-02). Cross-checked against `orchestrator_dispatch_sent` (Prom) + the cadence bound (~10 over 5 min / 30 s). **MISSING = (orchestrator-fired correlationIds) − (correlationIds with all 9 labels).** (Step_A-in-ES denominator rejected: blind to fires whose entry step never logged. Cadence math rejected as primary: can't attribute *which* correlationId is missing.)
  - ⚠ **Research item #1:** the exact orchestrator log event + field carrying the per-fire `correlationId` at dispatch time. **No new product log may be added** — the analyzer must use whatever the orchestrator already emits (e.g. dispatch / execution-scope / correlation-filter logs). If no per-fire correlationId is observable from existing ES logs, fall back to `orchestrator_dispatch_sent` + cadence for the count (degrades per-correlationId attribution — flag at research).
- **D-05:** **Window-boundary handling = post-window settle/drain.** After the 5-min observation window closes, the analyzer waits a **bounded drain period** (greater than worst-case 9-step traversal latency) before snapshotting ES/Prom, so in-flight runs complete; then **every** triggered `correlationId` is judged (none excluded). The harness controls timing. Exact drain duration + poll specifics = Claude's discretion (derive from observed traversal latency at research). (Excluding boundary triggers rejected: shrinks the proven sample.)

### Effect-once vs message-redelivery rule (OBS-02 + TEST pass bar)
- **D-06:** A detected **DUPLICATE** `(correlationId, StepLabel)` is **classified, not auto-failed**: if **corroborated by a dedupe-counter increment** (`processor_dispatch_deduped` / `orchestrator_result_deduped` — the system saw the redelivery and collapsed the true downstream effect) → **REPORTED as redelivery** (not a fail, per the milestone's "redelivery reported, not failed" carve-out matching the exactly-once-EFFECT guarantee). A duplicate with **no dedupe accounting** → un-collapsed double-effect → **FAIL**. (If research item #2 shows the `Step_*` log is emitted *post*-dedup so duplicates are structurally impossible, this rule collapses to "any duplicate = FAIL" — either way the analyzer encodes the reconciliation.)
  - ⚠ **Research item #2:** the processor **dedup-gate position relative to `ProcessAsync` / the `Step_*` log** (`processor_dispatch_deduped` is incremented at the existing dedup gate — `ProcessorMetrics.cs`). Determines whether a redelivered message can legitimately re-emit a `Step_*` log (duplicate possible) or is dropped before execution (duplicate impossible).
- **D-07:** **Fail-closed posture on ambiguous/unaccountable evidence.** An unexplained duplicate, or an imbalance that the counters neither clearly explain nor refute, **defaults to FAIL** and is surfaced loudly in the report. For a correctness proof the burden is on the evidence to affirmatively show effect-once held.

### Prometheus role + report/verdict output (OBS-03, OBS-04)
- **D-08:** **ES-primary + Prometheus reconciliation gate.** ES per-run completeness (zero-missing) + effect-once is the **PRIMARY** arbiter. Prometheus counters (`orchestrator_dispatch_sent`, `orchestrator_result_consumed`, `processor_dispatch_consumed`, `processor_result_sent_total{outcome="completed"}`, `processor_dispatch_deduped`, `keeper_reinject_dropped`) must **reconcile** against the trigger count within redelivery/dedupe accounting: an **UNRECONCILED** imbalance (e.g. a dispatched-but-never-completed delta not explained by reported redelivery) is itself a **FAIL** (consistent with D-07 fail-closed); a fully-accounted imbalance is **REPORTED**. Both sources feed the verdict "solely from Prometheus + ES" per OBS-04. (Fixed co-equal thresholds rejected: counter deltas vary by which tier crashed across the 7 scenarios — brittle.)
- **D-09:** **Report = structured JSON (machine) + human-readable summary.** The primary artifact is a structured JSON report the PowerShell harness parses (verdict, trigger count, per-`correlationId` trace, MISSING list, DUPLICATE/redelivery classification, Prometheus counter summary + reconciliation outcome). A human-readable rendering (markdown/console) accompanies it for the operator trace. **Per-test, parameterized by a scenario id**; exact path/dir + JSON schema = Claude's discretion (planner).

### Claude's Discretion
- ES multi-hit aggregation mechanism (Research item #3): the existing `ElasticsearchTestClient.PollEsForLog` returns only `hits[0]`; the analyzer needs **all** hits per `correlationId` across the window — extend the client with a size-bounded / `aggs` / scroll query. Implementation choice (terms aggregation on `attributes.CorrelationId` + `attributes.StepLabel` vs paged `_search`) is the planner's call.
- Exact settle/drain duration + poll intervals (D-05).
- Report file path/dir, JSON schema/field names, and scenario-id parameterization shape (D-09).
- How non-`completed` processor outcomes (`failed`/`cancelled`/`processing` on `processor_result_sent_total{outcome}`) surface in the report (expected to be zero in a clean proof; treat any non-completed terminal outcome as report-surfaced evidence feeding the fail-closed reconciliation).
- Multi-replica `processor-sample` attribution (replicas:2) — attribution is by `correlationId` + `StepLabel`, not instance, so replica identity is informational only.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase requirements & goal (locked)
- `.planning/ROADMAP.md` → "Phase 66: Prometheus + ES Analyzer & PASS/FAIL Engine" (Goal + 4 Success Criteria) and the v8.0.0 milestone header (scope discipline — only TWO product code changes, analyzer adds none; sources of truth = Prometheus + ES only).
- `.planning/REQUIREMENTS.md` → OBS-01, OBS-02, OBS-03, OBS-04 + the TEST-section pass bar ("zero-missing + effect-once; message-level redelivery during the fault is reported, not failed").

### Log shape the analyzer parses (Phase 64 — locked)
- `.planning/phases/64-processor-work-structured-logging/64-CONTEXT.md` — `SampleProcessor` emits exactly one `Step_<label>` Information log per execution with structured params `{StepLabel}` + `{Sum}` and ambient scope ids (`correlationId`, `stepId`, `workflowId`, `processorId`) surfacing as ES `attributes.*` (D-08/D-09/D-10).

### Fixed sets / topology (Phase 65 — locked)
- `.planning/phases/65-fan-out-workflow-seeder-clean-state-stack/65-CONTEXT.md` + `65-SPEC.md` — 9 labels, fan-out at C, sinks F1+F2, cron `*/30 * * * * *`, the RealStack fixture template (`SampleRoundTripE2ETests.cs` host-overrides + net-zero teardown) the analyzer fixture mirrors.

### Reusable analyzer infrastructure (DIRECT reuse)
- `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` — ES `_search` poller (BaseAddress `http://localhost:9200/`). **Returns only `hits[0]` today — extend for multi-hit/aggregation (Research item #3).**
- `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` — `/api/v1/query` (BaseAddress `http://localhost:9090/`), `QueryPrometheus` / `SumSampleValues` / `PollPromForQuery` — directly reusable for OBS-03 counter reads.
- `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` — verified `LogsDataStream = "logs-generic.otel-default"`, `CorrelationIdFieldPath = "attributes.CorrelationId"`, otel field shape (capital-cased scope keys under lowercase `attributes`). The analyzer queries `attributes.StepLabel`, `attributes.Sum`, `attributes.StepId`, `attributes.WorkflowId`, `attributes.ProcessorId` analogously.
- `tests/BaseApi.Tests/Observability/OrchestrationLogsE2ETests.cs`, `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` — worked examples of ES log-readback + Prom counter assertions against the live stack.

### Counter names (confirmed in source — OBS-03)
- `src/Orchestrator/Observability/OrchestratorMetrics.cs` — `orchestrator_dispatch_sent`, `orchestrator_result_consumed`, `orchestrator_result_deduped` (RETAINED-BUT-DORMANT — currently no time series; expect absent/zero).
- `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` — `processor_dispatch_consumed`, `processor_result_sent` (+ `outcome` label), `processor_dispatch_deduped`. Collector appends `_total`; Prom-form e.g. `processor_result_sent_total{outcome="completed"}`.
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` ~:333-340 — `ResultOutcome` maps the terminal result to the lowercase `outcome` tag (`completed`/`failed`/`cancelled`/`processing`). **Dedup gate (`processor_dispatch_deduped`) position relative to the `Step_*` log = Research item #2.**
- `src/Keeper/Observability/KeeperMetrics.cs` — `keeper_reinject_dropped`.

### Denominator source (Research item #1)
- `src/Orchestrator/Dispatch/StepDispatcher.cs` + the Quartz one-shot fire path (mints a fresh per-fire `correlationId`, `Send`s `EntryStepDispatch` for entry step A) — locate the EXISTING log event/field carrying that per-fire `correlationId` for the trigger denominator. No new log may be added.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `ElasticsearchTestClient` + `PrometheusTestClient` + `EsIndexNames` — the analyzer's read surface; reused verbatim except for the multi-hit ES extension (Research item #3).
- `SampleRoundTripE2ETests.cs` RealStack pattern (host-overrides `localhost:9200/9090`, `[Trait("Category","RealStack")]`, net-zero teardown discipline) — the fixture template (same template Phase 65 seeder used).
- Confirmed Prom counter names across Orchestrator/Processor/Keeper meters (above) — the OBS-03 cross-check inputs.

### Established Patterns
- **Anti-desync discipline** (Phase 21 `L2ProjectionKeys`, Phase 63 cron detector, Phase 65 seeder): contract-bearing logic (field shapes, counter names, API contracts) lives in C# that already encodes them — drove D-01.
- **Self-verifying RealStack fixture** invoked by `dotnet test --filter` (Phase 65 D-02/D-03) — drove D-01/D-02.
- **OTel → Prom naming**: monotonic Sums gain `_total`; `service.name` (metrics) is combined `{name}_{version}`, logs `service.name` stays bare (`PrometheusTestClient` docstring) — relevant when label-filtering counters.

### Integration Points
- Live stack ES :9200 + Prometheus :9090 (host-reachable, `compose.yaml`) — the analyzer's only inputs.
- The Phase 67/68 PowerShell harness invokes `dotnet test --filter <analyzer>` after the observation window + drain, reads the exit code + the JSON report (D-02/D-09).

</code_context>

<specifics>
## Specific Ideas

- Pass bar (verbatim): **zero-missing** (every triggered `correlationId` reaches both sinks F1+F2 with all 9 step effects) AND **effect-once** (each step's COMPLETED effect once per `correlationId`); message-level redelivery during a fault is **reported, not failed**.
- Three research items to resolve before/with planning: (1) orchestrator per-fire `correlationId` denominator field (no new log); (2) processor dedup-gate position vs the `Step_*` log; (3) ES multi-hit aggregation extension to `ElasticsearchTestClient`.
- Counter cross-check set (OBS-03): `orchestrator_dispatch_sent`, `orchestrator_result_consumed`, `processor_dispatch_consumed`, `processor_result_sent_total{outcome}`, `processor_dispatch_deduped`, `keeper_reinject_dropped` (+ dormant `orchestrator_result_deduped`).

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope. (Fault-injection harness → Phase 67; live 5-min / 7-scenario runs → Phase 68; Grafana dashboards / long-soak → milestone "Future Requirements (deferred)" in REQUIREMENTS.md.)

</deferred>

---

*Phase: 66-prometheus-es-analyzer-pass-fail-engine*
*Context gathered: 2026-06-14*
</content>
</invoke>
