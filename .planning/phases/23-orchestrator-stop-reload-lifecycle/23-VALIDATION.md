---
phase: 23
slug: orchestrator-stop-reload-lifecycle
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-31
---

# Phase 23 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `23-RESEARCH.md` § Validation Architecture (9 ORCH-* requirements → observable signals).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 (Microsoft.Testing.Platform runner) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (MTP: `OutputType=Exe`, `UseMicrosoftTestingPlatformRunner=true`) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -- --filter-class BaseApi.Tests.Orchestrator.*` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Estimated runtime** | ~30 seconds (orchestrator slice); full suite longer |
| **Test placement** | extend `tests/BaseApi.Tests/Orchestrator/` (Orchestrator already a ProjectReference; in-memory harness + Redis-mux-stub patterns live there) |

> **MTP filter note:** raw filter syntax is `-- --filter-class` (the `--` separates dotnet-test args from the MTP runner args). In-memory MassTransit harness (`AddMassTransitTestHarness`) — no real RabbitMQ broker needed. Redis multiplexer stubbed via NSubstitute (`AbsentL2`/`PresentL2`/`InfraFaultL2` helpers from `StartStopConsumerAckTests`). Deterministic clock via `FakeTimeProvider`.

---

## Sampling Rate

- **After every task commit:** Run `dotnet test ... -- --filter-class BaseApi.Tests.Orchestrator.*` (orchestrator slice)
- **After every plan wave:** Run `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (full suite)
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** ~30 seconds (orchestrator slice)

---

## Per-Task Verification Map

> Planner fills concrete task IDs during planning. Requirement → observable-signal mapping below is locked from research.

| Req ID | Behavior (observable signal) | Test Type | Automated Command (filter-class) | File Exists | Status |
|--------|------------------------------|-----------|----------------------------------|-------------|--------|
| ORCH-CONTRACT-01 | Real `skp:{wf}:{step}` JSON deserializes into reader record; `entryCondition` round-trips as int (writer enum value → reader int) | unit | `*.StepProjectionReaderTests` | ❌ W0 | ⬜ pending |
| ORCH-CONTRACT-02 | Constructed `EntryStepDispatch` has 7 fields; `ExecutionId`/`EntryId` = `Guid.Empty`; `CorrelationId` carries per-fire value | unit | `*.EntryStepDispatchTests` | ❌ W0 | ⬜ pending |
| ORCH-STARTUP-01 | After hydration of N-workflow L2: L1 holds exactly N workflow entries + each one's steps; NO processor key, NO parent-index key | unit (mux stub) | `*.HydrationTests` | ❌ W0 | ⬜ pending |
| ORCH-SCHED-01 | Scheduler has exactly one started (non-paused) job per workflow keyed by `jobId`; L1 liveness `interval` == next-two-fire-times delta seconds | unit (FakeTimeProvider) | `*.SchedulingTests` | ❌ W0 | ⬜ pending |
| ORCH-FIRE-01 | Synthetic consumer on `queue:{processorId}` receives one msg per entry step, correct fields; `correlationId` differs across 2 fires; L1 liveness `timestamp` advances; transport is `Send` | harness (in-memory) | `*.FireDispatchTests` | ❌ W0 | ⬜ pending |
| ORCH-CONSUME-01 | `StartOrchestration([wfX])` → L1 has wfX only + scheduled job for wfX; synthetic consumer receives wfX entry-step msgs | harness | `*.StartConsumerLifecycleTests` | ❌ W0 | ⬜ pending |
| ORCH-STOP-01 | After Stop(wfX): scheduler lacks the job, L1 has no wfX entries, L2 snapshot byte-identical before/after (zero orchestrator L2 writes) | harness | `*.StopConsumerLifecycleTests` | ❌ W0 | ⬜ pending |
| ORCH-SCALE-01 | Code review: no static/global singleton lock or process-uniqueness assumption gates the lifecycle | review (+ optional reflection guard) | `*.NoGlobalLockTests` (optional) | ❌ W0 | ⬜ pending |
| ORCH-ACK-01 | Absent-workflow Start/Stop → acked, no `_error` message; simulated Redis-unreachable consume → faults (propagates); startup with 1 corrupt entry hydrates the rest + host stays up | harness (mux stub) | `*.AckSemanticsTests` (extend `StartStopConsumerAckTests`) | ⚠️ extend | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

**Observable signals (Nyquist sampling targets):**
- Scheduler job count == workflow count (`scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup())`).
- L1 entry count == N; no `skp:` parent-index key and no processor key present in L1.
- Synthetic-consumer captured-message count == entry-step count per fire; `Guid` inequality across two fires' `CorrelationId`.
- L2 byte-identical snapshot before/after Stop (read all `skp:*` values pre/post; assert equal) — proves zero L2 writes.
- Zero `_error`-queue messages on an absent-workflow Stop (harness `Consumed` has no fault).
- `db.DidNotReceive().StringSetAsync(...)` / `SetAddAsync` / `KeyDeleteAsync` on any `skp:` key (extend the existing assertion).

---

## Wave 0 Requirements

- [ ] CPM pins `Quartz` + `Quartz.Extensions.Hosting` 3.18.1 in `Directory.Packages.props`; `PackageReference Include="Quartz.Extensions.Hosting"` in `Orchestrator.csproj` — **blocks all scheduling code**
- [ ] `tests/BaseApi.Tests/Orchestrator/StepProjectionReaderTests.cs` — cross-record round-trip (writer enum value → reader int), covers ORCH-CONTRACT-01 + Pitfall 7
- [ ] `tests/BaseApi.Tests/Orchestrator/EntryStepDispatchTests.cs` — 7-field + Guid.Empty stubs (ORCH-CONTRACT-02)
- [ ] `HydrationTests` fixture — Redis-mux stub returning a parent-index SET + roots + steps
- [ ] `SchedulingTests` — `FakeTimeProvider`-driven Cronos-interval assertion
- [ ] Synthetic `CapturingDispatchConsumer` + `ReceiveEndpoint("{processorId:D}")` harness helper (reusable across FIRE/CONSUME tests)
- [ ] Extend `StartStopConsumerAckTests` for the gate-drop + corrupt-entry + Redis-unreachable cases (ORCH-ACK-01)

*Framework already installed — only test files + the Quartz CPM pin are gaps.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| No static/global singleton lock or process-uniqueness assumption gates the lifecycle | ORCH-SCALE-01 | Architecture invariant; partly a code-review judgement (an optional reflection guard can assert no static lock fields, but "no process-uniqueness assumption" needs human review) | Review L1 store, scheduler wiring, consumers, hydration service for any `static` lock / singleton-process gate; confirm a 2nd replica would not break (it would N×-dispatch, which is the accepted/deferred behavior, not a crash) |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references (Quartz CPM pin + 6 test files)
- [ ] No watch-mode flags
- [ ] Feedback latency < 30s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
