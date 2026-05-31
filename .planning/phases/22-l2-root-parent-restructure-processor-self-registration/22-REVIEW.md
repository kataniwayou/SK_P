---
phase: 22-l2-root-parent-restructure-processor-self-registration
reviewed: 2026-05-31T10:18:40Z
depth: standard
files_reviewed: 24
files_reviewed_list:
  - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
  - src/BaseApi.Core/Configuration/RedisProjectionOptions.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs
  - src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs
  - src/Orchestrator/Consumers/StartOrchestrationConsumer.cs
  - src/Orchestrator/Consumers/StopOrchestrationConsumer.cs
  - src/Orchestrator/Messaging/OrchestratorL2Keys.cs
  - src/Orchestrator/Program.cs
  - src/Orchestrator/appsettings.json
  - src/BaseApi.Service/appsettings.json
  - tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs
  - tests/BaseApi.Tests/Composition/RedisFixture.cs
  - tests/BaseApi.Tests/Features/Orchestration/ProcessorLivenessFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/HappyPathE2EFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/RedisProjectionWriterFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/StopCleanupFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/GateNoWriteFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs
findings:
  critical: 0
  warning: 1
  info: 4
  total: 5
status: issues_found
---

# Phase 22: Code Review Report

**Reviewed:** 2026-05-31T10:18:40Z
**Depth:** standard
**Files Reviewed:** 24
**Status:** issues_found

## Summary

Reviewed the Phase 22 L2 restructure: the compile-time key prefix collapse
(`L2ProjectionKeys.Prefix = "skp:"`), the parent-index SET (SADD on Start / SREM on
Stop), the removal of the writer's per-processor key write, and the new async
`ProcessorLivenessValidator` Start gate.

The core mechanics are sound and well-tested:

- **Key correctness** is consistent across writer, cleanup, reader, and tests. `Root`
  uses `:D`, `Step`/`Processor` use bare interpolation (byte-identical to `:D` for the
  default `Guid.ToString()`), and `L2ProjectionKeysTests` pins the exact literals.
- **SADD/SREM idempotency** holds: both sides render the member as `ToString("D")`, so the
  added and removed strings match. SREM is hoisted above the absent-root early-return in
  `RedisL2Cleanup`, making GC idempotent; SADD into a SET is a natural no-op on re-Start.
- **Liveness staleness math** is unit-correct: `Timestamp.AddSeconds(Interval * 2)` and the
  `deadline <= now` stale test use the same `TimeProvider` clock as the writer, and the
  facts (`interval=300` live, `interval=0` + past timestamp stale) confirm the SECONDS
  interpretation.
- **Error mapping** is consistent: liveness failures throw `OrchestrationValidationException`
  (→ 422) and propagate uncaught past the `RedisException`→`redisOp` catch, while genuine
  Redis faults on the liveness GETs are tagged and surfaced as 500 — matching the documented
  OBSERV-REDIS-03 contract.

One Warning concerns null-handling in the new validator against EXTERNAL self-registered
data it does not own. The Info items are dead config/stale docs left behind by the
PROC-NOCREATE-01 removal, plus a couple of consistency notes.

## Warnings

### WR-01: `ProcessorLivenessValidator` NREs (→ 500) on a malformed external processor entry with null/absent `liveness`

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs:37-40`
**Issue:** The validator deserializes the externally self-registered `skp:{procId}` entry and
immediately dereferences the nested liveness sub-document:

```csharp
var projection = JsonSerializer.Deserialize<ProcessorProjection>(raw!)!;
var liveness = projection.Liveness;
var deadline = liveness.Timestamp.AddSeconds(liveness.Interval * 2);
```

`ProcessorProjection.Liveness` is a non-nullable positional record parameter, but
System.Text.Json does NOT enforce that at runtime — `RespectNullableAnnotations` /
`RespectRequiredConstructorParameters` are not configured anywhere in the codebase (grep
returned no matches). An external registrant writing `{}`, `{"liveness":null}`, or any entry
omitting the `liveness` member therefore deserializes to a `ProcessorProjection` with
`Liveness == null`, and `liveness.Timestamp` throws `NullReferenceException`. That NRE is NOT
a `RedisException`, so it bypasses the `redisOp` catch in `OrchestrationService.StartAsync`
and falls through to the fallback handler as a **500** — the wrong status for a processor that
is effectively not-live. This is exactly the class of malformed input the validator should
treat as "stale"/"absent" (a 422), since by design it consumes data it does not control. The
same risk applies if `raw` is non-null but not valid JSON (`JsonException` → 500); a clean
422 with reason such as "malformed" would be the consistent contract.

**Fix:** Guard the deserialized shape and map a bad/empty registration to the existing 422 gate:

```csharp
ProcessorProjection? projection;
try
{
    projection = JsonSerializer.Deserialize<ProcessorProjection>(raw!);
}
catch (JsonException)
{
    throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "malformed");
}

if (projection?.Liveness is not { } liveness)
    throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "malformed");

var deadline = liveness.Timestamp.AddSeconds(liveness.Interval * 2);
if (deadline <= now)
    throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "stale");
```

(`RedisException` from the GET still propagates to the `redisOp` catch and stays a 500 —
only the parse/shape failure becomes a 422.)

## Info

### IN-01: `RedisProjectionOptions.ProcessorKeyTtlDays` is now dead config; its XML doc is stale

**File:** `src/BaseApi.Core/Configuration/RedisProjectionOptions.cs:16-18`
**Issue:** PROC-NOCREATE-01 removed the writer's per-processor TTL'd write loop, so nothing
reads `ProcessorKeyTtlDays` any more (grep across `src/BaseApi.Service` returns no reads;
`RedisProjectionWriter` injects `IOptions<RedisProjectionOptions>` but never touches a field).
The property's doc-comment still asserts "Refresh-on-write: every Start re-SETs processor keys
with this expiry," which is no longer true. The class-level summary also still lists
`ProcessorKeyTtlDays` as a bound field as if it were live. Dead config plus an actively
misleading comment.
**Fix:** Remove `ProcessorKeyTtlDays` (and the matching `Redis:ProcessorKeyTtlDays` key in
`src/BaseApi.Service/appsettings.json`), or, if it is being held for a near-term v3.4
scheduler that will re-introduce processor TTLs, mark it explicitly as unused/reserved and
correct the "every Start re-SETs" comment to past/future tense.

### IN-02: `RedisProjectionWriter` injects `IOptions<RedisProjectionOptions>` but reads nothing from it

**File:** `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs:40,51,56-124`
**Issue:** `_options` is constructor-validated (`?? throw`) and stored, but `UpsertAsync`
never reads `_options.ProcessorKeyTtlDays` nor `_options.Serialization` — the only former
consumer (the processor-key TTL write) was deleted. The dependency is now inert. Serialization
is plain default `JsonSerializer.Serialize(...)`, so `Serialization.JsonOptions` is also unused.
**Fix:** Drop the `IOptions<RedisProjectionOptions>` constructor parameter (and the `_options`
field) until a field is actually consumed, or wire `Serialization.JsonOptions` into a
`JsonSerializerOptions` if that was the intended consumer. Keeping an injected-but-unread option
invites a future reader to assume it is honored when it is not.

### IN-03: Stop post-gate delete faults are tagged `redisOp="KeyExistsAsync"` though the faulting op is a delete

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs:243-254`
**Issue:** The post-gate cleanup loop runs `StopCleanupAsync` (which issues SREM + GET +
KeyDelete), but a `RedisException` thrown there is tagged `ex.Data["redisOp"] = "KeyExistsAsync"`.
The inline comment states this is deliberate ("a single stable 'KeyExistsAsync' op regardless of
which Redis call faults first"), mirroring the Start side's stable `"UpsertAsync"` label. It is
intentional and the no-leak contract still holds, but a 500 body reporting `KeyExistsAsync` when
the failing operation was actually a delete is misleading for operators triaging the trace.
**Fix:** Consider a Stop-path label that reflects the cleanup stage (e.g. `"StopCleanupAsync"`)
rather than reusing the gate's `"KeyExistsAsync"`, or document the chosen convention in the 500
body's accompanying docs so on-call readers know the op name is a stable stage label, not the
literal faulting command. No behavioral change required.

### IN-04: `interval * 2` is an unchecked `int` multiply (theoretical overflow)

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs:40`
**Issue:** `liveness.Interval` is an `int` (seconds). `liveness.Interval * 2` is an unchecked
`int` multiplication; an externally-supplied interval above ~1.07e9 seconds would overflow to a
negative value, making `AddSeconds` move the deadline into the past and a clearly-live processor
read as "stale". Practically unreachable (no legitimate registrant sets a >34-year heartbeat
interval) and the failure mode is fail-safe (over-rejects, never over-accepts), so this is a note,
not a warning.
**Fix:** If hardening against hostile external registrations, widen the arithmetic
(`(long)liveness.Interval * 2`, `AddSeconds((double)...)`) or clamp `Interval` to a sane maximum
when deserializing the external entry.

---

_Reviewed: 2026-05-31T10:18:40Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
