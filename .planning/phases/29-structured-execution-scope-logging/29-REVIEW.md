---
phase: 29-structured-execution-scope-logging
reviewed: 2026-06-02T00:00:00Z
depth: standard
files_reviewed: 16
files_reviewed_list:
  - scripts/phase-29-close.ps1
  - src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
  - src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs
  - src/BaseProcessor.Core/BaseProcessor.Core.csproj
  - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
  - src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs
  - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
  - src/Messaging.Contracts/ExecutionLogScope.cs
  - src/Orchestrator/Scheduling/WorkflowFireJob.cs
  - tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs
  - tests/BaseApi.Tests/Contracts/ExecutionLogScopeKeyTests.cs
  - tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs
  - tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs
  - tests/BaseApi.Tests/Orchestrator/WorkflowFireJobScopeTests.cs
  - tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs
  - tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs
findings:
  critical: 0
  warning: 3
  info: 3
  total: 6
status: issues_found
---

# Phase 29: Code Review Report

**Reviewed:** 2026-06-02T00:00:00Z
**Depth:** standard
**Files Reviewed:** 16
**Status:** issues_found

## Summary

This change set introduces structured execution-scope logging across three surfaces: a new `ExecutionLogScope` constants class (leaf contract), a bus-wide `InboundExecutionScopeConsumeFilter` that opens MEL scopes for `IExecutionCorrelated` messages, a `ProcessorIdLogEnricher` OTel `BaseProcessor<LogRecord>` that appends `ProcessorId` to every processor log record, a nested `BeginScope` on the Completed path of `EntryStepDispatchConsumer`, and an explicit `BeginScope` in `WorkflowFireJob` for the outside-pipeline Quartz path. The close-gate script is a well-structured triple-SHA gate. The overall design is sound and security-conscious (ids in scope values, never interpolated into templates). No critical bugs were found. Three warnings address real correctness/reliability risks, and three info items flag code quality improvements.

## Warnings

### WR-01: `RecordingResultConsumer.Received` is a static mutable list — cross-test pollution if tests run in parallel

**File:** `tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs:83`
**Issue:** `RecordingResultConsumer.Received` is `public static readonly List<ExecutionResult> Received = new()`. The test clears it at the top of the single fact (line 105), but the field is class-level static. If xUnit ever runs this test's fact in parallel with any other test that also triggers an `ExecutionResult` on the `OrchestratorQueues.Result` queue — now or when a future test class shares the same in-memory bus instance — the Clear/Assert window becomes a race. The `lock` guards on the list are present but do not prevent cross-test accumulation before the Clear executes. The pattern also means a second invocation of this test (e.g. via `dotnet test --repeat`) carries stale state if the first run left residual entries and the Clear at line 105 races with a still-in-flight consumer callback.
**Fix:** Move the list to an instance field of the test class (or to a local captured in the test method), not a static. Since `RecordingResultConsumer` is a nested private class inside `EntryStepDispatchRuntimeScopeTests`, the capturing can be done with a captured reference:

```csharp
// Replace:
private sealed class RecordingResultConsumer : IConsumer<ExecutionResult>
{
    public static readonly List<ExecutionResult> Received = new();
    ...
}

// With an instance-per-test approach: give the consumer an injected list:
private sealed class RecordingResultConsumer(List<ExecutionResult> received)
    : IConsumer<ExecutionResult>
{
    public Task Consume(ConsumeContext<ExecutionResult> context)
    {
        lock (received) received.Add(context.Message);
        return Task.CompletedTask;
    }
}

// In the test, create and pass the list directly, register the singleton:
var received = new List<ExecutionResult>();
// ...
.AddSingleton<RecordingResultConsumer>(new RecordingResultConsumer(received))
```

---

### WR-02: `prometheus` service silently skipped if unhealthy — health-check exception list is incomplete

**File:** `scripts/phase-29-close.ps1:143`
**Issue:** The pre-flight health loop exempts only `otel-collector` from the `health -ne 'healthy'` abort guard (`$svc -ne 'otel-collector'`). The comment at line 30–31 lists `prometheus` as a required service, and it appears in the `$services` array at line 136. However, Prometheus containers commonly expose no Docker health-check (the official Prometheus image ships without a `HEALTHCHECK` instruction), so `docker compose ps prometheus --format json` returns `Health = ""` (empty string, not `"healthy"`). This will cause the gate to exit 2 (`"Service 'prometheus' is not healthy"`) on every run where Prometheus is running but has no health-check configured. If the project's `docker-compose.yml` adds a custom health-check for Prometheus, this is fine; if not, the gate becomes impossible to pass without patching the script. The prior phase-22-close.ps1 precedent this script mirrors should be checked to see whether Prometheus was exempted there.
**Fix:** Either (a) add `'prometheus'` to the exemption condition to match the `otel-collector` treatment if Prometheus has no configured Docker health-check:

```powershell
if ($health -ne 'healthy' -and $svc -ne 'otel-collector' -and $svc -ne 'prometheus') {
```

or (b) ensure the project's `docker-compose.yml` defines a `healthcheck:` for the `prometheus` service so `docker compose ps` returns `"healthy"`. Chose the approach that matches what the compose file actually provides.

---

### WR-03: `InboundExecutionScopeConsumeFilter` opens scope on a `Dictionary<string, object>` — potential key-collision with outer correlation scope

**File:** `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs:28-36`
**Issue:** The filter is registered as the INNER filter (line 52 of `MessagingServiceCollectionExtensions.cs`), meaning it runs inside the outer `InboundCorrelationConsumeFilter`. The outer filter opens a scope carrying `CorrelationId` (via `CorrelationKeys.LogScope`). The `ExecutionLogScope` key set (`WorkflowId`, `StepId`, `ProcessorId`, `ExecutionId`, `EntryId`) does not overlap with `CorrelationId`, so there is no collision today. However, `ExecutionLogScope.ProcessorId = "ProcessorId"` and the `ProcessorIdLogEnricher` also appends a `"ProcessorId"` attribute to the `LogRecord.Attributes` directly (not via scope). In a scenario where the bus-wide scope filter adds `ProcessorId` to the MEL scope AND the OTel enricher also adds it to `LogRecord.Attributes`, the final OTel export will carry `ProcessorId` twice on processor-side log lines emitted inside a consume (once from the scope flattened by `IncludeScopes`, once from the enricher). This is a data-quality issue: Elasticsearch will see duplicate `attributes.ProcessorId` values (or an array rather than a scalar), which can break term queries.

The design note in the enricher comment (`"Registered ONLY here — processor-side — never in the shared AddBaseConsoleObservability block"`) acknowledges the enricher runs only on the processor, but does not address the overlap with the scope filter which also runs on the processor bus. For an entry-step dispatch message with a non-empty `ProcessorId`, the scope filter will place `ProcessorId` in the MEL scope (via `IncludeScopes`) AND the enricher will independently append `ProcessorId` to `LogRecord.Attributes`. Depending on OTel's merge behavior (which the code comments cite as safe for direct attribute mutation, "no State desync"), this results in a duplicate key on the final record.

**Fix:** In `ProcessorIdLogEnricher.OnEnd`, guard against adding `ProcessorId` if it already appears in the existing `record.Attributes` (set by the MEL scope-flattening path):

```csharp
public override void OnEnd(LogRecord record)
{
    if (context.Id is not { } id) return;
    // Skip if ProcessorId already present (e.g. placed by InboundExecutionScopeConsumeFilter
    // via IncludeScopes on an IExecutionCorrelated consume — avoids duplicate ES attribute).
    if (record.Attributes?.Any(kvp => kvp.Key == ExecutionLogScope.ProcessorId) == true)
        return;
    record.Attributes = (record.Attributes ?? Array.Empty<KeyValuePair<string, object?>>())
        .Append(new KeyValuePair<string, object?>(ExecutionLogScope.ProcessorId, id.ToString()))
        .ToList();
}
```

---

## Info

### IN-01: `Dictionary<string, object>` scope state — consider `IReadOnlyDictionary` or a value-type state object for MEL scope allocation

**File:** `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs:28`
**Issue:** The filter allocates a `new Dictionary<string, object>()` and populates it with up to five entries on every consumed `IExecutionCorrelated` message. MEL's `BeginScope` accepts any `TState`, and using a plain `Dictionary<string, object>` is idiomatic and correct. However, MEL scope providers that implement `ISupportExternalScope` (including the OTel logger provider) receive the `TState` typed as the concrete dictionary, which allocates a dictionary + up to five `KeyValuePair<string, object>` boxed values per message. This is purely a style/quality observation; the current approach is not wrong.
**Fix:** No immediate action required. If allocation on the hot consume path becomes a concern, consider a small readonly struct or record type with the five nullable fields, implementing `IEnumerable<KeyValuePair<string, object>>`, which avoids the dictionary overhead.

---

### IN-02: `WorkflowFireJobScopeTests` uses a unique Quartz scheduler per test invocation but does not await `scheduler.Shutdown` on failure paths before the outer harness stops

**File:** `tests/BaseApi.Tests/Orchestrator/WorkflowFireJobScopeTests.cs:138-168`
**Issue:** The `scheduler` is started at line 138 and shut down in the inner `finally` at line 167. However, the `await scheduler.Shutdown(waitForJobsToComplete: false, ct)` receives the test's `CancellationToken`. If the xUnit test runner cancels `ct` before shutdown completes (e.g. a timeout), the scheduler may not release its background thread pool resources cleanly. Additionally, the `WorkflowFireJob.Execute` path calls `scheduler.RescheduleAsync`, which will enqueue a Quartz trigger that may fire AFTER the scheduler shutdown begins. Since `waitForJobsToComplete: false` is passed, this is bounded, but the pattern is worth documenting: if a future test change switches to `true`, a trigger that fires after shutdown is requested could deadlock the teardown.
**Fix:** Low-severity — no immediate code change required. Consider using `CancellationToken.None` for the shutdown call to ensure the scheduler always terminates cleanly regardless of test cancellation, and note that `waitForJobsToComplete: false` is intentional here.

---

### IN-03: Close-gate script performs `[System.Reflection.Assembly]::Load($asmBytes)` into the current PowerShell AppDomain — assembly accumulates in the process

**File:** `scripts/phase-29-close.ps1:71`
**Issue:** The comment at line 69 says the bytes are loaded into "a throwaway context so the file handle is released afterward." However, `[System.Reflection.Assembly]::Load(byte[])` in PowerShell 7 loads into the default `AssemblyLoadContext` (the shared app-domain), not an isolated context. The assembly is never unloaded. This is benign for a single gate run (the process exits) but the comment's claim about "released" is misleading. If someone wraps this logic in a long-running script, the assembly would accumulate. For a one-shot gate script this has no practical consequence.
**Fix:** Clarify the comment, or use an isolated `AssemblyLoadContext` if unloading is actually desired:

```powershell
# NOTE: Assembly.Load(byte[]) loads into the default ALC in pwsh — the assembly is not
# unloaded, but the FILE handle is released (bytes were already read). The process exits
# after the gate, so this accumulation is harmless.
$asm = [System.Reflection.Assembly]::Load($asmBytes)
```

---

_Reviewed: 2026-06-02T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
