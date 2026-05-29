# Phase 14: Validation gates (DFS + schema-edge + payload-config-schema) - Context

**Gathered:** 2026-05-29
**Status:** Ready for planning

<domain>
## Phase Boundary

Fill the three no-op validator seams created in Phase 13 — `CycleDetector`, `SchemaEdgeValidator`, `PayloadConfigSchemaValidator` (all under `Features/Orchestration/Validation/`) — so a broken workflow Start request returns a deterministic **HTTP 422 + RFC 7807** at the **first** failed gate, with the offending entity ids in the error body, and L1 cleanup guaranteed even on the failure path. Closes v2-deferred **VALID-21** at orchestration-start scope only.

**In scope (this phase):** L1-VALIDATE-01..10
- Cycle-detection gate (iterative DFS, explicit stack — NO recursion) + missing-next-step gate, both owned by `CycleDetector`.
- Schema-edge compatibility gate (strict `Guid` equality; null-on-either-side passes) in `SchemaEdgeValidator`.
- Payload↔ConfigSchema gate (JsonSchema.Net draft 2020-12; per-Start parse cache; SSRF lockdown mirrored) in `PayloadConfigSchemaValidator`.
- A new 422 error path: a single orchestration-validation exception + a dedicated `IExceptionHandler`.
- A shared SSRF-locked JSON Schema config factory, with the Phase 8 `SchemaDtoValidator` refactored to consume it.
- Mandatory gate order enforced by the (already-final) orchestrator body: existence → cycles → schema-edge → Payload↔ConfigSchema.

**Out of scope (later phases):**
- Redis (L2) projection WRITE + Stop-as-Redis-EXISTS — **Phase 15** (L2-PROJECT-*, ORCH-START-*, ORCH-STOP-*, OBSERV-REDIS-*).
- Idempotency / concurrency regression facts + end-to-end happy-path + 3-GREEN closeout — **Phase 16** (TEST-REDIS-06..09).
- VALID-21 at Assignment HTTP-write time (PUT/POST) — explicitly deferred (FUTURE-VALID-21-HTTP-WRITE); v3.3.0 closes it only at orchestration-start.
- Schema-edge STRUCTURAL compatibility (subset/canonical) — strict `Schema.Id` equality only in v3.3.0.

</domain>

<decisions>
## Implementation Decisions

### 422 Error Contract (L1-VALIDATE-01, ORCH-START-03 forward-context)
- **D-01:** All three gates throw a **single new exception type** (e.g. `OrchestrationValidationException`) carrying a **gate discriminator** + a **typed offending payload**. One type, one envelope across all gates — mirrors the codebase's `NotFoundException` (one exception → one handler → typed extensions) pattern rather than per-gate subclasses.
- **D-02:** A **single new `IExceptionHandler`** renders that exception → **HTTP 422** RFC 7807. It is reached BEFORE the Core `FallbackExceptionHandler`. `correlationId` + `instance` are **NOT** set by this handler — the Phase 4 `AddProblemDetails.CustomizeProblemDetails` customizer injects both into every emission automatically. The handler sets only `Status` (422), `Title` (gate-specific), `Detail`, and the `errors` extension.
- **D-03:** The RFC 7807 body uses a **gate-discriminated, typed offending object** in the `errors` extension: `{ gate: "cycle" | "missingStep" | "schemaEdge" | "payloadConfigSchema", offending: { …gate-specific fields… } }`. Per-gate offending shapes:
  - `cycle` → `{ stepChain: [Guid, …] }` (the detected cycle path).
  - `missingStep` → `{ parentStepId: Guid, missingChildId: Guid }`.
  - `schemaEdge` → `{ parentStepId: Guid, childStepId: Guid }`.
  - `payloadConfigSchema` → `{ assignmentId: Guid, errors: [string, …] }` (flattened JsonSchema.Net validation messages — see D-09).
- **D-04 (handler-ordering constraint — planner/research must resolve the mechanism):** The Core IExceptionHandler chain registers `FallbackExceptionHandler` (a true catch-all that claims every exception) **last**, in `ErrorHandlingServiceCollectionExtensions.AddBaseApiErrorHandling`. `AddExceptionHandler` order == registration order == walk order. Therefore the orchestration 422 handler — placed in Service per D-11 — **cannot simply be appended by the feature extension after `AddBaseApiErrorHandling` has run**, or Fallback claims the exception first and emits 500. **Locked intent:** Core stays orchestration-agnostic; the concrete exception + handler live in Service. **Resolution options for the planner** (pick during planning, do NOT re-ask user): (a) ensure the orchestration feature's `AddExceptionHandler` call is registered BEFORE the Core Fallback in the composition root ordering; or (b) have Core expose a small "pre-Fallback domain-handler" seam. Either way, Fallback must remain the last-walked handler and the orchestration handler must be reachable.

### SSRF-locked JSON Schema config (L1-VALIDATE-07)
- **D-05:** Extract a **shared `JsonSchemaConfig`** (lives in **BaseApi.Service**, near Schema — `Features/Schema` or a Service-level `Validation` folder) as the **single source of SSRF truth**: sets `Dialect.Default = Dialect.Draft202012` + `SchemaRegistry.Global.Fetch = (_, _) => null` exactly once, and exposes the canonical default `EvaluationOptions`/`DefaultOptions`. **Refactor `SchemaCreateDtoValidator` + `SchemaUpdateDtoValidator`** to consume it (removing their inline static-ctor lockdown). The new `PayloadConfigSchemaValidator` consumes the same factory.
- **D-06 (regression guard):** The Phase 8 SSRF defense MUST NOT regress — the `<500ms` SSRF regression assertion and the draft-2020-12 meta-schema behavior stay GREEN after the refactor. This refactor touches Phase 8 code; treat it as a bisect-friendly commit that keeps the Schema validator tests passing throughout.

### Gate logic (L1-VALIDATE-03/04/05/06/08)
- **D-07 (CycleDetector owns cycle + missing-step; no shared walk):** `CycleDetector.Validate` runs ONE **iterative DFS** with an explicit stack/list over `snapshot.Steps[*].NextStepIds`, starting from each `Workflow.EntryStepIds[*]`. **No recursion** (`StackOverflowException` is uncatchable by `IExceptionHandler`). On each visit, check `visited.Contains(stepId)` BEFORE adding → if already present, throw `cycle` with the stepId chain. For every `NextStepId` referenced, if it is NOT a key in `snapshot.Steps`, throw `missingStep` with `(parentStepId, missingChildId)`. `null`/empty `NextStepIds` = terminal step, passes. `SchemaEdgeValidator` does its OWN independent edge walk — **no shared traversal abstraction** is built (honors Phase 13 D-03: the loader walk, cycle walk, and edge walk have differing semantics; do not force a shared shape now).
- **D-08 (missing-step is defense-in-depth — document, don't drop):** `StepNextSteps` has an FK-Restrict to `Step`, so any `NextStepId` in the junction already references a row that the loader's BFS will have loaded. The `missingStep` gate is therefore largely **defense-in-depth** under normal DB integrity, but **L1-VALIDATE-04 mandates it regardless** — implement and test it (a crafted/forced snapshot exercises the path).
- **D-09 (SchemaEdgeValidator):** For every `(parent → child)` edge over **EVERY** entry in `parent.NextStepIds` (not just the first), resolve `parent.ProcessorId → snapshot.Processors[pid].OutputSchemaId` and `child.ProcessorId → snapshot.Processors[pid].InputSchemaId`; require **strict `Guid` equality**. If either side is `null` (Phase 10 source/sink/unconfigured processor), the edge **passes**. On mismatch, throw `schemaEdge` with `(parentStepId, childStepId)`.
- **D-10 (PayloadConfigSchemaValidator + per-Start cache):** Iterate every Assignment in `snapshot.Assignments`. Resolve `Assignment.StepId → snapshot.Steps[stepId].ProcessorId → snapshot.Processors[pid].ConfigSchemaId → snapshot.Schemas[cid].Definition`; parse to a `JsonSchema` and validate `Assignment.Payload` (draft 2020-12, via the D-05 SSRF-locked options). If `ConfigSchemaId` is `null`, the assignment **passes** (no schema to validate against). On failure, throw `payloadConfigSchema` with `assignmentId` + a **flattened list of JsonSchema.Net error strings** (D-03). **Per-Start parse cache** (L1-VALIDATE-08): a `Dictionary<Guid, JsonSchema>` keyed by `Schema.Id`, each schema parsed at most once. Because the validator contract is synchronous `void Validate(snapshot)` and the seam is DI-registered (Scoped), the cache is a **local variable created per `Validate` invocation** (NOT an instance field) so it never leaks across requests.

### Scope (L1-VALIDATE-09/10)
- **D-11 (placement):** The new `OrchestrationValidationException` + its `IExceptionHandler` live in **`BaseApi.Service.Features.Orchestration`** (registered into the Core chain per D-04). `JsonSchemaConfig` lives in **BaseApi.Service** near Schema. **BaseApi.Core stays generic** — no orchestration-flavored types are promoted to Core (defers promotion until a second consumer exists, mirroring Phase 13 D-02 / ORCH-SPLIT-02 "internal until a second consumer surfaces").
- **D-12 (VALID-21 boundary):** VALID-21 closes ONLY at orchestration-start. `Assignment`-PUT and `Assignment`-POST remain "valid JSON only" — the v3.2.0 HTTP-write behavior is preserved and untouched. L1-VALIDATE-10 records that TEST-03 (Testcontainers) / TEST-04 (Respawn) remain deferred (not closed by Phase 14).

### Claude's Discretion
- Exact class/file names, the offending-payload record types, and whether the gate discriminator is a string vs an enum serialized to string.
- The precise composition-root mechanism that satisfies D-04 (the planner picks (a) or (b) or an equivalent that keeps Fallback last-walked and the orchestration handler reachable).
- Whether the per-Start `Dictionary<Guid, JsonSchema>` cache is an inline local or a tiny private helper struct.
- Whether `CycleDetector`'s DFS stack stores raw `Guid` step ids or `(parent, child)` frames to reconstruct the cycle chain for D-03's `stepChain`.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Requirements & roadmap (authoritative)
- `.planning/REQUIREMENTS.md` §L1-VALIDATE-01..10 — the 10 locked Phase 14 requirements: mandatory gate order (01), existence (02), iterative-DFS cycle (03), missing-next-step (04), schema-edge strict-equality (05), Payload↔ConfigSchema (06), SSRF lockdown extension (07), per-Start schema cache (08), VALID-21 orchestration-start-only (09), TEST-03/04 still-deferred (10). Also §ORCH-START-03 for the forward-context 422 error-body contract (structured `errors` extension identifying offending ids).
- `.planning/ROADMAP.md` §"Phase 14" — Goal, 5 Success Criteria, Depends-on (Phase 13), and the **v3.2.0 invariants that MUST NOT regress** (Phase 8 SSRF lockdown + `<500ms`; Phase 4 RFC 7807 + X-Correlation-Id + SQLSTATE→HTTP; Phase 6 FluentValidation 12 manual `ValidateAsync`; Assignment-PUT/POST "valid JSON only"; Mapperly RMG codes; byte-identical `psql \l` SHA-256).
- `.planning/PROJECT.md` §"Current Milestone: v3.3.0" — L3→L1→L2 pipeline framing; locked constraints (validation order is mandatory; schema-edge is strict `Schema.Id` equality with null-passes; no new SQL entities/columns).

### Seams to fill (the no-op validators)
- `src/BaseApi.Service/Features/Orchestration/Validation/CycleDetector.cs` — fill: iterative-DFS cycle + missing-step (D-07/D-08).
- `src/BaseApi.Service/Features/Orchestration/Validation/SchemaEdgeValidator.cs` — fill: independent edge walk, strict Guid equality (D-09).
- `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs` — fill: JsonSchema.Net validation + per-Start cache (D-10).
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` — the orchestrator; gate order is ALREADY structurally final (existence → cycle → schema-edge → payload → writer). Phase 14 fills seam bodies only — **zero re-wiring of `StartAsync`** (Phase 13 D-01).
- `src/BaseApi.Service/Features/Orchestration/WorkflowGraphSnapshot.cs` — the 5 in-memory dictionaries the validators read (input contract).

### L1 input shape (what the loader hands the validators)
- `src/BaseApi.Service/Features/Orchestration/Loading/WorkflowGraphLoader.cs` — **CRITICAL**: enriches `StepReadDto.NextStepIds` (from `StepNextSteps`) and `WorkflowReadDto.EntryStepIds`/`AssignmentIds` (from `WorkflowEntrySteps`/`WorkflowAssignments`) via `with { … }`; the BFS `visited` list keyed on StepId means a cyclic graph LOADS (terminates) but is NOT rejected — that rejection is Phase 14's `CycleDetector`. Shows why missing-step is defense-in-depth (D-08).
- `src/BaseApi.Service/Features/Step/StepDtos.cs` (`StepReadDto.NextStepIds`, `EntryCondition`, `ProcessorId`), `src/BaseApi.Service/Features/Processor/ProcessorDtos.cs` (`InputSchemaId`/`OutputSchemaId`/`ConfigSchemaId`, all `Guid?`), `src/BaseApi.Service/Features/Assignment/AssignmentDtos.cs` (`StepId`, `Payload`), `src/BaseApi.Service/Features/Schema/SchemaDtos.cs` (`Definition`), `src/BaseApi.Service/Features/Workflow/WorkflowDtos.cs` (`EntryStepIds?`) — the resolution-chain field sources for every gate.

### SSRF lockdown to extract + refactor (D-05/D-06)
- `src/BaseApi.Service/Features/Schema/SchemaDtoValidator.cs` — the Phase 8 inline static-ctor lockdown (`Dialect.Default = Dialect.Draft202012` + `SchemaRegistry.Global.Fetch = (_, _) => null`) + `MetaSchemas.Draft202012.Evaluate(... OutputFormat.List)`. This is the SSRF pattern to lift into the shared `JsonSchemaConfig` and the validation-error flattening template for D-03.

### 422 error path (the pattern to mirror + the wiring to extend)
- `src/BaseApi.Core/Exceptions/NotFoundException.cs` + `src/BaseApi.Core/Exceptions/Handlers/NotFoundExceptionHandler.cs` — the exception+handler+typed-extensions pattern to mirror for `OrchestrationValidationException` (D-01/D-02). Note the Pitfall-6 fast-bail (`is not …` → `return false`).
- `src/BaseApi.Core/Exceptions/Handlers/ValidationExceptionHandler.cs` — the existing FluentValidation→**400** handler (do NOT collide with it; the new gate exception is a distinct type → **422**).
- `src/BaseApi.Core/DependencyInjection/ErrorHandlingServiceCollectionExtensions.cs` — the LOAD-BEARING chain order (NotFound → Validation → DbUpdate → Fallback) + the `AddProblemDetails` customizer that auto-injects `correlationId`+`instance`. **D-04's ordering constraint lives here** — Fallback is the last-walked catch-all.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` — `AddOrchestrationFeature`; where the new handler registration is added (subject to the D-04 ordering resolution).
- `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs` §line 103 — the existing FK-violation→422 path (the ONLY 422 today); confirms 422 status semantics, not the domain-validation source.

### Prior decisions carried forward
- `.planning/phases/13-orchestrationservice-split-l3-fetch-l1-build/13-CONTEXT.md` §D-01 (orchestrator body structurally final — Phase 14 fills bodies only), §D-02 (validators are concrete-name, **synchronous** `void Validate(snapshot)`, throw-on-failure), §D-03 (loader walk vs cycle walk have opposite semantics — no forced shared abstraction).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **The 3 no-op validator seams** (`CycleDetector`, `SchemaEdgeValidator`, `PayloadConfigSchemaValidator`) — already created, internal, sealed, `void Validate(WorkflowGraphSnapshot)`; Phase 14 fills bodies only.
- **`WorkflowGraphSnapshot`'s 5 dictionaries** — `Steps`/`Processors`/`Schemas`/`Assignments`/`Workflows` keyed by Id; the validators' sole input (pure in-memory, no I/O).
- **`SchemaDtoValidator` SSRF pattern** — `Dialect.Default` + `SchemaRegistry.Global.Fetch = (_,_) => null` + `MetaSchemas.Draft202012.Evaluate(..., OutputFormat.List)` → the lift target for `JsonSchemaConfig` and the error-flattening template.
- **`NotFoundException` + `NotFoundExceptionHandler`** — the exact exception→handler→typed-extensions shape to mirror for the 422 path.
- **Phase 4 `AddProblemDetails` customizer** — gives `correlationId` + `instance` for free on the new 422 body.

### Established Patterns
- **No nav properties between entities** — gates resolve cross-entity references via the snapshot dictionaries (`Steps[id].ProcessorId` → `Processors[id]` → schema ids → `Schemas[id]`), never EF navigation.
- **Loader enriches junction collections** — `StepReadDto.NextStepIds` and `WorkflowReadDto.EntryStepIds` ARE populated in the L1 path (HTTP GET/List still return null — untouched). The validators rely on this L1-only enrichment.
- **IExceptionHandler chain is order-load-bearing** — first handler to return true claims; Fallback is the catch-all and must stay last-walked (D-04).
- **Pre-wired-but-no-op seam → fill body** (Phase 9 D-05, Phase 12 Redis, Phase 13 validators) — Phase 14 is the "fill" step; no orchestrator re-wiring.
- **Bisect-friendly N-commit sequence** (Phase 10 precedent) — the SSRF-config extraction (D-05) and each gate land as isolated commits keeping the suite GREEN.

### Integration Points
- `OrchestrationService.StartAsync` lines 79–81 — the three `*.Validate(snapshot)` calls whose bodies Phase 14 fills (call sites unchanged).
- The new handler registers via `OrchestrationServiceCollectionExtensions.AddOrchestrationFeature`, into the Core chain ahead of Fallback (D-04).
- `JsonSchemaConfig` is consumed by BOTH the refactored `SchemaDtoValidator` (Phase 8) and the new `PayloadConfigSchemaValidator` (Phase 14).

</code_context>

<specifics>
## Specific Ideas

- **Success-criterion-5 test is the order gate:** integration tests must supply a workflow that fails MULTIPLE gates at once and assert the FIRST gate's 422 (existence → cycle → schema-edge → payload), proving short-circuit order. L1 cleanup (`snapshot.Dispose()` / `IsDisposed`) must still run on the validation-failure path (the `using` declaration already guarantees this — assert it).
- **Cycle chain reconstruction:** the `cycle` offending `stepChain` (D-03) should be the actual detected path, so the DFS stack likely needs to carry enough to reconstruct it (Claude's Discretion on stack frame shape).
- **No recursion anywhere in the cycle gate** — explicit stack/list only; this is a hard requirement (StackOverflowException bypasses `IExceptionHandler`).

</specifics>

<deferred>
## Deferred Ideas

- **Redis L2 projection write + Stop-as-EXISTS** — Phase 15 (writer is still no-op after Phase 14; StopAsync keeps the existence check).
- **Idempotency / concurrency / end-to-end happy-path / 3-GREEN closeout** — Phase 16.
- **VALID-21 at Assignment HTTP-write (PUT/POST)** — FUTURE-VALID-21-HTTP-WRITE; never in v3.3.0.
- **Schema-edge STRUCTURAL compatibility** (subset/superset/canonical) — strict `Schema.Id` equality only this milestone.
- **Shared graph-walk abstraction** (loader walk + cycle walk + edge walk) — still not built (Phase 13 D-03 deferred; Phase 14 confirms separate walks per D-07). Revisit only if a clean shared shape emerges later.
- **Promotion of `OrchestrationValidationException`/handler/`JsonSchemaConfig` to BaseApi.Core** — deferred until a second consumer surfaces (D-11; mirrors ORCH-SPLIT-02).

None of these are scope creep — all are existing later-phase or future-milestone boundaries.

</deferred>

---

*Phase: 14-validation-gates-dfs-schema-edge-payload-config-schema*
*Context gathered: 2026-05-29*
