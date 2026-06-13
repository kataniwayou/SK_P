---
phase: 59-per-instance-l2-keyspace-two-state-liveness-value
plan: 01
subsystem: Messaging.Contracts / Projections
tags: [liveness, contract, projection, two-state, per-instance, KEY-04, STATE-01, STATE-02]
dependency_graph:
  requires:
    - "Messaging.Contracts.Projections.LivenessStatus (existing const-SoT)"
    - "Messaging.Contracts.Projections.LivenessProjection (positional-record [property: JsonPropertyName] pattern)"
  provides:
    - "LivenessStatus.Unhealthy const (two-state status)"
    - "SchemaOutcome string-const SoT (SUCCESS|FAIL)"
    - "ProcessorLivenessEntry liveness-only value record + nested LivenessSummary + Create factory"
  affects:
    - "Phase 60 dual-loop writer (serializes ProcessorLivenessEntry)"
    - "Phase 61 WebAPI ≥1-healthy gate (deserializes ProcessorLivenessEntry)"
tech_stack:
  added: []
  patterns:
    - "static-class string-const SoT (mirrors LivenessStatus / L2ProjectionKeys / OrchestratorQueues)"
    - "positional sealed record with load-bearing [property: JsonPropertyName] targets"
    - "single-factory invariant enforcement (status derived from summary; positional ctor public only for STJ)"
key_files:
  created:
    - src/Messaging.Contracts/Projections/SchemaOutcome.cs
    - src/Messaging.Contracts/Projections/ProcessorLivenessEntry.cs
    - tests/BaseApi.Tests/Features/Orchestration/Projection/ProcessorLivenessEntryFacts.cs
  modified:
    - src/Messaging.Contracts/Projections/LivenessStatus.cs
decisions:
  - "D-01: new isolated ProcessorLivenessEntry record (NOT a reshape of ProcessorProjection); timestamp/interval/status duplicated from the SHARED LivenessProjection by design so the out-of-scope workflow-root path is untouched."
  - "D-02: string-const SoT for SchemaOutcome (no enum, no JsonStringEnumConverter)."
  - "D-02a: configSchema is the v6.0.0 Gate-A startup config-compat outcome; null-is-skip => Success — field + factory mapping only this phase."
  - "Casing locked: title-case Unhealthy to match existing title-case Healthy; SchemaOutcome uses uppercase SUCCESS/FAIL (matches ROADMAP/REQUIREMENTS prose)."
metrics:
  duration: ~28m (excluding a one-off full-suite 24m run caused by the MTP filter-ignore environmental issue)
  completed: 2026-06-13
  tasks: 3
  files: 4
---

# Phase 59 Plan 01: Per-Instance L2 Keyspace Two-State Liveness Value Summary

Added the liveness-only `ProcessorLivenessEntry` wire contract (two-state `status`, per-schema `summary`, single `Create` invariant factory) plus the `SchemaOutcome` const SoT and `LivenessStatus.Unhealthy` — the one type the Phase-60 writer serializes and the Phase-61 gate deserializes, so they cannot desync, with definitions absent by construction.

## What Was Built

- **Task 1** (`1e2f084`): Added `LivenessStatus.Unhealthy = "Unhealthy"` beside the existing `Healthy` (additive, STATE-01) and created `SchemaOutcome` — a new static-class string-const SoT exposing `Success = "SUCCESS"` / `Fail = "FAIL"` (D-02, no enum). Messaging.Contracts built Release 0-warning.
- **Task 2** (`da9b295`): Created `ProcessorLivenessEntry` — a new isolated sealed positional record (timestamp/interval/status/summary) with load-bearing `[property: JsonPropertyName]` targets, a nested `LivenessSummary` (inputSchema/outputSchema/configSchema), and the single `Create` factory that is the STATE-01/02 enforcement point: any `SchemaOutcome.Fail` ⇒ `LivenessStatus.Unhealthy`, a null per-schema outcome ⇒ `SchemaOutcome.Success` (null-is-skip). No `inputDefinition`/`outputDefinition` field (KEY-04 — absence by construction). Built Release 0-warning.
- **Task 3** (`fcaa766`): Created `ProcessorLivenessEntryFacts` — `[Trait("Phase","59")]` hermetic facts serialized under DEFAULT `JsonSerializerOptions` (so the `[property: JsonPropertyName]` pins hold on their own): a shape fact proving `inputDefinition`/`outputDefinition` JSON keys are absent and the lower-camel keys present, a 4-row theory proving the any-Fail⇒Unhealthy + null-is-skip⇒Success invariant, and a two-state fact pinning `status ∈ {Healthy, Unhealthy}`. **6/6 facts green** (targeted MTP `--filter-class` run).

## Verification

- `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Release -warnaserror` → **0 Warning(s), 0 Error(s)** (run after both Task 1 and Task 2).
- `dotnet test tests/BaseApi.Tests -- --filter-class "*ProcessorLivenessEntryFacts"` → **Passed: 6, Failed: 0**.
- Full hermetic suite (incidental, from the filter-ignore full run) → **Passed: 574, Failed: 0** — no sibling regression.
- `git diff --name-only HEAD~3 HEAD` shows exactly the 4 intended files; `LivenessProjection.cs` and `ProcessorProjection.cs` unchanged; `git diff --diff-filter=D` empty (no deletions).

## Success Criteria

- **KEY-04**: new record carries no `inputDefinition`/`outputDefinition` field — shape test green (JSON keys absent). ✅
- **STATE-01**: `LivenessStatus.Unhealthy` exists; `status ∈ {Healthy, Unhealthy}` — two-state fact green. ✅
- **STATE-02**: per-schema `summary` exists; `Create` derives any-Fail⇒Unhealthy, null⇒Success — theory green. ✅
- **SC-5 (this slice)**: Messaging.Contracts builds Release 0-warning; hermetic facts green. ✅

## Deviations from Plan

None — plan executed exactly as written (verbatim file content). One documentation nuance noted below.

**Note (not a deviation):** Task 2's acceptance criterion "File does NOT contain the substring `inputDefinition` or `outputDefinition`" is satisfied at the field level (no such field declarations exist). The plan's own verbatim file content includes those two words once, inside the XML `<summary>` doc comment that explains the fields were deliberately *dropped* from L2. Both were authored by the planner; the substantive KEY-04 intent ("no definition fields") is enforced by Task 3's shape test asserting the serialized JSON has no `inputDefinition`/`outputDefinition` keys (green). The verbatim doc comment was retained as specified.

## Tooling Note (environmental, no production impact)

The plan's verify command `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~ProcessorLivenessEntry"` uses VSTest `--filter` syntax, which this project's Microsoft.Testing.Platform (MTP) runner **ignores** (emits `MTP0001`). The unfiltered run therefore executed the entire 574-test hermetic suite (24m, all green). A subsequent targeted run using the MTP-native `-- --filter-class "*ProcessorLivenessEntryFacts"` confirmed the 6 new facts in isolation (637ms, 6/6 green). One intermediate targeted run failed with exit 1 solely due to a `TestResults\*.log` file-lock collision with the still-running full suite (an I/O race, not a test failure); re-run after the lock released passed cleanly. Future Phase 59/60/61 plans should use `-- --filter-class`/`--filter-method` MTP syntax for targeted runs.

## Threat Surface

No new threat surface beyond the plan's `<threat_model>`. T-59-01 (status-vs-summary tampering) is mitigated as designed — `Create` is the single construction path deriving `status` from `summary`, pinned by the Task 3 factory-invariant theory. T-59-02/T-59-03 are `accept` dispositions owned downstream (Phase 61 reader). No new endpoints, auth paths, or schema changes introduced.

## Known Stubs

None.

## Self-Check: PASSED

All 3 created files + 1 modified file + the SUMMARY exist on disk; all 3 task commits (`1e2f084`, `da9b295`, `fcaa766`) exist in git history.
