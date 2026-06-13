---
phase: 60-dual-loop-writer-in-memory-l1-liveness-record
reviewed: 2026-06-13T00:00:00Z
depth: standard
files_reviewed: 17
files_reviewed_list:
  - src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs
  - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
  - src/BaseProcessor.Core/Liveness/IProcessorLivenessState.cs
  - src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs
  - src/BaseProcessor.Core/Liveness/ProcessorLivenessState.cs
  - src/BaseProcessor.Core/Liveness/ProcessorLivenessWriter.cs
  - src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
  - tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs
  - tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs
  - tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs
  - tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs
  - tests/BaseApi.Tests/Processor/LivenessResilienceFacts.cs
  - tests/BaseApi.Tests/Processor/ProcessorLivenessStateFacts.cs
  - tests/BaseApi.Tests/Processor/ProcessorLivenessWriterFacts.cs
  - tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs
  - tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs
  - tests/BaseApi.Tests/Processor/StartupUnhealthyWriteFacts.cs
findings:
  critical: 0
  warning: 2
  info: 3
  total: 5
status: issues_found
---

# Phase 60: Code Review Report

**Reviewed:** 2026-06-13
**Depth:** standard
**Files Reviewed:** 17
**Status:** issues_found

## Summary

Reviewed the Phase-60 dual-loop liveness writer subsystem: the in-memory L1 holder
(`ProcessorLivenessState`), the shared write path (`ProcessorLivenessWriter`), the
only-when-Healthy heartbeat loop (`ProcessorLivenessHeartbeat`), the startup orchestrator's
inline unhealthy writes (`ProcessorStartupOrchestrator`), the options binding, and the DI
composition root, plus the 10 supporting fact files.

Overall the subsystem is well-constructed and the core correctness invariants hold up under
scrutiny:

- **TTL math is correct and consistent** across both loops. Heartbeat: `max(10*2, Ttl-floor 30) = 30`;
  startup: `max(30*2, 30) = 60`. The derived-TTL formula `max(entry.Interval * 2, TtlSeconds)` in
  `ProcessorLivenessWriter` matches every doc comment and every `Assert.InRange` band in the tests.
- **Status derivation is sound.** `ProcessorLivenessEntry.Create` is the single enforcement point;
  any `SchemaOutcome.Fail` ⇒ `Unhealthy`, and the Gate-A clash path correctly forces
  `configOutcome = Fail` so an all-resolved-but-clashed replica is still published `Unhealthy`.
- **DI lifetimes are correct.** Writer / L1 state / orchestrator / heartbeat are all singletons
  (one per replica process); the orchestrator + heartbeat are registered as concrete singletons and
  re-surfaced as `IHostedService` resolving the same instance (no duplicate instances).
- **L1-vs-L2 cannot desync** (same immutable `ProcessorLivenessEntry` reference written to both;
  proven by `Assert.Same` in `ProcessorLivenessWriterFacts`).
- **The lock-free `volatile` reference-swap L1 holder (D-10) is an intentional design choice** and is
  correct for reference-type publication on the CLR — not flagged.
- **Redis-fault resilience** in `ProcessorLivenessWriter.WriteAsync` (log-and-continue, L1 updated
  unconditionally before the Redis attempt) is correct and well-tested.

Two warnings concern unhandled exception types that contradict the documented "host never crashes"
resilience contract, plus three minor quality items.

## Warnings

### WR-01: Loop A / Loop B catch only `RequestTimeoutException` — any other transient bus fault crashes the orchestrator host

**File:** `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:91-137` (Loop A) and `:165-184` (Loop B)
**Issue:** The class XML doc states (lines 59-63) "NotFound + timeout are caught and looped so the host
never crashes while the WebApi responder / DB row is absent. Only shutdown cancellation returns."
However the request loops catch ONLY `RequestTimeoutException`:

```csharp
catch (RequestTimeoutException)
{
    logger.LogWarning("Identity request timed out; retrying in {Delay}", delay);
}
```

`NotFound` is correctly handled as a dual-response (`resp.Is(out found)` false branch), so that part
is fine. But this is the explicit boot-before-register window — the broker may not yet be fully reachable.
If `identityClient.GetResponse(...)` / `schemaClient.GetResponse(...)` throws anything OTHER than
`RequestTimeoutException` (e.g. a `MassTransitException` / `RequestException` / broker connection drop
during a reconnect, or a transient transport fault), the exception propagates out of `ExecuteAsync`,
faults the `BackgroundService`, and — per the host's `BackgroundServiceExceptionBehavior` default
(`StopHost`) — can take the whole replica down. That directly contradicts the stated contract and is the
one failure mode the unbounded-retry design is meant to absorb. Note the Redis resilience path is robust
(the writer swallows faults), so this is the remaining gap on the messaging side.

**Fix:** Broaden the catch to cover transient transport faults and re-loop on backoff, e.g.:

```csharp
catch (RequestTimeoutException)
{
    logger.LogWarning("Identity request timed out; retrying in {Delay}", delay);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // Boot-before-register: a transient bus/transport fault must not crash the host — re-loop on backoff.
    logger.LogWarning(ex, "Identity request faulted transiently; retrying in {Delay}", delay);
}
```

(apply symmetrically to Loop B at lines 181-184). Preserve the existing `OperationCanceledException`
exclusion so shutdown cancellation still returns cleanly. If a deliberately narrower set is desired,
catch the specific MassTransit transport exception types — but a bare `RequestTimeoutException`-only
catch leaves the documented "host never crashes" guarantee unmet.

### WR-02: Heartbeat catch re-reads `_context.Id` after the gate already captured `id` — log can desync from the value used for the write

**File:** `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs:76-104`
**Issue:** The healthy gate captures the id into a local (`_context.Id is { } id`) and uses that local
for the write (`_writer.WriteAsync(id, _instanceId, entry)`). But the catch block logs `_context.Id`
(the property) rather than the captured `id`:

```csharp
"...failed for processor {ProcessorId}; will retry next beat",
_context.Id);
```

`IProcessorContext.Id` is set once and never un-set, so in practice the value is stable and this is not a
correctness bug today. However it is a latent inconsistency: the write and the diagnostic should reference
the identical value, and a future change that allows `Id` to be cleared (or read across a memory barrier)
would make the warning report a different/`null` id than the one whose write actually failed — degrading
incident triage on the exact path that exists to aid triage. The resilience test
(`LivenessResilienceFacts`) asserts on the WRITER's logger, not this one, so this branch is effectively
unexercised.

**Fix:** Log the already-captured local:

```csharp
_logger.LogWarning(
    ex,
    "Liveness heartbeat write failed for processor {ProcessorId}; will retry next beat",
    id);
```

## Info

### IN-01: `configOutcomeOverride` typed as `string?` but documented and used as a `SchemaOutcome`

**File:** `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:283`
**Issue:** `WriteUnhealthyAsync(string? configOutcomeOverride = null)` takes a bare `string?` even though
its only caller passes `SchemaOutcome.Fail` and the XML doc describes it as a `SchemaOutcome`. There is no
type-level guard preventing a caller from passing an arbitrary string that is neither `"SUCCESS"` nor
`"FAIL"`, which would silently pass the `Create` any-Fail check (any non-`FAIL` string ⇒ treated as not a
failure ⇒ `Healthy`).
**Fix:** Either keep `string?` and add a `Debug.Assert` / guard that the value is one of the two
`SchemaOutcome` consts, or document the contract inline. Low priority — there is exactly one internal
caller passing the correct const.

### IN-02: Stale/inaccurate doc comments reference the removed flat-key scheme and pre-Phase-60 counts

**File:** `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs:31` and `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:42-43`
**Issue:** A few comments still describe the intentionally-removed flat-key world:
- `ProcessorLivenessOptions.cs:31` — `TtlSeconds` doc says "the `SET..EX` TTL on `skp:{processorId:D}`",
  but the flat `skp:{id}` write was removed this phase (D-05); the TTL now lands on the per-instance key
  via the derived-TTL formula.
- `ProcessorStartupOrchestrator.cs:42-43` (Gate A doc) — "a clash means no `skp:{id}` key → the
  orchestration-start liveness gate reports 'absent' → 422." The Gate-A clash path now WRITES an
  `unhealthy` per-instance key (line 211), so the gate fails on `status`, not on `absent`.
- `BaseProcessorServiceCollectionExtensions.cs:100` says "four independent seconds-ints" while
  `ProcessorLivenessOptions` now exposes six.

These are documentation-only and do not affect behavior, but they will mislead future readers tracing the
D-05 removal.
**Fix:** Update the comments to reference the per-instance key (`skp:proc:{id}:{instanceId}`) and the
`status`-based gate behavior, and correct the "four"→"six" count.

### IN-03: `InstanceId.Resolve()` precedence is duplicated in four places guarded only by a comment

**File:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:244-248`
**Issue:** `ResolveInstanceIdForHolder()` re-implements the `POD_NAME ?? HOSTNAME ?? MachineName ?? GUID`
precedence that already exists in `Messaging.Contracts.Identity.InstanceId.Resolve()` (which this same
class calls at line 199 via `InstanceId.Resolve()`). The drift-guard comment lists four copies that "MUST
change in lock-step." A purely-comment-enforced invariant across four files is fragile — the holder's copy
even renders the GUID fallback identically to `InstanceId.Resolve()` yet is maintained separately.
**Fix:** Where the cycle-free reference allows, have `ResolveInstanceIdForHolder()` (and the other OTel
copies) delegate to `InstanceId.Resolve()` so there is one implementation. If the observability copies
genuinely cannot reference `Messaging.Contracts.Identity`, leave as-is — but at minimum the holder copy in
this file (which already references `InstanceId`) could call it directly. Out of strict Phase-60 scope;
noted because the new `InstanceId` SoT was introduced this phase and partially supersedes the older copies.

---

_Reviewed: 2026-06-13_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
