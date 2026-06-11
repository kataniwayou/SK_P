---
phase: 50
slug: contracts-slot-array-l2-key-reshape
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-11
---

# Phase 50 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (3.2.2) on Microsoft.Testing.Platform; NSubstitute for mocks |
| **Config file** | `tests/BaseApi.Tests/xunit.runner.json` (maxParallelThreads 6) |
| **Quick run command** | `dotnet build SK_P.sln -c Release` (0-warning gate — dominant failure mode is dangling refs/crefs) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release --filter-not-trait "Category=RealStack"` (hermetic) |
| **0-warning gate** | `dotnet build SK_P.sln -c Release` AND `dotnet build SK_P.sln -c Debug` (TreatWarningsAsErrors fatal) |
| **Estimated runtime** | build ~30-60s; hermetic suite ~tens of seconds |

> A1 (confirm at plan/exec time): exact Microsoft.Testing.Platform CLI flag for the hermetic filter — `--filter-not-trait "Category=RealStack"` is the documented repo idiom; the xUnit v3 / MTP surface differs from VSTest.

---

## Sampling Rate

- **After every task commit:** Run `dotnet build SK_P.sln -c Release` (catch the 0-warning break early — dangling refs/crefs/crefs CS1574).
- **After every plan wave:** Run hermetic `dotnet test ... --filter-not-trait "Category=RealStack"` + `dotnet build SK_P.sln -c Debug`.
- **Before `/gsd-verify-work`:** Both-config 0-warning build + full hermetic suite green.
- **Max feedback latency:** ~60 seconds (build).

---

## Per-Task Verification Map

> Filled per plan during execution. The 4 success criteria map to these test behaviors:

| SC | Requirement | Behavior | Test Type | Automated Command | File Exists |
|----|-------------|----------|-----------|-------------------|-------------|
| SC-1 | RETIRE-02 | `L2ProjectionKeys.MessageIndex(messageId)` == `skp:msg:{messageId:D}` (golden pin, D-06) | unit/golden | `dotnet test ... --filter "MessageIndex"` | ❌ W0 — add to `L2ProjectionKeysTests.cs` |
| SC-1 | RETIRE-02 | `ExecutionData` no-TTL GUID data key retained, single Guid overload | unit/reflection | existing `ExecutionData_*` pins | ✅ existing |
| SC-2 | RETIRE-01 | No `CompositeBackup` builder; `KeeperUpdate`/`KeeperCleanup`/`BackupOptions` deleted; no refs survive | reflection/source-scan + compile | NEW `ModelBContractsRetiredFacts` (mirrors `ReactivePathRetiredFacts`) | ❌ W0 |
| SC-3 | RETIRE-01 | `KeeperInject` carries `EntryId`+`Data`+`DeleteEntryId`; `KeeperReinject` `EntryId`+`Payload`; `KeeperDelete` `EntryId`; all `IKeeperRecoverable` | reflection/contract | rewrite `Contracts/KeeperContractTests.cs` | ✅ rewrite |
| SC-4 | RETIRE-01/02 | Solution 0-warning Release + Debug; hermetic suite green | build + suite | both `dotnet build` configs + hermetic `dotnet test` | ✅ gate exists |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `L2ProjectionKeysTests.cs` — ADD `MessageIndex` golden pin (D-06); DELETE the `CompositeBackup` `[Fact]`.
- [ ] `Contracts/KeeperContractTests.cs` — REWRITE to 3 surviving contracts + D-08 INJECT field assertions.
- [ ] NEW `ModelBContractsRetiredFacts.cs` (or extend `ReactivePathRetiredFacts`) — reflection guard: no `CompositeBackup` method, no `KeeperUpdate`/`KeeperCleanup` type, no `BackupOptions` type (SC-2 in-phase guard; full source/reflection sweep is Phase 53/RETIRE-03).
- [ ] DELETE `UpdateConsumerFacts.cs`, `CleanupConsumerFacts.cs`, `BackupOptionsBoundTests.cs`.
- [ ] RE-POINT/REWRITE `RecoveryGateWaitFacts.cs` (KeeperCleanup→KeeperDelete vehicle), `RecoveryPartitionFacts.cs` (PartitionKey/Guid owner), `RecoveryTestKit.cs` (drop `Backup()`), `RecoveryDeadLetterFacts.cs`, `InjectConsumerFacts.cs`, `PipelinePostFacts.cs`, `SC2RecoveryPathsE2ETests.cs` (composite block).
- [ ] No framework install needed — existing test infra covers all phase requirements.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| (none) | — | All phase behaviors have automated verification (build + reflection/golden tests). | — |

*All phase behaviors have automated verification.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
