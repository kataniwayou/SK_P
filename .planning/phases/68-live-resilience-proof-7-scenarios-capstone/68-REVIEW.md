---
phase: 68-live-resilience-proof-7-scenarios-capstone
reviewed: 2026-06-15T00:00:00Z
depth: standard
files_reviewed: 3
files_reviewed_list:
  - scripts/phase-67-harness.ps1
  - scripts/phase-68-sweep.ps1
  - tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
findings:
  critical: 0
  warning: 2
  info: 4
  total: 6
status: issues_found
---

# Phase 68: Code Review Report

**Reviewed:** 2026-06-15T00:00:00Z
**Depth:** standard
**Files Reviewed:** 3
**Status:** issues_found

## Summary

Phase 68 is a small, well-contained diff: (1) a cosmetic C# test-fixture rename
(`Analyze_HappyPath_Window_Yields_Pass` → `Analyze_Window_Yields_Pass`) plus its doc-comment
update, (2) the corresponding `--filter-method` literal update in `phase-67-harness.ps1` and
five new rows (TEST-03..07) appended to the in-script `$Scenarios` table, and (3) a brand-new
thin sweep driver, `phase-68-sweep.ps1`, that loops the harness over all seven scenarios and
rolls up the results.

The rename is correctly propagated to the single harness call site that depends on it, so the
"out-of-sync literal silently runs the whole 638-test suite" trap the comment warns about is
avoided. The sweep driver is genuinely thin (no re-scoring, no Prom reads, no auto-retry) and
its security posture is sound — it only ever feeds the hardcoded seven ids, and the harness
re-validates each id against `^[A-Za-z0-9_-]+$` before any docker/psql op. No injection,
no hardcoded secrets, no path-traversal exposure. The `[long]` fire-count cast and the
`finally`-scoped env-var cleanup carried over from Phase 67 remain correct.

No critical issues. Two warnings concern the runtime behavior of the newly-introduced
infrastructure-crash scenarios (TEST-04..07): the harness health-wait only waits on the crashed
tier, not the dependents that `compose.yaml` gates on `redis`/`rabbitmq` via `service_healthy`,
so a recovered-but-still-reconnecting dependent can silently poison the observation window. The
info items are documentation-drift and minor robustness notes.

## Warnings

### WR-01: New redis/rabbitmq crash scenarios only health-wait the crashed tier, not its dependents

**File:** `scripts/phase-67-harness.ps1:294-314` (exercised by new rows at `:92-94`)
**Issue:** TEST-05 (`redis`), TEST-06 (`rabbitmq`) and TEST-07 (`redis`,`rabbitmq`) are new in this
diff. `compose.yaml` shows orchestrator, processor-sample and keeper all `depends_on` redis and/or
rabbitmq with `condition: service_healthy` (lines 191-366). The crash sequencer does
`docker compose stop <svc>` / `docker compose start <svc>` for ONLY the target tier, and STEP F.4
then waits for healthy on ONLY that same target tier (`foreach ($svc in $scenario.targetContainers)`).
When redis/rabbitmq goes down mid-window, dependent services may go unhealthy or enter a reconnect
backoff; `docker compose start redis` does not restart or re-gate those dependents, and
`restart: unless-stopped` only resurrects *exited* containers, not ones that merely went unhealthy.
A dependent that is still reconnecting when the window closes will under-produce Step_* logs, which
the analyzer scores as MISSING — surfacing as a spurious `VERDICT_FAIL` (exit 1) that the sweep
classifies as a "REAL finding, NEVER auto-retried." The TEST-02 (processor) and TEST-03
(orchestrator) cases happen to be leaf/own-recovery tiers, so this gap was not exposed before
Phase 68 added the backing-service crashes.
**Fix:** After restarting a backing service, also wait for the full dependent set to return healthy
before closing the window — or widen the post-recovery hold so the dwell + reconnect completes
inside the 300s window. For example, after the target-tier health-wait, re-run the same
NDJSON-per-replica healthy gate against the dependent services for redis/rabbitmq targets:
```powershell
$dependentsOf = @{ redis = @('orchestrator','processor-sample','keeper'); rabbitmq = @('orchestrator','processor-sample','keeper') }
foreach ($svc in $scenario.targetContainers) {
    foreach ($dep in ($dependentsOf[$svc] | Select-Object -Unique)) {
        # same bounded NDJSON health-wait loop as STEP F.4, against $dep
    }
}
```

### WR-02: TEST-07 stops redis and rabbitmq sequentially, lengthening effective downtime beyond the planned 45s dwell

**File:** `scripts/phase-67-harness.ps1:274-285` (driven by new row `:94`)
**Issue:** TEST-07 sets `targetContainers = @('redis','rabbitmq')` with `dwellSeconds = 45`. The
sequencer stops every target in a `foreach`, sleeps `dwellSeconds` ONCE, then starts every target
in a `foreach`. With two tiers the second `docker compose stop` only begins after the first
returns, and on recovery the second tier does not start until the first `start` returns, so the
combined-blackout window is `stop(redis)+stop(rabbitmq) → 45s → start(redis)+start(rabbitmq)`.
The dwell comment (`45s ≥ one 30s cron interval so ≥1 full fire happens while the tier is dead`,
line 271-272) reasons about a single-tier crash; for the combined case the two tiers are not down
for an identical 45s, and rabbitmq cold-start (`start_period: 40s`, line 159) plus the dependent
reconnects in WR-01 can push total recovery well past the window remainder. This is not
incorrect per se, but the dwell/window budgeting was sized for single-tier crashes and TEST-07 is
the one scenario most likely to legitimately exceed the 300s window and produce a non-actionable
`VERDICT_FAIL`.
**Fix:** Either stop both tiers, then sleep, then start both (already the structure — confirm the
intent), and explicitly verify the 300s window comfortably covers worst-case recovery for the
two-tier case, or give TEST-07 a longer window / shorter dwell. At minimum, document in the row's
`notes` that TEST-07 recovery time is additive and validate it against the window once live.

## Info

### IN-01: Sweep doc-comment claims "verbatim from lines 31-42" — line ref will drift

**File:** `scripts/phase-68-sweep.ps1:18`
**Issue:** The EXIT-CODE TABLE comment says "copied verbatim from phase-67-harness.ps1 lines 31-42."
The harness table actually spans lines 31-42 today, but hardcoded line-number cross-references rot
the moment either file is edited above that point. The two tables are already duplicated content.
**Fix:** Reference by anchor instead of line number, e.g. "copied from the EXIT-CODE TABLE (D-04)
in phase-67-harness.ps1," and drop the line range.

### IN-02: `verdict`/`startedRuns`/`completeRuns` read raw from JSON without StrictMode-safe guards on field presence

**File:** `scripts/phase-68-sweep.ps1:99-103`
**Issue:** Under `Set-StrictMode -Version Latest`, accessing a non-existent property on the
`ConvertFrom-Json` object throws. The `if ($json)` guard only covers the null case (NO_REPORT). If
a report is ever written with a partial/older schema (e.g. a future analyzer change drops a field,
or a half-written file is read), `$json.Verdict` / `$json.StartedRuns` would throw inside the loop
and, because `$ErrorActionPreference = 'Stop'`, terminate the whole sweep — defeating the explicit
"run-all, no fail-fast" guarantee (D-02). `Missing` and `Duplicates` are required non-null members
in `AnalyzerReport.cs` today, so this is latent, not active.
**Fix:** Read fields defensively, e.g. `$json.PSObject.Properties['Verdict']?.Value` with a fallback,
or wrap the per-scenario report read in its own `try/catch` that records a NO_REPORT-style row so one
malformed report cannot abort the remaining scenarios.

### IN-03: Roll-up artifact path collides with per-scenario analyzer reports dir naming

**File:** `scripts/phase-68-sweep.ps1:116-119`
**Issue:** The sweep writes its roll-up to `<repoRoot>/analyzer-reports/phase-68-summary.json`,
while the per-scenario reports live under `tests/BaseApi.Tests/bin/.../analyzer-reports/<id>.json`
(read at line 90-91). Two different `analyzer-reports` directories now exist with the same name in
different roots; an operator (or a future glob) scanning for `analyzer-reports/*.json` could
conflate the roll-up summary with a scenario report. Not a bug, but a naming foot-gun.
**Fix:** Name the roll-up distinctly (e.g. `phase-68-capstone-summary.json` in a `sweep-reports/`
dir) or document the two-directory layout where the path is constructed.

### IN-04: Comment "copied from harness lines 353-354" points at the wrong lines

**File:** `scripts/phase-68-sweep.ps1:88`
**Issue:** The report-discovery comment says "copied from harness lines 353-354," but in the current
`phase-67-harness.ps1` that range is the `dotnet test ... Analyze_Window_Yields_Pass` invocation and
`$analyzerExit` capture (lines 353-354); the report-discovery `Get-ChildItem` it actually mirrors is
at lines 360-361. Same line-drift class as IN-01 — the reference is already off by the Phase 68 edit
that shifted those lines.
**Fix:** Drop the line numbers; reference "the analyzer-report discovery block (STEP H)" instead.

---

_Reviewed: 2026-06-15T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
