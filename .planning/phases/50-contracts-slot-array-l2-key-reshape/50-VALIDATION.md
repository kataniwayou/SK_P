---
phase: 50
slug: contracts-slot-array-l2-key-reshape
status: verified
nyquist_compliant: true
wave_0_complete: true
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
| **Full suite command (hermetic)** | `dotnet tests/BaseApi.Tests/bin/Release/net8.0/BaseApi.Tests.dll --filter-not-trait "Category=RealStack"` — run the built MTP dll directly (see A1-RESOLVED) |
| **0-warning gate** | `dotnet build SK_P.sln -c Release` AND `dotnet build SK_P.sln -c Debug` (TreatWarningsAsErrors fatal) |
| **Estimated runtime** | build ~10-60s; hermetic suite ~3-4 min |

> **A1-RESOLVED (2026-06-11):** This is a **Microsoft.Testing.Platform** project, so the trait filter must be passed to the test app, NOT through `dotnet test`'s VSTest layer. Confirmed during the Phase-50 validation audit:
> - ✅ `dotnet <BaseApi.Tests.dll> --filter-not-trait "Category=RealStack"` → 505 passed / 0 failed (correct hermetic run).
> - ❌ `dotnet test ... --filter-not-trait "..."` → MSBuild `MSB1001: Unknown switch` (dotnet test does not recognize the MTP flag).
> - ❌ `dotnet test ... --filter "Category!=RealStack"` → silently **ignored** under MTP (`VSTestTestCaseFilter` warning `MTP0001`); runs ALL tests incl. the 7 RealStack E2E tests, which fail without live Redis/RabbitMQ.
> The 7 RealStack E2E tests (`SC1/SC2/SC3*E2ETests`, `*RoundTripE2ETests`, `OrchestrationLogsE2ETests`) need the live compose stack and are excluded from the hermetic gate.

---

## Sampling Rate

- **After every task commit:** Run `dotnet build SK_P.sln -c Release` (catch the 0-warning break early — dangling refs/crefs CS1574).
- **After every plan wave:** Run the hermetic dll filter run + `dotnet build SK_P.sln -c Debug`.
- **Before `/gsd-verify-work`:** Both-config 0-warning build + full hermetic suite green.
- **Max feedback latency:** ~60 seconds (build).

---

## Per-Task Verification Map

> Audited post-execution 2026-06-11. The 4 success criteria map to these test behaviors:

| SC | Requirement | Behavior | Test Type | Automated Command | Status |
|----|-------------|----------|-----------|-------------------|--------|
| SC-1 | RETIRE-02 | `L2ProjectionKeys.MessageIndex(messageId)` == `skp:msg:{messageId:D}` (golden pin, D-06) | unit/golden | hermetic dll run | ✅ green — `L2ProjectionKeysTests.cs` `MessageIndex_Produces_*` pins `skp:msg:55555555-…` |
| SC-1 | RETIRE-02 | `ExecutionData` no-TTL GUID data key retained, single Guid overload | unit/reflection | existing `ExecutionData_*` pins + `ModelBContractsRetiredFacts` Fact 4 | ✅ green |
| SC-2 | RETIRE-01 | No `CompositeBackup` builder; `KeeperUpdate`/`KeeperCleanup`/`BackupOptions` deleted; no refs survive | reflection/source-scan + compile | `ModelBContractsRetiredFacts` (4 facts) + 0-warning build | ✅ green — guard present (4 facts), 0 src refs |
| SC-3 | RETIRE-01 | `KeeperInject` carries `EntryId`+`Data`+`DeleteEntryId`; `KeeperReinject` `EntryId`+`Payload`; `KeeperDelete` `EntryId`; all `IKeeperRecoverable` | reflection/contract | hermetic dll run | ✅ green — `KeeperContractTests.cs` asserts `DeleteEntryId` (3×), no `ValidatedData` |
| SC-4 | RETIRE-01/02 | Solution 0-warning Release + Debug; hermetic suite green | build + suite | both `dotnet build` configs + hermetic dll run | ✅ green — Release 0-warn, Debug 0-warn, hermetic 505/0 |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements — COMPLETE

- [x] `L2ProjectionKeysTests.cs` — ADDed `MessageIndex` golden pin (D-06); DELETEd the `CompositeBackup` `[Fact]` (0 refs remain).
- [x] `Contracts/KeeperContractTests.cs` — REWRITTEN to 3 surviving contracts + D-08 INJECT field assertions (`DeleteEntryId` asserted, `ValidatedData` gone).
- [x] NEW `ModelBContractsRetiredFacts.cs` — reflection guard (4 facts): no `CompositeBackup` method, no `KeeperUpdate`/`KeeperCleanup` type, no `BackupOptions` type, + positive `MessageIndex`/`ExecutionData` survivor. (Full source/reflection sweep is Phase 53/RETIRE-03.)
- [x] DELETEd `UpdateConsumerFacts.cs`, `CleanupConsumerFacts.cs`, `BackupOptionsBoundTests.cs`.
- [x] RE-POINTed/REWROTE `RecoveryGateWaitFacts.cs` (KeeperCleanup→KeeperDelete vehicle), `RecoveryPartitionFacts.cs` (11 refs → `ReinjectConsumerDefinition`, 0 `UpdateConsumerDefinition`), `RecoveryTestKit.cs` (dropped `Backup()`), `RecoveryDeadLetterFacts.cs`, `InjectConsumerFacts.cs` (deleted), `PipelinePostFacts.cs`, `SC2RecoveryPathsE2ETests.cs` (composite block excised, still compiles as RealStack).
- [x] No framework install needed — existing test infra covered all phase requirements.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| (none) | — | All phase behaviors have automated verification (build + reflection/golden tests). | — |

*All phase behaviors have automated verification.*

---

## Validation Audit 2026-06-11

| Metric | Count |
|--------|-------|
| Gaps found (pre-exec Wave 0) | 8 |
| Resolved by execution | 8 |
| Escalated to manual-only | 0 |

All 5 SC rows are COVERED by automated tests; the pre-execution Wave 0 gaps were all closed during plan execution (50-01 added the MessageIndex golden pin + reflection guard; 50-02 reconciled the full test surface). Independently re-verified post-execution: Release + Debug builds 0-warning, hermetic suite 505 passed / 0 failed. No gap-filling agent run required.

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 60s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** verified 2026-06-11
