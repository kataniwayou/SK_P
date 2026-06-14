# Phase 66: Prometheus + ES Analyzer & PASS/FAIL Engine - Research

**Researched:** 2026-06-14
**Domain:** Read-only observability analyzer (C# RealStack E2E fixture) consuming Elasticsearch logs + Prometheus counters
**Confidence:** HIGH (all three research items resolved by direct source trace of the running codebase)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Analyzer = C# RealStack E2E fixture in `tests/BaseApi.Tests` (likely `Observability/`), `[Trait("Category","RealStack")]`, run via `dotnet test --filter`. REUSES `ElasticsearchTestClient` + `PrometheusTestClient` + `EsIndexNames` directly. (PowerShell/Python rejected.)
- **D-02:** Verdict surfacing = test exit code + always-written report artifact. xUnit-assert the pass bar (FAIL → failing test → non-zero exit) AND write-then-assert ordering (report exists even on red).
- **D-03:** Per-step COMPLETED-effect signal = the Phase-64 processor `Step_<label>` Information log for `(correlationId, StepLabel)`. A run is COMPLETE iff distinct `StepLabel` set == the 9-label set `{Step_A, Step_B, Step_C, Step_D1, Step_E1, Step_F1, Step_D2, Step_E2, Step_F2}` (necessarily includes sinks F1+F2).
- **D-04:** TRIGGER-COUNT denominator = count of distinct per-fire `correlationId`s from the orchestrator's EXISTING dispatch telemetry. Cross-checked against `orchestrator_dispatch_sent` (Prom) + cadence bound (~10 over 5 min / 30s). MISSING = (fired correlationIds) − (correlationIds with all 9 labels). **⚠ Research item #1 — resolved below (fallback path mandated).**
- **D-05:** Window-boundary handling = post-window settle/drain. After the 5-min window closes, wait a bounded drain (> worst-case 9-step traversal latency) before snapshotting ES/Prom; then judge EVERY triggered correlationId. Harness controls timing. Exact drain duration + poll specifics = Claude's discretion.
- **D-06:** A detected DUPLICATE `(correlationId, StepLabel)` is classified, not auto-failed: corroborated by a dedupe-counter increment → REPORTED as redelivery; no dedupe accounting → FAIL. **⚠ Research item #2 — resolved below: duplicate Step_* is structurally near-impossible; dedupe counters are dormant.**
- **D-07:** Fail-closed posture on ambiguous/unaccountable evidence. Unexplained duplicate or unexplained imbalance → FAIL, surfaced loudly.
- **D-08:** ES-primary + Prometheus reconciliation gate. ES per-run completeness + effect-once is PRIMARY arbiter; Prom counters must reconcile within redelivery/dedupe accounting; UNRECONCILED imbalance = FAIL; fully-accounted imbalance = REPORTED.
- **D-09:** Report = structured JSON (machine, harness-parsed) + human-readable summary. Per-test, parameterized by scenario id. Exact path/dir + JSON schema = Claude's discretion.

### Claude's Discretion
- ES multi-hit aggregation mechanism (Research item #3) — extend `ElasticsearchTestClient.PollEsForLog` (returns only `hits[0]` today) to all hits per correlationId. terms-agg vs paged `_search` is the planner's call.
- Exact settle/drain duration + poll intervals (D-05).
- Report file path/dir, JSON schema/field names, scenario-id parameterization shape (D-09).
- How non-`completed` processor outcomes (`failed`/`cancelled`/`processing`) surface in the report (expected zero in a clean proof; any non-completed terminal outcome = report-surfaced evidence feeding fail-closed reconciliation).
- Multi-replica `processor-sample` attribution (replicas:2) — by `correlationId` + `StepLabel`, not instance; replica identity informational only.

### Deferred Ideas (OUT OF SCOPE)
None. (Fault-injection harness → Phase 67; live 5-min / 7-scenario runs → Phase 68; Grafana dashboards / long-soak → milestone deferred.)
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| OBS-01 | Aggregate all ES logs sharing a correlationId into a per-run trace; per run decide whether all 9 steps + both sinks (F1, F2) completed. | Research item #3 (multi-hit ES aggregation); D-03 9-label completeness set; the `Step_<label>` log shape (Phase 64 D-08, verified in `SampleProcessor.cs:39`). |
| OBS-02 | Against total cron triggers, detect MISSING runs/steps and DUPLICATE step effects. | Research item #1 (trigger denominator — fallback mandated); Research item #2 (duplicate Step_* near-impossible → duplicate detection is a fail-closed guard, not the common path). |
| OBS-03 | Query Prometheus counters and cross-check dispatched vs completed vs deduped against trigger count. | Confirmed counter names + Prom-form below; `PrometheusTestClient` reuse; **dedupe counters are DORMANT (no increment site) — see Counter Reality table.** |
| OBS-04 | Per-test smoke report (correlationId-aggregated trace + metric summary) + automated PASS/FAIL verdict derived SOLELY from Prom + ES. | D-02 (exit code + write-then-assert); D-09 (JSON + human report); `JsonSerializer.Serialize` + `File.WriteAllTextAsync` patterns already used in test project. |
</phase_requirements>

## Summary

The analyzer is a **read-only C# RealStack E2E fixture** that consumes only existing telemetry — it adds zero product code or product log/metric. All three research items are now resolved by **direct source trace of the running codebase** (not training inference), and two of them invalidate assumptions baked into the CONTEXT decisions, which the planner must address head-on:

1. **The per-fire correlationId is NOT observable from any existing orchestrator happy-path ES log.** `WorkflowFireJob` opens a `correlationId` log scope but emits NO message on the happy path (only business-skip warnings); `StepDispatcher` emits no log at all. The fallback path D-04 anticipated is therefore the *only* path: the trigger denominator must come from `orchestrator_dispatch_sent_total` (Prom) + the cadence bound, NOT from a per-fire correlationId log. Per-correlationId attribution of a MISSING *run* (a fire that produced zero step logs) is consequently impossible from ES alone — the analyzer must reconcile a Prom-derived expected count against the count of ES-observed correlationIds.

2. **A duplicate `Step_<label>` log is structurally near-impossible**, and **all dedupe counters are DORMANT (zero increment sites in `src/`).** The processor's dedup gate is the `exists L2[messageId]` branch (`ProcessorPipeline.cs:94-105`): a redelivery routes to `RunRecoveryAsync`, which never calls `ProcessAsync`, so `SampleProcessor` never re-emits a `Step_*` log on redelivery. The dedup gate is therefore BEFORE the `Step_*` log emission. This collapses D-06's "classify duplicate-vs-redelivery via dedupe counter" rule: there is no live `processor_dispatch_deduped` / `orchestrator_result_deduped` series to corroborate against. The analyzer should treat ANY duplicate `(correlationId, StepLabel)` as a **fail-closed FAIL** (D-07), because the dedupe-counter corroboration D-06 relied on does not exist in telemetry.

3. **ES multi-hit aggregation:** the expected volume is small (~10 fires × 9 steps = ~90 step logs + a handful of orchestrator/keeper logs per 5-min window). A **single size-bounded `_search` (size: ~2000) filtered to the observation window + the `Step_*` log family, returning all hits, then grouped in C# by `attributes.CorrelationId` + `attributes.StepLabel`** is the correct, simplest extension — pagination/scroll/aggs are unnecessary at this volume and a terms-agg loses the per-hit detail (timestamps, sums) the human report wants.

**Primary recommendation:** Build the analyzer as a `[Trait("Category","RealStack")]` fixture mirroring `MetricsRoundTripE2ETests` + `SampleRoundTripE2ETests` (host-override + net-zero teardown). Extend `ElasticsearchTestClient` with a `SearchAllHits(queryBody, indexPath)` method (single bounded `_search`, returns `List<JsonElement>`). Derive the trigger denominator from `orchestrator_dispatch_sent_total` (Prom) bounded by cadence — NOT from a per-fire correlationId log (it does not exist). Make completeness (9-label set per correlationId) the primary ES arbiter; reconcile Prom counters; treat any duplicate `Step_*` or unreconciled imbalance as a fail-closed FAIL. Write the JSON report (then assert) using `System.Text.Json` + `File.WriteAllTextAsync`.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Per-fire trigger minting (correlationId) | Orchestrator (`WorkflowFireJob`) | — | Quartz job mints `NewId.NextGuid()` per fire; the only source that knows a fire happened. |
| Per-step COMPLETED-effect signal | Processor (`SampleProcessor` log via OTel→ES) | — | `Step_<label>` Information log is the parse target (Phase 64 D-08). |
| Dedup / effect-once gate | Processor (`ProcessorPipeline` `exists L2[messageId]`) | — | Redelivery → RECOVERY pass, no re-execution, no second `Step_*` log. |
| Trigger-count denominator (telemetry surface) | Prometheus (`orchestrator_dispatch_sent_total`) | ES (observed correlationId union) | No per-fire correlationId log exists; Prom counter + cadence is the only denominator. |
| Per-run completeness verdict | **Analyzer (test fixture)** — ES read | Prom reconcile | OBS-01/02; ES-primary per D-08. |
| Counter cross-check / reconciliation | **Analyzer** — Prom read | — | OBS-03; `PrometheusTestClient` reuse. |
| PASS/FAIL verdict + report | **Analyzer** — fixture | Harness (consumes exit code + JSON) | OBS-04; D-02/D-09. |

## Standard Stack

This phase introduces NO new product dependencies. The analyzer reuses existing test infrastructure verbatim.

### Core (reused — DIRECT)
| Asset | Location | Purpose | Why Standard |
|-------|----------|---------|--------------|
| `ElasticsearchTestClient` | `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` | ES `_search` poller, BaseAddress `http://localhost:9200/`, backoff 200ms→3.2s, 404/empty-hits tolerance, `Clone()` detach. **Returns only `hits[0]` — must be extended (item #3).** | Encodes the verified field shape + polling discipline. [VERIFIED: read file] |
| `PrometheusTestClient` | `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` | `/api/v1/query`, BaseAddress `http://localhost:9090/`. `QueryPrometheus`, `SumSampleValues`, `PollPromForQuery(promQL, predicate, timeout)`, `VectorNonEmpty`, `HasNumericValue`. `Uri.EscapeDataString` on PromQL. | Directly reusable for OBS-03; no extension needed. [VERIFIED: read file] |
| `EsIndexNames` | `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` | `LogsDataStream = "logs-generic.otel-default"`, `CorrelationIdFieldPath = "attributes.CorrelationId"`, `FieldShape = "otel"`. | The Wave-0-verified ES index/field constants. The analyzer queries `attributes.StepLabel`, `attributes.Sum`, `attributes.WorkflowId`, etc. analogously. [VERIFIED: read file] |
| `Phase8WebAppFactory` / RealStack host-override pattern | `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs:448-538` | host-override ctor (RMQ 5673, Redis 6380, Postgres 5433, otel 4317; **port 9090/9200 NOT overridden** — clients hit server directly), net-zero teardown. | The fixture template the analyzer mirrors (same as Phase 65 seeder). [VERIFIED: read file] |

### Supporting (BCL — already in use in the test project)
| API | Purpose | When to Use |
|-----|---------|-------------|
| `System.Text.Json.JsonSerializer` | Serialize the JSON report; parse ES/Prom responses. | Report write (D-09) + parsing hits. Already used across the test project (e.g. `StepResultContractTests.cs:118`, `HydrationTests.cs:56`). [VERIFIED: grep] |
| `System.IO.File.WriteAllTextAsync` | Write the report artifact (write-then-assert, D-02). | Report emission. (No existing report-writer in the test project — this is the one net-new IO pattern; see Don't Hand-Roll.) [VERIFIED: grep — no prior report writer found] |
| `JsonElement` (cloned via `Clone()`) | Detached hit/sample elements safe to retain after the parsing `using var doc` disposes. | Mirror the existing `ElasticsearchTestClient`/`PrometheusTestClient` Clone discipline when extending. [VERIFIED: read file] |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Size-bounded `_search` (item #3) | ES terms aggregation on `attributes.CorrelationId` + `attributes.StepLabel` | Agg returns counts but loses per-hit detail (timestamp, sum, stepId) the human report wants and the duplicate-detector needs to show *which* hits collided. At ~90 hits, agg is over-engineering. |
| Size-bounded `_search` | scroll / `search_after` pagination | Pagination only matters past the 10k default `_search` ceiling. ~90 hits is two orders of magnitude under; pagination adds state + failure modes for zero benefit. |
| Prom `orchestrator_dispatch_sent_total` denominator | Per-fire correlationId from an orchestrator ES log | **Not available** — no such log is emitted on the happy path (item #1). Not a tradeoff; a hard constraint. |

**Installation:** None — no new packages. (Confirm via the existing `.csproj`; no `npm`/`dotnet add package` step.)

## Architecture Patterns

### System Architecture Diagram

```
                          Quartz cron fire (*/30s)
                                   │
                                   ▼
            ┌──────────────────────────────────────────┐
            │ Orchestrator WorkflowFireJob              │
            │  mints fresh correlationId per fire       │
            │  → StepDispatcher.DispatchAsync(Step A)   │
            │  → orchestrator_dispatch_sent_total++      │  ◄── NO ES log on happy path
            └──────────────────────────────────────────┘
                                   │ EntryStepDispatch (carries correlationId)
                                   ▼
            ┌──────────────────────────────────────────┐
            │ Processor (EntryStepDispatchConsumer)     │
            │  processor_dispatch_consumed_total++       │
            │  Inbound{Correlation,ExecutionScope}Filter │  ◄── opens correlationId + step scopes
            │  ProcessorPipeline.RunAsync:               │
            │   exists L2[messageId]? ─yes→ RECOVERY     │  ◄── DEDUP GATE (no ProcessAsync, no Step_* log)
            │                        └no → FORWARD:      │
            │      processor.ExecuteAsync → SampleProcessor
            │        emits  "step completed {StepLabel} sum {Sum}"  ◄── THE Step_* ES LOG (per (corr,label))
            │      SendResult → processor_result_sent_total{outcome}++
            └──────────────────────────────────────────┘
                                   │ StepCompleted result → orchestrator-result queue
                                   ▼
            ┌──────────────────────────────────────────┐
            │ Orchestrator TypedResultConsumer          │
            │  orchestrator_result_consumed_total++      │
            │  advances DAG → dispatches next step(s)    │  (fan-out at C → D1/D2)
            └──────────────────────────────────────────┘
                                   │  (loop until sinks F1, F2)
                                   ▼
   OTel SDK → otel-collector → ┬→ Elasticsearch (logs-generic.otel-default)   :9200
                               └→ Prometheus (scrape collector exporter)       :9090
                                   │
                  ┌────────────────┴─────────────────┐
                  ▼                                   ▼
        ANALYZER (this phase, read-only test fixture)
        ┌─────────────────────────────────────────────────────────┐
        │ 1. (post-drain) ES SearchAllHits(window, Step_* family)   │
        │ 2. group hits by attributes.CorrelationId → per-run trace  │
        │ 3. per run: distinct StepLabel set == 9-label set? COMPLETE │
        │ 4. duplicate (corr,label)? → FAIL (fail-closed, item #2)   │
        │ 5. Prom: dispatch_sent / consumed / result_sent{outcome} / │
        │    keeper_reinject_dropped → reconcile vs trigger count     │
        │ 6. trigger count = orchestrator_dispatch_sent_total +cadence│
        │ 7. MISSING = fired − complete; verdict = PASS iff zero-     │
        │    missing AND effect-once AND counters reconcile           │
        │ 8. WRITE JSON report  → THEN xUnit-assert PASS (D-02)       │
        └─────────────────────────────────────────────────────────┘
```

### Recommended Project Structure
```
tests/BaseApi.Tests/Observability/
├── Helpers/
│   ├── ElasticsearchTestClient.cs   # EXTEND: add SearchAllHits(...)
│   ├── PrometheusTestClient.cs       # reuse as-is
│   └── EsIndexNames.cs               # reuse as-is (+ optional StepLabel/Sum field path consts)
├── Analysis/                         # NEW (analyzer logic — plain testable objects)
│   ├── RunTrace.cs                   # per-correlationId aggregate (labels, sums, timestamps, dup flags)
│   ├── PassFailEngine.cs             # completeness + MISSING/DUPLICATE + reconciliation → verdict
│   ├── AnalyzerReport.cs             # JSON-serializable report record (D-09 schema)
│   └── PromCounterSnapshot.cs        # the OBS-03 counter read set
└── AnalyzerE2ETests.cs               # NEW: the RealStack fixture (write-then-assert, scenario-id param)
```
Keep `PassFailEngine` a **plain object** (no ES/Prom/host deps) so its decision branches are unit-testable hermetically (anti-desync + validation, below) — the fixture feeds it parsed inputs.

### Pattern 1: ES per-correlationId aggregation (item #3)
**What:** One bounded `_search` over the window for the `Step_*` log family; group in C#.
**When to use:** Always for OBS-01 trace assembly.
**Example (query shape — verified field paths):**
```jsonc
// POST logs-generic.otel-default/_search
// Source: EsIndexNames (verified Wave 0) + SampleProcessor.cs:39 template params
{
  "size": 2000,
  "query": {
    "bool": {
      "filter": [
        { "exists": { "field": "attributes.StepLabel" } },   // the Step_* log family
        { "range": { "@timestamp": { "gte": "<window_start>", "lte": "<snapshot>" } } }
      ]
    }
  },
  "sort": [ { "@timestamp": "asc" } ]
}
```
```csharp
// C# grouping (after SearchAllHits returns List<JsonElement>)
// Each hit: hit._source.attributes.{CorrelationId, StepLabel, Sum, WorkflowId, StepId}
var byRun = hits
    .Select(h => h.GetProperty("_source").GetProperty("attributes"))
    .GroupBy(a => a.GetProperty("CorrelationId").GetString());
// per group: distinct StepLabel set; duplicate = group.Count(label) > 1 for any label
```
**Field-mapping caveats (HIGH — verified in `EsIndexNames.cs`):**
- Capital-cased scope keys live under lowercase `attributes` (`attributes.CorrelationId`, `attributes.StepLabel`, `attributes.Sum`). The .NET MEL `BeginScope`/template param name is preserved verbatim.
- The data stream's ECS index template maps every string attribute DIRECTLY to `keyword` (`all_strings_to_keywords`, `ignore_above: 1024`) — there is **NO `.keyword` sub-field**. `term`/`exists` queries must target `attributes.StepLabel` directly. Querying `attributes.StepLabel.keyword` returns zero hits (this exact mistake broke 4 facts at Phase 11 UAT and was reverted — `EsIndexNames.cs:62-69`).
- `{Sum}` is logged as an integer template param; under otel mapping it may surface as a numeric or string attribute — read defensively (`TryGetInt32` then `GetString`+parse), mirroring `PrometheusTestClient.HasNumericValue`.

### Pattern 2: Prometheus counter read (OBS-03) — reuse `PrometheusTestClient`
**What:** Instant-vector queries for each counter; sum sample values across replica/instance labels.
**When to use:** OBS-03 reconciliation.
**Example (Prom-form names — verified, collector appends `_total`):**
```csharp
// Source: MetricsRoundTripE2ETests.cs:123-140 (worked example) + OrchestratorMetrics/ProcessorMetrics
var dispatchSent   = PrometheusTestClient.SumSampleValues(
    await prom.QueryPrometheus("orchestrator_dispatch_sent_total"));
var resultConsumed = PrometheusTestClient.SumSampleValues(
    await prom.QueryPrometheus("orchestrator_result_consumed_total"));
var consumed       = PrometheusTestClient.SumSampleValues(
    await prom.QueryPrometheus("processor_dispatch_consumed_total"));
var completed      = PrometheusTestClient.SumSampleValues(
    await prom.QueryPrometheus("processor_result_sent_total{outcome=\"completed\"}"));
var keeperDropped  = PrometheusTestClient.SumSampleValues(
    await prom.QueryPrometheus("keeper_reinject_dropped_total"));
// dedupe counters: query but expect EMPTY (dormant — see Counter Reality table)
```
**Caveat (HIGH):** counters are **monotonic process-lifetime cumulative** — they are NOT scoped to the observation window. For a per-run reconciliation, the harness must either (a) restart counter-emitting services between scenarios (resets to 0), or (b) the analyzer snapshots `dispatch_sent` BEFORE the window and subtracts (delta reconciliation). The cleaner approach given the Phase 65 reset does `docker exec ... FLUSHALL` + heal but does NOT restart counter processes: **delta reconciliation** (before/after snapshot) OR `increase(metric[window])` PromQL. Flag this for the planner — D-08's "reconcile against trigger count" is only meaningful on a windowed delta, not the lifetime cumulative.

### Pattern 3: Write-then-assert report (D-02 / D-09)
**What:** Serialize + write the JSON report FIRST, then xUnit-assert the verdict, so the artifact exists on a red run.
```csharp
var report = engine.Analyze(trace, promSnapshot, triggerCount, scenarioId);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOpts), ct);  // FIRST
// optional: human-readable .md/.txt alongside
Assert.True(report.Verdict == Verdict.Pass, report.HumanSummary);                          // THEN
```

### Anti-Patterns to Avoid
- **Querying `attributes.*.keyword`** — no sub-field exists; returns zero hits (reverted regression, `EsIndexNames.cs`).
- **Treating lifetime-cumulative Prom counters as window-scoped** — must delta or `increase(...)`.
- **Relying on a per-fire correlationId ES log for the denominator** — it does not exist (item #1).
- **Classifying duplicates as redelivery via dedupe counters** — those counters are dormant (item #2); a duplicate must fail-closed.
- **A single immediate Prom query** — pull-based 15s scrape + up to ~60s SDK export; poll with a ≥120s budget after drain (`MetricsRoundTripE2ETests` uses 120s).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| ES `_search` polling + backoff + 404/empty tolerance | A new HTTP loop | Extend `ElasticsearchTestClient` (keep its backoff + Clone) | Encodes verified field shape + lazy-index tolerance. |
| PromQL escaping + sample summation + scrape-delay polling | Manual `HttpClient` + string concat | `PrometheusTestClient` verbatim | `Uri.EscapeDataString` + `SumSampleValues` + mandatory initial sleep already correct. |
| ES index/field name constants | Hardcoded strings | `EsIndexNames` consts | The `.keyword` trap is documented + the index name is Wave-0-verified. |
| RealStack host wiring + net-zero teardown | New factory from scratch | Mirror `MetricsRoundTripE2ETests.RealStackWebAppFactory` | Host-override ctor + `L2KeysToCleanup`/`ParentIndexMembersToSrem` + composite-key sweep already proven. |
| JSON (de)serialization | Manual string building | `System.Text.Json` | Already the project standard. |

**Key insight:** The contract-bearing knowledge (ES field shape, the `.keyword` trap, Prom naming `_total`, scrape-delay) is already encoded in the three helper clients. The analyzer's net-new code is purely the **aggregation + decision logic** (`PassFailEngine`) and the **multi-hit ES extension** — everything else is reuse. This is the codebase's anti-desync discipline (Phase 21/63/65): contract knowledge lives in one C# place that already proves it.

## Runtime State Inventory

> This is a read-only analyzer phase — it adds no product code and renames nothing. No stored-data, service-config, OS-registration, secret, or build-artifact changes are introduced.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None — analyzer only READS ES + Prom; writes only its own report file. | None. |
| Live service config | None — no new logs/metrics/collector config. | None. |
| OS-registered state | None. | None. |
| Secrets/env vars | None new — reuses existing host-override env pattern (RMQ 5673 / Redis 6380 / Postgres 5433 / otel 4317) at fixture scope only. | None. |
| Build artifacts | None — no new project; new test files under existing `tests/BaseApi.Tests`. | Standard `dotnet build`. |

## Counter Reality (CRITICAL — verified by source trace)

The CONTEXT counter list (and the `*Metrics.cs` docstrings) reference dedupe gates that **no longer have an increment site** post Phase 43/51. The analyzer must NOT expect live dedupe series.

| Counter (Prom-form) | Increment site in `src/` | Live series? | Source |
|---------------------|--------------------------|--------------|--------|
| `orchestrator_dispatch_sent_total{ProcessorId}` | `StepDispatcher.cs:41` (after Send) | **YES** | [VERIFIED: grep `.Add(1`] |
| `orchestrator_result_consumed_total{ProcessorId}` | `TypedResultConsumer.cs:63` (top of Consume) | **YES** | [VERIFIED] |
| `orchestrator_result_deduped_total` | **NONE** (gate retired when TypedResultConsumer replaced ResultConsumer) | **NO — dormant** | [VERIFIED: docstring `OrchestratorMetrics.cs:36-42` + no `.Add`] |
| `processor_dispatch_consumed_total{ProcessorId}` | `EntryStepDispatchConsumer.cs:37` (top of Consume) | **YES** | [VERIFIED] |
| `processor_result_sent_total{ProcessorId,outcome}` | `ProcessorPipeline.cs:335` (after Send) | **YES**; outcome ∈ {completed,failed,cancelled,processing} via `ResultOutcome` (`ProcessorPipeline.cs:342-349`) | [VERIFIED] |
| `processor_dispatch_deduped_total` | **NONE** (docstring claims `flag[H]=="Ack"` gate in EntryStepDispatchConsumer — that machinery was retired in Phase 43; the consumer has no dedupe increment) | **NO — dormant** | [VERIFIED: `ProcessorMetrics.cs:36-40` docstring stale; no `.Add` anywhere] |
| `keeper_reinject_dropped_total` | `ReinjectConsumer.cs:38` | **YES** (only on by-design absent-data REINJECT drop) | [VERIFIED] |

**Implications for D-06 / D-08:**
- **D-06 collapses to "any duplicate = FAIL"** (its own contingency clause): there is no `processor_dispatch_deduped` / `orchestrator_result_deduped` series to corroborate a redelivery, so an un-corroboratable duplicate is the only possible kind → fail-closed (D-07). The analyzer should still QUERY the dormant counters (so the report shows "0 / absent" explicitly) but must not gate PASS on their presence.
- **D-08 reconciliation** uses the LIVE set: `dispatch_sent`, `result_consumed`, `dispatch_consumed`, `result_sent{outcome}`, `keeper_reinject_dropped`. The dormant counters appear in the report as `absent` and feed no arithmetic.
- **Dedup actually happens** at the processor's `exists L2[messageId]` gate (RECOVERY pass) — but it is **uncounted**. So a legitimate broker redelivery during a fault produces NO new `Step_*` log AND NO dedupe-counter tick — it is invisible to both ES and Prom. This is consistent with the pass bar ("message-level redelivery is reported, not failed"): the redelivery simply leaves zero footprint, so the run still shows exactly-one `Step_*` per label → PASS. The analyzer reports redelivery as "inferred from a `dispatch_sent` vs `result_consumed` delta that is NOT mirrored by extra `Step_*` logs," not from a dedupe counter.

## Common Pitfalls

### Pitfall 1: Expecting a per-fire correlationId in orchestrator ES logs (item #1)
**What goes wrong:** Building the trigger denominator by counting distinct correlationIds in an orchestrator dispatch log.
**Why it happens:** CONTEXT D-04 hypothesized such a log; the `WorkflowFireJob` *does* open a correlationId scope (`WorkflowFireJob.cs:63-67`).
**Reality:** The scope wraps only a happy-path dispatch loop that emits NO log; the only in-scope logs are business-skip WARNINGS (`WorkflowFireJob.cs:74`, entry step missing) which do not fire in the healthy proof. `StepDispatcher` emits no log. So the denominator MUST be `orchestrator_dispatch_sent_total` (Prom) + cadence.
**How to avoid:** Use Prom for the count; use the ES correlationId union only as the "observed" set, and accept that a fully-MISSING run (zero step logs) is detected as `dispatch_sent_count − observed_correlationId_count > 0`, not by naming the specific missing correlationId.
**Warning signs:** Trigger count == observed correlationId count always (you're counting the same ES source twice).

### Pitfall 2: The `.keyword` sub-field trap
**What goes wrong:** `term`/`exists` on `attributes.StepLabel.keyword` returns zero hits.
**Why it happens:** Stock ES 8.x dynamic mapping creates `.keyword` sub-fields; the ECS-managed data stream does not (maps strings directly to `keyword`).
**How to avoid:** Always target `attributes.StepLabel` / `attributes.CorrelationId` directly (`EsIndexNames.cs:62-69`).
**Warning signs:** Empty hits despite logs visibly present in ES.

### Pitfall 3: Treating cumulative Prom counters as window-scoped
**What goes wrong:** `dispatch_sent_total` includes every fire since process start, not just this scenario's window → reconciliation arithmetic is nonsense.
**How to avoid:** Snapshot-before-and-subtract, or `increase(metric[<window>])`. Confirm whether the Phase 65/67 reset restarts the orchestrator/processor (FLUSHALL does NOT). Flag for planner.
**Warning signs:** Counter values vastly exceed ~10× the per-window expectation.

### Pitfall 4: Snapshotting before the drain completes (D-05)
**What goes wrong:** In-flight runs at window close are scored MISSING (their later `Step_*` logs land after the snapshot).
**Why it happens:** ES indexing + OTel export latency + the 9-step traversal each add delay.
**How to avoid:** Bounded drain > worst-case 9-step traversal. **Recommended drain ≥ 30s** (the worked `MetricsRoundTripE2ETests` budgets 120s for a *single* round-trip end-to-end including cron alignment; the steady-state per-run traversal once the chain is warm is far shorter, but ES/Prom export adds ~15-75s). A conservative **45-60s drain** before snapshot, then poll ES until the hit count stabilizes across two consecutive polls (~5s apart). The harness (Phase 67/68) owns the wall-clock; the analyzer should also self-guard by polling-to-stable.
**Warning signs:** MISSING count tracks how fast you ran the analyzer (timing-dependent verdict).

### Pitfall 5: Prometheus scrape delay on the snapshot read
**What goes wrong:** A single immediate Prom query misses the latest samples (15s scrape + up to ~60s SDK export).
**How to avoid:** Use `PollPromForQuery` with a ≥120s budget (mirror `MetricsRoundTripE2ETests`), or place the Prom read after the ES drain (by then the scrape has caught up).

### Pitfall 6: Full compose stack precondition
**What goes wrong:** RealStack fixture passes/fails spuriously if `prometheus`, `elasticsearch`, `orchestrator`, or `processor-sample` aren't up healthy.
**How to avoid:** Same precondition as `MetricsRoundTripE2ETests` (Pitfall 6 there); the Phase 65 bring-up (`scripts/phase-65-up.ps1`) is the gate. Tag `[Trait("Category","RealStack")]` so hermetic filters exclude it.

## Code Examples

### Extend ElasticsearchTestClient — SearchAllHits (item #3)
```csharp
// Source: derived from existing PollEsForLog (ElasticsearchTestClient.cs:62-109) — same
// BaseAddress, same Clone() detach discipline, returns ALL hits instead of hits[0].
public async Task<List<JsonElement>> SearchAllHits(
    string queryBody, string? indexPath = null, CancellationToken ct = default)
{
    indexPath ??= EsIndexNames.LogsDataStream;
    using var req = new HttpRequestMessage(HttpMethod.Post, $"{indexPath}/_search")
    { Content = new StringContent(queryBody, Encoding.UTF8, "application/json") };
    using var resp = await _es.SendAsync(req, ct);
    var results = new List<JsonElement>();
    if (!resp.IsSuccessStatusCode) return results;   // 404 lazy-index tolerance (caller polls-to-stable)
    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    if (doc.RootElement.TryGetProperty("hits", out var outer)
        && outer.TryGetProperty("hits", out var hits)
        && hits.ValueKind == JsonValueKind.Array)
    {
        foreach (var h in hits.EnumerateArray())
        {
            using var inner = JsonDocument.Parse(h.GetRawText());
            results.Add(inner.RootElement.Clone());   // detach — safe to retain
        }
    }
    return results;
}
```

### PassFailEngine decision skeleton (plain testable object)
```csharp
// 9-label set (Phase 65 SPEC, verified FanOutSeederE2ETests.cs:66,80)
private static readonly HashSet<string> AllLabels = new()
{ "Step_A","Step_B","Step_C","Step_D1","Step_E1","Step_F1","Step_D2","Step_E2","Step_F2" };

public AnalyzerReport Analyze(IReadOnlyList<RunTrace> runs, PromCounterSnapshot prom,
                              int triggerCount, string scenarioId)
{
    var complete  = runs.Where(r => r.DistinctLabels.SetEquals(AllLabels)).ToList();
    var missing   = triggerCount - complete.Count;                       // OBS-02 MISSING
    var duplicates = runs.Where(r => r.HasAnyDuplicateLabel).ToList();   // OBS-02 DUPLICATE
    // item #2: no dedupe series exists → any duplicate is unaccountable → FAIL (D-06 contingency + D-07)
    var dupFail   = duplicates.Count > 0;
    // D-08 reconciliation on LIVE counters (windowed delta)
    var reconciled = prom.DispatchSentDelta == triggerCount
                  && prom.ResultSentCompletedDelta >= complete.Count * AllLabels.Count
                  && prom.UnaccountedDelta == 0;                          // fail-closed if not
    var pass = missing == 0 && !dupFail && reconciled;                    // zero-missing + effect-once + reconcile
    return new AnalyzerReport(scenarioId, pass ? Verdict.Pass : Verdict.Fail, /* ...trace, lists, counters... */);
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Effect-first dedup via `flag[H]=="Ack"` gate (Phase 32) incrementing `processor_dispatch_deduped` / `orchestrator_result_deduped` | `exists L2[messageId]` slot-array gate (Phase 51); typed dedup-free result consumer (Phase 24.1/43) | Phase 43/51 | The dedupe COUNTERS are dormant (no increment site). `*Metrics.cs` docstrings are stale. Analyzer must not depend on dedupe series. |
| `PollEsForLog` returns `hits[0]` only | (this phase) `SearchAllHits` returns all hits | Phase 66 | Enables per-correlationId multi-hit aggregation. |

**Deprecated/outdated (in-repo docs, not code):**
- `ProcessorMetrics.cs:36-40` docstring ("incremented at the existing `flag[H]=="Ack"` drop gate in EntryStepDispatchConsumer") — STALE; that gate was retired. The counter still exists as a meter slot but never increments.
- `OrchestratorMetrics.cs:36-42` already correctly notes `orchestrator_result_deduped` is "RETAINED-BUT-DORMANT … NO increment site." `processor_dispatch_deduped` has the same status but its docstring was not updated.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The `Step_*` log surfaces `Sum` as a queryable `attributes.Sum` field (numeric or string). | Pattern 1 | Low — the sum is a debugging aid, not a completeness gate; completeness keys on `StepLabel` only. Read defensively. |
| A2 | `@timestamp` is the window-range field on the otel data stream (vs `observedTimestamp`/`timestamp`). | Pattern 1 query | Medium — wrong field name → window filter returns 0 or all hits. **Confirm via a Wave-0 probe** (`GET logs-generic.otel-default/_mapping`) before locking the query; the existing facts filter by correlationId not time, so this is unverified for this stack. |
| A3 | The Phase 65/67 reset does NOT restart counter-emitting processes (FLUSHALL + heal only), so Prom counters are lifetime-cumulative across scenarios and need delta/`increase()`. | Pattern 2 caveat / Pitfall 3 | High for reconciliation correctness — verify against `scripts/phase-65-reset.ps1` (Phase 65 D-05/D-06: no `docker compose down`, no restart). If the harness DOES restart services per scenario, raw cumulative reads suffice. |
| A4 | A 45-60s drain + poll-to-stable is sufficient for the 9-step traversal + ES/Prom export at steady state. | Pitfall 4 | Medium — too short → spurious MISSING. Mitigated by poll-to-stable (count unchanged across two polls). Tune against an observed TEST-01 happy-path run. |

## Open Questions (RESOLVED)

1. **Counter windowing strategy (delta-snapshot vs `increase()` vs per-scenario restart).**
   - What we know: counters are lifetime-cumulative; Phase 65 reset is FLUSHALL+heal (no restart).
   - What's unclear: whether Phase 67's fault injection restarts services (TEST-02..07 crash a tier — a crashed+restarted processor RESETS its counters mid-window, breaking both delta-snapshot and `increase()` continuity).
   - Recommendation: prefer **ES-primary completeness** (counter-independent) as the binding verdict; treat Prom reconciliation as corroborating evidence that itself tolerates a counter reset (e.g. reconcile direction/sign, not exact equality, when a restart is known). Resolve concretely when planning against the Phase 67 fault model.

2. **Exact ES timestamp field for the window range (A2).**
   - What we know: existing facts filter by correlationId, never by time.
   - What's unclear: `@timestamp` vs alternative under otel mapping.
   - Recommendation: a one-line Wave-0 `_mapping` probe in the first analyzer task; bake the verified field into the query template (mirror how `EsIndexNames` baked the index name).

3. **Does a MISSING run (processor down at fire, TEST-02) leave ANY ES footprint?**
   - What we know: no orchestrator happy-path log; `processor_dispatch_consumed` only ticks if the processor consumed.
   - What's unclear: whether `orchestrator_dispatch_sent` ticks even when the processor is down (it does — Send to the queue succeeds; the message just sits unconsumed). So `dispatch_sent − dispatch_consumed > 0` is the Prom signal for a MISSING run; ES shows zero `Step_*` for that (un-nameable) correlationId.
   - Recommendation: MISSING detection = `dispatch_sent_count − complete_run_count`; the specific missing correlationId is NOT recoverable from telemetry (document this limitation in the report, per the fallback D-04 accepted).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Elasticsearch :9200 | OBS-01/02 ES reads | ✓ (compose `elasticsearch`) | 8.15.5 (per `EsIndexNames` Wave-0 note) | None — hard dependency. |
| Prometheus :9090 | OBS-03 counter reads | ✓ (compose `prometheus`) | — (scrapes collector exporter) | None — hard dependency. |
| otel-collector | log/metric export pipeline | ✓ (compose `otel-collector`) | 0.152.0 (per `EsIndexNames` note) | None. |
| orchestrator / processor-sample / keeper | produce the telemetry under analysis | ✓ (compose) | v8.0.0 | None — proof requires them up healthy. |
| `dotnet test` | run the fixture | ✓ (project build) | .NET 8 | None. |

**Missing dependencies with no fallback:** None at research time — the full stack is the documented precondition (same as `MetricsRoundTripE2ETests`). The fixture must fail LOUD (not silently pass) if any backend is unreachable.

## Validation Architecture

> Nyquist validation is ENABLED. The analyzer is itself a verification artifact, so the validation question is: **how do we prove each decision branch of the analyzer's correctness logic is actually exercised** (so a passing analyzer is trustworthy, not vacuously green)?

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (v2, `TestContext.Current.CancellationToken` in use) |
| Config file | none separate — standard `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~PassFailEngine"` (hermetic unit facts — no stack) |
| Full suite command | `dotnet test tests/BaseApi.Tests --filter "Category!=RealStack"` (hermetic) / `--filter "Category=RealStack&FullyQualifiedName~Analyzer"` (live) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| OBS-01 | per-correlationId trace aggregates all 9 labels → COMPLETE | unit (hermetic, fed synthetic hits) | `dotnet test --filter "FullyQualifiedName~PassFailEngine.Complete"` | ❌ Wave 0 |
| OBS-02 | a missing label → MISSING; a duplicate label → FAIL (fail-closed) | unit | `... ~PassFailEngine.Missing` / `... ~PassFailEngine.Duplicate` | ❌ Wave 0 |
| OBS-03 | Prom counter reconciliation: balanced→pass, unaccounted delta→FAIL | unit (fed synthetic `PromCounterSnapshot`) | `... ~PassFailEngine.Reconcile` | ❌ Wave 0 |
| OBS-04 | report JSON written BEFORE assert; verdict matches engine | integration (write-then-assert) + RealStack | `... ~Analyzer` | ❌ Wave 0 |
| item #3 | `SearchAllHits` returns N hits, groups correctly | unit against a captured ES `_search` JSON fixture | `... ~ElasticsearchTestClient.SearchAllHits` | ❌ Wave 0 |

### Sampling Rate (observable signals proving each branch fires)
Each `PassFailEngine` decision branch must be driven by a **synthetic input fixture** so the branch is provably exercised hermetically (no live stack needed to prove the LOGIC):
- **COMPLETE branch:** feed a run with all 9 labels → assert PASS contribution. Signal: `report.CompleteRuns == 1`.
- **MISSING branch:** feed a run missing `Step_F2` → assert `report.Missing >= 1` and `Verdict.Fail`. Signal: the missing label appears in `report.MissingDetail`.
- **DUPLICATE/fail-closed branch:** feed a run with two `Step_C` hits → assert `Verdict.Fail` with reason "unaccountable duplicate". Signal: `report.Duplicates` non-empty AND verdict Fail (proves item #2 fail-closed wiring).
- **RECONCILE-FAIL branch:** feed a `PromCounterSnapshot` with `dispatch_sent=10, complete=10` but `result_sent_completed` short by one → assert `Verdict.Fail` reason "unreconciled". Signal: `report.Reconciliation == Unreconciled`.
- **RECONCILE-PASS branch:** balanced snapshot + all-complete runs → `Verdict.Pass`. Signal: `report.Verdict == Pass`.
- **Window/drain branch (RealStack only):** TEST-01 happy-path live run → `Verdict.Pass` with `Missing == 0`. Signal: a real green over the live stack.

The **per-test-commit sample** is the hermetic `PassFailEngine` + `SearchAllHits` unit facts (sub-second, no stack). The **per-wave-merge sample** is the full hermetic suite (`Category!=RealStack`). The **phase gate** is one RealStack `Analyzer` happy-path run green against the live stack before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` — covers OBS-01/02/03 decision branches (synthetic inputs).
- [ ] `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClientFacts.cs` (or extend existing) — covers `SearchAllHits` grouping against a captured `_search` JSON fixture.
- [ ] ES `_mapping` Wave-0 probe to confirm the window timestamp field (A2) and `Sum` attribute type (A1) — single throwaway task, bake result into the query template + an `EsIndexNames` const.
- [ ] Confirm Prom counter windowing strategy vs the Phase 65/67 reset/restart behavior (A3) before locking reconciliation arithmetic.

## Security Domain

> `security_enforcement` status not found as explicit `false` in config — included for completeness. This phase is a read-only test fixture with a tightly bounded threat surface.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Local test fixture against localhost backends; no auth surface introduced. |
| V3 Session Management | no | — |
| V4 Access Control | no | Read-only; no new endpoints. |
| V5 Input Validation | yes | The analyzer parses untrusted-shaped ES/Prom JSON — parse defensively (`TryGetProperty`, no assumptions on field presence/type), mirroring existing `PrometheusTestClient.HasNumericValue`. PromQL parameter interpolation must use `Uri.EscapeDataString` (already in `PrometheusTestClient`); ES query bodies use static raw-string templates with only validated GUIDs/timestamps interpolated (T-18-04 pattern, mirror `OrchestrationLogsE2ETests.cs:195`). |
| V6 Cryptography | no | — |

### Known Threat Patterns for {C# test fixture reading ES/Prom}
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| PromQL/ES query injection via interpolated values | Tampering | `Uri.EscapeDataString` (Prom, already present); static templates + validated-GUID interpolation only (ES). |
| Report path traversal (scenario-id in file path) | Tampering | Validate scenario id against an allowlist/regex (`^[A-Za-z0-9_-]+$`) before composing the report path; write under a fixed reports dir. |
| Untrusted JSON shape causing unhandled exception (false RED) | DoS (self-inflicted) | Defensive parsing; missing/odd fields → recorded in report, not thrown. |

## Sources

### Primary (HIGH confidence — direct source trace, this session)
- `src/Orchestrator/Scheduling/WorkflowFireJob.cs` — per-fire correlationId mint + scope; NO happy-path log (item #1).
- `src/Orchestrator/Dispatch/StepDispatcher.cs` — `orchestrator_dispatch_sent` after Send; no log.
- `src/Orchestrator/Consumers/TypedResultConsumer.cs` — `orchestrator_result_consumed`; dedup-free.
- `src/Orchestrator/Observability/OrchestratorMetrics.cs` — counter names; `result_deduped` dormant.
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — `exists L2[messageId]` dedup gate BEFORE ProcessAsync (item #2); `processor_result_sent{outcome}` + `ResultOutcome` map.
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` — `processor_dispatch_consumed`; NO dedupe increment.
- `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` — counter names; `dispatch_deduped` dormant (stale docstring).
- `src/Processor.Sample/SampleProcessor.cs:39` — the `Step_*` log template (`"step completed {StepLabel} sum {Sum}"`).
- `src/BaseConsole.Core/Messaging/Inbound{Correlation,ExecutionScope}ConsumeFilter.cs` — scope-open only, no message.
- `src/Keeper/Observability/KeeperMetrics.cs` + `Recovery/ReinjectConsumer.cs:38` — `keeper_reinject_dropped` live.
- `tests/BaseApi.Tests/Observability/Helpers/{ElasticsearchTestClient,PrometheusTestClient,EsIndexNames}.cs` — reuse surface + `.keyword` trap + field shape.
- `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` — worked Prom-counter + RealStack factory example.
- `tests/BaseApi.Tests/Observability/OrchestrationLogsE2ETests.cs` — worked ES log-readback example + query template.
- `tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs:66,80` — 9-label set + edge set.
- `.planning/REQUIREMENTS.md` (OBS-01..04, TEST pass bar), `64-CONTEXT.md`, `65-CONTEXT.md`, `66-CONTEXT.md`.

### Secondary (MEDIUM)
- `*Metrics.cs` docstrings (Phase 32 dedup gate) — partially STALE; corrected against actual increment sites by grep.

### Tertiary (LOW)
- None — all claims verified by source or in-repo doc.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all reuse assets read in full this session.
- Architecture / item #1 (denominator): HIGH — exhaustively traced; no orchestrator happy-path log exists.
- Architecture / item #2 (dedup gate position + dormant counters): HIGH — gate + ProcessAsync call order + zero increment sites all verified by source + grep.
- Architecture / item #3 (ES aggregation): HIGH — volume bounded, field shape verified; only the timestamp field (A2) needs a Wave-0 probe.
- Pitfalls: HIGH — drawn from the actual `EsIndexNames`/`PrometheusTestClient` documented traps + counter reality.
- Counter windowing (A3) / drain duration (A4): MEDIUM — depend on the Phase 67 fault/reset model, flagged as open questions.

**Research date:** 2026-06-14
**Valid until:** 2026-07-14 (stable — internal codebase; revalidate if Phases 64/65 telemetry shape changes or the otel-collector/ES version bumps).
