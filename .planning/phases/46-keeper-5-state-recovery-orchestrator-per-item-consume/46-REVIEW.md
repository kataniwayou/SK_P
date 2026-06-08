---
phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume
reviewed: 2026-06-09T00:00:00Z
depth: standard
files_reviewed: 45
files_reviewed_list:
  - src/BaseConsole.Core/Resilience/RetryLoop.cs
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - src/Keeper/Program.cs
  - src/Keeper/Recovery/CleanupConsumer.cs
  - src/Keeper/Recovery/CleanupConsumerDefinition.cs
  - src/Keeper/Recovery/DeleteConsumer.cs
  - src/Keeper/Recovery/DeleteConsumerDefinition.cs
  - src/Keeper/Recovery/InjectConsumer.cs
  - src/Keeper/Recovery/InjectConsumerDefinition.cs
  - src/Keeper/Recovery/RecoveryConsumerBase.cs
  - src/Keeper/Recovery/RecoveryDataGoneException.cs
  - src/Keeper/Recovery/ReinjectConsumer.cs
  - src/Keeper/Recovery/ReinjectConsumerDefinition.cs
  - src/Keeper/Recovery/UpdateConsumer.cs
  - src/Keeper/Recovery/UpdateConsumerDefinition.cs
  - src/Keeper/RecoveryOptions.cs
  - src/Keeper/appsettings.json
  - src/Messaging.Contracts/KeeperReinject.cs
  - src/Orchestrator/Consumers/StepCancelledConsumer.cs
  - src/Orchestrator/Consumers/StepCancelledConsumerDefinition.cs
  - src/Orchestrator/Consumers/StepCompletedConsumer.cs
  - src/Orchestrator/Consumers/StepCompletedConsumerDefinition.cs
  - src/Orchestrator/Consumers/StepFailedConsumer.cs
  - src/Orchestrator/Consumers/StepFailedConsumerDefinition.cs
  - src/Orchestrator/Consumers/StepProcessingConsumer.cs
  - src/Orchestrator/Consumers/StepProcessingConsumerDefinition.cs
  - src/Orchestrator/Consumers/TypedResultConsumer.cs
  - src/Orchestrator/Observability/OrchestratorMetrics.cs
  - src/Orchestrator/Program.cs
  - tests/BaseApi.Tests/Contracts/KeeperContractTests.cs
  - tests/BaseApi.Tests/Keeper/CleanupConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs
  - tests/BaseApi.Tests/Keeper/RecoveryGateWaitFacts.cs
  - tests/BaseApi.Tests/Keeper/RecoveryPartitionFacts.cs
  - tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs
  - tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/UpdateConsumerFacts.cs
  - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
  - tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs
  - tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs
  - tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs
  - tests/BaseApi.Tests/Processor/RetryLoopFacts.cs
findings:
  critical: 0
  warning: 3
  info: 4
  total: 7
status: issues_found
---

# Phase 46: Code Review Report

**Reviewed:** 2026-06-09T00:00:00Z
**Depth:** standard
**Files Reviewed:** 45
**Status:** issues_found

## Summary

Reviewed the five Keeper recovery-state consumers (UPDATE/REINJECT/INJECT/DELETE/CLEANUP), their
single-owner partitioner/retry endpoint config, the shared `RecoveryConsumerBase` gate-wait + RetryLoop
Guard, the Orchestrator `TypedResultConsumer<T>` family, the relocated `RetryLoop`, the
`ProcessorPipeline`, and the Phase-46 test suite.

The focus areas hold up well:

- **Exception routing is correct.** The transient `RecoveryGateTimeoutException` is thrown only on the
  bounded gate-wait CTS firing (with a precise `when` filter that does NOT mask a genuine bus-shutdown
  cancellation), while the terminal `RecoveryDataGoneException` is thrown inside the retried read closure on
  an absent/empty L2 key. Both route to `skp-dlq-1` via the inherited consolidated error filter.
- **No double-registration of the error filter.** `ConfigureError` is registered exactly once, in
  `BaseConsole.Core`'s once-per-endpoint `AddConfigureEndpointsCallback`. None of the recovery or
  result-consumer definitions call `ConfigureError`/`SetQueueArgument` — the four sibling definitions are
  genuine no-ops and `UpdateConsumerDefinition`/`StepCompletedConsumerDefinition` are the sole owners of the
  endpoint retry + partitioner, matching the documented single-owner pattern.
- **Redis op correctness holds.** UPDATE writes the composite key WITH the `TtlDays` expiry; INJECT writes
  the data key with the bare 2-arg `StringSetAsync` (NO TTL) per contract; the partition key is the 4-tuple
  excluding StepId, deterministically mapped to a Guid for the 8.5.5 endpoint overload.

The findings below are one ordering/idempotency concern in INJECT, two concurrency/operability
observations, and minor consistency/style notes. No critical issues.

## Warnings

### WR-01: INJECT re-delivery can emit a duplicate StepCompleted (send-before-delete is not idempotent)

**File:** `src/Keeper/Recovery/InjectConsumer.cs:32-51`
**Issue:** The INJECT order is read composite → write `L2[entryId]` → Send `StepCompleted` → delete
composite. The Send to the orchestrator result queue happens BEFORE the composite delete. If the Send
succeeds but the subsequent `KeyDeleteAsync` exhausts its RetryLoop and throws (transient Redis fault on the
delete only), the whole `Consume` faults and the endpoint `UseMessageRetry` re-attempts the delivery. On
re-attempt the composite key is still present (the delete never landed), so the consumer reads it again,
mints a NEW `entryId`, writes a second data key, and sends a SECOND `StepCompleted` for the same execution.
Because the orchestrator's `TypedResultConsumer` has no dedup (it advances on every consumed result by
design, D-07), this fans out the downstream DAG twice. The window is narrow (delete-only failure after a
successful send) but it is a real at-least-once duplication path, and the second data key from the first
attempt is also leaked (never cleaned up).
**Fix:** Make the post-send delete best-effort so a delete failure does not re-drive the already-sent
completion, e.g. delete BEFORE the send, or swallow/log a delete exhaustion instead of re-throwing it (the
composite is a redundant backup at that point — a CLEANUP-style GC, not a correctness gate):
```csharp
// option A: order delete before the irreversible Send
await Guard(() => Db.StringSetAsync(L2ProjectionKeys.ExecutionData(entryId), data), ct);
await Guard(() => Db.KeyDeleteAsync(composite), ct);          // redundant backup gone first
var ep = await Send.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
await Guard(() => ep.Send(completed, ct), ct);                // last, irreversible step

// option B: keep order but make the trailing delete non-faulting (best-effort GC)
var del = await RetryLoop.ExecuteAsync(() => Db.KeyDeleteAsync(composite), RetryLimit, ct);
// do not re-throw del.Error — composite is redundant; on the next partitioned CLEANUP it is GC'd anyway
```
Note this mirrors the same hazard the ProcessorPipeline deliberately avoids by ordering UPDATE→write→CLEANUP
and treating the end-delete as a `finally` that routes to a `KeeperDelete` message rather than re-throwing.

### WR-02: The 300s in-Consume gate-wait holds the partition slot, serializing the whole replica behind one closed-gate exec

**File:** `src/Keeper/Recovery/RecoveryConsumerBase.cs:40-56`, `src/Keeper/Recovery/UpdateConsumerDefinition.cs:56-61`
**Issue:** The gate-wait blocks INSIDE `Consume` (correct per Pattern A), but `UsePartitioner` dispatches
one message per slot at a time — a `Consume` that parks for up to `GateWaitSeconds` (300s default) holds its
partition slot for that entire window. With the default `PartitionCount = 8` and no configured
`PrefetchCount`/`ConcurrentMessageLimit`, a sustained gate-closed period means at most 8 recovery messages
are in flight per replica and every other partitioned delivery queues behind them. This is the intended
back-pressure design (the gate being closed means L2 is down, so doing nothing is correct), but it is worth
confirming that `GateWaitSeconds` (300s) stays comfortably under the RabbitMQ `consumer_timeout` for the
deployed broker — the code comment claims "well under RabbitMQ's default 30-min consumer_timeout," which is
true for the stock default but NOT if an operator has lowered it. If the broker's `consumer_timeout` is ever
set below 300s, a parked recovery `Consume` is force-closed by the broker and the channel drops.
**Fix:** No code change required if the deployment keeps the default 30-min `consumer_timeout`. Recommend
adding an operator note (or a startup assertion) that `GateWaitSeconds` must remain below the broker's
`consumer_timeout`, since the two values are coupled but live in different config systems and cannot be
validated together at build time.

### WR-03: Hardcoded broker credentials in committed appsettings.json

**File:** `src/Keeper/appsettings.json:20-24`
**Issue:** `RabbitMq.Username`/`RabbitMq.Password` are committed as `guest`/`guest`. This is the standard
dev default and is consistent across all three consoles, and `cfg.Require("RabbitMq:Password")` allows env
override, so this is not a production-secret leak per se. Flagging because the file is in review scope and a
committed default credential is easy to carry into a non-dev environment unchanged.
**Fix:** Confirm production deployments override `RabbitMq:Username`/`RabbitMq:Password` via environment
variables / secret store (the `cfg.Require` fail-fast path already supports this), and consider leaving the
committed values empty or clearly dev-only to prevent accidental promotion of `guest/guest`.

## Info

### IN-01: REINJECT and INJECT use the per-message `ct` for Send while ProcessorPipeline uses CancellationToken.None

**File:** `src/Keeper/Recovery/ReinjectConsumer.cs:42`, `src/Keeper/Recovery/InjectConsumer.cs:49`
**Issue:** The recovery bodies pass `ct` to `ep.Send(..., ct)`, whereas `ProcessorPipeline.SendResult`/
`SendKeeper` deliberately pass `CancellationToken.None` so an in-flight send is not torn mid-flight by a
shutdown cancel. The recovery path's behavior is defensible (a cancelled send simply faults and re-delivers
later), but the inconsistency between the two send conventions is worth noting for a future reader who
assumes they match.
**Fix:** Optional — align on one convention. If the pipeline's `CancellationToken.None` choice is the
intended house style for "do not abort a broker send once started," apply it to the recovery sends too.

### IN-02: `Murmur3UnsafeHashGenerator` is constructed but the partition key is pre-hashed to a Guid via SHA256

**File:** `src/Keeper/Recovery/UpdateConsumerDefinition.cs:56-79`
**Issue:** The `Partitioner` is built with a `Murmur3UnsafeHashGenerator`, but the key selector returns a
SHA256-derived `Guid` (`PartitionGuid`). The effective slot is `murmur3(guid.bytes) % PartitionCount`, i.e.
two hash functions are layered. This is correct and deterministic (the documented reason is that the 8.5.5
endpoint-level overload is Guid-keyed), but the double-hash is non-obvious; the SHA256 already gives uniform
distribution, so the Murmur layer adds nothing beyond satisfying the API shape.
**Fix:** None required — behavior is correct and the rationale is documented in the XML comment. Noted only
so a future maintainer does not "simplify" by removing one hash without understanding the API constraint.

### IN-03: `OrchestratorMetrics.ResultDeduped` is declared but never incremented post-retirement

**File:** `src/Orchestrator/Observability/OrchestratorMetrics.cs:33-44`, `src/Orchestrator/Consumers/TypedResultConsumer.cs`
**Issue:** `ResultDeduped` (`orchestrator_result_deduped`) is still created on the meter, but its doc comment
points at the "`flag[H]=="Ack"` drop gate in ResultConsumer" — and the retired `ResultConsumer` plus its
effect-first dedup gate were removed (RETIRE-01). The new `TypedResultConsumer<T>` is dedup-free by design,
so this counter is now wired-but-never-incremented dead instrumentation. `ResultConsumed` and
`DispatchSent` are both still live.
**Fix:** Either remove `ResultDeduped` (and its `AddMeter` exposure remains unaffected — only the counter
goes) or update its XML doc to record that it is intentionally retained-but-dormant pending a future dedup
feature, so an operator does not expect a series that never emits.

### IN-04: REINJECT data-presence read result is discarded (`_ = await Guard(...)`)

**File:** `src/Keeper/Recovery/ReinjectConsumer.cs:28-33`
**Issue:** The read closure returns `raw.ToString()` but the value is discarded via `_ =`; the read exists
purely as a present/absent gate (the reconstructed dispatch carries `m.Payload`, not the blob). This is
intentional and documented, but reading and materializing the full blob to a string only to throw it away is
slightly wasteful on large payloads.
**Fix:** Optional micro-optimization — use `Db.KeyExistsAsync(...)` (or check `raw.IsNullOrEmpty` without
`ToString()`) so the gate does not pull the entire value over the wire just to confirm presence:
```csharp
await Guard(async () =>
{
    if (!await Db.KeyExistsAsync(L2ProjectionKeys.ExecutionData(m.EntryId)))
        throw new RecoveryDataGoneException();
    return true;
}, ct);
```

---

_Reviewed: 2026-06-09T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
