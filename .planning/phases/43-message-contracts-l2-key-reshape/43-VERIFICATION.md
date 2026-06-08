---
phase: 43-message-contracts-l2-key-reshape
verified: 2026-06-08T00:00:00Z
status: passed
score: 6/6 must-haves verified
overrides_applied: 0
---

# Phase 43: Message Contracts & L2 Key Reshape Verification Report

**Phase Goal:** The reshaped wire vocabulary exists — `EntryStepDispatch`/result contracts carry the six ids and no longer carry `H`, `entryId` is a GUID with `Guid.Empty` as the explicit source-step sentinel, the five Keeper-state contracts exist, and the two L2 key schemes (GUID data key with no TTL + composite backup key `corr:wf:proc:exec` with a configurable 2-day TTL) are defined in one place. RETIRE-01 (`H`/`flag[H]`/CAS dedup) and RETIRE-02 (content-addressing + result manifest + N×M fan-out) are coupled INTO this phase (D-01/D-02) and must be torn down.
**Verified:** 2026-06-08
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | `EntryStepDispatch` and the four `Step*` result records carry exactly the six ids and no longer carry `H`; a golden test pins the shape and asserts `H` is absent | VERIFIED | `IExecutionCorrelated.cs` declares `Guid EntryId { get; }` with no `H` member. `EntryStepDispatch.cs` carries `Guid EntryId = Guid.Empty` with no `H` property. All four `Step*` records carry the six ids. `StepResultContractTests` asserts `GetProperty("H") == null` on all four records. `EntryStepDispatchTests` asserts `GetProperty("H") == null` and `EntryId == Guid.Empty`. |
| 2 | `entryId` is a `Guid`; `Guid.Empty` is recognized as the source-step sentinel by `SourceStep.IsSource`; consumers branch off one predicate | VERIFIED | `SourceStep.cs` exists: `public static bool IsSource(Guid entryId) => entryId == Guid.Empty;`. `ExecutionLogScope.cs` uses `SourceStep.IsSource(ec.EntryId)`. `EntryStepDispatchConsumer.cs` uses `SourceStep.IsSource(dispatch.EntryId)`. `SourceStepTests` pins `IsSource(Guid.Empty) == true` and `IsSource(Guid.NewGuid()) == false`. |
| 3 | Five Keeper message contracts exist — `UPDATE`, `REINJECT`, `INJECT`, `DELETE`, `CLEANUP` — each implementing `IKeeperRecoverable`, which exposes exactly the corr/wf/proc/exec 4-tuple (no `StepId` on the marker) | VERIFIED | `IKeeperRecoverable.cs` declares exactly `CorrelationId`, `WorkflowId`, `ProcessorId`, `ExecutionId` — no `StepId`. All five `Keeper*.cs` records exist and implement `IKeeperRecoverable`. `KeeperUpdate` carries `ValidatedData`; `KeeperReinject`/`KeeperDelete` carry `EntryId`; `KeeperInject`/`KeeperCleanup` carry neither. `KeeperContractTests` fully pins all these constraints. |
| 4 | `L2ProjectionKeys` exposes `ExecutionData(Guid)` (no-TTL, sole overload) and `CompositeBackup(corr,wf,proc,exec)`; golden tests pin both key strings; `BackupOptions { TtlDays = 2 }` is bound from appsettings | VERIFIED | `L2ProjectionKeys.cs` has `ExecutionData(Guid)` only (no string overload, no `Flag(string)`). `CompositeBackup` produces `skp:{corr:D}:{wf:D}:{proc:D}:{exec:D}`. `L2ProjectionKeysTests` pins `CompositeBackup` to exact golden string and `ExecutionData(Guid.Parse("55555555-..."))` to `skp:data:55555555-...`. `BackupOptions.cs` exists with `TtlDays = 2`. `Keeper/Program.cs` binds `Configure<BackupOptions>(GetSection("Backup"))`. `appsettings.json` has `"Backup": { "TtlDays": 2 }`. `BackupOptionsBoundTests` pins default `TtlDays == 2`. |
| 5 | RETIRE-01 machinery (`H`/`flag[H]`/CAS dedup) and RETIRE-02 machinery (content-addressing, result manifest, N×M fan-out) are removed from orchestrator/processor | VERIFIED | `MessageIdentity.cs` deleted. `ExecutionResult.cs` deleted. `StepDispatcher.cs`: no `ComputeH`, no `Flag(`, no Redis dep. `WorkflowFireJob.cs`: seeds `Guid.Empty` as entryId sentinel. `ResultConsumer.cs`: `IConsumer<StepCompleted>` with no dedup gate, no manifest read, no Redis; single `SelectNext(StepOutcome.Completed)` call. `EntryStepDispatchConsumer.cs`: no `HashBlob`, no `HashManifest`, no `Flag(`, no CAS flip; uses `SourceStep.IsSource` + `ExecutionData(Guid)`. All eight RETIRE-01/02 test files deleted. Three RETIRE-01/02 E2E tests deleted. |
| 6 | The reactive Keeper recovery path remains present and COMPILING but dark (D-14) — not deleted | VERIFIED | `KeeperRecoveryHandler.cs` exists, uses `L2ProjectionKeys.CompositeBackup(...)` instead of `inner.H`. `L2ProbeRecovery.cs` exists with `RunAsync(Guid entryId, ...)` signature. `FaultEntryStepDispatchConsumer.cs` exists, bound to `Fault<EntryStepDispatch>`, registered in `Program.cs`. `FaultExecutionResultConsumer.cs` exists (retargeted to `Fault<StepCompleted>`, NOT registered). No file was deleted; diff guard satisfied. |

**Score:** 6/6 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Messaging.Contracts/IExecutionCorrelated.cs` | Six ids, no H, Guid EntryId | VERIFIED | Contains `Guid EntryId { get; }`, no `H` member |
| `src/Messaging.Contracts/EntryStepDispatch.cs` | Six ids, Guid EntryId = Guid.Empty, no H | VERIFIED | `Guid EntryId { get; init; } = Guid.Empty`, no H property |
| `src/Messaging.Contracts/IStepResult.cs` | Marker `interface IStepResult : IExecutionCorrelated` | VERIFIED | Exists; exact declaration confirmed |
| `src/Messaging.Contracts/StepCompleted.cs` | Six ids, no diagnostic field, no Guid.Empty default on EntryId | VERIFIED | Exists; correct shape |
| `src/Messaging.Contracts/StepFailed.cs` | Six ids, EntryId = Guid.Empty, ErrorMessage only | VERIFIED | Exists; no CancellationMessage |
| `src/Messaging.Contracts/StepCancelled.cs` | Six ids, EntryId = Guid.Empty, CancellationMessage only | VERIFIED | Exists; no ErrorMessage |
| `src/Messaging.Contracts/StepProcessing.cs` | Six ids, EntryId = Guid.Empty, no diagnostic field | VERIFIED | Exists; correct shape |
| `src/Messaging.Contracts/SourceStep.cs` | `IsSource(Guid entryId) => entryId == Guid.Empty` | VERIFIED | Exists; exact implementation confirmed |
| `src/Messaging.Contracts/IKeeperRecoverable.cs` | 4-tuple (no StepId) partition marker | VERIFIED | Declares exactly CorrelationId, WorkflowId, ProcessorId, ExecutionId |
| `src/Messaging.Contracts/KeeperUpdate.cs` | 5-id base + ValidatedData, no EntryId | VERIFIED | Exists; correct |
| `src/Messaging.Contracts/KeeperReinject.cs` | 5-id base + EntryId, no ValidatedData | VERIFIED | Exists; correct |
| `src/Messaging.Contracts/KeeperInject.cs` | 5-id base only | VERIFIED | Exists; correct |
| `src/Messaging.Contracts/KeeperDelete.cs` | 5-id base + EntryId, no ValidatedData | VERIFIED | Exists; correct |
| `src/Messaging.Contracts/KeeperCleanup.cs` | 5-id base only | VERIFIED | Exists; correct |
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | ExecutionData(Guid) sole overload + CompositeBackup; no Flag/ExecutionData(string) | VERIFIED | Both builders present; no string overload; no Flag |
| `src/Messaging.Contracts/KeeperQueues.cs` | Recovery const added; FaultRecovery + DeadLetter kept | VERIFIED | `Recovery = "keeper-recovery"` present alongside FaultRecovery + DeadLetter |
| `src/Keeper/BackupOptions.cs` | `TtlDays = 2` options class | VERIFIED | Exists; correct |
| `src/Keeper/Program.cs` | `Configure<BackupOptions>(GetSection("Backup"))` | VERIFIED | Line confirmed present |
| `src/Messaging.Contracts/ExecutionResult.cs` | MUST NOT EXIST (deleted) | VERIFIED | File absent |
| `src/Messaging.Contracts/Hashing/MessageIdentity.cs` | MUST NOT EXIST (deleted) | VERIFIED | File absent; Hashing/ directory absent |
| `src/Keeper/Recovery/KeeperRecoveryHandler.cs` | Present, uses CompositeBackup, no inner.H access | VERIFIED | Exists; uses `L2ProjectionKeys.CompositeBackup(...)` |
| `src/Keeper/Recovery/L2ProbeRecovery.cs` | Present, `RunAsync(Guid entryId, ...)` | VERIFIED | Exists; Guid entryId confirmed |
| `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` | Present, `IConsumer<Fault<EntryStepDispatch>>`, registered | VERIFIED | Exists; registered in Program.cs |
| `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` | Present but NOT registered (retargeted to StepCompleted) | VERIFIED | Exists; retargeted to `Fault<StepCompleted>`; not in Program.cs registrations |
| `tests/BaseApi.Tests/Contracts/StepResultContractTests.cs` | SC-1/SC-2 golden tests with H-absent + six-ids + Guid.Empty defaults + zero-GUID serialization pin | VERIFIED | All assertions present; Phase-43 trait |
| `tests/BaseApi.Tests/Contracts/SourceStepTests.cs` | SC-2 single-predicate test | VERIFIED | `IsSource(Guid.Empty)` pin present |
| `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs` | SC-3 five-record + 4-tuple marker tests | VERIFIED | All assertions present; StepId-not-on-marker pinned |
| `tests/BaseApi.Tests/Keeper/BackupOptionsBoundTests.cs` | D-10 TtlDays == 2 test | VERIFIED | `Assert.Equal(2, new BackupOptions().TtlDays)` |
| `tests/BaseApi.Tests/Orchestrator/EntryStepDispatchTests.cs` | No H, Guid EntryId assertions | VERIFIED | `GetProperty("H") == null` and `EntryId == Guid.Empty` pinned |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `ExecutionLogScope.cs` | `SourceStep.IsSource` | `BuildState` EntryId skip | WIRED | Line 32: `if (!SourceStep.IsSource(ec.EntryId)) state[EntryId] = ec.EntryId.ToString();` — no `string.IsNullOrEmpty` |
| `Keeper/Program.cs` | `BackupOptions` | `Configure<BackupOptions>(GetSection("Backup"))` | WIRED | Line 33 of Program.cs; binding confirmed |
| `StepDispatcher.cs` | `EntryStepDispatch` (no H) | `DispatchAsync` builds dispatch without H; Guid entryId param | WIRED | Signature `Guid entryId`; dispatch built with no H property; no Flag pre-write |
| `WorkflowFireJob.cs` | Source sentinel | Entry-step fire seeds `Guid.Empty` | WIRED | Line 86: `DispatchAsync(... Guid.Empty, Guid.Empty, ...)` confirmed |
| `ResultConsumer.cs` | `StepCompleted` (no dedup, no manifest, no Redis) | Straight-through result consumption | WIRED | `IConsumer<StepCompleted>`; no Redis ctor; `SelectNext(StepOutcome.Completed)` with single `DispatchAsync` |
| `EntryStepDispatchConsumer.cs` | `SourceStep.IsSource` + `ExecutionData(Guid)` | Input-read skip + Step* record emission | WIRED | `SourceStep.IsSource(dispatch.EntryId)` confirmed; `L2ProjectionKeys.ExecutionData(dispatch.EntryId)` confirmed; Step* builders confirmed |
| `KeeperRecoveryHandler.cs` | `L2ProjectionKeys.CompositeBackup` | Local key derived from corr/wf/proc/exec replaces inner.H | WIRED | `var localKey = L2ProjectionKeys.CompositeBackup(inner.CorrelationId, ...)` confirmed |
| `Keeper/Program.cs` | `FaultEntryStepDispatchConsumer` | `AddConsumer<FaultEntryStepDispatchConsumer, ...>` registration | WIRED | Sole reactive consumer registration confirmed; `FaultExecutionResultConsumer` registration absent |

---

### Requirements Coverage

| Requirement | Source Plan(s) | Description | Status | Evidence |
|-------------|---------------|-------------|--------|----------|
| MSG-01 | 43-01, 43-02, 43-03, 43-05 | `EntryStepDispatch` and result contracts carry six ids, no H | SATISFIED | IExecutionCorrelated, EntryStepDispatch, four Step* records all verified; golden tests confirm |
| MSG-02 | 43-01, 43-02, 43-03, 43-04, 43-05 | `entryId` is a GUID; `Guid.Empty` is source-step sentinel | SATISFIED | SourceStep.IsSource predicate verified; all consumer sites use it; EntryId typed as Guid throughout |
| MSG-03 | 43-01, 43-02, 43-05 | Five Keeper message contracts exist | SATISFIED | All five Keeper* records + IKeeperRecoverable verified; KeeperContractTests confirms shape |
| RETIRE-01 | 43-03, 43-05 | Remove H identity, flag[H] dedup gate, CAS flips | SATISFIED | MessageIdentity deleted; StepDispatcher has no H/flag/Redis; ResultConsumer + EntryStepDispatchConsumer have no dedup; eight RETIRE test files deleted |
| RETIRE-02 | 43-03, 43-05 | Remove content-addressed L2 data, result manifest, N×M fan-out | SATISFIED | HashBlob/HashManifest/content-addressing gone; ResultConsumer has no manifest read; EntryStepDispatchConsumer emits one Step* per result |

All five requirements satisfied. Traceability rows in `REQUIREMENTS.md` updated: RETIRE-01/02 map to `Phase 43 (coupled per D-01)`.

---

### Anti-Patterns Found

No blockers identified.

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| `KeeperRecoveryHandler.cs` | Intentionally dormant reactive path (D-14 dark) | Info | By design; retirement is RETIRE-03 (Phase 47/48). Dark path is present-and-compiling, not a stub — it carries real logic and is explicitly authorized. |
| `FaultExecutionResultConsumer.cs` | Unregistered consumer file (retained for D-14 diff guard) | Info | By design; retargeted to `Fault<StepCompleted>`, not registered. File kept so the diff does not show the reactive recovery path disappearing. |

---

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Phase-43 contract/golden tests pass | `dotnet test -- --filter-trait "Phase=43"` | 23 passed, 0 failed | PASS |
| Full hermetic suite green | `dotnet test -- --filter-not-trait "Category=RealStack"` | **480 passed, 0 failed** | PASS |
| Release build 0-warning | `dotnet build SK_P.sln -c Release` | `Build succeeded. 0 Warning(s), 0 Error(s)` | PASS |
| Messaging.Contracts isolated build | `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Release` | `Build succeeded. 0 Warning(s), 0 Error(s)` | PASS |

---

### Human Verification Required

None. All observable truths were verifiable programmatically. The full hermetic suite executed to completion with 480 passed / 0 failed.

---

## Gaps Summary

No gaps. All six success criteria are satisfied by the actual codebase:

1. The reshaped wire vocabulary (`EntryStepDispatch` + four `Step*` records carrying the six ids with no `H`) is confirmed by direct file inspection and by the 480-green hermetic suite.
2. The `entryId`-as-Guid + `SourceStep.IsSource` sentinel predicate is uniformly applied at every consumer site.
3. All five Keeper-state contracts with the correct `IKeeperRecoverable` 4-tuple partition marker are present and shape-pinned by `KeeperContractTests`.
4. `L2ProjectionKeys` is the single source of truth for both key schemes; golden tests in `L2ProjectionKeysTests` pin the exact byte strings.
5. RETIRE-01 and RETIRE-02 machinery (H/flag[H]/CAS, content-addressing/manifest/fan-out) is fully removed from both orchestrator and processor, with all associated test files deleted.
6. The reactive Keeper recovery path (D-14) remains present and compiling but dark — `KeeperRecoveryHandler`, `L2ProbeRecovery`, and `FaultEntryStepDispatchConsumer` all exist and compile; `FaultExecutionResultConsumer` is retained and retargeted (not registered), satisfying the diff guard.

---

_Verified: 2026-06-08_
_Verifier: Claude (gsd-verifier)_
