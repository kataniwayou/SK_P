# Phase 13: OrchestrationService split + L3 fetch + L1 build - Context

**Gathered:** 2026-05-29
**Status:** Ready for planning

<domain>
## Phase Boundary

Decompose the Phase 9 `OrchestrationService` (92 LOC, single shared `ValidateWorkflowIdsAsync`) into a thin orchestrator delegating to 4 internal seams, and load a transient in-memory **L1** `WorkflowGraphSnapshot` from Postgres (**L3**) on every Start request.

**In scope (this phase):**
- Split `OrchestrationService` into orchestrator + 4 seams under `Features/Orchestration/{Loading,Validation,Projection}/`:
  - `IWorkflowGraphLoader` (Loading/) — the only seam with real behavior in P13
  - `CycleDetector`, `SchemaEdgeValidator`, `PayloadConfigSchemaValidator` (Validation/) — **no-op** stubs in P13
  - `IRedisProjectionWriter` (Projection/) — **no-op** stub in P13
- `StartAsync` becomes the orchestrator (~80 LOC): existence-check → loader → 3 validators (ordered) → writer → `Dispose`.
- `StopAsync` extracted as a separate method (Start/Stop no longer share a method).
- `WorkflowGraphSnapshot` transient `IDisposable` record; flat `Dictionary<Guid, EntityDto>` per entity type (Workflows, Assignments, Steps, Processors, Schemas), keyed by `Id`, values = v3.2.0 ReadDto types.
- Loader uses direct `BaseDbContext.Set<>().AsNoTracking()...Where(x => ids.Contains(x.Id))` batch queries (Phase 3 5-method `IRepository<>` surface unchanged).
- Step traversal: walk every `Workflow.EntryStepIds[*]` → every `StepEntity.NextStepIds[*]`, per-traversal `visited` list keyed on StepId.
- Deterministic cleanup via `using` declaration on success AND failure paths.

**Out of scope (later phases):**
- Validation LOGIC (cycle/schema-edge/payload rejection → 422) — **Phase 14**.
- Redis (L2) projection WRITE + Stop-as-Redis-EXISTS — **Phase 15**.
- Idempotency / concurrency / closeout — **Phase 16**.

</domain>

<decisions>
## Implementation Decisions

### Seam Scaffolding (ORCH-SPLIT-01/03)
- **D-01:** Scaffold ALL 4 seams now as no-op (validators + writer). `StartAsync`'s body is structurally FINAL in P13 — `existence-check → loader → CycleDetector → SchemaEdgeValidator → PayloadConfigSchemaValidator → IRedisProjectionWriter → snapshot.Dispose()`. Phases 14/15 fill seam bodies only; zero re-wiring. Mirrors the codebase's pre-wire-for-stability pattern (Phase 9 D-05 pre-injected unused mappers; Phase 12 pre-wired Redis DI before any writer existed). The split commit shows the final orchestrator shape (bisect-friendly, Phase 10 precedent).
- **D-02:** Validators are invoked by **explicit ordered calls by concrete name** (not a shared-interface chain), contract `void Validate(WorkflowGraphSnapshot snapshot)` — **synchronous** (pure in-memory over the loaded L1, no I/O), **throws** a 422-mapped exception on failure. In P13 the bodies are empty (no-op). Rationale: the Phase 14 mandatory order (existence → cycles → schema-edge → payload) becomes visible and un-reorderable in the orchestrator body, rather than registration-order-dependent. Concrete (no `I` prefix) per REQ naming; loader/writer remain interfaces.

### Traversal (L1-BUILD-04)
- **D-03:** Traversal lives **inside `IWorkflowGraphLoader.LoadL1Async` as private logic** (iterative BFS from entry steps; visited-list keyed on StepId). `WorkflowGraphSnapshot` stays **pure data** (the 5 dictionaries only — no walk methods). Critical P13 role: because validators are no-op, **nothing rejects cycles yet** — the visited list is what makes loading TERMINATE on a cyclic workflow. Phase 14's `CycleDetector` does a *separate*, cycle-*rejecting* walk over the materialized snapshot (opposite semantics: tolerate-to-load vs reject), so no shared walk abstraction is forced now.

### Dispose Observability (L1-BUILD-05, Success Criterion 4)
- **D-04:** `WorkflowGraphSnapshot` exposes a public `IsDisposed` bool set true in `Dispose()` (which also clears the dictionaries) + an `ILogger.LogDebug("L1 snapshot disposed")` line. The forced-throw integration test captures the snapshot instance via a **recording `IWorkflowGraphLoader` test double** and asserts `IsDisposed == true` and dictionaries empty after the throw. The throw is forced by injecting a **throwing seam double** (validator or writer) that fires *after* the loader returns — enabled precisely because D-01 scaffolded all 4 seams. Log line is a prod-diagnostics bonus.

### Mapper Ownership (ORCH-SPLIT, L1-BUILD-03)
- **D-05:** The 5 `IEntityMapper` instances **move from `OrchestrationService` ctor to the `IWorkflowGraphLoader` impl ctor**. The orchestrator drops all 5 mapper fields AND their `[SuppressMessage(IDE0052)]` attributes (resolves the Phase 9 D-05 "pre-injected, unused" smell — P13 IS that "v2"). Loader produces ReadDtos via the existing **Mapperly** mappers (AsNoTracking entity load → `ToRead`), consistent with the codebase's single RMG-guarded mapping seam. Inline `.Select` projection rejected (would duplicate mapping outside the drift-guarded seam).
- **D-06:** Mapperly `ToRead` deliberately returns **null** junction collections (v1 GET/List deferral — see `StepEntityMapper.ToRead` `[MapValue(NextStepIds, null)]`). The loader **enriches** them: keep Mapperly for all 5 (scalars), then for **Step** (`NextStepIds`) and **Workflow** (`EntryStepIds`, `AssignmentIds`) batch-load the junction tables (`StepNextSteps`, `WorkflowEntrySteps`, `WorkflowAssignments`) and rebuild the immutable records via `readDto with { NextStepIds = [...] }`. Schema / Processor / Assignment (now `(StepId, Payload)` post-Phase-10) need no enrichment. **P13 closes the v1-deferred junction-null gap for the L1 path** (the HTTP GET/List null behavior is untouched).

### Derived (locked by REQ/ordering, confirmed by user)
- **D-07 (StopAsync, ORCH-SPLIT-04):** `StopAsync` is extracted as its own method retaining the current workflow-id existence-check behavior (the old `ValidateWorkflowIdsAsync` logic) in P13. Phase 15 swaps it to the Redis `EXISTS` check. Start and Stop no longer share a method (honors Phase 9 D-09 "refactor when they diverge").
- **D-08 (existence-check placement):** The existing `SELECT id WHERE id IN (...)` workflow-existence check stays as `StartAsync`'s FIRST step (ORCH-SPLIT-03 order is locked). The small overlap with the loader's later workflow fetch is intentional — it fails fast (404) before building any L1 state.

### Claude's Discretion
- **Loader batch-query staging:** depth-wave step loads (round-trips ≈ graph depth) → one batched processor query (collected `ProcessorId`s) → one batched schema query (collected Input/Output/Config schema ids); assignments via one batched query from the workflows' `AssignmentIds` junction. All `AsNoTracking` + `Where(ids.Contains)`. Exact staging is topology-forced — planner decides.
- **DI lifetimes for new seams:** register loader + 3 validators + writer in `OrchestrationServiceCollectionExtensions` mirroring `OrchestrationService`'s existing **Scoped** lifetime, unless research surfaces a reason otherwise.
- **No-op writer signature:** the `IRedisProjectionWriter` method shape (async, takes the snapshot, returns `Task`) — planner defines a forward-compatible signature Phase 15 can fill without re-wiring.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Requirements & roadmap (authoritative)
- `.planning/REQUIREMENTS.md` §ORCH-SPLIT-01..04, §L1-BUILD-01..05 — the locked P13 requirements (seam names, internal visibility, ~80 LOC orchestrator, snapshot shape, batch-query mechanism, traversal, cleanup contract). Also §L1-VALIDATE-01..10 for forward context (what the no-op validator seams become in Phase 14).
- `.planning/ROADMAP.md` §"Phase 13" — Goal, 5 Success Criteria, Depends-on (Phase 12), and the v3.2.0 invariants that MUST NOT regress.
- `.planning/PROJECT.md` §"Current Milestone: v3.3.0" — L3→L1→L2 pipeline framing, locked constraints (no new SQL entities/columns, validation order, L2 DTO field names), deferred items.

### Existing code to refactor (the split target)
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` — the 92-LOC service being split. Note the 5 pre-injected mappers (D-05 source) and `ValidateWorkflowIdsAsync` (split into Start path + D-07 StopAsync).
- `src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs` — Start/Stop endpoints; singular class name (Phase 9 D-13); concrete-on-concrete injection (Phase 9 D-06).
- `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` — `AddOrchestrationFeature`; add the 4 new seam registrations here.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationDtoValidator.cs` — auto-discovered `WorkflowIdsValidator` (unchanged).

### Mapping / junction patterns (critical for loader design — D-06)
- `src/BaseApi.Service/Features/Step/StepEntityMapper.cs` — `ToRead` uses `[MapValue(NextStepIds, null)]`; PROVES junction collections come back null from Mapperly (the D-06 enrichment driver).
- `src/BaseApi.Service/Features/Step/StepService.cs` — `SyncJunctionsAsync` junction read/write pattern over `StepNextSteps` (template for how the loader queries junction rows).
- `src/BaseApi.Service/Features/Step/StepNextSteps.cs` — `(StepId, NextStepId)` join entity.
- `src/BaseApi.Service/Features/Workflow/WorkflowEntityMapper.cs` + `WorkflowEntrySteps.cs` + `WorkflowAssignments.cs` — the dual-M2M mapper + both join entities (EntryStepIds, AssignmentIds enrichment).
- `src/BaseApi.Service/Features/Step/StepDtos.cs`, `src/BaseApi.Service/Features/Workflow/WorkflowDtos.cs` — ReadDto positional-record shapes (rebuild via `with {}`).

### Prior decisions carried forward
- `.planning/phases/12-redis-infra-composition-healthcheck-di-registration/12-CONTEXT.md` §D-14/D-15/D-16 — Redis `IConnectionMultiplexer` Singleton + `IDatabase` per-call; writer impl explicitly deferred to Phase 15 (the no-op writer in P13 must not assume more than this).
- Phase 9 CONTEXT decisions (referenced in `OrchestrationService.cs` XML docs): D-04 (not a `BaseService<>` subclass), D-05 (5 mappers pre-injected — now relocated by D-05 here), D-09 (refactor Start/Stop when they diverge — honored by D-07), D-10 (id-projection existence check — D-08).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`BaseDbContext.Set<>().AsNoTracking()`** — the loader's read mechanism (Phase 9 `ValidateWorkflowIdsAsync` already uses this exact pattern for the existence check; D-08 keeps it).
- **5 Mapperly `IEntityMapper` instances** — auto-discovered (Phase 6 `AddBaseApiMapping`); relocate into the loader (D-05).
- **`WorkflowIdsValidator`** — auto-discovered; unchanged, still gates Start/Stop input.
- **3 junction join entities** (`StepNextSteps`, `WorkflowEntrySteps`, `WorkflowAssignments`) — queried directly for D-06 enrichment.

### Established Patterns
- **No nav properties between entities** — junctions are scalar-FK join entities; the loader walks/enriches via explicit junction queries, NOT EF `.Include()` (rejected option in D-06).
- **Mapperly-everywhere + RMG drift = build errors** — loader uses `ToRead`, not inline `.Select` (D-05).
- **Concrete-on-concrete DI** (Phase 9 D-06) — `OrchestrationController` injects concrete `OrchestrationService`; new seams added to the feature's own `AddOrchestrationFeature` extension.
- **Pre-wire shape before use** (Phase 9 D-05, Phase 12 Redis) — justifies D-01 full-seam scaffolding.
- **Bisect-friendly N-commit sequence** (Phase 10) — the split lands as its own commits BEFORE L1 build logic.

### Integration Points
- `StartAsync` / `StopAsync` are the only orchestrator surface; controller endpoints unchanged in routing.
- New seams register in `OrchestrationServiceCollectionExtensions.AddOrchestrationFeature` (6th feature call in `AppFeatures`).
- Loader reads the same 5 entity tables + 3 junction tables created by the v3.2.0 `InitialCreate` migration (no schema change — locked).

</code_context>

<specifics>
## Specific Ideas

- The forced-throw cleanup test (Success Criterion 4) is THE acceptance gate for the L1 cleanup contract — D-04 (recording loader double + throwing seam double + `IsDisposed` assertion) is the agreed mechanism. Plan it explicitly.
- `visited` is a `List<Guid>` keyed on StepId (NOT a `HashSet` on object identity) — REQ L1-BUILD-04 is explicit; the user confirmed traversal lives in the loader where this list guarantees termination on cyclic input.

</specifics>

<deferred>
## Deferred Ideas

- **Validator rejection logic** (cycle / schema-edge / payload → 422, mandatory order) — Phase 14. P13 seams are no-op.
- **Redis L2 projection write + Stop-as-EXISTS** — Phase 15. P13 writer is no-op; StopAsync keeps workflow-id existence check (D-07).
- **Shared graph-walk abstraction** (loader walk + CycleDetector walk) — not built now; the two walks have opposite cycle semantics (D-03). Revisit in/after Phase 14 if a clean shared shape emerges.
- **HTTP GET/List junction enrichment** — D-06 enriches the L1 path only; the v1 GET/List null behavior on `StepReadDto.NextStepIds` etc. is intentionally untouched.

None of these are scope creep — all are existing later-phase boundaries.

</deferred>

---

*Phase: 13-orchestrationservice-split-l3-fetch-l1-build*
*Context gathered: 2026-05-29*
