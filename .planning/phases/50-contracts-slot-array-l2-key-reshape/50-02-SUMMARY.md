---
phase: 50-contracts-slot-array-l2-key-reshape
plan: 02
subsystem: messaging
tags: [masstransit, redis, l2-keys, keeper-recovery, contracts, retire, reflection-guard, xunit]

# Dependency graph
requires:
  - phase: 50-contracts-slot-array-l2-key-reshape (plan 01)
    provides: "L2ProjectionKeys.MessageIndex(Guid) builder + KeeperInject EntryId/Data/DeleteEntryId init props (additive)"
provides:
  - "Model-B recovery contract surface deleted at the contract level (KeeperUpdate/KeeperCleanup records, L2ProjectionKeys.CompositeBackup builder, Keeper.BackupOptions)"
  - "UpdateConsumer/CleanupConsumer + their ConsumerDefinitions + Program.cs registrations removed"
  - "Endpoint single-ownership (UseMessageRetry + 3-type partitioner + PartitionKey/PartitionGuid) re-homed onto ReinjectConsumerDefinition"
  - "3 surviving consumer bodies (REINJECT/INJECT/DELETE) + ProcessorPipeline Post Model-B mechanics reduced to compile-only shape-preserving stubs"
  - "ModelBContractsRetiredFacts reflection guard proving CompositeBackup/KeeperUpdate/KeeperCleanup/BackupOptions are gone (SC-2)"
  - "0-warning Release+Debug solution + green hermetic suite (505 passed)"
affects: [51-processor-slot-array-writes, 52-keeper-3-state-consumer, 53-remnant-verify]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Single-owner endpoint definition re-home (move retry+partitioner+byte-pinned PartitionKey/Guid verbatim, never re-derive)"
    - "Shape-preserving no-op consumer-body stub (Task.CompletedTask, NOT throw) so the hermetic gate-open path stays green"
    - "Reflection RETIRE guard (typeof(...).Assembly anchors + GetMethods/GetTypes + Assert.Empty/DoesNotContain) with a positive-survivor fact to prevent vacuous pass"

key-files:
  created:
    - tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs
  modified:
    - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
    - src/Messaging.Contracts/KeeperInject.cs
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - src/Keeper/Recovery/RecoveryConsumerBase.cs
    - src/Keeper/Recovery/ReinjectConsumerDefinition.cs
    - src/Keeper/Recovery/InjectConsumer.cs
    - src/Keeper/Program.cs
    - tests/BaseApi.Tests/Contracts/KeeperContractTests.cs
    - tests/BaseApi.Tests/Keeper/RecoveryPartitionFacts.cs

key-decisions:
  - "Re-homed the single-owner endpoint definition onto ReinjectConsumerDefinition (D-01 suggested target); InjectConsumerDefinition/DeleteConsumerDefinition stay intentional no-ops, preserving the exactly-one-owner invariant."
  - "Stubbed InjectConsumer body to Task.CompletedTask (shape-preserving no-op, NOT throw) per Pitfall 5 so RecoveryDeadLetterFacts/gate-open hermetic paths stay green; the real A18 forward-only INJECT body is Phase 52."
  - "RecoveryGateWaitFacts ProbeConsumer vehicle re-pointed KeeperCleanup -> KeeperDelete (a surviving contract); it exercises the base gate-wait, not CLEANUP semantics."

patterns-established:
  - "Pattern 1: byte-pinned PartitionKey/PartitionGuid moved verbatim on a single-owner re-home (RecoveryPartitionFacts re-points to the new owner, no algorithm change)."
  - "Pattern 2: dangling <see cref> sweep on every deleted symbol (CS1574 is fatal under TreatWarningsAsErrors) including sibling no-op definitions and options doc-comments."

requirements-completed: [RETIRE-01, RETIRE-02]

# Metrics
duration: ~75min
completed: 2026-06-11
---

# Phase 50 Plan 02: Contracts & Slot-Array L2 Key Reshape (Model-B retirement spine) Summary

**Deleted the v4.0.0 Model-B recovery contract surface (UPDATE/CLEANUP records, the composite-backup key builder, BackupOptions) at the contract level, re-homed the keeper-recovery endpoint single-ownership onto ReinjectConsumerDefinition with a 3-type partitioner, stubbed the 3 survivors + pipeline Post mechanics shape-preserving, and proved the retirement with a ModelBContractsRetiredFacts reflection guard — 0-warning Release+Debug, 505-pass hermetic suite.**

## Performance

- **Duration:** ~75 min
- **Started:** 2026-06-11T10:39:53Z
- **Completed:** 2026-06-11
- **Tasks:** 3
- **Files modified:** 36 (incl. 7 src + 4 test file deletions; 1 test file created)

## Accomplishments
- Atomic Model-B src deletion (Task 1): KeeperUpdate/KeeperCleanup, L2ProjectionKeys.CompositeBackup, Keeper.BackupOptions, UpdateConsumer/CleanupConsumer + definitions + Program.cs registrations + the dead "Backup" appsettings section — src builds 0-warning Release at task end.
- Single-owner re-home onto ReinjectConsumerDefinition with `EndpointName=keeper-recovery`, `UseMessageRetry`, exactly 3 `UsePartitioner<KeeperReinject/KeeperInject/KeeperDelete>`, and the byte-pinned `PartitionKey`/`PartitionGuid` moved verbatim.
- Shape-preserving stubs: ProcessorPipeline Post (dropped BuildUpdate/BuildCleanup builders + their 2 sends) + InjectConsumer body (Task.CompletedTask) — NO `throw NotImplementedException` anywhere.
- Test surface reconciled (Task 2): 4 test files deleted, KeeperContractTests reduced to the 3 survivors + D-08 INJECT-field assertions, RecoveryPartitionFacts re-pointed, RecoveryGateWaitFacts vehicle swapped, every `BackupOptions` ctor arg dropped, SC2RecoveryPathsE2ETests (RealStack) composite block excised so it compiles.
- ModelBContractsRetiredFacts reflection guard (Task 3) — 4 facts (3 absence + 1 positive survivor) proving the symbols are gone; full hermetic suite 505 passed / 0 failed, Release+Debug 0-warning.

## Task Commits

1. **Task 1: Atomic Model-B src deletion + single-owner re-home + compile-keeping stubs** - `07ed3eb` (feat)
2. **Task 2: Reconcile the impacted test surface (delete / re-point / stub)** - `5a1d873` (test)
3. **Task 3: Add ModelBContractsRetiredFacts reflection guard + both-config 0-warning gate** - `84f8e5a` (test)

## Files Created/Modified

**Created:**
- `tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs` - SC-2 reflection guard (no CompositeBackup builder, no KeeperUpdate/KeeperCleanup type, no BackupOptions type, + MessageIndex/ExecutionData positive survivor).

**Deleted (src):** `KeeperUpdate.cs`, `KeeperCleanup.cs`, `BackupOptions.cs`, `Recovery/UpdateConsumer.cs`, `Recovery/UpdateConsumerDefinition.cs`, `Recovery/CleanupConsumer.cs`, `Recovery/CleanupConsumerDefinition.cs`.

**Deleted (tests):** `Keeper/UpdateConsumerFacts.cs`, `Keeper/CleanupConsumerFacts.cs`, `Keeper/BackupOptionsBoundTests.cs`, `Keeper/InjectConsumerFacts.cs`.

**Modified (src):**
- `Messaging.Contracts/Projections/L2ProjectionKeys.cs` - deleted CompositeBackup builder + its `<list>` doc item.
- `Messaging.Contracts/KeeperInject.cs` - doc-comment prose cleanup (fields already added Plan 01).
- `BaseProcessor.Core/Processing/ProcessorPipeline.cs` - dropped BuildUpdate/BuildCleanup + 2 Post sends; doc/comment sweep.
- `Keeper/Recovery/RecoveryConsumerBase.cs` - dropped `IOptions<BackupOptions>` ctor param + `TtlDays`; doc "five"->"three".
- `Keeper/Recovery/ReinjectConsumerDefinition.cs` - rewritten as the single owner (retry + 3-type partitioner + PartitionKey/Guid).
- `Keeper/Recovery/{Inject,Reinject,Delete}Consumer.cs` - dropped BackupOptions arg; InjectConsumer body -> no-op stub.
- `Keeper/Recovery/{Inject,Delete}ConsumerDefinition.cs` - re-pointed `<see cref>` to ReinjectConsumerDefinition.
- `Keeper/Program.cs` - dropped Configure<BackupOptions> + 2 orphan AddConsumer; doc "five"->"three", new owner.
- `Keeper/RecoveryOptions.cs` - removed dangling `<see cref="BackupOptions"/>`.
- `Keeper/appsettings.json` - deleted dead "Backup" section.

**Modified (tests):** `Contracts/KeeperContractTests.cs`, `Keeper/RecoveryTestKit.cs`, `Keeper/RecoveryPartitionFacts.cs`, `Keeper/RecoveryGateWaitFacts.cs`, `Keeper/RecoveryDeadLetterFacts.cs`, `Keeper/DeleteConsumerFacts.cs`, `Keeper/ReinjectConsumerFacts.cs`, `Processor/PipelinePostFacts.cs`, `Processor/DispatchTestKit.cs`, `Orchestrator/SC2RecoveryPathsE2ETests.cs`, `Features/Orchestration/Projection/L2ProjectionKeysTests.cs`.

## Decisions Made
- ReinjectConsumerDefinition chosen as the re-home target (D-01 discretion); the other two definitions remain intentional no-ops to preserve the single-owner invariant.
- InjectConsumer reduced to `Task.CompletedTask` rather than `throw` (Pitfall 5) so hermetic tests publishing to it stay green.
- KeeperContractTests `[Trait("Phase","43")]` re-traited to `[Trait("Phase","50")]` to track this plan's contract shape.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Re-pointed dangling `<see cref="UpdateConsumerDefinition"/>` in sibling no-op definitions**
- **Found during:** Task 1 (single-owner re-home)
- **Issue:** `InjectConsumerDefinition.cs` and `DeleteConsumerDefinition.cs` doc-comments referenced `<see cref="UpdateConsumerDefinition"/>`. After deleting that type, those crefs become CS1574 — fatal under `TreatWarningsAsErrors`. Not enumerated in the plan's known-cref list.
- **Fix:** Re-pointed both crefs (and the inline no-op comments) to `ReinjectConsumerDefinition`.
- **Files modified:** src/Keeper/Recovery/InjectConsumerDefinition.cs, src/Keeper/Recovery/DeleteConsumerDefinition.cs
- **Verification:** `dotnet build src/Keeper -c Release` 0-warning.
- **Committed in:** `07ed3eb` (Task 1)

**2. [Rule 3 - Blocking] Removed orphaned unused E2E helper methods**
- **Found during:** Task 2 (SC2RecoveryPathsE2ETests composite excision)
- **Issue:** After removing the STATE-3 composite seed/read poll, `PollForNewExecutionDataKeyAsync` + `ScanExecutionDataKeys` became unreferenced private members — IDE0051 unused-private-member is fatal under `EnforceCodeStyleInBuild`.
- **Fix:** Deleted both now-orphaned helpers.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs
- **Verification:** test project builds 0-warning Release.
- **Committed in:** `5a1d873` (Task 2)

**3. [Rule 3 - Blocking] Reconciled DeleteConsumerFacts/ReinjectConsumerFacts ctor calls + the L2ProjectionKeysTests CompositeBackup golden**
- **Found during:** Task 2 (test compile gate)
- **Issue:** Three test files not in the plan's Task 2 file list directly broke from Task 1's BackupOptions ctor-drop / CompositeBackup deletion: `DeleteConsumerFacts.cs` + `ReinjectConsumerFacts.cs` passed the dropped `RecoveryTestKit.Backup()` 6th ctor arg; `L2ProjectionKeysTests.cs` still pinned the deleted `CompositeBackup` builder (Plan 01 had added the MessageIndex pin but left the composite `[Fact]`).
- **Fix:** Dropped the `Backup()` arg at all 3 ctor sites (bodies unchanged — DELETE/REINJECT survive); deleted the orphaned `CompositeBackup` golden `[Fact]`.
- **Files modified:** tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs, tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs, tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs
- **Verification:** test project 0-warning Release; hermetic suite green.
- **Committed in:** `5a1d873` (Task 2)

---

**Total deviations:** 3 auto-fixed (1 missing-critical cref hygiene, 2 blocking compile reconciliations)
**Impact on plan:** All three were direct, mechanical consequences of Task 1's deletions that the plan's file lists under-enumerated (the RESEARCH Test Impact Map missed DeleteConsumerFacts/ReinjectConsumerFacts and the Plan-01-residual CompositeBackup golden). No scope creep, no behavioral change beyond the planned retirement.

## Issues Encountered
- The hermetic test run uses xUnit v3 / Microsoft.Testing.Platform, so `--filter-not-trait` is an MTP runner flag passed AFTER `--` (`dotnet test ... -- --filter-not-trait "Category=RealStack"`), not a `dotnet test`/VSTest switch (the bare form errors MSB1001). Confirms RESEARCH A1's flagged uncertainty; the established hermetic invocation is the `--`-passthrough form.

## Known Stubs
The 3 surviving recovery consumer bodies + the ProcessorPipeline Post mechanics are intentional, plan-mandated "dark-but-compiling" stubs (D-01/D-02), shape-preserving (not throwing) so the hermetic suite stays green. Their real A18 rewrites are scheduled: ProcessorPipeline slot-array forward/recovery pass -> Phase 51; the 3-state Keeper recovery consumer bodies (incl. the forward-only INJECT) -> Phase 52. These are documented in the plan as the locked build order (50 -> 51 -> 52 -> 53) and are NOT a goal regression for Phase 50, whose goal is "the Model-B symbols are GONE and nothing references them, 0-warning + green hermetic."

## User Setup Required
None - no external service configuration required (pure contract/code + hermetic-test change; Redis/RabbitMQ/Postgres only exercised by the excluded RealStack E2E, deferred to Phase 54).

## Next Phase Readiness
- Phase 51 (processor slot-array writes) is unblocked: the Model-B Post mechanics are removed and the `MessageIndex` builder + reshaped `KeeperInject` id-set are in place; the survivor `BuildReinject`/`BuildDelete`/`BuildInject` stay untouched as the A18 stamp points.
- Phase 52 (3-state keeper) is unblocked: the single-owner endpoint + 3-type partitioner + shape-preserving consumer bodies are the scaffold the real REINJECT/INJECT/DELETE bodies drop into.
- Phase 53 (remnant-verify RETIRE-03) inherits the lightweight `ModelBContractsRetiredFacts` guard as the in-phase SC-2 anchor; the full source/reflection remnant sweep remains its scope.

## Self-Check: PASSED

- Created files exist: ModelBContractsRetiredFacts.cs, ReinjectConsumerDefinition.cs (rewritten), 50-02-SUMMARY.md.
- Deleted files absent: KeeperUpdate.cs, BackupOptions.cs (+ KeeperCleanup, Update/CleanupConsumer(.Definition), 4 test files).
- Commits exist: 07ed3eb (Task 1), 5a1d873 (Task 2), 84f8e5a (Task 3).

---
*Phase: 50-contracts-slot-array-l2-key-reshape*
*Completed: 2026-06-11*
