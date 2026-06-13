---
phase: 61-1-healthy-orchestration-start-gate-self-watchdog-probe
verified: 2026-06-13T15:00:00Z
status: passed
score: 5/5 must-haves verified
overrides_applied: 0
---

# Phase 61: Healthy Orchestration-Start Gate + Self-Watchdog Probe Verification Report

**Phase Goal:** Two reader-side consumers of the reshaped per-instance liveness keyspace land: (a) the WebAPI orchestration-start gate discovers processor replicas via `SMEMBERS skp:proc:{processorId}`, GETs each per-instance key, and admits iff >=1 replica is healthy and non-stale; (b) the self-watchdog liveness probe reads the in-memory L1 record and reports unhealthy when the L1 timestamp is stale beyond active-interval x2, returning the per-schema summary in its body.
**Verified:** 2026-06-13T15:00:00Z
**Status:** passed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | The orchestration-start validator discovers replicas by `SMEMBERS skp:proc:{processorId}` and reads each per-instance key with no prior knowledge of instanceIds. | VERIFIED | `ProcessorLivenessValidator.cs:35` calls `db.SetMembersAsync(L2ProjectionKeys.InstanceIndex(proc.Id))` then loops members calling `db.StringGetAsync(L2ProjectionKeys.PerInstance(proc.Id, instanceId))`. No hardcoded instanceId list. |
| 2 | A processor passes the gate iff >=1 replica is present AND status=healthy AND non-stale; a present-but-unhealthy replica and a stale replica each fail that replica (a single healthy-and-fresh replica admits even when siblings are unhealthy/stale). | VERIFIED | `ProcessorLivenessValidator.cs:62-64`: unhealthy increments `unhealthy++`, stale increments `stale++`, first matching replica sets `qualified = true; break`. `ProcessorLivenessGateUnitTests.cs` cases `OneStale_Plus_OneHealthyFresh_Admits_FirstQualifierWins` and `AllUnhealthyOrStale_Throws_AggregateReason` prove both pass and fail paths. |
| 3 | When no replica satisfies the gate, orchestration start is blocked with 422 + RFC 7807; genuine Redis faults still surface as 500 (not 422); an absent/TTL-expired index member is skipped and lazily SREM'd. | VERIFIED | `ProcessorLivenessValidator.cs:67-73`: throws `OrchestrationValidationException.ProcessorNotLive(proc.Id, reason)` -> 422 RFC 7807. No `catch (RedisException)` in the validator (grep confirmed absent) — Redis faults propagate to the caller's existing 500 catch in `OrchestrationService.cs`. `ProcessorLivenessValidator.cs:54`: absent member fires `db.SetRemoveAsync(..., CommandFlags.FireAndForget)`. Unit test `OneAbsent_Plus_OneHealthy_Admits_And_SREMs_Absent_Once` asserts `Received(1).SetRemoveAsync(InstanceIndex, "inst-gone", FireAndForget)` and `DidNotReceive` for the live member. |
| 4 | The processor's liveness probe reads the in-memory L1 record and reports unhealthy when the L1 timestamp is stale beyond the active-interval x2 grace. | VERIFIED | `LivenessWatchdogHealthCheck.cs:48-68`: resolves `IProcessorLivenessState` from outer provider at check time; `current is null` -> Unhealthy; `now > current.Timestamp.AddSeconds(current.Interval * 2)` -> Unhealthy ("liveness loop stale"). `LivenessWatchdogHealthCheckTests.cs` cases `Null_L1_Reports_Unhealthy_LoopNotStarted` and `Stale_L1_Reports_Unhealthy_With_Summary_In_Data` confirm. `ProcessorHealthLiveNullTests.Live_Is_Unhealthy_When_L1_Null` and `ProcessorHealthLiveTests.Live_Is_Unhealthy_When_L1_Stale` prove the full end-to-end path through the real embedded Kestrel listener. |
| 5 | The probe returns the per-schema summary in its response body. | VERIFIED | `LivenessWatchdogHealthCheck.cs:55-60`: builds `data` dict with keys `inputSchema`, `outputSchema`, `configSchema` from `current.Summary`. `LivenessWatchdogHealthCheckTests.AssertSummaryDataPresent` asserts all three keys in `result.Data`. `ProcessorHealthLiveTests.Live_Is_Healthy_And_Carries_Summary_When_L1_Fresh` asserts `body` contains all three key names at the HTTP level. |

**Score:** 5/5 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` | SMEMBERS->GET-each >=1-healthy gate | VERIFIED | Contains `SetMembersAsync`, `L2ProjectionKeys.InstanceIndex`, `L2ProjectionKeys.PerInstance`, `CommandFlags.FireAndForget`, `LivenessStatus.Healthy` const (no literal), `no healthy replica` reason string, no `catch (RedisException)`, no `ProcessorProjection`. |
| `src/Messaging.Contracts/Projections/ProcessorProjection.cs` | DELETED (D-11) | VERIFIED | File confirmed absent (shell: DELETED). Legacy single-key contract removed. |
| `src/BaseConsole.Core/Health/HealthCheckDescriptor.cs` | Generic pluggable health-check seam (Name, Tags, Factory) | VERIFIED | Sealed record with `Func<IServiceProvider, IHealthCheck> Factory`. Zero `BaseProcessor` references in BaseConsole.Core (grep confirmed 0 matches). |
| `src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs` | Self-watchdog IHealthCheck reading L1 | VERIFIED | Contains `IProcessorLivenessState`, `GetRequiredService<IProcessorLivenessState>`, `liveness loop not started`, data keys `inputSchema`/`outputSchema`/`configSchema`. |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` | Watchdog descriptor registered | VERIFIED | Contains `new HealthCheckDescriptor(`, `new LivenessWatchdogHealthCheck(outer)`, `Tags: new[] { "live" }` at line 152-155. |
| `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs` | Folds outer descriptors into inner container | VERIFIED | Contains `GetServices<HealthCheckDescriptor>()` loop at line 84, `Predicate = c => c.Tags.Contains("live")` mapping unchanged at line 94. |
| `tests/BaseApi.Tests/Features/Orchestration/ProcessorLivenessGateUnitTests.cs` | Pure-unit gate test (NSubstitute IDatabase) | VERIFIED | Contains `[Trait("Phase", "61")]`, `SetMembersAsync`, `CommandFlags.FireAndForget`, `Received(1)`. 6 facts covering admit, first-qualifier-wins, empty index, malformed, absent SREM. |
| `tests/BaseApi.Tests/Features/Liveness/LivenessWatchdogHealthCheckTests.cs` | Pure-unit probe test (null/stale/fresh L1) | VERIFIED | Contains `[Trait("Phase", "61")]`, asserts null/stale/fresh statuses, summary data keys, no-secrets guard. |
| `tests/BaseApi.Tests/Console/ProcessorConsoleTestHostFixture.cs` | Generic-Host fixture composing AddBaseProcessor | VERIFIED | Contains `AddBaseProcessor`, `SeedLiveness` helper resolving `IProcessorLivenessState`. |
| `tests/BaseApi.Tests/Console/ProcessorHealthLiveTests.cs` | Processor /health/live integration facts | VERIFIED | Contains `[Trait("Phase", "61")]`, `/health/live`, asserts 503 for stale, 200+summary for fresh, no-secrets guard. Includes `ProcessorHealthLiveNullTests` (isolated null case, 503 for null L1). |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `ProcessorLivenessValidator.cs` | `L2ProjectionKeys.InstanceIndex / PerInstance` | `db.SetMembersAsync(InstanceIndex)` then `db.StringGetAsync(PerInstance)` | WIRED | Both calls present at lines 35 and 49. Pattern `SetMembersAsync(L2ProjectionKeys.InstanceIndex` confirmed. |
| `ProcessorLivenessValidator.cs` | `OrchestrationValidationException.ProcessorNotLive` | `throw` on no-qualifier with aggregate reason | WIRED | Line 72: `throw OrchestrationValidationException.ProcessorNotLive(proc.Id, reason)`. Pattern `ProcessorNotLive(proc.Id` confirmed. |
| `LivenessWatchdogHealthCheck.cs` | `IProcessorLivenessState.Current` | `_outer.GetRequiredService<IProcessorLivenessState>().Current` at check time | WIRED | Lines 46-48: resolves state, reads `state.Current`. Never captured at registration. |
| `EmbeddedHealthEndpointService.cs` | `HealthCheckDescriptor` collection | `_outer.GetServices<HealthCheckDescriptor>()` folded into `AddHealthChecks` | WIRED | Line 84: `foreach (var d in _outer.GetServices<HealthCheckDescriptor>())` with factory call at line 87. |
| `BaseProcessorServiceCollectionExtensions.cs` | `HealthCheckDescriptor` | `AddSingleton(new HealthCheckDescriptor(... Factory: outer => new LivenessWatchdogHealthCheck(outer)))` | WIRED | Lines 152-155 register the descriptor adjacent to `IProcessorLivenessState` singleton. |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `ProcessorLivenessValidator.cs` | `members` (RedisValue[]) | `db.SetMembersAsync(L2ProjectionKeys.InstanceIndex(proc.Id))` — real Redis SMEMBERS | Yes — returns live SET members from Redis | FLOWING |
| `LivenessWatchdogHealthCheck.cs` | `current` (ProcessorLivenessEntry?) | `_outer.GetRequiredService<IProcessorLivenessState>().Current` — in-memory singleton, updated by heartbeat/startup loops | Yes — L1 holder is a live volatile-ref singleton | FLOWING |
| `ProcessorHealthLiveTests.cs` | `/health/live` response body | `ProcessorConsoleTestHostFixture.SeedLiveness` -> `IProcessorLivenessState.Update()` -> `LivenessWatchdogHealthCheck.CheckHealthAsync` -> `HealthCheckResult.Data` -> UIResponseWriter | Yes — real entry seeded, real Kestrel response read | FLOWING |

### Behavioral Spot-Checks

Step 7b: SKIPPED for live-stack execution. The test suite runs hermetically; `[Trait("Category","RealStack")]` tests require a live Docker compose stack (Postgres, Redis, RabbitMQ, processor container) and are documented as out-of-scope for Phase 62. All hermetic tests (585 passing) confirm the functional behaviors. The SUMMARY.md documents specific command outputs confirming the gate unit tests (25 ProcessorLiveness) and watchdog unit tests (3 LivenessWatchdog) and integration tests (4 ProcessorHealthLive) are green.

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| GATE-01 | 61-01-PLAN.md | Validator discovers replicas by SMEMBERS->GET-each with no prior instanceId knowledge | SATISFIED | `ProcessorLivenessValidator.cs:35`: `SetMembersAsync(L2ProjectionKeys.InstanceIndex(proc.Id))` iterates returned members; no instanceId list pre-loaded. REQUIREMENTS.md marks [x] Complete / Phase 61. |
| GATE-02 | 61-01-PLAN.md | Passes iff >=1 replica present + Healthy + non-stale; unhealthy/stale each fail that replica | SATISFIED | `ProcessorLivenessValidator.cs:62-64`: per-replica status + freshness checks with `break` on first qualifier. Unit tests: `OneHealthyFresh_Admits`, `OneStale_Plus_OneHealthyFresh_Admits_FirstQualifierWins`, `AllUnhealthyOrStale_Throws_AggregateReason`. REQUIREMENTS.md marks [x] Complete. |
| GATE-03 | 61-01-PLAN.md | No-qualifier -> 422 RFC 7807; absent member lazily SREM'd; genuine RedisException still 500 | SATISFIED | Lines 67-73: throws `ProcessorNotLive` (-> 422 RFC 7807). No `catch (RedisException)` in validator. Line 54: `SetRemoveAsync(..., FireAndForget)` for absent. Unit tests assert absent SREM + `gate=="processorLiveness"`. REQUIREMENTS.md marks [x] Complete. |
| PROBE-01 | 61-02-PLAN.md, 61-03-PLAN.md | Probe reads L1 and reports unhealthy when timestamp stale beyond active-interval x2 grace | SATISFIED | `LivenessWatchdogHealthCheck.cs:63`: `now > current.Timestamp.AddSeconds(current.Interval * 2)` -> Unhealthy. Integration proof: `ProcessorHealthLiveNullTests.Live_Is_Unhealthy_When_L1_Null` (503) and `ProcessorHealthLiveTests.Live_Is_Unhealthy_When_L1_Stale` (503). REQUIREMENTS.md marks [x] Complete. |
| PROBE-02 | 61-02-PLAN.md, 61-03-PLAN.md | Probe returns per-schema summary in response body | SATISFIED | `LivenessWatchdogHealthCheck.cs:55-60`: `HealthCheckResult.Data` carries `inputSchema`/`outputSchema`/`configSchema`. Integration proof: `Live_Is_Healthy_And_Carries_Summary_When_L1_Fresh` asserts all three keys in HTTP body. REQUIREMENTS.md marks [x] Complete. |

All 5 requirement IDs (GATE-01, GATE-02, GATE-03, PROBE-01, PROBE-02) mapped to Phase 61 in REQUIREMENTS.md traceability table. No orphaned requirements found. TEST-01/02/03 are Phase 62 pending — correctly deferred (see Deferred Items below).

### Deferred Items

Items not yet met but explicitly addressed in a later milestone phase.

| # | Item | Addressed In | Evidence |
|---|------|-------------|----------|
| 1 | RealStack E2E proof of gate + probe against live Docker stack | Phase 62 | REQUIREMENTS.md TEST-01, TEST-02, TEST-03 all mapped to Phase 62 as Pending. Phase 62 goal: "Live Proof & Close Gate". |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| None found | — | — | — | — |

Scanned all phase-modified production files. No TODO/FIXME/placeholder comments, no empty implementations, no hardcoded empty collections flowing to rendering. The single reference to `ProcessorProjection` in `ProcessorLivenessFacts.cs` is in a doc comment explaining the retired contract — not in live code.

### Human Verification Required

None. All observable truths are verifiable via code inspection, structural checks (file presence/absence), and pattern matching. The integration test suite (hermetic) covers the functional behaviors end-to-end through the real embedded Kestrel listener. Live-stack E2E is explicitly Phase 62 scope.

---

## Gaps Summary

No gaps. All 5 success criteria are satisfied by actual code in the codebase, not just SUMMARY claims.

- GATE-01/02/03: `ProcessorLivenessValidator.cs` implements the full SMEMBERS->GET-each per-replica loop with correct admit/fail/SREM/aggregate-reason logic; no `catch (RedisException)` preserves the 422-vs-500 split. The legacy `ProcessorProjection.cs` is confirmed deleted and `L2ProjectionKeys.Processor(Guid)` / `RedisProjectionKeys.Processor` forwarders are confirmed absent.
- PROBE-01/02: `LivenessWatchdogHealthCheck.cs` reads L1 at check time via the outer provider, reports null/stale as Unhealthy and fresh as Healthy, carries the three-key summary in `HealthCheckResult.Data`. The `HealthCheckDescriptor` seam in `BaseConsole.Core` is generic (zero `BaseProcessor` references). The `AddBaseProcessor` registration of the live-tagged descriptor is wired. End-to-end integration proof through real Kestrel listener is in place (`ProcessorHealthLiveTests` + `ProcessorHealthLiveNullTests`).
- All 7 commits (35dec7e, 6b62128, 222cc35, d750465, 5483150, 3001dec, 47f256c) confirmed present in git log.
- Hermetic test suite: 585 passed; 7 `[Trait("Category","RealStack")]` failures are pre-existing live-stack E2E tests, documented as Phase 62 scope, not regressions introduced by this phase.

---

_Verified: 2026-06-13T15:00:00Z_
_Verifier: Claude (gsd-verifier)_
