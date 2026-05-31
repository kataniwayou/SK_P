# Phase 22 close gate — v3.4.0 (triple-SHA)
# L2 root-parent restructure + processor self-registration (liveness gate)
# ---------------------------------------------------------------------------
# Identical protocol to scripts/phase-21-close.ps1 (the proven v3.4.0 triple-SHA gate):
#   - Phase 3 D-15 byte-identical psql \l SHA-256 invariant
#   - TEST-REDIS-04: docker exec sk-redis redis-cli --scan | sort | SHA-256, BEFORE = AFTER
#     (Phase 22 introduces the shared skp: parent-index SET to the prod keyspace tests share —
#      every test SREMs its own ids + deletes its known keys, so the scan SHA must return to BEFORE)
#   - TEST-RMQ-04/05 (D-09): docker exec sk-rabbitmq rabbitmqctl -q list_queues name | sort | SHA-256, BEFORE = AFTER
#   - Phase 3 D-18 3-consecutive-GREEN cadence (full suite, no Category filter — RealStack E2E run live)
#
# The full v3.4.0 stack MUST be up healthy (postgres, redis, rabbitmq, otel-collector,
# elasticsearch, prometheus, orchestrator, baseapi-service).
#
# Usage:
#   pwsh -File scripts/phase-22-close.ps1
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
    Write-Host "Phase 22 close gate (triple-SHA) starting from $repoRoot" -ForegroundColor Cyan

    # ---- Pre-flight: compose stack healthy (ONE canonical v3.4.0 list) ----
    Write-Host "Pre-flight: compose stack health check..." -ForegroundColor Cyan
    $services = @('postgres', 'redis', 'rabbitmq', 'otel-collector', 'elasticsearch', 'prometheus', 'orchestrator', 'baseapi-service')
    foreach ($svc in $services) {
        $raw = docker compose ps $svc --format json 2>$null | Out-String
        $parsed = if ([string]::IsNullOrWhiteSpace($raw)) { $null } else { $raw | ConvertFrom-Json }
        $health = if ($null -eq $parsed) { 'not-running' }
                  elseif ($parsed -is [array]) { $parsed[0].Health }
                  else { $parsed.Health }
        if ($health -ne 'healthy' -and $svc -ne 'otel-collector') {
            Write-Host "Service '$svc' is not healthy (Health=$health). Aborting." -ForegroundColor Red
            exit 2
        }
    }

    # ---- BEFORE snapshots (triple) ----
    Write-Host "Capturing BEFORE snapshots..." -ForegroundColor Cyan

    $beforePg = (docker compose exec -T postgres psql -U postgres -lqt | Out-String).Trim()
    $beforePgHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($beforePg))
    )).Hash.ToLower()
    Write-Host "  psql \l SHA-256 BEFORE = $beforePgHash" -ForegroundColor Gray

    $beforeRedis = (docker exec sk-redis redis-cli --scan | Sort-Object -CaseSensitive | Out-String).Trim()
    $beforeRedisHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($beforeRedis))
    )).Hash.ToLower()
    Write-Host "  redis-cli --scan SHA-256 BEFORE = $beforeRedisHash" -ForegroundColor Gray

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

    # ---- 3-GREEN cadence (Phase 3 D-18) — FULL suite, RealStack E2E run live ----
    $runResults = @()
    for ($i = 1; $i -le 3; $i++) {
        Write-Host "Run $i of 3: dotnet test (full suite)..." -ForegroundColor Cyan
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $output = dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build 2>&1 | Out-String
        $sw.Stop()
        $exit = $LASTEXITCODE

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
    Write-Host "  Observed Phase 22 fact count: $($distinctPassed[0])." -ForegroundColor Gray

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
        Write-Host "  Investigate a leaked skp: key (parent-index member or root/step/proc):" -ForegroundColor Red
        Write-Host "    'docker exec sk-redis redis-cli --scan'" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "TEST-REDIS-04 invariant HELD: redis-cli --scan SHA-256 = $afterRedisHash" -ForegroundColor Green
    }

    if ($beforeRmqHash -ne $afterRmqHash) {
        Write-Host "TEST-RMQ-04/05 INVARIANT VIOLATED: rabbitmqctl list_queues SHA-256 BEFORE != AFTER" -ForegroundColor Red
        Write-Host "  BEFORE = $beforeRmqHash" -ForegroundColor Red
        Write-Host "  AFTER  = $afterRmqHash" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "TEST-RMQ-04/05 invariant HELD: rabbitmqctl list_queues SHA-256 = $afterRmqHash" -ForegroundColor Green
    }

    if (-not $allGood) {
        Write-Host "Phase 22 close gate FAILED. Resolve violations and re-run." -ForegroundColor Red
        exit 1
    }

    Write-Host "Phase 22 close gate PASSED." -ForegroundColor Green
    Write-Host "  Total facts GREEN: $($distinctPassed[0])" -ForegroundColor Green
    Write-Host "  psql \l SHA-256:              $afterPgHash" -ForegroundColor Green
    Write-Host "  redis-cli --scan SHA-256:     $afterRedisHash" -ForegroundColor Green
    Write-Host "  rabbitmqctl list_queues SHA-256: $afterRmqHash" -ForegroundColor Green
    Write-Host "Operator: append these three SHA values + the Passed count to .planning/STATE.md Phase 22 close entry." -ForegroundColor Cyan

    exit 0
} finally {
    Pop-Location
}
