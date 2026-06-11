# Phase 50: Contracts & Slot-Array L2 Key Reshape - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-11
**Phase:** 50-contracts-slot-array-l2-key-reshape
**Areas discussed:** Build-keeping strategy, Slot-array key shape, Slot-array TTL home, INJECT field reshape

---

## Build-Keeping Strategy

| Option | Description | Selected |
|--------|-------------|----------|
| Delete-orphans + stub survivors | Delete Update/Cleanup consumers+defs+regs+BackupOptions+Pipeline builders; re-home endpoint ownership; stub the 3 surviving consumer bodies + Pipeline Post Model-B mechanics; real rewrites in 51/52, remnant-verify in 53 | ✓ |
| Merge full teardown into 50 | Above + fully collapse 5→3 consumer skeleton now, making Phase 53 pure verification; bends locked build-before-teardown order most | |
| Disable Model-B source in place | Keep files, #if-disable/comment; risks tripping SC2 'no references' scan; dead code | |

**User's choice:** Delete-orphans + stub survivors
**Notes:** Matches the project's "dark-but-compiling pending retirement" precedent (Phase 43). The composite-key deletion breaks all 5 consumer bodies; the `KeeperUpdate`/`KeeperCleanup` type deletion breaks `ProcessorPipeline.BuildUpdate/BuildCleanup` + 2 `UsePartitioner<>` lines in the single-owner `UpdateConsumerDefinition`. `IKeeperRecoverable` partition marker survives unchanged.

---

## Slot-Array Key Shape

| Option | Description | Selected |
|--------|-------------|----------|
| Array-key + Redis HASH | `MessageIndex(Guid messageId)` → `skp:msg:{messageId:D}`; slot = hash field; whole-array read via HGETALL; one EXPIRE | ✓ |
| Array-key + Redis LIST | Same key; slot = list index via RPUSH/LSET/LRANGE; clunkier retire-by-index | |
| Flat per-slot keys | `MessageSlot(messageId, slot)` → `skp:msg:{messageId:D}:{slot}`; recovery needs SCAN; N TTLs | |

**User's choice:** Array-key + Redis HASH
**Notes:** A18's recovery pass reads `L2[messageId] → entryIds[]` (whole array in one op) and retires individual slots — HASH (HGETALL + HSET-field) fits best. The golden-test-pinned builder signature locks the structure family for Phase 51. `messageId` = MassTransit broker `MessageId` (Guid); `msg:` discriminator required (flat `skp:{guid}` collides with Root/Processor).

---

## Slot-Array TTL Home

| Option | Description | Selected |
|--------|-------------|----------|
| Defer to Phase 51 | Builder bakes no TTL (ExecutionData precedent); random-TTL range + options record authored in BaseProcessor.Core in Phase 51 | ✓ |
| Introduce options record now | Add a BackupOptions successor in Phase 50; but consumed only in 51 and home is BaseProcessor.Core, not the deleted Keeper location | |

**User's choice:** Defer to Phase 51
**Notes:** Phase 50 stays contract-shape only. The deleted `BackupOptions` lived in `src/Keeper/`; the slot-array is processor-written, so its TTL knob belongs in `BaseProcessor.Core` in Phase 51.

---

## INJECT Field Reshape

| Option | Description | Selected |
|--------|-------------|----------|
| Match A18 literal | Add `EntryId` (Guid), `Data` (string raw-JSON), `DeleteEntryId` (Guid); names track A18 verbatim | ✓ |
| Rename DeleteEntryId→SourceEntryId | Same fields/types, clearer intent name; diverges from locked spec wording | |

**User's choice:** Match A18 literal
**Notes:** `INJECT(ids, entryId, data, deleteEntryId)` — writes `L2[entryId]=data`, sends StepCompleted, deletes deleteEntryId. `Data` continues the deleted `KeeperUpdate.ValidatedData` raw-string role (D-02). `KeeperReinject`/`KeeperDelete` already match A18 — unchanged. Partition marker stays `corr:wf:proc:exec`.

## Claude's Discretion

- Re-homed endpoint single-owner definition (suggest `ReinjectConsumerDefinition`).
- Stub style per call site (NotImplementedException vs no-op).
- Whether the HASH slot-field index is pinned in the same golden test or a sibling.

## Deferred Ideas

- Slot-array random-TTL range/options → Phase 51.
- Processor forward/recovery pass logic → Phase 51.
- 3-state Keeper recovery consumer (gate/exhaustion policy) → Phase 52.
- Model-B remnant sweep (RETIRE-03) → Phase 53.
