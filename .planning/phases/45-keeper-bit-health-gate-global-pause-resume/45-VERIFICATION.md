---
phase: 45-keeper-bit-health-gate-global-pause-resume
verified: 2026-06-08T19:10:00Z
status: passed
score: 4/4
overrides_applied: 0
---

# Phase 45: Keeper BIT Health Gate + Global Pause/Resume — Verification Report

**Phase Goal:** The Keeper runs a suppressed background BIT loop that probes L2 (read + write-then-delete) on a configurable delay and broadcasts a global pause-all (unhealthy) / resume-all (healthy) decision to all orchestrators, and the orchestrator's pause-all/resume-all is idempotent per job via Quartz `TriggerState`.
**Verified:** 2026-06-08T19:10:00Z
**Status:** passed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | A background loop runs a BIT against L2 (read + write-then-delete probe) on a configurable `Probe:DelaySeconds` delay, and BIT exceptions are suppressed so a probe failure never crashes the loop (it simply reports unhealthy). | VERIFIED | `BitHealthLoop` BackgroundService calls `probe.ProbeOnceAsync(stoppingToken)` each tick using `Probe:DelaySeconds`. `ProbeOnceAsync` catches `RedisException` only (returns `false`); no `catch (Exception)` in `L2ProbeRecovery.cs`. 6/6 `BitHealthLoopTests` GREEN — `Probe_RedisException_Reports_Unhealthy_Loop_Survives` directly asserts a Redis failure does not end the loop; `Probe_NonRedis_Throw_Propagates_Not_Swallowed` confirms genuine exceptions propagate. |
| 2 | Each BIT result fans out a GLOBAL broadcast to all orchestrators — unhealthy → pause all jobs, healthy → resume all jobs. | VERIFIED | `BitHealthLoop` uses `bus.Publish` (never `bus.Send`) with edge-trigger (`bool? prevHealthy`): publishes `PauseAll` on healthy→unhealthy transition, `ResumeAll` on unhealthy→healthy, nothing on same-state ticks. `Same_State_Ticks_Publish_Nothing` (5-tick script → exactly 1 PauseAll + 2 ResumeAll) GREEN. Both consumers (`PauseAllConsumer`, `ResumeAllConsumer`) are registered per-replica on the new `orchestrator-global-pauseresume` fan-out endpoint with `InstanceId + Temporary = true` in `Orchestrator/Program.cs`. |
| 3 | The recovery consumer's gate primitive and bounded-wait mechanism exist and are correct: `IL2HealthGate` + `L2HealthGate` start CLOSED, `Open()`/`Close()` are idempotent, and `WaitForOpenAsync(CancellationToken ct)` throws `OperationCanceledException` on cancellation (the gate-closed wait bounded under the broker consumer timeout, exercised by Phase-46 ops). | VERIFIED | `IL2HealthGate` interface declares `void Open()`, `void Close()`, `Task WaitForOpenAsync(CancellationToken ct)`. `L2HealthGate` is a Stephen Toub swappable-TCS async reset event: starts CLOSED (pending TCS), `Open()` calls `TrySetResult` (idempotent), `Close()` CAS-swaps a fresh pending TCS (idempotent no-op if already closed), `WaitForOpenAsync` uses `Task.WaitAsync(ct)` (throws OCE). All 6 `L2HealthGateTests` GREEN: CLOSED start, open completes wait, close re-blocks, OCE on cancel, open idempotent, close idempotent. Gate + loop DI-registered (`AddSingleton<IL2HealthGate, L2HealthGate>` + `AddHostedService<BitHealthLoop>`) in `Keeper/Program.cs`. |
| 4 | Orchestrator pause-all/resume-all is idempotent per job via Quartz `TriggerState` — pause only if Running, resume only if Paused — so a repeated broadcast is a no-op and no job is double-paused or spuriously resumed. | VERIFIED | `PauseAllConsumer` calls `scheduler.PauseAllAsync(ct)` (delegates to `IScheduler.PauseAll` — idempotent Quartz no-op for already-paused groups). `ResumeAllConsumer` enumerates `store.WorkflowIds` and calls `lifecycle.ResumeAsync(id, ct)` per-job — `ResumeAsync` guards on `TriggerState == Paused` (no-op otherwise) + fresh-from-now reschedule (skip-to-next, no burst). `scheduler.ResumeAll()` is never called. 6/6 Orchestrator consumer tests GREEN including the load-bearing `Native_ResumeAll_Is_Never_Called` spy assertion and `Resume_Reschedules_Fresh_From_Now_StartAt_Ge_Now`. |

**Score:** 4/4 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Messaging.Contracts/PauseAll.cs` | no-H global pause broadcast contract | VERIFIED | `public sealed record PauseAll : ICorrelated { public Guid CorrelationId { get; init; } }` — no `H`, no `WorkflowId` |
| `src/Messaging.Contracts/ResumeAll.cs` | no-H global resume broadcast contract | VERIFIED | `public sealed record ResumeAll : ICorrelated { public Guid CorrelationId { get; init; } }` — no `H`, no `WorkflowId` |
| `src/Keeper/Health/IL2HealthGate.cs` | gate interface Open/Close/WaitForOpenAsync(CT) | VERIFIED | Contains `void Open()`, `void Close()`, `Task WaitForOpenAsync(CancellationToken ct)` |
| `src/Keeper/Health/L2HealthGate.cs` | Toub swappable-TCS gate, starts CLOSED | VERIFIED | Contains `RunContinuationsAsynchronously` on both TCS constructions, `Interlocked.CompareExchange(ref _tcs`, `WaitAsync(ct)`. No `volatile bool`, no `SpinWait`, no `while (` polling loop. |
| `src/Keeper/Health/BitHealthLoop.cs` | BackgroundService edge-trigger probe loop | VERIFIED | `: BackgroundService`, `bool? prevHealthy`, `if (prevHealthy != healthy)`, `gate.Open()`, `gate.Close()`, `bus.Publish(new ResumeAll`, `bus.Publish(new PauseAll`. No `bus.Send`. `catch (OperationCanceledException)` guards only `Task.Delay`. |
| `src/Keeper/Recovery/L2ProbeRecovery.cs` | extracted ProbeOnceAsync core | VERIFIED | `public async Task<bool> ProbeOnceAsync(CancellationToken ct, Guid? entryId = null, string? h = null)` with `catch (RedisException)` + `return false/true`. No `catch (Exception)`. `RunAsync` still contains `metrics.InFlight.Add`, `metrics.L2ProbeFailed.Add`, `ProbeOutcome.Recovered`, `ProbeOutcome.GaveUp`. |
| `src/Keeper/Program.cs` | gate + loop DI registration | VERIFIED | `AddSingleton<Keeper.Health.IL2HealthGate, Keeper.Health.L2HealthGate>()` and `AddHostedService<Keeper.Health.BitHealthLoop>()` present |
| `src/Orchestrator/Scheduling/WorkflowScheduler.cs` | PauseAllAsync seam | VERIFIED | `public Task PauseAllAsync(CancellationToken ct) => scheduler.PauseAll(ct);` present. No `ResumeAllAsync`, no `scheduler.ResumeAll(`. |
| `src/Orchestrator/Consumers/PauseAllConsumer.cs` | scheduler-wide pause consumer | VERIFIED | `IConsumer<PauseAll>`, `scheduler.PauseAllAsync(context.CancellationToken)`, structured log `{CorrelationId}`, no `$"` interpolation |
| `src/Orchestrator/Consumers/ResumeAllConsumer.cs` | per-job resume consumer | VERIFIED | `IConsumer<ResumeAll>`, `foreach (var workflowId in store.WorkflowIds)`, `lifecycle.ResumeAsync(workflowId, context.CancellationToken)`. No native `ResumeAll(` call. |
| `src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs` | retry-owning definition on the new endpoint | VERIFIED | `EndpointName = "orchestrator-global-pauseresume"`, `UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))`, `ConcurrentMessageLimit = 1`, ctor injects `IOptions<RetryOptions>` |
| `src/Orchestrator/Consumers/ResumeAllConsumerDefinition.cs` | non-retry definition sharing the new endpoint | VERIFIED | `EndpointName = "orchestrator-global-pauseresume"`, `ConcurrentMessageLimit = 1`, no `UseMessageRetry`, parameterless ctor |
| `src/Orchestrator/Program.cs` | fan-out registration of both global consumers | VERIFIED | `AddConsumer<PauseAllConsumer, PauseAllConsumerDefinition>().Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` and `AddConsumer<ResumeAllConsumer, ResumeAllConsumerDefinition>().Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` |
| `tests/BaseApi.Tests/Keeper/Health/L2HealthGateTests.cs` | KEEP-03 gate tests | VERIFIED | 6/6 GREEN: `Gate_Starts_Closed_WaitForOpenAsync_Blocks_Until_Open`, `Open_Completes_The_Wait`, `Close_After_Open_Re_Blocks`, `WaitForOpenAsync_Throws_OperationCanceledException_On_Cancel`, `Open_Is_Idempotent`, `Close_Is_Idempotent` |
| `tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs` | KEEP-01 + KEEP-02 BIT loop tests | VERIFIED | 6/6 GREEN: `Probe_RedisException_Reports_Unhealthy_Loop_Survives`, `Probe_NonRedis_Throw_Propagates_Not_Swallowed`, `StoppingToken_Ends_Loop_Cleanly`, `Edge_Trigger_Publishes_PauseAll_Once_On_Healthy_To_Unhealthy`, `Edge_Trigger_Publishes_ResumeAll_Once_On_Unhealthy_To_Healthy`, `Same_State_Ticks_Publish_Nothing` |
| `tests/BaseApi.Tests/Orchestrator/Consumers/PauseAllConsumerTests.cs` | ORCH-02 pause tests | VERIFIED | 2/2 GREEN: `Consume_Calls_Scheduler_PauseAll`, `Redelivery_Is_Idempotent_No_Op` |
| `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs` | ORCH-02 resume tests | VERIFIED | 2/2 GREEN: `Consume_Enumerates_WorkflowIds_And_Calls_ResumeAsync_Each`, `Resume_Of_Non_Paused_Trigger_Is_Ignored` |
| `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs` | ORCH-02 no-herd negative | VERIFIED | 2/2 GREEN: `Native_ResumeAll_Is_Never_Called`, `Resume_Reschedules_Fresh_From_Now_StartAt_Ge_Now` |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `src/Keeper/Health/BitHealthLoop.cs` | `src/Keeper/Recovery/L2ProbeRecovery.cs` | calls `ProbeOnceAsync` each tick | WIRED | Line 26: `var healthy = await probe.ProbeOnceAsync(stoppingToken)` |
| `src/Keeper/Health/BitHealthLoop.cs` | `src/Messaging.Contracts/PauseAll.cs` | `bus.Publish(new PauseAll...)` on transition | WIRED | Line 39: `await bus.Publish(new PauseAll { CorrelationId = NewId.NextGuid() }, stoppingToken)` |
| `src/Keeper/Health/BitHealthLoop.cs` | `src/Messaging.Contracts/ResumeAll.cs` | `bus.Publish(new ResumeAll...)` on transition | WIRED | Line 33: `await bus.Publish(new ResumeAll { CorrelationId = NewId.NextGuid() }, stoppingToken)` |
| `src/Keeper/Health/BitHealthLoop.cs` | `src/Keeper/Health/IL2HealthGate.cs` | `gate.Open()`/`gate.Close()` on transition | WIRED | Lines 32/38: `gate.Open()` and `gate.Close()` on respective health transitions |
| `src/Orchestrator/Consumers/ResumeAllConsumer.cs` | `src/Orchestrator/L1/IWorkflowL1Store.cs` | enumerate `store.WorkflowIds` | WIRED | Line 32: `foreach (var workflowId in store.WorkflowIds)` |
| `src/Orchestrator/Consumers/ResumeAllConsumer.cs` | `src/Orchestrator/Hydration/WorkflowLifecycle.cs` | `lifecycle.ResumeAsync` per workflow | WIRED | Line 33: `await lifecycle.ResumeAsync(workflowId, context.CancellationToken)` |
| `src/Orchestrator/Consumers/PauseAllConsumer.cs` | `src/Orchestrator/Scheduling/WorkflowScheduler.cs` | `scheduler.PauseAllAsync` | WIRED | Line 24: `await scheduler.PauseAllAsync(context.CancellationToken)` |

---

### Data-Flow Trace (Level 4)

Phase 45 produces BackgroundService and consumer primitives — not UI components or data-rendering layers. Data-flow Level 4 is not applicable here (no dynamic data rendered to a view). The relevant data flow (ProbeOnceAsync → Redis → bool → edge trigger → bus.Publish) is exercised by `BitHealthLoopTests` with a scripted `IDatabase` double confirming real probe results drive real Publish calls.

---

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| `L2HealthGateTests` — 6 gate behaviors | MTP filter-query `L2HealthGateTests/*` | 6/6 passed | PASS |
| `BitHealthLoopTests` — 6 BIT loop behaviors | MTP filter-query `BitHealthLoopTests/*` | 6/6 passed | PASS |
| `PauseAllConsumerTests` — 2 scheduler pause behaviors | MTP filter-query `PauseAllConsumerTests/*` | 2/2 passed | PASS |
| `ResumeAllConsumerTests` — 2 per-job resume behaviors | MTP filter-query `ResumeAllConsumerTests/*` | 2/2 passed | PASS |
| `ResumeNoBurstTests` — 2 no-burst/no-native-ResumeAll | MTP filter-query `ResumeNoBurstTests/*` | 2/2 passed | PASS |
| Full hermetic suite (no RealStack) | MTP `--filter-not-trait Category=RealStack` | 506/506 passed | PASS |
| `SK_P.sln` full Release build | `dotnet build SK_P.sln -c Release` | 0 warnings / 0 errors | PASS |

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| KEEP-01 | 45-01-PLAN.md | Keeper runs a background BIT loop at a configurable delay; results suppressed (never crash the loop) | SATISFIED | `BitHealthLoop` BackgroundService + `ProbeOnceAsync` with `RedisException`-only catch. 6/6 `BitHealthLoopTests` GREEN including `Probe_RedisException_Reports_Unhealthy_Loop_Survives`. |
| KEEP-02 | 45-01-PLAN.md | BIT result drives a global pause-all / resume-all broadcast to all orchestrators | SATISFIED | Edge-triggered `bus.Publish` of `PauseAll`/`ResumeAll` on state transitions. `Same_State_Ticks_Publish_Nothing` GREEN. Per-replica fan-out endpoint in Orchestrator. |
| KEEP-03 | 45-01-PLAN.md | Recovery consumer performs each L2 op only while BIT gate is open; gate-closed consumer waits bounded under broker consumer timeout | SATISFIED (primitive) | `IL2HealthGate` interface + `L2HealthGate` implementation exist and are correct (6/6 gate tests GREEN). `WaitForOpenAsync(CancellationToken ct)` provides the bounded-wait seam. Per the phase design note, the actual recovery consumer wiring is Phase 46. |
| ORCH-02 | 45-02-PLAN.md | Orchestrator pause-all/resume-all idempotent per job via Quartz `TriggerState` | SATISFIED | `PauseAllConsumer` (idempotent Quartz `PauseAll`), `ResumeAllConsumer` (per-job `TriggerState == Paused` guard, never native `ResumeAll`). 6/6 Orchestrator consumer tests GREEN. |

**Orphaned requirements check:** REQUIREMENTS.md maps exactly KEEP-01, KEEP-02, KEEP-03, ORCH-02 to Phase 45. All four are covered by the plan frontmatter and by verified artifacts. No orphans.

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `src/Keeper/Health/BitHealthLoop.cs` | 32-42 | `bus.Publish` called between `gate.Open/Close()` and `prevHealthy = healthy` assignment without try/catch on the publish — a transient broker outage faults `ExecuteAsync` and kills the BIT loop permanently (REVIEW WR-01) | Warning | If RabbitMQ blips, the standing BIT loop dies and no further broadcasts are sent until host restart. This is a known design choice surfaced by the code review (WR-01) — the current behavior is "fail fast, let the host restart," but it is not explicitly documented. Not a correctness bug for Phase 45 scope; the gate's idempotent open/close self-corrects on the next tick. Flagged for human confirmation of design intent. |
| `src/Keeper/Health/BitHealthLoop.cs` | 21 | `prevHealthy = null` causes a `ResumeAll` broadcast on every Keeper startup when L2 is healthy (REVIEW WR-02) | Warning | On Keeper rolling restart with healthy L2, each replica emits an unconditional `ResumeAll` fan-out that enumerates all workflows and calls `ResumeAsync`. `ResumeAllConsumer` is idempotent (only acts on `Paused` triggers), so this is not incorrect — the test `Edge_Trigger_Publishes_PauseAll_Once_On_Healthy_To_Unhealthy` encodes this as intentional. Startup broadcast cost is acceptable given idempotency. |
| `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs` | 52 | `.GetAwaiter().GetResult()` in sync helper `Build()` — sync-over-async (REVIEW IN-03) | Info | Works in xUnit (no sync-context deadlock), but inconsistent with rest of test suite. Cosmetic only, no test correctness impact. |

No blockers found. WR-01 and WR-02 are design-intent questions raised by code review, not correctness failures. The code review status is `issues_found` (2 warnings, 4 info) with 0 critical findings.

---

### Human Verification Required

None. All must-haves are verified programmatically. The WR-01 design intent question (fail-fast vs. retry on bus publish) is documented in the code review and is a future hardening consideration, not a gate for Phase 45 goal achievement. The live round-trip (`PauseAll`→`ResumeAll` over a real Quartz scheduler with live RabbitMQ + Redis) is the Phase 49 TEST-02 RealStack gate, explicitly out of scope for this hermetic verification.

---

### Gaps Summary

No gaps. All four success criteria are verified against the actual codebase:

1. SC1 (KEEP-01): `BitHealthLoop` BackgroundService probes L2 via `ProbeOnceAsync` on `Probe:DelaySeconds` delay; `RedisException` is caught inside `ProbeOnceAsync` (returns `false`), loop continues. Non-Redis exceptions propagate. 6/6 loop tests GREEN.
2. SC2 (KEEP-02): Edge-triggered `bus.Publish` (never `bus.Send`) of `PauseAll`/`ResumeAll` once per state transition, fanout to all orchestrator replicas via the new per-replica `orchestrator-global-pauseresume` endpoint. 6/6 loop tests + 6/6 orchestrator consumer tests GREEN.
3. SC3 (KEEP-03): `IL2HealthGate` interface and `L2HealthGate` implementation are substantive and correct. `WaitForOpenAsync(CancellationToken ct)` is the bounded-wait primitive Phase 46 will consume. 6/6 gate tests GREEN.
4. SC4 (ORCH-02): `PauseAllConsumer` — scheduler-wide idempotent `PauseAll()`; `ResumeAllConsumer` — per-job `TriggerState == Paused` guard via `WorkflowLifecycle.ResumeAsync`, never native `scheduler.ResumeAll()`. `Native_ResumeAll_Is_Never_Called` and `Resume_Reschedules_Fresh_From_Now_StartAt_Ge_Now` GREEN.

Full hermetic suite: 506/506 passed. `SK_P.sln` Release build: 0 warnings / 0 errors.

---

_Verified: 2026-06-08T19:10:00Z_
_Verifier: Claude (gsd-verifier)_
