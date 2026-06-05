# Phase 36: L2 Health-Probe Recovery Loop & DLQs - Research

**Researched:** 2026-06-05
**Domain:** MassTransit-on-RabbitMQ error-transport topology + StackExchange.Redis bounded probe loop (.NET console)
**Confidence:** HIGH (codebase + Context7 + MT docs) — MEDIUM on the *exact* DLQ-1 consolidation hook (resolved below; one assumption flagged)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Keeper `Program.cs` calls existing `AddBaseConsoleRedis(cfg)` (singleton `IConnectionMultiplexer`, `abortConnect=false`). `ConnectionStrings:Redis` already in Keeper `appsettings.json`; compose injects `ConnectionStrings__Redis`. NO new `ProjectReference` — Redis rides `BaseConsole.Core`.
- **D-02 (PROBE-02):** each loop iteration does BOTH a **read** `db.StringGetAsync(L2ProjectionKeys.ExecutionData(inner.EntryId))` (value need NOT exist) AND a **write-then-delete** of a scratch key. Success only if **both** ops complete without a Redis exception (`RedisConnectionException`/timeout = fault → keep looping).
- **D-03 (scratch key):** new `L2ProjectionKeys.KeeperProbe(string h)` = `skp:keeper:probe:{h}`. Write with short TTL (~30s) then `KeyDeleteAsync`. TTL is the crash-safety net (net-zero by construction).
- **D-04 (PROBE-01):** new `"Probe": { "DelaySeconds": 5, "MaxAttempts": 12 }` → `ProbeOptions` via `IOptions`. `delay × attempts = 60s`, far under RabbitMQ's 30-min default `consumer_timeout`. Loop awaited *inside* `Consume`. Defaults LOCKED: 5s × 12.
- **D-05 (DLQ-02/04):** ONE shared `skp-dlq-1` queue, declared with `x-message-ttl`, consolidating EVERY console's `Immediate(N)` transport exhaustion — replacing per-`{queue}_error` default.
- **D-06 (where):** configured ONCE in `BaseConsole.Core` so processor + orchestrator + Keeper inherit uniformly. BaseApi is publisher-only → out of scope.
- **D-07 (TTL):** **7 days.**
- **D-08:** new const `KeeperQueues.DeadLetter = "keeper-dlq"` — plain durable queue, **NO `x-message-ttl`** (terminal operator alert; must persist until drained).
- **D-09 (give-up):** on max-attempts exhaustion, `GetSendEndpoint("queue:keeper-dlq")` → `Send(...)` → return (ack). A fault in the Send itself → endpoint `Immediate(N)` → DLQ-1.
- **D-10 (what to park):** the **original `Fault<T>` envelope** (`context.Message`), NOT the bare inner — it carries `Exceptions[]` for triage.
- **D-11 (scope LOCK):** Phase 36 **re-injects ONLY** — `GetSendEndpoint("queue:{processorId:D}")` for dispatch, `"queue:orchestrator-result"` for result, verbatim inner by type. Emits **NO** `PauseWorkflow`/`ResumeWorkflow` (Phase 37). Do NOT pull Phase-37 contracts in.
- **D-12 (PROBE-05):** ack-after-loop automatic (awaited inside `Consume`). Proof split: hermetic loop-logic tests + RealStack sibling of `FaultRecoverySpikeE2ETests`. Kill-mid-loop redeliver→restart stays operator runbook (Phase 39).

### Claude's Discretion
- `ProbeOptions` placement (Keeper-local vs `Messaging.Contracts.Configuration`) and the read/write StackExchange.Redis call shapes (mirror `ResultConsumer`).
- Shared probe-loop helper vs inline-per-consumer (both consumers run the identical loop — shared helper is the natural DRY move).
- Settle-window durations, `PollEsForLog` query shapes, RealStack test vehicle (extend spike-family vs new Keeper test).
- **D-13 prefetch/concurrency** — keep defaults; flag if fault-flood-during-outage surfaces real consumer-starvation. (Researched below — see Pitfall 5.)

### Deferred Ideas (OUT OF SCOPE)
- `PauseWorkflow`/`ResumeWorkflow` contracts + orchestrator pending-recovery set + cron pause/resume — **Phase 37** (PAUSE-01..05).
- Keeper meter + `keeper_l2_probe_failed` / DLQ-depth counters/histograms — **Phase 38** (KMET-01..03).
- `keeper-dlq`-depth Prometheus alert + real-stack close gate (3×GREEN triple-SHA) — **Phase 39** (TEST-01..03).
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| PROBE-01 | Bounded recovery loop; `delay × attempts` < RabbitMQ ack timeout (documented) | `ProbeOptions` + in-`Consume` await; 60s vs 30-min default `consumer_timeout` confirmed (30× margin). See "ack-after-loop legality". |
| PROBE-02 | Each iteration: read `skp:data:{entryId}` AND write-then-delete scratch key | StackExchange.Redis 2.13.1 `StringGetAsync`/`StringSetAsync`/`KeyDeleteAsync` signatures confirmed; mirror `ResultConsumer`. |
| PROBE-03 | First successful probe → exit loop → re-inject (Phase 36 = re-inject only) | Spike-proven `GetSendEndpoint`+`Send` verbatim-by-type (`FaultRecoverySpikeE2ETests`). |
| PROBE-04 | Max-attempts exhaust → park original message in `keeper-dlq` → exit | `KeeperQueues.DeadLetter` + `GetSendEndpoint("queue:keeper-dlq")`+`Send(context.Message)`. |
| PROBE-05 | Ack ONLY after loop exits; crash mid-loop → redeliver → restart | Await inside `Consume` holds delivery un-acked; at-least-once is MassTransit default. |
| DLQ-01 | Keeper's own infra faults retry under `Immediate(N)` → DLQ-1 | Existing `FaultEntryStepDispatchConsumerDefinition.UseMessageRetry(Immediate(Limit))`; DLQ-1 consolidation makes its `_error` land in `skp-dlq-1`. |
| DLQ-02 | Two DLQs split by mechanism (DLQ-1 consolidated TTL'd; DLQ-2 `keeper-dlq`) | **DLQ-1 consolidation mechanism resolved below** — REQUIREMENTS.md's "confirmed in Phase-33 spike" is INACCURATE (CONTEXT D-07); spike only recorded the decision. |
| DLQ-03 | `keeper-dlq` = primary alert (persistent); DLQ-1 = TTL'd forensic | Topology only this phase: durable no-TTL `keeper-dlq` vs `x-message-ttl=7d` `skp-dlq-1`. Alert wiring is Phase 39. |
| DLQ-04 | All consumers use shared `Immediate(N)` → DLQ-1 via shared error-transport (uniform) | Configure ONCE in `BaseConsole.Core` `AddBaseConsoleMessaging` via the `AddConfigureEndpointsCallback` / custom error-transport seam. |
</phase_requirements>

## Summary

Phase 36 turns Keeper from the Phase-35 observe-and-ack skeleton into the recovery engine. Three concrete deltas land in `src/`: (1) a **bounded L2 probe loop** awaited inside each Keeper fault consumer's `Consume` (read `skp:data:{entryId}` + write-then-delete a scratch key; success → spike-proven re-inject-by-type; max-attempts → `Send` the original `Fault<T>` to `keeper-dlq`); (2) the **`keeper-dlq` (DLQ-2)** plain durable queue contract; and (3) the **consolidated TTL'd `skp-dlq-1` (DLQ-1)** error transport wired once in `BaseConsole.Core` so all three consoles route exhaustion uniformly.

The probe loop is low-risk — it reuses the exact `IConnectionMultiplexer` injection + `StringGetAsync`/`StringSetAsync` shape from `ResultConsumer` and the exact `GetSendEndpoint`+`Send`-verbatim-by-type pattern proven LIVE in `FaultRecoverySpikeE2ETests` (GATE_EXIT=0). The `keeper-dlq` park is a one-line `Send(context.Message)` to a new queue const.

**The single genuine unknown is DLQ-1 consolidation** (CONTEXT D-07). MassTransit 8.5.5's retry-exhaustion path **moves/republishes** the message to a per-endpoint `{queue}_error` exchange/queue — it is NOT a RabbitMQ nack-to-DLX. A naive DLX bind therefore catches nothing on exhaustion. MassTransit provides **no built-in single-shared-error-queue formatter for RabbitMQ**. Two viable mechanisms exist (detailed below); **the recommended one is a custom error transport installed via `ConfigureError` in an `AddConfigureEndpointsCallback`**, replacing the default `ErrorTransportFilter`'s per-endpoint target with one fixed `skp-dlq-1` send endpoint, declared with `x-message-ttl` = 7 days. This is base-library infra touching ALL three consoles — the regression surface is the whole milestone's green suite.

**Primary recommendation:** Implement the probe loop + `keeper-dlq` park as a shared helper invoked by both Keeper consumers (low risk, spike-proven primitives). For DLQ-1, install a custom error transport in `BaseConsole.Core`'s `AddBaseConsoleMessaging` via `AddConfigureEndpointsCallback` → `e.ConfigureError(...)`, sending faulted messages to one declared `skp-dlq-1` queue with `x-message-ttl`. Verify SetQueueArgument-on-create + net-zero close-gate interaction carefully (Pitfall 1/2).

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| L2 probe loop (read+write) | Keeper console (consumer) | StackExchange.Redis client | Recovery logic owns the loop; Redis client is the probe surface (mirrors `ResultConsumer`). |
| Re-inject-by-type on success | Keeper console (consumer) | RabbitMQ transport (`GetSendEndpoint`+`Send`) | Spike-proven; Keeper sends verbatim inner to origin queue. |
| `keeper-dlq` (DLQ-2) park | Keeper console (consumer) | RabbitMQ transport (`Send`) | Terminal give-up is a Keeper decision; queue is plain durable. |
| `keeper-dlq` queue contract | `Messaging.Contracts` (`KeeperQueues`) | — | Single source of truth for the queue name. |
| Scratch + read key builders | `Messaging.Contracts` (`L2ProjectionKeys`) | — | Same leaf as `ExecutionData`/`Flag`; one home for key shapes. |
| `ProbeOptions` config | Keeper-local OR `Messaging.Contracts.Configuration` | `IOptions<T>` | Claude's discretion (D-04). Keeper-local is simplest; Contracts if shared later. |
| DLQ-1 consolidated error transport | `BaseConsole.Core` (`AddBaseConsoleMessaging`) | RabbitMQ transport (`ConfigureError`) | D-06: configured ONCE in the shared base so all 3 consoles inherit uniformly. |
| `Immediate(N)` retry budget | Per-consumer `ConsumerDefinition` (existing) | `RetryOptions` config | Already wired; DLQ-1 only changes the *destination* of exhausted messages. |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 | Bus + consumer + retry + error transport | Already the bus everywhere; 8.5.5 is the **last Apache-2.0 line** (v9+ commercial) — `[VERIFIED: Directory.Packages.props]`. |
| MassTransit.RabbitMQ | 8.5.5 | RabbitMQ transport (error exchange/queue, `SetQueueArgument`, `ConfigureError`) | `[VERIFIED: Directory.Packages.props]`. |
| StackExchange.Redis | 2.13.1 | `IConnectionMultiplexer` probe client | Already the L2 client (`AddBaseConsoleRedis`, `ResultConsumer`) — `[VERIFIED: Directory.Packages.props]`. |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Microsoft.Extensions.Options | (transitive) | Bind `ProbeOptions` (D-04) | `builder.Services.Configure<ProbeOptions>(cfg.GetSection("Probe"))`; mirror the existing `RetryOptions` bind in `Program.cs`. |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Custom error transport (DLQ-1) | RabbitMQ DLX bind on each `{queue}_error` | **Does not work for exhaustion** — MT *moves/republishes* to `_error`, never nacks; a DLX only fires on reject/expire/TTL. Could chain (per-`_error` TTL → DLX → `skp-dlq-1`) but adds a hop + per-endpoint queues. See Pitfall 4. |
| Custom error transport | Per-endpoint `_error` with `x-message-ttl` via `SetQueueArgument` | Gives N TTL'd `{queue}_error` queues, NOT the ONE consolidated `skp-dlq-1` DLQ-05 mandates (D-05). Fails DLQ-04 "same consolidated queue". |

**Installation:** No new packages. All three are already referenced.

**Version verification:** `[VERIFIED: Directory.Packages.props]` — MassTransit 8.5.5, MassTransit.RabbitMQ 8.5.5, StackExchange.Redis 2.13.1. (Did not re-query npm — these are NuGet, pinned centrally; the props file is the source of truth and explicitly comments the 8.5.5 Apache cap.)

## Architecture Patterns

### System Data Flow Diagram

```
                          Fault<EntryStepDispatch> / Fault<ExecutionResult>
                          (auto-published by ANY console on Immediate(N) exhaustion)
                                              │  pub/sub fanout
                                              ▼
                    ┌──────────────────────────────────────────────┐
                    │  Keeper: keeper-fault-recovery (1 durable Q)   │
                    │  FaultEntryStepDispatchConsumer / FaultExec…   │
                    │  double-unwrap context.Message.Message         │
                    └──────────────────┬───────────────────────────┘
                                       │ inner IExecutionCorrelated (EntryId, H, ProcessorId…)
                                       ▼
                    ┌──────────────────────────────────────────────┐
                    │  PROBE LOOP (awaited INSIDE Consume)           │
                    │  for attempt in 1..MaxAttempts(12):            │
                    │    read  StringGetAsync(skp:data:{EntryId})    │  ← value need not exist
                    │    write StringSetAsync(skp:keeper:probe:{H},  │
                    │           …, expiry 30s); KeyDeleteAsync(…)     │
                    │    both ok, no exception? ─yes─► SUCCESS ──┐    │
                    │    Redis exception ─► await Delay(5s); next │    │
                    │  loop ends without success ─► GIVE UP ──┐  │    │
                    └─────────────────────────────────────────┼──┼───┘
                                                              │  │
                          GIVE UP                  SUCCESS    │  │
                              │                       │       │  │
                              ▼                       ▼       ▼  ▼
        ┌─────────────────────────────┐   ┌──────────────────────────────┐
        │ Send(context.Message)       │   │ GetSendEndpoint("queue:{proc  │
        │   → queue:keeper-dlq (DLQ-2)│   │   :D}")  OR "queue:orchestrator│
        │   plain durable, NO TTL     │   │   -result"; Send(verbatim inner)│
        └──────────────┬──────────────┘   └───────────────┬──────────────┘
                       │ return → ACK                      │ return → ACK
                       ▼                                   ▼ receiver flag[H] collapses dup (PROBE-06)
        (Send fault → Immediate(N) → DLQ-1)   (Send fault → Immediate(N) → DLQ-1)

   ── ORTHOGONAL: DLQ-1 transport-exhaustion consolidation (BaseConsole.Core, ALL consoles) ──
   Any consumer: retry budget exhausts ─► ConfigureError pipeline ─► custom error transport ─►
        ONE shared queue  skp-dlq-1  (x-message-ttl = 7 days)   [replaces per-{queue}_error default]
```

### Recommended touch-map (no new projects)
```
src/
├── Messaging.Contracts/
│   ├── KeeperQueues.cs                       # + const DeadLetter = "keeper-dlq" (D-08)
│   └── Projections/L2ProjectionKeys.cs       # + KeeperProbe(string h) => "skp:keeper:probe:{h}" (D-03)
├── Messaging.Contracts/Configuration/ (or Keeper/)
│   └── ProbeOptions.cs                        # DelaySeconds, MaxAttempts (D-04, discretion on home)
├── Keeper/
│   ├── Program.cs                            # AddBaseConsoleRedis(cfg) + Configure<ProbeOptions>
│   ├── appsettings.json                      # + "Probe": { DelaySeconds:5, MaxAttempts:12 } (Redis key already present)
│   ├── Recovery/L2ProbeRecovery.cs           # shared loop helper (discretion) — probe → re-inject | park
│   └── Consumers/Fault*Consumer.cs           # invoke the helper between unwrap and return
└── BaseConsole.Core/DependencyInjection/
    └── MessagingServiceCollectionExtensions.cs  # DLQ-1 consolidated error transport (D-05/06) — ALL consoles
```

### Pattern 1: Bounded probe loop awaited inside Consume (PROBE-01/02/05)
**What:** await the loop inside `Consume`; the broker delivery stays un-acked until the method returns.
**When:** both Keeper fault consumers (shared helper).
**Example (shape — verify signatures against StackExchange.Redis 2.13.1):**
```csharp
// Source: codebase ResultConsumer.cs (db.StringGetAsync / StringSetAsync), CONTEXT D-02/03/04
// [CITED: src/Orchestrator/Consumers/ResultConsumer.cs:60-115]  [ASSUMED: exact helper shape]
public async Task<ProbeOutcome> RunAsync(IExecutionCorrelated inner, CancellationToken ct)
{
    var db = _redis.GetDatabase();                       // IConnectionMultiplexer ctor-injected (as ResultConsumer)
    for (var attempt = 0; attempt < _opts.MaxAttempts; attempt++)
    {
        try
        {
            // READ availability — value need NOT exist (D-02)
            _ = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(inner.EntryId));
            // WRITE availability — scratch key with short TTL then delete (D-03)
            var scratch = L2ProjectionKeys.KeeperProbe(inner.H);
            await db.StringSetAsync(scratch, "1", expiry: TimeSpan.FromSeconds(30));
            await db.KeyDeleteAsync(scratch);
            return ProbeOutcome.Recovered;               // both ops, no exception → success (D-02)
        }
        catch (RedisException)                            // RedisConnectionException : RedisException; timeouts too
        {
            if (attempt + 1 < _opts.MaxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(_opts.DelaySeconds), ct);
        }
    }
    return ProbeOutcome.GaveUp;
}
```
> NOTE: catch the broadest Redis fault that covers connection loss + timeout. `RedisConnectionException` and `RedisTimeoutException` both derive from `RedisException` in StackExchange.Redis — catching `RedisException` is the safe superset. Do NOT catch `Exception` (that would swallow a genuine bug as "down"). Confirm the inheritance during planning if a narrower catch is preferred.

### Pattern 2: Re-inject verbatim by type on success (PROBE-03)
```csharp
// Source: PROVEN LIVE in FaultRecoverySpikeE2ETests (GATE_EXIT=0)
// [CITED: tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs:214,242-243]
var uri = inner switch
{
    EntryStepDispatch d => new Uri($"queue:{d.ProcessorId:D}"),       // origin processor endpoint
    ExecutionResult    => new Uri($"queue:{OrchestratorQueues.Result}"), // "queue:orchestrator-result"
    _ => throw new InvalidOperationException("unknown inner type")
};
var endpoint = await context.GetSendEndpoint(uri);   // ConsumeContext.GetSendEndpoint — NOT a raw IBus needed
await endpoint.Send(inner, context.CancellationToken);   // Send (NOT Publish), verbatim inner, same H
```
> Use `context.GetSendEndpoint(...)` (the `ConsumeContext` overload) so the outbound correlation send-filter and the consume scope apply. PROBE-06: Keeper adds NO dedup — the receiver's surviving Phase-31 `flag[H]` gate collapses any duplicate re-inject.

### Pattern 3: Park original Fault<T> envelope on give-up (PROBE-04, D-09/D-10)
```csharp
// Send the ORIGINAL envelope (carries Exceptions[] for triage — D-10), NOT the bare inner.
// [CITED: CONTEXT D-08/09/10]
var dlq = await context.GetSendEndpoint(new Uri($"queue:{KeeperQueues.DeadLetter}"));  // "queue:keeper-dlq"
await dlq.Send(context.Message, context.CancellationToken);   // context.Message == Fault<EntryStepDispatch>/Fault<ExecutionResult>
return;  // → ack. A fault in THIS Send is infra → Immediate(N) → DLQ-1 (D-09, consistent with every consumer).
```
> `keeper-dlq` is a plain durable queue with NO consumer in Phase 36 (operator-drained). To make MassTransit *declare* it as a send endpoint with no consumer, the simplest reliable approach is to `Send` to `queue:keeper-dlq` — RabbitMQ auto-declares the queue on first send via MassTransit's send topology. If a guaranteed-durable-no-TTL declaration is required up front (so it exists before the first give-up, for the Phase-39 close-gate snapshot), add a no-consumer `ReceiveEndpoint("keeper-dlq", e => { })` OR declare it in the bus-factory seam. **Flag for planner: decide declare-on-send vs explicit declaration** (close-gate net-zero may need it present in both snapshots — see Pitfall 1).

### Pattern 4 (THE UNKNOWN — DLQ-1 consolidation): custom error transport via ConfigureError
See "## DLQ-1 Consolidation Mechanism" below — this is the load-bearing research item.

### Anti-Patterns to Avoid
- **DLX bind to catch exhaustion** — MT *moves/republishes* to `_error`; it does NOT nack. A DLX never fires on exhaustion. (Pitfall 4.)
- **`catch (Exception)` in the probe loop** — would treat a genuine code bug as "Redis down" and loop 60s then mis-route. Catch `RedisException` only.
- **Re-arming `flag[H]` in Keeper** — PROBE-06: Keeper owns no dedup. Re-injecting verbatim is sufficient; the receiver gate collapses dups.
- **Pulling `PauseWorkflow`/`ResumeWorkflow` into Phase 36** — D-11 LOCK. SC-4 "stays paused" is structurally vacuous here (no pause exists until Phase 37).
- **`SetQueueArgument` on an already-existing queue** — RabbitMQ rejects arg changes on a live queue; MT will not delete it. Args apply ONLY at create. (Pitfall 2.)

## DLQ-1 Consolidation Mechanism (TOP RESEARCH ITEM — D-07)

### The decisive facts (version-correct, MassTransit 8.5.5 + RabbitMQ)

1. **On retry-budget exhaustion, MassTransit MOVES/republishes the message to a per-endpoint `{queue}_error` exchange→queue, AND separately publishes a `Fault<T>`.** It is **NOT** a RabbitMQ `basic.nack`/dead-letter. `[CITED: masstransit.massient.com/documentation/concepts/exceptions — "The message is moved to the _error queue (prefixed by the queue name). The exception details are stored as headers."]`
   - Consequence: the existing `Fault<T>` pub/sub stream Keeper consumes is the *Fault* half; DLQ-1 is about the *`_error` move* half. They are independent.

2. **There is NO built-in "single shared error queue name formatter" for the RabbitMQ transport in MT8.** MT 8.0.7 added topology hooks to override error/skipped queue *names*, but no first-class "all endpoints → one error queue" switch. `[VERIFIED: WebSearch — multiple sources; no API surface found for RabbitMQ shared error queue]`

3. **The `_error` move is performed by the `ErrorTransportFilter` inside the endpoint's error pipeline, configured via `ConfigureError`.** The default error pipeline is `GenerateFaultFilter` → `ErrorTransportFilter`. You can replace/precede these via `e.ConfigureError(x => { x.UseFilter(...); ... })`. `[CITED: github.com/MassTransit/MassTransit/discussions/2945 — maintainer-confirmed default filter pair; omitting them means "nothing goes into _error queue"]`

4. **`SetQueueArgument("x-message-ttl", ...)` applies only at queue creation; it propagates from the main endpoint config to that endpoint's `_error` queue (the `_error` queue inherits base consumer config).** `[CITED: github.com/MassTransit/MassTransit/discussions/2990 — "the _error queue inherits the base consumer configuration, including the TTL argument"]` `[CITED: github.com/MassTransit/MassTransit/discussions/5868 — SetQueueArgument requires AddConfigureEndpointsCallback, not bus-level]`

5. **Queue arguments cannot be changed on an existing queue; MT never deletes queues.** `[CITED: github.com/MassTransit/MassTransit/discussions/4817 — phatboyg: "RabbitMQ doesn't allow changes to queue properties, they have to be recreated… MassTransit does not delete queues."]`

### REQUIREMENTS.md correction
REQUIREMENTS.md DLQ-02 says the consolidation mechanism "is confirmed in the Phase-33 spike." **This is INACCURATE** (CONTEXT D-07, and confirmed by reading `FaultRecoverySpikeE2ETests.cs` + Phase-33 `33-CONTEXT.md` D-10): Phase 33 only **recorded the retention decision** — it built NO error-transport wiring. The spike trips faults via WRONGTYPE and proves re-inject; it never touches `_error` topology. The planner must treat DLQ-1 wiring as **unbuilt and novel**, not "confirmed."

### Two viable mechanisms

**Mechanism A (RECOMMENDED): custom error transport — redirect the move to one fixed `skp-dlq-1` send endpoint.**
Replace the default `ErrorTransportFilter` (which targets `{queue}_error`) with a custom filter that `Send`s the faulted `ReceiveContext` to one fixed `skp-dlq-1` endpoint, installed uniformly via `AddConfigureEndpointsCallback` so it hits every receive endpoint on every console.
```csharp
// Source shape — [CITED: discussions/2945 ConfigureError pipeline]  [ASSUMED: exact filter impl]
// In BaseConsole.Core AddBaseConsoleMessaging, inside AddMassTransit(x => { ... }):
x.AddConfigureEndpointsCallback((ctx, name, cfg) =>
{
    cfg.UseMessageRetry(r => r.Immediate(/* RetryOptions.Limit */));   // existing budget (DLQ-04)
    cfg.ConfigureError(e =>
    {
        e.UseFilter(new GenerateFaultFilter());        // keep Fault<T> publication (Keeper depends on it!)
        e.UseFilter(new ConsolidatedErrorTransportFilter(/* send to "queue:skp-dlq-1" */));  // CUSTOM, replaces default ErrorTransportFilter
    });
});
```
- The custom filter implements `IFilter<ExceptionReceiveContext>`; it resolves a send endpoint for `skp-dlq-1` and moves the raw message there (mirroring the default `ErrorTransportFilter` but with a fixed destination). **GenerateFaultFilter MUST stay** — Keeper's whole recovery model rides the `Fault<T>` pub/sub stream; removing it would break Phases 33–35.
- Declare `skp-dlq-1` once with `x-message-ttl` = 7 days, e.g. a no-consumer `ReceiveEndpoint("skp-dlq-1", e => { e.SetQueueArgument("x-message-ttl", (int)TimeSpan.FromDays(7).TotalMilliseconds); })` in the bus factory, OR via the send-topology declaration. `x-message-ttl` value is **milliseconds as int/long** (RabbitMQ semantics).
- **Pro:** ONE consolidated queue (satisfies D-05/DLQ-04 literally), single declaration point in `BaseConsole.Core` (D-06), uniform across consoles.
- **Con:** Custom `IFilter<ExceptionReceiveContext>` is non-trivial — must faithfully reproduce the default move (headers, redelivery, content-type). Highest implementation risk. **Validate against the live `_error` header contract.**

**Mechanism B (FALLBACK): per-endpoint TTL'd `_error` + a chained DLX into `skp-dlq-1`.**
Keep per-`{queue}_error` queues but give each `x-message-ttl` (7d) AND `x-dead-letter-exchange = skp-dlq-1` via `SetQueueArgument` on each endpoint (propagates to its `_error`); messages expire/forward into a single `skp-dlq-1`.
- **Pro:** No custom filter; uses only `SetQueueArgument` + native DLX.
- **Con:** Does NOT give a *consolidated* queue at exhaustion time — messages sit in per-endpoint `_error` until TTL expiry, then DLX-forward. That's a *delayed* consolidation, contradicting DLQ-03's "forensic record" intent (operators want them in `skp-dlq-1` immediately). Also multiplies queues (one `_error` per endpoint), muddying the close-gate snapshot. **Weaker fit for D-05/DLQ-04.**

**Recommendation:** **Mechanism A.** It is the only one that produces the single consolidated `skp-dlq-1` at exhaustion (D-05 "ONE shared queue", DLQ-04 "routed uniformly… same design pattern"). Budget plan time for getting the custom `ExceptionReceiveContext` filter right and verify the moved-message shape against a live `_error` message before committing. If A proves too costly, B is the documented fallback (flag the DLQ-03 semantics gap to the user).

### Open verification for the planner (do during planning, not blocking)
- **Confirm the exact `IFilter<ExceptionReceiveContext>` API + how the default `ErrorTransportFilter` resolves its destination in MT 8.5.5** (read `MassTransit.RabbitMqTransport` source for `RabbitMqMoveToErrorTransport` / `MoveToErrorTransportFilter`). `[ASSUMED: ConsolidatedErrorTransportFilter is feasible by mirroring it]`
- **Confirm `SetQueueArgument` is exposed on the error-queue path** vs only the main queue. (Mechanism A sidesteps this by declaring `skp-dlq-1` as its own endpoint.)

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Fault delivery to Keeper | A custom error consumer per processor | Existing `Fault<T>` pub/sub bind (Phase 33/35) | MT auto-publishes `Fault<T>` on exhaustion; spike-proven. |
| Re-inject to origin | Hand-reconstruct the message from 6 ids | `GetSendEndpoint`+`Send(inner)` verbatim | Same `H` guarantees receiver collapse (PROBE-06). |
| Re-inject idempotency | Keeper-side dedup / `flag[H]` | Receiver's surviving Phase-31 gate | PROBE-06 LOCK — Keeper owns no dedup. |
| Error-queue move | Re-publish faulted messages manually | MT `ErrorTransportFilter` (custom destination) | Reproduces headers/redelivery/content-type correctly; hand-rolling drops forensic headers. |
| Retry budget | A manual attempt counter for transport faults | Existing `UseMessageRetry(Immediate(Limit))` | Already wired per-endpoint from `RetryOptions`. |
| Scratch-key cleanup on crash | A reaper job | Short TTL on the scratch key (D-03) | TTL self-cleans → net-zero by construction. |

**Key insight:** Every Phase-36 primitive except the DLQ-1 error transport is already proven in `src/` or the spike. The probe loop and `keeper-dlq` park are assembly of known parts; only DLQ-1 is genuinely new infra.

## Runtime State Inventory

> Phase 36 is **net-additive code** (new consumers logic, new queue, new error transport), not a rename/migration. This section is included because it touches **live RabbitMQ topology** that persists outside git.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data (Redis) | Scratch key `skp:keeper:probe:{h}` (NEW, D-03) — written + deleted each probe, TTL 30s | Code edit only; TTL self-cleans. No migration. Net-zero by construction (close-gate `skp:*` scan). |
| Live service config (RabbitMQ) | **NEW queues:** `keeper-dlq` (durable, no TTL) + `skp-dlq-1` (durable, `x-message-ttl=7d`). **CHANGED:** error-move destination for ALL 3 consoles' endpoints (was `{queue}_error`). Existing `{queue}_error` queues may still exist on the broker from prior runs. | Declare new queues via topology. **Mechanism A makes existing `{queue}_error` go dormant (no longer written) — they will NOT auto-delete (MT never deletes queues).** Decide whether to manually purge/delete the now-orphan `{queue}_error` queues on the dev broker so they don't pollute the Phase-39 close-gate rabbitmq snapshot. |
| OS-registered state | None — verified: no Task Scheduler / pm2 / systemd interaction in Keeper. | None. |
| Secrets/env vars | None new. Redis conn string already injected (`ConnectionStrings__Redis`, Phase 34 D-05); RabbitMq creds unchanged. | None. |
| Build artifacts | Keeper container image must be **rebuilt** before any RealStack proof (embedded SourceHash must match — project gotcha). | Rebuild keeper (+ processor/orchestrator/baseapi if their error transport changed via `BaseConsole.Core`) before live gate. |

**Critical RabbitMQ caveat (Pitfall 2):** because `x-message-ttl` is set at create time and MT cannot change args on a live queue, if `skp-dlq-1` was ever declared without the TTL, it must be deleted on the broker before the corrected declaration takes effect. Plan a one-time broker reset for the dev stack.

## Common Pitfalls

### Pitfall 1: Close-gate net-zero triple-SHA broken by new/orphan queues
**What goes wrong:** Phase 39's close gate snapshots RabbitMQ topology BEFORE/AFTER and asserts net-zero. New `keeper-dlq`/`skp-dlq-1` are fine if present in BOTH snapshots (declared at boot), but **orphaned `{queue}_error` queues** left from the old per-endpoint default, or a `keeper-dlq` that only appears after the first give-up, will drift the SHA.
**Why:** MT auto-declares on first send (so `keeper-dlq` may be absent until a give-up fires); MT never deletes the old `_error` queues.
**How to avoid:** Declare `keeper-dlq` and `skp-dlq-1` at boot (no-consumer `ReceiveEndpoint` or explicit topology) so they're in both snapshots. Manually purge/delete orphan `{queue}_error` queues on the dev broker once.
**Warning signs:** close-gate RED on a rabbitmq SHA mismatch with no code change.

### Pitfall 2: `x-message-ttl` silently ignored on a pre-existing queue
**What goes wrong:** declaring `skp-dlq-1` with `x-message-ttl` when a `skp-dlq-1` already exists without it → MT logs a declare mismatch or silently uses the old args; the 7-day TTL never applies.
**Why:** RabbitMQ rejects arg changes on live queues; MT won't delete. `[CITED: discussions/4817]`
**How to avoid:** Delete `skp-dlq-1` on the broker before first corrected declaration; verify args via `rabbitmqctl`/management UI after boot.
**Warning signs:** `skp-dlq-1` grows unbounded (TTL not draining) in DLQ-03's secondary-alert window.

### Pitfall 3: Per-endpoint retry double-registration (already a known Keeper trap)
**What goes wrong:** registering `UseMessageRetry` / `ConfigureError` on both colocated Keeper consumer definitions double-registers the filter on the one shared `keeper-fault-recovery` endpoint.
**Why:** retry/error middleware is PER-ENDPOINT, not per-consumer; both Keeper consumers share one endpoint.
**How to avoid:** DLQ-1 wiring lives in `BaseConsole.Core`'s `AddConfigureEndpointsCallback` (once per endpoint, framework-deduped), NOT in the per-consumer definitions. Keep `FaultExecutionResultConsumerDefinition.ConfigureConsumer` a no-op (existing design). `[CITED: src/Keeper/Consumers/FaultExecutionResultConsumerDefinition.cs — Pitfall 3 doc]`
**Warning signs:** doubled retry attempts in logs; hermetic harness fault counts off.

### Pitfall 4: Assuming a DLX catches exhaustion (the D-07 trap)
**What goes wrong:** binding a dead-letter-exchange to catch exhausted messages → catches nothing.
**Why:** MT *moves/republishes* to `_error`; it never nacks. A DLX fires only on reject/expire/TTL. `[CITED: concepts/exceptions]`
**How to avoid:** Use the custom error transport (Mechanism A) or the per-`_error`-TTL-then-DLX chain (Mechanism B), NOT a direct DLX on the main queue.

### Pitfall 5: Consumer starvation during a fault flood (D-13 — researched, low risk)
**What goes wrong:** during an L2 outage, many faults arrive; each Keeper `Consume` holds a delivery for up to 60s. With default prefetch/concurrency, the shared `keeper-fault-recovery` queue could head-of-line block.
**Why:** the loop is awaited in-`Consume`; un-acked deliveries occupy the prefetch window.
**Assessment:** **Low risk at default settings.** Default MT concurrency lets multiple deliveries run concurrently (each is an independent awaited loop — they don't serialize), and the 60s bound is 30× under `consumer_timeout`. The faults are competing-consumer round-robined across replicas. A genuine starvation concern only arises with a huge fault flood AND a single replica AND prefetch=1.
**Recommendation:** keep defaults (D-13). If the planner wants a belt-and-suspenders, set a modest `ConcurrentMessageLimit` on the Keeper endpoint — but this is optional and out of the locked scope. Document the rationale; do not over-engineer.

### Pitfall 6: `consumer_timeout` if MaxAttempts × Delay is ever raised
**What goes wrong:** raising `Probe.MaxAttempts × DelaySeconds` past 30 min → RabbitMQ kills the consumer mid-loop, the channel closes, the delivery requeues.
**How to avoid:** the `ProbeOptions` doc-comment MUST state the constraint (D-04). Defaults 5×12=60s leave a 30× margin. Validate the product at bind time or in a hermetic test.

## Code Examples

### ProbeOptions (D-04) — mirror the existing RetryOptions bind
```csharp
// Source: mirrors src/Keeper/Program.cs RetryOptions bind + RetryOptions.cs shape
// [CITED: src/Messaging.Contracts/Configuration/RetryOptions.cs]
/// <summary>
/// PROBE-01: bounded L2 probe loop knobs. CONSTRAINT (load-bearing, D-04): DelaySeconds × MaxAttempts
/// MUST stay well under RabbitMQ's default 30-min consumer_timeout — the loop is awaited INSIDE Consume,
/// holding the delivery un-acked for that window. Defaults 5s × 12 = 60s (30× margin).
/// </summary>
public sealed class ProbeOptions
{
    public int DelaySeconds { get; set; } = 5;
    public int MaxAttempts  { get; set; } = 12;
}
// Program.cs:  builder.Services.Configure<ProbeOptions>(builder.Configuration.GetSection("Probe"));
//              builder.Services.AddBaseConsoleRedis(builder.Configuration);   // D-01
```

### KeeperQueues + L2ProjectionKeys additions (D-08, D-03)
```csharp
// [CITED: src/Messaging.Contracts/KeeperQueues.cs]
public const string DeadLetter = "keeper-dlq";   // plain durable, NO x-message-ttl (D-08)

// [CITED: src/Messaging.Contracts/Projections/L2ProjectionKeys.cs]
/// <summary>D-03: probe scratch key — short-TTL write-then-delete; TTL is the crash net-zero net.</summary>
public static string KeeperProbe(string h) => $"{Prefix}keeper:probe:{h}";   // "skp:keeper:probe:{h}"
```

### appsettings.json Probe section (Redis key already present)
```jsonc
// src/Keeper/appsettings.json — ConnectionStrings:Redis already present (D-01 partially done)
"Probe": { "DelaySeconds": 5, "MaxAttempts": 12 }
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Per-`{queue}_error` default (every console today) | ONE consolidated TTL'd `skp-dlq-1` via custom error transport | This phase (D-05/06) | All 3 consoles' exhausted messages land in one queue. |
| Cancelled circuit-breaker (Phase 32) | Plain dead-lettering to `_error`, now consolidated to DLQ-1 | Phase 32→32.1 reverted; 36 consolidates | Idempotency layer preserved; DLQ-1 is the forensic sink. |
| Delayed-message-exchange plugin (`UseDelayedMessageScheduler`) | REMOVED (24.1) — no plugin dependency | Phase 24.1 | DLQ-1 must NOT reintroduce a plugin dependency; native `x-message-ttl` only. |

**Deprecated/outdated:**
- RabbitMQ delayed-exchange plugin: gone; do not reintroduce. Native TTL/DLX only.
- MassTransit v9+ `UseQueueBasedDelayedRedelivery`: exists but is v9 (commercial) — **not available** on the 8.5.5 Apache line. Do not plan around it.

## Validation Architecture

> nyquist_validation assumed enabled (no `.planning/config.json` override found disabling it).

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (v3 — `TestContext.Current.CancellationToken`), MassTransit.Testing `ITestHarness` for hermetic |
| Config file | none Keeper-specific; tests live in `tests/BaseApi.Tests/Keeper/` |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper&Category!=RealStack"` |
| Full suite command | `dotnet test` (hermetic) ; RealStack via `--filter Category=RealStack` against live compose |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| PROBE-01 | `Delay×Attempts` bound constraint | unit | `dotnet test --filter ProbeOptions_Bound` | ❌ Wave 0 |
| PROBE-02 | probe = read + write-then-delete; both-or-fault | unit (fake `IConnectionMultiplexer`/`IDatabase`) | `dotnet test --filter Probe_RequiresReadAndWrite` | ❌ Wave 0 |
| PROBE-03 | fail-then-succeed → re-inject by type | hermetic (`ITestHarness`, in-mem) | `dotnet test --filter Probe_Success_Reinjects` | ❌ Wave 0 |
| PROBE-04 | fail-to-max → park original `Fault<T>` to `keeper-dlq` | hermetic | `dotnet test --filter Probe_GiveUp_ParksToDlq` | ❌ Wave 0 |
| PROBE-05 | NO premature ack (consumed only after loop exits) | hermetic (assert ack timing / consumed-after) | `dotnet test --filter Probe_AcksOnlyAfterLoop` | ❌ Wave 0 |
| DLQ-01 | Keeper Send-fault → Immediate(N) → DLQ-1 | hermetic (throwing send endpoint) | `dotnet test --filter Keeper_SendFault_RetriesToDlq1` | ❌ Wave 0 |
| DLQ-02/04 | exhaustion routes to ONE `skp-dlq-1` uniformly | hermetic (in-mem harness w/ custom error transport) + RealStack | `dotnet test --filter Dlq1_Consolidated` | ❌ Wave 0 |
| PROBE-03/04 live | recover-both-paths + give-up against live stack | RealStack (sibling of `FaultRecoverySpikeE2ETests`) | `dotnet test --filter Category=RealStack&FullyQualifiedName~KeeperRecovery` | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper&Category!=RealStack"`
- **Per wave merge:** full hermetic `dotnet test` (exclude RealStack) — must be green across processor + orchestrator + Keeper (BaseConsole.Core change touches all).
- **Phase gate:** Phase-39 owns the 3×GREEN triple-SHA RealStack close gate; Phase 36's own gate is hermetic green + one RealStack recover/give-up pass.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs` — hermetic probe-logic (PROBE-01..05) with a fake `IDatabase`/`IConnectionMultiplexer` (no Redis); fail-then-succeed → re-inject, fail-to-max → park, no-premature-ack.
- [ ] `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` — hermetic in-mem harness asserting exhaustion lands in the consolidated error transport (DLQ-01/02/04).
- [ ] `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` — RealStack sibling of `FaultRecoverySpikeE2ETests`: live recover-both-paths (dispatch + result) + give-up park to `keeper-dlq`, net-zero `skp:*` teardown, keeper container rebuilt.
- [ ] Test double for `IConnectionMultiplexer`/`IDatabase` that throws `RedisConnectionException`/`RedisTimeoutException` on demand to simulate down-then-up. (StackExchange.Redis interfaces are mockable.)
- [ ] Update `KeeperDependencyFirewallTests` allow-list only if a new ref is added (none expected — Redis rides BaseConsole.Core).

*Framework already present (xUnit v3 + MassTransit.Testing); no install needed.*

## Security Domain

> `security_enforcement` assumed enabled (no config disabling it). Phase 36 is internal backend message routing — limited external surface.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Internal bus; RabbitMQ creds via config (unchanged). |
| V3 Session Management | no | N/A (no sessions). |
| V4 Access Control | no | N/A. |
| V5 Input Validation | yes (light) | Fault envelope is framework-typed; inner is a known contract. Log only `ExceptionType`+`Message` as STRUCTURED params (never interpolate) — existing Keeper convention (T-35-04/05). Do NOT log stack frames at Information. |
| V6 Cryptography | no | No crypto. `H` is a content hash, not a secret. |
| V7 Error/Logging | yes | Faulted-message forensics in `skp-dlq-1`/`keeper-dlq` may carry payloads — operators have broker access; no new exposure beyond existing `_error`. |

### Known Threat Patterns for MassTransit/RabbitMQ
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Log injection via exception text | Tampering | Structured params under fixed template holes (existing Keeper consumers already do this — keep it in any new log lines). |
| Unbounded `skp-dlq-1` growth (DoS on broker) | DoS | `x-message-ttl`=7d drains it (D-07); the whole point of the TTL. Verify it applies (Pitfall 2). |
| Poison-message loop (re-inject of a genuinely bad message) | DoS | The receiver's own `Immediate(N)` re-exhausts → re-faults → Keeper re-probes; if L2 is genuinely up but the message is bad, it loops through fault→recover. NOTE: this is the *existing* milestone behavior (Keeper recovers transient L2 faults only); not a Phase-36 regression, but worth flagging — a non-L2 fault that re-faults forever is a known model limitation, mitigated operationally by `keeper-dlq` only on probe give-up (L2-down), and by DLQ-1 TTL forensics for the rest. |

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Mechanism A custom `IFilter<ExceptionReceiveContext>` can faithfully reproduce the default `_error` move with a fixed `skp-dlq-1` destination in MT 8.5.5 | DLQ-1 Consolidation | HIGH — if the filter can't reproduce headers/redelivery correctly, fall to Mechanism B (documented). Planner must read `MassTransit.RabbitMqTransport` source to confirm. |
| A2 | `RedisConnectionException` and `RedisTimeoutException` both derive from `RedisException` (catch the superset) | Pattern 1 | LOW — if hierarchy differs, catch both explicitly. Verify against StackExchange.Redis 2.13.1 at plan time. |
| A3 | `keeper-dlq` declared-on-first-send is sufficient (vs explicit boot declaration) | Pattern 3 / Pitfall 1 | MEDIUM — close-gate net-zero (Phase 39) may require it present in both snapshots → prefer explicit no-consumer declaration. |
| A4 | `x-message-ttl` value is RabbitMQ milliseconds (int/long) via `SetQueueArgument` | Mechanism A | LOW — standard RabbitMQ semantics; verify the MT `SetQueueArgument` overload accepts the numeric type. |
| A5 | `GenerateFaultFilter`/`ErrorTransportFilter` are the public default error filters in MT 8.5.5 (per discussion 2945, version unstated) | DLQ-1 Consolidation | MEDIUM — names/visibility may differ in 8.5.5; confirm against the transport source. The CONCEPT (replace the move target, keep fault generation) holds regardless. |

## Open Questions (RESOLVED)

1. **Exact MT 8.5.5 error-transport filter API for Mechanism A.** — **RESOLVED:** handled by Plan 36-03 / Task 1, a `[BLOCKING] checkpoint:decision` that requires the executor to confirm the precise 8.5.5 API against `MassTransit.RabbitMqTransport` source (spike in a hermetic harness) before any base-library code, with Mechanism B as the documented fallback.
   - What we know: `ConfigureError` exposes the error pipeline; default = fault-gen + error-transport-move; a custom `IFilter<ExceptionReceiveContext>` can replace the move.
   - What's unclear: the precise public type names/constructors in 8.5.5 and how to resolve a fixed send endpoint inside the filter.
   - Recommendation: planner reads `MassTransit.RabbitMqTransport` 8.5.5 source (`MoveToErrorTransport*`) during planning; spike the filter in a hermetic harness before the base-library commit. If costly, use Mechanism B and flag the DLQ-03 immediacy gap.

2. **Orphan `{queue}_error` cleanup on the dev broker.** — **RESOLVED:** operator action documented in Plan 36-04 / Task 2 runbook (one-time broker reset before the Phase-39 close gate).
   - What we know: Mechanism A leaves old `{queue}_error` queues dormant; MT never deletes them.
   - What's unclear: whether the Phase-39 close-gate rabbitmq snapshot tolerates them.
   - Recommendation: plan a one-time broker reset (delete orphan `_error` + any pre-TTL `skp-dlq-1`) before the Phase-39 gate.

3. **`ProbeOptions` home (Keeper-local vs `Messaging.Contracts.Configuration`).** — **RESOLVED:** Keeper-local (PATTERNS.md Critical Correction; implemented in Plan 36-01 / Task 2).
   - Recommendation: Keeper-local — it's a Keeper-only knob (unlike `RetryOptions` which is shared). D-04 leaves this to discretion.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| RabbitMQ (compose) | DLQ topology, error transport, RealStack test | ✓ (compose stack) | 3.x (sk-rabbitmq, localhost:5673) | none |
| Redis (compose) | probe loop, RealStack | ✓ (localhost:6380 host / redis:6379 in-net) | per compose | none |
| .NET SDK + xUnit v3 | all tests | ✓ | existing | none |
| StackExchange.Redis 2.13.1 | probe client | ✓ (referenced) | 2.13.1 | none |
| MassTransit 8.5.5 | error transport | ✓ (referenced) | 8.5.5 (Apache cap) | none |

**Missing dependencies with no fallback:** none — full stack present.
**Missing dependencies with fallback:** none.

## Project Constraints (from codebase, no root CLAUDE.md found)
- **No root `./CLAUDE.md`** — verified absent. Constraints come from CONTEXT.md + existing code conventions.
- **Keeper dependency firewall** (`KeeperDependencyFirewallTests`): Keeper must NOT reference `BaseApi.Core`, `Microsoft.EntityFrameworkCore`, `Npgsql`, `Quartz`, `Cronos`. Redis rides `BaseConsole.Core` (allowed). Do NOT add a `StackExchange.Redis` ProjectReference to Keeper.csproj (D-01).
- **MassTransit 8.5.5 Apache cap** — do not plan around v9-only APIs.
- **Structured logging only** — exception text as params, never interpolated; no stack frames at Information (T-35-04/05).
- **Single endpoint-retry owner** on the shared Keeper endpoint (Pitfall 3) — keep the sibling definition's `ConfigureConsumer` a no-op.
- **Container rebuild before RealStack** — embedded SourceHash must match (project gotcha); rebuild keeper + any console whose `BaseConsole.Core` error transport changed.
- **Net-zero `skp:*`** — scratch key TTL guarantees it; teardown registers any minted keys (spike rig pattern).

## Sources

### Primary (HIGH confidence)
- Codebase (read directly): `src/Keeper/Consumers/Fault*Consumer*.cs`, `src/Keeper/Program.cs`, `src/Keeper/appsettings.json`, `src/BaseConsole.Core/DependencyInjection/{Messaging,ConsoleRedis}ServiceCollectionExtensions.cs`, `src/Orchestrator/Consumers/ResultConsumer*.cs`, `src/Messaging.Contracts/{KeeperQueues,OrchestratorQueues,ProcessorQueues,IExecutionCorrelated}.cs`, `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs`, `src/Messaging.Contracts/Configuration/RetryOptions.cs`, `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs`, `tests/BaseApi.Tests/Keeper/*`.
- `Directory.Packages.props` — MassTransit/RabbitMQ 8.5.5, StackExchange.Redis 2.13.1.
- CONTEXT.md (36, 33, 35), REQUIREMENTS.md, Context7 `/websites/masstransit_massient` (redelivery, error-queue, rabbitmq pages).
- masstransit.massient.com/documentation/concepts/exceptions — exhaustion = move to `_error`, NOT nack; `Fault<T>` published alongside.

### Secondary (MEDIUM confidence)
- github.com/MassTransit/MassTransit/discussions/2945 — `ConfigureError` default filter pair (`GenerateFaultFilter`+`ErrorTransportFilter`); omitting them stops `_error` writes.
- github.com/MassTransit/MassTransit/discussions/2990 — `_error` queue inherits base consumer config (incl. TTL arg).
- github.com/MassTransit/MassTransit/discussions/4817 — phatboyg: queue args immutable on live queue; MT never deletes queues.
- github.com/MassTransit/MassTransit/discussions/5868 — `SetQueueArgument` requires `AddConfigureEndpointsCallback`, not bus-level.
- masstransit.massient.com/documentation/configuration — `AddConfigureEndpointsCallback` pattern-match on `IRabbitMqReceiveEndpointConfigurator`.

### Tertiary (LOW confidence — flagged for validation)
- WebSearch: MT 8.0.7 added error/skipped queue name override topology hooks (no RabbitMQ shared-error-queue API confirmed) — validate against 8.5.5 source for Mechanism A.

## Metadata

**Confidence breakdown:**
- Probe loop + re-inject + keeper-dlq park: **HIGH** — every primitive is in `src/` or spike-proven LIVE (GATE_EXIT=0).
- `keeper-dlq` / scratch-key / ProbeOptions contracts: **HIGH** — direct additions to existing single-source-of-truth files.
- DLQ-1 consolidation mechanism: **MEDIUM** — the *facts* (move-not-nack, no built-in shared queue, ConfigureError pipeline, TTL-at-create) are verified; the *exact filter API in 8.5.5* (A1/A5) needs a source read at plan time. Mechanism A recommended with a documented Mechanism B fallback.
- Pitfalls: **HIGH** — drawn from codebase docs + maintainer statements + project memory.

**Research date:** 2026-06-05
**Valid until:** 2026-07-05 (stable — pinned package versions; MT/RabbitMQ behavior well-established)
