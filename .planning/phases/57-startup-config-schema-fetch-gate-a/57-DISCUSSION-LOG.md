# Phase 57: Startup Config-Schema Fetch + Gate A - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in 57-CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-12
**Phase:** 57-startup-config-schema-fetch-gate-a
**Areas discussed:** Gate A "covers" semantics, TOCTOU policy, Terminal-unhealthy mechanics, Gate A placement

---

## Gate A "covers" semantics (CFG-05)

| Question | Options | Selected |
|----------|---------|----------|
| Covers algorithm | Structural walk (schema→reflect TConfig) / Derive schema from TConfig & compare / Roundtrip sampling | **Structural walk** ✓ |
| Strictness | Deserialization-faithful (flag only real type clashes) / Strict coverage (TConfig must declare every schema property) | **Deserialization-faithful** ✓ |
| Phase-56 `JsonUnmappedMemberHandling.Disallow` deferral | Keep ignore-unknown, Gate A startup-only / Flip Disallow on (unknown keys fail) | **Keep ignore-unknown** ✓ |
| Comparison depth | Deep/recursive (nested objects, arrays, enums) / Shallow (top-level primitives) | **Deep/recursive** ✓ |

**Notes:** Crux of the phase — no existing subset/covers logic in the codebase. Framed around the fact that the forgiving SerializerOptions make STJ deserialization rarely throw, so the real failure surface is type clashes on overlapping properties.

---

## TOCTOU policy (CFG-10)

| Question | Options | Selected |
|----------|---------|----------|
| Mechanism | Immutable config-schema definitions / Re-validate-on-change | **Immutable** ✓ |
| Freeze model | Frozen once referenced / Immutable from creation (all schemas) / Config-referenced only | **Frozen once referenced** ✓ |
| Freeze scope | Only Definition (Name/Description editable) / Whole entity | **Only Definition** ✓ |
| Reject code | 409 Conflict at service/validator / 422 / DB-level trigger | **409 Conflict (service/validator)** ✓ |

**Notes:** Re-validate-on-change rejected because it requires net-new schema-change-event + processor-subscription infra that doesn't exist. Immutability closes the window by construction with no runtime machinery.

---

## Terminal-unhealthy mechanics (CFG-06)

| Question | Options | Selected |
|----------|---------|----------|
| Fail posture | Stay up (gate ready, withhold Healthy, no dispatch bind) / Withhold both (readiness red) / Crash | **Stay up** ✓ |
| Diagnostic surface | Structured error log only / Log + metric / Log + health-endpoint detail | **Structured error log only** ✓ |
| Terminal enforcement | Single post-fetch check, no retry / Bounded retry before terminal | **Single post-fetch check, no retry** ✓ |

**Notes:** One-way `MarkHealthy` latch (no `MarkUnhealthy`) means "unhealthy" = never latching. Stay-up posture avoids a deterministic crash-loop while still blocking orchestrations via absent L2 liveness. `gate.MarkReady` decoupled from `MarkHealthy`.

---

## Gate A placement (CFG-03/06)

| Question | Options | Selected |
|----------|---------|----------|
| Fetch wiring | Fetch ConfigSchemaId inside Loop B / Separate dedicated stage | **Inside Loop B** ✓ |
| Check position | After all fetches, before endpoint bind / After bind+Ready, before MarkHealthy | **Before endpoint bind** ✓ |
| L2 surface | Store on context only / Also in L2 ProcessorProjection | **Context only** ✓ |

---

## Post-decision clarification (user-confirmed)

User confirmed the runtime contract: config↔schema compatibility is validated **only at startup** (Gate A); the execution round-trip does **no** payload-vs-schema validation (deserialize-and-go); payload-vs-schema is Gate B at orchestration-start (different time, retained unchanged); a rare runtime deserialize exception (in-transit mutation only) is caught by the `ProcessorPipeline` catch-all as exactly one `StepFailed` (Phase-56 behavior, not local to `BaseProcessor<TConfig>`). Captured as the "Runtime contract" note in CONTEXT.md. No decisions changed.

## Claude's Discretion

- Schema-property → CLR-property name mapping rules and the exact STJ type-clash table (number coercion, nullable value types, enum name vs ordinal).
- Exact location of the immutability check within the Schema update path.

## Deferred Ideas

- Generalizing Gate A to input/output schema↔type compatibility (documented Future Requirement).
- Per-step config diagnostics for operators (documented Future Requirement).
- Gate-A failure metric / health-endpoint diagnostic (considered for CFG-06, deferred in favor of log-only).
