# Phase 56: Typed Base-Config Seam - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-12
**Phase:** 56-typed-base-config-seam
**Areas discussed:** Author seam shape, Deserializer options, Sample config example

> SPEC.md was loaded (5 requirements locked) — discussion was HOW-only.

---

## Author seam shape

| Option | Description | Selected |
|--------|-------------|----------|
| Generic over non-generic | `BaseProcessor<TConfig> : BaseProcessor`; pipeline + DI unchanged; generic subclass deserializes payload→TConfig inside `ExecuteAsync` (deser failure → existing `:241` catch → `StepFailed`) then calls typed `ProcessAsync`. | ✓ |
| Non-generic base, author casts | Author receives base `ProcessorConfig` and casts; keeps everything non-generic but defeats the typed-seam goal. | |

**User's choice:** Generic over non-generic.
**Notes:** Preserves the pipeline call site (`ProcessorPipeline.cs:226`) and DI registration unchanged; deser-failure mapping to `StepFailed` comes for free from the existing try/catch.

---

## Deserializer options

| Option | Description | Selected |
|--------|-------------|----------|
| Case-insensitive + ignore-unknown | One shared `JsonSerializerOptions`, `PropertyNameCaseInsensitive=true`, unknown props ignored; minimizes runtime throws on schema-valid payloads; Phase 57 Gate A models exactly this. | ✓ |
| Default STJ (case-sensitive) | Plain defaults; case-drifted payload throws at runtime. | |
| Strict: disallow unknown members | `JsonUnmappedMemberHandling.Disallow`; rejects extra props; works against the 'never throw on admitted payload' goal. | |

**User's choice:** Case-insensitive + ignore-unknown.
**Notes:** This instance is the canonical config-deserialization contract, to be exposed for Phase 57 Gate A reuse (single-source, not duplicated).

---

## Sample config example

| Option | Description | Selected |
|--------|-------------|----------|
| Minimal single-field record | `record SampleConfig(string? Value)`; payload `{"value":"StepA1"}`; null config → existing fallback token; `fail` demo via field value; least churn (payload shifts bare-string → object). | ✓ |
| Richer multi-field example | 2-3 field config to showcase typed config more fully; more illustrative but more test/payload churn. | |

**User's choice:** Minimal single-field record.
**Notes:** Preserve the null-config fallback (`processor-sample-ok`) and the `fail` status-exception demonstration; existing/RealStack round-trip payloads shift from bare string to object shape.

## Claude's Discretion

- Base config marker type name/namespace; exact location/visibility of the shared `JsonSerializerOptions` (single-source, Phase-57-reusable); the precise structure by which the generic class provides the framework `ExecuteAsync` (provided pipeline call site + DI resolution stay unchanged).

## Deferred Ideas

- Strict unknown-member handling (revisit with Gate A `additionalProperties` in Phase 57).
- Richer multi-field sample config (docs/sample concern).
- Typing the input `validatedData` (future milestone).
