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

## Milestone: v3.6.0 — Idempotent Execution (Exactly-Once-Effect Round-Trip)

**Shipped:** 2026-06-05
**Phases:** 4 (31, 31.1, 32→32.1) | **Plans:** 9 active | **Commits:** 93 | **Timeline:** ~2 days

> Note: v3.5.0 (Processor Console, phases 25-30) shipped 2026-06-02 but was not given its own retrospective section — its formal milestone close was deferred. This section covers v3.6.0 only.

### What Was Built
Exactly-once-effect execution idempotency: deterministic identity `H = SHA-256(correlationId, workflowId, stepId, processorId, EntryId)` (executionId excluded) + effect-first `flag[H]` `Pending → Ack` CAS dedup at both hops, content-addressed two-level L2 (blobs + manifest), N×M manifest fan-out, per-edge content-collapse merge, and a configurable retry budget. Phase 31.1 closed a redis net-zero gap in the close gate. Phase 32 built a cancelled circuit-breaker that Phase 32.1 then **reverted** to plain dead-lettering on exhaustion.

### What Worked
- **Wave-0 reality probes before depending on framework behavior.** Phase 32-01 pinned `GetRetryAttempt() == Limit` and `Fault<EntryStepDispatch>.Message.WorkflowId` round-tripping against a *real* in-memory MassTransit harness before any consumer code relied on them — cleared two load-bearing assumptions (R1/R2) up front.
- **Effect-first dedup as the core invariant.** Producing the downstream effect before the CAS flip made the only residual a collapsed duplicate (never a loss) — the right correctness/complexity trade vs. a transactional outbox (explicitly deferred).
- **Net-deletion as a first-class outcome.** Phase 32.1 reverted the breaker as a clean 161-deletion/0-insertion refactor with grep-verified zero dangling references and the Phase-31 idempotency layer proven byte-intact — a disciplined "undo" rather than leaving dead surface area.
- **Close gate caught the redis leak (Phase 31.1).** The 3×GREEN + triple-SHA gate surfaced per-fire `skp:flag:{H}` name churn from self-rescheduling cron E2E tests — exactly the class of resource leak the gate exists to catch.

### What Was Inefficient
- **The cancelled circuit-breaker was built (5/6 plans) before being reverted.** A two-level breaker (L2 marker + Fault-fanout unschedule) was fully specced, planned, and implemented across Phases 32-01..32-07, then judged not worth its surface area at review (32-REVIEW WR-01) and net-deleted in 32.1. An earlier "what does this actually buy us, and how does it degrade under the infra-down it triggers on?" check would have surfaced that it degrades to plain `_error` anyway — the eventual decision — before the build.
- **Superseded-phase artifact drift.** Phase 32's VERIFICATION/VALIDATION still read `human_needed`/`validated-partial` against code that no longer exists; the audit flagged it as hygiene debt rather than re-statusing at supersession time.
- **REQUIREMENTS.md never rolled forward.** The global traceability table sat stale at v3.5.0 for the entire milestone; v3.6.0 requirements lived only in per-phase SPECs and had to be consolidated at close (and revealed v3.5.0's own archival had never been completed).
- **`milestone.complete` still broken** (carried from v3.4.0) — full archival done manually again.

### Patterns Established
- **Wave-0 hermetic reality probes** against a real in-memory bus before building consumers that depend on framework-internal behavior (retry-attempt numbering, fault payload shape).
- **Effect-first CAS dedup** (`flag[H] = Pending|Ack`, effect before flip) as the canonical exactly-once-effect mechanism at every receiver.
- **Content-addressed identity via a single canonical hash class** so writer/reader desync is structurally impossible (extends the v3.4.0 single-source-of-truth key-scheme pattern to message identity).
- **Reverts are clean net-deletions with grep-verified zero dangling refs + a proof the kept layer is byte-intact**, committed atomically.

### Key Lessons
1. **Pressure-test a mechanism against its own trigger conditions before building it.** The breaker was meant to "cleanly stop" on infra-down but degraded to plain `_error` under exactly those conditions — knowable at design time, learned after implementation.
2. **Re-status superseded artifacts at the moment of supersession.** A reverted phase's VERIFICATION/VALIDATION reading "pending" is misleading debt; flip it when the supersession lands.
3. **Roll REQUIREMENTS.md forward at milestone start (`/gsd-new-milestone`), not at close.** A traceability table that lags by a milestone hides that a *prior* milestone's archival was never finished.

### Cost Observations
- Model mix: orchestration/execution on Opus (profile `quality`); audit integration-checker on Sonnet.
- Sessions: ~2 days (2026-06-04 → 2026-06-05), 93 commits.
- Notable: the milestone's net source delta was small (+2,710 / −255 across 56 files) despite 4 phases — much of the work was a build-then-revert (Phase 32 → 32.1).

## Milestone: v3.7.0 — Keeper (L2-Outage Dead-Letter Recovery & Workflow Pause/Resume)

**Shipped:** 2026-06-07
**Phases:** 10 (33-42, incl. gap-closure 40/41/42) | **Plans:** 32 | **Commits:** 220 | **Timeline:** ~3 days

### What Was Built
A multi-replica `Keeper` console that self-heals the autonomous execution loop through transient L2 outages: `Fault<T>` pub/sub intake → bounded crash-survivable L2 health-probe loop → orchestrator-coordinated workflow pause (Quartz `GetTriggerState` via deterministic `TriggerKey`) → re-inject-by-type to origin riding the receiver's `flag[H]` → resume on recovery → park the unrecoverable in `keeper-dlq`. A per-`H` attempt cap bounds the recover loop; transport exhaustion across all consoles consolidates into one TTL'd `skp-dlq-1`. Uniform `service_name={name}_{version}` + `service_instance_id` metric labels (processor DB-sourced) + a full Keeper meter. Three gap-closure phases (40/41/42) followed the original 33-39 build off a second audit.

### What Worked
- **Spike-first de-risking (Phase 33).** The whole milestone rested on one unproven assumption — that a `Fault<T>` event could be consumed via pub/sub, double-unwrapped, re-injected by type, and collapsed by the receiver's `flag[H]`. Proving it LIVE (close gate `GATE_EXIT=0`) in a standing 819-line regression guard before building anything meant Phases 34-39 built on solid ground, not hope.
- **One consolidated authoritative close gate over per-phase live runs.** Phases 34-37's live halves were deliberately deferred into the single Phase-39 real-stack gate (3×500 GREEN, triple-SHA net-zero) rather than re-run per phase — far cheaper wall-clock, and the gate is the real proof anyway.
- **Quartz as the single source of pause-state truth.** Using deterministic `TriggerKey` + `GetTriggerState` (no L1 state field, no in-memory pending-recovery set) collapsed PAUSE-02/03/04 into idempotent transitions — and keeping pause-state OUT of L2 was the load-bearing design call (L2 is the thing being recovered).
- **Audit → gap-closure phases as the close discipline.** The 2026-06-07 audit's functional/code/doc debt became three tightly-scoped phases (40 cap+drain+shared-handler, 41 WR-01/WR-02, 42 doc reconciliation) rather than ad-hoc fixes — clean history, each closed before archival.

### What Was Inefficient
- **The recover→reinject cap was missing from the original build** (Phases 33-39) and only added in gap-closure Phase 40. A persistent (non-transient) fault would have flooded the stack at ~67 cyc/s/replica — a bound that should have been in the PROBE design from the start, not discovered at audit.
- **A load-bearing regression shipped inside Phase 37 and was mis-filed.** 37-02's deterministic-key `RescheduleAsync` used `ScheduleJob` (add) instead of `RescheduleJob` (replace) → `ObjectAlreadyExistsException` on every self-reschedule (would have stopped all workflows after their first fire). The 37-04 run initially mis-filed it as pre-existing; execution caught + fixed it (`571498f`), but a Wave-0 "does a second fire actually reschedule?" probe would have caught it at design time.
- **The keeper-dlq depth==0 close-gate race** (late give-up park races the AFTER snapshot) cost a `GATE_EXIT=1` and a deterministic-drain follow-up (Phase 40 KHARD-02) — a teardown-timing gap in the give-up E2E.
- **Verification/UAT status drift again.** 6 phases sat `human_needed` and 13 had partial UAT at close — all genuinely consolidated into the Phase-39 gate, but the unreconciled statuses surfaced as 23 audit-open "decisions" at milestone close (the recurring pattern from v3.3.0/v3.4.0/v3.6.0).
- **`milestone.complete` still broken** (carried from v3.4.0/v3.6.0) — full archival done manually a third time.

### Patterns Established
- **Spike-as-standing-regression-guard** — the de-risk spike (Phase 33) becomes a permanent RealStack test, not throwaway; later phases clone its rig (`KeeperFaultIntakeE2ETests`, `KeeperRecoveryE2ETests` are siblings of `FaultRecoverySpikeE2ETests`).
- **Bounded crash-survivable recovery loop** — ack-after-loop so a mid-loop crash redelivers and restarts (at-least-once recovery); `delay × attempts` documented as bounded under the broker's consumer-delivery-ack timeout.
- **Two DLQs split by mechanism** — transport-exhaustion (Immediate(N) → consolidated TTL'd forensic `skp-dlq-1`) vs. domain give-up (explicit Send-then-ack to `keeper-dlq`, depth = the alert).
- **Recovery logic in one shared handler** so a bound/cap lands in exactly one place (KHARD-03) — both consumers are thin delegations.
- **Pause-state in the scheduler, never in the resource being recovered** — Quartz `GetTriggerState` is the source of truth; idempotency via `ConcurrentMessageLimit=1` + idempotent transitions, no bespoke lock.

### Key Lessons
1. **Bound every recover/retry loop at design time, against a persistent (not just transient) fault.** The probe loop was bounded per-intake but the recover→reinject *cycle* across redeliveries was not — a persistent fault is the adversarial case that exposes a missing cap.
2. **Add a Wave-0 probe for the second occurrence, not just the first.** The self-reschedule regression only manifests on the *second* cron fire; "does it work once?" passed while "does it keep working?" was broken.
3. **Reconcile VERIFICATION/UAT status at phase close, every phase.** Four milestones running, the same `human_needed`/partial-UAT drift turns a clean milestone into a 20+-item audit-open decision list. The consolidation-into-one-gate design is sound; the per-phase status hygiene is what lags.
4. **Drain domain DLQs deterministically in E2E teardown** when a close gate asserts depth==0 — a poll-until-stably-empty strategy beats a single check that can race a late async park.

### Cost Observations
- Model mix: orchestration/execution on Opus (profile `quality`); audit integration-checker + verifiers on Sonnet.
- Sessions: ~3 days (2026-06-05 → 2026-06-07), 220 commits (137 docs — heavy planning/artifact churn across 10 phases incl. 3 gap-closure).
- Notable: small source delta (+6,287 / −48 across 75 src+test files) for a whole new console + recovery engine — most of the new surface is the `Keeper` project + shared handler; the rest reuses v3.4.0/v3.6.0 tiers unchanged.

## Milestone: v6.0.0 — Config & Payload Validation Hardening

**Shipped:** 2026-06-13
**Phases:** 3 (56-58) | **Plans:** 11 | **Commits:** ~50 (13 feat) | **Timeline:** ~1 day (2026-06-12 → 2026-06-13)

### What Was Built
A breaking `BaseProcessor` author-contract change that makes runtime payload-deserialization exceptions structurally impossible (except in-transit mutation). Authors now inherit a typed `ProcessorConfig` and the framework deserializes the dispatch payload into it (replacing the raw-string seam); at startup the processor fetches the `ConfigSchemaId` definition and **Gate A** (`ConfigSchemaCoverageCheck`) validates `schema ⊨ configType` — on a clash it withholds `MarkHealthy` (terminal), so no L2 liveness is written and the existing orchestration-start `ProcessorLivenessValidator` blocks the workflow 422. TOCTOU closed by frozen-once-referenced schema immutability (409). Composes with the shipped WebAPI Gate B into the transitive invariant `payload ⊨ ConfigSchemaId ∧ ConfigSchemaId ⊨ configType ⟹ payload deserializes`. Proven live in Phase 58 (config-incompatible → 422; compatible → 204) behind an N=3 triple-SHA net-zero close gate.

### What Worked
- **Spike-locks-the-rule-table (Phase 57 Wave 0).** Before implementing the covers-checker, a blocking spike drove the 3 highest-risk `System.Text.Json` type-clash verdicts (string-enum→CLR-enum, number↔int, null→non-nullable value-type) through the *real* `ProcessorConfig.SerializerOptions` and confirmed them empirically — so the 18-row rule table encoded measured behavior, not assumed behavior.
- **Conservative-by-design coverage check.** Gate A flags only a real both-present clash and defaults unwalked constructs (tuple items, `$ref`/`allOf`/`oneOf`/`anyOf` composition) to FINE — never a false-positive block — and is SSRF-safe (no `Evaluate`, no external `$ref`). The INFO-level gaps are documented, not silent.
- **Faithful-clone negative subject behind a compose profile.** `Processor.BadConfig` is a byte-faithful `Processor.Sample` clone whose only delta is a clashing `BadConfig(int Quantity)` TConfig + its own embedded SourceHash (distinct procId), gated behind a `badconfig` compose profile so it never joins the default dev stack — a clean, isolated CFG-08 subject.
- **Three-signal causation for the live block proof.** CFG-08 didn't just assert 422 — it proved *why*: ES Gate-A clash log scoped to `service.name=processor-badconfig` + `skp:{badId}` stably absent across >1 heartbeat + Start 422. The negative control (CFG-09 → 204) rules out a false-positive gate.
- **Net-zero close-gate protocol reused verbatim.** `phase-58-close.ps1` cloned the proven phase-55 triple-SHA protocol + the two-schema/two-processor CREATE-IF-ABSENT seed — N=3 GREEN, BEFORE==AFTER, DLQ 0, first time.

### What Was Inefficient
- **3-source requirements drift, again.** `CFG-05` was exercised GREEN in 57-02 but its `requirements-completed` marking was deferred to "avoid over-claiming a partially-wired requirement"; 57-03 then wired it but listed CFG-03/04/06/07 and forgot CFG-05. The audit's SUMMARY-frontmatter cross-check flagged it as `partial → verify manually`; manual verification confirmed it satisfied. Bookkeeping, not implementation — but the recurring pattern (v3.3/3.4/3.6/3.7 all had verification/UAT status drift) shows up here as requirements-frontmatter drift.
- **VALIDATION strategy docs never status-updated post-execution.** Phases 57/58 carry `nyquist_compliant` / task-status columns left at their pre-execution values (57 `wave_0_complete:false` with tasks ⬜ pending; 58 `status:draft`), so the audit's Nyquist scan reads them PARTIAL even though the actual tests are green. Discovery-only/advisory, but the docs lie about state.
- **ROADMAP plan-checkbox drift.** 57-03-PLAN's checkbox sat `[ ]` despite full execution (commits + SUMMARY exist) — the same post-execution doc-hygiene lag.
- **Observability query path untested until the live gate.** The CFG-08 ES clash-log poll used `match: body`, but OTel nests the rendered message under `body.text` (not phrase-searchable) — the test never matched. Caught and fixed at the live N=3 run (`bfa5a65`, switched to `term` on `attributes.{OriginalFormat}`). Gate A's product behavior was always correct; the *test's* assumption about the log shape wasn't.
- **`milestone.complete` arg quirk** — the `gsd-sdk query` wrapper rejected the version arg; had to call `gsd-tools.cjs milestone complete` directly. (And the accomplishments extractor pulled one malformed "Rule 1 - Bug" line into MILESTONES.md — hand-cleaned.)

### Patterns Established
- **Spike-locks-the-rule-table** — when a check models an external contract (here STJ deserialization), drive the highest-risk verdicts through the real serializer in a Wave-0 spike *before* encoding the rule table; the spike facts become permanent regression rows.
- **Conservative-never-false-positive gate** — a startup compatibility gate defaults the unmodeled to "compatible" so it can only ever block a *proven* clash; unwalked constructs are documented INFO gaps, never silent blocks.
- **Two-gate transitive invariant** — compose an existing gate (B: payload⊨schema) with a new one (A: schema⊨type) so the product guarantee (payload deserializes) is provable, with each gate retained unchanged.
- **Faithful-clone-behind-a-profile negative subject** — for a "this is correctly blocked" proof, clone the known-good subject with the single adversarial delta + a distinct identity, gated out of the default stack.

### Key Lessons
1. **Tick `requirements-completed` in the same plan that wires the requirement to GREEN.** CFG-05's "defer marking to avoid over-claiming" was reasonable in 57-02 but created an orphan when 57-03 forgot to pick it up — the audit caught it, but the fix is: the wiring plan owns the tick.
2. **Update VALIDATION task-status (and the ROADMAP checkbox) at phase close, not just the SUMMARY.** Four+ milestones of the same drift; the strategy docs read as PARTIAL/incomplete long after the work is green.
3. **Test the observability assertion path early.** An E2E proof that polls logs/metrics is only as good as its query — exercise the otel field shape (`body.text` vs `body`, `attributes.*`) in a hermetic or early-live pass, not at the expensive N=3 close gate.
4. **A breaking author-contract change rides cleanly on a prerequisite-seam-first build order.** 56 (typed seam) → 57 (Gate A over that type) → 58 (compose + prove) meant each phase had a solid, already-green substrate — the clean-break removal in 56 never had to be revisited.

### Cost Observations
- Model mix: orchestration/execution on Opus (profile `quality`); audit/verifiers on Sonnet.
- Sessions: ~1 day, intensive (2026-06-12 17:10 → 2026-06-13 04:06); 93 files changed (+11,303 / −205).
- Notable: a small, surgical milestone (3 phases) for a breaking contract change — most of the delta is the new `BaseProcessor<TConfig>` layer + `ConfigSchemaCoverageCheck` (441 lines) + the BadConfig subject + tests; the existing pipeline/orchestrator tiers were reused unchanged.

## Milestone: v7.0.0 — Per-Replica Processor Liveness & Self-Watchdog

**Closed:** 2026-06-14 (audit-override)
**Phases:** 3 (59–61) + 1 inserted (62.1) | **Plans:** 12 | **Close gate:** NOT run

### What Was Built
Per-instance L2 liveness keyspace (`skp:proc:{processorId}:{instanceId}` + instance-index SET) replacing the single last-write-wins key; two-state health + per-schema summary written by both startup + heartbeat loops (unhealthy-is-written, frozen-healthy); in-memory L1 liveness record; WebAPI ≥1-healthy-and-fresh orchestration-start gate; self-watchdog liveness probe over L1 staleness; G-62-01 fix decoupling liveness refresh from `IsHealthy`.

### What Worked
- Dependency-driven build order (keyspace → writer → readers) kept each phase hermetically self-contained and 0-warning.
- Phase 62.1 was correctly inserted as a decimal phase the moment the Phase-62 dry run surfaced the IsHealthy-coupling gap (G-62-01) — caught before any live close.

### What Was Inefficient
- The live close gate (Phase 62) was repeatedly deferred and never run; the milestone accumulated authored-but-unproven live artifacts. Closing required an audit-override.
- Phase/ROADMAP drift accumulated (v5/v6 sections left uncollapsed, Phase 62.1 misfiled under v6.0.0, 66 phase dirs unarchived).

### Key Lessons
- **A narrow per-milestone live close gate is fragile when deferred.** v8.0.0 reframes the live proof as a single comprehensive resilience suite (Prometheus + ES, 7 fault scenarios) that subsumes per-milestone close gates — a better fit than repeatedly re-running a milestone-scoped triple-SHA gate.
- **Hermetic green ≠ shipped.** 17/17 functional reqs were hermetically green, but "recovery proven live under faults" remained unproven until a dedicated live milestone.

### Cost Observations
- Model mix: orchestration/execution on Opus (profile `quality`); audit/verifiers on Sonnet.
- Notable: feature-complete & hermetic, but closed without its live capstone — the capstone became the seed for v8.0.0.

## Cross-Milestone Trends

### Process Evolution

| Milestone | Sessions | Phases | Key Change |
|-----------|----------|--------|------------|
| v3.2.0 | 3 days | 11 | Established the reusable BaseApi.Core foundation + 3-GREEN/psql-SHA close gate |
| v3.3.0 | 1 day | 5 | Added Redis L2 tier; extended close gate with `redis-cli --scan` dual-SHA; doc-first contract amendments |
| v3.4.0 | ~3 days | 9 | Two-process messaging (RabbitMQ); triple-SHA close gate (+ `rabbitmqctl`); gap-closure decimal phases |
| v3.6.0 | ~2 days | 4 | Exactly-once-effect dedup; Wave-0 reality probes; build-then-revert (breaker → dead-letter) |
| v3.7.0 | ~3 days | 10 | Keeper recovery console; spike-first de-risk; consolidated authoritative close gate; audit→gap-closure phases (40/41/42) |

### Cumulative Quality

| Milestone | Tests | Coverage | Zero-Dep Additions |
|-----------|-------|----------|-------------------|
| v3.2.0 | 142 facts × 3 GREEN | real Postgres + ES + Prom | — |
| v3.3.0 | 235 facts × 3 GREEN | + real Redis | Redis soft-dependency (no HEALTH contract change) |
| v3.4.0 | 335 facts × 3 GREEN | + real RabbitMQ | MassTransit messaging tier |
| v3.6.0 | 452 facts × 3 GREEN | + induced-redelivery E2E | none (idempotency layer over existing tiers) |
| v3.7.0 | 500 facts × 3 GREEN | + induced-L2-outage recover/give-up E2E | `Keeper` console (multi-replica recovery tier) |

### Top Lessons (Verified Across Milestones)

1. **The 3-consecutive-GREEN + byte-identical SHA close gate catches flakes and resource leaks** — proven across 20+ phases now (psql `\l` in v3.2.0, `redis-cli --scan` in v3.3.0, `rabbitmqctl list_queues` in v3.4.0; the redis SHA caught the Phase-31.1 flag-churn leak).
2. **Bisect-friendly N-commit sequences + doc-first amendments keep spec and code aligned** through surface revisions (Phase 10 in v3.2.0; Phase 16 Stop-contract rewrite in v3.3.0).
3. **Check a design against its real constraints before building it** — infra availability (v3.4.0 delayed-message plugin → 24.1 teardown) and trigger-condition behavior (v3.6.0 breaker → 32.1 revert) both caused build-then-remove cycles a design-time check would have avoided.
4. **`gsd-sdk milestone.complete` is broken in this SDK build** — manual archival every milestone (v3.4.0, v3.6.0).
