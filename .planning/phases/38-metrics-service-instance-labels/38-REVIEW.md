---
phase: 38-metrics-service-instance-labels
reviewed: 2026-06-06T00:00:00Z
depth: standard
files_reviewed: 22
files_reviewed_list:
  - src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs
  - src/BaseApi.Service/Features/Processor/Responders/GetProcessorBySourceHashConsumer.cs
  - src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs
  - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
  - src/BaseProcessor.Core/Identity/IProcessorContext.cs
  - src/BaseProcessor.Core/Identity/ProcessorContext.cs
  - src/BaseProcessor.Core/Observability/MeterProviderHolder.cs
  - src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
  - src/Messaging.Contracts/ProcessorQueries.cs
  - tests/BaseApi.Tests/Messaging/ProcessorResponderTests.cs
  - tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs
  - tests/BaseApi.Tests/Observability/LogsResourceBareNameFacts.cs
  - tests/BaseApi.Tests/Observability/MeterProviderHolderFacts.cs
  - tests/BaseApi.Tests/Observability/MetricsExportTests.cs
  - tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs
  - tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs
  - tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs
  - tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs
  - tests/BaseApi.Tests/Processor/FakeProcessorContext.cs
  - tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs
  - tests/BaseApi.Tests/Processor/ProcessorTestHarness.cs
  - tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs
findings:
  critical: 0
  warning: 2
  info: 4
  total: 6
status: issues_found
---

# Phase 38: Code Review Report

**Reviewed:** 2026-06-06
**Depth:** standard
**Files Reviewed:** 22
**Status:** issues_found

## Summary

Phase 38 combines `service_name={name}_{version}` on the OpenTelemetry METRICS resource (across BaseApi, BaseConsole, and the new processor MeterProvider swap) while deliberately keeping the LOGS resource bare, and introduces `MeterProviderHolder` to swap a boot-placeholder metrics provider for a DB-sourced one once the processor's identity Loop A resolves. The change is well-structured, heavily documented, and well-tested: the hermetic `MeterProviderHolderFacts` proves the name swap, instance-id preservation, provider-#1 shutdown, and double-dispose idempotency; the positional-record change to `ProcessorIdentityFound` is consistently propagated through the responder, the contract, the context, and every test harness; and the LOGS-stays-bare invariant has a dedicated guard test (`LogsResourceBareNameFacts`).

No critical issues were found. The two warnings concern (a) a non-thread-safe field publish + missing barrier on the `_current` swap relative to the host-shutdown `Dispose`, and (b) a resource-leak window if `ForceFlush`/`Dispose` on the prior provider throws after `_current` has already been repointed. Both are low-likelihood given the single-call-site design but are worth tightening because the class is explicitly documented as straddling two threads (Loop A writer + DI/host shutdown disposer). The remaining items are informational hardening notes.

## Warnings

### WR-01: `_current` field is published without a memory barrier; host-shutdown `Dispose` may observe the stale (already-disposed) provider #1

**File:** `src/BaseProcessor.Core/Observability/MeterProviderHolder.cs:32,71-81`
**Issue:** `_current` is a plain (non-volatile) field. `SwapTo` runs on the Loop A background-service thread; `Dispose` runs on the DI-container / host-shutdown thread. There is no `lock`, `volatile`, or `Interlocked` coordinating the two. The class XML-doc explicitly states the holder "straddles" the swap thread and the shutdown-disposer thread, so this is a genuine cross-thread field access with no published happens-before edge. Two consequences:
1. A shutdown racing the swap could read a stale `_current` referencing provider #1 (already disposed by `SwapTo`) and skip disposing provider #2 → provider #2 (and its OTLP gRPC channel) leaks at shutdown. The double-dispose of #1 is safe (documented), but the *missed* dispose of #2 is the real leak.
2. Even absent a race, the write to `_current` inside `SwapTo` has no release semantics, so the shutdown thread is not guaranteed to observe `next`.

In the current happy path the swap completes long before shutdown, so this is unlikely to bite — but the safety argument the class relies on ("`SwapTo` before queue-bind") only orders the swap against the *dispatch counters*, not against host *shutdown*, which can be triggered at any time (SIGTERM during boot).
**Fix:** Make the field publish explicit and atomic. Minimal change:
```csharp
public void SwapTo(string resolvedServiceName)
{
    var next  = Build(resolvedServiceName);
    var prior = Interlocked.Exchange(ref _current, next); // release-publish #2, take #1
    prior.ForceFlush(timeoutMilliseconds: 5000);
    prior.Dispose();
}

public void Dispose() => Volatile.Read(ref _current)?.Dispose(); // acquire-read whatever is current
```
This guarantees `Dispose` always sees the latest provider and never skips #2. (If concurrent `SwapTo` calls are ever possible, `Interlocked.Exchange` also makes the take-prior atomic — see IN-02.)

### WR-02: prior provider leaks if `ForceFlush` throws after `_current` is repointed in `SwapTo`

**File:** `src/BaseProcessor.Core/Observability/MeterProviderHolder.cs:71-78`
**Issue:** Ordering is: build `next` → repoint `_current = next` → `prior.ForceFlush(...)` → `prior.Dispose()`. If `ForceFlush` throws (e.g., the OTLP export pump faults, or a reader's `OnForceFlush` throws), `prior.Dispose()` is skipped and the exception propagates out of `SwapTo` into Loop A. Because `_current` has already been repointed to #2, the local `prior` reference is lost and provider #1's reader/exporter/gRPC channel is never released → leak. Separately, the unhandled exception in `SwapTo` is thrown on the Loop A thread at line 89 of `ProcessorStartupOrchestrator`, which is *outside* the `catch (RequestTimeoutException)` block — it would escape `ExecuteAsync` and fault the hosted service (the host treats a faulted `BackgroundService` per its `BackgroundServiceExceptionBehavior`, default StopHost in .NET 8).
**Fix:** Guard the flush+dispose so the prior provider is always disposed, and don't let a best-effort flush failure crash startup:
```csharp
var prior = Interlocked.Exchange(ref _current, next);
try   { prior.ForceFlush(timeoutMilliseconds: 5000); }
finally { prior.Dispose(); } // always release #1 even if flush throws
```
Consider also wrapping the `SwapTo` call site (`ProcessorStartupOrchestrator.cs:89`) so a swap fault degrades to "keep emitting on the placeholder provider + log" rather than faulting the host — the metrics label is non-load-bearing for correctness, but identity has already resolved and the processor should still go Healthy.

## Info

### IN-01: `ForceFlush` return value is discarded

**File:** `src/BaseProcessor.Core/Observability/MeterProviderHolder.cs:76`
**Issue:** `prior.ForceFlush(timeoutMilliseconds: 5000)` returns a `bool` indicating whether the flush completed within the timeout. It is silently ignored, so a placeholder-window batch that fails to flush within 5s is dropped without any signal.
**Fix:** Optionally log when the flush times out: `if (!prior.ForceFlush(5000)) logger.LogWarning("Placeholder metrics flush timed out before swap");`. (Requires threading a logger into the holder — low priority; the dropped batch is the short boot window only.)

### IN-02: `SwapTo` is not guarded against repeated or concurrent invocation

**File:** `src/BaseProcessor.Core/Observability/MeterProviderHolder.cs:71-78`
**Issue:** `SwapTo` has a single documented call site (Loop A, once), but nothing in the class enforces that. A second call would build a third provider and dispose the second; two concurrent calls (not currently possible) would race on `_current` and could leak one of the built providers. This is a latent fragility, not a present bug.
**Fix:** The `Interlocked.Exchange` from WR-01 already makes the prior-take atomic. If single-shot is a true invariant, additionally assert it (e.g., a `_swapped` int CAS that throws/no-ops on the second call) so a future caller can't silently double-swap.

### IN-03: Four-way duplicated `service.instance.id` precedence is fragile despite the drift-guard comment

**File:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:176-181`, `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs:104-108`, `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs:95-99`
**Issue:** The `POD_NAME → HOSTNAME → MachineName → GUID` precedence is now copy-pasted in four places (this phase added the fourth, `ResolveInstanceIdForHolder`). The XML-doc DRIFT GUARD comments document the lock-step requirement and a hermetic mirror test (`ResolveInstanceIdFacts`) exists, but the only automated protection is that mirror — there is no compile-time link between the four copies. This is an accepted trade-off per D-09 (BaseConsole.Core cannot reference BaseApi.Core), but it remains a maintenance hazard: a fifth consumer or a precedence tweak must touch all four + the test.
**Fix:** No change required this phase (the documented mirror test is the agreed mitigation). If the copy count grows further, reconsider a tiny shared primitive in a neutral assembly (e.g., a `BaseConsole.Core` helper that BaseApi/BaseProcessor both reference) rather than continuing to fan out.

### IN-04: WR-03 memory-visibility caveat is bypassed at the swap call site only because of co-thread reads — worth an explicit assertion

**File:** `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:83-89`
**Issue:** `context.SetIdentity(found.Message)` writes `Name`/`Version` to plain auto-properties (documented WR-03: only safe cross-thread after `IsHealthy`), then `SwapTo($"{found.Message.Name}_{found.Message.Version}")` reads `Name`/`Version` *off the message directly* rather than off the context — which is correct and the comment notes this ("memory-visibility is moot at the call-site"). The subtlety is fine here, but the combined name is derived from the message in one place and the context stores the same fields separately; a future refactor that switches the `SwapTo` argument to read `context.Name`/`context.Version` (the seemingly-equivalent change) would reintroduce the WR-03 hazard on any thread other than Loop A.
**Fix:** No code change needed. Consider a one-line comment reinforcing "read Name/Version from the message, never from context, on the swap line" so the WR-03 trap isn't re-sprung by a well-meaning cleanup. (The existing comment at lines 84-88 mostly covers this; making the "never from context" part explicit would harden it.)

---

_Reviewed: 2026-06-06_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
