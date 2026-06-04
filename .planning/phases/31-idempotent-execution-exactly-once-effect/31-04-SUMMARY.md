---
phase: 31-idempotent-execution-exactly-once-effect
plan: 04
subsystem: orchestrator
tags: [exactly-once-effect, effect-first-dedup, manifest-fanout, merge-collapse, entry-entryid, redis-cas, orchestrator-consumer]

# Dependency graph
requires:
  - phase: 31-idempotent-execution-exactly-once-effect
    plan: 01
    provides: "MessageIdentity (ComputeH/EntryEntryId), L2ProjectionKeys.ExecutionData(string)/Flag(string)"
  - phase: 31-idempotent-execution-exactly-once-effect
    plan: 02
    provides: "string EntryId + string H on EntryStepDispatch/ExecutionResult; IStepDispatcher string-entryId signature; FireDispatch deferred req-2 marker"
  - phase: 31-idempotent-execution-exactly-once-effect
    plan: 03
    provides: "processor producer half — ONE manifest ExecutionResult (EntryId=manifestEntryId, H=ComputeH(...)), data[manifestHash]=string[], outbound flag[resultH]=Pending seed"
provides:
  - "StepDispatcher: deterministic child H = ComputeH(corr,wf,step,proc,entryId) stamped on every dispatch + flag[H]=Pending sender pre-write (D-06, symmetric inbound analog of the processor's outbound pre-write)"
  - "WorkflowFireJob: entry-step EntryId = MessageIdentity.EntryEntryId(correlationId, entryStepId) (req-2, non-empty deterministic)"
  - "ResultConsumer: inbound drop-on-Ack gate on flag[m.H] (effect-first, orchestrator hop) + manifest unbundle (data[m.EntryId] -> Deserialize<string[]>) + N x M fan-out + flag[m.H] Pending->Ack via When.Exists"
  - "ManifestFanoutFacts (req-6) + MergeCollapseFacts (req-5) + resolved FireDispatch req-2 assertions"
affects: [31-06]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Symmetric effect-first dedup: BOTH hops drop-on-Ack at consume top + flip Pending->Ack (When.Exists) AFTER the effect; sender pre-writes flag[H_child]=Pending so the receiver's XX flip has a key"
    - "Deterministic child H excludes executionId (D-02): an orchestrator redelivery regenerates executionId but reproduces the SAME H per (item, successor) -> deduped at the next hop (req-6)"
    - "Manifest N x M fan-out (D-08): for each manifest item x each SelectNext successor, dispatch one continuation carrying the item EntryId"
    - "Merge correctness via input EntryId (req-5): child H differs ONLY by item EntryId (predecessor step id never enters ComputeH) -> different output = distinct H (no override); identical output = same H (collapse)"
    - "Graceful manifest degrade (T-31-11): empty EntryId short-circuits the read; a missing/garbled key -> zero items via ?? Array.Empty, never a throw on the business path"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/ManifestFanoutFacts.cs
    - tests/BaseApi.Tests/Orchestrator/MergeCollapseFacts.cs
  modified:
    - src/Orchestrator/Dispatch/StepDispatcher.cs
    - src/Orchestrator/Dispatch/IStepDispatcher.cs
    - src/Orchestrator/Scheduling/WorkflowFireJob.cs
    - src/Orchestrator/Consumers/ResultConsumer.cs
    - tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs
    - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
    - tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorTestStubs.cs
    - tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs
    - tests/BaseApi.Tests/Orchestrator/WorkflowFireJobScopeTests.cs

key-decisions:
  - "StepDispatcher flag pre-write is UNCONDITIONAL (per plan): a re-send of Pending->Pending is idempotent and only runs at SEND time (before the receiver processes), so it never clobbers the receiver's effect-first Ack. A fixed 300s const TTL bounds the skp:flag: namespace (StepDispatcher has no IOptions today)"
  - "Realigned 3 pre-existing orchestrator hermetic tests (Rule 1) to the dedup+manifest behavior: the old L1-only / EntryId-passthrough invariant is superseded — the orchestrator now reads flag[m.H] + data[m.EntryId], fans out the manifest item EntryId, and regenerates executionId"
  - "Used a concrete RecordingDispatcher (not Substitute.For<IStepDispatcher>) for the fan-out assertions — NSubstitute Received-matcher resolution against a captured call mixing concrete + Arg.Any args proved fragile; capturing the calls directly is robust"
  - "When.Exists in the StringSetAsync Ack flip binds the modern (RedisKey,RedisValue,Expiration,ValueCondition,CommandFlags) overload (When.Exists -> ValueCondition rendering 'XX'); assertions inspect ReceivedCalls for a ValueCondition 'XX', mirroring EffectFirstDedupFacts' overload-robust approach"

requirements-completed: [req-2, req-4, req-5, req-6]

# Metrics
duration: 36min
completed: 2026-06-04
---

# Phase 31 Plan 04: Orchestrator Exactly-Once-Effect Consumer Half + Fan-out Summary

**The orchestrator dispatch/fan-out half of exactly-once-effect: `StepDispatcher` stamps a deterministic child `H` (executionId excluded) on every dispatch and pre-writes `flag[H]="Pending"`; `WorkflowFireJob` carries a non-empty `EntryId = EntryEntryId(corr, stepId)` (req-2); `ResultConsumer` drops inbound results whose `flag[m.H]` is already `"Ack"` (effect-first, orchestrator hop, req-4), unbundles the manifest from `data[m.EntryId]`, fans out N items x M successors each with the item EntryId + a regenerated executionId, and flips `flag[m.H]` Pending->Ack via `When.Exists` — merge correctness (req-5) and redelivery dedup (req-6) fall out for free, proven by `ManifestFanoutFacts` + `MergeCollapseFacts`, full hermetic suite green at 441/0.**

## Performance

- **Duration:** ~36 min
- **Started:** 2026-06-04T13:57:57Z
- **Completed:** 2026-06-04T14:33:55Z
- **Tasks:** 3
- **Files modified:** 12 (2 created, 10 modified)

## Accomplishments
- **StepDispatcher (D-02/D-06):** injects `IConnectionMultiplexer`; computes `h = MessageIdentity.ComputeH(correlationId, workflowId, stepId, processorId, entryId)` (executionId structurally excluded), stamps it on the `EntryStepDispatch`, and pre-writes `flag[h]="Pending"` (unconditional, 300s const TTL) BEFORE the `Send`. The sender-pre-write is the symmetric inbound analog of the processor's outbound `flag[resultH]="Pending"` seed (31-03) — without it the receiver's `When.Exists` Pending->Ack flip is a no-op on an absent key. A Redis fault on the pre-write is INFRA -> propagates.
- **WorkflowFireJob (req-2):** the entry-step dispatch now passes `entryId = MessageIdentity.EntryEntryId(correlationId, entryStepId)` (non-empty 64-hex, deterministic per fire) instead of the Plan-02 `""` placeholder; executionId stays `Guid.Empty` (lineage). Two fires -> different correlationId -> different EntryId -> different H.
- **ResultConsumer (req-4/5/6, D-06/D-08):** injects `IConnectionMultiplexer`. (1) **Dedup gate** — `flag[m.H] == "Ack"` at the top -> drop + broker-ack (after the metrics increment). (2) **Manifest unbundle** — `string.IsNullOrEmpty(m.EntryId)` short-circuits to zero items (Failed/Cancelled); else `Deserialize<string[]>(data[m.EntryId] ?? "[]") ?? Array.Empty` (graceful T-31-11 degrade). (3) **N x M fan-out** — `foreach item x foreach SelectNext successor` dispatch with the item EntryId + `NewId.NextGuid()` executionId (child H computed inside DispatchAsync). (4) **Effect-first flip** — `StringSet(Flag(m.H), "Ack", When.Exists)` after fan-out; a false return is the designed residual (not thrown on, D-07).
- **ManifestFanoutFacts (req-6, 3 facts):** 2-item manifest x 1 successor -> 2 distinct dispatches; empty `"[]"` -> 0 dispatches + flag flip still attempted (no throw); redeliver -> deterministic child H + an already-`"Ack"` result is dropped (no extra dispatch).
- **MergeCollapseFacts (req-5, 3 facts):** two predecessors -> one merge step; different-output -> distinct child H (both dispatch, no override); identical-output -> same child H (collapse); child H independent of the predecessor step id (only the successor + item enter ComputeH).
- **FireDispatchTests (req-2):** the deferred Plan-02 `EntryId == ""` assertion is flipped to `Assert.False(IsNullOrEmpty)` + `Assert.Equal(MessageIdentity.EntryEntryId(corr, stepId), EntryId)`; the two-fire test additionally asserts different correlationId -> different EntryId; the `Plan 04 changes this` marker is removed.

## Task Commits

Each task was committed atomically:

1. **Task 1: StepDispatcher deterministic-H + flag[H]=Pending pre-write; WorkflowFireJob entry-step EntryId** — `f9b7601` (feat)
2. **Task 2: ResultConsumer inbound flag[H] dedup + manifest unbundle + N x M fan-out (+ test realignment)** — `6e275fb` (feat)
3. **Task 3: ManifestFanoutFacts + MergeCollapseFacts + flip deferred FireDispatch req-2** — `3c30bdb` (test)

**Plan metadata:** committed separately (docs).

_Task-ordering note: the StepDispatcher/ResultConsumer ctor gained an `IConnectionMultiplexer` param, breaking the direct `new StepDispatcher(...)`/`new ResultConsumer(...)` test sites. The StepDispatcher-ctor threading (FireDispatch/WorkflowFireJobScope) was folded into Task 2's commit so the test project compiles per-commit; production code compiled 0/0 at each task boundary._

## Decisions Made
- **Unconditional flag pre-write + const TTL** — per the plan's `<action>`: a re-write of Pending is idempotent and runs only at SEND time (before the receiver processes), so it never clobbers the receiver's effect-first Ack. A fixed `TimeSpan.FromSeconds(300)` const bounds `skp:flag:` (StepDispatcher has no IOptions today).
- **RecordingDispatcher over NSubstitute for fan-out assertions** — see Issues; capturing the dispatch calls directly is robust to NSubstitute Received-matcher fragility.
- **`When.Exists` -> `ValueCondition "XX"` overload** — the `when:`-named `StringSetAsync` binds the modern `(…, Expiration, ValueCondition, …)` overload; assertions inspect `ReceivedCalls()` for a `ValueCondition` rendering `"XX"` (SET XX), mirroring EffectFirstDedupFacts' overload-robust approach.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Behavior-change realignment] Realigned 3 pre-existing orchestrator hermetic tests broken by the intended dedup+manifest behavior**
- **Found during:** Task 2 (the ResultConsumer rework superseded the L1-only / EntryId-passthrough invariant)
- **Issue:** The plan's `files_modified` lists `ResultConsumer.cs` + the two new facts + `FireDispatchTests.cs`, but the deliberate behavior change (the orchestrator now reads `flag[m.H]` + `data[m.EntryId]`, fans out the manifest ITEM EntryId, and regenerates executionId) necessarily breaks pre-existing tests that assert the OLD behavior. The plan's `<verification>` mandates the full hermetic suite green.
- **Fix:** (a) `ResultConsumeTests` — its two tests passed a Completed result with a real EntryId but no manifest in Redis (now zero fan-out); seeded a one-item manifest stub + asserted the dispatched EntryId == the manifest ITEM + a regenerated (non-empty) executionId. (b) `ResultAckTests` — replaced the `ResultPath_PerformsNoL2Read` "L1-only" test (the invariant is gone) with `ResultPath_ReadsDedupFlag_And_UnbundlesManifest_ThenDispatches` and added `ResultWithFlagAlreadyAck_IsDropped_NoDispatch`; `InfraFaultOnSend_Propagates` now seeds a one-item manifest so a dispatch actually fires and throws. (c) `StopConsumerLifecycleTests.LateResult_*` — gave the ResultConsumer a one-item-manifest mux so the late result still fans out one continuation (EntryId = item, executionId regenerated). (d) `OrchestratorTestStubs` — added a `NoopRedis()` helper; threaded the new `IConnectionMultiplexer` ctor arg through every `new StepDispatcher(...)` / `new ResultConsumer(...)` site (FireDispatch x3, WorkflowFireJobScope, ResultConsume, ResultAck, StopConsumer).
- **Files modified:** the 6 test files above.
- **Verification:** full hermetic suite `--filter-not-trait "Category=RealStack"` = **Passed 441 / Failed 0**.
- **Committed in:** `6e275fb` (Task 2) + `3c30bdb` (Task 3 FireDispatch flip).

---

**Total deviations:** 1 auto-fixed (Rule 1 - behavior-change realignment)
**Impact on plan:** Required to satisfy the plan's own full-hermetic-suite-green verification. No protocol logic beyond the plan's `<behavior>` was added; each realigned test asserts exactly the Plan 31-04 behavior.

## Threat Surface
No new security surface. The change operates entirely on the existing internal L2 (Redis) boundary (`flag[H]` / `data[hash]`) and the existing MassTransit/RabbitMQ envelope. T-31-11 (manifest deserialize) honored — `Deserialize<string[]>` with a `?? Array.Empty` guard degrades a missing/garbled key to zero fan-out, never a business-path throw. T-31-12 (child H forgery via executionId) honored — executionId is excluded from `ComputeH` by construction, so a redelivery cannot bypass dedup. T-31-13 (ids in logs) preserved — the existing scope-value-under-fixed-key convention is unchanged; no id interpolated into a template. T-31-14 (When.Exists false-return) honored — the residual is never thrown on.

## Known Stubs
None introduced. The processor's outbound `flag[resultH]="Pending"` seed (31-03) is now consumed by this plan's `ResultConsumer` `When.Exists` flip — the orchestrator hop is fully live. The `StepDispatcher` inbound `flag[H]="Pending"` pre-write seeds the processor's `EntryStepDispatchConsumer` `When.Exists` flip (31-03) — both hops are symmetric and complete.

## Issues Encountered
- **NSubstitute Received-matcher fragility:** `dispatcher.Received(1).DispatchAsync(...)` mixing concrete Guids/strings with `Arg.Any<>()` against a captured call reported "no matching calls" even though the dispatch demonstrably happened (proven via direct `ReceivedCalls()` inspection). Resolved by using a concrete `RecordingDispatcher` that captures each call's args — deterministic, no matcher resolution. Also confirmed the `when: When.Exists` SET binds the modern `ValueCondition` overload (renders `"XX"`), so the Ack-flip assertion matches a `ValueCondition` not a `When` — same lesson as 31-03's EffectFirstDedupFacts.
- **MTP runner log opacity (known):** the repo MTP runner emits the CLI help dump as the `.log` (no parseable failure detail); diagnosed via the test `.exe` direct console output (`BaseApi.Tests.exe --filter-method ...`), which surfaces the xUnit/NSubstitute exception text.

## Next Phase Readiness
- The exactly-once-effect round-trip is now symmetric and complete on both hops: processor (31-03 producer) + orchestrator (31-04 consumer/fan-out). A redelivery on EITHER hop reproduces the same deterministic H and is deduped; merge correctness falls out of the per-edge child H.
- Plan 06 (live E2E, req-8, wave 4) can now exercise the full content-addressed round-trip against the real stack: fire -> entry EntryId -> processor content-addressed write + manifest + outbound Pending -> orchestrator drop-on-Ack + manifest fan-out + Pending->Ack flip, and assert the Immediate(3) attempt count + exactly-once-effect under redelivery.

## Self-Check: PASSED
- FOUND: src/Orchestrator/Dispatch/StepDispatcher.cs (modified — ComputeH stamp + flag[h]=Pending pre-write)
- FOUND: src/Orchestrator/Scheduling/WorkflowFireJob.cs (modified — EntryEntryId)
- FOUND: src/Orchestrator/Consumers/ResultConsumer.cs (modified — Flag(m.H) gate + Deserialize<string[]> + N x M loop + When.Exists flip)
- FOUND: tests/BaseApi.Tests/Orchestrator/ManifestFanoutFacts.cs (created)
- FOUND: tests/BaseApi.Tests/Orchestrator/MergeCollapseFacts.cs (created)
- FOUND commit f9b7601 (Task 1)
- FOUND commit 6e275fb (Task 2)
- FOUND commit 3c30bdb (Task 3)
- VERIFIED: dotnet build src/Orchestrator -c Debug = 0/0
- VERIFIED: full hermetic suite --filter-not-trait "Category=RealStack" = Passed 441 / Failed 0

---
*Phase: 31-idempotent-execution-exactly-once-effect*
*Completed: 2026-06-04*
