# Phase 62: Live Proof & Close Gate - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-13
**Phase:** 62-live-proof-close-gate
**Areas discussed:** Two-replica deployment, Proof harness split, Fault injection, Close-gate v7 deltas

---

## Two-replica deployment

| Option | Description | Selected |
|--------|-------------|----------|
| Reshape processor-sample → replicas:2 | Drop container_name + published port, add deploy.replicas:2 (mirror keeper tier); default stack now runs 2 replicas | ✓ |
| New profile-gated multi-replica tier | Keep processor-sample single; add a separate replicas:2 tier brought up only for the close gate | |
| Runtime --scale | Remove container_name; `docker compose up --scale processor-sample=2` at runtime | |

**User's choice:** Reshape processor-sample → replicas:2

| Option (probe access) | Description | Selected |
|--------|-------------|----------|
| docker exec into the replica | `docker exec <replica> wget localhost:8082/health/live` — per-replica, no published port | ✓ |
| Publish a port range | Publish 8082 as a host range so each replica maps to a distinct host port | |
| Assert probe state via Redis/L1 indirectly | Infer the verdict from redis key state (weak — probe reads in-memory L1, not Redis) | |

**User's choice:** docker exec into the replica

---

## Proof harness split

| Option | Description | Selected |
|--------|-------------|----------|
| Mix: xUnit for deterministic, runbook for lifecycle | xUnit RealStack (host Redis) for gate/keyspace verdicts incl. fabricated sibling keys; operator runbook for real multi-container lifecycle + N=3 close | ✓ |
| All operator runbook | Drive every TEST-01/02 assertion from the runbook; nothing regression-gated in the suite | |
| All automated xUnit RealStack | Push everything into xUnit, simulating lifecycle/timing | |

**User's choice:** Mix: xUnit for deterministic, runbook for lifecycle

| Option (sibling state) | Description | Selected |
|--------|-------------|----------|
| Fabricated Redis key (xUnit) | Craft a status=unhealthy / old-timestamp per-instance key + SADD to index alongside the real healthy replica; assert in-process gate (Phase-61 WR-01/02 style) | ✓ |
| Real broken replica only | Only assert against genuinely-induced replica states (timing-sensitive) | |

**User's choice:** Fabricated Redis key (xUnit)

---

## Fault injection

| Option (restarting → unhealthy) | Description | Selected |
|--------|-------------|----------|
| Durably-broken extra replica | A profile-gated instance that reaches the startup loop (writes unhealthy) but never flips healthy → durably unhealthy + present in index | ✓ |
| docker restart + tight-poll | Restart a healthy replica and catch the transient startup-unhealthy window before it heals | |
| Fabricated unhealthy key only | Don't induce live; only fabricate in xUnit | |

**User's choice:** Durably-broken extra replica

| Option (stale-L1 probe) | Description | Selected |
|--------|-------------|----------|
| Hermetic verdict + live-wiring proof | Prove stale→Unhealthy verdict hermetically (clock); prove probe wired live via docker exec curl; no prod fault code | ✓ |
| Minimal fault-injection seam | Add a test-only FreezeLoopAfter env on a fault instance for a fully-live stale trip | |
| docker pause attempt | Rejected — pause freezes the whole process, probe unreachable | |

**User's choice:** Hermetic verdict + live-wiring proof

| Option (dead replica) | Description | Selected |
|--------|-------------|----------|
| docker stop + wait TTL + gate read | Stop a replica, wait > ~30s TTL, trigger a validator read → lazy SREM; assert SMEMBERS shrinks | ✓ |
| docker kill | SIGKILL variant — functionally equivalent | |

**User's choice:** docker stop + wait TTL + gate read

---

## Close-gate v7 deltas

| Option (SHA exclusion) | Description | Selected |
|--------|-------------|----------|
| Prefix-pattern exclude skp:proc:* | Exclude the whole per-replica liveness namespace (index SET + per-instance keys) by prefix — instanceIds non-deterministic so pattern required | ✓ |
| Include + rely on intra-run stability | Leave keys in the SHA, rely on stack staying up across the run | |

**User's choice:** Prefix-pattern exclude skp:proc:*

| Option (regression) | Description | Selected |
|--------|-------------|----------|
| Full accumulated suite + badconfig | Retag new v7 TEST-01/02 + SC1/2/3 + Gate-A CFG-08/09 + Sample; bring up badconfig profile | ✓ |
| v7-focused only | Retag only new v7 tests + Sample; run other suites separately | |

**User's choice:** Full accumulated suite + badconfig

| Option (fault isolation) | Description | Selected |
|--------|-------------|----------|
| No — separate runbook steps only | Close-gate SHA runs against clean steady state (2 healthy replicas + optional badconfig); fault steps OUTSIDE the window | ✓ |
| Include the broken replica in steady state | Keep the durably-unhealthy replica up during the close gate | |

**User's choice:** No — separate runbook steps only

## Claude's Discretion

- Exact never-healthy mechanism for the durably-broken replica (missing-definition vs Gate-A reuse).
- Crafted-key shapes/timestamps for fabricated siblings; xUnit collection/parallelization shaping.
- docker stop vs kill; the fault-instance compose profile name; probe `data`-dict key names.

## Deferred Ideas

- K8s liveness/startupProbe wiring + pod-restart on probe failure (milestone Future).
- A minimal in-process fault-injection seam for a fully-live stale-L1 probe trip (rejected this phase).
