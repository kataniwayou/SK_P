---
phase: 37
slug: orchestrator-pause-resume-coordination
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-06
---

# Phase 37 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from 37-RESEARCH.md §"Validation Architecture". Apply CONTEXT.md revisions
> **D-08** (Pause = Quartz `PauseJob`; `GetTriggerState` is sole source of truth) and
> **D-09** (resume on any successful recovery; give-up → DLQ, no re-pin).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (`tests/BaseApi.Tests`), NSubstitute for mocks |
| **Config file** | repo Central Package Management; xUnit analyzer enforces `xUnit1051` — every Quartz call taking a `CancellationToken` must pass `TestContext.Current.CancellationToken` |
| **Quartz under test** | real `StdSchedulerFactory` RAMJobStore, unique `quartz.scheduler.instanceName = test-{Guid:N}` per test (avoids shared process-wide repository collision) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Orchestrator.Scheduling|FullyQualifiedName~PauseResume"` |
| **Full suite command** | `dotnet test` (solution) + live `scripts/phase-37-close.ps1` 3×GREEN real-stack gate |
| **Estimated runtime** | ~30 seconds (hermetic filter); full suite + close-gate minutes |

---

## Sampling Rate

- **After every task commit:** Run the quick run command (filtered hermetic tests)
- **After every plan wave:** Run the full suite command
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** ~30 seconds (hermetic)

---

## Per-Task Verification Map

> Task IDs are assigned by the planner; the Requirement→Test rows below are the validation contract
> each task must satisfy. The executor binds `{N}-NN-NN` task IDs to these rows in Wave 0.

| Requirement | Behavior | Test Type | Automated Command | File Exists |
|-------------|----------|-----------|-------------------|-------------|
| PAUSE-01 | `PauseWorkflow`/`ResumeWorkflow` records implement `ICorrelated`, body-carry `CorrelationId`+`WorkflowId`+`H` | unit | `dotnet test --filter "~PauseResumeContract"` | ❌ W0 |
| PAUSE-01 | Keeper publishes `PauseWorkflow` at intake + `ResumeWorkflow` on Recovered (fan-out via `context.Publish`) | unit (mock `ConsumeContext.Publish`) | `dotnet test --filter "~KeeperPausePublish"` | ❌ W0 |
| PAUSE-02 (D-08) | Pause → `GetTriggerState(TriggerKey(jobId)) == Paused`; next self-reschedule suppressed (no live fire) | unit (real RAM scheduler) | `dotnet test --filter "~PauseSuppressesFire"` | ❌ W0 |
| PAUSE-03 | Resume on `Paused` → fresh trigger scheduled, `GetTriggerState == Normal`, future `StartAt` (no misfire) | unit (real RAM scheduler) | `dotnet test --filter "~ResumeReschedulesFresh"` | ❌ W0 |
| PAUSE-04 | Duplicate/concurrent Pause→Resume at `ConcurrentMessageLimit=1` → idempotent end state (one Normal trigger, no orphans) | unit (serial replay) | `dotnet test --filter "~PauseResumeIdempotent"` | ❌ W0 |
| PAUSE-05 (D-09) | `None` (Stopped) and `Normal` (already Running) workflows ignore Resume (no-op) | unit (real RAM scheduler) | `dotnet test --filter "~ResumeIgnoresStoppedAndRunning"` | ❌ W0 |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Messaging/PauseResumeContractTests.cs` — PAUSE-01 contract shape
- [ ] `tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs` — PAUSE-01 Keeper publish sites
- [ ] `tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs` — PAUSE-02/03/05 vs RAM scheduler
- [ ] `tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs` — PAUSE-04 idempotency + consumer ACK semantics
- [ ] Framework install: none — xUnit + NSubstitute + real Quartz RAMJobStore already in `tests/BaseApi.Tests`
- [ ] (optional) extend `KeeperRecoveryE2ETests` / new orchestrator E2E for the live bus round-trip (real-stack)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live bus round-trip: Keeper fault intake (poisoned L2) → `PauseWorkflow` → orchestrator pauses → recovery → `ResumeWorkflow` → orchestrator resumes | PAUSE-01..05 (E2E) | Requires RabbitMQ + Redis + live consoles; embedded SourceHash means orchestrator/keeper containers must be rebuilt before the gate | `scripts/phase-37-close.ps1` 3×GREEN; model on `KeeperRecoveryE2ETests`/`FaultRecoverySpikeE2ETests` |
| Orchestrator-restart pause-state loss | (accepted limitation) | Documented out-of-scope behavior (HydrationBackgroundService reschedules all on restart) | No test — accepted & documented in CONTEXT.md |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 30s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
