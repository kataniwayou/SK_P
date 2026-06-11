---
phase: 54-terminal-index-delete
reviewed: 2026-06-11T22:59:18Z
depth: standard
files_reviewed: 10
files_reviewed_list:
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - src/Keeper/Recovery/DeleteConsumer.cs
  - src/Messaging.Contracts/KeeperDelete.cs
  - tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs
  - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
  - tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs
  - tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs
  - tests/BaseApi.Tests/Processor/PipelinePreFacts.cs
  - tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs
findings:
  critical: 0
  warning: 3
  info: 1
  total: 4
status: issues_found
---

# Phase 54: Code Review Report

**Reviewed:** 2026-06-11T22:59:18Z
**Depth:** standard
**Files Reviewed:** 10
**Status:** issues_found

## Summary

The production code (`ProcessorPipeline.cs`, `DeleteConsumer.cs`, `KeeperDelete.cs`) is correct on every phase-specific correctness property: `DeleteTerminalAsync` issues one multi-key array DEL (GC-01), the D-03 persist-then-unconditional-send sequence is intact, there is no source-step early-return (D-06), the two `KeyExpireAsync` (D-07) TTL backstop calls are present, and REINJECT paths return before reaching the terminal delete (GC-02). `DeleteConsumer` correctly delegates to the single-call array DEL via `Guard(...)`.

The issues are entirely in the test layer. Three test facts make assertions that contradict the current production behavior and will silently pass (false-greens) or assert the wrong property. Two fault muxes (`PresentReadWriteFaultL2`, `ForwardDataFaultL2`) omit the array `KeyDeleteAsync` stub — an NSubstitute false-green trap documented in the phase context. One test method's assertion comments describe old pre-A19 behavior (source-step no end-delete) but the test body passes because the array-delete assertion is absent.

---

## Warnings

### WR-01: `PresentReadWriteFaultL2` — missing array `KeyDeleteAsync` stub (NSubstitute false-green trap)

**File:** `tests/BaseApi.Tests/Processor/DispatchTestKit.cs:115`

**Issue:** `PresentReadWriteFaultL2` stubs only the scalar `KeyDeleteAsync(RedisKey, CommandFlags)` overload (line 115). The production terminal tail now calls `db.KeyDeleteAsync(new RedisKey[]{...}, CommandFlags)` — the array overload. NSubstitute returns `Task.FromResult(0L)` for any unstubbed `Task<long>` method, so `RetryLoop` sees a successful result (`.Succeeded == true`), never exhausts, and the tail completes without escalating. Any fact that uses this mux to test the happy-path terminal delete is testing the right observable outcome (delete "succeeded"), but is doing so through an unstubbed default rather than a real return-value setup. More critically, no fact using this mux can detect a regression where the production code reverts to two scalar deletes — the scalar stub returns `true`, the array call returns `0L` default, and both look healthy. The missing stub also means `Received()` assertions on the array overload will fail on a clean run — though no current fact using this mux asserts the array overload (the only consumers are `PipelineForwardFacts.ExistCheckFault_Reinject_NoSourceDelete` via a hand-rolled mux, and `PipelinePreFacts.InputInvalid_Failed` which uses `PresentReadWriteDeleteOkL2` — but `EndDelete_RunsOnBusinessFail` uses `PresentReadWriteDeleteOkL2` not this mux, so the live risk is lower here; it surfaces when a future fact exercises the write-fault path and also checks delete behavior).

**Fix:** Add the array overload stub alongside the scalar one:
```csharp
// KeyDeleteAsync is a no-op success (the source-delete tail runs and succeeds on this path).
db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);   // A19: array DEL count removed
```

---

### WR-02: `ForwardDataFaultL2` — missing array `KeyDeleteAsync` stub (NSubstitute false-green trap)

**File:** `tests/BaseApi.Tests/Processor/DispatchTestKit.cs:239`

**Issue:** `ForwardDataFaultL2` stubs only the scalar `KeyDeleteAsync(RedisKey, CommandFlags)` overload (line 239). As described in WR-01, the production terminal tail calls the array overload. The mux is used by `PipelineForwardFacts.DataWriteFault_Inject_WithIdSet` (INFRA-02). On the INFRA-02 path, `SendKeeper(BuildInject(...))` fires and then `DeleteTerminalAsync` is **not** called (the item loop `continue`s, then the post-loop `await DeleteTerminalAsync(...)` at line 300 of `ProcessorPipeline.cs` still executes). The terminal delete therefore hits an unstubbed array overload returning `0L` (= "succeeded"), meaning the test passes for the wrong reason — the terminal delete would silently "succeed" even if the production code were broken, and any future assertion on `Received(1).KeyDeleteAsync(array)` on this path will fail because the stub is missing.

**Fix:**
```csharp
db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);   // A19: array DEL count removed
```

---

### WR-03: `PipelinePreFacts.SourceStep_Skip_EmptyData_NoEndDelete` — stale assertion comments and missing array-DEL assertion leave source-step delete unverified

**File:** `tests/BaseApi.Tests/Processor/PipelinePreFacts.cs:29`

**Issue:** The test method name (`NoEndDelete`) and the inline comment on line 43 (`// no end-delete (readSucceeded false)`) describe the **pre-A19** behavior where a source step (Guid.Empty entryId) skipped the terminal delete. Under A19/D-06, the source-step path now **does** call `DeleteTerminalAsync`, issuing `KeyDeleteAsync(new RedisKey[]{ ExecutionData(Guid.Empty), MessageIndex(messageId) })`. The assertions on lines 43–44 assert `DidNotReceive` on both scalar `KeyDeleteAsync` overloads — which is correct (no scalar deletes), but there is **no assertion on the array overload**. The test passes, but:

1. It does not verify that the array DEL **is** called (the defining A19 behavior for source steps per D-06).
2. Its stale name and comments actively mislead future maintainers about what the source-step path does.
3. The corresponding `PipelineEndDeleteFacts.EndDelete_Skipped_OnSourceStep` fact correctly asserts `Received(1).KeyDeleteAsync(array)` with the right operands — so the behavior is tested there, but the `PipelinePreFacts` test provides false documentation that contradicts it.

**Fix:** Update the method name, comment, and add the array-DEL assertion. The method in `PipelineEndDeleteFacts` already covers the positive case, so the simplest fix in `PipelinePreFacts` is to remove the misleading "no end-delete" framing and add a positive assertion:
```csharp
// A19/D-06: source-step DOES issue the array DEL (ExecutionData(Guid.Empty) is a harmless absent operand).
await db.Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
// … and zero scalar deletes (atomicity heart).
await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());
await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
```
Rename the method to `SourceStep_Skip_EmptyData_ArrayDeleteRuns` (or similar) to match the actual A19 behavior.

---

## Info

### IN-01: `ForwardSlotFaultL2` — missing array `KeyDeleteAsync` stub (lower-severity instance)

**File:** `tests/BaseApi.Tests/Processor/DispatchTestKit.cs:201`

**Issue:** `ForwardSlotFaultL2` stubs only the scalar `KeyDeleteAsync(RedisKey, CommandFlags)` (line 201), not the array overload. This mux is used by `PipelineForwardFacts.SlotWriteFault_Drop` (INFRA-01). On the INFRA-01 drop path, the item is skipped via `continue`, then the post-loop `DeleteTerminalAsync` still runs and hits the unstubbed array overload (returns `0L` default = succeeded). The fact only asserts `Assert.Empty(send.SentKeeper)` and `Assert.Empty(send.Sent.OfType<StepCompleted>())` — it does not check the delete behavior — so the missing stub does not cause a false-green on the currently-checked properties. However, it is inconsistent with the documented A19 pattern and would mask a regression if a future assertion were added to this fact.

**Fix:** For consistency and future-safety, add the array stub:
```csharp
db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);   // A19: array DEL
```

---

_Reviewed: 2026-06-11T22:59:18Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
