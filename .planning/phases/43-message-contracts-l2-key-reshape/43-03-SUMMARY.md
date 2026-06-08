---
phase: 43-message-contracts-l2-key-reshape
plan: 03
subsystem: orchestrator-processor-consumers
tags: [consumer-adaptation, dispatch-path, result-path, straight-through, guid-entryid, retire-h-dedup, retire-manifest, wave-2, breaking-change]

# Dependency graph
requires:
  - phase: 43-message-contracts-l2-key-reshape
    plan: 02
    provides: "Reshaped Messaging.Contracts — EntryStepDispatch (no H, Guid EntryId), four Step* records (StepCompleted/StepFailed/StepCancelled), IStepResult marker, SourceStep.IsSource sentinel, L2ProjectionKeys.ExecutionData(Guid) sole overload; ExecutionResult + MessageIdentity deleted"
provides:
  - "Orchestrator dispatch path (IStepDispatcher/StepDispatcher/WorkflowFireJob) adapted to Guid entryId, no H/flag pre-write, no Redis dep (D-03)"
  - "ResultConsumer re-targeted IConsumer<ExecutionResult> -> IConsumer<StepCompleted>: no dedup, no manifest, no Redis; one message = one item via SelectNext(StepOutcome.Completed) (D-03/D-06e/A4)"
  - "EntryStepDispatchConsumer straight-through: no CAS dedup, no content-addressing, no manifest; per result mints Guid entryId, writes L2[data(entryId)], sends one Step* record (D-03)"
  - "entryId flows as a Guid end-to-end on the orchestrator + processor execution path; entry-step fire seeds Guid.Empty (SC-2)"
affects: [43-04 (Keeper dark-path rebind — still red on OLD contracts, the intended wave boundary), 43-05 (full-suite GREEN gate), Phase 44 (Pre/In/Post pipeline), Phase 46 (typed-consumer routing)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Single-source sentinel predicate at the consumer hop — EntryStepDispatchConsumer input-read skip routes through SourceStep.IsSource(dispatch.EntryId), never an inline == Guid.Empty (D-07)"
    - "Straight-through (single-result, no-dedup, no-manifest) consumer body — the minimal compile-and-pass posture distinct from the v4 Pre/In/Post pipeline (Phase 44) and typed-consumer routing (Phase 46)"
    - "Explicit string outcomeLabel passed into the single Send owner (SendResult) — the metric outcome tag is supplied by each build path because Step* records carry no Outcome wire field (Pitfall 4)"
    - "endpoint.Send((object)result) so MassTransit routes/serializes the runtime Step* type, not the IStepResult interface"

key-files:
  created: []
  modified:
    - src/Orchestrator/Dispatch/IStepDispatcher.cs
    - src/Orchestrator/Dispatch/StepDispatcher.cs
    - src/Orchestrator/Scheduling/WorkflowFireJob.cs
    - src/Orchestrator/Consumers/ResultConsumer.cs
    - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
  deleted: []

key-decisions:
  - "Program.cs needed NO edit: both ResultConsumer (AddConsumer<ResultConsumer, ResultConsumerDefinition>) and StepDispatcher (AddSingleton<IStepDispatcher, StepDispatcher>) are DI-resolved, so dropping the IConnectionMultiplexer ctor param required no registration change — MassTransit/DI resolves the remaining params. files_modified listed Program.cs defensively; it was a no-op (no Redis-arg construction site exists, grep `new ResultConsumer(`/`new StepDispatcher(` = 0)."
  - "ResultConsumerDefinition references the consumer TYPE (ResultConsumer), never ExecutionResult, so re-targeting the consumer message type to StepCompleted left the definition untouched."
  - "SendResult takes an explicit string outcomeLabel from each build path rather than reading result type at runtime — the OutcomeLabel(StepOutcome) switch was deleted (Step* carry no Outcome by design, Pitfall 4); SendResult stays the single send owner so no send path is uncounted."

patterns-established:
  - "Consumer-hop source-step skip routes through SourceStep.IsSource (mirrors the ExecutionLogScope skip from Plan 02) — the one sentinel predicate is now applied at both the log-scope and the input-read sites"

requirements-completed: [MSG-01, MSG-02]

# Metrics
duration: 6min
completed: 2026-06-08
---

# Phase 43 Plan 03: Orchestrator + Processor Consumer Adaptation Summary

**Adapted the four execution-path consumer files to Plan-02's six-id no-H Guid-entryId contracts using the simplest straight-through behavior — made entryId a Guid end-to-end, seeded Guid.Empty at the entry-step fire, deleted the flag[H]/CAS dedup (RETIRE-01) + content-addressing + manifest fan-out (RETIRE-02) + the placeholder Redis result read, re-targeted ResultConsumer to IConsumer<StepCompleted>, and re-shaped EntryStepDispatchConsumer to emit one StepCompleted/StepFailed/StepCancelled per result — both target projects compile 0/0 against the new symbols.**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-06-08T12:06:06Z
- **Completed:** 2026-06-08T12:11:56Z
- **Tasks:** 3
- **Files:** 5 modified (0 created, 0 deleted)

## Accomplishments

- **Task 1 (dispatch path, SC-2):** `IStepDispatcher.DispatchAsync` + `StepDispatcher.DispatchAsync` signature `string entryId` -> `Guid entryId`. Deleted the `MessageIdentity.ComputeH` compute, the `EntryStepDispatch { H = h }` stamp, the `flag[H]="Pending"` pre-write, the `IConnectionMultiplexer redis` ctor dep, the `FlagTtl` field, and `using Messaging.Contracts.Hashing;`. `WorkflowFireJob` drops `MessageIdentity.EntryEntryId` and seeds `entryId = Guid.Empty` (the source sentinel) into `DispatchAsync`. Kept the `Send` block + `DispatchSent` metric.
- **Task 2 (result path, SC-2 / D-06e / A4):** `ResultConsumer` re-targeted `IConsumer<ExecutionResult>` -> `IConsumer<StepCompleted>` (dropped the alias). Deleted the `IConnectionMultiplexer redis` dep + `redis.GetDatabase()`, the `Flag(m.H)=="Ack"` dedup gate + `ResultDeduped`, the manifest `ExecutionData(m.EntryId)` read, and the effect-first `Flag` flip. Collapsed the N×M `foreach item × SelectNext` to one message = one item via `SelectNext(StepOutcome.Completed, ...)` with `m.EntryId` now a `Guid`. Kept `ResultConsumed` + the L1-miss business-ack.
- **Task 3 (processor consumer, D-03):** `EntryStepDispatchConsumer` re-shaped straight-through. Deleted the `Flag(dispatch.H)=="Ack"` CAS gate + `DispatchDeduped`, the `HashBlob` content-address write, the `HashManifest` manifest assembly, the outbound `flag[resultH]="Pending"` pre-write, and the inbound CAS flip. Input-read skip now routes through `SourceStep.IsSource(dispatch.EntryId)` (Guid sentinel). Per result: mint a `Guid` entryId (`NewId.NextGuid()`), write `L2[data(entryId)]`, send ONE `StepCompleted`. Builders -> `StepCompleted`/`StepFailed`/`StepCancelled` (Completed carries the real Guid key; Failed/Cancelled set `EntryId = Guid.Empty`). `SendResult` takes an explicit `string outcomeLabel`; the `OutcomeLabel(StepOutcome)` switch was removed.
- **Both target projects compile 0/0** under TreatWarningsAsErrors: `dotnet build src/Orchestrator/Orchestrator.csproj` and `dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj` both `Build succeeded` (0 errors, 0 warnings).

## Task Commits

1. **Task 1: orchestrator dispatch path — Guid entryId, no H/flag (D-03)** - `5cd3a4b` (feat)
2. **Task 2: ResultConsumer straight-through StepCompleted (D-03/D-06e/A4)** - `e38acaa` (feat)
3. **Task 3: EntryStepDispatchConsumer straight-through Step* records (D-03)** - `b3a7fef` (feat)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Restored `Messaging.Contracts.Projections` using in WorkflowFireJob.cs**
- **Found during:** Task 2 build (Orchestrator project would not compile after the Task 1 commit)
- **Issue:** Task 1 removed `using Messaging.Contracts.Hashing;` from `WorkflowFireJob.cs`. The file ALSO imported `using Messaging.Contracts.Projections;`, which I initially removed believing it carried only `L2ProjectionKeys`. That namespace actually carries `LivenessProjection` (used in the L1 liveness-refresh `new LivenessProjection(...)` at the bottom of `Execute`), so the build failed CS0246 on `LivenessProjection`. The `Projections` using is load-bearing and was restored.
- **Fix:** Re-added `using Messaging.Contracts.Projections;` to `WorkflowFireJob.cs`. Committed together with the Task 2 ResultConsumer change (the change that surfaced it on the Orchestrator-project build).
- **Files modified:** src/Orchestrator/Scheduling/WorkflowFireJob.cs
- **Commit:** `e38acaa`

### Task-grouping note (no behavior change)

`Program.cs` is in `files_modified` but received NO edit. The plan anticipated that dropping the `IConnectionMultiplexer` ctor param from `StepDispatcher`/`ResultConsumer` MIGHT ripple to the DI registration (Pitfall 5). It did not: both types are DI-resolved (`AddSingleton<IStepDispatcher, StepDispatcher>` and `AddConsumer<ResultConsumer, ResultConsumerDefinition>`), so MassTransit/DI resolves the remaining ctor params automatically, and there is no `new StepDispatcher(`/`new ResultConsumer(` Redis-arg construction site in `src/` (grep = 0). The Program.cs entry was defensive; leaving it untouched is the minimal correct outcome.

## Authentication Gates

None.

## Issues Encountered

**Wave-boundary build state (by design):** This plan deliberately leaves the Keeper consumer files (Plan 43-04's scope — `KeeperRecoveryHandler`, `L2ProbeRecovery`, the `Fault<ExecutionResult>` consumer) and the sibling consumer-test files (Plan 43-05's full-suite gate) untouched, so the SOLUTION-WIDE build remains red on those files until Plans 04/05. Verification was performed by isolated project builds of the two files_modified target projects (Orchestrator + BaseProcessor.Core), both 0/0 — the plan's own `<verification>` scopes exactly these two `dotnet build` commands. The Keeper-path red is the intended Wave-2 boundary, not a regression.

## Known Stubs

None. The straight-through behavior is the deliberate D-03 milestone posture (single-result + no-dedup + no-manifest), explicitly NOT a stub: it is the final shape for this phase. The v4 Pre/In/Post pipeline (Phase 44) and typed-consumer routing (Phase 46) are tracked future work, not placeholders left in this code. `metrics.DispatchDeduped` / `metrics.ResultDeduped` counters on the metrics classes are now unreferenced from these consumers (the dedup gates that fed them are retired) — left in place as public meter fields (no behavior, no stub; their full retirement is a later-phase cleanup if desired).

## Threat Flags

None. The two trust boundaries in the plan's threat_model are honored: the dispatch-consumer input read on `dispatch.EntryId` routes through the single `SourceStep.IsSource` predicate (T-43-06 mitigated — a Guid.Empty dispatch deterministically skips the L2 read), and the result consumer no longer holds an `IConnectionMultiplexer` (T-43-08 — reduced attack surface). The lost dedup (T-43-07) is the accepted intentional at-least-once posture (RESIL-03). No new network endpoint, auth path, file-access pattern, or schema change at a trust boundary was introduced.

## Self-Check: PASSED

- All 5 modified files exist on disk and compile (Orchestrator + BaseProcessor.Core both `Build succeeded` 0/0).
- No files created or deleted (`git diff --diff-filter=D` across the 3 task commits = empty).
- All three task commits exist in git history (5cd3a4b, e38acaa, b3a7fef).
- Acceptance greps confirmed: ResultConsumer.cs has 0 `MessageIdentity`/`Flag(`/`.H`/`ExecutionResult` matches; EntryStepDispatchConsumer.cs has 0 `MessageIdentity`/`HashBlob`/`HashManifest`/`Flag(`/`dispatch.H`/`Hashing`/`string.IsNullOrEmpty(dispatch.EntryId)`/`.Outcome` matches and contains `SourceStep.IsSource(dispatch.EntryId)` + `L2ProjectionKeys.ExecutionData(` + all three Step* builders.

---
*Phase: 43-message-contracts-l2-key-reshape*
*Completed: 2026-06-08*
