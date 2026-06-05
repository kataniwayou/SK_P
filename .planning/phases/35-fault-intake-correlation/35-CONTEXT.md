# Phase 35: Fault Intake & Correlation - Context

**Gathered:** 2026-06-05
**Status:** Ready for planning

<domain>
## Phase Boundary

Wire the **production fault-intake path**: replace Phase 34's throwaway `PlaceholderConsumer` with the **two real production fault consumers** — `IConsumer<Fault<EntryStepDispatch>>` and `IConsumer<Fault<ExecutionResult>>` — on the Keeper's stable competing-consumer queue (`keeper-fault-recovery`). Each consumed fault: unwraps `context.Message.Message` → extracts the 6-id `IExecutionCorrelated` correlation tuple + `H` → **opens the propagated execution log-scope** → emits one OTel log consistent with the other consoles → acks. Confirm the `_error`/DLQ-1 transport-exhaustion forensic path stays **separate** from Keeper's worklist (Keeper recovers off the `Fault<T>` pub/sub stream, never reads `_error`).

**Scope anchor:** INTAKE-03, KMET-04 (and only these — see requirement map in REQUIREMENTS.md).

**Explicitly OUT of this phase** (deferred — do NOT build here):
- L2 health-probe recovery loop — **Phase 36** (PROBE-01..05).
- Re-injection to origin endpoint by type — **Phase 36** (INTAKE-04).
- `keeper-dlq` / DLQ-2, the two-DLQ topology, the shared error-transport that *builds* the consolidated TTL'd DLQ-1 — **Phase 36** (DLQ-01..04).
- Pause/resume coordination — **Phase 37** (PAUSE-01..05).
- Keeper meter + counters/histograms + close gate — **Phase 38** (KMET-01..03, TEST-01..03).

Phase 35 is **logs-only** (KMET-04) — NO metrics/instruments are added here.

</domain>

<decisions>
## Implementation Decisions

### DLQ-1 scope boundary (GA-1 — the crux)
- **D-01:** Phase 35 builds **NO DLQ-1 / TTL / shared-error-transport topology.** That construction is DLQ-04 → **Phase 36**. Today faults land in per-consumer `{queue}_error` queues by MassTransit default (confirmed: no centralized DLQ-1, no `x-message-ttl`, no `x-dead-letter-exchange` in the current codebase).
- **D-02:** Phase 35 satisfies INTAKE-03's **Phase-35 slice** by *confirming/observing* (a standing RealStack assertion) the **separation property**: Keeper recovers strictly off the `Fault<T>` pub/sub stream, **never reads the `_error`/DLQ-1 queue**, and recovered work is **never double-processed** from it. The "consolidates into a TTL'd forensic DLQ-1" full property completes in Phase 36 (shared error-transport, DLQ-04). Consistent with Phase 33 D-10 ("DLQ-1 built in Phase 36 per INTAKE-03/DLQ-02").

### Fault-consumer endpoint topology (GA-2)
- **D-03:** Both `Fault<EntryStepDispatch>` and `Fault<ExecutionResult>` consumers **colocate on the single stable competing-consumer queue `keeper-fault-recovery`** (`KeeperQueues.FaultRecovery`) — one consolidated Keeper worklist, round-robined across replicas (evolves Phase 34's single-queue design; keeps KEEP-02 round-robin + net-zero close-gate SHA intact). **Replace the `PlaceholderConsumer` + `PlaceholderConsumerDefinition` + `KeeperPlaceholder` message wholesale** — they are throwaway scaffolding.
- **D-04:** Keep the `KeeperQueues.FaultRecovery` name (`"keeper-fault-recovery"`). Both consumers inherit the shared `UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))` pattern from the `"Retry"` section (DLQ-04 / Phase-34 D-09), mirroring `PlaceholderConsumerDefinition`.
- **Claude's Discretion:** the MassTransit colocation mechanism — same `EndpointName` on two `ConsumerDefinition`s vs one explicit `cfg.ReceiveEndpoint(KeeperQueues.FaultRecovery, e => { e.ConfigureConsumer<A>; e.ConfigureConsumer<B>; })`. Either yields the one-queue/two-type-binding worklist; planner's call.

### Consumer body + log-scope mechanism (GA-3 — KMET-04)
- **D-05:** **`Fault<T>` is NOT `IExecutionCorrelated`** — the bus-wide `InboundExecutionScopeConsumeFilter<T>` `is not IExecutionCorrelated` branch passes through WITHOUT opening a scope for fault consumers. Keeper therefore **opens the execution log-scope MANUALLY** from the unwrapped inner message (`context.Message.Message`).
- **D-06:** Phase-35 consumer body (per fault type): unwrap `context.Message.Message` → read the 6-id tuple + `H` off the inner `IExecutionCorrelated` message → `logger.BeginScope(...)` with the execution-scope ids → emit one structured log line → **ack**. **No recovery work in Phase 35** (the L2 probe loop + re-inject slot in between extract and ack in Phase 36). "Observe-and-ack" is the deliberate Phase-35 shape.
- **D-07:** To avoid duplicating the filter's empty-skip logic, **refactor the scope-dictionary builder out of `InboundExecutionScopeConsumeFilter` into a small shared helper** (e.g. `ExecutionLogScope.BuildState(IExecutionCorrelated)` returning the `Dictionary<string,object>` with the `Guid.Empty`/empty-string skips) and call it from BOTH the filter and the Keeper consumers. Single source of truth for the scope-key set (`WorkflowId`/`StepId`/`ProcessorId`/`ExecutionId`/`EntryId`).
- **Claude's Discretion:** refactor-shared-helper (recommended) vs inline-duplicate in Keeper — if the refactor proves to touch too much of the filter's hot path, inline is acceptable, but the scope-key set MUST stay identical to the other consoles.

### Log content / level (GA-5 — KMET-04 detail)
- **D-08:** `Information`-level structured "keeper fault intake" log, carrying correlationId (via the outer `InboundCorrelationConsumeFilter` body-correlation, which DOES fire) + the 5 execution-scope ids (via the manual `BeginScope`) + the fault type + the originating `Fault<T>.Exceptions[0]` exception message/summary. Match the other consoles' log conventions (message phrasing, structured fields).
- **Claude's Discretion:** exact event wording, which `Fault<T>.Exceptions` fields to surface, level nuance (Information vs Warning) — within the "consistent with other consoles" bar.

### End-to-end correlation proof (GA-4 — SC3)
- **D-09:** Prove SC3 (a faulted message processed by Keeper produces a correlated ES log) against the **running Keeper container** — NOT an in-test bus. **Extend the standing Phase-33 `FaultRecoverySpikeE2ETests`** (or add a sibling RealStack test) to live-trip a `Fault<T>` (proven WRONGTYPE recipe), then assert via `PollEsForLog` that the **Keeper container** emitted an ES log carrying the propagated correlationId + execution-scope ids.
- **Claude's Discretion:** extend-existing-spike-test vs new-Keeper-specific RealStack test; settle-window durations; the exact `PollEsForLog` query shape — builder choices within the established RealStack rig.

### Claude's Discretion (summary)
- MassTransit endpoint colocation mechanism (D-03).
- Shared-helper-vs-inline for the scope builder (D-07).
- Log event wording + exception-field surfacing + level nuance (D-08).
- Test vehicle: extend spike vs new test, settle windows, query shape (D-09).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents (researcher, planner, executor) MUST read these before planning or implementing.**

### Phase scope & requirements
- `.planning/ROADMAP.md` §"Phase 35: Fault Intake & Correlation" — goal + 3 success criteria + requirement map.
- `.planning/REQUIREMENTS.md` — **INTAKE-03** (line ~26: `_error` → TTL'd forensic DLQ-1, Keeper recovers off `Fault<T>`, never re-processes from `_error`/DLQ-1) and **KMET-04** (line ~58: Keeper OTel logs carry the propagated correlationId + execution-scope ids). Downstream-only (NOT this phase, read for the boundary): INTAKE-04, DLQ-01..04 (all Phase 36).
- `.planning/phases/33-fault-recovery-spike-de-risk/33-CONTEXT.md` §D-10 — the recorded `_error`/DLQ-1 retention decision (DLQ-1 built in Phase 36; triage axis = mechanism, not origin component).
- `.planning/phases/34-keeper-console-foundation/34-CONTEXT.md` — the placeholder this phase replaces (D-01/D-02/D-03) + the shared-queue/retry pattern Phase 35 inherits.

### The placeholder to replace (Phase 34 output)
- `src/Keeper/Program.cs` — composition root; the `AddBaseConsoleMessaging(..., x => x.AddConsumer<PlaceholderConsumer, PlaceholderConsumerDefinition>())` line that Phase 35 swaps to the two real fault consumers.
- `src/Keeper/Consumers/PlaceholderConsumer.cs`, `PlaceholderConsumerDefinition.cs`, `KeeperPlaceholder.cs` — throwaway scaffolding; **delete wholesale** (D-03).

### Log-scope helper + scope keys (KMET-04, D-05/D-07)
- `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` — the auto scope filter (lines ~17–40) that does NOT fire on `Fault<T>`; refactor its dict-builder into a shared helper (D-07).
- `src/Messaging.Contracts/ExecutionLogScope.cs` — the scope-key constants (`WorkflowId`/`StepId`/`ProcessorId`/`ExecutionId`/`EntryId`); candidate home for the shared `BuildState(...)` helper.
- `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs` (+ the registration in `MessagingServiceCollectionExtensions.cs` ~line 51–52) — the outer body-correlation filter that DOES fire on `Fault<T>` (supplies correlationId).

### Wire contracts (unwrap target + 6-id tuple + H)
- `src/Messaging.Contracts/IExecutionCorrelated.cs` + `src/Messaging.Contracts/ICorrelated.cs` — the tuple (`CorrelationId`, `ExecutionId`, `WorkflowId`, `StepId`, `ProcessorId`, `EntryId`).
- `src/Messaging.Contracts/EntryStepDispatch.cs`, `src/Messaging.Contracts/ExecutionResult.cs` — the two inner messages wrapped by `Fault<T>`; both carry `H` (a record property, NOT an interface member). Reached via `context.Message.Message` (double `.Message`).
- `src/Messaging.Contracts/Hashing/MessageIdentity.cs` — `ComputeH(...)` (5-field identity; executionId excluded) — for context on what `H` is.
- `src/Messaging.Contracts/KeeperQueues.cs` — `FaultRecovery` (`"keeper-fault-recovery"`) endpoint-name const (D-03/D-04).
- `src/Messaging.Contracts/Configuration/RetryOptions.cs` — the shared `Immediate(N)` budget both fault consumers bind (D-04).

### Consumer wiring precedent (structural templates)
- `src/Orchestrator/Consumers/ResultConsumer.cs` + `ResultConsumerDefinition.cs` — stable competing-consumer endpoint + `flag[H]` gate + `UseMessageRetry(Immediate(Limit))` shape; the `ExecutionResult` reader the `Fault<ExecutionResult>` consumer mirrors.
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` — the `EntryStepDispatch` reader + nested `BeginScope` precedent for the `Fault<EntryStepDispatch>` consumer.

### RealStack proof rig (SC3, D-09)
- `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` (Phase 33, `[Trait("Category","RealStack")]`) — the standing bind → unwrap → re-inject → `flag[H]`-collapse guard; extend for the Keeper-container correlated-ES-log assertion.
- `tests/BaseApi.Tests/Orchestrator/IdempotentExactlyOnceE2ETests.cs` + `CorrelationPropagationE2ETests.cs` — `PollEsForLog`, embedded-SourceHash, liveness-poll, net-zero `skp:*` teardown precedents.

### Compose / runtime knobs (RealStack)
- `compose.yaml` §`keeper:` — the multi-replica Keeper tier (Phase 34); rebuild before any live proof (embedded SourceHash must match — see prior-phase close-gate note).
- OTLP: `OTEL_EXPORTER_OTLP_ENDPOINT` env var is the live knob; ES message body is `body.text` (per project OTLP/E2E gotchas).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`InboundExecutionScopeConsumeFilter` dict-builder** — the exact scope-key set + `Guid.Empty`/empty-string skip logic Keeper needs; refactor into a shared `ExecutionLogScope.BuildState(IExecutionCorrelated)` (D-07).
- **`ResultConsumerDefinition` / `PlaceholderConsumerDefinition` shape** — drop-in template for the two new `Fault<T>` `ConsumerDefinition`s (stable `EndpointName`, `IOptions<RetryOptions>` ctor, `UseMessageRetry(Immediate(Limit))`).
- **`ResultConsumer` / `EntryStepDispatchConsumer` bodies** — `context.Message` read + `BeginScope` + the structured-log conventions to mirror (Keeper double-unwraps `context.Message.Message`).
- **Phase-33 `FaultRecoverySpikeE2ETests` + the RealStack harness** (`PollEsForLog`, WRONGTYPE live-trip recipe, embedded-SourceHash, net-zero teardown) — reusable verbatim for SC3 (D-09).

### Established Patterns
- `Fault<T>` consumed by binding `IConsumer<Fault<T>>`; inner message via `context.Message.Message` (double `.Message`); `Fault<T>` itself is NOT `IExecutionCorrelated` → manual scope (D-05).
- Competing-consumer (round-robin) = plain `AddConsumer<,>()` + stable `ConsumerDefinition.EndpointName`; both fault consumers share ONE such endpoint (D-03).
- Shared `Immediate(N)` from `RetryOptions."Retry"` section across ALL consoles (DLQ-04); `Immediate` is the ONLY wired strategy this milestone (Interval/Exponential structured-for but deliberately not branched — cross-console deferral).
- Per-consumer `{queue}_error` dead-letter is MassTransit default today; NO centralized DLQ-1/TTL exists yet (built Phase 36).

### Integration Points
- `src/Keeper/Program.cs` — swap the placeholder `AddConsumer` for the two `Fault<T>` consumers (one shared endpoint).
- New consumer/definition files under `src/Keeper/Consumers/` (replacing the 3 placeholder files).
- `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` + `src/Messaging.Contracts/ExecutionLogScope.cs` — shared scope-builder refactor (D-07) — touches a base library used by ALL consoles, so existing scope tests must stay green.
- `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` — extend for the Keeper-container correlated-ES-log assertion (SC3).

</code_context>

<specifics>
## Specific Ideas

- "Replace the placeholder wholesale" — Phase 34's `PlaceholderConsumer`/`KeeperPlaceholder` were explicitly throwaway scaffolding; Phase 35 deletes them, not extends them.
- "Observe-and-ack" is the deliberate Phase-35 consumer shape — the recovery loop (probe + re-inject) slots in between extract and ack during Phase 36, so Phase 35's consumer is intentionally a thin intake+correlation skeleton.
- The DLQ-1 line between Phase 35 and 36 is set by the requirement map: 35 *confirms the separation*, 36 *builds the consolidation* (TTL'd shared error-transport). Don't let SC1's "consolidates into TTL'd DLQ-1" wording pull TTL/topology work into Phase 35.
- The scope-builder refactor is a base-library change — keep every console's existing log-scope behavior byte-identical (single source of truth, no per-console drift).

</specifics>

<deferred>
## Deferred Ideas

- L2 health-probe recovery loop + re-inject-to-origin-by-type (INTAKE-04) + `keeper-dlq` (DLQ-2) + the two-DLQ split + the shared error-transport that builds the consolidated TTL'd DLQ-1 (DLQ-01..04) — **Phase 36**.
- `PauseWorkflow`/`ResumeWorkflow` contracts + orchestrator pending-recovery coordination — **Phase 37** (PAUSE-01..05).
- Keeper meter + `keeper_fault_consumed` / `keeper_l2_probe_failed` / DLQ-depth counters + real-stack close gate — **Phase 38** (KMET-01..03, TEST-01..03).

*No reviewed-but-deferred todos (none matched this phase).*

</deferred>

---

*Phase: 35-fault-intake-correlation*
*Context gathered: 2026-06-05*
