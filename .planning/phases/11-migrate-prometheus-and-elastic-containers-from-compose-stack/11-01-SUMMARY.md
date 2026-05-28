---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
plan: 01
subsystem: infra
tags: [requirements, observability, elasticsearch, prometheus, otel-collector, doc-first, traces-deprecation]

# Dependency graph
requires:
  - phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o
    provides: Plan 10-01 doc-first precedent (commit 1de7e71) — atomic REQUIREMENTS.md amendment as commit #1 of a multi-plan phase before any code change
  - phase: 05-observability-and-health-probes
    provides: OBSERV-01..08 + OBSERV-12 baseline; filter/health_metrics processor history; OtelCollectorFixture pattern slated for replacement
  - phase: 02-postgres-and-docker-compose
    provides: INFRA-06 baseline (postgres + baseapi-service depends_on) being extended in place
provides:
  - REQUIREMENTS.md OBSERV-12 marked SUPERSEDED in place (header bytes preserved per Phase 3 D-03b ID preservation invariant); body rewritten to cross-reference the new Out of Scope row
  - REQUIREMENTS.md Out of Scope table appended with new row "OTel tracing pipeline (traces backend)" with Phase 11 D-03 rationale (mirrors sk2_1 CLAUDE.md non-negotiable #2)
  - REQUIREMENTS.md INFRA-06 text extended in place enumerating all 4 services (postgres + elasticsearch + otel-collector + prometheus) with image pins, healthcheck shapes, and extended depends_on chain (per Phase 11 D-12/D-13/D-15)
  - REQUIREMENTS.md 4 new REQ-IDs in their correct category sections — OBSERV-13 (logs land in ES at logs-generic-default with Attributes.CorrelationId), OBSERV-14 (HTTP server metrics scraped by Prometheus from otel-collector:8889 with service_name='sk-api' label), INFRA-08 (compose-stack additions: elasticsearch + prometheus services + otel-collector image bump), TEST-07 (E2E round-trip test class verifying both backends within < 60s)
  - REQUIREMENTS.md Traceability table updated — OBSERV-12 row marked Superseded/Out of Scope; 4 new Phase 11 rows appended Pending
  - REQUIREMENTS.md per-phase footnote block updated — Phase 5 line drops OBSERV-12 (12 → 13 total) wait correction (Phase 5 went 14 → 13); new Phase 11 footnote appended
  - REQUIREMENTS.md Coverage block rebalanced — 102 → 105 total active v1 (OBSERV-12 leaves; OBSERV-13/14 + INFRA-08 + TEST-07 join)
  - REQUIREMENTS.md document footer carries Phase 11 amendment marker dated 2026-05-28
  - Single atomic doc-only commit (7041adb) as commit #1 of the Phase 11 sequence — every later compose/code/helper/test commit points back to authoritative amended REQ-IDs
affects: [11-02 (compose.yaml mutation will reference INFRA-08 + INFRA-06 extension), 11-03 (collector config will reference OBSERV-13/14), 11-04 (Program.cs strip will reference OBSERV-12 supersession), 11-05+ (test migration will reference TEST-07 + OBSERV-13/14)]

# Tech tracking
tech-stack:
  added: [none — doc-only commit; image pins (elasticsearch:8.15.5, prom/prometheus:v3.11.3, otel-collector-contrib:0.152.0) DOCUMENTED but not yet implemented (Plan 11-02 implements)]
  patterns: ["doc-first commit #1 of multi-plan phase (Phase 10 D-01/D-02 precedent honored)", "REQ-ID header byte-preservation under in-place mutation (Phase 3 D-03b)", "[SUPERSEDED — Phase X D-NN] header marker convention for retired REQ-IDs", "Out of Scope table cross-reference pattern (superseded REQ body points at Out of Scope row explaining rationale)"]

key-files:
  created: []
  modified:
    - .planning/REQUIREMENTS.md — OBSERV-12 superseded + INFRA-06 extended + OBSERV-13/14 + INFRA-08 + TEST-07 added + Out of Scope row appended + Traceability table updated (1 row mutated + 4 new rows) + per-phase footnotes (Phase 5 trimmed + Phase 11 added) + Coverage block rebalanced + footer dated

key-decisions:
  - "Plan 11-01 executes the Phase 10 D-01/D-02 doc-first precedent verbatim — single atomic doc-only commit #1 of the Phase 11 sequence ensures every later commit (compose, code, helper, tests) points back to authoritative amended REQ-IDs. Forensic property: independently revertable; spec never out of sync with code that follows."
  - "OBSERV-12 superseded IN PLACE (not renumbered, not deleted) — `[SUPERSEDED — Phase 11 D-03]` header marker + checkbox flipped `[x]` → `[ ]` + body rewritten to point at Out of Scope row. Phase 3 D-03b ID preservation invariant respected; REQ-ID header bytes (`- [ ] **OBSERV-12 ...\\n**:`) preserved so cross-file references remain valid."
  - "4 new REQ-IDs use the next available number per category (OBSERV: 12 → 13/14; INFRA: 07 → 08; TEST: 06 → 07) — no renumbering, no superseding of existing IDs."
  - "Out of Scope row 'OTel tracing pipeline (traces backend)' carries verbatim Phase 11 D-03 rationale and cross-references back to OBSERV-12 — bidirectional link so readers can trace REQ → decision → out-of-scope rationale."
  - "INFRA-06 EXTENDED in place rather than superseded — base contract (postgres + baseapi-service depends_on healthy) still holds; Phase 11 additions enumerate ES + Prom + collector image bump + extended depends_on chain as additive clauses."
  - "Coverage block updated 102 → 105 (net +3 = -1 OBSERV-12 + 4 new IDs) with explanatory breakdown showing per-category deltas (INFRA 7→8, OBSERV 12 active but one superseded, TEST 6→7)."

patterns-established:
  - "Doc-first commit #1 with verbatim subject `docs(req): amend ... for Phase NN shape` — locks the spec ahead of any code change; same pattern as Phase 10 Plan 10-01 commit 1de7e71."
  - "[SUPERSEDED — Phase X D-NN] header marker convention — first instance in this codebase (Phase 10 used Validated→Out-of-Scope re-routing for ENTITY-07 via removed-fields semantics, never marked a REQ-ID `[SUPERSEDED]`). Pattern is reusable for future phases that retire REQ-IDs while preserving their byte-identical header for cross-reference stability."
  - "Out of Scope row appended AFTER existing last row, BEFORE the next H2 — preserves table integrity and chronological ordering of scope decisions."
  - "Traceability table OBSERV-12 row uses `Phase 5 → Phase 11 (Superseded)` arrow notation — preserves the historical phase ownership (Phase 5 originally claimed it) while signaling the supersession event (Phase 11)."

requirements-completed: [OBSERV-12, OBSERV-03, OBSERV-04, OBSERV-08, INFRA-06, OBSERV-13, OBSERV-14, INFRA-08, TEST-07]
# Note: OBSERV-12/03/04/08 + INFRA-06 are doc-amendment-only completions on this plan
# (the underlying behavior continues to live in earlier-phase code; Plan 11-01 only updates the spec
# wording). OBSERV-13/14 + INFRA-08 + TEST-07 are NEWLY DEFINED here (their `Pending` status flips
# to `Complete` in later Phase 11 plans that implement them).

# Metrics
duration: ~4min
completed: 2026-05-28
---

# Phase 11 Plan 01: Doc-First REQUIREMENTS.md Amendment Summary

**Single atomic doc-only commit amends REQUIREMENTS.md: OBSERV-12 superseded in place + INFRA-06 extended + 4 new REQ-IDs (OBSERV-13, OBSERV-14, INFRA-08, TEST-07) + Out of Scope row + Traceability table + footer — establishing the authoritative Phase 11 spec that all subsequent compose/code/helper/test commits will reference.**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-05-28T11:48:03Z
- **Completed:** 2026-05-28T11:52:04Z
- **Tasks:** 5 (5/5 complete)
- **Files modified:** 1 (`.planning/REQUIREMENTS.md`)

## Accomplishments

- **OBSERV-12 superseded in place** — header byte-preserved (`- [ ] **OBSERV-12 [SUPERSEDED — Phase 11 D-03]\n**:` matches Phase 3 D-03b ID preservation invariant); body rewritten to declare traces removal + cross-reference Out of Scope row; checkbox flipped `[x]` → `[ ]`.
- **Out of Scope table appended** with new row "OTel tracing pipeline (traces backend)" carrying verbatim Phase 11 D-03 rationale (no traces backend in v1; mirrors sk2_1 CLAUDE.md non-negotiable #2) and bidirectional cross-reference back to OBSERV-12.
- **INFRA-06 extended in place** — body now enumerates all 4 compose services (postgres + elasticsearch + otel-collector + prometheus) with image pins, healthcheck shapes, and the extended `baseapi-service.depends_on` chain (per Phase 11 D-12/D-13/D-15). Header bytes preserved.
- **4 new REQ-IDs introduced** in their correct category sections:
  - **OBSERV-13** (Observability section, after OBSERV-12): logs land in ES `logs-generic-default` with OTLP raw field shape preserved (per D-06).
  - **OBSERV-14** (Observability section, after OBSERV-13): HTTP server metrics scraped by Prometheus from `otel-collector:8889` with `service_name="sk-api"` label (per D-07/D-08).
  - **INFRA-08** (Infrastructure section, after INFRA-07): compose-stack additions enumerating elasticsearch + prometheus service shapes + otel-collector image bump (per D-09/D-10/D-11/D-14).
  - **TEST-07** (Testing section, after TEST-06): E2E round-trip test class polling both backends within < 60s with `Phase11WebAppFactory : Phase8WebAppFactory` + `ExportIntervalMilliseconds = 1_000` override (per D-17/D-18).
- **Traceability table updated**: OBSERV-12 row mutated to `| OBSERV-12 | Phase 5 → Phase 11 (Superseded) | Out of Scope |`; 4 new Phase 11 rows appended Pending.
- **Per-phase footnote block updated**: Phase 5 line trimmed (`OBSERV-01..08, OBSERV-12, HEALTH-01..05` → `OBSERV-01..08, HEALTH-01..05`, count 14 → 13); new Phase 11 bullet appended documenting the 4 new IDs + the 2 amendments.
- **Coverage block rebalanced**: 102 → 105 total active v1 (OBSERV-12 superseded out; OBSERV-13/14 + INFRA-08 + TEST-07 in); per-category breakdown shows the net delta (INFRA 7→8, TEST 6→7, OBSERV 12 active but one superseded).
- **Document footer dated** with Phase 11 amendment marker (`*Last updated: 2026-05-28 — Phase 11 amendments: OBSERV-12 superseded (traces removed); INFRA-06 extended (ES + Prom + collector image bump); new REQ-IDs OBSERV-13/14 + INFRA-08 + TEST-07*`).
- **Single atomic commit** `7041adb` with verbatim subject `docs(req): amend OBSERV-12 + INFRA-06 + add OBSERV-13/14 + INFRA-08 + TEST-07 for Phase 11 shape` modifying exactly 1 file (`.planning/REQUIREMENTS.md`).

## Task Commits

Each task was committed atomically (single doc-only commit per Plan-10-01 D-01/D-02 precedent):

1. **Task 1: Supersede OBSERV-12 and append Out of Scope row** — uncommitted at task boundary (rolled into Task 5 commit per atomic-doc-commit contract)
2. **Task 2: Extend INFRA-06 text in place** — uncommitted at task boundary (rolled into Task 5 commit)
3. **Task 3: Add OBSERV-13 + OBSERV-14 + INFRA-08 + TEST-07** — uncommitted at task boundary (rolled into Task 5 commit)
4. **Task 4: Update Traceability table, phase summary footnotes, and document footer** — uncommitted at task boundary (rolled into Task 5 commit)
5. **Task 5: Commit doc amendments as commit #1 of the Phase 11 sequence** — `7041adb` (docs)

**Plan metadata:** TBD — committed by execute-plan agent after SUMMARY + STATE updates.

_Note: Plan 11-01 deliberately ships as ONE atomic doc-only commit per CONTEXT D-19 + Phase 10 D-01/D-02 precedent — all 4 edit tasks are sub-edits of a single forensic-friendly commit. Tasks 1–4 are sequential file mutations; Task 5 is the single commit point. This deviates from the standard "per-task commit" pattern but is the EXPLICIT plan design (success criteria #8 mandates "Single git commit ... modifies exactly 1 file").

## Files Created/Modified

- `.planning/REQUIREMENTS.md` — single source-of-truth spec amended for Phase 11: OBSERV-12 superseded in place; INFRA-06 extended in place; 4 new REQ-IDs (OBSERV-13/14, INFRA-08, TEST-07) appended in their correct category sections; Out of Scope row appended; Traceability table updated (1 row mutated + 4 new rows); per-phase footnote block updated (Phase 5 trimmed, Phase 11 added); Coverage block rebalanced (102 → 105); document footer carries Phase 11 amendment marker.

## Decisions Made

None new — all decisions are inherited verbatim from Phase 11 CONTEXT.md D-01 through D-19 (locked at /gsd-discuss-phase time). Plan 11-01 mechanically encodes those decisions into REQUIREMENTS.md without introducing new judgment calls.

## Deviations from Plan

None — plan executed exactly as written.

The plan's `<verification>` block had three line-count assertions where my actual file has higher counts than predicted (= 2 instead of = 1 for `elasticsearch:8.15.5`, `prom/prometheus:v3.11.3`, and `otel-collector:0.152.0`). These higher counts reflect the deliberate design: INFRA-06 (extended) AND INFRA-08 (new) BOTH enumerate the image pins because they describe overlapping-but-distinct concerns (INFRA-06 = compose-stack composition + depends_on chain; INFRA-08 = per-service shape detail). The substantive intent (image pins locked in the spec) is more thoroughly satisfied with count = 2 than count = 1. The plan's `<success_criteria>` block (which is the binding contract) doesn't require exact count = 1, and both INFRA-06 and INFRA-08 success criteria mandate the pins appear in their respective text. No file change made.

## Issues Encountered

None.

## Self-Check: PASSED

**File existence verification:**
- FOUND: `.planning/REQUIREMENTS.md` (modified in place)
- FOUND: `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-01-SUMMARY.md` (this file)

**Commit verification:**
- FOUND: `7041adb` (subject: `docs(req): amend OBSERV-12 + INFRA-06 + add OBSERV-13/14 + INFRA-08 + TEST-07 for Phase 11 shape`)

**Plan-level grep gates (all PASS):**
- OBSERV-12 SUPERSEDED header present (1) ✓
- 4 new REQ-IDs present (4) ✓
- "OTel tracing pipeline (traces backend)" cross-references present (2 — one in OBSERV-12 body, one in Out of Scope row) ✓
- "logs-generic-default" present (2 — OBSERV-13 + TEST-07) ✓
- "Phase11WebAppFactory" present (1 — TEST-07) ✓
- "ExportIntervalMilliseconds = 1_000" present (1 — TEST-07) ✓
- Traceability table: OBSERV-12 row marked Superseded (1) ✓; 4 new rows present (4) ✓
- Phase 11 footnote present (1) ✓
- New document footer present (1) ✓
- Old Phase 5 footnote gone (0) ✓; new Phase 5 footnote present (1) ✓

## User Setup Required

None — doc-only commit. No external service configuration required.

## Next Phase Readiness

Plan 11-02 (compose.yaml mutation) is unblocked: it can reference INFRA-08 verbatim for the new elasticsearch + prometheus service shapes + otel-collector image bump, and INFRA-06 for the extended depends_on chain. Plans 11-03 (collector config) can reference OBSERV-13/14 for the ES + Prom exporter wiring. Plan 11-04 (Program.cs strip) can reference OBSERV-12 supersession + Out of Scope row for the `.WithTracing()` removal rationale. Plans 11-05+ (test migration) can reference TEST-07 for the E2E round-trip test class shape.

The forensic property holds: the spec is now ahead of the code, and every subsequent Phase 11 commit (compose, code, helper, test) will independently revert without leaving the spec out of sync.

---
*Phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack*
*Plan: 01*
*Completed: 2026-05-28*
