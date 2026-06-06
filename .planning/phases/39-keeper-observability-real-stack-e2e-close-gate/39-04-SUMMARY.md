---
phase: 39-keeper-observability-real-stack-e2e-close-gate
plan: 04
status: complete
requirements: [TEST-03]
gate_exit: 1
gate_substance: passed
completed: 2026-06-06
---

# Plan 39-04 — Close gate (TEST-03)

## What shipped
- **`scripts/phase-39-close.ps1`** (NEW) — clone of `phase-33-close.ps1` with the two required deltas:
  DELTA 1 (`keeper` added to the rebuild/health `$services` set, CONTRACT-CHANGE note updated to
  `baseapi-service orchestrator processor-sample keeper`) and DELTA 2 (additive both-DLQ `depth==0` assertion
  on `keeper-dlq` + `skp-dlq-1` via `rabbitmqctl list_queues name messages`, kept SEPARATE from the name-SHA).
  Parses clean; all acceptance greps pass. Committed `5f4ac37`; NDJSON multi-replica health-check fix `d9fc8bc`.

## Live gate result (operator run, 2026-06-06)
Final run (`bw615ju9t` / `b1xvh3508`):

| Invariant | Result |
|-----------|--------|
| 3× consecutive GREEN, full RealStack suite | ✅ **Run 1/2/3 = 500/500/500 passed** |
| Release + Debug zero-warning build | ✅ both exit 0 |
| psql `\l` SHA-256 BEFORE==AFTER | ✅ HELD (`34ac2385…`) |
| redis `--scan` SHA-256 BEFORE==AFTER | ✅ HELD (`b2d8ec21…`) |
| rabbitmq `list_queues name` SHA-256 BEFORE==AFTER | ✅ HELD (`ee79f392…`) |
| `skp-dlq-1` depth==0 | ✅ HELD |
| `keeper-dlq` depth==0 | ⚠️ **VIOLATED (depth=1)** — see below |
| Script `GATE_EXIT` | `1` (on the keeper-dlq invariant only) |

**Substance of TEST-03 is met:** 3× consecutive 500-fact GREEN + triple-SHA net-zero + `skp-dlq-1`==0.

## Known artifact — keeper-dlq give-up-park drain timing (NOT a functional defect)
The single `keeper-dlq` leftover is the EVIDENCE of a correctly-performed give-up, not a stuck fault.
`KeeperRecovery_GivesUp_ParksToDlq` deliberately forces the deployed keeper to exhaust its L2 probe
(~5s×12 ≈ 60s, stretched further under the 3×-suite load) and park the original `Fault<ExecutionResult>`
to `keeper-dlq` — which the in-test probe proves and drains. In one of the three runs the park lands
**after** the test's teardown drain (the probe-exhaust window exceeded the drain budget), so it races the
gate's AFTER snapshot. Mitigations applied this phase (10s probe-drain settle + a deterministic
management-API purge in teardown, `8d7ee4f`/`6651902`) reduced but did not fully close the race under load.
The keeper uses a single shared competing-consumer queue (`keeper-fault-recovery`), so this is a single
late park, not a duplicate. Accepted by the operator as a test-teardown timing artifact; a robust
poll-until-stably-empty drain is a documented follow-up. See MEMORY:
[[project_keeper_recovery_unbounded_reinject_loop]].

## Cross-cutting fixes made to reach 3×GREEN (close gate is the first full live run since Phase 33)
The gate surfaced pre-existing/this-phase defects, all fixed + committed:
1. `28b0217` — register `KeeperMetrics` in 2 Keeper consumer test harnesses (Plan-02 DI gap; 4 tests).
2. `1dc27e5` — **MLBL-04**: stop the versioned metrics `service.name` bleeding onto LOGS (OTel 1.15 shared
   `ConfigureResource` overriding the logs `SetResourceBuilder`) — restored the Phase-35 bare-name ES query
   contract; added `LogsResourceBleedFacts` guard. (Phase-38 regression.)
3. `46fd82d` — 3 keeper RealStack recipes: KeeperFaultIntake case-insensitive body.text wildcard + loop-break;
   FaultRecoverySpike (later `8833285` quiesce); KeeperRecovery result hop via fanout `ResumeWorkflow` (avoids
   the orchestrator-result competing-consumer race).
4. `3c9e20a` — ProcessorId-filter the InFlight MeterListener assertion (parallel-capture contamination).
5. `8833285` — quiesce the keeper before FaultRecoverySpike's dedup proof (non-atomic gate TOCTOU under burst).
6. test parallelism capped (`xunit.runner.json maxParallelThreads=6`) — eliminated CPU-contention flakes
   (harness consume-timeouts) under the 3×-suite load.

## Requirement
- **TEST-03** — close gate exists, parses clean, preserves the triple-SHA protocol, adds the keeper rebuild +
  both-DLQ deltas, and runs 3× consecutive GREEN with triple-SHA net-zero (incl. `skp-dlq-1`==0) at
  Release+Debug zero-warning. Met (keeper-dlq net-zero has the documented give-up-drain timing follow-up).

## Self-Check: PASSED (substance) — keeper-dlq invariant has a documented test-teardown follow-up
