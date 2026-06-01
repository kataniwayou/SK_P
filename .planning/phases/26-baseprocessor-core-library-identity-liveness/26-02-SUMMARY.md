---
phase: 26-baseprocessor-core-library-identity-liveness
plan: 02
subsystem: infra
tags: [masstransit, request-client, dotnet, processor, identity, startup, di, backoff]

# Dependency graph
requires:
  - phase: 26-baseprocessor-core-library-identity-liveness
    plan: 01
    provides: "IProcessorContext / ISourceHashProvider / ProcessorLivenessOptions contracts + Wave 0 ProcessorTestHarness + CONFIRMED exchange:{name} scheme + dual-response GetResponse<TFound,TNotFound> / Response<T1,T2>.Is API"
  - phase: 25-shared-contracts-webapi-responders
    provides: "GetProcessorBySourceHash/GetSchemaDefinition dual-response contracts + ProcessorQueues endpoint-name constants + WebApi responders on named ReceiveEndpoints"
provides:
  - "AddBaseProcessor composition root (BPC-03): folds AddBaseConsole + AddBaseConsoleMessaging (two exchange:-scheme IRequestClients), registers TimeProvider/ISourceHashProvider/IProcessorContext/the startup orchestrator, and REMOVES the base StartupCompletionService (D-02)"
  - "ProcessorStartupOrchestrator BackgroundService (IDENT-04/SCHEMA-01/02/RPC-04): Loop A identity-by-SourceHash + Loop B per-non-null-definition via IRequestClient dual-response, unbounded retry + bounded exponential backoff, never queries the config schema id, tolerates boot-before-register, marks Healthy + flips the startup gate on completion"
affects: [phase-26-plan-03-liveness-heartbeat, phase-27-execution-round-trip]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Composition root folds the BaseConsole three-call seam internally; request clients go in the AddBaseConsoleMessaging configureConsumers lambda on the exchange:{name} scheme (RPC-04)"
    - "StartupCompletionService removal in the composition root (operates on IServiceCollection) so MarkReady fires on Healthy, not host-start (D-02 — verbatim Orchestrator/Program.cs:63-68 adapted to services)"
    - "Unbounded retry / bounded backoff BackgroundService: break only on Found, loop past NotFound + RequestTimeoutException; 3-arg Task.Delay(delay, clock, ct) for FakeTimeProvider-drivable tests"
    - "BackoffAsync returns the NEXT delay (TimeSpan?) instead of a ref param — async methods cannot have ref/out parameters"

key-files:
  created:
    - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
    - src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
    - tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs
    - tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs
    - tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs
  modified: []

key-decisions:
  - "BackoffAsync returns TimeSpan? (next delay; null = cancelled) rather than taking `ref TimeSpan delay` — CS1988 forbids ref/in/out parameters on async methods. The doubling math moved into the helper; callers assign the returned next-delay."
  - "AddBaseProcessor request-client resolution + options-binding tests use `await using BuildServiceProvider(true)` — the MassTransit container registers UsageTracker as IAsyncDisposable, so synchronous `using`/Dispose throws."
  - "Tests construct ProcessorStartupOrchestrator directly (request clients resolved from a DI scope per the Wave 0 scoped-client correction) and drive it via StartAsync/StopAsync, pumping a FakeTimeProvider until IsHealthy — no real-time sleeping, CTS timeout fails a hang fast."
  - "SchemaResolutionFacts uses a dedicated capturing harness (FixedIdentityResponder carrying caller-set schema Ids + CapturingSchemaResponder recording every queried Id) — the Wave 0 ResponderSequence returns all-null schema Ids so it cannot drive Loop B."

requirements-completed: [BPC-03, IDENT-04, RPC-04, SCHEMA-01, SCHEMA-02]

# Metrics
duration: 8min
completed: 2026-06-01
---

# Phase 26 Plan 02: AddBaseProcessor Composition Root + Two-Loop Startup Orchestrator Summary

**Built the processor's runtime brain — the `AddBaseProcessor` composition root (folds the BaseConsole stack + both exchange:-scheme request clients, removes the base StartupCompletionService so readiness flips on Healthy) and the `ProcessorStartupOrchestrator` BackgroundService (Loop A identity-by-SourceHash + Loop B per-non-null-definition via MassTransit IRequestClient dual-response, unbounded retry + bounded exponential backoff, never queries the config schema id, boot-before-register tolerant) — proven by 5 new fact methods, full Processor slice 28/28 GREEN.**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-06-01T19:21:00Z
- **Completed:** 2026-06-01T19:29:08Z
- **Tasks:** 2
- **Files created:** 5

## Accomplishments
- `AddBaseProcessor(cfg)` composition root: `AddBaseConsole` (Redis soft-dep + embedded health) + `AddBaseConsoleMessaging` with both `IRequestClient<GetProcessorBySourceHash>` / `IRequestClient<GetSchemaDefinition>` on the `exchange:{ProcessorQueues.name}` scheme (RPC-04), `Configure<ProcessorLivenessOptions>("Processor")`, idempotent `TryAddSingleton(TimeProvider.System)`, `ISourceHashProvider`/`IProcessorContext` singletons, the orchestrator hosted service, and the verbatim `StartupCompletionService` removal loop (D-02). NO dispatch consumer this phase (Phase 27); the heartbeat hosted service is deliberately left to Plan 03.
- `ProcessorStartupOrchestrator` `BackgroundService` (primary-ctor injection mirroring HydrationBackgroundService): Loop A resolves identity via dual-response `GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>`, retrying on BOTH `RequestTimeoutException` AND the dual-response NotFound with `Math.Min(delay*2, cap)` backoff, breaking only on Found; Loop B iterates `{ InputSchemaId, OutputSchemaId }`, skips nulls (no request), never reads the config schema id, resolves each via `GetResponse<SchemaDefinitionFound, SchemaDefinitionNotFound>`; on completion `context.MarkHealthy()` then `gate.MarkReady()`.
- Five new fact methods across 3 slices: AddBaseProcessorFacts (graph + both request clients + StartupCompletionService removal, 3 methods), IdentityResolutionFacts (NotFound->NotFound->Found retry-then-resolve, 1), SchemaResolutionFacts (never-config + null-input-still-Healthy, 2). Full `*Processor*` slice 28/28 GREEN (22 Plan 01 + 6 new).

## Task Commits

1. **Task 1: AddBaseProcessor composition root + StartupCompletionService removal** - `5eba602` (feat)
2. **Task 2: ProcessorStartupOrchestrator two-loop identity+definition resolution** - `7c18ea1` (feat)

_Plan metadata commit follows this SUMMARY._

## Files Created
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` - The `AddBaseProcessor` composition root (BPC-03)
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` - Two-loop startup orchestrator (IDENT-04 / SCHEMA-01/02 / RPC-04)
- `tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs` - Descriptor-inspection: registration graph + request clients + gate removal
- `tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs` - Loop A retry-then-resolve against the Wave 0 harness, FakeTimeProvider-driven
- `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs` - Loop B never-config + null-input-still-Healthy via a capturing harness

## Decisions Made
- **`BackoffAsync` returns the next delay (`TimeSpan?`), not a `ref` param.** The plan's pseudocode mutated `delay` inline; an async helper cannot take `ref/in/out` (CS1988). The helper now delays on the injected clock, returns the doubled-and-capped next delay, and returns `null` on shutdown cancellation so the caller returns. Behavior is identical (1s -> 2s -> ... -> cap).
- **MassTransit container needs async disposal.** `AddMassTransit` registers `UsageTracker` as `IAsyncDisposable`; the `AddBaseProcessorFacts` resolution/options tests therefore use `await using ... BuildServiceProvider(true)` (a synchronous `using` throws `InvalidOperationException`).
- **Tests construct the orchestrator directly** with request clients resolved from a DI scope (Wave 0 scoped-client correction), driving it via `StartAsync`/`StopAsync` and pumping a `FakeTimeProvider` past the backoff caps until `IsHealthy` — no real sleeping; a CTS timeout fails a hang fast.
- **A dedicated capturing harness for Loop B.** The Wave 0 `ResponderSequence` returns all-null schema Ids, so SchemaResolutionFacts adds a `FixedIdentityResponder` (caller-configured schema Ids) + `CapturingSchemaResponder` (records every queried Id) to assert input+output resolve and the config schema id is never queried.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] `ref TimeSpan delay` on an async backoff helper does not compile**
- **Found during:** Task 2 (first build of ProcessorStartupOrchestrator)
- **Issue:** The plan pseudocode kept `delay` mutated inline across the retry; factoring the cancellation-safe delay into an async helper triggered CS1988 (async methods cannot have ref/in/out parameters).
- **Fix:** `BackoffAsync(TimeSpan delay, TimeSpan cap, ct)` returns the NEXT delay as `TimeSpan?` (null = shutdown); both loops assign the returned value. Doubling/cap math unchanged.
- **Files modified:** src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
- **Verification:** Builds clean; both Loop A/B slices GREEN.
- **Committed in:** 7c18ea1 (Task 2 commit)

**2. [Rule 1 - Bug] MassTransit container requires async disposal in the descriptor-inspection facts**
- **Found during:** Task 1 (AddBaseProcessorFacts first run — `Registers_Both_Request_Clients` failed)
- **Issue:** `services.BuildServiceProvider()` disposed synchronously throws `InvalidOperationException: 'MassTransit.UsageTracking.UsageTracker' type only implements IAsyncDisposable`.
- **Fix:** Made the affected facts `async Task` and switched to `await using var provider = services.BuildServiceProvider(true)`.
- **Files modified:** tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs
- **Verification:** AddBaseProcessorFacts 3/3 GREEN.
- **Committed in:** 5eba602 (Task 1 commit)

**3. [Rule 3 - Blocking] Comment tokens tripped the literal grep acceptance criteria**
- **Found during:** Tasks 1 & 2 (acceptance-criteria grep checks)
- **Issue:** The acceptance criteria require `grep AddConsumer` (composition root) and `grep ConfigSchemaId` (orchestrator) to return NO matches; the code had zero real calls/reads but doc comments contained those literal tokens.
- **Fix:** Reworded the comments ("no consumer is registered", "the config schema id") so the greps return 0 — proving the invariants literally as the criteria intend. No behavioral change.
- **Files modified:** BaseProcessorServiceCollectionExtensions.cs, ProcessorStartupOrchestrator.cs
- **Verification:** `grep AddConsumer` = 0, `grep ConfigSchemaId` = 0 in the orchestrator; all slices re-run GREEN after the edits.
- **Committed in:** 5eba602 / 7c18ea1

---

**Total deviations:** 3 auto-fixed (2 compile/runtime bugs, 1 blocking grep-criteria reconciliation). No scope creep, no architectural changes.

## Verification Evidence
- `*AddBaseProcessorFacts*` slice: Passed 3, Failed 0.
- `*IdentityResolutionFacts*` + `*SchemaResolutionFacts*` slices: Passed 3, Failed 0.
- Full `*Processor*` slice (Plan 01 + Plan 02): **Passed 28, Failed 0** (22 prior + 6 new).
- grep acceptance criteria all satisfied: `exchange:` (both queues) present; `StartupCompletionService` removal present; `TryAddSingleton(TimeProvider.System)` present; `AddConsumer` = 0 in composition root; `GetResponse<ProcessorIdentityFound,...>` + `GetResponse<SchemaDefinitionFound,...>` present; `ConfigSchemaId` = 0 in orchestrator; `MarkHealthy` + `MarkReady` + `Math.Min` present.

> **Wave-merge full-suite no-regression** (`dotnet test SK_P.sln` against the live Postgres/Redis/RabbitMQ stack with the real-stack E2E tests) is the wave-close gate, not this plan's per-plan verification — it runs at phase close after Plan 03, not here.

## Known Stubs
None. The composition root + orchestrator are fully wired against the Wave 0 in-memory harness; no hardcoded empty values flow to any data sink. (The `ProcessorLivenessHeartbeat` hosted service that consumes `IProcessorContext.IsHealthy` is Plan 03 by design — its absence is the documented phase boundary, not a stub.)

## Issues Encountered
- None beyond the three auto-fixed deviations above.

## User Setup Required
None - the slices run entirely against an in-memory MassTransit harness (no real broker).

## Next Phase Readiness
- Plan 03 (liveness heartbeat) can consume the populated `IProcessorContext` (Id + definitions + `IsHealthy`/`WhenHealthy`) the orchestrator now fills, and registers its `AddHostedService<ProcessorLivenessHeartbeat>()` line in this same `AddBaseProcessor` composition root.
- No blockers.

## Self-Check: PASSED

All 5 created files verified present on disk; both task commits (5eba602, 7c18ea1) verified in git log.

---
*Phase: 26-baseprocessor-core-library-identity-liveness*
*Completed: 2026-06-01*
