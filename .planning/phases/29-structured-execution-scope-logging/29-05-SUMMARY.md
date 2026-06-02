---
phase: 29-structured-execution-scope-logging
plan: 05
subsystem: testing
tags: [logging, otel, scopes, e2e, real-stack, close-gate, elasticsearch]

# Dependency graph
requires:
  - phase: 29-structured-execution-scope-logging
    plan: 02
    provides: "InboundExecutionScopeConsumeFilter — bus-wide execution-id scope on IExecutionCorrelated consume logs (the ONLY source of attributes.WorkflowId on processor-sample logs)"
  - phase: 29-structured-execution-scope-logging
    plan: 03
    provides: "ProcessorId enricher + nested BeginScope (ExecutionId/EntryId) in EntryStepDispatchConsumer"
  - phase: 29-structured-execution-scope-logging
    plan: 04
    provides: "Explicit CorrelationId+WorkflowId scope in WorkflowFireJob (the fire path that triggers the round-trip the E2E observes)"
  - phase: 28-sourcehash-identity-processor-sample-e2e
    plan: 03
    provides: "Real-stack SampleRoundTripE2ETests + ElasticsearchTestClient.PollEsForLog; the orchestrator template-sourced advanceQuery block to mirror in shape"
provides:
  - "A SECOND PollEsForLog scope-proof in SampleRoundTripE2ETests asserting a scope-sourced WorkflowId on a PROCESSOR-side (processor-sample) log — fails if the 29-02 execution-scope filter is reverted (L1 trap closed)"
  - "scripts/phase-29-close.ps1 — the phase-29 close gate (3x full-suite GREEN + triple-SHA BEFORE==AFTER + Release/Debug zero-warning), mirroring phase-28-close.ps1"
  - "A Completed-path LogInformation inside the nested BeginScope of EntryStepDispatchConsumer so processor consume logs actually emit and export the five execution-id scopes to ES (the gap the close gate surfaced)"
  - "EntryStepDispatchRuntimeScopeTests — hermetic proof the five scope keys land on the Completed line via the runtime ConnectReceiveEndpoint path"
affects: [phase-close, milestone-v3.5.0-closeout, observability, es-attributes]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "L1-trap close: the real-stack scope proof pins resource.attributes.service.name == processor-sample so the assertion can ONLY be satisfied via the new filter — the orchestrator's template-sourced WorkflowId hit (StartOrchestration is NOT IExecutionCorrelated) cannot satisfy a processor-side service.name term"
    - "Additive-only ES read: the E2E adds ONLY a PollEsForLog (append-only telemetry), no new seeded Redis/RMQ/PG state, so the close-gate triple-SHA BEFORE==AFTER invariant holds unchanged"
    - "A scope is only observable if a log ACTUALLY emits inside it — the Completed path must write a LogInformation INSIDE the nested BeginScope for the five scope keys to export to ES (a silent path proves nothing)"
    - "phase-NN-close.ps1 = 3x full-suite GREEN (no Category filter, both real-stack E2Es live each run) + triple-SHA (psql \\l + redis-cli --scan + rabbitmqctl list_queues) BEFORE==AFTER + Release/Debug 0-warning, processor-sample REQUIRED healthy"

key-files:
  created:
    - scripts/phase-29-close.ps1
    - tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs
  modified:
    - tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs
    - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs

key-decisions:
  - "Scope proof pinned to service.name == processor-sample (not orchestrator): the processor consumes EntryStepDispatch (IExecutionCorrelated) so its logs carry attributes.WorkflowId ONLY via the new filter — closing the L1 trap the orchestrator/template hit would have left open"
  - "ADD ONLY an ES read; no new seeded state and no teardown changes, so the close-gate triple-SHA invariant is untouched (ES docs are append-only telemetry, not part of the SHA)"
  - "Gap fix (gate-surfaced): the original Completed path emitted NO log, so the scope proof had nothing to find — added a Completed-path LogInformation INSIDE the existing nested BeginScope; the bus-wide filter already reached the runtime-connected endpoint, so no endpoint-level wiring was needed"

patterns-established:
  - "L1-trap closure: a real-stack proof must be tied to a surface that ONLY the new code can satisfy (processor-side service.name + scope-sourced id), never a template-sourced hit that passes even with the feature reverted"
  - "Close gate authored as a parse-clean, BOM-free byte-faithful copy of the prior phase's gate (label strings only), run operator-authorized; gate evidence (3xPassed, triple-SHA, procId) recorded verbatim in STATE.md"

requirements-completed: [LOG-06, LOG-01]

# Metrics
duration: ~Operator-gated (Tasks 1-2 hermetic; Task 3 = 3x ~4m live gate runs)
completed: 2026-06-02
---

# Phase 29 Plan 05: Real-Stack Scope-Proof + Phase-29 Close Gate Summary

**The real-stack `SampleRoundTripE2ETests` now carries a SECOND `PollEsForLog` that proves a `WorkflowId` round-trips to Elasticsearch FROM A SCOPE on a `processor-sample` log (the L1 trap is closed — it fails if the 29-02 execution-scope filter is reverted), and `scripts/phase-29-close.ps1` (3x full-suite GREEN + triple-SHA BEFORE==AFTER + Release/Debug 0-warning) PASSED operator-authorized with GATE_EXIT=0 (405 Passed x3, all three SHAs HELD); the gate surfaced and fixed a gap — the Completed path emitted no log, so a `LogInformation` was added inside the nested `BeginScope` so the five execution-id scopes actually export to ES.**

## Performance

- **Duration:** Tasks 1-2 hermetic (build + author); Task 3 operator-authorized gate = 3 consecutive full-suite runs (4m07s / 3m46s / 4m07s) + triple-SHA capture
- **Completed:** 2026-06-02
- **Tasks:** 3 (2 auto + 1 operator-authorized checkpoint) + 1 gate-surfaced gap fix
- **Files modified:** 2 created, 2 modified

## Accomplishments

- **Task 1 — E2E scope proof (L1 trap closed):** extended `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` with a SECOND `PollEsForLog` (`scopeProofQuery`): a `term` on `attributes.WorkflowId == {{wfId}}` AND a `term` on `resource.attributes.service.name == "processor-sample"`, asserted `Assert.NotNull(scopeProof)`. The processor consumes `EntryStepDispatch` (`IExecutionCorrelated`), so its consume logs carry `attributes.WorkflowId` ONLY via the new `InboundExecutionScopeConsumeFilter` (29-02). This assertion FAILS if the scope work is reverted — the existing orchestrator hit above is template-sourced (`StartOrchestration` is NOT `IExecutionCorrelated`) and would still pass even with the entire scope feature removed. Additive only: the existing `advanceQuery` block + the net-zero teardown (`L2KeysToCleanup` / `ParentIndexMembersToSrem`) are unchanged; no new seeded Redis/RMQ/PG state (the triple-SHA invariant holds; ES logs are append-only telemetry).
- **Task 2 — phase-29 close gate:** authored `scripts/phase-29-close.ps1` as a byte-faithful copy of `scripts/phase-28-close.ps1` with ONLY the phase-identifying label strings updated (28→29). Gate logic unchanged: `$services` = full v3.5.0 stack with `processor-sample` REQUIRED healthy (not a health exception); the stable-Processor-row steady-state pre-flight; 3 consecutive full-suite `dotnet test` runs (no `Category` filter — both real-stack E2Es incl. the extended `SampleRoundTripE2ETests` run live each run); triple-SHA (`psql \l` + `redis-cli --scan` + `rabbitmqctl list_queues`) BEFORE==AFTER; Release+Debug zero-warning build; NO `FLUSHDB`. ParseFile = PARSE OK; BOM-free.
- **Task 3 — operator-authorized close gate, GATE_EXIT=0:** ran live and PASSED — 3 consecutive full-suite GREEN (**405 Passed x3**, Exit=0 each), triple-SHA all HELD (BEFORE==AFTER), Release/Debug 0/0. The live `scopeProof` now PASSES: an ES `processor-sample` Completed log carries `attributes.WorkflowId` (+ `StepId` / `ProcessorId` / `ExecutionId` / `EntryId`). Gate evidence recorded verbatim in STATE.md.
- **Gap fix (gate-surfaced, committed `9993261`):** the close gate's first live run exposed that the original Completed path in `EntryStepDispatchConsumer` emitted NO log, so the `scopeProof` had nothing to find. Added a Completed-path `LogInformation` INSIDE the existing nested `BeginScope` so the processor consume log actually emits and exports the five execution-id scopes to ES (`WorkflowId` / `StepId` / `ProcessorId` via the bus-wide `InboundExecutionScopeConsumeFilter`; `ExecutionId` / `EntryId` via the nested scope), plus a new TDD test `EntryStepDispatchRuntimeScopeTests.cs` mirroring the runtime `ConnectReceiveEndpoint` and proving the five scope keys land on the Completed line. Root cause: a scope is only observable if a log ACTUALLY emits inside it. The bus-wide filter was CONFIRMED to reach the runtime-connected endpoint — no endpoint-level wiring was needed.

## Task Commits

1. **Task 1: processor-side scope-sourced WorkflowId ES assertion** — `84959b9` (test)
2. **Task 2: author phase-29-close.ps1 (mirror phase-28-close.ps1)** — `d79cefe` (feat)
3. **Task 3: operator-authorized close-gate run** — no code commit (operator-authorized human-verify gate, GATE_EXIT=0)
4. **Gap fix (gate-surfaced): emit Completed-path log so execution scopes export to ES** — `9993261` (fix) + new TDD test `EntryStepDispatchRuntimeScopeTests.cs`

**Checkpoint marker:** `ca1e435` (docs — Tasks 1-2 committed, Task 3 awaiting operator gate)
**Plan metadata:** committed separately with this SUMMARY + STATE.md + REQUIREMENTS.md.

## Files Created/Modified

- `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` (modified) — added the SECOND `PollEsForLog` (`scopeProofQuery`) asserting a scope-sourced `WorkflowId` on a `processor-sample` log; existing `advanceQuery` block + teardown unchanged.
- `scripts/phase-29-close.ps1` (created) — phase-29 close gate (3x full-suite GREEN + triple-SHA BEFORE==AFTER + Release/Debug 0-warning), processor-sample required-healthy, no FLUSHDB, BOM-free.
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (modified) — Completed-path `LogInformation` added INSIDE the nested `BeginScope` so the five execution-id scopes export to ES (the gap the gate surfaced).
- `tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs` (created) — hermetic TDD proof the five scope keys land on the Completed line via the runtime `ConnectReceiveEndpoint` path.

## Decisions Made

- **Scope proof pinned to `processor-sample` (not `orchestrator`):** closing the L1 trap requires tying the assertion to a surface only the new filter can satisfy. The orchestrator's `attributes.WorkflowId` hit is template-sourced (`StartOrchestration` is not `IExecutionCorrelated`) and would pass even with scopes reverted; the processor-side `service.name` term cannot be satisfied without the new `InboundExecutionScopeConsumeFilter`.
- **ADD ONLY an ES read:** no new seeded Redis/RMQ/PG state and no teardown changes, so the close-gate triple-SHA BEFORE==AFTER invariant is untouched (ES docs are append-only telemetry, not part of the SHA).
- **Gap fix is correctness, not feature creep:** a `BeginScope` with no log inside it exports nothing — the missing Completed-path log meant the whole 29-02/29-03/29-04 scope chain was invisible on the processor's primary success path. Emitting the Completed log (inside the existing nested scope) is required for the scope work to be observable at all.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Completed path emitted no log, so execution scopes never exported to ES**
- **Found during:** Task 3 (operator-authorized close-gate run — the live `scopeProof` had nothing to find)
- **Issue:** The original Completed path in `EntryStepDispatchConsumer` wrapped a nested `BeginScope` (ExecutionId/EntryId) around work that wrote NO log line. With no emission inside the scope, none of the five execution-id scope keys (WorkflowId/StepId/ProcessorId via the bus-wide filter; ExecutionId/EntryId via the nested scope) ever reached ES — so the L1 scope proof would never see a `processor-sample` `attributes.WorkflowId`.
- **Fix:** Added a Completed-path `LogInformation` INSIDE the nested `BeginScope`. Confirmed (no new wiring) that the bus-wide `InboundExecutionScopeConsumeFilter` already reaches the runtime `ConnectReceiveEndpoint`. Added TDD test `EntryStepDispatchRuntimeScopeTests.cs` mirroring the runtime endpoint to prove the five keys land on the Completed line.
- **Files modified:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs`, `tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs`
- **Verification:** The operator-authorized close gate (GATE_EXIT=0) live-passed the `scopeProof` — an ES `processor-sample` Completed log now carries `attributes.WorkflowId` (+ StepId/ProcessorId/ExecutionId/EntryId); 405 Passed x3.
- **Committed in:** `9993261` (fix)

---

**Total deviations:** 1 auto-fixed (Rule 1 — bug surfaced by the close gate)
**Impact on plan:** The fix was necessary for the plan's own proof bar (LOG-06) to be satisfiable — without an emitted Completed log the scope chain was invisible on the processor's primary path. Additive (one log line inside an existing scope + a hermetic test); no log-shape regression to existing templates. No scope creep.

## L1-Trap Reasoning (proof integrity)

The plan's central risk (Landmine L1 / Pitfall 1) is a false-green proof: the EXISTING `attributes.WorkflowId` assertion is scoped to `service.name == "orchestrator"` and matches the TEMPLATE-sourced `"Start reload for WorkflowId={WorkflowId}"` placeholder from `StartOrchestrationConsumer`. Because `StartOrchestration` is NOT `IExecutionCorrelated`, the new filter never touches it — that assertion PASSES even with the entire scope feature reverted, proving nothing about scopes. The NEW `scopeProof` ties `attributes.WorkflowId` to a PROCESSOR-side log (`service.name == "processor-sample"`); the processor's only path to `attributes.WorkflowId` is the new `InboundExecutionScopeConsumeFilter` (29-02), so the assertion FAILS if the scope work reverts. The gate-surfaced gap fix is what makes this assertion *reachable at all* — before it, the processor's Completed path emitted nothing, so even with scopes present there was no log to carry the scope.

## Issues Encountered

The close gate (first full live suite run in a while) surfaced the missing-Completed-log gap (above), consistent with the known close-gate pattern of surfacing observability gaps on the first real-stack run. Triaged as a current-phase correctness bug (not a stale/flaky prior-phase test), fixed inline (`9993261`), and the gate then passed 405 x3 with all SHAs held.

## TDD Gate Compliance

The gap fix followed RED→GREEN within `9993261`'s lineage: `EntryStepDispatchRuntimeScopeTests.cs` mirrors the runtime `ConnectReceiveEndpoint` and asserts the five scope keys on the Completed line (RED before the Completed-path log, GREEN after). Task 1 is a `test(29-05)` E2E-extension commit (`84959b9`); Task 2 is a `feat(29-05)` close-script author (`d79cefe`).

## Self-Check: PASSED

- FOUND: `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` (modified)
- FOUND: `scripts/phase-29-close.ps1`
- FOUND: `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (modified)
- FOUND: `tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs`
- FOUND: `.planning/phases/29-structured-execution-scope-logging/29-05-SUMMARY.md`
- FOUND commit: `84959b9` (test — Task 1 E2E scope proof)
- FOUND commit: `d79cefe` (feat — Task 2 phase-29-close.ps1)
- FOUND commit: `9993261` (fix — Completed-path log gap fix)

### Close-Gate Evidence (Task 3, operator-authorized, GATE_EXIT=0)

- 3-consecutive GREEN: Run 1/2/3 each Exit=0, **Passed=405** (4m07s / 3m46s / 4m07s). Full suite, no Category filter — both real-stack E2Es (incl. the extended `SampleRoundTripE2ETests` with the new processor-side scope proof) ran live each run.
- Triple-SHA (psql \l + redis-cli --scan + rabbitmqctl list_queues), BEFORE == AFTER, all HELD:
  - psql \l SHA-256:                  `b48ce78302d9dd8ca93e6a7e694c153dc46705ec9ab4458b31c6933ea2e33fef`
  - redis-cli --scan SHA-256:         `36e8337392e62c5b07da807ae535f971674d8af1d6cf8755c3ddae360a9fe92f`
  - rabbitmqctl list_queues SHA-256:  `3cbd9c0324f25cffe7b2ed1522162564483c926abc342c98f7f0a729b58c9e60`
- Zero-warning build: Release = 0/0; Debug = 0/0.
- Steady-state procId (idempotent reuse): `fe9a48ae-4cdb-4ae0-9676-51ec61f0ec59` (genuine embedded SourceHash `9709ffbe33664420be7e1e6494b0bbe8947d463489cf31a790dc428a3d3db0f6`).
- Live scopeProof PASSES: an ES `processor-sample` Completed log carries `attributes.WorkflowId` (+ StepId/ProcessorId/ExecutionId/EntryId).

## Next Phase Readiness

- LOG-06 + LOG-01 complete; all six LOG requirements (LOG-01..06) now satisfied. Phase 29 = 5/5 plans, close gate GATE_EXIT=0. Milestone v3.5.0 = 17/17 plans across phases 25-29.
- Phase verification + `phase.complete` are owned by the orchestrator (NOT done here). No blockers — the live stack is current (rebuilt processor-sample container, embedded SourceHash matches the steady-state procId) and the triple-SHA invariant held.

---
*Phase: 29-structured-execution-scope-logging*
*Completed: 2026-06-02*
