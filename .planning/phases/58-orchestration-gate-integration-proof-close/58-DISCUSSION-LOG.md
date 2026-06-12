# Phase 58: Orchestration-Gate Integration Proof & Close - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-13
**Phase:** 58-orchestration-gate-integration-proof-close
**Areas discussed:** Config-incompatible processor mechanism, E2E test surface, Close-gate derivation, Compose placement, CFG-08 proof assertions

---

## Config-incompatible processor mechanism (CFG-08 subject)

| Option | Description | Selected |
|--------|-------------|----------|
| Second real container | New processor console (distinct SourceHash → own DB row → own ConfigSchemaId) seeded with a clashing schema; runs alongside Processor.Sample. Most faithful to RealStack-container close-gate culture; SourceHash/liveness machinery unmodified. | ✓ |
| In-process incompatible host | Host a BaseProcessor<TConfig> with a clashing schema inside the E2E test process. Lower infra, but a new harness pattern, less 'real-stack'. | |
| One binary, reseat config-schema | Single Processor.Sample; reseed incompatible ConfigSchemaId + bounce for CFG-08. No new project, but sequential/stateful/fragile. | |

**User's choice:** Second real container (Recommended)
**Notes:** Constraint that forced the question — one binary = one SourceHash = one DB row = one ConfigSchemaId, so Processor.Sample can't be both compatible and incompatible at once. Drives D-01/D-02/D-03.

---

## E2E test surface

| Option | Description | Selected |
|--------|-------------|----------|
| New Gate-A tests + retag v5 recovery SCs | Add Gate-A composition tests AND retag SC1/SC2/SC3 into the phase-58 live run as full regression (v6 left recovery unchanged → seal everything). | ✓ |
| New Gate-A tests only | Only the new Gate-A tests in the phase-58 gate; v5 SCs stay Phase 55. Narrower/faster. | |

**User's choice:** New Gate-A tests + retag v5 recovery SCs (Recommended)
**Notes:** Drives D-07. Milestone-close "seal everything" intent.

---

## Close gate derivation

| Option | Description | Selected |
|--------|-------------|----------|
| Clone phase-55-close.ps1 verbatim + v6 seed deltas | Clone the proven triple-SHA protocol; only deltas are the two-schema/two-processor CREATE-IF-ABSENT seed (frozen-once-referenced safe), version-string verify, badconfig profile bring-up. | ✓ |
| Let me describe specific changes | Freeform custom deltas. | |

**User's choice:** Clone phase-55-close.ps1 verbatim + v6 seed deltas (Recommended)
**Notes:** Drives D-08/D-09. v6 left slot-array/recovery machinery unchanged.

---

## Compose placement of the broken processor

| Option | Description | Selected |
|--------|-------------|----------|
| Dedicated tier behind a Compose profile | processor-badconfig gated behind a Compose profile; default `docker compose up` stack stays clean; gate brings it up explicitly. | ✓ |
| Plain always-on compose tier | Always-on tier like processor-sample; simpler, but default stack permanently carries a known-broken processor. | |
| Test-orchestrated container (no compose tier) | E2E test starts/stops the container directly; most isolated, new lifecycle pattern. | |

**User's choice:** Dedicated tier behind a Compose profile (Recommended)
**Notes:** Drives D-04. Resolved wrinkle: per Phase-57 D-09 the incompatible processor still flips MarkReady → /ready healthcheck passes; only L2 liveness withheld → net-zero-harmless (D-05).

---

## CFG-08 proof assertions

| Option | Description | Selected |
|--------|-------------|----------|
| Both: logged clash + absent liveness + 422 | Assert the D-10 Error-level config-clash log (via ES poll) + no skp:{id} liveness key + orchestration-start 422. The log distinguishes 'Gate A withheld' from 'processor not running'. | ✓ |
| Absent liveness + 422 only | Simpler (reuses SC harness mechanics) but doesn't positively prove Gate A fired vs processor down; relies on negative-control to imply causation. | |

**User's choice:** Both: logged clash + absent liveness + 422 (Recommended)
**Notes:** Drives D-06. The log assertion is the causation linchpin.

---

## Claude's Discretion

- The exact config-schema clash shape for Processor.BadConfig (property + schema-type↔CLR-type pair), per Phase-57 D-02/D-04.
- Processor.BadConfig project shape + Compose profile name.
- The compatible config-schema Definition for Processor.Sample's CFG-09 seed.
- xUnit collection/parallelization for the new Gate-A tests; inverse liveness-absence poll mechanics.

## Deferred Ideas

None — discussion stayed within CFG-08/09 + close-gate scope.
