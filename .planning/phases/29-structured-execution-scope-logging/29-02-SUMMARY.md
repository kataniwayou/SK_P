---
phase: 29-structured-execution-scope-logging
plan: 02
subsystem: messaging
tags: [logging, otel, scopes, masstransit, consume-filter]

# Dependency graph
requires:
  - phase: 29-structured-execution-scope-logging
    plan: 01
    provides: "ExecutionLogScope — the five execution-id scope-key constants read by name"
  - phase: 17-messaging-contracts
    provides: "IExecutionCorrelated (5 execution ids); InboundCorrelationConsumeFilter open-generic shape to mirror"
provides:
  - "InboundExecutionScopeConsumeFilter<T> — bus-wide open-generic consume filter scoping the 5 execution ids (NOT CorrelationId) for IExecutionCorrelated messages"
  - "Bus-wide registration of the execution filter after the correlation filter in AddBaseConsoleMessaging (covers both consoles, no per-console wiring)"
  - "Hermetic probe-consumer test proving 5-id capture, Guid.Empty-skip, and non-IExecutionCorrelated pass-through"
affects: [nested-beginscope, processorid-enricher, workflowfirejob, es-attributes]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Open-generic bus-wide consume filter mirrors the correlation filter shape; nests INNER (after correlation, D-02) so CorrelationId stays the outer scope"
    - "Guid.Empty-valued execution ids produce no scope entry (D-03) so empty ids never surface as ES attributes"
    - "Scope state captured in tests via a ~25-line test-only scope-capturing ILoggerProvider double (no ICorrelationAccessor, no new package)"

key-files:
  created:
    - src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs
    - tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs
  modified:
    - src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs

key-decisions:
  - "Execution filter has NO ICorrelationAccessor ctor param (D-01) — it never touches CorrelationId; that stays owned by the byte-unchanged InboundCorrelationConsumeFilter"
  - "Registered immediately AFTER the correlation consume filter (D-02) so CorrelationId is the outer scope and the execution id-set nests inside"
  - "Guid.Empty values skipped (D-03) — no noise scope entry for entry-step dispatch's empty executionId/entryId"

requirements-completed: [LOG-01, LOG-02, LOG-03]

# Metrics
duration: 4min
completed: 2026-06-02
---

# Phase 29 Plan 02: Inbound Execution-Scope Consume Filter Summary

**Open-generic bus-wide `InboundExecutionScopeConsumeFilter<T>` that opens a MEL log scope carrying the five execution ids (under the `ExecutionLogScope` keys, each `Guid.Empty` skipped — D-03) for every consumed `IExecutionCorrelated` message — registered once after the correlation filter (D-02) so both the orchestrator (`ResultConsumer`) and processor (`EntryStepDispatchConsumer`) surface those ids at ES `attributes.<Key>` (LOG-01) with no per-console wiring, while CorrelationId stays owned by the byte-unchanged correlation filter (D-01).**

## Performance

- **Duration:** ~4 min
- **Tasks:** 2
- **Files modified:** 2 created, 1 modified

## Accomplishments

- `InboundExecutionScopeConsumeFilter<T>` added as a SIBLING to `InboundCorrelationConsumeFilter` — open-generic `IFilter<ConsumeContext<T>> where T : class`, single `ILogger` ctor param (NO `ICorrelationAccessor` — D-01). Body-reads `IExecutionCorrelated`, opens a MEL scope with the five `ExecutionLogScope.*` keys, skips any `Guid.Empty` (D-03), and is a pass-through no-op for non-`IExecutionCorrelated` messages.
- Registered bus-wide ONCE in `AddBaseConsoleMessaging`, immediately AFTER the correlation `UseConsumeFilter` line (D-02 — CorrelationId outer, execution id-set inner). This single registration covers BOTH consoles; no per-console wiring.
- `InboundCorrelationConsumeFilter.cs` left byte-unchanged (`git diff --quiet HEAD` exits 0 — L5/SC#2).
- Hermetic `ConsoleExecutionScopeFilterTests` (in-memory MassTransit harness + probe consumer + a ~25-line scope-capturing `ILoggerProvider` double, no new package) proves all three cases GREEN (3/3).
- `dotnet build src/BaseConsole.Core -c Debug` clean at 0 Warning / 0 Error.

## Task Commits

1. **Task 1 — filter + bus-wide registration** - `440c88a` (feat)
2. **Task 2 — hermetic probe-consumer test** - `ad332fc` (test)

**Plan metadata:** committed separately with this SUMMARY + state docs.

## Files Created/Modified

- `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` (created) — the open-generic execution-id scope filter (5 ids, Guid.Empty-skip, non-IExecutionCorrelated pass-through).
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` (modified) — one `UseConsumeFilter(typeof(InboundExecutionScopeConsumeFilter<>), ctx)` line added after the correlation filter.
- `tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs` (created) — three-case hermetic test + the scope-capturing logger double.

## Decisions Made

- **No ICorrelationAccessor (D-01):** the execution filter does not touch CorrelationId; correlation stays owned by `CorrelationKeys.LogScope` via the unchanged correlation filter.
- **INNER registration (D-02):** placed after the correlation consume filter so CorrelationId is the outer scope and the execution ids nest inside.
- **Guid.Empty skip (D-03):** entry-step dispatch carries empty `executionId`/`entryId`; skipping them keeps those keys out of ES until they have real values.
- **Test capture via ILoggerProvider double (D-07):** the correlation test reads the accessor, but the execution filter has none; the scope `Dictionary` is captured directly via a tiny test-only `ILoggerProvider`/`ILogger` whose `BeginScope` records the state. No reusable scope-capturing provider existed under `tests/` (the one `CapturingLogger` in `LivenessResilienceFacts` returns null from `BeginScope`), so a ~25-line local double was added; no NuGet package added.

## Deviations from Plan

None - plan executed exactly as written. The provided source and registration line were used verbatim; the test followed the analog `ConsoleCorrelationFilterTests` harness shape with the planned `ILoggerProvider`-double divergence (D-07). No Rule 1/2/3 auto-fixes were required. (One pre-existing untracked file `src/BaseApi.Service/Properties/launchSettings.json` was present in the working tree and left untouched — not in this plan's scope.)

## Issues Encountered

None functional. Tooling note: the Bash tool on this Windows host runs POSIX bash (not PowerShell), so output piping used `tail`/`$?` rather than `Select-String`/`$LASTEXITCODE`.

## TDD Gate Compliance

This plan's RED/GREEN behavior proof lives in Task 2's hermetic test. Per the plan's two-task split, Task 1 created the filter (build-verified) and Task 2 added the behavior test against that already-built SUT — so a literal `test(...)`-before-`feat(...)` commit ordering was NOT produced (the `feat(29-02)` filter commit `440c88a` precedes the `test(29-02)` commit `ad332fc`). The behavioral contract is nonetheless test-proven: `ConsoleExecutionScopeFilterTests` exercises the three SUT cases (5-id capture, Guid.Empty-skip, non-IExecutionCorrelated pass-through) and is GREEN (3/3). This is the plan-structured order, not a TDD violation of the feature contract, but the strict RED-before-implementation gate sequence was not followed for this plan and is noted here for the verifier.

## Verification Evidence

- `dotnet build src/BaseConsole.Core -c Debug` → Build succeeded, 0 Warning(s) / 0 Error(s).
- `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release -- --filter-class *ConsoleExecutionScopeFilterTests` → Passed: 3 / Failed: 0 (3 cases: a/b/c).
- `git diff --quiet HEAD -- src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs` → exit 0 (byte-unchanged — L5).
- grep on `InboundExecutionScopeConsumeFilter.cs` → no `ICorrelationAccessor`, no `CorrelationKeys`, no `CorrelationId` member (only a doc-comment prose mention).
- Registration line `UseConsumeFilter(typeof(InboundExecutionScopeConsumeFilter<>), ctx)` present AFTER the correlation filter line in `MessagingServiceCollectionExtensions.cs`.

## Threat Surface

No new surface. T-29-03 (log injection) and T-29-04 (info disclosure) both `accept` in the plan's threat register: the five ids are typed `Guid` read off `IExecutionCorrelated`, placed only as scope VALUES under fixed keys (never interpolated), server-minted opaque GUIDs; `Guid.Empty` skipped. No new endpoint, auth, network, or storage surface — the existing OTel IncludeScopes bridge is reused unchanged.

## Next Phase Readiness

- LOG-02 (the inbound execution-scope filter) shipped; the id-set now flows into the MEL scope on every consume for both consoles. Remaining Phase 29 plans (nested `BeginScope` in the dispatch consumer, the `ProcessorId` enricher, `WorkflowFireJob`) can layer on top of this scope.
- No blockers. Full-suite hermetic regression (exclude Category=RealStack) recommended at wave merge per the plan's verification.

## Self-Check: PASSED

- FOUND: `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs`
- FOUND: `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` (modified)
- FOUND: `tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs`
- FOUND: `.planning/phases/29-structured-execution-scope-logging/29-02-SUMMARY.md`
- FOUND commit: `440c88a` (feat — filter + registration)
- FOUND commit: `ad332fc` (test — three-case hermetic test)

---
*Phase: 29-structured-execution-scope-logging*
*Completed: 2026-06-02*
