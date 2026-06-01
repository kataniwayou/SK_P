---
phase: 26-baseprocessor-core-library-identity-liveness
reviewed: 2026-06-01T00:00:00Z
depth: standard
files_reviewed: 23
files_reviewed_list:
  - src/BaseProcessor.Core/BaseProcessor.Core.csproj
  - src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs
  - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
  - src/BaseProcessor.Core/Identity/AssemblyMetadataSourceHashProvider.cs
  - src/BaseProcessor.Core/Identity/IProcessorContext.cs
  - src/BaseProcessor.Core/Identity/ISourceHashProvider.cs
  - src/BaseProcessor.Core/Identity/ProcessorContext.cs
  - src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs
  - src/BaseProcessor.Core/Processing/BaseProcessor.cs
  - src/BaseProcessor.Core/Processing/ProcessResult.cs
  - src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
  - tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs
  - tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs
  - tests/BaseApi.Tests/Processor/FakeProcessorContext.cs
  - tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs
  - tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs
  - tests/BaseApi.Tests/Processor/LivenessReaderRoundTripFacts.cs
  - tests/BaseApi.Tests/Processor/LivenessResilienceFacts.cs
  - tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs
  - tests/BaseApi.Tests/Processor/ProcessorTestHarness.cs
  - tests/BaseApi.Tests/Processor/RequestClientSchemeFacts.cs
  - tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs
  - tests/BaseApi.Tests/Processor/SourceHashProviderFacts.cs
findings:
  critical: 0
  warning: 0
  info: 2
  total: 2
status: issues_found
---

# Phase 26: Code Review Report

**Reviewed:** 2026-06-01T00:00:00Z
**Depth:** standard
**Files Reviewed:** 23
**Status:** issues_found (Info only — no Critical, no Warning)

## Summary

This is a re-review of the BaseProcessor.Core library (identity + liveness) after fixes were
applied for the three prior warnings (WR-01 stale composition-root doc, WR-02 shutdown-token doc,
WR-03 thread-safety doc invariant). All 23 listed source and test files were read in full at
standard depth, with cross-references to `Messaging.Contracts` (`ProcessorQueues`,
`GetProcessorBySourceHash`, `ProcessorIdentityFound`, `GetSchemaDefinition`, `SchemaDefinitionFound`)
verified to resolve.

**All three prior warnings are RESOLVED:**

- **WR-01 (stale composition-root doc):** `BaseProcessorServiceCollectionExtensions` now documents
  BOTH hosted services. The class-level `<para>` block explicitly states "The
  `ProcessorLivenessHeartbeat` hosted service IS registered here (step 7b)" and the inline comment
  at step 7b matches the actual `AddHostedService<ProcessorLivenessHeartbeat>()` call. The
  "NO dispatch consumer this phase" note is accurate — no consumer is registered, so no receive
  endpoints auto-bind. Doc matches code.

- **WR-02 (shutdown-token doc):** `ProcessorLivenessHeartbeat.ExecuteAsync` carries the explicit
  "BY DESIGN: stoppingToken is deliberately NOT threaded into the write" comment block
  (`ProcessorLivenessHeartbeat.cs:91-96`), correctly explaining that `StringSetAsync` has no
  `CancellationToken` overload and that shutdown latency is bounded by the StackExchange.Redis
  command timeout, with the token observed only at the `Task.Delay`. The rationale is sound and the
  D-11 log-and-continue catch is intact.

- **WR-03 (thread-safety doc invariant):** The memory-visibility invariant is now documented on
  `IProcessorContext` (`IProcessorContext.cs:22-31`) and cross-referenced from `ProcessorContext`
  (`ProcessorContext.cs:5-9`). The invariant correctly states that the plain auto-properties are
  only safe to read cross-thread AFTER observing `IsHealthy == true` (the `Interlocked.Exchange`
  full barrier in `MarkHealthy` publishes the prior writes). The actual reader
  (`ProcessorLivenessHeartbeat.cs:70`) honors this — it gates on `_context.IsHealthy && _context.Id
  is { } id` before reading `InputDefinition`/`OutputDefinition`, so the invariant holds in practice.

No new Critical or Warning issues were found. The identity/backoff loops are cancellation-safe
(both `BackoffAsync` and the heartbeat `Task.Delay` catch `OperationCanceledException` and return),
fail-fast secrets handling is correct (`AssemblyMetadataSourceHashProvider` throws naming the KEY
only, never a value — V7 mitigation), the config schema id is correctly never queried (D-05), and
null schema ids are skipped without a request (SCHEMA-02). Tests provide strong coverage of the
retry-then-resolve, only-when-Healthy, sliding-TTL, dead-Redis resilience, and reader round-trip
(live→stale) paths. Two Info-level observations follow; neither is a defect.

## Info

### IN-01: Heartbeat catch-block logs `_context.Id` instead of the captured local `id`

**File:** `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs:109`
**Issue:** The success path captures the non-null id into a local via the pattern
`_context.Id is { } id` (line 70) and uses that local for the write (line 98). The catch-block
warning, however, re-reads the property `_context.Id` (a `Guid?`) for the `{ProcessorId}` log
argument rather than reusing the already-captured `id`. This is a re-read of a non-volatile
nullable property inside the loop body; while the heartbeat is the only writer-observer here and the
value cannot change to null mid-beat in this phase, using the captured local is more consistent with
the success path and avoids logging a boxed `Guid?` instead of a `Guid`.
**Fix:**
```csharp
catch (Exception ex)
{
    _logger.LogWarning(
        ex,
        "Liveness write failed for processor {ProcessorId}; will retry next beat",
        id); // reuse the captured non-null local instead of re-reading _context.Id
}
```

### IN-02: `ProcessResult` is an empty positional record (intentional placeholder)

**File:** `src/BaseProcessor.Core/Processing/ProcessResult.cs:9`
**Issue:** `public sealed record ProcessResult();` carries no members. This is explicitly documented
as a Phase 27 placeholder ("the concrete fields ... are firmed up in Phase 27 when the seam is
actually invoked"), and the `BaseProcessor.ProcessAsync` seam is declared-not-invoked this phase, so
this is by design and not a defect. Recording only so the empty record is not mistaken for an
oversight in a future review — confirm the fields land in Phase 27 alongside the seam invocation.
**Fix:** None required this phase. Track as a Phase 27 follow-up (add output-data + per-result
identifier fields when the framework wires the `ProcessAsync` invocation).

---

_Reviewed: 2026-06-01T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
