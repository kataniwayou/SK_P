# Phase 50: Contracts & Slot-Array L2 Key Reshape - Context

**Gathered:** 2026-06-11
**Status:** Ready for planning

<domain>
## Phase Boundary

Reshape the **contract layer only** for the v5.0.0 recovery re-architecture (design-doc Amendment **A18**, LOCKED):

1. Add the slot-array allocation-index key builder `L2[messageId][slot]` to `L2ProjectionKeys`.
2. Reshape the three surviving Keeper-state contracts (`REINJECT`/`INJECT`/`DELETE`) to their A18 id-sets.
3. Delete the Model-B contracts — `UPDATE`/`CLEANUP` records, the composite backup key `corr:wf:proc:exec`, and `BackupOptions` — with a source/reflection scan finding **no references** (RETIRE-01, RETIRE-02 at contract level).
4. Keep the solution buildable 0-warning (Release + Debug), hermetic suite green.

**This phase is contract-shape + compile-keeping only.** The processor FORWARD/RECOVERY pipeline rebuild is **Phase 51**, the 3-state Keeper recovery consumer is **Phase 52**, and full Model-B teardown + remnant-verification is **Phase 53**. A18 is the LOCKED source of truth — the WHAT is decided; these decisions cover only the HOW the design doc left open.

</domain>

<decisions>
## Implementation Decisions

### Build-Keeping Strategy (the build-before-teardown tension)
Deleting `KeeperUpdate`/`KeeperCleanup` and `L2ProjectionKeys.CompositeBackup` compile-breaks `ProcessorPipeline` (BuildUpdate/BuildCleanup + Post Model-B mechanics) and **all five** Keeper consumer bodies (Update/Cleanup/Reinject/Inject/Delete all reference the composite key) plus the two `UsePartitioner<KeeperUpdate>`/`<KeeperCleanup>` lines in the single-owner `UpdateConsumerDefinition`. Decision:

- **D-01:** **Delete-orphans + stub survivors.** In Phase 50:
  - **Delete** `UpdateConsumer` + `CleanupConsumer` + their `ConsumerDefinition`s, their `Program.cs` registrations, `BackupOptions`, and `ProcessorPipeline.BuildUpdate`/`BuildCleanup`.
  - **Re-home** the endpoint single-ownership (currently on `UpdateConsumerDefinition` — the SINGLE OWNER of `UseMessageRetry` + the `UsePartitioner<>` set on `keeper-recovery`) onto a surviving definition (e.g. `ReinjectConsumerDefinition`); drop the partitioner set to the 3 surviving types.
  - **Reduce to compile-only stubs** the 3 surviving consumer bodies (`Reinject`/`Inject`/`Delete`) and the `ProcessorPipeline` Post-Process Model-B mechanics that referenced the composite key — `throw NotImplementedException` / no-op — since their real A18 rewrites land in Phase 51 (pipeline) and Phase 52 (keeper).
- **D-02:** This matches the project's established **"dark-but-compiling pending real retirement"** precedent (Phase 43 kept the reactive path dark-but-compiling). The locked build order holds: **50** contracts/keys + compile-keep → **51** processor pipeline (real forward/recovery passes) → **52** 3-state keeper consumer → **53** remnant-verification (`RETIRE-03`).
- **D-03:** The `IKeeperRecoverable` partition marker (`corr:wf:proc:exec`) is **unchanged** — it survives the composite-backup *key builder* deletion (the marker is the partition 4-tuple, not the deleted L2 key string). `PartitionKey`/`PartitionGuid` helpers stay.

### Slot-Array L2 Key (SLOT-01/02 vocabulary)
- **D-04:** **Array-key + Redis HASH.** New builder `L2ProjectionKeys.MessageIndex(Guid messageId)` → **`skp:msg:{messageId:D}`**. The `msg:` segment is a required namespace discriminator (the flat scheme has no type discriminator — bare `skp:{guid}` collides with `Root(workflowId)`/`Processor(processorId)`; `ExecutionData` sets the `data:` precedent). `messageId` = the **MassTransit broker `MessageId`** (a `Guid`).
- **D-05:** The slot is a **Redis HASH field** (int slot index → entryId value), NOT part of the key string. This is the structure family the builder signature locks for Phase 51: recovery reads `entryIds[]` via one `HGETALL`; slot retire = `HSET slot guid.empty`; one `EXPIRE(random)` on the whole key. Chosen over flat per-slot string keys (which would force a `SCAN` for the whole-array read) and over a LIST (clunkier grow-then-`LSET`/retire-by-index).
- **D-06:** **Golden-test-pinned.** A golden test pins the exact `skp:msg:{messageId:D}` string (mirrors the existing `ExecutionData`/`Root` key-format pins).

### Slot-Array Random TTL
- **D-07:** **Defer to Phase 51.** The key builder bakes **no TTL** (caller concern — mirrors `ExecutionData`). The `TTL(random)` min/max range + its options record are a **Phase-51** concern, authored in `BaseProcessor.Core` when the forward-pass write lands — NOT in the deleted Keeper-console `BackupOptions` location. Phase 50 ships only the key shape.

### INJECT Contract Reshape (A18 id-sets)
- **D-08:** **Match the A18 literal.** `KeeperInject` gains three fields beyond the 5-id base:
  - `EntryId` (`Guid`) — the allocation to write `L2[entryId]=data`.
  - `Data` (`string`) — the raw-JSON output, in-hand on the envelope (same role as the deleted `KeeperUpdate.ValidatedData`; D-02 raw-string convention).
  - `DeleteEntryId` (`Guid`) — the source entryId deleted after the orchestrator send. Name tracks the A18 spec literal `deleteEntryId` verbatim (chosen over `SourceEntryId` to avoid doc/golden-test cross-ref friction).
- **D-09:** `KeeperReinject` (carries `EntryId` + `Payload`) and `KeeperDelete` (carries `EntryId`) **already match** A18 (`REINJECT(ids, entryId, payload)` / `DELETE(entryId)`) — **no change**.

### Claude's Discretion
- Exact surviving definition chosen as the re-homed endpoint single-owner (`ReinjectConsumerDefinition` suggested).
- Stub style (`NotImplementedException` vs no-op return) per call site, as long as the solution builds 0-warning and no Model-B contract reference survives.
- Whether the HASH-field slot index is pinned in the same golden test or a sibling.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Source of Truth (LOCKED)
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` → **"Recovery Re-architecture (A18)"** (lines ~130-227, LOCKED 2026-06-11) — the v5.0.0 source of truth. Specifically: §New L2 vocabulary (`L2[messageId]` slot array), §Processor FORWARD/RECOVERY passes, §Keeper 3 states (`REINJECT`/`INJECT`/`DELETE` id-sets), §Invariants. Everything above A18 in that doc (Identities & L2 data key, Result contract A15, BIT gate + pause/resume A14, at-least-once A16, single `_DLQ1` A4) still holds unchanged.

### Requirements & Roadmap
- `.planning/REQUIREMENTS.md` — **RETIRE-01, RETIRE-02** are the Phase-50 scope (contract removal); the SLOT/INFRA/FWD/RECOV/KEEP families are downstream (51-52) but inform the contract shapes built here.
- `.planning/ROADMAP.md` → **Phase 50** section — the 4 success criteria.

### Contracts to reshape (this phase)
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — add `MessageIndex`; delete `CompositeBackup`.
- `src/Messaging.Contracts/KeeperInject.cs` — add `EntryId`/`Data`/`DeleteEntryId`.
- `src/Messaging.Contracts/KeeperReinject.cs`, `KeeperDelete.cs` — unchanged (verify match).
- `src/Messaging.Contracts/KeeperUpdate.cs`, `KeeperCleanup.cs` — **delete**.
- `src/Messaging.Contracts/IKeeperRecoverable.cs` — unchanged (partition marker stays).

### Code to touch for build-keeping (D-01)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — delete `BuildUpdate`/`BuildCleanup`; stub Post Model-B mechanics.
- `src/Keeper/Recovery/UpdateConsumer.cs`, `UpdateConsumerDefinition.cs`, `CleanupConsumer.cs`, `CleanupConsumerDefinition.cs` — **delete**; re-home endpoint ownership.
- `src/Keeper/Recovery/ReinjectConsumer.cs`, `InjectConsumer.cs`, `DeleteConsumer.cs`, `RecoveryConsumerBase.cs` — stub composite-backup bodies (compile-only).
- `src/Keeper/Program.cs` — remove `UpdateConsumer`/`CleanupConsumer` registrations + `BackupOptions` Configure.
- `src/Keeper/BackupOptions.cs` — **delete**.
- `src/Keeper/RecoveryOptions.cs` — verify no composite-backup coupling.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`L2ProjectionKeys`** (`Messaging.Contracts.Projections`) — flat single-prefix `const Prefix="skp:"` scheme, no type discriminator; `ExecutionData(entryId)` → `skp:data:{entryId:D}` is the precedent for the new `MessageIndex` `msg:` discriminator + no-baked-TTL pattern.
- **Golden key-format tests** — existing pins on `Root`/`Step`/`Processor`/`ExecutionData` strings are the template for the `skp:msg:{messageId:D}` pin (D-06).
- **`IKeeperRecoverable` + `UpdateConsumerDefinition.PartitionKey/PartitionGuid`** — the partition 4-tuple machinery survives untouched; only the endpoint *owner* re-homes off the deleted `UpdateConsumerDefinition`.
- **Record-contract idiom** — Keeper-state records use a positional 5-id base ctor (`WorkflowId, StepId, ProcessorId`) + `init` props for `CorrelationId`/`ExecutionId`/extras, default STJ (no `[JsonPropertyName]`). `KeeperInject`'s 3 new fields follow this exact idiom.

### Established Patterns
- **"Dark-but-compiling pending retirement"** — Phase 43 precedent for keeping a soon-to-be-retired path compiling without exercising it; D-01/D-02 apply it to the surviving consumer bodies.
- **Single-owner endpoint definition** — `keeper-recovery` has exactly one `ConsumerDefinition` registering `UseMessageRetry` + `UsePartitioner<>`; the others are intentional no-ops. Re-homing must preserve this single-owner invariant.
- **Raw-JSON string data on the wire** (D-02 convention) — the deleted `KeeperUpdate.ValidatedData` was a `string`; `KeeperInject.Data` continues it.

### Integration Points
- `Messaging.Contracts` is the leaf both `BaseProcessor.Core` (Phase 51) and `Keeper` (Phase 52) depend on — contract shapes locked here are the seam those phases build against.
- The `keeper-recovery` queue endpoint (`KeeperQueues.Recovery`) and its `Program.cs` registration set are the wiring touched by the re-home.

</code_context>

<specifics>
## Specific Ideas

- `MessageIndex(Guid messageId)` → `skp:msg:{messageId:D}` — the exact string to golden-pin.
- `KeeperInject` final field set: 5-id base + `EntryId` (Guid) + `Data` (string) + `DeleteEntryId` (Guid).
- `messageId` source is the MassTransit broker `MessageId` (`ConsumeContext.MessageId`, a `Guid`) — relevant to Phase 51's read of it, surfaced here so the key type is correct.

</specifics>

<deferred>
## Deferred Ideas

- Slot-array `TTL(random)` min/max range + its options record → **Phase 51** (BaseProcessor.Core).
- Real processor FORWARD/RECOVERY pass logic (allocation-before-data, split infra, send-before-retire) → **Phase 51**.
- Real 3-state Keeper recovery consumer (gate-open-only, configurable DLQ1-vs-outage exhaustion, non-destructive gate-closed consume) → **Phase 52**.
- Full Model-B teardown + reflection/source remnant sweep (RETIRE-03) → **Phase 53**.

None of these are scope creep — they are the locked downstream phases of the same milestone; captured so Phase 50 stays contract-only.

</deferred>

---

*Phase: 50-contracts-slot-array-l2-key-reshape*
*Context gathered: 2026-06-11*
