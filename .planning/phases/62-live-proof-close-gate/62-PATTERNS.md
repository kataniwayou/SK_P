# Phase 62: Live Proof & Close Gate - Pattern Map

**Mapped:** 2026-06-13
**Files analyzed:** 8 (3 create, 5 modify/retag)
**Analogs found:** 8 / 8 (all exact — this phase is a deliberate CLONE/ADAPT of the proven Phase 58 + Phase 61 surface)

> This phase has NO greenfield design. Every new file has an exact, named source analog already in the
> tree (verified file:line against RESEARCH.md). The planner/executor copy the analog and apply only the
> v7 keyspace deltas called out below. Discretion items are flagged inline.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `scripts/phase-62-close.ps1` (CREATE) | config / operator-gate script | batch (snapshot+compare) | `scripts/phase-58-close.ps1` | exact (verbatim clone + 3 deltas) |
| `tests/BaseApi.Tests/Orchestrator/<new>GateKeyspaceE2ETests.cs` (CREATE) | test (xUnit RealStack) | request-response + CRUD (fabricated Redis state) | `GateACompositionE2ETests.cs` (gate verdict pattern) + `SampleRoundTripE2ETests.cs` (factory/seed/teardown) | exact (role + data flow) |
| Fabricated-key seeding helper (CREATE; co-located static) | utility / test-helper | file-I/O → Redis (craft state) | `SampleRoundTripE2ETests.PollForHealthyLivenessAsync` (host-Redis connect + key builders) + Phase-61 `LivenessWatchdogHealthCheckTests` (`Create` craft style) | exact |
| `.planning/phases/62-live-proof-close-gate/62-HUMAN-UAT.md` (CREATE) | doc (operator runbook) | n/a | `.planning/phases/58-.../58-HUMAN-UAT.md` | exact (structure mirror) |
| `compose.yaml` `processor-sample` tier (MODIFY) | config | n/a | `keeper` tier in same file (`:229-252`) | exact (in-file template) |
| `SC1RoundTripE2ETests.cs` (RETAG) | test | n/a | self (trait flip only) | exact |
| `SC2RecoveryPathsE2ETests.cs` (RETAG) | test | n/a | self (trait flip only) | exact |
| `SC3PauseResumeOutageE2ETests.cs` (RETAG) | test | n/a | self (trait flip only) | exact |
| `GateACompositionE2ETests.cs` (RETAG) | test | n/a | self (trait flip only) | exact |

---

## Pattern Assignments

### `scripts/phase-62-close.ps1` (config/operator-gate, batch) — CLONE of `scripts/phase-58-close.ps1`

**Analog:** `scripts/phase-58-close.ps1` (487 lines, read in full). Clone verbatim, then apply ONLY the four deltas below. Everything else (dual SourceHash read `:106-158`, two-schema + two-processor CREATE-IF-ABSENT seed `:162-242`, compose-health pre-flight `:268-292`, dual-config 0-warning build gate `:315-329`, N=3 Smell-A cadence `:331-364`, DLQ depth==0 `:429-455`, `skp:msg:*` count==0 `:457-467`, `_bus_` exclusion, `psql -lqt` SHA) is UNCHANGED.

**DELTA 1 — the load-bearing change (D-09): redis-scan exclusion, BOTH at `:303` (BEFORE) and `:379` (AFTER).**

OLD (phase-58, exact single-key match — REPLACE both lines):
```powershell
$beforeRedis = (docker exec sk-redis redis-cli --scan | Where-Object { $_ -ne "skp:$($procId.ToString().ToLower())" } | Sort-Object -CaseSensitive | Out-String).Trim()
# ...identical at line 379 for $afterRedis
```
NEW (phase-62, prefix exclusion — replaces BOTH `:303` and `:379`):
```powershell
$beforeRedis = (docker exec sk-redis redis-cli --scan | Where-Object { $_ -notmatch '^skp:proc:' } | Sort-Object -CaseSensitive | Out-String).Trim()
# ...identical for $afterRedis
```
Why: v7 steady-state liveness is the index SET `skp:proc:{procId:D}` + per-instance keys `skp:proc:{procId:D}:{instanceId}` (2× healthy Sample replicas + the durably-unhealthy badconfig replica). instanceIds are non-deterministic (pod/hostname-derived), so the exact-key match cannot exclude them. `^skp:proc:` excludes both families (SET + per-instance) for ALL processors in one filter. `skp:data:*` / `skp:msg:*` leak detection is unaffected (those do not start `skp:proc:`).

**DELTA 2 — N=3 cadence block is UNCHANGED but the EXACT fact-count guard to copy is `:331-364`:**
```powershell
$runResults = @()
for ($i = 1; $i -le 3; $i++) {
    ...
    $output = dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build 2>&1 | Out-String
    ...
    $passedMatch = [Regex]::Match($output, 'Passed:\s+(\d+)')
    ...
}
$distinctPassed = @($runResults | Select-Object -ExpandProperty Passed -Unique)
if ($distinctPassed.Count -ne 1) { ...3-GREEN cadence violation... exit 1 }
```
Copy verbatim. The `dotnet test ... --no-build` command at `:336` is UNCHANGED — the gate runs the FULL suite (no `--filter`); the `[Trait("Phase","62")]` retag does NOT change what the gate runs (D-08/D-10 note in RESEARCH).

**DELTA 3 — the seed block is UNCHANGED in shape; the version string at `:222-225` must be VERIFIED (D-12).**
```powershell
$body = @{
    name           = $name
    version        = '3.5.0'     # VERIFY at plan time: src/Processor.Sample/appsettings.json:11 reads "3.5.0";
                                  # carry forward UNLESS v7 intentionally bumps it (SourceHash, not version, is identity)
    description    = "Phase 58 close-gate seed row for $name (genuine embedded hash)."  # RETITLE to Phase 62
    sourceHash     = $sourceHash
    inputSchemaId  = $null
    outputSchemaId = $null
    configSchemaId = $configSchemaId
} | ConvertTo-Json
```
The two GET-or-create functions (`Get-OrCreateSchemaId` `:170`, `Get-OrCreateProcessorId` `:205`), the two sentinel schema definitions (`:192-193`), and the two seed calls (`:239`, `:241`) are carried VERBATIM (CREATE-IF-ABSENT, never PUT — frozen-once-referenced 409, Phase-57 D-06).

**DELTA 4 — retitle (D-08): every "Phase 58" / "v6.0.0" header/Write-Host/operator-append string → "Phase 62" / "v7.0.0".** Notably the header comment `:1-2`, `Write-Host "Phase 58 close gate..."` `:104`, the PASS/FAIL banners `:470/474`, and the operator-append line `:481` (point it at `62-HUMAN-UAT.md`).

**UNCHANGED multi-replica pre-flight (D-12, IMPORTANT — no change needed):** the compose-health loop at `:276-292` already parses NDJSON line-by-line and requires ALL instances healthy (it was written for `keeper` replicas:2). So `processor-sample` at replicas:2 is handled correctly with NO edit. NOTE one nuance to confirm: the post-seed single-service wait at `:251-259` reads `$parsed[0].Health` for an array — for replicas:2 this only checks the FIRST replica; the `$services` pre-flight loop `:280-287` (all-instances-healthy) is the authoritative gate, so this is acceptable, but the planner should confirm the wait does not need to require BOTH (RESEARCH Clone Deltas D-12).

---

### `tests/BaseApi.Tests/Orchestrator/<new>GateKeyspaceE2ETests.cs` (test, RealStack request-response + fabricated CRUD)

**Analogs:** `GateACompositionE2ETests.cs` (the gate-verdict RealStack pattern) + `SampleRoundTripE2ETests.cs` (the `RealStackWebAppFactory`, seed helpers, net-zero teardown). Both reuse the SAME factory wholesale — author NO new harness.

**Trait + collection header to copy (from `GateACompositionE2ETests.cs:51-54`), with Phase=62:**
```csharp
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]   // hermetic filter (Category=RealStack) excludes it; the build gate still COMPILES it
[Trait("Phase", "62")]
[Collection("Observability")]       // DisableParallelization + ICollectionFixture<RealStackNetZeroSweepFixture>
public sealed class <New>E2ETests
```

**Factory wiring + client (from `GateACompositionE2ETests.cs:79-81`) — reuse the nested factory wholesale:**
```csharp
await using var factory = new SampleRoundTripE2ETests.RealStackWebAppFactory();
await factory.InitializeAsync();
using var client = factory.CreateClient();
```

**Imports block to copy (from `GateACompositionE2ETests.cs:1-8`):**
```csharp
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using BaseApi.Tests.Observability.Helpers;
using Messaging.Contracts.Projections;
using StackExchange.Redis;
using Xunit;
```

**Seed pattern (from `GateACompositionE2ETests.cs:93-102`) — reuse the promoted-internal helpers:**
```csharp
var compatibleSchemaId = await SampleRoundTripE2ETests.SeedConfigSchemaAsync(
    client, SampleRoundTripE2ETests.SampleCompatibleSchemaName,
    SampleRoundTripE2ETests.SampleCompatibleSchemaDefinition, ct);
var procId = await SampleRoundTripE2ETests.SeedProcessorAsync(client, hash, ct, configSchemaId: compatibleSchemaId);
var stepId = await SampleRoundTripE2ETests.SeedStepAsync(client, procId, ct);
var wfId   = await SampleRoundTripE2ETests.SeedWorkflowAsync(client, new List<Guid> { stepId }, cron: "* * * * *", ct);
```

**Gate-verdict assertion patterns (the two ends — from `GateACompositionE2ETests.cs`):**
- 422 (no qualifier): `:166-168`
```csharp
var startResp = await client.PostAsJsonAsync("/api/v1/orchestration/start", new List<Guid> { badWorkflowId }, ct);
Assert.Equal(HttpStatusCode.UnprocessableEntity, startResp.StatusCode);
```
- 204 (≥1 healthy admits): `:214-216`
```csharp
var startResp = await client.PostAsJsonAsync("/api/v1/orchestration/start", new List<Guid> { sampleWorkflowId }, ct);
Assert.Equal(HttpStatusCode.NoContent, startResp.StatusCode);
```

**The gate logic this test drives (READ-ONLY context — do NOT change) — `ProcessorLivenessValidator.cs:46-77`:** SMEMBERS index → GET each per-instance → first Healthy+fresh replica admits (short-circuit `:69`); `Status != "Healthy"` ⇒ unhealthy `:67`; `Timestamp.AddSeconds(Interval*2) <= now` ⇒ stale `:68`; null/JsonException ⇒ malformed `:63-64`; absent ⇒ lazy `SREM` fire-and-forget `:53-54`; no qualifier ⇒ `ProcessorNotLive` 422 with COUNTS-ONLY reason `:74-77`. The fabricated keys (below) must round-trip through THIS exact reader.

**Net-zero teardown registration (from `GateACompositionE2ETests.cs:174-176` / `SampleRoundTripE2ETests.cs:143-145`):**
```csharp
factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
factory.L2KeysToCleanup.Add($"skp:{wfId}");
factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepId}");
```
**LANDMINE (RESEARCH Pitfall 6):** the `RealStackNetZeroSweepFixture` does NOT sweep `skp:proc:*` (steady-state, gate-excluded). Every fabricated `skp:proc:*` key AND every fabricated index member MUST be registered in `L2KeysToCleanup` + a SREM list, or it pollutes a later test's gate verdict. Prefer a DISTINCT throwaway `procId` for pure-fabrication verdict tests so the index does not collide with the live replicas' index.

---

### Fabricated-key seeding helper (CREATE; `static`, co-located with `PollForHealthyLivenessAsync`)

**Analogs:** the host-Redis connect + key builders from `SampleRoundTripE2ETests.PollForHealthyLivenessAsync` (`:218-255`); the `Create`-factory craft style from Phase-61 `LivenessWatchdogHealthCheckTests` (`:57-68`). Place it in `tests/BaseApi.Tests/Orchestrator/` (same namespace/file family) so both new fixtures and future tests reuse it. Mirror the Phase-61 WR-01/WR-02 craft-redis-state pinning.

**Host-Redis const to reuse (from `SampleRoundTripE2ETests.cs:407`):**
```csharp
private const string HostRedis = "localhost:6380,abortConnect=false,connectTimeout=5000";
```

**Connect + key-builder pattern to mirror (from `PollForHealthyLivenessAsync` `:220-232`):**
```csharp
await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
var db = mux.GetDatabase();
// key builders (load-bearing — L2ProjectionKeys.cs:44-51):
//   L2ProjectionKeys.PerInstance(procId, instanceId) => "skp:proc:{procId:D}:{instanceId}"
//   L2ProjectionKeys.InstanceIndex(procId)           => "skp:proc:{procId:D}"
```

**The entry `Create` factory (the ONLY sanctioned construction path) — `ProcessorLivenessEntry.cs:27-47`:**
```csharp
public static ProcessorLivenessEntry Create(
    string? inputOutcome, string? outputOutcome, string? configOutcome,
    DateTime timestamp, int interval)
// null outcome => SchemaOutcome.Success; ANY SchemaOutcome.Fail => Status = LivenessStatus.Unhealthy; else Healthy.
```
Wire shape pinned by `[property: JsonPropertyName]` (`ProcessorLivenessEntry.cs:14-18`, `LivenessSummary.cs:55-58`): top-level `timestamp`/`interval`/`status`/`summary`; summary `inputSchema`/`outputSchema`/`configSchema`. NEVER hand-author the JSON (Pitfall 5) — build via `Create` + `JsonSerializer.Serialize(entry)` (default options).

**Craft recipes (RESEARCH Exact Contracts / Code Examples — verified against the reader):**
```csharp
var now = DateTime.UtcNow;
// healthy sibling (fresh) — admits:
var healthy   = ProcessorLivenessEntry.Create(null, null, null, now, interval: 10);
// unhealthy sibling — fails THAT replica (any Fail => Unhealthy):
var unhealthy = ProcessorLivenessEntry.Create(SchemaOutcome.Fail, null, null, now, interval: 10);
// stale sibling — deadline = (now-25)+20 = now-5 <= now => stale (ProcessorLivenessValidator.cs:68):
var stale     = ProcessorLivenessEntry.Create(null, null, null, now.AddSeconds(-25), interval: 10);
```
Use `LivenessStatus.Healthy`/`.Unhealthy` consts (`LivenessStatus.cs:12-13`) and `SchemaOutcome.Success`/`.Fail` — never string literals.

**The full helper body (mirror this — write + SADD + register-for-teardown):**
```csharp
internal static async Task SeedFabricatedLivenessAsync(
    SampleRoundTripE2ETests.RealStackWebAppFactory factory,
    Guid procId, string instanceId, ProcessorLivenessEntry entry, CancellationToken ct)
{
    await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
    var db = mux.GetDatabase();
    var key = L2ProjectionKeys.PerInstance(procId, instanceId);
    await db.StringSetAsync(key, JsonSerializer.Serialize(entry), TimeSpan.FromSeconds(60));
    await db.SetAddAsync(L2ProjectionKeys.InstanceIndex(procId), instanceId);
    factory.L2KeysToCleanup.Add(key);                 // net-zero: delete the per-instance key
    // + register `instanceId` into a SREM-from-InstanceIndex(procId) list drained in DisposeAsync
    //   (NOT ParentIndexMembersToSrem — that SREMs the bare skp: parent index, NOT skp:proc:{procId})
}
```
**NOTE (discretion / minor harness extension):** `RealStackWebAppFactory.DisposeAsync` (`SampleRoundTripE2ETests.cs:477-493`) currently drains `L2KeysToCleanup` (KeyDelete) + `ParentIndexMembersToSrem` (SREM from `ParentIndex()` = bare `skp:`). It has NO per-`skp:proc:` index SREM list. The planner must either (a) add a small `InstanceIndexMembersToSrem` list (keyed by procId) to the factory, or (b) have the helper's caller SREM the fabricated member in its own teardown. Either is consistent with the existing net-zero discipline.

---

### `.planning/phases/62-live-proof-close-gate/62-HUMAN-UAT.md` (doc, operator runbook)

**Analog:** `.planning/phases/58-.../58-HUMAN-UAT.md` (read in full). Mirror its section skeleton exactly; swap v6→v7 facts.

**Structure to mirror (section headings, verbatim shape):**
- Frontmatter: `status: pending` (flips to `passed` only on operator GREEN), `phase: 62-live-proof-close-gate`, `source: [62-..-PLAN.md]`.
- `## Current Test` — one-line live-run status (starts PENDING).
- `## Purpose` — "FINAL phase of v7.0.0; the live run IS the proof; TEST-01/02/03 stay `[ ]` until operator GREEN."
- `## Step 1 — Clean host build` — `dotnet clean SK_P.sln` + `-c Release` + `-c Debug` (the SourceHash-match rationale block, `58:59-87`).
- `## Step 2 — Rebuild the v7 stack WITH the badconfig profile` — start from CLEAN redis keyspace (BEFORE-dirty trap, `58:92-99`), then the rebuild command `58:105` (UNCHANGED per RESEARCH D-10):
  ```
  docker compose --profile badconfig up -d --build baseapi-service orchestrator processor-sample keeper processor-badconfig
  ```
  (`processor-sample` now scales to 2 via `deploy.replicas`; the up command is identical.)
- `## Step 3 — Invoke the close gate` — `pwsh -File scripts/phase-62-close.ps1` + the numbered what-it-does list + exit codes (`58:130-167`).
- **NEW lifecycle sections (the v7 additions OUTSIDE the close window — D-03/D-11, the genuinely-multi-container proofs xUnit can't do):**
  - TEST-01a: two REAL replicas self-register — `SMEMBERS skp:proc:{procId:D}` shows 2 distinct instanceIds, each with a `skp:proc:{procId:D}:{instanceId}` per-instance key.
  - TEST-01b: durably-broken replica observable as `unhealthy` (NOT absent) — bring up `--profile badconfig`; confirm `skp:proc:{badId:D}:*` exists with `status=Unhealthy` (v7 NEW — replaces v6's "stably absent" assertion; RESEARCH Pitfall 2).
  - TEST-01c: dead-replica TTL-expiry + lazy-SREM — `docker stop` a HEALTHY `processor-sample` replica, wait > 30s (heartbeat TTL = `max(interval*2, Ttl)` = `max(20,30)` = 30s; RESEARCH RF-03 — NOT 60s, which is the startup/unhealthy key), confirm its per-instance key `GET`→null, trigger an orchestration-start so the absent member is lazily SREM'd, assert `SMEMBERS` shrinks.
  - TEST-02-probe-live: `docker exec <replica> wget -qO- localhost:8082/health/live` on a HEALTHY replica → 200 + summary keys `inputSchema`/`outputSchema`/`configSchema` in body (D-06 live half; the verdict half is already hermetic in `LivenessWatchdogHealthCheckTests`). Resolve the replica name dynamically:
    ```powershell
    $name = (docker compose ps processor-sample --format json | ConvertFrom-Json | Select-Object -First 1).Name
    docker exec $name wget -qO- http://localhost:8082/health/live
    ```
    Do NOT target badconfig's probe — a looping-unhealthy startup keeps L1 fresh, so its `/health/live` returns Healthy by design (RESEARCH Open Question 1).
- `## Step 4 — Record the GREEN run` — copy the record-block TABLE (`58:213-228`): three SHA values, identical N=3 `Passed` count, `skp-dlq-1` depth==0, `skp:msg:*` count==0, gate exit 0, run date/operator. The redis SHA note changes from "single Sample exclusion" to "`^skp:proc:*` prefix excluded (2 Sample replicas + index + badconfig replica + index)".
- `## Step 5 — DoD gate` — tick TEST-01/TEST-02/TEST-03 (NOT CFG-08/09) ONLY after operator GREEN.
- `## Threat mitigations`, `## Tests`, `## Summary`, `## Gaps` — mirror `58:267-312`.

---

### `compose.yaml` `processor-sample` tier (MODIFY — `:265-290`)

**Analog:** the `keeper` tier in the SAME file (`:229-252`) — `deploy.replicas: 2`, no `container_name`, no `ports:`.

**SOURCE (current `processor-sample`, `:265-290`):**
```yaml
  processor-sample:
    build:
      context: .
      dockerfile: src/Processor.Sample/Dockerfile
    container_name: sk-processor-sample        # <-- DELETE (line 269)
    restart: unless-stopped
    depends_on:
      rabbitmq:
        condition: service_healthy
      redis:
        condition: service_healthy
      baseapi-service:
        condition: service_healthy
    environment:
      RabbitMq__Host: rabbitmq
      RabbitMq__Username: guest
      RabbitMq__Password: guest
      ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
      Processor__ExecutionDataTtl: "5"
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:8082/health/ready"]
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 30s
```

**TARGET (mirror `keeper` `:229-252`):** delete `container_name: sk-processor-sample`; add a `deploy:` block (mirroring keeper `:233-234`):
```yaml
  processor-sample:
    build:
      context: .
      dockerfile: src/Processor.Sample/Dockerfile
    deploy:
      replicas: 2
    restart: unless-stopped
    depends_on:
      rabbitmq:
        condition: service_healthy
      redis:
        condition: service_healthy
      baseapi-service:
        condition: service_healthy
    environment:
      RabbitMq__Host: rabbitmq
      RabbitMq__Username: guest
      RabbitMq__Password: guest
      ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
      Processor__ExecutionDataTtl: "5"
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:8082/health/ready"]
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 30s
```
**Change set:** exactly two edits — (1) DELETE `container_name: sk-processor-sample` (`:269`); (2) ADD `deploy:\n      replicas: 2`. There is NO `ports:` block to remove (8082 is already container-internal — RESEARCH Compose Reshape). Both replicas share internal port 8082 harmlessly (same as keeper on 8083). `processor-badconfig` tier (`:298-324`) is UNCHANGED — reused AS-IS for D-05 (RECOMMENDED, RF-01: it resolves identity → Gate-A clash → `WriteUnhealthyAsync(configOutcome=Fail)` → durable `Unhealthy` key, never flips healthy).

---

### Retag-only files (D-10) — flip `[Trait("Phase","58")]` → `[Trait("Phase","62")]`

These are mechanical one-line edits. Current trait line + location:

| File | Line | Current | Change to |
|------|------|---------|-----------|
| `GateACompositionE2ETests.cs` | 53 | `[Trait("Phase", "58")]` | `[Trait("Phase", "62")]` |
| `SC1RoundTripE2ETests.cs` | 72 | `[Trait("Phase", "58")]             // retagged into the phase-58 milestone close gate (was Phase 55)` | `[Trait("Phase", "62")]` (update the trailing comment to "phase-62 milestone close gate") |
| `SC2RecoveryPathsE2ETests.cs` | 76 | `[Trait("Phase", "58")]` | `[Trait("Phase", "62")]` |
| `SC3PauseResumeOutageE2ETests.cs` | 96 | `[Trait("Phase", "58")]` | `[Trait("Phase", "62")]` |

**Doc-comment references (cosmetic, optional but consistent):** `SC1RoundTripE2ETests.cs:30` and `SC3PauseResumeOutageE2ETests.cs:20` mention `[Trait("Phase","58")]` in XML-doc prose — update for accuracy when retagging. The `[Trait("Category","RealStack")]` on all four STAYS (excluded from hermetic suite, included in the live gate).

---

## Shared Patterns

### RealStack harness (factory + seed + net-zero teardown)
**Source:** `SampleRoundTripE2ETests.cs` — nested `internal sealed class RealStackWebAppFactory : Composition.Phase8WebAppFactory` (`:417-495`).
**Apply to:** the new GateKeyspace test + the fabricated-key helper.
- Host overrides in ctor (`:421-445`): RMQ `localhost:5673`, Redis `localhost:6380` (`HostRedisFull` `:447`), Postgres `localhost:5433` (`HostPostgres` `:448-449`), OTEL `localhost:4317`.
- Teardown drains `L2KeysToCleanup` (KeyDelete) + `ParentIndexMembersToSrem` (SREM from bare `skp:` parent index) in `DisposeAsync` (`:477-493`).
- Promoted-internal seed helpers reused across files: `SeedProcessorAsync(client, sourceHash, ct, configSchemaId=null)` `:315`, `SeedConfigSchemaAsync(client, sentinelName, definition, ct)` `:357`, `SeedStepAsync` `:376`, `SeedWorkflowAsync(client, entryStepIds, cron, ct)` `:391`, `PollForHealthyLivenessAsync(procId, ct)` `:218`.
- Shared sentinels: `SampleCompatibleSchemaName` `:81`, `SampleCompatibleSchemaDefinition` `:86` (the EXACT Gate-A-compatible schema the close script + GateAComposition reuse).

### Genuine embedded SourceHash read (identity loop)
**Source:** `SampleRoundTripE2ETests.cs:111-113` (Sample) / `GateACompositionE2ETests.cs:86-88` (BadConfig).
**Apply to:** any new RealStack test that seeds a real container's row.
```csharp
var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
    .GetCustomAttributes<AssemblyMetadataAttribute>()
    .First(a => a.Key == "SourceHash").Value!;
```
The script-side equivalent is the reflection read in `phase-58-close.ps1:129-137`. D-16: host build hash MUST match the rebuilt container image hash, or the gate false-passes/times out (RESEARCH Pitfall 3).

### Key builders + status/outcome consts (SoT — never literals)
**Source:** `L2ProjectionKeys.cs:44-51` (`PerInstance`/`InstanceIndex`), `ProcessorLivenessEntry.cs:27` (`Create`), `LivenessStatus.cs:12-13` (`Healthy`/`Unhealthy`).
**Apply to:** the fabricated-key helper, the gate test assertions, the close-script exclusion rationale.

### Net-zero sweep fixture (collection-scoped)
**Source:** `RealStackNetZeroSweepFixture` (wired via `[Collection("Observability")]`). Sweeps `skp:data:*`/`skp:msg:*` + purges `skp-dlq-1` after the last host-stack test. Does NOT sweep `skp:proc:*` (RESEARCH Harness Reuse Surface) — fabricated `skp:proc:*` keys are the test's own cleanup responsibility.

---

## No Analog Found

None. Every file in scope has an exact, named analog already in the tree.

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| — | — | — | (no gaps — this phase is a deliberate clone/adapt of the proven Phase 58 close gate + Phase 61 craft-state + the in-file `keeper` replica template) |

---

## Metadata

**Analog search scope:** `scripts/`, `tests/BaseApi.Tests/Orchestrator/`, `tests/BaseApi.Tests/Features/Liveness/`, `src/Messaging.Contracts/Projections/`, `src/BaseApi.Service/Features/Orchestration/Validation/`, `compose.yaml`, `.planning/phases/58-.../`.
**Files scanned (read for excerpts):** `phase-58-close.ps1`, `L2ProjectionKeys.cs`, `ProcessorLivenessEntry.cs`, `LivenessStatus.cs`, `SampleRoundTripE2ETests.cs`, `GateACompositionE2ETests.cs`, `ProcessorLivenessValidator.cs`, `LivenessWatchdogHealthCheckTests.cs`, `compose.yaml:220-348`, `58-HUMAN-UAT.md`. Trait lines confirmed via grep in `SC1/SC2/SC3RoundTrip/Recovery/PauseResume`.
**All RESEARCH.md file:line pointers VERIFIED against source** (the D-09 exclusion at `:303`/`:379`, the seed block at `:222-225`, the key builders at `L2ProjectionKeys.cs:44-51`, the `Create` factory at `ProcessorLivenessEntry.cs:27`, the gate reader at `ProcessorLivenessValidator.cs:46-77`, the `keeper` template at `compose.yaml:229-252`, all four `[Trait("Phase","58")]` lines).
**Pattern extraction date:** 2026-06-13

## PATTERN MAPPING COMPLETE
