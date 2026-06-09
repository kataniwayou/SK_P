# Phase 49: Live Proof & Close Gate - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-09
**Phase:** 49-live-proof-close-gate
**Areas discussed:** L2 outage simulation, Live-run definition-of-done, E2E structure & recovery driving, Close-gate params

---

## L2 outage simulation (SC3)

### Q1: How to induce + heal the transient L2 outage

| Option | Description | Selected |
|--------|-------------|----------|
| docker stop/start sk-redis | True down→up; probe throws RedisException → gate closes → PauseAll; start → gate opens → ResumeAll | ✓ |
| docker pause/unpause sk-redis | Freezes container; timeouts not clean RedisExceptions; murky timing | |
| redis CLIENT KILL / config | In-redis, no docker control; doesn't reproduce a true tier-down | |

**User's choice:** docker stop/start sk-redis
**Notes:** Truest representation of the recovery model's core assumption (recovery from a transient L2 outage).

### Q2: How to isolate the outage test from the rest of the RealStack suite

| Option | Description | Selected |
|--------|-------------|----------|
| Non-parallel collection + heal-wait | Own non-parallel xUnit `[Collection]`; blocks on redis-healthy + steady-state re-established before returning | ✓ |
| Separate gate run/segment | Run outage test outside the main 3× suite run | |
| You decide at plan time | Defer mechanism; lock only serialized + restore-steady-state | |

**User's choice:** Non-parallel collection + heal-wait
**Notes:** Keeps it in the one suite the close gate runs, just serialized; restores liveness key + gate re-open before returning.

---

## Live-run definition-of-done

### Q1: What marks Phase 49 (and v4.0.0) complete

| Option | Description | Selected |
|--------|-------------|----------|
| Authored + hermetic-green, live operator-gated | E2E + close script authored, 0-warning build, non-RealStack green, runbook committed; live N×GREEN operator-gated; TEST-01/02/03 unticked until operator GREEN | ✓ |
| Requires actual live GREEN run | Phase not complete until a real N×GREEN + triple-SHA net-zero live run | |

**User's choice:** Authored + hermetic-green, live operator-gated
**Notes:** Matches every prior milestone close; v4 breaking contract requires the four containers rebuilt before a valid live run. Tracked in 49-HUMAN-UAT.md.

---

## E2E structure & recovery driving (SC1/SC2)

### Q1: E2E file decomposition

| Option | Description | Selected |
|--------|-------------|----------|
| Separate sibling files per concern | round-trip (SC1) / recovery-paths (SC2) / pause-resume-outage (SC3) | ✓ |
| One consolidated v4 close E2E | Single file for SC1–SC3 | |
| Round-trip+recovery together, outage separate | Two files | |

**User's choice:** Separate sibling files per concern
**Notes:** Mirrors one-concern-per-file pattern; each registers its minted keys into teardown.

### Q2: How to exercise each Keeper recovery state

| Option | Description | Selected |
|--------|-------------|----------|
| Publish state messages to recovery consumer | Publish UPDATE/REINJECT/INJECT/DELETE directly to gate-open queue:keeper-recovery; assert each effect | ✓ |
| Drive through the real pipeline | Trigger states organically via the processor pipeline | |
| Hybrid | Pipeline for round-trip, direct for states | |

**User's choice:** Publish state messages to recovery consumer
**Notes:** Deterministic per-state coverage incl. data-gone REINJECT → skp-dlq-1; SC1 still drives the organic round trip.

---

## Close-gate params (N + composite net-zero)

### Q1: N consecutive GREEN

| Option | Description | Selected |
|--------|-------------|----------|
| 3 (carry forward) | Match every prior milestone close gate; identical fact count across runs | ✓ |
| 5 | Stronger flakiness margin; longer runtime; diverges from precedent | |
| You decide at plan time | Lock only N-consecutive-GREEN | |

**User's choice:** 3 (carry forward)

### Q2: How the gate proves the 2-day-TTL composite backup key is net-zero

| Option | Description | Selected |
|--------|-------------|----------|
| Unfiltered --scan SHA + E2E active-clean | Unfiltered redis --scan SHA BEFORE==AFTER (captures composite); E2E teardown registers composite keys so CLEANUP/INJECT removes them before AFTER; no TTL settle-wait | ✓ |
| Add explicit composite-namespace==0 assertion | Separate additive count==0 scan on the composite pattern | |
| Both | Unfiltered SHA + composite==0 + DLQ depth==0 | |

**User's choice:** Unfiltered --scan SHA + E2E active-clean
**Notes:** Composite's 2-day TTL can't be waited out — a leak surfaces as a redis SHA mismatch. Standard skp-dlq-1 depth==0 retained.

## Claude's Discretion

- Exact E2E file names/namespaces + non-parallel collection name.
- Precise docker stop/start wait/poll timings + steady-state detection.
- Fresh phase-49-close.ps1 clone vs parameterized reuse of phase-39-close.ps1.
- Processor-row seed version string (verify v4 Processor.Sample version).
- SC2 direct-publish helper (reuse kit vs new).
- 49-HUMAN-UAT.md runbook contents/format.

## Deferred Ideas

- Actual live N×GREEN close run (operator-gated, 49-HUMAN-UAT.md).
- Literal queue rename skp-dlq-1 → _DLQ1 (out of scope, carried from Phase 47).
- v4.0.0 milestone audit / archival (post-49 step).
