# Phase 64: Processor Work & Structured Logging - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-14
**Phase:** 64-processor-work-structured-logging
**Areas discussed:** Config shape, Result data, Random range, Log design

---

## Config shape

| Option | Description | Selected |
|--------|-------------|----------|
| Number+Label, drop demos | `SampleConfig(int Number, string? Label)` replacing `Value`; drop the `"fail"`→FailedException and null→`"processor-sample-ok"` demo paths | ✓ |
| Number+Label, keep demos | Add Number+Label but preserve the fail business-failed path and a null fallback | |
| Let me specify | User describes exact field names/nullability/demo handling | |

**User's choice:** Number+Label, drop demos
**Notes:** Demos are vestigial for the proof — every seeded step carries a real config and `processor-badconfig` is excluded from the v8.0.0 stack. All 3 hermetic facts will be rewritten. Null-config behavior left to Claude's discretion (proof never hits it).

---

## Result data

| Option | Description | Selected |
|--------|-------------|----------|
| JSON object | `Data = {"number":<sum>,"label":"Step_A1"}`; self-describing, schema-validatable | ✓ |
| Bare integer string | `Data = sum.ToString()` e.g. `"42"`; minimal | |
| Let me specify | User describes exact payload shape | |

**User's choice:** JSON object
**Notes:** Serialized with the shared `ProcessorConfig.SerializerOptions` so input↔output round-trips symmetrically across the A→B→C→… chain. Output property names mirror the config (`number`, `label`).

---

## Random range

| Option | Description | Selected |
|--------|-------------|----------|
| Bounded 0–99 | `Random.Shared.Next(0,100)`; overflow-safe, readable, non-deterministic | ✓ |
| Bounded 1–1000 | Wider spread, still overflow-safe | |
| Let me specify | User gives exact bounds / RNG strategy | |

**User's choice:** Bounded 0–99
**Notes:** Uses the framework-shared `Random.Shared` (same RNG as `ProcessorPipeline.SlotTtl()`).

---

## Log design

| Option | Description | Selected |
|--------|-------------|----------|
| Rely on ambient scope | One LogInformation with `{StepLabel}`+`{Sum}` only; ids come from the existing consume-filter scope; replaces the current log | ✓ |
| Explicit ids too | Same log but also add correlationId/stepId as explicit params | |
| Let me specify | User describes exact template/params/level | |

**User's choice:** Rely on ambient scope
**Notes:** `correlationId` (InboundCorrelationConsumeFilter) + `workflowId/stepId/processorId` (InboundExecutionScopeConsumeFilter) are already ambient during `ProcessAsync`, surfacing as ES `attributes.*` via OTel IncludeScopes. Exactly one entry per execution — the existing `"sample payload received"` log is replaced, not supplemented. `{StepLabel}` carries the verbatim `Step_*` token (no double-prefix).

---

## Claude's Discretion

- Null-config defensive behavior (proof never triggers it) — default-or-throw is the planner's call, provided the seam still emits exactly one result + one log.
- Exact JSON casing/ordering of the result object (governed by `ProcessorConfig.SerializerOptions`).
- Exact log message template wording around the structured params.

## Deferred Ideas

- Input/output JSON Schema definitions for the chained workflow (null-is-skip vs defined) — Phase 65 seeder.
- 9-step fan-out seeder + `{number, label:"Step_*"}` assignment rows — Phase 65.
- `processor-badconfig` exclusion from the stack — Phase 65 (ENV).
- ES/Prometheus analyzer that parses this log shape — Phase 66.
