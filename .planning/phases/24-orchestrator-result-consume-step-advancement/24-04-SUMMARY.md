---
phase: 24-orchestrator-result-consume-step-advancement
plan: 04
subsystem: orchestrator-messaging
tags: [orchestrator, result-consume, step-advancement, competing-consumer, scheduled-redelivery, gate-closed, masstransit]
dependency_graph:
  requires: [24-01, 24-03]
  provides: [24-05]
  affects: [Orchestrator.Consumers, Orchestrator (composition root), BaseConsole.Core (configureBus seam)]
tech_stack:
  added: []
  patterns: [competing-consumer-endpoint, scheduled-redelivery-before-retry, gate-closed-throw, business-ack-infra-throw, delayed-message-scheduler]
key-files:
  created:
    - src/Orchestrator/Consumers/ResultConsumer.cs
    - src/Orchestrator/Consumers/ResultConsumerDefinition.cs
    - tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs
    - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
    - tests/BaseApi.Tests/Orchestrator/GateClosedRedeliverTests.cs
  modified:
    - src/Orchestrator/Program.cs
    - src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
  decisions:
    - "MassTransit 8.5.5 has NO in-memory message scheduler (AddInMemoryMessageScheduler/UseInMemoryScheduler do not exist — the plan/research assumed they did). The in-version, no-new-package options are the publish scheduler (AddPublishMessageScheduler/UsePublishMessageScheduler — needs an external Quartz/Hangfire consumer, absent) and the transport delayed scheduler (AddDelayedMessageScheduler/UseDelayedMessageScheduler). Wired the delayed scheduler."
    - "RUNTIME CAVEAT (flagged for review): the delayed scheduler on RabbitMQ relies on the rabbitmq_delayed_message_exchange plugin to actually defer delivery; that plugin is NOT enabled in the compose broker (24-RESEARCH Pitfall 1). The orchestrator compiles + wires the policy, but a truly working gate-closed redelivery needs either that broker plugin OR the MassTransit.Quartz package (new dependency) — an infra/architectural decision deferred to review / Plan 24-05."
    - "Widened BaseConsole.Core's optional configureBus seam from Action<IRabbitMqBusFactoryConfigurator> to Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator> (Rule 3, additive/null-default, sole call site is Program.cs) so scheduler/redelivery middleware that needs the registration context can be wired."
    - "Scheduled-redelivery interval policy 5s/15s/30s/60s (~110s total, Claude's-discretion A1) — comfortably outlasts the sub-second-to-few-second hydration window; after exhaustion routes to _error."
metrics:
  duration: ~30min (incl. Rule 1/3 build-fix cycle)
  completed: 2026-06-01
requirements: [ORCH-RESULT-02, ORCH-ADVANCE-01, ORCH-ADVANCE-02, ORCH-RESULT-ACK-01, ORCH-GATE-01]
---

# Phase 24 Plan 04: ResultConsumer + Shared Competing Endpoint + Scheduler/Redelivery Wiring Summary

One-liner: Added the load-balanced result-consume path — an `IConsumer<ExecutionResult>` on the shared competing-consumer queue `orchestrator-result` with gate-closed-throw (D-06) + L1-only traversal (D-08) + business-ack/infra-throw split, dispatching continuations through Plan 03's `StepAdvancement`/`IStepDispatcher`; wired the result endpoint, a message scheduler for scheduled redelivery, the redelivery-before-retry middleware, and the dispatcher/advancement singletons in `Program.cs`.

## What Was Built

- **ResultConsumer** (ORCH-RESULT-02/ADVANCE-01/02/RESULT-ACK-01/GATE-01): `IConsumer<ExecutionResult>`. Gate-closed → `throw new GateClosedException()` (NOT ack-return, D-06 — inverts the Phase 23 Start/Stop ack-drop so a one-time result survives hydration). Reads L1 via `store.TryGet` + `wf.Steps.TryGetValue` ONLY — no `Upsert`/`Remove`/`TryAcquire` stripe, no Redis/L2 (D-08). Unknown `(wf,step)` / drained / corrupt-projection are a clean `return` (business ack). For each `advancement.SelectNext(m.Outcome, completed, wf.Steps)` match it calls `dispatcher.DispatchAsync(...)` copying `CorrelationId`/`ExecutionId`/`EntryId`/`WorkflowId` from the result and `StepId`/`ProcessorId`/`Payload` from the next-step L1 projection. An infra fault from the broker `Send` propagates → `Immediate(3)` → `_error`. (`ExecutionResult` aliased to `Messaging.Contracts.ExecutionResult` to disambiguate from `MassTransit.ExecutionResult`.)
- **ResultConsumerDefinition**: `EndpointName = OrchestratorQueues.Result` (`"orchestrator-result"`) — a STABLE shared competing-consumer name (not the `"orchestrator"` fan-out base, no `InstanceId`/`Temporary`). Middleware ORDER (Pitfall 2 / GitHub #1575): `UseScheduledRedelivery(5s/15s/30s/60s)` FIRST (outer), then `UseMessageRetry(Immediate(3))` (inner). `GateClosedException` is deliberately NOT `Ignore<>`-listed so it reaches the redelivery layer.
- **Program.cs wiring**: `x.AddConsumer<ResultConsumer, ResultConsumerDefinition>()` (no `.Endpoint(InstanceId/Temporary)`); `x.AddDelayedMessageScheduler()` (registration) inside the `configureConsumers` lambda; `configureBus: (ctx, c) => c.UseDelayedMessageScheduler()` through the (widened) Plan 01 optional seam; `AddSingleton<IStepDispatcher, StepDispatcher>()` + `AddSingleton<StepAdvancement>()` alongside the existing L1/scheduler/lifecycle singletons. Start/Stop fan-out config untouched.
- **BaseConsole.Core seam widened (Rule 3)**: `AddBaseConsoleMessaging`'s optional `configureBus` parameter changed from `Action<IRabbitMqBusFactoryConfigurator>?` to `Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>?` (still optional/null-default). Additive and behavior-preserving — the sole call site is `Program.cs`; no test referenced the old seam.
- **Three test files** (reuse Phase 23's harness — no new harness introduced):
  - `ResultConsumeTests` (harness + `CapturingDispatchConsumer` + per-processorId `ReceiveEndpoint`): `CompletedResult_DispatchesMatchingNextStep_WithCorrectFieldCopy` (SPEC req 4 field-copy + exactly-one) and `Result_ConsumedExactlyOnce_NotBroadcast`.
  - `ResultAckTests` (`StartupGate().MarkReady()` + `WorkflowL1Store` + NSubstitute `IStepDispatcher`/`IDatabase`): unknown-`(wf,step)` ack, no-matching-next-step ack, `ResultPath_PerformsNoL2Read` (`db.DidNotReceive().StringGetAsync(...)` while the continuation IS dispatched), and `InfraFaultOnSend_Propagates` (`Assert.ThrowsAsync`).
  - `GateClosedRedeliverTests`: closed-gate `Consume` THROWS `GateClosedException` (no dispatch), then `MarkReady()` → re-`Consume` succeeds + dispatches the match.

## How It Works

A processor (future) `Send`s an `ExecutionResult` to `queue:orchestrator-result`, which lands on `ReceiveEndpoint("orchestrator-result")` — a single durable shared queue, so exactly one replica competes for each result (vs. the per-replica fan-out Start/Stop endpoints). With the gate open, the consumer reads the completed step and its next steps from in-memory L1 only, matches each next step's `EntryCondition` against `(int)outcome` (or `Always(4)`) via the pure `StepAdvancement`, and `Send`s one continuation per match to `queue:{nextStep.ProcessorId}` — the same `EntryStepDispatch` shape the cron fire job uses, now carrying the real `ExecutionId`/`EntryId` copied from the result. With the gate closed (hydration in flight) the consumer throws `GateClosedException`; `UseScheduledRedelivery` removes-and-reschedules the message on the 5s/15s/30s/60s ladder so it is reprocessed after `MarkReady`.

## Verification

- `dotnet build src/Orchestrator/Orchestrator.csproj -c Debug` — Build succeeded, 0 Warning(s) / 0 Error(s).
- `dotnet build src/Orchestrator/Orchestrator.csproj -c Release` — Build succeeded, 0 Warning(s) / 0 Error(s).
- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug` — Build succeeded, 0 Warning(s) / 0 Error(s).
- Orchestrator namespace slice (`dotnet run --project tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --no-build -- --filter-namespace BaseApi.Tests.Orchestrator`, run against a FRESHLY-built DLL) — Passed: 71, Failed: 0, Total: 71 (64 pre-existing + 7 new: ResultConsume ×2, ResultAck ×4, GateClosedRedeliver ×1). No regression.

Note on MTP filter syntax + a stale-DLL trap (extends the project memory note):
- **Stale-DLL trap (root cause of the build-fix deviations below):** `dotnet run ... --no-build` runs the LAST-BUILT test DLL. After a source change that fails to compile, `--no-build` silently re-runs the previous green binary and reports a pass — masking the broken build. ALWAYS `dotnet build` first (or drop `--no-build`) after editing source, and read the build's Error(s) count.
- `--filter-class <FQN>` returned "Zero tests ran" for every class (including pre-existing ones) in this environment; `--filter-method <FQN>` and `--filter-namespace <NS>` both work. Filter values must be passed UNQUOTED through this shell. The plan's `dotnet test ... --filter "FullyQualifiedName~..."` VSTest form is ignored by MTP; the working invocation is `--filter-namespace BaseApi.Tests.Orchestrator` (unquoted).

## Acceptance Criteria

Task 1:
- `ResultConsumer.cs` contains `IConsumer<ExecutionResult>`, `throw new GateClosedException()`, `store.TryGet(`, `advancement.SelectNext(`, `dispatcher.DispatchAsync(` — yes.
- `ResultConsumer.cs` does NOT contain `TryAcquire`, `Upsert`, `store.Remove`, `GetDatabase`, `StringGetAsync`, `WorkflowLifecycle` — verified (L1-only, no L2, no stripe).
- `ResultConsumerDefinition.cs` contains `EndpointName = OrchestratorQueues.Result` and `UseScheduledRedelivery` BEFORE `UseMessageRetry` — yes.
- `ResultConsumerDefinition.cs` does NOT contain `Ignore<GateClosedException>` — verified.
- Tests for the four behaviors exit 0 — yes (within the 71/0 slice).

Task 2:
- `Program.cs` contains `x.AddConsumer<ResultConsumer, ResultConsumerDefinition>()` WITHOUT a chained `.Endpoint(InstanceId/Temporary)` — yes.
- `Program.cs` contains `AddInMemoryMessageScheduler()` — **NO, by design (deviation):** that method does not exist in MassTransit 8.5.5. The equivalent in-version wiring is `x.AddDelayedMessageScheduler()` + `c.UseDelayedMessageScheduler()`. The intent — a message scheduler is wired so `UseScheduledRedelivery` can fire — IS satisfied at compile/wiring time (with the runtime plugin caveat noted above).
- `Program.cs` contains `AddSingleton<IStepDispatcher, StepDispatcher>()` and `AddSingleton<StepAdvancement>()` — yes.
- `dotnet build src/Orchestrator/Orchestrator.csproj` exits 0 — yes.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Disambiguated `ExecutionResult` (CS0104)**
- **Found during:** Task 1 build.
- **Issue:** `ResultConsumer.cs` had both `using MassTransit;` and `using Messaging.Contracts;`; MassTransit ships its own `MassTransit.ExecutionResult`, so `ExecutionResult` was ambiguous at the `IConsumer<ExecutionResult>` declaration + `ConsumeContext<ExecutionResult>` parameter.
- **Fix:** dropped the broad `using Messaging.Contracts;`, added `using ExecutionResult = Messaging.Contracts.ExecutionResult;`.
- **Files modified:** src/Orchestrator/Consumers/ResultConsumer.cs
- **Commit:** 0040b17 (folded into the Task 1 commit, pre-first-green).

**2. [Rule 1 - Bug] ResultConsumer `logger` parameter unread (CS9113)**
- **Found during:** the corrective full build (the per-task verification had used `dotnet run --no-build`, which silently ran a STALE green test DLL — see the stale-DLL trap note).
- **Issue:** the primary-ctor `logger` was referenced only in XML doc, never in code; warnings-as-errors → CS9113.
- **Fix:** `logger.LogInformation(...)` on the gate-closed-redeliver + business-ack-skip branches (mirrors `StartOrchestrationConsumer`'s logging idiom).
- **Files modified:** src/Orchestrator/Consumers/ResultConsumer.cs
- **Commit:** 4577ed1.

**3. [Rule 3 - Blocking] MassTransit 8.5.5 scheduler API + base-library seam**
- **Found during:** the corrective full build (Task 2 Program.cs).
- **Issue:** Program.cs used `x.AddInMemoryMessageScheduler()` and `c.UseInMemoryScheduler()` (both CS1061 — neither exists in MassTransit 8.5.5; XML-doc + dll-metadata probe of the pinned package confirmed they are absent). The research/plan (A2, Pitfall 3) assumed an in-memory scheduler that this version does not ship.
- **Fix:** wired the in-version transport delayed scheduler instead — `x.AddDelayedMessageScheduler()` (registration) + `configureBus: (ctx, c) => c.UseDelayedMessageScheduler()` (bus factory). Widened the BaseConsole.Core optional seam from `Action<IRabbitMqBusFactoryConfigurator>?` to `Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>?` (additive/null-default; sole call site is Program.cs).
- **Files modified:** src/Orchestrator/Program.cs, src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
- **Commit:** 094cb32.

### Discretionary decision resolved (plan-anticipated, not a deviation)

1. **Redelivery interval policy (A1):** `5s/15s/30s/60s` (~110s total) per CONTEXT Claude's-discretion — comfortably outlasts a sub-second-to-few-second hydration window; after exhaustion routes to `_error` (D-06).

## Deferred / Review Flag

**FLAG-24-04-SCHEDULER (infra/architectural — surfaced for the verifier / Plan 24-05):** The delayed message scheduler wired here defers delivery on RabbitMQ ONLY when the `rabbitmq_delayed_message_exchange` plugin is enabled on the broker. The compose broker (`rabbitmq:4.1.8-management-alpine`) does NOT enable that plugin (24-RESEARCH Pitfall 1). So the gate-closed-never-drop policy compiles and is wired, and the unit/harness tests (which exercise the consumer's THROW directly, not the live broker pipeline) pass — but a true live gate-closed redelivery will not defer until one of two infra/architectural choices is made: (a) enable the broker plugin (a compose/Dockerfile change), or (b) add the `MassTransit.Quartz` package and back the scheduler with the orchestrator's existing Quartz scheduler (a new dependency). Both cross the Rule-4 architectural line and are deferred to review; logged here rather than silently shipping a no-op. This does NOT block Plan 24-05 (Start/Stop conditionless rewrite), which throws the same `GateClosedException` and shares this same scheduler wiring.

## Out of Scope (NOT touched)

The KNOWN GOTCHA from 24-02 (BOM-encoded test files under `tests/BaseApi.Tests/Features/Orchestration/`) did not apply — all three new test files are plain at the verified `tests/BaseApi.Tests/Orchestrator/` path with namespace `BaseApi.Tests.Orchestrator`. The large set of `D` (deleted) `.planning/phases/01..16/*` entries in `git status` are PRE-EXISTING (present in the session's initial git snapshot) and were left untouched. Conditionless Start/Stop + gate-closed-throw for the Start/Stop consumers (ORCH-START-RELOAD-01, ORCH-STOP-DRAIN-01) are Plan 24-05's scope.

## Threat Model Outcome

- T-24-08 (Tampering / V5 input validation — corrupt `ExecutionResult` or corrupt L1 projection): mitigated — unknown `(wf,step)` / no-match / corrupt-but-deserialized projection are business ack-skips (`return`, no throw); a projection that fails to deserialize never enters `wf.Steps` and hits the unknown-step ack path (asserted by `ResultAckTests`).
- T-24-09 (DoS / poison message): mitigated — bounded `UseMessageRetry(Immediate(3))` → `_error`; business faults ack-skip and never retry-storm.
- T-24-10 (DoS / redelivery storm during a long Redis/hydration outage): mitigated by design — finite `5s/15s/30s/60s` interval set exhausts to `_error` (subject to the scheduler-plugin caveat above for live deferral).

## Requirements Satisfied

- ORCH-RESULT-02 — shared competing-consumer result endpoint (`ResultConsumerDefinition` binds `orchestrator-result`, no `InstanceId`/`Temporary`).
- ORCH-ADVANCE-01 — L1-only edge traversal + entry-condition match (wired through Plan 03's `StepAdvancement.SelectNext`).
- ORCH-ADVANCE-02 — continuation dispatch reusing `EntryStepDispatch` (wired through Plan 03's `IStepDispatcher`).
- ORCH-RESULT-ACK-01 — business-ack vs infra-throw split on the result path.
- ORCH-GATE-01 — gate-closed never-drop via `UseScheduledRedelivery` + a registered message scheduler (result-consumer slice; the Start/Stop slice lands in Plan 24-05). Live-broker deferral subject to FLAG-24-04-SCHEDULER.

## Commits

- 0040b17 feat(24-04): add ResultConsumer + ResultConsumerDefinition (gate-closed throw, L1-only, business-ack, redelivery-before-retry) (Task 1, incl. Rule 1 ExecutionResult alias)
- 9f58ec4 feat(24-04): wire result endpoint + scheduler + dispatcher/advancement singletons (Task 2 — initial, did not build; corrected by 094cb32)
- 094cb32 fix(24-04): wire delayed message scheduler via correct 8.5.5 API + widen base seam (Rule 3)
- 4577ed1 fix(24-04): log gate-closed + business-ack paths in ResultConsumer (Rule 1 — CS9113 logger unread)

(The earlier docs commit 6a320cb carried a draft of this SUMMARY with the now-corrected in-memory-scheduler claims; this revision supersedes it. Plus the metadata-only docs commit for SUMMARY / STATE / ROADMAP.)

## Self-Check: PASSED

All 8 key files present on disk (7 src/test + SUMMARY); the four real commits (0040b17, 9f58ec4, 094cb32, 4577ed1) present in git log. Final state: Orchestrator builds 0/0 (Debug + Release); BaseApi.Tests builds 0/0; Orchestrator slice 71 passed / 0 failed against a freshly-built DLL.
