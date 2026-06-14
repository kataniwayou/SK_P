# Phase 66: Prometheus + ES Analyzer & PASS/FAIL Engine - Pattern Map

**Mapped:** 2026-06-14
**Files analyzed:** 7 (1 modified, 6 new)
**Analogs found:** 7 / 7

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` (MODIFY — add `SearchAllHits`) | helper/client | request-response (ES `_search`) | self (extend `PollEsForLog`, lines 62-109) | exact |
| `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` (NEW) | test (RealStack fixture) | request-response + file-I/O | `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` | exact |
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` (NEW) | service (pure decision logic) | transform | none (net-new logic) — structure per RESEARCH skeleton | no-analog (logic); test-shape analog below |
| `tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs` (NEW) | model | transform | none (plain aggregate record) | no-analog |
| `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` (NEW) | model | transform (JSON serialize) | none — `System.Text.Json` record (see Shared) | no-analog |
| `tests/BaseApi.Tests/Observability/Analysis/PromCounterSnapshot.cs` (NEW) | model | transform | none (plain DTO) | no-analog |
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` (NEW) | test (hermetic unit) | request-response (synthetic input) | `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` | role-match |
| `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClientFacts.cs` (NEW) | test (hermetic unit) | transform (parse captured JSON) | `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` | role-match |

> RESEARCH §"Recommended Project Structure" places `RunTrace.cs`, `PassFailEngine.cs`, `AnalyzerReport.cs`, `PromCounterSnapshot.cs` under `Observability/Analysis/` and the fixture as `Observability/AnalyzerE2ETests.cs`. Keep `PassFailEngine` a plain object (no ES/Prom/host deps) so its branches are hermetically unit-testable.

---

## Pattern Assignments

### `ElasticsearchTestClient.cs` — add `SearchAllHits` (helper, request-response)

**Analog:** SELF — extend the existing `PollEsForLog` method (`ElasticsearchTestClient.cs:62-109`). Same `BaseAddress = http://localhost:9200/` (line 39), same POST `$"{indexPath}/_search"` shape (line 74), same `Clone()` detach discipline (lines 88-92). The only change: return **all** hits as `List<JsonElement>` instead of `hits[0]`.

**BaseAddress + ctor pattern to keep** (lines 37-42):
```csharp
public ElasticsearchTestClient()
{
    _es = new HttpClient { BaseAddress = new Uri("http://localhost:9200/") };
}
public void Dispose() => _es.Dispose();
```

**Hit-envelope navigation to reuse** (lines 83-92) — `hits.hits[]` array, then `Clone()` each element to detach from the `using var doc` parsing scope:
```csharp
if (doc.RootElement.TryGetProperty("hits", out var outer)
    && outer.TryGetProperty("hits", out var hits)
    && hits.ValueKind == JsonValueKind.Array
    && hits.GetArrayLength() > 0)
{
    using var inner = JsonDocument.Parse(hits[0].GetRawText());
    return inner.RootElement.Clone();   // ← SearchAllHits: loop EnumerateArray, Clone each, add to List
}
```

**Target shape** (RESEARCH §"Code Examples — SearchAllHits", lines 319-340): a single bounded `_search` (no backoff loop needed — caller polls-to-stable), `if (!resp.IsSuccessStatusCode) return results;` for 404 lazy-index tolerance, `foreach (var h in hits.EnumerateArray()) results.Add(... .Clone());`.

**Default index param** — `indexPath ??= EsIndexNames.LogsDataStream;` exactly as line 65.

---

### `AnalyzerE2ETests.cs` — RealStack analyzer fixture (test, request-response + file-I/O)

**Analog:** `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` (RealStack Prom-counter fixture). Secondary: `SampleRoundTripE2ETests` / `FanOutSeederE2ETests` (host-override + 9-label set + net-zero seeder).

**Trait + collection attributes** (`MetricsRoundTripE2ETests.cs:46-49`):
```csharp
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class AnalyzerE2ETests   // ← mirror
```
The `[Trait("Category","RealStack")]` makes the hermetic filter (`Category!=RealStack`) exclude it (D-01, RESEARCH Pitfall 6). `[Collection("Observability")]` is defined in `tests/BaseApi.Tests/Observability/CollectionDefinitions.cs`.

**Poll-timeout constants** (`MetricsRoundTripE2ETests.cs:53-61`) — copy the Prom budget verbatim; the drain budget (D-05) is net-new:
```csharp
private const int PromPollTimeoutMs = 120_000;   // pull-based: ~60s SDK export + 15s scrape (Pitfall 5)
// NEW for D-05: bounded drain ≥45-60s + poll-ES-to-stable (RESEARCH Pitfall 4)
```

**RealStack host-override factory** — copy `RealStackWebAppFactory` (`MetricsRoundTripE2ETests.cs:448-538`) almost verbatim. Critical: it derives from `Composition.Phase8WebAppFactory`, sets RMQ 5673 / Redis 6380 / Postgres 5433 / otel 4317 in the ctor, and **does NOT override 9090/9200** (clients hit the servers directly):
```csharp
private sealed class RealStackWebAppFactory : Composition.Phase8WebAppFactory
{
    public RealStackWebAppFactory()
        : base(skipPostgresFixture: true, connectionStringOverride: HostPostgres,
               skipRedisFixture: true, redisConnectionStringOverride: HostRedisFull)
    {
        Set("RabbitMq__Host", "localhost"); Set("RabbitMq__Port", "5673");
        Set("RabbitMq__Username", "guest"); Set("RabbitMq__Password", "guest");
        Set("ConnectionStrings__Redis", HostRedisFull);
        Set("ConnectionStrings__Postgres", HostPostgres);
        Set("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
    }
    private const string HostRedisFull = "localhost:6380,abortConnect=false,connectTimeout=5000";
    private const string HostPostgres  = "Host=localhost;Port=5433;Database=stepsdb;Username=postgres;Password=postgres;Maximum Pool Size=20;Timeout=15";
    // Set/Restore env-var swap (lines 482-494)
}
```

**Net-zero teardown discipline** (`MetricsRoundTripE2ETests.cs:507-536`) — the analyzer is **read-only**, so it mints NO new Redis state of its own (the live stack's cron mints `skp:*`, not the fixture). It must NOT register cleanup keys it didn't write. Keep the `DisposeAsync` → `Restore()` → `base.DisposeAsync()` env-restore tail (lines 535-536). The `L2KeysToCleanup` / `ParentIndexMembersToSrem` / composite-key sweep (lines 502-533) is the seeder pattern — the analyzer omits it unless it itself writes Redis (it does not). Its only write is the JSON report file.

**Prom counter read pattern** (`MetricsRoundTripE2ETests.cs:120-147`) — construct `using var prom = new PrometheusTestClient();` then `PollPromForQuery(promQL, VectorNonEmpty, PromPollTimeoutMs, ct)`. The analyzer uses `QueryPrometheus` + `SumSampleValues` for the OBS-03 counter set (see Shared Pattern "Prometheus counter read"). **Caveat (RESEARCH Pitfall 3 / A3):** counters are lifetime-cumulative — use windowed delta (snapshot-before-subtract) or `increase(metric[window])`, NOT raw totals.

**Write-then-assert ordering** (D-02, RESEARCH §"Pattern 3", lines 222-227) — net-new IO; serialize + write the report FIRST, then `Assert.True`:
```csharp
var report = engine.Analyze(traces, promSnapshot, triggerCount, scenarioId);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOpts), ct);  // FIRST
Assert.True(report.Verdict == Verdict.Pass, report.HumanSummary);                          // THEN
```

**ES query template** (RealStack ES readback) — `OrchestrationLogsE2ETests.cs:195-200` is the canonical raw-string `_search` body using the `EsIndexNames` const (NOT a `.keyword` sub-field). Adapt the `term` filter to the `Step_*` family `exists` + window range:
```csharp
var queryBody = $$"""
  {
    "size": 10,
    "query": { "term": { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{corrId}}" } }
  }
  """;
```
Analyzer variant (RESEARCH §"Pattern 1", lines 171-186): `{ "exists": { "field": "attributes.StepLabel" } }` + `{ "range": { "@timestamp": {...} } }`, `"size": 2000`, `"sort": [{ "@timestamp": "asc" }]`. **Confirm `@timestamp` via a Wave-0 `_mapping` probe (A2) before locking the query.**

---

### `PassFailEngine.cs` (service, transform) + `RunTrace.cs` / `AnalyzerReport.cs` / `PromCounterSnapshot.cs` (models)

**Analog:** None for the logic (net-new correctness engine). Structure follows RESEARCH §"PassFailEngine decision skeleton" (lines 343-364) and §"Recommended Project Structure" (lines 152-165). Plain objects — no ES/Prom/host deps.

**9-label completeness set** — copy from `FanOutSeederE2ETests.cs:64-75` (`NodeNumbers` keys) / `:91` (`AllNodeLabels`). RESEARCH skeleton (line 346):
```csharp
private static readonly HashSet<string> AllLabels = new()
{ "Step_A","Step_B","Step_C","Step_D1","Step_E1","Step_F1","Step_D2","Step_E2","Step_F2" };
```

**The parse target** — the per-step COMPLETED signal is `SampleProcessor.cs:39`:
```csharp
logger.LogInformation("step completed {StepLabel} sum {Sum}", label, sum);
```
surfacing as ES `attributes.StepLabel` + `attributes.Sum` (D-03). `RunTrace` groups hits by `attributes.CorrelationId`; `DistinctLabels.SetEquals(AllLabels)` ⇒ COMPLETE.

**Decision rules baked in** (RESEARCH §Summary item #2 + Counter Reality, lines 260-277):
- MISSING = `triggerCount − completeRuns.Count` (denominator from Prom `orchestrator_dispatch_sent_total` + cadence — NOT from a per-fire ES log, which does not exist; item #1).
- Any duplicate `(correlationId, StepLabel)` ⇒ **FAIL** (dedupe counters are dormant, no corroboration possible; D-06 collapses to fail-closed per D-07).
- Reconcile only the LIVE counter set; dormant counters reported as `absent`, feed no arithmetic.

**`AnalyzerReport` JSON** — `System.Text.Json` record; see Shared Pattern "JSON serialization".

---

### `PassFailEngineFacts.cs` + `ElasticsearchTestClientFacts.cs` (test, hermetic unit)

**Analog:** `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` — the project's canonical hermetic-unit `*Facts` shape (no live stack, synthetic inputs, `sealed class`, xUnit `[Fact]`).

**Class + doc-comment shape** (`SampleProcessorFacts.cs:29`): `public sealed class PassFailEngineFacts` with a summary mapping facts → req IDs (OBS-01/02/03), mirroring the SampleProcessorFacts header (lines 11-28).

**Synthetic-input branch coverage** — RESEARCH §"Sampling Rate" (lines 436-443) enumerates the exact branches each fact must drive: COMPLETE, MISSING (drop `Step_F2`), DUPLICATE/fail-closed (two `Step_C`), RECONCILE-FAIL, RECONCILE-PASS. Feed `PassFailEngine.Analyze(...)` synthetic `RunTrace`/`PromCounterSnapshot` objects and assert on `report.Verdict` + detail lists.

**Captured-JSON fixture pattern** — `ElasticsearchTestClientFacts.cs` parses a captured `_search` response JSON (RESEARCH Test Map, line 434) and asserts `SearchAllHits` returns N hits grouped correctly by `attributes.CorrelationId` + `attributes.StepLabel`. Mirror the `CapturingLogger` self-contained-fixture style (`SampleProcessorFacts.cs:31-40`) — keep all fixture data inline/local, no stack.

**Quick-run command** (RESEARCH line 424): `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~PassFailEngine"` (sub-second, hermetic).

---

## Shared Patterns

### Prometheus counter read (OBS-03)
**Source:** `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` (reuse verbatim — `QueryPrometheus` :210, `SumSampleValues` :242, `PollPromForQuery` :76, `VectorNonEmpty` :123, `HasNumericValue` :136).
**Apply to:** `AnalyzerE2ETests` + (synthetically) `PassFailEngine` reconciliation.
**LIVE counter set** (RESEARCH Counter Reality table, lines 264-272 — all `_total`-suffixed):
```csharp
var dispatchSent = PrometheusTestClient.SumSampleValues(
    await prom.QueryPrometheus("orchestrator_dispatch_sent_total"));
var resultConsumed = PrometheusTestClient.SumSampleValues(
    await prom.QueryPrometheus("orchestrator_result_consumed_total"));
var consumed = PrometheusTestClient.SumSampleValues(
    await prom.QueryPrometheus("processor_dispatch_consumed_total"));
var completed = PrometheusTestClient.SumSampleValues(
    await prom.QueryPrometheus("processor_result_sent_total{outcome=\"completed\"}"));
var keeperDropped = PrometheusTestClient.SumSampleValues(
    await prom.QueryPrometheus("keeper_reinject_dropped_total"));
// DORMANT (query but expect EMPTY → report "absent", feed no arithmetic):
//   orchestrator_result_deduped_total, processor_dispatch_deduped_total
```
PromQL escaping (`Uri.EscapeDataString`) is already inside the client (`PrometheusTestClient.cs:87,214`) — never hand-roll the URL. Mandatory scrape-delay handling: prefer `PollPromForQuery` (≥120s budget) or read Prom AFTER the ES drain.

### ES index/field constants (the `.keyword` trap)
**Source:** `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` (reuse verbatim).
**Apply to:** every ES query in `AnalyzerE2ETests` + `SearchAllHits` default.
- `LogsDataStream = "logs-generic.otel-default"` (line 40), `CorrelationIdFieldPath = "attributes.CorrelationId"` (line 71), `FieldShape = "otel"` (line 87).
- **Query `attributes.StepLabel` / `attributes.CorrelationId` DIRECTLY — never `.keyword`** (lines 51-69: the `.keyword` sub-field does not exist on the ECS-managed data stream; broke 4 facts at Phase 11 UAT and was reverted). The analyzer's new field paths (`attributes.StepLabel`, `attributes.Sum`, `attributes.WorkflowId`, `attributes.StepId`, `attributes.ProcessorId`) follow the same direct shape; optionally add them as new `EsIndexNames` consts.

### `JsonElement` Clone detach discipline
**Source:** `ElasticsearchTestClient.cs:88-92` + `PrometheusTestClient.cs:97-100,230-233`.
**Apply to:** `SearchAllHits` and any retained ES/Prom element. Always `using var doc = JsonDocument.Parse(...)` then `.Clone()` before returning — the element is invalid once the parsing `doc` disposes.

### JSON report serialization (D-09)
**Source:** `System.Text.Json.JsonSerializer` — project standard (RESEARCH lines 82, 244). `File.WriteAllTextAsync` is the one net-new IO (no prior report writer; RESEARCH line 83).
**Apply to:** `AnalyzerReport` (record), written in `AnalyzerE2ETests` write-then-assert (D-02).
**Security (RESEARCH §Security V5, line 470):** validate scenario id against `^[A-Za-z0-9_-]+$` before composing the report path (path-traversal guard); write under a fixed reports dir.

### Defensive JSON parsing
**Source:** `PrometheusTestClient.HasNumericValue` (`:136-161`) — `TryGetProperty`, no field-presence assumptions, quoted-number tolerance.
**Apply to:** `RunTrace` aggregation + `PromCounterSnapshot`. `{Sum}` may surface numeric OR string under otel mapping — `TryGetInt32` then `GetString`+parse (RESEARCH line 198, A1).

---

## No Analog Found

These files have no per-file code analog — their **structure** comes from RESEARCH and their **test-shape** from `SampleProcessorFacts.cs`. The logic is net-new correctness code:

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `Analysis/PassFailEngine.cs` | service | transform | Net-new decision engine; no completeness/reconciliation analog exists in repo. Skeleton: RESEARCH lines 343-364. |
| `Analysis/RunTrace.cs` | model | transform | Net-new per-correlationId aggregate record. |
| `Analysis/AnalyzerReport.cs` | model | transform | Net-new JSON report record (D-09 schema is Claude's discretion). |
| `Analysis/PromCounterSnapshot.cs` | model | transform | Net-new counter-read DTO. |

The planner should use the RESEARCH §"Recommended Project Structure" + §"PassFailEngine decision skeleton" for these, NOT a fabricated repo analog.

---

## Metadata

**Analog search scope:** `tests/BaseApi.Tests/Observability/`, `tests/BaseApi.Tests/Orchestrator/`, `tests/BaseApi.Tests/Processor/`, `src/Processor.Sample/`.
**Files scanned:** 7 analog files read in full or in targeted ranges (ElasticsearchTestClient, PrometheusTestClient, EsIndexNames, MetricsRoundTripE2ETests, OrchestrationLogsE2ETests, FanOutSeederE2ETests, SampleProcessor, SampleProcessorFacts).
**Pattern extraction date:** 2026-06-14
