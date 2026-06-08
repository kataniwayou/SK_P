---
phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume
verified: 2026-06-09T00:00:00Z
status: passed
score: 6/6 must-haves verified
overrides_applied: 0
---

# Phase 46: Keeper 5-State Recovery + Orchestrator Per-Item Consume Verification Report

**Phase Goal:** The Keeper recovery consumer — partitioned by `corr:wf:ProcessorId:executionId` so each exec's messages process in order — applies the five states gate-open-only (UPDATE writes the composite backup w/ crash-backstop TTL; REINJECT re-injects a reconstructed dispatch or terminates if data is gone; INJECT reconstructs a Completed result to the orchestrator then deletes the composite copy; DELETE reclaims the data key; CLEANUP deletes the redundant composite copy on the happy path) — and the orchestrator advances workflow steps off per-item result messages with no manifest fan-out, a Keeper-INJECT'd completion being indistinguishable from a direct one.
**Verified:** 2026-06-09T00:00:00Z
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths (ROADMAP Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Recovery consumer partitioned by `corr:wf:ProcessorId:executionId` (per-key ordering; UPDATE precedes that exec's CLEANUP/INJECT; different execs parallel) | VERIFIED | `UpdateConsumerDefinition.cs` lines 56-61: single shared `Partitioner(PartitionCount, Murmur3UnsafeHashGenerator)` with five `UsePartitioner<T>` calls keyed on `PartitionGuid(p.Message)`, where `PartitionKey` = `$"{m.CorrelationId:D}:{m.WorkflowId:D}:{m.ProcessorId:D}:{m.ExecutionId:D}"` (StepId excluded). `RecoveryPartitionFacts` pins same-4-tuple-different-StepId → same key; different ExecutionId → different key. |
| 2 | UPDATE writes validatedData to the composite key with configurable TTL (default 2 days), only while the gate is open | VERIFIED | `UpdateConsumer.cs` line 25: `Db.StringSetAsync(CompositeBackup(4-tuple), m.ValidatedData, expiry: TimeSpan.FromDays(TtlDays))`. `BackupOptions.TtlDays = 2` (appsettings.json). Gate-wait enforced by `RecoveryConsumerBase.Consume` via `WaitForOpenAsync`. `UpdateConsumerFacts` asserts the TTL-bearing `Expiration` overload. |
| 3 | REINJECT reads L2[entryId]: present → re-injects reconstructed EntryStepDispatch to queue:{ProcessorId}; absent/empty → read fails → retry → terminal give-up | VERIFIED | `ReinjectConsumer.cs` lines 28-42: `StringGetAsync(ExecutionData(m.EntryId))` inside `Guard` (RetryLoop); if `raw.IsNullOrEmpty` throws `RecoveryDataGoneException` (terminal). Present → `EntryStepDispatch(m.WorkflowId, m.StepId, m.ProcessorId, m.Payload)` sent to `queue:{m.ProcessorId:D}`. `KeeperReinject.Payload` added (Plan 01, `KeeperReinject.cs` line 12). `ProcessorPipeline.BuildReinject` stamps `Payload = d.Payload` (line 203). `ReinjectConsumerFacts` tests both paths. |
| 4 | INJECT reads composite, generates new entryId, writes L2[entryId] (no TTL), injects reconstructed Completed result to orchestrator result queue, AND deletes the composite; DELETE deletes L2[entryId]; CLEANUP deletes the redundant composite on the happy path | VERIFIED | `InjectConsumer.cs` lines 32-51: strict order `StringGetAsync(composite)` → `NewId.NextGuid()` → `StringSetAsync(ExecutionData(entryId), data)` (no expiry argument) → `ep.Send(StepCompleted)` to `queue:orchestrator-result` → `KeyDeleteAsync(composite)`. `InjectConsumerFacts` asserts `Received.InOrder` for all four ops and the no-TTL write. `DeleteConsumer.cs` line 21: `KeyDeleteAsync(ExecutionData(m.EntryId))`. `CleanupConsumer.cs` lines 21-23: `KeyDeleteAsync(CompositeBackup(4-tuple))`. |
| 5 | After happy-path or recovery completion the composite copy is gone (deleted by CLEANUP/INJECT, not left to TTL) | VERIFIED | CLEANUP (`CleanupConsumer.cs`) deletes `CompositeBackup(4-tuple)` on the happy path. INJECT (`InjectConsumer.cs` line 51) deletes composite as its final step. Both consume `KeeperCleanup`/`KeeperInject` with the explicit `KeyDeleteAsync` call rather than relying on the UPDATE TTL expiry. TTL is described in both plans and the CONTEXT as "crash-backstop only." |
| 6 | Orchestrator consumes per-item result messages (no manifest fan-out) and advances steps; a Keeper-INJECT'd Completed is processed identically to a direct processor completion (same entryId + executionId) | VERIFIED | `TypedResultConsumer.cs`: generic base with `protected abstract StepOutcome Outcome`; body iterates `advancement.SelectNext(Outcome, completed, wf.Steps)` and calls `dispatcher.DispatchAsync` per match — no manifest, no fan-out. `StepCompletedConsumer.cs` implements `Outcome => StepOutcome.Completed`. `ResultConsumer.cs` + `ResultConsumerDefinition.cs` confirmed deleted. `TypedResultConsumerFacts.Injected_StepCompleted_indistinguishable_from_direct` asserts record value-equality of direct vs injected `StepCompleted` and identical `DispatchAsync` effects. |

**Score:** 6/6 truths verified

---

### Required Artifacts

| Artifact | Status | Details |
|----------|--------|---------|
| `src/BaseConsole.Core/Resilience/RetryLoop.cs` | VERIFIED | Exists; `namespace BaseConsole.Core.Resilience`; contains `RetryLoop` + `RetryOutcome<T>` |
| `src/BaseProcessor.Core/Resilience/RetryLoop.cs` | VERIFIED (deleted) | Confirmed absent — successfully relocated |
| `src/Messaging.Contracts/KeeperReinject.cs` | VERIFIED | Contains `public string Payload { get; init; } = "";` (line 12) |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` | VERIFIED | Line 203: `Payload = d.Payload` in `BuildReinject`; dual `using` for BaseConsole + BaseProcessor Resilience |
| `src/Keeper/RecoveryOptions.cs` | VERIFIED | `PartitionCount = 8`, `GateWaitSeconds = 300`; namespace `Keeper` |
| `src/Keeper/Recovery/RecoveryDataGoneException.cs` | VERIFIED | `sealed class RecoveryDataGoneException : Exception` |
| `src/Keeper/Recovery/RecoveryConsumerBase.cs` | VERIFIED | 84 lines; `WaitForOpenAsync`, `CancelAfter`, `abstract Task HandleAsync`, `RecoveryGateTimeoutException`; injects `IConnectionMultiplexer` + `ISendEndpointProvider` |
| `src/Keeper/Recovery/UpdateConsumer.cs` | VERIFIED | `StringSetAsync(CompositeBackup(4-tuple), m.ValidatedData, expiry: TimeSpan.FromDays(TtlDays))` |
| `src/Keeper/Recovery/ReinjectConsumer.cs` | VERIFIED | Reads `ExecutionData(m.EntryId)`; throws `RecoveryDataGoneException` on absent; sends `EntryStepDispatch` with `m.Payload` to `queue:{m.ProcessorId:D}` |
| `src/Keeper/Recovery/InjectConsumer.cs` | VERIFIED | read→write(no TTL)→send StepCompleted to `orchestrator-result`→delete composite; strict order |
| `src/Keeper/Recovery/DeleteConsumer.cs` | VERIFIED | `KeyDeleteAsync(ExecutionData(m.EntryId))` |
| `src/Keeper/Recovery/CleanupConsumer.cs` | VERIFIED | `KeyDeleteAsync(CompositeBackup(4-tuple))` |
| `src/Keeper/Recovery/UpdateConsumerDefinition.cs` | VERIFIED | Single-owner: `UseMessageRetry` + five `UsePartitioner<T>` calls sharing one `Partitioner`; `PartitionKey`/`PartitionGuid` public static helpers; `EndpointName = KeeperQueues.Recovery` |
| `src/Keeper/Recovery/ReinjectConsumerDefinition.cs` | VERIFIED | `EndpointName = KeeperQueues.Recovery`; `ConfigureConsumer` intentional no-op |
| `src/Keeper/Recovery/InjectConsumerDefinition.cs` | VERIFIED | `EndpointName = KeeperQueues.Recovery`; `ConfigureConsumer` intentional no-op |
| `src/Keeper/Recovery/DeleteConsumerDefinition.cs` | VERIFIED | `EndpointName = KeeperQueues.Recovery`; `ConfigureConsumer` intentional no-op |
| `src/Keeper/Recovery/CleanupConsumerDefinition.cs` | VERIFIED | `EndpointName = KeeperQueues.Recovery`; `ConfigureConsumer` intentional no-op |
| `src/Keeper/Program.cs` | VERIFIED | `Configure<RecoveryOptions>(GetSection("Recovery"))` at line 37; five `AddConsumer<Recovery.*Consumer, Recovery.*ConsumerDefinition>()` at lines 79-83; `FaultEntryStepDispatchConsumer` retained |
| `src/Keeper/appsettings.json` | VERIFIED | `"Recovery": { "PartitionCount": 8, "GateWaitSeconds": 300 }` |
| `src/Orchestrator/Consumers/TypedResultConsumer.cs` | VERIFIED | `protected abstract StepOutcome Outcome`; `SelectNext(Outcome, completed, wf.Steps)` at line 82; no status if/switch |
| `src/Orchestrator/Consumers/StepCompletedConsumer.cs` | VERIFIED | `sealed`; `Outcome => StepOutcome.Completed` |
| `src/Orchestrator/Consumers/StepFailedConsumer.cs` | VERIFIED | `sealed`; `Outcome => StepOutcome.Failed` |
| `src/Orchestrator/Consumers/StepCancelledConsumer.cs` | VERIFIED | `sealed`; `Outcome => StepOutcome.Cancelled` |
| `src/Orchestrator/Consumers/StepProcessingConsumer.cs` | VERIFIED | `sealed`; `Outcome => StepOutcome.Processing` |
| `src/Orchestrator/Consumers/StepCompletedConsumerDefinition.cs` | VERIFIED | `EndpointName = OrchestratorQueues.Result`; owns `UseMessageRetry` |
| `src/Orchestrator/Consumers/StepFailedConsumerDefinition.cs` | VERIFIED | `EndpointName = OrchestratorQueues.Result`; intentional no-op |
| `src/Orchestrator/Consumers/StepCancelledConsumerDefinition.cs` | VERIFIED | `EndpointName = OrchestratorQueues.Result`; intentional no-op |
| `src/Orchestrator/Consumers/StepProcessingConsumerDefinition.cs` | VERIFIED | `EndpointName = OrchestratorQueues.Result`; intentional no-op |
| `src/Orchestrator/Consumers/ResultConsumer.cs` | VERIFIED (deleted) | Confirmed absent — replaced by TypedResultConsumer family |
| `src/Orchestrator/Consumers/ResultConsumerDefinition.cs` | VERIFIED (deleted) | Confirmed absent |
| `src/Orchestrator/Program.cs` | VERIFIED | Four `AddConsumer<Step*Consumer, Step*ConsumerDefinition>()` at lines 66-69; no `AddConsumer<ResultConsumer` |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `ProcessorPipeline.cs` | `BaseConsole.Core.Resilience.RetryLoop` | `using BaseConsole.Core.Resilience` | WIRED | Line 1: `using BaseConsole.Core.Resilience;   // D-05: RetryLoop / RetryOutcome relocated here` |
| `ProcessorPipeline.BuildReinject` | `KeeperReinject.Payload` | `Payload = d.Payload` | WIRED | Line 203: `new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId, Payload = d.Payload }` |
| `UpdateConsumer` | L2 composite with TTL | `StringSetAsync(CompositeBackup(...), m.ValidatedData, expiry: TimeSpan.FromDays(TtlDays))` | WIRED | Line 25 |
| `ReinjectConsumer` | `queue:{ProcessorId}` | `GetSendEndpoint(new Uri($"queue:{m.ProcessorId:D}"))` | WIRED | Line 41 |
| `InjectConsumer` | `queue:orchestrator-result` | `GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"))` | WIRED | Line 48 |
| `RecoveryConsumerBase` | `IL2HealthGate` | `await gate.WaitForOpenAsync(cts.Token)` | WIRED | Line 46 |
| `UpdateConsumerDefinition` | Shared `Partitioner` 4-tuple key | `UsePartitioner<T>` × 5 on single instance | WIRED | Lines 56-61; key = `corr:wf:ProcessorId:executionId`, StepId excluded |
| `Keeper/Program.cs` | `RecoveryOptions` | `Configure<RecoveryOptions>(GetSection("Recovery"))` | WIRED | Line 37 |
| `TypedResultConsumer` | `StepAdvancement.SelectNext` | `advancement.SelectNext(Outcome, completed, wf.Steps)` | WIRED | Line 82 |
| `Orchestrator/Program.cs` | Four typed consumers | `AddConsumer<Step*Consumer, Step*ConsumerDefinition>()` | WIRED | Lines 66-69 |

---

### Locked-Decision Compliance

| Decision | Requirement | Verified |
|----------|-------------|---------|
| Partition key = 4-tuple excluding StepId | D-12 / KEEP-09 | `PartitionKey` = `$"{corr:D}:{wf:D}:{proc:D}:{exec:D}"` — no StepId; `RecoveryPartitionFacts` pins this |
| Single `UsePartitioner`-owning definition (others no-op) | Pitfalls 1 & 4 | `UsePartitioner`/`UseMessageRetry` only in `UpdateConsumerDefinition.ConfigureConsumer`; grep confirms four sibling definitions have empty/no-op overrides |
| Gate-wait throws transient marker, not broker-requeue | D-03 LOCKED | `RecoveryConsumerBase` catches `OperationCanceledException` when linked CTS fires (not inbound token) and throws `RecoveryGateTimeoutException`; `RecoveryGateWaitFacts` asserts this |
| No per-consumer `ConfigureError`/`SetQueueArgument` | Pitfall 3 | Grep over `src/Keeper/Recovery` finds only doc-comment references; no live code calls |
| INJECT order: write→send→delete | KEEP-06 | `InjectConsumer` lines 40, 49, 51 are in that order; `InjectConsumerFacts` asserts `Received.InOrder` |
| UPDATE has TTL; INJECT data key has none | KEEP-04 / KEEP-06 | `UpdateConsumer`: `expiry: TimeSpan.FromDays(TtlDays)`. `InjectConsumer` line 40: `StringSetAsync(ExecutionData(entryId), data)` — no expiry argument |
| No status if/switch in typed consumers | D-07 | Grep for `if`/`switch` in `Step*.cs` files finds only doc-comment mentions; single `if` in base is the L1 existence guard (not a status branch) |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `UpdateConsumer.HandleAsync` | `m.ValidatedData` | KeeperUpdate message field, populated upstream by processor Phase-44 pipeline | Yes — field on the consumed message | FLOWING |
| `ReinjectConsumer.HandleAsync` | L2 read result | `Db.StringGetAsync(ExecutionData(m.EntryId))` — Redis key written by processor Pre-process pipeline | Yes — reads actual L2 key | FLOWING |
| `InjectConsumer.HandleAsync` | `data` (composite read) | `Db.StringGetAsync(CompositeBackup(4-tuple))` — Redis key written by UPDATE consumer | Yes — reads actual L2 key | FLOWING |
| `InjectConsumer.HandleAsync` | `entryId` | `NewId.NextGuid()` | Yes — deterministic ID generation | FLOWING |
| `TypedResultConsumer.Consume` | `wf`, `completed`, successors | `IWorkflowL1Store.TryGet` + `wf.Steps.TryGetValue` | Yes — in-memory L1 populated at hydration | FLOWING |

---

### Behavioral Spot-Checks

Step 7b: SKIPPED for the running-server-dependent checks (RabbitMQ/Redis not available). Static code-structure checks confirmed above. Reported test results from SUMMARYs (18/18 Phase=46 trait run after Plan 03) corroborate green status; the notes confirm the only 2 failures in a bare `dotnet test` run are the pre-existing environment-dependent E2E tests (`SampleRoundTripE2ETests`, `MetricsRoundTripE2ETests`) that are out of scope per the verification notes.

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|-------------|-------------|--------|---------|
| KEEP-04 | Plans 02, 03 | UPDATE writes validated data to L2 composite with configurable TTL | SATISFIED | `UpdateConsumer.HandleAsync`; `UpdateConsumerFacts` green |
| KEEP-05 | Plans 02, 03 | REINJECT reads L2[entryId]; present → re-inject; absent → terminal | SATISFIED | `ReinjectConsumer.HandleAsync`; `ReinjectConsumerFacts` green |
| KEEP-06 | Plans 02, 03 | INJECT: read composite → write L2[entryId] (no TTL) → inject StepCompleted → delete composite | SATISFIED | `InjectConsumer.HandleAsync` strict-order; `InjectConsumerFacts` `Received.InOrder` green |
| KEEP-07 | Plans 02, 03 | DELETE deletes L2[entryId] | SATISFIED | `DeleteConsumer.HandleAsync`; `DeleteConsumerFacts` green |
| KEEP-08 | Plans 02, 03 | CLEANUP deletes redundant composite L2 copy | SATISFIED | `CleanupConsumer.HandleAsync`; `CleanupConsumerFacts` green |
| KEEP-09 | Plans 03 | Recovery consumer partitioned by 4-tuple; UPDATE before that exec's CLEANUP/INJECT | SATISFIED | `UpdateConsumerDefinition` single-owner partitioner; `RecoveryPartitionFacts` green; `RecoveryDeadLetterFacts` green |
| ORCH-01 | Plan 04 | Orchestrator advances per-item results; Keeper-INJECT'd completion indistinguishable | SATISFIED | `TypedResultConsumer` family; `TypedResultConsumerFacts.Injected_StepCompleted_indistinguishable_from_direct` green; `ResultConsumer.cs` deleted |

All 7 Phase-46 requirements satisfied. No orphaned requirements found (REQUIREMENTS.md maps exactly KEEP-04..09 + ORCH-01 to Phase 46).

**Note on requirements marked Pending in REQUIREMENTS.md:** The traceability table still shows KEEP-04 through ORCH-01 as "Pending" — this is a documentation state in the requirements file that was not updated as part of Phase 46 (the file notes these are pending implementation). The implementation is verified complete in the codebase. Updating the requirements file status from Pending to Complete is a housekeeping item for the user.

---

### Anti-Patterns Found

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| None found | — | — | — |

Checks run:
- `catch (Exception)` in `src/Keeper/Recovery/`: only doc-comment reference in `L2ProbeRecovery.cs` (a pre-existing file, not Phase-46 code). Phase-46 consumer bodies have no `catch(Exception)`.
- Status `if`/`switch` in `src/Orchestrator/Consumers/Step*.cs`: only doc-comment mentions.
- `ConfigureError`/`SetQueueArgument` in `src/Keeper/Recovery/`: only doc-comment references.
- Multiple `UsePartitioner`/`UseMessageRetry` owners: single owner confirmed.
- TODO/FIXME/placeholder in the new files: none found.
- `return null` / empty stub patterns in consumer bodies: none — all bodies contain real L2/Send operations.

---

### Human Verification Required

None. All must-haves are verifiable programmatically from the source. The strict cross-delivery serialization ordering proof (UPDATE-before-CLEANUP under the live partitioner across concurrent in-flight messages) and the literal `skp-dlq-1` queue routing are deferred to Phase-49 TEST-01 per REQUIREMENTS.md and the plans' own VALIDATION notes — these are not Phase-46 gaps, they are explicitly out-of-scope for the hermetic unit tests.

---

## Gaps Summary

No gaps. All 6 ROADMAP success criteria verified. All 7 requirement IDs satisfied. All locked-decision compliance items confirmed. Zero blocker anti-patterns found.

---

_Verified: 2026-06-09T00:00:00Z_
_Verifier: Claude (gsd-verifier)_
