# Phase 29: Structured Execution-Scope Logging - Context

**Gathered:** 2026-06-02
**Status:** Ready for planning

<domain>
## Phase Boundary

Wire the six execution ids (`CorrelationId`, `WorkflowId`, `StepId`, `ProcessorId`, `ExecutionId`, `EntryId`) into MEL log **scopes** so they serialize to Elasticsearch `attributes.*` via the existing OTel `IncludeScopes`/`ParseStateValues` bridge — making the orchestrator→processor→orchestrator round-trip queryable by any id without threading ids through method signatures or interpolating them into message text. This phase is logs-only and additive (existing templates untouched).

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**6 requirements are locked.** See `29-SPEC.md` for full requirements, boundaries, and acceptance criteria.

Downstream agents MUST read `29-SPEC.md` before planning or implementing. Requirements are not duplicated here.

**In scope (from SPEC.md):**
- `InboundExecutionScopeConsumeFilter<T>` (open-generic, bus-wide, registered once in `AddBaseConsoleMessaging`)
- `ExecutionLogScope` keys class in `Messaging.Contracts` (keys == param names; `Guid.Empty` skipped)
- Nested `BeginScope` in `EntryStepDispatchConsumer` for the minted `ExecutionId` + output `EntryId`
- OTel `LogRecord` enricher attaching `ProcessorId` from `IProcessorContext.Id` (null-safe) to all processor logs
- Explicit `BeginScope(CorrelationId + WorkflowId)` in `WorkflowFireJob.Execute`
- Hermetic scope-capture tests + one extension of the existing real-stack ES E2E asserting ≥1 execution id

**Out of scope (from SPEC.md):**
- Rewriting/stripping existing named-placeholder log templates (additive only — no log-shape regression)
- An execution-scope filter for WebApi/`BaseApi` (it never consumes `IExecutionCorrelated`; stays `CorrelationId`-only)
- Asserting all six `attributes.*` ids in ES (one real-stack assertion only)
- Any change to `InboundCorrelationConsumeFilter`
- New ids beyond the six on `IExecutionCorrelated`
- Trace/metric scope changes (logs-only)

</spec_lock>

<decisions>
## Implementation Decisions

### Scope composition (filter wiring)
- **D-01:** `InboundExecutionScopeConsumeFilter<T>` scopes ONLY the five execution ids (`WorkflowId`, `StepId`, `ProcessorId`, `ExecutionId`, `EntryId`). `CorrelationId` remains owned solely by the unchanged `InboundCorrelationConsumeFilter` — the execution filter does NOT re-scope it (no duplicate/competing `CorrelationId` scope entry).
- **D-02:** Register the execution filter **after** the correlation filter in `AddBaseConsoleMessaging` (`UseConsumeFilter` order) so `CorrelationId` is the outer scope and the execution id-set nests inside. Both end up in scope state regardless; ordering is for predictable nesting.
- **D-03:** The filter reads ids from the `IExecutionCorrelated` message body (mirrors the correlation filter's body-read pattern), skips any `Guid.Empty` value, and is a pass-through no-op for non-`IExecutionCorrelated` messages.

### ProcessorId enricher mechanism
- **D-04:** Implement `ProcessorId` enrichment as a custom `OpenTelemetry.BaseProcessor<LogRecord>` registered on the processor's logger provider. It reads the singleton `IProcessorContext.Id` and adds a `ProcessorId` attribute only when `Id` is non-null. This covers ALL processor logs (startup, heartbeat, consume) — including those emitted before identity resolves, where it simply adds nothing. Chosen over a one-shot ambient root scope, which would miss pre-identity logs and is awkward to scope process-wide.

### Minted-id override (processor consume path)
- **D-05:** `EntryStepDispatchConsumer` opens a nested `BeginScope` (using the same `ExecutionLogScope` keys) wrapping only the write/send log lines, carrying the per-result minted `ExecutionId` and output `EntryId`. MEL inner-scope-overrides-outer semantics mean those lines report the minted ids rather than the inbound `Guid.Empty` values the filter skipped.

### WorkflowFireJob scope
- **D-06:** `WorkflowFireJob.Execute` opens an explicit `BeginScope` carrying the per-fire `CorrelationId` (via `CorrelationKeys.LogScope`) and the `WorkflowId` (via `ExecutionLogScope`), since the Quartz job runs outside the consume pipeline and the filters never see it.

### Test strategy
- **D-07:** Hermetic tests mirror `ConsoleCorrelationFilterTests` — a probe consumer captures the scope dictionary to assert the filter/nested-scope carry the expected id-set (and that `Guid.Empty` and non-`IExecutionCorrelated` cases behave). A separate hermetic test asserts the `BaseProcessor<LogRecord>` enricher adds `ProcessorId` when `Id` is set and nothing when null.
- **D-08:** The single real-stack ES assertion **extends `SampleRoundTripE2ETests`** (the milestone's live orchestrator→processor→orchestrator round-trip) to assert `attributes.WorkflowId` reaches Elasticsearch from a scope, reusing the existing `PollEsForLog` precedent. `WorkflowId` is the chosen id because it is known at fire time and flows through the entire round-trip. Chosen over extending `OrchestrationLogsE2ETests` (orchestrator-only — doesn't exercise the processor path).

### Claude's Discretion
- Exact `ExecutionLogScope` constant layout (field names/order) — must satisfy SPEC constraint "keys == structured-param names"; otherwise planner/executor choose.
- Precise `BaseProcessor<LogRecord>` attribute-add API surface and registration call site within the processor's OTel logging setup.
- Whether the consume-filter scope is built as a `Dictionary<string,object>` or a list of `KeyValuePair` — match whatever `InboundCorrelationConsumeFilter` does for consistency.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements
- `.planning/phases/29-structured-execution-scope-logging/29-SPEC.md` — Locked requirements, boundaries, and acceptance criteria. MUST read before planning.

### Phase definition
- `.planning/ROADMAP.md` §"Phase 29: Structured Execution-Scope Logging" — goal + 5 success criteria (the SPEC requirements derive from these).

No external ADRs or specs — the approach is fully captured in the SPEC plus the decisions above.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs` — the pattern to mirror for the new filter: body-read id → `logger.BeginScope(Dictionary{ CorrelationKeys.LogScope = corrId })` wrapping `next.Send()`. MUST stay unchanged (SPEC).
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` (`AddBaseConsoleMessaging`, lines ~45-59) — where the three correlation filters register bus-wide via `UseConsumeFilter`/`UseSendFilter`/`UsePublishFilter`; the new execution filter registers here, after the correlation `UseConsumeFilter`.
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` (lines ~43-51) — the OTel logs bridge (`IncludeScopes=true`, `ParseStateValues=true`); reuse unchanged. The `BaseProcessor<LogRecord>` enricher registers into this logging provider (processor side).
- `src/Messaging.Contracts/CorrelationKeys.cs` — precedent for the scope-key constants class (`LogScope = "CorrelationId"`); `ExecutionLogScope` joins it as a sibling (pure POCO, no MassTransit ref).
- `src/Messaging.Contracts/IExecutionCorrelated.cs` — the contract the filter keys off; declares all six ids. `ExecutionResult` + `EntryStepDispatch` implement it.
- `src/BaseProcessor.Core/Identity/IProcessorContext.cs` (`Guid? Id`, line ~36) — read by the `ProcessorId` enricher (null-safe).
- `tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs` — hermetic probe-consumer pattern to mirror for the new filter/scope tests.
- `tests/BaseApi.Tests/Observability/LogExportTests.cs` + `EsIndexNames.cs` (`CorrelationIdFieldPath = "attributes.CorrelationId"`) — the ES `attributes.*` field-path + polling precedent; the new real-stack assertion uses an analogous `attributes.WorkflowId` path.

### Established Patterns
- Scope → ES attribute: a value placed under a fixed scope key surfaces at `attributes.<Key>` in ES via the OTel bridge — proven for `CorrelationId`. The new ids follow the identical path (no new infra).
- Body-read in filters (not envelope-only) — `InboundCorrelationConsumeFilter` reads `(message as ICorrelated)?.CorrelationId`; the execution filter reads `IExecutionCorrelated` members the same way.
- `Guid.Empty`-skip — avoids zero-guid noise attributes (SPEC constraint).

### Integration Points
- `EntryStepDispatchConsumer` (`src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs`) — `ExecutionId` minted ~line 186, `EntryId` ~line 140; nested `BeginScope` (D-05) wraps the write/send lines here.
- `WorkflowFireJob` (`src/Orchestrator/Scheduling/WorkflowFireJob.cs`) — `CorrelationId` minted ~line 54 in `Execute`; explicit `BeginScope` (D-06) wraps the body.
- `ResultConsumer` (`src/Orchestrator/Consumers/ResultConsumer.cs`) — covered ambiently by the bus-wide execution filter (consumes `ExecutionResult`); no per-consumer change needed beyond what the filter provides.

</code_context>

<specifics>
## Specific Ideas

- Assert `attributes.WorkflowId` (not another id) in the real-stack proof — it is the one id known at fire time that traverses the full round-trip, making it the most reliable single round-trip witness.

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope. (Stripping redundant id placeholders from existing templates and asserting all six attributes in ES were explicitly ruled out in the SPEC, not deferred.)

</deferred>

---

*Phase: 29-structured-execution-scope-logging*
*Context gathered: 2026-06-02*
