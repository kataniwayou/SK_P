# Phase 60: Dual-Loop Writer + In-Memory L1 Liveness Record - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-13
**Phase:** 60-dual-loop-writer-in-memory-l1-liveness-record
**Areas discussed:** Writer topology, Key transition, L1 record home, Intervals & TTL

---

## Area selection

| Option | Selected |
|--------|----------|
| Writer topology | ✓ |
| Key transition | ✓ |
| L1 record home | ✓ |
| Intervals & TTL | ✓ |

**User's choice:** All four.

---

## Writer topology

### Q: Which loop owns the pre-Healthy `unhealthy` write?
| Option | Selected |
|--------|----------|
| Orchestrator inline + heartbeat | ✓ |
| Dedicated 3rd startup loop | |
| Unified single writer | |

**Notes:** Chosen because the startup orchestrator is single-threaded over its own resolution progress and can safely build the `summary` from partial state — a separate concurrent loop would violate the WR-03 memory-visibility invariant on `IProcessorContext`.

### Q: What cadence drives the startup (`unhealthy`) writes?
| Option | Selected |
|--------|----------|
| Ride backoff, interval=startup_interval anchor | ✓ |
| Fixed startup_interval tick (decouple from backoff) | |

**Notes:** No new timer; the unhealthy write rides each backoff-retry iteration. Startup-phase gate/probe precision is low-stakes (gate fails on `status` first; K8s startupProbe covers a starting pod).

### Q: How granular is the `unhealthy` summary during startup?
| Option | Selected |
|--------|----------|
| Per-schema progress (FAIL→SUCCESS as each resolves) | ✓ |
| Coarse all-FAIL until Healthy | |

**Notes:** Richer diagnostic; feeds `ProcessorLivenessEntry.Create` with real per-schema outcomes.

**Derived constraint surfaced:** the per-instance key is keyed by `processorId`, which only exists after Loop A resolves identity — so the first key + index `SADD` land on the first iteration *after* identity resolves (accepted, unavoidable).

---

## Key transition

### Q: In the 60→61 window, what does the writer do with the old `skp:{id}` key?
| Option | Selected |
|--------|----------|
| Hard-swap to per-instance only | ✓ |
| Dual-write old + new | |

**Notes:** Old contract types stay (reader still compiles against them; deleted in 61). Orchestration-start liveness knowingly stale 60→61 — accepted; nothing live depends on it before Phase 62; hermetic suite stays green.

### Q: Does Phase 60 touch the orchestration-start reader (`ProcessorLivenessValidator`)?
| Option | Selected |
|--------|----------|
| No — reader is strictly Phase 61 | ✓ |
| Pull the reader swap forward into 60 | |

**Notes:** GATE-01/02/03 stays in Phase 61 per the locked build order.

---

## L1 record home

### Q: Where does the in-memory L1 liveness record live?
| Option | Selected |
|--------|----------|
| New dedicated singleton holder | ✓ |
| Extend IProcessorContext | |

**Notes:** Mirrors Phase 59 D-01 isolation; the L1 record must be readable DURING startup, unlike `IProcessorContext`'s "read only after Healthy" (WR-03) discipline.

### Q: What type is the in-memory L1 snapshot?
| Option | Selected |
|--------|----------|
| Reuse ProcessorLivenessEntry | ✓ |
| Separate L1-only struct | |

**Notes:** L1-01 fields are exactly `ProcessorLivenessEntry`; one type, L1/L2 can't desync.

### Q: How is the record published across threads?
| Option | Selected |
|--------|----------|
| Volatile immutable-reference swap | ✓ |
| Lock-guarded field | |

**Notes:** Lock-free atomic reference swap of the already-immutable record; mirrors the `IsHealthy` volatile/Interlocked discipline.

---

## Intervals & TTL

### Q: Default cadences for the two loops?
| Option | Selected |
|--------|----------|
| heartbeat=10s, startup-anchor=BackoffCap(30s) | ✓ |
| heartbeat=10s, startup-anchor=fixed small (e.g. 10s) | |

**Notes:** Startup writes ride the backoff (gaps up to BackoffCap=30s); recording the startup interval as BackoffCap keeps `interval×2` staleness + TTL covering the worst-case gap.

### Q: How is the per-instance key TTL sized?
| Option | Selected |
|--------|----------|
| Derive from active interval (×2), Ttl as floor | ✓ |
| Keep fixed Ttl(30s) for both loops | |

**Notes:** TTL = `max(activeInterval×2, Ttl)`; auto-adapts per loop so a live replica's key never lapses (preserves STATE-03 even under slow backoff). `Ttl` knob retained as the floor.

### Q: Config-options shape for the split intervals?
| Option | Selected |
|--------|----------|
| Keep `Interval`=heartbeat, add `StartupInterval` | ✓ |
| Rename to `HeartbeatInterval` + `StartupInterval` | |

**Notes:** Minimal appsettings blast radius; `Interval`(10s) keeps meaning heartbeat, new `StartupInterval` added, `Ttl` retained.

---

## Done

### Q: Ready to write CONTEXT.md, or explore more gray areas?
| Option | Selected |
|--------|----------|
| Create context | ✓ |
| Explore more gray areas | |

## Claude's Discretion

- L1 holder type/member names + DI site.
- `StartupInterval` `ConfigurationKeyName` + baked default (anchored to BackoffCap's 30s).
- Inline write as private helper vs injected shared writer collaborator (shared path encouraged).
- Startup-write Redis-fault resilience (mirror heartbeat log-and-continue).
- TTL rounding/units mechanics.

## Deferred Ideas

- Phase 61: WebAPI ≥1-healthy gate + self-watchdog probe; delete old `Processor(Guid)`/`ProcessorProjection`.
- Phase 62: RealStack proof + triple-SHA close gate.
- Future: K8s probe wiring; observability instanceId-copy repointing (Phase 59 carry).
- Out of scope: mid-life health re-validation (frozen-healthy this milestone).
