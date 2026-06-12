# Phase 57: Startup Config-Schema Fetch + Gate A - Context

**Gathered:** 2026-06-12
**Status:** Ready for planning

<domain>
## Phase Boundary

At processor startup, fetch the `ConfigSchemaId` definition over the bus (reusing Loop B's `GetSchemaDefinition` dual-response + retry) and run **Gate A** — validate that the concrete `TConfig` type *covers* that schema (`ConfigSchemaId ⊨ TConfig`). On incompatibility the processor never reaches *Healthy* (withhold `MarkHealthy` → heartbeat no-ops → no `skp:{id}` L2 key → orchestration-start `ProcessorLivenessValidator` blocks any workflow using it, 422). A null `ConfigSchemaId` skips Gate A. The config-schema↔config-type compatibility check is **startup-time only**; the execution round-trip stays deserialize-and-go.

**Requirements:** CFG-03, CFG-04, CFG-05, CFG-06, CFG-07, CFG-10. (CFG-08/09 — the real-stack orchestration-gate integration proof — are Phase 58.)

</domain>

<decisions>
## Implementation Decisions

### Gate A "covers" semantics (CFG-05)
- **D-01 — Structural walk:** Perform the covers check by parsing the fetched config-schema definition with the existing `Json.Schema` library (under `JsonSchemaConfig`'s SSRF-locked, Draft 2020-12 options), enumerating its declared properties + types, reflecting `TConfig`'s properties, and verifying each property present in **both** is `System.Text.Json`-bind-compatible. No new dependency, no schema-generation library, no roundtrip sampling.
- **D-02 — Deserialization-faithful strictness:** Flag incompatibility **only** for a real type clash on a property present in both the schema and `TConfig` (a schema-valid payload that would actually fail to deserialize). A schema property absent from `TConfig` is **fine** (ignored at runtime). Schema `required`/extra-in-schema does **not** block. This matches the literal invariant "every schema-valid payload deserializes into `TConfig`" — Gate A never blocks a config that *does* deserialize. Silent field-drop is explicitly **not** an incompatibility.
- **D-03 — Keep ignore-unknown; Gate A is startup-only:** The Phase-56 `ProcessorConfig.SerializerOptions` stay forgiving (case-insensitive, unknown members ignored). **Do NOT** flip `JsonUnmappedMemberHandling.Disallow` (the Phase-56 T-56-03 deferral is resolved as *keep ignore-unknown*). No new runtime deserialize-failure mode is introduced; the milestone invariant "admitted payloads always deserialize" is preserved.
- **D-04 — Deep / recursive comparison:** Model STJ binding at every level — nested objects, arrays, and enum constraints (e.g. schema `integer` vs CLR `string`, schema enum string vs CLR enum name, array vs scalar). Shallow top-level-only comparison is rejected (a nested clash would slip past Gate A and throw at runtime).

### TOCTOU policy (CFG-10)
- **D-05 — Immutable config-schema definitions:** Close the window between startup Gate A and orchestration-start Gate B by construction — the schema id a processor validated at startup always denotes the same definition Gate B later reads. No runtime re-validation, no processor schema-change subscription (that net-new infra is explicitly rejected).
- **D-06 — Frozen-once-referenced:** A schema's `Definition` is editable while it is an unreferenced draft, then **locks** the moment any processor (`ConfigSchemaId`/`InputSchemaId`/`OutputSchemaId`) or assignment references it. Uniform across schema roles; preserves authoring ergonomics. Editing an in-use schema = create a new id + re-point. (Detecting "referenced" = any `ProcessorEntity` FK — and assignment reference — pointing at the schema id.)
- **D-07 — Only `Definition` frozen:** `Name` and `Description` stay editable on a frozen schema; only the schema body (what Gate A/Gate B read) is locked.
- **D-08 — 409 Conflict at the service/validator layer:** An attempt to mutate a frozen `Definition` is rejected in the Schema update path with RFC 7807 ProblemDetails + **409 Conflict** (state conflict), consistent with the existing exception-handler chain and `X-Correlation-Id` echo. Not a DB-level trigger (message-friendliness preferred); not 422.

### Terminal-unhealthy mechanics (CFG-06)
- **D-09 — Stay-up fail posture:** On a terminal Gate A incompatibility the boot sequence **completes** — `gate.MarkReady()` flips (K8s sees startup done, **no crash-loop**) — but `MarkHealthy()` is **withheld** (no `skp:{id}` L2 liveness key → `ProcessorLivenessValidator` reports "absent" → orchestrations blocked 422) **and** the dispatch receive endpoint is **NOT bound** (the processor never consumes payloads it can't safely deserialize). Coherent state: "booted, concluded it must not serve." `gate.MarkReady` is therefore **decoupled** from `MarkHealthy` (today they fire together at `ProcessorStartupOrchestrator.cs:181-182`). Do NOT crash/throw; do NOT leave readiness red.
- **D-10 — Structured error log only:** Surface the incompatibility via a single Error-level log carrying processor id, `ConfigSchemaId`, and the specific clash (property + schema-type vs CLR-type). Satisfies CFG-06's "reason is logged." **No** new metric and **no** new health-endpoint diagnostic this phase (smallest scope).
- **D-11 — Single post-fetch check, terminal, no retry:** The fetch itself still retries transiently (`SchemaDefinitionNotFound`/timeout, exactly like input/output — CFG-04). Once the definition is in hand, Gate A runs **once**; incompatibility breaks out of the loop terminally with no retry. Re-checking is pointless — the definition is immutable (D-05), so the result is deterministic.

### Gate A placement (CFG-03)
- **D-12 — Fetch `ConfigSchemaId` inside Loop B:** Add the non-null `ConfigSchemaId` to the set of definitions Loop B resolves (`ProcessorStartupOrchestrator.cs:124`), reusing its `GetSchemaDefinition` dual-response + retry verbatim. Extend `ProcessorContext.SetDefinition` to also match `ConfigSchemaId` (→ `ConfigDefinition`). This lifts the D-05 "never read the config schema id" carve-out. No separate dedicated config-fetch loop.
- **D-13 — Check after all fetches, before the endpoint bind:** Gate A runs after Loop B completes and **before** the dispatch-endpoint bind, so a config-incompatible processor never binds its queue (nothing to tear down). Happy path: check passes → bind → `await handle.Ready` → `MarkHealthy()`. `gate.MarkReady()` fires on **both** paths (per D-09).
- **D-14 — Store on context only, not in the L2 projection:** Storing `ConfigDefinition` on the context (via the extended `SetDefinition`) satisfies CFG-03. Do **not** add it to the heartbeat's L2 `ProcessorProjection` — Gate B reads the config schema fresh from the DB/L1 snapshot, never from the processor's L2 liveness key, so there is no consumer for it there.

### Runtime contract (confirmed, not a new requirement)
- Config↔schema compatibility is checked **only at startup** (Gate A). The execution round-trip (dispatch → processor → result) does **no** payload-vs-schema validation — the processor deserializes-and-goes.
- Payload-vs-schema **is** validated, but by **Gate B (`PayloadConfigSchemaValidator`)** at **orchestration-start** in the WebAPI — a different time than the round-trip, and out of scope for this phase (retained unchanged).
- The only residual runtime deserialize-failure is **in-transit payload mutation** (the one window this milestone does not close). When it occurs, the `JsonException` is **not** caught inside `BaseProcessor<TConfig>` (Phase-56 D-03) — it propagates to the `ProcessorPipeline` catch-all (`ProcessorPipeline.cs:241`) → **exactly one `StepFailed`**, logged, batch aborts. Already-shipped Phase-56 behavior; this phase adds nothing to the hot path.

### Claude's Discretion
- The precise schema-property → CLR-property name mapping (honoring `[JsonPropertyName]` + case-insensitivity) and the exact STJ type-clash rule table (number coercion, nullable value types, enum name vs ordinal) are implementation details for research/planning, consistent with D-02/D-04.
- The exact location of the immutability check within the Schema update path (validator vs service method) and how "referenced" is queried, consistent with D-06/D-08.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Requirements & roadmap
- `.planning/REQUIREMENTS.md` — CFG-03..CFG-07, CFG-10 (full requirement text), the milestone invariant `payload ⊨ ConfigSchemaId ∧ ConfigSchemaId ⊨ configType ⟹ payload deserializes`, the three "Open Decisions" (now resolved here), and Out-of-Scope items.
- `.planning/ROADMAP.md` §"Phase 57: Startup Config-Schema Fetch + Gate A" — goal + success criteria.
- `.planning/PROJECT.md` §"Current Milestone: v6.0.0" — Gate A/Gate B framing; source of truth is the locked Gate A/Gate B planning analysis (2026-06-12 conversation; no standalone doc file).

### Prior phase
- `.planning/phases/56-typed-base-config-seam/56-SECURITY.md` — T-56-01/02/03 dispositions; T-56-03 (ignore-unknown accepted risk) is resolved by D-03 here.

[No external ADR/spec files beyond the planning artifacts — the milestone's source of truth is the locked planning conversation, captured in REQUIREMENTS.md/PROJECT.md.]

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`ProcessorStartupOrchestrator`** (`src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs`): Loop A identity (68-119), Loop B definitions (124-162), completion bind+MarkHealthy+MarkReady (164-184). D-05 "never read ConfigSchemaId" carve-out comment ~line 30; null-is-skip at 127-128. Loop B uses `IRequestClient<GetSchemaDefinition>` dual-response with `SchemaDefinitionNotFound` + `RequestTimeoutException` retry. **Insertion: D-12/D-13.**
- **`ProcessorContext`** (`src/BaseProcessor.Core/Identity/ProcessorContext.cs`): `Guid? ConfigSchemaId` (42), `SetDefinition(schemaId, definition)` keyed by Input/Output schema id (74-80) → extend to `ConfigSchemaId` → `ConfigDefinition` (D-14); one-way `MarkHealthy` latch via `Interlocked.Exchange` (83-89).
- **`ProcessorLivenessHeartbeat`** (`src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs`): no-ops while `!IsHealthy` (68-70); writes `ProcessorProjection` to `L2ProjectionKeys.Processor(id)` with sliding TTL (80-100). **Leave projection unchanged (D-14).**
- **`PayloadConfigSchemaValidator`** (`src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs`): Gate B — payload ⊨ ConfigSchemaId at orchestration-start; null-is-skip (42); `JsonSchema.FromText` (55) + `Evaluate` (84). **Retained unchanged.** Mirrors the null-is-skip convention Gate A follows (CFG-07).
- **`JsonSchemaConfig`** (`src/BaseApi.Service/Features/Schema/JsonSchemaConfig.cs`): Draft 2020-12 default dialect + global no-op `SchemaRegistry.Global.Fetch` (SSRF lockdown) + `DefaultOptions`. **Gate A's structural walk builds on this (D-01).**
- **`ProcessorConfig` / `BaseProcessor<TConfig>`** (`src/BaseProcessor.Core/Configuration/ProcessorConfig.cs`, `.../Processing/BaseProcessor`1.cs`): forgiving `SerializerOptions` (case-insensitive, ignore-unknown); `TConfig` reachable at startup via the DI-resolved processor's generic type arg (`GetType().BaseType?.GenericTypeArguments[0]`). **Source of the TConfig type for D-01.**
- **`GetSchemaDefinition` contract** (`src/Messaging.Contracts/ProcessorQueries.cs`) + `GetSchemaDefinitionConsumer` (`src/BaseApi.Service/Features/Schema/Responders/`): the exact fetch reused for config-schema (D-12).

### Established Patterns
- **One-way Healthy latch** — there is no `MarkUnhealthy`; "unhealthy" = never latching. Drives D-09 (withhold, don't reverse).
- **Schema definition shape** — JSON Schema text (`string`, Draft 2020-12) stored on `SchemaEntity.Definition`; processors reference by `Guid?` FK. `SchemaDtoValidator` already validates Definition is valid JSON Schema on write — natural home for the D-06/D-08 immutability check.
- **Orchestration validator order (LOCKED, Phase 14)** — `OrchestrationService.cs:182-195`: Cycle → SchemaEdge → PayloadConfigSchema (Gate B) → ProcessorLiveness. Gate A is **processor-side at startup**, NOT in this chain.
- **RFC 7807 + SQLSTATE→HTTP mapping** — existing exception-handler chain (23505→409, 23503→422) is the model for D-08's 409.

### Integration Points
- Config-incompatible processor → no `skp:{id}` L2 key → `ProcessorLivenessValidator` "absent" → 422 at orchestration-start. This is the Phase-58 end-to-end proof seam (CFG-08/09), already wired by the existing liveness gate.

</code_context>

<specifics>
## Specific Ideas

- The covers check is the *reverse direction* of Gate B: Gate B is `payload ⊨ schema` (JSON instance vs schema); Gate A is `schema ⊨ TConfig` (schema vs CLR type). They share the `Json.Schema` parse + `JsonSchemaConfig` options but are distinct checks.
- "Frozen-once-referenced" deliberately preserves a draft-then-freeze authoring flow rather than immutable-from-creation — schemas can be iterated until something points at them.

</specifics>

<deferred>
## Deferred Ideas

- **Generalizing Gate A to input/output schema↔type compatibility** — already a documented Future Requirement (REQUIREMENTS.md §Future). Out of scope; config-only this milestone.
- **Per-step config diagnostics for operators** (which step/assignment would fail which processor, beyond the binary liveness gate) — documented Future Requirement.
- A Gate-A failure **metric** and a **health-endpoint diagnostic** were considered for CFG-06's diagnostic surface but deferred in favor of structured-log-only (D-10). Candidates if operator observability needs grow.

[Discussion stayed within phase scope — no scope creep redirected.]

</deferred>

---

*Phase: 57-startup-config-schema-fetch-gate-a*
*Context gathered: 2026-06-12*
