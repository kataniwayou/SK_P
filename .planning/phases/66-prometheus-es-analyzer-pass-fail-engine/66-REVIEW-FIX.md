---
phase: 66-prometheus-es-analyzer-pass-fail-engine
fixed_at: 2026-06-14T00:00:00Z
review_path: .planning/phases/66-prometheus-es-analyzer-pass-fail-engine/66-REVIEW.md
iteration: 1
findings_in_scope: 9
fixed: 9
skipped: 0
status: all_fixed
---

# Phase 66: Code Review Fix Report

**Fixed at:** 2026-06-14
**Source review:** .planning/phases/66-prometheus-es-analyzer-pass-fail-engine/66-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 9
- Fixed: 9
- Skipped: 0

**Verification:** Build 0 warnings / 0 errors. Hermetic facts: 10/10 green.

## Fixed Issues

### WR-01: Reconciliation compares a float delta against an int with exact `==`

**Files modified:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs`
**Commit:** 8be7ee0
**Applied fix:** Added `private const double DeltaTolerance = 0.5` and replaced both exact equality comparisons (`prom.DispatchSentDelta == triggerCount` and `UnaccountedDelta(prom) == 0`) with `Math.Abs(...) < DeltaTolerance`. Added a comment explaining the round-then-compare coupling that motivates the tolerance.

### WR-02: Poll-to-stable accepts a transient empty/failed ES result as "stable"

**Files modified:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`
**Commit:** dfadfd0
**Applied fix:** Added `current.Count > 0` guard to the stability condition so two consecutive empty results are NOT accepted as stable. Updated the XML doc on `PollHitsToStableAsync` to document the residual case (genuinely zero runs exhausts the budget; the WR-04 precondition assert then surfaces the root cause).

### WR-03: Poll-to-stable budget is effectively halved by the preceding drain delay

**Files modified:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`
**Commit:** 48f043d
**Applied fix:** Introduced `private const int PollToStableBudgetMs = 60_000` (separate from `DrainMs`) and changed the deadline calculation from `AddMilliseconds(DrainMs)` to `AddMilliseconds(PollToStableBudgetMs)`. Added inline comment documenting total worst-case wall-clock = DrainMs + PollToStableBudgetMs = 120s.

### WR-04: `triggerCount == 0` (no fires in window) scores a vacuous Pass

**Files modified:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`
**Commit:** 7e09aa0
**Applied fix:** Added `Assert.True(triggerCount > 0, ...)` at the fixture level (step 6, before the engine call) per the task note preferring fixture-level guard over engine semantics change. The assert fires before the engine is called, producing a loud FAIL with the DispatchSentDelta diagnostic in the message.

### IN-01: `RunTrace.DuplicateLabels` ordering is non-deterministic

**Files modified:** `tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs`
**Commit:** 4802ddd
**Applied fix:** Changed `dupes.ToList()` to `dupes.OrderBy(s => s, StringComparer.Ordinal).ToList()` so the duplicate label list is always emitted in deterministic Ordinal order regardless of HashSet iteration order.

### IN-02: `DispatchConsumedDelta` and `KeeperReinjectDroppedDelta` are read but never reconciled

**Files modified:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs`
**Commit:** 710eaac
**Applied fix:** Added a multi-line comment at the reconciliation site (step 4 in `Analyze`) explaining that `DispatchConsumedDelta` and `KeeperReinjectDroppedDelta` are evidence-only counters deliberately excluded from arithmetic, and that dormant dedupe deltas also feed no arithmetic per the dormant-counter contract. No arithmetic changes.

### IN-03: Field-path helpers assume exactly two dot segments via `Split('.')[1]`

**Files modified:** `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClientFacts.cs`
**Commit:** 4ad70ff
**Applied fix:** Changed all three occurrences of `Split('.')[1]` to `Split('.')[^1]` (last segment via C# index-from-end operator) in `CorrelationId`, `StepLabel`, and `Sum` helper methods. This makes the leaf-property intent explicit and tolerates field paths with more than two segments.

### IN-04: `windowStartUtc` captured before the BEFORE snapshot widens the ES window vs the Prom window

**Files modified:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`
**Commit:** 4c201a6
**Applied fix:** Added a documentation comment at the `snapshotUtc` capture site explaining the alignment gap: `snapshotUtc` is set before polling to keep the ES search body stable; the Prom AFTER read is taken after poll-to-stable, so late fires in the tail gap are counted in `DispatchSentDelta` but excluded from the ES range and scored MISSING. This is documented as an accepted limitation for the happy-path window. No structural change (restructuring the ordering would change ES window semantics and require re-validation).

### IN-05: `PromPollTimeoutMs` constant is declared but unused in this fixture

**Files modified:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`
**Commit:** 2e36bf3
**Applied fix:** Removed the `private const int PromPollTimeoutMs = 120_000` constant and its comment block. The constant was never referenced within the fixture — `ReadCounterSetAsync` uses a single-shot `QueryPrometheus` call that does not accept a timeout parameter.

---

_Fixed: 2026-06-14_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
