---
phase: 4
slug: cross-cutting-middleware-error-handling
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-27
---

# Phase 4 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Planner fills the Per-Task Verification Map as plans materialize; checker verifies Wave 0 / sampling continuity / no-watch-flag invariants.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 + Microsoft.AspNetCore.Mvc.Testing 8.0.27 (Phase 1 D-13) + Npgsql 8.0.10 |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (existing — adds nothing in Wave 0; new test files only) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Middleware" --nologo -v minimal` |
| **Full suite command** | `dotnet test --nologo -v minimal` |
| **Estimated runtime** | ~30–60s full suite (Phase 3 baseline ~20s + 6 new fact-test files with per-class throwaway Postgres DBs at localhost:5433) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet build -c Release --nologo -v minimal` (Plan 04-01 build tasks) OR the quick command above (Plan 04-02 verification tasks)
- **After every plan wave:** Run full suite
- **Before `/gsd-verify-work`:** Full suite must be green; BEFORE/AFTER `psql \l` snapshots must be byte-identical (Phase 3 D-15 cleanup discipline)
- **Max feedback latency:** 60s

---

## Per-Task Verification Map

> Planner fills this from the final PLAN.md task list. Each task gets one row. Wave 0 rows pre-populated below from research's Validation Architecture (RESEARCH.md § Validation Architecture).

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 04-01-XX | 01 | 1 | OBSERV-09, OBSERV-10, OBSERV-11, ERROR-01..11 | T-04-XX | Build green, 0 warnings | build | `dotnet build -c Release --nologo` | ❌ W0 | ⬜ pending |
| 04-02-XX | 02 | 2 | OBSERV-09 (CorrelationId echo) | — | X-Correlation-Id echoed on 2xx/4xx/5xx | integration | `dotnet test --filter "FullyQualifiedName~CorrelationIdTests"` | ❌ W0 | ⬜ pending |
| 04-02-XX | 02 | 2 | ERROR-03, ERROR-10 (Validation 400 + model-binding 400) | — | ProblemDetails shape parity between FluentValidation 400 and [ApiController] model-binding 400 | integration | `dotnet test --filter "FullyQualifiedName~ValidationErrorTests"` | ❌ W0 | ⬜ pending |
| 04-02-XX | 02 | 2 | ERROR-04, ERROR-05, ERROR-11 (SQLSTATE 23503/23505 mapping) | T-04-FK, T-04-UQ | 23503→422 + col name in detail; 23505→409 + col name | integration | `dotnet test --filter "FullyQualifiedName~SqlStateMappingTests"` | ❌ W0 | ⬜ pending |
| 04-02-XX | 02 | 2 | ERROR-01, ERROR-02, ERROR-07 (NotFound 404 + Fallback 500) | T-04-LEAK | 500 body has NO stack trace; stack logged only | integration | `dotnet test --filter "FullyQualifiedName~NotFoundAndUnhandledTests"` | ❌ W0 | ⬜ pending |
| 04-02-XX | 02 | 2 | ERROR-06 (DbUpdateConcurrencyException → 409; D-03a Phase 3 carry-forward) | T-04-XMIN | xmin value NOT exposed; generic message only | integration | `dotnet test --filter "FullyQualifiedName~ConcurrencyTokenTests"` | ❌ W0 | ⬜ pending |
| 04-02-XX | 02 | 2 | ERROR-08, ERROR-09 (correlationId + instance extensions on every error response) | — | Every non-2xx response has `correlationId` AND `instance` ProblemDetails extension fields | integration | `dotnet test --filter "FullyQualifiedName~ProblemDetailsExtensionsTests"` | ❌ W0 | ⬜ pending |
| 04-02-XX | 02 | 2 | ERROR-11 (constraint-name regex paths) | — | PostgresExceptionMapper.TryMap returns expected (status, detail, column) for valid + null-inner + non-matching constraint inputs | unit | `dotnet test --filter "FullyQualifiedName~PostgresExceptionMapperTests"` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*
*Task IDs are placeholders — planner replaces with actual XX numbers as plans are written.*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` — `WebApplicationFactory<Program>` wrapper with seam to register test endpoints; lifts test-controller assembly part (research Q3)
- [ ] `tests/BaseApi.Tests/Middleware/PostgresFixture.cs` — `IAsyncLifetime` per-class throwaway DB (`stepsdb_test_{Guid:N}` + `ClearAllPools` + `DROP DATABASE WITH FORCE`); lifts Phase 3 03-02 pattern verbatim
- [ ] `tests/BaseApi.Tests/Endpoints/TestController.cs` — `[ApiController] [Route("test")]` with deliberately-throwing endpoints (GET /test/not-found, POST /test/validation-error with [Required] DTO, POST /test/fk-violation, POST /test/unique-violation, POST /test/concurrency, GET /test/unhandled)
- [ ] `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` — stubs for OBSERV-09 (echo on 2xx/4xx/5xx + verbatim echo of supplied header + 32-char hex generation when missing)
- [ ] `tests/BaseApi.Tests/Middleware/ValidationErrorTests.cs` — stubs for ERROR-03 + ERROR-10 (shape parity)
- [ ] `tests/BaseApi.Tests/Middleware/SqlStateMappingTests.cs` — stubs for ERROR-04, ERROR-05, ERROR-11
- [ ] `tests/BaseApi.Tests/Middleware/NotFoundAndUnhandledTests.cs` — stubs for ERROR-01, ERROR-02, ERROR-07
- [ ] `tests/BaseApi.Tests/Middleware/ConcurrencyTokenTests.cs` — stubs for ERROR-06 (D-03a)
- [ ] `tests/BaseApi.Tests/Middleware/ProblemDetailsExtensionsTests.cs` — stubs for ERROR-08, ERROR-09
- [ ] `tests/BaseApi.Tests/Persistence/PostgresExceptionMapperTests.cs` — unit-test stubs for the static mapper (regex paths + null-inner fallthrough)

*Framework install: NONE — xUnit v3 + Microsoft.AspNetCore.Mvc.Testing 8.0.27 + Npgsql already pinned in `Directory.Packages.props` (Phase 1 D-05/D-13, Phase 3 D-12). Plan 04-01 adds explicit `<PackageReference Include="Npgsql" />` to `BaseApi.Core.csproj` per research recommendation (one new pin to `Directory.Packages.props`); plus `<PackageReference Include="FluentValidation" />` (D-10).*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| BEFORE/AFTER `psql \l` snapshot byte-identical | Phase 3 D-15 cleanup discipline (cross-phase invariant) | psql output isn't easily diff-asserted from within xUnit; verifying byte-identity is a human eyeball check at the SUMMARY commit | 1. `docker exec stepsdb-postgres psql -U postgres -c '\l' > before.txt` 2. Run full suite 3. `docker exec stepsdb-postgres psql -U postgres -c '\l' > after.txt` 4. `diff before.txt after.txt` — must be empty |
| Stack trace appears in structured log AND NOT in 500 response body (ERROR-07 information-disclosure) | ERROR-07 | xUnit can assert response body absence, but verifying the structured log contains the stack while body doesn't requires looking at log output | After triggering `GET /test/unhandled`, eyeball console output: must see "Unhandled exception on /test/unhandled" with stack frames; HTTP body must NOT contain "at BaseApi" or filename:line refs. (Optional follow-on: `ProblemDetailsExtensionsTests` can do the body assertion automatically; the log half stays manual.) |

*If none: "All phase behaviors have automated verification."*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify (build / `dotnet test --filter`) or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references (10 new test files listed above)
- [ ] No watch-mode flags (`dotnet test` is one-shot; no `--watch`)
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter (planner flips this after Plan 04-02 fills the verification map)

**Approval:** pending
