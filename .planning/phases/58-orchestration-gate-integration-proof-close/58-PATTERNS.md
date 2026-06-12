# Phase 58: Orchestration-Gate Integration Proof & Close - Pattern Map

**Mapped:** 2026-06-13
**Files analyzed:** 14 (8 new, 6 modified)
**Analogs found:** 14 / 14 (every new/modified file has a direct in-repo analog — this phase is a faithful adaptation of the Phase-55/49 close-gate + the Sample/SC E2E harness)

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Processor.BadConfig/Processor.BadConfig.csproj` | config (project) | build | `src/Processor.Sample/Processor.Sample.csproj` | exact (clone) |
| `src/Processor.BadConfig/Program.cs` | composition-root | event-driven | `src/Processor.Sample/Program.cs` | exact (clone) |
| `src/Processor.BadConfig/BadConfig.cs` | model (TConfig) | transform | `src/Processor.Sample/SampleConfig.cs` | exact (clone + 1 clashing prop) |
| `src/Processor.BadConfig/BadConfigProcessor.cs` | service (processor) | event-driven | `src/Processor.Sample/SampleProcessor.cs` | exact (clone, minimal transform) |
| `src/Processor.BadConfig/Dockerfile` | config | build | `src/Processor.Sample/Dockerfile` | exact (clone, swap publish target) |
| `src/Processor.BadConfig/appsettings.json` | config | — | `src/Processor.Sample/appsettings.json` | exact (clone, swap Service.Name + container name) |
| `SK_P.sln` (add project) | config | — | existing `Processor.Sample` sln entry | exact |
| `compose.yaml` (`processor-badconfig` service) | config | — | `compose.yaml` `processor-sample` tier (~265-290) | exact (clone + `profiles:`) |
| `tests/.../GateACompositionE2ETests.cs` (NEW) | test | request-response + event-driven | `SampleRoundTripE2ETests.cs` | exact (harness reuse) |
| `tests/.../SampleRoundTripE2ETests.cs` (seed helpers) | test | CRUD | `SeedProcessorAsync` (lines 300-330) | exact (extend in place) |
| `scripts/phase-58-close.ps1` (NEW) | config (script) | batch | `scripts/phase-55-close.ps1` | exact (verbatim clone + D-09 seed deltas) |
| `.planning/.../58-HUMAN-UAT.md` (NEW) | doc | — | `.planning/phases/55-live-proof-close-gate/55-HUMAN-UAT.md` | exact (clone) |
| `tests/.../SC1RoundTripE2ETests.cs` (retag) | test | — | self (line 72) | mechanical edit |
| `tests/.../SC2RecoveryPathsE2ETests.cs` + `SC3...cs` (retag) | test | — | self (SC2:76, SC3:96) | mechanical edit |

---

## Pattern Assignments

### `src/Processor.BadConfig/Processor.BadConfig.csproj` (project, build)

**Analog:** `src/Processor.Sample/Processor.Sample.csproj` (whole file — clone verbatim, swap names)

**Whole-file pattern** (lines 14-42 — the load-bearing structure):
```xml
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <RootNamespace>Processor.Sample</RootNamespace>      <!-- → Processor.BadConfig -->
    <AssemblyName>Processor.Sample</AssemblyName>          <!-- → Processor.BadConfig -->
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MassTransit" />
    <PackageReference Include="MassTransit.RabbitMQ" />   <!-- the concrete adds the transport (Pitfall 5) -->
  </ItemGroup>

  <ItemGroup>
    <Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />  <!-- worker SDK doesn't copy it -->
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\BaseProcessor.Core\BaseProcessor.Core.csproj" />
    <ProjectReference Include="..\Messaging.Contracts\Messaging.Contracts.csproj" />
  </ItemGroup>

  <!-- D-01: explicit import — a ProjectReference does NOT auto-flow build/*.targets. -->
  <Import Project="..\BaseProcessor.Core\SourceHash.targets" />
```

**Deltas:** `RootNamespace` + `AssemblyName` → `Processor.BadConfig`. The `<Import ...SourceHash.targets />` line is MANDATORY and load-bearing — it is what folds the new project dir's `.cs` files into a **distinct embedded SourceHash** (the fold is `BaseProcessor.Core/**/*.cs` + `$(MSBuildProjectDirectory)/**/*.cs`, per `SourceHash.targets` lines 80-85). A separate project directory with its own (even trivially different) `.cs` files yields a distinct hash with zero extra effort (RESEARCH Pattern 1). Everything else is byte-identical to Sample. Common props (TargetFramework, Nullable, TreatWarningsAsErrors) inherit from `Directory.Build.props` — do NOT redeclare. No `Version=` on PackageReferences (CPM-pinned).

---

### `src/Processor.BadConfig/Program.cs` (composition-root, event-driven)

**Analog:** `src/Processor.Sample/Program.cs` (whole file — 21 lines, clone verbatim)

**Whole-file pattern** (lines 1-21):
```csharp
using BaseConsole.Core.DependencyInjection;
using BaseProcessor.Core.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Processor.Sample;                                              // → Processor.BadConfig
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

var builder = Host.CreateApplicationBuilder(args);                   // Generic Host, NOT WebApplication

builder.AddBaseConsoleObservability(builder.Configuration);          // metrics-only OTel
builder.Services.AddBaseProcessor(builder.Configuration);            // identity + liveness + dispatch + heartbeat (folds Gate A)
builder.Services.AddSingleton<BaseProcessorBase, SampleProcessor>(); // → BadConfigProcessor — the ONE concrete seam

var host = builder.Build();
await host.RunAsync();
```

**Deltas:** `using Processor.Sample;` → `using Processor.BadConfig;`; `AddSingleton<BaseProcessorBase, SampleProcessor>()` → `BadConfigProcessor`. Nothing else changes — `AddBaseProcessor` folds the entire processor stack INCLUDING the `ProcessorStartupOrchestrator` that runs Gate A (`ConfigSchemaCoverageCheck.Evaluate`). The clash is driven entirely by the seeded ConfigSchemaId vs `BadConfig`'s shape, not by any code change here.

---

### `src/Processor.BadConfig/BadConfig.cs` (model / TConfig, transform)

**Analog:** `src/Processor.Sample/SampleConfig.cs` (whole file — 10 lines)

**Analog pattern** (`SampleConfig.cs:1-10`):
```csharp
using BaseProcessor.Core.Configuration;

namespace Processor.Sample;

public sealed record SampleConfig(string? Value) : ProcessorConfig;
```

**Delta (the clash — D-02, RESEARCH Clash Shape Option A, recommended):**
```csharp
using BaseProcessor.Core.Configuration;

namespace Processor.BadConfig;

// CLR types Quantity as int; the seeded schema types "quantity" as "string" → ConfigSchemaCoverageCheck
// classifies String-vs-int as a CONFIRMED clash (ConfigSchemaCoverageCheck.cs:212-213, return Detail(...)).
public sealed record BadConfig(int Quantity) : ProcessorConfig;
```

**Why this trips Gate A (VERIFIED against `ConfigSchemaCoverageCheck.cs:200-213`):** schema `type:"string"` on a property whose CLR effective type is numeric falls through the `string→string/Guid/DateTime` FINE branch (line 207-211) to `return Detail(name, "string", declared)` (line 213) → `(Covered:false, "property 'quantity': schema string clashes with CLR Int32")`. This is `ProcessorConfig`-derived so the framework deserialize contract (`ProcessorConfig.SerializerOptions`, case-insensitive, ignore-unknown) is the exact one Gate A compares against. A distinct `.cs` file here also contributes to the distinct SourceHash. **Anti-pattern to avoid (RESEARCH Pitfall 3):** do NOT pick a pair the rule table treats as FINE (string→string, integer→int) — Gate A would PASS and BadConfig would go Healthy, false-failing CFG-08.

---

### `src/Processor.BadConfig/BadConfigProcessor.cs` (service / processor, event-driven)

**Analog:** `src/Processor.Sample/SampleProcessor.cs` (whole file — the `ProcessAsync` override seam)

**Analog core pattern** (`SampleProcessor.cs:18-37`):
```csharp
public sealed class SampleProcessor(ILogger<SampleProcessor> logger) : BaseProcessor<SampleConfig>
{
    protected override Task<List<ProcessItem>> ProcessAsync(
        string validatedData, SampleConfig? config, CancellationToken ct)
    {
        var value = config?.Value;
        logger.LogInformation("sample payload received: {Payload}", value);
        if (value == "fail") throw new FailedException("sample reason");
        return Task.FromResult(new List<ProcessItem>
        {
            new(ProcessOutcome.Completed, value ?? "processor-sample-ok", Guid.NewGuid()),
        });
    }
}
```

**Delta:** `: BaseProcessor<SampleConfig>` → `: BaseProcessor<BadConfig>`; minimal transform body (the transform is **never reached** — Gate A withholds the queue bind, so this is dead-but-must-compile code). A trivial one-item Completed return is sufficient; keep the `[assembly] SourceHash` distinct via this distinct file. The generic arg `BadConfig` is what `IConfigTypeProvider`/`BaseProcessorConfigTypeProvider` hands to Gate A as `configType.Get()` (`ProcessorStartupOrchestrator.cs:184`).

---

### `src/Processor.BadConfig/Dockerfile` (config, build)

**Analog:** `src/Processor.Sample/Dockerfile` (whole file — multi-stage net8.0)

**Analog pattern** (`Dockerfile:13-38`, the load-bearing lines):
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY ["Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
COPY ["src/Messaging.Contracts/Messaging.Contracts.csproj", "src/Messaging.Contracts/"]
COPY ["src/BaseConsole.Core/BaseConsole.Core.csproj", "src/BaseConsole.Core/"]
COPY ["src/BaseProcessor.Core/BaseProcessor.Core.csproj", "src/BaseProcessor.Core/"]
COPY ["src/Processor.Sample/Processor.Sample.csproj", "src/Processor.Sample/"]   # → Processor.BadConfig
RUN dotnet restore "src/Processor.Sample/Processor.Sample.csproj"                  # → Processor.BadConfig
COPY src/ src/
RUN dotnet publish "src/Processor.Sample/Processor.Sample.csproj" -c Release -o /publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime           # aspnet (Kestrel health listener), NOT runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends wget && rm -rf /var/lib/apt/lists/*   # wget for /health/ready
COPY --from=build /publish .
USER app
ENV ASPNETCORE_URLS=http://+:8082
EXPOSE 8082
ENTRYPOINT ["dotnet", "Processor.Sample.dll"]                               # → Processor.BadConfig.dll
```

**Deltas:** swap the 3 `Processor.Sample` csproj/publish-target strings → `Processor.BadConfig`, and the ENTRYPOINT dll → `Processor.BadConfig.dll`. The `aspnet:8.0` base + `wget` install + port 8082 + `USER app` all stay (BaseConsole.Core's embedded Kestrel health listener needs the ASP.NET shared framework; `/health/ready` healthcheck needs wget). Port 8082 can stay internal (not host-published — RESEARCH Open Question 2).

---

### `src/Processor.BadConfig/appsettings.json` (config)

**Analog:** `src/Processor.Sample/appsettings.json` (whole file — 41 lines)

**Delta (the only changes):**
```json
  "Service": {
    "Name": "processor-badconfig",   // was "processor-sample" — THIS is the ES service.name scope for the D-06 clash-log query
    "Version": "3.5.0"               // KEEP 3.5.0 (Sample's verified value; SourceHash, not this string, distinguishes identity)
  },
```
Everything else (Redis/RabbitMq/OTel hosts via compose DNS, `ConsoleHealth.Port: 8082`, `Processor.Interval: 10`) stays identical. **`Service.Name: "processor-badconfig"` is load-bearing** — the CFG-08 ES clash-log query (D-06) filters `resource.attributes.service.name == "processor-badconfig"`.

---

### `SK_P.sln` (config) + `compose.yaml` (`processor-badconfig` service)

**Analog (compose):** `compose.yaml` `processor-sample` tier (lines 265-290)

**Analog pattern** (lines 265-290):
```yaml
  processor-sample:                                 # → processor-badconfig
    build:
      context: .
      dockerfile: src/Processor.Sample/Dockerfile   # → src/Processor.BadConfig/Dockerfile
    container_name: sk-processor-sample             # → sk-processor-badconfig (MUST be unique)
    restart: unless-stopped
    depends_on:
      rabbitmq: { condition: service_healthy }
      redis: { condition: service_healthy }
      baseapi-service: { condition: service_healthy }   # processor resolves identity FROM the WebApi responder
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

**Deltas (D-04, D-05):**
- `processor-sample` → `processor-badconfig`; `container_name: sk-processor-badconfig`; `dockerfile: src/Processor.BadConfig/Dockerfile`.
- **Add a `profiles:` key** (e.g. `profiles: ["badconfig"]`) so the default `docker compose up` excludes it; the close gate + Gate-A E2E bring it up with `--profile badconfig`.
- The `/ready` healthcheck **stays** — BadConfig flips `MarkReady()` (no crash-loop, D-05), so `/health/ready` passes normally. It withholds `MarkHealthy` (no `skp:{id}` key) and binds no queue, so it adds NOTHING to the triple-SHA.
- `Processor__ExecutionDataTtl: "5"` can stay (harmless; BadConfig never writes data keys).

**SK_P.sln delta:** add the `Processor.BadConfig.csproj` project entry mirroring the existing `Processor.Sample` entry (so the both-config build gate compiles it).

---

### `tests/BaseApi.Tests/Orchestrator/GateACompositionE2ETests.cs` (NEW test) — CFG-08 + CFG-09

**Analog:** `SampleRoundTripE2ETests.cs` (whole harness — reuse `RealStackWebAppFactory`, `PollForHealthyLivenessAsync`, ES poll, net-zero teardown)

**Trait + collection pattern** (mirror `SampleRoundTripE2ETests.cs:69-72`):
```csharp
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]   // hermetic filter (Category!=RealStack) excludes it; build gate still COMPILES it
[Trait("Phase", "58")]
[Collection("Observability")]      // DisableParallelization + ICollectionFixture<RealStackNetZeroSweepFixture>
public sealed class GateACompositionE2ETests { ... }
```

**ES clash-log query pattern — CFG-08 D-06 causation linchpin** (mirror `SampleRoundTripE2ETests.cs:150-167`, swap the term + service scope):
```csharp
using var es = new ElasticsearchTestClient();
var clashLogQuery = $$"""
  {
    "size": 5,
    "sort": [ { "@timestamp": { "order": "desc" } } ],
    "query": { "bool": { "must": [
      { "term": { "resource.attributes.service.name": "processor-badconfig" } },
      { "match": { "body": "Gate A incompatibility" } }
    ] } }
  }
  """;
var clash = await es.PollEsForLog(clashLogQuery, timeoutMs: 120_000, ct: ct);
Assert.NotNull(clash);
Assert.Contains("Gate A incompatibility", clash!.Value.GetRawText());
```
Source log: `ProcessorStartupOrchestrator.cs:187-189` (`LogError("Gate A incompatibility for processor {ProcessorId} config schema {ConfigSchemaId}: {Clash}", ...)`). **Open Question 1 (RESEARCH):** the robust primary is `service.name` + a `match` on the message text; add the `attributes.ProcessorId` term as a tightening filter once the exact field path is confirmed at execution time. **Do the ES poll FIRST** — it proves the container booted AND ran Gate A, upgrading "absent" from coincidence to causation.

**Inverse liveness-absence poll — CFG-08** (the INVERSE of `PollForHealthyLivenessAsync` at `SampleRoundTripE2ETests.cs:201-240`):
```csharp
// After the ES clash log proves boot+clash, assert skp:{badId} is absent and STAYS absent across
// ~3 reads spanning > one heartbeat interval (Interval=10s). The positive poll returns when the key
// is fresh; the inverse fails if the key EVER appears within the stability window.
await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
var db = mux.GetDatabase();
var key = L2ProjectionKeys.Processor(badId);
for (var i = 0; i < 3; i++)
{
    Assert.True((await db.StringGetAsync(key)).IsNullOrEmpty, $"skp:{badId} unexpectedly present — Gate A did not withhold MarkHealthy");
    await Task.Delay(5_000, ct);
}
```

**CFG-08 Start → 422 assertion** (`ProcessorLivenessValidator.cs:33-35` → `ProcessorNotLive(id,"absent")` → 422):
```csharp
var startResp = await client.PostAsJsonAsync("/api/v1/orchestration/start", new List<Guid> { badWorkflowId }, ct);
Assert.Equal(HttpStatusCode.UnprocessableEntity, startResp.StatusCode);   // 422
```

**CFG-09:** reuse `SampleRoundTripE2ETests`'s positive flow almost verbatim — seed `Processor.Sample` with the **compatible non-null** ConfigSchemaId (Gate A RUNS and PASSES, not skipped), `PollForHealthyLivenessAsync(sampleId)`, Start → `Assert.Equal(HttpStatusCode.NoContent, ...)` (204). The net-zero teardown (`L2KeysToCleanup`, `ParentIndexMembersToSrem`, the GAP-49-8 composite sweep) is inherited from `RealStackWebAppFactory` — register the run's keys exactly as `SampleRoundTripE2ETests.cs:130-142` does.

---

### `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — two-schema seed helpers (extend in place)

**Analog:** `SeedProcessorAsync` (lines 300-330) — the existing GET-or-create processor seed

**Processor GET-or-create pattern (reuse verbatim; flip `ConfigSchemaId`)** (`SampleRoundTripE2ETests.cs:311-329`):
```csharp
var lookup = await client.GetAsync($"/api/v1/processors/by-source-hash/{sourceHash}", ct);
if (lookup.StatusCode == HttpStatusCode.OK)
    return (await lookup.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct))!.Id;

var dto = new ProcessorCreateDto(
    Name: $"sample-proc-{Guid.NewGuid():N}", Version: "1.0.0", Description: null,
    SourceHash: sourceHash,
    InputSchemaId: null, OutputSchemaId: null,
    ConfigSchemaId: null);                       // ← CFG-09 DELTA: pass the compatible $compatibleSchemaId here (was null)
var resp = await client.PostAsJsonAsync("/api/v1/processors", dto, ct);
resp.EnsureSuccessStatusCode();
return (await resp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct))!.Id;
```
`ProcessorCreateDto` field order (VERIFIED `ProcessorDtos.cs:11-18`): `Name, Version, Description, SourceHash, InputSchemaId, OutputSchemaId, ConfigSchemaId`.

**NEW `SeedConfigSchemaAsync` helper — GET-all-then-filter-by-Name (RESEARCH Pattern 2, D-09a):**
```csharp
// Schemas have NO uniqueness constraint (only FK indexes) → a blind POST duplicates every run.
// GET the list, match a fixed sentinel Name, reuse its Id; POST only if absent. NEVER PUT-edit
// (a referenced schema's Definition is FROZEN — PUT → 409, Phase-57 D-06 / SchemaService.cs).
var all = await client.GetFromJsonAsync<List<SchemaReadDto>>("/api/v1/schemas", ct);
var existing = all!.FirstOrDefault(s => s.Name == sentinelName);   // "gateA-sample-compatible" / "gateA-badconfig-clash"
if (existing is not null) return existing.Id;
var dto = new SchemaCreateDto(sentinelName, "1.0.0", null, definitionJson);
var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct);
resp.EnsureSuccessStatusCode();
return (await resp.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct))!.Id;
```
`SchemaCreateDto` field order (VERIFIED `SchemaDtos.cs:10-14`): `Name, Version, Description, Definition`. `SchemaReadDto` exposes `Id, Name, Version, Description, Definition, ...` (lines 33-42). CRUD lives at `/api/v1/schemas` (`SchemasController` — 5 inherited verbs).

**Compatible-schema definition for CFG-09 (`SampleConfig(string? Value)` covers it — any schema Sample covers):**
```json
{ "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
  "properties": { "value": { "type": "string" } } }
```
**Clash-schema definition for CFG-08 (`BadConfig(int Quantity)`; schema types it string → clash):**
```json
{ "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
  "properties": { "quantity": { "type": "string" } } }
```

---

### `scripts/phase-58-close.ps1` (NEW) — verbatim clone of `phase-55-close.ps1` + D-09 seed deltas

**Analog:** `scripts/phase-55-close.ps1` (whole 384-line file — clone verbatim, change ONLY the seed block + service list + bring-up + title)

**Embedded-hash read pattern (reuse verbatim, once per processor)** (`phase-55-close.ps1:99-120`):
```powershell
$sampleDll = Join-Path $repoRoot 'src/Processor.Sample/bin/Release/net8.0/Processor.Sample.dll'
# ... fallback to Debug, then build if absent ...
$asmBytes = [System.IO.File]::ReadAllBytes($sampleDll)
$asm = [System.Reflection.Assembly]::Load($asmBytes)
$sourceHash = ($asm.GetCustomAttributes([System.Reflection.AssemblyMetadataAttribute], $false) |
    Where-Object { $_.Key -eq 'SourceHash' } | Select-Object -First 1).Value
if ([string]::IsNullOrWhiteSpace($sourceHash) -or ($sourceHash -notmatch '^[a-f0-9]{64}$')) { exit 2 }
```
**D-09 DELTA:** repeat this block for `Processor.BadConfig.dll` to read its genuine embedded hash too.

**Processor GET-or-create seed pattern (reuse verbatim, twice)** (`phase-55-close.ps1:122-156`):
```powershell
$baseApi = 'http://localhost:8080'
try {
    $existing = Invoke-RestMethod -Method Get -Uri "$baseApi/api/v1/processors/by-source-hash/$sourceHash" -TimeoutSec 15 -ErrorAction Stop
    $procId = $existing.id
} catch {
    if (([int]$_.Exception.Response.StatusCode) -ne 404) { exit 2 }
    $body = @{ name='processor-sample'; version='3.5.0'; sourceHash=$sourceHash
              inputSchemaId=$null; outputSchemaId=$null; configSchemaId=$null } | ConvertTo-Json   # ← configSchemaId DELTA
    $created = Invoke-RestMethod -Method Post -Uri "$baseApi/api/v1/processors" -ContentType 'application/json' -Body $body -TimeoutSec 15
    $procId = $created.id
}
```

**D-09 DELTAS (the ONLY changes vs phase-55):**
1. **(D-09a)** Before the two processor seeds, GET-or-create TWO `Schema` rows by sentinel Name (`gateA-sample-compatible`, `gateA-badconfig-clash`) — GET-all `/api/v1/schemas` → filter by Name → reuse-or-POST (NEVER PUT — 409 frozen). Set `$compatibleSchemaId` / `$clashSchemaId`.
2. **(D-09a)** Seed `processor-sample` with `configSchemaId = $compatibleSchemaId` (was `$null`); seed a SECOND `processor-badconfig` row with `configSchemaId = $clashSchemaId` (using BadConfig's embedded hash). Both GET-or-create against `uq_processor_source_hash`.
3. **(D-09b)** Do **NOT** add a SHA exclusion for badconfig — it writes no liveness key and binds no queue, so it is simply absent from both snapshots. The existing Sample `skp:{procId}` exclusion (lines 211, 287) STAYS; badconfig needs none.
4. **Health pre-flight (RESEARCH anti-pattern):** keep `processor-sample` in the `$services` health-required list (line 183) and wait for its `skp:{procId}` liveness (lines 158-178). Do **NOT** add `processor-badconfig` to the liveness-required set — it intentionally never goes Healthy; its Docker `/ready` passes but its liveness key must NOT be expected.
5. **(D-09c)** Seed version stays `'3.5.0'` (VERIFIED `appsettings.json:11`; SourceHash, not the version string, distinguishes identity).
6. **(D-09d)** The bring-up referenced in the runbook adds `--profile badconfig` + `processor-badconfig` to the rebuild set.
7. Retitle all "Phase 55" → "Phase 58"; the triple-SHA invariant block (lines 202-365: psql `\l`, redis `--scan`, rabbitmq `list_queues name`, `skp-dlq-1` depth==0, `skp:msg:*` count==0, N=3 identical-fact-count Smell-A guard) is **verbatim**.

---

### `.planning/.../58-HUMAN-UAT.md` (NEW) — operator N=3 GREEN-run runbook

**Analog:** `.planning/phases/55-live-proof-close-gate/55-HUMAN-UAT.md` (whole file — clone the structure)

**Pattern (Steps 1-4 + record block):** clone `55-HUMAN-UAT.md`'s structure: Step 1 clean `dotnet clean + build -c Release` (host hash == container hash), Step 2 `docker compose --profile badconfig up -d --build baseapi-service orchestrator processor-sample keeper processor-badconfig`, Step 3 `pwsh -File scripts/phase-58-close.ps1`, Step 4 record block (3 SHA values + Passed count + DLQ depth + `skp:msg:*` count) + tick CFG-08/CFG-09.

**Deltas:** retitle to Phase 58 / v6.0.0; the bring-up command gains `--profile badconfig` + `processor-badconfig`; the DoD ticks **CFG-08 / CFG-09** (not TEST-01/TEST-02); add the CFG-08 three-signal note (clash-log + absent-liveness + 422). The frontmatter `status:` starts `pending` (the live run is operator-gated, D-12 — CFG-08/09 stay unticked in REQUIREMENTS.md until the GREEN run is recorded).

---

### SC1 / SC2 / SC3 retag (mechanical edit)

**Analog:** the files themselves — single-line trait edits, NO behavior change (v6 left the v5 recovery/slot-array machinery unchanged).

| File | Line | Edit |
|------|------|------|
| `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs` | 72 | `[Trait("Phase", "55")]` → `[Trait("Phase", "58")]` |
| `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` | 76 | `[Trait("Phase", "55")]` → `[Trait("Phase", "58")]` |
| `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs` | 96 | `[Trait("Phase", "55")]` → `[Trait("Phase", "58")]` |

Optionally update the XML-doc comment references to `Phase 55` (SC1:30, SC3:20) for accuracy. `[Trait("Category","RealStack")]` STAYS on all three (excluded from the hermetic run, included in the live close gate).

---

## Shared Patterns

### Distinct SourceHash via a separate project (D-01 / D-13)
**Source:** `src/BaseProcessor.Core/SourceHash.targets` (fold lines 80-85) + the `<Import>` at `Processor.Sample.csproj:40`
**Apply to:** `Processor.BadConfig.csproj` (the `<Import ...SourceHash.targets />` line) + every distinct `.cs` in the new project dir.
A separate project dir importing the targets yields a distinct embedded `[assembly: AssemblyMetadata("SourceHash", "<64-hex>")]` automatically — the runtime reader (`AssemblyMetadataSourceHashProvider`) reads it off `GetEntryAssembly()`, so BadConfig resolves a DISTINCT procId. **Never** hand-roll a hash/GUID — it would diverge from the container's embedded hash and identity would never resolve.

### GET-or-create idempotent seed (D-09a / D-13)
**Source (processor):** `SampleRoundTripE2ETests.cs:311-329` + `phase-55-close.ps1:127-156` (GET by source-hash, 200→reuse, 404→POST against `uq_processor_source_hash`).
**Source (schema):** RESEARCH Pattern 2 — GET-all `/api/v1/schemas` → filter by sentinel Name → reuse-or-POST (schemas have NO unique constraint; never PUT — 409 frozen-once-referenced, `SchemaService.cs`).
**Apply to:** all four seeds (2 schemas + 2 processors) in BOTH the close script AND the E2E helper.

### ES log assertion (D-06 causation linchpin)
**Source:** `SampleRoundTripE2ETests.cs:150-167` (term-query + `service.name` scope) + `ElasticsearchTestClient.PollEsForLog`.
**Apply to:** the CFG-08 clash-log assertion. Target log: `ProcessorStartupOrchestrator.cs:187` (`LogError("Gate A incompatibility ...")`). Scope to `service.name == "processor-badconfig"` + `match "Gate A incompatibility"`. **Do the ES poll FIRST** — absent-liveness alone is observationally identical to "processor not running" (RESEARCH Pitfall 1). Do NOT hand-roll an HTTP/backoff loop — `PollEsForLog` handles 404/empty-hits/backoff.

### Net-zero teardown (Pitfall 4 / GAP-49-8)
**Source:** `RealStackWebAppFactory` (`SampleRoundTripE2ETests.cs:373-464`) — `L2KeysToCleanup` + `ParentIndexMembersToSrem` + the composite `skp:*:{wf}:*` sweep (lines 433-460), plus `RealStackNetZeroSweepFixture` via `[Collection("Observability")]`.
**Apply to:** the new Gate-A tests (join `"Observability"`). CFG-08 binds no queue / writes no data (BadConfig is harmless — adds little teardown). CFG-09 (Sample, cron round-trip) needs the full sweep exactly like `SampleRoundTripE2ETests`. Steady-state `skp:{procId}` liveness keys are LEFT (both snapshots).

### Triple-SHA close protocol (D-08)
**Source:** `phase-55-close.ps1:202-379` — psql `\l` SHA, unfiltered redis `--scan` SHA (Sample `skp:{procId}` excluded via `Where-Object`, `_bus_` transients excluded), rabbitmq `list_queues name` SHA, separate `skp-dlq-1` depth==0, additive `skp:msg:*` count==0, N=3 identical-fact-count Smell-A guard.
**Apply to:** `phase-58-close.ps1` VERBATIM — only the seed block + service list + `--profile` bring-up change.

---

## No Analog Found

None. Every new/modified file has a direct in-repo analog (this phase is an adaptation of the proven Phase-55/49 close-gate + the Sample/SC E2E harness, not new infrastructure).

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| — | — | — | — |

## Metadata

**Analog search scope:** `src/Processor.Sample/`, `src/BaseProcessor.Core/` (Gate A + SourceHash + ConfigSchemaCoverageCheck), `src/BaseApi.Service/Features/{Schema,Processor,Orchestration}/`, `tests/BaseApi.Tests/Orchestrator/` (Sample + SC1/2/3 + RealStackNetZeroSweepFixture), `tests/BaseApi.Tests/Observability/Helpers/`, `scripts/phase-55-close.ps1`, `compose.yaml`, `.planning/phases/55-live-proof-close-gate/`.
**Files scanned (read in full or targeted):** 18.
**Pattern extraction date:** 2026-06-13
