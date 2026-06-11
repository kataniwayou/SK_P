# Phase 52: Three-State Keeper - Research

**Researched:** 2026-06-11
**Domain:** MassTransit 8.5.5 receive-endpoint lifecycle (pause/resume) + Redis recovery-consumer bodies on .NET 8
**Confidence:** HIGH (codebase + installed-assembly verified) / MEDIUM (one MassTransit architectural choice needs a planner decision — see OQ-1)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Single global enum `Recovery:ExhaustionPolicy ∈ {Dlq1, SustainedOutage}` on `RecoveryOptions`, read at startup. Keeper-wide, not per-state (all three states share the one `keeper-recovery` endpoint).
- **D-02:** Default = `Dlq1` when unset (matches v4, A4 single-DLQ, at-least-once; sustained-outage is opt-in).
- **D-03:** Sustained-outage = pure hold/requeue, no dead-letter, accepted spin. No bounded-then-DLQ1 backstop.
- **D-04:** Pause/resume the `keeper-recovery` receive endpoint, driven by the BIT loop / `IL2HealthGate`. Gate-closed → endpoint stops consuming (messages accumulate unconsumed on the broker queue); gate-open → resume + drain. Retires the bounded await-in-`Consume`; eliminates the WR-02 landmine.
- **D-05:** KEEP-04 is unconditional. Gate-closed stays non-destructive in BOTH exhaustion modes. The DLQ1-vs-sustained-outage choice (D-01) governs ONLY op/send failures that occur while the gate is OPEN. KEEP-04 and KEEP-05 are separate invariants — a closed gate never dead-letters, even in DLQ1 mode.
- **D-06:** Absent/empty `L2[entryId]` (no Redis exception) → silent drop / ack. Retire `RecoveryDataGoneException` for REINJECT. A Redis EXCEPTION on the read is still infra → Guard/RetryLoop → exhaustion policy. DELETE already drops-on-absent; INJECT has data in-hand (no presence read).
- **D-07:** Emit a log (Information/Warning) + a counter (e.g. `keeper_reinject_dropped`) on each by-design drop. Observability only — no behavior change.
- **D-08:** Keeper-recovery-endpoint-local scope ONLY. Phase 52 touches only the `keeper-recovery` endpoint (policy-conditional retry/dead-letter wiring + pause/resume). The processor-side latch, the global A18 `UseMessageRetry=none` rule, and the Model-B remnant sweep (RETIRE-03) all stay in Phase 53.
- **D-09:** Remove `RecoveryGateTimeoutException` + `RecoveryOptions.GateWaitSeconds` in-phase (obsoleted by D-04's pause/resume).

### Claude's Discretion
- The exact MassTransit mechanism for endpoint pause/resume (`HostReceiveEndpointHandle` Stop/Start, control bus, receive-endpoint connector, or `ConcurrentMessageLimit` gating) — resolved below (OQ-1).
- The exact startup-time conditional endpoint config for the two exhaustion modes — resolved below (Pattern 2).
- Whether `INJECT`/`DELETE` reuse `RecoveryConsumerBase.Guard`/`RetryLoop` unchanged and how much of the base survives — resolved below (§Reusable Asset Survival).
- Metric instrument type/name for the reinject-drop counter — resolved below (Pattern 3).
- The hermetic-fact decomposition — resolved below (Validation Architecture).

### Deferred Ideas (OUT OF SCOPE)
- Global `UseMessageRetry=none` rule + processor-side dead-letter latch removal → Phase 53.
- Model-B remnant sweep (RETIRE-03) → Phase 53.
- Auto-resume sweep into a recovered L2 after sustained-outage hold (`FUTURE-KEEPER-SWEEP`) → out of v5.0.0.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| KEEP-01 | REINJECT reads source `entryId` (drops if absent), re-injects reconstructed `EntryStepDispatch` carrying original `Payload` to processor input | `ReinjectConsumer` v4 body already does the read+reconstruct+send; only the absent-key path changes (throw → drop+metric, D-06/D-07). §Reusable Assets, Pattern 3, Code Example 1 |
| KEEP-02 | INJECT (forward-only, data in-hand) writes `L2[entryId]=data`, sends reconstructed `StepCompleted`, deletes `deleteEntryId` | `InjectConsumer` is a no-op stub to implement. All three ops go through `Guard`. Contracts (`KeeperInject`, `StepCompleted`) final. §Code Example 2, §Don't Hand-Roll |
| KEEP-03 | DELETE deletes the L2 key; drops if absent | `DeleteConsumer` already does exactly this (`KeyDeleteAsync` no-ops on missing key — VERIFIED). Likely zero code change; verify against A18. §Reusable Assets |
| KEEP-04 | L2 op only when BIT gate open; gate-closed → non-destructive consume (pause/requeue, accumulate, drain) | Pause/resume `keeper-recovery` endpoint via MassTransit receive-endpoint handle. OQ-1, Pattern 1, §State of the Art |
| KEEP-05 | Configurable exhaustion: DLQ1 (dead-letter to `skp-dlq-1`) vs sustained-outage (hold/requeue, no dead-letter) | Startup `ExhaustionPolicy` enum drives conditional endpoint config. Pattern 2 |
</phase_requirements>

## Summary

Phase 52 fills in the three real keeper recovery bodies (REINJECT/INJECT/DELETE) and replaces the bounded await-in-`Consume` gate mechanism (Phase-46 D-03) with a MassTransit receive-endpoint pause/resume driven by the BIT loop. Two of the three bodies are nearly done already: `DeleteConsumer` is complete (drops-on-absent via `KeyDeleteAsync` is VERIFIED at `DeleteConsumer.cs:20` + `L2ProjectionKeys.cs`), and `ReinjectConsumer` has a working v4 body whose ONLY change is flipping the absent-key path from a thrown `RecoveryDataGoneException` to a silent ack + log + counter (D-06/D-07). `InjectConsumer` is a genuine no-op stub (`InjectConsumer.cs:22`) requiring the full forward-only `write → send StepCompleted → delete` body.

The single hard problem is the pause/resume mechanism (OQ-1). I verified against the installed MassTransit 8.5.5 assembly XML that `HostReceiveEndpointHandle.StopAsync` **removes** the endpoint from the host and it "cannot be restarted using the ReceiveEndpoint property directly" — so the naive "stop the statically-configured endpoint and start it again" does not work in 8.5.5. The maintainer-blessed pattern (GitHub discussion #3549, phatboyg) is `IReceiveEndpointConnector.ConnectReceiveEndpoint` — dynamically connect the endpoint and Stop/Start the returned handle — but this **cannot coexist with a statically pre-configured endpoint** ("choose one approach or the other"). The `keeper-recovery` endpoint is currently statically configured via `AddConsumer<…>(definition)` in `Program.cs:47-49`. **This is the one decision the planner must make: switch `keeper-recovery` from static `AddConsumer` registration to a dynamic `ConnectReceiveEndpoint` connected at startup, so the handle is Stop/Start-able.** The exhaustion policy and the three bodies are otherwise straightforward.

**Primary recommendation:** Implement the two bodies (INJECT full, REINJECT absent-path flip) reusing `Guard`/`RetryLoop` unchanged; remove the gate-wait/`GateWaitSeconds`/`RecoveryGateTimeoutException` from the base; convert the `keeper-recovery` endpoint to a `ConnectReceiveEndpoint`-managed handle stored in a singleton and Stop/Start-driven by `BitHealthLoop`; add an `ExhaustionPolicy` enum that conditionally wires the dead-letter path; add a new Keeper meter for the drop counter.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| REINJECT/INJECT/DELETE state bodies | Keeper consumer (`src/Keeper/Recovery/*`) | Redis (L2 data), RabbitMQ (sends) | The keeper owns the recovery-state machine; L2 and broker are downstream effectors |
| Gate-open-only execution (KEEP-04) | Keeper messaging endpoint lifecycle (`keeper-recovery`) | BIT loop (`BitHealthLoop`) as driver | Pausing consumption at the transport (not inside `Consume`) is an endpoint-lifecycle concern; the BIT loop is the existing health authority |
| Exhaustion policy (KEEP-05) | Keeper endpoint config (`ReinjectConsumerDefinition` / startup) | — | DLQ1 vs sustained-outage is endpoint retry/dead-letter wiring, a startup-time decision |
| Drop observability (D-07) | Keeper observability (new `KeeperMetrics`) | OTel meter provider registration | Counter is code-owned by the keeper, exported via the inherited metrics-only OTel pipeline |
| BIT health → pause/resume coupling | `BitHealthLoop` (BackgroundService) | `IL2HealthGate` (state), endpoint handle (effector) | The loop already edge-triggers global PauseAll/ResumeAll (A14); driving the keeper's OWN endpoint is the same edge |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 | Bus, receive endpoints, partitioner, retry/dead-letter | Already the project's bus; 8.5.5 is the last Apache-2.0 line (CPM-pinned, `Directory.Packages.props`) [VERIFIED: Directory.Packages.props] |
| StackExchange.Redis (`IConnectionMultiplexer`/`IDatabase`) | (project-pinned) | L2 read/write/delete | The keeper's existing L2 client, DI singleton from `AddBaseConsole` [VERIFIED: RecoveryConsumerBase.cs:26-34] |
| System.Diagnostics.Metrics (`Meter`/`Counter<T>`, via `IMeterFactory`) | net8.0 BCL | The D-07 drop counter | The blessed .NET 8 DI metrics pattern used by `ProcessorMetrics`/`OrchestratorMetrics` [VERIFIED: ProcessorMetrics.cs] |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| MassTransit.Testing (`ITestHarness`, `AddMassTransitTestHarness`) | 8.5.5 | In-memory hermetic facts (dead-letter routing, pause/accumulate) | The existing `RecoveryDeadLetterFacts` harness pattern [VERIFIED: RecoveryDeadLetterFacts.cs] |
| NSubstitute | (project-pinned) | `IDatabase`/`ISendEndpointProvider`/`ConsumeContext<T>` doubles | The established `RecoveryTestKit` fact style [VERIFIED: RecoveryTestKit.cs] |
| xunit | (project-pinned) | Fact runner; `[Trait("Phase", "52")]` tagging | Project convention [VERIFIED: all *Facts.cs] |

### Alternatives Considered (for the pause/resume mechanism — OQ-1)
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `ConnectReceiveEndpoint` handle Stop/Start | `HostReceiveEndpointHandle.StopAsync` on a statically-configured endpoint | REJECTED: 8.5.5 `StopAsync` REMOVES the endpoint; "cannot be restarted using the ReceiveEndpoint property directly" [CITED: MassTransit.Abstractions.xml:6711-6717]. No clean resume. |
| `ConnectReceiveEndpoint` handle Stop/Start | Keep bounded await-in-`Consume` (Phase-46 D-03) | REJECTED by D-04: reintroduces the WR-02 `consumer_timeout` landmine (a parked `Consume` holds an in-flight unacked delivery the broker can force-close). |
| `ConnectReceiveEndpoint` handle Stop/Start | `ConcurrentMessageLimit`/`PrefetchCount = 0` gating | REJECTED: not dynamically mutable at runtime in 8.5.5 (config-time `IEndpointDefinition` properties) [VERIFIED: MassTransit.Abstractions.xml:1607-1616]; and PrefetchCount=0 still leaves the consumer registered. |
| `ConnectReceiveEndpoint` handle Stop/Start | Kill Switch middleware | REJECTED: kill switch trips on a consumer **failure threshold** and auto-restarts after a fixed cooldown — it is not externally drivable by an L2-health signal. [CITED: masstransit.io killswitch] |

**Installation:** No new packages. (D-07 counter uses BCL `System.Diagnostics.Metrics` + the already-present OTel meter provider.)

**Version verification:** MassTransit 8.5.5 confirmed pinned in `Directory.Packages.props` (`<PackageVersion Include="MassTransit" Version="8.5.5" />`) [VERIFIED: Directory.Packages.props]. Assembly XML inspected at `~/.nuget/packages/masstransit{,.abstractions}/8.5.5/lib/net8.0/`.

## Architecture Patterns

### System Architecture Diagram

```
                         ┌─────────────────────────────────────────┐
                         │  BitHealthLoop (BackgroundService)        │
   Redis probe tick ───► │  ProbeOnceAsync → healthy? (edge-trigger) │
                         └───────────────┬───────────────┬──────────┘
                                         │ healthy edge   │ unhealthy edge
                                         ▼                ▼
                         gate.Open()              gate.Close()
                         endpointHandle.Start()   endpointHandle.Stop()   ◄── NEW (D-04)
                         bus.Publish(ResumeAll)   bus.Publish(PauseAll)   ◄── existing A14
                                         │                │
                                         ▼                ▼
        ┌──────────────────────────────────────────────────────────────────┐
        │  keeper-recovery receive endpoint (ConnectReceiveEndpoint handle)  │
        │  STARTED → basic.consume active → messages delivered & drained     │
        │  STOPPED → basic.cancel → NO in-flight delivery → msgs accumulate  │
        │            on the broker queue (no consumer_timeout exposure)      │
        └───────────────┬───────────────┬───────────────┬──────────────────┘
                        │               │               │   (UsePartitioner on 4-tuple
                        ▼               ▼               ▼    serializes same-exec states)
              ┌──────────────┐ ┌──────────────┐ ┌──────────────┐
              │ ReinjectCon. │ │ InjectCon.   │ │ DeleteCon.   │
              │ read L2[eid] │ │ write L2[eid]│ │ del L2[eid]  │
              │  absent→DROP │ │ send Step-   │ │ absent→no-op │
              │  +log+metric │ │  Completed   │ │ (drop)       │
              │ present→send │ │ del L2[delId]│ │              │
              │ EntryStep-   │ │              │ │              │
              │ Dispatch     │ │              │ │              │
              └──────┬───────┘ └──────┬───────┘ └──────────────┘
                     │ Guard/RetryLoop on every L2 op + Send
                     ▼ exhausted → ExhaustionPolicy switch:
            ┌────────────────────────────────────────────┐
            │ Dlq1            → throw → UseMessageRetry    │
            │                   exhausted → skp-dlq-1      │
            │ SustainedOutage → hold/requeue, NO dead-let. │
            └────────────────────────────────────────────┘
                     │ REINJECT→ queue:{ProcessorId:D}  │ INJECT→ orchestrator result queue
```

### Recommended Project Structure
```
src/Keeper/
├── Recovery/
│   ├── RecoveryConsumerBase.cs   # REMOVE gate-wait + RecoveryGateTimeoutException (D-09); KEEP Guard/RetryLoop
│   ├── ReinjectConsumer.cs       # FLIP absent path: throw → drop + log + metric (D-06/D-07)
│   ├── InjectConsumer.cs         # IMPLEMENT full forward-only body (KEEP-02)
│   ├── DeleteConsumer.cs         # NO CHANGE expected (verify vs A18)
│   ├── ReinjectConsumerDefinition.cs  # ADD ExhaustionPolicy-conditional dead-letter wiring
│   ├── RecoveryDataGoneException.cs    # DELETE if no remaining users (D-06); verify INJECT didn't reuse it
│   └── RecoveryGateTimeoutException.cs # (lives in RecoveryConsumerBase.cs) DELETE (D-09)
├── Health/
│   └── BitHealthLoop.cs          # ADD endpoint Stop/Start on the existing health edges (D-04)
├── Observability/
│   └── KeeperMetrics.cs          # NEW (re-create; was deleted in Phase 48) — drop counter (D-07)
├── RecoveryOptions.cs            # ADD ExhaustionPolicy enum (D-01); REMOVE GateWaitSeconds (D-09)
└── Program.cs                    # CONVERT keeper-recovery to ConnectReceiveEndpoint; register handle singleton + KeeperMetrics meter
```

### Pattern 1: Pause/resume a receive endpoint via a connected handle (KEEP-04 / D-04)

**What:** Connect the `keeper-recovery` endpoint dynamically at startup via `IReceiveEndpointConnector`, store the returned `HostReceiveEndpointHandle` in a singleton, and call `handle.ReceiveEndpoint.Stop(ct)` / `.Start(ct)` from `BitHealthLoop` on the existing health edges. A stopped endpoint issues `basic.cancel` to RabbitMQ — there is no in-flight unacked delivery, so messages accumulate on the queue and are NOT exposed to `consumer_timeout`. `.Start(ct)` resumes consumption and drains the backlog.

**When to use:** Exactly this phase. This is the maintainer-recommended runtime pause/resume mechanism for a single endpoint in MassTransit 8.

**CRITICAL constraint (planner decision):** `ConnectReceiveEndpoint` and a static pre-configured endpoint are mutually exclusive ("you can't connect a receive endpoint that's already configured" — phatboyg, #3549). The current `keeper-recovery` is static (`AddConsumer<…>(def)` in `Program.cs:47-49`). The planner MUST convert it: add the three consumers WITHOUT auto-configuring their endpoint, then `ConnectReceiveEndpoint("keeper-recovery", …)` once at startup (after bus start), applying the same `UseMessageRetry` + three `UsePartitioner` + ExhaustionPolicy wiring inside the connect callback that `ReinjectConsumerDefinition.ConfigureConsumer` applies today.

**Example (shape — verify exact connect callback against 8.5.5 at implementation time):**
```csharp
// Source: MassTransit 8.5.5 IReceiveEndpointConnector (MassTransit.xml:7732-7765);
//         GitHub discussion #3549 (phatboyg). Pattern is CITED, exact callback to be confirmed in-phase.
var connector = bus.GetService<IReceiveEndpointConnector>()  // DI-registered in the container
                ?? throw new InvalidOperationException();
var handle = connector.ConnectReceiveEndpoint(KeeperQueues.Recovery, (ctx, cfg) =>
{
    cfg.UseMessageRetry(r => r.Immediate(retry.Limit));           // Dlq1 mode only (Pattern 2)
    var partition = new Partitioner(opts.PartitionCount, new Murmur3UnsafeHashGenerator());
    cfg.UsePartitioner<KeeperReinject>(partition, p => PartitionGuid(p.Message));
    cfg.UsePartitioner<KeeperInject>(partition, p => PartitionGuid(p.Message));
    cfg.UsePartitioner<KeeperDelete>(partition, p => PartitionGuid(p.Message));
    cfg.ConfigureConsumer<ReinjectConsumer>(ctx);
    cfg.ConfigureConsumer<InjectConsumer>(ctx);
    cfg.ConfigureConsumer<DeleteConsumer>(ctx);
});
await handle.Ready;                       // started + ready to consume
// store `handle` in a singleton so BitHealthLoop can drive Stop/Start
// later, on edge:  await handle.ReceiveEndpoint.Stop(ct);  /  await handle.ReceiveEndpoint.Start(ct);
```

**Verified API surface (installed 8.5.5):**
- `IReceiveEndpoint.Start(CancellationToken)` / `IReceiveEndpoint.Stop(CancellationToken)` [VERIFIED: MassTransit.Abstractions.xml:7306, 7313]
- `HostReceiveEndpointHandle.Ready` (Task; completed when ready) [VERIFIED: MassTransit.Abstractions.xml:6706]
- `HostReceiveEndpointHandle.StopAsync(CancellationToken)` — REMOVES the endpoint (do NOT use for pause) [VERIFIED: MassTransit.Abstractions.xml:6711]
- `IReceiveEndpointConnector.ConnectReceiveEndpoint(string, Action<IBusRegistrationContext, IReceiveEndpointConfigurator>)` [VERIFIED: MassTransit.xml:7759]

### Pattern 2: Exhaustion-policy-conditional dead-letter wiring (KEEP-05 / D-01..D-03)

**What:** A startup `ExhaustionPolicy` enum decides whether the endpoint wires a dead-letter path:
- **`Dlq1`** (default): keep `endpointConfigurator.UseMessageRetry(r => r.Immediate(limit))` exactly as today. An exhausted Guard re-throws → retry budget consumed → the inherited `ConsolidatedErrorTransportFilter` moves it to `skp-dlq-1` (the existing mechanism — VERIFIED in `RecoveryDeadLetterFacts`).
- **`SustainedOutage`**: do NOT consume the retry-then-dead-letter path. The simplest faithful realization of A18 "hold/requeue and wait for L2 recovery, no dead-letter": configure the endpoint so an exhausted/thrown delivery is **redelivered to the queue** (broker requeue) rather than routed to `skp-dlq-1`. In practice this means NOT wiring the consolidated error move for this endpoint and letting the thrown exception nack-with-requeue (MassTransit default redelivery), so the message spins on the queue until the gate-open path succeeds.

**When to use:** Read `ExhaustionPolicy` once at startup; branch the connect callback (Pattern 1) accordingly. Because the policy is a startup enum, this is a compile-time-shaped `if`, not per-message logic.

**IMPORTANT (separation from KEEP-04, per D-05):** This policy governs ONLY op/send exhaustion while the gate is OPEN. The pause/resume (Pattern 1) handles gate-closed non-destructive consume independently — in BOTH modes a closed gate simply isn't consuming, so nothing dead-letters. Do not conflate the two switches.

**Open mechanism detail (flag for in-phase verification):** The exact 8.5.5 call to express "requeue, no dead-letter" needs confirmation against the assembly — candidates are (a) simply omitting the `ConfigureError`/consolidated move so the thrown exception nacks-requeue, vs (b) an explicit redelivery filter. The DLQ1 path is fully proven (existing `RecoveryDeadLetterFacts`); the sustained-outage path is the newer surface. [ASSUMED] that omitting the consolidated error move yields broker requeue — confirm at implementation.

### Pattern 3: Drop counter via a code-owned Keeper meter (D-07)

**What:** Re-create `src/Keeper/Observability/KeeperMetrics.cs` (a `KeeperMetrics` existed but was DELETED as orphaned in Phase 48 — `48-01-PLAN.md`). Model it on `ProcessorMetrics`: `IMeterFactory`-built (NEVER a `static Meter`), one `Counter<long>`, snake_case name with NO Prometheus suffix (the collector's `add_metric_suffixes` appends `_total` itself). Register the meter name via `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(KeeperMetrics.MeterName))` in `Program.cs`. Increment in `ReinjectConsumer` on the by-design absent-key drop.

**Naming:** `keeper_reinject_dropped` (snake_case, no suffix) — matches `processor_dispatch_deduped` convention [VERIFIED: ProcessorMetrics.cs:47].

**Example:**
```csharp
// Source: ProcessorMetrics.cs (the project's blessed IMeterFactory pattern) [VERIFIED]
public sealed class KeeperMetrics
{
    public const string MeterName = "Keeper";   // pick the keeper meter name; register via AddMeter(MeterName)
    public Counter<long> ReinjectDropped { get; }
    public KeeperMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        ReinjectDropped = meter.CreateCounter<long>("keeper_reinject_dropped");  // collector appends the suffix
    }
}
```

### Anti-Patterns to Avoid
- **`HostReceiveEndpointHandle.StopAsync` for pause:** removes the endpoint; no clean resume in 8.5.5. Use `IReceiveEndpoint.Stop/Start` on a `ConnectReceiveEndpoint` handle instead.
- **`static Meter` for the drop counter:** leaks across the shared hermetic test process. Use `IMeterFactory` (ProcessorMetrics convention).
- **Per-consumer `ConfigureError`/`SetQueueArgument` in the connect callback:** Pitfall 3 — give-ups inherit the consolidated `skp-dlq-1` route from BaseConsole.Core's once-per-endpoint filter. Keep DLQ1 mode relying on that inherited filter.
- **Registering retry/partitioner more than once for the shared endpoint:** still single-owner. In the connect-callback model there is ONE callback, so register the retry+partitioner ONCE inside it (the three `ConsumerDefinition`s' `ConfigureConsumer` no-op concern dissolves — see §Reusable Asset Survival).
- **Treating gate-closed drop as a dead-letter:** D-05 — a closed gate never dead-letters. Closed = not consuming, full stop.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Pause/resume a single endpoint | A custom `volatile bool consuming` flag checked inside `Consume` (the parked-await pattern) | `IReceiveEndpoint.Stop/Start` on a connected handle | The parked-`Consume` approach is exactly the WR-02 landmine D-04 retires (holds an unacked delivery → broker `consumer_timeout` force-close + channel-wide requeue) [CITED: rabbitmq.com/docs/consumers; MassTransit #4235] |
| Retry/dead-letter on op exhaustion | A bespoke try/catch counter | Existing `Guard`/`RetryLoop` + endpoint `UseMessageRetry` + `ConsolidatedErrorTransportFilter` | Already built, proven by `RecoveryDeadLetterFacts`; reuse unchanged [VERIFIED] |
| Absent-key detection on delete | A pre-read `KeyExists` then conditional delete | `KeyDeleteAsync` (no-ops on missing key) | `DeleteConsumer` already relies on this; one round-trip, race-free [VERIFIED: DeleteConsumer.cs:20] |
| Presence check that pulls the blob | `StringGetAsync` then length check | `StringLengthAsync` (STRLEN) — REINJECT only | STRLEN returns 0 for absent OR empty without transferring the blob (existing IN-04 convention) [VERIFIED: ReinjectConsumer.cs:30-34] |
| Metric instrument lifecycle | `static Meter` field | `IMeterFactory.Create` | Blessed .NET 8 DI pattern; no cross-test leakage [VERIFIED: ProcessorMetrics.cs] |

**Key insight:** Nearly everything this phase needs already exists in the codebase; the phase is "wire the existing pieces into the A18 3-state shape + swap the gate mechanism," not "build new infrastructure." The ONLY genuinely new surface is the `ConnectReceiveEndpoint` handle plumbing.

## Reusable Asset Survival (verified against the real files)

| Asset | Survives? | Evidence |
|-------|-----------|----------|
| `RecoveryConsumerBase.Guard<T>` / `Guard` | YES, unchanged | `RecoveryConsumerBase.cs:66-75` — pure RetryLoop+rethrow wrapper, no gate coupling. INJECT/DELETE/REINJECT all reuse it. |
| `RecoveryConsumerBase.Consume` gate-wait block | NO — REMOVE | `RecoveryConsumerBase.cs:38-58` is the bounded `WaitForOpenAsync` + linked-CTS + `RecoveryGateTimeoutException` throw. D-04 moves gating to the endpoint; D-09 deletes this. After removal, `Consume` collapses to `await HandleAsync(...)` (or `HandleAsync` becomes the public seam). |
| `RecoveryGateTimeoutException` | NO — DELETE (D-09) | Defined `RecoveryConsumerBase.cs:83-86`; only thrown by the block being removed. |
| `RecoveryOptions.GateWaitSeconds` | NO — DELETE (D-09) | `RecoveryOptions.cs:21`; only consumed by the removed gate-wait. `PartitionCount` stays. |
| `RecoveryOptions.PartitionCount` | YES | `RecoveryOptions.cs:11` — drives the partitioner slot count. |
| `RecoveryDataGoneException` | LIKELY DELETE (D-06) | `RecoveryDataGoneException.cs`. REINJECT is its only thrower (`ReinjectConsumer.cs:33`); D-06 flips REINJECT to silent drop. **Verify INJECT's real body does not need a data-gone terminal** (it shouldn't — INJECT has data in-hand, no read). If no users remain → delete. |
| `ReinjectConsumer` read+reconstruct+send | YES — keep, flip absent path | `ReinjectConsumer.cs:30-47`. STRLEN gate, `EntryStepDispatch` reconstruction, `queue:{ProcessorId:D}` send, IN-01 `CancellationToken.None` inner send — ALL reusable. Only `throw new RecoveryDataGoneException()` (line 33) → `{ log + metric; return; }` (drop). |
| `DeleteConsumer` | YES — likely zero change | `DeleteConsumer.cs:20` already `Guard(() => Db.KeyDeleteAsync(...))`; `KeyDeleteAsync` no-ops on missing key (KEEP-03 drops-on-absent satisfied). Verify vs A18 line 217 — matches. |
| `InjectConsumer` | IMPLEMENT (no-op today) | `InjectConsumer.cs:22` returns `Task.CompletedTask`. Write the A18 forward-only body. |
| `ReinjectConsumerDefinition` single-owner retry+partitioner | RE-HOMED into the connect callback (Pattern 1) | If converting to `ConnectReceiveEndpoint`, the endpoint config moves from the definition's `ConfigureConsumer` into the connect callback; the three sibling definitions' no-op concern dissolves. (If the planner finds a static-endpoint pause mechanism after all, the definition stays — but research found none viable in 8.5.5.) |
| `BitHealthLoop` edge structure | EXTEND | `BitHealthLoop.cs:36-50` — add `await handle.ReceiveEndpoint.Start/Stop(ct)` alongside the existing `gate.Open()/Close()` + `PauseAll/ResumeAll` publishes on each edge. The startup `prevHealthy=null` first-tick semantics must be preserved (locked by `BitHealthLoopTests`). |

## Common Pitfalls

### Pitfall 1: Static endpoint + ConnectReceiveEndpoint collision
**What goes wrong:** Leaving `AddConsumer<…>(def)` auto-configuring the `keeper-recovery` endpoint AND also calling `ConnectReceiveEndpoint("keeper-recovery", …)` → duplicate/conflicting endpoint, or the connect throws.
**Why it happens:** "You can't connect a receive endpoint that's already configured" (phatboyg #3549).
**How to avoid:** Register the three consumers without endpoint auto-config (or register them as plain consumers and connect the endpoint manually). Exactly one source configures `keeper-recovery`.
**Warning signs:** Bus start throws on duplicate endpoint; or the endpoint is unstoppable because the static instance shadows the connected one.

### Pitfall 2: Confusing endpoint-stop with consumer_timeout
**What goes wrong:** Assuming a stopped endpoint will be force-closed by RabbitMQ `consumer_timeout`.
**Why it happens:** Carryover from the WR-02 reasoning about the OLD parked-`Consume` mechanism.
**How to avoid:** Understand the distinction — `consumer_timeout` only fires against an **in-flight unacked delivery**. `IReceiveEndpoint.Stop` issues `basic.cancel`; there is no in-flight delivery, so the queue just accumulates with zero timeout exposure. This is precisely why D-04 eliminates WR-02. [CITED: rabbitmq.com/docs/consumers]
**Warning signs:** Spurious worry about `GateWaitSeconds`-style broker coupling — it's gone with the mechanism.

### Pitfall 3: Resume ordering / startup race with the BIT first tick
**What goes wrong:** The endpoint is connected STARTED, but the gate starts CLOSED (fail-safe, `L2HealthGate.cs:7`). If the endpoint consumes before the first healthy probe, a message could be processed while L2 is presumed-closed.
**Why it happens:** Two independent lifecycles (bus start vs first BIT tick).
**How to avoid:** Decide the startup posture explicitly. Options: connect the endpoint STOPPED and let the first healthy BIT edge `.Start()` it (symmetric with `gate` starting closed), OR connect started and rely on the gate. The BIT loop's first tick is always a transition (`prevHealthy=null`, `BitHealthLoop.cs:27`), so a "connect stopped → first healthy edge starts it" posture is clean and matches the existing edge model. [ASSUMED] connect-stopped is preferable — confirm with planner.
**Warning signs:** A recovery message processed during the startup window before the first probe.

### Pitfall 4: Deleting RecoveryDataGoneException while a user remains
**What goes wrong:** Removing the class (D-06) breaks compilation if INJECT (or a test) still references it.
**Why it happens:** Two consumers historically shared the terminal.
**How to avoid:** grep for `RecoveryDataGoneException` across `src/` and `tests/` before deleting; update `ReinjectConsumerFacts.Reinject_absent_throws_RecoveryDataGone` and `RecoveryDeadLetterFacts.DataGone_reinject_faults_and_routes_to_dead_letter` (both currently assert the THROW — they must be rewritten to assert the DROP + counter under D-06).
**Warning signs:** Build break in `BaseApi.Tests`; a still-green "throws data-gone" fact contradicting D-06.

### Pitfall 5: INJECT op ordering and the source-delete
**What goes wrong:** Reordering the three INJECT ops, or deleting `deleteEntryId` before the send confirms.
**Why it happens:** A18 line 211-214 mandates a strict order: write `L2[entryId]=data` → send `StepCompleted` → delete `L2[deleteEntryId]`.
**How to avoid:** Follow A18 verbatim; each op through `Guard`; inner `Send` uses `CancellationToken.None` (IN-01), outer Guard keeps `ct`. The delete is the tail (forward-only; data is already persisted + sent).
**Warning signs:** A completed result lost because the source was deleted before the send landed.

## Code Examples

### Example 1: REINJECT absent-path flip (KEEP-01 / D-06 / D-07)
```csharp
// Source: ReinjectConsumer.cs:30-35 (existing) — CHANGE the throw to a drop+metric.
// BEFORE (Phase 46):
await Guard(async () =>
{
    if (await Db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId)) == 0)
        throw new RecoveryDataGoneException();   // terminal → skp-dlq-1
    return true;
}, ct);
// ... then reconstruct + send

// AFTER (Phase 52, D-06/D-07):
//   - The PRESENCE READ must still be Guarded so a Redis *exception* (infra) routes to the
//     exhaustion policy. But absent/empty (no exception) → DROP, not throw.
var present = await Guard(
    () => Db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId)).ContinueWith(t => t.Result != 0),
    ct);   // shape only — confirm STRLEN-via-Guard returning bool at implementation
if (!present)
{
    _metrics.ReinjectDropped.Add(1);
    _logger.LogWarning("REINJECT drop: L2 data gone EntryId={EntryId}", m.EntryId);  // structured hole, no interpolation
    return;   // silent ack (A18 "accepted silent loss")
}
// reconstruct EntryStepDispatch + send (unchanged from ReinjectConsumer.cs:37-47)
```

### Example 2: INJECT full forward-only body (KEEP-02)
```csharp
// Source: A18 spec lines 211-214 (LOCKED) + Guard convention (RecoveryConsumerBase.cs) +
//         IN-01 inner-send CancellationToken.None (ReinjectConsumer.cs:47). VERIFIED contracts.
protected override async Task HandleAsync(KeeperInject m, CancellationToken ct)
{
    // 1) write L2[entryId] = data  (data is in-hand on the envelope — no read, forward-only)
    await Guard(() => Db.StringSetAsync(L2ProjectionKeys.ExecutionData(m.EntryId), m.Data), ct);

    // 2) send StepCompleted → ORCHESTRATOR result queue (per A15)
    var completed = new StepCompleted(m.WorkflowId, m.StepId, m.ProcessorId)
    {
        CorrelationId = m.CorrelationId,
        ExecutionId   = m.ExecutionId,
        EntryId       = m.EntryId,   // the REAL data key just written
    };
    var ep = await Send.GetSendEndpoint(/* orchestrator result queue URI — confirm the const */);
    await Guard(() => ep.Send(completed, CancellationToken.None), ct);   // IN-01

    // 3) delete L2[deleteEntryId]  (source cleanup tail)
    await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.DeleteEntryId)), ct);
}
```
**Note for planner:** the orchestrator result-queue URI must be sourced from the same const the processor's result-send uses (check `OrchestratorQueues` / `ProcessorPipeline.SendResult`). REINJECT sends to `queue:{ProcessorId:D}`; INJECT sends to the orchestrator result endpoint — these are DIFFERENT targets. Confirm the exact URI at implementation.

### Example 3: BitHealthLoop edge extension (KEEP-04 / D-04)
```csharp
// Source: BitHealthLoop.cs:36-50 (existing edges) — ADD endpoint Stop/Start.
if (healthy)
{
    gate.Open();
    await endpointHandle.ReceiveEndpoint.Start(stoppingToken);          // NEW: resume consumption + drain
    await bus.Publish(new ResumeAll { CorrelationId = NewId.NextGuid() }, stoppingToken);
}
else
{
    gate.Close();
    await endpointHandle.ReceiveEndpoint.Stop(stoppingToken);           // NEW: basic.cancel; accumulate on queue
    await bus.Publish(new PauseAll { CorrelationId = NewId.NextGuid() }, stoppingToken);
}
// Preserve WR-01: a Stop/Start or Publish failure must NOT advance prevHealthy (existing catch at :53-60).
```
**Note:** `endpointHandle` is the connected handle (Pattern 1), injected as a singleton. Consider whether `gate.Open()/Close()` is still needed once the endpoint itself gates consumption — likely the gate becomes redundant for the recovery path BUT is still read by `L2ProbeRecovery`/tests; do not remove it in-phase (out of D-08 scope unless cleanly orphaned).

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Bounded `WaitForOpenAsync` inside `Consume` (Phase-46 D-03) | Endpoint Stop/Start via connected handle (D-04) | Phase 52 | No parked channels; eliminates WR-02 `consumer_timeout` coupling |
| 5-state Model-B recovery consumer | 3-state keeper (REINJECT/INJECT/DELETE) | A18 (v5.0.0) | UPDATE/CLEANUP removed (Phase 50); this phase fills the 3 surviving bodies |
| REINJECT absent → `RecoveryDataGoneException` → skp-dlq-1 | REINJECT absent → silent drop + log + counter | Phase 52 (D-06) | Data genuinely gone is no longer a dead-letter; A18 "accepted silent loss" |
| Single fixed DLQ1 exhaustion | Configurable Dlq1 vs SustainedOutage | Phase 52 (KEEP-05) | Operators can choose hold-and-wait over dead-lettering during outages |

**Deprecated/outdated:**
- `HostReceiveEndpointHandle.StopAsync` for pause: in 8.5.5 it removes the endpoint (no clean restart). Superseded by `IReceiveEndpoint.Stop/Start`.
- `RecoveryGateTimeoutException`, `RecoveryOptions.GateWaitSeconds`: obsoleted by D-04, removed by D-09.

## Runtime State Inventory

> This is a body-only code phase, but it touches messaging-endpoint lifecycle and config — checked anyway.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | L2 keys: `skp:data:{entryId}` (read by REINJECT, written/deleted by INJECT, deleted by DELETE) — VERIFIED `L2ProjectionKeys.ExecutionData`. No key SHAPE changes this phase. | None — code reads/writes existing shape |
| Live service config | RabbitMQ queue `keeper-recovery` is broker-declared. Converting static→`ConnectReceiveEndpoint` does NOT change the queue name (`KeeperQueues.Recovery`), so the existing broker queue is reused. | None — verify queue name unchanged; no migration |
| OS-registered state | None — Keeper is a Generic Host console app, no Task Scheduler/systemd registration of keeper-internal state. | None — verified: `Program.cs` is `Host.CreateApplicationBuilder` only |
| Secrets/env vars | `Recovery:ExhaustionPolicy` is a NEW appsettings key (D-01). No secret. `Recovery:GateWaitSeconds` is REMOVED config (D-09) — stale appsettings entries become inert (no binder error for an unbound key). | Add `Recovery:ExhaustionPolicy` to appsettings; optionally clean stale `GateWaitSeconds` |
| Build artifacts | `src/Keeper/Observability/KeeperMetrics.cs` was DELETED in Phase 48 (48-01-PLAN.md) — re-creating it is a NEW file, not a revival of a tracked-but-stale artifact. | Create new file; ensure `AddMeter("Keeper")` registered or the counter won't export |

## Validation Architecture

> nyquist_validation = true (config.json) — section included.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit (project-pinned) + NSubstitute + MassTransit.Testing `ITestHarness` |
| Config file | none (xunit auto-discovery); facts under `tests/BaseApi.Tests/Keeper/` |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper"` |
| Full suite command | `dotnet test` (Release + Debug, 0-warning per TEST-02 close gate) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| KEEP-01 | REINJECT present → sends `EntryStepDispatch` w/ Payload to `queue:{proc}` | unit (consumer + CapturingSendProvider) | `dotnet test --filter "FullyQualifiedName~ReinjectConsumerFacts"` | ✅ exists — keep present-path fact |
| KEEP-01 | REINJECT absent/empty → silent drop + counter, NO send, NO throw | unit | (extend `ReinjectConsumerFacts`) | ❌ Wave 0 — rewrite the current "throws" fact to "drops + increments counter" (D-06/D-07) |
| KEEP-02 | INJECT → write L2[entryId], send StepCompleted, delete L2[deleteEntryId] in order | unit (FakeRedis/`Received()` ordering) | `dotnet test --filter "FullyQualifiedName~InjectConsumerFacts"` | ❌ Wave 0 — new `InjectConsumerFacts.cs` |
| KEEP-03 | DELETE deletes key; absent → no-op | unit | `dotnet test --filter "FullyQualifiedName~DeleteConsumerFacts"` | ✅ exists (`DeleteConsumerFacts`) — add an absent-key drop assertion if not covered |
| KEEP-04 | Gate-closed → endpoint stopped → message NOT consumed (accumulates); gate-open → consumed/drained | integration (`ITestHarness` Stop/Start + Consumed assertions) | `dotnet test --filter "FullyQualifiedName~KeeperPauseAccumulate"` | ❌ Wave 0 — new fact; harness pause/resume on the connected endpoint |
| KEEP-04 | BitHealthLoop drives Stop on unhealthy edge, Start on healthy edge | unit (extend `BitHealthLoopTests` with a fake handle) | `dotnet test --filter "FullyQualifiedName~BitHealthLoop"` | ✅ exists — extend ScriptedRedis-driven facts to assert Stop/Start call counts |
| KEEP-05 | Dlq1 mode: exhausted op → routes to skp-dlq-1 (ConsolidatedFault) | integration (`ITestHarness`) | `dotnet test --filter "FullyQualifiedName~RecoveryDeadLetter"` | ✅ exists (`RecoveryDeadLetterFacts`) — adapt for op-exhaustion (not data-gone, which is now a drop) |
| KEEP-05 | SustainedOutage mode: exhausted op → requeue/hold, NO dead-letter | integration (`ITestHarness`) | `dotnet test --filter "FullyQualifiedName~SustainedOutage"` | ❌ Wave 0 — new fact; assert NO `ConsolidatedFault`, message redelivered |

### Sampling Rate
- **Per task commit:** `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper"` (fast keeper-scoped facts)
- **Per wave merge:** full `dotnet test` (Debug)
- **Phase gate:** full suite green at Release + Debug, 0 warnings, before `/gsd-verify-work` (TEST-02 posture, though live triple-SHA E2E is Phase 54 TEST-01/02 — see scope note)

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` — covers KEEP-02 (write→send→delete ordering)
- [ ] Extend `ReinjectConsumerFacts.cs` — absent-path now DROP+counter (KEEP-01/D-06/D-07); delete the old "throws" assertion
- [ ] New pause/accumulate integration fact — covers KEEP-04 (endpoint Stop → no consume → Start → drain)
- [ ] Extend `BitHealthLoopTests.cs` with a fake endpoint handle — covers KEEP-04 driver (Stop on unhealthy edge, Start on healthy)
- [ ] New SustainedOutage fact — covers KEEP-05 hold/requeue mode
- [ ] Adapt `RecoveryDeadLetterFacts.cs` — the data-gone→dead-letter fact contradicts D-06; repurpose to op-exhaustion→dead-letter (Dlq1)
- [ ] A `KeeperMetrics` instrument fact (optional) — confirm `keeper_reinject_dropped` increments (mirrors `ProcessorMetricsFacts`)
- [ ] Hermetic scope note (carry the existing pattern): the live RabbitMQ skp-dlq-1 queue/TTL + real partitioner serialization defer to Phase 54 TEST-01 (Manual-Only / RealStack rows). The in-memory harness proves routing/fault/drop shape, not broker-literal queues.

## Security Domain

> `security_enforcement` is not present in config.json. This phase is internal recovery-consumer infrastructure with NO external request surface, NO authn/authz, NO user input parsing (messages are typed bus envelopes from trusted internal services). The applicable ASVS surface is minimal.

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V5 Input Validation | partial | Bus envelopes are strongly-typed records (`KeeperInject` etc.); no string-parsing of untrusted input. `m.Data` is opaque JSON written verbatim to L2 (already the v4 posture). |
| V6 Cryptography | no | No crypto introduced (the SHA256 in `PartitionGuid` is a non-security partition hash, unchanged). |
| V7 Logging | yes | D-07 logs use structured holes (`{EntryId}`), NEVER string interpolation (existing T-37-05/T-45-09 convention — see `PauseAllConsumer.cs:23`). Do not log `m.Data`/`m.Payload` contents. |

No new threat patterns: this phase adds no network ingress, no deserialization of untrusted formats beyond MassTransit's existing typed envelope handling.

## Project Constraints (from CLAUDE.md)

No `./CLAUDE.md` found in the working directory [VERIFIED: Read returned "File does not exist"]. Project conventions are instead enforced by the established codebase patterns documented above (structured logging holes, `IMeterFactory` metrics, single-owner endpoint config, Guard/RetryLoop, snake_case no-suffix metric names). The planner should treat these in-code conventions as binding.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Omitting the consolidated error move (no `ConfigureError`) yields broker requeue/redelivery for the SustainedOutage mode | Pattern 2 | Medium — if MassTransit instead acks/discards a thrown delivery without the filter, sustained-outage would silently drop work. MUST verify the exact "no dead-letter, requeue" call against 8.5.5 in-phase. |
| A2 | "Connect endpoint STOPPED, first healthy BIT edge starts it" is the cleaner startup posture | Pitfall 3 | Low — both postures work; the choice affects only the startup window. Planner/user can pick. |
| A3 | `RecoveryDataGoneException` has no remaining user after the REINJECT flip and can be deleted | §Reusable Asset Survival, Pitfall 4 | Low — grep-verifiable; if a test/INJECT path references it, keep or update. |
| A4 | The orchestrator result-queue URI for INJECT's `StepCompleted` send matches the processor's result-send target | Code Example 2 | Medium — wrong URI = completed results never reach the orchestrator. Confirm the const at implementation. |
| A5 | Converting `keeper-recovery` from static `AddConsumer` to `ConnectReceiveEndpoint` is the only viable 8.5.5 pause/resume path | OQ-1, Summary | Medium — this is a non-trivial wiring change. Verified no static-endpoint runtime pause exists in 8.5.5, but the planner should sanity-check the connect-callback equivalence to the current definition (retry+3×partitioner+consumers). |

## Open Questions

1. **(OQ-1, the central decision) Static `AddConsumer` endpoint vs `ConnectReceiveEndpoint` handle.**
   - What we know: 8.5.5 has no runtime pause for a statically-configured endpoint (`StopAsync` removes it; `ConcurrentMessageLimit`/`PrefetchCount` are config-time). The maintainer-blessed runtime pause is `ConnectReceiveEndpoint` + handle `Stop/Start`, which is mutually exclusive with static config. [VERIFIED assembly + CITED #3549]
   - What's unclear: the exact connect-callback wiring that reproduces `ReinjectConsumerDefinition` (retry + 3× partitioner + 3 consumers) and where to obtain `IReceiveEndpointConnector` + persist the handle for `BitHealthLoop`.
   - Recommendation: Plan the conversion explicitly. Register the three consumers, connect `keeper-recovery` once at startup, store `HostReceiveEndpointHandle` in a singleton, inject into `BitHealthLoop`. Keep the `PartitionGuid`/`PartitionKey` statics (they're already `public static` and test-pinned).

2. **(OQ-2) Exact 8.5.5 expression of "requeue, no dead-letter" for SustainedOutage.** See Assumption A1. Confirm whether bare omission of the error-move filter requeues, or whether an explicit redelivery configuration is needed.

3. **(OQ-3) Is `IL2HealthGate` still needed once the endpoint itself gates?** Once consumption is paused at the transport, the in-`Consume` gate read is gone (D-09). The gate object is still written by `BitHealthLoop` and may be read elsewhere/by tests. Recommendation: keep `IL2HealthGate` (out of D-08 cleanup scope) unless cleanly orphaned; the endpoint handle becomes the real enforcement, the gate becomes (at most) a state mirror.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK 8 | Build/test | ✓ (project targets net8.0) | net8.0 | — |
| MassTransit 8.5.5 | Endpoint pause/resume, bus | ✓ (CPM-pinned, restored to NuGet cache) | 8.5.5 | — |
| RabbitMQ broker | Live KEEP-04/05 proof (deferred) | n/a in-phase (hermetic facts use in-memory transport) | — | `ITestHarness` in-memory transport for automated facts; live proof is Phase 54 |
| Redis | L2 ops (deferred to live) | n/a in-phase (facts use NSubstitute `IDatabase`/`FakeRedis`) | — | NSubstitute doubles |

**Missing dependencies with no fallback:** None for this phase's automated scope (all facts are hermetic).
**Missing dependencies with fallback:** Live broker/Redis proof is intentionally deferred to Phase 54 (TEST-01/02), consistent with the existing per-phase hermetic-then-live split.

## Sources

### Primary (HIGH confidence)
- Installed assembly XML `~/.nuget/packages/masstransit.abstractions/8.5.5/lib/net8.0/MassTransit.Abstractions.xml` — `IReceiveEndpoint.Start/Stop` (7306/7313), `HostReceiveEndpointHandle.StopAsync/Ready` (6711/6706), `ConcurrentMessageLimit`/`PrefetchCount` (1607-1616)
- Installed assembly XML `~/.nuget/packages/masstransit/8.5.5/lib/net8.0/MassTransit.xml` — `IReceiveEndpointConnector.ConnectReceiveEndpoint` (7732-7765)
- Codebase (read in full): `RecoveryConsumerBase.cs`, `ReinjectConsumer.cs`, `InjectConsumer.cs`, `DeleteConsumer.cs`, `ReinjectConsumerDefinition.cs`, `InjectConsumerDefinition.cs`, `BitHealthLoop.cs`, `L2HealthGate.cs`, `IL2HealthGate.cs`, `RecoveryOptions.cs`, `RecoveryDataGoneException.cs`, `Program.cs` (Keeper), `KeeperInject/Reinject/Delete.cs`, `StepCompleted.cs`, `EntryStepDispatch.cs`, `KeeperQueues.cs`, `L2ProjectionKeys.cs`, `ProcessorMetrics.cs`, `PauseAllConsumer.cs`, `ResumeAllConsumer.cs`, `WorkflowScheduler.cs`
- Test harness (read in full): `RecoveryTestKit.cs`, `ReinjectConsumerFacts.cs`, `DeleteConsumerFacts.cs`, `RecoveryGateWaitFacts.cs`, `RecoveryDeadLetterFacts.cs`, `RecoveryPartitionFacts.cs`, `BitHealthLoopTests.cs`
- A18 spec `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` lines 110-227 (LOCKED v5.0.0)
- `Directory.Packages.props` (MassTransit 8.5.5 pin), `.planning/config.json` (nyquist=true), `48-01-PLAN.md` (KeeperMetrics prior deletion)

### Secondary (MEDIUM confidence)
- GitHub discussion MassTransit #3549 "Is it possible to pause only one consumer?" — phatboyg recommends `ConnectReceiveEndpoint` + handle Stop/Start; static+connect are mutually exclusive
- rabbitmq.com/docs/consumers — `consumer_timeout` fires only on in-flight unacked deliveries; channel-close requeues
- masstransit.io kill-switch doc — kill switch trips on failure threshold, auto-restarts after cooldown (not externally drivable)

### Tertiary (LOW confidence)
- MassTransit #4235 / #5387 (consumer_timeout channel-close behavior) — corroborating, not load-bearing

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all from installed assemblies + read source
- Architecture / bodies (REINJECT/INJECT/DELETE, metrics, exhaustion DLQ1): HIGH — existing patterns verified in code
- Pause/resume mechanism (OQ-1): MEDIUM — verified the API exists and the static-vs-connect constraint, but the connect-callback equivalence + handle plumbing is a design choice the planner must finalize
- SustainedOutage "no dead-letter requeue" exact call (OQ-2/A1): MEDIUM-LOW — verify against 8.5.5 in-phase
- Pitfalls / validation: HIGH — grounded in the existing fact harness

**Research date:** 2026-06-11
**Valid until:** 2026-07-11 (MassTransit 8.5.5 is pinned/stable; codebase is the volatile input — re-verify file line numbers if Phase 50/51 follow-up edits land)
