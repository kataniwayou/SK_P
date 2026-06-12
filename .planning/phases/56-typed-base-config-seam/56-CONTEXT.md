# Phase 56: Typed Base-Config Seam - Context

**Gathered:** 2026-06-12
**Status:** Ready for planning

<domain>
## Phase Boundary

Replace the `BaseProcessor` raw-string config parameter with a typed config instance the framework deserializes from the dispatch payload, and migrate `Processor.Sample` as the worked example (old raw-string seam removed). This is the prerequisite seam for Phase 57's Gate A — it does NOT include Gate A, the startup `ConfigSchemaId` fetch, input typing, or any WebAPI change.

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**5 requirements are locked.** See `56-SPEC.md` for full requirements, boundaries, and acceptance criteria. Downstream agents MUST read `56-SPEC.md` before planning or implementing. Requirements are not duplicated here.

**In scope (from SPEC.md):**
- A framework base config type in `BaseProcessor.Core` (empty marker base).
- The new typed-config author seam on `BaseProcessor` (config param typed; input `validatedData` still string).
- Framework-owned deserialization of the dispatch payload into the author config type.
- Deterministic deser-failure (→ `StepFailed`) and empty-payload (→ null config) semantics.
- Migration of `Processor.Sample` to the new seam and removal of the old seam.
- Updating the hermetic test suite to the new seam.

**Out of scope (from SPEC.md):**
- Gate A (startup config-type↔config-schema compatibility) — Phase 57.
- Fetching the `ConfigSchemaId` definition at startup — Phase 57 (D-05 carve-out untouched).
- Typing the input `validatedData` — deferred future requirement.
- Mandating any common config fields on the base type — base is an empty marker.
- Any change to the WebAPI Gate B (`PayloadConfigSchemaValidator`) — unchanged.
- In-transit payload integrity — out of milestone scope.

</spec_lock>

<decisions>
## Implementation Decisions

### Author seam shape
- **D-01:** Expose the typed config via a **generic layer over the existing non-generic base**: `public abstract class BaseProcessor<TConfig> : BaseProcessor` (with `TConfig` constrained to a reference type / the base config marker so `null` is representable). The non-generic `BaseProcessor` stays the type the framework resolves and the pipeline calls — its string-based `ExecuteAsync(string validatedData, string payload, ct)` entry point is **preserved**, so `ProcessorPipeline` (`ProcessorPipeline.cs:226`) and the DI registration (`AddSingleton<BaseProcessor, SampleProcessor>`) are **unchanged**.
- **D-02:** The generic `BaseProcessor<TConfig>` overrides/implements the framework `ExecuteAsync`: it deserializes the dispatch `payload` into `TConfig`, then invokes the author's new typed seam `protected abstract Task<List<ProcessItem>> ProcessAsync(string validatedData, TConfig? config, ct)`. The old `ProcessAsync(string validatedData, string payload, ct)` author seam is **removed** (clean break).
- **D-03:** Because deserialization happens **inside** `BaseProcessor<TConfig>.ExecuteAsync`, which runs inside the pipeline's existing `try { processor.ExecuteAsync(...) } catch` at `ProcessorPipeline.cs:226`/`:241`, a non-deserializable payload throws a `JsonException` that the **existing catch-all maps to `StepFailed`** — no new error-routing code, satisfying SPEC Requirement 4(a). Do NOT route malformed config to the infra/Keeper path.
- **D-04:** Empty/whitespace/absent payload → the generic layer short-circuits to a **`null` config** (guard `string.IsNullOrWhiteSpace(payload)` before deserialize) and passes `null` to the author seam (SPEC Requirement 4(b)). The author decides how to handle a config-less dispatch.

### Deserializer options
- **D-05:** Deserialization uses **one shared `JsonSerializerOptions`** instance with `PropertyNameCaseInsensitive = true` and default unknown-member handling (**unknown JSON properties are ignored**, not disallowed). Rationale: minimizes runtime throws on a payload that is valid under `ConfigSchemaId` (extra or differently-cased properties never fault), directly serving the milestone goal that an admitted payload always deserializes.
- **D-06:** This shared options instance is the **canonical config-deserialization contract**. Expose it somewhere Phase 57's Gate A can reference/reuse (e.g. a public static on `BaseProcessor.Core`) so Gate A models exactly the same deserialization behavior it is gating against. (Where it lives — Claude's discretion; the requirement is that it is single-source and reusable, not duplicated in Phase 57.)
- **D-07:** Reuse the project's existing `System.Text.Json` stack — no new serialization dependency (SPEC constraint).

### Sample config worked example
- **D-08:** `Processor.Sample` declares a **minimal single-field config record** (e.g. `record SampleConfig(string? Value)`) deriving from the base config marker; `SampleProcessor` inherits `BaseProcessor<SampleConfig>` and overrides the typed seam. No manual `JsonSerializer.Deserialize` of the config remains in `SampleProcessor`.
- **D-09:** Preserve the sample's existing demonstrations in the new typed shape: **null config → the existing `"processor-sample-ok"` fallback token**; the **`fail` status-exception demo** maps to a field value (e.g. `Value == "fail"` → throw `FailedException`), keeping the `ProcessStatusException` author path demonstrated.
- **D-10:** Consequence to thread through tests: the dispatch payload shape shifts from a **bare JSON string** (`"StepA1"`) to an **object** (`{"value":"StepA1"}`). Any hermetic and (Phase 58) RealStack round-trip payloads that echo the sample config must be updated to the object shape. The `Processor.Sample` round-trip must still complete (SPEC Requirement 5).

### Claude's Discretion
- Base config marker type name and namespace within `BaseProcessor.Core`.
- Exact location/visibility of the shared `JsonSerializerOptions` (D-06), provided it is single-source and reusable by Phase 57.
- Whether the framework `ExecuteAsync` seam is `internal abstract` on `BaseProcessor` with the generic class providing it, vs. another equivalent structure — provided the pipeline call site and DI resolution stay unchanged (D-01).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase requirements (locked)
- `.planning/phases/56-typed-base-config-seam/56-SPEC.md` — Locked requirements, boundaries, acceptance criteria. MUST read before planning.

### Milestone context
- `.planning/REQUIREMENTS.md` — v6.0.0 CFG requirements + traceability (CFG-01/02 map to this phase).
- `.planning/ROADMAP.md` §"Phase 56: Typed Base-Config Seam" — goal + success criteria; §"Phase 57" for the downstream Gate A that consumes this seam.

### Code touchpoints (current state, not docs — read directly)
- `src/BaseProcessor.Core/Processing/BaseProcessor.cs` — the abstract seam being changed (`ProcessAsync` at :29, `internal ExecuteAsync` forwarder at :38).
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:226-246` — the call site + business/infra try/catch the deser-failure relies on (`:241` catch-all → `StepFailed`).
- `src/BaseProcessor.Core/Processing/ProcessItem.cs` — the author result record (unchanged).
- `src/Processor.Sample/SampleProcessor.cs` + `src/Processor.Sample/Program.cs` — the worked example to migrate; DI registration `AddSingleton<BaseProcessor, SampleProcessor>`.

No external ADR/design-doc governs this phase — the design source of truth is `56-SPEC.md` + the v6.0.0 Gate A/Gate B analysis recorded in `.planning/PROJECT.md` (Current Milestone section).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **Non-generic `BaseProcessor` + pipeline contract**: the `internal ExecuteAsync(string,string,ct)` seam is the exact non-generic entry point the generic layer plugs under — no pipeline/consumer/DI change needed (D-01).
- **Pipeline try/catch (`ProcessorPipeline.cs:226-246`)**: already maps `ProcessStatusException` → typed `Step*` and any other exception → `StepFailed`. The framework deserialize sits inside this, so deser-failure mapping is free (D-03).
- **`System.Text.Json`**: already the serialization stack across the pipeline and `SampleProcessor` — reuse it (D-07).

### Established Patterns
- **Author overrides exactly one seam; framework owns the rest** (D-12/BPC-02 from v4.0.0). The generic layer keeps that invariant — author still overrides only the (now typed) `ProcessAsync`.
- **Clean-break seam changes** — v4.0.0 already removed the old `ProcessAsync(string,string)` once; this repeats that pattern (remove, don't shim).

### Integration Points
- `BaseProcessor.cs` (seam), `ProcessorPipeline.cs:226` (call site — unchanged), `SampleProcessor.cs` + `Program.cs` (worked example), and the hermetic test suite (payload shape + seam signature).
- Forward link: Phase 57 Gate A reads the shared `JsonSerializerOptions` (D-06) and compares `TConfig` against the `ConfigSchemaId` definition.

</code_context>

<specifics>
## Specific Ideas

- The shared `JsonSerializerOptions` (case-insensitive, ignore-unknown) is deliberately the *most forgiving* practical contract so that "an orchestration-admitted payload always deserializes" holds with the widest margin — strictness is the schema's job (Gate B) and the type-coverage check's job (Gate A, Phase 57), not the deserializer's.

</specifics>

<deferred>
## Deferred Ideas

- **Strict unknown-member handling** (`JsonUnmappedMemberHandling.Disallow`) — considered and rejected for this phase (D-05) because it would throw on schema-valid payloads carrying extra properties. Could be revisited if a future requirement wants to forbid unknown config keys, but that belongs with Gate A's `additionalProperties` semantics in Phase 57, not here.
- **Richer multi-field sample config** — rejected (D-08) in favor of minimal churn; a fuller author-template example could be a docs/sample concern later.
- **Typing the input `validatedData`** — explicitly out of scope (SPEC); a future milestone could generalize the typed seam to input/output.

None of these block or belong in Phase 56.

</deferred>

---

*Phase: 56-typed-base-config-seam*
*Context gathered: 2026-06-12*
