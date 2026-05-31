---
phase: 24-orchestrator-result-consume-step-advancement
plan: 02
subsystem: webapi-orchestration
tags: [first-win, idempotency, l2-dedup, redis, supersession]
requires:
  - "OrchestrationService Start/Stop L2 write path (Phase 15/22)"
  - "RedisProjectionKeys.Root / L2ProjectionKeys (Phase 21/22)"
provides:
  - "WebApi first-win create-if-absent Start (WEBAPI-SUPPRESS-01)"
  - "WebApi delete-if-present Stop (no 422 on absent root)"
  - "deduped-subset StartOrchestration/StopOrchestration publish"
requires_runtime:
  - "Redis (L2 root key store)"
affects:
  - "src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs"
  - "Orchestrator Start/Stop consumers (Plan 05 — can now be conditionless)"
tech-stack:
  added: []
  patterns:
    - "KeyExistsAsync first-win probe-then-skip (single-owner write path)"
    - "KeyDeleteAsync delete-if-present bool-gated publish"
    - "deduped-subset publish (tracked started/stopped lists)"
key-files:
  created:
    - ".planning/phases/24-orchestrator-result-consume-step-advancement/24-02-SUMMARY.md"
  modified:
    - "src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs"
    - "tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs"
    - "tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs"
    - "tests/BaseApi.Tests/Features/Orchestration/StopGateFacts.cs"
    - "tests/BaseApi.Tests/Features/Orchestration/StopScanFacts.cs"
    - "tests/BaseApi.Tests/Features/Orchestration/IdempotencyFacts.cs"
    - "tests/BaseApi.Tests/Orchestration/OrchestrationServicePublishTests.cs"
decisions:
  - "First-win GUARDS the whole Start write path via KeyExistsAsync-then-skip (RESEARCH A4 fork resolved): the real root JSON is built inside UpsertAsync with a fresh jobId, so a When.NotExists placeholder claim would be defeated by UpsertAsync's overwrite — probe-then-skip keeps the write path single-owner."
  - "Stop replaces the Phase 22 422-on-missing-root gate with per-id KeyDeleteAsync delete-if-present; absent root is a tolerant no-op (NOT 422), making Stop idempotent."
  - "Both publishes carry ONLY the deduped subset (tracked started/stopped lists); an all-duplicate Start and an all-absent Stop emit nothing."
  - "Redis-op tags updated: Start path stays UpsertAsync; Stop path tag changed KeyExistsAsync -> KeyDeleteAsync (OBSERV-REDIS-03)."
metrics:
  duration: ~55min
  completed: 2026-06-01
---

# Phase 24 Plan 02: WebApi First-Win Duplicate-Suppression Summary

First-win idempotent L2-root create/delete in the WebApi (`OrchestrationService`): Start creates the
root-parent `workflowId` only if absent (else skips the entire write path + publish for that id —
no overwrite, no republish), Stop deletes it only if present (else a no-op — NOT 422). This makes the
WebApi the single duplicate-suppression point (D-04) so the orchestrator's Start/Stop consumers can be
made conditionless in Plan 05, and the orchestrator only ever sees genuine deduped transitions.

## What Was Built

### Task 1 — first-win Start + delete-if-present Stop (`feat`, commit `ff2ff6d`)
- **StartAsync:** added a per-`workflowId` `KeyExistsAsync(RedisProjectionKeys.Root(id))` first-win
  probe BEFORE the pre-clean. If the root already exists the whole iteration is skipped (`continue`):
  no pre-clean GC, no L1 load, no validators, no `UpsertAsync`, and the id is excluded from the
  publish. Newly-written ids are tracked in a `started` list; `StartOrchestration` is published with
  `started.ToArray()` (the deduped subset) and only when `started.Count > 0` (an all-duplicate request
  emits nothing). A genuine Redis fault on the probe is tagged `redisOp="UpsertAsync"`.
- **StopAsync:** removed the collect-all-missing `KeyExistsAsync` batch + the
  `OrchestrationValidationException.MissingRoots` 422 throw. Replaced with per-`workflowId`
  `KeyDeleteAsync(RedisProjectionKeys.Root(id))`: an absent root (`false`) is a tolerant no-op
  (`continue`), a present root (`true`) is deleted and its per-step keys GC'd via the tolerant
  `StopCleanupAsync`. Deleted ids are tracked in a `stopped` list; `StopOrchestration` is published
  with `stopped.ToArray()` and only when at least one root was deleted. The Stop-path Redis-op tag
  changed from `"KeyExistsAsync"` to `"KeyDeleteAsync"`.
- L2 key SHAPES unchanged (`RedisProjectionKeys.Root` still forwards to `L2ProjectionKeys.Root`);
  `RedisProjectionWriter.UpsertAsync` internals untouched (the first-win guard lives in the service,
  keeping the writer single-purpose). `OrchestrationValidationException.MissingRoots` factory left in
  place (no longer referenced by the Stop path) — `OrchestrationStopException.cs` unchanged.
- Verify: `dotnet build src/BaseApi.Service/BaseApi.Service.csproj -c Debug` → 0 Warning / 0 Error.

### Task 2 — reconcile WebApi facts to first-win semantics (`test`, commit `58f157a`)
The plan's `files_modified` listed the fact files under `tests/BaseApi.Tests/Orchestration/`, but the
real integration facts live under `tests/BaseApi.Tests/Features/Orchestration/` (only the NSubstitute
publish-harness file is under `tests/BaseApi.Tests/Orchestration/`). Reconciled both locations plus two
adjacent facts that also asserted the superseded behavior:
- **Features/Orchestration/StartOrchestrationFacts.cs:** added `ReStart_With_Present_Root_Does_Not_Overwrite`
  — a re-Start of a present root leaves the root value byte-identical + jobId unchanged (first-win, no
  re-Upsert), against live Redis.
- **Features/Orchestration/StopOrchestrationFacts.cs:** replaced the two 422 facts with delete-if-present
  no-op facts — present root → 204 + deleted; absent root → 204 no-op (NOT 422); repeated Stop →
  idempotent 204.
- **Features/Orchestration/StopGateFacts.cs:** replaced the all-or-nothing 422 gate facts
  (`Stop_Missing_422_NoDelete`, `Stop_Repeat_422`) with per-id delete-if-present
  (`Stop_MixedBatch_DeletesPresent_NoOpAbsent_204`, `Stop_Repeat_Is_Idempotent_204`); the two
  Redis-down 500 facts now assert `redisOp="KeyDeleteAsync"`.
- **Features/Orchestration/IdempotencyFacts.cs:** superseded `ReStart_SameWorkflow_ReflectsSecondWrite`
  (last-write-wins) with `ReStart_SameWorkflow_IsFirstWin_NoOverwrite` — jobId UNCHANGED + the orphan
  step key SURVIVES (no delete-then-write overwrite).
- **Features/Orchestration/StopScanFacts.cs:** comment reconciled to delete-if-present (behavior was
  already 204-deletes-root, no assertion change needed).
- **Orchestration/OrchestrationServicePublishTests.cs:** the in-memory MassTransit-harness publish
  tests now use a first-win-aware mux stub (`MuxWithPresentRoots`) whose `KeyExistsAsync`/`KeyDeleteAsync`
  report the given roots present. Added: Start over an absent root publishes the written id; an
  all-present Start publishes nothing; a mixed Start publishes only the absent ids; Stop over a present
  root publishes, over an absent root publishes nothing. The MSG-WEBAPI-03 propagation +
  FallbackExceptionHandler 500 facts are kept.
- Verify: `dotnet test --filter "FullyQualifiedName~Orchestration"` → Passed 99, Failed 0 (live Redis
  stack up; ~110s). The `Features.Orchestration` slice alone is 60/60; the publish harness is 7/7.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Plan `files_modified` paths were wrong (and one file BOM-encoded)**
- **Found during:** Task 2
- **Issue:** the plan listed `tests/BaseApi.Tests/Orchestration/{StartOrchestrationFacts,StopOrchestrationFacts,StopScanFacts}.cs`.
  Those files actually live under `tests/BaseApi.Tests/Features/Orchestration/` (UTF-8-with-BOM, which
  also made the Read/Grep tools return empty). Only `OrchestrationServicePublishTests.cs` is under
  `tests/BaseApi.Tests/Orchestration/`.
- **Fix:** located the real files via Glob, stripped the BOM in place (content is plain ASCII; compiles
  identically), and reconciled them at the correct path.
- **Files modified:** the five `Features/Orchestration/*Facts.cs` files listed above.
- **Commit:** `58f157a`

**2. [Rule 2 - Missing critical reconcile] StopGateFacts + IdempotencyFacts also asserted superseded behavior**
- **Found during:** Task 2
- **Issue:** the plan's reconcile list named Start/Stop/StopScan/PublishTests, but `StopGateFacts`
  (all-or-nothing 422 gate, repeat-422, Redis-down `redisOp=KeyExistsAsync`) and `IdempotencyFacts`
  (last-write-wins re-Start) would have FAILED under the new first-win/delete-if-present behavior.
- **Fix:** reconciled both to the new semantics (per-id delete-if-present, `KeyDeleteAsync` op tag,
  first-win no-op re-Start). Required for the `Orchestration` slice to stay green (plan acceptance gate).
- **Files modified:** `StopGateFacts.cs`, `IdempotencyFacts.cs`.
- **Commit:** `58f157a`

## Threat Model Outcome

- **T-24-03 (V5 input validation):** mitigated — `ExistenceCheckAsync` 404 + null-body guard +
  `_idsValidator` still run FIRST on Start; the Stop null-body guard + `_idsValidator` run before any
  Redis touch. The first-win guard only adds an existence check + conditional write.
- **T-24-04 (TOCTOU on the probe):** accepted — single active WebApi replica assumed; concurrent-Start
  dedup deferred (ORCH-SCALE-01). Documented in a code comment on the probe.
- **T-24-05 (DoS via repeated Stop):** mitigated — a repeated Stop of an absent id is now a cheap
  per-id `KeyDeleteAsync` returning false (no-op), not a 422 exception path.

## Requirements Satisfied

- **WEBAPI-SUPPRESS-01** — WebApi first-win idempotent L2-root create/delete.

## Self-Check: PASSED
