---
phase: 32-cancelled-circuit-breaker
plan: 06
subsystem: orchestrator consumer (future-fire stop / fault fanout)
tags: [masstransit, fault, fanout-endpoint, per-replica, unschedule, keep-l1, idempotent, no-flag, hermetic, wave-2, quartz]

# Dependency graph
requires:
  - phase: 32-cancelled-circuit-breaker
    provides: "Plan 01 — FaultConsumerBindingFacts proved Fault<EntryStepDispatch>.Message.WorkflowId round-trips via double-.Message (no fallback)"
  - phase: 32-cancelled-circuit-breaker
    provides: "Plan 04 — the processor breaker re-throw that exhausts UseMessageRetry and auto-publishes the Fault<EntryStepDispatch> this consumer handles"
  - phase: 23-orchestration (Stop/Teardown machinery)
    provides: "WorkflowLifecycle.UnscheduleOnlyAsync (idempotent keep-L1 jobId-addressed DeleteJob) + the per-replica InstanceId+Temporary fan-out endpoint pattern (Start/Stop)"
provides:
  - "FaultUnscheduleConsumer — IConsumer<Fault<EntryStepDispatch>> on the per-replica orchestrator-{instanceId} fan-out endpoint that extracts WorkflowId via context.Message.Message.WorkflowId and unschedules via UnscheduleOnlyAsync (req-4 / D-06)"
  - "FaultUnscheduleConsumerDefinition — EndpointName=orchestrator + Immediate(Limit), groups onto the per-replica fan-out queue (Pitfall 5)"
  - "Program.cs registration mirroring Start/Stop (InstanceId+Temporary) — every replica receives the fault, only the schedule owner acts"
  - "FaultUnscheduleFacts + FaultIdempotencyFacts — present/absent-L1 + duplicate-delivery idempotency + no-Redis-dependency (D-13) coverage"
affects: [32-07]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Fault<T> fanout consumer on a per-replica InstanceId+Temporary endpoint (shared EndpointName=orchestrator) — broadcast halt, NOT the shared orchestrator-result competing-consumer queue"
    - "No-Redis-by-construction guard: the consumer ctor takes only WorkflowLifecycle + ILogger, so it CANNOT read/write flag[H] or the cancelled marker (D-13) — asserted via ctor-param reflection"
    - "Hermetic fault-consumer test: NSubstitute Fault<EntryStepDispatch> whose .Message returns a real EntryStepDispatch, driven through a real WorkflowLifecycle over a real RAM Quartz scheduler; unschedule observed via scheduler.CheckExists"

key-files:
  created:
    - src/Orchestrator/Consumers/FaultUnscheduleConsumer.cs
    - src/Orchestrator/Consumers/FaultUnscheduleConsumerDefinition.cs
    - tests/BaseApi.Tests/Orchestrator/FaultUnscheduleFacts.cs
    - tests/BaseApi.Tests/Orchestrator/FaultIdempotencyFacts.cs
  modified:
    - src/Orchestrator/Program.cs

key-decisions:
  - "Relied on the double-.Message extraction (context.Message.Message.WorkflowId) with NO fallback — Plan 01 FaultConsumerBindingFacts proved it round-trips through real MassTransit fault publication."
  - "OMITTED r.Ignore<WorkflowRootNotFoundException>() from the definition (unlike Stop): the fault path's UnscheduleOnlyAsync returns a no-op on absent-L1 and never throws that exception (confirmed against WorkflowLifecycle.cs:149-158)."
  - "The consumer takes NO Redis dependency (lifecycle + logger only) — the cleanest D-13 / T-32-08c proof: it CANNOT seed flag[H]=Pending or write the cancelled marker by construction. FaultIdempotencyFacts asserts the ctor shape."
  - "Tests schedule via the real WorkflowScheduler.ScheduleAsync (builds the production WorkflowFireJob) rather than a hand-rolled JobBuilder — Quartz.Simpl.NoOpJob is absent in this Quartz version, and ScheduleAsync is the faithful analog (mirrors SchedulingTests/StopConsumerLifecycleTests)."
  - "Used a real RAM Quartz scheduler + real WorkflowL1Store + real WorkflowLifecycle (StopConsumerLifecycleTests idiom) so the unschedule effect is observed via scheduler.CheckExists — more faithful than substituting WorkflowScheduler (a concrete sealed type)."

metrics:
  duration: ~25m
  completed: 2026-06-04
---

# Phase 32 Plan 06: Fault Unschedule Consumer (Future-Fire Stop) Summary

The future-fire half of the cancelled circuit-breaker: a new `FaultUnscheduleConsumer : IConsumer<Fault<EntryStepDispatch>>` on a per-replica `InstanceId`+`Temporary` fan-out endpoint. On the MassTransit-auto-published `Fault<EntryStepDispatch>` (from the Plan-04 breaker re-throw exhausting `UseMessageRetry`), every replica extracts `WorkflowId` from `Fault.Message`, resolves the `jobId` from L1, and unschedules the Quartz job via the existing idempotent keep-L1 `UnscheduleOnlyAsync` — only the schedule-owning replica acts; others no-op. The halt is naturally idempotent: NO `flag[H]` CAS gate, NO L2 marker write (D-13). Almost entirely reuse + wiring: two new files + a one-block `Program.cs` registration + two hermetic Facts classes.

## What Was Built

### Task 1 — FaultUnscheduleConsumer + Definition + Program.cs registration (req-4, req-8; D-06/D-13)

- **`src/Orchestrator/Consumers/FaultUnscheduleConsumer.cs`** (analog: `StopOrchestrationConsumer`): `IConsumer<Fault<EntryStepDispatch>>` whose `Consume` extracts `var workflowId = context.Message.Message.WorkflowId` (double `.Message` — outer `Fault<T>`, inner the original `EntryStepDispatch`; proven by Plan-01 `FaultConsumerBindingFacts`), logs a WARN ("Fault halt — unscheduling workflow {WorkflowId}"), and `await lifecycle.UnscheduleOnlyAsync(workflowId, ct)`. Single `workflowId` (no `foreach` — a Fault carries one message). Ctor: `WorkflowLifecycle` + `ILogger<FaultUnscheduleConsumer>` ONLY — NO Redis handle (cannot touch flag[H]/marker by construction, D-13).
- **`src/Orchestrator/Consumers/FaultUnscheduleConsumerDefinition.cs`** (analog: `StopOrchestrationConsumerDefinition`): `ConsumerDefinition<FaultUnscheduleConsumer>` with `EndpointName = "orchestrator"` (the SHARED per-replica fan-out base name — Pitfall 5, load-bearing) and `ConfigureConsumer => UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))`. OMITS `r.Ignore<WorkflowRootNotFoundException>()` (the fault path never throws it — `UnscheduleOnlyAsync` no-ops on absent-L1). Same `IOptions<RetryOptions>` single source as Start/Stop (D-01).
- **`src/Orchestrator/Program.cs`**: added `x.AddConsumer<FaultUnscheduleConsumer, FaultUnscheduleConsumerDefinition>().Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` after the Stop registration, sharing the existing `instanceId` closure. NOT on the shared `orchestrator-result` endpoint (Pitfall 5). Nothing else added — the `Configure<RetryOptions>` bind + `instanceId` closure already existed.

### Task 2 — FaultUnscheduleFacts + FaultIdempotencyFacts (req-4, req-8; D-06/D-13)

- **`FaultUnscheduleFacts`** (3 facts, `[Trait("Category","Hermetic")]`): (1) workflow PRESENT in L1 → the schedule-owning replica deletes the Quartz job (observed via `scheduler.CheckExists` flipping false) AND keeps the L1 entry (keep-L1); (2) workflow ABSENT from L1 → no throw + an unrelated replica's job is untouched (non-owning replica no-op); (3) the unschedule is keyed on `context.Message.Message.WorkflowId` — only the target workflow's job (resolved by its L1 JobId) is deleted, a second seeded workflow's job survives.
- **`FaultIdempotencyFacts`** (2 facts): (1) TWO identical fault deliveries leave the SAME end state as one (job gone, L1 kept after both; DeleteJob on an absent job is idempotent) AND across both deliveries the path issued NO `StringSetAsync` to ANY key, across both the legacy `(TimeSpan?,When)` and modern `(Expiration,ValueCondition)` overloads (D-13 no-write guard); (2) the consumer ctor has EXACTLY two params — `WorkflowLifecycle` + `ILogger<FaultUnscheduleConsumer>` — and NO `IConnectionMultiplexer`/`IDatabase` (reflection guard — it CANNOT touch flag[H] or the marker by construction; T-32-08c).
- Both use a NSubstitute `Fault<EntryStepDispatch>` whose `.Message` returns a real `EntryStepDispatch` carrying a known `WorkflowId`, driven through a real `WorkflowLifecycle` over a real RAM Quartz scheduler (StopConsumerLifecycleTests idiom).

## Verification

- `dotnet build src/Orchestrator -c Debug` — 0 Warning / 0 Error.
- `dotnet build tests/BaseApi.Tests -c Debug` — 0 Warning / 0 Error.
- `dotnet build SK_P.sln -c Release` — 0 Warning / 0 Error.
- `--filter-class "*FaultUnscheduleFacts"` — Passed 3 / Failed 0.
- `--filter-class "*FaultIdempotencyFacts"` — Passed 2 / Failed 0.
- Full `BaseApi.Tests.Orchestrator` namespace (95 tests, including the two NEW classes + the Plan-01 `FaultConsumerBindingFacts`) — **Passed 95 / Failed 0** in isolation (zero regression from this plan's changes).
- Grep confirms `FaultUnscheduleConsumer.cs` references NO `Flag(` / `StringSet` / `StringGet` / `IConnectionMultiplexer` / `IDatabase` (no Redis handle — D-13).
- Acceptance greps hold: consumer implements `IConsumer<Fault<EntryStepDispatch>>`, contains `context.Message.Message.WorkflowId` + `UnscheduleOnlyAsync`; definition has `EndpointName = "orchestrator"` + `Immediate(_retryOptions.Value.Limit)` and NO `r.Ignore<WorkflowRootNotFoundException>`; `Program.cs` has `AddConsumer<FaultUnscheduleConsumer, FaultUnscheduleConsumerDefinition>()` + `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `Quartz.Simpl.NoOpJob` does not exist in this Quartz version**
- **Found during:** Task 2 (first `dotnet build tests/BaseApi.Tests`).
- **Issue:** the test code initially scheduled a probe job via `JobBuilder.Create<Quartz.Simpl.NoOpJob>()` to set up the "job present" precondition; `Quartz.Simpl.NoOpJob` is not a type in the Quartz version this repo references (CS0234, 4 sites).
- **Fix:** schedule via the real `WorkflowScheduler.ScheduleAsync(workflowId, jobId, cron, ct)` (builds the production `WorkflowFireJob`) — the faithful analog already used by `SchedulingTests`/`StopConsumerLifecycleTests`. More representative of the real path than a hand-rolled job.
- **Files modified:** `tests/BaseApi.Tests/Orchestrator/FaultUnscheduleFacts.cs`, `tests/BaseApi.Tests/Orchestrator/FaultIdempotencyFacts.cs`.
- **Commit:** `b05b34b` (folded into the Task-2 test commit — the fix was pre-first-green).

No auth gates. No architectural decisions required (Rule 4 not triggered).

## Deferred Issues

- **`BreakerTriggerFacts.ProcessAsyncThrow_StaysFailed_TripsNothing_NoRethrow` flakes under cross-namespace test ordering — OUT OF SCOPE (a Plan-32-04 test).** The full hermetic suite run during 32-06 showed exactly 1 failure: this Processor-namespace fact, created by Plan 32-04 (commit `69b06fa`, before this plan). This plan added ONLY Orchestrator code + two Orchestrator test classes — it touched zero processor code/tests, so it cannot have caused this. Proven independent: (a) `--filter-class "*BreakerTriggerFacts"` passes 3/3 in isolation; (b) the entire Orchestrator namespace (incl. both new 32-06 classes) is 95/95 in isolation; (c) the failure surfaces only when the Processor namespace runs alongside other classes — a timing/shared-state race in the in-memory MassTransit / `GetRetryAttempt`-payload harness, the same flake family logged from Plan 05 and MEMORY `reference_close_gate_surfaces_stale_flaky_tests`. NOT fixed here (scope boundary, per the executor SCOPE BOUNDARY rule). Logged to `deferred-items.md`. The live phase-32 close gate (Plan 07, 3×GREEN + triple-SHA) is the authoritative full-suite signal; stabilize the harness race there if it persists.

## Must-Haves Status

- ✅ Every orchestrator replica consumes `Fault<EntryStepDispatch>` on a per-replica InstanceId+Temporary fan-out endpoint (`Program.cs` registration + `EndpointName="orchestrator"`).
- ✅ The fault consumer extracts WorkflowId from `Fault.Message` (`context.Message.Message.WorkflowId`), resolves jobId from L1, and unschedules via `UnscheduleOnlyAsync` (FaultUnscheduleFacts present-in-L1 case).
- ✅ A workflow absent from L1 yields a no-op, no throw (FaultUnscheduleFacts absent-from-L1 case).
- ✅ A duplicate fault delivery yields an idempotent no-op (FaultIdempotencyFacts two-deliveries case — job already gone, L1 kept).
- ✅ The fault consumer reads/writes no flag[H] key and no L2 marker (D-13) — no Redis dependency by construction (FaultIdempotencyFacts ctor-shape + no-StringSetAsync assertions; grep confirms no Redis type in the consumer).

## Threat Surface Scan

No new security-relevant surface beyond the plan's `<threat_model>`. T-32-04 (forged Fault DoS) is accepted — publishing onto the broker requires the same broker credentials that already protect every Send/Publish; the unschedule is idempotent + keyed on a server-minted WorkflowId resolved from L1 (absent ⇒ no-op). T-32-04b (replay) mitigated — `FaultIdempotencyFacts` asserts two deliveries == one end state. T-32-08b (wrong endpoint) mitigated — `EndpointName="orchestrator"` + `.Endpoint(InstanceId/Temporary)` puts it on the per-replica fan-out queue (NOT the shared `orchestrator-result`); the registration shape is grep-asserted. T-32-08c (seeding flag[H]) mitigated — the consumer takes no Redis dependency (lifecycle + logger only); `FaultIdempotencyFacts` asserts the ctor shape. No new network endpoint, auth path, file access, or schema change.

## Known Stubs

None — the consumer is wired to the real idempotent `UnscheduleOnlyAsync` over the real L1/Quartz machinery; no placeholder/empty-value flow.

## Self-Check: PASSED

- All 4 created/modified production+test files present on disk; `Program.cs` registration present.
- Both task commits (`33b1d8b`, `b05b34b`) present in git history.
- No accidental file deletions across the two commits (each adds/modifies only its scoped paths).
