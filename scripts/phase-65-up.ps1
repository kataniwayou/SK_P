# Phase 65 minimal-stack bring-up — v8.0.0 (E2E Resilience Proof)
# fan-out-workflow-seeder-clean-state-stack — D-09 (ENV-01)
# ---------------------------------------------------------------------------
# Standalone bring-up the 67/68 fault-injection harness calls to start the minimal proof stack.
#
#   1. `docker compose up -d` against the DEFAULT profile. processor-badconfig is already gated
#      behind `profiles: ["badconfig"]` (compose.yaml:300), so the default up EXCLUDES it with NO
#      compose edit and NO `--profile badconfig`.
#   2. Wait until all 10 service TYPES report healthy/ready (NDJSON-per-replica parse reused verbatim
#      from scripts/phase-62-close.ps1:291-309). keeper and processor-sample carry deploy.replicas: 2
#      (one TYPE, two replicas — ALL instances must be healthy). otel-collector has NO in-container
#      healthcheck (compose.yaml:69-79) — its 'running' state is treated as ready (do NOT block on .Health).
#   3. Assert ZERO sk-processor-badconfig containers (ENV-01).
#
# processor-sample keeps deploy.replicas: 2 — attribution comes from correlationId, not single-instance.
# Non-destructive: NO `down`, NO `-v`, NO DB/keyspace mutation. compose.yaml is never edited.
# ---------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'

# ---- Bring up the default profile (badconfig excluded by its profile gate; NO compose edit) ----
Write-Host "[phase-65-up] docker compose up -d (default profile — processor-badconfig excluded by profiles:[badconfig] gate)..." -ForegroundColor Cyan
docker compose up -d | Out-Null

# ---- Canonical service list (D-09) — the 10 service TYPES to wait on ----
$services = @('postgres', 'redis', 'rabbitmq', 'otel-collector', 'elasticsearch', 'prometheus',
              'orchestrator', 'keeper', 'baseapi-service', 'processor-sample')

# ---- Health-wait loop (reuse phase-62-close.ps1:291-309 NDJSON-per-replica parse) ----
# Bounded poll: 180s overall deadline, ~2s interval — generous for elasticsearch / start_period cold starts.
$deadlineSeconds = 180
$pollInterval = 2
$deadline = (Get-Date).AddSeconds($deadlineSeconds)

Write-Host "[phase-65-up] Waiting up to ${deadlineSeconds}s for $($services.Count) service types healthy/ready..." -ForegroundColor Cyan

foreach ($svc in $services) {
    $lastHealth = 'unknown'
    $ready = $false
    do {
        # `docker compose ps <svc> --format json` emits NDJSON (one object per replica). A multi-replica
        # service (keeper replicas:2, processor-sample replicas:2) yields several lines, so the concatenated
        # string cannot be fed to a single ConvertFrom-Json — parse line-by-line and require ALL instances healthy.
        $instances = @(docker compose ps $svc --format json 2>$null |
            Where-Object { $_ -match '\S' } |
            ForEach-Object { $_ | ConvertFrom-Json })
        $health = if ($instances.Count -eq 0) { 'not-running' }
                  else {
                      $unhealthy = @($instances | Where-Object { $_.Health -ne 'healthy' })
                      if ($unhealthy.Count -gt 0) { "$($unhealthy[0].Health)" } else { 'healthy' }
                  }
        $lastHealth = $health

        if ($health -eq 'healthy') {
            $ready = $true
        }
        elseif ($svc -eq 'otel-collector') {
            # otel-collector has NO in-container healthcheck (compose.yaml:69-79) — treat 'running' as ready.
            $running = @($instances | Where-Object { $_.State -eq 'running' })
            if ($instances.Count -gt 0 -and $running.Count -eq $instances.Count) { $ready = $true }
        }

        if (-not $ready) {
            if ((Get-Date) -ge $deadline) {
                Write-Host "[phase-65-up] Service '$svc' did not converge within ${deadlineSeconds}s (last Health=$lastHealth). Aborting." -ForegroundColor Red
                exit 2
            }
            Start-Sleep -Seconds $pollInterval
        }
    } while (-not $ready)

    Write-Host "  [phase-65-up] $svc ready ($($instances.Count) instance(s))." -ForegroundColor Green
}

# ---- Zero-badconfig assertion (ENV-01) ----
$bad = @(docker ps --filter 'name=sk-processor-badconfig' --format '{{.Names}}')
if ($bad.Count -ne 0) {
    Write-Host "[phase-65-up] badconfig container running ($($bad -join ', ')) — ENV-01 violated." -ForegroundColor Red
    exit 2
}

# ---- Success ----
Write-Host "[phase-65-up] 10 service types healthy; 0 badconfig; processor-sample replicas:2" -ForegroundColor Green
exit 0
