# Phase 7: Generic HTTP Base + Composition Root - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-27
**Phase:** 07-generic-http-base-composition-root
**Areas discussed:** BaseController HTTP semantics, BaseService contract + error signaling, SyncJunctionsAsync hook design, AddBaseApi composition root structure, API versioning shape, Swagger config, UseBaseApi pipeline details

---

## Gray Area Selection

| Option | Description | Selected |
|--------|-------------|----------|
| BaseController HTTP semantics | Status codes, response shapes per verb, [ApiController]+ModelState vs FluentValidation interaction | ✓ |
| BaseService contract + error signaling | CT propagation, return shapes, NotFound signaling, concurrency conflict | ✓ |
| SyncJunctionsAsync hook design | Signature, default body, call site, SC#2 verification | ✓ |
| AddBaseApi composition root structure | Single mega vs sub-extension chain, DbContext sourcing | ✓ |

Follow-up areas (offered after first 4 complete):

| Option | Description | Selected |
|--------|-------------|----------|
| API versioning shape (Asp.Versioning.Http) | URL-prefix-only vs ApiVersion attribute, v1-only vs forward-compatible | ✓ |
| Swagger config | XML docs, security definitions, correlation header, /health filter | ✓ |
| UseBaseApi pipeline details | CORS yes/no, Swagger UI placement, health endpoint mapping | ✓ |
| Ready for context (no more areas) | — | ✓ |

---

## BaseController HTTP semantics

### POST response shape

| Option | Description | Selected |
|--------|-------------|----------|
| 201 Created + Location + ToRead body | REST-idiomatic; Phase 4 D-04 symmetric | ✓ |
| 201 Created + ToRead body only (no Location) | Skip Location to decouple from URL pattern | |
| 200 OK + ToRead body | Simpler — no Location, no 201 semantics | |

**User's choice:** 201 Created + Location + ToRead body
**Notes:** Recommended option; symmetric error path via Phase 4 ProblemDetails customizer.

### PUT response shape

| Option | Description | Selected |
|--------|-------------|----------|
| 200 OK + ToRead body | Canonical post-update state without follow-up GET | ✓ |
| 204 No Content | Bare success | |
| 200 OK + ToRead body + ETag/version header | xmin as HTTP-layer ETag | |

**User's choice:** 200 OK + ToRead body
**Notes:** ETag/If-Match deferred (no current REQ-ID).

### DELETE response shape

| Option | Description | Selected |
|--------|-------------|----------|
| 204 No Content | Missing id → NotFoundException → 404 (Phase 4 handler) | ✓ |
| 200 OK + deleted entity (ToRead body) | Audit-trail flavor | |
| 200 OK with { id, deletedAt } envelope | Custom shape | |

**User's choice:** 204 No Content

### GET list response shape

| Option | Description | Selected |
|--------|-------------|----------|
| Bare List<TRead> | Phase 8 may layer Paged when HTTP-04..06 land | ✓ |
| Pre-emptive Paged<TRead> envelope | Locks the contract earlier | |
| ActionResult<IAsyncEnumerable<TRead>> | Streaming JSON; complicates Swagger | |

**User's choice:** Bare List<TRead>
**Notes:** Phase 8 may break this contract for HTTP-04..06.

---

## BaseService contract + error signaling

### CancellationToken propagation

| Option | Description | Selected |
|--------|-------------|----------|
| Every public method takes CT, default from HttpContext.RequestAborted | Validator, repo, SaveChanges, SyncJunctionsAsync all get CT | ✓ |
| Same + explicit CT on SyncJunctionsAsync virtual | (Already covered by option 1) | |
| Default CTs to CancellationToken.None at service layer | Honor CT only at repo edge | |

**User's choice:** Every public method takes CT
**Notes:** xUnit v3 + TestContext.Current.CancellationToken pattern continues unchanged from Phase 3.

### NotFound signaling

| Option | Description | Selected |
|--------|-------------|----------|
| Throw NotFoundException → Phase 4 handler returns 404 | Symmetric with Validation/DbUpdate flow | ✓ |
| Return TRead? (null) — controller branches | Forces branch in every concrete controller | |
| Result<TRead, NotFoundError> envelope | New type system codebase doesn't use | |

**User's choice:** Throw NotFoundException
**Notes:** NotFoundException type already exists from Phase 4 ERROR-01.

### Validation timing

| Option | Description | Selected |
|--------|-------------|----------|
| Inside BaseService, first step, throw ValidationException | VALID-03 honored; Phase 4 ValidationExceptionHandler maps 400 | ✓ |
| Inside BaseService + explicit [ApiController] ModelState 400 carveout | (Same as option 1 — both paths land via IProblemDetailsService per ERROR-10) | |
| Controller calls validator before invoking service | Forces validation noise into every controller | |

**User's choice:** Inside BaseService, first step
**Notes:** [ApiController] ModelState 400 fires only for [FromBody] deserialization failures; validator failures never reach ModelState.

### Concurrency conflict handling

| Option | Description | Selected |
|--------|-------------|----------|
| Let DbUpdateConcurrencyException bubble to Phase 4 DbUpdateExceptionHandler | Already-proven 409 Conflict ProblemDetails path | ✓ |
| Catch in BaseService, rethrow as DomainConcurrencyException | Adds layers without functional gain | |
| Retry once with reload-then-reapply | Hides conflicts; contradicts xmin purpose | |

**User's choice:** Let it bubble
**Notes:** Pitfall 7 — concurrency check FIRST in Phase 4 handler, then constraint mapping. 31 existing Phase 4 facts cover the behavior.

---

## SyncJunctionsAsync hook design

### Signature

| Option | Description | Selected |
|--------|-------------|----------|
| `protected virtual Task SyncJunctionsAsync(TEntity, TCreate?, TUpdate?, CT)` | One virtual; non-null DTO indicates the path | ✓ |
| Two virtuals: SyncJunctionsOnCreateAsync + SyncJunctionsOnUpdateAsync | Per-path explicit methods; double surface area | |
| Single virtual takes IBaseDto + operation enum | Loses compile-time DTO type info | |

**User's choice:** Single virtual with TCreate? + TUpdate? + CT
**Notes:** Phase 8 overrides switch on whichever DTO is non-null.

### Default implementation

| Option | Description | Selected |
|--------|-------------|----------|
| No-op returning Task.CompletedTask | 4 of 5 v1 entities inherit no-op; Step+Workflow override | ✓ |
| Abstract — every concrete service MUST implement | Forces boilerplate on 3 of 5 entities | |
| Throw NotImplementedException by default | Contradicts "one inheritance step away from working" | |

**User's choice:** No-op (Task.CompletedTask)

### Call site / timing

| Option | Description | Selected |
|--------|-------------|----------|
| After repo.Add, before SaveChanges — entity attached but not persisted | Matches ROADMAP SC#2 verbatim; atomic transaction | ✓ |
| Before repo.Add | Junctions reference unattached entity — EF won't track relationships | |
| After SaveChanges — two-transaction model | Non-atomic; orphan risk | |

**User's choice:** After repo.Add, before SaveChanges
**Notes:** Guid Id assigned client-side (Phase 3 BaseEntity) makes this tracker state usable in override.

### SC#2 verification approach

| Option | Description | Selected |
|--------|-------------|----------|
| TestService overrides SyncJunctionsAsync with recording flag + ChangeTracker assertions | Zero real DB churn; uses Phase 6 TestEntity | ✓ |
| Real Postgres integration test with xmin advancement + audit timestamps | Heavier setup; Phase 8 already does this | |
| Source-generator / DispatchProxy interceptor recording call order | Most thorough but new mechanism just for Phase 7 | |

**User's choice:** TestService recording override

---

## AddBaseApi composition root structure

### Composition shape

| Option | Description | Selected |
|--------|-------------|----------|
| Chain of internal sub-extensions | AddBaseApiPersistence, Observability, Health, ErrorHandling, Http, Validation (already shipped), Mapping (already shipped) | ✓ |
| One giant method body inline | Phase 6's shipped extensions become orphaned | |
| Public sub-extensions called in order | Adds public surface area without REQ | |

**User's choice:** Chain of internal sub-extensions
**Notes:** Phase 6 AddBaseApiValidation/Mapping remain public (already shipped that way) but get absorbed into AddBaseApi.

### DbContext + connection string sourcing

| Option | Description | Selected |
|--------|-------------|----------|
| AddBaseApi reads cfg.GetConnectionString("Postgres") + sets convention + interceptors inline | All composition in BaseApi.Core; Phase 8 AppDbContext minimal | ✓ |
| AddBaseApi takes optional configurator lambda | YAGNI for single-app | |
| Naming/interceptors stay in Phase 3 AppDbContext.OnConfiguring | Splits composition between Core and Service | |

**User's choice:** Inline inside AddBaseApiPersistence
**Notes:** Matches HTTP-13 verbatim.

### Migration of existing Phase 4/5 Program.cs wiring

| Option | Description | Selected |
|--------|-------------|----------|
| AddBaseApi absorbs Phase 4+5+6 wiring; Program.cs shrinks to ~3 lines; 47 existing facts replay | Strong regression discipline | ✓ |
| AddBaseApi additive; old Program.cs lines stay as no-ops | Bloats Program.cs; contradicts SC#3 | |
| Big-bang rewrite then fix facts after | Higher regression risk | |

**User's choice:** Absorb + replay all 47 facts

### Test seam adaptation

| Option | Description | Selected |
|--------|-------------|----------|
| WebAppFactory stays same shape; subclasses override via ConfigureTestServices | Strong test/prod parity | ✓ |
| Introduce TestWebAppFactory that skips AddBaseApi | Drift risk; loses real-composition coverage | |
| Add AddBaseApi(opts => opts.SkipObservability = true) toggle | Drift risk between test and prod | |

**User's choice:** WebAppFactory unchanged + ConfigureTestServices overrides

---

## API versioning shape

| Option | Description | Selected |
|--------|-------------|----------|
| URL-segment versioning + [Route("api/v{version:apiVersion}/[controller]")] on BaseController + [ApiVersion("1.0")] + ReportApiVersions=true + ApiExplorer integration | One-attribute change to add v2; Swagger versioned routes render correctly | ✓ |
| Hardcoded [Route("api/v1/[controller]")] literal, no Asp.Versioning | Violates HTTP-15 | |
| URL-segment versioning + [ApiVersion] on each concrete controller | More boilerplate; loses inherit-and-go | |

**User's choice:** URL-segment versioning on BaseController with templated route

---

## Swagger / OpenAPI configuration

| Option | Description | Selected |
|--------|-------------|----------|
| Minimal: per-version SwaggerDoc + correlationId header OperationFilter + /health/* DocumentFilter; no XML docs, no security definitions | Practical v1 config; XML docs blocked by TreatWarningsAsErrors+CS1591 | ✓ |
| Minimal + XML doc inclusion enabled | CS1591 burden across every public type | |
| Minimal + auth Bearer scheme placeholder | Documents non-existent capability | |

**User's choice:** Minimal config

---

## UseBaseApi middleware pipeline

| Option | Description | Selected |
|--------|-------------|----------|
| Omit CORS in v1; Swagger UI inside IsDevelopment() after UseRouting; health endpoints via MapHealthChecks per probe with tag predicates | HTTP-14 verbal order honored; CORS deferred (no REQ) | ✓ |
| Same + CORS AllowAnyOrigin for local dev | Introduces auth/sec surface | |
| Same + Swagger UI in non-Dev with config flag | Contradicts HTTP-16 wording | |

**User's choice:** Omit CORS; Swagger Dev-only; tagged health endpoints

---

## Claude's Discretion

- BaseService constructor parameter order + nullability of optional IValidator<T>
- ProducesResponseType attribute decoration on BaseController actions for Swagger schema clarity
- Sealed vs non-sealed on BaseController/BaseService (must be abstract per HTTP-02)
- Sub-extension method visibility + file layout
- Mocking library choice for SC#2 ordering proof (Moq / NSubstitute / hand-rolled recorder)

## Deferred Ideas

- ETag / If-Match optimistic concurrency over HTTP (no REQ-ID in v1)
- XML doc inclusion in Swagger (CS1591 + TreatWarningsAsErrors burden)
- CORS configuration (no REQ-ID; pipeline slot reserved per HTTP-14 but policy undefined)
- Bearer JWT auth + Security Definitions in Swagger
- Paged<TRead> envelope for GET list (Phase 8 HTTP-04..06 may revisit D-04)
- Result<T, TError> envelope pattern (rejected in D-06 in favor of exception-based signaling)
- DispatchProxy / Castle interceptor for cross-cutting ordering audits
