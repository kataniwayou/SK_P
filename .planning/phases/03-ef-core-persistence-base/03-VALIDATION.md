---
phase: 3
slug: ef-core-persistence-base
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-26
---

# Phase 3 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 3.2.2 (Microsoft.Testing.Platform runner) |
| **Config file** | None — defaults to per-class parallelization, per-method sequencing |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Phase03"` |
| **Full suite command** | `dotnet test` from repo root |
| **Estimated runtime** | ~25 seconds (4-6 facts, throwaway DB create/drop per class) |

**Pre-conditions:**
- Phase 2 Postgres container running at `localhost:5433` (`docker compose up -d postgres`) before any test invocation
- `appsettings.Development.json` connection-string present (Phase 2 D-02)
- `Directory.Packages.props` pin added for `Microsoft.Extensions.TimeProvider.Testing 8.10.0`

---

## Sampling Rate

- **After every task commit (Plan 03-01):** `dotnet build -c Release` exit 0, zero warnings (TreatWarningsAsErrors enforces).
- **After every task commit (Plan 03-02):** Quick run command above (Phase03-filtered facts).
- **After every plan wave:** `dotnet test` (full suite — includes MetaTest sanity + Phase 3 facts).
- **Before `/gsd-verify-work`:** Full suite must be green; SUMMARY documents `dotnet test` output verbatim.
- **Max feedback latency:** ~25 seconds per quick run; ~40 seconds full suite.

---

## Per-Task Verification Map

> Task IDs follow `{phase}-{plan}-{task}` shape. Final task IDs are assigned at planning time; the table below maps Validation Dimensions to plans and requirements.

| Dim | Plan | Wave | Requirement(s) | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|-----|------|------|----------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 1. Zero-warning Build | 03-01 | 1 | ENTITY-01, ENTITY-02, PERSIST-02..07, PERSIST-11, PERSIST-15 (all compile) | T-3-01 | Build fails closed on any warning (TreatWarningsAsErrors) | build | `dotnet build -c Release /warnaserror` | ✅ (Phase 1) | ⬜ pending |
| 2. Schema Mapping (snake_case) | 03-02 | 2 | PERSIST-05, ENTITY-01, ENTITY-02 (SC#1) | T-3-02 | Schema deterministically lowercased; no PascalCase columns reach Postgres | integration | `dotnet test --filter "FullyQualifiedName~SchemaTests"` | ❌ W0 | ⬜ pending |
| 3. UTC Timestamp Stamping | 03-02 | 2 | PERSIST-03, PERSIST-07 (SC#2) | T-3-03 | All timestamps Kind=Utc; no `InvalidCastException` against `timestamptz` | integration | `dotnet test --filter "FullyQualifiedName~AuditInterceptorTests.*Utc"` | ❌ W0 | ⬜ pending |
| 4. HttpContext CreatedBy + Null Fallback | 03-02 | 2 | PERSIST-04 (SC#3) | T-3-04 | `created_by` populated when `User.Identity.Name` present; null and no crash when `HttpContext` null | integration | `dotnet test --filter "FullyQualifiedName~AuditInterceptorTests.*CreatedBy"` | ❌ W0 | ⬜ pending |
| 5. DbContext Scoped Lifetime | 03-02 | 2 | PERSIST-15 (SC#4) | T-3-05 | DI guarantees per-request DbContext isolation; no cross-request leakage | unit | `dotnet test --filter "FullyQualifiedName~DiLifetimeTests"` | ❌ W0 | ⬜ pending |
| 6. xmin Concurrency Token Wiring | 03-02 | 2 | PERSIST-16 (new, D-03b) | T-3-06 | Lost-update race terminates with EF `DbUpdateConcurrencyException` (Phase 4 maps to 409) | model-introspection | `dotnet test --filter "FullyQualifiedName~XminConcurrencyTokenTests"` | ❌ W0 | ⬜ pending |
| 7. Repository Surface | 03-01 | 1 | PERSIST-11 | T-3-07 | No `IQueryable` leakage prevents unbounded scan / N+1 from service layer | unit (reflection) | `dotnet test --filter "FullyQualifiedName~RepositorySurfaceTests"` | ❌ W0 | ⬜ pending |
| 8. Throwaway DB Lifecycle | 03-02 | 2 | D-15, Pitfall 26 (informational) | T-3-08 | No leaked test DBs; container resource use stable across runs | manual + automated dispose | `docker compose exec postgres psql -U postgres -c "\l"` (snapshot before / after) | ✅ (manual) | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

> Threat refs `T-3-01..T-3-08` are placeholders for the Phase 3 PLAN-level `<threat_model>` block — planner populates them. The security-enforcement gate (workflow step 5.55) injects the requirement to surface threats per task; if a dimension has no security-relevant threat, the planner sets `Threat Ref = —`.

---

## Wave 0 Requirements

> All test files are new — no existing test files to mirror beyond `tests/BaseApi.Tests/MetaTest.cs`. Wave 0 is created inside Plan 03-02 as the first set of tasks (test scaffold) before the assertion facts.

- [ ] `tests/BaseApi.Tests/Persistence/TestEntity.cs` — trivial `BaseEntity` subclass (test-only)
- [ ] `tests/BaseApi.Tests/Persistence/TestDbContext.cs` — concrete `BaseDbContext` with `DbSet<TestEntity>`
- [ ] `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` — `IAsyncLifetime` throwaway DB lifecycle (`CREATE DATABASE` in `InitializeAsync`, `ClearAllPools` + `DROP DATABASE ... WITH (FORCE)` in `DisposeAsync`)
- [ ] `tests/BaseApi.Tests/Persistence/StubHttpContextAccessor.cs` — handwritten `IHttpContextAccessor` test double (no Moq/NSubstitute — REQ TEST-04 out of scope)
- [ ] `tests/BaseApi.Tests/Persistence/SchemaTests.cs` — Dim 2 (SC#1)
- [ ] `tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs` — Dim 3 + Dim 4 (SC#2 + SC#3)
- [ ] `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs` — Dim 5 (SC#4)
- [ ] `tests/BaseApi.Tests/Persistence/XminConcurrencyTokenTests.cs` — Dim 6 (PERSIST-16, recommended)
- [ ] `tests/BaseApi.Tests/Persistence/RepositorySurfaceTests.cs` — Dim 7 (PERSIST-11 surface, recommended)
- [ ] `Directory.Packages.props` — add pin `<PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="8.10.0" />` (Wave 0 install)
- [ ] `tests/BaseApi.Tests/BaseApi.Tests.csproj` — add `<PackageReference>` entries for EF Core/Npgsql/EFCore.NamingConventions/TimeProvider.Testing + `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (for `DefaultHttpContext` in stub)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Throwaway test DBs do not leak across runs | D-15, Pitfall 26 | Cross-run state observable only by listing Postgres DBs from outside the test process | (1) Before run: `docker compose exec postgres psql -U postgres -c "\l"` — record DB list. (2) Run `dotnet test`. (3) After run: same `psql -l` — diff should show no `stepsdb_test_*` survivors. (4) Document both snapshots verbatim in 03-02 SUMMARY. |
| `dotnet test` exit code is 0 with all Phase 3 facts present | SC#1-4, PERSIST-* | Phase-gate sanity — automated facts assert outcome but human eyeballs the test summary | Run `dotnet test` from repo root; copy the final `Passed!  - Failed: 0, Passed: N, Skipped: 0, Total: N` line into 03-02 SUMMARY. |
| Plan 03-01 final `dotnet build -c Release` produces zero warnings | Phase 1 D-02, all PERSIST-* compile | Warning count human-verifiable from console; CI not in place yet | After Plan 03-01 final task: `dotnet build -c Release` — confirm no `warning CSxxxx` lines; copy build summary into 03-01 SUMMARY. |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references (10 files listed above)
- [ ] No watch-mode flags (`--watch` forbidden for phase-gate runs)
- [ ] Feedback latency < 60s (quick run ~25s, full ~40s)
- [ ] `nyquist_compliant: true` set in frontmatter after planner verifies Per-Task Verification Map task IDs

**Approval:** pending
