# Phase 53: Model-B Teardown - Research

**Researched:** 2026-06-11
**Domain:** MassTransit 8.5.5 retry/error-transport teardown + RETIRE-03 reflection/source-scan remnant guards
**Confidence:** HIGH (all three HOW questions resolved against installed code + prior verified research; the redelivery mechanism is the one MEDIUM item, mitigated by a worked in-repo precedent)

## Summary

This is a **code-to-doc alignment** phase, not a feature phase. The Model-B *contracts and consumers were already deleted* (Phase 50 dropped the composite backup key/`UPDATE`/`CLEANUP`/`BackupOptions`; Phase 52 left the keeper recovery consumer at exactly 3 states). So SC-1/SC-2 are largely **verification**. The genuinely-unfinished work is the **Phase-52 D-08 deferral**: A18 §Global-rules (`UseMessageRetry = none` / `_error` routing disabled / send-exhaust → throw → broker redelivery) is still unwired on the processor + orchestrator endpoints, which still carry the Phase-44 D-09 `UseMessageRetry(Immediate(N)) → skp-dlq-1` outer latch.

The work decomposes into four mechanical changes plus guards: (1) remove the processor keep-latch (`ProcessorStartupOrchestrator.cs:180`) and the single-owner `UseMessageRetry` on every orchestrator shared endpoint; (2) scope the global `GenerateFaultFilter` + `ConsolidatedErrorTransportFilter` (today applied to **every** endpoint via `AddConfigureEndpointsCallback` in `BaseConsole.Core`) so they apply to the keeper recovery endpoint **only**; (3) reconcile the processor pipeline's `throw → _error` doc-comments to the new throw → broker-redelivery end-state; (4) extend `ModelBContractsRetiredFacts` with a 5→3-state assertion, a `corr:wf:proc:exec` source sweep, and a **new standing guard** that no `UseMessageRetry`/error-transport wiring survives on the processor + orchestrator endpoints.

The single load-bearing technical question — *with no error transport and no retry, what makes a thrown send redeliver instead of being discarded?* — has a verified answer in this very repo: the keeper's `RecoveryEndpointBinder` SustainedOutage mode already relies on the fact that **without the error pipeline a thrown delivery is nacked-requeued by RabbitMQ (redelivered)**, not acked-discarded. The naive trap is `DiscardFaultedMessages` semantics; the safe levers are documented below.

**Primary recommendation:** Strip `UseMessageRetry` from the processor + all orchestrator definitions and **do not** add any error pipeline to those endpoints (the default, when neither `GenerateFaultFilter` nor `ErrorTransportFilter` is installed, is nack-requeue → broker redelivery — exactly A18). Move the `ConfigureError` filter pair out of the global `AddConfigureEndpointsCallback` and apply it **inside `RecoveryEndpointBinder`'s `ConnectReceiveEndpoint` callback** (the keeper-local seam — cleanest because the keeper already owns a per-endpoint connect callback; no new opt-in flag needed). Lock the end-state with a reflection/source-scan fact mirroring `ReactivePathRetiredFacts`.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Processor dispatch retry/redelivery | Processor console (`ProcessorStartupOrchestrator` connect callback) | RabbitMQ broker | A18: in-code `RetryLoop` owns per-op retry; bus owns none; throw → broker redelivery |
| Orchestrator result/control retry | Orchestrator consumer definitions (per shared endpoint) | RabbitMQ broker | Single-owner `UseMessageRetry` per shared endpoint removed; broker handles redelivery |
| Consolidated error-transport (`skp-dlq-1`) | Keeper recovery endpoint (post-Phase-53) | `BaseConsole.Core` topology (queue declaration) | D-03: scope the filter to keeper-only; the queue declaration + TTL stay shared infra |
| `Fault<T>` publication | Keeper recovery endpoint only (post-Phase-53) | — | `GenerateFaultFilter` moves with the consolidated filter; nothing else rides Fault<T> anymore |
| End-state regression guard | `tests/BaseApi.Tests/Resilience` (reflection/source-scan, no host) | — | D-06: hermetic facts, mirror existing negative-guard idiom |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 | Bus, retry middleware, error transport pipeline | CPM-pinned; last Apache-2.0 line (v9+ commercial). `[VERIFIED: Directory.Packages.props:137]` + installed at `~/.nuget/packages/masstransit/8.5.5` `[VERIFIED: nuget cache listing]` |
| MassTransit.RabbitMQ | 8.5.5 | RabbitMQ transport (nack-requeue redelivery, error exchange) | `[VERIFIED: Directory.Packages.props:138]` |
| xUnit | (repo-pinned) | Hermetic facts (reflection + source-scan) | The existing `*RetiredFacts` suites are xUnit `[Fact]`/`[Trait]`. `[VERIFIED: ReactivePathRetiredFacts.cs]` |

### Supporting
No new libraries. This phase **removes** wiring; it adds only test code that uses `System.Reflection` + `System.IO` (the verified idiom already in `ReactivePathRetiredFacts`).

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Default nack-requeue (no error pipeline) | `RethrowFaultedMessages()` + `ThrowOnSkippedMessages()` explicit nack config | Explicit-intent config; but it's not strictly needed if the error filters are simply absent, and it adds API surface the A18 rule doesn't mention. Keep as the documented fallback if "absence = redelivery" proves unreliable under spike. `[CITED: github discussion #3513]` |
| Default nack-requeue | Keeper's never-exhausting `Interval(1_000_000, 1s)` retry (SustainedOutage trick) | Guarantees no dead-letter by never exhausting; but it's a workaround for an endpoint that has a *globally-applied* error filter it can't remove. Once the filter is scoped keeper-only (D-03), processor/orchestrator need no such trick — plain absence suffices. |

**Installation:** No package changes.

**Version verification:** MassTransit 8.5.5 confirmed both in CPM (`Directory.Packages.props:137-138`) and in the local NuGet cache (`~/.nuget/packages/masstransit/8.5.5/lib/net8.0/MassTransit.dll`). No `npm`/registry step applies (.NET CPM). `[VERIFIED]`

## Architecture Patterns

### System Architecture Diagram

```
                          ┌─────────────────────────────────────────────┐
  EntryStepDispatch  ───► │ Processor dispatch endpoint  queue:{id:D}    │
  (from orchestrator)     │  ProcessorStartupOrchestrator connect cb     │
                          │  • NO UseMessageRetry   (was Immediate(N))   │  ◄── D-01 remove
                          │  • ProcessorPipeline.RunAsync                │
                          │     - per-op RetryLoop (UNCHANGED)           │
                          │     - send exhausts → throw  ────────────┐   │
                          └──────────────────────────────────────────┼───┘
                                                                      │ throw, no error pipeline
                                                                      ▼
                                                          ┌───────────────────────┐
                                                          │  RabbitMQ broker        │
                                                          │  nack → requeue         │ ◄── A18 redelivery
                                                          │  (NO _error, NO DLQ)    │     (default when no
                                                          └───────────┬─────────────┘     error filters)
                                                                      │ redeliver
                                                                      ▼ (back to the same endpoint)

  Step* results / Start/Stop / Pause/Resume  ──► Orchestrator shared endpoints
                                                  • orchestrator          (Start/Stop)
                                                  • orchestrator-result   (4 typed results)
                                                  • orchestrator-pauseresume
                                                  • orchestrator-global-pauseresume
                                                  ALL: NO UseMessageRetry  ◄── D-01/D-02 remove single-owner
                                                  throw → broker redelivery (same as above)

  KeeperReinject/Inject/Delete  ──► Keeper recovery endpoint  keeper-recovery
                                     RecoveryEndpointBinder connect cb (UNCHANGED behavior; D-05)
                                     • ConfigureError(GenerateFaultFilter, ConsolidatedErrorTransportFilter) ◄── D-03 MOVED here
                                     • Dlq1 mode: Immediate(N) → exhaust → skp-dlq-1
                                     • SustainedOutage: Interval(1e6,1s) → never dead-letter
                                                  │ Dlq1 exhaust
                                                  ▼
                                     ┌────────────────────────────┐
                                     │ skp-dlq-1 (TTL 7d, passive) │  ◄── ONLY producer post-Phase-53
                                     │ declared in BaseConsole.Core │      (queue decl stays shared)
                                     └────────────────────────────┘
```

A reader traces: a dispatch enters the processor endpoint, the in-code `RetryLoop` exhausts on a send, the consumer throws, and — because no error pipeline is installed on that endpoint — RabbitMQ nack-requeues for redelivery (no dead-letter). The keeper endpoint is the lone retainer of the error pipeline and the lone producer into `skp-dlq-1`.

### Recommended Project Structure (touch list — no new structure)
```
src/
├── BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs   # remove cfg.UseMessageRetry (line ~180)
├── BaseProcessor.Core/Processing/ProcessorPipeline.cs           # reconcile "→ _error" comments (lines ~57, 311, 318, 344)
├── BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs   # reconcile "→ _error" comment (lines ~22)
├── Orchestrator/Consumers/*ConsumerDefinition.cs                # remove single-owner UseMessageRetry (see table)
├── BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs  # remove global ConfigureError callback (lines 56-63)
├── Keeper/Recovery/RecoveryEndpointBinder.cs                    # ADD ConfigureError filter pair here (keeper-local seam)
└── (skp-dlq-1 queue declaration in MessagingServiceCollectionExtensions stays — only its sole producer changes)
tests/
└── BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs      # D-06: extend with 3 new facts
```

### Pattern 1: Single-owner `UseMessageRetry` removal (orchestrator)
**What:** On each *shared* endpoint exactly ONE `ConsumerDefinition` registers `UseMessageRetry`; siblings are intentional no-ops. Removal must target the owner only — but since the goal is *zero* retry, the no-op siblings need no change (they already register nothing).
**The complete owner map (so removal misses nothing):**

| Shared endpoint (`EndpointName`) | Retry OWNER definition | Co-located no-op siblings (no retry — leave) |
|----------------------------------|------------------------|-----------------------------------------------|
| `orchestrator` | `StartOrchestrationConsumerDefinition.cs:38` (also `Ignore<WorkflowRootNotFoundException>`) **and** `StopOrchestrationConsumerDefinition.cs:33` — **NOTE: BOTH register it** (same endpoint, both definitions). Verify which actually wins / remove from BOTH. | — |
| `orchestrator-result` (`OrchestratorQueues.Result`) | `StepCompletedConsumerDefinition.cs:40` | `StepFailedConsumerDefinition`, `StepCancelledConsumerDefinition`, `StepProcessingConsumerDefinition` (no-op `ConfigureConsumer` comments) |
| `orchestrator-pauseresume` | `PauseWorkflowConsumerDefinition.cs:38` | `ResumeWorkflowConsumerDefinition` (no retry; sets nothing) |
| `orchestrator-global-pauseresume` | `PauseAllConsumerDefinition.cs:37` | `ResumeAllConsumerDefinition` (sets `ConcurrentMessageLimit=1` only) |

> ⚠️ **Pitfall (verified by source-read):** the `orchestrator` endpoint is shared by Start **and** Stop, and BOTH `StartOrchestrationConsumerDefinition` and `StopOrchestrationConsumerDefinition` call `endpointConfigurator.UseMessageRetry(...)` — this is NOT the documented single-owner pattern the doc-comments on the *result* endpoint describe. `UseMessageRetry` is per-endpoint, so two registrations on one endpoint either stack or last-wins; either way both must be stripped. `[VERIFIED: StartOrchestrationConsumerDefinition.cs:38, StopOrchestrationConsumerDefinition.cs:33]`

After removal: keep `ConcurrentMessageLimit = 1` on Pause/PauseAll (that's serialization, not retry — A18-orthogonal). Keep `Ignore<WorkflowRootNotFoundException>` semantics ONLY if any retry survives; with retry fully removed, the `Ignore` becomes dead and should be deleted with the `UseMessageRetry` block.

**Example (the removal):**
```csharp
// BEFORE (StepCompletedConsumerDefinition.cs:38-41)
endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));

// AFTER — the whole ConfigureConsumer override becomes empty (or is deleted).
// _retryOptions injection becomes unused → remove the ctor param + IOptions<RetryOptions> dep
// (do this consistently so the build stays 0-warning per SC-3).
```

### Pattern 2: Processor keep-latch removal
**What:** Remove the OUTER bus retry; keep the in-code `RetryLoop`.
```csharp
// BEFORE (ProcessorStartupOrchestrator.cs:172-182)
var handle = endpointConnector.ConnectReceiveEndpoint(queueName, (ctx, cfg) =>
{
    cfg.UseMessageRetry(r => r.Immediate(retryLimit));   // ◄── REMOVE (D-01)
    cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx);
});

// AFTER — no bus retry, no error pipeline → a thrown send nack-requeues (broker redelivery).
var handle = endpointConnector.ConnectReceiveEndpoint(queueName, (ctx, cfg) =>
{
    cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx);
});
```
`retryLimit` / `retryOptions` stay live if `ProcessorPipeline` still reads `Retry:Limit` for its in-code `RetryLoop` (it does — `ProcessorPipeline` ctor takes `IOptions<RetryOptions>`). Do NOT remove the `RetryOptions` injection from the processor pipeline; only the bus-latch usage in the startup orchestrator goes.

### Pattern 3: Scope the consolidated error filter to the keeper endpoint (D-03 — THE key seam)
**What:** Move the `ConfigureError(GenerateFaultFilter + ConsolidatedErrorTransportFilter)` pair from the global `AddConfigureEndpointsCallback` (which hits EVERY endpoint) into the keeper's own per-endpoint connect callback.

**Recommended seam — keeper-local apply (cleanest):** The keeper already binds its endpoint via `RecoveryEndpointBinder.ConnectReceiveEndpoint(KeeperQueues.Recovery, (ctx, cfg) => {...})`. Add `cfg.ConfigureError(...)` inside that callback. This is cleaner than a per-endpoint opt-in flag because:
- The keeper is the ONLY endpoint that needs the filter post-Phase-53 (D-03).
- `RecoveryEndpointBinder` already owns the policy-conditional retry (`Dlq1` vs `SustainedOutage`) — the error filter belongs next to it (the Dlq1 mode's whole point is "exhaust → consolidated filter → skp-dlq-1", documented in the binder's xml-doc lines 26-35).
- No new public flag/API surface; the global callback in `BaseConsole.Core` is simply deleted.

```csharp
// RecoveryEndpointBinder.ExecuteAsync — inside ConnectReceiveEndpoint(KeeperQueues.Recovery, (ctx, cfg) => { ... })
// ADD (the pair moved verbatim from BaseConsole.Core's deleted global callback):
cfg.ConfigureError(ep =>
{
    ep.UseFilter(new GenerateFaultFilter());                  // Fault<T> publication — keeper-only now
    ep.UseFilter(new ConsolidatedErrorTransportFilter());     // exhausted → skp-dlq-1
});
// then the existing policy retry + partitioners + ConfigureConsumer<...> as today.
```

```csharp
// BaseConsole.Core/MessagingServiceCollectionExtensions.cs — DELETE the global callback (lines 56-63):
// x.AddConfigureEndpointsCallback((context, name, e) => { e.ConfigureError(ep => { ... }); });   // ◄── REMOVE
// KEEP the skp-dlq-1 topology declaration (lines 98-108) — the queue must still exist for the keeper to send to it.
```

**Alternative seam (rejected):** a per-endpoint opt-in flag threaded through `AddConfigureEndpointsCallback((ctx,name,e) => { if (name == "keeper-recovery") e.ConfigureError(...); })`. Rejected because matching on the endpoint *name string* in the shared base is brittle (renames silently disable the DLQ) and leaves the error-filter knowledge in `BaseConsole.Core` where every other console must reason about it. Keeper-local apply localizes the concern.

> ⚠️ **Topology dependency (verified):** `ConsolidatedErrorTransportFilter` sends to `exchange:skp-dlq-1`, and that exchange→queue binding is declared via `c.Publish<ConsolidatedFault>(...BindQueue...)` + `DeployPublishTopology = true` in `MessagingServiceCollectionExtensions` (lines 98-108). That declaration **must stay** in `BaseConsole.Core` — it is what makes the queue exist. Only the per-endpoint `ConfigureError` *application* moves. The keeper inherits the same bus, so the exchange is still deployed. `[VERIFIED: MessagingServiceCollectionExtensions.cs:73-108, ConsolidatedErrorTransportFilter.cs:51-59]`

### Anti-Patterns to Avoid
- **`DiscardFaultedMessages()` to suppress `_error`:** discards (acks) the message — the OPPOSITE of A18's redelivery intent. A18 wants the message *redelivered*, not dropped. `[CITED: masstransit docs — exceptions]`
- **Leaving a stray `UseMessageRetry` on a no-op sibling:** the result/pause endpoints have no-op siblings today; do not "tidy" them by adding retry. Zero retry is the target.
- **Removing the `skp-dlq-1` queue declaration:** the keeper still dead-letters in Dlq1 mode; the queue must survive. Only its *producer set* shrinks to keeper-only.
- **Removing `GenerateFaultFilter` from the keeper:** the keeper's `_error`/Fault path rides it; it moves WITH the consolidated filter, it is not deleted.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Make a thrown send redeliver | A custom requeue middleware / republish loop | Just remove the error pipeline → MassTransit nack-requeues by default | The transport already does broker redelivery when no `ErrorTransportFilter` runs. `[CITED: discussion #3513 — "throw errors back to the transport (Nack)"]` |
| Guarantee no dead-letter on a poison op (keeper SustainedOutage) | A parking queue | Never-exhausting `Interval(1_000_000, 1s)` retry (already in `RecoveryEndpointBinder`) | Proven in-repo; `int.MaxValue` OOMs the pre-allocated `TimeSpan[]`. `[VERIFIED: RecoveryEndpointBinder.cs:58-66]` |
| Prove "no Model-B remnant survives" | A new bespoke test harness | Extend `ModelBContractsRetiredFacts`; mirror `ReactivePathRetiredFacts` reflection + source-scan idiom | The repo already has the verified no-host-boot idiom incl. the `RepoRoot()` `[CallerFilePath]` anchor and the false-pass `Directory.Exists` guard. `[VERIFIED: ReactivePathRetiredFacts.cs:79-103,149-161]` |

**Key insight:** The entire D-01 redelivery behavior is achieved by *removing* code, not adding it. The only genuinely-new code is test code.

## Runtime State Inventory

> This is a rename/teardown-adjacent phase (RETIRE-03 remnant sweep). State inventory below.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | **None that this phase changes.** The composite backup key `L2[corr:wf:proc:exec]` was retired in Phase 50; no new write of it exists. The slot-array `L2[messageId]` / `L2[entryId]` keys are A18-current and untouched. `[VERIFIED: grep src/ for corr:wf:proc:exec found only doc-comment text, no key builder]` | None — verified by source sweep. |
| Live service config | **`skp-dlq-1` queue on the RabbitMQ broker** (declared passive, TTL 7d). Survives; its only *producer* becomes the keeper Dlq1 path. No broker-side change needed — the queue declaration is unchanged. | None — queue declaration unchanged; only code producers shrink. |
| OS-registered state | None. No Task Scheduler/systemd/pm2 registration embeds Model-B strings. `scheduled_tasks.lock` present in `.claude/` is unrelated (GSD harness). | None. |
| Secrets/env vars | None reference Model-B by name. `Retry:Limit` config key survives (still read by `ProcessorPipeline` in-code `RetryLoop` and keeper Dlq1). | None. |
| Build artifacts | **Stale `bin/*/Messaging.Contracts.xml` + `Keeper.xml`** contain the `corr:wf:proc:exec` doc-comment string from the partition-marker xml-doc (`[VERIFIED: grep hits under src/*/bin/]`). These are build outputs of *current* source doc-comments, not remnants — but the source sweep MUST scope to `src/**/*.cs` (NOT `bin/`) or it will false-positive on regenerated xml. | Source sweep scopes to `.cs` files under `src/`, excluding `bin/`/`obj/` (mirror `ReactivePathRetiredFacts` which enumerates `*.cs` only). |

**The canonical question — after every file is updated, what runtime systems still have the old string cached/stored/registered?** Answer: only the `corr:wf:proc:exec` literal as a *doc-comment* on the surviving partition marker (`IKeeperRecoverable` 4-tuple). The marker itself is A18-current (it keys the keeper partitioner). The string "corr:wf:proc:exec" is therefore an EXPECTED survivor in a doc-comment, NOT a Model-B remnant. The D-06 source sweep must target the retired *composite-backup-key builder* and the retired UPDATE/CLEANUP semantics — which are already gone (Phase 50, guarded by FACT 1-3) — not the partition-marker doc string. **Plan the literal scan carefully: scan for a composite-backup-KEY usage pattern (a `CompositeBackup` builder call or an `L2[...:...:...]` write), not the bare substring `corr:wf:proc:exec` which legitimately survives in the partitioner doc-comment.**

## Common Pitfalls

### Pitfall 1: Assuming "remove retry" = "messages discarded"
**What goes wrong:** Stripping `UseMessageRetry` AND the error pipeline could be assumed to drop messages.
**Why it happens:** `DiscardFaultedMessages()` exists and does drop; confusion between "no error transport" and "discard".
**How to avoid:** With NEITHER `UseMessageRetry` NOR an error pipeline, RabbitMQ nack-requeues (redelivery) — this is the default and what A18 wants. Verify with a live throw-spike before locking (the keeper's SustainedOutage facts already exercise the no-dead-letter path). `[CITED: discussion #3513]`
**Warning signs:** A message vanishes after one failed delivery instead of redelivering.

### Pitfall 2: Source sweep false-positive on the partition-marker doc-comment
**What goes wrong:** A `corr:wf:proc:exec` substring scan trips on the LEGITIMATE surviving doc-comment on `IKeeperRecoverable`'s 4-tuple marker.
**Why it happens:** The same string names the composite backup key (retired) AND the partition 4-tuple (current).
**How to avoid:** Scan for the retired KEY BUILDER / write pattern, not the substring. (FACT 1 in `ModelBContractsRetiredFacts` already pins absence of the `CompositeBackup` builder via reflection — extend that, don't substring-scan the partitioner doc.)
**Warning signs:** The new fact fails on a clean tree, or scopes into `bin/` xml.

### Pitfall 3: Missing the dual-owner retry on the `orchestrator` endpoint
**What goes wrong:** Removing retry only from `StartOrchestrationConsumerDefinition` leaves `StopOrchestrationConsumerDefinition`'s `UseMessageRetry` live (or vice versa) — the endpoint still retries.
**Why it happens:** The doc-comments describe a single-owner pattern, but the `orchestrator` endpoint actually has TWO definitions both calling `UseMessageRetry` (unlike the result/pause endpoints).
**How to avoid:** Strip from BOTH `StartOrchestrationConsumerDefinition.cs:38` and `StopOrchestrationConsumerDefinition.cs:33`. The new standing guard (D-06) should assert zero `UseMessageRetry` reachable on orchestrator endpoints to catch a missed one.
**Warning signs:** The end-state guard passes but a throw still redelivers a bounded number of times.

### Pitfall 4: Leaving dead `IOptions<RetryOptions>` injections → build warnings
**What goes wrong:** Removing the `UseMessageRetry` body but leaving the injected `_retryOptions` ctor param produces an unused-field warning, failing SC-3 (0-warning Release+Debug).
**Why it happens:** Every orchestrator definition injects `IOptions<RetryOptions>` solely for retry.
**How to avoid:** Remove the ctor param + field when the only use was retry. (Processor keeps it — `ProcessorPipeline` still reads `Retry:Limit`.) `[VERIFIED: each *ConsumerDefinition.cs ctor]`
**Warning signs:** `CS0169`/`IDE0052` on build.

### Pitfall 5: Removing the `skp-dlq-1` topology with the global callback
**What goes wrong:** Deleting the global `AddConfigureEndpointsCallback` AND the `c.Publish<ConsolidatedFault>(...BindQueue...)` declaration breaks the keeper's Dlq1 send (exchange no longer exists).
**Why it happens:** Both live in `MessagingServiceCollectionExtensions`; easy to over-delete.
**How to avoid:** Delete ONLY the `AddConfigureEndpointsCallback` (lines 56-63). KEEP `DeployPublishTopology = true` + the `Publish<ConsolidatedFault>` binding (lines 98-108).
**Warning signs:** Keeper Dlq1 send raises a routing/exchange-not-found error.

## Code Examples

### The end-state standing guard (mirror `ReactivePathRetiredFacts` source-scan)
```csharp
// Source pattern: ReactivePathRetiredFacts.cs:79-103 (verified idiom — no host boot)
// Asserts NO UseMessageRetry / ConfigureError wiring survives on processor + orchestrator source.
[Fact]
[Trait("Phase", "53")]
public void No_bus_retry_or_error_transport_on_execution_path_endpoints()
{
    foreach (var rel in new[] { Path.Combine("src","Orchestrator","Consumers"),
                                Path.Combine("src","BaseProcessor.Core","Startup") })
    {
        var dir = Path.Combine(RepoRoot(), rel);
        Assert.True(Directory.Exists(dir), $"bad anchor: {dir}");   // false-pass guard (Pitfall 5 idiom)
        var offenders = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => { var t = File.ReadAllText(f);
                          return t.Contains("UseMessageRetry") || t.Contains("ConfigureError"); })
            .ToList();
        Assert.True(offenders.Count == 0, "A18 end-state regressed: " + string.Join(", ", offenders));
    }
}
```

### The 5→3-state reflection assertion (extend `ModelBContractsRetiredFacts`)
```csharp
// Mirror ReactivePathRetiredFacts FACT 1 (interface-shape reflection).
// Exactly the 3 surviving recovery message types are consumed by the Keeper assembly; no 4th/5th.
[Fact]
[Trait("Phase", "53")]
public void Keeper_registers_exactly_three_recovery_consumers()
{
    var consumerIfaces = Keeper.GetTypes()
        .SelectMany(t => t.GetInterfaces())
        .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
        .Select(i => i.GetGenericArguments()[0].Name)
        .Where(n => n is "KeeperReinject" or "KeeperInject" or "KeeperDelete"
                 or "KeeperUpdate"   or "KeeperCleanup")   // the retired pair must NOT appear
        .Distinct().OrderBy(n => n).ToArray();
    Assert.Equal(new[] { "KeeperDelete", "KeeperInject", "KeeperReinject" }, consumerIfaces);
}
```
> Verify the keeper consumer types implement `IConsumer<KeeperReinject>` etc. directly (vs via `RecoveryConsumerBase`) — `RecoveryConsumerBase.cs` exists; confirm the interface is on the concrete consumer for the reflection to see it. `[ASSUMED: concrete consumers carry IConsumer<T>]`

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Phase-44 D-09 outer `UseMessageRetry(Immediate(N)) → skp-dlq-1` latch on every endpoint | A18 §Global-rules: `UseMessageRetry=none`, `_error` disabled, throw → broker redelivery | This phase (53) | Processor + orchestrator stop dead-lettering; broker redelivers indefinitely on poison sends (accepted, D-04) |
| Consolidated error filter applied to ALL endpoints (global callback) | Filter scoped to keeper recovery endpoint only | This phase (53) | `skp-dlq-1` receives keeper-Dlq1 traffic exclusively post-53 |
| Model-B 5-state recovery consumer | 3-state (REINJECT/INJECT/DELETE) | Phase 50/52 (verified here) | RETIRE-03 verification |

**Deprecated/outdated:**
- `rabbitmq_delayed_message_exchange` plugin + scheduled redelivery — removed Phase 24.1. No delayed-exchange redelivery is available; redelivery here is plain broker nack-requeue (immediate), which is exactly what A18 specifies. `[VERIFIED: StartOrchestrationConsumerDefinition.cs:13-17 doc-comment]`

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | With NEITHER `UseMessageRetry` NOR an error pipeline, MassTransit 8.5.5 + RabbitMQ nack-requeues (redelivers) rather than acks-discards | Pattern 1 / Pitfall 1 | If wrong, messages drop instead of redeliver → must add `RethrowFaultedMessages()`/`ThrowOnSkippedMessages()` (documented fallback) or use the keeper's never-exhausting Interval trick. **Mitigated:** the keeper's SustainedOutage mode already depends on a no-dead-letter redelivery path; a throw-spike test confirms before lock. Verify in Wave 0 / planner spike. |
| A2 | The keeper concrete consumers carry `IConsumer<KeeperReinject/Inject/Delete>` directly (visible to reflection) rather than only via `RecoveryConsumerBase` | Code Examples (5→3 fact) | If the interface is only on the base, the reflection fact needs to walk base types — adjust the LINQ. Low risk; confirm by reading the 3 consumer classes. |
| A3 | Removing the `Ignore<WorkflowRootNotFoundException>` (inside the removed retry block) has no behavioral effect once retry is gone | Pattern 1 | `Ignore` only matters with retry; with no retry a business exception throws → redelivers. If `WorkflowRootNotFoundException` is a *business* failure that should ack-and-drop (not redeliver), removing retry changes its disposition — the consumer must catch/ack it explicitly. **Verify:** does `StartOrchestrationConsumer` rely on `Ignore` to avoid redelivering a genuine business failure? If yes, that exception needs an explicit ack/catch, not bus retry. **This is a real open question — see below.** |

## Open Questions

1. **`WorkflowRootNotFoundException` disposition after retry removal.**
   - What we know: `StartOrchestrationConsumerDefinition` and `StopOrchestrationConsumerDefinition` register `r.Ignore<WorkflowRootNotFoundException>()` so a business failure does not retry-storm. A18 says business outcomes ack (the processor pipeline sends a `Step*` result and acks; orchestrator control messages may differ).
   - What's unclear: with `UseMessageRetry` removed entirely, an *unignored* throw now nack-requeues forever (D-04 accepted spin) — but a `WorkflowRootNotFoundException` is a BUSINESS failure that should NOT spin. Today `Ignore` converts it to "give up → move to `_error`" (terminal). After removal there is no `_error`, so an uncaught `WorkflowRootNotFoundException` would redeliver indefinitely — a regression.
   - Recommendation: the consumer must **catch `WorkflowRootNotFoundException` and ack** (log + drop) rather than rely on bus retry's `Ignore`. The planner must add this explicit handling to `StartOrchestrationConsumer`/`StopOrchestrationConsumer` as part of the retry removal, or confirm the exception cannot escape `Consume`. **This is the one place the teardown is not purely subtractive.** Flag to discuss-phase if the catch-and-ack changes observable behavior.

2. **Confirm the actual redelivery semantics under a live throw (A1).**
   - What we know: docs + discussion indicate nack-requeue is the no-error-pipeline default; keeper SustainedOutage relies on no-dead-letter redelivery.
   - What's unclear: whether the *immediate, no-delay* requeue causes a hot-spin CPU/broker-pressure issue on a permanently-failing send (D-04 accepts the spin, but the planner should bound observability).
   - Recommendation: Wave 0 spike — bind a throwing consumer with no retry/no error pipeline, assert the message redelivers (not discarded) and produces no `skp-dlq-1` traffic. Reuse the keeper SustainedOutage fact harness.

## Environment Availability

> This phase is code/config + test only. RabbitMQ + Redis are needed for the live throw-spike (A1/OQ-2) but the RETIRE-03 guards are hermetic (no host).

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| MassTransit / .NET SDK | Build + hermetic facts | ✓ (assumed — repo builds) | 8.5.5 / net8.0 | — |
| RabbitMQ broker | Live throw-spike (A1 confirmation) | ? (not probed) | — | Reuse existing keeper SustainedOutage live test harness; if unavailable, rely on the hermetic source-scan guard + documented redelivery semantics |
| Redis | not needed for this phase's guards | — | — | — |

**Missing dependencies with no fallback:** None — the RETIRE-03 deliverables are hermetic facts.
**Missing dependencies with fallback:** Live broker for A1 confirmation — fall back to the existing keeper live-test harness if present.

## Validation Architecture

> `nyquist_validation` enabled. Every success criterion + the D-01 end-state invariant maps to a HERMETIC fact (reflection or source-scan, NO host boot), mirroring the verified `ReactivePathRetiredFacts` / `ModelBContractsRetiredFacts` idiom. One live throw-spike is the only non-hermetic check and is OPTIONAL confirmation of A1.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (repo-pinned; `[Fact]`/`[Trait("Phase", "53")]`) |
| Config file | none custom — standard xUnit discovery under `tests/BaseApi.Tests` |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "Trait=Phase&Phase=53"` (or `--filter FullyQualifiedName~RetiredFacts`) |
| Full suite command | `dotnet test SK_P.sln -c Release` |

### Phase Requirements → Test Map
| Req / Criterion | Behavior | Test Type | Automated Command | File Exists? |
|-----------------|----------|-----------|-------------------|--------------|
| SC-1 (composite key + UPDATE/CLEANUP gone) | reflection: no `CompositeBackup` builder; no `KeeperUpdate`/`KeeperCleanup`/`BackupOptions` types | unit (reflection) | `dotnet test ...~ModelBContractsRetiredFacts` | ✅ FACT 1-3 already exist (Phase 50) |
| SC-2 / RETIRE-03 (5→3 collapse; no remnant) | reflection: keeper consumes EXACTLY KeeperReinject/Inject/Delete; source sweep finds no composite-backup-key BUILDER usage under `src/**/*.cs` | unit (reflection + source-scan) | `dotnet test ...~ModelBContractsRetiredFacts` | ❌ Wave 0 — add 5→3 fact + scoped source sweep |
| D-01 end-state (no bus retry / no `_error` on exec path) | source-scan: zero `UseMessageRetry`/`ConfigureError` under `src/Orchestrator/Consumers` + `src/BaseProcessor.Core/Startup` | unit (source-scan) | `dotnet test ...~ModelBContractsRetiredFacts` (or new `ExecutionPathEndStateFacts`) | ❌ Wave 0 — new standing guard |
| D-03 (filter keeper-only) | source-scan: `ConfigureError`/`ConsolidatedErrorTransportFilter` reachable ONLY under `src/Keeper/`; absent from `BaseConsole.Core` global callback | unit (source-scan) | same suite | ❌ Wave 0 |
| D-04 / A1 (throw → redelivery, no dead-letter) | live: a throwing exec-path consumer redelivers and produces no `skp-dlq-1` traffic | integration (live, OPTIONAL) | reuse keeper SustainedOutage live harness | ❌ Wave 0 (optional; gated on broker) |
| SC-3 (0-warning Release+Debug) | build is clean both configs | build gate | `dotnet build SK_P.sln -c Release` && `-c Debug` (warnaserror or `/warnaserror`) | n/a — toolchain |

### Sampling Rate
- **Per task commit:** `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~RetiredFacts"` (fast, hermetic).
- **Per wave merge:** `dotnet test SK_P.sln` (full hermetic suite).
- **Phase gate:** full Release + Debug build 0-warning AND full suite green before `/gsd-verify-work`. The live throw-spike (A1) runs if a broker is available; otherwise the hermetic source-scan + documented semantics carry SC and A1 is confirmed during Phase 54's live proof.

### Wave 0 Gaps
- [ ] Extend `tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs` — 5→3 reflection fact (covers SC-2/RETIRE-03).
- [ ] Add scoped composite-backup-key-BUILDER source sweep (NOT bare `corr:wf:proc:exec` substring — Pitfall 2) — covers SC-2.
- [ ] Add the D-01 end-state standing guard (no `UseMessageRetry`/`ConfigureError` under exec-path source dirs) — new fact (here or a sibling `ExecutionPathEndStateFacts.cs`).
- [ ] Add the D-03 scoping fact (error filter reachable only under `src/Keeper/`).
- [ ] (Optional) live throw-spike reusing the keeper SustainedOutage harness — confirms A1.
- All reuse the existing `RepoRoot()` `[CallerFilePath]` anchor + `Directory.Exists` false-pass guard from `ReactivePathRetiredFacts`.

## Security Domain

> `security_enforcement` not explicitly disabled in config (treated enabled). This phase removes resilience wiring and adds tests; it touches no auth/crypto/input-validation surface.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | — |
| V3 Session Management | no | — |
| V4 Access Control | no | — |
| V5 Input Validation | no | message schema validation is processor-pipeline logic, unchanged this phase |
| V6 Cryptography | no | — |

### Known Threat Patterns for this stack
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Poison-message hot-spin / resource exhaustion (D-04 accepted unbounded requeue) | Denial of Service | DELIBERATELY accepted residual (A18, mirrors keeper SustainedOutage). Bound observability (metric on redelivery count) so a true poison send is detectable; not a code mitigation this phase. |
| Silent message loss on mis-removal (`DiscardFaultedMessages`) | Tampering / Repudiation | Anti-pattern explicitly avoided; the end-state is nack-requeue (redelivery), proven by the optional live spike (A1). |

## Sources

### Primary (HIGH confidence)
- Installed source (verified by direct read): `RecoveryEndpointBinder.cs` (SustainedOutage never-exhausting Interval trick + Dlq1 consolidated-filter rationale), `MessagingServiceCollectionExtensions.cs` (global `AddConfigureEndpointsCallback` + `skp-dlq-1` topology), `ConsolidatedErrorTransportFilter.cs` (exchange-send + `ConfigureError` API confirmed against 8.5.5), all 10 `*ConsumerDefinition.cs` (retry owner map), `ProcessorStartupOrchestrator.cs:172-182` (keep-latch), `ReactivePathRetiredFacts.cs` + `ModelBContractsRetiredFacts.cs` (hermetic-fact idiom).
- `Directory.Packages.props:133-138` — MassTransit 8.5.5 CPM pin (last Apache-2.0 line). NuGet cache listing confirms 8.5.5 installed.
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` §Global rules (141-144), §Keeper 3 states (205-221), §Invariants (223-227) — LOCKED A18 source of truth.
- `.planning/phases/36-l2-health-probe-recovery-loop-dlqs/36-RESEARCH.md:230-279` — prior VERIFIED research: MT 8.5.5 moves to `_error` via `ErrorTransportFilter`; omitting the error filters means nothing goes to `_error`; `ConfigureError` pipeline is `GenerateFaultFilter → ErrorTransportFilter`.

### Secondary (MEDIUM confidence)
- MassTransit docs (exceptions / RabbitMQ transport) — default `_error` move; `DiscardFaultedMessages` discards (does NOT redeliver). `[CITED: masstransit.massient.com/documentation/concepts/exceptions]`
- GitHub discussion #3513 — "configure the receive endpoint to throw errors back to the transport (Nack)" via `RethrowFaultedMessages()` + `ThrowOnSkippedMessages()`. `[CITED]`

### Tertiary (LOW confidence)
- Web search snippet on indefinite RabbitMQ redelivery when a message is nacked-not-acked — directionally consistent with A1 but the exact no-error-pipeline path should be confirmed by the optional Wave 0 throw-spike.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — versions verified in CPM + NuGet cache; no new deps.
- Architecture (teardown seams): HIGH — every touch point read in source; retry owner map + scoping seam confirmed.
- Redelivery mechanism (A1): MEDIUM — docs + discussion + in-repo SustainedOutage precedent all align, but "absence of error pipeline ⇒ nack-requeue" is best confirmed by a live throw-spike (optional Wave 0). The `WorkflowRootNotFoundException` disposition (OQ-1) is a genuine open design point the planner must resolve.
- Pitfalls / guards: HIGH — mirror an already-verified hermetic idiom.

**Research date:** 2026-06-11
**Valid until:** 2026-07-11 (stable — pinned MassTransit, no fast-moving deps)
