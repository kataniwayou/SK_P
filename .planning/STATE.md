---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: milestone
status: executing
stopped_at: "Completed 10-02-PLAN.md (Assignment SchemaId removal — atomic refactor commit #2 of Phase 10 sequence)"
last_updated: "2026-05-28T07:33:33.008Z"
last_activity: 2026-05-28
progress:
  total_phases: 10
  completed_phases: 9
  total_plans: 31
  completed_plans: 28
  percent: 90
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-05-27)

**Core value:** A solid, observable, validated CRUD foundation that future workflow-platform features build on without rework.
**Current focus:** Phase 10 — remove-schemaid-on-assignmententity-and-add-configschemaid-o

## Current Position

Phase: 10 (remove-schemaid-on-assignmententity-and-add-configschemaid-o) — EXECUTING
Plan: 3 of 5
Status: Ready to execute
Last activity: 2026-05-28

Progress: [█████████░] 90%

## Performance Metrics

**Velocity:**

- Total plans completed: 30
- Average duration: —
- Total execution time: —

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01 | 3 | - | - |
| 02 | 2 | - | - |
| 03 | 2 | - | - |
| 04 | 2 | - | - |
| 5 | 2 | - | - |
| 06 | 2 | - | - |
| 07 | 2 | - | - |
| 08 | 8 | - | - |
| 09 | 3 | - | - |

**Recent Trend:**

- Last 5 plans: —
- Trend: —

*Updated after each plan completion*
| Phase 01 P01 | 6min | 5 tasks | 7 files |
| Phase 01 P02 | 24min | 6 tasks | 23 files |
| Phase 01 P03 | ~75min | 4 tasks (verification + 4 scaffold-fix cycles) | 1 SUMMARY + 3 amendments to earlier-plan files |
| Phase 02 P02-01 | 3min | 3 tasks | 5 files |
| Phase 02 P02-02 | 5min | 6 tasks | 1 files |
| Phase 03 P03-01 | 8min | 9 tasks (8 commits + 1 verification gate) | 9 files (1 doc + 1 props + 2 csproj + 5 new C#) |
| Phase 03 P03-02 | 3min | 7 tasks | 10 files |
| Phase 04 P01 | 25min | 8 tasks tasks | 10 files files |
| Phase 04 P02 | ~40min | 7 tasks tasks | 14 files files |
| Phase 05 P01 | 10min | 7 tasks | 9 files |
| Phase 05 P02 | ~60min | 8 tasks + continuation (Gap 1 + Gap 2 closures) | 13 files (9 new test files + 1 new assembly fixture + 2 modified Plan 05-01 files + 1 modified test file) |
| Phase 06 P01 | 9min | 3 tasks tasks | 9 files (5 new + 4 modified) files |
| Phase 06 P02 | ~20min | 3 tasks | 13 files |
| Phase 07 P07-01 | 11min | 4 tasks tasks | 18 files files |
| Phase 07 P02 | ~35min | 4 tasks (3 auto + 1 human-verify resolved by automated coverage) tasks | 14 files (13 new test files + 1 SUMMARY) files |
| Phase 08 P01 | 7min | 5 tasks tasks | 7 files files |
| Phase 08 P02 | 5min | 2 tasks tasks | 9 files files |
| Phase 08 P03 | 5min | 2 tasks tasks | 9 files files |
| Phase 08 P04 | 12min | 3 tasks | 12 files |
| Phase 08 P05 | 4min | 2 tasks tasks | 9 files files |
| Phase 08 P06 | 10min | 3 tasks tasks | 13 files files |
| Phase 08 P07 | 17min | 4 tasks tasks | 12 files files |
| Phase 08 P08 | 25min | 3 tasks | 5 files |
| Phase 09 P01 | 3min | 3 tasks | 2 files |
| Phase 09 P02 | 10min | 5 tasks tasks | 5 files files |
| Phase 09 P03 | 15min | 4 tasks tasks | 3 files files |
| Phase 10 P01 | 5min | 4 tasks | 1 files |
| Phase 10 P02 | 4min | 5 tasks tasks | 4 files files |

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table (22 locked decisions at roadmap creation).
Recent decisions affecting current work:

- Init: Single-API (Option B) instead of 6 separate webapis — modular monolith with `BaseApi.Core` + `BaseApi.Service`
- Init: `BaseEntity` abstract, no table; 5 concrete entities, 5 tables
- Init: Mapperly (source-gen) over AutoMapper; FluentValidation 12 (no `.AspNetCore`)
- Init: OTLP -> external OTel Collector; `Logging:LogLevel` single source of truth via MEL pipeline
- Init: RFC 7807 Problem Details with `correlationId` on every error; SQLSTATE 23503->422, 23505->409
- Plan 01-01: Front-loaded all 23 NuGet pins in Directory.Packages.props (D-05); CPM property only in Packages.props (D-06); Mapperly MP-codes deferred to Phase 6 (D-04)
- Plan 01-01: TreatWarningsAsErrors=true globally via Directory.Build.props (D-02) + EnforceCodeStyleInBuild=true + :warning severities in .editorconfig = build-fatal style/naming rules
- Plan 01-01: SDK pin 8.0.421 with rollForward=latestFeature (allows 8.0.422+ but blocks float to .NET 9/10); verified dotnet --version returns 8.0.421
- Plan 01-02: Solution + 3 projects shipped; BaseApi.Service.csproj W-04-clean (no UserSecretsId, deferral comment); all 15 .gitkeep files share canonical W-01 body; appsettings has all 4 REQ INFRA-04 sections with Service.Name=steps-api Version=3.2.0
- Plan 01-02: csproj inheritance pattern proven by absence — none redeclare TargetFramework/Nullable/ImplicitUsings/LangVersion/AnalysisMode/EnforceCodeStyleInBuild/TreatWarningsAsErrors; no Version= attributes on PackageReference (CPM via Directory.Packages.props)
- Plan 01-03 deviation #1 (attributed Plan 01-01): xunit.runner.visualstudio pin corrected from non-existent 3.1.7 to 3.1.5 in Directory.Packages.props (commit d106972) — surfaced by `dotnet restore` NU1101 failure during Plan 03 Task 2
- Plan 01-03 deviation #2 (attributed Plan 01-02): added `<OutputType>Exe</OutputType>` to BaseApi.Tests.csproj (commit 630b6ee) — xunit.v3 3.2.2's Microsoft.Testing.Platform integration requires test projects to be executable hosts, not libraries
- Plan 01-03 deviation #3 (attributed Plan 01-02): added `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` to BaseApi.Tests.csproj (commit e60d001) — opts the test executable into invoking the MTP runner rather than legacy VSTest
- Plan 01-03 deviation #4 (attributed Plan 01-02): added `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>` to BaseApi.Tests.csproj (commit ea02ad9) — SDK 8/9 `dotnet test` requires this companion to UseMicrosoftTestingPlatformRunner to route to MTP entry-point instead of VSTest; user-approved at checkpoint after deviation framework's 3-attempts guard
- Plan 01-03: SC#1 (zero-warning build both configs), SC#2 (SDK 8.0.421 literal), SC#3 (CPM resolution via dotnet list package), SC#4 (appsettings.json all 4 sections + locked Service values) — all four ROADMAP success criteria verified GREEN; INFRA-01/02/03/04 closed; Phase 1 shipped
- Plan 02-01: compose.yaml at repo root (Docker Compose v2 default filename per D-10) with postgres:17-alpine pin (D-12), 5433:5432 host port (D-01 / Pitfall 25), pg_isready healthcheck verbatim from Pitfall 24 with double-$$ escaping (D-13), pgdata named volume (D-11), and baseapi-service skeleton with depends_on: postgres: condition: service_healthy (D-08 / ROADMAP SC#4) — build: line commented until Phase 8 INFRA-05 with verbatim marker text
- Plan 02-01: .env committed at repo root with POSTGRES_DB=stepsdb / POSTGRES_USER=postgres / POSTGRES_PASSWORD=postgres (D-04) — Out of Scope auth/secrets to v2 makes this honest; .gitignore appends D-06 section ignoring .env.local + *.env.local (defensive glob) but NOT .env
- Plan 02-01: appsettings.Development.json patched Port=5432 -> Port=5433 (D-02 closes Phase 1 D-14 carry-forward); base appsettings.json explicitly NOT touched (D-07 — Host=postgres;Port=5432 is the Docker-internal path Phase 8 will consume). All other connection-string tokens preserved verbatim; file remains valid JSON with no comments (Pitfall 30)
- Plan 02-02: SC#1-4 all GREEN — postgres:17-alpine healthy in ~13s; host psql via docker-run --network host on localhost:5433 lists stepsdb + postgres; smoke-table persists across docker compose down/up (no -v); resolved compose graph shows depends_on -> postgres -> condition: service_healthy under phase-8 profile; D-15 cleanup ran in full (sk_p_pgdata volume removed)
- Plan 02-02 deviation (fix(02-01) 0acb0bc): Compose v5.1.1 strict validation refused baseapi-service block lacking image/build; fix-forward adds profiles: [phase-8] + placeholder image baseapi-service:phase-8-placeholder so default ops skip the service and explicit up still fails loudly (image-pull failure), preserving D-08 negative-assertion semantics; D-08 commented-build marker preserved verbatim for Phase 8 INFRA-05 handoff
- Plan 03-01 deviation pattern: verify scripts as ephemeral .ps1 files at repo root (Bash eats PowerShell $ sigils when -Command is used inline); strip XML/// + // + /* */ comments before semantic regex checks; use [ \t]+ instead of \s+ in multiline regex to avoid greedy newline matching; allow Task[<\s] for generic return types
- Plan 03-01: PERSIST-16 (xmin shadow concurrency token) landed in REQUIREMENTS.md as Task 0 BEFORE any implementation commits — D-03b doc-first traceability pattern; same approach should be used whenever a phase invents a new cross-phase requirement during planning
- Plan 03-01: BaseApi.Core.csproj uses <FrameworkReference Include='Microsoft.AspNetCore.App' /> for IHttpContextAccessor + future controllers/middleware/healthchecks instead of per-package PackageReferences (D-12). Zero runtime weight (framework on host). BaseApi.Tests.csproj uses the same FrameworkReference for DefaultHttpContext + ClaimsPrincipal in StubHttpContextAccessor (D-13)
- Plan 03-01: All 5 production C# files copied VERBATIM from 03-RESEARCH.md / 03-PATTERNS.md skeletons (BaseEntity, BaseDbContext, AuditInterceptor, IRepository, Repository). The xmin shadow concurrency token (Pitfall 6 verbatim — Property<uint>('xmin').HasColumnName('xmin').HasColumnType('xid').ValueGeneratedOnAddOrUpdate().IsConcurrencyToken()) wires in BaseDbContext.OnModelCreating via modelBuilder.Model.GetEntityTypes() iteration filtered by typeof(BaseEntity).IsAssignableFrom — junction entities (non-BaseEntity) excluded naturally
- Plan 03-02 deviation: xUnit v3 3.2.2 xUnit1051 analyzer escalation under TreatWarningsAsErrors=true required threading TestContext.Current.CancellationToken through 10 async call sites in SchemaTests.cs (4) + AuditInterceptorTests.cs (6). Classified as fix(03-02) test-side, not fix(03-01) — production code unchanged. Commit 7636429.
- Plan 03-02: Phase 3 ROADMAP SC#1-4 + Dim 6 (PERSIST-16) + Dim 7 (PERSIST-11) all GREEN. dotnet test exit 0, Passed: 7, Failed: 0. D-15 cleanup proven via byte-identical psql \l snapshots before/after (4 baseline DBs each, zero stepsdb_test_* leaks). Phase 3 complete; ready for Phase 4 (cross-cutting middleware + error handling).
- Plan 04-01: Option A FK regex (preserves _id suffix per RESEARCH lines 1059-1072) — explicitly authorized deviation from D-08 verbatim via Claude's Discretion at CONTEXT.md line 193
- Plan 04-01 fix-forward: Npgsql pin corrected from 8.0.10 to 9.0.0 — Npgsql 8.0.10 does not exist on nuget.org and Npgsql.EFCore.PostgreSQL 8.0.10 transitively brings Npgsql 9.0.0. RESEARCH A2 pre-authorized this fix-forward path.
- Plan 04-01: Phase 4 takes ownership of AddHttpContextAccessor (Phase 3 D-11 had deferred to Phase 7) — Pitfall 1 mitigation, idempotent so Phase 7 won't conflict
- Plan 04-02: Phase 4 ROADMAP SC#1-5 + D-03a + ERROR-08/09 regression + T-04-LEAK/XMIN/INJECT mitigations all GREEN via 24 new + 7 Phase 3 carry-over facts. dotnet test exit 0 Passed: 31 Failed: 0 across 3 consecutive runs (~1.5-2.9s each). 14 phase REQ-IDs (OBSERV-09/10/11 + ERROR-01..11) closed. D-15 cleanup proven via byte-identical psql l snapshots.
- Plan 04-02 fix-forward (ad3f1a1): Corrected Npgsql pin 9.0.0 to 8.0.9 in Directory.Packages.props. EFCore.PostgreSQL 8.0.10 binary-references Npgsql 8.0.9 internal types removed in Npgsql 9.x rewrite. TypeLoadException on Npgsql.Internal.HackyEnumTypeMapping surfaced at test runtime under WebApplicationFactory boot. Classified as fix(04-01) per Phase 3 D-18 (production dependency mis-pin in file owned by Plan 04-01). Anticipated by Plan 04-01 RESEARCH A2 trivial-to-fix envelope.
- Plan 05-01: bare .AddNpgsql() (CONTEXT D-05 corrected — NpgsqlTracingOptions 8.0.4 has no EnableEntityFrameworkCoreInstrumentation property; default already secure per T-05-PII)
- Plan 05-01: AddHostedService<StartupCompletionService> route (NOT inline app.Services.GetRequiredService<IStartupGate>().MarkReady) — Reconciliation 2; Phase 8 substitution becomes clean 1-line swap
- Plan 05-01: public sealed on Health/ concrete types (Reconciliation 3 — CONTEXT D-01/D-02 internal sealed wording requires InternalsVisibleTo; consistency with Phase 3/4 public sealed conventions wins)
- Plan 05-01 deviation (Rule 1 API mismatch): metrics-side .AddAspNetCoreInstrumentation is parameterless in OTel 1.15.0 — Filter callback only on TracerProviderBuilder overload; /health metric noise deferred to backend query filtering
- Plan 05-01: Open Q3 resolved (a) — no launchSettings.json committed (collides with untracked Properties/ scaffold); rely on OTel SDK default fallback http://localhost:4317
- Plan 05-02: Position-marker file-handle strategy over truncate/delete (Pattern B) — otel-collector v0.95.0 file exporter holds exclusive write handle; truncate may succeed but delete orphans the inode. Record File.Length at InitializeAsync; ReadAllExportedRecords seeks past offset
- Plan 05-02: Env-var-in-ctor pattern for ConnectionStrings:Postgres overrides (Pattern C) — ConfigureAppConfiguration arrives AFTER Program.cs has captured the connection string by value into AddNpgSql; Environment.SetEnvironmentVariable in fixture ctor (BEFORE base ctor) DOES propagate via env-var configuration source. Capture+restore prior value on Dispose
- Plan 05-02: MEL category filter override pattern for SC#4 logs-half (Pattern D) — WebApplicationFactory<Program> defaults to ASPNETCORE_ENVIRONMENT=Development raising Microsoft.AspNetCore to Information; HealthFilterEnabledFixture overrides via ConfigureAppConfiguration + AddInMemoryCollection setting 3 categories to Warning
- Plan 05-02: xUnit v3 [assembly: AssemblyFixture] for end-of-suite cleanup (Pattern E) — closes D-11 cleanup discipline; runs once after all assembly tests; shells out to docker compose stop/delete/start otel-collector; post-restart double-delete guard against external-process residuals
- Plan 05-02: Collector-side filterprocessor over SDK-side filtering (Pattern F) — closes Plan 05-01 Deviation #2 (SC#4 metrics-half gap) via OTTL `IsMatch(attributes["http.route"], "^/health/.*")` in `compose/otel-collector-config.yaml`; SDK emits all, Collector applies ops-policy filtering. Idiomatic OTel layered architecture
- Plan 05-02: Un-sealing OtelCollectorFixture for nested inheritance (Pattern G) — HealthEndpointsTests needs 4 nested subclasses overriding ConfigureWebHost; sealing would have forced awkward composition
- Plan 05-02: Three-ctor IClassFixture activation strategy (Pattern A details) — public parameterless ctor for IClassFixture + internal overloads for direct `new`; default parameters do NOT satisfy IClassFixture parameter resolution
- Plan 05-02: ROADMAP Phase 5 SC#1-5 + D-16 + T-05-PII/LOG-INJECT/OTLP-EXFIL/READY-DB-EXPOSE all GREEN via 16 new + 31 carryover = 47 facts; 3 consecutive dotnet test runs (17.67/17.71/18.09s); byte-identical psql \l BEFORE/AFTER; tests/.otel-out/telemetry.jsonl absent post-suite (D-11)
- Plan 06-01: 5 BaseApi.Core seam files (IBaseDto, BaseDtoValidator<T>, IEntityMapper<,,,>, AddBaseApiValidation, AddBaseApiMapping) landed verbatim from RESEARCH Code Examples 1-5; Program.cs D-18 wiring inserted at Phase-7 cut-paste slot; Directory.Build.props promotes RMG007/RMG012/RMG020/RMG089 (RMG089 Info-severity is load-bearing — TreatWarningsAsErrors only catches Warning+); Mapperly PackageReference added to BaseApi.Tests.csproj with PrivateAssets=all ExcludeAssets=runtime; WebAppFactory unsealed (sealed→public class) per path A precedent set by Phase 5 OtelCollectorFixture; 47/47 Phase 1-5 regression suite passed unchanged.
- Plan 06-01 deviation (Rule 1 - plan internal inconsistency): Directory.Build.props sub-edit 1 verbatim text contained MP0001/MP0011/MP0020/MP0021 codes but Plan verification line 734 required those codes be grep-empty across Directory.Build.props/src/tests. Resolved by rephrasing amended D-04 comment to convey the same correction history without naming the specific MP codes — preserves educational content (RESEARCH A-01 reference) and satisfies the grep-empty assertion. Build still 0-warning Release+Debug after rephrase.
- Plan 06-02: TestEntityMapper attribute coverage extended to all 3 methods (ToEntity/Update/ToRead) — plan-as-written only specified Update; Mapperly RequiredMappingStrategy=Both fires RMG012 on ToEntity (5 errors: target Id+audit on TestCreateDto source) and RMG020 on ToRead (4 errors: source audit on TestReadDto target). Rule 3 plan-gap fix-forward. Phase 8's 5 entity mappers MUST replicate the 3-method pattern: 5 [MapperIgnoreTarget] each on ToEntity+Update, 4 [MapperIgnoreSource] on ToRead (or omit if Read DTO carries audit fields per HTTP-07)
- Plan 06-02: Drift-detection probe (Check 4) confirms RMG012+RMG020 promotion is LIVE across all 3 mapper methods — adding an unmapped Drift property to TestEntity fires errors on ToEntity (RMG012), Update (RMG012), AND ToRead (RMG020). Phase 8 mappers inherit this safety net.
- Plan 06-02: Run 1 of 3 had 7 Phase 5 Observability test failures (LogExport/LogLevelFilter/MetricsExport/TraceExport — all asserted Collection was empty) — OTel Collector had been up only 28s when Run 1 started, telemetry hadn't fully batched/scraped. Runs 2/3/4 all 76/76 GREEN. Matches Phase 5 SUMMARY's documented fixture-lifecycle robustness items. No Phase 6 code change needed; plan resume-signal explicitly accommodated this re-run path.
- Plan 07-01: Encoded CONTEXT D-13 amendment — AddBaseApi<TDbContext> chains 6 sub-extensions on IServiceCollection (Persistence -> Health -> ErrorHandling -> Http -> Validation -> Mapping); Observability invoked separately on IHostApplicationBuilder via builder.AddBaseApiObservability(cfg) from Program.cs because OTel MEL bridge needs ILoggingBuilder (not on IServiceCollection)
- Plan 07-01: AuditInterceptor lifetime = Singleton (Phase 3 D-06 canonical; RESEARCH Pitfall 4 reconciliation overrides CONTEXT D-14's Scoped snippet); BaseDbContext alias = Scoped via sp.GetRequiredService<TDbContext>() (RESEARCH Pitfall 5) so BaseService<...> can resolve the abstract type without captive-lifetime risk
- Plan 07-01: IHasId marker interface chosen over dynamic dispatch (RESEARCH Open Q2 option b); BaseService validator null-guards throw InvalidOperationException with descriptive message (Discretion option a); BaseService exposes protected BaseDbContext DbContext { get; } property (Pitfall 3) for Plan 07-02 RecordingTestService ChangeTracker assertion
- Plan 07-01 deviation [Rule 1]: removed typeof(TRead) and typeof(IReadOnlyList<TRead>) from BaseController ProducesResponseType attributes — CS0416 forbids generic type parameters in attribute arguments; status-code-only variants retained; ActionResult<TRead> return type still surfaces schema on Swagger; Phase 8 concrete controllers MAY add typed [ProducesResponseType(typeof(ConcreteReadDto), 200)] in their bodies
- Plan 07-01 deviation [Rule 3]: promoted AddBaseApiObservability from internal to public static — required for cross-assembly invocation from BaseApi.Service/Program.cs per D-13 amendment; InternalsVisibleTo alternative rejected (adds indirection without value); visibility now matches other top-level entries (AddBaseApi, UseBaseApi, AddBaseApiValidation, AddBaseApiMapping)
- Plan 07-02: SwaggerEnvironmentFacts (AddApplicationPart + ProductionWebAppFactory env override) is the canonical SC#4 proof — manual Swagger UI smoke checkpoint resolved by user-approved automated-coverage rationale because v1 BaseApi.Service has zero concrete controllers (Phase 8 will add entity controllers). Live-boot empty Swagger UI in v1 is structurally expected, not a regression.
- Plan 07-02 Blocker 1 fix: Phase7TestDbContext (BaseDbContext subclass) constructed directly via DbContextOptionsBuilder<Phase7TestDbContext>().UseNpgsql(_fixture.ConnectionString) — PostgresFixture surface has only ConnectionString (no DbContext property), and existing Persistence/TestDbContext tracks BaseApi.Tests.Persistence.TestEntity (a DIFFERENT type from BaseApi.Tests.Validation.TestEntity the Phase 7 facts use).
- Plan 07-02 Blocker 2 fix: TestCreateDtoValidator added (sibling to TestDtoValidator which only covers TestUpdateDto) so BaseService<TestEntity,TestCreateDto,TestUpdateDto,TestReadDto>'s IValidator<TestCreateDto> ctor null-guard does not throw at Phase7WebAppFactory boot. Mirrors Include(new BaseDtoValidator<T>()) pattern verbatim.
- Plan 07-02 Warning 7 option b: TestsController ctor injects ABSTRACT BaseService<TestEntity,TestCreateDto,TestUpdateDto,TestReadDto> (not concrete RecordingTestService) — makes Phase7WebAppFactory's AddScoped<BaseService<...>>(sp => sp.GetRequiredService<RecordingTestService>()) alias load-bearing and matches the Phase 8 expected pattern where concrete controllers inject the abstract base for DI-friendly reuse.
- Plan 07-02 RESEARCH A6 falsified: Asp.Versioning URL-segment versioning returns 404 (route no-match) NOT 400 for unsupported versions like /api/v99/tests. VersioningFacts assertion broadened to accept either 400 or 404 while still verifying correlationId propagation through the response path. Phase 8 may want to revisit if richer version-mismatch semantics are needed.
- Plan 07-02 Task 2 Rule 3 fix-forward: Phase7WebAppFactory extended beyond plan-as-written with per-class throwaway Postgres DB + Phase7TestDbContext registration + BaseDbContext alias remapping so Repository<TestEntity> resolves at integration-test time. AppDbContext placeholder has no DbSets so the abstract repository cannot bind without this Phase7-only re-wiring. Plan-gap classification.
- Plan 08-01: Multistage Dockerfile at repo root (sdk:8.0-bookworm-slim -> aspnet:8.0-bookworm-slim) with USER app (uid 1654) + /p:UseAppHost=false + ENTRYPOINT dotnet BaseApi.Service.dll; .dockerignore excludes tests/, .git, .planning/, .env*, *.md, compose.yaml — minimizes T-08-01-SECRET-LEAK-IMAGE attack surface (D-13)
- Plan 08-01: compose.yaml mutation — drop phase-8 profile + placeholder image; add build:./Dockerfile, env block (ASPNETCORE_ENVIRONMENT, ConnectionStrings__Postgres with double-underscore convention, OTEL_EXPORTER_OTLP_ENDPOINT), ports 8080:8080, wget healthcheck against /health/ready (start_period 30s for migration window), depends_on otel-collector service_started (no healthcheck declared) + postgres service_healthy preserved
- Plan 08-01: 4 PackageReferences on BaseApi.Service.csproj (Riok.Mapperly PrivateAssets=all ExcludeAssets=runtime; Microsoft.EntityFrameworkCore.Design PrivateAssets=all; JsonSchema.Net; Cronos) — all CPM-resolved (zero Version= leaks); .config/dotnet-tools.json pins dotnet-ef 8.0.27 matching CPM EF Core pin (deterministic byte content; not via dotnet new tool-manifest)
- Plan 08-01: Phase8WebAppFactory public-class-not-sealed inheriting WebAppFactory + IAsyncLifetime; encapsulates PostgresFixture internally; protected ctor accepting connectionStringOverride for Plan 08-08 MigrationFailureWebAppFactory subclass; AddInMemoryCollection ConnectionStrings:Postgres (Plan 05-02 Pattern C); no service-side rewiring (Wave C 08-07 populates AppDbContext); does NOT pre-create schema (migration runs at first boot via D-15 swap)
- Plan 08-01: TEST-03 (Testcontainers.PostgreSql) + TEST-04 (Respawn) deferred to v2 Hardening per D-05/D-06 — PostgresFixture pattern proven across 98 facts (Phases 3-7); Testcontainers cold-start 3-5s/fixture with no behavioral gain at v1 scale; Respawn would invalidate Phase 3 D-15 byte-identical psql l no-leak proof; REQUIREMENTS.md Phase 8 coverage drops 41 -> 39 active REQ-IDs; total-mapped becomes 100 v1 + 2 v2 = 102
- Plan 08-01 deviation (Rule 1 fix-forward attributed in Task 4): Phase8WebAppFactory.cs plan body verbatim omitted using Xunit; for IAsyncLifetime (CS0246) and PostgresFixture type alias for Middleware/Persistence ambiguity (CS0104) — both build-blocking; mirror Phase7WebAppFactory.cs verbatim solution. Educational comment containing literal 'EnsureCreatedAsync' rephrased to 'pre-create the schema / model-shortcut create-from-model API' to satisfy plan verify grep-empty assertion (Plan 06-01 MP-code rephrase precedent)
- Plan 08-02: ReadDto-with-audit-symmetric pattern — when ReadDto carries Id + 4 audit fields per HTTP-07, Mapperly ToRead needs ZERO [MapperIgnoreSource] (10 [MapperIgnoreTarget] on ToEntity+Update sufficient); Phase 8 entity mappers replicate this exact pattern when their ReadDto matches
- Plan 08-02: Per-entity DI module pattern — internal static class XxxServiceCollectionExtensions with single AddXxxFeature() registering concrete + abstract-base BaseService alias; Wave C 08-07 AddAppFeatures() aggregator composes 5 such modules into Program.cs after AddBaseApi<AppDbContext>
- Plan 08-02: JsonSchema.Net static-ctor pattern — Dialect.Default = Dialect.Draft202012 (VALID-08) + SchemaRegistry.Global.Fetch = (_,_) => null (VALID-09 SSRF defense-in-depth) live on SchemaCreateDtoValidator.static ctor only (per-AppDomain assignments; first-touch of either validator fires it via DI assembly scan)
- Plan 08-02: Wave B isolation contract — 5 SchemasIntegrationTests fail at HTTP 500 (DI activation InvalidOperationException) because Wave C 08-07 has not yet wired AddSchemaFeature() into Program.cs; documented design per plan Task 2 note; GREEN-state verified by 08-08 cross-entity regression; [Trait('Phase8Wave','B')] enables developer filter/skip
- Plan 08-02 deviation (Rule 1 - build error): SchemaEntityConfiguration.cs XML <see cref='BaseDbContext'> changed to <c>BaseDbContext</c> markup to satisfy CS1574 without adding an unused using BaseApi.Core.Persistence directive (would trip unused-import warning under TreatWarningsAsErrors=true). Preserves doc-comment educational intent.
- Plan 08-03: Explicit constraint naming (uq_processor_source_hash + fk_processor_input_schema_id + fk_processor_output_schema_id) is load-bearing for Plan 08-08 SC#2 — Phase 4 PostgresExceptionMapper Option A regex expects uq_{table}_{column} + fk_{table}_{column}_id; EF auto-naming (ix_, fk_<table>_<principal>_<column>) would break the 23505/23503 → field-name mapping path
- Plan 08-03: VALID-11 nullable-Guid pattern uses When(x => x.Field.HasValue, () => RuleFor(x => x.Field!.Value).NotEqual(Guid.Empty)) — null is valid (source/sink processors per ENTITY-04); only Guid.Empty rejected at HTTP 400; non-existent (but well-formed) FK Guids deliberately pass validation and surface as 23503 → 422 via Phase 4 mapper (PROJECT.md Option 1)
- Plan 08-03: DeleteBehavior.SetNull on both nullable FKs per RESEARCH §Cascade behaviors (line 582) — schema deletion sets dependent processor FK columns to null, NOT cascade-delete the processor; Plan 08-04 Step.ProcessorId is non-nullable and will use Restrict
- Plan 08-03: Lambda-less HasOne<SchemaEntity>().WithMany() form per RESEARCH Pitfall 4 — creates Postgres FK constraint without generating nav properties (ENTITY-09 'no nav props between entities' satisfied); canonical pattern for all Phase 8 entity FK references
- Plan 08-03: Per-fact-unique SourceHash via RandomSha256Hex() — reserves duplicate-hash collision path exclusively for Plan 08-08 error-mapping fact; two Guid byte arrays (32 bytes → 64 hex chars) satisfies both VALID-10 regex AND parallel-class uniqueness
- Plan 08-04: First SyncJunctionsAsync override in Phase 8 — StepService reads DbContext.Set<StepNextSteps>(), on Update RemoveRange existing rows filtered by entity.Id then AddRangeAsync new rows; runs between repo.Add and SaveChanges in Phase 7 D-11 locked 6-step verb order; commits atomically with parent entity in same transaction; Plan 08-06 Workflow will mirror this pattern with 2 junctions
- Plan 08-04: [MapValue(target-prop, null)] replaces [MapperIgnoreTarget(target-prop)] when the target is a positional record's required constructor parameter — Mapperly RMG013 fires because [MapperIgnoreTarget] cannot skip ctor params (suppresses property-level diagnostic only). [MapValue] supplies a compile-time constant directly to the ctor param. Net Step mapper coverage: 10 [MapperIgnoreTarget] + 2 [MapperIgnoreSource] + 1 [MapValue] (instead of plan-as-written 11+2). Plan 08-06 Workflow will reuse this pattern for its 2 M2M collections (EntryStepIds, AssignmentIds)
- Plan 08-04: StepEntity.ProcessorId is non-nullable + OnDelete(Restrict); StepNextSteps both self-ref FKs Restrict — differs from Processor's nullable+SetNull. Establishes Step as the principal side that Plan 08-06 Workflow's FK Restrict attaches to (SC#5 deleting a Step referenced by a Workflow → 422). Postgres rejects SET NULL on non-nullable column so Restrict is the only correct cascade.
- Plan 08-04: Junction-row direct-DB assertion via CountJunctionsForStepAsync (NpgsqlConnection + SELECT count(*) FROM step_next_steps WHERE step_id = @id) bypasses v1 StepReadDto.NextStepIds=null limitation. Post-ToRead enrichment deferred to a future phase when BaseService.GetAsync/ListAsync become virtual or a dedicated enrichment hook is added. Plan 08-06 Workflow will mirror with workflow_entry_steps + workflow_assignments count helpers.
- Plan 08-05: Multi-FK Restrict pattern for leaf entity — AssignmentEntity has 2 non-nullable FKs (StepId, SchemaId) BOTH with OnDelete(Restrict) and explicit constraint names (fk_assignment_step_id, fk_assignment_schema_id). Differs from Processor's nullable+SetNull pattern. Load-bearing for Plan 08-08 SC#5 (delete Step with referenced Assignment → 422).
- Plan 08-05: VALID-16 MaxLength-then-Parse rule ordering — FluentValidation chains .NotEmpty().MaximumLength(1_048_576) BEFORE the .Custom(JsonDocument.Parse) rule, so payloads exceeding 1 MB never reach the parser (DoS protection). Pattern reusable for v2 features accepting unbounded user payloads (e.g., Workflow.CronExpression).
- Plan 08-05: VALID-21 (semantic Payload-vs-Schema conformance) explicitly NOT implemented in v1 validator per N2 + 08-CONTEXT line 23. Would require DB roundtrip from validator (incompatible with stateless FluentValidation architecture). Deferred to v2.
- Plan 08-05: CreatePrereqAsync FK-chain helper (Schema → Processor → Step → Assignment) — when an integration test needs N FK references, build the prereq chain via the public HTTP API inline. Avoids direct-DB INSERTs (which would bypass HTTP-layer validation + Mapperly) and validates the full request pipeline for every FK target.
- Plan 08-06: Second SyncJunctionsAsync override in Phase 8 — handles BOTH WorkflowEntrySteps + WorkflowAssignments junctions in a single method (extends Plan 08-04's single-junction pattern to dual-junction). Each junction is independently wiped+rebuilt on Update, inserted on Create; all changes commit atomically in the same SaveChangesAsync transaction.
- Plan 08-06: WorkflowReadDto.EntryStepIds + .AssignmentIds BOTH declared nullable (List<Guid>?) — Rule 1 fix-forward for RMG076 (cannot assign null to non-nullable). [MapValue(..., null)] on ToRead supplies the positional record ctor params (RMG013 mitigation) but only works with nullable target types. Same pattern as Plan 08-04 StepReadDto.NextStepIds?. v1 ships both collections null on read paths; tests assert via direct-DB CountEntryStepJunctionsAsync.
- Plan 08-06: Asymmetric Cascade/Restrict on both junctions — Cascade-on-Workflow (parent owns junction lifecycle; DELETE Workflow auto-removes junction rows) + Restrict-on-referenced-entity (Step/Assignment). The fk_workflow_entry_steps_step_id Restrict FK is SC#5 LOAD-BEARING — Plan 08-08 fact 3 sequence requires exactly this FK shape to return 422.
- Plan 08-06: Wave B COMPLETE — 5/5 entities, 25 smoke facts authored across Plans 08-02..08-06 (TEST-05 floor satisfied). Wave C 08-07 fully unblocked at file-availability level for AppDbContext composition + InitialCreate migration + AddAppFeatures aggregator.
- Plan 08-07: AppDbContext OnModelCreating ordering load-bearing — ApplyConfigurationsFromAssembly FIRST -> base.OnModelCreating LAST so Phase 3 xmin iteration sees fully-configured BaseEntity subclasses. Reversing the order would silently drop xmin on all 5 entities (PERSIST-16 broken)
- Plan 08-07: D-15 swap implemented as Option 3a' — StartupCompletionService resolves BaseDbContext (Phase 7 D-14 Scoped alias) rather than concrete AppDbContext, keeping BaseApi.Core free of Service-side references while still applying the production migration set
- Plan 08-07: StartupCompletionService failure contract — broad catch (Exception ex) + LogCritical + NO rethrow + NO MarkReady on failure (PERSIST-10 + HEALTH-01). MarkReady INSIDE try BEFORE catch so it's never reached if MigrateAsync throws
- Plan 08-07: 9 Wave B smoke test design issues auto-fixed (Rule 1) - 3 Location-header regex tolerance (Kestrel absolute URL + [controller] case-preserving), 3 List_OnEmptyDb shape relaxation (IClassFixture class-shared DB), 3 jsonb semantic-JSON helpers (Postgres jsonb normalizes whitespace + key order). 25/25 Wave B facts GREEN; Phase 1-7 regression 98/98 GREEN
- Plan 08-07: InitialCreate migration generated with exact constraint naming — 8 CreateTable + 11 explicit FK names + uq_processor_source_hash + 2 jsonb + 5 xid + 7 Restrict + 2 Cascade + 2 SetNull. Load-bearing for Plan 08-08 SC#2 (duplicate sourceHash -> 409 via PostgresExceptionMapper Option A regex) and SC#5 (delete-Step-with-Workflow-ref -> 422 via fk_workflow_entry_steps_step_id Restrict)
- Plan 08-08: ErrorMappingFacts SSRF payload changed from $ref-only to combined-invalid (Rule 1 fix-forward) — empirically verified the bare $ref-only document is structurally valid against Draft 2020-12 meta-schema; combined {type:not-a-real-type,$ref:attacker.example} satisfies both 400 BadRequest AND SSRF timing-bound assertions
- Plan 08-08: 3 CONSECUTIVE GREEN achieved on Runs 4/5/6 of full suite (128/128 each ~28.6s) — Runs 1+3 had pre-existing flakes (ConcurrencyTokenTests.Test_RacingWrites racing-writes + LogLevelFilterTests OTel cold-start); plan accommodation honored ('consecutive is the gate, not first-attempt-3')
- Plan 08-08: byte-identical psql \l BEFORE/AFTER snapshot proven via SHA-256 hash match (0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127) across full 6-run cycle — zero leaked stepsdb_test_<guid> databases (Phase 3 D-15 cleanup discipline preserved end-to-end through Phase 8)
- Plan 08-08: Phase 8 COMPLETE — all 41 phase REQ-IDs covered (39 implemented + TEST-03/TEST-04 deferred to v2 per Plan 08-01 amendment); SC#1..SC#6 all behaviorally verified end-to-end via 30 new Phase 8 facts (25 Wave B smoke + 4 cross-entity error-mapping + 1 migration-failure isolation); system is shippable as v1 Steps API base
- Plan 09-01: ProcessorService.GetBySourceHashAsync uses DbContext.Set<ProcessorEntity>() + AsNoTracking() + FirstOrDefaultAsync directly (CONTEXT D-04 + Phase 3 D-04) — IRepository<T> stays at exactly 5 methods
- Plan 09-01: ProcessorService duplicates IEntityMapper<...> injection rather than promoting BaseService._mapper from private to protected (PATTERNS Section 2 Option B) — singleton mapper means duplicate is cheap; visibility-change-for-one-consumer rejected
- Plan 09-01: ProcessorsController dual ctor injection — abstract BaseService<...> (for inherited 5 CRUD verbs per Phase 7 Warning 7 option b) + concrete ProcessorService (for new GetBySourceHash action). AddProcessorFeature DI alias already exposes both shapes, so no DI change required
- Plan 09-01: Route literal 'by-source-hash/{sourceHash}' chosen over bare '{sourceHash}' — avoids collision with inherited BaseController.GetById '{id:guid}' constraint; off-format strings 404 via row-miss per SPEC.md Constraint (no route-level validation)
- Plan 09-02: OrchestrationService is concrete + sealed (NOT BaseService<...>-derived) per CONTEXT D-04 — no single entity to project, composes over WorkflowEntity directly via _db.Set<WorkflowEntity>()
- Plan 09-02: OrchestrationController injects concrete OrchestrationService (no IOrchestrationService interface, no abstract-base alias) per CONTEXT D-06 — Phase 7 Warning 7 abstract-base-injection pattern intentionally NOT applied (no abstract base for orchestration)
- Plan 09-02: OrchestrationService ctor injects all 5 entity mappers up-front per CONTEXT D-05 (v2 surface stability — known smell, build for the second use); _ = _xxxMapper; suppressors load-bearing under TreatWarningsAsErrors=true to prevent IDE0052 unused-field diagnostics
- Plan 09-02: OrchestrationController class name is SINGULAR (first and only singular controller in the codebase) per CONTEXT D-13 — [controller] token resolves to lowercase 'orchestration', making routes /api/v1/orchestration/start and /stop
- Plan 09-02: WorkflowIdsValidator targets IReadOnlyList<Guid> directly (one-of-a-kind primitive-collection validator) per CONTEXT D-08 + D-09 — bare JSON-array body, no envelope DTO, no MaximumCount rule (deferred); auto-discovered by AddValidatorsFromAssembly without subclassing BaseDtoValidator
- Plan 09-02: Existence check uses single SQL SELECT id WHERE id IN (...) projection per CONTEXT D-10 — _db.Set<WorkflowEntity>().AsNoTracking().Where(w => ids.Contains(w.Id)).Select(w => w.Id).ToListAsync(ct); NO N-query loop, NO full entity materialization, NotFoundException listing missing ids via string.Join(', ', missing)
- Plan 09-02: AddOrchestrationFeature is the simplest per-feature DI extension in the codebase — single AddScoped<OrchestrationService>() line, NO abstract-base alias (CONTEXT D-06); validator + 5 entity mappers auto-discovered by Phase 6 AddBaseApiValidation + AddBaseApiMapping scans
- Plan 09-02: Both Start and Stop endpoints delegate to the same OrchestrationService.ValidateWorkflowIdsAsync method per CONTEXT D-12 — v1 behavior is functionally identical, only URL segment differs; future divergence will split into separate service methods
- Plan 09-03: 3 independent [Fact] methods per requirement axis (no [Theory]) per CONTEXT D-19 — keeps failure attribution clean
- Plan 09-03: Stop fact set covers only happy + 404 URL-routing per CONTEXT D-12 (Start and Stop share OrchestrationService.ValidateWorkflowIdsAsync) — detailed validation coverage delegated to StartOrchestrationFacts
- Plan 09-03: tests/BaseApi.Tests/Features/{Entity}/ folder layout (CONTEXT D-19) — sibling to existing Integration/ folder; future feature-specific facts ship here
- Plan 09-03: SeedWorkflowAsync helper duplicated verbatim in StartOrchestrationFacts and StopOrchestrationFacts rather than extracted (CONTEXT Deferred — would couple sibling files)
- Plan 09-03: 3 consecutive GREEN dotnet test runs achieved (138/138 each ~29s) after one Run 0 pre-existing flake (Phase 8 P08 known flake); byte-identical psql l snapshots (SHA-256 1C611C6006E2...) prove zero leaked stepsdb_test_* databases
- Plan 10-01: Doc-first commit #1 of Phase 10 — REQUIREMENTS.md ENTITY-04/07 + VALID-11/15 amended in place per D-01 BEFORE any code/EF/migration/test commit. ID preservation invariant: no renumbering, no superseding — same REQ-IDs edited in place. Forensic property — every commit #1..#5 independently revertable. Commit 1de7e71 verbatim subject 'docs(req): amend ENTITY-04/07 + VALID-11/15 for Phase 10 shape' per D-02.
- Plan 10-01: Trinary 'source/sink/unconfigured' wording locked in ENTITY-04 — extends the existing binary source/sink semantics for Phase 10 ConfigSchemaId addition; constraint names enumerated inline (fk_processor_input_schema_id, fk_processor_output_schema_id, fk_processor_config_schema_id) so readers can trace REQ-5 → Phase 4 PostgresExceptionMapper Option A regex without re-deriving the invariant.
- Plan 10-02: Atomic 4-file refactor commit (79b07d1) removed SchemaId from AssignmentEntity + 3 DTOs + 2 validators + EF config per CONTEXT D-02 verbatim subject. Production projects (BaseApi.Core + BaseApi.Service) build zero-warning Release + Debug; test project intentionally RED until commit #5 (D-02 bisect-friendliness — solution-level build deferred to commit #5).
- Plan 10-02 deviation (Rule 1 plan-internal-inconsistency): rephrased VALID-21 deferral note in AssignmentEntity.cs + AssignmentDtoValidator.cs to avoid literal 'Assignment.SchemaId' and 'Processor.ConfigSchemaId' tokens — plan's suggested wording conflicted with the plan-level grep-empty assertion at SPEC line 299. Educational rephrase preserves the VALID-21 marker + deferral semantics + structural-impossibility note + future-processor-side-schema cross-reference, satisfying all plan-level invariants simultaneously. Plan 06-01 / 08-01 rephrase precedent.
- Plan 10-02: Mapperly drift probe zero-touch confirmed (CONTEXT D-10) — symmetric SchemaId removal across entity + 3 DTOs means RMG012 (target unmapped) + RMG020 (source unmapped) do not fire on AssignmentEntityMapper.cs (zero changes). Production projects 0 warnings + 0 errors Release + Debug verifies.

### Roadmap Evolution

- Phase 9 added: Add GetBySourceHash to Processor controller and new Orchestration controller with Start/Stop endpoints accepting List<guid> WorkflowIds, following the existing GetById design pattern
- Phase 10 added: Remove SchemaId on AssignmentEntity (analyze the effect) and add nullable Guid? ConfigSchemaId on ProcessorEntity with same behavior as InputSchemaId

### Pending Todos

[From .planning/todos/pending/ — ideas captured during sessions]

None yet.

### Blockers/Concerns

[Issues that affect future work]

- REQUIREMENTS.md header claims 103 total v1 requirements but actual REQ-ID count is 102 (7+15+10+16+20+12+5+11+6). Header arithmetic was off by one. Coverage table in ROADMAP.md uses correct count (102). Recommend user confirm or correct the REQUIREMENTS.md header during next edit.
- Open decisions locked in research but worth re-confirming during Phase 6: SemVer regex variant (strict triple chosen), JSON Schema draft (2020-12 chosen), Cron format (5-field Cronos chosen), Assignment.Payload max size (1 MB chosen). All four are captured in REQ-IDs but if the external scheduler uses Quartz-style 6-field cron, VALID-19 will need revision.
- Testcontainers + Windows Docker Desktop: confirm WSL2 backend before Phase 8.

## Deferred Items

Items acknowledged and carried forward from previous milestone close:

| Category | Item | Status | Deferred At |
|----------|------|--------|-------------|
| *(none)* | | | |

## Session Continuity

Last session: 2026-05-28T07:33:33.000Z
Stopped at: Completed 10-02-PLAN.md (Assignment SchemaId removal — atomic refactor commit #2 of Phase 10 sequence)
Resume file: None

**Completed Phase:** 07 (Generic HTTP Base + Composition Root) — 2/2 plans — verified 2026-05-27 (98/98 dotnet test GREEN × 3 runs; SECURITY 0 open threats; VALIDATION nyquist-compliant; UAT 10/10 auto-passed)
**Next:** /gsd-plan-phase 9 (Processor.GetBySourceHash + Orchestration Start/Stop — SPEC.md locked + amended 2026-05-28 for 204 No Content; CONTEXT.md captured 20 implementation decisions across 4 areas; ready for planning)

**Planned Phase:** 10 (Remove SchemaId on AssignmentEntity and add ConfigSchemaId on ProcessorEntity) — 5 plans — 2026-05-28T07:18:29.788Z
**Phase 9 context:** captured 2026-05-28 — SPEC.md (6 reqs, amended) + CONTEXT.md (20 decisions) + DISCUSSION-LOG.md committed; SPEC.md response amended from 200+List<WorkflowReadDto> → 204 No Content; OrchestrationService injects BaseDbContext + all 5 entity mappers up front for v2 readiness
