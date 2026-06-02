---
phase: 30-runtime-business-metrics
verified: 2026-06-03T00:00:00Z
status: human_needed
score: 6/7 must-haves verified
overrides_applied: 0
human_verification:
  - test: "Run: docker compose up -d (full stack including prometheus, orchestrator, processor-sample). Then: dotnet test tests/BaseApi.Tests -- --filter-class \"*MetricsRoundTripE2ETests\""
    expected: "Exit 0. All six PollPromForQuery assertions pass: orchestrator_dispatch_sent_total, orchestrator_result_consumed_total, processor_dispatch_consumed_total, processor_result_sent_total series exist for the exercised ProcessorId; the bottleneck PromQL evaluates numerically; a process_runtime_dotnet_* series carries a non-empty service_instance_id. Label assertions confirm ProcessorId + service_instance_id present, no workflowId, outcome ∈ {completed,failed,cancelled} on result_sent."
    why_human: "The live Prometheus scrape→query round-trip requires the full compose stack (prometheus container, running orchestrator + processor-sample with current code). The stack was not available during execution-phase verification and was honestly recorded as a Human-Verify item in 30-04-SUMMARY. The CODE for the E2E is verified as correct and builds 0/0 — only the live run is pending."
---

# Phase 30: Runtime & Business Metrics Verification Report

**Phase Goal:** Every service emits code-defined metrics carrying a per-replica `service_instance_id` label so that, across multiple orchestrator/processor replicas, PromQL can measure the rate of orchestrator→processor dispatch sending vs processor consuming (the per-processor bottleneck) and per-processor outcome rates — without high-cardinality workflow labels and without collector-side metric config.
**Verified:** 2026-06-03T00:00:00Z
**Status:** human_needed
**Re-verification:** No — initial verification.

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Both base libs resolve a per-replica instance id ONCE per process (POD_NAME → HOSTNAME → MachineName → GUID) and apply it as a service.instance.id resource attribute to BOTH the logs and metrics OTel resources. | ✓ VERIFIED | `ObservabilityServiceCollectionExtensions.cs:50-51` calls `ResolveInstanceId()` once into `instanceId`, then `AddAttributes(instanceAttrs)` on both `SetResourceBuilder` (line 62) and `.ConfigureResource` (line 72). `BaseConsoleObservabilityExtensions.cs:46-47` mirrors this exactly. Both `ResolveInstanceId()` helpers are byte-equivalent (lines 94-98 / 85-89). |
| 2 | Every metric (runtime, HTTP, custom) and every log carries a non-empty service_instance_id with no collector config change. | ✓ VERIFIED | `AddAttributes(instanceAttrs)` is applied to both resources in both base libs. `AddRuntimeInstrumentation()` + `AddAspNetCoreInstrumentation()` + `AddHttpClientInstrumentation()` are preserved unchanged. The collector config (`compose/otel-collector-config.yaml`) is unmodified: `resource_to_telemetry_conversion: enabled: true` is present (line 66-67), promoting `service.instance.id` → `service_instance_id` label. No `add_metric_suffixes: false` is present, so the default (append `_total`) holds. |
| 3 | The Orchestrator owns a DI-singleton OrchestratorMetrics built via IMeterFactory.Create("Orchestrator") exposing two Counter<long> named orchestrator_dispatch_sent and orchestrator_result_consumed; the "Orchestrator" meter is registered via ConfigureOpenTelemetryMeterProvider in Program.cs; counters increment tagged ProcessorId with no workflowId. | ✓ VERIFIED | `OrchestratorMetrics.cs`: `sealed`, `const string MeterName = "Orchestrator"`, `meterFactory.Create(MeterName)`, `CreateCounter<long>("orchestrator_dispatch_sent")`, `CreateCounter<long>("orchestrator_result_consumed")` — no `_total` in names. `Program.cs` lines 59-60: `AddSingleton<OrchestratorMetrics>()` + `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(OrchestratorMetrics.MeterName))`. `StepDispatcher.cs` line 33: `metrics.DispatchSent.Add(1, new KeyValuePair<string,object?>("ProcessorId", processorId.ToString("D")))` after `endpoint.Send`. `ResultConsumer.cs` line 52: `metrics.ResultConsumed.Add(1, new KeyValuePair<string,object?>("ProcessorId", m.ProcessorId.ToString("D")))` at top of Consume. No `workflowId` in any `.Add(` tag confirmed by grep. |
| 4 | BaseProcessor.Core owns a DI-singleton ProcessorMetrics built via IMeterFactory.Create("BaseProcessor") exposing two Counter<long> named processor_dispatch_consumed and processor_result_sent; the "BaseProcessor" meter registered via ConfigureOpenTelemetryMeterProvider inside AddBaseProcessor (not in BaseConsoleObservabilityExtensions); counters increment tagged ProcessorId (+ outcome on result_sent) with no workflowId. | ✓ VERIFIED | `ProcessorMetrics.cs`: `sealed`, `const string MeterName = "BaseProcessor"`, `meterFactory.Create(MeterName)`, `CreateCounter<long>("processor_dispatch_consumed")`, `CreateCounter<long>("processor_result_sent")` — no `_total` in names. `BaseProcessorServiceCollectionExtensions.cs` lines 114-115 (step 6c): `AddSingleton<ProcessorMetrics>()` + `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(ProcessorMetrics.MeterName))` inside `AddBaseProcessor`. Firewall held: grep finds NO `BaseProcessor` token in `BaseConsoleObservabilityExtensions.cs`. `EntryStepDispatchConsumer.cs` line 61-62: `metrics.DispatchConsumed.Add(1, new KVP("ProcessorId", context.Id!.Value.ToString("D")))` at top of Consume. `SendResult` (lines 219-226): `metrics.ResultSent.Add(1, KVP("ProcessorId",...), KVP("outcome", result.Outcome.ToString().ToLowerInvariant()))` after `endpoint.Send`. No `workflowId` in any `.Add(` call confirmed. |
| 5 | A PrometheusTestClient can poll localhost:9090/api/v1/query with exponential backoff; it has PollPromForQuery, VectorNonEmpty, HasNumericValue. | ✓ VERIFIED | `PrometheusTestClient.cs`: `BaseAddress = new Uri("http://localhost:9090/")` (line 43). `PollPromForQuery` method (lines 72-112): exponential backoff 200ms→3.2s, `Uri.EscapeDataString(promQL)`, status+predicate gate, `Clone()` detach. `VectorNonEmpty` static (lines 119-122). `HasNumericValue` static (lines 132-157). No `8889` in the file. Phase-11 `PollPrometheusUntilSumAtLeast`/`QueryPrometheus`/`SumSampleValues` preserved additively. |
| 6 | The MetricsRoundTripE2ETests E2E asserts all four business series, the by-ProcessorId bottleneck PromQL, the instance label on a runtime metric, and the label-shape (ProcessorId + service_instance_id, no workflowId, outcome on result_sent). The file builds 0/0 and is tagged RealStack so the hermetic suite excludes it. | ✓ VERIFIED (build only) | `MetricsRoundTripE2ETests.cs` exists; `[Trait("Category","RealStack")]` + `[Collection("Observability")]` present (lines 46-48). All four `_total` counter names present in PollPromForQuery calls. `sum by (ProcessorId)` bottleneck expression present (lines 136-139). `service_instance_id` asserted (lines 154-156). `AssertBusinessLabels` checks ProcessorId + service_instance_id non-empty, no workflowId (case-insensitive scan), outcome ∈ {completed,failed,cancelled}. No `8889` in file. Runtime broad selector `process_runtime_dotnet_.*` present (line 150). Build verified: 30-04-SUMMARY records `dotnet build tests/BaseApi.Tests -c Debug` = 0 Warning / 0 Error; hermetic suite 409/409. **Live Prometheus run NOT observed** — operator-runnable gate pending (see Human Verification). |
| 7 | The otel-collector metrics pipeline is unchanged (no new label injection, no new instrument definitions, no new processors). | ✓ VERIFIED | `compose/otel-collector-config.yaml` contains no changes from phase 30 work. The file has no `service_instance_id` injection rule, no new metric definitions. `resource_to_telemetry_conversion: enabled: true` was pre-existing (Phase 11). `git diff --quiet compose/otel-collector-config.yaml` confirmed exit 0 in both 30-01-SUMMARY and 30-04-SUMMARY. |

**Score:** 6/7 truths verified (7th is the live E2E run — code verified, live-stack proof is a human-verification item as documented in 30-04-SUMMARY).

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` | service.instance.id on logs + metrics; ResolveInstanceId helper | ✓ VERIFIED | Contains `service.instance.id`, `AddAttributes`, `ResolveInstanceId`, `POD_NAME`, `HOSTNAME`, `Environment.MachineName`. No `AddMeter("BaseProcessor")` or `AddMeter("Orchestrator")`. |
| `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` | service.instance.id on logs + metrics; ResolveInstanceId; MassTransit meter preserved | ✓ VERIFIED | Contains `service.instance.id`, `AddAttributes`, `ResolveInstanceId`, `POD_NAME`, `HOSTNAME`, `Environment.MachineName`. `InstrumentationOptions.MeterName` preserved (line 71). No `BaseProcessor` reference. |
| `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` | PollPromForQuery + VectorNonEmpty/HasNumericValue; localhost:9090 | ✓ VERIFIED | `localhost:9090`, `PollPromForQuery`, `Uri.EscapeDataString`, `VectorNonEmpty`, `HasNumericValue` all present. No `8889`. |
| `tests/BaseApi.Tests/Observability/ResolveInstanceIdFacts.cs` | Hermetic env-precedence test; POD_NAME, HOSTNAME present | ✓ VERIFIED | 3 facts; `POD_NAME`, `HOSTNAME`, `Environment.MachineName` all present; `finally` env restore. |
| `src/Orchestrator/Observability/OrchestratorMetrics.cs` | IMeterFactory holder; MeterName "Orchestrator"; two counters | ✓ VERIFIED | `meterFactory.Create(`, `const string MeterName = "Orchestrator"`, `orchestrator_dispatch_sent`, `orchestrator_result_consumed`. No `_total` in counter names. |
| `src/Orchestrator/Program.cs` | AddSingleton<OrchestratorMetrics> + ConfigureOpenTelemetryMeterProvider | ✓ VERIFIED | Lines 59-60 contain both registrations. `using Orchestrator.Observability;` and `using OpenTelemetry.Metrics;` present. |
| `src/Orchestrator/Dispatch/StepDispatcher.cs` | DispatchSent.Add after endpoint.Send; ProcessorId tag; no workflowId | ✓ VERIFIED | `OrchestratorMetrics metrics` in primary ctor (line 12). `metrics.DispatchSent.Add(1,...)` after `endpoint.Send` (line 33). Tag `"ProcessorId"` with `.ToString("D")`. No workflowId in `.Add(`. |
| `src/Orchestrator/Consumers/ResultConsumer.cs` | ResultConsumed.Add at top of Consume; ProcessorId tag; no workflowId | ✓ VERIFIED | `OrchestratorMetrics metrics` in primary ctor (line 43). `metrics.ResultConsumed.Add(1,...)` at top of Consume (line 52), before `store.TryGet`. Tag `"ProcessorId"` with `.ToString("D")`. No workflowId in `.Add(`. |
| `tests/BaseApi.Tests/Orchestrator/OrchestratorMetricsFacts.cs` | Hermetic D-02 const + non-null counter facts | ✓ VERIFIED | 2 facts; `MeterName == "Orchestrator"` and non-null counter construction from real `IMeterFactory`. |
| `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` | IMeterFactory holder; MeterName "BaseProcessor"; two counters | ✓ VERIFIED | `meterFactory.Create(`, `const string MeterName = "BaseProcessor"`, `processor_dispatch_consumed`, `processor_result_sent`. No `_total` in counter names. |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` | AddSingleton<ProcessorMetrics> + ConfigureOpenTelemetryMeterProvider inside AddBaseProcessor | ✓ VERIFIED | Lines 114-115 (step 6c block): `services.AddSingleton<ProcessorMetrics>()` + `services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(ProcessorMetrics.MeterName))`. `using OpenTelemetry.Metrics;` present (line 15). |
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` | DispatchConsumed at top of Consume; SendResult single owner with outcome tag; all send paths routed through it | ✓ VERIFIED | `ProcessorMetrics metrics` in primary ctor (line 47). `metrics.DispatchConsumed.Add(1,...)` lines 61-62 at top of Consume. Private `SendResult(ExecutionResult result)` method (lines 219-226) with `metrics.ResultSent.Add(1, KVP("ProcessorId",...), KVP("outcome",...))`. All four early returns use `await SendResult(...)` (lines 85, 100, 119, 132, 138). Final loop uses `await SendResult(er)` (line 200). No raw `endpoint.Send(er)` bypassing SendResult in the result loop. |
| `tests/BaseApi.Tests/Processor/ProcessorMetricsFacts.cs` | Hermetic D-02 const + non-null counter facts | ✓ VERIFIED | 2 facts; `MeterName == "BaseProcessor"` and non-null counter construction from real `IMeterFactory`. |
| `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` | RealStack E2E proving METRIC-01..06; PollPromForQuery; all four _total names; bottleneck PromQL; service_instance_id; no workflowId; outcome | ✓ VERIFIED (build) | All acceptance greps pass: `[Trait("Category","RealStack")]`, `PollPromForQuery`, all four `*_total` names, `sum by (ProcessorId)`, `service_instance_id`, `outcome`, NO `workflowId` or `8889`. Build 0/0. Live run pending (human-verify). |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `ObservabilityServiceCollectionExtensions.cs` | OTel logs + metrics resources | `AddAttributes([new("service.instance.id", instanceId)])` on both `SetResourceBuilder` and `ConfigureResource` | ✓ WIRED | Lines 62 and 72 both apply `instanceAttrs` containing `service.instance.id`. |
| `BaseConsoleObservabilityExtensions.cs` | OTel logs + metrics resources | `AddAttributes([new("service.instance.id", instanceId)])` on both `SetResourceBuilder` and `ConfigureResource` | ✓ WIRED | Lines 58 and 67 both apply `instanceAttrs` containing `service.instance.id`. |
| `StepDispatcher.cs` | `OrchestratorMetrics.DispatchSent` | ctor-injected metrics; `.Add(1, ProcessorId)` after `endpoint.Send` | ✓ WIRED | `OrchestratorMetrics metrics` in ctor; `metrics.DispatchSent.Add(1,...)` line 33, positioned after `endpoint.Send` line 27. |
| `ResultConsumer.cs` | `OrchestratorMetrics.ResultConsumed` | ctor-injected metrics; `.Add(1, ProcessorId)` at top of Consume | ✓ WIRED | `OrchestratorMetrics metrics` in ctor; `metrics.ResultConsumed.Add(1,...)` line 52, before `store.TryGet` line 55. |
| `Program.cs` | Orchestrator MeterProvider | `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(OrchestratorMetrics.MeterName))` | ✓ WIRED | Line 60 confirmed. |
| `EntryStepDispatchConsumer.cs` | `ProcessorMetrics.DispatchConsumed` | ctor-injected metrics; `.Add(1, ProcessorId)` at top of Consume | ✓ WIRED | `ProcessorMetrics metrics` in ctor line 47; `metrics.DispatchConsumed.Add(1,...)` lines 61-62 at top of Consume before any logic. |
| `EntryStepDispatchConsumer.cs` (SendResult) | `ProcessorMetrics.ResultSent` | `SendResult` single owner; `.Add(1, ProcessorId, outcome)` after `endpoint.Send` | ✓ WIRED | `SendResult` at lines 219-226; `metrics.ResultSent.Add(1,...)` after `endpoint.Send` (line 222); outcome from `result.Outcome.ToString().ToLowerInvariant()`. |
| `BaseProcessorServiceCollectionExtensions.cs` | Processor MeterProvider | `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(ProcessorMetrics.MeterName))` inside `AddBaseProcessor` | ✓ WIRED | Line 115 confirmed. Firewall held: no `BaseProcessor` in `BaseConsoleObservabilityExtensions.cs`. |
| `MetricsRoundTripE2ETests.cs` | Prometheus server :9090 | `PrometheusTestClient.PollPromForQuery` after live round-trip | ✓ WIRED (build) | `using var prom = new PrometheusTestClient()` line 113; six `PollPromForQuery` calls (lines 116-139); live-stack run pending. |
| `PrometheusTestClient.cs` | Prometheus server | `HttpClient BaseAddress http://localhost:9090/` | ✓ WIRED | `BaseAddress = new Uri("http://localhost:9090/")` line 43. |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `StepDispatcher.cs` | `metrics.DispatchSent` | `OrchestratorMetrics` DI-singleton; `IMeterFactory.Create("Orchestrator")` | Yes — increment fires on confirmed `endpoint.Send` | ✓ FLOWING |
| `ResultConsumer.cs` | `metrics.ResultConsumed` | `OrchestratorMetrics` DI-singleton | Yes — increment fires at top of Consume on every consumed result | ✓ FLOWING |
| `EntryStepDispatchConsumer.cs` | `metrics.DispatchConsumed` | `ProcessorMetrics` DI-singleton | Yes — increment fires at top of Consume on every consumed dispatch | ✓ FLOWING |
| `EntryStepDispatchConsumer.cs` | `metrics.ResultSent` | `ProcessorMetrics` DI-singleton; `SendResult` single owner | Yes — increment fires after confirmed `endpoint.Send` in SendResult; all send paths (4 early returns + result loop) route through it | ✓ FLOWING |

---

### Behavioral Spot-Checks

Step 7b SKIPPED for live stack tests — the live compose stack (prometheus container) is not running in this environment. Hermetic test behavior was verified by the execution phase:

| Behavior | Command (from SUMMARY) | Result | Status |
|----------|------------------------|--------|--------|
| ResolveInstanceIdFacts pass hermetically | `dotnet test ... --filter-class "*ResolveInstanceIdFacts"` | 3/3 GREEN (30-01-SUMMARY) | ✓ PASS |
| OrchestratorMetricsFacts pass hermetically | `dotnet test ... --filter-class "*OrchestratorMetricsFacts"` | 2/2 GREEN (30-02-SUMMARY) | ✓ PASS |
| ProcessorMetricsFacts pass hermetically | `dotnet test ... --filter-class "*ProcessorMetricsFacts"` | 2/2 GREEN (30-03-SUMMARY) | ✓ PASS |
| Full hermetic suite (Category!=RealStack) | `dotnet test ... --filter-not-trait "Category=RealStack"` | 409/409 (30-03/04-SUMMARY) | ✓ PASS |
| MetricsRoundTripE2ETests builds | `dotnet build tests/BaseApi.Tests -c Debug` | 0 Warning / 0 Error (30-04-SUMMARY) | ✓ PASS |
| MetricsRoundTripE2ETests live run | Full compose stack required | NOT OBSERVED | ? SKIP (human-verify) |

---

### Requirements Coverage

| Requirement | Source Plans | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| METRIC-01 | 30-01, 30-04 | `service.instance.id` resource attribute in both base libs, all process types, all signals, no collector change | ✓ SATISFIED | `AddAttributes(instanceAttrs)` on both logs + metrics resources in both `ObservabilityServiceCollectionExtensions.cs` and `BaseConsoleObservabilityExtensions.cs`; `ResolveInstanceId()` present in both. Live proof pending (E2E). |
| METRIC-02 | 30-01, 30-04 | All three process types emit .NET runtime metrics (AddRuntimeInstrumentation) carrying service_instance_id | ✓ SATISFIED | `AddRuntimeInstrumentation()` preserved in both base libs. `service.instance.id` on metrics resource in both. WebApi, Orchestrator, Processor.* all use one of the two base libs. |
| METRIC-03 | 30-01, 30-04 | WebApi ASP.NET Core HTTP server metrics carry service_instance_id | ✓ SATISFIED | `AddAspNetCoreInstrumentation()` preserved in `ObservabilityServiceCollectionExtensions.cs` line 79. `service.instance.id` now on that metrics resource. |
| METRIC-04 | 30-02 | Orchestrator code-owned Meter with orchestrator_dispatch_sent_total + orchestrator_result_consumed_total, labelled ProcessorId, registered via AddMeter, no workflowId | ✓ SATISFIED | `OrchestratorMetrics.cs`, `StepDispatcher.cs`, `ResultConsumer.cs`, `Program.cs` all verified. No workflowId in `.Add(` calls confirmed. |
| METRIC-05 | 30-03 | BaseProcessor.Core code-owned Meter with processor_dispatch_consumed_total + processor_result_sent_total (labelled ProcessorId + outcome), registered via AddMeter inside AddBaseProcessor, no workflowId, no processing outcome | ✓ SATISFIED | `ProcessorMetrics.cs`, `EntryStepDispatchConsumer.cs`, `BaseProcessorServiceCollectionExtensions.cs` all verified. `outcome` is `result.Outcome.ToString().ToLowerInvariant()` from StepOutcome enum; build paths never produce Processing. Firewall held. |
| METRIC-06 | 30-04 | Counters align by ProcessorId for bottleneck PromQL; proven by real-stack assertion | ✓ SATISFIED (code) / ? PENDING (live) | `MetricsRoundTripE2ETests.cs` has the correct bottleneck PromQL (lines 136-139) and all assertions. Build verified 0/0. Live-stack run is the human-verify item. |
| METRIC-07 | 30-01, 30-04 | Metric definitions in application code only; collector pipeline NOT modified | ✓ SATISFIED | `compose/otel-collector-config.yaml` unchanged throughout phase. `git diff --quiet` confirmed exit 0. No new processors, label injections, or instrument definitions in the collector. `resource_to_telemetry_conversion: enabled: true` is pre-existing (Phase 11). |

All 7 METRIC requirements are mapped to Phase 30 plans. No orphaned requirements. REQUIREMENTS.md traceability table marks METRIC-01..07 as Complete at Phase 30.

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| (none found) | — | — | — | — |

Specific checks performed:
- No `workflowId` in metric `.Add(` calls in StepDispatcher, ResultConsumer, or EntryStepDispatchConsumer.
- No `_total` in counter name strings in OrchestratorMetrics.cs or ProcessorMetrics.cs.
- No `8889` in PrometheusTestClient.cs or MetricsRoundTripE2ETests.cs.
- No `BaseProcessor` token in BaseConsoleObservabilityExtensions.cs (compile firewall held).
- No `static Meter` field in either metrics holder (both use `IMeterFactory`).
- No raw `endpoint.Send(er)` loop bypassing `SendResult` in EntryStepDispatchConsumer — all paths route through the single owner.
- No `"processing"` literal in outcome tags in EntryStepDispatchConsumer.
- No `AddMeter("BaseProcessor")` or `AddMeter("Orchestrator")` in either base-lib observability extension.

---

### Human Verification Required

#### 1. MetricsRoundTripE2ETests — Live Prometheus Round-Trip

**Test:** Bring up the full compose stack:
```
docker compose up -d
```
Ensure `prometheus`, `orchestrator`, and `processor-sample` containers are healthy. Allow at least one Prometheus scrape cycle (15s). Then run:
```
dotnet test tests/BaseApi.Tests -- --filter-class "*MetricsRoundTripE2ETests"
```

**Expected:** Exit 0. The single `LiveRoundTrip_ProvesBusinessSeries_BottleneckPromQL_AndInstanceLabel` test passes. Specifically:
- `orchestrator_dispatch_sent_total{ProcessorId="<procId>"}` returns a non-empty vector from Prometheus.
- `orchestrator_result_consumed_total{ProcessorId="<procId>"}` returns a non-empty vector.
- `processor_dispatch_consumed_total{ProcessorId="<procId>"}` returns a non-empty vector.
- `processor_result_sent_total{ProcessorId="<procId>"}` returns a non-empty vector.
- The bottleneck PromQL `sum by (ProcessorId)(rate(orchestrator_dispatch_sent_total{...}[5m])) - sum by (ProcessorId)(rate(processor_dispatch_consumed_total{...}[5m]))` evaluates to a numeric result.
- A `process_runtime_dotnet_*` series carries a non-empty `service_instance_id`.
- Business counter labels: `ProcessorId` and `service_instance_id` non-empty; no `workflowId`; `processor_result_sent_total` carries `outcome` ∈ {completed, failed, cancelled}.

**Why human:** The live Prometheus scrape→query round-trip requires the full docker compose stack running current processor-sample code. This was explicitly not run during execution-phase verification and was documented as a Human-Verify item in `30-04-SUMMARY.md`. The E2E code is correct (builds 0/0, all acceptance greps pass); only the live-stack execution is pending. This is the pattern established by Phases 28/29 where the real-stack proof runs via the operator-authorized close gate.

---

### Gaps Summary

No blocking gaps. All seven requirements have correct code in the source tree. The only pending item is the live Prometheus scrape assertion in `MetricsRoundTripE2ETests`, which requires the full compose stack and is designated a human-verification item per the phase context instructions and the milestone's established operator close-gate pattern.

---

_Verified: 2026-06-03T00:00:00Z_
_Verifier: Claude (gsd-verifier)_
