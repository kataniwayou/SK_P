# Phase 22: L2 Root-Parent Restructure + Processor Self-Registration Boundary — Specification

**Created:** 2026-05-31
**Ambiguity score:** 0.16 (gate: ≤ 0.20)
**Requirements:** 5 locked (L2IDX-02 dropped 2026-05-31 during discuss-phase — see Amendments)

## Goal

The L2 Redis projection gains a parent index — a single Redis SET enumerating all active workflow IDs — the key prefix becomes a hardcoded compile-time constant, and the orchestration Start flow stops *creating* processor L2 entries and instead *validates* each participating processor's self-registered entry for existence and timestamp-based liveness, failing with the same 422 contract as other Start-time validations. The per-workflow root keeps its existing `entryStepIds` shape and member-step enumeration stays traversal-based (`entryStepIds → nextStepIds` BFS); the separate per-workflow member SET originally specced as L2IDX-02 was dropped — see Amendments.

## Background

Grounded in the codebase as of Phase 21 (v3.4.0):

- **L2 is three flat keyspaces, no top-level index.** `L2ProjectionKeys` (in `Messaging.Contracts/Projections`) builds: `Root(prefix, wfId) → {prefix}{wfId:D}` (holds `WorkflowRootProjection`: EntryStepIds, Cron, JobId, Liveness, CorrelationId), `Step(prefix, wfId, stepId) → {prefix}{wfId}:{stepId}` (holds `StepProjection`), and `Processor(prefix, procId) → {prefix}{procId}` (holds `ProcessorProjection`). Writer (`RedisProjectionKeys`) and reader (`OrchestratorL2Keys`) both forward to `L2ProjectionKeys` (HARDEN-03 single source of truth). There is **no key that enumerates which workflows exist**, and **no per-workflow list of member step keys** — `RedisL2Cleanup` finds a workflow's step keys by BFS-traversing `EntryStepIds → NextStepIds`. All values are `StringSet` JSON; no Redis SETs are used today.
- **The prefix is configurable.** `RedisProjectionOptions.KeyPrefix` (default `"skp:"`, bound from `Redis:KeyPrefix`) on the writer; `OrchestratorRedisOptions.KeyPrefix` (also `Redis:KeyPrefix`, default `"skp:"`) on the reader. `RedisProjectionOptions` also carries `ProcessorKeyTtlDays` (default 100) and serialization options.
- **The writer creates processor L2 entries.** `RedisProjectionWriter` (BaseApi.Service Start flow) writes a `ProcessorProjection` per participating processor with a TTL (`RedisProjectionWriter.cs` ~lines 100–113). `LivenessProjection { timestamp, interval, status }` exists but is hardcoded `(now, 0, "Pending")` — `interval=0` makes any `timestamp + interval*2 > now` check evaluate as *dead*.
- **Edge-schema validation is L1-only.** `SchemaEdgeValidator` checks `parent.OutputSchemaId == child.InputSchemaId` over the in-memory snapshot during Start; it never reads L2.
- **Processors are external, long-lived apps** (no Processor console exists in v3.4.0 — locked out of scope per REQUIREMENTS.md). They are intended to self-register their own `{prefix}{procId}` key and refresh `timestamp` + `interval`. The Start flow must verify their health before orchestrating.

This phase covers the **writer / contracts side** (mods 1–3). The orchestrator-side reload/stop lifecycle (mods 4–6) is Phase 23.

## Requirements

1. **L2IDX-01 — Workflow parent index**: A single parent index, stored as a Redis SET, enumerates all currently-active workflow IDs.
   - Current: No top-level key lists which workflows exist; enumeration is impossible without scanning the keyspace.
   - Target: On `StartOrchestration`, the writer `SADD`s each workflow's ID into the parent index SET (literal key form per design: `{prefix}`). The set is the authoritative "which workflows are active" index.
   - Acceptance: After Starting N workflows, `SMEMBERS` of the parent index returns exactly those N workflow IDs (D-format GUIDs). *(Removal-on-Stop is Phase 23.)*

2. **L2PREFIX-01 — Hardcoded prefix constant**: The L2 key prefix is a compile-time constant, not configuration.
   - Current: Prefix is read from `Redis:KeyPrefix` via `RedisProjectionOptions` (writer) and `OrchestratorRedisOptions` (reader); default `"skp:"`.
   - Target: The prefix is a `const` on the shared `L2ProjectionKeys` (or equivalent shared location), consumed by both writer and reader; the config-driven `KeyPrefix` read path is removed on both sides, and the `Redis:KeyPrefix` key is removed from both `appsettings.json` files. `RedisProjectionOptions` retains `ProcessorKeyTtlDays` + serialization.
   - Acceptance: A grep of `src/` shows zero reads of a configurable key-prefix (no `KeyPrefix` property feeding key construction); both `appsettings.json` files no longer contain `Redis:KeyPrefix`; all L2 keys still resolve through the single shared `L2ProjectionKeys`.

3. **PROC-NOCREATE-01 — Writer stops creating processor L2 entries**: The Start flow no longer writes `ProcessorProjection` keys to L2.
   - Current: `RedisProjectionWriter` writes one processor key per participating processor (with TTL).
   - Target: That write path is removed; processor L2 entries are owned exclusively by the (external) processor apps that self-register them.
   - Acceptance: After a Start with M participating processors, the writer has created zero `{prefix}{procId}` keys (verified by reading the processor keyspace post-Start).

4. **PROC-LIVE-01 — Processor existence + liveness validation at Start**: The Start flow validates that every participating processor has a live self-registered L2 entry, failing identically to other Start-time validations.
   - Current: No processor existence/liveness check occurs at Start; the writer simply creates the entries.
   - Target: During Start (writer side, after L1 is built from L3), for each participating processor the flow reads its `{prefix}{procId}` L2 entry and requires it to (a) exist and (b) be alive by `timestamp + interval*2 > now`. Absence or staleness throws `OrchestrationValidationException` → HTTP **422**, the same error shape/status code as `SchemaEdgeValidator` and other Start validations. `interval` is sourced from the processor's self-registered entry (not the hardcoded `0`).
   - Acceptance: With L2 seeded directly (simulating self-registration): Start returns **204** when all participating processors exist and `timestamp + interval*2 > now`; Start returns **422** when any participating processor's entry is absent; Start returns **422** when any participating processor's entry is stale (`timestamp + interval*2 ≤ now`).

5. **PROC-EDGE-01 — Edge-schema validation preserved**: Processor edge-schema validation behavior is unchanged.
   - Current: `SchemaEdgeValidator` enforces `parent.OutputSchemaId == child.InputSchemaId` (null on either side passes) over the L1 snapshot, throwing on mismatch.
   - Target: Identical behavior; not coupled to the L2 processor-entry changes.
   - Acceptance: Existing `SchemaEdgeValidator` tests remain green with no behavioral change.

## Boundaries

**In scope:**
- Parent-index Redis SET of active workflow IDs, populated by the writer on Start (L2IDX-01).
- Hardcoded prefix constant on shared `L2ProjectionKeys`; removal of config-driven prefix on both sides + both `appsettings.json` (L2PREFIX-01).
- Removal of the writer's processor-entry creation (PROC-NOCREATE-01).
- Processor existence + timestamp-liveness validation in the Start flow, returning 422 on failure (PROC-LIVE-01).
- Updating the locked golden/round-trip/writer/cleanup tests to the new shapes; keeping the triple-SHA close gate green.

**Out of scope:**
- **Processor-side self-registration write path** (the processor app writing/refreshing its own `{prefix}{procId}` key, timestamp, interval) — no Processor console exists in v3.4.0; tests simulate it by seeding L2 directly.
- **Orchestrator startup reload, start-orchestration reload, stop unlink + cascade-delete, publishing jobIds instead of workflowIds** — all Phase 23 (mods 4–6).
- **Removing a workflow from the parent index on Stop** — Phase 23 (this phase only `SADD`s on Start; `SREM` on Stop is Phase 23).
- **Scheduler/Quartz integration and the real source of `interval`/`Cron` job triggering** — deferred beyond v3.4.0.
- **Any change to `SchemaEdgeValidator` logic** — explicitly preserved, not modified.

## Constraints

- **Triple-SHA close gate must stay at exit 0** (full suite GREEN ×3; `psql \l` + `redis-cli --scan` + `rabbitmqctl list_queues` BEFORE==AFTER). The key reshape updates `L2ProjectionKeysTests` (golden byte strings), `ProjectionRecordRoundTripTests`, `RedisProjectionWriterFacts`, and `StopCleanupFacts` — these are expected, in-scope test updates.
- **HARDEN-03 single-source-of-truth must be preserved**: all L2 key construction (including the new index keys and the hardcoded prefix) flows through the shared `L2ProjectionKeys`; no hand-copied interpolation literals outside it.
- **Introduces the Redis SET type** to a keyspace that is `StringSet`-only today; SET membership ops (`SADD`/`SREM`) are the chosen primitives for the **parent index only** — the sole Redis SET this phase adds. Per-workflow member enumeration stays traversal-based (`entryStepIds → nextStepIds` BFS); no member SET (L2IDX-02 dropped).
- **Partially lifts the v3.4.0 "no Orchestrator Redis writes / new keyspaces" lock** — but only for the **writer-side** parent-index `SADD`/`SREM`; the orchestrator process still performs no L2 writes in this phase (orchestrator writes are Phase 23).
- 422 failures must use the existing `OrchestrationValidationException` path so the HTTP contract is identical to current validation behavior.

## Acceptance Criteria

- [ ] After Starting N workflows, `SMEMBERS` of the parent index returns exactly those N workflow IDs.
- [ ] `{prefix}` is a compile-time `const`; `grep src/` shows no configurable key-prefix read; neither `appsettings.json` contains `Redis:KeyPrefix`; all keys resolve via shared `L2ProjectionKeys`.
- [ ] After a Start with M processors, the writer created zero `{prefix}{procId}` keys.
- [ ] Start returns 204 when all participating processors exist and `timestamp + interval*2 > now`.
- [ ] Start returns 422 (`OrchestrationValidationException`) when a participating processor's L2 entry is absent.
- [ ] Start returns 422 when a participating processor's L2 entry is stale (`timestamp + interval*2 ≤ now`).
- [ ] `SchemaEdgeValidator` tests remain green (edge validation behavior unchanged).
- [ ] Triple-SHA close gate exits 0 (full suite GREEN ×3).

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                        |
|--------------------|-------|------|--------|--------------------------------------------------------------|
| Goal Clarity       | 0.88  | 0.75 | ✓      | Parent-index SET + const prefix + validate-not-create (L2IDX-02 member SET dropped — see Amendments) |
| Boundary Clarity   | 0.86  | 0.70 | ✓      | Writer-side only; orchestrator reload/stop + self-reg deferred |
| Constraint Clarity | 0.74  | 0.65 | ✓      | Triple-SHA gate, HARDEN-03 SoT, new SET type, lock partially lifted |
| Acceptance Criteria| 0.84  | 0.70 | ✓      | 9 pass/fail checks; 204/422 paths seeded                     |
| **Ambiguity**      | 0.16  | ≤0.20| ✓      |                                                              |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective       | Question summary                                    | Decision locked                                                                 |
|-------|-------------------|-----------------------------------------------------|---------------------------------------------------------------------------------|
| 1     | Researcher        | What L2 structure / prefix / processor-creation exists today? | Existing `Root` is the per-workflow key (not a parent); no index; prefix is config; writer creates processor entries; no L2→L1 reload anywhere |
| 1     | Boundary Keeper   | Key-reshape blast radius — add-only (a) vs membership index (b)? | **(b)**: two-level membership index with native **Redis SETs** (parent lists workflows; each workflow lists member step IDs) — enables mods 4/6 enumeration without graph traversal |
| 2     | Researcher        | Where does the processor check live; what does it own? | Processors are external long-lived apps that self-register `{procId}` + refresh timestamp/interval; Start (writer side) validates, does not create |
| 2     | Failure Analyst   | What happens when a processor is absent/stale at Start? | Same as other validations: `OrchestrationValidationException` → **422**; liveness `timestamp + interval*2 > now` **enforced this phase**; self-registration write path out of scope (tests seed L2) |

## Amendments

**2026-05-31 (discuss-phase) — L2IDX-02 dropped.** The original spec required a per-workflow member step-id **Redis SET** enabling `SMEMBERS` enumeration with no graph traversal. During `/gsd-discuss-phase`, the project owner finalized the L2 structure as **today's root/step shape + exactly one new key (the parent index `skp:`)**:

- `skp:` → Redis SET of active workflow IDs (L2IDX-01, unchanged).
- `skp:{wf}` → JSON `{ entryStepIds[], cron, jobId, liveness, correlationId }` — **entry steps only** (unchanged from v3.4.0).
- `skp:{wf}:{stepId}` → JSON `{ entryCondition, processorId, payload, nextStepIds[] }` (unchanged).
- **No `skp:{wf}:members` key.**

Member-step enumeration (and cleanup's step discovery) stays **traversal-based** (`entryStepIds → nextStepIds` BFS GET-and-follow), exactly as `RedisL2Cleanup` does today. The parent index is therefore the **only** Redis SET introduced. Owner rationale: the separate member SET was over-specification; the parent index is the valuable new structure, and traversal already serves enumeration. Phase 23's reload/stop enumeration will likewise traverse rather than `SMEMBERS`.

All implementation decisions are captured in `22-CONTEXT.md`.

---

*Phase: 22-l2-root-parent-restructure-processor-self-registration*
*Spec created: 2026-05-31*
*Spec amended: 2026-05-31 (L2IDX-02 dropped — discuss-phase)*
*Next step: /gsd-discuss-phase 22 — implementation decisions (exact index key strings, SET vs embedded member list, writer/cleanup wiring, validator placement)*
