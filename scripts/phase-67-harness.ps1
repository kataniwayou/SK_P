<#
.SYNOPSIS
    Phase 67 fault-injection harness (FAULT-01 / FAULT-02 / FAULT-03).

.DESCRIPTION
    Single self-contained PowerShell orchestrator — a sibling of the 18 phase-NN-close.ps1
    family — that shells the already-proven Phase 65 / Phase 66 artifacts plus docker fault
    ops into one fully-automated scenario run. Invoked once per scenario id:

        pwsh -File scripts/phase-67-harness.ps1 -ScenarioId TEST-01

    Flow (per the resolved D-01..D-16 decisions):

        STEP A   phase-65-up.ps1                 bring the minimal stack up (10 svc types healthy)
        STEP B   phase-65-reset.ps1              FLUSHALL + heal-wait + FK-safe graph DELETE
        STEP B1  docker compose restart orchestrator + wait healthy
                                                 GUARANTEE A CLEAN ORCHESTRATOR (no ghost crons)
        STEP C   dotnet test ~FanOutSeeder       seed v8-fanout-proof (idempotent, self-verifying)
        STEP D   psql sentinel lookup            resolve the v8-fanout-proof workflow id
        STEP E   POST /orchestration/start       activation gate — REQUIRE HTTP 204
        STEP F   observe-loop + crash sequencer  poll fire-count to N, crash whole tier, recover, hold window
        STEP H   dotnet test ~Analyzer           verdict (D-16 env seam: SCENARIO_ID / WINDOW_*_UTC)
        STEP Z   docker compose down             teardown (keep volumes + images)

    Final harness exit code == the analyzer verdict (0 = PASS, non-0 = FAIL). Each infra step
    fails loud with a DISTINCT non-zero code so an infra abort is never mistaken for a verdict.

    EXIT-CODE TABLE (D-04):
        0   analyzer PASS (final verdict green)
        1   analyzer FAIL verdict (mirrors the dotnet test exit — the legitimate verdict path)
        10  bring-up (phase-65-up.ps1) failed
        20  reset (phase-65-reset.ps1) failed
        25  orchestrator clean-restart / health-wait failed (clean-window guarantee, STEP B1)
        30  seeder (dotnet test ~FanOutSeeder) failed
        40  wf-id psql lookup failed/empty
        50  activation gate != 204
        60  fault inject / recover / baseline-firing failed
        70  teardown (docker compose down) failed (NON-FATAL — logs loud, still surfaces the verdict)
        64  bad -ScenarioId argument (unknown scenario key — config-usage error, never collides)

    IMPORTANT (clean-window guarantee, STEP B1): phase-65-reset.ps1 FLUSHALLs Redis and deletes
    the workflow-graph rows, but the long-running orchestrator keeps its already-registered Quartz
    crons in its in-process RAMJobStore — so without this step it keeps firing dozens of GHOST
    workflow crons (NULL payloads, no Step_* labels) that pollute the observation window and make
    the analyzer score everything MISSING (verified live in plan 67-01). Restarting the orchestrator
    AFTER the reset (empty L2 parent index) and BEFORE seed+start drops every stale Quartz job; on
    boot HydrationBackgroundService finds an empty index and schedules nothing, so ONLY the freshly
    seeded+started v8-fanout-proof workflow fires in the window. The same dirty state also caused the
    POST /start 422 observed in 67-01 — a clean orchestrator resolves it.

    MTP FILTER NOTE (verified live in plan 67-01): tests/BaseApi.Tests is xunit.v3 under
    Microsoft.Testing.Platform, which SILENTLY IGNORES `dotnet test --filter` (VSTest syntax) and
    runs the entire 638-test suite. This harness MUST use the MTP-native filter passed AFTER `--`:
    `-- --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*"` (seed) and
    `-- --filter-method "*Analyze_HappyPath_Window_Yields_Pass*"` (analyzer).

.NOTES
    Dev/ops-only tooling. No product source touched. NEVER `docker compose down -v` (keeps data).
    Fully automated — no interactive prompt anywhere (FAULT-03 / V11).
#>

param([Parameter(Mandatory)][string]$ScenarioId)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {

    # -----------------------------------------------------------------------
    # FRAME 2 — prefixed console-trace helper.
    # -----------------------------------------------------------------------
    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-67-harness] $msg" -ForegroundColor $color
    }

    # -----------------------------------------------------------------------
    # FRAME 10 / D-12 — in-script scenario table (the Phase 68 "just data" seam).
    # [ordered] preserves TEST-01-first (D-10). Phase 68 adds rows 03-07 with the
    # SAME shape — { targetContainers, faultType, injectAfterNFires, dwellSeconds, notes }.
    # -----------------------------------------------------------------------
    $Scenarios = [ordered]@{
        'TEST-01' = @{ targetContainers = @();                   faultType = 'none';       injectAfterNFires = 0; dwellSeconds = 0;  notes = 'no-fault baseline' }
        'TEST-02' = @{ targetContainers = @('processor-sample'); faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'processor whole-tier crash' }
    }

    # Validate the requested id against the table BEFORE any docker/psql op (T-67-02).
    # Use a dedicated config-usage code (64) so a bad argument never collides with an
    # infra abort (10-70) or the analyzer FAIL verdict (1).
    if (-not $Scenarios.Contains($ScenarioId)) {
        Write-Phase "unknown scenario '$ScenarioId'. Known: $($Scenarios.Keys -join ', ')" 'Red'
        exit 64
    }
    $scenario = $Scenarios[$ScenarioId]
    Write-Phase "scenario '$ScenarioId' — $($scenario.notes) (faultType=$($scenario.faultType), N=$($scenario.injectAfterNFires), dwell=$($scenario.dwellSeconds)s)"

    # -----------------------------------------------------------------------
    # STEP A — BRING-UP (FRAME 3; code 10).
    # -----------------------------------------------------------------------
    Write-Phase "STEP A: bring-up (phase-65-up.ps1)"
    pwsh -File (Join-Path $PSScriptRoot 'phase-65-up.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Phase "bring-up failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 10 }

    # =======================================================================
    # PER-RUN body for the single requested $ScenarioId. Phase 67 invokes this
    # script once per id (TEST-01 then TEST-02 = two separate invocations per
    # Plan 03 / D-10); the multi-id loop is NOT in this script.
    # =======================================================================

    # -----------------------------------------------------------------------
    # STEP B — RESET (FRAME 3; code 20).
    # -----------------------------------------------------------------------
    Write-Phase "STEP B: reset (phase-65-reset.ps1)"
    pwsh -File (Join-Path $PSScriptRoot 'phase-65-reset.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Phase "reset failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 20 }

    # -----------------------------------------------------------------------
    # STEP B1 — CLEAN-ORCHESTRATOR GUARANTEE (code 25).
    # Restart the orchestrator AFTER the reset (empty L2 parent index + clean DB)
    # and BEFORE seed+start so its in-process Quartz RAMJobStore is wiped of all
    # ghost crons. On boot HydrationBackgroundService SMEMBERS the (now empty)
    # parent index and schedules nothing; the orchestrator healthcheck
    # (/health/ready, gated on initial-hydration-complete) only returns healthy
    # once that empty hydration finished. Waiting for Health=healthy is therefore
    # the proof the orchestrator is clean. ONLY the freshly seeded+started
    # v8-fanout-proof workflow will fire in the window after this.
    # -----------------------------------------------------------------------
    Write-Phase "STEP B1: clean orchestrator (docker compose restart orchestrator) — drop ghost Quartz crons"
    docker compose restart orchestrator | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Phase "orchestrator restart failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 25 }

    # Post-restart NDJSON-per-replica health-wait (FRAME 9). orchestrator is a single
    # named instance (sk-orchestrator) WITH a healthcheck — require it healthy before
    # seed/start so the POST /start does not 422 against a still-hydrating orchestrator.
    $orchDeadline = (Get-Date).AddSeconds(90)
    $orchHealthy = $false
    do {
        $orchInstances = @(docker compose ps orchestrator --format json 2>$null |
            Where-Object { $_ -match '\S' } |
            ForEach-Object { $_ | ConvertFrom-Json })
        if ($orchInstances.Count -gt 0) {
            $orchUnhealthy = @($orchInstances | Where-Object { $_.Health -ne 'healthy' })
            if ($orchUnhealthy.Count -eq 0) { $orchHealthy = $true }
        }
        if (-not $orchHealthy) {
            if ((Get-Date) -ge $orchDeadline) {
                Write-Phase "orchestrator did not return healthy within 90s after restart. Aborting." 'Red'; exit 25
            }
            Start-Sleep -Seconds 2
        }
    } while (-not $orchHealthy)
    Write-Phase "  orchestrator healthy after clean restart (empty hydration — no ghost crons)." 'Gray'

    # -----------------------------------------------------------------------
    # STEP C — SEED (FRAME 4; code 30).
    # MTP-native filter after `--` (NOT VSTest `--filter` — silently ignored under MTP).
    # -----------------------------------------------------------------------
    Write-Phase "STEP C: seed (dotnet test ~FanOutSeeder)"
    dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*" 2>&1 | Out-String | Write-Host
    if ($LASTEXITCODE -ne 0) { Write-Phase "seeder failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 30 }

    # -----------------------------------------------------------------------
    # STEP D — WF-ID (FRAME 5 / D-02; code 40) — static-literal psql, no interpolated input (T-67-01).
    # -----------------------------------------------------------------------
    Write-Phase "STEP D: resolve v8-fanout-proof workflow id (psql)"
    $wfId = (docker compose exec -T postgres psql -U postgres -d stepsdb -tA `
              -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'").Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($wfId)) {
        Write-Phase "could not resolve v8-fanout-proof workflow id (psql exit $LASTEXITCODE). Aborting." 'Red'; exit 40
    }
    Write-Phase "resolved wfId = $wfId"

    # -----------------------------------------------------------------------
    # STEP E — ACTIVATION 204 (FRAME 6 / D-03; code 50).
    # ConvertTo-Json @($wfId) forces the JSON array even for a single id (Pitfall 4).
    # -----------------------------------------------------------------------
    Write-Phase "STEP E: activation gate (POST /orchestration/start, require 204)"
    $startBody = ConvertTo-Json @($wfId)
    try {
        $resp = Invoke-WebRequest -Method Post -Uri 'http://localhost:8080/api/v1/orchestration/start' `
                  -ContentType 'application/json' -Body $startBody -TimeoutSec 15 -ErrorAction Stop
    } catch { Write-Phase "POST /orchestration/start threw: $($_.Exception.Message)" 'Red'; exit 50 }
    if ($resp.StatusCode -ne 204) {
        Write-Phase "activation gate failed — expected 204, got $($resp.StatusCode). Aborting." 'Red'; exit 50
    }
    Write-Phase "  activation accepted (204)." 'Gray'

    # =======================================================================
    # TASK 2: STEP F (observe-loop fire-counter + crash sequencer + recovery
    # health-wait + window hold), STEP H (drain + analyze = verdict), STEP Z
    # (teardown + final `exit $analyzerExit`) are filled in by Task 2 below.
    # =======================================================================

} finally {
    Pop-Location
}
