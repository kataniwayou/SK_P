# Phase 53: Model-B Teardown - Pattern Map

**Mapped:** 2026-06-11
**Files analyzed:** 11 modify-targets + 1 new/extended test file
**Analogs found:** 11 / 11 (this is a teardown phase — most "analogs" are the sibling already in the target end-state, plus two test-idiom analogs to mirror)

> **Map style note:** This is a code-to-doc TEARDOWN phase. "Match quality" below means *how directly the planner can paste the excerpt into an `<action>`*. For removal targets the "analog" is usually the **already-correct sibling** (a no-op definition that registers nothing) or the **doc-comment that already names the end-state**. For the two NEW test facts the analogs are real source-to-mirror.

---

## Critical pre-planning findings (verified against live source — supersede RESEARCH assumptions)

1. **`WorkflowRootNotFoundException` is NEVER thrown anywhere in the repo and NEVER asserted in any test.**
   - `Grep("throw new WorkflowRootNotFoundException")` → **0 matches** (whole repo).
   - `Grep` in `tests/` → **0 matches**.
   - The only producer path, `WorkflowLifecycle.HydrateAndScheduleAsync` (`src/Orchestrator/Hydration/WorkflowLifecycle.cs:42-47`), treats an absent L2 root as a **logged business no-op (`return;`), NOT a throw**. Same for Stop (`UnscheduleOnlyAsync` 150-159 — absent-from-L1 is `return;`).
   - **Implication for D-07 / RESEARCH OQ-1:** the `Ignore<WorkflowRootNotFoundException>()` in Start/Stop definitions guards a throw that **does not exist**. RESEARCH A3 / OQ-1 assumed this exception "would redeliver indefinitely after retry removal — a regression." That regression **cannot occur** because the exception never escapes `Consume`. The planner must surface this: the D-07 explicit-catch-and-DLQ seam, **as literally specified, has no live trigger today.** Two coherent dispositions for the planner to choose (CONTEXT D-07 says "planner finalizes the seam"):
     - (a) **Document-only carve-out:** record that Start/Stop *would* dead-letter `WorkflowRootNotFoundException` to `skp-dlq-1` if/when a future change makes `WorkflowLifecycle` throw it, and add the explicit catch as a forward-looking guard (the catch is dead today but encodes D-07 intent). The Phase-54 close-gate "two-producer" baseline (keeper Dlq1 + Start/Stop) then reflects *potential*, not *observed*, traffic.
     - (b) **Make the throw real:** change `WorkflowLifecycle` absent-root from `return;` to `throw new WorkflowRootNotFoundException(workflowId)` AND add the explicit catch→DLQ in the consumer. This is **net-additive behavior change** and arguably scope creep against CONTEXT's "code-to-doc alignment, NOT new behavior" framing — flag to the user before adopting.
   - **Either way:** the `Ignore<WorkflowRootNotFoundException>()` lines (`StartOrchestrationConsumerDefinition.cs:41`, `StopOrchestrationConsumerDefinition.cs:36`) die with the `UseMessageRetry` block they live inside (RESEARCH Pattern 1 / A3 — `Ignore` only has meaning with retry). Removing them is mechanically required regardless of which disposition is chosen.

2. **The in-repo explicit-DLQ-send precedent exists** and is the exact idiom for the D-07 catch-block: `ConsolidatedErrorTransportFilter.Send` (`src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs:61-90`) — `GetSendEndpoint(new Uri("exchange:skp-dlq-1"))` then `endpoint.Send(envelope, headerCopy, ct)`. A consumer-side catch would use `context.GetSendEndpoint(ConsolidatedErrorTransportFilter.Dlq1Uri-equivalent)`. The `Dlq1` const (`"skp-dlq-1"`) is `public` at line 49; `Dlq1Uri` is `private` (line 59) — the planner must either expose `Dlq1Uri` or rebuild `new Uri("exchange:skp-dlq-1")` in the consumer.

3. **Keeper reflection-fact viability (RESEARCH A2 — CONFIRMED safe):** `RecoveryConsumerBase<TMessage> : IConsumer<TMessage>` (`src/Keeper/Recovery/RecoveryConsumerBase.cs:26`); `ReinjectConsumer : RecoveryConsumerBase<KeeperReinject>` (etc.). `Type.GetInterfaces()` **reports inherited interfaces with the closed generic arg** (`IConsumer<KeeperReinject>`), so the RESEARCH 5→3 reflection LINQ (`SelectMany(GetInterfaces()).Where(IConsumer<>)`) works **without** walking base types. A2 risk retired.

4. **`corr:wf:proc:exec` source-sweep (RESEARCH Pitfall 2 — CONFIRMED):** the substring survives **legitimately** as a partition-marker doc-comment; FACT 1 (`L2ProjectionKeys_has_no_CompositeBackup_builder`, already present) is the correct reflection guard. **Do NOT add a bare-substring scan.** Extend FACT 1's reflection approach, not a string scan.

---

## File Classification

| Target file | Role (this phase) | Data Flow | Closest Analog | Match Quality |
|-------------|-------------------|-----------|----------------|---------------|
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:172-182` | remove-wiring (bus keep-latch) | request-response (dispatch endpoint bind) | `RecoveryEndpointBinder.cs:80-108` (sibling connect-callback, post-removal shape) | role-match |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:55-58, 311, 318, 344` | doc-reconcile (`→ _error` comments) | comment-only | the comment at :57 already names the A18 end-state as the target | exact (self-describing) |
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:22` | doc-reconcile (`→ _error` comment) | comment-only | same as above | exact |
| `src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs:38-42` | remove-wiring + del-ctor-dep | request-response | `StepFailedConsumerDefinition.cs` (no-op sibling: zero retry, no `IOptions`) | exact (target = no-op shape) |
| `src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs:33-37` | remove-wiring + del-ctor-dep | request-response | `StepFailedConsumerDefinition.cs` | exact |
| `src/Orchestrator/Consumers/StepCompletedConsumerDefinition.cs:40` | remove-wiring + del-ctor-dep | request-response (result) | `StepFailedConsumerDefinition.cs` (its own no-op sibling) | exact |
| `src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs:38` | remove-wiring + del-ctor-dep (KEEP `ConcurrentMessageLimit=1`) | request-response (control) | `ResumeWorkflowConsumerDefinition.cs` (no-op sibling: keeps `ConcurrentMessageLimit=1`, no `IOptions`) | exact |
| `src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs:37` | remove-wiring + del-ctor-dep (KEEP `ConcurrentMessageLimit=1`) | request-response (control) | `ResumeAllConsumerDefinition.cs` (no-op sibling) | exact |
| `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:56-63` | remove-wiring (global ConfigureError callback); KEEP topology 73-108 | config / topology | — (deletion; no analog) | n/a |
| `src/Keeper/Recovery/RecoveryEndpointBinder.cs:80-108` | scope-wiring (ADD moved `ConfigureError` pair inside connect-callback) | event-driven (recovery) | `MessagingServiceCollectionExtensions.cs:58-62` (the exact filter pair being moved) | exact (verbatim move) |
| `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs:28-40` + `StopOrchestrationConsumer.cs:27-37` | add-guard (D-07 explicit-catch seam — see Finding 1) | request-response | `ConsolidatedErrorTransportFilter.cs:61-90` (DLQ explicit-send idiom) | role-match |
| `tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs` (extend) | add-guard-test | reflection + source-scan | self (FACT 1) + `ReactivePathRetiredFacts.cs` (source-scan idiom) | exact |

---

## Pattern Assignments

### `ProcessorStartupOrchestrator.cs` (remove-wiring, processor keep-latch)

**`<read_first>`:** `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:170-189`

**Exact current code to modify** (lines 172-182):
```csharp
var handle = endpointConnector.ConnectReceiveEndpoint(queueName, (ctx, cfg) =>
{
    // D-09 reconcile (Phase 44, Pitfall 1): ... [comment block 174-179] ...
    cfg.UseMessageRetry(r => r.Immediate(retryLimit));        // outer dead-letter latch (D-09/D-10); D-10 config-bound Limit  ◄── REMOVE
    cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx);    // DI-resolved consumer attached
});
```

**Action:** delete the `cfg.UseMessageRetry(...)` line + reconcile the 174-179 comment block to A18 (no bus latch; throw → broker redelivery). **KEEP** `var retryLimit = retryOptions.Value.Limit;` (line 171) ONLY if still read elsewhere in this method — **VERIFIED it is not used after the latch removal**, so line 171 + the `retryOptions` ctor dependency become dead → remove to stay 0-warning (SC-3). **Caution:** confirm `retryOptions` is not used elsewhere in the class before deleting the ctor param (it is injected only for this latch — `Grep` the file).

**Analog (post-removal shape)** — the sibling connect-callback with no bus latch is `RecoveryEndpointBinder.cs:80-108`'s `ConnectReceiveEndpoint(...)` (it has retry *by policy*, but the bare `cfg.ConfigureConsumer<...>(ctx)` tail is the A18-clean target shape).

---

### `ProcessorPipeline.cs` + `EntryStepDispatchConsumer.cs` (doc-reconcile, `→ _error`)

**`<read_first>`:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:54-58, 311, 318, 344`; `EntryStepDispatchConsumer.cs:20-24`

**Exact current comments to reconcile** (the throw stays; only the `→ _error` narrative changes to `→ broker redelivery`):
```csharp
// ProcessorPipeline.cs:55-58 (xml-doc <para> Resilience)
/// <see cref="RetryLoop"/> using <c>Retry:Limit</c>; a send that exhausts PROPAGATES (→ the bus
/// <c>UseMessageRetry</c> dead-letter latch → <c>_error</c>; the A18 <c>UseMessageRetry=none</c> end-state is
/// a Phase-53 teardown item). The in-code retry owns per-op retries; the bus retry is the OUTER latch.

// :311  (banner comment)
// ---- Send owners: every send wrapped in RetryLoop; send-exhaustion PROPAGATES (D-10 → bus _error). ----

// :318
if (!sent.Succeeded) throw sent.Error!;   // D-10: propagate → UseMessageRetry → _error

// :344
if (!sent.Succeeded) throw sent.Error!;   // D-10: propagate → _error
```
```csharp
// EntryStepDispatchConsumer.cs:21-23 (xml-doc)
/// <c>Step*</c> result and ack; infra outcomes emit the matching Keeper-state message; only a send-exhaustion
/// PROPAGATES (→ the runtime <c>UseMessageRetry</c> dead-letter latch → <c>_error</c>, D-10). The consumer
```

**Action:** comment-only. Rewrite each `→ UseMessageRetry → _error` / `bus _error` narrative to the A18 end-state: *send-exhaustion throws → no bus retry, no error pipeline → RabbitMQ nack-requeue (broker redelivery), no dead-letter (Phase-53 D-01)*. **The `throw sent.Error!` lines (318, 344) themselves are UNCHANGED** — only their trailing `// → _error` comment changes. No code logic moves.

---

### Orchestrator `*ConsumerDefinition.cs` (remove-wiring — the single-owner removal map)

**The complete verified owner map (file:line of each `UseMessageRetry` to strip):**

| Endpoint (`EndpointName`) | Definition + line | Strip | Also strip (dead with retry) | Ctor `IOptions<RetryOptions>` |
|---------------------------|-------------------|-------|------------------------------|-------------------------------|
| `orchestrator` (Start+Stop SHARE) | `StartOrchestrationConsumerDefinition.cs:38-42` | full `UseMessageRetry(r => {...})` block | `r.Ignore<WorkflowRootNotFoundException>()` (:41) | remove (`:22,:24-27`) |
| `orchestrator` (Start+Stop SHARE) | `StopOrchestrationConsumerDefinition.cs:33-37` | full `UseMessageRetry(r => {...})` block | `r.Ignore<WorkflowRootNotFoundException>()` (:36) | remove (`:17,:19-23`) |
| `orchestrator-result` | `StepCompletedConsumerDefinition.cs:40` | `UseMessageRetry(r => r.Immediate(...))` | — | remove (`:23,:25-29`) |
| `orchestrator-pauseresume` | `PauseWorkflowConsumerDefinition.cs:38` | `UseMessageRetry(...)` ONLY | — KEEP `ConcurrentMessageLimit=1` (:35) | remove (`:22,:24-28`) |
| `orchestrator-global-pauseresume` | `PauseAllConsumerDefinition.cs:37` | `UseMessageRetry(...)` ONLY | — KEEP `ConcurrentMessageLimit=1` (:35) | remove (`:24,:24-28`) |

> ⚠️ **DUAL-OWNER (RESEARCH Pitfall 3 — VERIFIED):** the `orchestrator` endpoint is shared by Start AND Stop and **BOTH** definitions call `UseMessageRetry` (`StartOrchestrationConsumerDefinition.cs:38` + `StopOrchestrationConsumerDefinition.cs:33`). This is NOT the documented single-owner pattern. **Strip from BOTH.** The D-06 standing guard catches a missed one.

**No-op siblings (LEAVE UNCHANGED — they already register zero retry; this is the post-removal target shape):**
- `StepFailedConsumerDefinition.cs` / `StepCancelledConsumerDefinition.cs` / `StepProcessingConsumerDefinition.cs` — parameterless ctor, no `ConfigureConsumer` body or an intentional no-op.
- `ResumeWorkflowConsumerDefinition.cs` — keeps `ConcurrentMessageLimit=1` only, no `IOptions`.
- `ResumeAllConsumerDefinition.cs` — keeps `ConcurrentMessageLimit=1` only.

**Exact removal excerpt** (Start — Stop is identical):
```csharp
// BEFORE — StartOrchestrationConsumerDefinition.cs:38-42
endpointConfigurator.UseMessageRetry(r =>
{
    r.Immediate(_retryOptions.Value.Limit);
    r.Ignore<WorkflowRootNotFoundException>();   // business failure never retries (D-07/D-08, MSG-ACK-03)
});
// AFTER — ConfigureConsumer body becomes empty (or the override is deleted entirely).
// Also delete: field `_retryOptions` (:22), its ctor param + assignment (:24-26),
// and the `using Microsoft.Extensions.Options;` if now unused → 0-warning (SC-3, RESEARCH Pitfall 4).
```

**Analog (post-removal target shape):** `src/Orchestrator/Consumers/StepFailedConsumerDefinition.cs` (full file, 17-25) — parameterless ctor, sets only `EndpointName`, no retry, no `IOptions`. The Start/Stop/StepCompleted definitions should collapse to this shape (minus the dual EndpointName for Start/Stop).

---

### `MessagingServiceCollectionExtensions.cs` (remove-wiring — global ConfigureError callback)

**`<read_first>`:** `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:46-119`

**Exact current code to DELETE** (lines 56-63):
```csharp
x.AddConfigureEndpointsCallback((context, name, e) =>
{
    e.ConfigureError(ep =>
    {
        ep.UseFilter(new GenerateFaultFilter());                  // keep Fault<T> publication (Keeper rides it)
        ep.UseFilter(new ConsolidatedErrorTransportFilter());     // move exhausted msg → skp-dlq-1 (replaces {queue}_error)
    });
});
```

**KEEP UNCHANGED (RESEARCH Pitfall 5 — do NOT over-delete):** the `skp-dlq-1` topology declaration at lines 98-108 (`c.DeployPublishTopology = true;` + `c.Publish<ConsolidatedFault>(p => p.BindQueue(...x-message-ttl...))`). The keeper still sends into this exchange; the queue must exist. Only the per-endpoint *application* (56-63) moves to the keeper.

**Side-effect to verify:** after deleting 56-63, the `using` for `GenerateFaultFilter`/`ConsolidatedErrorTransportFilter` (`using BaseConsole.Core.Messaging;` :2, `using MassTransit.Middleware;` :4) may go unused *here* but the filters move to `RecoveryEndpointBinder` — confirm 0-warning across both files.

---

### `RecoveryEndpointBinder.cs` (scope-wiring — ADD the moved ConfigureError pair, D-03)

**`<read_first>`:** `src/Keeper/Recovery/RecoveryEndpointBinder.cs:80-108`

**Exact insertion point** — inside the existing `connector.ConnectReceiveEndpoint(KeeperQueues.Recovery, (ctx, cfg) => { ... })` callback (the callback ALREADY owns the policy-conditional retry at lines 86-99). Add the filter pair adjacent to that retry (RESEARCH Pattern 3 — keeper-local apply):
```csharp
// ADD inside the connect-callback (moved VERBATIM from BaseConsole.Core:58-62), e.g. after the
// policy retry block (line 99) and before the partitioners (line 101):
cfg.ConfigureError(ep =>
{
    ep.UseFilter(new GenerateFaultFilter());                  // Fault<T> publication — keeper-only post-Phase-53
    ep.UseFilter(new ConsolidatedErrorTransportFilter());     // exhausted → skp-dlq-1 (Dlq1 mode)
});
```
**`using` to add to `RecoveryEndpointBinder.cs`:** `using BaseConsole.Core.Messaging;` (for `GenerateFaultFilter` + `ConsolidatedErrorTransportFilter`). It already has `using MassTransit;` + `using MassTransit.Middleware;`.

**Existing context the filter sits next to** (UNCHANGED, lines 86-99): the `if (policy == ExhaustionPolicy.SustainedOutage) cfg.UseMessageRetry(r => r.Interval(...)); else cfg.UseMessageRetry(r => r.Immediate(retryLimit));`. The binder's xml-doc (lines 26-35) already describes "exhaust → consolidated filter → skp-dlq-1" — so the filter pair *documentationally already belongs here*; this change makes the doc literally true keeper-local.

---

### D-07 carve-out seam — `StartOrchestrationConsumer.cs` / `StopOrchestrationConsumer.cs`

**`<read_first>`:** `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs:28-41`, `StopOrchestrationConsumer.cs:27-38`, `WorkflowRootNotFoundException.cs` (full), `WorkflowLifecycle.cs:37-47,150-159`, and the DLQ-send idiom `ConsolidatedErrorTransportFilter.cs:61-90`.

**SEE CRITICAL FINDING 1 — the seam has no live trigger today.** Current `Consume` shape (Start; Stop is structurally identical, `UnscheduleOnlyAsync` instead of teardown+hydrate):
```csharp
public async Task Consume(ConsumeContext<StartOrchestration> context)
{
    foreach (var workflowId in context.Message.WorkflowIds)
    {
        logger.LogInformation("Start reload for WorkflowId={WorkflowId}", workflowId);
        await lifecycle.TeardownAsync(workflowId, context.CancellationToken);
        await lifecycle.HydrateAndScheduleAsync(workflowId, context.CancellationToken);
    }
    // returns normally -> ACK
}
```
**Insertion point if the planner adopts the explicit catch (D-07 option (a) or (b)):** wrap the loop body (or the whole loop) in `try { ... } catch (WorkflowRootNotFoundException ex) { <send to skp-dlq-1> ; <ack by returning/swallowing> }`.

**In-repo DLQ-send idiom to mirror** (`ConsolidatedErrorTransportFilter.cs:61-90`, adapted to a consumer):
```csharp
// the const is public: ConsolidatedErrorTransportFilter.Dlq1 == "skp-dlq-1"
var dlq = await context.GetSendEndpoint(new Uri($"exchange:{ConsolidatedErrorTransportFilter.Dlq1}"));
await dlq.Send(/* forensic envelope or the original message */, context.CancellationToken);
// then let Consume return normally → ACK (no spin, no _error)
```
> **Note for planner:** `ConsolidatedErrorTransportFilter.Dlq1Uri` (the `new Uri("exchange:skp-dlq-1")`) is **`private`** (`:59`). Either rebuild the Uri inline (above) or promote `Dlq1Uri` to `public`. The `exchange:` scheme (NOT `queue:`) is load-bearing — a `queue:skp-dlq-1` send triggers the RabbitMQ 406 ttl-inequivalence poison-loop documented at `ConsolidatedErrorTransportFilter.cs:51-58`.

**Sibling control-plane business exceptions to enumerate (D-07 mandate):** the planner MUST confirm no OTHER business exception silently regresses when retry is stripped from Start/Stop. Verified: `WorkflowLifecycle` raises **no** business exceptions — every business outcome is a logged `return;` (absent root :44-46, malformed JSON :55-59, no-cron :61-66, absent step :81-86, absent-from-L1 :133-137/152-156/165-169/179-183). The ONLY infra throws are Redis faults (`IsInfra` :205-206), which under A18 correctly nack-requeue. So **`WorkflowRootNotFoundException` is the sole named carve-out exception, and it is currently unreachable** — confirming Finding 1.

---

### NEW test facts — `ModelBContractsRetiredFacts.cs` (extend, D-06)

**`<read_first>`:** `tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs` (full, 1-97) + `ReactivePathRetiredFacts.cs:79-103,149-161` (source-scan + `RepoRoot()` idiom).

**Existing assets in `ModelBContractsRetiredFacts.cs` to BUILD ON:**
- Assembly anchors (lines 27-31): `Contracts = typeof(KeeperInject).Assembly`, `Keeper = typeof(BitHealthLoop).Assembly`. **Add** an `Orchestrator` + `BaseProcessorCore` anchor for the D-01 end-state guard (copy from `ReactivePathRetiredFacts.cs:35-39`).
- FACT 1 (`L2ProjectionKeys_has_no_CompositeBackup_builder`, :38-47) — **this is the correct composite-backup guard; do NOT add a `corr:wf:proc:exec` substring scan** (Finding 4).
- `AssertSingleGuidOverload` helper (:86-96) — reusable.

**Mirror-source idioms from `ReactivePathRetiredFacts.cs` (copy verbatim):**
- `RepoRoot()` `[CallerFilePath]` anchor (:149-161) — walks up to `SK_P.sln`. **Copy into `ModelBContractsRetiredFacts` (it doesn't have one yet)** — required for any source-scan fact.
- The `Directory.Exists(dir)` **false-pass guard** (:86) — `Assert.True(Directory.Exists(dir), $"bad anchor: {dir}")` before any `EnumerateFiles`.
- The `*.cs` source-scan loop (:88-97) — `Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => File.ReadAllText(f).Contains(...))`. **Scope to `src/` only — NEVER `bin/`/`obj/`** (Runtime State Inventory: stale xml in `bin/` would false-positive).

**Four new facts to add (D-06 / Wave-0 gaps):**

1. **5→3 reflection** (RESEARCH Code Examples; A2 CONFIRMED safe via inherited interface):
```csharp
// reuse the existing `Keeper` assembly anchor (:30-31)
var consumed = Keeper.GetTypes()
    .SelectMany(t => t.GetInterfaces())
    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
    .Select(i => i.GetGenericArguments()[0].Name)
    .Where(n => n is "KeeperReinject" or "KeeperInject" or "KeeperDelete" or "KeeperUpdate" or "KeeperCleanup")
    .Distinct().OrderBy(n => n).ToArray();
Assert.Equal(new[] { "KeeperDelete", "KeeperInject", "KeeperReinject" }, consumed);
```
   > Verified anchor: `ReinjectConsumer : RecoveryConsumerBase<KeeperReinject>` and `RecoveryConsumerBase<TMessage> : IConsumer<TMessage>` — the closed `IConsumer<KeeperReinject>` IS reported by `GetInterfaces()`. Needs `using MassTransit;`.

2. **D-01 end-state standing guard** (source-scan, mirror `ReactivePathRetiredFacts` :79-103):
```csharp
foreach (var rel in new[] { Path.Combine("src","Orchestrator","Consumers"),
                            Path.Combine("src","BaseProcessor.Core","Startup") })
{
    var dir = Path.Combine(RepoRoot(), rel);
    Assert.True(Directory.Exists(dir), $"bad anchor: {dir}");
    var offenders = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
        .Where(f => { var t = File.ReadAllText(f); return t.Contains("UseMessageRetry") || t.Contains("ConfigureError"); })
        .ToList();
    Assert.True(offenders.Count == 0, "A18 end-state regressed: " + string.Join(", ", offenders));
}
```
   > **Caveat for planner:** a raw `.Contains("UseMessageRetry")` on `src/Orchestrator/Consumers` will trip on the **doc-comment text** in the no-op sibling definitions (e.g. `ResumeAllConsumerDefinition.cs:10` "does NOT register a second `UseMessageRetry`"). Decide: (i) strip those doc-comment mentions as part of the teardown, OR (ii) scan for the *call* pattern `endpointConfigurator.UseMessageRetry(` / `cfg.UseMessageRetry(` rather than the bare word. Option (ii) is more robust. **This is a real false-positive risk verified in source** (`Grep` shows the word in 6 comment lines that survive).

3. **D-03 scoping fact** (`ConfigureError`/`ConsolidatedErrorTransportFilter` reachable ONLY under `src/Keeper/`; absent from `BaseConsole.Core`'s global callback):
```csharp
// positive: present under src/Keeper ; negative: NOT a global callback in BaseConsole.Core's MessagingServiceCollectionExtensions
// scan src/BaseConsole.Core for `AddConfigureEndpointsCallback` + `ConfigureError` co-occurrence → assert absent.
```
   > Anchor the negative on the *file* `MessagingServiceCollectionExtensions.cs` not containing `ConfigureError`, and the positive on `RecoveryEndpointBinder.cs` containing it.

4. **D-07 disposition fact** (only if a seam lands): assert `StartOrchestrationConsumer.cs`/`StopOrchestrationConsumer.cs` reference `skp-dlq-1`/`Dlq1` (option (a)/(b)), OR — if document-only — assert the `Ignore<WorkflowRootNotFoundException>` is gone from both definitions. Planner picks based on Finding 1 disposition.

---

## Shared Patterns

### Single-owner `UseMessageRetry` per shared endpoint (the removal invariant)
**Source of truth:** `StepCompletedConsumerDefinition.cs:14-19` (owner doc) + `StepFailedConsumerDefinition.cs:11-15` (no-op sibling doc).
**Apply to:** every orchestrator definition removal. The no-op siblings already register nothing — leave them; only the owner's `UseMessageRetry` line is removed. **Exception:** `orchestrator` endpoint has TWO owners (Start+Stop) — strip both.

### `IOptions<RetryOptions>` is retry-only on the orchestrator (dead after removal)
**Source:** every orchestrator owner definition's ctor (`StartOrchestrationConsumerDefinition.cs:22-28`, etc.).
**Apply to:** Start/Stop/StepCompleted/PauseWorkflow/PauseAll definitions — remove the field + ctor param to stay 0-warning (SC-3). **Counter-example (KEEP):** `ProcessorPipeline` ctor `IOptions<RetryOptions>` stays — its in-code `RetryLoop` reads `Retry:Limit` (`ProcessorPipeline.cs:55-58`, 16 `RetryLoop.ExecuteAsync` call sites). The processor *startup orchestrator*'s `retryOptions` injection (used only for the deleted latch) DOES go.

### `exchange:`-addressed DLQ send (never `queue:`)
**Source:** `ConsolidatedErrorTransportFilter.cs:51-59` (the RabbitMQ 406 ttl-inequivalence rationale) + the `Send` impl :61-90.
**Apply to:** the D-07 explicit-catch seam IF adopted, AND the keeper-local `ConsolidatedErrorTransportFilter` (unchanged behavior, now keeper-scoped).

### Hermetic negative-guard idiom (reflection + `src/`-scoped source-scan, no host boot)
**Source:** `ReactivePathRetiredFacts.cs` (whole file) + `ModelBContractsRetiredFacts.cs` (whole file).
**Apply to:** all four new D-06 facts. Reuse `RepoRoot()`/`Directory.Exists` false-pass guard; scope `*.cs` under `src/` only; prefer reflection over substring where a legitimate doc-comment survivor exists (Findings 1, 4).

---

## No Analog Found

| File | Role | Reason |
|------|------|--------|
| `MessagingServiceCollectionExtensions.cs:56-63` deletion | remove global callback | Pure deletion of a unique global seam — no analog needed; the *destination* (RecoveryEndpointBinder) is the analog for where it lands. |
| D-07 explicit catch→DLQ in a *consumer* | add-guard | No existing consumer catches a business exception and sends to DLQ today (the only DLQ producer is the `IFilter` `ConsolidatedErrorTransportFilter`, not a consumer). The filter is the closest idiom but lives in the error-transport pipeline, not `Consume`. Planner adapts; see Finding 1 caveats. |

---

## Metadata

**Analog search scope:** `src/BaseProcessor.Core/{Startup,Processing}`, `src/Orchestrator/Consumers` + `/Hydration`, `src/BaseConsole.Core/{DependencyInjection,Messaging}`, `src/Keeper/Recovery`, `tests/BaseApi.Tests/Resilience`.
**Files scanned (read):** 18 source/test files + 4 targeted greps.
**Verification highlights:** dual-owner retry on `orchestrator` (CONFIRMED); `WorkflowRootNotFoundException` never thrown/tested (CONFIRMED — supersedes RESEARCH OQ-1/A3); keeper `IConsumer<T>` via base IS reflection-visible (A2 retired); `corr:wf:proc:exec` legitimate doc-comment survivor (Pitfall 2 CONFIRMED); `UseMessageRetry` survives in 6 doc-comment lines under `src/Orchestrator/Consumers` (new false-positive risk for the D-01 source-scan guard — flagged).
**Pattern extraction date:** 2026-06-11

## PATTERN MAPPING COMPLETE
