---
phase: 38-metrics-service-instance-labels
verified: 2026-06-06T00:00:00Z
status: passed
score: 5/5 must-haves verified
overrides_applied: 0
gaps: []
human_verification: []
---

# Phase 38: Uniform `service_name` + Instance Labels Across All Metrics — Verification Report

**Phase Goal:** Every Prometheus metric series (runtime, HTTP, and business instruments) for all four consoles carries a human-distinguishable `service_name = {name}_{version}` label plus a non-empty `service_instance_id` label — where the processor's `{name}_{version}` is sourced from the DATABASE (the single source of truth), not appsettings. Logs service.name unchanged. Prometheus query consumers updated. No live operator verification required (hermetic + scrape-assertion provable).

**Verified:** 2026-06-06
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Combined `service_name={name}_{version}` on all metric series (MLBL-01) | VERIFIED | `$"{serviceName}_{serviceVersion}"` in `ConfigureResource` in both `BaseConsoleObservabilityExtensions.cs:70` and `ObservabilityServiceCollectionExtensions.cs:75`; the LOGS `SetResourceBuilder` blocks at lines 56-58 and 61-63 are bare (unchanged). Exactly 1 hit per file inside the metrics block. RealStack scrape confirmed: sk-api_3.2.0 (15 series), orchestrator_3.4.0 (business+runtime), keeper inherits same code path. |
| 2 | Non-empty `service_instance_id` on runtime, HTTP, and business families (MLBL-02) | VERIFIED | `MetricsRoundTripE2ETests.cs` asserts `TryGetNonEmpty(runtimeMetric, "service_instance_id")`, `TryGetNonEmpty(httpMetric, "service_instance_id")`, and `TryGetNonEmpty(orchMetric, "service_instance_id")` — all three families. Test passed 1/1 RealStack (scrape evidence: USERPC, f2735eda5f87, fed831ef1cac). `AssertBusinessLabels` also asserts non-empty `service_instance_id` on all business series. |
| 3 | Processor `service_name` sourced from DB — contract, responder, context, MeterProvider swap (MLBL-03) | VERIFIED | (i) `ProcessorQueries.cs:8-10`: `ProcessorIdentityFound` extended with `string Name, string Version`. (ii) `GetProcessorBySourceHashConsumer.cs:26`: `new ProcessorIdentityFound(p.Id, p.InputSchemaId, p.OutputSchemaId, p.ConfigSchemaId, p.Name, p.Version)`. (iii) `IProcessorContext.cs:48-52` + `ProcessorContext.cs:45-48,69-70`: `Name`/`Version` stored in `SetIdentity`. (iv) `MeterProviderHolder.cs`: 83-line sealed class; `SwapTo` implements 4-step body (Build#2→repoint→ForceFlush→Dispose#1); bare OTLP exporter; captured `_instanceId` reused. (v) `ProcessorStartupOrchestrator.cs:89`: `meterProviderHolder.SwapTo($"{found.Message.Name}_{found.Message.Version}")` after `SetIdentity`, before `break`/MarkHealthy. (vi) `appsettings.json:9-12` RETAINS `Service:Name=processor-sample`, `Service:Version=3.5.0` (GA-3). (vii) `MeterProviderHolderFacts` hermetic test asserts placeholder→DB swap + same instance id + dispose idempotency. (viii) RealStack: DB-sourced `sample-proc-8ca4608c07744158b90d586533752433_1.0.0` confirmed; placeholder series `processor-sample_3.5.0` count=0 on business counter post-swap. |
| 4 | Logs `service.name` stays bare identity — no `_{version}` suffix (MLBL-04) | VERIFIED | Both base libs' `SetResourceBuilder` blocks use bare `serviceName` (no interpolation). `LogsResourceBareNameFacts.cs` hermetic guard: asserts `service.name == "keeper"` and `DoesNotContain("_3.7.0")`. Build confirmed: `_{serviceVersion}` never appears inside a `SetResourceBuilder` call in `src/`. |
| 5 | All in-repo PromQL consumers updated to combined literal; zero bare service_name for the four services (MLBL-05) | VERIFIED | Grep `service_name="(sk-api|orchestrator|keeper|processor-sample)"` over `tests/**/*.cs` → 0 hits. `MetricsExportTests.cs`: 3 query literals + comment → `sk-api_3.2.0`. `SchemasMetricsE2ETests.cs`: 1 literal + 2 doc comments → `sk-api_3.2.0`. `PrometheusTestClient.cs`: doc-comment narrative updated. `MetricsRoundTripE2ETests.cs`: 4 new combined-literal queries added (sk-api_3.2.0, orchestrator_3.4.0 ×2, dynamic procServiceName). No recording/alert rules. No Grafana dashboards. |

**Score:** 5/5 truths verified

---

## MLBL-* Requirement Verdict Table

| Req | Description | Status | Key Evidence |
|-----|-------------|--------|--------------|
| MLBL-01 | Combined `service_name={name}_{version}` on all metric series | PASSED | `$"{serviceName}_{serviceVersion}"` in `ConfigureResource` blocks of both base libs; RealStack scrape GREEN; zero bare literals remain |
| MLBL-02 | Non-empty `service_instance_id` on runtime, HTTP, and business families | PASSED | `MetricsRoundTripE2ETests` asserts all 3 families; 1/1 RealStack GREEN with non-empty values on each |
| MLBL-03 | Processor `service_name` from DB; appsettings retained as boot-window placeholder | PASSED | `ProcessorIdentityFound` extended; responder populates `p.Name/p.Version`; `MeterProviderHolder` A1 swap wired after `SetIdentity`; appsettings keys retained; hermetic + RealStack evidence |
| MLBL-04 | Logs `service.name` stays bare (no `_{version}` suffix) | PASSED | `SetResourceBuilder` blocks untouched in both base libs; `LogsResourceBareNameFacts` hermetic guard passes |
| MLBL-05 | All PromQL consumers updated; zero bare literals | PASSED | grep gate returns 0 bare hits; 4 test files updated; no rules/dashboards; D-08 exact literals used throughout |

---

## Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Messaging.Contracts/ProcessorQueries.cs` | `ProcessorIdentityFound` with `string Name, string Version` | VERIFIED | Lines 8-10: positional params appended after 4 existing params |
| `src/BaseApi.Service/Features/Processor/Responders/GetProcessorBySourceHashConsumer.cs` | Populates `p.Name, p.Version` | VERIFIED | Line 26: `new ProcessorIdentityFound(p.Id, p.InputSchemaId, p.OutputSchemaId, p.ConfigSchemaId, p.Name, p.Version)` |
| `src/BaseProcessor.Core/Identity/IProcessorContext.cs` | `string? Name { get; }` + `string? Version { get; }` | VERIFIED | Lines 48-52: both members present with WR-03 XML-doc, listed in the memory-visibility invariant paragraph |
| `src/BaseProcessor.Core/Identity/ProcessorContext.cs` | Auto-props + `SetIdentity` storage | VERIFIED | Lines 45-48: auto-props; lines 69-70: `Name = identity.Name; Version = identity.Version;` |
| `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` | Combined metrics service.name; bare logs | VERIFIED | Line 70: `AddService(serviceName: $"{serviceName}_{serviceVersion}", ...)`; line 57: logs block bare |
| `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` | Combined metrics service.name; bare logs | VERIFIED | Line 75: same interpolated pattern; line 62: logs block bare |
| `src/BaseProcessor.Core/Observability/MeterProviderHolder.cs` | Sealed class; `SwapTo`; 4-step body; bare OTLP; captured instance id | VERIFIED | 83-line file; `SwapTo` at line 71; 4-step order at lines 73-77; bare `.AddOtlpExporter()` at line 63; `_instanceId` reused at line 58 |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` | `AddSingleton<MeterProviderHolder>` | VERIFIED | Lines 133-139: factory lambda; `sp.GetRequiredService<MeterProvider>()` + `cfg.Require("Service:Version")` + `ResolveInstanceIdForHolder()` |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` | `MeterProviderHolder` ctor param; `SwapTo` after `SetIdentity` | VERIFIED | Line 58: ctor param; line 89: `meterProviderHolder.SwapTo($"{found.Message.Name}_{found.Message.Version}")` immediately after `SetIdentity` at line 83, before `break` at line 92 |
| `src/Processor.Sample/appsettings.json` | RETAINS `Service:Name` + `Service:Version` (GA-3) | VERIFIED | Lines 9-12: `"Name": "processor-sample"`, `"Version": "3.5.0"` — not removed |
| `tests/BaseApi.Tests/Observability/LogsResourceBareNameFacts.cs` | Hermetic guard asserting bare `service.name` | VERIFIED | File exists; `Assert.Equal("keeper", serviceName)` + `Assert.DoesNotContain("_3.7.0", ...)` |
| `tests/BaseApi.Tests/Observability/MeterProviderHolderFacts.cs` | Hermetic swap proof | VERIFIED | File exists; asserts `PlaceholderName`→`DbName` swap, `InstanceId` preserved, sentinel disposed, double-dispose safe |
| `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` | RealStack family assertions; dynamic DB-sourced processor label | VERIFIED | Lines 184-220: 4 MLBL assertions (processor DB-sourced, HTTP sk-api_3.2.0 + instance id, orchestrator_3.4.0 + instance id, runtime named); `SeedProcessorAsync` returns `(Id, Name, Version)` |
| `tests/BaseApi.Tests/Messaging/ProcessorResponderTests.cs` | Asserts `found.Message.Name` / `.Version` | VERIFIED | Lines 112-113: `Assert.Equal("seed", found.Message.Name)` + `Assert.Equal("1.0.0", found.Message.Version)` |

---

## Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `GetProcessorBySourceHashConsumer.cs` | `ProcessorIdentityFound` | `p.Name, p.Version` in ctor call | WIRED | Line 26 confirmed |
| `ProcessorContext.SetIdentity` | `ProcessorContext.Name/Version` props | `Name = identity.Name; Version = identity.Version;` | WIRED | Lines 69-70 confirmed |
| `ProcessorStartupOrchestrator` Loop A | `MeterProviderHolder.SwapTo` | `meterProviderHolder.SwapTo($"{found.Message.Name}_{found.Message.Version}")` after `SetIdentity`, before `break` | WIRED | Line 89 confirmed; ordering: SetIdentity(83) → SwapTo(89) → LogInformation(90) → break(92) |
| `AddBaseProcessor` | `MeterProviderHolder` singleton | `services.AddSingleton<MeterProviderHolder>(sp => ...)` | WIRED | Line 133 confirmed |
| Metrics `ConfigureResource` | `service_name` Prom label | `AddService(serviceName: $"{serviceName}_{serviceVersion}", ...)` | WIRED | Both base libs confirmed; logs `SetResourceBuilder` stays bare |
| `MetricsExportTests` query literals | Prometheus | `service_name="sk-api_3.2.0"` | WIRED | 3 query literals confirmed; grep gate clean |
| `MetricsRoundTripE2ETests` | Prometheus `:9090` | Combined service_name selectors across families | WIRED | 4 MLBL assertions; dynamic `procServiceName` built from seeded row |

---

## Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `MeterProviderHolder.Build` | `service.name` resource attr | `resolvedServiceName` param ← `SwapTo` ← `found.Message.Name`/`.Version` ← `GetProcessorBySourceHashConsumer` ← DB `ProcessorEntity.Name`/`.Version` | Yes — DB row via ORM | FLOWING |
| `BaseConsoleObservabilityExtensions` metrics block | `service.name` resource attr | `cfg.Require("Service:Name")` + `cfg.Require("Service:Version")` → interpolated | Yes — appsettings (static, validated) | FLOWING |
| `ObservabilityServiceCollectionExtensions` metrics block | `service.name` resource attr | Same pattern | Yes | FLOWING |
| Both base libs logs blocks | `service.name` resource attr | `serviceName` bare (no interpolation) | Yes — intentionally bare (MLBL-04) | FLOWING |

---

## Behavioral Spot-Checks

| Behavior | Evidence Source | Result | Status |
|----------|----------------|--------|--------|
| Combined metric service.name in BaseConsole | Read `BaseConsoleObservabilityExtensions.cs:70` | `AddService(serviceName: $"{serviceName}_{serviceVersion}", ...)` present in metrics block only | PASS |
| Combined metric service.name in BaseApi | Read `ObservabilityServiceCollectionExtensions.cs:75` | Same pattern; logs block at line 62 bare | PASS |
| Bare logs service.name guarded hermetically | `LogsResourceBareNameFacts` test file verified | `Assert.Equal("keeper", ...)` + `DoesNotContain("_3.7.0")` | PASS |
| MeterProvider swap wired in Loop A | `ProcessorStartupOrchestrator.cs:89` | `SwapTo(...)` after `SetIdentity`, before `break` | PASS |
| Zero bare service_name query literals | grep `service_name="(sk-api|orchestrator|keeper|processor-sample)"` over tests/ | 0 hits | PASS |
| Appsettings keys retained (GA-3) | Read `src/Processor.Sample/appsettings.json:9-12` | `Service:Name=processor-sample`, `Service:Version=3.5.0` present | PASS |
| RealStack gate GREEN (hermetic 479/479 + live 1/1) | `38-04-SUMMARY.md` scrape evidence | sk-api_3.2.0 (15 series), orchestrator_3.4.0, DB-sourced processor service_name confirmed; placeholder count=0 | PASS |

---

## Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| MLBL-01 | Plans 02, 04 | Combined `service_name={name}_{version}` on all metric series | SATISFIED | Both base libs combine in `ConfigureResource`; RealStack scrape confirmed |
| MLBL-02 | Plan 04 | Non-empty `service_instance_id` on runtime, HTTP, and business families | SATISFIED | All 3 families asserted in `MetricsRoundTripE2ETests`; 1/1 RealStack GREEN |
| MLBL-03 | Plans 01, 03, 04 | Processor service_name from DB; appsettings retained; MeterProvider swap | SATISFIED | Full round-trip: contract→responder→context→swap; hermetic + RealStack proof |
| MLBL-04 | Plans 02, 04 | Logs service.name stays bare | SATISFIED | `SetResourceBuilder` blocks untouched; `LogsResourceBareNameFacts` hermetic guard |
| MLBL-05 | Plans 02, 04 | All PromQL consumers updated; zero bare literals | SATISFIED | grep gate: 0 bare hits; 4 test files updated with exact literals |

---

## Anti-Patterns Found

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| None | — | — | — |

No TODOs, placeholder comments, stub implementations, empty handlers, or hardcoded empty data found in the phase-38 modified files. The placeholder `Name`/`Version` values in test fixtures (`"proc"`, `"1.0.0"` in SchemaResolutionFacts/ProcessorTestHarness) are test-double inputs that do not flow to rendering — not stubs. `StubMeterProviderHolder()` in `IdentityResolutionFacts` is a real holder over a real provider that exercises the production swap path.

---

## Human Verification Required

None — the phase explicitly excludes live operator verification (SPEC constraint: "No live operator verification required — hermetic + scrape-assertion provable"). All acceptance criteria are verifiable programmatically and have been verified via:

- Hermetic suite: 479 passed / 0 failed (exit 0)
- RealStack MetricsRoundTripE2ETests: 1/1 PASSED with direct Prometheus `:9090` scrape evidence
- grep gate: 0 bare service_name literals
- All 9 phase commits present in git history

---

## Gaps Summary

No gaps. All 5 MLBL requirements are SATISFIED with code present, substantive, wired, and data flowing. The phase goal is fully achieved.

---

_Verified: 2026-06-06_
_Verifier: Claude (gsd-verifier)_
