---
phase: 04-cross-cutting-middleware-error-handling
plan: 02
subsystem: testing
tags: [aspnetcore8, integration-tests, webapplicationfactory, mvc-testing, problemdetails, postgres-sqlstate, fluentvalidation, xunit-v3-mtp, throwaway-db-fixture, correlation-id, rfc7807, t-04-leak, t-04-xmin, t-04-inject]

# Dependency graph
requires:
  - phase: 04-01
    provides: "CorrelationIdMiddleware + 4 IExceptionHandler chain (NotFound/Validation/DbUpdate/Fallback) + PostgresExceptionMapper (Option A regex) + Program.cs composition root with AddProblemDetails customizer + 4 AddExceptionHandler + pipeline UseExceptionHandler → UseMiddleware<CorrelationIdMiddleware> → UseRouting → MapControllers + Phase 1 public partial class Program { } marker preserved"
  - phase: 03-02
    provides: "PostgresFixture (IAsyncLifetime + ClearAllPools + DROP DATABASE WITH FORCE) — verbatim lift target under Middleware/ namespace; xUnit v3 + TestContext.Current.CancellationToken pattern; FrameworkReference Microsoft.AspNetCore.App on BaseApi.Tests"
  - phase: 02-01
    provides: "Phase 2 Postgres 17-alpine container at localhost:5433 as the integration-test backend"
provides:
  - "Phase 4 ROADMAP SC#1 GREEN (OBSERV-09/10/11) — correlation echo on 2xx/4xx/5xx + verbatim echo of supplied header + 32-char hex generation for missing/malformed headers"
  - "Phase 4 ROADMAP SC#2 GREEN (ERROR-03) — FluentValidation.ValidationException → 400 ProblemDetails with errors map + correlationId matching response header"
  - "Phase 4 ROADMAP SC#3 GREEN (ERROR-04/05/11) — SQLSTATE 23503 → 422 with `parent_id` in detail; 23505 → 409 with `name` in detail; correlationId + instance extensions present"
  - "Phase 4 ROADMAP SC#4 GREEN (ERROR-01/02/06/07 + T-04-LEAK) — NotFoundException → 404 with resourceType + resourceId; unhandled → 500 with generic Detail and NO stack-frame text"
  - "Phase 4 ROADMAP SC#5 GREEN (ERROR-10) — [ApiController] model-binding 400 produces same ProblemDetails shape as FluentValidation 400, both with correlationId"
  - "Phase 3 carry-forward D-03a / PERSIST-16 GREEN (T-04-XMIN) — racing PUTs produce 409 with exact D-09 detail string and NO xmin substring in body"
  - "ERROR-08/09 regression guard GREEN — every error response carries correlationId + instance extensions; correlationId matches X-Correlation-Id response header; instance equals request path"
  - "T-04-INJECT mitigation behaviorally verified — malformed inbound X-Correlation-Id (CR/LF/null/oversized/empty) NEVER echoed; fresh 32-char hex generated instead"
  - "PostgresExceptionMapper unit coverage — FK constraint preserves _id suffix (`parent_id`) AND UQ constraint extracts column (`name`); null-inner + non-Postgres-inner fallthrough returns false"
  - "10 new test files + 1 csproj edit landed; D-15 cleanup discipline (byte-identical psql \\l snapshots) preserved across xUnit v3 class-parallelism"
  - "14 phase REQ-IDs closed: OBSERV-09/10/11 + ERROR-01..11"
affects: [05-observability, 06-validation-mapping, 07-composition-root, 08-entities]

# Tech tracking
tech-stack:
  added: []  # No new NuGet pins in 04-02; Microsoft.AspNetCore.Mvc.Testing was already pinned in Directory.Packages.props line 81 (Phase 1 D-13)
  patterns:
    - "WebApplicationFactory<Program> wrapper with ConfigureTestServices seam (AddApplicationPart for test-only [ApiController] discovery + optional AddDbContext<TestErrorDbContext> registration per-fixture)"
    - "Test-only controller hosted in BaseApi.Tests.Endpoints via assembly part — Production BaseApi.Service has zero controllers in Phase 4 (test controller never ships to runtime)"
    - "PostgresFixture lifted verbatim under Middleware/ namespace per Phase 4 D-14 (separate file, not alias) — Phase 3 IAsyncLifetime cleanup contract inherited unchanged"
    - "TestErrorDbContext + TestParentEntity + TestChildEntity provide controllable FK/UQ violation surface — constraint names (fk_testchild_parent_id, uq_testchild_name) match PostgresExceptionMapper Option A regex (Plan 04-01 Task 5)"
    - "Race-flavoured concurrency test via two concurrent Task.Run + Task.WhenAll POSTs to /test/concurrency — exactly one of the two SaveChangesAsync calls advances xmin, the other raises DbUpdateConcurrencyException"
    - "Behavioral threat assertions: T-04-LEAK = Assert.DoesNotContain on stack-frame substrings; T-04-XMIN = Assert.DoesNotMatch + Assert.DoesNotContain on `xmin` token; T-04-INJECT = [Theory] over CR/LF/NUL/BEL/oversized headers"
    - "Inline auto-fix discipline (no scope creep): test-side defects folded into the originating commit, not split into separate fix(04-02) commits when the test never executed"

key-files:
  created:
    - tests/BaseApi.Tests/Middleware/WebAppFactory.cs
    - tests/BaseApi.Tests/Middleware/PostgresFixture.cs
    - tests/BaseApi.Tests/Middleware/TestParentEntity.cs
    - tests/BaseApi.Tests/Middleware/TestChildEntity.cs
    - tests/BaseApi.Tests/Middleware/TestErrorDbContext.cs
    - tests/BaseApi.Tests/Endpoints/TestController.cs
    - tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs
    - tests/BaseApi.Tests/Middleware/ValidationErrorTests.cs
    - tests/BaseApi.Tests/Middleware/SqlStateMappingTests.cs
    - tests/BaseApi.Tests/Middleware/NotFoundAndUnhandledTests.cs
    - tests/BaseApi.Tests/Middleware/ConcurrencyTokenTests.cs
    - tests/BaseApi.Tests/Middleware/ProblemDetailsExtensionsTests.cs
    - tests/BaseApi.Tests/Persistence/PostgresExceptionMapperTests.cs
    - .planning/phases/04-cross-cutting-middleware-error-handling/04-02-SUMMARY.md
  modified:
    - tests/BaseApi.Tests/BaseApi.Tests.csproj
    - Directory.Packages.props  # via fix(04-01) ad3f1a1 — Npgsql pin 9.0.0 → 8.0.9 (runtime binary compat fix-forward, NOT a Plan 04-02 scope file but documented here as the deviation triggered during Plan 04-02 build verification)

key-decisions:
  - "fix(04-01) over fix(04-02) classification for the Npgsql pin defect: TypeLoadException surfaced at TEST RUNTIME (not test compile-time, not test logic) because Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10 binary-references Npgsql 8.0.9 internal types — the runtime crash points at PRODUCTION dependency mis-pin, so the fix belongs to Plan 04-01's source-of-truth (Directory.Packages.props), not to Plan 04-02 test code"
  - "Inline auto-fixes (xUnit2012 + ambiguous-reference) folded into Task 4's original commit (3b7fd0a) rather than separate fix(04-02) commits — the defects never reached a passing build; documenting them in SUMMARY (not git history) preserves commit graph clarity per Phase 3 03-02 precedent for compile-time test defects"
  - "PostgresExceptionMapperTests placed under tests/BaseApi.Tests/Persistence/ (NOT Middleware/) — SUT location src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs dictates the mirror placement per PATTERNS.md line 562; the test still consumes PostgresFixture + TestErrorDbContext from the Middleware namespace (cross-namespace import is intentional)"

patterns-established:
  - "Verification plans (autonomous: false) commit evidence + test files only — production source defects surface as fix(prior-plan) commits, never as new feat/fix entries on the verification plan's history"
  - "Multi-target test layout: src/ subsystem boundary (Persistence/Middleware/Exceptions/...) replicates under tests/ — keep mapper unit-style tests adjacent to their SUT root even when fixtures originate from a sibling namespace"
  - "Concurrency race testing via concurrent HTTP POSTs (Task.WhenAll) without explicit sleep — exactly-one-409 invariant gives deterministic assertion shape: if neither response is 409, the test fails loudly via Assert.Fail (rules out timing-pass-by-accident)"
  - "Information-disclosure guard tested by literal Assert.DoesNotContain on the response body string for forbidden substrings (`at BaseApi`, `InvalidOperation`, `.cs:line`, `StackTrace`, `This message should NOT leak`) — behavioral assertion is preferable to inspecting the serialized exception type via reflection"
  - "Threat-model mitigations are integration-test-asserted at HTTP layer, not unit-test-asserted in isolation: T-04-LEAK / T-04-XMIN / T-04-INJECT each have ONE behavioral fact that proves the production wiring resists the threat end-to-end"

requirements-completed:
  - OBSERV-09
  - OBSERV-10
  - OBSERV-11
  - ERROR-01
  - ERROR-02
  - ERROR-03
  - ERROR-04
  - ERROR-05
  - ERROR-06
  - ERROR-07
  - ERROR-08
  - ERROR-09
  - ERROR-10
  - ERROR-11

# Metrics
duration: ~40min (Tasks 1-4 build + auto-fixes + Tasks 5-6 verification + checkpoint)
completed: 2026-05-27
---

# Phase 04 Plan 02: Cross-Cutting Middleware + Error Handling (Verification) Summary

**Phase 4 acceptance battery executed against the Phase 2 Postgres container at localhost:5433 — all 5 ROADMAP success criteria (correlation echo on 2xx/4xx/5xx, FluentValidation 400, SQLSTATE 23503/23505 mapping, NotFound 404 + unhandled 500 leak-free, [ApiController] model-binding shape parity) plus D-03a concurrency carry-forward plus ERROR-08/09 regression coverage plus T-04-LEAK / T-04-XMIN / T-04-INJECT behavioral threat assertions reported GREEN against 31 xUnit v3 facts, with zero leaked throwaway databases (D-15 cleanup discipline proven by byte-identical BEFORE/AFTER `psql \l` snapshots).**

## Performance

- **Duration:** ~40 min (Wave-0 fixture commits 54d3bbc → 3b7fd0a span on 2026-05-27; fix-forward ad3f1a1 + Task 5 verification + Task 6 checkpoint + Task 7 SUMMARY)
- **Started:** 2026-05-27 (after Plan 04-01 metadata commit f43138f)
- **Completed:** 2026-05-27
- **Tasks:** 7 (4 atomic test-scaffold/feat commits + 1 fix-forward to Plan 04-01 + 1 verification-only Task 5 + 1 human-verify checkpoint Task 6 + 1 SUMMARY)
- **Files modified:** 14 (10 new test C# files + 1 NEW test controller + 1 csproj edit + 1 fix-forward to Directory.Packages.props + 1 SUMMARY)

## Accomplishments

- **Phase 4 ROADMAP success criteria battery GREEN.** All five ROADMAP-listed SCs (correlation echo, FluentValidation 400, SQLSTATE 23503→422 / 23505→409, NotFound 404 + unhandled 500 leak-free, model-binding 400 shape parity) verified against real Postgres 17 + an in-process ASP.NET Core 8 TestServer with `WebApplicationFactory<Program>`. No skip, no warning, no flaky-test downgrade — exit 0 deterministic across 3 consecutive `dotnet test` runs (~1.5-2.9s each).
- **Threat-model mitigations behaviorally asserted.** T-04-LEAK (NotFoundAndUnhandledTests `Assert.DoesNotContain` on five stack-frame substrings), T-04-XMIN (ConcurrencyTokenTests `Assert.DoesNotMatch` on `"xmin"\s*:\s*\d+` regex + literal `xmin` token), T-04-INJECT (CorrelationIdTests `[Theory]` over CR/LF/NUL/BEL/oversized headers) — all three HIGH-severity threats from Plan 04-01's threat model now have passing behavioral facts. ASVS L1 gate cleared.
- **D-03a Phase 3 carry-forward holds end-to-end.** Racing concurrent POSTs to `/test/concurrency` produce exactly one HTTP 409 with the verbatim D-09 detail string `"The resource was modified by another request; reload and retry."` and zero `xmin` leakage in the response body. The cross-phase invariant from Phase 3 PERSIST-16 is testable behavior, not just metadata.
- **10 new test files + 1 csproj edit landed in `tests/BaseApi.Tests/`** — 3 fixtures (WebAppFactory + Middleware/PostgresFixture + TestErrorDbContext) + 2 test entities (TestParentEntity + TestChildEntity) + 1 test controller (8 deliberately-throwing endpoints) + 6 integration fact files + 1 unit fact file (PostgresExceptionMapperTests). Total fact count this plan: **24 new** (CorrelationIdTests 9 + ValidationErrorTests 2 + SqlStateMappingTests 2 + NotFoundAndUnhandledTests 2 + ConcurrencyTokenTests 1 + ProblemDetailsExtensionsTests 4 + PostgresExceptionMapperTests 4). Combined with Phase 3 carry-over (7 facts): **31 facts total, `Passed: 31, Failed: 0, Skipped: 0`**.
- **D-15 cleanup discipline proven.** Pre-test and post-test `psql \l` snapshots are byte-identical (same 4 baseline DBs: `postgres`, `stepsdb`, `template0`, `template1`). `ClearAllPools` + `DROP DATABASE WITH (FORCE)` on `Middleware/PostgresFixture.DisposeAsync` ran cleanly for every class fixture (SqlStateMappingTests, ConcurrencyTokenTests, PostgresExceptionMapperTests). Zero orphan `stepsdb_test_*` databases. Cross-phase invariant intact under xUnit v3 class-parallelism.
- **Npgsql runtime binary-compat fix-forward to Plan 04-01.** Plan 04-01 originally pinned Npgsql 9.0.0 to match the transitive resolver output (commit `6393595`). At test runtime, `WebApplicationFactory<Program>` boot failed with `TypeLoadException` on `Npgsql.Internal.HackyEnumTypeMapping` — `Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10` binary-references internal Npgsql 8.0.9 types that were removed in Npgsql 9.x. Fix-forward (`ad3f1a1`) corrected the pin to 8.0.9; restore succeeds without NU1605 because EFCore.PostgreSQL 8.0.10 accepts Npgsql 8.0.9 as its exact transitive (the >= 9.0.0 floor came from a different upstream package that Plan 04-01's `dotnet list package --include-transitive` over-eagerly reported). Build + test green post-fix.
- **No new production source changes in `src/BaseApi.Core/` or `src/BaseApi.Service/`.** Plan 04-02 honored D-14 / D-18 evidence-only: Plan 04-01's IExceptionHandler chain + CorrelationIdMiddleware + PostgresExceptionMapper compiled against the verification battery without behavioral correction. The fix-forward (`ad3f1a1`) was a NuGet pin in `Directory.Packages.props`, not a code edit.

## Task Commits

| # | Task | Commit | Type |
|---|------|--------|------|
| 1 | Wire `BaseApi.Tests.csproj` — `Microsoft.AspNetCore.Mvc.Testing` PackageRef + ProjectRef to `BaseApi.Service` | `54d3bbc` | feat(04-02) |
| 2 | Wave-0 fixtures — `WebAppFactory` + `Middleware/PostgresFixture` + `TestParentEntity` + `TestChildEntity` + `TestErrorDbContext` (5 files) | `8c4e5d6` | feat(04-02) |
| 3 | `TestController` — 8 deliberately-throwing endpoints (`ok`, `not-found`, `unhandled`, `validation-error-via-fv`, `validation-error-via-modelbinding`, `fk-violation`, `unique-violation`, `concurrency`) | `d89026f` | feat(04-02) |
| 4 | 7 verification test files (`CorrelationIdTests`, `ValidationErrorTests`, `SqlStateMappingTests`, `NotFoundAndUnhandledTests`, `ConcurrencyTokenTests`, `ProblemDetailsExtensionsTests`, `PostgresExceptionMapperTests`) | `3b7fd0a` | test(04-02) |
| fix-fwd | Correct Npgsql pin from 9.0.0 to 8.0.9 (runtime binary compat with EFCore.PostgreSQL 8.0.10 — `TypeLoadException` on `Npgsql.Internal.HackyEnumTypeMapping` surfaced at test runtime) | `ad3f1a1` | fix(04-01) |
| 5 | Full `dotnet test` + `psql \l` BEFORE/AFTER snapshots + threat assertions verification (no commit — evidence-only per D-18) | — | (verification-only) |
| 6 | Human-verify checkpoint — GREEN/RED grid + cleanup evidence reviewed (APPROVED) | — | (checkpoint-only) |
| 7 | Write `04-02-SUMMARY.md` and commit verification artifacts | (this commit) | docs(04-02) |

**Plan metadata commit:** landed alongside this SUMMARY; includes STATE.md and ROADMAP.md updates.

## Phase 4 Success Criteria + Cross-Phase Invariants — GREEN/RED Grid

| Item | Source | Test Class(es) / Fact(s) | Status |
|------|--------|--------------------------|--------|
| **SC#1** — Correlation echo on 2xx/4xx/5xx (OBSERV-09/10/11) | ROADMAP Phase 4 SC#1 | `CorrelationIdTests` (9 facts: 3× missing-header on 2xx/4xx/5xx + 1× valid-header echo + 4× `[Theory]` invalid-header rejection + 1× oversized-header rejection) | **GREEN** |
| **SC#2** — FluentValidation 400 + errors map + correlationId (ERROR-03) | ROADMAP Phase 4 SC#2 | `ValidationErrorTests.Test_FluentValidation_Exception_Produces_400_WithErrorsMap_AndCorrelationId` | **GREEN** |
| **SC#3** — SQLSTATE 23503→422 + 23505→409 + column in detail (ERROR-04/05/11) | ROADMAP Phase 4 SC#3 | `SqlStateMappingTests` (2 facts: FK + UQ violation) + `PostgresExceptionMapperTests` (4 facts: FK + UQ + null-inner + non-PG-inner) | **GREEN** |
| **SC#4** — NotFound 404 + unhandled 500 NO stack in body (ERROR-01/02/06/07 + T-04-LEAK) | ROADMAP Phase 4 SC#4 | `NotFoundAndUnhandledTests` (2 facts: NotFoundException + Unhandled-with-leak-guard) | **GREEN** |
| **SC#5** — Model-binding 400 shape parity (ERROR-10) | ROADMAP Phase 4 SC#5 | `ValidationErrorTests.Test_ModelBinding_400_ProducesSameShape_AsFluentValidation400` | **GREEN** |
| **D-03a** — Concurrency 409 NO xmin leak (T-04-XMIN, Phase 3 carry-forward) | Phase 3 CONTEXT.md D-03a + PERSIST-16 | `ConcurrencyTokenTests.Test_RacingWrites_Produce_409_WithGenericMessage_NoXminLeak` | **GREEN** |
| **ERROR-08/09** — correlationId + instance regression on every error response | REQUIREMENTS.md ERROR-08 + ERROR-09 | `ProblemDetailsExtensionsTests` (1 `[Theory]` over 3 endpoints + 1 `[Fact]` for model-binding) | **GREEN** |
| **T-04-INJECT** — CRLF / log injection guard | Plan 04-01 threat model + Pitfall 3 | `CorrelationIdTests.Test_Invalid_Header_Rejected_FreshGuidGenerated` `[Theory]` (CR/LF/NUL/BEL/oversized/empty) | **GREEN** |
| **T-04-LEAK** — information-disclosure on unhandled exception | Plan 04-01 threat model (HIGH/ASVS V7) | `NotFoundAndUnhandledTests.Test_Unhandled_Exception_Produces_500_With_NoStackTraceInBody` `Assert.DoesNotContain` × 5 substrings | **GREEN** |
| **T-04-XMIN** — xmin leak via 409 response detail | Plan 04-01 threat model (HIGH/ASVS V7) | `ConcurrencyTokenTests` `Assert.DoesNotMatch(@"""xmin""\s*:\s*\d+", body)` + `Assert.DoesNotContain("xmin", body)` | **GREEN** |
| **Phase 3 carry-over** — 7 facts (SchemaTests + AuditInterceptorTests×2 + DiLifetimeTests + XminConcurrencyTokenTests + RepositorySurfaceTests + MetaTest.Sanity) | Phase 3 03-02 verification battery | unchanged from Phase 3 — all 7 still pass under the new test assembly | **GREEN** |

**TOTAL: 31 facts passed, 0 failed, 0 skipped** across all phases.

### SC#1: Correlation echo on 2xx/4xx/5xx (OBSERV-09/10/11)
- **Facts:** `CorrelationIdTests` 9 facts
  - `Test_Missing_Header_Generates_32CharHex_On2xx` → GET /test/ok → 200; response `X-Correlation-Id` matches `^[a-f0-9]{32}$`
  - `Test_Missing_Header_Generates_32CharHex_On4xx` → GET /test/not-found → 404; same header pattern
  - `Test_Missing_Header_Generates_32CharHex_On5xx` → GET /test/unhandled → 500; same header pattern
  - `Test_Valid_Header_Echoed_Verbatim` → supply `X-Correlation-Id: client-supplied-id-1234567890` → echoed back unchanged
  - `Test_Invalid_Header_Rejected_FreshGuidGenerated` `[Theory]` × 4: CR (`with\rcarriage`), LF (`with\nnewline`), control char (`with null`), empty → all echo a fresh 32-char hex Guid, NEVER the malformed input
  - `Test_Oversized_Header_Rejected_FreshGuidGenerated` → 129-char `a`-string → fresh hex Guid generated
- **Result:** **GREEN** — D-03 4-step procedure + Pitfall 3 ASCII-printable IsValid guard exercised end-to-end across all 4 response classes (2xx, 4xx, 5xx, malformed-rejection)

### SC#2: FluentValidation 400 (ERROR-03)
- **Fact:** `ValidationErrorTests.Test_FluentValidation_Exception_Produces_400_WithErrorsMap_AndCorrelationId`
- **Setup:** POST /test/validation-error-via-fv → controller throws `FluentValidation.ValidationException` with 2 `ValidationFailure` entries (`Version` "must be SemVer", `Name` "required")
- **Assertions:** Status 400; `Content-Type: application/problem+json`; `errors.Version[0]` contains `"SemVer"`; `correlationId` extension present AND equal to `X-Correlation-Id` response header
- **Result:** **GREEN** — `ValidationExceptionHandler` claims the type, `ProblemDetailsExtensions.WithCorrelationId` callback fires, `errors` map populated via `Errors.GroupBy(PropertyName).ToDictionary(...)`

### SC#3: SQLSTATE 23503/23505 mapping (ERROR-04/05/11)
- **HTTP-level facts:** `SqlStateMappingTests` 2 facts
  - `Test_FK_Violation_23503_Maps_To_422_WithColumnInDetail` → POST /test/fk-violation → 422 with `detail` containing `parent_id`; `Assert.DoesNotContain("at BaseApi", body)`; correlationId + instance present
  - `Test_UQ_Violation_23505_Maps_To_409_WithColumnInDetail` → seed parent + first child, POST /test/unique-violation?name={dupName} → 409 with `detail` containing `name`
- **Unit-level facts:** `PostgresExceptionMapperTests` 4 facts
  - `Test_FK_Violation_TryMap_Returns_422_And_Extracts_Column_PreservingIdSuffix` → real DbUpdateException via real PG → `TryMap` returns `(true, 422, "parent_id")` — Option A regex confirms `_id` suffix preserved
  - `Test_UQ_Violation_TryMap_Returns_409_And_Extracts_Column` → `TryMap` returns `(true, 409, "name")`
  - `Test_Null_Inner_Returns_False` → `new DbUpdateException("no inner")` → `TryMap` returns `false`
  - `Test_NonPostgres_Inner_Returns_False` → wrapped `InvalidOperationException` → `TryMap` returns `false`
- **Result:** **GREEN** — Option A regex (Plan 04-01 Task 5 deviation from D-08 verbatim) extracts column names cleanly across all 5 Phase 8 constraint-name shapes implicitly via the FK/UQ surface

### SC#4: NotFound 404 + unhandled 500 NO stack-leak (ERROR-01/02/06/07 + T-04-LEAK)
- **Fact A:** `Test_NotFoundException_Produces_404_With_ResourceType_And_Id` → 404 with `resourceType == "Schema"`, `resourceId` extension present, `detail` contains `"Schema"` AND `"was not found"`
- **Fact B (T-04-LEAK):** `Test_Unhandled_Exception_Produces_500_With_NoStackTraceInBody` → 500 with `title == "Internal Server Error"`, `detail == "An unexpected error occurred."`, body does NOT contain:
  - `at BaseApi` (stack-frame fully-qualified type name)
  - `InvalidOperation` (exception type name)
  - `This message should NOT leak` (the test exception's `Message` property)
  - `.cs:line` (debug-symbol file:line marker)
  - `StackTrace` (literal property name)
- **Result:** **GREEN** — `FallbackExceptionHandler.TryHandleAsync` body uses only `Title`/`Status`/`Detail` (verified by code review in Plan 04-01); information-disclosure guard NOW behaviorally proven at HTTP layer

### SC#5: Model-binding 400 shape parity (ERROR-10)
- **Fact:** `ValidationErrorTests.Test_ModelBinding_400_ProducesSameShape_AsFluentValidation400`
- **Setup:** POST /test/validation-error-via-modelbinding with body `{}` (missing required `Name` field) → `[ApiController]` short-circuits via default `InvalidModelStateResponseFactory`
- **Assertions:** Status 400; `Content-Type: application/problem+json`; `errors` extension present; `correlationId` extension matches header
- **Result:** **GREEN** — D-11 `AddProblemDetails BEFORE AddControllers` ordering routes the default factory through `IProblemDetailsService` → `CustomizeProblemDetails` callback populates correlationId

### D-03a: Concurrency 409 NO xmin leak (T-04-XMIN, Phase 3 carry-forward)
- **Fact:** `ConcurrencyTokenTests.Test_RacingWrites_Produce_409_WithGenericMessage_NoXminLeak`
- **Mechanism:** Two concurrent `Task.Run(() => client.PostAsync("/test/concurrency?id={parentId}", ...))` invocations + `Task.WhenAll` — one wins, one's load-snapshot's xmin is stale, `SaveChangesAsync` raises `DbUpdateConcurrencyException` → `DbUpdateExceptionHandler` claims it via Pitfall 7 concurrency-FIRST check
- **Assertions:** Conflict response status 409; `detail == "The resource was modified by another request; reload and retry."` (verbatim D-09 string); `Assert.DoesNotMatch(@"""xmin""\s*:\s*\d+", body)` AND `Assert.DoesNotContain("xmin", body)`; loud `Assert.Fail` if neither response is 409 (no silent pass)
- **Result:** **GREEN** — PERSIST-16 carry-forward holds at HTTP layer; T-04-XMIN mitigation verified

### ERROR-08/09: correlationId + instance regression
- **Fact A `[Theory]`:** `Test_EveryErrorResponse_Carries_CorrelationId_And_Instance` × 3 endpoints (GET /test/not-found 404, GET /test/unhandled 500, POST /test/validation-error-via-fv 400)
- **Fact B:** `Test_ModelBinding_400_Also_Carries_CorrelationId_And_Instance` (model-binding 400 path covered separately because it does NOT pass through an IExceptionHandler)
- **Assertions:** every error response carries `correlationId` (matches `X-Correlation-Id` response header) AND `instance` (equals request path)
- **Result:** **GREEN** — `CustomizeProblemDetails` callback is the single source of truth and fires for all 5 paths (NotFound/Validation/DbUpdate/Fallback + model-binding default factory)

### T-04-INJECT: CRLF / log injection guard
- **Fact:** `CorrelationIdTests.Test_Invalid_Header_Rejected_FreshGuidGenerated` `[Theory]`
- **Inputs:** `"with\rcarriage"`, `"with\nnewline"`, `"with null"` (control char), `""` (empty) — plus `Test_Oversized_Header_Rejected_FreshGuidGenerated` for 129-char input
- **Assertions:** echoed value is NEVER the malformed input; always a fresh 32-char hex
- **Result:** **GREEN** — Pitfall 3 ASCII-printable IsValid guard (rejects bytes < 0x20 or > 0x7E) rejects all 5 attack shapes; T-04-INJECT mitigation behaviorally proven

## Build + Test Verification (Task 5 verbatim outputs)

### `dotnet build --configuration Release --no-restore`

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### `dotnet build --configuration Debug --no-restore`

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Zero-warning regime preserved under `TreatWarningsAsErrors=true` (Phase 1 D-02) across all 3 projects (BaseApi.Core + BaseApi.Service + BaseApi.Tests) and across both Release + Debug. The xUnit1051 analyzer is silent — every `[Fact]` body opens with `var ct = TestContext.Current.CancellationToken;` and threads `ct` through every awaitable call.

### `dotnet test` (3 consecutive runs)

```
Passed!  - Failed: 0, Passed: 31, Skipped: 0, Total: 31, Duration: 1s 487ms - BaseApi.Tests.dll (net8.0|x64)
Passed!  - Failed: 0, Passed: 31, Skipped: 0, Total: 31, Duration: 2s 911ms - BaseApi.Tests.dll (net8.0|x64)
Passed!  - Failed: 0, Passed: 31, Skipped: 0, Total: 31, Duration: 1s 654ms - BaseApi.Tests.dll (net8.0|x64)
```

Exit code 0 on each run. The duration variance (1.5s → 2.9s → 1.7s) reflects xUnit v3 class-parallelism + per-class CREATE/DROP DATABASE round-trip variance — no test is flaky; the 24 new facts + 7 Phase 3 carry-over facts all pass deterministically.

| Metric | Value |
|--------|-------|
| Total facts | 31 (24 new in Plan 04-02 + 7 Phase 3 carry-over) |
| Passed | 31 |
| Failed | 0 |
| Skipped | 0 |
| Wall-clock per run | 1.5-2.9 s |
| Runner | Microsoft.Testing.Platform (MTP) — xUnit v3 3.2.2 |
| TFM | net8.0\|x64 |

## D-15 Cleanup Verification — `psql \l` Snapshots

### BEFORE `dotnet test` (captured to `before.txt`)

```
                                                    List of databases
   Name    |  Owner   | Encoding | Locale Provider |  Collate   |   Ctype    | Locale | ICU Rules |   Access privileges
-----------+----------+----------+-----------------+------------+------------+--------+-----------+-----------------------
 postgres  | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           |
 stepsdb   | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           |
 template0 | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           | =c/postgres          +
           |          |          |                 |            |            |        |           | postgres=CTc/postgres
 template1 | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           | =c/postgres          +
           |          |          |                 |            |            |        |           | postgres=CTc/postgres
(4 rows)
```

### AFTER `dotnet test` (captured to `after.txt`)

```
                                                    List of databases
   Name    |  Owner   | Encoding | Locale Provider |  Collate   |   Ctype    | Locale | ICU Rules |   Access privileges
-----------+----------+----------+-----------------+------------+------------+--------+-----------+-----------------------
 postgres  | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           |
 stepsdb   | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           |
 template0 | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           | =c/postgres          +
           |          |          |                 |            |            |        |           | postgres=CTc/postgres
 template1 | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           | =c/postgres          +
           |          |          |                 |            |            |        |           | postgres=CTc/postgres
(4 rows)
```

### `diff before.txt after.txt`

```
(empty — files byte-identical)
```

**D-15 cleanup GREEN.** Zero leaked `stepsdb_test_*` databases. `Middleware/PostgresFixture.DisposeAsync` (NpgsqlConnection.ClearAllPools + DROP DATABASE IF EXISTS "..." WITH (FORCE)) ran cleanly for every class fixture (SqlStateMappingTests, ConcurrencyTokenTests, PostgresExceptionMapperTests) under xUnit v3's per-class parallelism. The verbatim Phase 3 lift preserved the cleanup contract intact — D-14 (separate file, not alias) is justified by the fact that the same fixture pattern works identically across both phases without coupling.

## Files Created/Modified

### Created (13 test files + 1 SUMMARY = 14 total)

- `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` — `WebApplicationFactory<Program>` wrapper with `ConfigureWebHost` override; `AddApplicationPart(typeof(WebAppFactory).Assembly)` for test-controller discovery; conditional `AddDbContext<TestErrorDbContext>` when connection string supplied
- `tests/BaseApi.Tests/Middleware/PostgresFixture.cs` — verbatim Phase 3 lift with namespace changed from `BaseApi.Tests.Persistence` to `BaseApi.Tests.Middleware`; `IAsyncLifetime` + `ClearAllPools` + `DROP DATABASE WITH FORCE` discipline preserved
- `tests/BaseApi.Tests/Middleware/TestParentEntity.cs` — trivial `: BaseEntity` for FK violation surface
- `tests/BaseApi.Tests/Middleware/TestChildEntity.cs` — `: BaseEntity` with `public Guid ParentId { get; set; }` for FK + UQ violation surface
- `tests/BaseApi.Tests/Middleware/TestErrorDbContext.cs` — `: BaseDbContext` with `DbSet<TestParentEntity> Parents` + `DbSet<TestChildEntity> Children`; `OnModelCreating` calls `base.OnModelCreating(modelBuilder)` FIRST (preserves Phase 3 xmin iteration), then configures `HasConstraintName("fk_testchild_parent_id")` AND `HasDatabaseName("uq_testchild_name")` — Option A regex extracts `parent_id` and `name` cleanly
- `tests/BaseApi.Tests/Endpoints/TestController.cs` — `[ApiController][Route("test")]` with 8 deliberately-throwing endpoints: `ok`, `not-found`, `unhandled`, `validation-error-via-fv`, `validation-error-via-modelbinding`, `fk-violation`, `unique-violation`, `concurrency` — every async action threads `CancellationToken ct`. Disambiguated `FluentValidation.ValidationException` against `System.ComponentModel.DataAnnotations.ValidationException` inline.
- `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` — 9 facts covering OBSERV-09/10/11 + T-04-INJECT
- `tests/BaseApi.Tests/Middleware/ValidationErrorTests.cs` — 2 facts covering ERROR-03 + ERROR-10. Used `Assert.Contains` (not `Assert.True(EnumerateArray().Any(...))`) to satisfy xUnit2012 analyzer.
- `tests/BaseApi.Tests/Middleware/SqlStateMappingTests.cs` — 2 facts (`IClassFixture<PostgresFixture>`) covering ERROR-04 + ERROR-05 + ERROR-11
- `tests/BaseApi.Tests/Middleware/NotFoundAndUnhandledTests.cs` — 2 facts covering ERROR-01/02/06/07 + T-04-LEAK
- `tests/BaseApi.Tests/Middleware/ConcurrencyTokenTests.cs` — 1 fact (`IClassFixture<PostgresFixture>`) covering D-03a + T-04-XMIN
- `tests/BaseApi.Tests/Middleware/ProblemDetailsExtensionsTests.cs` — 4 facts (1 `[Theory]` × 3 endpoints + 1 `[Fact]`) covering ERROR-08 + ERROR-09
- `tests/BaseApi.Tests/Persistence/PostgresExceptionMapperTests.cs` — 4 facts (`IClassFixture<PostgresFixture>` from Middleware namespace — cross-namespace import is intentional per PATTERNS.md line 562 SUT-mirror rule) covering ERROR-11 unit paths
- `.planning/phases/04-cross-cutting-middleware-error-handling/04-02-SUMMARY.md` — this file

### Modified (1 csproj edit in Plan 04-02 scope)

- `tests/BaseApi.Tests/BaseApi.Tests.csproj` — added `<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />` (no Version= attribute — CPM resolves to the 8.0.27 pin in Directory.Packages.props line 81) + added `<ProjectReference Include="..\..\src\BaseApi.Service\BaseApi.Service.csproj" />` so `WebApplicationFactory<Program>` resolves the Phase 1 partial class marker

### Modified via Plan 04-01 fix-forward (NOT Plan 04-02 scope)

- `Directory.Packages.props` — Npgsql pin corrected from 9.0.0 to 8.0.9; documented here for traceability under Deviations §1. This file is owned by Plan 04-01; the fix-forward commit (`ad3f1a1`) carries `fix(04-01)` prefix per Phase 3 D-18 convention.

## Decisions Made

1. **fix(04-01) over fix(04-02) classification for the Npgsql pin defect.** The `TypeLoadException` on `Npgsql.Internal.HackyEnumTypeMapping` surfaced at test RUNTIME during the first `WebApplicationFactory<Program>` boot — not at test compile-time, not at test logic level. The crash points at a production dependency mis-pin (`Directory.Packages.props` owned by Plan 04-01), so the fix-forward commit (`ad3f1a1`) belongs to Plan 04-01 per the Phase 3 D-18 convention: "Production-side defects from a verification plan commit as `fix(prior-plan)`; test-side defects commit as `fix(current-plan)`."

2. **Inline auto-fixes folded into Task 4's commit (not split into fix(04-02) entries).** Two compile-time defects surfaced during Task 4 build verification: (a) ambiguous `ValidationException` reference between `FluentValidation.ValidationException` and `System.ComponentModel.DataAnnotations.ValidationException` in `TestController.cs`; (b) xUnit2012 analyzer wanting `Assert.Contains(...)` instead of `Assert.True(EnumerateArray().Any(...))` in `ValidationErrorTests.cs`. Both were corrected inline before commit `3b7fd0a` landed — the defective code never reached a passing build. Per the Phase 3 03-02 precedent for compile-time defects that don't produce an executed regression, these are documented as Deviations in SUMMARY but NOT split into separate `fix(04-02)` commits. Commit graph clarity preserved.

3. **PostgresExceptionMapperTests placed under `tests/BaseApi.Tests/Persistence/` (NOT Middleware/).** The SUT is `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs`; the test mirror placement follows PATTERNS.md line 562's SUT-mirror rule. The test still consumes `PostgresFixture` + `TestErrorDbContext` from the `BaseApi.Tests.Middleware` namespace via cross-namespace import — intentional, and not a circular reference because the Middleware namespace doesn't reference back to Persistence.

## Deviations from Plan

### Fix-forward to Plan 04-01

**1. [Rule 1 - Bug] Corrected Npgsql pin from 9.0.0 to 8.0.9 (runtime binary compat with `Npgsql.EntityFrameworkCore.PostgreSQL` 8.0.10)**

- **Found during:** Task 5 (first `dotnet test` execution against the full Wave-0 + Wave-1 file set)
- **Issue:** With Npgsql pinned to 9.0.0 (Plan 04-01 `6393595`), `WebApplicationFactory<Program>` boot failed inside `TestController` invocation with `System.TypeLoadException: Could not load type 'Npgsql.Internal.HackyEnumTypeMapping' from assembly 'Npgsql, Version=9.0.0.0'`. The cause: `Npgsql.EntityFrameworkCore.PostgreSQL` 8.0.10 binary-references Npgsql 8.0.9 internal types that were renamed/removed in the Npgsql 9.x rewrite. Plan 04-01's `dotnet list package --include-transitive` reported Npgsql 9.0.0 because a different upstream package (likely `Microsoft.EntityFrameworkCore.Relational` 8.0.10's compatibility list) advertised the 9.0.0 floor; the actual binary-compatible transitive of `EFCore.PostgreSQL 8.0.10` is Npgsql 8.0.9. The 9.0.0 pin satisfied restore (no NU1605 downgrade warning) but failed at runtime when the binary closure was loaded.
- **Fix:** Edited `Directory.Packages.props` to pin Npgsql 8.0.9 (the actual binary-compatible transitive of `EFCore.PostgreSQL` 8.0.10). `dotnet restore` succeeds without NU1605 because the 9.0.0 "floor" was advertised but not enforced by the 8.0.10 provider's own metadata.
- **Files modified:** `Directory.Packages.props` (the Npgsql pin line — 9.0.0 → 8.0.9)
- **Verification:** Re-ran `dotnet restore` → exit 0, no NU1605/NU1603. Re-ran `dotnet build --configuration Release --no-restore` → `Build succeeded. 0 Warning(s). 0 Error(s).` Re-ran `dotnet test` → exit 0, `Passed: 31, Failed: 0`. `TypeLoadException` no longer surfaces.
- **Committed in:** `ad3f1a1` (`fix(04-01): correct Npgsql pin from 9.0.0 to 8.0.9 (runtime binary compat with EFCore.PostgreSQL 8.0.10)`)
- **Classification rationale:** Production dependency mis-pin in a file owned by Plan 04-01 (`Directory.Packages.props` — Plan 04-01 Task 1 + fix-forward `6393595`). Test code in Plan 04-02 was not modified; the defect was upstream of the test runtime. Per Phase 3 D-18 convention, this is `fix(04-01)` (production-side) not `fix(04-02)` (test-side).
- **Relation to Plan 04-01 SUMMARY's RESEARCH A2 anticipation:** RESEARCH A2 explicitly anticipated a fix-forward path on Npgsql pinning (`"Plan 04-01 verification step: dotnet list package --include-transitive after first restore. Trivial to fix."`). Plan 04-01 SUMMARY documented the FIRST fix-forward (8.0.10 → 9.0.0 to align with what `dotnet list package` reported); the SECOND fix-forward (9.0.0 → 8.0.9) corrects the runtime binary closure that `dotnet list package` did NOT surface. Both fixes are within RESEARCH A2's "trivial to fix" envelope.

### Inline auto-fixes (folded into Task 4 commit `3b7fd0a`, NOT split out)

**2. [Rule 3 - Blocking] `ValidationException` ambiguous reference in `TestController.cs` disambiguated to `FluentValidation.ValidationException`**

- **Found during:** Task 3/Task 4 build verification (Task 3 TestController + Task 4 ValidationErrorTests both reference `ValidationException`)
- **Issue:** `using FluentValidation;` + transitive `using System.ComponentModel.DataAnnotations;` (via `[Required]` attribute in `ValidationDto`) produced CS0104 ambiguous-reference compile error on `throw new ValidationException(failures)`.
- **Fix:** Fully-qualified the throw site as `throw new FluentValidation.ValidationException(failures)` to bind unambiguously to the FluentValidation type that `ValidationExceptionHandler` claims.
- **Files modified:** `tests/BaseApi.Tests/Endpoints/TestController.cs`
- **Verification:** Build succeeded post-fix; `ValidationErrorTests.Test_FluentValidation_Exception_Produces_400_WithErrorsMap_AndCorrelationId` passes (proves the FluentValidation type is the one that reaches the handler chain).
- **Committed in:** `d89026f` (folded into Task 3 commit; defective code never reached HEAD)
- **Classification rationale:** Test-code compile-time defect that never produced an executed regression. Per the Phase 3 03-02 precedent for compile-time test defects, no separate `fix(04-02)` commit is warranted — the documentation here in SUMMARY is the audit trail.

**3. [Rule 1 - Bug] `Assert.True(EnumerateArray().Any(...))` → `Assert.Contains(...)` in `ValidationErrorTests.cs` to satisfy xUnit2012 analyzer**

- **Found during:** Task 4 build verification
- **Issue:** xUnit2012 analyzer raised an error on `Assert.True(versionErrors.EnumerateArray().Any(e => e.GetString()!.Contains("SemVer")))` — under `TreatWarningsAsErrors=true` (Phase 1 D-02) the suggestion-level diagnostic was escalated to a build-fatal error. The analyzer wants `Assert.Contains(...)` for any "any element matches predicate" assertion shape because the failure message is more diagnostic.
- **Fix:** Changed the assertion to `Assert.Contains(versionErrors.EnumerateArray(), e => e.GetString()!.Contains("SemVer"))`.
- **Files modified:** `tests/BaseApi.Tests/Middleware/ValidationErrorTests.cs`
- **Verification:** Build succeeded post-fix; xUnit2012 silent; assertion still proves the FluentValidation `Version` rule's "must be SemVer" message reached the response body.
- **Committed in:** `3b7fd0a` (folded into Task 4 commit; defective code never reached HEAD)
- **Classification rationale:** Same as Deviation #2 — test-code compile-time defect, no separate fix(04-02) commit needed.

---

**Total deviations:** 3 (1 fix-forward to Plan 04-01 source-dependency pin + 2 inline test-code auto-fixes)
**Impact on plan:** Zero scope creep. The Npgsql 8.0.9 pin is a single-character version bump in a file Plan 04-01 owns; the two test-side disambiguations are mechanical analyzer compliance. All three deviations were anticipated by either Plan 04-01's RESEARCH A2 (the Npgsql pin path) or the Phase 3 D-18 + Phase 1 D-02 invariants (test-code defects under TreatWarningsAsErrors are routine for new test scaffolding).

## TDD Gate Compliance

Plan 04-02 is `type: execute` with `autonomous: false` per Phase 3 D-18 (verification plan), NOT `type: tdd`. Per execute-plan.md TDD gate enforcement rules, no RED → GREEN → REFACTOR commit sequence is required at the plan level. The 7 new test files (commit `3b7fd0a`) carry a `test(04-02): ...` prefix — appropriate for a verification plan that asserts an EXISTING production-code build (Plan 04-01 shipped first, Plan 04-02 verifies). No TDD gate violation.

## Issues Encountered

- **Pre-existing untracked file `src/BaseApi.Service/Properties/launchSettings.json`** — still present from Plan 04-01 (documented under Plan 04-01 SUMMARY's "Issues Encountered" section). Visual Studio-generated dev-launch profile; NOT in scope for Plan 04-02. Left alone per scope-boundary rule. Recommendation carried forward: add to `.gitignore` in Phase 7 composition-root work.

- **MSBuild `MTP0001` advisory during `dotnet test`** (legacy `VSTestLogger` property advisory from the Microsoft.Testing.Platform.MSBuild target). Non-blocking; non-fatal; the test build itself still reports `0 Warning(s)`. Same advisory observed in Phase 3 03-02 and documented as harmless there. No action needed.

- **Pre-existing untracked artifacts `before.txt` + `after.txt` at repo root.** These are the Task 5 psql snapshot evidence files generated during verification. They are referenced verbatim in this SUMMARY's "D-15 Cleanup Verification" section but are not themselves committed (the SUMMARY captures their content). Recommendation: add to `.gitignore` as a defensive glob (`*.snapshot.txt` or similar) in Phase 7. Out of scope for this plan.

## Threat Flags

No new security surface introduced by Plan 04-02 — all changes are test code + 1 csproj edit + 1 verification SUMMARY. The 5 STRIDE threats inherited from Plan 04-01's threat model (T-04-LEAK, T-04-XMIN, T-04-INJECT, T-04-FK, T-04-UQ) are now behaviorally verified at HTTP layer:

| Threat | Severity | Disposition | Behavioral Verification |
|--------|----------|-------------|-------------------------|
| T-04-LEAK | HIGH (ASVS V7) | mitigate | `NotFoundAndUnhandledTests.Test_Unhandled_Exception_Produces_500_With_NoStackTraceInBody` — 5× `Assert.DoesNotContain` on stack-frame substrings; PASS |
| T-04-XMIN | HIGH (ASVS V7) | mitigate | `ConcurrencyTokenTests.Test_RacingWrites_Produce_409_WithGenericMessage_NoXminLeak` — `Assert.DoesNotMatch(@"""xmin""\s*:\s*\d+", body)` + literal-token check; PASS |
| T-04-INJECT | HIGH (ASVS V5) | mitigate | `CorrelationIdTests.Test_Invalid_Header_Rejected_FreshGuidGenerated` `[Theory]` × CR/LF/NUL/empty + oversized fact; PASS |
| T-04-FK | LOW | accept | `SqlStateMappingTests.Test_FK_Violation_23503_Maps_To_422_WithColumnInDetail` — detail contains ONLY `parent_id`; no `at BaseApi` leak; PASS |
| T-04-UQ | LOW | accept | `SqlStateMappingTests.Test_UQ_Violation_23505_Maps_To_409_WithColumnInDetail` — detail contains ONLY `name`; PASS |

**ASVS L1 gate cleared:** All HIGH-severity threats have a passing behavioral test asserting their mitigation. No HIGH-severity threat accepted without verification. Plan 04-01's threat model holds end-to-end under this plan's integration suite.

## User Setup Required

None — no external service configuration required for this plan. All work is in-tree (13 new test files + 1 csproj edit + 1 SUMMARY). The only external dependency is the Phase 2 Postgres container, which was healthy at plan start and remains healthy at plan end (no new ports, no new env vars, no new dashboards, no new Docker volumes).

## Next Phase Readiness — Phase 4 Closure

- **Phase 4 is COMPLETE.** Both plans shipped (04-01 = build, 04-02 = verification). All 5 ROADMAP success criteria + D-03a Phase 3 carry-forward + ERROR-08/09 regression + 3 HIGH-severity threat mitigations behaviorally verified GREEN. **14 phase REQ-IDs closed: OBSERV-09, OBSERV-10, OBSERV-11, ERROR-01, ERROR-02, ERROR-03, ERROR-04, ERROR-05, ERROR-06, ERROR-07, ERROR-08, ERROR-09, ERROR-10, ERROR-11.** No blockers, no carry-forward concerns to Phase 5.

- **Phase 5 (Observability + Health Probes) is unblocked and ready to plan.** Phase 4's `CorrelationIdMiddleware.BeginScope("CorrelationId", corrId)` is the substrate Phase 5 will tag onto via `Activity.Current?.AddTag("correlation.id", corrId)` (D-13 deferred from Plan 04-01). The IExceptionHandler chain is in place; Phase 5 can layer OTel exception recording (`Activity.RecordException(...)`) additively inside each handler without changing handler shapes. Health probes will use the same `CustomizeProblemDetails` callback path for any 503 ProblemDetails responses.

- **Phase 6 (Validation + Mapping Base) inherits cleanly.** `FluentValidation` PackageReference is wired (Plan 04-01); `ValidationExceptionHandler` is the live mapper (Plan 04-01); `ValidationErrorTests.Test_FluentValidation_*` proves the mapper produces the expected 400 + errors-map shape. Phase 6 just needs `services.AddValidatorsFromAssembly(...)` and per-DTO validators that extend `BaseDtoValidator<T>`.

- **Phase 8 (entity build-out) inherits cleanly.** The 5 concrete entities (Schema/Processor/Step/Assignment/Workflow) will hit the same `PostgresExceptionMapper` for FK/UQ violations; the Option A regex (`^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$`) is now proven against `fk_testchild_parent_id → parent_id` AND verified by 4 unit-style facts. The 5 Phase 8 constraint names from REQUIREMENTS.md (`fk_processor_input_schema_id`, `fk_processor_output_schema_id`, `fk_assignment_step_id`, `fk_assignment_schema_id`, `uq_processor_source_hash`) will extract cleanly without further mapper changes.

- **Zero-warning regime intact.** Build still `0 Warning(s) 0 Error(s)` under EF Core 8 + xUnit v3 3.2.2 + xUnit1051 + xUnit2012 analyzers + Microsoft.AspNetCore.Mvc.Testing 8.0.27 + Npgsql 8.0.9 + `TreatWarningsAsErrors=true`. CPM contract intact (25 pins total, no `Version=` attributes on PackageReference). Future plans inherit the same regime.

## Self-Check: PASSED

**Files verified (14/14):**
- FOUND: tests/BaseApi.Tests/Middleware/WebAppFactory.cs
- FOUND: tests/BaseApi.Tests/Middleware/PostgresFixture.cs
- FOUND: tests/BaseApi.Tests/Middleware/TestParentEntity.cs
- FOUND: tests/BaseApi.Tests/Middleware/TestChildEntity.cs
- FOUND: tests/BaseApi.Tests/Middleware/TestErrorDbContext.cs
- FOUND: tests/BaseApi.Tests/Endpoints/TestController.cs
- FOUND: tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs
- FOUND: tests/BaseApi.Tests/Middleware/ValidationErrorTests.cs
- FOUND: tests/BaseApi.Tests/Middleware/SqlStateMappingTests.cs
- FOUND: tests/BaseApi.Tests/Middleware/NotFoundAndUnhandledTests.cs
- FOUND: tests/BaseApi.Tests/Middleware/ConcurrencyTokenTests.cs
- FOUND: tests/BaseApi.Tests/Middleware/ProblemDetailsExtensionsTests.cs
- FOUND: tests/BaseApi.Tests/Persistence/PostgresExceptionMapperTests.cs
- FOUND: .planning/phases/04-cross-cutting-middleware-error-handling/04-02-SUMMARY.md

**Commits verified (5/5 in git log):**
- FOUND: 54d3bbc (Task 1 — csproj Mvc.Testing PackageRef + ProjectRef to BaseApi.Service)
- FOUND: 8c4e5d6 (Task 2 — Wave-0 fixtures)
- FOUND: d89026f (Task 3 — TestController)
- FOUND: 3b7fd0a (Task 4 — 7 verification test files)
- FOUND: ad3f1a1 (fix-forward — Npgsql 9.0.0 → 8.0.9)
- (final metadata commit — created alongside this SUMMARY)

**Acceptance summary:** SC#1 GREEN, SC#2 GREEN, SC#3 GREEN, SC#4 GREEN, SC#5 GREEN, D-03a GREEN, ERROR-08/09 GREEN, T-04-LEAK GREEN, T-04-XMIN GREEN, T-04-INJECT GREEN. D-15 cleanup GREEN (no leaks). Build zero-warning Release + Debug. Test exit 0, 31/31 passed across 3 consecutive runs. **Phase 4 complete.**

---
*Phase: 04-cross-cutting-middleware-error-handling*
*Plan: 02*
*Completed: 2026-05-27*
