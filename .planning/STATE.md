---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: milestone
status: ready_to_plan
stopped_at: Completed 05-02-PLAN.md
last_updated: "2026-05-27T10:25:00.000Z"
last_activity: 2026-05-27
progress:
  total_phases: 8
  completed_phases: 6
  total_plans: 11
  completed_plans: 11
  percent: 75
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-05-26)

**Core value:** A solid, observable, validated CRUD foundation that future workflow-platform features build on without rework.
**Current focus:** Phase 5 — Observability + Health Probes

## Current Position

Phase: 6
Plan: Not started
Status: Ready to plan
Last activity: 2026-05-27

Progress: [██████████] 100% (planned plans complete; Phase 6-8 plans TBD)

## Performance Metrics

**Velocity:**

- Total plans completed: 13
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

Last session: 2026-05-27T10:25:00.000Z
Stopped at: Completed 05-02-PLAN.md (verification battery shipped — Phase 5 closed)
Resume file: None

**Planned Phase:** 5 (Observability + Health Probes) — 2 plans — COMPLETE 2026-05-27
**Next:** /gsd-plan-phase 6 (Validation + Mapping Base — BaseDtoValidator + FluentValidation DI + Mapperly IEntityMapper seam; covers VALID-01..07 + HTTP-10)
