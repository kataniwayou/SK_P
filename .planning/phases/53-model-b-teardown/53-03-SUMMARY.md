---
phase: 53-model-b-teardown
plan: 03
subsystem: processor-console-messaging
tags: [masstransit, usemessageretry, configureerror, teardown, model-b, D-01, D-03, RETIRE-03, 0-warning]

# Dependency graph
requires:
  - phase: 53-model-b-teardown
    plan: 01
    provides: "Standing RED guards FACT 6 (D-01 processor-half source-scan) + FACT 7 (D-03 ConfigureError keeper-local) that this plan turns GREEN"
  - phase: 53-model-b-teardown
    plan: 02
    provides: "All orchestrator-source D-01 offenders already stripped; FACT 6 reduced to the single processor-half offender (ProcessorStartupOrchestrator)"
provides:
  - "Processor dispatch keep-latch (cfg.UseMessageRetry on the {id:D} endpoint) removed -> bare ConfigureConsumer tail; send-exhaust throw -> RabbitMQ nack-requeue redelivery (A18 D-01)"
  - "Dead startup-orchestrator retryOptions ctor dep + unused using removed (pipeline IOptions<RetryOptions> RETAINED — the in-code RetryLoop reads Retry:Limit)"
  - "ConfigureError(GenerateFaultFilter + ConsolidatedErrorTransportFilter) MOVED from BaseConsole.Core global callback to RecoveryEndpointBinder connect-callback (D-03 keeper-local); skp-dlq-1 topology KEPT — skp-dlq-1 is now keeper-only"
  - "All 5 Phase-53 standing guards GREEN (FACTS 5/6/7/8); phase end-state reached"
affects: [model-b-teardown verification, close-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Bare ConfigureConsumer tail (no UseMessageRetry, no ConfigureError) is the A18 end-state for the processor dispatch endpoint — default RabbitMQ nack-requeue is the sole redelivery mechanism"
    - "Per-endpoint error-transport move (ConfigureError) relocated from a bus-wide AddConfigureEndpointsCallback to a single keeper connect-callback — same filter pair, now keeper-scoped"
    - "Drop dead IOptions<RetryOptions> ctor-param + its sole-namespace using together with the removed latch to stay 0-warning (SC-3)"

key-files:
  created: []
  modified:
    - "src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs"
    - "src/BaseProcessor.Core/Processing/ProcessorPipeline.cs"
    - "src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs"
    - "src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs"
    - "src/Keeper/Recovery/RecoveryEndpointBinder.cs"
    - "tests/BaseApi.Tests/Processor/ProcessorStartupOrchestrator construction sites (3 files — ctor-trim ripple)"

key-decisions:
  - "Removed `using Messaging.Contracts.Configuration;` from ProcessorStartupOrchestrator — RetryOptions was that namespace's only reference in this file, so the using went dead with the latch (0-warning / TreatWarningsAsErrors)"
  - "Removed `using MassTransit.Middleware;` from BaseConsole.Core's MessagingServiceCollectionExtensions — GenerateFaultFilter (the only member used from it) left with the deleted global callback; the surviving skp-dlq-1 topology uses only BaseConsole.Core.Messaging types"
  - "Reworded the BaseConsole.Core teardown comment to NOT contain the bare word `ConfigureError` — FACT 7's global-file guard is `Assert.DoesNotContain(\"ConfigureError\")` (bare-word, not CALL-pattern), so a doc-comment mention would have re-RED'd the guard"
  - "Three processor test construction sites (DispatchBindSequenceFacts/IdentityResolutionFacts/SchemaResolutionFacts) updated for the trimmed ctor (Rule 3 blocking-issue fix — CS7036); no test asserted on the removed latch, so behavior-coverage is preserved"

patterns-established:
  - "The processor dispatch endpoint and the orchestrator endpoints now share the SAME A18 posture: no bus retry, no error filter; send-exhaust throw -> broker redelivery"

requirements-completed: [RETIRE-03]

# Metrics
duration: 9min
completed: 2026-06-11
---

# Phase 53 Plan 03: Model-B Teardown — Processor Keep-Latch Strip + Keeper-Local ConfigureError Summary

**Removed the processor dispatch keep-latch (`cfg.UseMessageRetry`) + its dead startup-orchestrator `retryOptions` from `ProcessorStartupOrchestrator` (bare `ConfigureConsumer` tail; the pipeline's `IOptions<RetryOptions>` STAYS), reconciled the `ProcessorPipeline`/`EntryStepDispatchConsumer` `-> _error` narrative comments to `throw -> broker redelivery` (throw lines unchanged), and MOVED the `ConfigureError(GenerateFaultFilter + ConsolidatedErrorTransportFilter)` pair from BaseConsole.Core's global callback into `RecoveryEndpointBinder`'s connect-callback (keeping the skp-dlq-1 topology) — turning the last two Phase-53 guards (FACT 6 processor-half, FACT 7) GREEN; all 5 standing guards (FACTS 5/6/7/8) are now GREEN and the full hermetic suite (528) is green at 0-warning Release+Debug.**

## Performance

- **Duration:** ~9 min
- **Started:** 2026-06-11T20:03:58Z
- **Completed:** 2026-06-11T20:13:07Z
- **Tasks:** 3 (2 code + 1 gate)
- **Files modified:** 5 source + 3 test ripple

## Accomplishments

- **Task 1 — processor keep-latch strip (D-01, FACT 6 processor-half):** deleted `cfg.UseMessageRetry(r => r.Immediate(retryLimit))` from the `ProcessorStartupOrchestrator` `ConnectReceiveEndpoint` callback, leaving the bare `cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx)` tail. Grep-confirmed `retryLimit`/`retryOptions` were used ONLY by the removed latch -> deleted `var retryLimit = ...`, the `IOptions<RetryOptions> retryOptions` ctor param, and the now-dead `using Messaging.Contracts.Configuration;` (RetryOptions was that namespace's sole reference here). Reconciled the completion-block xml-doc + the 174-179 D-09 comment to the A18 end-state. **COUNTER-EXAMPLE honored:** `ProcessorPipeline`'s `IOptions<RetryOptions>` is UNTOUCHED (`grep -c "IOptions<RetryOptions>"` = 1) — the in-code RetryLoop reads `Retry:Limit`. The `throw sent.Error!;` statements (:318/:344) are byte-unchanged (`grep -c` = 2); only their trailing `// -> _error` narrative + the :55-58 / :311 comments changed to `throw -> broker redelivery (no _error, Phase-53 D-01)`. EntryStepDispatchConsumer's :21-23 xml-doc reconciled likewise.
- **Task 2 — keeper-local ConfigureError (D-03, FACT 7):** DELETED the entire `x.AddConfigureEndpointsCallback((context, name, e) => { e.ConfigureError(ep => { ... }); })` block from `MessagingServiceCollectionExtensions.cs` and the now-unused `using MassTransit.Middleware;` (GenerateFaultFilter went with the block). ADDED the filter pair VERBATIM into `RecoveryEndpointBinder`'s existing `ConnectReceiveEndpoint(KeeperQueues.Recovery, ...)` callback — after the policy-retry block, before the partitioners — with `using BaseConsole.Core.Messaging;` for `ConsolidatedErrorTransportFilter`. **KEPT (Pitfall 5):** the skp-dlq-1 topology (`c.DeployPublishTopology = true;` + `c.Publish<ConsolidatedFault>(p => p.BindQueue(...x-message-ttl...))`) is intact (`grep -c "Publish<ConsolidatedFault>"` = 1). The :86-99 policy retry (Dlq1 Immediate / SustainedOutage Interval) is UNCHANGED (D-05 keeper untouched). skp-dlq-1 is now keeper-only.
- **Task 3 — SC-3 dual-config + full-suite gate:** no code change; confirmed Release AND Debug build 0-warning, the full hermetic suite is 528/0, and all 5 Phase-53 standing guards (FACTS 5/6/7/8 in `ModelBContractsRetiredFacts` — 8 total in the class incl. the Phase-50 facts) are GREEN.

## Task Commits

1. **Task 1: Remove processor keep-latch + dead startup retryOptions; reconcile pipeline comments** — `fcc1794` (refactor)
2. **Task 2: Move ConfigureError filter pair to keeper-local** — `593d1e2` (refactor)
3. **Task 3: SC-3 dual-config + full-suite gate** — no commit (gate task, no code change)

**Plan metadata:** _(final docs commit — SUMMARY/STATE/ROADMAP/REQUIREMENTS)_

## Files Created/Modified

- `ProcessorStartupOrchestrator.cs` — latch removed; bare ConfigureConsumer tail; dead retryOptions ctor param + Messaging.Contracts.Configuration using dropped; doc reconciled.
- `ProcessorPipeline.cs` — comment-only reconcile at :55-58 / :311 / :318 / :344 (throw lines + IOptions<RetryOptions> ctor untouched).
- `EntryStepDispatchConsumer.cs` — comment-only :21-23 xml-doc reconcile.
- `MessagingServiceCollectionExtensions.cs` — global AddConfigureEndpointsCallback/ConfigureError block deleted; MassTransit.Middleware using dropped; skp-dlq-1 topology KEPT.
- `RecoveryEndpointBinder.cs` — ConfigureError filter pair added keeper-local (after policy retry, before partitioners); BaseConsole.Core.Messaging using added.
- `DispatchBindSequenceFacts.cs` / `IdentityResolutionFacts.cs` / `SchemaResolutionFacts.cs` — removed the `Options.Create(new ...RetryOptions())` arg from the `new ProcessorStartupOrchestrator(...)` call (ctor-trim ripple).

## Phase-53 Guard End-State (all GREEN)

| Fact | Test | State after this plan | Turned GREEN by |
|------|------|-----------------------|-----------------|
| FACT 5 | `Keeper_registers_exactly_three_recovery_consumers` | **GREEN** | (already, Plan 01 — states collapsed in Phase 50/52) |
| FACT 6 | `No_bus_retry_or_error_transport_on_execution_path_endpoints` | **GREEN** | **53-03** (processor keep-latch removed; orchestrator-source done in 53-02) |
| FACT 7 | `ConfigureError_is_keeper_local_only` | **GREEN** | **53-03** (moved BaseConsole.Core global -> RecoveryEndpointBinder) |
| FACT 8 | `Dead_WorkflowRootNotFound_ignore_removed_from_start_stop_definitions` | **GREEN** | (already, Plan 02) |

`dotnet test ... -- --filter-trait "Phase=53"` -> **Passed: 4, Failed: 0, Total: 4**. `--filter-class "*ModelBContractsRetiredFacts*"` -> **8/8 GREEN** (the 4 Phase-53 + 4 Phase-50 facts in the same class).

## Verification

- `grep -c "cfg.UseMessageRetry(" src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` -> **0**.
- `grep -c "IOptions<RetryOptions>" src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` -> **1** (pipeline injection RETAINED).
- `grep -c "throw sent.Error!" src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` -> **2** (throw lines unchanged; only comments edited).
- `grep -c "ConfigureError" src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` -> **0**; `grep -c "AddConfigureEndpointsCallback"` -> **0**.
- `grep -c "Publish<ConsolidatedFault>" src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` -> **1** (skp-dlq-1 topology KEPT — Pitfall 5).
- `grep -c "ConfigureError(ep" src/Keeper/Recovery/RecoveryEndpointBinder.cs` -> **1** (filter pair moved keeper-local).
- `dotnet build SK_P.sln -c Release` -> **0 Warning / 0 Error**; `-c Debug` -> **0 Warning / 0 Error** (SC-3).
- `dotnet test SK_P.sln -c Release -- --filter-not-trait "Category=RealStack"` -> **528 passed / 0 failed / 0 skipped**.
- `git diff --diff-filter=D` clean on both task commits (no file deletions).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Trimmed-ctor ripple in three processor test construction sites**
- **Found during:** Task 1 (post-edit build)
- **Issue:** Removing the `IOptions<RetryOptions> retryOptions` ctor param from `ProcessorStartupOrchestrator` broke three direct-construction test sites (`DispatchBindSequenceFacts`, `IdentityResolutionFacts`, `SchemaResolutionFacts`) at compile (CS7036 — they each passed `Options.Create(new Messaging.Contracts.Configuration.RetryOptions())`).
- **Fix:** Removed the dead `RetryOptions` argument from each `new ProcessorStartupOrchestrator(...)` call, keeping the trailing `clock`/`fakeClock` arg. No test asserted on the removed latch (`grep` for UseMessageRetry/Immediate/latch in DispatchBindSequenceFacts -> 0 matches), so existing behavior-coverage is fully preserved.
- **Files modified:** tests/BaseApi.Tests/Processor/{DispatchBindSequenceFacts,IdentityResolutionFacts,SchemaResolutionFacts}.cs
- **Commit:** fcc1794

**2. [Rule 1 - Guard fidelity] Reworded the BaseConsole.Core teardown comment to omit the bare word "ConfigureError"**
- **Found during:** Task 2 (FACT 7 acceptance grep)
- **Issue:** My first teardown comment in `MessagingServiceCollectionExtensions.cs` contained the literal phrase "global per-endpoint ConfigureError move", so `grep -c "ConfigureError"` returned 1 (acceptance requires 0). FACT 7's guard is `Assert.DoesNotContain("ConfigureError", File.ReadAllText(global))` — a BARE-WORD scan on this file (unlike FACT 6's CALL-pattern scan), so a doc-comment mention would have re-RED'd the guard.
- **Fix:** Reworded to "global per-endpoint error-transport move (...)"; re-verified `grep -c "ConfigureError"` -> 0 and FACT 7 GREEN.
- **Files modified:** src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
- **Commit:** 593d1e2

No architectural changes (no Rule 4), no auth gates, no checkpoints.

## Known Stubs

None — this plan only removes wiring (the keep-latch + the global error callback) and relocates the existing filter pair keeper-local; the `throw sent.Error!` lines, the pipeline `IOptions<RetryOptions>`, and the skp-dlq-1 topology are all intact. No placeholder values, no unwired data sources.

## Threat Flags

None new. The plan's threat register dispositions hold: T-53-Topology (over-deleting skp-dlq-1) is MITIGATED — `Publish<ConsolidatedFault>` survives (grep = 1); T-53-Tamper (silent message loss on the processor endpoint) is MITIGATED — no error pipeline added on the dispatch endpoint, so the default with neither retry nor error filter is nack-requeue (asserted by FACT 6, now GREEN); T-53-DoS (unbounded requeue spin on a permanently-failing poison send) is the deliberately-ACCEPTED residual (D-04/A18), not new surface.

## Self-Check: PASSED

- FOUND: `.planning/phases/53-model-b-teardown/53-03-SUMMARY.md`
- FOUND: `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` (0 cfg.UseMessageRetry calls)
- FOUND: `src/Keeper/Recovery/RecoveryEndpointBinder.cs` (1 ConfigureError(ep call)
- FOUND commit: `fcc1794` (Task 1)
- FOUND commit: `593d1e2` (Task 2)

---
*Phase: 53-model-b-teardown*
*Completed: 2026-06-11*
