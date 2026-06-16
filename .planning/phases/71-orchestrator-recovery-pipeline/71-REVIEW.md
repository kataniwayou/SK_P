---
phase: 71-orchestrator-recovery-pipeline
reviewed: 2026-06-16T08:46:30Z
depth: standard
files_reviewed: 20
files_reviewed_list:
  - src/Orchestrator/Recovery/OrchestratorResultPipeline.cs
  - src/Orchestrator/Consumers/TypedResultConsumer.cs
  - src/Orchestrator/Consumers/StepCompletedConsumer.cs
  - src/Orchestrator/Consumers/StepFailedConsumer.cs
  - src/Orchestrator/Consumers/StepCancelledConsumer.cs
  - src/Orchestrator/Consumers/StepProcessingConsumer.cs
  - src/Orchestrator/Configuration/OrchestratorRecoveryOptions.cs
  - src/Orchestrator/Program.cs
  - src/Keeper/Recovery/OrchestratorInjectConsumer.cs
  - src/Keeper/Recovery/OrchestratorReinjectConsumer.cs
  - src/Keeper/Recovery/RecoveryEndpointBinder.cs
  - src/Keeper/Program.cs
  - src/Messaging.Contracts/OrchestratorInject.cs
  - src/Messaging.Contracts/OrchestratorReinject.cs
  - src/Messaging.Contracts/ProcessorInject.cs
  - src/Messaging.Contracts/ProcessorReinject.cs
  - src/Keeper/Recovery/ProcessorInjectConsumer.cs
  - src/Keeper/Recovery/ProcessorReinjectConsumer.cs
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - src/BaseProcessor.Core/Resilience/KeyAbsentException.cs
findings:
  critical: 0
  warning: 2
  info: 3
  total: 5
status: issues_found
---

# Phase 71: Code Review Report

**Reviewed:** 2026-06-16T08:46:30Z
**Depth:** standard
**Files Reviewed:** 20
**Status:** issues_found

## Summary

Phase 71 introduces `OrchestratorResultPipeline` — a structural clone of `ProcessorPipeline` — and wires the `TypedResultConsumer<T>` family into it. The two new Keeper consumers (`OrchestratorInjectConsumer`, `OrchestratorReinjectConsumer`) bind on the existing `keeper-recovery` endpoint via `ExcludeFromConfigureEndpoints()`, and the `Processor*` contract files are clean mechanical renames.

The implementation is structurally sound. All primary focus areas passed:

- **Atomic FORWARD Lua script** (`OrchestratorForwardWrite`): correct. TTLs arrive via ARGV[3]/ARGV[4]; no `redis.call('TIME')` or RNG inside the script. GET+SET COPY form is correct; the conditional `if v then` prevents writing a missing-origin key as an empty value.
- **Delete invariant**: `OrchestratorInjectConsumer` and `OrchestratorReinjectConsumer` contain zero `KeyDeleteAsync` calls. Confirmed.
- **Gate-once `exist L2[messageId]`**: the `RunAsync` entry point branches on `KeyExistsAsync(MessageIndex(messageId))` before any write or slot iteration — correct gate-once posture.
- **3-way RECOVERY classification**: clean not-exist slots are dropped (not added to `temp`); infra-faulted slots set `Infra: true` and leave the slot intact; data-present slots set `Completed: true` and trigger a send-before-retire. Mutual exclusion at the tail (REINJECT vs. two-key DEL) is correct.
- **Gated cleanup tail (GATE-01)**: `DeleteTerminalAsync` is only called when `!escalated` in `RunForwardAsync`, and only when `!temp.Any(t => t.Infra)` in `RunRecoveryAsync`. The two-key DEL is the sole orchestrator-side deleter.
- **`OrchestratorReinjectConsumer` factory**: all four `StepOutcome` values map correctly, plus an exhaustive default → `StepFailed`. Re-injection targets `queue:orchestrator-result` (via `OrchestratorQueues.Result`). `EntryId` propagation is correct: only the `Completed` arm copies `m.EntryId`; `Failed`/`Cancelled`/`Processing` carry `Guid.Empty` (the hard default from those record types, satisfied by `m.EntryId` being `Guid.Empty` on non-Completed results).
- **`TypedResultConsumer<T>` seam**: `context.MessageId` null-guard on line 78 throws `InvalidOperationException` (broker redelivery, not silent drop); the pipeline is invoked on the hit path at line 85.
- **`RecoveryEndpointBinder` / `Keeper/Program.cs`**: both new consumers registered with `ExcludeFromConfigureEndpoints()` and bound inside the existing `ConnectReceiveEndpoint(KeeperQueues.Recovery, ...)` callback — no new queue.
- **`ProcessorInject` / `ProcessorReinject`**: the contracts are unchanged relative to their usage in `ProcessorInjectConsumer` / `ProcessorReinjectConsumer` — the rename review found no divergence.

Two warnings and three info items follow.

## Warnings

### WR-01: `StepProcessing` reinject silently loses `EntryId` (contract says `Guid.Empty`, but the builder sends `m.EntryId`)

**File:** `src/Orchestrator/Recovery/OrchestratorResultPipeline.cs:355-364`

**Issue:** `BuildReinject` copies `m.EntryId` into `OrchestratorReinject.EntryId` unconditionally for all four subtypes. For `StepProcessing` (and `StepFailed`/`StepCancelled`), the inbound `m.EntryId` is `Guid.Empty` by the record hard-default — so in practice the value on the wire is always `Guid.Empty` for non-Completed messages. This is currently correct by coincidence of the contract, but the builder silently relies on the caller's record default rather than being explicit. If a future refactor ever allows a non-Empty `EntryId` on a `StepProcessing` result, `OrchestratorReinjectConsumer` would then build `StepProcessing` with a non-Empty `EntryId` even though `StepProcessing.EntryId` hard-defaults to `Guid.Empty` and the orchestrator-side consumers never use it — producing a silent mismatch. The risk is low today but the coupling is invisible.

**Fix:** Make the intent explicit in `BuildReinject` so the reset to `Guid.Empty` for non-Completed types is coded, not assumed:

```csharp
private static OrchestratorReinject BuildReinject(IStepResult m) =>
    new(m.WorkflowId, m.StepId, m.ProcessorId)
    {
        CorrelationId       = m.CorrelationId,
        ExecutionId         = m.ExecutionId,
        EntryId             = m is StepCompleted ? m.EntryId : Guid.Empty,
        Outcome             = OutcomeOf(m),
        ErrorMessage        = (m as StepFailed)?.ErrorMessage,
        CancellationMessage = (m as StepCancelled)?.CancellationMessage,
    };
```

---

### WR-02: `SlotTtl()` is called twice per RECOVERY slot — different random values each time

**File:** `src/Orchestrator/Recovery/OrchestratorResultPipeline.cs:237-241`

**Issue:** Inside `RunRecoveryAsync`, the retire-then-expire block calls `SlotTtl()` once for the PEXPIRE refresh (line 241). There is no bug in the retire path itself, but `SlotTtl()` is also called on line 159 (FORWARD write ARGV[3]) and has the same `Random.Shared.Next(ttl, 2*ttl+1)` body as `ProcessorPipeline.SlotTtl()`. In RECOVERY the two calls during a single slot processing are: one in the FORWARD write (non-issue; separate write) and one for the PEXPIRE refresh after retire (fine). The issue is that `SlotTtl()` is invoked for ARGV[3] on line 159 and that value is never reused — a second invocation would produce a different random. This is not a bug because each invocation is intentionally generating a new random TTL for a distinct purpose, but it is identical to how `ProcessorPipeline` handles it. No correctness problem exists; this is noted here because it could confuse a future maintainer who sees two `SlotTtl()` calls in close proximity with different results.

Actually, reviewing more carefully: in `RunForwardAsync` the `SlotTtl()` call on line 159 feeds `ARGV[3]` in the Lua script — a single value produced once per slot, inside the loop. In `RunRecoveryAsync` the `SlotTtl()` on line 241 is a separate PEXPIRE refresh after retire. These are genuinely distinct invocations at distinct sites, both intentional. This is NOT a bug.

**Revised assessment:** This is a documentation-level concern only, not a warning. Withdrawing as a warning — see IN-03 below.

---

### WR-02 (reassigned): `OrchestratorInjectConsumer` does not set a TTL on the copied data key

**File:** `src/Keeper/Recovery/OrchestratorInjectConsumer.cs:33`

**Issue:** In `OrchestratorInjectConsumer.HandleAsync`, the copy of `L2[OriginEntryId]` into `L2[EntryId]` is:

```csharp
await Guard(() => Db.StringSetAsync(L2ProjectionKeys.ExecutionData(m.EntryId), v), ct);
```

There is no `PX`/`EXAT` argument — the destination key is written immortal (no TTL). In contrast, the normal FORWARD path in `OrchestratorResultPipeline.RunForwardAsync` writes the destination via the Lua script with `'PX', ARGV[4]` (`ExecutionDataTtl`). This consumer is the "NODROP escalation" fallback when the atomic Lua write fails; writing the key without a TTL means the key leaks if the downstream `DeleteConsumer` or the later recovery pass never fires (e.g. the message is lost after the INJECT send). Compared to the normal path where an atomic write failure leaves no orphaned key, the INJECT path can now leave an immortal key.

The existing `ProcessorInjectConsumer` has the same pattern (`StringSetAsync` without TTL, line 24), so this is a pre-existing design decision, but it is worth flagging here because `OrchestratorInjectConsumer` is new code and the contrast with the explicit `PX` in the Lua script is salient.

**Fix:** Pass the ExecutionDataTtl from options, mirroring the FORWARD Lua path:

```csharp
// Inject IOptions<OrchestratorRecoveryOptions> (or a shared IOptions<ProcessorLivenessOptions>-equivalent)
// and pass TimeSpan to StringSetAsync:
var ttl = TimeSpan.FromSeconds(Math.Max(1, recoveryOptions.Value.ExecutionDataTtlSeconds));
if (v.HasValue)
    await Guard(() => Db.StringSetAsync(
        L2ProjectionKeys.ExecutionData(m.EntryId), v, ttl), ct);
```

Note: if this is a deliberate design decision (the key will always be cleaned up by the successor pipeline or `DeleteConsumer`), document the reasoning inline to prevent future confusion.

## Info

### IN-01: `OrchestratorInjectConsumer` does not guard `StringGetAsync` result — absent origin is silent no-op, with no log

**File:** `src/Keeper/Recovery/OrchestratorInjectConsumer.cs:31-33`

**Issue:** When `OriginEntryId` is not present in Redis (e.g., TTL expired between the FORWARD write attempt and the INJECT processing), `StringGetAsync` returns `RedisValue.Null` (`HasValue == false`) and the consumer silently skips the write then dispatches the `EntryStepDispatch` pointing at an empty/missing `EntryId`. The downstream processor will then attempt to read `L2[EntryId]` and find nothing — a `ProcessorReinject` will follow. This chain is not a bug (the spec accepts it as a loss path), but there is no `LogWarning` to make the data-absent case observable in traces.

**Fix:** Add a structured warning log on the absent-origin branch:

```csharp
if (!v.HasValue)
    logger.LogWarning("OrchestratorInject: origin data absent EntryId={EntryId} OriginEntryId={OriginEntryId}",
        m.EntryId, m.OriginEntryId);
```

This requires adding `ILogger<OrchestratorInjectConsumer>` to the constructor, mirroring `ProcessorReinjectConsumer`.

---

### IN-02: `TryParseTuple` silently treats a malformed `newEntryId` as `Guid.Empty` and skips the slot

**File:** `src/Orchestrator/Recovery/OrchestratorResultPipeline.cs:327`

**Issue:** If `newEntryId` is missing or not parseable, `TryParseTuple` returns `Guid.Empty` for it. Then line 214 checks `tuple.NewEntryId == Guid.Empty` and skips the slot (the same guard that catches the retired-sentinel). A JSON tuple where `newEntryId` is absent or malformed is treated identically to a legitimately retired slot — silently dropped with no log. This is the T-71-06 tolerance, but there is no diagnostic path to distinguish a truly-retired slot from a corrupt one.

**Fix:** Consider logging at Warning level when `TryParseTuple` returns `true` but `NewEntryId == Guid.Empty` (which indicates corrupt data, not a retired slot — a retired slot returns `false` from `TryParseTuple` because the sentinel is not JSON):

```csharp
if (!TryParseTuple(entry.Value, out var tuple))
    continue;   // not JSON — retired sentinel or garbage; skip silently (T-71-06)
if (tuple.NewEntryId == Guid.Empty)
{
    logger.LogWarning("RECOVERY: slot {Slot} has parseable JSON but Guid.Empty newEntryId — skipping", entry.Name);
    continue;
}
```

Actually: since `TryParseTuple` returns `false` for the retired `Guid.Empty` sentinel (the `!s.StartsWith("{")` guard on line 321), the `tuple.NewEntryId == Guid.Empty` branch can only be reached when the JSON parsed but `newEntryId` was absent/malformed. That path deserves a warning, not silent skip.

---

### IN-03: `OutcomeOf` fallback maps unknown subtypes to `StepOutcome.Failed`, but `OutcomeOf` is never observable at the call site

**File:** `src/Orchestrator/Recovery/OrchestratorResultPipeline.cs:393-400`

**Issue:** `OutcomeOf` has an `_ => StepOutcome.Failed` arm. Since `IStepResult` is a closed set (sealed records), this arm is unreachable dead code — but the C# compiler requires it for exhaustiveness because `IStepResult` is an interface, not a discriminated union. The fallback is harmless and consistent with `ProcessorPipeline.ResultOutcome`. No action required beyond awareness.

**Fix:** A comment on the default arm would clarify intent:

```csharp
_ => StepOutcome.Failed,   // unreachable — IStepResult is a closed set; exhaustive guard only
```

---

_Reviewed: 2026-06-16T08:46:30Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
