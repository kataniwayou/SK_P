---
phase: 62-live-proof-close-gate
plan: 03
subsystem: testing
tags: [close-gate, net-zero, triple-sha, runbook, build-gate, operator-gated, powershell, liveness]

# Dependency graph
requires:
  - phase: 62-01
    provides: "processor-sample deploy.replicas:2 reshape + Phase-62 retags (SC1/2/3 + GateAComposition) — the live gate runs the full suite, so these must be in the tree"
  - phase: 62-02
    provides: "GateKeyspaceE2ETests fabricated-key gate-verdict tests + SeedFabricatedLivenessAsync helper — they run inside the live gate"
  - phase: 58
    provides: "scripts/phase-58-close.ps1 (verbatim clone source) + 58-HUMAN-UAT.md (runbook structure to mirror)"
provides:
  - "scripts/phase-62-close.ps1 — v7 triple-SHA net-zero close gate (clone of phase-58 + D-09 ^skp:proc: prefix exclusion + Phase 62/v7.0.0 retitle), AST-valid under pwsh 7"
  - "62-HUMAN-UAT.md — operator runbook: clean build + 2-replica/badconfig rebuild + close-gate invoke + four multi-container lifecycle proofs + GREEN record block (status: pending)"
  - "Autonomous build gate PASSED: Release + Debug 0-warning, hermetic suite 591/591 green, new RealStack tests compile, close script AST-valid"
affects: [v7.0.0-milestone-close, REQUIREMENTS-TEST-01, REQUIREMENTS-TEST-02, REQUIREMENTS-TEST-03]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Triple-SHA net-zero close gate cloned verbatim with a single load-bearing keyspace-exclusion delta (D-09 prefix vs exact-key)"
    - "Operator-gated live run: executor authors + machine-verifies artifacts; the ~50-min live N=3 GREEN gate is a blocking human-verify checkpoint (D-15)"

key-files:
  created:
    - "scripts/phase-62-close.ps1"
    - ".planning/phases/62-live-proof-close-gate/62-HUMAN-UAT.md"
  modified: []

key-decisions:
  - "D-09 prefix exclusion ^skp:proc: replaces phase-58's exact single-key skp:{procId} exclusion on BOTH before/after redis snapshots — v7 instanceIds are non-deterministic and there are now 2-3 of them (2 healthy Sample replicas + index + durably-unhealthy badconfig replica + index)"
  - "D-12 seed version carried forward at 3.5.0 (verified src/Processor.Sample/appsettings.json Service.Version = 3.5.0); SourceHash, not version, is identity"
  - "AST validity verified under pwsh 7 (the actual runtime), NOT Windows PowerShell 5.1 — the plan's verify command hardcoded `powershell` which is 5.1 and reports 18 spurious parse errors for BOTH phase-58 and phase-62 (a 5.1 parser limitation, not a script defect; pwsh 7 reports 0 errors for both)"

patterns-established:
  - "Pattern 1: clone-and-retitle close gate — copy the proven phase-NN-close.ps1 verbatim, apply only the keyspace-exclusion delta + retitle, verify AST + grep the unchanged distinctive blocks"
  - "Pattern 2: runbook mirrors the prior close runbook structure; lifecycle proofs live OUTSIDE the close-gate window so they never perturb BEFORE==AFTER (D-11)"

requirements-completed: []  # TEST-01/02/03 remain UNTICKED — operator-gated live GREEN run pending (D-15)

# Metrics
duration: ~12min
completed: 2026-06-13
---

# Phase 62 Plan 03: Live Proof & Close Gate Summary

**v7.0.0 triple-SHA net-zero close gate (scripts/phase-62-close.ps1, ^skp:proc: prefix exclusion) + operator runbook authored and build-gate-verified; the live N=3 GREEN run is a pending blocking operator checkpoint.**

## Performance

- **Duration:** ~12 min (autonomous Tasks 1-3)
- **Started:** 2026-06-13
- **Completed (autonomous portion):** 2026-06-13
- **Tasks:** 3 of 4 complete (Task 4 PAUSED at blocking operator checkpoint)
- **Files created:** 2

## Accomplishments

- Cloned `scripts/phase-58-close.ps1` → `scripts/phase-62-close.ps1` with the three deltas: D-09 redis-scan prefix exclusion (`^skp:proc:` on BOTH before/after snapshots, replacing the exact single-key exclusion), D-08 retitle (Phase 58/v6.0.0 → Phase 62/v7.0.0, operator-append repointed at 62-HUMAN-UAT.md), D-12 version carried at 3.5.0 + seed description retitled. AST-valid under pwsh 7 (0 parse errors); all unchanged blocks (dual SourceHash read, two-schema/two-processor CREATE-IF-ABSENT seed, N=3 Smell-A cadence, DLQ depth==0, skp:msg:* count==0, _bus_ exclusion, psql SHA) carried verbatim.
- Authored `62-HUMAN-UAT.md` (status: pending) mirroring the 58 runbook: clean host build (Step 1), 2-replica + badconfig-profile rebuild from a clean redis keyspace (Step 2), close-gate invoke + numbered what-it-does + exit codes (Step 3), the four v7 lifecycle proofs OUTSIDE the close window (TEST-01a 2-replica self-register, TEST-01b durably-broken Unhealthy not absent, TEST-01c dead-replica >30s TTL-expiry + lazy SREM, TEST-02-probe-live /health/live 200 + summary), GREEN record block (Step 4), DoD gate with TEST-01/02/03 unticked (Step 5), threat mitigations.
- Ran the autonomous build gate (D-14): Release 0-warning/0-error, Debug 0-warning/0-error, hermetic suite 591/591 green (RealStack excluded; the new GateKeyspaceE2ETests compiled with the solution), close script AST-valid.

## Task Commits

1. **Task 1: Clone phase-58-close.ps1 → phase-62-close.ps1 with the 3 deltas** - `c0c03c4` (feat)
2. **Task 2: Author 62-HUMAN-UAT.md operator runbook** - `7743f2e` (docs)
3. **Task 3: Autonomous build gate + AST/existence verification** - no source changes (pure verification gate; result recorded here — Release+Debug 0-warning, hermetic 591/591, AST-valid)
4. **Task 4: Operator-gated live N=3×GREEN close run + lifecycle proofs** - PAUSED at blocking human-verify checkpoint (NOT executed — D-15)

## Files Created/Modified

- `scripts/phase-62-close.ps1` - v7 triple-SHA net-zero close gate (506 lines); D-09 `^skp:proc:` prefix exclusion on both redis snapshots; Phase 62/v7.0.0 retitle; everything else verbatim from phase-58.
- `.planning/phases/62-live-proof-close-gate/62-HUMAN-UAT.md` - operator runbook (380 lines, status: pending); build/rebuild/close-gate steps + four lifecycle proofs + GREEN record block; TEST-01/02/03 unticked until operator GREEN.

## Decisions Made

- **AST validation runtime:** The plan's `<verify>` block invokes `powershell` (Windows PowerShell 5.1), which reports 18 spurious "Missing closing ')'/'}'" parse errors. Confirmed this is a 5.1 parser limitation, NOT a script defect: the verbatim-proven `scripts/phase-58-close.ps1` produces the IDENTICAL 18 errors under 5.1 but 0 errors under `pwsh` 7. The script is invoked with `pwsh` in production (Step 3 of the runbook + the close-gate usage line), and parses with 0 errors under pwsh 7. AST validity therefore confirmed under the correct runtime.
- **Version 3.5.0 carried forward (D-12):** verified `src/Processor.Sample/appsettings.json` `Service.Version` = `3.5.0` (unchanged for v7); the seed carries it forward (SourceHash is identity, not version).

## Deviations from Plan

None - plan executed exactly as written. No Rule 1-3 auto-fixes were required; the build gate passed against the existing tree with zero source modifications.

(Note: the Task-1 `<verify>` automated command uses `powershell` 5.1, which spuriously fails AST parsing on the proven phase-58 script too. Verification was performed under `pwsh` 7 — the actual script runtime — where the gate passes. This is a verify-tooling observation, not a deviation in the artifact.)

## Issues Encountered

- The hermetic test run emits MassTransit "Connection Failed: rabbitmq://..." log noise to stderr from tests that exercise bus-failure paths; these are expected logged failures inside passing tests and did NOT fail the suite (failed: 0). The Test run summary line confirms `Passed! total: 591, failed: 0, skipped: 0, exit 0`.

## Checkpoint Status (Task 4)

**Task 4 is a `checkpoint:human-verify` with `gate="blocking"` — NOT executed (D-15).** The executor authored and machine-verified all autonomous artifacts; the ~50-min live N=3×GREEN close run + the four multi-container lifecycle proofs are operator-gated. Nothing live has been run.

**Operator must:** follow `62-HUMAN-UAT.md` end-to-end (clean build → clean-keyspace 2-replica/badconfig rebuild → `pwsh -File scripts/phase-62-close.ps1` expecting exit 0 with N=3 GREEN identical Passed count + triple-SHA BEFORE==AFTER + skp-dlq-1 depth==0 + skp:msg:* count==0 → the four lifecycle proofs → record in Step 4 → tick TEST-01/02/03 only on a full GREEN).

**Resume signal:** `approved - N=3 GREEN recorded` (with the three SHA values + identical Passed count), or a failure description (which step, exit code, SHA drift, or lifecycle assertion).

## Next Phase Readiness

- This is the FINAL plan of the FINAL phase of v7.0.0. On a recorded operator GREEN run, TEST-01/02/03 tick in REQUIREMENTS.md, the runbook flips to `status: passed`, and the v7.0.0 milestone can close.
- Blocker to milestone close: the operator-gated live N=3 GREEN close run (Task 4) is pending.

## Self-Check: PASSED

- FOUND: scripts/phase-62-close.ps1
- FOUND: .planning/phases/62-live-proof-close-gate/62-HUMAN-UAT.md
- FOUND: .planning/phases/62-live-proof-close-gate/62-03-SUMMARY.md
- FOUND commit: c0c03c4 (Task 1)
- FOUND commit: 7743f2e (Task 2)

---
*Phase: 62-live-proof-close-gate*
*Completed (autonomous Tasks 1-3): 2026-06-13 — Task 4 pending operator verification*
