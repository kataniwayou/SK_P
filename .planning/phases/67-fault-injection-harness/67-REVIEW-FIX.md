---
phase: 67-fault-injection-harness
fixed_at: 2026-06-14T00:00:00Z
review_path: .planning/phases/67-fault-injection-harness/67-REVIEW.md
iteration: 1
findings_in_scope: 6
fixed: 6
skipped: 0
status: all_fixed
---

# Phase 67: Code Review Fix Report

**Fixed at:** 2026-06-14
**Source review:** .planning/phases/67-fault-injection-harness/67-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 6 (2 Warning + 4 Info; fix_scope=all)
- Fixed: 6
- Skipped: 0

**Verification (run after all fixes):**
- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -warnaserror` → 0 warnings / 0 errors
- `dotnet test ... -- --filter-class "*PassFailEngineFacts*"` → 7 passed / 0 failed
- `phase-67-harness.ps1` parses with 0 errors (PowerShell AST parse)
- Zero `src/**` edits — this phase touches only tests + dev-ops tooling.

**Guardrail compliance:** No verdict SEMANTICS of `PassFailEngine` were changed. The two
warnings that touch the live-proven corroboration logic (WR-01, WR-02) were made
behavior-preserving for the proven happy path: WR-01 is documentation-only (no new failure
path), and WR-02 changes ONLY the standalone (non-window-pinned) branch — the window-pinned
path the Phase 67 harness always exercises is byte-for-byte unchanged. The IN-04 fixture name
and the harness `--filter-method` pattern were kept consistent (no rename; a clarifying comment
was added instead).

## Fixed Issues

### WR-01: Prom corroboration cross-check is one-directional — documented "mirror" case silently absorbed

**Files modified:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs`, `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs`
**Commit:** 26b3c49
**Applied fix:** Chose the documentation option over the symmetric-warning option, because adding
a warning on the negative direction would introduce a new corroboration failure path that risks
flipping `Reconciled → Unreconciled` for the proven scenarios (a spurious-failure-path the
guardrails prohibit). Added a code comment at the corroboration site in `PassFailEngine.cs`
stating the positive-only direction is intentional (the dead-run signal the harness is built to
detect; the negative direction cannot indicate a dead run and Prom is corroboration-only).
Corrected the misleading doc comment in `PassFailEngineFacts.cs` (lines 129-130) to clarify that
within-±1 both directions stay clean, but the OUT-of-tolerance warning is one-directional by
design. No behavior change; all 7 facts still pass.

### WR-02: Standalone-mode tail gap can score a real run MISSING / raise a spurious warning

**Files modified:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`
**Commit:** c50bb8b
**Applied fix:** In standalone mode (seam absent — Phase 66 invocation only), the live Prom AFTER
counter read is now taken at the SAME instant `snapshotUtc` is captured (before poll-to-stable),
via a new `standaloneAfter` capture, instead of a fresh post-poll read. This aligns the Prom delta
cohort with the ES `[windowStart, snapshotUtc]` range, closing the tail-gap that could count a
tail-window dispatch in `DispatchSentDelta` but exclude it from ES. The window-pinned branch
(always used by the Phase 67 harness, since it sets both `WINDOW_*_UTC`) is UNTOUCHED — both its
Prom reads remain pinned to the recorded bounds. Build clean.

### IN-01: `Math.Round` uses banker's rounding on the dispatch-implied run count

**Files modified:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs`, `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`
**Commit:** 0c0c176
**Applied fix:** Chose the comment option over switching to `MidpointRounding.AwayFromZero` to
avoid any chance of shifting a value on the proven path. Added a comment at both round sites
(`promImpliedRuns` in the engine, `triggerCount` in the fixture) documenting that `ToEven` is
intentionally accepted because the value is corroboration-only and the ±1-run tolerance absorbs
any single-unit rounding wobble. Comment-only; no behavior change.

### IN-02: `Get-FireCount` `[int]` cast can overflow on long-lived counters

**Files modified:** `scripts/phase-67-harness.ps1`
**Commit:** 941a91e
**Applied fix:** Widened the fire-count cast from `[int]` to `[long]` so a summed
`orchestrator_dispatch_sent_total` exceeding `Int32.MaxValue` cannot overflow to a negative
baseline and break the `observed -ge N` loop. Added a one-line rationale comment. Script parses
clean (0 errors). The `-ge` loop math is unaffected (64-bit comparison).

### IN-03: `Remove-Item Env:...` on STEP H leaks env vars if the analyzer step throws

**Files modified:** `scripts/phase-67-harness.ps1`
**Commit:** 0129a27 (shared with IN-04 — see note)
**Applied fix:** Wrapped the STEP H analyze block (env-seam set → `dotnet test` → exit capture) in
a `try { ... } finally { Remove-Item Env:SCENARIO_ID, Env:WINDOW_START_UTC, Env:WINDOW_END_UTC
-ErrorAction SilentlyContinue }` so a terminating error under `$ErrorActionPreference='Stop'`
cannot leak the three env vars into the parent shell. Script parses clean.

### IN-04: TEST-02 reuses the `*Analyze_HappyPath_*` filter — name is misleading

**Files modified:** `scripts/phase-67-harness.ps1`
**Commit:** 0129a27 (shared with IN-03 — see note)
**Applied fix:** Per the reviewer's "no behavior change needed" guidance AND the critical guardrail
(a rename would require keeping the `--filter-method` pattern consistent in the same commit and is
risky for the live-proven harness), chose the harness-comment option over renaming the fixture.
Added a comment at the `--filter-method` invocation explaining that a non-zero exit here is the
EXPECTED FAIL verdict for a fault scenario (exit 1 per the D-04 table), not an infra error, and
noting that any future rename must update the filter pattern in the same change. The fixture name
and the filter pattern remain consistent (unchanged).

**Note on shared commit (IN-03 + IN-04):** Both findings modify the same intertwined STEP H block
in `phase-67-harness.ps1` — the IN-03 `try/finally` wraps the exact `dotnet test` line that carries
the IN-04 clarifying comment. They were committed together (0129a27) because the hunks are
inseparable; each finding's change is independently described above.

## Skipped Issues

None — all 6 in-scope findings were fixed.

---

_Fixed: 2026-06-14_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
