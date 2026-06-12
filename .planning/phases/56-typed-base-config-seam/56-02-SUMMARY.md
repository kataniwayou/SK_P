---
phase: 56-typed-base-config-seam
plan: 02
subsystem: api
tags: [base-processor, config-seam, hermetic-tests, system-text-json, generic-base, phase-gate]

# Dependency graph
requires:
  - phase: 56-typed-base-config-seam
    plan: 01
    provides: "Typed seam BaseProcessor<TConfig> + ProcessorConfig marker + canonical SerializerOptions; old raw-string author seam removed (test project left non-compiling)"
provides:
  - "Hermetic test suite migrated to the typed-config seam (no (string,string) override remains anywhere)"
  - "New deser-failure->StepFailed fact proving a malformed payload through a real BaseProcessor<TConfig> yields exactly one business StepFailed, no Keeper send (Req 4a / T-56-01)"
  - "Phase 56 closing gate passed: 0-warning Release + Debug build, full hermetic suite green (530/530)"
affects: [57-startup-config-schema-fetch-gate-a]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Test doubles derive from the generic base (BaseProcessor<TConfig>) because the non-generic internal ExecuteAsync is not InternalsVisibleTo the test assembly"
    - "Field-less DummyConfig keeps the pipeline-double FakeProcessor deserialize-inert under ignore-unknown options"
    - "Reflection invoke of the typed protected ProcessAsync passing a real TConfig instance (the hermetic equivalent of the framework's post-deserialize forwarder)"
    - "Deserialize-fault proof: a REAL BaseProcessor<TConfig> subclass + malformed payload through RunAsync surfaces the JsonException at the pipeline catch-all"

key-files:
  created: []
  modified:
    - tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
    - tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs
    - tests/BaseApi.Tests/Processor/PipelineInFacts.cs

key-decisions:
  - "Removed FakeProcessor.LastConfig raw-payload capture (no fact asserted it; the double now receives a deserialized DummyConfig, not a raw string) — capture became meaningless under the typed seam"
  - "Dispatch default payload left as {\"cfg\":1} — deserializes harmlessly into the field-less DummyConfig under ignore-unknown, keeping every Pre/In/Post/recovery/end-delete fact deser-inert"
  - "The new deser-failure fact uses a REAL BaseProcessor<TConfig> (not a fake) so the framework actually runs JsonSerializer.Deserialize and a malformed payload throws inside ExecuteAsync before the transform body"
  - "Verified the gate with the xUnit v3 MTP runner natively (--filter-not-trait Category=RealStack + TRX report) because dotnet test's --filter is silently ignored under Microsoft.Testing.Platform (MTP0001) and its console summary is suppressed by the MSBuild integration"

patterns-established:
  - "Hermetic test doubles for the typed seam derive from BaseProcessor<TConfig> with a trivial local config record"
  - "Deserialize-failure coverage drives a real generic subclass through the pipeline rather than mocking the JsonException"

requirements-completed: [CFG-01, CFG-02]

# Metrics
duration: 36min
completed: 2026-06-12
---

# Phase 56 Plan 02: Hermetic Suite Migration + Phase Gate Summary

**Migrated the four affected hermetic test files from the removed raw-string seam to the typed-config seam (test doubles now derive from `BaseProcessor<TConfig>`), added the genuinely-new framework-deserialize-failure -> single `StepFailed` fact (Req 4a / T-56-01), and passed the blocking phase gate: 0-warning Release + Debug build and a green 530/530 hermetic suite, closing the Plan-01 clean break.**

## Performance

- **Duration:** ~36 min (most of it the slow full-suite run — broker-down MassTransit retry-backoff paths dominate hermetic runtime)
- **Started:** 2026-06-12T14:50:03Z
- **Completed:** 2026-06-12T15:25:59Z
- **Tasks:** 3 (2 source-editing + 1 verification-only gate)
- **Files modified:** 4 (all in tests/BaseApi.Tests/Processor)

## Accomplishments

- **Task 1 — non-generic doubles migrated:** `BaseProcessorSeamFacts.TestProcessor` and `DispatchTestKit.FakeProcessor` were `: BaseProcessor` overriding the removed `ProcessAsync(string,string,ct)`; both now derive from the generic `BaseProcessor<TConfig>` (`TestConfig` / field-less `DummyConfig`) and override the typed `ProcessAsync(string, TConfig?, ct)`. The DI-resolution assertion (resolve as the non-generic base, `Assert.IsType<TestProcessor>`) still holds because `TestProcessor : BaseProcessor<TestConfig> : BaseProcessor`. Removed the now-meaningless `LastConfig` raw-payload capture; kept `Invoked`/`LastInputData` (the only asserted state, at `PipelinePreFacts.cs:41`).
- **Task 2 — typed SampleProcessor facts + deser-failure fact:** `SampleProcessorFacts.InvokeProcessAsync` now reflects the typed `ProcessAsync(string, SampleConfig?, ct)` and passes real `SampleConfig` instances (`new SampleConfig("StepA1")` object-shape, `(SampleConfig?)null` blank, `new SampleConfig("fail")`); the three migrated facts assert echo (`"StepA1"`), null-config fallback (`"processor-sample-ok"`), and the sync `FailedException` (via `TargetInvocationException`). Added `PipelineInFacts.MalformedPayload_DeserFailure_Emits_Single_StepFailed`: a REAL `BaseProcessor<DeserConfig>` subclass driven with `"not json"` through `RunAsync` -> exactly one `StepFailed`, empty `SentKeeper` (Req 4a — the framework-deserialize-failure path uncovered before this plan).
- **Task 3 — phase gate (BLOCKING):** `dotnet build SK_P.sln` is **0-warning in Release AND Debug**; the full hermetic suite (excluding `RealStack`) is **green: 530 total / 530 passed / 0 failed / 0 error** (TRX `outcome=Completed`, runner exit 0). The old `(string,string)` seam is absent from both `src` and `tests` (grep = 0). All 7 SPEC acceptance-criteria boxes are satisfiable from this automated evidence.

## Task Commits

1. **Task 1: Migrate non-generic test doubles to the generic typed-config base** - `9eca160` (test)
2. **Task 2: Typed-seam SampleProcessor facts + deser-failure StepFailed fact** - `21e6af4` (test)
3. **Task 3: Phase gate (verification-only, no source edits)** - no commit (build + test evidence recorded here)

## Files Created/Modified

- `tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs` (modified) - `TestProcessor : BaseProcessor<TestConfig>`; `record TestConfig(string? V) : ProcessorConfig`; XML-doc retyped to the typed seam; DI-resolution fact preserved (`InvokeAsync("input", new TestConfig("config"), ct)`).
- `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` (modified) - `FakeProcessor : BaseProcessor<DummyConfig>` (field-less `DummyConfig`); typed `ProcessAsync` impl delegates; removed `LastConfig`; `Dispatch` default `{"cfg":1}` unchanged (deser-inert).
- `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` (modified) - reflection helper passes `SampleConfig?`; three facts feed typed configs (object-shape / null / fail); XML-doc retyped to "framework deserializes, fact passes a typed SampleConfig".
- `tests/BaseApi.Tests/Processor/PipelineInFacts.cs` (modified) - added `record DeserConfig`, `RealDeserProcessor : BaseProcessor<DeserConfig>`, and `MalformedPayload_DeserFailure_Emits_Single_StepFailed`.

## Decisions Made

- Dropped `FakeProcessor.LastConfig` (raw-payload capture) — under the typed seam the double receives a deserialized `DummyConfig`, not a raw string, so the capture was meaningless and no fact read it.
- Kept the `Dispatch` default payload `{"cfg":1}` so every existing pipeline-double fact stays deserialize-inert (field-less `DummyConfig` under ignore-unknown).
- Drove the deser-failure proof through a REAL generic subclass (not a mock) so the framework's actual `JsonSerializer.Deserialize` throws — proving the JsonException reaches `ProcessorPipeline.cs:241` and maps to a single business `StepFailed`, never a Keeper/infra route (D-03).

## Deviations from Plan

### Tooling adaptations (no source/behavioral deviation)

**1. [Rule 3 - Blocking] `dotnet test --filter` is silently ignored under Microsoft.Testing.Platform; its console summary is suppressed by the MSBuild integration**
- **Found during:** Task 2 and Task 3 verification.
- **Issue:** The project uses xUnit v3 on Microsoft.Testing.Platform (MTP). `dotnet test ... --filter "Category!=RealStack"` emits `MTP0001` (VSTest-only property ignored) and the MSBuild task swallows the pass/fail summary, so the plan's verbatim verify command produced no readable result. Separately, leftover orphaned `testhost`/`BaseApi.Tests` processes from earlier collided runs held a lock on `bin/Debug/.../TestResults/*.log` and `*.dll`, producing `MSB3026` copy-retry warnings that would fail the Debug build under `TreatWarningsAsErrors`.
- **Fix:** Ran the gate via the xUnit v3 MTP runner natively — `BaseApi.Tests.exe --filter-class` (Task 2) and `--filter-not-trait Category=RealStack --report-xunit-trx` (Task 3) — and parsed the TRX counters + runner exit code for an authoritative result. Killed orphaned `testhost`/`BaseApi.Tests` processes and cleared the stale `TestResults` directory before the Debug build, then re-ran it clean (0-warning, 0 `MSB3026`).
- **Files modified:** None (test-execution mechanics only).
- **Verification:** Release 0-warning; Debug 0-warning (clean re-run); hermetic suite 530/530 passed, TRX `outcome=Completed`, exit 0.

---

**Total deviations:** 1 tooling adaptation (no source or behavioral change).
**Impact on plan:** The plan's semantic verify intent (0-warning Release+Debug, green non-RealStack hermetic suite) was met exactly; only the invocation mechanics changed to suit MTP and to clear orphaned-process file locks. No architectural changes, no auth gates, no scope creep.

## Issues Encountered

- Orphaned `testhost`/`BaseApi.Tests` processes from earlier collided/auto-backgrounded test runs held file locks on the test `bin` output, causing transient `MSB3026` copy-retry warnings on the first Debug build. Resolved by killing the orphaned processes and clearing `TestResults`, then re-running the Debug build clean (0-warning).

## Threat Surface

No new threat surface beyond the plan's `<threat_model>`. T-56-01 (malformed-payload deserialize at the framework seam) is now **proven mitigated** by `MalformedPayload_DeserFailure_Emits_Single_StepFailed` (one `StepFailed`, no Keeper send). T-56-02 (empty/whitespace -> null config) is proven by `ProcessAsync_Null_Config_Falls_Back_To_Fixed_Token`. T-56-03 (forgiving options residual) remains accepted by design and is incidentally demonstrated by the deser-inert field-less `DummyConfig`. No new network/auth/persistence surface; tests only.

## Known Stubs

None. All four test files exercise the real typed seam; no placeholder/empty-data stubs introduced. `RealDeserProcessor.ProcessAsync` returns an empty list deliberately (it is never reached on a malformed payload — documented inline).

## Next Phase Readiness

- The clean break is complete: production (Plan 01) and the hermetic suite (Plan 02) are both on the typed seam; the solution builds 0-warning in Release and Debug and the full hermetic suite is green. Phase 56 SPEC acceptance criteria are all satisfied.
- The canonical `ProcessorConfig.SerializerOptions` + the config *type* anchor are in place and test-proven for **Phase 57's Gate A** (config-type <-> config-schema compatibility), which will lift the D-05 carve-out and add the startup `ConfigSchemaId` fetch.

## Self-Check: PASSED

All 4 modified test files + the SUMMARY exist on disk; both task commits (9eca160, 21e6af4) are present in git history. Task 3 is verification-only (no commit by design). Gate evidence: Release/Debug 0-warning, hermetic suite 530/530 passed (TRX outcome=Completed, exit 0).

---
*Phase: 56-typed-base-config-seam*
*Completed: 2026-06-12*
