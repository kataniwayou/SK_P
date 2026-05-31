# Phase 21 close gate — v3.4.0 (triple-SHA)
# v3.4.0 Closeout Hygiene — HARDEN-03 (shared L2 key source of truth)
# ---------------------------------------------------------------------------
# Honors:
#   - Phase 3 D-15 byte-identical psql \l SHA-256 invariant
#   - TEST-REDIS-04: docker exec sk-redis redis-cli --scan piped through a
#     locale-stable sort then SHA-256, BEFORE = AFTER across the full suite
#   - TEST-RMQ-04/05 (D-09): docker exec sk-rabbitmq rabbitmqctl -q list_queues name
#     piped through a locale-stable sort then SHA-256, BEFORE = AFTER (net-zero queue
#     leak — temporary/auto-delete per-class test queues + the stable
#     orchestrator-{InstanceId} queue present in both snapshots while the stack is up)
#   - Phase 3 D-18 3-consecutive-GREEN cadence
#
# This is the TRIPLE-snapshot gate (psql \l + redis-cli --scan + rabbitmqctl list_queues).
# The full v3.4.0 stack MUST be up (postgres, redis, rabbitmq, otel-collector,
# elasticsearch, prometheus, orchestrator, baseapi-service) so TEST-RMQ-02
# (CorrelationPropagationE2ETests, Category=RealStack) runs live in the suite.
#
# Three deferred-at-v3.3.0 smells are FIXED here:
#   A. unparseable fact-count is a HARD failure (no -1 false-green aliasing)
#   B. compose ps --format json read tolerates object-OR-array
#   C. one canonical service list (no PS1/SH divergence), including rabbitmq +
#      orchestrator + baseapi-service (D-12 wget fix makes baseapi-service healthy)
#
# Usage:
#   pwsh -File scripts/phase-21-close.ps1
#
# Exit codes:
#   0  — both build configs zero-warning, all three runs GREEN, all three SHA-256 invariants held
#   1  — invariant violation OR any build/test run RED OR unparseable fact count (Smell A)
#   2  — environment misconfigured (compose stack not healthy)
# ---------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    Write-Host "Phase 21 close gate (triple-SHA) starting from $repoRoot" -ForegroundColor Cyan

    # ---- Pre-flight: compose stack healthy (Smell C — ONE canonical Phase-21 list) ----
    Write-Host "Pre-flight: compose stack health check..." -ForegroundColor Cyan
    $services = @('postgres', 'redis', 'rabbitmq', 'otel-collector', 'elasticsearch', 'prometheus', 'orchestrator', 'baseapi-service')
    foreach ($svc in $services) {
        # Smell B — tolerate object-OR-array compose ps --format json output (shape varies by compose version).
        # A stopped/absent service yields empty output → ConvertFrom-Json is $null; report it as not-running
        # (StrictMode would otherwise throw on a missing .Health property).
        $raw = docker compose ps $svc --format json 2>$null | Out-String
        $parsed = if ([string]::IsNullOrWhiteSpace($raw)) { $null } else { $raw | ConvertFrom-Json }
        $health = if ($null -eq $parsed) { 'not-running' }
                  elseif ($parsed -is [array]) { $parsed[0].Health }
                  else { $parsed.Health }
        # otel-collector has no in-image healthcheck (distroless) — allowed non-healthy.
        if ($health -ne 'healthy' -and $svc -ne 'otel-collector') {
            Write-Host "Service '$svc' is not healthy (Health=$health). Aborting." -ForegroundColor Red
            exit 2
        }
    }

    # ---- BEFORE snapshots (triple) ----
    Write-Host "Capturing BEFORE snapshots..." -ForegroundColor Cyan

    # (a) Phase 3 D-15 psql \l invariant
    $beforePg = (docker compose exec -T postgres psql -U postgres -lqt | Out-String).Trim()
    $beforePgHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($beforePg))
    )).Hash.ToLower()
    Write-Host "  psql \l SHA-256 BEFORE = $beforePgHash" -ForegroundColor Gray

    # (b) TEST-REDIS-04 redis-cli --scan invariant — locale-locked.
    $beforeRedis = (docker exec sk-redis redis-cli --scan | Sort-Object -CaseSensitive | Out-String).Trim()
    $beforeRedisHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($beforeRedis))
    )).Hash.ToLower()
    Write-Host "  redis-cli --scan SHA-256 BEFORE = $beforeRedisHash" -ForegroundColor Gray

    # (c) TEST-RMQ-04/05 D-09 rabbitmqctl list_queues invariant — locale-locked. -q per Pitfall 3.
    $beforeRmq = (docker exec sk-rabbitmq rabbitmqctl -q list_queues name | Sort-Object -CaseSensitive | Out-String).Trim()
    $beforeRmqHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($beforeRmq))
    )).Hash.ToLower()
    Write-Host "  rabbitmqctl list_queues SHA-256 BEFORE = $beforeRmqHash" -ForegroundColor Gray

    # ---- Zero-warning build gate, BOTH configs ----
    Write-Host "dotnet clean SK_P.sln..." -ForegroundColor Cyan
    dotnet clean SK_P.sln | Out-Null

    foreach ($cfg in @('Release', 'Debug')) {
        Write-Host "dotnet build SK_P.sln -c $cfg..." -ForegroundColor Cyan
        $buildOut = dotnet build SK_P.sln -c $cfg 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Build ($cfg) FAILED (TreatWarningsAsErrors makes a warning fatal)." -ForegroundColor Red
            Write-Host $buildOut -ForegroundColor DarkGray
            exit 1
        }
        Write-Host "  Build ($cfg) zero-warning, exit 0." -ForegroundColor Green
    }

    # ---- 3-GREEN cadence (Phase 3 D-18) ----
    # FULL suite, no Category filter — TEST-RMQ-02 (Category=RealStack) MUST run with the stack up.
    $runResults = @()
    for ($i = 1; $i -le 3; $i++) {
        Write-Host "Run $i of 3: dotnet test (full suite)..." -ForegroundColor Cyan
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $output = dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build 2>&1 | Out-String
        $sw.Stop()
        $exit = $LASTEXITCODE

        # Smell A — an unparseable fact count is a HARD failure (never coerce to -1; three failed
        # parses would alias -1==-1==-1 and false-green the 3-GREEN equality check below).
        $passedMatch = [Regex]::Match($output, 'Passed:\s+(\d+)')
        if (-not $passedMatch.Success) {
            Write-Host "Run $i — could not parse 'Passed: <n>' from dotnet test output. Aborting (Smell A guard)." -ForegroundColor Red
            Write-Host $output -ForegroundColor DarkGray
            exit 1
        }
        $passed = [int]$passedMatch.Groups[1].Value

        $runResults += [PSCustomObject]@{ Run = $i; Exit = $exit; Passed = $passed; Duration = $sw.Elapsed }
        Write-Host "  Run $i — Exit=$exit, Passed=$passed, Duration=$($sw.Elapsed)" -ForegroundColor Gray

        if ($exit -ne 0) {
            Write-Host "Run $i FAILED with exit code $exit." -ForegroundColor Red
            Write-Host $output -ForegroundColor DarkGray
            exit 1
        }
    }

    $distinctPassed = @($runResults | Select-Object -ExpandProperty Passed -Unique)
    if ($distinctPassed.Count -ne 1) {
        Write-Host "3-GREEN cadence violation — fact counts diverge: $($runResults.Passed -join ', ')" -ForegroundColor Red
        exit 1
    }
    Write-Host "3-GREEN cadence passed — $($distinctPassed[0]) facts GREEN across 3 runs." -ForegroundColor Green
    # Phase 21 adds new facts vs the v3.3.0 baseline — informational print, not a warning.
    Write-Host "  Observed Phase 21 fact count: $($distinctPassed[0])." -ForegroundColor Gray

    # ---- AFTER snapshots (triple) ----
    Write-Host "Capturing AFTER snapshots..." -ForegroundColor Cyan
    $afterPg = (docker compose exec -T postgres psql -U postgres -lqt | Out-String).Trim()
    $afterPgHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($afterPg))
    )).Hash.ToLower()
    Write-Host "  psql \l SHA-256 AFTER  = $afterPgHash" -ForegroundColor Gray

    $afterRedis = (docker exec sk-redis redis-cli --scan | Sort-Object -CaseSensitive | Out-String).Trim()
    $afterRedisHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($afterRedis))
    )).Hash.ToLower()
    Write-Host "  redis-cli --scan SHA-256 AFTER  = $afterRedisHash" -ForegroundColor Gray

    $afterRmq = (docker exec sk-rabbitmq rabbitmqctl -q list_queues name | Sort-Object -CaseSensitive | Out-String).Trim()
    $afterRmqHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($afterRmq))
    )).Hash.ToLower()
    Write-Host "  rabbitmqctl list_queues SHA-256 AFTER  = $afterRmqHash" -ForegroundColor Gray

    # ---- Invariant assertions (triple) ----
    $allGood = $true

    if ($beforePgHash -ne $afterPgHash) {
        Write-Host "Phase 3 D-15 INVARIANT VIOLATED: psql \l SHA-256 BEFORE != AFTER" -ForegroundColor Red
        Write-Host "  BEFORE = $beforePgHash" -ForegroundColor Red
        Write-Host "  AFTER  = $afterPgHash" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "Phase 3 D-15 invariant HELD: psql \l SHA-256 = $afterPgHash" -ForegroundColor Green
    }

    if ($beforeRedisHash -ne $afterRedisHash) {
        Write-Host "TEST-REDIS-04 INVARIANT VIOLATED: redis-cli --scan SHA-256 BEFORE != AFTER" -ForegroundColor Red
        Write-Host "  BEFORE = $beforeRedisHash" -ForegroundColor Red
        Write-Host "  AFTER  = $afterRedisHash" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "TEST-REDIS-04 invariant HELD: redis-cli --scan SHA-256 = $afterRedisHash" -ForegroundColor Green
    }

    if ($beforeRmqHash -ne $afterRmqHash) {
        Write-Host "TEST-RMQ-04/05 INVARIANT VIOLATED: rabbitmqctl list_queues SHA-256 BEFORE != AFTER" -ForegroundColor Red
        Write-Host "  BEFORE = $beforeRmqHash" -ForegroundColor Red
        Write-Host "  AFTER  = $afterRmqHash" -ForegroundColor Red
        Write-Host "  Investigate a leaked queue: 'docker exec sk-rabbitmq rabbitmqctl -q list_queues name'" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "TEST-RMQ-04/05 invariant HELD: rabbitmqctl list_queues SHA-256 = $afterRmqHash" -ForegroundColor Green
    }

    if (-not $allGood) {
        Write-Host "Phase 21 close gate FAILED. Resolve violations and re-run." -ForegroundColor Red
        exit 1
    }

    Write-Host "Phase 21 close gate PASSED." -ForegroundColor Green
    Write-Host "  Total facts GREEN: $($distinctPassed[0])" -ForegroundColor Green
    Write-Host "  psql \l SHA-256:              $afterPgHash" -ForegroundColor Green
    Write-Host "  redis-cli --scan SHA-256:     $afterRedisHash" -ForegroundColor Green
    Write-Host "  rabbitmqctl list_queues SHA-256: $afterRmqHash" -ForegroundColor Green
    Write-Host "Operator: append these three SHA values + the Passed count to .planning/STATE.md Phase 21 P01 close entry." -ForegroundColor Cyan

    exit 0
} finally {
    Pop-Location
}
