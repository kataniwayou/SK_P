---
phase: 60-dual-loop-writer-in-memory-l1-liveness-record
plan: 01
subsystem: infra
tags: [liveness, processor, config-binding, volatile, lock-free, dotnet, redis-adjacent]

# Dependency graph
requires:
  - phase: 59-per-instance-l2-keyspace-two-state-liveness-value
    provides: "ProcessorLivenessEntry.Create factory + LivenessStatus/SchemaOutcome consts (the value the L1 holder stores)"
provides:
  - "ProcessorLivenessOptions.StartupIntervalSeconds knob (default 30 = BackoffCap anchor, D-11/D-12)"
  - "IProcessorLivenessState + ProcessorLivenessState: lock-free volatile-ref-swap L1 liveness holder (D-08/09/10, L1-01)"
affects: [60-02-shared-writer, 60-03-startup-loop, 60-04-heartbeat-loop, 61-gate-and-self-watchdog-probe]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Lock-free cross-thread publication via a plain volatile reference field + plain assignment (mirrors ProcessorContext.IsHealthy; no lock{}, no Interlocked.Exchange<T>)"
    - "[ConfigurationKeyName] seconds-int knob added beside existing knobs with a baked default + pinned binding fact"

key-files:
  created:
    - src/BaseProcessor.Core/Liveness/IProcessorLivenessState.cs
    - src/BaseProcessor.Core/Liveness/ProcessorLivenessState.cs
    - tests/BaseApi.Tests/Processor/ProcessorLivenessStateFacts.cs
  modified:
    - src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs
    - tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs

key-decisions:
  - "StartupInterval baked default = 30 (anchored to BackoffCap per D-12); Interval (10 heartbeat) and Ttl (30 floor) left unchanged — no appsettings churn (D-11/D-13)"
  - "L1 holder is a NEW dedicated singleton, NOT bolted onto IProcessorContext — it must be readable DURING startup (unhealthy), a different access discipline than IProcessorContext's read-after-Healthy (WR-03/D-08)"
  - "Publication is a plain volatile reference swap (D-10) — reference-type assignment is atomic in the CLR; over-synchronization (lock/Interlocked.Exchange<T>) deliberately avoided"

patterns-established:
  - "Lock-free volatile immutable-reference holder for cross-thread liveness snapshots"

requirements-completed: [LOOP-03, L1-01]

# Metrics
duration: 53min
completed: 2026-06-13
---

# Phase 60 Plan 01: Foundation Primitives (Interval Split + L1 Holder) Summary

**Added the `StartupInterval` config knob (default 30) to `ProcessorLivenessOptions` and a new lock-free volatile-ref-swap L1 liveness holder (`IProcessorLivenessState`/`ProcessorLivenessState`) — the two no-dependency primitives the shared writer and both loops consume in later Phase-60 plans.**

## Performance

- **Duration:** ~53 min
- **Started:** 2026-06-13T10:20:53Z
- **Completed:** 2026-06-13T11:13:45Z
- **Tasks:** 2 (both TDD)
- **Files modified:** 5 (3 created, 2 modified)

## Accomplishments
- `ProcessorLivenessOptions` gains `StartupIntervalSeconds` ([ConfigurationKeyName("StartupInterval")], default 30 = BackoffCap anchor); `Interval` (10) and `Ttl` (30) retained unchanged.
- New dedicated singleton L1 holder: `IProcessorLivenessState` (Update + Current snapshot) + `ProcessorLivenessState` (lock-free `volatile ProcessorLivenessEntry?` ref swap).
- Two test files: extended `ProcessorOptionsBindingFacts` to six-knob binding + new `ProcessorLivenessStateFacts` (null-before-update, Assert.Same publication, last-write-wins with const-status assertion).
- All 7 Phase=60 trait facts green; Release AND Debug builds 0-warning under `-warnaserror`.

## Task Commits

Each task was committed atomically (TDD: test then feat):

1. **Task 1 (RED): failing StartupInterval binding facts** - `1aa2a0b` (test)
2. **Task 1 (GREEN): StartupInterval knob on ProcessorLivenessOptions** - `64f1f7e` (feat)
3. **Task 2 (RED): failing L1 holder facts** - `5923ede` (test)
4. **Task 2 (GREEN): lock-free L1 liveness holder** - `6133c7d` (feat)

_No REFACTOR commits needed — minimal implementations passed cleanly._

## Files Created/Modified
- `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` - Added `StartupIntervalSeconds` (default 30); class XML doc updated Five -> Six INDEPENDENT knobs.
- `src/BaseProcessor.Core/Liveness/IProcessorLivenessState.cs` - L1 holder contract (`Update(entry)` + `Current`).
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessState.cs` - `public sealed` lock-free `volatile`-ref-swap impl.
- `tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs` - Six-knob binding + default `StartupIntervalSeconds == 30`; tagged `[Trait("Phase","60")]`.
- `tests/BaseApi.Tests/Processor/ProcessorLivenessStateFacts.cs` - New hermetic L1 holder facts.

## Decisions Made
None beyond the plan — followed the locked decisions (D-08/09/10/11/12/13) exactly. Member names `Update`/`Current` and config-key string `StartupInterval` were the plan's explicitly-recommended discretion picks.

## Deviations from Plan

None - plan executed exactly as written. (No Rule 1-4 deviations; the two minimal implementations passed the RED tests on the first GREEN attempt.)

## Issues Encountered

**Tooling — test runner invocation (no code impact).** The repo's `tests/BaseApi.Tests` is xUnit v3 on Microsoft.Testing.Platform (MTP), not VSTest. The plan's verify command `dotnet test ... --filter "FullyQualifiedName~..."` uses VSTest filter syntax that the MTP runner rejects ("Unknown option '--filter'" -> dumps help -> exit 1). Resolved by using the MTP-native filter flags: `dotnet test ... -- --filter-class "*ProcessorOptionsBindingFacts"` and `-- --filter-trait "Phase=60"`. A separate transient `TestResults/*.log` file-lock from a backgrounded run was cleared by stopping stray `dotnet`/`testhost` processes. No production-code consequence — purely the command form used to run the same facts.

## User Setup Required
None - no external service configuration required.

## Verification
- `dotnet test ... -- --filter-trait "Phase=60"` -> Passed! Failed: 0, Passed: 7, Total: 7.
- `dotnet build src/BaseProcessor.Core -c Release -warnaserror` -> Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet build tests/BaseApi.Tests -c Debug -warnaserror` -> Build succeeded, 0 Warning(s).
- Acceptance greps: `StartupIntervalSeconds` present; `ConfigurationKeyName("StartupInterval")` ==1; `IntervalSeconds { get; set; } = 10` ==1 (Interval unchanged); `interface IProcessorLivenessState` ==1; `volatile ProcessorLivenessEntry?` ==1; `lock\b|Interlocked.Exchange` ==0 (D-10); `Assert.Same` present in state facts.

## Next Phase Readiness
- Plan 02 (shared internal writer) can inject `IProcessorLivenessState` and read `StartupIntervalSeconds`/`IntervalSeconds`/`TtlSeconds` against fixed signatures.
- Plans 03/04 (startup + heartbeat loops) read the interval defaults and call `IProcessorLivenessState.Update`.
- No blockers. DI registration of the holder is deferred to Plan 04 (per plan; not touched here).

## Known Stubs
None - both primitives are fully implemented; no placeholder/empty-value stubs introduced.

## Self-Check: PASSED

All created files exist on disk; all four task commits (`1aa2a0b`, `64f1f7e`, `5923ede`, `6133c7d`) present in git history.

---
*Phase: 60-dual-loop-writer-in-memory-l1-liveness-record*
*Completed: 2026-06-13*
