---
phase: 67-fault-injection-harness
reviewed: 2026-06-14T00:00:00Z
depth: standard
files_reviewed: 6
files_reviewed_list:
  - scripts/phase-67-harness.ps1
  - tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs
  - tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs
  - tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs
  - tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
  - tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs
findings:
  critical: 0
  warning: 2
  info: 4
  total: 6
status: issues_found
---

# Phase 67: Code Review Report

**Reviewed:** 2026-06-14
**Depth:** standard
**Files Reviewed:** 6
**Status:** issues_found

## Summary

Reviewed the Phase 67 fault-injection harness (one PowerShell orchestrator) plus the ES-binding
re-foundation of the analyzer (`PassFailEngine`, `AnalyzerReport`, the hermetic facts, the RealStack
E2E fixture, and the `PrometheusTestClient` time-pin overload). No product source (`src/**`) is
touched — this is test + dev-ops tooling only.

**Verdict math is coherent.** The ES-binding arbiter is sound: `startedRuns = runs.Count` is the
binding denominator, `complete` is computed via exact 9-label `SetEquals`, `missing = startedRuns -
complete.Count`, and `pass = missing == 0 && !dupFail`. The duplicate fail-closed signal is
preserved end-to-end (`RunTrace.HasAnyDuplicateLabel` → `dupFail` → binding `pass`), Prometheus is
strictly corroboration-only (it never enters the `pass` expression), and the ±1-run boundary
tolerance is applied correctly (`deadRunExcess > CorroborationRunTolerance`). The seven hermetic
facts cover each branch (pass, incomplete-fail, duplicate-fail-closed, dead-run-warning-not-fail,
window-edge-within-tolerance, retired-conflation-no-longer-fails, dormant-dedupe-no-block) and are
not vacuously green.

**Security posture is good.** The `ScenarioId` path-traversal whitelist (`^[A-Za-z0-9_-]+$`) is
validated *before* any path composition; PromQL is `Uri.EscapeDataString`-escaped (including the new
`time=` param); the harness psql query is a static literal with no interpolated input; and
fault-injection targets the compose *service* name from a fixed in-script table, never a
caller-controlled string. No hardcoded secrets, no `eval`/`Invoke-Expression`, no command injection
surface found.

The findings below are correctness-hardening and clarity items — none are blockers.

## Warnings

### WR-01: Prom corroboration cross-check is one-directional — the documented "mirror" case is silently absorbed

**File:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs:123-130`
**Issue:** The corroboration warning only fires on a *positive* excess
(`deadRunExcess = promImpliedRuns - startedRuns > CorroborationRunTolerance`). The negative direction
— ES observing materially MORE started runs than Prom dispatched (`startedRuns - promImpliedRuns`
large) — is never surfaced. That direction is a genuine telemetry inconsistency (a dispatch counter
that under-counted, a scrape gap, or ES double-counting correlationIds) and would currently pass
corroboration silently. Note this also contradicts the fact's own doc comment, which claims the
tolerance protects "implied 11 vs ES 10, **or the mirror: implied 9 vs ES 10**"
(`PassFailEngineFacts.cs:129-130`) — only one side is actually guarded by a warning; the mirror side
is unguarded entirely (not just inside tolerance — outside tolerance too).

This is defensible as a deliberate design (the harness is built to detect *dead* runs, i.e. the
positive direction), so it is a Warning, not Critical. But the asymmetry should be either
intentional-and-documented at the code site or made symmetric.

**Fix:** Either guard both directions with an absolute comparison, or add a code comment stating the
negative direction is intentionally not a warning. If symmetric:
```csharp
var deadRunExcess = promImpliedRuns - startedRuns;
if (Math.Abs(deadRunExcess) > CorroborationRunTolerance)
{
    var direction = deadRunExcess > 0
        ? $"Prom implies ~{promImpliedRuns} dispatched run(s) but ES observed only {startedRuns} STARTED (dead-run)"
        : $"ES observed {startedRuns} STARTED run(s) but Prom implies only ~{promImpliedRuns} dispatched (under-count/scrape gap)";
    corroborationDetail.Add($"Prom corroboration WARNING: {direction} " +
        $"(|excess| {Math.Abs(deadRunExcess)} > ±{CorroborationRunTolerance} tolerance). NON-FATAL (67-03).");
}
```

### WR-02: Standalone-mode tail gap can score a real run MISSING (self-documented, but still latent)

**File:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs:131-140`
**Issue:** The code comment at lines 133-138 (the "standalone IN-04" note) candidly describes a real
defect in the non-window-pinned path: `snapshotUtc` (ES range upper bound) is captured *before*
poll-to-stable, but the live Prom AFTER read happens *after* poll-to-stable. A run dispatched in that
gap is counted in `DispatchSentDelta` (inflating `triggerCount`/`PromImpliedRuns`) yet excluded from
the ES `[windowStart, snapshotUtc]` range — so under the *old* binding model it scored MISSING. Under
67-03 it no longer fails the verdict (ES-binding), but it WILL raise a spurious
`Unreconciled`/dead-run corroboration warning whenever a fire lands in the tail window. Since the
Phase 67 harness always runs window-pinned (both `WINDOW_*_UTC` set), this only bites the standalone
Phase 66 invocation — hence Warning, not Critical.

**Fix:** In standalone mode, capture the live AFTER counter read at the *same* instant `snapshotUtc`
is taken (before poll-to-stable), so the Prom delta cohort matches the ES range upper bound:
```csharp
var snapshotUtc = windowPinned ? pinnedWindowEnd : DateTimeOffset.UtcNow;
// Standalone: take the live AFTER counters NOW, aligned to snapshotUtc — before poll-to-stable,
// so a tail-window dispatch is excluded from BOTH the ES range and the Prom delta.
var standaloneAfter = windowPinned ? null : await ReadCounterSetAsync(prom, ct);
var stepHits = await PollHitsToStableAsync(es, windowStartUtc, snapshotUtc, ct);
// ... then use standaloneAfter instead of a fresh post-poll read at lines 148-151.
```

## Info

### IN-01: `Math.Round` uses banker's rounding on the dispatch-implied run count

**File:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs:120`
**Issue:** `promImpliedRuns = (int)Math.Round(prom.DispatchSentDelta / LabelsPerRun)` uses the default
`MidpointRounding.ToEven`. A half-step delta rounds toward even (e.g. `121.5/9`→ not exact, but a
constructed `13.5` → 14 while `12.5` → 12). With a ±1-run tolerance the practical impact is nil, but
the behavior is mildly surprising for a "round to nearest run" intent. The same applies to
`triggerCount = (int)Math.Round(promSnapshot.DispatchSentDelta)` in the fixture
(`AnalyzerE2ETests.cs:164`).
**Fix:** If "nearest, halves up" is intended, pass `MidpointRounding.AwayFromZero` explicitly;
otherwise add a one-line comment that ToEven is accepted because the tolerance absorbs it.

### IN-02: `Get-FireCount` `[int]` cast can overflow on long-lived counters

**File:** `scripts/phase-67-harness.ps1:231`
**Issue:** `[int][double](... Measure-Object -Sum).Sum` narrows to a 32-bit int. `orchestrator_dispatch_sent_total`
summed across all label series on a long-running stack could (eventually) exceed `Int32.MaxValue`,
overflowing to a negative/garbage baseline and breaking the `observed >= N` loop. The proof flow
flushes + clean-restarts before each run so this is unlikely in practice, but the cast is gratuitously
narrow for a counter.
**Fix:** Use `[long]` (or `[int64]`) for the fire count, or keep it as `[double]` and compare
numerically — the loop math (`observed -ge N`) works fine with a double.

### IN-03: `Remove-Item Env:...` on STEP H leaks the env vars if the analyzer step throws before line 335

**File:** `scripts/phase-67-harness.ps1:330-335`
**Issue:** The D-16 env seam (`SCENARIO_ID`/`WINDOW_START_UTC`/`WINDOW_END_UTC`) is set at 330-332 and
cleaned at 335, but the cleanup is a plain sequential statement, not a `finally`. With
`$ErrorActionPreference = 'Stop'`, any throw between setting and clearing (e.g. the `dotnet test`
invocation surfacing a terminating error, or `Get-ChildItem` issues) would unwind to the outer
`finally` (which only does `Pop-Location`) and leave the three env vars set in the parent shell.
Process-scoped env vars die with the pwsh process, so for the one-shot `pwsh -File` invocation this is
harmless; it only matters if someone dot-sources or runs the body in an interactive session.
**Fix:** Wrap the analyze block in its own `try { ... } finally { Remove-Item Env:SCENARIO_ID,
Env:WINDOW_START_UTC, Env:WINDOW_END_UTC -ErrorAction SilentlyContinue }`, or move the cleanup into
the outer `finally`.

### IN-04: TEST-02 reuses the `*Analyze_HappyPath_*` filter — verdict semantics are correct but the name is misleading

**File:** `scripts/phase-67-harness.ps1:333` (with `tests/.../AnalyzerE2ETests.cs:85`)
**Issue:** STEP H invokes `--filter-method "*Analyze_HappyPath_Window_Yields_Pass*"` for *every*
scenario, including TEST-02 (deliberate processor-tier crash). The fixture name asserts a PASS
("...Yields_Pass"), but a crash run may legitimately produce started-but-incomplete runs → ES-binding
FAIL → failed assert → non-zero exit → harness exit 1 ("legitimate verdict FAIL" per the D-04 exit
table). The wiring is therefore *correct* (the exit code is the verdict either way), but a fixture
named `_Yields_Pass` that is expected to FAIL on a fault scenario is a readability/maintenance trap —
a future reader could "fix" the failing TEST-02 assert and silently neuter fault detection.
**Fix:** No behavior change needed. Consider renaming the fixture to a neutral
`Analyze_Window_Yields_Verdict` (the assert still enforces PASS; the harness exit still mirrors it),
or add a harness comment at line 333 stating that a non-zero exit here is the expected FAIL verdict
for fault scenarios, not an infra error.

---

_Reviewed: 2026-06-14_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
