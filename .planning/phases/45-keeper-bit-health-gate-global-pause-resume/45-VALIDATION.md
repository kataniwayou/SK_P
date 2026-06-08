---
phase: 45
slug: keeper-bit-health-gate-global-pause-resume
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-08
---

# Phase 45 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from 45-RESEARCH.md §Validation Architecture.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (repo standard — `tests/` projects). Confirm exact version in Wave 0 from `Directory.Packages.props`. |
| **Config file** | Per-project `.csproj` under `tests/` (no central runsettings observed) |
| **Quick run command** | `dotnet test --filter "FullyQualifiedName~L2HealthGate\|FullyQualifiedName~BitHealthLoop\|FullyQualifiedName~PauseAll\|FullyQualifiedName~ResumeAll"` |
| **Full suite command** | `dotnet test` (solution root) |
| **Estimated runtime** | ~30–60 seconds (unit suite; no live broker/Redis) |

---

## Sampling Rate

- **After every task commit:** Run the quick-filter `dotnet test` for the artifact touched
- **After every plan wave:** Run `dotnet test` full suite (must be green)
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** ~60 seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| W0 setup | 00 | 0 | — | — | N/A | scaffold | `dotnet test` (compiles, stubs red) | ❌ W0 | ⬜ pending |
| KEEP-03 gate | — | 1 | KEEP-03 | — | Gate fail-safe CLOSED start | unit | `dotnet test --filter FullyQualifiedName~L2HealthGateTests` | ❌ W0 | ⬜ pending |
| KEEP-01 loop | — | 1 | KEEP-01 | T-45 (probe bug→false down) | RedisException-only catch; bug propagates | unit | `dotnet test --filter FullyQualifiedName~BitHealthLoopTests` | ❌ W0 | ⬜ pending |
| KEEP-02 edge broadcast | — | 1 | KEEP-02 | — | Edge-trigger limits broadcast volume | unit | `dotnet test --filter FullyQualifiedName~BitHealthLoopBroadcastTests` | ❌ W0 | ⬜ pending |
| KEEP-02 fan-out | — | 1 | KEEP-02 | — | Per-replica delivery (Temporary=true) | integration | `dotnet test --filter FullyQualifiedName~PauseAllEndpointTests` | ❌ W0 | ⬜ pending |
| ORCH-02 pause | — | 1 | ORCH-02 | — | Idempotent re-pause no-op | unit | `dotnet test --filter FullyQualifiedName~PauseAllConsumerTests` | ❌ W0 | ⬜ pending |
| ORCH-02 resume | — | 1 | ORCH-02 | — | Resume only if Paused (no spurious) | unit | `dotnet test --filter FullyQualifiedName~ResumeAllConsumerTests` | ❌ W0 | ⬜ pending |
| ORCH-02 no-burst | — | 1 | ORCH-02 | T-45 (catch-up herd) | Native ResumeAll() NEVER called | unit | `dotnet test --filter FullyQualifiedName~ResumeNoBurstTests` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

*Plan/Wave/Task-ID columns finalized by the planner; requirement→behavior mapping below is locked from research.*

---

## Observable Validation Signals (what to probe/assert + cadence)

| Signal | Behavior asserted | Test type | Cadence |
|--------|-------------------|-----------|---------|
| Gate state machine | CLOSED→OPEN→CLOSED; `WaitForOpenAsync` blocks until `Open()`; `WaitForOpenAsync(ct)` throws OCE on cancel | unit | deterministic — assert each transition |
| Probe survival | `RedisException`→unhealthy (loop survives); non-Redis throw propagates (not swallowed); `stoppingToken` ends loop cleanly | unit | each tick simulated |
| Edge-trigger broadcast | drive healthy,healthy,unhealthy,unhealthy,healthy → exactly 1 PauseAll + 1 ResumeAll publish (not per-tick) | unit (MassTransit test harness) | once per simulated transition sequence |
| Fan-out receipt per replica | each replica's temp queue receives exactly one copy; endpoint config `InstanceId`+`Temporary=true`, distinct from `orchestrator-pauseresume` | integration / config assertion | once per pause + once per resume |
| Idempotent re-pause | `PauseAll` twice → `scheduler.PauseAll()` invoked twice, second a no-op (no exception, state unchanged) | unit (fake `IScheduler`) | redelivery simulation |
| Skip-to-next no-burst | after resume, `ScheduleAsync` `StartAt` ≥ now (next cron occurrence); native `ResumeAll()` NEVER called (spy on `IScheduler`) | unit (fake L1 store + scheduler spy) | per resume |

---

## Wave 0 Requirements

- [ ] `tests/Keeper.Tests/Health/L2HealthGateTests.cs` — stubs for KEEP-03 (CLOSED start, open/close/re-block, CT cancel)
- [ ] `tests/Keeper.Tests/Health/BitHealthLoopTests.cs` — stubs for KEEP-01 + KEEP-02/SC#2 (probe survival, edge-trigger publish)
- [ ] `tests/Orchestrator.Tests/Consumers/PauseAllConsumerTests.cs` + `ResumeAllConsumerTests.cs` — stubs for ORCH-02/SC#4
- [ ] `tests/Orchestrator.Tests/Consumers/ResumeNoBurstTests.cs` — stubs for the no-herd negative (no `ResumeAll()`)
- [ ] Confirm a fake/mock `IScheduler` + `IWorkflowL1Store` test double exists or add one (shared fixture)
- [ ] Confirm MassTransit test harness package is referenced in `Orchestrator.Tests` for fan-out/publish assertions
- [ ] If `Keeper.Tests` project does not yet exist, Wave 0 creates it mirroring existing test-project conventions

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live fan-out to N real orchestrator replicas via RabbitMQ | KEEP-02 | Requires a running broker + ≥2 replicas; unit harness proves topology but not live multi-process delivery | Start ≥2 orchestrator replicas + Keeper against a live RabbitMQ; force an L2 outage (stop Redis); observe every replica logs `Global PauseAll`; restore Redis; observe every replica logs `Global ResumeAll` and no catch-up burst |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
