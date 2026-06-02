# Phase 28: SourceHash Identity + Processor.Sample + E2E Closeout - Pattern Map

**Mapped:** 2026-06-02
**Files analyzed:** 13 (8 new src/config, 1 new script, 4 test new/modified)
**Analogs found:** 12 / 13 (1 net-new: the MSBuild `.targets`)

> **Key insight (from RESEARCH.md):** Phase 28 is almost entirely *mirroring* a proven in-repo triad — `Orchestrator` (thin console + multistage Dockerfile + compose tier + csproj), `CorrelationPropagationE2ETests` (host-stack E2E harness), and `scripts/phase-22-close.ps1` (triple-SHA gate). The only net-new logic is ~30 lines of C# inside one MSBuild inline task. Resist building anything else.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseProcessor.Core/SourceHash.targets` | config (build tooling) | transform (build-time hash) | RESEARCH §1/§2 + MS Learn (no in-repo `.targets`) | NO ANALOG (research-pattern) |
| `src/Processor.Sample/Processor.Sample.csproj` | config (project) | n/a | `src/Orchestrator/Orchestrator.csproj` | exact (worker console csproj) |
| `src/Processor.Sample/Program.cs` | config (composition root) | event-driven (host bootstrap) | `src/Orchestrator/Program.cs` | exact (thin Generic-Host shell) |
| `src/Processor.Sample/SampleProcessor.cs` | service (concrete transform) | request-response (dispatch→result) | `DispatchTestKit.FakeProcessor` + `BaseProcessor` | exact (`: BaseProcessor`, override `ProcessAsync`) |
| `src/Processor.Sample/appsettings.json` | config | n/a | `src/Orchestrator/appsettings.json` | exact |
| `src/Processor.Sample/Dockerfile` | config (container) | n/a | `src/Orchestrator/Dockerfile` | exact (multistage net8.0) |
| `compose.yaml` (MODIFIED) | config (orchestration) | n/a | `orchestrator` service block (lines 185-214) | exact |
| `SK_P.sln` (MODIFIED) | config (solution) | n/a | `Orchestrator` project entry (line 17) | exact |
| `tests/BaseApi.Tests/Processor/SourceHashEmbedFacts.cs` | test (reflection) | transform | `AssemblyMetadataSourceHashProvider` reader + RESEARCH §6 reflect | role-match (reflection-over-dll) |
| `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` | test (unit) | request-response | `DispatchTestKit` / `BaseProcessorSeamFacts` | exact (ProcessAsync unit) |
| `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` (MODIFIED) | test (file-regex guard) | n/a | existing facts in same file | exact (extend in place) |
| `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` | test (E2E RealStack) | request-response (live round-trip) | `CorrelationPropagationE2ETests` | exact (host-stack harness) |
| `scripts/phase-28-close.ps1` | config (gate script) | batch | `scripts/phase-22-close.ps1` | exact (copy + `$services` edit) |

## Pattern Assignments

### `src/BaseProcessor.Core/SourceHash.targets` (config/build, transform) — NO IN-REPO ANALOG

This is the single net-new asset. There is no existing `.targets` in the repo to copy. Use RESEARCH.md Patterns 1-4 + Code Examples §1 directly. The **constraint** comes from the reader seam below.

**Reader constraint (D-03)** — `src/BaseProcessor.Core/Identity/AssemblyMetadataSourceHashProvider.cs:26-28`:
```csharp
var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
var value = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
               .FirstOrDefault(a => a.Key == "SourceHash")?.Value;
```
The attribute key is the literal `"SourceHash"` and it is read off `GetEntryAssembly()` → the target MUST emit onto `Processor.Sample.dll` (the entry/concrete), NOT `BaseProcessor.Core`. Reader fail-fast at lines 30-32 throws naming the KEY only (so the embed MUST land or runtime dies).

**Inline task body** — copy RESEARCH §1 (netstandard2.0-only surface: `SHA256.Create()`, `File.ReadAllText`, `Replace("\r\n","\n")`, `StringComparer.Ordinal`). **Pitfall 1 (HIGH risk):** normalize paths to forward-slash + LF-normalize content so the Windows-dev-build hash == Linux-Docker-build hash.

**Two-target split** — copy RESEARCH Pattern 4 (always-run emit target reads stamp + adds `<AssemblyAttribute>`; incremental compute target writes stamp). Required to avoid Pitfall 2 (stale/dropped attribute on incremental builds).

**File-scope glob** — RESEARCH Pattern 2: `$(MSBuildThisFileDirectory)**\*.cs` (reaches `BaseProcessor.Core`) + `$(MSBuildProjectDirectory)\**\*.cs` (the concrete), Exclude `obj/**`, `*.g.cs`, `*GlobalUsings*`, `*AssemblyInfo*`. `BaseConsole.Core`/`Messaging.Contracts` are excluded automatically (siblings, not under either dir) — assert in a test.

---

### `src/Processor.Sample/Processor.Sample.csproj` (config) — analog `src/Orchestrator/Orchestrator.csproj`

**PropertyGroup pattern** (Orchestrator.csproj:24-32) — copy verbatim, swap names:
```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <RootNamespace>Processor.Sample</RootNamespace>
  <AssemblyName>Processor.Sample</AssemblyName>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <NoWarn>$(NoWarn);CS1591</NoWarn>
</PropertyGroup>
```
> `TargetFramework=net8.0`, `Nullable`, `TreatWarningsAsErrors` etc. inherit from `Directory.Build.props` — DO NOT redeclare (Orchestrator.csproj:16-18 comment).

**Package + Content pattern** (Orchestrator.csproj:34-50) — CPM (no `Version=`); `MassTransit.RabbitMQ` is REQUIRED (Pitfall 5 — `BaseProcessor.Core.csproj` declares `MassTransit` but NOT the transport); `appsettings.json` Content copy is REQUIRED (Pitfall 6 — worker SDK does not copy it):
```xml
<ItemGroup>
  <PackageReference Include="MassTransit" />
  <PackageReference Include="MassTransit.RabbitMQ" />
</ItemGroup>
<ItemGroup>
  <Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

**ProjectReference + Import** (mirror Orchestrator.csproj:52-55, BUT this concrete references `BaseProcessor.Core` + `Messaging.Contracts`, and adds the D-01 explicit `<Import>` — see RESEARCH §2):
```xml
<ItemGroup>
  <ProjectReference Include="..\BaseProcessor.Core\BaseProcessor.Core.csproj" />
  <ProjectReference Include="..\Messaging.Contracts\Messaging.Contracts.csproj" />
</ItemGroup>
<Import Project="..\BaseProcessor.Core\SourceHash.targets" />
```

---

### `src/Processor.Sample/Program.cs` (config/composition root, event-driven) — analog `src/Orchestrator/Program.cs`

**Thin Generic-Host shell** (Orchestrator/Program.cs:18-21) — copy the bootstrap shape; the processor variant replaces the orchestrator's consumer wiring with `AddBaseProcessor` + the single concrete registration (RESEARCH Pattern 5; `AddBaseProcessor` verified to wire identity/liveness/dispatch/heartbeat — see `BaseProcessorServiceCollectionExtensions.cs:50-110`):
```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.AddBaseConsoleObservability(builder.Configuration);     // metrics-only OTel (Orchestrator/Program.cs:20)
builder.Services.AddBaseProcessor(builder.Configuration);        // identity+liveness+dispatch+heartbeat
builder.Services.AddSingleton<BaseProcessor, SampleProcessor>(); // the ONE concrete seam
var host = builder.Build();
await host.RunAsync();
```
> Do NOT call `AddBaseConsole`/`AddBaseConsoleMessaging` directly — `AddBaseProcessor` calls them internally (`BaseProcessorServiceCollectionExtensions.cs:53,61`). Do NOT replicate the orchestrator's `StartupCompletionService` removal (lines 63-68) — `AddBaseProcessor` already does it (extension lines 102-107). `AddBaseConsoleObservability` STAYS in Program.cs (it needs `IHostApplicationBuilder` / `ILoggingBuilder`).

**Consumer-resolution contract:** `EntryStepDispatchConsumer` ctor takes `BaseProcessor processor` (verified `DispatchTestKit.Build` line 119-126), so register the concrete AS `BaseProcessor` (the abstract), not as `SampleProcessor`.

---

### `src/Processor.Sample/SampleProcessor.cs` (service/concrete, request-response) — analog `DispatchTestKit.FakeProcessor` + `BaseProcessor`

**Seam shape** (`BaseProcessor.cs:22-23`) — the concrete overrides EXACTLY this one `protected abstract` method, nothing else:
```csharp
protected abstract Task<IReadOnlyList<ProcessResult>> ProcessAsync(
    string inputData, string config, CancellationToken ct);
```

**Concrete pattern** (mirror `DispatchTestKit.FakeProcessor` at lines 32-62 — the only existing `: BaseProcessor` subclass). D-04: return a SINGLE fixed deterministic `ProcessResult`:
```csharp
public sealed class SampleProcessor : BaseProcessor
{
    protected override Task<IReadOnlyList<ProcessResult>> ProcessAsync(
        string inputData, string config, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProcessResult>>(
            new[] { new ProcessResult("<single deterministic dummy payload>") });
}
```
> `ProcessResult` is a `sealed record ProcessResult(string OutputData)` (`ProcessResult.cs:10`) — carries ONLY output data, NO outcome (the framework owns outcomes). The dummy payload shape is Claude's discretion (D-04). No infra/DI/id/L2/bus code in this file (BPC-02).

---

### `src/Processor.Sample/appsettings.json` (config) — analog `src/Orchestrator/appsettings.json`

Copy the Orchestrator file (all 29 lines), change:
- `Service.Name` → `"processor-sample"`, `Service.Version` → `"3.5.0"`
- `ConsoleHealth.Port` → `8082` (avoid orchestrator 8081 / baseapi 8080 collision — RESEARCH §3 note)
- Add a `"Processor"` section for the liveness/TTL knobs bound by `Configure<ProcessorLivenessOptions>(cfg.GetSection("Processor"))` (extension line 77). Notably `ExecutionDataTtl` — set short in the container env (Pitfall 4), default 300.

Keep `Logging`, `ConnectionStrings.Redis`, `RabbitMq`, `AllowedHosts` blocks as-is (Docker-internal hostnames; compose env overrides them at runtime).

---

### `src/Processor.Sample/Dockerfile` (config/container) — analog `src/Orchestrator/Dockerfile`

Copy Orchestrator/Dockerfile (35 lines), swap the reference closure + port. Key load-bearing lines:
- **Restore-cache COPY list** (Orchestrator lines 12-16) — replace with `Messaging.Contracts` + `BaseConsole.Core` + `BaseProcessor.Core` + `Processor.Sample` csprojs (RESEARCH §3 lines 411-416).
- **publish target** (line 19) → `src/Processor.Sample/Processor.Sample.csproj`. **The SourceHash target runs INSIDE this publish (Linux)** — this is where Pitfall 1 cross-OS reproducibility is exercised.
- **Runtime base = `aspnet:8.0-bookworm-slim`** (line 21, NOT `runtime:8.0`) — `BaseConsole.Core` carries the embedded Kestrel health listener (FrameworkReference Microsoft.AspNetCore.App).
- **wget install before `USER app`** (lines 28-30) — the `aspnet` image ships neither wget nor curl, so the compose `wget --spider` healthcheck needs it.
- **Port 8082** — `ENV ASPNETCORE_URLS=http://+:8082` / `EXPOSE 8082` / `ENTRYPOINT ["dotnet", "Processor.Sample.dll"]`.

---

### `compose.yaml` (MODIFIED) — analog `orchestrator` service block (lines 185-214)

Add a `processor-sample` service mirroring the `orchestrator` block, with ONE critical difference + env additions (RESEARCH §4):

**Mirror from orchestrator** (lines 186-214): `build.context`/`dockerfile`, `container_name: sk-processor-sample`, `restart: unless-stopped`, the `RabbitMq__*` + `ConnectionStrings__Redis` + `OTEL_EXPORTER_OTLP_ENDPOINT` env (lines 197-208), and the `wget --spider` healthcheck shape (lines 210-214) pointed at `:8082/health/ready`.

**CRITICAL difference (RESEARCH §4 line 442/457):** `depends_on` MUST add `baseapi-service: { condition: service_healthy }` — the processor resolves identity over the bus FROM the WebApi responder (`ProcessorStartupOrchestrator` queries `GetProcessorBySourceHash`). The orchestrator block does NOT depend on baseapi.

**Add** (Pitfall 4): `Processor__ExecutionDataTtl: "5"` so `skp:data:*` keys self-expire before the close-gate AFTER snapshot.

> Chicken-and-egg (Open Q2): the container `/health/ready` flips green only after a Processor DB row with its hash exists; the boot-before-register retry is UNBOUNDED (host never crashes). The close-gate pre-flight (phase-22-close.ps1:41) may need `processor-sample` added to the health-exception list OR a pre-seeded row. Planner decision.

---

### `SK_P.sln` (MODIFIED) — analog `Orchestrator` project entry (line 17)

Mirror line 17:
```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Orchestrator", "src\Orchestrator\Orchestrator.csproj", "{FB8035AA-443E-4731-9BB9-997B52E5ED12}"
```
Add a `Processor.Sample` entry with a fresh GUID, plus matching `GlobalSection(ProjectConfigurationPlatforms)` Debug/Release rows (lines 26-55) and any `NestedProjects` (lines 59-64) the existing src projects use.

> **MEMORY note:** GSD subagents corrupt STATE.md encoding via Set-Content UTF8 (BOM + mojibake). When editing `.sln` use the Edit tool, not Set-Content, and grep-check for BOM after any scripted write.

---

### `tests/BaseApi.Tests/Processor/SourceHashEmbedFacts.cs` (NEW test, reflection) — analog reader seam + RESEARCH §6

D-08: READ the embedded value (do NOT recompute — anti-pattern). Reflect the built `Processor.Sample.dll` the same way the runtime reader does (`AssemblyMetadataSourceHashProvider.cs:27-28`):
```csharp
var hash = typeof(Processor.Sample.SampleProcessor).Assembly
    .GetCustomAttributes<AssemblyMetadataAttribute>()
    .First(a => a.Key == "SourceHash").Value!;
Assert.Matches(new Regex("^[a-f0-9]{64}$"), hash);   // IDENT-01 + DB validator shape
```
Place in `tests/BaseApi.Tests/Processor/` (namespace `BaseApi.Tests.Processor`, like `DispatchTestKit.cs:16`). Also assert (per RESEARCH Wave-0 map) the file-scope exclusion (a `BaseConsole.Core`/`Messaging.Contracts` file is NOT in `@(ImplFiles)`) — via a build target that dumps the item list. Incremental behavior (Pitfall 2) is a build-script fact, not a pure xUnit fact.

---

### `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` (NEW test, unit) — analog `DispatchTestKit.FakeProcessor` pattern

Unit-test `SampleProcessor.ProcessAsync` returns a single deterministic `ProcessResult` (SAMPLE-01). Since `ProcessAsync` is `protected`, follow the same pattern `DispatchTestKit` uses — either a test subclass exposing it, or invoke via the `internal ExecuteAsync` forwarder (`BaseProcessor.cs:31-32`, `[InternalsVisibleTo]` already grants the test assembly access — verify). Assert `result.Count == 1` and the payload is the fixed deterministic value. Standard xUnit `[Fact]` (no RealStack trait — hermetic).

---

### `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` (MODIFIED) — extend in place

Add facts mirroring the existing file-regex assertion style (e.g. `ComposeYaml_Has_Redis_Service_Block` lines 23-28, `ComposeYaml_BaseApi_DependsOn_Redis_Healthy` lines 80-86). Reuse the existing `ComposeYamlContent()` + `FindRepoRoot()` helpers (lines 16-21, 131-139). New facts per RESEARCH §5:
```csharp
Assert.Contains("container_name: sk-processor-sample", content);
Assert.Matches(new Regex(@"dockerfile:\s*src/Processor\.Sample/Dockerfile"), content);
Assert.Matches(new Regex(@"(?ms)processor-sample:[\s\S]*?baseapi-service:\s+condition:\s+service_healthy"), content);
```
> The class trait is `[Trait("Phase12Wave", "C")]` (line 13) — leave it or add the phase-28 facts as a sibling; match the existing `Assert.Matches`/`Assert.Contains` idiom exactly.

---

### `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` (NEW test, E2E RealStack) — analog `CorrelationPropagationE2ETests`

Mirror the harness MINUS the synthetic liveness seed. Copy these proven pieces verbatim:

**Class attributes + collection** (lines 55-58):
```csharp
[Trait("Category", "E2E")] [Trait("Category", "RealStack")] [Collection("Observability")]
```

**`RealStackWebAppFactory`** (lines 296-406) — reuse WHOLESALE: the env-var-in-ctor host overrides (host RMQ 5673 / Redis 6380 / Postgres 5433 / otel 4317, lines 309-326), `L2KeysToCleanup` / `ParentIndexMembersToSrem` net-zero teardown (lines 379-405). This is the established host-stack driver.

**Seeding helpers** (lines 234-278) — `SeedProcessorAsync` / `SeedStepAsync` / `SeedWorkflowAsync` via CRUD. CHANGE `SeedProcessorAsync`: instead of `HashHelpers.RandomSha256Hex()` (line 241), register the GENUINE embedded hash reflected off `Processor.Sample.dll` (D-08, see SourceHashEmbedFacts excerpt). Keep `InputSchemaId`/`OutputSchemaId`/`ConfigSchemaId` null (D-05 — schema-less; matches lines 242-243 already null).

**Start drive** (lines 93-95):
```csharp
var startResp = await client.PostAsJsonAsync("/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
Assert.Equal(HttpStatusCode.NoContent, startResp.StatusCode);
```

**CRITICAL difference (Pitfall 3 / D-07):** DO NOT call `factory.SeedHostProcessorLiveAsync(procId, ct)` (the analog's line 89). The REAL `Processor.Sample` container writes the genuine `skp:{procId}` Healthy heartbeat — seeding it would mask whether the live gate works. Instead, after seeding the DB row, POLL host Redis for `L2ProjectionKeys.Processor(procId)` presence/freshness (`L2ProjectionKeys.cs:37`) BEFORE POSTing Start.

**Assertion (Claude's discretion, D-09):** prove "orchestrator advances" via the ES advance/seam log (reuse `ElasticsearchTestClient.PollEsForLog`, the term-on-`attributes` query pattern at lines 125-176) AND/OR poll `skp:data:*` (`L2ProjectionKeys.ExecutionData`, line 39) for the L2 output write. The entryId is server-minted (unknown), so an L2 SCAN of `skp:data:*` is needed for the output-written clause.

**Teardown (Pitfall 4):** register the run's `skp:data:*` keys for cleanup (the factory's `L2KeysToCleanup` / `DisposeAsync` at lines 379-405 is the mechanism). Liveness key `skp:{procId}` is steady-state (container keeps it) so it's in BOTH gate snapshots — leave it.

---

### `scripts/phase-28-close.ps1` (NEW) — analog `scripts/phase-22-close.ps1`

Copy phase-22-close.ps1 (186 lines) VERBATIM, change exactly (RESEARCH §7):
1. Header label `Phase 22` → `Phase 28` (lines 1, 30, 171, 175, 180) + version label.
2. `$services` list (line 34) — add `'processor-sample'`:
```powershell
$services = @('postgres','redis','rabbitmq','otel-collector','elasticsearch','prometheus',
              'orchestrator','processor-sample','baseapi-service')
```

Everything else unchanged: the triple-SHA BEFORE/AFTER (`psql -lqt` lines 50-54, `redis-cli --scan` lines 56-60, `rabbitmqctl list_queues` lines 62-66), zero-warning Release+Debug build gate (lines 68-81), 3-GREEN cadence with `Passed:` parse + distinct-count guard (lines 83-116), invariant assertions (lines 138-173).

> Teardown extension (D-10 / Pitfall 4) lives in the TEST + the container's short `ExecutionDataTtl`, NOT the gate script. **Open Q2:** the pre-flight health loop aborts on non-healthy except `otel-collector` (line 41) — if `processor-sample` can be unhealthy at pre-flight (no DB row yet), add it to the exception OR pre-seed a row. Planner decision.

## Shared Patterns

### Composition-root delegation (do not hand-roll infra)
**Source:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:50-110`
**Apply to:** `Program.cs` (and by exclusion, `SampleProcessor.cs` carries NO infra).
`AddBaseProcessor` folds `AddBaseConsole` + `AddBaseConsoleMessaging` + identity + liveness + dispatch + heartbeat + the `StartupCompletionService` removal. The concrete adds ONLY `AddBaseConsoleObservability` (in Program.cs) + the single `AddSingleton<BaseProcessor, SampleProcessor>()`. BPC-02/03.

### SourceHash embed/read contract
**Source:** `src/BaseProcessor.Core/Identity/AssemblyMetadataSourceHashProvider.cs:26-32`
**Apply to:** `SourceHash.targets` (producer), `SourceHashEmbedFacts.cs` + `SampleRoundTripE2ETests.cs` (readers).
Key = literal `"SourceHash"`; read off `GetEntryAssembly()`; must be lowercase 64-hex; absence is a fail-fast `InvalidOperationException`. The DB validator (`ProcessorDtoValidator`, `^[a-f0-9]{64}$`) gates the same value at CRUD.

### L2 key shapes (single source of truth)
**Source:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:31-39`
**Apply to:** `SampleRoundTripE2ETests.cs` (liveness poll + teardown), `appsettings.json`/compose (TTL knob).
`Processor(procId)` = `skp:{procId}` (the liveness key to poll, NOT seed); `ExecutionData(entryId)` = `skp:data:{entryId:D}` (the round-trip output key to clean up); `ParentIndex()` = `skp:` (SREM members, never KeyDelete).

### Host-stack E2E driver + net-zero teardown
**Source:** `tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs:296-406`
**Apply to:** `SampleRoundTripE2ETests.cs`.
`RealStackWebAppFactory` (env-var-in-ctor host endpoints) + `L2KeysToCleanup`/`ParentIndexMembersToSrem` drained in `DisposeAsync`. Reuse the factory; OMIT `SeedHostProcessorLiveAsync`.

### Triple-SHA close-gate discipline
**Source:** `scripts/phase-22-close.ps1` (full file)
**Apply to:** `scripts/phase-28-close.ps1`.
3-consecutive-GREEN + `psql -lqt`/`redis-cli --scan`/`rabbitmqctl list_queues` BEFORE==AFTER + zero-warning Release & Debug. FLUSHDB forbidden — targeted KeyDelete/SetRemove only.

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `src/BaseProcessor.Core/SourceHash.targets` | config (MSBuild build tooling) | transform (build-time hash) | No `.targets`/`RoslynCodeTaskFactory` inline-task exists anywhere in the repo. Planner uses RESEARCH.md Patterns 1-4 + Code Examples §1, constrained by the `AssemblyMetadataSourceHashProvider` reader seam (D-03). This is the only net-new code asset; ~30 lines of inline C#. HIGHEST-RISK item = Pitfall 1 cross-OS hash reproducibility (mandatory Wave-0 dual-build verification). |

## Metadata

**Analog search scope:** `src/Orchestrator/`, `src/BaseProcessor.Core/`, `src/Messaging.Contracts/`, `tests/BaseApi.Tests/{Orchestrator,Composition,Processor}/`, `scripts/`, `compose.yaml`, `SK_P.sln`
**Files read (analogs):** Orchestrator Program.cs / .csproj / Dockerfile / appsettings.json; compose.yaml orchestrator block; SK_P.sln entry; BaseProcessor.cs; ProcessResult.cs; BaseProcessorServiceCollectionExtensions.cs; AssemblyMetadataSourceHashProvider.cs; L2ProjectionKeys.cs; CorrelationPropagationE2ETests.cs; ComposeYamlFacts.cs; DispatchTestKit.cs; phase-22-close.ps1
**Pattern extraction date:** 2026-06-02
