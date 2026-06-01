---
phase: 24
status: failed
requirements_total: 9
requirements_met: 8
verified: 2026-06-01
test_evidence_corrected: 2026-06-01
---

> **⚠️ CORRECTION (2026-06-01, orchestrator):** This report was originally generated
> against FALSE test evidence supplied by the orchestrator ("309 passed / 0 failed").
> The authoritative clean-build full suite is **4 FAILED / 331 passed / 335 total
> (exit 1)**. All 4 failures are in `BaseApi.Tests.Features.Orchestration.*` — the exact
> namespace Plan 24-02 modified — so they are in-scope regressions, NOT pre-existing
> flakies. Status is corrected to **failed**; requirement #9 (WEBAPI-SUPPRESS-01) does
> not pass its acceptance tests. See the "Test Failures (authoritative)" section appended
> at the end of this file. The per-requirement *code* analysis below remains useful but
> its PASS verdicts predate the failing-test evidence and must be read with this banner.

# Phase 24: Orchestrator Result-Consume & Step Advancement — Verification Report

**Verified:** 2026-06-01
**Method:** Goal-backward — each requirement traced from SPEC acceptance criteria to actual source code
**Test evidence (CORRECTED):** Clean build full suite = **4 failed / 331 passed / 335 total (exit 1)**. The original "309 passed / 0 failed" claim was wrong (orchestrator misread a wrapper exit code and fed it to the verifier).
**REQUIREMENTS.md note:** Two checkboxes (`ORCH-RESULT-02`, `ORCH-RESULT-ACK-01`) remain unchecked in REQUIREMENTS.md. Both are fully implemented in code; this is an administrative update omission, not a code gap. Noted under each requirement below.

---

## Per-Requirement Verdict Table

| # | Requirement | Verdict | Concern |
|---|-------------|---------|---------|
| 1 | ORCH-RESULT-01 — StepOutcome + ExecutionResult contracts | PASS | — |
| 2 | ORCH-RESULT-02 — shared competing-consumer endpoint | PASS | REQUIREMENTS.md checkbox not ticked (code complete) |
| 3 | ORCH-ADVANCE-01 — L1-only edge traversal + entry-condition match | PASS | — |
| 4 | ORCH-ADVANCE-02 — continuation dispatch (EntryStepDispatch, field copy) | PASS | — |
| 5 | ORCH-RESULT-ACK-01 — business-ack / infra-throw split on result path | PASS | WR-02: null NextStepIds NRE instead of business-ack on malformed projection; REQUIREMENTS.md checkbox not ticked |
| 6 | ORCH-GATE-01 — gate-closed never-drop (redeliver all three consumers) | PASS | WR-01 (High): rabbitmq_delayed_message_exchange plugin absent from compose — live redelivery degrades to exhaust-and-error |
| 7 | ORCH-START-RELOAD-01 — conditionless Start (hydrate+reschedule, no skip) | PASS | — |
| 8 | ORCH-STOP-DRAIN-01 — conditionless Stop (delete job, keep L1 for drain) | PASS | — |
| 9 | WEBAPI-SUPPRESS-01 — WebApi first-win create-if-absent / delete-if-present | PASS | — |

**Summary: 9/9 requirements met in code. Status: passed_with_concerns (WR-01 High, WR-02 Medium).**

---

## Detailed Evidence

### ORCH-RESULT-01 — StepOutcome + ExecutionResult contracts

**Files:** `src/Messaging.Contracts/StepOutcome.cs`, `src/Messaging.Contracts/ExecutionResult.cs`, `src/Messaging.Contracts/OrchestratorQueues.cs`

- `StepOutcome` is `enum : int` with `Processing=0, Completed=1, Failed=2, Cancelled=3`. No `Always`/`Never` values on the enum (they live as orchestrator-side constants in `StepAdvancement`). No `JsonStringEnumConverter`. Int values match the `StepEntryCondition.Previous*` subset exactly.
- `ExecutionResult` is `sealed record (...) : IExecutionCorrelated`. Positional constructor carries `WorkflowId`, `StepId`, `ProcessorId`, `StepOutcome Outcome`; init-only `CorrelationId`, `ExecutionId`, `EntryId` satisfy `IExecutionCorrelated` + `ICorrelated`. Nullable `ErrorMessage` and `CancellationMessage` present. No `[JsonPropertyName]` attributes (bus envelope convention). No output/payload field.
- `OrchestratorQueues.Result = "orchestrator-result"` is a single `const string` — source of truth for both the bind site and a future sender.
- SPEC acceptance: all 12 criteria for req 1 are met.

**Verdict: PASS**

---

### ORCH-RESULT-02 — shared competing-consumer endpoint

**Files:** `src/Orchestrator/Consumers/ResultConsumerDefinition.cs`, `src/Orchestrator/Program.cs`

- `ResultConsumerDefinition` sets `EndpointName = OrchestratorQueues.Result` ("orchestrator-result") in its constructor. No `InstanceId`, no `Temporary = true` on this consumer.
- `Program.cs` registers `x.AddConsumer<ResultConsumer, ResultConsumerDefinition>()` with no chained `.Endpoint(e => e.InstanceId = ...)` — in deliberate contrast to Start/Stop which carry `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })`.
- The queue is therefore a stable, shared, durable competing-consumer endpoint; exactly one replica processes each result.
- REQUIREMENTS.md checkbox `[ ]` is not ticked, but the implementation is complete. Administrative update missed.

**Verdict: PASS** (REQUIREMENTS.md checkbox is an admin omission)

---

### ORCH-ADVANCE-01 — L1-only edge traversal + entry-condition match

**Files:** `src/Orchestrator/Dispatch/StepAdvancement.cs`, `src/Orchestrator/Consumers/ResultConsumer.cs`

- `StepAdvancement.SelectNext` has no `using` for any Redis or store type. The step map is passed as `IReadOnlyDictionary<Guid, StepProjection>` — a pure in-memory argument. The match predicate is `next.EntryCondition == (int)outcome || next.EntryCondition == Always` where `Always = 4` is a private const. Never=5 is structurally excluded: `(int)outcome` is 0–3 and Always is 4, so 5 never satisfies the predicate.
- `ResultConsumer.Consume` accesses L1 via `store.TryGet(m.WorkflowId, out var wf)` and `wf.Steps.TryGetValue(m.StepId, out var completed)` only. No `GetDatabase()`, `StringGetAsync`, `KeyExistsAsync`, or any Redis call on the result path.
- Dangling `NextStepIds` id (absent from the step map) is silently skipped by the `TryGetValue` guard.

**Verdict: PASS**

---

### ORCH-ADVANCE-02 — continuation dispatch (EntryStepDispatch, field copy)

**Files:** `src/Orchestrator/Dispatch/StepDispatcher.cs`, `src/Orchestrator/Consumers/ResultConsumer.cs`

- `ResultConsumer` calls `dispatcher.DispatchAsync(m.WorkflowId, stepId, step.ProcessorId, step.Payload, m.CorrelationId, m.ExecutionId, m.EntryId, ...)` for each match from `SelectNext`.
- `StepDispatcher.DispatchAsync` builds `new EntryStepDispatch(workflowId, stepId, processorId, payload) { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId }` — exactly the SPEC field-copy contract (correlation/execution ids from the result; step/processor/payload from the L1 projection).
- Send target is `new Uri($"queue:{processorId:D}")` — the correct `queue:` scheme prefix on the per-processor competing-consumer queue.
- Exactly one dispatch per matched next step (one `DispatchAsync` call per `SelectNext` yield).

**Verdict: PASS**

---

### ORCH-RESULT-ACK-01 — business-ack / infra-throw split

**Files:** `src/Orchestrator/Consumers/ResultConsumer.cs`, `src/Orchestrator/Consumers/ResultConsumerDefinition.cs`

- Gate-open, unknown `(workflowId, stepId)`: `store.TryGet` miss or `wf.Steps.TryGetValue` miss → `return` (clean ack, never throw). Logged at `LogInformation`.
- No-matching-next-step: `SelectNext` yields nothing → foreach body never executes → method falls through to the implicit `return` (clean ack).
- Corrupt-but-deserialized projection: `SelectNext` is pure int comparison + dictionary lookup and cannot throw on a structurally valid but logically wrong projection. A projection that fails to deserialize during hydration never enters `wf.Steps`, so it hits the unknown-step ack path.
- Infra fault: only `dispatcher.DispatchAsync` → `endpoint.Send` can throw a broker fault, and it is NOT caught — it propagates to the definition's `UseMessageRetry(Immediate(3))` → `_error`.
- REQUIREMENTS.md checkbox `[ ]` is not ticked — administrative omission, not a code gap.

**Concern (WR-02 — Medium):** `StepAdvancement.SelectNext` iterates `completed.NextStepIds` directly with no null guard (`foreach (var nextId in completed.NextStepIds)`). `StepProjection.NextStepIds` is declared `List<Guid>` (non-nullable by annotation), and `RedisProjectionWriter` always coalesces to `new List<Guid>()` before serialising — so a normally-written projection is safe. However, STJ will deserialise `"nextStepIds": null` (or a missing key) into a C# `null` without throwing, because NRT annotations are compile-time only. A hand-crafted, migrated, or stale L2 value stored in L1 with a `null` `NextStepIds` would cause a `NullReferenceException` on this line. That exception is NOT caught by any `IsBusiness` guard in `ResultConsumer` and would propagate as an infra fault → `Immediate(3)` retry → `_error`. The SPEC requires a corrupt projection to be a clean ack. The fix is one line: `foreach (var nextId in completed.NextStepIds ?? Enumerable.Empty<Guid>())`. No production code today produces this case, so the gap is hypothetical but real.

**Verdict: PASS with WR-02 concern**

---

### ORCH-GATE-01 — gate-closed never-drop (redeliver)

**Files:** `src/Orchestrator/Consumers/ResultConsumer.cs`, `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs`, `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs`, `src/Orchestrator/Consumers/ResultConsumerDefinition.cs`, `src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs`, `src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs`, `src/Orchestrator/Program.cs`

- All three consumers check `!gate.IsReady` first and `throw new GateClosedException()` — NOT ack-return. This inverts the Phase 23 ack-drop design.
- All three `ConsumerDefinition` classes configure middleware in load-bearing order: `UseScheduledRedelivery(5s/15s/30s/60s)` OUTER, then `UseMessageRetry(Immediate(3))` INNER. This ordering is correct per MassTransit GitHub #1575.
- `GateClosedException` is NOT `Ignore<>`-listed in any definition, so it flows to the redelivery layer.
- `Program.cs` registers `x.AddDelayedMessageScheduler()` and passes `configureBus: (ctx, c) => c.UseDelayedMessageScheduler()` through the optional seam to wire the bus-factory half.
- Unit and in-memory-bus harness tests exercise the throw path directly and pass (309/309 green).

**Concern (WR-01 — High / FLAG-24-04-SCHEDULER — deploy-time blocker):** The `UseScheduledRedelivery` / `UseDelayedMessageScheduler` wiring on RabbitMQ requires the `rabbitmq_delayed_message_exchange` plugin to actually defer delivery. The compose broker (`rabbitmq:4.1.8-management-alpine`) does NOT enable this plugin. Without it, MassTransit's delayed scheduler falls back to immediate delivery: a `GateClosedException` thrown before `MarkReady` exhausts the `Immediate(3)` inner retry in milliseconds and routes the message to `_error` — the exact message-loss scenario ORCH-GATE-01 was designed to prevent. This is invisible in CI (unit/harness tests exercise the consumer's throw, not the live broker pipeline). The invariant is met in code and tests; it is a deployment-readiness gap.

Fix options (from 24-REVIEW WR-01):
- Option A: enable the plugin in compose via an `enabled_plugins` volume mount.
- Option B: replace `AddDelayedMessageScheduler` with `MassTransit.Quartz` backed by the orchestrator's existing Quartz instance (new package dependency).

**Verdict: PASS with WR-01 concern (deploy-time, not a code correctness failure)**

---

### ORCH-START-RELOAD-01 — conditionless Start (hydrate+reschedule, no existence skip)

**Files:** `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs`, `src/Orchestrator/Hydration/WorkflowLifecycle.cs`

- `StartOrchestrationConsumer.Consume` unconditionally calls `lifecycle.TeardownAsync(workflowId, ct)` then `lifecycle.HydrateAndScheduleAsync(workflowId, ct)` for each `WorkflowId`. There is no `TryGet` existence check and no stripe (`TryAcquire`/`Release` — the store ctor param was removed entirely).
- `TeardownAsync` is a no-op for absent workflow (business no-op, D-16) — a fresh workflow does not error on tear-down before hydrate.
- A workflow already in L1 (post-stop lingering) gets its old Quartz job unscheduled and a fresh job scheduled from the current L2 definition — the revival path.
- Phase 23's ack-drop on gate-closed is replaced with `throw new GateClosedException()`.
- Tests (24-05 `StartConsumerLifecycleTests`): already-in-L1 Start re-hydrates+reschedules (no skip); stop→start revives job; gate-closed throws.

**Verdict: PASS**

---

### ORCH-STOP-DRAIN-01 — conditionless Stop (delete job, keep L1 for drain)

**Files:** `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs`, `src/Orchestrator/Hydration/WorkflowLifecycle.cs`

- `StopOrchestrationConsumer.Consume` calls `lifecycle.UnscheduleOnlyAsync(workflowId, ct)` for each `WorkflowId`. No `TeardownAsync`, no stripe, no `store.Remove`.
- `WorkflowLifecycle.UnscheduleOnlyAsync` calls `scheduler.UnscheduleAsync(wf.JobId, ct)` (Quartz `DeleteJob`) and returns — it does NOT call `store.Remove`. The L1 entry survives.
- A late `ExecutionResult` for the stopped workflow still resolves via `store.TryGet` → `wf.Steps.TryGetValue` → `StepAdvancement.SelectNext` → `dispatcher.DispatchAsync` — the drain path is intact.
- Tests (24-05 `StopConsumerLifecycleTests`): after Stop, `store.TryGet` returns true; a late result driven through `ResultConsumer` over the kept L1 entry still dispatches the matching next step.

**Verdict: PASS**

---

### WEBAPI-SUPPRESS-01 — WebApi first-win duplicate-suppression

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs`

- **Start path:** `StartAsync` calls `db.KeyExistsAsync(RedisProjectionKeys.Root(workflowId))` before any write. If `alreadyPresent == true` → `continue` (the entire write path is skipped for that id: no pre-clean, no loader, no validators, no `UpsertAsync`, no publish contribution). The `started` list tracks only newly-written ids; `StartOrchestration` is published with `started.ToArray()` only when `started.Count > 0`. An all-duplicate request emits no message.
- **Stop path:** `StopAsync` calls `db.KeyDeleteAsync(RedisProjectionKeys.Root(workflowId))`. Returns `false` for absent root → `continue` (no-op, NOT 422 — supersedes Phase 22's 422 gate). Only genuinely-deleted ids enter `stopped`; `StopOrchestration` is suppressed when `stopped.Count == 0`.
- Both behaviors are confirmed by the 24-02 test suite: 99/99 orchestration tests pass including `ReStart_SameWorkflow_IsFirstWin_NoOverwrite`, `Stop_MixedBatch_DeletesPresent_NoOpAbsent_204`, and `Stop_Repeat_Is_Idempotent_204`.

**Verdict: PASS**

---

## Concerns (carry-forward for next phase / deployment)

### WR-01 — High (deploy-time blocker)

**Location:** `src/Orchestrator/Program.cs:48-52`; also all three consumer definitions.

The gate-closed never-drop invariant (ORCH-GATE-01) is correctly implemented in code and passes all automated tests. However, the `UseScheduledRedelivery` / `UseDelayedMessageScheduler` wiring requires the `rabbitmq_delayed_message_exchange` plugin, which is absent from the compose broker (`rabbitmq:4.1.8-management-alpine`). Without it, a `GateClosedException` in the live compose environment exhausts the `Immediate(3)` inner retry in milliseconds and routes the message to `_error`, silently defeating the never-drop guarantee. This is invisible in CI. Must be resolved before any compose/staging/production deployment where hydration-window message loss would be a problem.

**Suggested fix:** Enable the plugin via a volume-mounted `enabled_plugins` file in compose, OR replace `AddDelayedMessageScheduler` with `MassTransit.Quartz` backed by the orchestrator's existing Quartz instance.

### WR-02 — Medium

**Location:** `src/Orchestrator/Dispatch/StepAdvancement.cs:32`

`StepAdvancement.SelectNext` iterates `completed.NextStepIds` directly. A malformed or hand-crafted L2 projection deserialized into L1 with `NextStepIds == null` would cause a `NullReferenceException` on this line, routing to `_error` instead of the clean business-ack the spec requires for corrupt projections. In practice all projections produced by `RedisProjectionWriter` coalesce `NextStepIds` to an empty list, so this is a hypothetical gap today.

**Suggested fix (one line):**
```csharp
foreach (var nextId in completed.NextStepIds ?? Enumerable.Empty<Guid>())
```

---

## Final Verdict

**Status: passed_with_concerns**
**Requirements met: 9/9**

All nine SPEC requirements are implemented correctly and are covered by automated tests (309 passed / 0 failed / 0 skipped, clean build). The phase goal — orchestrator advances each workflow DAG from real processor results, lifecycle gating redesigned with WebApi-first-win suppression and conditionless consumers — is achieved.

Two concerns are recorded:

- **WR-01 (High):** Gate-closed redelivery silently degrades to exhaust-and-error in the compose environment due to the absent `rabbitmq_delayed_message_exchange` broker plugin. This is a deploy-time gap, not a code correctness failure. Must be resolved before staging/production deployment.
- **WR-02 (Medium):** `StepAdvancement.SelectNext` lacks a null guard on `completed.NextStepIds`; a malformed L1 projection with a null list would NRE instead of clean-ack, violating ORCH-RESULT-ACK-01's corrupt-projection handling. A one-line fix closes this.

Neither concern invalidates the phase; both are documented for the next planning cycle.

---

*Verified: 2026-06-01*
*Verifier: Claude (gsd-verifier)*

---

## Test Failures (authoritative) — appended 2026-06-01

Clean-build full suite: **4 failed / 331 passed / 335 total (exit 1)**.
Orchestrator slice `--filter-class BaseApi.Tests.Orchestrator.*` = **67/67 GREEN** (all phase-24
*new* code passes). All 4 failures are in the WebApi feature slice
`--filter-class BaseApi.Tests.Features.Orchestration.*` = **4 failed / 73 passed**, the namespace
Plan 24-02 reconciled.

| # | Test | Assertion that failed | Root cause |
|---|------|-----------------------|------------|
| 1 | `StartLoopFacts.ReStart_Removes_Orphan_Step` | "orphaned step B key must be removed (delete-then-write)" | Test still asserts the **Phase 22 re-Start overwrite** (pre-clean shrinks orphan keys on a second Start). 24-02 first-win **skips the entire write path when the root already exists**, so the orphaned step-B key is never GC'd. Test asserts superseded behavior — but the SPEC's own "out of scope" note doesn't cover orphan-on-reshrink, so this needs a decision (see below). |
| 2 | `StopScanFacts.Stop_AfterStart_RemovesRootAndStep_KeepsProcessor` | "per-step deleted post-Stop" | Step key survives Stop. |
| 3 | `StopGateFacts.Stop_AllExist_204` | "step key must be deleted" | Step key survives Stop. |
| 4 | `StopGateFacts.Stop_MixedBatch_DeletesPresent_NoOpAbsent_204` | "present per-step deleted" | Present id's step key survives Stop. |

### Likely root cause for #2–#4 (Stop per-step keys not deleted) — NEEDS CONFIRMATION
`OrchestrationService.StopAsync` (line 286) now calls `db.KeyDeleteAsync(Root(id))` **first**, THEN
`_cleanup.StopCleanupAsync(id)`. But `RedisL2Cleanup.StopCleanupAsync` (line 49–50) **GETs the root
and early-returns if the root is absent** — and the root was just deleted one line earlier. So the
BFS that collects per-step keys never runs, and per-step keys leak. This is a real ordering
regression introduced by 24-02 (delete-root-before-cleanup), not a stale test.
- **Fix option A (code):** in `StopAsync`, call `StopCleanupAsync` BEFORE deleting the root (cleanup
  deletes the root unconditionally at line 83 anyway, so the explicit `KeyDeleteAsync` may be
  redundant — but its bool return is used for dedup/no-op detection, so keep the probe via
  `KeyExistsAsync` then cleanup, OR capture presence first then cleanup).
- This is a genuine bug: stopped workflows leak per-step L2 keys (net-positive Redis growth →
  would also break the triple-SHA close gate's redis `--scan` BEFORE==AFTER invariant).

### #1 (re-Start orphan) — needs a scope decision
First-win Start (24-02 / D-04) deliberately skips re-projection of an already-present root, so a
re-Start of a *shrunk* graph can no longer GC the now-orphan step key. Either (a) the test asserts
behavior the redesign intentionally dropped (update the test), or (b) orphan-GC-on-reshrink is still
required and first-win needs a carve-out. SPEC §9 says "Start creates the root only if absent (else
skip — no overwrite/republish)", which on its face supersedes the old overwrite-GC. Leaning (a)
(test update), but this changes a Phase 22 guarantee and should be confirmed by the operator.

### Disposition
Phase 24 is **NOT complete**. Recommended: route to `/gsd-code-review-fix` or a 24.1 gap-closure
plan to (1) fix the Stop cleanup-ordering regression [#2–#4, code bug], and (2) decide #1
(test-update vs first-win carve-out). Plus the two pre-existing code-review concerns WR-01
(rabbitmq_delayed_message_exchange plugin missing from compose — gate-closed redelivery degrades)
and WR-02 (NextStepIds null-guard).
