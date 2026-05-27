---
phase: 7
slug: generic-http-base-composition-root
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-27
---

# Phase 7 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 |
| **Config file** | tests/BaseApi.Tests/BaseApi.Tests.csproj |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~<tag>"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Estimated runtime** | ~30-60 seconds (per Phase 3 D-15 throwaway DB pattern) |

---

## Sampling Rate

- **After every task commit:** Run quick (filtered) test suite for that area
- **After every plan wave:** Run full suite (`dotnet test`)
- **Before `/gsd-verify-work`:** Full suite must be green × 3 consecutive runs (Phase 3 D-18 cadence inherited)
- **Max feedback latency:** ~60 seconds for full suite

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| TBD | 07-01 | 1 | HTTP-01 | — | ASP.NET Core controllers in use (not Minimal APIs) | unit | `dotnet test --filter "FullyQualifiedName~ControllerBase"` | ❌ W0 | ⬜ pending |
| TBD | 07-01 | 1 | HTTP-02 | — | Abstract generic BaseController exposes 5 verbs | integration | `dotnet test --filter "FullyQualifiedName~BaseControllerRoutesFacts"` | ❌ W0 | ⬜ pending |
| TBD | 07-01 | 1 | HTTP-03 | — | URL `/api/v1/{entity}` resolves via `[controller]` token | integration | `dotnet test --filter "FullyQualifiedName~RoutingFacts"` | ❌ W0 | ⬜ pending |
| TBD | 07-01 | 1 | HTTP-08 | — | BaseService verb order: validate→ToEntity→Add→Sync→Save→ToRead | unit | `dotnet test --filter "FullyQualifiedName~BaseServiceOrderingFacts"` | ❌ W0 | ⬜ pending |
| TBD | 07-01 | 1 | HTTP-09 | — | NotFound surfaces as NotFoundException → 404 ProblemDetails | integration | `dotnet test --filter "FullyQualifiedName~NotFoundFacts"` | ❌ W0 | ⬜ pending |
| TBD | 07-01 | 1 | HTTP-13 | T-04 / — | AddBaseApi composition root wires DbContext + interceptors | integration | `dotnet test --filter "FullyQualifiedName~AddBaseApiFacts"` | ❌ W0 | ⬜ pending |
| TBD | 07-01 | 1 | HTTP-14 | T-04 / — | UseBaseApi pipeline order (ExceptionHandler→Correlation→Routing→Swagger→Health) | integration | `dotnet test --filter "FullyQualifiedName~UseBaseApiPipelineFacts"` | ❌ W0 | ⬜ pending |
| TBD | 07-01 | 1 | HTTP-15 | — | URL-segment versioning `/api/v{version}/` | integration | `dotnet test --filter "FullyQualifiedName~VersioningFacts"` | ❌ W0 | ⬜ pending |
| TBD | 07-01 | 1 | HTTP-16 | T-09 / — | `/swagger` 200 in Dev, 404 in Prod | integration | `dotnet test --filter "FullyQualifiedName~SwaggerEnvironmentFacts"` | ❌ W0 | ⬜ pending |
| TBD | 07-02 | 2 | — | — | Regression: all 47 prior facts GREEN × 3 runs post-AddBaseApi migration | regression | `for i in 1 2 3; do dotnet test || exit 1; done` | ✅ | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

*Note: Task IDs (TBD) will be filled in by the planner. The planner MUST emit a verification fact for each REQ-ID above and update this table.*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Controllers/BaseControllerRoutesFacts.cs` — stubs for HTTP-02, HTTP-03 (5-verb route table inspection)
- [ ] `tests/BaseApi.Tests/Services/BaseServiceOrderingFacts.cs` — stubs for HTTP-08 (6-step verb order; uses NSubstitute `Received.InOrder`)
- [ ] `tests/BaseApi.Tests/Services/NotFoundFacts.cs` — stubs for HTTP-09
- [ ] `tests/BaseApi.Tests/Composition/AddBaseApiFacts.cs` — stubs for HTTP-13 (sub-extension chain wiring)
- [ ] `tests/BaseApi.Tests/Composition/UseBaseApiPipelineFacts.cs` — stubs for HTTP-14 (middleware order assertion)
- [ ] `tests/BaseApi.Tests/Versioning/VersioningFacts.cs` — stubs for HTTP-15 (URL-segment substitution)
- [ ] `tests/BaseApi.Tests/Swagger/SwaggerEnvironmentFacts.cs` — stubs for HTTP-16 (Dev 200, Prod 404)
- [ ] `tests/BaseApi.Tests/Composition/TestsController.cs` — empty-body `BaseController<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>` derived class (SC#1 + SC#2 end-to-end)
- [ ] `tests/BaseApi.Tests/Composition/RecordingTestService.cs` — `BaseService<...>` subclass overriding `SyncJunctionsAsync` to record `(timestamp, ChangeTracker.State)` for SC#2
- [ ] `Directory.Packages.props` — add `NSubstitute 5.3.0` pin (test mocking library; CONTEXT.md Discretion item)
- [ ] `Directory.Packages.props` — add `Asp.Versioning.Mvc 8.1.0` + `Asp.Versioning.Mvc.ApiExplorer 8.1.0` (RESEARCH A-01 correction over CONTEXT D-17's `Asp.Versioning.Http`)
- [ ] `Directory.Packages.props` — confirm `Swashbuckle.AspNetCore` pin matches RESEARCH recommendation (6.9.0)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Browse `/swagger` in Development environment renders OpenAPI UI | HTTP-16 / SC#4 | UI smoke test — automated SwaggerEnvironmentFacts asserts the 200/404 contract via HTTP probe; visual UI rendering is human-eyeball confirmation | 1. `dotnet run --project src/BaseApi.Service --launch-profile Development`  2. Open `http://localhost:5000/swagger`  3. Confirm UI lists `GET /api/v1/tests`, `GET /api/v1/tests/{id:guid}`, `POST /api/v1/tests`, `PUT /api/v1/tests/{id:guid}`, `DELETE /api/v1/tests/{id:guid}` |
| Production `/swagger` returns 404 | HTTP-16 / SC#4 | Cross-validation of the automated Prod 404 fact via real `ASPNETCORE_ENVIRONMENT=Production` boot | 1. `ASPNETCORE_ENVIRONMENT=Production dotnet run --project src/BaseApi.Service`  2. `curl -i http://localhost:5000/swagger`  3. Confirm `HTTP/1.1 404 Not Found` |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references (NSubstitute, Asp.Versioning.Mvc/.ApiExplorer pins; new fact-test files)
- [ ] No watch-mode flags (CI must run in one-shot `dotnet test` mode)
- [ ] Feedback latency < 60s for full suite
- [ ] Regression: all 47 prior Phase 1-5 facts GREEN × 3 consecutive runs post-AddBaseApi migration (Phase 3 D-18 cadence)
- [ ] `nyquist_compliant: true` set in frontmatter (after planner fills task IDs)

**Approval:** pending
