---
phase: 32-cancelled-circuit-breaker
verified: 2026-06-04T20:48:12Z
status: human_needed
score: 7/8 must-haves verified (req-6 half-override + req-5/req-8-live/req-6-data pending live gate)
overrides_applied: 1
overrides:
  - must_have: "StepEntryCondition.PreviousCancelled (3) is removed and 3 left as a numeric gap; a StepDto with EntryCondition == 3 fails IsInEnum validation"
    reason: "Plan 32-03 was cancelled by explicit user direction (D-12 reversed). PreviousCancelled = 3 is deliberately kept — the member is unused and removing it is unnecessary churn. The 'keep StepOutcome.Cancelled = 3' half of req-6 is fully satisfied. Live-data assertion (no live step has EntryCondition == 3) remains pending the operator E2E gate (Plan 07 Task 3)."
    accepted_by: "user (per 32-03-SUMMARY.md explicit cancellation record)"
    accepted_at: "2026-06-04T00:00:00Z"
human_verification:
  - test: "Live breaker-trip → halt → resume round-trip"
    expected: "Run: docker compose up -d --build processor-sample orchestrator baseapi-service && dotnet test tests/BaseApi.Tests -- --filter-class \"*CancelledCircuitBreakerE2ETests\" && pwsh -NoProfile -File ./scripts/phase-32-close.ps1. Expected: CancelledCircuitBreakerE2ETests GREEN (TTL==-1 on skp:cancelled marker, 'Fault halt' WARN in ES, no Cancelled result on orchestrator-result, future cron fires stop, resume re-fires); GATE_EXIT=0 with triple-SHA BEFORE==AFTER (including explicit skp:cancelled:* scan-clean)."
    why_human: "Requires the full v3.6.0 docker compose stack running with REBUILT processor-sample/orchestrator/baseapi containers (embedded SourceHash must match the host build — the breaker catch and FaultUnscheduleConsumer are new this phase). Not runnable in this environment. This gates req-5 live proof, req-8 live resume proof, and req-6 data assertion (no live Steps row with EntryCondition == 3)."
---

# Phase 32: Cancelled Circuit-Breaker — Verification Report

**Phase Goal:** A two-level "cancelled circuit breaker" — on final-attempt infra exhaustion the processor sets a no-TTL Redis cancellation marker, increments a trip counter, WARN-logs, and re-throws so MassTransit auto-publishes Fault<EntryStepDispatch>; both the processor and orchestrator consumers check-and-drop in-flight messages for a cancelled workflow (in-flight stop); a Fault fan-out consumer idempotently unschedules the Quartz job (future-fire stop); plus resume (clear marker + re-Start).
**Verified:** 2026-06-04T20:48:12Z
**Status:** human_needed
**Re-verification:** No — initial verification.

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Breaker trips ONLY on infra-fault retry-budget exhaustion (`GetRetryAttempt() == Limit`); business `ProcessAsync` throws stay immediate-`Failed` and trip nothing | ✓ VERIFIED | `EntryStepDispatchConsumer.cs:246` outer catch gated on `ctx.GetRetryAttempt() == retryOptions.Value.Limit`; inner business catches at lines 150/156 convert all business outcomes to acked sends before anything escapes; `BreakerTriggerFacts` pins all three paths (trip/no-trip/business-throw) |
| 2 | `cancelled[workflowId]` marker is set effect-first (before re-throw), no TTL, via `L2ProjectionKeys.Cancelled` | ✓ VERIFIED | `EntryStepDispatchConsumer.cs:255-258` uses `Expiration.Persist` (NO TTL); SET precedes `throw;` at line 267; `CancelledMarkerFacts` pins `Expiration.Persist`; `CancelledMarkerKeyFacts` pins key shape `skp:cancelled:{workflowId:D}` and `CancelledMarkerValue = "true"` |
| 3 | Both `EntryStepDispatchConsumer` and `ResultConsumer` ack-and-discard in-flight messages when the cancelled marker is set; other workflows unaffected | ✓ VERIFIED | Processor: `EntryStepDispatchConsumer.cs:94-96` reads `L2ProjectionKeys.Cancelled(dispatch.WorkflowId)` after the flag gate, before input resolution; Orchestrator: `ResultConsumer.cs:80-82` reads same marker after flag gate, before `store.TryGet`; `CheckAndDropFacts` + `ResultCheckAndDropFacts` each assert workflowId-keying, no flag[H] touch (D-13), and no downstream send/counter on the cancelled path |
| 4 | Every orchestrator replica consumes `Fault<EntryStepDispatch>` on a per-replica `InstanceId`+`Temporary` fan-out endpoint; extracts `WorkflowId` from `Fault.Message`; unschedules via `UnscheduleOnlyAsync`; absent-from-L1 and duplicate deliveries are no-ops | ✓ VERIFIED | `FaultUnscheduleConsumer.cs` implements `IConsumer<Fault<EntryStepDispatch>>`, extracts `context.Message.Message.WorkflowId`, calls `lifecycle.UnscheduleOnlyAsync`; `FaultUnscheduleConsumerDefinition.cs` has `EndpointName = "orchestrator"`; `Program.cs:48-49` registers with `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })`; `FaultUnscheduleFacts` pins present/absent-L1 behavior; `FaultIdempotencyFacts` pins two-delivery idempotency |
| 5 | No `Cancelled` `ExecutionResult` is sent to `orchestrator-result` on the breaker path; `_error` still receives the dead-lettered message; the token-cancellation `Cancelled` outcome (EXEC-08) is unchanged | ✓ VERIFIED | The only `SendResult(BuildCancelled(...))` call is at `EntryStepDispatchConsumer.cs:153` (inside the inner `OperationCanceledException` catch — token-cancellation path); the outer breaker catch at line 246-268 only sets marker, increments counter, logs, and `throw;`s — no `SendResult`/`BuildCancelled` on the breaker path; the same `throw;` exhausts `UseMessageRetry` which dead-letters to `_error` AND auto-publishes `Fault<EntryStepDispatch>` (D-03). Live proof PENDING operator gate (Task 3). |
| 6 | `StepEntryCondition.PreviousCancelled (3)` removed; `StepOutcome.Cancelled = 3` kept | PASSED (override) | Plan 32-03 cancelled by user. `PreviousCancelled = 3` deliberately retained (D-12 reversed). `StepOutcome.Cancelled = 3` confirmed present in `StepOutcome.cs:20`. `IsInEnum` ACCEPTS 3 (because member still exists). Override accepted — see frontmatter. Live-data assertion (no live row with EntryCondition == 3) remains pending operator gate. |
| 7 | `processor_dispatch_deduped_total`, `orchestrator_result_deduped_total`, and `workflow_cancelled_total` increment at their gates/trip; the trip emits a WARN/ERROR log with `workflowId`/`stepId`/`processorId`/`H`; no counter carries a `workflowId` label | ✓ VERIFIED | `ProcessorMetrics.cs`: `processor_dispatch_deduped` (line 53) + `workflow_cancelled` (line 54); `OrchestratorMetrics.cs`: `orchestrator_result_deduped` (line 44); no `_total` suffix in instrument names (collector appends); `EntryStepDispatchConsumer.cs:263-265` WARN log with all 4 structured template args; `BreakerMetricsFacts` MeterListener asserts `ProcessorId` tag present, `workflowId`/`WorkflowId` tag absent |
| 8 | Resume = clear marker + re-`POST /orchestration/start` re-fires; Fault path reads/writes no `flag[H]` key; duplicate fault deliveries idempotent | ✓ VERIFIED (hermetic) / PENDING (live) | `FaultUnscheduleConsumer.cs` takes NO `IConnectionMultiplexer`/`IDatabase` ctor param — CANNOT touch `flag[H]` or marker by construction; `FaultIdempotencyFacts` asserts ctor shape (exactly 2 params: `WorkflowLifecycle` + `ILogger`) and no `StringSetAsync` across both overloads across two deliveries; live resume (DEL marker + re-POST) is authored in `CancelledCircuitBreakerE2ETests.cs:221-227` but requires the operator live gate |

**Score:** 7/8 truths verified (req-6 carried via override; req-5/req-8-live/req-6-data live proof pending operator gate)

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | `Cancelled(Guid)` builder + `CancelledMarkerValue` const | ✓ VERIFIED | Line 64: `Cancelled(Guid) => $"{Prefix}cancelled:{workflowId:D}"`; line 71: `CancelledMarkerValue = "true"` |
| `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` | `DispatchDeduped` + `WorkflowCancelled` counters | ✓ VERIFIED | Lines 40, 46: counter properties; lines 53-54: `processor_dispatch_deduped` + `workflow_cancelled` instrument names, no `_total` suffix |
| `src/Orchestrator/Observability/OrchestratorMetrics.cs` | `ResultDeduped` counter | ✓ VERIFIED | Line 37: `ResultDeduped` property; line 44: `orchestrator_result_deduped` instrument name, no `_total` suffix |
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` | Breaker catch + check-and-drop gate + dedup counter + `IOptions<RetryOptions>` ctor dep | ✓ VERIFIED | Line 50: `IOptions<RetryOptions> retryOptions` in ctor; lines 83-86: dedup counter at flag gate; lines 94-96: check-and-drop gate; lines 246-268: outer breaker catch with `GetRetryAttempt() == Limit`, `Expiration.Persist`, WARN log, re-throw |
| `src/Orchestrator/Consumers/ResultConsumer.cs` | Check-and-drop gate + `ResultDeduped` increment at flag gate | ✓ VERIFIED | Lines 65-72: `ResultDeduped.Add` at flag-Ack gate; lines 80-82: `L2ProjectionKeys.Cancelled(m.WorkflowId)` check-and-drop gate; no `StringSetAsync` to marker (orchestrator never writes it) |
| `src/Orchestrator/Consumers/FaultUnscheduleConsumer.cs` | `IConsumer<Fault<EntryStepDispatch>>` extracting `WorkflowId` + `UnscheduleOnlyAsync` + no Redis dep | ✓ VERIFIED | Line 23: implements `IConsumer<Fault<EntryStepDispatch>>`; line 29: `context.Message.Message.WorkflowId`; line 35: `lifecycle.UnscheduleOnlyAsync`; no `Flag(`, no `StringSet`/`StringGet`, no `IConnectionMultiplexer`/`IDatabase` |
| `src/Orchestrator/Consumers/FaultUnscheduleConsumerDefinition.cs` | `EndpointName = "orchestrator"` + `Immediate(Limit)` retry | ✓ VERIFIED | Line 28: `EndpointName = "orchestrator"`; line 35: `UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))`; no `r.Ignore<WorkflowRootNotFoundException>` |
| `src/Orchestrator/Program.cs` | `AddConsumer<FaultUnscheduleConsumer, FaultUnscheduleConsumerDefinition>().Endpoint(InstanceId/Temporary)` | ✓ VERIFIED | Lines 48-49: registration with `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` after the Stop registration, sharing the `instanceId` closure |
| `tests/BaseApi.Tests/Processor/RetryAttemptNumberingFacts.cs` | Pins `GetRetryAttempt() == Limit` exhaustion boundary | ✓ VERIFIED | Exists; uses `AddMassTransitTestHarness`; confirmed `== Limit` (sequence `0,1,2,3` for `LIMIT=3`; NO escalation) |
| `tests/BaseApi.Tests/Orchestrator/FaultConsumerBindingFacts.cs` | Proves `Fault<EntryStepDispatch>.Message.WorkflowId` round-trips | ✓ VERIFIED | Exists; uses `AddMassTransitTestHarness`; asserts `WorkflowId == knownWorkflowId` via `.Message.Message.WorkflowId` |
| `tests/BaseApi.Tests/Contracts/CancelledMarkerKeyFacts.cs` | Pins `skp:cancelled:{workflowId:D}` key shape + sentinel | ✓ VERIFIED | Exists; 4 facts pinning exact key string and `CancelledMarkerValue == "true"` |
| `tests/BaseApi.Tests/Orchestrator/BreakerMetricsFacts.cs` | Counter construction + no-`workflowId`-label cardinality guard | ✓ VERIFIED | Exists; asserts all 3 new counters non-null + MeterListener cardinality guard (`ProcessorId` present, `workflowId`/`WorkflowId` absent) |
| `tests/BaseApi.Tests/Processor/CheckAndDropFacts.cs` | Processor check-and-drop + dedup counter behavior | ✓ VERIFIED | Exists; 4 facts covering flag-Ack dedup counter, cancelled-set ack-and-discard, workflowId-keying, D-13 no-flag-touch |
| `tests/BaseApi.Tests/Processor/BreakerTriggerFacts.cs` | Breaker trigger behavior (trip/no-trip/business-throw) | ✓ VERIFIED | Exists; 3 facts: attempt==Limit trips (marker+counter+WARN+rethrow); attempt<Limit no-trip; business throw stays Failed |
| `tests/BaseApi.Tests/Processor/CancelledMarkerFacts.cs` | Marker write binds no-TTL `Expiration.Persist` overload | ✓ VERIFIED | Exists; 1 fact asserting `PERSIST` not `EX` |
| `tests/BaseApi.Tests/Orchestrator/ResultCheckAndDropFacts.cs` | Orchestrator check-and-drop + dedup counter | ✓ VERIFIED | Exists; 3 facts covering dedup counter, cancelled-set ack-and-discard, workflowId-keying |
| `tests/BaseApi.Tests/Orchestrator/FaultUnscheduleFacts.cs` | Fault consumer present/absent-L1 behavior | ✓ VERIFIED | Exists; 3 facts: present-in-L1 unschedules, absent-from-L1 no-op, keyed on correct WorkflowId |
| `tests/BaseApi.Tests/Orchestrator/FaultIdempotencyFacts.cs` | Duplicate-delivery idempotency + no-Redis-dep (D-13) | ✓ VERIFIED | Exists; 2 facts: two deliveries == one end state; ctor has exactly `WorkflowLifecycle` + `ILogger` (no Redis handle) |
| `tests/BaseApi.Tests/Orchestrator/CancelledCircuitBreakerE2ETests.cs` | Live E2E: breaker trip → halt → resume; authored + build-verified | ✓ AUTHORED (live run pending) | Exists, 653 lines, `[Trait("Category","RealStack")]`; contains `KeyTimeToLiveAsync`/`Assert.Null` (TTL==-1), "Fault halt" ES log assertion, DEL marker + re-POST resume, `WHERE "EntryCondition" = 3` count assertion, `skp:cancelled` in `L2KeysToCleanup` teardown |
| `scripts/phase-32-close.ps1` | 3×GREEN + triple-SHA with `skp:cancelled:*` scan-clean; parse-verified | ✓ AUTHORED (live run pending) | Exists, 359 lines; ParseFile-clean, BOM-free, `FLUSHDB` grep == 0; contains explicit `redis-cli --scan --pattern 'skp:cancelled:*' | del` before AFTER snapshot; retains unfiltered BEFORE/AFTER, 3-GREEN loop, triple-SHA |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `EntryStepDispatchConsumer` breaker catch | `L2ProjectionKeys.Cancelled(dispatch.WorkflowId)` | `db.StringSetAsync(..., Expiration.Persist)` | ✓ WIRED | Lines 255-258; no TTL overload |
| `EntryStepDispatchConsumer` breaker catch gate | `retryOptions.Value.Limit` | `ctx.GetRetryAttempt() == retryOptions.Value.Limit` | ✓ WIRED | Line 246; single source of truth |
| `EntryStepDispatchConsumer` check-and-drop | `L2ProjectionKeys.Cancelled(dispatch.WorkflowId)` | `db.StringGetAsync` at lines 94-96 | ✓ WIRED | After flag gate, before input resolution |
| `ResultConsumer` check-and-drop | `L2ProjectionKeys.Cancelled(m.WorkflowId)` | `db.StringGetAsync` at lines 80-82 | ✓ WIRED | After flag gate, before `store.TryGet` |
| `FaultUnscheduleConsumer` | `WorkflowLifecycle.UnscheduleOnlyAsync` | `context.Message.Message.WorkflowId` → lifecycle | ✓ WIRED | Double-`.Message` extraction proven by `FaultConsumerBindingFacts` |
| `Program.cs` | per-replica fan-out endpoint | `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` | ✓ WIRED | Lines 48-49; mirrors Start/Stop pattern |
| `ResultConsumer` flag gate | `metrics.ResultDeduped` | `ResultDeduped.Add(1, ProcessorId)` at line 70 | ✓ WIRED | Inside flag-Ack branch |
| `EntryStepDispatchConsumer` flag gate | `metrics.DispatchDeduped` | `DispatchDeduped.Add(1, ProcessorId)` at lines 83-84 | ✓ WIRED | Inside flag-Ack branch |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|-------------------|--------|
| `EntryStepDispatchConsumer` breaker catch | `dispatch.WorkflowId` fed to `Cancelled(workflowId)` key | `EntryStepDispatch` record (broker message) | Yes — server-minted GUID from the dispatch message | ✓ FLOWING |
| `ResultConsumer` check-and-drop | `m.WorkflowId` fed to `Cancelled(m.WorkflowId)` key | `ExecutionResult` record (broker message via `IExecutionCorrelated`) | Yes — same server-minted GUID | ✓ FLOWING |
| `FaultUnscheduleConsumer` | `context.Message.Message.WorkflowId` | `Fault<EntryStepDispatch>.Message` (MT auto-published) | Yes — proven by `FaultConsumerBindingFacts` real MT harness | ✓ FLOWING |
| Three new counters | `ProcessorId` tag value | `context.Id!.Value.ToString("D")` / `m.ProcessorId.ToString("D")` | Yes — real ctor-injected ids | ✓ FLOWING |

### Behavioral Spot-Checks

Step 7b SKIPPED for the live E2E behaviors — no live compose stack available. The hermetic suite (467/0 GREEN per 32-07-SUMMARY) verifies all non-RealStack behaviors. Live behaviors are routed to the human-verification gate.

### Requirements Coverage

| Requirement | Source Plan(s) | Description | Status | Evidence |
|-------------|---------------|-------------|--------|----------|
| req-1 | 32-01, 32-04 | Breaker trips only on infra-fault exhaustion; business throws stay Failed | ✓ SATISFIED | `GetRetryAttempt() == Limit` catch; `BreakerTriggerFacts` 3 facts |
| req-2 | 32-02, 32-04 | No-TTL `cancelled[workflowId]` marker set effect-first via `L2ProjectionKeys.Cancelled` | ✓ SATISFIED | `Expiration.Persist`; `CancelledMarkerKeyFacts` + `CancelledMarkerFacts`; live TTL==-1 pending gate |
| req-3 | 32-04, 32-05 | Both consumers ack-and-discard in-flight messages for a cancelled workflow | ✓ SATISFIED | `CheckAndDropFacts` + `ResultCheckAndDropFacts` |
| req-4 | 32-01, 32-06 | `Fault<EntryStepDispatch>` consumer on per-replica fan-out; extracts WorkflowId; unschedules via `UnscheduleOnlyAsync` | ✓ SATISFIED | `FaultUnscheduleFacts` + `FaultIdempotencyFacts`; Program.cs registration |
| req-5 | 32-07 | No `Cancelled` ExecutionResult on `orchestrator-result` on the breaker path; `_error` retained; token-cancellation unchanged | ✓ SATISFIED (code) / PENDING (live) | Only `SendResult(BuildCancelled)` at token-cancellation path (line 153); breaker catch re-throws only; live "Fault halt" log assertion in E2E pending gate |
| req-6 | 32-03 (CANCELLED/override) | `PreviousCancelled (3)` removal; `StepOutcome.Cancelled (3)` kept | PASSED (override) | User cancelled plan 32-03 (D-12 reversed); `StepOutcome.Cancelled = 3` confirmed; `PreviousCancelled = 3` deliberately retained; live data assertion (no live row with EntryCondition==3) pending gate |
| req-7 | 32-02, 32-04, 32-05 | Dedup counters + trip counter + 4-field WARN log; no `workflowId` label | ✓ SATISFIED | All 3 counters instrumented; `BreakerMetricsFacts` cardinality guard; WARN log at `EntryStepDispatchConsumer.cs:263-265` |
| req-8 | 32-06, 32-07 | Resume via clear-marker + re-Start; Fault path outside `flag[H]` dedup | ✓ SATISFIED (hermetic) / PENDING (live) | `FaultUnscheduleConsumer` has no Redis dep (ctor-shape assertion in `FaultIdempotencyFacts`); live resume sequence authored in `CancelledCircuitBreakerE2ETests.cs:221-227`; pending operator gate |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `FaultUnscheduleConsumerDefinition.cs:35` | 35 | Retry exhaustion publishes unconsumed `Fault<Fault<EntryStepDispatch>>` (WR-01 from code review) | ⚠️ Warning | Future-fire stop is best-effort against a persistently-faulting schedule owner; in-flight marker (req-3) still holds. Design-accepted; documented in REVIEW.md WR-01. Not a blocker. |

No blockers found. The WR-01 warning is a known design-accepted tradeoff (the R4 "bus/Redis down" backstop) documented in the code review; it does not prevent goal achievement.

### Human Verification Required

#### 1. Live Breaker-Trip → Halt → Resume + Close Gate (req-5, req-8-live, req-6-data)

**Test:** Rebuild the v3.6.0 containers and run the capstone:
```
docker compose up -d --build processor-sample orchestrator baseapi-service
dotnet test tests/BaseApi.Tests -- --filter-class "*CancelledCircuitBreakerE2ETests"
pwsh -NoProfile -File ./scripts/phase-32-close.ps1
```

**Expected:**
- `CancelledCircuitBreakerE2ETests` GREEN: breaker trips on the WRONGTYPE-poisoned output write; `GET skp:cancelled:{wf:D}` == `"true"` with `KeyTimeToLiveAsync == null` (TTL==-1, no-TTL marker); orchestrator ES log contains `"Fault halt — unscheduling workflow {WorkflowId}"` carrying the test's `workflowId`; ZERO processor downstream-effect logs for the poisoned correlationId on the breaker path; no new `skp:data:*` output key across a >1-minute window (Quartz job unscheduled); resume (DEL marker + re-POST `/api/v1/orchestration/start`) re-fires (fresh `skp:data:*` key appears); `SELECT COUNT(*) FROM "Steps" WHERE "EntryCondition" = 3` == 0.
- `phase-32-close.ps1` GATE_EXIT=0: 3×GREEN full suite (467 facts), Release+Debug 0/0, triple-SHA BEFORE==AFTER all three (psql `\l`, redis `--scan`, rabbitmqctl `list_queues`). The explicit `skp:cancelled:*` scan-clean between the settle-drain and the AFTER snapshot keeps the redis SHA stable.

**Why human:** Requires the full v3.6.0 docker compose stack running with REBUILT containers (embedded SourceHash must match the host build — the breaker catch and `FaultUnscheduleConsumer` are new this phase). Not runnable in this environment. This is the precedent set by Phase 31 (31-06 was similarly left as an operator gate).

### Gaps Summary

No gaps blocking goal achievement. All hermetic/code verifications pass (467/0 GREEN). The only pending items are the live operator gate (Task 3 of Plan 07) and the user-overridden req-6 half (PreviousCancelled deliberately retained per 32-03-SUMMARY.md). The phase is fully authored, committed, and build-verified; the live gate is the final closure step.

---

_Verified: 2026-06-04T20:48:12Z_
_Verifier: Claude (gsd-verifier)_
