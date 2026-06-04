---
phase: 32-cancelled-circuit-breaker
plan: 05
subsystem: orchestrator consumer (in-flight stop half)
tags: [cancelled-marker, check-and-drop, dedup-counter, ack-and-discard, workflowId-keyed, read-only-marker, hermetic, wave-2, tdd]

# Dependency graph
requires:
  - phase: 32-cancelled-circuit-breaker
    provides: "Plan 02 — L2ProjectionKeys.Cancelled(Guid) + CancelledMarkerValue const + OrchestratorMetrics.ResultDeduped (orchestrator_result_deduped) counter"
  - phase: 31-idempotent-execution-exactly-once-effect
    provides: "ResultConsumer effect-first flag[H] gate + ExecutionResult.WorkflowId/ProcessorId/H contract (IExecutionCorrelated)"
provides:
  - "ResultConsumer check-and-drop gate (ack-and-discards an in-flight ExecutionResult for a cancelled workflow, workflowId-keyed, marker read-only, no flag[H] touch — D-05/D-13)"
  - "ResultConsumer flag[H]==Ack dedup counter (orchestrator_result_deduped +1 at the existing drop gate, ProcessorId-tagged — D-10)"
affects: [32-06]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Orchestrator check-and-drop MIRRORS the processor-side 32-04 gate: cancelled read placed AFTER the flag[H] gate, BEFORE the L1 store.TryGet — RESEARCH Unknown-3 (cheaper for the common dedup case, keeps the Phase-31 gate visually first)"
    - "Single sentinel const (L2ProjectionKeys.CancelledMarkerValue) on the check-and-drop compare — same literal the 32-04 processor writer SETs; value desync impossible"
    - "ProcessorId tag is .ToString(\"D\") on a NON-nullable Guid (m.ProcessorId) — NO bang (unlike the processor's context.Id!.Value), mirrors the existing :55 ResultConsumed idiom"
    - "Hermetic MeterListener scoped per-test via an IDisposable DedupScope (filters by Meter-instance ReferenceEquals, disposes listener+provider) — no cross-test instrument leak under xUnit parallel-by-default"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/ResultCheckAndDropFacts.cs
  modified:
    - src/Orchestrator/Consumers/ResultConsumer.cs

key-decisions:
  - "The orchestrator only checks-and-drops — it does NOT set the marker and does NOT trip the breaker (the trip is processor-side per D-01/32-04). Asserted: NO StringSetAsync to any key on the cancelled path (grep `StringSetAsync.*Cancelled` = 0)."
  - "Cancelled read placed AFTER the flag[H]==Ack gate and BEFORE store.TryGet (line order pinned) — reads L2ProjectionKeys.Cancelled(m.WorkflowId) ONLY, never L2ProjectionKeys.Flag(...) (D-13)."
  - "Dedup counter lives inside the existing flag[H]==Ack branch (instrument the existing drop, NOT a new gate) — the cancelled drop deliberately increments NO counter (it is not a flag-Ack drop)."
  - "DedupScope MeterListener filters by ReferenceEquals to THIS holder's ResultDeduped instrument (not just the instrument name) so a sibling parallel test's \"Orchestrator\" meter cannot bleed into the asserted count."

metrics:
  duration: ~20m
  completed: 2026-06-04
---

# Phase 32 Plan 05: Breaker Orchestrator Half Summary

The orchestrator half of the in-flight stop — two surgical edits to `ResultConsumer`, mirroring the processor-side 32-04 gate. (1) The check-and-drop gate that ack-and-discards an in-flight `ExecutionResult` for a cancelled workflow so a result already on the shared `orchestrator-result` queue when the breaker tripped does not advance the halted DAG (req-3, D-05). (2) The `orchestrator_result_deduped` counter at the existing `flag[H]=="Ack"` drop gate, surfacing the orchestrator-side redelivery-collapse rate (req-7, D-10). The orchestrator reads the marker only — it never sets it and never trips the breaker (D-01 keeps the trip processor-side). One hermetic Facts class pins both edits plus the workflowId-keying and the D-13 no-flag-touch guard. TDD: RED commit (failing facts) -> GREEN commit (the two edits).

## What Was Built

### Task 1 — ResultConsumer check-and-drop gate + orchestrator_result_deduped counter (req-3 orchestrator, req-7 dedup; D-05/D-10/D-13)

- **Edit 1 (dedup counter, D-10):** at the existing `flag[m.H]=="Ack"` gate (was a bare `return`), wrapped in a block that first increments `metrics.ResultDeduped.Add(1, new KeyValuePair<string,object?>("ProcessorId", m.ProcessorId.ToString("D")))` then returns. `m.ProcessorId` is a non-nullable Guid so NO bang (mirrors the `:55` `ResultConsumed` idiom, unlike the processor's `context.Id!.Value`). Counts how often a redelivery is dropped at the Phase-31 orchestrator-hop dedup gate. No dispatch, no flip — the existing drop is unchanged beyond the +1.
- **Edit 2 (check-and-drop gate, req-3 / D-05):** placed AFTER the `flag[H]` gate and BEFORE the L1 `store.TryGet` — `if ((string?)await db.StringGetAsync(L2ProjectionKeys.Cancelled(m.WorkflowId)) == L2ProjectionKeys.CancelledMarkerValue) return;`. Reads the cancelled marker ONLY (never `flag[H]` — D-13). ack-and-discard: no advancement, no manifest read, no fan-out, no flip, NO dedup counter (this is the cancelled drop, not a flag-Ack drop), and crucially NO marker write (the orchestrator never WRITES the marker — the trip is processor-side per D-01). INFRA read (no catch) -> a Redis fault propagates to the definition's bounded retry. `workflowId`-keyed so a result for another (un-cancelled) workflow is unaffected.
- **`ResultCheckAndDropFacts` (3 facts, analog of `ResultAckTests`/`ResultConsumeTests`):**
  1. `FlagAlreadyAck_IncrementsDedupOnce_Drops_NoDispatch` — flag[m.H]=="Ack" -> exactly ONE `orchestrator_result_deduped` increment (observed via a scoped `MeterListener`), tagged `ProcessorId`, dropped with NO `dispatcher.DispatchAsync` and NO `StringSetAsync` flip.
  2. `CancelledMarkerSet_AckAndDiscards_NoDispatch_NoDedup_NoWrite` — cancelled[m.WorkflowId]=="true", flag null -> return with NO dispatch, ZERO dedup increment, and NO `StringSetAsync` to ANY key (across all overloads — the D-13 / no-marker-write guard); confirms the cancelled marker key was read.
  3. `OtherWorkflowCancelled_ThisResultProceeds_Dispatches` — a DIFFERENT workflow's marker is set but THIS result's `WorkflowId` is not cancelled -> it proceeds to the normal L1 + manifest + fan-out path and dispatches one continuation carrying the manifest item EntryId (proves workflowId-keying via a concrete `RecordingDispatcher`).

## Verification

- `dotnet build src/Orchestrator -c Debug` — 0 Warning / 0 Error.
- `dotnet build SK_P.sln -c Release` — 0 Warning / 0 Error.
- `dotnet test tests/BaseApi.Tests -- --filter-class "*ResultCheckAndDropFacts"` — Passed 3 / Failed 0.
- Directly-impacted pre-existing classes in isolation: `*ResultAckTests` 6/6, `*ResultConsumeTests` 2/2 — zero regression from the new read-only gate (their `NoopRedis` returns Null for the cancelled key -> the gate passes through unchanged).
- Acceptance greps: `ResultDeduped.Add(1,` inside the flag-Ack branch (line 70); `StringGetAsync(L2ProjectionKeys.Cancelled(m.WorkflowId))` vs `CancelledMarkerValue` (lines 80-81) AFTER the flag gate and BEFORE `store.TryGet`; `StringSetAsync(...Cancelled...)` = **0 matches** (orchestrator never WRITES the marker).
- No file deletions across the two task commits (`git diff --diff-filter=D HEAD~2 HEAD` empty).
- Full hermetic suite `--filter-not-trait "Category=RealStack"` — **Passed 461 / Failed 1 of 462** (459 prior + 3 new ResultCheckAndDropFacts). The 1 failure is a PRE-EXISTING non-deterministic in-memory-MassTransit-harness race in the Orchestrator namespace, NOT a regression — see Deferred Issues.

## Pre-existing Flaky-Test Investigation (NOT a regression)

The full hermetic suite showed 1 failure with my change applied. To prove independence from this plan's edit, I stashed `src/Orchestrator/Consumers/ResultConsumer.cs` (my only source change) and re-ran the Orchestrator namespace on the baseline: it failed **5** tests (the count varies run-to-run: observed 5, 3, 1). The failure count is non-deterministic and present WITHOUT my change — the signature of a timing/parallelism race in the in-memory MassTransit harness tests (consistent with the MEMORY note `reference_close_gate_surfaces_stale_flaky_tests`). All three classes my edit touches pass deterministically in isolation (ResultCheckAndDropFacts 3/3, ResultAckTests 6/6, ResultConsumeTests 2/2). The new cancelled read is read-only and, under the existing `NoopRedis` (Null for every key), is a pass-through that cannot alter the pre-existing tests' behavior.

## Deferred Issues

- **[Out of scope] Flaky Orchestrator in-memory-harness tests.** 1-5 non-deterministic failures in `BaseApi.Tests.Orchestrator` in a full-suite run, present on the baseline with this plan's change stashed. Not caused by this plan. The live phase-32 close gate (3xGREEN + triple-SHA, Plan 06) is the authoritative full-suite signal; these races are a known prior-phase concern, not a 32-05 deliverable.
- **[Out of scope, NOT committed] `tests/BaseApi.Tests/Processor/CheckAndDropFacts.cs`** carried a pre-existing uncommitted working-tree edit (threading `DispatchTestKit.Retry()` into the processor consumer ctor — a 32-04 follow-up). Left untouched; NOT staged in either of this plan's commits (scoped-commit discipline).

## Deviations from Plan

None of substance — the single autonomous TDD task executed as written, with the literal plan snippet for both edits realized verbatim. The plan's `<verification>` note explicitly anticipated possible Rule-3 realignment if a pre-existing ResultConsumer test now hit the cancelled read; none was needed (the existing `NoopRedis` already returns Null for the cancelled key, so all pre-existing facts pass unchanged).

No auth gates. No architectural decisions required (Rule 4 not triggered).

## Must-Haves Status

- [x] ResultConsumer ack-and-discards an in-flight ExecutionResult when the cancelled marker is set (ResultCheckAndDropFacts test 2).
- [x] Results for other (un-cancelled) workflows are unaffected (ResultCheckAndDropFacts test 3, workflowId-keyed).
- [x] The flag[H]==Ack drop gate increments orchestrator_result_deduped once (ResultCheckAndDropFacts test 1, MeterListener).
- [x] The cancelled check reads the cancelled marker only, never flag[H] (D-13) — and never WRITES the marker (grep `StringSetAsync.*Cancelled` = 0; test 2 asserts no StringSetAsync on the cancelled path).

## Threat Surface Scan

No new security-relevant surface beyond the plan's `<threat_model>`. T-32-03 (in-flight result advancing a cancelled workflow) is MITIGATED: the check-and-drop gate reads `skp:cancelled:{m.WorkflowId:D}` before the L1 advance and ack-and-discards when set (test 2 asserts no dispatch). T-32-02 (marker read) is ACCEPTED: read-only against the broker-internal Redis on the same trust boundary as the existing flag/data dedup reads; the orchestrator never writes the marker (asserted). T-32-08 (per-message extra Redis GET) is ACCEPTED. No new network endpoint, auth path, file access, or schema change.

## Known Stubs

None — the gate is wired to a real Redis read and the dedup counter to a real metrics increment. No placeholder/empty-value flow.

## Self-Check: PASSED

- `src/Orchestrator/Consumers/ResultConsumer.cs` present (modified); `tests/BaseApi.Tests/Orchestrator/ResultCheckAndDropFacts.cs` present (created).
- Both task commits present in git history: `00bfeb2` (test RED), `e43bc1b` (feat GREEN).
- No accidental file deletions across the two commits.
