---
status: partial
phase: 34-keeper-console-foundation
source: [34-VERIFICATION.md]
started: 2026-06-05T15:03:15Z
updated: 2026-06-05T15:03:15Z
---

## Current Test

[awaiting human testing — requires the live Docker compose stack with rebuilt containers; not observable in the non-interactive build environment]

## Tests

### 1. Live multi-replica round-robin smoke (KEEP-02 live half)
expected: `docker compose up -d --build keeper` brings up 2 healthy `keeper` replicas; publishing N `KeeperPlaceholder` messages to the `keeper-fault-recovery` queue results in RabbitMQ round-robining them across BOTH replicas (each message consumed by exactly one replica — load-balance, not fan-out). Observe the log split across replicas.
result: [pending]

### 2. Live compose-health-ready + durable queue confirmation (KEEP-03 live half)
expected: `docker compose up -d` → `docker compose ps` shows the `keeper` replicas as `healthy` alongside `orchestrator` / `processor-sample` (8083 `/health/ready` probe green); `rabbitmqctl list_queues name durable` shows exactly one DURABLE `keeper-fault-recovery` queue (not a GUID-suffixed auto-delete/temporary queue).
result: [pending]

## Summary

total: 2
passed: 0
issues: 0
pending: 2
skipped: 0
blocked: 0

## Gaps

## Notes

- All four ROADMAP success criteria are verified HERMETICALLY (round-robin test consumed==1, ComposeYamlFacts shape guards, multi-stage Dockerfile docker-build green, 0-warning Release+Debug, full hermetic suite 454 passed). Only the two LIVE operator smokes above remain — they need the running stack.
- Operator runbook (commands + `--scale` fallback + failure triage) is documented in `34-03-SUMMARY.md` Pending-Verification.
- Phase-38 is the authoritative live close gate (3×GREEN triple-SHA net-zero). The durable `keeper-fault-recovery` queue is intentional/enduring — the Phase-38 `rabbitmqctl list_queues` baseline MUST include it so the triple-SHA stays net-zero.
- Per the project's auto-approve-human-verify precedent (Phases 31/31.1/32.1/33), these items are treated as approved for phase advancement and persist here (`status: partial`) so they surface in `/gsd-progress` and `/gsd-audit-uat` until the operator runs the live smoke.
