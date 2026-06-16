# Phase 71: Orchestrator Recovery Pipeline - Context

**Gathered:** 2026-06-16
**Status:** Ready for planning

<domain>
## Phase Boundary

Give the orchestrator's **result-consume path** the same `messageId`-indexed
forward/recovery/keeper pipeline the processor already has (canonical pattern
`ProcessorPipeline.cs` + spec §3–§8), **reversing Phase 24.1's L1-only
`TypedResultConsumer`** by re-introducing L2 to the result path. Plus the keeper-contract
**split-by-origin**: rename `KeeperInject`/`KeeperReinject` → `ProcessorInject`/`ProcessorReinject`,
add `OrchestratorInject`/`OrchestratorReinject`, keep `KeeperDelete` shared; both new consumers
bind on the **existing** `keeper-recovery` endpoint (no new queue). The orchestrator-side
delete invariant holds: only the cleanup tail (and out-of-band `KeeperDelete`) deletes keys.

**Governing principle (user, locked):** **"Consistent with processor."** Every decision below
mirrors the shipped processor pipeline's shape, idioms, and resilience posture. Divergences exist
ONLY where the orchestrator's domain genuinely differs (heterogeneous slots; copy-an-existing-key
vs write-in-hand-data; reconstruct an `IStepResult` subtype vs an `EntryStepDispatch`).

**Out of scope:** new queues/endpoints; a status discriminator `switch` (routing stays by message
type / outcome knob); any change to the processor's runtime behavior beyond the mechanical rename.

</domain>

<decisions>
## Implementation Decisions

### D-01 — Pipeline organization: MIRROR + ADAPT (ORCV-01, ORCV-02, ORCV-04, ORCV-05)
A **new** `OrchestratorResultPipeline` class mirrors `ProcessorPipeline`'s
gate → forward/recovery → cleanup-tail skeleton rather than extracting a shared base.
Rationale: lowest coupling, zero risk to the shipped processor path; the two pipelines diverge
enough (heterogeneous slots, copy-not-write, `IStepResult` reconstruction) that a shared base would
over-abstract. Reuse the processor's building blocks verbatim where identical: `RetryLoop`, the
`exist L2[messageId]` gate-once, the gated atomic two-key cleanup tail (the `DeleteTerminalAsync`
analog), `SendKeeper`/`SendResult` send-owners (exhaust → throw → broker redelivery, no `_error`).
- **Gate (ORCV-01):** `exist L2[messageId]` once (messageId = inbound result's broker message id).
  absent → FORWARD; present → RECOVERY; gate L2-op exhaustion → `OrchestratorReinject`, end round
  trip, **no cleanup** (mirrors `ProcessorPipeline` PRE-read exhaust → REINJECT-and-return).
- Lives orchestrator-side (new file under `src/Orchestrator/...`; exact namespace Claude's discretion).

### D-02 — Heterogeneous slot encoding: JSON object per slot (ORCV-03)
Same `L2ProjectionKeys.MessageIndex(messageId)` HASH-of-`int slot → value` structure as the
processor (consistent). The processor stores a bare `entryId` Guid string because its slots are
**homogeneous**; orchestrator slots are **heterogeneous**, so the HASH field **value** is a small
**JSON object** `{ nextStepId, nextProcessorId, payload, newEntryId }`. `payload` is already a JSON
string. RECOVERY parses the JSON to reconstruct the per-slot `EntryStepDispatch`. Same structure,
richer value — a consistent extension, not a divergence.

### D-03 — Atomic FORWARD op: ONE Lua script (ORCV-02)
Mirror the processor's `AtomicForwardWrite` single-script pattern. FORWARD, per next step, runs ONE
server-side Lua op over `KEYS = { MessageIndex(messageId), ExecutionData(newEntryId) }`:
**`HSET` the index slot** = the D-02 JSON tuple, **whole-hash `PEXPIRE`** (index TTL), and
**copy `L2[origin entryId] → L2[newEntryId]`** with the **data TTL**. TTLs are computed in C# and
passed as `ARGV` (NO RNG inside Lua — preserves the Phase-68 TEST-06 index/data-desync guard).
Exhaust (transient infra OR deterministic script error) → a single `OrchestratorInject` (NO silent
drop — spec §10 bullet 1). After the write: send `EntryStepDispatch` to `queue:{nextProcessorId}`,
then retire the slot to `guid.empty` (ORCV-04).
- **Copy semantics (Claude's discretion, inside the one script):** Redis `COPY src dst REPLACE`
  then `PEXPIRE dst <dataTtl>` (COPY does not carry TTL), or `GET`+`SET dst PX <dataTtl>`. Either is
  acceptable as long as it is ONE atomic script and the dest carries the data TTL.

### D-04 — Cleanup tail + delete invariant (ORCV-04, ORCV-07)
Gated atomic two-key `DEL` of `L2[messageId]` + `L2[origin entryId]`, run **only if no slot escalated
to the keeper this pass** (forward) / at the end of a recovery pass — mirrors
`ProcessorPipeline.DeleteTerminalAsync` + the GATE-01 skip. Delete exhaustion → out-of-band
`KeeperDelete` (best-effort `PERSIST` index then escalate). **`OrchestratorInject` and
`OrchestratorReinject` NEVER delete a key.** `KeeperDelete` stays the ONLY deleting keeper state,
now shared across both origins.

### D-05 — RECOVERY (ORCV-05)
Mirror `ProcessorPipeline.RunRecoveryAsync`: read the index slots; per slot a 3-way classification —
**data exists → re-send** the reconstructed dispatch; **clean not-exist → drop, no retire**;
**L2 fault → leave the slot intact**. Tail `OrchestratorReinject`s if any slot faulted, else runs
the D-04 two-key delete. A redelivery re-sends the **stable persisted entryIds** and skips retired
(`guid.empty`) slots.

### D-06 — Keeper contract split by origin, RENAME contracts + consumers, dedicated first plan (ORCV-06)
Route-by-type, **no discriminator switch** (MassTransit dispatches by message type).
- **Rename (a dedicated FIRST plan/wave, isolated diff, before any `Orchestrator*` is added):**
  - Contracts: `KeeperInject` → `ProcessorInject`, `KeeperReinject` → `ProcessorReinject`
    (file + type). `KeeperDelete` stays.
  - Consumers (for symmetry with the new `OrchestratorInjectConsumer`/`OrchestratorReinjectConsumer`):
    `InjectConsumer` → `ProcessorInjectConsumer`, `ReinjectConsumer` → `ProcessorReinjectConsumer`.
    `DeleteConsumer` stays.
  - Update ALL ~25 `.cs` reference sites: `ProcessorPipeline.cs` (incl. `BuildInject`/`BuildReinject`
    builder names follow the rename), `KeyAbsentException.cs`, `RecoveryEndpointBinder.cs`,
    `RecoveryTestKit.cs`, and every test (`KeeperContractTests`, `KeeperDeleteInvariantFacts`,
    `InjectConsumerFacts`, `ReinjectConsumerFacts`, `RecoveryPartitionFacts`, `RecoveryDeadLetterFacts`,
    the `Processor/Pipeline*Facts`, `SC2RecoveryPathsE2ETests`, analyzer/observability refs, etc.).
- **Then add `Orchestrator*`** (later wave): `OrchestratorInject`, `OrchestratorReinject` contracts
  (both implement `IKeeperRecoverable`), `OrchestratorInjectConsumer`, `OrchestratorReinjectConsumer`
  (extend the same `RecoveryConsumerBase<T>` Guard base).

### D-07 — OrchestratorReinject contract shape: outcome enum + union fields (ORCV-06)
Consistent with how `ProcessorReinject` (née `KeeperReinject`) carries discrete fields (`Payload`)
and the consumer **reconstructs** the strongly-typed message — NOT a serialized polymorphic blob.
`OrchestratorReinject` carries the base 5-id + `EntryId` + a **`StepOutcome` discriminator** + the
result-field superset (`ErrorMessage`, `CancellationMessage`). A factory maps outcome → the right
`IStepResult` subtype (`StepCompleted` / `StepFailed` + `ErrorMessage` / `StepCancelled` +
`CancellationMessage` / `StepProcessing`) and re-injects to `queue:orchestrator-result`. No status
`if`/`switch` beyond the factory — reuse the existing `StepOutcome`/`StepAdvancement` knob idiom.
(Whether to reuse `StepOutcome` directly or mint a parallel discriminator: Claude's discretion.)
`OrchestratorInject` completes the index+data copy and sends `EntryStepDispatch` downstream.

### D-08 — Bind the two new consumers on the existing endpoint (ORCV-06)
Add to `RecoveryEndpointBinder`'s `ConnectReceiveEndpoint` callback: `UsePartitioner<OrchestratorInject>`
and `UsePartitioner<OrchestratorReinject>` on the **same** `Partitioner` instance with the **same**
`ReinjectConsumerDefinition.PartitionGuid` 4-tuple key selector, plus `ConfigureConsumer` for both new
consumers. Register them in Keeper `Program.cs` with `ExcludeFromConfigureEndpoints()`. **No new queue,
same health gate / partitioner / exhaustion posture.**

### D-09 — Negative-guard facts, behavioral (ORCV-07)
Consistent with Phase 70's `KeeperDeleteInvariantFacts`: prove `OrchestratorInject` /
`OrchestratorReinject` never delete via **behavioral NSubstitute `DidNotReceive()` on BOTH
`KeyDeleteAsync` overloads**, each co-asserted with a positive side-effect (so a no-op can't pass).
(Extend the existing invariant fact vs a parallel `Orchestrator*` fact: Claude's discretion — keep it
consistent with the processor fact.)

### D-10 — Fold in the Phase-70 code-review WR-01 fix
The rename touches `RecoveryTestKit.cs` anyway. While there, add the missing **5-arg
`StringSetAsync(RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags)` stub** to
`RecoveryTestKit.Db()` so the kit stubs the overload the consumers actually bind to (SE.Redis 2.13.1).
See `70-REVIEW.md` WR-01.

### Claude's Discretion
- Exact namespaces/filenames/class names for the new pipeline, contracts, consumers, facts.
- COPY-vs-GET/SET inside the single atomic Lua script (D-03).
- Reuse `StepOutcome` vs a parallel discriminator for `OrchestratorReinject` (D-07).
- Exact JSON property names of the slot tuple (D-02).
- Extend vs duplicate the delete-invariant fact (D-09).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### The normative spec
- `docs/design/processor-keeper-recovery-spec.md` §3 (gate-once), §4 (forward + atomic write + gated
  cleanup), §5 (recovery 3-way per-slot), §6 (atomic two-key cleanup tail), §7 (resilience: in-code
  retry only, no broker retry / no DLQ), §8 (keeper states; DELETE is the only deleter).

### The canonical pattern to MIRROR
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — the gate, the `AtomicForwardWrite` Lua
  constant, `RunForward`/`RunRecovery`, `DeleteTerminalAsync` (gated two-key DEL + persist-then-DELETE),
  `SendKeeper`/`SendResult`, the builders. This is the structural template for `OrchestratorResultPipeline`.

### The path being reversed + the bind point
- `src/Orchestrator/Consumers/TypedResultConsumer.cs` — the L1-only straight-through result advancer
  (24.1) being reversed; its `StepAdvancement.SelectNext(Outcome, …)` DAG-advancement is still the
  source of "next steps" that FORWARD iterates. **Integration question for research:** how the new
  L2-gated pipeline composes with the existing per-type result consumers (`StepCompletedConsumer` etc.)
  and `StepAdvancement`.
- `src/Keeper/Recovery/RecoveryEndpointBinder.cs` — where the two new consumers bind (partitioner +
  `ConfigureConsumer`).

### Contracts + keys to rename / mirror
- `src/Keeper/Recovery/{InjectConsumer,ReinjectConsumer,DeleteConsumer}.cs` + `RecoveryConsumerBase<T>`.
- `src/Keeper/Recovery/ReinjectConsumerDefinition.cs` — `PartitionKey`/`PartitionGuid` (4-tuple, works
  for any `IKeeperRecoverable`; the `Orchestrator*` contracts implement the same marker).
- `src/Messaging.Contracts/{KeeperInject,KeeperReinject,KeeperDelete}.cs` and the four `IStepResult`
  subtypes `{StepCompleted,StepFailed,StepCancelled,StepProcessing}.cs` (for the D-07 reinject factory).
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `MessageIndex` (the slot HASH),
  `ExecutionData` (the data key).

### Phase 70 lineage
- `.planning/phases/70-processor-inject-cleanup/70-CONTEXT.md` — the non-destructive-INJECT +
  reduced (`DeleteEntryId`-free) contract shape the `Orchestrator*` contracts mirror.
- `.planning/phases/70-processor-inject-cleanup/70-REVIEW.md` — WR-01 (the `RecoveryTestKit` stub gap, D-10).

### Phase definition
- `.planning/ROADMAP.md` → "#### Phase 71: Orchestrator Recovery Pipeline" (goal + 5 success criteria).
- `.planning/REQUIREMENTS.md` → ORCV-01..ORCV-07 (lines 26–32, 55–61).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets (consistent-with-processor reuse)
- `ProcessorPipeline` skeleton + `RetryLoop` + `AtomicForwardWrite` Lua + `DeleteTerminalAsync` +
  `SendKeeper`/`SendResult` + builders — the template for `OrchestratorResultPipeline`.
- `RecoveryConsumerBase<TMessage>` — the `Guard` (bounded-retry) base every recovery consumer extends;
  the two new `Orchestrator*` consumers extend it too.
- `RecoveryEndpointBinder` partitioner + `ConfigureConsumer` pattern; `ReinjectConsumerDefinition.PartitionGuid`
  (the shared 4-tuple key selector — origin-agnostic, works for `Orchestrator*`).
- `StepAdvancement`/`StepOutcome` — the existing route-by-outcome knob (no status switch); reuse for
  the D-07 `OrchestratorReinject` factory and for FORWARD's "next steps" selection.
- `RecoveryTestKit` substitute harness (fix WR-01 here, D-10).

### Established Patterns
- Every L2/bus op wrapped in `RetryLoop`/`Guard`; exhaust → keeper escalate OR throw-for-redelivery.
- No broker retry, no `_error`/DLQ on the execution path (symmetric endpoints).
- Single Lua script for atomic multi-key writes; TTLs computed in C#, passed as ARGV.
- Behavioral NSubstitute facts (`DidNotReceive` for negative guards; both `KeyDeleteAsync` overloads).

### Integration Points
- New `OrchestratorResultPipeline` (new file, `src/Orchestrator/...`); invoked from the result-consume path.
- `RecoveryEndpointBinder.cs` (+2 `UsePartitioner` / +2 `ConfigureConsumer`); Keeper `Program.cs` (+2 consumers).
- ~25 `.cs` rename sites (D-06).

</code_context>

<specifics>
## Specific Ideas

- "Consistent with processor" is the through-line: when in doubt, do what `ProcessorPipeline` /
  `ProcessorReinject` / `KeeperDeleteInvariantFacts` do. Divergence only for the three genuine domain
  differences (heterogeneous slots → JSON tuple; copy-existing-key → COPY/SET in the atomic script;
  reconstruct `IStepResult` subtype → outcome-enum factory).
- Sequence the rename as an isolated first plan so its large mechanical diff reviews cleanly before
  any new `Orchestrator*` code lands.

</specifics>

<deferred>
## Deferred Ideas

- The processor's INJECT index-slot-write divergence from spec §8 (deferred in Phase 70) remains
  deferred — not reopened here.
- A stricter keeper-recovery endpoint startup posture (connect-stopped) noted in `RecoveryEndpointBinder`
  remains a future option, untouched.

</deferred>

---

*Phase: 71-orchestrator-recovery-pipeline*
*Context gathered: 2026-06-16*
