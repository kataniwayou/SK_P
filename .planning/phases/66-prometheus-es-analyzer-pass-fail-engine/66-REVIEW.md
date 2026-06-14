---
phase: 66-prometheus-es-analyzer-pass-fail-engine
reviewed: 2026-06-14T00:00:00Z
depth: standard
files_reviewed: 9
files_reviewed_list:
  - tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs
  - tests/BaseApi.Tests/Observability/Analysis/PromCounterSnapshot.cs
  - tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs
  - tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs
  - tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs
  - tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs
  - tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs
  - tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClientFacts.cs
  - tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
findings:
  critical: 0
  warning: 4
  info: 5
  total: 9
status: issues_found
---

# Phase 66: Code Review Report

**Reviewed:** 2026-06-14
**Depth:** standard
**Files Reviewed:** 9
**Status:** issues_found

## Summary

This phase adds the Prometheus/ES pass-fail analyzer: a pure correctness engine
(`PassFailEngine`) with DTOs (`RunTrace`, `PromCounterSnapshot`, `AnalyzerReport`),
hermetic unit facts for each decision branch, a multi-hit ES aggregation primitive
(`ElasticsearchTestClient.SearchAllHits`), and a RealStack E2E fixture
(`AnalyzerE2ETests`).

Overall the code is well-structured: the pure-engine/IO-shell split is clean, the
fail-closed reconciliation logic is sound, the path-traversal guard on `scenarioId` is
present, and the hermetic facts cover every engine branch. No security or crash issues
found.

The findings below are correctness/robustness concerns, concentrated in the live fixture
(`AnalyzerE2ETests`): floating-point exact-equality in reconciliation, a poll-to-stable
loop that treats a transient empty ES result as "stable," and a wall-clock budget that is
effectively halved by an earlier blocking delay. None are crashes; the engine fails
closed, so the risks are false negatives (a real defect scored Pass) rather than false
alarms. These are worth addressing before the Phase 67/68 fault-injection harness depends
on this fixture as its binding arbiter.

## Warnings

### WR-01: Reconciliation compares a float delta against an int with exact `==`

**File:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs:68`
**Issue:** `prom.DispatchSentDelta == triggerCount` and `UnaccountedDelta(prom) == 0`
(line 70) use exact `double` equality. The deltas are computed in the fixture as
`after.DispatchSent - before.DispatchSent` over `double` values parsed from Prometheus
string samples (`SumSampleValues`). Meanwhile `triggerCount` is derived in the fixture as
`(int)Math.Round(promSnapshot.DispatchSentDelta)` (AnalyzerE2ETests.cs:121). The engine
then re-compares that rounded int against the *un-rounded* double. If the delta ever
carries any fractional residue (multi-series summation, future non-integer counters, or
float accumulation across many samples), `DispatchSentDelta == triggerCount` silently
becomes false and forces `Unreconciled` → `Fail`. Today the counters are integer-valued so
this holds, but the round-then-exact-compare pairing is fragile and couples two files via
an implicit "the delta is always an exact integer" assumption.
**Fix:** Reconcile with an epsilon/tolerance instead of exact equality, e.g.:
```csharp
private const double DeltaTolerance = 0.5;

var reconciled =
    Math.Abs(prom.DispatchSentDelta - triggerCount) < DeltaTolerance
    && prom.ResultSentCompletedDelta >= complete.Count * LabelsPerRun
    && Math.Abs(UnaccountedDelta(prom)) < DeltaTolerance
    && !nonCompletedTerminal;
```

### WR-02: Poll-to-stable accepts a transient empty/failed ES result as "stable"

**File:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs:166-184`
**Issue:** `PollHitsToStableAsync` returns as soon as two consecutive `SearchAllHits` calls
report the same `.Count`. `SearchAllHits` returns an EMPTY list (not an exception) on any
non-success response — 404 lazy-index, ES briefly unreachable, compose-network blip
(ElasticsearchTestClient.cs:145). If ES is transiently unavailable across two consecutive
polls (~5s apart), the loop sees `0 == 0` and returns ZERO traces as "stable" even though
runs actually executed. The fixture then derives `triggerCount` from Prometheus (which is a
different backend and may still be live), producing `Missing = triggerCount - 0 > 0` →
`Fail`. That fails closed (loud, not silent), so it is not a crash — but it converts a
backend blip into a spurious correctness FAIL, which undermines the "trustworthy verdict"
goal once the Phase 67/68 harness reads the exit code.
**Fix:** Require stability at a non-zero count, or treat an empty result as "not yet
stable" until the budget expires:
```csharp
var current = await es.SearchAllHits(body, ct: ct);
if (current.Count == last.Count && current.Count > 0)
{
    return current; // stable across two polls AND we actually have hits
}
last = current;
```
Document the residual case (genuinely zero runs) so it is not masked.

### WR-03: Poll-to-stable budget is effectively halved by the preceding drain delay

**File:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs:104,171`
**Issue:** The fact blocks for `DrainMs` (60s) at line 104, then `PollHitsToStableAsync`
computes its own deadline as `DateTime.UtcNow.AddMilliseconds(DrainMs)` (line 171) — i.e.,
ANOTHER 60s budget. The XML doc on the method says it is "Bounded by `DrainMs` total
wall-clock," but the actual total wall-clock spent waiting is `DrainMs` (drain) + up to
`DrainMs` (poll) = up to 120s, not the documented 60s. Conversely, if the intent was a
single 60s drain that includes polling, the budget is double-counted. Either the constant
is being reused for two semantically different purposes or the doc is wrong; both are
maintenance traps for the harness that tunes these windows.
**Fix:** Introduce a distinct constant for the poll-to-stable budget and correct the doc:
```csharp
private const int PollToStableBudgetMs = 60_000; // separate from the DrainMs settle
...
var deadline = DateTime.UtcNow.AddMilliseconds(PollToStableBudgetMs);
```
Update the XML summary to state the true combined worst-case wall-clock.

### WR-04: `triggerCount == 0` (no fires in window) scores a vacuous Pass

**File:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs:121` and
`tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs:50,68-75`
**Issue:** If the observation window captured no dispatches, `DispatchSentDelta == 0` →
`triggerCount == 0`. In the engine: `complete.Count == 0`, `missing = 0 - 0 = 0`,
`ResultSentCompletedDelta (0) >= 0 * 9 (0)` is true, `UnaccountedDelta == 0`, no
duplicates → `Verdict.Pass`. A window in which the stack produced nothing at all reports a
GREEN verdict. For a self-verifying happy-path fixture whose precondition is "the fan-out
workflow is seeded and firing," an all-zero window almost certainly means a broken
precondition, yet it passes — exactly the "vacuously green" outcome the hermetic facts
were built to prevent at the engine level but which is reachable at the fixture level.
**Fix:** Assert a positive trigger count before scoring (or treat `triggerCount == 0` as a
FAIL in the engine):
```csharp
Assert.True(triggerCount > 0,
    $"No dispatches observed in the window (DispatchSentDelta={promSnapshot.DispatchSentDelta}); " +
    "the fan-out workflow precondition is not satisfied.");
```

## Info

### IN-01: `RunTrace.DuplicateLabels` ordering is non-deterministic

**File:** `tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs:59,74`
**Issue:** `DuplicateLabels` is materialized from a `HashSet<string>` via `dupes.ToList()`.
HashSet enumeration order is unspecified, so the duplicate list (which is serialized into
the JSON report and surfaced to humans) can appear in different orders across runs for the
same input. This makes report diffs noisy and any snapshot/golden-file comparison flaky.
**Fix:** Sort deterministically before exposing, e.g. `DuplicateLabels = dupes.OrderBy(s => s, StringComparer.Ordinal).ToList();`.

### IN-02: `DispatchConsumedDelta` and `KeeperReinjectDroppedDelta` are read but never reconciled

**File:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs:66-71`
**Issue:** `PromCounterSnapshot` carries `DispatchConsumedDelta` and
`KeeperReinjectDroppedDelta` (live counters the fixture scrapes at cost), but the engine's
reconciliation only uses `DispatchSentDelta`, `ResultConsumedDelta`,
`ResultSentCompletedDelta`, and `NonCompletedOutcomes`. The two unused live deltas are
carried into the report as evidence only. This is intentional per the doc ("reported as
evidence"), but a reader expecting `DispatchConsumed` to participate in the
dispatch-balance check (it is the natural counterpart to `DispatchSent`) may be surprised.
**Fix:** No code change required; consider a one-line comment at the reconciliation site
noting these two deltas are evidence-only and deliberately excluded from arithmetic.

### IN-03: Field-path helpers assume exactly two dot segments via `Split('.')[1]`

**File:** `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClientFacts.cs:78,82,91`
**Issue:** `CorrelationId`, `StepLabel`, and `Sum` derive the JSON property name with
`EsIndexNames.CorrelationIdFieldPath.Split('.')[1]`. This silently assumes every field-path
const is exactly `prefix.Name` (two segments). If a future path const gains a third segment
(e.g., a `.keyword` sub-field is ever reintroduced, or a nested resource path), `[1]` would
grab the wrong segment with no error. The coupling between the dot-path const format and the
hard-coded index `[1]` is implicit.
**Fix:** Use the last segment instead of a fixed index: `path.Split('.')[^1]`, or add a
small helper `LeafName(string dottedPath)` so the intent ("the leaf property name") is
explicit.

### IN-04: `windowStartUtc` captured before the BEFORE snapshot widens the ES window vs the Prom window

**File:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs:97-98,106-107`
**Issue:** `windowStartUtc` is captured at line 97 immediately before the BEFORE counter
read, and `snapshotUtc` at line 106 just before the ES poll. The ES `range` filter spans
`[windowStartUtc, snapshotUtc]`, but `snapshotUtc` is taken AFTER the 60s drain
(`Task.Delay(DrainMs)` at line 104) while the Prom AFTER snapshot is read even later (line
113, after poll-to-stable). The ES window upper bound (`snapshotUtc`) and the Prom window
upper bound (the AFTER read) therefore differ by the poll-to-stable duration. In a clean
happy path with no late fires this is harmless, but a run that dispatches between
`snapshotUtc` and the AFTER Prom read would be counted in `DispatchSentDelta` (trigger
denominator) yet excluded from the ES `range`, inflating `Missing`. Fails closed, so not a
crash; noted for window-alignment correctness the harness may care about.
**Fix:** Capture the ES upper-bound timestamp and the Prom AFTER read at the same point
(after poll-to-stable completes), or document that late fires in the tail gap are
intentionally scored MISSING.

### IN-05: `PromPollTimeoutMs` constant is declared but unused in this fixture

**File:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs:57`
**Issue:** `PromPollTimeoutMs = 120_000` is documented as mirroring the MetricsRoundTrip
budget, but the fixture reads counters via `ReadCounterSetAsync` →
`PrometheusTestClient.QueryPrometheus` (single-shot, `EnsureSuccessStatusCode`), which does
not take a timeout parameter. The constant is dead within this file.
**Fix:** Remove the unused constant, or wire it through if a bounded Prom poll was intended
for the counter reads.

---

_Reviewed: 2026-06-14_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
