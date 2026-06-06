# Phase 41: Orchestrator Pause/Resume Diagnostics - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-07
**Phase:** 41-orchestrator-pause-resume-diagnostics
**Areas discussed:** WR-01 (ResumeAsync diagnostics), WR-02 (RescheduleAsync fallback)

**Format note:** Per recorded user preference, gray areas were presented as prose numbered forks
with recommendations (not AskUserQuestion menus). User confirmed both recommendations with "proceed".

---

## WR-01 — ResumeAsync ignore-branch diagnostics

| Option | Description | Selected |
|--------|-------------|----------|
| Fork A — log only | Informational log on the `state != Paused` ignore branch (`WorkflowId` + observed state). Satisfies SC1 exactly; dropped-Resume edge becomes observable but still occurs. | ✓ |
| Fork B — log + re-arm on `None` | Also treat "paused-then-trigger-vanished" (`None` + L1 present) as resumable, re-arming the workflow. | |

**User's choice:** Fork A (log only).
**Notes:** Fork B rejected because `None` is overloaded (operator-Stopped vs transient mid-fire
vanish); the D-09 model treats `None = Stopped → ignore`, and re-arming on `None` would resurrect a
Stopped workflow with no clean way to disambiguate. SC1 is deliberately scoped to log-only for this
reason. Behavioral fix recorded under Deferred Ideas. Log level locked to informational with
`WorkflowId` + observed state by the SC.

---

## WR-02 — RescheduleAsync fallback hardening

| Option | Description | Selected |
|--------|-------------|----------|
| Fork A — re-create job+trigger | Fallback mirrors `ScheduleAsync` (`JobBuilder` + two-arg `ScheduleJob(job, trigger, ct)`), re-establishing the schedule even if the non-durable job was purged. Cannot throw. | ✓ |
| Fork B — fail loudly + document | Throw a clear explicit exception on the null branch + comment that callers only invoke while the trigger is live (defensive-only). | |

**User's choice:** Fork A (re-create job+trigger).
**Notes:** Fork B rejected because the phase Goal reads "the reschedule fallback **cannot throw** on a
purged non-durable job" — fail-loud still throws and leaves the method unable to reschedule. Re-create
honors the Goal at trivial cost (build logic already exists in `ScheduleAsync`). Mandatory hermetic
test (SC2) asserts the fallback re-establishes the schedule (drive `replaced is null`, then assert a
live trigger exists), not that it throws.

---

## Claude's Discretion

- Exact informational log message wording (fields fixed: `WorkflowId` + observed state).
- Hermetic test file placement/naming; degree of mirroring `PauseResumeSchedulingTests`.
- Whether to factor the re-created `JobBuilder` block into a private helper shared with `ScheduleAsync`.

## Deferred Ideas

- Behavioral fix for the dropped Resume (WR-01 Fork B) — needs an explicit Stopped marker to
  disambiguate `None`; revisit only if D-01's logs show dropped Resumes in practice.
