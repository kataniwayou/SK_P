---
phase: 32-cancelled-circuit-breaker
plan: 04
subsystem: processor consumer (breaker seam)
tags: [breaker, circuit-breaker, cancelled-marker, check-and-drop, dedup-counter, trip-counter, effect-first, no-ttl, hermetic, wave-2, GetRetryAttempt]

# Dependency graph
requires:
  - phase: 32-cancelled-circuit-breaker
    provides: "Plan 01 — RetryAttemptNumberingFacts pinned the exhaustion boundary == Limit (no escalation)"
  - phase: 32-cancelled-circuit-breaker
    provides: "Plan 02 — L2ProjectionKeys.Cancelled(Guid) + CancelledMarkerValue const + ProcessorMetrics.DispatchDeduped / WorkflowCancelled counters"
  - phase: 31-idempotent-execution-exactly-once-effect
    provides: "EntryStepDispatchConsumer effect-first flag[H] gate + EntryStepDispatch.WorkflowId/StepId/ProcessorId/H contract"
provides:
  - "EntryStepDispatchConsumer check-and-drop gate (ack-and-discards in-flight dispatches for a cancelled workflow, workflowId-keyed, no flag[H] touch — D-05/D-13)"
  - "EntryStepDispatchConsumer flag[H]==Ack dedup counter (processor_dispatch_deduped +1 at the existing drop gate — D-10)"
  - "EntryStepDispatchConsumer final-attempt breaker catch (set no-TTL marker effect-first -> workflow_cancelled +1 -> WARN log -> re-throw; gated ONLY on GetRetryAttempt()==Limit — D-01/D-02/D-11)"
  - "DispatchTestKit.RetryContext / Retry helpers (controllable GetRetryAttempt + IOptions<RetryOptions> for hermetic breaker facts)"
affects: [32-05, 32-06]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "GetRetryAttempt() reads the ConsumeRetryContext payload's RetryAttempt (NOT a header) — verified by IL reflection against MassTransit 8.5.5; hermetic tests stub TryGetPayload<ConsumeRetryContext> to drive the boundary"
    - "No-TTL marker write uses Expiration.Persist (renders PERSIST), diverging from the EX-TTL'd data/flag writes (Pitfall 3) — a TimeSpan would render EX 300"
    - "Outer breaker try wraps the whole infra body; the two existing business catches stay INNER, so anything reaching the outer catch is INFRA by construction (RESEARCH A3 / Pitfall 2) — no IsInfra predicate"
    - "Single sentinel const (CancelledMarkerValue) on BOTH the check-and-drop compare and the breaker SET — value desync impossible"

key-files:
  created:
    - tests/BaseApi.Tests/Processor/CheckAndDropFacts.cs
    - tests/BaseApi.Tests/Processor/BreakerTriggerFacts.cs
    - tests/BaseApi.Tests/Processor/CancelledMarkerFacts.cs
  modified:
    - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
    - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs

key-decisions:
  - "Marker no-TTL write binds the modern Expiration overload via Expiration.Persist (NOT expiry: null — that fails to bind against the Expiration overload, and (TimeSpan?)null would bind the obsolete legacy overload). Persist renders PERSIST; a finite TTL renders EX 300 — the CancelledMarkerFacts assertion pins PERSIST / no EX."
  - "GetRetryAttempt() decompiled (IL) to read ConsumeRetryContext.RetryAttempt via TryGetPayload — NOT the MT-Redelivery-Count header. DispatchTestKit.RetryContext stubs the payload so the hermetic breaker facts drive attempt==Limit / attempt<Limit without a real bus."
  - "Breaker catch gated ONLY on GetRetryAttempt()==Limit (no IsInfra) — the existing business catches at :OperationCanceled/:generic convert ALL business outcomes to acked sends BEFORE anything escapes the outer try, so anything reaching the outer catch is infra by construction (RESEARCH A3)."
  - "Wave-0 dependency cleared: Plan 01 RetryAttemptNumberingFacts confirmed == Limit (NO escalation), so the catch was built against == Limit as the SPEC locks."
  - "Rule 3 mechanical realignment: the new required IOptions<RetryOptions> ctor param broke 2 EntryStepDispatchScopeTests direct ctors + the runtime-scope DI harness — threaded DispatchTestKit.Retry() / registered IOptions<RetryOptions> (mirrors the 31-05 ProcessorStartupOrchestrator threading precedent)."

metrics:
  duration: ~13m
  completed: 2026-06-04
---

# Phase 32 Plan 04: Breaker Processor Half Summary

The only genuinely new logic in this phase: three behavioral edits to `EntryStepDispatchConsumer` — (1) the final-attempt breaker catch (marker-set effect-first -> trip counter -> WARN log -> re-throw), (2) the check-and-drop gate that ack-and-discards in-flight dispatches for a cancelled workflow, and (3) the dedup counter at the existing `flag[H]=="Ack"` drop gate — plus the `IOptions<RetryOptions>` ctor dependency the breaker check needs. Three hermetic Facts classes pin trigger, marker, and check-and-drop. Business `ProcessAsync` throws stay immediate-`Failed` and trip nothing (D-01/D-15).

## What Was Built

### Task 1 — Check-and-drop gate + dedup counter (req-3 processor / req-7 dedup; D-05/D-10/D-13)
- **Edit 1 (dedup counter):** at the existing `flag[H]=="Ack"` gate, before `return`, increment `metrics.DispatchDeduped.Add(1, ProcessorId)` (the same `context.Id!.Value.ToString("D")` idiom as `DispatchConsumed`). Counts how often a redelivery is dropped at the Phase-31 dedup gate.
- **Edit 2 (check-and-drop gate):** placed AFTER the `flag[H]` gate (RESEARCH Unknown-3 — cheaper for the common dedup case, keeps the Phase-31 gate visually first) and BEFORE input resolution: `if ((string?)await db.StringGetAsync(L2ProjectionKeys.Cancelled(dispatch.WorkflowId)) == L2ProjectionKeys.CancelledMarkerValue) return;`. Reads the cancelled marker ONLY (never `flag[H]` — D-13). ack-and-discard: no advancement, no rollback, no dead-letter, NO dedup counter (this is the cancelled drop, not a flag-Ack drop).
- **`CheckAndDropFacts` (4 facts):** flag-Ack drop increments `processor_dispatch_deduped` exactly once (MeterListener) + no transform/send/write; cancelled-set ack-and-discards with NO send, NO dedup counter, NO `StringSetAsync` to ANY key (across all overloads); a DIFFERENT workflow's marker leaves THIS dispatch processing normally (proves workflowId-keying); the cancelled path writes NO `flag` key (D-13 guard, across all set overloads).

### Task 2 — Final-attempt breaker catch (req-1, req-2, req-7-trip; D-01/D-02/D-07/D-11)
- **Edit 3 (ctor dep):** added `IOptions<RetryOptions> retryOptions` to the primary ctor + `using Messaging.Contracts.Configuration;`. DI resolves it automatically (bound at `BaseProcessorServiceCollectionExtensions.cs:88` — no registration change).
- **Edit 4 (breaker catch):** wrapped the infra body (input read, output writes, manifest, outbound Pending seed, the Send, the inbound CAS flip) in an OUTER `try` whose single `catch (Exception) when (ctx.GetRetryAttempt() == retryOptions.Value.Limit)`: (a) sets `L2ProjectionKeys.Cancelled(WorkflowId) = CancelledMarkerValue` with `Expiration.Persist` (NO TTL, D-07 — effect-first, BEFORE the re-throw, D-02); (b) `metrics.WorkflowCancelled.Add(1, ProcessorId)`; (c) a four-field WARN log (`{WorkflowId}/{StepId}/{ProcessorId}/{H}` as structured template args — T-18-04); (d) `throw;` so MassTransit publishes `Fault<EntryStepDispatch>` + dead-letters to `_error` (D-03). The two existing business catches stay INNER, so anything reaching the outer catch is INFRA by construction (no `IsInfra` predicate — RESEARCH A3 / Pitfall 2).
- **`BreakerTriggerFacts` (3 facts):** infra Send-throw at attempt==Limit trips (marker SET + `workflow_cancelled` +1 via MeterListener + WARN log carrying the 4 fields + re-throw observed); infra throw at attempt<Limit does NOT trip (no marker, no counter, no WARN) but re-throws; a business `ProcessAsync` throw stays Failed (one Failed ExecutionResult sent) + acked, no marker, no counter, no WARN, no re-throw (Pitfall 2).
- **`CancelledMarkerFacts` (1 fact):** the marker write binds the no-TTL `Expiration.Persist` overload (captured expiry renders `PERSIST`, never `EX`) — pins the no-TTL at the call site (Plan 06's E2E owns the live `TTL == -1`).
- **`DispatchTestKit` helpers:** `RetryContext<T>` (stubs the `ConsumeRetryContext` payload that `GetRetryAttempt()` reads — verified by IL reflection that it is NOT a header read) and `Retry(limit)` (`IOptions<RetryOptions>`).

## Wave-0 Dependency Resolution

Plan 01's `RetryAttemptNumberingFacts` CONFIRMED `GetRetryAttempt() == Limit` on the exhausting delivery (observed 0,1,2,3 for Limit=3; NO escalation, no `[Trait("Escalate","Risk-R1")]`). The breaker catch was therefore built against `== Limit` exactly as the SPEC locks — no fallback header/counter path needed.

## Verification

- `dotnet build src/BaseProcessor.Core -c Debug` — 0 Warning / 0 Error.
- `dotnet build tests/BaseApi.Tests -c Debug` — 0 Warning / 0 Error.
- `dotnet build SK_P.sln -c Release` — 0 Warning / 0 Error.
- `--filter-class "*CheckAndDropFacts"` — Passed 4 / Failed 0.
- `--filter-class "*BreakerTriggerFacts"` — Passed 3 / Failed 0.
- `--filter-class "*CancelledMarkerFacts"` — Passed 1 / Failed 0.
- Full hermetic suite `--filter-not-trait "Category=RealStack"` — **Passed 459 / Failed 0** (451 prior + 8 net new: 4 CheckAndDrop + 3 BreakerTrigger + 1 CancelledMarker; zero regression).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Threaded IOptions<RetryOptions> through the consumer-construction test sites**
- **Found during:** Task 2 (build after adding the ctor param).
- **Issue:** the new required `IOptions<RetryOptions>` ctor param broke 2 direct `new EntryStepDispatchConsumer(...)` sites in `EntryStepDispatchScopeTests.cs` (CS7036) and the DI-resolved `EntryStepDispatchRuntimeScopeTests` harness (missing service).
- **Fix:** threaded `DispatchTestKit.Retry()` through the 2 direct ctors; registered `.AddSingleton(DispatchTestKit.Retry())` in the runtime-scope DI graph. Default `Limit=3`, not exercised on those Completed paths. Mirrors the 31-05 `ProcessorStartupOrchestrator` threading precedent (anticipated by the plan's `<verification>` note).
- **Files modified:** `tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs`, `tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs`.
- **Commit:** `69b06fa`.

**2. [Rule 1 - Implementation correctness] Marker no-TTL overload binding**
- **Found during:** Task 2 (the plan's literal `expiry: null` snippet did not compile).
- **Issue:** `expiry: null` cannot bind the modern `(RedisKey, RedisValue, Expiration, ...)` overload (`Expiration` is a struct); `(TimeSpan?)null` would bind the OBSOLETE legacy `When` overload (and mismatch the test stubs).
- **Fix:** used `Expiration.Persist` (the modern no-TTL form, renders `PERSIST`; a finite TTL renders `EX 300`). Semantically identical to the plan's intent (NO TTL, D-07); `CancelledMarkerFacts` asserts `PERSIST` / no `EX`. The plan's snippet was illustrative; this is the binding-correct realization.
- **Files modified:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs`.
- **Commit:** `69b06fa`.

No auth gates. No architectural decisions required (Rule 4 not triggered).

## Must-Haves Status

- ✅ A dispatch whose infra op fails through Limit attempts sets the cancelled marker and re-throws (BreakerTriggerFacts trip case).
- ✅ A `ProcessAsync` throw produces a Failed ExecutionResult with NO marker and NO re-throw (BreakerTriggerFacts business-throw case).
- ✅ The processor sets `cancelled[workflowId]` with NO TTL before the infra fault propagates (effect-first, `Expiration.Persist`; CancelledMarkerFacts pins it).
- ✅ EntryStepDispatchConsumer ack-and-discards an in-flight dispatch when the cancelled marker is set (CheckAndDropFacts).
- ✅ The flag[H]==Ack drop gate increments `processor_dispatch_deduped` once (CheckAndDropFacts MeterListener).
- ✅ A breaker trip increments `workflow_cancelled` once and emits a WARN log carrying workflowId/stepId/processorId/H (BreakerTriggerFacts).

## Threat Surface Scan

No new security-relevant surface beyond the plan's `<threat_model>`. The breaker catch (T-32-01 mitigate) is gated at the pinned `== Limit` boundary with business throws caught earlier — `BreakerTriggerFacts` asserts a business throw sets no marker and re-throws nothing. The WARN log (T-32-04 mitigate) passes the four server-minted ids as structured template args, never interpolated. The marker SET (T-32-02 accept) is on the same broker-internal Redis as the existing `skp:flag:*`/`skp:data:*` keys. No new network endpoint, auth path, file access, or schema change.

## Known Stubs

None — both gates and the breaker catch are wired to real Redis reads/writes and real metrics/log calls. No placeholder/empty-value flow.

## Self-Check: PASSED

- All 3 created test files present on disk; all 4 modified files present.
- Both task commits (`6cb20d5`, `69b06fa`) present in git history.
- No accidental file deletions across the two commits (`git diff --diff-filter=D HEAD~2 HEAD` empty).
