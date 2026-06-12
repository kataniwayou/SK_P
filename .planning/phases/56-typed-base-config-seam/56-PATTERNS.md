# Phase 56: Typed Base-Config Seam - Pattern Map

**Mapped:** 2026-06-12
**Files analyzed:** 8 (3 new, 5 modified)
**Analogs found:** 8 / 8 (all in-repo — this phase is a seam reshape, not greenfield)

## File Classification

| New/Modified File | New? | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|------|-----------|----------------|---------------|
| `src/BaseProcessor.Core/Processing/BaseProcessor.cs` | modify | framework base / seam | transform | itself (current shape) | exact (self-reshape) |
| `src/BaseProcessor.Core/Processing/BaseProcessor`1.cs` (generic) | **new** | framework base / generic interposer | transform + deserialize | `BaseProcessor.cs` current `ExecuteAsync` forwarder | role-match |
| `src/BaseProcessor.Core/Configuration/ProcessorConfig.cs` (marker; name = discretion) | **new** | config marker type + shared options holder | n/a (type anchor) | `ProcessItem.cs` (record), `SlotArrayOptions.cs` (Configuration ns style) | role-match |
| `src/Processor.Sample/SampleConfig.cs` | **new** | author config model | n/a | `ProcessItem.cs` (positional record) | role-match |
| `src/Processor.Sample/SampleProcessor.cs` | modify | author processor | transform | itself (current override) | exact (self-migrate) |
| `src/Processor.Sample/Program.cs` | **unchanged** (verify) | composition root | DI | — | n/a |
| `tests/.../Processor/SampleProcessorFacts.cs` | modify | unit test | transform | itself | exact (self-migrate) |
| `tests/.../Processor/BaseProcessorSeamFacts.cs` | modify | unit test | DI + transform | itself | exact (self-migrate) |
| `tests/.../Processor/DispatchTestKit.cs` (`FakeProcessor`) | modify | test double | transform | itself | exact (self-migrate) |
| `tests/.../Processor/PipelineInFacts.cs` (+ new deser-failure fact) | modify | integration test | request-response (pipeline) | `StatusException_Failed_AbortsBatch` (cs:32-45) | exact (sibling fact) |

All paths verified present. The hermetic suite lives at `tests/BaseApi.Tests/Processor/`.

---

## Pattern Assignments

### `src/BaseProcessor.Core/Processing/BaseProcessor.cs` (MODIFY — non-generic base seam reshape)

**Analog:** itself, current shape (full file is 40 lines; read in context).

**Current shape to change** (BaseProcessor.cs:29-39):
```csharp
protected abstract Task<List<ProcessItem>> ProcessAsync(
    string validatedData, string payload, CancellationToken ct);     // :29 — REMOVE (clean break, D-02)

internal Task<List<ProcessItem>> ExecuteAsync(string validatedData, string payload, CancellationToken ct)
    => ProcessAsync(validatedData, payload, ct);                       // :38 — concrete forwarder; make abstract
```

**Target shape** (D-01 / Research Pattern 1 / Pitfall 1 — the ONE structural decision):
```csharp
public abstract class BaseProcessor
{
    // Was concrete forwarder → now internal abstract; BaseProcessor<TConfig> supplies the body.
    // The pipeline still calls THIS non-generic method (ProcessorPipeline.cs:226) — unchanged.
    internal abstract Task<List<ProcessItem>> ExecuteAsync(
        string validatedData, string payload, CancellationToken ct);
    // The protected abstract ProcessAsync(string,string,ct) is GONE — no shim (SPEC constraint).
}
```

**Why `internal abstract` works cross-assembly** (preserve the existing XML-doc rationale at :32-37): `Processor.Sample` derives from `BaseProcessor<SampleConfig>`, which provides the `internal` override; the concrete author never sees the `internal` member. The DI binding `AddSingleton<BaseProcessor, SampleProcessor>` still holds because `SampleProcessor : BaseProcessor<SampleConfig> : BaseProcessor`.

**Doc-comment update required:** the class summary (:3-18) and the ProcessAsync param doc (:21-30) describe a string-payload author seam that "deserializes BOTH raw-JSON strings." That narrative moves to the generic class. Update both blocks to describe the typed seam; 0-warning build with `TreatWarningsAsErrors` means stale `<see cref="ProcessAsync"/>` references will FAIL the build — fix every cref.

**Compile warning signs:** CS0506 (override non-virtual) or CS0534 (missing abstract `ProcessAsync(string,string)`) mean the reshape is half-done.

---

### `src/BaseProcessor.Core/Processing/BaseProcessor`1.cs` (NEW — generic deserialize-then-dispatch layer)

**Analog:** the current concrete `ExecuteAsync` forwarder (BaseProcessor.cs:38-39) — same forwarder role, now with a deserialize step inserted.

**Pattern to write** (D-02/D-03/D-04/D-05, Research Pattern 1):
```csharp
using System.Text.Json;

namespace BaseProcessor.Core.Processing;

public abstract class BaseProcessor<TConfig> : BaseProcessor
    where TConfig : ProcessorConfig                     // marker constraint → reference type, null representable (Pattern 4)
{
    internal sealed override Task<List<ProcessItem>> ExecuteAsync(
        string validatedData, string payload, CancellationToken ct)
    {
        TConfig? config = string.IsNullOrWhiteSpace(payload)             // D-04 guard BEFORE deserialize
            ? null
            : JsonSerializer.Deserialize<TConfig>(payload, ProcessorConfig.SerializerOptions); // D-05; JsonException → :241 StepFailed (D-03)
        return ProcessAsync(validatedData, config, ct);
    }

    protected abstract Task<List<ProcessItem>> ProcessAsync(            // NEW typed author seam (D-02)
        string validatedData, TConfig? config, CancellationToken ct);
}
```

**Anti-patterns (Research):** do NOT `try/catch` the `JsonException` here (D-03 requires propagation to `ProcessorPipeline.cs:241`); do NOT `new JsonSerializerOptions()` per call (D-05/D-06 → one cached static); do NOT return a default config on failure (violates "no silent default", SPEC Req 4).

**Namespace note:** `ProcessorConfig` (marker) lives in `BaseProcessor.Core.Configuration` (see below) — add `using BaseProcessor.Core.Configuration;` if the marker is placed there, or co-locate. The constraint references it either way.

---

### `src/BaseProcessor.Core/Configuration/ProcessorConfig.cs` (NEW — empty marker base + canonical options holder)

**Role:** config marker (SPEC Req 1, zero framework fields) AND the single-source `JsonSerializerOptions` (D-06, reused by Phase 57 Gate A).

**Analog 1 — record style:** `ProcessItem.cs:7` is the repo's record idiom:
```csharp
public sealed record ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId);
```

**Analog 2 — `Configuration` namespace file style:** `SlotArrayOptions.cs` (sealed type in `namespace BaseProcessor.Core.Configuration;`, dense XML-doc tying each member to a decision ID). Match this doc density.

**Pattern to write** (Research Pattern 2, D-05/D-06):
```csharp
using System.Text.Json;

namespace BaseProcessor.Core.Configuration;

/// <summary>SPEC Req 1: empty marker base config — zero framework-mandated fields; pure type anchor
/// the author config inherits and Phase 57 Gate A compares against the config-schema definition.</summary>
public abstract record ProcessorConfig
{
    /// <summary>D-06: the SINGLE canonical config-deserialization contract. Phase 57 Gate A reuses THIS
    /// instance so it gates against exactly the deserialize behavior the framework runs (D-05:
    /// case-insensitive; unknown JSON properties IGNORED — NOT JsonUnmappedMemberHandling.Disallow).</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,            // D-05
        // default unknown-member handling = ignore (do NOT set Disallow) — D-05
    };
}
```

**Discretion (CONTEXT):** type name, `record` vs `class`, namespace are Claude's call — Research Open Question 1 recommends `abstract record` for symmetry with the author's `record SampleConfig`, and `BaseProcessor.Core.Configuration` as home (a `using BaseProcessor.Core.Configuration` already exists in DispatchTestKit.cs:2). Constraint: single-source + reusable by Phase 57.

---

### `src/Processor.Sample/SampleConfig.cs` (NEW — author config model)

**Analog:** `ProcessItem.cs:7` positional record idiom.

**Pattern** (D-08, Research Pattern 3):
```csharp
using BaseProcessor.Core.Configuration;   // for ProcessorConfig (wherever the marker lands)

namespace Processor.Sample;

public sealed record SampleConfig(string? Value) : ProcessorConfig;
```

`PropertyNameCaseInsensitive = true` maps JSON `{"value":...}` to the positional `Value` param (Research Code Example 1).

---

### `src/Processor.Sample/SampleProcessor.cs` (MODIFY — clean-break migration)

**Analog:** itself, current override (SampleProcessor.cs:25-48).

**Current** (to remove — SampleProcessor.cs:28-34):
```csharp
public sealed class SampleProcessor(ILogger<SampleProcessor> logger) : BaseProcessorBase   // :25
{
    protected override Task<List<ProcessItem>> ProcessAsync(
        string validatedData, string payload, CancellationToken ct)
    {
        var cfg = string.IsNullOrWhiteSpace(payload)
            ? null
            : JsonSerializer.Deserialize<string>(payload);     // :34 — REMOVE (SPEC Req 5: no JsonSerializer)
        ...
```

**Target** (D-08/D-09, Research Pattern 3 / Code Example 4):
```csharp
// Drop: `using System.Text.Json;` and the BaseProcessorBase alias (no longer needed once base is generic).
public sealed class SampleProcessor(ILogger<SampleProcessor> logger) : BaseProcessor<SampleConfig>
{
    protected override Task<List<ProcessItem>> ProcessAsync(
        string validatedData, SampleConfig? config, CancellationToken ct)
    {
        var value = config?.Value;                                   // D-04: null config → null value
        logger.LogInformation("sample payload received: {Payload}", value);   // preserve log token (asserted in facts)

        if (value == "fail")                                         // D-09: status-exception demo preserved
            throw new FailedException("sample reason");

        return Task.FromResult(new List<ProcessItem>
        {
            new(ProcessOutcome.Completed, value ?? "processor-sample-ok", Guid.NewGuid()),  // D-09 fallback token + D-03 mint
        });
    }
}
```

**Preserve verbatim** (tests assert these literals): log message `"sample payload received: {Payload}"`; fallback token `"processor-sample-ok"`; `FailedException("sample reason")`; `ProcessOutcome.Completed`; author-minted `Guid.NewGuid()`.

**Alias caution (Research Pattern 3 note):** `SampleProcessor.cs` can drop the `BaseProcessorBase` alias, but `Program.cs:6,17` still references the non-generic base by name via the alias for `AddSingleton<BaseProcessorBase, SampleProcessor>` — keep the alias in `Program.cs`. `SampleProcessor : BaseProcessor<SampleConfig>` IS-A `BaseProcessor`, so the registration is unchanged.

---

### `src/Processor.Sample/Program.cs` (UNCHANGED — verify only)

DI registration `builder.Services.AddSingleton<BaseProcessorBase, SampleProcessor>();` (Program.cs:17) stays literally identical (D-01). The alias `using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;` (:6) remains. Plan should assert "no change" rather than edit.

---

### `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` (MODIFY — typed seam + object payloads)

**Analog:** itself (full file read in context). Three facts + a reflection helper change.

**Reflection helper change** (SampleProcessorFacts.cs:42-50) — the seam is now `(string, SampleConfig?, ct)`:
```csharp
// Pass a SampleConfig (or null) instead of a payload string.
private static Task<List<ProcessItem>> InvokeProcessAsync(
    SampleProcessor processor, string validatedData, SampleConfig? config)
{
    var method = typeof(SampleProcessor).GetMethod(
        "ProcessAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    return (Task<List<ProcessItem>>)method.Invoke(
        processor, new object?[] { validatedData, config, CancellationToken.None })!;
}
```
(Reflection still required — no `InternalsVisibleTo` to the test assembly. VERIFIED: grep found IVT only in `Identity/ProcessorContext.cs` referencing the pattern, none granting the test project.)

**Per-fact migration (Pitfall 4/5):**
- `..._Deserializes_Payload_Logs_It_And_Echoes_It` (cs:52-70): pass `new SampleConfig("StepA1")`; keep asserting `only.Data == "StepA1"` and log contains `"StepA1"`.
- `..._Blank_Config_Falls_Back_To_Fixed_Token` (cs:72-83): pass `(SampleConfig?)null`; keep `only.Data == "processor-sample-ok"` + single log entry.
- `..._Fail_Payload_Throws_FailedException` (cs:85-97): pass `new SampleConfig("fail")`; keep `Throws<TargetInvocationException>` → inner `FailedException` (sync throw before Task return).

**Add (Research Test Map, Req 4b/empty + a generic-deser unit, Req 3):** a fact driving the framework deserialize end-to-end is better placed at pipeline level (next file) since reflecting the `internal ExecuteAsync` is blocked by no-IVT. The unit facts here stay at the typed `ProcessAsync` level passing `SampleConfig` directly.

---

### `tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs` (MODIFY — TestProcessor to generic base; Pitfall 2)

**Analog:** itself (cs:14-52).

**Change** (cs:20-30): `TestProcessor : BaseProcessorBase` overriding `ProcessAsync(string,string,ct)` no longer compiles. Migrate to the generic base with a local test config:
```csharp
private sealed record TestConfig(string? V) : ProcessorConfig;   // or reuse SampleConfig

private sealed class TestProcessor : BaseProcessor<TestConfig>
{
    public static readonly ProcessItem Result = new(ProcessOutcome.Completed, "output", Guid.NewGuid());

    protected override Task<List<ProcessItem>> ProcessAsync(
        string validatedData, TestConfig? config, CancellationToken ct)
        => Task.FromResult(new List<ProcessItem> { Result });

    public Task<List<ProcessItem>> InvokeAsync(string validatedData, TestConfig? config, CancellationToken ct)
        => ProcessAsync(validatedData, config, ct);
}
```
**Preserve** the DI-resolution assertion (cs:33-51): resolve as `BaseProcessorBase`, `Assert.IsType<TestProcessor>` — still holds because `TestProcessor : BaseProcessor<TestConfig> : BaseProcessor`. The `InvokeAsync` call at cs:47 passes a `TestConfig` (or null) instead of `"config"`.

---

### `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` (MODIFY — FakeProcessor; Pitfall 3)

**Analog:** itself (cs:35-65), the pipeline test double.

**Constraint:** `FakeProcessor` must stay **deserialization-free** so the existing Pre/In/Post/recovery/end-delete facts (which inject it and pass `payload="{\"cfg\":1}"` via `Dispatch` default, cs:558) keep asserting pipeline behavior, not deserialization. After the reshape, the non-generic base no longer has `protected abstract ProcessAsync(string,string)`; it has `internal abstract ExecuteAsync(string,string,ct)`.

**Two viable strategies (planner picks; no IVT → option B is the safe default):**
- **A (needs IVT — NOT available):** override `internal ExecuteAsync(string,string,ct)` directly. Blocked: BaseProcessor.Core grants no `InternalsVisibleTo` to the test assembly (VERIFIED). Skip.
- **B (recommended):** make `FakeProcessor : BaseProcessor<DummyConfig>` with a trivial `record DummyConfig : ProcessorConfig;` and override the typed `ProcessAsync(string, DummyConfig?, ct)`. The framework `BaseProcessor<DummyConfig>.ExecuteAsync` WILL run `JsonSerializer.Deserialize<DummyConfig>("{\"cfg\":1}")` — but `DummyConfig` with no fields + ignore-unknown (D-05) deserializes harmlessly (`cfg` ignored), so the double stays effectively deser-inert and `LastConfig` semantics change from "raw payload" to "the deserialized DummyConfig". **Audit the assertions:** any fact asserting `FakeProcessor.LastConfig == "{\"cfg\":1}"` (raw string) must be re-expressed (the double now receives a `DummyConfig`, not the raw string). If many facts assert on `LastConfig`, prefer keeping `LastConfig` as the raw payload by recording it via an override of a captured value — but the cleanest is to record the validatedData (`LastInputData`) which is unaffected, and drop raw-payload assertions.

**Current double to migrate** (cs:35-65): the two ctors (`List<ProcessItem>` and `Exception toThrow`) and the `Invoked`/`LastInputData`/`LastConfig` capture stay; only the base type and override signature change. The throw ctor still serves both the `ProcessStatusException` family and the unexpected-exception case.

**Dispatch payload default (cs:558):** `Dispatch(..., string payload = "{\"cfg\":1}")` is already an object shape and deserializes harmlessly into a field-less `DummyConfig` — leave it. Only confirmed `SampleProcessor`-bound bare-string payloads need the `{"value":...}` shift (Pitfall 4: only `SampleProcessorFacts` had bare strings, now handled by passing `SampleConfig` directly).

---

### `tests/BaseApi.Tests/Processor/PipelineInFacts.cs` (MODIFY — add deser-failure→StepFailed fact; Req 4a)

**Analog:** the sibling fact `StatusException_Failed_AbortsBatch` (PipelineInFacts.cs:32-45) — same Build/RunAsync/assert-single-StepFailed shape.

**Existing pattern to copy** (cs:32-45):
```csharp
[Fact]
public async Task StatusException_Failed_AbortsBatch()
{
    var ct = TestContext.Current.CancellationToken;
    var processor = new DispatchTestKit.FakeProcessor(new FailedException("x"));
    var (pipeline, send, entryId) = Build(processor);

    await pipeline.RunAsync(DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), Guid.NewGuid(), ct);

    var sent = Assert.Single(send.Sent);
    var failed = Assert.IsType<StepFailed>(sent);
    Assert.Equal("x", failed.ErrorMessage);
    Assert.Empty(send.SentKeeper);                     // no Keeper item sends — batch aborted before Post
}
```

**New fact to add** (Req 4a — genuine framework-deserialize failure → `:241` catch-all → exactly one `StepFailed`). Crucially this needs a **real `BaseProcessor<TConfig>` subclass** (NOT `FakeProcessor`, which bypasses deser) fed a malformed payload:
```csharp
private sealed record DeserConfig(string? Value) : ProcessorConfig;

private sealed class RealDeserProcessor : BaseProcessor<DeserConfig>
{
    protected override Task<List<ProcessItem>> ProcessAsync(
        string validatedData, DeserConfig? config, CancellationToken ct)
        => Task.FromResult(new List<ProcessItem>());   // never reached on malformed payload
}

[Fact]
public async Task MalformedPayload_DeserFailure_Emits_Single_StepFailed()
{
    var ct = TestContext.Current.CancellationToken;
    var (pipeline, send, entryId) = Build(new RealDeserProcessor());

    // "not json" → JsonException inside BaseProcessor<TConfig>.ExecuteAsync → ProcessorPipeline.cs:241 → StepFailed
    await pipeline.RunAsync(DispatchTestKit.Dispatch(entryId, Guid.NewGuid(), "not json"), Guid.NewGuid(), ct);

    var sent = Assert.Single(send.Sent);               // exactly ONE result (no default, no crash)
    Assert.IsType<StepFailed>(sent);
    Assert.Empty(send.SentKeeper);                     // business StepFailed — NOT routed to Keeper (D-03)
}
```
The `Build` helper (cs:19-30) takes any `BaseProcessorBase`, so `RealDeserProcessor` plugs in unchanged. `Dispatch` overloads on `payload` (cs:558), so `"not json"` is passed directly.

---

## Shared Patterns

### Single-source deserialization contract (D-05/D-06)
**Source:** NEW `ProcessorConfig.SerializerOptions` (`public static readonly JsonSerializerOptions`).
**Apply to:** `BaseProcessor<TConfig>.ExecuteAsync` (the only caller in Phase 56); Phase 57 Gate A (forward consumer).
```csharp
public static readonly JsonSerializerOptions SerializerOptions = new()
{ PropertyNameCaseInsensitive = true /* + default ignore-unknown */ };
```
One cached instance — never `new` per call (Research anti-pattern).

### Business-vs-infra split (deser-failure routing — reused, NOT rebuilt) (D-03)
**Source:** `ProcessorPipeline.cs:226-246` — the `try { ExecuteAsync } catch (ProcessStatusException) {…} catch (Exception ex) { BuildFailed → StepFailed }`.
**Apply to:** the deserialize step in `BaseProcessor<TConfig>` — it must throw INTO this try, never catch internally.
```csharp
catch (Exception ex)                                        // ProcessorPipeline.cs:241 — JsonException lands here
{
    await SendResult(BuildFailed(d, ex.Message), limit, ct);   // exactly one StepFailed
    await DeleteTerminalAsync(d, messageId, db, limit, ct);
    return;
}
```
Do NOT add a Keeper/infra route for malformed config (SPEC Constraint).

### Author status-exception family (preserved) (D-09)
**Source:** `ProcessStatusException.cs` — `FailedException`/`CancelledException`/`ProcessingException`.
**Apply to:** `SampleProcessor` (`Value == "fail" → throw new FailedException("sample reason")`); `PipelineInFacts` (the status-exception facts stay valid through `FakeProcessor`).

### Positional sealed record idiom
**Source:** `ProcessItem.cs:7` — `public sealed record ProcessItem(...)`.
**Apply to:** `SampleConfig`, `DummyConfig`/`DeserConfig` test configs, and the `ProcessorConfig` marker (as `abstract record`).

### Base-type alias for CS0118 avoidance
**Source:** `Program.cs:6` / `SampleProcessor.cs:4` / test files — `using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;`.
**Apply to:** keep in `Program.cs` (still names the non-generic base in DI); `SampleProcessor.cs` can drop it (now derives from the generic `BaseProcessor<SampleConfig>`, which is the type name, not the namespace).

---

## No Analog Found

None. Every file is either an in-repo self-reshape or follows an existing record/options/test idiom. This phase is a relocation of an existing responsibility (deserialize: author → framework) plus a generic indirection — there is no greenfield component requiring RESEARCH.md fallback patterns.

---

## Metadata

**Analog search scope:** `src/BaseProcessor.Core/Processing/`, `src/BaseProcessor.Core/Configuration/`, `src/Processor.Sample/`, `tests/BaseApi.Tests/Processor/`.
**Files scanned (read):** `BaseProcessor.cs`, `BaseProcessor`1` (n/a — new), `ProcessItem.cs`, `ProcessStatusException.cs`, `ProcessorPipeline.cs:200-268`, `SampleProcessor.cs`, `Program.cs`, `SlotArrayOptions.cs`, `SampleProcessorFacts.cs`, `BaseProcessorSeamFacts.cs`, `DispatchTestKit.cs:1-70`, `PipelineInFacts.cs:1-75`.
**Verified facts:** no `InternalsVisibleTo` to the test assembly (grep); `Configuration` namespace exists in BaseProcessor.Core; `Dispatch` default payload `{"cfg":1}` (object shape, cs:558); pipeline catch-all at `ProcessorPipeline.cs:241`.
**Pattern extraction date:** 2026-06-12
