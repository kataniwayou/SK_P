# Phase 41: Orchestrator Pause/Resume Diagnostics - Context

**Gathered:** 2026-06-07
**Status:** Ready for planning

<domain>
## Phase Boundary

Pure **code-quality gap-closure** phase that closes two warnings from the v3.7.0 Phase-37 audit
(37-REVIEW **WR-01** and **WR-02**). No new REQ-IDs, no new capability, no behavior added beyond
what the two findings call for. **Only the Orchestrator project is touched.**

Two things must become TRUE:
1. A Resume dropped during the narrow fire window is **diagnosable** (observable in logs).
2. The scheduler's reschedule **fallback cannot throw** on a purged non-durable job.

Out of scope: re-arming dropped Resumes (the actual behavioral fix to WR-01 — see deferred), any
change to the Keeper, the pause/resume contracts, the D-09 state model, or REQUIREMENTS.md.

</domain>

<decisions>
## Implementation Decisions

### WR-01 — ResumeAsync ignore-branch diagnostics (SC1)
- **D-01:** **Log only, no behavior change.** On the `state != TriggerState.Paused` ignore branch in
  `WorkflowLifecycle.ResumeAsync`, emit an **informational** log carrying `WorkflowId` + the observed
  `TriggerState`. The dropped-Resume edge still exists but becomes observable. (Fork A.)
- **D-02:** **Do NOT re-arm on `None`.** The 37-REVIEW floated also re-arming when the trigger is
  `None` but L1 still holds the workflow. Rejected: `None` is overloaded — it means BOTH
  "operator-Stopped" (`Stop → DeleteJob` leaves the key empty) AND "trigger transiently vanished
  mid-fire." The D-09 model deliberately treats `None = Stopped → ignore`; re-arming on `None` would
  resurrect a Stopped workflow, and Quartz cannot cleanly disambiguate the two cases. The SC is
  scoped to *log only* for exactly this reason. The behavioral fix is recorded under Deferred Ideas.
- **D-03:** Log level is **informational** (not debug/warning) and the structured fields are
  `WorkflowId` + observed state — locked by SC1, not a discretionary choice.

### WR-02 — RescheduleAsync fallback hardening (SC2)
- **D-04:** **Re-create the full job+trigger in the `RescheduleJob`-returns-null fallback.** Mirror
  `ScheduleAsync`'s `JobBuilder` + two-arg `ScheduleJob(job, trigger, ct)` so the fallback
  re-establishes the schedule even when the non-durable job has been purged. The method becomes
  genuinely self-sufficient and the fallback **cannot throw** `JobPersistenceException`. (Fork A.)
- **D-05:** Rejected Fork B (fail-loud + "defensive-only" comment): the phase Goal reads "the
  reschedule fallback **cannot throw** on a purged non-durable job" — fail-loud still throws and
  leaves the method unable to reschedule. Re-create honors the Goal's intent at trivial cost (the
  build logic already exists in `ScheduleAsync`).
- **D-06:** **Hermetic test is mandatory and covers the fallback path** (SC2). The test must drive
  the `replaced is null` branch — delete the job so no prior trigger exists, call `RescheduleAsync`,
  and assert the schedule is **re-established** (a live trigger exists for the deterministic key)
  rather than asserting a throw. Reuse the existing hermetic Quartz test harness pattern.

### Claude's Discretion
- Exact log message wording for D-01 (the fields `WorkflowId` + observed state are fixed; the prose is not).
- Test file placement and naming, and how closely to mirror the existing
  `PauseResumeSchedulingTests` / `SchedulingTests` setup (real in-memory `IScheduler`).
- Whether the re-created `JobBuilder` block in D-04 is factored into a small private helper shared
  with `ScheduleAsync` or duplicated — planner's call based on readability.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Findings being closed
- `.planning/phases/37-orchestrator-pause-resume-coordination/37-REVIEW.md` — WR-01 (§ "Resume guard
  can transiently observe a non-Paused state") and WR-02 (§ "RescheduleAsync fallback ScheduleJob
  assumes the non-durable job still exists") define the exact issues, file:line, and suggested fixes.

### Source under change
- `src/Orchestrator/Hydration/WorkflowLifecycle.cs` — `ResumeAsync` (lines ~177-194); WR-01 fix lands
  on the `state != TriggerState.Paused` ignore branch.
- `src/Orchestrator/Scheduling/WorkflowScheduler.cs` — `RescheduleAsync` (lines ~58-87); WR-02 fix
  lands on the `replaced is null` fallback. `ScheduleAsync` (lines ~28-51) is the build pattern to mirror.
- `src/Orchestrator/Scheduling/WorkflowFireJob.cs` — the self-rescheduling caller; context for why
  the fallback is latent (Execute holds the trigger live, so current callers never hit the purged case).

### State model (informs WHY D-02 rejects re-arm-on-None)
- `.planning/phases/37-orchestrator-pause-resume-coordination/37-RESEARCH.md` §4 — Quartz trigger
  state model: `None = Stopped`, `Normal = Running`, `Paused`; Resume acts only on exactly `Paused`.

### Test pattern to reuse
- `tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs` — closest hermetic
  Quartz scheduling test (Pause/Resume/ignore) to model the WR-02 fallback test on.
- `tests/BaseApi.Tests/Orchestrator/SchedulingTests.cs` — base scheduling test harness.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `WorkflowScheduler.ScheduleAsync` already builds the non-durable job + one-shot trigger and calls
  the two-arg `ScheduleJob(job, trigger, ct)` — D-04's fallback is exactly this build block.
- `PauseResumeSchedulingTests` / `SchedulingTests` give a working hermetic `IScheduler` setup the
  WR-02 fallback test can clone.
- `ILogger<WorkflowLifecycle> logger` is already injected into `WorkflowLifecycle` — D-01 needs no new
  dependency, just a `logger.LogInformation(...)` call.

### Established Patterns
- Deterministic keying: `JobKey`/`TriggerKey = jobId.ToString("D")` via `KeyFor`/`TriggerKeyFor`.
  Both fixes stay on these helpers.
- Structured logging convention throughout the Orchestrator (named placeholders, e.g.
  `{WorkflowId}`) — D-01 must follow it.
- `RescheduleJob` returns `null` only when no prior trigger existed — the single signal the fallback keys on.

### Integration Points
- WR-01: `WorkflowLifecycle.ResumeAsync` ignore branch only — no caller change, no contract change.
- WR-02: `WorkflowScheduler.RescheduleAsync` fallback branch only — `WorkflowFireJob.Execute` (the sole
  caller) is unaffected at runtime; the new test drives the branch directly.

</code_context>

<specifics>
## Specific Ideas

- The whole phase is two small, surgical diffs in one project plus one hermetic test. Keep diffs
  minimal — this is gap-closure, not refactor. Hermetic suite must stay GREEN, Release 0-warning.

</specifics>

<deferred>
## Deferred Ideas

- **Behavioral fix for the dropped Resume (WR-01 Fork B):** actually re-arm a workflow whose Resume
  arrives while the trigger is transiently `None`. Requires first disambiguating `None = Stopped`
  from `None = trigger-vanished-mid-fire`, which the current Quartz-only state model cannot do
  cleanly (would need an explicit Stopped marker in L1). Out of scope for this gap-closure phase;
  revisit only if dropped Resumes are observed in the logs D-01 adds.

</deferred>

---

*Phase: 41-orchestrator-pause-resume-diagnostics*
*Context gathered: 2026-06-07*
