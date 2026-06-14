---
phase: 64-processor-work-structured-logging
plan: 01
subsystem: processor
tags: [dotnet, processor-sample, structured-logging, system-text-json, otel, xunit-v3]

# Dependency graph
requires:
  - phase: 56-typed-base-config-seam
    provides: "BaseProcessor<TConfig> typed-config seam + ProcessorConfig marker base + shared case-insensitive SerializerOptions"
provides:
  - "SampleConfig reshaped to (int Number, string? Label) — the {number,label} assignment-payload contract"
  - "ProcessAsync sum transform: sum = (config?.Number ?? 0) + Random.Shared.Next(0,100), emitted as a {number,label} JSON string in ProcessItem.Data"
  - "Exactly one structured LogInformation per execution carrying {StepLabel}=label verbatim + {Sum}=sum (no template interpolation; 6 ids ambient via consume-filter scope)"
  - "CapturingLogger extended to capture structured state KVPs (IReadOnlyList<KeyValuePair<string,object?>>) for hermetic param-value assertions"
affects: [65-fan-out-workflow-seeder, 66-prometheus-es-analyzer, fault-injection-harness, processor-sample-deployment]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Structured-log param safety: attacker-controllable label passed ONLY as {StepLabel} structured param, never interpolated (T-64-01 log-injection mitigation)"
    - "Null-config single code path: config?.Number ?? 0 guards the nullable seam config without a branch (warning-clean under Nullable=enable)"
    - "Hermetic structured-log assertions via state-KVP capture, not formatted-string matching"

key-files:
  created: []
  modified:
    - src/Processor.Sample/SampleConfig.cs
    - src/Processor.Sample/SampleProcessor.cs
    - tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs

key-decisions:
  - "Null-config default is a uniform single path (baseNumber 0 still gets the random addend; label null), keeping ProcessAsync branch-free and warning-clean"
  - "The PROC-01 deserialization fact REPLACES the deleted fail-path fact — file stays at exactly 3 facts"
  - "Random.Shared.Next(0,100) is max-exclusive (0..99); range assertions are [Number, Number+99], never exact sums"

patterns-established:
  - "Pattern 1: structured-log values asserted by capturing log state KVPs in the hermetic logger (not regex on the formatted message)"
  - "Pattern 2: JSON result SHAPE produced via JsonSerializer.Serialize(new {…}, ProcessorConfig.SerializerOptions) into the string ProcessItem.Data — symmetric with the framework's deserialize"

requirements-completed: [PROC-01, PROC-02, PROC-03]

# Metrics
duration: ~62min (wall-clock; ~10min active implementation, remainder dominated by slow full-suite + RealStack test runs)
completed: 2026-06-14
---

# Phase 64 Plan 01: Processor Work + Structured Logging Summary

**`processor-sample` reshaped to do observable, correlatable work — `SampleConfig(int Number, string? Label)`, a random-sum transform emitting `{number,label}` JSON, and exactly one injection-safe structured log (`{StepLabel}`+`{Sum}`) — proven by 3 green hermetic facts and 0-warning Debug+Release builds.**

## Performance

- **Duration:** ~62 min wall-clock (~10 min active implementation; the balance was the slow Microsoft.Testing.Platform full-suite + RealStack live-stack test runs)
- **Started:** 2026-06-14T08:41:51Z
- **Completed:** 2026-06-14T09:44:04Z
- **Tasks:** 3
- **Files modified:** 3

## Accomplishments
- `SampleConfig` reshaped from `(string? Value)` to `(int Number, string? Label)` — the `{number,label}` per-step assignment-payload contract the Phase-66 ES/Prometheus analyzer will parse.
- `ProcessAsync` rewritten: `sum = (config?.Number ?? 0) + Random.Shared.Next(0,100)` serialized as a `{number,label}` JSON string into `ProcessItem.Data`; demo `"fail"`→`FailedException` throw and `"processor-sample-ok"` fallback removed.
- Exactly **one** `LogInformation` per execution carrying `{StepLabel}`=label verbatim + `{Sum}`=sum as structured params — never interpolated (T-64-01 log-injection mitigation). The 6 correlation ids attach automatically from the ambient consume-filter scope; none added here (D-09).
- `CapturingLogger` extended to capture structured state KVPs, enabling the facts to assert param VALUES; 3 hermetic facts (sum+log shape, null-config one-item/one-log, PROC-01 case-insensitive deserialization) green.

## Task Commits

Each task was committed atomically:

1. **Task 1: Reshape SampleConfig to (int Number, string? Label)** - `b323286` (feat)
2. **Task 2: Rewrite ProcessAsync — sum transform + single structured log** - `ddbfd8b` (feat)
3. **Task 3: Rewrite 3 hermetic facts + capture state KVPs (TDD)** - `4fe6142` (test)

_Note: Task 3 is a `tdd="true"` task. Because the production behavior was already in place from Tasks 1-2, the rewritten facts went straight to GREEN (3/3) — see TDD Gate Compliance below._

## Files Created/Modified
- `src/Processor.Sample/SampleConfig.cs` - record reshaped to `(int Number, string? Label)`; XML doc updated to the `{"number":N,"label":"Step_*"}` payload shape.
- `src/Processor.Sample/SampleProcessor.cs` - sum transform + single structured log; `System.Text.Json` + `BaseProcessor.Core.Configuration` usings added; demo fail/fallback paths removed.
- `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` - `CapturingLogger` captures state KVPs; 3 facts rewritten to the new shape (range assertions, StepLabel/Sum KVP assertions, PROC-01 deserialization).

## Decisions Made
- **Null-config single path:** `config?.Number ?? 0` then add the random addend; `label = config?.Label` (null). Keeps `ProcessAsync` branch-free and warning-clean under `Nullable=enable`/`TreatWarningsAsErrors=true`.
- **3-fact invariant:** the PROC-01 deserialization fact replaces the deleted fail-path fact; the file stays at exactly 3 `[Fact]`s.
- **Range, not exact:** `Random.Shared.Next(0,100)` is max-exclusive (0..99), so the result-`number` assertion is `InRange(Number, Number+99)`.

## Deviations from Plan

None - plan executed exactly as written. No Rule 1-4 deviations were required; all three tasks' acceptance criteria and grep guards passed on first implementation.

## TDD Gate Compliance

Task 3 carries `tdd="true"`, but the plan deliberately orders the production change (Tasks 1-2) **before** the test rewrite (Task 3) — the tests validate already-written behavior, so they reached GREEN immediately rather than via a separate RED commit. This is an intentional plan-authored ordering, not a skipped RED gate: a true RED (writing the new-shape facts against the old code) was structurally impossible because Tasks 1-2 had already landed the new shape. The committed sequence is `feat`(b323286) → `feat`(ddbfd8b) → `test`(4fe6142), with the `test` commit proving the feature green. No standalone failing-test commit exists for this plan.

## Issues Encountered
- **Test-filter scoping (Microsoft.Testing.Platform):** the plan's verification command `dotnet test … --filter "FullyQualifiedName~SampleProcessorFacts"` is silently ignored (warning **MTP0001** — `VSTestTestCaseFilter` is a VSTest property, not honored under MTP), so an unfiltered full-suite run executes instead. Resolved by using the MTP-native xUnit v3 filter `-- --filter-method "*SampleProcessorFacts*"`, which correctly scopes to the 3 facts → **3/3 GREEN**.

## Verification Results
- `dotnet build src/Processor.Sample/Processor.Sample.csproj -c Debug` → **0 warnings, 0 errors**.
- `dotnet build -c Release` (solution-wide) → **0 warnings, 0 errors**.
- `dotnet build -c Debug` (solution-wide) → src projects compile 0/0; only the stale test file failed pre-Task-3 (expected per plan), GREEN after Task 3.
- `dotnet test … -- --filter-method "*SampleProcessorFacts*"` → **Passed! Failed: 0, Passed: 3** (the 3 PROC facts).
- Grep guards (all PASS): exactly one `LogInformation` in `SampleProcessor.cs`; no `processor-sample-ok` / `throw new FailedException` / `.Value` / `sample payload received`; no `Step_{` / `$"Step_` (T-64-01); test file has exactly 3 `[Fact]`, contains the KVP type + `Assert.InRange` + `JsonSerializer.Deserialize<SampleConfig>`.

## Deferred Issues (out of scope)
An unfiltered full-suite run reported **7 failed / 619 passed / 626 total**. The 7 failures are confined to the `[Category=RealStack]` live-stack E2E set (excluded from the hermetic suite) and stem from the source-vs-deployed-image mismatch (the change was not redeployed) — out of scope for this three-file plan. Details + later-phase action recorded in [`deferred-items.md`](./deferred-items.md). Exact per-test names await a RealStack re-run after the `processor-sample` image is rebuilt and the E2E payload/schema are updated to the `{number,label}` shape (Phase 65+).

## Next Phase Readiness
- The `{number,label}` result + `{StepLabel}`+`{Sum}` structured log are the exact data shapes the Phase-66 ES/Prometheus analyzer will aggregate by `correlationId`.
- **Blocker for the live milestone proof (not for this plan):** the `processor-sample` container image must be rebuilt from this source and the RealStack E2E payload/schema/assertions migrated from `{"value":…}` to `{"number":N,"label":"Step_*"}` before the fan-out workflow + fault-injection harness can run green.

## Self-Check: PASSED

- Files: `64-01-SUMMARY.md`, `SampleConfig.cs`, `SampleProcessor.cs`, `SampleProcessorFacts.cs` — all FOUND.
- Commits: `b323286`, `ddbfd8b`, `4fe6142` — all FOUND in git history.

---
*Phase: 64-processor-work-structured-logging*
*Completed: 2026-06-14*
