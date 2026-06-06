# Phase 42: v3.7.0 Docs & Traceability Reconciliation - Context

**Gathered:** 2026-06-07
**Status:** Ready for planning

<domain>
## Phase Boundary

**Doc-only** phase. Make REQUIREMENTS.md and ROADMAP.md tell the truth about the v3.7.0 milestone
before archival, and backfill the one missing verification artifact. No new REQ-IDs, no code, no
milestone archival (that is `/gsd-complete-milestone`, run separately afterward).

The four locked success criteria (from ROADMAP Phase 42):
1. REQUIREMENTS.md checkboxes for all satisfied v3.7.0 reqs read `[x]` (INTAKE/PROBE/DLQ/PAUSE/KMET)
   and their traceability rows reflect "Complete (Phase-39 live gate)" rather than "Not started".
2. MLBL-01..05 rows exist in the REQUIREMENTS.md traceability table mapped to Phase 38; the coverage
   footer reads the correct totals — no stale "29 / 6 phases / 33-38".
3. The ROADMAP.md progress table Phase-38 row reads its true plan count + "Complete" (not "0/? Not started").
4. `39-VERIFICATION.md` exists for the close-gate phase, recording the 3×500 GREEN triple-SHA result
   and the accepted keeper-dlq drain-timing follow-up.

This is deterministic reconciliation — there are no design gray areas. The decisions below lock the
*approach*, not the vision.

</domain>

<decisions>
## Implementation Decisions

### Source of truth for corrected counts (SC1, SC2)
- **D-01:** The reconciled totals are derived by **counting the actual REQ-IDs and their checkbox
  state in REQUIREMENTS.md** — the file's real content is authoritative, NOT the SC's stated "34."
  The SC numbers are guidance/target shape; if the counted reality differs, the file wins and the
  footer states the *counted* number with a per-phase breakdown.
- **D-02:** Scouting found the numbers do not cleanly add up (raw grep: INTAKE=4, PROBE=6, DLQ=5,
  PAUSE=5, KMET=4, MLBL=1-present, KHARD=3 — DLQ shows 5 not the SC's 4, and MLBL rows are mostly
  absent). The executor MUST read every REQ-ID definition + its traceability row and reconcile both
  the checkboxes AND the per-phase counts to match the actual delivered set. Present the headline as
  "{counted} delivered across phases 33-39, + KHARD-01..03 gap-closure (Phase 40)".
- **D-03:** If any number still looks wrong after a careful count, add a one-line explanatory note —
  never silently "fix" by inventing data. Reconcile to truth, flag genuine discrepancies.

### 39-VERIFICATION.md backfill (SC4)
- **D-04:** Create `39-VERIFICATION.md` by **backfilling from the existing Phase-39 close-gate
  evidence** — primarily `39-04-SUMMARY.md` and its sibling 39 artifacts. Record the 3×500 GREEN
  triple-SHA result and the accepted keeper-dlq drain-timing follow-up.
- **D-05:** Do **NOT** re-run the close gate. This is a documentation backfill of an already-passed
  gate; re-running it (RealStack, container rebuilds) is out of scope for a doc phase.

### Scope discipline
- **D-06:** Touch **only** `REQUIREMENTS.md`, `ROADMAP.md`, and create the phase-39 directory's
  `39-VERIFICATION.md`. No code, no other docs, no milestone archival.

### Claude's Discretion
- Plan/wave structure and how the doc edits are split across plans.
- Exact prose wording of the corrected footer note and the backfilled VERIFICATION.md narrative
  (the *facts* are fixed by the evidence; the phrasing is the planner's call).
- Whether to verify each doc change with a grep-based acceptance check (encouraged given this repo's
  conventions).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Files being reconciled (the deliverables)
- `.planning/REQUIREMENTS.md` — the v3.7.0 requirement list + traceability table + coverage footer.
  Current stale state: footer reads "29 requirements across 6 phases, 33–38"; line ~131 NOTE
  acknowledges missing MLBL-01..05 rows and "Not started" status text for satisfied reqs.
- `.planning/ROADMAP.md` — progress table; Phase-38 row currently reads `0/? | Not started`. Also the
  v3.7.0 milestone section (phases 33-42) for cross-checking per-phase plan counts.

### Evidence source for the backfill (SC4)
- `.planning/phases/39-keeper-metrics-realstack-e2e-close-gate/39-04-SUMMARY.md` — close-gate result
  (3×500 GREEN, triple-SHA) to backfill into the new `39-VERIFICATION.md`.
- `.planning/phases/39-*/39-01..03-SUMMARY.md`, `39-CONTEXT.md`, `39-VALIDATION.md` — supporting
  evidence (metrics, RealStack facts, keeper-dlq drain-timing follow-up).
- `.planning/phases/39-*/` — confirmed: contains all 39 artifacts EXCEPT `39-VERIFICATION.md` (the gap).

### Plan-count truth for SC3
- Phase-38 directory (`.planning/phases/38-*/`) — count its `38-*-PLAN.md` files for the true plan
  count to write into the ROADMAP progress row.

### Template
- `$HOME/.claude/get-shit-done/templates/` — VERIFICATION.md template shape (if present) for the
  backfilled `39-VERIFICATION.md` structure.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- N/A — doc-only phase, no code.

### Established Patterns
- Other phases' `*-VERIFICATION.md` files (e.g. earlier completed phases) are the structural model
  for the backfilled `39-VERIFICATION.md` (frontmatter `status:`, must-haves, evidence sections).
- This repo's STATE.md/ROADMAP.md encoding hazard: GSD subagents writing these files via Set-Content
  can introduce a BOM / mojibake em-dashes. Whoever edits must verify encoding stays clean (grep for
  mojibake, no BOM) — prefer the Edit tool over wholesale rewrites.

### Integration Points
- Downstream `/gsd-complete-milestone` (run after this phase) consumes the reconciled REQUIREMENTS.md
  and ROADMAP.md — this phase is the precondition that makes archival truthful.

</code_context>

<specifics>
## Specific Ideas

- Reconcile to TRUTH, not to the SC's example numbers. The SC says "34"; the actual count may differ.
  Count the file, state the real total, flag any genuine discrepancy with a one-liner.

</specifics>

<deferred>
## Deferred Ideas

- Milestone archival (`/gsd-complete-milestone v3.7.0`) — explicitly out of scope here; runs after
  Phase 42. On this repo the CLI is broken (always throws "version required for phases archive" and
  does no ROADMAP/REQ archival), so archival is done manually with explicit staged paths.

</deferred>

---

*Phase: 42-v3.7.0-docs-traceability-reconciliation*
*Context gathered: 2026-06-07*
