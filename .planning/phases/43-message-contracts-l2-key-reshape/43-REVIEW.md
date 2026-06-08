---
phase: 43-message-contracts-l2-key-reshape
reviewed: 2026-06-08T12:55:07Z
depth: standard
files_reviewed: 30
files_reviewed_list:
  - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
  - src/Keeper/BackupOptions.cs
  - src/Keeper/Consumers/FaultExecutionResultConsumer.cs
  - src/Keeper/Program.cs
  - src/Keeper/Recovery/KeeperRecoveryHandler.cs
  - src/Keeper/Recovery/L2ProbeRecovery.cs
  - src/Keeper/appsettings.json
  - src/Messaging.Contracts/EntryStepDispatch.cs
  - src/Messaging.Contracts/ExecutionLogScope.cs
  - src/Messaging.Contracts/IExecutionCorrelated.cs
  - src/Messaging.Contracts/IKeeperRecoverable.cs
  - src/Messaging.Contracts/IStepResult.cs
  - src/Messaging.Contracts/KeeperCleanup.cs
  - src/Messaging.Contracts/KeeperDelete.cs
  - src/Messaging.Contracts/KeeperInject.cs
  - src/Messaging.Contracts/KeeperQueues.cs
  - src/Messaging.Contracts/KeeperReinject.cs
  - src/Messaging.Contracts/KeeperUpdate.cs
  - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
  - src/Messaging.Contracts/SourceStep.cs
  - src/Messaging.Contracts/StepCancelled.cs
  - src/Messaging.Contracts/StepCompleted.cs
  - src/Messaging.Contracts/StepFailed.cs
  - src/Messaging.Contracts/StepProcessing.cs
  - src/Orchestrator/Consumers/ResultConsumer.cs
  - src/Orchestrator/Dispatch/IStepDispatcher.cs
  - src/Orchestrator/Dispatch/StepDispatcher.cs
  - src/Orchestrator/Scheduling/WorkflowFireJob.cs
findings:
  critical: 0
  warning: 3
  info: 5
  total: 8
status: issues_found
---

# Phase 43: Code Review Report

**Reviewed:** 2026-06-08T12:55:07Z
**Depth:** standard
**Files Reviewed:** 30 (27 production `src/` files + cross-referenced `FaultEntryStepDispatchConsumer`, `ProbeOptions`, `Orchestrator/Program.cs`; the 30 test files in scope were read for expected-behavior cross-reference but are not the focus — no test-only findings raised)
**Status:** issues_found

## Summary

This phase reshaped the message-contract wire vocabulary cleanly. The `Guid` seeding/defaulting is consistent across every record and call site: `EntryStepDispatch`/`StepFailed`/`StepCancelled`/`StepProcessing` hard-default `EntryId` to `Guid.Empty`, `StepCompleted` carries a freshly minted real key, the `WorkflowFireJob` entry-step fire seeds both `executionId` and `entryId` as `Guid.Empty`, and every source-step branch routes through the single `SourceStep.IsSource` predicate rather than an ad-hoc `== Guid.Empty`. The straight-through processor/orchestrator path is correct: the L2 output write is uncaught (INFRA), `SendResult` is the single Send owner, and the at-least-once duplicate is absorbed by the L1-idempotent `ResultConsumer`. No security issues found — every id, `localKey`, and exception text in the Keeper recovery body is a structured log param, never interpolated; key builders take no config-injected segment.

The findings below are concentrated in two areas the phase brief called out as risk zones: (1) the **Keeper recovery rebind off the removed `H`** — the new `localKey` derivation collapses distinct entry-step faults onto one identity, weakening the recover-attempt cap on the registered fault path; and (2) the **straight-through result path** — the orchestrator binds only `StepCompleted`, so the `StepFailed`/`StepCancelled` records the processor now actively emits land on `_skipped` with no consumer. Both are arguably in-scope-for-later (the reactive path is "dark", terminal-outcome routing is Phase 46), but each is a latent correctness gap worth recording now. The remainder are quality/consistency nits (stale version string, the `{H}` template param name, key-format-specifier drift).

## Warnings

### WR-01: Keeper recover-attempt cap and probe key collapse for distinct entry-step faults

**File:** `src/Keeper/Recovery/KeeperRecoveryHandler.cs:81-82`
**Issue:** `localKey` is derived from `CompositeBackup(CorrelationId, WorkflowId, ProcessorId, ExecutionId)`. On the *registered* production path (`FaultEntryStepDispatchConsumer` → `Fault<EntryStepDispatch>`), the inner `EntryStepDispatch.ExecutionId` is `Guid.Empty` on an entry-step fire (lineage-only, per `EntryStepDispatch.cs:15`). Two distinct entry-step dispatch faults that share `(CorrelationId, WorkflowId, ProcessorId)` but target different `StepId`s therefore produce an **identical** `localKey` — because `StepId` is deliberately excluded from the 4-tuple (D-12) and `ExecutionId` is empty. That single `localKey` keys both the probe scratch key (`KeeperProbe`) and the outer recover-attempt counter (`KeeperRecoverAttempts`), so faults for different steps share one cap budget and can prematurely park each other. On the result-path (dark) consumer this is moot, but it is live on the dispatch path.
**Fix:** This is the documented dormant rebind (T-43-10 "accepted — no auth/dedup decision depends on it here"), but the recover-attempt cap *is* a behavioral decision keyed off `localKey`. If the dispatch path is meant to be exercised before RETIRE-03, fold `StepId` into the derived identity for the cap/probe slot only (keeping it off the wire `IKeeperRecoverable` partition marker), e.g.:
```csharp
// internal probe/cap identity only — NOT the wire CompositeBackup
var localKey = $"{L2ProjectionKeys.CompositeBackup(
    inner.CorrelationId, inner.WorkflowId, inner.ProcessorId, inner.ExecutionId)}:{inner.StepId:D}";
```
Otherwise, document explicitly that the cap collapses across steps for entry-step faults (ExecutionId == Guid.Empty) and is accepted until RETIRE-03.

### WR-02: Orchestrator consumes only StepCompleted; StepFailed/StepCancelled land on `_skipped`

**File:** `src/Orchestrator/Consumers/ResultConsumer.cs:44` (and `src/Orchestrator/Program.cs:53`)
**Issue:** `ResultConsumer` binds `IConsumer<StepCompleted>` only, and it is the sole consumer registered on `queue:orchestrator-result`. The processor (`EntryStepDispatchConsumer`) now *actively* sends `StepFailed` (missing/invalid input, transform exception, output-schema failure) and `StepCancelled` (token-tripped) to that same queue via `SendResult`. With no consumer bound to those two message types on the receive endpoint, MassTransit routes them to `orchestrator-result_skipped` — silently. A workflow whose step fails or cancels produces no observable orchestrator-side reaction and no continuation; the failure is effectively swallowed at the transport layer rather than handled or logged.
**Fix:** Confirm this is the intended Phase-43 boundary (terminal-outcome routing deferred to Phase 46). If so, leave as-is but ensure a `_skipped`-queue depth alert exists so dropped terminal results are observable in the interim. If terminal outcomes should be acknowledged now, add a minimal `IConsumer<StepFailed>`/`IConsumer<StepCancelled>` (even a log-and-ack) so they are not silently skipped. Note the `EntryStepDispatchConsumer` xmldoc (lines 33-37, 138-141) describes Failed/Cancelled as delivered business outcomes that "must always reach the orchestrator" — the current binding does not satisfy that for two of the three outcome types.

### WR-03: `RunAsync` write-probe success can be a false positive when only the WRITE path is degraded

**File:** `src/Keeper/Recovery/L2ProbeRecovery.cs:38-42`
**Issue:** The probe treats READ + WRITE + DELETE completing without a `RedisException` as `Recovered`. The `KeyDeleteAsync` of the scratch key is awaited but its boolean result is discarded; if a partial/cluster condition lets the SET appear to succeed but the key is not actually durable, the loop still reports `Recovered`. This is a narrow edge and consistent with the "both ops, no exception" contract (D-02), but the discarded delete result means a silently-failed delete (no exception, returns `false`) leaves a short-TTL scratch key behind — harmless (30s TTL) but the comment claims "then delete (net-zero)" which is not guaranteed.
**Fix:** Low priority. If strict net-zero matters, assert/log when `KeyDeleteAsync` returns `false`. Otherwise update the comment to reflect that the delete is best-effort and the 30s TTL is the actual net-zero guarantee (which it already is — making this mostly a doc-accuracy nit; left at Warning only because the discarded result is easy to misread as guaranteed).

## Info

### IN-01: `{H}` structured log-param name is stale after the H field removal

**File:** `src/Keeper/Recovery/KeeperRecoveryHandler.cs:94`
**Issue:** The intake log template `"...for H={H}..."` binds the derived `localKey` value to a structured param named `H`. The whole point of D-14 is that the wire `H` is gone; surfacing the value under an Elasticsearch attribute named `H` is misleading (it is now a corr:wf:proc:exec composite, not a content hash) and inconsistent with the rename throughout the rest of the file.
**Fix:** Rename the template hole to reflect the new identity, e.g. `"...for LocalKey={LocalKey}..."`, and rename the param at line 95 accordingly. Pure observability-clarity change.

### IN-02: `appsettings.json` Service.Version still "3.7.0" despite v4.0.0 wire break

**File:** `src/Keeper/appsettings.json:11`
**Issue:** Every contract xmldoc in this phase states the wire types were removed/replaced "in v4.0.0" (e.g. `EntryStepDispatch.cs:8`, `IExecutionCorrelated.cs:9`, `L2ProjectionKeys.cs:24`). The Keeper service version remains `"3.7.0"`. The reported `service.version` will under-report the breaking wire change.
**Fix:** Bump to `"4.0.0"` to match the documented breaking contract change (and check the sibling Orchestrator/Processor appsettings for the same drift).

### IN-03: `h` parameter name in `L2ProbeRecovery.RunAsync` and key builders is stale

**File:** `src/Keeper/Recovery/L2ProbeRecovery.cs:25`, `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:50,53`
**Issue:** `RunAsync(Guid entryId, string h, ...)` and `KeeperProbe(string h)` / `KeeperRecoverAttempts(string h)` still name the identity argument `h`, a holdover from the removed content-hash. The value passed is now `localKey` (a composite key string). The name no longer describes the data.
**Fix:** Rename the parameter to `localKey` (or `identityKey`) across the call chain for consistency with the caller's variable. Cosmetic.

### IN-04: GUID format-specifier drift in `L2ProjectionKeys.Step` / `Processor`

**File:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:36,38`
**Issue:** `Root`, `ExecutionData`, and `CompositeBackup` use the explicit `:D` format specifier; `Step` (`$"{Prefix}{workflowId}:{stepId}"`) and `Processor` (`$"{Prefix}{processorId}"`) use bare interpolation. The class xmldoc (lines 11-17) emphasizes a single canonical shape and calls out that `Root` "makes this explicit with `:D`". Bare interpolation produces the same string today (default `Guid.ToString()` is "D"), so this is not a bug — but the inconsistency invites a future divergence if the default ever changes or someone reads it as intentional.
**Fix:** Add `:D` to `Step` and `Processor` for uniformity with the rest of the builders. (Pre-existing; not introduced by this phase's diff to this file, but adjacent to the reshaped `ExecutionData`/`CompositeBackup`.)

### IN-05: Empty-result-list silently halts a workflow branch

**File:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:142-181`
**Issue:** When `processor.ExecuteAsync` returns an empty `results` list, the `foreach` body never executes, no `Step*` record is sent, and the dispatch is acked. The xmldoc (lines 139-141) documents this as intended ("the orchestrator simply observes no continuation"). Combined with WR-02, this means a step can terminate a workflow branch with zero observable signal on the result queue. Flagging for awareness, not as a defect — it is a documented design choice.
**Fix:** None required if intended. Consider emitting a `StepCompleted` with an empty/sentinel `EntryId` or a `StepProcessing`/diagnostic so an empty-result branch is at least observable, once terminal-outcome routing (Phase 46) exists.

---

_Reviewed: 2026-06-08T12:55:07Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
