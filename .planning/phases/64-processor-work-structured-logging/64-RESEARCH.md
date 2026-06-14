# Phase 64: Processor Work & Structured Logging - Research

**Researched:** 2026-06-14
**Domain:** C#/.NET 8 backend — in-process processor transform reshape + structured logging (MEL → OTel)
**Confidence:** HIGH (every claim verified against the actual codebase; no training-data guesses)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** `SampleConfig` becomes `SampleConfig(int Number, string? Label)` — **replaces** the single `string? Value` field. (Field names: `Number` for the int, `Label` for the string.) Still a `sealed record` deriving from `ProcessorConfig`; the v6.0.0 typed seam deserializes the assignment payload `{ "number": N, "label": "Step_*" }` into it.
- **D-02:** **Drop the demo paths** — remove the `"fail"` → `FailedException` worked example and the null-config → `"processor-sample-ok"` fallback token. Every seeded step carries a real config; `processor-badconfig` is excluded from the v8.0.0 stack.
- **D-03:** Null-config edge handling is **Claude's discretion** — the proof always supplies a config; defensive hermetic concern only, not product behavior.
- **D-04:** The completed `ProcessItem.Data` is a **JSON object** carrying the sum and step label, e.g. `{ "number": <sum>, "label": "Step_A1" }` — `number` = `config.Number + random`, `label` = `config.Label` verbatim. Flows to `L2[entryId]`, read as next step's `validatedData`.
- **D-05:** Serialize the output with the framework's shared `ProcessorConfig.SerializerOptions` (same options the seam deserializes with) so input↔output round-trip is symmetric.
- **D-06:** The author still **mints its own `ExecutionId`** per item (carried-forward D-03) — unchanged.
- **D-07:** Random addend is **bounded `Random.Shared.Next(0, 100)`** (0–99 inclusive). Use the framework-shared thread-safe `Random.Shared` (same RNG the pipeline's `SlotTtl()` uses).
- **D-08:** Emit **exactly one** `logger.LogInformation` per execution with **structured params `{StepLabel}` and `{Sum}` only**. The existing `"sample payload received: {Payload}"` log is **replaced** (not added to).
- **D-09:** **Rely on the ambient log scope** for the ids — `correlationId` opened by `InboundCorrelationConsumeFilter`, `workflowId/stepId/processorId/executionId/entryId` by `InboundExecutionScopeConsumeFilter` around the consume; they surface as ES `attributes.*` via the OTel IncludeScopes bridge. Do **not** re-add them as explicit params.
- **D-10:** `{StepLabel}` carries `config.Label` **verbatim** — the seeded label value IS already the full `Step_*` token (e.g. `"Step_A1"`). Do **not** prepend another `Step_` prefix. `{Sum}` is the integer sum.

### Claude's Discretion
- Null-config defensive behavior (D-03): sensible default acceptable — e.g. treat `Number` as `0` and `Label` as null/absent, still emitting one log + one completed item. Default-or-throw is the planner's call; keep "the seam always runs and emits exactly one result + one log."
- Exact JSON property casing/ordering of the D-04 result object (governed by `ProcessorConfig.SerializerOptions`).
- Exact log message template wording around `{StepLabel}`/`{Sum}` (level is Information, one entry).

### Deferred Ideas (OUT OF SCOPE)
- Input/output JSON Schema definitions for the chained fan-out workflow — Phase 65 seeder.
- The 9-step fan-out seeder + per-step `{number, label:"Step_*"}` assignment rows (WF-01/02) — Phase 65.
- `processor-badconfig` exclusion from the stack (ENV-01) — Phase 65.
- The ES/Prometheus analyzer that aggregates by `correlationId` and parses this `Step_*` + sum log shape — Phase 66.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| PROC-01 | A step's payload carries an integer + string; the framework deserializes the assignment payload into the typed config exposing both fields. | `BaseProcessor<TConfig>.ExecuteAsync` already deserializes `payload` → `TConfig` via `ProcessorConfig.SerializerOptions` (case-insensitive). Reshape `SampleConfig` to `(int Number, string? Label)`; `{ "number":N, "label":"Step_*" }` binds case-insensitively. Proven by a deserialization fact. [VERIFIED: BaseProcessor`1.cs:19-26, ProcessorConfig.cs:18-22] |
| PROC-02 | `ProcessAsync` generates a random number, adds it to the payload integer, produces the sum as the completed result. | `Random.Shared.Next(0,100)` + `config.Number`; serialize `{number,label}` JSON string into `ProcessItem.Data`. `Data` is `string` and written verbatim to `L2[entryId]`. [VERIFIED: ProcessItem.cs:7, ProcessorPipeline.cs:275] |
| PROC-03 | `ProcessAsync` emits a structured log tagged `Step_<label>` + computed sum, carrying correlationId+stepId(+workflowId/processorId) so ES aggregates a run by correlationId. | One `LogInformation("…{StepLabel}…{Sum}", config.Label, sum)`; ids come free from the ambient consume-filter scope + OTel `IncludeScopes=true`. [VERIFIED: InboundExecutionScopeConsumeFilter.cs:28-29, BaseConsoleObservabilityExtensions.cs:54-55] |
</phase_requirements>

## Summary

This is a tightly-scoped, two-file product change plus a three-fact test rewrite. The v6.0.0 typed-config seam and the v-?.?.? ambient log-scope plumbing are **already in place and verified** — the author code does almost nothing but transform and log; the framework owns deserialization, id-scoping, OTel export, result-building, and L2 writes. The risk is not architectural; it is getting the exact shapes right (nullability of `int Number`, JSON-string-vs-object for `ProcessItem.Data`, verbatim label, single log entry) and rewriting the hermetic facts to assert the new shape — including the one genuinely tricky bit: asserting structured-param VALUES (`{Sum}`, `{StepLabel}`), not just the formatted string.

The current `SampleProcessor.ProcessAsync` echoes `config?.Value`, logs `"sample payload received: {Payload}"`, throws `FailedException` on `"fail"`, and falls back to `"processor-sample-ok"` on null. All four behaviors change: echo→sum, log template→`{StepLabel}/{Sum}`, drop the `fail` path (D-02), drop the fallback token (D-02). `ProcessItem.Data` is a **`string`** (verified `ProcessItem.cs:7`) written byte-for-byte to Redis (`ProcessorPipeline.cs:275`), so D-04's "JSON object" must be produced via `JsonSerializer.Serialize(new {...}, ProcessorConfig.SerializerOptions)` into a string — the author serializes; the framework does not.

**Primary recommendation:** Reshape `SampleConfig` to `sealed record SampleConfig(int Number, string? Label) : ProcessorConfig`; rewrite `ProcessAsync` to compute `sum = config.Number + Random.Shared.Next(0,100)`, serialize `{ number = sum, label = config.Label }` to a JSON string with `ProcessorConfig.SerializerOptions` as `ProcessItem.Data`, mint `Guid.NewGuid()` ExecutionId, and emit exactly one `LogInformation` with `{StepLabel}`=`config.Label` and `{Sum}`=`sum`; for null-config, default `Number=0`/`Label=null` and still emit one log + one item. Rewrite all three facts. No framework files change.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Payload → typed config deserialization | Framework (`BaseProcessor<TConfig>`) | — | Already owned at `BaseProcessor`1.cs:22-24`; author never deserializes. PROC-01 is satisfied by the config SHAPE, not by author code. |
| Random + sum transform | Author (`SampleProcessor.ProcessAsync`) | — | The only product compute. PROC-02. |
| Output JSON serialization (`{number,label}`) | Author (`ProcessAsync`) | Framework (`SerializerOptions`) | `ProcessItem.Data` is a `string`; the author must serialize using the shared options (D-05). |
| L2 write of `Data` | Framework (`ProcessorPipeline`) | Redis | `StringSetAsync(ExecutionData(entryId), item.Data, …)` at `:275`. Author returns the string only. |
| Structured log emission | Author (`ProcessAsync`) | Framework (OTel bridge) | Author emits one MEL `LogInformation`; framework exports it. PROC-03. |
| correlationId/stepId/workflowId/processorId/executionId/entryId attributes | Framework (consume filters + OTel `IncludeScopes`) | — | Ambient scope; author MUST NOT re-add (D-09). |
| ExecutionId minting | Author (`ProcessAsync`) | — | `Guid.NewGuid()` per item (D-06), carried forward. |

## Standard Stack

### Core (all already referenced — NO new packages)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `System.Text.Json` | net8.0 BCL | Serialize the `{number,label}` output object | Same serializer the seam deserializes with (`ProcessorConfig.SerializerOptions`); D-05 symmetry. [VERIFIED: ProcessorConfig.cs:1,18] |
| `Microsoft.Extensions.Logging.Abstractions` | (transitive) | `ILogger<SampleProcessor>` — already injected | Existing constructor param. [VERIFIED: SampleProcessor.cs:18] |
| `System.Random` (`Random.Shared`) | net8.0 BCL | Bounded random addend | Framework-shared thread-safe RNG; same one `SlotTtl()` uses. [VERIFIED: ProcessorPipeline.cs:81-83] |
| `xunit` | (test project existing) | Hermetic facts | Existing `SampleProcessorFacts` uses `[Fact]`, `Assert.Single`, `Assert.Equal`. [VERIFIED: SampleProcessorFacts.cs:5,54] |

**Installation:** None. This phase adds zero NuGet packages. The reshape uses only BCL types and already-referenced abstractions.

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `JsonSerializer.Serialize(new { number, label }, SerializerOptions)` | Hand-built JSON string interpolation | Hand-rolling breaks escaping for the `label` string and violates D-05 symmetry — never do this. |
| `Random.Shared` | `new Random()` per call | A fresh `Random()` is not the framework-shared RNG (D-07) and risks seed-collision under load; the codebase mandates `Random.Shared`. |

## Architecture Patterns

### System Architecture Diagram (data flow for one step execution)

```
EntryStepDispatch (MassTransit consume)
        │  carries Payload (JSON string), CorrelationId, WorkflowId, StepId, ProcessorId, EntryId
        ▼
InboundCorrelationConsumeFilter ── opens log scope: CorrelationId
        │
InboundExecutionScopeConsumeFilter ── opens log scope: WorkflowId/StepId/ProcessorId/ExecutionId/EntryId
        │   (ExecutionLogScope.BuildState — Guid.Empty keys skipped)
        ▼
ProcessorPipeline.RunForwardAsync  (IN stage, ~:226)
        │   processor.ExecuteAsync(validatedData, d.Payload, ct)
        ▼
BaseProcessor<SampleConfig>.ExecuteAsync  (BaseProcessor`1.cs:19)
        │   payload empty/whitespace? → null config (D-04 guard)
        │   else JsonSerializer.Deserialize<SampleConfig>(payload, SerializerOptions)   ← PROC-01
        ▼
SampleProcessor.ProcessAsync(validatedData, SampleConfig? config, ct)   ◄── THE ONLY CODE THIS PHASE CHANGES
        │   sum = config.Number + Random.Shared.Next(0,100)                              ← PROC-02
        │   data = JsonSerializer.Serialize(new { number = sum, label = config.Label }, SerializerOptions)
        │   logger.LogInformation("...{StepLabel}...{Sum}", config.Label, sum)           ← PROC-03 (ids inherited from ambient scope)
        │   return [ new ProcessItem(Completed, data, Guid.NewGuid()) ]                  ← D-06
        ▼
ProcessorPipeline POST stage (~:248-298)
        │   output-schema validation vs context.OutputDefinition (null-is-skip, :254)
        │   StringSetAsync(L2.ExecutionData(entryId), item.Data, ttl)   (:275)  → L2[entryId]
        │   BuildCompleted(d, item.ExecutionId, entryId) → SendResult → orchestrator (:290)
        ▼
   next chained step reads L2[entryId] as its validatedData (Phase 65 fan-out)
   OTel exporter ships the LogInformation → Elasticsearch (correlationId + 5 ids as attributes.*)  ← Phase 66 parses this
```

### Recommended Project Structure (no structural change)
```
src/Processor.Sample/
├── SampleConfig.cs        # reshape record (1 line of substance)
└── SampleProcessor.cs     # rewrite ProcessAsync body
tests/BaseApi.Tests/Processor/
└── SampleProcessorFacts.cs  # rewrite 3 facts → 3 (or 4) new facts
```

### Pattern 1: Author serializes output to a JSON string with the shared options
**What:** `ProcessItem.Data` is a `string`, not an object. The "JSON object" of D-04 is a serialized string the author produces.
**When to use:** Always for this phase — the framework writes `item.Data` to Redis verbatim.
**Example:**
```csharp
// Source: ProcessorConfig.cs:18 (SerializerOptions) + ProcessItem.cs:7 (Data is string) + ProcessorPipeline.cs:275 (written verbatim)
var sum  = config.Number + Random.Shared.Next(0, 100);        // D-07 (0..99 inclusive)
var data = JsonSerializer.Serialize(
    new { number = sum, label = config.Label },               // D-04 field names; D-05 shared options
    ProcessorConfig.SerializerOptions);
```
> Note on casing (D-05/Claude's discretion): `ProcessorConfig.SerializerOptions` sets only `PropertyNameCaseInsensitive = true` — it does NOT set `PropertyNamingPolicy = CamelCase`. With an anonymous object whose members are already lowercase (`number`, `label`), the serialized output is `{"number":...,"label":...}` regardless. Using lowercase anonymous-member names is the safe, explicit way to guarantee the `number`/`label` output keys (and is symmetric with the case-insensitive input bind). [VERIFIED: ProcessorConfig.cs:18-22]

### Pattern 2: One MEL log line; ids inherited from ambient scope (D-09)
**What:** Emit a single `LogInformation` with ONLY `{StepLabel}` + `{Sum}` as template params. The 6 ids attach automatically.
**Why it works:** `InboundExecutionScopeConsumeFilter.Send` wraps the consume in `logger.BeginScope(ExecutionLogScope.BuildState(ec))` (5 ids), `InboundCorrelationConsumeFilter` adds CorrelationId, and `BaseConsoleObservabilityExtensions` sets `IncludeScopes=true` + `ParseStateValues=true` on the OTel logger — so scope KVPs surface as `attributes.<Key>`. [VERIFIED: InboundExecutionScopeConsumeFilter.cs:28, BaseConsoleObservabilityExtensions.cs:54-55]
**Example:**
```csharp
// Source: T-18-04 structured-param security pattern (CONTEXT code_context) — values as params, never interpolated
logger.LogInformation("step completed {StepLabel} sum {Sum}", config.Label, sum);  // D-08/D-10
```

### Anti-Patterns to Avoid
- **Re-adding ids as log params** — `LogInformation("... {CorrelationId} {StepId} ...")`. D-09 forbids it; the ambient scope already supplies them. Duplicating risks double `attributes.CorrelationId` and key drift.
- **Interpolating the label into the template** — `LogInformation($"step {config.Label}")`. Breaks structured logging (no `{StepLabel}` attribute) and the T-18-04 security pattern. Always pass as a param.
- **Prepending `Step_`** — `$"Step_{config.Label}"`. D-10: the seeded label IS already `Step_A1`. Double-prefix produces `Step_Step_A1`.
- **Hand-building the output JSON** — string interpolation breaks escaping and D-05 symmetry. Use `JsonSerializer.Serialize` with the shared options.
- **Emitting more than one log** — keeping the old "sample payload received" log AND a new one violates the one-entry-per-execution invariant (D-08, PROC-03 success criterion 3). The old log is **replaced**, not supplemented.
- **`new Random()`** — must be `Random.Shared` (D-07).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Payload → typed config | A manual `JsonDocument` parse in `ProcessAsync` | The framework seam already hands you `SampleConfig? config` | `BaseProcessor<TConfig>.ExecuteAsync` does it via the shared options; author never touches the raw payload. [VERIFIED: BaseProcessor`1.cs:22-24] |
| id attributes on the log line | Manual `BeginScope` of correlationId/stepId in `ProcessAsync` | Ambient consume-filter scope (D-09) | Already opened bus-wide; re-doing it duplicates keys. [VERIFIED: InboundExecutionScopeConsumeFilter.cs:28] |
| Output JSON string | Interpolated `"{\"number\":" + sum + …` | `JsonSerializer.Serialize(new {number,label}, SerializerOptions)` | Escaping + D-05 symmetry. |
| Thread-safe RNG | `new Random()` / `[ThreadStatic]` | `Random.Shared` | BCL-provided, already the house RNG. [VERIFIED: ProcessorPipeline.cs:81] |

**Key insight:** This phase is almost entirely "delete demo code and wire two BCL calls." Every supporting concern (deserialize, scope, export, L2 write, result build) is owned by already-verified framework code. The author footprint is ~6 lines.

## Common Pitfalls

### Pitfall 1: Treating `ProcessItem.Data` as an object
**What goes wrong:** Planner specs "return a JSON object" and an implementer tries to set `Data` to an anonymous object or `JsonDocument`.
**Why it happens:** D-04 says "JSON object," but `ProcessItem` is `record ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId)` — `Data` is a `string`. [VERIFIED: ProcessItem.cs:7]
**How to avoid:** The author serializes to a string. The "object" lives only as the JSON shape inside that string.
**Warning signs:** Compile error `cannot convert object to string`, or a stringified `.ToString()` of an anonymous type.

### Pitfall 2: `int Number` nullability / non-nullable-warning on null config
**What goes wrong:** `config.Number` when `config` may be null → CS8602 (dereference of possibly-null) → build error (TreatWarningsAsErrors). [VERIFIED: Directory.Build.props:35]
**Why it happens:** The seam's signature is `SampleConfig? config` (nullable). D-04's empty-payload guard hands a real null. With `Nullable=enable`, dereferencing without a guard is a hard error.
**How to avoid:** Guard the null first (D-03 discretion): e.g. `var number = config?.Number ?? 0; var label = config?.Label;` then compute on the locals. This keeps "always emits one log + one item" and is warning-clean.
**Warning signs:** CS8602/CS8604 at build; Release+Debug both fail (TreatWarningsAsErrors is global).

### Pitfall 3: `Random.Shared.Next(0, 100)` bound semantics
**What goes wrong:** Assuming inclusive-100 or off-by-one in a test that asserts a sum range.
**Why it happens:** `Random.Next(min, max)` is `min`-inclusive, `max`-EXCLUSIVE — so `Next(0,100)` yields 0..99. [CITED: learn.microsoft.com/dotnet/api/system.random.next] D-07 explicitly says "0–99 inclusive," matching exclusive-upper. A range fact must assert `sum >= config.Number && sum <= config.Number + 99`.
**How to avoid:** Encode the bound as `Number + Random.Shared.Next(0,100)` and any range test uses `[Number, Number+99]`.
**Warning signs:** Flaky test that fails ~1% when the addend lands at the boundary.

### Pitfall 4: Asserting log CONTENT vs log SCOPE in the hermetic fact (THE RISKIEST UNKNOWN — RESOLVED)
**What goes wrong:** Trying to assert correlationId/stepId attributes inside `SampleProcessorFacts` — but those ids come from the ambient consume-filter scope, which does NOT exist in the reflection-invoked unit harness.
**Why it happens:** The existing `CapturingLogger.BeginScope` returns a `NullScope` that swallows scope state (`SampleProcessorFacts.cs:30,37-41`) — and even the real `LogInformation` in `ProcessAsync` opens no scope (D-09: ids are ambient, set by filters outside the unit). So a unit fact **cannot and should not** assert the 6 ids.
**How to avoid (planner guidance):**
- The hermetic facts assert what `ProcessAsync` *produces*: the `{number,label}` JSON `Data`, the single log entry, and the message contains the label + sum.
- To assert the structured-param **values** (`{StepLabel}`, `{Sum}`) rather than only the formatted string, extend `CapturingLogger.Log` to also capture `state` as `IReadOnlyList<KeyValuePair<string,object?>>` (MEL's `FormattedLogValues` implements this) and assert `entry.State` contains `("StepLabel", "Step_A1")` and `("Sum", sum)`. This is a small, well-trodden test pattern. The current `CapturingLogger` captures only `formatter(state, exception)` (the formatted string) — `SampleProcessorFacts.cs:33-35` — so capturing `state` is a minor extension.
- PROC-03's "ids surface as attributes via IncludeScopes" is proven *separately and already* by the existing `ProcessorIdEnricherTests` / `LogExportTests` (OTel `LoggerFactory` + capturing processor pattern, `ProcessorIdEnricherTests.cs:64-77`) and by the consume-filter facts — it is NOT this phase's burden to re-prove the bridge. This phase proves only "exactly one `Step_<label>` log with the sum is emitted."
**Warning signs:** A fact that asserts `attributes.CorrelationId` and finds nothing — that id is filter-supplied, not author-supplied.

### Pitfall 5: Forgetting the demo paths still referenced by tests
**What goes wrong:** Reshaping the processor but leaving `FailedException`/`processor-sample-ok` assertions in the facts → compile (still valid) but logically stale + failing.
**Why it happens:** Three facts hard-code the old shape (`SampleProcessorFacts.cs:54-100`).
**How to avoid:** All three facts are rewritten/replaced in lockstep with the processor change (see Validation Architecture). `FailedException` may even become unused — check no other reference before/if removing it (it lives in `BaseProcessor.Core`, used by the pipeline catch, so the type stays; just the *demo throw* goes).

### Pitfall 6: `processor-sample-ok` fallback removal vs null-config "always emits one item"
**What goes wrong:** Dropping the fallback token (D-02) and ALSO returning zero items on null config → violates "the seam always runs and emits exactly one result + one log."
**Why it happens:** Conflating "drop the magic token" with "drop the null path entirely."
**How to avoid:** D-03 discretion: on null config, still emit one item (e.g. `{number:0+rand, label:null}`) and one log. Drop only the *token string*, not the always-one-item invariant.

## Code Examples

### Reshaped `SampleConfig.cs` (verbatim target)
```csharp
// Source: SampleConfig.cs:10 (current) → reshape per D-01
using BaseProcessor.Core.Configuration;

namespace Processor.Sample;

public sealed record SampleConfig(int Number, string? Label) : ProcessorConfig;
```

### Reshaped `ProcessAsync` body (illustrative — planner owns final wording)
```csharp
// Source: SampleProcessor.cs:21-36 (current) → rewrite per D-02/04/05/06/07/08/10
protected override Task<List<ProcessItem>> ProcessAsync(
    string validatedData, SampleConfig? config, CancellationToken ct)
{
    var baseNumber = config?.Number ?? 0;            // D-03 null-config default (warning-clean, Pitfall 2)
    var label      = config?.Label;                  // D-10 verbatim (already "Step_*")
    var sum        = baseNumber + Random.Shared.Next(0, 100);   // D-07 (0..99 inclusive)

    var data = JsonSerializer.Serialize(
        new { number = sum, label },                 // D-04 shape; D-05 shared options
        ProcessorConfig.SerializerOptions);

    logger.LogInformation("step completed {StepLabel} sum {Sum}", label, sum);  // D-08/D-09/D-10 — exactly one, ids ambient

    return Task.FromResult(new List<ProcessItem>
    {
        new(ProcessOutcome.Completed, data, Guid.NewGuid()),    // D-06 author-minted ExecutionId
    });
}
```
> Requires adding `using System.Text.Json;` (for `JsonSerializer`) and `using BaseProcessor.Core.Configuration;` (for `ProcessorConfig.SerializerOptions`) to `SampleProcessor.cs`. With `ImplicitUsings=enable`, `System.Text.Json` is NOT auto-included (it is not in the .NET 8 default implicit-usings set) — add it explicitly. [VERIFIED: BaseProcessor`1.cs:1 adds it explicitly]

### Capturing structured-param VALUES in the hermetic fact (test-harness extension)
```csharp
// Extension of SampleProcessorFacts.cs CapturingLogger (lines 26-42) to capture state KVPs
public List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = new();

public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
    Func<TState, Exception?, string> formatter)
{
    var kvps = state as IReadOnlyList<KeyValuePair<string, object?>>
               ?? Array.Empty<KeyValuePair<string, object?>>();   // MEL FormattedLogValues implements this
    Entries.Add((logLevel, formatter(state, exception), kvps));
}
// Then in a fact:
//   var only = Assert.Single(logger.Entries);
//   Assert.Equal(LogLevel.Information, only.Level);
//   Assert.Contains(only.State, kv => kv.Key == "StepLabel" && (string?)kv.Value == "Step_A1");
//   Assert.Contains(only.State, kv => kv.Key == "Sum" && (int)kv.Value! == /* baseNumber + addend */ ...);
```

## State of the Art

| Old Approach (current code) | Current Approach (this phase) | Why |
|--------------|------------------|--------|
| `SampleConfig(string? Value)` | `SampleConfig(int Number, string? Label)` | PROC-01 typed int+string. |
| Echo `config?.Value` as `Data` | Serialize `{number:sum, label}` JSON string | PROC-02 sum result + Phase 66 parseable shape. |
| `"sample payload received: {Payload}"` log | `"...{StepLabel}...{Sum}"` single log | PROC-03 + D-08 one-entry invariant. |
| `"fail"` → `FailedException` demo | (removed) | D-02 — no `processor-badconfig` in v8.0.0 stack. |
| null → `"processor-sample-ok"` token | null → default `{number:rand, label:null}`, still one item/log | D-02/D-03 — drop magic token, keep always-one invariant. |

**Deprecated/outdated:** The `FailedException` *demo throw* and `processor-sample-ok` token are removed from author code. The `FailedException` *type* stays (used by `ProcessorPipeline.cs:231` catch) — do not delete the type.

## Runtime State Inventory

> This is a code-shape change to an in-process transform, not a rename/migration. No stored data, OS state, or registered services carry the changed strings. Included for completeness because the field rename `Value→Number/Label` could in theory leak into stored payloads.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None — the OLD `{"value":...}` payload shape exists only in seeded assignment rows, which are reseeded by Phase 65 (the 9-step fan-out seeder writes the NEW `{number,label}` rows). No persisted v8.0.0 data carries the old shape. | None this phase. |
| Live service config | None — `SampleConfig` is a code type, not external config. | None. |
| OS-registered state | None. | None. |
| Secrets/env vars | None — no secret or env var references `Value`/`Number`/`Label`. | None. |
| Build artifacts | None requiring action — standard rebuild recompiles `Processor.Sample` + `BaseApi.Tests`. No egg-info/binary-tag equivalents. | Normal `dotnet build`. |

**Nothing found requiring migration** — verified: the only consumers of `SampleConfig`'s shape are `SampleProcessor`, `SampleProcessorFacts`, and the (deferred, Phase 65) seeder rows.

## Validation Architecture

**Nyquist enabled** (no `workflow.nyquist_validation: false` found). Test framework: **xUnit**, project `tests/BaseApi.Tests`, facts file `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs`.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (existing — `[Fact]`, `Assert.*`) [VERIFIED: SampleProcessorFacts.cs:5] |
| Config file | none separate — standard `dotnet test` on `BaseApi.Tests.csproj` |
| Harness pattern | Reflection-invoke the `protected ProcessAsync` (no `InternalsVisibleTo`) + a `CapturingLogger` fake. [VERIFIED: SampleProcessorFacts.cs:44-52, 26-42] |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~SampleProcessorFacts"` |
| Full suite command | `dotnet test` (solution) — must be green (Success Criterion 4) |
| Build gate | `dotnet build -c Release` AND `dotnet build -c Debug` — 0 warnings (TreatWarningsAsErrors global). [VERIFIED: Directory.Build.props:35] |

### Existing Facts (OLD shape) → Required NEW assertions
| Existing Fact (file:line) | Asserts (OLD) | NEW assertion (this phase) |
|---------------------------|---------------|----------------------------|
| `ProcessAsync_Receives_Typed_Config_Logs_It_And_Echoes_It` (:54) | `only.Data == "StepA1"`; log contains `"sample payload received"` + `"StepA1"` | Given `SampleConfig(Number, "Step_A1")`: `only.Data` is JSON `{number:<sum>, label:"Step_A1"}` with `sum ∈ [Number, Number+99]`; `only.Result==Completed`; `only.ExecutionId != Guid.Empty`; **exactly one** log; log `State` carries `("StepLabel","Step_A1")` and `("Sum", sum)`. |
| `ProcessAsync_Null_Config_Falls_Back_To_Fixed_Token` (:74) | `only.Data == "processor-sample-ok"`; single log | Given null config: still **one** item + **one** log; `Data` is JSON `{number:<rand>, label:null}` (or chosen default); `label` attribute null/absent — proves "seam always runs" without the dropped token (D-02/D-03). |
| `ProcessAsync_Fail_Config_Throws_FailedException` (:88) | `"fail"` → `TargetInvocationException(FailedException)` | **Delete** (D-02 removes the demo throw). Optionally replace with a deserialization fact proving `JsonSerializer.Deserialize<SampleConfig>("{\"number\":5,\"label\":\"Step_A1\"}", ProcessorConfig.SerializerOptions)` binds both fields case-insensitively → PROC-01 deserialization fact. |

### Success Criteria + PROC → concrete proof map
| Criterion / Req | Proof (test or build assertion) |
|-----------------|---------------------------------|
| SC#1 / PROC-01 (typed int+string deserialized) | NEW deserialization fact: `JsonSerializer.Deserialize<SampleConfig>("{\"number\":N,\"label\":\"Step_*\"}", ProcessorConfig.SerializerOptions)` yields `Number==N`, `Label=="Step_*"`. Case-insensitive bind asserted. |
| SC#2 / PROC-02 (random sum is the result) | Rewritten fact 1: parse `only.Data` JSON, assert `number ∈ [Number, Number+99]` and `label == "Step_A1"`; structure deterministic, addend non-deterministic (assert range, not exact). |
| SC#3 / PROC-03 (exactly one `Step_<label>` log + sum, ids aggregatable by correlationId) | Rewritten fact 1: `Assert.Single(logger.Entries)`; `State` contains `("StepLabel","Step_A1")` + `("Sum",sum)`. The "ids surface as ES attributes via IncludeScopes" half is already proven by the consume-filter facts + `ProcessorIdEnricherTests` / `LogExportTests` (out-of-phase, not re-proven here). |
| SC#4 (0-warning Release+Debug; suite green) | `dotnet build -c Release` + `-c Debug` exit 0 (TreatWarningsAsErrors); `dotnet test` green. The reshape must be nullable-clean (Pitfall 2). |

### Sampling Rate
- **Per task commit:** `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~SampleProcessorFacts"` + `dotnet build -c Debug`.
- **Per wave merge:** `dotnet build -c Release` + `dotnet build -c Debug` + `dotnet test` (full suite).
- **Phase gate:** Full suite green + both configs 0-warning before `/gsd-verify-work`.

### Wave 0 Gaps
- None — `SampleProcessorFacts.cs` + `CapturingLogger` already exist; the only "gap" is a minor `CapturingLogger.Log` extension to capture `state` KVPs (see Code Examples). No new test file or framework install required.

## Project Constraints (from Directory.Build.props — no CLAUDE.md present)
- `TreatWarningsAsErrors=true` solution-wide → the reshape must be 0-warning in **both** Release and Debug. [VERIFIED: Directory.Build.props:35]
- `Nullable=enable` → `config?.Number ?? 0` style null-guarding required before dereferencing the nullable `SampleConfig? config` (Pitfall 2). [VERIFIED: Directory.Build.props:30]
- `EnforceCodeStyleInBuild=true`, `AnalysisMode=latest` → analyzer/style issues are build errors. [VERIFIED: Directory.Build.props:33-34]
- `ImplicitUsings=enable` but `System.Text.Json` is NOT in the .NET 8 implicit set → add `using System.Text.Json;` explicitly. [VERIFIED: Directory.Build.props:31, BaseProcessor`1.cs:1]
- No CLAUDE.md exists at repo root [VERIFIED: Read returned "File does not exist"].

## Security Domain

> `security_enforcement` default-enabled. This phase is in-process compute + logging; minimal attack surface.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V5 Input Validation | yes (boundary) | The payload→`SampleConfig` deserialize is bounded by the framework's `SerializerOptions` (unknown members ignored, not rejected — D-05). Output JSON schema validation is null-is-skip at `ProcessorPipeline.cs:254` (deferred to Phase 65 seeder). |
| V7 Logging | yes | T-18-04 pattern: `config.Label` is passed as a **structured param** `{StepLabel}`, never interpolated into the template — prevents log-injection/forging. [VERIFIED: CONTEXT code_context "structured params … never interpolated"] |
| V6 Cryptography | no | `Random.Shared` is non-crypto by design (D-07) — acceptable; the addend is a demo value, not a security token. |
| V2/V3/V4 (auth/session/access) | no | No auth/session/access surface in an in-process transform. |

### Known Threat Patterns
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Log injection via attacker-controlled `label` | Tampering/Repudiation | Structured param `{StepLabel}` (not string interpolation) — value lands in `attributes.StepLabel`, never alters the template. [VERIFIED pattern in InboundExecutionScopeConsumeFilter security note :13-15] |
| Unbounded payload int → overflow on sum | Tampering | D-07 bounds the addend to 0..99; sum overflow only at `int.MaxValue-99` payloads (out of realistic range; D-07 calls this "overflow-safe for any reasonable payload int"). Planner may note (not require) a guard. |

## Sources

### Primary (HIGH confidence — codebase, verified this session)
- `src/Processor.Sample/SampleConfig.cs:10` — current record to reshape
- `src/Processor.Sample/SampleProcessor.cs:18-37` — current `ProcessAsync` (echo/fail/fallback/log)
- `src/BaseProcessor.Core/Processing/BaseProcessor`1.cs:19-36` — seam: deserialize payload→TConfig, signature `(string, TConfig?, CancellationToken)` → `Task<List<ProcessItem>>`
- `src/BaseProcessor.Core/Configuration/ProcessorConfig.cs:18-22` — shared `SerializerOptions` (case-insensitive, unknown-ignore; NO camelCase policy)
- `src/BaseProcessor.Core/Processing/ProcessItem.cs:7` — `Data` is `string`; `ProcessOutcome.cs:5` Completed/Failed
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:226,254,275,290,363` — IN stage, null-is-skip output validation, `StringSetAsync(...item.Data...)`, `BuildCompleted`, `Random.Shared`/`SlotTtl():81-83`
- `src/Messaging.Contracts/ExecutionLogScope.cs:11-34` — 5 execution-id scope keys + `BuildState`
- `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs:22-30` — opens ambient scope around consume
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs:51-55` — OTel `IncludeScopes=true` + `ParseStateValues=true` (the bridge)
- `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs:26-100` — 3 facts + `CapturingLogger` (NullScope swallows scope) + reflection invoke
- `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs:64-101` — OTel `LoggerFactory`+capturing-processor pattern for attribute assertions
- `Directory.Build.props:30-41` — Nullable/TreatWarningsAsErrors/AnalysisMode strictness bundle
- `.planning/REQUIREMENTS.md:24-26` — PROC-01/02/03 verbatim

### Secondary (MEDIUM confidence)
- `.planning/phases/64-processor-work-structured-logging/64-CONTEXT.md` — locked decisions D-01..D-10

### Tertiary (LOW confidence)
- None requiring validation — all claims codebase-verified.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `MEL` `FormattedLogValues` (the `state` passed to `ILogger.Log` for a templated message) implements `IReadOnlyList<KeyValuePair<string,object?>>`, so the test can capture `{StepLabel}`/`{Sum}` values. | Code Examples / Pitfall 4 | If the cast fails the test falls back to asserting the formatted string only (still proves single-log + label/sum presence). LOW risk — this is documented MEL behavior. [ASSUMED, well-established] |
| A2 | Lowercase anonymous-member names (`number`,`label`) suffice to get `{"number":…,"label":…}` output keys without setting a `PropertyNamingPolicy`. | Pattern 1 | If the planner wants guaranteed camelCase regardless of member casing, they may set a naming policy — but `SerializerOptions` deliberately does NOT, and D-05 mandates using it as-is. Output keys match the lowercase members. [VERIFIED via ProcessorConfig.cs:18-22 + STJ default behavior] |

## Open Questions

1. **Should null-config return `{number: rand, label: null}` or `{number: 0, label: null}`?**
   - What we know: D-03 leaves the default to the planner; "seam always runs, one item + one log."
   - What's unclear: Whether `number` on null-config should still get the random addend (rand) or be a clean `0`.
   - Recommendation: `number = 0 + Random.Shared.Next(0,100)` (i.e. treat `Number` as 0 and still add) keeps ProcessAsync uniform (one code path) and warning-clean; the proof never hits this. Either is acceptable per D-03.

2. **Does the PROC-01 deserialization fact replace the deleted `fail` fact, or is it added?**
   - What we know: SC#1 wants "a deserialization fact"; the `fail` fact is deleted (D-02).
   - Recommendation: Replace the deleted slot with a deserialization fact — keeps the file at 3 facts, directly proves PROC-01.

## Environment Availability

> Code/test-only change. The only tooling is the .NET 8 SDK, already required by every prior phase.

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK (`dotnet build`/`test`) | build + hermetic suite | ✓ (assumed — net8.0 TFM, 63 phases built on it) | net8.0 | — |

No external services (Redis/RabbitMQ/ES) are needed for this phase's hermetic facts — the unit harness reflection-invokes `ProcessAsync` directly with a fake logger.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — zero new packages; all types verified present in-tree.
- Architecture: HIGH — seam, scope, pipeline, OTel bridge all read and line-cited this session.
- Pitfalls: HIGH — derived from actual signatures (`ProcessItem.Data:string`, `SampleConfig? config`, TreatWarningsAsErrors) not training data.
- Test harness (the flagged risk): HIGH on mechanism (CapturingLogger + reflection verified), MEDIUM on the `state`-capture micro-pattern (A1, well-established but not run here).

**Research date:** 2026-06-14
**Valid until:** 2026-07-14 (stable — internal codebase, no fast-moving external deps)
