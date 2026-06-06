---
phase: 37-orchestrator-pause-resume-coordination
plan: 02
subsystem: orchestrator-scheduling
tags: [messaging-contracts, quartz, triggerkey, pause-resume, icorrelated, tdd-green]

# Dependency graph
requires:
  - phase: 37-orchestrator-pause-resume-coordination
    plan: 01
    provides: Wave 0 RED tests (PauseResumeContractTests, PauseResumeSchedulingTests) pinning the exact contract + scheduler shapes this plan ships
provides:
  - PauseWorkflow + ResumeWorkflow ICorrelated control contracts (Messaging.Contracts)
  - Deterministic TriggerKey(jobId.ToString("D")) stamped on BOTH WorkflowScheduler builder sites
  - WorkflowScheduler.PauseAsync (wraps Quartz PauseJob) + GetTriggerStateAsync (wraps GetTriggerState)
affects: [37-03, 37-04, orchestrator-scheduling, keeper-recovery]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Deterministic Quartz trigger identity: .WithIdentity(TriggerKeyFor(jobId)) on EVERY builder (schedule + self-reschedule) so GetTriggerState(TriggerKey(jobId)) is the sole Normal/Paused/None source of truth"
    - "Thin idempotent scheduler wrappers (PauseAsync -> PauseJob, GetTriggerStateAsync -> GetTriggerState) consumed by the Plan-03 consumers"
    - "Wave-1 GREEN verification when the test assembly cannot yet compile end-to-end: prove this plan's tests via a TEMPORARY <Compile Remove> of the still-RED downstream test files, run the MTP executable --filter-class, then REVERT the csproj before commit"

key-files:
  created:
    - src/Messaging.Contracts/PauseWorkflow.cs
    - src/Messaging.Contracts/ResumeWorkflow.cs
  modified:
    - src/Orchestrator/Scheduling/WorkflowScheduler.cs

key-decisions:
  - "Contracts are sealed records (Guid WorkflowId, string H) : ICorrelated { Guid CorrelationId { get; init; } } — byte-identical mirror of StartOrchestration; single per-workflow WorkflowId (NOT an array, D-01); H positional & correlation/observability-only (D-02), never used for counting"
  - "Deterministic TriggerKey stamped on BOTH ScheduleAsync AND the per-fire RescheduleAsync — without the RescheduleAsync stamp the key reverts to a random GUID after the first fire and GetTriggerState silently returns None"
  - "PauseAsync wraps PauseJob (idempotent); GetTriggerStateAsync wraps GetTriggerState(TriggerKeyFor) and returns None for an unknown key, never throws (RESEARCH §4)"

requirements-completed: []
requirements-partial: [PAUSE-01, PAUSE-02, PAUSE-03]

# Metrics
duration: ~12min
completed: 2026-06-06
---

# Phase 37 Plan 02: Pause/Resume Contracts + Deterministic-TriggerKey Scheduler Summary

**Ships the two `ICorrelated` orchestrator control contracts (`PauseWorkflow`/`ResumeWorkflow`) and the load-bearing scheduler change the whole three-state model rests on — stamping a deterministic `TriggerKey(jobId.ToString("D"))` on BOTH `WorkflowScheduler` builder sites plus the thin `PauseAsync`/`GetTriggerStateAsync` wrappers — turning Plan-01's contract + scheduling RED tests GREEN.**

## Performance

- **Duration:** ~12 min
- **Started:** 2026-06-05T23:59Z
- **Completed:** 2026-06-06T00:11Z
- **Tasks:** 2
- **Files modified:** 3 (2 created, 1 modified)

## TDD Gate Compliance

This is the **GREEN** plan for the contracts + scheduler half of Phase 37.

- Both task commits are `feat(...)` commits (GREEN gate). The matching RED gate (`test`) was shipped by Plan 37-01 (`8622788`, `8a87407`).
- The two RED test files targeting ONLY this plan's symbols flipped RED→GREEN:
  - `PauseResumeContractTests` — 4/4 passed (PAUSE-01 contract shape)
  - `PauseResumeSchedulingTests` — 3/3 passed (PAUSE-02/03/05 scheduler state model)
- The pre-existing `SchedulingTests` (GetTriggersOfJob path) — 2/2 still passed (no regression from the identity stamping). Combined `*SchedulingTests` run: 5/5.
- The consumer/Keeper RED tests (`PauseResumeConsumerTests`, `KeeperPausePublishTests`) **remain RED** — they reference Plan-03 (`PauseWorkflowConsumer`/`ResumeWorkflowConsumer`) and Plan-04 (Keeper publish-site) symbols that do not exist yet. This is expected and correct; those plans own their GREEN.

## Accomplishments

- **Task 1 — contracts (PAUSE-01 contract half):** `src/Messaging.Contracts/PauseWorkflow.cs` and `ResumeWorkflow.cs`, each `public sealed record …(Guid WorkflowId, string H) : ICorrelated { public Guid CorrelationId { get; init; } }`. Byte-identical mirror of `StartOrchestration`, single per-workflow `WorkflowId` (NOT array — D-01), `H` positional and observability-only (D-02).
- **Task 2 — scheduler (PAUSE-02/03 mechanism foundation):** in `src/Orchestrator/Scheduling/WorkflowScheduler.cs`:
  - Added `private static TriggerKey TriggerKeyFor(Guid jobId) => new(jobId.ToString("D"));` next to `KeyFor`.
  - Inserted `.WithIdentity(TriggerKeyFor(jobId))` before `.ForJob(...)` on BOTH the `ScheduleAsync` trigger and the per-fire `RescheduleAsync` trigger (grep count == 2).
  - Added thin idempotent wrappers: `PauseAsync(Guid jobId, CancellationToken ct) => scheduler.PauseJob(KeyFor(jobId), ct)` and `GetTriggerStateAsync(Guid jobId, CancellationToken ct) => scheduler.GetTriggerState(TriggerKeyFor(jobId), ct)`.
  - No signature changes, no cron-skip-guard changes (reused verbatim by the Plan-03 resume path).

## Task Commits

Each task was committed atomically (scoped `src/` paths only — pre-existing `.planning/` archive deletions + untracked files left untouched; no file deletions):

1. **Task 1: PauseWorkflow + ResumeWorkflow control contracts (PAUSE-01)** — `63fb4de` (feat)
2. **Task 2: Deterministic TriggerKey + PauseAsync + GetTriggerStateAsync (PAUSE-02/03)** — `8ee43a1` (feat)

**Plan metadata:** this SUMMARY + STATE + ROADMAP — see final docs commit.

## Files Created/Modified

- `src/Messaging.Contracts/PauseWorkflow.cs` (created) — Pause control contract.
- `src/Messaging.Contracts/ResumeWorkflow.cs` (created) — Resume control contract (byte-identical sibling).
- `src/Orchestrator/Scheduling/WorkflowScheduler.cs` (modified) — `TriggerKeyFor` helper, `.WithIdentity` stamping ×2, `PauseAsync` + `GetTriggerStateAsync` wrappers (+13 lines).

## Decisions Made

- **Contract shape** — `(Guid WorkflowId, string H) : ICorrelated`, mirroring `StartOrchestration`; per-workflow `WorkflowId` not an array (D-01); `H` correlation/observability-only (D-02).
- **Stamp BOTH builder sites** — the per-fire `RescheduleAsync` MUST also stamp the deterministic key, otherwise the trigger reverts to a random GUID after the first fire and `GetTriggerState(TriggerKey(jobId))` silently returns `None`, breaking the state model.
- **Thin idempotent wrappers** — `PauseAsync`/`GetTriggerStateAsync` are pass-throughs to Quartz; `GetTriggerStateAsync` returns `None` for an unknown key and never throws (RESEARCH §4).

## Deviations from Plan

None — the plan executed exactly as written. No auto-fixes (Rules 1–3), no architectural decisions (Rule 4), no authentication gates, no scope creep.

## Verification

- **Build:** `dotnet build src/Orchestrator/Orchestrator.csproj` — **Build succeeded, 0 Error(s), 0 Warning(s).**
- **Targeted tests (GREEN):** `PauseResumeContractTests` + `PauseResumeSchedulingTests` = **7/7 passed**; `*SchedulingTests` = **5/5 passed** (3 new + 2 pre-existing — no regression).
- **Acceptance greps:** `.WithIdentity(TriggerKeyFor` count == 2; `PauseAsync`/`scheduler.PauseJob(`, `GetTriggerStateAsync`/`scheduler.GetTriggerState(TriggerKeyFor(` all present; both contracts contain the required record signature + `CorrelationId { get; init; }`.
- **Post-commit deletion check:** zero deletions across both task commits.

### Test-runner note (infra, not a failure)

The `dotnet test` MTP MSBuild wrapper crashed with `MSB4166: Child node exited prematurely` / MTP `FileSystem.CreateNew` file-create errors — a known-flaky infrastructure issue with the xunit.v3 3.2.2 MTP wrapper in this repo, NOT a test failure. The plan's tests were proven GREEN by building the project once and invoking the produced MTP executable directly with its native `--filter-class` flag (matching the MEMORY `-- --filter-class` note).

### Why the full BaseApi.Tests assembly does not yet compile end-to-end (EXPECTED)

The test assembly still references Plan-03 symbols (`PauseWorkflowConsumer`/`ResumeWorkflowConsumer` in `PauseResumeConsumerTests.cs`) and a Plan-04 publish-site assertion (`KeeperPausePublishTests.cs`) that do not exist yet, so the whole assembly cannot compile end-to-end. These are missing-symbol (`CS0246`) errors confined to those two still-RED files — not harness/syntax errors and not failures of this plan. To obtain a GREEN signal for the contract + scheduler tests this plan owns, those two files were TEMPORARILY excluded via a verification-only `<Compile Remove>` in `tests/BaseApi.Tests/BaseApi.Tests.csproj`, the tests were run, and the csproj was then REVERTED — it is byte-unchanged on disk (confirmed via `git status`, no entry). No test source was modified.

## Issues Encountered

- The MTP `dotnet test` wrapper crash above (worked around by running the executable directly). No other issues.

## Requirement Status (honest)

- **PAUSE-01 / PAUSE-02 / PAUSE-03 — PARTIAL, NOT ticked in REQUIREMENTS.md.** This plan ships only the contract shapes (PAUSE-01 contract half) and the scheduler mechanism foundation (PAUSE-02/03). The full requirement text spans the phase: PAUSE-01's "fanned out from Keeper to the orchestrator" is Plan 04; the PAUSE-02/03 orchestrator consumer behavior is Plan 03. Per the 35-03/36-04 precedent, requirements are ticked when the end-to-end behavior is proven (verifier/phase-complete owns traceability), so they are deliberately left unchecked here.

## User Setup Required

None — no external service configuration required.

## Next Phase Readiness

- **Plan 03** (orchestrator `PauseWorkflowConsumer`/`ResumeWorkflowConsumer` over `WorkflowLifecycle`, `ConcurrentMessageLimit = 1`, Program wiring) consumes the `PauseAsync`/`GetTriggerStateAsync` wrappers shipped here and turns `PauseResumeConsumerTests` GREEN. The Plan-01 RED test pins the consumer ctor shape `(WorkflowLifecycle, ILogger<T>)`.
- **Plan 04** wires the Keeper publish sites (`context.Publish(PauseWorkflow)` at intake, `context.Publish(ResumeWorkflow)` on Recovered, no Resume on GaveUp — D-09) and turns `KeeperPausePublishTests` GREEN.

## Self-Check: PASSED

- Files created/modified — all present:
  - FOUND: src/Messaging.Contracts/PauseWorkflow.cs
  - FOUND: src/Messaging.Contracts/ResumeWorkflow.cs
  - FOUND: src/Orchestrator/Scheduling/WorkflowScheduler.cs (TriggerKeyFor + .WithIdentity ×2 + PauseAsync + GetTriggerStateAsync)
- Commits exist: FOUND `63fb4de`, FOUND `8ee43a1`.
- GREEN verified: PauseResumeContractTests 4/4 + PauseResumeSchedulingTests 3/3 + pre-existing SchedulingTests 2/2; Orchestrator build 0/0.

---
*Phase: 37-orchestrator-pause-resume-coordination*
*Completed: 2026-06-06*
