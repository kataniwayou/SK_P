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
  warning: 3
  info: 5
  total: 8
status: issues_found
---

# Phase 26: Code Review Report

**Reviewed:** 2026-06-01T00:00:00Z
**Depth:** standard
**Files Reviewed:** 23
**Status:** issues_found

## Summary

Reviewed the Phase 26 `BaseProcessor.Core` library (identity resolution, liveness
heartbeat, startup orchestration, the abstract processor seam, DI composition root) plus
its 12 test fixtures. Overall the code is clean, well-documented, and the concurrency
primitives in `ProcessorContext` are correct (the `Interlocked.Exchange` in `MarkHealthy`
publishes the prior definition writes; the heartbeat's `Volatile.Read` of `IsHealthy`
acquires them — no torn-read on the gated path).

No critical security or correctness defects were found. The findings are three warnings
worth confirming against intent (a documentation/comment drift in the composition root, an
unbounded-retry liveness concern that is by-design but worth flagging, and an unguarded
direct read of `InputDefinition`/`OutputDefinition` on the non-gated startup path) and a
handful of minor quality observations.

## Warnings

### WR-01: Composition-root XML doc contradicts the actual heartbeat registration (stale comment)

**File:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:30-34` (and `:51-52`)
**Issue:** The class-level `<summary>` explicitly states the heartbeat is NOT registered
this phase:

> "**NO dispatch consumer this phase.** The `EntryStepDispatch` consumer + the
> `ProcessorLivenessHeartbeat` hosted service are Phase 27 / Plan 03 — they are NOT
> registered here"

But the method DOES register it at line 79
(`services.AddHostedService<ProcessorLivenessHeartbeat>();`), and the inline comment at
lines 51-52 likewise still says "no consumer is registered this phase: the dispatch
consumer is Phase 27." The heartbeat registration (step 7b) is correct and intended for
this phase; the doc comment is stale and will mislead the next maintainer about what
`AddBaseProcessor` wires. (The `EntryStepDispatch` consumer note remains accurate — only
the heartbeat clause is wrong.)
**Fix:** Update the `<summary>` so it no longer lists `ProcessorLivenessHeartbeat` under
"NOT registered here." For example:
```csharp
/// <para>
/// <b>NO dispatch consumer this phase.</b> The <c>EntryStepDispatch</c> consumer is
/// Phase 27 — it is NOT registered here (no consumer is registered, so no receive
/// endpoints are auto-bound this phase). The <see cref="ProcessorLivenessHeartbeat"/>
/// hosted service IS registered here (step 7b).
/// </para>
```

### WR-02: Heartbeat aborts the current beat on `OperationCanceledException` thrown by Redis, not only on shutdown

**File:** `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs:90-103`
**Issue:** The write `catch (Exception ex)` at line 95 deliberately swallows ALL exceptions
to keep the host alive (D-11, correct). However, `StringSetAsync` is invoked without the
`stoppingToken`, so a Redis-side cancellation/timeout surfaced as `OperationCanceledException`
is caught here and logged as a "Liveness write failed" warning — which is the intended
log-and-continue behavior, so this is acceptable. The real concern is the inverse: because
the token is NOT passed into `StringSetAsync`, a shutdown request will not cancel an
in-flight slow Redis write; the beat only observes cancellation at the subsequent
`Task.Delay(period, _clock, stoppingToken)` (line 108). On a hung-but-not-dead Redis this
can delay graceful shutdown until StackExchange.Redis' own command timeout elapses.
**Fix:** Thread the token into the write so shutdown cancels a pending command promptly,
while still catching `OperationCanceledException` only when shutdown was NOT requested:
```csharp
await db.StringSetAsync(
    L2ProjectionKeys.Processor(id),
    json,
    expiry: TimeSpan.FromSeconds(opts.TtlSeconds));
// StringSetAsync has no CancellationToken overload; consider WaitAsync(stoppingToken)
// so shutdown is not blocked by a hung command:
// await db.StringSetAsync(...).WaitAsync(stoppingToken);
```
If the current "let the StackExchange.Redis command timeout bound it" behavior is the
intended design, leave the code as-is and add a one-line comment documenting that shutdown
latency is bounded by the Redis command timeout, not `stoppingToken`.

### WR-03: Startup orchestrator reads `context.InputSchemaId`/`OutputSchemaId` across the Loop A→Loop B boundary without an explicit memory barrier

**File:** `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:72,97`
**Issue:** This is single-threaded within `ExecuteAsync` (Loop A writes via
`context.SetIdentity`, Loop B reads `context.InputSchemaId`/`OutputSchemaId`), so it is
correct today. The latent risk is for a future maintainer: `ProcessorContext`'s identity
properties are plain `private set` auto-properties with NO volatile/barrier semantics
(unlike `IsHealthy`). The only reason cross-thread reads (e.g., the heartbeat reading
`InputDefinition`) are safe is the incidental full barrier in `MarkHealthy`'s
`Interlocked.Exchange`. If Phase 27 ever reads identity/definition fields from another
thread WITHOUT first observing `IsHealthy == true` (or awaiting `WhenHealthy`), it could
observe stale nulls. The `<summary>` on `ProcessorContext` claims "Thread-safe" but only
`IsHealthy`/`WhenHealthy` actually carry synchronization.
**Fix:** Either document the invariant precisely on `IProcessorContext` — "identity and
definition properties are only safe to read after `IsHealthy` is observed true or
`WhenHealthy` has completed" — or, if cheap-anytime reads are desired, back the Guid?/string?
properties with `Volatile.Read`/`Volatile.Write` (or a single immutable snapshot record
swapped via `Volatile`). No code change required if the read-after-Healthy invariant is
documented.

## Info

### IN-01: `ProcessResult` is an empty positional record — confirm analyzer/style gates accept it

**File:** `src/BaseProcessor.Core/Processing/ProcessResult.cs:9`
**Issue:** `public sealed record ProcessResult();` has no members. This is intentional
(fields land in Phase 27 per the doc comment), and `BaseProcessorSeamFacts` exercises it,
so it is not dead code. Flagging only because an empty record with explicit `()` can read as
accidental; the design note in the file makes the intent clear.
**Fix:** No change needed. Optionally drop the empty `()` to `public sealed record
ProcessResult;` for slightly cleaner intent, or keep as-is.

### IN-02: Redundant local `var opts = _options;` / `var opts = options.Value;`

**File:** `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs:63` and `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:55`
**Issue:** In the heartbeat, `var opts = _options;` (line 63) just aliases the field; later
code mixes `opts.IntervalSeconds`/`opts.TtlSeconds` with no clear benefit over `_options`
directly. Minor readability noise. (The orchestrator's `var opts = options.Value;` is more
justified — it dereferences `IOptions<T>.Value` once.)
**Fix:** In the heartbeat, use `_options` directly and drop the `opts` local, or keep the
local but read all option values through it consistently.

### IN-03: `IdentityResolutionFacts` reaches into a private field via reflection

**File:** `tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs:110-117`
**Issue:** `GetIdentityCallCount` uses reflection to read the private
`_identityNotFoundCalls` field of `ResponderSequence`. This couples the test to an internal
field name; renaming the field silently breaks the assertion (reflection throws at runtime,
not compile time). The harness already exposes `IdentityNotFoundCount` publicly — consider
exposing a public read-only call counter instead.
**Fix:** Add a public counter to `ResponderSequence`, e.g.
`public int IdentityCalls => Volatile.Read(ref _identityNotFoundCalls);`, and assert against
that, eliminating the reflection.

### IN-04: Test polling loops sleep on real time (`Task.Delay(20/50)`) — potential flakiness on loaded CI

**File:** `tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs:106`, `tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs:61,93`, `tests/BaseApi.Tests/Processor/LivenessResilienceFacts.cs:57-59`
**Issue:** Several facts use fixed `await Task.Delay(20|50)` to let a background continuation
run after advancing a `FakeTimeProvider`. These are bounded by an outer 30s
`CancelAfter`, so a hang fails fast, but a heavily loaded CI agent could occasionally not
schedule the continuation within 50ms, producing a flaky "key not written yet" assertion in
`LivenessHeartbeatFacts.Healthy_Writes_WholeValue_With_Sliding_Ttl`. The
`AdvanceUntilAsync` poll-loop pattern (IdentityResolutionFacts) is more robust; the
single-shot `Task.Delay(50)` cases are the weaker spots.
**Fix:** Where feasible, replace single-shot `Task.Delay(50)` with a short poll-until
loop (poll `KeyExistsAsync` up to a deadline) so the test waits for the observable effect
rather than a fixed wall-clock interval. Given the existing CancelAfter guards this is a
robustness improvement, not a correctness defect.

### IN-05: `BackoffAsync` doubles delay using floating-point seconds

**File:** `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:159`
**Issue:** `TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, cap.TotalSeconds))`
computes the next backoff in `double` seconds. For the small integer-second delays used here
(1, 2, 4, ... capped at 30) this is exact and correct. Flagging only as a minor style note —
`TimeSpan` arithmetic (`delay * 2` and `delay > cap ? cap : delay * 2`) would express the
intent without the float round-trip.
**Fix:** Optional:
```csharp
var doubled = delay * 2;
return doubled > cap ? cap : doubled;
```

---

_Reviewed: 2026-06-01T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
