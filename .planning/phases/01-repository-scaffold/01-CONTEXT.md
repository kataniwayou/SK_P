# Phase 1: Repository Scaffold - Context

**Gathered:** 2026-05-26
**Status:** Ready for planning

<domain>
## Phase Boundary

Establish the .NET 8 solution layout, SDK pin, central package management, build-strictness defaults, scaffold folder structure, and configuration skeleton so every subsequent phase compiles against a known toolchain with zero warnings. This phase produces compilable-but-empty `BaseApi.Core` (class library), `BaseApi.Service` (webapi that boots and 404s on everything), and `BaseApi.Tests` (xUnit v3 with one sanity test).

Out of this phase: Postgres container (Phase 2), EF Core base (Phase 3), any cross-cutting middleware/error/OTel (Phases 4-5), HTTP base (Phase 7), concrete entities (Phase 8).

</domain>

<decisions>
## Implementation Decisions

### Build Strictness (Directory.Build.props)
- **D-01:** Shared MSBuild settings live in a single `Directory.Build.props` at repo root — all `*.csproj` inherit automatically. No per-project overrides for global concerns; csproj only carries project-specific package references and target framework.
- **D-02:** `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` globally (all configurations). Phase 1 SC #1 ("zero warnings") is enforced at compile time, not via post-hoc review.
- **D-03:** Language-level strictness bundle (set in Directory.Build.props):
  - `<Nullable>enable</Nullable>`
  - `<ImplicitUsings>enable</ImplicitUsings>`
  - `<LangVersion>latest</LangVersion>`
  - `<AnalysisMode>latest</AnalysisMode>`
  - `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`
- **D-04:** Mapperly diagnostics (MP0001, MP0011, MP0020, MP0021) are NOT promoted to errors in Phase 1 — that responsibility moves to Phase 6 (Validation + Mapping Base) where Mapperly is actually referenced. Phase 6 will add `<WarningsAsErrors>MP0001;MP0011;MP0020;MP0021</WarningsAsErrors>` to the `BaseApi.Service` csproj.

### NuGet Pin Scope (Directory.Packages.props)
- **D-05:** Front-load ALL 14 STACK-verified versions in `Directory.Packages.props` in Phase 1. Each subsequent phase only adds `<PackageReference Include="..." />` in csproj; the version is already pinned centrally. Pin list (authoritative, from research/STACK.md):
  - `Microsoft.EntityFrameworkCore` **8.0.27**
  - `Microsoft.EntityFrameworkCore.Design` **8.0.27** (PrivateAssets=all)
  - `Microsoft.EntityFrameworkCore.Relational` **8.0.27**
  - `Npgsql.EntityFrameworkCore.PostgreSQL` **8.0.10**
  - `EFCore.NamingConventions` **8.0.3** (exact, latest 8.x as of 2026-05-26)
  - `Riok.Mapperly` **4.3.1** (PrivateAssets=all, ExcludeAssets=runtime)
  - `FluentValidation` **12.1.1**
  - `FluentValidation.DependencyInjectionExtensions` **12.1.1`
  - `OpenTelemetry` **1.15.3**
  - `OpenTelemetry.Extensions.Hosting` **1.15.3**
  - `OpenTelemetry.Exporter.OpenTelemetryProtocol` **1.15.3**
  - `OpenTelemetry.Instrumentation.AspNetCore` **1.15.0**
  - `OpenTelemetry.Instrumentation.Http` **1.15.0**
  - `JsonSchema.Net` **9.2.1**
  - `Cronos` **0.13.0**
  - `AspNetCore.HealthChecks.NpgSql` **9.0.0**
  - `Microsoft.AspNetCore.Mvc.Testing` **8.0.27**
  - `xunit.v3` **3.2.2**
  - `xunit.v3.assert` **3.2.2**
  - `xunit.runner.visualstudio` (latest compatible with v3 3.2.2)
  - `Testcontainers.PostgreSql` **4.11.0**
  - `Asp.Versioning.Http` (latest stable 8.x — to be resolved at Phase 7; pin now or in Phase 7)
  - `Swashbuckle.AspNetCore` (latest stable 6.x — to be resolved at Phase 7; pin now or in Phase 7)
  - *(Note: STACK research listed 14 core packages; Asp.Versioning.Http and Swashbuckle.AspNetCore are mentioned in REQUIREMENTS HTTP-15/HTTP-16 but exact versions were not part of the STACK pin table — researcher to resolve current stable in Phase 1.)*
- **D-06:** `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` set only inside `Directory.Packages.props`. SDK auto-enables CPM when the file is present. No duplication in Directory.Build.props.
- **D-07:** Source-generator convention documented as a header comment in `Directory.Packages.props`: Mapperly requires `PrivateAssets='all' ExcludeAssets='runtime'`; EFCore.Design requires `PrivateAssets='all'`. Comment lives at the file root so future phases see the rule before adding the reference.

### Scaffold Depth
- **D-08:** Pre-create the locked architecture folder skeleton in `BaseApi.Core` with `.gitkeep` markers in each empty folder:
  - `Entities/`
  - `Persistence/`, `Persistence/Interceptors/`, `Persistence/Repositories/`
  - `Services/`
  - `Controllers/`
  - `Validation/`
  - `Middleware/`
  - `ErrorHandling/`
  - `Health/`
  - `Telemetry/`
  - `DependencyInjection/`
- **D-09:** Pre-create `BaseApi.Service` folders: `Features/` (entity feature-folder root; empty in Phase 1, populated in Phase 8), `Persistence/`, `Persistence/Configurations/` (for IEntityTypeConfiguration<T>). All with `.gitkeep`.
- **D-10:** `BaseApi.Service/Program.cs` ships with default `WebApplication.CreateBuilder + AddControllers + MapControllers + Run()` scaffold so the host boots end-to-end (verifiable: `dotnet run` returns 404 on every path). Phase 7's `AddBaseApi/UseBaseApi` replaces the body.
- **D-11:** `BaseApi.Tests/` ships with `xunit.v3 + xunit.v3.assert + xunit.runner.visualstudio` references (versions from Directory.Packages.props) and one `MetaTest.cs` containing `[Fact] public void Sanity() => Assert.True(true);`. Proves the test stack wires correctly and `dotnet test` from repo root passes. Future regressions in test wiring caught immediately.

### Repo Hygiene & Dev Configuration
- **D-12:** Solution file is **`SK_P.sln` classic format** at repo root. Standard binary `.sln` — full VS/Rider/VS Code C# Dev Kit support. `.slnx` rejected (still preview-flagged in some toolchains).
- **D-13:** Adopt the full Microsoft-published .NET `.editorconfig` ruleset at repo root: 4-space indent, LF line endings, UTF-8 charset, file-scoped namespaces preferred, var usage rules, `IPascalCase`/`_camelCase` naming, using placement. Pairs with `EnforceCodeStyleInBuild=true` (D-03) — style violations become build errors.
- **D-14:** `appsettings.Development.json` (committed) ships alongside `appsettings.json` with localhost Postgres dev connection string defaults (placeholder host=localhost, port=5432, database=stepsdb, user=postgres, password=postgres — Phase 2 finalizes the compose values). PROJECT.md Out of Scope explicitly excludes auth/secrets in v1, so no real secrets to hide — User Secrets / .env handling deferred to v2 when an auth boundary appears.
- **D-15:** `.gitignore` is the dotnet-flavored standard (`bin/`, `obj/`, `.vs/`, `*.user`, `*.suo`, etc. — from `dotnet new gitignore`). Add `*.received.*` for snapshot testing and Rider `.idea/` folder if applicable.
- **D-16:** `README.md` at repo root: project description (Steps API — .NET 8 modular monolith CRUD over workflow data model), prereqs (.NET 8.0.421 SDK pinned via global.json; Docker Desktop with WSL2 backend per STATE.md concern for future Phase 8 Testcontainers), quickstart (`dotnet restore`, `dotnet build`, `dotnet test`), link to `.planning/PROJECT.md` for architectural detail.

### Claude's Discretion
- The exact content of the .NET `.editorconfig` ruleset: planner/executor may copy the Microsoft `dotnet/runtime` `.editorconfig` (or `dotnet/aspnetcore`) as the baseline rather than authoring from scratch.
- The README's exact prose — keep it short (one-page max), preserve the prereqs + quickstart + .planning link bullets.
- Whether to commit a `.gitattributes` file pinning `* text=auto eol=lf` (LF line endings on checkout) — strongly recommended on a Windows host targeting Linux containers; planner may include it.
- Whether to commit a top-level `LICENSE` file — internal project, defer unless user signals otherwise.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Project specs (authoritative for this phase)
- `.planning/PROJECT.md` — Locked decisions table (22 entries), Out of Scope list, project structure plan, constraints (tech stack pins)
- `.planning/REQUIREMENTS.md` — INFRA-01..04 (Phase 1 scope); REQ-IDs that downstream phases will consume. Header arithmetic noted: 102 actual REQ-IDs (not 103).
- `.planning/ROADMAP.md` — Phase 1 goal, depends-on (nothing), 4 success criteria (the testable definition of done)

### Research artifacts (verified 2026-05-26)
- `.planning/research/STACK.md` — Authoritative version pin table for all 14 packages, `global.json` template, `Directory.Packages.props` template snippets
- `.planning/research/PITFALLS.md` — Phase-tagged pitfalls; Phase 1 ("P0 Repository scaffold") is light, but pitfalls 30 (JSON comments fail appsettings parse) and 39 (secrets in git) are Phase-1-relevant
- `.planning/research/SUMMARY.md` — Executive summary, locked stack table, "Phase 0 Repository Scaffold" item lists what to deliver and what to avoid
- `.planning/research/ARCHITECTURE.md` — Folder structure source of truth (Entities/, Persistence/, Services/, Controllers/, Validation/, Middleware/, ErrorHandling/, Health/, Telemetry/, DependencyInjection/ in Core; Features/, Persistence/Configurations/ in Service)
- `.planning/research/FEATURES.md` — Feature-vs-anti-feature framing; confirms `AddBaseApi`/`UseBaseApi` composition root pattern that Phase 1 leaves room for in Program.cs

### State
- `.planning/STATE.md` — Open concerns relevant to Phase 1: REQUIREMENTS.md header off-by-one (102 vs 103); Windows + Docker Desktop WSL2 backend confirmation needed before Phase 8 (mention in README prereqs)

### Authoritative external docs (for executor reference)
- https://learn.microsoft.com/en-us/dotnet/core/tools/global-json — `global.json` schema, `rollForward` policies
- https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management — Directory.Packages.props / CPM
- https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory — Directory.Build.props inheritance
- https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#nullable — `<Nullable>` settings
- https://github.com/dotnet/runtime/blob/main/.editorconfig — reference .editorconfig baseline

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- None — repository is greenfield (only `.git/` and `.planning/` exist at repo root). Phase 1 introduces the first code.

### Established Patterns
- No code patterns yet to inherit from. Phase 1 establishes them: Directory.Build.props inheritance, central package management, feature-folder convention in Service, layer-folder convention in Core.

### Integration Points
- `.planning/` directory is the only existing root entry — Phase 1's `.gitignore` MUST NOT ignore `.planning/` (it tracks project decisions and is intentionally version-controlled).
- `.git/` already initialized — Phase 1 commits build on top of an existing repo state.

</code_context>

<specifics>
## Specific Ideas

- **Source-generator package syntax** — Documented as a header comment in Directory.Packages.props because it's a frequent footgun: forgetting `PrivateAssets='all'` on Mapperly leaks the source-gen assembly to consumers; forgetting `ExcludeAssets='runtime'` causes a runtime DLL load that's never used.
- **global.json `rollForward`** — STACK research recommends `latestFeature`; planner should pin SDK="8.0.421" and rollForward="latestFeature" so dev machines without exactly 8.0.421 can use 8.0.422+ but not float to .NET 9/10.
- **Test framework choice (xUnit v3 over v2)** — Already locked by REQUIREMENTS.md TEST-01. xunit.v3 packages have different IDs than xunit (v2): use `xunit.v3` not `xunit`, and `xunit.v3.assert` not `xunit.assert`. Planner: verify exact package IDs against nuget.org during Phase 1 planning.

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within Phase 1 scope. Items intentionally moved to other phases:

- Mapperly MP-code warnings-as-errors → Phase 6 (Validation + Mapping Base) when Mapperly is actually referenced
- Connection-string production secrets / Vault / Key Vault → v2 (no auth/secrets surface in v1 per PROJECT.md Out of Scope)
- Migration locking / `pg_advisory_lock` → v1.x (single-replica today per REQUIREMENTS INFRA-08 v2 list)
- CI scaffolding (GitHub Actions / Azure Pipelines YAML, build matrix, NuGet feed publish) → not in any phase yet; could be Phase 8 or a separate CI phase if the user wants automated PR builds
- `.gitattributes`, `LICENSE` → Claude's discretion; can be added in Phase 1 if obvious or deferred

</deferred>

---

*Phase: 01-repository-scaffold*
*Context gathered: 2026-05-26*
