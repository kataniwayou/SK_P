# Phase 43: Message Contracts & L2 Key Reshape - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-08
**Phase:** 43-message-contracts-l2-key-reshape
**Areas discussed:** Build-coexistence strategy, entryId type + data-key format, Composite key + TTL home, Keeper 5-state contract shape

---

## Area selection

| Option | Description | Selected |
|--------|-------------|----------|
| Build-coexistence strategy | H/entryId removal blast radius vs. teardown-is-Phase-48 | ✓ |
| entryId type + data-key format | string(64-hex)→Guid migration + L2 data key | ✓ |
| Composite key + TTL home | composite backup key format + configurable-in-days TTL | ✓ |
| Keeper 5-state contract shape | partition marker + recovery queue + ExecutionResult trim | ✓ |

**User's choice:** All four.
**Notes:** Surfaced upfront that `H`/`EntryId`/`IExecutionCorrelated` are referenced 37× across 13 files incl. the live execution path, while teardown is nominally Phase 48 — the central tension.

---

## Build-coexistence strategy

| Option | Description | Selected |
|--------|-------------|----------|
| Reshape in place; teardown bleeds into 43 | Honor SC-1; pull coupled RETIRE-01 + RETIRE-02 into 43; Phase 48 shrinks to RETIRE-03 + sweep | ✓ |
| Additive in 43; reshape+teardown deferred | Keep two execution contracts on v3.x shape; add new vocab only; 44/46 remove H-usage by replacement | |
| Hybrid: reshape contracts, bridge behavior | Local-H compute to preserve dedup — but doesn't help the entryId/content-addressing axis | |

**User's choice:** Reshape in place; teardown bleeds into 43.
**Notes:** Analysis established the coupling is real and non-bridgeable on the entryId axis (a random Guid kills content-addressing regardless), so RETIRE-01 + RETIRE-02 cannot stay in Phase 48.

### Follow-up — bleed scope

| Option | Description | Selected |
|--------|-------------|----------|
| Dead-machinery removal + straight-through | Remove flag[H]/CAS + content-addressing + manifest; adapt consumers to simplest compile-and-pass; v4 behavior stays in 44/46 | ✓ |
| Pull v4 consumer rewrites forward too | Collapse 44/46 consumer work into 43 | |

**User's choice:** Dead-machinery removal + straight-through.
**Notes:** Keeps 43 = contracts + keys + teardown without swallowing the Phase 44/46 incremental proof points.

---

## entryId type + data-key format

| Option | Description | Selected |
|--------|-------------|----------|
| Keep skp:data:{entryId:D} | Single ExecutionData(Guid) builder; drop content-addressed string + transitional Guid overloads; keep 'data:' segment | ✓ |
| Flatten to skp:{entryId:D} | Drop 'data:' segment; rely on GUID uniqueness vs root/processor keys | |

**User's choice:** Keep skp:data:{entryId:D}.

---

## Composite backup key + TTL home

| Option | Description | Selected |
|--------|-------------|----------|
| skp:-prefixed key + new BackupOptions | CompositeBackup => skp:{corr:D}:{wf:D}:{proc:D}:{exec:D}; TtlDays=2 in a new appsettings-bound options class (ProbeOptions precedent) | ✓ |
| Bare key + TTL on RetryOptions | Literal-to-doc bare key; reuse RetryOptions for the day knob | |

**User's choice:** skp:-prefixed key + new BackupOptions.
**Notes:** Prefix is a deliberate documented divergence from the doc's bare notation, for convention consistency.

---

## Keeper 5-state contract shape

| Option | Description | Selected |
|--------|-------------|----------|
| Shared marker interface + new queue const | IKeeperRecoverable (partition 4-tuple) on all 5 records; KeeperQueues.Recovery const; ExecutionResult keeps ErrorMessage/CancellationMessage, drops H | ✓ |
| Standalone records, no shared interface | Each record independent; partitioner builds key per-type | |

**User's choice:** Shared marker interface + new queue const.
**Notes:** Clarified post-answer that all five contracts carry `{corr, wf, step, proc, exec}` per the design doc id-sets; the partition 4-tuple (corr/wf/proc/exec, no step) is what `IKeeperRecoverable` exposes; `stepId` rides as a plain property.

## Claude's Discretion

- Sentinel helper naming/shape; `IKeeperRecoverable` member names; whether Keeper contracts also implement `ICorrelated` (leaning yes); `BackupOptions` appsettings section name; golden-test file layout.

## Deferred Ideas

- ROADMAP/REQUIREMENTS reconciliation for the RETIRE-01/02 phase move (doc update).
- `keeper-fault-recovery` / `keeper-dlq` const removal → Phases 47/48.
- Durable non-L2 recovery backup → milestone-deferred.
