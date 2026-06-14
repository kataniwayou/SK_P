---
task: 260614-9jd-remove-dead-skp-dlq-1-dead-letter-tier-c
type: quick-full
completed: 2026-06-14
commits:
  - d3ae457  # refactor: delete dead skp-dlq-1 filter + topology (+ proof test, already staged)
  - e2c6db7  # test: reduce recovery fact, compile-fix SC2, slim phase-62 close
files_deleted:
  - src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs
  - tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs
files_modified:
  - src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
  - tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs
  - tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs
  - scripts/phase-62-close.ps1
---

# Quick Task 260614-9jd: Remove dead skp-dlq-1 dead-letter tier Summary

Deleted the orphaned `skp-dlq-1` consolidated dead-letter tier — the filter, the `ConsolidatedFault`
envelope type, its broker topology, its proof test, and the live phase-62 close-gate depth probe. After
quick task 260614-2hf made the keeper-recovery endpoint nack-requeue with no `ConfigureError`, nothing in
`src/` produced into `skp-dlq-1`, so the entire tier was dead code. SK_P.sln builds clean (0 warnings, Debug
+ Release) and the hermetic Keeper (13/13) + Resilience (14/14) suites are green.

## What was deleted

**Production (Task 1, commit d3ae457):**
- `ConsolidatedErrorTransportFilter.cs` — the whole file (the `IFilter<ExceptionReceiveContext>` filter +
  the `ConsolidatedFault` forensic-envelope record + the `Dlq1`/`Dlq1Uri` consts). Deleted via `git rm`.
- `MessagingServiceCollectionExtensions.cs` — removed the dead `skp-dlq-1` topology block: the A18/Phase-53
  comment paragraph, the entire DLQ-1 topology comment, `c.DeployPublishTopology = true;`, and the whole
  `c.Publish<ConsolidatedFault>(p => { p.BindQueue(...x-message-ttl=7d...); });` block. **Retained**
  `using BaseConsole.Core.Messaging;` (line 2) — the four correlation filters (InboundCorrelation*,
  InboundExecutionScope*, OutboundCorrelation*) live in that namespace and are still referenced. Kept the
  four filter registrations, `configureBus?.Invoke`, and `c.ConfigureEndpoints(ctx)`.

**Tests + close script (Task 2, commit e2c6db7, plus the proof test deleted under Task 1):**
- `KeeperDlqConsolidationTests.cs` — deleted whole (it was the proof of the removed consolidated-DLQ
  behavior; every fact referenced the deleted type). Deleted via `git rm`; the staged deletion landed in
  the d3ae457 commit alongside the production delete — intentional, both are the dead tier.
- `RecoveryDeadLetterFacts.cs` — reduced to a fault/nack-requeue shape: removed
  `using BaseConsole.Core.Messaging;`, deleted the consolidated-sink `AddHandler<ConsolidatedFault>`
  endpoint declaration in `BuildHarness`, deleted the negative `Assert.False(...Any<ConsolidatedFault>...)`
  block, kept the positive `Assert.True(...Any<KeeperReinject>(f => f.Exception is not null))` fault
  assertion, and reworded the class/method XML doc (no DLQ sink claim). Left the RESIL-03
  `Duplicate_Reinject_reproduces_effect_no_collapse` fact + `PresentMux` alone.
- `SC2RecoveryPathsE2ETests.cs` — compile fix only: dropped the now-orphaned `using BaseConsole.Core.Messaging;`
  (line 9) and replaced each of the 4 `ConsolidatedErrorTransportFilter.Dlq1` refs (lines 148, 165, 168, 175)
  with the literal `"skp-dlq-1"`. STATE-2 data-gone logic (assert DLQ depth does NOT climb — by-design drop)
  preserved verbatim; the defensive `BrokerQueuesToPurge.Add("skp-dlq-1")` stays as a self-healing no-op.
- `phase-62-close.ps1` — removed the `// ---- Single-DLQ depth==0 assertion ----` comment + the entire
  `foreach ($q in @('skp-dlq-1')) { ... }` depth loop, the `DLQ depth: skp-dlq-1=0` summary line, and the
  `+ the skp-dlq-1 depth` token in the operator-append line. Trimmed the now-inaccurate header comments
  (cosmetic). **Preserved**: triple-SHA (psql/redis/rabbitmq), the `skp:msg:* count==0` assertion, the
  build gate, the 3-GREEN cadence, and all exit codes. **phase-39/49/55/58-close.ps1 byte-unchanged**
  (`git diff --stat` returned empty for all four).

## Ref-sweep result

`grep -rE "ConsolidatedFault|ConsolidatedErrorTransportFilter|\bDlq1\b|Dlq1Uri" src/` → **zero hits**. The
permitted bare-string `skp-dlq-1` doc-comment survivors in `RecoveryConsumerBase.cs` /
`RecoveryEndpointBinder.cs` and the comment-only / literal-only out-of-gate files
(`RealStackNetZeroSweepFixture.cs`, `SC3PauseResumeOutageE2ETests.cs`, `KeeperPauseAccumulateFacts.cs`,
`CollectionDefinitions.cs`) were left untouched per scope_boundary.

## Build + test outcomes

- `dotnet build SK_P.sln -c Debug --nologo` → **exit 0, 0 warnings**.
- `dotnet build SK_P.sln -c Release --nologo` → **exit 0, 0 warnings**.
- `BaseApi.Tests.exe --filter-namespace "BaseApi.Tests.Keeper"` → **Passed! 13/13, failed: 0** (6.4s).
- `BaseApi.Tests.exe --filter-namespace "BaseApi.Tests.Resilience"` → **Passed! 14/14, failed: 0** (0.8s,
  source-scan FACT 6 + FACT 7 still hold).
- Real Redis at localhost:6380 up; RabbitMQ may be down → RealStack/E2E excluded (expected, not a failure).

## Deviations from Plan

**Commit boundary note (not a code deviation):** `KeeperDlqConsolidationTests.cs` was deleted via `git rm`
at the start of Task 2 work but, being already staged, was captured by the Task 1 commit (d3ae457) together
with the production delete rather than a separate Task 2 commit. Both are the same dead-tier removal; no
behavioral or scope difference. All other plan steps executed exactly as written.

No Rule 1-4 deviations. No auth gates.

## Self-Check: PASSED

- Deleted files confirmed absent: `ConsolidatedErrorTransportFilter.cs` (D in HEAD~1),
  `KeeperDlqConsolidationTests.cs` (D in HEAD~1).
- Commits confirmed: d3ae457, e2c6db7 in `git log`.
- Frozen scripts confirmed unchanged: phase-39/49/55/58-close.ps1 (empty `git diff --stat`).
