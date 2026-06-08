# Phase 45: Keeper BIT Health Gate + Global Pause/Resume - Research

**Researched:** 2026-06-08
**Domain:** .NET 8 distributed messaging — async coordination primitives, BackgroundService loops, MassTransit per-replica fan-out, Quartz pause/resume semantics
**Confidence:** HIGH

## Summary

Phase 45 builds a proactive Keeper **BIT loop** (`BackgroundService`) that probes L2 (Redis) on a configurable delay and drives two outputs: (1) an **edge-triggered** global `PauseAll`/`ResumeAll` broadcast to every orchestrator replica, and (2) a local DI-singleton **`IL2HealthGate`** (an async manual-reset event) that the Phase-46 recovery consumer will `await` before touching L2. All five mechanisms in the research focus map cleanly onto well-established .NET/MassTransit/Quartz idioms already present in this codebase — this phase is almost entirely *composition of existing patterns*, not novel design.

The two load-bearing facts that justify the locked decisions were both verified against authoritative sources this session: (a) the canonical async reset-event is Stephen Toub's `AsyncManualResetEvent` — a swappable `TaskCompletionSource` created with `RunContinuationsAsynchronously`, swapped via `Interlocked.Exchange` [CITED: devblogs.microsoft.com/dotnet]; (b) Quartz `ResumeAll()` **applies misfire instructions on resume**, so every past-due one-shot trigger built with `WithMisfireHandlingInstructionFireNow()` (`WorkflowScheduler.cs:47`) fires immediately — the exact cross-workflow herd D-02 avoids [CITED: quartz-scheduler.net + groups.google.com/g/quartznet]. The per-job `ResumeAsync` (`WorkflowLifecycle.cs:177–199`) already does `UnscheduleAsync` + fresh-from-now `ScheduleAsync` with a `TriggerState == Paused` guard, so `ResumeAll` is a thin enumerate-and-call loop over the L1 snapshot.

**Primary recommendation:** Build four new artifacts — `IL2HealthGate` (Toub AsyncManualResetEvent, starts CLOSED), a `BitHealthLoop : BackgroundService` (outer `while`, `RedisException`-only catch, edge-triggered publish), `PauseAll`/`ResumeAll` no-`H` contracts, and `PauseAllConsumer`/`ResumeAllConsumer` + definitions on a NEW dedicated per-replica fan-out endpoint `orchestrator-global-pauseresume` — plus extract a `ProbeOnceAsync()→bool` core from `L2ProbeRecovery`. Touch nothing on the per-workflow path (defer to Phase 48).

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| BIT probe loop (read+write-then-delete L2) | Keeper (background service) | Redis/L2 | Keeper owns L2 health detection; it is the recovery authority |
| Health gate primitive (`IL2HealthGate`) | Keeper (DI singleton, in-process) | — | Writer = BIT loop; reader = Phase-46 consumer; both in Keeper process |
| Global pause/resume *decision* (edge detection) | Keeper (BIT loop) | — | Single source of health truth; broadcasts on transition |
| Global pause/resume *enactment* | Orchestrator (per-replica consumers) | Quartz scheduler + L1 | Each replica's in-memory Quartz + L1 is per-instance state — must act locally |
| Broadcast transport | MassTransit / RabbitMQ | — | Fan-out exchange; one copy per replica |
| Per-job resume reschedule | Orchestrator (`WorkflowLifecycle`/`WorkflowScheduler`) | Quartz + Cronos | Skip-to-next-occurrence math lives where the one-shot triggers live |

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| KEEP-01 | Keeper BIT loop probes L2 on `Probe:DelaySeconds`, suppresses probe exceptions | §"BIT BackgroundService Loop" — `BackgroundService.ExecuteAsync` outer `while`, `Task.Delay(stoppingToken)`, `RedisException`-only catch, `ProbeOnceAsync` extraction |
| KEEP-02 | Each health transition fans out a global broadcast to all orchestrator replicas (D-07 reword) | §"MassTransit Per-Replica Fan-Out" — `Publish` + `InstanceId`+`Temporary=true` NEW endpoint mirroring `Orchestrator/Program.cs:41–50` |
| KEEP-03 | Keeper exposes an `IL2HealthGate` the Phase-46 consumer awaits (gate mechanism only) | §"Async Reset-Event Gate" — Toub `AsyncManualResetEvent`, `Open`/`Close`/`WaitForOpenAsync(CT)`, starts CLOSED |
| ORCH-02 | Orchestrator pause-all (scheduler-wide idempotent) + resume-all (per-job idempotent via `TriggerState==Paused`, fresh-from-now) | §"Quartz Pause-All / Per-Job Resume" — `scheduler.PauseAll()` + `ResumeAll` enumerates L1 → `ResumeAsync` each |
</phase_requirements>

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

- **D-01:** Pause-all = Quartz native `scheduler.PauseAll()` — one scheduler-wide call, idempotent (re-pausing all groups is a no-op).
- **D-02:** Resume-all = **per-job, NOT native `ResumeAll()`.** The `ResumeAll` consumer iterates the `IWorkflowL1Store` snapshot and calls `WorkflowLifecycle.ResumeAsync(workflowId)` for each. Native `ResumeAll()` would un-pause every past-due one-shot `SimpleSchedule` trigger and fire them all immediately = cross-workflow catch-up burst. `ResumeAsync` does `UnscheduleAsync` + fresh-from-now `ScheduleAsync` → skips missed fires, resumes at next scheduled occurrence, no burst.
- **D-03:** Resume idempotency already provided by `ResumeAsync`'s `state != TriggerState.Paused → ignore` guard. Pause idempotency provided by `PauseAll()`. No new per-job pause guard needed.
- **D-04:** `[DisallowConcurrentExecution]` on `WorkflowFireJob` (`WorkflowFireJob.cs:29`) relied upon as-is, unchanged.
- **D-05 (SC reword):** SC#4 → pause-all is scheduler-wide idempotent (`PauseAll()`); resume-all is per-job idempotent via the existing `TriggerState == Paused` guard with a fresh-from-now reschedule.
- **D-06:** **Edge-triggered broadcast.** BIT loop tracks previous health state, publishes `PauseAll` once on healthy→unhealthy and `ResumeAll` once on unhealthy→healthy — NOT every tick. Costs one bool of previous-state.
- **D-07 (SC reword):** SC#2 → "each health **transition** fans out a global broadcast."
- **D-08:** Transport = MassTransit `Publish` to a NEW dedicated per-replica fan-out endpoint (e.g. `orchestrator-global-pause`, `InstanceId` + `Temporary = true`), mirroring `Orchestrator/Program.cs:41–50` but **independent** from `orchestrator-pauseresume`. Separate endpoint lets Phase 48 drop the old per-workflow endpoint with zero entanglement.
- **D-09:** Dedicated `IL2HealthGate` DI-singleton, injected into BIT loop (writer) and Phase-46 consumer (reader). API: `Open()` / `Close()` / `WaitForOpenAsync(CancellationToken)`.
- **D-10:** Async reset-event implementation — a swappable `TaskCompletionSource` (open = completed task, close = fresh pending task); `WaitForOpenAsync` awaits the current task. No polling, no busy-wait, no `volatile` flag loop.
- **D-11:** The consumer owns the bound. `WaitForOpenAsync` is awaited inside `Consume` (holding the broker delivery un-acked), so the Phase-46 consumer applies its own bounded timeout via a linked `CancellationTokenSource` well under the RabbitMQ 30-min `consumer_timeout`. Phase 45 builds the gate + wait primitive; Phase 46 sets the exact timeout.
- **D-12:** Gate starts CLOSED until the first successful BIT probe opens it (fail-safe).
- **D-13:** Phase 45 is purely ADDITIVE — defer all removal to Phase 48. The per-workflow `PauseWorkflow`/`ResumeWorkflow` contracts + consumers + `KeeperRecoveryHandler` stay live and untouched.

### Claude's Discretion

- **Probe primitive reuse:** extract `ProbeOnceAsync()→bool healthy` core from `L2ProbeRecovery.RunAsync` (`L2ProbeRecovery.cs:36–48`) — read (`L2[entryId]`) + write-then-delete-scratch (`KeeperProbe`), catching `RedisException`→unhealthy. The v4.0 BIT loop is the *outer* `while`. Refactor-extract, not wholesale reuse. Do **not** catch `Exception`.
- **BIT loop hosting:** `BackgroundService`/`IHostedService` in `Keeper/Program.cs`. Exact class shape, `Probe:DelaySeconds` reuse vs new knob, graceful-shutdown wiring are Claude's.
- **`IL2HealthGate` namespace/placement** and the exact `TaskCompletionSource` swap mechanics (`Interlocked.Exchange`, `RunContinuationsAsynchronously`) are Claude's within D-09/D-10.
- **New contract shape** for `PauseAll`/`ResumeAll`: follow the v4.0.0 no-`H` posture (RETIRE-01) — no `H`/dedup key; a `CorrelationId` for tracing is acceptable.

### Deferred Ideas (OUT OF SCOPE)

- Removal of the per-workflow `PauseWorkflow`/`ResumeWorkflow` path + the reactive `KeeperRecoveryHandler` — **Phase 48 / RETIRE-03**.
- Transition-window coexistence (Phases 45–47): both recovery mechanisms run concurrently during migration; this is the planned overlap, not a Phase-45 concern.
- Exact Phase-46 gate wait bound (concrete `WaitForOpenAsync` timeout under broker `consumer_timeout`) — Phase 46 sets it.
- Phase-46 recovery consumer internals, `_DLQ1` consolidation (Phase 47), per-workflow path removal (Phase 48).
</user_constraints>

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 | Bus, consumers, `Publish`, per-replica endpoint config | [VERIFIED: Directory.Packages.props:137] CPM-pinned; last Apache-2.0 line — **MUST NOT bump** (v9+ commercial) |
| MassTransit.RabbitMQ | 8.5.5 | RabbitMQ transport | [VERIFIED: Directory.Packages.props:138] |
| Quartz.Extensions.Hosting | 3.18.1 | Scheduler, `IScheduler.PauseAll()`, `TriggerState` | [VERIFIED: Directory.Packages.props:96–97] transitively brings Quartz + Quartz.Extensions.DI |
| StackExchange.Redis | 2.13.1 | L2 probe (`IConnectionMultiplexer`), `RedisException` hierarchy | [VERIFIED: Directory.Packages.props:131] already a singleton via `AddBaseConsole` |
| Microsoft.Extensions.Hosting | (net8.0 SDK) | `BackgroundService`, `IHostedService` | [VERIFIED: net8.0 framework] |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Microsoft.Extensions.Options | (net8.0 SDK) | `IOptions<ProbeOptions>` for `Probe:DelaySeconds` | BIT loop delay binding (already bound `Keeper/Program.cs:29`) |
| Microsoft.Extensions.Logging | (net8.0 SDK) | Structured logging (template holes, never interpolate) | Transition + probe-failure logs |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Hand-rolled `AsyncManualResetEvent` (D-10) | `Nito.AsyncEx.AsyncManualResetEvent` | NuGet dep for ~30 lines of code; project has no AsyncEx ref and the Toub pattern is trivial and inspectable. Hand-roll the well-known pattern. |
| `BackgroundService` | bare `IHostedService` | `BackgroundService` gives `ExecuteAsync(stoppingToken)` + base Start/Stop wiring for free. Standard. |
| New `BitOptions` knob | reuse `Probe:DelaySeconds` (`ProbeOptions`) | `ProbeOptions.DelaySeconds` already bound + documented; reuse it (Claude's discretion — grounded default). Note `MaxAttempts` is irrelevant to the BIT outer loop. |

**Installation:** No new packages. All references already present (CPM-pinned). Do **not** add `Nito.AsyncEx` or bump MassTransit.

**Version verification:** All versions read directly from `Directory.Packages.props` (Central Package Management) this session — not from training data. [VERIFIED: repo CPM file]

## Architecture Patterns

### System Architecture Diagram

```
                        KEEPER PROCESS                                ORCHESTRATOR REPLICAS (N)
  ┌─────────────────────────────────────────────┐         ┌──────────────────────────────────────┐
  │                                              │         │  replica A          replica B   ...   │
  │  BitHealthLoop : BackgroundService           │         │                                       │
  │  ┌────────────────────────────────────────┐ │         │  orchestrator-global-pauseresume-{Aid}│
  │  │ while(!stoppingToken):                  │ │         │  orchestrator-global-pauseresume-{Bid}│
  │  │   healthy = ProbeOnceAsync()  ──────────┼─┼──┐      │  (Temporary=true fan-out queues)      │
  │  │   if healthy != prev:        (edge D-06)│ │  │      │            │            │              │
  │  │     if healthy:                         │ │  │      │            ▼            ▼              │
  │  │        gate.Open()                      │ │  │   ┌──┴──┐   PauseAllConsumer (per replica)    │
  │  │        Publish(ResumeAll) ──────────────┼─┼──┼──▶│ Rabbit│  → scheduler.PauseAll()           │
  │  │     else:                               │ │  │   │ fan- │                                    │
  │  │        gate.Close()                     │ │  │   │ out  │   ResumeAllConsumer (per replica)  │
  │  │        Publish(PauseAll)  ───────────────┼─┼──┼──▶│ exch │  → foreach wfId in L1.WorkflowIds  │
  │  │     prev = healthy                      │ │  │   └──────┘      → ResumeAsync(wfId)           │
  │  │   await Task.Delay(DelaySeconds, stop)  │ │  │                    (TriggerState==Paused guard│
  │  └────────────────────────────────────────┘ │  │                     + fresh-from-now reschedule│
  │                                              │  │                     = skip-to-next, no burst)  │
  │  IL2HealthGate (DI singleton, starts CLOSED) │  │      └──────────────────────────────────────┘
  │   - Open()/Close()  ◀── written by loop      │  │
  │   - WaitForOpenAsync(CT) ◀── read by Phase-46│  │      L2 / Redis ◀── ProbeOnceAsync read+write-
  │     consumer (NOT built here, D-11)          │  └──────  then-delete scratch (RedisException→down)
  └─────────────────────────────────────────────┘
```

Primary use case trace: BIT tick probes L2 → on transition only (edge), the loop drives the local gate AND publishes a global broadcast → RabbitMQ fan-out delivers one copy to every replica's own temporary queue → each replica acts on its own in-memory Quartz scheduler + L1.

### Recommended Project Structure
```
src/Keeper/
├── Health/
│   ├── IL2HealthGate.cs          # interface: Open/Close/WaitForOpenAsync(CT)
│   ├── L2HealthGate.cs           # Toub AsyncManualResetEvent impl, starts CLOSED
│   └── BitHealthLoop.cs          # BackgroundService outer while-loop
├── Recovery/
│   └── L2ProbeRecovery.cs        # MODIFY: extract ProbeOnceAsync()→bool core
└── Program.cs                    # register gate + loop (singletons + AddHostedService)

src/Messaging.Contracts/
├── PauseAll.cs                   # no-H control record (CorrelationId only)
└── ResumeAll.cs                  # no-H control record (CorrelationId only)

src/Orchestrator/
├── Consumers/
│   ├── PauseAllConsumer.cs            + PauseAllConsumerDefinition.cs
│   └── ResumeAllConsumer.cs           + ResumeAllConsumerDefinition.cs
├── Scheduling/WorkflowScheduler.cs    # ADD thin PauseAllAsync() seam over raw IScheduler
└── Program.cs                         # register on NEW per-replica fan-out endpoint
```

### Pattern 1: Async Reset-Event Gate (KEEP-03 / D-09/D-10/D-12)
**What:** A manual-reset event whose "wait" is awaitable. Built on a swappable `TaskCompletionSource`: open = a *completed* task, closed = a *fresh pending* task. `Open()`/`Close()` swap the TCS; `WaitForOpenAsync` awaits the current one.
**When to use:** Exactly the BIT gate — one writer (loop), many awaiters (consumer), no polling.
**Example:**
```csharp
// Source: Stephen Toub, "Building Async Coordination Primitives, Part 1: AsyncManualResetEvent"
// https://devblogs.microsoft.com/dotnet/building-async-coordination-primitives-part-1-asyncmanualresetevent/
// Adapted: starts CLOSED (D-12), exposes CancellationToken-aware wait (D-11).
public interface IL2HealthGate
{
    void Open();
    void Close();
    Task WaitForOpenAsync(CancellationToken ct);
}

public sealed class L2HealthGate : IL2HealthGate
{
    // D-12: start CLOSED -> a pending TCS (not yet completed).
    private volatile TaskCompletionSource<bool> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open()
    {
        // Complete the CURRENT tcs. TrySetResult is the standard idempotent set.
        // RunContinuationsAsynchronously (set at creation) means awaiters resume on the
        // thread pool, NOT inline on this Open() call -> no deadlock / no reentrancy into the loop.
        _tcs.TrySetResult(true);
    }

    public void Close()
    {
        // Swap in a fresh PENDING tcs so future WaitForOpenAsync blocks again.
        // Only swap if the current one is already completed (open). Interlocked.CompareExchange
        // makes the read-test-swap atomic against a concurrent Open().
        var current = _tcs;
        if (!current.Task.IsCompleted)
            return; // already closed — idempotent no-op
        var fresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.CompareExchange(ref _tcs, fresh, current);
    }

    public Task WaitForOpenAsync(CancellationToken ct)
    {
        var openTask = _tcs.Task;            // snapshot current state
        if (openTask.IsCompleted) return openTask;     // fast path: already open
        // Compose with the caller's CancellationToken (D-11). The caller (Phase-46) supplies a
        // linked CTS with its own bounded timeout under the broker consumer_timeout.
        return openTask.WaitAsync(ct);       // net8.0 Task.WaitAsync(CancellationToken) throws OCE on cancel
    }
}
```
**Notes on the choices (all D-10 discretion):**
- `RunContinuationsAsynchronously` is mandatory — without it `TrySetResult` runs awaiter continuations *inline* on the BIT-loop thread, which can deadlock or starve the loop. [CITED: devblogs.microsoft.com/dotnet — "always provide RunContinuationsAsynchronously"]
- `Task.WaitAsync(CancellationToken)` is built-in on net8.0 and the clean way to honor D-11's caller bound without leaking a registration. The single shared open-task can be `WaitAsync`'d by many callers independently.
- `Interlocked.CompareExchange` for the swap is the canonical pattern; the BIT loop is single-threaded so contention is theoretical, but the gate is a shared singleton so make it correct.

### Pattern 2: BIT BackgroundService Loop (KEEP-01 / D-06)
**What:** `BackgroundService.ExecuteAsync` outer `while`, one probe per tick, edge-triggered side-effects, `RedisException`-only suppression, graceful shutdown via `stoppingToken`.
**Example:**
```csharp
// Source: Microsoft.Extensions.Hosting BackgroundService pattern (net8.0) + repo RedisException discipline.
public sealed class BitHealthLoop(
    L2ProbeRecovery probe,
    IL2HealthGate gate,
    IBus bus,                              // MassTransit IBus for Publish (fan-out)
    IOptions<ProbeOptions> opts,
    ILogger<BitHealthLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool? prevHealthy = null;          // D-06: previous-state tracker (null = no prior tick)
        var delay = TimeSpan.FromSeconds(opts.Value.DelaySeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var healthy = await probe.ProbeOnceAsync(stoppingToken);   // RedisException caught INSIDE -> bool

            if (prevHealthy != healthy)    // EDGE: only on transition (or first tick)
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
                prevHealthy = healthy;
            }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }   // graceful shutdown — not an error
        }
    }
}
```
**Edge-trigger + first-tick semantics:** With `prevHealthy = null`, the very first probe always counts as a transition, so the first healthy tick opens the gate + broadcasts `ResumeAll` (which is idempotent on the orchestrator — D-03), and the first unhealthy tick closes + broadcasts `PauseAll`. This satisfies D-12 (gate starts CLOSED, opens on first success) without special-casing.
**Graceful shutdown:** Inject `stoppingToken` into `Task.Delay` and `Publish`. On host stop, `Task.Delay` throws `OperationCanceledException` → break the loop cleanly. The base `BackgroundService` already cooperates with `StopAsync`. Do NOT catch the OCE from a genuine shutdown as a probe failure.

### Pattern 3: MassTransit Per-Replica Fan-Out Broadcast (KEEP-02 / D-08)
**What:** A NEW dedicated endpoint where each orchestrator replica gets its OWN temporary auto-delete queue bound to the message-type exchange, so `Publish` reaches *every* replica (fan-out), not one (competing-consumer).
**When to use:** Pause/resume must hit every replica's in-memory scheduler. Exactly the existing `StartOrchestration`/`StopOrchestration` pattern (`Orchestrator/Program.cs:40–43`), on a NEW endpoint name independent of `orchestrator-pauseresume`.
**Example (Orchestrator/Program.cs — mirrors lines 41–50):**
```csharp
// Source: Orchestrator/Program.cs:40-50 (existing per-replica fan-out idiom).
x.AddConsumer<PauseAllConsumer, PauseAllConsumerDefinition>()
    .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
x.AddConsumer<ResumeAllConsumer, ResumeAllConsumerDefinition>()
    .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
```
```csharp
// Definition — NEW endpoint name, NOT "orchestrator-pauseresume" (D-08 independence).
public sealed class PauseAllConsumerDefinition : ConsumerDefinition<PauseAllConsumer>
{
    private readonly IOptions<RetryOptions> _retry;
    public PauseAllConsumerDefinition(IOptions<RetryOptions> retry)
    {
        _retry = retry;
        EndpointName = "orchestrator-global-pauseresume";   // NEW dedicated base name
    }
    protected override void ConfigureConsumer(IReceiveEndpointConfigurator ep,
        IConsumerConfigurator<PauseAllConsumer> c, IRegistrationContext ctx)
    {
        c.ConcurrentMessageLimit = 1;                       // serialize Pause/Resume on this replica
        ep.UseMessageRetry(r => r.Immediate(_retry.Value.Limit));   // Pause def OWNS the shared-endpoint retry
    }
}
// ResumeAllConsumerDefinition: SAME EndpointName, ConcurrentMessageLimit=1, NO second UseMessageRetry
// (per-endpoint retry already owned by Pause def — mirrors ResumeWorkflowConsumerDefinition.cs:13-27).
```
**Publish vs Send:** Use **`Publish`** (KEEP-02/D-08). `Publish` routes to the message-type exchange, which fans out to every bound queue → every replica. `Send` targets ONE queue (competing-consumer) and is wrong for a broadcast. (Contrast: `WorkflowFireJob` uses `Send` to `queue:{processorId}` because work is load-balanced; pause/resume is broadcast.) [VERIFIED: codebase — `Program.cs:53` `ResultConsumer` is competing-consumer (no InstanceId); fan-out consumers all use InstanceId+Temporary]
**Same instanceId closure:** Both new consumers must capture the SAME `instanceId` variable already in `Program.cs:35` so they share ONE temporary fan-out queue `orchestrator-global-pauseresume-{instanceId}` per replica — mirroring how Start+Stop share `orchestrator-{instanceId}`.

### Pattern 4: Quartz Pause-All / Per-Job Resume (ORCH-02 / D-01/D-02/D-03)
**What:** Pause = one scheduler-wide `PauseAll()`; resume = enumerate L1 snapshot, call existing per-job `ResumeAsync` each.
**Example:**
```csharp
// PauseAllConsumer — scheduler-wide, idempotent (D-01/D-03).
public async Task Consume(ConsumeContext<PauseAll> context)
{
    logger.LogWarning("Global PauseAll CorrelationId={CorrelationId}", context.Message.CorrelationId);
    await scheduler.PauseAllAsync(context.CancellationToken);   // thin seam over IScheduler.PauseAll()
}

// ResumeAllConsumer — per-job, NEVER native ResumeAll() (D-02).
public async Task Consume(ConsumeContext<ResumeAll> context)
{
    logger.LogInformation("Global ResumeAll CorrelationId={CorrelationId}", context.Message.CorrelationId);
    foreach (var workflowId in store.WorkflowIds)               // L1 snapshot (IWorkflowL1Store.WorkflowIds)
        await lifecycle.ResumeAsync(workflowId, context.CancellationToken);  // TriggerState==Paused guard inside
}
```
```csharp
// WorkflowScheduler.cs — add a thin seam (mirrors existing PauseAsync at line 116).
/// <summary>Scheduler-wide pause-all (ORCH-02, D-01). Idempotent — re-pausing is a Quartz no-op.</summary>
public Task PauseAllAsync(CancellationToken ct) => scheduler.PauseAll(ct);
```
**Why native `ResumeAll()` is forbidden (D-02 — VERIFIED this session):** Quartz applies trigger misfire instructions when `ResumeAll()` un-pauses [CITED: quartz-scheduler.net troubleshooting + groups.google.com/g/quartznet "all jobs that were missed during the Pause execute"]. Every one-shot trigger here is built `WithMisfireHandlingInstructionFireNow()` (`WorkflowScheduler.cs:47,81`), so all past-due triggers across all workflows fire immediately = cross-workflow herd. `ResumeAsync` instead does `UnscheduleAsync` (DeleteJob) + fresh-from-now `ScheduleAsync` off each workflow's cron (`WorkflowLifecycle.cs:197–198`), sidestepping misfire entirely → skip-to-next, no burst.
**Idempotency (D-03, no new code):** `ResumeAsync` guards `if (state != TriggerState.Paused) return;` (`WorkflowLifecycle.cs:186`). So a duplicate `ResumeAll` redelivery finds already-resumed (Normal) triggers and no-ops each. `PauseAll()` re-pausing already-paused groups is a Quartz no-op. Together = ORCH-02 idempotency with zero new guards.

### ProbeOnceAsync Extraction (Claude's Discretion)
**What:** Pull the single-iteration read + write-then-delete body out of `L2ProbeRecovery.RunAsync`'s `for` loop (`L2ProbeRecovery.cs:36–48`) into a public `ProbeOnceAsync(CancellationToken)→Task<bool>`. The v3.x bounded `MaxAttempts` caller (`RunAsync`) keeps its outer `for` + metrics; the v4.0 BIT loop is a *different* outer `while`. This is a refactor-extract that leaves the existing caller's behavior byte-identical.
**Example:**
```csharp
// Extracted core — RedisException ONLY (D-discretion: a genuine bug must not masquerade as "down").
public async Task<bool> ProbeOnceAsync(CancellationToken ct)
{
    try
    {
        var db = redis.GetDatabase();
        // READ — value need NOT exist (a present/absent read still proves L2 reachable).
        _ = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(/* a fixed BIT entryId */));
        // WRITE-then-DELETE scratch (net-zero, short TTL crash-net).
        var scratch = (RedisKey)L2ProjectionKeys.KeeperProbe(/* a fixed BIT h */);
        await db.StringSetAsync(scratch, "1", expiry: TimeSpan.FromSeconds(30));
        await db.KeyDeleteAsync(scratch);
        return true;                          // both ops, no exception
    }
    catch (RedisException)                    // superset of RedisConnectionException + RedisTimeoutException
    {
        return false;                         // L2 down — NOT a crash
    }
    // Deliberately NO catch(Exception): a genuine bug propagates and is visible, not swallowed as "down".
}
// RunAsync's for-loop now calls ProbeOnceAsync once per attempt, keeping its metrics + delay + outcome shape.
```
**Open question for the planner (OQ-1):** `RunAsync` probes a *specific* `entryId`/`h` passed in from the fault context. The BIT loop has no inbound message — it needs a **fixed sentinel** read key + scratch `h` for the standing probe. The read target need not exist (the read only proves reachability), so a constant BIT key (e.g. a reserved GUID or a dedicated `L2ProjectionKeys.KeeperProbe("bit")` scratch with a throwaway read) is fine. Decide whether `ProbeOnceAsync` takes optional `entryId`/`h` params (so `RunAsync` passes the real ones and the BIT loop passes sentinels) or whether the BIT loop probes scratch-only. Sentinel choice is a small Plan-task decision; it does not change any locked decision.

### Anti-Patterns to Avoid
- **`catch (Exception)` in the probe:** masks genuine bugs as "L2 down" → false pause storms. Catch `RedisException` only (repo discipline, `L2ProbeRecovery.cs:44`).
- **Native `scheduler.ResumeAll()`:** fires the cross-workflow herd (D-02 — VERIFIED). Always per-job `ResumeAsync`.
- **`Send` for the broadcast:** delivers to one replica only. Use `Publish` (D-08).
- **Reusing the `orchestrator-pauseresume` endpoint:** entangles Phase-48 teardown. Use the NEW `orchestrator-global-pauseresume` endpoint (D-08).
- **`TaskCompletionSource` without `RunContinuationsAsynchronously`:** inline continuations on `Open()` deadlock the loop (D-10).
- **Per-tick broadcast:** spams the orchestrator; D-06 is edge-triggered. Track `prevHealthy`.
- **Touching `PauseWorkflow`/`ResumeWorkflow`/`KeeperRecoveryHandler`:** they stay live (D-13). Deleting them breaks the still-live reactive handler's compile.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Awaitable manual-reset gate | A `volatile bool` + `SpinWait`/polling loop | Toub `AsyncManualResetEvent` (swappable TCS) | Polling burns CPU + adds latency; the TCS pattern is allocation-light, instant wake, deadlock-safe with `RunContinuationsAsynchronously` (D-10 mandates this) |
| Background loop hosting | A raw `Task.Run` + manual lifecycle | `BackgroundService` | Free Start/Stop wiring, `stoppingToken`, graceful shutdown integration with the Generic Host |
| Fan-out to all replicas | Manual exchange/queue declaration | MassTransit `Publish` + `InstanceId`+`Temporary=true` endpoint | The framework owns exchange topology + auto-delete temp queues; the repo already proves this pattern (`Program.cs:40-50`) |
| Skip-to-next resume | New misfire-suppression code | Existing `WorkflowLifecycle.ResumeAsync` | Already does Unschedule + fresh-from-now + `TriggerState==Paused` guard. Zero new logic. |
| Pause idempotency | Per-job "pause only if Running" guard | `scheduler.PauseAll()` | Scheduler-wide pause is inherently idempotent (D-03) — re-pausing is a Quartz no-op |
| Probe loop | A second L2 probe implementation | Extract `ProbeOnceAsync` from `L2ProbeRecovery` | Reuses the verified read+write-then-delete + `RedisException` discipline |

**Key insight:** This phase is ~90% composition of existing repo patterns. The only genuinely new primitive is `IL2HealthGate`, and even that is a textbook 30-line Toub pattern. Resist inventing — wire what exists.

## Common Pitfalls

### Pitfall 1: TaskCompletionSource synchronous continuations deadlock the loop
**What goes wrong:** `Open()` calls `TrySetResult`, and an awaiter's continuation runs *inline* on the BIT-loop thread, blocking the next probe.
**Why it happens:** Default `TaskCompletionSource` runs continuations synchronously.
**How to avoid:** Always create with `TaskCreationOptions.RunContinuationsAsynchronously` (D-10). [CITED: devblogs.microsoft.com/dotnet]
**Warning signs:** BIT loop stalls after a consumer starts awaiting; tick cadence drifts.

### Pitfall 2: Native ResumeAll() fires the cross-workflow herd
**What goes wrong:** Using `scheduler.ResumeAll()` un-pauses every past-due one-shot trigger and fires them ALL immediately.
**Why it happens:** Quartz applies misfire instructions on resume; `WithMisfireHandlingInstructionFireNow()` means "fire now if missed." [CITED: quartz-scheduler.net]
**How to avoid:** Per-job `ResumeAsync` (D-02) — Unschedule + fresh-from-now reschedule sidesteps misfire.
**Warning signs:** A burst of `EntryStepDispatch` sends to all processors immediately after an L2 blip clears.

### Pitfall 3: Send instead of Publish → only one replica pauses
**What goes wrong:** `Send` load-balances to one replica's queue; the other replicas keep firing.
**Why it happens:** `Send` = competing-consumer; `Publish` = fan-out.
**How to avoid:** `Publish` + per-replica `Temporary=true` endpoint (D-08). Verify every replica's temp queue binds the `PauseAll`/`ResumeAll` exchange.
**Warning signs:** Some replicas pause, others don't; inconsistent scheduler state across the fleet.

### Pitfall 4: Per-endpoint retry registered twice
**What goes wrong:** Both `PauseAllConsumerDefinition` and `ResumeAllConsumerDefinition` register `UseMessageRetry` on the SAME shared endpoint → double-wrapped retry middleware.
**Why it happens:** `UseMessageRetry` is per-ENDPOINT, not per-consumer; two definitions on one endpoint both try to own it.
**How to avoid:** Only the Pause definition registers `UseMessageRetry`; the Resume definition's `ConfigureConsumer` only sets `ConcurrentMessageLimit=1` (mirror `ResumeWorkflowConsumerDefinition.cs:13-27`).
**Warning signs:** Retry counts double; `_error` queue receives messages after 2N attempts instead of N.

### Pitfall 5: Catch(Exception) in the probe masks bugs as "L2 down"
**What goes wrong:** A `NullReferenceException` or serialization bug is swallowed and reported as unhealthy → false `PauseAll` storm.
**Why it happens:** Over-broad catch.
**How to avoid:** `catch (RedisException)` ONLY (repo discipline). Let genuine bugs propagate (the BIT loop should NOT crash, but a bug must be *visible*, not relabeled "down" — the loop's `ProbeOnceAsync` returns bool only for `RedisException`; any other exception propagates out of `ExecuteAsync` and is logged by the host).
**Warning signs:** Frequent pause/resume churn with no actual Redis outage in the logs.

### Pitfall 6: Bumping MassTransit off 8.5.5
**What goes wrong:** Adding a new consumer tempts a "latest" package bump; v9+ is commercial-licensed.
**Why it happens:** Tooling/IDE "update available" prompts.
**How to avoid:** CPM-pinned at 8.5.5 (last Apache-2.0). Do NOT bump. [VERIFIED: Directory.Packages.props:133-138 doc-comment]

## Code Examples

All load-bearing examples are inline in §Architecture Patterns above (Pattern 1–4 + ProbeOnceAsync). They are sourced from: Stephen Toub's AsyncManualResetEvent blog, the net8.0 `BackgroundService` contract, and the repo's own existing files (`Orchestrator/Program.cs:40-50`, `WorkflowLifecycle.cs:177-199`, `ResumeWorkflowConsumerDefinition.cs`, `L2ProbeRecovery.cs:36-48`).

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Per-workflow reactive pause (`PauseWorkflow`) on `Fault<T>` | Global proactive `PauseAll`/`ResumeAll` from BIT loop | v4.0 (this phase, A14) | Both coexist during Phases 45–47 migration (deferred); reactive removed Phase 48 |
| `volatile bool` + spin/poll gates (pre-async-await era) | Swappable-TCS `AsyncManualResetEvent` | .NET 4.5+ async/await | No busy-wait; instant wake; the D-10 mandate |
| Custom `ManualResetEventSlim.Wait()` blocking a thread | `await WaitForOpenAsync(ct)` non-blocking | async coordination primitives | Frees the consumer thread while gate is closed; the broker delivery stays un-acked (D-11) |

**Deprecated/outdated:** Nothing being deprecated *in this phase* — it is purely additive (D-13).

## Validation Architecture

Nyquist Dimension 8. `workflow.nyquist_validation: true` [VERIFIED: .planning/config.json:19] → section included.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (repo standard — `tests/` projects). Confirm exact version in Wave 0 from `Directory.Packages.props`. |
| Config file | Per-project `.csproj` under `tests/` (no central runsettings observed) |
| Quick run command | `dotnet test --filter "FullyQualifiedName~L2HealthGate|FullyQualifiedName~BitHealthLoop|FullyQualifiedName~PauseAll|FullyQualifiedName~ResumeAll"` |
| Full suite command | `dotnet test` (solution root) |

### Phase Requirements → Test Map (mapped to the 4 ROADMAP success criteria + rewords)

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| KEEP-03 / SC (gate) | Gate starts CLOSED; `WaitForOpenAsync` blocks until `Open()`; `Open()`→completes; `Close()`→re-blocks; `WaitForOpenAsync(ct)` throws OCE on cancel | unit | `dotnet test --filter FullyQualifiedName~L2HealthGateTests` | ❌ Wave 0 |
| KEEP-01 | BIT loop calls `ProbeOnceAsync` each tick; `RedisException`→unhealthy (loop survives); a non-Redis throw propagates (not swallowed); `stoppingToken` ends loop cleanly | unit | `dotnet test --filter FullyQualifiedName~BitHealthLoopTests` | ❌ Wave 0 |
| KEEP-02 / SC#2 (D-07) | Loop publishes `PauseAll` ONCE on healthy→unhealthy and `ResumeAll` ONCE on unhealthy→healthy; NO publish on same-state ticks (edge) | unit (fake `IPublishEndpoint`/MassTransit harness) | `dotnet test --filter FullyQualifiedName~BitHealthLoopBroadcastTests` | ❌ Wave 0 |
| KEEP-02 / SC (fan-out) | Each replica receives its own copy (per-replica temp queue) — verify endpoint config: `InstanceId`+`Temporary=true`, distinct from `orchestrator-pauseresume` | integration (MassTransit `InMemoryTestHarness` per-replica, or config assertion) | `dotnet test --filter FullyQualifiedName~PauseAllEndpointTests` | ❌ Wave 0 |
| ORCH-02 / SC#4 (D-05) part A | `PauseAllConsumer` calls `scheduler.PauseAll()`; re-delivery is a no-op (idempotent) | unit (fake `IScheduler`) | `dotnet test --filter FullyQualifiedName~PauseAllConsumerTests` | ❌ Wave 0 |
| ORCH-02 / SC#4 (D-05) part B | `ResumeAllConsumer` enumerates `store.WorkflowIds` and calls `ResumeAsync` each; resume of a non-Paused trigger is ignored (no burst); resume reschedules fresh-from-now (skip-to-next) | unit (fake L1 store + scheduler) | `dotnet test --filter FullyQualifiedName~ResumeAllConsumerTests` | ❌ Wave 0 |
| ORCH-02 (no-herd) | After pause-then-resume, NO trigger fires immediately; next fire is at the cron's next occurrence | unit (verify `ScheduleAsync` called with from-now `StartAt`, never `ResumeAll()`) | `dotnet test --filter FullyQualifiedName~ResumeNoBurstTests` | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** the quick-filter `dotnet test` for the artifact touched.
- **Per wave merge:** `dotnet test` full suite green.
- **Phase gate:** full suite green before `/gsd-verify-work`.

### Observable validation signals (what to probe/assert + cadence)
- **Gate transitions:** unit-assert state machine (CLOSED→OPEN→CLOSED). Sampling: deterministic — assert each `Open`/`Close` immediately.
- **Broadcast fan-out receipt per replica:** integration-assert each replica's temp queue received exactly one copy. Sampling: once per pause and once per resume event in the harness.
- **Idempotent re-pause no-op:** send `PauseAll` twice → `scheduler.PauseAll()` invoked twice but second is a no-op (verify no exception, scheduler state unchanged).
- **Skip-to-next no-burst resume:** after resume, assert `ScheduleAsync` `StartAt` ≥ now (next cron occurrence), and assert native `ResumeAll()` is NEVER called (the load-bearing negative — a spy on `IScheduler`).
- **Edge-trigger:** drive the loop through healthy,healthy,unhealthy,unhealthy,healthy → assert exactly 1 PauseAll + 1 ResumeAll publish (not 1 per tick).

### Wave 0 Gaps
- [ ] `tests/Keeper.Tests/Health/L2HealthGateTests.cs` — covers KEEP-03 (CLOSED start, open/close/re-block, CT cancel)
- [ ] `tests/Keeper.Tests/Health/BitHealthLoopTests.cs` — covers KEEP-01 + KEEP-02/SC#2 (probe survival, edge-trigger publish)
- [ ] `tests/Orchestrator.Tests/Consumers/PauseAllConsumerTests.cs` + `ResumeAllConsumerTests.cs` — covers ORCH-02/SC#4
- [ ] `tests/Orchestrator.Tests/Consumers/ResumeNoBurstTests.cs` — covers the no-herd negative (no `ResumeAll()`)
- [ ] Confirm a fake/mock `IScheduler` + `IWorkflowL1Store` test double exists or add one (shared fixture)
- [ ] Confirm MassTransit test harness package is referenced in `Orchestrator.Tests` for the fan-out/publish assertions

*(If a `Keeper.Tests` project does not yet exist, Wave 0 also creates it mirroring the existing test-project conventions.)*

## Security Domain

`security_enforcement` absent in config → treated as enabled. This is an internal control-plane phase (no external input, no auth surface) — most ASVS categories are N/A.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No new auth surface; internal bus traffic |
| V3 Session Management | no | Stateless control messages |
| V4 Access Control | no | No user-facing endpoint |
| V5 Input Validation | yes (minimal) | `PauseAll`/`ResumeAll` carry only a `CorrelationId` (Guid) — no free-text. Log via structured template holes, NEVER string interpolation (repo rule T-37-05, see `PauseWorkflowConsumer.cs:25`) |
| V6 Cryptography | no | No secrets/crypto in this phase |
| V7 Error Handling / Logging | yes | Probe failures logged without leaking Redis connection internals; `RedisException`-only catch keeps genuine bugs visible |

### Known Threat Patterns for .NET / MassTransit / Quartz
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Log injection via interpolated message bodies | Tampering | Structured logging template holes only (repo discipline, V5) |
| Spoofed `PauseAll` flooding the fleet (DoS) | Denial of Service | Internal-only bus (no external publisher); edge-trigger (D-06) limits legitimate volume; idempotent consumers absorb redelivery. Out-of-scope to harden further this phase. |
| Probe bug masquerading as outage → false pause storm | Denial of Service | `RedisException`-only catch (Pitfall 5) prevents a code bug from triggering fleet-wide pause |

## Sources

### Primary (HIGH confidence)
- Repo codebase (read this session): `WorkflowScheduler.cs`, `WorkflowLifecycle.cs`, `WorkflowFireJob.cs`, `IWorkflowL1Store.cs`, `L2ProbeRecovery.cs`, `ProbeOptions.cs`, `Keeper/Program.cs`, `Orchestrator/Program.cs`, `PauseWorkflowConsumer(.cs/Definition.cs)`, `ResumeWorkflowConsumer(.cs/Definition.cs)`, `StartOrchestrationConsumerDefinition.cs`, `FaultEntryStepDispatchConsumerDefinition.cs`, `PauseWorkflow.cs`, `KeeperUpdate.cs`, `KeeperQueues.cs`, `ICorrelated.cs`, `Directory.Packages.props`, `.planning/config.json`
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` lines 82–90, 111 (A14) — LOCKED design
- [Stephen Toub — Building Async Coordination Primitives, Part 1: AsyncManualResetEvent](https://devblogs.microsoft.com/dotnet/building-async-coordination-primitives-part-1-asyncmanualresetevent/) — the swappable-TCS gate pattern + RunContinuationsAsynchronously
- [Quartz.NET Troubleshooting](https://www.quartz-scheduler.net/documentation/troubleshooting.html) + [More About Triggers](https://www.quartz-scheduler.net/documentation/quartz-3.x/tutorial/more-about-triggers.html) — misfire-on-resume behavior

### Secondary (MEDIUM confidence)
- [The danger of TaskCompletionSource — Microsoft DevBlogs](https://devblogs.microsoft.com/premier-developer/the-danger-of-taskcompletionsourcet-class/) — RunContinuationsAsynchronously rationale
- [quartznet google group — PauseJob/ResumeJob queue-up behavior](https://groups.google.com/g/quartznet/c/wTV7UFAT5g0) — confirms "missed jobs execute on resume"

### Tertiary (LOW confidence)
- None — all load-bearing claims verified against primary sources or the codebase.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Test framework is xUnit; a `Keeper.Tests` project may or may not exist | Validation Architecture | Low — Wave 0 confirms from `tests/` dir + `Directory.Packages.props`; only affects test-file scaffolding, not production code |
| A2 | `L2ProjectionKeys.KeeperProbe`/`ExecutionData` are reusable for a standing BIT probe with a sentinel key | ProbeOnceAsync extraction | Low — confirmed these methods exist (`L2ProbeRecovery.cs:38-39`); only the *sentinel value* (OQ-1) is open, a small Plan-task decision |
| A3 | MassTransit test harness package is available to `Orchestrator.Tests` for fan-out assertions | Validation Architecture | Low — Wave 0 verifies; falls back to config-assertion tests if absent |

**These are the only unverified items. All architectural/locked-decision claims were verified against the codebase or authoritative docs this session.**

## Open Questions

1. **BIT probe sentinel key (OQ-1)**
   - What we know: `ProbeOnceAsync` needs a fixed read key + scratch `h`; the read target need not exist (read only proves reachability).
   - What's unclear: whether to add optional `entryId`/`h` params (real for `RunAsync`, sentinel for BIT) or have the BIT loop probe scratch-only.
   - Recommendation: parameterize `ProbeOnceAsync(Guid? entryId, string? h, CancellationToken)` with sentinel defaults for the BIT call — keeps `RunAsync` byte-identical while letting the loop pass a reserved BIT key. Decide in Plan.

2. **`Probe:DelaySeconds` reuse vs new BIT knob**
   - What we know: `ProbeOptions.DelaySeconds` (5s) is already bound; `MaxAttempts` is irrelevant to the BIT outer loop.
   - What's unclear: whether the BIT cadence should share the bounded-probe delay or get its own knob.
   - Recommendation: reuse `Probe:DelaySeconds` (Claude's discretion grounded default in CONTEXT). If a separate cadence is later wanted, add `Bit:DelaySeconds` then — not now.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | build/test | ✓ (assumed — repo is net8.0) | net8.0 | — |
| RabbitMQ | broadcast fan-out (runtime) | (runtime, not build) | — | Unit tests use MassTransit in-memory harness — no live broker needed for Wave 0 tests |
| Redis | BIT probe (runtime) | (runtime, not build) | — | Unit tests fake `IConnectionMultiplexer` — no live Redis needed for tests |

**No build-time external dependencies.** All new code is pure .NET + already-referenced (CPM-pinned) packages. RabbitMQ/Redis are runtime-only and unit tests mock both.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all versions read from CPM file this session
- Architecture: HIGH — every pattern maps to an existing repo file or a primary-source idiom; both load-bearing facts (TCS/RunContinuationsAsynchronously, Quartz misfire-on-ResumeAll) independently verified
- Pitfalls: HIGH — derived from verified sources + existing repo discipline comments

**Research date:** 2026-06-08
**Valid until:** 2026-07-08 (stable stack, pinned versions; 30 days)

## RESEARCH COMPLETE
