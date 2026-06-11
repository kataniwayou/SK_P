---
phase: 51-processor-forward-recovery-pipeline
plan: 03
subsystem: BaseProcessor.Core pipeline (recovery pass)
tags: [pipeline, slot-array, recovery-pass, send-before-retire, reinject-xor-delete, fresh-exec, messageId, hgetall]
requires:
  - "ProcessorPipeline A18 dispatcher + RunRecoveryAsync stub (51-02) — the dispatcher already calls RunRecoveryAsync(d, messageId, db, limit, ct) on exist L2[messageId]"
  - "SlotTtl() helper + Build* builders + SendResult/SendKeeper owners (51-02) — reused unchanged"
  - "MessageIndex(messageId) HASH key builder + ExecutionData(entryId) (L2ProjectionKeys, Phase 43/50)"
  - "DispatchTestKit forward HASH-fake shape + CapturingSendProvider (51-02) — cloned for recovery"
provides:
  - "ProcessorPipeline.RunRecoveryAsync — the A18 RECOVERY pass body (HGETALL temp-list, send-before-retire, REINJECT-xor-source-delete) replacing the 51-02 NotImplementedException stub"
  - "DispatchTestKit recovery muxes: RecoveryL2 (per-entry exist matrix + fault entries), RecoveryHGetAllFaultL2, RecoveryAllCompletedL2, Slots() HashEntry[] builder, ResultSendFailProvider"
  - "PipelineRecoveryFacts (5 facts) — RECOV-01/02/03, SLOT-03 send-before-retire, D-03 fresh-exec mint"
  - "EntryStepDispatchConsumerFacts — D-10 null-MessageId fail-fast"
affects:
  - "Phase 52 (3-state keeper consumes the REINJECT/DELETE this recovery pass emits)"
  - "Phase 53 (Model-B teardown — UseMessageRetry=none end-state)"
tech-stack:
  added: []
  patterns:
    - "Recovery temp-list: a local tuple list (RedisValue Slot, Guid EntryId, bool Completed, bool Infra) per slot — Claude's-Discretion representation per CONTEXT"
    - "Send-before-retire (SLOT-03/T-51-08): SendResult (which throws on send-exhaust) precedes HashSetAsync(slot, Guid.Empty) — reaching the retire == confirmed send"
    - "Pattern 3 distinct routing: clean KeyExistsAsync==false → drop (no send/retire); thrown fault inside RetryLoop → infra_entryId (leave slot)"
    - "REINJECT⊻source-delete mutual exclusion (RECOV-03/T-51-09): any infra_entryId → REINJECT + return BEFORE the source delete; else delete source"
    - "Fresh-exec mint (D-03/Pitfall 4): recovery completed mints NewId.NextGuid() because the slot stores only entryId (no exec persisted)"
key-files:
  created:
    - "tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs"
    - "tests/BaseApi.Tests/Processor/EntryStepDispatchConsumerFacts.cs"
  modified:
    - "src/BaseProcessor.Core/Processing/ProcessorPipeline.cs"
    - "tests/BaseApi.Tests/Processor/DispatchTestKit.cs"
decisions:
  - "Recovery not-exist and L2-fault route differently (Pattern 3): a clean KeyExistsAsync==false drops the entry (no send, no retire); a thrown fault inside the bounded retry is infra_entryId (leave the slot, tail REINJECTs). NOT unified."
  - "Recovery temp-list representation (Claude's-Discretion per CONTEXT): a local List of (RedisValue Slot, Guid EntryId, bool Completed, bool Infra) tuples — no new type."
  - "SLOT-03 send-fail fact asserts the no-retire by routing the completed re-send through a throwing ResultSendFailProvider (SendResult propagates out of RunAsync) and asserting db.DidNotReceive().HashSetAsync(MessageIndex, ..., Guid.Empty) — proves the retire is never reached when the send fails."
metrics:
  duration: "~single session (2 tasks)"
  completed: "2026-06-11"
  tasks: 2
  files: 4
---

# Phase 51 Plan 03: Pipeline RECOVERY Pass Summary

Implemented `RunRecoveryAsync` — the A18 RECOVERY pass (design doc lines 179-203) — replacing the 51-02 `NotImplementedException` stub, completing the slot-array forward+recovery pipeline. On a redelivery (`exist L2[messageId]`) the pass HGETALLs the slot array, builds a per-slot temp list (clean not-exist → drop; thrown L2 fault → `infra_entryId`/leave-slot; exists → completed), re-sends every `completed` result with a FRESH `NewId.NextGuid()` exec (D-03) BEFORE retiring the slot to `Guid.Empty` (SLOT-03 send-before-retire), and finally either REINJECTs (any `infra_entryId` — replay owns the source lifecycle) ⊻ deletes the source (the all-clear path) — the two tail routes are mutually exclusive (RECOV-03). Added the recovery HASH fakes to `DispatchTestKit`, `PipelineRecoveryFacts` (5 facts) proving every route, and the consumer null-MessageId fact (D-10).

## What Was Built

### Task 1 — Implement RunRecoveryAsync (`c2b7d85`)
- Replaced the `private static Task RunRecoveryAsync(...) => throw new NotImplementedException(...)` stub with the full `private async Task` body.
- **RECOV-01:** `RetryLoop.ExecuteAsync(() => db.HashGetAllAsync(MessageIndex(messageId)), ...)` — exhaust → `SendKeeper(BuildReinject(d))` + `return` (no source delete, input intact).
- **Temp list:** per `HashEntry`, skip `Guid.Empty`/unparsable slots (inert/retired); else `RetryLoop` `KeyExistsAsync(ExecutionData(entryId))` — `!Succeeded` → `(Infra: true)` (leave slot); `Value==true` → `(Completed: true)`; clean `false` → NOT added (drop). Distinct routes (Pattern 3 / T-51-12).
- **Send-before-retire (SLOT-03/T-51-08):** for each completed temp item, `SendResult(BuildCompleted(d, NewId.NextGuid(), t.EntryId))` FIRST (D-03 fresh exec), then `HashSetAsync(MessageIndex, t.Slot, Guid.Empty.ToString())` + `KeyExpireAsync(MessageIndex, SlotTtl())` (D-06 refresh) AFTER. A retire exhaust is a deliberate no-op (A18 line 192; A16 dup-tolerant).
- **RECOV-03 tail (T-51-09):** `temp.Any(t => t.Infra)` → `SendKeeper(BuildReinject(d))` + `return` (no delete); else `!SourceStep.IsSource(d.EntryId)` → `KeyDeleteAsync(ExecutionData(d.EntryId))` (exhaust → `SendKeeper(BuildDelete(d))`).
- Dropped `static` from the signature (the body now calls instance members `SendResult`/`SendKeeper`/`Build*`/`SlotTtl`). `executionDataTtl` deliberately NOT threaded in (recovery writes no data keys). 0-warning Release + Debug.

### Task 2 — Recovery HASH fakes + PipelineRecoveryFacts + consumer fact (`23e80e9`)
- **DispatchTestKit:** added `Slots(params Guid[])` (`new HashEntry(i, id.ToString("D"))`), `RecoveryL2(messageId, slots, entryExists matrix, faultEntries, out db)` (existence-check on `MessageIndex` = TRUE → recovery branch; per-`ExecutionData(entryId)` exist resolved from the matrix; fault entries via per-key `When/Do` throw; retire HashSet + KeyExpire + KeyDelete succeed), `RecoveryHGetAllFaultL2` (HGETALL throws), `RecoveryAllCompletedL2` (all entries exist — SLOT-03 base), and `ResultSendFailProvider` (IStepResult sends throw, keeper sends record).
- **PipelineRecoveryFacts (5):** RECOV-01 (`Single(OfType<KeeperReinject>())` + `DidNotReceive().KeyDeleteAsync`); RECOV-02+RECOV-03-with-infra mixed 3-entry (exists/absent/fault) → `Single(OfType<StepCompleted>())` + `Received(1).HashSetAsync(MessageIndex, ..., Guid.Empty)` + `Single(OfType<KeeperReinject>())` + `DidNotReceive().KeyDeleteAsync`; D-03 (`sc.ExecutionId != Guid.Empty && != d.ExecutionId`); RECOV-03 no-infra (`Received().KeyDeleteAsync(ExecutionData(d.EntryId))` + no REINJECT); SLOT-03 send-fail (`ThrowsAsync<RedisConnectionException>` + `DidNotReceive().HashSetAsync(MessageIndex, ..., Guid.Empty)`).
- **EntryStepDispatchConsumerFacts:** null `ctx.MessageId` → `ThrowsAsync<InvalidOperationException>` (D-10/T-51-11); a real-ish pipeline (ForwardOkL2) satisfies the ctor but is never reached (the guard fires after the metric line, before `RunAsync`).

## Verification

- `dotnet build SK_P.sln -c Release --nologo` and `-c Debug` — both 0 warnings, 0 errors.
- Grep gate: `ProcessorPipeline.cs` contains `HashGetAllAsync(L2ProjectionKeys.MessageIndex(messageId))`, contains NO `NotImplementedException`, contains NO `finally` (block); `BuildCompleted(d, NewId.NextGuid()` precedes the `HashSetAsync(... Guid.Empty.ToString())` retire (send-before-retire); `temp.Any(t => t.Infra)` gates `BuildReinject(d)` + `return` before the source delete.
- `--filter-query "/*/*/PipelineRecoveryFacts"` — 5/5 passed.
- `--filter-query "/*/*/EntryStepDispatchConsumerFacts"` — 1/1 passed.
- Full Pipeline suite (Pre+In+Post+EndDelete+Forward+Recovery, 6 classes via multiple `--filter-query`) — **31/31 passed** (26 prior + 5 recovery).

Test-runner: MTP `--filter-query "/*/*/<Class>"` graph syntax (class-level wildcards do NOT match; each Pipeline class passed explicitly). `dotnet test --filter` is ignored under MTP — not used.

## Deviations from Plan

None — both tasks executed as written. The plan's example code was applied verbatim modulo the `static`→instance signature change (called for in the plan's action note) and the test-double `Items()` overload-ambiguity resolved with `new List<ProcessItem>()` (a NoopProcessor — the In stage is never reached on the recovery branch).

## Deferred Issues

- **A18 `UseMessageRetry = none` / `_error`-disabled end-state** — carried from 51-02 (keep-latch decision); deferred to Phase 53 teardown (threat T-51-07 accept/deferred). Not in scope here.

## Known Stubs

None — the `RunRecoveryAsync` stub (the only stub flagged in 51-02) is now fully implemented. No new stubs introduced.

## Threat Flags

None — no new security-relevant surface beyond the plan's `<threat_model>` (the recovery pass re-reads the same `MessageIndex`/`ExecutionData` keys the forward pass already touches; the L2 and bus trust boundaries are unchanged). T-51-08 (send-before-retire), T-51-09 (REINJECT⊻delete), T-51-10/D-03 (fresh exec), T-51-11/D-10 (null MessageId), T-51-12 (Pattern 3 distinct routing) are all mitigated and asserted.

## Self-Check: PASSED

- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — RunRecoveryAsync body present, no NotImplementedException/finally — FOUND
- `tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs` — FOUND (created `23e80e9`)
- `tests/BaseApi.Tests/Processor/EntryStepDispatchConsumerFacts.cs` — FOUND (created `23e80e9`)
- `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` — recovery muxes added — FOUND
- Commit `c2b7d85` (feat, Task 1) — FOUND
- Commit `23e80e9` (test, Task 2) — FOUND
