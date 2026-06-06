# Phase 39: Keeper Observability + Real-Stack E2E + Close Gate - Pattern Map

**Mapped:** 2026-06-06
**Files analyzed:** 6 (1 NEW source + 3 MODIFY source + 1 MODIFY test + 1 NEW script) + 2 Wave-0 gaps noted
**Analogs found:** 6 / 6 (every file has a strong in-repo analog; 2 instrument *types* have NO analog — flagged)

> This phase **instruments already-existing code** and **clones a script**. No new control flow. Every "new" file
> mirrors a verified analog; the only genuinely net-new shapes are the `UpDownCounter` and `Histogram`
> instruments (no in-repo precedent — see "No Analog Found").

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| **NEW** `src/Keeper/Observability/KeeperMetrics.cs` | observability/meter holder | event-driven (telemetry emit) | `src/Orchestrator/Observability/OrchestratorMetrics.cs` | exact (counters) / none (histogram+updowncounter) |
| **MODIFY** `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` | consumer (instrument injection) | event-driven / request-response | `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (increment-site + `OutcomeLabel`) | role+flow match |
| **MODIFY** `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` | consumer (instrument injection) | event-driven / request-response | its sibling `FaultEntryStepDispatchConsumer.cs` (identical map, `fault_type=result`) | exact |
| **MODIFY** `src/Keeper/Recovery/L2ProbeRecovery.cs` | service (probe loop) | batch/retry loop | self (in-place `++/finally--` + catch increment); shape ref `EntryStepDispatchConsumer` tag-add | in-place |
| **MODIFY** `src/Keeper/Program.cs` | config/composition root | DI registration | `src/Orchestrator/Program.cs:72-73` (`AddSingleton` + `AddMeter` symmetry) | exact |
| **MODIFY** `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` | test (E2E, extend 2 facts) | request-response / scrape-assert | `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` (scrape block) + `PrometheusTestClient` | role+flow match |
| **NEW** `scripts/phase-39-close.ps1` | gate script | batch/snapshot-compare | `scripts/phase-33-close.ps1` (triple-SHA clone source) | exact (clone + delta) |
| *(Wave 0)* `tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs` | test (hermetic meter assert) | event-driven | no exact analog — `MeterListener`/in-memory exporter (see RESEARCH Wave 0) | partial |

## Pattern Assignments

### `src/Keeper/Observability/KeeperMetrics.cs` (NEW — observability/meter holder)

**Analog:** `src/Orchestrator/Observability/OrchestratorMetrics.cs` (counters); `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` (second house example).

**Class shape + ctor pattern** (OrchestratorMetrics lines 22-46) — copy this exactly for the SIX counters:
```csharp
public sealed class OrchestratorMetrics
{
    public const string MeterName = "Orchestrator";        // ← KeeperMetrics: "Keeper"
    public Counter<long> DispatchSent { get; }

    public OrchestratorMetrics(IMeterFactory meterFactory)  // ← NEVER a static Meter (D-01 test-isolation invariant)
    {
        var meter = meterFactory.Create(MeterName);
        DispatchSent = meter.CreateCounter<long>("orchestrator_dispatch_sent"); // snake_case, NO _total (collector appends it)
    }
}
```
Only `using System.Diagnostics.Metrics;` is needed (OrchestratorMetrics.cs:1). The `sealed class` + `public const string MeterName` + per-instrument `{ get; }` property + `IMeterFactory` ctor is the locked house shape.

**KeeperMetrics counter set (D-02/D-03)** — six `Counter<long>`, instrument names snake_case, NO `_total`:
`keeper_fault_consumed`, `keeper_recovered`, `keeper_dlq_pushed`, `keeper_workflow_paused`, `keeper_workflow_resumed`, `keeper_l2_probe_failed`.

**Net-new instruments (NO analog — see "No Analog Found"):**
- `UpDownCounter<long> InFlight = meter.CreateUpDownCounter<long>("keeper_in_flight");` (D-05)
- `Histogram<double> RecoveryDuration = meter.CreateHistogram<double>("keeper_recovery_duration", unit: "s");` (D-04)
  with `{1,5,10,30,60,120}` second buckets — via `InstrumentAdvice<double>` if DiagnosticSource ≥ 9.0.0 (Wave-0 check) else OTel `AddView` route.

**Interned-label tag helper (Pattern 2 — copy `OutcomeLabel`)** from `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:262-269`:
```csharp
private static string OutcomeLabel(StepOutcome outcome) => outcome switch
{
    StepOutcome.Completed  => "completed",
    StepOutcome.Failed     => "failed",
    StepOutcome.Cancelled  => "cancelled",
    _                      => outcome.ToString().ToLowerInvariant(),
};
```
Apply the same pinned-literal `switch` shape for `fault_type ∈ {dispatch, result}`, `outcome ∈ {recovered, gave_up}`, `reason = "probe_exhausted"` (Claude's discretion: a small `KeeperMetricTags` static or inline consts + an optional shared tag-builder to keep the two consumers DRY).

**Tag-add idiom (Pattern 2 increment site)** from `EntryStepDispatchConsumer.cs:248-250`:
```csharp
metrics.ResultSent.Add(1,
    new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")),
    new KeyValuePair<string, object?>("outcome", OutcomeLabel(result.Outcome)));
```
> `ProcessorId` is `inner.ProcessorId.ToString("D")` in the Keeper consumers. The E2E asserts `ProcessorId="{guid:D}"`.

---

### `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` (MODIFY — consumer, increment-site injection)

**Analog:** itself (existing branch structure, verified below) + `EntryStepDispatchConsumer` tag-add idiom (above).

**Existing branch structure to thread increments into** (current file lines 30-77 — DO NOT change control flow):
```csharp
public sealed class FaultEntryStepDispatchConsumer(
    ILogger<FaultEntryStepDispatchConsumer> logger, L2ProbeRecovery recovery)   // ← ADD `KeeperMetrics metrics` 3rd primary-ctor param
    : IConsumer<Fault<EntryStepDispatch>>
{
    public async Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context)
    {
        var inner = context.Message.Message;        // ← keeper_fault_consumed{fault_type=dispatch, ProcessorId} HERE (top) + Stopwatch.StartNew()
        ...
        await context.Publish(new PauseWorkflow(...));   // ← keeper_workflow_paused{ProcessorId} AFTER this
        var outcome = await recovery.RunAsync(inner.EntryId, inner.H, context.CancellationToken);
        if (outcome == ProbeOutcome.Recovered)
        {
            var endpoint = await context.GetSendEndpoint(new Uri($"queue:{inner.ProcessorId:D}"));
            await endpoint.Send(inner, context.CancellationToken);
            await context.Publish(new ResumeWorkflow(...));   // ← keeper_workflow_resumed{ProcessorId} + keeper_recovered{fault_type,ProcessorId} HERE
                                                              //   + RecoveryDuration.Record(sw.Elapsed.TotalSeconds, outcome=recovered)
        }
        else
        {
            var dlq = await context.GetSendEndpoint(new Uri($"queue:{KeeperQueues.DeadLetter}"));
            await dlq.Send(context.Message, context.CancellationToken);   // ← keeper_dlq_pushed{reason=probe_exhausted,fault_type,ProcessorId} HERE
                                                                          //   + RecoveryDuration.Record(sw.Elapsed.TotalSeconds, outcome=gave_up)
        }
    }
}
```

**Increment-site map (D-03/D-04):**
| Site | Instrument | Tags |
|------|-----------|------|
| top of `Consume` (after `var inner = ...`) | `FaultConsumed.Add(1, ...)` + `Stopwatch.StartNew()` | `fault_type=dispatch, ProcessorId` |
| after `Publish(PauseWorkflow)` | `WorkflowPaused.Add(1, ...)` | `ProcessorId` (no `fault_type` — workflow-scoped) |
| Recovered branch (after `Publish(ResumeWorkflow)`) | `WorkflowResumed.Add(1)` + `Recovered.Add(1)` + `RecoveryDuration.Record(sw.Elapsed.TotalSeconds, outcome=recovered)` | resumed: `ProcessorId`; recovered: `fault_type=dispatch, ProcessorId` |
| else (GaveUp) branch (around `dlq.Send`) | `DlqPushed.Add(1)` + `RecoveryDuration.Record(sw.Elapsed.TotalSeconds, outcome=gave_up)` | `reason=probe_exhausted, fault_type=dispatch, ProcessorId` |

> **Pitfall (RESEARCH Pitfall 2):** record `sw.Elapsed.TotalSeconds`, NOT `ElapsedMilliseconds` — buckets are seconds.
> Ctor injection mirrors `ProcessorMetrics metrics` injection into `EntryStepDispatchConsumer` (primary-ctor param).

---

### `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` (MODIFY — consumer)

**Analog:** `FaultEntryStepDispatchConsumer.cs` — **byte-for-byte identical increment map**, only `fault_type=result` differs (and the recovered re-inject endpoint is `OrchestratorQueues.Result` not `queue:{procId:D}`; see current file lines 58-69). Apply the exact same four-site map above with `fault_type=result`. Add `KeeperMetrics metrics` to the primary ctor `(ILogger<...> logger, L2ProbeRecovery recovery)` (current line 31-32).

---

### `src/Keeper/Recovery/L2ProbeRecovery.cs` (MODIFY — service, probe loop)

**Analog:** self (in-place edit) + the `KeyValuePair` tag-add idiom from `EntryStepDispatchConsumer.cs:248-250`.

**Existing `RunAsync` to wrap** (current file lines 17-40):
```csharp
public sealed class L2ProbeRecovery(IConnectionMultiplexer redis, IOptions<ProbeOptions> opts)  // ← ADD `KeeperMetrics metrics` param
{
    public async Task<ProbeOutcome> RunAsync(string entryId, string h, CancellationToken ct)
    {                                                    // ← metrics.InFlight.Add(1, ...) HERE (entry, D-05)
        var db = redis.GetDatabase();                    // ← wrap whole body in try { ... } finally { metrics.InFlight.Add(-1, ...) }
        var max = opts.Value.MaxAttempts;
        for (var attempt = 0; attempt < max; attempt++)
        {
            try { /* READ skp:data + WRITE/DEL scratch */ return ProbeOutcome.Recovered; }
            catch (RedisException)                        // ← metrics.L2ProbeFailed.Add(1, ...) HERE (per catch)
            {
                if (attempt + 1 < max)
                    await Task.Delay(TimeSpan.FromSeconds(opts.Value.DelaySeconds), ct);
            }
        }
        return ProbeOutcome.GaveUp;
    }
}
```

**Increment-site map (D-05 + `keeper_l2_probe_failed`):**
| Site | Instrument | Tags |
|------|-----------|------|
| `RunAsync` entry | `InFlight.Add(1)` | unlabelled OR `ProcessorId` (see Open Question below) |
| `RunAsync` `finally` | `InFlight.Add(-1)` | same as entry |
| inside `catch (RedisException)` | `L2ProbeFailed.Add(1)` | `ProcessorId` (per D-03; needs threading — see below) |

> **OPEN QUESTION (RESEARCH OQ-1):** `RunAsync(entryId, h, ct)` has **no `ProcessorId` param** today. D-03 puts
> `ProcessorId` on `keeper_l2_probe_failed`. The planner must either (a) thread a `procId` param into `RunAsync`
> (both consumers already hold `inner.ProcessorId`) for the `ProcessorId` tag on `l2_probe_failed` + optionally
> `in_flight`, or (b) emit `keeper_in_flight` UNLABELLED. RESEARCH recommends threading `procId` for
> `l2_probe_failed` and treating `in_flight` as a best-effort/optional E2E series. `long` recommended over `int`.

---

### `src/Keeper/Program.cs` (MODIFY — composition root)

**Analog:** `src/Orchestrator/Program.cs:72-73` — the exact `AddSingleton` + `AddMeter` symmetry:
```csharp
builder.Services.AddSingleton<OrchestratorMetrics>();
builder.Services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(OrchestratorMetrics.MeterName));
```
**Apply in Keeper** alongside the existing `AddSingleton<L2ProbeRecovery>()` (current Keeper/Program.cs:31):
```csharp
builder.Services.AddSingleton<KeeperMetrics>();
builder.Services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(KeeperMetrics.MeterName));
```
> `builder.AddBaseConsoleObservability(...)` (Keeper/Program.cs:18) already built the shared metrics-only
> MeterProvider — `ConfigureOpenTelemetryMeterProvider` additively attaches the Keeper meter to it (no new OTel
> pipeline, no resource work). If the OTel `AddView` bucket route is chosen (Wave 0), the `AddView` call also
> goes here in the same `ConfigureOpenTelemetryMeterProvider` lambda.

---

### `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` (MODIFY — extend 2 facts)

**Analog:** `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs:120-152` (scrape block) +
`tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` (the client to REUSE).

**Scrape-assertion block to add to each fact** (mirror MetricsRoundTripE2ETests.cs:120-130 — series existence):
```csharp
using var prom = new PrometheusTestClient();
var consumed = await prom.PollPromForQuery(
    $"keeper_fault_consumed_total{{fault_type=\"dispatch\",ProcessorId=\"{procId:D}\"}}",
    PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
Assert.NotNull(consumed);
```

**Label-shape helper to reuse VERBATIM** (MetricsRoundTripE2ETests.cs:231-277) — `AssertBusinessLabels`,
`FirstMetricObject`, `TryGetNonEmpty`: assert non-empty `service_instance_id`, assert NO `workflowId` (the
cardinality ban, D-08). Copy the `foreach (var prop in metric.EnumerateObject()) Assert.False(...workflowId...)`
loop (lines 243-248).

**Add `private const int PromPollTimeoutMs = 120_000;`** to the test class (mirror MetricsRoundTripE2ETests.cs:61
— SDK 60s export + 15s scrape budget).

**Per-fact assertion targets (D-08, RESEARCH Code Examples):**
| Fact | Series to assert (Prom-suffixed names) |
|------|-----------------------------------------|
| `KeeperRecovery_RecoversBothPaths` (line 112) | `keeper_fault_consumed_total`, `keeper_recovered_total`, `keeper_workflow_paused_total`, `keeper_workflow_resumed_total`, `keeper_recovery_duration_seconds_count{outcome="recovered"}` |
| `KeeperRecovery_GivesUp_ParksToDlq` (line 266) | `keeper_dlq_pushed_total{reason="probe_exhausted",fault_type="result"}`, `keeper_recovery_duration_seconds_count{outcome="gave_up"}`, `keeper_l2_probe_failed_total` |

> **Pitfall 1 (RESEARCH #1, highest flake risk):** Prom suffixes — Counter→`_total`; Histogram (unit `"s"`)
> →`_seconds_count`/`_sum`/`_bucket`; UpDownCounter→bare gauge `keeper_in_flight`. **Pin the histogram unit
> decision first, then write queries to match.** Confirm `_seconds` vs bare on the first live scrape.
> The existing facts already capture `procId` (FACT 1 line 129, FACT 2 line 277) — interpolate `{procId:D}`.
> `keeper_in_flight` is transient (gauge → 0 after the loop): assert PRESENCE best-effort, not a value.

---

### `scripts/phase-39-close.ps1` (NEW — clone of `scripts/phase-33-close.ps1`)

**Analog:** `scripts/phase-33-close.ps1` (clone verbatim, apply 2 deltas).

**Clone these blocks VERBATIM** (line refs into phase-33-close.ps1):
- Pre-flight stable-Processor-row seed (lines 74-157): genuine embedded SourceHash reflection → GET-or-create via
  WebApi → wait-for-healthy. (Update the version string `'3.7.0'` on line 124 only if the milestone version moves.)
- BEFORE triple-SHA capture (lines 174-193): `psql -lqt` / `redis-cli --scan | Sort-Object -CaseSensitive` /
  `rabbitmqctl -q list_queues name | Sort-Object -CaseSensitive`, each SHA-256'd. **Keep `list_queues name`
  UNCHANGED** (Pitfall 4 — adding `messages` to the SHA input churns it every run).
- Zero-warning build gate BOTH configs (lines 195-208).
- 3×consecutive-GREEN cadence, full suite no filter, distinct-Passed==1 guard (lines 210-243).
- Settle-drain (lines 245-266) + AFTER triple-SHA (lines 268-286) + invariant assertions (lines 288-328).

**DELTA 1 — rebuild set:** add `'keeper'` to the compose service list (phase-33-close.ps1:161) and to the
CONTRACT-CHANGE rebuild note (lines 49-55). D-10: rebuild `baseapi-service orchestrator processor-sample keeper`
so the embedded SourceHash + the new `Keeper` meter are in the live images.
```powershell
$services = @('postgres','redis','rabbitmq','otel-collector','elasticsearch','prometheus',
              'orchestrator','processor-sample','baseapi-service','keeper')   # + keeper (DELTA 1)
```

**DELTA 2 — additive both-DLQ depth==0 assertion** (NEW block, after the RMQ name-SHA invariant, before the
final `if (-not $allGood)` — D-09). Separate from the name-SHA (Pitfall 4):
```powershell
$depthRaw = docker exec sk-rabbitmq rabbitmqctl -q list_queues name messages | Out-String
foreach ($q in @('keeper-dlq','skp-dlq-1')) {              # DLQ-2 (KeeperQueues.DeadLetter) + DLQ-1 (ConsolidatedErrorTransportFilter.Dlq1)
    $line  = ($depthRaw -split "`n" | Where-Object { $_ -match "^\s*$([regex]::Escape($q))\s+\d+\s*$" })
    $depth = if ($line -match "\s+(\d+)\s*$") { [int]$Matches[1] } else { -1 }
    if ($depth -ne 0) {
        Write-Host "DLQ depth invariant VIOLATED: $q depth=$depth (expected 0)" -ForegroundColor Red
        $allGood = $false
    }
}
```
> **Discrepancy 1 (CONFIRMED):** DLQ-1 is the single const `skp-dlq-1` = `ConsolidatedErrorTransportFilter.Dlq1`
> (`src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs:45`), NOT a `*_error` queue (Phase 36
> replaced the per-`{queue}_error` default). The gate targets `keeper-dlq` AND `skp-dlq-1`.
> The unfiltered redis `--scan` SHA already covers leaked `skp:keeper:probe:*` scratch keys — no scan delta needed.
> Net-zero DLQ drain stays in the E2E test teardown (D-10) — NO gate-side `purge_queue`.

## Shared Patterns

### IMeterFactory meter (never static Meter)
**Source:** `src/Orchestrator/Observability/OrchestratorMetrics.cs:22-46` (+ `ProcessorMetrics.cs:25-49`)
**Apply to:** `KeeperMetrics.cs`
```csharp
public const string MeterName = "Keeper";                       // single const ↔ AddMeter symmetry (D-01)
public KeeperMetrics(IMeterFactory meterFactory) {              // never `static Meter` (test-process leak)
    var meter = meterFactory.Create(MeterName);
    FaultConsumed = meter.CreateCounter<long>("keeper_fault_consumed");   // snake_case, NO _total
}
```

### Interned per-increment label (decouple Prom label from C# enum)
**Source:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:262-269` (`OutcomeLabel`)
**Apply to:** `KeeperMetrics` tag-builders for `fault_type`, `outcome`, `reason`
```csharp
private static string XLabel(SomeEnum e) => e switch { SomeEnum.A => "a", _ => e.ToString().ToLowerInvariant() };
```

### `KeyValuePair` tag-add at the increment site
**Source:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:248-250`
**Apply to:** every Keeper increment site (both consumers + probe loop)
```csharp
metrics.X.Add(1,
    new KeyValuePair<string, object?>("ProcessorId", inner.ProcessorId.ToString("D")),
    new KeyValuePair<string, object?>("fault_type", "dispatch"));   // ProcessorId is "{guid:D}" (E2E asserts this)
```

### AddSingleton + AddMeter registration symmetry
**Source:** `src/Orchestrator/Program.cs:72-73`
**Apply to:** `src/Keeper/Program.cs` (alongside existing `AddSingleton<L2ProbeRecovery>()` at line 31)

### Prometheus live-scrape assertion (poll + label-shape + no-workflowId)
**Source:** `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs:120-152, 231-277`
+ `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` (`PollPromForQuery`, `VectorNonEmpty`)
**Apply to:** both extended facts in `KeeperRecoveryE2ETests.cs`. Reuse `Uri.EscapeDataString` (built into the
client) — never build raw query URLs (V5/T-30-01).

### Triple-SHA snapshot-and-compare close gate
**Source:** `scripts/phase-33-close.ps1` (full protocol)
**Apply to:** `scripts/phase-39-close.ps1` (clone + the 2 deltas above)

## No Analog Found

These two **instrument TYPES** have ZERO in-repo precedent (RESEARCH Discrepancy 3 — only `CreateCounter<long>`
exists in `src/`). The planner must use the doc-grounded API shapes from RESEARCH Patterns 3 & 4, NOT an
existing example:

| Instrument (in KeeperMetrics.cs) | Type | Data Flow | Reason / Reference |
|---------------------------------|------|-----------|--------------------|
| `keeper_in_flight` | `UpDownCounter<long>` | gauge (probe-loop occupancy) | First UpDownCounter in the repo. API: `meter.CreateUpDownCounter<long>("keeper_in_flight")`; `+1` entry / `-1` finally in `L2ProbeRecovery.RunAsync` (RESEARCH Pattern 3) |
| `keeper_recovery_duration` | `Histogram<double>` (unit `"s"`) | histogram (intake→terminal latency) | First Histogram in the repo. Custom `{1,5,10,30,60,120}`s buckets via `InstrumentAdvice<double>{HistogramBucketBoundaries=...}` (DiagnosticSource≥9, Wave-0 verify) OR OTel `AddView(..., ExplicitBucketHistogramConfiguration{Boundaries=...})` in `Program.cs` (net8-safe fallback) (RESEARCH Pattern 4) |

**Wave-0 prerequisites the planner must sequence first** (RESEARCH Wave 0 Gaps / Open Questions):
1. `dotnet list src/Keeper/Keeper.csproj package --include-transitive | Select-String "DiagnosticSource"` → picks
   advice (≥9.0.0) vs `AddView` (8.x) bucket route.
2. Confirm histogram Prom suffix (`_seconds` vs bare) via one live scrape before locking the test query strings.
3. NEW hermetic `KeeperMetricsFacts.cs` (KMET-01/02/03) via `MeterListener`/in-memory exporter — **no exact
   in-repo analog**; closest is the live `MetricsRoundTripE2ETests` shape, but a hermetic `MeterListener` capture
   is a different mechanism. Flagged for the planner as a partial-analog task.

## Metadata

**Analog search scope:** `src/Keeper/**`, `src/Orchestrator/**`, `src/BaseProcessor.Core/**`,
`src/BaseConsole.Core/Messaging/**`, `tests/BaseApi.Tests/{Keeper,Orchestrator,Observability}/**`, `scripts/**`
**Files read (full or targeted):** OrchestratorMetrics.cs, ProcessorMetrics.cs, FaultEntryStepDispatchConsumer.cs,
FaultExecutionResultConsumer.cs, L2ProbeRecovery.cs, Keeper/Program.cs, Orchestrator/Program.cs (60-84),
EntryStepDispatchConsumer.cs (238-277), PrometheusTestClient.cs, MetricsRoundTripE2ETests.cs,
KeeperRecoveryE2ETests.cs, phase-33-close.ps1; grep-confirmed `ConsolidatedErrorTransportFilter.Dlq1 = "skp-dlq-1"`
**Pattern extraction date:** 2026-06-06
