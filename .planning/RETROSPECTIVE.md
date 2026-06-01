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

## Milestone: v3.4.0 — BaseConsole + Orchestrator Messaging

**Shipped:** 2026-06-01
**Phases:** 9 (17-24 + 24.1) | **Plans:** 31 | **Commits:** 273 | **Timeline:** ~3 days

### What Was Built
A reusable `BaseConsole.Core` Generic-Host library + a runnable `Orchestrator` console over MassTransit/RabbitMQ; body-carried CorrelationId proven end-to-end into Elasticsearch; the full orchestrator lifecycle (L1 hydration, Quartz scheduling, entry-step dispatch, stop teardown); the processor→orchestrator result round-trip with L1-only step advancement; and a gating redesign (24.1) replacing the boot gate/redelivery/plugin with L2-existence dedup + atomic Stop + L1-only graceful result.

### What Worked
- **Single-source-of-truth key scheme (Phase 21 HARDEN-03).** Hoisting `L2ProjectionKeys` into `Messaging.Contracts` with thin writer/reader forwarders made cross-process key desync structurally impossible — the integration check confirmed zero wiring gaps across the WebApi↔Orchestrator boundary.
- **Deferring a human-UAT item to an automated proof.** Phase 19's live-correlation item was explicitly deferred to Phase 20's `CorrelationPropagationE2ETests` rather than left as a manual gate — the right call; it became a permanent regression test.
- **Triple-SHA close gates (psql + redis-cli + rabbitmqctl) at 3× GREEN.** Caught leaks/orphans deterministically across phases 20/21/22.
- **Gap-closure as a decimal phase (24.1).** A FAILED Phase 24 verification was closed by a tightly-scoped redesign phase rather than reopening Phase 24 — clean history, locked SPEC, operator-approved checkpoint.

### What Was Inefficient
- **The boot-gate/scheduled-redelivery/plugin design (Phase 24) was over-built and had to be torn out in 24.1.** The `rabbitmq_delayed_message_exchange` plugin dependency wasn't available in the compose stack, degrading redelivery to exhaust-and-error — a design that an earlier "what infra does this actually require?" check would have avoided. The L1-only graceful result path (already present) turned out to be the sole arbiter needed.
- **Verification-artifact drift.** Phase 20 shipped with no VERIFICATION.md (close gate substituted) and Phase 19 stayed `human_needed`; the REQUIREMENTS.md traceability table fell two milestone-extensions behind. All reconciled at audit, but it made the milestone audit return `gaps_found` on bookkeeping rather than code.
- **`gsd-sdk milestone.complete` is broken in this SDK build** (calls `phasesArchive([])` with empty args → always throws); milestone archival had to be done manually.

### Patterns Established
- Body-carried `ICorrelated.CorrelationId` (NOT the MassTransit envelope) as the per-stage correlation handoff, with the literal `"CorrelationId"` MEL scope key shared via `Messaging.Contracts`.
- Fan-out (instance-unique temporary/auto-delete endpoints) for control vs. competing-consumer (shared named queue) for results — proven with two in-process bus instances.
- Orchestrator never mutates L2 (read-only into L1); WebApi owns all L2 writes + teardown.
- L2-existence-probe dedup + parent-index compensation (SREM/SADD) as the first-win idempotency mechanism.

### Key Lessons
- **Check infra availability before designing around it.** The delayed-message-scheduler plugin dependency caused a verification failure that a one-line "is this plugin in compose?" check would have surfaced at design time.
- **Reconcile verification artifacts at phase close, not milestone close.** Letting Phase 19/20 artifacts and the traceability table drift turned a clean milestone into a bookkeeping audit.
- **Trust the dotnet summary line, not wrapper exit codes** (carried lesson — the 24.1 clean-build gate relied on `Failed: 0`, not the wrapper).

### Cost Observations
- Model mix: orchestration on Opus; verification/integration-check/review on Sonnet.
- Notable: the milestone audit's parallel integration-checker (Sonnet) confirmed every REQ-ID WIRED across 9 phases in one pass — high signal for the cost.

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
