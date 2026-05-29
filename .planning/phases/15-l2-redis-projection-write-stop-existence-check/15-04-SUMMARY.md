---
phase: 15-l2-redis-projection-write-stop-existence-check
plan: 04
subsystem: orchestration-l2-projection
tags: [redis, orchestration, start-loop, stop-gate, exists, 422, 500, observ-redis, integration-test]
requires:
  - "IRedisProjectionWriter.UpsertAsync(snapshot, correlationId, ct) — widened signature (Plan 15-02)"
  - "IRedisL2Cleanup.StopCleanupAsync + OrchestrationValidationException.MissingRoots 422 factory (Plan 15-03)"
  - "RedisProjectionKeys.Root + RedisProjectionOptions.KeyPrefix (Plan 15-01)"
  - "IConnectionMultiplexer Singleton + IHttpContextAccessor (Phase 12 / Phase 4)"
  - "CycleDetector / SchemaEdgeValidator / PayloadConfigSchemaValidator locked gate order (Phase 14)"
  - "FallbackExceptionHandler generic-500 + Phase 4 correlationId customizer"
  - "Phase8WebAppFactory.RedisMultiplexer / RedisKeyPrefix + dead-Redis ctor (Phase 12)"
provides:
  - "OrchestrationService.StartAsync — D-07 per-workflow stop-then-build loop (404 gate, correlationId once, pre-clean -> LoadL1([id]) -> 3 validators -> UpsertAsync, snapshot disposed each iteration)"
  - "OrchestrationService.StopAsync — D-04/D-06 Redis EXISTS gate (collect ALL missing -> 422 no-delete) then per-workflow cleanup -> 204; repeat -> 422"
  - "OBSERV-REDIS-03 op-name seam — RedisException tagged Data[redisOp] (UpsertAsync/KeyExistsAsync); FallbackExceptionHandler surfaces it as Extensions[redisOp]"
  - "OrchestrationController — 422/500 ProducesResponseType on Start+Stop (ORCH-START-08)"
affects:
  - "Phase 16 (asserts the live Start->L2->Stop semantics against real Redis)"
  - "Plan 15-05 (negative-grep: no OTel-Redis instrumentation; OBSERV-REDIS-01)"
tech-stack:
  added: []
  patterns:
    - "Per-workflow loop with using-declaration snapshot disposal each iteration (L1 cleanup on success AND throw)"
    - "exception.Data[redisOp] tag + handler-read seam to surface a fixed op-name literal in a 500 without leaking connection string/stack"
    - "Collect-then-decide EXISTS gate: Task.WhenAll the KeyExistsAsync batch, gather ALL missing (not fail-fast), 422 before any deletion"
    - "Deterministic Redis-fault facts via ConfigureTestServices test doubles (throwing IRedisProjectionWriter / NSubstitute IConnectionMultiplexer) instead of a flaky dead-TCP endpoint whose backlog swallows the op"
    - "Update-in-place of obsolete Phase-N facts when a later milestone changes the endpoint contract (Stop 404 -> 422)"
key-files:
  created:
    - tests/BaseApi.Tests/Features/Orchestration/StartLoopFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/StopGateFacts.cs
  modified:
    - src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs
    - src/BaseApi.Core/Exceptions/Handlers/FallbackExceptionHandler.cs
    - tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs
decisions:
  - "OBSERV-REDIS-03 chose the plan's PREFERRED seam: a 2-line FallbackExceptionHandler edit reading exception.Data[redisOp] into Extensions[redisOp]; the service tags the RedisException with the op-name literal. No second exception type / handler. Cross-project Core touch documented."
  - "Tagged the Start PRE-CLEAN RedisException with redisOp=UpsertAsync too (Rule 1): the pre-clean is the first half of delete-then-write and on a dead connection it throws BEFORE the writer is reached; without this the Start-down 500 would bubble untagged and report no op name."
  - "Injected IOptions<RedisProjectionOptions> into OrchestrationService (cached _keyPrefix) so the Stop EXISTS gate builds the same RedisProjectionKeys.Root keys the writer uses — added the matching factory line (kept ctor + factory in sync, plan-anticipated)."
  - "Dead-Redis facts use ConfigureTestServices test doubles (throwing writer + NSubstitute multiplexer) rather than the abortConnect=false dead-port factory — the dead-port multiplexer's backlog completed the SET/EXISTS as no-ops (no throw), so the network approach could not deterministically exercise the 500 op-name path."
metrics:
  duration: ~15min
  completed: 2026-05-29
  tasks: 3
  files: 7
---

# Phase 15 Plan 04: Per-Workflow Start Loop + Redis Stop Gate/Cleanup Summary

Connected the Plan-02 writer and Plan-03 cleanup into the live HTTP pipeline. `OrchestrationService.StartAsync` became the D-07 per-workflow stop-then-build loop (upfront 404 existence gate → resolve `X-Correlation-Id` once → for each workflow: tolerant pre-clean → per-workflow `LoadL1Async([id])` → cycle/schema-edge/payload validators in the LOCKED Phase 14 order → `UpsertAsync`, with the snapshot disposed each iteration). `StopAsync` became the D-04/D-06 Redis EXISTS gate: batch `KeyExistsAsync` over the root keys, collect ALL missing (not fail-fast), 422 `MissingRoots` with NO deletion if any are absent, else per-workflow tolerant cleanup → 204 (repeat Stop → 422, non-idempotent). A Redis fault on either path is tagged with the offending op name (`UpsertAsync`/`KeyExistsAsync`) and surfaced in the 500 body via a 2-line `FallbackExceptionHandler` seam (OBSERV-REDIS-03) — no connection string or stack leaks.

## What Was Built

- **DI factory** (`OrchestrationServiceCollectionExtensions`) — added `IRedisL2Cleanup`, `IHttpContextAccessor`, `IConnectionMultiplexer`, and `IOptions<RedisProjectionOptions>` to the internal-ctor `OrchestrationService` factory closure (`IRedisL2Cleanup -> RedisL2Cleanup` was already registered by Plan 03). New usings: `BaseApi.Core.Configuration`, `Microsoft.AspNetCore.Http`, `Microsoft.Extensions.Options`, `StackExchange.Redis`. The existing `AddExceptionHandler<OrchestrationValidationExceptionHandler>` (which claims the Plan-03 `MissingRoots` 422) was kept.
- **`OrchestrationService`** — new fields/ctor params (`_cleanup`, `_httpContextAccessor`, `_multiplexer`, cached `_keyPrefix` from options) with the `?? throw ArgumentNullException` idiom.
  - **StartAsync**: `ExistenceCheckAsync` first (404 before any mutation); `correlationId = HttpContext?.Items["CorrelationId"] as string ?? string.Empty` resolved ONCE; `foreach` workflow → pre-clean (`StopCleanupAsync`) → `using var snapshot = LoadL1Async(new[]{ id })` → 3 validators → `UpsertAsync(snapshot, correlationId, ct)`. Both the pre-clean and the writer call are wrapped in `catch (RedisException) { ex.Data["redisOp"] = "UpsertAsync"; throw; }`.
  - **StopAsync**: null-body guard + `_idsValidator.ValidateAndThrowAsync` (mirrors `ExistenceCheckAsync`'s pre-check, T-15-12), then `var db = _multiplexer.GetDatabase()`, batch `KeyExistsAsync(RedisProjectionKeys.Root(_keyPrefix, id))` collected and `Task.WhenAll`-awaited inside `catch (RedisException) { ex.Data["redisOp"] = "KeyExistsAsync"; throw; }`, collect ALL ids where exists==false; `missing.Count > 0` → `throw MissingRoots(missing)` (422, no delete); else `foreach` → `StopCleanupAsync`. `ExistenceCheckAsync` body left UNCHANGED (still the Start 404 gate).
  - XML docs rewritten to describe the per-workflow loop + Redis gate/cleanup (the old "Phase 15 swaps StopAsync's body" placeholder text is gone).
- **`FallbackExceptionHandler`** (Core, cross-project touch) — after building the generic 500 ProblemDetails, reads `exception.Data["redisOp"]` (a fixed non-sensitive literal) into `problem.Extensions["redisOp"]`. The detail stays generic; correlationId + instance are added by the Phase 4 customizer.
- **`OrchestrationController`** — `[ProducesResponseType(typeof(ProblemDetails), 422)]` + `[ProducesResponseType(typeof(ProblemDetails), 500)]` added to BOTH `Start` and `Stop` (existing 204/400/404 kept). No body change.
- **`StartLoopFacts`** (3 facts) — `Start_Returns204` (valid graph → 204; root-key `correlationId` == sent `X-Correlation-Id`, ORCH-START-07), `ReStart_Removes_Orphan_Step` (A→B graph Started, A→B edge removed via Step PUT, re-Start → B's orphaned per-step key GONE, ORCH-START-05), `Start_RedisDown_500` (throwing-writer double → 500 + `redisOp`=="UpsertAsync" + correlationId, no `localhost`/exception-type leak).
- **`StopGateFacts`** (4 facts) — `Stop_AllExist_204` (Start then Stop → 204; root+step gone, processor retained — ORCH-STOP-04 rev), `Stop_Missing_422_NoDelete` (Stop [started, never-started] → 422 listing the missing id; started keys survive), `Stop_Repeat_422` (Start→Stop(204)→Stop→422), `Stop_RedisDown_500` (NSubstitute `IConnectionMultiplexer` whose `KeyExistsAsync` throws → 500 + `redisOp`=="KeyExistsAsync" + correlationId, no leak).

## Tasks Completed

| Task | Name | Commit | Files |
| ---- | ---- | ------ | ----- |
| 1 | Wire DI (cleanup + accessor + multiplexer + options) | 8759964 | OrchestrationServiceCollectionExtensions.cs |
| 2 | Per-workflow Start loop + Redis Stop gate/cleanup + op-name 500 | dd2d132 | OrchestrationService.cs, FallbackExceptionHandler.cs |
| 3 | Controller 422/500 + Start/Stop integration facts (+Rule 1 fixes) | d44fe27 | OrchestrationController.cs, OrchestrationService.cs, StartLoopFacts.cs, StopGateFacts.cs, StopOrchestrationFacts.cs |

## Verification Results

- `dotnet build src/BaseApi.Service -c Release` → succeeded, **0 warnings, 0 errors** (ValidateOnBuild resolves all new ctor deps).
- `dotnet build SK_P.sln -c Debug` → 0 warnings, 0 errors.
- Orchestration slice (`--filter-class "*Orchestration*"`) → **53/53 GREEN** (3.7s) including the 7 new Plan-04 facts and the 3 updated `StopOrchestrationFacts`.
- StartLoopFacts + StopGateFacts in isolation → **7/7 GREEN** (2.9s) against live compose Redis (`localhost:6380`).
- Full `dotnet test` suite → **Passed: 221, Failed: 0, Skipped: 0** (3m06s) — 214 prior (210 P02 + 4 P03) + 7 new.
- `rg "IServer|\.Keys\(|\bKEYS\b" OrchestrationService.cs` → **0 matches** (Stop gate uses `KeyExistsAsync`, not enumeration — L2-PROJECT-07).
- `ExistenceCheckAsync` body unchanged (Start 404 gate preserved; still called by `StartAsync`, no longer by `StopAsync`).
- `OrchestrationController` contains `Status422UnprocessableEntity` + `Status500InternalServerError` 4× total (2 per action).

Note: the runner is Microsoft.Testing.Platform; `dotnet test --filter` is ignored (MTP0001), so per-class runs use the test executable's native `--filter-class` flag (precedent: Plans 15-02 / 15-03 SUMMARY).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Start pre-clean Redis fault bubbled untagged**
- **Found during:** Task 3 (`Start_RedisDown_500`).
- **Issue:** The plan wrapped only `UpsertAsync` with the `redisOp` tag. But the per-workflow pre-clean (`StopCleanupAsync`, a `StringGetAsync`) runs BEFORE the writer; on a genuine Redis connection fault it throws there, untagged, so the 500 body would carry no `redisOp` — failing OBSERV-REDIS-03 for the Start-down case the plan itself specifies as `redisOp=="UpsertAsync"`.
- **Fix:** Wrapped the pre-clean call in `catch (RedisException) { ex.Data["redisOp"] = "UpsertAsync"; throw; }` too — the pre-clean is the first half of delete-then-write, so the Start L2-write path reports a single stable op name regardless of which Redis call faults first. (A missing key is still tolerated INSIDE the cleanup routine; only a thrown `RedisException` is tagged.)
- **Files modified:** OrchestrationService.cs.
- **Commit:** d44fe27.

**2. [Rule 1 - Obsolete tests] Phase-9 StopOrchestrationFacts asserted the superseded 404 contract**
- **Found during:** Task 3 (full Orchestration-slice run — 3 `StopOrchestrationFacts` failed: expected 404/204 Postgres-existence behavior).
- **Issue:** Those Phase 9 facts encode Stop = Postgres existence check (204 valid / 404 missing). The v3.3.0 milestone (and this plan) deliberately changed Stop to a Redis EXISTS gate (204 if all L2 roots exist / 422 if any missing). Leaving them would assert behavior the milestone removed.
- **Fix:** Retargeted the three facts to the new gate semantics — `Stop_Returns204_AndEmptyBody_WhenAllRootsExist` (Start-then-Stop → 204), `Stop_Returns422_WhenAnyWorkflowRootMissing`, `Stop_Returns422_WithFullMissingList_WhenMultipleWorkflowRootsMissing`. Retrait to Phase 15; doc-comment updated to explain the contract change. Full gate coverage (processor retention, repeat-422, Redis-down 500) lives in the new `StopGateFacts`.
- **Files modified:** StopOrchestrationFacts.cs.
- **Commit:** d44fe27.

### Plan-offered choice resolved

- **OBSERV-REDIS-03 op-name seam (plan offered two paths):** Chose the plan's PREFERRED 2-line Core edit — `FallbackExceptionHandler` reads `exception.Data["redisOp"]` into `Extensions["redisOp"]`; the service tags the `RedisException` with the op-name literal. No second exception type, no extra handler registration. The cross-project Core touch (`FallbackExceptionHandler.cs`) is recorded in `key-files.modified` per the plan's instruction.
- **Dead-Redis test strategy (Claude's discretion):** The plan suggested the existing `abortConnect=false` dead-port factory (`HealthDeadRedisFixture` analog). Empirically that multiplexer's backlog completed `StringSetAsync`/`KeyExistsAsync` as no-ops (Start returned 204; Stop's gate saw the key as absent → 422) — it never threw a `RedisException`, so the network approach could NOT exercise the 500 op-name path deterministically. Switched to `ConfigureTestServices` test doubles: a throwing `IRedisProjectionWriter` for Start and an NSubstitute `IConnectionMultiplexer` (whose `KeyExistsAsync` throws `RedisConnectionException`) for Stop. This pins the exact catch-and-tag path with zero timing flakiness (precedent: `StartCleanupFacts.ThrowingRedisProjectionWriter`).

## Threat Surface

All five register threats from the plan's `<threat_model>` are mitigated and proven:
- **T-15-12** (malformed/empty id list) — `_idsValidator.ValidateAndThrowAsync` + null-body guard run before any Redis/Postgres touch on BOTH Start (via `ExistenceCheckAsync`) and Stop (inline). Existing 400 facts in StartOrchestrationFacts cover the rule violations.
- **T-15-13** (500 leaking connection string/stack) — `FallbackExceptionHandler` emits a generic detail; only `redisOp` (a fixed literal) + correlationId reach Extensions. Both Redis-down facts assert `redisOp` present AND no `localhost`/`RedisConnectionException` substring in the body.
- **T-15-14** (X-Correlation-Id injection) — read-only; the header is ASCII-sanitized (≤128) at `CorrelationIdMiddleware` before `HttpContext.Items`; the service only reads it.
- **T-15-15** (Redis ops uncorrelated) — correlationId flows via the Phase 4 MEL scope; no new OTel-Redis instrumentation added (OBSERV-REDIS-01 negative-grep is Plan 05's gate).
- **T-15-16** (unbounded Start/Stop work) — accepted; bounded by the requested id-list size + each workflow's reachable graph; no distributed lock (ORCH-START-06 last-write-wins).

No NEW security-relevant surface beyond the plan's threat model.

## Notes

- `IConnectionMultiplexer` is Singleton, `IHttpContextAccessor` already registered (AuditInterceptor consumes it) — both resolve cleanly into the Scoped `OrchestrationService` factory (no captive-dependency bug; mirrors the Plan-03 cleanup registration rationale).
- The `.planning/phases/*` git-status churn (mass D/??) is pre-existing and unrelated to this plan; only the 7 source/test files above were staged across the three task commits.

## Self-Check: PASSED

- Created files exist: `tests/BaseApi.Tests/Features/Orchestration/StartLoopFacts.cs`, `tests/BaseApi.Tests/Features/Orchestration/StopGateFacts.cs` — FOUND.
- Modified files contain expected edits (per-workflow loop + Redis gate; factory closure deps; controller 422/500; FallbackExceptionHandler redisOp read; retargeted StopOrchestrationFacts) — verified via build + greps + GREEN test run.
- All task commits exist in git log: 8759964, dd2d132, d44fe27 — FOUND.
