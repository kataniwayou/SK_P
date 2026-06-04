---
phase: 31-idempotent-execution-exactly-once-effect
plan: 05
subsystem: messaging
tags: [retry, options, ioptions, config-binding, masstransit, usemessageretry, d-10]

# Dependency graph
requires:
  - phase: 31-idempotent-execution-exactly-once-effect
    provides: "Messaging.Contracts.Configuration.RetryOptions (Limit/Strategy) + RetryStrategy enum (Plan 01)"
provides:
  - "All 4 UseMessageRetry sites read Immediate(Limit) from per-process IOptions<RetryOptions> bound from the Retry config section (D-10 single source of truth)"
  - "Retry appsettings sections in Orchestrator + Processor.Sample (Limit=3, Strategy=Immediate)"
  - "RetryOptionsBindFacts — bind-from-section + default-Immediate(3) + enum-by-name unit coverage (req-7 retry half)"
affects: [31-06, phase-32-retry-final-attempt]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Per-process IOptions<RetryOptions> bind mirrors the ProcessorLivenessOptions Configure<T>(GetSection) analog"
    - "ConsumerDefinition ctor injection of IOptions<RetryOptions> (MassTransit resolves definitions from DI)"
    - "EndpointName assignment moved from expression-bodied ctor into a ctor body to accommodate the injected param"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/RetryOptionsBindFacts.cs
  modified:
    - src/Orchestrator/Consumers/ResultConsumerDefinition.cs
    - src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs
    - src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs
    - src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
    - src/Orchestrator/Program.cs
    - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
    - src/Orchestrator/appsettings.json
    - src/Processor.Sample/appsettings.json
    - tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs
    - tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs

key-decisions:
  - "Threaded Options.Create(new RetryOptions()) through the 3 processor-startup tests that construct ProcessorStartupOrchestrator directly (the new ctor param broke positional construction) — fully-qualified Messaging.Contracts.Configuration.RetryOptions to avoid touching usings (Rule 3, mechanical)"
  - "EndpointName moved into the ctor body (was expression-bodied `=> EndpointName = ...`) on the 3 definitions so the IOptions param can be captured to a field"

requirements-completed: [req-7]

# Metrics
duration: 6min
completed: 2026-06-04
---

# Phase 31 Plan 05: Config-Bound Retry Budget Summary

**All 4 hard-coded `Immediate(3)` sites now read the retry `Limit` from a per-process `IOptions<RetryOptions>` bound from the `Retry` appsettings section (D-10 single source of truth), with a `Retry` section in both consoles defaulting to `Immediate(3)` and `RetryOptionsBindFacts` pinning bind / default / enum-by-name — suite stays green (429/429).**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-06-04T13:11:52Z
- **Tasks:** 2
- **Files modified:** 11 (1 created, 10 modified)

## Accomplishments

- **Per-process binding (D-10):** `Orchestrator/Program.cs` and `BaseProcessor.Core/AddBaseProcessor` each add `Configure<RetryOptions>(GetSection("Retry"))` (mirroring the existing `ProcessorLivenessOptions` bind). Absent section → `RetryOptions` defaults (`Immediate(3)`).
- **3 orchestrator ConsumerDefinitions** (`Result`/`Start`/`Stop`) now take an `IOptions<RetryOptions>` primary-ctor param (captured to a field; `EndpointName` moved into the ctor body) and call `UseMessageRetry(r => r.Immediate(retryOptions.Value.Limit))`. The `Ignore<WorkflowRootNotFoundException>()` on Start/Stop is preserved verbatim.
- **The landmine (site 4):** `ProcessorStartupOrchestrator` gained `IOptions<RetryOptions> retryOptions` on its primary ctor (alongside the existing `IOptions<ProcessorLivenessOptions>`); the inline `ConnectReceiveEndpoint` bind lambda reads a ctor-captured `var retryLimit = retryOptions.Value.Limit;`.
- **Appsettings:** both `Orchestrator` and `Processor.Sample` appsettings gained an additive `"Retry": { "Limit": 3, "Strategy": "Immediate" }` block (valid JSON, all existing sections intact).
- **`RetryOptionsBindFacts`** (3 hermetic facts): `Binds_Limit_From_Section` (Retry:Limit=7 → Limit=7, Strategy=Immediate), `Defaults_To_Immediate3_When_Absent` (empty config → Limit=3, Strategy=Immediate), `Strategy_Binds_Enum_ByName` (Retry:Strategy=Exponential → RetryStrategy.Exponential).

Only the `Immediate` branch is wired this phase (D-10); `Strategy` binds but only `Immediate` is honored — no `Interval`/`Exponential` retry branch.

## Task Commits

1. **Task 1: Bind RetryOptions per process + thread Limit into all 4 retry sites** — `9a69e65` (feat)
2. **Task 2: Retry appsettings sections + RetryOptionsBindFacts** — `c6d805e` (test)

## Decisions Made

- **Test ctor-arg threading (Rule 3, mechanical):** three processor-startup tests (`IdentityResolutionFacts`, `SchemaResolutionFacts`, `DispatchBindSequenceFacts`) construct `ProcessorStartupOrchestrator` directly with positional args. The new `IOptions<RetryOptions>` param (inserted after `options`) broke them. Threaded `Options.Create(new Messaging.Contracts.Configuration.RetryOptions())` (default Limit=3) — fully-qualified to avoid adding a `using` to each file. No behavior change (default budget identical to the prior hard-coded `Immediate(3)`).
- **`EndpointName` in ctor body:** the 3 definitions were expression-bodied (`public Def() => EndpointName = ...`). To capture the injected `IOptions` to a field, the ctor became a block body assigning both the field and `EndpointName`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Threaded RetryOptions through 3 direct-construction processor-startup tests**
- **Found during:** Task 1
- **Issue:** Adding `IOptions<RetryOptions>` to the `ProcessorStartupOrchestrator` primary ctor breaks `tests/BaseApi.Tests/Processor/{IdentityResolutionFacts,SchemaResolutionFacts,DispatchBindSequenceFacts}.cs`, which `new` the orchestrator with positional args (CS7036 — would fail `dotnet build SK_P.sln`).
- **Fix:** Inserted `Options.Create(new Messaging.Contracts.Configuration.RetryOptions())` after the `options` arg in each of the 3 call sites (fully-qualified — no `using` change). Default `RetryOptions` Limit=3 → byte-equivalent to the prior hard-coded `Immediate(3)`.
- **Files modified:** tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs, SchemaResolutionFacts.cs, DispatchBindSequenceFacts.cs
- **Verification:** `dotnet build SK_P.sln -c Debug` 0/0; the 3 startup-fact classes pass within the 429/429 hermetic run.
- **Committed in:** `9a69e65` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (Rule 3 - blocking, mechanical test-ctor threading — mirrors the 30-02/30-03 metrics-helper threading precedent). No scope creep; every acceptance criterion met.

## Verification

- `dotnet build SK_P.sln -c Debug` — **0 Warning / 0 Error** (all 8 projects incl. BaseApi.Tests).
- `dotnet test tests/BaseApi.Tests -- --filter-class "*RetryOptionsBindFacts"` — **Passed 3 / Failed 0**.
- `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack"` — **Passed 429 / Failed 0** (426 prior + 3 new, zero regression).
- Grep `r.Immediate(3)` under `src/` — **0 matches** (no hard-coded retry budget survives in any of the 3 definitions or `ProcessorStartupOrchestrator`).
- Both appsettings parse as valid JSON (`json.load` OK).
- `git diff --diff-filter=D HEAD~1 HEAD` (each task commit) — empty (no archive deletions folded in).

## Threat Surface

- **T-31-16 (mitigate — retry desync):** SATISFIED structurally — all 4 `UseMessageRetry` sites now read the ONE shared `RetryOptions.Limit`, the same value Phase 32's final-attempt check will read (D-10). No second source of the budget exists.
- **T-31-15 (accept — unbounded Limit):** unchanged — operator-owned config; default `Immediate(3)`; no external config-injection path (intra-cluster).

No new threat surface introduced (additive config bind + retry-site injection; no new endpoints/auth/file/schema boundaries).

## Notes on Scope

- The live attempt-count proof (changing the configured `Limit` changes the effective retry budget against the real broker) is the req-8 E2E in Plan 06; this plan's unit-tier sample is binding correctness (VALIDATION § Tier 1), satisfied by `RetryOptionsBindFacts`.
- Scoped-commit discipline held: only the plan's `files_modified` set + the 3 Rule-3 test-ctor edits were staged. The in-progress `.planning/` archive deletions in the working tree were left untouched (NOT staged, NOT reverted), per execution instructions.

## Self-Check: PASSED
- FOUND: tests/BaseApi.Tests/Orchestrator/RetryOptionsBindFacts.cs
- FOUND: src/Orchestrator/appsettings.json (Retry section)
- FOUND: src/Processor.Sample/appsettings.json (Retry section)
- FOUND: src/Orchestrator/Consumers/ResultConsumerDefinition.cs (IOptions<RetryOptions> + Immediate(retryOptions.Value.Limit))
- FOUND: src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs (IOptions<RetryOptions> + retryLimit)
- FOUND commit 9a69e65 (Task 1)
- FOUND commit c6d805e (Task 2)

---
*Phase: 31-idempotent-execution-exactly-once-effect*
*Completed: 2026-06-04*
