---
phase: 22-l2-root-parent-restructure-processor-self-registration
plan: 03
subsystem: webapi-writer
tags: [redis, l2-projection, parent-index, processor-no-create, di-cleanup, prefix-deconfig]

# Dependency graph
requires:
  - phase: 22-l2-root-parent-restructure-processor-self-registration
    provides: "no-prefix RedisProjectionKeys forwarders + ParentIndex() (Plan 01); writer/service CS1501 call sites named for this plan"
provides:
  - "Start writer SADDs wf.Id (D-format) into RedisProjectionKeys.ParentIndex() — idempotent on re-Start (L2IDX-01)"
  - "Writer creates ZERO processor L2 keys — per-processor write loop removed (PROC-NOCREATE-01)"
  - "RedisL2Cleanup SREMs the workflow from ParentIndex(), hoisted above the absent-root early-return (idempotent GC, D-10)"
  - "No configurable KeyPrefix in writer/cleanup/service code path or BaseApi.Service appsettings (L2PREFIX-01)"
  - "IOptions<RedisProjectionOptions> dropped from OrchestrationService + RedisL2Cleanup ctors + the OrchestrationService DI factory arg"
affects: [22-04, redis-projection-writer, redis-l2-cleanup, orchestration-service, parent-index]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Parent-index SET maintained by the writer (SADD on Start) + cleanup (SREM hoisted, idempotent GC)"
    - "Writer-side L2 key construction has zero config-driven inputs — prefix is the compile-time const from Plan 01"
    - "Coupled ctor/DI-factory edit: removing the last IOptions read drops the ctor param AND its factory arg in lockstep"

key-files:
  created: []
  modified:
    - src/BaseApi.Core/Configuration/RedisProjectionOptions.cs
    - src/BaseApi.Service/appsettings.json
    - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs
    - tests/BaseApi.Tests/Features/Orchestration/Projection/RedisProjectionKeysTests.cs
    - tests/BaseApi.Tests/Composition/RedisProjectionOptionsBindingFacts.cs
    - tests/BaseApi.Tests/Orchestration/OrchestrationServicePublishTests.cs

key-decisions:
  - "SREM hoisted ABOVE the absent-root early-return so GC is idempotent even when the root is already gone (D-10 step 1 / PATTERNS caveat 2)."
  - "RedisL2Cleanup drops its IOptions<RedisProjectionOptions> ctor param + _options field entirely — after the prefix read is gone the only remaining dep is the multiplexer (D-12)."
  - "OrchestrationService drops the IOptions ctor param + the matching DI factory arg in the SAME edit (PATTERNS caveat 3 coupled edit) — _keyPrefix was the only IOptions consumer in the class."
  - "RedisProjectionWriter KEEPS its IOptions<RedisProjectionOptions> ctor injection (options still owns ProcessorKeyTtlDays + Serialization) but reads neither field after the processor loop removal — assigned-readonly field, no analyzer warning under TreatWarningsAsErrors."

patterns-established:
  - "Parent-index lifecycle split across the two Redis maintainers: writer SADD (Start), cleanup SREM (idempotent, hoisted)."
  - "No caller-supplied input feeds writer/service key construction (T-22-07 mitigation) — prefix is a compile-time const, not config/DI."
  - "Removing per-processor writes reduces the writer's write surface (T-22-08) — processor entries owned solely by external self-registration."

requirements-completed: [L2IDX-01, L2PREFIX-01, PROC-NOCREATE-01]

# Metrics
duration: 6min
completed: 2026-05-31
---

# Phase 22 Plan 03: Writer Parent Index + Processor-No-Create + Writer/Service Prefix De-config Summary

**On the WebApi writer/service side: the Start writer now SADDs each workflow id into the shared parent-index SET and creates ZERO processor keys, cleanup SREMs the workflow from the index (hoisted above the absent-root early-return for idempotent GC), and the configurable `KeyPrefix` was removed from `RedisProjectionOptions`, `BaseApi.Service` appsettings, and every writer/cleanup/service read site — dropping `IOptions<RedisProjectionOptions>` from `OrchestrationService` + `RedisL2Cleanup` ctors and the matching DI factory arg in lockstep.**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-05-31T08:55:08Z
- **Completed:** 2026-05-31T09:00:45Z
- **Tasks:** 3
- **Files modified:** 9 (6 production/config + 3 dependent test files via Rule 3)

## Accomplishments
- **L2IDX-01 (writer SADD):** `RedisProjectionWriter.UpsertAsync` adds `batch.SetAddAsync(RedisProjectionKeys.ParentIndex(), wf.Id.ToString("D"))` to the existing pipeline batch alongside the root/step writes — idempotent on re-Start (a SET add of an already-present member is a no-op).
- **PROC-NOCREATE-01 (writer no processor keys):** the entire per-processor `StringSetAsync(Processor(...))` TTL loop (plus the `TimeSpan? ttl = ...` line) was deleted. The writer now writes only the root key + per-step keys (both no-TTL) + the parent-index SADD. Zero `skp:{procId}` keys are created.
- **L2IDX-01 / D-10 (cleanup SREM, hoisted):** `RedisL2Cleanup.StopCleanupAsync` issues `db.SetRemoveAsync(RedisProjectionKeys.ParentIndex(), workflowId.ToString("D"))` immediately after `GetDatabase()` and BEFORE the `if (rootJson.IsNullOrEmpty) return;` early-return — so the workflow leaves the index even when the root is already gone (idempotent GC). No double-SREM in the delete batch.
- **L2PREFIX-01 (prefix de-config):** deleted the `KeyPrefix` property from `RedisProjectionOptions` (kept `ProcessorKeyTtlDays` + `Serialization`), removed the `"KeyPrefix": "skp:"` line from `BaseApi.Service/appsettings.json`, and switched every writer/cleanup/service key builder to the no-prefix Plan-01 signatures (`Root(wf.Id)`, `Step(wf.Id, step.Id)`, `Root(id)`).
- **PATTERNS caveat 3 (coupled DI edit):** `_keyPrefix` was the only `IOptions<RedisProjectionOptions>` consumer in `OrchestrationService`, so the ctor param + field + assignment AND the matching `sp.GetRequiredService<IOptions<RedisProjectionOptions>>()` factory arg were removed together; `RedisL2Cleanup` likewise dropped its now-unused `IOptions` param + `_options` field (D-12 — only the multiplexer remains). Unused `using Microsoft.Extensions.Options;` / `using BaseApi.Core.Configuration;` removed where orphaned (TreatWarningsAsErrors).
- **Builds:** `BaseApi.Core`, `BaseApi.Service`, and the full `BaseApi.Tests` project all build 0 Warnings / 0 Errors. The prior-wave 7 `CS1501` writer/service errors are resolved; the whole solution compiles again.

## Task Commits

Each task was committed atomically:

1. **Task 1: Remove KeyPrefix from RedisProjectionOptions + BaseApi.Service appsettings** - `f291e14` (refactor)
2. **Task 2: Writer — SADD parent index + remove processor-write loop + drop prefix reads** - `a26e78e` (feat)
3. **Task 3: Cleanup SREM (hoisted) + service/DI prefix + IOptions coupling (+ Rule 3 test fixes)** - `f93c786` (refactor)

## Files Created/Modified
- `src/BaseApi.Core/Configuration/RedisProjectionOptions.cs` - Deleted the `KeyPrefix` property + its XML doc; rewrote the class doc to note the prefix is now a const (binds only `ProcessorKeyTtlDays` + `Serialization`).
- `src/BaseApi.Service/appsettings.json` - Removed the `"KeyPrefix": "skp:"` line from the `Redis` section (kept `ProcessorKeyTtlDays` + `Serialization`; valid JSON).
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` - Added the `ParentIndex()` SADD; deleted the per-processor write loop + `ttl`; dropped `var prefix`/`var days`; no-prefix root/step builders; rewrote the class XML doc.
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs` - Hoisted `SetRemoveAsync(ParentIndex(), ...)` above the absent-root return; dropped the `IOptions` ctor param + `_options` field + the `Options`/`Configuration` usings; no-prefix Root/Step builders; updated the class XML doc.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` - Removed the `_keyPrefix` field + its ctor param/assignment; `StopAsync` now calls `Root(id)`; dropped the unused `Options`/`Configuration` usings.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` - Dropped the `IOptions<RedisProjectionOptions>` factory arg (ctor/arg order match preserved); dropped the unused `Options`/`Configuration` usings. Validator registrations untouched (Plan 04 owns the ProcessorLivenessValidator wiring).
- `tests/BaseApi.Tests/Features/Orchestration/Projection/RedisProjectionKeysTests.cs` - (Rule 3) rewrote to the no-prefix builders, added a `ParentIndex()` golden, dropped the per-class-prefix fact, bumped the Phase trait 15 -> 22.
- `tests/BaseApi.Tests/Composition/RedisProjectionOptionsBindingFacts.cs` - (Rule 3) removed the two `KeyPrefix` binding facts; kept the `Serialization.JsonOptions` facts; updated the class doc.
- `tests/BaseApi.Tests/Orchestration/OrchestrationServicePublishTests.cs` - (Rule 3) removed the `Options()` helper + the now-orphan `Prefix` const + the 13th ctor arg + the unused `Options`/`Configuration` usings, matching the new 12-arg `OrchestrationService` ctor.

## Decisions Made
- None beyond the plan + the PATTERNS caveats it cites — the SREM-hoist (caveat 2), the coupled ctor/DI edit (caveat 3), and dropping `RedisL2Cleanup`'s now-unused `IOptions` (D-12) were all explicitly authorized by the plan's Task 3 action notes.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Updated three dependent test files broken by the contract change**
- **Found during:** Task 3 (full-solution build verification)
- **Issue:** Removing the `KeyPrefix` property and the 13th (`IOptions`) ctor arg broke compilation in `RedisProjectionOptionsBindingFacts.cs` (read `opts.Value.KeyPrefix` + bound `Redis:KeyPrefix`) and `OrchestrationServicePublishTests.cs` (built the service with a 13-arg ctor + a `RedisProjectionOptions { KeyPrefix = ... }`). Additionally `RedisProjectionKeysTests.cs` (NOT touched by Plan 01, which only updated the sibling `L2ProjectionKeysTests.cs`) still called the old multi-arg `Root/Step/Processor("skp:", ...)` builders — a leftover from Plan 01's Wave-1 contract change. All three block the full-solution/test build that this plan's `<verification>` requires.
- **Fix:** `RedisProjectionKeysTests` rewritten to the no-prefix builders + a `ParentIndex()` golden, per-class-prefix fact dropped, Phase trait bumped 15 -> 22 (mirrors Plan 01's `L2ProjectionKeysTests` treatment). `RedisProjectionOptionsBindingFacts` lost its two `KeyPrefix` facts (per PATTERNS golden-update table) and kept the `Serialization.JsonOptions` facts. `OrchestrationServicePublishTests` dropped the `Options()` helper, the orphan `Prefix` const, the 13th ctor arg, and the now-unused `Options`/`Configuration` usings.
- **Files modified:** `tests/BaseApi.Tests/Features/Orchestration/Projection/RedisProjectionKeysTests.cs`, `tests/BaseApi.Tests/Composition/RedisProjectionOptionsBindingFacts.cs`, `tests/BaseApi.Tests/Orchestration/OrchestrationServicePublishTests.cs`
- **Commit:** `f93c786`

## Issues Encountered

**The writer KEEPS its `IOptions<RedisProjectionOptions>` injection while reading neither field.** After Task 2 removed both `_options.KeyPrefix` and `_options.ProcessorKeyTtlDays` reads, `RedisProjectionWriter._options` is assigned in the ctor but never read. The plan objective explicitly mandates keeping the injection (options still owns `ProcessorKeyTtlDays` + `Serialization` for future use), and a private readonly field that is *assigned* (not merely declared) does NOT trip CS0169/IDE0052 under `TreatWarningsAsErrors` — `BaseApi.Service` built 0/0. No action needed.

## Deferred Issues

The PATTERNS golden-assertion enhancements for `RedisProjectionWriterFacts` (assert `SMEMBERS` of `ParentIndex()` contains `wf.Id:D` + zero processor keys), `StopCleanupFacts` (assert SREM observable), and `GateNoWriteFacts` (add a `processorLiveness` 422 arm) are NOT in this plan's scope — they depend on the new `ProcessorLivenessValidator` + the `processorLiveness` gate owned by **Plan 04**, and the test-isolation rewrite (D-20..D-23). Those three test files already compile against the no-prefix builders today (full test project builds 0/0); their behavioral assertions are tightened in Plan 04. No build blocker carried forward.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- The writer-side parent index (SADD), the processor-no-create boundary, and writer/service prefix de-config are all in place; the full solution + test project build clean again (the prior-wave CS1501 hand-off is resolved).
- Plan 04 (Wave 3) edits `OrchestrationService.cs` + `OrchestrationServiceCollectionExtensions.cs` again to wire the new `ProcessorLivenessValidator` (ctor param + DI factory arg + `AddScoped`), adds the `processorLiveness` gate + `ProcessorLivenessFacts`, and lands the golden-assertion + test-isolation updates flagged above.
- No blockers.

## Self-Check: PASSED

All 9 modified files present on disk; all 3 task commits (`f291e14`, `a26e78e`, `f93c786`) found in git history (verified below).

---
*Phase: 22-l2-root-parent-restructure-processor-self-registration*
*Completed: 2026-05-31*
