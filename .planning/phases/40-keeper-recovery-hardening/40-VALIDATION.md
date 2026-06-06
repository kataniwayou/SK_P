---
phase: 40
slug: keeper-recovery-hardening
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-06
---

# Phase 40 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `40-RESEARCH.md` → "Validation Architecture". KHARD-01/03 are fully
> hermetic (no stack); KHARD-02's determinism proof needs the live close gate.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (`TestContext.Current.CancellationToken`) + MassTransit.Testing in-memory harness + NSubstitute |
| **Hermetic test home** | `tests/BaseApi.Tests/Keeper/` (`KeeperProbeLoopTests.cs`, `FakeRedis.cs`) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release --filter "Category!=RealStack"` |
| **Full suite + live gate** | `pwsh -File scripts/phase-39-close.ps1` (or a Phase-40 clone) |
| **Estimated runtime** | hermetic ~seconds; full close gate = 3× cadence (minutes) |

---

## Sampling Rate

- **After every task commit:** Run the hermetic quick run command above (seconds).
- **After every plan wave:** Full hermetic suite GREEN.
- **Before `/gsd-verify-work`:** Full hermetic suite green; KHARD-02 needs the close gate.
- **Phase gate:** `scripts/phase-39-close.ps1` 3× consecutive GREEN, triple-SHA net-zero, both DLQs `depth==0`, Release+Debug 0-warning.
- **Max feedback latency:** ~10 seconds (hermetic).

---

## Requirement → Observable Signal Map

| Req | Behavior | Test Type | Observable signal (proves it) | File |
|-----|----------|-----------|-------------------------------|------|
| KHARD-01 | Cap honored: exactly `Cap` reinjects then 1 park; no reinject after | unit/hermetic | Harness `Sent.Any<TInner>` count == Cap; then exactly one `Sent<Fault<T>>` to keeper-dlq; `Sent<TInner>` count stays == Cap after | NEW fact in `KeeperProbeLoopTests.cs` (or `KeeperRecoverCapTests.cs`) |
| KHARD-01 | Single idempotent park (persistent fault converges) | unit/hermetic | Driving same `H` `Cap+N` times yields exactly ONE park, not N | NEW fact |
| KHARD-01 | Counter does not leak | unit/hermetic | `skp:keeper:attempts:{H}` deleted after park (fake-counter assertion) | NEW fact |
| KHARD-02 | Deterministic `keeper-dlq depth==0` across 3× cadence | live (RealStack) | close-gate DLQ depth==0 GREEN ×3 (eliminates the lone `GATE_EXIT=1`); no late give-up park races AFTER snapshot | `KeeperRecoveryE2ETests` teardown + gate |
| KHARD-03 | No duplication; cap in exactly one place | static/build | One `KeeperRecoveryHandler`; both consumers delegate; Release build 0-warning; hermetic suite GREEN | `FaultEntryStepDispatchConsumer`, `FaultExecutionResultConsumer`, `Program.cs` |

---

## Per-Task Verification Map

> Populated by the planner — every task that touches KHARD-01/02/03 maps here with its
> `<automated>` verify command. Sampling continuity rule: no 3 consecutive tasks without an
> automated verify.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| _(planner fills)_ | | | KHARD-01/02/03 | — | N/A (internal infra) | unit / build / live | `{command}` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] **Extend `FakeRedis`** (`tests/BaseApi.Tests/Keeper/FakeRedis.cs`) to back the per-`H` counter ops (`StringIncrementAsync` + `KeyExpireAsync`, or `StringGet`/`StringSet When.NotExists`) — current double only configures Get/Set/Delete. *(Alternative: abstract the counter behind a small fakeable interface.)*
- [ ] **NEW hermetic cap fact(s)** in `KeeperProbeLoopTests.cs` (or a sibling `KeeperRecoverCapTests.cs`).
- [ ] **Update `BuildHarness`** to register `KeeperRecoveryHandler` once it exists.
- [ ] *(No framework install needed — xUnit + harness + NSubstitute all present.)*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Deterministic `keeper-dlq depth==0` across the 3× cadence | KHARD-02 | Requires the running compose stack (redis/rabbitmq/postgres/otel/ES/prometheus + orchestrator/processor/baseapi/keeper×2) and a rebuilt keeper image; cannot run hermetically | Rebuild `baseapi-service orchestrator processor-sample keeper`, then `pwsh -File scripts/phase-39-close.ps1` — expect 3× GREEN, both DLQs depth==0, triple-SHA net-zero |

*A live KHARD-01 cap test is FORBIDDEN (would flood the stack ~67 cyc/s/replica) — KHARD-01 is proven hermetically only.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references (FakeRedis counter ops, cap fact, harness registration)
- [ ] No watch-mode flags
- [ ] Feedback latency < 10s (hermetic)
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
