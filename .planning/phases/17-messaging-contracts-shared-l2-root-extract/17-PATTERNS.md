# Phase 17: Messaging.Contracts + Shared L2 Root Extract - Pattern Map

**Mapped:** 2026-05-30
**Files analyzed:** 16 (6 new in Messaging.Contracts, 2 moved, 3 production using-swaps, 5 test using-swaps — 2 moves overlap the new-file set; net distinct = 6 new + 2 moved + 8 swaps)
**Analogs found:** 16 / 16 (every target has a verified in-repo analog or is itself the analog)

> All line numbers below were re-verified against the live v3.3.0 codebase on 2026-05-30. Where
> RESEARCH cited a line, it is confirmed (✓) inline.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Messaging.Contracts/Messaging.Contracts.csproj` | config (project) | n/a | `src/BaseApi.Core/BaseApi.Core.csproj` | exact (inheritance idiom) |
| `src/Messaging.Contracts/ICorrelated.cs` | model (interface/vocabulary) | transform (DTO) | RESEARCH Code Example (no existing interface analog) | role-only / synthesize |
| `src/Messaging.Contracts/StartOrchestration.cs` | model (message record) | event-driven (control msg) | `LivenessProjection.cs` (positional record shape) | role-match |
| `src/Messaging.Contracts/StopOrchestration.cs` | model (message record) | event-driven (control msg) | `LivenessProjection.cs` (positional record shape) | role-match |
| `src/Messaging.Contracts/CorrelationKeys.cs` (name = discretion) | constant (config) | n/a | `CorrelationIdMiddleware.cs:51-52` (the literal) | exact (literal source) |
| `src/Messaging.Contracts/.../WorkflowRootProjection.cs` | model (DTO, moved) | transform (JSON wire) | **itself** (verbatim move) | exact (self) |
| `src/Messaging.Contracts/.../LivenessProjection.cs` | model (DTO, moved) | transform (JSON wire) | **itself** (verbatim move) | exact (self) |
| `src/.../Projection/ProcessorProjection.cs` | model (DTO, stays internal) | transform (JSON wire) | using-swap only | mechanical |
| `src/.../Projection/RedisProjectionWriter.cs` | service (write engine, stays internal) | CRUD (Redis write) | using-swap only | mechanical |
| `src/.../Projection/RedisL2Cleanup.cs` | service (cleanup, stays internal) | CRUD (Redis delete) | using-swap only | mechanical |
| `tests/.../Projection/ProjectionRecordRoundTripTests.cs` | test (serialization guard) | transform | using-swap only | mechanical |
| `tests/.../Orchestration/HappyPathE2EFacts.cs` | test (integration) | request-response | using-swap only | mechanical |
| `tests/.../Orchestration/IdempotencyFacts.cs` | test (integration) | request-response | using-swap only | mechanical |
| `tests/.../Orchestration/StopCleanupFacts.cs` | test (integration) | event-driven | using-swap only | mechanical |
| `tests/.../Orchestration/RedisProjectionWriterFacts.cs` | test (unit/integration) | CRUD | using-swap only | mechanical |
| `Directory.Packages.props` | config (CPM) | n/a | Npgsql cautionary block `:52-60` | exact (mirror voice/format) |
| `SK_P.sln` | config (solution) | n/a | existing `{A1A1…}`/`{B2B2…}`/`{C3C3…}` rows `:5-28` | exact |

## Pattern Assignments

### `src/Messaging.Contracts/Messaging.Contracts.csproj` (config, new project)

**Analog:** `src/BaseApi.Core/BaseApi.Core.csproj` (the Phase 1 csproj-inheritance idiom)

**Inheritance idiom** (`BaseApi.Core.csproj:1`, `23-28` ✓):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>BaseApi.Core</RootNamespace>
    <AssemblyName>BaseApi.Core</AssemblyName>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>
  ...
```

**Copy pattern for the new csproj** — `Microsoft.NET.Sdk` (NOT `.Web`; D-01 / RESEARCH STACK row),
`RootNamespace`/`AssemblyName` = `Messaging.Contracts`, `GenerateDocumentationFile=true` + `NoWarn;CS1591`
(mirror exactly — Pitfall 4: doc-file ON without CS1591 suppression turns missing-doc into an error
under the global `TreatWarningsAsErrors=true`). **NO `<ItemGroup>`** — pure POCO, no PackageReference,
no FrameworkReference (`BaseApi.Core` adds `<FrameworkReference Include="Microsoft.AspNetCore.App" />`
at line 34 — Messaging.Contracts must NOT, per D-01). All common props
(`TargetFramework`/`Nullable`/`ImplicitUsings`/`LangVersion`/`AnalysisMode`/`EnforceCodeStyleInBuild`/
`TreatWarningsAsErrors`/`WarningsAsErrors`) inherit from `Directory.Build.props:28-42` (✓ verified) — do
NOT redeclare. CPM means no `Version=` on anything.

> Open Q2 (RESEARCH): do NOT add `Microsoft.Extensions.Logging.Abstractions` — no contract member
> references a logging type, and it is not currently in CPM. Adding it would require a new
> `<PackageVersion>` entry for zero benefit.

---

### `src/Messaging.Contracts/.../WorkflowRootProjection.cs` (model, MOVED verbatim)

**Analog:** itself — the file currently at
`src/BaseApi.Service/Features/Orchestration/Projection/WorkflowRootProjection.cs` (✓ read in full).

**Exact current shape** (lines 1-16 ✓ — this is the BEFORE):
```csharp
using System.Text.Json.Serialization;

namespace BaseApi.Service.Features.Orchestration.Projection;

/// <summary>
/// L2 root projection value for the <c>{prefix}{workflowId}</c> key (L2-PROJECT-03).
/// <c>correlationId</c> is the originating Start POST's <c>X-Correlation-Id</c>, letting
/// consumers trace a projection back to its build request. The <c>[property: JsonPropertyName]</c>
/// targets are load-bearing (RESEARCH Pitfall 1).
/// </summary>
internal sealed record WorkflowRootProjection(
    [property: JsonPropertyName("entryStepIds")] List<Guid> EntryStepIds,
    [property: JsonPropertyName("cron")]          string? Cron,
    [property: JsonPropertyName("jobId")]         Guid JobId,
    [property: JsonPropertyName("liveness")]      LivenessProjection Liveness,
    [property: JsonPropertyName("correlationId")] string CorrelationId);
```

**The ONLY two lines that change (AFTER)** — namespace (line 3) + visibility (line 11):
```csharp
namespace Messaging.Contracts;            // or Messaging.Contracts.Projections (granularity = discretion, D-discretion)
public sealed record WorkflowRootProjection(   // internal → public (D-05; forced by cross-assembly read)
```
Everything else — the `using`, the `<summary>` doc block, every `[property: JsonPropertyName(...)]`
camelCase target, the positional ctor, the order — moves byte-for-byte (D-07/D-08, Pitfall 3). The
`[property:]` prefix is load-bearing: bare attribute binds to the ctor param and STJ ignores it.
Note: `LivenessProjection` (line 15) resolves from the same target namespace since it moves with it.

---

### `src/Messaging.Contracts/.../LivenessProjection.cs` (model, MOVED verbatim)

**Analog:** itself — `src/BaseApi.Service/Features/Orchestration/Projection/LivenessProjection.cs` (✓).

**Exact current shape** (lines 1-14 ✓ — BEFORE):
```csharp
using System.Text.Json.Serialization;

namespace BaseApi.Service.Features.Orchestration.Projection;

/// <summary>
/// L2 liveness sub-document nested inside the root and processor projections
/// (L2-PROJECT-03/05). Field shapes only — no scheduler integration this milestone.
/// The <c>[property: JsonPropertyName]</c> target is load-bearing: on a positional record
/// a bare attribute binds to the ctor parameter and STJ ignores it (RESEARCH Pitfall 1).
/// </summary>
internal sealed record LivenessProjection(
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("interval")]  int Interval,
    [property: JsonPropertyName("status")]    string Status);
```

**Change exactly two lines (AFTER):** `namespace Messaging.Contracts;` (line 3) and
`public sealed record LivenessProjection(` (line 11). All three `[property: JsonPropertyName]`
camelCase keys (`timestamp`, `interval`, `status`) preserved verbatim (D-08).

---

### `src/Messaging.Contracts/ICorrelated.cs` (model, NEW)

**Analog:** none in repo (no existing interface declares this vocabulary). Synthesize from D-09 +
RESEARCH Code Example (lines 389-404). Pattern to copy:
```csharp
namespace Messaging.Contracts;

/// <summary>The frozen correlation vocabulary shared by future ICorrelated messages.</summary>
public interface ICorrelated
{
    Guid CorrelationId { get; }
    Guid ExecutionId   { get; }
    Guid WorkflowId    { get; }
    Guid StepId        { get; }
    Guid ProcessorId   { get; }
    Guid EntryId       { get; }
}
```
Six **get-only** `Guid` properties (D-09). Zero implementers this milestone — Start/Stop deliberately
do NOT implement it (D-10). Add an XML `<summary>` to satisfy the doc-file/CS1591 gate (or rely on the
csproj `NoWarn;CS1591` suppression, matching `BaseApi.Core`).

---

### `src/Messaging.Contracts/StartOrchestration.cs` + `StopOrchestration.cs` (model, NEW)

**Analog (record shape):** `LivenessProjection.cs` (positional `public sealed record` form). Per D-10
these are plain POCO — no `[JsonPropertyName]` needed (no wire-shape lock this phase):
```csharp
namespace Messaging.Contracts;

public sealed record StartOrchestration(Guid[] WorkflowIds);
public sealed record StopOrchestration(Guid[] WorkflowIds);
```
Exactly `Guid[] WorkflowIds`, no correlation field, do NOT implement `ICorrelated` (D-10). **No type
collision** — the existing `StartOrchestrationFacts`/`StopOrchestrationFacts` are xUnit test classes,
not types named `StartOrchestration` (RESEARCH MSG-CONTRACTS-02 row, verified). Add `<summary>` docs or
rely on `NoWarn;CS1591`.

---

### `src/Messaging.Contracts/CorrelationKeys.cs` (constant, NEW — name = discretion)

**Analog:** the literal at `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs:52` (✓ verified):
```csharp
private const string ItemKey = "CorrelationId";    // CorrelationIdMiddleware.cs:52
```
Hoist that exact literal into a shared `public const string` (D-11). Casing is load-bearing — it is the
single cross-service log-join key (`CorrelationIdMiddleware` + OTel `IncludeScopes`; see the doc block at
`CorrelationIdMiddleware.cs:44-47`). Pattern (name `CorrelationKeys`/`LogScope` = discretion):
```csharp
namespace Messaging.Contracts;

/// <summary>Cross-service correlation log-scope key. MUST equal the literal
/// CorrelationIdMiddleware uses so OTel IncludeScopes serializes one Elasticsearch attribute.</summary>
public static class CorrelationKeys
{
    public const string LogScope = "CorrelationId";
}
```
**Do NOT** repoint `CorrelationIdMiddleware.cs:52` at this constant this phase (RESEARCH Open Q1 + Q1
recommendation): repointing forces a `BaseApi.Core → Messaging.Contracts` ProjectReference (wider edge)
for zero behavior change and risks the `BaseApi.Core` zero-warning gate. Add the constant only; Phase 18's
consume filter consolidates if desired.

---

### Production using-swap consumers (stay internal in BaseApi.Service)

These three files live in `namespace BaseApi.Service.Features.Orchestration.Projection;` and therefore
referenced the moved types with **NO `using`** (same namespace). After the move each MUST add
`using Messaging.Contracts;` (or the chosen sub-namespace). No logic change.

| File | Line touching moved type (✓ verified) | What it does |
|------|----------------------------------------|--------------|
| `src/.../Projection/ProcessorProjection.cs` | `:14` `[property: JsonPropertyName("liveness")] LivenessProjection Liveness` | nests `LivenessProjection` — stays `internal sealed record` (D-06) |
| `src/.../Projection/RedisProjectionWriter.cs` | `:60` `new LivenessProjection(...)`; `:66` `new WorkflowRootProjection(...)`; `<see cref>` docs `:12,:21` | constructs both — stays `internal sealed class` (D-06) |
| `src/.../Projection/RedisL2Cleanup.cs` | `:45` `JsonSerializer.Deserialize<WorkflowRootProjection>(rootJson!)!.EntryStepIds` | deserializes the root — stays `internal sealed class` (D-06). **NOT in CONTEXT canonical swap list** — Pitfall 1 |

Swap pattern (add the import alongside existing usings, e.g. `RedisProjectionWriter.cs:1-6`):
```csharp
using System.Text.Json;
using BaseApi.Core.Configuration;
using BaseApi.Service.Features.Orchestration;
using Messaging.Contracts;          // <-- ADD (or Messaging.Contracts.Projections)
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
```

---

### Test using-swap consumers (BaseApi.Tests)

All five already carry an **explicit** `using BaseApi.Service.Features.Orchestration.Projection;`
line (✓ verified). The swap = add `using Messaging.Contracts;` (the old `using` may stay — `StepProjection`/
`ProcessorProjection` remain in it). Public types flow transitively via `BaseApi.Service`'s ProjectReference
(Pitfall 2) — no test-csproj ProjectReference change required (`BaseApi.Tests.csproj:111-116` ✓ already
references both `BaseApi.Core` and `BaseApi.Service`).

| File | Using line (✓) | Moved-type reference (✓) |
|------|----------------|--------------------------|
| `tests/.../Projection/ProjectionRecordRoundTripTests.cs` | `:4` | constructs Root (`:40`) + Liveness (`:34-35`); asserts exact camelCase keys (`:49-57`) — the SC#3 byte-identical guard, must stay GREEN |
| `tests/.../Orchestration/HappyPathE2EFacts.cs` | `:5` | `Deserialize<WorkflowRootProjection>` (`:137`) |
| `tests/.../Orchestration/IdempotencyFacts.cs` | `:4` | `Deserialize<WorkflowRootProjection>` ×3 (`:124,:136,:177`) |
| `tests/.../Orchestration/StopCleanupFacts.cs` | `:2` | `new LivenessProjection` (`:35`) + `new WorkflowRootProjection` (`:43`) |
| `tests/.../Orchestration/RedisProjectionWriterFacts.cs` | `:4` | `Deserialize<WorkflowRootProjection>` (`:155`) |

---

### `Directory.Packages.props` (config, MODIFIED — CPM pin)

**Analog:** the Npgsql cautionary comment block at `Directory.Packages.props:52-60` (✓ — the comment
explaining why Npgsql is pinned at 8.0.9). Mirror its authorial voice. Add two `<PackageVersion>` rows
inside the single `<ItemGroup>` (`:46-121`):
```xml
<!-- MassTransit 8.5.5 is the last Apache-2.0 line. v9+ is COMMERCIAL ($400/mo min for
     >$1M-revenue orgs); v8.x stays open-source + security-patched through end-2026. Do NOT
     bump to 9.x without a license decision. No PackageReference yet (Phase 17 = CPM pin only;
     publisher/consumer wiring lands Phase 18+). -->
<PackageVersion Include="MassTransit" Version="8.5.5" />
<PackageVersion Include="MassTransit.RabbitMQ" Version="8.5.5" />
```
`<PackageVersion>` (CPM), NOT `<PackageReference Version=>`. NO csproj anywhere gets a MassTransit
`PackageReference` this phase (D-03/D-12 — forbidden-package grep is a Wave 0 assertion).

---

### `SK_P.sln` (config, MODIFIED)

**Analog:** the existing project blocks + config rows at `SK_P.sln:5-28` (✓). Each project has a
`Project(...)/EndProject` block (`:5-10`) AND **4** `ProjectConfigurationPlatforms` rows (Debug/Release ×
ActiveCfg/Build.0, e.g. `{C3C3C3C3…}:25-28`). Adding the project block WITHOUT the 4 config rows means
it is skipped under `dotnet build SK_P.sln -c Release` (Pitfall 5 — defeats the zero-warning gate).
**Prefer the CLI:** `dotnet sln SK_P.sln add src/Messaging.Contracts/Messaging.Contracts.csproj` (writes
the block + 4 rows + a fresh GUID). Also add the `ProjectReference` to `Messaging.Contracts` in
`src/BaseApi.Service/BaseApi.Service.csproj` (mirror the existing `:35`
`<ProjectReference Include="..\BaseApi.Core\BaseApi.Core.csproj" />`).

## Shared Patterns

### csproj inheritance (apply to: new Messaging.Contracts.csproj)
**Source:** `BaseApi.Core.csproj:23-28` + `Directory.Build.props:28-42`
Declare only `RootNamespace`/`AssemblyName`/`GenerateDocumentationFile`/`NoWarn;CS1591`. Everything else
inherits. CPM → no `Version=`. Pure-POCO → no ItemGroup.

### Behavior-preserving record move (apply to: WorkflowRootProjection, LivenessProjection)
**Source:** the two record files themselves.
Change exactly two lines (namespace + `internal→public`). Preserve `using`, `<summary>`, every
`[property: JsonPropertyName]`, positional ctor, parameter order. Verbatim everywhere else (D-07/D-08).

### using-swap (apply to: all 8 consumers)
**Source:** Pattern 3 (RESEARCH). Add `using Messaging.Contracts;`. The 3 production files in the old
namespace had no using and must ADD one; the 5 tests already import the old namespace and ADD the new one.
Safety net: a solution-wide `dotnet build -c Debug` surfaces any missed file as CS0246.

### CPM pin + forbidden-reference (apply to: Directory.Packages.props + all csproj)
**Source:** Npgsql block `Directory.Packages.props:52-60`.
`<PackageVersion>` only; zero `<PackageReference Include="MassTransit">` anywhere; cautionary comment
mirrors the Npgsql voice.

### Wire-shape regression guard (apply to: the whole move)
**Source:** `ProjectionRecordRoundTripTests.cs:26-28,38-58`.
Serializes under **default** `JsonSerializerOptions` (no camelCase policy) and asserts exact camelCase
keys → the `[property:]` pins must hold on their own. This test staying GREEN is the SC#3 byte-identical
proof. (Pitfall 3.)

## No Analog Found

| File | Role | Data Flow | Reason / Resolution |
|------|------|-----------|---------------------|
| `src/Messaging.Contracts/ICorrelated.cs` | model (interface) | transform | No existing interface declares the 6-Guid vocabulary. Synthesize from D-09 + RESEARCH Code Example (lines 389-404). Record-shape analog (`LivenessProjection`) covers the file conventions; the interface body is net-new. |

(Start/Stop and CorrelationKeys DO have analogs — the positional-record form and the
`CorrelationIdMiddleware.cs:52` literal respectively — so they are not listed here.)

## Metadata

**Analog search scope:** `src/BaseApi.Core/`, `src/BaseApi.Service/Features/Orchestration/Projection/`,
`src/BaseApi.Core/Middleware/`, `tests/BaseApi.Tests/Features/Orchestration/`, repo-root
`Directory.Build.props` / `Directory.Packages.props` / `SK_P.sln`.
**Files read (verbatim):** WorkflowRootProjection.cs, LivenessProjection.cs, ProcessorProjection.cs,
RedisProjectionWriter.cs, RedisL2Cleanup.cs, ProjectionRecordRoundTripTests.cs, BaseApi.Core.csproj,
BaseApi.Service.csproj, BaseApi.Tests.csproj (refs), CorrelationIdMiddleware.cs:40-64,
Directory.Packages.props, Directory.Build.props, SK_P.sln, AssemblyInfo.cs.
**Line-number verification:** ALL RESEARCH-cited lines confirmed against live code — middleware
literal `:52` ✓, ProcessorProjection liveness `:14` ✓, writer `:60`/`:66` ✓, cleanup `:45` ✓, 5 test
deserialize/construct sites ✓, sln config rows `:5-28` ✓, Npgsql block `:52-60` ✓, Build.props global
props `:28-42` ✓.
**Pattern extraction date:** 2026-05-30
