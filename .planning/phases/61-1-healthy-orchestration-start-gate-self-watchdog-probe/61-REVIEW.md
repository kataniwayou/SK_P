---
phase: 61-1-healthy-orchestration-start-gate-self-watchdog-probe
reviewed: 2026-06-13T00:00:00Z
depth: standard
files_reviewed: 12
files_reviewed_list:
  - src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs
  - src/BaseConsole.Core/Health/HealthCheckDescriptor.cs
  - src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs
  - src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs
  - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
  - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
  - tests/BaseApi.Tests/Features/Orchestration/ProcessorLivenessGateUnitTests.cs
  - tests/BaseApi.Tests/Features/Liveness/LivenessWatchdogHealthCheckTests.cs
  - tests/BaseApi.Tests/Console/ProcessorConsoleTestHostFixture.cs
  - tests/BaseApi.Tests/Console/ProcessorHealthLiveTests.cs
findings:
  critical: 0
  warning: 2
  info: 4
  total: 6
status: issues_found
---

# Phase 61: Code Review Report

**Reviewed:** 2026-06-13
**Depth:** standard
**Files Reviewed:** 12
**Status:** issues_found

## Summary

This phase swaps the WebAPI orchestration-start processor-liveness gate from a single
last-write-wins `GET skp:{procId}` read to a per-instance discovery gate
(`SMEMBERS skp:proc:{procId}` then `GET` each per-instance key, ">=1 healthy + fresh" admits),
adds a processor self-watchdog `/health/live` probe through a generic `HealthCheckDescriptor` seam,
and removes the legacy `ProcessorProjection` / flat `Processor(Guid)` key builder.

Overall the implementation is solid and the security-sensitive surfaces are well-handled:

- The 422-vs-500 split is correctly partitioned. Every deterministic data state (absent / unhealthy /
  stale / malformed) is counted into the 422 gate; the validator adds no `RedisException` catch, so a
  genuine transport fault propagates to the caller's `redisOp` catch for a 500. The unit tests prove
  the malformed-JSON path is counted rather than thrown.
- Info-disclosure is correctly contained. The 422 reason carries per-state COUNTS only (no instanceIds,
  connection strings, or stack traces), and the `/health/live` body carries only the three SchemaOutcome
  strings plus a static status literal. Both are covered by explicit no-secret tests.
- The watchdog resolves `IProcessorLivenessState` + `TimeProvider` at check time via the outer provider
  rather than capturing at registration, avoiding the captive-dependency pitfall.
- The fire-and-forget SREM is correctly absent-only, never awaited, and proven to fire exactly once
  without pruning the present member.
- DI registration of the `HealthCheckDescriptor` is correct, transitively reaches the embedded listener,
  and the "live" tag is picked up by the unchanged predicate. The two-step hosted-service teardown in the
  test fixture correctly drops both the wrapper and the concrete singleton.

Two warnings concern (1) a staleness boundary inconsistency between the gate and the watchdog that
contradicts the "identical math" claims in the docstrings, and (2) the 422-vs-500 split's reliance on
`JsonException` being the only non-Redis exception deserialization can produce. Info items cover minor
robustness and consistency notes.

## Warnings

### WR-01: Staleness boundary comparison differs between gate and watchdog despite "identical math" claims

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs:63`
and `src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs:63`

**Issue:** The two freshness checks use different boundary operators, yet both docstrings assert the
math is identical ("identical math to the orchestration-start gate", "D-03: same clock discipline as
writer + gate", and the validator's "expired freshness window => stale").

- Gate: `if (entry.Timestamp.AddSeconds(entry.Interval * 2) <= now) { stale++; }` — stale when the
  deadline is reached or passed; an entry is fresh only when `deadline > now`.
- Watchdog: `if (now > current.Timestamp.AddSeconds(current.Interval * 2))` — stale only when `now`
  is strictly past the deadline; an entry exactly at the deadline (`now == deadline`) is still Healthy.

At the exact boundary instant (`now == Timestamp + Interval*2`) the gate treats the replica as stale
while the watchdog treats the same record as fresh. This is a one-tick edge case (unlikely to fire in
practice given sub-second clock resolution and a several-second interval), so it is a Warning rather than
a bug that will routinely manifest. The risk is future drift: a maintainer trusting the "identical math"
comments could refactor one side and silently change the asymmetry. The writer's TTL uses
`max(entry.Interval * 2, TtlSeconds)`, so a key may also survive in Redis slightly past the gate's stale
deadline, but the gate independently re-derives staleness from the entry timestamp, so this does not by
itself create an admit/deny disagreement beyond the boundary tick.

**Fix:** Pick one boundary convention and apply it on both sides, then make the docstrings literally
match. For example, standardize on "fresh iff `deadline > now`":

```csharp
// watchdog — align with the gate's <= boundary
if (now >= current.Timestamp.AddSeconds(current.Interval * 2))
{
    return Task.FromResult(HealthCheckResult.Unhealthy("liveness loop stale", data: data));
}
```

Or, if the watchdog's strict-`>` convention is the intended one, change the gate to
`AddSeconds(entry.Interval * 2) < now`. Either way the "identical math" comment should reference a
single shared helper or constant so the two cannot drift.

### WR-02: 422-vs-500 split assumes deserialization can only throw `JsonException`

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs:59-60`

**Issue:** The catch is `catch (JsonException)`. The surrounding contract (validator comment lines 39-45)
states that "None of them may escape as a JsonException/NRE — only a genuine transport RedisException ...
propagates." However `JsonSerializer.Deserialize<T>` can throw exceptions other than `JsonException` for
malformed external input — notably `NotSupportedException` (e.g. an unsupported type-conversion path) and
`ArgumentNullException` is excluded here only because `IsNullOrEmpty` is pre-checked. Because the
per-instance value is EXTERNAL self-registered data the validator explicitly does not trust, any such
non-`JsonException` would escape this catch and, not being a `RedisException`, would propagate past the
caller's `redisOp` catch straight to the 500 fallback handler. That violates the stated invariant that
every deterministic data state maps to the 422 gate. For the current `ProcessorLivenessEntry` shape
(primitive + string fields) this is low-probability, but the validator is deliberately written to be
defensive against untrusted data, so the gap is worth closing.

**Fix:** Broaden the catch to the documented "any deserialization failure => malformed, never a 500"
intent while still letting a genuine transport fault through (the GET that produced `raw` already
succeeded by this point, so no `RedisException` originates inside the try):

```csharp
try { entry = JsonSerializer.Deserialize<ProcessorLivenessEntry>(raw!); }
catch (Exception ex) when (ex is JsonException or NotSupportedException) { malformed++; continue; }
```

## Info

### IN-01: `entry.Status` comparison is case-sensitive against an untrusted external value

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs:62`

**Issue:** `entry.Status != LivenessStatus.Healthy` is an ordinal, case-sensitive string compare against
externally self-registered data. A replica that wrote `"healthy"` (wrong casing) would be counted as
`unhealthy` and denied. This is correct behavior given the writer is the single source of truth and
always emits the `LivenessStatus.Healthy` const, so in-system this can never mismatch. Flagging only
because the value is treated as untrusted elsewhere in the same loop (malformed JSON is tolerated) while
the status string is matched exactly. No change required if the writer remains the sole producer.

**Fix:** None required. If defensiveness is desired for parity with the malformed-tolerance stance,
consider `string.Equals(entry.Status, LivenessStatus.Healthy, StringComparison.Ordinal)` to make the
intentional case-sensitivity explicit and self-documenting.

### IN-02: Per-replica GETs are issued sequentially (one round-trip per member)

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs:46-65`

**Issue:** The loop awaits each `StringGetAsync` before issuing the next, producing N+1 sequential Redis
round-trips per processor (1 SMEMBERS + 1 GET per member). The first-qualifier-wins short-circuit limits
this in the common healthy case, but a processor with many stale/absent replicas where none qualifies
walks the full set serially. This is a latency/throughput concern, which is explicitly out of scope for
v1 review, and the short-circuit makes the happy path fine. Noted for awareness only.

**Fix:** None for v1. If this gate ever sits on a hot path, a batched `StringGetAsync(RedisKey[])` /
pipeline could collapse the GETs, but that complicates the first-qualifier short-circuit and is not
warranted now.

### IN-03: `RedisProjectionKeys.Step` omits the `:D` format specifier used elsewhere

**File:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:37`

**Issue:** `Step` interpolates `$"{Prefix}{workflowId}:{stepId}"` with bare GUIDs, while `Root`,
`PerInstance`, `InstanceIndex`, `ExecutionData`, and `MessageIndex` all use the explicit `:D` specifier.
The class doc notes the `:D` and bare interpolation are byte-identical, so this is currently harmless.
It is an internal consistency nit, not a correctness defect — but the doc's stated rationale for adding
`:D` ("makes this explicit ... a future GUID-format change cannot silently desynchronize") applies
equally to `Step`, which is the one builder left implicit. This builder is outside the phase's liveness
reshape but lives in a file the phase edited.

**Fix:** For consistency apply the same explicit specifier: `$"{Prefix}{workflowId:D}:{stepId:D}"`.

### IN-04: `EmbeddedHealthEndpointService` re-declares `var captured = d` for a foreach variable that is no longer a closure hazard

**File:** `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs:86-87`

**Issue:** `var captured = d;` copies the foreach iteration variable before `captured.Factory(_outer)`.
Since C# 5 the `foreach` iteration variable is per-iteration scoped, and here `Factory` is invoked
eagerly inside the same iteration (not deferred into a captured lambda), so the defensive copy is
unnecessary dead-ish code. It is harmless and arguably documents intent, but it can mislead a reader into
thinking a deferred closure exists. The factory IS stored long-lived by `AddCheck`, but it is
`captured.Factory` already evaluated to an `IHealthCheck` instance, not a closure over the loop variable.

**Fix:** Optional simplification — inline `d` directly:

```csharp
foreach (var d in _outer.GetServices<HealthCheckDescriptor>())
{
    hc.AddCheck(d.Name, d.Factory(_outer), tags: d.Tags);
}
```

---

_Reviewed: 2026-06-13_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
