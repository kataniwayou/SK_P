---
phase: 15-l2-redis-projection-write-stop-existence-check
reviewed: 2026-05-29T00:00:00Z
depth: standard
files_reviewed: 29
files_reviewed_list:
  - src/BaseApi.Core/Configuration/RedisProjectionOptions.cs
  - src/BaseApi.Core/Exceptions/Handlers/FallbackExceptionHandler.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationStopException.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/IRedisL2Cleanup.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/IRedisProjectionWriter.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/LivenessProjection.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/StepProjection.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/WorkflowRootProjection.cs
  - src/BaseApi.Service/appsettings.Development.json
  - src/BaseApi.Service/appsettings.json
  - tests/BaseApi.Tests/Features/Orchestration/Projection/ProjectionRecordRoundTripTests.cs
  - tests/BaseApi.Tests/Features/Orchestration/Projection/RedisProjectionKeysTests.cs
  - tests/BaseApi.Tests/Features/Orchestration/RedisDisciplineGuardFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/RedisProjectionWriterFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/StartCleanupFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/StartLoopFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/StopCleanupFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/StopGateFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/ValidationOrderFacts.cs
  - tests/BaseApi.Tests/Observability/OrchestrationLogsE2ETests.cs
findings:
  critical: 0
  warning: 3
  warning_resolved: 1
  info: 4
  total: 7
  open: 6
status: issues_found
---

# Phase 15: Code Review Report

**Reviewed:** 2026-05-29T00:00:00Z
**Depth:** standard
**Files Reviewed:** 29
**Status:** issues_found

## Summary

Phase 15 implements the L2 (Redis) projection write engine, the shared tolerant cleanup
routine, and the redesigned Redis-EXISTS Stop gate. The code is well-structured, heavily
documented against its design constraints (D-xx / OBSERV-REDIS-xx / L2-PROJECT-xx), and
backed by a thorough integration-test suite that covers the happy paths, the 422 gate, the
500 fault path, processor-key retention, cycle termination, dangling-step tolerance, and the
no-key-enumeration discipline guard.

No critical (security / data-loss / crash) issues were found. The information-disclosure
guard in `FallbackExceptionHandler` correctly surfaces only a fixed op-name literal, never
the connection string or stack — and `StopGateFacts`/`StartLoopFacts` assert that explicitly.

The findings below are correctness/consistency concerns. The most actionable is a missing
`redisOp` tag on the Stop cleanup loop (an asymmetry with the Start pre-clean) and the
uniform non-propagation of `CancellationToken` into every Redis call across the three new
runtime types.

## Warnings

### WR-01: Stop cleanup loop does not tag `redisOp` on a Redis fault (asymmetry with Start) — ✅ RESOLVED (commit 44dd29a)

**Resolution (2026-05-29):** Wrapped the Stop post-gate cleanup loop in the same
`try/catch (RedisException)` guard, tagging the Stop path's stable op name `"KeyExistsAsync"`
(consistent with the Start pre-clean single-stable-op-name convention, IN-04). Locked by
`StopGateFacts.Stop_RedisDown_OnPostGateCleanup_500_KeyExistsAsync` (hand-rolled throwing
`IRedisL2Cleanup` stub). Full suite 227/227 GREEN.

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs:203-206`
**Issue:** In `StartAsync`, the pre-clean call to `_cleanup.StopCleanupAsync` is wrapped in a
`try/catch (RedisException)` that tags `Data["redisOp"] = "UpsertAsync"` (lines 118-126). In
`StopAsync`, the post-gate cleanup loop calls the same `_cleanup.StopCleanupAsync` with NO
try/catch. The EXISTS batch is tagged `"KeyExistsAsync"` (lines 189-193), but if a Redis
fault occurs during the cleanup deletes (after the gate passed), the resulting
`RedisException` reaches `FallbackExceptionHandler` with no `redisOp` in `Data`, so the 500
body omits the op name. This breaks the OBSERV-REDIS-03 contract ("a Redis fault on either
path is caught, tagged with the offending op name") for the Stop delete sub-path, and is an
unintended asymmetry with Start's pre-clean which uses the identical routine.
**Fix:** Wrap the cleanup loop in the same catch, tagging a stable op name (e.g.
`"KeyDeleteAsync"`, matching the actual faulting op, or reuse `"KeyExistsAsync"` if a single
stable Stop op name is intended — mirror whatever Start does):
```csharp
foreach (var workflowId in workflowIds)
{
    try
    {
        await _cleanup.StopCleanupAsync(workflowId, ct);
    }
    catch (RedisException ex)
    {
        ex.Data["redisOp"] = "KeyDeleteAsync";
        throw;
    }
}
```

### WR-02: `CancellationToken` accepted but never propagated to any Redis call

**File:** `src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs:35-79`,
`src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs:52-130`,
`src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs:163-207`
**Issue:** All three new runtime methods accept a `CancellationToken ct` but never observe
it. `StopCleanupAsync` issues `StringGetAsync`, `CreateBatch`/`KeyDeleteAsync` without it;
`UpsertAsync` issues `StringSetAsync` calls without it; `StopAsync` issues `KeyExistsAsync`
without it. StackExchange.Redis does not take a `CancellationToken` on these overloads, so
this is not a compile error — but the token is silently dropped, meaning a client
disconnect / shutdown cannot abort an in-flight BFS walk over a large step graph (the
`while` loop in `StopCleanupAsync` awaits one `StringGetAsync` per step with no
`ct.ThrowIfCancellationRequested()`). A long or cyclic-but-large graph can keep running after
the request is abandoned.
**Fix:** Add a cheap cooperative check at each wave/iteration boundary so a cancelled request
stops promptly, e.g. in the `StopCleanupAsync` `while` loop and the `UpsertAsync`/`StopAsync`
loops:
```csharp
while (currentWave.Count > 0)
{
    ct.ThrowIfCancellationRequested();
    ...
}
```
At minimum, document on `IRedisL2Cleanup.StopCleanupAsync` / `IRedisProjectionWriter.UpsertAsync`
that `ct` is currently advisory (SE.Redis sync-over-pipeline has no token overload) so the
unused parameter is not mistaken for an oversight.

### WR-03: `Task.WhenAll` result harvested via `.Task.Result` instead of awaited results

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs:183-187`
**Issue:** The EXISTS batch awaits `Task.WhenAll(checks.Select(c => c.Task))` and then reads
`c.Task.Result` to build the `missing` list. Reading `.Result` is safe here because the tasks
are already completed by the awaited `WhenAll`, but it is a fragile pattern: if the
collection were ever refactored such that a task is faulted/cancelled but not surfaced by the
`WhenAll` projection (e.g. a future change to which tasks are awaited), `.Result` would
re-wrap the exception in an `AggregateException` rather than the unwrapped `RedisException`
the `catch (RedisException)` expects, silently bypassing the `redisOp` tag. The faulted-task
path is already inside the `try`, so today it is correct, but the `.Result` read is an
avoidable code smell.
**Fix:** Await each task's value directly to keep exception unwrapping consistent:
```csharp
var results = await Task.WhenAll(
    workflowIds.Select(async id =>
        (Id: id, Exists: await db.KeyExistsAsync(RedisProjectionKeys.Root(_keyPrefix, id)))));
missing = results.Where(r => !r.Exists).Select(r => r.Id).ToList();
```

## Info

### IN-01: `Gate` discriminator doc-comment omits the new `"stopMissingRoots"` value

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs:23`
**Issue:** The XML doc on `Gate` enumerates `"cycle" | "missingStep" | "schemaEdge" |
"payloadConfigSchema"`, but the Phase 15 partial (`OrchestrationStopException.cs:29`) adds a
fifth discriminator `"stopMissingRoots"`. The doc is now stale and could mislead a consumer
switching on the gate value.
**Fix:** Append `| "stopMissingRoots"` to the doc-comment list on line 23 (and the class-level
"ALL four Phase 14 gates" summary on line 5, which is now five gates across the two files).

### IN-02: `MissingRootsOffending` record uses lowercase `missing` property name

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationStopException.cs:36`
**Issue:** `public sealed record MissingRootsOffending(IReadOnlyList<Guid> missing)` declares a
parameter named `missing` (lowercase), which becomes a public property `missing` — inconsistent
with C# PascalCase convention and with the sibling offending records in
`OrchestrationValidationException.cs` (`CycleOffending(IReadOnlyList<Guid> stepChain)`,
`MissingStepOffending(Guid parentStepId, ...)`) which all use PascalCase. When serialized into
the 422 `errors.offending` envelope this also produces a `missing` JSON field rather than the
camelCase the other gates would yield from PascalCase + the default policy. Confirm the wire
shape is intended; if not, rename to `Missing`.
**Fix:** `public sealed record MissingRootsOffending(IReadOnlyList<Guid> Missing);` (verify no
test asserts the lowercase JSON key first).

### IN-03: Class-level summary still says "ALL four Phase 14 orchestration validation gates"

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs:5`
**Issue:** Same root cause as IN-01 — the type now backs five gates (the four Phase 14 gates
plus the Phase 15 Stop `stopMissingRoots` gate added via the partial). Documentation drift only;
no behavioral impact.
**Fix:** Update the count to five and note the Stop gate lives in the `OrchestrationStopException.cs`
partial.

### IN-04: Start pre-clean tags a faulting cleanup op as `"UpsertAsync"` (semantically misleading)

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs:122-126`
**Issue:** The Start pre-clean wraps `_cleanup.StopCleanupAsync` and, on a `RedisException`,
tags `Data["redisOp"] = "UpsertAsync"` even though the faulting op is a cleanup GET/DELETE, not
an upsert. The inline comment explicitly justifies this as a deliberate "single stable op name
for the whole Start write path" (OBSERV-REDIS-03), and `StartLoopFacts` asserts `"UpsertAsync"`,
so this is by design — flagged only so a future reader does not mistake it for a copy-paste bug.
No change required unless the contract is revisited (see WR-01, which should then align with
whatever convention is chosen).
**Fix:** None required; documented design choice. If WR-01 is addressed, keep the two op-name
conventions consistent.

---

_Reviewed: 2026-05-29T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
