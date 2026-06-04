---
phase: 31-idempotent-execution-exactly-once-effect
plan: 02
subsystem: messaging-contracts
tags: [contract-change, entryid, guid-to-string, content-addressing, type-ripple, hermetic-suite]

# Dependency graph
requires:
  - phase: 31-idempotent-execution-exactly-once-effect
    plan: 01
    provides: "MessageIdentity (ComputeH/HashBlob/HashManifest/EntryEntryId), L2ProjectionKeys.ExecutionData(string)/Flag(string), RetryOptions"
provides:
  - "string EntryId on IExecutionCorrelated + both wire contracts (was Guid)"
  - "string H init prop on EntryStepDispatch and ExecutionResult (empty until Plan 04)"
  - "IStepDispatcher.DispatchAsync(... string entryId ...) signature"
  - "InboundExecutionScopeConsumeFilter string EntryId guard (!IsNullOrEmpty)"
  - "EntryStepDispatchConsumer string EntryId guard (IsNullOrEmpty) + ExecutionData(string) shim path"
affects: [31-03, 31-04, 31-05]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Atomic mechanical-but-wide type ripple: contract type change + every production AND test ripple site land compile-green in ONE wave (RESEARCH Pitfall 1)"
    - "Empty-string sentinel \"\" replaces Guid.Empty as the no-input EntryId marker; never used as a content key"
    - "Transitional .ToString(\"D\") shim: a still-minted Guid local feeds the now-string ExecutionData(string)/BuildCompleted (Plan 03 replaces the mint with MessageIdentity.HashBlob)"

key-files:
  created: []
  modified:
    - src/Messaging.Contracts/IExecutionCorrelated.cs
    - src/Messaging.Contracts/EntryStepDispatch.cs
    - src/Messaging.Contracts/ExecutionResult.cs
    - src/Orchestrator/Dispatch/IStepDispatcher.cs
    - src/Orchestrator/Dispatch/StepDispatcher.cs
    - src/Orchestrator/Scheduling/WorkflowFireJob.cs
    - src/Orchestrator/Consumers/ResultConsumer.cs
    - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
    - src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs
    - tests/BaseApi.Tests/Orchestrator/EntryStepDispatchTests.cs
    - tests/BaseApi.Tests/Orchestrator/ExecutionResultContractTests.cs
    - tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs
    - tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs
    - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
    - tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs
    - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
    - tests/BaseApi.Tests/Processor/DispatchInputFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchResultSendFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchCorrelationFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchAckSemanticsFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchInvokeFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchOutputWriteFacts.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs
    - tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs

key-decisions:
  - "Failed/Cancelled 'no output' sentinel pinned as \"\" (empty string), mirroring the old Guid.Empty — never used as a content key (Open Q1)"
  - "H added to both concrete contracts directly, NOT to IExecutionCorrelated (keeps the interface minimal, RESEARCH/PATTERNS note)"
  - "ExecutionData(Guid) overload from Plan 01 NOT removed this plan — it is not in files_modified and no longer referenced by any caller (all test seeds + the consumer now pass strings); leaving it is harmless and out-of-scope, removed later if desired. Avoids touching L2ProjectionKeys.cs"
  - "Test entryId locals rendered via Guid.NewGuid().ToString(\"D\") so both the L2 seed key (ExecutionData(string)) and the dispatch EntryId share one byte-identical string content address"

requirements-completed: [req-1, req-2]

# Metrics
duration: 14min
completed: 2026-06-04
---

# Phase 31 Plan 02: EntryId Guid->string + H Contract Ripple Summary

**The mechanical-but-wide `EntryId` Guid->string re-typing plus a new empty-default `H` field, landed compile-green across the 3 wire contracts, every production ripple site (dispatcher signature, fire-job placeholder, processor guard/shim, execution-scope filter), and ~17 test files — full hermetic suite green (426/0), behavior byte-equivalent via placeholder/shim values for Plans 03/04 to inject real hashes.**

## Performance

- **Duration:** ~14 min
- **Started:** 2026-06-04T12:53:17Z
- **Completed:** 2026-06-04T13:07:31Z
- **Tasks:** 2
- **Files modified:** 25 (0 created, 25 modified)

## Accomplishments
- **Contracts:** `IExecutionCorrelated.EntryId` is now `string`; `EntryStepDispatch` and `ExecutionResult` each carry `string EntryId { get; init; } = ""` and a new `string H { get; init; } = ""`. The interface stays minimal (H lives on the two concrete records).
- **Production ripple (all compile-green, behavior unchanged):**
  - `IStepDispatcher`/`StepDispatcher`: `entryId` param `Guid` -> `string` (assignment now string-to-string).
  - `WorkflowFireJob`: the entry-step dispatch passes `""` for entryId (was `Guid.Empty`); Plan 04 replaces with `MessageIdentity.EntryEntryId` + threads `H`.
  - `ResultConsumer`: `m.EntryId` flows through as a string automatically (no edit needed).
  - `EntryStepDispatchConsumer`: source-step guard `== Guid.Empty` -> `string.IsNullOrEmpty(dispatch.EntryId)`; the output write + `BuildCompleted` feed `newEntryId.ToString("D")` into the now-string `ExecutionData(string)`/`BuildCompleted(string)` (TEMPORARY shim — the local stays a minted Guid this plan; Plan 03 swaps in `MessageIdentity.HashBlob`); `BuildFailed`/`BuildCancelled` `EntryId = ""`.
  - `InboundExecutionScopeConsumeFilter`: `!= Guid.Empty` -> `!string.IsNullOrEmpty(ec.EntryId)`, assigning `ec.EntryId` verbatim (no `.ToString()`). The four Guid id-guards (Workflow/Step/Processor/Execution) are unchanged.
- **Test ripple (compile + green):** ~17 test files re-typed to the string contract — `DispatchTestKit.Dispatch` takes `string entryId` (default `""`); the 6 `Dispatch*Facts` render their entryId locals via `.ToString("D")` so the L2 seed key and the dispatch EntryId share one byte-identical content address; `EntryStepDispatchScope/Runtime` use the `""` source-step sentinel; the output-write `Guid.Empty` assertions become `string.IsNullOrEmpty`/`Equal("")`; the contract tests round-trip the string EntryId + assert the `H` default; `ConsoleExecutionScopeFilterTests.ExecProbeMessage.EntryId` is re-typed to `string` and a new `Case_D` proves the empty-string EntryId is skipped by the filter guard.
- **Verification:** `dotnet build SK_P.sln -c Debug` 0 Warning / 0 Error; `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack"` = **Passed 426 / Failed 0** (zero regression).

## Task Commits

Each task was committed atomically:

1. **Task 1: re-type the 3 contracts + add H; production ripple** — `4e8206c` (feat)
2. **Task 2: re-type the EntryId-as-Guid test assertions to string** — `735dffd` (test)

**Plan metadata:** committed separately (docs).

## Decisions Made
- **Failed/Cancelled sentinel = `""`** (Open Q1): a Failed/Cancelled result short-circuits before any manifest read; `""` mirrors the old `Guid.Empty` and is never used as a content key.
- **`H` on the concrete records, not the interface** — keeps `IExecutionCorrelated` minimal per RESEARCH/PATTERNS.
- **Kept `L2ProjectionKeys.ExecutionData(Guid)`** — it is NOT in this plan's `files_modified`, and after the ripple no caller references it (all test seeds + the consumer now pass strings). Leaving the unused transitional overload is harmless and avoids touching `L2ProjectionKeys.cs`; it can be removed in a later cleanup. (The Plan-01 SUMMARY's "removed in Plan 02" wording predates the finalized 31-02 PLAN, whose Task 1 `<action>` uses the string overload via shim and does not list `L2ProjectionKeys.cs`.)
- **`.ToString("D")` for test entryId locals** — yields `skp:data:{guid:D}` from `ExecutionData(string)`, byte-identical to the old Guid-overload key, so each test's L2 seed and dispatch EntryId still match.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Re-typed `ResultAckTests.cs` DispatchAsync arg matchers (file absent from the plan's test inventory)**
- **Found during:** Task 2 (full-solution build)
- **Issue:** The plan's `files_modified` + Task 2 `<files>` list did NOT include `tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs`, but it has 5 `dispatcher.DispatchAsync(... Arg.Any<Guid>(), Arg.Any<CancellationToken>())` call-spec matchers whose 7th argument (entryId) bound to `Guid`. After the `IStepDispatcher` signature change to `string entryId`, these 5 sites failed to compile (CS1503), breaking `dotnet build SK_P.sln`. The plan's own objective mandates the whole tree builds compile-green in ONE wave, so leaving this for "later" is exactly the Pitfall-1 failure the plan forbids.
- **Fix:** Changed the 7th-position `Arg.Any<Guid>()` to `Arg.Any<string>()` in all 5 DispatchAsync matchers (4 `DidNotReceive`/`Received` verifications + 1 `.Returns` throw-stub). No behavioral change — the matchers still match any entryId.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
- **Verification:** `dotnet build SK_P.sln -c Debug` 0/0 afterwards; the 4 ResultAck facts pass in the 426/0 hermetic run.
- **Committed in:** `735dffd` (Task 2 commit)

**2. [Rule 2 - Missing coverage] Added `Case_D` empty-string EntryId skip proof to ConsoleExecutionScopeFilterTests**
- **Found during:** Task 2 (per the plan's instruction to "assert a non-empty string value flows when set" and that "the scope skips empty EntryId ... use `""` instead of `Guid.Empty`")
- **Issue:** The existing Case B proves a `Guid.Empty` ExecutionId is skipped (a Guid id, unchanged), but after the EntryId type change there was no direct proof that the NEW `!string.IsNullOrEmpty(ec.EntryId)` guard skips an empty-string EntryId. The plan asked for that assertion.
- **Fix:** Added `Case_D_Empty_String_EntryId_Is_Skipped` — publishes an `ExecProbeMessage` with `EntryId = ""` and asserts the `ExecutionLogScope.EntryId` key is ABSENT while the other four ids are present. Case A/B updated to assert the string EntryId is stored verbatim (no `.ToString()`).
- **Files modified:** tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs
- **Verification:** Case_D passes in the 426/0 hermetic run (the suite count rose to 426).
- **Committed in:** `735dffd` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (1 Rule 3 - blocking, 1 Rule 2 - missing coverage)
**Impact on plan:** Both were required to satisfy the plan's own "ONE compile-green wave" objective and its explicit Console-test instruction. No scope creep — no protocol logic changed; every site keeps its current behavior with placeholder/shim values.

## Threat Surface
No new security surface introduced. The change is a field RE-TYPE across the existing MassTransit/RabbitMQ envelope boundary (threat-model T-31-04/05/06) — no new endpoint, auth path, or trust boundary. EntryId remains a server-derived value placed only as a scope VALUE under the fixed `ExecutionLogScope.EntryId` key (T-31-05 precedent preserved through the Guid->string change).

## Known Stubs
- `WorkflowFireJob` dispatches `entryId = ""` and threads no `H` — INTENTIONAL placeholder; Plan 04 (req-2) replaces it with `MessageIdentity.EntryEntryId(correlationId, entryStepId)` + the real `H`. Marked in `FireDispatchTests.cs` with `// Plan 04 changes this to the non-empty hash (req-2)`.
- `EntryStepDispatchConsumer` mints `newEntryId = NewId.NextGuid()` and feeds `.ToString("D")` to `ExecutionData(string)` — INTENTIONAL transitional shim; Plan 03 replaces the mint with `MessageIdentity.HashBlob` (a real 64-hex content address).
These stubs are by-design for this wave (the plan objective is the type ripple only; Plans 03/04 inject real hashes) and do not block the plan's goal (a compile-green tree on the string contract).

## Issues Encountered
- The plan's test-file inventory was slightly inaccurate: two files were listed under `tests/BaseApi.Tests/Contracts/` but actually live under `tests/BaseApi.Tests/Orchestrator/` (`EntryStepDispatchTests.cs`, `ExecutionResultContractTests.cs`), and `ResultAckTests.cs` (a real ripple site) was omitted. Resolved by building the full solution and letting the compiler surface every site, then fixing each (see Deviations).

## Next Phase Readiness
- The whole solution compiles 0/0 on the string `EntryId` + `H` contract; the full hermetic suite is green (426/0). This is the stable contract precondition the plan objective targets.
- Plan 03 can now make the processor receiver a pure logic change: replace the `NewId.NextGuid()` mint + `.ToString("D")` shim with `MessageIdentity.HashBlob`, drop the `IsNullOrEmpty` source-step branch in favor of `InputDefinition == null`, and wire the effect-first flag dedup — all against the now-string `EntryId`/`H` contract.
- Plan 04 can replace `WorkflowFireJob`'s `""` placeholder with the real `EntryEntryId` hash and thread `H`, then flip the `FireDispatchTests` `EntryId == ""` assertion to the non-empty-hash assertion (req-2).

## Self-Check: PASSED
- FOUND: .planning/phases/31-idempotent-execution-exactly-once-effect/31-02-SUMMARY.md
- FOUND commit 4e8206c (Task 1) — `string EntryId` in IExecutionCorrelated.cs, `string H` in EntryStepDispatch.cs
- FOUND commit 735dffd (Task 2)
- Full solution build 0/0; hermetic suite 426 passed / 0 failed
</content>
</invoke>
