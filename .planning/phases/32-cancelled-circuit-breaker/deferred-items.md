# Phase 32 — Deferred / Out-of-Scope Items

Items discovered during execution that are NOT part of any 32-* plan's scope. Logged, not fixed.

## From Plan 05 (orchestrator check-and-drop)

- **Flaky Orchestrator in-memory-MassTransit-harness tests.** A full hermetic suite run during 32-05
  showed 1 non-deterministic failure in the `BaseApi.Tests.Orchestrator` namespace. Proven independent
  of 32-05: stashing the lone source change (`ResultConsumer.cs`) and re-running the namespace on the
  baseline failed 5 tests; the count varies run-to-run (observed 5 / 3 / 1) — a timing/parallelism race
  in the in-memory harness tests, present WITHOUT this plan's edit. Matches MEMORY
  `reference_close_gate_surfaces_stale_flaky_tests`. All 32-05 directly-impacted classes
  (ResultCheckAndDropFacts, ResultAckTests, ResultConsumeTests) pass deterministically in isolation.
  The live phase-32 close gate (Plan 07, 3xGREEN + triple-SHA) is the authoritative full-suite signal;
  fix the harness races there if they persist, not as a 32-05 deliverable.

- **Pre-existing uncommitted edit to `tests/BaseApi.Tests/Processor/CheckAndDropFacts.cs`** (threads
  `DispatchTestKit.Retry()` into the `EntryStepDispatchConsumer` ctor — a 32-04 follow-up). Was already
  in the working tree before 32-05 started. Left untouched and NOT staged in either 32-05 commit
  (scoped-commit discipline). Belongs to whoever finalizes the 32-04 fixture threading.
