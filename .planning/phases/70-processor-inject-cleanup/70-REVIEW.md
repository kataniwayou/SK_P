---
phase: 70-processor-inject-cleanup
reviewed: 2026-06-16T00:00:00Z
depth: standard
files_reviewed: 8
files_reviewed_list:
  - src/Keeper/Recovery/InjectConsumer.cs
  - src/Messaging.Contracts/KeeperInject.cs
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - tests/BaseApi.Tests/Keeper/KeeperDeleteInvariantFacts.cs
  - tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs
  - tests/BaseApi.Tests/Contracts/KeeperContractTests.cs
  - tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs
  - tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs
findings:
  critical: 0
  warning: 2
  info: 2
  total: 4
status: issues_found
---

# Phase 70: Code Review Report

**Reviewed:** 2026-06-16T00:00:00Z
**Depth:** standard
**Files Reviewed:** 8
**Status:** issues_found

## Summary

The Phase 70 deletion-heavy refactor is mechanically sound. `InjectConsumer` correctly implements
the non-destructive INJECT spec (write-then-send, no delete). `KeeperInject` is clean after the
`DeleteEntryId` drop. `ProcessorPipeline.BuildInject` no longer supplies the deleted field. The
new `KeeperDeleteInvariantFacts` and the reshaped `InjectConsumerFacts` add meaningful invariant
coverage with proper co-assertions to prevent the negative guard from passing on a silent no-op.

Two warnings surface. The more significant one is a test-infrastructure mismatch: `RecoveryTestKit.Db()`
stubs the legacy 6-arg `StringSetAsync` overload that SE.Redis 2.13.1 no longer binds the 2-arg
production call to, leaving the stub dead and the assertion in `InjectConsumerFacts` relying on
NSubstitute's default-return behavior rather than an explicit stub. The second warning is a
potential exception-transparency gap in `InjectConsumer.HandleAsync` when `GetSendEndpoint` throws
synchronously. Two info items round out the findings.

---

## Warnings

### WR-01: `RecoveryTestKit.Db()` stubs the wrong `StringSetAsync` overload — the production write path is unstubbed

**File:** `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs:67`

**Issue:** `RecoveryTestKit.Db()` stubs:
```csharp
db.StringSetAsync(
    Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
    Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
    .Returns(true);
```
This is the legacy 6-arg overload (`TimeSpan?/bool/When/CommandFlags`). SE.Redis 2.13.1 removed
the `bool keepTtl` parameter and introduced a new 5-arg signature:
`StringSetAsync(RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags)`.

The production `InjectConsumer.HandleAsync` line 24 calls `Db.StringSetAsync(key, data)` (2-arg).
The comment in `InjectConsumerFacts.cs:53-55` explicitly acknowledges that SE.Redis 2.13.1 binds
this 2-arg call to the **5-arg** `Expiration/ValueCondition` overload — the same shape the test
asserts with `Arg.Any<Expiration>()`. The kit's stub therefore never fires for the INJECT write
path.

**Consequence:** The `Received(1)` assertion in `InjectConsumerFacts` succeeds because NSubstitute
captures the actual (unstubbed) dispatch to the 5-arg overload. However, the write "succeeds" only
because NSubstitute returns `Task.FromResult(false)` for unstubbed `Task<bool>` methods, and
`RetryLoop` treats any non-throwing return as success. The `false` value is discarded by the
consumer, so the test passes today. The danger is forward: if a future refactor changes the
consumer to act on the write's boolean return, or if the NSubstitute version changes its
default-return behavior, the absent stub becomes a silent regression vector.

Additionally, `KeeperDeleteInvariantFacts.InjectConsumer_never_deletes` (line 63-93) uses the same
`RecoveryTestKit.Db()` and would silently succeed even if the write op threw — the co-assertion
only checks for `StepCompleted` being sent, but a thrown write (after retry exhaustion) would
re-throw out of `Consume` before the send, causing the fact to fail with an unexpected exception
rather than a meaningful assertion failure.

**Fix:** Add the 5-arg overload stub to `RecoveryTestKit.Db()` so the production write path has an
explicit, observable success stub:
```csharp
// SE.Redis 2.13.1: 2-arg StringSetAsync binds to the Expiration/ValueCondition overload
db.StringSetAsync(
        Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
        Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>())
    .Returns(true);
```
Retain the 6-arg stub (or remove it) as appropriate for other consumers that use the named-param
form (`expiry: TimeSpan...`). The fix makes the stub intentional and removes the silent reliance on
NSubstitute's default return.

---

### WR-02: `InjectConsumer.HandleAsync` resolves `GetSendEndpoint` outside the `Guard` return value — a synchronous throw from the endpoint factory escapes retry

**File:** `src/Keeper/Recovery/InjectConsumer.cs:35`

**Issue:**
```csharp
var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}")), ct);
await Guard(() => ep.Send(completed, CancellationToken.None), ct);
```
`Guard<T>` wraps `Func<Task<T>>`. `Send.GetSendEndpoint(...)` returns `Task<ISendEndpoint>`, so
it is wrapped correctly and a transient `GetSendEndpoint` failure is retried. This is noted in the
inline comment "IN-01: resolve the send endpoint through Guard too". No logic bug here per se.

However, the `Send` property returns `sendProvider` which is `ISendEndpointProvider`. If the
provider implementation throws **synchronously** before returning a `Task` (i.e., throws from
within the `Func<Task<T>>` constructor rather than from the `await`), the lambda captures the
exception and `RetryLoop.ExecuteAsync` catches it in the `try/catch` at line 17 of `RetryLoop.cs`.
That is fine for synchronous throws too — `RetryLoop` catches any `Exception` regardless of where
in the lambda it originates.

The actual risk is narrower: `ep.Send(completed, CancellationToken.None)` uses
`CancellationToken.None` rather than the `ct` passed into `HandleAsync`. This means a consumer
cancellation (e.g. graceful shutdown) during the `Send` leg will not be honored — the send will
block until the bus times out rather than cooperating with the `CancellationToken` on the outer
consumer. The `Guard` wrapping does call `ct.ThrowIfCancellationRequested()` at the top of each
`RetryLoop` attempt, but only between attempts, not during the blocking send itself.

**Fix:** Pass `ct` through the send call:
```csharp
await Guard(() => ep.Send(completed, ct), ct);
```
This mirrors the pattern used in `ProcessorPipeline.SendResult/SendKeeper` where `CancellationToken.None`
is also used. If the design intent is to always complete an in-flight send even during shutdown
(i.e., `CancellationToken.None` is deliberate), the comment at line 36 should document the
rationale explicitly so a future reader does not "fix" it to `ct` inadvertently. Currently
the comment says "IN-01 inner send" without addressing the token choice.

---

## Info

### IN-01: `PipelineForwardFacts.ExistCheckFault_Reinject_NoSourceDelete` covers only the single-key `KeyDeleteAsync` overload in the no-source-delete assertion

**File:** `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs:59-60`

**Issue:**
```csharp
await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());                  // input intact — never deleted
await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
```
This fact guards the REINJECT exit path (no source delete). It asserts BOTH single-key overloads
(no-flags and with-flags), which is correct. However, it does not assert the multi-key overload
`KeyDeleteAsync(RedisKey[], CommandFlags)` — the shape used by `DeleteTerminalAsync`. Since the
REINJECT path returns early (before `DeleteTerminalAsync` is called), the multi-key DEL should also
not fire. The `KeeperDeleteInvariantFacts` pattern correctly asserts both overloads; this fact does
not.

This is not a regression risk in the current implementation (the early `return` before any delete
call means neither overload fires), but it is an incomplete guard compared to the invariant pattern
established elsewhere.

**Fix:** Add the multi-key assertion to match the invariant pattern:
```csharp
await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
```

---

### IN-02: `SC2RecoveryPathsE2ETests` INJECT proof does not assert the `StepCompleted` send to `queue:orchestrator-result`

**File:** `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs:177-204`

**Issue:** The INJECT block (STATE 3) asserts only the L2 write side-effect (the data key is
present). The consumer's second effect — sending a `StepCompleted` to `queue:orchestrator-result`
— is not asserted. The comment on line 201 explicitly calls this out: "the StepCompleted send to
queue:orchestrator-result is exercised end-to-end by SC1's round-trip."

This is a documented scope decision, not a defect. However, in a live-stack E2E that is gated as
the authoritative proof of the INJECT state (tagged `[Trait("Phase", "62")]`), the absence of the
send assertion means a regression that drops the `StepCompleted` send but preserves the write
would be invisible to this test. The hermetic `InjectConsumerFacts` and the
`KeeperDeleteInvariantFacts` co-assertion do catch it, so it is covered at the unit level.

**Fix (optional):** If the orchestrator-result queue depth is observable on the live stack (it may
be consumed rapidly by the orchestrator, making depth polling unreliable), add a bounded poll on
`queue:orchestrator-result` depth increment as a secondary assertion. If the queue is consumed too
fast to poll, document the cross-test dependency explicitly (SC1 covers it) in the test's summary
comment and close this as by-design.

---

_Reviewed: 2026-06-16T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
