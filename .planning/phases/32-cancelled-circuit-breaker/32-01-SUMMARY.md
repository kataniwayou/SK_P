---
phase: 32-cancelled-circuit-breaker
plan: 01
subsystem: tests
tags: [masstransit, retry, get-retry-attempt, fault, in-memory-harness, hermetic, wave-0, risk-probe]

# Dependency graph
requires:
  - phase: 31-idempotent-execution-exactly-once-effect
    provides: "RetryOptions.Limit single source feeding UseMessageRetry; EntryStepDispatch.WorkflowId contract"
  - phase: 25-shared-contracts-webapi-responders
    provides: "EntryStepDispatch wire contract (Messaging.Contracts leaf)"
provides:
  - "RetryAttemptNumberingFacts — PINS GetRetryAttempt() exhaustion boundary == Limit for a single endpoint-level Immediate(Limit) policy (unblocks the Plan-04 breaker seam; Risk R1)"
  - "FaultConsumerBindingFacts — PROVES Fault<EntryStepDispatch>.Message.WorkflowId round-trips through real MassTransit fault publication (unblocks the Plan-05 fault consumer; Risk R2 / D-06)"
affects: [32-04, 32-05]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Wave-0 MassTransit-reality probe: AddMassTransitTestHarness + UsingInMemory drives a REAL bus to pin behavior a NSubstitute ConsumeContext cannot (GetRetryAttempt header extension, Fault.Message population)"
    - "Single endpoint-level UseMessageRetry(Immediate(Limit)) on an explicit ReceiveEndpoint — the documented reliable GetRetryAttempt case"
    - "await harness.Published.Any<Fault<T>>(ct) to settle retry exhaustion; GetConsumerHarness<T>().Consumed.Any<Fault<T>>(ct) to settle the fault-fanout consume"

key-files:
  created:
    - tests/BaseApi.Tests/Processor/RetryAttemptNumberingFacts.cs
    - tests/BaseApi.Tests/Orchestrator/FaultConsumerBindingFacts.cs
  modified: []

key-decisions:
  - "Used an explicit cfg.ReceiveEndpoint(... e.UseMessageRetry(r => r.Immediate(LIMIT))) (not a ConsumerDefinition) to GUARANTEE a single endpoint-level policy — the reliable GetRetryAttempt case, isolating Risk R1 from any consumer-level/stacked-policy interference"
  - "Recorded GetRetryAttempt() into a static ConcurrentQueue cleared at test start (hermetic, single test per class) since the value is only observable inside a real consumer's Consume against the real bus"
  - "Task 2 uses Immediate(0) on the throwing endpoint so it exhausts on the first delivery — fastest path to the auto-published Fault<EntryStepDispatch>"
  - "Both probes are throwaway test types (RetryProbeMessage / ThrowingDispatchConsumer / FaultProbeConsumer) — no production code touched, matching the plan's no-production-change constraint"

metrics:
  duration: ~12m
  completed: 2026-06-04
---

# Phase 32 Plan 01: Wave-0 MassTransit-Reality Probes Summary

Two hermetic Wave-0 probes that pin the two load-bearing MassTransit assumptions behind the cancelled circuit-breaker against a REAL in-memory bus (not NSubstitute stubs), de-risking the breaker seam (Plan 04) and the fault consumer (Plan 05) before any production code depends on them.

## What Was Built

### Task 1 — RetryAttemptNumberingFacts (Risk R1 / A1+A2)
- Drives a real `AddMassTransitTestHarness` + `UsingInMemory` bus with ONE explicit receive endpoint carrying a SINGLE `UseMessageRetry(r => r.Immediate(LIMIT))` policy (`LIMIT = 3`), mirroring the production endpoint-level idiom.
- A probe consumer records `context.GetRetryAttempt()` per delivery (thread-safe queue), then unconditionally throws an infra-style exception to exhaust the budget.
- **PINNED RESULT (boundary confirmed):** the observed attempt sequence is exactly `0,1,2,3` for `LIMIT=3` — first delivery records `0`, the final/exhausting delivery records `== LIMIT`. Total deliveries `= LIMIT + 1`. MassTransit publishes `Fault<RetryProbeMessage>` from the same exhaustion.
- **The SPEC's `GetRetryAttempt() == Limit` is CONFIRMED — NO escalation.** Plan 04's breaker catch can gate on `ctx.GetRetryAttempt() == retryOptions.Value.Limit` as the SPEC locks. The MassTransit#1217/#3216 "returns 0 every delivery" bug does NOT manifest for this project's single endpoint-level policy.

### Task 2 — FaultConsumerBindingFacts (Risk R2 / D-06)
- Real in-memory harness: a throwing `IConsumer<EntryStepDispatch>` with `Immediate(0)` (exhausts on first delivery) plus a probe `IConsumer<Fault<EntryStepDispatch>>`.
- Publishes one `EntryStepDispatch` with a KNOWN fixed `WorkflowId`; the probe captures `context.Message.Message.WorkflowId` (double `.Message`: outer `Fault<EntryStepDispatch>`, inner the original `EntryStepDispatch`).
- **PROVEN:** the probe is invoked exactly once and the captured `WorkflowId == knownWorkflowId` (not `Guid.Empty`). `Fault<EntryStepDispatch>.Message` IS the original message instance — D-06's "extract WorkflowId from Fault.Message" holds with NO fallback. Plan 05's `FaultUnscheduleConsumer` can rely on the double-`.Message` extraction.

## Verification

- `dotnet test tests/BaseApi.Tests -- --filter-class "*RetryAttemptNumberingFacts"` → Passed 1 / Failed 0.
- `dotnet test tests/BaseApi.Tests -- --filter-class "*FaultConsumerBindingFacts"` → Passed 1 / Failed 0.
- `dotnet build tests/BaseApi.Tests -c Debug` → 0 Warning / 0 Error.
- Full hermetic suite `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack"` → **Passed 443 / Failed 0** (441 prior + 2 new; zero regression).
- Acceptance greps hold: both files exist; `RetryAttemptNumberingFacts` contains `GetRetryAttempt` + `Immediate(` + `AddMassTransitTestHarness` (NOT `OrchestratorTestStubs.Context`); `FaultConsumerBindingFacts` contains `Fault<EntryStepDispatch>` + `.Message.Message.WorkflowId` + `AddMassTransitTestHarness` + a known-fixed-Guid assertion.

## Deviations from Plan

None — both autonomous tasks executed exactly as written. No production code changed. No escalation triggered (Risk R1's boundary confirmed `== Limit`).

## Risk R1 Resolution (BLOCKING note status: CLEARED)

The plan's ESCALATION CLAUSE fires only if the exhausting-delivery boundary is NOT `== Limit`. It is `== Limit` (sequence `0,1,2,3` for `LIMIT=3`). **No `[Trait("Escalate","Risk-R1")]` trait was added and NO blocking escalation note is required.** Plan 04 may build the breaker catch on the SPEC-locked `GetRetryAttempt() == retryOptions.Value.Limit` predicate.

## Commits

- `998dd49` test(32-01): pin GetRetryAttempt() exhaustion boundary == Limit (Risk R1)
- `3aca386` test(32-01): prove Fault<EntryStepDispatch>.Message.WorkflowId round-trips (Risk R2)

## Self-Check: PASSED
