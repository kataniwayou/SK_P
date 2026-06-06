---
phase: 42-v3.7.0-docs-traceability-reconciliation
plan: 01
subsystem: docs
tags: [requirements, traceability, reconciliation, milestone-v3.7.0, encoding-safe]

# Dependency graph
requires:
  - phase: 39-metrics-e2e-close-gate
    provides: the Phase-39 live close gate (3x500 GREEN triple-SHA) that proves INTAKE/PROBE/DLQ/PAUSE/KMET-04 satisfied
  - phase: 38-uniform-service-name-instance-labels
    provides: MLBL-01..05 delivery (verified 5/5) being added to the traceability table
  - phase: 40-keeper-recovery-hardening
    provides: KHARD-01..03 gap-closure rows already present in the table
provides:
  - "Reconciled .planning/REQUIREMENTS.md: all satisfied v3.7.0 checkboxes read [x]"
  - "Traceability table with Complete-style rows naming the Phase-39 live gate"
  - "MLBL-01..05 traceability rows mapped to Phase 38 (phase-sorted)"
  - "Corrected coverage footer: counted 34/34 across phases 33-39 + KHARD Phase 40"
affects: [gsd-complete-milestone, milestone-archival, requirements-traceability]

# Tech tracking
tech-stack:
  added: []
  patterns: ["Edit-tool-only targeted in-place edits to avoid BOM/mojibake on em-dash/times/middot glyphs", "Re-count before writing totals (file wins on discrepancy)"]

key-files:
  created: []
  modified: [".planning/REQUIREMENTS.md"]

key-decisions:
  - "Used Edit-tool targeted string replacements exclusively (no full-file Write) to preserve em-dash/x/middot glyphs and avoid UTF-8 BOM injection"
  - "Re-counted the per-phase breakdown (33=4, 34=3, 35=2, 36=9, 37=5, 38=5, 39=6 = 34) before writing the footer; matched the planner's counted truth exactly, no discrepancy note needed"

patterns-established:
  - "Encoding-safe doc reconciliation: byte-level UTF-8 decode + cp1252-sentinel-char mojibake scan + BOM byte check as the acceptance gate"

requirements-completed: []

# Metrics
duration: 12min
completed: 2026-06-07
---

# Phase 42 Plan 01: v3.7.0 Requirements Traceability Reconciliation Summary

**Reconciled REQUIREMENTS.md to the counted truth of the delivered v3.7.0 milestone: flipped 20 satisfied checkboxes to [x], rewrote 16 "Not started" traceability rows to "Complete (Phase-39 live gate)", added the 5 absent MLBL-01..05 rows (Phase 38), and corrected the coverage footer to the counted 34/34 across phases 33-39 + KHARD Phase 40 — all with no BOM and no mojibake (em-dashes/x glyphs byte-intact).**

## Performance

- **Duration:** ~12 min
- **Started:** 2026-06-07
- **Completed:** 2026-06-07
- **Tasks:** 2
- **Files modified:** 1 (.planning/REQUIREMENTS.md)

## Accomplishments
- Flipped 20 satisfied v3.7.0 checkboxes `[ ]` -> `[x]`: INTAKE-01..04, PROBE-01..06, DLQ-01..04, PAUSE-01..05, KMET-04 (KEEP/KMET-01..03/TEST/KHARD left already-checked).
- Rewrote all 16 traceability rows reading exactly "Not started" (Phase 35/36/37) to "Complete (Phase-39 live gate — 3x500 GREEN triple-SHA)".
- Inserted 5 MLBL-01..05 rows mapped to Phase 38, phase-sorted between PAUSE-05 (37) and KMET-01 (39).
- Corrected the opener ("34 requirements across phases 33–39, + KHARD-01..03 gap-closure in Phase 40") and the footer ("34/34 requirements mapped ... 33=4 · 34=3 · 35=2 · 36=9 · 37=5 · 38=5 · 39=6 (= 34)").
- Deleted the stale Phase-42-scope NOTE blockquote (premise now satisfied by this plan).
- Verified zero BOM and zero mojibake; 90 em-dashes + 20 × glyphs preserved byte-intact.

## Task Commits

Each task was committed atomically:

1. **Task 1: Flip satisfied v3.7.0 checkboxes and reconcile traceability rows** - `08b72b8` (docs)
2. **Task 2: Add MLBL-01..05 rows, correct coverage footer, delete stale NOTE** - `8ee4e1a` (docs)

## Files Created/Modified
- `.planning/REQUIREMENTS.md` - Reconciled v3.7.0 requirement checkboxes, traceability table (Complete-style rows + MLBL-01..05), corrected coverage opener/footer, stale NOTE removed.

## Decisions Made
- Edit-tool-only targeted replacements (no full-file Write/Set-Content) per the encoding hazard, preventing the known BOM + em-dash/× mojibake corruption on this repo's planning files.
- Re-counted the per-phase breakdown before writing the footer; the recount (=34) matched the planner's counted truth exactly, so no D-03 discrepancy note was required.

## Deviations from Plan

None - plan executed exactly as written. The verify scripts run via the Bash tool (bash, not PowerShell) and the harness's `$`-stripping required authoring the BOM/mojibake checks as `.ps1` files invoked with `powershell -File` and a byte-level UTF-8 decode (cp1252 sentinel-char scan) rather than inline literal-glyph greps; this is a tooling adaptation of the plan's acceptance check, not a content deviation. Temp verify scripts were deleted and never committed.

## Issues Encountered
- The Bash tool strips PowerShell `$` variables and re-encodes literal mojibake glyphs under the console codepage, so the plan's inline literal-character mojibake greps could not run as-written. Resolved by writing standalone `.ps1` verify scripts that read the file bytes, decode explicitly as UTF-8, and scan for the cp1252 sentinel chars 0x00E2 (corrupt em-dash lead byte) and 0x00C3 (corrupt × lead byte) plus the EF BB BF BOM prefix. Both tasks passed (T1 OK: gate=16, no unchecked, no Not started, no BOM/mojibake; T2 OK: mlbl=5 phase-sorted, new footer present, stale strings + NOTE gone).

## User Setup Required
None - documentation-only change, no external service configuration required.

## Next Phase Readiness
- REQUIREMENTS.md now tells the truth about the delivered v3.7.0 milestone; `/gsd-complete-milestone` (run later) can consume it safely.
- ROADMAP/STATE milestone-counter reconciliation remains Phase 42's broader scope (other plans); this plan covered only REQUIREMENTS.md (SC1 + SC2).
- Known repo quirk: `roadmap update-plan-progress` is a no-op on this ROADMAP format (expected); phase orchestrator reconciles tracking after the wave.

## Self-Check: PASSED

- FOUND: `.planning/REQUIREMENTS.md` — BOM=False, mojibake=0 (em-dash/× sentinels absent)
- FOUND: `.planning/phases/42-v3.7.0-docs-traceability-reconciliation/42-01-SUMMARY.md` — BOM=False, mojibake=0
- FOUND commit: `08b72b8` (Task 1)
- FOUND commit: `8ee4e1a` (Task 2)

---
*Phase: 42-v3.7.0-docs-traceability-reconciliation*
*Completed: 2026-06-07*
