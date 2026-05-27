---
phase: 04-cross-cutting-middleware-error-handling
fixed_at: 2026-05-27T00:00:00Z
review_path: .planning/phases/04-cross-cutting-middleware-error-handling/04-REVIEW.md
iteration: 2
findings_in_scope: 7
fixed: 7
skipped: 0
status: all_fixed
fix_scope: all
---

# Phase 4: Code Review Fix Report

**Fixed at:** 2026-05-27T00:00:00Z
**Source review:** .planning/phases/04-cross-cutting-middleware-error-handling/04-REVIEW.md
**Iteration:** 2 (cumulative — covers all 7 findings across both iterations)

**Summary:**
- Findings in scope: 7 (WR-01, WR-02, WR-03, IN-01, IN-02, IN-03, IN-04)
- Fixed: 7
- Skipped: 0

---

## Iteration 1 — Warning Fixes (fix_scope: critical_warning)

### WR-01: FallbackExceptionHandler catch-all can return `false` if response has already started

**Files modified:** `src/BaseApi.Core/Exceptions/Handlers/FallbackExceptionHandler.cs`
**Commit:** fa1ae7f
**Applied fix:** Changed `return await _pdSvc.TryWriteAsync(...)` to discard the
`TryWriteAsync` return value (`_ = await ...`) and unconditionally `return true` after
the write attempt. Added an inline comment explaining the catch-all contract: if headers
are already committed and `TryWriteAsync` returns `false`, the handler still claims the
exception so `UseExceptionHandler` does not re-throw or produce a blank Kestrel error.

---

### WR-02: `EnsureCreatedAsync` called per-request inside `TestController` — concurrent schema creation race

**Files modified:** `tests/BaseApi.Tests/Middleware/PostgresFixture.cs`, `tests/BaseApi.Tests/Endpoints/TestController.cs`
**Commit:** 648d9e0
**Applied fix:** Added schema pre-creation at the end of `PostgresFixture.InitializeAsync`
using a freshly constructed `DbContextOptionsBuilder<TestErrorDbContext>().UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention()` instance and `EnsureCreatedAsync()`. Removed the three per-request `await db.Database.EnsureCreatedAsync(ct)` calls from `FkViolation`, `UniqueViolation`, and `Concurrency` handlers in `TestController`. The `DisposeAsync` cleanup (`ClearAllPools` + `DROP DATABASE IF EXISTS ... WITH (FORCE)`) is preserved verbatim — the schema is dropped along with the database.

---

### WR-03: `PostgresExceptionMapper` regex extracts full suffix for multi-column compound constraints

**Files modified:** `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs`
**Commit:** e6d1b07
**Applied fix:** Added two documentation blocks without changing the regex:
1. An inline comment block above `FkRegex` and `UqRegex` declarations explicitly stating
   the INVARIANT — table segment MUST be a single word with NO underscores — with an
   explanation of the boundary-parsing behaviour and a reference to WR-03 and ERROR-11.
2. An XML `<summary>` doc comment on `TryMap` describing the constraint-name format
   invariant for callers, noting that Phase 8 entity table names will be enforced to
   comply via the EFCore.NamingConventions snake_case mapping. The regex itself is
   unchanged to preserve the current tested contract.

---

## Iteration 2 — Info Fixes (fix_scope: all)

### IN-01: `CorrelationIdMiddleware` pipeline placement comment is imprecise

**Files modified:** `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs`
**Commit:** 635085b
**Applied fix:** Updated the `<b>Pipeline placement (D-01):</b>` paragraph in the class
XML doc comment. Replaced "registered AFTER `UseExceptionHandler` so the IExceptionHandler
chain ... can read `HttpContext.Items["CorrelationId"]`" with the more precise phrasing:
"registered as the second middleware (after `UseExceptionHandler`), so it runs INSIDE the
exception handler's try-catch. When an endpoint throws, `CorrelationIdMiddleware` has
already populated `HttpContext.Items["CorrelationId"]` — the IExceptionHandler chain reads
it directly from `HttpContext.Items` via the ProblemDetails customizer (D-04)." This
eliminates the registration-order vs execution-order ambiguity flagged by IN-01.

---

### IN-02: `NotFoundExceptionHandler` includes `nfx.Message` verbatim — document contract at `NotFoundException`

**Files modified:** `src/BaseApi.Core/Exceptions/NotFoundException.cs`
**Commit:** 427c1fd
**Applied fix:** Added an XML `<param name="resourceType">` and `<param name="id">` doc
block to the `NotFoundException(string resourceType, object id)` constructor. The `id`
param comment warns that the value is included verbatim in the HTTP 404 response body
(`detail` and `resourceId` extension field) and that callers MUST NOT pass raw DB keys,
file paths, or user-supplied strings — only safe client-visible identifiers such as
`Guid` or numeric ids. No behavior change; documentation-only.

---

### IN-03: `TestController.UniqueViolation` uses `[FromQuery] string name` without length constraint — mark as test-only

**Files modified:** `tests/BaseApi.Tests/Endpoints/TestController.cs`
**Commit:** 85a9657
**Applied fix:** Prepended a prominent warning to the existing class-level `<summary>` XML
doc comment: "Deliberately-throwing endpoints used ONLY by Phase 4 verification
(WebApplicationFactory integration tests). NOT FOR PRODUCTION USE — parameters are
unbounded and inputs are unvalidated." The pre-existing endpoint coverage table and
`WebAppFactory` discovery note are preserved, now wrapped in a `<para>` block. No
behavior change; documentation-only.

---

### IN-04: `Testcontainers.PostgreSql` is pinned in `Directory.Packages.props` but no csproj references it

**Files modified:** `Directory.Packages.props`
**Commit:** 0bad9a8
**Applied fix:** Added the inline XML comment `<!-- Reserved for Phase 8 integration tests
(TEST-01..06); currently unreferenced. -->` immediately above the
`<PackageVersion Include="Testcontainers.PostgreSql" Version="4.11.0" />` line. Future
phase developers will immediately see that the pin is intentional and targeted at Phase 8,
preventing confusion about whether the package is already consumed or needs a new
`PackageReference`.

---

## Validation

`dotnet build -c Release`: 0 warnings, 0 errors (TreatWarningsAsErrors=true satisfied — all XML doc comments are syntactically valid).
`dotnet test --nologo -v minimal`: 31/31 passed.

---

_Fixed: 2026-05-27T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 2_
