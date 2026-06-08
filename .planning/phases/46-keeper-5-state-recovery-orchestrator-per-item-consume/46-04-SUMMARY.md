---
phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume
plan: 04
subsystem: orchestrator
tags: [masstransit, dotnet, orchestrator, consumers, advancement, generics, tdd, orch-01]

# Dependency graph
requires:
  - phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume
    plan: 01
    provides: TypedResultConsumerFacts RED stub (Wave 0) + the four typed Step* result records
  - phase: 43-message-contracts-l2-key-reshape
    provides: IStepResult marker + StepOutcome int mapping (Processing=0/Completed=1/Failed=2/Cancelled=3)
provides:
  - TypedResultConsumer<TMessage> generic advancement base (the ResultConsumer body with the hardcoded outcome replaced by an abstract Outcome knob)
  - Four sealed Outcome-knob subclasses (StepCompleted/Failed/Cancelled/Processing) on OrchestratorQueues.Result
  - Four ConsumerDefinitions on the shared orchestrator-result endpoint (single-owner UseMessageRetry)
  - ORCH-01 indistinguishability proof (a Keeper-INJECT'd StepCompleted == a direct one)
affects: [47-resilience-dlq1, 48-retire-reactive-recovery]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Per-item result advancement is routed purely by message TYPE via an abstract Outcome knob — no status if/switch anywhere"
    - "Four competing consumers co-locate on one shared endpoint; exactly ONE definition owns the per-endpoint UseMessageRetry, siblings are intentional no-ops (Pitfall 4)"
    - "A reconstructed (Keeper-INJECT) result record is byte-indistinguishable from a direct one — same record type, same queue, same consumer"

key-files:
  created:
    - src/Orchestrator/Consumers/TypedResultConsumer.cs
    - src/Orchestrator/Consumers/StepCompletedConsumer.cs
    - src/Orchestrator/Consumers/StepFailedConsumer.cs
    - src/Orchestrator/Consumers/StepCancelledConsumer.cs
    - src/Orchestrator/Consumers/StepProcessingConsumer.cs
    - src/Orchestrator/Consumers/StepCompletedConsumerDefinition.cs
    - src/Orchestrator/Consumers/StepFailedConsumerDefinition.cs
    - src/Orchestrator/Consumers/StepCancelledConsumerDefinition.cs
    - src/Orchestrator/Consumers/StepProcessingConsumerDefinition.cs
  modified:
    - src/Orchestrator/Program.cs
    - src/Orchestrator/Observability/OrchestratorMetrics.cs
    - tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs
    - tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs
    - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
    - tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs
  deleted:
    - src/Orchestrator/Consumers/ResultConsumer.cs
    - src/Orchestrator/Consumers/ResultConsumerDefinition.cs

key-decisions:
  - "D-07: the ONLY per-type knob is `protected abstract StepOutcome Outcome` — the four subclasses are one-liners and there is NO status if/switch anywhere (grep confirms only doc-comment mentions)"
  - "Single-owner retry: StepCompletedConsumerDefinition registers the shared-endpoint UseMessageRetry; the other three ConfigureConsumer are intentional no-ops (Pitfall 4)"
  - "Rule 3 deviation: ResultConsumer.cs deletion deferred from Task 1 into Task 2 so every commit builds; three existing tests migrated to StepCompletedConsumer in the same commit to keep the test project compiling"

requirements-completed: [ORCH-01]

# Metrics
duration: 8min
completed: 2026-06-08
---

# Phase 46 Plan 04: Orchestrator Per-Item Typed Result Consume Summary

**Replaced the single straight-through `ResultConsumer` with the `TypedResultConsumer<TMessage>` family — a generic advancement base (the verbatim `ResultConsumer.Consume` body with the hardcoded `StepOutcome.Completed` swapped for an abstract `Outcome` knob) plus four sealed one-line subclasses and four thin definitions on the shared `orchestrator-result` endpoint, with the ORCH-01 indistinguishability requirement proven green.**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-06-08T20:59Z
- **Completed:** 2026-06-08T21:07Z
- **Tasks:** 3
- **Files:** 15 (9 created, 6 modified, 2 deleted)

## Accomplishments
- **TypedResultConsumer<TMessage>** (`where TMessage : class, IStepResult`): the `ResultConsumer.Consume` body lifted verbatim — retained `ResultConsumed` metric at the top, L1-miss graceful business-ack (log + return, never throw), `StepAdvancement.SelectNext(Outcome, …)` traversal, and `DispatchAsync` per matched successor preserving correlationId/workflowId, regenerating executionId, and seeding `entryId = m.EntryId`. The single line that changed: hardcoded `StepOutcome.Completed` → `protected abstract StepOutcome Outcome`.
- **Four sealed subclasses** — `StepCompletedConsumer` (Completed), `StepFailedConsumer` (Failed), `StepCancelledConsumer` (Cancelled), `StepProcessingConsumer` (Processing) — each a primary-ctor pass-through of the five deps + the one-line Outcome override. `StepCompletedConsumer` replaces the old `ResultConsumer`.
- **Four ConsumerDefinitions** on `OrchestratorQueues.Result` ("orchestrator-result") as competing consumers; `StepCompletedConsumerDefinition` owns the single endpoint-level `UseMessageRetry(Immediate(Retry:Limit))`, the other three are intentional no-ops (Pitfall 4).
- **Program.cs**: the single `AddConsumer<ResultConsumer, ResultConsumerDefinition>()` replaced by the four `AddConsumer<Step*Consumer, Step*ConsumerDefinition>()` registrations; surrounding PauseAll/ResumeAll and the competing-consumer comment intact.
- **ResultConsumer.cs + ResultConsumerDefinition.cs deleted** (replaced).
- **TypedResultConsumerFacts green**: a Theory over all four subclasses (each advances ONLY its outcome-gated successor), a cross-outcome isolation fact (a Failed-gated successor advances for `StepFailedConsumer` but NOT `StepCompletedConsumer` over the same L1), an L1-miss graceful-ack fact, and the **ORCH-01 indistinguishability** fact (a Keeper-INJECT-style `StepCompleted` produces byte-identical `DispatchAsync` effects to a direct one).

## Task Commits

1. **Task 1: TypedResultConsumer<T> base + four sealed subclasses** - `a9d387a` (feat)
2. **Task 2: four ConsumerDefinitions + Program swap; delete ResultConsumer(Definition)** - `c5b56cb` (feat)
3. **Task 3: TypedResultConsumerFacts green (routing + ORCH-01)** - `4425ef8` (test)

## Decisions Made
- The four subclass bodies carry zero branching: routing is entirely the compile-time `Outcome` constant feeding `SelectNext`. A grep for `if`/`switch` across the four `Step*Consumer.cs` files finds only doc-comment text; the lone `if` in the family is the L1-existence guard in the base (an L1 lookup, not a status branch).
- All four definitions co-locate on one shared endpoint. Following the established `FaultEntryStepDispatchConsumerDefinition` single-owner convention, only `StepCompletedConsumerDefinition` registers `UseMessageRetry`; the three siblings expose no `ConfigureConsumer` override (an empty/no-op definition body), so the per-endpoint retry is registered exactly once.
- The ORCH-01 indistinguishability assertion was written against record value-equality (`Assert.Equal(direct, injected)`) PLUS field-by-field equality of the captured `DispatchAsync` args (excluding the deliberately-regenerated `executionId`).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Deferred ResultConsumer.cs deletion from Task 1 to Task 2 to keep every commit buildable**
- **Found during:** Task 1
- **Issue:** The plan's Task 1 deletes `ResultConsumer.cs`, but `ResultConsumerDefinition.cs` and `Program.cs` (Task-2 edits) still reference it — deleting it in the Task-1 commit would leave `Orchestrator.csproj` non-buildable at that commit, violating the per-task build-green requirement.
- **Fix:** Task 1 added the five new consumer files only (Orchestrator built green alongside the still-present `ResultConsumer`). Task 2 deleted `ResultConsumer.cs` + `ResultConsumerDefinition.cs` together with the new definitions and the `Program.cs` swap, so that commit also builds green. Net end state is identical to the plan.
- **Files modified:** task boundary only — no extra production code.
- **Commit:** `a9d387a` (Task 1), `c5b56cb` (Task 2)

**2. [Rule 3 - Blocking] Migrated three existing tests off the deleted ResultConsumer**
- **Found during:** Task 2
- **Issue:** `ResultConsumeTests.cs`, `ResultAckTests.cs`, and `StopConsumerLifecycleTests.cs` instantiate the now-deleted `ResultConsumer` type, breaking the test project build. (The plan listed only the three Task-files; these three were not enumerated but transitively depend on the deleted type.)
- **Fix:** Repointed each to `StepCompletedConsumer` (the exact Completed-outcome behavioral replacement — same five-dep ctor, `ILogger<StepCompleted>`); updated the doc-comments to reference the D-07 replacement. All three suites pass unchanged (9/9).
- **Files modified:** `tests/.../ResultConsumeTests.cs`, `tests/.../ResultAckTests.cs`, `tests/.../StopConsumerLifecycleTests.cs`
- **Commit:** `c5b56cb`

**3. [Rule 1 - Doc accuracy] Updated stale OrchestratorMetrics doc-comment**
- **Found during:** Task 2
- **Issue:** `OrchestratorMetrics.ResultConsumed` doc-comment said "incremented at the TOP of ResultConsumer.Consume" — a now-deleted type.
- **Fix:** Updated to `TypedResultConsumer<T>.Consume`.
- **Files modified:** `src/Orchestrator/Observability/OrchestratorMetrics.cs`
- **Commit:** `c5b56cb`

## Threat Surface

All three plan threats are addressed by the as-built consumer family:
- **T-46-07 (DoS — poison-message loop):** single-owner `UseMessageRetry(Immediate(Limit))` on the shared endpoint → `_error`/`skp-dlq-1`; an L1 miss is a graceful business-ack (return), so it cannot loop. Unchanged from the retired `ResultConsumer` posture.
- **T-46-08 (Spoofing — INTENDED indistinguishability):** the ORCH-01 fact deliberately proves a reconstructed `StepCompleted` is processed identically — accept, by design.
- **T-46-09 (Tampering — wrong-outcome advancement):** the `Outcome` knob is a compile-time constant per subclass (no runtime status field to tamper); `SelectNext` is pure int-match. The Theory pins each subclass's outcome routing.

No new security surface beyond the plan's threat model — the change is a refactor of an existing trusted-internal-queue consumer into a typed family on the same endpoint.

## Verification
- `dotnet build SK_P.sln`: **Build succeeded, 0 warnings, 0 errors**.
- `dotnet build src/Orchestrator/Orchestrator.csproj`: green at Task 1 and Task 2.
- `TypedResultConsumerFacts` (MTP `--filter-class "*TypedResultConsumerFacts*"`): **7/7 passed** (4 Theory cases + 3 facts).
- Migrated suites (`ResultConsumeTests` + `ResultAckTests` + `StopConsumerLifecycleTests`): **9/9 passed**.
- grep: no status `if`/`switch` in the four `Step*Consumer.cs` files (doc-comment mentions only); no live `ResultConsumer`/`ResultConsumerDefinition` type usage remains (only historical doc-comments).

Note: the test project uses Microsoft.Testing.Platform (xunit.v3), which ignores the VSTest `--filter` flag — filters applied via `dotnet run -- --filter-class`. A bare full `dotnet test` shows 2 PRE-EXISTING unrelated E2E failures (`SampleRoundTripE2ETests`, `MetricsRoundTripE2ETests`, need docker) — not regressions from this plan.

## Self-Check: PASSED

- All 9 created files + 2 deletions verified on disk/git; the four definition files + base + four subclasses present, `ResultConsumer.cs`/`ResultConsumerDefinition.cs` removed.
- All 3 task commits verified in git history: `a9d387a`, `c5b56cb`, `4425ef8`.

---
*Phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume*
*Completed: 2026-06-08*
