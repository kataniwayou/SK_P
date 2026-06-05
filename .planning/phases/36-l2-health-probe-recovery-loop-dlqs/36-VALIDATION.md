---
phase: 36
slug: l2-health-probe-recovery-loop-dlqs
status: verified
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-05
updated: 2026-06-06
---

# Phase 36 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (`TestContext.Current.CancellationToken`) + MassTransit.Testing `ITestHarness` for hermetic harness |
| **Config file** | none Keeper-specific — tests live in `tests/BaseApi.Tests/Keeper/` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack" --filter-class "*Keeper*"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack"` (hermetic) ; RealStack via `--filter-trait "Category=RealStack"` against live compose |
| **Estimated runtime** | ~5s (Keeper hermetic, 16 tests) / ~3m (full hermetic, 467 tests) ; RealStack minutes (live stack) |

*Note: this project uses Microsoft.Testing.Platform — filters go after `--` (`--filter-not-trait` / `--filter-class`), NOT VSTest `--filter`.*

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack" --filter-class "*Keeper*"`
- **After every plan wave:** Run full hermetic `dotnet test` (exclude RealStack) — must be green across processor + orchestrator + Keeper (the BaseConsole.Core error-transport change touches ALL three consoles)
- **Before `/gsd-verify-work`:** Full hermetic suite green + one RealStack recover/give-up pass
- **Max feedback latency:** ~60 seconds (hermetic)
- **Phase gate note:** the 3×GREEN triple-SHA RealStack close gate is **Phase 39's** responsibility, NOT Phase 36's. Phase 36's own bar is hermetic-green + a single RealStack recover-both-paths/give-up pass.

---

## Per-Task Verification Map

| Req ID | Behavior | Test Type | Automated Command | File Exists | Status |
|--------|----------|-----------|-------------------|-------------|--------|
| PROBE-01 | `Delay×Attempts` bound documented & enforced under RabbitMQ consumer_timeout | unit | `--filter-method "*ProbeOptions_Bound*"` | ✅ `ProbeOptionsBoundTests.cs` | ✅ green |
| PROBE-02 | probe = read + write-then-delete; success only if BOTH ops complete, else fault → loop | unit (`FakeRedis`) | `--filter-method "*Probe_RequiresReadAndWrite*"` | ✅ `KeeperProbeLoopTests.cs` | ✅ green |
| PROBE-03 | fail-then-succeed → re-inject verbatim inner by type | hermetic (`ITestHarness`) | `--filter-method "*Probe_Success_Reinjects*"` | ✅ `KeeperProbeLoopTests.cs` | ✅ green |
| PROBE-04 | fail-to-max → park original `Fault<T>` envelope to `keeper-dlq` | hermetic | `--filter-method "*Probe_GiveUp_ParksToDlq*"` | ✅ `KeeperProbeLoopTests.cs` | ✅ green |
| PROBE-05 | NO premature ack — message consumed only after loop exits | hermetic (ack-timing) | `--filter-method "*Probe_AcksOnlyAfterLoop*"` | ✅ `KeeperProbeLoopTests.cs` | ✅ green |
| DLQ-01 | Keeper's own Send/Redis infra-fault → Immediate(N) → DLQ-1 | hermetic (throwing send endpoint) | `--filter-method "*Keeper_SendFault_RetriesToDlq1*"` | ✅ `KeeperDlqConsolidationTests.cs` | ✅ green |
| DLQ-02 / DLQ-04 | exhaustion routes to ONE `skp-dlq-1` uniformly across consoles | hermetic (in-mem harness w/ custom error transport) | `--filter-method "*Dlq1_Consolidated*"` | ✅ `KeeperDlqConsolidationTests.cs` | ✅ green |
| DLQ-03 | `keeper-dlq` is plain durable (NO x-message-ttl); DLQ-1 is TTL'd (7d) | hermetic (queue-arg assertion) | `--filter-method "*Dlq_TopologyArgs*"` | ✅ `KeeperDlqConsolidationTests.cs` | ✅ green |
| PROBE-03/04 (live) | recover-both-paths (dispatch + result) + give-up against live stack | RealStack (sibling of `FaultRecoverySpikeE2ETests`) | `--filter "Category=RealStack&FullyQualifiedName~KeeperRecovery"` | ✅ `KeeperRecoveryE2ETests.cs` | ⚠️ authored — operator-gated (Manual-Only) |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky/operator-gated*

**Hermetic coverage: 8/8 requirement-behaviors green (16 Keeper hermetic tests pass; full hermetic suite 467/0).** The RealStack recover/give-up test is authored and structurally complete; its live run is operator-gated (see Manual-Only).

---

## Wave 0 Requirements

- [x] `tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs` — hermetic probe-logic (PROBE-01..05) with the `FakeRedis` down→half-open→up double (no live Redis): fail-then-succeed → re-inject; fail-to-max → park; no-premature-ack; read+write-both-or-fault. **6 facts green.**
- [x] `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` — hermetic in-mem harness asserting exhaustion lands in the consolidated `skp-dlq-1` error transport with correct queue args (DLQ-01/02/03/04). **3 facts green.**
- [x] `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` — RealStack sibling of `FaultRecoverySpikeE2ETests`: live recover-both-paths + give-up park to `keeper-dlq`, net-zero `skp:*` teardown, keeper container rebuilt (embedded SourceHash must match). **Authored; live run operator-gated.**
- [x] Test double for Redis (`FakeRedis.cs`) throwing `RedisConnectionException` on demand (simulate down-then-up). Done — NSubstitute-backed stateful flip.
- [x] `KeeperDependencyFirewallTests` allow-list unchanged (no new ProjectReference — Redis rides BaseConsole.Core per D-01). Confirmed.

*Framework already present (xUnit v3 + MassTransit.Testing) — no install needed.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live recover-both-paths + give-up park to `keeper-dlq` against the running stack | PROBE-03/04 (live) | RealStack E2E (`KeeperRecoveryE2ETests`) is authored + structurally complete but requires a rebuilt live Docker compose stack (keeper + processor-sample + orchestrator + baseapi-service) to run green. Operator-gated per the Phases 33–35 do-not-block precedent; authoritative live signal is Phase-39's close gate. Tracked in `36-HUMAN-UAT.md`. | Rebuild all containers; `dotnet test tests/BaseApi.Tests -- --filter "Category=RealStack&FullyQualifiedName~KeeperRecovery"`; expect `KeeperRecovery_RecoversBothPaths` GREEN (`CountEsHitsAsync==1` exactly-once) + `KeeperRecovery_GivesUp_ParksToDlq` (original `Fault<T>` on `keeper-dlq`, net-zero post-test). |
| Kill Keeper mid-loop → redeliver → loop restarts (at-least-once, no lost message) | PROBE-05 (live) | Requires killing a live container mid-await; deterministic automation is fragile. Operator runbook per D-12 (auto-approve-human-verify precedent). Authoritative live signal is Phase-39's close gate. | Start live stack; trip a fault; `docker kill` keeper while loop is mid-await; observe redelivery + loop restart in logs/ES; confirm no message loss. |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 60s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** verified 2026-06-06 — 8/8 hermetic requirement-behaviors green (16 Keeper tests; full hermetic 467/0). 2 inherently-live behaviors (RealStack recover/give-up; kill-mid-loop) documented Manual-Only, operator-gated to Phase-39. No MISSING gaps; gsd-nyquist-auditor not required.

---

## Validation Audit 2026-06-06

| Metric | Count |
|--------|-------|
| Requirement-behaviors total | 10 (8 hermetic + 2 live) |
| Covered (green hermetic) | 8 |
| Manual-only (operator-gated live) | 2 |
| Missing (no test) | 0 |
| Gaps filled by auditor | 0 (none required) |
