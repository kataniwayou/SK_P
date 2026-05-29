# Phase 12 close gate — v3.3.0
# ---------------------------------------------------------------------------
# Honors:
#   - Phase 3 D-15 byte-identical psql \l SHA-256 invariant (baseline
#     0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127;
#     5th consecutive phase to record)
#   - TEST-REDIS-04: docker exec sk-redis redis-cli --scan piped through
#     LC_ALL=C sort then SHA-256, BEFORE = AFTER across the full suite
#   - Phase 3 D-18 3-consecutive-GREEN cadence (honored 11x through Phase 11)
#
# Usage:
#   pwsh -File scripts/phase-12-close.ps1
#
# Exit codes:
#   0  — all three runs GREEN, both SHA-256 invariants held
#   1  — invariant violation OR any test run RED
#   2  — environment misconfigured (compose stack not healthy)
# ---------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    Write-Host "Phase 12 close gate starting from $repoRoot" -ForegroundColor Cyan

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
    $beforePg = (docker exec sk-postgres psql -U postgres -lqt | Out-String).Trim()
    $beforePgHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($beforePg))
    )).Hash.ToLower()
    Write-Host "  psql \l SHA-256 BEFORE = $beforePgHash" -ForegroundColor Gray

    # (b) TEST-REDIS-04 redis-cli --scan invariant — locale-locked per Pitfall 6.
    # `sort` inside docker exec uses the container's locale (default C in alpine);
    # we re-sort on the PowerShell side using locale-stable [string]::CompareOrdinal
    # via Sort-Object -CaseSensitive to defend against any host-side locale variance.
    $beforeRedis = (docker exec sk-redis redis-cli --scan | Sort-Object -CaseSensitive | Out-String).Trim()
    $beforeRedisHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($beforeRedis))
    )).Hash.ToLower()
    Write-Host "  redis-cli --scan SHA-256 BEFORE = $beforeRedisHash" -ForegroundColor Gray

    # ---- 3-GREEN cadence (Phase 3 D-18) ----
    $runResults = @()
    for ($i = 1; $i -le 3; $i++) {
        Write-Host "Run $i of 3: dotnet test..." -ForegroundColor Cyan
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $output = dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build 2>&1 | Out-String
        $sw.Stop()
        $exit = $LASTEXITCODE

        # Extract fact count from output (e.g., "Passed: 177").
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
            Write-Host "Per Phase 8 Plan 08-08 precedent (consecutive is the gate, not first-attempt-3):" -ForegroundColor Yellow
            Write-Host "  If failures are known-flake observability (LogLevelFilterTests OTel cold-start) or" -ForegroundColor Yellow
            Write-Host "  ConcurrencyTokenTests racing-writes, the operator may retry. Otherwise, abort." -ForegroundColor Yellow
            Write-Host $output -ForegroundColor DarkGray
            exit 1
        }
    }

    # All three runs must have the same Passed count (deterministic invariant)
    $distinctPassed = $runResults | Select-Object -ExpandProperty Passed -Unique
    if ($distinctPassed.Count -ne 1) {
        Write-Host "3-GREEN cadence violation — fact counts diverge: $($runResults.Passed -join ', ')" -ForegroundColor Red
        exit 1
    }
    Write-Host "3-GREEN cadence passed — $($distinctPassed[0]) facts GREEN across 3 runs." -ForegroundColor Green

    # ---- AFTER snapshots ----
    Write-Host "Capturing AFTER snapshots..." -ForegroundColor Cyan
    $afterPg = (docker exec sk-postgres psql -U postgres -lqt | Out-String).Trim()
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
        Write-Host "  Investigate: residual test:cls-* keys via 'docker exec sk-redis redis-cli --scan | findstr test:cls-'" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "TEST-REDIS-04 invariant HELD: redis-cli --scan SHA-256 = $afterRedisHash" -ForegroundColor Green
    }

    # ---- Negative schema-migration assertion (no new EF migrations from Phase 12) ----
    $newMigrations = (git status --porcelain src/BaseApi.Service/Migrations/ 2>$null | Out-String).Trim()
    if (-not [string]::IsNullOrWhiteSpace($newMigrations)) {
        Write-Host "Unexpected EF migration changes under src/BaseApi.Service/Migrations/:" -ForegroundColor Red
        Write-Host $newMigrations -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "No EF migrations generated by Phase 12 (negative schema-push assertion HELD)." -ForegroundColor Green
    }

    # ---- HEALTH-01..05 byte-immutable invariant ----
    $healthDiff = (git diff src/BaseApi.Core/Health/StartupCompletionService.cs src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs | Out-String).Trim()
    if (-not [string]::IsNullOrWhiteSpace($healthDiff)) {
        Write-Host "HEALTH-01..05 byte-immutable invariant VIOLATED — D-05/D-06 regression:" -ForegroundColor Red
        Write-Host $healthDiff -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "HEALTH-01..05 byte-immutable invariant HELD (D-05/D-06)." -ForegroundColor Green
    }

    if (-not $allGood) {
        Write-Host "Phase 12 close gate FAILED. Resolve violations and re-run." -ForegroundColor Red
        exit 1
    }

    Write-Host "Phase 12 close gate PASSED." -ForegroundColor Green
    Write-Host "  Total facts GREEN: $($distinctPassed[0])" -ForegroundColor Green
    Write-Host "  psql \l SHA-256:    $afterPgHash" -ForegroundColor Green
    Write-Host "  redis-cli --scan SHA-256: $afterRedisHash" -ForegroundColor Green
    Write-Host "Operator: append these values to .planning/STATE.md Phase 12 P08 close entry." -ForegroundColor Cyan

    exit 0
} finally {
    Pop-Location
}
