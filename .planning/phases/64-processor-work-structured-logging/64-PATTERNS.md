# Phase 64: Processor Work & Structured Logging - Pattern Map

**Mapped:** 2026-06-14
**Files analyzed:** 4 (3 product/test modifies + 1 in-file test-harness extension)
**Analogs found:** 4 / 4 (every modified file has an in-tree analog; one shared pattern has NO direct call-site analog — flagged below)

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Processor.Sample/SampleConfig.cs` (MODIFY: `string? Value` → `int Number, string? Label`) | model / config record | transform (payload→typed config) | itself (current `sealed record … : ProcessorConfig`) | exact (self-reshape) |
| `src/Processor.Sample/SampleProcessor.cs` (MODIFY: `ProcessAsync` body) | service / processor seam | request-response → transform | itself (current `ProcessAsync`) + `BaseProcessor`1.cs` seam signature | exact (self-rewrite) |
| `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` (MODIFY: rewrite 3 facts) | test | request-response (reflection invoke) | itself (existing 3 facts + `CapturingLogger`) | exact (self-rewrite) |
| `CapturingLogger` (in `SampleProcessorFacts.cs`, MODIFY: also capture `state` KVPs) | test fixture / fake | event-capture | itself (current `Entries` list) | exact (minor extension) |

**Key:** all four targets are self-modifications of an existing, working shape. The "analog" the planner replicates is therefore the **current form of the same file** (style + structure to preserve) plus, for the new compute/log/serialize lines, the **cross-file style analogs** in `## Shared Patterns` below.

---

## Pattern Assignments

### `src/Processor.Sample/SampleConfig.cs` (model, transform)

**Analog:** itself + the `ProcessorConfig` marker base it derives from.

**Current form** (`SampleConfig.cs:1,3,10`):
```csharp
using BaseProcessor.Core.Configuration;

namespace Processor.Sample;

public sealed record SampleConfig(string? Value) : ProcessorConfig;
```

**Base it derives from — keep the inheritance + the shared options live here** (`ProcessorConfig.cs:10,18-22`):
```csharp
public abstract record ProcessorConfig
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,   // D-05 — this is why {"number":N,"label":"…"} binds case-insensitively
    };
}
```

**Reshape (D-01):** keep `using BaseProcessor.Core.Configuration;`, keep `sealed record … : ProcessorConfig`, change only the positional parameters:
```csharp
public sealed record SampleConfig(int Number, string? Label) : ProcessorConfig;
```
- `Number` is **non-nullable `int`** (Pitfall 2: `config` itself is `SampleConfig?`, so guard `config?.Number` before deref — do NOT make `Number` nullable to dodge the warning).
- Update the XML doc comment (`SampleConfig.cs:5-9`) — it references `{"value":"StepA1"}`; rewrite to `{"number":N,"label":"Step_*"}`.

---

### `src/Processor.Sample/SampleProcessor.cs` (service, transform)

**Analog:** current `ProcessAsync` (structure to keep: `protected override`, return `Task.FromResult(new List<ProcessItem>{ new(Completed, …, Guid.NewGuid()) })`, single `LogInformation`) + the framework seam signature it overrides.

**Framework seam signature it MUST match** (`BaseProcessor`1.cs:34-35` — abstract) and the caller that deserializes into `config` (`BaseProcessor`1.cs:19-26`):
```csharp
// abstract seam the author overrides — signature is fixed:
protected abstract Task<List<ProcessItem>> ProcessAsync(
    string validatedData, TConfig? config, CancellationToken ct);

// framework forwarder (already deserializes payload → SampleConfig with the shared options):
internal sealed override Task<List<ProcessItem>> ExecuteAsync(
    string validatedData, string payload, CancellationToken ct)
{
    TConfig? config = string.IsNullOrWhiteSpace(payload)
        ? null
        : JsonSerializer.Deserialize<TConfig>(payload, ProcessorConfig.SerializerOptions);
    return ProcessAsync(validatedData, config, ct);
}
```
> This is the **Serialize-direction analog by symmetry**: input uses `JsonSerializer.Deserialize<TConfig>(payload, ProcessorConfig.SerializerOptions)`; the new output line uses the SAME options on the Serialize side (D-05). There is **no existing `JsonSerializer.Serialize(obj, SerializerOptions)` call site** in the codebase to copy (the two existing `Serialize` calls — `ProcessorLivenessWriter.cs:69`, `RedisProjectionWriter.cs:73,100` — deliberately use DEFAULT options for pinned wire shapes). Mirror the Deserialize line, not those.

**Current body to REPLACE** (`SampleProcessor.cs:21-36`):
```csharp
protected override Task<List<ProcessItem>> ProcessAsync(
    string validatedData, SampleConfig? config, CancellationToken ct)
{
    var value = config?.Value;                       // D-04: null config → null value
    logger.LogInformation("sample payload received: {Payload}", value);

    if (value == "fail")                             // ← DROP (D-02 demo throw)
        throw new FailedException("sample reason");

    return Task.FromResult(new List<ProcessItem>
    {
        new(ProcessOutcome.Completed, value ?? "processor-sample-ok", Guid.NewGuid()),  // ← DROP fallback token (D-02)
    });
}
```

**Result-builder pattern to KEEP** (the `ProcessItem` ctor + author-minted `ExecutionId`, `ProcessItem.cs:7`):
```csharp
public sealed record ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId);
// → new(ProcessOutcome.Completed, data, Guid.NewGuid())   — Data is a STRING (Pitfall 1), ExecutionId author-minted (D-06)
```

**Random addend pattern to COPY** (`ProcessorPipeline.cs:81-83` — the house RNG, exclusive upper bound):
```csharp
// Random.Shared is the framework-shared thread-safe RNG; max is exclusive → Next(0,100) yields 0..99 (D-07, Pitfall 3)
Random.Shared.Next(slotOptions.Value.SlotArrayTtlMinSeconds, slotOptions.Value.SlotArrayTtlMaxSeconds + 1)
```

**Rewrite target** (D-02/04/05/06/07/08/10 — planner owns final template wording, level=Information, exactly one entry):
```csharp
protected override Task<List<ProcessItem>> ProcessAsync(
    string validatedData, SampleConfig? config, CancellationToken ct)
{
    var baseNumber = config?.Number ?? 0;            // D-03 null-config default — warning-clean guard (Pitfall 2)
    var label      = config?.Label;                  // D-10 verbatim — already "Step_*", do NOT prepend
    var sum        = baseNumber + Random.Shared.Next(0, 100);   // D-07 (0..99 inclusive)

    var data = JsonSerializer.Serialize(
        new { number = sum, label },                 // D-04 lowercase members → {"number":…,"label":…}; D-05 shared options
        ProcessorConfig.SerializerOptions);

    logger.LogInformation("step completed {StepLabel} sum {Sum}", label, sum);  // D-08/D-09/D-10 — one entry, ids ambient

    return Task.FromResult(new List<ProcessItem>
    {
        new(ProcessOutcome.Completed, data, Guid.NewGuid()),   // D-06
    });
}
```

**New usings required** (`ImplicitUsings=enable` does NOT include `System.Text.Json`; `BaseProcessor`1.cs:1` adds it explicitly):
```csharp
using System.Text.Json;                       // JsonSerializer
using BaseProcessor.Core.Configuration;       // ProcessorConfig.SerializerOptions
```
**Doc-comment updates:** `SampleProcessor.cs:6-17` describes echo/fail/fallback — rewrite to describe sum + single `Step_*` log. `FailedException` *type* stays (used by `ProcessorPipeline` catch); only the demo throw goes.

---

### `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` (test, request-response)

**Analog:** itself — keep the reflection-invoke harness, the `CapturingLogger` fake, the `Assert.Single` / `Assert.Equal` style.

**Reflection-invoke harness to KEEP verbatim** (`SampleProcessorFacts.cs:44-52` — no `InternalsVisibleTo`, so `protected ProcessAsync` is called by reflection):
```csharp
private static Task<List<ProcessItem>> InvokeProcessAsync(
    SampleProcessor processor, string validatedData, SampleConfig? config)
{
    var method = typeof(SampleProcessor).GetMethod(
        "ProcessAsync",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    return (Task<List<ProcessItem>>)method.Invoke(
        processor, new object?[] { validatedData, config, CancellationToken.None })!;
}
```

**Current `CapturingLogger`** (`SampleProcessorFacts.cs:26-42`) — captures ONLY the formatted string; `BeginScope` returns a `NullScope` that swallows scope state (Pitfall 4: a unit fact CANNOT assert the 6 ambient ids):
```csharp
private sealed class CapturingLogger : ILogger<SampleProcessor>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
    private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
}
```

**Required `CapturingLogger` extension** — also capture `state` as KVPs so facts assert `{StepLabel}`/`{Sum}` VALUES, not just the formatted string (MEL's `FormattedLogValues` implements `IReadOnlyList<KeyValuePair<string,object?>>` — assumption A1):
```csharp
public List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = new();

public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
    Func<TState, Exception?, string> formatter)
{
    var kvps = state as IReadOnlyList<KeyValuePair<string, object?>>
               ?? Array.Empty<KeyValuePair<string, object?>>();
    Entries.Add((logLevel, formatter(state, exception), kvps));
}
```

**Current fact assertion style to MIRROR** (`SampleProcessorFacts.cs:54-72`):
```csharp
var result = await InvokeProcessAsync(processor, "any-input", new SampleConfig("StepA1"));
var only = Assert.Single(result);
Assert.Equal(ProcessOutcome.Completed, only.Result);
Assert.Equal("StepA1", only.Data);
Assert.NotEqual(Guid.Empty, only.ExecutionId);
var logged = Assert.Single(logger.Entries);
Assert.Equal(LogLevel.Information, logged.Level);
Assert.Contains("sample payload received", logged.Message);
```

**Three-fact rewrite map** (keep file at 3 facts; per RESEARCH Validation Architecture + Open Question 2):

| Existing fact (file:line) | OLD assertion | NEW assertion (this phase) |
|---------------------------|---------------|----------------------------|
| `ProcessAsync_Receives_Typed_Config_Logs_It_And_Echoes_It` (:54) | `only.Data == "StepA1"`; log contains `"sample payload received"` | `SampleConfig(Number, "Step_A1")` → parse `only.Data` JSON: `number ∈ [Number, Number+99]`, `label == "Step_A1"`; `only.Result == Completed`; `only.ExecutionId != Guid.Empty`; `Assert.Single(logger.Entries)`; `State` contains `("StepLabel","Step_A1")` + `("Sum", sum)`. |
| `ProcessAsync_Null_Config_Falls_Back_To_Fixed_Token` (:74) | `only.Data == "processor-sample-ok"` | null config → still ONE item + ONE log; `Data` is JSON `{number:<0..99>, label:null}`; proves "seam always runs" without the dropped token (D-02/D-03). Rename the fact (no more "fixed token"). |
| `ProcessAsync_Fail_Config_Throws_FailedException` (:88) | `"fail"` → `TargetInvocationException(FailedException)` | **DELETE the demo-throw fact; REPLACE** with a PROC-01 deserialization fact: `JsonSerializer.Deserialize<SampleConfig>("{\"number\":5,\"label\":\"Step_A1\"}", ProcessorConfig.SerializerOptions)` → `Number==5`, `Label=="Step_A1"` (case-insensitive bind). |

**Range-assert note (Pitfall 3):** `Next(0,100)` is upper-EXCLUSIVE → assert `sum >= Number && sum <= Number + 99` (never an exact value — addend is non-deterministic).

---

## Shared Patterns

### Structured logging — params, never interpolation (T-18-04 / V7)
**Source analog:** `ProcessorStartupOrchestrator.cs:121-122` (two message-template params passed positionally):
```csharp
logger.LogInformation("Identity resolved for hash {Hash}: processor {ProcessorId}",
    hash, found.Message.Id);
```
**Security rationale source:** `InboundExecutionScopeConsumeFilter.cs:13-15` — "ids are placed only as scope VALUES under fixed keys, never interpolated into a message template."
**Apply to:** the single new `LogInformation` in `SampleProcessor.ProcessAsync`. Pass `config.Label` as the `{StepLabel}` param — NEVER `$"Step_{config.Label}"` or `$"...{label}..."` interpolation (breaks the `attributes.StepLabel` projection + enables log injection).

### Ambient id scope — do NOT re-add ids as log params (D-09)
**Source:** `InboundExecutionScopeConsumeFilter.cs:28` opens `logger.BeginScope(ExecutionLogScope.BuildState(ec))` (5 execution ids) around every `IExecutionCorrelated` consume; `correlationId` is owned by `InboundCorrelationConsumeFilter`. OTel `IncludeScopes=true` (BaseConsoleObservabilityExtensions) projects them to `attributes.*`.
**Apply to:** `SampleProcessor.ProcessAsync` — the new log carries ONLY `{StepLabel}` + `{Sum}`. The 6 ids attach automatically at runtime. In the unit harness they are absent (NullScope) — facts assert label+sum only, not ids (Pitfall 4).

### Shared SerializerOptions — symmetric round-trip (D-05)
**Source:** `ProcessorConfig.cs:18` (`SerializerOptions`, case-insensitive, no camelCase policy) consumed on the input side at `BaseProcessor`1.cs:24` (`Deserialize<TConfig>(payload, ProcessorConfig.SerializerOptions)`).
**Apply to:** the new `JsonSerializer.Serialize(new { number, label }, ProcessorConfig.SerializerOptions)` output line — use lowercase anonymous-member names to guarantee `{"number":…,"label":…}` keys (no naming policy is set; A2). Never hand-build the JSON string.

### Framework-shared RNG (D-07)
**Source:** `ProcessorPipeline.cs:83` — `Random.Shared.Next(min, max+1)`; upper bound exclusive.
**Apply to:** the addend — `Random.Shared.Next(0, 100)` (0..99). Never `new Random()`.

---

## No Analog Found

| Concern | Why no direct call-site analog | Planner guidance |
|---------|--------------------------------|------------------|
| `JsonSerializer.Serialize(obj, ProcessorConfig.SerializerOptions)` | The only two `JsonSerializer.Serialize` call sites in `src/` (`ProcessorLivenessWriter.cs:69`, `RedisProjectionWriter.cs:73,100`) use DEFAULT options for pinned wire shapes — none pass the shared `SerializerOptions`. | Mirror the **Deserialize** call at `BaseProcessor`1.cs:24` by symmetry (same options instance, opposite direction). This is the D-05 intent — input and output share one options object. |
| Capturing structured-param `state` KVPs in a fake `ILogger` | The existing `CapturingLogger` captures only the formatted string; no in-tree fake captures `state` as KVPs. (`ProcessorIdEnricherTests`/`LogExportTests` prove attributes via the real OTel `LoggerFactory`+capturing-processor — a heavier pattern, out of scope for a hermetic unit fact.) | Use the minimal `CapturingLogger.Log` extension shown above (cast `state` to `IReadOnlyList<KeyValuePair<string,object?>>`). Do NOT pull in the OTel harness — the bridge is proven elsewhere; this phase proves only "one `Step_<label>` log + sum." |

## Metadata

**Analog search scope:** `src/Processor.Sample/`, `src/BaseProcessor.Core/` (Processing, Configuration, Startup), `src/BaseConsole.Core/Messaging/`, `tests/BaseApi.Tests/Processor/`.
**Files read for excerpts:** `SampleConfig.cs`, `SampleProcessor.cs`, `SampleProcessorFacts.cs`, `BaseProcessor`1.cs`, `ProcessorConfig.cs`, `ProcessItem.cs`, `InboundExecutionScopeConsumeFilter.cs`, `ProcessorPipeline.cs` (RNG lines), `ProcessorStartupOrchestrator.cs` (log-template line).
**Pattern extraction date:** 2026-06-14

## PATTERN MAPPING COMPLETE

**Phase:** 64 - Processor Work & Structured Logging
**Files classified:** 4 (SampleConfig, SampleProcessor, SampleProcessorFacts, in-file CapturingLogger)
**Analogs found:** 4 / 4

### Coverage
- Files with exact analog: 4 (all are self-modifications of an existing working shape)
- Files with role-match analog: 0
- Files with no analog: 0 (two CROSS-CUTTING lines — Serialize-with-shared-options and state-KVP capture — have no direct call-site analog; both resolved via symmetry/minimal-extension above)

### Key Patterns Identified
- The author footprint is ~6 lines: every supporting concern (deserialize, id-scope, OTel export, L2 write, result build) is already-owned framework code; the rewrite is "delete demo paths + wire two BCL calls (Random.Shared, JsonSerializer.Serialize) + one LogInformation."
- Output serialization mirrors the input deserialize by SYMMETRY (`ProcessorConfig.SerializerOptions` both directions, D-05) — there is no existing Serialize-with-shared-options call site to copy; mirror `BaseProcessor`1.cs:24`.
- Structured logging uses positional message-template params (`{StepLabel}`,`{Sum}`), never interpolation (T-18-04/V7); ids come free from the ambient consume-filter scope (D-09) and are NOT assertable in the hermetic unit harness (NullScope) — facts prove label+sum + single-entry only.

### File Created
`C:\Users\UserL\source\repos\SK_P4\.planning\phases\64-processor-work-structured-logging\64-PATTERNS.md`

### Ready for Planning
Pattern mapping complete. Planner can reference each analog (with file:line) directly in PLAN.md action sections.
