# Steps API — v7.0.0 Requirements

> **Milestone:** v7.0.0 — Per-Replica Processor Liveness & Self-Watchdog
> **Source of truth:** This milestone's planning conversation (2026-06-13), design confirmed point-by-point.
> **Posture:** **Breaking change** to the processor liveness contract. The single last-write-wins L2 liveness key `skp:{processorId}` is replaced by per-instance keys under a per-processor instance-index SET; the liveness value gains a two-state `status` + per-schema `summary` and is written by BOTH the startup and heartbeat loops; an in-memory L1 liveness record is added; the WebAPI orchestration-start gate becomes a ≥1-healthy-and-fresh check across replicas; and a self-watchdog liveness probe detects a silently-crashed loop. Builds directly on v6.0.0 Gate A (its result becomes the `configSchema` summary field). Phases continue at **59**.

## Goal

Make processor liveness multi-replica-accurate and self-healing: L2 reflects every replica's true current state (including a restarting one, visible as `unhealthy` — never absent); orchestration admits a workflow iff **at least one** replica of each required processor is healthy and fresh; and a silently-crashed startup/heartbeat loop makes its own pod restartable via a staleness-based liveness probe.

## Requirements

### L2 Keyspace Reshape (KEY)
- [ ] **KEY-01**: Processor liveness is stored at a per-instance key `skp:proc:{processorId}:{instanceId}`, replacing the single last-write-wins key `skp:{processorId}` (one key per replica, no cross-replica overwrite).
- [ ] **KEY-02**: A per-processor instance-index Redis SET `skp:proc:{processorId}` lists the live instanceIds; each replica `SADD`s its own instanceId on its first liveness write (mirrors the Phase-22 workflow parent-index discipline).
- [ ] **KEY-03**: `instanceId` is the pod identity, resolved via the existing `POD_NAME → HOSTNAME → MachineName → GUID` resolution (reused, no new mechanism).
- [ ] **KEY-04**: The per-instance value is liveness-only — `inputDefinition`/`outputDefinition` are dropped from L2 (no consumer reads them from L2; the processor validates against its own in-memory L1 copy).

### Liveness State Model (STATE)
- [ ] **STATE-01**: The liveness `status` is two-valued (`healthy` / `unhealthy`).
- [ ] **STATE-02**: Each liveness entry carries a per-schema `summary` `{ inputSchema, outputSchema, configSchema ∈ SUCCESS | FAIL }`; any `FAIL` ⇒ `unhealthy`. `configSchema` reuses the v6.0.0 Gate A startup config-compat outcome; a null schema id is treated as not-failing (null-is-skip).
- [ ] **STATE-03**: A starting or failed replica **writes** its `unhealthy` entry — L2 reflects a restarting replica as `unhealthy`, never absent (removes the current "only a Healthy replica writes" rule).

### Dual-Loop Writer (LOOP)
- [ ] **LOOP-01**: The startup loop writes the replica's liveness entry (to both L2 and L1) on every iteration, with `status`/`summary` reflecting current schema-resolution progress (`unhealthy` until identity + all non-null schemas resolve).
- [ ] **LOOP-02**: On startup success the processor starts the heartbeat loop; each heartbeat iteration refreshes the entry's timestamp (to both L2 and L1). Health is frozen `healthy` once the heartbeat loop starts — monotonic within a process, reset on restart (no mid-life re-validation).
- [ ] **LOOP-03**: Liveness intervals are split into `startup_interval` (startup-loop cadence) and `heartbeat_interval` (heartbeat cadence); each entry records its active interval so downstream staleness math adapts. The existing `Ttl` knob is retained.
- [ ] **LOOP-04**: Each per-instance key is written with a TTL; a dead replica's key TTL-expires. The per-instance key's TTL is the source of truth for liveness — the index SET is only a discovery hint.

### In-Memory L1 Liveness (L1)
- [ ] **L1-01**: The processor maintains an in-memory L1 liveness record (`timestamp`, active `interval`, `status`, `summary`), updated by BOTH loops on every iteration — the source the self-watchdog probe reads.

### Orchestration-Start Gate (GATE)
- [ ] **GATE-01**: The WebAPI orchestration-start validator discovers a processor's replicas by `SMEMBERS skp:proc:{processorId}` and reads each per-instance key (no prior knowledge of instanceIds required).
- [ ] **GATE-02**: A processor passes the gate iff **≥1** replica is present AND `status=healthy` AND non-stale (`timestamp + interval×2 > now`); a present-but-`unhealthy` or stale replica fails *that* replica (presence no longer implies live).
- [ ] **GATE-03**: When no replica satisfies the gate, orchestration start is blocked with **422 + RFC 7807**; an absent/TTL-expired index member is skipped and lazily `SREM`'d from the index (self-healing).

### Self-Watchdog Probe (PROBE)
- [ ] **PROBE-01**: The processor's liveness probe reads the in-memory L1 record and reports `unhealthy` when the L1 timestamp is stale beyond the active-interval ×2 grace — detecting a silently-crashed startup or heartbeat loop while the host process stays up.
- [ ] **PROBE-02**: The probe returns the per-schema `summary` in its response body. (K8s liveness-probe wiring + restart policy is future; this milestone delivers the probe semantics so the restart trigger exists.)

## Future Requirements (deferred)

- **K8s liveness-probe wiring** — pointing the actual Kubernetes liveness probe at the watchdog endpoint with a restart policy (semantics built this milestone; deployment wiring deferred).
- **Operator liveness diagnostics** — surfacing which replica is unhealthy and why beyond the summary in the probe body (dashboards/alerts).
- **Mid-life health re-validation (TOCTOU)** — re-running schema/config checks during the heartbeat loop so a replica can transition `healthy → unhealthy` within a process (frozen-healthy this milestone).

## Out of Scope

- **Workflow-root liveness** — the `WorkflowFireJob` / `WorkflowLifecycle` `.Liveness` projection is a different (workflow-root) concern and is unchanged; this milestone is processor-replica liveness only.
- **Multi-replica orchestrator/keeper** — the orchestrator stays single-replica; this milestone is about *processor* replicas.
- **Per-field Redis TTL (`HEXPIRE` / Redis 7.4 hash-field expiry)** — explicitly not used; per-instance keys + whole-key TTL is the chosen cleanup mechanism.
- **Gate A / Gate B compatibility logic** — the v6.0.0 config-compat checks are unchanged; this milestone only surfaces their result into the `summary` and does not alter how compatibility is computed.

## Traceability

REQ-IDs are filled into phases by the roadmapper (Step 10). Every requirement maps to exactly one phase; the roadmapper validates 100% coverage. Phases continue at **59**.

| Requirement | Phase | Status |
|-------------|-------|--------|
| KEY-01 | TBD | Pending |
| KEY-02 | TBD | Pending |
| KEY-03 | TBD | Pending |
| KEY-04 | TBD | Pending |
| STATE-01 | TBD | Pending |
| STATE-02 | TBD | Pending |
| STATE-03 | TBD | Pending |
| LOOP-01 | TBD | Pending |
| LOOP-02 | TBD | Pending |
| LOOP-03 | TBD | Pending |
| LOOP-04 | TBD | Pending |
| L1-01 | TBD | Pending |
| GATE-01 | TBD | Pending |
| GATE-02 | TBD | Pending |
| GATE-03 | TBD | Pending |
| PROBE-01 | TBD | Pending |
| PROBE-02 | TBD | Pending |

**Coverage:** 17 requirements across 6 categories (KEY, STATE, LOOP, L1, GATE, PROBE) — to be mapped by the roadmapper.
