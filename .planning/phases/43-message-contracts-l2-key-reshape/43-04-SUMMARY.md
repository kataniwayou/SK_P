---
phase: 43-message-contracts-l2-key-reshape
plan: 04
subsystem: keeper-reactive-recovery-dark-path
tags: [keeper, reactive-recovery, dark-path, d-14, compile-preserving, guid-entryid, retarget, wave-2, dormant-feature]

# Dependency graph
requires:
  - phase: 43-message-contracts-l2-key-reshape
    plan: 02
    provides: "Reshaped Messaging.Contracts — IExecutionCorrelated without H (Guid EntryId), ExecutionResult deleted (Fault<ExecutionResult> no longer resolves), ExecutionData(Guid) sole overload + CompositeBackup builder, KeeperProbe/KeeperRecoverAttempts kept string-keyed, PauseWorkflow/ResumeWorkflow carry their OWN string H positional"
provides:
  - "KeeperRecoveryHandler retargeted off the removed IExecutionCorrelated.H onto a local 4-tuple key (L2ProjectionKeys.CompositeBackup) — DARK but compiling (D-14)"
  - "L2ProbeRecovery.RunAsync entryId retyped string -> Guid, bound to the surviving ExecutionData(Guid) overload"
  - "Fault<ExecutionResult> consumer neutralized (retargeted onto the surviving StepCompleted, file retained, registration dropped); the reactive recovery FEATURE survives registered on Fault<EntryStepDispatch>"
  - "Keeper.csproj builds clean 0/0 against the Plan-02 reshaped contracts"
affects: [43-05 (full-suite GREEN gate), Phase 47/48 (RETIRE-03 — the real retirement of this dormant reactive path)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Dark-path compile-preserving retarget (D-14): a feature whose wire dependencies were deleted is kept present-and-compiling by deriving a LOCAL identity key from surviving ids (CompositeBackup) instead of the removed member, NOT deleted — its retirement is a later milestone"
    - "Local identity key from the partition 4-tuple replaces a removed wire content-hash — used only for an internal probe-attempts/pause-key slot, never an auth or dedup decision (T-43-10 accepted)"
    - "Neutralize-and-keep over delete for the unresolvable consumer: retarget the broken generic onto a surviving IExecutionCorrelated type (StepCompleted) and drop only the registration, so the diff shows no reactive-recovery file disappearing (D-14 diff guard)"

key-files:
  created: []
  modified:
    - src/Keeper/Recovery/KeeperRecoveryHandler.cs
    - src/Keeper/Recovery/L2ProbeRecovery.cs
    - src/Keeper/Consumers/FaultExecutionResultConsumer.cs
    - src/Keeper/Program.cs
  deleted: []

key-decisions:
  - "Neutralize-and-keep (NOT delete) for FaultExecutionResultConsumer + its Definition: the plan and 43-PATTERNS §Dark Path sanction BOTH deleting the pair and neutralizing-the-body; chose neutralize-and-keep because it most strictly honors the D-14 diff guard ('the diff must NOT show FaultExecutionResultConsumer.cs disappearing'). The consumer's broken Fault<ExecutionResult> generic is retargeted onto the surviving result-path contract StepCompleted (IStepResult : IExecutionCorrelated); the file + ConsumerDefinition stay compiling, the registration is removed. Zero file deletions in this plan."
  - "localKey feeds FOUR string slots that survived H's removal: PauseWorkflow(WorkflowId, string H) + ResumeWorkflow(WorkflowId, string H) (these records carry their OWN string H positional, independent of the interface — confirmed by reading the records), KeeperRecoverAttempts(string), and the intake log's {H} structured hole. All take the CompositeBackup-derived local string; no wire H is re-introduced."
  - "L2ProbeRecovery.RunAsync param retyped string entryId -> Guid entryId (minimal change per 43-PATTERNS §Dark Path 'minimal change is re-typing to Guid entryId'); inner.EntryId is already a Guid so the call site needed no conversion. Added 'using System;' to L2ProbeRecovery.cs for the Guid token (it previously imported only System.Collections.Generic)."
  - "FaultEntryStepDispatchConsumer + FaultEntryStepDispatchConsumerDefinition were NOT touched — they already bind Fault<EntryStepDispatch> (a surviving type) and remain the sole registered reactive consumer carrying the feature. KeeperRecoveryHandler (the shared body they delegate to) is retained and referenced."

patterns-established:
  - "When a milestone deletes a wire dependency of a feature whose retirement is sequenced LATER, apply a compile-preserving retarget (local-key substitution + generic retarget onto a surviving type) and keep every file — the diff must not telegraph the later retirement"

requirements-completed: [MSG-01, MSG-02]

# Metrics
duration: 4min
completed: 2026-06-08
---

# Phase 43 Plan 04: Keeper Reactive-Recovery Dark Path (D-14) Summary

**Kept the reactive Keeper recovery path DARK-but-compiling against Plan-02's reshaped contracts (D-14): rebound KeeperRecoveryHandler off the removed IExecutionCorrelated.H onto a local CompositeBackup 4-tuple key, retyped L2ProbeRecovery to the surviving ExecutionData(Guid) overload, and neutralized the now-unresolvable Fault<ExecutionResult> consumer by retargeting it onto StepCompleted + dropping its registration — while retaining the reactive recovery FEATURE registered on Fault<EntryStepDispatch>. Keeper builds clean 0/0; zero files deleted; the feature's real retirement stays RETIRE-03 (Phases 47/48).**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-06-08T12:14:35Z
- **Completed:** 2026-06-08T12:18Z
- **Tasks:** 2
- **Files:** 4 modified (0 created, 0 deleted)

## Accomplishments

- **Task 1 (handler + probe retarget, D-14):** `KeeperRecoveryHandler.HandleAsync<T>` keeps its `where T : class, IExecutionCorrelated` bound (the interface still exists) but STOPS reading `inner.H`. It now derives `var localKey = L2ProjectionKeys.CompositeBackup(inner.CorrelationId, inner.WorkflowId, inner.ProcessorId, inner.ExecutionId)` once at intake and passes it to all five former `inner.H` sites: the intake-log `{H}` hole, `PauseWorkflow(WorkflowId, localKey)`, `recovery.RunAsync(inner.EntryId, localKey, ...)`, `KeeperRecoverAttempts(localKey)`, and `ResumeWorkflow(WorkflowId, localKey)`. `L2ProbeRecovery.RunAsync` param retyped `string entryId -> Guid entryId`; `ExecutionData(entryId)` now binds the surviving Guid overload; `KeeperProbe(h)` stays string-keyed (caller passes `localKey`). The bounded probe-loop body is otherwise unchanged. Build at this point errored ONLY on the Fault<ExecutionResult> consumer (Task 2's scope) — the handler + probe compiled, exactly per the acceptance criteria.
- **Task 2 (consumer neutralization, D-14):** Removed the `x.AddConsumer<FaultExecutionResultConsumer, FaultExecutionResultConsumerDefinition>()` registration from `Program.cs`. Retargeted `FaultExecutionResultConsumer`'s unresolvable `IConsumer<Fault<ExecutionResult>>` onto `IConsumer<Fault<StepCompleted>>` (StepCompleted is the surviving result-path `IStepResult : IExecutionCorrelated` from Plan 03), dropped the `ExecutionResult` alias, kept the file + its `ConsumerDefinition` compiling, and kept it delegating to the single `KeeperRecoveryHandler` body. `FaultEntryStepDispatchConsumer` (Fault<EntryStepDispatch>) is untouched and remains the sole registered reactive consumer carrying the feature. `KeeperQueues.FaultRecovery` / `KeeperQueues.DeadLetter` / the keeper-fault-recovery binding / `KeeperRecoveryHandler` are all retained (their retirement is Phases 47/48).
- **Keeper builds clean 0/0:** `dotnet build src/Keeper/Keeper.csproj` -> `Build succeeded`, 0 warnings, 0 errors.

## Task Commits

1. **Task 1: retarget KeeperRecoveryHandler off inner.H + L2ProbeRecovery to ExecutionData(Guid) (D-14)** - `27b2c5a` (refactor)
2. **Task 2: neutralize Fault<ExecutionResult> consumer; retain reactive feature on Fault<EntryStepDispatch> (D-14)** - `97c0a25` (refactor)

## Deviations from Plan

### Choice within plan-sanctioned options (not a deviation)

The plan offered TWO sanctioned options for the `Fault<ExecutionResult>` consumer: (a, preferred per 43-PATTERNS §Dark Path) delete the consumer/definition pair because `Fault<EntryStepDispatch>` preserves the feature; or (b) neutralize the body and keep the file "if any ambiguity remains." **Chose option (b) — neutralize-and-keep** — because it most strictly satisfies the D-14 diff guard, whose load-bearing wording is "the diff must NOT show KeeperRecoveryHandler.cs / FaultExecutionResultConsumer.cs disappearing." Deleting the pair would technically be allowed (the feature survives via the dispatch consumer), but keeping every file guarantees the diff cannot be read as the reactive path being retired early (RETIRE-03 is Phases 47/48). Net effect on the running bus is identical to option (a): only `FaultEntryStepDispatchConsumer` is registered. Result: **zero file deletions in this plan.**

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Added `using System;` to L2ProbeRecovery.cs**
- **Found during:** Task 1
- **Issue:** Retyping the `RunAsync` `entryId` param to `Guid` introduced a `Guid` token, but `L2ProbeRecovery.cs` imported only `System.Collections.Generic` (not `System`), so the `Guid` type would not resolve.
- **Fix:** Added `using System;` to the import block.
- **Files modified:** src/Keeper/Recovery/L2ProbeRecovery.cs
- **Commit:** `27b2c5a`

## Authentication Gates

None.

## Issues Encountered

**Residual `inner.H` / `ExecutionResult` string matches are doc-only (by design):** A post-edit grep for `inner\.H|ExecutionResult` under `src/Keeper/` returns matches ONLY in XML-doc / inline comments / identifiers — `KeeperRecoveryHandler.cs` doc explaining the retarget, `Program.cs` doc explaining the neutralization, the retained `FaultExecutionResultConsumer*.cs` file name + doc, the `FaultEntryStepDispatchConsumerDefinition.cs` cross-reference, and a `KeeperMetrics.cs` instrument `<summary>` describing the (now tag-driven) result-fault intake. There is NO live `inner.H` member access and NO live `Messaging.Contracts.ExecutionResult` type reference — the clean 0/0 build proves the deleted type is no longer bound anywhere. These prose references are intentional historical/explanatory context for the dark path.

**Full-suite GREEN is Plan 43-05's gate (by design):** This plan scopes its verification to `dotnet build src/Keeper/Keeper.csproj` (the plan's own `<verification>`); the solution-wide build + the sibling consumer-test files remain Plan 05's full-suite gate. Staying strictly within the files_modified list (src/Keeper/...) per the plan notes.

## Known Stubs

The reactive Keeper recovery path is INTENTIONALLY DORMANT (DARK), not a stub: it compiles and stays registered (on `Fault<EntryStepDispatch>`) but promises no v4 behavior — this is the user-authorized D-14 milestone-sequencing posture, with the path's real retirement (RETIRE-03) deferred to Phases 47/48. The retargeted-but-unregistered `FaultExecutionResultConsumer` (now bound to `Fault<StepCompleted>`) is a retained-for-diff-guard dormant file, also by design. Neither is a placeholder/empty-data flow blocking a plan goal — the plan's goal IS to keep the feature present-and-compiling-yet-dark.

## Threat Flags

None. The plan's two threat-register rows are honored as accepted: T-43-09 (the handler going DARK but registered) — the path processes no new external input differently and is deliberately dormant; T-43-10 (localKey from CompositeBackup replacing inner.H) — the key is derived deterministically from the same four trusted in-process ids and is used only for an internal probe-attempts / pause-key string slot, never an auth or dedup decision. No new network endpoint, auth path, file-access pattern, or schema change at a trust boundary was introduced.

## Self-Check: PASSED

- All 4 modified files exist on disk; Keeper builds `Build succeeded` 0/0.
- No files created or deleted (`git diff --diff-filter=D HEAD~2 HEAD` = empty across both task commits).
- Both task commits exist in git history (27b2c5a, 97c0a25).
- Acceptance greps confirmed: KeeperRecoveryHandler.cs contains `L2ProjectionKeys.CompositeBackup(` and has NO live `inner.H` access; L2ProbeRecovery.RunAsync signature contains `Guid entryId`; Program.cs has NO `FaultExecutionResultConsumer` registration and the surviving `AddConsumer<FaultEntryStepDispatchConsumer, ...>` is the sole registration; FaultEntryStepDispatchConsumer.cs (Fault<EntryStepDispatch>) + KeeperRecoveryHandler.cs both still exist (feature retained).

---
*Phase: 43-message-contracts-l2-key-reshape*
*Completed: 2026-06-08*
