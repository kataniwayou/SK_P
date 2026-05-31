---
phase: 22-l2-root-parent-restructure-processor-self-registration
plan: 04
subsystem: orchestration-validation
tags: [redis, l2-projection, processor-liveness, validation-gate, rfc7807, di]

# Dependency graph
requires:
  - phase: 22-l2-root-parent-restructure-processor-self-registration
    provides: "RedisProjectionKeys.Processor(Guid) no-prefix forwarder (Plan 01); ProcessorProjection/LivenessProjection L2 shapes; processor-no-create writer boundary (Plan 03) so entries are external-only"
  - phase: 22-l2-root-parent-restructure-processor-self-registration
    provides: "OrchestrationService + OrchestrationServiceCollectionExtensions post-IOptions-removal ctor/factory arg list (Plan 03 — Wave 3 ordering)"
provides:
  - "ProcessorLivenessValidator — async existence + SECONDS-based liveness gate over snapshot.Processors.Values (PROC-LIVE-01)"
  - "OrchestrationValidationException.ProcessorNotLive(procId, reason) factory + ProcessorLivenessOffending record — gate string 'processorLiveness', reused 422/RFC-7807 contract"
  - "StartAsync invokes the liveness gate AFTER the sync trio and BEFORE UpsertAsync, wrapped in the redisOp catch (D-15)"
  - "AddScoped<ProcessorLivenessValidator>() + matching DI factory arg"
  - "SchemaEdgeValidator verified untouched (PROC-EDGE-01 — verification-only claim)"
affects: [22-05, orchestration-service, processor-liveness-validator]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Async validation gate slotted into the otherwise-sync Start validator chain — wrapped in the same RedisException->redisOp catch as the L2 write so a Redis fault tags 'UpsertAsync'"
    - "Fail-safe liveness: absent OR stale (timestamp + interval*2 <= now) both throw ProcessorNotLive -> 422; never fail-open"
    - "New validation gate reuses the single OrchestrationValidationException + its 422 handler — discriminator string only, no new handler"

key-files:
  created:
    - src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs
  modified:
    - src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs

key-decisions:
  - "Liveness time unit is SECONDS (locked plan objective + D-16): deadline = liveness.Timestamp.AddSeconds(liveness.Interval * 2), compared to _clock.GetUtcNow().UtcDateTime. The Plan 05 test seed must match (positive seconds = live; past timestamp or interval=0 = stale)."
  - "The liveness gate is the ONE async gate in the chain; it is wrapped in the existing redisOp catch (redisOp='UpsertAsync') so a genuine RedisException tags the Start op name. The 422 (ProcessorNotLive) is NOT a RedisException and propagates uncaught past that catch to the 422 handler (correct)."
  - "Gate slots AFTER the sync trio (cycle -> schema-edge -> payload-config) and BEFORE UpsertAsync — the locked D-15 position."
  - "No handler change — OrchestrationValidationExceptionHandler already maps ANY OrchestrationValidationException to 422 + RFC-7807 via the { gate, offending } ErrorsExtension envelope."

patterns-established:
  - "Untrusted self-registered L2 JSON is GET-then-strongly-typed-deserialized; a malformed/zero/far-past timestamp yields deadline<=now -> 'stale' -> 422 (fail-safe to reject, T-22-09)."
  - "Offending payload carries only the processor Guid + a fixed reason enum ('absent'|'stale') — no stack/connection leakage (T-22-12)."

requirements-completed: [PROC-LIVE-01, PROC-EDGE-01]

# Metrics
duration: 2min
completed: 2026-05-31
---

# Phase 22 Plan 04: Processor-Liveness Gate (PROC-LIVE-01 + PROC-EDGE-01) Summary

**A new async `ProcessorLivenessValidator` now runs during Start — after the cycle/schema-edge/payload-config sync trio and before the L2 `UpsertAsync` — GETting each participating processor's self-registered `skp:{procId}` entry and requiring existence + SECONDS-based liveness (`timestamp + interval*2 > now`); absence throws `ProcessorNotLive(procId, "absent")` and staleness throws `... "stale"`, both surfacing as 422 + RFC-7807 through the unchanged `OrchestrationValidationException` handler — while `SchemaEdgeValidator` is verified byte-unchanged (PROC-EDGE-01).**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-05-31T09:04:32Z
- **Completed:** 2026-05-31T09:06:33Z
- **Tasks:** 3
- **Files modified:** 4 (1 created + 3 modified)

## Accomplishments
- **PROC-LIVE-01 (factory + offending — Task 1):** added `OrchestrationValidationException.ProcessorNotLive(Guid procId, string reason)` mirroring the `SchemaEdge` factory (gate literal `"processorLiveness"`, title "Participating processor is not live", detail `Processor '{procId}' is not live: {reason}.`) and the `public sealed record ProcessorLivenessOffending(Guid procId, string reason)`; the `Gate` discriminator XML comment now lists `processorLiveness`. The `OrchestrationValidationExceptionHandler` is reused unchanged.
- **PROC-LIVE-01 (validator — Task 2):** new `internal sealed class ProcessorLivenessValidator` (ctor deps `IConnectionMultiplexer` + `TimeProvider`). `ValidateAsync` iterates `snapshot.Processors.Values`, GETs `RedisProjectionKeys.Processor(proc.Id)`; an absent key throws `ProcessorNotLive(proc.Id, "absent")`, otherwise deserializes `ProcessorProjection` and checks `liveness.Timestamp.AddSeconds(liveness.Interval * 2)` against `_clock.GetUtcNow().UtcDateTime` — `<= now` throws `ProcessorNotLive(proc.Id, "stale")`. Interval interpreted in SECONDS from the entry (D-16). The validator does NOT catch `RedisException` (the caller wraps it).
- **PROC-LIVE-01 (wiring + DI — Task 3):** added the `_processorLivenessValidator` field, ctor param (after `payloadConfigSchemaValidator`), and null-check assignment to `OrchestrationService`; `StartAsync` invokes `await _processorLivenessValidator.ValidateAsync(snapshot, ct)` AFTER the sync trio and BEFORE `UpsertAsync`, wrapped in the existing `catch (RedisException ex) { ex.Data["redisOp"] = "UpsertAsync"; throw; }` pattern (D-15 / OBSERV-REDIS-03). The DI extension gained the matching factory arg `sp.GetRequiredService<ProcessorLivenessValidator>(),` (order matches the new ctor) and `services.AddScoped<ProcessorLivenessValidator>();` — `IConnectionMultiplexer` + `TimeProvider` were already in the container.
- **PROC-EDGE-01 (verification-only):** `git diff --stat src/BaseApi.Service/Features/Orchestration/Validation/SchemaEdgeValidator.cs` shows zero changes — the schema-edge gate is untouched.
- **Builds:** `dotnet build src/BaseApi.Service/BaseApi.Service.csproj -c Debug` succeeds 0 Warnings / 0 Errors after each task (ctor/factory arg order matches).

## Task Commits

Each task was committed atomically:

1. **Task 1: ProcessorNotLive factory + ProcessorLivenessOffending record** - `933c890` (feat)
2. **Task 2: ProcessorLivenessValidator (async, existence + liveness)** - `9563152` (feat)
3. **Task 3: Wire validator into StartAsync (locked position) + DI registration** - `8f0ab18` (feat)

## Files Created/Modified
- `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` - **(created)** the async existence + SECONDS-liveness gate; ctor deps multiplexer + clock; throws `ProcessorNotLive(.., "absent"|"stale")`.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs` - added the `ProcessorNotLive` factory + `ProcessorLivenessOffending` record; extended the `Gate` discriminator XML comment with `processorLiveness`.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` - added `_processorLivenessValidator` field + ctor param + null-check; `StartAsync` invokes the gate in the locked position with the redisOp wrap.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` - added the factory arg (matching ctor order) + `AddScoped<ProcessorLivenessValidator>()`.

## Decisions Made
- None beyond the plan's locked decisions (SECONDS unit / D-16, locked D-15 gate position, redisOp-wrap convention, reused 422 handler) — all explicitly authorized by the plan's task actions and objective.

## Deviations from Plan

None - plan executed exactly as written.

## Verification Evidence
- `dotnet build src/BaseApi.Service/BaseApi.Service.csproj -c Debug` → Build succeeded, 0 Warning(s), 0 Error(s) (after each of the 3 tasks).
- `grep -n "_processorLivenessValidator.ValidateAsync" OrchestrationService.cs` → line 152, between the payload-config gate and `UpsertAsync`.
- `git diff --stat .../SchemaEdgeValidator.cs` → empty (PROC-EDGE-01 — zero lines changed).
- `grep -n "processorLiveness" OrchestrationValidationException.cs` → factory + gate string present.
- DI: `sp.GetRequiredService<ProcessorLivenessValidator>(),` (factory arg, line 63) + `services.AddScoped<ProcessorLivenessValidator>();` (line 76).

## Issues Encountered

None. All three builds were clean on first attempt; no auto-fixes (Rules 1-3) or auth gates were triggered.

## Deferred Issues

None carried forward from this plan. As flagged in 22-03's hand-off, the PATTERNS golden-assertion enhancements (a `processorLiveness` 422 arm in `GateNoWriteFacts`, the `ParentIndex` SMEMBERS / zero-processor-key writer asserts) and the test-isolation rewrite (D-20..D-23) remain owned by **Plan 05 (Wave 4)** — they depend on this gate (now landed). The `ProcessorLivenessFacts` test class is part of that Plan 05 work, NOT this plan (this plan is production-code + DI only; no test files).

## Known Stubs

None. The validator is fully wired (real `IConnectionMultiplexer` + `TimeProvider` from the container) and invoked on the live Start path; no placeholder data or empty sinks.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- The `processorLiveness` gate is live in the Start chain (locked D-15 position) and registered in DI; the 422 contract is reused unchanged.
- Plan 05 (Wave 4) can now add `ProcessorLivenessFacts` (seeding `skp:{procId}` with positive-seconds intervals for live, past timestamp / interval=0 for stale), the `GateNoWriteFacts` `processorLiveness` arm, the writer golden-assertion enhancements, and the test-isolation rewrite.
- No blockers.

## Self-Check: PASSED

All 4 files present on disk; all 3 task commits (`933c890`, `9563152`, `8f0ab18`) found in git history (verified below).

---
*Phase: 22-l2-root-parent-restructure-processor-self-registration*
*Completed: 2026-05-31*
