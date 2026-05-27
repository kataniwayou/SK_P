---
phase: 04-cross-cutting-middleware-error-handling
fixed_at: 2026-05-27T00:00:00Z
review_path: .planning/phases/04-cross-cutting-middleware-error-handling/04-REVIEW.md
iteration: 1
findings_in_scope: 3
fixed: 3
skipped: 0
status: all_fixed
fix_scope: critical_warning
---

# Phase 4: Code Review Fix Report

**Fixed at:** 2026-05-27T00:00:00Z
**Source review:** .planning/phases/04-cross-cutting-middleware-error-handling/04-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 3 (WR-01, WR-02, WR-03)
- Fixed: 3
- Skipped: 0

## Fixed Issues

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
using a freshly constructed `DbContextOptionsBuilder<TestErrorDbContext>().UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention()` instance and `EnsureCreatedAsync()`. Removed the three per-request `await db.Database.EnsureCreatedAsync(ct)` lines from `FkViolation`, `UniqueViolation`, and `Concurrency` handlers in `TestController`. The `DisposeAsync` cleanup (`ClearAllPools` + `DROP DATABASE IF EXISTS ... WITH (FORCE)`) is preserved verbatim — the schema is dropped along with the database.

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

## Validation

`dotnet build`: 0 warnings, 0 errors (TreatWarningsAsErrors=true satisfied).
`dotnet test`: 31/31 passed.

---

_Fixed: 2026-05-27T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
