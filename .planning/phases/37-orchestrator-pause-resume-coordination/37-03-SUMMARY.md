---
phase: 37-orchestrator-pause-resume-coordination
plan: 03
subsystem: orchestrator-scheduling
tags: [masstransit, quartz, pause-resume, consumers, concurrent-message-limit, tdd-green]

# Dependency graph
requires:
  - phase: 37-orchestrator-pause-resume-coordination
    plan: 01
    provides: PauseResumeConsumerTests RED test (pins consumer ctor (WorkflowLifecycle, ILogger<T>) + serial-replay idempotency end state)
  - phase: 37-orchestrator-pause-resume-coordination
    plan: 02
    provides: PauseWorkflow/ResumeWorkflow contracts + WorkflowScheduler.PauseAsync/GetTriggerStateAsync/ScheduleAsync/UnscheduleAsync + deterministic TriggerKey(jobId.ToString("D"))
provides:
  - PauseWorkflowConsumer (PauseJob via WorkflowLifecycle.PauseOnlyAsync seam)
  - ResumeWorkflowConsumer (guard-on-Paused, delete-then-fresh-schedule via WorkflowLifecycle.ResumeAsync seam)
  - PauseWorkflowConsumerDefinition + ResumeWorkflowConsumerDefinition (ConcurrentMessageLimit=1, dedicated "orchestrator-pauseresume" fan-out endpoint, single retry ownership)
  - Program.cs per-replica fan-out registration for both consumers
affects: [37-04, orchestrator-scheduling, keeper-recovery]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Lifecycle seam idiom cloned for Pause/Resume: resolve jobId from L1 (TryGet business no-op) then issue the idempotent Quartz transition — PauseOnlyAsync -> PauseJob (keep L1), ResumeAsync -> guard on == Paused then DeleteJob + fresh ScheduleAsync"
    - "Resume guards on TriggerState == Paused EXACTLY (not != None): None(Stopped)/Normal(Running)/Blocked/Error all fall through to a no-op (D-09), so a forged/duplicate Resume on a Stopped workflow stays Stopped"
    - "Concurrency safety with NO lock/stripe: ConcurrentMessageLimit=1 serializes delivery + idempotent Quartz transitions + return->ACK-after-transition redelivery (D-07); deliberately NOT the IWorkflowL1Store Wait(0) drop-if-held stripe (it would silently lose a Resume)"
    - "Per-endpoint UseMessageRetry ownership: two definitions sharing one endpoint -> only ONE (Pause) registers UseMessageRetry; the sibling (Resume) inherits it"

key-files:
  created:
    - src/Orchestrator/Consumers/PauseWorkflowConsumer.cs
    - src/Orchestrator/Consumers/ResumeWorkflowConsumer.cs
    - src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs
    - src/Orchestrator/Consumers/ResumeWorkflowConsumerDefinition.cs
  modified:
    - src/Orchestrator/Hydration/WorkflowLifecycle.cs
    - src/Orchestrator/Program.cs
    - tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs

key-decisions:
  - "PauseOnlyAsync is a verbatim clone of UnscheduleOnlyAsync but calls scheduler.PauseAsync (PauseJob) instead of UnscheduleAsync (DeleteJob) — preserves the job+trigger AND keeps the L1 entry (D-06/D-08)"
  - "ResumeAsync guards on state != TriggerState.Paused -> return (exact-Paused, RESEARCH §4); on Paused it UnscheduleAsync (DeleteJob the stale paused job) then ScheduleAsync a fresh from-now trigger (Normal), sidestepping misfire (D-03/D-06)"
  - "Both definitions bind a DEDICATED shared endpoint 'orchestrator-pauseresume' (NOT 'orchestrator') so Pause/Resume own their own retry + ConcurrentMessageLimit and don't throttle Start/Stop/Result (RESEARCH §5b); UseMessageRetry registered on the Pause def ONLY (per-endpoint ownership)"
  - "No HydrateAndScheduleAsync on the resume path (RESEARCH §7) — Resume rebuilds the trigger from the already-hydrated L1 wf.Cron, not from an L2 re-read"

requirements-completed: []
requirements-partial: [PAUSE-02, PAUSE-03, PAUSE-04, PAUSE-05]

# Metrics
duration: ~4min
completed: 2026-06-06
---

# Phase 37 Plan 03: Orchestrator Pause/Resume Consumers Summary

**Ships the orchestrator half of the three-state pause/resume model: two MassTransit consumers (`PauseWorkflowConsumer`/`ResumeWorkflowConsumer`) over two idempotent `WorkflowLifecycle` seams (`PauseOnlyAsync` -> `PauseJob`, `ResumeAsync` -> guard-on-Paused + delete-then-fresh-schedule) plus their `ConcurrentMessageLimit=1` definitions on a dedicated per-replica fan-out endpoint — turning Plan-01's `PauseResumeConsumerTests` GREEN with no lock, no reference-count set, and deliberately no `Wait(0)` drop-if-held stripe (D-07).**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-06-06T00:13Z
- **Completed:** 2026-06-06T00:17Z
- **Tasks:** 2
- **Files modified:** 7 (4 created, 3 modified)

## TDD Gate Compliance

This is the **GREEN** plan for the consumer half of Phase 37.

- Both task commits are `feat(...)` commits (GREEN gate). The matching RED gate (`test`) was shipped by Plan 37-01 (`8a87407` — `PauseResumeConsumerTests`).
- `PauseResumeConsumerTests.PauseResumeIdempotent` flipped RED→GREEN: **1/1 passed**.
- Adjacent already-GREEN classes confirmed no regression: `PauseResumeSchedulingTests` **3/3**, `PauseResumeContractTests` **4/4**.
- The Keeper publish-site RED tests (`KeeperPausePublishTests`) **remain RED (2/2 failed)** — they reference the Plan-04 Keeper publish sites (`context.Publish(PauseWorkflow)`/`Publish(ResumeWorkflow)`) that do not exist yet. This is expected and correct; Plan 37-04 owns their GREEN.

## Accomplishments

- **Task 1 — lifecycle seams + consumers (PAUSE-02/03/05):**
  - `WorkflowLifecycle.PauseOnlyAsync(workflowId, ct)`: a verbatim clone of `UnscheduleOnlyAsync` but calling `scheduler.PauseAsync` (PauseJob) — keeps the L1 entry (D-06/D-08), absent-from-L1 is a business no-op, idempotent.
  - `WorkflowLifecycle.ResumeAsync(workflowId, ct)`: TryGet L1 -> `GetTriggerStateAsync` -> guard `state != TriggerState.Paused` return (None/Normal/Blocked/Error ignored, D-09) -> `UnscheduleAsync` (DeleteJob the stale paused job) -> `ScheduleAsync` a fresh from-now Normal trigger off `wf.Cron` (sidesteps misfire, no HydrateAndScheduleAsync — RESEARCH §7). Added `using Quartz;` for `TriggerState`.
  - `PauseWorkflowConsumer` / `ResumeWorkflowConsumer`: ctor `(WorkflowLifecycle, ILogger<T>)` mirroring `StopOrchestrationConsumer`; single-WorkflowId (no foreach); `{WorkflowId}`/`{H}` structured log holes; delegate to the seams; return -> ACK.
- **Task 2 — definitions + Program wiring (PAUSE-04):**
  - `PauseWorkflowConsumerDefinition`: `EndpointName = "orchestrator-pauseresume"`, `ConcurrentMessageLimit = 1`, owns the shared-endpoint `UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))`; dropped the Start/Stop `r.Ignore<WorkflowRootNotFoundException>()` (no L2 hydration here).
  - `ResumeWorkflowConsumerDefinition`: same dedicated endpoint + `ConcurrentMessageLimit = 1`, no second `UseMessageRetry` (per-endpoint ownership held by Pause def — RESEARCH §5).
  - `Program.cs`: registered both consumers on the per-replica fan-out endpoint (`.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })`). `WorkflowLifecycle`/`WorkflowScheduler` already-registered singletons — no new DI.

## Task Commits

Each task was committed atomically (scoped `src/` + the single required `tests/` harness fix; pre-existing untracked files and `.planning/` archive items left untouched; no file deletions):

1. **Task 1: Pause/Resume lifecycle seams + consumers (PAUSE-02/03/05)** — `ce81ac7` (feat)
2. **Task 2: Consumer definitions + Program wiring (PAUSE-04)** — `0631fc3` (feat)

**Plan metadata:** this SUMMARY + STATE + ROADMAP — see final docs commit.

## Files Created/Modified

- `src/Orchestrator/Consumers/PauseWorkflowConsumer.cs` (created) — Pause consumer (PauseJob via lifecycle seam).
- `src/Orchestrator/Consumers/ResumeWorkflowConsumer.cs` (created) — Resume consumer (guard-on-Paused, delete + fresh schedule).
- `src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs` (created) — ConcurrentMessageLimit=1, dedicated endpoint, owns retry.
- `src/Orchestrator/Consumers/ResumeWorkflowConsumerDefinition.cs` (created) — ConcurrentMessageLimit=1, same endpoint, inherits retry.
- `src/Orchestrator/Hydration/WorkflowLifecycle.cs` (modified) — `PauseOnlyAsync` + `ResumeAsync` seams (+`using Quartz;`).
- `src/Orchestrator/Program.cs` (modified) — per-replica fan-out registration of both consumers.
- `tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs` (modified) — removed one redundant blocking analyzer line (see Deviations).

## Decisions Made

- **PauseOnlyAsync = UnscheduleOnlyAsync + PauseAsync** — same TryGet-L1 idiom; `PauseJob` preserves the job+trigger and the L1 entry (D-06/D-08).
- **ResumeAsync guards on exact `== Paused`** — `None`(Stopped)/`Normal`(Running)/`Blocked`/`Error` all fall through to a no-op (RESEARCH §4); on Paused it deletes the stale job and schedules a fresh from-now trigger (sidesteps misfire).
- **Dedicated `orchestrator-pauseresume` endpoint** — Pause+Resume share it so they own their retry + `ConcurrentMessageLimit` and don't throttle Start/Stop/Result; `UseMessageRetry` registered on the Pause def only (per-endpoint ownership, RESEARCH §5).
- **No `Wait(0)` drop-if-held stripe / no lock** — serialization is `ConcurrentMessageLimit=1` + idempotent Quartz transitions + redelivery-on-crash (D-07, mitigates T-37-04 "lost Resume strands the workflow paused forever").

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Removed a redundant xUnit2013-flagged assertion in the 37-01 RED test**
- **Found during:** Task 1 (test-assembly compile for GREEN verification)
- **Issue:** `tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs:84` had `Assert.Equal(1, jobKeys.Count)` directly below `Assert.Single(jobKeys)` (line 83). This repo treats the `xUnit2013` analyzer ("Do not use Assert.Equal() to check for collection size") as an **error**, so the whole test assembly failed to compile. While Plans 37-02/03 symbols were still missing, this analyzer error was masked by the CS0246/CS1061 missing-symbol errors; once this plan created the symbols, the assembly reached the analyzer pass and the error surfaced — blocking any GREEN signal.
- **Fix:** Deleted the redundant line 84 (`Assert.Single(jobKeys)` on line 83 already asserts the exact same intent — exactly one Quartz job).
- **Files modified:** tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs
- **Verification:** `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` → Build succeeded; the test then ran GREEN.
- **Committed in:** `ce81ac7` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (blocking). No production behavior altered by the deviation — only a redundant, analyzer-rejected duplicate assertion was removed; the assertion's intent (`Assert.Single`) is fully preserved.

## Verification

- **Build:** `dotnet build src/Orchestrator/Orchestrator.csproj -c Release` — **Build succeeded, 0 Error(s), 0 Warning(s)** (verified after both tasks).
- **Targeted tests (GREEN):** `PauseResumeConsumerTests` = **1/1**; `PauseResumeSchedulingTests` = **3/3**; `PauseResumeContractTests` = **4/4** (no regression).
- **Keeper RED (expected):** `KeeperPausePublishTests` = **2/2 failed** — owned by Plan 37-04 (publish sites absent).
- **D-06 untouched:** `git diff` confirms `StartOrchestrationConsumer.cs`, `StopOrchestrationConsumer.cs`, and both their definitions show ZERO edits.
- **Acceptance greps:** Pause def contains `EndpointName = "orchestrator-pauseresume"` + `ConcurrentMessageLimit = 1` + `UseMessageRetry`; Resume def contains both EndpointName + ConcurrentMessageLimit but NO code `UseMessageRetry`; neither def's code contains `WorkflowRootNotFoundException`; no code `Wait(0)`/`TryAcquire` in either consumer or the two seams (only doc-comment negations); Program.cs registers both consumers each with `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })`.
- **Post-commit deletion check:** zero deletions across both task commits.

### Test-runner note (infra, not a failure)

Per the repo MEMORY note, the `dotnet test` MTP MSBuild wrapper is flaky here; tests were proven GREEN by building the assembly once and invoking the produced MTP executable directly with its native `--filter-class` flag.

## Requirement Status (honest)

- **PAUSE-02 / PAUSE-03 / PAUSE-04 / PAUSE-05 — PARTIAL, NOT ticked in REQUIREMENTS.md.** This plan ships the orchestrator consumer behavior (halt via PauseJob, resume-from-L1-only-when-Paused, serial idempotency, ignore None/Normal). The end-to-end requirement also needs the Keeper publish fan-out (Plan 04) before the behavior is provable through the live round-trip. Per the 37-02 precedent and repo convention, requirements are ticked when the end-to-end behavior is verified (verifier/phase-complete owns traceability), so they remain unchecked here.

## Known Stubs

None — both consumers are fully wired to the live `WorkflowLifecycle` seams and the real `WorkflowScheduler`; no placeholder/empty data sources.

## Issues Encountered

- The one blocking analyzer error documented under Deviations. No other issues.

## User Setup Required

None — no external service configuration required. `Orchestrator:InstanceId` is optional (falls back to a fresh GUID), same as Start/Stop.

## Next Phase Readiness

- **Plan 04** wires the Keeper publish sites (`context.Publish(PauseWorkflow)` at intake, `context.Publish(ResumeWorkflow)` on `Recovered`, no Resume on `GaveUp` — D-09) and turns `KeeperPausePublishTests` GREEN. It MUST publish `PauseWorkflow`/`ResumeWorkflow(WorkflowId, H)` carrying the inner workflow's id/H; these consumers are now live to receive them on the `orchestrator-pauseresume-{instanceId}` fan-out endpoint.

## Self-Check: PASSED

- Files created/modified — all present:
  - FOUND: src/Orchestrator/Consumers/PauseWorkflowConsumer.cs
  - FOUND: src/Orchestrator/Consumers/ResumeWorkflowConsumer.cs
  - FOUND: src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs
  - FOUND: src/Orchestrator/Consumers/ResumeWorkflowConsumerDefinition.cs
  - FOUND: src/Orchestrator/Hydration/WorkflowLifecycle.cs (PauseOnlyAsync + ResumeAsync)
  - FOUND: src/Orchestrator/Program.cs (both AddConsumer registrations)
- Commits exist: FOUND `ce81ac7`, FOUND `0631fc3`.
- GREEN verified: PauseResumeConsumerTests 1/1 + PauseResumeSchedulingTests 3/3 + PauseResumeContractTests 4/4; Orchestrator build 0/0; Keeper tests RED (expected, 37-04).

---
*Phase: 37-orchestrator-pause-resume-coordination*
*Completed: 2026-06-06*
