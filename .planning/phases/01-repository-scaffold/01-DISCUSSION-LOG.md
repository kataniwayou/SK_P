# Phase 1: Repository Scaffold - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-26
**Phase:** 01-repository-scaffold
**Areas discussed:** Build strictness defaults, NuGet pin scope, Scaffold depth, Repo hygiene & dev config

---

## Build Strictness Defaults

### Q1: Where should shared MSBuild settings (Nullable, ImplicitUsings, LangVersion, warning policy) live?

| Option | Description | Selected |
|--------|-------------|----------|
| Directory.Build.props at repo root | One file at /Directory.Build.props that all *.csproj inherit automatically. Industry-standard for monorepos. | ✓ |
| Per-project <PropertyGroup> in each .csproj | Each csproj declares its own flags. Easier to see per-project but drifts. | |
| Hybrid: Directory.Build.props + per-project overrides | Defaults in root, project-specific tweaks in csproj. | |

**User's choice:** Directory.Build.props at repo root

### Q2: TreatWarningsAsErrors policy?

| Option | Description | Selected |
|--------|-------------|----------|
| true globally (strictest) | Phase 1 SC is 'zero warnings' — enforces at compile time. | ✓ |
| true in Release config only | Local dev sees warnings; CI/Release blocks. | |
| false (warnings stay warnings) | Relies on review to enforce. | |

**User's choice:** true globally

### Q3: Language-level strictness bundle?

| Option | Description | Selected |
|--------|-------------|----------|
| Modern strict | Nullable=enable, ImplicitUsings=enable, LangVersion=latest, AnalysisMode=latest, EnforceCodeStyleInBuild=true | ✓ |
| Modern moderate | Nullable=enable, ImplicitUsings=enable, LangVersion=12.0, AnalysisMode=Recommended | |
| Minimal baseline | Nullable=enable only; ImplicitUsings off; LangVersion at SDK default | |

**User's choice:** Modern strict

### Q4: Mapperly analyzer diagnostics (MP0001/MP0011/MP0020/MP0021) — promote to errors in Phase 1 or Phase 6?

| Option | Description | Selected |
|--------|-------------|----------|
| Phase 6 (when Mapperly lands) | Promote in Validation+Mapping Base phase via WarningsAsErrors in Service csproj. Keeps Phase 1 scope tight. | ✓ |
| Phase 1 (lock now in Directory.Build.props) | Add MP-codes now even though Mapperly isn't installed. No-op until Phase 6 but ensures it can't be forgotten. | |

**User's choice:** Phase 6

---

## NuGet Pin Scope

### Q1: How many STACK-verified package versions should Directory.Packages.props pin in Phase 1?

| Option | Description | Selected |
|--------|-------------|----------|
| Front-load all 14 STACK pins now | Pin every verified version. Future phases only add PackageReference in csproj. | ✓ |
| Phase-aligned: pin only Phase 1 needs | Add pins as each phase introduces packages. | |
| Two-tier: Phase 1 + Phase 2 essentials | Compromise between front-load and minimal-now. | |

**User's choice:** Front-load all 14 STACK pins now

### Q2: EFCore.NamingConventions pin strategy?

| Option | Description | Selected |
|--------|-------------|----------|
| Pin to latest 8.x exact (8.0.3) | Resolve the exact patch now; locks against silent drift. | ✓ |
| Pin to 8.0.* floating | Auto-track 8.x patches; breaks reproducible builds without lockfile. | |
| Skip now, add in Phase 3 | Defer to when EF base lands. | |

**User's choice:** Pin to latest 8.x exact (8.0.3)

### Q3: ManagePackageVersionsCentrally placement?

| Option | Description | Selected |
|--------|-------------|----------|
| Directory.Packages.props only (SDK default) | SDK auto-enables CPM when file exists. Simplest. | ✓ |
| Also set CentralPackageTransitivePinningEnabled=true | Stronger transitive guarantee but can over-constrain. | |
| Lock CPM warnings to errors | Treat NU1604/NU1010 as errors. | |

**User's choice:** Directory.Packages.props only

### Q4: PrivateAssets/ExcludeAssets convention for source-generator packages?

| Option | Description | Selected |
|--------|-------------|----------|
| Lock the convention in a comment in Directory.Packages.props now | Future phases see the rule before adding refs. | ✓ |
| Defer documentation to the phases that adopt them | Each phase's plan includes correct syntax. | |

**User's choice:** Lock the convention in a comment in Directory.Packages.props now

---

## Scaffold Depth

### Q1: BaseApi.Core folder structure — pre-create the locked architecture skeleton, or leave empty?

| Option | Description | Selected |
|--------|-------------|----------|
| Pre-create with .gitkeep | Entities/, Persistence/{Interceptors,Repositories}/, Services/, Controllers/, Validation/, Middleware/, ErrorHandling/, Health/, Telemetry/, DependencyInjection/ | ✓ |
| Only top-level placeholder | Single Class1.cs or nothing. Phases create folders as needed. | |
| Pre-create top-level only, not subfolders | Entities/, Persistence/, Services/ as top-level only — phases nest as files arrive. | |

**User's choice:** Pre-create with .gitkeep

### Q2: BaseApi.Service folder structure — scaffold which now?

| Option | Description | Selected |
|--------|-------------|----------|
| Pre-create Features/ + Persistence/Configurations/ + Persistence/AppDbContext placeholder | Locks feature-folder convention from day one. | ✓ |
| Only top-level Persistence/ folder | Features/ deferred until Phase 8. | |
| Truly empty Service project | All folders born as Phase 3+ introduces them. | |

**User's choice:** Pre-create Features/ + Persistence/Configurations/ + Persistence/AppDbContext placeholder

### Q3: Initial Program.cs content in BaseApi.Service?

| Option | Description | Selected |
|--------|-------------|----------|
| Default WebApplication.CreateBuilder + Run | Standard webapi template output: builder, AddControllers, MapControllers, Run. Boots empty host, 404s on everything. | ✓ |
| Truly minimal Program.cs | Single line: WebApplication.CreateBuilder(args).Build().Run(); | |
| Skeleton with TODO comments naming the AddBaseApi insertion point | Default + // TODO(Phase 7) markers. | |

**User's choice:** Default WebApplication.CreateBuilder + Run

### Q4: BaseApi.Tests project content in Phase 1?

| Option | Description | Selected |
|--------|-------------|----------|
| Scaffold packages + one trivial test | xunit.v3 refs + MetaTest.cs with Assert.True(true). Proves test stack wires; dotnet test from repo root passes. | ✓ |
| Scaffold packages only, no test | xUnit v3 refs only; dotnet test runs but reports zero tests. | |
| Empty test project (no xUnit refs yet) | Phase 8 adds xUnit + Testcontainers together. | |

**User's choice:** Scaffold packages + one trivial test

---

## Repo Hygiene & Dev Config

### Q1: Solution file format?

| Option | Description | Selected |
|--------|-------------|----------|
| .sln classic | Standard Microsoft format. Works with VS, Rider, VS Code C# Dev Kit, dotnet CLI, all CI. | ✓ |
| .slnx (XML, preview) | Human-readable, diff-friendly. Still preview-flagged in some toolchains. | |
| No solution file (folder-based) | Skip .sln. Breaks VS/Rider 'open folder' for solution-aware features. | |

**User's choice:** .sln classic

### Q2: .editorconfig scope?

| Option | Description | Selected |
|--------|-------------|----------|
| Full .NET-idiomatic ruleset | Microsoft .NET .editorconfig baseline: indent, naming, var rules, file-scoped namespaces, etc. Pairs with EnforceCodeStyleInBuild=true. | ✓ |
| Minimal hygiene rules only | indent_style/size, EOL, charset, final newline, trim trailing whitespace. | |
| Defer .editorconfig to a later phase | Skip in Phase 1; add when convention disputes arise. | |

**User's choice:** Full .NET-idiomatic ruleset

### Q3: Dev secrets / connection string strategy?

| Option | Description | Selected |
|--------|-------------|----------|
| appsettings.Development.json (committed, dev-only placeholders) | localhost Postgres connection string for docker-compose dev. Committed (dev defaults, not real secrets). Env-var override via config provider chain. | ✓ |
| Initialize User Secrets (dotnet user-secrets init) | Per-developer secrets.json. Useful when real secrets appear (v2 auth). | |
| Env-vars only via .env file (with .env.example committed) | docker-compose 12-factor style. | |
| Both appsettings.Development.json + .env.example | Belt-and-suspenders. | |

**User's choice:** appsettings.Development.json (committed, dev-only placeholders)

### Q4: .gitignore + README — generate now or defer?

| Option | Description | Selected |
|--------|-------------|----------|
| Both now: dotnet .gitignore + README with quickstart | Standard 'dotnet new gitignore' output + README (prereqs, quickstart, .planning/PROJECT.md link). | ✓ |
| Just .gitignore (dotnet-flavored) | Skip README in Phase 1. | |
| Neither in Phase 1 — add later | Defer to Phase 2 or Phase 8. | |

**User's choice:** Both now

---

## Claude's Discretion

- Exact content of the .NET .editorconfig ruleset — planner/executor may copy from dotnet/runtime or dotnet/aspnetcore baseline
- README exact prose (one-page max; preserve prereqs + quickstart + .planning link bullets)
- `.gitattributes` line-ending normalization (* text=auto eol=lf) — strongly recommended on Windows host targeting Linux containers
- LICENSE file — deferred unless user signals otherwise

## Deferred Ideas

- Mapperly MP-code warnings-as-errors → Phase 6
- Production secrets handling (Vault, Key Vault) → v2 (no auth in v1)
- Migration locking / pg_advisory_lock → v1.x (single-replica today)
- CI scaffolding (GitHub Actions YAML) → not yet scheduled; may be Phase 8 or a separate phase
