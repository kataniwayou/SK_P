# Project Retrospective

*A living document updated after each milestone. Lessons feed forward into future planning.*

## Milestone: v3.3.0 — Orchestration L3 → L1 → L2 Build Pipeline

**Shipped:** 2026-05-29
**Phases:** 5 (12-16) | **Plans:** 26 | **Sessions:** 1 intensive day

### What Was Built
- Redis as a soft compose-stack tier wired into the DI graph (`AddBaseApiRedis`, `RedisFixture`, dead-Redis resilience) without touching Phase 5 HEALTH contracts.
- `OrchestrationService` decomposed into a thin orchestrator + 4 seams; transient `WorkflowGraphSnapshot` (L1) loaded from Postgres per Start and deterministically disposed.
- Validation gates in locked order (existence → cycle DFS → schema-edge → payload-config-schema) returning 422 + RFC 7807 at the first failed gate; closes v2-deferred VALID-21 at orchestration-start scope.
- L2 Redis materialized projection (3 keyspaces) + Stop existence gate with GET-and-follow cleanup.
- Idempotency/concurrency regression facts + the 3-GREEN dual-SHA close gate at 235 passed.

### What Worked
- **Doc-first amendments** — Phase 16 Plan 01 rewrote REQUIREMENTS/ROADMAP to the inverted Stop contract before touching code, keeping the spec and tests aligned through a mid-milestone semantics change.
- **Phase-close gate as a verbatim copy** — phase-16-close scripts were byte-faithful copies of the proven Phase-12 gate (relabel-only); the 3-run stability assertion (same Passed count ×3, not a fixed literal) absorbed suite growth (142 → 235) without script edits.
- **Soft-dependency design for Redis** — making `/health/ready` Redis-agnostic kept the v1 CRUD surface fully available and let the new tier land without destabilizing the shipped product.
- **Real-backend integration facts throughout** — every fact ran against real Postgres + real Redis, so the close gate was empirical, not mocked.

### What Was Inefficient
- **Stop scope churned twice** (mirror-of-Start eviction → existence-only → root+step deletion) across milestone-gathering and Phase 15 CONTEXT amendments, forcing a reconciliation pass (15-05) and a Phase 16 doc-first rewrite. Earlier lock-down of the Stop contract would have avoided the rework.
- **Verification documentation lag** — Phase 14 VERIFICATION sat at `human_needed` after its human-UAT had already passed; the stale status surfaced as false "debt" at milestone close and needed a reconciliation commit.
- **Nyquist VALIDATION left in draft** on 3 of 5 phases — the validation loop wasn't formally closed even though the close gate provided stronger empirical evidence.

### Patterns Established
- **Phase-close gate = relabel-only copy of the prior proven gate**; never re-derive the dual-SHA logic.
- **3-GREEN = stability assertion** (same Passed count across 3 runs), not a fixed count literal — survives suite growth.
- **Inverted/changed contracts land doc-first** — amend REQUIREMENTS/ROADMAP/SC in a dedicated plan before code.
- **`IServer.Keys()`/`KEYS` forbidden in production; targeted GET-and-follow traversal** for any graph-scoped Redis cleanup.

### Key Lessons
1. **Lock irreversible contract semantics (like Stop's mutation behavior) before implementation** — scope that churns across CONTEXT amendments multiplies reconciliation cost downstream.
2. **Reconcile VERIFICATION status the moment the human item resolves** — a stale `human_needed` reads as real debt at milestone close even when the work is done.
3. **A stability-based close gate (BEFORE==AFTER, same-count-×3) is more durable than literal baselines** — the evolved `psql \l` baseline at the Phase 16 gate held the true invariant while the frozen Phase-12 literal had gone stale.

### Cost Observations
- Model mix: ~100% opus (executor + planner both `opus`, profile `quality`).
- Sessions: 1 intensive day (2026-05-29), 158 commits.
- Notable: 26 plans across 5 phases in a single day; per-plan execution mostly 3–25 min (see STATE.md velocity table).

---

## Cross-Milestone Trends

### Process Evolution

| Milestone | Sessions | Phases | Key Change |
|-----------|----------|--------|------------|
| v3.2.0 | 3 days | 11 | Established the reusable BaseApi.Core foundation + 3-GREEN/psql-SHA close gate |
| v3.3.0 | 1 day | 5 | Added Redis L2 tier; extended close gate with `redis-cli --scan` dual-SHA; doc-first contract amendments |

### Cumulative Quality

| Milestone | Tests | Coverage | Zero-Dep Additions |
|-----------|-------|----------|-------------------|
| v3.2.0 | 142 facts × 3 GREEN | real Postgres + ES + Prom | — |
| v3.3.0 | 235 facts × 3 GREEN | + real Redis | Redis soft-dependency (no HEALTH contract change) |

### Top Lessons (Verified Across Milestones)

1. **The 3-consecutive-GREEN + byte-identical SHA close gate catches flakes and resource leaks** — proven across 16 phases now (psql `\l` in v3.2.0, extended with `redis-cli --scan` in v3.3.0).
2. **Bisect-friendly N-commit sequences + doc-first amendments keep spec and code aligned** through surface revisions (Phase 10 in v3.2.0; Phase 16 Stop-contract rewrite in v3.3.0).
