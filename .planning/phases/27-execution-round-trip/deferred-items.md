# Phase 27 — Deferred / Out-of-Scope Items

## Pre-existing test failure (NOT caused by Plan 27-01)

- **Test:** `BaseApi.Tests.Orchestrator.ResultConsumeTests.CompletedResult_DispatchesMatchingNextStep_WithCorrectFieldCopy`
- **Symptom:** `Assert.True() Failure` + "Failed to stop bus: rabbitmq://... (Not Started)" / "Connection Failed: rabbitmq://".
- **Cause:** A broker-dependent Orchestrator harness test that requires a live RabbitMQ broker; the broker was not reachable during the Plan 27-01 run (this is a real-stack/harness dependency, not a code regression).
- **Scope:** Out of scope for Plan 27-01 (touches none of the 27-01 files; the validator/options/ProcessResult/seam changes do not affect the Orchestrator result-consume path).
- **Disposition:** Left untouched. The 7 new `ProcessorJsonSchemaValidatorFacts` and the extended options/seam facts all pass in isolation via `-- --filter-class`.
