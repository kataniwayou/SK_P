---
phase: quick-260615-kgz
plan: 01
subsystem: [orchestrator, processor, observability-analyzer]
tags: [execution-id, fan-out, lineage, per-instance-trace, obs-03]
requires:
  - EntryStepDispatch.ExecutionId (already existed)
  - ExecutionLogScope.ExecutionId (already existed)
provides:
  - ORCH-EXEC-PROPAGATE
  - PROC-PER-EXEC-LOG
  - OBS-PER-EXEC-KEY
affects:
  - src/Orchestrator/Consumers/TypedResultConsumer.cs
  - src/BaseProcessor.Core/Processing/*
  - src/Processor.Sample/SampleProcessor.cs
  - tests/BaseApi.Tests/Observability/*
tech-stack:
  added: []
  patterns:
    - "Per-(correlationId, executionId) instance keying for run traces"
    - "Spawn-aware OBS-03 reconciliation (ResultConsumed = DispatchSent + spawnExtra), spawnExtra derived from distinct-correlationId count"
    - "Nested BeginScope to inject minted ExecutionId alongside structured StepLabel (consume filter skips Guid.Empty)"
key-files:
  created:
    - .planning/quick/260615-kgz-per-correlationid-executionid-multi-exec/deferred-items.md
  modified:
    - src/Orchestrator/Consumers/TypedResultConsumer.cs
    - src/BaseProcessor.Core/Processing/BaseProcessor.cs
    - src/BaseProcessor.Core/Processing/BaseProcessor`1.cs
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - src/Processor.Sample/SampleProcessor.cs
    - src/Processor.BadConfig/BadConfigProcessor.cs
    - tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs
    - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
    - tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs
    - tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs
    - tests/BaseApi.Tests/Processor/PipelineInFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
    - tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs
    - tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs
    - tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs
    - tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs
    - tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs
    - tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
decisions:
  - "MassTransit using kept in TypedResultConsumer — IConsumer/ConsumeContext still reference it (plan's conditional remove did not apply)"
  - "CapturingLogger in SampleProcessorFacts now records active BeginScope KVPs so the per-execution ExecutionId is observable hermetically"
  - "spawnExtra derived from traces.Select(t => t.CorrelationId).Distinct().Count() — never hard-coded 2"
metrics:
  duration: ~21m
  completed: 2026-06-15
---

# Phase quick-260615-kgz Plan 01: Per-(correlationId, executionId) Multi-Execution Lineage Summary

Each spawned execution instance is now an independently traceable run with a stable ExecutionId from ENTRY through both fan-out sinks: the orchestrator propagates ExecutionId unchanged through continuation dispatch, the processor seam threads `Guid executionId` and branches entry-vs-downstream emitting a per-execution log carrying both StepLabel and ExecutionId, and the analyzer keys per (correlationId, executionId) with a spawn-aware OBS-03 reconciliation.

## What Was Built

### Task 1 — Orchestrator (commit abdd29d)
`TypedResultConsumer.Consume` now passes `m.ExecutionId` (was `NewId.NextGuid()`) to the continuation dispatch, so the per-instance lineage survives every step. `ResultConsumeTests` and `ResultAckTests` flipped from asserting regeneration to asserting the dispatched ExecutionId EQUALS the inbound result's ExecutionId.

### Task 2 — Processor seam + transform (commit cb408bd, TDD)
- Seam threads `Guid executionId`: `BaseProcessor.ExecuteAsync`, `BaseProcessor<TConfig>.ExecuteAsync`/`ProcessAsync`, and the `ProcessorPipeline` call site (passes `d.ExecutionId` unchanged).
- `SampleProcessor.ProcessAsync` branches: ENTRY (`executionId == Guid.Empty`) spawns 2 items with distinct freshly-minted ExecutionIds (independent random sum); DOWNSTREAM reuses the inbound ExecutionId and accumulates deterministically (`incoming.number + config.Number`, no random). Every Step_* log carries `{StepLabel}` AND the ExecutionId via a nested `BeginScope`.
- All non-spawning overriders (BadConfig, SeamFacts TestProcessor, PipelineInFacts RealDeserProcessor, DispatchTestKit FakeProcessor) accept and ignore `executionId`.
- `SampleProcessorFacts` rewritten with entry/downstream/null facts; its `CapturingLogger` now records active scope KVPs so the per-execution ExecutionId is asserted hermetically.

### Task 3 — Analyzer (commit 1a5009b)
- `EsIndexNames.ExecutionIdFieldPath = "attributes.ExecutionId"` (mirrors StepLabelFieldPath, DIRECT path).
- `RunTrace` keyed by `(correlationId, executionId)`; `FromLabels` gains `executionId`.
- `PassFailEngine.Analyze` gains `spawnExtra` (default 0): reconciles `ResultConsumedDelta` against `DispatchSentDelta + spawnExtra` within ±1-run result slack; mismatch is a NON-FATAL warning. `AnalyzerReport` gains `SpawnExtra` + `ExpectedResultConsumed`.
- `AnalyzerE2ETests` (RealStack) groups Step_* hits per `(correlationId, executionId)`, adds an `ExecutionId` exists filter, and derives `spawnExtra` from `traces.Select(t => t.CorrelationId).Distinct().Count()`.
- `PassFailEngineFacts`: every `FromLabels` gains an executionId; +2 spawn-aware reconciliation facts (clean excess vs out-of-tolerance warning).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] MassTransit `using` kept in TypedResultConsumer**
- **Found during:** Task 1
- **Issue:** The plan suggested removing `using MassTransit;` if `NewId.NextGuid()` removal left it unused; but `IConsumer<TMessage>` and `ConsumeContext<TMessage>` also reference MassTransit. Removing it broke the build (CS0246).
- **Fix:** Restored the `using` (the plan's own guidance: "VERIFY whether MassTransit is referenced elsewhere first — if any other symbol uses it, keep the using").
- **Files modified:** src/Orchestrator/Consumers/TypedResultConsumer.cs
- **Commit:** abdd29d

**2. [Rule 3 - Blocking] Local name collision `sum`**
- **Found during:** Task 2
- **Issue:** The downstream `var sum` collided with the entry-loop `var sum` (CS0136).
- **Fix:** Renamed the downstream local to `accumulated`.
- **Files modified:** src/Processor.Sample/SampleProcessor.cs
- **Commit:** cb408bd

## Verification

- `dotnet build SK_P.sln -c Release` → **0 warnings, 0 errors** (TreatWarningsAsErrors; RealStack fixture compiles).
- Per-task hermetic filters all green:
  - Task 1: ResultConsumeTests + ResultAckTests → 7/7
  - Task 2: SampleProcessorFacts + PipelineInFacts + BaseProcessorSeamFacts → 10/10; hermetic PipelinePre/PostFacts (FakeProcessor) → 9/9
  - Task 3: PassFailEngineFacts + RunTrace + EsIndexNames → 9/9
- Combined edited-class run → **35/35 passed**.

## Deferred Issues

- The full-suite `--filter-not-trait "Category=RealStack"` form is silently ignored by this MTP suite, so it runs all 757 tests and 272 live-stack integration tests fail with connection-refused (Postgres/Redis/ES not running — HERMETIC ONLY task). None of these failures touch any edited class (verified via grep of the test log). See `deferred-items.md`. The live 7-scenario sweep (run separately by the orchestrator) covers the RealStack path.

## Self-Check: PASSED
- src/Orchestrator/Consumers/TypedResultConsumer.cs — FOUND (m.ExecutionId)
- src/BaseProcessor.Core/Processing/BaseProcessor.cs — FOUND (Guid executionId)
- src/Processor.Sample/SampleProcessor.cs — FOUND (executionId == Guid.Empty)
- tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs — FOUND (ExecutionIdFieldPath)
- tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs — FOUND (ExecutionId)
- Commits abdd29d, cb408bd, 1a5009b — all present in git log.
