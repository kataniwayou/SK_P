---
phase: 32-cancelled-circuit-breaker
plan: 02
subsystem: contracts + observability
tags: [l2-keys, cancelled-marker, sentinel-const, metrics, counters, cardinality-guard, hermetic, wave-1, additive]

# Dependency graph
requires:
  - phase: 31-idempotent-execution-exactly-once-effect
    provides: "L2ProjectionKeys single-source key-builder convention; EntryStepDispatch.WorkflowId / ExecutionResult.WorkflowId Guid contract"
  - phase: 30-runtime-business-metrics
    provides: "ProcessorMetrics / OrchestratorMetrics IMeterFactory holders + the no-_total snake_case Phase-30 convention"
provides:
  - "L2ProjectionKeys.Cancelled(Guid) => skp:cancelled:{workflowId:D} — the single-source no-TTL in-flight cancellation marker key builder"
  - "L2ProjectionKeys.CancelledMarkerValue const = \"true\" — the single sentinel literal for writer (Plan 04) + readers (Plan 03)"
  - "ProcessorMetrics.DispatchDeduped (processor_dispatch_deduped, D-10) + ProcessorMetrics.WorkflowCancelled (workflow_cancelled, D-11)"
  - "OrchestratorMetrics.ResultDeduped (orchestrator_result_deduped, D-10)"
affects: [32-03, 32-04, 32-05]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Marker key builder mirrors the Root(Guid) :D hyphenated precedent (NOT the 64-hex content-addressed data/flag keys) — workflow ids render hyphenated everywhere"
    - "One shared sentinel const colocated with the key builder so writer + both readers use ONE literal — value desync impossible"
    - "New counters follow the Phase-30 no-_total snake_case convention (collector's add_metric_suffixes appends _total; Risk R3)"
    - "MeterListener cardinality guard: capture tag-key sets of recorded measurements; assert ProcessorId present, NO workflowId/WorkflowId (T-32-02)"

key-files:
  created:
    - tests/BaseApi.Tests/Contracts/CancelledMarkerKeyFacts.cs
    - tests/BaseApi.Tests/Orchestrator/BreakerMetricsFacts.cs
  modified:
    - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
    - src/BaseProcessor.Core/Observability/ProcessorMetrics.cs
    - src/Orchestrator/Observability/OrchestratorMetrics.cs

key-decisions:
  - "Cancelled(Guid) renders :D (hyphenated) to match Root(Guid) — both EntryStepDispatch.WorkflowId and ExecutionResult.WorkflowId are Guid; NO Guid/string overload split added (only the Guid shape is needed)"
  - "CancelledMarkerValue colocated in L2ProjectionKeys (not the consumers) so the single-source-of-truth holder owns BOTH the key shape and the sentinel value"
  - "WorkflowCancelled lives on ProcessorMetrics (BaseProcessor meter), NOT OrchestratorMetrics — the breaker trip is processor-side per D-01"
  - "Instrument names carry NO _total suffix (processor_dispatch_deduped / workflow_cancelled / orchestrator_result_deduped) — the collector appends it, same as the existing Phase-30 instruments (Risk R3)"
  - "BreakerMetricsFacts authored as the construction + no-workflowId-label cardinality proof now; Plan 04 extends THIS class with the increment-once-per-trip / once-per-drop behavioral assertions at the real sites"

metrics:
  duration: ~9m
  completed: 2026-06-04
---

# Phase 32 Plan 02: Cancellation Marker Key + Breaker/Dedup Counters Summary

The additive foundation for the cancelled circuit-breaker: the no-TTL cancellation marker KEY BUILDER + a shared sentinel const (the single source of truth the processor writer in Plan 04 and both consumer readers in Plan 03 wire to) and the THREE new observability counters (processor-side dedup + breaker-trip, orchestrator-side dedup) that Plan 04 increments. Pure additive surface — no consumer/breaker logic here; only the declarations and hermetic tests pinning their shape and construction.

## What Was Built

### Task 1 — `L2ProjectionKeys.Cancelled` marker builder + `CancelledMarkerValue` sentinel (req-2 foundation, D-02/D-07)
- Added `public static string Cancelled(Guid workflowId) => $"{Prefix}cancelled:{workflowId:D}"` alongside the existing `Flag`/`ExecutionData` builders — mirrors the `Root(Guid)` `:D` hyphenated precedent (NOT the 64-hex `data`/`flag` content-addressed keys), since workflow ids render hyphenated everywhere in this codebase.
- Added `public const string CancelledMarkerValue = "true"` — ONE literal used at both the writer SET site (Plan 04) and both reader CHECK sites (Plan 03), so writer/reader cannot desync on the value either.
- Added a matching `<item><description>` line to the class doc-comment `<list>` block.
- `CancelledMarkerKeyFacts` (4 facts) pins: the exact `skp:cancelled:77777777-...` string; the `:D` interpolation for an arbitrary Guid; distinctness from `Root`/`Processor`; and `CancelledMarkerValue == "true"`. The exact-string pin lets the Plan-05 close-gate teardown scan `skp:cancelled:*` (no-TTL keys won't self-expire).

### Task 2 — three new counters + construction/cardinality proof (req-7, D-10/D-11)
- **`ProcessorMetrics`** (the trip is processor-side per D-01, so `workflow_cancelled` lives here): added `DispatchDeduped` (`processor_dispatch_deduped`, D-10) + `WorkflowCancelled` (`workflow_cancelled`, D-11).
- **`OrchestratorMetrics`**: added `ResultDeduped` (`orchestrator_result_deduped`, D-10).
- All three instrument names are snake_case with **NO `_total` suffix** — the OTel collector's `add_metric_suffixes` appends it (the existing Phase-30 convention; Risk R3). Additive-only: no registration change (the meters are already `AddMeter`-registered), nothing else touched in either holder.
- `BreakerMetricsFacts` (4 facts, analog of `OrchestratorMetricsFacts`/`ProcessorMetricsFacts`) builds both holders from a real `IMeterFactory` and asserts: the meter-name consts are unchanged; all three new counters non-null; and via a `MeterListener` that each recorded measurement carries the bounded `ProcessorId` tag and NO `workflowId`/`WorkflowId` tag key (T-32-02 cardinality guard). This class is where Plan 04 will add the increment-once-per-trip / once-per-drop behavioral assertions.

## Verification

- `dotnet build src/Messaging.Contracts -c Debug` — 0 Warning / 0 Error.
- `dotnet build src/BaseProcessor.Core -c Debug` + `dotnet build src/Orchestrator -c Debug` — both 0 Warning / 0 Error.
- `dotnet build SK_P.sln -c Release` — 0 Warning / 0 Error.
- `dotnet test tests/BaseApi.Tests -- --filter-class "*CancelledMarkerKeyFacts"` — Passed 4 / Failed 0.
- `dotnet test tests/BaseApi.Tests -- --filter-class "*BreakerMetricsFacts"` — Passed 4 / Failed 0.
- Grep `_total` across both metric holders — **0 matches** (the doc-comments say "the suffix", never the literal token; the instrument names omit it).
- Full hermetic suite `--filter-not-trait "Category=RealStack"` — **Passed 451 / Failed 0** (443 prior + 8 net new: 4 CancelledMarkerKeyFacts + 4 BreakerMetricsFacts; zero regression).

## Commits

- `0420e79` feat(32-02): add L2ProjectionKeys.Cancelled marker builder + shared sentinel const
- `b5c7019` feat(32-02): add breaker/dedup counters (processor_dispatch_deduped, workflow_cancelled, orchestrator_result_deduped)

## Deviations from Plan

None — both autonomous tasks executed exactly as written. Pure additive surface; no production behavior wired (Plans 03/04/05 do the wiring). No auth gates. No architectural decisions required.

## Must-Haves Status

- ✅ `L2ProjectionKeys.Cancelled(workflowId)` produces `skp:cancelled:{workflowId:D}` (pinned by CancelledMarkerKeyFacts).
- ✅ A single shared sentinel literal exists for the marker value (`CancelledMarkerValue`, one const, used by writer + both readers in later plans).
- ✅ `ProcessorMetrics` exposes `processor_dispatch_deduped` + `workflow_cancelled` counters.
- ✅ `OrchestratorMetrics` exposes `orchestrator_result_deduped` counter.
- ✅ None of the new counters carry a `workflowId` label (BreakerMetricsFacts MeterListener guard).

## Threat Flags

None — this plan adds declarations only; no new network endpoint, auth path, file access, or schema change. T-32-02 (counter cardinality) is mitigated in-plan by the BreakerMetricsFacts no-workflowId guard; T-32-07 (marker key namespace) is accepted (server-minted GUID only, same posture as `skp:flag:*`).

## Self-Check: PASSED

- All 5 key files present on disk (2 created, 3 modified).
- Both task commits (`0420e79`, `b5c7019`) present in git history.
