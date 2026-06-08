---
phase: 43
slug: message-contracts-l2-key-reshape
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-08
updated: 2026-06-08
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
| SC-1 / MSG-01 | `EntryStepDispatch` carries six ids, no `H` | unit (reflection + round-trip) | `dotnet test … --filter "FullyQualifiedName~EntryStepDispatch"` | ✅ `Orchestrator/EntryStepDispatchTests.cs` (reshaped 43-05) |
| SC-1 / MSG-01 | All four `Step*` records carry six ids, no `H`, `: IStepResult` | unit (reflection + round-trip) | `dotnet test … --filter "FullyQualifiedName~StepResult"` | ✅ `Contracts/StepResultContractTests.cs` (43-01) |
| SC-2 / MSG-02 | `entryId` is `Guid`; `StepFailed/Cancelled/Processing` default `Guid.Empty`; `StepCompleted` carries real key | unit | `dotnet test … --filter "FullyQualifiedName~StepResult"` | ✅ `Contracts/StepResultContractTests.cs` |
| SC-2 / MSG-02 | `SourceStep.IsSource(Guid.Empty)==true`, else false; single predicate | unit | `dotnet test … --filter "FullyQualifiedName~SourceStep"` | ✅ `Contracts/SourceStepTests.cs` (43-01) |
| SC-3 / MSG-03 | Five Keeper records exist, each with id set; all `: IKeeperRecoverable` exposing the 4-tuple; `UPDATE` has `validatedData`; `REINJECT`/`DELETE` have `entryId` | unit (reflection) | `dotnet test … --filter "FullyQualifiedName~Keeper.*Contract"` | ✅ `Contracts/KeeperContractTests.cs` (43-01) |
| SC-4 / MSG-03 | `ExecutionData(Guid) == "skp:data:{guid:D}"` | unit (golden) | `dotnet test … --filter "FullyQualifiedName~L2ProjectionKeys"` | ✅ `Features/Orchestration/Projection/L2ProjectionKeysTests.cs` (extended 43-01) |
| SC-4 / MSG-03 | `CompositeBackup(...) == "skp:{corr}:{wf}:{proc}:{exec}"` (`:D` GUIDs, skp-prefixed) | unit (golden) | same | ✅ `L2ProjectionKeysTests.cs` — CompositeBackup golden (43-01) |
| D-10 | `BackupOptions` defaults `TtlDays == 2`; binds from config | unit (mirror `ProbeOptionsBoundTests`) | `dotnet test … --filter "FullyQualifiedName~BackupOptions"` | ✅ `Keeper/BackupOptionsBoundTests.cs` (43-01) |
| RETIRE-01/02 | `H`/`flag[H]`/CAS dedup + content-addressing/manifest/N×M fan-out machinery removed | teardown (test deletion) | full `dotnet test` (machinery proofs gone) | ✅ 11 machinery test files deleted (43-01 + 43-05) |
| build-gate | Solution + test project compile against new shapes (straight-through) | build + full suite | `dotnet build SK_P.sln -c Release` then `dotnet test -- --filter-not-trait "Category=RealStack"` | ✅ green — 480 passed / 0 failed, Release 0 warnings (43-05) |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [x] `Contracts/StepResultContractTests.cs` — SC-1/SC-2 for the four `Step*` records (six ids, no `H`, `IStepResult`, `Guid.Empty` defaults, `ErrorMessage`/`CancellationMessage` placement) — created 43-01
- [x] `Contracts/SourceStepTests.cs` — SC-2 single predicate — created 43-01
- [x] `Contracts/KeeperContractTests.cs` — SC-3 (five records, id sets, `IKeeperRecoverable` 4-tuple) — created 43-01
- [x] `Keeper/BackupOptionsBoundTests.cs` — D-10 (default 2, bound invariant), mirrors `ProbeOptionsBoundTests.cs` — created 43-01
- [x] Extend `Features/Orchestration/Projection/L2ProjectionKeysTests.cs` — `CompositeBackup` golden + `ExecutionData(Guid)` expectation — extended 43-01
- [x] Reshape/split `Orchestrator/ExecutionResultContractTests.cs` → four `Step*` contract tests — folded into `StepResultContractTests.cs`; legacy file deleted (43-05)
- [x] DELETE removed-machinery tests (`EffectFirstDedupFacts`, `ManifestFanoutFacts`, `MergeCollapseFacts`, `ResultCheckAndDropFacts`, `CheckAndDropFacts`, `IdempotentExactlyOnceE2ETests`, `HashHelperGoldenFacts`, content-addr write facts) — 8 deleted in 43-01; 3 additional obsolete E2E machinery tests (`FaultRecoverySpikeE2ETests`, `KeeperFaultIntakeE2ETests`, `KeeperRecoveryE2ETests`) deleted in 43-05
- [x] Framework install: none — xUnit v3 already wired

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Reactive Keeper path stays DARK-but-compiling (D-14) | — (scoping invariant) | "Files still present" is a diff/structure check, not a behavioral test | Confirm `KeeperRecoveryHandler.cs`, `FaultExecutionResultConsumer.cs`, `L2ProbeRecovery.cs` still exist in the diff; confirm `dotnet build` is green; no D-row authorizes their deletion |

*All shape/key/predicate behaviors have automated verification.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 30s (quick)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** validated 2026-06-08 — all 4 success criteria (SC-1..SC-4) + D-10 + RETIRE-01/02 teardown carry green automated coverage; full hermetic suite 480 passed / 0 failed.

---

## Validation Audit 2026-06-08

| Metric | Count |
|--------|-------|
| Requirements / SCs audited | 9 (SC-1×2, SC-2×2, SC-3, SC-4×2, D-10, RETIRE-01/02) |
| COVERED (green automated) | 9 |
| PARTIAL | 0 |
| MISSING | 0 |
| Manual-only (documented) | 1 (D-14 dark-path diff/structure check) |
| Gaps resolved this audit | 0 (draft strategy reconciled post-execution; all Wave-0 deps satisfied) |

State-A reconciliation of the pre-execution draft strategy: all six Wave-0 test files exist on disk, all eleven RETIRE-01/02 machinery test files are deleted, and the build-gate is green (480 passed / 0 failed, Release 0 warnings — independently confirmed by the phase verifier with SC-1..SC-4 file:line evidence). No nyquist-auditor spawn required — no MISSING/PARTIAL gaps to fill. The single manual-only item (the D-14 reactive-Keeper-path-stays-dark structural check) is a diff/structure assertion, not a behavioral test, and was confirmed present-and-compiling by both the verifier and the security auditor.
