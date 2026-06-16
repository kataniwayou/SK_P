# Phase 70: Processor INJECT Cleanup - Context

**Gathered:** 2026-06-16
**Status:** Ready for planning

<domain>
## Phase Boundary

Make the keeper **INJECT** path non-destructive so it matches the canonical recovery
spec and the already-non-destructive REINJECT path. Three coupled changes:

1. **Behavior:** `InjectConsumer.HandleAsync` performs exactly **two** effects — write
   `L2[EntryId]=Data`, then send a reconstructed `StepCompleted` to
   `queue:orchestrator-result`. The trailing source-delete (`InjectConsumer.cs:40`,
   `Db.KeyDeleteAsync(L2[DeleteEntryId])`) is **removed**.
2. **Contract:** the now-vestigial `KeeperInject.DeleteEntryId` field is **dropped** from
   the record; `ProcessorPipeline.BuildInject` no longer supplies it.
3. **Tests:** the affected facts/golden tests move to the reduced id-set and stay green;
   a negative-guard fact makes the **"DELETE is the only keeper state that deletes keys"**
   invariant enforceable, not just documented.

**This is a mechanical cleanup** — the consumer change is one deleted op + one deleted
field, but it inverts assertions across four test files. No new capability is added.

**Out of scope:** giving INJECT the index-slot write (`L2[messageId][x]=outputEntryId`)
that canonical spec §8 also prescribes — the ROADMAP deliberately scopes INJECT to the
two effects above (see Deferred Ideas).

</domain>

<decisions>
## Implementation Decisions

### Consumer behavior (KINJ-01)
- **D-01:** `InjectConsumer.HandleAsync` keeps **exactly two** Guarded effects in order:
  (1) `Db.StringSetAsync(L2ProjectionKeys.ExecutionData(m.EntryId), m.Data)`, then
  (2) resolve the `queue:orchestrator-result` endpoint via `Guard` and `Send` the
  reconstructed `StepCompleted` (carrying `m.EntryId`). **Delete the entire op-3 block**
  at `InjectConsumer.cs:39-40` (`Db.KeyDeleteAsync(...ExecutionData(m.DeleteEntryId))`).
  Update the class XML doc (lines 10-16) to describe the two-effect non-destructive body
  and drop the Pitfall-5 "source delete is the tail" rationale.

### Contract reshape (KINJ-02)
- **D-02:** Remove the `DeleteEntryId` property from `KeeperInject` (`KeeperInject.cs:14`)
  and drop its mention from the record's XML doc (lines 4-7). The record then carries the
  5-id base + `EntryId` (Guid) + `Data` (string).
- **D-03:** `ProcessorPipeline.BuildInject` (`ProcessorPipeline.cs:430-438`) constructs
  `KeeperInject` **without** `DeleteEntryId` (drop line 437); update the INFRA-02/Pitfall-1
  comment (lines 428-429) to drop the `DeleteEntryId = the source entryId` clause.
- **No wire migration needed:** `KeeperInject` is a default-STJ bus envelope (no
  `[JsonPropertyName]`). STJ ignores unknown properties on deserialize, so an in-flight
  pre-deploy `KeeperInject` carrying `DeleteEntryId` deserializes harmlessly after deploy —
  the field is simply dropped and INJECT no longer needs it. Planner MUST NOT invent a
  versioning/migration step.

### Negative-guard enforcement (KINJ-01, KINJ-03)
- **D-04 (guard style):** Prove "never deletes" **behaviorally** with NSubstitute —
  `db.DidNotReceive().KeyDeleteAsync(...)` against a substitute `IDatabase`. Chosen for
  consistency with the existing `InjectConsumerFacts` NSubstitute pattern (not a
  reflection/IL scan, not a source-text scan).
- **D-05 (test layout):** Add **one dedicated invariant fact file** (Claude's discretion on
  exact name, e.g. `tests/BaseApi.Tests/Keeper/KeeperDeleteInvariantFacts.cs`) that locks
  the whole rule in one readable place:
  - `DeleteConsumer` **DOES** delete — `db.Received(...).KeyDeleteAsync(<multi-key>)`
    (the `RedisKey[]` overload at `DeleteConsumer.cs:20`).
  - `InjectConsumer` **does NOT** — `db.DidNotReceive().KeyDeleteAsync(...)` for every
    delete overload.
  - `ReinjectConsumer` **does NOT** — same assertion (it was already non-destructive;
    the guard makes that permanent).
  Reuse `RecoveryTestKit` (`Db()`, `Mux()`, `Retry()`, `CapturingSendProvider`).

### Field-removal guard (KINJ-02)
- **D-06:** Reshape the reflection contract test in `KeeperContractTests.cs`, keeping
  **all-three** coverage:
  - Rename `KeeperInject_carries_the_A18_id_set_EntryId_Data_DeleteEntryId` →
    a reduced-id-set name (e.g. `KeeperInject_carries_the_reduced_id_set_EntryId_Data`).
  - Assert `EntryId` (Guid) and `Data` (string) are present **and add the negative guard**
    `Assert.Null(typeof(KeeperInject).GetProperty("DeleteEntryId"))` so re-adding the field
    breaks the build. This directly satisfies the KINJ-02 "reflection scan finds no
    remaining DeleteEntryId reference" criterion.
  - Update the class-level XML doc (lines 12-13) to drop "INJECT carries ... + DeleteEntryId"
    → "INJECT carries EntryId + Data". Leave the REINJECT (EntryId+Payload) and DELETE
    (EntryId+MessageId) facts unchanged.

### Compile-driven test updates (KINJ-02 — "stay green", in scope)
- **D-07:** `InjectConsumerFacts.cs` — invert the delete half: remove
  `DeleteEntryId = Guid.NewGuid()` from the test message (line 35); drop the
  `Received.InOrder(write < KeyDelete)` chain and the `db.Received(1).KeyDeleteAsync(...)`
  assertion (lines 57-74); the order check now locks **write-then-send** only
  (`CapturingSendProvider` already captures the single send), plus a
  `db.DidNotReceive().KeyDeleteAsync(...)` belt. Update the class XML doc.
- **D-08:** `PipelineForwardFacts.cs` — delete the `Assert.Equal(d.EntryId, inj.DeleteEntryId)`
  assertion (line 149) and adjust the NODROP-01 doc comment (line 19) to drop `DeleteEntryId`.
- **D-09:** `SC2RecoveryPathsE2ETests.cs` — remove `DeleteEntryId = deleteEntryId` from the
  `KeeperInject` it builds (line 204), remove the "delete `L2[m.DeleteEntryId]`" assertion and
  any `deleteEntryId` seeding, and update the doc comments (lines 42, 179). The E2E now
  asserts INJECT writes the data key + sends `StepCompleted` and **deletes nothing**.

### Build gate
- **D-10:** Solution builds **0-warning in both Release and Debug** after the change
  (KINJ-02 success criterion). The full `BaseApi.Tests` suite stays green.

### Claude's Discretion
- Exact filename/namespace of the new dedicated invariant fact (D-05).
- Exact wording of the rewritten XML doc comments.
- How to express the surviving write-then-send order in `InjectConsumerFacts` now that the
  third op is gone (e.g. a `Received.InOrder` of `StringSetAsync` alone + the captured-send
  belt, or a write-Received check before asserting the single send).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Recovery state machine (the normative contract)
- `docs/design/processor-keeper-recovery-spec.md` §8 — Keeper states table: INJECT =
  "write index slot + data, then send the completed result"; DELETE = the only state that
  deletes (both `L2[messageId]` and `L2[inputEntryId]`). Confirms INJECT must not delete.
- `docs/design/processor-keeper-recovery-spec.md` §4.3 — Forward Post-Process: when an item
  escalates to the keeper, the processor **skips its cleanup tail** (Phase 69 already aligned
  this), which is why the INJECT source-delete is now redundant.

### Phase definition & requirements
- `.planning/ROADMAP.md` → "#### Phase 70: Processor INJECT Cleanup" — goal + 4 success
  criteria (scopes INJECT to exactly two effects).
- `.planning/REQUIREMENTS.md` → KINJ-01, KINJ-02, KINJ-03 (lines 17-19, 49-51).

### Touch-point files (read current state before editing)
- `src/Keeper/Recovery/InjectConsumer.cs` — op to remove at line 40.
- `src/Keeper/Recovery/ReinjectConsumer.cs` — already non-destructive (guard reference).
- `src/Keeper/Recovery/DeleteConsumer.cs` — the only state that deletes (positive half of D-05).
- `src/Messaging.Contracts/KeeperInject.cs` — field to drop at line 14.
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — `BuildInject` at lines 430-438.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `RecoveryTestKit` (`Db()`, `Mux()`, `Retry()`, `CapturingSendProvider`) — the substitute
  harness already used by `InjectConsumerFacts`; reuse for the new dedicated invariant fact.
- NSubstitute `Received` / `DidNotReceive` behavioral assertions — established in
  `InjectConsumerFacts` and the other `*ConsumerFacts`.
- Reflection shape tests in `KeeperContractTests` — established pattern for locking record
  id-sets (positive + `Assert.Null` negative).

### Established Patterns
- Each keeper consumer wraps every L2/bus op in `RecoveryConsumerBase.Guard` (bounded retry).
  Removing op 3 must not disturb the Guarded structure of ops 1-2.
- `KeeperInject` and siblings are default-STJ bus envelopes (no `[JsonPropertyName]`) — STJ
  ignores unknown fields on deserialize, so dropping a property is wire-tolerant.

### Integration Points
- Source edits: `InjectConsumer.cs:39-40`, `KeeperInject.cs:14`, `ProcessorPipeline.cs:437`.
- Test edits: `InjectConsumerFacts.cs`, `KeeperContractTests.cs:67-81`,
  `PipelineForwardFacts.cs:149`, `SC2RecoveryPathsE2ETests.cs:42/179/204`.
- New file: dedicated `KeeperDelete`-invariant fact (D-05).

</code_context>

<specifics>
## Specific Ideas

- The dedicated invariant fact should read as the literal statement of the rule: DELETE
  deletes; INJECT and REINJECT do not. It is the machine-side proof of the KINJ-03 invariant.
- Behavioral over reflective: the suite already proves intent by *running* the consumer
  against a substitute and asserting on the calls it made — keep that idiom.

</specifics>

<deferred>
## Deferred Ideas

- **INJECT index-slot write (spec §8 divergence).** Canonical spec §8 says INJECT also writes
  the index slot `L2[messageId][x]=outputEntryId` atomically with the data. The current
  `InjectConsumer` writes only the data key, and the ROADMAP scopes Phase 70 to "exactly two
  effects" (write data + send). Closing this divergence is **not** Phase 70 — note it as an
  observed gap for a future phase, do not implement it here.
- **`L2ProbeRecovery.cs:35` scratch delete.** This is a net-zero probe scratch
  `KeyDeleteAsync`, not a keeper recovery state. It is explicitly **outside** the
  "DELETE-only-deletes" invariant and must not be touched or asserted against by the guard.

</deferred>

---

*Phase: 70-processor-inject-cleanup*
*Context gathered: 2026-06-16*
