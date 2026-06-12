---
phase: 55-live-proof-close-gate
plan: 01
subsystem: testing
tags: [close-gate, powershell, triple-sha, net-zero, slot-array, redis, rabbitmq, operator-runbook]

# Dependency graph
requires:
  - phase: 49-live-proof-close-gate
    provides: scripts/phase-49-close.ps1 (the verbatim triple-SHA net-zero close-gate template) + 49-HUMAN-UAT.md (operator runbook structure)
  - phase: 54-terminal-index-delete-atomic-keeper-gc
    provides: A19 active two-key DELETE (ProcessorPipeline.DeleteTerminalAsync + DeleteConsumer) — the production property the skp:msg:* count==0 assertion proves
provides:
  - "scripts/phase-55-close.ps1 — v5 triple-SHA net-zero close gate (clone of phase-49-close.ps1 + 3 deltas: composite settle-GC removed, skp:msg:* count==0 added, retitled)"
  - ".planning/phases/55-live-proof-close-gate/55-HUMAN-UAT.md — operator runbook gating the live N=3xGREEN close run"
affects: [55-04 (the operator-gated live N=3xGREEN run), v5.0.0 milestone close]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Additive count==0 invariant assertion (skp:msg:* slot-array index) parallel to the skp-dlq-1 depth==0 check — net-zero proven by active reclaim, not a TTL settle"
    - "Verbatim clone-plus-cited-deltas close-gate authoring (no re-derivation of the SHA protocol)"

key-files:
  created:
    - scripts/phase-55-close.ps1
    - .planning/phases/55-live-proof-close-gate/55-HUMAN-UAT.md
  modified: []

key-decisions:
  - "Seed version stays '3.5.0' (Landmine 1) — the Processor row is keyed by uq_processor_source_hash; the SourceHash is what changed v4->v5, not the version string. No '5.0.0' seed invented."
  - "No skp:msg:* settle-wait added in place of the removed composite settle-GC loop (Pitfall 2) — the 300/600s SlotArrayOptions TTL cannot be waited out; net-zero is the A19 active two-key DELETE, asserted as count==0."
  - "Name-only SHA preserved (list_queues name, not name messages) — name messages used ONLY in the separate skp-dlq-1 depth read (Pitfall 4)."

patterns-established:
  - "D-06c additive A19 active-reclaim assertion: skp:msg:* count==0 surfaces a leak as BOTH a redis SHA mismatch AND count>0, never a silent TTL pass"

requirements-completed: [TEST-02]

# Metrics
duration: 4min
completed: 2026-06-12
---

# Phase 55 Plan 01: v5.0.0 Close Gate + Operator Runbook Summary

**v5 triple-SHA net-zero close gate (`scripts/phase-55-close.ps1`) cloned verbatim from `phase-49-close.ps1` with exactly three deltas — Model-B composite settle-GC removed, additive `skp:msg:*` count==0 assertion added, retitled to Phase 55 (seed version unchanged at `3.5.0`) — plus the `55-HUMAN-UAT.md` operator runbook that gates the live N=3xGREEN run.**

## Performance

- **Duration:** 4 min
- **Started:** 2026-06-12T07:50:04Z
- **Completed:** 2026-06-12T07:53:47Z
- **Tasks:** 2
- **Files modified:** 2 (both created)

## Accomplishments
- `scripts/phase-55-close.ps1` — verbatim clone of the proven Phase-49 triple-SHA protocol (idempotent Processor-row seed, compose-health pre-flight, both-config 0-warning build gate, N=3 identical-fact-count cadence + Smell-A guard, triple-SHA BEFORE==AFTER, separate `skp-dlq-1` depth==0) carrying exactly the three cited v5 deltas. Parses clean under `pwsh`.
- D-06a: the retired Model-B composite settle-GC loop, the four composite-naming redis-mismatch error lines, and the composite header comment blocks are removed. NOT replaced with a `skp:msg:*` settle-wait (Pitfall 2).
- D-06c: an additive `skp:msg:*` count==0 block (parallel to the `skp-dlq-1` depth==0 check) proves net-zero of the processor-owned slot-array index via the A19 active two-key DELETE.
- D-09: `55-HUMAN-UAT.md` operator runbook — Phase-49 structure cloned (Step 1-4 + record blocks), the v5 four-service rebuild + the clean `dotnet clean + build` SourceHash==host caution, the Step-3 record block including the new `skp:msg:*` count==0, the three threat mitigations, and TEST-01/02 left UNticked.

## Task Commits

Each task was committed atomically:

1. **Task 1: Clone phase-49-close.ps1 to phase-55-close.ps1 with the three v5 deltas** - `df603de` (feat)
2. **Task 2: Create the 55-HUMAN-UAT.md operator runbook** - `d0e2fbc` (docs)

## Files Created/Modified
- `scripts/phase-55-close.ps1` - v5 triple-SHA net-zero close gate; clone of phase-49-close.ps1 + 3 deltas. 384 lines, parses clean.
- `.planning/phases/55-live-proof-close-gate/55-HUMAN-UAT.md` - operator runbook gating the live N=3xGREEN run; TEST-01/02 unticked.

## Decisions Made
- **Seed version stays `3.5.0`** (Landmine 1): the Processor row is keyed by `uq_processor_source_hash`; the SourceHash changed v4->v5, not the version string. Only the `description` text changed (`Phase 49 ...` -> `Phase 55 ...`). No `'5.0.0'` seed string invented.
- **No `skp:msg:*` settle-wait** in place of the removed composite settle-GC loop (Pitfall 2): the 300/600s `SlotArrayOptions` TTL cannot be waited out. Net-zero is proven by the A19 active two-key DELETE and asserted additively as count==0.
- **Name-only SHA preserved** (`list_queues name`): `name messages` appears ONLY in the separate `skp-dlq-1` depth read (Pitfall 4), keeping depth churn out of the name invariant.
- **Autonomous verify is a PARSE check, not a live run** (per the additional context): the script is verified by `[System.Management.Automation.Language.Parser]::ParseFile`; the live N=3xGREEN run is operator-gated in plan 55-04.

## Deviations from Plan

None - plan executed exactly as written. Both tasks (clone+deltas, runbook) landed verbatim against the PATTERNS.md / RESEARCH.md citations with no auto-fixes, no architectural decisions, no auth gates, no scope creep.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required for this plan. (The live N=3xGREEN run that the runbook gates IS an operator action, tracked in 55-HUMAN-UAT.md for plan 55-04.)

## Known Stubs
None. The script is a complete operator-runnable close gate; the runbook record blocks are intentionally `<fill>`/pending placeholders to be completed by the operator on the live GREEN run (the documented operator-gate pattern, mirroring 49-HUMAN-UAT.md).

## Verification

Plan-level verification all PASSED:
- `pwsh` parses `scripts/phase-55-close.ps1` with zero errors (PARSE OK).
- `scripts/phase-55-close.ps1` contains `skp:msg:*` (12 hits incl. the `redis-cli --scan --pattern 'skp:msg:*'` count check) and does NOT contain `skp:{corr}` (0) or `Settle:` (0).
- `grep -c "Phase 49" scripts/phase-55-close.ps1` == 0 (fully retitled).
- `version = '3.5.0'` present (1, unchanged — Landmine 1).
- both-config build gate (`-c Release` + `-c Debug`) and `skp-dlq-1` preserved.
- `55-HUMAN-UAT.md` exists with all four `## Step` headings, the four-service rebuild command, the `SourceHash`/`dotnet clean` caution, the `skp:msg:*` + `skp-dlq-1` record block, and TEST-01/02 UNticked (0 `[x]`, 2 `[ ]`).
- Runbook automated verify: RUNBOOK OK.

## Next Phase Readiness
- The v5 close gate and its operator runbook are authored and parse-clean. Ready for the live N=3xGREEN operator run (plan 55-04) against the rebuilt v5 stack.
- No blockers. TEST-02 (the script-exists + parses half) is the autonomously-verifiable deliverable of this plan; TEST-01 + the live half of TEST-02 stay operator-gated.

## Self-Check: PASSED

All created files exist (scripts/phase-55-close.ps1, 55-HUMAN-UAT.md, 55-01-SUMMARY.md); both task commits (df603de, d0e2fbc) present in git history.

---
*Phase: 55-live-proof-close-gate*
*Completed: 2026-06-12*
