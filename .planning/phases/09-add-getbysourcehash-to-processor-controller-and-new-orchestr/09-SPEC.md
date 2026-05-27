# Phase 9: Processor.GetBySourceHash + Orchestration Start/Stop — Specification

**Created:** 2026-05-28
**Ambiguity score:** 0.11 (gate: ≤ 0.20)
**Requirements:** 6 locked

## Goal

Add one new endpoint to `ProcessorsController` (`GetBySourceHash`) and one new `OrchestrationController` with two endpoints (`StartOrchestration`, `StopOrchestration`) — all three following the existing `BaseController.GetById` response semantics (200 on hit, 404 on miss via `NotFoundException`). The Orchestration endpoints accept `List<Guid> WorkflowIds` and, for v1, return the matching `WorkflowReadDto` list without any actual orchestration side-effects.

## Background

**What exists today (verified in codebase):**
- `ProcessorsController` (`src/BaseApi.Service/Features/Processor/ProcessorController.cs:19`) is an empty body inheriting `BaseController<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto>`. It exposes 5 CRUD verbs — no `GetBySourceHash`.
- `ProcessorEntity.SourceHash` (`src/BaseApi.Service/Features/Processor/ProcessorEntity.cs:25`) is a lowercase 64-char SHA-256 hex string with unique index `uq_processor_source_hash`.
- The 5 existing feature folders (`Schema`, `Processor`, `Step`, `Assignment`, `Workflow`) share a stable layout: `Entity + Dtos + Validator + Mapper + Service + Controller + ServiceCollectionExtensions`.
- No `Orchestration/` feature folder exists.
- `GetById` design (`src/BaseApi.Core/Controllers/BaseController.cs:50-55`): `[HttpGet("{id:guid}")] → Ok(_service.GetByIdAsync) → 404 via NotFoundException` handled by Phase 4 `NotFoundExceptionHandler`.
- Phase 4 exception-handler pipeline: `NotFoundException → 404 ProblemDetails`; `ValidationException → 400 ValidationProblemDetails`.

**What this phase delivers:** one extra method on `ProcessorsController` plus a new `Orchestration/` feature folder mirroring the existing folder layout (no entity, no migration). The Orchestration controller is a composition over `WorkflowService`; future endpoints will be added to this folder once orchestration semantics are designed.

## Requirements

1. **Processor.GetBySourceHash endpoint**: New endpoint on `ProcessorsController` that returns a single `ProcessorReadDto` by its `SourceHash`.
   - Current: No such endpoint exists. Looking up a Processor by hash requires a full-list scan client-side.
   - Target: `GET /api/v1/processors/by-source-hash/{sourceHash}` returns `200 OK + ProcessorReadDto` when a row matches; returns `404 ProblemDetails` via `NotFoundException` when no row matches (including off-format strings — no route-level validation in v1). Implemented as a direct method on `ProcessorsController` (concrete, NOT on `BaseController<>`); calls a new `ProcessorService.GetBySourceHashAsync(string sourceHash, CancellationToken)` method.
   - Acceptance: Integration test asserts 200 + correct `ProcessorReadDto` for a seeded row; integration test asserts 404 for a non-existent hash and for a malformed (non-hex / wrong-length) hash.

2. **OrchestrationController exists**: New `OrchestrationController` lives in a new `Features/Orchestration/` feature folder mirroring the existing folder layout.
   - Current: No `OrchestrationController` / `Features/Orchestration/` folder exists.
   - Target: `src/BaseApi.Service/Features/Orchestration/` contains `OrchestrationController.cs`, `OrchestrationService.cs`, `OrchestrationServiceCollectionExtensions.cs` (with `AddOrchestrationFeature()`). The `OrchestrationService` composes over `WorkflowService` (or the abstract `BaseService<WorkflowEntity, …>`) — no entity, no DTOs of its own (request is bare `List<Guid>`, response is `List<WorkflowReadDto>` from the existing Workflow feature). `AddOrchestrationFeature()` is wired into `Program.cs` alongside the other `AddXxxFeature()` calls.
   - Acceptance: `dotnet build` is zero-warning. `Program.cs` calls `AddOrchestrationFeature()`. Folder layout matches the established Phase 8 feature-folder pattern.

3. **StartOrchestration endpoint**: New endpoint on `OrchestrationController` accepting `List<Guid> WorkflowIds` and returning the corresponding Workflows.
   - Current: No such endpoint exists.
   - Target: `POST /api/v1/orchestration/start` with request body = bare JSON array of guids (e.g., `["g1","g2"]`); returns `200 OK + List<WorkflowReadDto>` on success. **No actual orchestration side-effects in v1** — endpoint only fetches the Workflow entities by id.
   - Acceptance: Integration test asserts 200 + correct `List<WorkflowReadDto>` array (order preserved or stably defined) for a body of valid, existing, distinct guids.

4. **StopOrchestration endpoint**: New endpoint on `OrchestrationController`, behaviorally identical to `StartOrchestration` in v1, only the route segment differs.
   - Current: No such endpoint exists.
   - Target: `POST /api/v1/orchestration/stop` — same body shape, same response shape, same validation, same error semantics as Start. The only difference is the URL segment `/stop`. Both endpoints delegate to the same `OrchestrationService` method in v1; future divergence is out of scope for Phase 9.
   - Acceptance: Integration test mirrors the Start test: 200 + correct `List<WorkflowReadDto>` for a valid body.

5. **Duplicate-id validation on Orchestration endpoints**: A `List<Guid>` body containing duplicate guids is rejected.
   - Current: No such endpoints exist, so no validation.
   - Target: Both `StartOrchestration` and `StopOrchestration` validate that the request body contains no duplicate guids. On duplicate, throw `FluentValidation.ValidationException` — the Phase 4 `ValidationExceptionHandler` maps it to `400 ValidationProblemDetails`. Same code path as Phase 6 / Phase 8 validation failures.
   - Acceptance: Integration test asserts `400 ValidationProblemDetails` when the body is `["g1","g1","g2"]`. Response shape matches existing Phase 4 validation-failure shape (no new error contract).

6. **Existence validation on Orchestration endpoints**: A `List<Guid>` body where any guid does not match an existing Workflow is rejected.
   - Current: No such endpoints exist, so no validation.
   - Target: Both endpoints check that EVERY guid in the body resolves to an existing Workflow row (all-or-nothing semantic mirroring `GetById`). On any miss, throw `NotFoundException` — the Phase 4 `NotFoundExceptionHandler` maps it to `404 ProblemDetails`. Same code path as `BaseService.GetByIdAsync` miss.
   - Acceptance: Integration test asserts `404 ProblemDetails` when the body contains a guid that does not match any Workflow row; response identifies the missing id(s) in `detail` (same shape as existing `NotFoundException` payload).

## Boundaries

**In scope:**
- One new method on `ProcessorsController` (`GetBySourceHash`)
- One new `ProcessorService.GetBySourceHashAsync` method (delegates to the repository / DbContext for a `WHERE source_hash = …` lookup)
- New `Features/Orchestration/` folder with `OrchestrationController + OrchestrationService + OrchestrationServiceCollectionExtensions`
- Two new endpoints on `OrchestrationController` (`StartOrchestration`, `StopOrchestration`), behaviorally identical in v1
- `Program.cs` updated to call `AddOrchestrationFeature()`
- Duplicate-id and existence validation via existing Phase 4/6 exception-handler pipeline (no new error shapes)
- Integration tests in `tests/BaseApi.Tests/Features/Processor/GetBySourceHashFacts.cs` and `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs` + `StopOrchestrationFacts.cs` covering hit, miss, duplicate, and missing-id paths

**Out of scope:**
- **Actual orchestration side-effects** (queueing jobs, starting/stopping runners, dispatching to external schedulers) — v1 endpoints only fetch Workflow entities by id; real orchestration is a future phase
- **New domain entities, DB tables, or EF migrations** — Orchestration is purely a controller+service composition over the existing Workflow feature; zero schema changes
- **Auth / authorization on the new endpoints** — endpoints remain anonymous in v1, matching the Phase 8 controller convention; auth is a separate concern
- **Persisting Start/Stop audit history** (no `orchestration_actions` table, no audit log records of who-started-what) — future concern
- **Idempotency keys / retry semantics** — no `Idempotency-Key` header support; v1 has no side-effect to deduplicate
- **Route-layer validation of sourceHash format** (no `:regex(...)` constraint, no request-DTO + FluentValidation) — off-format strings simply 404 on row-miss
- **A request DTO around the Orchestration `List<Guid>` body** — body is the bare list; no envelope object in v1
- **Diverging Start vs Stop behavior in v1** — they are functionally identical (same body, same validation, same response, same delegation); only the URL differs
- **A new `OrchestrationReadDto`** — response is `List<WorkflowReadDto>` from the existing Workflow feature

## Constraints

- `GetBySourceHash` must live on the **concrete** `ProcessorsController`, not on `BaseController<>` — `SourceHash` is processor-specific (not on `BaseEntity`).
- The route literal for `GetBySourceHash` must be `by-source-hash/{sourceHash}` (literal prefix) — a bare `{sourceHash}` would conflict with `BaseController.GetById`'s `{id:guid}` at route matching.
- All error responses MUST use the existing Phase 4 exception-handler pipeline (`NotFoundException → 404 ProblemDetails`, `ValidationException → 400 ValidationProblemDetails`). No new error shapes, no new exception types.
- Orchestration response body MUST be `List<WorkflowReadDto>` (existing type) — no new response DTO.
- Orchestration request body MUST be a bare JSON array of guids (`List<Guid>`) — no envelope DTO.
- Zero-warning `dotnet build` (Debug + Release) per project-wide `TreatWarningsAsErrors=true`.
- Existing Phase 1–8 test suite (128 facts) MUST remain GREEN — no regressions.

## Acceptance Criteria

- [ ] `GET /api/v1/processors/by-source-hash/{sourceHash}` returns `200 OK + ProcessorReadDto` for an existing row
- [ ] `GET /api/v1/processors/by-source-hash/{sourceHash}` returns `404 ProblemDetails` for a non-existent or malformed hash
- [ ] `POST /api/v1/orchestration/start` with a valid `List<Guid>` body returns `200 OK + List<WorkflowReadDto>`
- [ ] `POST /api/v1/orchestration/stop` with a valid `List<Guid>` body returns `200 OK + List<WorkflowReadDto>` (same shape as Start)
- [ ] `POST /api/v1/orchestration/start` (and `/stop`) with duplicate guids in the body returns `400 ValidationProblemDetails` via the Phase 4 `ValidationExceptionHandler`
- [ ] `POST /api/v1/orchestration/start` (and `/stop`) with any guid not matching a Workflow row returns `404 ProblemDetails` via the Phase 4 `NotFoundExceptionHandler`, identifying the missing id(s)
- [ ] `src/BaseApi.Service/Features/Orchestration/` contains `OrchestrationController.cs + OrchestrationService.cs + OrchestrationServiceCollectionExtensions.cs` (mirrors existing feature-folder layout)
- [ ] `Program.cs` calls `AddOrchestrationFeature()`
- [ ] No new DB tables, no new EF migration, no new entity type
- [ ] `dotnet build` is zero-warning in Debug + Release
- [ ] Integration tests pass for all 6 acceptance scenarios above (GetBySourceHash hit/miss; Orchestration Start happy/dup/miss; Orchestration Stop happy)
- [ ] Phase 1–8 full regression suite remains GREEN (128 facts pass)

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                    |
|--------------------|-------|------|--------|----------------------------------------------------------|
| Goal Clarity       | 0.92  | 0.75 | ✓      | Three endpoints, exact routes/verbs/payloads/responses   |
| Boundary Clarity   | 0.92  | 0.70 | ✓      | 9-item explicit out-of-scope list                        |
| Constraint Clarity | 0.85  | 0.65 | ✓      | All error semantics tied to existing Phase 4 pipeline    |
| Acceptance Criteria| 0.85  | 0.70 | ✓      | 11 pass/fail checkboxes                                  |
| **Ambiguity**      | 0.11  | ≤0.20| ✓      | Gate passed in 3 rounds                                  |

## Interview Log

| Round | Perspective      | Question summary                                              | Decision locked                                                                                          |
|-------|------------------|---------------------------------------------------------------|----------------------------------------------------------------------------------------------------------|
| 1     | Researcher       | Route shape for `GetBySourceHash`                             | `GET /api/v1/processors/by-source-hash/{sourceHash}` on concrete `ProcessorsController`                  |
| 1     | Researcher       | Verb + payload shape for Orchestration Start/Stop             | `POST` with bare `List<Guid>` JSON-array body                                                            |
| 1     | Researcher       | Missing-id behavior on Orchestration                          | All-or-nothing 404 (via `NotFoundException`) — strictest mirror of `GetById`                             |
| 2     | Simplifier       | Orchestration feature folder scope                            | Full feature folder (Controller + Service + ServiceCollectionExtensions) — design-mirror for future endpoints |
| 2     | Simplifier       | Response DTO + validation rules for Orchestration             | No new DTOs (response = `List<WorkflowReadDto>`); validate duplicates → 400, existence → 404; both reuse Phase 4 pipeline |
| 2     | Boundary Keeper  | `sourceHash` validation                                       | No validation — plain string param; off-format strings 404 via row-miss                                  |
| 3     | Failure Analyst  | Test coverage expectation                                     | Full integration tests like Phase 8 — hit + miss for `GetBySourceHash`; happy + dup + missing-id for Orchestration Start; happy for Stop |
| 3     | Boundary Keeper  | Start vs Stop functional divergence in v1                     | Identical in v1 — only the route segment differs                                                          |
| 3     | Boundary Keeper  | Explicit out-of-scope items                                   | Side-effects, new entities/migrations, auth, audit history, idempotency — all explicitly excluded         |

---

*Phase: 09-add-getbysourcehash-to-processor-controller-and-new-orchestr*
*Spec created: 2026-05-28*
*Next step: /gsd-discuss-phase 9 — implementation decisions (e.g., where the existence check lives, whether `OrchestrationService` takes `WorkflowService` or `BaseService<WorkflowEntity,…>`, response ordering guarantee, test fixture layout)*
