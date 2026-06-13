# Phase 59: Per-Instance L2 Keyspace & Two-State Liveness Value - Pattern Map

**Mapped:** 2026-06-13
**Files analyzed:** 7 (4 new, 3 modified) + 3 test targets
**Analogs found:** 7 / 7 (all exact or strong role-matches — every new symbol mirrors an existing SoT in the same leaf)

> Pure additive contract-surface phase (mirrors shipped Phases 43 & 50). Every "value" already has an established single-source-of-truth home in `Messaging.Contracts.Projections`; the work is "add to the existing SoT in the existing style." All analogs live in the same leaf or its hermetic test slice. Read-only mapping — do NOT map writer/reader swap work (Phases 60/61).

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (MODIFY — add `PerInstance`, `InstanceIndex`) | key SoT (const builders) | transform (Guid→key string) | self (existing `Root`/`Processor`/`ExecutionData` builders) | exact |
| `src/Messaging.Contracts/Projections/ProcessorLivenessEntry.cs` (NEW — record + nested `LivenessSummary` + `Create` factory) | value record | transform (POCO→JSON wire) | `LivenessProjection.cs` + `ProcessorProjection.cs` | exact |
| `src/Messaging.Contracts/Projections/SchemaOutcome.cs` (NEW — `Success`/`Fail` consts) | const SoT | n/a (compile-time consts) | `LivenessStatus.cs` | exact |
| `src/Messaging.Contracts/Projections/LivenessStatus.cs` (MODIFY — add `Unhealthy`) | const SoT | n/a (compile-time consts) | self (existing `Healthy` const) | exact |
| `src/Messaging.Contracts/Identity/InstanceId.cs` (NEW — name/namespace TBD; resolver SoT) | resolver | transform (env→string) | `ObservabilityServiceCollectionExtensions.ResolveInstanceId` + `BaseConsoleObservabilityExtensions.ResolveInstanceId` (the two duplicated copies being hoisted) | exact (verbatim hoist) |
| `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs` (MODIFY — golden pins for new builders) | test (golden) | n/a | self (existing golden facts) | exact |
| `tests/.../ProcessorLivenessEntryFacts.cs` (NEW — shape + factory invariant) | test (shape + invariant) | n/a | `ProjectionRecordRoundTripTests.cs` + `ResolveInstanceIdFacts.cs` | exact |

## Pattern Assignments

### `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (key SoT, transform) — MODIFY

**Analog:** self — the existing builders in this same file.

**Builder style** (`L2ProjectionKeys.cs:30-48`): `const Prefix = "skp:"` owned here; every builder is `public static string` interpolating off `Prefix`. Two GUID-format precedents, both render byte-identical hyphenated lowercase ("D" format):
```csharp
public const string Prefix = "skp:";
public static string Root(Guid workflowId) => $"{Prefix}{workflowId:D}";        // explicit :D
public static string Processor(Guid processorId) => $"{Prefix}{processorId}";    // bare (still "D")
public static string ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}"; // discriminator + :D
public static string MessageIndex(Guid messageId) => $"{Prefix}msg:{messageId:D}";
```

**Discriminator precedent** (lines 42, 48): `data:` and `msg:` segments precede the GUID — the new `proc:` segment follows this exact precedent. The class XML doc (lines 9-17) is the canonical statement that GUIDs render "D"/hyphenated, NOT "N".

**Pattern to copy** (additions — use explicit `:D` matching `Root`, per RESEARCH Pitfall 2; `instanceId` is a plain `string`, NOT a Guid — resolution is the resolver's job):
```csharp
public static string PerInstance(Guid processorId, string instanceId)
    => $"{Prefix}proc:{processorId:D}:{instanceId}";

public static string InstanceIndex(Guid processorId)
    => $"{Prefix}proc:{processorId:D}";
```
Add a `<summary>` XML doc per builder matching the existing `ExecutionData`/`MessageIndex` doc style (lines 40-48). Leave `Processor(Guid)` in place (D-03; retired in 60/61).

---

### `src/Messaging.Contracts/Projections/ProcessorLivenessEntry.cs` (value record, transform) — NEW

**Analog:** `LivenessProjection.cs` (positional-record + load-bearing attribute) and `ProcessorProjection.cs` (nested-record composition).

**Imports + namespace** (`LivenessProjection.cs:1-3`):
```csharp
using System.Text.Json.Serialization;

namespace Messaging.Contracts.Projections;
```

**Load-bearing positional-record pattern** (`LivenessProjection.cs:11-14` — the canary; copy VERBATIM, attribute MUST be `[property: ...]` or STJ ignores it, RESEARCH Pitfall 1, also called out in-code on both `LivenessProjection` and `ProcessorProjection`):
```csharp
public sealed record LivenessProjection(
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("interval")]  int Interval,
    [property: JsonPropertyName("status")]    string Status);
```
Note: `ProcessorLivenessEntry` duplicates `timestamp`/`interval`/`status` from `LivenessProjection` by design (D-01) rather than reusing it — `LivenessProjection` is SHARED with the out-of-scope workflow-root path and MUST NOT be reshaped.

**Nested-record composition pattern** (`ProcessorProjection.cs:14-17` — how a record nests another record as a field, here `summary` instead of `liveness`):
```csharp
public sealed record ProcessorProjection(
    [property: JsonPropertyName("inputDefinition")]  string? InputDefinition,
    [property: JsonPropertyName("outputDefinition")] string? OutputDefinition,
    [property: JsonPropertyName("liveness")]         LivenessProjection Liveness);
```
The new record deliberately carries NO `inputDefinition`/`outputDefinition` (KEY-04) — by construction, proved by the shape test, NOT by mutating `ProcessorProjection`.

**Pattern to build** (`Create` static factory is the single STATE-01/02 invariant enforcement point — see RESEARCH §Pattern 2 lines 157-203 for the full grounded skeleton). Shape: record `ProcessorLivenessEntry(timestamp, interval, status, summary)` + nested `LivenessSummary(inputSchema, outputSchema, configSchema)`, all `[property: JsonPropertyName(...)]` lower-camel. `Create(string? inputOutcome, string? outputOutcome, string? configOutcome, DateTime ts, int interval)`: null ⇒ `SchemaOutcome.Success` (null-is-skip); any `== SchemaOutcome.Fail` ⇒ `status = LivenessStatus.Unhealthy`, else `Healthy`. Compare against the const SoT, never a literal. Positional ctor stays public (STJ deserialization needs it — Phase-61 reader) but XML-doc `Create` as the only sanctioned construction path (RESEARCH Pitfall 3, Open Q2).

---

### `src/Messaging.Contracts/Projections/SchemaOutcome.cs` (const SoT) — NEW

**Analog:** `LivenessStatus.cs:9-12`.

**Const-SoT pattern** (`LivenessStatus.cs` whole file — static class of `public const string`, no enum, no `JsonStringEnumConverter`; XML doc explains the can't-desync rationale):
```csharp
namespace Messaging.Contracts.Projections;

/// <summary>
/// Single source of truth ... Mirrors the L2ProjectionKeys / OrchestratorQueues static-const SoT shape.
/// </summary>
public static class LivenessStatus
{
    public const string Healthy = "Healthy";
}
```

**Pattern to build:** identical shape; `SchemaOutcome { Success = "SUCCESS"; Fail = "FAIL"; }`. Casing: `"SUCCESS"`/`"FAIL"` matches the ROADMAP/REQUIREMENTS prose (no existing precedent for this class). The golden/shape test pins whatever is chosen — planner must pin explicitly (RESEARCH Assumption A1).

---

### `src/Messaging.Contracts/Projections/LivenessStatus.cs` (const SoT) — MODIFY

**Analog:** self.

**Pattern to copy** (add one const beside the existing one — additive, line 11):
```csharp
public const string Healthy   = "Healthy";    // existing — consumed by heartbeat + validator
public const string Unhealthy = "Unhealthy";  // NEW (STATE-01, additive)
```
Casing decision (RESEARCH Assumption A1, **recommended (a)**): use title-case `"Unhealthy"` to match the existing `"Healthy"` — the writer/validator already serialize `"Healthy"`; prose casing is descriptive, not normative. Pin in test.

---

### `src/Messaging.Contracts/Identity/InstanceId.cs` (resolver SoT) — NEW (name/namespace TBD)

**Analog:** the two byte-identical duplicates being hoisted — `ObservabilityServiceCollectionExtensions.ResolveInstanceId` and `BaseConsoleObservabilityExtensions.ResolveInstanceId`.

**The chain to hoist VERBATIM** (`ObservabilityServiceCollectionExtensions.cs:109-113`, byte-identical to `BaseConsoleObservabilityExtensions.cs:100-104` and the test mirror `ResolveInstanceIdFacts.cs:34-38`). The `ToString("N")` fallback is LOCKED — do NOT change the format specifier:
```csharp
private static string ResolveInstanceId() =>
    Environment.GetEnvironmentVariable("POD_NAME")
    ?? Environment.GetEnvironmentVariable("HOSTNAME")
    ?? Environment.MachineName
    ?? Guid.NewGuid().ToString("N");   // MachineName effectively non-null; GUID is documented final fallback (D-10)
```

**Resolver-home decision (the ONE non-mechanical choice — D-04 / RESEARCH Open Q1).** Candidate analog locations for the planner to finalize:

| Candidate home | Reachable by all 3 callers? | Cycle risk | Notes |
|----------------|------------------------------|------------|-------|
| `Messaging.Contracts` (e.g. `Messaging.Contracts.Identity.InstanceId.Resolve()`) | YES — all four `.csproj` already reference it | NONE — zero `ProjectReference`s, BCL-only body | **RESEARCH RECOMMENDED.** Overrides the STALE in-code "Messaging.Contracts is the wrong home" comment (Phase-30 D-09), which predates the resolver becoming a cross-cutting liveness SoT. |
| `BaseConsole.Core` | NO — `BaseApi.Core` is forbidden from referencing it (D-08) | n/a | Processor path works; BaseApi.Core can't reach it. |
| 3rd duplicate copy | n/a | n/a | REJECTED — KEY-03 "reused, no new mechanism"; D-04 mandates one SoT. |

**Stale-comment caveat (RESEARCH Pitfall 5):** the "wrong home" / "duplicated, NOT test-visible" / "DRIFT GUARD (IN-03)" comment blocks in BOTH observability files (`ObservabilityServiceCollectionExtensions.cs:96-108`, `BaseConsoleObservabilityExtensions.cs:87-99`) AND the test (`ResolveInstanceIdFacts.cs:5-38`) become stale once the SoT exists. Repointing the two observability copies is OPTIONAL this phase (CONTEXT Deferred allows a later sweep); D-04's mandate is only that the SoT exists and the liveness path uses it. If repointed now, also update the three comment blocks and consider repointing the `ResolveInstanceIdFacts` mirror to call the real `Resolve()`.

---

### `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs` (test, golden) — MODIFY

**Analog:** self.

**Golden-pin mechanics** (`L2ProjectionKeysTests.cs:17-44` — shared static `Guid` fixtures parsed from repeating-digit GUIDs; one `[Fact]` per builder, `Assert.Equal(exact-literal-string, builder(...))`):
```csharp
private static readonly Guid Processor = Guid.Parse("33333333-3333-3333-3333-333333333333");

[Fact]
public void Processor_Produces_Prefix_Plus_HyphenatedGuid()
{
    Assert.Equal("skp:33333333-3333-3333-3333-333333333333", L2ProjectionKeys.Processor(Processor));
}
```

**Pattern to add** (mirror style — see RESEARCH §"Golden key-string pins" lines 332-353 for the full grounded skeleton): a representative resolved `instanceId` string const (e.g. `"pod-abc-123"`), golden pins for `PerInstance` → `skp:proc:33333333-...:pod-abc-123` and `InstanceIndex` → `skp:proc:33333333-...`, plus a `StartsWith(InstanceIndex(p) + ":", PerInstance(p, i))` prefix-relationship fact (forward-fit for the Phase-61 SMEMBERS→GET relationship).

---

### `tests/.../ProcessorLivenessEntryFacts.cs` (test, shape + invariant) — NEW

**Analogs:** `ProjectionRecordRoundTripTests.cs` (serialize-the-record-under-DEFAULT-options discipline) and `ResolveInstanceIdFacts.cs` (env-mutation hermetic facts, if resolver facts land here).

**Serialize-the-record / field-presence pattern** (`ProjectionRecordRoundTripTests.cs:30, 107-119` — uses DEFAULT `JsonSerializerOptions` so the `[JsonPropertyName]` pins must hold on their own; asserts exact field names via `Assert.Contains("\"name\":", json)` and absence via `Assert.DoesNotContain`):
```csharp
private static readonly JsonSerializerOptions Default = new();

[Fact]
public void ProcessorProjection_Serializes_Null_InputDefinition_With_Exact_Field_Name()
{
    var proc = new ProcessorProjection(InputDefinition: null, OutputDefinition: "{\"type\":\"object\"}", Liveness: Liveness());
    var json = JsonSerializer.Serialize(proc, Default);
    Assert.Contains("\"inputDefinition\":null", json);
    Assert.DoesNotContain("definitionIn", json);
}
```

**Pattern to build:**
- Shape test (KEY-04): construct via `Create`, serialize, assert `inputDefinition`/`outputDefinition` ABSENT and `timestamp`/`interval`/`status`/`summary.inputSchema` present (RESEARCH lines 356-379, uses `JsonDocument.Parse` + `TryGetProperty` — match the DEFAULT-options discipline above).
- Factory-invariant theory (STATE-01/02): `[Theory]`/`[InlineData]` covering all-null⇒Healthy, all-SUCCESS⇒Healthy, any-FAIL⇒Unhealthy, null-mixed-with-FAIL⇒Unhealthy; assert `entry.Status` + null-is-skip surfaces as `Success` in `summary` (RESEARCH lines 382-398). Use the `SchemaOutcome`/`LivenessStatus` consts in assertions, literals only inside `[InlineData]`.
- Resolver facts (KEY-03): either a new `InstanceIdResolverFacts` against the real `Resolve()`, or repoint `ResolveInstanceIdFacts` (decide alongside Open Q1). If env-mutating, copy the `[Collection("Observability")]` + try/finally restore discipline from `ResolveInstanceIdFacts.cs:26-99`.

## Shared Patterns

### Single-source-of-truth const/record discipline
**Sources:** `L2ProjectionKeys.cs`, `LivenessStatus.cs`, `ProcessorProjection.cs`
**Apply to:** every new symbol this phase
One shared definition in the `Messaging.Contracts` leaf consumed by both (future) writer and reader so field names/key strings/status values cannot desync. The XML doc on each existing SoT states this rationale explicitly (e.g. `LivenessStatus.cs:5-7`, `L2ProjectionKeys.cs:4-7`) — replicate the rationale-doc style.

### Load-bearing `[property: JsonPropertyName]` on positional records
**Source:** `LivenessProjection.cs:8-14`, `ProcessorProjection.cs:8-17`
**Apply to:** `ProcessorLivenessEntry` + `LivenessSummary`
On a positional record the attribute MUST target the property (`[property: ...]`) or STJ binds it to the ctor parameter and silently ignores it (Pitfall 1). The shape test asserting lower-camel keys is the canary.

### Hermetic test discipline (no real stack)
**Source:** `L2ProjectionKeysTests.cs`, `ProjectionRecordRoundTripTests.cs`, `ResolveInstanceIdFacts.cs`
**Apply to:** all Phase-59 tests
Golden string pins + in-process `JsonSerializer.Serialize` under DEFAULT options + env-mutation-with-finally-restore. No Redis/RabbitMQ/Postgres. `[Trait("Phase", "59")]` on new test classes (matching the `[Trait("Phase", ...)]` convention on the analogs).

### 0-warning additive build (SC-5)
**Source:** `Directory.Build.props` (net8.0, Nullable enable, `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild`, `NoWarn CS1591` so missing XML docs do NOT warn)
**Apply to:** all new `.cs` files
Mirror the existing files' brace/spacing/`using` style exactly; no unused `private` members (CS0169/IDE0051); record reference types are non-null by default (no CS86xx); resolver fallbacks are non-null. Build BOTH Release and Debug with `-warnaserror`.

## No Analog Found

None. Every new symbol mirrors an existing SoT in the same `Messaging.Contracts.Projections` leaf (or its `Identity` sibling) and every test mirrors an existing hermetic fact class. The only open judgment is the resolver-home placement (D-04 / Open Q1), captured above with candidate analog locations — not a missing-analog gap.

## Metadata

**Analog search scope:** `src/Messaging.Contracts/Projections/`, `src/Messaging.Contracts/` (Identity sibling), `src/BaseApi.Core/DependencyInjection/`, `src/BaseConsole.Core/DependencyInjection/`, `tests/BaseApi.Tests/Features/Orchestration/Projection/`, `tests/BaseApi.Tests/Observability/`
**Files scanned/read:** 8 source/test analogs read in full (all ≤180 lines, single-pass) + glob/grep to locate the shape-test analog
**Pattern extraction date:** 2026-06-13
