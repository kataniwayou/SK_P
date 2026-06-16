---
phase: 71-orchestrator-recovery-pipeline
plan: 03
subsystem: orchestrator
tags: [redis, lua, masstransit, recovery-pipeline, l2-projection, idempotency, nsubstitute, xunit]

# Dependency graph
requires:
  - phase: 71-orchestrator-recovery-pipeline (plan 02)
    provides: OrchestratorInject / OrchestratorReinject keeper-recovery contracts (the Build* escalation targets)
  - phase: 71-orchestrator-recovery-pipeline (plan 01)
    provides: ProcessorInject/ProcessorReinject rename + RecoveryConsumerBase Guard pattern (the keeper-side analog)
  - phase: 51-processor-forward-recovery-pipeline
    provides: ProcessorPipeline.cs — the structural template mirrored verbatim (gate/atomic-Lua/recovery/cleanup)
provides:
  - OrchestratorResultPipeline (gate exist L2[messageId] -> FORWARD atomic-Lua write / 3-way RECOVERY / gated 2-key cleanup)
  - The D-02 heterogeneous slot tuple (JSON {nextStepId, nextProcessorId, payload, newEntryId}) per index slot
  - The D-03 single atomic FORWARD Lua (HSET tuple + whole-hash PEXPIRE + GET+SET origin->new copy; TTLs as ARGV, no RNG)
  - TypedResultConsumer<T> seam wired to the pipeline (reverses Phase 24.1's L1-only result posture)
  - OrchestratorRecoveryOptions (the orchestrator-side data-TTL knob)
affects: [71-04 (OrchestratorInject/Reinject keeper consumers + delete-invariant facts), orchestrator-result live recovery proof]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "OrchestratorResultPipeline = a structural clone of ProcessorPipeline (D-01 independent class, not a shared base)"
    - "Single atomic Lua FORWARD write with a copy-an-existing-key (GET+SET) data leg; TTLs computed in C# as ARGV (no RNG in Lua — Phase-68 TEST-06 guard)"
    - "Heterogeneous index slots: the HASH field value is a JSON tuple; RECOVERY tolerantly parses + skips retired/unparsable slots"
    - "The pipeline is the ONLY orchestrator-side deleter (gated 2-key cleanup tail); FORWARD/RECOVERY escalation legs never delete"

key-files:
  created:
    - src/Orchestrator/Recovery/OrchestratorResultPipeline.cs
    - src/Orchestrator/Configuration/OrchestratorRecoveryOptions.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorPipelineTestKit.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorResultPipelineForwardFacts.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorResultPipelineRecoveryFacts.cs
  modified:
    - src/Orchestrator/Consumers/TypedResultConsumer.cs
    - src/Orchestrator/Consumers/StepCompletedConsumer.cs
    - src/Orchestrator/Consumers/StepFailedConsumer.cs
    - src/Orchestrator/Consumers/StepCancelledConsumer.cs
    - src/Orchestrator/Consumers/StepProcessingConsumer.cs
    - src/Orchestrator/Program.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorTestStubs.cs
    - tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs
    - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
    - tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs
    - tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs

key-decisions:
  - "GET+SET copy leg (RESEARCH Q3 Option B) inside the single atomic Lua — byte-closest to the processor's SET...PX leg, sidesteps the COPY-drops-TTL footgun (Pitfall 3)"
  - "OrchestratorRecoveryOptions (new orchestrator-side config) supplies ExecutionDataTtlSeconds — the orchestrator does not reference BaseProcessor.Core.ProcessorLivenessOptions"
  - "Pipeline registered Scoped (mirrors ProcessorPipeline) — it consumes the scoped ISendEndpointProvider; a Singleton would be a captive dependency"
  - "Dropped StepAdvancement + IStepDispatcher from the TypedResultConsumer<T> base ctor (the pipeline now owns SelectNext + dispatch) and forwarded OrchestratorResultPipeline through the four sealed subclasses"

patterns-established:
  - "Pipeline-owned downstream dispatch: FORWARD sends EntryStepDispatch to queue:{nextProcessorId} with a minted per-slot newEntryId, then retires the slot to guid.empty"
  - "Slot-tuple tolerant parse (TryParseTuple): a retired guid.empty sentinel / malformed JSON returns false and is skipped (T-71-06)"

requirements-completed: [ORCV-01, ORCV-02, ORCV-03, ORCV-04, ORCV-05]

# Metrics
duration: 45min
completed: 2026-06-16
---

# Phase 71 Plan 03: OrchestratorResultPipeline + TypedResultConsumer Seam Summary

**A ProcessorPipeline-mirrored, L2-gated orchestrator result pipeline — gate `exist L2[messageId]` → single-atomic-Lua FORWARD (D-02 JSON slot tuple + GET/SET origin→new copy) / 3-way RECOVERY / gated two-key cleanup — wired into the `TypedResultConsumer<T>` seam with a MessageId null-guard, reversing Phase 24.1's L1-only result posture.**

## Performance

- **Duration:** ~45 min
- **Started:** 2026-06-16T07:48:03Z
- **Completed:** 2026-06-16T08:33:15Z
- **Tasks:** 3 (Task 0 kit → Task 1 pipeline → Task 2 seam)
- **Files modified:** 16 (5 created, 11 modified)

## Accomplishments
- `OrchestratorResultPipeline` mirrors `ProcessorPipeline` (gate → FORWARD/RECOVERY → gated cleanup tail) as an independent class (D-01), diverging only for the three locked domain differences (D-02 JSON slot tuple, D-03 copy-an-existing-key atomic Lua, D-05 reconstruct-from-tuple re-send).
- FORWARD is exactly ONE Lua `ScriptEvaluateAsync` per next step (3 KEYS = index, copy-dest, copy-source); the index/data TTLs are computed in C# and ride as ARGV — NO RNG inside Lua (preserves the Phase-68 TEST-06 index/data anti-desync guard).
- The pipeline is wired into `TypedResultConsumer<T>` with a `context.MessageId is null` infra-throw guard; the four sealed subclasses forward the pipeline; DI-registered Scoped + `RetryOptions`/`OrchestratorRecoveryOptions` bound from config.
- 9 new pipeline facts GREEN (`*OrchestratorResultPipeline*`: 3 Forward + 6 Recovery); the 4 pre-71 L1-only result-consume fact classes migrated to assert the pipeline-owned `EntryStepDispatch` effect (all green).
- Solution builds 0-warning in Debug AND Release.

## Task Commits

1. **Task 0: Scaffold the pipeline test kit + recovery TTL options** - `babf18e` (test)
2. **Task 1: Build OrchestratorResultPipeline (gate, atomic FORWARD, slot tuple, gated cleanup, 3-way RECOVERY)** - `29f8ac2` (feat) — TDD plan: the Forward/Recovery facts + the production pipeline landed together as GREEN (the facts reference the type, so they could not compile/RED separately without dangling references — Task 0 deferred them per the plan's own guidance).
3. **Task 2: Wire the pipeline into the TypedResultConsumer seam + migrate the pre-71 result facts** - `ca21104` (feat)

**Plan metadata:** _(this commit)_ — docs: complete 71-03 plan

## Files Created/Modified
- `src/Orchestrator/Recovery/OrchestratorResultPipeline.cs` - the gate/forward/recovery/cleanup pipeline (the ProcessorPipeline mirror)
- `src/Orchestrator/Configuration/OrchestratorRecoveryOptions.cs` - the orchestrator-side data-TTL knob (`ExecutionDataTtlSeconds`)
- `src/Orchestrator/Consumers/TypedResultConsumer.cs` - the seam: L1 guard → MessageId null-guard → `pipeline.RunAsync(...)`
- `src/Orchestrator/Consumers/Step{Completed,Failed,Cancelled,Processing}Consumer.cs` - ctor forwards `OrchestratorResultPipeline` (Outcome override unchanged)
- `src/Orchestrator/Program.cs` - `Configure<RetryOptions>("Retry")`, `Configure<OrchestratorRecoveryOptions>("Recovery")`, `AddScoped<OrchestratorResultPipeline>()`
- `tests/BaseApi.Tests/Orchestrator/OrchestratorPipelineTestKit.cs` - gate/forward/recovery/atomic-write fault muxes + generic CapturingSendProvider + slot-tuple JSON builders
- `tests/BaseApi.Tests/Orchestrator/OrchestratorResultPipeline{Forward,Recovery}Facts.cs` - ORCV-01..05 hermetic facts
- `tests/BaseApi.Tests/Orchestrator/OrchestratorTestStubs.cs` - `Pipeline()` / `ForwardOkL2()` helpers + a MessageId-stamping `Context` overload
- `tests/BaseApi.Tests/Orchestrator/{TypedResultConsumerFacts,ResultAckTests,ResultConsumeTests,StopConsumerLifecycleTests}.cs` - migrated to the pipeline-owned dispatch effect

## Decisions Made
- **GET+SET copy leg (D-03 / RESEARCH Q3 Option B):** the single atomic Lua copies the origin key via `GET KEYS[3]` + `SET KEYS[2] v PX ARGV[4]` — byte-closest to the processor's `SET ... PX` leg and carries the data TTL inline (no COPY-drops-TTL footgun, Pitfall 3).
- **OrchestratorRecoveryOptions (new):** the orchestrator references only `BaseConsole.Core` + `Messaging.Contracts`, so it cannot reuse the processor's `ProcessorLivenessOptions.ExecutionDataTtlSeconds`. A small orchestrator-side options class supplies the single TTL source of truth (index TTL = `random[ttl, 2×ttl]`, data TTL = `ttl`), preserving the one-knob anti-desync property.
- **Pipeline lifetime Scoped:** mirrors `ProcessorPipeline` (the processor registers it Scoped). It consumes the scoped `ISendEndpointProvider`; a Singleton would be a captive dependency under the host's ValidateScopes.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Removed `StepAdvancement` + `IStepDispatcher` from the `TypedResultConsumer<T>` base ctor and forwarded the pipeline through the four sealed subclasses**
- **Found during:** Task 2 (seam wiring)
- **Issue:** The plan says "the four sealed subclasses do NOT change," but adding `OrchestratorResultPipeline` to the base primary constructor mechanically forces every subclass that forwards ctor args to update. Leaving the now-unused `advancement`/`dispatcher` primary-ctor params triggers CS9113 ("parameter is unread") under `TreatWarningsAsErrors`, failing the build.
- **Fix:** Swapped the base ctor to `(IWorkflowL1Store store, OrchestratorResultPipeline pipeline, OrchestratorMetrics metrics, ILogger<TMessage> logger)` (the pipeline now owns SelectNext + dispatch) and forwarded `pipeline` through all four subclasses. The subclasses' behavioral logic (the `Outcome` override) is unchanged — only the ctor forwarding edited.
- **Files modified:** TypedResultConsumer.cs + the four Step*Consumer.cs
- **Verification:** Debug + Release build 0-warning; `*TypedResultConsumer*` facts 8/8 GREEN.
- **Committed in:** `ca21104`

**2. [Rule 3 - Blocking] Migrated the four pre-71 L1-only result-consume fact classes to the pipeline-owned dispatch effect**
- **Found during:** Task 2 (seam wiring)
- **Issue:** `TypedResultConsumerFacts`, `ResultAckTests`, `ResultConsumeTests`, `StopConsumerLifecycleTests` constructed the consumers with the old 5-arg ctor (`store, advancement, dispatcher, metrics, logger`) and asserted against a `RecordingDispatcher`/`IStepDispatcher.DispatchAsync` effect that Phase 71 deliberately replaces with the pipeline-owned `EntryStepDispatch` send. They no longer compiled.
- **Fix:** Updated each to construct the consumer with the new 4-arg ctor over a forward-OK `OrchestratorResultPipeline` and assert the downstream `EntryStepDispatch` to `queue:{nextProcessorId}` (with a minted per-slot newEntryId + preserved correlation/execution lineage). Added `OrchestratorTestStubs.Pipeline()` / `ForwardOkL2()` helpers + a MessageId-stamping `Context` overload.
- **Files modified:** the four fact files + OrchestratorTestStubs.cs
- **Verification:** all four classes GREEN in isolation (8/5/2/2); the new `EntryId` is a minted newEntryId (NOT the inbound origin — the FORWARD copy mints a new key), so the old "entryId straight-through" assertions were correctly replaced.
- **Committed in:** `ca21104`

---

**Total deviations:** 2 auto-fixed (both Rule 3 - blocking compile failures directly caused by the seam ctor change).
**Impact on plan:** Necessary for a clean compile + faithful tests under the new architecture. No scope creep — the pipeline-owned dispatch is exactly the plan's intent; the subclass/test edits are the mechanical consequence.

## Issues Encountered
- **Pre-existing parallel-run flakiness (13 `BaseApi.Tests.Orchestrator` tests):** the full-suite/namespace run under `maxParallelThreads: 6` fails 13 tests — but the SAME 13 fail at the pre-71-03 baseline `4102e44` (verified via a read-only worktree: baseline 78/13, after-71-03 87/13 — the +9 are the new pipeline facts, the failing count is identical). These are timing-sensitive harness/Quartz Orchestrator tests; each passes in isolation. NOT a regression from this plan. Logged to `71/deferred-items.md` as an operator follow-up. Every Phase-71-03 deliverable class is green (pipeline 9/9; migrated result facts 8/5/2/2).

## Threat Flags
None — the pipeline's atomic Lua is a compile-time `const` with parameterized KEYS/ARGV (T-71-05 mitigated: no orchestrator data concatenated into the script); the copy leg carries the data TTL inline (T-71-07); the only deleter is the gated cleanup tail (T-71-08); RECOVERY parses slots tolerantly (T-71-06). No new network/auth/file surface introduced.

## Next Phase Readiness
- ORCV-01..05 delivered with passing facts; the orchestrator result path is L2-gated end-to-end (gate → FORWARD/RECOVERY → cleanup).
- 71-04 (the `OrchestratorInject`/`OrchestratorReinject` keeper consumers + the delete-invariant negative-guard facts) can build on the `Build*` escalation targets this pipeline emits — zero file overlap (this plan owns `src/Orchestrator/*`).
- Operator follow-up: pin the 13 flaky Orchestrator harness/Quartz tests so the full namespace is green under parallel run.

## Self-Check: PASSED

- All 5 created files present on disk (OrchestratorResultPipeline.cs, OrchestratorRecoveryOptions.cs, OrchestratorPipelineTestKit.cs, the two fact files) + the SUMMARY.
- All 3 task commits exist (`babf18e`, `29f8ac2`, `ca21104`).
- Acceptance greps: `pipeline.RunAsync` + `context.MessageId is null` present in the seam; `dispatcher.DispatchAsync` removed from Consume; the only `KeyDeleteAsync` is in `DeleteTerminalAsync`; the Lua const has no RNG/`TIME`; `OrchestratorResultPipeline` DI-registered.
- Targeted facts: `*OrchestratorResultPipeline*` 9/9, `*TypedResultConsumer*` 8/8, `*ResultAck*`/`*ResultConsume*`/`*StopConsumerLifecycle*` all green. Debug + Release build 0-warning.

---
*Phase: 71-orchestrator-recovery-pipeline*
*Completed: 2026-06-16*
