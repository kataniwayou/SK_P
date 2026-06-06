# Phase 39: Keeper Observability + Real-Stack E2E + Close Gate - Research

**Researched:** 2026-06-06
**Domain:** .NET 8 OpenTelemetry metrics (IMeterFactory meter + Counter/UpDownCounter/Histogram), live Prometheus scrape-assertion E2E, triple-SHA close-gate scripting
**Confidence:** HIGH

## Summary

Phase 39 instruments **already-existing** Keeper code with a code-defined `Keeper` OTel meter, extends two existing Phase-36 RealStack E2E facts with `keeper_*` Prometheus scrape assertions, and clones the proven Phase-33 triple-SHA close gate. Every named source site, pattern, and queue from CONTEXT.md was verified against the live code and matches — with **three precise discrepancies** the planner must absorb (see below). The house meter pattern (`OrchestratorMetrics` / `ProcessorMetrics`) is a clean copy-shape for the counter half, but the histogram (`keeper_recovery_duration`) and UpDownCounter (`keeper_in_flight`) are **net-new instrument types with zero in-repo precedent** — those two need fresh, doc-grounded API wiring.

The most consequential finding is the `Advice<double>` / `InstrumentAdvice<double>` version question (D-04). In `System.Diagnostics.DiagnosticSource`, the public stable `InstrumentAdvice<T>` type and the `CreateHistogram(..., InstrumentAdvice<T>)` overload were introduced in **v9.0.0** and are NOT in the net8.0 framework-default DiagnosticSource (8.x). This repo targets `net8.0` but pins **OpenTelemetry 1.15.3**, which transitively floats DiagnosticSource to 9.x — so `InstrumentAdvice<double> { HistogramBucketBoundaries = [...] }` *is* compilable here, but the literal token is `InstrumentAdvice<double>`/`HistogramBucketBoundaries`, NOT the `Advice<double>`/`ExplicitBucketBoundaries` shorthand CONTEXT.md uses. Wave 0 must confirm the transitive DiagnosticSource version; a robust fallback exists via the OpenTelemetry SDK `AddView(..., ExplicitBucketHistogramConfiguration{ Boundaries = ... })` route, which is net8-safe regardless.

**Primary recommendation:** Build `KeeperMetrics` as an `IMeterFactory`-built singleton mirroring `OrchestratorMetrics` for the six counters; add the two new instrument types (UpDownCounter, Histogram) with verified .NET API shapes; wire bucket boundaries via the SDK View route (net8-safe) unless Wave 0 confirms DiagnosticSource ≥ 9.0.0 transitively (then `InstrumentAdvice<double>` is fine). Extend the two existing E2E facts with `PrometheusTestClient.PollPromForQuery(...VectorNonEmpty...)` blocks querying the correctly-suffixed Prom names, and clone `phase-33-close.ps1` adding a `list_queues name messages` depth==0 check on `keeper-dlq` + `skp-dlq-1`.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** `KeeperMetrics` built via `IMeterFactory` (never `static Meter`). `MeterName` const = `"Keeper"`, referenced at BOTH `meterFactory.Create(MeterName)` AND `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(KeeperMetrics.MeterName))` in `src/Keeper/Program.cs`. Registered as singleton, injected into `FaultEntryStepDispatchConsumer`, `FaultExecutionResultConsumer`, `L2ProbeRecovery`. Instruments snake_case, NO `_total`/counter suffix. Inherits ambient `service_instance_id` + combined `service_name={name}_{version}` resource labels for free — no Keeper-specific resource work.
- **D-02:** `keeper_dlq_pushed{reason, fault_type, ProcessorId}`. `reason` = forward-looking closed enum, single value today `"probe_exhausted"`. `fault_type ∈ {dispatch, result}`.
- **D-03:** Uniform tag scheme: `keeper_fault_consumed{fault_type, ProcessorId}` (top of Consume); `keeper_recovered{fault_type, ProcessorId}` (Recovered branch); `keeper_workflow_paused{ProcessorId}` (after Publish Pause); `keeper_workflow_resumed{ProcessorId}` (Recovered branch, after Publish Resume); `keeper_l2_probe_failed{ProcessorId}` (per RedisException catch). `ProcessorId = inner.ProcessorId`. NO `workflowId` label anywhere. `paused`/`resumed` carry no `fault_type`.
- **D-04:** `keeper_recovery_duration` histogram records BOTH outcomes, tagged `outcome ∈ {recovered, gave_up}`. Custom second-scale bucket boundaries via `Advice<double>` (≈ `{1, 5, 10, 30, 60, 120}` seconds). Measured in the CONSUMER (Stopwatch spanning intake→terminal: Publish(Pause) → probe loop → re-inject+Publish(Resume) OR dlq.Send).
- **D-05:** `keeper_in_flight` is an `UpDownCounter` measured INSIDE `L2ProbeRecovery.RunAsync` (+1 entry, -1 in finally).
- **D-06:** EXTEND the two existing Phase-36 RealStack facts (`KeeperRecovery_RecoversBothPaths`, `KeeperRecovery_GivesUp_ParksToDlq` in `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs`) with a `keeper_*` Prometheus scrape-assertion block. Do NOT author a separate `KeeperMetricsRoundTripE2ETests`.
- **D-07:** Keep the WRONGTYPE LIST-poison-on-a-GET-key outage recipe (`ArmWrongTypePoisonAsync`). `docker stop sk-redis` is REJECTED. TEST-01 dispatch path = genuine fault; result path = synthetic `Fault<ExecutionResult>`.
- **D-08:** Assert new `keeper_*` series appear in Prometheus with `fault_type`/`outcome`/`reason`/`ProcessorId` + non-empty `service_instance_id` labels after recover (FACT 1) and give-up (FACT 2). Reuse `PrometheusTestClient`/scrape helpers.
- **D-09:** `scripts/phase-39-close.ps1` = clone of `scripts/phase-33-close.ps1`. 3×consecutive-GREEN full suite + triple-SHA BEFORE==AFTER. ADD message-depth==0 assertion on BOTH `keeper-dlq` (DLQ-2) and the DLQ-1 via `rabbitmqctl list_queues name messages`. Unfiltered redis `--scan` SHA already catches leaked `skp:keeper:probe:*`.
- **D-10:** Net-zero DLQ drain stays in E2E test teardown (no gate-side `purge_queue`). Inherit pre-flight stable-processor-row seed + container-rebuild discipline (rebuild `baseapi-service orchestrator processor-sample keeper`).

### Claude's Discretion
- Exact `KeeperMetrics` instrument wiring; precise `Advice<double>` bucket values within the agreed second-scale family; `reason`/`fault_type`/`outcome` enum constant naming and location (likely a small `KeeperMetricTags` static or inline consts); `keeper_in_flight` numeric type (`long` vs `int`); exact `Stopwatch` placement inside each consumer.
- Whether `KeeperMetrics` carries a single shared tag-builder helper to avoid `KeyValuePair` repetition.

### Deferred Ideas (OUT OF SCOPE)
- `reinject_failed` as a second `keeper_dlq_pushed{reason}` value (enum shaped to accept it, not emitted today).
- An `in_flight`/recovery-rate Grafana dashboard or Prometheus alert rule.
- A `keeper_processing`-style intermediate outcome.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| KMET-01 | Code-defined `Keeper` meter, house pattern (snake_case, no `_total`, inherited `service_instance_id`) | `OrchestratorMetrics`/`ProcessorMetrics` copy-shape verified; `AddMeter`/singleton symmetry confirmed in `Program.cs` patterns; resource labels inherited via `AddBaseConsoleObservability` (verified) |
| KMET-02 | Throughput/outcome counters (`keeper_fault_consumed`, `_recovered`, `_dlq_pushed{reason}`, `_workflow_paused`, `_workflow_resumed`, `_l2_probe_failed`), labeled `processorId` where meaningful, no `workflowId` | All six increment sites verified to exist as real branches in the two consumers + probe loop; `inner.ProcessorId` available on both inner messages; `{outcome}` interned-label precedent found in `EntryStepDispatchConsumer.OutcomeLabel` |
| KMET-03 | Bottleneck signals: `keeper_in_flight` UpDownCounter + `keeper_recovery_duration` histogram | `RunAsync` body confirmed as the in-flight scope; consumer `Consume` confirmed as the histogram span (sees both terminals). NO in-repo precedent for either type — API shapes verified from .NET docs |
| TEST-01 | Real-stack E2E: dead-letter both dispatch+result, pause, recover, resume, re-inject exactly-once | `KeeperRecovery_RecoversBothPaths` already induces both; extend with scrape block |
| TEST-02 | Real-stack E2E: give-up path → `keeper-dlq` + `keeper_dlq_pushed` increments | `KeeperRecovery_GivesUp_ParksToDlq` already induces give-up; extend with scrape block |
| TEST-03 | Phase-close gate 3×GREEN + triple-SHA incl. both DLQs + scratch scan-clean, Release+Debug 0-warning | `phase-33-close.ps1` clone source fully mapped; precise delta documented below |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Meter definition (`KeeperMetrics`) | Keeper console (`src/Keeper`) | — | Owns its own code-defined meter; resource labels come from `BaseConsole.Core` for free |
| Counter increments (6) | Keeper consumers + probe loop | — | Increment sites are the existing fault-consumer branches + the probe `RedisException` catch |
| Histogram span timing | Keeper consumer `Consume` | — | Only the consumer sees both terminals (re-inject+resume OR dlq.Send); the probe helper cannot |
| UpDownCounter in-flight | `L2ProbeRecovery.RunAsync` | — | KMET-03 scopes it literally to "messages currently held in probe loops" = the RunAsync body |
| Meter→Prom pipeline | OTel collector (`compose/`) + Prometheus | Keeper OTLP export | Collector appends Prom suffixes + promotes resource attrs to labels; Keeper only emits |
| E2E scrape assertion | Test harness (`tests/BaseApi.Tests`) | live Prometheus :9090 | Tests query the Prometheus SERVER, not the collector exporter |
| Close gate net-zero | `scripts/phase-39-close.ps1` | docker compose / E2E teardown | Gate snapshots-and-compares; the E2E teardown owns the drain |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `System.Diagnostics.Metrics` (in `System.Diagnostics.DiagnosticSource`) | transitive via OTel 1.15.3 (≥9.0.0 — VERIFY Wave 0) | `Meter`, `IMeterFactory`, `Counter<T>`, `UpDownCounter<T>`, `Histogram<T>`, `InstrumentAdvice<T>` | The .NET 8 blessed metrics API; house pattern already uses it [VERIFIED: src/Orchestrator/Observability/OrchestratorMetrics.cs] |
| `OpenTelemetry` | 1.15.3 | MeterProvider, `AddMeter`, `AddView`, OTLP exporter | Pinned repo-wide [VERIFIED: Directory.Packages.props:77] |
| `OpenTelemetry.Extensions.Hosting` | 1.15.3 | `ConfigureOpenTelemetryMeterProvider` seam | The additive-attach seam the house pattern uses [VERIFIED: src/Orchestrator/Program.cs:73] |

### Supporting (test + gate — already present, no installs)
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `PrometheusTestClient` (in-repo) | — | live `/api/v1/query` poll + predicates | Scrape assertions (D-08) [VERIFIED: tests/.../Observability/Helpers/PrometheusTestClient.cs] |
| MassTransit + MassTransit.RabbitMQ | (CPM-pinned) | in-test `IBusControl` probes | Already used by both E2E facts |
| StackExchange.Redis | (CPM-pinned) | poison arm + scan + teardown | Already used by both E2E facts |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `InstrumentAdvice<double>` on `CreateHistogram` (D-04) | OTel SDK `AddView("keeper_recovery_duration", new ExplicitBucketHistogramConfiguration { Boundaries = [...] })` | The View route is net8-safe regardless of DiagnosticSource version, but splits bucket config out of `KeeperMetrics` into `Program.cs`/`AddBaseConsoleObservability`. The advice route keeps it co-located with the instrument. **Recommend advice IF Wave 0 confirms DiagnosticSource ≥ 9.0.0; else View.** |
| Histogram unit `"s"` (→ `_seconds` Prom suffix) | no unit (→ bare `_count`/`_sum`/`_bucket`) | Setting unit `"s"` makes the Prom name `keeper_recovery_duration_seconds_*` (matches the http_server precedent). Omitting unit yields `keeper_recovery_duration_*`. **The test query string MUST match whichever is chosen** — this is the #1 flake risk. |

**Installation:** No new packages. All dependencies are present/transitive.

**Version verification (Wave 0 MUST run):**
```powershell
# Confirm the transitive DiagnosticSource version that OTel 1.15.3 floats in
dotnet list src/Keeper/Keeper.csproj package --include-transitive | Select-String "DiagnosticSource"
```
If ≥ 9.0.0 → `InstrumentAdvice<double> { HistogramBucketBoundaries = [...] }` compiles. If 8.x → use the OTel `AddView` route. `[VERIFIED: Directory.Packages.props pins OpenTelemetry 1.15.3; DiagnosticSource not pinned standalone — floats transitively]`

## Architecture Patterns

### System Data Flow

```
                  Fault<EntryStepDispatch> / Fault<ExecutionResult>  (pub/sub)
                                     │
                                     ▼
        ┌──────────────────── Keeper console ──────────────────────┐
        │  FaultEntryStepDispatchConsumer / FaultExecutionResultConsumer │
        │  Consume(ctx):                                            │
        │   ├─ [keeper_fault_consumed{fault_type,ProcessorId}] ◄ top │
        │   ├─ Stopwatch.StartNew()  ◄──────── D-04 span START      │
        │   ├─ Publish(PauseWorkflow)                               │
        │   ├─ [keeper_workflow_paused{ProcessorId}]                │
        │   ├─ await L2ProbeRecovery.RunAsync(entryId,h,ct) ────────┼──┐
        │   │     ├─ [keeper_in_flight ++]  ◄── D-05 entry          │  │
        │   │     ├─ for(attempt<max): READ + WRITE+DEL scratch     │  │ probe
        │   │     │     catch RedisException →                      │  │ loop
        │   │     │        [keeper_l2_probe_failed{ProcessorId}]     │  │
        │   │     └─ [keeper_in_flight --]  ◄── D-05 finally        │  │
        │   ├─ if Recovered:                                        │◄─┘
        │   │     ├─ Send(inner) to origin endpoint                 │
        │   │     ├─ Publish(ResumeWorkflow)                        │
        │   │     ├─ [keeper_workflow_resumed{ProcessorId}]         │
        │   │     ├─ [keeper_recovered{fault_type,ProcessorId}]     │
        │   │     └─ record(keeper_recovery_duration, outcome=recovered) ◄ span END
        │   └─ else (GaveUp):                                       │
        │         ├─ dlq.Send(ctx.Message) → keeper-dlq             │
        │         ├─ [keeper_dlq_pushed{reason=probe_exhausted,fault_type,ProcessorId}]│
        │         └─ record(keeper_recovery_duration, outcome=gave_up) ◄ span END
        └───────────────────────────────────────────────────────────┘
                                     │ OTLP metrics export
                                     ▼
        otel-collector  ── add_metric_suffixes + resource_to_telemetry_conversion ──▶ prometheus :9090
                                     │                                                    ▲
                                     │  Counter → _total ; Histogram → _count/_sum/_bucket │ E2E scrape
                                     │  UpDownCounter → gauge (no suffix)                  │ assertion
                                     ▼                                                    │
                          keeper_fault_consumed_total{...} etc. ◄──── PrometheusTestClient.PollPromForQuery
```

### Pattern 1: IMeterFactory meter (copy `OrchestratorMetrics` exactly)
**What:** A `sealed class` with a `public const string MeterName`, instrument properties, and a ctor taking `IMeterFactory`.
**When:** The counter half of `KeeperMetrics`.
**Example:**
```csharp
// Source: src/Orchestrator/Observability/OrchestratorMetrics.cs [VERIFIED]
public sealed class OrchestratorMetrics
{
    public const string MeterName = "Orchestrator";
    public Counter<long> DispatchSent { get; }
    public OrchestratorMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        DispatchSent = meter.CreateCounter<long>("orchestrator_dispatch_sent"); // collector appends _total
    }
}
```
Registration symmetry (mirror in `src/Keeper/Program.cs`):
```csharp
// Source: src/Orchestrator/Program.cs:72-73 [VERIFIED]
builder.Services.AddSingleton<OrchestratorMetrics>();
builder.Services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(OrchestratorMetrics.MeterName));
```

### Pattern 2: Interned per-increment label (copy `OutcomeLabel`)
**What:** Map an enum to a pinned lower-case Prometheus label literal via a `switch` returning interned consts — decouples the Prom label from the C# enum name and avoids per-send allocation.
**When:** `fault_type ∈ {dispatch, result}`, `outcome ∈ {recovered, gave_up}`, `reason = "probe_exhausted"`.
**Example:**
```csharp
// Source: src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:248-269 [VERIFIED]
metrics.ResultSent.Add(1,
    new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")),
    new KeyValuePair<string, object?>("outcome", OutcomeLabel(result.Outcome)));

private static string OutcomeLabel(StepOutcome o) => o switch {
    StepOutcome.Completed => "completed",
    StepOutcome.Failed    => "failed",
    StepOutcome.Cancelled => "cancelled",
    _                     => o.ToString().ToLowerInvariant(),
};
```
> Note: the two fault consumers differ ONLY by `fault_type` ("dispatch" vs "result"). A shared tag-builder on `KeeperMetrics` (Claude's discretion) keeps the increment sites DRY. `ProcessorId` is `inner.ProcessorId.ToString("D")` (the test asserts `ProcessorId="{guid:D}"`).

### Pattern 3: UpDownCounter (net-new — verified API)
**What:** Non-monotonic counter; `+1` on probe-loop entry, `-1` in `finally`.
**Example:**
```csharp
// .NET API [CITED: learn.microsoft.com/dotnet/api/system.diagnostics.metrics.meter.createupdowncounter]
UpDownCounter<long> InFlight = meter.CreateUpDownCounter<long>("keeper_in_flight");
// at RunAsync:
metrics.InFlight.Add(1, new KeyValuePair<string,object?>("ProcessorId", procId));   // entry
try { /* for(attempt...) loop */ }
finally { metrics.InFlight.Add(-1, new KeyValuePair<string,object?>("ProcessorId", procId)); }
```
> `RunAsync` currently has signature `RunAsync(string entryId, string h, CancellationToken ct)` — it does NOT receive `ProcessorId`. If `keeper_in_flight` is to carry `ProcessorId` the planner must thread it through (KMET-03 does not require a ProcessorId label on in_flight; the requirement text scopes it to "messages currently held in probe loops" with no label mandate — recommend emitting it UNLABELLED or adding a `procId` param). **Decision point — see Open Questions.** `long` recommended over `int` (Prom gauge is a double anyway; `long` matches the house `Counter<long>` convention).

### Pattern 4: Histogram with custom buckets (net-new — version-sensitive)
**Route A (advice — only if DiagnosticSource ≥ 9.0.0, VERIFY Wave 0):**
```csharp
// [CITED: learn.microsoft.com/dotnet/api/system.diagnostics.metrics.instrumentadvice-1] (net9+ type)
Histogram<double> RecoveryDuration = meter.CreateHistogram<double>(
    "keeper_recovery_duration",
    unit: "s",                       // → Prom name keeper_recovery_duration_seconds_* (see suffix note)
    advice: new InstrumentAdvice<double> {
        HistogramBucketBoundaries = new double[] { 1, 5, 10, 30, 60, 120 }
    });
// record:
metrics.RecoveryDuration.Record(sw.Elapsed.TotalSeconds,
    new KeyValuePair<string,object?>("outcome", outcomeLabel),
    new KeyValuePair<string,object?>("ProcessorId", procId));
```
**Route B (OTel View — net8-safe, recommend if advice unavailable):**
```csharp
// in AddBaseConsoleObservability's .WithMetrics(...) OR a Keeper-local ConfigureOpenTelemetryMeterProvider
// [CITED: github.com/open-telemetry/opentelemetry-dotnet/.../customizing-the-sdk]
mp.AddView("keeper_recovery_duration",
    new ExplicitBucketHistogramConfiguration { Boundaries = new double[] { 1, 5, 10, 30, 60, 120 } });
```
> The instrument is still `meter.CreateHistogram<double>("keeper_recovery_duration", unit: "s")` in `KeeperMetrics`; only the bucket config moves to a View. CONTEXT.md D-04 says `Advice<double>`, which is Claude's-discretion on exact mechanism within the agreed bucket family — the View route satisfies the intent.

### Anti-Patterns to Avoid
- **`static Meter` field** — leaks across the shared hermetic test process (D-01 invariant). Always `IMeterFactory`.
- **Embedding `_total`/`_seconds` in the instrument name** — the collector's `add_metric_suffixes` doubles it. Name = `keeper_fault_consumed`; Prom series = `keeper_fault_consumed_total`.
- **A `workflowId` label** — high-cardinality DoS ban (KMET-02). `AssertBusinessLabels` actively asserts its absence.
- **Querying the wrong Prom suffix in the test** — counter `_total`; histogram `_count`/`_sum`/`_bucket` (+`_seconds` if unit set); UpDownCounter (gauge) bare. Mismatch = guaranteed flake.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Prometheus scrape poll | A new HttpClient loop | `PrometheusTestClient.PollPromForQuery(promQL, VectorNonEmpty, timeoutMs, ct)` | Handles scrape-interval sleep, exp backoff, EscapeDataString, JSON clone [VERIFIED] |
| Label-shape assertion | Manual JSON walking | `FirstMetricObject` + `TryGetNonEmpty` (copy from MetricsRoundTripE2ETests) | Proven; same shape the keeper assertions need |
| Outage induction | `docker stop sk-redis` | `ArmWrongTypePoisonAsync` (LIST-poison-on-a-GET-key) | D-07; deterministic, keeps stack Redis-healthy |
| DLQ net-zero drain | gate-side `purge_queue` | E2E teardown `L2KeysToCleanup` + ACK-drain probe | D-10; purge would mask a real leak |
| Triple-SHA gate | New script | Clone `phase-33-close.ps1` | Proven protocol incl. pre-flight seed + rebuild discipline |

**Key insight:** Every hard part (scrape timing, poison recipe, net-zero discipline, triple-SHA) is already solved in-repo. Phase 39 is threading + extension + a precise gate delta — not new machinery. The ONLY genuinely new code is the two instrument types.

## Verified Code Sites & Discrepancies

All CONTEXT.md-named sites were read and confirmed to exist with the claimed branch structure. Confirmations:

- `FaultEntryStepDispatchConsumer.Consume` / `FaultExecutionResultConsumer.Consume`: top-of-Consume, `Publish(PauseWorkflow)`, `await recovery.RunAsync(...)`, `if (outcome == ProbeOutcome.Recovered)` → `Send` + `Publish(ResumeWorkflow)`, `else` → `dlq.Send(context.Message)` to `KeeperQueues.DeadLetter`. `inner.ProcessorId` available. Consumers use a **primary constructor** `(ILogger<...> logger, L2ProbeRecovery recovery)` — `KeeperMetrics metrics` is added as a third primary-ctor param (mirrors `ProcessorMetrics metrics,` injection). [VERIFIED]
- `L2ProbeRecovery.RunAsync(string entryId, string h, CancellationToken ct)`: `for (var attempt = 0; attempt < max; attempt++)` body with `catch (RedisException)` — the `l2_probe_failed` site. `RunAsync` body is the `in_flight` ++/finally-- scope. The class uses a primary constructor `(IConnectionMultiplexer redis, IOptions<ProbeOptions> opts)` — add `KeeperMetrics metrics`. [VERIFIED]
- `src/Keeper/Program.cs`: `AddSingleton`/`AddMeter` go alongside line 31 (`AddSingleton<L2ProbeRecovery>()`). [VERIFIED]
- `ProbeOptions`: `DelaySeconds=5`, `MaxAttempts=12` (60s window) — confirmed in both `ProbeOptions.cs` defaults and `src/Keeper/appsettings.json` `"Probe"` section. [VERIFIED]
- `AddBaseConsoleObservability`: supplies `service.instance.id` + combined metrics `service.name={name}_{version}`; no per-Keeper resource work. [VERIFIED]
- `compose/otel-collector-config.yaml`: `resource_to_telemetry_conversion.enabled: true` + (implicit default) `add_metric_suffixes`. Suffix rules documented in-file (lines 54-58). [VERIFIED]
- `KeeperQueues.cs`: `FaultRecovery="keeper-fault-recovery"`, `DeadLetter="keeper-dlq"` (no-TTL, durable). [VERIFIED]
- `L2ProjectionKeys.KeeperProbe(h)` → `skp:keeper:probe:{h}`; covered by unfiltered redis `--scan`. [VERIFIED]

### Discrepancy 1 — DLQ-1 is `skp-dlq-1`, NOT a `*_error` queue (D-09)
CONTEXT.md D-09 says assert depth==0 on "the `*_error` DLQ-1." The actual DLQ-1 is the single fixed const **`skp-dlq-1`** (`ConsolidatedErrorTransportFilter.Dlq1`), a durable no-consumer 7-day-TTL queue declared once in `BaseConsole.Core`. Phase 36's `ConsolidatedErrorTransportFilter` **replaced** the per-`{queue}_error` default. The gate's depth check must target `keeper-dlq` AND `skp-dlq-1` — there is no `*_error` queue to scan. [VERIFIED: src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs:45; MessagingServiceCollectionExtensions.cs:79]

### Discrepancy 2 — `Advice<double>` token (D-04)
The .NET 8 framework-default type was `Advice<T>` with `ExplicitBucketBoundaries`; the current stable public type (DiagnosticSource ≥ 9.0.0) is `InstrumentAdvice<T>` with `HistogramBucketBoundaries`, and the `CreateHistogram(..., InstrumentAdvice<T>)` overload is gated to net9+. This repo's OTel 1.15.3 floats DiagnosticSource to 9.x transitively, so `InstrumentAdvice<double>` is the correct token here — NOT the literal `Advice<double>` in CONTEXT.md. Verify the transitive version in Wave 0; fall back to the OTel View route if it resolves to 8.x. [VERIFIED: docs moniker ranges + nuget transitive]

### Discrepancy 3 — No in-repo histogram/UpDownCounter precedent
CONTEXT.md references the house `Advice<double>` histogram "if present" — it is NOT present anywhere in `src/`. Only `CreateCounter<long>` exists. Both `keeper_in_flight` (UpDownCounter) and `keeper_recovery_duration` (Histogram) are the FIRST instances of their type in the codebase. The planner cannot point a task at an existing example for these two; the API shapes in Patterns 3 & 4 above are the reference. [VERIFIED: grep for Advice/CreateHistogram/CreateUpDownCounter returned no src matches]

## Common Pitfalls

### Pitfall 1: Prom suffix mismatch in the scrape assertion
**What goes wrong:** Test queries `keeper_recovery_duration` but the series is `keeper_recovery_duration_seconds_bucket`; or queries `keeper_in_flight_total` but it's a gauge `keeper_in_flight`.
**Why:** The collector's `add_metric_suffixes` appends `_total` to counters, expands histograms to `_count`/`_sum`/`_bucket`, adds `_seconds` when unit="s", and leaves UpDownCounters (gauges) bare.
**How to avoid:** Counter queries → `keeper_fault_consumed_total{...}`. Histogram existence → `keeper_recovery_duration_seconds_count{outcome="..."}` (if unit="s") else `keeper_recovery_duration_count`. Gauge → `keeper_in_flight`. **Pin the unit decision first, then write queries to match.**
**Warning signs:** `PollPromForQuery` returns null / `Assert.NotNull` fails despite the live flow succeeding.

### Pitfall 2: Histogram value scale vs bucket scale
**What goes wrong:** Recording `sw.ElapsedMilliseconds` (ms) against second-scale buckets `{1,5,...,120}` puts every observation in the top bucket.
**Why:** Buckets are seconds (D-04); the Stopwatch must be recorded as `sw.Elapsed.TotalSeconds`.
**How to avoid:** `Record(sw.Elapsed.TotalSeconds, ...)`. A recover lands ~0–2s (bottom bucket), a give-up ~60s (top bucket) — the histogram shape IS the saturation story.

### Pitfall 3: Stale keeper container in the live gate
**What goes wrong:** The E2E facts pass-locally-authored but the live run uses a Phase-34 placeholder keeper with no meter → `keeper_*` series never appear.
**Why:** The embedded SourceHash + the new meter must be in the live image.
**How to avoid:** Rebuild `baseapi-service orchestrator processor-sample keeper` before the gate (D-10). Already documented in the E2E test's operator-gate remarks.
**Warning signs:** Liveness/scrape timeouts; `keeper_*` queries empty.

### Pitfall 4: Adding the `messages` column changes the RMQ name-SHA
**What goes wrong:** Changing the gate's `list_queues name` to `list_queues name messages` for the SHA would make every run's depth churn the SHA.
**Why:** Message depth fluctuates; it is not a stable snapshot input.
**How to avoid:** Keep the existing `list_queues name` SHA UNCHANGED. Add the depth==0 check as a SEPARATE `rabbitmqctl list_queues name messages` parse asserting `keeper-dlq` and `skp-dlq-1` both read 0 (additive assertion, not folded into the SHA).

### Pitfall 5: Probe loop holds the delivery un-acked for 60s (give-up histogram tail)
**What goes wrong:** The give-up E2E budget (`DlqParkPollTimeoutMs=180_000`) must absorb the full 60s probe window + Immediate(N) + park.
**Why:** The loop is awaited inside Consume (un-acked window, ProbeOptions constraint).
**How to avoid:** Existing budgets already account for this. The `keeper_recovery_duration{outcome=gave_up}` ~60s observation is expected, not a bug.

## Code Examples

### KeeperMetrics shape (synthesis of verified house patterns + verified .NET API)
```csharp
// Synthesis — counters from OrchestratorMetrics [VERIFIED]; histogram/updowncounter from .NET docs [CITED]
public sealed class KeeperMetrics
{
    public const string MeterName = "Keeper";
    public Counter<long> FaultConsumed   { get; }
    public Counter<long> Recovered       { get; }
    public Counter<long> DlqPushed        { get; }
    public Counter<long> WorkflowPaused   { get; }
    public Counter<long> WorkflowResumed  { get; }
    public Counter<long> L2ProbeFailed    { get; }
    public UpDownCounter<long> InFlight    { get; }
    public Histogram<double> RecoveryDuration { get; }

    public KeeperMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        FaultConsumed   = meter.CreateCounter<long>("keeper_fault_consumed");
        Recovered       = meter.CreateCounter<long>("keeper_recovered");
        DlqPushed       = meter.CreateCounter<long>("keeper_dlq_pushed");
        WorkflowPaused  = meter.CreateCounter<long>("keeper_workflow_paused");
        WorkflowResumed = meter.CreateCounter<long>("keeper_workflow_resumed");
        L2ProbeFailed   = meter.CreateCounter<long>("keeper_l2_probe_failed");
        InFlight        = meter.CreateUpDownCounter<long>("keeper_in_flight");
        RecoveryDuration = meter.CreateHistogram<double>("keeper_recovery_duration", unit: "s");
        // bucket boundaries via InstrumentAdvice (if DiagnosticSource≥9) OR AddView in Program.cs (net8-safe)
    }
}
```

### Scrape assertion block to add to each E2E fact (mirror MetricsRoundTripE2ETests)
```csharp
// Source pattern: tests/.../Orchestrator/MetricsRoundTripE2ETests.cs:120-152 [VERIFIED]
using var prom = new PrometheusTestClient();
var consumed = await prom.PollPromForQuery(
    $"keeper_fault_consumed_total{{fault_type=\"dispatch\",ProcessorId=\"{procId:D}\"}}",
    PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
Assert.NotNull(consumed);
// label-shape: non-empty service_instance_id, no workflowId (reuse FirstMetricObject + TryGetNonEmpty)
```
> FACT 2 (give-up) asserts `keeper_dlq_pushed_total{reason="probe_exhausted",fault_type="result"}` and `keeper_recovery_duration_seconds_count{outcome="gave_up"}`. FACT 1 (recover) asserts `keeper_recovered_total`, `keeper_workflow_paused_total`, `keeper_workflow_resumed_total`, and `keeper_recovery_duration_seconds_count{outcome="recovered"}`. `keeper_in_flight` is transient (gauge back to 0 after the loop) — assert PRESENCE of the series, not a specific value, and do it while a probe is in-flight or accept that it may have decremented; safest is to assert the COUNTER series and treat the gauge as best-effort (see Open Questions).

### phase-39-close.ps1 delta from phase-33-close.ps1
```powershell
# 1. Rebuild set: add 'keeper' to the services list + the CONTRACT-CHANGE rebuild note.
$services = @('postgres','redis','rabbitmq','otel-collector','elasticsearch','prometheus',
              'orchestrator','processor-sample','baseapi-service','keeper')   # + keeper

# 2. NEW additive assertion (after the RMQ name-SHA, before exit). Keep the name-SHA unchanged.
$depthRaw = docker exec sk-rabbitmq rabbitmqctl -q list_queues name messages | Out-String
foreach ($q in @('keeper-dlq','skp-dlq-1')) {              # DLQ-2 + DLQ-1 (skp-dlq-1, NOT *_error)
    $line = ($depthRaw -split "`n" | Where-Object { $_ -match "^\s*$([regex]::Escape($q))\s+(\d+)\s*$" })
    $depth = if ($line -match "\s+(\d+)\s*$") { [int]$Matches[1] } else { -1 }
    if ($depth -ne 0) {
        Write-Host "DLQ depth invariant VIOLATED: $q depth=$depth (expected 0)" -ForegroundColor Red
        $allGood = $false
    }
}
```
> The unfiltered redis `--scan` SHA already covers `skp:keeper:probe:*` scratch keys — no change there. The pre-flight stable-processor-row seed, the settle-drain, and the psql/redis/rmq triple-SHA stay verbatim.

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `Advice<T>` / `ExplicitBucketBoundaries` | `InstrumentAdvice<T>` / `HistogramBucketBoundaries` | DiagnosticSource 9.0.0 | The token in D-04 must be the 9.x name; verify transitive version |
| per-`{queue}_error` DLQ | one consolidated `skp-dlq-1` | Phase 36 (`ConsolidatedErrorTransportFilter`) | Gate asserts `skp-dlq-1`, not `*_error` |

**Deprecated/outdated:** `Advice<double>` token (renamed `InstrumentAdvice<double>` in 9.0.0).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | OTel 1.15.3 floats DiagnosticSource to ≥9.0.0 transitively, making `InstrumentAdvice<double>` compilable on net8.0 | Standard Stack / Discrepancy 2 | If it resolves to 8.x, `InstrumentAdvice` won't compile → MUST use the OTel `AddView` route. **Wave 0 `dotnet list package --include-transitive` resolves this — LOW residual risk.** |
| A2 | Histogram unit `"s"` yields Prom name `keeper_recovery_duration_seconds_*` (matching the http_server_request_duration_seconds precedent) | Pattern 4 / Pitfall 1 | If unit is omitted, the suffix is bare `_count`/`_sum`/`_bucket` and the test query must drop `_seconds`. Verifiable by one live scrape. MEDIUM. |
| A3 | `keeper_in_flight` (UpDownCounter) is exported as a Prometheus gauge with NO suffix | Pattern 3 / Pitfall 1 | Standard OTel→Prom mapping for non-monotonic sums; if exported differently the gauge query needs adjustment. LOW. |

## Open Questions

1. **`keeper_in_flight` ProcessorId label + assertion strategy**
   - What we know: `RunAsync(entryId, h, ct)` has no `ProcessorId` param today. KMET-03 mandates the instrument but not a `ProcessorId` label on it. The gauge returns to 0 after the loop, so a post-flow scrape may see 0 or a no-longer-present series.
   - What's unclear: whether to thread `procId` into `RunAsync` for a label, and whether the E2E can reliably assert the gauge series (it is transient).
   - Recommendation: Emit `keeper_in_flight` UNLABELLED (or with `ProcessorId` only if `RunAsync` is given the param — minor signature change, Claude's discretion). For the E2E, assert the six COUNTER series + the histogram `_count`; treat `keeper_in_flight` as best-effort/optional in the scrape block (its correctness is covered by the unit test, not the live race). Flag for planner.

2. **Bucket-config mechanism (advice vs View)** — resolved by Wave 0 transitive-version check (A1). Default to `AddView` if uncertain (net8-safe).

3. **Histogram unit decision (`"s"` vs none)** — pick `"s"` for parity with the existing `_seconds` histogram convention; write test queries with `_seconds`. Confirm with one live scrape during the first GREEN run.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Docker compose stack (postgres, redis, rabbitmq, otel-collector, elasticsearch, prometheus, orchestrator, processor-sample, baseapi-service, **keeper**) | E2E facts + close gate | assumed ✓ (live RealStack precedent) | per compose | none — gate exits 2 if unhealthy |
| Prometheus :9090 | scrape assertions | ✓ (PrometheusTestClient hardcodes localhost:9090) | — | none |
| RabbitMQ mgmt (`rabbitmqctl list_queues name messages`) | gate depth check | ✓ (Phase-33 gate uses `list_queues name`) | — | none |
| `dotnet` SDK (net8.0) | build/test/gate | ✓ | net8.0 | none |
| `pwsh` | close gate | ✓ (phase-33 gate is pwsh) | — | none |

**Missing dependencies with no fallback:** None identified — all are inherited from the Phase-33/36 live precedent. Wave 0 MUST confirm the transitive DiagnosticSource version (A1) and that the compose `keeper` tier rebuilds with the new meter.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit on Microsoft.Testing.Platform (MTP); RealStack facts run LIVE against docker compose |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release --filter-not-trait "Category=RealStack"` (hermetic only) |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build` (incl. RealStack, as the gate runs it) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| KMET-01 | `Keeper` meter registered, `AddMeter("Keeper")` symmetry, snake_case no `_total` | unit (hermetic) | `dotnet test ... --filter-class *KeeperMetrics*` | ❌ Wave 0 (new hermetic fact: assert MeterName const, instrument names, registration) |
| KMET-02 | Six counters increment at the right branches with correct tags, no workflowId | unit + E2E | hermetic consumer test (mock `KeeperMetrics`/MeterListener) + live scrape | ❌ Wave 0 hermetic; ✅ E2E extends existing facts |
| KMET-03 | `keeper_in_flight` UpDownCounter +/- around probe loop; `keeper_recovery_duration` histogram both outcomes | unit + E2E | hermetic `MeterListener` capture test + live scrape `_count` | ❌ Wave 0 hermetic; ✅ E2E |
| TEST-01 | Live recover-both-paths + `keeper_*` recover series scrape | E2E (RealStack) | `dotnet test ... --filter-method KeeperRecovery_RecoversBothPaths` | ✅ exists — extend with scrape block |
| TEST-02 | Live give-up → keeper-dlq + `keeper_dlq_pushed`/`keeper_recovery_duration{gave_up}` scrape | E2E (RealStack) | `dotnet test ... --filter-method KeeperRecovery_GivesUp_ParksToDlq` | ✅ exists — extend with scrape block |
| TEST-03 | 3×GREEN + triple-SHA + both-DLQ depth==0 + scratch scan-clean, Release+Debug 0-warning | gate script | `pwsh -File scripts/phase-39-close.ps1` | ❌ Wave 0 (clone phase-33-close.ps1) |

### Sampling Rate
- **Per task commit:** `dotnet test ... --filter-not-trait "Category=RealStack" -c Release` (hermetic; <30s) — catches meter wiring/tag regressions without the live stack.
- **Per wave merge:** Full hermetic suite Release + Debug 0-warning build.
- **Phase gate:** `scripts/phase-39-close.ps1` — 3× consecutive GREEN full suite (RealStack live) + triple-SHA net-zero + both-DLQ depth==0.

### Falsifiable Signals (the "did it actually work?" test per requirement)
- **KMET-01/02/03 (code):** A hermetic `MeterListener`/in-memory exporter test subscribes to the `"Keeper"` meter, drives a faked consume/probe, and asserts each instrument fired with the exact tag set (and `keeper_in_flight` went +1 then -1). Falsified if a tag is wrong, a `workflowId` appears, or an instrument is missing.
- **TEST-01/02 (live):** `PollPromForQuery("keeper_*_total{...}", VectorNonEmpty, 120_000)` returns a non-empty vector with non-empty `service_instance_id` and no `workflowId`, after the live recover/give-up flow. Falsified if any keeper series is absent or mislabeled.
- **TEST-03 (gate):** exit 0 = 3×GREEN (identical Passed count) + psql/redis/rmq SHA BEFORE==AFTER + `keeper-dlq`==0 + `skp-dlq-1`==0. Falsified by any RED run, any SHA drift, or any DLQ depth>0.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs` (or similar) — hermetic meter/instrument/tag assertions for KMET-01/02/03 (no live stack). Use `MeterListener` or an in-memory OTel exporter.
- [ ] `scripts/phase-39-close.ps1` — clone of `phase-33-close.ps1` with the documented delta (rebuild `keeper`, both-DLQ depth==0 against `keeper-dlq`+`skp-dlq-1`).
- [ ] Confirm transitive DiagnosticSource version (`dotnet list package --include-transitive`) → picks advice-vs-View route.
- [ ] Confirm histogram Prom suffix (`_seconds` vs bare) via one live scrape before locking test query strings.
- *(The two E2E facts already exist — they are EXTENDED, not created.)*

## Security Domain

> `security_enforcement` is absent from `.planning/config.json` (= enabled by default). This phase adds NO new auth, session, access-control, or external-input surface — it instruments existing code and reads telemetry. The relevant controls are already established and must be preserved.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No new auth surface |
| V3 Session Management | no | — |
| V4 Access Control | no | — |
| V5 Input Validation | yes (preserve) | The only interpolated test input is the PromQL query — `Uri.EscapeDataString` is already applied in `PrometheusTestClient` (T-30-01 Tampering mitigation). Keep using the client; never build raw query URLs. |
| V6 Cryptography | no | — |
| Cardinality / metric-label DoS | yes | NO `workflowId` (or any unbounded id) as a metric label (KMET-02). `ProcessorId` is bounded by registered processors. `fault_type`/`outcome`/`reason` are closed enums. The hermetic test asserts no `workflowId`. |
| Telemetry info-leak | yes (inherited) | Existing consumers log only `ExceptionType`+`Message` (not stack frames) at Information; this phase adds metrics only (no message bodies/PII in labels). |

### Known Threat Patterns for {Keeper metrics + PromQL test client}
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| High-cardinality metric label (workflowId/correlationId) → Prometheus memory DoS | Denial of Service | Closed-enum + bounded-id labels only; hermetic assertion that no workflowId label exists |
| PromQL injection via interpolated test query | Tampering | `Uri.EscapeDataString` (already in PrometheusTestClient); only validated GUID procId is interpolated |

## Sources

### Primary (HIGH confidence)
- `src/Orchestrator/Observability/OrchestratorMetrics.cs`, `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` — house IMeterFactory pattern + `{outcome}` interned-label precedent (`EntryStepDispatchConsumer.cs:248-269`)
- `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs`, `FaultExecutionResultConsumer.cs`, `Recovery/L2ProbeRecovery.cs`, `Program.cs`, `ProbeOptions.cs`, `appsettings.json` — increment sites + injection points + probe window
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs`, `MessagingServiceCollectionExtensions.cs`, `Messaging/ConsolidatedErrorTransportFilter.cs` — inherited resource labels + `skp-dlq-1` const
- `compose/otel-collector-config.yaml` — suffix + resource_to_telemetry_conversion behavior
- `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs`, `Orchestrator/MetricsRoundTripE2ETests.cs`, `Observability/Helpers/PrometheusTestClient.cs` — E2E facts to extend + scrape pattern
- `scripts/phase-33-close.ps1` — triple-SHA gate clone source
- `src/Messaging.Contracts/KeeperQueues.cs`, `Projections/L2ProjectionKeys.cs` — queue names + scratch-key pattern
- `Directory.Build.props` (net8.0, TreatWarningsAsErrors), `Directory.Packages.props` (OTel 1.15.3)

### Secondary (MEDIUM confidence)
- [learn.microsoft.com/dotnet/api/system.diagnostics.metrics.instrumentadvice-1](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.metrics.instrumentadvice-1) — `InstrumentAdvice<T>`/`HistogramBucketBoundaries`, net9+ moniker
- [learn.microsoft.com/dotnet/api/system.diagnostics.metrics.meter.createhistogram](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.metrics.meter.createhistogram?view=net-8.0) — CreateHistogram overloads; advice overload gated net9+
- [github.com/open-telemetry/opentelemetry-dotnet customizing-the-sdk](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/docs/metrics/customizing-the-sdk/README.md) — `AddView` + `ExplicitBucketHistogramConfiguration` (net8-safe bucket route)

### Tertiary (LOW confidence — flagged)
- WebSearch: "InstrumentAdvice introduced in DiagnosticSource 9.0.0" — corroborated by the docs moniker ranges; verify transitive version in Wave 0.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all packages verified in repo; only the histogram-advice version needs a one-command Wave 0 confirmation.
- Architecture / increment sites: HIGH — every CONTEXT.md-named site read and confirmed; 3 discrepancies surfaced precisely.
- Pitfalls / suffix mapping: HIGH for the rules (collector config documents them); MEDIUM for the exact `_seconds` outcome (one live scrape confirms).
- Close gate delta: HIGH — clone source fully mapped; `skp-dlq-1` correction verified.

**Research date:** 2026-06-06
**Valid until:** 2026-07-06 (stable; the only moving part is the transitive DiagnosticSource version, pinned by OTel 1.15.3)
