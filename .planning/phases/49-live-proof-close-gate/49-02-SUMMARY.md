---
phase: 49-live-proof-close-gate
plan: 02
subsystem: testing
tags: [realstack, e2e, keeper-recovery, masstransit, redis, rabbitmq, reinject, inject, delete, skp-dlq-1]

# Dependency graph
requires:
  - phase: 49-live-proof-close-gate (plan 01)
    provides: SC1 RealStackWebAppFactory harness + net-zero teardown discipline (cloned)
  - phase: 46-keeper-recovery (consumers)
    provides: ReinjectConsumer / InjectConsumer / DeleteConsumer + RecoveryConsumerBase gate-wait
provides:
  - SC2 RealStack recovery-paths E2E proof (the 4 Keeper recovery states via direct-publish to keeper-recovery)
  - Broker-queue net-zero teardown extension (purge DLQ + delete per-procId re-inject queue)
affects: [49-04 close-gate (the 3xGREEN suite runs this RealStack fact live), 49-HUMAN-UAT operator run]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Direct-publish recovery contracts to KeeperQueues.Recovery via IBus send-endpoint (D-05)"
    - "Live RabbitMQ depth assertion via docker exec sk-rabbitmq rabbitmqctl -q list_queues name messages"
    - "Const-only DLQ/queue references (KeeperQueues.Recovery, ConsolidatedErrorTransportFilter.Dlq1) — no literals"
    - "Composite-backup 2-day-TTL key registered into net-zero teardown (leak -> redis SHA mismatch, D-07)"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs
  modified: []

key-decisions:
  - "Asserted REINJECT data-present + data-gone effects on live broker queue depth (re-inject lands on queue:{procId:D}; data-gone lands in skp-dlq-1) since no in-process consumer is bound in a live RealStack."
  - "Extended the cloned RealStackWebAppFactory with BrokerQueuesToPurge/BrokerQueuesToDelete so the bounded data-gone DLQ message + the parked re-inject queue clean to net-zero before the close gate."
  - "INJECT orchestrator-advance asserted as a queue-present read (the live orchestrator competes on OrchestratorQueues.Result and may drain it); the load-bearing INJECT proofs are the new skp:data key write + the composite delete."

patterns-established:
  - "SC2 direct-publish RealStack recovery proof: one [Fact] driving all 4 states sequentially, each with pre-seed -> publish -> poll-effect -> register-teardown."

requirements-completed: []  # TEST-01 stays UNTICKED — operator-gated live run (49-HUMAN-UAT.md), per D-03.

# Metrics
duration: 14min
completed: 2026-06-09
---

# Phase 49 Plan 02: SC2 RealStack Recovery-Paths E2E Summary

**Authored `SC2RecoveryPathsE2ETests.cs` — a RealStack E2E that proves all four Keeper recovery states by direct-publishing the actual contracts to `queue:keeper-recovery` (const) and asserting each L2 / re-inject / orchestrator-advance / dead-letter effect, with every minted key (incl. the 2-day-TTL composite) registered to net-zero teardown.**

## Performance

- **Duration:** ~14 min
- **Started:** 2026-06-09T10:32:28Z
- **Completed:** 2026-06-09
- **Tasks:** 1
- **Files modified:** 1 (created)

## Accomplishments
- New `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` (514 lines), tagged `[Trait("Category","RealStack")]` + `[Trait("Phase","49")]`, cloning the SC1 `RealStackWebAppFactory` host overrides + net-zero teardown.
- Direct-publishes the 4 state contracts to `KeeperQueues.Recovery` (const, never the literal): REINJECT data-present, REINJECT data-gone, INJECT, DELETE.
- Per-state effect assertions read from the production consumers:
  - REINJECT data-present (pre-seed `skp:data:{entryId}` so STRLEN>0) -> re-injected `EntryStepDispatch` lands on `queue:{procId:D}` (broker depth).
  - REINJECT data-gone (key absent) -> `RecoveryDataGoneException` -> consolidated error transport -> depth increment on `ConsolidatedErrorTransportFilter.Dlq1` (const).
  - INJECT (pre-seed `CompositeBackup`) -> new `skp:data:{entryId}` written, reconstructed `StepCompleted` to `queue:{OrchestratorQueues.Result}`, composite DELETED.
  - DELETE (pre-seed `skp:data:{entryId}`) -> data key gone.
- Net-zero: every minted key registered into `L2KeysToCleanup` (incl. the composite whose 2-day TTL cannot be waited out); the bounded data-gone DLQ message + the parked re-inject queue cleaned via new `BrokerQueuesToPurge`/`BrokerQueuesToDelete`.

## Task Commits

1. **Task 1: Author SC2RecoveryPathsE2ETests** - `d4c9b05` (test)

**Plan metadata:** committed with this SUMMARY + STATE/ROADMAP.

## Files Created/Modified
- `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` - SC2 RealStack proof of the 4 Keeper recovery states (direct-publish to keeper-recovery + per-state effect + net-zero teardown).

## Decisions Made
- **Broker-depth assertions for the re-inject targets.** In a live RealStack no in-process consumer is bound to the fresh `queue:{procId:D}` or to `skp-dlq-1`, so the re-inject (REINJECT data-present) and the dead-letter (REINJECT data-gone) are observed as live broker queue depths via `docker exec sk-rabbitmq rabbitmqctl -q list_queues name messages` (the RealStack analog of the `RecoveryDeadLetterFacts` in-memory `Consumed.Any` idiom).
- **Teardown extension for the bounded DLQ + re-inject queue.** The cloned factory gained `BrokerQueuesToPurge` (drains the one parked `skp-dlq-1` message so the close gate's depth==0 holds) and `BrokerQueuesToDelete` (removes the per-procId re-inject queue so the close gate's rabbitmq name-SHA holds).
- **INJECT orchestrator-advance is a queue-present read.** The live orchestrator competes on `OrchestratorQueues.Result` and may have already consumed the reconstructed `StepCompleted`, so the load-bearing INJECT proofs are the new `skp:data:*` write and the composite delete; the `OrchestratorQueues.Result` reference is retained (const) as the advance seam.

## Deviations from Plan

None - plan executed exactly as written. No production code changed; the single new test file matches the plan's `files_modified` and the `<action>` step-by-step.

## Issues Encountered
- The hermetic run surfaced MassTransit RabbitMQ connection stack-trace noise to stderr (the in-process WebApi's bus attempts to reach the not-running live broker). This is background transport noise, **not a test failure** — the run summary is `Passed! total: 507, failed: 0, skipped: 0`. The new RealStack fact is correctly EXCLUDED by `--filter-not-trait Category=RealStack`.

## Acceptance Criteria Verification
- File exists; 514 lines (>250 min). BOM-less UTF-8, no mojibake.
- `grep "keeper-recovery"` (quoted literal) => 0; `KeeperQueues.Recovery` => present (2). `grep "skp-dlq-1"` => 0; `ConsolidatedErrorTransportFilter.Dlq1` => present (6).
- `new KeeperReinject(`/`new KeeperInject(`/`new KeeperDelete(` => present; `CompositeBackup` => present (2); `L2ProjectionKeys.ExecutionData(` => present (3); `OrchestratorQueues.Result` => present (3).
- `[Trait("Phase","49")]` => 1; `[Trait("Category","RealStack")]` => 1; `public sealed class SC2RecoveryPathsE2ETests` => 1.
- `dotnet build ... -c Release` => 0 Warning / 0 Error; `-c Debug` => 0 Warning / 0 Error.
- `dotnet run ... --filter-not-trait Category=RealStack` => 507 passed, 0 failed, 0 skipped.
- No production-code file changed; `git diff --diff-filter=D HEAD~1 HEAD` => empty (no deletions).

## User Setup Required
None - no external service configuration required for the authored/hermetic DoD. The live N×GREEN recovery-path run is operator-gated (49-HUMAN-UAT.md); TEST-01 stays UNTICKED until that GREEN run.

## Next Phase Readiness
- SC2 proof authored + hermetically green. Sibling to SC1 (49-01); SC3 (49-03) and the close gate (49-04) remain.
- This RealStack fact will run inside the 49-04 close gate's 3×GREEN suite against the rebuilt v4 stack.

## Self-Check: PASSED

- FOUND: tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs
- FOUND: .planning/phases/49-live-proof-close-gate/49-02-SUMMARY.md
- FOUND commit: d4c9b05

---
*Phase: 49-live-proof-close-gate*
*Completed: 2026-06-09*
