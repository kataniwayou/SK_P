---
phase: 65-fan-out-workflow-seeder-clean-state-stack
plan: 03
subsystem: infra
tags: [powershell, docker-compose, health-check, e2e-harness, bring-up]

# Dependency graph
requires:
  - phase: 62-live-proof-close-gate
    provides: NDJSON-per-replica compose-health pre-flight parse (phase-62-close.ps1:289-309)
provides:
  - scripts/phase-65-up.ps1 — minimal-stack bring-up the 67/68 fault-injection harness calls to start the proof stack
  - 10-service-type health-wait (default profile, badconfig excluded by its profile gate, no compose edit)
  - ENV-01 zero-badconfig assertion (fail-loud exit 2)
affects: [67, 68, fault-injection-harness, clean-state-stack]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Default-profile bring-up + bounded NDJSON-per-replica health-wait poll (reuse phase-62-close.ps1)"
    - "profiles:[badconfig] compose gate as the sole exclusion mechanism — no compose.yaml edit, no --profile flag"

key-files:
  created: [scripts/phase-65-up.ps1]
  modified: []

key-decisions:
  - "processor-badconfig excluded purely via its existing profiles:[badconfig] gate (compose.yaml:300) — default docker compose up -d omits it with zero compose edit"
  - "otel-collector has no in-container healthcheck — its 'running' state is treated as ready (do not block on .Health)"
  - "keeper and processor-sample stay deploy.replicas:2 — all instances must be healthy (NDJSON multi-line parse)"
  - "Bounded poll loop (180s deadline, 2s interval) wraps the close-script's single-pass pre-flight to absorb elasticsearch/start_period cold starts"

patterns-established:
  - "Pattern: phase-NN-up.ps1 = compose up default profile → per-service bounded health-wait → invariant assertion → exit 0/2"

requirements-completed: [ENV-01]

# Metrics
duration: 2min
completed: 2026-06-14
---

# Phase 65 Plan 03: phase-65-up minimal-stack bring-up Summary

**scripts/phase-65-up.ps1 — `docker compose up -d` (default profile) + bounded 10-service-type health-wait + zero-sk-processor-badconfig assertion (ENV-01), reusing the phase-62-close NDJSON-per-replica parse, with no compose.yaml edit.**

## Performance

- **Duration:** 2 min
- **Started:** 2026-06-14T11:37:27Z
- **Completed:** 2026-06-14T11:38:31Z
- **Tasks:** 1
- **Files modified:** 1

## Accomplishments
- Created the runnable minimal-stack bring-up artifact the 67/68 harness depends on to start the proof stack.
- Brings up the default compose profile (processor-badconfig auto-excluded by its `profiles:["badconfig"]` gate — no compose edit, no `--profile badconfig`).
- Waits up to 180s (2s poll) for all 10 service types healthy/ready, reusing the proven NDJSON-per-replica parse from phase-62-close.ps1; otel-collector 'running'=ready; keeper + processor-sample replicas:2 require all instances healthy.
- Fail-loud `exit 2` on either non-convergence (names the offending service + last health) or any running sk-processor-badconfig container (ENV-01).

## Task Commits

Each task was committed atomically:

1. **Task 1: Write scripts/phase-65-up.ps1 (compose up + 10-service health wait + zero-badconfig assert)** - `0aed7e5` (feat)

**Plan metadata:** (this commit) (docs: complete plan)

## Files Created/Modified
- `scripts/phase-65-up.ps1` - Default-profile bring-up + 10-service-type bounded health-wait + zero-badconfig assertion (84 lines)

## Decisions Made
- Excluded processor-badconfig solely via its existing `profiles:["badconfig"]` gate (compose.yaml:300) — the default `docker compose up -d` omits it with zero compose edit and no `--profile` flag, per the plan's explicit constraint.
- Treated otel-collector's 'running' instance state as ready (it has no in-container healthcheck, compose.yaml:69-79) rather than blocking on `.Health`.
- Wrapped the close-script's single-pass health pre-flight in a per-service bounded poll loop (180s overall deadline, 2s interval) so elasticsearch/start_period cold starts converge instead of false-failing on first check.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None. The acceptance-criteria grep for `--profile badconfig` matched only a documentation comment (line 8: "and NO `--profile badconfig`") — verified the forbidden flag appears in no executable code. compose.yaml confirmed unmodified.

## User Setup Required
None - no external service configuration required. (Live exercise `pwsh -File scripts/phase-65-up.ps1` is a manual step on a Docker host; the script itself is operator tooling.)

## Next Phase Readiness
- ENV-01 closed: the bring-up artifact exists, parses cleanly, and enforces the zero-badconfig + 10-service-healthy invariant.
- Ready for the 67/68 fault-injection harness to call `scripts/phase-65-up.ps1` as its stack-start step.
- Live exercise on a Docker host remains a manual acceptance step (not runnable in this hermetic execution context).

## Self-Check: PASSED

- FOUND: scripts/phase-65-up.ps1
- FOUND: .planning/phases/65-fan-out-workflow-seeder-clean-state-stack/65-03-SUMMARY.md
- FOUND commit: 0aed7e5 (Task 1)

---
*Phase: 65-fan-out-workflow-seeder-clean-state-stack*
*Completed: 2026-06-14*
