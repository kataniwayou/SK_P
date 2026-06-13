---
phase: 61-1-healthy-orchestration-start-gate-self-watchdog-probe
fixed_at: 2026-06-13T00:00:00Z
review_path: .planning/phases/61-1-healthy-orchestration-start-gate-self-watchdog-probe/61-REVIEW.md
iteration: 1
findings_in_scope: 6
fixed: 5
skipped: 1
status: partial
---

# Phase 61: Code Review Fix Report

**Fixed at:** 2026-06-13
**Source review:** .planning/phases/61-1-healthy-orchestration-start-gate-self-watchdog-probe/61-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 6 (fix_scope=all — Critical + Warning + Info)
- Fixed: 5
- Skipped: 1

Each fix was committed atomically with a `fix(61):` prefix after a clean
`dotnet build SK_P.sln -c Debug` (0 warnings / 0 errors, warnaserror enforced).

## Fixed Issues

### WR-01: Staleness boundary comparison differs between gate and watchdog despite "identical math" claims

**Files modified:** `src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs`
**Commit:** d19f77d
**Status:** fixed: requires human verification (boundary/logic change)
**Applied fix:** Standardized on the gate's convention "fresh iff `deadline > now`".
Changed the watchdog comparison from `now > deadline` to `now >= deadline` so the
watchdog and the gate (`deadline <= now => stale`) agree at the exact boundary
instant (`now == Timestamp + Interval*2` is now stale on BOTH sides). Updated the
watchdog XML docstring (the `<item>` bullet) to literally state the `>=` boundary
and reference the gate's `<= now` form, making the "identical math" claim true.
The gate side (`ProcessorLivenessValidator.cs:63`) was already correct and was left
unchanged. No existing test asserts exact-boundary behavior
(`LivenessWatchdogHealthCheckTests.cs` uses `-1` past/before the boundary), so no
test required adjustment. Flagged for human verification because this alters a
boundary-instant semantic.

### WR-02: 422-vs-500 split assumes deserialization can only throw `JsonException`

**Files modified:** `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs`
**Commit:** cc9d872
**Status:** fixed: requires human verification (422-vs-500 invariant change)
**Applied fix:** Broadened the per-instance deserialize catch from
`catch (JsonException)` to
`catch (Exception ex) when (ex is JsonException or NotSupportedException)`, counting
the replica as `malformed` (422 gate) instead of letting a `NotSupportedException`
from `JsonSerializer.Deserialize` escape to the 500 fallback. The exception filter
preserves the "every deterministic data state => 422" invariant while still letting
a genuine transport `RedisException` through (the GET that produced `raw` already
succeeded, so no RedisException originates inside the try). Added an explanatory
comment. Flagged for human verification because it touches the 422-vs-500 split
semantics — confirm no other exception type should also be admitted/rejected.

### IN-01: `entry.Status` comparison is case-sensitive against an untrusted external value

**Files modified:** `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs`
**Commit:** a1aa1ae
**Applied fix:** Replaced `entry.Status != LivenessStatus.Healthy` with
`!string.Equals(entry.Status, LivenessStatus.Healthy, StringComparison.Ordinal)` to
make the intentional case-sensitivity explicit and self-documenting. Behavior is
unchanged (both are ordinal case-sensitive); the writer remains the sole producer of
the `LivenessStatus.Healthy` const, so no in-system mismatch can occur.

### IN-03: `L2ProjectionKeys.Step` omits the `:D` format specifier used elsewhere

**Files modified:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs`
**Commit:** d9f9f66
**Applied fix:** Changed `$"{Prefix}{workflowId}:{stepId}"` to
`$"{Prefix}{workflowId:D}:{stepId:D}"` for internal consistency with `Root`,
`PerInstance`, `InstanceIndex`, `ExecutionData`, and `MessageIndex`. Byte-identical
output (default `Guid.ToString()` already renders "D"); the explicit specifier guards
against a future GUID-format change silently desynchronizing this one builder.

### IN-04: `EmbeddedHealthEndpointService` re-declares `var captured = d` for a foreach variable

**Files modified:** `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs`
**Commit:** 6a0daf2
**Applied fix:** Removed the unnecessary `var captured = d;` copy and inlined `d`
directly in the `hc.AddCheck(d.Name, d.Factory(_outer), tags: d.Tags)` call. Since
C# 5 the foreach iteration variable is per-iteration scoped and `Factory` is invoked
eagerly (not deferred into a captured lambda), so the defensive copy was dead-ish
code. Added a comment documenting why no copy is needed.

## Skipped Issues

### IN-02: Per-replica GETs are issued sequentially (one round-trip per member)

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs:46-65`
**Reason:** skipped: explicitly out of scope and would change architecture. The
review itself states "None for v1" and that a batched `StringGetAsync(RedisKey[])` /
pipeline "complicates the first-qualifier short-circuit and is not warranted now".
The first-qualifier-wins short-circuit already bounds the happy path. Converting to a
batched/pipelined read would alter the validator's control flow (the short-circuit
that admits on the first healthy+fresh replica) and is a performance/architecture
change, not a quality fix — out of scope per the fix directive.
**Original issue:** The loop awaits each `StringGetAsync` before issuing the next,
producing N+1 sequential Redis round-trips per processor; a processor with many
non-qualifying replicas walks the full set serially.

---

_Fixed: 2026-06-13_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
