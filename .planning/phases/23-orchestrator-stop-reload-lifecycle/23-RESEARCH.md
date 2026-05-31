# Phase 23: Orchestrator Lifecycle — L1 Hydration, Quartz Scheduling, Entry-Step Dispatch & Stop Teardown - Research

**Researched:** 2026-05-31
**Domain:** .NET 8 Generic-Host orchestrator console — Quartz.NET scheduling, Cronos fire-time math, MassTransit `Send` dispatch, StackExchange.Redis L2→L1 hydration, startup-gate repoint, concurrency striping
**Confidence:** HIGH (all decisions locked; every code touchpoint read in-repo; external APIs verified against official docs)

## Summary

This phase graduates the orchestrator from a log-only seam (Phases 17–22) into a real runtime lifecycle. The WHAT is locked by `23-SPEC.md` (9 requirements) and the HOW is locked by `23-CONTEXT.md` (D-01..D-17). The research here is **de-risking, not deciding**: it verifies the real Quartz.NET 3.x / Cronos 0.13.0 / MassTransit 8.5.5 APIs against the in-repo shapes, supplies concrete code patterns the planner can turn into executable tasks, and resolves the six "Claude's Discretion" items with verified specifics.

Three findings are load-bearing for planning. First, **Quartz is a NEW dependency** — `Quartz` + `Quartz.Extensions.Hosting` 3.18.1 must be added to `Directory.Packages.props` (CPM) and referenced (no `Version=`) by the Orchestrator csproj; RAMJobStore is Quartz's default in-memory store (no persistent store configured). Second, **Cronos — not Quartz's cron parser — computes fire times** (D-08): the stored cron is 5-field `CronFormat.Standard`, Quartz uses a different 6/7-field grammar, so the job is a self-rescheduling one-shot `SimpleTrigger.StartAt(nextCronosOccurrence)`. Third, **`MarkReady()` moves** from the base library's `StartupCompletionService` (fires on bare host start) to hydration-complete — but `StartupCompletionService` is registered *inside* `AddBaseConsoleHealth` in `BaseConsole.Core`, so the orchestrator must **remove that hosted-service registration by type** (the exact pattern `ConsoleStartupGateTests.NoStartupCompletionConsoleFixture` already uses) and call `MarkReady()` from the hydration `BackgroundService` instead.

**Primary recommendation:** Add Quartz 3.18.1 via CPM; build a self-rescheduling one-shot `SimpleTrigger` keyed by `JobKey(jobId)` with `WithMisfireHandlingInstructionFireNow()`; drive all fire-time math through `Cronos.CronExpression.Parse(cron, CronFormat.Standard).GetNextOccurrence(timeProvider.GetUtcNow().UtcDateTime)`; dispatch via `ISendEndpointProvider.GetSendEndpoint(new Uri($"queue:{processorId:D}"))`; remove `StartupCompletionService` by type and repoint `MarkReady()` to hydration-complete; stripe per-workflowId with a `ConcurrentDictionary<Guid, SemaphoreSlim>` using `Wait(0)` drop-if-held.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions (D-01..D-17 — verbatim intent)

**Carried forward (LOCKED — not re-discussed):**
- **D-01 (message contract):** Define `IExecutionCorrelated : ICorrelated` adding `{ ExecutionId, WorkflowId, StepId, ProcessorId, EntryId }`; the entry-step dispatch message implements it. `executionId`/`entryId` = `Guid.Empty` per SPEC. (`IExecutionCorrelated` confirmed not yet defined; `ICorrelated` = `{ Guid CorrelationId }`.)
- **D-02 (ack split):** Reuse Phase 19 D-07/D-08 verbatim — business failure → a `WorkflowRootNotFoundException`-style type the retry pipeline `Ignore<>`s → log + ack; infra fault → bounded `UseMessageRetry` → `_error`. This is ORCH-ACK-01.
- **D-03 (keys):** Build/read keys via shared `L2ProjectionKeys`. `OrchestratorL2Keys` today only forwards `Root` — add **`ParentIndex()` + `Step()`** reader forwarders.
- **D-04 (clock):** `TimeProvider` is the injected "now" for both the liveness `timestamp` refresh and the cron math.
- **D-05 (per-fire correlation):** mint `correlationId` per job fire with `NewId.NextGuid()` (sequential — Phase 19 D-02).

**Area 1 — L1 store:**
- **D-06:** A **singleton thread-safe `IWorkflowL1Store`** wrapping `ConcurrentDictionary<Guid, WorkflowL1>`, keyed by workflowId. Each entry holds root fields (`entryStepIds`, `cron`, `jobId`, `liveness`) + a steps map (`stepId → step projection`). The SPEC's `{prefix}:wf` / `{prefix}:wf:step` namespacing becomes in-memory nesting. L1 holds NO processor keys and NOT the parent-index key.

**Area 2 — Quartz + cron interpretation:**
- **D-07:** Use **raw Quartz.NET** (`Quartz.Extensions.Hosting`) with a DI-backed job factory + **RAMJobStore** (rebuilt from L2 each startup). Job `JobKey` embeds the workflow's `jobId`; teardown is `DeleteJob(JobKey(jobId))`.
- **D-08 (load-bearing):** **Cronos computes fire times, NOT Quartz's cron parser.** Stored `cron` is a **5-field `Cronos.CronFormat.Standard`** expression (VALID-19 rejects 6-field). The job is a **one-shot scheduled at the next Cronos occurrence; on fire it computes the next occurrence and reschedules itself.**
- **D-09:** Liveness `interval` = the delta **in whole seconds between the next two Cronos occurrences** (`GetNextOccurrence` ×2). Stored in L1 liveness.

**Area 3 — Outbound Send addressing + verification:**
- **D-10:** Dispatch via `ISendEndpointProvider.GetSendEndpoint(new Uri($"queue:{processorId:D}"))` then `Send(message)` — load-balanced `Send` (FUT-SEND-01), NOT `Publish`. One message per entry step; `stepId` = the entry step, `processorId`+`payload` from that step's L1 entry; entry condition irrelevant for entry steps.
- **D-11:** Verify with a **synthetic MassTransit receive endpoint** asserting one message per entry step with correct fields, and `correlationId` differing across fires.

**Area 4 — Lifecycle gating, startup, concurrency:**
- **D-12 (global startup gate = the probe gate):** On boot, **drop** every consumed Start/Stop while initial hydration+scheduling is in progress. The gate opens when initial hydration+scheduling completes — that moment **is `IStartupGate.MarkReady()`**. Move `MarkReady()` from `StartupCompletionService` to **initial-hydration-complete**. `/health/live` stays always-green.
- **D-13 (Redis-at-startup = probe-driven, parity with WebApi):** Hydration runs as a non-blocking `BackgroundService` against the shared `abortConnect=false` soft-dep multiplexer. Redis down → hydration retries (bounded backoff) → gate **never opens** → `/health/startup`+`/health/ready` stay Unhealthy → platform restarts on the startup-probe threshold; `/health/live` stays green. Never fail-fast/crash.
- **D-14 (per-workflowId gate, drop-if-held):** A **per-workflowId try-lock** serializes operations on the same workflowId; different workflowIds run concurrently. While wfX's gate is held, any consumed Start/Stop **for wfX** is **dropped** (first-in-flight wins).
- **D-15 (Start consume = replace, gated):** Acquire wfX's gate → **tolerant teardown** (unschedule + clear L1 if present, silent if absent) → **hydrate L2→L1 + schedule** → release. Re-Start re-applies the current L2 definition. Reuses the Stop teardown as the first half (DRY).
- **D-16 (Stop consume, gated):** Acquire wfX's gate → if present, unschedule + clean up L1; if absent, skip (business no-op) → release.
- **D-17 (ack timing):** Each gated operation runs inside `Consume`, so the message acks only after it completes; transient infra fault throws → retry → `_error`. The receive endpoint is temporary/auto-delete (Phase 19 D-06).

### Claude's Discretion
- Per-workflowId lock implementation (per-key `SemaphoreSlim` / lock striping inside `IWorkflowL1Store`) and its lifecycle (no semaphore leak). → **Resolved: Pitfall 5 + Code Example "Lock striping".**
- Exact shape of the hydration `BackgroundService` retry/backoff and how the startup gate is awaited/dropped-against inside the consumers. → **Resolved: Code Example "Hydration BackgroundService" + "Consumer gate-drop".**
- `WorkflowL1` record shape and where `IExecutionCorrelated` + the dispatch message record live in `Messaging.Contracts`. → **Resolved: Contracts Placement section.**
- Quartz job factory wiring details; misfire policy for the self-rescheduling one-shot. → **Resolved: Code Example "Quartz wiring" — `WithMisfireHandlingInstructionFireNow()`.**
- Whether bulk startup hydration runs sequentially or with bounded parallelism (store thread-safe either way). → **Recommendation: sequential for v1; trivially correct, no race surface; N is small.**
- Test project placement (extend `tests/BaseApi.Tests/Orchestrator/` vs a dedicated project). → **Recommendation: extend `tests/BaseApi.Tests/Orchestrator/` — the Orchestrator project is already referenced there and the in-memory harness pattern lives there.**

### Deferred Ideas (OUT OF SCOPE)
- Periodic reconciliation pass (re-read parent index + roots, converge) — explicit deferred hardening.
- Cross-replica duplicate-dispatch dedup — separate future solution; design must merely not preclude it (ORCH-SCALE-01).
- Processor→orchestrator result round-trip (FUT-SEND-02, FUT-REQRESP-01, round-trip half of FUT-CONTRACTS-01).
- WebApi guard against bare re-Start of an active workflow — optional, not needed.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| ORCH-CONTRACT-01 | Reader-consumable step-projection record in `Messaging.Contracts.Projections` (`entryCondition:int`, `processorId:Guid`, `payload:string`, `nextStepIds:List<Guid>`), camelCase `[property: JsonPropertyName]` | Contracts Placement; Pitfall 1 (positional-record JsonPropertyName); the writer's `StepProjection` uses enum `StepEntryCondition` — reader record uses `int` and round-trips because the enum serializes as its int value |
| ORCH-CONTRACT-02 | Entry-step dispatch message (7 fields, `IExecutionCorrelated : ICorrelated`); `executionId`/`entryId` = `Guid.Empty` | Contracts Placement; `ICorrelated` shape verified; Code Example "Dispatch message record" |
| ORCH-STARTUP-01 | Startup hydrates ALL parent-index workflows into L1 (workflow + step entries only) | Code Example "Hydration BackgroundService"; `L2ProjectionKeys.ParentIndex()` = `skp:` is a Redis SET → `SetMembersAsync` |
| ORCH-SCHED-01 | One in-memory Quartz job per workflow keyed by `jobId`; interval = next-two-fire-times delta seconds | Code Example "Quartz wiring" + "Cronos fire-time math" |
| ORCH-FIRE-01 | Fire → fresh `correlationId`, `Send` to `queue:{processorId}` per entry step, L1 liveness `timestamp` refresh | Code Example "Job Execute"; D-10 addressing verified |
| ORCH-CONSUME-01 | Start consumer hydrates one workflow then schedules+fires | Code Example "Consumer gate-drop"; reuses startup hydration unit |
| ORCH-STOP-01 | Stop consumer resolves `jobId` from L1, `DeleteJob(JobKey(jobId))`, clears L1; zero L2 writes | Code Example "Stop teardown"; Validation Architecture (byte-identical L2 snapshot) |
| ORCH-SCALE-01 | Multi-replica-safe; no global-uniqueness/single-instance assumption | Pitfall 2 (RAMJobStore + replicas); all state per-instance |
| ORCH-ACK-01 | Business=log+ack (Ignore<>'d), infra=throw→retry→`_error`; startup skips corrupt entry | D-02; existing `WorkflowRootNotFoundException` + `StartOrchestrationConsumerDefinition` retry config |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Reader step-projection contract | Messaging.Contracts (leaf) | — | Shared by writer (BaseApi) + reader (Orchestrator); HARDEN-03 single source of truth; leaf has no MassTransit/AspNetCore coupling |
| Entry-step dispatch message + `IExecutionCorrelated` | Messaging.Contracts (leaf) | — | Pure POCO record; both ends reference the leaf |
| L1 in-memory store | Orchestrator (console process) | — | Per-instance runtime state; never persisted; ORCH-SCALE-01 |
| Quartz scheduling (RAMJobStore) | Orchestrator (console process) | — | Per-instance; rebuilt from L2 each boot |
| L2 read (hydration source) | Redis (StackExchange.Redis) | — | Orchestrator is read-only on L2; WebApi owns all writes |
| L2 write / teardown | BaseApi.Service (WebApi) | — | `RedisL2Cleanup` owns ALL L2 mutation; orchestrator NEVER writes |
| Outbound dispatch | MassTransit/RabbitMQ (`Send`) | — | `queue:{processorId}` competing-consumer load-balance |
| Startup/readiness gate | BaseConsole.Core health surface | Orchestrator (repoints `MarkReady`) | Gate latch is base-library; the *moment* it flips is orchestrator-owned this phase |
| Cron fire-time math | Cronos (in-process library) | — | Must match VALID-19 5-field Standard; NOT Quartz's parser |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Quartz | 3.18.1 | In-process job scheduler (RAMJobStore) | `[VERIFIED: nuget.org]` Canonical .NET scheduler; D-07 mandates raw Quartz. **NEW dependency** — not yet in repo |
| Quartz.Extensions.Hosting | 3.18.1 | `AddQuartz` + `AddQuartzHostedService` Generic-Host wiring + MS-DI job factory | `[VERIFIED: nuget.org]` Provides the hosted scheduler + `UseMicrosoftDependencyInjectionJobFactory` default; **NEW** |
| Cronos | 0.13.0 | 5-field cron fire-time computation | `[VERIFIED: Directory.Packages.props line 91]` Already pinned + used by `WorkflowDtoValidator` VALID-19; reuse for D-08/D-09 |
| MassTransit | 8.5.5 | Bus + `ISendEndpointProvider` `Send` | `[VERIFIED: Directory.Packages.props line 131]` Last Apache-2.0 line; do NOT bump to 9.x |
| MassTransit.RabbitMQ | 8.5.5 | RabbitMQ transport + `queue:` short-name addressing | `[VERIFIED: Directory.Packages.props line 132]` |
| StackExchange.Redis | 2.13.1 | L2 read (SMEMBERS/GET) | `[VERIFIED: Directory.Packages.props line 125]` |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Microsoft.Extensions.TimeProvider.Testing (`FakeTimeProvider`) | 8.10.0 | Deterministic cron/liveness "now" in tests | `[VERIFIED: BaseApi.Tests.csproj line 74]` Already referenced; use for D-04 clock tests |
| xunit.v3 / xunit.v3.assert | 3.2.2 | Test framework (MTP runner) | `[VERIFIED: Directory.Packages.props]` |
| NSubstitute | 5.3.0 | Redis multiplexer stubs (`Substitute.For<IConnectionMultiplexer>()`) | `[VERIFIED: BaseApi.Tests.csproj]` Pattern already in `StartStopConsumerAckTests` |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `Quartz.Extensions.Hosting` | `Quartz.Extensions.DependencyInjection` (3.18.1) alone | Hosting transitively includes DI + adds `AddQuartzHostedService`; D-07 wants the hosted scheduler, so Hosting is correct |
| Quartz `CronTrigger` | self-rescheduling one-shot `SimpleTrigger` | LOCKED by D-08 — Quartz cron grammar (6/7-field) misreads the stored 5-field Cronos expression; one-shot keeps VALID-19 semantics |
| `Send` | `Publish` | LOCKED by D-10 — competing-consumer load balance, not broadcast |

**Installation (CPM — add to `Directory.Packages.props`, then reference with NO `Version=`):**
```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="Quartz" Version="3.18.1" />
<PackageVersion Include="Quartz.Extensions.Hosting" Version="3.18.1" />
```
```xml
<!-- src/Orchestrator/Orchestrator.csproj -->
<PackageReference Include="Quartz.Extensions.Hosting" />
```
> Quartz.Extensions.Hosting transitively brings `Quartz` and `Quartz.Extensions.DependencyInjection`; a single PackageReference suffices, but pin both CPM versions for auditability (repo convention — Directory.Packages.props comment block).

**Version verification performed this session:**
- `dotnet package search Quartz` → latest **3.18.1** `[VERIFIED: nuget.org, 2026-05-31]`
- `dotnet package search Quartz.Extensions.Hosting` → **3.18.1** `[VERIFIED: nuget.org]`
- `dotnet --version` → **8.0.421**; repo TargetFramework `net8.0` `[VERIFIED: Orchestrator.csproj comment + global SDK]`
- Cronos pinned **0.13.0** `[VERIFIED: Directory.Packages.props line 91]`; targets net6.0+ — compatible with net8.0 `[CITED: nuget.org/packages/Cronos/0.13.0]`

## Architecture Patterns

### System Architecture Diagram

```
                         ┌──────────────── Orchestrator console (per-replica) ────────────────┐
                         │                                                                     │
  Redis L2 (read-only)   │   ┌─ HydrationBackgroundService (D-13) ──────────────┐             │
  skp: SET (parent idx) ─┼──▶│  SMEMBERS skp:  → [wfId...]                       │             │
  skp:{wf}  (root)       │   │  per wfId: GET root + GET each step key           │             │
  skp:{wf}:{step} (step) │   │  populate L1 ◀──────────┐  schedule Quartz job ───┼──┐          │
                         │   └─ bounded backoff if Redis down; on done ─┐        │  │          │
                         │                                              ▼        │  │          │
                         │                              IStartupGate.MarkReady() │  │          │
                         │                              (/health/startup+ready)  │  │          │
                         │                                                       ▼  ▼          │
                         │   ┌─ IWorkflowL1Store (singleton) ──────────────────────────┐      │
                         │   │  ConcurrentDictionary<Guid, WorkflowL1>                  │      │
                         │   │  + per-wf SemaphoreSlim stripe (Wait(0) drop-if-held)    │      │
                         │   └──────────────▲───────────────────────────▲──────────────┘      │
                         │                  │ resolve jobId/steps        │ refresh liveness    │
  RabbitMQ control bus   │   ┌─ Start/StopOrchestrationConsumer ─┐  ┌─ Quartz Job.Execute ─┐   │
  StartOrchestration ────┼──▶│ if !gate.IsReady → ack/drop (D-12)│  │ fresh correlationId   │   │
  StopOrchestration  ────┼──▶│ Wait(0) wf stripe → else drop     │  │ per entry step:       │   │
                         │   │ Start: teardown→hydrate→schedule  │  │   Send queue:{procId} │   │
                         │   │ Stop:  DeleteJob(JobKey(jobId))   │  │ reschedule next Cronos│   │
                         │   └───────────────────────────────────┘  └──────────┬────────────┘  │
                         └───────────────────────────────────────────────────── │ ─────────────┘
                                                                                 ▼
                                                            ISendEndpointProvider.GetSendEndpoint(
                                                              "queue:{processorId}").Send(msg)
                                                                                 │
                                                                                 ▼
                                                      RabbitMQ  queue:{processorId}  (no real consumer yet;
                                                                synthetic test consumer asserts dispatch)
```

### Recommended Project Structure
```
src/Messaging.Contracts/
├── IExecutionCorrelated.cs              # NEW — IExecutionCorrelated : ICorrelated (D-01)
├── EntryStepDispatch.cs                 # NEW — 7-field dispatch record (ORCH-CONTRACT-02)
└── Projections/
    └── StepProjection.cs                # NEW (hoisted) — reader record, entryCondition:int (ORCH-CONTRACT-01)

src/Orchestrator/
├── Program.cs                           # EDIT — AddQuartz/HostedService, L1 store, hydration svc, remove StartupCompletionService, repoint gate
├── L1/
│   ├── IWorkflowL1Store.cs              # NEW — singleton store + per-wf stripe
│   ├── WorkflowL1Store.cs              # NEW
│   └── WorkflowL1.cs                    # NEW — record { EntryStepIds, Cron, JobId, Liveness, Steps }
├── Scheduling/
│   ├── WorkflowFireJob.cs               # NEW — IJob: dispatch + reschedule
│   ├── WorkflowScheduler.cs            # NEW — schedule/unschedule by JobKey(jobId)
│   └── CronInterval.cs                  # NEW — Cronos next-two-occurrence delta (D-09)
├── Hydration/
│   └── HydrationBackgroundService.cs    # NEW — startup L2→L1 + schedule + MarkReady (D-12/D-13)
├── Consumers/
│   ├── StartOrchestrationConsumer.cs    # EDIT — seam → gated hydrate+schedule+fire
│   └── StopOrchestrationConsumer.cs     # EDIT — seam → gated DeleteJob + L1 clear
└── Messaging/
    └── OrchestratorL2Keys.cs            # EDIT — add ParentIndex() + Step() forwarders
```

### Pattern 1: Cronos fire-time math (D-08/D-09)
**What:** Cronos owns all fire-time computation; `TimeProvider` is "now".
**When:** Initial schedule, reschedule-on-fire, and interval computation.
```csharp
// Source: [VERIFIED in-repo: WorkflowDtoValidator parses identically] + [CITED: github.com/HangfireIO/Cronos]
// GetNextOccurrence(DateTime fromUtc, bool inclusive=false) REQUIRES DateTimeKind.Utc — throws ArgumentException otherwise.
var expr = CronExpression.Parse(cron, CronFormat.Standard);      // 5-field — matches VALID-19 exactly
DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;          // Kind=Utc guaranteed
DateTime? next1 = expr.GetNextOccurrence(nowUtc);                 // nullable: null if no future occurrence
if (next1 is null) { /* business: log + skip scheduling this workflow */ }
DateTime? next2 = expr.GetNextOccurrence(next1.Value);            // strictly after next1 (inclusive=false default)
int intervalSeconds = next2 is { } n2
    ? (int)Math.Round((n2 - next1.Value).TotalSeconds)
    : 0;                                                          // store in L1 liveness.interval (D-09)
// schedule one-shot at next1 (Pattern 2); intervalSeconds → liveness.interval
```

### Pattern 2: Quartz self-rescheduling one-shot (D-07/D-08)
**What:** A `SimpleTrigger` `StartAt(next Cronos occurrence)`, no repeat; the job reschedules itself on fire.
**When:** Every scheduled workflow.
```csharp
// Source: [CITED: quartz-scheduler.net/documentation/quartz-3.x]
var jobKey = new JobKey(workflowId == default ? jobId.ToString("D") : jobId.ToString("D")); // JobKey embeds jobId (D-07)
var job = JobBuilder.Create<WorkflowFireJob>()
    .WithIdentity(jobKey)
    .UsingJobData("workflowId", workflowId.ToString("D"))   // job data flows the wf id (RAMJobStore-serializable string)
    .Build();
var trigger = TriggerBuilder.Create()
    .ForJob(jobKey)
    .StartAt(new DateTimeOffset(nextUtc, TimeSpan.Zero))    // nextUtc = Cronos GetNextOccurrence (Kind=Utc)
    .WithSimpleSchedule(s => s.WithMisfireHandlingInstructionFireNow())  // one-shot: missed fire runs immediately on recovery
    .Build();
await scheduler.ScheduleJob(job, trigger, ct);
// Inside WorkflowFireJob.Execute: after dispatch, compute next Cronos occurrence and ScheduleJob a fresh trigger
// for the SAME jobKey (job already exists → use TriggerBuilder.ForJob(jobKey) + scheduler.ScheduleJob(trigger)).
```
> **Misfire choice (Discretion → resolved):** `WithMisfireHandlingInstructionFireNow()` on the SimpleTrigger means a one-shot that missed its slot (scheduler paused/threadpool-starved) fires immediately on recovery rather than being silently dropped. `[CITED: quartz-scheduler.net SimpleTrigger lesson — MisfirePolicy.SimpleTrigger.FireNow]`. RAMJobStore never misfires across a *restart* (it's rebuilt from L2), so misfire only matters for in-process pauses.

### Pattern 3: `Send` to `queue:{processorId}` (D-10)
**What:** Short-name `queue:` URI dispatch.
```csharp
// Source: [CITED: masstransit.io send addressing] + [VERIFIED in-repo: OutboundCorrelationSendFilter handles SendContext<T>]
var endpoint = await _sendProvider.GetSendEndpoint(new Uri($"queue:{processorId:D}"));
await endpoint.Send(message, ct);   // message : IExecutionCorrelated — the outbound send filter stamps the envelope
```
> The repo already has `OutboundCorrelationSendFilter<T>` bus-wide; it stamps `context.CorrelationId` from the ambient accessor. The dispatch message body ALSO carries `CorrelationId` (per-fire `NewId.NextGuid()`), set on the record before `Send` — body is the source of truth (D-01 body-carried model).

### Anti-Patterns to Avoid
- **Feeding the stored cron into a Quartz `CronTrigger`** — Quartz's 6/7-field grammar misreads/rejects the 5-field Cronos Standard expression. Use Cronos + SimpleTrigger (D-08).
- **Calling `MarkReady()` from `StartupCompletionService`** — that fires on bare host start (current base-library behavior), opening the gate BEFORE hydration. Must move (D-12).
- **`Publish` instead of `Send`** — broadcasts to all processor types; D-10 mandates load-balanced `Send`.
- **Writing to L2** — any `StringSetAsync`/`SetAddAsync`/`KeyDeleteAsync` on a `skp:` key from the orchestrator violates the zero-L2-write invariant. `StartStopConsumerAckTests` already asserts `db.DidNotReceive().StringSetAsync(...)`; extend that guard.
- **`SemaphoreSlim` per operation never disposed** — see Pitfall 5.
- **`Wait()` (blocking, no timeout) on the stripe** — D-14 is *drop-if-held*, use `Wait(0)`/`WaitAsync(0, ct)` returning bool.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Cron next-fire computation | Custom cron field parser | `Cronos.CronExpression` (already pinned) | DST, leap, field-range edge cases; MUST match VALID-19 exactly |
| In-process scheduling | A `Timer`/`PeriodicTimer` loop per workflow | Quartz RAMJobStore (D-07) | Job identity/lookup/delete by key, misfire handling, threadpool — D-07 locks Quartz |
| Per-key concurrency | Global `lock`/static mutex | `ConcurrentDictionary<Guid,SemaphoreSlim>` stripe | A global lock serializes ALL workflows (D-14 wants per-wf) AND would introduce a process-uniqueness smell (ORCH-SCALE-01) |
| Redis SET read | `KEYS skp:*` / SCAN wildcard | `SetMembersAsync(L2ProjectionKeys.ParentIndex())` | Parent index is a real SET (Phase 22); wildcard scans are banned (L2-PROJECT-07) |
| Sequential GUID minting | `Guid.NewGuid()` | `NewId.NextGuid()` (D-05) | Sequential GUIDs (repo correlation convention, Phase 19 D-02) |
| Startup gate latch | A second bool/flag | Existing `IStartupGate`/`StartupGate` | D-12: ONE gate; reuse `BaseConsole.Core.Health.IStartupGate` |

**Key insight:** Almost every primitive this phase needs already exists in-repo (Cronos, `IStartupGate`, the soft-dep multiplexer, `L2ProjectionKeys`, `NewId`, the in-memory test harness). The only genuinely new dependency is Quartz. Hand-rolling any of the above re-introduces a class of bug the prior phases already closed.

## Common Pitfalls

### Pitfall 1: Positional-record `JsonPropertyName` binds to ctor param and STJ ignores it
**What goes wrong:** On a positional record, a bare `[JsonPropertyName]` attribute binds to the constructor *parameter*, not the property, and System.Text.Json silently ignores it → camelCase serialization breaks → the orchestrator deserializes `null`/default fields.
**Why:** STJ reads the attribute from the property, not the ctor param.
**How to avoid:** Use the `[property: JsonPropertyName("...")]` target — exactly as `WorkflowRootProjection`/`LivenessProjection` do. The hoisted `StepProjection` reader record MUST mirror this. `[VERIFIED in-repo: WorkflowRootProjection.cs line 12-16 comment "load-bearing (RESEARCH Pitfall 1)"]`
**Warning signs:** A round-trip unit test where `entryCondition`/`processorId` come back as default.

### Pitfall 2: RAMJobStore + multiple replicas = N× dispatch (ORCH-SCALE-01)
**What goes wrong:** With >1 replica, every replica hydrates all workflows into its own RAMJobStore and fires every job → N duplicate dispatches per fire.
**Why:** RAMJobStore is per-process; there is no cross-replica coordination.
**How to avoid (this phase):** Single active replica assumed at runtime (D-12 fan-out + ORCH-SCALE-01). The *design* must not introduce a global-uniqueness lock that breaks a 2nd replica — but cross-replica dedup is explicitly deferred. Code review confirms no static singleton lock gates the lifecycle.
**Warning signs:** A reviewer asking "what stops two replicas both firing?" — the correct answer is "nothing this phase; deferred reconciliation/dedup; single replica at runtime."

### Pitfall 3: Cronos `DateTimeKind` / UTC
**What goes wrong:** `GetNextOccurrence(DateTime)` throws `ArgumentException` if the DateTime is not `DateTimeKind.Utc`. `DateTime.Now`/`Local`/`Unspecified` all throw.
**Why:** Cronos requires explicit UTC to avoid ambiguity.
**How to avoid:** Always feed `_timeProvider.GetUtcNow().UtcDateTime` (Kind=Utc). Never `DateTime.UtcNow` literal in code (D-04 mandates `TimeProvider`). `[VERIFIED: github.com/HangfireIO/Cronos — throws if kind != Utc]`
**Warning signs:** `ArgumentException: ... must be UTC` at first schedule.

### Pitfall 4: Quartz reschedule race / double-fire on self-reschedule
**What goes wrong:** A one-shot that reschedules itself inside `Execute` can double-fire if the reschedule overlaps the trigger's completion, or if a concurrent Start re-schedules the same `jobKey`.
**Why:** Trigger completion + new-trigger insertion are distinct operations.
**How to avoid:** (a) Mark the job `[DisallowConcurrentExecution]` so a single jobKey never runs two `Execute`s at once. (b) On reschedule, schedule a *new trigger for the existing job* (`TriggerBuilder.ForJob(jobKey)`) rather than re-adding the job; if a Start path concurrently rebuilds, the per-wf stripe (D-14) serializes it. (c) Use `DeleteJob(jobKey)` (removes job + all its triggers atomically) on teardown so no orphan trigger survives.
**Warning signs:** Two dispatches with the same fire timestamp; a test seeing 2× messages per entry step per fire.

### Pitfall 5: SemaphoreSlim leak in the per-key stripe (Discretion → resolved)
**What goes wrong:** A naive `ConcurrentDictionary<Guid,SemaphoreSlim>.GetOrAdd(...)` that creates one semaphore per workflowId and never removes it leaks a `SemaphoreSlim` (a disposable, kernel-backed when waited) per ever-seen workflow.
**Why:** Removing the entry while another thread holds/awaits it is unsafe; never removing leaks.
**How to avoid (recommended):** Because the workflow population is bounded and stable (the parent index), **create the semaphore lazily on first L1 insert and dispose it on L1 removal (Stop/teardown), inside the store under the dictionary's own per-key safety.** Use `Wait(0)`/`WaitAsync(0)` (drop-if-held, D-14). Couple the semaphore lifecycle to the L1 entry lifecycle: `WorkflowL1` (or a sibling map) owns its `SemaphoreSlim`; when the entry is removed from L1, `Dispose()` the semaphore. Since teardown holds the stripe at removal time, dispose-after-release is safe. For absolute safety, prefer **not disposing** at all (the count is bounded by distinct workflows ever scheduled, typically small) — a never-disposed `SemaphoreSlim` that was only ever `Wait(0)`'d holds no kernel handle. Recommendation: lifecycle-couple-and-dispose; fall back to never-dispose if a race is suspected.
**Warning signs:** Growing handle count under repeated Start/Stop churn.

### Pitfall 6: Gate dropping messages during startup must be an ACK, not a fault
**What goes wrong:** Dropping a Start/Stop while the gate is closed (D-12) by throwing → the message faults → retry → eventually `_error`, polluting the error queue with legitimately-early control messages.
**Why:** "Drop" means ack-and-do-nothing, not fault.
**How to avoid:** In the consumer, if `!gate.IsReady`, log + `return` (clean ack). Never throw for the gate-closed case. Likewise the per-wf stripe drop (D-14) is a clean `return`, not a throw. Only infra faults throw (D-02/D-17).
**Warning signs:** `_error` queue messages during boot; retry storms on Start.

### Pitfall 7: `entryCondition` enum-vs-int mismatch
**What goes wrong:** The writer's `StepProjection.EntryCondition` is typed `StepEntryCondition` (an enum), the reader record's is `int`. If the writer ever registers a string-enum converter, the int read breaks.
**Why:** No string-enum converter is registered anywhere — the enum serializes as its underlying int (`PreviousCompleted` → `1`). `[VERIFIED in-repo: StepProjection.cs comment "MUST serialize as an int — no string-enum converter is registered"; StepEntryCondition has explicit int assignments 0-5]`
**How to avoid:** Reader record uses `int EntryCondition` with `[property: JsonPropertyName("entryCondition")]`. A round-trip test must assert a real writer-produced value deserializes to the matching int. The writer refactor to consume the shared record is OUT OF SCOPE (SPEC) — keep the writer's enum-typed record and ADD a reader int-typed record; they serialize byte-identically.
**Warning signs:** A `JsonException` or a wrong int if a converter is ever added; cover with a cross-record round-trip test.

## Code Examples

### Hoisted reader StepProjection (ORCH-CONTRACT-01)
```csharp
// Source: [VERIFIED in-repo: mirrors WorkflowRootProjection.cs exactly]
// File: src/Messaging.Contracts/Projections/StepProjection.cs
using System.Text.Json.Serialization;
namespace Messaging.Contracts.Projections;

public sealed record StepProjection(
    [property: JsonPropertyName("entryCondition")] int EntryCondition,       // int, NOT the enum (writer's enum serializes as its int value)
    [property: JsonPropertyName("processorId")]    Guid ProcessorId,
    [property: JsonPropertyName("payload")]        string Payload,
    [property: JsonPropertyName("nextStepIds")]    List<Guid> NextStepIds);
```

### IExecutionCorrelated + dispatch message (ORCH-CONTRACT-02)
```csharp
// Source: [VERIFIED in-repo: ICorrelated.cs = { Guid CorrelationId }]
// File: src/Messaging.Contracts/IExecutionCorrelated.cs
namespace Messaging.Contracts;
public interface IExecutionCorrelated : ICorrelated
{
    Guid ExecutionId { get; }
    Guid WorkflowId  { get; }
    Guid StepId      { get; }
    Guid ProcessorId { get; }
    Guid EntryId     { get; }
}

// File: src/Messaging.Contracts/EntryStepDispatch.cs   (7 fields per SPEC ORCH-CONTRACT-02)
public sealed record EntryStepDispatch(
    Guid WorkflowId, Guid StepId, Guid ProcessorId, string Payload) : IExecutionCorrelated
{
    public Guid CorrelationId { get; init; }                 // per-fire NewId.NextGuid() (D-05)
    public Guid ExecutionId  { get; init; } = Guid.Empty;    // Guid.Empty per SPEC
    public Guid EntryId      { get; init; } = Guid.Empty;    // Guid.Empty per SPEC
}
```
> No `[JsonPropertyName]` needed on the message record — MassTransit serializes the message envelope itself (this is a bus message, not a Redis JSON projection). Only the *projection* records need camelCase targets.

### Quartz wiring in Program.cs (D-07, D-12)
```csharp
// Source: [CITED: quartz-scheduler.net microsoft-di-integration] + [VERIFIED in-repo: Program.cs Generic-Host shape]
builder.Services.AddQuartz();   // default MS-DI job factory (scoped jobs since 3.3.2); RAMJobStore is the default in-memory store
builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
builder.Services.AddSingleton<IWorkflowL1Store, WorkflowL1Store>();
builder.Services.AddHostedService<HydrationBackgroundService>();   // D-13 — drives MarkReady (D-12)

// CRITICAL (D-12): remove the base library's StartupCompletionService so MarkReady no longer fires on bare host start.
// Pattern proven by ConsoleStartupGateTests.NoStartupCompletionConsoleFixture (remove by TYPE identity, refactor-safe):
foreach (var d in builder.Services
             .Where(d => d.ImplementationType == typeof(BaseConsole.Core.Health.StartupCompletionService))
             .ToList())
    builder.Services.Remove(d);
```
> Jobs get DI services because `AddQuartz` installs `UseMicrosoftDependencyInjectionJobFactory` by default — the job is resolved per fire (scoped). Inject the L1 store (singleton), `ISendEndpointProvider`, `TimeProvider`, and the scheduler/Cronos helper into `WorkflowFireJob`'s constructor. `[CITED: quartz-scheduler.net — "all jobs produced by the default job factory are scoped jobs"]` Singleton deps (L1 store) inject fine into a scoped job.

### Hydration BackgroundService (ORCH-STARTUP-01, D-13)
```csharp
// Source: [VERIFIED in-repo: ConsoleRedisServiceCollectionExtensions soft-dep multiplexer; L2ProjectionKeys]
protected override async Task ExecuteAsync(CancellationToken ct)
{
    var delay = TimeSpan.FromSeconds(1);
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var db = _redis.GetDatabase();                                  // soft-dep; throws if Redis down → caught below
            RedisValue[] ids = await db.SetMembersAsync(OrchestratorL2Keys.ParentIndex());  // skp: SET → SMEMBERS
            foreach (var raw in ids)
            {
                if (!Guid.TryParse(raw, out var wfId)) continue;             // corrupt id → skip (ORCH-ACK-01)
                try { await HydrateAndScheduleAsync(wfId, db, ct); }         // per-wf: GET root + steps, L1 insert, Quartz schedule
                catch (Exception ex) when (IsBusiness(ex))                   // corrupt/missing entry → skip, host stays up (ORCH-ACK-01)
                { _logger.LogWarning(ex, "Skipping corrupt workflow {WorkflowId} during hydration", wfId); }
            }
            _gate.MarkReady();                                              // D-12 — gate flips HERE, not on bare host start
            return;                                                          // initial hydration complete
        }
        catch (Exception ex) when (IsInfra(ex))                            // Redis unreachable → retry, gate stays closed (D-13)
        {
            _logger.LogWarning(ex, "Redis unavailable during hydration; retrying in {Delay}", delay);
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));   // bounded backoff
        }
    }
}
```
> The gate NEVER opens while Redis is down → `/health/startup`+`/health/ready` stay Unhealthy → platform restarts on the startup-probe threshold. `/health/live` (the `"self"` always-Healthy check) is untouched → no self-inflicted crash loop (D-13). `[VERIFIED in-repo: ConsoleHealthServiceCollectionExtensions — "self" tagged "live"; StartupHealthCheck tagged "startup","ready"]`

### Consumer gate-drop + stripe (ORCH-CONSUME-01, D-12/D-14/D-15)
```csharp
// Source: [VERIFIED in-repo: existing consumer shape + StartStopConsumerAckTests ack model]
public async Task Consume(ConsumeContext<StartOrchestration> context)
{
    if (!_gate.IsReady) { _logger.LogInformation("Gate closed — dropping Start (ack)"); return; }   // D-12: ACK, never throw (Pitfall 6)
    foreach (var wfId in context.Message.WorkflowIds)
    {
        if (!_store.TryAcquire(wfId))   // SemaphoreSlim.Wait(0) — drop-if-held (D-14)
        { _logger.LogInformation("Stripe held for {WorkflowId} — dropping (ack)", wfId); continue; }
        try
        {
            await _lifecycle.TeardownAsync(wfId, context.CancellationToken);   // D-15: tolerant teardown (reuses Stop)
            await _lifecycle.HydrateAndScheduleAsync(wfId, context.CancellationToken);  // re-applies current L2 definition
        }
        // business (absent root) → log + continue (Ignore<WorkflowRootNotFoundException>); infra → throw → retry → _error (D-02/D-17)
        finally { _store.Release(wfId); }
    }
}
```

### Stop teardown by jobId (ORCH-STOP-01, D-16)
```csharp
// Source: [VERIFIED in-repo: StopOrchestrationConsumer shape]
public async Task TeardownAsync(Guid workflowId, CancellationToken ct)
{
    if (!_store.TryGet(workflowId, out var wf)) return;          // absent → business no-op (D-16), zero L2 writes
    await _scheduler.DeleteJob(new JobKey(wf.JobId.ToString("D")), ct);   // removes job + all triggers atomically (Pitfall 4c)
    _store.Remove(workflowId);                                  // clears workflow + step entries from L1
    // NO L2 mutation — RedisL2Cleanup (WebApi) owns all L2 teardown
}
```

### Lock striping (Discretion → resolved)
```csharp
// Source: [VERIFIED pattern: ConcurrentDictionary + SemaphoreSlim(1,1) Wait(0)]
private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _stripes = new();
public bool TryAcquire(Guid wfId)
    => _stripes.GetOrAdd(wfId, _ => new SemaphoreSlim(1, 1)).Wait(0);   // drop-if-held: false = held elsewhere
public void Release(Guid wfId)
{
    if (_stripes.TryGetValue(wfId, out var s)) s.Release();
}
// On L1 removal (Stop), optionally: if (_stripes.TryRemove(wfId, out var s)) s.Dispose();  — safe because the
// removing thread holds the stripe; or never-dispose (bounded count). See Pitfall 5.
```

### Synthetic test consumer on `queue:{processorId}` (ORCH-FIRE-01, D-11)
```csharp
// Source: [VERIFIED in-repo: AddMassTransitTestHarness + ReceiveEndpoint pattern; FanOutBroadcastTests / OutboundFilterSyntheticTests]
// [CITED: masstransit.io/documentation/concepts/testing — in-memory harness, Consumed.Any/Select assertions]
var processorId = Guid.NewGuid();
await using var provider = new ServiceCollection()
    .AddSingleton(muxStub)            // PresentL2 stub (existing StartStopConsumerAckTests helper)
    .AddSingleton<IWorkflowL1Store, WorkflowL1Store>()
    .AddLogging()
    .AddMassTransitTestHarness(x =>
    {
        x.AddConsumer<CapturingDispatchConsumer>();                 // synthetic consumer of EntryStepDispatch
        x.UsingInMemory((ctx, cfg) =>
        {
            // Bind the synthetic consumer to the SHORT-NAME queue the orchestrator Sends to:
            cfg.ReceiveEndpoint($"{processorId:D}", e => e.ConfigureConsumer<CapturingDispatchConsumer>(ctx));
            cfg.ConfigureEndpoints(ctx);
        });
    })
    .BuildServiceProvider(true);

var harness = provider.GetRequiredService<ITestHarness>();
await harness.Start();
// ... trigger a fire (call the job's Execute directly, or schedule + advance FakeTimeProvider) ...
Assert.True(await harness.Consumed.Any<EntryStepDispatch>(ct));      // one per entry step
var dispatched = harness.Consumed.Select<EntryStepDispatch>(ct).ToList();
Assert.All(dispatched, d => Assert.Equal(Guid.Empty, d.Context.Message.ExecutionId));
// fire twice → assert the two correlationIds differ (per-fire NewId.NextGuid())
```
> **`queue:` ↔ ReceiveEndpoint name mapping:** `GetSendEndpoint(new Uri("queue:{id}"))` targets a receive endpoint named `{id}` (the short name). On the in-memory harness, `cfg.ReceiveEndpoint("{id}", ...)` creates that exact endpoint, so a `Send` to `queue:{id}` is delivered to the synthetic consumer. On RabbitMQ the same `queue:` short name resolves to the queue `{id}` (no exchange-routing prefix) — `[CITED: masstransit.io send addressing]`. **Landmine:** `queue:` (short name → the queue directly) vs `exchange:`/full URI (would route through an exchange) — D-10's `queue:` is the load-balanced competing-consumer form. Use the GUID `:D` format consistently (matches `L2ProjectionKeys` D-format convention).

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Quartz `IJobFactory` hand-implemented | `AddQuartz()` default MS-DI job factory (`UseMicrosoftDependencyInjectionJobFactory`), scoped jobs | Quartz 3.3.2+ | No custom factory needed; constructor-inject services directly `[CITED: quartz-scheduler.net]` |
| Cronos 5-field only via `Parse(string)` | `Parse(string, CronFormat.Standard)` explicit; `GetNextOccurrence(DateTime, bool inclusive)` | Cronos 0.13.0 | `GetPreviousOccurrence` added 0.13.0 (not needed here); `inclusive` defaults false `[VERIFIED: nuget/github]` |
| MassTransit 8.5.x Apache-2.0 | 9.x is COMMERCIAL | MassTransit 9.0 | Stay on 8.5.5 — do NOT bump (repo-locked) `[VERIFIED: Directory.Packages.props line 127-130]` |

**Deprecated/outdated:**
- Quartz `quartz.config` XML files — superseded by `AddQuartz` fluent DI config; not used here.
- Hand-written `IJobFactory` — superseded by MS-DI factory (default since 3.3.2).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | RAMJobStore is Quartz's default when no persistent store is configured (`UseInMemoryStore`/none) | Standard Stack, Code Examples | LOW — Quartz's documented behavior is in-memory-by-default; if a config requires it explicitly, add `q.UseInMemoryStore()` to `AddQuartz`. The config reference confirmed the property name but not the implicit default; long-standing Quartz behavior is RAMJobStore default. Mitigated by explicit `UseInMemoryStore()` if a test surfaces otherwise. |
| A2 | `cfg.ReceiveEndpoint("{guid:D}")` on the in-memory harness matches a `Send` to `queue:{guid:D}` exactly | Synthetic consumer | LOW — short-name `queue:` → endpoint name is documented MassTransit addressing; verify in the first synthetic-consumer test (fast feedback). |
| A3 | `WithMisfireHandlingInstructionFireNow()` is the correct SimpleTrigger fluent method for one-shot fire-on-recovery | Pattern 2 | LOW — the constant `MisfirePolicy.SimpleTrigger.FireNow` is confirmed; the fluent wrapper name is the standard SimpleScheduleBuilder method. If absent, set `.WithMisfireHandlingInstructionIgnoreMisfires()` (fire ASAP) or the raw constant. Misfire only matters for in-process pauses (RAMJobStore rebuilt on restart). |
| A4 | The enum-typed writer `StepProjection` and the int-typed reader record serialize byte-identically (no string-enum converter registered) | Pitfall 7 | LOW — `[VERIFIED in-repo: StepProjection.cs comment + StepEntryCondition explicit int values]`; cover with a cross-record round-trip test in Wave 0. |

## Open Questions

1. **Does the orchestrator's `appsettings.json` need a Quartz config section, or is `AddQuartz()` (code-only) sufficient?**
   - What we know: D-07 schedules programmatically at runtime, not via config; RAMJobStore is default.
   - What's unclear: whether a threadpool-size override is wanted.
   - Recommendation: code-only `AddQuartz()`; no appsettings section. Default threadpool (10) is ample for the single-replica fire cadence.

2. **Sequential vs bounded-parallel startup hydration?**
   - What we know: the store is thread-safe either way (D-06); Discretion item.
   - Recommendation: sequential `foreach` for v1 — trivially correct, no race surface, N is small (parent-index size). Revisit only if hydration latency becomes a startup-probe concern.

3. **Where does the per-wf stripe live — inside `IWorkflowL1Store` or a sibling service?**
   - What we know: Discretion says "inside `IWorkflowL1Store`".
   - Recommendation: expose `TryAcquire(wfId)`/`Release(wfId)` on `IWorkflowL1Store` so the stripe and the L1 entry share a lifecycle (Pitfall 5 disposal coupling).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | Build/test | ✓ | 8.0.421 | — |
| Quartz / Quartz.Extensions.Hosting | Scheduling | ✗ (NEW) | 3.18.1 (nuget) | none — must add to CPM + csproj |
| Cronos | Fire-time math | ✓ | 0.13.0 (pinned) | — |
| MassTransit / .RabbitMQ | Bus + Send | ✓ | 8.5.5 (pinned) | — |
| StackExchange.Redis | L2 read | ✓ | 2.13.1 (pinned) | — |
| RabbitMQ broker (runtime) | E2E real-broker tests | n/a (tests use in-memory harness) | — | In-memory `AddMassTransitTestHarness` (existing pattern) — no real broker needed for Phase 23 verification |
| Redis (runtime) | Hydration source | n/a for unit tests (NSubstitute mux stubs) | — | `RedisFixture` (real Redis) exists for integration; unit tests stub the multiplexer |

**Missing dependencies with no fallback:**
- Quartz 3.18.1 — a Wave 0 task MUST add the two CPM pins + the Orchestrator `PackageReference` before any scheduling code compiles.

**Missing dependencies with fallback:**
- None — every other dependency is pinned or has an established in-memory test substitute.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 (Microsoft.Testing.Platform runner) |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (MTP: `OutputType=Exe`, `UseMicrosoftTestingPlatformRunner=true`, `TestingPlatformDotnetTestSupport=true`) |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter-class BaseApi.Tests.Orchestrator.*` (MTP filter syntax: `-- --filter-class` when raw) |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |

> Test placement: extend `tests/BaseApi.Tests/Orchestrator/` (the Orchestrator project is already a ProjectReference there; the in-memory harness + Redis-mux-stub patterns live there).

### Phase Requirements → Test Map
| Req ID | Behavior (observable signal) | Test Type | Automated Command | File Exists? |
|--------|------------------------------|-----------|-------------------|-------------|
| ORCH-CONTRACT-01 | Real `skp:{wf}:{step}` JSON deserializes into reader record; `entryCondition` round-trips as int | unit | `--filter-class *.StepProjectionReaderTests` | ❌ Wave 0 |
| ORCH-CONTRACT-02 | Constructed `EntryStepDispatch` has 7 fields; `ExecutionId`/`EntryId` = `Guid.Empty` | unit | `--filter-class *.EntryStepDispatchTests` | ❌ Wave 0 |
| ORCH-STARTUP-01 | After hydration vs N-workflow L2: L1 holds exactly N workflow entries + each one's steps; NO processor key, NO parent-index key | unit (mux stub) | `--filter-class *.HydrationTests` | ❌ Wave 0 |
| ORCH-SCHED-01 | Scheduler has exactly one started (non-paused) job per workflow keyed by `jobId`; L1 liveness `interval` == next-two-fire-times delta seconds | unit (FakeTimeProvider) | `--filter-class *.SchedulingTests` | ❌ Wave 0 |
| ORCH-FIRE-01 | Synthetic consumer on `queue:{processorId}` receives one msg per entry step, correct fields; correlationId differs across 2 fires; L1 liveness `timestamp` advances; transport is `Send` | harness (in-memory) | `--filter-class *.FireDispatchTests` | ❌ Wave 0 |
| ORCH-CONSUME-01 | `StartOrchestration([wfX])` → L1 has wfX only + scheduled job for wfX; synthetic consumer receives wfX entry-step msgs | harness | `--filter-class *.StartConsumerLifecycleTests` | ❌ Wave 0 |
| ORCH-STOP-01 | After Stop(wfX): scheduler lacks the job, L1 has no wfX entries, **L2 snapshot byte-identical before/after** (zero orchestrator L2 writes) | harness | `--filter-class *.StopConsumerLifecycleTests` | ❌ Wave 0 |
| ORCH-SCALE-01 | Code review: no static/global singleton lock or process-uniqueness assumption gates the lifecycle | review + arch test | `--filter-class *.NoGlobalLockTests` (optional reflection guard) | ❌ Wave 0 |
| ORCH-ACK-01 | Absent-workflow Start/Stop → acked, **no `_error` message**; simulated Redis-unreachable consume → faults (propagates); startup with 1 corrupt entry hydrates the rest + host stays up | harness (mux stub: AbsentL2/InfraFaultL2) | `--filter-class *.AckSemanticsTests` | ⚠️ extend existing `StartStopConsumerAckTests` |

**Observable signals (Nyquist sampling targets):**
- Scheduler job count == workflow count (`scheduler.GetJobKeys(GroupMatcher.AnyGroup())`).
- L1 entry count == N; no `skp:` parent-index key and no processor key present in L1.
- Synthetic-consumer captured-message count == entry-step count per fire; `Guid` inequality across two fires' `CorrelationId`.
- L2 byte-identical snapshot before/after Stop (read all `skp:*` values pre/post; assert equal) — proves zero L2 writes.
- Zero `_error`-queue messages on an absent-workflow Stop (harness `Consumed` has no fault).
- `db.DidNotReceive().StringSetAsync(...)` / `SetAddAsync` / `KeyDeleteAsync` on any `skp:` key (extend the existing assertion).

### Sampling Rate
- **Per task commit:** `--filter-class *.Orchestrator.*` (the orchestrator slice).
- **Per wave merge:** full `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj`.
- **Phase gate:** full suite green before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] CPM pins `Quartz` + `Quartz.Extensions.Hosting` 3.18.1 in `Directory.Packages.props`; `PackageReference Include="Quartz.Extensions.Hosting"` in `Orchestrator.csproj` — **blocks all scheduling code**.
- [ ] `StepProjectionReaderTests` — cross-record round-trip (writer enum value → reader int) covers ORCH-CONTRACT-01 + Pitfall 7.
- [ ] `HydrationTests` fixture — Redis-mux stub returning a parent-index SET + roots + steps.
- [ ] `SchedulingTests` — `FakeTimeProvider`-driven Cronos-interval assertion.
- [ ] Synthetic `CapturingDispatchConsumer` + `ReceiveEndpoint("{processorId:D}")` harness helper (reusable across FIRE/CONSUME tests).
- [ ] Extend `StartStopConsumerAckTests` for the gate-drop + corrupt-entry + Redis-unreachable cases (ORCH-ACK-01).

*(Framework already installed — only test files + the Quartz CPM pin are gaps.)*

## Sources

### Primary (HIGH confidence)
- In-repo code (READ this session): `ICorrelated.cs`, `L2ProjectionKeys.cs`, `WorkflowRootProjection.cs`, `LivenessProjection.cs`, `StepProjection.cs` (writer), `StartOrchestrationConsumer.cs`, `StopOrchestrationConsumer.cs`, `StartOrchestrationConsumerDefinition.cs`, `WorkflowRootNotFoundException.cs`, `OrchestratorL2Keys.cs`, `Program.cs`, `IStartupGate.cs`, `StartupHealthCheck.cs`, `StartupCompletionService.cs`, `ConsoleRedisServiceCollectionExtensions.cs`, `ConsoleHealthServiceCollectionExtensions.cs`, `BaseConsoleServiceCollectionExtensions.cs`, `MessagingServiceCollectionExtensions.cs` (console), `OutboundCorrelationSendFilter.cs`, `WorkflowDtoValidator.cs`, `StepEntryCondition.cs`, `RedisL2Cleanup.cs`, `StartStopConsumerAckTests.cs`, `FanOutBroadcastTests.cs`, `OutboundFilterSyntheticTests.cs`, `ConsoleTestHostFixture.cs`, `ConsoleStartupGateTests.cs`, `Directory.Packages.props`, `Orchestrator.csproj`, `BaseApi.Tests.csproj`, `Messaging.Contracts.csproj`.
- `dotnet package search Quartz` / `Quartz.Extensions.Hosting` → 3.18.1 (nuget.org, this session).
- github.com/HangfireIO/Cronos (CronExpression.cs) — `GetNextOccurrence` overloads, UTC requirement, nullable return.

### Secondary (MEDIUM confidence)
- quartz-scheduler.net/documentation/quartz-3.x — hosted-services + microsoft-di-integration + simpletriggers (DI job factory, scoped jobs, misfire constants).
- masstransit.io/documentation/concepts/testing — in-memory `ITestHarness`, `Consumed.Any/Select`, ReceiveEndpoint.
- nuget.org/packages/Cronos/0.13.0 — target frameworks, CronFormat values.

### Tertiary (LOW confidence — flagged in Assumptions Log)
- RAMJobStore-as-default (A1); ReceiveEndpoint↔queue: name mapping on harness (A2); `WithMisfireHandlingInstructionFireNow` fluent name (A3) — all verify cheaply in the first Wave 0/1 tests.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — every version pinned or verified against nuget.org this session.
- Architecture: HIGH — all decisions locked in CONTEXT.md; every touchpoint read in-repo; patterns mirror existing code.
- Pitfalls: HIGH — most derive from in-repo comments (Pitfall 1, 7) or verified external behavior (Pitfall 3); Pitfall 2/5/6 are design-judgement calls grounded in the locked decisions.
- Quartz specifics: MEDIUM-HIGH — DI/RAMJobStore patterns CITED from official docs; the three LOW items are isolated in the Assumptions Log with cheap in-test verification.

**Research date:** 2026-05-31
**Valid until:** 2026-06-30 (stable stack; Quartz 3.x + Cronos 0.13 + MassTransit 8.5 are all settled lines)
