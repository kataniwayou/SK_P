---
phase: 51-processor-forward-recovery-pipeline
plan: 02
subsystem: BaseProcessor.Core pipeline
tags: [pipeline, slot-array, forward-pass, infra-taxonomy, keeper-inject, wr-01, messageId, dispatcher]
requires:
  - "SlotArrayOptions + DI bind (51-01) — IOptions<SlotArrayOptions> ctor dependency"
  - "MessageIndex slot-array key builder + KeeperInject A18 id-set (Phase 50)"
  - "ProcessorLivenessOptions.ExecutionDataTtlSeconds (data-key TTL)"
provides:
  - "ProcessorPipeline.RunAsync(EntryStepDispatch, Guid messageId, CancellationToken) — A18 thin dispatcher branching on exist L2[messageId]"
  - "RunForwardAsync — allocation-before-data Post (HASH slot before data key), split infra (infra_messageId drop / infra_entryId INJECT), per-item dispatch, explicit inline source-delete tail (WR-01 finally RETIRED)"
  - "BuildInject populating EntryId/Data/DeleteEntryId (Phase-50 KeeperInject contract)"
  - "EntryStepDispatchConsumer messageId seam with null fail-fast (InvalidOperationException)"
  - "RunRecoveryAsync stub (throws NotImplementedException) — body lands in plan 51-03"
affects:
  - "Plan 51-03 (recovery pass replaces the RunRecoveryAsync stub body)"
  - "Phase 52 (3-state keeper consumes the INJECT/REINJECT/DELETE this forward pass emits)"
  - "Phase 53 (Model-B teardown — the deferred UseMessageRetry=none end-state lands here)"
tech-stack:
  added: []
  patterns:
    - "Thin dispatcher: RetryLoop exist-check on L2[messageId] → REINJECT-on-exhaust / forward-vs-recovery branch"
    - "Allocation-before-data: HashSetAsync(MessageIndex) + random-TTL KeyExpire BEFORE StringSetAsync(ExecutionData) — worst case is a skippable dangling pointer, never an unreferenced-data leak"
    - "Per-site infra taxonomy: alloc-write exhaust → infra_messageId drop (no send); data-write exhaust → infra_entryId keeper INJECT"
    - "Explicit inline source-delete tail reached only on the no-REINJECT happy path (replaces the WR-01 finally)"
key-files:
  created:
    - "tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs"
  modified:
    - "src/BaseProcessor.Core/Processing/ProcessorPipeline.cs"
    - "src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs"
    - "tests/BaseApi.Tests/Processor/DispatchTestKit.cs"
    - "tests/BaseApi.Tests/Processor/PipelinePreFacts.cs"
    - "tests/BaseApi.Tests/Processor/PipelineInFacts.cs"
    - "tests/BaseApi.Tests/Processor/PipelinePostFacts.cs"
    - "tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs"
decisions:
  - "UseMessageRetry/keep-latch (RESEARCH Open Q1): KEEP the Phase-44 outer dead-letter latch (cfg.UseMessageRetry(r => r.Immediate(retryLimit)), ProcessorStartupOrchestrator.cs) in Phase 51 — NO change made — so send-exhaust PROPAGATE (D-10) still reaches skp-dlq-1; A18's UseMessageRetry=none / _error-disabled end-state is DEFERRED to Phase 53 teardown"
  - "WR-01 finally RETIRED (D-08): removed entirely (not refactored in place); source-delete is an explicit inline tail reached only on the no-REINJECT happy path; the Pre-read-exhaust REINJECT returns WITHOUT deleting (input intact, FWD-01)"
  - "Slot ordinal increments ONLY for completed items (business-failed + infra_messageId-dropped consume no slot) — Claude's-Discretion per CONTEXT"
metrics:
  duration: "~prior-executor tasks 1-3 + continuation Task 4"
  completed: "2026-06-11"
  tasks: 4
  files: 8
---

# Phase 51 Plan 02: Pipeline Dispatcher + FORWARD Pass Summary

Rewrote `ProcessorPipeline` into the A18 thin dispatcher + FORWARD pass — allocation-before-data Post (HASH slot index written before the data key), per-site split-infra taxonomy (`infra_messageId`→drop / `infra_entryId`→keeper `INJECT`), per-item dispatch, and an explicit inline source-delete tail that RETIRES the WR-01 `finally` (D-08). Fixed the stale `BuildInject` to carry the full Phase-50 `KeeperInject` id-set, plumbed the broker `messageId` through the consumer seam with a null fail-fast (D-09/D-10), and proved every forward branch hermetically. Resolved RESEARCH Open Q1 (`UseMessageRetry`) as **keep-latch** — the Phase-44 dead-letter latch stays; the A18 `none` end-state defers to Phase 53.

## What Was Built

### Task 1 — DispatchTestKit slot-array HASH fakes + adapt the four existing facts (`5412ca6`)
- Added `SlotOptions(min=300, max=600)`, a FORWARD-happy `ForwardOkL2` HASH mux (existence-check FALSE → forward branch; HSET / KeyExpire / data SET / source-delete all succeed), and the slot-write-fault / data-write-fault / source-delete-fault-forward muxes using the overload-robust When/Do style.
- Adapted `PipelinePreFacts`, `PipelineInFacts`, `PipelinePostFacts`, `PipelineEndDeleteFacts` to the new ctor (`IOptions<SlotArrayOptions>` via `DispatchTestKit.SlotOptions()`) and the new three-arg `RunAsync(d, messageId, ct)` signature, routing through `KeyExistsAsync=false` so they still exercise the forward path. `PipelineEndDeleteFacts` rephrased from "finally runs on read-succeeded" to "forward tail runs on the no-REINJECT happy path" (still asserts FWD-03).

### Task 2 — Rewrite ProcessorPipeline + consumer seam (`c4df040`)
- **Thin dispatcher (D-07/FWD-01):** removed the outer `try/finally` and the `readSucceeded` gate; `RunAsync` now does a `RetryLoop` exist-check on `KeyExistsAsync(L2ProjectionKeys.MessageIndex(messageId))` — exhaust → `SendKeeper(BuildReinject)` + return (no source delete); `true` → `RunRecoveryAsync` (stub, plan 03); `false` → `RunForwardAsync`.
- **RunForwardAsync allocation-before-data (SLOT-01/02):** per completed item, allocate `entryId`, write `HashSetAsync(MessageIndex(messageId), slot, entryId)` + random-TTL `KeyExpireAsync` (the `SlotTtl()` pick from `SlotArrayOptions`) FIRST, then `StringSetAsync(ExecutionData(entryId))`. Alloc-write exhaust → `infra_messageId` DROP (no data write, no send, slot NOT consumed); data-write exhaust → `infra_entryId` keeper `INJECT` (slot consumed). Completed item → `SendResult(BuildCompleted)`.
- **Explicit inline source-delete tail (FWD-03, WR-01 RETIRED):** `DeleteSourceTail` local helper (`KeyDeleteAsync(ExecutionData(d.EntryId))`, exhaust → `SendKeeper(BuildDelete)`), guarded by `!SourceStep.IsSource(d.EntryId)`, invoked on every non-REINJECT forward exit. NO `finally` anywhere; the Pre-read-exhaust REINJECT returns WITHOUT it (input intact).
- **BuildInject fix (Pitfall 1 / INFRA-02):** now `new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId, ExecutionId, EntryId = entryId, Data = item.Data, DeleteEntryId = d.EntryId }`.
- **Consumer seam (D-09/D-10):** `EntryStepDispatchConsumer` reads `ctx.MessageId ?? throw new InvalidOperationException(...)` then `pipeline.RunAsync(ctx.Message, messageId, ctx.CancellationToken)`; class doc-comment updated to describe the forward/recovery split + inline tails (PIPE-08 finally paragraph removed).

### Task 3 — PipelineForwardFacts (`ec8567e`)
- One `[Fact]` per behavior: FWD-01 (exist-fault → `Single(SentKeeper.OfType<KeeperReinject>())` + `DidNotReceive().KeyDeleteAsync`), SLOT-01/02 (`Received.InOrder` HashSet→StringSet), INFRA-01 (slot-fault → empty SentKeeper + no data write), INFRA-02 (data-fault → `KeeperInject` with `Data!="" && DeleteEntryId==d.EntryId && EntryId!=Guid.Empty`), FWD-02 (mixed completed+failed each on the right channel), FWD-03 (happy → `KeyDeleteAsync(ExecutionData(d.EntryId))`; fault → `Single(OfType<KeeperDelete>())`).

### Task 4 — UseMessageRetry decision (this continuation)
- See **Decision (RESEARCH Open Q1)** below. NO code change.

## Decision (RESEARCH Open Q1): UseMessageRetry — keep-latch

**Resolved: keep-latch** (the researcher recommendation / `[ASSUMED]` default; user-confirmed).

- A18 (design doc line 142) specifies `_error` routing disabled / `UseMessageRetry = none` for the v5 path. The processor currently binds `cfg.UseMessageRetry(r => r.Immediate(retryLimit))` as the OUTER dead-letter latch (`ProcessorStartupOrchestrator.cs`); the send-exhaust PROPAGATE (D-10) RELIES on this latch to reach the dead-letter sink `skp-dlq-1`.
- **Outcome:** NO change to `ProcessorStartupOrchestrator.cs` in this phase. The latch STAYS so send-exhaust still propagates to `skp-dlq-1`.
- **Deferred to Phase 53 (Model-B teardown):** the A18 `UseMessageRetry = none` / `_error`-disabled end-state. **Rationale:** removing the latch now would orphan exhausted sends with no dead-letter target before the Phase-52 keeper consumer (which rides the `Fault<T>` stream) and the Phase-53 Model-B teardown land. The `none` end-state lands cleanly in Phase 53 alongside the rest of the Model-B removal + `GenerateFaultFilter` rewiring.
- Tracked in the plan threat register as **T-51-07** (`accept (deferred)`).

## Verification

- `dotnet build SK_P.sln -c Release --nologo` and `-c Debug` — both 0-warning (confirmed through Task 3).
- `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Pipeline"` — green: Pre 4, In 4, Post 5, EndDelete 6, Forward 7 = **26/26**.
- Grep gate: `ProcessorPipeline.cs` contains no `finally` and no `readSucceeded` (confirmed); dispatcher branches on `KeyExistsAsync(L2ProjectionKeys.MessageIndex(messageId))`; `RunForwardAsync` + `RunRecoveryAsync` present; `BuildInject` carries `EntryId`/`Data = item.Data`/`DeleteEntryId = d.EntryId`; consumer contains `ctx.MessageId ?? throw new InvalidOperationException` and `RunAsync(ctx.Message, messageId, ctx.CancellationToken)`.

## Deviations from Plan

None. Tasks 1-3 executed as written by the prior executor (per the completed-tasks record); Task 4 resolved per the user's keep-latch decision with no code change.

## Deferred Issues

- **A18 `UseMessageRetry = none` / `_error`-disabled end-state** — deferred to Phase 53 teardown (decision above; threat T-51-07 accept/deferred). Not an in-scope failure.
- **`RunRecoveryAsync` stub** — see Known Stubs.

## Known Stubs

- **`RunRecoveryAsync` (ProcessorPipeline.cs)** — throws `NotImplementedException("recovery pass — plan 51-03")`. INTENTIONAL and by design: this plan is the FORWARD half of A18 (Wave 1); the recovery half lands in plan 51-03 (Wave 2, same file). The forward facts never reach this branch (`KeyExists=false` in every forward mux), so the stub cannot affect any forward behavior. Resolved by 51-03.

## Self-Check: PASSED

- `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs` — FOUND (created in `ec8567e`)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` no `finally`/`readSucceeded`; dispatcher + `RunForwardAsync` + `BuildInject` id-set — FOUND
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` messageId seam + null fail-fast — FOUND
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — UNCHANGED (`git diff` clean; keep-latch)
- Commit `5412ca6` (test, Task 1) — FOUND
- Commit `c4df040` (feat, Task 2) — FOUND
- Commit `ec8567e` (test, Task 3) — FOUND
