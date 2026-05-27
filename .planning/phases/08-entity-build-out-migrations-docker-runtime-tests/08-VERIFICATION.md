---
phase: 08-entity-build-out-migrations-docker-runtime-tests
verified: 2026-05-28T00:00:00Z
status: human_needed
score: 13/13 must-haves verified
overrides_applied: 0
human_verification:
  - test: "Run the full test suite (dotnet test SK_P.sln) against a live Postgres instance and confirm 128 facts pass on 3 consecutive runs"
    expected: "128 Passed, 0 Failed on each of 3 consecutive runs (25 Wave B + 4 ErrorMapping + 1 MigrationFailure + 98 Phase 1-7)"
    why_human: "Test execution requires Docker Compose stack (Postgres on port 5433) and cannot be driven from a static code scan. The SUMMARY documents 3 consecutive GREEN runs (Runs 4/5/6) but these cannot be re-verified programmatically without running the test suite against the live stack."
  - test: "Verify byte-identical psql \\l BEFORE/AFTER snapshot holds after running the full suite"
    expected: "diff of psql-l-before-phase08.txt vs psql-l-after-phase08.txt produces no output (exit 0); SHA-256 of both files matches 0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127"
    why_human: "Snapshot comparison requires running psql against the live Docker container; cannot be verified statically. Artifact files exist at .planning/phases/.../artifacts/ but their validity depends on the live DB state matching the logged snapshot."
  - test: "Run docker compose up --build and confirm service boots and /health/live returns 200, then /api/v1/schemas returns 200 + []"
    expected: "Service container builds, migrations apply at startup, GET /api/v1/schemas returns HTTP 200 with body []"
    why_human: "Docker image build and runtime health require an active Docker daemon; cannot verify statically. SUMMARY claims compose.yaml + Dockerfile are correct and docker compose config resolves, but end-to-end runtime is only provable by execution."
---

# Phase 8: Entity Build-Out + Migrations + Docker Runtime + Tests Verification Report

**Phase Goal:** Ship the 5 v1 entities (Schema, Processor, Step, Assignment, Workflow) with EF Core persistence + InitialCreate migration + cross-entity error-mapping facts, satisfying SC#1..SC#6 from CONTEXT, locking in 3 consecutive GREEN runs of the full 128-fact suite, and proving zero leaked databases via byte-identical psql \l snapshot.

**Verified:** 2026-05-28T00:00:00Z
**Status:** human_needed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | SC#1: All 5 entities exist with correct BaseEntity inheritance and entity-specific fields | VERIFIED | SchemaEntity (Definition string), ProcessorEntity (SourceHash, InputSchemaId?, OutputSchemaId?), StepEntity (ProcessorId, EntryCondition), AssignmentEntity (StepId, SchemaId, Payload), WorkflowEntity (CronExpression?) — all sealed, all inherit BaseEntity, zero nav properties |
| 2 | SC#2: InitialCreate migration captures all 5 entities + 3 junctions + jsonb + xmin + 11 explicit FK constraint names | VERIFIED | Migration file `20260527203118_InitialCreate.cs` confirmed via grep: 11 FK constraint names (fk_processor_input/output_schema_id, fk_step_processor_id, fk_step_next_steps_step/next_step_id, fk_assignment_step/schema_id, fk_workflow_entry_steps_workflow/step_id, fk_workflow_assignments_workflow/assignment_id) + uq_processor_source_hash; jsonb on definition + payload columns; xmin on all 5 entity tables |
| 3 | SC#3: MigrateAsync applied at startup via StartupCompletionService swap; failure leaves process alive and readiness unhealthy | VERIFIED | StartupCompletionService.cs swapped body: try { scope + BaseDbContext + MigrateAsync + MarkReady } catch (Exception) { LogCritical, no rethrow, no MarkReady }; MigrationFailureFacts.cs asserts /health/live=200, /health/startup=503, /health/ready=503 |
| 4 | SC#4: Multistage Dockerfile + compose.yaml with default profile + 5-entity DI composition wired in Program.cs | VERIFIED | Dockerfile matches D-13 spec verbatim (sdk:8.0 build → aspnet:8.0 runtime, USER app, UseAppHost=false); compose.yaml has build:, ConnectionStrings__Postgres, ports 8080:8080, healthcheck, no phase-8 profile; Program.cs calls AddBaseApi<AppDbContext> + AddAppFeatures() |
| 5 | SC#5: 25 smoke facts (5×5 CRUD per entity) + 4 cross-entity error-mapping facts authored and structured | VERIFIED | 5 [Fact] per entity class (confirmed via standalone [Fact] count) = 25; ErrorMappingFacts.cs has 4 [Fact]: Create_Duplicate_SourceHash_Returns409, Create_Workflow_Non_Existent_EntryStepId_Returns422, Delete_Step_Referenced_By_Workflow_Returns422, Create_Schema_Invalid_JsonSchema_Returns400_NoOutboundCall |
| 6 | SC#6: Migration failure fact proves PERSIST-10 (process alive, readiness unhealthy) | VERIFIED | MigrationFailureFacts.cs + MigrationFailureWebAppFactory subclass (Port=5434); fact asserts /health/live=200 + /health/startup=503 + /health/ready=503 |
| 7 | PERSIST-01: AppDbContext exposes 5 entity DbSets + 3 junction DbSets | VERIFIED | AppDbContext.cs: Schemas, Processors, Steps, Assignments, Workflows + StepNextSteps, WorkflowEntrySteps, WorkflowAssignments |
| 8 | PERSIST-08: Definition (Schema) and Payload (Assignment) map to jsonb | VERIFIED | SchemaEntityConfiguration.HasColumnType("jsonb") on Definition; AssignmentEntityConfiguration.HasColumnType("jsonb") on Payload; migration confirms both as jsonb type |
| 9 | All 13 validators implement VALID-08..20 with correct rules and Include(BaseDtoValidator<T>) | VERIFIED | Schema: Dialect.Default=Draft202012 + SchemaRegistry.Global.Fetch=no-op + MetaSchemas.Draft202012.Evaluate; Processor: SHA-256 regex + nullable Guid.Empty guard; Step: ProcessorId NotEqual(Guid.Empty) + NextStepIds unique + EntryCondition IsInEnum; Assignment: StepId/SchemaId NotEqual + Payload MaxLength(1_048_576) + JsonDocument.Parse; Workflow: EntryStepIds NotEmpty+unique + AssignmentIds unique when present + CronExpression Cronos 5-field parse; all Include(BaseDtoValidator) |
| 10 | ENTITY-09: No navigation properties between entities | VERIFIED | All 5 entity files contain only scalar properties; no ICollection<T>, IList<T>, or virtual navigation properties; ENTITY-10 (Name+Version not unique) confirmed by absence of uniqueness config |
| 11 | PERSIST-12: StepNextSteps + WorkflowEntrySteps + WorkflowAssignments configured with composite PKs and FK constraints | VERIFIED | All 3 junction configurations confirmed; composite PK patterns; cascade behaviors: StepNextSteps both-Restrict; WorkflowEntrySteps Workflow-Cascade/Step-Restrict (SC#5 load-bearing); WorkflowAssignments Workflow-Cascade/Assignment-Restrict |
| 12 | PERSIST-14: Unique index on Processor.SourceHash with name uq_processor_source_hash | VERIFIED | ProcessorEntityConfiguration.HasIndex(e => e.SourceHash).IsUnique().HasDatabaseName("uq_processor_source_hash"); confirmed in migration |
| 13 | TEST-03+04 correctly deferred to v2 in REQUIREMENTS.md with D-05/D-06 reasoning | VERIFIED | REQUIREMENTS.md: TEST-03/04 removed from v1 Testing, added to v2 Hardening with verbatim D-05/D-06 paragraphs; traceability table shows v2/Deferred; Phase 8 row shows 39 requirements |

**Score:** 13/13 truths verified (automated checks)

**Human verification required for 3 additional runtime truths:**
- 3 consecutive 128-fact GREEN runs (claimed in SUMMARY, requires live stack to re-verify)
- Byte-identical psql \l BEFORE/AFTER snapshot (artifacts exist, require live stack validation)
- End-to-end docker compose up + service boot + /api/v1/schemas round-trip

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `Dockerfile` | Multistage sdk:8.0 → aspnet:8.0, USER app, UseAppHost=false | VERIFIED | Matches D-13 spec verbatim; USER app; ENTRYPOINT dotnet BaseApi.Service.dll |
| `.dockerignore` | Excludes tests/, bin, obj, .git, .planning/, *.md | VERIFIED | All required exclusions present |
| `compose.yaml` | baseapi-service with build:., env, ports, healthcheck, no phase-8 profile | VERIFIED | build: context + dockerfile; ConnectionStrings__Postgres; 8080:8080; /health/ready healthcheck; no phase-8 profile; otel-collector service_started |
| `.config/dotnet-tools.json` | dotnet-ef 8.0.27 | VERIFIED | {"version": "8.0.27"} for dotnet-ef |
| `src/BaseApi.Service/BaseApi.Service.csproj` | Riok.Mapperly + EFCore.Design + JsonSchema.Net + Cronos, no Version= | VERIFIED | All 4 PackageReferences present; no Version= attributes (CPM contract); PrivateAssets on Mapperly and EFCore.Design |
| `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` | public class, IAsyncLifetime, PostgresFixture encapsulated, protected ctor for override | VERIFIED | public class (not sealed); IAsyncLifetime; _fixture field; protected Phase8WebAppFactory(string); ConnectionStrings:Postgres injection; no Phase7TestDbContext rewiring |
| `src/BaseApi.Service/Features/Schema/` (7 files) | Entity, Dtos, Mapper, Validator, Controller, Service, DI Extensions | VERIFIED | All 7 files exist and are substantive; SchemaEntity+Definition; SchemaReadDto:IHasId; [Mapper] partial; static ctor VALID-08/09; empty controller; passthrough service; AddSchemaFeature |
| `src/BaseApi.Service/Features/Processor/` (7 files) | Entity, Dtos, Mapper, Validator, Controller, Service, DI Extensions | VERIFIED | All files exist; SourceHash+InputSchemaId?+OutputSchemaId?; VALID-10 SHA-256 regex; VALID-11 nullable Guid.Empty guard; uq_processor_source_hash EF config |
| `src/BaseApi.Service/Features/Step/` (9 files) | Entity+Enum+Junction, Dtos, Mapper, Validator, Controller, Service (override), DI Extensions + EF configs (2) | VERIFIED | StepEntity+StepEntryCondition(6 values)+StepNextSteps; StepService overrides SyncJunctionsAsync; StepNextStepsConfiguration (both Restrict); VALID-12/13/14 |
| `src/BaseApi.Service/Features/Assignment/` (7 files) | Entity, Dtos, Mapper, Validator, Controller, Service, DI Extensions | VERIFIED | AssignmentEntity+StepId+SchemaId+Payload; VALID-15+16; Payload jsonb; both FKs Restrict |
| `src/BaseApi.Service/Features/Workflow/` (10 files) | Entity+2 junctions, Dtos, Mapper, Validator, Controller, Service (override), DI Extensions + EF configs (3) | VERIFIED | WorkflowEntity+WorkflowEntrySteps+WorkflowAssignments; WorkflowService overrides SyncJunctionsAsync (dual junction); VALID-17/18/19; Cascade/Restrict asymmetric FKs |
| `src/BaseApi.Service/Persistence/Configurations/` (8 files) | 5 entity configs + 3 junction configs | VERIFIED | All 8 files present; explicit FK names; jsonb; composite PKs; cascade behaviors |
| `src/BaseApi.Service/Persistence/Migrations/20260527203118_InitialCreate.cs` | All 5 entities + 3 junctions + jsonb + xmin + 11 FK names + uq index | VERIFIED | Confirmed: 11 FK constraint names + uq_processor_source_hash + jsonb on definition/payload + xmin on all entity tables |
| `src/BaseApi.Service/Composition/AppFeatures.cs` | AddAppFeatures aggregator composing all 5 per-entity DI extensions | VERIFIED | Internal static class; calls AddSchemaFeature + AddProcessorFeature + AddStepFeature + AddAssignmentFeature + AddWorkflowFeature |
| `src/BaseApi.Service/AppDbContext.cs` | 5 entity DbSets + 3 junction DbSets + OnModelCreating ordering | VERIFIED | ApplyConfigurationsFromAssembly FIRST; base.OnModelCreating LAST (load-bearing for xmin); all 8 DbSets present |
| `src/BaseApi.Core/Health/StartupCompletionService.cs` | D-15 swap: MigrateAsync + try/catch/LogCritical/no-rethrow contract | VERIFIED | 3-param ctor; IServiceScopeFactory scope; BaseDbContext alias; MarkReady inside try; catch swallows + LogCritical; no rethrow |
| `src/BaseApi.Service/Program.cs` | AddBaseApi<AppDbContext> + AddAppFeatures() wired | VERIFIED | Both calls present; under 10 non-trivial body lines |
| `tests/BaseApi.Tests/Integration/SchemasIntegrationTests.cs` | 5 [Fact]s: List/Create/GetById/Update/Delete | VERIFIED | 5 standalone [Fact] methods; IClassFixture<Phase8WebAppFactory>; TestContext.Current.CancellationToken in all async calls |
| `tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs` | 5 [Fact]s | VERIFIED | 5 facts; IClassFixture<Phase8WebAppFactory>; CancellationToken |
| `tests/BaseApi.Tests/Integration/StepsIntegrationTests.cs` | 5 [Fact]s | VERIFIED | 5 facts; IClassFixture<Phase8WebAppFactory>; CancellationToken |
| `tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs` | 5 [Fact]s | VERIFIED | 5 facts; IClassFixture<Phase8WebAppFactory>; CancellationToken |
| `tests/BaseApi.Tests/Integration/WorkflowsIntegrationTests.cs` | 5 [Fact]s | VERIFIED | 5 facts; IClassFixture<Phase8WebAppFactory>; CancellationToken |
| `tests/BaseApi.Tests/Integration/ErrorMappingFacts.cs` | 4 [Fact]s: DuplicateSourceHash→409, NonExistentEntryStepId→422, DeleteStepReferencedByWorkflow→422, InvalidJsonSchema→400+SSRF-guard | VERIFIED | All 4 facts present with correct names; Stopwatch SSRF timing bound; correlationId assertions |
| `tests/BaseApi.Tests/Composition/MigrationFailureWebAppFactory.cs` | Phase8WebAppFactory subclass with Port=5434 | VERIFIED | Sealed class; calls base("Host=localhost;Port=5434;...") |
| `tests/BaseApi.Tests/Integration/MigrationFailureFacts.cs` | 1 [Fact]: /health/live=200, /health/startup=503, /health/ready=503 | VERIFIED | Single fact; asserts all 3 health probes with correct expected status codes |
| `.planning/REQUIREMENTS.md` | TEST-03+04 deferred to v2; Phase 8 = 39 requirements | VERIFIED | v2 Hardening entries with D-05/D-06 reasoning; traceability v2/Deferred rows; "39 requirements" in per-phase coverage |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `SchemasController` → `/api/v1/schemas` | `SchemaService` → `AppDbContext.Schemas` | BaseController 5 verbs; DI alias AddSchemaFeature; AppDbContext.Schemas DbSet | WIRED | Controller injects abstract BaseService<SchemaEntity,...>; AddSchemaFeature registers SchemaService + alias; AppDbContext.Schemas DbSet present |
| `SchemaCreateDto.Definition` | `MetaSchemas.Draft202012.Evaluate` | FluentValidation Custom rule in SchemaCreateDtoValidator | WIRED | Static ctor sets Dialect.Default + SchemaRegistry.Global.Fetch no-op; Custom rule calls MetaSchemas.Draft202012.Evaluate |
| `ProcessorEntityConfiguration` | `uq_processor_source_hash` | HasIndex.IsUnique.HasDatabaseName | WIRED | Confirmed in config file and in migration DDL |
| `ProcessorEntityConfiguration` | `fk_processor_input/output_schema_id` | lambda-less HasOne<SchemaEntity> + HasConstraintName + OnDelete(SetNull) | WIRED | Both FK constraints confirmed in config and migration |
| `StepService.SyncJunctionsAsync` | `DbContext.Set<StepNextSteps>()` | override SyncJunctionsAsync; remove-and-replace pattern | WIRED | StepService overrides SyncJunctionsAsync; confirmed via grep |
| `WorkflowService.SyncJunctionsAsync` | `DbContext.Set<WorkflowEntrySteps>() + Set<WorkflowAssignments>()` | dual-junction override | WIRED | WorkflowService overrides SyncJunctionsAsync; handles both junctions |
| `fk_workflow_entry_steps_step_id` (Restrict) | SC#5: DELETE Step → 422 | WorkflowEntryStepsConfiguration.OnDelete(Restrict) | WIRED | Config confirmed; migration DDL confirmed; ErrorMappingFacts fact 3 tests this path |
| `StartupCompletionService.StartAsync` | `db.Database.MigrateAsync` | IServiceScopeFactory scope; BaseDbContext alias | WIRED | Try/catch/LogCritical; MarkReady inside try only |
| `Phase8WebAppFactory.InitializeAsync` | `PostgresFixture.ConnectionString` | Internal _fixture field; IAsyncLifetime ordering | WIRED | PostgresFixture allocated and initialized before host boot; AddInMemoryCollection injects the connection string |
| `compose.yaml baseapi-service` | `Dockerfile` at repo root | build: { context: ., dockerfile: Dockerfile } | WIRED | compose.yaml confirmed; Dockerfile at repo root confirmed |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `SchemasIntegrationTests` | HTTP response body | Phase8WebAppFactory → AppDbContext.Schemas → Postgres | Yes — migration applies at boot; real Postgres throwaway DB | FLOWING (human-verified per SUMMARY runs 4/5/6) |
| `ErrorMappingFacts` | HTTP 409/422/400 ProblemDetails | Phase8WebAppFactory → AppDbContext → Phase4 PostgresExceptionMapper | Yes — real Postgres constraint violations | FLOWING (human-verified per SUMMARY) |
| `MigrationFailureFacts` | /health/* responses | MigrationFailureWebAppFactory → StartupCompletionService try/catch | Yes — MigrateAsync throws on Port=5434 | FLOWING |

---

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Dockerfile well-formed | Inspect Dockerfile for required directives | FROM sdk:8.0-bookworm-slim AS build; USER app; UseAppHost=false; ENTRYPOINT ["dotnet","BaseApi.Service.dll"] — all present | PASS |
| compose.yaml valid parse | grep/inspect for required keys | build:, ConnectionStrings__Postgres, 8080:8080, /health/ready, no phase-8 profile — all confirmed | PASS |
| Migration has correct constraint names | grep on InitialCreate.cs | 11/11 explicit FK names + uq_processor_source_hash found | PASS |
| MigrateAsync runner: catch does not rethrow | Inspect StartupCompletionService | catch (Exception) { LogCritical; no rethrow; no MarkReady } — confirmed | PASS |
| All 5 entity smoke test classes use IClassFixture<Phase8WebAppFactory> | grep integration test files | All 5 confirmed | PASS |
| 3-consecutive GREEN runs | dotnet test against live Postgres | Documented in SUMMARY (Runs 4/5/6 = 128/128/128); requires live execution to re-verify | SKIP (human-needed) |
| Byte-identical psql \l snapshot | diff artifact files + live psql | Artifacts exist; SHA-256 in SUMMARY = 0d98b0de...; requires live DB to re-verify | SKIP (human-needed) |

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| PERSIST-01 | 08-07 | AppDbContext with 5+3 DbSets | SATISFIED | AppDbContext.cs confirmed |
| PERSIST-08 | 08-02, 08-05 | jsonb for Schema.Definition + Assignment.Payload | SATISFIED | HasColumnType("jsonb") in both configs; migration confirms |
| PERSIST-09 | 08-07 | MigrateAsync on startup | SATISFIED | StartupCompletionService swapped |
| PERSIST-10 | 08-07, 08-08 | Migration failure = unhealthy probe, no crash | SATISFIED | try/catch/no-rethrow + MigrationFailureFacts fact |
| PERSIST-12 | 08-04, 08-06 | Junction tables with composite PKs + FKs | SATISFIED | 3 junction configs + migration DDL |
| PERSIST-13 | 08-02..08-06 | DB-level FK constraints on all FK columns | SATISFIED | 11 FK constraint names in migration |
| PERSIST-14 | 08-03 | Unique index on Processor.SourceHash | SATISFIED | uq_processor_source_hash confirmed |
| ENTITY-03 | 08-02 | SchemaEntity adds Definition | SATISFIED | SchemaEntity.cs |
| ENTITY-04 | 08-03 | ProcessorEntity adds SourceHash + InputSchemaId? + OutputSchemaId? | SATISFIED | ProcessorEntity.cs |
| ENTITY-05 | 08-04 | StepEntity adds ProcessorId + EntryCondition (default PreviousCompleted) | SATISFIED | StepEntity.cs |
| ENTITY-06 | 08-04 | StepEntryCondition enum 6 values (0..5) | SATISFIED | StepEntryCondition.cs |
| ENTITY-07 | 08-05 | AssignmentEntity adds StepId + SchemaId + Payload | SATISFIED | AssignmentEntity.cs |
| ENTITY-08 | 08-06 | WorkflowEntity adds CronExpression (junctions on DTOs only) | SATISFIED | WorkflowEntity.cs |
| ENTITY-09 | 08-02..08-06 | No navigation properties between entities | SATISFIED | All entity files: only scalar properties |
| ENTITY-10 | 08-02..08-06 | (Name, Version) NOT unique on any entity | SATISFIED | No unique constraint on Name+Version in any config |
| HTTP-04 | 08-02..08-06 | 3 DTOs per entity | SATISFIED | CreateDto + UpdateDto + ReadDto in each Dtos.cs |
| HTTP-05 | 08-02..08-06 | CreateDto excludes server-controlled fields | SATISFIED | No Id/CreatedAt/UpdatedAt/CreatedBy/UpdatedBy in any CreateDto |
| HTTP-06 | 08-02..08-06 | UpdateDto excludes Id/CreatedAt/CreatedBy | SATISFIED | Confirmed per Dtos.cs patterns |
| HTTP-07 | 08-02..08-06 | ReadDto includes every entity field + audit | SATISFIED | All ReadDtos carry Id + 4 audit fields + entity-specific fields; IHasId |
| HTTP-11 | 08-02..08-06 | Mapperly [Mapper] partial per entity | SATISFIED | All 5 mapper files have [Mapper] partial class |
| HTTP-12 | 08-02..08-06 | Concrete controllers are empty-body inheritors | SATISFIED | All 5 controllers confirmed: empty body, passthrough ctor |
| VALID-08 | 08-02 | Schema.Definition valid JSON Schema 2020-12 via JsonSchema.Net | SATISFIED | SchemaCreateDtoValidator static ctor + MetaSchemas.Draft202012.Evaluate |
| VALID-09 | 08-02 | JsonSchema.Net remote $ref disabled | SATISFIED | SchemaRegistry.Global.Fetch = (_, _) => null; SSRF timing fact in ErrorMappingFacts |
| VALID-10 | 08-03 | Processor.SourceHash regex ^[a-f0-9]{64}$ | SATISFIED | ProcessorDtoValidator: .Matches(@"^[a-f0-9]{64}$") |
| VALID-11 | 08-03 | ProcessorDto nullable Guid?: null OK; Guid.Empty rejected | SATISFIED | When(HasValue, RuleFor(.Value).NotEqual(Guid.Empty)) pattern |
| VALID-12 | 08-04 | StepDto.ProcessorId NotEmpty Guid | SATISFIED | StepDtoValidator: NotEqual(Guid.Empty) |
| VALID-13 | 08-04 | StepDto.NextStepIds each unique | SATISFIED | Uniqueness check confirmed (self-ref check noted as v1 limitation) |
| VALID-14 | 08-04 | StepDto.EntryCondition IsInEnum | SATISFIED | .IsInEnum() in validator |
| VALID-15 | 08-05 | AssignmentDto StepId/SchemaId NotEmpty | SATISFIED | NotEqual(Guid.Empty) on both |
| VALID-16 | 08-05 | AssignmentDto.Payload valid JSON + MaxLength 1,048,576 | SATISFIED | MaximumLength(1_048_576) before JsonDocument.Parse |
| VALID-17 | 08-06 | WorkflowDto.EntryStepIds NotEmpty + each unique | SATISFIED | NotEmpty check + uniqueness check |
| VALID-18 | 08-06 | WorkflowDto.AssignmentIds each unique when present | SATISFIED | Uniqueness guard with When |
| VALID-19 | 08-06 | WorkflowDto.CronExpression valid 5-field Cronos when present | SATISFIED | BeValidStandardCron via Cronos.CronExpression.Parse default (5-field) |
| VALID-20 | 08-02..08-06 | Validators inherit via Include(BaseDtoValidator<T>) | SATISFIED | All 10 validator classes (Create + Update per entity) confirmed |
| INFRA-05 | 08-01 | Multistage Dockerfile | SATISFIED | Dockerfile at repo root confirmed |
| TEST-01 | 08-01 | xUnit v3 test project | SATISFIED | Pre-existing (Phase 1-7); Phase 8 adds to it |
| TEST-02 | 08-01 | WebApplicationFactory + Phase8WebAppFactory | SATISFIED | Phase8WebAppFactory.cs confirmed |
| TEST-03 | v2 (deferred) | Testcontainers.PostgreSql | DEFERRED | Per D-05; REQUIREMENTS.md updated |
| TEST-04 | v2 (deferred) | Respawn DB reset | DEFERRED | Per D-06; REQUIREMENTS.md updated |
| TEST-05 | 08-02..08-06 | Min 25 smoke facts (5 entities × 5 verbs) | SATISFIED | 5 [Fact] per entity class × 5 classes = 25 |
| TEST-06 | 08-08 | Min 1 negative fact per error type (400/404/409/422) | SATISFIED | ErrorMappingFacts: 409 (duplicate SourceHash) + 422 (FK violation) + 422 (Restrict DELETE) + 400 (invalid schema) |

**Orphaned requirements check:** No Phase 8 requirement IDs appear in REQUIREMENTS.md that are unaccounted for in the plans. TEST-03 and TEST-04 are explicitly deferred to v2 per D-05/D-06 as documented in the plan instructions.

---

### Anti-Patterns Found

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| N/A | No TODO/FIXME/PLACEHOLDER markers found in feature files | - | Clean |
| N/A | No empty implementations (`return null` / `return []`) in production controllers or services | - | Clean |
| N/A | Mapper `[MapValue(..., null)]` on junction-collection DTO fields in StepEntityMapper + WorkflowEntityMapper | Info | v1 limitation: NextStepIds / EntryStepIds / AssignmentIds return null on read paths; junction state verified via direct-DB helpers in tests; documented design decision per SUMMARY 08-04/08-06 |
| `tests/...` | NormalizeJson helpers duplicated across AssignmentsIntegrationTests + WorkflowsIntegrationTests | Info | Minor duplication; SUMMARY 08-07 notes v2 may centralize into TestJsonHelper module; does not affect correctness |

No blockers or warnings found. All identified patterns are documented design decisions or minor cosmetic issues.

---

### Human Verification Required

#### 1. Full Suite 3-Consecutive GREEN Runs

**Test:** Run `dotnet test SK_P.sln` on a machine with the Docker Compose stack running (Postgres on localhost:5433 via `docker compose up`); repeat until 3 consecutive runs each show 128 Passed, 0 Failed.

**Expected:** Each run: 128 Passed (98 Phase 1-7 + 25 Wave B + 4 ErrorMapping + 1 MigrationFailure), 0 Failed, exit code 0. Three runs must be consecutive without code changes between them.

**Why human:** Requires a live Postgres instance; Docker Compose stack must be up; OTel Collector warmup flakes (Phase 5 documented pattern) may require extra runs. The SUMMARY documents Runs 4/5/6 as the 3-consecutive GREEN gate, but this cannot be replayed from a static code scan.

#### 2. Byte-Identical psql \l Snapshot

**Test:** Before the suite: `docker exec sk_p-postgres-1 psql -U postgres -l > /tmp/before.txt`. Run full suite. After: `docker exec sk_p-postgres-1 psql -U postgres -l > /tmp/after.txt`. Then `diff /tmp/before.txt /tmp/after.txt` (expect no output) and `sha256sum /tmp/before.txt /tmp/after.txt` (expect matching hashes).

**Expected:** diff exits 0 (no output); both files hash to the same SHA-256 value; only 4 baseline databases visible (postgres, stepsdb, template0, template1) — no leaked stepsdb_test_\<guid\> databases.

**Why human:** Requires running the Docker container and psql tool against a live instance. Artifact files exist at `.planning/phases/08-entity-build-out-migrations-docker-runtime-tests/artifacts/` and match the SUMMARY-documented SHA-256 `0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127`, but re-verification requires live execution.

#### 3. Docker Compose End-to-End Boot (SC#1 + SC#6)

**Test:** From repo root: `docker compose up --build -d`. Wait ~30s for migration window. Then `curl -fsS http://localhost:8080/health/live` (expect 200) and `curl -fsS http://localhost:8080/api/v1/schemas` (expect 200 + `[]`). Then `docker compose down -v`.

**Expected:** Service container builds successfully via multistage Dockerfile; migrations apply at first boot via StartupCompletionService; GET /api/v1/schemas returns HTTP 200 with body `[]`; docker compose down -v cleans up without errors.

**Why human:** Requires Docker daemon + pulling .NET 8 base images + building the multistage image; cannot verify statically. The SUMMARY confirms `docker compose config` exits 0 but the full build+run path requires execution.

---

### Gaps Summary

No gaps found in code artifacts, wiring, or requirements coverage. All 39 active Phase 8 requirement IDs are satisfied by concrete code. The 3 human verification items are runtime execution claims that are plausible (all structural prerequisites exist in the codebase) but require a live stack to confirm.

The phase goal — "ship the 5 v1 entities with EF Core persistence + InitialCreate migration + cross-entity error-mapping facts, locking in 3 consecutive GREEN runs of the full 128-fact suite" — is structurally complete. Execution confirmation is the remaining gate.

---

_Verified: 2026-05-28T00:00:00Z_
_Verifier: Claude (gsd-verifier)_
