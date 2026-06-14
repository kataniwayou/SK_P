---
phase: 65-fan-out-workflow-seeder-clean-state-stack
plan: 01
subsystem: testing
tags: [e2e, realstack, npgsql, fan-out-workflow, seeder, idempotency, seconds-cron, xunit-v3, mtp]

# Dependency graph
requires:
  - phase: 22-l2-root-parent-restructure-processor-self-registration
    provides: "SeedProcessorAsync by-source-hash GET-or-create + RealStackWebAppFactory host-stack overrides"
  - phase: 24.1-gating-redesign
    provides: "WorkflowCreateDto/StepCreateDto/AssignmentCreateDto REST contracts + junction sync"
  - phase: 58-orchestration-gate-integration-proof
    provides: "6-field IncludeSeconds cron validator (WorkflowDtoValidator accepts '*/30 * * * * *')"
provides:
  - "Runnable, idempotent fan-out workflow seeder artifact (FanOutSeederE2ETests) Phases 67/68 invoke"
  - "Self-verifying RealStack [Fact] proving WF-01 (topology) + WF-02 (payloads + idempotency) inline via Npgsql"
  - "SeedAssignmentAsync + SeedStepWithNextAsync reusable internal-static seed helpers"
affects: [67-fault-injection-harness, 68-prometheus-es-analyzer, clean-state-harness]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Reverse-topological step create (sinks first) to satisfy OnDelete(Restrict) step_next_steps FKs"
    - "Sentinel-name idempotency gate (GET /workflows -> FirstOrDefault by name 'v8-fanout-proof')"
    - "Direct Npgsql snake_case self-verification for junction rows REST read DTOs do not surface"

key-files:
  created:
    - "tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs"
  modified: []

key-decisions:
  - "Reverse-topo create order (F1,F2 -> E1,E2 -> D1,D2 -> C -> B -> A) because both step_next_steps FKs are OnDelete(Restrict) — every NextStepId must pre-exist"
  - "Idempotency keyed on the stable sentinel workflow name 'v8-fanout-proof' (workflows have no unique-name constraint); 2nd in-process seed GET-matches and returns the same id"
  - "Run-twice idempotency proven in ONE [Fact] (capture both wfIds, Assert.Equal) rather than two dotnet test invocations"
  - "Seed via REST only (no raw SQL inserts) so cron/payload/FK validation runs server-side; verification reads via Npgsql because REST read DTOs return null junctions"

patterns-established:
  - "Fan-out multi-node topology seeded through validated REST DTOs with a node->stepId dictionary and explicit edge wiring"
  - "Inline Npgsql count + edge-set + payload-regex self-verification mirroring StepsIntegrationTests junction strategy"

requirements-completed: [WF-01, WF-02]

# Metrics
duration: ~55min
completed: 2026-06-14
---

# Phase 65 Plan 01: Fan-Out Workflow Seeder & Self-Verify Summary

**Idempotent RealStack fan-out workflow seeder (A→B→C→{D1→E1→F1, D2→E2→F2}, 1 workflow / 9 steps / 8 edges / 9 assignments, single shared processor-sample, 6-field `*/30 * * * * *` cron) delivered as a self-verifying xunit.v3 fixture that proves WF-01 + WF-02 live in one fact.**

## Performance

- **Duration:** ~55 min
- **Started:** 2026-06-14T10:37:20Z (plan execution start)
- **Completed:** 2026-06-14T11:30Z
- **Tasks:** 2
- **Files modified:** 1 (created)

## Accomplishments
- `SeedFanOutAsync` build routine: reverse-topological create of the 9-step / 8-edge graph, all steps bound to the single by-source-hash-resolved processor-sample id, 9 two-sided-bound `{number,label}` assignments, single `v8-fanout-proof` workflow with 6-field seconds-cron and entry = A.
- Sentinel-name idempotency gate proven: seeding twice in-process leaves counts at 1/9/9/8 with an unchanged workflow id.
- Self-verifying `[Fact]` asserts all SPEC acceptance counts (1/9/9/8/1, distinct processor=1, workflow_assignments=9), the exact 8-edge node-pair set, F1/F2 zero-outgoing sinks, and 9 distinct `^Step_(A|B|C|D1|E1|F1|D2|E2|F2)$` payloads with the fixed A=1..F2=9 number mapping — via direct Npgsql snake_case reads.
- Live-proven against a reset-clean stack: **1/1 passed (3.7s)**; final DB state exactly 1/9/9/8/1, distinct proc=1, workflow_assignments=9, one `v8-fanout-proof`.

## Task Commits

1. **Task 1: Fixture shell + SeedAssignmentAsync + reverse-topo build routine** - `c9cc582` (feat)
2. **Task 2: Self-verify [Fact] + run-twice idempotency** - `c16d5fe` (test)

_TDD note: this artifact IS a self-verifying E2E fixture — the test and the seeder are one file; Task 1 delivered the build routine (gated by Release build 0-warnings), Task 2 added the assertions (gated by the live run)._

## Files Created/Modified
- `tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs` - New RealStack fixture: `SeedFanOutAsync` idempotent build routine, `SeedAssignmentAsync` + `SeedStepWithNextAsync` helpers, and the `FanOutSeeder_SeedsAndSelfVerifies` self-verifying fact.

## Decisions Made
- Reverse-topo create order driven by the `OnDelete(Restrict)` FK constraint on `step_next_steps`.
- Idempotency by sentinel workflow name (no unique constraint exists), mirroring `SeedConfigSchemaAsync`.
- Run-twice idempotency collapsed into one fact for a simpler, faster artifact.
- Reused `SampleRoundTripE2ETests.SeedProcessorAsync` + `RealStackWebAppFactory` verbatim (the by-source-hash GET-or-create resolves the same processor row the live container heartbeats against).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] MTP filter syntax for live verification**
- **Found during:** Task 2 (live run)
- **Issue:** The plan's acceptance command `dotnet test --filter "FullyQualifiedName~FanOutSeeder"` uses VSTest filter syntax, but this test project runs on **Microsoft.Testing.Platform (xunit.v3 3.2.2, `UseMicrosoftTestingPlatformRunner=true`)**, which **ignores** `--filter` (emits `MTP0001: VSTestTestCaseFilter ... will be ignored`) and instead runs the ENTIRE suite. That caused other RealStack tests (`SampleRoundTrip`, `CorrelationPropagation`) to run concurrently and pollute the DB, defeating the absolute-count assertions.
- **Fix:** Invoked the MTP host directly with the native xunit.v3 filter — `BaseApi.Tests.exe --filter-class "*FanOutSeederE2ETests"` — which runs ONLY the FanOut fact. Verified hermetic exclusion with `--filter-not-trait "Category=RealStack"` (Zero tests ran). No code change required; this is an invocation correction for Phases 67/68 to note.
- **Files modified:** none (invocation only)
- **Verification:** `total: 1, failed: 0, succeeded: 1` against a reset-clean stack; final DB 1/9/9/8 confirmed via psql.
- **Committed in:** n/a (no file change)

**2. [Rule 3 - Blocking] Definite-assignment compile error on `number`**
- **Found during:** Task 2 (Release build)
- **Issue:** Using `&&`-chained `numberEl.TryGetInt32(out var number)` inside `Assert.True(...)` left `number` "possibly unassigned" for later use (CS0165).
- **Fix:** Split into a boolean `hasInt` check then `var number = numberEl.GetInt32();` after the assertion.
- **Files modified:** `tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs`
- **Verification:** `dotnet build SK_P.sln -c Release` 0 warnings / 0 errors.
- **Committed in:** `c16d5fe` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (both Rule 3 - blocking)
**Impact on plan:** Both necessary to complete the live verification; neither changes the seeder behavior or scope. No product code (`src/**`) touched.

## Issues Encountered
- **Orphaned MTP test-host processes held file locks / kept writing.** Because `--filter` was silently ignored, an early full-suite run kept executing other RealStack tests in the background (writing `sample-wf-*`/`corr-wf-*` rows) and a stale test-host held `TestResults/*.log`, causing an `MSB4166` file-lock build failure. Resolved by killing the `BaseApi.Tests` dotnet/test-host processes, confirming DB writes stabilized, re-applying the FK-safe reset, and running the MTP host directly with `--filter-class`.
- **DB reset-clean precondition is provided by a later plan.** This fact asserts absolute counts (steps=9, etc.) that require a reset-clean DB; the Phase-65 reset/up scripts are plans 65-02/65-03 (not yet executed). For this verification the reset was applied manually using the exact FK-safe DELETE order documented in 65-PATTERNS.md (preserving `processors` + `config_schemas`), matching the 67/68 harness contract (reset BEFORE the seeder).

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The runnable seeder artifact is ready for Phases 67/68 to invoke. **Invocation note for those phases:** because the suite runs on Microsoft.Testing.Platform, target the fact with the MTP host directly — `BaseApi.Tests.exe --filter-class "*FanOutSeederE2ETests"` (or the equivalent MTP `--filter-class`) — NOT VSTest `--filter "FullyQualifiedName~..."`, which MTP ignores.
- The fact presumes a reset-clean DB; plans 65-02 (`phase-65-reset.ps1`) and 65-03 (`phase-65-up.ps1`) supply the clean-state precondition the harness invokes before this seeder.
- No blockers.

## Self-Check: PASSED

- `tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs` — FOUND
- `65-01-SUMMARY.md` — FOUND
- Commit `c9cc582` (Task 1) — FOUND
- Commit `c16d5fe` (Task 2) — FOUND

---
*Phase: 65-fan-out-workflow-seeder-clean-state-stack*
*Completed: 2026-06-14*
