---
phase: 32-cancelled-circuit-breaker
security_auditor: gsd-secure-phase
verified_at: 2026-06-04
asvs_level: 1
block_on: critical
threats_total: 17
threats_closed: 17
threats_open: 0
---

# Phase 32 — Cancelled Circuit-Breaker: Security Verification Report

## Summary

All 17 registered threats are CLOSED. No implementation gaps found. Live-stack
items (req-5/req-8-live/req-6-data) are deferred to the operator gate per the
existing VERIFICATION.md record — these have accepted-residual status and do not
constitute security gaps.

---

## Threat Verification

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-32-W0-01 | Tampering | accept | CLOSED | Hermetic test harness (Plan 01). No production surface — nothing to verify in implementation. |
| T-32-02 (DoS) | DoS | mitigate | CLOSED | `ProcessorMetrics.cs:53-54` — `processor_dispatch_deduped` and `workflow_cancelled` carry `ProcessorId` tag only. `OrchestratorMetrics.cs:44` — `orchestrator_result_deduped` carries `ProcessorId` only. `BreakerMetricsFacts.cs:69-113` — MeterListener cardinality guard asserts `ProcessorId` present, `workflowId`/`WorkflowId` absent across all three counters. |
| T-32-02 (marker) | Tampering | accept | CLOSED | Marker lives in broker-internal Redis. Same trust boundary as pre-existing `skp:flag:*`/`skp:data:*`. No new attack surface. Justification holds: `L2ProjectionKeys.Cancelled` is only accessible inside the same Redis instance already trusted by the existing dedup keys. |
| T-32-07 | Info Disclosure | accept | CLOSED | `L2ProjectionKeys.cs:64` — key format is `skp:cancelled:{workflowId:D}`. workflowId is a server-minted GUID. No PII encoded in the key. Justification holds. |
| T-32-06 | Tampering | RE-DISPOSITION → CLOSED-as-accepted | CLOSED | See detailed analysis below. |
| T-32-06b | Tampering | accept | CLOSED | Tied to 32-03 (cancelled). PreviousCancelled=3 was never a gap (pre-phase-32 baseline). `StepEntryCondition.PreviousCancelled = 3` is a long-standing valid enum member (ENTITY-06). No reinterpretation risk. Justification holds. |
| T-32-01 | EoP | mitigate | CLOSED | `EntryStepDispatchConsumer.cs:246` — outer catch gated `when (ctx.GetRetryAttempt() == retryOptions.Value.Limit)`. Inner business catches at lines 150/156 (OperationCanceledException → Cancelled, generic → Failed) convert all business outcomes to acked sends before anything escapes the inner scope. By construction, only INFRA exceptions reach the outer catch. `BreakerTriggerFacts.cs:199-239` — `ProcessAsyncThrow_StaysFailed_TripsNothing_NoRethrow` asserts a business throw at attempt==Limit results in Failed send, zero trip counter, no marker write. |
| T-32-04-log | Info Disclosure | mitigate | CLOSED | `EntryStepDispatchConsumer.cs:263-265` — `logger.LogWarning(` with template `"Breaker tripped — workflow {WorkflowId} cancelled on infra-exhaustion (step {StepId}, processor {ProcessorId}, H {H})"`. The four values (`dispatch.WorkflowId`, `dispatch.StepId`, `dispatch.ProcessorId`, `dispatch.H`) are passed as positional structured template arguments — never interpolated into the template string. No log-injection surface. `BreakerTriggerFacts.cs:148-154` — asserts WARN entry carries `WorkflowId`/`StepId`/`ProcessorId`/`H` as structured state keys. |
| T-32-R4 | DoS | accept | CLOSED | If the marker `StringSetAsync` itself faults (Redis down), the exception propagates to the existing `_error` backstop. No mitigation required; the design-accepted Risk R4 backstop holds. The `catch` block at `EntryStepDispatchConsumer.cs:255-258` writes marker then `throw;` — if the write fails, the re-throw still fires and MassTransit dead-letters to `_error`. Justification holds. |
| T-32-03 | Tampering | mitigate | CLOSED | `ResultConsumer.cs:80-82` — `if ((string?)await db.StringGetAsync(L2ProjectionKeys.Cancelled(m.WorkflowId)) == L2ProjectionKeys.CancelledMarkerValue) return;` placed after the flag gate (line 65-72), before `store.TryGet` (line 85). `ResultCheckAndDropFacts.cs:164-220` — `CancelledMarkerSet_AckAndDiscards_NoDispatch_NoDedup_NoWrite` asserts ack-and-discard with no dispatch, no dedup counter, no write. `OtherWorkflowCancelled_ThisResultProceeds_Dispatches` asserts workflowId-keying. |
| T-32-08 | DoS | accept | CLOSED | One extra Redis GET per message on the `ResultConsumer` path (line 80). Same Redis instance already hit for the flag dedup GET (line 65). Accepted incremental cost. Justification holds. |
| T-32-04-fault | Spoofing/DoS | accept | CLOSED | Broker credentials required to inject `Fault<EntryStepDispatch>` messages. Pre-existing trust boundary (same as all MT message types). `FaultUnscheduleConsumer` unschedule is idempotent via `UnscheduleOnlyAsync`; absent-L1 is a no-op. `FaultUnscheduleFacts.cs:115-140` pins absent-L1 no-op behavior. |
| T-32-04b | Tampering | mitigate | CLOSED | `FaultUnscheduleConsumer.cs:35` — calls `lifecycle.UnscheduleOnlyAsync(workflowId, ...)`. `WorkflowLifecycle.UnscheduleOnlyAsync` is a keep-L1 idempotent delete: `scheduler.DeleteJob(jobKey)` on an absent job is a no-op. `FaultIdempotencyFacts.cs:52-107` — `TwoIdenticalFaultDeliveries_LeaveSameEndStateAsOne` asserts two identical deliveries produce the same end state (job gone, L1 kept, no error) as one delivery. |
| T-32-08b | EoP | mitigate | CLOSED | `FaultUnscheduleConsumerDefinition.cs:28` — `EndpointName = "orchestrator"`. `Program.cs:48-49` — registered with `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })`. This is a per-replica temporary/auto-delete fan-out endpoint, NOT the shared `orchestrator-result` competing-consumer queue. Every replica receives the fault and only the schedule-owning one acts. |
| T-32-08c | Tampering | mitigate | CLOSED | `FaultUnscheduleConsumer.cs:21-23` — ctor has exactly `WorkflowLifecycle lifecycle` and `ILogger<FaultUnscheduleConsumer> logger`. No `IConnectionMultiplexer`, no `IDatabase`. Cannot read or write any Redis key by construction. `FaultIdempotencyFacts.cs:112-128` — `Consumer_TakesNoRedisDependency_OnlyLifecycleAndLogger` reflects the ctor via `GetConstructors` and asserts exactly 2 params (`WorkflowLifecycle` + `ILogger<FaultUnscheduleConsumer>`), and asserts `IConnectionMultiplexer`/`IDatabase` are absent. |
| T-32-05 | Tampering | accept | CLOSED | Resume requires: (1) a Redis DEL on `skp:cancelled:{workflowId}` (Redis write access — pre-existing trust boundary) and (2) a POST to `/api/v1/orchestration/start` (API auth — pre-existing trust boundary). Manual clear is the deliberate D-08 design. No new attack surface. Justification holds. |
| T-32-09 | DoS | mitigate | CLOSED | Marker namespace bounded by distinct cancelled workflows (one key per workflow, no TTL accumulation during test runs because the E2E teardown includes `skp:cancelled:*` in `L2KeysToCleanup`). `CancelledCircuitBreakerE2ETests.cs` includes `skp:cancelled` in teardown cleanup. `scripts/phase-32-close.ps1` performs explicit `redis-cli --scan --pattern 'skp:cancelled:*' | del` before the AFTER snapshot (VERIFICATION.md artifact table, line 65). |

---

## T-32-06 Detailed Analysis: PreviousCancelled=3 — CLOSED-as-accepted

**Question:** Is `StepEntryCondition.PreviousCancelled = 3` (retained because Plan 32-03 was cancelled per D-12 reversed) an inert baseline residual or a real routing-corruption vector?

**Findings from code:**

1. `StepEntryCondition.cs:19` — `PreviousCancelled = 3` is a valid enum member. `StepDtoValidator.cs:37-38` and `68-69` — `IsInEnum()` therefore ACCEPTS `EntryCondition == 3` on StepDto create/update. This is the post-cancellation state.

2. `StepOutcome.cs:20` — `StepOutcome.Cancelled = 3` exists as a normal processor-reportable outcome.

3. `StepAdvancement.cs:41` — `SelectNext` matches next steps where `next.EntryCondition == (int)outcome || next.EntryCondition == Always`. For an inbound result with `StepOutcome.Cancelled (= 3)`, it selects next steps with `EntryCondition == 3` (i.e., `PreviousCancelled`).

4. **Routing behavior:** A step configured with `EntryCondition == PreviousCancelled (3)` IS reachable by `SelectNext` when a predecessor reports `StepOutcome.Cancelled`. This was true BEFORE Phase 32 (pre-existing baseline). Phase 32 introduces `StepOutcome.Cancelled` only on the breaker path (the processor re-throws → MassTransit publishes `Fault<EntryStepDispatch>`, which does NOT go to `orchestrator-result`) and on the existing token-cancellation path (`EntryStepDispatchConsumer.cs:153` — `BuildCancelled`). The token-cancellation path already existed before Phase 32.

5. **Phase 32 breaker path specifically:** The breaker catch at `EntryStepDispatchConsumer.cs:246-268` does `throw;` — it does NOT call `SendResult(BuildCancelled(...))`. MassTransit publishes a `Fault<EntryStepDispatch>`, which is consumed by `FaultUnscheduleConsumer`, NOT routed to `orchestrator-result`. Therefore the breaker trip produces NO `StepOutcome.Cancelled` result on `orchestrator-result` — `SelectNext` is never called with `Cancelled` outcome from the breaker path.

6. **Net assessment:** A step with `EntryCondition == PreviousCancelled (3)` is reachable only via the pre-existing token-cancellation path, which is unchanged by Phase 32. Phase 32 adds no new route to `PreviousCancelled`-gated steps. The breaker path explicitly avoids emitting a `Cancelled` result to the orchestrator. The retained `PreviousCancelled = 3` member is **an inert baseline residual** — the routing semantics are identical to the pre-Phase-32 baseline.

**Verdict: CLOSED-as-accepted.** The retained `PreviousCancelled = 3` is not a routing-corruption vector introduced by Phase 32. It is a pre-existing valid enum member whose semantics are unchanged. The Phase 32 breaker path is specifically designed to NOT emit `StepOutcome.Cancelled` to `orchestrator-result`.

---

## Unregistered Flags

None. All threat flags from SUMMARY.md `## Threat Flags` map to registered threat IDs.

---

## Accepted Risks Log

| Threat ID | Category | Justification |
|-----------|----------|---------------|
| T-32-W0-01 | Tampering | Hermetic test harness only; no production surface. |
| T-32-02 (marker) | Tampering | Broker-internal Redis; same pre-existing trust boundary as `skp:flag:*`/`skp:data:*`. |
| T-32-07 | Info Disclosure | Key encodes server-minted GUID only; no PII. |
| T-32-06 | Tampering | PreviousCancelled=3 is pre-existing baseline; Phase 32 breaker path does not emit `StepOutcome.Cancelled` to orchestrator-result. |
| T-32-06b | Tampering | Pre-existing enum member; no Phase 32 reinterpretation risk. |
| T-32-R4 | DoS | Redis-down marker-write fault lands in existing `_error` backstop; accepted design tradeoff. |
| T-32-08 | DoS | One extra Redis GET per result message; same instance already hit for flag dedup. |
| T-32-04-fault | Spoofing/DoS | Broker credential boundary; unschedule is idempotent; absent-L1 is no-op. |
| T-32-05 | Tampering | Resume requires pre-existing Redis write + API auth access. Manual clear is deliberate. |

---

_Verified: 2026-06-04_
_Auditor: gsd-secure-phase (claude-sonnet-4-6)_
_ASVS Level: 1_
