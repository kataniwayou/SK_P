---
phase: 44-processor-pre-in-post-process-pipeline
plan: 02
subsystem: processor-pipeline
tags: [csharp, dotnet8, masstransit, redis, retry, processor-pipeline, keeper-recovery, xunit-v3]

# Dependency graph
requires:
  - phase: 44-processor-pre-in-post-process-pipeline (plan 01)
    provides: ProcessOutcome/ProcessItem, ProcessStatusException family, RetryLoop + RetryOutcome<T>, KeyAbsentException sentinel
  - phase: 43-message-contracts-l2-key-reshape
    provides: reshaped Step* records, the five Keeper-state contracts + IKeeperRecoverable, L2ProjectionKeys GUID data key, SourceStep.IsSource
provides:
  - ProcessorPipeline (Pre→In→Post→end-delete runner with terminal routing) — the load-bearing Phase-44 behavior change
  - Retyped BaseProcessor seam — ProcessAsync(validatedData, payload, ct) → List<ProcessItem>
  - Thin EntryStepDispatchConsumer (metric + pipeline.RunAsync)
  - Four pipeline Wave-0 fact files (PipelinePre/In/Post/EndDeleteFacts) proving the five terminals hermetically
  - DispatchTestKit Keeper-capture + Redis-fake extensions
affects: [44-03-old-seam-deletion, keeper-recovery (Phases 46-48)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Pipeline-as-plain-object (RESEARCH Pattern 1): ProcessorPipeline takes the consumer collaborators + IOptions<RetryOptions> and is constructed directly by hermetic facts — no MassTransit harness needed for the five terminals"
    - "Surface-not-throw terminal routing: each RetryLoop op routes its exhaustion to the correct Keeper message (read→REINJECT, write→INJECT, delete→DELETE) while a send-exhaustion re-throws (→ bus _error, D-10)"
    - "readSucceeded-gated finally end-delete: the end-delete is a finally over every read-succeeded path, skipped on REINJECT + Guid.Empty source (T-44-08 data-loss mitigation)"
    - "A1 id provenance: REINJECT/DELETE carry the inbound d.ExecutionId; UPDATE/INJECT/CLEANUP carry the author-minted item.ExecutionId"
    - "No-TTL Post write: db.StringSetAsync(key, data) with NO expiry arg (design §16/64); asserted overload-agnostically by inspecting received-call arguments"

key-files:
  created:
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - tests/BaseApi.Tests/Processor/PipelinePreFacts.cs
    - tests/BaseApi.Tests/Processor/PipelineInFacts.cs
    - tests/BaseApi.Tests/Processor/PipelinePostFacts.cs
    - tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs
  modified:
    - src/BaseProcessor.Core/Processing/BaseProcessor.cs
    - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
    - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
    - src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
    - src/Processor.Sample/SampleProcessor.cs
    - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
    - tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs
    - tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs
  deleted:
    - tests/BaseApi.Tests/Processor/DispatchInputFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchResultSendFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchAckSemanticsFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchInvokeFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchCorrelationFacts.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs

key-decisions:
  - "Task 1 bridged the OLD consumer (read ProcessItem.Data) so BaseProcessor.Core stays 0-warning between Task 1 and Task 2; Task 2 fully replaced it with the thin delegate"
  - "Retired SEVEN superseded straight-through fact files (not the 4 the plan literally named) — the 3 scope/correlation files also constructed the OLD 7-arg consumer ctor + asserted old straight-through behavior, so they could not survive the Task-2 rewrite (VALIDATION.md Wave-0 sanctions retiring superseded straight-through facts)"
  - "Migrated SampleProcessor + its two hermetic facts (SampleProcessorFacts, BaseProcessorSeamFacts) to the new seam — a Rule-3 blocking necessity because BaseApi.Tests references Processor.Sample and could not compile for Task 3 otherwise; ProcessResult.cs delete stays Plan 03"
  - "No-TTL Post write asserted by inspecting ReceivedCalls() arguments overload-agnostically (the 2-arg StringSetAsync binds to the SE.Redis 2.13 Expiration overload defaulted to no-expiry, NOT a TimeSpan? overload)"

requirements-completed: [PIPE-01, PIPE-02, PIPE-03, PIPE-04, PIPE-05, PIPE-06, PIPE-07, PIPE-08, RESIL-01]

# Metrics
duration: 30min
completed: 2026-06-08
---

# Phase 44 Plan 02: Pre→In→Post Pipeline + Thin Consumer + Retry Reconcile Summary

**The load-bearing Phase-44 behavior change: a new `ProcessorPipeline` runner implements the explicit Pre→In→Post→`finally`-end-delete flow with the five terminals (REINJECT / UPDATE→write→CLEANUP / INJECT / DELETE + business StepFailed/StepCancelled/StepProcessing) proven hermetically, the `BaseProcessor` seam retyped to `ProcessAsync(validatedData, payload, ct) → List<ProcessItem>`, every L2 op and send wrapped in the Plan-01 `RetryLoop` (Retry:Limit), and the bus `UseMessageRetry` reconciled to the outer dead-letter latch — full hermetic suite 487 passed / 0 failed, `BaseProcessor.Core` Release 0-warning.**

## Performance
- **Duration:** ~30 min
- **Started:** 2026-06-08T14:14:43Z
- **Completed:** 2026-06-08T14:44:31Z
- **Tasks:** 3
- **Files:** 5 created, 8 modified, 7 deleted

## Accomplishments
- **`ProcessorPipeline.RunAsync`** implements the design §Processor-round-trip flow exactly: Pre (source-skip via `SourceStep.IsSource`; bounded-retry L2 read with A2 absent/empty-unified-with-Redis-fault via `KeyAbsentException`; read-exhaust → `KeeperReinject` carrying `d.ExecutionId`+`d.EntryId`, no end-delete armed; input-schema fail → business `StepFailed`, end-delete still runs), In (try/catch mapping `FailedException`/`CancelledException`/`ProcessingException` to the matching `Step*` record, one result, batch aborts; unexpected ⇒ `StepFailed`), Post (per item: `KeeperUpdate` → no-TTL write → `KeeperCleanup`/`KeeperInject` → `StepCompleted` carrying framework entryId + author executionId; per-item business-failed → one `StepFailed`, no abort; N completed → N results), and the `readSucceeded`-gated `finally` end-delete (bounded retry; exhaust → `KeeperDelete`).
- **A1 id-sets encoded:** REINJECT/DELETE carry the inbound `d.ExecutionId`; UPDATE/INJECT/CLEANUP carry the author-minted `item.ExecutionId`.
- **RESIL-01:** every L2 op + every send wrapped in `RetryLoop.ExecuteAsync` using `Retry:Limit`; send-exhaustion propagates (→ `_error`). The startup `UseMessageRetry(Immediate(retryLimit))` comment was reconciled (D-09) to state it is the OUTER dead-letter latch, not a second L2/send retry (Pitfall 1).
- **`BaseProcessor` seam retyped** to the In-Process `ProcessAsync(validatedData, payload, ct) → List<ProcessItem>` (D-01/D-02/D-03); the consumer reduced to the entry metric + `pipeline.RunAsync` delegate; `ProcessorPipeline` registered `AddScoped` in the composition root.
- **Four pipeline Wave-0 fact files** authored (19 facts) covering PIPE-02..08 + the A1/A2/A3 behavior changes; every VALIDATION.md `--filter-method` glob resolves to ≥1 GREEN test. Full hermetic suite 487 passed / 0 failed; `SK_P.sln` Release 0-warning.

## Task Commits
1. **Task 1: Retype seam + extend DispatchTestKit + retire superseded facts** — `c826506` (feat)
2. **Task 2: ProcessorPipeline + thin consumer + DI + Retry reconcile (+ Sample migration)** — `b5f9c2a` (feat)
3. **Task 3: Four pipeline Wave-0 fact files** — `8a81581` (test)

## Files Created/Modified
- `ProcessorPipeline.cs` (created) — the Pre→In→Post→end-delete runner with terminal routing, A1 id-sets, no-TTL write, finally-gated end-delete, RetryLoop-wrapped ops + sends.
- `BaseProcessor.cs` — abstract `ProcessAsync` + internal `ExecuteAsync` forwarder retyped to `List<ProcessItem>`.
- `EntryStepDispatchConsumer.cs` — reduced to metric + `pipeline.RunAsync`.
- `BaseProcessorServiceCollectionExtensions.cs` — `AddScoped<ProcessorPipeline>()`.
- `ProcessorStartupOrchestrator.cs` — D-09 reconcile comment on `UseMessageRetry`.
- `SampleProcessor.cs` + `SampleProcessorFacts.cs` + `BaseProcessorSeamFacts.cs` — migrated to the new seam (Rule-3, see deviations).
- `DispatchTestKit.cs` — `FakeProcessor(List<ProcessItem>)`/throw ctors, `Items(...)`, `CapturingSendProvider.SentKeeper`, `Retry(limit)`, and `PresentReadWriteFaultL2`/`PresentReadWriteDeleteOkL2`/`AbsentReadL2`/`ReadOkDeleteFaultL2`/`ReadFaultL2` fakes.
- `PipelinePre/In/Post/EndDeleteFacts.cs` (created) — 19 hermetic facts.
- **Deleted (retired):** `DispatchInputFacts`, `DispatchResultSendFacts`, `DispatchAckSemanticsFacts`, `DispatchInvokeFacts`, `DispatchCorrelationFacts`, `EntryStepDispatchScopeTests`, `EntryStepDispatchRuntimeScopeTests`.

## Decisions Made
- Bridged the old consumer in Task 1 (read `ProcessItem.Data`) to keep `BaseProcessor.Core` 0-warning between tasks; Task 2 replaced it wholesale.
- Retired SEVEN straight-through fact files (3 beyond the 4 the plan named) because all 7 either used the removed `Results` helper, the OLD 7-arg consumer ctor, or asserted superseded straight-through behavior. Their behavioral coverage is replaced by the Pipeline*Facts (scope behavior is now exercised via the pipeline's `BeginScope`; correlation via the builders).
- No-TTL Post write asserted overload-agnostically (the bound SE.Redis 2.13 `StringSetAsync` overload exposes an `Expiration` parameter, not `TimeSpan?`; the assertion checks whichever expiry parameter the bound overload exposes is the no-expiry default).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Migrated `SampleProcessor` + two hermetic facts to the new seam so `BaseApi.Tests` compiles**
- **Found during:** Task 2 (test-project build for Task 3)
- **Issue:** The plan's scope guard states `Processor.Sample` migration is Plan 03 and `Processor.Sample` "does NOT compile yet". But `BaseApi.Tests` (this plan's stated test target for Task 3) has a `ProjectReference` to `Processor.Sample`, AND `SampleProcessorFacts.cs` / `BaseProcessorSeamFacts.cs` override/return the OLD seam shape. After the Task-1 seam retype, the test project could not compile, blocking Task 3's `dotnet test`.
- **Fix:** Migrated `SampleProcessor.cs` to the new `ProcessAsync(validatedData, payload, ct) → List<ProcessItem>` seam (per `44-PATTERNS.md §SampleProcessor`, incl. the demonstrated `FailedException` status path) and updated `SampleProcessorFacts.cs` + `BaseProcessorSeamFacts.cs` to the new return type/assertions. `ProcessResult.cs` itself is left intact for Plan 03's delete.
- **Files modified:** `src/Processor.Sample/SampleProcessor.cs`, `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs`, `tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs`
- **Impact:** A POSITIVE deviation — `SK_P.sln` now builds Release 0-warning a plan earlier than the scope guard anticipated. No architectural change; `ProcessResult.cs` deletion remains Plan 03.
- **Commit:** `b5f9c2a`

**2. [Rule 3 - Blocking] Retired 3 additional superseded straight-through fact files beyond the 4 named**
- **Found during:** Task 1
- **Issue:** `EntryStepDispatchScopeTests`, `EntryStepDispatchRuntimeScopeTests`, and `DispatchCorrelationFacts` construct the OLD 7-arg `EntryStepDispatchConsumer` ctor / use the removed `Results` helper and assert old straight-through behavior — they could not compile after the Task-2 consumer rewrite.
- **Fix:** Deleted all 7 superseded straight-through fact files (the 4 named + these 3). VALIDATION.md § Wave-0 sanctions retiring superseded straight-through facts; their behavioral coverage is replaced by the Pipeline*Facts.
- **Commit:** `c826506`

**3. [Rule 1 - Bug] No-TTL assertion targeted the wrong `StringSetAsync` overload**
- **Found during:** Task 3
- **Issue:** The plan's suggested `db.Received().StringSetAsync(key, value)` 2-arg / 6-arg-`TimeSpan?` assertion did not match — the SE.Redis 2.13 2-arg call binds to the `Expiration`-parameter overload, not a `TimeSpan?` one.
- **Fix:** Rewrote the no-TTL assertion to inspect `db.ReceivedCalls()` and assert whichever expiry parameter the bound overload exposes (`TimeSpan?` OR `Expiration`) is the no-expiry default — overload-agnostic and robust.
- **Files modified:** `tests/BaseApi.Tests/Processor/PipelinePostFacts.cs`
- **Commit:** `8a81581`

**Total deviations:** 3 auto-fixed (2 blocking-build, 1 test-assertion bug). All Rules 1-3. No architectural changes, no auth gates, no Rule-4 escalations.

**Tooling note (not a content deviation):** this xunit.v3/MTP project requires `--filter-*` flags AFTER a `--` separator; `--report-trx` is not available in this MTP build (per-test failure detail lands in the UTF-16 `TestResults/*.log`).

## Issues Encountered
None beyond the deviations above.

## TDD Gate Compliance
All three tasks are marked `tdd="true"`. This plan is interface-first composition: the pipeline-coupled facts (Task 3) were authored AFTER the type they exercise (`ProcessorPipeline`, Task 2) per the VALIDATION.md interface-first plan, so the RED→GREEN ordering is plan-level (types then facts) rather than per-fact. The facts went GREEN on authoring (after the one no-TTL assertion fix); there is no spurious-pass concern because they exercise behavior (terminal routing) that did not exist before Task 2.

## Threat Surface
No new endpoints, secrets, or auth surfaces. The threat-register mitigations are upheld: T-44-05 (Post output-validation gates the write), T-44-06 (in-code RetryLoop owns bounded per-op retries; bus retry reconciled to the outer latch), T-44-08 (end-delete `readSucceeded`-gated, skipped on REINJECT/Guid.Empty — proven by `EndDelete_Skipped_OnReinject`/`_OnSourceStep`). No threat flags raised.

## Known Stubs
None. The pipeline wires real L2 ops + real Keeper/Step* sends; the test fakes are test-only doubles.

## Next Phase Readiness
- The five terminals are proven hermetically; `ProcessorPipeline` is the production runner the thin consumer delegates to.
- Plan 03 deletes `ProcessResult.cs` (now unreferenced by `BaseProcessor.Core`; still present) and owns any residual full-solution hygiene — though `SK_P.sln` already builds Release 0-warning after this plan's Sample migration.
- No blockers.

## Self-Check: PASSED

All 5 created files + 4 modified source files exist on disk; all 3 task commits (`c826506`, `b5f9c2a`, `8a81581`) exist in git history; the 7 deleted fact files are gone. Full hermetic suite 487 passed / 0 failed; `BaseProcessor.Core` Release 0-warning.

---
*Phase: 44-processor-pre-in-post-process-pipeline*
*Completed: 2026-06-08*
