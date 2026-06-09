# Phase 48: v3.x Teardown - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-09
**Phase:** 48-v3-x-teardown
**Areas discussed:** Test disposition, Remnant-sweep scope, Close-gate depth, Audit/reconciliation

---

## Test disposition

| Option | Description | Selected |
|--------|-------------|----------|
| Delete + add negative guards | Delete orphaned reactive-only test classes, add a Phase-48 guard fact set proving absence (no `Fault<T>` consumer / `keeper-fault-recovery` endpoint / `keeper-dlq` const). Mirrors 47 `AtLeastOnceStructuralFacts`. | ✓ |
| Delete only | Delete orphaned tests so the suite compiles; no replacement guards. | |
| Convert in place | Rewrite each orphaned test to assert the negative rather than delete + write fresh. | |

**User's choice:** Delete + add negative guards
**Notes:** Self-verifying teardown — a future edit reintroducing a `Fault<T>` bind fails the guard. Orphaned classes: `KeeperFaultConsumerScopeTests`, `KeeperRecoverCapTests`, `KeeperRoundRobinTests`, reactive parts of `KeeperProbeLoopTests`.

---

## Remnant-sweep scope

| Option | Description | Selected |
|--------|-------------|----------|
| Exhaustive orphan hunt | Remove every now-dead dependent: dead config (`RecoveryOptions`, reactive-only `ProbeOptions`/`BackupOptions` members), `KeeperMetrics` fault counters, dead `Ignore<>`/bindings, retired `KeeperQueues` consts, stale `H`/manifest/`Fault<T>` comments. Preserve `L2ProbeRecovery.ProbeOnceAsync` (v4 BIT gate uses it). | ✓ |
| Named artifacts only | Delete enumerated files/consts/endpoint + verify RETIRE-01/02 symbols absent; leave orphaned config/metrics/comments. | |
| Sweep, but keep config/metrics | Remove dead code/bindings/comments but retain unused options classes + metric instruments. | |

**User's choice:** Exhaustive orphan hunt
**Notes:** Satisfies SC-4 "no dead `Ignore<>`/binding/key remnants" literally. Zero dead surface.

---

## Close-gate depth

| Option | Description | Selected |
|--------|-------------|----------|
| Hermetic + clean build only | Full hermetic suite GREEN (×3) + Release/Debug 0-warning build — exactly SC-4. No triple-SHA. Phase 49 owns live proof. | ✓ |
| Add triple-SHA infra gate | Also run triple-SHA `psql`/`redis`/`rabbitmqctl` BEFORE==AFTER (with documented expected queue delta). | |
| Record expected queue delta | Hermetic + clean build + a documented `rabbitmqctl` topology-delta note for Phase 49's baseline. | |

**User's choice:** Hermetic + clean build only
**Notes:** Pure deletion, no new behavior to live-prove. Removing `keeper-dlq` + `keeper-fault-recovery` makes `rabbitmqctl list_queues` deliberately non-net-zero this phase. Phase-49 baseline implication captured as a forward note in CONTEXT Deferred Ideas (not a 48 deliverable).

---

## Audit / reconciliation

| Option | Description | Selected |
|--------|-------------|----------|
| Full reconciliation | `48-TEARDOWN-AUDIT.md` ledger (RETIRE-01/02/03 + SC-1..4 → proving test/scan) + mark RETIRE-01/02/03 satisfied in REQUIREMENTS.md + design-doc amendment recording the retirement. | ✓ |
| Audit ledger only | Ledger + REQUIREMENTS.md update; skip design-doc amendment. | |
| Verification report only | Standard VERIFICATION.md; no ledger, no amendment; REQUIREMENTS.md update only. | |

**User's choice:** Full reconciliation
**Notes:** Last RETIRE phase — closes the v4.0.0 retirement story end-to-end. Follows the 47-DLQ-AUDIT.md + Phase-46/47 design-doc-amendment patterns.

---

## Claude's Discretion

- Negative-guard fact set namespace/file name (parity with `AtLeastOnceStructuralFacts`).
- Removal ordering within the phase (any order that keeps every intermediate commit buildable).
- `48-TEARDOWN-AUDIT.md` table layout.
- Design-doc amendment wording.
- Exactly which `ProbeOptions`/`BackupOptions` members are reactive-only vs. shared.

## Deferred Ideas

- Phase-49 net-zero baseline must start from the post-teardown topology (`keeper-dlq` + `keeper-fault-recovery` gone) — forward note, not Phase-48 scope.
