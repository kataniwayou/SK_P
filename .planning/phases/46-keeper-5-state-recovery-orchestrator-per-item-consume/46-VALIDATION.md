---
phase: 46
slug: keeper-5-state-recovery-orchestrator-per-item-consume
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-08
validated: 2026-06-09
---

# Phase 46 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 + NSubstitute 5.3.0 [VERIFIED: Directory.Packages.props:120-121] |
| **Config file** | none — tests live in `tests/BaseApi.Tests/` (solution-standard) |
| **Runner** | **Microsoft.Testing.Platform** (xunit.v3) — `dotnet test --filter` is IGNORED by this runner; use `dotnet run --project tests/BaseApi.Tests` with `--filter-trait` / `--filter-method` for scoped runs |
| **Quick run command** | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=46"` |
| **Full suite command** | `dotnet run --project tests/BaseApi.Tests -c Debug` (or `dotnet build SK_P.sln` for build-only) |
| **Estimated runtime** | ~30–60 seconds (unit + harness; no real Redis/RabbitMQ) |

Established seams (Phase 43/44): contract pins via reflection (`KeeperContractTests`), helper facts driving the unit directly (`RetryLoopFacts`), consumer facts with substituted `IDatabase`/`ISendEndpointProvider` (`RecoveryTestKit`, `DispatchTestKit`), and MassTransit `InMemoryTestHarness` for consumer + partitioner/dead-letter behavior. NSubstitute `Received.InOrder` is the codebase's documented ordering-proof technique.

> **Note:** a bare full `dotnet run`/`dotnet test` shows 2 PRE-EXISTING failures (`SampleRoundTripE2ETests`, `MetricsRoundTripE2ETests`) that require a live RabbitMQ/docker broker — they are environment-dependent, not Phase-46 regressions.

---

## Sampling Rate

- **After every task commit:** `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=46"`
- **After every plan wave:** full unit/harness suite `dotnet run --project tests/BaseApi.Tests -c Debug` (catches the D-01 contract ripple and the D-05 relocation breaking other consumers)
- **Before `/gsd-verify-work`:** `dotnet build SK_P.sln` 0/0 + Phase=46 trait green
- **Max feedback latency:** ~60 seconds

*Real-stack E2E of all recovery paths is TEST-01 (Phase 49) — out of scope here.*

---

## Per-Task Verification Map

| Requirement | Plan | Wave | Secure Behavior | Test Type | Test File | Automated Command | Status |
|-------------|------|------|-----------------|-----------|-----------|-------------------|--------|
| KEEP-04 | 46-02 | 1 | UPDATE writes composite w/ TTL only while gate open | unit | `Keeper/UpdateConsumerFacts.cs` | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*UpdateConsumer*"` | ✅ green |
| KEEP-05 | 46-02 | 1 | REINJECT present→Send w/ Payload; absent/empty→`RecoveryDataGoneException`→`skp-dlq-1` | unit | `Keeper/ReinjectConsumerFacts.cs` | `... --filter-method "*ReinjectConsumer*"` | ✅ green (2) |
| KEEP-06 | 46-02 | 1 | INJECT read→new entryId→write(no TTL)→Send→delete composite, in order; trailing delete best-effort (WR-01) | unit | `Keeper/InjectConsumerFacts.cs` | `... --filter-method "*InjectConsumer*"` | ✅ green (2) |
| KEEP-07 | 46-02 | 1 | DELETE deletes `L2[entryId]` | unit | `Keeper/DeleteConsumerFacts.cs` | `... --filter-method "*DeleteConsumer*"` | ✅ green |
| KEEP-08 | 46-02 | 1 | CLEANUP deletes composite | unit | `Keeper/CleanupConsumerFacts.cs` | `... --filter-method "*CleanupConsumer*"` | ✅ green |
| KEEP-09 (partition) | 46-03 | 2 | Endpoint partitioned; key = 4-tuple, excludes StepId; same-exec serializes | unit (harness) | `Keeper/RecoveryPartitionFacts.cs` | `... --filter-method "*Partition*"` | ✅ green (2) |
| KEEP-09 / D-03 (gate-wait) | 46-02 | 1 | Gate-closed→bounded wait; gate-open→proceeds; bound→transient `RecoveryGateTimeoutException`→retry→`skp-dlq-1` | unit (fake gate) | `Keeper/RecoveryGateWaitFacts.cs` | `... --filter-method "*GateWait*"` | ✅ green (2) |
| ORCH-01 | 46-04 | 1 | Each typed consumer advances via `SelectNext(Outcome)`; INJECT'd==direct `StepCompleted` (indistinguishability) | unit | `Orchestrator/TypedResultConsumerFacts.cs` | `... --filter-method "*TypedResultConsumer*"` | ✅ green (7 cases) |
| D-01 | 46-01 | 0 | `KeeperReinject` carries `Payload`; `BuildReinject` stamps it | contract + unit | `Contracts/KeeperContractTests.cs` | `... --filter-method "*KeeperContractTests*"` | ✅ green |
| D-04 (dead-letter) | 46-03 | 2 | L2/send exhaustion + data-gone marker → message reaches `skp-dlq-1` | integration (harness) | `Keeper/RecoveryDeadLetterFacts.cs` | `... --filter-method "*RecoveryDeadLetter*"` | ✅ green |
| D-05 | 46-01 | 0 | `RetryLoop` relocated to `BaseConsole.Core`; `RetryLoopFacts` green after `using` update | unit | `Processor/RetryLoopFacts.cs` | `... --filter-method "*RetryLoopFacts*"` | ✅ green (5) |

*Status: ✅ green · ❌ red · ⚠️ flaky. All paths relative to `tests/BaseApi.Tests/`. Phase=46 trait run: **all passed, 0 failed, 0 skipped** (2026-06-09).*

---

## Wave 0 Requirements

- [x] `tests/BaseApi.Tests/Keeper/UpdateConsumerFacts.cs` — KEEP-04
- [x] `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs` — KEEP-05 (present + data-gone)
- [x] `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` — KEEP-06 (ordered ops via `Received.InOrder` + best-effort-delete fact)
- [x] `tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs` — KEEP-07
- [x] `tests/BaseApi.Tests/Keeper/CleanupConsumerFacts.cs` — KEEP-08
- [x] `tests/BaseApi.Tests/Keeper/RecoveryGateWaitFacts.cs` — KEEP-09/D-03 (fake gate + bound)
- [x] `tests/BaseApi.Tests/Keeper/RecoveryPartitionFacts.cs` — KEEP-09 (key fn + harness ordering)
- [x] `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs` — D-04 (dead-letter routing)
- [x] `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs` — ORCH-01 (four subclasses + indistinguishability)
- [x] Extended `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs` — D-01 `Payload` pin
- [x] Updated `using` in `tests/BaseApi.Tests/Processor/RetryLoopFacts.cs` — D-05 relocation
- [x] Shared Keeper consumer test fixture `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs` (substituted `IDatabase`/`ISendEndpoint`/fake `IL2HealthGate`)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Net-zero composite invariant across a real multi-item run (SC-5) | KEEP-08 | True end-to-end scan needs a live Redis + full pipeline | Deferred to Phase 49 close-gate (TEST-01); unit/harness assert per-state delete, and `RecoveryPartitionFacts` asserts the UPDATE-before-CLEANUP/INJECT partition ordering that guarantees the invariant |

*All other phase behaviors have automated verification at the unit/harness level.*

---

## Validation Sign-Off

- [x] All requirements have an automated verify (unit/harness)
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (all stubs turned green)
- [x] No watch-mode flags
- [x] Feedback latency < 60s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved 2026-06-09

---

## Validation Audit 2026-06-09

| Metric | Count |
|--------|-------|
| Requirements audited | 11 (KEEP-04..09, ORCH-01, D-01, D-04, D-05) |
| COVERED (automated, green) | 11 |
| PARTIAL | 0 |
| MISSING (automatable gaps) | 0 |
| Manual-only (deferred to Phase 49 TEST-01) | 1 (SC-5 live net-zero) |

**Result:** Phase 46 is **Nyquist-compliant**. Every phase requirement has an automated unit/harness test that exists, targets the behavior, and runs green (Phase=46 trait + `KeeperContractTests` + `RetryLoopFacts` all 0-failed, 2026-06-09). No auditor agent was required — gap analysis found no automatable gaps. The single Manual-Only item (SC-5 net-zero composite invariant across a live multi-item run) is correctly deferred to the Phase-49 real-stack close gate (TEST-01), not a Phase-46 gap. Recorded test commands were corrected from `dotnet test --filter` (ignored by Microsoft.Testing.Platform) to the working `dotnet run … --filter-trait/--filter-method` form.
