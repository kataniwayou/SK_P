# Phase 29: Structured Execution-Scope Logging — Specification

**Created:** 2026-06-02
**Ambiguity score:** 0.17 (gate: ≤ 0.20)
**Requirements:** 6 locked

## Goal

Every console emits the execution id-set (`CorrelationId`, `WorkflowId`, `StepId`, `ProcessorId`, `ExecutionId`, `EntryId`) as structured log **scope** values under fixed keys — carried ambiently via MEL `BeginScope` and serialized by the existing OTel `IncludeScopes`/`ParseStateValues` bridge into Elasticsearch `attributes.*` — so the orchestrator→processor→orchestrator round-trip is queryable by any id without threading ids through method signatures, and without interpolating ids into message text (T-18-04).

## Background

Grounded in the codebase scout (2026-06-02):

- `CorrelationId` is **already** scoped end-to-end: `InboundCorrelationConsumeFilter<T>` (`src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs:39`) opens `logger.BeginScope({ CorrelationKeys.LogScope = corrId })`; the OTel logs bridge (`BaseConsoleObservabilityExtensions.cs:43-51` and `BaseApi.Core/.../ObservabilityServiceCollectionExtensions.cs:48-56`) sets `IncludeScopes=true` + `ParseStateValues=true`; ES receives it at `attributes.CorrelationId` — proven by `LogExportTests` and `OrchestrationLogsE2ETests`.
- `IExecutionCorrelated` (`src/Messaging.Contracts/IExecutionCorrelated.cs`) already declares all six ids; `ExecutionResult` and `EntryStepDispatch` implement it.
- `IProcessorContext.Id` (`src/BaseProcessor.Core/Identity/IProcessorContext.cs:36`) exposes `Guid? Id` (null until identity resolves in startup Loop A).
- `Messaging.Contracts` has `CorrelationKeys` (`LogScope = "CorrelationId"`) but **no** `ExecutionLogScope` keys class for the other five ids.
- **Gap:** the other five ids are NOT in scopes. `ResultConsumer`, `EntryStepDispatchConsumer`, and `WorkflowFireJob` either log ids as named template placeholders or do not surface them at all. `ExecutionId` is minted per result in `EntryStepDispatchConsumer.BuildCompleted/Failed/Cancelled` (`:186`) and `EntryId` in `BuildCompleted` (`:140`); `CorrelationId` is minted per fire in `WorkflowFireJob.Execute` (`:54`).

This phase adds the missing ambient scopes (additive — existing templates are untouched) so the round-trip becomes queryable by every id.

## Requirements

1. **LOG-01 — Execution ids appear as scope-sourced ES attributes**: All six ids surface in Elasticsearch as `attributes.CorrelationId` / `WorkflowId` / `StepId` / `ProcessorId` / `ExecutionId` / `EntryId`, sourced from log **scopes** (fixed-key values, never interpolated into message text — T-18-04), via the existing `IncludeScopes`+`ParseStateValues` OTel bridge.
   - Current: only `attributes.CorrelationId` reaches ES from a scope; the other five ids are absent from scopes
   - Target: scope values for the full id-set flow through the unchanged OTel bridge into `attributes.*`
   - Acceptance: the real-stack E2E assertion (LOG-06) confirms at least one newly-scoped execution id (e.g. `attributes.WorkflowId`) round-trips to ES from a scope; OTel logging config (`IncludeScopes`/`ParseStateValues`) is reused unchanged

2. **LOG-02 — Bus-wide execution-scope consume filter**: A new open-generic `InboundExecutionScopeConsumeFilter<T>` scopes the execution id-set for `IExecutionCorrelated` messages and passes through all other messages; registered once in `AddBaseConsoleMessaging` so BOTH the orchestrator (`ResultConsumer` ← `ExecutionResult`) and the processor (`EntryStepDispatchConsumer` ← `EntryStepDispatch`) are covered with no per-console wiring. `InboundCorrelationConsumeFilter` is left unchanged.
   - Current: no execution-scope filter exists; only `InboundCorrelationConsumeFilter` runs in the consume pipeline
   - Target: `InboundExecutionScopeConsumeFilter<T>` registered bus-wide in `MessagingServiceCollectionExtensions.AddBaseConsoleMessaging`; non-`IExecutionCorrelated` messages pass through untouched
   - Acceptance: a hermetic harness test confirms the filter opens a scope carrying the execution id-set for an `IExecutionCorrelated` message and is a no-op for a non-`IExecutionCorrelated` message; `InboundCorrelationConsumeFilter.cs` is byte-unchanged

3. **LOG-03 — Shared `ExecutionLogScope` keys class**: A single `ExecutionLogScope` constants class in `Messaging.Contracts` is the source of truth for scope keys, with key strings equal to the structured-param names (`WorkflowId`, `StepId`, `ProcessorId`, `ExecutionId`, `EntryId`) so scope-derived and param-derived attributes coincide on the same ES field; `Guid.Empty` values are skipped (no zero-guid noise attributes).
   - Current: no execution scope-key class exists; only `CorrelationKeys.LogScope` is defined
   - Target: `ExecutionLogScope` added to `Messaging.Contracts` (pure POCO leaf, no MassTransit ref); the filter (LOG-02) and nested scope (LOG-04) read keys from it; entries whose value is `Guid.Empty` are omitted from the scope
   - Acceptance: a hermetic test asserts each key string equals its param name and that a `Guid.Empty`-valued id produces NO corresponding scope entry

4. **LOG-04 — Processor-minted ids scoped + `ProcessorId` enriches all processor logs**: The per-result minted `ExecutionId` and output `EntryId` are captured via a nested `BeginScope` in `EntryStepDispatchConsumer` (overriding inbound values for the write/send log lines), and `ProcessorId` enriches ALL processor logs (startup, heartbeat, consume) via an OTel `LogRecord` enricher reading `IProcessorContext.Id` (null-safe before identity resolves).
   - Current: minted `ExecutionId`/`EntryId` are not scoped; `ProcessorId` appears on no processor logs ambiently
   - Target: nested `BeginScope` in `EntryStepDispatchConsumer` carries the minted `ExecutionId`+`EntryId`; an OTel log enricher attaches `ProcessorId` from `IProcessorContext.Id` to every processor log, emitting nothing when `Id` is null
   - Acceptance: a hermetic test confirms the nested scope carries the minted ids on the write/send path; a test confirms the enricher attaches `ProcessorId` when `Id` is set and attaches nothing (no exception, no zero-guid) when `Id` is null

5. **LOG-05 — `WorkflowFireJob` explicit scope**: `WorkflowFireJob` (a Quartz job, outside the consume pipeline) opens an explicit `BeginScope(CorrelationId + WorkflowId)` in `Execute` so its fire logs correlate with the round-trip it triggers.
   - Current: `WorkflowFireJob.Execute` logs without any scope; its fire logs carry neither id ambiently
   - Target: `Execute` wraps its body in `BeginScope` carrying the per-fire `CorrelationId` and the `WorkflowId`, using the `ExecutionLogScope`/`CorrelationKeys` keys
   - Acceptance: a hermetic test confirms `WorkflowFireJob` log lines emitted during `Execute` carry the `CorrelationId` and `WorkflowId` scope values

6. **LOG-06 — No regression + real-stack proof**: The full hermetic + real-stack suite stays GREEN with no log-shape regression, and the close-gate triple-SHA still holds; the proof bar is hermetic scope-capture tests PLUS one extension of the existing real-stack E2E asserting ≥1 execution id round-trips to ES.
   - Current: existing suite is GREEN (395 facts at Phase 28 close); no test asserts execution ids in ES from scopes
   - Target: existing log templates are untouched (additive scopes only); the existing real-stack E2E is extended to assert at least one `attributes.<executionId>` reaches ES
   - Acceptance: full hermetic suite GREEN with no removed/changed existing log assertions; the extended real-stack E2E passes; the phase close-gate (3× full-suite GREEN + triple-SHA BEFORE==AFTER) holds

## Boundaries

**In scope:**
- `InboundExecutionScopeConsumeFilter<T>` (open-generic, bus-wide, registered once in `AddBaseConsoleMessaging`)
- `ExecutionLogScope` keys class in `Messaging.Contracts` (keys == param names; `Guid.Empty` skipped)
- Nested `BeginScope` in `EntryStepDispatchConsumer` for the minted `ExecutionId` + output `EntryId`
- OTel `LogRecord` enricher attaching `ProcessorId` from `IProcessorContext.Id` (null-safe) to all processor logs
- Explicit `BeginScope(CorrelationId + WorkflowId)` in `WorkflowFireJob.Execute`
- Hermetic scope-capture tests + one extension of the existing real-stack ES E2E asserting ≥1 execution id

**Out of scope:**
- Rewriting/stripping existing named-placeholder log templates — chose additive (1a); avoids log-shape regression (SC#5)
- An execution-scope filter for WebApi/`BaseApi` — it never consumes `IExecutionCorrelated` (it is the producer side); stays `CorrelationId`-only (2a)
- Asserting all six `attributes.*` ids in ES — chose one real-stack assertion (3a); full assertion is over-brittle for the value
- Any change to `InboundCorrelationConsumeFilter` — SC#2 requires it unchanged
- New ids beyond the six already on `IExecutionCorrelated` — contract is fixed
- Trace/metric scope changes — this phase is logs-only

## Constraints

- `ExecutionLogScope` key strings MUST equal the structured-param names (`WorkflowId`, …) so scope-derived and param-derived attributes coincide on the same ES field
- `Guid.Empty` id values MUST be skipped (no zero-guid noise attributes)
- The `ProcessorId` enricher MUST be null-safe — `IProcessorContext.Id` is null until identity resolves; emit nothing rather than `Guid.Empty`
- Reuse the existing OTel bridge (`IncludeScopes=true`, `ParseStateValues=true`) — do NOT reconfigure logging
- `Messaging.Contracts` stays a pure POCO leaf (no MassTransit reference) — `ExecutionLogScope` is plain string constants
- No log-shape regression; full hermetic + real-stack suite GREEN; close-gate triple-SHA (`psql \l` + `redis-cli --scan` + `rabbitmqctl list_queues`) BEFORE==AFTER must hold

## Acceptance Criteria

- [ ] At least one newly-scoped execution id (e.g. `attributes.WorkflowId`) round-trips from a log scope to Elasticsearch in the extended real-stack E2E
- [ ] `InboundExecutionScopeConsumeFilter<T>` is registered bus-wide in `AddBaseConsoleMessaging` and covers both orchestrator and processor consumers with no per-console wiring
- [ ] `InboundExecutionScopeConsumeFilter<T>` is a pass-through no-op for non-`IExecutionCorrelated` messages
- [ ] `InboundCorrelationConsumeFilter` is unchanged
- [ ] `ExecutionLogScope` exists in `Messaging.Contracts` with key strings equal to the param names; a `Guid.Empty` value produces no scope entry
- [ ] `EntryStepDispatchConsumer` nested `BeginScope` carries the minted `ExecutionId` + `EntryId` on the write/send log lines
- [ ] An OTel log enricher attaches `ProcessorId` to processor logs when `IProcessorContext.Id` is set, and attaches nothing (no exception, no zero-guid) when it is null
- [ ] `WorkflowFireJob.Execute` log lines carry `CorrelationId` + `WorkflowId` scope values
- [ ] Existing log templates/assertions are unchanged (additive only — no log-shape regression)
- [ ] Full hermetic suite GREEN; extended real-stack E2E passes; phase close-gate (3× full-suite GREEN + triple-SHA BEFORE==AFTER) holds

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                              |
|--------------------|-------|------|--------|----------------------------------------------------|
| Goal Clarity       | 0.88  | 0.75 | ✓      | Six named ids, scope-sourced, ES-queryable         |
| Boundary Clarity   | 0.85  | 0.70 | ✓      | Additive (1a) + project set (2a) locked            |
| Constraint Clarity | 0.72  | 0.65 | ✓      | Empty-skip, null-safe enricher, reuse OTel bridge  |
| Acceptance Criteria| 0.82  | 0.70 | ✓      | Proof bar = hermetic + one real-stack (3a)         |
| **Ambiguity**      | 0.17  | ≤0.20| ✓      |                                                    |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective              | Question summary                                  | Decision locked                                                        |
|-------|--------------------------|---------------------------------------------------|------------------------------------------------------------------------|
| 1     | Researcher + Boundary    | Existing templates: additive or rewrite?          | Additive only (1a) — add scopes, leave templates; no log-shape regress |
| 1     | Boundary Keeper          | Which projects get the execution-scope filter?    | Orchestrator + BaseProcessor.Core (2a); WebApi stays CorrelationId-only|
| 1     | Failure Analyst (proof)  | Acceptance/proof bar for ES round-trip?           | Hermetic + one real-stack ES assertion (3a)                            |

---

*Phase: 29-structured-execution-scope-logging*
*Spec created: 2026-06-02*
*Next step: /gsd-discuss-phase 29 — implementation decisions (filter wiring, enricher registration, scope-key layout, test design)*
