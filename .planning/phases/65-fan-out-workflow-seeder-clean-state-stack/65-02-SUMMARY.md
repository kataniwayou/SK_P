---
phase: 65-fan-out-workflow-seeder-clean-state-stack
plan: 02
subsystem: infra
tags: [powershell, docker, redis, postgres, reset, clean-state, liveness, fk-safe-delete]

# Dependency graph
requires:
  - phase: 65-fan-out-workflow-seeder-clean-state-stack (Plan 01)
    provides: FanOutSeederE2ETests seeder fixture (the reset->seed cycle's seed half + 1/9/9/8 self-verify)
provides:
  - "scripts/phase-65-reset.ps1 — standalone per-run clean-state reset (FLUSHALL + bounded fail-loud heal-wait on per-instance liveness key + FK-safe transactional DELETE of the 6 workflow-graph tables preserving processors+config_schemas + processor-set assertion), stack stays UP"
affects: [65-03 (bring-up), 67, 68 (fault-injection harness — calls reset then the Plan 01 seeder per run)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Liveness heal-wait gates on the L2 KEY (skp:proc:{procId:D}:{instanceId}) not container readiness — the exact state ProcessorLivenessValidator reads — preventing a later seed/Start 422"
    - "Index-SET exclusion via regex ^skp:proc:[^:]+$ (one segment after proc:) to distinguish the bare index SET from a per-instance liveness key"
    - "FK-safe DELETE in migration Down() order inside a single BEGIN/COMMIT transaction; processors + config_schemas explicitly preserved (idempotent re-seed avoided)"
    - "Destructive orphan removal restricted to exact name sk-processor-badconfig; NEVER a processor-sample glob; no docker compose down, no -v"

key-files:
  created:
    - scripts/phase-65-reset.ps1
  modified: []

key-decisions:
  - "Heal-wait budget = 60s (6x the 10s default heartbeat), fail-loud on non-convergence — gates on the per-instance liveness key, not container readiness"
  - "psql DELETE scoped to -d stepsdb via `docker compose exec` (NOT `docker exec sk-postgres`, NOT `-lqt`) — Pitfall 3 makes the `-d stepsdb` load-bearing"
  - "processors + config_schemas preserved (not deleted) — they are idempotent (source-hash / sentinel-name) so re-seeding is wasteful"
  - "Stack stays UP — no `docker compose down`, no `-v`, no DB/volume drop (D-06)"

patterns-established:
  - "Per-run clean-state reset that the 67/68 harness calls before each seeded run so metrics/logs are attributable to that run only"

requirements-completed: [ENV-02]

# Metrics
duration: 5min
completed: 2026-06-14
---

# Phase 65 Plan 02: Clean-State Reset Summary

**scripts/phase-65-reset.ps1 — a standalone per-run clean-state reset that FLUSHALLs dev Redis, heal-waits (bounded 60s, fail-loud) for a fresh per-instance liveness key, FK-safe-DELETEs the 6 workflow-graph tables while preserving processors+config_schemas, and asserts the running processor set == {processor-sample} — with the stack staying UP.**

## Performance

- **Duration:** ~5 min (finalization only; Task 1 implementation committed in a prior session)
- **Tasks:** 1 implemented (Task 1) + 1 checkpoint waived (Task 2)
- **Files modified:** 1 (scripts/phase-65-reset.ps1)

## Accomplishments
- Delivered scripts/phase-65-reset.ps1: the runnable per-run clean-state reset the 67/68 fault-injection harness calls (reset, then the Plan 01 seeder) so each run's metrics/logs are attributable to that run only.
- Reset gates on the exact L2 liveness key the orchestration-start validator reads (heal-wait), preventing a subsequent seed+Start 422 (Pitfall 1).
- FK-safe transactional DELETE of the 6 workflow-graph tables in migration Down() order, preserving processors + config_schemas (D-06).
- Processor-set assertion removes only a stray sk-processor-badconfig by exact name — never the unnamed processor-sample replicas (Pitfall 4); stack stays up (no down / no -v).

## Task Commits

1. **Task 1: Write scripts/phase-65-reset.ps1 (FLUSHALL + heal-wait + FK-safe psql DELETE + processor-set assert)** — `e9fa3a6` (feat)
2. **Task 2: Human-verify reset->seed cycle on a live Docker host** — checkpoint:human-verify, **WAIVED by operator** (see Checkpoint Status). No commit (verification-only task).

**Plan metadata:** committed with this SUMMARY (docs: complete plan)

## Files Created/Modified
- `scripts/phase-65-reset.ps1` — FLUSHALL + bounded fail-loud heal-wait on the per-instance liveness key + FK-safe transactional psql DELETE (preserving processors/config_schemas) + processor-set assertion. 139 lines. AST-parses with zero errors.

## Checkpoint Status

**Task 2 (`checkpoint:human-verify`, blocking gate) — WAIVED by operator.**

Task 2 is a verification checkpoint, not implementation. The reset's FLUSHALL, heal-wait, and processor-set assertion cannot be exercised hermetically — they require a live Docker stack (a hermetic CI/agent environment cannot run docker/redis/psql). The plan's automated `<verify>` for Task 2 is explicitly `MISSING — live Docker host required`.

**Outcome:** The operator responded "no human verification required" — the live reset->seed cycle is **waived**, and this waiver unblocks plan completion.

**Honest status of what was and was NOT done:**
- **DONE (automated):** scripts/phase-65-reset.ps1 was created and AST-parses with zero PowerShell parse errors (Task 1's automated verify, re-confirmed at finalization). All Task 1 acceptance criteria (FLUSHALL present; heal-wait poll with index-SET-exclude regex + 60s fail-loud deadline; `docker compose exec -T postgres psql -U postgres -d stepsdb`; all 6 ordered DELETEs; no DELETE of processors/config_schemas/schemas; no `docker compose down` / `-v`; badconfig-by-exact-name orphan removal) are satisfiable by static inspection of the committed script.
- **NOT DONE (waived, outstanding manual acceptance):** The end-to-end live reset->seed cycle on a Docker host was **NOT executed**. The following remain **unverified against a running stack** and constitute an outstanding manual acceptance step:
  - reset exits 0 and the heal-wait reports liveness reconvergence after FLUSHALL,
  - graph rows (workflows/steps/assignments/step_next_steps/workflow_entry_steps/workflow_assignments) are cleared,
  - processors + config_schemas counts are unchanged (preservation),
  - skp:data:* / skp:msg:* are empty and >=1 skp:proc:*:* per-instance key is present post-reset,
  - processor set == {processor-sample} (2 replicas), no sk-processor-badconfig, stack still up,
  - the re-seed self-verify passes 1/9/9/8.

This SUMMARY does **not** claim the live cycle was run. The reset script is implemented and parse-clean; its runtime behavior against a live stack remains to be confirmed by an operator (see 65-02-PLAN.md Task 2 `how-to-verify` steps 1–9, and 65-VALIDATION.md Manual-Only Verifications).

## Decisions Made
- Closed Task 2 as operator-waived rather than blocking the plan, per the operator's explicit "no human verification required." Recorded the live cycle as an outstanding manual acceptance step rather than claiming it passed.
- Did not re-implement or re-commit Task 1 (already committed as `e9fa3a6`); finalization confirmed the commit is present and the script AST-parses cleanly.

## Deviations from Plan

None — plan executed as written. Task 1 was implemented and committed exactly to spec; Task 2 (the human-verify checkpoint) was waived by the operator, which is the plan's intended resume path for that gate.

## Issues Encountered
None. The live-Docker verification gate could not run in the hermetic environment; this is the expected reason the checkpoint exists and was waived by the operator (not a failure).

## User Setup Required
None — no external service configuration required. (Note: the optional live reset->seed acceptance run in Task 2 requires a Docker host but is operator-waived; it is not a setup prerequisite for downstream plans to be authored.)

## Next Phase Readiness
- scripts/phase-65-reset.ps1 is in place for the 67/68 fault-injection harness (reset -> Plan 01 seeder per run).
- **Outstanding (non-blocking):** the live reset->seed acceptance cycle (Task 2) should be run by an operator once a Docker host is available, to confirm runtime behavior matches the static contract before the 67/68 harness depends on it.

## Self-Check: PASSED

- FOUND: `scripts/phase-65-reset.ps1` (created by Task 1, AST-parses with zero errors)
- FOUND: `.planning/phases/65-fan-out-workflow-seeder-clean-state-stack/65-02-SUMMARY.md`
- FOUND: commit `e9fa3a6` (Task 1)

---
*Phase: 65-fan-out-workflow-seeder-clean-state-stack*
*Completed: 2026-06-14*
