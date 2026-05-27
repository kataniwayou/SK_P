---
phase: 7
slug: generic-http-base-composition-root
status: complete
nyquist_compliant: true
wave_0_complete: true
created: 2026-05-27
last_updated: 2026-05-27
last_audit: 2026-05-27
---

# Phase 7 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (3.2.2) under Microsoft.Testing.Platform |
| **Config file** | tests/BaseApi.Tests/BaseApi.Tests.csproj |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~<tag>"` |
| **Full suite command** | `dotnet test SK_P.sln --no-restore` |
| **Estimated runtime** | ~20-30 seconds full suite (76 prior + ~16-18 new Phase 7 facts; Phase 5 OTel warmup ~17-18s baseline) |

---

## Sampling Rate

- **After every task commit:** Run quick (filtered) test suite for that area (per Phase 3 / 6 cadence inherited)
- **After every plan wave:** Run full suite (`dotnet test SK_P.sln`)
- **Before `/gsd-verify-work`:** Full suite must be green × 3 consecutive runs (Phase 3 D-18 cadence inherited; matches Phase 6 Plan 06-02 76/76 GREEN cadence)
- **Max feedback latency:** ~60 seconds for full suite

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 07-02-02 | 07-02 | 2 | HTTP-01 | — | ASP.NET Core controllers in use (not Minimal APIs) — verified by typeof(TestsController) inheritance from BaseController<,,,> resolving as ControllerActionDescriptor | integration | `dotnet test --filter "FullyQualifiedName~BaseControllerRoutesFacts"` | ✅ | ✅ green |
| 07-02-02 | 07-02 | 2 | HTTP-02 | — | Abstract generic BaseController exposes 5 verbs decorated with [ApiController]+[ApiVersion(\"1.0\")]+[Route(\"api/v{version:apiVersion}/[controller]\")] | integration | `dotnet test --filter "FullyQualifiedName~BaseControllerRoutesFacts"` | ✅ | ✅ green |
| 07-02-02 | 07-02 | 2 | HTTP-03 | — | URL `/api/v1/{entity}` resolves via `[controller]` token — verified via route template inspection (substituted templates assert "Tests" literal; Rule 1 fix-forward from plan body's un-substituted assertion) | integration | `dotnet test --filter "FullyQualifiedName~BaseControllerRoutesFacts"` | ✅ | ✅ green |
| 07-02-02 | 07-02 | 2 | HTTP-08 | — | BaseService verb order: validate→ToEntity→Add→Sync→Save→ToRead via NSubstitute Received.InOrder; ChangeTracker assertion uses Phase7TestDbContext (Blocker 1 fix — directly constructed from `_fixture.ConnectionString`); validator mock targets IValidator.ValidateAsync(IValidationContext, CT) overload (Rule 1 fix-forward from plan body's generic IValidator<T>.ValidateAsync(T, CT) mock) | unit | `dotnet test --filter "FullyQualifiedName~BaseServiceOrderingFacts"` | ✅ | ✅ green |
| 07-02-02 | 07-02 | 2 | HTTP-09 | T-07-03 / Information Disclosure | NotFound surfaces as NotFoundException → 404 ProblemDetails with resourceType=TestEntity + correlationId matching X-Correlation-Id header | integration | `dotnet test --filter "FullyQualifiedName~NotFoundFacts"` | ✅ | ✅ green |
| 07-02-02 | 07-02 | 2 | HTTP-13 | T-07-06 / Tampering (supply chain) | AddBaseApi composition root wires DbContext + interceptors + repository + 4 IExceptionHandler + 3 health probes + Asp.Versioning + Swashbuckle + validators + mappers — all resolvable from a Scope; asserts BOTH IValidator<TestCreateDto> AND IValidator<TestUpdateDto> resolve (Blocker 2 fix) | integration | `dotnet test --filter "FullyQualifiedName~AddBaseApiFacts"` | ✅ | ✅ green |
| 07-02-02 | 07-02 | 2 | HTTP-14 | T-07-05 / Tampering / Log Injection | UseBaseApi pipeline order (ExceptionHandler→Correlation→Routing→Swagger→Health) — verified via X-Correlation-Id header echo on /api/v1/tests probe | integration | `dotnet test --filter "FullyQualifiedName~UseBaseApiPipelineFacts"` | ✅ | ✅ green |
| 07-02-02 | 07-02 | 2 | HTTP-15 | T-07-04 / Information Disclosure (Asp.Versioning ProblemDetails) | URL-segment versioning `/api/v{version}/` — supported v1 returns 200; unsupported v99 returns 404 (Asp.Versioning routing no-match path, RESEARCH A6 falsified — see plan body deviation note); correlationId still flows via Phase 4 customizer when ProblemDetails body present | integration | `dotnet test --filter "FullyQualifiedName~VersioningFacts"` | ✅ | ✅ green |
| 07-02-02 | 07-02 | 2 | HTTP-16 | T-07-01 / Information Disclosure (Swagger in Prod) | `/swagger` 200 in Dev, 404 in Prod via ProductionWebAppFactory pattern (UseEnvironment(Environments.Production)) | integration | `dotnet test --filter "FullyQualifiedName~SwaggerEnvironmentFacts"` | ✅ | ✅ green |
| 07-02-02 | 07-02 | 2 | SC#3 (Program.cs thin) | — | Program.cs source inspection — positive presence of AddBaseApi< / UseBaseApi( / MapControllers / AddBaseApiObservability (D-13 amendment per Plan 07-01 `<context_deviation>`); negative absence of AddOpenTelemetry / AddHealthChecks / AddExceptionHandler< / AddDbContext< / AddSwaggerGen / AddApiVersioning; body line count ≤ 10 | unit (file read) | `dotnet test --filter "FullyQualifiedName~ProgramMinimalityFacts"` | ✅ | ✅ green |
| 07-02-03 | 07-02 | 2 | — | — | Regression: all 76 prior Phase 1-6 facts GREEN × 3 consecutive runs post-AddBaseApi migration encoded as a single PowerShell `1..3 \| ForEach-Object { dotnet test ... }` loop in `<automated>` (Warning 9 fix — not human-driven); byte-identical psql \l snapshot BEFORE/AFTER; tests/.otel-out clean | regression | PowerShell `1..3 \| ForEach-Object { dotnet test SK_P.sln --no-restore; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }` + Compare-Object psql_before psql_after + Test-Path tests\.otel-out\telemetry.jsonl | ✅ | ✅ green (98/98 × 3 runs: 20.7s / 19.0s / 18.4s — Phase 6's known-stable 76 plus 22 new Phase 7 facts) |
| 07-02-04 | 07-02 | 2 | HTTP-16 / SC#4 (visual) | T-07-01 / Information Disclosure (defense-in-depth) | Manual UI smoke checkpoint resolved via user-approved automated-coverage rationale — SwaggerEnvironmentFacts loads TestsController via AddApplicationPart inside WebApplicationFactory and asserts /swagger 200 in Dev + 404 in Prod + /swagger/v1/swagger.json 200 in Dev + 404 in Prod across 4 facts; live-boot UI in v1 is structurally empty (Phase 8 adds the 5 entity controllers) | manual→automated (resolved) | covered by `dotnet test --filter "FullyQualifiedName~SwaggerEnvironmentFacts"` | ✅ (resolved 2026-05-27) | ✅ resolved (user-approved 2026-05-27 — SwaggerEnvironmentFacts is canonical SC#4 proof; see Manual-Only section + 07-02-SUMMARY decision matrix) |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

*Task IDs follow the convention `{phase}-{plan}-{task}` (e.g., `07-02-02` = Phase 7 / Plan 02 / Task 2 — the "8 fact-test classes" task in Plan 07-02). Task `07-02-03` is the regression-replay task; `07-02-04` is the human-verify checkpoint.*

---

## Wave 0 Requirements

All Wave 0 (test scaffold) artifacts are landed by **Plan 07-02 Task 1** before any fact-test class is written:

- [x] `tests/BaseApi.Tests/Validation/TestCreateDtoValidator.cs` — `AbstractValidator<TestCreateDto>` mirroring the existing `TestDtoValidator` pattern (covers TestUpdateDto only); REQUIRED to satisfy BaseService<...>'s ctor null-guard on `IValidator<TCreate>` (Plan 07-02 Task 1; Blocker 2 fix from revision iter 1)
- [x] `tests/BaseApi.Tests/Composition/Phase7TestDbContext.cs` — `BaseDbContext` subclass exposing `DbSet<BaseApi.Tests.Validation.TestEntity>`; needed because the existing `Persistence/TestDbContext.cs` tracks the DIFFERENT `BaseApi.Tests.Persistence.TestEntity`, and PostgresFixture has no `DbContext` property (Plan 07-02 Task 1; Blocker 1 fix from revision iter 1)
- [x] `tests/BaseApi.Tests/Composition/TestsController.cs` — empty-body `BaseController<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>` derived class; ctor injects the ABSTRACT `BaseService<...>` so Phase7WebAppFactory's alias registration is load-bearing (Warning 7 option b from revision iter 1; Plan 07-02 Task 1)
- [x] `tests/BaseApi.Tests/Composition/RecordingTestService.cs` — `BaseService<...>` subclass overriding `SyncJunctionsAsync` to record `(timestamp, ChangeTracker.State)` for SC#2 (Plan 07-02 Task 1)
- [x] `tests/BaseApi.Tests/Composition/Phase7WebAppFactory.cs` — WebAppFactory subclass adding TestsController + RecordingTestService + TestDtoValidator + TestCreateDtoValidator + TestEntityMapper via Phase 6 D-16 multi-assembly scan pattern; registers BOTH `RecordingTestService` AND `BaseService<...>` alias (load-bearing — TestsController injects the abstract) (Plan 07-02 Task 1)
- [x] `tests/BaseApi.Tests/Composition/ProductionWebAppFactory.cs` — WebAppFactory subclass calling `builder.UseEnvironment(Environments.Production)` in `CreateHost` (Plan 07-02 Task 1)
- [x] `tests/BaseApi.Tests/Validation/TestDtos.cs` audit/edit — TestReadDto must implement BOTH `: IBaseDto, IHasId` so it satisfies BaseController's `where TRead : IHasId` generic constraint while preserving Phase 6's IBaseDto membership (Plan 07-02 Task 1)
- [x] `Directory.Packages.props` — add `NSubstitute 5.3.0` pin (Plan 07-01 Task 1; test mocking library; RESEARCH Pitfall 9)
- [x] `Directory.Packages.props` — add `Asp.Versioning.Mvc 8.1.0` + `Asp.Versioning.Mvc.ApiExplorer 8.1.0` pins (Plan 07-01 Task 1; RESEARCH A-01 correction over CONTEXT D-17's `Asp.Versioning.Http`)
- [x] `Directory.Packages.props` — confirm `Swashbuckle.AspNetCore` pin remains at RESEARCH-recommended 6.9.0 (no change needed; Plan 07-01 Task 1 verification)

All Wave 0 artifacts are landed atomically by Plan 07-01 (package pins + BaseApi.Core surface) and Plan 07-02 Task 1 (test scaffolds) — no out-of-order dependencies.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Browse `/swagger` in Development renders OpenAPI UI listing 5 verbs under "Tests" group | HTTP-16 / SC#4 | UI smoke — automated `SwaggerEnvironmentFacts` asserts the 200/404 status-code contract via HTTP probe; visual UI rendering + visible verbs are human-eyeball confirmation | 1. `docker compose up -d postgres otel-collector`<br>2. `$env:ASPNETCORE_ENVIRONMENT = "Development"; dotnet run --project src/BaseApi.Service`<br>3. Open `http://localhost:5000/swagger`<br>4. Confirm UI lists `GET /api/v1/Tests`, `GET /api/v1/Tests/{id}`, `POST /api/v1/Tests`, `PUT /api/v1/Tests/{id}`, `DELETE /api/v1/Tests/{id}`<br>5. Expand each operation; confirm `X-Correlation-Id` header parameter is visible |
| Production `/swagger` returns 404 (live process, not just WebApplicationFactory) | HTTP-16 / SC#4 | Cross-validation of the automated Prod-404 fact via real `ASPNETCORE_ENVIRONMENT=Production` boot | 1. Stop the Dev host (Ctrl+C)<br>2. `$env:ASPNETCORE_ENVIRONMENT = "Production"; dotnet run --project src/BaseApi.Service`<br>3. `curl -i http://localhost:5000/swagger`<br>4. Confirm `HTTP/1.1 404 Not Found` |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (NSubstitute, Asp.Versioning.Mvc/.ApiExplorer pins; 4 test scaffolds + 8 fact-test files + 1 missing validator + 1 Phase 7-specific DbContext from revision iter 1)
- [x] No watch-mode flags (CI must run in one-shot `dotnet test` mode)
- [x] Feedback latency < 60s for full suite
- [x] Regression: all 76 prior Phase 1-6 facts GREEN × 3 consecutive runs post-AddBaseApi migration encoded as PowerShell loop in `<automated>` (Phase 3 D-18 cadence; Warning 9 fix from revision iter 1) — verified during Plan 07-02 Task 3 execution (98/98 × 3 runs GREEN: 20.7s / 19.0s / 18.4s; psql snapshots byte-identical; tests/.otel-out clean)
- [x] `nyquist_compliant: true` set in frontmatter (planner-filled — Task IDs are concrete `07-02-02` / `07-02-03` / `07-02-04`)

**Approval:** approved by ROADMAP SC verification — Plan 07-02 Task 3 (3 consecutive GREEN runs encoded as PowerShell `<automated>` loop, 98/98 GREEN at 20.7s / 19.0s / 18.4s) + Task 4 (manual UI smoke checkpoint resolved 2026-05-27 via user-approved automated-coverage rationale — SwaggerEnvironmentFacts as canonical SC#4 proof) close all 9 HTTP-* REQ-IDs and SC#1-4. **Executed and approved 2026-05-27.**

---

## Validation Audit 2026-05-27

| Metric | Count |
|--------|-------|
| Requirements audited | 9 HTTP-* + 4 SC + 1 regression |
| Gaps found | 0 |
| Resolved | 0 (no spawn needed) |
| Escalated | 0 |
| Manual-only | 2 (defense-in-depth cross-checks; primary SC#4 proof is automated) |

**Audit outcome:** Phase 7 is Nyquist-compliant. All 9 HTTP-* requirements (HTTP-01/02/03/08/09/13/14/15/16) plus SC#1-4 have automated coverage by 9 fact-test classes (BaseControllerRoutesFacts, BaseServiceOrderingFacts, NotFoundFacts, AddBaseApiFacts, UseBaseApiPipelineFacts, VersioningFacts, SwaggerEnvironmentFacts, ProgramMinimalityFacts) under Phase7WebAppFactory + ProductionWebAppFactory. All 14 test files claimed by VALIDATION.md exist on disk. Regression replay (98/98 GREEN × 3 consecutive runs) verified during Plan 07-02 Task 3 execution. No new tests required.

**State drift fixed in this audit:** (a) frontmatter `status: planned` → `complete` (phase executed per 07-02-SUMMARY.md); (b) Per-Task Map row 07-02-04 status `⬜ pending` → `✅ resolved` reflecting user-approved automated-coverage rationale documented in 07-02-SUMMARY.md decision matrix; (c) Sign-off "Pending execution" → "Executed and approved 2026-05-27".

---

## Revision History

- **2026-05-27 (iter 1):** Surgical edits applied to Plans 07-01 + 07-02 in response to plan-checker review. Blocker fixes: (1) added `Phase7TestDbContext` for ChangeTracker assertion in BaseServiceOrderingFacts; (2) added `TestCreateDtoValidator` to satisfy BaseService<...> ctor null-guard; (3) rewrote Plan 07-01 must_haves.key_links #3 to match actual 6-call IServiceCollection chain + added separate Observability key_link; (4) added `<context_deviation>` block to Plan 07-01 documenting the engineering-necessary D-13 amendment (Observability invoked on IHostApplicationBuilder, not IServiceCollection, because OTel MEL bridge requires ILoggingBuilder). Warning fixes: (5) parameterless `_fixture.InitializeAsync()`; (6) auto-fixed via Blocker 2; (7) TestsController ctor injects abstract `BaseService<...>` so alias is load-bearing; (8) auto-fixed via Blocker 2; (9) regression replay encoded as PowerShell 3× loop in `<automated>`. No task ID shifts.
