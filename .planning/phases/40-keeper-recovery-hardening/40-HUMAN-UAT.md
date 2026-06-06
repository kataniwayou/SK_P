---
status: partial
phase: 40-keeper-recovery-hardening
source: [40-VERIFICATION.md]
started: "2026-06-06T20:51:14.000Z"
updated: "2026-06-06T20:51:14.000Z"
---

## Current Test

[awaiting human testing]

## Tests

### 1. Live close-gate: 3× GREEN with rebuilt containers

expected: Rebuild `baseapi-service`, `orchestrator`, `processor-sample`, and `keeper` container images from HEAD, bring up the full compose stack, then run `pwsh -File scripts/phase-39-close.ps1` three consecutive times. Each run exits GREEN (GATE_EXIT=0); `keeper-dlq depth==0` in every BEFORE and AFTER snapshot across all three runs; triple-SHA net-zero (psql `\l` hash, redis `--scan skp:*` hash, `rabbitmqctl list_queues` hash identical BEFORE/AFTER each run); Release+Debug 0-warning. The bounded drain (`DrainKeeperDlqUntilStablyEmptyAsync`) tolerates both the probe-exhausted 2-replica late park (>10s apart) AND the Plan-02 `recover_cap` single-winner park without racing the AFTER snapshot.
why_human: `KeeperRecovery_GivesUp_ParksToDlq` is `[Trait("Category","RealStack")]` — it needs a running compose stack with two Keeper replicas that independently exhaust the probe loop and park to `keeper-dlq` >10s apart. The 15s stably-empty window of `DrainKeeperDlqUntilStablyEmptyAsync` can only be proven against this real multi-replica timing. KHARD-01's cap is proven hermetically only — a LIVE cap test is FORBIDDEN (would flood the stack ~67 cyc/s/replica) per 40-VALIDATION.md.
result: [pending]

## Summary

total: 1
passed: 0
issues: 0
pending: 1
skipped: 0
blocked: 0

## Gaps
