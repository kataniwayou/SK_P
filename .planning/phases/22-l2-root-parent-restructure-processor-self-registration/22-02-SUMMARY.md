---
phase: 22-l2-root-parent-restructure-processor-self-registration
plan: 02
subsystem: orchestrator
tags: [redis, l2-projection, reader, key-builder, di-cleanup, hardening]

# Dependency graph
requires:
  - phase: 22-l2-root-parent-restructure-processor-self-registration
    provides: "no-prefix OrchestratorL2Keys.Root(Guid) reader forwarder + L2ProjectionKeys.Prefix const (Plan 01)"
provides:
  - "Orchestrator (reader) constructs L2 root keys with no configurable prefix — calls OrchestratorL2Keys.Root(workflowId) directly"
  - "OrchestratorRedisOptions type deleted; no DI registration; no Redis:KeyPrefix config on the reader side"
affects: [22-03, orchestrator-consumers, redis-l2-read]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Reader-side L2 key construction has zero config-driven inputs — prefix is the compile-time const from Plan 01"

key-files:
  created: []
  modified:
    - src/Orchestrator/Consumers/StartOrchestrationConsumer.cs
    - src/Orchestrator/Consumers/StopOrchestrationConsumer.cs
    - src/Orchestrator/Program.cs
    - src/Orchestrator/appsettings.json
    - tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs
  deleted:
    - src/Orchestrator/Messaging/OrchestratorRedisOptions.cs

key-decisions:
  - "OrchestratorRedisOptions deleted wholesale (D-07) — its only field was the prefix, now a compile-time const."
  - "Removed the unused `using Orchestrator.Messaging;` from Program.cs after dropping the singleton registration (TreatWarningsAsErrors would otherwise fail)."
  - "Removed the entire Redis config section from Orchestrator appsettings.json — ConnectionStrings:Redis carries the connection; KeyPrefix was the section's only remaining key."

patterns-established:
  - "No caller-supplied input feeds reader-side key construction (T-22-03 mitigation): the prefix is a const, not config/DI."

requirements-completed: [L2PREFIX-01]

# Metrics
duration: 2min
completed: 2026-05-31
---

# Phase 22 Plan 02: Orchestrator Reader-Side Prefix Removal Summary

**Removed the configurable key-prefix from the entire Orchestrator (reader) side: dropped the `OrchestratorRedisOptions` ctor param from both consumers and switched their L2 root reads to the parameterless `OrchestratorL2Keys.Root(workflowId)` forwarder, then deleted the `OrchestratorRedisOptions` record, its DI registration, and the now-empty `Redis` config section.**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-05-31T08:50:58Z
- **Completed:** 2026-05-31T08:52:48Z
- **Tasks:** 2
- **Files modified:** 5 (+ 1 deleted)

## Accomplishments
- Both consumers (`StartOrchestrationConsumer`, `StopOrchestrationConsumer`) no longer take `OrchestratorRedisOptions`; their ctors are now `(IConnectionMultiplexer redis, ILogger<T> logger)` and their L2 root reads call `OrchestratorL2Keys.Root(workflowId)` — the parameterless Plan-01 forwarder.
- Deleted `src/Orchestrator/Messaging/OrchestratorRedisOptions.cs` entirely (D-07) — its only purpose was carrying the prefix, now a compile-time const on `L2ProjectionKeys`.
- Removed the `AddSingleton(new OrchestratorRedisOptions(...))` registration from `Program.cs` plus the resulting unused `using Orchestrator.Messaging;`.
- Removed the `"Redis": { "KeyPrefix": "skp:" }` section from the Orchestrator `appsettings.json`; the file remains valid JSON and `ConnectionStrings:Redis` still carries the connection.
- Orchestrator project builds clean: 0 Warnings, 0 Errors (Debug). Reader side is the last config-driven input into key construction removed (T-22-03 mitigation complete).

## Task Commits

Each task was committed atomically:

1. **Task 1: Drop OrchestratorRedisOptions from both consumers + switch to no-prefix Root** - `33057d0` (refactor)
2. **Task 2: Delete OrchestratorRedisOptions + its registration + Orchestrator Redis config** - `18e7f87` (refactor)

## Files Created/Modified
- `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` - Dropped `OrchestratorRedisOptions options` ctor param; read switched to `OrchestratorL2Keys.Root(workflowId)`.
- `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` - Same change (mirror).
- `src/Orchestrator/Program.cs` - Removed the `AddSingleton(OrchestratorRedisOptions)` registration + its comment + the now-unused `using Orchestrator.Messaging;`.
- `src/Orchestrator/appsettings.json` - Removed the `Redis` (KeyPrefix) section.
- `src/Orchestrator/Messaging/OrchestratorRedisOptions.cs` - DELETED.
- `tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs` - (Deviation Rule 3) removed the two `AddSingleton(new OrchestratorRedisOptions(Prefix))` harness registrations, the `using Orchestrator.Messaging;`, and the now-unused `Prefix` const so the no-arg consumer ctors resolve.

## Decisions Made
- None beyond the plan — D-07 implemented exactly as specified. The Program.cs `using` removal and the full `Redis`-section removal were explicitly authorized by the plan's Task 2 action notes.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fixed the in-memory ack harness that referenced the deleted type**
- **Found during:** Task 2
- **Issue:** `tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs` (not listed in the plan's `files_modified`) referenced `OrchestratorRedisOptions` in two `BuildStartHarness`/`BuildStopHarness` `AddSingleton` calls + a `using Orchestrator.Messaging;` + a now-orphan `private const string Prefix = "skp:"`. Deleting the type and dropping the consumer ctor params would break this dependent compilation unit (and, with `TreatWarningsAsErrors`, the orphan const + unused using would be build-fatal).
- **Fix:** Removed both `.AddSingleton(new OrchestratorRedisOptions(Prefix))` lines (the consumers no longer take it), removed the `using Orchestrator.Messaging;`, and removed the unused `Prefix` const. The test's behavior (absent/present/infra-fault ack split) is unchanged — it never asserted on the prefix value, only stubbed `StringGetAsync(Arg.Any<RedisKey>(), ...)`.
- **Files modified:** `tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs`
- **Commit:** `18e7f87`

## Issues Encountered

**The full test project / solution does NOT build at the end of this plan — expected and acceptable mid-Wave-2.** `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj` fails with exactly 7 `CS1501` errors, ALL in `src/BaseApi.Service` (`OrchestrationService.cs:206`, `RedisProjectionWriter.cs:80/97/112`, `RedisL2Cleanup.cs:43/63/77`) — the writer/service-side call sites that Plan 01's SUMMARY explicitly names as "fixed in Plans 02 and 03" and that the parallel Wave-2 Plan 03 owns. There are ZERO errors in my reader-side files or in the ack harness I touched, confirming the Rule 3 fix is correct. The `Orchestrator.csproj` (this plan's scope) builds clean (0/0). My reader side compiles and is complete; the test class goes GREEN once Plan 03 lands and fixes those 7 writer-side call sites.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Reader-side L2PREFIX-01 is complete: no configurable prefix, no `OrchestratorRedisOptions`, no `Redis:KeyPrefix` reader config.
- Plan 03 (parallel Wave 2) MUST fix the 7 writer/service-side `CS1501` call sites listed above before the solution and the `StartStopConsumerAckTests` class build and run GREEN.
- No blockers; the mid-wave non-building solution is the intended hand-off state (matches Plan 01's documented expectation).

## Self-Check: PASSED

All reader-side claims verified — see Self-Check section below.

---
*Phase: 22-l2-root-parent-restructure-processor-self-registration*
*Completed: 2026-05-31*
