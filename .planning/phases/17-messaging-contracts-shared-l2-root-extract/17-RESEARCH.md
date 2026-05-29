# Phase 17: Messaging.Contracts + Shared L2 Root Extract - Research

**Researched:** 2026-05-30
**Domain:** .NET 8 class-library extraction (behavior-preserving move) + Central Package Management pin
**Confidence:** HIGH (everything load-bearing was verified directly against the v3.3.0 codebase; only the MassTransit licensing detail relied on web sources, and it confirms the locked decision)

<user_constraints>
## User Constraints (from CONTEXT.md)

> CONTEXT.md is AUTHORITATIVE. Decisions D-01..D-12 are locked. This research does NOT
> re-derive them — it verifies the current code state and surfaces mechanics/landmines the
> planner needs. Where this research found facts CONTEXT.md omitted, they are flagged
> **[RESEARCH ADDITION]**.

### Locked Decisions

- **D-01:** `Messaging.Contracts` is **pure-POCO** — references at most
  `Microsoft.Extensions.Logging.Abstractions`; **NO MassTransit PackageReference** (honors
  MSG-CONTRACTS-01 literally).
- **D-02:** Correlation filters (`IFilter<ConsumeContext>` / `IFilter<SendContext>` /
  `IFilter<PublishContext>`) and the AsyncLocal accessor are **DEFERRED to Phase 18**. Do NOT
  introduce filters into Phase 17. The ROADMAP one-liner that places "correlation machinery"
  / filters in Phase 17 is shorthand, superseded by the Phase 17 success criteria.
- **D-03:** MassTransit **CPM pins are added this phase** (INFRA-RMQ-01); the
  `PackageReference` waits for Phase 18 (no consumer yet).
- **D-04:** Move **both** `WorkflowRootProjection` AND `LivenessProjection` to
  `Messaging.Contracts`.
- **D-05:** Both moved records flip `internal sealed record` → **`public sealed record`**
  (mechanical, forced by cross-assembly read).
- **D-06:** Everything else **stays `internal` in `BaseApi.Service`**: `RedisProjectionWriter`,
  `IRedisProjectionWriter`, `StepProjection`, `ProcessorProjection`, `RedisProjectionKeys`,
  `RedisL2Cleanup`. `ProcessorProjection` (stays) references the moved `LivenessProjection` via
  using-swap. Step/Processor shapes do NOT move.
- **D-07:** Keep the type name `WorkflowRootProjection` (do NOT rename to
  `WorkflowRootProjectionContract`). Only the namespace relocates.
- **D-08:** **Wire shape byte-identical** — every `[property: JsonPropertyName(...)]` camelCase
  target preserved verbatim (`entryStepIds`, `cron`, `jobId`, `liveness`, `correlationId`;
  liveness: `timestamp`, `interval`, `status`). The `[property:]` prefix MUST stay.
- **D-09:** `ICorrelated` declares six `Guid` get-only properties `{ CorrelationId, ExecutionId,
  WorkflowId, StepId, ProcessorId, EntryId }`. Zero implementers this milestone.
- **D-10:** `StartOrchestration` / `StopOrchestration` are POCO records, each `Guid[] WorkflowIds`,
  no correlation field, do NOT implement `ICorrelated`.
- **D-11:** The `"CorrelationId"` PascalCase log-scope key becomes a **shared constant** in
  `Messaging.Contracts` (the exact literal `CorrelationIdMiddleware.cs:52` uses).
- **D-12:** Add `MassTransit` + `MassTransit.RabbitMQ` at `8.5.5` to `Directory.Packages.props`
  with a blocking comment that v9+ is commercial, mirroring the Npgsql cautionary block.

### Claude's Discretion

- Namespace granularity inside `Messaging.Contracts` (flat root vs `Messaging.Contracts.Projections`
  sub-namespace).
- Exact placement/name of the `"CorrelationId"` constant (e.g. `CorrelationKeys` / `LogScope`).
- New project's `.csproj` shape — follow the Phase 1 csproj-inheritance idiom (no redeclared
  common properties; CPM means no `Version=`).

### Deferred Ideas (OUT OF SCOPE)

- Correlation filters + AsyncLocal accessor → Phase 18 (CORR-01/CORR-02).
- `ICorrelated` mutability (settable/init for outbound stamping) → later filter phase.
- Concrete `ICorrelated` implementers (`JobTrigger`, `ExecutionResult`) → Processor milestone.
- Step/Processor projection shapes moving to Contracts → only when a consumer reads them.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| MSG-CONTRACTS-01 | `Messaging.Contracts` class library exists, referenced by both hosts, NO MassTransit/host dependency (POCO records only) | New `Microsoft.NET.Sdk` (non-Web) project; `BaseApi.Service` adds `ProjectReference`. `Orchestrator` doesn't exist yet (Phase 19) — SC#1 "referenced by both" is partly forward-looking; only the `BaseApi.Service` reference is provable this phase. The csproj must declare a `ProjectReference` to nothing and at most `Microsoft.Extensions.Logging.Abstractions` (see Standard Stack). |
| MSG-CONTRACTS-02 | `StartOrchestration`/`StopOrchestration` records, each exactly `Guid[] WorkflowIds`, no correlation field | New POCO records. **No name collision** with the existing test classes `StartOrchestrationFacts`/`StopOrchestrationFacts` (those are xUnit classes + HTTP endpoint names, not types). Verified: no type `StartOrchestration`/`StopOrchestration` exists in `src/`. |
| MSG-CONTRACTS-03 | `ICorrelated` declares the 6-Guid frozen vocabulary | New interface, get-only properties (D-09). Zero implementers. |
| MSG-CONTRACTS-04 | L2 root read-shape lives in `Messaging.Contracts`, single source of truth | Move `WorkflowRootProjection` + `LivenessProjection` (D-04). Blast radius is wider than CONTEXT.md's canonical list — see **Common Pitfall 1**. |
| INFRA-RMQ-01 | MassTransit + MassTransit.RabbitMQ pinned at 8.5.5 with blocking comment | CPM pin in `Directory.Packages.props`. Mirror the Npgsql block at `:52`. v9 licensing **VERIFIED** (see State of the Art). |
</phase_requirements>

## Summary

Phase 17 is a **behavior-preserving extract + a CPM pin**, with three net-new POCO type groups
(`ICorrelated`, `StartOrchestration`/`StopOrchestration`, the shared `"CorrelationId"` constant)
and one mechanical move (`WorkflowRootProjection` + `LivenessProjection` from `BaseApi.Service`
into a new leaf library). There is **zero runtime behavior change** — the only observable effects
are (1) the new assembly exists and is referenced by `BaseApi.Service`, (2) the MassTransit CPM
pins appear, and (3) the v3.3.0 suite stays GREEN, proving the using-swap is behavior-preserving.

The single highest-risk item is **scope-of-move blindness**: CONTEXT.md's canonical-refs list
names the move/swap files but the codebase has **two more consumers it does not list** —
`RedisL2Cleanup.cs` (production) deserializes `WorkflowRootProjection`, and **five** test files
reference the moved types (CONTEXT.md names only `ProjectionRecordRoundTripTests.cs`). Every one
of these compiles today because the types live in `BaseApi.Service.Features.Orchestration.Projection`
and consumers either share that namespace or `using` it. After the move, **each consumer needs a
using-swap to the new namespace** or the solution will not build. The full list is in Pitfall 1 —
the planner must enumerate all eight consumer files, not the four CONTEXT.md mentions.

The wire shape is protected by `ProjectionRecordRoundTripTests.cs`, which serializes under
**default** `JsonSerializerOptions` (no camelCase policy) and asserts the exact camelCase keys —
so the `[property: JsonPropertyName]` pins must travel with the records verbatim (D-08). That test
staying GREEN is the byte-identical proof for SC#3.

**Primary recommendation:** Add the new `Microsoft.NET.Sdk` (non-Web) `Messaging.Contracts`
project with no redeclared common properties (Phase 1 idiom) and no PackageReference beyond an
optional `Microsoft.Extensions.Logging.Abstractions`; move the two records verbatim (only the
`namespace` line and `internal`→`public` change); using-swap **all eight** consumer files; add the
CPM pins mirroring the Npgsql block; gate on zero-warning Release+Debug + 3-consecutive-GREEN +
byte-identical Redis/psql snapshots.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Frozen message vocabulary (`ICorrelated`, Start/Stop records) | Shared contracts library (`Messaging.Contracts`) | — | Leaf assembly both publisher (`BaseApi.Service`) and future consumer (`Orchestrator`) compile against; no host coupling (D-01). |
| L2 root read-shape (`WorkflowRootProjection` + `LivenessProjection`) | Shared contracts library | — | Single source of truth: WebApi writes, Orchestrator reads (MSG-CONTRACTS-04). |
| L1→L2 write engine (`RedisProjectionWriter`) | API/Backend (`BaseApi.Service`) | — | Stays internal (D-06); only swaps the using. |
| L2 cleanup (`RedisL2Cleanup`) | API/Backend (`BaseApi.Service`) | — | Stays internal (D-06); deserializes the moved root → using-swap. **[RESEARCH ADDITION]** |
| Step/Processor projection shapes | API/Backend (`BaseApi.Service`) | — | Not read cross-assembly this milestone; stay internal (D-06). |
| Cross-service log-join key constant | Shared contracts library | API/Backend (consumes constant) | Casing is load-bearing; single literal source prevents drift (D-11). |
| MassTransit version governance | Build infra (`Directory.Packages.props`) | — | CPM pin only; no consumer yet (D-03/D-12). |

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `Microsoft.NET.Sdk` (project SDK) | n/a (SDK 8.0.421) | The plain class-library SDK for `Messaging.Contracts` | `BaseApi.Core` uses `Microsoft.NET.Sdk`; `BaseApi.Service` uses `Microsoft.NET.Sdk.Web`. A pure-POCO contracts lib uses the **plain** SDK — NOT the Web SDK (no AspNetCore framework reference needed). [VERIFIED: BaseApi.Core.csproj:1] |
| `Microsoft.Extensions.Logging.Abstractions` | (none currently pinned) | Allowed-by-D-01 dependency ceiling | **[RESEARCH ADDITION]** D-01 permits "at most `Microsoft.Extensions.Logging.Abstractions`". This package is **NOT currently in CPM** (`Directory.Packages.props` has no entry). Phase 17 likely does **not need it at all** — the `"CorrelationId"` constant is a bare `const string` requiring no logging types. **Recommendation:** do NOT add a PackageReference unless a contract member actually needs a logging abstraction type; if it is added, it requires a new CPM `PackageVersion` entry. The frozen vocabulary, the records, and the constant are all plain POCO/`string`. [VERIFIED: Directory.Packages.props — no M.E.Logging.Abstractions entry] |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `MassTransit` | `8.5.5` | CPM pin only (D-12) — no PackageReference this phase | INFRA-RMQ-01. Add `<PackageVersion Include="MassTransit" Version="8.5.5" />`. |
| `MassTransit.RabbitMQ` | `8.5.5` | CPM pin only (D-12) | INFRA-RMQ-01. Add `<PackageVersion Include="MassTransit.RabbitMQ" Version="8.5.5" />`. |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `Microsoft.NET.Sdk` | `Microsoft.NET.Sdk.Web` | Web SDK auto-references `Microsoft.AspNetCore.App` — violates D-01 "no host library" and bloats the leaf. Rejected. |
| Move only `WorkflowRootProjection` | Move root + Liveness (D-04) | Liveness is nested by both root AND `ProcessorProjection`; moving only the root would force a duplicate Liveness shape. D-04 chose the no-dup path. |
| Rename to `…Contract` | Keep `WorkflowRootProjection` (D-07) | Rename touches `RedisProjectionWriter` + `RedisL2Cleanup` + 5 tests for zero wire benefit. Rejected. |

**Installation (Phase 17 = CPM pins only; no `dotnet add package`):**
```xml
<!-- Directory.Packages.props — new <PackageVersion> entries (CPM); no PackageReference yet -->
<PackageVersion Include="MassTransit" Version="8.5.5" />
<PackageVersion Include="MassTransit.RabbitMQ" Version="8.5.5" />
```

**Version verification:**
- MassTransit `8.5.5` is the locked Apache-2.0 version per STACK.md and CONTEXT D-12. v8.x remains
  open-source through end-2026; v9 is commercial. [VERIFIED: web — see State of the Art]
- `dotnet build` / `dotnet test` use **SDK 8.0.421** (the project's pinned SDK; runtime 8.0.27).
  [VERIFIED: STATE.md:182 "SDK 8.0.421 literal"]

## Architecture Patterns

### System Architecture Diagram (after Phase 17)

```
                  ┌───────────────────────────────────────────────────────┐
                  │  Messaging.Contracts  (NEW leaf — Microsoft.NET.Sdk)    │
                  │  pure POCO, no MassTransit PackageReference (D-01)      │
                  │                                                         │
                  │   • ICorrelated (6 get-only Guids)            [D-09]    │
                  │   • StartOrchestration { Guid[] WorkflowIds } [D-10]    │
                  │   • StopOrchestration  { Guid[] WorkflowIds } [D-10]    │
                  │   • WorkflowRootProjection (public, moved)    [D-04/05] │
                  │   • LivenessProjection     (public, moved)    [D-04/05] │
                  │   • const "CorrelationId" log-scope key       [D-11]    │
                  └───────────────▲───────────────────────────────────────┘
                                  │ ProjectReference (added this phase)
                                  │ (Orchestrator ref is Phase 19 — forward)
        ┌─────────────────────────┴──────────────────────────┐
        │  BaseApi.Service  (Microsoft.NET.Sdk.Web)           │
        │                                                     │
        │   RedisProjectionWriter ──constructs──► Root+Liveness (using-swap)
        │   RedisL2Cleanup ───────deserializes──► Root          (using-swap) [ADDITION]
        │   ProcessorProjection ──nests─────────► Liveness      (using-swap)
        │   StepProjection (stays internal, untouched)         │
        └─────────────────────────────────────────────────────┘
                                  ▲
                                  │ ProjectReference (existing) + InternalsVisibleTo
        ┌─────────────────────────┴──────────────────────────┐
        │  BaseApi.Tests  (transitively sees Messaging.Contracts public types
        │   via BaseApi.Service ProjectReference)              │
        │   5 test files using-swap to the new namespace [ADDITION: 4 beyond CONTEXT]
        └─────────────────────────────────────────────────────┘

  Build infra:  Directory.Build.props (common props) · Directory.Packages.props (CPM + MassTransit pins)
  SK_P.sln  ← gains the Messaging.Contracts project + its Debug/Release config rows
```

### Recommended Project Structure

```
src/
├── BaseApi.Core/                  # unchanged
├── BaseApi.Service/               # records removed; consumers using-swapped
└── Messaging.Contracts/           # NEW leaf project
    ├── Messaging.Contracts.csproj # Microsoft.NET.Sdk; no redeclared common props
    ├── ICorrelated.cs             # D-09
    ├── StartOrchestration.cs      # D-10
    ├── StopOrchestration.cs       # D-10
    ├── CorrelationKeys.cs         # D-11 const (name is discretion)
    └── Projections/               # OR flat root — namespace granularity is discretion
        ├── WorkflowRootProjection.cs   # moved, public sealed
        └── LivenessProjection.cs       # moved, public sealed
```

### Pattern 1: csproj-inheritance idiom (Phase 1 proven)
**What:** The new csproj declares ONLY project-specific properties (`RootNamespace`,
`AssemblyName`, optionally `GenerateDocumentationFile` + `NoWarn;CS1591`). Common props
(`TargetFramework`, `Nullable`, `ImplicitUsings`, `LangVersion`, `AnalysisMode`,
`EnforceCodeStyleInBuild`, `TreatWarningsAsErrors`, the RMG `WarningsAsErrors`) inherit from
`Directory.Build.props`. NO `Version=` on any PackageReference (CPM).
**When to use:** Every project in this solution.
**Example (mirror `BaseApi.Core.csproj` minus the package/framework refs):**
```xml
<!-- Source: BaseApi.Core.csproj:1,23-28 (inheritance idiom) -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Messaging.Contracts</RootNamespace>
    <AssemblyName>Messaging.Contracts</AssemblyName>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>
  <!-- NO ItemGroup needed: pure POCO. Add M.E.Logging.Abstractions ONLY if a
       contract member references a logging type (it does not, per current scope). -->
</Project>
```
Note: `BaseApi.Core` sets `GenerateDocumentationFile=true` + `NoWarn;CS1591`. Because
`TreatWarningsAsErrors=true` is global, **every public member needs an XML doc comment** OR
CS1591 must be suppressed. The moved records already carry `<summary>` blocks; new types
(`ICorrelated`, Start/Stop, constant) should carry them too, or rely on the `NoWarn;CS1591`
suppression as `BaseApi.Core` does. Recommend mirroring `BaseApi.Core` exactly: doc-file on +
CS1591 suppressed.

### Pattern 2: behavior-preserving record move
**What:** Move the `.cs` file's content verbatim; change exactly two things — the `namespace`
line and `internal sealed record` → `public sealed record`. Keep all `[property: JsonPropertyName]`
attributes, the positional ctor, and the `<summary>` XML docs byte-for-byte.
**When to use:** D-04/D-05/D-07/D-08.
**Example (the only two lines that change in `WorkflowRootProjection.cs`):**
```csharp
// BEFORE
namespace BaseApi.Service.Features.Orchestration.Projection;
internal sealed record WorkflowRootProjection(
// AFTER  (e.g. flat root namespace; granularity is discretion)
namespace Messaging.Contracts;          // or Messaging.Contracts.Projections
public sealed record WorkflowRootProjection(
//   ...all [property: JsonPropertyName(...)] params unchanged...
```

### Pattern 3: using-swap at every consumer
**What:** Add `using Messaging.Contracts;` (or the chosen sub-namespace) to every file that
referenced the moved types, and remove the now-dead reliance on the old in-namespace visibility.
Files in `BaseApi.Service.Features.Orchestration.Projection` previously needed NO using (same
namespace); after the move they MUST add the new using.
**When to use:** All eight consumers (see Pitfall 1).

### Anti-Patterns to Avoid
- **Introducing MassTransit into `Messaging.Contracts`:** violates D-01/MSG-CONTRACTS-01. The pins
  are CPM-only; no PackageReference anywhere this phase.
- **Adding the correlation filters / AsyncLocal accessor:** explicitly Phase 18 (D-02). The
  ROADMAP one-liner is shorthand.
- **Rewriting the records "cleaner" (non-positional, camelCase via policy, etc.):** any change to
  the positional/attribute shape risks the byte-identical wire contract (D-08). Move verbatim.
- **Redeclaring common MSBuild props in the new csproj:** defeats `Directory.Build.props` SSOT.
- **Putting `<Version=>` on the MassTransit pins as PackageReference:** they are `<PackageVersion>`
  in CPM, and there is no PackageReference this phase.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| camelCase wire mapping | A custom JSON converter or naming policy | The existing `[property: JsonPropertyName]` pins (move verbatim) | The pins are already correct + regression-guarded; rewriting risks drift (D-08, Pitfall 1 below). |
| Cross-service log-join key | A second `"CorrelationId"` literal in Contracts | A single shared `const string` referenced by both sides (D-11) | Two literals silently diverge in casing → broken Elasticsearch join. |
| Version governance | A `Version=` on a per-project PackageReference | CPM `<PackageVersion>` in `Directory.Packages.props` | The whole repo is CPM; a local Version= is a build error (`ManagePackageVersionsCentrally=true`). |

**Key insight:** Phase 17 is almost entirely *moving and pinning existing, already-correct
artifacts*. The value is in NOT changing them — every "improvement" is a regression risk against
the byte-identical / zero-warning / GREEN gates.

## Runtime State Inventory

> This IS an extract/refactor phase, so the inventory applies. The critical finding is the
> **compile-time** consumer set (Pitfall 1), but runtime state is also checked below.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data (Redis L2) | The on-disk JSON of `{prefix}{workflowId}` root keys is produced by `WorkflowRootProjection` serialization. Because the move is byte-identical (D-08), **stored values are unaffected** — the C# type identity (assembly/namespace) is NOT part of the JSON. | **None** — verified the wire shape is `[JsonPropertyName]`-pinned and the move changes only namespace + visibility, not field names/order/attributes. The triple-SHA close gate (`redis-cli --scan` BEFORE=AFTER) proves it. |
| Live service config | No external service stores a .NET namespace or type name. RabbitMQ isn't wired this phase (no PackageReference). | **None** — verified: no MassTransit consumer/endpoint exists yet (Phase 18+). |
| OS-registered state | None (no scheduled tasks, services, or daemons reference these types). | **None** — verified by absence; this is a library refactor. |
| Secrets/env vars | The `"CorrelationId"` literal is a **code constant**, not a secret or env var. Hoisting it to a shared `const` is a code rename only. | **None** — no env/secret key references the moved types or the constant. |
| Build artifacts | `BaseApi.Service`'s compiled assembly will no longer contain `WorkflowRootProjection`/`LivenessProjection`; a new `Messaging.Contracts.dll` appears. `bin/`/`obj/` are regenerated by build. No published/cached package carries the old types. | A clean `dotnet build` regenerates everything; no manual artifact surgery. Recommend `dotnet clean` before the zero-warning gate to avoid stale-assembly false-greens. |

**Canonical question — "after every file is updated, what runtime systems still have the old string
cached/stored/registered?"** Answer: **none**. The only persisted artifact is the Redis L2 JSON,
and its shape is decoupled from the C# type identity by the `[JsonPropertyName]` pins (D-08). The
real risk is **compile-time**, not runtime — see Pitfall 1.

## Common Pitfalls

### Pitfall 1: Scope-of-move blindness — CONTEXT.md's consumer list is INCOMPLETE [RESEARCH ADDITION]
**What goes wrong:** You using-swap the four files CONTEXT.md names and the solution still fails to
build, because two more consumers reference the moved types.
**Why it happens:** The types live in `BaseApi.Service.Features.Orchestration.Projection`; consumers
in the **same namespace** need no `using`, so a grep for `using …Projection` misses them. CONTEXT.md's
canonical-refs list named: `WorkflowRootProjection.cs`, `LivenessProjection.cs`, `ProcessorProjection.cs`,
`RedisProjectionWriter.cs`, `RedisL2Cleanup.cs` (mentioned in the "stays internal" list), and one test
(`ProjectionRecordRoundTripTests.cs`). The **complete** verified consumer set is:

| # | File | Tier | How it consumes | Swap needed |
|---|------|------|-----------------|-------------|
| 1 | `src/.../Projection/WorkflowRootProjection.cs` | move | (the type itself) | namespace + public (D-05/07) |
| 2 | `src/.../Projection/LivenessProjection.cs` | move | (the type itself) | namespace + public (D-05/07) |
| 3 | `src/.../Projection/ProcessorProjection.cs` | stays internal | nests `LivenessProjection` (line 14) | add `using Messaging.Contracts;` |
| 4 | `src/.../Projection/RedisProjectionWriter.cs` | stays internal | constructs Root (line 66) + Liveness (line 60); `<see cref>` in docs | add using |
| 5 | `src/.../Projection/RedisL2Cleanup.cs` | stays internal | `Deserialize<WorkflowRootProjection>` (line 45) | add using **(NOT in CONTEXT canonical list as a swap target)** |
| 6 | `tests/.../Projection/ProjectionRecordRoundTripTests.cs` | test | constructs Root + Liveness | add using |
| 7 | `tests/.../HappyPathE2EFacts.cs` | test | `Deserialize<WorkflowRootProjection>` (line 137) | add using |
| 8 | `tests/.../IdempotencyFacts.cs` | test | `Deserialize<WorkflowRootProjection>` (3×) | add using |
| 9 | `tests/.../StopCleanupFacts.cs` | test | constructs Root + Liveness | add using |
| 10 | `tests/.../RedisProjectionWriterFacts.cs` | test | `Deserialize<WorkflowRootProjection>` (line 155) | add using |

So: **2 moves + 3 production consumers (incl. RedisL2Cleanup) + 5 test consumers = 10 files touched**,
of which **8 are using-swaps**. CONTEXT.md's canonical list named only 4 of the 8 swap targets.
**How to avoid:** Plan a single explicit swap task that enumerates all eight. After moving, a
solution-wide `dotnet build` will surface any missed file as a CS0246 ("type or namespace not found");
treat that as the safety net, not the plan.
**Warning signs:** CS0246 on `WorkflowRootProjection`/`LivenessProjection` after the move.
[VERIFIED: grep across src/ and tests/]

### Pitfall 2: Test-project visibility — `internal`→`public` removes the InternalsVisibleTo dependency, but the using still changes
**What goes wrong:** Tests currently reach `WorkflowRootProjection`/`LivenessProjection` because
they were `internal` in `BaseApi.Service` and `BaseApi.Service/Properties/AssemblyInfo.cs` has
`[assembly: InternalsVisibleTo("BaseApi.Tests")]`. After the move they are `public` in
`Messaging.Contracts`, which `BaseApi.Tests` does **not** reference directly.
**Why it's fine:** `BaseApi.Tests` references `BaseApi.Service` (ProjectReference), and
`BaseApi.Service` will reference `Messaging.Contracts` — so the **public** types flow transitively
to the test project. No new ProjectReference on the test csproj is strictly required for *compilation*
of the public types. **However**, the test files still need the new `using` (Pitfall 1).
**Recommendation:** Rely on transitive flow (no test-csproj ProjectReference change) unless the
planner prefers an explicit `ProjectReference` to `Messaging.Contracts` for clarity — either works;
transitive is the minimal change. The `InternalsVisibleTo` line stays (still needed for the records/
seams that remain `internal`, e.g. `RedisProjectionWriter`, `RedisL2Cleanup`, `StepProjection`).
[VERIFIED: BaseApi.Tests.csproj:111-116; AssemblyInfo.cs:3]
**Warning sign:** if a test references a type that is now public-in-Contracts and the build still
fails, confirm `BaseApi.Service` actually added the ProjectReference (transitive flow depends on it).

### Pitfall 3: Positional-record `[property:]` prefix is load-bearing (D-08)
**What goes wrong:** Dropping the `property:` prefix (or "tidying" to a non-positional record)
makes STJ bind the attribute to the ctor *parameter*, which it ignores — the camelCase key reverts
to PascalCase and the wire shape silently breaks.
**Why it happens:** On a positional record, a bare `[JsonPropertyName]` targets the parameter, not
the generated property. The existing records correctly use `[property: JsonPropertyName(...)]`.
**How to avoid:** Move verbatim. Do not refactor record syntax.
**Warning sign:** `ProjectionRecordRoundTripTests.WorkflowRoot_Serializes_Exact_CamelCase_Keys`
fails (it serializes under **default** options, so the pins must hold on their own).
[VERIFIED: WorkflowRootProjection.cs:9 comment; ProjectionRecordRoundTripTests.cs:26-28]

### Pitfall 4: Zero-warning gate + GenerateDocumentationFile = missing-doc CS1591
**What goes wrong:** If the new csproj sets `GenerateDocumentationFile=true` (to match
`BaseApi.Core`) **without** `NoWarn;CS1591`, every undocumented public member becomes an error
because `TreatWarningsAsErrors=true` is global.
**How to avoid:** Mirror `BaseApi.Core.csproj` exactly — `GenerateDocumentationFile=true` **and**
`<NoWarn>$(NoWarn);CS1591</NoWarn>`. The moved records already have `<summary>` docs; new types
should too, but the suppression is the safety net.
[VERIFIED: BaseApi.Core.csproj:26-27; Directory.Build.props:35]

### Pitfall 5: New project must be added to SK_P.sln with BOTH Debug AND Release config rows
**What goes wrong:** Adding only the `Project(...)`/`EndProject` block without the
`GlobalSection(ProjectConfigurationPlatforms)` rows means the project is in the solution but not
built under `dotnet build SK_P.sln -c Release` — the zero-warning Release gate would skip it.
**How to avoid:** Use `dotnet sln SK_P.sln add src/Messaging.Contracts/Messaging.Contracts.csproj`
(it writes both the project block and the 4 config rows + a fresh GUID), OR hand-edit mirroring the
existing `{A1A1…}`/`{B2B2…}` patterns (Debug/Release × ActiveCfg/Build.0). Prefer the CLI.
[VERIFIED: SK_P.sln:5-28 — each project has 4 config rows]

### Pitfall 6: `dotnet test` runs under Microsoft.Testing.Platform (xunit.v3), not VSTest
**What goes wrong:** Assuming a stale VSTest invocation; the suite is wired for MTP via
`OutputType=Exe` + `UseMicrosoftTestingPlatformRunner` + `TestingPlatformDotnetTestSupport`.
**How to avoid:** Run the existing `dotnet test` exactly as prior phases did (no special flags).
This phase adds NO test infrastructure — it only keeps the existing suite GREEN.
[VERIFIED: BaseApi.Tests.csproj:28-52]

## Code Examples

### ICorrelated (D-09) — get-only frozen vocabulary
```csharp
// Source: derived from D-09; no external API involved
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

### Start/Stop control records (D-10) — exactly Guid[] WorkflowIds, no correlation, no ICorrelated
```csharp
// Source: derived from D-10 / MSG-CONTRACTS-02
namespace Messaging.Contracts;

public sealed record StartOrchestration(Guid[] WorkflowIds);
public sealed record StopOrchestration(Guid[] WorkflowIds);
```

### Shared log-scope constant (D-11) — exact literal from CorrelationIdMiddleware.cs:52
```csharp
// Source: CorrelationIdMiddleware.cs:52  const string ItemKey = "CorrelationId";
namespace Messaging.Contracts;

/// <summary>Cross-service correlation log-scope key. MUST equal the literal
/// CorrelationIdMiddleware uses so OTel IncludeScopes serializes one Elasticsearch attribute.</summary>
public static class CorrelationKeys   // name is Claude's discretion
{
    public const string LogScope = "CorrelationId";
}
```
**Follow-up (not this phase, but flag for the planner):** `CorrelationIdMiddleware.cs:52` currently
hard-codes `"CorrelationId"`. D-11 creates the shared constant; whether Phase 17 *also* refactors
the middleware to reference it, or leaves that to Phase 18 (where the consume filter uses the same
constant), is a planning decision. The safe Phase-17-minimal read is: **add the constant**; the
middleware refactor is optional and risks touching `BaseApi.Core` for no behavior change. If the
middleware is refactored to use the constant, `BaseApi.Core` would then need a ProjectReference to
`Messaging.Contracts` — a wider dependency edge than this phase strictly requires. **Recommend:
add the constant only; do not refactor the middleware this phase.** [RESEARCH ADDITION — Open Q1]

### MassTransit CPM pins (D-12) — mirror the Npgsql cautionary block
```xml
<!-- Source: mirror Directory.Packages.props:52 (Npgsql cautionary block authorial voice) -->
<!-- MassTransit 8.5.5 is the last Apache-2.0 line. v9+ is COMMERCIAL: organizations over
     $1M revenue pay a $400/mo minimum; v8.x stays open-source + security-patched through
     end-2026. Do NOT bump to 9.x without a license decision. No PackageReference yet
     (Phase 17 = CPM pin only; the publisher/consumer wiring lands Phase 18+). -->
<PackageVersion Include="MassTransit" Version="8.5.5" />
<PackageVersion Include="MassTransit.RabbitMQ" Version="8.5.5" />
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| MassTransit fully OSS (Apache-2.0) | v9 commercial / source-available; v8 stays Apache-2.0 + patched through end-2026 | Announced April 2025; v9 GA in the v9 line | Pin v8.5.5 and add the blocking comment (D-12). A v9 bump is a licensing decision ($400/mo min for >$1M-revenue orgs), not a routine upgrade. [VERIFIED: masstransit.io v9 announcement; Milan Jovanović; antondevtips] |

**Deprecated/outdated:**
- The ROADMAP one-liner (line 27) + research SUMMARY (line 95) placing the correlation filters +
  AsyncLocal accessor in Phase 17 are **superseded** by CONTEXT D-01/D-02 (filters → Phase 18).
  Likewise ARCHITECTURE.md uses the name `WorkflowRootProjectionContract` — **superseded** by D-07
  (keep `WorkflowRootProjection`). Treat CONTEXT.md as authoritative over the older research docs.

## Validation Architecture

> nyquist_validation: treated as ENABLED (no `.planning/config.json` override found disabling it).
> This section lets the orchestrator derive VALIDATION.md (Nyquist Dimension 8).

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 `3.2.2` under Microsoft.Testing.Platform (MTP) |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (`OutputType=Exe` + `UseMicrosoftTestingPlatformRunner` + `TestingPlatformDotnetTestSupport`) |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (whole project — the suite is fast, ~28-35s historically) |
| Full suite command | `dotnet test SK_P.sln` (or the existing close-gate script that runs the suite 3× and snapshots psql/redis) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| MSG-CONTRACTS-04 | L2 root + liveness wire shape byte-identical after move (camelCase keys, null `cron`, nested liveness keys, round-trip) | unit (serialization) | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter-class *ProjectionRecordRoundTripTests` | ✅ exists — must stay GREEN through the move (using-swap only) |
| MSG-CONTRACTS-04 | Writer + cleanup still produce/consume the root after using-swap | integration | existing `RedisProjectionWriterFacts`, `HappyPathE2EFacts`, `IdempotencyFacts`, `StopCleanupFacts` | ✅ exist — regression guard for the swap |
| MSG-CONTRACTS-01 | `Messaging.Contracts` has no MassTransit/host dependency | build assertion (negative grep on the new csproj) | grep the new csproj for `MassTransit`/`AspNetCore` PackageReference/FrameworkReference → none | ❌ Wave 0 (new assertion; analogous to Plan 12-01 negative-grep) |
| MSG-CONTRACTS-01 | New assembly referenced by `BaseApi.Service` | build assertion | `dotnet list src/BaseApi.Service/BaseApi.Service.csproj reference` includes `Messaging.Contracts` | ❌ Wave 0 (assertion) |
| MSG-CONTRACTS-02 | `StartOrchestration`/`StopOrchestration` shape = exactly `Guid[] WorkflowIds`, no correlation field | unit OR reflection assertion (optional) | new tiny test asserting record shape, or rely on compile + review | ❌ Wave 0 (optional — low value; compiler + review may suffice) |
| MSG-CONTRACTS-03 | `ICorrelated` declares the six Guid get-only properties | unit OR reflection assertion (optional) | new tiny test, or rely on compile + review | ❌ Wave 0 (optional) |
| INFRA-RMQ-01 | MassTransit + MassTransit.RabbitMQ pinned at 8.5.5 with blocking comment; no PackageReference yet | build/grep assertion | grep `Directory.Packages.props` for both `PackageVersion … 8.5.5`; grep all csproj for ZERO `MassTransit` PackageReference | ❌ Wave 0 (assertion; mirrors Plan 12-01 CPM-pin + forbidden-package grep) |
| SC#5 (cross-cutting) | Zero-warning Release + Debug; v3.3.0 suite GREEN | build + full suite | `dotnet build SK_P.sln -c Release` (warnaserror) + `dotnet build -c Debug` + 3× `dotnet test` | ✅ existing gate cadence |

### Sampling Rate
- **Per task commit:** `dotnet build SK_P.sln -c Debug` (catches CS0246 missed-swap + CS1591
  missing-doc immediately) + the affected projection tests
  (`--filter-class *ProjectionRecordRoundTripTests`).
- **Per wave merge:** full `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` + zero-warning
  `dotnet build SK_P.sln -c Release`.
- **Phase gate:** 3-consecutive-GREEN full suite + zero-warning Release **and** Debug + byte-identical
  Redis (`redis-cli --scan`) and psql (`\l`) snapshots BEFORE=AFTER (the established close-gate; the
  triple-SHA rabbitmqctl arm is NOT applicable this phase — no broker is wired yet, so it's the
  existing double-snapshot gate).

### Wave 0 Gaps
- [ ] CPM-pin + forbidden-PackageReference grep assertion for MassTransit (mirror Plan 12-01's
  negative-grep pattern) — proves INFRA-RMQ-01 (pin present) AND MSG-CONTRACTS-01 (no PackageReference).
- [ ] `dotnet list reference` assertion that `BaseApi.Service` references `Messaging.Contracts`.
- [ ] New csproj dependency-ceiling grep (no `MassTransit`, no `Microsoft.AspNetCore.App`
  FrameworkReference) — proves MSG-CONTRACTS-01 / D-01.
- [ ] (Optional, low value) reflection/shape tests for `StartOrchestration`/`StopOrchestration`
  (`Guid[] WorkflowIds`, no correlation) and `ICorrelated` (six get-only Guids). The compiler +
  review already constrain these; add only if the project's discipline wants explicit proof.
- No new framework install needed — xUnit v3 stack already present.
- **No gaps for the wire-shape guard:** `ProjectionRecordRoundTripTests.cs` already covers SC#3 /
  MSG-CONTRACTS-04 and must simply stay GREEN through the using-swap.

## Security Domain

> security_enforcement treated as enabled (no config override found). This phase adds **no new
> attack surface**: pure-POCO library, no I/O, no deserialization of untrusted input introduced,
> no network/broker wiring (MassTransit is a CPM pin with no consumer).

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No auth surface touched. |
| V3 Session Management | no | No sessions. |
| V4 Access Control | no | No access-control surface. |
| V5 Input Validation | partial | The moved records are deserialized from Redis L2 (trusted, self-written) — no new untrusted-input path. The existing `WorkflowIds` validation (duplicate/empty/Guid.Empty) lives on the HTTP edge (Phase 9), unchanged. |
| V6 Cryptography | no | No crypto; the `"CorrelationId"` value is non-secret. |
| V7 Error/Logging | indirect | D-11's shared constant preserves the correlation log-join; casing drift would degrade audit traceability (availability of logs), not confidentiality. Mitigation = single shared `const`. |
| V14 Configuration | yes | MassTransit pin is a **supply-chain/licensing** control: pin v8.5.5 (Apache-2.0) + blocking comment prevents an accidental v9 (commercial + different terms) pull. CPM enforces the single pinned version. |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Wire-shape drift breaks the WebApi-writes / Orchestrator-reads contract (data-integrity, not attacker-driven) | Tampering (integrity) | Byte-identical move (D-08) + `ProjectionRecordRoundTripTests` regression guard. |
| Correlation log-key casing drift breaks cross-service audit join | Repudiation (audit availability) | Single shared `const` (D-11) referenced by all sides; no duplicate literals. |
| Accidental MassTransit v9 (commercial) pull | (supply chain / compliance) | CPM pin at 8.5.5 + blocking comment (D-12); forbidden-PackageReference grep in Wave 0. |

## Open Questions

1. **Should Phase 17 refactor `CorrelationIdMiddleware.cs:52` to consume the new D-11 constant?**
   - What we know: D-11 mandates the shared constant exists in `Messaging.Contracts`. The middleware
     currently hard-codes `"CorrelationId"`.
   - What's unclear: whether the middleware should be repointed at the constant now.
   - Recommendation: **No** — add the constant only. Repointing the middleware forces a
     `BaseApi.Core → Messaging.Contracts` ProjectReference (a wider dependency edge than the phase
     requires) for zero behavior change, and risks the zero-warning/GREEN gates on `BaseApi.Core`.
     Phase 18's consume filter will reference the same constant; let the consolidation happen there
     if desired. Flag for the planner to confirm against CONTEXT (CONTEXT D-11 says "becomes a shared
     constant," which the literal reading satisfies by *creating* it).

2. **Does `Messaging.Contracts` need `Microsoft.Extensions.Logging.Abstractions` at all?**
   - What we know: D-01 *permits* it as the dependency ceiling; the package is NOT currently in CPM.
   - What's unclear: whether any planned contract member references a logging type.
   - Recommendation: **No** — the records, `ICorrelated`, the Start/Stop POCOs, and the `const string`
     all need only `System`/`System.Text.Json.Serialization`. Add the package (and a new CPM
     `PackageVersion`) ONLY if a concrete member demands it. Keeping it out keeps the leaf maximally
     pure.

3. **Test-project reference to `Messaging.Contracts`: transitive or explicit?**
   - What we know: public types flow transitively via `BaseApi.Service`'s ProjectReference; tests
     only need the new `using`.
   - Recommendation: rely on transitive flow (minimal change). An explicit `ProjectReference` on
     `BaseApi.Tests.csproj` is acceptable if the project prefers directness; either passes the gate.

## Environment Availability

> Phase 17 is code/config-only (new project + record move + CPM pin). The build/test toolchain is
> the only external dependency, and prior phases prove it present.

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | ✓ (proven by prior phases) | 8.0.421 | — |
| RabbitMQ | nothing this phase (MassTransit is a CPM pin only, no consumer) | n/a | — | — |
| PostgreSQL / Redis | only the existing v3.3.0 suite that must stay GREEN | (used by integration tests as in prior phases) | per compose | the suite's existing fixtures |

**Missing dependencies with no fallback:** none — Phase 17 introduces no new runtime dependency.

## Sources

### Primary (HIGH confidence — direct codebase reads)
- `src/BaseApi.Service/Features/Orchestration/Projection/WorkflowRootProjection.cs`,
  `LivenessProjection.cs`, `ProcessorProjection.cs`, `StepProjection.cs`,
  `RedisProjectionWriter.cs`, `RedisL2Cleanup.cs` — the move/swap set + load-bearing attributes.
- `tests/BaseApi.Tests/Features/Orchestration/Projection/ProjectionRecordRoundTripTests.cs` +
  `HappyPathE2EFacts.cs`, `IdempotencyFacts.cs`, `StopCleanupFacts.cs`, `RedisProjectionWriterFacts.cs`
  — the complete test consumer set (Pitfall 1).
- `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs:52` — the `"CorrelationId"` literal (D-11).
- `Directory.Packages.props` (CPM + Npgsql block `:52`), `Directory.Build.props` (global props),
  `BaseApi.Core.csproj` (inheritance idiom), `BaseApi.Service.csproj`, `BaseApi.Tests.csproj`,
  `SK_P.sln` (config-row pattern), `BaseApi.Service/Properties/AssemblyInfo.cs` (InternalsVisibleTo).
- `.planning/REQUIREMENTS.md`, `.planning/ROADMAP.md` (§Phase 17 + cross-phase constraints),
  `.planning/STATE.md` (gate cadence: 3-consecutive-GREEN + byte-identical snapshots).

### Secondary (MEDIUM-HIGH — corroborated)
- MassTransit v9 commercial / v8 Apache-2.0 through end-2026: masstransit.io v9 announcement;
  milanjovanovic.tech; antondevtips.com; netmentor.es — multiple sources agree. Confirms D-12.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — SDK type, CPM mechanics, csproj idiom all verified against existing files.
- Architecture / move mechanics: HIGH — full consumer set enumerated by grep; the two CONTEXT-omitted
  consumers (`RedisL2Cleanup` + 4 extra test files) are the key research value-add.
- Pitfalls: HIGH — each is grounded in a specific verified file/line.
- MassTransit licensing: HIGH — multiple independent sources confirm the locked 8.5.5 / v9-commercial pin.

**Research date:** 2026-05-30
**Valid until:** ~2026-06-29 (stable; the only fast-moving fact, MassTransit licensing, is already
locked by CONTEXT D-12 and won't change the plan).
