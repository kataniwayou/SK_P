---
phase: 45-keeper-bit-health-gate-global-pause-resume
plan: "02"
subsystem: orchestrator-global-pause-resume
tags: [orchestrator, masstransit, quartz, fan-out, pause-all, resume-all, no-burst, idempotent, edge-trigger-consumer]

# Dependency graph
requires:
  - phase: 45-00
    provides: PauseAll/ResumeAll no-H broadcast contracts + Orchestrator Consumers RED test stubs
  - phase: 23 (Orchestrator lifecycle)
    provides: WorkflowScheduler (IScheduler seam), WorkflowLifecycle.ResumeAsync (Paused-guard + fresh-from-now reschedule), IWorkflowL1Store.WorkflowIds snapshot
  - phase: 37 (Orchestrator pause/resume coordination)
    provides: deterministic TriggerKey model + the per-workflow orchestrator-pauseresume fan-out endpoint (the analog this plan mirrors on a NEW endpoint)
provides:
  - Orchestrator.Scheduling.WorkflowScheduler.PauseAllAsync     # thin scheduler-wide pause seam over IScheduler.PauseAll()
  - Orchestrator.Consumers.PauseAllConsumer                     # ORCH-02 scheduler-wide idempotent pause
  - Orchestrator.Consumers.ResumeAllConsumer                    # ORCH-02 per-job resume over the L1 snapshot (never native ResumeAll)
  - Orchestrator.Consumers.PauseAllConsumerDefinition           # retry owner on orchestrator-global-pauseresume
  - Orchestrator.Consumers.ResumeAllConsumerDefinition          # non-owner sharing the new endpoint
affects:
  - 46  # Phase-46 gate-open-only recovery consumer runs alongside these global control consumers
  - 48  # Phase-48 drops the old per-workflow orchestrator-pauseresume endpoint (D-08 independence makes this clean)

# Tech tracking
tech-stack:
  added: []   # no new NuGet packages
  patterns:
    - "Scheduler-wide pause via a thin expression-bodied PauseAllAsync seam over IScheduler.PauseAll() — idempotent (re-pause = Quartz no-op)"
    - "Per-job resume over the L1 IWorkflowL1Store.WorkflowIds snapshot — NEVER native scheduler.ResumeAll() (no cross-workflow catch-up herd); idempotency + no-burst inherited free from WorkflowLifecycle.ResumeAsync's TriggerState==Paused guard + fresh-from-now reschedule"
    - "Two ConsumerDefinitions on ONE new per-replica fan-out endpoint orchestrator-global-pauseresume, with UseMessageRetry owned ONLY by the Pause def (per-endpoint retry, single ownership) — Resume def sets ConcurrentMessageLimit=1 only"
    - "Per-replica fan-out registration reusing the shared instanceId (Endpoint InstanceId + Temporary) so Publish reaches every replica — not competing-consumer"

key-files:
  created:
    - src/Orchestrator/Consumers/PauseAllConsumer.cs
    - src/Orchestrator/Consumers/ResumeAllConsumer.cs
    - src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs
    - src/Orchestrator/Consumers/ResumeAllConsumerDefinition.cs
  modified:
    - src/Orchestrator/Scheduling/WorkflowScheduler.cs
    - src/Orchestrator/Program.cs
    - tests/BaseApi.Tests/Orchestrator/Consumers/PauseAllConsumerTests.cs
    - tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs
    - tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs

key-decisions:
  - "PauseAllConsumer injects WorkflowScheduler (NOT WorkflowLifecycle): pause-all is scheduler-wide (one PauseAll() over all groups), not per-workflow."
  - "ResumeAllConsumer injects IWorkflowL1Store + WorkflowLifecycle and resumes per-job by enumerating store.WorkflowIds — the load-bearing D-02 decision that avoids native ResumeAll()'s misfire-on-resume herd."
  - "Test harness: PauseAll + no-burst tests use an NSubstitute IScheduler spy (asserting Received(n).PauseAll(...) and DidNotReceive().ResumeAll(...)); the enumerate-and-resume test uses a real Quartz RAMJobStore (mirroring PauseResumeConsumerTests) so WorkflowLifecycle.ResumeAsync runs its real Paused-guard against real trigger state."
  - "Resume-enumeration test seeds the Paused precondition via per-job PauseAsync (not scheduler-wide PauseAll) to isolate the consumer's enumerate-and-resume-each behavior from Quartz's paused-trigger-GROUP semantics (see Issues)."

requirements-completed: []   # ORCH-02 is phase-spanning — marked at phase close by the orchestrator, NOT here

# Metrics
duration: ~11min
completed: 2026-06-08
---

# Phase 45 Plan 02: Orchestrator Global Pause/Resume Consumers Summary

**The fleet-side enactment of the global broadcast: a thin scheduler-wide `PauseAllAsync` seam, a `PauseAllConsumer` (idempotent `PauseAll()`) + a `ResumeAllConsumer` (per-job resume over the L1 snapshot, NEVER native `ResumeAll()` — no catch-up herd), and both `ConsumerDefinition`s on a NEW per-replica fan-out endpoint `orchestrator-global-pauseresume` with single retry ownership — turning the Wave-0 Orchestrator RED stubs (incl. the load-bearing no-burst negative) GREEN.**

## Performance

- **Duration:** ~11 min
- **Started:** 2026-06-08T18:14:34Z
- **Completed:** 2026-06-08T18:25:48Z
- **Tasks:** 3
- **Files modified:** 9 (4 created, 5 modified — counting the 3 test stubs reshaped from RED to GREEN)

## Accomplishments

- **`WorkflowScheduler.PauseAllAsync` seam:** a one-line expression-bodied `public Task PauseAllAsync(CancellationToken ct) => scheduler.PauseAll(ct);` over the already-injected `IScheduler` (D-01). Idempotent — re-pausing already-paused groups is a Quartz no-op. NO `ResumeAllAsync` seam and NO `scheduler.ResumeAll(` anywhere (grep-confirmed): resume is strictly per-job via `WorkflowLifecycle.ResumeAsync` (D-02).
- **`PauseAllConsumer`:** scheduler-wide pause — injects `WorkflowScheduler` + `ILogger`, logs `{CorrelationId}` (structured template hole only), calls `scheduler.PauseAllAsync(context.CancellationToken)`. A redelivery re-invokes it (idempotent Quartz no-op, no exception). `Consume_Calls_Scheduler_PauseAll` + `Redelivery_Is_Idempotent_No_Op` GREEN.
- **`ResumeAllConsumer`:** per-job resume — injects `IWorkflowL1Store` + `WorkflowLifecycle` + `ILogger`, logs `{CorrelationId}`, enumerates `store.WorkflowIds` and calls `lifecycle.ResumeAsync(workflowId, ct)` for each. The `TriggerState==Paused` guard + fresh-from-now reschedule live INSIDE `ResumeAsync`, so idempotency and the no-burst skip-to-next are inherited free. NEVER native `scheduler.ResumeAll()` (the load-bearing T-45-07 negative). `Consume_Enumerates_WorkflowIds_And_Calls_ResumeAsync_Each` + `Resume_Of_Non_Paused_Trigger_Is_Ignored` + `Native_ResumeAll_Is_Never_Called` + `Resume_Reschedules_Fresh_From_Now_StartAt_Ge_Now` GREEN.
- **Two definitions + Program.cs registration:** `PauseAllConsumerDefinition` (RETRY OWNER — `IOptions<RetryOptions>`, `EndpointName = "orchestrator-global-pauseresume"`, `ConcurrentMessageLimit=1`, `UseMessageRetry(Immediate(Limit))`) and `ResumeAllConsumerDefinition` (NON-OWNER — parameterless ctor, SAME new endpoint, `ConcurrentMessageLimit=1` only, NO second `UseMessageRetry` — Pitfall 4 / T-45-08). Both registered per-replica in `Program.cs` reusing the SAME `instanceId` (one temp fan-out queue `orchestrator-global-pauseresume-{instanceId}` per replica). The old `orchestrator-pauseresume` path is left untouched (D-08 independence → Phase 48 drops it cleanly).

## Task Commits

Each task was committed atomically:

1. **Task 1: PauseAllAsync seam on WorkflowScheduler (ORCH-02 / D-01)** — `3e7cfcd` (feat)
2. **Task 2: PauseAllConsumer + ResumeAllConsumer + the 3 GREEN test classes (D-01/D-02/D-03)** — `d1b5444` (feat)
3. **Task 3: both ConsumerDefinitions on the new fan-out endpoint + Program.cs registration (D-08 / Pitfall 4)** — `be29ca1` (feat)

_TDD note: the Wave-0 RED stubs already existed from Plan 45-00. Task 1's seam has no dedicated scheduler-level test (its behavior is asserted through the Task-2 consumer tests, per the plan), so it is a single GREEN-enabling commit. Task 2's commit replaces the three Orchestrator RED stub bodies with real assertions against the just-built production consumers — a combined test+impl GREEN commit per the 45-01 convention._

## Files Created/Modified

- `src/Orchestrator/Scheduling/WorkflowScheduler.cs` — added the thin idempotent `PauseAllAsync` seam beneath `PauseAsync`.
- `src/Orchestrator/Consumers/PauseAllConsumer.cs` — scheduler-wide idempotent pause consumer.
- `src/Orchestrator/Consumers/ResumeAllConsumer.cs` — per-job resume consumer over the L1 snapshot; never native `ResumeAll()`.
- `src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs` — retry-owning definition on `orchestrator-global-pauseresume`.
- `src/Orchestrator/Consumers/ResumeAllConsumerDefinition.cs` — non-owner definition sharing the new endpoint (no second retry).
- `src/Orchestrator/Program.cs` — fan-out registration of both global consumers reusing `instanceId`.
- `tests/BaseApi.Tests/Orchestrator/Consumers/PauseAllConsumerTests.cs` — 2 real assertions (IScheduler spy: `Received(1)`/`Received(2)` `PauseAll`).
- `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs` — 2 real assertions (real RAM scheduler: enumerate-and-resume-each Paused→Normal; non-Paused ignored, trigger untouched).
- `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs` — 2 real assertions (IScheduler spy: `DidNotReceive().ResumeAll`; fresh trigger `StartTimeUtc >= now`).

## Decisions Made

- **PauseAll injects WorkflowScheduler, not WorkflowLifecycle** — pause-all is a single scheduler-wide `PauseAll()`, not a per-workflow PauseJob loop. (Plan `<action>` directed this; D-01.)
- **ResumeAll is strictly per-job over the L1 snapshot** — the load-bearing D-02: native `ResumeAll()` applies misfire-on-resume to every past-due `WithMisfireHandlingInstructionFireNow()` one-shot trigger → cross-workflow herd. Per-job `ResumeAsync` (delete-stale + fresh-from-now) sidesteps misfire entirely.
- **Test-harness split** — the PauseAll and no-burst assertions need to observe the raw `IScheduler` (`Received(n).PauseAll`, `DidNotReceive().ResumeAll`), so they wire a real `WorkflowScheduler` over an NSubstitute `IScheduler` spy (the plan's suggested shape). The enumerate-and-resume test instead drives a real Quartz RAMJobStore so `WorkflowLifecycle.ResumeAsync` exercises its real Paused-guard and fresh-reschedule against live trigger state (mirroring the existing `PauseResumeConsumerTests`).

## Deviations from Plan

None functional — plan executed as written. One harness-design refinement worth recording (not a behavior change):

### Test-harness refinement (Resume-enumeration precondition)

The plan's `<action>` suggested seeding the Paused precondition for `Consume_Enumerates_WorkflowIds_And_Calls_ResumeAsync_Each` either via the spy's `GetTriggerState → Paused` or a real scheduler. The first real-scheduler attempt seeded the precondition with the scheduler-wide `PauseAllAsync()` (Quartz `PauseAll`), which pauses the trigger *GROUP* and adds it to Quartz's paused-groups set — so the fresh trigger added by the resume reschedule *inherited* the paused group and the post-resume assertion saw `Paused`, not `Normal`. This is a real Quartz `PauseAll`-group semantic, not a production bug in the consumer (the consumer never calls `PauseAll`). The fix isolates the behavior this test owns — *enumerate the snapshot and run per-job ResumeAsync each* — by seeding the Paused precondition with per-job `PauseAsync(jobId)` (which pauses the individual trigger, mirroring the proven `PauseResumeSchedulingTests.ResumeReschedulesFresh` cycle). The end-to-end `PauseAll()`→`ResumeAll` round trip over a live scheduler is the RealStack gate's concern, noted below.

## Issues Encountered

- **Quartz `PauseAll` pauses the trigger GROUP, not just individual triggers** — a fresh trigger added (by the resume reschedule) to an already-`PauseAll`-ed group is created in the `Paused` state in the RAM store. The consumer code path is unaffected (`ResumeAllConsumer` never calls `PauseAll`; `PauseAllConsumer` does, but resume's per-job `ResumeAsync` deletes the job and reschedules). Whether the live PauseAll-group-then-per-job-resume round trip clears the group's paused flag is a Quartz-version behavior that should be confirmed on the **RealStack live gate** (hermetic unit tests deliberately isolate the two concerns). Flagged for the phase-close live verification; no production-code change made this plan.
- **MTP filter syntax** — this project runs xUnit v3 under Microsoft.Testing.Platform; the VSTest `dotnet test --filter "FullyQualifiedName~..."` idiom the plan's `<automated>` lines name is replaced by the built executable's `--filter-query "/asm/ns/class/method"`. Class-level alternation (`A|B|C`) is not a single valid filter-query, so the three classes were run one query each. No code/behavior change (same convention 45-00/45-01 recorded).

## Verification

- `dotnet build src/Orchestrator/Orchestrator.csproj -c Debug` — 0 warnings / 0 errors.
- `dotnet build SK_P.sln -c Release` — **0 warnings / 0 errors**.
- `PauseAllConsumerTests` — **2/2 GREEN** (IScheduler spy `Received(1)`/`Received(2).PauseAll`).
- `ResumeAllConsumerTests` — **2/2 GREEN** (real RAM scheduler: enumerate-and-resume-each; non-Paused ignored).
- `ResumeNoBurstTests` (THE load-bearing negative) — **2/2 GREEN** (`DidNotReceive().ResumeAll`; fresh `StartTimeUtc >= now`).
- Full hermetic suite (`--filter-not-trait Category=RealStack`, Release) — **506 total, 506 passed, 0 failed**. The 6 previously-RED Plan-45-02 Orchestrator stubs are now GREEN; no regression (45-01 closed at 500 passed / 6 deliberately-RED; this plan turns those 6 GREEN → 506/506).
- Acceptance greps confirmed: `PauseAllAsync` present + NO `ResumeAllAsync` / `scheduler.ResumeAll(` in `WorkflowScheduler.cs`; `IConsumer<PauseAll>` + `scheduler.PauseAllAsync(context.CancellationToken)` + `LogWarning("Global PauseAll CorrelationId={CorrelationId}"`; `IConsumer<ResumeAll>` + `foreach (var workflowId in store.WorkflowIds)` + `lifecycle.ResumeAsync(workflowId, context.CancellationToken)` with NO native `ResumeAll(` call; both defs `EndpointName = "orchestrator-global-pauseresume"` (neither uses the OLD `orchestrator-pauseresume`); Resume def has NO `UseMessageRetry` + parameterless ctor; Program.cs registers both `AddConsumer<...>().Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })`; no `$"` interpolation in any consumer log call.

## Known Stubs

None. All four production classes (`PauseAllConsumer`, `ResumeAllConsumer`, `PauseAllConsumerDefinition`, `ResumeAllConsumerDefinition`) plus the `PauseAllAsync` seam are complete and behavior-verified by the now-GREEN tests. No hardcoded empty values flowing to runtime; no placeholder bodies.

## Threat Flags

None beyond the plan's `<threat_model>`. T-45-07 (native `ResumeAll()` catch-up herd) mitigated: the `Native_ResumeAll_Is_Never_Called` spy assertion is GREEN and `ResumeAllConsumer` uses per-job `ResumeAsync` only. T-45-08 (double-wrapped retry) mitigated: only `PauseAllConsumerDefinition` owns `UseMessageRetry` (grep-confirmed the Resume def has none). T-45-09 (log injection) mitigated: both consumers log a single Guid `{CorrelationId}` via structured template holes, no interpolation. No new network endpoints (the new MassTransit endpoint is internal control-plane on the existing bus), no auth paths, no schema changes.

## Next Phase Readiness

- The Orchestrator side of the global broadcast is live: each replica pauses its whole scheduler on `PauseAll` and resumes per-job on `ResumeAll`. Combined with 45-01's Keeper `BitHealthLoop` (which publishes the broadcasts) the BIT-health pause/resume loop is end-to-end at the unit level.
- ORCH-02 is hermetically GREEN. The PauseAll-group-then-per-job-resume live round trip is flagged for the RealStack phase-close gate.
- D-08 independence (new `orchestrator-global-pauseresume` endpoint) leaves the old `orchestrator-pauseresume` per-workflow path untouched for a clean Phase-48 retirement.

## Self-Check: PASSED

- FOUND: src/Orchestrator/Consumers/PauseAllConsumer.cs
- FOUND: src/Orchestrator/Consumers/ResumeAllConsumer.cs
- FOUND: src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs
- FOUND: src/Orchestrator/Consumers/ResumeAllConsumerDefinition.cs
- FOUND: src/Orchestrator/Scheduling/WorkflowScheduler.cs (modified — PauseAllAsync)
- FOUND: src/Orchestrator/Program.cs (modified — both registrations)
- FOUND commit: 3e7cfcd (feat 45-02 PauseAllAsync seam)
- FOUND commit: d1b5444 (feat 45-02 consumers + GREEN tests)
- FOUND commit: be29ca1 (feat 45-02 definitions + Program.cs)

---
*Phase: 45-keeper-bit-health-gate-global-pause-resume*
*Completed: 2026-06-08*
