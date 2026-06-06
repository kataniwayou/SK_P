---
phase: 40
slug: keeper-recovery-hardening
status: planned
nyquist_compliant: true
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
| **Hermetic test home** | `tests/BaseApi.Tests/Keeper/` (`KeeperProbeLoopTests.cs`, `KeeperRecoverCapTests.cs`, `FakeRedis.cs`) |
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
| KHARD-01 | Cap honored: exactly `Cap` reinjects then 1 park; no reinject after | unit/hermetic | Harness `Sent<TInner>` count == Cap; then exactly one `Sent<Fault<T>>` to keeper-dlq; `Sent<TInner>` count stays == Cap after | `KeeperRecoverCapTests.cs` |
| KHARD-01 | Single idempotent park (persistent fault converges) | unit/hermetic | Driving same `H` `Cap+N` times yields exactly ONE park, not N | `KeeperRecoverCapTests.cs` |
| KHARD-01 | Counter does not leak | unit/hermetic | `skp:keeper:attempts:{H}` deleted after park (`fake.CounterKeyExists == false`) | `KeeperRecoverCapTests.cs` |
| KHARD-02 | Deterministic `keeper-dlq depth==0` across 3× cadence | live (RealStack) | close-gate DLQ depth==0 GREEN ×3 (eliminates the lone `GATE_EXIT=1`); no late give-up park races AFTER snapshot | `KeeperRecoveryE2ETests` teardown + gate |
| KHARD-03 | No duplication; cap in exactly one place | static/build | One `KeeperRecoveryHandler`; both consumers delegate; Release build 0-warning; hermetic suite GREEN | `FaultEntryStepDispatchConsumer`, `FaultExecutionResultConsumer`, `Program.cs` |

---

## Per-Task Verification Map

> Populated by the planner — every task that touches KHARD-01/02/03 maps here with its
> `<automated>` verify command. Sampling continuity rule: no 3 consecutive tasks without an
> automated verify.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 40-01-T1 | 01 | 1 | KHARD-03 | T-40-01 | Generic bound carries H (no untyped access) | build | `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Release` | ✅ | ⬜ pending |
| 40-01-T2 | 01 | 1 | KHARD-03 | T-40-01,03 | One shared body; retry seam untouched | build | `dotnet build src/Keeper/Keeper.csproj -c Release` | ✅ | ⬜ pending |
| 40-01-T3 | 01 | 1 | KHARD-03 | T-40-01 | Behavior-preserving extraction (existing facts GREEN) | unit | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release --filter "FullyQualifiedName~KeeperProbeLoopTests"` | ✅ | ⬜ pending |
| 40-02-T1 | 02 | 2 | KHARD-01 | T-40-05 | Counter key TTL-bounded; keepTtl discipline (Wave-0: FakeRedis counter ops) | build | `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` | ❌ W0 → created in-task | ⬜ pending |
| 40-02-T2 | 02 | 2 | KHARD-01 | T-40-04,05,06 | Single-winner cap (n==cap+1); DEL on park; no TTL clobber | build | `dotnet build src/Keeper/Keeper.csproj -c Release` | ✅ (after T1) | ⬜ pending |
| 40-02-T3 | 02 | 2 | KHARD-01 | T-40-04,06,07 | Exactly cap reinjects then one idempotent park; counter DEL'd; hermetic-only | unit | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release --filter "FullyQualifiedName~KeeperRecoverCapTests"` | ❌ W0 → created in-task | ⬜ pending |
| 40-03-T1 | 03 | 2 | KHARD-02 | T-40-08 | Bounded poll-until-stably-empty; fail-loud on timeout | build | `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` | ✅ | ⬜ pending |
| 40-03-T2 | 03 | 2 | KHARD-02 | T-40-08,09 | Gate stays snapshot-only (no gate-side purge); fragile Task.Delay removed | build + live | `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` (live: `pwsh -File scripts/phase-39-close.ps1`) | ✅ | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*
*Sampling continuity: every task carries an `<automated>` build/unit command (no 3 consecutive un-verified tasks). The two ❌ W0 rows (40-02-T1 FakeRedis counter ops, 40-02-T3 cap fact) are self-satisfied within Plan 02: T1 extends FakeRedis BEFORE T3's cap fact runs, and T2 lands the handler cap between them.*

---

## Wave 0 Requirements

- [ ] **Update `BuildHarness`** (`KeeperProbeLoopTests.cs:108-122`) to register `KeeperRecoveryHandler` — **Plan 01, Task 3** (both fault consumers ctor-inject the handler after the extraction).
- [ ] **Extend `FakeRedis`** (`tests/BaseApi.Tests/Keeper/FakeRedis.cs`) to back the per-`H` counter ops (`StringIncrementAsync` + `KeyExpireAsync` + DEL-tracking + `CounterKeyExists` accessor) — **Plan 02, Task 1** (current double only configures Get/Set/Delete).
- [ ] **NEW hermetic cap fact(s)** in a sibling `KeeperRecoverCapTests.cs` — **Plan 02, Task 3**.
- [ ] *(No framework install needed — xUnit + harness + NSubstitute all present.)*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Deterministic `keeper-dlq depth==0` across the 3× cadence | KHARD-02 | Requires the running compose stack (redis/rabbitmq/postgres/otel/ES/prometheus + orchestrator/processor/baseapi/keeper×2) and a rebuilt keeper image; cannot run hermetically | Rebuild `baseapi-service orchestrator processor-sample keeper`, then `pwsh -File scripts/phase-39-close.ps1` — expect 3× GREEN, both DLQs depth==0, triple-SHA net-zero |

*A live KHARD-01 cap test is FORBIDDEN (would flood the stack ~67 cyc/s/replica) — KHARD-01 is proven hermetically only.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (FakeRedis counter ops, cap fact, harness registration)
- [x] No watch-mode flags
- [x] Feedback latency < 10s (hermetic)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** planned (pending execution)
