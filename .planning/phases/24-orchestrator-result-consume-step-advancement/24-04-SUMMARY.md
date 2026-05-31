---
phase: 24-orchestrator-result-consume-step-advancement
plan: 04
subsystem: orchestrator-messaging
tags: [orchestrator, result-consume, step-advancement, competing-consumer, scheduled-redelivery, gate-closed, masstransit]
dependency_graph:
  requires: [24-01, 24-03]
  provides: [24-05]
  affects: [Orchestrator.Consumers, Orchestrator (composition root)]
tech_stack:
  added: []
  patterns: [competing-consumer-endpoint, scheduled-redelivery-before-retry, gate-closed-throw, business-ack-infra-throw, in-memory-message-scheduler]
key-files:
  created:
    - src/Orchestrator/Consumers/ResultConsumer.cs
    - src/Orchestrator/Consumers/ResultConsumerDefinition.cs
    - tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs
    - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
    - tests/BaseApi.Tests/Orchestrator/GateClosedRedeliverTests.cs
  modified:
    - src/Orchestrator/Program.cs
  decisions:
    - "UseInMemoryScheduler() (bus-factory half) IS required in MassTransit 8.5.5 — AddInMemoryMessageScheduler() alone does not wire the scheduler into the consume pipeline that UseScheduledRedelivery depends on; routed through Plan 01's configureBus seam."
    - "Scheduled-redelivery interval policy 5s/15s/30s/60s (~110s total, Claude's-discretion A1) — comfortably outlasts the sub-second-to-few-second hydration window; a true Redis-down outage exhausts to _error."
metrics:
  duration: ~15min
  completed: 2026-06-01
requirements: [ORCH-RESULT-02, ORCH-ADVANCE-01, ORCH-ADVANCE-02, ORCH-RESULT-ACK-01, ORCH-GATE-01]
---

# Phase 24 Plan 04: ResultConsumer + Shared Competing Endpoint + Scheduler/Redelivery Wiring Summary

One-liner: Added the load-balanced result-consume path — an `IConsumer<ExecutionResult>` on the shared competing-consumer queue `orchestrator-result` with gate-closed-throw (D-06) + L1-only traversal (D-08) + business-ack/infra-throw split, dispatching continuations through Plan 03's `StepAdvancement`/`IStepDispatcher`; wired the result endpoint, the in-memory message scheduler (`AddInMemoryMessageScheduler` + `UseInMemoryScheduler`), the redelivery-before-retry middleware, and the dispatcher/advancement singletons in `Program.cs`.

## What Was Built

- **ResultConsumer** (ORCH-RESULT-02/ADVANCE-01/02/RESULT-ACK-01/GATE-01): `IConsumer<ExecutionResult>`. Gate-closed → `throw new GateClosedException()` (NOT ack-return, D-06 — inverts the Phase 23 Start/Stop ack-drop so a one-time result survives hydration). Reads L1 via `store.TryGet` + `wf.Steps.TryGetValue` ONLY — no `Upsert`/`Remove`/`TryAcquire` stripe, no Redis/L2 (D-08). Unknown `(wf,step)` / drained / corrupt-projection are a clean `return` (business ack). For each `advancement.SelectNext(m.Outcome, completed, wf.Steps)` match it calls `dispatcher.DispatchAsync(...)` copying `CorrelationId`/`ExecutionId`/`EntryId`/`WorkflowId` from the result and `StepId`/`ProcessorId`/`Payload` from the next-step L1 projection. An infra fault from the broker `Send` propagates → `Immediate(3)` → `_error`.
- **ResultConsumerDefinition**: `EndpointName = OrchestratorQueues.Result` (`"orchestrator-result"`) — a STABLE shared competing-consumer name (not the `"orchestrator"` fan-out base, no `InstanceId`/`Temporary`). Middleware ORDER (Pitfall 2 / GitHub #1575): `UseScheduledRedelivery(5s/15s/30s/60s)` FIRST (outer), then `UseMessageRetry(Immediate(3))` (inner). `GateClosedException` is deliberately NOT `Ignore<>`-listed so it reaches the redelivery layer.
- **Program.cs wiring**: `x.AddConsumer<ResultConsumer, ResultConsumerDefinition>()` (no `.Endpoint(InstanceId/Temporary)`); `x.AddInMemoryMessageScheduler()` inside the `configureConsumers` lambda; `configureBus: c => c.UseInMemoryScheduler()` through Plan 01's optional seam; `AddSingleton<IStepDispatcher, StepDispatcher>()` + `AddSingleton<StepAdvancement>()` alongside the existing L1/scheduler/lifecycle singletons. Start/Stop fan-out config untouched.
- **Three test files** (reuse Phase 23's harness — no new harness introduced):
  - `ResultConsumeTests` (harness + `CapturingDispatchConsumer` + per-processorId `ReceiveEndpoint`): `CompletedResult_DispatchesMatchingNextStep_WithCorrectFieldCopy` (SPEC req 4 field-copy + exactly-one) and `Result_ConsumedExactlyOnce_NotBroadcast`.
  - `ResultAckTests` (`StartupGate().MarkReady()` + `WorkflowL1Store` + NSubstitute `IStepDispatcher`/`IDatabase`): unknown-`(wf,step)` ack, no-matching-next-step ack, `ResultPath_PerformsNoL2Read` (`db.DidNotReceive().StringGetAsync(...)` while the continuation IS dispatched), and `InfraFaultOnSend_Propagates` (`Assert.ThrowsAsync`).
  - `GateClosedRedeliverTests`: closed-gate `Consume` THROWS `GateClosedException` (no dispatch), then `MarkReady()` → re-`Consume` succeeds + dispatches the match.

## How It Works

A processor (future) `Send`s an `ExecutionResult` to `queue:orchestrator-result`, which lands on `ReceiveEndpoint("orchestrator-result")` — a single durable shared queue, so exactly one replica competes for each result (vs. the per-replica fan-out Start/Stop endpoints). With the gate open, the consumer reads the completed step and its next steps from in-memory L1 only, matches each next step's `EntryCondition` against `(int)outcome` (or `Always(4)`) via the pure `StepAdvancement`, and `Send`s one continuation per match to `queue:{nextStep.ProcessorId}` — the same `EntryStepDispatch` shape the cron fire job uses, now carrying the real `ExecutionId`/`EntryId` copied from the result. With the gate closed (hydration in flight) the consumer throws `GateClosedException`; `UseScheduledRedelivery` removes-and-reschedules the message on the 5s/15s/30s/60s ladder so it is reprocessed after `MarkReady`, never dropped.

## Verification

- `dotnet build src/Orchestrator/Orchestrator.csproj -c Debug` — Build succeeded, 0 Warning(s) / 0 Error(s).
- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug` — Build succeeded, 0 Warning(s) / 0 Error(s).
- Orchestrator namespace slice (`dotnet run --project tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --no-build -- --filter-namespace BaseApi.Tests.Orchestrator`) — Passed: 71, Failed: 0, Total: 71 (64 pre-existing + 7 new: ResultConsume ×2, ResultAck ×4, GateClosedRedeliver ×1). Run both after Task 1 and after the Task 2 Program.cs change — no regression either time.

Note on MTP filter syntax (extends the project memory note): this project uses Microsoft.Testing.Platform with xUnit v3 filters. `--filter-class <FQN>` returned "Zero tests ran" for every class (including pre-existing ones) in this environment, while `--filter-method <FQN>` and `--filter-namespace <NS>` both work. Also CRITICAL: filter values must be passed UNQUOTED through this shell — `--filter-method "Class.Method"` (quoted) passes the quotes literally and matches nothing; `--filter-method Class.Method` (unquoted) works. The plan's `dotnet test ... --filter "FullyQualifiedName~..."` VSTest form is ignored by MTP; the working invocation is `--filter-namespace BaseApi.Tests.Orchestrator` (unquoted).

## Acceptance Criteria

Task 1:
- `ResultConsumer.cs` contains `IConsumer<ExecutionResult>`, `throw new GateClosedException()`, `store.TryGet(`, `advancement.SelectNext(`, `dispatcher.DispatchAsync(` — yes.
- `ResultConsumer.cs` does NOT contain `TryAcquire`, `Upsert`, `store.Remove`, `GetDatabase`, `StringGetAsync`, `WorkflowLifecycle` — verified (L1-only, no L2, no stripe).
- `ResultConsumerDefinition.cs` contains `EndpointName = OrchestratorQueues.Result` and `UseScheduledRedelivery` BEFORE `UseMessageRetry` — yes.
- `ResultConsumerDefinition.cs` does NOT contain `Ignore<GateClosedException>` — verified.
- Tests for the four behaviors exit 0 — yes (within the 71/0 slice).

Task 2:
- `Program.cs` contains `x.AddConsumer<ResultConsumer, ResultConsumerDefinition>()` WITHOUT a chained `.Endpoint(InstanceId/Temporary)` — yes.
- `Program.cs` contains `AddInMemoryMessageScheduler()` — yes.
- `Program.cs` contains `AddSingleton<IStepDispatcher, StepDispatcher>()` and `AddSingleton<StepAdvancement>()` — yes.
- `dotnet build src/Orchestrator/Orchestrator.csproj` exits 0 — yes.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Disambiguated `ExecutionResult` (CS0104)**
- **Found during:** Task 1 build (`dotnet build src/Orchestrator/Orchestrator.csproj`).
- **Issue:** `ResultConsumer.cs` had both `using MassTransit;` and `using Messaging.Contracts;`, and MassTransit ships its own `MassTransit.ExecutionResult` type — so `ExecutionResult` was an ambiguous reference (CS0104) at the `IConsumer<ExecutionResult>` declaration and the `ConsumeContext<ExecutionResult>` parameter.
- **Fix:** dropped the broad `using Messaging.Contracts;` and added a `using ExecutionResult = Messaging.Contracts.ExecutionResult;` alias (the other contract types used — `OrchestratorQueues` in the definition, `EntryStepDispatch` via the dispatcher — are not referenced by simple name in `ResultConsumer.cs`, so the targeted alias is sufficient and minimal).
- **Files modified:** src/Orchestrator/Consumers/ResultConsumer.cs
- **Commit:** 56b9c9a (folded into the Task 1 commit, pre-first-green)
- **Result:** Orchestrator + test projects build 0/0; slice 71/0.

### Discretionary decisions resolved (plan-anticipated, not deviations)

1. **`UseInMemoryScheduler()` verification outcome (A2):** the plan instructed to attempt `AddInMemoryMessageScheduler()` alone first and add the `configureBus` callback IFF required. The bus-factory `UseInMemoryScheduler()` half IS required in MassTransit 8.5.5 — the `AddXxx` half registers the scheduler service but does not wire it into the consume pipeline that `UseScheduledRedelivery` reschedules through (the RabbitMQ transport has no native scheduler; the delayed-exchange plugin is not installed). Routed through Plan 01's optional `configureBus` seam (base = infra firewall intact). The Orchestrator builds clean with it; the seam Plan 01 added speculatively is now exercised.
2. **Redelivery interval policy (A1):** `5s/15s/30s/60s` (~110s total) per CONTEXT Claude's-discretion — comfortably outlasts a sub-second-to-few-second hydration window; after exhaustion a true Redis-down outage routes to `_error` (D-06).

## Out of Scope (NOT touched)

The KNOWN GOTCHA from 24-02 (BOM-encoded test files under `tests/BaseApi.Tests/Features/Orchestration/`) did not apply — all three new test files are plain at the verified `tests/BaseApi.Tests/Orchestrator/` path with namespace `BaseApi.Tests.Orchestrator`. Conditionless Start/Stop + gate-closed-throw for the Start/Stop consumers (ORCH-START-RELOAD-01, ORCH-STOP-DRAIN-01) are Plan 24-05's scope — this plan only adds the result consumer and reuses the existing (still-ack-drop) Start/Stop consumers unchanged.

## Threat Model Outcome

- T-24-08 (Tampering / V5 input validation — corrupt `ExecutionResult` or corrupt L1 projection): mitigated — unknown `(wf,step)` / no-match / corrupt-but-deserialized projection are business ack-skips (`return`, no throw); a projection that fails to deserialize never enters `wf.Steps` and hits the unknown-step ack path (asserted by `ResultAckTests.UnknownWorkflowStep_Acks_NoThrow_NoDispatch` + `NoMatchingNextStep_Acks_NoThrow_NoDispatch`).
- T-24-09 (DoS / poison message): mitigated — bounded `UseMessageRetry(Immediate(3))` → `_error`; business faults ack-skip and never retry-storm.
- T-24-10 (DoS / redelivery storm during a long Redis/hydration outage): mitigated — finite `5s/15s/30s/60s` interval set exhausts to `_error` (no infinite redelivery loop, D-06).

## Requirements Satisfied

- ORCH-RESULT-02 — shared competing-consumer result endpoint (`ResultConsumerDefinition` binds `orchestrator-result`, no `InstanceId`/`Temporary`).
- ORCH-ADVANCE-01 — L1-only edge traversal + entry-condition match (wired through Plan 03's `StepAdvancement.SelectNext`).
- ORCH-ADVANCE-02 — continuation dispatch reusing `EntryStepDispatch` (wired through Plan 03's `IStepDispatcher`).
- ORCH-RESULT-ACK-01 — business-ack vs infra-throw split on the result path.
- ORCH-GATE-01 — gate-closed never-drop via `UseScheduledRedelivery` (result-consumer slice; the Start/Stop slice lands in Plan 24-05).

## Commits

- 56b9c9a feat(24-04): add ResultConsumer + ResultConsumerDefinition (gate-closed throw, L1-only, business-ack, redelivery-before-retry) (Task 1, incl. Rule 1 alias fix)
- 86e44e9 feat(24-04): wire result endpoint + in-memory scheduler + dispatcher/advancement singletons (Task 2)

(Plus the metadata-only docs commit for SUMMARY / STATE / ROADMAP.)

## Self-Check: PASSED

All 7 key files present on disk (6 src/test + SUMMARY); both task commits (56b9c9a, 86e44e9) present in git log.
