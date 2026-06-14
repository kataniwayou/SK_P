# Phase 67: Fault-Injection Harness - Pattern Map

**Mapped:** 2026-06-14
**Files analyzed:** 2 (1 new PowerShell orchestrator, 1 test-fixture edit)
**Analogs found:** 2 / 2 (both exact role-matches; all primitives proven in the close-script family)

> RESEARCH.md already supplied the exact analog file:line refs and most code frames. This doc is the
> tight **file → analog → excerpt** table the planner/executor pattern-matches against. It does NOT
> re-decide D-01..D-16; it cites the in-repo code to copy.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `scripts/phase-67-harness.ps1` (NEW) | ops orchestrator script (PowerShell) | request-response + event-driven poll + process-invoke (shells docker/psql/HTTP/`dotnet test`, exit-code-as-verdict) | `scripts/phase-62-close.ps1` (script frame, Invoke-RestMethod, `dotnet test`, health-wait) + `scripts/phase-65-reset.ps1` (psql exec, `Write-Phase`, exit-code discipline) + `scripts/phase-65-up.ps1` (NDJSON-per-replica health loop) | exact (composition of proven siblings) |
| `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` (MODIFIED, D-16) | test-fixture edit (~10-line env-var seam) | transform (env-var read → existing scenarioId/window fields, fallback to current consts) | `tests/BaseApi.Tests/Observability/ResolveInstanceIdFacts.cs:34-38` (read-with-fallback-default chain) | exact (same `Environment.GetEnvironmentVariable(...) ?? <default>` idiom, same `Observability/` tree) |

---

## Pattern Assignments

### `scripts/phase-67-harness.ps1` (NEW — ops orchestrator)

The harness is a new sibling of the 18 `scripts/phase-NN-close.ps1` family. Each step below maps to a
verbatim-copyable frame from an existing script. The flow is RESEARCH §Architecture
(`phase-65-up` → per-run: `reset → seed → wf-id → activate(204) → observe/inject → drain → analyze` → `compose down`).

**FRAME 1 — script preamble + Push-Location/finally (copy verbatim).**
Analog: `scripts/phase-62-close.ps1:108-113` + `:473-475`.
```powershell
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    # ... harness body ...
} finally {
    Pop-Location
}
```
Note: `phase-65-reset.ps1` / `phase-65-up.ps1` use the lighter `$ErrorActionPreference='Stop'` only (no
Push-Location); the harness invokes them as child scripts, so it should adopt the **fuller** close-script
frame (`Set-StrictMode` + `Push-Location/finally`) for the `-ScenarioId <id>` entrypoint.

**FRAME 2 — `Write-Phase` console-trace helper (copy, rename prefix).**
Analog: `scripts/phase-65-reset.ps1:37-39`. RESEARCH §Artifacts wants every line prefixed `[phase-67-harness]`.
```powershell
function Write-Phase([string]$msg, [string]$color = 'Cyan') {
    Write-Host "[phase-67-harness] $msg" -ForegroundColor $color
}
```

**FRAME 3 — child-script shell-out with exit-code gate (STEP A/B; codes 10/20).**
Pattern: invoke `phase-65-up.ps1` / `phase-65-reset.ps1` via `pwsh -File`, check `$LASTEXITCODE`, abort with the
distinct code (RESEARCH §D-04 scheme). The reset/up scripts already `exit 2` on internal failure and `exit 0`
on success (`phase-65-reset.ps1:138`, `phase-65-up.ps1:84`) — the harness only checks `$LASTEXITCODE -ne 0`.
The `$LASTEXITCODE`-then-abort idiom is everywhere, e.g. `phase-65-reset.ps1:60-63`:
```powershell
docker exec sk-redis redis-cli FLUSHALL | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Phase "redis-cli FLUSHALL failed (exit $LASTEXITCODE). Aborting." 'Red'
    exit 2          # harness substitutes 10 (up) / 20 (reset) per D-04
}
```

**FRAME 4 — `dotnet test --filter` process-invoke + exit capture (STEP C seeder code 30; STEP H analyzer = verdict).**
Analog: `scripts/phase-62-close.ps1:353-355` (run `dotnet test`, capture `$LASTEXITCODE`).
```powershell
$output = dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build 2>&1 | Out-String
$exit = $LASTEXITCODE
```
Harness specializes with the RESEARCH-cited filters:
`--filter "Category=RealStack&FullyQualifiedName~FanOutSeeder"` (seed, abort 30 on non-zero) and
`--filter "Category=RealStack&FullyQualifiedName~Analyzer"` (verdict — the harness's **final exit mirrors this
exit**, D-04; do NOT remap it to an infra code).

**FRAME 5 — psql sentinel wf-id lookup (STEP D; code 40).**
Analog: `scripts/phase-65-reset.ps1:99` (`docker compose exec -T postgres psql -U postgres -d stepsdb`).
RESEARCH §D-02 gives the exact `-tA` scalar-capture form:
```powershell
$wfId = (docker compose exec -T postgres psql -U postgres -d stepsdb -tA `
          -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'").Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($wfId)) {
    Write-Phase "could not resolve v8-fanout-proof workflow id (psql exit $LASTEXITCODE). Aborting." 'Red'
    exit 40
}
```
Reset's STEP 3 (`:99-102`) is the literal-statement, no-interpolated-input discipline (threat T-65-06) to mirror.

**FRAME 6 — activation gate, HTTP POST hard-204 (STEP E; code 50).**
Analog: `Invoke-RestMethod`/`Invoke-WebRequest` against `$baseApi='http://localhost:8080'`
(`scripts/phase-62-close.ps1:170,181,193`). RESEARCH §D-03 gives the 204-gate form (use `Invoke-WebRequest`
to read `.StatusCode`; `ConvertTo-Json @($wfId)` forces the JSON array — Pitfall 4):
```powershell
$startBody = ConvertTo-Json @($wfId)        # ["<guid>"] — @() forces array, not bare string
try {
    $resp = Invoke-WebRequest -Method Post -Uri 'http://localhost:8080/api/v1/orchestration/start' `
              -ContentType 'application/json' -Body $startBody -TimeoutSec 15 -ErrorAction Stop
} catch { Write-Phase "POST /orchestration/start threw: $($_.Exception.Message)" 'Red'; exit 50 }
if ($resp.StatusCode -ne 204) {
    Write-Phase "activation gate failed — expected 204, got $($resp.StatusCode). Aborting." 'Red'; exit 50
}
```

**FRAME 7 — observe-loop fire poll (STEP F baseline; D-07).**
This is genuinely-new logic (no exact analog) but reuses the `Invoke-RestMethod` GET idiom
(`phase-62-close.ps1:181`). RESEARCH §D-07 gives the Prometheus `orchestrator_dispatch_sent_total` poll
(record value at windowStart, poll until `current - start >= N`, N=4); ES `Step_A` `_count` is the documented
fallback. The bounded-poll-with-deadline shape mirrors `phase-65-reset.ps1:73-83` (heal-wait while-loop):
```powershell
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline) {
    # ... poll condition ...
    if (<condition met>) { break }
    Start-Sleep -Seconds 2
}
```

**FRAME 8 — whole-tier crash sequencer (STEP F crash runs; D-05/06/08; code 60).**
New logic; RESEARCH §D-05/06 gives the verbatim `docker compose stop/start <service>` loop over
`$scenario.targetContainers` with `Start-Sleep -Seconds $scenario.dwellSeconds` (45s) between. Use
**compose service name** (not generated container names) — Pitfall 2. The `| Out-Null` + `$LASTEXITCODE`
guard idiom is `phase-65-reset.ps1:59-63`.

**FRAME 9 — post-`start` NDJSON-per-replica health-wait (recovery gate; code 60).**
Analog (copy verbatim): `scripts/phase-65-up.ps1:37-73` (the multi-replica NDJSON parse — "ALL instances
healthy") and the bounded variant at `scripts/phase-62-close.ps1:260-282`. CRITICAL (Pitfall 3): the harness
MUST confirm both `processor-sample` replicas are `Health=healthy` after `docker compose start` and BEFORE the
run ends / next `phase-65-reset` (reset `phase-65-reset.ps1:114-122` aborts if 0 replicas).
```powershell
$instances = @(docker compose ps $svc --format json 2>$null |
    Where-Object { $_ -match '\S' } |
    ForEach-Object { $_ | ConvertFrom-Json })
$health = if ($instances.Count -eq 0) { 'not-running' }
          else {
              $unhealthy = @($instances | Where-Object { $_.Health -ne 'healthy' })
              if ($unhealthy.Count -gt 0) { "$($unhealthy[0].Health)" } else { 'healthy' }
          }
```
Note: `phase-65-up.ps1:57-61` carries the otel-collector "no healthcheck → `State -eq 'running'`" special-case;
the harness's recovery wait targets only `processor-sample` (always has a healthcheck) so that branch is N/A here.

**FRAME 10 — in-script scenario hashtable (D-12 seam — the Phase 68 "just data" deliverable).**
No analog (new); RESEARCH §Architecture gives the shape. `[ordered]` preserves TEST-01-first (D-10):
```powershell
$Scenarios = [ordered]@{
  'TEST-01' = @{ targetContainers = @();                  faultType = 'none';       injectAfterNFires = 0; dwellSeconds = 0;  notes = 'no-fault baseline' }
  'TEST-02' = @{ targetContainers = @('processor-sample'); faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'processor whole-tier crash' }
}
```

**FRAME 11 — final teardown (STEP Z; code 70 non-fatal).**
`docker compose down` (NO `-v`, keep images — D-15), once in/after the `finally` or as the last try-body step,
guarded so a `down` failure logs loud but still surfaces the prior analyzer verdict (D-04).

---

### `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` (MODIFIED — D-16 env-var seam, ~10 lines)

**Analog:** `tests/BaseApi.Tests/Observability/ResolveInstanceIdFacts.cs:34-38` — the in-repo
read-with-fallback-default idiom (same `Observability/` test tree). Copy its `?? `-chain shape so the standalone
Phase 66 `dotnet test ~Analyzer` keeps passing on the existing defaults:
```csharp
private static string Resolve() =>
    Environment.GetEnvironmentVariable("POD_NAME")
    ?? Environment.GetEnvironmentVariable("HOSTNAME")
    ?? Environment.MachineName
    ?? Guid.NewGuid().ToString("N");
```

**Current hardwired lines the seam must override (verified — these IGNORE any caller scenario-id/window):**

1. `AnalyzerE2ETests.cs:64` — scenario id constant, and `:74` consumes it:
```csharp
private const string DefaultScenarioId = "TEST-01";          // :64
...
var scenarioId = DefaultScenarioId;                          // :74  ← replace RHS with env read
```
2. `AnalyzerE2ETests.cs:93` — self-computed window start (ignores caller window):
```csharp
var windowStartUtc = DateTimeOffset.UtcNow;                  // :93  ← replace RHS with env read
```
3. `AnalyzerE2ETests.cs:108` — self-computed ES upper bound (`snapshotUtc`), fed to the cohort query:
```csharp
var snapshotUtc = DateTimeOffset.UtcNow;                     // :108 ← replace RHS with env read (WINDOW_END_UTC)
```
4. `AnalyzerE2ETests.cs:154-168` — `BuildStepSearchBody(windowStart, snapshot)` already interpolates the two
   timestamps into the ES `range` filter `gte: {{windowStart:o}}, lte: {{snapshot:o}}`. It is **already
   parameterized** — it honors whatever `windowStartUtc`/`snapshotUtc` it is handed. So feeding the env-var
   values into lines 93 + 108 is sufficient; **no edit to `BuildStepSearchBody` itself is required.**
   (RESEARCH Open Question 2 / D-16 planner-verify: confirm on the TEST-01 baseline run that the supplied
   `WINDOW_START_UTC..WINDOW_END_UTC` range yields `TriggerCount ≈ 10` — i.e. it bounds the full ~10-fire
   cohort, not just a ~60s tail.)

**Pattern to apply (D-16 — read-with-fallback so defaults preserved; the `?? ` shape from the analog):**
```csharp
var scenarioId = Environment.GetEnvironmentVariable("SCENARIO_ID") ?? DefaultScenarioId;          // was :74
...
var windowStartUtc = ParseUtcOr(Environment.GetEnvironmentVariable("WINDOW_START_UTC"), DateTimeOffset.UtcNow);  // was :93
...
var snapshotUtc = ParseUtcOr(Environment.GetEnvironmentVariable("WINDOW_END_UTC"), DateTimeOffset.UtcNow);       // was :108
```
- Keep the existing `ScenarioIdPattern` whitelist guard (`:68,79-81`) — the env-supplied `SCENARIO_ID` MUST
  still pass `^[A-Za-z0-9_-]+$` before composing the report path (path-traversal guard T-66-07).
- `WINDOW_END_UTC` overriding `snapshotUtc` means the harness, not the fixture's `DrainMs`, defines the ES upper
  bound; the existing `await Task.Delay(DrainMs)` (`:100`) + poll-to-stable still run for settle. Planner: verify
  the `DrainMs`/poll-to-stable interaction still drains correctly when `snapshotUtc` is caller-supplied (the
  fixture comment at `:102-107` documents the snapshot-vs-after-read tail-gap reasoning to preserve).
- Scope discipline (D-16): test-only edit, NO product code, NO new product log/metric; `PassFailEngine` scoring
  (`:133`) untouched — Phase 66 stays the single source of truth.

---

## Shared Patterns

### Exit-code-as-verdict (D-04)
**Source:** `scripts/phase-62-close.ps1:353-355` (`$exit = $LASTEXITCODE`) + the analyzer's failed-assert→non-zero
contract (`AnalyzerE2ETests.cs:143`).
**Apply to:** Every harness step. Infra aborts use **distinct codes 10/20/30/40/50/60/70** (RESEARCH §D-04
table); the analyzer-verdict path stays at the raw `dotnet test` exit (0=PASS, 1=FAIL) and is the harness's
**final** exit. Never remap the analyzer exit to an infra code.

### NDJSON-per-replica health parse
**Source:** `scripts/phase-65-up.ps1:37-73` (canonical) and `scripts/phase-62-close.ps1:264-282,297-309`.
**Apply to:** Bring-up gate (via `phase-65-up`) AND the post-crash recovery wait (FRAME 9). Multi-replica services
(`processor-sample`, `keeper` = `replicas:2`) emit NDJSON — parse line-by-line, require ALL instances `healthy`;
never feed the concatenated string to a single `ConvertFrom-Json`.

### `docker compose exec -T postgres psql -U postgres -d stepsdb`
**Source:** `scripts/phase-65-reset.ps1:99`, `scripts/phase-62-close.ps1:314`.
**Apply to:** The D-02 sentinel wf-id lookup (FRAME 5). Container-side exec — no host-port/auth string; runs as
the `postgres` superuser; static literal SQL (no interpolated input).

### `Write-Phase`-style prefixed console trace
**Source:** `scripts/phase-65-reset.ps1:37-39`.
**Apply to:** Every harness line, prefix `[phase-67-harness]`. Print windowStart/windowEnd, observed-fire counts,
the injected fault, and the `analyzer-reports/{scenarioId}.json` path (D-04 requires printing the report path).

### Read-env-with-fallback-default (`?? `-chain)
**Source:** `tests/BaseApi.Tests/Observability/ResolveInstanceIdFacts.cs:34-38`.
**Apply to:** The D-16 analyzer-fixture seam ONLY (SCENARIO_ID / WINDOW_START_UTC / WINDOW_END_UTC). Fallback to
the current `const`/`UtcNow` defaults so standalone Phase 66 is unchanged.

---

## No Analog Found

| Logic | Role | Data Flow | Reason | Planner guidance |
|-------|------|-----------|--------|------------------|
| Observe-loop fire-counter (D-07) | poll | event-driven | No existing script polls Prometheus `orchestrator_dispatch_sent_total` for a *fire count* | New logic; reuse `Invoke-RestMethod` GET (`phase-62-close.ps1:181`) + bounded-while-loop shape (`phase-65-reset.ps1:73-83`); query/parse per RESEARCH §D-07 |
| Crash sequencer stop→dwell→start (D-05/06/08) | docker fault op | request-response | No existing script does `docker compose stop/start <service>` for fault injection | New logic; verbatim loop in RESEARCH §D-05/06; guard idiom from `phase-65-reset.ps1:59-63` |
| Scenario hashtable (D-12) | config seam | — | No existing in-script scenario table | New; shape in RESEARCH §Architecture / FRAME 10 |

These three are the *only* genuinely new logic in the phase (RESEARCH "Don't Hand-Roll" key insight: Phase 67 is
almost entirely composition of proven primitives + these three new pieces + the D-16 seam).

## Metadata

**Analog search scope:** `scripts/*.ps1` (close-script family), `tests/BaseApi.Tests/Observability/` (env-read idioms),
`src/BaseApi.Service/Features/Orchestration/` (activation endpoint — already cited in RESEARCH §D-03).
**Files read this pass:** `AnalyzerE2ETests.cs` (1-175), `phase-65-reset.ps1` (full), `phase-65-up.ps1` (1-90),
`phase-62-close.ps1` (105-224, 255-364, 465-475), `ResolveInstanceIdFacts.cs` (28-39); grep of `Observability/`
for `GetEnvironmentVariable`.
**Pattern extraction date:** 2026-06-14

## PATTERN MAPPING COMPLETE
