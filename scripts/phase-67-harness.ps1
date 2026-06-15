<#
.SYNOPSIS
    Phase 67 fault-injection harness (FAULT-01 / FAULT-02 / FAULT-03).

.DESCRIPTION
    Single self-contained PowerShell orchestrator — a sibling of the 18 phase-NN-close.ps1
    family — that shells the already-proven Phase 65 / Phase 66 artifacts plus docker fault
    ops into one fully-automated scenario run. Invoked once per scenario id:

        pwsh -File scripts/phase-67-harness.ps1 -ScenarioId TEST-01

    Flow (per the resolved D-01..D-16 decisions):

        STEP A0  docker compose build            rebuild images so the container SourceHash matches
                                                 the host-built Processor.Sample (liveness id currency)
        STEP A   compose up --force-recreate
                 + phase-65-up.ps1               bring the minimal stack up (10 svc types healthy)
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
    `-- --filter-method "*Analyze_Window_Yields_Pass*"` (analyzer).

.NOTES
    Dev/ops-only tooling. No product source touched. Teardown keeps volumes + images (never the
    volume-dropping flag) so the proof data survives between runs.
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
        'TEST-03' = @{ targetContainers = @('orchestrator');        faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'orchestrator crash — RAMJobStore re-hydration from L2 parent index' }
        'TEST-04' = @{ targetContainers = @('keeper');              faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'keeper whole-tier crash (BOTH replicas — total liveness blackout)' }
        'TEST-05' = @{ targetContainers = @('redis');               faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'redis crash — L2 slot-array + liveness + BIT probe' }
        'TEST-06' = @{ targetContainers = @('rabbitmq');            faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'rabbitmq crash — nack-requeue redelivery on reconnect' }
        'TEST-07' = @{ targetContainers = @('redis','rabbitmq');    faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'redis + rabbitmq combined crash' }
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
    # STEP A0 — IMAGE REBUILD (code 10) — SourceHash currency guarantee.
    # The processor self-registers its L2 liveness under a processor id derived from its
    # assembly-embedded SourceHash; the seeder (running on the TEST HOST) resolves the
    # workflow's processor by reflecting the host-built Processor.Sample.dll SourceHash. If
    # the running CONTAINER image is stale (built from older source than the working tree),
    # the container registers under a DIFFERENT SourceHash → a DIFFERENT processor id than
    # the one the seeded v8-fanout-proof workflow binds, and the ProcessorLivenessValidator
    # gate correctly 422s the POST /start (0 replicas for the seeded processor id). This was
    # observed live in plan 67-03: container hash 536d0868… (proc 2f6f59b0…) vs host build
    # a67a3ed8… (proc 3cf7023b…). `phase-65-up.ps1` does a plain `docker compose up -d` with
    # NO rebuild, so a stale image silently survives. Building here (and force-recreating in
    # STEP A) guarantees the container SourceHash == the seeder's host-build SourceHash, so
    # the seeded workflow's processor id matches a live replica. Build is no-op fast when the
    # image is already current (BuildKit layer cache).
    # -----------------------------------------------------------------------
    Write-Phase "STEP A0: build images (SourceHash currency — container must match host-built Processor.Sample)"
    docker compose build 2>&1 | Out-String | Write-Host
    if ($LASTEXITCODE -ne 0) { Write-Phase "image build failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 10 }

    # -----------------------------------------------------------------------
    # STEP A — BRING-UP (FRAME 3; code 10).
    # Force-recreate so the just-built image is the one actually running (a stale container
    # from a prior run would otherwise keep its old SourceHash even after the image rebuild).
    # -----------------------------------------------------------------------
    Write-Phase "STEP A: bring-up (docker compose up -d --force-recreate, then phase-65-up.ps1 health gate)"
    docker compose up -d --force-recreate 2>&1 | Out-String | Write-Host
    if ($LASTEXITCODE -ne 0) { Write-Phase "force-recreate up failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 10 }
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

    # -----------------------------------------------------------------------
    # STEP F.1 — RECORD WINDOW START + read the baseline Prometheus fire counter (D-13/D-07).
    # Fire signal = orchestrator_dispatch_sent_total summed across label series — the SAME
    # counter the analyzer's trigger denominator uses (AnalyzerE2ETests.cs:119-123). The
    # harness does NO Prom correctness logic beyond this fire count (Pitfall 5): it does NOT
    # inspect Prom deltas and does NOT abort on a counter discontinuity from the restart;
    # scoring is the analyzer's job (ES-primary).
    # -----------------------------------------------------------------------
    $windowStart = [DateTimeOffset]::UtcNow
    Write-Phase "STEP F: window open at $($windowStart.ToString('o'))"

    function Get-FireCount {
        $r = Invoke-RestMethod -Uri 'http://localhost:9090/api/v1/query?query=orchestrator_dispatch_sent_total' -TimeoutSec 10
        if (-not $r.data.result) { return 0 }
        # [long] (not [int]) — orchestrator_dispatch_sent_total summed across all label series on a
        # long-lived stack could exceed Int32.MaxValue and overflow to a negative baseline, breaking the
        # `observed -ge N` loop. A 64-bit cast is safe for a monotonic counter (IN-02).
        return [long][double]($r.data.result | ForEach-Object { [double]$_.value[1] } | Measure-Object -Sum).Sum
    }
    $fireBaseline = Get-FireCount
    Write-Phase "  baseline fire count = $fireBaseline" 'Gray'

    # 5-minute observation window (300s). Poll cadence ~5s.
    $windowSeconds = 300
    $windowDeadline = (Get-Date).AddSeconds($windowSeconds)

    # -----------------------------------------------------------------------
    # STEP F.2 — OBSERVE LOOP until N observed fires (D-07; N from $scenario.injectAfterNFires),
    # bounded by the window deadline. For TEST-01 (N=0, faultType='none') the inject is skipped
    # entirely — fall straight through to the window hold. For a crash run, poll until
    # current - baseline >= N (proves the cron is ACTUALLY firing — V6). If the window elapses
    # without reaching N, abort loud (exit 60).
    # -----------------------------------------------------------------------
    if ($scenario.faultType -eq 'stop-start') {
        Write-Phase "STEP F.2: observe-loop — waiting for N=$($scenario.injectAfterNFires) fires before inject"
        $reachedN = $false
        while ((Get-Date) -lt $windowDeadline) {
            $observed = (Get-FireCount) - $fireBaseline
            if ($observed -ge $scenario.injectAfterNFires) { $reachedN = $true; break }
            Start-Sleep -Seconds 5
        }
        if (-not $reachedN) {
            Write-Phase "baseline never reached N=$($scenario.injectAfterNFires) fires before window close. Aborting." 'Red'; exit 60
        }
        Write-Phase "  reached N=$($scenario.injectAfterNFires) observed fires — injecting fault." 'Gray'

        # -------------------------------------------------------------------
        # STEP F.3 — CRASH SEQUENCER (FRAME 8 / D-05/06/08; code 60). Whole-tier stop via the
        # compose SERVICE name (Pitfall 2 — never a generated/literal container name), dwell
        # from the table (45s ≥ one 30s cron interval so ≥1 full fire happens while the tier is
        # dead), then start. NOT `docker kill` (restart:unless-stopped would auto-resurrect).
        # -------------------------------------------------------------------
        foreach ($svc in $scenario.targetContainers) {
            Write-Phase "STEP F.3: crashing whole tier '$svc' (docker compose stop)"
            docker compose stop $svc | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Phase "docker compose stop $svc failed." 'Red'; exit 60 }
        }
        Write-Phase "  dwell $($scenario.dwellSeconds)s (tier down)..."
        Start-Sleep -Seconds $scenario.dwellSeconds
        foreach ($svc in $scenario.targetContainers) {
            Write-Phase "STEP F.3: restarting tier '$svc' (docker compose start)"
            docker compose start $svc | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Phase "docker compose start $svc failed." 'Red'; exit 60 }
        }

        # -------------------------------------------------------------------
        # STEP F.4 — POST-START HEALTH-WAIT (FRAME 9 / Pitfall 3; code 60). For each crashed
        # service, require ALL instances Health=healthy before proceeding — so the NEXT run's
        # phase-65-reset (Plan 03 between-runs) does not abort on 0 replicas. NDJSON-per-replica
        # parse copied verbatim from phase-65-up.ps1:37-73 (processor-sample always has a
        # healthcheck — the otel no-healthcheck branch is N/A here). Bounded 90s deadline.
        # -------------------------------------------------------------------
        foreach ($svc in $scenario.targetContainers) {
            Write-Phase "STEP F.4: waiting for crashed tier '$svc' to return healthy (bounded 90s)"
            $svcDeadline = (Get-Date).AddSeconds(90)
            $svcHealthy = $false
            do {
                $instances = @(docker compose ps $svc --format json 2>$null |
                    Where-Object { $_ -match '\S' } |
                    ForEach-Object { $_ | ConvertFrom-Json })
                if ($instances.Count -gt 0) {
                    $unhealthy = @($instances | Where-Object { $_.Health -ne 'healthy' })
                    if ($unhealthy.Count -eq 0) { $svcHealthy = $true }
                }
                if (-not $svcHealthy) {
                    if ((Get-Date) -ge $svcDeadline) {
                        Write-Phase "crashed tier '$svc' did not return healthy before deadline. Aborting." 'Red'; exit 60
                    }
                    Start-Sleep -Seconds 2
                }
            } while (-not $svcHealthy)
            Write-Phase "  tier '$svc' healthy again ($($instances.Count) instance(s))." 'Gray'
        }
    }
    else {
        Write-Phase "STEP F.2: no-fault baseline — no injection (faultType='$($scenario.faultType)')"
    }

    # -----------------------------------------------------------------------
    # STEP F.5 — HOLD OUT THE REST OF THE 5-MIN WINDOW, then record windowEnd. For TEST-01 this
    # is the whole post-activation wait; for TEST-02 it is the remainder after recovery.
    # -----------------------------------------------------------------------
    while (([DateTimeOffset]::UtcNow - $windowStart).TotalSeconds -lt $windowSeconds) {
        Start-Sleep -Seconds 5
    }
    $windowEnd = [DateTimeOffset]::UtcNow
    Write-Phase "STEP F: window closed at $($windowEnd.ToString('o')) ($([int](($windowEnd - $windowStart).TotalSeconds))s)"

    # -----------------------------------------------------------------------
    # STEP H — DRAIN + ANALYZE (FRAME 4 / D-04 / D-16; VERDICT — do NOT remap to an infra code).
    # Set the D-16 env seam (SCENARIO_ID / WINDOW_START_UTC / WINDOW_END_UTC) from the recorded
    # window + scenario id, then invoke the analyzer via the MTP-native filter. The fixture's
    # internal DrainMs (60s) + poll-to-stable (60s) provide the settle, so no extra harness sleep
    # is needed beyond the window close. The analyzer's exit IS the harness verdict.
    # -----------------------------------------------------------------------
    Write-Phase "STEP H: analyze (dotnet test ~Analyzer) for scenario $ScenarioId"
    $env:SCENARIO_ID      = $ScenarioId
    $env:WINDOW_START_UTC = $windowStart.ToString('o')
    $env:WINDOW_END_UTC   = $windowEnd.ToString('o')
    # IN-03: clear the D-16 env seam in a `finally` so a terminating error inside the analyze block
    # ($ErrorActionPreference='Stop') cannot leak SCENARIO_ID / WINDOW_*_UTC into the parent shell
    # (matters when the body is dot-sourced / run interactively; harmless for the one-shot `pwsh -File`).
    try {
        # IN-04: this --filter-method targets the fixture named "*Analyze_Window_Yields_Pass*" for EVERY
        # scenario, including fault runs (TEST-02..07). The capstone (Phase 68) requires a RECOVERED fault
        # run to assert PASS: zero-missing + effect-once hold once the crashed tier rejoins, so exit 0 is
        # what every scenario must produce. A non-zero $analyzerExit is the legitimate verdict FAIL
        # (exit code 1 per the D-04 table) — a REAL finding to investigate (D-01b: a stateful tier whose
        # recovery exceeds the window), NOT an infra error and NOT a normal result. The exit code
        # mirrors the verdict either way. The fixture method and this --filter-method literal MUST stay in
        # sync (renamed together — an out-of-sync literal silently runs the whole 638-test suite).
        dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-method "*Analyze_Window_Yields_Pass*" 2>&1 | Out-String | Write-Host
        $analyzerExit = $LASTEXITCODE
    } finally {
        Remove-Item Env:SCENARIO_ID, Env:WINDOW_START_UTC, Env:WINDOW_END_UTC -ErrorAction SilentlyContinue
    }

    # Locate + echo the analyzer report path (D-04 requires printing it).
    $report = Get-ChildItem -Path (Join-Path $repoRoot 'tests/BaseApi.Tests/bin') -Recurse -Filter "$ScenarioId.json" -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -match 'analyzer-reports' } | Select-Object -First 1
    if ($report) { Write-Phase "analyzer report: $($report.FullName)" 'Green' }
    else { Write-Phase "WARNING: analyzer-reports/$ScenarioId.json not found" 'Yellow' }
    Write-Phase "analyzer verdict exit = $analyzerExit (0=PASS, non-0=FAIL)" $(if ($analyzerExit -eq 0) { 'Green' } else { 'Yellow' })

    # -----------------------------------------------------------------------
    # STEP Z — TEARDOWN (FRAME 11 / D-15; code 70 NON-FATAL). `docker compose down` keeps volumes
    # + images (NEVER `-v`). A down failure logs loud but the harness STILL surfaces the analyzer
    # verdict — the FINAL exit mirrors the analyzer (D-04), never the teardown result.
    # -----------------------------------------------------------------------
    Write-Phase "STEP Z: teardown (docker compose down — keep volumes + images)"
    docker compose down | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Phase "teardown failed (would be exit 70) — surfacing analyzer verdict regardless." 'Yellow' }

    exit $analyzerExit

} finally {
    Pop-Location
}
