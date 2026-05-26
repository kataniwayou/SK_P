# Phase 3: EF Core Persistence Base - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-26
**Phase:** 03-ef-core-persistence-base
**Areas discussed:** Guid generation strategy, Concurrency token (xmin shadow), Repository<T> surface & SaveChanges ownership, AuditInterceptor clock + verification target

---

## Gray Area Selection

User selected ALL FOUR offered gray areas: Guid generation strategy, Concurrency token (xmin shadow), Repository<T> surface & SaveChanges ownership, AuditInterceptor clock + verification target.

---

## Area 1: Guid generation strategy

### Q1 — Initial framing (rejected by user, asked for explanation)

| Option | Description | Selected |
|--------|-------------|----------|
| Client-side via AuditInterceptor | Stamp Id = Guid.NewGuid() on Added if Empty | (deferred to Q1b) |
| Client-side via BaseEntity constructor | Id = Guid.NewGuid() in ctor | (deferred to Q1b) |
| DB-side default | `gen_random_uuid()` + ValueGeneratedOnAdd | (deferred to Q1b) |
| Hybrid | DB default + client override | (deferred to Q1b) |

**Outcome:** User asked Claude to explain *why* DB-side was not recommended. Claude responded with three project-specific reasons:
1. Junction-table inserts force atomicity problems (Phase 8 friction) — scalar FK columns + no nav properties + DB-side Guid means workflow.Id == Guid.Empty until INSERT executes, breaking single-transaction junction sync.
2. Audit symmetry — AuditInterceptor already runs on EntityState.Added; stamping Id there keeps every server-controlled field in one code path.
3. Service + log ergonomics — Id is available pre-SaveChanges for logging and pre-commit DTO returns.

Claude also acknowledged what's NOT a real reason (pgcrypto extension; PG 17 has gen_random_uuid in core).

### Q1b — After explanation

| Option | Description | Selected |
|--------|-------------|----------|
| **Client-side via AuditInterceptor** | Interceptor stamps Id on Added if Empty; single source of generation; junctions work atomically | **✓** |
| Client-side via BaseEntity constructor | Set in protected ctor | |
| DB-side default | `gen_random_uuid()` + ValueGeneratedOnAdd | |
| Hybrid | DB default + client override | |

**User's choice:** Client-side via AuditInterceptor (D-01).

### Q2 — Override behavior

| Option | Description | Selected |
|--------|-------------|----------|
| **Honor caller-set Id (only generate if Guid.Empty)** | Lets tests pin TestGuids.Workflow1; lets seeders use known constants; CreateDto excludes Id per HTTP-05 so production-path always Empty | **✓** |
| Always overwrite | Stricter; tests must read Id back after Add | |
| Honor caller-set + Debug log on default fill | Same as option 1 + log when filling Guid.Empty | |

**User's choice:** Honor caller-set Id (D-02).

---

## Area 2: Concurrency token (xmin shadow)

### Q3 — Wire xmin or defer

| Option | Description | Selected |
|--------|-------------|----------|
| **Wire shadow xmin now** | BaseDbContext.OnModelCreating iterates BaseEntity subclasses; adds `.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken()`; Phase 4 maps DbUpdateConcurrencyException → 409 | **✓** |
| Defer to v2 | Skip in v1; document as hardening item | |
| Skip permanently | Last-write-wins acceptable | |

**User's choice:** Wire shadow xmin now (D-03).

**Notes / rationale captured:**
- Wiring is essentially free at Phase 3 (one block in BaseDbContext OnModelCreating) but expensive to retrofit later (touches every entity mapping).
- The migration is generated AFTER Phase 3 (Phase 8 does InitialCreate), so wiring now costs zero migration churn.
- Junction entities (StepNextSteps, WorkflowEntrySteps, WorkflowAssignments) are excluded because they DELETE+INSERT as a set, never UPDATE.
- Cross-phase impact captured as D-03a (Phase 4 new error-mapping rule) and D-03b (new REQ PERSIST-16).

---

## Area 3: Repository<T> surface & SaveChanges ownership

### Q4 — Combined shape (4 sub-decisions in one question)

| Option | Description | Selected |
|--------|-------------|----------|
| **Tight + Service owns + : BaseEntity** | 5 methods only (Get/List/Add/Update/Delete), no IQueryable, no ExistsAsync; Service injects DbContext for SaveChanges + junction Set<T>() access; constraint `where TEntity : BaseEntity` | **✓** |
| Tight + Repository owns SaveChanges | Same 5 methods but each op commits; breaks multi-entity transaction semantics | |
| Tight + ExistsAsync helper | 6 methods including ExistsAsync; marginal v1 value | |
| Richer + IQueryable exposed | `IQueryable<TEntity> Query()` — rejected by FEATURES.md L70 | |

**User's choice:** Tight + Service owns + : BaseEntity (D-04, D-05).

**Notes / rationale captured:**
- Caller-owns-SaveChanges matches unit-of-work; lets Service compose multi-entity transactions (Workflow + junctions in Phase 8).
- Tight surface explicitly per FEATURES.md L70 ("Don't add IQueryable leakage").
- BaseEntity constraint narrows to audit-stamped entities; junctions go through raw DbContext.Set<TJunction>() in entity Service.

---

## Area 4: AuditInterceptor clock + verification target

### Q5 — Clock abstraction

| Option | Description | Selected |
|--------|-------------|----------|
| **TimeProvider (.NET 8)** | Inject TimeProvider; production wires `TimeProvider.System`; tests use FakeTimeProvider; idiomatic .NET 8+ | **✓** |
| DateTime.UtcNow direct | Simpler; tests assert "within last N seconds" | |
| Custom IClock interface | Pre-TimeProvider pattern | |

**User's choice:** TimeProvider (D-07).

### Q6 — Verification target

| Option | Description | Selected |
|--------|-------------|----------|
| **Existing BaseApi.Tests** | Add EF Core + Npgsql + EFCore.NamingConventions + Microsoft.Extensions.TimeProvider.Testing refs to existing xUnit v3 project; 4 fact tests for SC#1-4; targets running docker compose postgres at localhost:5433 | **✓** |
| New BaseApi.Core.Tests project | Cleaner separation per ARCHITECTURE.md; requires amending REQUIREMENTS.md INFRA-01 | |
| Scratch console (tests/BaseApi.Core.Smoke) | SC#1 permits "scratch console"; throwaway | |
| Both: console + xUnit | Two artifacts to maintain | |

**User's choice:** Existing BaseApi.Tests (D-14, D-15, D-16).

---

## Final Check

### Q7 — Ready for context or explore more?

| Option | Description | Selected |
|--------|-------------|----------|
| **I'm ready for context** | Write CONTEXT.md with D-01..D-18 plus canonical refs plus cross-phase impacts | **✓** |
| Explore more gray areas | Surface 2-3 additional gray areas (junction-table conventions, migration generation seam, non-HTTP context paths) | |
| Revisit an area | Pick one of the four areas to dig deeper | |

**User's choice:** I'm ready for context.

---

## Claude's Discretion

Areas where the user did not lock implementation specifics (planner has flexibility):

- Exact `Repository<T>.Update` signature: `void Update(TEntity entity)` (sync) vs `Task UpdateAsync(TEntity entity, CancellationToken ct)` (async for symmetry)
- Whether `Repository<T>` takes raw `DbContext` or a typed `TDbContext where TDbContext : BaseDbContext`
- Test database naming scheme (`stepsdb_test_{Guid:N}` vs alternatives)
- Whether the xmin shadow-property iteration filter is the clean `IsAssignableFrom` or a defensive variant excluding owned types
- Whether `TestEntity` lives in the test project or in BaseApi.Core via `InternalsVisibleTo`
- Whether to add an XML doc cross-reference from `BaseEntity.CreatedAt`/`UpdatedAt` to `AuditInterceptor` (strongly recommended but not load-bearing)
- FrameworkReference (`Microsoft.AspNetCore.App`) vs explicit package ref (`Microsoft.AspNetCore.Http.Abstractions`) for `IHttpContextAccessor` in `BaseApi.Core.csproj` — researcher resolves; planner picks

## Deferred Ideas

Captured in CONTEXT.md `<deferred>` section, summarized here:

- **To Phase 4:** `DbUpdateConcurrencyException` → 409 mapping (D-03a); correlation-id scope flowing into AuditInterceptor logs.
- **To Phase 5:** Startup probe flip after migrations; Npgsql tracing instrumentation.
- **To Phase 6:** `BaseDtoValidator<T>` with Name/Version/Description rules; Mapperly [Mapper] partial-class template.
- **To Phase 7:** `AddBaseApi<TDbContext>(IConfiguration)` composition root extension; API versioning + Swagger.
- **To Phase 8:** `AppDbContext` concrete + 5 entities + 3 junctions; InitialCreate migration; Testcontainers integration suite; migration runner with startup-probe gating.
- **To v2:** ExistsAsync helper on Repository<T> if hot-path; IQueryable exposure if query endpoints land; multi-instance migration with pg_advisory_lock; soft delete.
