# Phase 32: Cancelled Circuit-Breaker — Research

**Researched:** 2026-06-04
**Domain:** MassTransit 8.5.5 retry-exhaustion → Fault fanout; Redis L2 cancelled-marker gate; Quartz idempotent unschedule reuse; .NET 8 metrics/logging
**Confidence:** HIGH (all five unknowns resolved against the actual codebase + MassTransit 8.5.5 docs; one MassTransit reality flagged as a Wave-0 verification risk)

## Summary

All WHAT is locked in `32-SPEC.md` (8 reqs) and all HOW-decisions are locked in `32-CONTEXT.md` (D-01…D-13). This research resolves the five remaining MassTransit/codebase placement unknowns so the planner can write concrete tasks.

The two load-bearing MassTransit facts both confirm the locked design: (a) on a single endpoint-level `UseMessageRetry(Immediate(Limit))` exhaustion MassTransit produces a `Fault<EntryStepDispatch>` **and** dead-letters to `_error` from the *same* mechanism `[CITED: masstransit exceptions docs]` (D-03 holds); (b) `Fault<T>.Message` carries the original message instance, so `Fault<EntryStepDispatch>.Message.WorkflowId` is available to the fanout consumer `[CITED: masstransit exceptions docs]` (D-06 holds — no fallback needed).

The one MassTransit reality that complicates the locked design: `context.GetRetryAttempt()` has documented reliability caveats — it returns 0 every time under *consumer-level* retry config and under *stacked* retry policies `[CITED: MassTransit#1217, #3216]`. This project uses a SINGLE retry policy at the RECEIVE-ENDPOINT level (the reliable case), so the SPEC's locked `GetRetryAttempt() == Limit` check is expected to work — but the exact attempt-numbering (0-based first-delivery) MUST be pinned by a hermetic Wave-0 test before the breaker seam is built. This is surfaced as Risk R1 with a fallback.

**Primary recommendation:** Implement the final-attempt marker-set as an **in-consumer try/catch guard around the infra ops inside `EntryStepDispatchConsumer.Consume`** (not a separate consume filter, not a retry observer) — it is the only seam that already holds both the Redis handle and `dispatch.WorkflowId`, keeps effect-first ordering trivially correct (set marker → re-throw → MassTransit publishes Fault + dead-letters), and reuses the consumer's existing INFRA-throw discipline. Gate it on `ctx.GetRetryAttempt() == retryOptions.Value.Limit` (the SPEC-locked predicate), pinned by a Wave-0 attempt-count test.

## User Constraints (from CONTEXT.md)

### Locked Decisions (D-01…D-13 — research HOW to honor, not WHETHER)
- **D-01:** Breaker trips on infra-fault retry-budget exhaustion ONLY — `GetRetryAttempt() == RetryOptions.Limit` (the Phase-31 single source feeding `UseMessageRetry`). Business `ProcessAsync` throws stay immediate-`Failed`-and-acked (D-15 contract) and trip nothing. Trip on first exhaustion; blast radius = whole workflow.
- **D-02:** Effect-first — processor sets `cancelled[workflowId]=true` in L2 BEFORE the infra fault propagates.
- **D-03:** Future-fire signal = MassTransit automatic `Fault<EntryStepDispatch>` (fanout) on retry exhaustion. No custom event/publish. The SAME exhaustion dead-letters to `_error` — bus-down backstop falls out of one mechanism.
- **D-04:** NO `Cancelled` ExecutionResult sent to `orchestrator-result` for the breaker path (shared competing-consumer queue = wrong channel for a broadcast halt).
- **D-05:** In-flight stop = shared L2 `cancelled[workflowId]` marker; every receiver checks before processing and ack-and-discards. `workflowId`-keyed (concurrent fires stop too).
- **D-06:** Future-fire stop = `IConsumer<Fault<EntryStepDispatch>>` on a per-replica `InstanceId`+`Temporary` fan-out endpoint (mirrors Start/Stop in `Program.cs:31-46`, NOT shared `orchestrator-result`). Extract `WorkflowId` from `Fault.Message`, resolve `jobId` from L1 (`store.TryGet → wf.JobId`; absent ⇒ no-op), unschedule via existing Stop/Teardown machinery. Only schedule-owning replica acts.
- **D-07:** Marker has NO TTL; Stop/Teardown does NOT clear it.
- **D-08:** Resume is manual — operator clears marker + re-`POST /orchestration/start` (existing endpoint; no new API surface).
- **D-09:** Idempotent on re-exhaustion — redelivery re-sets marker (idempotent) + re-publishes Fault (idempotent halt).
- **D-10:** Increment `processor_dispatch_deduped_total` (`EntryStepDispatchConsumer.cs:76`) + `orchestrator_result_deduped_total` (`ResultConsumer.cs:65`) at the existing `flag[H]=="Ack" → return` gates, tagged `ProcessorId`, on the existing Phase-30 meters.
- **D-11:** `workflow_cancelled_total` counter on breaker trip + structured WARN/ERROR log carrying `workflowId`/`stepId`/`processorId`/`H`.
- **D-12:** Remove `StepEntryCondition.PreviousCancelled (3)` — leave `3` as a numeric gap (do NOT renumber 0/1/2/4/5). `IsInEnum()` auto-rejects 3. KEEP `StepOutcome.Cancelled (3)`; update its mirror comment.
- **D-13:** Fault path is OUTSIDE the Phase-31 `flag[H]` CAS dedup — carries no `H`, seeds no `flag=Pending`, gets no CAS gate. Absorbed by natural idempotency. **Downstream agents MUST NOT pattern-match the Completed-result `flag[H]=Pending` pre-write onto the fault path.**

### Claude's Discretion
- Marker key shape: `L2ProjectionKeys.Cancelled(Guid workflowId) => $"{Prefix}cancelled:{workflowId:D}"` in `L2ProjectionKeys` (follows existing `skp:` flat convention).
- Exact metric names / tag-key literals (Phase-30 pinned-lowercase convention, decoupled from C# enum names).
- Fault-consumer endpoint registration/naming details (per-instance `Temporary` fan-out, mirroring Start/Stop definitions).
- The per-message marker read adds one Redis `GET` per received message — accepted.

### Deferred Ideas (OUT OF SCOPE)
- Persistent business-failure circuit-breaking (B2 — making `ProcessAsync` exceptions retry-to-exhaustion).
- Dedicated `POST /orchestration/resume` endpoint or auto-cooldown (TTL) resume.
- Renumbering `StepEntryCondition` 0/1/2/4/5.
- Routing a `Cancelled` ExecutionResult over `orchestrator-result` for the halt.
- Putting the Fault path through `flag[H]` CAS dedup.
- Changing the token-cancellation `Cancelled` business outcome (EXEC-08).
- Multi-replica deployment itself (design is multi-replica-correct; one replica runs today).

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| req-1 | Breaker trips on infra-exhaustion only (`GetRetryAttempt()==Limit`); business stays Failed | Unknown 1 — in-consumer guard gated on attempt==Limit; Risk R1 (attempt-numbering) |
| req-2 | Effect-first `cancelled[workflowId]` marker, no TTL, set before fault | Unknown 1 + `L2ProjectionKeys.Cancelled` builder; `StringSetAsync` with `expiry: null` |
| req-3 | Check-and-drop gate at both consumers | Unknown 3 — one `StringGetAsync` at top of each `Consume`, AFTER the `flag[H]` gate |
| req-4 | Fault fanout → Quartz unschedule, idempotent | Unknown 2 — `IConsumer<Fault<EntryStepDispatch>>` per-replica + `UnscheduleOnlyAsync` |
| req-5 | No `Cancelled` ExecutionResult on breaker path; `_error` retained | D-03/D-04 — falls out of single exhaustion mechanism; no code routes Cancelled here |
| req-6 | Remove `PreviousCancelled (3)`; keep `StepOutcome.Cancelled (3)` | Unknown 5 — 2 source touch-points only; blast radius confirmed minimal |
| req-7 | Dedup + trip counters + structured trip log | Unknown 4 — extend ProcessorMetrics/OrchestratorMetrics; reuse OutcomeLabel/BeginScope idioms |
| req-8 | Manual resume; Fault path outside `flag[H]` dedup | D-08/D-13 — fault consumer reads/writes no `flag[H]` key; resume = clear marker + re-Start |

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Detect final-attempt exhaustion + set marker | **Processor** (`EntryStepDispatchConsumer`) | — | Only the processor holds the Redis handle + `dispatch.WorkflowId` at the infra-throw site; the orchestrator never sees the dispatch retry |
| Publish `Fault<EntryStepDispatch>` | **MassTransit runtime** (processor endpoint) | — | Automatic on retry exhaustion; no app code (D-03) |
| Consume `Fault<EntryStepDispatch>` + unschedule | **Orchestrator** (new fault consumer, per-replica) | Quartz/`WorkflowScheduler` | The schedule lives in the orchestrator's in-process Quartz RAMJobStore + L1; only the orchestrator can resolve `jobId` and `DeleteJob` |
| Check-and-drop in-flight | **Both** (`EntryStepDispatchConsumer` + `ResultConsumer`) | Redis L2 | Shared `workflowId`-keyed marker is the only multi-replica-correct in-flight stop |
| Marker key builder | **Shared contract** (`Messaging.Contracts/L2ProjectionKeys`) | — | Single source of truth consumed by both processor and orchestrator |
| Dedup/trip counters + trip log | **Processor + Orchestrator** observability | Phase-30 meters / Phase-29 logging | Code-owned meters already exist on each side |
| Enum removal | **BaseApi.Service** (`StepEntryCondition`) | `Messaging.Contracts/StepOutcome` comment | Validation surface lives in BaseApi; outcome mirror comment in contracts |

## The Five Unknowns — Recommended Decisions

### Unknown 1 — Final-attempt marker-set seam (THE primary unknown)

**Recommendation: in-consumer try/catch guard around the infra ops inside `EntryStepDispatchConsumer.Consume`, gated on `ctx.GetRetryAttempt() == retryOptions.Value.Limit`.** `[VERIFIED: codebase]` `[CITED: MassTransit#1217]`

**Why this seam (and not the alternatives):**

| Seam | Verdict | Reason |
|------|---------|--------|
| **In-consumer guard** (RECOMMENDED) | ✅ | Already holds the Redis `db` handle + `dispatch.WorkflowId`; effect-first ordering is trivially correct (set marker → re-throw → MassTransit publishes Fault); reuses the consumer's existing INFRA-throw discipline (the infra ops at `:76`, `:86`, `:169`, `:189`, `:201`, `:216` already throw uncaught into the retry). Set-marker-then-rethrow is one local change. |
| **Consume filter** `IFilter<ConsumeContext<EntryStepDispatch>>` | ⚠️ viable but worse | An analog EXISTS (`InboundExecutionScopeConsumeFilter<T>`, `src/BaseConsole.Core/Messaging/`) so the idiom is known. BUT the filter would have to (a) re-resolve `IConnectionMultiplexer` from DI, (b) re-read `WorkflowId` off `context.Message` (only works if the message is `EntryStepDispatch` — the filter is bus-wide open-generic today, so a typed filter is needed), (c) `try { await next.Send(ctx) } catch when (GetRetryAttempt()==Limit) { setMarker; throw; }`. More indirection, a second Redis resolution, and it duplicates the WorkflowId-extraction the consumer already does. Reserve as the fallback if the guard proves to entangle business/infra catches. |
| **Retry observer / `IRetryObserver.PostFault`/`RetryComplete`** | ❌ | `[CITED: MassTransit#1510]` exists, but the observer fires OUTSIDE the consumer scope (no Redis handle, no typed message guarantee), runs on EVERY retry not just the final, and ordering vs the Fault publish is not the clean effect-first the design wants. Rejected. |
| **`r.Ignore<T>` / exhaustion callback** | ❌ | No `OnRetryExhausted` callback exists in MT 8 `[CITED: masstransit retry docs]`. Rejected. |

**Concrete idiom** (extends the existing consumer; the infra ops it wraps are the L2 read at `:86`, the output write at `:169`, the manifest write at `:189`, the outbound Pending seed at `:201`, and the `Send` inside `SendResult`):

```csharp
// EntryStepDispatchConsumer ctor — add the two deps the guard needs:
//   IOptions<RetryOptions> retryOptions   (mirror ResultConsumerDefinition's source)
//   (logger + metrics already injected)

public async Task Consume(ConsumeContext<EntryStepDispatch> ctx)
{
    var dispatch = ctx.Message;
    // ... existing METRIC-05 increment, db handle, flag[H] gate (:76) ...

    // ---- NEW (req-3): check-and-drop BEFORE doing any work, AFTER the flag[H] gate ----
    // (see Unknown 3 for exact placement relative to :76)
    if (await db.StringGetAsync(L2ProjectionKeys.Cancelled(dispatch.WorkflowId)) == "true")
    {
        metrics.... // NO dedup counter here (this is the cancelled drop, not a flag[H]==Ack drop)
        return;     // ack-and-discard
    }

    try
    {
        // ... existing input-resolve / ExecuteAsync / write / send body unchanged ...
    }
    catch (Exception ex) when (
        IsInfra(ex) &&                                   // only INFRA faults trip the breaker (D-01)
        ctx.GetRetryAttempt() == retryOptions.Value.Limit) // ONLY the final exhausted attempt (req-1)
    {
        // EFFECT-FIRST (D-02): set the no-TTL marker BEFORE re-throwing so the marker is
        // observable before MassTransit publishes Fault<EntryStepDispatch> + dead-letters to _error.
        await db.StringSetAsync(
            L2ProjectionKeys.Cancelled(dispatch.WorkflowId), "true", expiry: null);  // NO TTL (D-07)

        metrics.WorkflowCancelled.Add(1,                                              // req-7 / D-11
            new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")));
        logger.LogWarning(                                                            // structured trip log (D-11)
            "Breaker tripped — workflow {WorkflowId} cancelled on infra-exhaustion (step {StepId}, processor {ProcessorId}, H {H})",
            dispatch.WorkflowId, dispatch.StepId, dispatch.ProcessorId, dispatch.H);

        throw;   // re-throw → MassTransit publishes Fault<EntryStepDispatch> + dead-letters to _error (D-03)
    }
}
```

**Critical sub-decisions:**
- **`IsInfra` reuse:** the consumer must distinguish INFRA (Redis/Send) from BUSINESS so business `ProcessAsync` throws never reach this catch. Today business throws are caught EARLIER (`:123`/`:129`) and never escape — so the `IsInfra(ex)` predicate in the new catch is a belt-and-braces guard against future business-throw leaks. Reuse the existing `WorkflowLifecycle.IsInfra(Exception)` static (Redis* exceptions) — but note a broker `Send` fault is NOT a Redis exception. **Plan decision needed:** either widen the catch to "any exception that escaped the existing business catches" (simplest — by this point in the body only infra faults remain uncaught) OR define a processor-side `IsInfra`. RECOMMEND the former: the existing `:123`/`:129` catches already convert ALL business outcomes to acked sends, so anything that reaches the outer try's catch IS infra by construction — gate on `GetRetryAttempt()==Limit` alone and drop the `IsInfra` predicate. (Document this invariant in the task.)
- **Attempt numbering:** the SPEC locks `== Limit` (0-based: first delivery=0, the Limit-th retry=Limit). See Risk R1 — pin with a Wave-0 hermetic test before relying on it.
- **Marker value:** `"true"` string (matches the `== "true"` check-and-drop read). Any non-empty sentinel works; pick one literal and use it at both set and check sites (single source — consider a `const` next to the key builder).

**Analog files:** infra-throw discipline = the existing uncaught Redis/Send ops in `EntryStepDispatchConsumer.cs` (D-15 doc comment lines 27-40); `IOptions<RetryOptions>` injection = `ResultConsumerDefinition.cs:24-30` and `ProcessorStartupOrchestrator.cs:151`.

### Unknown 2 — `Fault<EntryStepDispatch>` consumer (orchestrator, per-replica fanout)

**Recommendation: new `FaultUnscheduleConsumer : IConsumer<Fault<EntryStepDispatch>>` + `FaultUnscheduleConsumerDefinition`, registered on the per-replica `InstanceId`+`Temporary` fan-out endpoint in `Program.cs`, mirroring Start/Stop exactly.** `[VERIFIED: codebase + masstransit Fault<T> docs]`

**Confirmed MassTransit facts:**
- `Fault<T>.Message` IS the original message instance — `Fault<EntryStepDispatch>.Message.WorkflowId` is directly available `[CITED: masstransit exceptions docs]`. The `Fault<T>` interface: `{ Guid FaultId; Guid? FaultedMessageId; DateTime Timestamp; ExceptionInfo[] Exceptions; HostInfo Host; T Message; }`. **D-06's assumption holds — no fallback needed.**
- MassTransit publishes `Fault<T>` automatically on retry exhaustion (no app publish) AND moves the message to `_error` from the same mechanism `[CITED: masstransit exceptions docs]` — D-03 holds.

**Concrete idiom:**

```csharp
// src/Orchestrator/Consumers/FaultUnscheduleConsumer.cs  (analog: StopOrchestrationConsumer.cs)
public sealed class FaultUnscheduleConsumer(
    WorkflowLifecycle lifecycle,
    ILogger<FaultUnscheduleConsumer> logger) : IConsumer<Fault<EntryStepDispatch>>
{
    public async Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context)
    {
        var workflowId = context.Message.Message.WorkflowId;   // Fault.Message IS the original EntryStepDispatch
        logger.LogWarning("Fault halt — unscheduling workflow {WorkflowId}", workflowId);
        // Reuse the idempotent keep-L1 unschedule (D-06). Absent-from-L1 ⇒ business no-op inside lifecycle.
        // NOTE: NO flag[H] read/write here (D-13). NO L2 marker write (the processor already set it).
        await lifecycle.UnscheduleOnlyAsync(workflowId, context.CancellationToken);
    }
}
```

```csharp
// src/Orchestrator/Consumers/FaultUnscheduleConsumerDefinition.cs (analog: StopOrchestrationConsumerDefinition.cs)
public sealed class FaultUnscheduleConsumerDefinition : ConsumerDefinition<FaultUnscheduleConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;
    public FaultUnscheduleConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = "orchestrator";   // SHARED base name → groups onto the per-replica fan-out endpoint with Start/Stop
    }
    protected override void ConfigureConsumer(IReceiveEndpointConfigurator ep,
        IConsumerConfigurator<FaultUnscheduleConsumer> cc, IRegistrationContext ctx)
        => ep.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));  // bounded infra retry of the unschedule's own Send/Quartz faults
}
```

```csharp
// src/Orchestrator/Program.cs — inside the AddBaseConsoleMessaging consumer lambda (mirror :40-43)
x.AddConsumer<FaultUnscheduleConsumer, FaultUnscheduleConsumerDefinition>()
    .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });   // per-replica fan-out (D-06)
```

**Endpoint-naming decision:** Start/Stop already share `EndpointName = "orchestrator"` + the same `instanceId` closure (`Program.cs:35`) → MassTransit groups all three consumers onto ONE per-replica queue `orchestrator-{instanceId}` (a fan-out broadcast). Giving the fault consumer the SAME `EndpointName = "orchestrator"` + the SAME `.Endpoint(InstanceId=instanceId, Temporary=true)` puts it on that same per-replica broadcast queue — exactly what D-06 wants (every replica receives the published Fault; only the schedule-owning replica's `UnscheduleOnlyAsync` does work, others no-op via `store.TryGet` miss). It deliberately AVOIDS the shared `orchestrator-result` competing-consumer queue (D-04).

**Risk note:** Start/Stop are SENT point-to-point (`StopOrchestration` is a Send to the fan-out endpoint by the WebApi); `Fault<T>` is PUBLISHED by MassTransit. A published `Fault<EntryStepDispatch>` routes to every queue subscribed to `Fault<EntryStepDispatch>` — i.e. every replica's `orchestrator-{instanceId}` temp queue that has the consumer bound. This is the desired fanout. Confirm at plan-time that no OTHER consumer in the system subscribes `Fault<EntryStepDispatch>` (grep: none today). See Risk R2.

**Analog files:** `StopOrchestrationConsumer.cs` (body shape), `StopOrchestrationConsumerDefinition.cs` (definition + shared EndpointName), `Program.cs:42-43` (registration + `.Endpoint(InstanceId/Temporary)`), `WorkflowLifecycle.UnscheduleOnlyAsync` (the reused idempotent unschedule).

### Unknown 3 — Check-and-drop gate placement (both consumers)

**Recommendation: read `cancelled[workflowId]` with one `StringGetAsync` placed immediately AFTER the existing `flag[H]=="Ack"` gate and BEFORE any business work, at the top of both `Consume` bodies. The cancelled-check does NOT seed/read `flag[H]`.** `[VERIFIED: codebase]`

**Ordering — cancelled-check goes AFTER the flag[H] gate:**
- The `flag[H]` gate (`EntryStepDispatchConsumer.cs:76`, `ResultConsumer.cs:65`) is a pure read-and-return; it neither seeds nor depends on the cancelled marker. Putting the cancelled-check second means a duplicate-of-an-already-Acked message exits via the existing dedup path (and increments the D-10 dedup counter) without an extra cancelled read for the common dedup case. Either order is functionally correct (both are read-only ack-and-discards); AFTER is marginally cheaper and keeps the Phase-31 gate visually first/untouched.
- **The cancelled-check MUST NOT touch `flag[H]`** (D-13): it reads `L2ProjectionKeys.Cancelled(workflowId)`, never `L2ProjectionKeys.Flag(...)`. It seeds no Pending. It is a plain `GET` + `return`.

**Concrete idiom (processor — `EntryStepDispatchConsumer`, after `:76`):**
```csharp
if ((string?)await db.StringGetAsync(L2ProjectionKeys.Flag(dispatch.H)) == "Ack")   // EXISTING :76 — add D-10 counter here
{
    metrics.DispatchDeduped.Add(1, new KeyValuePair<string,object?>("ProcessorId", context.Id!.Value.ToString("D")));
    return;
}
// NEW (req-3 / D-05): check-and-drop for a cancelled workflow. INFRA read (no catch) → propagates.
if ((string?)await db.StringGetAsync(L2ProjectionKeys.Cancelled(dispatch.WorkflowId)) == "true")
    return;   // ack-and-discard: no advancement, no rollback, no dead-letter, NO dedup counter
```

**Orchestrator — `ResultConsumer`, after `:65`:** identical shape using `m.WorkflowId`. Place AFTER the `flag[H]` gate (`:65`→add `orchestrator_result_deduped_total` counter) and BEFORE the L1 `store.TryGet` (`:69`).

**Decision — workflowId source:** both messages already carry `WorkflowId` (`EntryStepDispatch.WorkflowId`, `ExecutionResult.WorkflowId` via `IExecutionCorrelated`). No L1/L2 lookup needed to get it.

**Analog files:** the existing dedup gates at `EntryStepDispatchConsumer.cs:72-77` and `ResultConsumer.cs:62-66` (same `(string?)await db.StringGetAsync(...) == sentinel → return` idiom).

### Unknown 4 — Metrics + trip-log seam

**Recommendation: add three counters across the two existing meters; increment the two dedup counters at the existing `flag[H]=="Ack"` return points; increment the trip counter + emit the WARN log inside the Unknown-1 catch.** `[VERIFIED: codebase]`

**Counters to add (snake_case, NO `_total` suffix in the instrument name — the collector appends it; D-03/Phase-30 convention — see `ProcessorMetrics`/`OrchestratorMetrics` doc comments):**

| Counter (SPEC name) | Instrument name in code | Meter | Increment site |
|---------------------|-------------------------|-------|----------------|
| `processor_dispatch_deduped_total` | `processor_dispatch_deduped` | `ProcessorMetrics` (`BaseProcessor`) | `EntryStepDispatchConsumer.cs:76` Ack-return |
| `orchestrator_result_deduped_total` | `orchestrator_result_deduped` | `OrchestratorMetrics` (`Orchestrator`) | `ResultConsumer.cs:65` Ack-return |
| `workflow_cancelled_total` | `workflow_cancelled` | `ProcessorMetrics` (`BaseProcessor`) — trip is processor-side | Unknown-1 catch |

> **Verify the `_total` convention at plan-time (Risk R3):** existing instruments (`processor_dispatch_consumed`, `orchestrator_result_consumed`) carry NO `_total` and rely on the collector's `add_metric_suffixes` to append `_total`. The SPEC writes the counter names WITH `_total` (the Prometheus-exposed name). To stay consistent with Phase-30, the C# instrument name OMITS `_total` and the collector adds it. Confirm the collector config still has `add_metric_suffixes` on — otherwise the SPEC's `_total` names won't match. (Phase-30 already depends on this; should be intact.)

**Tagging (Phase-30 pinned convention):** every counter tagged `ProcessorId` (PascalCase tag key, value `.ToString("D")`), inherits ambient `service_instance_id`. **NO `workflowId` label** (cardinality — T-30-04). The dedup counters need NO `outcome` tag.

**Counter construction idiom** (extend the existing ctors — `ProcessorMetrics.cs:36-41`, `OrchestratorMetrics.cs:33-38`):
```csharp
// ProcessorMetrics ctor
DispatchDeduped  = meter.CreateCounter<long>("processor_dispatch_deduped");   // D-10
WorkflowCancelled = meter.CreateCounter<long>("workflow_cancelled");          // D-11
// OrchestratorMetrics ctor
ResultDeduped = meter.CreateCounter<long>("orchestrator_result_deduped");     // D-10
```

**Trip log (D-11):** structured WARN (or ERROR) carrying `workflowId`/`stepId`/`processorId`/`H` as message-template fields (shown in the Unknown-1 idiom). The four ids are server-minted Guids/hash — safe as template args (consistent with `InboundExecutionScopeConsumeFilter` security note T-18-04: ids as scope/structured values, never as a tainted template). The inbound `InboundExecutionScopeConsumeFilter` already opens a MEL scope with `WorkflowId`/`StepId`/`ProcessorId` on the dispatch — so the log line inherits those as scope attributes too; including them explicitly in the template per D-11 is additive and explicit.

**Analog files:** `ProcessorMetrics.cs` / `OrchestratorMetrics.cs` (meter+counter construction), `EntryStepDispatchConsumer.cs:62-63` and `:239-241` (increment idiom with `ProcessorId` tag + `OutcomeLabel`), `ResultConsumer.cs:55` (top-of-consume increment).

### Unknown 5 — Enum removal blast radius

**Recommendation: delete the single line `PreviousCancelled = 3,` from `StepEntryCondition.cs:19`; update the mirror comment in `StepOutcome.cs:20`. No other source change compiles-breaks.** `[VERIFIED: grep src/]`

**Confirmed blast radius (grep `PreviousCancelled` across `src/`):**
- `src/BaseApi.Service/Features/Step/StepEntryCondition.cs:19` — the member definition (DELETE this line; leaves `0,1,2,4,5`).
- `src/Messaging.Contracts/StepOutcome.cs:20` — `Cancelled = 3, // == StepEntryCondition.PreviousCancelled` — a COMMENT only (update per D-12: `Cancelled` is special-cased by the consumer, not matched by `SelectNext`).
- **No other `src/` references.** `StepAdvancement.SelectNext` (`src/Orchestrator/Dispatch/StepAdvancement.cs:39-42`) matches `next.EntryCondition == (int)outcome || == Always(4)` — it never names `PreviousCancelled`, and a `Cancelled(3)` outcome simply finds no `EntryCondition==3` successor (no member 3 exists post-removal) and yields nothing. ✅ confirms "Cancelled advances no successor".

**Confirmations against acceptance criteria:**
- `0/1/2/4/5` intact (only line 19 removed; no renumber). ✅
- `StepDtoValidator.IsInEnum()` (`StepDtoValidator.cs:38` Create, `:69` Update — both `RuleFor(x => x.EntryCondition).IsInEnum()`) auto-rejects `EntryCondition == 3` once `3` is not a defined member. ✅ (FluentValidation `IsInEnum` rejects any value not defined on the enum.)
- `StepOutcome.Cancelled = 3` KEPT — still emitted by `BuildCancelled` (`EntryStepDispatchConsumer.cs:289`) for the EXEC-08 token-cancellation outcome (unchanged). ✅
- "No live step has `EntryCondition == 3`" — a DATA assertion, not a code change. Dual-pipeline steps use `Always(4)` (per D-12). Plan should include a verification query (see Validation Architecture / Environment).

**Analog/touch files:** `StepEntryCondition.cs` (delete line), `StepOutcome.cs` (comment), `StepDtoValidator.cs` (no change — behavior falls out of `IsInEnum`). A unit test asserting `EntryCondition==3` fails validation is the falsifiable proof.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Future-fire stop signal | A custom `Cancelled`/`StopFire` event + explicit publish | MassTransit automatic `Fault<EntryStepDispatch>` (D-03) | The user explicitly wants the fanout + `_error` backstop from ONE mechanism; a custom event re-implements what retry-exhaustion already publishes |
| Quartz unschedule | New `DeleteJob` logic in the fault consumer | `WorkflowLifecycle.UnscheduleOnlyAsync` (reuse, D-06) | Already idempotent (absent-from-L1 no-op), keep-L1 drain semantics, jobId-addressed `DeleteJob` (Pitfall 4c) |
| Fault-path dedup | A `flag[H]` CAS gate on the fault consumer | Natural idempotency (D-13) | Unschedule + marker SET are both idempotent; a CAS gate would be wrong (the fault carries no `H`) |
| Reading the retry limit | A second hard-coded `3` or a new config key | `IOptions<RetryOptions>.Value.Limit` (the SAME source feeding `UseMessageRetry`) | Single source of truth — the breaker check cannot desync from the retry budget (D-01) |
| Marker key string | Inline `$"skp:cancelled:{id}"` at each site | `L2ProjectionKeys.Cancelled(workflowId)` (one builder) | Single-source key shape; a format change can't desync writer/reader (the whole point of `L2ProjectionKeys`) |
| Metric label literals | `outcome.ToString().ToLowerInvariant()` per increment | Pinned lowercase const literals (Phase-30 `OutcomeLabel` pattern) | Decouples Prometheus labels from C# enum renames; no per-send allocation |

**Key insight:** This phase is almost entirely *reuse + wiring*. The only genuinely new logic is the final-attempt catch (Unknown 1) and the one-line check-and-drop (Unknown 3); everything else attaches existing idempotent machinery to a new trigger.

## Common Pitfalls

### Pitfall 1: Trusting `GetRetryAttempt()` without pinning the attempt numbering
**What goes wrong:** `GetRetryAttempt()` returns 0 every time under consumer-level config or stacked retry policies `[CITED: MassTransit#1217, #3216]`. Even in the working (single endpoint-level policy) case, 0-based-vs-1-based and "first delivery counts as attempt 0" must be confirmed, or the breaker trips one attempt early/late (or never).
**How to avoid:** Wave-0 hermetic test (see Risk R1) that drives a real `Immediate(Limit)` endpoint with a deterministically-throwing infra op and asserts the exact `GetRetryAttempt()` value on the delivery that should trip. The SPEC locks `== Limit`; the test PROVES `Limit` is the right boundary value for this config.
**Warning sign:** the breaker never trips in the real-stack E2E, or trips on attempt 1.

### Pitfall 2: Catching business throws in the breaker catch
**What goes wrong:** if the new final-attempt catch is too broad it could swallow a business `ProcessAsync` throw and trip the breaker on a business failure (violates D-01/req-1).
**How to avoid:** the existing `:123`/`:129` catches already convert ALL business outcomes to acked sends BEFORE the outer try's catch — so anything reaching the breaker catch is infra by construction. Gate ONLY on `GetRetryAttempt()==Limit`, and place the breaker catch as the OUTERMOST catch (after the business catches). Add a unit test: a `ProcessAsync` throw yields `Failed` + no marker set + no re-throw.

### Pitfall 3: Marker-set with a TTL or via a TTL'd helper
**What goes wrong:** copying the existing `StringSetAsync(..., expiry: TimeSpan.FromSeconds(...))` idiom (used everywhere else in the consumer for `data`/`flag` keys) would give the cancelled marker a TTL → a self-expiring breaker (violates D-07).
**How to avoid:** the marker write MUST be `StringSetAsync(key, "true", expiry: null)` (or the no-expiry overload). Add a test asserting `TTL skp:cancelled:{id}` returns `-1`.

### Pitfall 4: Fault consumer seeding `flag[H]=Pending`
**What goes wrong:** an agent pattern-matching the Completed-result pre-write (`EntryStepDispatchConsumer.cs:199-203`) onto the fault path would seed a `flag[H]=Pending` and add a CAS gate — explicitly forbidden by D-13.
**How to avoid:** the fault consumer reads/writes NO `skp:flag:*` key. Add a test asserting the fault consumer touches no flag key (and the fault carries no `H`).

### Pitfall 5: Fault consumer on the wrong endpoint
**What goes wrong:** registering the fault consumer WITHOUT `.Endpoint(InstanceId/Temporary)` (or with `EndpointName = OrchestratorQueues.Result`) puts it on a shared competing-consumer queue → only ONE replica gets the fault → the schedule-owning replica might miss it (violates D-04/D-06 multi-replica correctness).
**How to avoid:** SAME `EndpointName = "orchestrator"` + SAME `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` as Start/Stop. Verify the registration mirrors `Program.cs:42-43` exactly.

### Pitfall 6: Stop/Teardown clearing the marker
**What goes wrong:** the fault halt reuses `UnscheduleOnlyAsync`; if anyone adds a marker-clear to Stop/Teardown the breaker silently disarms (violates D-07).
**How to avoid:** `WorkflowLifecycle` is documented READ-ONLY against L2 (`:14-16`) and writes zero `skp:` keys — keep it that way. The marker is cleared ONLY by the manual resume (operator), never by code.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (`tests/BaseApi.Tests`), real-stack E2E in the same project (Testcontainers/compose-backed) |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (MTP — `-- --filter-class` per MEMORY) |
| Quick run command | `dotnet test tests/BaseApi.Tests -- --filter-class "*<HermeticFactsClass>"` |
| Full suite / close gate | `pwsh .planning/phases/32-*/phase-32-close.ps1` (3×GREEN + triple-SHA) — authored in the real-stack plan |

### Phase Requirements → Test Map
| Req | Behavior | Test type | Automated command | File exists? |
|-----|----------|-----------|-------------------|-------------|
| req-1 | Breaker trips ONLY on infra-exhaustion at `GetRetryAttempt()==Limit`; business stays Failed | unit (hermetic) | `dotnet test tests/BaseApi.Tests -- --filter-class "*BreakerTriggerFacts"` | ❌ Wave 0 |
| req-1 | Exact attempt-number at exhaustion (PIN) | hermetic (real `Immediate(Limit)` endpoint, fake infra throw) | `... --filter-class "*RetryAttemptNumberingFacts"` | ❌ Wave 0 (Risk R1) |
| req-2 | Marker set effect-first, no TTL (`TTL==-1`) | unit (fake/real Redis) | `... --filter-class "*CancelledMarkerFacts"` | ❌ Wave 0 |
| req-3 | Both consumers ack-and-discard when marker set; other workflows unaffected | unit | `... --filter-class "*CheckAndDropFacts"` | ❌ Wave 0 |
| req-4 | Fault consumer resolves jobId from L1 + unschedules; absent-L1 + duplicate = idempotent no-op | unit (fake L1 + fake scheduler) | `... --filter-class "*FaultUnscheduleFacts"` | ❌ Wave 0 |
| req-4 | `Fault<EntryStepDispatch>.Message.WorkflowId` extraction (MT binding) | hermetic (in-memory MT harness) | `... --filter-class "*FaultConsumerBindingFacts"` | ❌ Wave 0 (Risk R2) |
| req-5 | No `Cancelled` ExecutionResult to `orchestrator-result` on breaker path; `_error` still gets the message | real-stack E2E | close-gate E2E (`*CancelledCircuitBreakerE2ETests`) | ❌ Wave N |
| req-6 | `EntryCondition==3` fails `IsInEnum`; `StepOutcome.Cancelled==3` kept; no member 3 | unit | `... --filter-class "*StepEntryConditionEnumFacts"` | ❌ Wave 0 |
| req-6 | No live step row has `EntryCondition==3` | data assertion (DB query) | see Environment Availability | ❌ Wave 0 |
| req-7 | Dedup counters increment once per drop; `workflow_cancelled` once per trip; trip log carries 4 fields; no `workflowId` label | unit (MeterListener) + log capture | `... --filter-class "*BreakerMetricsFacts"` | ❌ Wave 0 |
| req-8 | Resume = clear marker + re-Start re-fires; Fault path touches no `flag[H]`; 2 faults == 1 end-state | real-stack E2E + unit | E2E (`*CancelledCircuitBreakerE2ETests`) + `*FaultIdempotencyFacts` | ❌ Wave N / Wave 0 |

### Sampling Rate
- **Per task commit:** the matching hermetic facts class (`dotnet test ... --filter-class "*<Facts>"`).
- **Per wave merge:** full `tests/BaseApi.Tests` run.
- **Phase gate:** `phase-32-close.ps1` 3×GREEN + triple-SHA BEFORE==AFTER (rebuild processor/orchestrator/baseapi containers first per MEMORY — embedded SourceHash must match; E2E teardown must POST `/orchestration/stop` + scan-clean the NEW `skp:cancelled:*` namespace so the triple-SHA holds — note the no-TTL marker means the close-gate net-zero teardown MUST explicitly delete `skp:cancelled:*` keys, they will NOT self-expire).

### Wave 0 Gaps
- [ ] `RetryAttemptNumberingFacts` — pins `GetRetryAttempt()` boundary value for `Immediate(Limit)` at endpoint level (UNBLOCKS the breaker seam; Risk R1).
- [ ] `FaultConsumerBindingFacts` — proves `Fault<EntryStepDispatch>.Message.WorkflowId` round-trips through an in-memory MT harness (Risk R2).
- [ ] `BreakerTriggerFacts`, `CancelledMarkerFacts`, `CheckAndDropFacts`, `FaultUnscheduleFacts`, `StepEntryConditionEnumFacts`, `BreakerMetricsFacts`, `FaultIdempotencyFacts`.
- [ ] Close-gate teardown extension: scan-clean `skp:cancelled:*` (no-TTL keys won't self-expire — without this the triple-SHA drifts every gate run, exactly the failure mode in MEMORY `reference_close_gate_container_rebuild_and_flag_churn`).
- [ ] No new framework install — xUnit + the real-stack harness already cover this surface.

## Environment Availability

| Dependency | Required by | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| MassTransit / MassTransit.RabbitMQ | Fault fanout, retry | ✓ (pinned) | 8.5.5 (last Apache-2.0 line; v9+ commercial) | — (do NOT bump to v9) |
| RabbitMQ | bus / Fault publish / `_error` | ✓ (compose) | per compose | — |
| Redis (StackExchange.Redis) | cancelled marker + check-and-drop | ✓ | per compose | — |
| Quartz | unschedule reuse | ✓ (in-proc RAMJobStore) | per `Program.cs` | — |
| PostgreSQL | "no live step has `EntryCondition==3`" data assertion (req-6) | ✓ (compose) | per compose | run the assertion as a hermetic seed-and-query test against a throwaway DB if prod DB not reachable in CI |

**Data-assertion note (req-6):** "no live step has `EntryCondition == 3`" is a one-time data check. Provide it as a SQL/EF query (`SELECT COUNT(*) FROM "Steps" WHERE "EntryCondition" = 3` → expect 0) the plan runs against the target DB, plus a unit test proving validation now rejects 3 going forward. D-12 expects this to be clean (dual-pipeline steps use `Always`/4).

## Risks (locked design vs MassTransit reality)

### Risk R1 — `GetRetryAttempt()` reliability & numbering (HIGH attention, LOW-MEDIUM likelihood of surprise)
**The design depends on** the SPEC-locked `GetRetryAttempt() == Limit`. **MassTransit reality:** `GetRetryAttempt()` returns 0 under *consumer-level* retry config and under *stacked* policies `[CITED: MassTransit#1217, #3216]`. **Why the project is likely fine:** this codebase configures a SINGLE `Immediate(Limit)` policy at the RECEIVE-ENDPOINT level (`ProcessorStartupOrchestrator.cs:154` via `ConnectReceiveEndpoint`, and `ResultConsumerDefinition.cs:41`) — the documented working case. **Action:** Wave-0 `RetryAttemptNumberingFacts` MUST pin the exact value before the breaker seam is built. **Fallback if it misbehaves:** read the redelivery/retry count from the message header MassTransit sets (`MT-Redelivery-Count` / the retry header is set when faults are produced `[CITED: MassTransit search]`) or count attempts via a small consumer-scoped counter keyed off `ctx.MessageId` in Redis. Do NOT silently change the `== Limit` semantics — escalate to discuss-phase if the boundary value differs from `Limit`.

### Risk R2 — Fault routing scope (LOW)
A PUBLISHED `Fault<EntryStepDispatch>` fans out to every queue with an `IConsumer<Fault<EntryStepDispatch>>` bound. Desired: every orchestrator replica's temp queue. **Confirm at plan-time:** no other consumer subscribes `Fault<EntryStepDispatch>` (grep today: none). If a future consumer subscribes it, the fanout still works but adds an unintended receiver. **Action:** `FaultConsumerBindingFacts` proves the binding + WorkflowId extraction in an in-memory harness.

### Risk R3 — `_total` suffix convention (LOW)
The SPEC names counters WITH `_total`; the Phase-30 convention names the C# instrument WITHOUT it and lets the collector's `add_metric_suffixes` append `_total`. **Action:** name the instruments without `_total` (consistent with `processor_dispatch_consumed` etc.) and confirm the collector still appends the suffix. A mismatch produces `*_total_total` or a missing-`_total` metric name. Phase-30 already relies on this; should be intact.

### Risk R4 — Marker-write-fault edge (ACCEPTED, not a blocker)
If the marker `SET` itself faults (Redis down) the in-flight marker isn't set; this lands in the same bus/redis-down `_error` backstop the design already accepts (CONTEXT §code_context). No mitigation required — document the edge in the plan.

## Code Examples (verified analogs in this repo)

- **In-consumer infra-throw + effect-first ordering:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:68-70, 199-217` (uncaught infra ops; effect-first `Pending→Ack` flip after the effect).
- **Per-replica fan-out endpoint registration:** `src/Orchestrator/Program.cs:40-43` + `StopOrchestrationConsumerDefinition.cs:22` (shared `EndpointName="orchestrator"`).
- **Idempotent keep-L1 unschedule:** `src/Orchestrator/Hydration/WorkflowLifecycle.cs:142-158` (`UnscheduleOnlyAsync`).
- **Typed consume filter (the Unknown-1 fallback analog):** `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` (an `IFilter<ConsumeContext<T>>` wrapping `next.Send`).
- **Filter bus-wide registration:** `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:51-52` (`c.UseConsumeFilter(typeof(...<>), ctx)`).
- **Meter + counter construction + tagged increment:** `src/.../ProcessorMetrics.cs`, `OrchestratorMetrics.cs`, increment at `EntryStepDispatchConsumer.cs:62-63`.
- **L2 key builder convention:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:34-51`.
- **`IOptions<RetryOptions>` single source:** `src/Messaging.Contracts/Configuration/RetryOptions.cs`, consumed at `ResultConsumerDefinition.cs:26` + `ProcessorStartupOrchestrator.cs:151`.

## Assumptions Log

| # | Claim | Section | Risk if wrong |
|---|-------|---------|---------------|
| A1 | `GetRetryAttempt()` increments reliably for a SINGLE `Immediate(Limit)` endpoint-level policy (this project's config), unlike the consumer-level/stacked-policy bug reports | Unknown 1 / Risk R1 | Breaker never trips or trips early — fallback to redelivery header / Redis attempt counter. Pinned by Wave-0 test before relying on it. |
| A2 | The SPEC-locked `== Limit` is the correct boundary (0-based: first delivery=0, Limit-th retry=Limit) for `Immediate(Limit)` | Unknown 1 | Off-by-one in trip timing — Wave-0 `RetryAttemptNumberingFacts` resolves; if it differs, escalate (SPEC value is locked, don't silently change). |
| A3 | Anything reaching the outer breaker catch in `EntryStepDispatchConsumer` is INFRA by construction (business throws already caught at `:123`/`:129`) | Unknown 1 / Pitfall 2 | A leaked business throw could trip the breaker — covered by a unit test (ProcessAsync throw → Failed + no marker). |
| A4 | No other consumer in the system subscribes `Fault<EntryStepDispatch>` | Unknown 2 / Risk R2 | Unintended extra fanout receiver — grep confirms none today; `FaultConsumerBindingFacts` proves binding. |
| A5 | The OTel collector's `add_metric_suffixes` is on, so instrument names without `_total` export as `*_total` | Unknown 4 / Risk R3 | Metric name mismatch — Phase-30 already depends on this; confirm collector config unchanged. |

## Sources

### Primary (HIGH confidence)
- Codebase (read in full): `EntryStepDispatchConsumer.cs`, `ResultConsumer.cs`, `ResultConsumerDefinition.cs`, `Start/StopOrchestrationConsumerDefinition.cs`, `StopOrchestrationConsumer.cs`, `WorkflowLifecycle.cs`, `WorkflowScheduler.cs`, `Program.cs`, `L2ProjectionKeys.cs`, `StepEntryCondition.cs`, `StepOutcome.cs`, `StepAdvancement.cs`, `StepDtoValidator.cs`, `IWorkflowL1Store.cs`, `RetryOptions.cs`, `EntryStepDispatch.cs`, `ProcessorMetrics.cs`, `OrchestratorMetrics.cs`, `BaseProcessorServiceCollectionExtensions.cs`, `ProcessorStartupOrchestrator.cs`, `InboundExecutionScopeConsumeFilter.cs`, `MessagingServiceCollectionExtensions.cs`.
- `Directory.Packages.props` — MassTransit pinned at **8.5.5** (last Apache-2.0; v9+ commercial).
- MassTransit Exceptions docs — `Fault<T>` interface shape (`FaultId/FaultedMessageId/Timestamp/Exceptions[]/Host/Message`); retry exhaustion produces BOTH `Fault<T>` AND `_error` move. https://masstransit.io/documentation/concepts/exceptions

### Secondary (MEDIUM confidence)
- MassTransit#1217 — `GetRetryAttempt()` returns 0 at consumer-level config; consumer body DOES re-execute each retry. https://github.com/MassTransit/MassTransit/issues/1217
- MassTransit#3216 — `GetRetryAttempt()` returns 0 with stacked/multiple retry policies. https://github.com/MassTransit/MassTransit/issues/3216
- MassTransit retry middleware docs — `Immediate(n)` = retry immediately up to the limit; `UseMessageRetry` best at receive-endpoint level. https://masstransit.io/documentation/configuration/middleware/retry
- MassTransit#1510 / IRetryObserver discussion — `PostFault`/`RetryComplete` exist but fire outside consumer scope (rejected seam). https://github.com/MassTransit/MassTransit/issues/1510

## Metadata

**Confidence breakdown:**
- Standard stack / placement seams: HIGH — every new file maps to an existing analog read in full.
- Fault<T> binding + dual-mechanism (D-03/D-06): HIGH — confirmed against official docs.
- Retry-attempt numbering (req-1 boundary): MEDIUM — works for this project's single-endpoint config, but the exact value MUST be pinned by a Wave-0 hermetic test (Risk R1/A1/A2).
- Enum blast radius: HIGH — grep-confirmed 2 source touch-points.
- Metrics `_total` convention: MEDIUM — depends on unchanged collector config (Risk R3).

**Research date:** 2026-06-04
**Valid until:** 2026-07-04 (MassTransit 8.5.5 pinned; codebase fast-moving — re-grep enum/seam line numbers at plan-time if other phases land first)

## RESEARCH COMPLETE
