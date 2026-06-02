---
phase: 27-execution-round-trip
verified: 2026-06-02T05:28:55Z
status: passed
score: 5/5 success criteria verified (11/11 requirements covered)
overrides_applied: 0
requirements_covered: [EXEC-01, EXEC-02, EXEC-03, EXEC-04, EXEC-05, EXEC-06, EXEC-07, EXEC-08, EXEC-09, EXEC-10, CONFIG-02]
deferred:
  - truth: "Live ConnectReceiveEndpoint-against-RabbitMQ bind + Healthy-after-bind ordering proven against a real broker (not just an in-memory fake connector); full live orchestrator -> processor -> orchestrator E2E round-trip"
    addressed_in: "Phase 28"
    evidence: "Phase 28 Success Criterion 4: 'A real-stack E2E proves the live round-trip - a dispatch is consumed, output is written to L2, and the orchestrator advances on the returned ExecutionResult - and proves the liveness-gated Start path'. Deferral is documented in 27-03-SUMMARY key-decisions (TEST-01) and is by design, not a gap."
gate:
  build: "dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj -c Debug -> Build succeeded. 0 Warning(s) / 0 Error(s) (exit 0)"
  test: "dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -> Passed! - Failed: 0, Passed: 387, Skipped: 0, Total: 387, Duration: 3m 05s (exit 0)"
---

# Phase 27: Execution Round-Trip Verification Report

**Phase Goal:** A Healthy processor consumes a real `EntryStepDispatch`, resolves + validates its input from L2, runs the `abstract ProcessAsync` transform, validates + writes each output to L2, mints results, and sends `ExecutionResult`s back to the orchestrator one-by-one - with the framework owning all id-minting, validation, L2 I/O, and sending so a concrete overrides only `ProcessAsync`.

**Verified:** 2026-06-02T05:28:55Z
**Status:** PASS
**Re-verification:** No - initial verification

## Verdict

**PASS.** All 5 ROADMAP success criteria are met and all 11 claimed requirements (EXEC-01..EXEC-10 + CONFIG-02) are COVERED by substantive, wired implementation code with proving tests. The build is clean (0/0) and the full test suite is GREEN (387/387, 0 skipped). The only deferral - the live RabbitMQ bind proof and full real-stack E2E - is explicitly owned by Phase 28 (SC #4 / TEST-01) by locked design decision, not a Phase 27 gap.

## Gate Command Output (run from repo root this session)

```
dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj -c Debug
  Messaging.Contracts -> ...Messaging.Contracts.dll
  BaseConsole.Core    -> ...BaseConsole.Core.dll
  BaseProcessor.Core  -> ...BaseProcessor.Core.dll
Build succeeded.
    0 Warning(s)
    0 Error(s)
(exit 0)

dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj
  Passed! - Failed: 0, Passed: 387, Skipped: 0, Total: 387, Duration: 3m 05s 963ms - BaseApi.Tests.dll (net8.0|x64)
(exit 0)
```

Note: the MTP/xUnit-v3 runner ignores VSTest `--filter`, so an unfiltered full-suite run is expected and is the gate. The broker-dependent `Orchestrator.ResultConsumeTests.CompletedResult_DispatchesMatchingNextStep_WithCorrectFieldCopy` (logged in `deferred-items.md` as a 27-01-run-time infra blip) PASSED this run - the full compose stack (RabbitMQ 5673, Redis 6380) was up, so 387/387 with zero deferred failures.

## Goal Achievement - Observable Truths (ROADMAP Success Criteria)

| # | Truth (ROADMAP SC) | Status | Evidence |
| - | ------------------ | ------ | -------- |
| 1 | Durable `queue:{processorId:D}` competing-consumer endpoint, bound only once Healthy, Healthy written to L2 only after bind | VERIFIED | `ProcessorStartupOrchestrator.cs:148-159`: bind `queueName = $"{context.Id!.Value:D}"` (bare name) via `endpointConnector.ConnectReceiveEndpoint`, `await handle.Ready` (154) THEN `context.MarkHealthy()` (156). Heartbeat writes L2 only when IsHealthy. Proven by `DispatchBindSequenceFacts` ordered-event log `["connect","ready","markhealthy"]` (line 101) + bare-name assertion (110-112). |
| 2 | Input read only from `L2[data(entryId)]` (existence-checked); Payload is config; validated vs inputDefinition; empty def skips; missing/empty entryId -> Failed | VERIFIED | `EntryStepDispatchConsumer.cs:58-108`: `db.StringGetAsync(L2ProjectionKeys.ExecutionData(dispatch.EntryId))` (78), `raw.IsNullOrEmpty` existence check (79), `dispatch.Payload` only passed as config (114), required-input-missing -> `BuildFailed` before invoke (72/87/106). Proven by `DispatchInputFacts`. |
| 3 | Sole transform seam `ProcessAsync(inputData, config, ct)`; per-result output-validate (empty=valid), mint entryId + write L2 with TTL on success, nothing written on fail, mint per-result executionId | VERIFIED | `BaseProcessor.cs:22-32` (protected abstract + internal `ExecuteAsync` forwarder); `EntryStepDispatchConsumer.cs:129-150`: `TryValidate(OutputDefinition,...)` (133), `NewId.NextGuid()` newEntryId (140), `StringSetAsync(...ExecutionData(newEntryId), expiry: TimeSpan.FromSeconds(opts.ExecutionDataTtlSeconds))` (144-147), fail path writes nothing + `Guid.Empty` (136), per-result `ExecutionId = NewId.NextGuid()` (187/194/203). Proven by `DispatchOutputWriteFacts` (real Redis + TTL) + `DispatchInvokeFacts`. |
| 4 | Sent to `queue:orchestrator-result` one-by-one (never batched); empty list = ack only; Failed (incl. caught exception) + Cancelled always sent | VERIFIED | `EntryStepDispatchConsumer.cs:155-164`: empty `built` returns/ack-only (155-156), per-result loop `endpoint.Send(er, ...)` (159-164) - never a list. `OrchestratorQueues.Result = "orchestrator-result"` (`OrchestratorQueues.cs:16`). Cancelled (119) + Failed-on-exception (125) always sent via `SendOne`. Proven by `DispatchResultSendFacts`. |
| 5 | Acked only after all sends; infra faults throw + retry `Immediate(3)` (business-ack/infra-throw); body CorrelationId flows into log scope + onto every ExecutionResult | VERIFIED | Ack-after-all-sends (165, normal return); L2 read/write + Send uncaught = infra-throw (54-56, 142-147 no catch); `Immediate(3)` at bind (`ProcessorStartupOrchestrator.cs:151`); business outcomes return without throw. CorrelationId onto every result (185/193/202); log scope via inherited `InboundCorrelationConsumeFilter.BeginScope("CorrelationId")` (`BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs:39`). Proven by `DispatchAckSemanticsFacts` + `DispatchCorrelationFacts`. |

**Score:** 5/5 success criteria verified.

## Requirements Coverage

| Requirement | Source Plan | Status | Evidence |
| ----------- | ----------- | ------ | -------- |
| EXEC-01 (durable `{Id:D}` bind once Healthy; Healthy->L2 only after bind) | 27-03 | COVERED | `ProcessorStartupOrchestrator.cs:148-157`; `DispatchBindSequenceFacts` (ordered `connect/ready/markhealthy`, bare-name). |
| EXEC-02 (input from `L2[data(entryId)]` existence-checked; Payload is config) | 27-02 | COVERED | `EntryStepDispatchConsumer.cs:78-79,96,114`; `DispatchInputFacts`. |
| EXEC-03 (validate vs inputDefinition; empty skips; missing entryId -> Failed) | 27-01/02 | COVERED | `ProcessorJsonSchemaValidator.cs:30-35` (empty=true); consumer `:66-108`; `DispatchInputFacts` + `ProcessorJsonSchemaValidatorFacts`. |
| EXEC-04 (`ProcessAsync(inputData, config, ct)` sole seam, config=Payload) | 27-01/02 | COVERED | `BaseProcessor.cs:22-32`; consumer `:114`; `DispatchInvokeFacts` + `BaseProcessorSeamFacts`. |
| EXEC-05 (output-validate empty=valid; success mint+write L2 TTL; fail writes nothing) | 27-01/02 | COVERED | consumer `:133-150`; `DispatchOutputWriteFacts` (real Redis + `KeyTimeToLiveAsync` TTL assert). |
| EXEC-06 (mint per-result executionId, stamp shared Ids, one ExecutionResult per result; concretes write no infra) | 27-02 | COVERED | consumer `:182-206` builders; `ProcessResult` is OutputData-only (`ProcessResult.cs:10`); `DispatchInvokeFacts` distinct ExecutionId. |
| EXEC-07 (sent one-by-one, never batched) | 27-02 | COVERED | consumer `:159-164` loop; `DispatchResultSendFacts` (3 individual messages). |
| EXEC-08 (empty=ack-only; Failed incl. caught exception + Cancelled always sent) | 27-02 | COVERED | consumer `:116-127,155-156`; `DispatchResultSendFacts` (Cancelled_Always_Sent, the Rule-1 `CancellationToken.None` fix). |
| EXEC-09 (ack after all sends; infra throw + `Immediate(3)`) | 27-02/03 | COVERED | consumer `:54-56,142-147,165`; retry at `ProcessorStartupOrchestrator.cs:151`; `DispatchAckSemanticsFacts` (output-write + input-read faults propagate, business-fail does not). |
| EXEC-10 (body CorrelationId -> log scope + every ExecutionResult, inherited from BaseConsole.Core) | 27-02 | COVERED | consumer `:185,193,202`; log scope `InboundCorrelationConsumeFilter.cs:39`; `DispatchCorrelationFacts`. |
| CONFIG-02 (execution-data L2 keys own configurable TTL seconds) | 27-01 | COVERED | `ProcessorLivenessOptions.cs:40-41` (`ExecutionDataTtl`, default 300); bound via `services.Configure<ProcessorLivenessOptions>(cfg.GetSection("Processor"))` (`BaseProcessorServiceCollectionExtensions.cs:77`); applied `EntryStepDispatchConsumer.cs:147`; `ProcessorOptionsBindingFacts` (bind 120 + default 300). |

**Coverage counts:** 11 COVERED / 0 PARTIAL / 0 MISSING (of 11 claimed). No ORPHANED requirements - REQUIREMENTS.md maps exactly EXEC-01..10 + CONFIG-02 to Phase 27, all claimed by plans 27-01/02/03.

## Required Artifacts

| Artifact | Provides | Status | Details |
| -------- | -------- | ------ | ------- |
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` | Round-trip consumer (EXEC-02..10) | VERIFIED | 207 lines, fully implemented, wired via DI registration + runtime bind; data flows through real L2 read/write. |
| `src/BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs` | SSRF-locked Json.Schema validator (EXEC-03/05) | VERIFIED | 85 lines; SSRF cctor + Evaluate guard; called at consumer `:101,133`. |
| `src/BaseProcessor.Core/Processing/ProcessResult.cs` | OutputData-only record (EXEC-06) | VERIFIED | `record ProcessResult(string OutputData)`; consumed by consumer + seam. |
| `src/BaseProcessor.Core/Processing/BaseProcessor.cs` | abstract ProcessAsync seam + internal ExecuteAsync invoker (EXEC-04) | VERIFIED | protected abstract + internal forwarder; invoked at consumer `:114`. |
| `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` | ExecutionDataTtlSeconds (CONFIG-02) | VERIFIED | `[ConfigurationKeyName("ExecutionDataTtl")]` default 300; bound + applied. |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` | bind-then-MarkHealthy (EXEC-01) | VERIFIED | `IReceiveEndpointConnector` ctor dep + bind block `:148-159`. |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` | consumer DI registration + options binding | VERIFIED | `AddConsumer<EntryStepDispatchConsumer>().ExcludeFromConfigureEndpoints()` (73); `Configure<ProcessorLivenessOptions>(GetSection("Processor"))` (77). |

## Key Link Verification

| From | To | Via | Status |
| ---- | -- | --- | ------ |
| Consumer | `ProcessAsync` seam | `processor.ExecuteAsync(inputData, dispatch.Payload, ct)` (`:114`) -> internal forwarder | WIRED |
| Consumer | L2 input | `db.StringGetAsync(L2ProjectionKeys.ExecutionData(entryId))` (`:78`) | WIRED |
| Consumer | L2 output | `db.StringSetAsync(...ExecutionData(newEntryId), expiry: TTL)` (`:144`) | WIRED |
| Consumer | orchestrator-result queue | `sendProvider.GetSendEndpoint("queue:orchestrator-result")` + per-result `Send` (`:158-164`) | WIRED |
| Startup orchestrator | consumer endpoint | `endpointConnector.ConnectReceiveEndpoint($"{Id:D}", ...ConfigureConsumer<EntryStepDispatchConsumer>)` (`:149-153`) | WIRED |
| DI composition | consumer + options | `AddConsumer<...>().ExcludeFromConfigureEndpoints()` + `Configure<ProcessorLivenessOptions>` (`:73,77`) | WIRED |

## Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
| -------- | ------------- | ------ | ------------------ | ------ |
| EntryStepDispatchConsumer | `inputData` | `db.StringGetAsync` of real L2 key (existence-checked) | Yes (real Redis read; `DispatchOutputWriteFacts` exercise localhost:6380) | FLOWING |
| EntryStepDispatchConsumer | `built` ExecutionResults | per-result mint + `db.StringSetAsync` output write + builder | Yes (real Redis write + TTL asserted via `KeyTimeToLiveAsync`) | FLOWING |
| ExecutionResult.CorrelationId | `d.CorrelationId` | dispatch body | Yes (`DispatchCorrelationFacts` asserts flow to every result) | FLOWING |

## Behavioral Spot-Checks

| Behavior | Command | Result | Status |
| -------- | ------- | ------ | ------ |
| BaseProcessor.Core compiles clean | `dotnet build ... -c Debug` | Build succeeded, 0 Warning / 0 Error | PASS |
| Full test suite green (incl. processor slice + real-Redis facts) | `dotnet test tests/BaseApi.Tests/...` | 387 passed, 0 failed, 0 skipped | PASS |
| Real-Redis output-write + TTL | (covered by `DispatchOutputWriteFacts` in the suite, localhost:6380) | passed within suite | PASS |
| Bind-then-MarkHealthy ordering | (covered by `DispatchBindSequenceFacts`) | passed within suite | PASS |

## Anti-Patterns Found

None. The consumer is fully implemented (no TODO/placeholder/`return null` stubs). The `inputData = string.Empty` and `EntryId = Guid.Empty` assignments are deliberate no-input-source / failed-result semantics (EXEC-02/05), not hollow defaults - each is overwritten or is the documented business value. No empty-handler or console-log-only patterns.

## Deferred Items

| # | Item | Addressed In | Evidence |
| - | ---- | ------------ | -------- |
| 1 | Live `ConnectReceiveEndpoint`-against-RabbitMQ bind proof + Healthy-after-bind ordering against a real broker; full live orchestrator -> Processor.Sample -> orchestrator E2E round-trip | Phase 28 | Phase 28 SC #4 ("A real-stack E2E proves the live round-trip ... and proves the liveness-gated Start path"); locked decision in `27-03-SUMMARY` key-decisions (TEST-01) - the unit test proves the SEQUENCING via a fake `IReceiveEndpointConnector`; the live broker proof is by-design Phase 28 work. |

Phase 27 proves the bind ORDERING structurally (`DispatchBindSequenceFacts`) and the round-trip outcome matrix against real Redis + an in-memory MassTransit harness. The live-broker bind and end-to-end advance are correctly scoped to Phase 28; this is a documented deferral, not a Phase 27 gap.

## Human Verification Required

None for Phase 27's scope. The live-stack behavior (real RabbitMQ queue declaration, orchestrator-advances-on-ExecutionResult) is an automated Phase 28 E2E (TEST-01), not a manual check, and is out of scope for this phase's verification.

## Gaps Summary

No gaps. Every claimed requirement is implemented in substantive, wired, data-flowing code with a dedicated proving test; the build is clean and the full suite (387/387) is green with the full compose stack up. The single deferral is explicitly owned by Phase 28 by locked design.

---

_Verified: 2026-06-02T05:28:55Z_
_Verifier: Claude (gsd-verifier)_
