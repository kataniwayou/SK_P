---
phase: 32-cancelled-circuit-breaker
reviewed: 2026-06-04T00:00:00Z
depth: standard
files_reviewed: 23
files_reviewed_list:
  - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
  - src/Orchestrator/Consumers/ResultConsumer.cs
  - src/Orchestrator/Consumers/FaultUnscheduleConsumer.cs
  - src/Orchestrator/Consumers/FaultUnscheduleConsumerDefinition.cs
  - src/Orchestrator/Program.cs
  - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
  - src/BaseProcessor.Core/Observability/ProcessorMetrics.cs
  - src/Orchestrator/Observability/OrchestratorMetrics.cs
  - tests/BaseApi.Tests/Orchestrator/CancelledCircuitBreakerE2ETests.cs
  - tests/BaseApi.Tests/Processor/BreakerTriggerFacts.cs
  - tests/BaseApi.Tests/Processor/CheckAndDropFacts.cs
  - tests/BaseApi.Tests/Processor/CancelledMarkerFacts.cs
  - tests/BaseApi.Tests/Orchestrator/ResultCheckAndDropFacts.cs
  - tests/BaseApi.Tests/Orchestrator/FaultUnscheduleFacts.cs
  - tests/BaseApi.Tests/Orchestrator/FaultIdempotencyFacts.cs
  - tests/BaseApi.Tests/Orchestrator/BreakerMetricsFacts.cs
  - tests/BaseApi.Tests/Contracts/CancelledMarkerKeyFacts.cs
  - tests/BaseApi.Tests/Orchestrator/FaultConsumerBindingFacts.cs
  - tests/BaseApi.Tests/Processor/RetryAttemptNumberingFacts.cs
  - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
  - tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs
  - tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs
findings:
  critical: 0
  warning: 1
  info: 4
  total: 5
status: issues_found
---

# Phase 32: Code Review Report

**Reviewed:** 2026-06-04T00:00:00Z
**Depth:** standard
**Files Reviewed:** 23 (8 source + 15 test)
**Status:** issues_found

## Summary

The "cancelled circuit breaker" implementation is correct on every load-bearing axis the
prompt flagged, and the supporting test corpus is unusually thorough (Wave-0 reality probes
pin the two MassTransit assumptions the design rests on — the retry-attempt boundary and the
`Fault<T>.Message` round-trip — against a real in-memory bus rather than mocks). I found no
correctness, security, or data-loss defects.

Specifically verified:

- **`GetRetryAttempt() == Limit` boundary (correct).** `RetryAttemptNumberingFacts` pins the
  exhausting delivery of a single endpoint-level `Immediate(Limit)` policy at `== Limit`
  (sequence `0,1,2,3` for `Limit=3`, `Limit+1` deliveries). The breaker reads
  `retryOptions.Value.Limit` and the production runtime bind
  (`ProcessorStartupOrchestrator.cs:151-154`) feeds the SAME `retryOptions.Value.Limit` into
  `UseMessageRetry(r => r.Immediate(retryLimit))`. Single source of truth — the check cannot
  desync from the policy.
- **No-TTL marker write (correct).** `Expiration.Persist` binds the modern overload; the
  hermetic `CancelledMarkerFacts` asserts the captured expiry renders `"PERSIST"` (not `EX`)
  and the E2E asserts the live `KeyTimeToLive == null` (redis `-1`). A self-expiring breaker
  (Pitfall 3) is ruled out.
- **Effect-first ordering (correct).** The marker SET precedes the `throw;`, so the marker is
  observable before MassTransit publishes the Fault and dead-letters to `_error`.
- **Double `.Message` extraction (correct).** `FaultConsumerBindingFacts` proves
  `Fault<EntryStepDispatch>.Message.WorkflowId` round-trips through real MT fault publication —
  no fallback needed.
- **Check-and-drop gates (correct, idempotent).** Both consumers read the `Cancelled(WorkflowId)`
  marker only (never `flag[H]`, D-13), workflowId-keyed, ack-and-discard with no write/dispatch/
  counter. The fault unschedule is naturally idempotent (`DeleteJob` on an absent job is a no-op;
  absent-from-L1 is a business no-op) — `FaultIdempotencyFacts` proves two deliveries == one.
- **Redis key correctness (correct).** `Cancelled` renders `:D` (hyphenated) matching the `Root`
  workflow-id precedent, distinct from `Root`/`Processor`, and the sentinel value is a single
  shared const used at both SET and CHECK sites — `CancelledMarkerKeyFacts` pins all of it.

The remaining items are one race-window observation (already a design-accepted tradeoff, raised
for documentation completeness) and four informational notes.

## Warnings

### WR-01: Fault-unschedule retry exhaustion publishes an unhandled `Fault<Fault<EntryStepDispatch>>`

**File:** `src/Orchestrator/Consumers/FaultUnscheduleConsumerDefinition.cs:35`,
`src/Orchestrator/Consumers/FaultUnscheduleConsumer.cs:35`

**Issue:** `FaultUnscheduleConsumer` consumes `Fault<EntryStepDispatch>` and re-throws on
infra-fault retry exhaustion (its only call, `UnscheduleOnlyAsync`, propagates
`RedisConnectionException`/Quartz faults — `WorkflowLifecycle.IsInfra`). When the
`Immediate(Limit)` budget on this endpoint is spent, MassTransit will auto-publish
`Fault<Fault<EntryStepDispatch>>`. No consumer is bound to that type, so it is silently
dead-lettered/skipped. This is almost certainly the accepted behaviour (the R4 "bus/Redis down"
backstop), but unlike the processor's `Fault<EntryStepDispatch>` — which the design deliberately
fans out — the double-fault is undocumented. The operational consequence: if the *schedule-owning*
replica's Quartz `DeleteJob` keeps faulting, the future-fire stop (req-4) silently does NOT happen
and there is no second-order signal beyond the `_error`/skipped queue. The processor-side marker is
already set, so in-flight messages are still dropped (req-3 holds); only the cron unschedule is at
risk.

**Fix:** No code change required if this is intended. To make it explicit, add one sentence to the
`FaultUnscheduleConsumerDefinition` XML doc noting that exhaustion here yields an unconsumed
`Fault<Fault<EntryStepDispatch>>` (dead-lettered, no further fan-out) and that the future-fire stop
is therefore best-effort against a persistently-faulting schedule owner — the marker still halts
in-flight work. Alternatively, if the unschedule should be more durable than `Immediate(Limit)`,
consider a longer/backoff retry budget for this endpoint only.

## Info

### IN-01: Check-and-drop TOCTOU window is real but design-accepted — worth a one-line code comment at the gate

**File:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:94-96`,
`src/Orchestrator/Consumers/ResultConsumer.cs:80-82`

**Issue:** A dispatch/result that passes the `Cancelled(...)` gate read *microseconds before* the
breaker writes the marker will proceed to full processing (advance/output-write/send). This is the
inherent read-then-act race; the marker is a best-effort in-flight stop, not a transactional fence.
The design clearly accepts this (the E2E only asserts the marker is set and future fires stop, and
the orchestrator `ResultConsumer` is independently L1/H-idempotent so a slipped-through duplicate is
absorbed downstream). The gate comments explain ordering and key choice thoroughly but never state
the residual race explicitly, so a future reader could mistake the gate for a hard guarantee.

**Fix:** Add a short note at one of the gates, e.g. `// Best-effort: a message that passed this read
just before the marker write still proceeds; the L1/H dedup downstream absorbs it (req-3 is
best-effort-against-marker, mirrors the boot-window relaxation).`

### IN-02: Defensive `metrics.WorkflowCancelled` tag uses `context.Id!.Value` inside the catch — bang is sound but the justification is implicit here

**File:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:260-261`

**Issue:** The `context.Id!.Value.ToString("D")` null-forgiving access in the breaker catch is
correct (Consume only runs post-MarkHealthy, Landmine 2), and the same idiom is justified-with-a-
comment at lines 62-63 and 82-84. The catch-block usage at 261 reuses the bang without re-stating
the justification, so it reads as a bare `!` if the catch is viewed in isolation. Not a bug.

**Fix:** Optional — append `// bang justified post-MarkHealthy (Landmine 2), as at :65` to line 261
for parity with the other increment sites.

### IN-03: `OutcomeLabel` switch has an unreachable `Processing` arm plus a defensive default — minor redundancy

**File:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:304-311`

**Issue:** The switch maps `StepOutcome.Processing => "processing"` *and* has a
`_ => outcome.ToString().ToLowerInvariant()` default. Both are unreachable on the send path (build
paths never emit `Processing`, and the enum is exhausted by the four named arms). This is
intentional defensive coding (documented in the method's XML), so it is not dead-code-to-remove,
but the explicit `Processing` arm and the catch-all default are mutually redundant — either alone
keeps the switch total. Purely cosmetic.

**Fix:** None required. If trimming is desired, keep the `_` default and drop the explicit
`Processing` arm (or vice-versa) — not both.

### IN-04: `ResultConsumer` manifest deserialize can throw `JsonException` on the business path (pre-existing, not introduced this phase)

**File:** `src/Orchestrator/Consumers/ResultConsumer.cs:99-103`

**Issue:** The manifest unbundle uses `JsonSerializer.Deserialize<string[]>(... ?? "[]") ??
Array.Empty`. The `??` guards cover a *null/absent* value, but a *present-but-garbled* manifest
string (not valid JSON) would throw `JsonException`, which the class doc (lines 29-37) claims cannot
happen on the business path ("deserialize as string[], never throw on the business path",
T-31-11). The manifest is server-written and content-addressed so in practice it is always
well-formed, but the stated invariant ("never throw") is not strictly enforced by the `??` guards —
a corrupted/wrongtype value would propagate as an INFRA-classified throw to the retry. This is
pre-existing Phase-31 code (untouched by Phase 32's two added lines at 65-72 and 80-82) and out of
this phase's change scope, but the cancelled-marker gate sits immediately above it, so it is in the
reviewed neighbourhood.

**Fix:** Out of scope for Phase 32. If the "never throw on business path" invariant is to be made
literal, wrap the deserialize in a try/catch that degrades a malformed manifest to
`Array.Empty<string>()` with a business-skip log, matching the corrupt-projection handling at
lines 85-92.

---

_Reviewed: 2026-06-04T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
