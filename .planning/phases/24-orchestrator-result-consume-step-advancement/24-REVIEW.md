---
phase: 24
status: issues_found
depth: standard
files_reviewed: 18
findings: 3
critical: 0
high: 1
medium: 1
low: 1
---

# Phase 24: Code Review Report

**Reviewed:** 2026-06-01
**Depth:** standard (per-file, C#/.NET-specific checks)
**Files Reviewed:** 18
**Status:** issues_found (1 high, 1 medium, 1 low)

## Summary

Reviewed the 18 source files changed in Phase 24 (orchestrator result-consume & step advancement).
The overall architecture is sound: the gate-closed never-drop redesign, conditionless Start/Stop consumers,
WebApi first-win suppression, and L1-only result path are all implemented correctly. The
business-ack/infra-throw split is consistent across all three consumers, the `UseScheduledRedelivery`
outer / `UseMessageRetry` inner ordering is correct on all three definitions, and `GateClosedException`
is correctly not `Ignore<>`-listed. The SPEC requirements are faithfully implemented.

Three findings are raised, in decreasing severity:

- **High (WR-01):** The `UseScheduledRedelivery` / `UseDelayedMessageScheduler` wiring depends on the
  `rabbitmq_delayed_message_exchange` broker plugin, which is absent from the compose stack. Without it
  the gate-closed never-drop invariant (ORCH-GATE-01 / SPEC req 6) silently degrades to exhaust-and-error
  in the compose environment. This is a known FLAG (FLAG-24-04-SCHEDULER) but must be resolved before
  any production or shared-environment deployment.
- **Medium (WR-02):** `StepAdvancement.SelectNext` iterates `completed.NextStepIds` directly without a
  null guard. The writer always coalesces to an empty list, so this cannot happen on a normally-written
  projection — but a `"nextStepIds": null` in a malformed or hand-crafted projection would cause a
  `NullReferenceException` that propagates as an infra fault, routing to `_error` instead of the
  business-ack skip the spec requires for corrupt projections.
- **Low (IN-01):** `BaseConsoleBusFactory.cs` is listed in the files-to-review but does not exist on
  disk. The seam it was expected to contain was folded into `MessagingServiceCollectionExtensions.cs`
  instead. No action required — purely an artefact of the review request.

---

## High

### WR-01: Gate-closed redelivery is a no-op without the RabbitMQ delayed-exchange plugin

**File:** `src/Orchestrator/Program.cs:48-52`
(also `src/Orchestrator/Consumers/ResultConsumerDefinition.cs:32-37`,
`src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs:31-36`,
`src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs:23-28`)

**Issue:**
`UseScheduledRedelivery` on RabbitMQ is backed by `AddDelayedMessageScheduler` /
`UseDelayedMessageScheduler`, which internally uses the `rabbitmq_delayed_message_exchange` plugin
to hold messages until the redelivery interval elapses. The compose broker
(`rabbitmq:4.1.8-management-alpine`) does **not** enable that plugin.

Without the plugin, MassTransit's delayed scheduler falls back to immediate delivery, which means a
`GateClosedException` thrown before `MarkReady` will exhaust the `Immediate(3)` inner retry in
milliseconds and route the message to `_error` — the exact message-loss scenario ORCH-GATE-01 was
designed to prevent. Unit and harness tests pass because they exercise the consumer's throw directly,
not the live broker pipeline; the gap is invisible in CI.

**Why it matters:**
SPEC req 6 ("gate-closed never-drop") is the safety net for processor results (one-time events) and
Start/Stop control messages arriving during the hydration window. A silent no-op on this invariant
means dropped messages in any compose/staging environment that shares the same broker image, with no
visible error beyond `_error` queue accumulation.

**Suggested fix (choose one):**

Option A — Enable the plugin in compose (additive, recommended for local dev/staging):
```yaml
# compose.yaml — rabbitmq service
rabbitmq:
  image: rabbitmq:4.1.8-management-alpine
  # Add a custom entrypoint or rabbitmq.conf/enabled_plugins file:
  volumes:
    - ./compose/rabbitmq/enabled_plugins:/etc/rabbitmq/enabled_plugins:ro
```
`compose/rabbitmq/enabled_plugins`:
```
[rabbitmq_management,rabbitmq_delayed_message_exchange].
```

Option B — Back the scheduler with the existing Quartz instance (no broker plugin required;
adds `MassTransit.Quartz` package):
```csharp
// Program.cs — replace AddDelayedMessageScheduler with:
x.AddQuartzConsumers();   // MassTransit.Quartz — reuses the already-registered IScheduler
// configureBus callback:
configureBus: (ctx, c) => c.UseInMemoryOutbox(ctx)   // or c.UseMessageScheduler(...)
```

Either fix must be in place before the gate-closed behaviour is testable end-to-end. The FLAG is already
documented in the SUMMARY; this finding elevates it to a blocking review item for deployment readiness.

---

## Medium

### WR-02: `StepAdvancement.SelectNext` lacks a null guard on `completed.NextStepIds`

**File:** `src/Orchestrator/Dispatch/StepAdvancement.cs:32`

**Issue:**
```csharp
foreach (var nextId in completed.NextStepIds)   // NullReferenceException if NextStepIds is null
```

`Messaging.Contracts.Projections.StepProjection` declares `NextStepIds` as `List<Guid>` (non-nullable
by declaration) but STJ will deserialize `"nextStepIds": null` — or a payload missing the
`nextStepIds` key entirely — into a C# `null` without throwing, because nullable-reference-type
annotations are compile-time only. The `RedisProjectionWriter` always coalesces with
`step.NextStepIds ?? new List<Guid>()` before serialising, so a correctly-produced projection is
always safe. However, a hand-crafted, migrated, or stale L2 value that reaches
`WorkflowLifecycle.HydrateAndScheduleAsync` will deserialise OK (no `JsonException`) and be stored in
`wf.Steps` with a `null` `NextStepIds`. When the result consumer later calls `SelectNext` for that
step the `foreach` throws `NullReferenceException`, which propagates as an infra fault
(not caught by `IsBusiness`) → `Immediate(3)` retry → `_error`. SPEC req 5 says a corrupt-but-
deserialized projection must be a **clean ack**, not an infra retry storm.

**Suggested fix:**
```csharp
// StepAdvancement.cs — SelectNext body
foreach (var nextId in completed.NextStepIds ?? Enumerable.Empty<Guid>())
```

Or add a defensive coalescing null-check in `HydrateAndScheduleAsync` when building the
`StepProjection` before inserting into `steps` (mirrors the writer's own coalesce):

```csharp
// WorkflowLifecycle.HydrateAndScheduleAsync — after deserialising step
var step = JsonSerializer.Deserialize<StepProjection>(stepRaw!)
           ?? throw new JsonException("step deserialized to null");
// ensure NextStepIds is never null in L1
step = step with { NextStepIds = step.NextStepIds ?? new List<Guid>() };
steps[stepId] = step;
```

The `SelectNext` fix is simpler and more defensive; either is sufficient.

---

## Low

### IN-01: `BaseConsoleBusFactory.cs` is listed in the review scope but does not exist on disk

**File:** `src/BaseConsole.Core/Messaging/BaseConsoleBusFactory.cs` — file not found

**Issue:**
The review request included this file path but the file does not exist. The bus-factory seam that was
planned as a separate `BaseConsoleBusFactory` type was instead folded directly into the optional
`configureBus` callback parameter of `AddBaseConsoleMessaging` in
`MessagingServiceCollectionExtensions.cs` (Plan 24-04 Rule 3 deviation).

**Why it matters:** No functional impact — the seam is correctly implemented. This is informational
only; no source change is needed.

---

## Files Reviewed

| File | Status |
|------|--------|
| `src/Messaging.Contracts/StepOutcome.cs` | clean |
| `src/Messaging.Contracts/ExecutionResult.cs` | clean |
| `src/Messaging.Contracts/OrchestratorQueues.cs` | clean |
| `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` | clean |
| `src/BaseConsole.Core/Messaging/BaseConsoleBusFactory.cs` | file not found (IN-01) |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` | clean |
| `src/Orchestrator/Dispatch/IStepDispatcher.cs` | clean |
| `src/Orchestrator/Dispatch/StepDispatcher.cs` | clean |
| `src/Orchestrator/Dispatch/StepAdvancement.cs` | WR-02 |
| `src/Orchestrator/Consumers/GateClosedException.cs` | clean |
| `src/Orchestrator/Consumers/ResultConsumer.cs` | clean |
| `src/Orchestrator/Consumers/ResultConsumerDefinition.cs` | WR-01 (wiring target) |
| `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` | clean |
| `src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs` | WR-01 (wiring target) |
| `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` | clean |
| `src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs` | WR-01 (wiring target) |
| `src/Orchestrator/Scheduling/WorkflowFireJob.cs` | clean |
| `src/Orchestrator/Hydration/WorkflowLifecycle.cs` | clean |
| `src/Orchestrator/Program.cs` | WR-01 (scheduler wiring origin) |

---

_Reviewed: 2026-06-01_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
