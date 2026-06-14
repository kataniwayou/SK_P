---
phase: 67-fault-injection-harness
plan: 02
subsystem: ops-tooling
tags: [powershell, fault-injection, harness, docker, orchestrator, observability, mtp]

# Dependency graph
requires:
  - phase: 65-fan-out-workflow-seeder-clean-state-stack
    provides: "phase-65-up.ps1 (bring-up + NDJSON health gate) + phase-65-reset.ps1 (FLUSHALL/heal-wait/FK-safe DELETE) + FanOutSeeder fixture (v8-fanout-proof seed)"
  - phase: 66-analyzer
    provides: "AnalyzerE2ETests Analyze_HappyPath_Window_Yields_Pass (the scored verdict the harness invokes)"
  - phase: 67-fault-injection-harness (plan 01)
    provides: "D-16 env-var seam in AnalyzerE2ETests (SCENARIO_ID / WINDOW_START_UTC / WINDOW_END_UTC)"
provides:
  - "scripts/phase-67-harness.ps1 — single self-contained fault-injection orchestrator (-ScenarioId entrypoint)"
  - "In-script [ordered] scenario table (TEST-01 + TEST-02) shaped for Phase 68 to add rows 03-07 by data alone (D-12)"
  - "Clean-orchestrator guarantee (STEP B1) — docker compose restart orchestrator + health-wait drops ghost Quartz crons before the window"
affects: [67-03-live-proof, 68-live-resilience-proof]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "MTP-native test isolation: dotnet test ... -- --filter-method \"*Method*\" (xunit.v3 under Microsoft.Testing.Platform ignores VSTest --filter)"
    - "Clean-orchestrator-before-window: restart the long-running orchestrator after a Redis FLUSHALL so its in-process Quartz RAMJobStore is rebuilt from the now-empty L2 index (drops ghost crons)"
    - "Exit-code-as-verdict with distinct infra-abort codes (10/20/25/30/40/50/60/70 + 64) so an infra abort never masquerades as the analyzer FAIL verdict"

key-files:
  created:
    - scripts/phase-67-harness.ps1
    - .planning/phases/67-fault-injection-harness/67-02-SUMMARY.md
  modified: []

key-decisions:
  - "Added STEP B1 (clean-orchestrator restart + health-wait, exit code 25) NOT in the plan — required to satisfy 67-01 finding #2 (ghost crons survive reset) and finding #3 (the 422). This is the only way existing primitives can guarantee a clean observation window."
  - "Used MTP --filter-method (after --) instead of the plan body's VSTest --filter for both seeder and analyzer invocations, per 67-01 finding #1 (--filter is silently ignored under MTP and runs the whole 638-test suite)."
  - "Validation per the plan's own gate (AST parse + static greps), NOT a live end-to-end run — that is plan 67-03's job and the stack was in a known-dirty 67-01 state."

patterns-established:
  - "A fault-injection harness composes proven close-script primitives + three new pieces (observe-loop fire-counter, crash sequencer, in-script scenario table) + one infra fix (clean-orchestrator restart)."

requirements-completed: [FAULT-01, FAULT-02, FAULT-03]

# Metrics
duration: ~5 min (script authoring + static validation; no live run)
completed: 2026-06-14
---

# Phase 67 Plan 02: Fault-Injection Harness Summary

**`scripts/phase-67-harness.ps1` authored — a single `-ScenarioId`-driven PowerShell orchestrator that runs clean→reset→clean-orchestrator→seed→activate(204)→observe→crash→recover→analyze→teardown with no human prompt, using MTP `--filter-method` isolation and a restart-the-orchestrator step that bakes in the three live findings from plan 67-01.**

## Performance

- **Duration:** ~5 minutes (script authoring + AST/grep validation; no live proof run per the environment note)
- **Tasks:** 2 of 2 executed to completion
- **Files created:** 1 script (+1 summary)

## Accomplishments

- Authored `scripts/phase-67-harness.ps1` (330 lines), the sibling of the close-script family, composing:
  - **STEP A** bring-up via `phase-65-up.ps1` (code 10)
  - **STEP B** clean-state reset via `phase-65-reset.ps1` (code 20)
  - **STEP B1** *new* clean-orchestrator guarantee — `docker compose restart orchestrator` + bounded NDJSON health-wait (code 25)
  - **STEP C** seed via `dotnet test ... -- --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*"` (code 30)
  - **STEP D** static-literal psql wf-id lookup `SELECT id FROM workflows WHERE name = 'v8-fanout-proof'` (code 40)
  - **STEP E** `POST /api/v1/orchestration/start` hard-204 gate with `ConvertTo-Json @($wfId)` array (code 50)
  - **STEP F** observe-loop fire-counter (`orchestrator_dispatch_sent_total`, N=4) → whole-tier crash sequencer (stop→dwell 45s→start over `$scenario.targetContainers`) → post-start health-wait → 300s window hold (code 60)
  - **STEP H** D-16 env seam (`SCENARIO_ID`/`WINDOW_START_UTC`/`WINDOW_END_UTC`) + analyzer via `-- --filter-method "*Analyze_HappyPath_Window_Yields_Pass*"`; echoes the `analyzer-reports/{id}.json` path
  - **STEP Z** `docker compose down` (no volume drop, non-fatal code 70); final `exit $analyzerExit` mirrors the verdict (D-04)
- In-script `[ordered]` scenario table with TEST-01 (no-fault baseline) + TEST-02 (processor whole-tier crash) only, shaped so Phase 68 adds rows 03-07 as pure data (D-12). Unknown `-ScenarioId` aborts with config-usage code 64 before any docker/psql op (T-67-02).
- Header comment documents the full exit-code table and the clean-orchestrator rationale so an operator understands verdict-vs-infra-abort.
- No interactive prompt anywhere (FAULT-03 / V11); no Prometheus correctness logic beyond the fire count (Pitfall 5).

## Task Commits

1. **Task 1: frame + scenario table + STEP A-E (+clean-orchestrator)** — `6ca3f0d` (feat)
2. **Task 2: STEP F observe/crash/recover + H analyze + Z teardown** — `79a7137` (feat)

## Files Created/Modified

- `scripts/phase-67-harness.ps1` — the harness orchestrator (created across both commits; 330 lines). No product source touched.

## Decisions Made

- **STEP B1 clean-orchestrator restart added (not in the plan).** Plan/RESEARCH D-13 assumed row-deletion alone halts fires ("No `orchestration/stop`"), but 67-01 finding #2 proved the long-running orchestrator keeps firing ghost Quartz crons from its in-process RAMJobStore even after a FLUSHALL + DB DELETE. Verified by reading `HydrationBackgroundService.cs` (boot SMEMBERS the L2 parent index → schedules from it), `WorkflowScheduler.cs` (Quartz RAMJobStore, in-memory), and `StartOrchestrationConsumer.cs` (POST /start re-hydrates+schedules only the requested workflow). Restarting the orchestrator AFTER the reset (empty index) and BEFORE seed+start rebuilds an empty scheduler; the `/health/ready` healthcheck (gated on initial-hydration-complete) waited on as the clean-proof. This also resolves finding #3 (the 422 was tied to the dirty orchestrator state). Uses only existing primitives — no new endpoint, no product code, so no architectural checkpoint required.
- **MTP `--filter-method` over VSTest `--filter`** for both seeder and analyzer invocations, per 67-01 finding #1. The plan body literally showed `--filter "Category=RealStack&FullyQualifiedName~..."`; that form is silently ignored under Microsoft.Testing.Platform and runs the entire 638-test suite, polluting the shared backends. Method names verified in-repo: `FanOutSeeder_SeedsAndSelfVerifies`, `Analyze_HappyPath_Window_Yields_Pass`.
- **Validation = static (AST parse + acceptance greps), no live run.** Per the environment note, the live stack was in a dirty 67-01 state and the full proof is plan 67-03's job.

## Deviations from Plan

### Auto-fixed / required adjustments

**1. [Rule 3 - Blocking issue] Added STEP B1 clean-orchestrator restart (code 25)**
- **Found during:** Task 1 (pre-flight analysis of 67-01 finding #2/#3)
- **Issue:** Without a clean orchestrator, ghost Quartz crons fire NULL-payload workflows that emit no `Step_*` labels → the analyzer scores everything MISSING and `POST /start` can 422. Existing primitives (reset's FLUSHALL + DELETE) do not stop the in-process scheduler.
- **Fix:** Inserted `docker compose restart orchestrator` + a bounded NDJSON health-wait between STEP B (reset) and STEP C (seed), with a dedicated exit code 25 that does not collide with any plan code.
- **Files modified:** `scripts/phase-67-harness.ps1`
- **Commit:** `6ca3f0d`

**2. [Rule 1 - Correctness] Replaced VSTest `--filter` with MTP `--filter-method`**
- **Found during:** Task 1 (STEP C) and Task 2 (STEP H)
- **Issue:** The plan body's `dotnet test --filter "..."` is silently ignored under MTP (xunit.v3) and runs the whole suite — the harness would not isolate the seeder/analyzer and would pollute shared backends.
- **Fix:** Used `dotnet test <proj> -c Release -- --filter-method "*<Method>*"` for both invocations.
- **Files modified:** `scripts/phase-67-harness.ps1`
- **Commit:** `6ca3f0d` (seed), `79a7137` (analyzer)

Both adjustments were explicitly mandated by the orchestrator's `<critical_findings_from_67-01>` block, which overrides conflicting plan-body syntax.

## Authentication Gates

None — all operations are local (docker, loopback HTTP, container-side psql).

## Known Stubs

None. The harness is complete end-to-end; STEP F/H/Z are fully implemented (no placeholders remain).

## Issues Encountered

- The plan body and 67-RESEARCH still carried the pre-finding `--filter` syntax and the D-13 "no orchestration/stop, row-deletion halts fires" assumption. Both were superseded by the verified 67-01 findings and handled as deviations above. No blockers — existing primitives were sufficient to guarantee a clean window via the orchestrator restart.

## Next Phase Readiness

- **Ready for 67-03:** the harness exists, parses clean, and is statically validated. Plan 67-03 invokes it twice (`-ScenarioId TEST-01` then `TEST-02`) for the two reference runs that produce the live verdicts owed since 67-01.
- **Carried forward to 67-03:** verify on the live TEST-01 run that the supplied `WINDOW_START_UTC..WINDOW_END_UTC` yields `TriggerCount ≈ 10` (RESEARCH OQ2) and that the clean-orchestrator restart actually yields a single firing workflow (resolves findings #2/#3).
- **Ready for 68:** the `[ordered]` scenario table is the data-only seam for rows 03-07.

## Self-Check: PASSED

- `scripts/phase-67-harness.ps1` — FOUND (330 lines, AST parses clean, all acceptance greps pass).
- `.planning/phases/67-fault-injection-harness/67-02-SUMMARY.md` — FOUND.
- Commit `6ca3f0d` (Task 1) — FOUND in git log.
- Commit `79a7137` (Task 2) — FOUND in git log.

All claims verified. Static validation only — no live proof run was performed (that is plan 67-03's job per the environment note).

---
*Phase: 67-fault-injection-harness*
*Completed: 2026-06-14*
