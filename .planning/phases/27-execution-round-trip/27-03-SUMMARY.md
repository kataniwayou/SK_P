---
phase: 27-execution-round-trip
plan: 03
subsystem: processor
tags: [masstransit, connect-receive-endpoint, runtime-bind, exclude-from-configure-endpoints, healthy-ordering, immediate-retry, round-trip]

# Dependency graph
requires:
  - phase: 27-execution-round-trip
    plan: 02
    provides: "EntryStepDispatchConsumer (IConsumer<EntryStepDispatch>) — the framework round-trip consumer awaiting a runtime queue binding"
  - phase: 26-baseprocessor-core
    plan: "*"
    provides: "ProcessorStartupOrchestrator (Loop A identity + Loop B definitions), IProcessorContext (Id/MarkHealthy), IStartupGate, AddBaseProcessor composition root, ProcessorLivenessHeartbeat (writes L2 only when IsHealthy)"
provides:
  - "Runtime ConnectReceiveEndpoint bind of EntryStepDispatchConsumer to the durable bare {Id:D} competing-consumer queue, with Immediate(3) retry, awaited Ready, BEFORE MarkHealthy (EXEC-01 — the load-bearing bind→Ready→Healthy order)"
  - "EntryStepDispatchConsumer DI registration via AddConsumer<T>().ExcludeFromConfigureEndpoints() — present for ConfigureConsumer<T> at bind time but suppressed from the unconditional ConfigureEndpoints(ctx) so no wrong-named kebab queue is auto-bound at bus start"
affects: [28-sourcehash-sample-e2e]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Runtime endpoint bind: IReceiveEndpointConnector.ConnectReceiveEndpoint(bareName, (ctx,cfg)=>{ UseMessageRetry(Immediate(3)); ConfigureConsumer<T>(ctx); }) then `await handle.Ready` — IBus implements IReceiveEndpointConnector, so it is DI-resolvable"
    - "Load-bearing ordering proven structurally + by test: bind → await Ready → MarkHealthy → MarkReady. Because the heartbeat writes L2 only when IsHealthy, Healthy necessarily lands in L2 AFTER the queue exists (EXEC-01)"
    - "Consumer-without-static-endpoint: AddConsumer<T>().ExcludeFromConfigureEndpoints() keeps the DI registration while suppressing the auto-bound kebab queue (Pitfall 1)"

key-files:
  created: []
  modified:
    - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
    - src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
    - tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs
    - tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs

key-decisions:
  - "Bind lambda contains ONLY UseMessageRetry(r => r.Immediate(3)) + ConfigureConsumer<EntryStepDispatchConsumer>(ctx) — the RabbitMqReceiveEndpointConfigurator cast is OMITTED (locked Open Q1): it is null under the in-memory harness and non-load-bearing; RabbitMQ durable/AutoDelete defaults already satisfy EXEC-01."
  - "Bind name is the BARE Guid `{Id:D}` (no `queue:` prefix — the scheme is sender-only), yielding a competing-consumer endpoint; the orchestrator Sends to `queue:{Id:D}` against the same queue."
  - "MarkHealthy() is called STRICTLY AFTER `await handle.Ready` (D-03) — proven by an ordered event-log test, not merely trusted from source order."
  - "The live ConnectReceiveEndpoint-against-RabbitMQ + Healthy-after-bind proof is deferred to the Phase 28 E2E (TEST-01); this plan proves the SEQUENCING with a fake IReceiveEndpointConnector (RESEARCH Pitfall 6 / VALIDATION Manual-Only)."

patterns-established:
  - "DispatchBindSequenceFacts drives the real ProcessorStartupOrchestrator.ExecuteAsync to completion (in-memory identity Found immediately, null schema Ids → Loop B no-op, FakeTimeProvider) with a RecordingConnector + RecordingContext appending to a shared List<string>; the event order is asserted == ['connect','ready','markhealthy']."

requirements-completed: [EXEC-01]

# Metrics
duration: 12min
completed: 2026-06-02
---

# Phase 27 Plan 03: Dispatch Endpoint Wiring (bind-then-MarkHealthy) Summary

**Closed the execution round-trip loop (EXEC-01): registered `EntryStepDispatchConsumer` for DI via `AddConsumer<T>().ExcludeFromConfigureEndpoints()` (so no wrong-named kebab queue is auto-bound at bus start) and inserted the runtime `IReceiveEndpointConnector.ConnectReceiveEndpoint` bind — durable bare `{Id:D}` queue + `Immediate(3)` retry + `ConfigureConsumer<EntryStepDispatchConsumer>` — BEFORE `MarkHealthy()` in `ProcessorStartupOrchestrator`, with the load-bearing bind→`await Ready`→Healthy order proven by an ordered-event-log test.**

## Resume Note

This plan was executed as a RESUME: both task commits (`826bdea` feat, `ba34b4d` test) were already on the branch from a prior session. This session RE-VERIFIED that the committed code satisfies every acceptance criterion against the actual files (no criterion required new code) and ran the full `<verification>` block green. No source was rewritten; the only new commit is this docs/state finalization.

## Performance
- **Duration:** ~12 min (verification + finalization; implementation pre-committed)
- **Completed:** 2026-06-02
- **Tasks:** 2 (both pre-committed; verified, not rewritten)
- **Files modified:** 4 (2 src + 2 test)

## Accomplishments
- **Task 1 — registration + runtime bind (`826bdea`):**
  - `BaseProcessorServiceCollectionExtensions.cs` adds `x.AddConsumer<EntryStepDispatchConsumer>().ExcludeFromConfigureEndpoints()` inside the `AddBaseConsoleMessaging` configure-consumers lambda (alongside the two `AddRequestClient`s), with the class-level XML-doc updated to state the dispatch consumer IS registered (excluded from auto-config) and bound at runtime. The CONFIG-02 TTL needs no new wiring — `services.Configure<ProcessorLivenessOptions>(cfg.GetSection("Processor"))` already binds `ExecutionDataTtlSeconds`.
  - `ProcessorStartupOrchestrator.cs` primary ctor gains `IReceiveEndpointConnector endpointConnector`; the completion block now binds `queueName = $"{context.Id!.Value:D}"` via `endpointConnector.ConnectReceiveEndpoint(queueName, (ctx, cfg) => { cfg.UseMessageRetry(r => r.Immediate(3)); cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx); })`, `await handle.Ready`, THEN `context.MarkHealthy()` → `gate.MarkReady()`. The RabbitMQ configurator cast is omitted (locked Open Q1).
- **Task 2 — ordering + registration tests (`ba34b4d`):**
  - `DispatchBindSequenceFacts.cs` (new) — a hand-rolled `RecordingConnector`/`RecordingHandle`/`RecordingContext` appending `"connect"`/`"ready"`/`"markhealthy"` to a shared log; drives the real orchestrator to Healthy on a `FakeTimeProvider`. `Connect_Then_Ready_Then_MarkHealthy_In_Order` asserts the log equals `["connect","ready","markhealthy"]`; `Binds_Bare_IdFormat_QueueName` asserts the bound name == `Id.ToString("D")` and contains no `queue:` prefix.
  - `AddBaseProcessorFacts.cs` — added `Registers_Dispatch_Consumer` asserting a `ServiceDescriptor` for `EntryStepDispatchConsumer` is present in the composed collection.

## Acceptance-Criteria Evidence

**Task 1 (all already satisfied — confirmed against the files):**
- `BaseProcessorServiceCollectionExtensions.cs` contains `AddConsumer<EntryStepDispatchConsumer>().ExcludeFromConfigureEndpoints()` — line 73. ✔
- `ProcessorStartupOrchestrator.cs` ctor contains `IReceiveEndpointConnector endpointConnector` — line 55. ✔
- `ProcessorStartupOrchestrator.cs` contains `ConnectReceiveEndpoint` (149), `cfg.UseMessageRetry(r => r.Immediate(3))` (151), `cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx)` (152), `await handle.Ready` (154). ✔
- `await handle.Ready` (154) appears BEFORE `context.MarkHealthy()` (156) — D-03 order. ✔
- The bind lambda does NOT contain `RabbitMqReceiveEndpointConfigurator` (cast omitted). ✔
- `dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj -c Debug` → `Build succeeded. 0 Warning(s) / 0 Error(s)` (exit 0). ✔

**Task 2 (all already satisfied — confirmed against the files + run):**
- `DispatchBindSequenceFacts.cs` asserts `MarkHealthy` strictly AFTER the handle's `Ready` is awaited via `Assert.Equal(new[] { "connect", "ready", "markhealthy" }, log)`. ✔
- `DispatchBindSequenceFacts.cs` asserts the bound queue name == `Id.ToString("D")` with no `queue:` prefix. ✔
- `AddBaseProcessorFacts.cs` asserts the `EntryStepDispatchConsumer` registration is present (`Registers_Dispatch_Consumer`). ✔
- `DispatchBindSequenceFacts` (MTP filter-class) → `Passed: 2, Failed: 0` (exit 0). ✔
- `AddBaseProcessorFacts` (MTP filter-class) → `Passed: 4, Failed: 0` (exit 0). ✔

## Verification (the plan's `<verification>` block — all green)
- `dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj -c Debug` → **Build succeeded. 0 Warning(s) / 0 Error(s)** (exit 0).
- `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Processor"` → **Passed! - Failed: 0, Passed: 387, Skipped: 0, Total: 387, Duration: 3m 20s** (exit 0). NOTE: the MTP/xUnit-v3 runner ignores the VSTest `--filter` (warning `MTP0001`) and runs the whole suite — so this run is the full 387, confirming the processor slice with zero regression.
- `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` → **Passed! - Failed: 0, Passed: 387, Skipped: 0, Total: 387, Duration: 3m 08s** (exit 0).

Targeted MTP-filter runs additionally confirm the two new facts ran (not silently skipped): `--filter-class DispatchBindSequenceFacts` = 2/2, `--filter-class AddBaseProcessorFacts` = 4/4.

## Task Commits
1. **Task 1: bind dispatch endpoint before MarkHealthy (EXEC-01)** — `826bdea` (feat) — pre-committed.
2. **Task 2: prove bind-then-MarkHealthy ordering + dispatch consumer registration** — `ba34b4d` (test) — pre-committed.

**Plan metadata:** final docs commit (this SUMMARY + STATE + ROADMAP + REQUIREMENTS) — created this session.

## Files Modified
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` — dispatch-consumer DI registration excluded from auto-endpoint config.
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — `IReceiveEndpointConnector` ctor dep + runtime bind-then-MarkHealthy completion block.
- `tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs` (new) — EXEC-01 ordered-sequence + bare-name facts.
- `tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs` — `Registers_Dispatch_Consumer` fact.

## Deviations from Plan
None — the plan executed exactly as written. Every acceptance criterion was already satisfied by the pre-committed code; this session added no source changes and required no auto-fixes. (Both pre-existing task commits were verified, not rewritten.)

## Known Stubs
None — the consumer is now fully wired (DI registration + runtime bind). The only deliberate deferral is the LIVE bind-against-RabbitMQ proof, which is Phase 28 E2E (TEST-01) by design — the unit test proves the ordering, the locked decision documents the deferral.

## Threat Flags
None — no new security surface beyond the plan's `<threat_model>`. The runtime bind derives the queue name from the trusted `Id` Guid (identity round-trip); `ExcludeFromConfigureEndpoints` realizes T-27-09 (no wrong-named static queue), the bind→Ready→Healthy order realizes T-27-10 (no consume-before-ready / send-to-missing-queue), and `Immediate(3)` realizes T-27-11 (bounded poison-dispatch retry).

## Next Phase Readiness
- Phase 28 (SourceHash + Processor.Sample + E2E) inherits a complete framework round-trip: identity → definitions → durable `{Id:D}` bind → Healthy → dispatch consume → L2 I/O → ExecutionResult send. The live `ConnectReceiveEndpoint`-against-RabbitMQ proof + the Healthy-after-bind ordering against a real broker are the Phase 28 E2E (TEST-01).
- Phase 27 = 3/3 plans complete; milestone v3.5.0 = 8/8 plans across phases 25-27 (Phase 28 plans TBD).
- No blockers.

---
*Phase: 27-execution-round-trip*
*Completed: 2026-06-02*

## Self-Check: PASSED

All 4 modified files exist on disk; both task commits (`826bdea`, `ba34b4d`) exist in git history; the full suite is 387/387 green.
