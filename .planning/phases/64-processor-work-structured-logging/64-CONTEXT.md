# Phase 64: Processor Work & Structured Logging - Context

**Gathered:** 2026-06-14
**Status:** Ready for planning

<domain>
## Phase Boundary

Make the shared `processor-sample` do **observable, correlatable work** — the second (and only other) product code change in v8.0.0. `SampleConfig` carries an **integer** + a **string**; `ProcessAsync` random-adds to the integer and emits the **sum** as the step's completed result; and it emits **exactly one structured log entry** tagged with the payload string `Step_<label>` + the computed sum, carrying `correlationId` + `stepId` (+ `workflowId`/`processorId`) so Elasticsearch can aggregate a whole run by `correlationId` and identify each step. This is the data shape the Phase 66 analyzer parses.

**In scope:** `SampleConfig` field reshape, `SampleProcessor.ProcessAsync` work + logging, the `SampleProcessorFacts` hermetic rewrite, 0-warning Release+Debug build.

**Out of scope (other phases):** the 9-step fan-out workflow seeder + the per-step `{number, label}` assignment rows + input/output schema definitions + clean-state stack (Phase 65); the ES/Prometheus analyzer that consumes this log shape (Phase 66); `processor-badconfig` exclusion from the stack (Phase 65 ENV). Requirements PROC-01/02/03 are **locked** — this phase decided only HOW.

</domain>

<decisions>
## Implementation Decisions

### Config shape (PROC-01)
- **D-01:** `SampleConfig` becomes `SampleConfig(int Number, string? Label)` — **replaces** the single `string? Value` field. (Field names: `Number` for the int, `Label` for the string.) Still a `sealed record` deriving from `ProcessorConfig`; the v6.0.0 typed seam deserializes the assignment payload `{ "number": N, "label": "Step_*" }` into it.
- **D-02:** **Drop the demo paths** — remove the `"fail"` → `FailedException` worked example and the null-config → `"processor-sample-ok"` fallback token. They are vestigial for this proof (every seeded step carries a real config; `processor-badconfig` is excluded from the v8.0.0 stack).
- **D-03:** Null-config edge handling is **Claude's discretion** (see below) — the proof always supplies a config, so this is a defensive hermetic concern only, not a product behavior.

### Result data shape (PROC-02)
- **D-04:** The completed `ProcessItem.Data` is a **JSON object** carrying the sum and the step label, e.g. `{ "number": <sum>, "label": "Step_A1" }` — where `number` is `config.Number + random` and `label` is `config.Label` verbatim. Self-describing and schema-validatable; this is what flows to `L2[entryId]` and is read as the next step's `validatedData`.
- **D-05:** Serialize the output with the framework's shared `ProcessorConfig.SerializerOptions` (same options the seam deserializes with) so input↔output round-trip is symmetric across the chain.
- **D-06:** The author still **mints its own `ExecutionId`** per item (carried-forward D-03 from v4/v6) — unchanged.

### Random addend (PROC-02)
- **D-07:** Random addend is **bounded `Random.Shared.Next(0, 100)`** (0–99 inclusive). Overflow-safe for any reasonable payload int, human-readable sums in logs, and non-deterministic across fires (satisfies "non-deterministic across fires, deterministic in structure"). Use the framework-shared thread-safe `Random.Shared` (same RNG the pipeline's `SlotTtl()` uses).

### Structured log entry (PROC-03)
- **D-08:** Emit **exactly one** `logger.LogInformation` per execution with **structured params `{StepLabel}` and `{Sum}` only**. The existing `"sample payload received: {Payload}"` log is **replaced** (not added to) so the one-entry-per-execution invariant holds.
- **D-09:** **Rely on the ambient log scope** for the ids — `correlationId` is already opened by `InboundCorrelationConsumeFilter` and `workflowId/stepId/processorId/executionId/entryId` by `InboundExecutionScopeConsumeFilter` around the consume, so they surface as ES `attributes.*` on this log line automatically via the OTel IncludeScopes bridge. Do **not** re-add them as explicit params.
- **D-10:** `{StepLabel}` carries `config.Label` **verbatim** — the seeded label value IS already the full `Step_*` token (e.g. `"Step_A1"`). Do **not** prepend another `Step_` prefix. `{Sum}` is the integer sum.

### Claude's Discretion
- Null-config defensive behavior (D-03): the proof never hits it, so a sensible default — e.g. treat `Number` as `0` and `Label` as null/absent, still emitting one log + one completed item — is acceptable. Whether to default-or-throw is the planner's call; just keep "the seam always runs and emits exactly one result + one log."
- Exact JSON property casing/ordering of the D-04 result object (governed by `ProcessorConfig.SerializerOptions`).
- Exact log message template wording around the `{StepLabel}`/`{Sum}` params (level is Information, one entry).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase requirements & goal (locked)
- `.planning/ROADMAP.md` → "Phase 64: Processor Work & Structured Logging" (Goal + 4 Success Criteria) and the v8.0.0 milestone header ("Scope discipline (locked)" — only TWO product code changes; this is the second).
- `.planning/REQUIREMENTS.md` → PROC-01, PROC-02, PROC-03.

### Typed config seam (v6.0.0 — what deserializes the payload)
- `src/BaseProcessor.Core/Processing/BaseProcessor`1.cs` — the generic `ExecuteAsync` that deserializes `payload` → `TConfig` via `ProcessorConfig.SerializerOptions` and calls the author `ProcessAsync` seam (D-04 null-config guard lives here).
- `src/BaseProcessor.Core/Configuration/ProcessorConfig.cs` — the marker base + shared `SerializerOptions`.

### Ambient log scope (the PROC-03 mechanism)
- `src/Messaging.Contracts/ExecutionLogScope.cs` — the 5 execution-id scope keys (WorkflowId/StepId/ProcessorId/ExecutionId/EntryId) and `BuildState`.
- `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` — opens that scope around every `IExecutionCorrelated` consume; `correlationId` is owned separately by `InboundCorrelationConsumeFilter`/`CorrelationKeys.LogScope`.

### Pipeline that calls the seam (output validation + result builders)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — IN stage (`processor.ExecuteAsync` at ~:226), per-item output-schema validation vs `context.OutputDefinition` (~:254, null-is-skip), `BuildCompleted` result builder, and the existing inner `ExecutionLogScope` BeginScope around `SendResult` (~:284).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `src/Processor.Sample/SampleConfig.cs` — the record to reshape (`string? Value` → `int Number, string? Label`).
- `src/Processor.Sample/SampleProcessor.cs` — the one concrete `BaseProcessor<SampleConfig>`; override only the typed `ProcessAsync`. Inject `ILogger<SampleProcessor>` (already present).
- Ambient consume-filter scope (correlation + execution-id) — already wired bus-wide; PROC-03 ids come free, no new plumbing.
- `Random.Shared` — framework's thread-safe RNG, already used in `ProcessorPipeline.SlotTtl()`.

### Established Patterns
- Typed base-config seam (v6.0.0): framework deserializes, author gets a typed `TConfig?`.
- Author mints `ExecutionId` per `ProcessItem` (D-03 carried forward).
- Null-is-skip schema validation: output validated against `context.OutputDefinition` only when non-null (relevant to whether the D-04 JSON object needs a matching output schema — that schema is a Phase 65 seeder concern).
- Structured params as scope/template values, never interpolated into the template (T-18-04 security pattern).

### Integration Points
- `ProcessorPipeline.RunForwardAsync` IN stage invokes `processor.ExecuteAsync(validatedData, d.Payload, ct)`; the D-04 JSON result becomes `item.Data` → written to `L2[entryId]` → read as the next chained step's `validatedData`.
- `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` — 3 hermetic facts assert the OLD shape (echo `Value`, `"processor-sample-ok"` fallback, `"fail"`→`FailedException`); all three must be rewritten for the new Number/Label + sum + single `Step_*` log (and the dropped demo paths).

</code_context>

<specifics>
## Specific Ideas

- The seeded `label` field value is the full `Step_*` token (e.g. `Step_A1`) — logged verbatim, no double-prefix (D-10).
- Output JSON mirrors the config field names (`number`, `label`) so a chained step reads a shape symmetric to what it was seeded with (D-04/D-05).

</specifics>

<deferred>
## Deferred Ideas

- **Input/output JSON Schema definitions** for the chained fan-out workflow (whether each step's schemas are null → null-is-skip, or defined to match `{ number, label }`) — Phase 65 seeder. The D-04 object shape is chosen to be schema-validatable either way.
- **The 9-step fan-out seeder + per-step `{number, label:"Step_*"}` assignment rows** (WF-01/02) — Phase 65.
- **`processor-badconfig` exclusion from the stack** (ENV-01) — Phase 65.
- **The ES/Prometheus analyzer** that aggregates by `correlationId` and parses this `Step_*` + sum log shape — Phase 66.

</deferred>

---

*Phase: 64-processor-work-structured-logging*
*Context gathered: 2026-06-14*
