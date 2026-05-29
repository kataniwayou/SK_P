---
phase: 15-l2-redis-projection-write-stop-existence-check
plan: 03
subsystem: orchestration-l2-projection
tags: [redis, projection, cleanup, bfs, get-and-follow, 422, integration-test]
requires:
  - RedisProjectionKeys + WorkflowRootProjection/StepProjection records (Plan 15-01)
  - RedisProjectionOptions.KeyPrefix (Phase 12 / Plan 15-01)
  - IConnectionMultiplexer Singleton (Phase 12)
  - OrchestrationValidationException + OrchestrationValidationExceptionHandler (Phase 14 — reused for the 422)
  - Phase8WebAppFactory.RedisMultiplexer / RedisKeyPrefix + RedisFixture SCAN+DEL teardown (Phase 12)
  - WorkflowGraphLoader BFS visited-List wave-loop (Phase 13 — mirrored in reverse over Redis)
provides:
  - "IRedisL2Cleanup.StopCleanupAsync(workflowId, ct) — shared, always-tolerant traverse-and-delete contract (D-06/D-07)"
  - "RedisL2Cleanup — cycle-safe GET-and-follow BFS + collect-then-batch-delete (root + reachable per-step keys; never processor keys)"
  - "OrchestrationValidationException.MissingRoots(missing) — 422 stopMissingRoots factory carrying the full missing-id set (D-04)"
  - "MissingRootsOffending record"
affects:
  - "Plan 15-04 wires the two callers: StopAsync (gated, after the D-04 all-exist check) + Start pre-clean (tolerant)"
  - "Plan 15-04 throws OrchestrationValidationException.MissingRoots from StopAsync's existence gate"
  - "Phase 16 (asserts Stop semantics against real Redis)"
tech-stack:
  added: []
  patterns:
    - "GET-and-follow BFS over Redis (StringGetAsync each step key, read nextStepIds off the deserialized StepProjection) — reverse-mirror of the writer's key layout"
    - "visited List<Guid> (NOT hash-set) cycle guard — mirror of WorkflowGraphLoader (REQ convention; terminates on cycles)"
    - "Collect-then-delete: traverse fully, then one CreateBatch deleting root unconditionally + per-step KeyDeleteAsync(RedisKey[]) only when count>0 (Pitfall 7; array delete auto-selects UNLINK)"
    - "sealed partial class to house a per-concern static factory in its own file while reusing the single existing exception handler"
    - "Comment-rephrase to satisfy negative-grep acceptance criteria (precedent: 01/02/06/08 plans)"
    - "Seed L2 keys directly via the factory's RedisMultiplexer; resolve internal IRedisL2Cleanup from a DI scope (InternalsVisibleTo)"
key-files:
  created:
    - src/BaseApi.Service/Features/Orchestration/Projection/IRedisL2Cleanup.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationStopException.cs
    - tests/BaseApi.Tests/Features/Orchestration/StopCleanupFacts.cs
  modified:
    - src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs
decisions:
  - "Chose the plan's PREFERRED factory-on-existing-exception path: OrchestrationValidationException made `partial`, MissingRoots factory + MissingRootsOffending housed in OrchestrationStopException.cs — reuses the one 422 handler (no second AddExceptionHandler registration), satisfies the artifact contract (file exists at the listed path, contains 'missing'), and keeps the gate=stopMissingRoots discriminator distinct from the Phase 14 build gates"
  - "Registered IRedisL2Cleanup -> RedisL2Cleanup in AddOrchestrationFeature THIS plan (not Plan 04) because Plan 03's StopCleanupFacts resolve it from DI; Plan 04 still owns wiring the two CALLERS (Rule 3 unblock). Scoped to match the other orchestration seams; the Singleton multiplexer it depends on creates no captive bug"
  - "StopCleanupAsync threads `ct` for interface symmetry only — SE.Redis IDatabase async ops are not CancellationToken-bearing (documented in the plan)"
metrics:
  duration: ~5min
  completed: 2026-05-29
  tasks: 3
  files: 6
---

# Phase 15 Plan 03: L2 Cleanup Routine + Stop 422 Exception Summary

Created the shared, always-tolerant `RedisL2Cleanup` routine (D-06/D-07) — for one workflowId it GETs the root key, cycle-safe BFS-walks the step graph in Redis via GET-and-follow (reading `nextStepIds` off each deserialized `StepProjection`), collects every reachable per-step key, then batch-deletes the root + per-step keys, NEVER processor keys. An absent root is a no-op; a dangling step is skipped. Also added the `OrchestrationValidationException.MissingRoots` factory (gate=`stopMissingRoots`) that produces the 422 listing the full missing-workflowIds set (D-04), reusing the existing Phase 14 validation-exception handler.

## What Was Built

- **`IRedisL2Cleanup`** — `internal interface` with `Task StopCleanupAsync(Guid workflowId, CancellationToken ct)`. XML doc pins the always-tolerant contract: absent root → no-op; the 422 gate lives in `StopAsync`, not here (Pitfall D). Two future callers (Plan 04): Stop (gated) + Start pre-clean (tolerant; GC for shrunk graphs, ORCH-START-05).
- **`RedisL2Cleanup`** — `internal sealed class`, ctor injects `IConnectionMultiplexer` + `IOptions<RedisProjectionOptions>` (KeyPrefix) with null guards. `StopCleanupAsync` mirrors the `WorkflowGraphLoader` iterative wave-BFS but walks Redis: `visited` is a `List<Guid>` (plain list, not a hash-set — terminates on cycles), each wave GETs `{prefix}{wf}:{stepId}`, skips dangling step keys (`IsNullOrEmpty → continue`), collects the rest, and follows the deserialized `StepProjection.NextStepIds` for the next wave (multi-child fan-out, `Distinct()`-deduped). After the full traverse, one `CreateBatch` deletes the root key unconditionally + the per-step keys via a single `KeyDeleteAsync(RedisKey[])` (auto-UNLINK) only when at least one was collected (Pitfall 7). No keyspace enumeration; no processor-key formatter ever constructed.
- **`OrchestrationValidationException.MissingRoots(IReadOnlyList<Guid> missing)`** — static factory (gate=`stopMissingRoots`, title "Workflow root keys not found in L2", detail `string.Join(", ", missing)`) + `MissingRootsOffending(missing)` record. Housed in `OrchestrationStopException.cs` as a `partial` extension of the now-`sealed partial` `OrchestrationValidationException`, so it reuses the existing `OrchestrationValidationExceptionHandler` (→ 422 + RFC 7807; Phase 4 customizer adds correlationId + instance) with no second handler.
- **DI registration** — `services.AddScoped<IRedisL2Cleanup, RedisL2Cleanup>()` added to `AddOrchestrationFeature` next to the writer registration.
- **`StopCleanupFacts`** (4 integration facts, `[Trait("Phase","15")]`, `IClassFixture<Phase8WebAppFactory>`, all pass `TestContext.Current.CancellationToken`): seed L2 keys directly via the factory's `RedisMultiplexer` + `RedisKeyPrefix`, resolve internal `IRedisL2Cleanup` from a DI scope:
  - `Stop_Deletes_Root_Step_Keeps_Processor` — root + 2 linear steps + 1 processor key; asserts root + both step keys gone, processor key retained.
  - `Stop_CyclicGraph_Terminates` — A→B→A cycle reachable from root; asserts the call returns (no hang) and deletes A, B, root.
  - `Stop_DanglingStep_Skipped` — root references a step key never written; asserts root deleted, no throw, missing step skipped.
  - `Stop_AbsentRoot_NoOp` — `StopCleanupAsync(randomGuid)`; asserts no throw, nothing deleted.

## Tasks Completed

| Task | Name | Commit | Files |
| ---- | ---- | ------ | ----- |
| 1 | MissingRoots 422 factory (D-04) | 623392a | OrchestrationStopException.cs, OrchestrationValidationException.cs |
| 2 | IRedisL2Cleanup + RedisL2Cleanup (GET-and-follow traverse+delete) | f65fbfe | IRedisL2Cleanup.cs, RedisL2Cleanup.cs, OrchestrationServiceCollectionExtensions.cs |
| 3 | Real-Redis StopCleanupFacts | 2326d58 | StopCleanupFacts.cs |

## Verification Results

- `dotnet build src/BaseApi.Service -c Release` → succeeded, **0 warnings, 0 errors**.
- `dotnet build tests/BaseApi.Tests -c Debug` → succeeded, 0 warnings, 0 errors.
- `StopCleanupFacts` via the MTP `--filter-class` executable flag → **4/4 GREEN** against the live compose Redis (`localhost:6380`), duration ~2.3s.
- `rg "IServer|\.Keys\(|\bKEYS\b|RedisProjectionKeys.Processor" RedisL2Cleanup.cs` → **0 matches** (L2-PROJECT-07; no enumeration, never deletes processor keys).
- `rg "HashSet|Processor\(" RedisL2Cleanup.cs` → 0 matches (List mirror; no processor-key formatter).
- `rg "MissingRoots|stopMissingRoots" src/.../Orchestration/` → matches present (the 422 path exists).
- `rg` confirms `RedisL2Cleanup.cs` contains `new List<Guid>()`, `StringGetAsync`, `Deserialize<StepProjection>`, `KeyDeleteAsync` (10 token occurrences across the four).

Note: the runner is Microsoft.Testing.Platform; `dotnet test --filter` is ignored (MTP0001), so the per-class run used the test executable's native `--filter-class` flag (precedent: Plan 15-02 SUMMARY).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Registered IRedisL2Cleanup in DI this plan (plan assigned it to Plan 04)**
- **Found during:** Task 3 (StopCleanupFacts resolve `IRedisL2Cleanup` from `_factory.Services.CreateScope()`).
- **Issue:** The plan's `<interfaces>` note says "Plan 04 adds the registration; this plan only creates the types." But Task 3's own acceptance requires resolving `IRedisL2Cleanup` from the DI scope, which is impossible without a registration.
- **Fix:** Added `services.AddScoped<IRedisL2Cleanup, RedisL2Cleanup>()` to `AddOrchestrationFeature` (next to the writer). Plan 04 still owns wiring the two CALLERS (StopAsync gate + Start pre-clean). The registration is idempotent and harmless to Plan 04. Scoped lifetime matches the sibling orchestration seams; the Singleton multiplexer dependency is no captive bug.
- **Files modified:** OrchestrationServiceCollectionExtensions.cs.
- **Commit:** f65fbfe.

**2. [Rule 3 - Plan-internal consistency] Rephrased doc-comments to satisfy negative-grep**
- **Found during:** Task 2 verification.
- **Issue:** Acceptance + the plan `<verification>` block require `rg "IServer|\.Keys\(|\bKEYS\b" RedisL2Cleanup.cs` and `rg "HashSet"` to return 0, but my XML/inline comments contained the literal tokens `IServer.Keys()`, `KEYS`, and `HashSet` (used to explain what the routine deliberately avoids / mirrors).
- **Fix:** Rephrased to "never enumerates the keyspace via a server scan or wildcard match" and "a plain list, NOT a hash-set". Preserves the educational intent (L2-PROJECT-07 reference, loader-mirror note) while satisfying grep-empty. Established precedent (Plans 01/02/06/08).
- **Files modified:** RedisL2Cleanup.cs (comments only; no behavior change).
- **Commit:** f65fbfe.

### Plan-offered choice resolved

- **Stop 422 seam (PATTERNS.md offered two paths):** Chose the plan's stated PREFERRED path — factory-on-existing-exception. `OrchestrationValidationException` is now `sealed partial`; the `MissingRoots` factory + `MissingRootsOffending` record live in `OrchestrationStopException.cs` (a `partial` of the same class). This honors BOTH the preferred path (one handler, least new code) AND the plan's `must_haves.artifacts` contract (a file at `OrchestrationStopException.cs` providing the 422 missing-roots path and containing "missing"). No sibling `OrchestrationStopException` exception type was created — that would have required a second handler registration the plan explicitly discouraged.

## Threat Surface

All four register threats from the plan's `<threat_model>` are mitigated and proven:
- **T-15-08** (DoS via key enumeration) — GET-and-follow only; negative-grep `IServer`/`KEYS`/`.Keys(` → 0; walk bounded by reachable step set.
- **T-15-09** (cyclic-graph runaway) — `visited` List guard; `Stop_CyclicGraph_Terminates` proves termination.
- **T-15-10** (processor-key deletion) — no `RedisProjectionKeys.Processor(` construction (grep 0); `Stop_Deletes_Root_Step_Keeps_Processor` asserts processor retention.
- **T-15-11** (dangling-step abort) — `if (stepJson.IsNullOrEmpty) continue;`; `Stop_DanglingStep_Skipped` proves skip-and-continue.

No NEW security-relevant surface beyond the plan's threat model.

## Notes

- `StopCleanupAsync` accepts `ct` for interface symmetry; SE.Redis `IDatabase` async ops do not take a CancellationToken, so it is not threaded into the GET/DELETE calls (documented in the plan's action note).
- The `.planning/phases/*` git-status churn (mass D/??) is pre-existing and unrelated to this plan; only the 6 source/test files above were staged across the three task commits.

## Self-Check: PASSED

- Created files exist: IRedisL2Cleanup.cs, RedisL2Cleanup.cs, OrchestrationStopException.cs, StopCleanupFacts.cs — FOUND.
- Modified files contain expected edits (MissingRoots factory; IRedisL2Cleanup registration) — verified via build + greps.
- All task commits exist in git log: 623392a, f65fbfe, 2326d58 — FOUND.
