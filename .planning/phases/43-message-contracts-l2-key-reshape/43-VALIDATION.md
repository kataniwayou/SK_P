---
phase: 43
slug: message-contracts-l2-key-reshape
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-08
---

# Phase 43 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `43-RESEARCH.md` §Validation Architecture. Shape/key/predicate proofs are pure STJ + reflection — no broker, no Redis, no harness.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (`xunit.v3` 3.2.2) under Microsoft.Testing.Platform (MTP runner) |
| **Config file** | `tests/BaseApi.Tests/xunit.runner.json` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Contracts\|FullyQualifiedName~Projection\|FullyQualifiedName~SourceStep\|FullyQualifiedName~BackupOptions"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Estimated runtime** | Quick ~<30s (no I/O) · Full ~per existing suite |

---

## Sampling Rate

- **After every task commit:** Run the quick run command (Contracts/Projection/SourceStep/BackupOptions filters) — sub-30s, no I/O.
- **After every plan wave:** Run the full suite — catches the test-project compile/reshape fallout (the large bucket).
- **Before `/gsd-verify-work`:** Full suite must be green. Because Wave 3 deletes RETIRE-01/02 machinery covered by ~8 test files, "green" requires the DELETE/RESHAPE test bucket to be actioned — a partial reshape leaves the project red even though source compiles.
- **Max feedback latency:** ~30 seconds (quick) / full-suite at wave boundaries.

---

## Per-Task Verification Map

| Req / SC | Behavior | Test Type | Automated Command | File Exists |
|----------|----------|-----------|-------------------|-------------|
| SC-1 / MSG-01 | `EntryStepDispatch` carries six ids, no `H` | unit (reflection + round-trip) | `dotnet test … --filter "FullyQualifiedName~EntryStepDispatch"` | ⚠️ reshape `Orchestrator/EntryStepDispatchTests.cs` |
| SC-1 / MSG-01 | All four `Step*` records carry six ids, no `H`, `: IStepResult` | unit (reflection + round-trip) | `dotnet test … --filter "FullyQualifiedName~StepResult"` | ❌ W0 — `Contracts/StepResultContractTests.cs` |
| SC-2 / MSG-02 | `entryId` is `Guid`; `StepFailed/Cancelled/Processing` default `Guid.Empty`; `StepCompleted` carries real key | unit | `dotnet test … --filter "FullyQualifiedName~StepResult"` | ❌ W0 |
| SC-2 / MSG-02 | `SourceStep.IsSource(Guid.Empty)==true`, else false; single predicate | unit | `dotnet test … --filter "FullyQualifiedName~SourceStep"` | ❌ W0 — `Contracts/SourceStepTests.cs` |
| SC-3 / MSG-03 | Five Keeper records exist, each with id set; all `: IKeeperRecoverable` exposing the 4-tuple; `UPDATE` has `validatedData`; `REINJECT`/`DELETE` have `entryId` | unit (reflection) | `dotnet test … --filter "FullyQualifiedName~Keeper.*Contract"` | ❌ W0 — `Contracts/KeeperContractTests.cs` |
| SC-4 / MSG-03 | `ExecutionData(Guid) == "skp:data:{guid:D}"` | unit (golden) | `dotnet test … --filter "FullyQualifiedName~L2ProjectionKeys"` | ⚠️ update `Features/Orchestration/Projection/L2ProjectionKeysTests.cs` |
| SC-4 / MSG-03 | `CompositeBackup(...) == "skp:{corr}:{wf}:{proc}:{exec}"` (`:D` GUIDs, skp-prefixed) | unit (golden) | same | ❌ W0 (add to `L2ProjectionKeysTests`) |
| D-10 | `BackupOptions` defaults `TtlDays == 2`; binds from config | unit (mirror `ProbeOptionsBoundTests`) | `dotnet test … --filter "FullyQualifiedName~BackupOptions"` | ❌ W0 — `Keeper/BackupOptionsBoundTests.cs` |
| build-gate | Solution + test project compile against new shapes (straight-through) | build | `dotnet build` then full `dotnet test` | n/a (gate) |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `Contracts/StepResultContractTests.cs` — SC-1/SC-2 for the four `Step*` records (six ids, no `H`, `IStepResult`, `Guid.Empty` defaults, `ErrorMessage`/`CancellationMessage` placement)
- [ ] `Contracts/SourceStepTests.cs` — SC-2 single predicate
- [ ] `Contracts/KeeperContractTests.cs` — SC-3 (five records, id sets, `IKeeperRecoverable` 4-tuple)
- [ ] `Keeper/BackupOptionsBoundTests.cs` — D-10 (default 2, bound invariant) — mirror `ProbeOptionsBoundTests.cs`
- [ ] Extend `Features/Orchestration/Projection/L2ProjectionKeysTests.cs` — add `CompositeBackup` golden + update `ExecutionData(Guid)` expectation
- [ ] Reshape/split `Orchestrator/ExecutionResultContractTests.cs` → four `Step*` contract tests (or fold into `StepResultContractTests`)
- [ ] DELETE removed-machinery tests (`EffectFirstDedupFacts`, `ManifestFanoutFacts`, `MergeCollapseFacts`, `ResultCheckAndDropFacts`, `CheckAndDropFacts`, `IdempotentExactlyOnceE2ETests`, `HashHelperGoldenFacts`, content-addr write facts) — assert RETIRE-01/02 machinery, cannot survive
- [ ] Framework install: none — xUnit v3 already wired

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Reactive Keeper path stays DARK-but-compiling (D-14) | — (scoping invariant) | "Files still present" is a diff/structure check, not a behavioral test | Confirm `KeeperRecoveryHandler.cs`, `FaultExecutionResultConsumer.cs`, `L2ProbeRecovery.cs` still exist in the diff; confirm `dotnet build` is green; no D-row authorizes their deletion |

*All shape/key/predicate behaviors have automated verification.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 30s (quick)
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
