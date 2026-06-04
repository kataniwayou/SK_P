# Phase 31 close gate — v3.6.0 (triple-SHA)
# Idempotent execution — exactly-once-effect round-trip closeout
# ---------------------------------------------------------------------------
# Identical protocol to scripts/phase-29-close.ps1 (the proven v3.5.0 triple-SHA gate),
# extended for the new content-addressed L2 key namespaces this phase introduced:
#   - Phase 3 D-15 byte-identical psql \l SHA-256 invariant (database LIST — a seeded
#     Processor row is row-level, NOT db-level, so it does NOT move this SHA — RESEARCH A6).
#   - TEST-REDIS-04: docker exec sk-redis redis-cli --scan | sort | SHA-256, BEFORE = AFTER.
#     The scan is UNFILTERED (ALL keys), so the new Phase 31 key families are captured
#     automatically in the BEFORE/AFTER SHA — no prefix filter excludes them:
#       * the content-addressed execution-data keys skp:data:{64hex} — re-typed from the old
#         skp:data:{Guid:D} to a 64-hex content address (Phase 31 D-01). Minted per round-trip;
#         cleaned by the E2E net-zero teardown + the container's short ExecutionDataTtl, NOT this
#         script. A leaked skp:data:* surfaces here as a redis SHA mismatch (exit 1).
#       * the effect-first dedup flag keys skp:flag:{64hex} — NEW this phase (D-05). The symmetric
#         Pending->Ack dedup state both hops write. Bounded by a TTL and scan-cleaned by the E2E
#         net-zero teardown (D-12). A leaked skp:flag:* ALSO surfaces here as a redis SHA mismatch.
#       * the steady-state processor-liveness key skp:{procId:D} — written by the LIVE
#         processor-sample container; STEADY-STATE (in BOTH snapshots), so it does not break the
#         SHA as long as the procId is stable.
#   - TEST-RMQ-04/05 (D-09): docker exec sk-rabbitmq rabbitmqctl -q list_queues name | sort |
#     SHA-256, BEFORE = AFTER. The steady-state dispatch queue {procId:D} bound by the live
#     processor-sample container is steady-state (both snapshots) as long as procId is stable.
#   - Phase 3 D-18 3-consecutive-GREEN cadence (full suite, no Category filter — RealStack E2E run live).
#
# The net-zero discipline for the new skp:data:*/skp:flag:* namespaces is enforced by the E2E teardown
# (IdempotentExactlyOnceE2ETests registers BOTH namespaces into L2KeysToCleanup, D-12); this gate's job is
# to SNAPSHOT-and-COMPARE the full unfiltered keyspace. There is deliberately NO FLUSHDB and NO prefix
# filter on the scan — widening to the full keyspace is what makes skp:flag:* part of the SHA.
#
# Steady-state processor identity (Open Q1/Q2 resolution — DECISION: stable Processor row, NOT a
# pre-flight health exception). The gate REQUIRES processor-sample healthy at pre-flight. processor-sample
# only goes Healthy once a Processor DB row carrying its genuine embedded SourceHash exists (boot-before-
# register, unbounded retry). So this gate SEEDS that row idempotently FIRST (GET-or-create against the
# unique uq_processor_source_hash index — a fixed genuine hash means a stable procId across the whole 3-run
# gate, keeping its skp:{procId:D} liveness key + {procId:D} dispatch queue in both redis/rmq snapshots).
# This is the SAME idempotent row the E2E (IdempotentExactlyOnceE2ETests / SampleRoundTripE2ETests) reuses,
# so the id never churns. A health exception was REJECTED — it would let the gate pass with a dead
# processor (defeats the liveness point).
#
# CONTRACT-CHANGE NOTE (v3.6.0): the wire contract is NOT backward-compatible (EntryId Guid->string + new
# H). The live stack MUST run a REBUILT processor-sample (docker compose up -d --build processor-sample
# orchestrator baseapi-service) — a mixed-version deployment mis-deserializes the new contract.
#
# The full v3.6.0 stack MUST be up healthy (postgres, redis, rabbitmq, otel-collector,
# elasticsearch, prometheus, orchestrator, processor-sample, baseapi-service).
#
# Usage:
#   pwsh -File scripts/phase-31-close.ps1
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
    Write-Host "Phase 31 close gate (triple-SHA) starting from $repoRoot" -ForegroundColor Cyan

    # ---- Pre-flight seed: stable Processor row carrying the genuine embedded SourceHash ----
    # (Open Q2 resolution) processor-sample goes Healthy only once this row exists. Seed it idempotently
    # (GET-or-create on the unique source-hash) BEFORE the health loop so processor-sample can be healthy at
    # pre-flight, and so its procId — hence its skp:{procId:D} liveness key + {procId:D} queue — is stable
    # across the whole gate run (steady-state in both BEFORE and AFTER snapshots).
    Write-Host "Pre-flight: seeding the stable Processor row (genuine embedded SourceHash)..." -ForegroundColor Cyan

    $sampleDll = Join-Path $repoRoot 'src/Processor.Sample/bin/Release/net8.0/Processor.Sample.dll'
    if (-not (Test-Path $sampleDll)) {
        $sampleDll = Join-Path $repoRoot 'src/Processor.Sample/bin/Debug/net8.0/Processor.Sample.dll'
    }
    if (-not (Test-Path $sampleDll)) {
        Write-Host "  Processor.Sample.dll not found; building src/Processor.Sample (Release)..." -ForegroundColor Gray
        dotnet build (Join-Path $repoRoot 'src/Processor.Sample/Processor.Sample.csproj') -c Release | Out-Null
        $sampleDll = Join-Path $repoRoot 'src/Processor.Sample/bin/Release/net8.0/Processor.Sample.dll'
    }

    # Reflect the GENUINE embedded hash the SAME way the runtime reader does (D-08): read the
    # [assembly: AssemblyMetadata("SourceHash", "<64-hex>")] off the built concrete assembly. NOT recomputed,
    # NOT a synthetic/random hash. Load into a throwaway context so the file handle is released afterward.
    $asmBytes = [System.IO.File]::ReadAllBytes($sampleDll)
    $asm = [System.Reflection.Assembly]::Load($asmBytes)
    $sourceHash = ($asm.GetCustomAttributes([System.Reflection.AssemblyMetadataAttribute], $false) |
        Where-Object { $_.Key -eq 'SourceHash' } | Select-Object -First 1).Value
    if ([string]::IsNullOrWhiteSpace($sourceHash) -or ($sourceHash -notmatch '^[a-f0-9]{64}$')) {
        Write-Host "Could not read a valid 64-hex SourceHash off $sampleDll (got '$sourceHash'). Aborting." -ForegroundColor Red
        exit 2
    }
    Write-Host "  Genuine embedded SourceHash = $sourceHash" -ForegroundColor Gray

    # GET-or-create the Processor row via the WebApi CRUD over the host port (8080) — idempotent against the
    # unique uq_processor_source_hash index, so re-runs reuse the existing stable row (same procId the live
    # container already heartbeats against). Null schema Ids (D-05 — Processor.Sample runs schema-less).
    $baseApi = 'http://localhost:8080'
    $procId = $null
    try {
        $existing = Invoke-RestMethod -Method Get -Uri "$baseApi/api/v1/processors/by-source-hash/$sourceHash" `
            -TimeoutSec 15 -ErrorAction Stop
        $procId = $existing.id
        Write-Host "  Processor row already exists (idempotent) — reusing procId $procId" -ForegroundColor Gray
    } catch {
        $status = $null
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        if ($status -ne 404) {
            Write-Host "Unexpected error resolving the Processor row by source-hash (HTTP $status). Aborting." -ForegroundColor Red
            Write-Host $_.Exception.Message -ForegroundColor DarkGray
            exit 2
        }
        $body = @{
            name           = 'processor-sample'
            version        = '3.6.0'
            description    = 'Phase 31 close-gate steady-state Processor.Sample row (genuine embedded hash, schema-less).'
            sourceHash     = $sourceHash
            inputSchemaId  = $null
            outputSchemaId = $null
            configSchemaId = $null
        } | ConvertTo-Json
        $created = Invoke-RestMethod -Method Post -Uri "$baseApi/api/v1/processors" `
            -ContentType 'application/json' -Body $body -TimeoutSec 15 -ErrorAction Stop
        $procId = $created.id
        Write-Host "  Processor row created — procId $procId" -ForegroundColor Gray
    }

    # Wait for processor-sample to flip Healthy now that its row exists (identity resolved -> bind -> MarkHealthy
    # -> heartbeat). The unbounded boot-before-register retry means the container only goes Healthy after this seed.
    Write-Host "Pre-flight: waiting for processor-sample /health/ready (post-seed identity resolution)..." -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds(120)
    $procHealthy = $false
    while ((Get-Date) -lt $deadline) {
        $raw = docker compose ps processor-sample --format json 2>$null | Out-String
        $parsed = if ([string]::IsNullOrWhiteSpace($raw)) { $null } else { $raw | ConvertFrom-Json }
        $h = if ($null -eq $parsed) { 'not-running' }
             elseif ($parsed -is [array]) { $parsed[0].Health }
             else { $parsed.Health }
        if ($h -eq 'healthy') { $procHealthy = $true; break }
        Start-Sleep -Seconds 3
    }
    if (-not $procHealthy) {
        Write-Host "processor-sample never reported healthy within 120s after seeding the row. Aborting." -ForegroundColor Red
        Write-Host "  Check 'docker compose logs processor-sample' — identity may have failed to resolve" -ForegroundColor Red
        Write-Host "  (or the container predates the v3.6.0 wire contract — rebuild it: docker compose up -d --build processor-sample)." -ForegroundColor Red
        exit 2
    }
    Write-Host "  processor-sample is healthy (steady-state liveness key skp:$($procId.ToString().ToLower()) live)." -ForegroundColor Green

    # ---- Pre-flight: compose stack healthy (ONE canonical v3.6.0 list, processor-sample included) ----
    Write-Host "Pre-flight: compose stack health check..." -ForegroundColor Cyan
    $services = @('postgres', 'redis', 'rabbitmq', 'otel-collector', 'elasticsearch', 'prometheus', 'orchestrator', 'processor-sample', 'baseapi-service')
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
    Write-Host "  Observed Phase 31 fact count: $($distinctPassed[0])." -ForegroundColor Gray

    # ---- Settle-drain the transient TTL-bounded namespaces (NET-ZERO-31, Phase 31.1) ----
    # The Phase 31 skp:flag:{64hex} / skp:data:{64hex} keys are per-fire-UNIQUE (H embeds the per-fire
    # correlationId) and TTL-bounded (keepTtl, 300s). Every RealStack E2E test now STOPS its workflow in
    # teardown, so no self-rescheduled cron fire keeps minting fresh names after the runs finish. Wait for
    # the keyspace to drain back to the BEFORE set before snapshotting AFTER (timeout = TTL + slack). This
    # does NOT weaken leak detection: a key that never drains (a permanent-key regression, or a workflow
    # left firing) keeps the scan != BEFORE and still fails the redis invariant below.
    Write-Host "Settle: draining transient skp:flag:*/skp:data:* keys to the BEFORE baseline (<=330s)..." -ForegroundColor Cyan
    $settleDeadline = (Get-Date).AddSeconds(330)
    $settled = $false
    while ((Get-Date) -lt $settleDeadline) {
        $nowRedis = (docker exec sk-redis redis-cli --scan | Sort-Object -CaseSensitive | Out-String).Trim()
        if ($nowRedis -ceq $beforeRedis) { $settled = $true; break }
        Start-Sleep -Seconds 5
    }
    if ($settled) {
        Write-Host "  Settled: redis keyspace returned to the BEFORE set." -ForegroundColor Green
    } else {
        $flagN = (docker exec sk-redis redis-cli --scan --pattern 'skp:flag:*' | Measure-Object).Count
        $dataN = (docker exec sk-redis redis-cli --scan --pattern 'skp:data:*' | Measure-Object).Count
        Write-Host "  Settle timed out after 330s (skp:flag:*=$flagN, skp:data:*=$dataN remain) — the AFTER SHA will flag the residual leak." -ForegroundColor Yellow
    }

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
        Write-Host "  Investigate a leaked skp: key. Likely a leaked content-addressed execution-data key" -ForegroundColor Red
        Write-Host "  skp:data:{64hex} OR a leaked effect-first dedup flag skp:flag:{64hex} (the E2E net-zero" -ForegroundColor Red
        Write-Host "  teardown should drain BOTH namespaces — D-12), or a churned processor-liveness key" -ForegroundColor Red
        Write-Host "  skp:{procId} (the seeded row's id changed across the run):" -ForegroundColor Red
        Write-Host "    'docker exec sk-redis redis-cli --scan'" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "TEST-REDIS-04 invariant HELD: redis-cli --scan SHA-256 = $afterRedisHash" -ForegroundColor Green
    }

    if ($beforeRmqHash -ne $afterRmqHash) {
        Write-Host "TEST-RMQ-04/05 INVARIANT VIOLATED: rabbitmqctl list_queues SHA-256 BEFORE != AFTER" -ForegroundColor Red
        Write-Host "  BEFORE = $beforeRmqHash" -ForegroundColor Red
        Write-Host "  AFTER  = $afterRmqHash" -ForegroundColor Red
        Write-Host "  A churned dispatch queue {procId} (the seeded Processor row's id changed) is the likely" -ForegroundColor Red
        Write-Host "  cause — make the seed idempotent against the unique source-hash so the id stays stable." -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "TEST-RMQ-04/05 invariant HELD: rabbitmqctl list_queues SHA-256 = $afterRmqHash" -ForegroundColor Green
    }

    if (-not $allGood) {
        Write-Host "Phase 31 close gate FAILED. Resolve violations and re-run." -ForegroundColor Red
        exit 1
    }

    Write-Host "Phase 31 close gate PASSED." -ForegroundColor Green
    Write-Host "  Total facts GREEN: $($distinctPassed[0])" -ForegroundColor Green
    Write-Host "  psql \l SHA-256:              $afterPgHash" -ForegroundColor Green
    Write-Host "  redis-cli --scan SHA-256:     $afterRedisHash" -ForegroundColor Green
    Write-Host "  rabbitmqctl list_queues SHA-256: $afterRmqHash" -ForegroundColor Green
    Write-Host "Operator: append these three SHA values + the Passed count to .planning/STATE.md Phase 31 close entry." -ForegroundColor Cyan

    exit 0
} finally {
    Pop-Location
}
