---
phase: 27-execution-round-trip
plan: 02
subsystem: processor
tags: [masstransit, consumer, redis, l2, json-schema, round-trip, business-ack, infra-throw]

# Dependency graph
requires:
  - phase: 27-execution-round-trip
    plan: 01
    provides: "ProcessorJsonSchemaValidator.TryValidate (SSRF-locked), ProcessResult(string OutputData), BaseProcessor.ExecuteAsync invoker seam, ProcessorLivenessOptions.ExecutionDataTtlSeconds (CONFIG-02 TTL)"
provides:
  - "EntryStepDispatchConsumer — the framework IConsumer<EntryStepDispatch> running the processor-half round-trip: L2 input read+validate -> ProcessAsync seam -> per-result output-validate/mint/L2-write(TTL) -> one-by-one Send -> ack-after-send (business-ack / infra-throw)"
affects: [27-03 (runtime ConnectReceiveEndpoint bind + Immediate(3) retry posture wires THIS consumer), 28-sourcehash-sample-e2e]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Symmetric mirror of Orchestrator.ResultConsumer (IConsumer<T> shape, `using ExecutionResult =` alias, business-ack-by-return / infra-throw split)"
    - "Business-outcome ExecutionResult Sends use CancellationToken.None so the signal survives a tripped inbound dispatch token"
    - "L2 OUTPUT-WRITE is INFRA (no catch) — propagates so a transient Redis blip retries the whole dispatch rather than emitting a Completed with no data behind it (D-15)"

key-files:
  created:
    - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
    - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
    - tests/BaseApi.Tests/Processor/DispatchInputFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchInvokeFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchOutputWriteFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchResultSendFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchAckSemanticsFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchCorrelationFacts.cs
  modified: []

key-decisions:
  - "Business-outcome ExecutionResult Sends are issued with CancellationToken.None (Rule 1 bug fix): a Cancelled/Failed result is the business SIGNAL and must reach the orchestrator even when the inbound dispatch token has tripped; the inbound ct governs only the ProcessAsync transform, not result delivery. An infra Send fault still propagates (D-15)."
  - "The L2 output-write call StringSetAsync(key, value, expiry: TimeSpan) binds (under SE.Redis 2.13.1) to the Expiration/ValueCondition overload (TimeSpan implicitly converts to Expiration) — the infra-throw test stub configures that overload (plus the bool-keepTtl and When overloads defensively)."
  - "DispatchOutputWriteFacts drives the real-Redis WRITE path via a no-input source processor (empty InputDefinition + Guid.Empty entryId) so the consumer skips the L2 READ and exercises only the WRITE + TTL against localhost:6380; every minted skp:data key is _redis.Track'd for net-zero teardown."

patterns-established:
  - "Framework owns ALL outcomes: ProcessResult carries only OutputData; the consumer mints executionId/entryId, classifies pass->Completed (mint+write+TTL) / fail->Failed (Guid.Empty, nothing written) / empty-list->ack / cancel->Cancelled / exception->Failed."
  - "Round-trip closes through L2, never on the wire: output written to L2[data(newEntryId)], newEntryId flows onto ExecutionResult.EntryId; ExecutionResult has no output field."

requirements-completed: [EXEC-02, EXEC-03, EXEC-04, EXEC-05, EXEC-06, EXEC-07, EXEC-08, EXEC-09, EXEC-10, CONFIG-02]

# Metrics
duration: 13min
completed: 2026-06-01
---

# Phase 27 Plan 02: EntryStepDispatchConsumer (Execution Round-Trip) Summary

**Built `EntryStepDispatchConsumer` — the framework's `IConsumer<EntryStepDispatch>` that runs the full processor-half round-trip (read+validate L2 input with Payload-as-config -> invoke the `ProcessAsync` seam -> per-result output-validate/mint/L2-write-with-TTL/build -> one-by-one Send to `queue:orchestrator-result` -> ack-after-send) with the locked business-ack / infra-throw discipline, proven green by a six-file outcome-matrix.**

## Performance
- **Duration:** ~13 min
- **Started:** 2026-06-01T20:59:03Z
- **Completed:** 2026-06-01T21:11:46Z
- **Tasks:** 2
- **Files created:** 8 (1 consumer + 7 test files)

## Accomplishments
- `EntryStepDispatchConsumer` mirrors `Orchestrator.ResultConsumer` (the `using ExecutionResult =` alias, the business-ack-by-return / infra-throw split) and implements the full EXEC-02..EXEC-10 + CONFIG-02 pipeline:
  - **Input (EXEC-02/03, D-07):** read from `L2[data(entryId)]`, existence-checked; `Payload` is config, never input. No-input source (`InputDefinition` empty + `EntryId == Guid.Empty`) skips the L2 read and passes `inputData = ""`. Required-input + missing/empty entryId -> single `Failed` BEFORE `ProcessAsync`. Non-empty input failing its definition -> single `Failed`.
  - **Invoke (EXEC-04):** calls `processor.ExecuteAsync(inputData, dispatch.Payload, ct)` (the Plan 01 internal seam).
  - **Per result (EXEC-05/06, D-09/D-11/D-13):** output-validate -> pass: mint `newEntryId`, write `L2[data(newEntryId)] = OutputData` with the CONFIG-02 TTL, mint `executionId`, `Completed` with `EntryId = newEntryId`; fail: `Failed`, `EntryId = Guid.Empty`, nothing written (mixed batches yield mixed outcomes).
  - **Outcomes (EXEC-08):** empty list -> ack with no message; `OperationCanceledException` -> single `Cancelled`; any other exception -> single `Failed` carrying `ex.Message`.
  - **Send + ack (EXEC-07/09, D-14/D-15):** one-by-one `Send` to `queue:orchestrator-result`; ack only after all sends. Missing-input / validation-fail / empty / caught-exception are business-acked; an L2 read fault, the L2 OUTPUT-WRITE fault, and a `Send` fault are infra and PROPAGATE.
  - **Correlation (EXEC-10):** body `CorrelationId` copied onto every `ExecutionResult`.
- Six Wave-0 fact files prove the full outcome matrix (37+2 = 39 Processor-slice tests green), including a real-localhost:6380 output-write + TTL assertion (`KeyTimeToLiveAsync`) with net-zero `_redis.Track` teardown, and `Assert.ThrowsAsync<RedisConnectionException>` for BOTH the output-write-fault and input-read-fault infra cases.

## Task Commits
1. **Task 1: EntryStepDispatchConsumer round-trip orchestration** - `da4e073` (feat)
2. **Task 2: Wave-0 dispatch outcome-matrix facts + cancelled-result delivery fix** - `942fe57` (test, includes the Rule 1 consumer fix)

**Plan metadata:** final docs commit (this SUMMARY + STATE + ROADMAP).

## Files Created
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` - the framework round-trip consumer (mirror of ResultConsumer).
- `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` - shared kit: configurable fake `BaseProcessor`, `CapturingSendProvider` (NSubstitute), in-memory result harness builder, `PresentReadWriteFaultL2` (read-OK / write-throws Redis stub), consumer/dispatch builders.
- `tests/BaseApi.Tests/Processor/DispatchInputFacts.cs` - EXEC-02/03: missing-required-input Fails-before-invoke; empty-def+empty-entryId invokes with `""` and NO L2 read; present input + passing def invokes with L2 value + Payload-as-config; failing def Fails-before-invoke.
- `tests/BaseApi.Tests/Processor/DispatchInvokeFacts.cs` - EXEC-04/06: invoked with `(inputData, config)`; distinct per-result `ExecutionId` mint.
- `tests/BaseApi.Tests/Processor/DispatchOutputWriteFacts.cs` - EXEC-05 (real Redis): pass writes L2 + TTL + Completed; fail writes nothing (Guid.Empty); mixed batch writes only the valid output.
- `tests/BaseApi.Tests/Processor/DispatchResultSendFacts.cs` - EXEC-07/08 (harness): empty=ack-no-message; one-by-one (3 individual messages); Cancelled always sent; caught-exception -> Failed with message.
- `tests/BaseApi.Tests/Processor/DispatchAckSemanticsFacts.cs` - EXEC-09/D-15: output-write-fault propagates, input-read-fault propagates, business-failure does NOT throw.
- `tests/BaseApi.Tests/Processor/DispatchCorrelationFacts.cs` - EXEC-10: body CorrelationId flows to every result.

## Decisions Made
- **Cancelled/Failed business results are sent with `CancellationToken.None`** (see Deviations — Rule 1). The inbound dispatch token governs the transform; the result is the business signal that must always reach the orchestrator.
- **Infra output-write classification confirmed against SE.Redis overload resolution.** The `expiry:`-named write binds to the `Expiration/ValueCondition` overload in 2.13.1; the infra-throw stub targets that overload (and the keepTtl/When overloads defensively) so the D-15 propagation is genuinely exercised.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Cancelled/Failed business result was swallowed when the inbound dispatch token tripped**
- **Found during:** Task 2 (`DispatchResultSendFacts.Cancelled_Always_Sent`)
- **Issue:** The plan's structure sent every `ExecutionResult` (including the early `Cancelled`/`Failed` business returns) with the inbound `ctx.CancellationToken`. When `ProcessAsync` was cancelled (token already tripped), `endpoint.Send(result, ct)` re-threw `OperationCanceledException` BEFORE delivering the `Cancelled` result — so the orchestrator would never learn the step cancelled (the business signal was lost, and the consumer would escalate to a retry/`_error` on a purely business outcome). This violates EXEC-08 ("Cancelled always sent") and the business-ack discipline (D-15).
- **Fix:** All `ExecutionResult` Sends now use `CancellationToken.None` (both the `SendOne` early-return helper and the main one-by-one loop). The inbound `ct` still governs only the `ProcessAsync` transform invocation. An infra `Send` fault still propagates (D-15 unchanged).
- **Files modified:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs`
- **Verification:** `DispatchResultSendFacts.Cancelled_Always_Sent` green (one `Cancelled` ExecutionResult delivered); full Processor slice 39/39 green.
- **Committed in:** `942fe57` (Task 2 commit)

**2. [Rule 3 - Blocking] Infra-throw test stub had to target the actual bound `StringSetAsync` overload**
- **Found during:** Task 2 (`DispatchAckSemanticsFacts.OutputWriteFault_Propagates`)
- **Issue:** The output-write `StringSetAsync(key, value, expiry: TimeSpan)` call binds (SE.Redis 2.13.1) to the `StringSetAsync(RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags)` overload (`TimeSpan` implicitly converts to `Expiration`), NOT the `TimeSpan?`-expiry overloads. The initial stub only faulted the `TimeSpan?` overloads, so the substitute never threw and the infra-propagation proof failed (Assert.ThrowsAsync saw no exception).
- **Fix:** `PresentReadWriteFaultL2` now faults the `Expiration/ValueCondition` overload (via `When/Do`), plus the `bool keepTtl` 6-arg and `When` 4/5-arg overloads defensively, so the stub is robust to overload resolution.
- **Files modified:** `tests/BaseApi.Tests/Processor/DispatchTestKit.cs`
- **Verification:** `OutputWriteFault_Propagates` green (`RedisConnectionException` propagates).
- **Committed in:** `942fe57` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (1 bug, 1 blocking-test-harness). Both necessary for correctness/coverage; no scope creep. The Rule 1 fix is load-bearing for EXEC-08.

## At-Least-Once Tradeoff (D-15, documented per plan)
Throwing mid-batch (an L2 output-write fault or a `Send` fault) re-runs the whole dispatch on the `Immediate(3)` retry (wired in Plan 03), re-sending already-sent results. The orchestrator's `ResultConsumer` is L1-idempotent (an already-advanced step / unknown id is a graceful business-ack), so the duplicate is safe. The never-lose-output guarantee is preferred over never-duplicate.

## Issues Encountered
- The MTP/xUnit v3 runner ignores VSTest-style `--filter "FullyQualifiedName~..."`; scoped runs use the MTP filter `-- --filter-namespace BaseApi.Tests.Processor` / `--filter-class <FQN>`, and per-test failure detail is only visible by running the built `BaseApi.Tests.exe` directly (the `dotnet test` wrapper suppresses it to a UTF-16 log).
- `DispatchOutputWriteFacts` requires the host-side compose Redis at `localhost:6380` (RedisFixture, D-11) — it was running; the three facts passed and Track'd their keys for net-zero teardown.

## Known Stubs
None — the consumer is fully implemented. Runtime registration (the `ConnectReceiveEndpoint` bind + `Immediate(3)` retry posture + DI wiring of the consumer with no static endpoint) is Plan 03, as designed by the wave ordering.

## Threat Flags
None — no new security surface beyond the plan's `<threat_model>`. The consumer builds Redis keys only via `L2ProjectionKeys.ExecutionData(Guid)` (T-27-06 mitigated), validates input/output through the SSRF-locked `ProcessorJsonSchemaValidator` (T-27-04/05), and the output-write infra-throw realizes T-27-08.

## Next Phase Readiness
- Plan 03 binds `EntryStepDispatchConsumer` at the runtime `queue:{Id:D}` endpoint AFTER Loop B and BEFORE `MarkHealthy` (D-02/D-03), applies `UseMessageRetry(r => r.Immediate(3))`, and registers the consumer WITHOUT a static auto-bound endpoint (D-01). All collaborators the consumer's ctor needs (`IConnectionMultiplexer`, `IProcessorContext`, `BaseProcessor`, `IOptions<ProcessorLivenessOptions>`, `ISendEndpointProvider`, `ILogger`) are already in the `AddBaseProcessor` / `AddBaseConsole` composition root.
- No blockers.

---
*Phase: 27-execution-round-trip*
*Completed: 2026-06-01*

## Self-Check: PASSED

All 8 listed files exist on disk; both task commits (`da4e073`, `942fe57`) exist in git history.
