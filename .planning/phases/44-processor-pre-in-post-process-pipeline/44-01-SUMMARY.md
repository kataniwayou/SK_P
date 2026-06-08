---
phase: 44-processor-pre-in-post-process-pipeline
plan: 01
subsystem: infra
tags: [csharp, dotnet8, masstransit, redis, retry, processor-pipeline, xunit-v3]

# Dependency graph
requires:
  - phase: 43-message-contracts-l2-key-reshape
    provides: reshaped Step* wire records (StepCompleted/Failed/Cancelled/Processing) + Keeper-state contracts + L2ProjectionKeys GUID data key that Plan 02 maps these new types onto
provides:
  - ProcessOutcome enum (Completed|Failed) — author-declared per-item outcome
  - ProcessItem record (Result, Data, author-minted ExecutionId) — replaces framework-owned ProcessResult
  - ProcessStatusException family (abstract base + Processing/Failed/Cancelled) — author status signals
  - RetryLoop.ExecuteAsync<T> static helper + RetryOutcome<T> struct — N immediate attempts, surface-not-throw exhaustion
  - KeyAbsentException internal sentinel — unifies absent/empty L2 read with Redis fault (A2)
  - RetryLoopFacts (5 hermetic facts) — RESIL-01 attempt-count + surface contract
affects: [44-02-processor-pipeline, 44-03-old-seam-deletion, keeper-recovery]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Surface-not-throw retry: RetryLoop returns RetryOutcome<T> (Succeeded/Value/Error) so the caller routes the terminal per op (read→REINJECT, write→INJECT, delete→DELETE, send→re-throw)"
    - "Author-owned outcome+id: ProcessItem carries the per-item ProcessOutcome AND an author-minted ExecutionId (inverts ProcessResult where the framework owned outcome)"
    - "Status-by-exception: a primary-ctor ProcessStatusException family the pipeline maps by runtime type to the matching Step* record"
    - "Internal sentinel unify: KeyAbsentException converts a no-throw RedisValue.Null/empty read into a retryable failure (A2)"

key-files:
  created:
    - src/BaseProcessor.Core/Processing/ProcessOutcome.cs
    - src/BaseProcessor.Core/Processing/ProcessItem.cs
    - src/BaseProcessor.Core/Processing/ProcessStatusException.cs
    - src/BaseProcessor.Core/Resilience/RetryLoop.cs
    - src/BaseProcessor.Core/Resilience/KeyAbsentException.cs
    - tests/BaseApi.Tests/Processor/RetryLoopFacts.cs
  modified: []

key-decisions:
  - "RetryLoop lives in a new BaseProcessor.Core/Resilience/ folder (placement was Claude's discretion per CONTEXT); the static-helper + result-struct shape mirrors ProcessorJsonSchemaValidator"
  - "ProcessStatusException uses primary-ctor classes and is mapped by runtime type (no exposed Status property) — the pipeline's catch type-switches per CONTEXT discretion"
  - "All five RetryLoopFacts pass NOW (not RED) — they depend only on the Task-2 RetryLoop that already exists; this is a self-contained Wave-0 unit fact, not the pipeline-coupled RED of Plan 02"

patterns-established:
  - "Surface-not-throw bounded retry via RetryOutcome<T>"
  - "Author-owned per-item outcome+ExecutionId via ProcessItem"
  - "Status-carrying exception family mapped by runtime type"

requirements-completed: [RESIL-01, PIPE-04, PIPE-05]

# Metrics
duration: 6min
completed: 2026-06-08
---

# Phase 44 Plan 01: Pre/In/Post-Process Leaf Types + RetryLoop Summary

**Five additive author-contract source files (ProcessOutcome/ProcessItem/ProcessStatusException family, RetryLoop surface-not-throw helper, KeyAbsentException sentinel) plus the RESIL-01 RetryLoopFacts — the Wave-0 foundation Plan 02 composes from, with the old ProcessResult/ProcessAsync seam left untouched and the solution still GREEN.**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-06-08T14:04:48Z
- **Completed:** 2026-06-08T14:10:43Z
- **Tasks:** 3
- **Files modified:** 6 created, 0 existing modified

## Accomplishments
- `ProcessOutcome` enum + `ProcessItem` record establish the new author-return unit (author declares the per-item outcome AND mints `ExecutionId`), inverting the framework-owned `ProcessResult`.
- `ProcessStatusException` family (abstract base + `ProcessingException`/`FailedException`/`CancelledException`) gives the author a uniform-message API the pipeline maps by runtime type to `StepProcessing`/`StepFailed`/`StepCancelled`.
- `RetryLoop.ExecuteAsync<T>` runs `Math.Max(1, limit)` immediate attempts and **surfaces** exhaustion via `RetryOutcome<T>` (no throw on exhaust), with `KeyAbsentException` unifying an absent/empty L2 read with a Redis fault (A2).
- `RetryLoopFacts` proves the RESIL-01 attempt-count + surface contract (5/5 GREEN); full hermetic suite 485 passed / 0 failed (no regression from the additive types).
- The old `ProcessResult.cs` / `BaseProcessor.cs` seam is byte-unchanged (Plan 02/03 territory); `BaseProcessor.Core` builds Release 0-warning.

## Task Commits

Each task was committed atomically:

1. **Task 1: ProcessOutcome + ProcessItem + ProcessStatusException family** - `3c9c17f` (feat)
2. **Task 2: RetryLoop helper + KeyAbsentException sentinel** - `ead8e14` (feat)
3. **Task 3: RetryLoopFacts (RESIL-01, Wave-0 self-contained)** - `4a11d80` (test)

_Task 3 is the plan's `tdd="true"` task but is documented (and behaved) as a single GREEN-from-the-start fact file — RetryLoop already existed from Task 2, so there was no RED phase (the plan explicitly states this is NOT RED)._

## Files Created/Modified
- `src/BaseProcessor.Core/Processing/ProcessOutcome.cs` - `enum ProcessOutcome { Completed, Failed }` (D-03 author-declared outcome).
- `src/BaseProcessor.Core/Processing/ProcessItem.cs` - `sealed record ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId)` (D-03 author-return unit).
- `src/BaseProcessor.Core/Processing/ProcessStatusException.cs` - abstract base + 3 primary-ctor subclasses (D-04/D-05 author status signals).
- `src/BaseProcessor.Core/Resilience/RetryLoop.cs` - `static RetryLoop.ExecuteAsync<T>` + `readonly record struct RetryOutcome<T>` (D-08/A3 surface-not-throw bounded retry).
- `src/BaseProcessor.Core/Resilience/KeyAbsentException.cs` - `internal sealed class KeyAbsentException` (A2 absent/empty → retryable failure).
- `tests/BaseApi.Tests/Processor/RetryLoopFacts.cs` - 5 hermetic facts (exhaust-count+surface, first-success, second-attempt, zero-limit guard, send-exhaust re-throw).

## Decisions Made
- `RetryLoop`/`RetryOutcome` placed in a new `BaseProcessor.Core/Resilience/` folder (CONTEXT left placement to discretion); shape mirrors the assembly's existing `ProcessorJsonSchemaValidator` static-helper convention.
- `ProcessStatusException` mapped by runtime type (no exposed `Status` property) — pipeline catch type-switches, per CONTEXT discretion.
- RetryLoopFacts authored as GREEN-from-start (not RED) per the plan's explicit note — they depend only on the already-existing Task-2 `RetryLoop`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fixed CS1734 doc-comment build error in RetryLoop.cs**
- **Found during:** Task 2 (RetryLoop helper)
- **Issue:** The plan's verbatim class-level `<summary>` used `<paramref name="limit"/>`, but `limit` is a parameter of the `ExecuteAsync` method, not of the `RetryLoop` class. Under the assembly's `TreatWarningsAsErrors`, this raised CS1734 and failed the Release build.
- **Fix:** Changed the class-doc `<paramref name="limit"/>` to a plain `<c>limit</c>` code reference. No code/behavior change; the method-level `int limit` parameter and all acceptance greps are intact.
- **Files modified:** src/BaseProcessor.Core/Resilience/RetryLoop.cs
- **Verification:** `dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj -c Release` → 0 Warning / 0 Error.
- **Committed in:** `ead8e14` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking doc-error). **Tooling adaptation (not a content deviation):** the plan's literal `dotnet test ... --filter-method`/`--filter-not-trait` flags are VSTest syntax; this project runs xunit.v3 under Microsoft.Testing.Platform (MTP), which requires those flags AFTER a `--` separator (`dotnet test <proj> -- --filter-method "*RetryLoop*" --filter-not-trait "Category=RealStack"`). Same filters, MTP invocation form.
**Impact on plan:** The doc-error fix was required to compile; no scope creep, no behavior change, no architectural change, no auth gates, no stubs, no new threat surface (additive in-process types only).

## Issues Encountered
None beyond the deviation above — the additive types broke nothing; the old seam stayed unchanged as planned.

## User Setup Required
None - no external service configuration required.

## TDD Gate Compliance
Task 3 is marked `tdd="true"` but the plan explicitly states it is **NOT RED** — `RetryLoop` already exists from Task 2, so the five facts are GREEN from authoring. There is consequently a single `test(...)` commit (`4a11d80`) with no preceding RED for this fact file; the implementation it exercises was GREEN-committed in `ead8e14` (`feat`). This ordering matches the VALIDATION.md interface-first plan (pipeline-coupled RED facts are authored in Plan 02 alongside the pipeline type they need).

## Next Phase Readiness
- All five leaf types + the RetryLoop helper exist, compile Release 0-warning, and are referenced-by-name-ready for Plan 02's pure-composition consumer rewrite.
- Solution stays GREEN: old `ProcessResult`/`ProcessAsync(string,string,ct)` seam present and untouched (Plan 03 deletes it).
- No blockers.

## Self-Check: PASSED

All 6 source/test files + the SUMMARY exist on disk; all 3 task commits (`3c9c17f`, `ead8e14`, `4a11d80`) exist in git history.

---
*Phase: 44-processor-pre-in-post-process-pipeline*
*Completed: 2026-06-08*
