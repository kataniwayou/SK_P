---
phase: 24-orchestrator-result-consume-step-advancement
plan: 03
subsystem: orchestrator-messaging
tags: [orchestrator, dispatch, step-advancement, entry-condition, tdd, masstransit]
dependency_graph:
  requires: [24-01]
  provides: [24-04, 24-05]
  affects: [Orchestrator.Dispatch, Orchestrator.Scheduling, Orchestrator.Consumers]
tech_stack:
  added: []
  patterns: [single-owner-dispatch, pure-match-helper, sealed-exception, tdd-red-green]
key-files:
  created:
    - src/Orchestrator/Dispatch/IStepDispatcher.cs
    - src/Orchestrator/Dispatch/StepDispatcher.cs
    - src/Orchestrator/Dispatch/StepAdvancement.cs
    - src/Orchestrator/Consumers/GateClosedException.cs
    - tests/BaseApi.Tests/Orchestrator/StepAdvancementTests.cs
  modified:
    - src/Orchestrator/Scheduling/WorkflowFireJob.cs
    - tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs
  decisions: []
metrics:
  duration: ~75min
  completed: 2026-06-01
requirements: [ORCH-ADVANCE-01, ORCH-ADVANCE-02]
---

# Phase 24 Plan 03: Step Dispatch Extraction + Pure Step Advancement Summary

One-liner: Extracted the EntryStepDispatch build+Send into a single-owner IStepDispatcher/StepDispatcher (refactoring WorkflowFireJob to call it), added the pure I/O-free StepAdvancement.SelectNext outcome-to-entry-condition match (== (int)outcome || == Always(4), Never(5) never selected), and the gate-closed GateClosedException type -- the dispatch/match/throw contracts Plan 04's result consumer builds on.

## What Was Built

- **IStepDispatcher / StepDispatcher** (D-01): the single owner of the EntryStepDispatch build-and-Send shape -- Send (not Publish) to queue:{processorId:D} with executionId/entryId parameterized (not forced Guid.Empty) so the result-continuation path (Plan 04) can carry real values. Infra fault on Send propagates unchanged.
- **WorkflowFireJob refactor**: ctor now injects IStepDispatcher dispatcher (replacing ISendEndpointProvider sendProvider); the inline new EntryStepDispatch(...) + GetSendEndpoint + Send block is replaced by one dispatcher.DispatchAsync(..., Guid.Empty, Guid.Empty, ct) call (initial fire). The liveness-refresh + self-reschedule block is untouched. using MassTransit; stays (NewId at line 53); using Messaging.Contracts; stays (LivenessProjection).
- **StepAdvancement.SelectNext** (D-02 / SPEC req 3): pure match+traversal -- private const int Always = 4; predicate next.EntryCondition == (int)outcome || next.EntryCondition == Always; Never(5) falls out of the predicate (never selected); a dangling NextStepIds id is skipped via TryGetValue (no throw). No Redis/store reference -- step map passed as argument.
- **GateClosedException** (D-06): sealed Exception subclass for the gate-closed throw, mirroring WorkflowRootNotFoundException. CRITICAL: NOT added to any r.Ignore<>() list -- it must flow to redelivery.
- **StepAdvancementTests** (pure, no harness): table-driven Theory matrix -- SelectsExactlyMatchingConditionPlusAlways (x4), NeverConditionStep_IsNeverSelected_ForAnyOutcome (x4), NonMatchingOutcomeConditionSteps_AreExcluded (x4), DanglingNextStepId_IsSkipped_NoThrow, HelperPerformsNoIo_TakesStepMapAsArgument (14 facts total). The map is keyed by IdFor(condition) (11111111-...-1111111111{cond:D2}) and assertions are by stepId.

## How It Works

WorkflowFireJob (cron initial fire) and the future ResultConsumer (continuation) now share one dispatch implementation -- a single change to the queue convention or message shape lands in StepDispatcher only. The advancement match is a free function: given an outcome (0-3), the completed step, and the L1 step map, it yields the next steps to dispatch. Always(4) is an orchestrator-side constant (not on the StepOutcome enum, which is contracts-only 0-3); Never(5) is structurally unreachable because (int)outcome is 0-3 and Always is 4.

## Verification

- dotnet build src/Orchestrator/Orchestrator.csproj -c Debug -- Build succeeded, 0 Warning(s) / 0 Error(s).
- dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug -- Build succeeded, 0 Warning(s) / 0 Error(s) (after the FireDispatchTests reconcile, Rule 1 below).
- StepAdvancementTests isolated (dotnet run -- --filter-class "BaseApi.Tests.Orchestrator.StepAdvancementTests") -- Passed: 14, Failed: 0, Total: 14 (stable across repeated runs).
- Full Orchestrator namespace slice (dotnet run -- --filter-class "BaseApi.Tests.Orchestrator.*") -- Passed: 54, Failed: 0, Total: 54 (no regression after the WorkflowFireJob refactor; 14 StepAdvancement + 40 fire/lifecycle/contract tests).

Note on MTP filter syntax: the test project uses Microsoft.Testing.Platform; the VSTest-style `dotnet test --filter` is IGNORED (MTP0001 warning, runs the whole suite). The correct invocation is `dotnet run --project ... -- --filter-class <FQN>` (per project memory note "MTP filter is -- --filter-class").

## TDD Gate Compliance

Plan task 2 was tdd="true". Gate sequence held: RED commit 83b75d7 (test(24-03): ...) -- test failed to compile because StepAdvancement did not exist (a genuine RED, not a false-pass); GREEN commit ddd73b1 (feat(24-03): implement pure StepAdvancement...) -- the production match logic landed and is correct. A follow-up test commit 60f4d3b hardened the test fixture (see Deviation 2). The production SelectNext was correct throughout. Final: StepAdvancement 14/14. No REFACTOR of production code needed.

## Acceptance Criteria

Task 1:
- IStepDispatcher contains interface IStepDispatcher with DispatchAsync( taking executionId/entryId -- yes.
- StepDispatcher contains GetSendEndpoint(new Uri($"queue:{processorId:D}")) and .Send(msg, ct) -- yes.
- WorkflowFireJob contains dispatcher.DispatchAsync( and IStepDispatcher dispatcher; does NOT contain new EntryStepDispatch( -- verified (0 matches).
- GateClosedException contains class GateClosedException and : Exception( -- yes; NOT ignore-listed.
- Orchestrator build exits 0 -- yes.

Task 2:
- StepAdvancement contains private const int Always = 4 and next.EntryCondition == (int)outcome || next.EntryCondition == Always -- yes.
- StepAdvancement does NOT contain StepEntryCondition / IDatabase / IConnectionMultiplexer / IWorkflowL1Store -- yes (pure).
- Tests assert Never(5) never selected for any outcome -- yes (NeverConditionStep_IsNeverSelected_ForAnyOutcome Theory x4).
- Tests do NOT contain AddMassTransitTestHarness -- yes (pure test).
- StepAdvancement tests exit 0 -- yes (14/14).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Reconciled FireDispatchTests to the new WorkflowFireJob ctor**
- Found during: Task 2 verification (the Orchestrator regression slice did not compile).
- Issue: the existing tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs constructs WorkflowFireJob at 3 sites passing harness.Bus (an IBus) into the 2nd ctor slot, which was ISendEndpointProvider. Task 1 changed that slot to IStepDispatcher, so the test assembly failed to compile (CS1503: cannot convert IBus to IStepDispatcher).
- Fix: the 3 sites now pass new StepDispatcher(harness.Bus) (the real dispatcher wrapping the harness bus -- IBus satisfies ISendEndpointProvider), preserving the test's intent (harness captures the Send). Added using Orchestrator.Dispatch;.
- Files modified: tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs
- Commit: b494318
- Result: test assembly builds 0/0.

**2. [Rule 1 - Bug] StepAdvancementTests deterministic-id false positive**
- Found during: Task 2 verification (an isolated StepAdvancementTests run reported NeverIsNeverSelected_ForAnyOutcome(Processing) seeing a step with EntryCondition 5).
- Issue: the original test built its step map with an IdFor pattern whose cond=0 key collapsed toward Guid.Empty, and the by-condition projection assertions intermittently mis-attributed a selected step -- reporting Never(5) as selected for Processing. A throwaway by-stepId diagnostic confirmed the production SelectNext was ALWAYS correct (Processing selects exactly the EntryCondition-0 step + Always); the defect was purely in the test fixture's id pattern + assertion shape.
- Fix: rewrote the fixture to key the map by IdFor(condition) = 11111111-...-1111111111{cond:D2} (distinct, non-empty) and assert selection BY stepId (no EntryCondition projection). 14 facts, all GREEN.
- Files modified: tests/BaseApi.Tests/Orchestrator/StepAdvancementTests.cs
- Commit: 60f4d3b
- Result: StepAdvancementTests 14/14; Orchestrator slice 54/54.

The KNOWN GOTCHA from 24-02 (BOM / off test paths) did not apply to the new files: StepAdvancementTests.cs is plain ASCII at the verified path tests/BaseApi.Tests/Orchestrator/ with namespace BaseApi.Tests.Orchestrator.

## Out of Scope (pre-existing, NOT fixed)

A full-suite run (no namespace filter) reports 4 failures: RabbitMQ "Connection Failed: rabbitmq://rabbitmq/" + DbUpdateConcurrencyException + FluentValidation integration flakies. These are entirely OUTSIDE the Orchestrator namespace (the Orchestrator slice is 54/54 clean) and match the documented "false-alarm 4-failures" from Plan 24-02 (commit 8102b59: "clean re-run ... 0 fail"). Per the SCOPE BOUNDARY rule they are not in this plan's change surface and were not touched.

## Threat Model Outcome

- T-24-06 (Tampering / input validation over NextStepIds): mitigated -- a dangling NextStepIds id absent from the step map is SKIPPED via the TryGetValue guard (asserted by DanglingNextStepId_IsSkipped_NoThrow), never an exception; match is strict int equality.
- T-24-07 (Elevation via unintended auto-advance of a Never step): mitigated -- Never(5) falls out of the predicate ((int)outcome is 0-3, Always is 4) so a Never-gated step can never be auto-dispatched (asserted by NeverConditionStep_IsNeverSelected_ForAnyOutcome x4).

## Requirements Satisfied

- ORCH-ADVANCE-01 -- L1-only edge traversal + entry-condition match (StepAdvancement.SelectNext).
- ORCH-ADVANCE-02 -- continuation dispatch reusing EntryStepDispatch (IStepDispatcher single owner).

## Commits

- 24f6668 feat(24-03): extract IStepDispatcher; refactor WorkflowFireJob; add GateClosedException (Task 1)
- 83b75d7 test(24-03): add failing table-driven test for StepAdvancement.SelectNext (Task 2 RED)
- ddd73b1 feat(24-03): implement pure StepAdvancement.SelectNext match + traversal (Task 2 GREEN)
- b494318 fix(24-03): reconcile FireDispatchTests to IStepDispatcher ctor change (Rule 1)
- 60f4d3b test(24-03): fix StepAdvancementTests IdFor Guid pattern -- Never false-positive (Rule 1)

(Plus metadata-only docs commits for STATE / ROADMAP / SUMMARY.)

## Self-Check: PASSED

All 6 key files present on disk; all 5 task/fix commits (24f6668, 83b75d7, ddd73b1, b494318, 60f4d3b) present in git log.
