# Phase 56: Typed Base-Config Seam — Specification

**Created:** 2026-06-12
**Ambiguity score:** 0.15 (gate: ≤ 0.20)
**Requirements:** 5 locked

## Goal

The `BaseProcessor` author seam changes from receiving the dispatch config as a raw `string payload` to receiving a **typed config instance** that inherits a framework-provided base config type; the framework deserializes the dispatch payload into that type before invoking the author's transform. `Processor.Sample` is migrated as the worked example with the old raw-string seam removed.

## Background

Today `BaseProcessor` (`src/BaseProcessor.Core/Processing/BaseProcessor.cs:29`) exposes exactly one seam — `protected abstract Task<List<ProcessItem>> ProcessAsync(string validatedData, string payload, ct)` — plus an `internal ExecuteAsync` forwarder (`:38`) that `ProcessorPipeline` calls at `ProcessorPipeline.cs:226`. That call is wrapped in a try/catch where a thrown `ProcessStatusException` maps to a typed `Step*` result and **any other exception maps to `StepFailed`** (`ProcessorPipeline.cs:241-246`). The author hand-deserializes the raw `payload`: `Processor.Sample` does `JsonSerializer.Deserialize<string>(payload)` (`SampleProcessor.cs:34`), so its "config type" is effectively `string`, and a blank payload becomes `null`. `Processor.Sample` is the **only** concrete processor in the solution.

There is no framework config type and no framework-owned payload deserialization. This phase introduces both. It is the prerequisite seam for Phase 57's Gate A (a config-type↔config-schema compatibility check needs a config *type* to check). `validatedData` (the input L2 blob, validated against `InputSchemaId`) is a separate parameter and is **not** in scope here — input typing is deferred (REQUIREMENTS.md "Future Requirements").

## Requirements

1. **Framework base config type**: An empty marker base config type exists that author config classes inherit.
   - Current: No framework config type exists; the seam's config parameter is a raw `string`.
   - Target: `BaseProcessor.Core` declares a base config type with **no framework-mandated fields** — purely the type anchor the seam delivers and that Phase 57's Gate A will compare against the config-schema definition. Authors add all their own fields on the derived type.
   - Acceptance: A type exists in `BaseProcessor.Core` that an author config class inherits; it declares zero required domain fields; the solution compiles with an author config deriving from it.

2. **Typed config author seam**: The author's transform receives a typed config instance, not a raw payload string.
   - Current: `ProcessAsync(string validatedData, string payload, ct)` — author hand-deserializes `payload`.
   - Target: The author overrides a transform that receives the input `validatedData` (still a string) and a **typed config instance** (the author's type, deriving from the base config). No raw config `string` appears in the author-facing seam.
   - Acceptance: A processor author can override the transform with a strongly-typed config parameter and access its fields without calling `JsonSerializer` themselves; the old `ProcessAsync(string validatedData, string payload)` signature no longer exists on `BaseProcessor`.

3. **Framework-owned payload deserialization**: The framework deserializes the dispatch payload into the author's config type before invoking the transform.
   - Current: The framework passes the raw `payload` string through untouched; deserialization is the author's responsibility.
   - Target: Before calling the author transform, the framework deserializes the dispatch `payload` into the author's config type and passes the resulting instance in.
   - Acceptance: For a payload that is valid JSON for the config type, the author transform receives a populated config instance with the expected field values (proven by a hermetic test on a known payload).

4. **Deterministic deserialization-failure & empty-payload semantics**: A non-deserializable payload fails deterministically as a business `StepFailed`; an empty/whitespace/absent payload yields a null config.
   - Current: The author owns these cases ad hoc (`SampleProcessor` maps blank→null; a malformed payload would throw inside the author's `Deserialize<string>` call and be caught by the pipeline catch-all).
   - Target: (a) A payload that does **not** deserialize into the config type surfaces as a business `StepFailed` — exactly one result emitted, logged, batch aborted, no crash and no silent default (reusing the existing catch-all at `ProcessorPipeline.cs:241`). (b) An empty/whitespace/absent payload yields a **null** config instance to the author transform (the author decides how to handle a config-less dispatch).
   - Acceptance: A hermetic test feeding a non-deserializable payload produces a single `StepFailed` (not a thrown/un-acked message, not a default instance); a separate test feeding an empty/whitespace payload invokes the author transform with a null config.

5. **`Processor.Sample` migrated (clean break)**: The sole concrete processor uses the new typed-config seam and the old seam is removed.
   - Current: `SampleProcessor` overrides `ProcessAsync(string, string)` and calls `JsonSerializer.Deserialize<string>(payload)`.
   - Target: `SampleProcessor` declares a typed config (deriving from the base config) and overrides the new typed seam; the old raw-string `ProcessAsync(string validatedData, string payload)` deserialize is removed (clean break — no compatibility shim). `Processor.Sample` still completes an execution round-trip.
   - Acceptance: `SampleProcessor` contains no `JsonSerializer.Deserialize` of the config payload; the old seam is absent from the codebase; the `Processor.Sample` round-trip test passes against the migrated seam.

## Boundaries

**In scope:**
- A framework base config type in `BaseProcessor.Core` (empty marker base).
- The new typed-config author seam on `BaseProcessor` (config param typed; input `validatedData` still string).
- Framework-owned deserialization of the dispatch payload into the author config type.
- Deterministic deser-failure (→ `StepFailed`) and empty-payload (→ null config) semantics.
- Migration of `Processor.Sample` to the new seam and removal of the old seam.
- Updating the hermetic test suite to the new seam.

**Out of scope:**
- **Gate A** (startup config-type↔config-schema compatibility check) — that is Phase 57; this phase only builds the seam Gate A will use.
- **Fetching the `ConfigSchemaId` definition at startup** — Phase 57 (the D-05 carve-out at `ProcessorStartupOrchestrator.cs:124` is untouched here).
- **Typing the input `validatedData`** — input remains a raw string; input/output schema-to-type typing is a deferred future requirement.
- **Mandating any common config fields** on the base type — the base is an empty marker (Requirement 1).
- **Any change to the orchestration-start WebAPI Gate B** (`PayloadConfigSchemaValidator`) — retained unchanged.
- **In-transit payload integrity** — out of milestone scope.

## Constraints

- The framework deserialization must reuse the project's existing JSON stack (`System.Text.Json`, already used in `SampleProcessor` and across the pipeline) — no new serialization dependency.
- The deser-failure path must route through the **existing** business-vs-infra split (`StepFailed` via `ProcessorPipeline.cs:241-246`); it must NOT introduce a new infra/Keeper route for malformed config.
- Clean break: no backward-compatible overload of the old `ProcessAsync(string, string)` seam may remain (only `Processor.Sample` consumes it, so no external break surface).
- Solution builds **0 warnings** in both Release and Debug; the hermetic suite is green with the new seam (no reliance on the live stack for this phase).

## Acceptance Criteria

- [ ] A framework base config type exists in `BaseProcessor.Core` with no framework-mandated domain fields.
- [ ] The `BaseProcessor` author seam delivers a typed config instance; the old `ProcessAsync(string validatedData, string payload)` signature is gone.
- [ ] The framework deserializes the dispatch payload into the author config type before invoking the transform (verified on a known payload → populated instance).
- [ ] A non-deserializable payload produces exactly one `StepFailed` (logged, batch aborted) — not a crash, un-acked redelivery, or default instance.
- [ ] An empty/whitespace/absent payload invokes the author transform with a null config.
- [ ] `SampleProcessor` uses the typed seam, contains no manual `JsonSerializer.Deserialize` of the config payload, and completes a round-trip.
- [ ] `dotnet build SK_P.sln` is 0-warning in Release and Debug; the hermetic suite is green.

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                        |
|--------------------|-------|------|--------|--------------------------------------------------------------|
| Goal Clarity       | 0.88  | 0.75 | ✓      | Typed seam + empty-marker base + framework deserialize locked |
| Boundary Clarity   | 0.85  | 0.70 | ✓      | Config-only; input stays string; Gate A is Phase 57          |
| Constraint Clarity | 0.82  | 0.65 | ✓      | Deser-failure → StepFailed; empty → null; clean break        |
| Acceptance Criteria| 0.82  | 0.70 | ✓      | 7 pass/fail criteria                                         |
| **Ambiguity**      | 0.15  | ≤0.20| ✓      |                                                              |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective | Question summary | Decision locked |
|-------|-------------|------------------|-----------------|
| 0 | (Roadmap baseline) | Goal/SCs from ROADMAP + CFG-01/02 | Typed seam, framework deserializes, sample migrated, clean break |
| 1 | Researcher / Failure Analyst | Runtime deser-failure of payload (pre-Gate-A)? | Non-deserializable payload → business `StepFailed` (reuse `ProcessorPipeline.cs:241` catch-all); deterministic, no crash/default |
| 1 | Boundary Keeper | Empty/whitespace/absent payload → what does the author get? | Null config instance; author handles config-less dispatch (mirrors today's blank→null) |
| 1 | Simplifier | Shape of the framework base config type? | Empty marker base (no framework-mandated fields); author adds all fields; pure type anchor for the seam + Phase 57 Gate A |

---

*Phase: 56-typed-base-config-seam*
*Spec created: 2026-06-12*
*Next step: /gsd-discuss-phase 56 — implementation decisions (generic `BaseProcessor<TConfig>` vs non-generic base, deserializer options, how the pipeline threads the typed config)*
