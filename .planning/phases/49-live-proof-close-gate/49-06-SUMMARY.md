---
phase: 49-live-proof-close-gate
plan: 06
subsystem: messaging-topology / processor-observability / e2e-test-query
tags: [gap-closure, masstransit-topology, dead-letter, prometheus-metric, elasticsearch-query]
requires:
  - GAP-49-1 406-fix (ConsolidatedErrorTransportFilter sends to exchange:skp-dlq-1)
  - ProcessorMetrics.ResultSent counter (defined, previously never incremented)
provides:
  - skp-dlq-1 as a PASSIVE parking queue (no consumer) so forwarded faults park observably
  - processor_result_sent_total{ProcessorId,outcome} series emitted on every confirmed send
  - SC3 OrchestratorSeamQuery aligned to the phrase-searchable body.text ES field
affects:
  - all consoles (BaseConsole.Core topology): baseapi-service, orchestrator, processor-sample, keeper
  - processor-sample (BaseProcessor.Core metric)
  - SC3PauseResumeOutageE2ETests (test-only query shape)
tech-stack:
  added: []
  patterns:
    - "MassTransit publish-topology BindQueue(exchange, queue, args) + DeployPublishTopology=true to declare a dead-letter queue+exchange WITHOUT a consuming ReceiveEndpoint"
    - "Counter.Add(1, ProcessorId, outcome) tag shape mirrored from EntryStepDispatchConsumer.DispatchConsumed"
key-files:
  created:
    - .planning/phases/49-live-proof-close-gate/49-06-SUMMARY.md
  modified:
    - src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs
    - tests/BaseApi.Tests/Processor/PipelinePreFacts.cs
    - tests/BaseApi.Tests/Processor/PipelineInFacts.cs
    - tests/BaseApi.Tests/Processor/PipelinePostFacts.cs
    - tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs
decisions:
  - "GAP-49-3 fix uses publish-topology BindQueue (shape b) over keeping a non-consuming ReceiveEndpoint (shape a) — a ReceiveEndpoint always consumes and routes unhandled messages to _skipped; only a topology declaration with no consumer lets faults PARK in skp-dlq-1"
  - "DeployPublishTopology=true is required because the filter SENDS (not Publishes) ConsolidatedFault — without eager deploy the exchange:skp-dlq-1 fanout would have no bound queue at the first send and the fault would be dropped"
  - "GAP-49-5 outcome tag mapped by IStepResult runtime type to lowercase enum-strings (completed/failed/cancelled/processing) to satisfy AssertBusinessLabels(expectOutcome:true)"
metrics:
  duration: ~5 min (code tasks; live close gate deferred to Task 4 operator gate)
  completed: 2026-06-09
  tasks_completed: 3 of 4 (Task 4 = checkpoint:human-verify, deferred)
  files_modified: 7
---

# Phase 49 Plan 06: GAP-49-3/4/5 Gap Closure (Round 2) Summary

Closed the three live-proof gaps from close-gate run #2 with two minimal production changes and one test-query alignment: `skp-dlq-1` is now a passive parking queue so exhausted faults park observably (GAP-49-3); `processor_result_sent_total` is now emitted on every confirmed send with a ProcessorId + outcome tag (GAP-49-5); SC3's Elasticsearch seam query now match_phrases the phrase-searchable `body.text` field instead of plain `body` (GAP-49-4). Tasks 1-3 are complete, committed, and build 0-warning in both Release and Debug. Task 4 (rebuild v4 stack + live N×GREEN close gate) is a `checkpoint:human-verify` deferred to the operator gate.

## Tasks Completed

| Task | Name | Commit | Files |
| ---- | ---- | ------ | ----- |
| 1 | GAP-49-3: skp-dlq-1 passive parking queue | `9999d0f` | MessagingServiceCollectionExtensions.cs |
| 2 | GAP-49-5: wire processor_result_sent_total | `e3bbaae` | ProcessorPipeline.cs |
| 3 | GAP-49-4: SC3 seam query → body.text (+ pipeline ctor harness fix) | `7464944` | SC3PauseResumeOutageE2ETests.cs, Pipeline{Pre,In,Post,EndDelete}Facts.cs |

## GAP-49-3 — passive-queue API chosen (and why)

**Chosen: shape (b) — publish-topology `BindQueue` + `DeployPublishTopology = true`.**

```csharp
c.DeployPublishTopology = true;
c.Publish<ConsolidatedFault>(p =>
{
    p.BindQueue(ConsolidatedErrorTransportFilter.Dlq1, ConsolidatedErrorTransportFilter.Dlq1, q =>
    {
        q.SetQueueArgument("x-message-ttl", (int)TimeSpan.FromDays(7).TotalMilliseconds);
    });
});
```

API confirmed against the MassTransit 8.5.5 assembly (`MassTransit.RabbitMqTransport.dll`, net8.0) via reflection:
- `IRabbitMqMessagePublishTopologyConfigurator<T>.BindQueue(string exchangeName, string queueName, Action<IRabbitMqQueueBindingConfigurator>)` exists; its XML doc says "Bind an exchange to a queue, both of which are declared if they do not exist. Useful for creating alternate/dead-letter exchanges and queues for messages."
- `IRabbitMqQueueBindingConfigurator : IRabbitMqQueueConfigurator`, which exposes `SetQueueArgument(string, object)` — so `x-message-ttl` is set on the declared queue.
- `IBusFactoryConfigurator.DeployPublishTopology` is a settable `bool` (inherited onto `IRabbitMqBusFactoryConfigurator`).

**Why over shape (a):** A `ReceiveEndpoint` is intrinsically a *consuming* endpoint — MassTransit binds a competing consumer that immediately dequeues every arriving message; with no registered consumer for the forwarded `ConsolidatedFault`, MassTransit moved it to `skp-dlq-1_skipped` (the live run #2 symptom: `_skipped` depth 3, `skp-dlq-1` depth 0). `DiscardSkippedMessages()` would *drop* the fault rather than park it (SC2 STATE 2 asserts depth increments), so shape (a) cannot satisfy the requirement. A topology `BindQueue` declares the exact `skp-dlq-1` fanout exchange → `skp-dlq-1` queue (with the 7-day ttl) binding with **no consumer**, so a message sent to `exchange:skp-dlq-1` PARKS in the queue.

**Why `DeployPublishTopology = true` is load-bearing:** the filter SENDS `ConsolidatedFault` to `exchange:skp-dlq-1` (it does not Publish it). Without eager deploy, the publish-topology binding is materialized lazily on first publish — which never happens — so the fanout exchange would have no bound queue at send time and the fault would be silently dropped. Eager deploy creates the exchange+queue+binding at bus start.

**GAP-49-1 406 fix preserved:** `ConsolidatedErrorTransportFilter.cs` is ZERO-diff (confirmed `git diff --name-only` returns nothing). It still sends to `Dlq1Uri = exchange:skp-dlq-1`. The queue's `x-message-ttl` is declared exactly once (`grep` of the actual `SetQueueArgument("x-message-ttl", ...)` call = 1); the filter never re-declares the queue with default args, so the 406 inequivalent-arg poison-loop stays closed.

## GAP-49-5 — tag shape (ProcessorId + outcome)

Injected `ProcessorMetrics metrics` into the `ProcessorPipeline` primary ctor (registered `AddSingleton`, resolves cleanly into the `AddScoped` pipeline). In `SendResult`, AFTER the confirmed-send guard (`if (!sent.Succeeded) throw sent.Error!;`):

```csharp
metrics.ResultSent.Add(1,
    new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")),
    new KeyValuePair<string, object?>("outcome", ResultOutcome(result)));
```

- `ProcessorId` uses `context.Id!.Value.ToString("D")` — identical to `EntryStepDispatchConsumer.cs:32` `DispatchConsumed.Add`.
- `outcome` is added because `MetricsRoundTripE2ETests.AssertBusinessLabels(resultSent, expectOutcome: true)` asserts a non-empty `outcome` label restricted to `{ completed, failed, cancelled }`. A private `ResultOutcome(IStepResult)` maps the concrete record by runtime type → `StepCompleted`→"completed", `StepFailed`→"failed", `StepCancelled`→"cancelled", `StepProcessing`→"processing" (the round trip emits "completed"; the others are stable for the non-asserted paths).
- Placed after the success guard so a propagated send-exhaustion (which throws before reaching the increment) never counts a non-sent result.

## GAP-49-4 — investigation finding (body vs body.text)

**Root cause confirmed (test query-shape defect, NOT a prod export gap):** SC3's `OrchestratorSeamQuery` filtered `{ "match_phrase": { "body": "{{seam}}" } }`. The otel collector maps the log message under the nested `body.text` object; a `match_phrase` on plain `body` matches nothing (total:0), which is why the 150s seam poll returned null even though the orchestrator emits + exports the seam. The proven precedents:
- `LogExportTests.cs:57` uses `{ "match_phrase": { "body.text": ... } }`.
- `CorrelationPropagationE2ETests.cs:126-129` documents: "we do NOT query the 'body' field — otel maps the message under the nested 'body.text' object, which is not phrase-searchable; assert text in C# via GetRawText()."
- `SampleRoundTripE2ETests.cs:151-167` (the live-passing orchestrator query) is term-only on the service name and asserts text in C#.

**Fix:** changed ONLY the match clause `"body"` → `"body.text"` in `OrchestratorSeamQuery`. The `term resource.attributes.service.name=orchestrator` clause and both `Assert.Contains(...GetRawText())` call sites are unchanged. No `src/Orchestrator` change (confirmed `git diff --name-only src/Orchestrator/` is empty). The secondary (BIT-cadence-aware wait) was NOT applied — the evidence is a query-shape miss, and `OutageSettleMs`=20s (4× the 5s probe) plus the 150s poll already straddle ingest latency; the `body.text` fix alone is expected to pass.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Pipeline ctor call sites updated for the new ProcessorMetrics parameter**
- **Found during:** Task 2 (surfaced when building the test project in Task 3).
- **Issue:** Adding `ProcessorMetrics metrics` to the `ProcessorPipeline` primary ctor broke the four hermetic Pattern-1 facts that construct the pipeline directly (`PipelinePreFacts`, `PipelineInFacts`, `PipelinePostFacts`, `PipelineEndDeleteFacts`) — CS7036 "no argument for parameter 'logger'".
- **Fix:** passed the existing `DispatchTestKit.Metrics()` helper (a real `ProcessorMetrics` built from a live `IMeterFactory`, no-op in-test) at each construction site, before the logger argument.
- **Files modified:** the four `Pipeline*Facts.cs` files.
- **Commit:** `7464944` (committed alongside Task 3, the test-harness changeset).

## Build Evidence

| Project | Release | Debug |
| ------- | ------- | ----- |
| BaseConsole.Core | 0 Warning / 0 Error | 0 Warning / 0 Error |
| BaseProcessor.Core | 0 Warning / 0 Error | 0 Warning / 0 Error |
| BaseApi.Tests | 0 Warning / 0 Error | 0 Warning / 0 Error |

Hermetic sanity: `dotnet run --project tests/BaseApi.Tests -c Release --no-build -- --filter-method "*StatusException*" --filter-method "*MultiItem*"` → 4/4 passed (the touched pipeline facts still green).

## Task 4 — Operator Gate Handoff (deferred)

Task 4 is a `checkpoint:human-verify` (gate="blocking"): rebuild the v4 stack so the GAP-49-3 (BaseConsole.Core) + GAP-49-5 (BaseProcessor.Core) code is live, then run `scripts/phase-49-close.ps1` for the live N×GREEN close gate. Per D-03 this is operator-gated and was NOT executed by the executor (no docker rebuild, no close-gate run). The orchestrator handles the rebuild + live gate run.

Live acceptance to record on the GREEN run (49-HUMAN-UAT.md, then tick TEST-01/02/03):
- Both build configs 0-warning; 3 runs GREEN with identical Passed fact count (expect SC2 STATE 2, SC3, MetricsRoundTrip now pass).
- Triple-SHA psql/redis/rabbitmq BEFORE==AFTER; `skp-dlq-1` depth==0 (SC2 teardown purges the one parked fault).

## Known Stubs

None. No placeholder/empty-value stubs introduced.

## Self-Check: PASSED

- Files: 49-06-SUMMARY.md, MessagingServiceCollectionExtensions.cs, ProcessorPipeline.cs, SC3PauseResumeOutageE2ETests.cs — all FOUND.
- Commits: 9999d0f, e3bbaae, 7464944 — all FOUND in git log.
