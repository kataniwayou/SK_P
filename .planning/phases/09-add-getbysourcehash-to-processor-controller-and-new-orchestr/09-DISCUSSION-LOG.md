# Phase 9: Processor.GetBySourceHash + Orchestration Start/Stop - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-28
**Phase:** 09-add-getbysourcehash-to-processor-controller-and-new-orchestr
**Areas discussed:** GetBySourceHash data access, OrchestrationService composition, Validation, Response shape

---

## Area 1: GetBySourceHash data access

### Q1.1: Where should the SourceHash lookup execute?

| Option | Description | Selected |
|---|---|---|
| ProcessorService.GetBySourceHashAsync via DbContext.Set<> | Service queries DbContext directly; mirrors Phase 3 D-04 doc-comment guidance | ✓ |
| Extend IRepository<ProcessorEntity> with GetBySourceHashAsync | Violates Phase 3 D-04 "EXACTLY 5 methods, no helpers" | |
| New IProcessorRepository : IRepository<ProcessorEntity> | Entity-specific repo; heavier; introduces new DI registration | |

**User's choice:** Option 1 (ProcessorService.GetBySourceHashAsync via DbContext.Set<>)
**Notes:** Most consistent with Phase 3 D-04 — junction/specialized lookups go via raw DbContext.Set<T>() from the Service.

### Q1.2: Should the lookup use AsNoTracking() for the read-only path?

| Option | Description | Selected |
|---|---|---|
| Yes — AsNoTracking() for the read path | Read-only intent; slightly faster; avoids ChangeTracker pollution | ✓ |
| No — default tracking (mirror BaseService.GetByIdAsync) | Behavioral parity with GetById which uses default tracking | |

**User's choice:** Option 1 (AsNoTracking)
**Notes:** User asked for an explanation before deciding; explanation covered ChangeTracker semantics, identity-map behavior, and the small deliberate divergence from BaseService.GetByIdAsync.

---

## Area 2: OrchestrationService composition

### Q2.1: What does OrchestrationService inject to fetch Workflows?

| Option | Description | Selected |
|---|---|---|
| Abstract BaseService<WorkflowEntity, …> | Reuses existing DI alias; matches Phase 7 Warning 7 | |
| Concrete WorkflowService | Direct dependency; couples to implementation type | |
| DbContext + WorkflowEntityMapper directly | Single SQL query via WHERE…IN; minimum surface | ✓ (modified — all 5 mappers up front) |

**User's choice:** Option 3 (Direct DbContext + Mapper) with a key modification — **all 5 entity mappers injected up front** in the ctor, not just WorkflowEntityMapper, to support stated v2 direction ("eventually the orchestration controller will need all entities").

**Notes — extended discussion:**
- User flagged: "eventually the orchestration controller will need all entities (not in this phase)." Led to comparison of Path A (grow ctor one entity at a time across phases) vs. Path B (build a façade now).
- User asked: "is it possible to di to orchestration controller the repositories?" — explained the IRepository<T> route is possible but diverges from convention (controllers inject BaseService<…>) and would require injecting both repo + mapper (doubles ctor params in v2).
- User asked: "I only need ReadDto — what option do you recommend?" — changed recommendation from BaseService<…> (Option 1) to Direct DbContext + Mapper (Option 3) on the basis that the List<Guid>→List<ReadDto> shape is inherently a batch read; BaseService.GetByIdAsync would generate N queries instead of 1.
- After the Area 4 clarification (v1 returns 204 No Content), the mappers became unused in v1 entirely. User confirmed the ctor still injects all 5 for v2 readiness.

### Q2.2: How should the controller depend on OrchestrationService?

| Option | Description | Selected |
|---|---|---|
| Inject concrete OrchestrationService | No abstract base for orchestration; concrete-on-concrete is appropriate | ✓ |
| Introduce an IOrchestrationService interface | Pure abstraction for testability; heavier | |

**User's choice:** Option 1 (Inject concrete OrchestrationService)
**Notes:** Locked alongside Q2.1 — controller convention here intentionally diverges from Phase 7 Warning 7 because there's no generic BaseService<…> equivalent for orchestration.

---

## Area 3: Validation

### Q3.1: Where should the duplicate-id check fire?

| Option | Description | Selected |
|---|---|---|
| FluentValidation AbstractValidator<IReadOnlyList<Guid>> via AddBaseApiValidation pipeline | Auto-discovered; OrchestrationService calls ValidateAndThrowAsync as step 1; mirrors Phase 6 / BaseService.CreateAsync step 1 | ✓ |
| Inline check in OrchestrationService (throw ValidationException manually) | Bypasses auto-discovery convention | |
| Inline check in OrchestrationController (return BadRequest manually) | Bypasses exception-handler chain entirely | |

**User's choice:** Option 1 (FluentValidation auto-discovery)
**Notes:** User asked "what is the most consistent pattern?" — explanation traced Phase 6 VALID-03 + AddValidatorsFromAssembly + Phase 4 ValidationExceptionHandler precedents. Option 1 is the only path that hits all four established conventions. Flagged the unavoidable divergence: `AbstractValidator<IReadOnlyList<Guid>>` targets a generic collection (not a project DTO record) and is shared by Start + Stop endpoints — acceptable in v1 because they're behaviorally identical.

### Q3.2: Which rules go on the validator?

| Option | Description | Selected |
|---|---|---|
| NotEmpty — reject empty list [] | [] is a no-op request; 400 is more honest than 200-with-empty-array | ✓ |
| NotEqual(Guid.Empty) per id | Phase 8 VALID-11 pattern — reject all-zero guids explicitly | ✓ |
| MaximumCount(100) — DoS guard | Phase 8 VALID-16 parallel; reasonable but not in SPEC.md | |
| Just duplicates — nothing else | Smallest possible surface | |

**User's choice:** NotEmpty + NotEqual(Guid.Empty) (plus the duplicate check that's the area's primary rule)
**Notes:** MaximumCount deferred (outside SPEC.md v1 scope).

---

## Area 4: Response shape

### Q4.1 (original): What ordering guarantee should the response provide?

| Option | Description | Selected |
|---|---|---|
| Preserve input order | Predictable for clients | (moot after SPEC.md amendment) |
| Database-natural order | No ORDER BY; cheaper but unpredictable | (moot) |
| Order by CreatedAt DESC | Deterministic but semantically arbitrary | (moot) |

**User's response:** "why it need to reurn something. explain?" — user questioned the SPEC.md decision to return any body at all.

**Extended discussion:**
- Explanation laid out reasons SPEC.md originally chose `200 OK + List<WorkflowReadDto>` (GetById design parity, HTTP convention for POST 200, acknowledges what was acted on) vs. reasons to reconsider (redundant when v1 has no side-effects, payload weight, v2 mismatch risk).
- Presented 4 alternative response shapes: full WorkflowReadDto[] (current SPEC), 204 No Content, minimal echo, per-id status envelope.

### Q4.2 (clarified): What is the actual v1 behavior?

**User's clarification:** "I want to clarify again, the orchestrator start/stop get workflowids list then it validate the list. that all. it do the same to stop. for further phase i'll need all entities. confirm all"

**Decision locked:**
- v1 endpoints validate the list only (NotEmpty + no duplicates + NotEqual(Guid.Empty) + existence check)
- No entity projection, no response body
- **Return `204 No Content` on success**
- Both Start and Stop delegate to the same service method (functionally identical)
- All 5 entity mappers still injected in OrchestrationService ctor for v2 readiness (confirms Q2.1 modification)

**SPEC.md amendment:** Requirements 3 + 4 + one Constraints bullet + one Out-of-scope bullet + 2 Acceptance Criteria checkboxes rewritten to reflect 204 No Content. Original lock was `200 OK + List<WorkflowReadDto>`. Amendment dated 2026-05-28; rationale documented in SPEC.md Goal section.

---

## Claude's Discretion

- Whether to use `[Theory]`-parameterization for the Start/Stop endpoint pair vs. duplicating facts across two files
- The naming of facts (existing Phase 4-7 convention: `Verb_Behavior_Condition`)
- Whether to land controller / service / validator as `internal sealed` vs. `public sealed` — match closest existing precedent
- Whether to add a per-class Phase9WebAppFactory subclass if a Phase 9-specific service override is needed (none currently anticipated)

## Deferred Ideas

- Real orchestration side-effects (queueing jobs, dispatching to schedulers) — future phase
- Per-id status response envelope for v2 (e.g., `[{ workflowId, status, jobId }]`) — future phase
- Diverging Start vs Stop behavior — future phase
- `MaximumCount(N)` validator cap — outside SPEC.md v1; defer until production traffic patterns inform a limit
- Typed request records `StartOrchestrationRequest` / `StopOrchestrationRequest` — refactor if Start and Stop diverge later
- `OrchestrationReadDto` — explicitly out of scope per SPEC.md amendment; design when real response shape is needed
- Promoting `GetBySourceHash` to `BaseController<>` via an `IHasSourceHash` marker interface — overkill for one endpoint on one entity
