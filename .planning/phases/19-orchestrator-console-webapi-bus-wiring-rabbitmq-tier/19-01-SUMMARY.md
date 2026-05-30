---
phase: 19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier
plan: 01
subsystem: messaging-correlation
tags: [messaging, correlation, masstransit, contracts, console]
requires:
  - Messaging.Contracts (Phase 17) — ICorrelated vocabulary + Start/Stop control records
  - BaseConsole.Core inbound consume filter (Phase 18, CORR-01)
provides:
  - "ICorrelated slim contract { Guid CorrelationId } — single body-carried correlation member"
  - "StartOrchestration : ICorrelated (init-set CorrelationId, retains Guid[] WorkflowIds)"
  - "StopOrchestration : ICorrelated (mirror)"
  - "InboundCorrelationConsumeFilter reading correlation from the message BODY"
affects:
  - All downstream Phase 19 plans (19-02 Orchestrator console, 19-03/04 WebApi publish join) compile against the slim contract + body-reading filter
tech-stack:
  added: []
  patterns:
    - "Body-carried correlation (D-01): correlation id lives on the message body via ICorrelated, not the MassTransit envelope"
    - "Non-positional init member on a positional record to implement a get-only interface (Pitfall 5)"
key-files:
  created: []
  modified:
    - src/Messaging.Contracts/ICorrelated.cs
    - src/Messaging.Contracts/StartOrchestration.cs
    - src/Messaging.Contracts/StopOrchestration.cs
    - src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs
    - tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs
decisions:
  - "ICorrelated narrowed 6→1 Guids (zero production implementers removed members; only the filter test broke)"
  - "Body test made a genuine discriminator: publish bodyId on body + a DIFFERENT envelopeId on the envelope, assert accessor==bodyId (MassTransit's by-convention envelope population from the CorrelationId property would otherwise mask body-vs-envelope)"
  - "IExecutionCorrelated NOT defined — deferred to the Processor milestone (D-01)"
metrics:
  duration: ~12min
  completed: 2026-05-30
---

# Phase 19 Plan 01: ICorrelated Slim + Body-Carried Correlation Reconciliation Summary

Reconciled the shipped Phase 17/18 code to the body-carried per-stage correlation model (D-01): slimmed `ICorrelated` to a single `{ Guid CorrelationId }`, made `StartOrchestration`/`StopOrchestration` implement it with an init-set member, and re-pointed `InboundCorrelationConsumeFilter` to read the correlation off the message body instead of the MassTransit envelope. Full SK_P.sln suite stays GREEN (250/250) and the Release build is zero-warning.

## What Was Built

- **Task 1 — Slim `ICorrelated` + Start/Stop implement it** (commit `8ff3d69`)
  - `ICorrelated` reduced from 6 get-only Guids (CorrelationId, ExecutionId, WorkflowId, StepId, ProcessorId, EntryId) to a single get-only `Guid CorrelationId`.
  - `StartOrchestration(Guid[] WorkflowIds) : ICorrelated` with `public Guid CorrelationId { get; init; }` (non-positional init member per Pitfall 5); `WorkflowIds` positional param retained.
  - `StopOrchestration` mirrors exactly.
  - `IExecutionCorrelated` intentionally NOT defined (deferred to the Processor milestone, D-01).
  - Verified: `dotnet build src/Messaging.Contracts ... -c Debug` → 0 Warning / 0 Error.

- **Task 2 — Body re-point + filter-test rewrite** (TDD; commits `eb22777` RED, `d77cb10` GREEN)
  - `ConsoleCorrelationFilterTests` rewritten: `ProbeMessage` slimmed to `record ProbeMessage(Guid CorrelationId) : ICorrelated`; added a `PlainMessage` (non-ICorrelated) tolerance case.
  - `InboundCorrelationConsumeFilter` correlation read changed to `(context.Message as ICorrelated)?.CorrelationId.ToString() ?? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString()`. `where T : class`, the `string?` accessor contract, `accessor.Set`, and `CorrelationKeys.LogScope` are all unchanged. XML doc updated.
  - Verified: both Console filter tests GREEN against the body-reading filter.

- **Task 3 — Full-suite regression gate** (verification only, no file changes)
  - Grep `\.ExecutionId|\.StepId|\.ProcessorId|\.EntryId` across `src/`: all hits are on entity/DTO/junction-config types (StepEntity, AssignmentEntity, ProcessorEntity, junction rows) — zero hits on any `ICorrelated`-typed value. Blast-radius assumption (Phase 17 D-09: zero production implementers) confirmed.
  - `dotnet test SK_P.sln` → Failed: 0, Passed: 250, Total: 250.
  - `dotnet build SK_P.sln -c Release` → 0 Warning / 0 Error.

## Deviations from Plan

None for Tasks 1 and 3. One TDD-process correction in Task 2 (not a code deviation):

**RED fail-fast investigation (Task 2)** — The first RED run of `Inbound_Filter_Populates_Accessor_From_Body` PASSED against the old envelope-reading filter, which the TDD gate flags as a stop-and-investigate. Root cause: MassTransit's in-memory transport populates `context.CorrelationId` by convention from a message property named `CorrelationId`; with the slim `ProbeMessage(Guid CorrelationId)`, the envelope and body values were identical, so the test could not distinguish body-read from envelope-read. Fix: the test now publishes the body `bodyId` while stamping a DIFFERENT `envelopeId` on the envelope (`ctx => ctx.CorrelationId = envelopeId`) and asserts `accessor == bodyId.ToString()`. RED then genuinely failed against the old filter (accessor got `envelopeId`), and GREEN passed after the body re-point. This made the test a true body-vs-envelope discriminator.

## TDD Gate Compliance

Task 2 (`tdd="true"`) gate sequence satisfied in git log:
1. RED — `test(19-01): rewrite ConsoleCorrelationFilterTests for body-sourced correlation` (`eb22777`), failing against the envelope-reading filter.
2. GREEN — `feat(19-01): re-point inbound filter to read body CorrelationId (D-01)` (`d77cb10`).
3. REFACTOR — none needed (filter change is minimal).

## Threat Surface

Per the plan's threat register: the correlation value now comes from the deserialized message body (`ICorrelated.CorrelationId`). It is read as a typed `Guid` (no string injection), only `.ToString()`'d into the structured `CorrelationKeys.LogScope` log-scope key — never used in a query, path, or command (T-19-corr-integrity, mitigated). Non-ICorrelated messages fall back through `?? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString()` with `where T : class` preserved — no NRE on bus-wide registration (T-19-corr-null, accepted). The tolerance test (`Inbound_Filter_Tolerates_NonCorrelated_Message`) exercises this path. No new threat surface beyond the plan's register.

## Verification Evidence

| Check | Result |
|-------|--------|
| `dotnet build src/Messaging.Contracts -c Debug --nologo` | 0 Warning / 0 Error |
| Console filter tests (body-sourced + tolerance) | 2/2 Passed |
| Grep removed-member access on ICorrelated-typed values in src/ | 0 hits |
| `dotnet test SK_P.sln --nologo` | Failed: 0, Passed: 250, Total: 250 |
| `dotnet build SK_P.sln -c Release --nologo` | 0 Warning / 0 Error |

## Commits

- `8ff3d69` feat(19-01): slim ICorrelated to single CorrelationId; Start/Stop implement it
- `eb22777` test(19-01): rewrite ConsoleCorrelationFilterTests for body-sourced correlation (RED)
- `d77cb10` feat(19-01): re-point inbound filter to read body CorrelationId (D-01) (GREEN)

## Self-Check: PASSED

All 5 modified files + SUMMARY.md present on disk; all 3 task commits (`8ff3d69`, `eb22777`, `d77cb10`) present in git history.
