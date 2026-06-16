---
phase: 71-orchestrator-recovery-pipeline
plan: 04
subsystem: infra
tags: [masstransit, redis, keeper-recovery, partitioner, nsubstitute, xunit-v3]

# Dependency graph
requires:
  - phase: 71-orchestrator-recovery-pipeline (plan 02)
    provides: "OrchestratorInject / OrchestratorReinject IKeeperRecoverable contracts (origin-split, StepOutcome discriminator + union fields)"
  - phase: 70-processor-inject-cleanup
    provides: "RecoveryConsumerBase<T> Guard base + KeeperDeleteInvariantFacts non-destructive-INJECT pattern + 5-arg StringSetAsync stub"
provides:
  - "OrchestratorInjectConsumer — completes the origin->newEntryId copy then dispatches EntryStepDispatch to queue:{NextProcessorId} (non-destructive)"
  - "OrchestratorReinjectConsumer — D-07 outcome->IStepResult factory re-injecting to queue:orchestrator-result (the only status branch)"
  - "Both consumers bound on the existing keeper-recovery endpoint (same partitioner, same 4-tuple selector, no new queue) + ExcludeFromConfigureEndpoints"
  - "Behavioral D-09 delete-invariant facts proving neither consumer deletes (both KeyDeleteAsync overloads + positive co-assertion)"
affects: [71-orchestrator-recovery-pipeline live proof / close gate, orchestrator result-consume path]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Origin-split keeper-recovery consumer pair (Processor*/Orchestrator*) sharing one Partitioner + 4-tuple selector on one endpoint"
    - "Outcome->IStepResult reconstruction factory as the single status branch (exhaustive switch with safe StepFailed default)"
    - "CapturingSendProvider boxed-object Send capture for polymorphic IStepResult re-sends"

key-files:
  created:
    - src/Keeper/Recovery/OrchestratorInjectConsumer.cs
    - src/Keeper/Recovery/OrchestratorReinjectConsumer.cs
    - tests/BaseApi.Tests/Keeper/OrchestratorInjectConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/OrchestratorReinjectConsumerFacts.cs
  modified:
    - src/Keeper/Recovery/RecoveryEndpointBinder.cs
    - src/Keeper/Program.cs
    - tests/BaseApi.Tests/Keeper/KeeperDeleteInvariantFacts.cs
    - tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs

key-decisions:
  - "OrchestratorInject dispatches the EntryStepDispatch even when the origin key is absent (forward-only, no-write no-op) — INJECT never deletes, so an absent origin removes nothing and the forward pass still proceeds"
  - "OrchestratorReinject re-sends the result boxed as (object) so a single Send overload carries every IStepResult subtype; CapturingSendProvider extended to record the boxed-object Send (RESEARCH A6)"
  - "Used SK_P.sln for the build verification — the plan's SK_P4.sln does not exist (Rule 3 blocking adaptation)"

patterns-established:
  - "Pattern: a new keeper-recovery message type binds by adding one UsePartitioner<T> (same partition instance + same PartitionGuid selector) + one ConfigureConsumer<T> in the binder callback, plus AddConsumer<T>().ExcludeFromConfigureEndpoints() in Program.cs — no new queue"
  - "Pattern: prove non-destructiveness behaviorally — DidNotReceive on BOTH KeyDeleteAsync overloads co-asserted with Assert.Single + Assert.IsType on the captured send so a no-op cannot pass"

requirements-completed: [ORCV-06, ORCV-07]

# Metrics
duration: 5min
completed: 2026-06-16
---

# Phase 71 Plan 04: Orchestrator Keeper-Recovery Consumers Summary

**Two new keeper-recovery consumers (OrchestratorInject copy+dispatch, OrchestratorReinject outcome->IStepResult factory) bound on the existing keeper-recovery endpoint via the shared partitioner with no new queue, both behaviorally proven never to delete.**

## Performance

- **Duration:** 5 min
- **Started:** 2026-06-16T08:36:53Z
- **Completed:** 2026-06-16T08:41:07Z
- **Tasks:** 2
- **Files modified:** 8 (4 created, 4 modified)

## Accomplishments
- `OrchestratorInjectConsumer` completes the origin->newEntryId index+data copy (read `L2[OriginEntryId]`, SET `L2[EntryId]`) then sends a reconstructed `EntryStepDispatch` to `queue:{NextProcessorId}` — non-destructive, deletes nothing.
- `OrchestratorReinjectConsumer` reconstructs the matching `IStepResult` subtype from the carried `StepOutcome` (the only status branch, exhaustive default -> safe `StepFailed`) and re-injects to `queue:orchestrator-result`.
- Both consumers extend `RecoveryConsumerBase<T>` and bind on the existing `keeper-recovery` endpoint via the same `Partitioner` instance + same `ReinjectConsumerDefinition.PartitionGuid` 4-tuple selector; registered with `ExcludeFromConfigureEndpoints()`. `KeeperQueues.Recovery = "keeper-recovery"` unchanged — no new queue.
- `KeeperDeleteInvariantFacts` extended with `OrchestratorInjectConsumer_never_deletes` and `OrchestratorReinjectConsumer_never_deletes` — each asserts `DidNotReceive()` on BOTH `KeyDeleteAsync` overloads, co-asserted with a positive captured send (Assert.Single + Assert.IsType).
- Solution builds 0-warning Debug + Release; all targeted facts green.

## Task Commits

1. **Task 1: Add OrchestratorInject/Reinject consumers + bind + register** - `2da54a9` (feat)
2. **Task 2: Consumer facts + extend KeeperDeleteInvariantFacts (D-09)** - `773b385` (test)

**Plan metadata:** see final docs commit.

_Note: Task 2 is `tdd="true"`. Because Task 1 already delivered the consumers under test, the RED/GREEN cycle could not be split into separate test->feat commits for this plan; the facts were authored and verified GREEN in one `test(...)` commit against the Task-1 implementation. See TDD Gate Compliance below._

## Files Created/Modified
- `src/Keeper/Recovery/OrchestratorInjectConsumer.cs` - FORWARD-escalation consumer (copy origin->newEntryId + dispatch EntryStepDispatch; no delete)
- `src/Keeper/Recovery/OrchestratorReinjectConsumer.cs` - REINJECT consumer (outcome->IStepResult factory; re-inject to orchestrator-result; no delete)
- `src/Keeper/Recovery/RecoveryEndpointBinder.cs` - added 2 UsePartitioner<Orchestrator*> (same partition + selector) + 2 ConfigureConsumer<Orchestrator*Consumer>
- `src/Keeper/Program.cs` - added 2 AddConsumer<Orchestrator*Consumer>().ExcludeFromConfigureEndpoints()
- `tests/BaseApi.Tests/Keeper/OrchestratorInjectConsumerFacts.cs` - copy-then-dispatch + deletes-nothing fact
- `tests/BaseApi.Tests/Keeper/OrchestratorReinjectConsumerFacts.cs` - [Theory] over all 4 StepOutcome values -> matching IStepResult subtype
- `tests/BaseApi.Tests/Keeper/KeeperDeleteInvariantFacts.cs` - 2 new behavioral never-deletes facts
- `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs` - CapturingSendProvider now captures the boxed-object Send (RESEARCH A6)

## Decisions Made
- **Absent-origin dispatch:** OrchestratorInject still sends the EntryStepDispatch when `L2[OriginEntryId]` is absent (skips only the copy SET). INJECT is forward-only and deletes nothing, so an absent origin removes nothing and the forward pass proceeds — consistent with the processor INJECT's non-destructive posture.
- **Boxed-object re-send:** OrchestratorReinject sends `(object)result` so one Send overload carries every IStepResult subtype; `CapturingSendProvider` gained a generic `Send(object,...)` capture to record StepFailed/StepCancelled/StepProcessing (RESEARCH A6).
- **Outcome factory default:** the exhaustive `_` arm degrades an unknown outcome to a safe `StepFailed` (T-71-10) — never an exception or a mis-typed result.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Build/test against SK_P.sln, not SK_P4.sln**
- **Found during:** Task 1 (build verification)
- **Issue:** The plan's verify commands reference `SK_P4.sln`, which does not exist in the repo; the actual solution file is `SK_P.sln` (confirmed by glob and called out in the phase critical constraints).
- **Fix:** Ran all `dotnet build`/`dotnet test` verification against `SK_P.sln`.
- **Files modified:** None (tooling-only adaptation).
- **Verification:** `dotnet build SK_P.sln -c Debug` and `-c Release` both Build succeeded, 0 warnings.
- **Committed in:** N/A (no file change)

---

**Total deviations:** 1 auto-fixed (1 blocking, tooling-only)
**Impact on plan:** No code/scope impact. Solution-file name corrected per the phase's own LOCKED note; all in-plan acceptance criteria met.

## TDD Gate Compliance

Task 2 carried `tdd="true"`, but the consumers under test were delivered in Task 1 (a non-TDD `auto` task whose own verification is a build). The RED->GREEN split was therefore not separable into `test(...)` then `feat(...)` commits: the facts were authored and verified GREEN against the existing implementation and committed as a single `test(71-04)` commit (`773b385`). No RED commit precedes a GREEN feat commit for this plan. The behavioral negative-guard facts nonetheless provide the intended regression protection (a reintroduced delete of either overload, or a silent no-op, fails the fact).

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Keeper side of ORCV-06 (the two consumers + D-07 factory + bind) and ORCV-07 (delete invariant) complete and green.
- Ready for the phase live proof / close gate. The orchestrator-side producers (Plan 03, src/Orchestrator/*) complete the forward/recovery emit path that feeds these consumers.

## Self-Check: PASSED

All claimed created files exist (both consumers, both consumer-fact files, the SUMMARY) and both task commits (`2da54a9`, `773b385`) are present in git history.

---
*Phase: 71-orchestrator-recovery-pipeline*
*Completed: 2026-06-16*
