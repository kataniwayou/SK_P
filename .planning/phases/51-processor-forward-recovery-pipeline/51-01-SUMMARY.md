---
phase: 51-processor-forward-recovery-pipeline
plan: 01
subsystem: BaseProcessor.Core configuration
tags: [config, options-binding, slot-array, ttl, di, scaffolding]
requires:
  - "ProcessorLivenessOptions (the precedent idiom; ExecutionDataTtlSeconds=300 floor reference)"
  - "AddBaseProcessor composition root (BaseProcessorServiceCollectionExtensions)"
provides:
  - "SlotArrayOptions — sealed class, two [ConfigurationKeyName] seconds-int props, baked defaults 300/600 (D-04/D-05)"
  - "services.Configure<SlotArrayOptions>(cfg.GetSection(\"Processor\")) — IOptions<SlotArrayOptions> resolvable from DI"
affects:
  - "Plan 51-02 (ProcessorPipeline rewrite consumes IOptions<SlotArrayOptions> as a ctor dependency)"
tech-stack:
  added: []
  patterns:
    - "ConfigurationKeyName-mapped seconds-int auto-props bound from the shared \"Processor\" section (mirrors ProcessorLivenessOptions)"
key-files:
  created:
    - "src/BaseProcessor.Core/Configuration/SlotArrayOptions.cs"
  modified:
    - "src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs"
    - "tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs"
decisions:
  - "Bound from the SAME \"Processor\" section as the liveness knobs (D-04) — no new config section introduced"
  - "Defaults 300/600: 300 floor = ProcessorLivenessOptions.ExecutionDataTtlSeconds default so the L2[messageId] marker outlives indexed data keys; 600 ceiling = 2x for expiry jitter (D-05)"
metrics:
  duration: "~12 min"
  completed: "2026-06-11"
  tasks: 2
  files: 3
---

# Phase 51 Plan 01: SlotArrayOptions Record + DI Bind Summary

Authored the slot-array random-TTL options record `SlotArrayOptions` (D-04/D-05, deferred from Phase-50 D-07), wired its DI bind from the shared `"Processor"` config section, and proved bind + baked-defaults with two hermetic facts — Wave 0 scaffolding so the plan-02 `ProcessorPipeline` ctor dependency resolves from the container.

## What Was Built

- **`SlotArrayOptions.cs`** — a sealed class mirroring `ProcessorLivenessOptions` exactly: two independent `[ConfigurationKeyName]`-mapped seconds-int auto-properties — `SlotArrayTtlMinSeconds` (key `SlotArrayTtlMin`, default `300`) and `SlotArrayTtlMaxSeconds` (key `SlotArrayTtlMax`, default `600`). The 300 floor is pinned to `ProcessorLivenessOptions.ExecutionDataTtlSeconds`'s default so the `L2[messageId]` marker outlives the data keys it indexes; the 600 ceiling is 2× for expiry jitter (D-05).
- **DI bind** — one line added immediately after the existing `Configure<ProcessorLivenessOptions>` bind: `services.Configure<SlotArrayOptions>(cfg.GetSection("Processor"))`. No new config section (D-04). The existing `using BaseProcessor.Core.Configuration;` already covered the type — no import added.
- **Two bind facts** — extended `ProcessorOptionsBindingFacts.cs`: `SlotArray_Binds_Min_Max_From_Processor_Section` (keys `300`/`600` → props) and `SlotArray_Empty_Config_Yields_Baked_Defaults` (empty config → `300`/`600`).

## Verification

- `dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj -c Debug --nologo` — 0 warnings / 0 errors.
- `dotnet build SK_P.sln -c Release --nologo` — Build succeeded, 0 warnings / 0 errors (full-solution Debug also built clean during the test run).
- `ProcessorOptionsBindingFacts` (MTP treenode filter `/*/*/ProcessorOptionsBindingFacts`) — **Passed: 4, Failed: 0, Total: 4** (2 existing + 2 new SlotArray facts).

## Deviations from Plan

None — plan executed exactly as written. Both tasks are marked `tdd="true"`; since Task 1 is the production options record and Task 2 is its hermetic bind facts, the production code landed first (compile-verified) then the facts proved bind + baked-defaults green — the same RED/GREEN spirit at the plan grain.

## Deferred Issues

None in scope. The full-suite run (an MTP runner artifact — the `--filter` flag is VSTest-only and was ignored with an `MTP0001` warning, so all 514 tests ran) reported 4 failures. These are out-of-scope, pre-existing docker-dependent RealStack E2E tests (PROJECT.md documents "the 2 pre-existing docker-dependent E2E tests" plus the Phase-49 operator-gated RealStack proofs). This plan added only a hermetic config-options class + two in-memory bind facts; it cannot affect live-stack E2E timing. Per the executor scope boundary, out-of-scope failures are neither fixed nor re-run. The targeted MTP `--filter-query` run isolated this plan's facts at 4/4 green.

## Known Stubs

None. `SlotArrayOptions` is a complete, fully-wired options record consumed by plan 02; the values are real defaults, not placeholders.

## Self-Check: PASSED

- `src/BaseProcessor.Core/Configuration/SlotArrayOptions.cs` — FOUND
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` contains `Configure<SlotArrayOptions>(cfg.GetSection("Processor"))` — FOUND
- `tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs` contains both new facts — FOUND
- Commit `6433c27` (feat, Task 1) — FOUND
- Commit `368b5e3` (test, Task 2) — FOUND
