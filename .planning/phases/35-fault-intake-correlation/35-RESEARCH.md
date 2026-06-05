# Phase 35: Fault Intake & Correlation - Research

**Researched:** 2026-06-05
**Domain:** MassTransit `Fault<T>` pub/sub intake + manual execution log-scope correlation (.NET 8 distributed workflow engine, SK_P)
**Confidence:** HIGH (every claim grounded in the actual codebase at file:line; the load-bearing `Fault<T>` mechanics were already proven LIVE by the Phase-33 spike)

## Summary

Phase 35 replaces Phase 34's throwaway `PlaceholderConsumer`/`PlaceholderConsumerDefinition`/`KeeperPlaceholder` with the two real production fault consumers — `IConsumer<Fault<EntryStepDispatch>>` and `IConsumer<Fault<ExecutionResult>>` — both colocated on the single stable durable competing-consumer queue `keeper-fault-recovery` (`KeeperQueues.FaultRecovery`, `src/Messaging.Contracts/KeeperQueues.cs:15`). Each consumer is an "observe-and-ack" body: double-unwrap `context.Message.Message` to the inner `IExecutionCorrelated`, build the execution log-scope manually (the bus-wide auto scope filter does NOT fire on `Fault<T>`), emit one structured Information log, and return (ack). No recovery work, no metrics, no DLQ-topology — those are Phases 36/38.

The most important correctness finding (CORRECTS an over-confident reading of CONTEXT.md D-08): `Fault<EntryStepDispatch>` and `Fault<ExecutionResult>` are NOT `ICorrelated` and NOT `IExecutionCorrelated` — `Fault<T>` is a MassTransit framework envelope, and the inner message is reached via `context.Message.Message`. Therefore **neither** bus-wide inbound filter extracts the propagated ids: `InboundExecutionScopeConsumeFilter` short-circuits at its `is not IExecutionCorrelated` branch (`src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs:22-26`), AND `InboundCorrelationConsumeFilter` reads `context.Message as ICorrelated` which is null for a `Fault<T>` envelope, so it falls back to `context.CorrelationId` or a *fresh* Guid (`src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs:35-37`). The Keeper consumer body MUST therefore add **CorrelationId too** to its manual scope from the unwrapped inner message — not just the 5 execution ids. This is the key correctness point the planner must encode.

**Primary recommendation:** Refactor the scope-dict builder into a shared `ExecutionLogScope.BuildState(IExecutionCorrelated)` helper in `Messaging.Contracts` (reachable by Keeper with no new reference), call it from both the filter and the two new Keeper consumers, and in the Keeper consumers wrap the scope in an OUTER `BeginScope([CorrelationKeys.LogScope] = inner.CorrelationId)` because the outer correlation filter does NOT recover the inner correlationId for a `Fault<T>`. Use the single explicit `cfg.ReceiveEndpoint(KeeperQueues.FaultRecovery, e => { e.ConfigureConsumer<A>; e.ConfigureConsumer<B>; })` colocation form is NOT needed — two `ConsumerDefinition`s with the same `EndpointName` colocate cleanly under the existing `AddConsumer<,>()` + `ConfigureEndpoints` flow (proven by the existing `ResultConsumerDefinition`/`PlaceholderConsumerDefinition` precedent). See D-03 section for the recommended sketch and the gotcha.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Consume `Fault<T>` pub/sub events | Keeper console (`src/Keeper`) | — | Keeper is the dedicated fault-recovery console; D-03 colocates both consumers on its one queue |
| Unwrap inner message + extract 6-id tuple + H | Keeper consumer body | Messaging.Contracts (the `IExecutionCorrelated` contract) | The contract lives in the leaf assembly; the read is `context.Message.Message` |
| Open execution log-scope from inner message | Keeper consumer body (manual) | BaseConsole.Core / Messaging.Contracts (shared helper) | The bus-wide filter passes through `Fault<T>` — manual scope required (D-05) |
| Scope-dict build (Guid.Empty/empty-string skips, key set) | Messaging.Contracts (`ExecutionLogScope.BuildState`) | BaseConsole.Core filter (caller) | Single source of truth — both filter and Keeper call it (D-07) |
| Correlation-id scope for `Fault<T>` | Keeper consumer body (manual outer scope) | — | Outer correlation filter canNOT extract inner corrId from a `Fault<T>` envelope (NEW finding) |
| `{queue}_error` dead-letter on retry exhaustion | MassTransit default transport | — | Phase 35 CONFIRMS the separation; the consolidated TTL'd DLQ-1 is BUILT in Phase 36 |
| End-to-end correlated-ES-log proof | RealStack test (`tests/BaseApi.Tests`) | running Keeper container (compose `keeper:` tier) | D-09 asserts the CONTAINER emits the log, not an in-test bus |

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Phase 35 builds **NO DLQ-1 / TTL / shared-error-transport topology.** That construction is DLQ-04 → Phase 36. Today faults land in per-consumer `{queue}_error` queues by MassTransit default (confirmed: no centralized DLQ-1, no `x-message-ttl`, no `x-dead-letter-exchange` in the current codebase).
- **D-02:** Phase 35 satisfies INTAKE-03's Phase-35 slice by *confirming/observing* (a standing RealStack assertion) the separation property: Keeper recovers strictly off the `Fault<T>` pub/sub stream, never reads the `_error`/DLQ-1 queue, recovered work is never double-processed. The full "consolidates into TTL'd forensic DLQ-1" property completes in Phase 36.
- **D-03:** Both `Fault<EntryStepDispatch>` and `Fault<ExecutionResult>` consumers colocate on the single stable competing-consumer queue `keeper-fault-recovery` (`KeeperQueues.FaultRecovery`). Replace the `PlaceholderConsumer` + `PlaceholderConsumerDefinition` + `KeeperPlaceholder` message wholesale.
- **D-04:** Keep `KeeperQueues.FaultRecovery` name (`"keeper-fault-recovery"`). Both consumers inherit the shared `UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))` pattern from the `"Retry"` section.
- **D-05:** `Fault<T>` is NOT `IExecutionCorrelated` — the bus-wide `InboundExecutionScopeConsumeFilter<T>` `is not IExecutionCorrelated` branch passes through WITHOUT opening a scope. Keeper opens the execution log-scope MANUALLY from `context.Message.Message`.
- **D-06:** Phase-35 consumer body (per fault type): unwrap `context.Message.Message` → read the 6-id tuple + `H` → `logger.BeginScope(...)` → emit one structured log line → ack. NO recovery work in Phase 35. "Observe-and-ack" is the deliberate shape.
- **D-07:** Refactor the scope-dictionary builder out of `InboundExecutionScopeConsumeFilter` into a small shared helper (e.g. `ExecutionLogScope.BuildState(IExecutionCorrelated)`) called from BOTH the filter and the Keeper consumers. Single source of truth for the scope-key set.
- **D-08:** `Information`-level structured "keeper fault intake" log, carrying correlationId + the 5 execution-scope ids + the fault type + `Fault<T>.Exceptions[0]` exception message/summary. Match the other consoles' log conventions.
- **D-09:** Prove SC3 against the RUNNING Keeper container (NOT an in-test bus). Extend the standing Phase-33 `FaultRecoverySpikeE2ETests` (or add a sibling RealStack test) to live-trip a `Fault<T>`, then assert via `PollEsForLog` that the Keeper container emitted an ES log carrying the propagated correlationId + execution-scope ids.

### Claude's Discretion
- MassTransit endpoint colocation mechanism (D-03): same `EndpointName` on two `ConsumerDefinition`s vs one explicit `cfg.ReceiveEndpoint(... ConfigureConsumer<A>; ConfigureConsumer<B>)`. Either yields the one-queue/two-type-binding worklist; planner's call.
- Shared-helper-vs-inline for the scope builder (D-07) — refactor recommended; inline acceptable if it touches too much of the filter hot path, but the scope-key set MUST stay identical.
- Log event wording + which `Fault<T>.Exceptions` fields to surface + level nuance (Information vs Warning) (D-08), within "consistent with other consoles."
- Test vehicle: extend spike vs new Keeper-specific RealStack test; settle-window durations; exact `PollEsForLog` query shape (D-09).

### Deferred Ideas (OUT OF SCOPE)
- L2 health-probe recovery loop + re-inject-to-origin-by-type (INTAKE-04) + `keeper-dlq` (DLQ-2) + the two-DLQ split + the shared error-transport that BUILDS the consolidated TTL'd DLQ-1 (DLQ-01..04) — **Phase 36**.
- `PauseWorkflow`/`ResumeWorkflow` contracts + orchestrator pending-recovery coordination — **Phase 37** (PAUSE-01..05).
- Keeper meter + `keeper_fault_consumed` / `keeper_l2_probe_failed` / DLQ-depth counters/histograms + real-stack close gate — **Phase 38** (KMET-01..03, TEST-01..03).
- Phase 35 is **logs-only** (KMET-04) — NO metrics/instruments added here.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| INTAKE-03 | `_error`/DLQ-1 transport-exhaustion record consolidates into a TTL'd forensic DLQ-1, never Keeper's worklist; Keeper recovers off the `Fault<T>` events, never re-processed from the error/DLQ-1 queue. (`.planning/REQUIREMENTS.md:26`) | Phase-35 SLICE only (D-02): assert the separation topology — Keeper binds the `Fault<T>` message-type exchanges, NOT any `{queue}_error` queue. No DLQ-1/TTL exists in the codebase today (verified — see "_error / DLQ-1" section). The consolidation BUILD is Phase 36. |
| KMET-04 | Keeper emits OTel logs consistent with the other consoles, carrying the correlationId + execution-scope ids propagated from the faulted message. (`.planning/REQUIREMENTS.md:58`) | The manual scope build (D-05/D-06/D-07) + the manual outer CorrelationId scope (NEW finding) — the two consumer bodies emit one Information log inside the combined scope, mirroring `EntryStepDispatchConsumer`/`ResultConsumer` conventions. |
</phase_requirements>

---

## Standard Stack

No new packages. Everything is already referenced. Verified versions are pinned centrally in `Directory.Packages.props` (CPM — no version attrs on `PackageReference`); Keeper already references `MassTransit` + `MassTransit.RabbitMQ` (`src/Keeper/Keeper.csproj:35-36`).

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.x (per `InboundCorrelationConsumeFilter` doc-comment "MassTransit 8.5.5", `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs:20`) | `Fault<T>` pub/sub + consumer endpoints | Already the bus across all consoles |
| MassTransit.RabbitMQ | (same) | RabbitMQ transport | Already wired in `AddBaseConsoleMessaging` |
| Microsoft.Extensions.Logging | net8 | `BeginScope` execution-id scopes → OTel logs | Already the log surface (MEL bridge, IncludeScopes) |

### Supporting (already present, reused verbatim)
| Asset | Location | Purpose |
|-------|----------|---------|
| `KeeperQueues.FaultRecovery` | `src/Messaging.Contracts/KeeperQueues.cs:15` | The stable durable queue name (kept across the placeholder swap) |
| `RetryOptions` | `src/Messaging.Contracts/Configuration/RetryOptions.cs` (bound in `src/Keeper/Program.cs:22` from `"Retry"` section) | `Immediate(N)` budget both fault consumers bind |
| `ExecutionLogScope` | `src/Messaging.Contracts/ExecutionLogScope.cs` | scope-key constants + (after D-07) the `BuildState` helper home |
| `CorrelationKeys.LogScope` | `src/Messaging.Contracts/CorrelationKeys.cs:7` (`"CorrelationId"`) | the correlation scope key the Keeper body must set manually |
| `ResultConsumerDefinition` / `PlaceholderConsumerDefinition` | `src/Orchestrator/Consumers/ResultConsumerDefinition.cs`, `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs` | drop-in template for the two new `Fault<T>` definitions |

**Installation:** None. `dotnet build` only.

---

## Architecture Patterns

### System Architecture Diagram (Phase-35 fault-intake data flow)

```
 Producer side (Processor / Orchestrator) — UNCHANGED in Phase 35
 ┌──────────────────────────────────────────────────────────────────┐
 │ EntryStepDispatchConsumer / ResultConsumer hits an INFRA fault    │
 │ (e.g. Redis WRONGTYPE on the flag[H] GET) → UseMessageRetry        │
 │ Immediate(N) exhausts → MassTransit AUTO-PUBLISHES Fault<T>        │
 └───────────────┬───────────────────────────┬──────────────────────┘
                 │ Fault<EntryStepDispatch>   │ Fault<ExecutionResult>
                 ▼ (message-type EXCHANGE,    ▼
                    pub/sub fan-out)
   RabbitMQ:  exchange "Messaging.Contracts:Fault--EntryStepDispatch"
              exchange "Messaging.Contracts:Fault--ExecutionResult"
                 │                            │
                 │  (BOTH bound to the ONE queue keeper-fault-recovery)
                 ▼                            ▼
 ┌──────────────────────────────────────────────────────────────────┐
 │ Keeper container (replicas:2, competing-consumer round-robin)     │
 │ receive endpoint: keeper-fault-recovery                            │
 │   bus-wide filters run on Fault<T>:                                │
 │     InboundCorrelationConsumeFilter → Message as ICorrelated == null│
 │        → falls back to envelope/FRESH guid (WRONG corrId!)         │
 │     InboundExecutionScopeConsumeFilter → not IExecutionCorrelated  │
 │        → pass-through, NO execution scope                          │
 │                                                                    │
 │   FaultEntryStepDispatchConsumer / FaultExecutionResultConsumer:   │
 │     var inner = context.Message.Message;  // double .Message       │
 │     using BeginScope([CorrelationId] = inner.CorrelationId)  ◄─ manual, corrects the filter
 │       using BeginScope(ExecutionLogScope.BuildState(inner)) ◄─ manual exec ids (D-07 helper)
 │         logger.LogInformation("keeper fault intake ...", ...);     │
 │     return;  // ACK — observe-and-ack (D-06), NO recovery in P35   │
 └──────────────────────────────┬───────────────────────────────────┘
                                ▼  OTLP (OTEL_EXPORTER_OTLP_ENDPOINT)
                        otel-collector → Elasticsearch
                  (log: service.name=keeper, attributes.CorrelationId,
                   attributes.WorkflowId/StepId/ProcessorId/ExecutionId/EntryId)
                                ▲
                  SC3: RealStack test PollEsForLog asserts this log exists & is correlated

 SEPARATE forensic path (Phase 35 CONFIRMS, does NOT build):
   retry exhaustion ALSO leaves the failed delivery in the per-consumer
   {origin-queue}_error dead-letter queue (MassTransit default) — Keeper
   NEVER binds/reads it. (Consolidated TTL'd DLQ-1 = Phase 36.)
```

### Recommended file structure (Keeper)
```
src/Keeper/
├── Program.cs                                  # swap the placeholder AddConsumer for the two real ones
└── Consumers/
    ├── FaultEntryStepDispatchConsumer.cs       # NEW — IConsumer<Fault<EntryStepDispatch>>
    ├── FaultExecutionResultConsumer.cs         # NEW — IConsumer<Fault<ExecutionResult>>
    ├── FaultRecoveryConsumerDefinition.cs       # NEW — see D-03 sketch (one OR two definitions)
    ├── PlaceholderConsumer.cs                   # DELETE (D-03)
    ├── PlaceholderConsumerDefinition.cs         # DELETE (D-03)
    └── KeeperPlaceholder.cs                     # DELETE (D-03)
```

### Pattern 1 (D-03): Two consumer types on ONE shared receive endpoint

**RECOMMENDED — two `ConsumerDefinition`s, same `EndpointName`, plain `AddConsumer<,>()`.** This is the idiomatic SK_P pattern already in production (`ResultConsumerDefinition` / `PlaceholderConsumerDefinition`), and it composes with the existing `AddBaseConsoleMessaging(...) → ConfigureEndpoints(ctx)` flow at `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:59` **without changing the base library**.

```csharp
// src/Keeper/Consumers/FaultRecoveryConsumerDefinition.cs  (option: ONE generic-ish base, or TWO siblings)
// Mirrors PlaceholderConsumerDefinition.cs:15-39 verbatim except the consumer type.
public sealed class FaultEntryStepDispatchConsumerDefinition
    : ConsumerDefinition<FaultEntryStepDispatchConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;
    public FaultEntryStepDispatchConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = KeeperQueues.FaultRecovery;   // "keeper-fault-recovery" — SAME on both
    }
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<FaultEntryStepDispatchConsumer> consumerConfigurator,
        IRegistrationContext context)
        => endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));
}
// + an identical FaultExecutionResultConsumerDefinition with EndpointName = KeeperQueues.FaultRecovery.

// Program.cs (replaces line 27-28):
builder.Services.AddBaseConsoleMessaging(builder.Configuration, x =>
{
    x.AddConsumer<FaultEntryStepDispatchConsumer, FaultEntryStepDispatchConsumerDefinition>();
    x.AddConsumer<FaultExecutionResultConsumer,   FaultExecutionResultConsumerDefinition>();
});
```

**GOTCHA #1 — `UseMessageRetry` is PER-ENDPOINT, not per-consumer.** `endpointConfigurator.UseMessageRetry(...)` configures the *receive endpoint* pipeline. When two consumers share one endpoint, the retry middleware is applied once to that endpoint and wraps BOTH consumers. If both definitions call `endpointConfigurator.UseMessageRetry(...)` with the SAME `Immediate(Limit)`, MassTransit applies it consistently — but to keep it unambiguous and avoid double-registering retry middleware on the same endpoint, **prefer setting `UseMessageRetry` on `endpointConfigurator` from only ONE definition, OR (cleaner) collapse both definitions into a single explicit `ReceiveEndpoint` config (alternative below).** The Limit is identical on both paths (the same `"Retry"` section), so behavior is safe either way; the planner should pick one to own the endpoint-level retry call to keep intent clear. Document this in the task.

**GOTCHA #2 — same `EndpointName` DOES colocate; `ConfigureEndpoints` does NOT split them.** MassTransit's `ConfigureEndpoints` groups consumers by their definition's `EndpointName`; two definitions naming `keeper-fault-recovery` produce ONE receive endpoint with two consumer bindings (two message-type exchange bindings into the one queue). This is exactly what the spike proved at the topology level (two `Fault<T>` exchanges, one logical worklist). The durable single queue + net-zero close-gate SHA (KEEP-02) is preserved because the queue name const is unchanged (`KeeperQueues.FaultRecovery`).

**ALTERNATIVE (also valid, D-03 discretion) — one explicit `ReceiveEndpoint` via the `configureBus` seam.** `AddBaseConsoleMessaging` exposes an optional `configureBus` callback that runs BEFORE `ConfigureEndpoints` (`MessagingServiceCollectionExtensions.cs:37,55-58`). You could `cfg.ReceiveEndpoint(KeeperQueues.FaultRecovery, e => { e.UseMessageRetry(...); e.ConfigureConsumer<FaultEntryStepDispatchConsumer>(ctx); e.ConfigureConsumer<FaultExecutionResultConsumer>(ctx); })` there. This makes the single-retry-call ownership explicit and reads as "one queue, two consumers" in one place. Tradeoff: it bypasses the `ConsumerDefinition.EndpointName` convention the rest of the codebase uses, and you still `AddConsumer<...>()` without a definition. **Recommendation: use the two-definitions form (Pattern 1) for codebase consistency; if GOTCHA #1's double-retry concern bothers the planner, use this explicit form instead.** Either yields the identical one-queue/two-binding topology.

### Pattern 2 (D-05/D-06/D-07): the consumer body

Each Keeper fault consumer mirrors the read+scope+log shape of `ResultConsumer.Consume` (`src/Orchestrator/Consumers/ResultConsumer.cs:48-50`) and `EntryStepDispatchConsumer`'s nested `BeginScope` (`src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:170-188`), but with the double-unwrap and the NEW manual CorrelationId scope:

```csharp
// src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs
public sealed class FaultEntryStepDispatchConsumer(ILogger<FaultEntryStepDispatchConsumer> logger)
    : IConsumer<Fault<EntryStepDispatch>>
{
    public Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context)
    {
        var inner = context.Message.Message;            // double .Message — the VERBATIM IExecutionCorrelated instance
        var ex    = context.Message.Exceptions is { Length: > 0 } exs ? exs[0] : null;  // ExceptionInfo (nullable-safe)

        // NEW (correctness): the OUTER InboundCorrelationConsumeFilter could NOT recover the inner
        // correlationId from the Fault<T> envelope (Message as ICorrelated == null), so set it here.
        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = inner.CorrelationId.ToString() }))
        using (logger.BeginScope(ExecutionLogScope.BuildState(inner)))   // D-07 shared helper — the 5 exec ids w/ skips
        {
            logger.LogInformation(
                "Keeper fault intake: {FaultType} for H={H} — {ExceptionType}: {ExceptionMessage}",
                nameof(EntryStepDispatch), inner.H, ex?.ExceptionType, ex?.Message);
        }
        return Task.CompletedTask;   // observe-and-ack (D-06) — NO recovery in Phase 35
    }
}
// FaultExecutionResultConsumer is identical with inner type ExecutionResult.
```

**Why the manual CorrelationId scope is REQUIRED (key correctness point):** `InboundCorrelationConsumeFilter.Send` reads `context.Message as ICorrelated` (`InboundCorrelationConsumeFilter.cs:35`). For a `Fault<EntryStepDispatch>` consumer, `context.Message` is the `Fault<EntryStepDispatch>` *envelope*, which is NOT `ICorrelated` (only the *inner* `EntryStepDispatch` is, `src/Messaging.Contracts/EntryStepDispatch.cs:12`). So the filter falls back to `context.CorrelationId` (the MassTransit envelope id — NOT the propagated business correlationId) or a fresh `Guid.NewGuid()`. Without the manual inner scope, the Keeper log would carry the WRONG `attributes.CorrelationId` and SC3's "correlated to the original execution by correlationId" would FAIL. CONTEXT.md D-08 says the correlation filter "DOES fire" — it does *run*, but it does NOT extract the right id; the body must add it. **This is the single most important detail for the planner to encode and for the plan-checker to verify.**

### Pattern 3 (D-07): the shared scope-builder refactor (byte-identical behavior)

Extract the dict-builder from `InboundExecutionScopeConsumeFilter.Send` (`InboundExecutionScopeConsumeFilter.cs:28-33`) into a static helper in `Messaging.Contracts/ExecutionLogScope.cs` so both the filter and Keeper share it:

```csharp
// ADD to src/Messaging.Contracts/ExecutionLogScope.cs (pure POCO, no MassTransit ref — Keeper-reachable)
public static Dictionary<string, object> BuildState(IExecutionCorrelated ec)
{
    var state = new Dictionary<string, object>();
    if (ec.WorkflowId  != Guid.Empty) state[WorkflowId]  = ec.WorkflowId.ToString();
    if (ec.StepId      != Guid.Empty) state[StepId]      = ec.StepId.ToString();
    if (ec.ProcessorId != Guid.Empty) state[ProcessorId] = ec.ProcessorId.ToString();
    if (ec.ExecutionId != Guid.Empty) state[ExecutionId] = ec.ExecutionId.ToString();
    if (!string.IsNullOrEmpty(ec.EntryId)) state[EntryId] = ec.EntryId;
    return state;
}
```

Then the filter becomes (byte-identical observable behavior):
```csharp
// InboundExecutionScopeConsumeFilter.Send body (after the is-not-IExecutionCorrelated pass-through):
using (logger.BeginScope(ExecutionLogScope.BuildState(ec)))
    await next.Send(context);
```

**Signature/return:** `static Dictionary<string,object> BuildState(IExecutionCorrelated ec)`. The skip rules MUST stay exactly: `!= Guid.Empty` for the four Guids, `!string.IsNullOrEmpty` for the string `EntryId`, EntryId stored verbatim (no `.ToString()`). The key set is exactly `{WorkflowId, StepId, ProcessorId, ExecutionId, EntryId}` — NO CorrelationId (that is `CorrelationKeys.LogScope`, owned separately). This is enforced by the existing tests (see Regression Guards).

**Home choice — `Messaging.Contracts/ExecutionLogScope.cs` (CONFIRMED reachable from Keeper).** `ExecutionLogScope` is a pure POCO leaf in `Messaging.Contracts` (no MassTransit ref — `ExecutionLogScope.cs:8`), and Keeper references `Messaging.Contracts` directly (`src/Keeper/Keeper.csproj:50`). So the helper is reachable from Keeper with NO new reference. Putting it in `BaseConsole.Core` would ALSO work (Keeper references it too, `Keeper.csproj:49`), but `Messaging.Contracts` is the better home: it keeps the helper next to its key constants and `IExecutionCorrelated`, and avoids a MassTransit/logging dependency on the contracts leaf (the helper only needs `IExecutionCorrelated` + `Dictionary`, both BCL/leaf). **Recommendation: `Messaging.Contracts/ExecutionLogScope.cs`.** The filter in `BaseConsole.Core` already references `Messaging.Contracts` (`InboundExecutionScopeConsumeFilter.cs:2`).

---

## The `_error` / DLQ-1 confirmation (INTAKE-03, SC1) — what to ASSERT vs not BUILD

**How `Fault<EntryStepDispatch>` is produced (verified, proven live):** The producer-side consumers (`EntryStepDispatchConsumer`, `ResultConsumer`) wrap genuine infra faults in `UseMessageRetry(Immediate(N))` (`ResultConsumerDefinition.cs:41`, `EntryStepDispatchConsumer` runtime bind). When the bounded immediate retry exhausts, MassTransit AUTO-PUBLISHES a `Fault<T>` to the message-type fault exchange (proven live in Phase 33: a Redis WRONGTYPE on the `flag[H]` GET exhausts `Immediate(N)` → `Fault<EntryStepDispatch>` fans out — `33-02-SUMMARY.md:111`, `FaultRecoverySpikeE2ETests.cs:127-193`).

**How Keeper receives it:** `IConsumer<Fault<EntryStepDispatch>>` bound to `keeper-fault-recovery` binds the `Fault<EntryStepDispatch>` message-type EXCHANGE (pub/sub fan-out), NOT any per-`{procId}_error` queue. The spike proved this is a type-scoped pub/sub bind: `Fault<StartOrchestration>`/`Fault<StopOrchestration>` are demonstrably NOT delivered to the `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` consumers (`FaultRecoverySpikeE2ETests.cs:246-271` — the negative proof, ZERO captures over an 8s window).

**The "consolidates into TTL'd forensic DLQ-1" property (Phase 35 CONFIRMS, Phase 36 BUILDS):**
- **Verified: there is NO centralized DLQ-1 / TTL in the codebase today.** No `x-message-ttl`, no `x-dead-letter-exchange`, no consolidated error transport. Faults land in the per-consumer `{queue}_error` dead-letter queue (MassTransit default) — recorded in Phase 33 D-10 (`33-02-SUMMARY.md:34,83`) and CONTEXT.md D-01. The grep audit found no DLQ-1 topology.
- **In-scope to ASSERT (the Phase-35 INTAKE-03 slice, D-02):** the SEPARATION property — Keeper recovers strictly off the `Fault<T>` pub/sub stream and NEVER binds/reads any `{queue}_error` queue, and a recovered (re-injected, in Phase 36) message is never double-processed because it rides the receiver's `flag[H]` dedup gate (proven live in Phase 33, `FaultRecoverySpikeE2ETests.cs:206-231`, `CountEsHitsAsync == 1`).
- **Out-of-scope to BUILD:** the consolidated TTL'd DLQ-1 transport, the `x-message-ttl`/dead-letter-exchange topology, DLQ-2 `keeper-dlq` — all Phase 36 (DLQ-01..04).

**What a test can assert TODAY about the separation:**
1. **Topology assertion:** Keeper's receive endpoint `keeper-fault-recovery` is bound to the `Fault<EntryStepDispatch>` and `Fault<ExecutionResult>` message-type exchanges (RabbitMQ binding introspection), and is NOT bound to any `*_error` queue. The simplest robust form is the spike's already-proven NEGATIVE/positive bind facts: positive — a `Fault<T>` reaches the Keeper consumer; negative — non-execution faults do NOT.
2. **No-double-process:** the existing `flag[H]` collapse assertion (`CountEsHitsAsync == 1`) proves a re-delivered identity produces exactly one downstream effect — but note: in Phase 35 the Keeper consumer is observe-and-ack (no re-inject), so the double-process guarantee is about Keeper NOT re-reading `_error`. The strongest Phase-35-specific assertion is: Keeper's intake log fires off the `Fault<T>` event (SC3), and the `_error` queue is untouched by Keeper (it binds only the fault exchanges). The full re-inject/collapse no-double-process is proven by the standing spike (dispatch hop) and completed in Phase 36.

---

## The two consumer bodies — `Fault<T>.Exceptions` / `ExceptionInfo` shape (D-06, D-08)

MassTransit's `Fault<T>` envelope carries (relevant fields):
- `Fault<T>.Message` — the inner original message instance (here `EntryStepDispatch` / `ExecutionResult`), reached via `context.Message.Message` (double `.Message`). VERBATIM — `Fault.Message` IS the original instance, no re-deserialize (proven in spike, `FaultRecoverySpikeE2ETests.cs:313,329`).
- `Fault<T>.Exceptions` — an array of `ExceptionInfo`. Use `Exceptions[0]` (D-08). `ExceptionInfo` exposes (MassTransit framework type): `ExceptionType` (string, the CLR type name), `Message` (string), `StackTrace` (string), `Source`, `Data`, and a nested `InnerException` (ExceptionInfo). `[CITED: MassTransit Fault<T> / ExceptionInfo contract]` — surface `ExceptionType` + `Message` in the log (D-08 discretion: keep the StackTrace OUT of the structured log to avoid huge attributes; if needed, log it at Debug). The spike's synthetic publish proves MassTransit fills `FaultId`/`Timestamp`/`Exceptions` with defaults when published via initializer (`FaultRecoverySpikeE2ETests.cs:481-487`).
- `Fault<T>.FaultId`, `Fault<T>.Timestamp` — available if the planner wants them in the log.

**Is `Fault<EntryStepDispatch>` `ICorrelated`? NO.** Verified: `Fault<T>` is the MassTransit envelope; only the inner `EntryStepDispatch`/`ExecutionResult` implement `IExecutionCorrelated : ICorrelated` (`EntryStepDispatch.cs:12`, `ExecutionResult.cs:11`, `IExecutionCorrelated.cs:10`). So `InboundCorrelationConsumeFilter` does NOT supply the propagated correlationId for a fault consumer (it reads `Fault<T> as ICorrelated` == null → fallback). **The Keeper body MUST manually add CorrelationId to the scope from `inner.CorrelationId`** (Pattern 2). This corrects CONTEXT.md D-08's "the outer correlation filter DOES fire" — it runs but does not extract the right id; the manual scope is mandatory for SC3 correctness.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Unwrap `Fault<T>` to inner | Header parsing / re-deserialize | `context.Message.Message` (double `.Message`) | Proven verbatim in spike; `Fault.Message` IS the original instance (`FaultRecoverySpikeE2ETests.cs:313`) |
| Scope-dict build w/ skip rules | Inline copy in each Keeper consumer | `ExecutionLogScope.BuildState(inner)` (D-07) | Single source of truth; the `Guid.Empty`/empty-string skips + key set must not drift (regression-guarded) |
| Colocating two consumers on one queue | Custom topology/binding code | Two `ConsumerDefinition`s w/ same `EndpointName` + `ConfigureEndpoints` | Idiomatic; `ConfigureEndpoints` groups by EndpointName (Pattern 1) |
| Retry on exhaustion → `_error` | Custom dead-letter | `UseMessageRetry(Immediate(N))` (MassTransit default `_error`) | Already the cross-console pattern; DLQ-1 consolidation is Phase 36 |
| ES log readback | Manual ES client/backoff | `ElasticsearchTestClient.PollEsForLog` | Verified Wave-0 index/field constants (`EsIndexNames`) |
| Correlation id on `Fault<T>` log | Rely on the bus-wide filter | Manual `BeginScope([CorrelationKeys.LogScope] = inner.CorrelationId)` | The filter CANNOT see the inner corrId on a `Fault<T>` envelope (key finding) |

**Key insight:** Phase 33 already de-risked and proved LIVE the entire bind → double-unwrap → extract → flag[H]-collapse path. Phase 35 is a thin production wiring of that proven mechanism into the Keeper container plus the manual log-scope. Do NOT re-derive any of the spike's findings; reuse the harness verbatim for SC3.

---

## Common Pitfalls

### Pitfall 1: Wrong CorrelationId in the Keeper log (the SC3-killer)
**What goes wrong:** Relying on the bus-wide `InboundCorrelationConsumeFilter` to supply the propagated correlationId for a `Fault<T>` consumer. It can't — `Fault<T>` is not `ICorrelated`, so the filter scopes `context.CorrelationId` (envelope id) or a fresh Guid. SC3's "correlated to the original execution by correlationId" then fails.
**How to avoid:** Manually `BeginScope([CorrelationKeys.LogScope] = inner.CorrelationId.ToString())` in the consumer body BEFORE the log (Pattern 2). MEL inner-overrides-outer, so the body's scope wins.
**Warning signs:** The Keeper ES log's `attributes.CorrelationId` is a random Guid not matching the tripped message's correlationId.

### Pitfall 2: Drifting the scope-key set during the D-07 refactor
**What goes wrong:** The refactor changes a skip rule (e.g. drops the `!string.IsNullOrEmpty(EntryId)` guard, or `.ToString()`s the EntryId) — silently splitting the ES attribute or breaking the empty-skip. Touches a base library used by ALL consoles.
**How to avoid:** Keep `BuildState` byte-identical to the current filter body (Pattern 3). Run the four `ConsoleExecutionScopeFilterTests` cases + `ExecutionLogScopeKeyTests` as the regression guard (see below) — they assert exact key count, Guid.Empty skip, empty-string EntryId skip, and verbatim EntryId.
**Warning signs:** `ConsoleExecutionScopeFilterTests.Case_A` fails on `Assert.Equal(5, scope.Count)`; or processor/orchestrator ES logs lose an `attributes.*` field.

### Pitfall 3: Double `UseMessageRetry` on the shared endpoint (D-03)
**What goes wrong:** Both `ConsumerDefinition`s call `endpointConfigurator.UseMessageRetry(...)` for the SAME endpoint — applying the retry middleware twice on `keeper-fault-recovery`.
**How to avoid:** Have ONE definition own the endpoint-level `UseMessageRetry`, OR use the explicit `ReceiveEndpoint` form (Pattern 1 alternative). The Limit is identical (same `"Retry"` section) so it is harmless functionally, but keep intent unambiguous.
**Warning signs:** Unexpected retry-count behavior; doubled retry on one fault.

### Pitfall 4: Pulling DLQ-1/TTL topology into Phase 35
**What goes wrong:** SC1's "consolidates into TTL'd forensic DLQ-1" wording tempts building `x-message-ttl`/dead-letter-exchange/consolidated transport — that is Phase 36 (DLQ-04).
**How to avoid:** Phase 35 only CONFIRMS the separation (D-02). Assert Keeper binds the fault exchanges, not `_error`; do not change any `_error` topology.
**Warning signs:** A task creating a `keeper-dlq` queue or setting RabbitMQ TTL args.

### Pitfall 5: Stale container SourceHash on the SC3 live proof
**What goes wrong:** The RealStack proof runs against a Keeper container whose embedded SourceHash/code predates the Phase-35 changes — the new fault consumers aren't running, so no Keeper ES log appears.
**How to avoid:** `docker compose up -d --build keeper processor-sample orchestrator baseapi-service` before the live proof (per project memory + `33-02-SUMMARY.md:119-123`). The Keeper container MUST be rebuilt this phase (its consumers changed).
**Warning signs:** SC3 `PollEsForLog` times out with no `service.name=keeper` hit despite a tripped fault.

---

## Code Examples (verified patterns from this codebase)

### Double-unwrap a `Fault<T>` (proven live, spike)
```csharp
// Source: tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs:311-315
public Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context)
{
    var m = context.Message.Message;   // double .Message — the VERBATIM original instance
    // m.H, m.CorrelationId, m.WorkflowId, m.StepId, m.ProcessorId, m.EntryId, m.ExecutionId
}
```

### Nested BeginScope with execution ids (mirror the convention)
```csharp
// Source: src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:170-188
using (logger.BeginScope(new Dictionary<string, object>
{
    [ExecutionLogScope.ExecutionId] = executionId.ToString(),
    [ExecutionLogScope.EntryId]     = blobHash,
}))
{
    logger.LogInformation("Dispatch {CorrelationId}: ... (scoped execution ids)", dispatch.CorrelationId);
}
```

### The current scope-dict builder being refactored (D-07 source)
```csharp
// Source: src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs:28-36
var state = new Dictionary<string, object>();
if (ec.WorkflowId  != Guid.Empty) state[ExecutionLogScope.WorkflowId]  = ec.WorkflowId.ToString();
if (ec.StepId      != Guid.Empty) state[ExecutionLogScope.StepId]      = ec.StepId.ToString();
if (ec.ProcessorId != Guid.Empty) state[ExecutionLogScope.ProcessorId] = ec.ProcessorId.ToString();
if (ec.ExecutionId != Guid.Empty) state[ExecutionLogScope.ExecutionId] = ec.ExecutionId.ToString();
if (!string.IsNullOrEmpty(ec.EntryId)) state[ExecutionLogScope.EntryId] = ec.EntryId;
using (logger.BeginScope(state)) await next.Send(context);
```

### ES log query shape (adapt for the Keeper log, SC3)
```csharp
// Source: tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs:518-534 (adapt service.name → "keeper")
// term attributes.CorrelationId = corr:D ; term attributes.StepId = step:D ;
// term resource.attributes.service.name = "keeper" ; wildcard body.text = "*keeper fault intake*"
// poll via ElasticsearchTestClient.PollEsForLog(query, EsPollTimeoutMs, ct: ct)
```

---

## SC3 RealStack proof vehicle (D-09) — concrete recipe

**Service identifier to filter ES logs on:** `service.name = "keeper"` (from `src/Keeper/appsettings.json:10` `"Service":{"Name":"keeper"}`; the compose `keeper:` block sets NO `Service__Name` env override, so appsettings wins — `compose.yaml:241-246`). ES field path: `resource.attributes.service.name` (`EsIndexNames.ResourceAttributesFieldPath = "resource.attributes"`). The log body is matched on `body.text` (per project OTLP/E2E gotchas + `FaultRecoverySpikeE2ETests.cs:529`). The correlation/exec ids are at `attributes.CorrelationId` / `attributes.WorkflowId` / `attributes.StepId` / etc. (`EsIndexNames.CorrelationIdFieldPath = "attributes.CorrelationId"`, capital-keyed scope keys preserved by the MEL bridge — `EsIndexNames.cs:71`).

**Vehicle (D-09 discretion):** Extend `FaultRecoverySpikeE2ETests` OR add a sibling RealStack test in `tests/BaseApi.Tests/Orchestrator/`. Recommended: a sibling `KeeperFaultIntakeE2ETests` cloning the spike's harness (it already binds, trips, and tears down net-zero), so the standing spike's invariant stays untouched and the new assertion is isolated. Reuse verbatim: `RealStackWebAppFactory` (env-var host overrides incl. `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`, `FaultRecoverySpikeE2ETests.cs:743-820`), `PollForHealthyLivenessAsync`, `ArmWrongTypePoisonAsync`, the net-zero `skp:*` teardown (`L2KeysToCleanup`/`ParentIndexMembersToSrem`).

**Live-trip recipe (proven WRONGTYPE):** Seed processor/step/workflow (`* * * * *` cron), poll the live `processor-sample` Healthy heartbeat, then arm `ArmWrongTypePoisonAsync(L2ProjectionKeys.Flag(dispatchH))` (a LIST → the `EntryStepDispatchConsumer` effect-first dedup-gate GET throws WRONGTYPE on every delivery → `Immediate(N)` exhausts → `Fault<EntryStepDispatch>` fans out, `FaultRecoverySpikeE2ETests.cs:127-156`). This time, instead of an in-test probe bus, let the RUNNING Keeper container consume it (D-09). For the result type, the spike's D-06 synthetic fallback (`PublishSyntheticResultFaultAsync`, `:472-488`) is acceptable if the live result window proves fragile — but for SC3 the dispatch trip alone proves the Keeper container emits a correlated log.

**The SC3 assertion:** After arming the trip (don't clear the poison — you WANT the fault published), `PollEsForLog` a query that matches the Keeper-emitted intake log by `attributes.CorrelationId = dCorr:D` + `attributes.StepId = stepId:D` + `resource.attributes.service.name = "keeper"` + `wildcard body.text = "*keeper fault intake*"` (the chosen log phrasing, D-08). `Assert.NotNull(hit)` proves the running Keeper container emitted the correlated log end-to-end. Settle window: reuse `EsPollTimeoutMs = 120_000` (`FaultRecoverySpikeE2ETests.cs:80`). Net-zero teardown: stop the workflow (`POST /api/v1/orchestration/stop`) + register all run-minted `skp:data:*`/`skp:flag:*` + the poison key for deletion (`:278-292`).

**Container rebuild (mandatory):** `docker compose up -d --build keeper processor-sample orchestrator baseapi-service` before the run — Keeper's code changed this phase, so its embedded SourceHash/image must be rebuilt or the old placeholder consumer runs and no intake log appears (Pitfall 5).

---

## Read-First + Regression-Guard file list (for the planner)

### Read-first (canonical, before implementing)
- `src/Keeper/Program.cs` (swap target, lines 27-28)
- `src/Keeper/Consumers/PlaceholderConsumer.cs`, `PlaceholderConsumerDefinition.cs`, `KeeperPlaceholder.cs` (DELETE wholesale)
- `src/Orchestrator/Consumers/ResultConsumer.cs` + `ResultConsumerDefinition.cs` (consumer + definition template)
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:170-188` (nested BeginScope convention)
- `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs:22-37` (the dict-builder to extract, D-07)
- `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs:35-37` (proves the corrId fallback on Fault<T>)
- `src/Messaging.Contracts/ExecutionLogScope.cs` (helper home), `CorrelationKeys.cs:7`, `IExecutionCorrelated.cs`, `EntryStepDispatch.cs`, `ExecutionResult.cs`, `KeeperQueues.cs:15`, `Configuration/RetryOptions.cs`
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:34-64` (registration flow + the configureBus seam)
- `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` (SC3 harness to clone/extend)

### Regression guards (must stay GREEN after the D-07 refactor — base library change)
| Test file | What it pins |
|-----------|--------------|
| `tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs` | Case A (5 keys, no CorrelationId), Case B (Guid.Empty skip), Case C (non-IExecutionCorrelated pass-through), Case D (empty-string EntryId skip) — the EXACT `BuildState` behavior |
| `tests/BaseApi.Tests/Contracts/ExecutionLogScopeKeyTests.cs` | each `ExecutionLogScope` key string == its param name |
| `tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs` | EntryStepDispatchConsumer scope behavior (uses ExecutionLogScope keys) |
| `tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs` | runtime scope assertions |
| `tests/BaseApi.Tests/Orchestrator/WorkflowFireJobScopeTests.cs` | fire-job scope keys |
| `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` | end-to-end scope propagation |
| `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs` | ProcessorId scope/enrich |
| `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` | the standing spike (must not break if extended) |

---

## State of the Art

| Old (Phase 34) | New (Phase 35) | Impact |
|----------------|----------------|--------|
| `PlaceholderConsumer` / `KeeperPlaceholder` no-op on `keeper-fault-recovery` | Two real `IConsumer<Fault<T>>` on the same queue | Real fault intake; placeholder deleted |
| Scope-dict inline in `InboundExecutionScopeConsumeFilter` only | Shared `ExecutionLogScope.BuildState` called by filter + Keeper | Single source of truth (D-07) |
| Faults observed only by the in-test spike bus | Observed by the running Keeper container (SC3) | Production fault-intake path live |

**Deprecated/outdated:** none.

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `ExceptionInfo` exposes `ExceptionType` + `Message` + `StackTrace` (MassTransit 8.5.x) | Consumer bodies / D-08 | LOW — field names are stable across MassTransit 8.x; if a name differs, the log template adjusts. Verify with a one-line compile against the referenced MassTransit version. Tagged `[CITED: MassTransit Fault<T>/ExceptionInfo]` not `[VERIFIED]` because I did not grep a decompiled MassTransit symbol in this session. |
| A2 | The RabbitMQ fault message-type exchange names are `Messaging.Contracts:Fault--EntryStepDispatch` / `...Fault--ExecutionResult` | Architecture diagram | LOW — exact exchange name only matters if a test introspects RMQ bindings by name; the bind/positive+negative-delivery proof (spike) does not depend on the literal name. Confirm via `rabbitmqctl list_exchanges` if a binding-name assertion is written. |

**Note:** Everything else in this research is `[VERIFIED: codebase grep/read at file:line]` or proven live by the Phase-33 spike. The two `[ASSUMED/CITED]` items are MassTransit-framework details, low-risk, and resolvable by a single build/compile or `rabbitmqctl` probe during Wave 0.

---

## Open Questions

1. **Should the SC3 test extend the spike or be a sibling?**
   - Known: D-09 leaves it to discretion; the spike harness is reusable verbatim.
   - Recommendation: SIBLING `KeeperFaultIntakeE2ETests` (clone the harness) so the standing spike invariant stays isolated and the Keeper-container assertion is a clean new fact.
2. **One definition owning `UseMessageRetry` vs the explicit `ReceiveEndpoint` form (Pitfall 3).**
   - Recommendation: planner picks; two-definitions form for consistency, with ONE definition owning the endpoint-level retry call (document it). Either is correct.

---

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | build | ✓ (project builds today) | net8 | — |
| MassTransit / MassTransit.RabbitMQ | consumers | ✓ (referenced) | 8.5.x | — |
| Docker compose stack (rabbitmq, redis, otel-collector, elasticsearch, keeper, processor-sample, orchestrator, baseapi) | SC3 live proof | ✓ (compose.yaml) | — | SC3 is operator-gated (do-not-block precedent, Phase 33) |

**Missing dependencies with no fallback:** none. **With fallback:** the SC3 live proof requires the full rebuilt compose stack; per the Phase-31..34 precedent it is operator-gated (the authored test + runbook complete the plan; `GATE_EXIT=0` confirms the live half).

---

## Validation Architecture

> nyquist_validation enabled (no explicit `false` in config) — this section feeds 35-VALIDATION.md.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (v3 — `TestContext.Current.CancellationToken`) |
| Config / traits | `[Trait("Category","RealStack")]` for live; hermetic tests use the in-memory MassTransit test harness (`AddMassTransitTestHarness`) |
| Quick run (hermetic scope tests) | `dotnet test tests/BaseApi.Tests -- --filter-class "*ConsoleExecutionScopeFilterTests"` |
| Full RealStack (SC3) | `dotnet test tests/BaseApi.Tests -- --filter-class "*KeeperFaultIntakeE2ETests"` (or extend `*FaultRecoverySpikeE2ETests`) |
| Full suite gate | `pwsh -NoProfile -File ./scripts/phase-35-close.ps1` (Phase 38 owns the formal close gate; Phase 35 may clone the 33 gate if a gate is desired) |

### Success-criterion → observable signal map
| SC | Behavior | Observable signal | Test type | Automated command |
|----|----------|-------------------|-----------|-------------------|
| SC1 (INTAKE-03 slice) | Keeper recovers off `Fault<T>` pub/sub, never reads `_error`; no double-process | Keeper consumer receives a published `Fault<EntryStepDispatch>` (positive bind); non-execution `Fault<Start/Stop>` NOT delivered (negative); Keeper binds fault exchanges, NOT `*_error` | RealStack bind fact (reuse spike positive/negative) + hermetic harness consume-assert | `dotnet test ... --filter-class "*KeeperFaultIntake*"` |
| SC2 (KMET-04) | Keeper opens execution log-scope from the inner message | The Keeper-emitted log carries `attributes.CorrelationId` (== inner.CorrelationId) + `WorkflowId/StepId/ProcessorId/ExecutionId/EntryId` (non-empty ones) | hermetic scope-capture (clone `ConsoleExecutionScopeFilterTests` capturing-provider against the Keeper consumer) + RealStack ES readback | hermetic: `--filter-class "*KeeperFaultScope*"`; live: SC3 query |
| SC3 | A faulted message processed by the running Keeper container produces a correlated ES log | `PollEsForLog` returns a hit with `service.name=keeper` + `attributes.CorrelationId=dCorr` + `attributes.StepId=stepId` + `body.text` ~ "keeper fault intake" | RealStack (Keeper CONTAINER) | `--filter-class "*KeeperFaultIntakeE2ETests"` + container rebuild |
| Regression | D-07 refactor keeps filter behavior byte-identical | the 4 `ConsoleExecutionScopeFilterTests` cases + `ExecutionLogScopeKeyTests` stay GREEN | hermetic | `--filter-class "*ConsoleExecutionScopeFilterTests" / "*ExecutionLogScopeKeyTests"` |

### Sampling rate
- **Per task commit:** the hermetic scope + Keeper-consumer-body tests (sub-30s, in-memory harness).
- **Per wave merge:** full hermetic suite (`dotnet test tests/BaseApi.Tests` minus RealStack).
- **Phase gate / SC3:** the RealStack Keeper-container ES-readback test against the rebuilt compose stack (operator-gated, do-not-block precedent).

### Wave 0 gaps
- [ ] `tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs` (or `Console/`) — hermetic: assert the new Keeper consumer opens the CorrelationId + 5 exec-id scope from an inner `IExecutionCorrelated` (covers SC2 fast). Clone the `ConsoleExecutionScopeFilterTests` capturing-provider rig (`ConsoleExecutionScopeFilterTests.cs:65-90`). NEW file.
- [ ] `tests/BaseApi.Tests/Orchestrator/KeeperFaultIntakeE2ETests.cs` — RealStack SC3 ES-readback (sibling of the spike). NEW file.
- [ ] Confirm `ExceptionInfo` field names compile against the referenced MassTransit (A1) — single build.
- [ ] (optional) `scripts/phase-35-close.ps1` if a per-phase close gate is wanted (clone `phase-33-close.ps1`); otherwise Phase 38 owns the milestone gate.
- Framework install: none — xUnit + MassTransit test harness already present.

---

## Security Domain

> security_enforcement: absent in config → treated as enabled. Phase 35 is logs-only intake; low new attack surface.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Keeper has no auth surface (worker console, broker creds via compose env) |
| V3 Session Management | no | — |
| V4 Access Control | no | — |
| V5 Input Validation / Log Injection | yes | IDs placed as scope VALUES under fixed keys, NEVER interpolated into the message template (T-18-04 convention, `InboundExecutionScopeConsumeFilter.cs:14-15`; `EntryStepDispatchConsumer.cs:99`). The Keeper body MUST follow the same rule — `inner.H`, ids, and `ex.Message` go in as structured params, never string-concatenated. |
| V6 Cryptography | no | `H` is content-addressing SHA-256, not security (T-31-03, `MessageIdentity.cs:12-15`) — no hand-rolled crypto added |
| V7 Error Handling / Logging | yes | The fault exception message is logged as a structured field; do NOT log the full StackTrace at Information (large attribute / potential info leak) — surface `ExceptionType` + `Message` (D-08) |

### Known threat patterns for this stack
| Pattern | STRIDE | Standard mitigation |
|---------|--------|---------------------|
| Log injection via faulted message ids/exception text | Tampering / Repudiation | Structured params only, never template interpolation (existing T-18-04 convention) |
| Unbounded attribute size from StackTrace in logs | DoS (storage) | Log `ExceptionType` + `Message` only; StackTrace at Debug if needed |
| Wrong correlationId hides a real fault from operators | Repudiation | Manual inner-CorrelationId scope (Pitfall 1) — ensures the fault log is traceable to the original execution |

---

## Sources

### Primary (HIGH — codebase, read at file:line)
- `src/Keeper/Program.cs`, `src/Keeper/Keeper.csproj`, `src/Keeper/appsettings.json`, `src/Keeper/Consumers/*.cs`
- `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs`, `InboundCorrelationConsumeFilter.cs`
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs`, `BaseConsoleObservabilityExtensions.cs`
- `src/Messaging.Contracts/{ExecutionLogScope,CorrelationKeys,IExecutionCorrelated,ICorrelated,EntryStepDispatch,ExecutionResult,KeeperQueues}.cs`, `Hashing/MessageIdentity.cs`
- `src/Orchestrator/Consumers/ResultConsumer.cs` + `ResultConsumerDefinition.cs`
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs`
- `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs`
- `tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs`, `tests/BaseApi.Tests/Contracts/ExecutionLogScopeKeyTests.cs`
- `tests/BaseApi.Tests/Observability/Helpers/{ElasticsearchTestClient,EsIndexNames}.cs`
- `compose.yaml` (keeper tier), `.planning/REQUIREMENTS.md:25-58`
- `.planning/phases/33-fault-recovery-spike-de-risk/33-02-SUMMARY.md` (live-proven spike findings)
- `.planning/phases/35-fault-intake-correlation/35-CONTEXT.md` (locked decisions)

### Secondary (MEDIUM/CITED)
- MassTransit `Fault<T>` / `ExceptionInfo` contract (framework type — A1, confirm at build)

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new packages; all assets exist and are referenced.
- Architecture / colocation: HIGH — two-definition same-EndpointName pattern is in production; spike proved the topology.
- Correlation-filter finding (manual CorrelationId scope): HIGH — verified by reading `InboundCorrelationConsumeFilter.cs:35` + the `Fault<T>`/`ICorrelated` type relationship.
- SC3 vehicle: HIGH — the spike harness is reusable verbatim; only the assertion (Keeper container ES log) is new.
- `ExceptionInfo` field names: MEDIUM (A1) — stable MassTransit 8.x, confirm at build.

**Research date:** 2026-06-05
**Valid until:** ~30 days (stable internal codebase; only churn risk is a MassTransit major bump, which is pinned via CPM)
