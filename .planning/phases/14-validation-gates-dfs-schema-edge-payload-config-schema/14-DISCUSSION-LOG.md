# Phase 14: Validation gates (DFS + schema-edge + payload-config-schema) - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-29
**Phase:** 14-validation-gates-dfs-schema-edge-payload-config-schema
**Areas discussed:** 422 error contract, SSRF config refactor, graph-walk ownership, payload-gate error detail, error-body shape, type placement

---

## 422 Error Contract

| Option | Description | Selected |
|--------|-------------|----------|
| One exception + one handler | Single OrchestrationValidationException (gate-code + offending-ids) claimed by one new IExceptionHandler before Fallback → 422; one consistent envelope across all 3 gates; mirrors NotFoundException pattern | ✓ |
| Per-gate exception subclasses | Abstract base + Cycle/SchemaEdge/Payload subclasses, one handler on the base | |
| Reuse ValidationProblemDetails errors map | Render as Dictionary<string,string[]> keyed by gate/field, at 422 | |

**User's choice:** One exception + one handler.
**Notes:** Today 422 only exists via Postgres FK (23503); no domain-validation 422 path. correlationId/instance ride free via the Phase 4 AddProblemDetails customizer.

---

## SSRF Config (L1-VALIDATE-07)

| Option | Description | Selected |
|--------|-------------|----------|
| Extract + refactor Phase 8 to use it | Shared JsonSchemaConfig is single SSRF source; rewire SchemaCreate/UpdateDtoValidator to consume it; new payload validator uses same factory | ✓ |
| Add new factory, leave Phase 8 untouched | New factory for payload validator only; SchemaDtoValidator keeps its inline static ctor | |

**User's choice:** Extract + refactor Phase 8 to use it.
**Notes:** Must preserve the Phase 8 `<500ms` SSRF regression assertion + draft-2020-12 behavior (no regression). Refactor lands as bisect-friendly commit(s).

---

## Graph-Walk Ownership (VALIDATE-04 gate owner + shared abstraction)

| Option | Description | Selected |
|--------|-------------|----------|
| CycleDetector owns cycle + missing-step; no shared walk | One walk detects both cycles and missing NextStepIds; SchemaEdgeValidator does its own edge walk; honors P13 D-03 | ✓ |
| Shared graph-walk helper | One internal traversal helper used by cycle + schema-edge gates | |
| Missing-step as its own gate | Distinct MissingStepValidator between cycle and schema-edge (4th seam + orchestrator re-wire) | |

**User's choice:** CycleDetector owns cycle + missing-step; no shared walk.
**Notes:** Given StepNextSteps FK-Restrict, missing-step is largely defense-in-depth but VALIDATE-04 mandates it. Honors P13 D-03 (loader/cycle/edge walks have differing semantics — don't force sharing).

---

## Payload-Gate Error Detail (VALIDATE-06)

| Option | Description | Selected |
|--------|-------------|----------|
| assignmentId + flattened error list | Offending assignmentId + flat list of JsonSchema.Net error strings | ✓ |
| assignmentId only | Minimal body, consumer re-derives details | |
| assignmentId + full hierarchical output | Full List-format evaluation tree (instance/keyword locations) | |

**User's choice:** assignmentId + flattened error list.

---

## Error-Body Shape (follow-up on the single-exception envelope)

| Option | Description | Selected |
|--------|-------------|----------|
| gate discriminator + typed offending object | errors = { gate: "cycle"\|"missingStep"\|"schemaEdge"\|"payloadConfigSchema", offending: {gate-specific} }; Title reflects gate | ✓ |
| Uniform Dictionary<string,string[]> | Reuse 400 errors-map shape at 422; keys like "stepChain"/"parentStepId"/"assignmentId" | |

**User's choice:** gate discriminator + typed offending object.

---

## Type Placement (where the new types live)

| Option | Description | Selected |
|--------|-------------|----------|
| Exception+handler in Service; JsonSchemaConfig in Service | OrchestrationValidationException + handler in Features/Orchestration (registered into Core chain via feature extension); JsonSchemaConfig near Schema; Core stays generic | ✓ |
| Exception+handler in Core; JsonSchemaConfig in Core | Promote both to BaseApi.Core as reusable infrastructure | |
| Split: handler in Core, exception in Service, config near Schema | Generic Core handler keyed on a marker interface; concrete exception in Orchestration | |

**User's choice:** Exception+handler in Service; JsonSchemaConfig in Service.
**Notes:** Surfaced a wiring constraint (CONTEXT D-04): the Core FallbackExceptionHandler is a catch-all registered last, so a Service-side handler must be reached before it — the composition-root ordering mechanism is left to the planner.

## Claude's Discretion

- Exact class/file names, offending-payload record types, gate-discriminator enum-vs-string.
- The precise composition-root mechanism satisfying D-04 (keep Fallback last-walked, orchestration handler reachable).
- Per-Start `Dictionary<Guid, JsonSchema>` cache as inline local vs tiny helper.
- DFS stack frame shape for reconstructing the cycle `stepChain`.

## Deferred Ideas

- Redis L2 write + Stop-as-EXISTS (Phase 15); idempotency/concurrency/closeout (Phase 16).
- VALID-21 at HTTP-write time (future milestone); schema-edge structural compatibility (future).
- Shared graph-walk abstraction; promotion of new types to BaseApi.Core (deferred until a second consumer).
