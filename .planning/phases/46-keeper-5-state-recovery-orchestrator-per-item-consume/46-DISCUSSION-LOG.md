# Phase 46: Keeper 5-State Recovery + Orchestrator Per-Item Consume - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-08
**Phase:** 46-keeper-5-state-recovery-orchestrator-per-item-consume
**Areas discussed:** REINJECT payload reconstruction, Recovery consumer structure, Gate-wait bound, Terminal give-up routing, Retry-loop reuse, Partition count

---

## REINJECT payload reconstruction (scout-surfaced contract gap)

| Option | Description | Selected |
|--------|-------------|----------|
| Add Payload to KeeperReinject | Processor stamps the dispatch's Payload onto KeeperReinject; Keeper reconstructs a faithful EntryStepDispatch. Touches the Phase-43 shipped contract (additive) + the Phase-44 BuildReinject send site. | ✓ |
| Re-inject with empty Payload | No contract change, but the author's In-Process loses its config on the recovered run — silent behavior divergence. | |
| This is a real gap — let me decide | Stop and weigh in with full context. | |

**User's choice:** Add Payload to KeeperReinject
**Notes:** Highest-value finding of the discussion. `KeeperReinject` (shipped Phase 43) lacks `Payload`, but the locked design's "reconstructed EntryStepDispatch" requires it. Additive wire field + Phase-44 send-site edit + golden-test update; design-doc amendment warranted (user-owned).

---

## Recovery consumer structure

| Option | Description | Selected |
|--------|-------------|----------|
| 5 consumers + shared gate/retry base | Thin base (gate-await + RetryLoop wrapper) + 5 sealed per-state consumers, each with a ConsumerDefinition on queue:keeper-recovery. Mirrors the orchestrator TypedResultConsumer<T> symmetry; independently testable. | ✓ |
| One class implementing all 5 IConsumer<T> | Fewer files; gate/retry once. But five unrelated bodies in one class, harder to test. | |
| You decide | Claude picks during planning. | |

**User's choice:** 5 consumers + shared gate/retry base
**Notes:** Partitioner applies endpoint-wide regardless of the class split.

---

## Gate-wait bound (Phase-45 D-11 deferral resolved here)

| Option | Description | Selected |
|--------|-------------|----------|
| Once at Consume entry, bound ~5 min | await WaitForOpenAsync once at the top of Consume via a linked CTS bounded ~5 min (well under the 30-min broker consumer_timeout); timeout → un-acked → redelivered. | ✓ |
| Before each L2 op, bound ~5 min | Finer-grained but multiplies wait windows within one delivery. | |
| You decide the bound value | Lock 'await once at entry'; Claude picks exact seconds. | |

**User's choice:** Once at Consume entry, bound ~5 min
**Notes:** Matches the design's "gate-closed → waits bounded under the broker consumer timeout." Partitioner re-slots the redelivered message into its key group.

---

## Terminal give-up routing (_DLQ1 deferral)

| Option | Description | Selected |
|--------|-------------|----------|
| Defer to Phase 47 — throw to existing error queue | Mirror Phase-44 D-10: exhaustion propagates → MassTransit dead-letters to existing _error/keeper-dlq; REINJECT-data-gone throws a marker exception to force the same route. _DLQ1 rename stays Phase 47. | ✓ |
| Build _DLQ1 now | Pulls Phase-47 scope forward — contradicts the locked phase boundary. | |
| You decide | Claude picks during planning. | |

**User's choice:** Defer to Phase 47 — throw to existing error queue
**Notes:** Keeps every intermediate buildable, consistent with build-before-teardown order.

---

## Retry-loop reuse (project firewall)

| Option | Description | Selected |
|--------|-------------|----------|
| Relocate RetryLoop to BaseConsole.Core | Move the shared helper down so both BaseProcessor.Core and Keeper reference one A3 Retry:Limit implementation. | ✓ |
| Keeper gets its own retry helper | No cross-project move, but two implementations drift. | |
| You decide | Claude picks placement. | |

**User's choice:** Relocate RetryLoop to BaseConsole.Core
**Notes:** Keeper's firewall references only BaseConsole.Core + Messaging.Contracts, so it cannot reuse the Phase-44 RetryLoop in place.

---

## Partition count (UsePartitioner(N))

| Option | Description | Selected |
|--------|-------------|----------|
| Config knob, sensible default (e.g. 8) | Bind N from appsettings (mirrors Probe/Backup/Retry); default 8. Tunable without rebuild. | ✓ |
| Fixed constant | Simpler, but requires a rebuild to retune. | |
| You decide | Claude picks mechanism + default. | |

**User's choice:** Config knob, sensible default (e.g. 8)
**Notes:** Partition key is the IKeeperRecoverable 4-tuple (Phase-43 D-12).

---

## Claude's Discretion

- Exact namespaces/placement of the recovery-consumer base + give-up marker exception.
- Precise gate-wait timeout seconds under the 30-min bound; options-key choice.
- RetryLoop target namespace in BaseConsole.Core + exhaustion-surfacing parity.
- KeeperReinject Payload member name/position.
- REINJECT reconstructed-dispatch Send mechanism (reuse StepDispatcher's queue:{ProcessorId:D} idiom).

## Deferred Ideas

- _DLQ1 consolidation + at-least-once-no-dedup — Phase 47.
- Removal of dark reactive Fault<T> path + keeper-dlq + per-workflow pause/resume — Phase 48 (RETIRE-03).
- Design-doc amendment recording Payload-on-KeeperReinject — user-owned doc update.
