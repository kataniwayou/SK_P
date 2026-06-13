# Phase 59: Per-Instance L2 Keyspace & Two-State Liveness Value - Research

**Researched:** 2026-06-13
**Domain:** Contract-surface / vocabulary-shape reshape in the `Messaging.Contracts` leaf (Redis L2 key builders + a liveness-only value record + a shared instanceId resolver) — additive, no consumer touched.
**Confidence:** HIGH (entire phase is grounded in the actual current source; no external/library uncertainty)

## Summary

Phase 59 is a pure additive contract-surface phase, identical in shape to the shipped Phases 43 and 50: add new symbols to the `Messaging.Contracts.Projections` leaf and pin them with hermetic tests, while leaving every consumer (the Phase-60 heartbeat writer, the Phase-61 WebAPI reader) untouched on the old path. Nothing is deleted this phase — the old `L2ProjectionKeys.Processor(Guid)` and `ProcessorProjection` are retired in 60/61 when their last callers move.

Four new things land: (1) two key builders — `PerInstance(Guid,string)` → `skp:proc:{processorId}:{instanceId}` and `InstanceIndex(Guid)` → `skp:proc:{processorId}` — added to `L2ProjectionKeys`; (2) a new liveness-only positional record `ProcessorLivenessEntry { timestamp, interval, status, summary }` with a nested `summary { inputSchema, outputSchema, configSchema }`, deliberately carrying NO `inputDefinition`/`outputDefinition`; (3) a `SchemaOutcome` string-const SoT (`Success`/`Fail`) plus `LivenessStatus.Unhealthy` added additively; (4) a shared `instanceId` resolver that hoists the byte-identical `POD_NAME → HOSTNAME → MachineName → GUID` chain currently duplicated in two observability extensions.

The single non-mechanical decision is **where the shared instanceId resolver lives (D-04)**. There is a load-bearing tension: the existing code comments (Phase-30 D-09) in BOTH observability files explicitly state "`Messaging.Contracts` is the wrong home" and "`BaseConsole.Core` is hard-forbidden from referencing `BaseApi.Core`." Phase 59's D-04 changes that calculus — the resolver is now a cross-cutting SoT the *liveness path* needs too, not just a 6-line OTel helper. The dependency graph makes `Messaging.Contracts` the *only* assembly all three callers (BaseApi.Core, BaseConsole.Core, BaseProcessor.Core) already reference, and the resolver uses only BCL (`Environment`/`Guid`) — zero new dependencies, zero cycle risk. See "Open Questions" for the recommended resolution and the stale-comment cleanup it implies.

**Primary recommendation:** Mirror the existing `L2ProjectionKeys` interpolation style and golden-test mechanics exactly; author the new record as a positional record with load-bearing `[property: JsonPropertyName(...)]`; enforce the STATE-01/02 invariant in a single static factory; and place the shared resolver in `Messaging.Contracts` (the only no-cycle leaf all callers reference), updating the now-stale "wrong home" comments. Repointing the two existing observability copies is optional this phase (a separate dedupe sweep is acceptable per the Deferred section), but the SoT must exist and the liveness path must use it.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| L2 key string format (`PerInstance`, `InstanceIndex`) | `Messaging.Contracts` leaf (`L2ProjectionKeys`) | — | Existing key SoT both writer (Phase 60) and reader (Phase 61) consume; a future format change cannot desync them. |
| Liveness value wire shape (`ProcessorLivenessEntry` + `summary`) | `Messaging.Contracts` leaf (Projections) | — | Shared POCO; writer serializes it, reader deserializes it; one type = can't-desync field names (same rationale as `ProcessorProjection`). |
| Two-state status / per-schema outcome consts | `Messaging.Contracts` leaf (`LivenessStatus`, new `SchemaOutcome`) | — | String-const SoT discipline (mirrors `LivenessStatus.Healthy`, `OrchestratorQueues`). |
| `status` ⇐ `summary` invariant (any Fail ⇒ Unhealthy; null ⇒ Success) | `Messaging.Contracts` leaf (static factory on the record) | — | Single enforcement point so neither writer nor any future caller can produce a contradictory `status`. |
| instanceId resolution (`POD_NAME → HOSTNAME → MachineName → GUID`) | `Messaging.Contracts` leaf (new shared resolver) — recommended | (current: duplicated in BaseApi.Core + BaseConsole.Core) | Only assembly all three callers reference without a cycle; BCL-only body = no new deps. See Open Q1. |
| Gate A → `configSchema` plumbing | **OUT OF SCOPE (Phase 60 writer)** | — | Phase 59 only defines the *field + factory mapping*; actual `IProcessorContext.ConfigSchemaId/ConfigDefinition` → `summary` wiring is Phase 60. |

## Standard Stack

This is an in-repo C# (.NET 8) contract phase. No new NuGet packages, no version research needed — `Messaging.Contracts` is a pure-POCO leaf with zero `PackageReference`s and only `System.Text.Json.Serialization` (BCL) in use.

### Core (existing, reused)
| Symbol | Location | Purpose | Why Standard |
|--------|----------|---------|--------------|
| `L2ProjectionKeys` (static class) | `Messaging.Contracts/Projections/L2ProjectionKeys.cs` | Key-string SoT; `const Prefix = "skp:"` | Extended here with the two new builders. |
| `LivenessStatus` (static class) | `Messaging.Contracts/Projections/LivenessStatus.cs` | Status string-const SoT (`Healthy`) | Add `Unhealthy` additively. |
| `[property: JsonPropertyName]` on positional records | `LivenessProjection.cs`, `ProcessorProjection.cs` | STJ JSON name binding | Load-bearing pattern reused verbatim for the new record. |
| `System.Text.Json.Serialization` (BCL) | — | Serialization attributes | Already the only dependency the projection records use. |

### Supporting (new symbols to add)
| Symbol | Where | Purpose |
|--------|-------|---------|
| `L2ProjectionKeys.PerInstance(Guid, string)` | extend existing file | `skp:proc:{processorId}:{instanceId}` |
| `L2ProjectionKeys.InstanceIndex(Guid)` | extend existing file | `skp:proc:{processorId}` (the index SET key) |
| `ProcessorLivenessEntry` (sealed positional record) | new file in `Projections/` | `{ timestamp, interval, status, summary }` |
| `LivenessSummary` (sealed positional record) | new file (or nested) | `{ inputSchema, outputSchema, configSchema }` |
| `SchemaOutcome` (static class) | new file in `Projections/` | `Success` / `Fail` string consts |
| `LivenessStatus.Unhealthy` (const) | extend existing file | `"Unhealthy"` |
| instanceId resolver (static method/class) | `Messaging.Contracts` (recommended — Open Q1) | hoisted `POD_NAME → HOSTNAME → MachineName → GUID` |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| New `ProcessorLivenessEntry` record | Reshape `ProcessorProjection` in place | REJECTED by D-01 — `ProcessorProjection` nests the SHARED `LivenessProjection` used by the out-of-scope workflow-root path; reshaping ripples into out-of-scope code. New record fully isolates. |
| String-const `SchemaOutcome`/`LivenessStatus` | C# `enum` + `JsonStringEnumConverter` | REJECTED by D-02 — repo discipline is string-const SoT (no enum-converter wiring, JSON-stable strings, can't-desync). |
| Resolver in `Messaging.Contracts` | Resolver in BaseConsole.Core, or 3rd copy | See Open Q1 — `Messaging.Contracts` is the only no-cycle home all three callers reference. |

**Installation:** None. No `dotnet add package`. The new files inherit all build props from `Directory.Build.props` (net8.0, Nullable enable, `TreatWarningsAsErrors=true`).

**Version verification:** N/A — no external packages added this phase. `Messaging.Contracts.csproj` confirmed to have zero `PackageReference` entries (pure POCO leaf).

## Architecture Patterns

### System Architecture Diagram

```
                         Phase 59 ADDS (additive surface — nothing wired yet)
                         ───────────────────────────────────────────────────

  ┌─────────────────────────── Messaging.Contracts (leaf, zero project deps) ───────────────────────────┐
  │                                                                                                       │
  │   L2ProjectionKeys (static SoT)              ProcessorLivenessEntry (new record)                      │
  │   ├ Prefix = "skp:"  (existing)              ├ timestamp : DateTime   [property: timestamp]           │
  │   ├ Processor(Guid)  (existing, superseded)  ├ interval  : int        [property: interval]            │
  │   ├ PerInstance(Guid procId, string instId)  ├ status    : string     [property: status]   ◀─derived─┐│
  │   │     → "skp:proc:{procId}:{instId}"        └ summary   : LivenessSummary [property: summary]       ││
  │   └ InstanceIndex(Guid procId)                     ├ inputSchema  : string [property: inputSchema]   ││
  │         → "skp:proc:{procId}"                       ├ outputSchema : string [property: outputSchema] ││
  │                                                      └ configSchema : string [property: configSchema]││
  │   SchemaOutcome (static SoT)                        (NO inputDefinition / outputDefinition — KEY-04) ││
  │   ├ Success = "SUCCESS"                                                                              ││
  │   └ Fail    = "FAIL"                          static factory:                                        ││
  │                                                ProcessorLivenessEntry.Create(                        ││
  │   LivenessStatus (static SoT)                    string? inputOutcome, string? outputOutcome,        ││
  │   ├ Healthy   = "Healthy" (existing)             string? configOutcome, DateTime ts, int interval)   ││
  │   └ Unhealthy = "Unhealthy" (NEW)                  ── any Fail ⇒ status=Unhealthy ────────────────────┘│
  │                                                    ── null ⇒ Success (null-is-skip) ──────────────────┘
  │   InstanceIdResolver (NEW, BCL-only):  POD_NAME ?? HOSTNAME ?? MachineName ?? Guid.NewGuid().ToString("N")
  └───────────────────────────────────────────────────────────────────────────────────────────────────┘
        ▲ referenced by (existing project graph — no edges added)        ▲ consumed LATER (not this phase)
        │                                                                │
   BaseApi.Core ───┐   BaseConsole.Core ──┐   BaseProcessor.Core ───────┘   Phase 60 writer  /  Phase 61 reader
   (OTel ext)      │   (OTel ext)         │   (refs Console + Contracts)     inject resolver,   SMEMBERS index,
   currently has   │   currently has      │                                   write PerInstance   GET each key
   own copy ───────┘   own copy ──────────┘                                   + summary           (out of scope)

   UNCHANGED THIS PHASE (old path stays live until 60/61 swap):
   ProcessorLivenessHeartbeat ──writes──▶ skp:{id}  via  L2ProjectionKeys.Processor(id)   [Phase-60 swap target]
   ProcessorLivenessValidator ──reads───▶ skp:{id}  via  RedisProjectionKeys.Processor(id) [Phase-61 swap target]
```

### Component Responsibilities

| File | Responsibility this phase |
|------|---------------------------|
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | Add `PerInstance` + `InstanceIndex` builders (edit existing). |
| `src/Messaging.Contracts/Projections/LivenessStatus.cs` | Add `Unhealthy` const (edit existing). |
| `src/Messaging.Contracts/Projections/SchemaOutcome.cs` | New string-const SoT (`Success`/`Fail`). |
| `src/Messaging.Contracts/Projections/ProcessorLivenessEntry.cs` | New record + nested `LivenessSummary` + static factory. |
| `src/Messaging.Contracts/.../InstanceIdResolver.cs` (name TBD) | New shared resolver (recommended home — Open Q1). |
| `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs` | Add golden pins for the two new builders (extend existing). |
| `tests/BaseApi.Tests/...` (new file) | Shape test (definitions absent) + factory invariant tests. |

### Pattern 1: Key builder — interpolation style (mirror exactly)
**What:** `L2ProjectionKeys` builds keys by string interpolation off `const Prefix = "skp:"`. **Critical GUID-format detail:** the existing builders are INCONSISTENT in their format specifier and the golden tests pin the *rendered* hyphenated string either way:
- `Root(Guid)` uses explicit `:D` → `$"{Prefix}{workflowId:D}"`
- `Processor(Guid)` uses a BARE interpolation → `$"{Prefix}{processorId}"`

Both render byte-identical hyphenated lowercase GUIDs (`Guid.ToString()` defaults to "D"). The class XML doc states this explicitly: *"GUIDs render in the default `Guid.ToString()` (\"D\") format — hyphenated — NOT the \"N\" (32-digit) format."* The golden test `Processor_Produces_Prefix_Plus_HyphenatedGuid` pins `skp:33333333-...-333333333333`.

**When to use:** Always — the new builders MUST produce hyphenated "D"-format GUIDs to stay consistent and golden-pinnable.

**Example (the two new builders — recommend explicit `:D` for clarity, matching `Root`):**
```csharp
// Source: src/Messaging.Contracts/Projections/L2ProjectionKeys.cs (mirror existing style)
// instanceId is a plain string (already resolved by the resolver — D-04: "resolution is the
// resolver's job, not the builder's"). It is NOT a Guid; the POD_NAME/HOSTNAME chain yields a
// string, only the final fallback is a GUID rendered ToString("N").
public static string PerInstance(Guid processorId, string instanceId)
    => $"{Prefix}proc:{processorId:D}:{instanceId}";

public static string InstanceIndex(Guid processorId)
    => $"{Prefix}proc:{processorId:D}";
```
Note the new `proc:` discriminator segment — it distinguishes the new per-instance scheme from the old flat `skp:{processorId}` (and from `Root`/`Step`/`data:`/`msg:`). This is intentional and consistent with the existing `data:`/`msg:` discriminator precedent (`ExecutionData`, `MessageIndex`). The ROADMAP SC-1 and CONTEXT both lock the literal strings `skp:proc:{processorId}:{instanceId}` and `skp:proc:{processorId}`.

### Pattern 2: Positional record with load-bearing `[property: JsonPropertyName]`
**What:** On a C# positional record, a bare `[JsonPropertyName(...)]` binds to the *constructor parameter* and STJ IGNORES it — the attribute MUST target the property via `[property: JsonPropertyName(...)]`. This is documented as "RESEARCH Pitfall 1" in both `LivenessProjection.cs` and `ProcessorProjection.cs`. Names are explicit lower-camel.

**When to use:** Every new record this phase.

**Example (the new value record + nested summary):**
```csharp
// Source: pattern from src/Messaging.Contracts/Projections/LivenessProjection.cs + ProcessorProjection.cs
using System.Text.Json.Serialization;

namespace Messaging.Contracts.Projections;

/// <summary>
/// L2 per-INSTANCE processor-liveness value (KEY-04 / STATE-01 / STATE-02) for the
/// skp:proc:{processorId}:{instanceId} key. Liveness-only by construction: it carries NO
/// inputDefinition/outputDefinition (dropped from L2 — the processor validates against its own
/// in-memory L1 copy). Isolated from the SHARED LivenessProjection (D-01) so the out-of-scope
/// workflow-root path is untouched. [property: JsonPropertyName] is load-bearing (Pitfall 1).
/// status is DERIVED from summary via Create(...) — never set independently (STATE-01/02 invariant).
/// </summary>
public sealed record ProcessorLivenessEntry(
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("interval")]  int Interval,
    [property: JsonPropertyName("status")]    string Status,
    [property: JsonPropertyName("summary")]   LivenessSummary Summary)
{
    /// <summary>
    /// Single enforcement point for the STATE-01/02 invariant. Each per-schema outcome is the
    /// SchemaOutcome string (or null = schema id absent = null-is-skip ⇒ Success). ANY Fail ⇒
    /// status = LivenessStatus.Unhealthy; otherwise Healthy. The Phase-60 writer feeds Gate-A /
    /// schema-resolution outcomes in and CANNOT produce a status that contradicts the summary.
    /// </summary>
    public static ProcessorLivenessEntry Create(
        string? inputOutcome,
        string? outputOutcome,
        string? configOutcome,
        DateTime timestamp,
        int interval)
    {
        // null-is-skip: a null schema id is not-failing ⇒ Success (D-02 / D-02a; STATE-02).
        var input  = inputOutcome  ?? SchemaOutcome.Success;
        var output = outputOutcome ?? SchemaOutcome.Success;
        var config = configOutcome ?? SchemaOutcome.Success;

        var summary = new LivenessSummary(input, output, config);

        // any FAIL ⇒ Unhealthy (STATE-02 / SC-3). Compare against the SchemaOutcome const SoT,
        // never a literal "FAIL".
        var anyFail = input  == SchemaOutcome.Fail
                   || output == SchemaOutcome.Fail
                   || config == SchemaOutcome.Fail;

        var status = anyFail ? LivenessStatus.Unhealthy : LivenessStatus.Healthy;

        return new ProcessorLivenessEntry(timestamp, interval, status, summary);
    }
}

/// <summary>
/// Per-schema liveness summary (STATE-02): each field is a SchemaOutcome string (SUCCESS|FAIL).
/// configSchema is the v6.0.0 Gate A startup config-compat outcome (D-02a — never recomputed;
/// a null ConfigSchemaId ⇒ Success/null-is-skip). [property: JsonPropertyName] is load-bearing.
/// </summary>
public sealed record LivenessSummary(
    [property: JsonPropertyName("inputSchema")]  string InputSchema,
    [property: JsonPropertyName("outputSchema")] string OutputSchema,
    [property: JsonPropertyName("configSchema")] string ConfigSchema);
```

### Pattern 3: String-const SoT (mirror `LivenessStatus`)
**What:** A static class of `public const string` values, one shared definition both writer and reader consume so they cannot desync. No enum, no `JsonStringEnumConverter`.

**Example:**
```csharp
// Source: pattern from src/Messaging.Contracts/Projections/LivenessStatus.cs
namespace Messaging.Contracts.Projections;

/// <summary>
/// Single source of truth for the per-schema liveness summary outcome (STATE-02 / D-02).
/// Mirrors the LivenessStatus / L2ProjectionKeys static-const SoT shape. Consumed by
/// LivenessSummary's fields and by ProcessorLivenessEntry.Create's invariant.
/// </summary>
public static class SchemaOutcome
{
    public const string Success = "SUCCESS";
    public const string Fail    = "FAIL";
}
```
```csharp
// And the additive change to the existing LivenessStatus.cs:
public static class LivenessStatus
{
    public const string Healthy   = "Healthy";    // existing — consumed by heartbeat + validator
    public const string Unhealthy = "Unhealthy";  // NEW (STATE-01, additive)
}
```
**Casing note (decide and pin):** The ROADMAP/REQUIREMENTS prose uses lowercase `healthy`/`unhealthy` and uppercase `SUCCESS`/`FAIL`, but the *existing* `LivenessStatus.Healthy` const is the title-case string `"Healthy"`. Two valid readings:
- (a) Keep `Unhealthy = "Unhealthy"` to match the existing title-case `Healthy` (consistency within the const SoT). **Recommended** — the existing writer/validator already serialize `"Healthy"`, and the prose casing is descriptive not normative.
- (b) Use lowercase to match prose.
This is a `[ASSUMED]` casing choice; the *golden/shape test will pin whatever is chosen*, so the planner must pick one explicitly. Recommend (a) for SoT consistency; `SchemaOutcome` has no existing precedent so `"SUCCESS"/"FAIL"` (matching prose) is the natural pick.

### Pattern 4: Shared instanceId resolver (hoist the duplicated chain)
**What:** The exact expression duplicated byte-for-byte in three places today (`ObservabilityServiceCollectionExtensions.ResolveInstanceId`, `BaseConsoleObservabilityExtensions.ResolveInstanceId`, and the test mirror `ResolveInstanceIdFacts.Resolve`). Body is BCL-only.

**Example (preserve byte-identically — the `ToString("N")` fallback is locked):**
```csharp
// Source: src/BaseApi.Core/.../ObservabilityServiceCollectionExtensions.cs:109-113 (verbatim chain)
namespace Messaging.Contracts;   // or Messaging.Contracts.Identity — planner's call

public static class InstanceId   // name TBD (Claude's discretion)
{
    /// <summary>
    /// Per-replica instance identity: POD_NAME → HOSTNAME → MachineName → GUID (KEY-03).
    /// The GUID final fallback renders via ToString("N") — byte-identical to the two existing
    /// observability copies (DO NOT change the format specifier).
    /// </summary>
    public static string Resolve() =>
        Environment.GetEnvironmentVariable("POD_NAME")
        ?? Environment.GetEnvironmentVariable("HOSTNAME")
        ?? Environment.MachineName
        ?? Guid.NewGuid().ToString("N");
}
```
**Cycle check:** `Messaging.Contracts` has zero `ProjectReference`s and the body uses only `System.Environment`/`System.Guid` (BCL). BaseApi.Core, BaseConsole.Core, and BaseProcessor.Core all ALREADY reference `Messaging.Contracts` (verified in all four csproj files). No new edge, no cycle, no new dependency. See Open Q1 for the stale-comment caveat.

### Anti-Patterns to Avoid
- **Bare `[JsonPropertyName]` on a positional record** — STJ silently ignores it (Pitfall 1). Always `[property: ...]`.
- **Setting `status` independently of `summary`** — defeats STATE-01/02. The ONLY constructor path that sets status is `Create(...)`. Consider whether to make the positional ctor less prominent (it must stay public for STJ deserialization, but callers should be steered to `Create`).
- **Rendering the GUID in `PerInstance`/`InstanceIndex` as `"N"`** — the keyspace uses hyphenated "D" GUIDs (golden-pinned); `"N"` is only for the instanceId *fallback string*, not the processorId key segment.
- **A third copy of the resolver chain** — KEY-03 says "reused, no new mechanism"; D-04 says hoist to one SoT. Do not add a fourth duplicate.
- **Touching `LivenessProjection`** — it is SHARED with the out-of-scope workflow-root path. New record is fully separate.
- **Deleting `Processor(Guid)` or `ProcessorProjection`** — deferred to Phase 60/61 (D-03). Deleting now forces consumer changes and breaks the additive posture.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Per-schema outcome ↔ status consistency | Ad-hoc `status` assignment at each write site | The single `ProcessorLivenessEntry.Create(...)` factory | One enforcement point; writer can't desync status from summary (STATE-01/02). |
| JSON field naming | Manual JSON string assembly | `[property: JsonPropertyName]` + `JsonSerializer.Serialize` | The existing seeding tests serialize the RECORD precisely to avoid camelCase drift (see `SeedLivenessAsync`). |
| instanceId resolution | New env-var lookup per call site | The hoisted shared resolver | KEY-03 "reused, no new mechanism"; removes existing triple-duplication. |
| Key string format | Inline `$"skp:..."` literals at call sites | `L2ProjectionKeys.PerInstance/InstanceIndex` | Key SoT — a format change can't silently desync writer/reader. |

**Key insight:** Every "value" in this domain (key strings, status strings, outcome strings, the value shape) already has an established single-source-of-truth home in the leaf. Phase 59 is entirely "add to the existing SoT in the existing style," not "design something new."

## Runtime State Inventory

> Phase 59 is a **pure additive contract-surface** phase — it adds new symbols and writes nothing to any runtime system. The keyspace migration (old `skp:{id}` → new `skp:proc:{id}:{instId}`) is exercised by the Phase-60 writer / Phase-61 reader, NOT here. This inventory is included because the *milestone* is a keyspace reshape; for Phase 59 specifically every category is "nothing this phase."

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None this phase — Phase 59 writes no Redis keys. (Milestone-level: the old `skp:{processorId}` String keys are written by `ProcessorLivenessHeartbeat` and read by `ProcessorLivenessValidator`; they migrate to `skp:proc:{id}:{instId}` + the `skp:proc:{id}` SET index in **Phase 60/61**, not 59.) | None in 59. |
| Live service config | None — no service config embeds these new symbols yet (no writer/reader wired until 60/61). | None in 59. |
| OS-registered state | None — no OS registration references the liveness keyspace. | None in 59. |
| Secrets/env vars | `POD_NAME` / `HOSTNAME` are READ by the resolver (already read today by both observability extensions); no env var is renamed or added. Behavior is byte-identical to the existing copies. | None — verified: resolver only reads existing env vars; no new var introduced. |
| Build artifacts / installed packages | None — no package rename, no egg-info/binary analog in this .NET solution; new `.cs` files compile into the existing `Messaging.Contracts.dll`. The downstream test assembly `BaseApi.Tests` picks up new types automatically. | None in 59. |

**Canonical question — "after Phase 59 lands, what runtime systems still have the old string cached/stored/registered?"** Answer: ALL of them — by design (D-03 additive posture). The old `skp:{processorId}` keys keep being written/read by the untouched heartbeat/validator until Phase 60/61. Phase 59 changes ZERO runtime behavior; it only makes new symbols *available*.

## Common Pitfalls

### Pitfall 1: Bare `[JsonPropertyName]` on a positional record (STJ silently ignores it)
**What goes wrong:** JSON comes out PascalCase (`Status`, `Summary`) instead of the intended lower-camel, silently breaking the wire contract the Phase-60/61 consumers expect.
**Why it happens:** On a positional record the attribute defaults to targeting the constructor parameter, not the generated property; STJ only reads property-targeted attributes.
**How to avoid:** Always `[property: JsonPropertyName("...")]`. This is documented in-code as "RESEARCH Pitfall 1" on `LivenessProjection`/`ProcessorProjection` — copy the exact pattern.
**Warning signs:** A serialization/shape test asserting the lowercase key names is the canary — it will fail immediately if the attribute target is wrong.

### Pitfall 2: GUID format inconsistency in the new key builders
**What goes wrong:** Using `ToString("N")` or a bare interpolation that renders differently from `Root`'s `:D`, producing keys that don't match the golden-pinned hyphenated form.
**Why it happens:** The existing builders mix `:D` (`Root`) and bare (`Processor`) — both happen to render "D", but copying the bare form without understanding can invite an "N" "cleanup."
**How to avoid:** Use explicit `:D` in both new builders (matches `Root`, self-documents). Golden-pin the exact hyphenated string.
**Warning signs:** Golden test mismatch on the GUID segment.

### Pitfall 3: status / summary desync
**What goes wrong:** A future writer sets `status = Healthy` but `summary.configSchema = FAIL` (or vice versa), so the gate admits an unhealthy replica.
**Why it happens:** If the record exposes only the positional ctor, callers set both independently.
**How to avoid:** Funnel all construction through `Create(...)`; add a factory invariant test (Fail ⇒ Unhealthy; null ⇒ Success). The positional ctor stays public ONLY because STJ deserialization needs it.
**Warning signs:** Factory unit test failing, or a code review spotting `new ProcessorLivenessEntry(...)` outside deserialization/factory.

### Pitfall 4: 0-warning break from the new public symbols
**What goes wrong:** Build fails under `TreatWarningsAsErrors=true`.
**Why it happens:** `Messaging.Contracts.csproj` sets `GenerateDocumentationFile=true` with `<NoWarn>$(NoWarn);CS1591</NoWarn>` — so *missing XML docs do NOT warn* (CS1591 is suppressed). Real risks instead: (a) an unused `private` member (CS0169/IDE0051) — avoid by not adding dead helpers; (b) a nullable annotation mismatch (CS86xx) — the records are non-null reference types by default, fine; (c) analyzer style errors (`EnforceCodeStyleInBuild=true`, `AnalysisMode=latest`) — match the existing file's brace/spacing/`using` style exactly. The resolver's `Guid.NewGuid().ToString("N")` is non-null; `Environment.MachineName` is non-null — no CS8603.
**How to avoid:** Mirror the existing files' style; no unused privates; build Release AND Debug (SC-5 requires both).
**Warning signs:** `dotnet build -warnaserror` red on the new files.

### Pitfall 5: Repointing the observability copies introduces a hidden coupling (only if you do the dedupe now)
**What goes wrong:** If the planner *also* repoints `ObservabilityServiceCollectionExtensions`/`BaseConsoleObservabilityExtensions` to the new shared resolver this phase, the `ResolveInstanceIdFacts` test (which mirrors the chain locally) and the in-code "DRIFT GUARD (IN-03)" / "duplicated, NOT test-visible" / "Messaging.Contracts is the wrong home" comments become stale/misleading.
**Why it happens:** Three places are documented to "change in lock-step"; collapsing them to one SoT obsoletes the lock-step guard.
**How to avoid:** Per the CONTEXT Deferred section, repointing the two observability copies is OPTIONAL this phase — D-04's mandate is only that the SoT exists and the LIVENESS path uses it. **Recommended:** create the SoT + have the (Phase-60) liveness path target it; if dedup is done now, also update the three stale comment blocks and consider repointing `ResolveInstanceIdFacts` to call the real resolver. If deferred, leave a note that the SoT and the two copies temporarily coexist.
**Warning signs:** Comments still claiming "duplicated" / "wrong home" after a dedupe; a drift-guard test that no longer guards anything real.

## Code Examples

See Patterns 1–4 above for the four concrete skeletons (key builders, value record + factory, outcome const SoT, resolver). One more — the golden-test extension mirroring the existing mechanics:

### Golden key-string pins (extend the existing test class verbatim style)
```csharp
// Source: tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs (mirror)
private const string Instance = "pod-abc-123";   // a representative resolved instanceId string

[Fact]
public void PerInstance_Produces_Prefix_Proc_Processor_Colon_Instance()
    => Assert.Equal(
        "skp:proc:33333333-3333-3333-3333-333333333333:pod-abc-123",
        L2ProjectionKeys.PerInstance(Processor, Instance));

[Fact]
public void InstanceIndex_Produces_Prefix_Proc_Processor()
    => Assert.Equal(
        "skp:proc:33333333-3333-3333-3333-333333333333",
        L2ProjectionKeys.InstanceIndex(Processor));

[Fact]
public void PerInstance_Is_Prefixed_By_Its_InstanceIndex()   // the SMEMBERS→GET relationship (Phase-61 forward-fit)
    => Assert.StartsWith(
        L2ProjectionKeys.InstanceIndex(Processor) + ":",
        L2ProjectionKeys.PerInstance(Processor, Instance));
```

### Shape test — prove definitions are ABSENT (SC-3)
```csharp
// New test file. Asserts the new record has NO inputDefinition/outputDefinition keys by construction.
[Fact]
public void ProcessorLivenessEntry_Json_Has_No_Definition_Fields()
{
    var entry = ProcessorLivenessEntry.Create(
        inputOutcome: SchemaOutcome.Success,
        outputOutcome: SchemaOutcome.Success,
        configOutcome: SchemaOutcome.Success,
        timestamp: DateTime.UnixEpoch,
        interval: 30);

    var json = JsonSerializer.Serialize(entry);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    Assert.False(root.TryGetProperty("inputDefinition", out _));   // KEY-04
    Assert.False(root.TryGetProperty("outputDefinition", out _));  // KEY-04
    Assert.True(root.TryGetProperty("timestamp", out _));
    Assert.True(root.TryGetProperty("interval", out _));
    Assert.True(root.TryGetProperty("status", out _));
    Assert.True(root.GetProperty("summary").TryGetProperty("inputSchema", out _));
}
```

### Factory invariant tests (STATE-01/02)
```csharp
[Theory]
[InlineData(null,            null,            null,            "Healthy")]   // all skip ⇒ Healthy
[InlineData("SUCCESS",       "SUCCESS",       "SUCCESS",       "Healthy")]
[InlineData("FAIL",          "SUCCESS",       "SUCCESS",       "Unhealthy")] // any FAIL ⇒ Unhealthy
[InlineData("SUCCESS",       null,            "FAIL",          "Unhealthy")] // null-is-skip + a FAIL
public void Create_Derives_Status_From_Summary(
    string? input, string? output, string? config, string expectedStatus)
{
    var entry = ProcessorLivenessEntry.Create(input, output, config, DateTime.UnixEpoch, 30);
    Assert.Equal(expectedStatus, entry.Status);
    // null-is-skip surfaced as Success in the summary:
    Assert.Equal(input  ?? "SUCCESS", entry.Summary.InputSchema);
    Assert.Equal(config ?? "SUCCESS", entry.Summary.ConfigSchema);
}
```
(Use the `SchemaOutcome`/`LivenessStatus` consts rather than literals in the actual assertions; literals shown for `[InlineData]` constant-expression constraints.)

## State of the Art

| Old Approach (still live until 60/61) | Current/Target Approach (defined this phase) | When Changed | Impact |
|----------------------------------------|----------------------------------------------|--------------|--------|
| Single key `skp:{processorId}` (`L2ProjectionKeys.Processor`) | Per-instance `skp:proc:{id}:{instId}` + index SET `skp:proc:{id}` | Phase 59 (builder); cutover 60/61 | No cross-replica overwrite. |
| `ProcessorProjection { inputDefinition, outputDefinition, liveness }` | `ProcessorLivenessEntry { timestamp, interval, status, summary }` (definitions dropped) | Phase 59 (record); cutover 60/61 | L2 is liveness-only. |
| One-state `LivenessStatus.Healthy` (only-Healthy-writes) | Two-state `Healthy`/`Unhealthy` (unhealthy-is-written in Phase 60) | Phase 59 adds `Unhealthy` | Restarting replica visible, never absent. |
| instanceId chain duplicated ×3 | Single shared resolver SoT | Phase 59 (D-04) | Dedup; one mechanism. |

**Deprecated/outdated (NOT removed this phase):**
- `L2ProjectionKeys.Processor(Guid)` — superseded by `PerInstance`/`InstanceIndex`; deleted Phase 60/61.
- `ProcessorProjection` — superseded by `ProcessorLivenessEntry`; deleted Phase 60/61.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Status const casing should be title-case `"Unhealthy"` (matching existing `"Healthy"`), and `SchemaOutcome` uses `"SUCCESS"/"FAIL"` (matching prose). | Pattern 3 | LOW — golden/shape test pins whatever is chosen; only matters that writer & reader agree. Planner must pick explicitly. |
| A2 | `Messaging.Contracts` is the correct home for the shared resolver (despite the existing in-code "wrong home" comment). | Open Q1 | MEDIUM — if the planner disagrees, resolver lands in BaseConsole.Core (still no cycle for the processor path, but BaseApi.Core can't reach it). See Open Q1. |
| A3 | New `proc:` discriminator segment in the key is intended (CONTEXT/ROADMAP lock the literal `skp:proc:{...}` strings). | Pattern 1 | LOW — strings are quoted verbatim in ROADMAP SC-1 + CONTEXT D-01. |
| A4 | Repointing the two existing observability copies is OPTIONAL this phase (separate sweep allowed). | Pitfall 5 | LOW — explicitly stated in CONTEXT Deferred Ideas. |
| A5 | The positional ctor of `ProcessorLivenessEntry` must stay public for STJ deserialization (Phase-61 reader). | Pattern 2 | LOW — matches how `ProcessorProjection` is deserialized by the validator today. |

## Open Questions

1. **Where does the shared instanceId resolver live? (D-04 — the one real decision)**
   - **What we know:** Dependency graph (verified from all four csproj files): `Messaging.Contracts` = pure leaf, zero project refs. `BaseApi.Core` → `Messaging.Contracts`. `BaseConsole.Core` → `Messaging.Contracts` ONLY (hard-forbidden from `BaseApi.Core`, D-08). `BaseProcessor.Core` → `BaseConsole.Core` + `Messaging.Contracts`. The resolver body is BCL-only (`Environment`, `Guid`). The Phase-60 writer lives in `BaseProcessor.Core`; the two existing observability copies live in `BaseApi.Core` and `BaseConsole.Core`.
   - **What's unclear:** The existing Phase-30 comments (in BOTH observability files AND the test) explicitly assert *"`Messaging.Contracts` is the wrong home"* for this helper and that the duplication is deliberate. D-04 overrides that judgment for a *different reason* (it's now a cross-cutting SoT the liveness path needs, not a 6-line OTel-only helper), but the comments will read as contradictory if not updated.
   - **Recommendation:** Place the resolver in `Messaging.Contracts` (e.g. `Messaging.Contracts.Identity.InstanceId.Resolve()`). It is the ONLY assembly all three potential callers already reference without creating a cycle, and the BCL-only body adds zero dependency weight to the leaf. **Update the now-stale "wrong home"/"duplicated, NOT test-visible" comments** in both observability files (and the `ResolveInstanceIdFacts` doc) to point at the new SoT. Whether to physically repoint the two observability copies now or in a later sweep is the planner's call (CONTEXT Deferred allows deferral) — but if repointed, the `ResolveInstanceIdFacts` mirror should call the real resolver so the test stops being a hand-copy. If the planner prefers minimal blast radius, the resolver can land in `Messaging.Contracts` and only the (Phase-60) liveness path consumes it this milestone, leaving the dedupe for later — D-04 is satisfied either way.

2. **Should the positional ctor be discouraged in favor of `Create`?**
   - **What we know:** STJ needs a public ctor to deserialize. `Create` is the invariant gate.
   - **What's unclear:** Whether to add an analyzer/convention nudge so writers don't bypass `Create`.
   - **Recommendation:** Keep the positional ctor public (STJ requirement), document `Create` as the only sanctioned construction path in XML, and rely on the factory invariant test + code review. No analyzer needed for a leaf this small.

## Environment Availability

> SKIPPED — Phase 59 is a code-only contract-surface change. No external tools/services/runtimes are invoked (the new symbols compile into `Messaging.Contracts.dll` and are exercised by hermetic in-process xUnit tests). The only build prerequisites are the existing .NET 8 SDK + the solution's CPM-pinned packages, both already present (the repo builds today).

## Validation Architecture

> `workflow.nyquist_validation` is treated as enabled (no `.planning/config.json` opt-out found). All Phase-59 tests are **hermetic** (no real Redis/RabbitMQ/Postgres) — pure string-pinning and in-process serialization, matching the existing `L2ProjectionKeysTests` / `ResolveInstanceIdFacts` discipline.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (xUnit v3 idiom — `TestContext.Current.CancellationToken` in use) + `Microsoft.NET.Test.Sdk`; assertions via `Assert.*` |
| Config file | none — convention-based; tests live in `tests/BaseApi.Tests/` |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~L2ProjectionKeys|FullyQualifiedName~ProcessorLivenessEntry|FullyQualifiedName~SchemaOutcome|FullyQualifiedName~InstanceId"` |
| Full suite command | `dotnet test` (from repo root) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| KEY-01 | `PerInstance` → `skp:proc:{id}:{instId}` (golden) | unit | `dotnet test --filter "Name~PerInstance"` | ❌ Wave 0 (add to `L2ProjectionKeysTests`) |
| KEY-02 | `InstanceIndex` → `skp:proc:{id}` (golden) + is prefix of `PerInstance` | unit | `dotnet test --filter "Name~InstanceIndex"` | ❌ Wave 0 (add to `L2ProjectionKeysTests`) |
| KEY-03 | resolver chain `POD_NAME→HOSTNAME→MachineName→GUID(N)` | unit | `dotnet test --filter "FullyQualifiedName~InstanceId"` | ⚠️ partial — `ResolveInstanceIdFacts` exists but mirrors a LOCAL copy; add facts against the new shared resolver (or repoint the mirror) |
| KEY-04 | new record JSON has NO `inputDefinition`/`outputDefinition` | unit (serialization/shape) | `dotnet test --filter "Name~No_Definition_Fields"` | ❌ Wave 0 (new file) |
| STATE-01 | `status ∈ {Healthy, Unhealthy}`; `Unhealthy` const exists | unit | `dotnet test --filter "Name~Create_Derives_Status"` | ❌ Wave 0 (new file) |
| STATE-02 | per-schema `summary`; any FAIL ⇒ Unhealthy; null ⇒ Success | unit (factory invariant) | `dotnet test --filter "Name~Create_Derives_Status"` | ❌ Wave 0 (new file) |
| SC-5 | 0-warning Release + Debug | build | `dotnet build -c Release -warnaserror; dotnet build -c Debug -warnaserror` | n/a (build gate) |

### Sampling Rate
- **Per task commit:** the quick-run filter above (sub-second; hermetic).
- **Per wave merge:** `dotnet test tests/BaseApi.Tests` (the projection/observability slice) + `dotnet build -c Release -warnaserror`.
- **Phase gate:** full `dotnet test` green AND `dotnet build -c Release -warnaserror && dotnet build -c Debug -warnaserror` (SC-5) before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] Extend `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs` — golden pins for `PerInstance` + `InstanceIndex` (KEY-01/02). (File exists; add facts.)
- [ ] New test file (e.g. `tests/BaseApi.Tests/Features/Orchestration/Projection/ProcessorLivenessEntryFacts.cs`) — shape test (definitions absent, KEY-04) + factory invariant theory (STATE-01/02).
- [ ] instanceId resolver facts — either add `InstanceIdResolverFacts` against the new shared `Resolve()`, or repoint `ResolveInstanceIdFacts.Resolve` to call the real SoT (KEY-03). Decide alongside Open Q1.
- [ ] Framework install: none — `BaseApi.Tests` already references the new types' assembly (`Messaging.Contracts`) and xUnit is wired.

*(No new test project, no new fixtures, no real-stack harness needed — all Phase-59 surface is hermetic.)*

## Security Domain

> Phase 59 adds POCO contracts and a BCL-only string resolver to a pure leaf — no auth, no session, no access control, no crypto, no new input-trust boundary. The wire shape is consumed by the existing trusted writer/reader, which ALREADY treat the L2 value as external/untrusted (see `ProcessorLivenessValidator`'s WR-01 malformed-JSON → 422 hardening). No new threat surface is introduced this phase.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | — (contract types only) |
| V3 Session Management | no | — |
| V4 Access Control | no | — |
| V5 Input Validation | indirect | The Phase-61 reader (out of scope here) must keep the existing WR-01 discipline — malformed/absent external L2 entries map to the gate's not-live outcome, never a 500. The new record's STJ deserialization carries the same "non-nullable annotation not runtime-enforced" caveat as `ProcessorProjection`; the Phase-61 reader (not this phase) owns the try/catch + null-guard. |
| V6 Cryptography | no | — (the `Guid.NewGuid()` fallback is an identity discriminator, NOT a security token; do not treat it as a secret) |

### Known Threat Patterns for the leaf-contract stack
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Malformed external L2 entry → 500 escape | Denial of Service | Existing WR-01 pattern in the reader (try/catch `JsonException` + null-guard → 422). Forward-fit note: the Phase-61 reader of `ProcessorLivenessEntry` must replicate this; Phase 59 only ensures the shape is deserializable. |
| status/summary tampering (writer desync) | Tampering | The `Create(...)` factory invariant — status cannot contradict summary at construction. |

## Sources

### Primary (HIGH confidence) — actual current source, read this session
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — key SoT, `Prefix`, `Root`/`Processor` GUID-format precedent (`:D` vs bare, both hyphenated).
- `src/Messaging.Contracts/Projections/LivenessProjection.cs` — load-bearing `[property: JsonPropertyName]` positional-record pattern (Pitfall 1).
- `src/Messaging.Contracts/Projections/ProcessorProjection.cs` — legacy value (left in place); deserialization shape the new reader mirrors.
- `src/Messaging.Contracts/Projections/LivenessStatus.cs` — string-const SoT to extend with `Unhealthy`.
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` — current writer (Phase-60 swap target); `interval` in SECONDS, `LivenessStatus.Healthy` const usage.
- `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` — current reader (Phase-61 swap target); WR-01 malformed→422, `timestamp + interval*2 > now` staleness math.
- `src/BaseProcessor.Core/Identity/IProcessorContext.cs` + `ProcessorContext.cs` — Gate A outcome source (`ConfigSchemaId`/`ConfigDefinition`, null-is-skip) for D-02a.
- `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs:98-114` — resolver copy #1 (`ToString("N")` fallback; "wrong home" comment).
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs:87-105` — resolver copy #2 (byte-identical).
- `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs` — golden-pin mechanics to mirror.
- `tests/BaseApi.Tests/Observability/ResolveInstanceIdFacts.cs` — resolver test mirror #3 (drift guard).
- `tests/BaseApi.Tests/Features/Orchestration/ProcessorLivenessFacts.cs` — seed-the-record (not raw JSON) discipline; WR-01 malformed facts.
- All four `.csproj` (`Messaging.Contracts`, `BaseApi.Core`, `BaseConsole.Core`, `BaseProcessor.Core`) + `Directory.Build.props` — dependency graph + 0-warning policy (`TreatWarningsAsErrors`, `NoWarn CS1591`).
- `.planning/phases/59-.../59-CONTEXT.md` (D-01..D-04), `.planning/REQUIREMENTS.md` (KEY/STATE), `.planning/ROADMAP.md` (§Phase 59 SC-1..SC-5; Phase 43/50 precedent).

### Secondary / Tertiary
- None — no web/Context7 lookup needed; entire phase is grounded in in-repo source.

## Metadata

**Confidence breakdown:**
- Standard stack (new symbols + style): HIGH — all mirror existing, verified-in-source patterns; no external deps.
- Architecture (additive posture, no consumer touch): HIGH — D-03 + Phase 43/50 precedent + verified untouched call sites.
- Pitfalls: HIGH — Pitfalls 1–4 are each grounded in an in-code comment or the build config; Pitfall 5 is the only judgment call.
- Resolver home (Open Q1): MEDIUM — recommendation is sound on the dependency graph, but contradicts a pre-existing in-code comment that the planner should consciously override + clean up.

**Research date:** 2026-06-13
**Valid until:** ~30 days (stable in-repo contract surface; only invalidated if the leaf's csproj deps or the `L2ProjectionKeys`/`LivenessStatus` style change before planning).
