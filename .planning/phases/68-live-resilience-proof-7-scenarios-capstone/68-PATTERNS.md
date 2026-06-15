# Phase 68: Live Resilience Proof — 7 Scenarios (Capstone) - Pattern Map

**Mapped:** 2026-06-15
**Files analyzed:** 4 (1 CREATE script, 1 MODIFY script-data, 1 MODIFY+MODIFY C#/script-literal pair, 1 CREATE artifact)
**Analogs found:** 4 / 4 (every deliverable has an in-repo analog; this phase is "just data + a thin wrapper")

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `scripts/phase-68-sweep.ps1` (CREATE) | PowerShell ops-orchestration (loop wrapper) | batch / process-invocation + roll-up | `scripts/phase-67-harness.ps1` (structure) + `scripts/phase-NN-close.ps1` family (close-script idiom) | role-match (no existing multi-scenario LOOP wrapper; harness is the per-scenario sibling it wraps) |
| `scripts/phase-67-harness.ps1` `$Scenarios` table (MODIFY: +5 rows) | scenario-data (config seam) | transform / pure-data | The existing TEST-01 + TEST-02 rows in the SAME `[ordered]@{}` table (lines 88-89) | exact (identical hashtable row shape) |
| `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` method rename (MODIFY, cosmetic) | C# test-fixture | request-response (verdict assertion) | The current `Analyze_HappyPath_Window_Yields_Pass` decl + its `[Fact]` (line 84-85) | exact (rename in place) |
| `scripts/phase-67-harness.ps1` two `--filter-method` literals (MODIFY, sync with rename) | scenario-data / literal-string | transform | The two literals at lines 58 (doc-comment) + 346 (call site) | exact |
| `analyzer-reports/phase-68-summary.json` + console/.md table (CREATE) | report-artifact | file-I/O / read-7-JSON + tabulate | The per-scenario `analyzer-reports/{id}.json` (AnalyzerReport shape) + repo-root `psql-*.txt` artifact convention | role-match (no existing roll-up; aggregates existing per-scenario reports) |

**Template-selection note (D-02 best structural template):** For the multi-scenario LOOP wrapper specifically, `scripts/phase-67-harness.ps1` is the best structural template — NOT a `phase-NN-close.ps1`. The close-scripts are single-pass net-zero gates (no loop, no per-iteration exit-code capture). The harness already owns: the `Write-Phase` prefixed console-trace helper, the EXIT-CODE TABLE the wrapper must classify against, the `Push-Location $repoRoot / try { } finally { Pop-Location }` frame, the `docker compose ps --format json` NDJSON-per-replica health parse, and the `Get-ChildItem ... -Filter "$id.json" | Where-Object FullName -match 'analyzer-reports'` report-discovery the wrapper reuses. The wrapper should adopt the harness's frame + helper, set the close-script header tone (`$ErrorActionPreference='Stop'`, `Set-StrictMode -Version Latest`, `[phase-68-sweep]`-prefixed `Write-Host`), and invoke the harness as a child `pwsh -File` process per id.

---

## Pattern Assignments

### `scripts/phase-68-sweep.ps1` (PowerShell ops-orchestration, batch loop)

**Analog:** `scripts/phase-67-harness.ps1` (structure + helpers) — the per-scenario script this wraps.

**Header + frame pattern** (harness lines 66-73, 370-372) — copy verbatim into the wrapper:
```powershell
param(...)   # see "optional id-subset arg" below; default all-7

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    # ... loop body ...
} finally {
    Pop-Location
}
```

**Prefixed console-trace helper** (harness lines 78-80) — copy, re-prefix to `[phase-68-sweep]`:
```powershell
function Write-Phase([string]$msg, [string]$color = 'Cyan') {
    Write-Host "[phase-68-sweep] $msg" -ForegroundColor $color
}
```

**Scenario-id order seam** — drive these 7 in numeric order (D-02, baseline-first per Phase 67 D-10). These keys MUST match the `$Scenarios` table keys in the harness (which is the source of truth):
```powershell
$Ids = @('TEST-01','TEST-02','TEST-03','TEST-04','TEST-05','TEST-06','TEST-07')
```

**Core loop: invoke harness + capture exit code, NO fail-fast** (RESEARCH §Pattern 1; verified against harness `exit $analyzerExit` line 368). Because the harness sets `$ErrorActionPreference='Stop'` *inside* its own process and is launched as a separate `pwsh -File` process, its `exit N` surfaces as `$LASTEXITCODE` in the wrapper WITHOUT terminating the wrapper loop:
```powershell
foreach ($id in $Ids) {
    Write-Phase "=== scenario $id ==="
    & pwsh -File (Join-Path $PSScriptRoot 'phase-67-harness.ps1') -ScenarioId $id
    $code = $LASTEXITCODE

    $class = switch ($code) {
        0       { 'PASS' }
        1       { 'VERDICT_FAIL' }   # real finding — NEVER retried (D-04)
        64      { 'BAD_ARG' }
        default { 'INFRA_ABORT' }    # 10/20/25/30/40/50/60/70 — operator re-runnable (D-04)
    }
    # ... record ($id, $code, $class) + read the per-scenario JSON (next pattern) ...
}
```

**Exit-code classification source** — copy the table from harness lines 31-42 verbatim into the wrapper header comment so the classification switch is self-documenting:
```
0   analyzer PASS (final verdict green)
1   analyzer FAIL verdict (the legitimate verdict path) — NEVER auto-retried (D-04)
10  bring-up failed        20  reset failed           25  orchestrator clean-restart failed
30  seeder failed          40  wf-id psql lookup failed  50  activation gate != 204
60  fault inject/recover failed   70  teardown failed (NON-FATAL)
64  bad -ScenarioId argument (config-usage error)
```

**Per-scenario report discovery** (RESEARCH §Pattern 2; copied from harness report-discovery lines 353-354):
```powershell
$report = Get-ChildItem -Path (Join-Path $repoRoot 'tests/BaseApi.Tests/bin') -Recurse -Filter "$id.json" `
            -ErrorAction SilentlyContinue |
          Where-Object { $_.FullName -match 'analyzer-reports' } | Select-Object -First 1
$json = if ($report) { Get-Content $report.FullName -Raw | ConvertFrom-Json } else { $null }
# AnalyzerReport fields: Verdict, StartedRuns, CompleteRuns, Missing, Duplicates,
#   TriggerCount, PromImpliedRuns, Reconciliation, CorroborationDetail.
# zeroMissing = ($json.Missing -eq 0);  effectOnce = ($json.Duplicates.Count -eq 0)
# Cross-check: $json.Verdict should equal PASS iff $code -eq 0 (a mismatch is itself a finding).
```

**Final exit** (D-02): exit 0 iff all 7 are PASS (`$code -eq 0`), else non-zero. Anti-pattern guard (RESEARCH §Anti-Patterns): the wrapper NEVER re-scores missing/duplicates, NEVER reads Prom deltas, NEVER auto-retries a FAIL, NEVER touches harness machinery.

**Optional id-subset arg** (Claude's discretion, D-02): a `[string[]]$ScenarioIds` param defaulting to the all-7 list is acceptable convenience; a single-id re-run is just the bare harness, so this is non-essential.

---

### `scripts/phase-67-harness.ps1` — `$Scenarios` table (+5 rows TEST-03..07) (scenario-data, transform)

**Analog:** the existing TEST-01 + TEST-02 rows in the SAME `[ordered]@{}` table.

**Exact existing rows to mirror** (harness lines 87-90 — copy the column alignment + key order verbatim):
```powershell
$Scenarios = [ordered]@{
    'TEST-01' = @{ targetContainers = @();                   faultType = 'none';       injectAfterNFires = 0; dwellSeconds = 0;  notes = 'no-fault baseline' }
    'TEST-02' = @{ targetContainers = @('processor-sample'); faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'processor whole-tier crash' }
}
```

**The 5 new rows to append** (D-01 uniform recipe: `faultType='stop-start'`, `injectAfterNFires=4`, `dwellSeconds=45`; D-01a targets are compose SERVICE names, whole-tier per Phase 67 D-06). TEST-04 → `@('keeper')` is BOTH replicas (total liveness blackout, Note 1); TEST-07 → `@('redis','rabbitmq')` combined:
```powershell
    'TEST-03' = @{ targetContainers = @('orchestrator');        faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'orchestrator crash — RAMJobStore re-hydration from L2 parent index' }
    'TEST-04' = @{ targetContainers = @('keeper');              faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'keeper whole-tier crash (BOTH replicas — total liveness blackout)' }
    'TEST-05' = @{ targetContainers = @('redis');               faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'redis crash — L2 slot-array + liveness + BIT probe' }
    'TEST-06' = @{ targetContainers = @('rabbitmq');            faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'rabbitmq crash — nack-requeue redelivery on reconnect' }
    'TEST-07' = @{ targetContainers = @('redis','rabbitmq');    faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'redis + rabbitmq combined crash' }
```
(Notes text is illustrative — keep within the existing column style; the load-bearing fields are `targetContainers` / `faultType` / `injectAfterNFires` / `dwellSeconds`.)

**Why no other harness change is needed:** the crash sequencer (STEP F.3, lines 269-280) already loops `foreach ($svc in $scenario.targetContainers)` over the array, and the post-start health-wait (STEP F.4, lines 289-309) already does the NDJSON-per-replica parse per service — so a 2-replica `keeper` tier or a 2-service `@('redis','rabbitmq')` row flows through the existing control flow with no new code (Phase 67 D-12 "just data" seam).

---

### `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — method rename (C# test-fixture, cosmetic) + harness literal sync

**Analog:** the current method declaration (the ONLY change site in the .cs file).

**Exact current declaration** (AnalyzerE2ETests.cs lines 84-85). NOTE: the `[Trait("Category", ...)]` and `[Collection("Observability")]` attributes are at the CLASS level (lines 49-52); the method itself carries ONLY `[Fact]`:
```csharp
    [Fact]
    public async Task Analyze_HappyPath_Window_Yields_Pass()
```

**Rename target** (RESEARCH §IN-04 resolution; verdict-neutral — the fixture now asserts PASS for *recovered* fault runs too, not just the happy path):
```csharp
    [Fact]
    public async Task Analyze_Window_Yields_Pass()     // or Analyze_Scenario_Window_Yields_Pass
```
Also fix the misleading "TEST-01-shaped happy path" wording in the class XML-doc (line 23) and the `DefaultScenarioId = "TEST-01"` comment context if touched — but `DefaultScenarioId` itself stays `TEST-01` (the harness always passes `SCENARIO_ID`, line 88).

**Harness literal sync — TWO call sites that MUST change in the SAME commit** (MTP-filter constraint [[mtp-filter-syntax]]: keep the `-- --filter-method "*...*"` form; `dotnet test --filter` VSTest syntax is silently ignored):
- `scripts/phase-67-harness.ps1` line 58 (doc-comment): `` `-- --filter-method "*Analyze_HappyPath_Window_Yields_Pass*"` (analyzer). ``
- `scripts/phase-67-harness.ps1` line 346 (the live call site):
```powershell
    dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-method "*Analyze_HappyPath_Window_Yields_Pass*" 2>&1 | Out-String | Write-Host
```
Both must become `"*Analyze_Window_Yields_Pass*"` (or whatever the chosen new name is).

**Stale-comment fix — REQUIRED even if the rename is skipped** (harness lines 340-345, the IN-04 comment). The current text *"for a fault scenario a FAIL verdict is the EXPECTED outcome"* is now actively WRONG for the capstone — a *recovered* fault run MUST assert PASS (zero-missing + effect-once hold). Reword to: a recovered fault run yields PASS (exit 0); a non-recovered run yields the legitimate verdict-FAIL (exit 1) which is a real finding, not an infra error. (RESEARCH: rename is OPTIONAL for correctness — the wiring already works via `Assert.True(report.Verdict == Verdict.Pass)` at line 197 — but the comment fix is mandatory; rename is RECOMMENDED to drop the misleading "HappyPath" signal.)

---

### `analyzer-reports/phase-68-summary.json` + console/.md table (report-artifact, read-7-JSON + tabulate)

**Analog:** the per-scenario `analyzer-reports/{id}.json` (the AnalyzerReport the harness/fixture writes) + the repo-root `psql-*.txt` artifact convention (e.g. `psql-after-phase11-final.txt`).

**Shape** (D-03; derived from the 7 per-scenario JSONs + each harness exit code — NO new scoring). A 7-row table, one row per scenario:
```
scenarioId · verdict · zeroMissing · effectOnce · startedRuns · completeRuns · harnessExit · class
```
Where `verdict = $json.Verdict`, `zeroMissing = ($json.Missing -eq 0)`, `effectOnce = ($json.Duplicates.Count -eq 0)`, `startedRuns = $json.StartedRuns`, `completeRuns = $json.CompleteRuns`, `harnessExit = $code`, `class` = the PASS/VERDICT_FAIL/INFRA_ABORT/BAD_ARG classification from the wrapper switch.

**Path** (Claude's discretion, default per RESEARCH §Pattern 3 + Open Q 3):
- Machine: `analyzer-reports/phase-68-summary.json` (sibling to the 7 per-scenario reports — note this dir lives under `tests/BaseApi.Tests/bin/**/analyzer-reports/` at runtime; mirror the harness discovery path or write to a repo-root sibling).
- Human: a console table in the `[phase-68-sweep]`-prefixed `Write-Host` close-script style, optionally mirrored to a repo-root `phase-68-summary.txt` (matching the `psql-*.txt` convention).

**Single-glance milestone proof:** the summary's only verdict is "7/7 PASS" ⇔ wrapper exit 0. It adds no scoring beyond reading + tabulating the existing reports.

---

## Shared Patterns

### Prefixed console-trace + StrictMode frame
**Source:** `scripts/phase-67-harness.ps1` lines 66-80, 370-372 (and the `phase-NN-close.ps1` family header tone)
**Apply to:** `scripts/phase-68-sweep.ps1`
```powershell
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-68-sweep] $msg" -ForegroundColor $color
    }
    # ...
} finally { Pop-Location }
```

### Exit-code-as-verdict classification (D-04)
**Source:** `scripts/phase-67-harness.ps1` lines 31-42 (EXIT-CODE TABLE) + line 368 (`exit $analyzerExit`)
**Apply to:** the wrapper loop (classify each child-process `$LASTEXITCODE`) AND the roll-up `class` column
```
0 → PASS | 1 → VERDICT_FAIL (never retried) | 64 → BAD_ARG | 10/20/25/30/40/50/60/70 → INFRA_ABORT (operator re-runnable)
```

### Per-scenario analyzer report discovery
**Source:** `scripts/phase-67-harness.ps1` lines 353-354
**Apply to:** the wrapper (read each `{id}.json` for the roll-up) — already excerpted above.

### MTP filter form (project constraint)
**Source:** [[mtp-filter-syntax]] + harness lines 54-58 / 188 / 346
**Apply to:** any `dotnet test` invocation touched by the rename — keep `-- --filter-method "*...*"`, never VSTest `--filter`.

## No Analog Found

None. Every deliverable maps to an in-repo analog. Two deliverables (the loop wrapper and the roll-up summary) have no *identical* predecessor (there was never a multi-scenario sweep before — Phase 67 invoked the harness twice manually), but both are direct compositions of existing harness helpers, so they are role-matched, not analog-less.

## Metadata

**Analog search scope:** `scripts/` (harness + close-script family), `tests/BaseApi.Tests/Observability/` (analyzer fixture), repo-root artifact convention (`psql-*.txt`).
**Files scanned:** `scripts/phase-67-harness.ps1` (full, 373 lines), `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` (lines 1-90 — declaration + attributes + env seam), `scripts/phase-62-close.ps1` (header — close-script idiom confirmation), 16 `phase-NN-close.ps1` filenames enumerated.
**Pattern extraction date:** 2026-06-15
