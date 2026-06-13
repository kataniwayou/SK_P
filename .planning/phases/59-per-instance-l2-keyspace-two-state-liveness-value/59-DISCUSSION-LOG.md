# Phase 59: Per-Instance L2 Keyspace & Two-State Liveness Value - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-13
**Phase:** 59-per-instance-l2-keyspace-two-state-liveness-value
**Areas discussed:** Value type strategy, summary + status modeling, Reshape posture, instanceId resolution

---

## Area selection

All four surfaced gray areas were selected for discussion:
Value type strategy · summary + status modeling · Key builder migration (→ reframed as Reshape posture) · instanceId resolution reuse.

---

## Value type identity

| Option | Description | Selected |
|--------|-------------|----------|
| New isolated record | Processor-liveness-only record (`ProcessorLivenessEntry { timestamp, interval, status, summary }`); leaves shared `LivenessProjection`/`ProcessorProjection` untouched. | ✓ |
| Reshape ProcessorProjection in place | Drop definitions, add summary beside the nested (shared) `LivenessProjection`; add `LivenessStatus.Unhealthy`. Smallest diff but keeps the shared sub-doc shared. | |

**User's choice:** New isolated record.
**Notes:** `LivenessProjection` is shared with the out-of-scope workflow-root liveness path (`WorkflowLifecycle`, `WorkflowFireJob`, `RedisProjectionWriter`). Isolation prevents the reshape from rippling into out-of-scope code; the new name signals the breaking change. Accepts timestamp/interval/status duplication.

---

## summary + status modeling

| Option | Description | Selected |
|--------|-------------|----------|
| String-const SoT + factory now | Add `LivenessStatus.Unhealthy` + `SchemaOutcome` (Success/Fail) consts; `summary` nested record; static factory derives `status` from `summary` (any FAIL⇒Unhealthy, null⇒Success). | ✓ |
| String-const SoT, dumb DTO | Same string shapes, no factory; Phase-60 writer computes status. | |
| C# enums | Enums + `JsonStringEnumConverter`; departs from the const pattern. | |

**User's choice:** String-const SoT + factory now.
**Notes:** Matches the established `LivenessStatus`/`L2ProjectionKeys`/`OrchestratorQueues` single-source-of-truth discipline; JSON-stable; no STJ converter wiring. The factory makes the STATE-01/02 invariant (any FAIL ⇒ unhealthy; null-is-skip) un-violable by the Phase-60 writer. `configSchema` is the v6.0.0 Gate A outcome, never recomputed.

---

## Reshape posture

| Option | Description | Selected |
|--------|-------------|----------|
| Additive surface, swap+delete in 60/61 | Add new builders + value type + tests additively; heartbeat writer + validator stay on `skp:{id}`; old builder/value deleted in 60/61. Solution builds green by addition. | ✓ |
| Coupled clean break now | Delete old + minimally retarget the 4 call sites now (compile-forced, à la Phase 43). | |

**User's choice:** Additive surface, swap+delete in 60/61.
**Notes:** The writer is Phase 60 and the reader is Phase 61, so a clean-break-now would pull their touches into 59. Additive keeps phase boundaries clean and still satisfies SC-5 (builds 0-warning, hermetic suite green). Mirrors the shipped Phase-50 (50-01 additive → later teardown) decomposition. "Replacing the single key" is satisfied at the builder/contract level; physical deletion deferred to the consumer swap.

---

## instanceId resolution

| Option | Description | Selected |
|--------|-------------|----------|
| Extract one shared resolver in 59 | Hoist the duplicated `POD_NAME→HOSTNAME→MachineName→GUID` chain into one SoT resolver; Phase-60 writer injects it; builder takes `string instanceId`. | ✓ |
| Builder signature only; defer resolution to 60 | Define the builder string param + pin the chain; extract/wire in Phase 60. | |

**User's choice:** Extract one shared resolver in 59.
**Notes:** Satisfies KEY-03 "reused, no new mechanism" at the contract/foundation layer and removes the existing duplication (BaseApi + BaseConsole copies). `GUID` fallback keeps `ToString("N")` rendering. Open for planner: the cycle-free home for the resolver and whether the two existing copies are repointed now or in a later sweep.

---

## Claude's Discretion

- Exact type/file names (`ProcessorLivenessEntry`, `SchemaOutcome`, resolver type).
- Golden-test pinning mechanics and test home (mirror existing `L2ProjectionKeys` golden tests).
- `[property: JsonPropertyName]` placement (load-bearing — must target properties on the positional record).

## Deferred Ideas

- Delete `Processor(Guid)` + `ProcessorProjection` → Phase 60/61.
- Dual-loop writer / unhealthy-is-written / SADD / TTL / split intervals / L1 record → Phase 60.
- ≥1-healthy gate / 422 + RFC 7807 / lazy-SREM / self-watchdog probe → Phase 61.
- K8s probe wiring; mid-life TOCTOU re-validation → future / out of scope.
