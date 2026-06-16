# Phase 70: Processor INJECT Cleanup - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-16
**Phase:** 70-processor-inject-cleanup
**Areas discussed:** Negative-guard enforcement style, Test layout for the delete invariant, Dropped-field guard

---

## Negative-guard enforcement style (KINJ-01 + KINJ-03)

| Option | Description | Selected |
|--------|-------------|----------|
| Behavioral DidNotReceive | Run consumer with substitute IDatabase, assert `db.DidNotReceive().KeyDeleteAsync(...)`. Consistent with existing InjectConsumerFacts NSubstitute pattern. Catches only the overloads asserted against. | ✓ |
| Reflection/IL method scan | Scan the consumer type's IL for any `KeyDelete*` call. Catches all overloads + future additions. New pattern in this suite. | |
| Source-text scan | Read the `.cs` files, assert no `KeyDelete` substring. Simple but brittle (comments/strings), not behavior-level. | |

**User's choice:** Behavioral DidNotReceive
**Notes:** Keeps the established idiom — the suite proves intent by running the consumer
against a substitute and asserting on the calls it makes.

---

## Test layout for the delete invariant (KINJ-03)

| Option | Description | Selected |
|--------|-------------|----------|
| Dedicated invariant fact | New `KeeperDeleteInvariantFacts` asserting DELETE deletes AND INJECT/REINJECT do not — the whole "DELETE-only-deletes" rule in one readable place. | ✓ |
| Extend per-consumer facts | Add no-delete assertions to InjectConsumerFacts (KINJ-01) and ReinjectConsumerFacts (KINJ-03) separately. Invariant spread across two files. | |

**User's choice:** Dedicated invariant fact
**Notes:** The dedicated fact covers all three states (Delete = positive, Inject + Reinject =
negative). InjectConsumerFacts still carries its own KINJ-01 no-delete belt; minor, intentional
overlap.

---

## Dropped-field guard (KINJ-02)

| Option | Description | Selected |
|--------|-------------|----------|
| Reflection guard in contract test (all three) | Reshape KeeperContractTests to assert KeeperInject carries only EntryId+Data AND `Assert.Null(GetProperty("DeleteEntryId"))`, keeping all-three record coverage. Permanent guard against re-adding the field; directly satisfies the KINJ-02 reflection-scan criterion. | ✓ |
| Positive shape only | Reduce the contract test to assert just EntryId+Data; rely on a one-time verification grep. Lighter, no permanent guard. | |

**User's choice:** Reflection guard in contract test — extended to all three keeper records
**Notes:** User initially paused to review the current code flow (InjectConsumer's
write→send→delete body, BuildInject populating DeleteEntryId, and the four test touch-points)
before confirming. "For all three" = keep the all-three KeeperContractTests coverage intact,
reshaping the INJECT fact and locking the field's absence.

## Claude's Discretion

- Exact filename/namespace of the new dedicated invariant fact.
- Exact wording of rewritten XML doc comments.
- How to express the surviving write-then-send order in InjectConsumerFacts now that op 3 is gone.

## Deferred Ideas

- INJECT index-slot write (canonical spec §8 divergence) — not in Phase 70 scope; the ROADMAP
  scopes INJECT to two effects. Noted as an observed gap for a future phase.
- `L2ProbeRecovery.cs:35` scratch delete — a probe net-zero op, not a keeper state; explicitly
  outside the DELETE-only invariant.
