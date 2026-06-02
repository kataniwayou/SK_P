# Phase 29: Structured Execution-Scope Logging - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-02
**Phase:** 29-structured-execution-scope-logging
**Areas discussed:** Scope composition, ProcessorId enricher, Test strategy, Minted-id override

---

## A — Scope composition (new filter vs existing correlation filter)

| Option | Description | Selected |
|--------|-------------|----------|
| (a) Five-id filter, correlation stays separate | Execution filter scopes only WorkflowId/StepId/ProcessorId/ExecutionId/EntryId; CorrelationId owned by existing filter; execution filter registered after correlation filter (correlation outer) | ✓ |
| (b) Six-id filter | Execution filter re-scopes CorrelationId too — simpler contract read, but redundant/competing scope entry | |

**User's choice:** (a) — recommended.
**Notes:** Avoids a duplicate CorrelationId scope entry; predictable nesting with correlation as outer scope. `InboundCorrelationConsumeFilter` stays unchanged per SPEC.

---

## B — ProcessorId enricher mechanism

| Option | Description | Selected |
|--------|-------------|----------|
| (a) `BaseProcessor<LogRecord>` | Custom OTel LogRecord processor reads singleton `IProcessorContext.Id`, adds ProcessorId attribute when non-null; covers ALL processor logs incl. pre-identity | ✓ |
| (b) Ambient root BeginScope | One-shot scope opened after identity resolves — misses pre-identity logs, awkward process-wide | |

**User's choice:** (a) — recommended.
**Notes:** True "enriches ALL processor logs" (startup, heartbeat, consume); null-safe before identity resolves.

---

## C — Test strategy (proof bar locked as hermetic + one real-stack)

| Option | Description | Selected |
|--------|-------------|----------|
| (a) Extend SampleRoundTripE2ETests | Hermetic probe-consumer tests (mirror ConsoleCorrelationFilterTests) + extend the live round-trip E2E to assert attributes.WorkflowId in ES | ✓ |
| (b) Extend OrchestrationLogsE2ETests | Orchestrator-only — doesn't exercise the processor path | |

**User's choice:** (a) — recommended.
**Notes:** SampleRoundTripE2ETests exercises the full orchestrator→processor→orchestrator path; reuses the PollEsForLog precedent.

---

## D — Minted-id override in the nested scope

| Option | Description | Selected |
|--------|-------------|----------|
| (a) Nested BeginScope in EntryStepDispatchConsumer | Inner scope (same ExecutionLogScope keys) wraps write/send lines; MEL inner-overrides-outer carries minted ExecutionId+EntryId | ✓ |

**User's choice:** (a) — recommended (sole option).
**Notes:** Inbound ExecutionId/EntryId are Guid.Empty (skipped by the filter); the consumer mints real ones mid-process and the nested scope surfaces them on the write/send lines.

## Claude's Discretion

- Exact `ExecutionLogScope` constant layout (subject to "keys == param names" SPEC constraint)
- Precise `BaseProcessor<LogRecord>` attribute-add API + registration call site
- Scope container shape (`Dictionary` vs `KeyValuePair` list) — match `InboundCorrelationConsumeFilter`

## Deferred Ideas

None — discussion stayed within phase scope.
