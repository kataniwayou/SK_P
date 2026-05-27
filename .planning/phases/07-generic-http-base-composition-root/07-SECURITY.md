# Phase 07 Security Audit — Generic HTTP Base + Composition Root

**Phase:** 07-generic-http-base-composition-root
**Plans audited:** 07-01 (production code) + 07-02 (verification battery)
**Audit date:** 2026-05-27
**ASVS level:** L1 (default)
**Block-on policy:** high (default)
**Result:** SECURED — 10/10 mitigations verified; 4 accepted + 1 transferred dispositions documented

---

## Threat Verification Matrix

### Plan 07-01 STRIDE register (10 threats)

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-07-01 | Information Disclosure (Swagger UI exposed in Production) | mitigate | CLOSED | `src/BaseApi.Core/DependencyInjection/BaseApiApplicationBuilderExtensions.cs:32-44` — `if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(...); }` gates both calls. Production returns 404. |
| T-07-02 | Tampering / EoP (mass assignment via PUT) | mitigate | CLOSED | `src/BaseApi.Core/Services/BaseService.cs:106-115` — `UpdateAsync` calls only `_mapper.Update(dto, entity)`; never writes Id, CreatedAt, CreatedBy directly. Phase 6 D-08 dual-mechanism (UpdateDto exclusion source-side + `[MapperIgnoreTarget]` target-side) preserved. |
| T-07-03 | Information Disclosure (500 ProblemDetails leaks TEntity / stack) | mitigate | CLOSED | `src/BaseApi.Core/Exceptions/Handlers/FallbackExceptionHandler.cs:38-48` — `_logger.LogError(exception, ...)` writes stack to MEL only; ProblemDetails body contains only `Title="Internal Server Error"`, `Detail="An unexpected error occurred."`, `Status=500`. Exception type/message/stack NEVER serialized to body. |
| T-07-04 | Information Disclosure (Asp.Versioning 400 lacks correlationId/instance) | mitigate | CLOSED | `src/BaseApi.Core/DependencyInjection/ErrorHandlingServiceCollectionExtensions.cs:16-27` — `services.AddProblemDetails(options => { options.CustomizeProblemDetails = ctx => { ...Extensions["correlationId"] = corrId; ctx.ProblemDetails.Instance = ctx.HttpContext.Request.Path; }; })` registers the customizer extending `IProblemDetailsService`. Note: Plan 07-02 falsified RESEARCH A6 — Asp.Versioning URL-segment returns 404 route-no-match (not 400), but the customizer enriches all ProblemDetails emissions and correlationId echo is verified through the X-Correlation-Id response header by VersioningFacts. |
| T-07-05 | Tampering / Log Injection (CRLF in X-Correlation-Id) | mitigate | CLOSED | `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs:97-107` — `IsValid()` rejects null/empty, length > 128, and any byte outside ASCII-printable range 0x20-0x7E (rejects CR/LF/null/control). Invalid inbound headers trigger `Guid.NewGuid().ToString("N")` fallback (line 94). Companion `src/BaseApi.Core/Swagger/CorrelationIdHeaderOperationFilter.cs:24` declares `MaxLength=128` on the OpenAPI parameter schema. |
| T-07-06 | Tampering / supply chain (DI reflection scan) | mitigate | CLOSED | `src/BaseApi.Core/DependencyInjection/MappingServiceCollectionExtensions.cs:40` — `assembly.GetExportedTypes()` (public only). `src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs:33-36` — FluentValidation's `AddValidatorsFromAssembly` with `includeInternalTypes: false`. Scan scope: `typeof(TDbContext).Assembly` per caller in `BaseApi.Service/Program.cs:6` (production = BaseApi.Service.dll). |
| T-07-07 | Denial of Service (unbounded GET list) | accept | CLOSED | Disposition: accept per PLAN.md — out of scope per REQUIREMENTS.md "v2 HTTP-17 Pagination". v1 returns full table; documented for v2 hardening. |
| T-07-08 | Spoofing (no auth boundary) | transfer | CLOSED | Disposition: transfer per PLAN.md — out of scope per PROJECT.md "Out of Scope: Authentication / authorization". `CreatedBy`/`UpdatedBy` remain null without `HttpContext.User.Identity.Name` (Phase 3 PERSIST-04). |
| T-07-09 | Information Disclosure (ApiController 400 leaks C# property paths) | mitigate | CLOSED | `src/BaseApi.Core/DependencyInjection/ErrorHandlingServiceCollectionExtensions.cs:16-27` — Phase 4 customizer registered. FluentValidation drives validation (Phase 6 / VALID-03 explicit in `BaseService.cs:97`), errors use DTO JSON property names not C# member names. ValidationExceptionHandler in the chain (line 31) normalizes the errors map. |
| T-07-10 | Tampering (URL-segment version manipulation) | mitigate | CLOSED | `src/BaseApi.Core/DependencyInjection/HttpServiceCollectionExtensions.cs:23-35` — `AddApiVersioning` sets `DefaultApiVersion = new ApiVersion(1, 0)` and `AssumeDefaultVersionWhenUnspecified = true`; URL-segment reader default with `[Route("api/v{version:apiVersion}/[controller]")]`. `src/BaseApi.Core/Controllers/BaseController.cs:22` — exactly ONE `[Route]` attribute (verified via grep — Pitfall 1 avoided). `CreatedAtAction(nameof(GetById), ...)` at `BaseController.cs:69` returns versioned Location header. |

### Plan 07-02 STRIDE register (5 threats)

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-07-V01 | Information Disclosure (test connection string in logs) | accept | CLOSED | Disposition: accept per PLAN.md — Phase 2 D-04 lab-only Postgres at localhost:5433 with postgres:postgres credentials; correlation IDs are 32-char hex with no PII. |
| T-07-V02 | Tampering (NSubstitute mocks mask real bugs) | mitigate | CLOSED | Unit-level proof: `tests/BaseApi.Tests/Services/BaseServiceOrderingFacts.cs` (mocked collaborators + REAL Phase7TestDbContext for ChangeTracker.State assertion). Integration proof: `tests/BaseApi.Tests/Controllers/BaseControllerRoutesFacts.cs`, `tests/BaseApi.Tests/Composition/UseBaseApiPipelineFacts.cs`, `tests/BaseApi.Tests/Composition/AddBaseApiFacts.cs`, `tests/BaseApi.Tests/Services/NotFoundFacts.cs`, `tests/BaseApi.Tests/Swagger/SwaggerEnvironmentFacts.cs`, `tests/BaseApi.Tests/Versioning/VersioningFacts.cs` all boot full composition via Phase7WebAppFactory. Both shapes present (RESEARCH Pattern 2). |
| T-07-V03 | Information Disclosure (psql snapshots) | accept | CLOSED | Disposition: accept per PLAN.md — snapshots are `\l` only (database list), 4 baseline DB names, captured to `$env:TEMP` (process-private). |
| T-07-V04 | Denial of Service (WebAppFactory + Postgres DB resource exhaustion) | mitigate | CLOSED | `tests/BaseApi.Tests/Composition/Phase7WebAppFactory.cs:49,69-82` — implements `IAsyncLifetime`; `DisposeAsync` calls `base.DisposeAsync()` + `NpgsqlConnection.ClearPool(conn)` (per-factory pool, not process-global) + `_fixture.DisposeAsync()`. `tests/BaseApi.Tests/Services/BaseServiceOrderingFacts.cs:48-52` — `DisposeAsync` disposes `_dbContext` then `_fixture`. PostgresFixture (Phase 3) is IAsyncLifetime per inherited contract. 07-02-SUMMARY confirms byte-identical BEFORE/AFTER psql `\l` snapshots (zero stepsdb_test_* leaks). |
| T-07-V05 | Information Disclosure (Swagger UI manual smoke) | accept | CLOSED | Disposition: accept per PLAN.md — local-only Kestrel port 5000; out of scope per PROJECT.md (no auth in v1). |

---

## Summary

| Total threats | Mitigated (verified) | Accepted | Transferred | Open |
|--------------:|--------------------:|---------:|------------:|-----:|
| 15            | 10                  | 4        | 1           | 0    |

**ASVS L1 verification scope satisfied:**
- V5 Input validation: FluentValidation explicit (BaseService.cs:97) + ASCII guard on correlation header (CorrelationIdMiddleware.cs:97-107)
- V7 Error handling: 4 IExceptionHandler chain in load-bearing order; FallbackExceptionHandler never leaks stack to body
- V8 Data protection: mass assignment defense-in-depth via UpdateDto exclusion + Mapperly `[MapperIgnoreTarget]`
- V11 Business logic: locked 6-step CreateAsync order verified by BaseServiceOrderingFacts unit test + integration tests
- V13 API: URL-segment versioning bound to ApiVersion(1,0) only; one [Route] per BaseController; Swagger dev-only

## Unregistered Threat Flags

SUMMARY.md `## Threat Flags` sections in 07-01-SUMMARY.md and 07-02-SUMMARY.md: NONE present (no new attack surface flagged by executor during implementation beyond the 15 PLAN-declared threats). All threat-model coverage explicitly enumerated in 07-02-SUMMARY.md Self-Check section confirms full T-07-V01..V05 coverage.

## Accepted Risks Log

| Risk | Disposition | Rationale | Owner | Re-evaluate |
|------|-------------|-----------|-------|-------------|
| T-07-07 Unbounded GET list | accept | v2 HTTP-17 Pagination scheduled | product | v2 hardening |
| T-07-V01 Test connection string in lab logs | accept | localhost:5433 + postgres:postgres lab-only (Phase 2 D-04) | platform | v2 / production deployment |
| T-07-V03 psql `\l` snapshots | accept | DB list only, no table-level detail, $TEMP-private | platform | v2 |
| T-07-V05 Local Swagger UI on Kestrel:5000 | accept | local dev only, no auth boundary in v1 | platform | v2 hardening |

## Transferred Risks Log

| Risk | Transfer Target | Rationale |
|------|-----------------|-----------|
| T-07-08 No auth boundary | v2 Hardening (PROJECT.md Out of Scope) | Authentication/authorization explicitly out-of-scope per PROJECT.md and REQUIREMENTS.md v2 Hardening track |

---

*Phase: 07-generic-http-base-composition-root*
*Audited: 2026-05-27*
*Verifier: gsd-security-auditor*
