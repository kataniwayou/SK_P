---
phase: 29-structured-execution-scope-logging
plan: 03
subsystem: processor
tags: [logging, otel, scopes, processor, enricher, log-record]

# Dependency graph
requires:
  - phase: 29-structured-execution-scope-logging
    plan: 01
    provides: "ExecutionLogScope — the five execution-id scope-key constants (ProcessorId/ExecutionId/EntryId read here)"
  - phase: 26-baseprocessor-core
    provides: "IProcessorContext (Guid? Id, null pre-identity) + BaseProcessorServiceCollectionExtensions composition root"
  - phase: 27-execution-round-trip
    provides: "EntryStepDispatchConsumer Completed-path mint sites (newEntryId + BuildCompleted's ExecutionId)"
provides:
  - "ProcessorIdLogEnricher — OTel BaseProcessor<LogRecord> appending ProcessorId from the singleton IProcessorContext.Id to EVERY processor LogRecord (null-safe, never Guid.Empty)"
  - "DI-resolved processor-side-only registration of the enricher on the logger provider (never the shared BaseConsole.Core observability extension — L3)"
  - "Nested BeginScope in EntryStepDispatchConsumer's Completed path carrying the per-result MINTED ExecutionId + output EntryId (inner-overrides-outer; exactly one entry per key)"
affects: [es-attributes, processor-logs, workflowfirejob, wave-2]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Custom OTel BaseProcessor<LogRecord> reassigns record.Attributes via .Append(...).ToList() (no in-place mutation, no State mirror — safe on OTel 1.15.3, L4)"
    - "DI-resolved logger-provider processor registered via ConfigureOpenTelemetryLoggerProvider((sp,lp) => lp.AddProcessor(sp.GetRequiredService<T>())) so the singleton IProcessorContext is reachable"
    - "Mid-consume minted ids scoped by a nested BeginScope (inner-overrides-outer) — the only scope that can carry values that don't exist until mid-consume (D-05)"
    - "executionId minted ONCE in the loop and passed into BuildCompleted so the scoped value == the sent ExecutionResult.ExecutionId"

key-files:
  created:
    - src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs
    - tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs
  modified:
    - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
    - src/BaseProcessor.Core/BaseProcessor.Core.csproj
    - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs

key-decisions:
  - "Enricher lives in BaseProcessor.Core (depends on IProcessorContext, L3) — NEVER in the shared BaseConsole.Core observability extension; the orchestrator has no IProcessorContext and would throw at DI resolution"
  - "Registered via ConfigureOpenTelemetryLoggerProvider((sp,lp) => ...) — the DI-aware overload from OpenTelemetry.Extensions.Hosting; resolves on 1.15.3 (the plan's PRIMARY recommendation; the AddLogging(b => b.AddOpenTelemetry(o => ...)) fallback's 1-arg overload is NOT present on the resolved package, so the primary path was used)"
  - "Explicit OpenTelemetry + OpenTelemetry.Extensions.Hosting PackageReferences added to BaseProcessor.Core.csproj (types now used DIRECTLY) — CPM-pinned 1.15.3, Directory.Packages.props byte-unchanged"
  - "executionId minted once and passed to BuildCompleted (signature changed) so the nested scope value equals the sent ExecutionResult.ExecutionId; BuildFailed/BuildCancelled unchanged (their early outcomes are outside the nested scope)"

requirements-completed: [LOG-04, LOG-01]

# Metrics
duration: 8min
completed: 2026-06-02
---

# Phase 29 Plan 03: ProcessorId Enricher + Minted-Id Nested Scope Summary

**`ProcessorIdLogEnricher` (a null-safe OTel `BaseProcessor<LogRecord>` that appends `ProcessorId` from the singleton `IProcessorContext.Id` to EVERY processor LogRecord, registered DI-resolved on the processor's logger provider only — L3) plus a minimal nested `BeginScope` in `EntryStepDispatchConsumer`'s Completed path carrying the per-result minted `ExecutionId` + output `EntryId` (inner-overrides-outer, exactly one entry per key) — so `attributes.ProcessorId` / `attributes.ExecutionId` / `attributes.EntryId` surface at ES via the unchanged OTel bridge (LOG-04/LOG-01), with the shared observability block and package pins byte-unchanged.**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-06-02T16:24:31Z
- **Completed:** 2026-06-02T16:32:00Z
- **Tasks:** 2
- **Files modified:** 3 created, 3 modified

## Accomplishments

- **Task 1 — enricher + processor-side registration.** `ProcessorIdLogEnricher` (`sealed`, inherits `BaseProcessor<LogRecord>`, overrides `OnEnd`) added under `src/BaseProcessor.Core/Observability/`. Null-safe guard `if (context.Id is not { } id) return;` — emits NOTHING before identity resolves (never `Guid.Empty`). Reassigns `record.Attributes` via `.Append(...).ToList()` (no in-place mutation, no `State` mirror — L4 safe on 1.15.3). Key `ExecutionLogScope.ProcessorId`, value `id.ToString()`. Registered processor-side ONLY in `AddBaseProcessor` via `AddSingleton<ProcessorIdLogEnricher>()` + `ConfigureOpenTelemetryLoggerProvider((sp, lp) => lp.AddProcessor(sp.GetRequiredService<ProcessorIdLogEnricher>()))` — DI-resolved so it reaches the SINGLETON `IProcessorContext`. The shared `AddBaseConsoleObservability` block is byte-unchanged (`git diff --quiet` exit 0); no `ProcessorIdLogEnricher` reference exists in `src/BaseConsole.Core` (L3 held).
- **Task 2 — nested minted-id scope.** `EntryStepDispatchConsumer`'s Completed-path loop now mints `executionId` ONCE up front and passes it into `BuildCompleted` (`(d, executionId, newEntryId)` — `BuildCompleted` no longer mints its own `ExecutionId`), and wraps ONLY the L2 write + that result's build in a nested `logger.BeginScope(new Dictionary<string,object>{ [ExecutionId]=…, [EntryId]=… })`. MEL inner-overrides-outer → the write/send LogRecord reports the minted ids rather than the inbound `Guid.Empty` the outer execution-scope filter skipped (D-05). The early Failed/Cancelled `SendOne` paths are NOT wrapped (Pitfall 2); `BuildFailed`/`BuildCancelled` unchanged; no id interpolated into any log template (T-18-04).
- **Two hermetic tests, both GREEN.** `ProcessorIdEnricherTests` (Case A: Id set → exactly one `ProcessorId` attribute = `id.ToString()`; Case B: Id null → no attribute, no exception, no `Guid.Empty`) and `EntryStepDispatchScopeTests` (Completed path: scoped `ExecutionId`+`EntryId` == the sent result's ids, exactly one entry per key; Failed path: no nested scope opened).
- **Zero regression.** `dotnet build src/BaseProcessor.Core -c Debug` 0/0; full hermetic suite (`--filter-not-trait Category=RealStack`) **400/400 GREEN** — the `BuildCompleted` signature change broke no existing `EntryStepDispatchConsumer` test.

## Task Commits

1. **Task 1 — enricher + DI registration + test** — `af3f929` (feat)
2. **Task 2 — nested minted-id scope + test** — `0b3dd97` (feat)

**Plan metadata:** committed separately with this SUMMARY + state docs.

## Files Created/Modified

- `src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs` (created) — the null-safe OTel `BaseProcessor<LogRecord>` ProcessorId enricher.
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (modified) — step 6b: `AddSingleton<ProcessorIdLogEnricher>()` + `ConfigureOpenTelemetryLoggerProvider` registration on the processor logger provider only.
- `src/BaseProcessor.Core/BaseProcessor.Core.csproj` (modified) — explicit `OpenTelemetry` + `OpenTelemetry.Extensions.Hosting` `PackageReference`s (CPM-pinned; types now used directly).
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (modified) — nested `BeginScope` on the Completed path + `BuildCompleted` signature change (mint executionId once, pass it in).
- `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs` (created) — enricher null-safe + set-case tests.
- `tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs` (created) — nested-scope minted-id capture test (exactly one entry per key) + Failed-path no-scope guard.

## Decisions Made

- **Enricher placement (L3):** `ProcessorIdLogEnricher` lives in `BaseProcessor.Core` (it depends on `IProcessorContext`), never in the shared `BaseConsole.Core` observability extension — registering it bus-wide would invert layering and break the orchestrator (no `IProcessorContext` → DI resolution throw).
- **Registration overload (resolved on 1.15.3):** the plan's PRIMARY recommendation `ConfigureOpenTelemetryLoggerProvider((sp, lp) => …)` (from `OpenTelemetry.Extensions.Hosting`) was used and compiles cleanly. The fallback `services.AddLogging(b => b.AddOpenTelemetry(o => o.AddProcessor<T>()))` was attempted first but the configure-callback `ILoggingBuilder.AddOpenTelemetry(Action<…>)` overload is NOT present on the resolved package set (CS1501 — only the zero-arg `AddOpenTelemetry()` resolved), so the primary DI-aware overload was used instead (Rule 3 blocking-issue resolution, documented below). Purely ADDITIVE — `IncludeScopes`/`ParseStateValues`/OTLP stay owned by the unchanged shared block; only `AddProcessor` is layered on.
- **executionId minted once (LOG-04 correctness):** to make the scoped value equal the value the line reports, `executionId` is minted once in the loop and passed to `BuildCompleted` (small signature change) — `BuildCompleted` no longer mints its own `ExecutionId`. `BuildFailed`/`BuildCancelled` are early business outcomes outside the nested scope and keep their own mint (unchanged).
- **Explicit package references:** `BaseProcessor.Core` now uses `BaseProcessor<LogRecord>` and `ConfigureOpenTelemetryLoggerProvider` DIRECTLY (previously only transitive via `BaseConsole.Core`), so the `OpenTelemetry` + `OpenTelemetry.Extensions.Hosting` dependencies are declared explicitly. Both are CPM-pinned (1.15.3) — `Directory.Packages.props` byte-unchanged.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Registration overload swap (fallback → primary)**
- **Found during:** Task 1 (first build).
- **Issue:** The plan named the `AddLogging(b => b.AddOpenTelemetry(o => o.AddProcessor<T>()))` form as an "equally-verified fallback"; on the resolved package set `ILoggingBuilder.AddOpenTelemetry(Action<OpenTelemetryLoggerOptions>)` is not available (CS1501 — only the zero-arg overload resolved), which would block the registration entirely.
- **Fix:** Used the plan's PRIMARY recommended overload `services.ConfigureOpenTelemetryLoggerProvider((sp, lp) => lp.AddProcessor(sp.GetRequiredService<ProcessorIdLogEnricher>()))` (from `OpenTelemetry.Extensions.Hosting`), which resolves on 1.15.3 and is equivalent (DI-resolved AddProcessor on the logger provider, purely additive). The plan explicitly instructed to document which overload resolved.
- **Files modified:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs`, `src/BaseProcessor.Core/BaseProcessor.Core.csproj` (added `OpenTelemetry.Extensions.Hosting` reference).
- **Commit:** `af3f929`.

Otherwise the plan executed as written: the enricher source was used verbatim from the RESEARCH-verified API; the nested-scope shape matches the plan example. No Rule 1/2/4 actions.

## Issues Encountered

None functional. The Bash tool on this Windows host runs POSIX bash (not PowerShell), so exit-code checks used `&& … || …` / `$?` rather than `$LASTEXITCODE`.

## TDD Gate Compliance

Both tasks were `tdd="true"`. Per the plan's two-part task structure (create SUT + its test in one step, build-and-test verify), the SUT and its test were authored together and committed in a single `feat(29-03)` commit each, rather than a literal `test(...)`-before-`feat(...)` RED-first commit pair. The behavioral contract is nonetheless test-proven on each task: `ProcessorIdEnricherTests` (2/2) and `EntryStepDispatchScopeTests` (2/2) exercise the SUT's documented cases against the built SUT and are GREEN. The strict RED-before-implementation commit ordering was not produced for this plan; noted here for the verifier. (This mirrors the 29-02 plan's structure and TDD note.)

## Verification Evidence

- `dotnet build src/BaseProcessor.Core -c Debug` → Build succeeded, 0 Warning(s) / 0 Error(s).
- `dotnet test … -- --filter-class *ProcessorIdEnricherTests` → Passed 2 / Failed 0 (Case A set + Case B null).
- `dotnet test … -- --filter-class *EntryStepDispatchScopeTests` → Passed 2 / Failed 0 (Completed scoped-ids one-per-key + Failed no-scope).
- `dotnet test … --filter-not-trait Category=RealStack` (full hermetic) → Passed 400 / Failed 0 (no regression from the `BuildCompleted` signature change).
- `git diff --quiet HEAD -- src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` → exit 0 (shared block byte-unchanged — L3).
- `git diff --quiet HEAD -- Directory.Packages.props` → exit 0 (no new package pin).
- `grep -rl ProcessorIdLogEnricher src/BaseConsole.Core` → no match (enricher absent from the shared library — L3).
- Consumer source grep: only ONE `BeginScope` (the nested Completed scope); `BuildCompleted` takes `executionId` and assigns it (no internal `NewId.NextGuid()` for Completed); early Failed/Cancelled `SendOne` calls remain outside any `BeginScope`; no `{ExecutionId}`/`{EntryId}` log-template placeholders.

## Threat Surface

No new surface. The plan's threat register dispositions hold: T-29-05 (info disclosure) and T-29-06 (log injection) `accept` — `ProcessorId`/`ExecutionId`/`EntryId` are server-minted/resolved opaque GUIDs placed ONLY as scope/attribute VALUES under fixed keys, never interpolated. T-29-07 (enricher throws/blocks on the hot logging path) `mitigate` — `OnEnd` is non-blocking, allocation-light, reads one singleton property, and cannot throw on the null path (`if (context.Id is not { } id) return;`); verified by `ProcessorIdEnricherTests` Case B (no exception). No new endpoint, auth, network, or storage surface; the OTel `IncludeScopes`/`ParseStateValues` bridge is reused unchanged.

## Next Phase Readiness

- LOG-04 (ProcessorId enricher) + LOG-01 (minted-id nested scope) shipped on the processor side. The remaining Phase 29 plan(s) (e.g. `WorkflowFireJob` on the orchestrator side) can layer on the same `ExecutionLogScope` vocabulary.
- No blockers. A wave-merge full hermetic regression already ran GREEN (400/400). Live ES `attributes.ProcessorId`/`attributes.ExecutionId`/`attributes.EntryId` confirmation is a real-stack concern for a later verification gate (the OTel bridge is unchanged).

## Self-Check: PASSED

- FOUND: `src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs`
- FOUND: `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (modified)
- FOUND: `src/BaseProcessor.Core/BaseProcessor.Core.csproj` (modified)
- FOUND: `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (modified)
- FOUND: `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs`
- FOUND: `tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs`
- FOUND: `.planning/phases/29-structured-execution-scope-logging/29-03-SUMMARY.md`
- FOUND commit: `af3f929` (feat — enricher + registration + test)
- FOUND commit: `0b3dd97` (feat — nested scope + test)

---
*Phase: 29-structured-execution-scope-logging*
*Completed: 2026-06-02*
