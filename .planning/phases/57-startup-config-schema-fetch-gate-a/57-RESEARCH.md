# Phase 57: Startup Config-Schema Fetch + Gate A - Research

**Researched:** 2026-06-12
**Domain:** .NET startup orchestration (MassTransit `IRequestClient` + hosted-service loop), JSON Schema structural introspection (`JsonSchema.Net` 9.2.1), `System.Text.Json` deserialization-binding semantics, and an RFC-7807 immutability gate in the WebAPI Schema CRUD path.
**Confidence:** HIGH (all claims grounded in read source; the STJ rule table is `[ASSUMED]`/`[CITED]` per STJ docs and flagged for confirmation)

## Summary

Every load-bearing seam this phase touches already exists and is read-confirmed. The fetch (CFG-03/04) is a verbatim third iteration of Loop B's existing `GetSchemaDefinition` dual-response retry at `ProcessorStartupOrchestrator.cs:124-162`; `ConfigSchemaId` is already carried on `ProcessorContext.cs:42` and `IProcessorContext`, and `SetDefinition` (`ProcessorContext.cs:74-80`) need only gain a third `if`. Gate A's "covers" check (CFG-05) is a pure-reasoning structural walk: parse the fetched definition with `JsonSchema.FromText` (the exact Gate B reuse model at `PayloadConfigSchemaValidator.cs:55`), enumerate its declared properties via the `JsonSchema.Net` keyword accessors (`GetProperties()`/`GetType()`/`GetEnum()`/`GetItems()`), reflect `TConfig` (reached at startup via the DI processor's `GetType().BaseType?.GenericTypeArguments[0]`), and flag a clash only when a property present in BOTH would fail STJ binding under the forgiving `ProcessorConfig.SerializerOptions` (`ProcessorConfig.cs:18-22`). The terminal-unhealthy posture (CFG-06) is achieved by *decoupling* the two calls that fire together today at `ProcessorStartupOrchestrator.cs:181-182` — run Gate A after Loop B, fire `gate.MarkReady()` on both paths, withhold `MarkHealthy()` + skip the `ConnectReceiveEndpoint` bind on a clash. The immutability gate (CFG-10) slots into `SchemaService.UpdateAsync` as an override that queries `DbContext.Set<ProcessorEntity>()` (the established cross-entity pattern at `ProcessorService.cs:77`) and throws a new domain exception mapped to 409 by a new `IExceptionHandler` registered ahead of the fallback — mirroring `OrchestrationValidationExceptionHandler`.

The single genuine design unknown is the STJ type-clash rule table (Claude's Discretion per D-02/D-04). I provide a concrete deterministic table below. The risk it carries: STJ's number/enum/nullable coercion rules are nuanced and version-sensitive; a too-strict rule blocks a config that *would* deserialize (violates D-02 "Gate A never blocks a config that does deserialize"), and a too-loose rule lets a runtime `JsonException` slip past (violates the milestone invariant). The table is biased toward D-02: when in doubt, treat a difference as *ignorable* (pass), because a false-pass merely preserves today's behavior (a deser failure that already propagates to `ProcessorPipeline.cs:241` → one `StepFailed`), whereas a false-block withholds Healthy from a working processor.

**Primary recommendation:** Implement Gate A as a single stateless `internal` checker class in `BaseProcessor.Core` (e.g. `ConfigSchemaCoverageCheck`) returning a structured `(bool covered, string? clashDetail)`; call it once from the orchestrator after Loop B and before the bind; implement CFG-10 as a `SchemaService.UpdateAsync` override + new `SchemaDefinitionFrozenException` + handler. No new NuGet dependency (`JsonSchema.Net` 9.2.1 is already referenced by `BaseProcessor.Core.csproj:37`).

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Fetch `ConfigSchemaId` definition over the bus (CFG-03/04) | Processor startup (`BaseProcessor.Core`) | WebAPI responder (`GetSchemaDefinitionConsumer`, unchanged) | Loop B already owns definition resolution; this is its third iteration |
| Store `ConfigDefinition` on context (CFG-03) | Processor (`ProcessorContext`) | — | Per D-14, on context only, NOT in the L2 `ProcessorProjection` |
| Gate A "covers" check (CFG-05) | Processor startup (`BaseProcessor.Core`) | — | The check is processor-side; the schema text comes over the bus, the type is local |
| Withhold Healthy / skip bind on clash (CFG-06) | Processor startup (`ProcessorStartupOrchestrator`) | Liveness heartbeat (no-ops while `!IsHealthy`, unchanged) | The one-way latch + heartbeat gate already encode "unhealthy = never latched" |
| Null-`ConfigSchemaId` skip (CFG-07) | Processor startup | — | Mirrors the existing null-is-skip at `:127-128` |
| Frozen-once-referenced immutability + 409 (CFG-10) | WebAPI Schema CRUD (`SchemaService.UpdateAsync`) | WebAPI exception-handler chain (new handler) | Mutation happens in the WebAPI; the FK reference lives in the same DB |
| Orchestration-start liveness gate (proves blocked-422) | WebAPI (`ProcessorLivenessValidator`, unchanged) | — | Phase 58 end-to-end proof seam; NOT touched this phase |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `JsonSchema.Net` | 9.2.1 | Parse the fetched config-schema definition + enumerate its declared properties for the structural walk | Already the single SSRF-locked JSON-Schema engine (`JsonSchemaConfig.cs`); already referenced by `BaseProcessor.Core.csproj:37` `[VERIFIED: Directory.Packages.props:90]` |
| `System.Text.Json` | (.NET 8 BCL) | The deserialization contract Gate A models; reflection over `TConfig` properties honoring `[JsonPropertyName]` | The exact `ProcessorConfig.SerializerOptions` instance the framework deserializes with (`ProcessorConfig.cs:18`) — Gate A must model THIS behavior, not generic STJ |
| MassTransit `IRequestClient<GetSchemaDefinition>` | (existing) | Fetch the config-schema definition over the bus | Verbatim reuse of Loop B's dual-response retry (`ProcessorStartupOrchestrator.cs:136`) |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| FluentValidation | (existing) | NOT used for the immutability gate (a referential precondition, not a field-shape rule) | The 409 belongs in `SchemaService.UpdateAsync`, not the DTO validator — see Architecture Pattern 4 |
| EF Core `DbContext.Set<T>()` | (existing) | Query "is this schema referenced by any ProcessorEntity FK" | Established cross-entity read pattern (`ProcessorService.cs:77`, `WorkflowService.cs:58-59`); keeps `IRepository<T>` at its locked 5 methods |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Structural walk over parsed `JsonSchema` (D-01) | Schema-generation lib (`JsonSchema.Net.Generation`) to emit a schema FROM `TConfig`, then schema-vs-schema compare | Rejected by D-01 (new dependency, indirection). The forward direction is the wrong question — we need "does a schema-valid payload bind", not "are the two schemas equal" |
| Structural walk | Roundtrip sampling (generate payloads valid under the schema, try `Deserialize<TConfig>`) | Rejected by D-01 (non-deterministic, can't enumerate the infinite valid-payload space) |
| 409 in `SchemaService.UpdateAsync` | DB trigger / check constraint | Rejected by D-08 (message-friendliness; a trigger yields a raw 500-shaped error, not RFC-7807 409) |

**Installation:** None — no new packages. `JsonSchema.Net` 9.2.1 is already on both `BaseApi.Service` and `BaseProcessor.Core`.

**Version verification:** `JsonSchema.Net` pinned at `9.2.1` in `Directory.Packages.props:90` `[VERIFIED: Directory.Packages.props:90]`. The keyword-accessor API used below (`GetProperties()`, `GetKeyword<T>()`, `JsonSchema[string]`, `FindSubschema()`) is stable since v4.0.0/5.2.0 `[CITED: github.com/json-everything/json-everything release notes rn-json-schema.md]`. I did not run `npm view` (this is a NuGet project); registry currency is confirmed by the pinned props file.

## Architecture Patterns

### System Architecture Diagram

```
                         PROCESSOR STARTUP (BaseProcessor.Core)
                         ─────────────────────────────────────
  host start
     │
     ▼
  Loop A: GetProcessorBySourceHash ──(dual-response retry)──► WebAPI responder
     │  Found → context.SetIdentity(Id, In?, Out?, Config?, Name, Ver)
     ▼
  Loop B: for each NON-NULL in { Input, Output, **Config** }      ◄── CFG-03/04 (D-12)
     │      GetSchemaDefinition(id) ──(SchemaDefinitionNotFound/timeout = transient retry)──► WebAPI
     │      Found → context.SetDefinition(id, def)                ◄── SetDefinition gains 3rd `if` (D-14)
     ▼
  ┌─────────────────── GATE A (after Loop B, before bind) ───────┐  ◄── CFG-05 (D-13)
  │ ConfigDefinition == null ? ──yes──► SKIP (null-is-skip)──┐    │  ◄── CFG-07 (D-11 fetch already null-skipped)
  │           │ no                                            │   │
  │           ▼                                               │   │
  │  parse def (JsonSchema.FromText) + reflect TConfig        │   │
  │  walk BOTH-present properties for STJ bind clash          │   │
  │           │                                               │   │
  │     covered? ──no──► CLASH (terminal)                     │   │
  │           │ yes              │                            │   │
  └───────────┼─────────────────┼────────────────────────────┼───┘
              ▼ (pass)          ▼ (clash, CFG-06)             ▼ (skip)
   ConnectReceiveEndpoint    LOG Error(procId, cfgId,    ConnectReceiveEndpoint
   ({id:D}) + await Ready     property+schemaType vs       + await Ready
        │                     CLR-type)                        │
        ▼                         │                            ▼
   MarkHealthy() ◄────────────────┼──────(NOT called)────► MarkHealthy()
        │                         │                            │
        └───────────► gate.MarkReady() ◄──── fires on ALL THREE paths (D-09) ──────┘
                            │
                            ▼
  heartbeat: writes skp:{id} ONLY while IsHealthy ──► (clash ⇒ no key ⇒ ProcessorLivenessValidator "absent" ⇒ 422)

                         WEBAPI SCHEMA UPDATE (CFG-10 / TOCTOU)
                         ─────────────────────────────────────
  PUT /api/v1/schemas/{id}  →  SchemaService.UpdateAsync (override)
     │  validate DTO  →  load entity  →  IF Definition is changing AND schema is referenced
     │                                   (any ProcessorEntity.{Config,Input,Output}SchemaId == id) :
     │                                       throw SchemaDefinitionFrozenException
     │                                   (Name/Description still mutable — D-07)
     ▼  → handler → RFC-7807 + 409 Conflict (+ X-Correlation-Id via CustomizeProblemDetails)
```

### Recommended Project Structure
```
src/BaseProcessor.Core/
├── Startup/
│   └── ProcessorStartupOrchestrator.cs   # extend Loop B + insert Gate A call before bind
├── Identity/
│   ├── IProcessorContext.cs              # add ConfigDefinition getter
│   └── ProcessorContext.cs               # add ConfigDefinition + 3rd `if` in SetDefinition
├── Configuration/
│   ├── ProcessorConfig.cs                # SerializerOptions = the binding contract (read-only)
│   └── ConfigSchemaCoverageCheck.cs      # NEW — the Gate A structural walk (stateless, internal)
src/BaseApi.Service/Features/Schema/
├── SchemaService.cs                      # override UpdateAsync — freeze-on-referenced (D-06/D-08)
├── SchemaDefinitionFrozenException.cs    # NEW — domain exception
└── SchemaDefinitionFrozenExceptionHandler.cs  # NEW — RFC-7807 + 409 (mirror OrchestrationValidationExceptionHandler)
```

### Pattern 1: Loop B third-iteration fetch (CFG-03/04 — D-12)
**What:** Add `context.ConfigSchemaId` to the iterated set; `SetDefinition` routes it to `ConfigDefinition`.
**When to use:** The non-null config-schema fetch.
**Example:**
```csharp
// ProcessorStartupOrchestrator.cs — change line 125 from:
foreach (var schemaId in new[] { context.InputSchemaId, context.OutputSchemaId })
// to:
foreach (var schemaId in new[] { context.InputSchemaId, context.OutputSchemaId, context.ConfigSchemaId })
//   null config id is skipped by the existing `if (schemaId is not { } id) continue;` at :127 (CFG-07 fetch-side).
//   SchemaDefinitionNotFound/timeout still loop transiently (CFG-04) — UNCHANGED retry body.

// ProcessorContext.cs — SetDefinition (:74-80) gains a 3rd branch (D-14):
public void SetDefinition(Guid schemaId, string definition)
{
    if (schemaId == InputSchemaId)  InputDefinition  = definition;
    if (schemaId == OutputSchemaId) OutputDefinition = definition;
    if (schemaId == ConfigSchemaId) ConfigDefinition = definition;   // NEW
}
```
**Source:** read `ProcessorStartupOrchestrator.cs:124-162`, `ProcessorContext.cs:74-80`.

> **Edge case to confirm in planning:** if two of `Input/Output/Config` carry the *same* schema Id, the existing `if`s (not `else if`) all fire — which is correct (one fetch populates all matching slots). The `foreach` would still query the same Id up to 3 times; acceptable (idempotent, retry-bounded). Not a blocker, but note it.

### Pattern 2: Gate A placement + the three-path `MarkReady` decoupling (CFG-06 — D-09/D-13)
**What:** Today `MarkHealthy()` (`:181`) and `gate.MarkReady()` (`:182`) fire together after the bind. Decouple: `MarkReady` must fire on pass, clash, AND skip; `MarkHealthy` + the bind fire ONLY on pass/skip.
**When to use:** The completion block.
**Example:**
```csharp
// After Loop B, BEFORE the ConnectReceiveEndpoint bind (D-13):
var result = ConfigSchemaCoverageCheck.Evaluate(context.ConfigDefinition, ConcreteConfigType());  // null-def → Covered (skip)
if (!result.Covered)
{
    logger.LogError(
        "Gate A incompatibility for processor {ProcessorId} config schema {ConfigSchemaId}: {Clash}",
        context.Id, context.ConfigSchemaId, result.ClashDetail);   // CFG-06 single Error log (D-10)
    gate.MarkReady();   // K8s sees startup done — NO crash-loop (D-09). MarkHealthy + bind NOT reached.
    return;             // terminal, no retry (D-11) — break out of ExecuteAsync.
}

// pass/skip path (unchanged bind):
var handle = endpointConnector.ConnectReceiveEndpoint(queueName, (ctx, cfg) => cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx));
await handle.Ready;
context.MarkHealthy();
gate.MarkReady();
```
**Source:** read `ProcessorStartupOrchestrator.cs:164-185`, `ProcessorContext.cs:83-89` (one-way latch), `ProcessorLivenessHeartbeat.cs:70` (no-op while `!IsHealthy`).

> **Resolving the type of `TConfig` at startup (D-01):** the orchestrator does not currently hold a `BaseProcessor` reference. Per the CONTEXT note, reach it via `GetType().BaseType?.GenericTypeArguments[0]` on the DI-resolved `BaseProcessor` instance. Two viable wirings for planning to choose:
> - Inject `BaseProcessor` (or `IServiceProvider`) into `ProcessorStartupOrchestrator` and read `processor.GetType().BaseType!.GenericTypeArguments[0]`. NOTE the orchestrator is a `BackgroundService` (singleton-ish) and `ProcessorPipeline` is `Scoped`; `BaseProcessor` is author-registered as Singleton or Scoped (`BaseProcessorServiceCollectionExtensions.cs:87-96`). Resolve the *type* only — do not capture a scoped instance across the heartbeat lifetime.
> - Or add a tiny `IConfigTypeProvider` singleton the author host registers (mirrors `ISourceHashProvider`). Cleaner DI, but more surface.
> Recommend option 1 reading only `.GetType()` inside `ExecuteAsync` (no captive dependency, type identity is process-stable).

### Pattern 3: The `JsonSchema.Net` structural walk (CFG-05 — D-01/D-04)
**What:** Parse the definition; enumerate declared properties + their `type`/`enum`/`items`/nested-`properties`; recurse.
**When to use:** Inside `ConfigSchemaCoverageCheck.Evaluate`.
**Example:**
```csharp
// Source: JsonSchema.Net 9.2.1 keyword accessors [CITED: json-everything README + release notes]
using Json.Schema;

var schema = JsonSchema.FromText(configDefinition);          // same parse as PayloadConfigSchemaValidator.cs:55
// reference JsonSchemaConfig.DefaultOptions is NOT needed for a pure structural walk (no Evaluate),
// BUT touch a JsonSchemaConfig member first if you ever Evaluate, to fire the SSRF cctor (Pitfall 3).

SchemaValueType? declaredType = schema.GetType();            // TypeKeyword value (e.g. Object/String/Integer/Array)
IReadOnlyDictionary<string, JsonSchema>? props = schema.GetProperties();   // PropertiesKeyword.Properties
foreach (var (name, propSchema) in props ?? new Dictionary<string, JsonSchema>())
{
    SchemaValueType? pType = propSchema.GetType();
    var enumValues       = propSchema.GetEnum();             // EnumKeyword (IReadOnlyList<JsonNode?>?) or null
    JsonSchema? itemSchema = propSchema.GetItems();          // ItemsKeyword single-schema form
    var nestedProps      = propSchema.GetProperties();       // recurse for SchemaValueType.Object
    // ... map `name` → CLR property (Pattern below), then apply the rule table.
}
```
Keyword accessors confirmed: `GetType()`→`SchemaValueType?`, `GetProperties()`→`IReadOnlyDictionary<string,JsonSchema>?`, `GetEnum()`, `GetItems()`, generic `GetKeyword<T>()`, indexer `schema["properties"]`, `FindSubschema()` `[CITED: json-everything release notes v4.0.0/5.2.0]`. All return `null` (not throw) when the keyword is absent (fixed in 5.2.0) `[CITED: rn-json-schema.md 5.2.0]`.

**Source:** Gate B's parse reuse model at `PayloadConfigSchemaValidator.cs:55,84`; Json.Schema docs via Context7.

### Pattern 4: schema-property → CLR-property name mapping (Claude's Discretion under D-02)
**What:** Map a schema property name to a `TConfig` CLR property honoring `[JsonPropertyName]` + STJ case-insensitivity, matching exactly the `ProcessorConfig.SerializerOptions` behavior (`PropertyNameCaseInsensitive = true`, no naming policy set).
**Example:**
```csharp
// Build the lookup the SAME way STJ would resolve it under ProcessorConfig.SerializerOptions.
// SerializerOptions has PropertyNameCaseInsensitive=true and NO PropertyNamingPolicy → the JSON-facing
// name is [JsonPropertyName] if present, else the raw CLR member name (NO camelCase transform). [CITED: STJ docs]
static string JsonName(PropertyInfo p) =>
    p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name;

var clrByJsonName = configType
    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
    .Where(p => p.CanWrite || /* init / ctor-bound */ true)     // STJ binds settable + init + ctor params
    .ToDictionary(JsonName, p => p, StringComparer.OrdinalIgnoreCase);   // case-insensitive (D-05 mirror)
```
**Decision point for planning:** records use init-only/positional properties (the `ProcessorConfig` base is an `abstract record`, `ProcessorConfig.cs:10`; author configs inherit it). `GetProperties()` surfaces init-only properties (they have a setter). Confirm constructor-parameter-bound positional records are covered — STJ binds them via the parameterized ctor; reflect both the properties AND, if needed, the primary-ctor parameters. Recommend: enumerate public instance properties (covers positional records' synthesized properties).

### Anti-Patterns to Avoid
- **Modeling generic STJ instead of `ProcessorConfig.SerializerOptions`:** Gate A MUST gate against the exact cached options instance (`ProcessorConfig.cs:18`). Do not assume camelCase policy (none is set), do not assume strict unmapped-member handling (it is `Skip` — `ProcessorConfig.cs:21`, confirmed by `56-SECURITY.md` T-56-03).
- **Flagging schema-only or TConfig-only properties as clashes:** D-02 — a schema property absent from `TConfig` is FINE (ignored at runtime); a `TConfig` property absent from the schema is FINE. Only BOTH-present clashes count.
- **Shallow top-level walk:** D-04 rejects this — a nested-object/array-item clash would slip past Gate A and throw at runtime. Recurse into `SchemaValueType.Object` `GetProperties()` and `SchemaValueType.Array` `GetItems()`.
- **Putting the 409 in the DTO validator:** "is the schema referenced" is a cross-entity DB precondition, not a field-shape rule. FluentValidation validators are constructed without DbContext. Use the `SchemaService.UpdateAsync` override (D-08 "service/validator layer" — service is the right home).
- **Throwing inside `BaseProcessor<TConfig>.ExecuteAsync`:** Gate A is startup-only (D-03 / runtime contract). Do NOT add any payload-vs-schema check to the hot path.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Parse + introspect JSON Schema | A hand-written JSON-Schema-keyword parser | `JsonSchema.FromText` + `GetProperties()`/`GetType()`/`GetEnum()`/`GetItems()` | Draft-2020-12 keyword semantics (refs, combinators, type unions) are a minefield; the lib is already the SSRF-locked engine |
| The bus fetch + transient retry | A new dedicated config-fetch loop | Loop B's existing `GetSchemaDefinition` dual-response retry | D-12 explicitly: verbatim reuse; a parallel loop duplicates the boot-before-register backoff logic |
| RFC-7807 409 envelope + correlation echo | Manual `ProblemDetails` JSON | A domain `IExceptionHandler` + the existing `CustomizeProblemDetails` customizer | `correlationId` + `instance` are auto-injected (`ErrorHandlingServiceCollectionExtensions.cs:26-34`); the handler must NOT set them |
| "Is this schema referenced" | A new `IRepository` method | `DbContext.Set<ProcessorEntity>()` direct query | `IRepository<T>` is locked at 5 methods (`IRepository.cs:9-13`); cross-entity reads use `DbContext.Set<T>()` (`ProcessorService.cs:77`) |

**Key insight:** This phase is almost entirely *recomposition* of existing, read-confirmed seams. The only net-new code is (1) the `ConfigSchemaCoverageCheck` rule engine and (2) the freeze exception + handler. Everything else is a one-line `foreach` extension, a one-`if` `SetDefinition` extension, and a decoupling of two adjacent calls.

## STJ Type-Clash Rule Table (CFG-05 core — D-02/D-04)

> This is the phase's principal design artifact. Direction: for each property present in **both** the schema and `TConfig`, decide whether a JSON value that is *valid under the schema's constraint* would **FAIL** to bind into the CLR property under `ProcessorConfig.SerializerOptions` (case-insensitive, ignore-unknown, no naming policy, default number handling = strict — STJ does NOT read JSON strings into numeric CLR types unless `NumberHandling` allows it, and `ProcessorConfig.SerializerOptions` sets no `NumberHandling`). A row marked **CLASH** = withhold Healthy. A row marked **FINE** = pass.

| # | Schema constraint (Draft 2020-12) | CLR property type | Verdict | Rationale |
|---|-----------------------------------|-------------------|---------|-----------|
| 1 | `type: integer` | `int` / `long` | FINE | Integer JSON binds to integral CLR. |
| 2 | `type: integer` | `double` / `decimal` | FINE | STJ binds an integer-valued JSON number to floating/decimal. |
| 3 | `type: integer` | `string` | **CLASH** | `42` (JSON number) → `string` fails under default `NumberHandling`. `[ASSUMED: STJ default number handling]` |
| 4 | `type: number` | `double` / `decimal` / `float` | FINE | — |
| 5 | `type: number` (allows `3.14`) | `int` / `long` | **CLASH** | A schema-valid `3.14` does not bind to `int` (STJ throws on fractional → integral). `[ASSUMED]` |
| 6 | `type: number` (integer-only by other keywords, e.g. `multipleOf:1`) | `int` | **CLASH (conservatively FINE-able)** | Schema *permits* fractional unless constrained; default to CLASH only if `type:number` without an integrality constraint. Recommend treating bare `type:number` → `int` as CLASH. |
| 7 | `type: string` | `string` | FINE | — |
| 8 | `type: string` | `int` / numeric | **CLASH** | `"abc"` valid under schema; STJ won't read a JSON string into `int` without `NumberHandling.AllowReadingFromString` (not set). `[ASSUMED]` |
| 9 | `type: string`, `format: date-time` | `DateTime` / `DateTimeOffset` | FINE | STJ binds ISO-8601 strings to `DateTime`. `format` is annotation-only in 2020-12 but the value is still a string. |
| 10 | `type: string` | `Guid` | FINE | STJ binds GUID-shaped strings; a non-GUID string would fail — but that is a *runtime payload* issue Gate B doesn't cover and is out of D-02's "type clash" scope. Treat as FINE (string↔string at the type level). Flag in Open Questions. |
| 11 | `type: boolean` | `bool` | FINE | — |
| 12 | `type: boolean` | `string` / `int` | **CLASH** | `true` won't bind to `string`/`int`. `[ASSUMED]` |
| 13 | `enum: ["A","B"]` (string enum) | CLR `enum` (default STJ: by NAME via no converter… but STJ binds enums from **number** by default, names require `JsonStringEnumConverter`) | **CLASH** unless a `JsonStringEnumConverter` is registered | By default STJ deserializes enums from their numeric value, NOT names. `ProcessorConfig.SerializerOptions` registers no converter (`ProcessorConfig.cs:18-22`), so a JSON string `"A"` valid under the schema FAILS to bind to a CLR enum. **This is the highest-value clash to catch.** `[ASSUMED: STJ default enum = numeric; CONFIRM]` |
| 14 | `enum: ["A","B"]` (string enum) | `string` | FINE | Enum string values bind to `string`. |
| 15 | `enum: [1,2,3]` (numeric enum) | `int` / CLR `enum` | FINE | Numeric enum values bind to numeric CLR / enum-by-number. |
| 16 | `type: array` | `List<T>` / `T[]` / `IReadOnlyList<T>` | FINE (recurse into `items` × `T`) | Array→collection; recurse item schema vs `T`. |
| 17 | `type: array` | scalar (`int`/`string`/object) | **CLASH** | A JSON array won't bind to a scalar CLR property. |
| 18 | scalar (`type: string/number/...`) | `List<T>` / `T[]` | **CLASH** | A scalar won't bind to a collection. |
| 19 | `type: object` with `properties` | nested CLR class/record | FINE (recurse property-by-property) | Recurse the nested object's both-present properties. |
| 20 | `type: object` | scalar / collection | **CLASH** | object↔scalar mismatch. |
| 21 | `type: ["string","null"]` (nullable union) | reference type / `Nullable<T>` (`int?`) | FINE | `null` binds to nullable. |
| 22 | `type: ["string","null"]` | non-nullable value type (`int`) | **CLASH (borderline)** | A schema-valid `null` fails to bind to non-nullable `int` (STJ throws). Recommend **CLASH**. `[ASSUMED]` |
| 23 | no `type` keyword (any) | any CLR type | FINE | An unconstrained schema permits any JSON; you cannot prove a clash — D-02 bias = FINE. |
| 24 | schema property NOT in `TConfig` | (n/a) | FINE | D-02: ignored at runtime (ignore-unknown). |
| 25 | `TConfig` property NOT in schema | (n/a) | FINE | D-02: schema doesn't constrain it; any payload omits or includes it harmlessly. |
| 26 | `type` is a union of multiple non-null types (e.g. `["string","integer"]`) | single CLR type | **CLASH if ANY member of the union would fail** | The schema admits a string OR an integer; if `TConfig` is `int`, the string branch fails to bind. Conservative: CLASH if any union member clashes. `[ASSUMED]` |

**The "real type clash" definition (D-02):** a clash exists for a both-present property iff there exists at least one JSON value `v` such that (`v` is valid under the schema's declared constraint for that property) AND (`Deserialize<TConfig>` of an object carrying `{ "<jsonName>": v }` would throw `JsonException`). If no such `v` exists, it is an *ignorable difference* (FINE).

**D-02 tie-break rule:** when a row is genuinely ambiguous (e.g. `format`-constrained strings, `Guid`), default to **FINE**. A false-FINE preserves today's behavior (a deser failure already maps to one `StepFailed` at `ProcessorPipeline.cs:241`); a false-CLASH withholds Healthy from a working processor (worse — it's a false-positive blocker the Phase-58 negative-control would catch).

**Rows requiring confirmation before locking (highest risk):** #13 (enum name-vs-number — the single most likely real-world clash), #5/#6 (`number`→`int`), #22 (nullable union → non-nullable value type). These should be verified with a one-time `Deserialize<T>` spike in Wave 0 (see Validation Architecture) rather than trusted from the `[ASSUMED]` tags.

## Common Pitfalls

### Pitfall 1: `gate.MarkReady()` left red on a Gate A clash → K8s crash-loop
**What goes wrong:** Withholding `MarkReady` (not just `MarkHealthy`) makes the readiness probe never go green → K8s restarts the pod forever.
**Why it happens:** Today the two fire together (`:181-182`); a naive "skip the completion block on clash" skips BOTH.
**How to avoid:** Fire `gate.MarkReady()` on the clash path before returning (D-09). Only `MarkHealthy` + the bind are withheld.
**Warning signs:** `DispatchBindSequenceFacts`-style ordered-log shows no `"ready"` on the clash path.

### Pitfall 2: Modeling STJ as camelCase / strict
**What goes wrong:** Gate A flags a property as missing because it camelCased the CLR name, or flags extra keys as clashes.
**Why it happens:** Assuming a default web `JsonSerializerOptions` (camelCase, sometimes strict). `ProcessorConfig.SerializerOptions` sets NEITHER a naming policy NOR `JsonUnmappedMemberHandling.Disallow` (`ProcessorConfig.cs:18-22`, confirmed `56-SECURITY.md` T-56-03).
**How to avoid:** Map names via `[JsonPropertyName] ?? p.Name` with `OrdinalIgnoreCase`; ignore-unknown means rows #24/#25 are always FINE.

### Pitfall 3: SSRF cctor not fired if you ever `Evaluate`
**What goes wrong:** A pure structural walk does not call `Evaluate`, so `JsonSchemaConfig`'s static ctor (which pins Draft 2020-12 + the no-op `$ref` fetcher) may not fire. If a future change adds an `Evaluate`, external `$ref` resolution could regress.
**Why it happens:** The cctor only fires on first member access of `JsonSchemaConfig` (`JsonSchemaConfig.cs:16-18` doc).
**How to avoid:** If the walk needs to resolve `$ref` subschemas via `FindSubschema`, touch `JsonSchemaConfig.DefaultOptions` first (forces the cctor). For a pure top-level/nested-`properties` walk with no external `$ref`, it's not strictly needed — but document the dependency.

### Pitfall 4: Reflecting `TConfig` from a captured scoped instance
**What goes wrong:** Capturing a `Scoped` `BaseProcessor` instance in the singleton `BackgroundService` is a captive-dependency bug (`BaseProcessorServiceCollectionExtensions.cs:87-96` warns about this).
**How to avoid:** Resolve only the `Type` (`processor.GetType().BaseType!.GenericTypeArguments[0]`) — type identity is process-stable; do not hold the instance.

### Pitfall 5: The freeze check blocking a Name/Description-only update
**What goes wrong:** Throwing 409 on any update of a referenced schema, including Name/Description edits.
**Why it happens:** D-07 — only `Definition` is frozen.
**How to avoid:** Throw only when the incoming `Definition` differs from the persisted `Definition` AND the schema is referenced. Compare the parsed/canonical form or the raw string (the DTO carries `Definition` — `SchemaUpdateDto`). Name/Description changes with an unchanged Definition pass.

### Pitfall 6: "Referenced" query missing the assignment/role coverage
**What goes wrong:** Checking only `ConfigSchemaId` misses `InputSchemaId`/`OutputSchemaId` references (D-06 is uniform across all three roles) and any assignment reference.
**How to avoid:** Query `DbContext.Set<ProcessorEntity>().Any(p => p.InputSchemaId == id || p.OutputSchemaId == id || p.ConfigSchemaId == id)`. Note: `AssignmentEntity` has NO direct schema FK (`AssignmentEntity.cs:24-28` — Phase 10 removed it); the "assignment reference" in D-06 resolves transitively through Step→Processor, so the ProcessorEntity FK query is sufficient. Confirm in planning that there is no other schema-referencing FK.

## Code Examples

### CFG-10 freeze gate in `SchemaService.UpdateAsync` (override)
```csharp
// SchemaService.cs — override UpdateAsync (mirrors the BaseService 5-step minus add; D-06/D-08).
public sealed class SchemaService : BaseService<SchemaEntity, SchemaCreateDto, SchemaUpdateDto, SchemaReadDto>
{
    // ctor unchanged (5-param passthrough) — DbContext is available via the protected DbContext property.
    public override async Task<SchemaReadDto> UpdateAsync(Guid id, SchemaUpdateDto dto, CancellationToken ct)
    {
        // NOTE: BaseService.UpdateAsync is NOT currently virtual (BaseService.cs:106). Planning must make it
        // `virtual` (additive, no behavior change) OR move the check into a SyncJunctionsAsync override —
        // but SyncJunctionsAsync runs AFTER _mapper.Update mutates the entity, so the freeze check needs the
        // pre-mutation persisted Definition. Cleanest: make UpdateAsync virtual and override here.
        var existing = await DbContext.Set<SchemaEntity>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        if (existing is not null && !string.Equals(existing.Definition, dto.Definition, StringComparison.Ordinal))
        {
            var referenced = await DbContext.Set<ProcessorEntity>().AsNoTracking().AnyAsync(
                p => p.InputSchemaId == id || p.OutputSchemaId == id || p.ConfigSchemaId == id, ct);
            if (referenced)
                throw new SchemaDefinitionFrozenException(id);   // → 409 (D-08)
        }
        return await base.UpdateAsync(id, dto, ct);   // Name/Description (and unchanged Definition) flow through (D-07)
    }
}
```

### CFG-10 handler (mirror of `OrchestrationValidationExceptionHandler`)
```csharp
// SchemaDefinitionFrozenExceptionHandler.cs — register in the chain AHEAD of FallbackExceptionHandler.
public sealed class SchemaDefinitionFrozenExceptionHandler(IProblemDetailsService pd) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        if (ex is not SchemaDefinitionFrozenException f) return false;     // fast-bail (Pitfall 6 pattern)
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        var problem = new ProblemDetails {
            Status = StatusCodes.Status409Conflict,
            Title  = "Schema definition is frozen",
            Detail = $"Schema '{f.SchemaId}' is referenced by a processor; its Definition cannot be modified. " +
                     "Create a new schema and re-point. (Name and Description remain editable.)",
        };  // correlationId + instance injected by CustomizeProblemDetails (do NOT set here)
        return await pd.TryWriteAsync(new ProblemDetailsContext { HttpContext = ctx, ProblemDetails = problem, Exception = ex });
    }
}
```
**Registration:** add `services.AddExceptionHandler<SchemaDefinitionFrozenExceptionHandler>()` in the WebAPI feature/error wiring so it is walked BEFORE `FallbackExceptionHandler` (registered last by `AddBaseApiFallbackHandler`). Walk order == registration order (`ErrorHandlingServiceCollectionExtensions.cs:37-43`).

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| D-05 "never read ConfigSchemaId" carve-out (`ProcessorStartupOrchestrator.cs:30,124`) | Loop B now resolves ConfigSchemaId too (D-12) | This phase | The carve-out comment + the SchemaResolutionFacts `DoesNotContain(configId)` assertion must be inverted |
| `MarkHealthy()` + `MarkReady()` fire together (`:181-182`) | Decoupled: `MarkReady` on all paths, `MarkHealthy` only on pass/skip | This phase | Enables the stay-up fail posture (D-09) |
| Schema `Definition` always mutable | Frozen-once-referenced (D-06) | This phase | New 409 on referenced-schema Definition edits |

**Deprecated/outdated:**
- `SchemaResolutionFacts.LoopB_Resolves_Input_And_Output_Never_Config` — its `Assert.DoesNotContain(configId, queried)` (line 145) becomes `Assert.Contains` once Gate A lands. This is an intentional, planned inversion, not a regression.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | STJ default enum handling is by numeric value (not name); a string enum schema → CLR enum is a CLASH absent `JsonStringEnumConverter` | Rule table #13 | If wrong (e.g. a global converter is registered somewhere), Gate A false-blocks every string-enum config → false-positive Healthy withholding. **Highest-impact assumption.** Verify with a Wave-0 `Deserialize` spike. |
| A2 | `ProcessorConfig.SerializerOptions` sets no `NumberHandling`, so JSON-number↔CLR-string and JSON-string↔CLR-number both clash (rows #3,#8) | Rule table | Read-confirmed options are empty except case-insensitive (`ProcessorConfig.cs:18-22`); the *consequence* (strict number handling) is the STJ default `[ASSUMED]`. Verify in spike. |
| A3 | Schema-valid `null` (`type:["...","null"]`) fails to bind to a non-nullable value type (row #22) | Rule table #22 | Borderline; if STJ coerces, a false-CLASH. Low frequency. Verify in spike. |
| A4 | `BaseService.UpdateAsync` can be made `virtual` (it is non-virtual today, `BaseService.cs:106`) without disturbing other entities' update paths | CFG-10 example | Making it virtual is additive; the only override is SchemaService. Low risk. Confirm no analyzer/seal constraint. |
| A5 | No schema-referencing FK exists other than `ProcessorEntity.{Input,Output,Config}SchemaId` (assignments reference schemas only transitively via Step→Processor) | Pitfall 6 | `AssignmentEntity.cs:24-28` confirms no direct FK; if a future FK exists the freeze query under-detects. Confirm against the full entity/FK graph in planning. |
| A6 | `JsonSchema.GetProperties()/GetType()/GetEnum()/GetItems()` exist and return null-on-absent in 9.2.1 | Pattern 3 | `[CITED]` from release notes (4.0.0 keyword accessors, 5.2.0 null-on-absent); 9.2.1 is far past both. Low risk. Confirm by compiling the spike. |

## Open Questions

1. **Enum name-vs-number binding (rule #13) — the load-bearing assumption.**
   - What we know: STJ binds enums numerically by default; `ProcessorConfig.SerializerOptions` registers no `JsonStringEnumConverter`.
   - What's unclear: whether any author config or the framework registers a converter elsewhere; whether the intended author contract is string-enums.
   - Recommendation: Wave-0 spike — `Deserialize<EnumConfig>("{\"mode\":\"A\"}")` with the real `ProcessorConfig.SerializerOptions`; if it throws, #13 CLASH is correct.

2. **`Guid`/`format`-constrained strings (rules #9,#10).**
   - What we know: at the *type* level these are string↔string (FINE per D-02).
   - What's unclear: a non-GUID string valid under `type:string` would throw at runtime binding to `Guid` — but that's a payload-value issue Gate B does not catch and D-02's "type clash" arguably excludes.
   - Recommendation: treat as FINE (D-02 tie-break); document that `Guid`/format-typed configs rely on Gate B + the residual in-transit-mutation window, which is the milestone's one accepted gap.

3. **Positional-record / init-only / primary-ctor property coverage.**
   - What we know: `ProcessorConfig` is an `abstract record`; author configs inherit it.
   - What's unclear: whether enumerating public instance properties fully covers positional records (synthesized properties have setters, so likely yes).
   - Recommendation: confirm in the spike with a positional `record SampleConfig(...) : ProcessorConfig`.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| `JsonSchema.Net` | Gate A structural walk | ✓ | 9.2.1 | — (already referenced both projects) |
| `System.Text.Json` | TConfig reflection + binding model | ✓ | .NET 8 BCL | — |
| MassTransit `IRequestClient` | Config-schema fetch | ✓ | existing | — |
| EF Core / Postgres | CFG-10 referenced-query | ✓ | existing | — |

**Missing dependencies with no fallback:** None.
**Missing dependencies with fallback:** None. This phase adds no external dependency.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (v3 — `TestContext.Current`) + NSubstitute + `Microsoft.Extensions.Time.Testing.FakeTimeProvider` + MassTransit `ITestHarness` |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Processor.ConfigSchemaCoverage" ` (Gate A unit slice) |
| Full suite command | `dotnet test tests/BaseApi.Tests --filter-not-trait "Category=RealStack"` (hermetic suite; RealStack E2E is Phase 58) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| CFG-03 | Loop B fetches non-null ConfigSchemaId → `context.ConfigDefinition` set | unit (harness) | `dotnet test --filter "Name~LoopB_Resolves...Config"` | ⚠️ INVERT — `SchemaResolutionFacts.cs:118` currently asserts config NOT queried; flip to assert queried + ConfigDefinition set |
| CFG-04 | Missing config def is transient (retry on NotFound/timeout) | unit (harness) | same harness, `SchemaNotFoundCount > 0` for config id | ✅ pattern exists (`SchemaCapture.NextIsNotFound`) |
| CFG-05 | `covers` check: clash detected on a both-present type mismatch; FINE on schema-only/TConfig-only | unit (pure) | `dotnet test --filter "FullyQualifiedName~ConfigSchemaCoverage"` | ❌ Wave 0 — new `ConfigSchemaCoverageFacts.cs` (table-driven over the rule table) |
| CFG-06 | Clash ⇒ ordered log `["ready"]` only (NO connect/markhealthy); Error logged; `!IsHealthy`; no skp key | unit (harness) | `dotnet test --filter "Name~GateA_Clash"` | ⚠️ extend `DispatchBindSequenceFacts.cs` pattern — add a clash case asserting `["ready"]` and `connector.BoundQueueName is null` |
| CFG-07 | Null ConfigSchemaId ⇒ skip Gate A ⇒ Healthy + bind | unit (harness) | `dotnet test --filter "Name~GateA_NullSkip"` | ⚠️ extend harness (config id null → reaches Healthy, queue bound) |
| CFG-10 | Frozen schema Definition mutation ⇒ 409; Name/Description edit ⇒ 200; unreferenced draft edit ⇒ 200 | integration (WebApplicationFactory) | `dotnet test --filter "FullyQualifiedName~Schema.Freeze"` | ❌ Wave 0 — new `SchemaDefinitionFreezeFacts.cs` (the test that RECORDS the chosen TOCTOU mechanism per ROADMAP SC-5) |

### Sampling Rate
- **Per task commit:** `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~ConfigSchemaCoverage | FullyQualifiedName~Schema.Freeze | Name~GateA"` (the Gate A + freeze slice — sub-30s).
- **Per wave merge:** `dotnet test tests/BaseApi.Tests --filter-not-trait "Category=RealStack"` (full hermetic suite — was 530/530 green at the Phase-56 gate).
- **Phase gate:** Full hermetic suite green + Release+Debug 0-warning before `/gsd-verify-work`. RealStack E2E (config-incompatible processor blocked 422) is deferred to **Phase 58** (CFG-08/09), not this phase.

### Observable signals per success criterion
| Success criterion | Observable signal | Seam |
|-------------------|-------------------|------|
| Gate A pass → Healthy | ordered log `["connect","ready","markhealthy"]`; `context.IsHealthy`; `skp:{id}` written | `DispatchBindSequenceFacts` recording connector/context + `FakeTimeProvider` |
| Gate A fail → never Healthy | ordered log `["ready"]` only; `connector.BoundQueueName is null`; `!context.IsHealthy`; one Error log | extended `DispatchBindSequenceFacts` clash case + a capturing `ILogger` |
| Null config → skip → Healthy | config id never queried for a definition mismatch; reaches Healthy + bound | extended `SchemaResolutionFacts` |
| Transient fetch failure → retry | config id queried >1× before Found (`SchemaCapture` count) | `SchemaResolutionFacts` `NextIsNotFound` |
| Frozen-schema mutation → 409 | HTTP 409 + RFC-7807 body + `X-Correlation-Id` echo; Name/Desc edit → 200 | integration `SchemaDefinitionFreezeFacts` |

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Processor/ConfigSchemaCoverageFacts.cs` — table-driven pure-unit tests over the STJ rule table (covers CFG-05). MUST include the A1/A2/A3 spike rows (enum name, number→int, null→value-type) driving the REAL `ProcessorConfig.SerializerOptions` through `Deserialize` to ground the `[ASSUMED]` verdicts.
- [ ] `tests/BaseApi.Tests/Features/Schema/SchemaDefinitionFreezeFacts.cs` — integration (covers CFG-10 + records the TOCTOU mechanism for ROADMAP SC-5).
- [ ] Extend `tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs` — add Gate A clash + null-skip cases (covers CFG-06/07).
- [ ] Invert `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs` — config-now-fetched assertions (covers CFG-03/04).
- [ ] Framework install: none — all test deps already present.

## Project Constraints (from CLAUDE.md / memory)

- No `./CLAUDE.md` found in the working directory (checked; not present). No project skill `SKILL.md` directories found under `.claude/skills/` or `.agents/skills/`.
- From auto-memory (binding conventions for this repo):
  - **Design-iteration style:** the user owns the spec; this research analyzes/refactors within the locked D-01..D-14 — no scope additions.
  - **Close-gate net-zero protocol:** any future close gate (Phase 58, not this phase) must start from a clean Redis keyspace, rebuild stale images, and run the net-zero sweep — out of scope here but noted for the milestone.
  - **0-warning Release+Debug** is the standing build gate (confirmed by every prior phase footer in ROADMAP).

## Sources

### Primary (HIGH confidence)
- Read source (this session): `ProcessorStartupOrchestrator.cs`, `ProcessorContext.cs`, `IProcessorContext.cs`, `ProcessorLivenessHeartbeat.cs`, `ProcessorConfig.cs`, `BaseProcessor`1.cs`, `JsonSchemaConfig.cs`, `PayloadConfigSchemaValidator.cs`, `SchemaService.cs`, `SchemaDtoValidator.cs`, `SchemaEntity.cs`, `ProcessorEntity.cs`, `AssignmentEntity.cs`, `BaseService.cs`, `IRepository.cs`, `Repository.cs`, `ProcessorService.cs`, `AppDbContext.cs`, `PostgresExceptionMapper.cs`, `DbUpdateExceptionHandler.cs`, `OrchestrationValidationException.cs`, `OrchestrationValidationExceptionHandler.cs`, `ErrorHandlingServiceCollectionExtensions.cs`, `BaseProcessorServiceCollectionExtensions.cs`, `ProcessorQueries.cs`, `SchemaResolutionFacts.cs`, `DispatchBindSequenceFacts.cs`.
- `Directory.Packages.props:90` — `JsonSchema.Net` 9.2.1 (verified).
- `.planning/phases/57-.../57-CONTEXT.md` (D-01..D-14), `.planning/REQUIREMENTS.md` (CFG-03..07,10), `.planning/ROADMAP.md` (Phase 57 success criteria), `.planning/phases/56-.../56-SECURITY.md` (T-56-03 ignore-unknown).

### Secondary (MEDIUM confidence)
- Context7 `/json-everything/json-everything` — `JsonSchema.FromText`, fluent keyword model, `GetKeyword`/keyword-accessor extensions, null-on-absent (5.2.0), `IBaseDocument`/`FindSubschema` (4.0.0).

### Tertiary (LOW confidence)
- STJ default number/enum/nullable binding behavior under empty `JsonSerializerOptions` — `[ASSUMED]` from training; flagged A1/A2/A3 for a Wave-0 `Deserialize` spike (do NOT lock the rule table without it).

## Metadata

**Confidence breakdown:**
- Fetch wiring (CFG-03/04): HIGH — verbatim Loop B reuse, read-confirmed.
- Gate A placement / decoupling (CFG-06/07): HIGH — exact line sites and the one-way latch read-confirmed.
- STJ rule table (CFG-05): MEDIUM — structure HIGH, individual coercion verdicts MEDIUM/LOW (A1/A2/A3 need the spike).
- Immutability gate (CFG-10): HIGH — established override + handler + cross-entity query patterns read-confirmed; the only open item is making `UpdateAsync` virtual (additive).
- Validation seams: HIGH — the exact harness/recording-connector/FakeTimeProvider patterns read-confirmed in existing facts.

**Research date:** 2026-06-12
**Valid until:** 2026-07-12 (stable; .NET 8 + pinned NuGet). Re-verify the STJ rule-table assumptions via the Wave-0 spike regardless of date.

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| CFG-03 | Startup fetches non-null ConfigSchemaId definition over the bus + stores on context | Pattern 1 (Loop B third iteration + `SetDefinition` 3rd `if`); read `ProcessorStartupOrchestrator.cs:124-162`, `ProcessorContext.cs:74-80` |
| CFG-04 | Missing config definition is transient (retry on NotFound/timeout) | Pattern 1 reuses the unchanged Loop B retry body; `SchemaResolutionFacts` `NextIsNotFound` seam |
| CFG-05 | Concrete config type *covers* the fetched config-schema definition | Pattern 3 (JsonSchema.Net walk) + Pattern 4 (name mapping) + the STJ Type-Clash Rule Table |
| CFG-06 | Clash ⇒ never Healthy (withhold MarkHealthy, no skp key, terminal, logged) | Pattern 2 (decouple MarkReady from MarkHealthy; skip bind; single Error log D-10); `DispatchBindSequenceFacts` ordered-log seam |
| CFG-07 | Null ConfigSchemaId skips Gate A entirely | Pattern 2 null-is-skip (def==null → Covered); mirrors `:127-128` / `PayloadConfigSchemaValidator.cs:42` |
| CFG-10 | TOCTOU closed by frozen-once-referenced immutability + 409 | CFG-10 example (`SchemaService.UpdateAsync` override + `SchemaDefinitionFrozenException` + handler); `DbContext.Set<ProcessorEntity>()` referenced-query; `SchemaDefinitionFreezeFacts` records the mechanism |
</phase_requirements>
