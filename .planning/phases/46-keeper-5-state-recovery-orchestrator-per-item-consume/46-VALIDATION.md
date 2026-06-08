---
phase: 46
slug: keeper-5-state-recovery-orchestrator-per-item-consume
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-08
---

# Phase 46 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 + NSubstitute 5.3.0 [VERIFIED: Directory.Packages.props:120-121] |
| **Config file** | none — tests live in `tests/BaseApi.Tests/` (solution-standard) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "Phase=46"` |
| **Full suite command** | `dotnet test` (solution) |
| **Estimated runtime** | ~30–60 seconds (unit + harness; no real Redis/RabbitMQ) |

Established seams (Phase 43/44): contract pins via reflection (`KeeperContractTests`), helper facts driving the unit directly (`RetryLoopFacts`), consumer facts with substituted `IDatabase`/`ISendEndpointProvider` (`DispatchTestKit`, `PipelinePreFacts`), and MassTransit `InMemoryTestHarness` for consumer + partitioner behavior. NSubstitute `Received.InOrder` is the codebase's documented ordering-proof technique.

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "Phase=46"`
- **After every plan wave:** Run `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (full Keeper+Orchestrator+contract suite — catches the D-01 contract ripple and the D-05 relocation breaking other consumers)
- **Before `/gsd-verify-work`:** Full solution `dotnet test` must be green
- **Max feedback latency:** ~60 seconds

*Real-stack E2E of all recovery paths is TEST-01 (Phase 49) — out of scope here.*

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| (TBD by planner) | — | — | KEEP-04 | — | UPDATE writes composite w/ TTL only while gate open | unit | `dotnet test --filter "FullyQualifiedName~UpdateConsumer"` | ❌ W0 | ⬜ pending |
| (TBD by planner) | — | — | KEEP-05 | poison-msg → DLQ | REINJECT present→Send w/ Payload; absent→throw RecoveryDataGone→`skp-dlq-1` | unit | `dotnet test --filter "FullyQualifiedName~ReinjectConsumer"` | ❌ W0 | ⬜ pending |
| (TBD by planner) | — | — | KEEP-06 | — | INJECT read→new entryId→write(no TTL)→Send→delete composite, in order | unit | `dotnet test --filter "FullyQualifiedName~InjectConsumer"` | ❌ W0 | ⬜ pending |
| (TBD by planner) | — | — | KEEP-07 | — | DELETE deletes `L2[entryId]` | unit | `dotnet test --filter "FullyQualifiedName~DeleteConsumer"` | ❌ W0 | ⬜ pending |
| (TBD by planner) | — | — | KEEP-08 | — | CLEANUP deletes composite | unit | `dotnet test --filter "FullyQualifiedName~CleanupConsumer"` | ❌ W0 | ⬜ pending |
| (TBD by planner) | — | — | KEEP-09 | hash collision (no correctness impact) | Endpoint partitioned; key = 4-tuple, excludes StepId; same-exec serializes | unit (harness) | `dotnet test --filter "FullyQualifiedName~Partition"` | ❌ W0 | ⬜ pending |
| (TBD by planner) | — | — | KEEP-09/D-03 | un-acked-hold bound (DoS) | Gate-closed→bounded wait; gate-open→proceeds; bound→transient retry→`skp-dlq-1` | unit (fake gate) | `dotnet test --filter "FullyQualifiedName~GateWait"` | ❌ W0 | ⬜ pending |
| (TBD by planner) | — | — | ORCH-01 | — | Each typed consumer advances via SelectNext(Outcome); INJECT'd==direct StepCompleted | unit | `dotnet test --filter "FullyQualifiedName~TypedResultConsumer"` | ❌ W0 | ⬜ pending |
| (TBD by planner) | — | — | D-01 | — | `KeeperReinject` carries `Payload`; `BuildReinject` stamps it | contract + unit | `dotnet test --filter "FullyQualifiedName~KeeperContractTests"` | ⚠️ extend | ⬜ pending |
| (TBD by planner) | — | — | D-04 | poison-msg → DLQ | L2/send exhaustion + data-gone marker → message reaches `skp-dlq-1` | integration (harness) | `dotnet test --filter "FullyQualifiedName~RecoveryDeadLetter"` | ❌ W0 | ⬜ pending |
| (TBD by planner) | — | — | D-05 | — | `RetryLoop` compiles from `BaseConsole.Core`; `RetryLoopFacts` green after `using` update | unit (existing) | `dotnet test --filter "FullyQualifiedName~RetryLoopFacts"` | ✅ exists | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky · the planner assigns concrete Task IDs/Plan/Wave columns.*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Keeper/UpdateConsumerFacts.cs` — KEEP-04
- [ ] `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs` — KEEP-05 (present + data-gone)
- [ ] `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` — KEEP-06 (ordered ops via `Received.InOrder`)
- [ ] `tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs` — KEEP-07
- [ ] `tests/BaseApi.Tests/Keeper/CleanupConsumerFacts.cs` — KEEP-08
- [ ] `tests/BaseApi.Tests/Keeper/RecoveryGateWaitFacts.cs` — KEEP-09/D-03 (fake gate + bound)
- [ ] `tests/BaseApi.Tests/Keeper/RecoveryPartitionFacts.cs` — KEEP-09 (key fn + harness ordering)
- [ ] `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs` — ORCH-01 (four subclasses + indistinguishability)
- [ ] Extend `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs` — D-01 `Payload` pin
- [ ] Update `using` in `tests/BaseApi.Tests/Processor/RetryLoopFacts.cs` — D-05 relocation
- [ ] Shared Keeper consumer test fixture (substituted `IDatabase`/`ISendEndpoint`/fake `IL2HealthGate`) — reuse `DispatchTestKit` style

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Net-zero composite invariant across a real multi-item run (SC-5) | KEEP-08 | True end-to-end scan needs a live Redis + full pipeline | Deferred to Phase 49 close-gate (TEST-01); unit/harness assert per-state delete, harness asserts UPDATE-before-CLEANUP/INJECT ordering |

*All other phase behaviors have automated verification at the unit/harness level.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
