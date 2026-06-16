---
phase: 70-processor-inject-cleanup
verified: 2026-06-16T07:30:00Z
status: passed
score: 7/7 must-haves verified
overrides_applied: 0
human_verification:
  - test: "Full BaseApi.Tests suite (RealStack + integration) runs green against live stack (Redis:6380, Postgres:5433, RabbitMQ:5673)"
    expected: "All tests pass (hermetic facts already confirmed green; operator close-gate confirms E2E)"
    why_human: "SC2RecoveryPathsE2ETests is [Trait(\"Category\",\"RealStack\")] and requires live docker-compose stack. The close-gate script (scripts/*close*.ps1) is the gated execution context — not runnable hermetically. Pre-existing environmental failure: 272 untagged integration tests fail on connection-refused when infra is not running; this is a pre-existing condition documented in 70-02-SUMMARY.md and is NOT a regression from this phase."
---

# Phase 70: Processor INJECT Cleanup Verification Report

**Phase Goal:** The processor keeper INJECT path becomes non-destructive — InjectConsumer writes the data key and sends the result but deletes NO key (the trailing delete L2[DeleteEntryId] is removed). The vestigial KeeperInject.DeleteEntryId field is dropped from the contract, ProcessorPipeline.BuildInject no longer supplies it, and InjectConsumerFacts + the Phase-50 golden tests move to the new shape. Establishes the uniform delete invariant: DELETE is the only keeper state that deletes keys.
**Verified:** 2026-06-16T07:30:00Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | InjectConsumer.HandleAsync performs exactly two effects — write L2[EntryId]=Data then send StepCompleted — and issues no KeyDelete* | VERIFIED | InjectConsumer.cs (38 lines): only `StringSetAsync` + `GetSendEndpoint/ep.Send`. `grep KeyDelete src/Keeper/Recovery/InjectConsumer.cs` = 0 hits. |
| 2 | KeeperInject no longer carries DeleteEntryId; the field is deleted from the contract record | VERIFIED | KeeperInject.cs: 5 properties only (CorrelationId, ExecutionId, EntryId, Data + ctor ids). `grep DeleteEntryId src/` = 0 hits. |
| 3 | ProcessorPipeline.BuildInject constructs KeeperInject without DeleteEntryId | VERIFIED | ProcessorPipeline.cs lines 430-437: initializer has CorrelationId, ExecutionId, EntryId, Data only. No DeleteEntryId present. |
| 4 | A reflection scan finds no remaining DeleteEntryId reference on the INJECT path (src/ grep returns zero code hits) | VERIFIED | `grep -rn DeleteEntryId src/` = 0 hits confirmed. Tests/ contains only the KeeperContractTests negative guard string (intentional). |
| 5 | KeeperDeleteInvariantFacts proves DELETE is the only deleting keeper state (positive + negative behavioral guards, both KeyDeleteAsync overloads, co-asserted side-effects) | VERIFIED | KeeperDeleteInvariantFacts.cs: 3 facts — DeleteConsumer_deletes_both_keys (positive RedisKey[]), InjectConsumer_never_deletes (DidNotReceive both overloads + StepCompleted co-assert), ReinjectConsumer_never_deletes (same + EntryStepDispatch co-assert). Run: 3/3 passed. |
| 6 | KeeperContractTests reflection guard asserts Assert.Null(GetProperty("DeleteEntryId")) making field re-addition build-breaking | VERIFIED | KeeperContractTests.cs line 80: `Assert.Null(typeof(KeeperInject).GetProperty("DeleteEntryId"))`. Fact `KeeperInject_carries_the_reduced_id_set_EntryId_Data` — 6/6 passed. |
| 7 | InjectConsumerFacts, PipelineForwardFacts, SC2RecoveryPathsE2ETests are updated to the reduced id-set; solution builds 0-warning in both Release and Debug | VERIFIED | InjectConsumerFacts.cs: 3/3 passed, no DeleteEntryId, no Received.InOrder, DidNotReceive belt present. PipelineForwardFacts.cs: 8/8 passed, no inj.DeleteEntryId. SC2RecoveryPathsE2ETests.cs: delete-half block fully removed (compiles; RealStack-only execution). Debug build: 0 warnings, 0 errors. Release build: 0 warnings, 0 errors. |

**Score:** 7/7 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Keeper/Recovery/InjectConsumer.cs` | Two-effect non-destructive INJECT consumer body | VERIFIED | Contains `StringSetAsync` (op 1) and `GetSendEndpoint` (op 2). No `KeyDelete*`, no `DeleteEntryId`. XML doc describes two-effect non-destructive body. |
| `src/Messaging.Contracts/KeeperInject.cs` | Reduced KeeperInject record (5-id base + EntryId + Data, no DeleteEntryId) | VERIFIED | Contains `public string Data` and `public Guid EntryId`. No `DeleteEntryId`. Wire-tolerance "NO [JsonPropertyName]" comment preserved. |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` | BuildInject minting KeeperInject without DeleteEntryId | VERIFIED | Contains `new KeeperInject` at line 430. Initializer has 4 fields only (CorrelationId, ExecutionId, EntryId, Data). |
| `tests/BaseApi.Tests/Keeper/KeeperDeleteInvariantFacts.cs` | KINJ-03 cross-consumer delete-invariant fact (3 facts) | VERIFIED | 133 lines, `[Trait("Phase","70")]`. Contains `DidNotReceive` (negative guard) and `Received(1)` (positive DELETE). Both `Arg.Any<RedisKey[]>()` and `Arg.Any<RedisKey>()` present. No `L2ProbeRecovery` reference. 3/3 pass. |
| `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs` | Reflection guard: EntryId+Data present, DeleteEntryId Assert.Null | VERIFIED | Contains `Assert.Null(typeof(KeeperInject).GetProperty("DeleteEntryId"))` at line 80. 6/6 pass. |
| `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` | Reshaped INJECT fact: write-then-send, DidNotReceive delete belt | VERIFIED | Contains `DidNotReceive`. No `DeleteEntryId`, no `Received.InOrder`. 5-arg `StringSetAsync` Received + captured StepCompleted. 3/3 pass. |
| `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs` | NODROP-01 fact with inj.DeleteEntryId assertion removed | VERIFIED | No `inj.DeleteEntryId` present. NODROP-01 doc updated. 8/8 pass. |
| `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` | E2E asserting INJECT writes data + sends StepCompleted and deletes nothing (compile-only) | VERIFIED | No `deleteKey`, no `deleteEntryId`, no `PollForKeyAbsentAsync(db, deleteKey`. `PollForKeyValueAsync(db, entryKey` kept. STATE 3 doc updated. Compiles (covered by 0-warning build). |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `ProcessorPipeline.cs (BuildInject)` | `KeeperInject` | `new KeeperInject(...)` initializer without DeleteEntryId | WIRED | BuildInject at line 430 constructs `new KeeperInject(d.WorkflowId, d.StepId, d.ProcessorId)` with 4-field initializer only. |
| `InjectConsumer.cs (HandleAsync)` | `queue:orchestrator-result` | `Guard(GetSendEndpoint) + Guard(ep.Send)` of StepCompleted — no KeyDeleteAsync | WIRED | Lines 35-36: `GetSendEndpoint` + `ep.Send` both wrapped in `Guard`. No `KeyDelete` anywhere in file. |
| `KeeperDeleteInvariantFacts.cs` | `InjectConsumer / ReinjectConsumer / DeleteConsumer` | Consume against RecoveryTestKit.Db() substitute, assert Received/DidNotReceive KeyDeleteAsync | WIRED | All three consumers instantiated over RecoveryTestKit.Db(). Both KeyDeleteAsync overloads asserted. 3/3 pass. |
| `KeeperContractTests.cs` | `Messaging.Contracts.KeeperInject` | reflection GetProperty negative guard | WIRED | `typeof(KeeperInject).GetProperty("DeleteEntryId")` at line 80 confirmed present. 6/6 pass. |

### Data-Flow Trace (Level 4)

Not applicable — this phase removes data flow (the destructive delete) and modifies a contract shape. The remaining two effects in InjectConsumer (StringSetAsync write + StepCompleted send) were pre-existing wired behaviors from Phase 50 and not modified by this phase.

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| KeeperDeleteInvariantFacts 3 facts pass | `dotnet test tests/BaseApi.Tests -c Debug --no-build -- --filter-method "*KeeperDeleteInvariant*"` | Failed: 0, Passed: 3 | PASS |
| KeeperContractTests 6 facts pass | `dotnet test tests/BaseApi.Tests -c Debug --no-build -- --filter-method "*KeeperContractTests*"` | Failed: 0, Passed: 6 | PASS |
| InjectConsumerFacts 3 facts pass | `dotnet test tests/BaseApi.Tests -c Debug --no-build -- --filter-method "*InjectConsumerFacts*"` | Failed: 0, Passed: 3 | PASS |
| PipelineForwardFacts 8 facts pass | `dotnet test tests/BaseApi.Tests -c Debug --no-build -- --filter-method "*PipelineForwardFacts*"` | Failed: 0, Passed: 8 | PASS |
| Debug build 0-warning | `dotnet build -c Debug` | Build succeeded, 0 Warning(s), 0 Error(s) | PASS |
| Release build 0-warning | `dotnet build -c Release` | Build succeeded, 0 Warning(s), 0 Error(s) | PASS |

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| KINJ-01 | 70-01-PLAN.md | INJECT path non-destructive: writes data key + sends result, deletes NO key | SATISFIED | InjectConsumer.cs contains no KeyDelete. InjectConsumerFacts 3/3 pass with DidNotReceive belt on both KeyDeleteAsync overloads. |
| KINJ-02 | 70-01-PLAN.md, 70-02-PLAN.md | KeeperInject.DeleteEntryId removed from contract; BuildInject no longer supplies it; InjectConsumerFacts and Phase-50 golden tests updated; 0-warning build | SATISFIED | src/ grep for DeleteEntryId = 0 hits. tests/ grep = only the Assert.Null negative guard string (intentional). KeeperContractTests reflection guard in place. 0-warning Debug + Release build confirmed. |
| KINJ-03 | 70-02-PLAN.md | DELETE is the ONLY keeper state that deletes keys — enforced by negative-guard fact | SATISFIED | KeeperDeleteInvariantFacts.cs: 3 behavioral facts, both KeyDeleteAsync overloads asserted negative for INJECT/REINJECT, positive for DELETE. 3/3 pass. |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| None | — | — | — | — |

No TODO/FIXME/placeholder patterns found in the phase-modified files. No stub patterns detected. The `DeleteEntryId` string appears in tests/ only as the KeeperContractTests `Assert.Null(typeof(KeeperInject).GetProperty("DeleteEntryId"))` negative guard and its XML doc sentence — both are intentional, not stubs.

### Human Verification Required

#### 1. Full RealStack / Integration Suite Green (Operator Close-Gate)

**Test:** Run the full `BaseApi.Tests` suite against the live docker-compose stack (Redis 6380, Postgres 5433, RabbitMQ 5673).
**Expected:** All tests pass. SC2RecoveryPathsE2ETests STATE 3 (INJECT: write L2[entryId]=Data, no source-delete assertion) should pass against the live keeper worker.
**Why human:** SC2RecoveryPathsE2ETests is `[Trait("Category","RealStack")]` and requires the live stack. The hermetic run (272+ connection-refused failures) is a pre-existing environmental condition documented in 70-02-SUMMARY.md — not a regression from this phase. The operator close-gate script (`scripts/*close*.ps1`) is the designated execution context per the Phase 55/62/68 pattern.

### Gaps Summary

No gaps. All 7 observable truths verified against the actual codebase. The sole open item is the operator-gated RealStack close-gate run, which is an environment-gated confirmation (not a code defect) and is noted in the human verification section. This is a pre-existing condition carried from previous milestones — it is NOT introduced by Phase 70.

### Phase-70 Commit Inventory

All 6 task commits confirmed in git history:

| Commit | Plan | Task | Files |
|--------|------|------|-------|
| `3b69a2e` | 70-01 | D-01: Remove source-delete from InjectConsumer | `src/Keeper/Recovery/InjectConsumer.cs` |
| `559b90e` | 70-01 | D-02: Drop DeleteEntryId from KeeperInject contract | `src/Messaging.Contracts/KeeperInject.cs` |
| `5de7b1a` | 70-01 | D-03: Stop supplying DeleteEntryId in BuildInject | `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` |
| `52c0780` | 70-02 | KINJ-03: Add KeeperDeleteInvariantFacts | `tests/BaseApi.Tests/Keeper/KeeperDeleteInvariantFacts.cs` |
| `b110f67` | 70-02 | D-07/D-06: Reshape InjectConsumerFacts + KeeperContractTests | `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs`, `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs` |
| `2292651` | 70-02 | D-08/D-09: Reshape PipelineForwardFacts + SC2 E2E | `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs`, `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` |

---

_Verified: 2026-06-16T07:30:00Z_
_Verifier: Claude (gsd-verifier)_
