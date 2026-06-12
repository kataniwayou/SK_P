# Phase 55 close gate — v5.0.0 (triple-SHA)
# Recovery Re-architecture (messageId slot-array + 3-state keeper) — live-proof + net-zero closeout
# ---------------------------------------------------------------------------
# This v5.0.0 gate is a verbatim clone of scripts/phase-49-close.ps1 (the proven triple-SHA protocol),
# carrying exactly three v5 deltas: the retired Model-B composite settle-GC block is removed (D-06a), an
# additive skp:msg:* count==0 assertion is added (D-06c), and it is retitled to Phase 55. The triple-SHA
# protocol (idempotent steady-state Processor-row seed, compose-health pre-flight, BOTH-config 0-warning
# build gate, 3-consecutive-GREEN identical-fact-count cadence, triple-SHA BEFORE==AFTER, separate DLQ
# depth==0) is IDENTICAL to phase-49.
#
#   - Phase 3 D-15 byte-identical psql \l SHA-256 invariant (database LIST — a seeded Processor row is
#     row-level, NOT db-level, so it does NOT move this SHA).
#   - redis: docker exec sk-redis redis-cli --scan | sort | SHA-256, BEFORE == AFTER.
#     The scan is UNFILTERED (ALL keys), so the v5 key families are captured automatically in the
#     BEFORE/AFTER SHA — NO new prefix filter excludes them (D-06b):
#       * the GUID execution-data keys skp:data:{guid} — minted per round-trip; cleaned by the
#         E2E net-zero teardown (L2KeysToCleanup) and by the A19 active two-key DELETE. A leaked
#         skp:data:* surfaces here as a redis SHA mismatch (exit 1).
#       * the messageId slot-array index keys skp:msg:{messageId} (the processor-owned allocation index,
#         SlotArrayOptions 300/600s TTL). NET-ZERO IS PROVEN BY THE A19 ACTIVE TWO-KEY DELETE, NOT BY
#         WAITING OUT THE TTL: the 300/600s TTL CANNOT be waited out. The processor actively reclaims the
#         index at end-of-message (ProcessorPipeline.DeleteTerminalAsync + DeleteConsumer two-key DEL), so
#         the index is gone BEFORE the AFTER snapshot — a lingering index surfaces here as a redis SHA
#         mismatch (exit 1) AND as the additive skp:msg:* count>0 assertion below, NEVER a silent TTL pass.
#       * the steady-state processor-liveness key skp:{procId:D} — written by the LIVE processor-sample
#         container on a sliding-TTL heartbeat. It is EXCLUDED from the redis name-SHA (a single
#         Where-Object filter on the seeded procId): its presence flaps with the heartbeat-vs-TTL race
#         and is briefly absent during the SC3 docker stop sk-redis outage, which would otherwise churn
#         the SHA. It is steady-state by intent (the live container keeps re-writing it) — excluding it
#         removes the timing fragility WITHOUT weakening leak detection for skp:data:* / skp:msg:*.
#   - rabbitmq: docker exec sk-rabbitmq rabbitmqctl -q list_queues name | sort | SHA-256, BEFORE == AFTER.
#     The steady-state dispatch queue {procId:D} bound by the live processor-sample container is
#     steady-state (both snapshots) as long as procId is stable.
#     The transient MassTransit *_bus_* temporary endpoint queues are EXCLUDED (Where-Object -notmatch
#     '_bus_'): they are auto-delete queues with a random per-connection NewId suffix that is RE-MINTED
#     when a container's bus reconnects after the SC3 redis outage — random transient names are not
#     net-zero topology, so folding them into the SHA would churn it on any bus reconnect.
#     NOTE (Pitfall 4): the name-SHA input stays `list_queues name` (NOT `name messages`) — folding the
#     message-depth column into the SHA would churn it every run. The skp-dlq-1 depth==0 check below is a
#     SEPARATE additive assertion that uses `list_queues name messages`.
#   - 3-consecutive-GREEN cadence (full suite, no Category filter — RealStack E2E run live), with an
#     identical fact count across all 3 runs as a Smell-A guard.
#
# The net-zero discipline for the skp:data:* + skp:msg:* slot-array namespaces is enforced by the A19
# active two-key DELETE in production (ProcessorPipeline.DeleteTerminalAsync + DeleteConsumer.cs both-key)
# plus the E2E teardown (the E2E tests register every namespace into L2KeysToCleanup); this gate's job is
# to SNAPSHOT-and-COMPARE the full unfiltered keyspace. There is deliberately NO destructive whole-db flush
# and NO prefix filter on the scan — widening to the full keyspace is what makes the slot-array index part
# of the SHA. Likewise the skp-dlq-1 depth==0 check is a snapshot-and-compare assertion: the net-zero DLQ
# drain stays in the E2E teardown, NOT this gate (NO gate-side purge_queue) — so a teardown regression
# surfaces here as depth>0, not a silent pass.
#
# v5 SINGLE DLQ: the sole surviving dead-letter queue is skp-dlq-1 (ConsolidatedErrorTransportFilter.Dlq1
# — the consolidated transport-exhaustion forensic sink). The DLQ loop below is the SINGLE-element
# @('skp-dlq-1').
#
# Steady-state processor identity (DECISION: stable Processor row, NOT a pre-flight health exception). The
# gate REQUIRES processor-sample healthy at pre-flight. processor-sample only goes Healthy once a Processor
# DB row carrying its genuine embedded SourceHash exists (boot-before-register, unbounded retry). So this
# gate SEEDS that row idempotently FIRST (GET-or-create against the unique uq_processor_source_hash index —
# a fixed genuine hash means a stable procId across the whole 3-run gate, keeping its skp:{procId:D}
# liveness key + {procId:D} dispatch queue in both redis/rmq snapshots).
#
# CONTRACT-CHANGE NOTE (v5.0.0): the wire contract is a fresh BREAKING change (messageId slot-array +
# 3-state keeper + A19 active reclaim). The live stack MUST run the REBUILT contract-changed images
# (docker compose up -d --build baseapi-service orchestrator processor-sample keeper) — a mixed-version
# deployment mis-deserializes the new contract, and the embedded SourceHash must match the host build or
# the liveness gate false-passes/times out. The rebuild set is therefore
# `baseapi-service orchestrator processor-sample keeper`.
#
# The full v5.0.0 stack MUST be up healthy (postgres, redis, rabbitmq, otel-collector, elasticsearch,
# prometheus, orchestrator, processor-sample, baseapi-service, keeper).
#
# Usage:
#   pwsh -File scripts/phase-55-close.ps1
#
# Exit codes:
#   0  — both build configs zero-warning, all three runs GREEN, all three SHA-256 invariants held,
#        the single DLQ (skp-dlq-1) depth==0, and the skp:msg:* slot-array index count==0
#   1  — invariant violation OR any build/test run RED OR unparseable fact count (Smell A)
#   2  — environment misconfigured (compose stack not healthy)
# ---------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    Write-Host "Phase 55 close gate (triple-SHA) starting from $repoRoot" -ForegroundColor Cyan

    # ---- Pre-flight seed: stable Processor row carrying the genuine embedded SourceHash ----
    # processor-sample goes Healthy only once this row exists. Seed it idempotently (GET-or-create on the
    # unique source-hash) BEFORE the health loop so processor-sample can be healthy at pre-flight, and so
    # its procId — hence its skp:{procId:D} liveness key + {procId:D} queue — is stable across the whole
    # gate run (steady-state in both BEFORE and AFTER snapshots).
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

    # Reflect the GENUINE embedded hash the SAME way the runtime reader does: read the
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
    # container already heartbeats against). Null schema Ids (Processor.Sample runs schema-less).
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
        # Seed version: the verified live Processor.Sample value (src/Processor.Sample/appsettings.json:11
        # => "Version": "3.5.0"). The row is keyed by the unique uq_processor_source_hash index — the
        # SourceHash is what changed v4->v5, NOT this version string, so the seed version stays '3.5.0'.
        $body = @{
            name           = 'processor-sample'
            version        = '3.5.0'
            description    = 'Phase 55 close-gate steady-state Processor.Sample row (genuine embedded hash, schema-less).'
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
        Write-Host "  (or the container predates the v5.0.0 wire contract — rebuild it: docker compose up -d --build processor-sample)." -ForegroundColor Red
        exit 2
    }
    Write-Host "  processor-sample is healthy (steady-state liveness key skp:$($procId.ToString().ToLower()) live)." -ForegroundColor Green

    # ---- Pre-flight: compose stack healthy (ONE canonical v5.0.0 list, processor-sample + keeper included) ----
    Write-Host "Pre-flight: compose stack health check..." -ForegroundColor Cyan
    # v5 canonical service list (verified against compose.yaml — keeper has deploy.replicas: 2).
    $services = @('postgres', 'redis', 'rabbitmq', 'otel-collector', 'elasticsearch', 'prometheus', 'orchestrator', 'processor-sample', 'baseapi-service', 'keeper')
    foreach ($svc in $services) {
        # `docker compose ps --format json` emits NDJSON (one object per line). A multi-replica
        # service (keeper has replicas: 2) yields several lines, so the concatenated string cannot
        # be fed to a single ConvertFrom-Json — parse line-by-line and require ALL instances healthy.
        $instances = @(docker compose ps $svc --format json 2>$null |
            Where-Object { $_ -match '\S' } |
            ForEach-Object { $_ | ConvertFrom-Json })
        $health = if ($instances.Count -eq 0) { 'not-running' }
                  else {
                      $unhealthy = @($instances | Where-Object { $_.Health -ne 'healthy' })
                      if ($unhealthy.Count -gt 0) { "$($unhealthy[0].Health)" } else { 'healthy' }
                  }
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

    $beforeRedis = (docker exec sk-redis redis-cli --scan | Where-Object { $_ -ne "skp:$($procId.ToString().ToLower())" } | Sort-Object -CaseSensitive | Out-String).Trim()
    $beforeRedisHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($beforeRedis))
    )).Hash.ToLower()
    Write-Host "  redis-cli --scan SHA-256 BEFORE = $beforeRedisHash" -ForegroundColor Gray

    $beforeRmq = (docker exec sk-rabbitmq rabbitmqctl -q list_queues name | Where-Object { $_ -notmatch '_bus_' } | Sort-Object -CaseSensitive | Out-String).Trim()
    $beforeRmqHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($beforeRmq))
    )).Hash.ToLower()
    Write-Host "  rabbitmqctl list_queues SHA-256 BEFORE = $beforeRmqHash" -ForegroundColor Gray

    # ---- Zero-warning build gate, BOTH configs ----
    # Both-config 0-warning gate (TreatWarningsAsErrors fatal): dotnet build SK_P.sln -c Release AND -c Debug.
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

    # ---- 3-GREEN cadence — FULL suite, RealStack E2E run live ----
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
    Write-Host "  Observed Phase 55 fact count: $($distinctPassed[0])." -ForegroundColor Gray

    # ---- AFTER snapshots (triple) ----
    # NOTE (D-06a): there is deliberately NO composite settle-GC loop here. Model-B (the composite backup
    # key corr:wf:proc:exec) was retired in Phases 50/53. Net-zero of the v5 slot-array index skp:msg:* is
    # proven by the A19 active two-key DELETE (ProcessorPipeline.DeleteTerminalAsync + DeleteConsumer), NOT
    # by waiting out a TTL (the 300/600s SlotArrayOptions TTL cannot be waited out — Pitfall 2). A lingering
    # index surfaces as a redis SHA mismatch AND as the additive skp:msg:* count>0 assertion below.
    Write-Host "Capturing AFTER snapshots..." -ForegroundColor Cyan
    $afterPg = (docker compose exec -T postgres psql -U postgres -lqt | Out-String).Trim()
    $afterPgHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($afterPg))
    )).Hash.ToLower()
    Write-Host "  psql \l SHA-256 AFTER  = $afterPgHash" -ForegroundColor Gray

    $afterRedis = (docker exec sk-redis redis-cli --scan | Where-Object { $_ -ne "skp:$($procId.ToString().ToLower())" } | Sort-Object -CaseSensitive | Out-String).Trim()
    $afterRedisHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($afterRedis))
    )).Hash.ToLower()
    Write-Host "  redis-cli --scan SHA-256 AFTER  = $afterRedisHash" -ForegroundColor Gray

    $afterRmq = (docker exec sk-rabbitmq rabbitmqctl -q list_queues name | Where-Object { $_ -notmatch '_bus_' } | Sort-Object -CaseSensitive | Out-String).Trim()
    $afterRmqHash = (Get-FileHash -Algorithm SHA256 -InputStream (
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($afterRmq))
    )).Hash.ToLower()
    Write-Host "  rabbitmqctl list_queues SHA-256 AFTER  = $afterRmqHash" -ForegroundColor Gray

    # ---- Invariant assertions (triple) ----
    $allGood = $true

    if ($beforePgHash -ne $afterPgHash) {
        Write-Host "psql \l SHA-256 INVARIANT VIOLATED: BEFORE != AFTER" -ForegroundColor Red
        Write-Host "  BEFORE = $beforePgHash" -ForegroundColor Red
        Write-Host "  AFTER  = $afterPgHash" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "psql \l SHA-256 invariant HELD: $afterPgHash" -ForegroundColor Green
    }

    if ($beforeRedisHash -ne $afterRedisHash) {
        Write-Host "redis-cli --scan SHA-256 INVARIANT VIOLATED: BEFORE != AFTER" -ForegroundColor Red
        Write-Host "  BEFORE = $beforeRedisHash" -ForegroundColor Red
        Write-Host "  AFTER  = $afterRedisHash" -ForegroundColor Red
        Write-Host "  Investigate a leaked skp: key. Likely a leaked GUID execution-data key skp:data:{guid}" -ForegroundColor Red
        Write-Host "  OR a leaked slot-array index skp:msg:{messageId} (the A19 active two-key DELETE in" -ForegroundColor Red
        Write-Host "  ProcessorPipeline.DeleteTerminalAsync + DeleteConsumer should reclaim every index" -ForegroundColor Red
        Write-Host "  — the 300/600s SlotArrayOptions TTL is NOT waited out), or a churned processor-liveness" -ForegroundColor Red
        Write-Host "  key skp:{procId} (the seeded row's id changed across the run):" -ForegroundColor Red
        Write-Host "    'docker exec sk-redis redis-cli --scan'" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "redis-cli --scan SHA-256 invariant HELD: $afterRedisHash" -ForegroundColor Green
    }

    if ($beforeRmqHash -ne $afterRmqHash) {
        Write-Host "rabbitmqctl list_queues SHA-256 INVARIANT VIOLATED: BEFORE != AFTER" -ForegroundColor Red
        Write-Host "  BEFORE = $beforeRmqHash" -ForegroundColor Red
        Write-Host "  AFTER  = $afterRmqHash" -ForegroundColor Red
        Write-Host "  A churned dispatch queue {procId} (the seeded Processor row's id changed) is the likely" -ForegroundColor Red
        Write-Host "  cause — make the seed idempotent against the unique source-hash so the id stays stable." -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "rabbitmqctl list_queues SHA-256 invariant HELD: $afterRmqHash" -ForegroundColor Green
    }

    # ---- Single-DLQ depth==0 assertion (additive, SEPARATE from the name-SHA per Pitfall 4) ----
    # Net-zero closeout for the SOLE surviving dead-letter queue: skp-dlq-1 (DLQ-1,
    # ConsolidatedErrorTransportFilter.Dlq1 — the consolidated transport-exhaustion forensic sink). This uses
    # `list_queues name messages` to read the depth column, kept OUT of the name-SHA above (the SHA input
    # stays `list_queues name`) so depth churn cannot break the name invariant. The net-zero drain stays in
    # the E2E teardown — NO gate-side purge_queue — so a teardown regression surfaces here as depth>0.
    $depthRaw = docker exec sk-rabbitmq rabbitmqctl -q list_queues name messages | Out-String
    foreach ($q in @('skp-dlq-1')) {                          # ConsolidatedErrorTransportFilter.Dlq1 — sole surviving DLQ (Phase 53)
        $line  = ($depthRaw -split "`n" | Where-Object { $_ -match "^\s*$([regex]::Escape($q))\s+\d+\s*$" })
        $depth = if ($line -match "\s+(\d+)\s*$") { [int]$Matches[1] } else { -1 }
        if ($depth -ne 0) {
            Write-Host "DLQ depth invariant VIOLATED: $q depth=$depth (expected 0)" -ForegroundColor Red
            $allGood = $false
        } else {
            Write-Host "DLQ depth invariant HELD: $q depth=0" -ForegroundColor Green
        }
    }

    # ---- D-06c: additive A19 active-reclaim assertion — parallel to the skp-dlq-1 depth==0 check above. ----
    # Net-zero of the slot-array index is a PRODUCTION property (the two-key DEL in ProcessorPipeline.DeleteTerminalAsync
    # + DeleteConsumer.cs both-key), NOT a TTL settle (the 300/600s SlotArrayOptions TTL cannot be waited out).
    # A lingering index surfaces here as count>0 AND as a redis SHA mismatch — never a silent TTL pass.
    $msgCount = (docker exec sk-redis redis-cli --scan --pattern 'skp:msg:*' | Measure-Object).Count
    if ($msgCount -ne 0) {
        Write-Host "skp:msg:* count invariant VIOLATED: $msgCount (expected 0 — A19 active reclaim leaked an index)" -ForegroundColor Red
        $allGood = $false
    } else {
        Write-Host "skp:msg:* count invariant HELD: 0 (A19 active two-key DEL reclaimed every index)" -ForegroundColor Green
    }

    if (-not $allGood) {
        Write-Host "Phase 55 close gate FAILED. Resolve violations and re-run." -ForegroundColor Red
        exit 1
    }

    Write-Host "Phase 55 close gate PASSED." -ForegroundColor Green
    Write-Host "  Total facts GREEN: $($distinctPassed[0])" -ForegroundColor Green
    Write-Host "  psql \l SHA-256:              $afterPgHash" -ForegroundColor Green
    Write-Host "  redis-cli --scan SHA-256:     $afterRedisHash" -ForegroundColor Green
    Write-Host "  rabbitmqctl list_queues SHA-256: $afterRmqHash" -ForegroundColor Green
    Write-Host "  DLQ depth: skp-dlq-1=0" -ForegroundColor Green
    Write-Host "  skp:msg:* slot-array index count: 0" -ForegroundColor Green
    Write-Host "Operator: append these three SHA values + the Passed count + the skp-dlq-1 depth + the skp:msg:* count to .planning/phases/55-live-proof-close-gate/55-HUMAN-UAT.md Phase 55 GREEN-run record." -ForegroundColor Cyan

    exit 0
} finally {
    Pop-Location
}
