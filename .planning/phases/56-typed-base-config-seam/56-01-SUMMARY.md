---
phase: 56-typed-base-config-seam
plan: 01
subsystem: api
tags: [base-processor, config-seam, system-text-json, generic-base, deserialize]

# Dependency graph
requires:
  - phase: 44-processor-pipeline
    provides: "BaseProcessor + ProcessorPipeline.ExecuteAsync(string,string,ct) call site (:226) and catch-all (:241)"
provides:
  - "Empty marker base config ProcessorConfig + single canonical JsonSerializerOptions (D-05/D-06)"
  - "Generic deserialize-then-dispatch framework layer BaseProcessor<TConfig>"
  - "Non-generic BaseProcessor reshaped to internal abstract ExecuteAsync (old raw-string author seam removed, clean break)"
  - "Processor.Sample migrated to the typed seam (SampleConfig : ProcessorConfig, SampleProcessor : BaseProcessor<SampleConfig>)"
affects: [57-startup-config-schema-fetch-gate-a, 56-02-hermetic-suite-migration]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Empty marker abstract record as a type anchor (zero framework-mandated fields)"
    - "Single canonical cached static JsonSerializerOptions shared by framework + future Gate A"
    - "Generic base layer supplies the non-generic internal seam body (deserialize-then-dispatch)"

key-files:
  created:
    - src/BaseProcessor.Core/Configuration/ProcessorConfig.cs
    - "src/BaseProcessor.Core/Processing/BaseProcessor`1.cs"
    - src/Processor.Sample/SampleConfig.cs
  modified:
    - src/BaseProcessor.Core/Processing/BaseProcessor.cs
    - src/Processor.Sample/SampleProcessor.cs

key-decisions:
  - "Marker is an abstract record named ProcessorConfig in BaseProcessor.Core.Configuration (planner discretion locked)"
  - "Empty/whitespace payload short-circuits to a null config BEFORE deserialize (D-04) — Deserialize never runs on an empty string"
  - "Malformed payload throws an UNCAUGHT JsonException → propagates to ProcessorPipeline.cs:241 catch-all → exactly one StepFailed (D-03); no local try/catch, no default-config fallback"
  - "Default unknown-member handling (ignore) + PropertyNameCaseInsensitive=true; JsonUnmappedMemberHandling.Disallow deliberately OUT of scope (deferred to Phase 57 Gate A, D-05)"
  - "Clean break: old protected ProcessAsync(string,string,ct) removed with no shim; Program.cs DI registration byte-unchanged (IS-A via BaseProcessor<SampleConfig> : BaseProcessor)"

patterns-established:
  - "Pattern 1: typed config seam — framework owns deserialization, author overrides only the typed ProcessAsync(string, TConfig?, ct)"
  - "Pattern 2: deserialize-fault propagation — let JsonException reach the pipeline catch-all rather than catching locally (deterministic single StepFailed)"

requirements-completed: [CFG-01, CFG-02]

# Metrics
duration: 3min
completed: 2026-06-12
---

# Phase 56 Plan 01: Typed Base-Config Seam Summary

**Reshaped the BaseProcessor author seam from a raw-string config parameter to a framework-deserialized typed config instance (`BaseProcessor<TConfig>` + empty marker `ProcessorConfig` with a single canonical `JsonSerializerOptions`), and migrated `Processor.Sample` onto it with the old raw-string seam removed (clean break).**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-06-12T14:44:11Z
- **Completed:** 2026-06-12T14:47:06Z
- **Tasks:** 3
- **Files modified:** 5 (3 created, 2 modified)

## Accomplishments
- Empty marker base config `ProcessorConfig` (abstract record, zero framework-mandated fields) + the single cached canonical `SerializerOptions` (case-insensitive, unknown members ignored) that Phase 57's Gate A will reuse (D-05/D-06).
- Generic framework layer `BaseProcessor<TConfig> : BaseProcessor where TConfig : ProcessorConfig` that supplies the non-generic `internal abstract ExecuteAsync` body: empty/whitespace payload → null config (D-04); otherwise `JsonSerializer.Deserialize<TConfig>(payload, ProcessorConfig.SerializerOptions)`; malformed payload throws an uncaught `JsonException` that propagates to the pipeline catch-all (D-03).
- Non-generic `BaseProcessor` reshaped: the old `protected abstract ProcessAsync(string,string,ct)` author seam removed with no shim; the pipeline-facing `ExecuteAsync(string,string,ct)` is now `internal abstract` (D-01).
- `Processor.Sample` migrated: `SampleConfig(string? Value) : ProcessorConfig` + `SampleProcessor : BaseProcessor<SampleConfig>` overriding the typed `ProcessAsync(string, SampleConfig?, ct)`, with no manual deserialize and `Program.cs` byte-unchanged.
- `src/BaseProcessor.Core` and `src/Processor.Sample` both build 0-warning in Release.

## Task Commits

Each task was committed atomically:

1. **Task 1: Empty marker base config + single-source JsonSerializerOptions** - `b604137` (feat)
2. **Task 2: Reshape non-generic base + add generic deserialize-then-dispatch layer** - `be70a9f` (feat)
3. **Task 3: Migrate Processor.Sample to the typed seam, clean break** - `b52d334` (feat)

_TDD note: each task gated on a `dotnet build` (the behavioral deserialize/null/JsonException facts are owned by Plan 02, which migrates the hermetic suite and runs the green build/test gate — this plan deliberately leaves the test project non-compiling)._

## Files Created/Modified
- `src/BaseProcessor.Core/Configuration/ProcessorConfig.cs` (created) - Empty marker abstract record + single canonical `SerializerOptions`.
- `src/BaseProcessor.Core/Processing/BaseProcessor`1.cs` (created) - Generic deserialize-then-dispatch layer; supplies the non-generic `ExecuteAsync` body and declares the typed author `ProcessAsync`.
- `src/BaseProcessor.Core/Processing/BaseProcessor.cs` (modified) - Reshaped to `internal abstract ExecuteAsync`; old raw-string author seam removed.
- `src/Processor.Sample/SampleConfig.cs` (created) - `sealed record SampleConfig(string? Value) : ProcessorConfig`.
- `src/Processor.Sample/SampleProcessor.cs` (modified) - Migrated to `BaseProcessor<SampleConfig>` + typed override; dropped `System.Text.Json` + the `BaseProcessorBase` alias.

## Decisions Made
- Locked the marker as `abstract record ProcessorConfig` in namespace `BaseProcessor.Core.Configuration` (planner-granted discretion).
- Kept the deserialize forgiving (case-insensitive + unknown-members-ignored) per D-05; `JsonUnmappedMemberHandling.Disallow` intentionally NOT set (Phase 57 concern).
- No local try/catch around the deserialize — a malformed payload's `JsonException` is allowed to reach `ProcessorPipeline.cs:241` for a deterministic single `StepFailed` (T-56-01 mitigation honored).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Class-level `<paramref name="payload"/>` in the verbatim `BaseProcessor`1.cs` doc block failed the build under TreatWarningsAsErrors**
- **Found during:** Task 2 (generic layer)
- **Issue:** The plan's verbatim XML doc for the generic CLASS used `<paramref name="payload"/>`, but a class has no parameters (only its methods do) → CS1734, which is fatal under `TreatWarningsAsErrors=true`.
- **Fix:** Changed the single class-level `<paramref name="payload"/>` to `<c>payload</c>` (narrative reference at the class-doc level). The method-level `<paramref name="payload"/>` on `ExecuteAsync` was left intact (valid there).
- **Files modified:** src/BaseProcessor.Core/Processing/BaseProcessor`1.cs
- **Verification:** `dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj -c Release --nologo` → 0 warnings, 0 errors.
- **Committed in:** be70a9f (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** Doc-comment correctness fix required to compile under TreatWarningsAsErrors; no behavioral or structural change. No scope creep.

## Issues Encountered
None beyond the deviation above. Both target projects build 0-warning in Release; the old `(string,string)` seam is gone from all of `src`; `Program.cs` is byte-unchanged.

## Threat Surface
No new threat surface beyond the plan's `<threat_model>` (T-56-01/02/03). The deserialize fault-propagation (T-56-01, mitigate) and the empty-payload null short-circuit (T-56-02, mitigate) are both implemented as specified; the forgiving-options residual risk (T-56-03) is accepted by design. No new network/auth/persistence surface.

## Known Stubs
None. (No empty-value/placeholder stubs introduced; all production code is wired onto the new seam.)

## Next Phase Readiness
- All production code is on the typed seam. The hermetic test project still references the old `(string,string)` seam and does NOT compile — this is the intended clean-break interim; **Plan 02 (Wave 2)** migrates the hermetic suite and runs the green full-solution build/test gate.
- The single canonical `ProcessorConfig.SerializerOptions` + the config *type* anchor are now in place for **Phase 57's Gate A** (config-type ↔ config-schema compatibility).

## Self-Check: PASSED

All 5 source files + the SUMMARY exist on disk; all 3 task commits (b604137, be70a9f, b52d334) are present in git history.

---
*Phase: 56-typed-base-config-seam*
*Completed: 2026-06-12*
