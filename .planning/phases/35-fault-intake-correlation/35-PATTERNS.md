# Phase 35: Fault Intake & Correlation - Pattern Map

**Mapped:** 2026-06-05
**Files analyzed:** 9 (3 CREATE-consumers, 1-2 CREATE-definitions, 1 CREATE-test, 3 MODIFY, 3 DELETE)
**Analogs found:** 8 / 8 (every new/modified file has an exact or role-match in-repo analog; no RESEARCH-only fallbacks)

> All analogs are in-repo and verified at file:line. The load-bearing `Fault<T>` double-unwrap + bind + flag[H]-collapse mechanics were already proven LIVE by the Phase-33 spike — Phase 35 is thin production wiring of that proven mechanism. **The single most important correctness point the executor MUST encode: the bus-wide correlation filter CANNOT recover the inner correlationId from a `Fault<T>` envelope — the Keeper consumer body must open the CorrelationId scope MANUALLY** (RESEARCH Pattern 2 / Pitfall 1).

---

## File Classification

| New/Modified/Deleted File | Role | Data Flow | Closest Analog | Match Quality |
|---------------------------|------|-----------|----------------|---------------|
| `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` (NEW) | consumer | event-driven (pub/sub fault intake) | `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (nested BeginScope) + spike `FaultDispatchProbe` (double-unwrap) | exact (composite) |
| `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` (NEW) | consumer | event-driven (pub/sub fault intake) | `src/Orchestrator/Consumers/ResultConsumer.cs` (ExecutionResult reader) + spike `FaultResultProbe` | exact (composite) |
| `src/Keeper/Consumers/FaultEntryStepDispatchConsumerDefinition.cs` (NEW) | config (consumer definition) | request-response (endpoint/retry seam) | `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs` | exact |
| `src/Keeper/Consumers/FaultExecutionResultConsumerDefinition.cs` (NEW; or one shared) | config (consumer definition) | request-response (endpoint/retry seam) | `src/Orchestrator/Consumers/ResultConsumerDefinition.cs` + `PlaceholderConsumerDefinition.cs` | exact |
| `tests/BaseApi.Tests/Orchestrator/KeeperFaultIntakeE2ETests.cs` (NEW) | test (RealStack) | event-driven + ES readback | `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` | exact (clone harness) |
| `tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs` (NEW; Wave-0 hermetic) | test (hermetic) | scope capture | `tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs` | exact (clone capturing-provider rig) |
| `src/Keeper/Program.cs` (MODIFY) | config (composition root) | DI registration | self (lines 27-28) — swap consumer registration | self |
| `src/Messaging.Contracts/ExecutionLogScope.cs` (MODIFY) | utility (scope-key POCO) | transform (build scope dict) | `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs:28-33` (the block to extract) | exact (refactor source) |
| `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` (MODIFY) | middleware (consume filter) | request-response (pipeline) | self (lines 28-36) — replace inline dict-build with `BuildState(ec)` call | self |
| `src/Keeper/Consumers/PlaceholderConsumer.cs` (DELETE) | — | — | — | — |
| `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs` (DELETE) | — | — | — | — |
| `src/Keeper/Consumers/KeeperPlaceholder.cs` (DELETE) | — | — | — | — |

---

## Pattern Assignments

### `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` (NEW — consumer, event-driven)

**Primary analog:** `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs:307-317` (`FaultDispatchProbe` — the proven-live double-unwrap) + `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:170-188` (nested `BeginScope` convention).

**COPY — the class shape + double-unwrap** (from `FaultRecoverySpikeE2ETests.cs:311-315`):
```csharp
public sealed class FaultEntryStepDispatchConsumer(ILogger<FaultEntryStepDispatchConsumer> logger)
    : IConsumer<Fault<EntryStepDispatch>>
{
    public Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context)
    {
        var inner = context.Message.Message;   // double .Message — VERBATIM inner IExecutionCorrelated instance
        ...
    }
}
```
- `inner` is the verbatim original `EntryStepDispatch` (no re-deserialize). It exposes `CorrelationId`, `ExecutionId`, `WorkflowId`, `StepId`, `ProcessorId`, `EntryId` (`IExecutionCorrelated`, `src/Messaging.Contracts/IExecutionCorrelated.cs:10-17`) plus `H` (record property, `EntryStepDispatch.cs:17`).

**COPY — the nested-scope + structured-log convention** (mirror `EntryStepDispatchConsumer.cs:170-188`):
```csharp
using (logger.BeginScope(new Dictionary<string, object>
{
    [ExecutionLogScope.ExecutionId] = executionId.ToString(),
    [ExecutionLogScope.EntryId]     = blobHash,
}))
{
    logger.LogInformation("Dispatch {CorrelationId}: ...", dispatch.CorrelationId);
}
```
Mirror: values placed under fixed `ExecutionLogScope` keys, NEVER interpolated into the template (T-18-04 security convention, `InboundExecutionScopeConsumeFilter.cs:14-15`). `inner.H` + ids + `ex.Message` go in as structured params only.

**CHANGE vs analogs (the Phase-35-specific delta — REQUIRED):**
1. **Manual OUTER CorrelationId scope** — the spike probe did NOT log; the production consumer MUST. Because `InboundCorrelationConsumeFilter.Send` reads `context.Message as ICorrelated` (`InboundCorrelationConsumeFilter.cs:35`) and `Fault<EntryStepDispatch>` is NOT `ICorrelated`, it falls back to `context.CorrelationId` or a fresh Guid (`:36-37`) — the WRONG id. Wrap the body in an OUTER scope from the inner message:
   ```csharp
   using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = inner.CorrelationId.ToString() }))
   ```
   (`CorrelationKeys.LogScope` == `"CorrelationId"`, `src/Messaging.Contracts/CorrelationKeys.cs:7`.) MEL inner-overrides-outer so this body scope wins over the filter's wrong id.
2. **Use the D-07 shared helper** for the exec-id scope (NOT the inline dict the processor analog uses):
   ```csharp
   using (logger.BeginScope(ExecutionLogScope.BuildState(inner)))   // the 5 exec ids w/ Guid.Empty/empty-string skips
   ```
3. **Surface the fault exception** — read `context.Message.Exceptions` (array of `ExceptionInfo`), use `[0]`, surface `ExceptionType` + `Message` only (NOT `StackTrace` at Information — V7/DoS, RESEARCH Security). Nullable-safe: `Exceptions is { Length: > 0 } exs ? exs[0] : null`.
4. **`Information`-level "keeper fault intake" log** (D-08), then `return Task.CompletedTask;` — observe-and-ack, NO recovery work (D-06).

**Sketch (RESEARCH Pattern 2, `35-RESEARCH.md:195-216`):**
```csharp
public Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context)
{
    var inner = context.Message.Message;
    var ex    = context.Message.Exceptions is { Length: > 0 } exs ? exs[0] : null;
    using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = inner.CorrelationId.ToString() }))
    using (logger.BeginScope(ExecutionLogScope.BuildState(inner)))
    {
        logger.LogInformation(
            "Keeper fault intake: {FaultType} for H={H} — {ExceptionType}: {ExceptionMessage}",
            nameof(EntryStepDispatch), inner.H, ex?.ExceptionType, ex?.Message);
    }
    return Task.CompletedTask;   // observe-and-ack (D-06)
}
```

---

### `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` (NEW — consumer, event-driven)

**Primary analog:** identical to the dispatch consumer above with the inner type swapped to `ExecutionResult` (spike `FaultResultProbe`, `FaultRecoverySpikeE2ETests.cs:323-333`). `ExecutionResult` also implements `IExecutionCorrelated` and carries `H` (`src/Messaging.Contracts/ExecutionResult.cs:11,18`). Disambiguate the type alias at the top of the file (mirror `ResultConsumer.cs:8`):
```csharp
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult
```
**CHANGE:** `IConsumer<Fault<ExecutionResult>>`, `nameof(ExecutionResult)` in the log template. Everything else (manual CorrelationId scope, `BuildState`, exception surfacing, observe-and-ack) is identical.

---

### `src/Keeper/Consumers/FaultEntryStepDispatchConsumerDefinition.cs` (NEW — config, endpoint/retry seam)

**Analog:** `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs:15-39` (the exact template) — confirmed identical shape to `src/Orchestrator/Consumers/ResultConsumerDefinition.cs:22-43`.

**COPY verbatim except the consumer type** (`PlaceholderConsumerDefinition.cs:15-39`):
```csharp
public sealed class FaultEntryStepDispatchConsumerDefinition : ConsumerDefinition<FaultEntryStepDispatchConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;
    public FaultEntryStepDispatchConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = KeeperQueues.FaultRecovery;   // "keeper-fault-recovery" — SAME on both definitions (D-03)
    }
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<FaultEntryStepDispatchConsumer> consumerConfigurator,
        IRegistrationContext context)
        => endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));
}
```
- `EndpointName = KeeperQueues.FaultRecovery` (`"keeper-fault-recovery"`, `src/Messaging.Contracts/KeeperQueues.cs:15`) — UNCHANGED const → net-zero close-gate SHA preserved (KEEP-02).
- `UseMessageRetry(Immediate(Limit))` bound from the `"Retry"` section via `IOptions<RetryOptions>` (D-04) — same single source of truth both Keeper/Orchestrator read.
- **KEEP the `RetryOptions.Strategy` comment** from `PlaceholderConsumerDefinition.cs:32-36` — `Immediate` is the ONLY wired strategy this milestone; do not add Strategy-switching (cross-console deferral, project memory + Phase-34 WR-01).

**GOTCHA (RESEARCH Pitfall 3, `35-RESEARCH.md:184,307-310`):** `UseMessageRetry` is PER-ENDPOINT, not per-consumer. Two definitions naming `keeper-fault-recovery` both call `endpointConfigurator.UseMessageRetry(...)` on the SAME endpoint → double-registers the retry middleware. The Limit is identical (same `"Retry"` section) so it is functionally harmless, but for unambiguous intent, **have ONE definition own the endpoint-level `UseMessageRetry`** (leave the second definition's `ConfigureConsumer` body empty/no-retry), OR use the explicit `ReceiveEndpoint` form (alternative below). Planner's call (D-03 discretion); document whichever is chosen.

---

### `src/Keeper/Consumers/FaultExecutionResultConsumerDefinition.cs` (NEW — config) *(or collapse to one explicit ReceiveEndpoint)*

**Analog:** same `PlaceholderConsumerDefinition.cs:15-39` template, `ConsumerDefinition<FaultExecutionResultConsumer>`, SAME `EndpointName = KeeperQueues.FaultRecovery`. Same retry note (see GOTCHA above — only one definition should own the endpoint retry).

**ALTERNATIVE (D-03 discretion, RESEARCH `35-RESEARCH.md:188`):** one explicit `cfg.ReceiveEndpoint(KeeperQueues.FaultRecovery, e => { e.UseMessageRetry(...); e.ConfigureConsumer<FaultEntryStepDispatchConsumer>(ctx); e.ConfigureConsumer<FaultExecutionResultConsumer>(ctx); })` via the `configureBus` seam (`MessagingServiceCollectionExtensions.cs` ~:37,55-58). Makes single-retry ownership explicit; trades off the `ConsumerDefinition.EndpointName` convention. **RESEARCH recommends two-definitions for codebase consistency.**

---

### `src/Keeper/Program.cs` (MODIFY — composition root)

**Self-analog:** lines 27-28 (the placeholder registration to swap).

**CHANGE — swap the single placeholder `AddConsumer` for the two fault consumers** (RESEARCH `35-RESEARCH.md:176-182`):
```csharp
// REPLACE lines 27-28:
builder.Services.AddBaseConsoleMessaging(builder.Configuration, x =>
{
    x.AddConsumer<FaultEntryStepDispatchConsumer, FaultEntryStepDispatchConsumerDefinition>();
    x.AddConsumer<FaultExecutionResultConsumer,   FaultExecutionResultConsumerDefinition>();
});
```
**KEEP UNCHANGED:** `AddBaseConsoleObservability` (line 17), `AddBaseConsole` (line 18), `Configure<RetryOptions>(GetSection("Retry"))` (line 22) — these supply the bus/retry budget the new consumers bind. Update the `using Keeper.Consumers;` import block as needed (line 7 already present).

---

### `src/Messaging.Contracts/ExecutionLogScope.cs` (MODIFY — utility, D-07 helper home)

**Analog (refactor SOURCE):** the inline dict-build in `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs:28-33`.

**ADD — extract that block byte-identically into a static helper** (RESEARCH Pattern 3, `35-RESEARCH.md:227-236`):
```csharp
public static Dictionary<string, object> BuildState(IExecutionCorrelated ec)
{
    var state = new Dictionary<string, object>();
    if (ec.WorkflowId  != Guid.Empty) state[WorkflowId]  = ec.WorkflowId.ToString();
    if (ec.StepId      != Guid.Empty) state[StepId]      = ec.StepId.ToString();
    if (ec.ProcessorId != Guid.Empty) state[ProcessorId] = ec.ProcessorId.ToString();
    if (ec.ExecutionId != Guid.Empty) state[ExecutionId] = ec.ExecutionId.ToString();
    if (!string.IsNullOrEmpty(ec.EntryId)) state[EntryId] = ec.EntryId;
    return state;
}
```
**MUST stay byte-identical to the filter's current rules:** `!= Guid.Empty` for the four Guids; `!string.IsNullOrEmpty` for the string `EntryId`; `EntryId` stored VERBATIM (no `.ToString()`); key set EXACTLY `{WorkflowId, StepId, ProcessorId, ExecutionId, EntryId}` — **NO CorrelationId** (that is `CorrelationKeys.LogScope`, owned separately). `ExecutionLogScope` is a pure POCO leaf (no MassTransit ref, `ExecutionLogScope.cs:8`); `IExecutionCorrelated` lives in the same assembly — the helper compiles with no new reference, and is reachable from Keeper (`Keeper.csproj:50` references `Messaging.Contracts`).

---

### `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` (MODIFY — middleware)

**Self-analog:** lines 28-36.

**CHANGE — replace the inline dict-build (lines 28-33) with the shared helper call** (RESEARCH `35-RESEARCH.md:242-243`), keeping observable behavior byte-identical:
```csharp
// lines 28-36 collapse to:
using (logger.BeginScope(ExecutionLogScope.BuildState(ec)))
    await next.Send(context);
```
**KEEP UNCHANGED:** the `is not IExecutionCorrelated` pass-through (lines 22-26) and `Probe` (line 39). The filter already `using Messaging.Contracts;` (line 2) so `ExecutionLogScope.BuildState` is in scope. This is a BASE-LIBRARY change used by ALL consoles — the four `ConsoleExecutionScopeFilterTests` cases + `ExecutionLogScopeKeyTests` are the regression guard (see Regression Guards).

---

### `tests/BaseApi.Tests/Orchestrator/KeeperFaultIntakeE2ETests.cs` (NEW — RealStack, SC3)

**Analog:** `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` (clone the harness; sibling, do NOT mutate the standing spike — RESEARCH Open Q1).

**COPY verbatim (the reusable rig):**
- Traits: `[Trait("Category","E2E")]` + `[Trait("Category","RealStack")]` + `[Collection("Observability")]` (`FaultRecoverySpikeE2ETests.cs:54-56`).
- `RealStackWebAppFactory` + `InitializeAsync` + env-var host overrides incl. `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` (`:95-97`, factory `:743-820`).
- Genuine embedded SourceHash reflection off `Processor.Sample.SampleProcessor` (`:99-107`); `SeedProcessorAsync`/`SeedStepAsync`/`SeedWorkflowAsync` with `cron:"* * * * *"` (`:107-112`).
- `PollForHealthyLivenessAsync(procId, ct)` liveness gate (`:116`).
- **The proven WRONGTYPE live-trip:** build the entry-step dispatch with `MessageIdentity.EntryEntryId`/`ComputeH` (`:143-152`), then `ArmWrongTypePoisonAsync(L2ProjectionKeys.Flag(dispatchH))` as a LIST → `EntryStepDispatchConsumer` dedup-gate GET throws WRONGTYPE on every delivery → `Immediate(N)` exhausts → `Fault<EntryStepDispatch>` fans out (`:154-156`, recipe at `ArmWrongTypePoisonAsync:381-387`).
- Net-zero `skp:*` teardown: `factory.ParentIndexMembersToSrem` / `factory.L2KeysToCleanup` register the workflow/step/poison keys + snapshot `data:*`/`flag:*` before the trip (`:118-125`).
- ES query builder shape + `PollEsForLog` (`BuildEffectQuery:518-534`).

**CHANGE vs the spike (the SC3-specific delta):**
1. **NO in-test Fault probe / no re-inject** — instead, let the RUNNING Keeper container consume the published `Fault<T>` (D-09). Do NOT clear the poison (you WANT the fault published).
2. **Assert the KEEPER container's correlated ES log** — adapt `BuildEffectQuery` (`:518-534`): `service.name` → `"keeper"` (`resource.attributes.service.name = "keeper"`, from `src/Keeper/appsettings.json` Service.Name; compose sets no override), `attributes.CorrelationId = dCorr:D`, `attributes.StepId = stepId:D`, `wildcard body.text = "*keeper fault intake*"` (the chosen D-08 phrasing). `Assert.NotNull(hit)` via `ElasticsearchTestClient.PollEsForLog`, settle window `EsPollTimeoutMs = 120_000` (`:80`).
3. **Container rebuild mandatory** before the run: `docker compose up -d --build keeper processor-sample orchestrator baseapi-service` — Keeper's consumers changed; a stale SourceHash runs the old placeholder and no intake log appears (Pitfall 5 / project memory).

---

### `tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs` (NEW — hermetic, Wave-0, SC2 fast)

**Analog:** `tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs` (clone the capturing-provider rig).

**COPY verbatim:** the `CapturingProvider`/`CapturingLogger` scope-capturing `ILoggerProvider` double (`ConsoleExecutionScopeFilterTests.cs:65-90`), the `BuildHarness(...)` in-memory `AddMassTransitTestHarness` wiring (`:92-105`), and the `ExecutionScope(capturing)` scope-extraction helper (`:107-113`).

**CHANGE:** wire the new `FaultEntryStepDispatchConsumer` (and `FaultExecutionResultConsumer`) as the SUT instead of `ExecProbeConsumer`; publish a `Fault<EntryStepDispatch>` carrying a known inner `IExecutionCorrelated`. **Assert the captured scope carries BOTH** the `CorrelationKeys.LogScope` (`"CorrelationId"`) key == `inner.CorrelationId` AND the 5 `ExecutionLogScope` keys (the Phase-35 delta vs the filter test, which asserts NO CorrelationId key). This is the fast SC2 guard for the manual-CorrelationId-scope correctness point.

---

### DELETE (D-03 — replace placeholder wholesale)

| File | Reason |
|------|--------|
| `src/Keeper/Consumers/PlaceholderConsumer.cs` | Throwaway no-op (`IConsumer<KeeperPlaceholder>`) — replaced by the two real fault consumers. |
| `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs` | Its `EndpointName`/retry shape is COPIED into the new definitions first, then this file is deleted. |
| `src/Keeper/Consumers/KeeperPlaceholder.cs` | Throwaway `ICorrelated` message — no longer published once Program.cs swaps consumers. |

---

## Shared Patterns

### Manual CorrelationId scope on `Fault<T>` (the SC3-critical pattern — applies to BOTH new consumers)
**Source of the problem:** `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs:35-37` — `(context.Message as ICorrelated)?.CorrelationId ?? context.CorrelationId ?? Guid.NewGuid()`. For a `Fault<T>` envelope the `as ICorrelated` is null → wrong id.
**Apply to:** `FaultEntryStepDispatchConsumer`, `FaultExecutionResultConsumer` — wrap an OUTER `BeginScope([CorrelationKeys.LogScope] = inner.CorrelationId.ToString())` before the log. MEL inner-overrides-outer, so the body wins over the filter's wrong id. Without this, SC3's "correlated by correlationId" FAILS.
```csharp
using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = inner.CorrelationId.ToString() }))
```

### Execution-scope builder (D-07 — single source of truth)
**Source:** `src/Messaging.Contracts/ExecutionLogScope.cs` (new `BuildState`, extracted from `InboundExecutionScopeConsumeFilter.cs:28-33`).
**Apply to:** the filter AND both Keeper consumers. The `Guid.Empty`/empty-string skip rules + the exact 5-key set must NOT drift (regression-guarded).
```csharp
using (logger.BeginScope(ExecutionLogScope.BuildState(inner)))
```

### Endpoint/retry definition (competing-consumer + Immediate(N))
**Source:** `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs:15-39` (== `ResultConsumerDefinition.cs:22-43`).
**Apply to:** both new fault `ConsumerDefinition`s — `ConsumerDefinition<T>`, `IOptions<RetryOptions>` ctor, `EndpointName = KeeperQueues.FaultRecovery`, `UseMessageRetry(Immediate(Limit))`. ONE definition owns the endpoint retry (Pitfall 3). Keep the `Strategy`-not-wired comment.

### Structured-log security (T-18-04 — applies to both consumers)
**Source:** `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs:14-15`, `EntryStepDispatchConsumer.cs:99,170-188`.
**Apply to:** ids, `inner.H`, and `ex.Message` go in as STRUCTURED PARAMS under fixed keys/template holes — NEVER string-concatenated/interpolated (log-injection mitigation). Surface `ExceptionType` + `Message` only; keep `StackTrace` OUT of the Information log (unbounded-attribute DoS, V7).

### Double-unwrap `Fault<T>` → inner (proven live)
**Source:** `FaultRecoverySpikeE2ETests.cs:313,329`.
**Apply to:** both consumers — `var inner = context.Message.Message;` (double `.Message`). `Fault.Message` IS the verbatim original instance; NO header parsing, NO re-deserialize.

---

## No Analog Found

*(none — every new/modified file maps to an in-repo analog at a cited file:line)*

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| — | — | — | All Phase-35 files have exact or composite in-repo analogs; RESEARCH provides only the `ExceptionInfo` field-name confirmation (A1), resolvable at build. |

---

## Regression Guards (must stay GREEN after the D-07 base-library refactor)

| Test file | What it pins |
|-----------|--------------|
| `tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs` | Case A (5 keys, NO CorrelationId), B (Guid.Empty skip), C (non-IExecutionCorrelated pass-through), D (empty-string EntryId skip) — the EXACT `BuildState` behavior |
| `tests/BaseApi.Tests/Contracts/ExecutionLogScopeKeyTests.cs` | each `ExecutionLogScope` key string == its param name |
| `tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs`, `EntryStepDispatchRuntimeScopeTests.cs` | processor scope behavior using `ExecutionLogScope` keys |
| `tests/BaseApi.Tests/Orchestrator/WorkflowFireJobScopeTests.cs`, `SampleRoundTripE2ETests.cs` | fire-job + end-to-end scope propagation |
| `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs` | ProcessorId scope/enrich |
| `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` | the standing spike (must not break if cloned, not mutated) |

---

## Metadata

**Analog search scope:** `src/Keeper/Consumers/`, `src/Orchestrator/Consumers/`, `src/BaseProcessor.Core/Processing/`, `src/BaseConsole.Core/Messaging/`, `src/Messaging.Contracts/`, `tests/BaseApi.Tests/{Console,Orchestrator}/`.
**Files scanned (read at file:line):** Program.cs, Placeholder{Consumer,ConsumerDefinition,}.cs, KeeperPlaceholder.cs, InboundExecutionScopeConsumeFilter.cs, InboundCorrelationConsumeFilter.cs, ExecutionLogScope.cs, CorrelationKeys.cs, IExecutionCorrelated.cs, KeeperQueues.cs, EntryStepDispatch.cs, ExecutionResult.cs, ResultConsumer.cs, ResultConsumerDefinition.cs, EntryStepDispatchConsumer.cs, FaultRecoverySpikeE2ETests.cs, ConsoleExecutionScopeFilterTests.cs, Keeper.csproj.
**Pattern extraction date:** 2026-06-05
