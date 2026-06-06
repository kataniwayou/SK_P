# Phase 37 — Deferred Items

Out-of-scope discoveries logged during execution (NOT fixed in-plan per the executor scope boundary).

## 37-04 (Keeper publish sites)

### D1 — RESOLVED (`571498f`): 37-02 self-reschedule regression, NOT a pre-existing harness bug

- **Discovered during:** 37-04 full-suite verification run.
- **Symptom:** `BaseApi.Tests.Orchestrator.FireDispatchTests.{OneMessagePerEntryStep_CorrectFields, CorrelationIdDiffersAcrossTwoFires, LivenessTimestampAdvancesOnFire_NoL2Write}` and `WorkflowFireJobScopeTests.PostMint_FireLogs_Carry_CorrelationId_And_WorkflowId_Scope` failed with `Quartz.ObjectAlreadyExistsException : Unable to store Trigger: 'DEFAULT.{guid}', because one already exists with this identification.`
- **Corrected root cause (the original 37-04 entry was WRONG):** This was NOT a cross-class DEFAULT-group collision. Each failing test DOES use a per-test unique `quartz.scheduler.instanceName = test-{Guid:N}` (FireDispatchTests.cs:43), so cross-class isolation was never the issue. The real cause: **37-02 (`8ee43a1`) introduced a regression.** It stamped the load-bearing deterministic `TriggerKey(jobId.ToString("D"))` on `RescheduleAsync` but left the call as `ScheduleJob` (add). `WorkflowFireJob.Execute` self-reschedules on every fire while the firing one-shot trigger — which now shares that deterministic key — is still in the store (Quartz removes a completed no-repeat trigger only AFTER `Execute` returns). The add therefore collided on the same key. Before 37-02 the reschedule used a random trigger identity and never collided. This was a production-critical break (every workflow would stop self-rescheduling after its first fire), not a test artifact.
- **Fix (`571498f`):** `WorkflowScheduler.RescheduleAsync` now calls `RescheduleJob(triggerKey, trigger)` to atomically replace the existing deterministic-key trigger (without deleting the non-durable job), falling back to `ScheduleJob` only when no prior trigger existed.
- **Verified:** the 4 tests + `PauseResumeSchedulingTests` (7/7) GREEN; full hermetic suite 477/477, 0 failed.

### D2 — Two RealStack/live E2E tests operator-pending (expected, per Phase 35/36 precedent)

- `BaseApi.Tests.Keeper.KeeperRecoveryE2ETests.KeeperRecovery_RecoversBothPaths` and `BaseApi.Tests.Orchestrator.KeeperFaultIntakeE2ETests.LiveWrongTypeTrip_KeeperContainer_EmitsCorrelatedIntakeLog` require a live RabbitMQ + Redis + a running Keeper container (rebuilt to the current SourceHash). They fail in a hermetic run with `Connection Failed: rabbitmq://rabbitmq/`. These are operator/live-gate tests tracked in `35-HUMAN-UAT.md` / `36-HUMAN-UAT.md`, not hermetic regressions.
