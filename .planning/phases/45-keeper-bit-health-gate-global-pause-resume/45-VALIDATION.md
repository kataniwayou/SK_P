---
phase: 45
slug: keeper-bit-health-gate-global-pause-resume
status: verified
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-08
---

# Phase 45 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from 45-RESEARCH.md §Validation Architecture.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 / Microsoft Testing Platform (MTP) — single shared `tests/BaseApi.Tests` project (has `AddMassTransitTestHarness` + NSubstitute). |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release --no-build -- --filter-namespace BaseApi.Tests.Keeper.Health` (+ `...Orchestrator.Consumers` for the consumer slice) |
| **Full hermetic command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release --no-build -- --filter-not-trait "Category=RealStack"` |
| **Estimated runtime** | ~3 min full hermetic suite (no live broker/Redis) |

---

## Sampling Rate

- **After every task commit:** Run the quick-filter `dotnet test` for the artifact touched
- **After every plan wave:** Run the full hermetic suite (must be green)
- **Before `/gsd-verify-work`:** Full hermetic suite must be green
- **Max feedback latency:** ~3 min (full hermetic suite)

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command / Class | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|---------------------------|-------------|--------|
| W0 setup | 00 | 0 | — | — | N/A | scaffold | full suite compiles; 18 stubs land RED then turn green | ✅ | ✅ green |
| KEEP-03 gate | 01 | 1 | KEEP-03 | — | Gate fail-safe CLOSED start; open/close/re-block idempotent; CT-cancel→OCE | unit | `L2HealthGateTests` (6/6) | ✅ | ✅ green |
| KEEP-01 loop | 01 | 1 | KEEP-01 | T-45-03 | `RedisException`-only catch (unhealthy, loop survives); non-Redis bug propagates; stoppingToken ends clean | unit | `BitHealthLoopTests` (`Probe_*`, `StoppingToken_Ends_Loop_Cleanly`) | ✅ | ✅ green |
| KEEP-02 edge broadcast | 01 | 1 | KEEP-02 | T-45-04 | Edge-trigger: PauseAll-once / ResumeAll-once; same-state ticks publish nothing | unit (MassTransit harness) | `BitHealthLoopTests` (`Edge_Trigger_*`, `Same_State_Ticks_Publish_Nothing`) — *not a separate `BitHealthLoopBroadcastTests`* | ✅ | ✅ green |
| KEEP-02 fan-out | 02 | 1 | KEEP-02 | — | Per-replica delivery (`InstanceId` + `Temporary=true` temp queue per replica) | integration (live broker, ≥2 replicas) | **Manual-only** — config in `src/Orchestrator/Program.cs:41-57`; in-memory harness does not model RabbitMQ temporary fan-out | ⚠️ config-only | 🔶 manual-only |
| ORCH-02 pause | 02 | 1 | ORCH-02 | — | Idempotent re-pause no-op | unit (NSubstitute `IScheduler`) | `PauseAllConsumerTests` (`Consume_Calls_Scheduler_PauseAll`, `Redelivery_Is_Idempotent_No_Op`) | ✅ | ✅ green |
| ORCH-02 resume | 02 | 1 | ORCH-02 | — | Resume only if Paused (no spurious); enumerates L1 `WorkflowIds` | unit (real Quartz RAMJobStore) | `ResumeAllConsumerTests` (`Consume_Enumerates_WorkflowIds_And_Calls_ResumeAsync_Each`, `Resume_Of_Non_Paused_Trigger_Is_Ignored`) | ✅ | ✅ green |
| ORCH-02 no-burst | 02 | 1 | ORCH-02 | T-45-07 | Native `ResumeAll()` NEVER called; fresh-from-now reschedule | unit (`IScheduler` spy) | `ResumeNoBurstTests` (`Native_ResumeAll_Is_Never_Called`, `Resume_Reschedules_Fresh_From_Now_StartAt_Ge_Now`) | ✅ | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky · 🔶 manual-only*

*Coverage: 7/8 rows automated-green; 1 row (KEEP-02 live fan-out) documented manual-only — see Manual-Only Verifications.*

---

## Observable Validation Signals (what to probe/assert + cadence)

| Signal | Behavior asserted | Test type | Cadence | Status |
|--------|-------------------|-----------|---------|--------|
| Gate state machine | CLOSED→OPEN→CLOSED; `WaitForOpenAsync` blocks until `Open()`; `WaitForOpenAsync(ct)` throws OCE on cancel | unit | deterministic — assert each transition | ✅ `L2HealthGateTests` |
| Probe survival | `RedisException`→unhealthy (loop survives); non-Redis throw propagates (not swallowed); `stoppingToken` ends loop cleanly | unit | each tick simulated | ✅ `BitHealthLoopTests` |
| Edge-trigger broadcast | drive healthy,healthy,unhealthy,unhealthy,healthy → exactly 1 PauseAll + 1 ResumeAll publish (not per-tick) | unit (MassTransit harness) | once per simulated transition sequence | ✅ `BitHealthLoopTests` |
| Fan-out receipt per replica | each replica's temp queue receives exactly one copy; endpoint config `InstanceId`+`Temporary=true`, distinct from `orchestrator-pauseresume` | integration / live | once per pause + once per resume | 🔶 manual-only (live broker + ≥2 replicas) |
| Idempotent re-pause | `PauseAll` twice → `scheduler.PauseAll()` invoked twice, second a no-op (no exception, state unchanged) | unit (fake `IScheduler`) | redelivery simulation | ✅ `PauseAllConsumerTests` |
| Skip-to-next no-burst | after resume, `ScheduleAsync` `StartAt` ≥ now (next cron occurrence); native `ResumeAll()` NEVER called (spy on `IScheduler`) | unit (fake L1 store + scheduler spy) | per resume | ✅ `ResumeNoBurstTests` |

---

## Wave 0 Requirements

> Test project (grounded by planner): single shared **`tests/BaseApi.Tests`** (xUnit v3 / MTP, already has `AddMassTransitTestHarness` + NSubstitute) — NOT new `Keeper.Tests`/`Orchestrator.Tests` projects.

- [x] `tests/BaseApi.Tests/Keeper/Health/L2HealthGateTests.cs` — KEEP-03 (CLOSED start, open/close/re-block, CT cancel) — 6/6 green
- [x] `tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs` — KEEP-01 + KEEP-02/SC#2 (probe survival, edge-trigger publish) — 6/6 green
- [x] `tests/BaseApi.Tests/Orchestrator/Consumers/PauseAllConsumerTests.cs` + `ResumeAllConsumerTests.cs` — ORCH-02/SC#4 — 2/2 + 2/2 green
- [x] `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs` — no-herd negative (no `ResumeAll()`) — 2/2 green
- [x] Fake/mock `IScheduler` (NSubstitute spy) + real Quartz RAMJobStore + scripted `IDatabase`/`ScriptedRedis` doubles present
- [x] MassTransit test harness (`AddMassTransitTestHarness`) used for the edge-trigger publish assertions

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live fan-out to N real orchestrator replicas via RabbitMQ | KEEP-02 | Requires a running broker + ≥2 replicas; the in-memory harness proves edge-trigger publish topology but not live multi-process per-replica delivery (RabbitMQ temporary fan-out queues). Config asserted by reading `src/Orchestrator/Program.cs:41-57` (`InstanceId` + `Temporary=true`). Flagged for the Phase-46 RealStack close gate. | Start ≥2 orchestrator replicas + Keeper against a live RabbitMQ; force an L2 outage (stop Redis); observe every replica logs `Global PauseAll`; restore Redis; observe every replica logs `Global ResumeAll` and no catch-up burst. |
| Live BIT-loop survival of a transient broker outage (WR-01) | KEEP-01/02 | The WR-01 resilience fix (loop survives a `bus.Publish` failure and re-broadcasts next tick) only exercises under a real broker blip mid-transition. | With the stack live, bounce RabbitMQ during an L2 health transition; confirm the Keeper BIT loop does not die and re-emits the pending PauseAll/ResumeAll on the next tick. |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or a documented manual-only carve-out
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (all scaffolded + green)
- [x] No watch-mode flags
- [x] Feedback latency acceptable (~3 min full hermetic suite)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** verified 2026-06-08 (7/8 automated-green; 1 documented manual-only — KEEP-02 live fan-out, RealStack/Phase-46-gated)

---

## Validation Audit 2026-06-08

| Metric | Count |
|--------|-------|
| Gaps found | 1 |
| Resolved (automated) | 0 |
| Escalated (manual-only) | 1 |

Audit method: State-A audit of the pre-execution draft against the implemented + GREEN test suite (full hermetic 506/506). 7 of 8 per-task rows map to existing green tests (class-name references for the edge-broadcast and fan-out rows reconciled to the real classes). The lone gap — KEEP-02 live per-replica fan-out — is inherently a live-broker/≥2-replica concern the in-memory harness cannot model; dispositioned manual-only (user-approved) and recorded for the Phase-46 RealStack close gate.
