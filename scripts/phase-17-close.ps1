# Phase 17 close gate — v3.4.0
# Messaging.Contracts + Shared L2 Root Extract (behavior-preserving using-swap)
# ---------------------------------------------------------------------------
# Honors:
#   - Phase 3 D-15 byte-identical psql \l SHA-256 invariant
#   - TEST-REDIS-04: docker exec sk-redis redis-cli --scan piped through a
#     locale-stable sort then SHA-256, BEFORE = AFTER across the full suite
#   - Phase 3 D-18 3-consecutive-GREEN cadence (baseline 235 facts, v3.3.0)
#
# This is the DUAL-snapshot gate (psql \l + redis-cli --scan). The triple-SHA
# rabbitmqctl arm is NOT applicable this phase (no broker wired — RESEARCH line 492).
# The Phase-16-specific EF-migration + HEALTH-immutable assertion arms are stripped:
# Phase 17 is a compile-time using-swap with no schema and no Health change.
#
# Usage:
#   pwsh -File scripts/phase-17-close.ps1
#
# Exit codes:
#   0  — both build configs zero-warning, all three runs GREEN, both SHA-256 invariants held
#   1  — invariant violation OR any build/test run RED
#   2  — environment misconfigured (compose stack not healthy)
# ---------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    Write-Host "Phase 17 close gate starting from $repoRoot" -ForegroundColor Cyan

    # ---- Pre-flight: compose stack healthy ----
    Write-Host "Pre-flight: compose stack health check..." -ForegroundColor Cyan
    $services = @('postgres', 'redis', 'otel-collector', 'elasticsearch', 'prometheus')
    foreach ($svc in $services) {
        $health = (docker compose ps $svc --format json | ConvertFrom-Json).Health
        if ($health -ne 'healthy' -and $svc -ne 'otel-collector') {
            Write-Host "Service '$svc' is not healthy (Health=$health). Aborting." -ForegroundColor Red
            exit 2
        }
    }

    # ---- BEFORE snapshots ----
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

    # ---- Zero-warning build gate, BOTH configs ----
    # dotnet clean first to avoid stale-assembly false-greens (the old BaseApi.Service
    # assembly no longer carries the moved types — RESEARCH Runtime State Inventory).
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

    # ---- Wire-shape guard first (fast feedback on SC#3 / MSG-CONTRACTS-04) ----
    Write-Host "Wire-shape guard: ProjectionRecordRoundTripTests..." -ForegroundColor Cyan
    dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build `
        --filter-class *ProjectionRecordRoundTripTests 2>&1 | Out-String | Write-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Wire-shape guard FAILED — byte-identical proof broken." -ForegroundColor Red
        exit 1
    }
    Write-Host "  Wire-shape guard GREEN." -ForegroundColor Green

    # ---- 3-GREEN cadence (Phase 3 D-18) ----
    $runResults = @()
    for ($i = 1; $i -le 3; $i++) {
        Write-Host "Run $i of 3: dotnet test..." -ForegroundColor Cyan
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $output = dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build 2>&1 | Out-String
        $sw.Stop()
        $exit = $LASTEXITCODE

        $passedMatch = [Regex]::Match($output, 'Passed:\s+(\d+)')
        $passed = if ($passedMatch.Success) { [int]$passedMatch.Groups[1].Value } else { -1 }

        $runResults += [PSCustomObject]@{
            Run = $i
            Exit = $exit
            Passed = $passed
            Duration = $sw.Elapsed
        }
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
    if ($distinctPassed[0] -ne 235) {
        Write-Host "WARNING: fact count $($distinctPassed[0]) != v3.3.0 baseline 235 — a fact was dropped or added by the swap." -ForegroundColor Yellow
    }

    # ---- AFTER snapshots ----
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

    # ---- Invariant assertions ----
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

    if (-not $allGood) {
        Write-Host "Phase 17 close gate FAILED. Resolve violations and re-run." -ForegroundColor Red
        exit 1
    }

    Write-Host "Phase 17 close gate PASSED." -ForegroundColor Green
    Write-Host "  Total facts GREEN: $($distinctPassed[0])" -ForegroundColor Green
    Write-Host "  psql \l SHA-256:    $afterPgHash" -ForegroundColor Green
    Write-Host "  redis-cli --scan SHA-256: $afterRedisHash" -ForegroundColor Green
    Write-Host "Operator: append these values to .planning/STATE.md Phase 17 P02 close entry." -ForegroundColor Cyan

    exit 0
} finally {
    Pop-Location
}
