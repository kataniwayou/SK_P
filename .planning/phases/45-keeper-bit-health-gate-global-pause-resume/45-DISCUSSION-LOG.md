# Phase 45: Keeper BIT Health Gate + Global Pause/Resume - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-08
**Phase:** 45-keeper-bit-health-gate-global-pause-resume
**Areas discussed:** Orchestrator pause-all mechanism (ORCH-02), Per-workflow pause contracts (clean-break vs defer), BIT gate primitive + bounded wait (KEEP-03), Broadcast cadence + transport (KEEP-01/02)

---

## Orchestrator pause-all / resume-all mechanism (ORCH-02)

### Q1 — initial mechanism

| Option | Description | Selected |
|--------|-------------|----------|
| Reuse per-job lifecycle over L1 snapshot | PauseOnlyAsync/ResumeAsync each, per-job TriggerState guard | |
| Quartz native PauseAll() / ResumeAll() | One scheduler-wide call; simpler; no per-job guard | ✓ |
| Hybrid: PauseAll() native, per-job resume | Mixed model | |

**User's choice:** Quartz native `PauseAll()` / `ResumeAll()`.

### Q2 — misfire-on-resume (raised after the one-shot FireNow trigger model was surfaced)

User clarified intent: select skip-to-next, "disallow concurrent fires." Investigation found `[DisallowConcurrentExecution]` already on `WorkflowFireJob` (same-workflow overlap already prevented), AND that the orchestrator uses one-shot `SimpleSchedule(FireNow)` triggers (not repeating cron triggers) — so native `ResumeAll()` would fire every past-due one-shot immediately (cross-workflow herd). This created a conflict between "native ResumeAll" and "skip-to-next / no burst," which was surfaced honestly.

| Option | Description | Selected |
|--------|-------------|----------|
| Native PauseAll() + per-job fresh resume | PauseAll() native; ResumeAll iterates L1 → ResumeAsync fresh-from-now. Skip-to-next, no burst. | ✓ |
| Native PauseAll() + native ResumeAll(), accept burst | Simplest; contradicts skip-to-next intent | |
| Per-job for both pause and resume | Fully symmetric; drops native PauseAll() | |

**User's choice:** Native `PauseAll()` + per-job fresh resume.
**Notes:** Resolves the conflict — pause is scheduler-wide native (idempotent), resume is per-job `ResumeAsync` (fresh-from-now reschedule, deterministic skip-to-next, no catch-up burst). `[DisallowConcurrentExecution]` relied on unchanged. SC#4 to be reworded; resume idempotency already provided by `ResumeAsync`'s `TriggerState == Paused` guard.

---

## Per-workflow pause contracts: clean-break or defer

Investigation: the **only sender** of `PauseWorkflow`/`ResumeWorkflow` is `KeeperRecoveryHandler` (the reactive `Fault<T>` path), which is the explicit Phase-48 / RETIRE-03 removal target. Deleting the contracts now breaks that still-live handler's compile and pulls Phase-48 work forward.

| Option | Description | Selected |
|--------|-------------|----------|
| Defer to Phase 48 — additive now | Phase 45 adds global PauseAll/ResumeAll only; old path stays live until RETIRE-03 sweeps handler + contracts + consumers + keeper-dlq | ✓ |
| Clean-break now — delete in Phase 45 | Forces gutting KeeperRecoveryHandler's publishes; partial Phase-48 teardown bleeds in | |

**User's choice:** Defer to Phase 48 — additive now.
**Notes:** Keeps every intermediate buildable. Transition-window coexistence (reactive per-workflow pause + BIT global pause across Phases 45–47) noted as a deferred concern for a downstream phase.

---

## BIT health gate primitive + bounded wait (KEEP-03)

### Q1 — gate mechanism

| Option | Description | Selected |
|--------|-------------|----------|
| Dedicated IL2HealthGate service, async reset-event | DI-singleton; Open()/Close() + WaitForOpenAsync via swappable TaskCompletionSource; no polling | ✓ |
| Dedicated IL2HealthGate service, poll-the-bool | Same seam, but WaitForOpenAsync polls a volatile bool with Task.Delay | |
| Shared volatile state, no dedicated service | No injection seam; wait policy duplicated at call site | |

**User's choice:** Dedicated `IL2HealthGate` service, async reset-event.

### Q2 — initial gate state at startup

| Option | Description | Selected |
|--------|-------------|----------|
| Closed until first healthy probe | Fail-safe; pre-first-probe messages wait rather than touch unverified L2 | ✓ |
| Open optimistically at startup | First failing BIT closes it; lower first-message latency | |

**User's choice:** Closed until first healthy probe.
**Notes:** Phase 45 builds the mechanism (gate + WaitForOpenAsync); Phase 46 sets the concrete bound under the broker consumer_timeout and is its first consumer.

---

## Broadcast cadence + transport (KEEP-01 / KEEP-02)

### Q1 — cadence

| Option | Description | Selected |
|--------|-------------|----------|
| Edge-triggered — only on health transition | Publish once per healthy↔unhealthy edge; one bool of loop state; no steady-state spam | ✓ |
| Level-triggered — every tick (design-literal) | Stateless; leans on idempotency; resume-all every 5s while healthy = L1 iteration + log noise | |

**User's choice:** Edge-triggered.
**Notes:** SC#2 to be reworded from "each BIT result broadcasts" to "each health transition broadcasts." The design doc's literal "each BIT result fans out" read as descriptive intent, not a per-tick mandate.

### Q2 — endpoint topology

Investigation: the orchestrator is multi-replica by design and the existing per-workflow pause/resume already uses a per-replica fan-out endpoint (`InstanceId` + `Temporary = true`). So per-replica fan-out is the established, correct pattern (each replica pauses its own in-memory scheduler) — not itself an open fork. Remaining choice was endpoint topology.

| Option | Description | Selected |
|--------|-------------|----------|
| New dedicated per-replica fan-out endpoint | e.g. orchestrator-global-pause; independent from orchestrator-pauseresume; clean Phase-48 separation | ✓ |
| Reuse existing orchestrator-pauseresume endpoint | Fewer endpoints; mixes soon-retired path with new path | |

**User's choice:** New dedicated per-replica fan-out endpoint.

---

## Claude's Discretion

- Probe primitive reuse — extract `ProbeOnceAsync` from `L2ProbeRecovery` for the BIT loop (grounded default, not asked).
- BIT loop hosting (`BackgroundService`), `IL2HealthGate` placement/TCS mechanics, `PauseAll`/`ResumeAll` contract shape (no `H`), `Probe:DelaySeconds` reuse vs new knob.

## Deferred Ideas

- Removal of per-workflow `PauseWorkflow`/`ResumeWorkflow` + reactive `KeeperRecoveryHandler` → Phase 48 / RETIRE-03.
- Transition-window coexistence of reactive + BIT pause paths (Phases 45–47) → downstream phase decides when reactive recovery is switched off.
- Exact Phase-46 gate wait bound → set in Phase 46.
