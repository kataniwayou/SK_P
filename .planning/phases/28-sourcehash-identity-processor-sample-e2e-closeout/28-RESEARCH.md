# Phase 28: SourceHash Identity + Processor.Sample + E2E Closeout - Research

**Researched:** 2026-06-02
**Domain:** MSBuild build-time code-identity (RoslynCodeTaskFactory inline task) + thin Generic-Host concrete console + multistage Docker/compose tier + real-stack MassTransit/Redis/Postgres E2E + phase-close gate
**Confidence:** HIGH (every codebase claim grep/read-verified against the real files; MSBuild mechanics CITED to Microsoft Learn + VERIFIED against the dotnet/msbuild .NET 8 limitation issues)

## Summary

Phase 28 is a closeout phase: the framework is fully built (Phases 25-27), and Phase 28 produces the *first concrete consumer of it* plus the build-time identity mechanism its runtime reader already expects. There are three deliverables, all with locked decisions (D-01..D-10) — the open risk is purely *implementation form*.

The single genuinely novel piece is the **SourceHash MSBuild target** (IDENT-01/02). Everything else mirrors a proven, in-repo triad: `Orchestrator` is the exact analog for the thin console + multistage Dockerfile + compose tier + the host-stack E2E (`CorrelationPropagationE2ETests`), and `scripts/phase-22-close.ps1` is the exact analog for the triple-SHA close gate. The runtime reader (`AssemblyMetadataSourceHashProvider`) is already in place and *constrains* the target precisely: it reads `[assembly: AssemblyMetadata("SourceHash", …)]` off `Assembly.GetEntryAssembly()`, so the target MUST emit onto the concrete entry assembly (`Processor.Sample`), not `BaseProcessor.Core`.

**Primary recommendation:** Author the hash-and-embed logic as a shared `.targets` in `src/BaseProcessor.Core/` (D-01), explicitly `<Import>`ed by `Processor.Sample.csproj`. Inside it, declare a `RoslynCodeTaskFactory` inline task (`Code Type="Class"`, netstandard2.0 surface) that takes the implementation file list + an output path, computes the LF-normalized per-file SHA-256 fold over an ordinal path sort, and writes the hash to an output property. A second target consumes that property to add an `<AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">` item (the SDK's `GenerateAssemblyInfo` then emits the `[assembly: AssemblyMetadata]`). Wire `BeforeTargets="CoreCompile"` and use an `Inputs`/`Outputs` stamp file so incremental builds re-hash only when an implementation `.cs` changes. For the E2E (D-08), reflect the *built* `Processor.Sample.dll` (read the `AssemblyMetadataAttribute`) to obtain the genuine embedded hash, register it as the Processor DB row via CRUD, and let a **real `Processor.Sample` container** heartbeat its `skp:{id:D}` liveness so the WebApi Start-path liveness gate passes truthfully.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**SourceHash MSBuild Target (IDENT-01/02)**
- **D-01:** Hash-and-embed logic authored **once** as a shared `.targets` in `BaseProcessor.Core`; each concrete `Processor.*` **explicitly `<Import>`s** it. Reason: `BaseProcessor.Core` is consumed by **ProjectReference, not PackageReference** — a `build/*.targets` convention does NOT auto-flow over a ProjectReference. Rejected: `Directory.Build.targets` under `src/Processor.*/` (implicit-inheritance risk); inline target in `Processor.Sample.csproj` (copy-paste debt).
- **D-02:** Hash computed by an **inline `RoslynCodeTaskFactory` MSBuild task** (no compiled tool, no external script, no NuGet dep). Algorithm locked by IDENT-01: SHA-256, lowercase 64-hex, LF-normalized, per-file hashes folded over an **ordinal path sort** of `BaseProcessor.Core` + the concrete's `.cs` (excluding generated files, `BaseConsole.Core`, `Messaging.Contracts`); `BeforeTargets=CoreCompile`; re-runs on implementation-source change (no stale hash on incremental builds).
- **D-03:** The runtime reader `AssemblyMetadataSourceHashProvider.Get()` resolves from `Assembly.GetEntryAssembly()`. The target MUST emit the attribute onto the **concrete (entry) assembly** (`Processor.Sample`), even though the hash folds in `BaseProcessor.Core`'s sources.

**Processor.Sample Behavior (SAMPLE-01/02)**
- **D-04:** `ProcessAsync` returns a **single, fixed, deterministic dummy result** (multi-result already unit-proven in 27-02; live POC needs only 1).
- **D-05:** `Processor.Sample` runs **schema-less** — `InputSchemaId`/`OutputSchemaId`/`ConfigSchemaId` all null on its registered Processor row. Validation already unit-covered in Phase 27; null schemas keep the E2E minimal. The processor still reads its L2 input key, just skips validation.
- **D-06:** Dockerfile + compose tier **mirror the Orchestrator tier** verbatim in shape (multistage build→runtime; a `processor-sample` service alongside `sk-orchestrator`). `ComposeYamlFacts` extended for the new service.

**Real-Stack E2E Topology (TEST-01)**
- **D-07:** **Both** Orchestrator AND `Processor.Sample` run as **real containers** in the host compose stack; the E2E drives the round-trip **only** via `POST /api/v1/orchestration/start` (in-process WebApi pointed at host stack — RMQ `localhost:5673`, Redis `localhost:6380`, Postgres `localhost:5433`, otel `localhost:4317`), asserting the orchestrator advances on the returned `ExecutionResult`. Established `CorrelationPropagationE2ETests` pattern + one container. A containerized heartbeating `Processor.Sample` is REQUIRED to prove SC#4's liveness-gated Start truthfully. Rejected: Sample-container + orchestrator-in-process; both-in-process.
- **D-08:** The E2E proves the **real embedded hash** (SC#3): extract `Processor.Sample`'s **actual built** SourceHash (reflect built assembly / read build artifact) and register THAT value as the Processor DB row via CRUD — must also satisfy the DB `^[a-f0-9]{64}$` validator. Hardcoded/known hash rejected as not proving the embed.
- **D-09:** New E2E lives alongside `CorrelationPropagationE2ETests` in `BaseApi.Tests` (Orchestrator/Processor E2E area), reusing host-stack helper conventions.

**Close Gate (TEST-02)**
- **D-10:** Gate unchanged in discipline (locked since Phase 3 D-15/D-18): 3-consecutive-GREEN + triple-SHA (`psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues`) BEFORE=AFTER. Scan-clean teardown **extended** to cover new processor-liveness (`skp:{id:D}`) and execution-data (`L2ProjectionKeys.ExecutionData` = `skp:data:{entryId:D}`) keys.

### Claude's Discretion
- Exact `.targets` file name and the RoslynCodeTaskFactory task internals (algorithm is locked; implementation form is open).
- The dummy result's concrete payload shape (any single deterministic value satisfying the outcome-only result contract).
- E2E helper/fixture structure and container readiness-wait mechanics, consistent with existing host-stack E2E helpers.

### Deferred Ideas (OUT OF SCOPE)
- Real (non-dummy) transform logic (future milestone, REQUIREMENTS non-goal).
- A second concrete `Processor.<Purpose>` (only `Processor.Sample` ships).
- NuGet packaging of `BaseProcessor.Core` / `BaseApi.Core` (single-repo ProjectReference model retained).
- Live schema-validation hop in the E2E (Sample runs schema-less per D-05).
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| IDENT-01 | MSBuild target (`BeforeTargets=CoreCompile`) computes SourceHash — SHA-256, lowercase 64-hex, LF-normalized, per-file folded over ordinal path sort of `BaseProcessor.Core` + concrete `.cs`, excluding generated/`BaseConsole.Core`/`Messaging.Contracts`. | Pattern 1 (RoslynCodeTaskFactory inline task) + Pattern 2 (file-glob selection) + Code Examples §1/§2. Algorithm reproducibility analysis in Pitfall 1. |
| IDENT-02 | Hash embedded as `[assembly: AssemblyMetadata("SourceHash", …)]`; re-runs on implementation-source change (no stale hash on incremental builds). | Pattern 3 (`<AssemblyAttribute>` item → SDK `GenerateAssemblyInfo`) + Pattern 4 (Inputs/Outputs incremental stamp). Reader seam verified in `AssemblyMetadataSourceHashProvider.cs`. |
| SAMPLE-01 | `Processor.Sample` first concrete console (family `Processor.<Purpose>`), implements `ProcessAsync` with minimal dummy result list, no infra/id/L2/bus code. | Pattern 5 (thin Program.cs mirroring `Orchestrator/Program.cs`) + `AddBaseProcessor` already wires everything (verified `BaseProcessorServiceCollectionExtensions.cs`). Concrete overrides only `ProcessAsync` (verified `BaseProcessor.cs`). |
| SAMPLE-02 | Multistage Dockerfile + joins compose stack (mirror Orchestrator tier); `ComposeYamlFacts` extended. | Code Examples §3 (Dockerfile, mirror `src/Orchestrator/Dockerfile`) + §4 (compose service, mirror `orchestrator` block) + §5 (ComposeYamlFacts assertions). |
| TEST-01 | Real-stack E2E proves live round-trip (dispatch consumed → output to L2 → orchestrator advances on `ExecutionResult`) + liveness-gated Start. | Architecture Patterns + Code Examples §6 (E2E harness mirroring `CorrelationPropagationE2ETests`) + the round-trip seam map. Liveness gate lives in WebApi Start path (verified Phase 22). |
| TEST-02 | Close gate: 3-GREEN + triple-SHA BEFORE=AFTER, scan-clean teardown covering new liveness + execution-data keys. | Code Examples §7 (phase-28-close.ps1, copy of phase-22-close.ps1) + teardown extension analysis (Pitfall 4). |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| SourceHash computation | Build tooling (MSBuild target in `BaseProcessor.Core` `.targets`) | — | Identity is a *build-time* fact about the source; must be reproducible at build AND at test reflection-read time. Not a runtime concern. |
| SourceHash embed | Build tooling → concrete entry assembly (`Processor.Sample`) | — | D-03: reader uses `GetEntryAssembly()`. The attribute must land on the concrete `.dll`, not the library. |
| SourceHash read | Processor runtime (`AssemblyMetadataSourceHashProvider`, already built) | — | Phase 26 deliverable; Phase 28 only *produces* what it reads. |
| `ProcessAsync` transform | Concrete console (`Processor.Sample`) | — | BPC-02: the sole seam a concrete owns. Everything else inherited from `BaseProcessor.Core`. |
| Identity/liveness/dispatch/result | `BaseProcessor.Core` (inherited, unchanged) | — | All wired by `AddBaseProcessor`; the concrete supplies none of it. |
| Processor DB row registration | WebApi CRUD (`POST /api/v1/processors`) | — | D-08: the E2E seeds via the existing CRUD surface; `^[a-f0-9]{64}$` validator gates it. |
| Liveness-gated Start admission | WebApi Start path (`ProcessorLivenessValidator`, Phase 22, unchanged) | Redis L2 | The gate reads `skp:{procId}` freshness; only a real heartbeating processor makes it pass (D-07). |
| Round-trip orchestration | Orchestrator container (unchanged) + Processor.Sample container | RabbitMQ + Redis L2 | Both real containers; the in-process WebApi only kicks off Start. |
| Close gate | CI/operator script (`scripts/phase-28-close.ps1`) | Docker exec into containers | Triple-SHA invariant over postgres/redis/rabbitmq; mirrors phase-22. |

## Standard Stack

This phase introduces **no new NuGet packages**. Everything is in-repo or SDK-native.

### Core
| Tool/Library | Version | Purpose | Why Standard |
|--------------|---------|---------|--------------|
| MSBuild `RoslynCodeTaskFactory` | SDK-native (.NET 8 SDK) | Inline C# build task to compute SHA-256 fold | D-02 locked; ships with the .NET SDK, no dependency. [VERIFIED: dotnet build SK_P.sln uses net8.0 SDK per global.json + Directory.Build.props] |
| SDK `GenerateAssemblyInfo` + `AssemblyAttribute` item | SDK-native | Emits `[assembly: AssemblyMetadata]` from an MSBuild item | Standard SDK mechanism; the SDK auto-generates assembly attributes from `@(AssemblyAttribute)` items during `CoreCompile`. [CITED: learn.microsoft.com/dotnet — GenerateAssemblyInfo] |
| `System.Security.Cryptography.SHA256` | net standard 2.0 surface | Hashing inside the inline task | Available in the netstandard2.0 surface RoslynCodeTaskFactory compiles against. [VERIFIED: RoslynCodeTaskFactory compiles against netstandard2.0 — search result] |
| MassTransit 8.5.5 | CPM-pinned | Bus (inherited via `BaseProcessor.Core`) | Already pinned; `Processor.Sample.csproj` adds `MassTransit.RabbitMQ` like `Orchestrator.csproj`. [VERIFIED: Orchestrator.csproj lines 35-36] |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `MassTransit.RabbitMQ` | CPM-pinned | RabbitMQ transport for the concrete | `Processor.Sample.csproj` needs it the same way `Orchestrator.csproj` declares it (the transport package isn't transitively in `BaseProcessor.Core`). [VERIFIED: BaseProcessor.Core.csproj declares MassTransit but NOT MassTransit.RabbitMQ; Orchestrator.csproj declares MassTransit.RabbitMQ] |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| RoslynCodeTaskFactory inline task | A compiled MSBuild task assembly / external script | Rejected by D-02 (no compiled tool, no script). Inline task is recompiled every build (perf note) but the source set is tiny. |
| `<AssemblyAttribute>` item → GenerateAssemblyInfo | Hand-writing a generated `.cs` with `[assembly: AssemblyMetadata]` and adding it to `@(Compile)` | Both work; the `<AssemblyAttribute>` item is the SDK-blessed path and avoids managing a generated-file lifecycle. The generated-`.cs` route risks the file being re-hashed (it sits under the project) unless excluded. |

**Installation:** None. Add `MassTransit.RabbitMQ` PackageReference to the new `Processor.Sample.csproj` (no `Version=` — CPM-pinned).

**Version verification:** No registry lookup needed — phase adds zero new external packages. The SDK toolchain is `net8.0` (pinned in `Directory.Build.props` line 29; `global.json` present per Dockerfile COPY).

## Architecture Patterns

### System Architecture Diagram

```
                          BUILD TIME (Processor.Sample.csproj)
  ┌──────────────────────────────────────────────────────────────────────────┐
  │  Processor.Sample.csproj  ── <Import> ──►  SourceHash.targets             │
  │                                            (in src/BaseProcessor.Core/)    │
  │                                                                            │
  │   BeforeTargets=CoreCompile, Inputs=@(ImplFiles), Outputs=$(StampFile)    │
  │        │                                                                   │
  │        ▼                                                                   │
  │   [ItemGroup] ImplFiles = ordinal-sorted .cs from                         │
  │        BaseProcessor.Core + Processor.Sample                              │
  │        (EXCL: obj/**, *.g.cs, GlobalUsings, AssemblyInfo,                  │
  │               BaseConsole.Core, Messaging.Contracts)                       │
  │        │                                                                   │
  │        ▼                                                                   │
  │   <ComputeSourceHash Files="@(ImplFiles)" Output="Hash"/>  ◄── inline task │
  │        │   (LF-normalize each file → SHA-256 per file → fold → SHA-256)    │
  │        ▼                                                                   │
  │   <AssemblyAttribute Include="...AssemblyMetadataAttribute"                │
  │        _Parameter1="SourceHash" _Parameter2="$(Hash)"/>                    │
  │        │                                                                   │
  │        ▼  (SDK GenerateAssemblyInfo, during CoreCompile)                   │
  │   Processor.Sample.dll  ::  [assembly: AssemblyMetadata("SourceHash",hex)] │
  └──────────────────────────────────────────────────────────────────────────┘

                          RUN TIME (real containers in compose stack)
  ┌──────────────┐  POST /start   ┌─────────────────┐  StartOrchestration  ┌──────────────┐
  │ E2E in-proc  │ ─────────────► │ WebApi          │ ──── publish ──────► │ Orchestrator │
  │ WebApi       │                │ (in-proc, host- │                      │ CONTAINER    │
  │ (host stack) │                │  stack pointed) │                      │              │
  └──────────────┘                │  Start path:    │                      │  hydrate +   │
        │ seeds                   │  ProcessorLive- │◄── reads skp:{proc}──►│  schedule +  │
        │ Processor DB row        │  nessValidator  │     (Redis L2)        │  DISPATCH    │
        │ (real hash, CRUD)       │  gate (Ph22)    │                      └──────┬───────┘
        ▼                         └─────────────────┘                             │ Send
  ┌──────────────┐                                                                │ queue:{id:D}
  │ Postgres     │                            ┌───────────────────────────────────▼──────────┐
  │ (host 5433)  │                            │ Processor.Sample CONTAINER                     │
  └──────────────┘                            │  ProcessorStartupOrchestrator:                 │
        ▲                                      │   identity-by-SourceHash (reads embedded hash) │
        │ identity query (bus)                 │   → bind queue:{id:D} → MarkHealthy            │
        └──────────────────────────────────────│  Liveness heartbeat → skp:{id:D} (Redis L2)   │
                                                │  EntryStepDispatchConsumer:                    │
  ┌──────────────┐  output write skp:data:{e}  │   read L2 input → ProcessAsync (DUMMY) →       │
  │ Redis L2     │◄────────────────────────────│   write output L2 → Send ExecutionResult       │
  │ (host 6380)  │                             └───────────────────┬────────────────────────────┘
  └──────────────┘                                                 │ Send queue:orchestrator-result
        ▲                                      ┌───────────────────▼──────────┐
        │ orchestrator advances on result      │ Orchestrator ResultConsumer  │
        └──────────────────────────────────────│  step advancement (L1)       │
                                                └──────────────────────────────┘
   E2E asserts: ExecutionResult consumed + orchestrator advance log in ES (proven via otel→ES,
   the CorrelationPropagationE2ETests precedent).
```

### Recommended Project Structure
```
src/
├── BaseProcessor.Core/
│   ├── SourceHash.targets          # NEW (D-01) — inline task + 2 targets, imported by concretes
│   └── ... (unchanged Phase 26/27 code)
├── Processor.Sample/               # NEW project (SAMPLE-01)
│   ├── Processor.Sample.csproj     # OutputType=Exe; <Import> SourceHash.targets; refs BaseProcessor.Core
│   ├── Program.cs                  # thin host (mirror Orchestrator/Program.cs)
│   ├── SampleProcessor.cs          # the ONE concrete: override ProcessAsync → 1 dummy result
│   ├── appsettings.json            # Service/RabbitMq/Redis/ConsoleHealth/Processor section
│   └── Dockerfile                  # multistage (mirror src/Orchestrator/Dockerfile)
tests/BaseApi.Tests/
├── Orchestrator/
│   └── SampleRoundTripE2ETests.cs  # NEW (D-09) — mirror CorrelationPropagationE2ETests
├── Composition/
│   └── ComposeYamlFacts.cs         # EXTEND — assert processor-sample service block
scripts/
└── phase-28-close.ps1              # NEW — copy of phase-22-close.ps1 + processor-sample in services list
compose.yaml                        # EXTEND — processor-sample service alongside orchestrator
SK_P.sln                            # ADD Processor.Sample project
```

### Pattern 1: RoslynCodeTaskFactory inline task in a `.targets`
**What:** A `<UsingTask TaskFactory="RoslynCodeTaskFactory">` declaring a C# class task. `Code Type="Class"` infers the `ParameterGroup` from the source (no separate ParameterGroup needed).
**When to use:** D-02 — the hash computation. Author it in `src/BaseProcessor.Core/SourceHash.targets`.
**Key constraint [VERIFIED]:** RoslynCodeTaskFactory compiles against **netstandard2.0**, so the task body may only use netstandard2.0 APIs (`SHA256.Create()`, `File.ReadAllText`, `string.Replace`, `Array.Sort` with `StringComparer.Ordinal` — all available). It is **recompiled every build** (perf note; the impl source set is small so this is negligible).
```xml
<!-- Source: learn.microsoft.com/visualstudio/msbuild/msbuild-roslyncodetaskfactory -->
<UsingTask TaskName="ComputeSourceHash" TaskFactory="RoslynCodeTaskFactory"
           AssemblyFile="$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll">
  <ParameterGroup>
    <Files ParameterType="Microsoft.Build.Framework.ITaskItem[]" Required="true" />
    <Hash ParameterType="System.String" Output="true" />
  </ParameterGroup>
  <Task>
    <Code Type="Class" Language="cs"><![CDATA[ /* see Code Examples §1 */ ]]></Code>
  </Task>
</UsingTask>
```

### Pattern 2: Implementation-file selection by ordinal path sort
**What:** Build the `@(ImplFiles)` item set: all `.cs` from `BaseProcessor.Core` + the concrete, excluding generated files / out-of-scope projects.
**When to use:** IDENT-01 file scope. The hash MUST be reproducible — both at build time and when the E2E reflects the built assembly (the E2E does NOT recompute; it *reads* the embedded value — D-08).
**Critical:** exclude `**/obj/**`, `**/bin/**`, `*.g.cs`, `*GlobalUsings*`, `*AssemblyInfo*`, and the generated SourceHash file itself. Sort with `StringComparer.Ordinal` on a **normalized relative path** (use `/` separators, not `\`, so the hash is OS-independent between a Windows dev build and a Linux Docker build — see Pitfall 1).
```xml
<ItemGroup>
  <ImplFiles Include="$(MSBuildThisFileDirectory)**\*.cs;$(MSBuildProjectDirectory)\**\*.cs"
             Exclude="$(MSBuildThisFileDirectory)obj\**\*.cs;$(MSBuildProjectDirectory)\obj\**\*.cs;
                      **\*.g.cs;**\*GlobalUsings*.cs;**\*AssemblyInfo*.cs" />
</ItemGroup>
```
> NOTE: `$(MSBuildThisFileDirectory)` resolves to `src/BaseProcessor.Core/` (where the `.targets` lives) — this is how the target reaches the base library's sources from inside the concrete's build. Do NOT glob `BaseConsole.Core` or `Messaging.Contracts` (they are siblings, not under either directory — exclusion is automatic, but assert it in a test).

### Pattern 3: Emit `[assembly: AssemblyMetadata]` via the `<AssemblyAttribute>` item
**What:** Add an MSBuild item that the SDK's `GenerateAssemblyInfo` turns into the attribute during `CoreCompile`.
**When to use:** IDENT-02 / D-03 — onto the concrete entry assembly.
```xml
<!-- Source: SDK GenerateAssemblyInfo mechanism (CITED: learn.microsoft.com/dotnet) -->
<ItemGroup>
  <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
    <_Parameter1>SourceHash</_Parameter1>
    <_Parameter2>$(SourceHash)</_Parameter2>
  </AssemblyAttribute>
</ItemGroup>
```
This must run AFTER the hash target sets `$(SourceHash)` and BEFORE `CoreCompile` (the attribute-item generation happens in `GetAssemblyAttributes`/`CoreGenerateAssemblyInfo`, which runs in the compile pipeline). Sequencing: the compute target is `BeforeTargets="CoreCompile"` and writes the property; the `<AssemblyAttribute>` item must be added in the SAME `BeforeTargets="CoreCompile"` target (item added in a target IS visible to a later target in the same build). Verify the attribute actually lands by reflecting the built `.dll` in a Wave-0 test.

### Pattern 4: Incremental re-hash (no stale hash) via Inputs/Outputs
**What:** Make the compute target incremental on the implementation file set so an unchanged build skips, but any `.cs` edit re-runs it. IDENT-02 explicitly requires "re-runs on implementation-source change (no stale hash on incremental builds)."
**When to use:** Always — without this, a `dotnet build` after editing `BaseProcessor.Core` could keep the old embedded hash.
```xml
<Target Name="ComputeAndEmitSourceHash" BeforeTargets="CoreCompile"
        Inputs="@(ImplFiles)" Outputs="$(IntermediateOutputPath)sourcehash.stamp">
  <ComputeSourceHash Files="@(ImplFiles)"><Output PropertyName="SourceHash" TaskParameter="Hash"/></ComputeSourceHash>
  <ItemGroup>
    <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
      <_Parameter1>SourceHash</_Parameter1><_Parameter2>$(SourceHash)</_Parameter2>
    </AssemblyAttribute>
  </ItemGroup>
  <WriteLinesToFile File="$(IntermediateOutputPath)sourcehash.stamp" Lines="$(SourceHash)" Overwrite="true"/>
</Target>
```
> WARNING (Pitfall 2): MSBuild Inputs/Outputs is timestamp-based. The stamp gates the *target run*, but the `<AssemblyAttribute>` item must be present for `CoreCompile` even on an incremental no-change build. If the target is skipped (Outputs newer than Inputs), the item is NOT re-added → the SDK may regenerate AssemblyInfo without it. **Recommended:** split into two targets — a cheap always-run target that READS the stamp file's hash into `$(SourceHash)` and adds the `<AssemblyAttribute>` item (unconditional, `BeforeTargets="CoreCompile"`), and the incremental compute target that WRITES the stamp. This guarantees the attribute is always emitted with the current hash while only recomputing when sources change. Validate both paths in Wave 0.

### Pattern 5: Thin concrete console (mirror `Orchestrator/Program.cs`)
**What:** `Processor.Sample/Program.cs` is a Generic-Host shell — `Host.CreateApplicationBuilder`, `AddBaseConsoleObservability`, `AddBaseProcessor`, register the one `BaseProcessor` subclass, `RunAsync`.
**When to use:** SAMPLE-01. `AddBaseProcessor` already wires identity/liveness/dispatch/heartbeat (verified `BaseProcessorServiceCollectionExtensions.cs`). The concrete adds ONLY `services.AddSingleton<BaseProcessor, SampleProcessor>()` (the consumer resolves `BaseProcessor` — verified `EntryStepDispatchConsumer` ctor takes `BaseProcessor processor`).
```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.AddBaseConsoleObservability(builder.Configuration);     // metrics-only OTel
builder.Services.AddBaseProcessor(builder.Configuration);        // identity+liveness+dispatch+heartbeat
builder.Services.AddSingleton<BaseProcessor, SampleProcessor>(); // the ONE concrete seam
var host = builder.Build();
await host.RunAsync();
```
> NOTE: `AddBaseProcessor` calls `AddBaseConsole` + `AddBaseConsoleMessaging` internally (verified lines 53-74) and registers `MassTransit.RabbitMQ` transitively only if `BaseConsole.Core` does. The `Orchestrator.csproj` explicitly declares `MassTransit.RabbitMQ`, so `Processor.Sample.csproj` should too (defensive — confirm at Wave 0 whether `BaseConsole.Core` already brings it).

### Anti-Patterns to Avoid
- **Emitting the attribute onto `BaseProcessor.Core`:** D-03 — the reader uses `GetEntryAssembly()`. The library is never the entry assembly. The attribute MUST be on `Processor.Sample.dll`.
- **`Directory.Build.targets` under `src/Processor.*/`:** Rejected by D-01 (implicit-inheritance risk).
- **Re-seeding the processor liveness key in the E2E:** The existing `CorrelationPropagationE2ETests.SeedHostProcessorLiveAsync` writes a SYNTHETIC liveness key. In Phase 28 the REAL `Processor.Sample` container writes the genuine `skp:{id:D}` "Healthy" heartbeat — the E2E must NOT seed it (that would mask whether the live gate actually works). This is the core difference from the Phase 22 test (see Pitfall 3).
- **Recomputing the hash in the E2E:** D-08 — the test READS the embedded value (reflect `Processor.Sample.dll`), it does NOT recompute. Recomputing would re-implement the algorithm in test code and prove nothing about the embed.
- **`FLUSHDB` in teardown:** Forbidden by close-gate discipline (D-10). Use targeted `KeyDelete`/`SetRemove` (verified `CorrelationPropagationE2ETests` teardown pattern).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Reflecting the embedded hash | Custom IL parsing / assembly metadata reader | `typeof(SampleProcessor).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key=="SourceHash").Value` — OR reuse `AssemblyMetadataSourceHashProvider` against the loaded `Processor.Sample` assembly | The reader seam already exists; the test reflects the same way the runtime does (verified `AssemblyMetadataSourceHashProvider.cs:26-28`). |
| Emitting the assembly attribute | Hand-written generated `.cs` added to `@(Compile)` | SDK `<AssemblyAttribute>` item + GenerateAssemblyInfo | SDK-blessed; avoids a generated file that would itself be hashed. |
| Host wiring / identity / liveness / dispatch | Any infra in `Processor.Sample` | `AddBaseProcessor` | BPC-02/BPC-03 — the concrete carries no infra (verified composition root). |
| Close-gate triple-SHA orchestration | New gate script from scratch | Copy `scripts/phase-22-close.ps1`, add `processor-sample` to `$services` | Proven gate; only the services list + phase label change. |
| E2E host-stack wiring | New WebApplicationFactory | `RealStackWebAppFactory` pattern (env-var-in-ctor host overrides) | Verified working in `CorrelationPropagationE2ETests` — reuse the env-var ctor override + teardown discipline. |

**Key insight:** Phase 28 is almost entirely *mirroring* existing proven assets. The only net-new logic is ~30 lines of C# inside one MSBuild inline task. Resist building anything else.

## Runtime State Inventory

> This phase ADDS new runtime state (a new container, new compose service, new Redis key families exercised live) rather than renaming existing state. The relevant audit is "what new state must teardown cover so the close-gate SHA holds."

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | Postgres: a new `ProcessorEntity` row registered by the E2E (real hash, null schemas). | E2E must delete the row (or use the base factory's Postgres teardown — the Start-path test seeds via CRUD against host Postgres; verify `psql \l` SHA is database-list level, NOT row level — see below). |
| Live service config | New `processor-sample` compose service (image built from new Dockerfile); joins the host stack alongside `sk-orchestrator`. | compose.yaml edit + `ComposeYamlFacts` guard; gate's `$services` list extended (D-06/D-10). |
| OS-registered state | None — no Task Scheduler / pm2 / systemd. Verified: only `compose.yaml` + `scripts/*.ps1` orchestration in repo. | None. |
| Secrets/env vars | RabbitMq guest/guest + Redis/otel endpoints (dev-only, already in compose). `Processor.Sample` container env mirrors `orchestrator` block (RabbitMq__Host, ConnectionStrings__Redis, OTEL_EXPORTER_OTLP_ENDPOINT, Processor__* knobs). | Add env block to the new compose service (mirror orchestrator). |
| Build artifacts | `Processor.Sample.dll` carrying the embedded `[assembly: AssemblyMetadata]`; the `obj/sourcehash.stamp` incremental marker. | The E2E reflects the built `.dll`; the container build runs the same target inside Docker (Linux). Cross-OS hash reproducibility is a real risk — Pitfall 1. |

**The canonical concern:** after the E2E runs, the triple-SHA close gate (`psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues`) must return to its BEFORE state.
- `psql \l` lists **databases**, not rows — a new Processor *row* does not change the database list, so the Postgres SHA is unaffected by the E2E's CRUD row (verified `phase-22-close.ps1:50` uses `psql -U postgres -lqt`, the database list). The row still SHOULD be cleaned for hygiene but does not break the gate.
- `redis-cli --scan` enumerates **all keys** — the new `skp:{id:D}` liveness key (written by the live `Processor.Sample` container, sliding TTL) and `skp:data:{entryId:D}` execution-data keys (written by the round-trip, TTL 300s default) WILL appear. The TTL means they eventually expire, but the gate captures BEFORE/AFTER around the test run; the test teardown must explicitly delete the execution-data keys it created and the gate must run after the liveness key's TTL lapses OR the live container is the steady-state (BEFORE already includes its key). **This is the load-bearing teardown extension (D-10) — see Pitfall 4.**
- `rabbitmqctl list_queues` enumerates queue names — the durable `{id:D}` dispatch queue is created by the live `Processor.Sample` container at bind. If the container is part of the steady-state stack (running for BOTH the BEFORE and AFTER snapshot), its queue is in both → SHA holds. If the test spins the container up/down, the queue name changes per processor id → SHA breaks. **Recommendation:** the `processor-sample` container runs as a steady-state compose service (like `orchestrator`), and its processor id is STABLE across the gate run (the DB row + embedded hash are fixed), so its `{id:D}` queue and `skp:{id:D}` liveness key are in both BEFORE and AFTER. See Open Question 1.

## Common Pitfalls

### Pitfall 1: Cross-OS hash non-reproducibility (Windows dev build vs Linux Docker build)
**What goes wrong:** The SourceHash computed on the Windows dev machine (where the E2E reflects `Processor.Sample.dll`) differs from the hash embedded in the container's `Processor.Sample.dll` (built on `linux/amd64` inside Docker). The E2E registers the dev-machine hash; the container resolves identity by its own (different) hash → identity never resolves → liveness never written → Start gate fails.
**Why it happens:** (a) path separators differ (`\` vs `/`) if the ordinal sort or any per-file path content uses native separators; (b) line endings — git `core.autocrlf` may check out CRLF on Windows but the Docker build context copies whatever is on disk; the LF-normalization (IDENT-01) defends against this ONLY if applied to file *content*, but if file *order* or *path strings* leak into the fold, divergence returns; (c) which files are globbed (a Windows build might include an `obj/` file a Linux clean build doesn't).
**How to avoid:** (1) Normalize every path to forward-slash + ordinal-sort on the normalized relative path. (2) LF-normalize file CONTENT (`text.Replace("\r\n","\n").Replace("\r","\n")`) before hashing — this is the IDENT-01 mandate; apply it rigorously. (3) Fold only file CONTENT hashes, never path strings, into the final hash (so path-separator differences can't leak) — OR if path is part of the identity, use the normalized relative path consistently. (4) Glob deterministically with explicit excludes (Pattern 2). (5) **Wave-0 verification:** build `Processor.Sample` locally AND in Docker, extract both embedded hashes, assert equal. This is the single highest-risk item in the phase.
**Warning signs:** E2E hangs on Start (liveness gate 422) even though the container is "Up"; container logs show "Processor row not yet registered for hash {Hash}" looping forever (verified `ProcessorStartupOrchestrator.cs:87` logs this).

### Pitfall 2: Stale hash on incremental build (the `<AssemblyAttribute>` item dropped on skip)
**What goes wrong:** An Inputs/Outputs-gated compute target skips on a no-source-change build, so the `<AssemblyAttribute>` item is never added → SDK regenerates AssemblyInfo WITHOUT the SourceHash attribute → reader throws `InvalidOperationException` ("Assembly metadata 'SourceHash' is missing" — verified `AssemblyMetadataSourceHashProvider.cs:30-32`).
**Why it happens:** Conflating "recompute the hash" (expensive, should be incremental) with "emit the attribute" (cheap, must be unconditional). MSBuild Inputs/Outputs skips the WHOLE target including the item add.
**How to avoid:** Pattern 4's two-target split — always-run emit target (reads stamp → adds item) + incremental compute target (writes stamp). Verify with: clean build → edit one impl `.cs` → incremental build → reflect `.dll` (hash changed); then incremental build with NO change → reflect `.dll` (attribute still present, hash unchanged).
**Warning signs:** Reader throws on second build; `dotnet build` clean works but `dotnet build` (no clean) fails the runtime.

### Pitfall 3: E2E masking the live liveness gate by re-seeding the processor key
**What goes wrong:** Copying `CorrelationPropagationE2ETests` verbatim includes `SeedHostProcessorLiveAsync` which writes a synthetic `skp:{procId}` "Live" key. With that present, the Start gate passes regardless of whether the REAL `Processor.Sample` container is heartbeating — defeating SC#4's whole point (D-07: "only a real heartbeating container makes the Start gate pass truthfully").
**Why it happens:** The Phase 22 test had NO real processor container, so it HAD to seed. Phase 28 HAS one — the seed must be removed.
**How to avoid:** In `SampleRoundTripE2ETests`, do NOT seed the liveness key. Instead, register the Processor DB row with the REAL embedded hash (D-08), then WAIT for the `Processor.Sample` container to resolve identity + bind + MarkHealthy + write `skp:{id:D}` (poll Redis for the key's presence/freshness) BEFORE POSTing Start. The processor id in the DB row MUST equal the id the container resolves — which it will, because the container queries `GetProcessorBySourceHash(<embedded hash>)` and the DB row carries that same hash (the registration closes the identity loop). See Open Question 2 for the id-coordination sequence.
**Warning signs:** Test passes even when `processor-sample` container is stopped (false green).

### Pitfall 4: Close-gate scan-clean teardown not covering the new key families
**What goes wrong:** `redis-cli --scan` SHA BEFORE != AFTER because the round-trip left `skp:data:{entryId:D}` execution-data keys (written by `EntryStepDispatchConsumer`, verified line 144-147, TTL = ExecutionDataTtlSeconds default 300) that don't expire within the gate window.
**Why it happens:** D-10 explicitly calls this out: teardown must be EXTENDED to cover liveness (`skp:{id:D}`) + execution-data (`skp:data:{entryId:D}`) keys.
**How to avoid:** The E2E test (which controls the round-trip) cannot predict the minted `newEntryId` for the output (it's `NewId.NextGuid()` server-side — verified line 140). Options: (a) set `Processor__ExecutionDataTtl` very short (e.g. 2-3s) in the `processor-sample` container so execution-data keys self-expire before the AFTER snapshot, and have the gate sleep > TTL before snapshotting; (b) have the E2E SCAN `skp:data:*` after the run and delete the new keys it didn't have before; (c) snapshot AFTER with a settle delay. The liveness key `skp:{id:D}` is steady-state (the container keeps refreshing it) so it's in BOTH BEFORE and AFTER if the container is up for both → no action needed for liveness IF the container is steady-state. **Recommend (a)+(b):** short execution-data TTL in the sample container + explicit `skp:data:*` cleanup of the run's keys. Document the chosen approach as a decision for the planner.
**Warning signs:** Gate exit 1 with "redis-cli --scan SHA-256 BEFORE != AFTER" + a leaked `skp:data:...` key.

### Pitfall 5: `MassTransit.RabbitMQ` not referenced by the concrete
**What goes wrong:** `Processor.Sample` builds but cannot connect to RabbitMQ at runtime (the transport package is missing).
**Why it happens:** `BaseProcessor.Core.csproj` declares `MassTransit` but NOT `MassTransit.RabbitMQ` (verified lines 35-37). The transport is a separate package. `Orchestrator.csproj` declares it explicitly (verified line 36).
**How to avoid:** Add `<PackageReference Include="MassTransit.RabbitMQ" />` (no Version — CPM) to `Processor.Sample.csproj`. Confirm at Wave 0 whether `BaseConsole.Core` already brings it transitively (if so this is belt-and-braces; the Orchestrator pattern declares it anyway).
**Warning signs:** Runtime bus-start failure / no RabbitMQ connection; the orchestrator pattern declares it, so mirror that.

### Pitfall 6: `appsettings.json` not copied to output (worker SDK)
**What goes wrong:** `Processor.Sample` boots with no config (no Service name, no RabbitMq host, no Processor knobs).
**Why it happens:** `Microsoft.NET.Sdk` (worker) does NOT copy `appsettings.json` by default — verified `Orchestrator.csproj:45-50` explicitly `<Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />`.
**How to avoid:** Mirror the Orchestrator csproj's `<Content Include="appsettings.json">`.
**Warning signs:** Missing config at runtime; container can't find broker.

## Code Examples

### §1 — The inline task body (netstandard2.0 surface only)
```csharp
// Source: pattern per learn.microsoft.com/visualstudio/msbuild/msbuild-roslyncodetaskfactory
// Code Type="Class" — ParameterGroup inferred. netstandard2.0 APIs ONLY.
public class ComputeSourceHash : Microsoft.Build.Utilities.Task
{
    [Microsoft.Build.Framework.Required]
    public Microsoft.Build.Framework.ITaskItem[] Files { get; set; }
    [Microsoft.Build.Framework.Output]
    public string Hash { get; set; }

    public override bool Execute()
    {
        // Ordinal sort on a normalized (forward-slash) full path for OS-independence (Pitfall 1).
        var paths = new System.Collections.Generic.List<string>();
        foreach (var f in Files) paths.Add(f.GetMetadata("FullPath").Replace('\\','/'));
        paths.Sort(System.StringComparer.Ordinal);

        using (var outer = System.Security.Cryptography.SHA256.Create())
        using (var acc = new System.IO.MemoryStream())
        {
            foreach (var p in paths)
            {
                var text = System.IO.File.ReadAllText(p);              // UTF-8 default
                text = text.Replace("\r\n", "\n").Replace("\r", "\n"); // LF-normalize CONTENT (IDENT-01)
                using (var per = System.Security.Cryptography.SHA256.Create())
                {
                    var h = per.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
                    acc.Write(h, 0, h.Length);                          // fold per-file hashes, in sorted order
                }
            }
            acc.Position = 0;
            var final = outer.ComputeHash(acc);
            var sb = new System.Text.StringBuilder(64);
            foreach (var b in final) sb.Append(b.ToString("x2"));       // lowercase 64-hex (IDENT-01)
            Hash = sb.ToString();
        }
        return true;
    }
}
```
> The fold-over-per-file-hashes shape (hash each file → concatenate hashes → hash the concatenation) is ONE valid reading of "per-file hashes folded over an ordinal path sort." Confirm with the planner that this matches the intended algorithm; an alternative is to fold the raw normalized content directly. The reader does NOT care (it only reads the embedded string) — the only constraint is build-time == E2E-reflection-time == container-build-time reproducibility (Pitfall 1).

### §2 — `Processor.Sample.csproj` (mirror Orchestrator.csproj + import)
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <RootNamespace>Processor.Sample</RootNamespace>
    <AssemblyName>Processor.Sample</AssemblyName>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MassTransit" />
    <PackageReference Include="MassTransit.RabbitMQ" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\BaseProcessor.Core\BaseProcessor.Core.csproj" />
    <ProjectReference Include="..\Messaging.Contracts\Messaging.Contracts.csproj" />
  </ItemGroup>
  <!-- D-01: explicit import — ProjectReference does NOT auto-flow build/*.targets -->
  <Import Project="..\BaseProcessor.Core\SourceHash.targets" />
</Project>
```

### §3 — `src/Processor.Sample/Dockerfile` (mirror `src/Orchestrator/Dockerfile`)
```dockerfile
# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY ["Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
COPY ["src/Messaging.Contracts/Messaging.Contracts.csproj", "src/Messaging.Contracts/"]
COPY ["src/BaseConsole.Core/BaseConsole.Core.csproj", "src/BaseConsole.Core/"]
COPY ["src/BaseProcessor.Core/BaseProcessor.Core.csproj", "src/BaseProcessor.Core/"]
COPY ["src/Processor.Sample/Processor.Sample.csproj", "src/Processor.Sample/"]
RUN dotnet restore "src/Processor.Sample/Processor.Sample.csproj"
COPY src/ src/
RUN dotnet publish "src/Processor.Sample/Processor.Sample.csproj" -c Release -o /publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends wget && rm -rf /var/lib/apt/lists/*
COPY --from=build /publish .
USER app
ENV ASPNETCORE_URLS=http://+:8082
EXPOSE 8082
ENTRYPOINT ["dotnet", "Processor.Sample.dll"]
```
> Health port 8082 to avoid colliding with orchestrator 8081 / baseapi 8080 (set `ConsoleHealth:Port` to 8082 in appsettings). aspnet runtime base (NOT runtime base) because `BaseConsole.Core` carries the embedded Kestrel health listener (verified `Orchestrator/Dockerfile:7-8`). The SourceHash target runs inside `dotnet publish` here — Pitfall 1 (cross-OS reproducibility) is exercised by THIS build vs the dev build.

### §4 — compose `processor-sample` service (mirror `orchestrator` block)
```yaml
  processor-sample:
    build:
      context: .
      dockerfile: src/Processor.Sample/Dockerfile
    container_name: sk-processor-sample
    restart: unless-stopped
    depends_on:
      rabbitmq: { condition: service_healthy }
      redis: { condition: service_healthy }
      baseapi-service: { condition: service_healthy }   # NEW vs orchestrator: needs the WebApi responder for identity
    environment:
      RabbitMq__Host: rabbitmq
      RabbitMq__Username: guest
      RabbitMq__Password: guest
      ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
      Processor__ExecutionDataTtl: "5"   # short TTL so skp:data keys self-expire (Pitfall 4)
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:8082/health/ready"]
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 30s
```
> CRITICAL difference from orchestrator: `processor-sample` depends on `baseapi-service` healthy (it resolves identity over the bus FROM the WebApi responder — verified `ProcessorStartupOrchestrator` queries `GetProcessorBySourceHash`). Its `/health/ready` flips green only after identity resolves + queue binds + MarkHealthy (verified `ProcessorStartupOrchestrator.cs:156-157` — gate.MarkReady fires at Healthy). This means the container is "starting/unhealthy" until a Processor DB row with its hash exists — a chicken-and-egg the E2E resolves by registering the row first (Open Question 2).

### §5 — ComposeYamlFacts extension (mirror existing assertion style)
```csharp
[Fact]
public void ComposeYaml_Has_ProcessorSample_Service_Block()
{
    var content = ComposeYamlContent();
    Assert.Contains("container_name: sk-processor-sample", content);
    Assert.Matches(new Regex(@"dockerfile:\s*src/Processor\.Sample/Dockerfile"), content);
    Assert.Matches(new Regex(@"(?ms)processor-sample:[\s\S]*?baseapi-service:\s+condition:\s+service_healthy"), content);
}
```

### §6 — E2E shape (mirror `CorrelationPropagationE2ETests`, MINUS the liveness seed)
```csharp
// Source: pattern from tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs
[Trait("Category", "E2E")] [Trait("Category", "RealStack")] [Collection("Observability")]
public sealed class SampleRoundTripE2ETests
{
    [Fact]
    public async Task LiveSampleProcessor_RoundTrip_OrchestratorAdvances()
    {
        await using var factory = new RealStackWebAppFactory();   // reuse the existing host-stack factory
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // D-08: read the GENUINE embedded hash off the BUILT Processor.Sample assembly.
        var hash = typeof(Processor.Sample.SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;            // must match container's build (Pitfall 1)

        // Register the Processor DB row with that real hash, null schemas (D-05), via CRUD.
        var procId = await SeedProcessorAsync(client, hash /* null schemas */, ct);
        var stepId = await SeedStepAsync(client, procId, ct);
        var wfId   = await SeedWorkflowAsync(client, new() { stepId }, ct);

        // Pitfall 3: DO NOT seed liveness. Wait for the REAL container to write skp:{procId} Healthy.
        await PollForHealthyLiveness(procId, ct);                 // poll host Redis for the key (fresh)

        // Start passes ONLY because the live container's heartbeat is fresh (SC#4 truthful gate).
        var startResp = await client.PostAsJsonAsync("/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, startResp.StatusCode);

        // Assert the round-trip: orchestrator advances on the returned ExecutionResult.
        // Prove via the ES advance/seam log (otel→ES precedent) OR via L2 skp:data presence.
        // ... poll ES for the orchestrator step-advancement log, OR poll Redis for skp:data:{newEntryId}.

        // Pitfall 4: clean up skp:data:* keys this run minted; SREM parent-index; delete L2 root/step.
    }
}
```
> The assertion mechanism (ES log vs L2 key presence) is Claude's discretion (D-09). The ES-log route reuses the proven `ElasticsearchTestClient.PollEsForLog` + otel pipeline (verified `CorrelationPropagationE2ETests`); the L2 route polls `skp:data:{entryId}` but the entryId is server-minted (unknown to the test) so it must SCAN `skp:data:*`. Recommend the ES-log route for the "orchestrator advances" proof + an L2 SCAN to confirm output was written, mirroring SC#4's two clauses ("output written to L2" AND "orchestrator advances").

### §7 — `scripts/phase-28-close.ps1` (copy phase-22 + one services edit)
```powershell
# Copy scripts/phase-22-close.ps1 verbatim, change:
#   1. header label Phase 22 -> Phase 28
#   2. $services list: add 'processor-sample'
$services = @('postgres','redis','rabbitmq','otel-collector','elasticsearch','prometheus',
              'orchestrator','processor-sample','baseapi-service')
# Everything else (triple-SHA BEFORE/AFTER, 3-GREEN cadence, zero-warning Release+Debug build) unchanged.
```
> The teardown extension (Pitfall 4) lives in the TEST (key cleanup) + the `processor-sample` container's short ExecutionDataTtl, NOT in the gate script. The gate only snapshots; the test + container config ensure BEFORE==AFTER. If liveness/dispatch-queue steady-state holds (container up for both snapshots), no gate-script change beyond `$services` is needed.

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `CodeTaskFactory` (legacy, .NET Framework only) | `RoslynCodeTaskFactory` (cross-platform, netstandard2.0) | MSBuild 15.8+ | Use RoslynCodeTaskFactory — works in the Linux Docker build (D-02). [VERIFIED: search results] |
| Hand-written generated AssemblyInfo `.cs` | SDK `GenerateAssemblyInfo` + `@(AssemblyAttribute)` items | .NET SDK (Core era) | Default `GenerateAssemblyInfo=true` for SDK projects; the `<AssemblyAttribute>` item is the supported emit path. [CITED: learn.microsoft.com/dotnet] |

**Deprecated/outdated:**
- Phase 22's `SeedHostProcessorLiveAsync` synthetic liveness seed is NOT the model for this phase's E2E — the real container heartbeats (Pitfall 3). Reuse the FACTORY (`RealStackWebAppFactory`) but not the seed call.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The "fold-over per-file hashes" reading (hash each file → concat → hash) is the intended IDENT-01 algorithm. | Code Examples §1 | LOW for the embed loop (reader only reads the string), but the planner should confirm the exact fold shape so the algorithm is pinned in a unit test. The ONLY hard constraint is reproducibility across build environments. |
| A2 | `$(MSBuildThisFileDirectory)` (resolving to `src/BaseProcessor.Core/`) reaches the base library's `.cs` from inside the concrete's build. | Pattern 2 | MEDIUM — if the `.targets` import doesn't expose the base dir as expected, the glob may miss base files. Verify in Wave 0 by listing `@(ImplFiles)`. |
| A3 | The `<AssemblyAttribute>` item added inside a `BeforeTargets="CoreCompile"` target IS picked up by the SDK's AssemblyInfo generation. | Pattern 3/4 | MEDIUM — sequencing between custom target and `CoreGenerateAssemblyInfo` must be verified (reflect the built `.dll`). The two-target split (Pattern 4) mitigates. |
| A4 | Cross-OS (Windows dev vs Linux Docker) hash reproducibility is achievable with path-normalization + LF content normalization. | Pitfall 1 | HIGH — if not reproducible, the E2E's reflected hash != container's hash and identity never resolves. Mandatory Wave-0 dual-build verification. |
| A5 | `processor-sample` running as a steady-state compose service keeps its `skp:{id:D}` liveness key + `{id:D}` queue in BOTH the gate's BEFORE and AFTER snapshots (SHA holds). | Runtime State Inventory / Pitfall 4 | MEDIUM — depends on the processor id being stable across the gate run; confirm the DB row + hash are fixed (Open Question 1). |
| A6 | `psql -lqt` (database list) is unaffected by a new Processor row (row-level, not db-level). | Runtime State Inventory | LOW — verified the gate hashes the database LIST (`-lqt`), not table contents. |
| A7 | The E2E can prove "orchestrator advances" via the ES advance/seam log using the existing otel→ES pipeline. | Code Examples §6 | LOW-MEDIUM — the precedent (`CorrelationPropagationE2ETests`) proves seam logs reach ES; the specific advancement log message must exist (verify `ResultConsumer` log text). |
| A8 | `MassTransit.RabbitMQ` must be added to `Processor.Sample.csproj` (not transitively from `BaseConsole.Core`). | Pitfall 5 | LOW — `Orchestrator.csproj` declares it explicitly; mirroring is safe even if redundant. |

## Open Questions

1. **Steady-state vs test-spawned `processor-sample` container, and processor-id stability across the close gate.**
   - What we know: the gate snapshots `redis-cli --scan` + `rabbitmqctl list_queues` BEFORE and AFTER the 3 test runs. A live processor writes `skp:{id:D}` + binds `{id:D}` queue. The DB row carries a fixed embedded hash → fixed processor id (the WebApi assigns the id at row create; the container resolves THAT id by hash).
   - What's unclear: is the Processor DB row (and thus the id) created ONCE and persisted across the whole gate run (so the queue + liveness key are steady-state in both snapshots), or re-created per test run (id churns → queue name churns → SHA breaks)? The E2E currently seeds via CRUD per-test.
   - Recommendation: Register the Processor row with a STABLE id seeded BEFORE the gate's BEFORE snapshot (e.g. a fixture/setup step), so the live container's queue + liveness key are in both snapshots. OR set short TTLs + clean up so the keys/queues are gone by AFTER. Planner must decide; document as a Phase 28 decision. (Affects A5.)

2. **Chicken-and-egg: the container needs a DB row to go Healthy, but the E2E registers the row.**
   - What we know: `processor-sample` `/health/ready` flips green only at Healthy (identity resolved → row must exist). The E2E registers the row with the real hash. compose `depends_on` can't gate on a row that a test creates.
   - What's unclear: ordering — the container will boot, loop "row not yet registered" (verified `ProcessorStartupOrchestrator.cs:87`, UNBOUNDED retry, host never crashes), and only resolve once the E2E (or a one-time setup) registers the row. So the container can start "unhealthy" and become healthy AFTER the row is seeded.
   - Recommendation: this is actually fine — the unbounded boot-before-register retry (Phase 26 design, verified) tolerates it. The E2E seeds the row, then polls Redis for the liveness key (container went Healthy), THEN POSTs Start. The compose healthcheck `start_period: 30s` + retries should NOT mark the container failed/exited during the wait (the host stays up; only `/ready` is red). Confirm the gate's pre-flight health check (`phase-22-close.ps1:41`) tolerates `processor-sample` being unhealthy until a row exists — it currently aborts on non-healthy EXCEPT otel-collector (line 41). **The gate pre-flight may need `processor-sample` added to the health-exception list, OR a row must be pre-seeded so it's healthy at pre-flight.** This is a real gate-integration decision for the planner.

3. **Exact fold algorithm (ties to A1).** Confirm whether IDENT-01's "per-file hashes folded over an ordinal path sort" means hash-of-concatenated-per-file-hashes (§1) or hash-of-concatenated-normalized-content. Pin it in a unit test either way.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK + RoslynCodeTaskFactory | IDENT-01/02 build target | ✓ (assumed — repo builds net8.0) | net8.0 | None — SDK-native |
| Docker + compose | SAMPLE-02, TEST-01, close gate | ✓ (existing stack) | — | None |
| Host compose stack (postgres/redis/rabbitmq/otel/es/prometheus/orchestrator/baseapi) | TEST-01 / gate | ✓ (running per existing gates) | per compose.yaml | None |
| pwsh | close gate | ✓ (existing `scripts/*.ps1`) | — | None |

**Missing dependencies with no fallback:** None identified — the phase reuses the established toolchain.
**Note:** Wave 0 MUST verify the dual-build hash reproducibility (Windows SDK build vs Linux Docker build) — this is environment-sensitive (Pitfall 1 / A4) and is the single highest-risk verification.

## Validation Architecture

> nyquist_validation: `.planning/config.json` not separately confirmed here; treating as ENABLED (absent/true = enabled). The repo's discipline (3-GREEN + triple-SHA gate) is the de-facto validation regime.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (`[Fact]`, `[Trait]`, `[Collection]`) + NSubstitute + MassTransit.Testing in-memory harness |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "Category!=RealStack"` (hermetic) |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build` (RealStack live) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| IDENT-01 | Hash is SHA-256 lowercase 64-hex, LF-normalized, ordinal-sorted, correct file scope | unit + build | `dotnet build src/Processor.Sample` then reflect `.dll`; xUnit asserts `^[a-f0-9]{64}$` | ❌ Wave 0 |
| IDENT-01 | Excludes generated / BaseConsole.Core / Messaging.Contracts | unit | xUnit asserts a known file from those is NOT in `@(ImplFiles)` (dump item list via a build target) | ❌ Wave 0 |
| IDENT-02 | `[assembly: AssemblyMetadata("SourceHash",…)]` present on `Processor.Sample.dll` | unit (reflection) | xUnit reflects the built assembly, asserts attribute present + 64-hex | ❌ Wave 0 |
| IDENT-02 | Incremental: edit impl `.cs` → hash changes; no change → attribute still present | build | scripted clean/edit/incremental build + reflect (Pitfall 2) | ❌ Wave 0 |
| IDENT-02 | Cross-OS reproducibility (dev build == Docker build hash) | build/integration | dual-build script comparing embedded hashes (A4 — highest risk) | ❌ Wave 0 |
| SAMPLE-01 | `Processor.Sample` boots, `ProcessAsync` returns 1 deterministic dummy | unit | xUnit on `SampleProcessor.ProcessAsync` returns single fixed `ProcessResult` | ❌ Wave 0 |
| SAMPLE-01 | Concrete carries no infra (only overrides ProcessAsync) | static/unit | assert `SampleProcessor : BaseProcessor`, no other DI registrations | ❌ Wave 0 |
| SAMPLE-02 | compose has `processor-sample` service mirroring orchestrator | unit (file regex) | extend `ComposeYamlFacts` (§5) | ✓ extend `ComposeYamlFacts.cs` |
| TEST-01 | Live round-trip + liveness-gated Start | E2E (RealStack) | `SampleRoundTripE2ETests` (§6) | ❌ Wave 0 |
| TEST-02 | 3-GREEN + triple-SHA BEFORE=AFTER incl. new keys | gate script | `scripts/phase-28-close.ps1` (§7) | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** `dotnet test --filter "Category!=RealStack"` (hermetic unit + the MSBuild-reflection facts; sub-30s).
- **Per wave merge:** full hermetic suite + `dotnet build SK_P.sln -c Release` zero-warning.
- **Phase gate:** `scripts/phase-28-close.ps1` — 3-consecutive-GREEN full suite (RealStack live) + triple-SHA + zero-warning Release & Debug builds.

### Wave 0 Gaps
- [ ] `src/BaseProcessor.Core/SourceHash.targets` — the inline task + two targets (compute/emit) — IDENT-01/02
- [ ] `src/Processor.Sample/` project skeleton (csproj, Program.cs, SampleProcessor.cs, appsettings.json, Dockerfile) — SAMPLE-01/02
- [ ] `tests/BaseApi.Tests/Processor/SourceHashEmbedFacts.cs` — reflect built `Processor.Sample.dll`, assert attribute + 64-hex + incremental behavior — IDENT-02
- [ ] `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` — ProcessAsync returns 1 deterministic result — SAMPLE-01
- [ ] `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` — EXTEND for processor-sample — SAMPLE-02
- [ ] `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — live round-trip + truthful liveness gate — TEST-01
- [ ] `scripts/phase-28-close.ps1` — copy phase-22 + add processor-sample to `$services` — TEST-02
- [ ] Dual-build hash-reproducibility verification harness/script (A4 — the highest-risk gap)
- [ ] `SK_P.sln` — add `Processor.Sample` project
- [ ] compose.yaml — add `processor-sample` service

## Security Domain

> `security_enforcement` not separately confirmed; treating as enabled. This phase is mostly build tooling + dev-stack composition; the security surface is narrow.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Dev stack, guest/guest broker creds (accepted dev posture, verified compose). |
| V3 Session Management | no | N/A |
| V4 Access Control | no | N/A |
| V5 Input Validation | yes | The DB `^[a-f0-9]{64}$` SourceHash validator (verified `ProcessorDtoValidator.cs:30-32`) gates the registered hash; the reader fails-fast on a missing attribute naming the KEY only, never the value (V7 info-disclosure mitigation — verified `AssemblyMetadataSourceHashProvider.cs:14-15`). |
| V6 Cryptography | yes | SHA-256 via `System.Security.Cryptography` (NOT hand-rolled). Hash is an IDENTITY digest, not a security secret — no salt/HMAC needed (it's a build fingerprint). |

### Known Threat Patterns for {MSBuild inline task + dev compose}

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Inline task executing arbitrary code at build | Elevation | The task is in-repo, reviewed, netstandard2.0-only (no shell-out); standard for build tooling. |
| Stale/forged SourceHash registered as DB row | Spoofing | D-08 mandates the GENUINE built hash; the `^[a-f0-9]{64}$` validator + the unique index (`uq_processor_source_hash`, verified validator doc) gate registration. |
| Info-disclosure of hash value in error path | Information Disclosure | Reader names the attribute KEY only on failure (verified). |
| Build-context leakage in Dockerfile | Information Disclosure | Mirror Orchestrator Dockerfile's selective COPY (csproj-first restore layer); no secrets copied. |

## Sources

### Primary (HIGH confidence)
- Codebase files (read/verified this session):
  - `src/BaseProcessor.Core/Identity/AssemblyMetadataSourceHashProvider.cs` — reader uses `GetEntryAssembly()`, key `"SourceHash"`, fail-fast (D-03 constraint).
  - `src/BaseProcessor.Core/Identity/ISourceHashProvider.cs`, `ProcessorContext.cs` — seam contract.
  - `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` — `AddBaseProcessor` wires everything; concrete adds only the seam.
  - `src/BaseProcessor.Core/Processing/BaseProcessor.cs`, `EntryStepDispatchConsumer.cs`, `ProcessResult.cs` — the seam + round-trip; consumer takes `BaseProcessor`, writes `skp:data:{entryId}` (L2ProjectionKeys.ExecutionData).
  - `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — bind-then-MarkHealthy; UNBOUNDED boot-before-register retry; logs "row not yet registered".
  - `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` — writes `skp:{id:D}` only when Healthy.
  - `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` — `ExecutionDataTtl` default 300 (Pitfall 4).
  - `src/Orchestrator/Program.cs`, `Orchestrator.csproj`, `src/Orchestrator/Dockerfile` — the thin-console + multistage + csproj analog (D-06).
  - `compose.yaml` — the `orchestrator` service block to mirror.
  - `src/Orchestrator/Dispatch/StepDispatcher.cs` — `Send queue:{processorId:D}`; the queue the processor binds.
  - `tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs` — host-stack E2E harness + `RealStackWebAppFactory` + teardown discipline + the synthetic seed to OMIT (Pitfall 3).
  - `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` — guard-test assertion style.
  - `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` — in-memory MassTransit harness pattern for processor facts.
  - `src/BaseApi.Service/Features/Processor/ProcessorService.cs`, `ProcessorDtoValidator.cs` — CRUD + `^[a-f0-9]{64}$` validator (D-08).
  - `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `Processor` (`skp:{id}`) + `ExecutionData` (`skp:data:{id}`) key shapes.
  - `scripts/phase-22-close.ps1` — the triple-SHA / 3-GREEN gate to copy; pre-flight health check (line 41 aborts on non-healthy except otel — Open Q2).
  - `Directory.Build.props` — net8.0, TreatWarningsAsErrors (build constraint).
  - `.planning/REQUIREMENTS.md`, `.planning/ROADMAP.md`, `28-CONTEXT.md` — requirements + locked decisions.
- [learn.microsoft.com/visualstudio/msbuild/msbuild-roslyncodetaskfactory](https://learn.microsoft.com/en-us/visualstudio/msbuild/msbuild-roslyncodetaskfactory?view=vs-2022) — inline task declaration, `Code Type="Class"` parameter inference, `.targets`/UsingTask pattern.
- [learn.microsoft.com/visualstudio/msbuild/msbuild-inline-tasks](https://learn.microsoft.com/en-us/visualstudio/msbuild/msbuild-inline-tasks?view=vs-2022) — inline task mechanics.

### Secondary (MEDIUM confidence)
- [dotnet/msbuild#9854](https://github.com/dotnet/msbuild/issues/9854) — RoslynCodeTaskFactory compiles against netstandard2.0 (the .NET 8 API-surface limitation; constrains the task body to netstandard2.0 APIs).
- [dotnet/msbuild#9471](https://github.com/dotnet/msbuild/issues/9471) — .NET 8 RoslynCodeTaskFactory regression awareness (verify at Wave 0 that the task compiles under the pinned SDK).

### Tertiary (LOW confidence)
- The exact `<AssemblyAttribute>`-item-in-custom-target sequencing vs `CoreGenerateAssemblyInfo` (A3) — pattern is standard but MUST be reflection-verified in Wave 0; no single authoritative source pins the ordering for a `BeforeTargets="CoreCompile"` item add.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — zero new packages; all SDK-native or in-repo; verified against csproj files.
- Architecture (mirror-the-Orchestrator triad + reuse the E2E harness): HIGH — every analog file read and verified.
- MSBuild target form: MEDIUM-HIGH — mechanics CITED to Microsoft Learn; the `<AssemblyAttribute>`-in-target sequencing (A3) and cross-OS reproducibility (A4) require Wave-0 build verification.
- Pitfalls: HIGH — derived from verified code (reader fail-fast, ExecutionDataTtl, the synthetic-seed anti-pattern, the csproj transport/content gaps).
- Close gate: HIGH — `phase-22-close.ps1` read in full; only `$services` + label change, plus the documented teardown extension.

**Research date:** 2026-06-02
**Valid until:** 2026-07-02 (stable domain; MSBuild/SDK mechanics are slow-moving). Re-verify A4 (cross-OS hash) at Wave 0 regardless — it is build-environment-specific, not time-sensitive.
