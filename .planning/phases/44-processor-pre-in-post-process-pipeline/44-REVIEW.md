---
phase: 44-processor-pre-in-post-process-pipeline
reviewed: 2026-06-08T00:00:00Z
depth: standard
files_reviewed: 11
files_reviewed_list:
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
  - src/BaseProcessor.Core/Processing/BaseProcessor.cs
  - src/BaseProcessor.Core/Processing/ProcessItem.cs
  - src/BaseProcessor.Core/Processing/ProcessOutcome.cs
  - src/BaseProcessor.Core/Processing/ProcessStatusException.cs
  - src/BaseProcessor.Core/Resilience/RetryLoop.cs
  - src/BaseProcessor.Core/Resilience/KeyAbsentException.cs
  - src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
  - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
  - src/Processor.Sample/SampleProcessor.cs
findings:
  critical: 0
  warning: 5
  info: 4
  total: 9
status: issues_found
---

# Phase 44: Code Review Report

**Reviewed:** 2026-06-08
**Depth:** standard
**Files Reviewed:** 11
**Status:** issues_found

## Summary

Reviewed the Phase-44 Pre→In→Post→finally-end-delete pipeline rewrite of `EntryStepDispatchConsumer` into a testable `ProcessorPipeline` runner, plus the supporting `RetryLoop`, `ProcessStatusException` family, `ProcessItem`/`ProcessOutcome` records, the thinned consumer, the startup-orchestrator bind reconciliation, the DI wiring, and the migrated `SampleProcessor`.

The terminal routing is well-structured and matches the locked design: REINJECT on read-exhaustion returns without arming end-delete, the `readSucceeded` gate correctly distinguishes the source-step / REINJECT paths from every read-succeeded path, UPDATE→write→CLEANUP ordering holds, write-exhaustion routes to INJECT without aborting the batch, and send-exhaustion propagates to the bus `_error` latch. The single-place `RetryLoop` (surface-not-throw) cleanly separates the four in-code op retries from the outer `UseMessageRetry` dead-letter latch (Pitfall 1 reconciled via the shared `Retry:Limit`), so there is no double-retry of L2/send ops. No security vulnerabilities found — the only validation surface (`ProcessorJsonSchemaValidator`) is SSRF-locked and fail-closed.

The findings below are correctness/robustness concerns, the most material being the ordering of the `finally` end-delete relative to a propagating send-exhaustion (WR-01) and the swallowing of `OperationCanceledException` into a business `StepFailed` (WR-02). None are blocking, but WR-01 and WR-02 are worth an explicit decision before this ships.

## Warnings

### WR-01: `finally` end-delete runs BEFORE a propagating send-exhaustion reaches the bus, deleting the L2 input the bus replay needs

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:154-164` (interacts with `:138`, `:174`, `:182`)
**Issue:** The `finally` end-delete is designed to fire on every read-succeeded path, including the path where the `try` body is unwinding because a `SendResult`/`SendKeeper` exhausted and re-threw (`:174` / `:182`, the D-10 propagate-to-`_error` contract). Because `finally` executes during unwind — *before* the exception reaches `UseMessageRetry` — the sequence for a CLEANUP/Completed send-exhaustion is:

1. `read` succeeds → `readSucceeded = true`
2. Post: write `L2[entryId]` succeeds (a NEW key), then `SendKeeper(Cleanup)` or `SendResult(Completed)` exhausts and throws
3. unwind enters `finally` → `KeyDeleteAsync(L2[d.EntryId])` deletes the **inbound input key**
4. exception propagates → `UseMessageRetry`/`_error` re-delivers `EntryStepDispatch`
5. the replay re-enters Pre, reads `L2[d.EntryId]`, now **absent** → REINJECT (a different terminal than intended)

So a send give-up that is supposed to be replayed verbatim by the bus is instead silently downgraded to a REINJECT on redelivery because the input was already deleted by the finally. This is the inverse of the deliberate "REINJECT leaves the input intact for the keeper" invariant (`:84-85`). Note the produced-output key written at `:132` is a fresh `entryId`, so it is the *inbound* `d.EntryId` deletion that is the problem.

**Fix:** Gate the end-delete so it does NOT run when the `try` body is unwinding via an exception (i.e. only on the normal-completion + handled-business-result paths). One option — capture completion explicitly and check it in the finally:

```csharp
var readSucceeded = false;
var pipelineFaulted = false;
try
{
    // ... existing body ...
}
catch
{
    pipelineFaulted = true;   // a propagating send-exhaustion (D-10) — let the bus replay own recovery
    throw;
}
finally
{
    if (readSucceeded && !pipelineFaulted)
    {
        var del = await RetryLoop.ExecuteAsync(
            () => db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(d.EntryId)), limit, ct);
        if (!del.Succeeded)
            await SendKeeper(BuildDelete(d), limit, ct);
    }
}
```

If, conversely, deleting-before-replay is the intended behavior, add an explicit comment at `:157` stating that a propagated send-exhaustion deliberately deletes the input first and is expected to land as a REINJECT on redelivery — because the current XML doc ("over every read-succeeded path") reads as if the exception path was not considered.

### WR-02: `OperationCanceledException` from the author/`RetryLoop` is swallowed into a business `StepFailed`

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:113-117`
**Issue:** The IN-stage `catch (Exception ex)` catches everything that is not a `ProcessStatusException`, including `OperationCanceledException`/`TaskCanceledException` raised by shutdown cancellation (the `ct` flows into `processor.ExecuteAsync` and into `RetryLoop`, which calls `ct.ThrowIfCancellationRequested()` at `RetryLoop.cs:16`). On host shutdown this turns a genuine cancellation into a business `StepFailed` sent to the orchestrator (`:115`) — a false business outcome that also performs a wire send during teardown. The same swallow applies to any `OperationCanceledException` surfaced from the Pre read loop if it unwinds here.

**Fix:** Let cancellation propagate (so MassTransit treats it as a non-ack/redeliver rather than a business failure):

```csharp
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    throw;   // shutdown cancellation is not a business failure
}
catch (Exception ex)
{
    await SendResult(BuildFailed(d, ex.Message), limit, ct);
    return;
}
```

### WR-03: A send-exhaustion thrown inside `finally` masks the original in-flight exception

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:159-162`
**Issue:** When the `try` body is already unwinding with an exception (e.g. the WR-01 propagating send-exhaustion, or a cancellation), and the end-delete itself exhausts so `SendKeeper(BuildDelete(d))` at `:162` *also* throws (send-exhaustion re-throws at `:182`), the `finally`'s exception replaces the original. The orchestrator/`_error` latch then sees the DELETE-send fault instead of the real CLEANUP/Completed-send fault that triggered recovery — losing the true failure cause and routing to the wrong Keeper terminal. This is a direct consequence of doing an awaited, throwing operation inside a `finally`.

**Fix:** This is largely subsumed by the WR-01 fix (not running end-delete on the faulted path). If end-delete must still run while unwinding, guard the keeper send so the finally never throws over an in-flight exception:

```csharp
if (!del.Succeeded)
{
    try { await SendKeeper(BuildDelete(d), limit, ct); }
    catch (Exception keeperEx)
    {
        logger.LogError(keeperEx, "End-delete KeeperDelete send exhausted during unwind; preserving original fault.");
    }
}
```

### WR-04: `CancellationToken` is checked between retry attempts but never passed to the Redis/send operations

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:77,132,160,173,181` and `src/BaseProcessor.Core/Resilience/RetryLoop.cs:14-19`
**Issue:** `RetryLoop.ExecuteAsync` honors `ct` only via `ThrowIfCancellationRequested()` at the top of each attempt (`RetryLoop.cs:16`); the wrapped operation itself receives no token. The Redis calls (`StringGetAsync`/`StringSetAsync`/`KeyDeleteAsync` at `:77/:132/:160`) and the bus sends are invoked with no cancellation — in fact both `ep.Send(...)` calls at `:173/:181` are hardcoded to `CancellationToken.None`. Consequence: a single in-flight Redis or broker await that hangs cannot be cancelled by shutdown; cancellation only takes effect at the *next* attempt boundary, and an op that never returns blocks shutdown indefinitely. The `CancellationToken.None` on sends appears intentional (avoid cancelling a half-sent recovery message mid-flight), but the Redis ops have no such justification and silently ignore `ct`.

**Fix:** Pass `ct` into the StackExchange.Redis calls where supported is N/A (these overloads take `CommandFlags`, not a token), so the realistic mitigation is to document that per-attempt op cancellation is intentionally coarse-grained, and/or pass `ct` to the sends if mid-send cancellation is acceptable. At minimum, add a comment at `RetryLoop.cs:16` clarifying that `ct` cancels only at attempt boundaries, not in-flight — otherwise the `ct` parameter threaded everywhere reads as finer-grained than it is.

### WR-05: Source-step path sends NO orchestrator result and emits NO Keeper message on an empty author batch

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:69-71,120` and `src/Processor.Sample/SampleProcessor.cs:43-46`
**Issue:** A `SourceStep.IsSource` dispatch sets `validatedData = string.Empty`, skips input validation, and proceeds to IN. If the author returns an empty `List<ProcessItem>` (an explicitly documented legal return per D-03 — "empty list = no continuation, sends nothing"), the `foreach` at `:120` is a no-op, `readSucceeded` is `false`, the finally does nothing, and the consumer acks — the dispatch is silently consumed with zero result and zero Keeper trace. For a source step this is by design, but there is no log line marking "dispatch produced 0 items," so an author bug that accidentally returns an empty list is indistinguishable from intended no-continuation and leaves no diagnostic. Combined with `SampleProcessor.cs:32` deserializing a possibly-empty `validatedData`/`payload` only via the `IsNullOrWhiteSpace` guard on `payload` (not on `validatedData`), an author who deserializes `validatedData` on a source step would hit a `JsonException` → swallowed to `StepFailed` (WR-02 territory).

**Fix:** Emit a debug/information log when `items` is empty so a zero-continuation dispatch is observable:

```csharp
if (items.Count == 0)
    logger.LogInformation("ProcessAsync returned 0 items for dispatch {StepId}; no continuation emitted.", d.StepId);
```

This is advisory, not a correctness bug — the silent-ack is intended behavior.

## Info

### IN-01: `RetryLoop` returns `RetryOutcome.Exhausted(last!)` with a `last` that can only be null if `limit < 1` is coerced — but `Math.Max(1, limit)` guarantees ≥1 attempt, so verify `last!` is always set

**File:** `src/BaseProcessor.Core/Resilience/RetryLoop.cs:14,20`
**Issue:** `Math.Max(1, limit)` guarantees the loop body runs at least once, so on exhaustion `last` is always assigned by the `catch` at `:18` before `:20` — the `last!` bang is sound. This is correct as written; flagging only because the invariant (loop always executes ≥1 attempt, so `last` is non-null at exhaustion) is load-bearing and undocumented. A one-line comment at `:20` ("`last` is non-null: Math.Max(1,limit) forces ≥1 attempt, and reaching here means every attempt threw") would make the bang self-justifying.

### IN-02: Magic queue-name string interpolation duplicated across the two send owners

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:171,179`
**Issue:** `new Uri($"queue:{OrchestratorQueues.Result}")` and `new Uri($"queue:{KeeperQueues.Recovery}")` repeat the `queue:` scheme-prefix idiom inline. Low risk (the queue-name constants are centralized), but the `queue:` prefix is itself a magic literal repeated in two places. Consider a small helper (`QueueUri(name)`) if a third send owner is ever added.

### IN-03: `SampleProcessor` `ct` parameter is unused (no cancellation observed in the sync transform)

**File:** `src/Processor.Sample/SampleProcessor.cs:28-30`
**Issue:** `ProcessAsync` takes `CancellationToken ct` but never observes it (the body is synchronous, returns `Task.FromResult`). This is fine for the sample and matches the abstract seam signature, but a longer-running real author would need to thread `ct`. No action needed — noting that the sample does not demonstrate `ct` usage, which a copy-paste author might inherit.

### IN-04: `BuildProcessing` mints a fresh `ExecutionId` while the `ProcessingException` message is logged-only — confirm the discarded message is intended

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:106,109,198-199`
**Issue:** `ProcessingException` maps to `StepProcessing`, which per D-05 has no wire message field, so `e.Message` is logged at `:109` only and `BuildProcessing` (`:198`) carries a newly-minted `ExecutionId` with no message. This matches the locked design (D-05: "processing message logged only"). Flagging only to confirm the asymmetry is intentional: `FailedException`/`CancelledException` carry their message onto the wire, `ProcessingException` does not — an author may be surprised their processing message never leaves the box. The XML doc on `ProcessStatusException.cs:6-7` already documents this, so this is informational only.

---

_Reviewed: 2026-06-08_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
