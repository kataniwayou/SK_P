# Phase 44: Processor Pre/In/Post-Process Pipeline - Pattern Map

**Mapped:** 2026-06-08
**Files analyzed:** 13 (4 rewrite, 1 delete, 5 add-source, 1 migrate, 6 test files/extensions)
**Analogs found:** 13 / 13 (every new file has a strong in-repo analog — this phase is composition, not new infrastructure)

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (REWRITE) | consumer | event-driven (request→fan-out) | itself (straight-through baseline) | exact (self-rewrite) |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (ADD, recommended) | service (pipeline runner) | transform + routing | `EntryStepDispatchConsumer.cs` (current Consume body) | exact (extracted logic) |
| `src/BaseProcessor.Core/Processing/BaseProcessor.cs` (REWRITE) | model (abstract seam) | request-response (seam) | itself (current 2-arg seam) | exact (self-retype) |
| `src/BaseProcessor.Core/Processing/ProcessItem.cs` (ADD) | model (record) | data carrier | `ProcessResult.cs` (the record it replaces) | exact (same role) |
| `src/BaseProcessor.Core/Processing/ProcessOutcome.cs` (ADD) | model (enum) | data carrier | `RetryStrategy` enum in `RetryOptions.cs`; `StepOutcome.cs` | role-match |
| `src/BaseProcessor.Core/Processing/ProcessStatusException.cs` (ADD) | model (exception family) | control-flow signal | no exact analog (new mechanism); shape derived from CONTEXT D-04 | role-match |
| `src/BaseProcessor.Core/Resilience/RetryLoop.cs` (ADD) | utility (static helper) | retry/transform | `ProcessorJsonSchemaValidator.cs` (static helper + cctor + TryX shape) | role-match |
| `src/Processor.Sample/SampleProcessor.cs` (MIGRATE) | concrete author seam | transform | itself (current override) | exact (self-migrate) |
| `src/BaseProcessor.Core/Processing/ProcessResult.cs` (DELETE) | — | — | — | n/a |
| Keeper message construction (in pipeline) | data carrier | event emission | `KeeperReinject/Update/Inject/Delete/Cleanup.cs` (exist, GREEN) | exact (consume as-is) |
| `tests/BaseApi.Tests/Processor/PipelinePreFacts.cs` (ADD) | test | unit | `DispatchAckSemanticsFacts.cs` | exact |
| `tests/BaseApi.Tests/Processor/Pipeline{In,Post,EndDelete}Facts.cs` (ADD) | test | unit | `DispatchAckSemanticsFacts.cs` | exact |
| `tests/BaseApi.Tests/Processor/RetryLoopFacts.cs` (ADD) | test | unit | `DispatchAckSemanticsFacts.cs` (Fact structure) | role-match |
| `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` (EXTEND) | test fixture | fakes | itself (`FakeProcessor`, `CapturingSendProvider`, `PresentReadWriteFaultL2`) | exact (extend in place) |

---

## Pattern Assignments

### `src/BaseProcessor.Core/Processing/BaseProcessor.cs` (REWRITE — abstract seam)

**Analog:** itself (current shape at `BaseProcessor.cs:15-33`). Keep the **exact two-method structure** (abstract `ProcessAsync` + `internal ExecuteAsync` forwarder) — only the types change.

**Current shape to retype** (`BaseProcessor.cs:22-32`):
```csharp
protected abstract Task<IReadOnlyList<ProcessResult>> ProcessAsync(
    string inputData, string config, CancellationToken ct);

internal Task<IReadOnlyList<ProcessResult>> ExecuteAsync(string inputData, string config, CancellationToken ct)
    => ProcessAsync(inputData, config, ct);
```

**New shape** (D-01/D-02/D-03 — param names `validatedData, payload`, return `List<ProcessItem>`):
```csharp
// Author overrides ONLY this. Deserializes both raw-JSON strings; mints ExecutionId per item; may throw a status.
protected abstract Task<List<ProcessItem>> ProcessAsync(
    string validatedData, string payload, CancellationToken ct);

// Same-assembly pipeline calls this internal forwarder (the concrete in another assembly never sees it).
internal Task<List<ProcessItem>> ExecuteAsync(string validatedData, string payload, CancellationToken ct)
    => ProcessAsync(validatedData, payload, ct);
```
Preserve the file-level `<summary>` + the `internal`-forwarder rationale doc (the cross-assembly `protected` access note at `BaseProcessor.cs:25-30` still applies verbatim).

---

### `src/BaseProcessor.Core/Processing/ProcessItem.cs` (ADD — record)

**Analog:** `ProcessResult.cs` (the record it replaces; `ProcessResult.cs:1-10`).

**Existing record style to copy** (file-scoped namespace, `sealed record`, positional params, no `[JsonPropertyName]`):
```csharp
namespace BaseProcessor.Core.Processing;

public sealed record ProcessResult(string OutputData);
```
**New** (D-03 — author owns outcome + id, unlike `ProcessResult` where the framework owned outcome):
```csharp
namespace BaseProcessor.Core.Processing;

/// <summary>D-03: the unit returned by the author's ProcessAsync. The author constructs it directly,
/// declares the per-item outcome, and MINTS ExecutionId itself (new GUID per item).</summary>
public sealed record ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId);
```

### `src/BaseProcessor.Core/Processing/ProcessOutcome.cs` (ADD — enum)

**Analog:** the `RetryStrategy` enum at `src/Messaging.Contracts/Configuration/RetryOptions.cs:14-19` (plain enum, file-scoped namespace). Mirror that minimal style:
```csharp
namespace BaseProcessor.Core.Processing;

public enum ProcessOutcome { Completed, Failed }   // D-03
```

---

### `src/BaseProcessor.Core/Processing/ProcessStatusException.cs` (ADD — exception family)

**Analog:** no in-repo exception family; shape is dictated by CONTEXT D-04/D-05. Use C# primary-constructor style consistent with the rest of the codebase (e.g. the consumer's primary ctor at `EntryStepDispatchConsumer.cs:46-53`).

**Wire-message mapping (D-05, confirmed by file inspection):** `StepFailed.ErrorMessage` (`StepFailed.cs:14`) and `StepCancelled.CancellationMessage` (`StepCancelled.cs:14`) exist; **`StepProcessing` has NO message field** (`StepProcessing.cs:6-11`) → the processing message is logged only. All three ctors still take a message (uniform author API).

**Shape:**
```csharp
namespace BaseProcessor.Core.Processing;

public abstract class ProcessStatusException(string message) : Exception(message);
public sealed class ProcessingException(string message) : ProcessStatusException(message);
public sealed class FailedException(string message)     : ProcessStatusException(message);
public sealed class CancelledException(string message)  : ProcessStatusException(message);
```
The pipeline's `catch (ProcessStatusException e)` switches on the runtime type → builds the matching `Step*` record. (Whether `Status` is an exposed property or the catch type-switches is Claude's discretion per CONTEXT.)

---

### `src/BaseProcessor.Core/Resilience/RetryLoop.cs` (ADD — static helper)

**Analog:** `src/BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs` — the established **static-class helper** convention in this assembly: file-scoped namespace, static class, `TryValidate(... out errors) -> bool` result-not-throw shape.

**Convention to mirror** (`ProcessorJsonSchemaValidator.cs:12-30`):
```csharp
public static class ProcessorJsonSchemaValidator
{
    static ProcessorJsonSchemaValidator() { /* one-time setup */ }
    public static bool TryValidate(string? definition, string data, out IReadOnlyList<string> errors) { ... }
}
```
**New** (D-08 — N immediate attempts, surface exhaustion as a typed result; `limit` from `RetryOptions.Limit`):
```csharp
namespace BaseProcessor.Core.Resilience;   // (or Processing/ — placement is Claude's discretion per CONTEXT)

public static class RetryLoop
{
    public static async Task<RetryOutcome<T>> ExecuteAsync<T>(Func<Task<T>> op, int limit, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < Math.Max(1, limit); attempt++)
        {
            try { return RetryOutcome<T>.Ok(await op()); }
            catch (Exception ex) { last = ex; }   // immediate retry, no backoff (A3)
        }
        return RetryOutcome<T>.Exhausted(last!);
    }
}
public readonly record struct RetryOutcome<T>(bool Succeeded, T? Value, Exception? Error);
```
**A2 unify rule:** the Pre-read closure must `throw` an internal sentinel (e.g. `KeyAbsentException`) when `RedisValue.IsNullOrEmpty` so an absent/empty key and a Redis exception both reach the exhaustion branch (see Shared Pattern: L2 read).

---

### `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (ADD — pipeline runner) + `EntryStepDispatchConsumer.cs` (REWRITE)

**Analog:** the **entire current `EntryStepDispatchConsumer`** (`EntryStepDispatchConsumer.cs:46-241`). The recommended decomposition (RESEARCH Pattern 1) moves the Pre/In/Post logic into a `ProcessorPipeline` taking the same collaborators; the consumer keeps the metric increment + delegate.

**Collaborator set to copy verbatim** (the new `ProcessorPipeline` ctor takes the same six the consumer has today, `EntryStepDispatchConsumer.cs:46-53`):
```csharp
public sealed class EntryStepDispatchConsumer(
    IConnectionMultiplexer redis,
    IProcessorContext context,
    BaseProcessor processor,
    IOptions<ProcessorLivenessOptions> options,   // + IOptions<RetryOptions> for Retry:Limit (D-09)
    ISendEndpointProvider sendProvider,
    ProcessorMetrics metrics,
    ILogger<EntryStepDispatchConsumer> logger) : IConsumer<EntryStepDispatch>
```

**Entry-point metric — KEEP exactly** (`EntryStepDispatchConsumer.cs:66-67`):
```csharp
metrics.DispatchConsumed.Add(1,
    new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")));
```

**Pre — source-step skip via `SourceStep.IsSource`, NEVER `== Guid.Empty`** (`EntryStepDispatchConsumer.cs:82`). The current line returns `RedisValue.Null` on skip; the new pipeline sets `validatedData = string.Empty` and skips end-delete:
```csharp
var raw = SourceStep.IsSource(dispatch.EntryId)
    ? RedisValue.Null
    : await db.StringGetAsync(L2ProjectionKeys.ExecutionData(dispatch.EntryId));
```
**CHANGE (Pitfall: absent input):** old code at `EntryStepDispatchConsumer.cs:85-97` sends a business `StepFailed` on absent input. Phase 44 replaces this — absent/empty after retry exhaustion → `infra(READ)` → `KeeperReinject`, no end-delete. Input-schema *validation* failure stays business `StepFailed`.

**Input-schema validation — REUSE verbatim** (`EntryStepDispatchConsumer.cs:108`, `ProcessorJsonSchemaValidator.TryValidate`):
```csharp
if (!ProcessorJsonSchemaValidator.TryValidate(context.InputDefinition, validatedData, out var inErrors))
    // → business StepFailed (string.Join("; ", inErrors)); end-delete still runs (read succeeded)
```

**In — try/catch generalized from** `EntryStepDispatchConsumer.cs:119-134`. Current code catches `OperationCanceledException` + generic `Exception`; the new shape adds `catch (ProcessStatusException e)` mapped to the matching `Step*` record, then `catch (Exception)` ⇒ `StepFailed`. Any status aborts the batch (no Post), sends exactly one result, then end-delete runs.

**Post — per completed item** (rework of `EntryStepDispatchConsumer.cs:142-181`):
- output-validate via `ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, item.Data, out _)` (line 144) — but now a per-item business `failed`, NOT a whole-dispatch abort (old behavior at line 148-149 returned).
- mint entryId via `NewId.NextGuid()` (line 152) — **framework still mints entryId**; **author mints `item.ExecutionId`** (D-03 / Pitfall 4).
- write `L2[ExecutionData(entryId)]` via `db.StringSetAsync` **WITHOUT the `expiry:` arg** — drop the TTL at line 171 (no TTL per design §16/64).
- send order per item: `KeeperUpdate` → write → `KeeperCleanup` (success) / `KeeperInject` (write-exhausted) → `StepCompleted` (Pitfall 5).

**LOG-04 nested scope — KEEP** (`EntryStepDispatchConsumer.cs:160-164`) but use `item.ExecutionId` (author-minted) as the scoped value:
```csharp
using (logger.BeginScope(new Dictionary<string, object>
{
    [ExecutionLogScope.ExecutionId] = item.ExecutionId.ToString(),   // author-minted (D-03)
    [ExecutionLogScope.EntryId]     = entryId.ToString(),            // framework-minted
}))
```

**Send owner — generalize** `SendResult` (`EntryStepDispatchConsumer.cs:201-208`). Keep the single-owner send pattern + `CancellationToken.None` + post-send metric increment, but route through `RetryLoop` and resolve two endpoint targets: `queue:{OrchestratorQueues.Result}` (results) and `queue:{KeeperQueues.Recovery}` (Keeper messages, `KeeperQueues.cs:19`).

**Builders — copy the inherit-ids style** (`EntryStepDispatchConsumer.cs:216-240`): every `Step*`/`Keeper*` ctor takes `(d.WorkflowId, d.StepId, d.ProcessorId)` positionally, then object-initializer for `CorrelationId`/`ExecutionId`/extras. Example (`BuildCompleted`, lines 216-222):
```csharp
new StepCompleted(d.WorkflowId, d.StepId, d.ProcessorId)
{
    CorrelationId = d.CorrelationId,
    ExecutionId = executionId,
    EntryId = newEntryId,
};
```

**Keeper builders** (records exist & GREEN — `KeeperUpdate/Reinject/Inject/Delete/Cleanup.cs`). Use the same positional-ctor + init style. Per-item exec = `item.ExecutionId`; inbound exec = `d.ExecutionId` (A1 — confirm at plan):
```csharp
new KeeperUpdate(d.WorkflowId, d.StepId, d.ProcessorId)
    { CorrelationId = d.CorrelationId, ExecutionId = item.ExecutionId, ValidatedData = item.Data };  // UPDATE-only extra
new KeeperReinject(d.WorkflowId, d.StepId, d.ProcessorId)
    { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId };           // REINJECT-only extra
new KeeperInject(d.WorkflowId, d.StepId, d.ProcessorId)
    { CorrelationId = d.CorrelationId, ExecutionId = item.ExecutionId };
new KeeperDelete(d.WorkflowId, d.StepId, d.ProcessorId)
    { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId };           // DELETE-only extra
new KeeperCleanup(d.WorkflowId, d.StepId, d.ProcessorId)
    { CorrelationId = d.CorrelationId, ExecutionId = item.ExecutionId };
```

---

### `src/Processor.Sample/SampleProcessor.cs` (MIGRATE — worked example)

**Analog:** itself (current override at `SampleProcessor.cs:24-40`). Keep the **type alias** (`using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;` — avoids CS0118, `SampleProcessor.cs:4`), the DI registration (`Program.cs:17` `AddSingleton<BaseProcessorBase, SampleProcessor>()`), and the `ILogger<SampleProcessor>` primary-ctor injection.

**Current override to migrate** (`SampleProcessor.cs:27-39`):
```csharp
protected override Task<IReadOnlyList<ProcessResult>> ProcessAsync(string inputData, string config, CancellationToken ct)
{
    var payload = string.IsNullOrWhiteSpace(config) ? null : JsonSerializer.Deserialize<string>(config);
    logger.LogInformation("sample payload received: {Payload}", payload);
    return Task.FromResult<IReadOnlyList<ProcessResult>>(new[] { new ProcessResult(payload ?? "processor-sample-ok") });
}
```
**New** (D-07 — new seam; deserialize both, emit ≥1 completed `ProcessItem` with author-minted `ExecutionId`, demonstrate one status-exception path):
```csharp
protected override Task<List<ProcessItem>> ProcessAsync(string validatedData, string payload, CancellationToken ct)
{
    var cfg = string.IsNullOrWhiteSpace(payload) ? null : JsonSerializer.Deserialize<string>(payload);
    logger.LogInformation("sample payload received: {Payload}", cfg);
    // demonstrate the status-exception path, e.g.:  if (cfg == "fail") throw new FailedException("sample reason");
    return Task.FromResult(new List<ProcessItem>
    {
        new(ProcessOutcome.Completed, cfg ?? "processor-sample-ok", Guid.NewGuid()),   // author mints ExecutionId
    });
}
```

---

## Shared Patterns

### Schema validation (Pre input + Post output)
**Source:** `src/BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs:30` (`TryValidate(definition, data, out errors) -> bool`)
**Apply to:** `ProcessorPipeline` Pre (`context.InputDefinition`) and Post (`context.OutputDefinition`). SSRF-locked, dialect-pinned, error-flattening already correct — DO NOT hand-roll a validator.
```csharp
if (!ProcessorJsonSchemaValidator.TryValidate(definition, data, out var errors)) { /* business Failed, errors flattened */ }
```

### Source-step detection
**Source:** `src/Messaging.Contracts/SourceStep.cs:8` (`SourceStep.IsSource(entryId)`)
**Apply to:** Pre read-skip AND end-delete-skip. Never inline `entryId == Guid.Empty`.

### L2 key building
**Source:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:42` (`ExecutionData(entryId) -> "skp:data:{entryId:D}"`)
**Apply to:** Pre read, Post write, end-delete. Single source of truth; never interpolate the key. The processor NEVER builds/writes `CompositeBackup` (line 46) — Model B, the keeper owns it (RESEARCH A7).

### L2 read unifying absent/empty with Redis fault (A2)
**Source:** current read at `EntryStepDispatchConsumer.cs:82-105` + `RedisValue.IsNullOrEmpty` (line 85), reworked into a RetryLoop closure:
```csharp
var read = await RetryLoop.ExecuteAsync(async () =>
{
    var raw = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(dispatch.EntryId));
    if (raw.IsNullOrEmpty) throw new KeyAbsentException();   // unify absent/empty with Redis fault (A2)
    return raw.ToString();
}, retryLimit, ct);
if (!read.Succeeded) { /* infra(READ): Send KeeperReinject; END — no end-delete */ }
```

### GUID minting (the entryId data key)
**Source:** `EntryStepDispatchConsumer.cs:152` (`NewId.NextGuid()` — sequential/COMB GUID). Framework mints entryId; author mints `item.ExecutionId`.

### Retry budget config
**Source:** `RetryOptions.Limit` (`RetryOptions.cs:10`, default 3), bound from `"Retry"` at `BaseProcessorServiceCollectionExtensions.cs:89`; consumed by `UseMessageRetry(r => r.Immediate(retryLimit))` at `ProcessorStartupOrchestrator.cs:174` (reads `retryOptions.Value.Limit` line 171).
**Apply to:** inject `IOptions<RetryOptions>` into `ProcessorPipeline`; pass `.Value.Limit` to every `RetryLoop.ExecuteAsync`. D-09: in-code RetryLoop owns per-op retries; bus `UseMessageRetry` is the outer dead-letter latch (do NOT double-retry — Pitfall 1).

### Single send owner + post-send metric
**Source:** `EntryStepDispatchConsumer.cs:201-208` (`SendResult`). Keep `endpoint.Send((object)result, CancellationToken.None)` + the `metrics.ResultSent.Add(...)` AFTER the confirmed send. Generalize to route both `OrchestratorQueues.Result` and `KeeperQueues.Recovery` targets through `RetryLoop`; send-exhaustion propagates (D-10 → `_error`).

---

## Test Patterns

### Hermetic fact structure
**Source:** `tests/BaseApi.Tests/Processor/DispatchAckSemanticsFacts.cs:21-71` — the established per-outcome unit fact: `[Fact]`, `TestContext.Current.CancellationToken`, build collaborators from `DispatchTestKit` + `FakeProcessorContext`, `DispatchTestKit.Build(...)`, then assert on `send.Sent` (e.g. `Assert.Single` + `Assert.IsType<StepFailed>` + `Assert.False(processor.Invoked)`), OR `Assert.ThrowsAsync<RedisConnectionException>` for the infra-propagate case.
**Apply to:** all 5 new `Pipeline*Facts.cs` + `RetryLoopFacts.cs`. Drive the consumer via `OrchestratorTestStubs.Context(DispatchTestKit.Dispatch(...), ct)`.

### DispatchTestKit extensions (the test fixture to grow in place)
**Source:** `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` (`internal static class`). Extend, do not replace:
- **`FakeProcessor`** (`DispatchTestKit.cs:33-63`) — currently has `(IReadOnlyList<ProcessResult>)` and `(Exception)` ctors recording `LastInputData`/`LastConfig`. Retype to `List<ProcessItem>`; add a ctor (or reuse the `Exception` ctor) for throwing a `ProcessStatusException`.
- **`Results(...)` helper** (`DispatchTestKit.cs:66-67`) → a `ProcessItem(...)`-building helper.
- **`PresentReadWriteFaultL2`** (`DispatchTestKit.cs:74-110`) — REUSE for the Post write-fault case (already covers `StringSetAsync` throwing across all SE.Redis overloads). Add an absent-key (`RedisValue.Null` for the Pre A2 path) and a `KeyDeleteAsync`-faulting fake for end-delete.
- **`CapturingSendProvider`** (`DispatchTestKit.cs:161-176`) — captures `IStepResult` sends. Extend to ALSO capture `IKeeperRecoverable` sends (the `endpoint.Send(Arg.Any<object>(), ...)` capture at line 170 boxes any record; add a `SentKeeper` list and cast on `IKeeperRecoverable`), or add a `CapturingKeeperSendProvider`.
- **`Build(...)`** (`DispatchTestKit.cs:133-140`) — add the `IOptions<RetryOptions>` arg the rewritten consumer/pipeline needs.

### Tests to retire/rewrite
**Source:** RESEARCH § Wave 0 Gaps. `DispatchAckSemanticsFacts.BusinessFailure_DoesNotThrow` (`DispatchAckSemanticsFacts.cs:54-71`) asserts absent input → `StepFailed` — now absent input → `KeeperReinject` (rewrite). `DispatchInputFacts` input-absence semantics likewise changed.

---

## No Analog Found

None. Every new file has a strong in-repo analog. The only genuinely new mechanisms (`RetryLoop` ~15-line loop, `ProcessStatusException` family of 3 trivial subclasses) follow established conventions (static helper / primary-ctor classes) even though no exact prior instance exists.

## Metadata

**Analog search scope:** `src/BaseProcessor.Core/{Processing,Validation,Identity,Startup,DependencyInjection}/`, `src/Messaging.Contracts/`, `src/Processor.Sample/`, `tests/BaseApi.Tests/Processor/`
**Files scanned:** 23 (10 read in full, 13 contract/record files)
**Pattern extraction date:** 2026-06-08
**Open id-set confirmations deferred to plan:** REINJECT/DELETE ExecutionId provenance (A1/Open Q1), Keeper send target = `KeeperQueues.Recovery` (A2), per-item business-failed routing (A3).
