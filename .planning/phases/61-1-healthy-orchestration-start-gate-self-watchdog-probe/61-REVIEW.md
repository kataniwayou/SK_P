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
  warning: 0
  info: 1
  total: 1
status: issues_found
---

# Phase 61: Code Review Report (Re-Review)

**Reviewed:** 2026-06-13
**Depth:** standard
**Files Reviewed:** 12
**Status:** issues_found (1 info only)

## Summary

This is a re-review after fixes were applied for the prior round (WR-01, WR-02, IN-01, IN-03, IN-04; IN-02 intentionally skipped). All six prior findings are confirmed resolved, and the fixes did not introduce any new correctness, security, or quality regressions. The boundary `>=` change, the broadened exception filter, and the new tests were checked against their call sites and contracts.

The single remaining item is a pre-existing INFO observation (not introduced by the fixes) about a malformed-shape edge case in the validator that is correctly counted but worth noting for completeness.

### Prior-finding resolution

- **WR-01 (boundary drift) — RESOLVED.** Validator stale predicate (`ProcessorLivenessValidator.cs:68` `entry.Timestamp.AddSeconds(entry.Interval * 2) <= now`) and watchdog stale predicate (`LivenessWatchdogHealthCheck.cs:67` `now >= current.Timestamp.AddSeconds(current.Interval * 2)`) are algebraically identical (`deadline <= now` ⟺ `now >= deadline`). Both classify the exact-boundary instant as stale. Confirmed by paired exact-boundary + one-tick-before tests on both sides (`ProcessorLivenessGateUnitTests.cs:163,182`; `LivenessWatchdogHealthCheckTests.cs:85,101`). The watchdog XML doc (`LivenessWatchdogHealthCheck.cs:18-21`) accurately cross-references the gate's `deadline <= now` form.

- **WR-02 (NotSupportedException escape) — RESOLVED, filter is correctly narrow.** The catch is `when (ex is JsonException or NotSupportedException)` (`ProcessorLivenessValidator.cs:63`). The producing `StringGetAsync` is OUTSIDE the `try` (`:49`), so a transport `RedisException` cannot enter the filter and propagates untouched to the caller's `redisOp` catch → 500 (verified at `OrchestrationService.cs:197-201`). The new transport-fault test (`ProcessorLivenessGateUnitTests.cs:197`) pins this. The filter is not over-broad: `JsonSerializer.Deserialize<T>(string)` realistically throws only `JsonException`/`NotSupportedException` on this external-data path; `ArgumentNullException` is precluded by the prior `raw.IsNullOrEmpty` guard (`:50`). A null/empty-shape result is separately handled by the `entry?.Summary is null` malformed check (`:64`).

- **IN-01 (case-sensitive compare) — RESOLVED.** Now explicit `string.Equals(entry.Status, LivenessStatus.Healthy, StringComparison.Ordinal)` (`ProcessorLivenessValidator.cs:67`), self-documenting the intentional ordinal/case-sensitive contract with the sole writer.

- **IN-03 (missing `:D` on Step) — RESOLVED.** `L2ProjectionKeys.Step` now renders `$"{Prefix}{workflowId:D}:{stepId:D}"` (`L2ProjectionKeys.cs:37`), consistent with `Root`, `PerInstance`, `InstanceIndex`, `ExecutionData`, `MessageIndex`. Byte-identical to the prior bare interpolation, so no key-shape regression.

- **IN-04 (unnecessary captured copy) — RESOLVED.** `EmbeddedHealthEndpointService.cs:88` invokes `d.Factory(_outer)` eagerly inside the loop with no deferred lambda capture; the C# 5+ per-iteration `foreach` scoping makes a defensive copy unnecessary. The descriptor seam remains generic (BaseConsole.Core holds no reference to the concrete check type).

- **IN-02 (sequential per-replica GETs) — intentionally skipped.** Out of v1 scope (performance), and the first-qualifier short-circuit limits the cost. No action.

### Security-sensitive surfaces re-checked

- **422-vs-500 split.** Intact. Deterministic external-data states (absent/unhealthy/stale/malformed) map to the counted 422 gate; only a genuine `RedisException` (raised outside the try) reaches the 500 path. Confirmed end-to-end against `OrchestrationService.cs:193-201`.
- **Staleness/freshness time comparisons.** Single clock discipline (`TimeProvider.GetUtcNow().UtcDateTime`) on both sides; boundary semantics aligned and pinned by tests.
- **Fire-and-forget SREM safety.** Absent-only, never awaited, never faults the verdict (`ProcessorLivenessValidator.cs:54`); the unit test asserts exactly-once SREM of the absent member and never of present members (`ProcessorLivenessGateUnitTests.cs:227-231`).
- **Info-disclosure (422 reason + /health/live body).** The 422 reason is counts-only — no instanceIds, connection strings, or stack traces (`ProcessorLivenessValidator.cs:75-76`). The watchdog Data carries only the three SchemaOutcome strings; descriptions are static literals (`LivenessWatchdogHealthCheck.cs:57-62`). Both are guarded by negative-assertion tests (`LivenessWatchdogHealthCheckTests.cs:121-131`; `ProcessorHealthLiveTests.cs:69-83`).
- **DI registration correctness.** The `liveness-watchdog` descriptor is registered as a singleton with a factory that bridges the OUTER provider and resolves `IProcessorLivenessState` + `TimeProvider` at check time, never captured at registration (`BaseProcessorServiceCollectionExtensions.cs:152-155`, `LivenessWatchdogHealthCheck.cs:48-49`). The "live" tag means the unchanged `/health/live` predicate picks it up; Orchestrator/Keeper register no descriptor so their liveness stays self-only. The embedded listener folds outer descriptors via `GetServices<HealthCheckDescriptor>()` (`EmbeddedHealthEndpointService.cs:84-89`).

## Info

### IN-01: Validator treats a present-but-empty `summary` (null inner schema strings) as a qualifying replica when status is "Healthy"

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs:64-67`
**Issue:** The malformed guard is `entry?.Summary is null`. A wire value such as `{"timestamp":...,"interval":...,"status":"Healthy","summary":{}}` deserializes to a non-null `LivenessSummary` whose `InputSchema`/`OutputSchema`/`ConfigSchema` are null (the record fields are declared non-nullable `string`, but STJ populates them as null for absent JSON members). Such an entry passes the `Summary is null` check and is then evaluated purely on `Status`, so a "Healthy" status with an empty summary would admit. This is within the threat model's tolerance (the sole trusted writer always emits a full summary via `Create`), and it is NOT a regression introduced by this round's fixes — it predates them. Noting it only to document the malformed-shape boundary.
**Fix (optional, defense-in-depth):** If stricter external-data hardening is wanted later, extend the malformed check to also reject a summary with any null schema field:
```csharp
if (entry?.Summary is not { InputSchema: not null, OutputSchema: not null, ConfigSchema: not null })
{
    malformed++; continue;
}
```
No action required for this phase.

---

_Reviewed: 2026-06-13_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
