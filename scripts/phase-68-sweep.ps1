<#
.SYNOPSIS
    Phase 68 capstone sweep — drive scripts/phase-67-harness.ps1 over all 7 scenarios (TEST-01..07).

.DESCRIPTION
    The single operator command for the v8.0.0 milestone proof. A THIN run-all-and-collect driver
    (NOT a re-implementation of any harness step): it loops the already-proven Phase 67 harness once
    per scenario id, captures + classifies each child-process exit code against the Phase 67 EXIT-CODE
    TABLE, reads each per-scenario analyzer report, and emits one roll-up summary.

        pwsh -File scripts/phase-68-sweep.ps1                 # all 7, numeric order
        pwsh -File scripts/phase-68-sweep.ps1 -ScenarioIds TEST-05   # operator re-run of a subset

    Run-all + collect, NO fail-fast (D-02): every one of the 7 scenarios runs even if an earlier one
    is non-PASS, so a single sweep yields all 7 results. Final exit is 0 IFF all selected scenarios
    PASS (harness exit 0), else non-zero.

    EXIT-CODE TABLE (copied verbatim from phase-67-harness.ps1 lines 31-42 — the classification source
    of truth the switch below maps against):
        0   analyzer PASS (final verdict green)
        1   analyzer FAIL verdict (the legitimate verdict path) — a REAL finding, NEVER auto-retried (D-04)
        10  bring-up (phase-65-up.ps1) failed
        20  reset (phase-65-reset.ps1) failed
        25  orchestrator clean-restart / health-wait failed (clean-window guarantee, STEP B1)
        30  seeder (dotnet test ~FanOutSeeder) failed
        40  wf-id psql lookup failed/empty
        50  activation gate != 204
        60  fault inject / recover / baseline-firing failed
        70  teardown (docker compose down) failed (NON-FATAL)
        64  bad -ScenarioId argument (config-usage error)

    FLAKE / RE-RUN POLICY (D-04): a verdict FAIL (exit 1) is a real finding to investigate and is
    NEVER retried away; an INFRA_ABORT (10/20/25/30/40/50/60/70) is operator-re-runnable by invoking
    the bare harness for that single id. This wrapper does NOT auto-retry anything.

    ANTI-PATTERNS (RESEARCH §Anti-Patterns — encoded as hard NOTs): the wrapper NEVER re-scores
    Missing/Duplicates (it only reads + tabulates the analyzer's already-computed values), NEVER reads
    Prometheus deltas, NEVER auto-retries a scenario, and NEVER touches harness machinery (it only
    invokes phase-67-harness.ps1 -ScenarioId <id> as a child `pwsh -File` process).

.NOTES
    Dev/ops-only tooling. No product source touched. The harness owns all docker/dotnet/psql ops and
    re-validates each -ScenarioId against ^[A-Za-z0-9_-]+$; this wrapper only ever feeds the hardcoded
    7 ids (no untrusted input, no shell/psql interpolation).
#>

param(
    [string[]]$ScenarioIds = @('TEST-01','TEST-02','TEST-03','TEST-04','TEST-05','TEST-06','TEST-07')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {

    # FRAME 2 — prefixed console-trace helper (mirrors phase-67-harness.ps1, re-prefixed).
    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-68-sweep] $msg" -ForegroundColor $color
    }

    # Scenario-id order seam (D-02 / baseline-first per Phase 67 D-10). Defaults to all 7 in numeric
    # order; an operator may pass a subset to re-run. The harness is the source of truth for the keys.
    $Ids = @($ScenarioIds)
    Write-Phase "sweep over $($Ids.Count) scenario(s): $($Ids -join ', ')"

    $rows = @()

    foreach ($id in $Ids) {
        Write-Phase "=== scenario $id ==="

        # Invoke the harness as a SEPARATE child process. Because the harness sets
        # $ErrorActionPreference='Stop' inside its OWN process and is launched via `pwsh -File`, its
        # `exit N` surfaces here as $LASTEXITCODE WITHOUT terminating this loop (NO fail-fast, D-02).
        & pwsh -File (Join-Path $PSScriptRoot 'phase-67-harness.ps1') -ScenarioId $id
        $code = $LASTEXITCODE

        # Classify the child exit per the Phase 67 EXIT-CODE TABLE (D-04). No auto-retry on any class.
        $class = switch ($code) {
            0       { 'PASS' }
            1       { 'VERDICT_FAIL' }   # real finding — NEVER auto-retried (D-04)
            64      { 'BAD_ARG' }
            default { 'INFRA_ABORT' }    # 10/20/25/30/40/50/60/70 — operator re-runnable (D-04)
        }
        Write-Phase "  $id -> harness exit $code ($class)" $(if ($code -eq 0) { 'Green' } else { 'Yellow' })

        # Per-scenario analyzer report discovery (copied from harness lines 353-354). The wrapper only
        # READS + tabulates the analyzer's already-computed values — it NEVER re-scores.
        $report = Get-ChildItem -Path (Join-Path $repoRoot 'tests/BaseApi.Tests/bin') -Recurse -Filter "$id.json" -ErrorAction SilentlyContinue |
                  Where-Object { $_.FullName -match 'analyzer-reports' } | Select-Object -First 1
        $json = if ($report) { Get-Content $report.FullName -Raw | ConvertFrom-Json } else { $null }

        # AnalyzerReport fields: Verdict, StartedRuns, CompleteRuns, Missing, Duplicates, TriggerCount,
        # PromImpliedRuns, Reconciliation, CorroborationDetail.
        # zeroMissing = ($json.Missing -eq 0);  effectOnce = (@($json.Duplicates).Count -eq 0)
        $rows += [pscustomobject]@{
            scenarioId   = $id
            verdict      = if ($json) { $json.Verdict } else { 'NO_REPORT' }
            zeroMissing  = if ($json) { ($json.Missing -eq 0) } else { $false }
            effectOnce   = if ($json) { (@($json.Duplicates).Count -eq 0) } else { $false }
            startedRuns  = if ($json) { $json.StartedRuns } else { $null }
            completeRuns = if ($json) { $json.CompleteRuns } else { $null }
            harnessExit  = $code
            class        = $class
        }
    }

    # -----------------------------------------------------------------------
    # ROLL-UP (D-03) — console table + machine artifact. NO new scoring: this only reads + tabulates
    # the 7 per-scenario reports plus each harness exit code.
    # -----------------------------------------------------------------------
    Write-Phase "=== capstone roll-up ==="
    $rows | Format-Table -AutoSize | Out-String | Write-Host

    $summaryDir = Join-Path $repoRoot 'analyzer-reports'
    New-Item -ItemType Directory -Force -Path $summaryDir | Out-Null
    $summaryPath = Join-Path $summaryDir 'phase-68-summary.json'
    $rows | ConvertTo-Json -Depth 5 | Set-Content -Path $summaryPath -Encoding utf8
    Write-Phase "roll-up artifact: $summaryPath" 'Green'

    # Single-glance milestone verdict: 7/7 PASS <=> wrapper exit 0 (D-02). Pass == harness exit 0.
    $passCount = (@($rows | Where-Object { $_.harnessExit -eq 0 }).Count)
    $total = $rows.Count
    Write-Phase "CAPSTONE: $passCount/$total PASS" $(if ($passCount -eq $total) { 'Green' } else { 'Red' })

    # Final exit (D-02): 0 iff every selected scenario was a PASS, else non-zero.
    exit ($(if ($passCount -eq $total) { 0 } else { 1 }))

} finally {
    Pop-Location
}
