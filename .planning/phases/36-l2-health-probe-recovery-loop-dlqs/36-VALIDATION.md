---
phase: 36
slug: l2-health-probe-recovery-loop-dlqs
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-05
---

# Phase 36 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (`TestContext.Current.CancellationToken`) + MassTransit.Testing `ITestHarness` for hermetic harness |
| **Config file** | none Keeper-specific — tests live in `tests/BaseApi.Tests/Keeper/` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper&Category!=RealStack"` |
| **Full suite command** | `dotnet test` (hermetic, excludes RealStack) ; RealStack via `--filter Category=RealStack` against live compose |
| **Estimated runtime** | ~30–60 seconds (hermetic) ; RealStack minutes (live stack) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper&Category!=RealStack"`
- **After every plan wave:** Run full hermetic `dotnet test` (exclude RealStack) — must be green across processor + orchestrator + Keeper (the BaseConsole.Core error-transport change touches ALL three consoles)
- **Before `/gsd-verify-work`:** Full hermetic suite green + one RealStack recover/give-up pass
- **Max feedback latency:** ~60 seconds (hermetic)
- **Phase gate note:** the 3×GREEN triple-SHA RealStack close gate is **Phase 39's** responsibility, NOT Phase 36's. Phase 36's own bar is hermetic-green + a single RealStack recover-both-paths/give-up pass.

---

## Per-Task Verification Map

| Req ID | Behavior | Test Type | Automated Command | File Exists | Status |
|--------|----------|-----------|-------------------|-------------|--------|
| PROBE-01 | `Delay×Attempts` bound documented & enforced under RabbitMQ consumer_timeout | unit | `dotnet test --filter ProbeOptions_Bound` | ❌ W0 | ⬜ pending |
| PROBE-02 | probe = read + write-then-delete; success only if BOTH ops complete, else fault → loop | unit (fake `IConnectionMultiplexer`/`IDatabase`) | `dotnet test --filter Probe_RequiresReadAndWrite` | ❌ W0 | ⬜ pending |
| PROBE-03 | fail-then-succeed → re-inject verbatim inner by type | hermetic (`ITestHarness`) | `dotnet test --filter Probe_Success_Reinjects` | ❌ W0 | ⬜ pending |
| PROBE-04 | fail-to-max → park original `Fault<T>` envelope to `keeper-dlq` | hermetic | `dotnet test --filter Probe_GiveUp_ParksToDlq` | ❌ W0 | ⬜ pending |
| PROBE-05 | NO premature ack — message consumed only after loop exits | hermetic (ack-timing / consumed-after assertion) | `dotnet test --filter Probe_AcksOnlyAfterLoop` | ❌ W0 | ⬜ pending |
| DLQ-01 | Keeper's own Send/Redis infra-fault → Immediate(N) → DLQ-1 | hermetic (throwing send endpoint) | `dotnet test --filter Keeper_SendFault_RetriesToDlq1` | ❌ W0 | ⬜ pending |
| DLQ-02 / DLQ-04 | exhaustion routes to ONE `skp-dlq-1` uniformly across consoles | hermetic (in-mem harness w/ custom error transport) + RealStack | `dotnet test --filter Dlq1_Consolidated` | ❌ W0 | ⬜ pending |
| DLQ-03 | `keeper-dlq` is plain durable (NO x-message-ttl); DLQ-1 is TTL'd (7d) | hermetic (queue-arg assertion) | `dotnet test --filter Dlq_TopologyArgs` | ❌ W0 | ⬜ pending |
| PROBE-03/04 (live) | recover-both-paths (dispatch + result) + give-up against live stack | RealStack (sibling of `FaultRecoverySpikeE2ETests`) | `dotnet test --filter "Category=RealStack&FullyQualifiedName~KeeperRecovery"` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs` — hermetic probe-logic (PROBE-01..05) with a fake `IDatabase`/`IConnectionMultiplexer` (no live Redis): fail-then-succeed → re-inject; fail-to-max → park; no-premature-ack; read+write-both-or-fault.
- [ ] `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` — hermetic in-mem harness asserting exhaustion lands in the consolidated `skp-dlq-1` error transport with correct queue args (DLQ-01/02/03/04).
- [ ] `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` — RealStack sibling of `FaultRecoverySpikeE2ETests`: live recover-both-paths + give-up park to `keeper-dlq`, net-zero `skp:*` teardown, keeper container rebuilt (embedded SourceHash must match).
- [ ] Test double for `IConnectionMultiplexer`/`IDatabase` throwing `RedisConnectionException`/`RedisTimeoutException` on demand (simulate down-then-up). StackExchange.Redis interfaces are mockable.
- [ ] Update `KeeperDependencyFirewallTests` allow-list ONLY if a new ProjectReference is added (none expected — Redis rides BaseConsole.Core per D-01).

*Framework already present (xUnit v3 + MassTransit.Testing) — no install needed.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Kill Keeper mid-loop → redeliver → loop restarts (at-least-once, no lost message) | PROBE-05 | Requires killing a live container mid-await; deterministic automation is fragile. Operator runbook per D-12 (auto-approve-human-verify precedent from Phases 33–35). Authoritative live signal is Phase-39's close gate. | Start live stack; trip a fault; `docker kill` keeper while loop is mid-await; observe redelivery + loop restart in logs/ES; confirm no message loss. |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
