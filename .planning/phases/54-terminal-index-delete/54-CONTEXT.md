# Phase 54: Terminal Index Delete + Atomic Keeper GC - Context

**Gathered:** 2026-06-12
**Status:** Ready for planning

<domain>
## Phase Boundary

The processor's end-of-message tail actively reclaims the `L2[messageId]` allocation index (design-doc amendment **A19**). The no-`REINJECT` happy-path tails (forward + recovery all-clear) delete BOTH the source `L2[entryId]` and the `L2[messageId]` index in one atomic Redis multi-key `DEL`. A terminal-delete exhaustion `PERSIST`s the index (cancels its random TTL) then escalates to a keeper `DELETE{messageId, entryId}` that deletes both keys atomically — replacing A18's passive TTL-only index reclaim.

Hermetic-only this phase. The live/real-stack proof + close-gate net-zero is **Phase 55** (TEST-01/02; build-before-proof split).

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**3 requirements are locked** (GC-01, GC-02, GC-03). See `54-SPEC.md` for full requirements, boundaries, and acceptance criteria. **Downstream agents MUST read `54-SPEC.md` before planning or implementing.** Requirements are not duplicated here.

**In scope (from SPEC.md):**
- Unify the forward + recovery happy-path tails into one atomic two-key `DEL` (`ExecutionData` + `MessageIndex`) via the existing `RetryLoop`.
- Persist-on-escalate: `KeyPersist(MessageIndex(messageId))` before the keeper handoff on terminal-delete exhaustion.
- `KeeperDelete` contract gains `MessageId`; `BuildDelete` stamps it from the inbound message id.
- `DeleteConsumer` deletes both keys atomically (single multi-key `DEL`), drop-on-absent.
- Hermetic facts for all of the above, including the source-step (`Guid.Empty`) index-only edge.
- Solution builds 0-warning Release + Debug; full hermetic suite green.

**Out of scope (from SPEC.md):**
- Real-stack / live E2E proof + close-gate triple-SHA net-zero — that is **Phase 55** (TEST-01/02).
- New metrics / counters for the GC path — deferred (no instrumentation this phase).
- Per-slot random-TTL writes (`ProcessorPipeline.cs:165`/`:275`) — **unchanged** (crash-before-terminal-delete backstop, A19).
- `REINJECT` / `INJECT` consumer bodies, forward-Post allocation, recovery slot-classification — **unchanged**.
- Eliminating the source-step crash-before-ack duplicate window — **accepted** under A16 (at-least-once / no-dedup); must NOT be "fixed" with a dedup key.
- Redis-cluster multi-slot atomic delete — N/A (single-instance `sk-redis`).

</spec_lock>

<decisions>
## Implementation Decisions

### Tail structure
- **D-01:** Extract ONE shared private method `DeleteTerminalAsync(EntryStepDispatch d, Guid messageId, IDatabase db, int limit, CancellationToken ct)` on `ProcessorPipeline`. Both the forward happy-path tail (replacing the `DeleteSourceTail()` local fn at `ProcessorPipeline.cs:195-201`, called at `:228`/`:247`/`:253`/`:309`) and the recovery all-clear tail (replacing the inline delete at `:180-185`) call it. The atomic two-key `DEL` + persist-on-escalate + `KeeperDelete` handoff lives in ONE place. Matches the SPEC's "unify the tails" language; least duplication.
  - *Rejected:* two independent inline sites each repeating the two-key delete + persist-escalate (more duplication, divergence risk).

### Key-array shape
- **D-02:** Build the RedisKey array INLINE at the call site: `db.KeyDeleteAsync(new RedisKey[]{ L2ProjectionKeys.ExecutionData(d.EntryId), L2ProjectionKeys.MessageIndex(messageId) })`, wrapped in the existing `RetryLoop`. `L2ProjectionKeys` stays returning pure `string` shapes — its class doc already declares key *usage* (TTL, etc.) a "caller concern", and the array is a StackExchange.Redis-call-local concept. No new array-returning member on the projection-keys class.
  - *Rejected:* a `L2ProjectionKeys.TerminalKeys(entryId, messageId)` helper returning `RedisKey[]` (would be the first array member; over-centralizes a call-local concern).

### Persist-on-escalate failure handling
- **D-03:** On a terminal-delete exhaustion: `RetryLoop`-wrap `db.KeyPersistAsync(MessageIndex(messageId))`, then `SendKeeper(BuildDelete(d, messageId))` — **best-effort persist**. If the `KeyPersistAsync` itself exhausts (Redis still faulting), proceed to send the `KeeperDelete` regardless. The keeper's atomic both-key `DEL` is the real GC; the index retains its existing random-TTL backstop, so a persist failure must never block the keeper handoff.
  - *Rejected:* throwing on persist-exhaust (broker redelivery → replay re-walks recovery rather than reaching the keeper; stricter but slower self-heal and abandons the in-hand keeper escalation).

### Fact placement
- **D-04:** Edit hermetic facts IN PLACE, per behavior area — do NOT create a new grouped A19 fact file.
  - `PipelineEndDeleteFacts.cs` (forward tail): **invert** `EndDelete_RunsOnHappyPath` (single-key → ONE multi-key `KeyDeleteAsync(RedisKey[]{ExecutionData, MessageIndex})`) and **invert** `EndDelete_Skipped_OnSourceStep` (source step now DELETES the index — assert the multi-key `DEL` runs and the `Guid.Empty` data operand no-ops without throw). Keep `EndDelete_RunsOnBusinessFail` / `EndDelete_RunsOnInException` / `EndDelete_Skipped_OnReinject` / `EndDelete_Exhaust_Delete`, updating their delete assertions to the multi-key shape; add a persist-on-escalate assertion (`KeyPersistAsync(MessageIndex)` called then `KeeperDelete` carrying `MessageId`).
  - `PipelineRecoveryFacts.cs` (recovery all-clear tail + REINJECT exclusion): add a fact that the all-clear tail issues the same single multi-key `DEL`, and a GC-02 fact that an `anyInfra` recovery path sends `REINJECT`, issues NO source/index delete, and the `MessageIndex` key survives.
  - `DeleteConsumerFacts.cs` (keeper): assert `DeleteConsumer` issues ONE multi-key `KeyDeleteAsync([ExecutionData(entryId), MessageIndex(messageId)])`, drop-on-absent on either operand.

### Carried-forward locks (SPEC / convention — captured, not re-decided)
- **D-05:** `KeeperDelete` gains `MessageId` as an `init` property (mirrors the existing `EntryId` init-property pattern in `KeeperDelete.cs:11`; the 3 base ids stay positional ctor params). `BuildDelete(d, messageId)` populates it from the inbound `messageId`.
- **D-06:** The source-step guard inverts: the current `if (SourceStep.IsSource(d.EntryId)) return;` early-skip in the tail is removed — for a source step the index `DEL` still runs; the `ExecutionData(Guid.Empty)` operand is a harmless absent no-op (DEL is effectively index-only).
- **D-07:** Per-slot random-TTL writes (`ProcessorPipeline.cs:165`/`:275`) remain present, unchanged — the crash-before-terminal-delete backstop.
- **D-08:** No new metric series. The `UseMessageRetry = none` Phase-53 end-state is preserved (send-exhaustion still throws → broker redelivery; no `_error` routing).

### Claude's Discretion
- The NSubstitute test-kit mock shape for the multi-key `KeyDeleteAsync(RedisKey[], CommandFlags)` overload (extend `DispatchTestKit.PresentReadWriteDeleteOkL2` / `ReadOkDeleteFaultL2` to mock the array overload alongside the single-key one) — mechanical, planner/executor concern.
- Exact internal control flow of `DeleteTerminalAsync` (parameter ordering, how the source-step no-op is expressed) provided it satisfies D-01/D-02/D-03/D-06.

### Folded Todos
None — no pending todos matched this phase.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase requirements (locked)
- `.planning/phases/54-terminal-index-delete/54-SPEC.md` — Locked requirements GC-01/02/03, boundaries, constraints, 10 acceptance criteria. **MUST read before planning.**

### Design source of truth
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` → "Active Index GC (A19)" section (LOCKED 2026-06-12) — the active terminal index delete + atomic both-key keeper GC design. Also "Recovery Re-architecture (A18)" for the surrounding slot-array / 3-state-keeper context this amends.

### Requirements register
- `.planning/REQUIREMENTS.md` — GC-01/02/03 (and the milestone's 24 reqs).

### Implementation targets (existing code)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — forward tail `DeleteSourceTail()` (`:195-201`, reached `:228`/`:247`/`:253`/`:309`); recovery all-clear tail (`:180-185`); per-slot TTL writes (`:165`/`:275`); `BuildDelete` (`:367-368`); `SendKeeper` (`:340`).
- `src/Messaging.Contracts/KeeperDelete.cs` — contract gaining `MessageId`.
- `src/Keeper/Recovery/DeleteConsumer.cs` — `HandleAsync` (`:19-20`) becoming the both-key atomic delete.
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `ExecutionData` (`:42`), `MessageIndex` (`:48`); stays string-only (D-02).

### Hermetic facts (edit in place)
- `tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs` — forward-tail facts (two invert under A19).
- `tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs` — recovery-tail + REINJECT-exclusion facts.
- `tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs` — both-key keeper DELETE fact.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `RetryLoop.ExecuteAsync(op, limit, ct)` (`BaseConsole.Core.Resilience`) — wraps every L2 op; the `KeyPersist` and the multi-key `DEL` reuse it directly. Returns `RetryOutcome` with `.Succeeded` / `.Value` / `.Error`.
- `SendKeeper(IKeeperRecoverable, limit, ct)` (`ProcessorPipeline.cs:340`) — existing keeper-recovery send owner; `BuildDelete` already routes through it.
- `RecoveryConsumerBase<KeeperDelete>.Guard(...)` (base of `DeleteConsumer`) — wraps the keeper-side delete in its own RetryLoop + drop-on-absent + DLQ-1 escalation; the both-key `DEL` slots straight in.
- `SourceStep.IsSource(entryId)` — the `Guid.Empty` source-step predicate; reused to express the index-only no-op (D-06).

### Established Patterns
- Single-instance `sk-redis`: a multi-key `DEL key1 key2` is atomic by Redis single-threaded execution — no cluster hash-slot concern (out of scope).
- `KeeperDelete` contract: 3 base ids positional ctor params, extras as `init` properties (drives D-05).
- `L2ProjectionKeys` returns pure `string` shapes; usage/TTL is explicitly a "caller concern" (drives D-02 inline array).
- Allocation-before-data ordering and the `RetryLoop`-wrap-everything discipline are unchanged.

### Integration Points
- The new `DeleteTerminalAsync` is the single tail both `RunForwardAsync` and `RunRecoveryAsync` converge on.
- `DeleteConsumer.HandleAsync` is the keeper-side terminus of the persist-on-escalate path.
- The NSubstitute `DispatchTestKit` Redis fakes must gain the `KeyDeleteAsync(RedisKey[], ...)` overload mock (Claude's discretion).

</code_context>

<specifics>
## Specific Ideas

- Atomicity is specifically "ONE `DEL key1 key2` command" — facts must assert a single multi-key `KeyDeleteAsync(RedisKey[]{...})`, NOT two single-key `KeyDeleteAsync(RedisKey)` calls. This distinction is the heart of GC-01's acceptance.
- Best-effort persist (D-03): the random TTL is deliberately retained as the A19 crash backstop, which is exactly why a persist failure can safely fall through to the keeper handoff.

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope. (Live proof + close-gate net-zero is the already-planned Phase 55, not a deferral.)

</deferred>

---

*Phase: 54-terminal-index-delete*
*Context gathered: 2026-06-12*
