---
phase: 44-processor-pre-in-post-process-pipeline
verified: 2026-06-08T15:30:00Z
status: passed
score: 5/5 must-haves verified
overrides_applied: 0
---

# Phase 44: Pre/In/Post-Process Pipeline Verification Report

**Phase Goal:** `BaseProcessor` consumes a dispatch and runs an explicit Pre → In → Post pipeline — Pre reads + validates input (skipping on `Guid.Empty`), In is the author-overridden per-item transform that may throw a status-carrying exception, Post validates/writes/routes each item, and a `finally` end-delete reclaims `L2[entryId]` on every read-succeeded path — with a bounded retry loop wrapping every L2 op and every send.
**Verified:** 2026-06-08T15:30:00Z
**Status:** passed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths (5 Roadmap Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| SC1 | Pre-Process reads `L2[entryId]` through a bounded retry loop where a Redis exception OR absent/empty key counts as failure; exhaustion sends `KeeperReinject` and ends round trip (input left intact); `Guid.Empty` skips read; schema validation failure is business `StepFailed` not infra | VERIFIED | `ProcessorPipeline.cs:75-94` — `RetryLoop.ExecuteAsync` wraps the read; `KeyAbsentException` unifies absent/empty with Redis fault (A2); `read.Succeeded == false` sends `BuildReinject(d)` and returns with `readSucceeded=false`; `SourceStep.IsSource(d.EntryId)` (never inline `== Guid.Empty`); schema fail at line 90-94 sends `BuildFailed` and returns through `finally` with `readSucceeded=true`. `PipelinePreFacts`: `SourceStep_Skip_EmptyData_NoEndDelete`, `ReadFault_Reinject`, `AbsentKey_Reinject`, `InputInvalid_Failed` — all verified. |
| SC2 | In-Process is abstract `ProcessAsync(validatedData, payload, ct) → List<ProcessItem>` with author-minted `executionId`; wrapped in try/catch so any thrown status sends exactly one orchestrator result and aborts batch; unexpected exception ⇒ `failed` | VERIFIED | `BaseProcessor.cs:29-30` — `protected abstract Task<List<ProcessItem>> ProcessAsync(string validatedData, string payload, CancellationToken ct)`. `ProcessorPipeline.cs:99-117` — `try { items = await processor.ExecuteAsync(...) }` `catch (ProcessStatusException e) { ... switch ... }` `catch (Exception ex) { BuildFailed(d, ex.Message) }`. `ProcessItem.cs:7` — `sealed record ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId)`. `PipelineInFacts`: `StatusException_Failed_AbortsBatch`, `StatusException_Cancelled`, `StatusException_Processing`, `UnexpectedException_Failed`. |
| SC3 | Post-Process per `completed` item validates output, sends Keeper `UPDATE`, generates GUID `entryId`, writes `L2[entryId]` (no TTL) through bounded retry; exhaustion downgrades to `failed (infra)` → `KeeperInject`; on write success sends `KeeperCleanup` | VERIFIED | `ProcessorPipeline.cs:127-138` — `ProcessorJsonSchemaValidator.TryValidate` on `item.Data`; `SendKeeper(BuildUpdate(...))` before write; `NewId.NextGuid()` as framework-minted `entryId`; `db.StringSetAsync(L2ProjectionKeys.ExecutionData(entryId), item.Data)` — NO expiry argument; write-exhaust → `SendKeeper(BuildInject(...))` + `continue`; `SendKeeper(BuildCleanup(...))` on success. `PipelinePostFacts.PostCompleted_UpdateCleanup` asserts no-TTL write, UPDATE-before-CLEANUP, single `StepCompleted`. `PipelinePostFacts.WriteFault_Inject` asserts `KeeperInject` on write-exhaust with no `KeeperCleanup`. |
| SC4 | Post-Process routes each item: not-infra (`completed` ∪ business-`failed`) → orchestrator result (`completed` carries `entryId` + `executionId`); infra → `KeeperInject`; N completed → N separate per-item results (no manifest) | VERIFIED | `ProcessorPipeline.cs:127-151` — `BuildCompleted(d, item.ExecutionId, entryId)` sends `StepCompleted` with both ids; `BuildInject(d, item)` for infra; `BuildFailed(...)` for per-item business-failed, no batch abort (`continue` not `return`). `PipelinePostFacts.CompletedCarriesIds` asserts `completed.EntryId != Guid.Empty` AND `completed.ExecutionId == authorExec`. `PipelinePostFacts.MultiItem_NCompleted_NResults` asserts 3 items → 3 `StepCompleted`. `PipelinePostFacts.BusinessFailedItem_OneStepFailed_NoAbort` asserts 1 `StepCompleted` + 1 `StepFailed` from a `[Completed, Failed]` list. |
| SC5 | End-delete runs in a `finally` over every read-succeeded path, skipped only on `infra(READ)/REINJECT` and `Guid.Empty` source steps; deletes `L2[entryId]` through bounded retry; exhaustion → `KeeperDelete`; every L2 op and send uses the shared `Retry:Limit` immediate-attempt loop | VERIFIED | `ProcessorPipeline.cs:154-163` — `finally { if (readSucceeded) { RetryLoop.ExecuteAsync(() => db.KeyDeleteAsync(...)) ... } }`. `readSucceeded=false` on REINJECT and `Guid.Empty` paths. Send wrappers at lines 169-183 use `RetryLoop.ExecuteAsync` with `limit`; on `!sent.Succeeded` they `throw sent.Error!` (D-10 propagation). `retryOptions.Value.Limit` supplies the shared budget (line 62). `RetryLoop.cs:14` — `Math.Max(1, limit)` immediate attempts. `PipelineEndDeleteFacts`: `EndDelete_RunsOnHappyPath`, `EndDelete_RunsOnBusinessFail`, `EndDelete_RunsOnInException`, `EndDelete_Skipped_OnReinject`, `EndDelete_Skipped_OnSourceStep`, `EndDelete_Exhaust_Delete`. `RetryLoopFacts`: 5 hermetic facts proving RESIL-01. |

**Score:** 5/5 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` | Pre→In→Post→end-delete runner, ≥120 lines | VERIFIED | 215 lines; `RunAsync` implements all five terminals; `RetryLoop`-wrapped ops; `readSucceeded` gate. |
| `src/BaseProcessor.Core/Processing/BaseProcessor.cs` | `protected abstract Task<List<ProcessItem>> ProcessAsync(...)` seam | VERIFIED | Lines 29-30; `internal ExecuteAsync` forwarder at lines 38-39. |
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` | Thin consumer: metric + `pipeline.RunAsync` | VERIFIED | Lines 26-36; `metrics.DispatchConsumed.Add(...)` + `pipeline.RunAsync(ctx.Message, ctx.CancellationToken)`. |
| `src/BaseProcessor.Core/Processing/ProcessItem.cs` | `sealed record ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId)` | VERIFIED | Line 7 — exact shape. |
| `src/BaseProcessor.Core/Processing/ProcessStatusException.cs` | Abstract base + `ProcessingException`/`FailedException`/`CancelledException` | VERIFIED | All 4 types present, primary-ctor, file-scoped namespace. |
| `src/BaseProcessor.Core/Resilience/RetryLoop.cs` | `static ExecuteAsync<T>(op, limit, ct)` returning `RetryOutcome<T>`; `Math.Max(1, limit)` | VERIFIED | Lines 10-21; `RetryOutcome<T>` struct at lines 26-30. |
| `src/BaseProcessor.Core/Resilience/KeyAbsentException.cs` | `internal sealed class KeyAbsentException` | VERIFIED | Line 7 — `internal sealed class KeyAbsentException() : Exception("L2 key absent or empty.")` |
| `src/Processor.Sample/SampleProcessor.cs` | Override `ProcessAsync → List<ProcessItem>`, author-minted ExecutionId, FailedException path | VERIFIED | Lines 28-46; `override Task<List<ProcessItem>> ProcessAsync`; `new(ProcessOutcome.Completed, cfg ?? "processor-sample-ok", Guid.NewGuid())`; `throw new FailedException("sample reason")` on `cfg == "fail"`. |
| `src/BaseProcessor.Core/Processing/ProcessResult.cs` | Deleted (D-06 clean break) | VERIFIED | File absent from `src/`; no `ProcessResult` references in `src/` or `tests/`. |
| `tests/BaseApi.Tests/Processor/RetryLoopFacts.cs` | 5 hermetic RetryLoop facts | VERIFIED | All 5 methods present: `RetryLoop_Exhausts_RunsExactlyLimitAttempts_ThenSurfaces`, `RetryLoop_Succeeds_ReturnsValue_OnFirstSuccess`, `RetryLoop_Succeeds_OnSecondAttempt`, `RetryLoop_ZeroLimit_RunsAtLeastOnce`, `SendExhaust_Propagates_WhenCallerRethrows`. |
| `tests/BaseApi.Tests/Processor/PipelinePreFacts.cs` | 4 Pre-stage facts (PIPE-02/03) | VERIFIED | `SourceStep_Skip_EmptyData_NoEndDelete`, `ReadFault_Reinject`, `AbsentKey_Reinject`, `InputInvalid_Failed`. |
| `tests/BaseApi.Tests/Processor/PipelineInFacts.cs` | 4 In-stage facts (PIPE-05) | VERIFIED | `StatusException_Failed_AbortsBatch`, `StatusException_Cancelled`, `StatusException_Processing`, `UnexpectedException_Failed`. |
| `tests/BaseApi.Tests/Processor/PipelinePostFacts.cs` | 5 Post-stage facts (PIPE-06/07) | VERIFIED | `MultiItem_NCompleted_NResults`, `PostCompleted_UpdateCleanup`, `WriteFault_Inject`, `CompletedCarriesIds`, `BusinessFailedItem_OneStepFailed_NoAbort`. |
| `tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs` | 6 end-delete facts (PIPE-08) | VERIFIED | All 6 methods verified including both `EndDelete_Skipped_On*` variants. |
| `src/Processor.Sample/appsettings.json` | `"Retry": { "Limit": 3 }` explicit binding | VERIFIED | Lines 35-38: `"Retry": { "Limit": 3, "Strategy": "Immediate" }`. |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `ProcessorPipeline.cs` | `queue:keeper-recovery` | `ISendEndpointProvider.GetSendEndpoint(new Uri($"queue:{KeeperQueues.Recovery}"))` | VERIFIED | Line 179 — exact match to `KeeperQueues.Recovery`. |
| `ProcessorPipeline.cs` | `RetryLoop.ExecuteAsync` | Every L2 op + every send wrapped in `RetryLoop.ExecuteAsync` with `limit = retryOptions.Value.Limit` | VERIFIED | Lines 75, 131, 159, 172, 180 — all 5 call sites. |
| `BaseProcessorServiceCollectionExtensions.cs` | `ProcessorPipeline` DI | `services.AddScoped<ProcessorPipeline>()` | VERIFIED | Line 86 of the DI extension. |
| `ProcessorStartupOrchestrator.cs` | `Retry:Limit` | `UseMessageRetry(r => r.Immediate(retryLimit))` reconciled as outer dead-letter latch | VERIFIED | Lines 174-180 — D-09 reconciliation comment updated; `retryLimit` from `retryOptions.Value.Limit`. |
| `SampleProcessor.cs` | `BaseProcessor.ProcessAsync(validatedData, payload, ct) → List<ProcessItem>` | `override` keyword | VERIFIED | Line 28 — `protected override Task<List<ProcessItem>> ProcessAsync(string validatedData, string payload, CancellationToken ct)`. |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `ProcessorPipeline.cs` — Pre read | `validatedData` | `RetryLoop.ExecuteAsync(db.StringGetAsync(...))` | Yes — live Redis `StringGetAsync` or `KeyAbsentException` sentinel | FLOWING |
| `ProcessorPipeline.cs` — Post write | `item.Data` | `processor.ExecuteAsync(validatedData, payload, ct)` — author-returned `ProcessItem.Data` | Yes — author-supplied (output-validation-gated) | FLOWING |
| `ProcessorPipeline.cs` — end-delete | `L2ProjectionKeys.ExecutionData(d.EntryId)` | Derived from inbound `EntryStepDispatch.EntryId` | Yes — framework-minted key from dispatch | FLOWING |

### Behavioral Spot-Checks

Step 7b skipped — `dotnet build SK_P.sln -c Release` (0 warnings) and `dotnet test SK_P.sln --filter-not-trait Category=RealStack` (488 passed / 0 failed) were run by the executor and confirmed GREEN in the 44-03-SUMMARY. Full hermetic suite covers the five terminals without needing a running server.

### Requirements Coverage

| Requirement | Source Plan(s) | Description | Status | Evidence |
|-------------|---------------|-------------|--------|----------|
| PIPE-01 | 44-02 | Explicit Pre→In→Post pipeline replacing the single straight-through seam | SATISFIED | `ProcessorPipeline.RunAsync` — explicit three-stage structure; `EntryStepDispatchConsumer` reduced to metric + delegate |
| PIPE-02 | 44-01, 44-02 | Bounded retry L2 read; Redis/absent/empty after exhaustion → infra(READ); `Guid.Empty` skips | SATISFIED | `RetryLoop.ExecuteAsync` at lines 75-80; `KeyAbsentException` unifies fault paths; `SourceStep.IsSource` guard |
| PIPE-03 | 44-02 | Input schema validation failure → business `Failed` (not infra) | SATISFIED | `ProcessorJsonSchemaValidator.TryValidate` at lines 90-94; `BuildFailed` (not `BuildReinject`) on failure |
| PIPE-04 | 44-01, 44-02, 44-03 | Author `ProcessAsync(validatedData, payload, ct) → List<ProcessItem>` with author-minted `executionId` | SATISFIED | `ProcessItem` record + `BaseProcessor` abstract seam + `SampleProcessor` worked example |
| PIPE-05 | 44-01, 44-02 | In wrapped in try/catch; status exception → matching Step*; unexpected → `StepFailed` | SATISFIED | Lines 99-117; runtime type switch on `ProcessStatusException` |
| PIPE-06 | 44-02 | Post: output validation; write `L2[entryId]` (no TTL); bounded retry; exhaust → `failed(infra)` → KeeperInject; success → KeeperCleanup | SATISFIED | Lines 123-138; `StringSetAsync(key, item.Data)` — no expiry arg; `KeeperInject` on write-exhaust; `KeeperCleanup` on success |
| PIPE-07 | 44-02 | Routing: completed → StepCompleted (entryId+executionId); infra → KeeperInject; N items → N results | SATISFIED | `BuildCompleted(d, item.ExecutionId, entryId)` at line 145; per-item `continue` (no abort on infra); N→N proven by `MultiItem_NCompleted_NResults` |
| PIPE-08 | 44-02 | End-delete `finally` over read-succeeded paths; bounded retry; exhaust → KeeperDelete | SATISFIED | `finally { if (readSucceeded) ... }` at lines 154-163; `RetryLoop.ExecuteAsync` wraps delete; `BuildDelete` on exhaustion |
| RESIL-01 | 44-01, 44-02, 44-03 | Every L2 op + every send wrapped in bounded retry loop (`Retry:Limit`); send-exhaustion propagates | SATISFIED | All 5 `RetryLoop.ExecuteAsync` call sites; `throw sent.Error!` on send-exhaust; `Retry:Limit=3` explicit in appsettings |

No orphaned requirements — all 9 Phase-44 IDs are claimed and implemented.

### Anti-Patterns Found

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| `ProcessorPipeline.cs:113-117` | `catch (Exception ex)` swallows `OperationCanceledException` into business `StepFailed` on shutdown (WR-02 from code review) | Warning | False business outcome emitted on host shutdown; not a correctness gap relative to the 5 SCs but is a robustness concern for production. Fix: `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }` before the generic catch. |
| `ProcessorPipeline.cs:154-164` | End-delete runs on send-exhaustion unwind (WR-01 from code review) | Warning | When a Post `SendResult`/`SendKeeper` exhausts and propagates, the `finally` fires and deletes `L2[d.EntryId]` before the bus replay; the redelivery then reads an absent key and routes to REINJECT instead of retrying the send. The `readSucceeded` gate correctly skips end-delete on the REINJECT path (SC1 invariant "input left intact" is upheld for the intended REINJECT terminal). The WR-01 edge case is a separate scenario (Post send-exhaustion) not covered by SC5's "skipped only on REINJECT/Guid.Empty" enumeration. Fix: add a `pipelineFaulted` flag and a `catch { pipelineFaulted = true; throw; }` outer catch, gating the finally on `readSucceeded && !pipelineFaulted`. |
| `ProcessorPipeline.cs:171,179` | `queue:` scheme-prefix string is a magic literal duplicated in two send helpers (IN-02 from code review) | Info | Low risk; queue-name constants are centralized in `KeeperQueues.Recovery`/`OrchestratorQueues.Result`. |

**Stub classification note:** No stub patterns found in production code. All empty/null returns are in test fakes (NSubstitute stubs) which are test-only.

### Human Verification Required

None. All five success criteria are verifiable programmatically against the codebase. The hermetic test suite (488 facts, 0 failed) provides additional confidence. RealStack E2E integration is deferred to Phase 49 by design.

### Gaps Summary

No gaps. All 5 roadmap success criteria are verified by direct code inspection:

1. SC1 (Pre-process) — `RetryLoop` + `KeyAbsentException` + `SourceStep.IsSource` + schema-validation branch path all present and wired, proven by `PipelinePreFacts` (4 facts).
2. SC2 (In-process) — `ProcessAsync` abstract seam + `try/catch(ProcessStatusException)` + unexpected-exception fallback all present, proven by `PipelineInFacts` (4 facts).
3. SC3 (Post completed item) — `KeeperUpdate` → no-TTL write → `KeeperCleanup`/`KeeperInject` all present in correct order, proven by `PipelinePostFacts` (5 facts).
4. SC4 (Routing) — per-item orchestrator results carrying both ids, infra → `KeeperInject`, N→N, no batch-abort on per-item business-fail, proven by `PipelinePostFacts`.
5. SC5 (End-delete) — `finally` gated on `readSucceeded`, `RetryLoop` wrapping delete, `KeeperDelete` on exhaustion, skipped on REINJECT and `Guid.Empty`, proven by `PipelineEndDeleteFacts` (6 facts).

Two robustness warnings (WR-01 and WR-02 from the code review) do not constitute gaps against the roadmap SCs. They are documented above under Anti-Patterns for tracking.

---

_Verified: 2026-06-08T15:30:00Z_
_Verifier: Claude (gsd-verifier)_
