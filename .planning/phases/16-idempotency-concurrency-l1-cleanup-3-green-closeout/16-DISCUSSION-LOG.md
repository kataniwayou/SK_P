# Phase 16: Idempotency + concurrency + L1 cleanup + 3-GREEN closeout - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-29
**Phase:** 16-idempotency-concurrency-l1-cleanup-3-green-closeout
**Areas discussed:** Concurrent-Start test design, Doc reconciliation, Happy-path E2E scope, Gate-failure no-write proof

---

## ① Concurrent-Start test — what to assert

| Option | Description | Selected |
|--------|-------------|----------|
| (a) Observational / non-flaky | Two parallel Starts → both 204, no crash, final L2 structurally valid; documents interleave | ✓ |
| (b) Strict last-write-wins | Assert final L2 == exactly one writer's payload | |
| (c) Distinct workflowIds | Sidesteps real contention | |

**User's choice:** (a) — locked as recommended.
**Notes:** No Redis lock (last-write-wins by design); (b) flakes under genuine key interleave. TEST-REDIS-08 wording is "documents the behavior," not "proves a winner." Must survive the 3-GREEN cadence.

---

## ② Stale-doc reconciliation

| Option | Description | Selected |
|--------|-------------|----------|
| (a) Doc-first amendment commit | Plan #1 rewrites REQUIREMENTS TEST-REDIS-09 + ROADMAP Phase 16 SC2/SC5, then facts follow | ✓ |
| (b) Leave docs, reconcile at milestone-close | Facts to amended behavior only | |

**User's choice:** (a) — locked as recommended.
**Notes:** Matches Phase 11/15 doc-first precedent; closes the flagged SC2/SC5 inversion NOTE (ROADMAP line 125) and the stale TEST-REDIS-09 "Stop NOT delete" clause.

---

## ③ Happy-path E2E scope (TEST-REDIS-06)

| Option | Description | Selected |
|--------|-------------|----------|
| (a) New dedicated full-HTTP fact | POST /start → GET all 3 keyspaces → System.Text.Json round-trip | ✓ |
| (b) Lean on existing RedisProjectionWriterFacts | Writer-isolation facts treated as sufficient | |

**User's choice:** (a) — locked as recommended.
**Notes:** Writer facts test the writer in isolation; (a) exercises the real HTTP→service→Redis path incl. X-Correlation-Id + per-workflow Start loop.

---

## ④ Gate-failure no-write proof (TEST-REDIS-07)

| Option | Description | Selected |
|--------|-------------|----------|
| (a) One consolidated new fact class | Drives all 4 failure types + SCAN-asserts no keys for failed workflowId | ✓ |
| (b) Amend each of the 4 Phase-14 gate facts in place | Add SCAN assertion to existing facts | |

**User's choice:** (a) — locked as recommended.
**Notes:** Leaves Phase 14 facts untouched (no regression risk); clean TEST-REDIS-07 ownership; SCAN-only.

---

## Claude's Discretion

- New test class names / file placement / `[Trait]` tagging.
- Whether D-02 idempotency assertion extends `ReStart_Removes_Orphan_Step` or a new class.
- Observable delta proving "second write reflected".
- Whether the E2E reuses `OrchestrationLogsE2ETests` infra.
- Whether gate scripts are copied to `phase-16-close.*` or reused with a parameter.

## Deferred Ideas

- Milestone-close (PROJECT.md evolution / archive / version bump) → separate `/gsd-complete-milestone`.
- Strict last-write-wins concurrency assertion (rejected as flaky).
- 15-CONTEXT carry-forward deferrals: liveness lifecycle, stopCorrelationId, processor GC, jobId/interval semantics, OBSERV-REDIS-04.
