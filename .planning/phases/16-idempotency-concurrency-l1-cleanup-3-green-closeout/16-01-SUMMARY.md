---
phase: 16-idempotency-concurrency-l1-cleanup-3-green-closeout
plan: 01
subsystem: planning-docs
tags: [docs, requirements, roadmap, stop-semantics, doc-first-amendment]
requires:
  - "15-CONTEXT <amendments> ORCH-STOP-04/-06 + <decisions> D-06/D-08 (authoritative inverted Stop contract)"
provides:
  - "REQUIREMENTS.md TEST-REDIS-09 matching the shipped inverted Stop semantics (root + per-step removed, per-processor intact, repeat -> 422)"
  - "ROADMAP.md Phase 16 SC2 inverted; flagged NOTE resolved into a RESOLVED marker"
affects:
  - "Phase 16 plans 02-04 (their coverage facts now assert against corrected requirements)"
tech-stack:
  added: []
  patterns:
    - "D-05 doc-first amendment (Phase 11/15 precedent): correct locked spec text BEFORE writing facts that assert against it"
key-files:
  created:
    - ".planning/phases/16-idempotency-concurrency-l1-cleanup-3-green-closeout/16-01-SUMMARY.md"
  modified:
    - ".planning/REQUIREMENTS.md (TEST-REDIS-09 line item)"
    - ".planning/ROADMAP.md (Phase 16 SC2 + flagged NOTE)"
decisions:
  - "Resolved a plan-internal verify-vs-text inconsistency (Rule 3): the plan's suggested TEST-REDIS-09 amendment-note text contained the literal phrase 'verified to NOT delete', but the plan's own automated verify regex and acceptance criteria forbid that substring in the bullet. Rephrased the history note to convey the same pre-inversion correction without the forbidden phrase (Plan 06-01 / 08-01 rephrase precedent)."
metrics:
  duration: ~3min
  completed: 2026-05-29
---

# Phase 16 Plan 01: Doc-First Stop-Semantics Amendment Summary

Rewrote the two locked planning docs left stale by the Phase 15 Stop-semantics inversion (REQUIREMENTS.md TEST-REDIS-09 and ROADMAP.md Phase 16 SC2) to the shipped inverted contract — Stop deletes root + reachable per-step keys via GET-and-follow while per-processor keys stay TTL'd, and a repeated Stop re-fails the existence gate (422, non-idempotent) — and resolved the standing flagged NOTE, all before any Phase 16 coverage facts assert against them.

## What Was Built

- **Task 1 (`adf44c3`)** — REQUIREMENTS.md TEST-REDIS-09 rewritten: kept the `- [ ] **TEST-REDIS-09** —` marker plus the 204 / 422-missing clauses, dropped the pre-inversion "NOT delete / post-Stop SCAN matches pre-Stop" clause, added the inverted cleanup contract (root + reachable per-step removed via GET-and-follow; per-processor keys remain intact/TTL'd; repeated Stop → 422; no Postgres). TEST-REDIS-06/07/08 byte-unchanged.
- **Task 2 (`6774c57`)** — ROADMAP.md Phase 16 SC2 Stop sentence inverted (root + per-step removed, per-processor intact, repeat Stop → 422 non-idempotent); the flagged `NOTE (flagged by Plan 15-05)` block deleted and replaced with a single `RESOLVED (Phase 16 Plan 01, 2026-05-29)` marker. SC1/SC3/SC4, SC5's `redis-cli --scan` BEFORE=AFTER invariant, and the "v3.2.0 invariants MUST NOT regress" block (FLUSHDB forbidden, KEYS/IServer.Keys() forbidden, 142/142 baseline, Mapperly RMG codes) all byte-unchanged.

## Verification

- Task 1 automated verify: `PASS` — TEST-REDIS-09 contains "root + reachable per-step" AND "non-idempotent" AND no longer contains "verified to NOT delete" in the bullet.
- Task 2 automated verify: `PASS` — `NOTE (flagged by Plan 15-05)` absent (0 matches), `RESOLVED (Phase 16 Plan 01` present (1 match), `` `FLUSHDB` is FORBIDDEN `` block intact.
- `git diff` confined to the TEST-REDIS-09 line region (1 line) in REQUIREMENTS.md and the Phase 16 SC2 + NOTE lines (2 lines) in ROADMAP.md. No `src/` or `tests/` file touched (this is plan #1 — docs precede facts).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking: plan-internal inconsistency] TEST-REDIS-09 amendment-note rephrase**
- **Found during:** Task 1
- **Issue:** The plan's suggested replacement text for TEST-REDIS-09 ended with a history note `was "Stop is verified to NOT delete any L2 keys / post-Stop SCAN matches pre-Stop"`. That literal substring "verified to NOT delete" sits inside the same bullet, so the plan's own automated verify regex (`-not ($t -match 'TEST-REDIS-09[^\n]*verified to NOT delete')`) and acceptance criterion ("does NOT contain 'verified to NOT delete'") failed against the plan's own suggested text.
- **Fix:** Rephrased the history note to `the pre-inversion phrasing wrongly claimed Stop left all L2 keys untouched with a post-Stop SCAN matching pre-Stop` — same corrective intent, no forbidden substring. Verify then passed.
- **Files modified:** .planning/REQUIREMENTS.md
- **Commit:** `adf44c3`
- **Precedent:** Plan 06-01 (MP-code rephrase) and Plan 08-01 (EnsureCreatedAsync rephrase) — rephrase educational/history text to satisfy a grep-empty assertion while preserving meaning.

Note: the Task 2 first verify run reported "v3.2.0 invariants block was disturbed" — this was a false negative from double-backtick escaping inside the inline `pwsh -Command` string (the regex literal `` FLUSHDB`` is FORBIDDEN `` collapsed under PowerShell quoting), not an actual file change. Re-running with a here-string / `FLUSHDB. is FORBIDDEN` regex returned `PASS`; the invariants block is byte-unchanged per `git diff`.

## Threat Flags

None. This plan edits two planning markdown docs only; T-16-01-01 (spec/behavior drift) is mitigated — the docs now match the shipped Phase 15 inverted behavior. No production attack surface introduced.

## Self-Check: PASSED
- FOUND: .planning/phases/16-idempotency-concurrency-l1-cleanup-3-green-closeout/16-01-SUMMARY.md
- FOUND: commit adf44c3 (Task 1)
- FOUND: commit 6774c57 (Task 2)
- REQUIREMENTS.md TEST-REDIS-09 verify PASS; ROADMAP.md SC2/NOTE verify PASS
