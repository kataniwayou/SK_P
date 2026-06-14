<#
.SYNOPSIS
    Phase 65 clean-state reset (D-05, D-06, D-07 / ENV-02).

.DESCRIPTION
    Standalone per-run clean-state reset, run WITH THE STACK UP. The 67/68 fault-injection
    harness calls this (then the Plan 01 seeder) so each run's metrics/logs are attributable
    to that run only. Executes, in order:

      STEP 0  Pre-flight — assert the stack is up (postgres running); fail loud otherwise.
      STEP 1  `docker exec sk-redis redis-cli FLUSHALL` — wipe the dev Redis keyspace
              (skp:data:*, skp:msg:*, skp:proc:*, parent index, ...).
      STEP 2  HEAL-WAIT (D-07) — poll up to a bounded 60s deadline (6x the 10s heartbeat,
              fail-loud) until >=1 PER-INSTANCE liveness key `skp:proc:{procId:D}:{instanceId}`
              reappears (a live processor-sample replica re-wrote it). This is the exact L2
              state the orchestration-start ProcessorLivenessValidator reads — gating on the
              key (not container readiness) prevents a subsequent seed+Start 422 (Pitfall 1).
      STEP 3  FK-safe psql DELETE (Pitfall 3 — `docker compose exec` + `-d stepsdb`) of the 6
              workflow-graph tables in migration Down() order, in a single transaction.
              PRESERVES processors + config_schemas (schemas) — idempotent, NOT deleted (D-06).
      STEP 4  Processor-set assertion + orphan removal (Pitfall 4) — assert the running
              processor set == {processor-sample} (expect 2 replicas); remove a stray
              sk-processor-badconfig BY EXACT NAME only; NEVER `docker rm` the 2 unnamed
              processor-sample replicas.
      STEP 5  One-line success summary; exit 0.

    Stack stays UP — NO `docker compose down`, NO `-v`, NO DB/volume drop (D-06).

.NOTES
    Dev-only destructive tooling. Must never be pointed at a shared/prod DB or Redis.
    Container naming (compose.yaml): sk-redis is NAMED (docker exec); postgres + processor-sample
    are UNNAMED (docker compose exec / ps). sk-processor-badconfig is NAMED.
#>

$ErrorActionPreference = 'Stop'

function Write-Phase([string]$msg, [string]$color = 'Cyan') {
    Write-Host "[phase-65-reset] $msg" -ForegroundColor $color
}

# ---------------------------------------------------------------------------
# STEP 0 — pre-flight: assert the stack is up (the reset assumes a running stack, D-06).
# ---------------------------------------------------------------------------
Write-Phase 'STEP 0: pre-flight — asserting the stack is up (postgres running)...'
$pgInstances = @(docker compose ps postgres --format json 2>$null |
    Where-Object { $_ -match '\S' } |
    ForEach-Object { $_ | ConvertFrom-Json })
if ($pgInstances.Count -eq 0) {
    Write-Phase 'postgres is not running — the stack must be UP before reset. Aborting.' 'Red'
    Write-Phase 'Bring the stack up first: pwsh -File scripts/phase-65-up.ps1' 'Red'
    exit 2
}
Write-Phase "  stack is up (postgres has $($pgInstances.Count) running instance(s))." 'Gray'

# ---------------------------------------------------------------------------
# STEP 1 — Redis FLUSHALL (named container sk-redis).
# ---------------------------------------------------------------------------
Write-Phase 'STEP 1: redis-cli FLUSHALL (wiping the dev Redis keyspace)...'
docker exec sk-redis redis-cli FLUSHALL | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Phase "redis-cli FLUSHALL failed (exit $LASTEXITCODE). Aborting." 'Red'
    exit 2
}
Write-Phase '  FLUSHALL ok.' 'Gray'

# ---------------------------------------------------------------------------
# STEP 2 — HEAL-WAIT (D-07): bounded, fail-loud poll for a fresh per-instance liveness key.
#   per-INSTANCE key  skp:proc:{procId:D}:{instanceId}  (SECOND ':' segment after proc:)
#   index SET         skp:proc:{procId:D}               (ONE segment — excluded by the regex)
# Do NOT poll the deprecated flat skp:{procId} key (deleted Phase 61).
# ---------------------------------------------------------------------------
Write-Phase 'STEP 2: heal-wait — polling skp:proc:*:* for liveness reconvergence (bounded 60s)...'
$deadline = (Get-Date).AddSeconds(60)   # 6x the 10s heartbeat — generous, fail-loud
$healed = $false
while ((Get-Date) -lt $deadline) {
    # The regex `^skp:proc:[^:]+$` matches the bare index SET (one segment after proc:) and
    # EXCLUDES it; a key with a second ':' segment is the per-instance liveness key written by
    # a live replica.
    $keys = @(docker exec sk-redis redis-cli --scan --pattern 'skp:proc:*' |
              Where-Object { $_ -notmatch '^skp:proc:[^:]+$' })
    if ($keys.Count -ge 1) { $healed = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $healed) {
    Write-Phase 'Liveness did not reconverge in 60s after FLUSHALL — aborting.' 'Red'
    Write-Phase '  Check `docker compose logs processor-sample` — a 422 on a later seed/Start is the symptom of skipping this gate.' 'Red'
    exit 2
}
Write-Phase "  liveness reconverged ($($keys.Count) per-instance key(s) present)." 'Gray'

# ---------------------------------------------------------------------------
# STEP 3 — FK-safe psql DELETE (Pitfall 3: MUST be `docker compose exec` + `-d stepsdb`).
# Single transaction in migration Down() order (20260528074618_InitialCreate.cs:271-288):
#   step_next_steps -> workflow_assignments -> workflow_entry_steps -> assignments -> workflows -> steps
# PRESERVE processors + config_schemas (schemas) — NOT in the DELETE list (D-06).
# Statements are static literals (no interpolated input) — see threat T-65-06.
# ---------------------------------------------------------------------------
Write-Phase 'STEP 3: psql DELETE of the 6 workflow-graph tables (FK-safe, single transaction)...'
docker compose exec -T postgres psql -U postgres -d stepsdb -c "BEGIN;
DELETE FROM step_next_steps; DELETE FROM workflow_assignments; DELETE FROM workflow_entry_steps;
DELETE FROM assignments; DELETE FROM workflows; DELETE FROM steps;
COMMIT;" | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Phase "psql DELETE failed (exit $LASTEXITCODE). The transaction rolled back; no rows deleted. Aborting." 'Red'
    exit 2
}
Write-Phase '  graph rows deleted (processors + config_schemas preserved).' 'Gray'

# ---------------------------------------------------------------------------
# STEP 4 — processor-set assertion + orphan removal (Pitfall 4).
# NEVER `docker rm` the 2 unnamed processor-sample replicas. Remove only a stray
# sk-processor-badconfig BY EXACT NAME (a leftover from a prior `--profile badconfig` run).
# ---------------------------------------------------------------------------
Write-Phase 'STEP 4: asserting the running processor set == {processor-sample}...'
$sample = @(docker compose ps processor-sample --format json 2>$null |
    Where-Object { $_ -match '\S' } |
    ForEach-Object { $_ | ConvertFrom-Json })
if ($sample.Count -eq 0) {
    Write-Phase 'processor-sample has 0 running replicas — the processor set is empty. Aborting.' 'Red'
    Write-Phase '  A later seed/Start would 422 on liveness. Bring the stack up: pwsh -File scripts/phase-65-up.ps1' 'Red'
    exit 2
}
Write-Phase "  processor-sample replicas present: $($sample.Count) (expected 2)." 'Gray'

# Remove only a stray badconfig container, targeted by its EXACT compose name (compose.yaml:304).
$bad = @(docker ps --filter 'name=sk-processor-badconfig' --format '{{.Names}}' | Where-Object { $_ -match '\S' })
if ($bad.Count -gt 0) {
    Write-Phase "  Removing stray badconfig container(s): $($bad -join ',')" 'Yellow'
    docker rm -f sk-processor-badconfig | Out-Null
} else {
    Write-Phase '  no stray sk-processor-badconfig container running.' 'Gray'
}

# ---------------------------------------------------------------------------
# STEP 5 — success.
# ---------------------------------------------------------------------------
Write-Phase '[phase-65-reset] FLUSHALL ok; liveness reconverged; graph rows deleted (processors+schemas preserved); processor set == {processor-sample}' 'Green'
exit 0
