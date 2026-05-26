# Steps API (SK_P)

A .NET 8 Web API modular monolith providing CRUD over a workflow-engine data model — Schema, Processor, Step, Assignment, Workflow — built on a reusable base library (`BaseApi.Core`) and a single runnable service (`BaseApi.Service`). The service ships logs, metrics, and traces to an OpenTelemetry Collector and runs against PostgreSQL via Docker Compose.

> Detailed project context, locked decisions, and the full requirements list live under [`.planning/`](./.planning/) — start with [`.planning/PROJECT.md`](./.planning/PROJECT.md).

## Prereqs

| Tool | Version | Why |
|------|---------|-----|
| .NET SDK | **8.0.421** (pinned via `global.json`) | All projects target `net8.0`; SDK pin prevents float to .NET 9/10. Verify with `dotnet --version` at the repo root. |
| Docker Desktop | latest, **WSL2 backend** (Windows hosts) | Required for the local Postgres container (Phase 2) and for `Testcontainers.PostgreSql`-backed integration tests (Phase 8). |
| Git | any modern | Repo is a git repo; line endings forced to LF via `.gitattributes`. |

If `dotnet --version` returns anything other than `8.0.421`, install the .NET 8.0.421 SDK from <https://dotnet.microsoft.com/download/dotnet/8.0> — `global.json` will then resolve to it.

## Quickstart

From the repo root (`SK_P/`):

```powershell
# 1. Verify the SDK pin resolved
dotnet --version
# expected: 8.0.421

# 2. Restore NuGet packages (resolves against Directory.Packages.props)
dotnet restore

# 3. Build the solution (warnings-as-errors via Directory.Build.props)
dotnet build

# 4. Run the test suite (currently a single sanity test in BaseApi.Tests)
dotnet test

# 5. (Optional) Run the empty webapi — returns HTTP 404 on every path until later phases register routes
dotnet run --project src/BaseApi.Service
```

## Project Layout

```
SK_P/
├── global.json                      # SDK pin (8.0.421)
├── Directory.Build.props            # repo-wide MSBuild: warnings-as-errors, nullable, latest analyzers
├── Directory.Packages.props         # central NuGet version pins (all 22 packages)
├── .editorconfig                    # Microsoft .NET style ruleset; enforced at build via EnforceCodeStyleInBuild
├── SK_P.sln                         # solution file (classic .sln)
├── src/
│   ├── BaseApi.Core/                # reusable class library (entities, persistence base, middleware, DI extensions)
│   └── BaseApi.Service/             # runnable webapi (Program.cs, appsettings, feature folders, migrations)
├── tests/
│   └── BaseApi.Tests/               # xUnit v3 — unit + integration tests
└── .planning/                       # GSD project artifacts (decisions, research, phases). Tracked, not ignored.
```

## More

- Architectural decisions and constraints: [`.planning/PROJECT.md`](./.planning/PROJECT.md)
- Requirements list: [`.planning/REQUIREMENTS.md`](./.planning/REQUIREMENTS.md)
- Phase-by-phase roadmap: [`.planning/ROADMAP.md`](./.planning/ROADMAP.md)
- Tech stack pin provenance: [`.planning/research/STACK.md`](./.planning/research/STACK.md)
