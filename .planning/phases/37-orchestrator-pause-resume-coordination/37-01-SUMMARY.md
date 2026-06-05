---
phase: 37-orchestrator-pause-resume-coordination
plan: 01
subsystem: testing
tags: [xunit, nsubstitute, quartz, ramjobstore, masstransit, tdd-red, pause-resume]

# Dependency graph
requires:
  - phase: 36-l2-health-probe-recovery-loop-dlqs
    provides: L2ProbeRecovery + ProbeOutcome + FakeRedis double + FaultEntryStepDispatchConsumer (the Keeper recovery seam these tests drive)
  - phase: 23-orchestrator-stop-reload-lifecycle
    provides: WorkflowScheduler + WorkflowLifecycle + WorkflowL1Store + OrchestratorTestStubs (the RAM-scheduler/consumer harness reused here)
provides:
  - Four Wave 0 RED test files encoding the PAUSE-01..05 acceptance contract before any production symbol exists
  - Executable implementation target for plans 37-02 (contracts + scheduler methods), 37-03 (consumers), 37-04 (Keeper publish sites)
  - Deterministic-TriggerKey state model assertions (TriggerKey(jobId.ToString("D")) == Paused / Normal / None)
affects: [37-02, 37-03, 37-04, orchestrator-scheduling, keeper-recovery]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Wave 0 RED test authoring: name every production symbol the phase will ship; build fails ONLY with missing-symbol errors (CS0246/CS1061)"
    - "Real L2ProbeRecovery over FakeRedis (Up -> Recovered, Down -> GaveUp) to drive a sealed/non-virtual recovery dependency that NSubstitute cannot mock"
    - "ConsumeContext<T> substitute with GetSendEndpoint stubbed to a benign ISendEndpoint so the DLQ/re-inject Send path does not NRE while asserting context.Publish fan-out"

key-files:
  created:
    - tests/BaseApi.Tests/Messaging/PauseResumeContractTests.cs
    - tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs
    - tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs
    - tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs
  modified: []

key-decisions:
  - "Contract test placed in namespace BaseApi.Tests.MessagingContracts (NOT BaseApi.Tests.Messaging) to avoid shadowing the top-level Messaging namespace — same gotcha ProcessorResponderTests documents"
  - "Keeper publish-site test backs the non-substitutable L2ProbeRecovery with a real instance over FakeRedis (Up/Down) rather than a NSubstitute mock — RunAsync is non-virtual on a sealed type"
  - "Consumer idempotency test seeds L1 + Quartz via WorkflowLifecycle.HydrateAndScheduleAsync over PresentL2, mirroring StopConsumerLifecycleTests, then drives the (not-yet-existent) Pause/ResumeWorkflowConsumer twice each"

patterns-established:
  - "Pattern 1: every Quartz call passes TestContext.Current.CancellationToken (xUnit1051)"
  - "Pattern 2: every RAM scheduler uses a unique quartz.scheduler.instanceName = test-{Guid:N} to prevent cross-class repository collision"

requirements-completed: [PAUSE-01, PAUSE-02, PAUSE-03, PAUSE-04, PAUSE-05]

# Metrics
duration: ~25min
completed: 2026-06-06
---

# Phase 37 Plan 01: Wave 0 Pause/Resume RED Tests Summary

**Four xUnit RED test files encoding the full PAUSE-01..05 acceptance contract (ICorrelated contract shape, Keeper Pause/Resume publish fan-out, deterministic-TriggerKey Paused/Normal/None state model, and serial-replay idempotency) against a real Quartz RAMJobStore + the FakeRedis-backed L2ProbeRecovery — failing ONLY because the production symbols plans 02/03/04 will create do not exist yet.**

## Performance

- **Duration:** ~25 min
- **Started:** 2026-06-05T23:30Z (approx)
- **Completed:** 2026-06-05T23:55Z
- **Tasks:** 2
- **Files modified:** 4 (all created)

## TDD Gate Compliance

This is a **TDD RED test-authoring plan**. The expected and intended outcome is a RED build/suite.

- Both task commits are `test(...)` commits (RED gate). The GREEN (`feat`) gate is owned by downstream plans 37-02/03/04, which implement the production symbols these tests name.
- The build of `tests/BaseApi.Tests/BaseApi.Tests.csproj` fails **only** with missing-symbol errors. Verified: after filtering out `CS0246` (missing type) and `CS1061` (missing member), there are **zero** remaining compile errors — i.e. no harness/syntax/wiring errors.
- Missing-symbol tally (all six target production symbols are referenced and RED):
  - `PauseWorkflow` ×14, `ResumeWorkflow` ×14 (Plan 02 contracts)
  - `PauseAsync` ×8, `GetTriggerStateAsync` ×12 (Plan 02 WorkflowScheduler methods)
  - `PauseWorkflowConsumer` ×4, `ResumeWorkflowConsumer` ×4 (Plan 03 consumers)
- A deliberate RED for a test-authoring plan is a **PASS**, not a failure.

## Accomplishments
- `PauseResumeContractTests` (PAUSE-01): asserts `PauseWorkflow`/`ResumeWorkflow` implement `ICorrelated` and body-carry `WorkflowId`/`H`/`CorrelationId`.
- `KeeperPausePublishTests` (PAUSE-01): drives `FaultEntryStepDispatchConsumer.Consume` over a substituted `ConsumeContext<Fault<EntryStepDispatch>>`; Recovered → `context.Received(1).Publish(PauseWorkflow)` + `Publish(ResumeWorkflow)` carrying the inner workflow's id/H; GaveUp → Pause published, Resume `DidNotReceive` (D-09).
- `PauseResumeSchedulingTests` (PAUSE-02/03/05): `PauseSuppressesFire` (Paused via deterministic `TriggerKey`, D-08), `ResumeReschedulesFresh` (one Normal trigger, future next-fire, no misfire), `ResumeIgnoresStoppedAndRunning` (None / Normal ignore branches, D-09).
- `PauseResumeConsumerTests` (PAUSE-04): seeds one workflow into L1+Quartz, invokes Pause×2 then Resume×2 serially over one scheduler; asserts one Normal trigger and one Quartz job (no orphans, D-07 idempotency).

## Task Commits

Each task was committed atomically:

1. **Task 1: Contract-shape + Keeper-publish RED tests (PAUSE-01)** - `8622788` (test)
2. **Task 2: Scheduler + consumer RED tests (PAUSE-02/03/04/05)** - `8a87407` (test)

**Plan metadata:** (this SUMMARY + STATE + ROADMAP commit — see final docs commit)

## Files Created/Modified
- `tests/BaseApi.Tests/Messaging/PauseResumeContractTests.cs` - PAUSE-01 contract-shape assertions (ICorrelated, WorkflowId/H/CorrelationId)
- `tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs` - PAUSE-01 Keeper publish-site assertions (Pause at intake, Resume on Recovered, none on GaveUp)
- `tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs` - PAUSE-02/03/05 RAM-scheduler state-model assertions
- `tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs` - PAUSE-04 serial-replay idempotency

## Decisions Made
- **Namespace `BaseApi.Tests.MessagingContracts`** for the contract test: a `BaseApi.Tests.Messaging` namespace would shadow the top-level `Messaging` namespace for sibling files referencing `Messaging.Contracts.*` unqualified (documented in `ProcessorResponderTests`/`BaseApiCoreFirewallTests`).
- **Real `L2ProbeRecovery` over `FakeRedis`** in the Keeper publish test: `L2ProbeRecovery` is a `sealed class` with a non-virtual `RunAsync`, so NSubstitute cannot mock it. An Up `FakeRedis` deterministically yields `Recovered`; a Down one yields `GaveUp` — the plan anticipated this fallback.
- **Consumer ctor shape `(WorkflowLifecycle, ILogger<T>)`** assumed for `PauseWorkflowConsumer`/`ResumeWorkflowConsumer`, mirroring `StopOrchestrationConsumer`. Plans 02/03 must match this shape (the RED test pins it).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Added `using Keeper;` for `ProbeOptions`**
- **Found during:** Task 1 (Keeper publish test)
- **Issue:** `ProbeOptions` lives in namespace `Keeper` (not `Keeper.Recovery`); first build failed with CS0246 on `ProbeOptions` — a genuine harness error, not the intended RED.
- **Fix:** Added `using Keeper;` to `KeeperPausePublishTests.cs`.
- **Files modified:** tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs
- **Verification:** Re-build — `ProbeOptions` resolved; only missing-symbol RED errors remained.
- **Committed in:** `8622788` (Task 1 commit)

**2. [Rule 1 - Bug] Fixed `IReadOnlyCollection<ITrigger>` indexing (CS0021)**
- **Found during:** Task 2 (scheduling test)
- **Issue:** `scheduler.GetTriggersOfJob(...)` returns `IReadOnlyCollection<ITrigger>`, which cannot be indexed with `[0]` — a harness compile error (CS0021), not an intended RED.
- **Fix:** Replaced `triggers[0]` with the element returned by `Assert.Single(triggers)`.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs
- **Verification:** Re-build — no non-missing-symbol errors (filtering CS0246/CS1061 leaves zero errors).
- **Committed in:** `8a87407` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (1 blocking, 1 bug)
**Impact on plan:** Both were harness errors that would have masked the deliberate RED; fixing them was required so the RED is purely "production symbol does not exist yet." No scope creep — no production code touched.

## Issues Encountered
- None beyond the two auto-fixed harness errors above. The MEMORY note "Bash tool runs POSIX bash, not PowerShell" was honored — used `grep`/`sed` rather than `Select-String`.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Plans 37-02/03/04 now have an executable RED target. To turn the suite GREEN they must create, with these exact names/shapes:
  - `PauseWorkflow(Guid WorkflowId, string H) : ICorrelated { Guid CorrelationId { get; init; } }` and `ResumeWorkflow(...)` (Plan 02, `src/Messaging.Contracts/`).
  - `WorkflowScheduler.PauseAsync(Guid jobId, CancellationToken)` (wraps `PauseJob`) and `GetTriggerStateAsync(Guid jobId, CancellationToken)` (wraps `GetTriggerState(TriggerKey)`); every trigger stamped `.WithIdentity(new TriggerKey(jobId.ToString("D")))` (Plan 02).
  - `PauseWorkflowConsumer`/`ResumeWorkflowConsumer` with ctor `(WorkflowLifecycle, ILogger<T>)` (Plan 03).
  - `FaultEntryStepDispatchConsumer` publish sites: `context.Publish(PauseWorkflow)` at intake, `context.Publish(ResumeWorkflow)` on Recovered, no Resume on GaveUp (Plan 04).

## Self-Check: PASSED

- Files created — all present:
  - FOUND: tests/BaseApi.Tests/Messaging/PauseResumeContractTests.cs
  - FOUND: tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs
  - FOUND: tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs
  - FOUND: tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs
- Commits exist: FOUND `8622788`, FOUND `8a87407`.
- RED verified deliberate: build fails ONLY with missing-symbol errors (CS0246/CS1061) naming the six downstream production symbols; zero harness/syntax errors after filtering. Per the TDD RED expectation, this is a PASS.

---
*Phase: 37-orchestrator-pause-resume-coordination*
*Completed: 2026-06-06*
