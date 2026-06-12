# Phase 56: Typed Base-Config Seam — Research

**Researched:** 2026-06-12
**Domain:** .NET 8 / System.Text.Json deserialization, C# generic-over-non-generic abstract class seam design
**Confidence:** HIGH (design is locked in CONTEXT.md/SPEC.md; this research de-risks implementation only)

## Summary

Phase 56 inserts a generic `BaseProcessor<TConfig> : BaseProcessor` layer between the framework's non-generic `BaseProcessor` (the type the pipeline resolves and calls) and the concrete author processor. The generic layer overrides the framework's `internal ExecuteAsync(string validatedData, string payload, ct)` forwarder, deserializes `payload` into `TConfig` using one shared `JsonSerializerOptions`, and dispatches to a new typed author seam `protected abstract Task<List<ProcessItem>> ProcessAsync(string validatedData, TConfig? config, ct)`. The non-generic call site (`ProcessorPipeline.cs:226`) and DI registration (`AddSingleton<BaseProcessor, SampleProcessor>`) stay literally unchanged. `Processor.Sample` migrates to a minimal `record SampleConfig(string? Value)` deriving from an empty marker base.

The whole design rests on three verified System.Text.Json (net8.0) facts: malformed JSON throws `JsonException`, which the existing pipeline catch-all (`ProcessorPipeline.cs:241`) maps to `StepFailed` for free (no new error-routing code); the literal `null` JSON token and `default`-shaped inputs return `null` cleanly into a nullable `TConfig?`; and `PropertyNameCaseInsensitive = true` with default (ignore) unknown-member handling never faults on extra or differently-cased properties. The current `internal ExecuteAsync` forwarder on `BaseProcessor` is **not** `virtual`/`abstract` (it is a concrete `internal` method calling `protected abstract ProcessAsync`), so the cleanest structure requires changing the non-generic base's seam shape — this is the one structural decision the planner must make explicitly (see Architecture Patterns, Pattern 1).

**Primary recommendation:** Make the non-generic `BaseProcessor.ExecuteAsync` (or an internal `protected abstract` core method it forwards to) overridable so `BaseProcessor<TConfig>` supplies the deserialize-then-dispatch body; remove the non-generic `protected abstract ProcessAsync(string, string)` and replace it with the typed seam on the generic class. Expose a single `public static readonly JsonSerializerOptions` on `BaseProcessor.Core` as the canonical config-deserialization contract for Phase 57 reuse.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Generic layer over the existing non-generic base: `public abstract class BaseProcessor<TConfig> : BaseProcessor` (`TConfig` constrained to a reference type / the base config marker so `null` is representable). The non-generic `BaseProcessor` stays the type the framework resolves and the pipeline calls — its string-based `ExecuteAsync(string validatedData, string payload, ct)` entry point is **preserved**, so `ProcessorPipeline.cs:226` and `AddSingleton<BaseProcessor, SampleProcessor>` are **unchanged**.
- **D-02:** The generic `BaseProcessor<TConfig>` overrides/implements the framework `ExecuteAsync`: it deserializes the dispatch `payload` into `TConfig`, then invokes the author's new typed seam `protected abstract Task<List<ProcessItem>> ProcessAsync(string validatedData, TConfig? config, ct)`. The old `ProcessAsync(string validatedData, string payload, ct)` author seam is **removed** (clean break).
- **D-03:** Deserialization happens **inside** `BaseProcessor<TConfig>.ExecuteAsync`, inside the pipeline's existing `try/catch` at `ProcessorPipeline.cs:226`/`:241` — a non-deserializable payload throws `JsonException` that the **existing catch-all maps to `StepFailed`** (no new error-routing code). Do NOT route malformed config to the infra/Keeper path.
- **D-04:** Empty/whitespace/absent payload → generic layer short-circuits to a **`null` config** (guard `string.IsNullOrWhiteSpace(payload)` before deserialize) and passes `null` to the author seam.
- **D-05:** Deserialization uses **one shared `JsonSerializerOptions`** with `PropertyNameCaseInsensitive = true` and default unknown-member handling (**unknown JSON properties are ignored**, not disallowed).
- **D-06:** This shared options instance is the **canonical config-deserialization contract**. Expose it somewhere Phase 57's Gate A can reference/reuse (e.g. a public static on `BaseProcessor.Core`) so Gate A models exactly the same deserialization behavior. (Where it lives — Claude's discretion; must be single-source and reusable.)
- **D-07:** Reuse the project's existing `System.Text.Json` stack — no new serialization dependency.
- **D-08:** `Processor.Sample` declares a **minimal single-field config record** (e.g. `record SampleConfig(string? Value)`) deriving from the base config marker; `SampleProcessor` inherits `BaseProcessor<SampleConfig>` and overrides the typed seam. No manual `JsonSerializer.Deserialize` of the config remains in `SampleProcessor`.
- **D-09:** Preserve the sample's existing demonstrations in the new typed shape: **null config → the existing `"processor-sample-ok"` fallback token**; the **`fail` status-exception demo** maps to a field value (e.g. `Value == "fail"` → throw `FailedException`), keeping the `ProcessStatusException` author path demonstrated.
- **D-10:** Consequence to thread through tests: the dispatch payload shape shifts from a **bare JSON string** (`"StepA1"`) to an **object** (`{"value":"StepA1"}`). Any hermetic and (Phase 58) RealStack round-trip payloads that echo the sample config must be updated to the object shape. The `Processor.Sample` round-trip must still complete (SPEC Requirement 5).

### Claude's Discretion
- Base config marker type name and namespace within `BaseProcessor.Core`.
- Exact location/visibility of the shared `JsonSerializerOptions` (D-06), provided it is single-source and reusable by Phase 57.
- Whether the framework `ExecuteAsync` seam is `internal abstract` on `BaseProcessor` with the generic class providing it, vs. another equivalent structure — provided the pipeline call site and DI resolution stay unchanged (D-01).

### Deferred Ideas (OUT OF SCOPE)
- **Strict unknown-member handling** (`JsonUnmappedMemberHandling.Disallow`) — rejected for this phase (D-05); belongs with Gate A's `additionalProperties` semantics in Phase 57.
- **Richer multi-field sample config** — rejected (D-08) in favor of minimal churn.
- **Typing the input `validatedData`** — explicitly out of scope (SPEC); a future milestone could generalize the typed seam to input/output.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| CFG-01 | Processor authors declare configuration as a typed class inheriting a framework-provided base config; framework deserializes the dispatch payload into that typed config. | Standard Stack (STJ 8 `JsonSerializer.Deserialize<TConfig>`); Architecture Pattern 1 (generic seam) + Pattern 2 (empty marker base); Code Examples 1–3. Verified deserialize-success behavior. |
| CFG-02 | The typed config replaces the raw-string `payload` author parameter; `Processor.Sample` migrated as worked example, old seam removed (clean break). | Architecture Pattern 3 (sample migration); Code Example 4 (`SampleConfig` + `SampleProcessor`); Pitfall 4 (test double `FakeProcessor` is non-generic — deser-failure test needs a real `BaseProcessor<TConfig>`). |

Both requirements map to SPEC Requirements 1–5. CFG-01 is satisfied by Requirements 1 (marker base), 3 (framework deserialize), 4 (deser-failure/empty semantics). CFG-02 is satisfied by Requirements 2 (typed seam) and 5 (sample clean break).
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Payload → typed config deserialization | Framework (`BaseProcessor<TConfig>` in BaseProcessor.Core) | — | D-02/D-03: framework owns deserialization; author never calls `JsonSerializer`. |
| Deser-failure → business `StepFailed` | Framework pipeline (`ProcessorPipeline.cs:241` catch-all) | — | D-03: reuse existing business-vs-infra split; no new route. |
| Empty/absent payload → null config | Framework (`BaseProcessor<TConfig>.ExecuteAsync` guard) | Author (decides config-less behavior) | D-04: framework short-circuits; author handles null. |
| Canonical `JsonSerializerOptions` contract | Framework (`BaseProcessor.Core` public static) | Phase 57 Gate A (reuses it) | D-05/D-06: single-source so Gate A gates against the same behavior. |
| Author config fields + transform logic | Author (`SampleConfig` + `SampleProcessor` typed seam) | — | D-08/D-09: empty marker base, author adds all fields. |
| Pipeline call site + DI resolution | Framework pipeline / composition root | — | D-01: unchanged — non-generic `BaseProcessor` stays the resolved type. |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| System.Text.Json | net8.0 BCL (in-box) | Deserialize dispatch `payload` into `TConfig` | [VERIFIED: BaseProcessor.Core.csproj targets net8.0; already used in SampleProcessor.cs:1 + across pipeline] Already the project's JSON stack (D-07); no new dependency. |

### Supporting
No new packages. The phase is a seam-shape change plus a sample migration; everything needed (STJ, the existing `ProcessStatusException` family, `ProcessItem`, the pipeline) already exists.

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `JsonSerializer.Deserialize<TConfig>(payload, options)` | Source-generated `JsonSerializerContext` (`TypeInfoResolver`) | Source-gen is faster/AOT-safe but requires a registered context per author config type — defeats the "author just declares a record" goal and adds churn. Reflection-based deserialize is correct for this phase. [ASSUMED: training knowledge of STJ source-gen tradeoffs] |
| Empty marker `abstract class` base config | `interface IProcessorConfig {}` marker | An interface cannot be the deserialization target directly and complicates the `TConfig?` null story; an empty (possibly `abstract`) class/record base is the cleanest anchor. SPEC Requirement 1 says "a type … an author config class inherits" → class/record. |

**Installation:** None — in-box BCL only.

**Version verification:** `System.Text.Json` ships in the net8.0 shared framework; no NuGet `PackageReference` is added or needed. [VERIFIED: Directory.Build.props sets `<TargetFramework>net8.0</TargetFramework>` and the csproj comment explicitly forbids net9.0.]

## Architecture Patterns

### System Architecture Diagram

```
                    EntryStepDispatch (Messaging.Contracts)
                    record { …, string Payload }   ← config JSON, e.g. {"value":"StepA1"}
                                  │
                                  ▼
            ProcessorPipeline.RunAsync  (ProcessorPipeline.cs)
                                  │  Pre: L2 read + input-schema validate (UNCHANGED)
                                  ▼
            ┌─────────────────────────────────────────────────────┐
            │  try { items = await processor.ExecuteAsync(         │  ← :226 call site UNCHANGED
            │           validatedData, d.Payload, ct); }          │     (processor is the non-generic
            │  catch (ProcessStatusException e) { → Step* }        │      BaseProcessor reference)
            │  catch (Exception ex) { → StepFailed }   ← :241      │  ← deser-failure lands HERE (D-03)
            └─────────────────────────────────────────────────────┘
                                  │
                  resolved runtime type = SampleProcessor
                                  │  ExecuteAsync dispatches into the GENERIC layer
                                  ▼
            BaseProcessor<TConfig>.ExecuteAsync(validatedData, payload, ct)   ← NEW framework layer
                                  │
              string.IsNullOrWhiteSpace(payload)? ──yes──▶ config = null  (D-04)
                                  │ no
                                  ▼
              JsonSerializer.Deserialize<TConfig>(payload, SharedConfigOptions)  (D-05)
                 │ malformed JSON → throws JsonException ─────▶ propagates to :241 → StepFailed (D-03)
                 │ literal "null" → returns null
                 ▼ success → populated TConfig
            ProcessAsync(validatedData, config: TConfig?, ct)   ← NEW typed author seam (D-02)
                                  │
                                  ▼
                    List<ProcessItem>  (author mints ExecutionId — unchanged)
                                  │
                                  ▼
            Post: output-schema validate + L2 forward write (UNCHANGED)
```

### Recommended Project Structure
```
src/BaseProcessor.Core/
├── Processing/
│   ├── BaseProcessor.cs         # non-generic base — seam shape changes (see Pattern 1)
│   ├── BaseProcessor`1.cs       # NEW: BaseProcessor<TConfig> (generic deser-then-dispatch layer)
│   ├── ProcessItem.cs           # unchanged
│   ├── ProcessStatusException.cs# unchanged
│   └── ProcessorPipeline.cs     # call site :226 UNCHANGED
└── Configuration/               # existing namespace — candidate home for the marker + shared options
    ├── ProcessorConfig.cs       # NEW: empty marker base (name = Claude's discretion)
    └── (or co-locate shared JsonSerializerOptions on the marker / a static holder)

src/Processor.Sample/
├── SampleConfig.cs              # NEW: record SampleConfig(string? Value) : <marker>
├── SampleProcessor.cs           # migrate: BaseProcessor<SampleConfig>, typed seam, no JsonSerializer
└── Program.cs                   # UNCHANGED (AddSingleton<BaseProcessor, SampleProcessor>)
```
Note: a `Configuration` namespace already exists in BaseProcessor.Core (`using BaseProcessor.Core.Configuration` appears in DispatchTestKit.cs:2) — a natural home for the marker + shared options, satisfying D-06's "public static on BaseProcessor.Core."

### Pattern 1: Generic-over-non-generic seam (D-01/D-02) — THE structural decision

**What:** The non-generic `BaseProcessor` currently has a concrete `internal ExecuteAsync(string,string,ct)` forwarder that calls a `protected abstract ProcessAsync(string,string,ct)` (BaseProcessor.cs:38-39, :29-30). To let `BaseProcessor<TConfig>` own the deserialize-then-dispatch body while the pipeline keeps calling the non-generic `ExecuteAsync`, the non-generic base must expose an **overridable** core. The current forwarder is **not** `virtual` — this is the change.

**When to use:** This phase. The cleanest of the D-01-permitted structures:

```csharp
// BaseProcessor.cs (non-generic) — seam shape change
public abstract class BaseProcessor
{
    // Framework-internal core the generic layer fills in. Was: concrete forwarder → protected abstract.
    // Now: internal abstract, so BaseProcessor<TConfig> provides the deserialize-then-dispatch body.
    internal abstract Task<List<ProcessItem>> ExecuteAsync(
        string validatedData, string payload, CancellationToken ct);
    // The old `protected abstract ProcessAsync(string,string,ct)` is REMOVED (clean break, D-02).
}
```

```csharp
// BaseProcessor`1.cs (generic) — the new framework layer
public abstract class BaseProcessor<TConfig> : BaseProcessor
    where TConfig : ProcessorConfig            // reference-type/base-marker constraint (Pattern 4)
{
    internal sealed override Task<List<ProcessItem>> ExecuteAsync(
        string validatedData, string payload, CancellationToken ct)
    {
        TConfig? config = string.IsNullOrWhiteSpace(payload)            // D-04 short-circuit
            ? null
            : JsonSerializer.Deserialize<TConfig>(payload, ProcessorConfig.SerializerOptions); // D-05; may throw JsonException → :241 StepFailed (D-03)
        return ProcessAsync(validatedData, config, ct);
    }

    protected abstract Task<List<ProcessItem>> ProcessAsync(            // NEW typed author seam (D-02)
        string validatedData, TConfig? config, CancellationToken ct);
}
```

**Why `internal abstract` works across assemblies:** `Processor.Sample` (a different assembly) derives from `BaseProcessor<SampleConfig>`, which already provides the `internal` override — the concrete author never needs to see or implement the `internal` member (mirrors the existing comment at BaseProcessor.cs:32-37 explaining why the forwarder is `internal`). The author still overrides exactly one seam (the typed `ProcessAsync`), preserving the BPC-02 invariant. [VERIFIED: existing BaseProcessor.cs already relies on this internal-forwarder cross-assembly pattern.]

**DI/pipeline invariance:** `AddSingleton<BaseProcessor, SampleProcessor>` still binds because `SampleProcessor : BaseProcessor<SampleConfig> : BaseProcessor` — it IS a `BaseProcessor`. The pipeline calls `processor.ExecuteAsync(validatedData, d.Payload, ct)` on the non-generic reference (ProcessorPipeline.cs:226); virtual dispatch resolves to the generic override. No call-site or registration change. [VERIFIED: ProcessorPipeline.cs:226 + Program.cs:17.]

### Pattern 2: Empty marker base config (D-08, SPEC Req 1)
**What:** A field-less base type the author config inherits — pure type anchor for the seam and Phase 57's Gate A.
**When to use:** Always for this phase.
```csharp
// ProcessorConfig.cs (name = discretion). A record or class both work; record pairs naturally with
// the author's `record SampleConfig(...)`. Zero framework-mandated fields (SPEC Req 1 acceptance).
public abstract record ProcessorConfig
{
    // D-06: the single canonical config-deserialization contract, reused by Phase 57 Gate A.
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,    // D-05
        // default unknown-member handling = ignore (NOT JsonUnmappedMemberHandling.Disallow) — D-05
    };
}
```
A positional author record `record SampleConfig(string? Value) : ProcessorConfig;` deserializes correctly because STJ matches the constructor parameter `Value` to the JSON property `value` under `PropertyNameCaseInsensitive = true`.

### Pattern 3: Sample migration clean break (D-08/D-09, SPEC Req 5)
```csharp
// SampleConfig.cs
public sealed record SampleConfig(string? Value) : ProcessorConfig;

// SampleProcessor.cs — migrated; NO JsonSerializer.Deserialize remains
public sealed class SampleProcessor(ILogger<SampleProcessor> logger)
    : BaseProcessor<SampleConfig>
{
    protected override Task<List<ProcessItem>> ProcessAsync(
        string validatedData, SampleConfig? config, CancellationToken ct)
    {
        var value = config?.Value;                       // D-04: null config → value null
        logger.LogInformation("sample payload received: {Payload}", value);

        if (value == "fail")                             // D-09: status-exception demo preserved
            throw new FailedException("sample reason");

        return Task.FromResult(new List<ProcessItem>
        {
            new(ProcessOutcome.Completed, value ?? "processor-sample-ok", Guid.NewGuid()), // D-09 fallback
        });
    }
}
```
The `BaseProcessorBase` alias (SampleProcessor.cs:4, :21-22) and the CS0118 workaround are no longer needed once the base is `BaseProcessor<SampleConfig>` — but verify whether `Program.cs` still needs the alias for `AddSingleton<BaseProcessorBase, SampleProcessor>` (it references the non-generic base type by name; keep that alias in Program.cs).

### Pattern 4: TConfig constraint for null-representability (D-01/D-04)
**What:** `where TConfig : ProcessorConfig` makes `TConfig` a reference type (the marker is a class/record), so `TConfig?` is a nullable reference and `config = null` compiles without a `class` constraint or `default!` gymnastics. Constraining to the marker also guarantees the deserialization target derives from the Gate-A anchor.
**Anti-pattern:** `where TConfig : struct` (would make null unrepresentable) or no constraint (then `TConfig?` is ambiguous and `null` assignment needs `default`). The marker-class constraint is exactly right.

### Anti-Patterns to Avoid
- **Re-deserializing in the author:** any surviving `JsonSerializer.Deserialize` in `SampleProcessor` fails SPEC Req 5 acceptance ("contains no manual JsonSerializer.Deserialize of the config payload").
- **Catching `JsonException` inside `BaseProcessor<TConfig>`:** do NOT — D-03 requires it to propagate to the pipeline catch-all (`:241`). Swallowing it (e.g. returning a default config) violates "no silent default" (SPEC Req 4 acceptance).
- **Per-call `new JsonSerializerOptions()`:** D-05/D-06 require ONE cached instance; a fresh options object per call defeats reuse and (minor) incurs STJ's per-options metadata warm-up. [VERIFIED: no existing shared options instance exists in src/ — grep found zero `JsonSerializerOptions` in src; current SampleProcessor uses the parameterless `Deserialize<string>(payload)`.]
- **Leaving the old `protected abstract ProcessAsync(string,string,ct)` as a shim:** SPEC constraint forbids a backward-compatible overload; clean break only.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Payload → typed config | A custom JSON parser or manual `JsonDocument` walk | `JsonSerializer.Deserialize<TConfig>(payload, options)` | STJ handles case-insensitivity, nulls, nesting, escaping — D-05/D-07. |
| Deser-failure routing | A new try/catch or infra/Keeper route for malformed config | The existing `ProcessorPipeline.cs:241` catch-all | D-03: `JsonException` already maps to `StepFailed`; new routing duplicates and risks mis-routing to Keeper. |
| Empty-payload handling | Author-side null checks scattered per processor | The framework `string.IsNullOrWhiteSpace` guard in `BaseProcessor<TConfig>` | D-04: framework owns it once; authors get a clean `TConfig?`. |
| Case/extra-property tolerance | Custom property mapping | `PropertyNameCaseInsensitive = true` + default ignore-unknown | D-05: most-forgiving contract; strictness is Gate A/Gate B's job. |

**Key insight:** This phase is deliberately a *relocation of existing responsibility* (deserialize moves from author to framework) plus a generic indirection — there is almost nothing new to build, only a seam reshape. Every "hard part" (error routing, status mapping, the result record) already exists and stays untouched.

## Common Pitfalls

### Pitfall 1: The non-generic forwarder is not currently overridable
**What goes wrong:** Assuming `BaseProcessor<TConfig>` can just `override ExecuteAsync` — but the current `internal ExecuteAsync` (BaseProcessor.cs:38) is a concrete non-virtual method, and `ProcessAsync(string,string)` is `protected abstract`. You cannot override a non-virtual method, and the abstract `ProcessAsync(string,string)` still demands an implementation.
**Why it happens:** The seam was designed in Phase 44 for a single non-generic author override, not a generic interposer.
**How to avoid:** Change `BaseProcessor` to declare `internal abstract Task<List<ProcessItem>> ExecuteAsync(string,string,ct)` and REMOVE the `protected abstract ProcessAsync(string,string,ct)`. The generic class supplies the `internal` override. This is the D-01-sanctioned "internal abstract on BaseProcessor with the generic class providing it" structure (Claude's discretion, but it is the clean one).
**Warning signs:** Compile error CS0506 (cannot override non-virtual) or CS0534 (does not implement inherited abstract `ProcessAsync(string,string)`).

### Pitfall 2: `BaseProcessorSeamFacts.TestProcessor` derives from the non-generic base directly
**What goes wrong:** `BaseProcessorSeamFacts.cs:20` defines `TestProcessor : BaseProcessorBase` and overrides `protected override ProcessAsync(string validatedData, string payload, ct)` (the OLD seam). After the seam change that override no longer exists on the non-generic base — the test won't compile.
**Why it happens:** The fact predates the generic layer.
**How to avoid:** Migrate `TestProcessor` to `BaseProcessor<SomeTestConfig>` and override the typed seam, OR keep a minimal non-generic test double if the non-generic base still needs direct subclassing for the DI-resolution assertion. The DI-resolution assertion (resolve as `BaseProcessor`, `IsType<TestProcessor>`) still holds through the generic base. [VERIFIED: BaseProcessorSeamFacts.cs:20-30.]

### Pitfall 3: `DispatchTestKit.FakeProcessor` is a non-generic double — it cannot exercise framework deserialization
**What goes wrong:** `FakeProcessor : BaseProcessorBase` (DispatchTestKit.cs:35) overrides `ProcessAsync(string,string)` and records `LastConfig = payload`. After the change it must override the framework `internal ExecuteAsync` (or derive from `BaseProcessor<TConfig>`). More importantly, the pipeline facts (`PipelineInFacts`, all the forward/recovery/end-delete facts) inject `FakeProcessor` and pass `payload="{\"cfg\":1}"` (DispatchTestKit.Dispatch default, :558) — they assert pipeline behavior, NOT deserialization, so they should keep bypassing deser. Keep `FakeProcessor` non-generic by having it override the `internal abstract ExecuteAsync` directly (a test-only non-generic subclass is legal — the `internal` member is visible to the test assembly via `InternalsVisibleTo`, OR the kit can derive from `BaseProcessor<DummyConfig>`).
**How to avoid:** Decide deliberately: (a) make `FakeProcessor` override the framework `internal ExecuteAsync` so it stays deser-free (pipeline facts unaffected), and (b) write the NEW deser-failure→StepFailed test using a REAL `BaseProcessor<TConfig>` subclass fed a malformed payload through `RunAsync` so the `JsonException`→`:241`→`StepFailed` path is genuinely exercised (SPEC Req 4(a) acceptance: "a single StepFailed, not a default instance"). Check whether BaseProcessor.Core grants `InternalsVisibleTo` to the test assembly — SampleProcessorFacts uses reflection (SampleProcessorFacts.cs:45) precisely because it does NOT, so the test-double override of an `internal` member may require the kit to derive from `BaseProcessor<TConfig>` instead.
**Warning signs:** Pipeline facts compile-fail on the `ProcessAsync(string,string)` override; the deser-failure test green-falses because it never actually deserializes.

### Pitfall 4: Hermetic test payloads carry bare-string config — shape shift required (D-10)
**What goes wrong:** `SampleProcessorFacts.cs:59` feeds `"\"StepA1\""` and asserts `only.Data == "StepA1"`; with the typed seam the payload must become `{"value":"StepA1"}` and the assertion path runs through `SampleConfig.Value`. Other dispatch payloads in the kit/facts are objects already (`{"cfg":1}` at DispatchTestKit.cs:558, `{"cfg":7}` in ReinjectConsumerFacts) and deserialize harmlessly into `SampleConfig` (unknown `cfg` ignored under D-05) IF they reach the sample processor — but most reach `FakeProcessor` which doesn't deserialize, so they are inert. Bare-string payloads like `"step-config"` (SustainedOutageFacts.cs:125, RecoveryDeadLetterFacts.cs:99/151) are Keeper-contract payloads, not sample-config inputs — confirm each before changing.
**How to avoid:** Audit every payload that actually flows into `SampleProcessor` / `BaseProcessor<SampleConfig>` and shift those to the `{"value":...}` object shape. Leave Keeper/pipeline-double payloads alone. The only confirmed sample-config payload in the hermetic suite is in `SampleProcessorFacts` (`"\"StepA1\""`, `""`, `"\"fail\""` → `{"value":"StepA1"}`, `""`, `{"value":"fail"}`). [VERIFIED: SampleProcessorFacts.cs:59/78/95.]
**Warning signs:** `SampleProcessorFacts` asserts `only.Data == "StepA1"` but gets `"processor-sample-ok"` (because `JsonSerializer.Deserialize<SampleConfig>("\"StepA1\"")` throws — a bare string is not an object — surfacing as a thrown `JsonException`, not a populated config).

### Pitfall 5: SampleProcessorFacts invokes the seam by reflection — signature + invocation must change
**What goes wrong:** `SampleProcessorFacts.InvokeProcessAsync` (cs:42-50) reflects `ProcessAsync` with `new object[] { validatedData, payload, ct }` — a `(string,string,ct)` signature. The new seam is `(string, SampleConfig?, ct)`. The reflection call must construct a `SampleConfig` and the `Throws<TargetInvocationException>` fail-demo still works (synchronous throw before returning the Task). Alternatively, the fact could drive `BaseProcessor<SampleConfig>.ExecuteAsync` (the `internal` deser path) by JSON string, which is closer to production and exercises D-04/D-05 end-to-end — but that requires `internal` visibility (see Pitfall 3). Simplest: keep reflecting the typed `ProcessAsync` and pass a `SampleConfig` directly for the unit facts; add ONE pipeline-level deser-failure fact for Req 4(a).
**How to avoid:** Rewrite the three SampleProcessorFacts to pass `SampleConfig` instances (or null) to the typed seam; add a separate test for the JSON deser path.

### Pitfall 6: net8.0 only — no net9.0 STJ features
**What goes wrong:** Reaching for a net9-only STJ API.
**How to avoid:** Everything used here (`Deserialize<T>`, `PropertyNameCaseInsensitive`, default unknown-member ignore) is net8.0-stable. [VERIFIED: csproj comment "CRITICAL: net8.0, never net9.0."]

## Code Examples

### Example 1: Verified deserialize-success behavior (SPEC Req 3 acceptance)
```csharp
// Source: System.Text.Json (net8.0) — verified behavior
var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var cfg = JsonSerializer.Deserialize<SampleConfig>("""{"value":"StepA1"}""", opts);
// cfg.Value == "StepA1"  (case-insensitive: "Value"/"value" both match the positional record param)
var cfg2 = JsonSerializer.Deserialize<SampleConfig>("""{"VALUE":"x","extra":1}""", opts);
// cfg2.Value == "x"; the unknown "extra" property is IGNORED (default unknown-member handling, D-05)
```

### Example 2: Verified deser-failure → JsonException (SPEC Req 4a / D-03)
```csharp
// Source: https://learn.microsoft.com/dotnet/api/system.text.json.jsonserializer.deserialize
// "Exceptions: JsonException — The JSON is invalid. -or- TValue is not compatible with the JSON."
JsonSerializer.Deserialize<SampleConfig>("not json", opts);     // throws JsonException
JsonSerializer.Deserialize<SampleConfig>("\"StepA1\"", opts);   // throws JsonException — a bare string
                                                                // is not a JSON object for SampleConfig
// In BaseProcessor<TConfig>.ExecuteAsync this JsonException is UNCAUGHT and propagates to
// ProcessorPipeline.cs:241 `catch (Exception ex)` → SendResult(BuildFailed(...)) → exactly one StepFailed.
```

### Example 3: Verified null/empty behavior (SPEC Req 4b / D-04)
```csharp
// Source: https://learn.microsoft.com/dotnet/api/system.text.json.jsonserializer.deserialize
// The literal "null" token deserializes to null for a reference type:
JsonSerializer.Deserialize<SampleConfig>("null", opts);   // returns null (no throw)
// An EMPTY string is NOT valid JSON and WOULD throw JsonException — but D-04's guard
// `string.IsNullOrWhiteSpace(payload)` short-circuits BEFORE the deserialize call, so the empty/
// whitespace/absent path returns null WITHOUT ever calling Deserialize. (Confirmed: empty-string is
// not handled by Deserialize — it must be guarded, which D-04 does.)
```

### Example 4: The migrated worked example (full, D-08/D-09)
```csharp
// SampleConfig.cs
public sealed record SampleConfig(string? Value) : ProcessorConfig;

// SampleProcessor.cs
public sealed class SampleProcessor(ILogger<SampleProcessor> logger) : BaseProcessor<SampleConfig>
{
    protected override Task<List<ProcessItem>> ProcessAsync(
        string validatedData, SampleConfig? config, CancellationToken ct)
    {
        var value = config?.Value;
        logger.LogInformation("sample payload received: {Payload}", value);
        if (value == "fail") throw new FailedException("sample reason");
        return Task.FromResult(new List<ProcessItem>
            { new(ProcessOutcome.Completed, value ?? "processor-sample-ok", Guid.NewGuid()) });
    }
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Author hand-deserializes raw `string payload` (`Deserialize<string>(payload)`) | Framework deserializes into typed `TConfig` via generic `BaseProcessor<TConfig>` | This phase (56) | Author declares a record, never calls `JsonSerializer`; deser-failure handled by framework. |
| `protected abstract ProcessAsync(string,string,ct)` on non-generic base | `internal abstract ExecuteAsync` on base + `protected abstract ProcessAsync(string,TConfig?,ct)` on generic | This phase (56) | Clean break; non-generic base no longer carries the author seam. |

**Deprecated/outdated:** The bare-JSON-string config payload convention (`"StepA1"`) — replaced by an object shape (`{"value":"StepA1"}`) per D-10.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Reflection-based `Deserialize<TConfig>` (not source-gen) is the intended approach; no `JsonSerializerContext` is registered. | Standard Stack / Alternatives | LOW — source-gen would only be a perf/AOT optimization; reflection is correct and matches the "author just declares a record" goal. If AOT is later required, a source-gen context can be added without changing the seam. |
| A2 | The literal `"null"` JSON token returns null (not throws) for a reference-type `TConfig`. | Code Example 3 | LOW — standard STJ behavior; the D-04 guard already covers empty/whitespace, so the only way `"null"` reaches Deserialize is an explicit `null` literal payload, which is an edge case the author's null-handling already covers. |
| A3 | BaseProcessor.Core does NOT grant `InternalsVisibleTo` to the test assembly (SampleProcessorFacts uses reflection, implying no IVT). | Pitfall 3/5 | MEDIUM — if IVT actually exists, the test double can override the `internal` seam directly and the deser-failure test is simpler. Planner should `grep InternalsVisibleTo` in BaseProcessor.Core before choosing the test-double strategy. |

## Open Questions

1. **Marker base: `class` vs `record`, and `abstract` vs concrete?**
   - What we know: SPEC Req 1 wants a field-less type an author config inherits; a `record SampleConfig(...)` pairs most naturally with a `record` base; `abstract` prevents direct instantiation.
   - What's unclear: whether Phase 57 Gate A needs to instantiate or reflect the base type in a way that constrains `abstract`/`record`.
   - Recommendation: `public abstract record ProcessorConfig` (or `abstract class`) — both deserialize fine as a base; pick `record` for symmetry with author records. Defer the Gate-A-driven choice to D-06's "Claude's discretion."

2. **Does `InternalsVisibleTo` exist for the test assembly?** (drives Pitfall 3/5 test strategy)
   - Recommendation: planner greps `InternalsVisibleTo` in `BaseProcessor.Core.csproj` / `AssemblyInfo` at plan time; if absent (likely), the deser-failure test derives a real `BaseProcessor<TConfig>` subclass and drives it through `ProcessorPipeline.RunAsync`.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | Build + hermetic test suite | ✓ (assumed — repo targets net8.0 and has prior green phases) | net8.0 | — |
| System.Text.Json | Config deserialization | ✓ (in-box) | net8.0 BCL | — |

No external services required — the phase is code/config + hermetic tests only (the RealStack round-trip is Phase 58 per D-10). [VERIFIED: SPEC Constraint "no reliance on the live stack for this phase."]

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (v3 — uses `TestContext.Current.CancellationToken`) + NSubstitute |
| Config file | none detected at repo root; per-project test csproj under `tests/BaseApi.Tests` |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "Category!=RealStack&Category!=E2E"` |
| Full suite command | `dotnet test SK_P.sln --filter "Category!=RealStack"` (hermetic; RealStack excluded for Phase 56) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| CFG-01 / Req 3 | Valid object payload → populated `TConfig` reaches the typed seam | unit | `dotnet test --filter "FullyQualifiedName~SampleProcessorFacts"` (rewritten) + new `BaseProcessor<TConfig>` deser unit | ❌ Wave 0 (rewrite SampleProcessorFacts; add generic-deser fact) |
| CFG-01 / Req 4a | Malformed payload → exactly one `StepFailed` (no default, no crash) | integration (pipeline) | `dotnet test --filter "FullyQualifiedName~PipelineInFacts"` (add deser-failure fact) | ❌ Wave 0 (new fact through `RunAsync` with a real `BaseProcessor<TConfig>` subclass) |
| CFG-01 / Req 4b | Empty/whitespace/absent payload → null config to author | unit | `dotnet test --filter "FullyQualifiedName~SampleProcessorFacts"` (Blank fact) | ⚠️ exists (cs:73) — adapt to typed seam |
| CFG-02 / Req 2,5 | `SampleProcessor` uses typed seam; old `(string,string)` seam absent; round-trip token preserved | unit + compile | `dotnet test --filter "FullyQualifiedName~SampleProcessorFacts|BaseProcessorSeamFacts"` | ⚠️ exists — migrate both (Pitfall 2,5) |
| CFG-02 / Req 5 | `fail` value → `FailedException` (`ProcessStatusException` path) | unit | `dotnet test --filter "FullyQualifiedName~SampleProcessorFacts"` (Fail fact) | ⚠️ exists (cs:85) — adapt to `{"value":"fail"}` / `SampleConfig("fail")` |
| Constraint | 0-warning build (Release + Debug) | build | `dotnet build SK_P.sln -c Release` and `-c Debug` | ✅ (TreatWarningsAsErrors=true enforces) |

### Sampling Rate
- **Per task commit:** `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Processor"` (the seam + sample facts).
- **Per wave merge:** `dotnet test SK_P.sln --filter "Category!=RealStack"` (full hermetic suite — catches pipeline-double regressions from Pitfall 3).
- **Phase gate:** `dotnet build -c Release` AND `-c Debug` both 0-warning + full hermetic suite green before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] Rewrite `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` — typed seam + `{"value":...}` payloads (Req 3, 4b, 5).
- [ ] Migrate `tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs` — `TestProcessor` to `BaseProcessor<TConfig>` (Req 2).
- [ ] Adapt `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` `FakeProcessor` to the new seam shape WITHOUT introducing deserialization (keep pipeline facts deser-free — Pitfall 3).
- [ ] Add a NEW deser-failure fact (likely in `PipelineInFacts` or a sibling) driving a real `BaseProcessor<TConfig>` subclass through `ProcessorPipeline.RunAsync` with a malformed payload → assert single `StepFailed` (Req 4a — this path is NOT covered today because `FakeProcessor` never deserializes).
- [ ] Confirm/decide `InternalsVisibleTo` for the test assembly (drives whether the test double overrides the `internal` seam or derives `BaseProcessor<TConfig>`).

*(No new framework install — xUnit/NSubstitute already present.)*

## Sources

### Primary (HIGH confidence)
- `src/BaseProcessor.Core/Processing/BaseProcessor.cs` — current seam shape (`internal ExecuteAsync` forwarder :38, `protected abstract ProcessAsync(string,string)` :29).
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:226-246` — call site + try/catch (`:241` catch-all → StepFailed).
- `src/BaseProcessor.Core/Processing/ProcessStatusException.cs` — `FailedException`/`CancelledException`/`ProcessingException` mapping.
- `src/Processor.Sample/SampleProcessor.cs`, `src/Processor.Sample/Program.cs` — current author + DI registration.
- `tests/BaseApi.Tests/Processor/{SampleProcessorFacts,BaseProcessorSeamFacts,DispatchTestKit,PipelineInFacts}.cs` — the hermetic suite that must migrate.
- `Directory.Build.props` (net8.0, TreatWarningsAsErrors), `src/BaseProcessor.Core/BaseProcessor.Core.csproj`.
- https://learn.microsoft.com/dotnet/api/system.text.json.jsonserializer.deserialize — `JsonException` on invalid JSON / incompatible value; null behavior.

### Secondary (MEDIUM confidence)
- (none beyond the official STJ doc)

### Tertiary (LOW confidence)
- A1/A2/A3 in the Assumptions Log — training-knowledge STJ behaviors and the `InternalsVisibleTo` inference; flagged for plan-time confirmation.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — in-box STJ on a verified net8.0 target; no new dependency.
- Architecture: HIGH — seam shape, call site, and DI all read from current source; the one structural choice (internal abstract) is D-01-sanctioned.
- Pitfalls: HIGH — every pitfall traces to a specific current source line (FakeProcessor, SampleProcessorFacts reflection, non-virtual forwarder).
- STJ failure/null behavior: HIGH (failure → JsonException, verified via MS docs); MEDIUM on the exact `"null"`-literal return (A2).

**Research date:** 2026-06-12
**Valid until:** 2026-07-12 (stable — in-box BCL + locked design; only the codebase touchpoints could drift)
