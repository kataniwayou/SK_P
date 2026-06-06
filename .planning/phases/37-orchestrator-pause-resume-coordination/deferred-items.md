# Phase 37 — Deferred Items

Out-of-scope discoveries logged during execution (NOT fixed in-plan per the executor scope boundary).

## 37-04 (Keeper publish sites)

### D1 — FireDispatchTests cross-class Quartz RAMJobStore collision (pre-existing test-harness isolation bug)

- **Discovered during:** 37-04 full-suite verification run.
- **Symptom:** `BaseApi.Tests.Orchestrator.FireDispatchTests.{OneMessagePerEntryStep_CorrectFields, CorrelationIdDiffersAcrossTwoFires, LivenessTimestampAdvancesOnFire_NoL2Write}` and `WorkflowFireJobScopeTests.PostMint_FireLogs_Carry_CorrelationId_And_WorkflowId_Scope` fail with `Quartz.ObjectAlreadyExistsException : Unable to store Trigger: 'DEFAULT.{guid}', because one already exists with this identification.`
- **Root cause:** These older Orchestrator tests (last touched Phase 31, `3c30bdb`) schedule into the **shared process-wide Quartz RAMJobStore under the DEFAULT trigger group** without the per-test unique `quartz.scheduler.instanceName = test-{Guid:N}` discipline that the Phase-37 scheduling tests adopt (see 37-VALIDATION.md "real `StdSchedulerFactory` RAMJobStore, unique instanceName per test"). When Phase-37's new test classes (`PauseResumeSchedulingTests`, added 37-01 `8a87407`) populate the shared repository in the same test process, FireDispatch/WorkflowFireJobScope collide on the DEFAULT group. Reproduces in isolation (3/3) AND in the full suite (4 fails) — it is deterministic, not load-flakiness.
- **Why out of scope for 37-04:** Plan 37-04 modifies ONLY `src/Keeper/Consumers/{FaultEntryStepDispatchConsumer,FaultExecutionResultConsumer}.cs`. The failing tests exercise the Orchestrator fire/dispatch path, which does not reference Keeper. The failure is independent of this plan's change.
- **Suggested fix (future):** Give `FireDispatchTests` / `WorkflowFireJobScopeTests` their own unique `quartz.scheduler.instanceName` (mirror `PauseResumeSchedulingTests` Pattern 2 from 37-01), or unschedule/clear in teardown. Candidate for a 37 cleanup plan or a Phase-38 hygiene pass.

### D2 — Two RealStack/live E2E tests operator-pending (expected, per Phase 35/36 precedent)

- `BaseApi.Tests.Keeper.KeeperRecoveryE2ETests.KeeperRecovery_RecoversBothPaths` and `BaseApi.Tests.Orchestrator.KeeperFaultIntakeE2ETests.LiveWrongTypeTrip_KeeperContainer_EmitsCorrelatedIntakeLog` require a live RabbitMQ + Redis + a running Keeper container (rebuilt to the current SourceHash). They fail in a hermetic run with `Connection Failed: rabbitmq://rabbitmq/`. These are operator/live-gate tests tracked in `35-HUMAN-UAT.md` / `36-HUMAN-UAT.md`, not hermetic regressions.
