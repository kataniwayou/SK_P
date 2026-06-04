---
phase: 31-idempotent-execution-exactly-once-effect
plan: 03
subsystem: processor
tags: [effect-first-dedup, content-addressing, manifest, exactly-once-effect, redis-cas, processor-receiver]

# Dependency graph
requires:
  - phase: 31-idempotent-execution-exactly-once-effect
    plan: 01
    provides: "MessageIdentity (ComputeH/HashBlob/HashManifest), L2ProjectionKeys.ExecutionData(string)/Flag(string)"
  - phase: 31-idempotent-execution-exactly-once-effect
    plan: 02
    provides: "string EntryId + string H on EntryStepDispatch/ExecutionResult; the IsNullOrEmpty source-guard + ToString shim this plan replaces"
provides:
  - "EntryStepDispatchConsumer effect-first drop-on-Ack dedup gate keyed on flag[dispatch.H]"
  - "Content-addressed two-level write: data[HashBlob(blob)] blobs + data[HashManifest(manifest)] manifest"
  - "ONE manifest ExecutionResult per dispatch (EntryId = HashManifest, H = ComputeH over manifestEntryId); empty result sends terminal '[]'"
  - "Outbound flag[resultH]='Pending' sender pre-write (D-06) seeding the orchestrator-hop dedup (Plan 04 flips it)"
  - "Inbound flag[dispatch.H] Pending->Ack flip via StringSet When.Exists AFTER the send"
  - "Source-step input-skip keyed on InputDefinition==null (empty EntryId only short-circuits the L2 read)"
affects: [31-04, 31-06]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Effect-first dedup (D-06): read-drop-on-Ack at consume top; flip Pending->Ack only AFTER the effect (write+send)"
    - "Content-addressed idempotent overwrite: a retry reproduces identical data[hash] keys + identical result H"
    - "Manifest collapse: N result blobs -> ONE deterministic manifest result identity (D-08)"
    - "Sender pre-write (D-06): the producer of a message seeds flag[H_child]='Pending' so the consumer's When.Exists Ack flip has a key to flip"
    - "When.Exists false-return is the DESIGNED residual (Pitfall 3) — never thrown on"

key-files:
  created:
    - tests/BaseApi.Tests/Processor/EffectFirstDedupFacts.cs
  modified:
    - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
    - tests/BaseApi.Tests/Processor/DispatchInputFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchInvokeFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchResultSendFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchCorrelationFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchOutputWriteFacts.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs

key-decisions:
  - "BuildCompleted receives the already-computed resultH so the outbound flag[resultH]='Pending' pre-write and the message carry the IDENTICAL H — one ComputeH call, no drift"
  - "An output-schema validation failure on ANY blob is a WHOLE-DISPATCH business Failed (D-09/Open Q1): send BuildFailed(EntryId='',H=''), return, no Pending pre-write — faithfully preserving today's per-result BuildFailed short-circuit"
  - "The per-blob nested log scope now carries the CONTENT-ADDRESSED blob hash (EntryId key) + an independent per-blob lineage ExecutionId; the single manifest result mints its OWN lineage ExecutionId (N->1 collapse breaks the old scope==result-id equality by design)"
  - "Realigned 7 pre-existing consumer facts (Rule 1) to the manifest-collapse + dedup-gate behavior — the wave's full-hermetic-suite-green verification mandates it"

requirements-completed: [req-3, req-4]

# Metrics
duration: 29min
completed: 2026-06-04
---

# Phase 31 Plan 03: Effect-First Content-Addressed Processor Receiver Summary

**`EntryStepDispatchConsumer` reworked into the exactly-once-effect producer half: a drop-on-Ack dedup gate on `flag[dispatch.H]`, content-addressed two-level L2 write (`data[hash(blob)]` blobs + `data[hash(manifest)]` manifest), ONE manifest `ExecutionResult` per dispatch carrying `H = ComputeH(manifestEntryId)`, an outbound `flag[resultH]="Pending"` sender pre-write seeding the orchestrator hop, and an effect-first `Pending->Ack` `When.Exists` flip AFTER the send — proven by `EffectFirstDedupFacts` (req-3/req-4) with the full hermetic suite green at 434/0.**

## Performance

- **Duration:** ~29 min
- **Started:** 2026-06-04T13:23:43Z
- **Completed:** 2026-06-04T13:52:17Z
- **Tasks:** 2
- **Files modified:** 9 (1 created, 8 modified)

## Accomplishments
- **Effect-first dedup gate (D-06):** at the top of `Consume` (after `GetDatabase`), `flag[dispatch.H] == "Ack"` -> immediate `return` (drop + broker-ack), producing NO effect. The flag read is INFRA (no catch) -> Immediate(3) retry. `H=""` (legacy/unset) never matches `"Ack"`.
- **Source-step input-skip rekeyed (req-2 / D-01):** removed the `if (string.IsNullOrEmpty(dispatch.EntryId)) { ... } else { ... }` branch. Replaced with a single path — an empty `EntryId` short-circuits only the L2 DATA read via an inline ternary (`RedisValue.Null`); the source vs required-input decision keys on `InputDefinition == null` (absent L2 + required input = business Failed; absent L2 + no definition = empty input).
- **Content-addressed two-level write (D-03/D-09):** each output-validated blob is written at `ExecutionData(MessageIdentity.HashBlob(blob))` (idempotent overwrite — same blob, same key); the blob hashes collect into a `manifestHashes` list. Output-schema validation still runs on each DATA blob (never the manifest, D-09).
- **Manifest + outbound Pending pre-write + ONE send (D-06/D-08, Pitfall 4):** after the loop, serialize the manifest JSON array, write it at `ExecutionData(HashManifest(manifest))`, compute `resultH = ComputeH(corr, wf, step, proc, manifestEntryId)`, pre-write `flag[resultH]="Pending"` (the SENDER pre-write — without it the orchestrator's `When.Exists` Ack flip is a no-op and the orchestrator hop is dead), then send ONE `ExecutionResult` with `EntryId = manifestEntryId` AND `H = resultH`. An EMPTY result still sends a terminal `"[]"` manifest result so the orchestrator observes-and-terminates and acks (req-3).
- **Effect-first CAS flip then ack (D-06/D-07):** after the send, flip `flag[dispatch.H]` Pending->Ack via `StringSet(..., When.Exists)`. A false return (Pending lost = crash residual) is NOT thrown on (Pitfall 3). The outbound `resultH` is NOT flipped to Ack here — the orchestrator owns that on its hop (Plan 04).
- **`EffectFirstDedupFacts` (req-3/req-4, 5 hermetic facts):** drop-on-Ack produces no effect (no data write, no outbound Pending, no Send); effect-then-CAS happens in order (blob write -> outbound `flag[resultH]="Pending"` -> Ack flip); the outbound Pending pre-write precedes the Send (orchestrator-hop seed regression guard); the inbound flip uses `When.Exists`; and a crash-window redelivery re-produces the SAME content-addressed data key + SAME result H (a collapsed downstream DUPLICATE, never a LOSS).

## Task Commits

Each task was committed atomically:

1. **Task 1: consumer rework (dedup gate + content-addressed write + manifest + outbound Pending pre-write)** — `73c0cab` (feat)
2. **Task 2: EffectFirstDedupFacts + realign 7 pre-existing consumer facts** — `b008995` (test)

**Plan metadata:** committed separately (docs).

## Decisions Made
- **One `ComputeH`, no drift:** `BuildCompleted(d, executionId, manifestEntryId, resultH)` takes the already-computed `resultH` so the outbound `flag[resultH]="Pending"` pre-write and the sent message carry the IDENTICAL `H`.
- **Whole-dispatch Failed on any invalid blob (D-09 / Open Q1):** an output-schema validation failure short-circuits to `SendResult(BuildFailed(EntryId="", H=""))` + return — no manifest read, no Pending pre-write. This faithfully preserves the prior per-result `BuildFailed` short-circuit (the prior code added a Failed and `continue`d, but the orchestrator short-circuits on outcome before the manifest, so a single whole-dispatch Failed is the simplest equivalent).
- **Scope vs result identity decoupled by the N->1 collapse:** the per-blob nested log scope carries the blob hash (`EntryId` key) + a per-blob lineage `ExecutionId`; the single manifest result mints its own lineage `ExecutionId` and carries the manifest `EntryId`. The old "scope id == sent result id" equality no longer holds — the scope/runtime-scope facts were updated to assert the scope `EntryId == HashBlob(blob)` and a valid independent lineage `ExecutionId`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Realigned 7 pre-existing consumer facts broken by the intended behavior change**
- **Found during:** Task 1/2 (full hermetic suite run after the consumer rework)
- **Issue:** The plan's `files_modified` lists only `EntryStepDispatchConsumer.cs` + `EffectFirstDedupFacts.cs`, but the deliberate behavior change (dedup-gate flag read; N result blobs collapsing into ONE manifest result; empty result now SENDING a terminal `"[]"`; content-addressed `data[hash(blob)]` keys; whole-dispatch Failed on any invalid blob; per-blob scope id no longer equal to the result id) necessarily breaks pre-existing consumer facts that assert the OLD per-result / Guid-key / ack-only-on-empty behavior. The plan's own `<verification>` mandates the full hermetic suite green at wave merge, so leaving them RED is not an option.
- **Fix:** Updated each broken assertion to the new contract (no protocol logic invented — each fact now asserts the Plan 31-03 behavior):
  - `DispatchInputFacts.EmptyDefinition_EmptyEntryId_*`: source step skips only the L2 DATA read (the dedup flag gate still reads) — `DidNotReceive` now scopes to `skp:data:` keys.
  - `DispatchInvokeFacts`: `Mints_Distinct_ExecutionIds_Per_Result` -> `Collapses_Multiple_Results_Into_One_Manifest_Result` (2 blobs -> 1 result whose EntryId = hash of the JSON array of blob hashes; non-empty H).
  - `DispatchResultSendFacts`: `EmptyList_AcksWithNoMessage` -> `EmptyResult_Sends_One_Terminal_Manifest_Result`; `Sends_One_By_One` -> `Multiple_Results_Collapse_Into_One_Manifest_Send`.
  - `DispatchCorrelationFacts`: `Body_CorrelationId_Flows_To_Every_Result` -> `..._To_The_Manifest_Result` (single result).
  - `DispatchOutputWriteFacts` (real Redis): `Pass_*` asserts the blob at `data[hash(blob)]`, the manifest at `data[manifestEntryId]`, and the outbound `flag[resultH]="Pending"` (all `Track`'d for net-zero teardown); `MixedBatch_*` -> `AnyInvalidBlob_FailsWholeDispatch`.
  - `EntryStepDispatchScopeTests` + `EntryStepDispatchRuntimeScopeTests`: the nested-scope `EntryId` asserts `HashBlob("out")` and the scope `ExecutionId` is a valid independent lineage Guid (no longer == the result's id).
- **Files modified:** the 7 test files above.
- **Verification:** full hermetic suite `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack"` = **Passed 434 / Failed 0**; the Processor namespace alone = 56/56.
- **Committed in:** `b008995` (Task 2 commit)

**2. [Rule 3 - Blocking] Replaced the cross-overload `Received.InOrder` in `Effect_Then_AckCas_InOrder` with a captured-call ordering check**
- **Found during:** Task 2 (first run of `EffectFirstDedupFacts`)
- **Issue:** The `expiry:`-named data/manifest/Pending writes and the `when:`-named Ack flip bind DIFFERENT `StringSetAsync` overloads in SE.Redis 2.13.1. NSubstitute's `Received.InOrder` replays the exact overload spec, so a single `InOrder` block spanning both overloads raised `CallSequenceNotFound` — the test was fragile to overload binding, not a code defect (the other 4 facts passed).
- **Fix:** Inspect `db.ReceivedCalls()` filtered to `StringSetAsync`, project each to `(key, value)` regardless of overload, and assert the index order `blobWrite < outboundPending < inboundAck` plus a separate `When.Exists` match on the Ack flip. Robust to overload binding; same invariant proven.
- **Files modified:** tests/BaseApi.Tests/Processor/EffectFirstDedupFacts.cs
- **Verification:** `EffectFirstDedupFacts` 5/5 green.
- **Committed in:** `b008995` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (1 Rule 1 - behavior-change realignment, 1 Rule 3 - test-harness robustness)
**Impact on plan:** Both were required to satisfy the plan's own wave verification (full hermetic suite green). No scope creep — no protocol logic beyond the plan's `<behavior>` was added; the realigned facts assert exactly the Plan 31-03 behavior.

## Threat Surface
No new security surface. The change operates entirely on the existing internal L2 (Redis) boundary (`flag[H]` / `data[hash]`). T-31-07 (manifest content-address) is honored — the manifest is a server-serialized JSON array of server-derived 64-hex, hashed via the one `HashManifest` helper over the exact wire bytes, never user input. T-31-08 (id-in-logs) preserved — execution ids appear only as nested-scope VALUES under fixed `ExecutionLogScope` keys, never in the message template. T-31-09 (When.Exists false-return) honored — the residual is never thrown on. T-31-10 (output validation) honored — schema validation runs on each DATA blob pre-write, never on the manifest.

## Known Stubs
None introduced by this plan. The outbound `flag[resultH]="Pending"` pre-write is a real seed consumed by Plan 04 (the orchestrator flips it Pending->Ack via `When.Exists`); the inbound `flag[dispatch.H]` Pending seed is written by Plan 04's `StepDispatcher`/`WorkflowFireJob` sender pre-write (until then the inbound `When.Exists` flip is a no-op on an absent key — by design, not a stub: a missing key means "first delivery, nothing to flip").

## Issues Encountered
- The repo MTP test runner did not emit a parseable TRX/console failure detail in this environment (`--report-trx` produced no file; the `.log` is the CLI help dump). Resolved by bisecting failures per-namespace then per-class via `--filter-class`/`--filter-method` exit status, which localized all 4 collateral breakages precisely.
- Plan executed out of strict wave order relative to 31-05 (which was already complete on `master` — it has no dependency on 31-03); 31-04 and 31-06 remain unstarted. No conflict: 31-03 touches only the processor consumer + its facts.

## Next Phase Readiness
- The processor is now the complete producer half of exactly-once-effect: content addressing makes a retry reproduce identical keys, the manifest collapses N results into one deterministic identity, and the outbound `flag[resultH]="Pending"` seed wires (does not leave dead) the orchestrator hop.
- Plan 04 can now: add the orchestrator inbound drop-on-Ack gate on `flag[m.H]`, flip it Pending->Ack via `When.Exists` (the seed this plan writes makes that flip live), do manifest fan-out, set the entry-step `EntryId = EntryEntryId(corr, stepId)`, and add the `StepDispatcher`/`WorkflowFireJob` sender pre-write `flag[H_child]="Pending"` (the inbound analog of this plan's outbound pre-write).

## Self-Check: PASSED
- FOUND: src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs (modified)
- FOUND: tests/BaseApi.Tests/Processor/EffectFirstDedupFacts.cs (created)
- FOUND commit 73c0cab (Task 1) — feat(31-03) consumer rework
- FOUND commit b008995 (Task 2) — test(31-03) EffectFirstDedupFacts + realignment
- VERIFIED: dotnet build src/BaseProcessor.Core -c Debug = 0/0
- VERIFIED: EffectFirstDedupFacts 5/5; full hermetic suite 434/0

---
*Phase: 31-idempotent-execution-exactly-once-effect*
*Completed: 2026-06-04*
