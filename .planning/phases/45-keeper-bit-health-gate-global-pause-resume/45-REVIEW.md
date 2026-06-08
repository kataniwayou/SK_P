---
phase: 45-keeper-bit-health-gate-global-pause-resume
reviewed: 2026-06-08T00:00:00Z
depth: standard
files_reviewed: 18
files_reviewed_list:
  - src/Messaging.Contracts/PauseAll.cs
  - src/Messaging.Contracts/ResumeAll.cs
  - src/Keeper/Health/IL2HealthGate.cs
  - src/Keeper/Health/L2HealthGate.cs
  - src/Keeper/Health/BitHealthLoop.cs
  - src/Keeper/Recovery/L2ProbeRecovery.cs
  - src/Keeper/Program.cs
  - src/Orchestrator/Consumers/PauseAllConsumer.cs
  - src/Orchestrator/Consumers/ResumeAllConsumer.cs
  - src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs
  - src/Orchestrator/Consumers/ResumeAllConsumerDefinition.cs
  - src/Orchestrator/Scheduling/WorkflowScheduler.cs
  - src/Orchestrator/Program.cs
  - tests/BaseApi.Tests/Keeper/Health/L2HealthGateTests.cs
  - tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs
  - tests/BaseApi.Tests/Orchestrator/Consumers/PauseAllConsumerTests.cs
  - tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs
  - tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs
findings:
  critical: 0
  warning: 2
  info: 4
  total: 6
status: issues_found
---

# Phase 45: Code Review Report

**Reviewed:** 2026-06-08T00:00:00Z
**Depth:** standard
**Files Reviewed:** 18
**Status:** issues_found

## Summary

Reviewed the Phase 45 keeper BIT health gate and global pause/resume feature: two no-H broadcast contracts (`PauseAll`/`ResumeAll`), the swappable-TCS `L2HealthGate`, the edge-triggered `BitHealthLoop` BackgroundService, the extracted `ProbeOnceAsync` standing-probe path, both orchestrator global consumers + their shared-endpoint definitions, the new `WorkflowScheduler.PauseAllAsync` seam, the two composition roots, and five test files.

Overall quality is high. The concurrency design in `L2HealthGate` (CAS-guarded TCS swap, fail-safe closed start, `RunContinuationsAsynchronously`) is correct and well-tested. The edge-trigger logic in `BitHealthLoop` is sound and the test matrix is thorough. No critical (security/data-loss/crash) issues found. The findings below are two correctness edge cases worth confirming against the design intent, plus four minor/informational notes.

## Warnings

### WR-01: PauseAll/ResumeAll publish failure desyncs the gate from `prevHealthy` (transient duplicate-broadcast / silent-skip window)

**File:** `src/Keeper/Health/BitHealthLoop.cs:28-43`
**Issue:** On a state transition the loop mutates the gate (`gate.Open()`/`gate.Close()`) BEFORE `await bus.Publish(...)`, and only assigns `prevHealthy = healthy` AFTER the publish completes. If `bus.Publish` throws (broker transiently unavailable), `ExecuteAsync` faults and the entire BackgroundService stops — the BIT loop dies and no further pause/resume broadcasts are ever sent until the host restarts. Unlike a probe `RedisException` (caught inside `ProbeOnceAsync` and treated as "unhealthy"), a publish-side exception is not handled anywhere, so a RabbitMQ blip silently kills the standing health gate. This is the more consequential failure of the two ordering concerns: the gate having moved while `prevHealthy` did not is self-correcting on the next tick (gate ops are idempotent), but a dead loop is not self-correcting.
**Fix:** Wrap the transition body so a transient publish failure does not terminate the loop, and keep `prevHealthy` un-advanced so the next tick re-attempts the broadcast:
```csharp
if (prevHealthy != healthy)
{
    try
    {
        if (healthy)
        {
            gate.Open();
            await bus.Publish(new ResumeAll { CorrelationId = NewId.NextGuid() }, stoppingToken);
            logger.LogInformation("L2 healthy — gate OPEN, ResumeAll broadcast");
        }
        else
        {
            gate.Close();
            await bus.Publish(new PauseAll { CorrelationId = NewId.NextGuid() }, stoppingToken);
            logger.LogWarning("L2 unhealthy — gate CLOSED, PauseAll broadcast");
        }
        prevHealthy = healthy;   // only advance the edge once the broadcast actually went out
    }
    catch (OperationCanceledException) { break; }   // shutdown
    catch (Exception ex)
    {
        logger.LogError(ex, "BIT transition broadcast failed — will retry next tick");
        // prevHealthy intentionally NOT advanced -> next tick re-broadcasts the same edge
    }
}
```
If the intended design is in fact "fail fast and let the host restart the loop," that is a legitimate choice — but it should be made explicit (a comment + a test), because as written the failure mode is silent. Confirm against the phase design (D-06).

### WR-02: First-tick `prevHealthy == null` healthy path broadcasts a `ResumeAll` to a system that was never paused

**File:** `src/Keeper/Health/BitHealthLoop.cs:21-35`
**Issue:** `prevHealthy` starts `null`, so the first probe is always treated as a transition. When the first probe is healthy the loop calls `gate.Open()` and broadcasts `ResumeAll`. The gate.Open() is correct and necessary (fail-safe gate starts closed — D-12). But the `ResumeAll` broadcast on startup is unconditional: every Keeper start/restart with a healthy L2 fans out a global `ResumeAll` to all orchestrator replicas, which enumerates every workflow and runs `ResumeAsync`. The `ResumeAllConsumer` is idempotent (only acts on `Paused` triggers), so this is not a correctness bug, but it does mean a Keeper rolling restart emits a spurious global resume sweep across all replicas on every restart. The test `Edge_Trigger_Publishes_PauseAll_Once_On_Healthy_To_Unhealthy` (line 168) explicitly encodes this "first healthy tick → 1 ResumeAll" behavior, so it appears intended — but the cost (per-restart global resume fan-out) is worth confirming.
**Fix:** If the startup `ResumeAll` is unwanted, decouple gate-open from broadcast on the first tick:
```csharp
if (healthy)
{
    gate.Open();
    if (prevHealthy is not null)   // suppress the startup ResumeAll; still open the gate
        await bus.Publish(new ResumeAll { CorrelationId = NewId.NextGuid() }, stoppingToken);
}
```
If the startup broadcast is intended (idempotent, harmless), no change needed — document the rationale where `prevHealthy` is declared so a future reader does not "fix" it.

## Info

### IN-01: `WaitForOpenAsync` fast path ignores an already-cancelled token

**File:** `src/Keeper/Health/L2HealthGate.cs:21-26`
**Issue:** When the gate is already open, `WaitForOpenAsync` returns the completed `openTask` without observing `ct`. A caller that passes an already-cancelled token while the gate is open gets a successful completion instead of an `OperationCanceledException`. This is a benign and common fast-path convention, and the current sole reader (Phase-46 recovery, per the interface doc) is unlikely to care, but it is a minor asymmetry vs. the blocking path which honors cancellation.
**Fix:** If strict cancellation semantics are desired, add `ct.ThrowIfCancellationRequested();` before the fast-path return. Otherwise leave as-is (acceptable convention).

### IN-02: `ResumeAllConsumer` enumerates the L1 snapshot without isolating per-workflow resume failures

**File:** `src/Orchestrator/Consumers/ResumeAllConsumer.cs:32-34`
**Issue:** The `foreach` awaits `ResumeAsync` for each workflow id serially; if `ResumeAsync` throws for one workflow (e.g. a transient Quartz fault), the loop aborts and the remaining workflows are not resumed for that delivery. Because the endpoint has `UseMessageRetry(Immediate(N))` and the consumer is idempotent, a retry re-runs the whole loop and idempotently skips the already-resumed ones, so this self-heals. Noting it only because partial progress + retry-the-whole-batch is an intentional trade-off worth being aware of (no per-item dead-lettering).
**Fix:** No change required given idempotency + retry. If per-workflow isolation is ever wanted, wrap the body in try/catch and aggregate failures. Document the current "all-or-retry" behavior.

### IN-03: `ResumeNoBurstTests` uses `.GetAwaiter().GetResult()` in a sync helper

**File:** `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs:52`
**Issue:** `Build()` is synchronous and blocks on `HydrateAndScheduleAsync(...).GetAwaiter().GetResult()`. This works here (no sync-context deadlock risk in xUnit), but it is an inconsistency with the rest of the suite (e.g. `ResumeAllConsumerTests` uses `async` helpers). Sync-over-async in tests can mask `OperationCanceledException` wrapping (it surfaces as `AggregateException`-unwrapped in newer runtimes, but the pattern is fragile).
**Fix:** Make `Build` async (`static async Task<(...)> Build(...)`) and `await` the hydrate call, mirroring `ResumeAllConsumerTests.NewRamSchedulerAsync`. Cosmetic — not blocking.

### IN-04: `L2ProbeRecovery` carries unused `using System;`

**File:** `src/Keeper/Recovery/L2ProbeRecovery.cs:1`
**Issue:** `using System;` is present; `System.Guid`/`System.TimeSpan` are referenced unqualified, so the using is genuinely used — however `using System.Collections.Generic;` (line 2) is required only for `KeyValuePair`/`IEnumerable`-style usage. Both are in fact used here (`KeyValuePair<,>` on line 27). No dead usings found after closer inspection. Flagging only to record that the import set was checked and is clean — no action.

---

_Reviewed: 2026-06-08T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
