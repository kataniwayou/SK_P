---
phase: 38-metrics-service-instance-labels
plan: 03
subsystem: processor-observability
tags: [opentelemetry, metrics, meterprovider, service_name, swap, MLBL-03]

# Dependency graph
requires:
  - phase: 38-metrics-service-instance-labels
    plan: 01
    provides: "ProcessorIdentityFound.Name/.Version (read at the swap call-site as found.Message.Name/.Version); IProcessorContext/ProcessorContext expose Name/Version"
  - phase: 38-metrics-service-instance-labels
    plan: 02
    provides: "the {name}_{version} combine pattern reused for the DB-sourced metrics resource"
  - phase: 30-runtime-business-metrics
    provides: "the single-resolve service.instance.id invariant (Phase 30 D-10) preserved across the swap"
provides:
  - "MeterProviderHolder (Model A1): a sealed singleton owning the placeholder->DB MeterProvider swap (build #2 -> repoint -> ForceFlush -> Dispose #1)"
  - "Loop-A swap trigger: meterProviderHolder.SwapTo($\"{found.Message.Name}_{found.Message.Version}\") after SetIdentity, before MarkHealthy"
  - "hermetic MeterProviderHolderFacts proving placeholder->DB service.name swap + instance-id preservation + A1 dispose idempotency"
affects: [38-04, phase-39-keeper-metrics, processor-prometheus-consumers]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Standalone Sdk.CreateMeterProviderBuilder() provider owned outside the host DI lifetime (the holder owns provider #2; provider #1 is the host's, disposed at swap — A1 double-dispose-safe)"
    - "Build-before-dispose swap ordering for race-safety: _current is ALWAYS a live provider"
    - "Hermetic MeterProvider resource inspection via the public MeterProvider.GetResource() extension (metrics analog of the logs ParentProvider.GetResource())"
    - "Provider-disposal proof via a custom MetricReader whose OnShutdown flips a flag when MeterProvider.Dispose() shuts the pipeline down"

key-files:
  created:
    - src/BaseProcessor.Core/Observability/MeterProviderHolder.cs
    - tests/BaseApi.Tests/Observability/MeterProviderHolderFacts.cs
  modified:
    - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
    - src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
    - tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs
    - tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs

key-decisions:
  - "Model A1 (per plan): the holder owns ONLY provider #2; the host builds provider #1 via the unchanged shared AddBaseConsoleObservability path (D-06); the holder disposes #1 at swap; the DI container disposes #1 again at shutdown — a safe no-op (MeterProvider.Dispose is idempotent)."
  - "Instance-id capture: ResolveInstanceIdForHolder() in AddBaseProcessor reads the identical POD_NAME??HOSTNAME??MachineName??GUID precedence INLINE (the shared path resolves it once but does not expose it) — documented as the 4th IN-03 drift site, kept in lock-step with the 3 existing copies."
  - "Hermetic disposal proof: a custom MetricReader (ShutdownSentinelReader) on provider #1 flips a flag on OnShutdown; SwapTo's Dispose(#1) fires it. provider #2 read off the holder's private _current via reflection (the only seam — no public getter)."

patterns-established:
  - "MeterProviderHolderFacts: build a placeholder host provider #1, construct the real holder, SwapTo a DB name, assert service.name swapped + service.instance.id preserved + #1 shut down + double-dispose safe"

requirements-completed: [MLBL-03]

# Metrics
duration: ~18min
completed: 2026-06-06
---

# Phase 38 Plan 03: Processor MeterProvider Swap (DB-sourced metrics service_name) Summary

**The processor's metric `service_name` now swaps from the appsettings placeholder (`processor-sample_3.5.0`) to the DB-sourced `{Name}_{Version}` the instant Loop A resolves identity — via a sealed `MeterProviderHolder` (Model A1) that builds a standalone provider #2 and disposes the host's #1, reusing the single resolved `service.instance.id` across the swap.**

## Performance

- **Duration:** ~18 min
- **Tasks:** 3
- **Files:** 7 (2 created + 5 modified)

## Accomplishments

- **MeterProviderHolder (A1) — the one non-trivial mechanism in the phase.** A `public sealed class MeterProviderHolder : IDisposable` that receives the host's provider #1 via ctor and owns the swap. `SwapTo(resolvedServiceName)` runs the load-bearing 4-step body: `Build(#2)` -> `_current = next` (repoint) -> `prior.ForceFlush(5000)` -> `prior.Dispose()`. `Build` uses `Sdk.CreateMeterProviderBuilder()` with `.AddMeter(ProcessorMetrics.MeterName)` + `.AddMeter(InstrumentationOptions.MeterName)` + `.AddRuntimeInstrumentation()` + a **bare** `.AddOtlpExporter()` (inherits `OTEL_EXPORTER_OTLP_ENDPOINT`), and a resource carrying the combined `service.name` + the captured `service.instance.id`.
- **Registration + swap trigger.** `AddBaseProcessor` registers `AddSingleton<MeterProviderHolder>` constructed from `sp.GetRequiredService<MeterProvider>()` (provider #1), `cfg.Require("Service:Version")`, and `ResolveInstanceIdForHolder()` (the documented 4th IN-03 capture). `ProcessorStartupOrchestrator` gained a `MeterProviderHolder` ctor param and fires `meterProviderHolder.SwapTo($"{found.Message.Name}_{found.Message.Version}")` in Loop A immediately after `context.SetIdentity(...)` and before the `{id:D}` queue-bind / `MarkHealthy` — race-safe (dispatch counters can't fire pre-bind; the heartbeat writes Redis only).
- **Hermetic MeterProviderHolderFacts.** Builds a placeholder host provider #1 (`service.name=processor-sample_3.5.0`, `service.instance.id=unit-instance-42`), constructs the real holder, calls `SwapTo("db-name_9.9.9")`, and asserts: provider #2 `service.name == db-name_9.9.9` with the SAME `service.instance.id`; provider #1 was shut down (reader `OnShutdown` sentinel); a second `Dispose()` does not throw. No compose stack, no live boot-window race.

## Task Commits

1. **Task 1: MeterProviderHolder (A1)** — `69bbd55` (feat)
2. **Task 2: register holder + fire SwapTo in Loop A** — `ef209d0` (feat)
3. **Task 3: hermetic MeterProviderHolderFacts** — `e41a475` (test)

## Files Created/Modified

- `src/BaseProcessor.Core/Observability/MeterProviderHolder.cs` — NEW; the A1 holder (ctor takes host provider #1 + captured instance id + appsettings version; `Build` + `SwapTo` + `Dispose`).
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` — `AddSingleton<MeterProviderHolder>(...)` from the host `MeterProvider` + `Service:Version` + captured id; new private `ResolveInstanceIdForHolder()` (4th IN-03 drift site); added `using BaseConsole.Core.Configuration;` for `cfg.Require`.
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — `MeterProviderHolder` primary-ctor param; `SwapTo(...)` call after `SetIdentity`, before `break`; added `using BaseProcessor.Core.Observability;`.
- `tests/BaseApi.Tests/Observability/MeterProviderHolderFacts.cs` — NEW hermetic swap proof.
- `tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs` — new shared `internal static StubMeterProviderHolder()` helper (real holder over a minimal hermetic host provider) + the new ctor arg at its own call-site; OTel usings added.
- `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs` + `DispatchBindSequenceFacts.cs` — pass `IdentityResolutionFacts.StubMeterProviderHolder()` at the orchestrator construction sites.

## Decisions Made

See `key-decisions` in the frontmatter (Model A1 ownership; inline instance-id capture as the 4th IN-03 drift site; reader-sentinel + reflection seam for the hermetic test). All within plan scope — A1 was the plan's resolved ownership model.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] `MeterProvider.ForceFlush` parameter name + XML cref**
- **Found during:** Task 1 (first Release build of BaseProcessor.Core).
- **Issue:** The plan's holder body used `ForceFlush(milliseconds: 5000)` and `<see cref="MeterProvider.Dispose()"/>`. In OTel 1.15.3 the parameter is `timeoutMilliseconds` (CS1739), and `MeterProvider.Dispose()` is inherited (not declared on the type) so the cref did not resolve (CS1574, which is an error under `GenerateDocumentationFile` + TreatWarningsAsErrors).
- **Fix:** `ForceFlush(timeoutMilliseconds: 5000)` and changed the cref to plain `<c>MeterProvider.Dispose()</c>`.
- **Files modified:** `src/BaseProcessor.Core/Observability/MeterProviderHolder.cs`.
- **Commit:** `69bbd55`.

**2. [Rule 3 - Blocking] Three orchestrator-construction test sites broken by the new ctor param**
- **Found during:** Task 2 (full-solution build after adding the `MeterProviderHolder` ctor param).
- **Issue:** `IdentityResolutionFacts.cs`, `SchemaResolutionFacts.cs`, and `DispatchBindSequenceFacts.cs` construct `ProcessorStartupOrchestrator` directly (CS7036). These tests drive the orchestrator THROUGH identity-resolve, so the swap **fires** — a null holder would NRE.
- **Fix:** Added a shared `internal static MeterProviderHolder StubMeterProviderHolder()` in `IdentityResolutionFacts` that builds a minimal hermetic host provider #1 (placeholder resource) and returns a REAL holder, so the production `SwapTo`/`Build` path executes under test (building provider #2 does not open a live OTLP connection; ForceFlush/Dispose are safe with no collector). Wired it at all three call-sites.
- **Files modified:** `tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs`, `SchemaResolutionFacts.cs`, `DispatchBindSequenceFacts.cs`.
- **Commit:** `ef209d0`.

No architectural changes. No auth gates. No file deletions (commits are pure additions/edits).

## Authentication Gates

None.

## Known Stubs

None. `StubMeterProviderHolder()` is a test-double factory that constructs the REAL `MeterProviderHolder` over a minimal real host provider (it exercises the production swap path), not a no-op stub. The production instance-id capture (`ResolveInstanceIdForHolder`) is the real env-precedence read; its end-to-end runtime equality (#1 id == #2 id) is exercised by Plan 04's RealStack E2E, while the hermetic test proves the equality with an explicit known id.

## TDD Gate Compliance

Task 3 is `tdd="true"`. The SUT (`MeterProviderHolder`) was built in Task 1, so `MeterProviderHolderFacts` was authored GREEN-by-construction rather than RED-first — but its assertions have real teeth: they exercise the actual `Build`/`SwapTo` path and would go RED if any of build-#2 / repoint / Dispose-#1 / instance-id-reuse were absent or broken (distinct service.name values, a distinct provider-#2 instance, and the reader-shutdown sentinel each fail independently if the swap regresses). The Task-3 commit is a `test(...)` commit; the prior `feat(...)` commits (`69bbd55`, `ef209d0`) provide the GREEN implementation.

## Threat Surface Scan

No new security-relevant surface beyond the plan's `<threat_model>`. T-38-06 (provider #1 OTLP gRPC channel leak on swap) is **mitigated**: `SwapTo` calls `ForceFlush(5000)` then `Dispose()` on provider #1 (exactly one swap per process); the hermetic test proves the shutdown fires. T-38-07 (instance-id divergence) is **mitigated**: the captured id is reused for provider #2; the test asserts the SAME `service.instance.id` before/after. T-38-05 (DB `{Name}_{Version}` -> Prom label cardinality) remains **accepted** (bounded by the write-path FluentValidation — no new input surface added here).

## Verification Evidence

- `dotnet build SK_P.sln -c Release` — **Build succeeded, 0 Warning(s), 0 Error(s)**.
- `dotnet build SK_P.sln -c Debug` — **Build succeeded, 0 Warning(s), 0 Error(s)**.
- `BaseApi.Tests.exe --filter-class "*MeterProviderHolderFacts*"` — **Passed! total: 1, failed: 0** (hermetic, no compose stack).
- `BaseApi.Tests.exe --filter-class "*IdentityResolutionFacts*" "*SchemaResolutionFacts*" "*DispatchBindSequenceFacts*"` — **Passed! total: 5, failed: 0** (the real `SwapTo` fires through each orchestrator drive with no regression).
- `BaseApi.Tests.exe --filter-namespace "BaseApi.Tests.Observability" --filter-not-trait "Category=RealStack"` — **Passed! total: 27, failed: 0** (26 prior + the new 1; RealStack live-Prom E2E excluded — operator/Phase-39 live gate). Note: MassTransit RabbitMQ transport stack-trace lines appear in stdout from other tests' bus teardown but are logging noise, not failures.
- Swap call-site positioning verified by reading `ProcessorStartupOrchestrator.cs`: `SwapTo(...)` sits immediately after `context.SetIdentity(found!.Message);` and before `break;` in the Loop-A Found block — well before the Completion block's queue-bind + `MarkHealthy` (line ~159).
- Note: `dotnet test ... --filter`/`--filter-not-trait` (VSTest style) is not usable here — this is a Microsoft.Testing.Platform (MTP / xunit.v3) project; tests were run via the built `BaseApi.Tests.exe` with `--filter-class`/`--filter-namespace` per the repo's MTP discipline.

## Scope Hygiene

All commits scoped to this plan's `src/` + `tests/` paths only. The pre-existing untracked items (`psql-*.txt`, `.claude/`, `.planning/phases/27-*/27-PATTERNS.md`, `src/BaseApi.Service/Properties/launchSettings.json`) were left untouched throughout.

## Next Phase Readiness

- **Phase 38 Wave 2 (this plan) is complete.** The processor's steady-state metric `service_name` swap mechanism is in place and hermetically proven. Wave 2 had a single plan (38-03, depends on 38-01).
- Plan 38-04 (RealStack live scrape verification) can now assert a running processor's business metric carries `service_name = {seeded DB Name}_{seeded DB Version}` (not `processor-sample_3.5.0`) once identity resolves, with the boot-window placeholder swap exercised end-to-end against the compose stack's Prometheus.

## Self-Check: PASSED
