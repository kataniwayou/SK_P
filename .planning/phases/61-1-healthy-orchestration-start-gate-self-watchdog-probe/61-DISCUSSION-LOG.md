# Phase 61: ≥1-Healthy Orchestration-Start Gate + Self-Watchdog Probe - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-13
**Phase:** 61-1-healthy-orchestration-start-gate-self-watchdog-probe
**Areas discussed:** Probe endpoint, Lazy SREM, Gate 422 reporting, Probe boot verdict, Probe wiring mechanism

---

## Probe endpoint

| Option | Description | Selected |
|--------|-------------|----------|
| Augment /health/live (processor-only) | Watchdog joins /health/live for processors only; it's the K8s liveness probe → future restart trigger. Orchestrator/Keeper stay self-only. | ✓ |
| New dedicated endpoint | Add /health/watchdog separate from /health/live; keeps self-only contract pristine. | |
| Discuss the wiring mechanism | Defer endpoint, talk through the cross-container wiring first. | |

**User's choice:** Augment /health/live (processor-only)
**Notes:** Self-watchdog is in-process loop liveness (not a dependency blip), so /health/live — the K8s restart trigger — is the right surface. Scoped to processors; shared consoles unchanged.

---

## Lazy SREM

| Option | Description | Selected |
|--------|-------------|----------|
| Absent-only, fire-and-forget | SREM only TTL-expired (GET→null) members; present-but-stale/unhealthy kept; SREM never affects verdict or 500s. | ✓ |
| Absent + stale, fire-and-forget | Also prune present-but-stale keys (more aggressive). | |
| Awaited best-effort | Same absent-only prune but awaited before verdict (deterministic for close gate). | |

**User's choice:** Absent-only, fire-and-forget
**Notes:** Index is a discovery hint; prune only genuinely-expired members. SREM is opportunistic hygiene, never a correctness gate.

---

## Gate 422 reporting + malformed-replica handling

| Option | Description | Selected |
|--------|-------------|----------|
| Aggregate reason; malformed = fail-that-replica | One 422 per processor with a replica-count breakdown; malformed JSON fails that replica (counted), never 500. | ✓ |
| Single reason (first-fail style) | Keep the single short reason word; minimal change, less diagnostic. | |
| Malformed value = 500 (terminal) | Present-but-malformed JSON → 500 data-integrity fault. | |

**User's choice:** Aggregate reason; malformed = fail-that-replica
**Notes:** Preserves the shipped WR-01 reasoning (external self-registered data must not 500) while extending the single reason to a multi-replica aggregate.

---

## Probe boot verdict (Current == null)

| Option | Description | Selected |
|--------|-------------|----------|
| Null → Unhealthy | No L1 record = loops not proven alive → Unhealthy; catches a crashed-before-first-write loop (K8s startupProbe covers boot, deferred). | ✓ |
| Null → Healthy (boot grace) | Treat null as still-booting → Healthy until first write; masks a loop that crashed before writing. | |

**User's choice:** Null → Unhealthy
**Notes:** Safe liveness default; boot coverage is the future K8s startupProbe's job (out of scope).

---

## Probe wiring mechanism

| Option | Description | Selected |
|--------|-------------|----------|
| Generic pluggable-check hook in BaseConsole.Core | Add a generic registered-descriptor seam the embedded listener enumerates + bridges the outer provider into; BaseProcessor registers the watchdog tagged 'live'. Reusable; one focused change. | ✓ |
| Processor-local mini health endpoint | BaseProcessor stands up its own tiny health endpoint; two listeners per processor. | |
| Let researcher/planner choose | Lock intent, defer the bridge mechanism to research. | |

**User's choice:** Generic pluggable-check hook in BaseConsole.Core
**Notes:** Forced by dependency direction (BaseConsole.Core is a leaf BaseProcessor.Core depends on, not the reverse). Mirrors the existing BusReadyHealthCheck outer-provider bridge, generalized into a reusable seam.

## Claude's Discretion

- Exact type/file names (LivenessWatchdogHealthCheck, the BaseConsole.Core hook seam shape).
- The probe's `data`-dictionary key names carrying the summary.
- Exact aggregate 422 reason string format / structured-vs-string breakdown.
- SREM batching/pipelining; sequential-vs-pipelined per-instance GETs.

## Deferred Ideas

- Phase 62: RealStack proof + triple-SHA close gate.
- Future: K8s livenessProbe/startupProbe wiring + pod-restart policy.
- Out of scope: mid-life health re-validation (frozen-healthy this milestone).
- Optional sweep (from Phase 59 D-04): repoint observability instanceId copies to InstanceId.Resolve().
</content>
