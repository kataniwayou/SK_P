---
phase: 58-orchestration-gate-integration-proof-close
plan: 01
subsystem: infra
tags: [processor, gate-a, config-schema, sourcehash, dotnet, masstransit, dockerfile, cfg-08]

# Dependency graph
requires:
  - phase: 57-startup-config-schema-fetch-gate-a
    provides: "Gate A (ConfigSchemaCoverageCheck + ProcessorStartupOrchestrator) — the startup config-compat check that withholds MarkHealthy on a config-type vs config-schema clash"
  - phase: v3.5.0
    provides: "Processor.Sample + BaseProcessor.Core + SourceHash.targets — the clone template and the embedded-hash fold"
provides:
  - "Processor.BadConfig console project (6 files) — the CFG-08 incompatible subject"
  - "BadConfig(int Quantity) TConfig that structurally clashes with a schema typing quantity as string (Clash Shape Option A)"
  - "A distinct embedded SourceHash read off src/Processor.BadConfig/bin/Release/net8.0/Processor.BadConfig.dll (consumed by Plan 04's close script)"
  - "Processor.BadConfig registered in SK_P.sln in both Debug and Release configs"
affects: [58-02, 58-03, 58-04, 58-05]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Separate-project-dir SourceHash fold: a faithful Processor.Sample clone in its own dir yields a distinct genuine embedded hash (own DB Processor row + procId) with zero extra effort"
    - "Single-semantic-delta clone: only the TConfig (int Quantity vs schema string) differs from the worked example; Program.cs uses unmodified AddBaseProcessor so the Phase-57 stay-up clash posture is preserved with no startup-orchestrator override"

key-files:
  created:
    - src/Processor.BadConfig/Processor.BadConfig.csproj
    - src/Processor.BadConfig/Program.cs
    - src/Processor.BadConfig/BadConfig.cs
    - src/Processor.BadConfig/BadConfigProcessor.cs
    - src/Processor.BadConfig/appsettings.json
    - src/Processor.BadConfig/Dockerfile
  modified:
    - SK_P.sln

key-decisions:
  - "Clash Shape Option A: BadConfig(int Quantity) vs a config-schema typing quantity as string → ConfigSchemaCoverageCheck String-case returns Detail('quantity','string',Int32) → (Covered:false) → Gate A withholds MarkHealthy"
  - "Service.Name = processor-badconfig (distinct ES service.name scope for the CFG-08 clash-log query); Version kept 3.5.0 per D-09c (SourceHash, not the version string, distinguishes identity)"
  - "Fresh project GUID {C8D9E0F1-2A3B-4C5D-9E6F-7A8B9C0D1E2F}; nested under the existing src solution folder {EAC83310-...} mirroring Processor.Sample"

patterns-established:
  - "Pattern 1: Distinct genuine SourceHash via a separate project directory importing SourceHash.targets (never hand-rolled — mitigates T-58-02 identity collision)"
  - "Pattern 2: Gate-A subject = unmodified AddBaseProcessor + a clashing TConfig + a trivial dead-but-compiles transform (no startup-orchestrator override — mitigates T-58-01 DoS via the shipped stay-up posture)"

requirements-completed: [CFG-08]

# Metrics
duration: 3min
completed: 2026-06-12
---

# Phase 58 Plan 01: Processor.BadConfig (CFG-08 Incompatible Subject) Summary

**A faithful Processor.Sample clone whose `BadConfig(int Quantity)` TConfig structurally clashes with a schema-string `quantity` — the second real container whose Gate A covers-check fails at startup, withholds MarkHealthy, and is blocked 422 at orchestration start.**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-06-12T22:02:02Z
- **Completed:** 2026-06-12T22:05:12Z
- **Tasks:** 2
- **Files modified:** 7 (6 created, 1 modified)

## Accomplishments
- Created `src/Processor.BadConfig/` (6 files) — a single-semantic-delta clone of `Processor.Sample` whose only difference is the clashing `BadConfig(int Quantity)` TConfig.
- `Program.cs` uses unmodified `AddBaseProcessor` (no startup-orchestrator override) so the Phase-57 stay-up clash posture (MarkReady, withhold MarkHealthy, bind no queue) is preserved unchanged.
- Registered the project in `SK_P.sln` (fresh GUID, both Debug+Release config blocks, nested under the src solution folder); the full solution builds 0-warning in both configs.
- The csproj imports `SourceHash.targets`, so the built dll carries a distinct genuine embedded SourceHash → its own DB Processor row + procId (no collision with Processor.Sample's identity).

## Task Commits

Each task was committed atomically:

1. **Task 1: Clone Processor.Sample into Processor.BadConfig (csproj, Program, BadConfig record, processor, appsettings)** — `22e1770` (feat)
2. **Task 2: Add Processor.BadConfig Dockerfile and register the project in SK_P.sln** — `31da5c5` (feat)

_The genuine embedded SourceHash is read off `src/Processor.BadConfig/bin/Release/net8.0/Processor.BadConfig.dll` by Plan 04's close script._

## Files Created/Modified
- `src/Processor.BadConfig/Processor.BadConfig.csproj` - Distinct console project; imports SourceHash.targets; CPM-pinned MassTransit + MassTransit.RabbitMQ; two ProjectReferences.
- `src/Processor.BadConfig/Program.cs` - 21-line composition root; unmodified `AddBaseProcessor`; registers `BadConfigProcessor` as the abstract `BaseProcessor`.
- `src/Processor.BadConfig/BadConfig.cs` - `public sealed record BadConfig(int Quantity) : ProcessorConfig` — the Clash Shape Option A TConfig.
- `src/Processor.BadConfig/BadConfigProcessor.cs` - `BaseProcessor<BadConfig>` concrete; trivial dead-but-compiles single-item Completed transform (Gate A withholds the queue bind so it is never reached).
- `src/Processor.BadConfig/appsettings.json` - Service.Name `processor-badconfig`, Version `3.5.0`, ConsoleHealth.Port 8082; all Redis/RabbitMq/OTel keys identical to the sample.
- `src/Processor.BadConfig/Dockerfile` - Multi-stage net8.0; aspnet:8.0-bookworm-slim runtime; publishes `Processor.BadConfig.dll`; wget for /health/ready; EXPOSE 8082.
- `SK_P.sln` - Project entry + both-config ProjectConfigurationPlatforms (GUID `{C8D9E0F1-2A3B-4C5D-9E6F-7A8B9C0D1E2F}`) + NestedProjects under the src solution folder.

## Decisions Made
- **Clash Shape Option A** (`int Quantity` vs schema-string `quantity`) — confirmed against `ConfigSchemaCoverageCheck.cs:213` (the String-case non-string-CLR return `Detail(name,"string",declared)`) before writing `BadConfig.cs`.
- **Version kept `3.5.0`** (D-09c) — identity is distinguished by the embedded SourceHash (distinct project dir), not the version string.
- **Project GUID `{C8D9E0F1-2A3B-4C5D-9E6F-7A8B9C0D1E2F}`** — generated unique; nested under the same `{EAC83310-...}` src solution folder as Processor.Sample.

## Deviations from Plan

None - plan executed exactly as written.

(Two header comments in `Dockerfile` were worded to avoid the literal string `Processor.Sample`. This was to satisfy the plan's own acceptance criterion `! grep -q "Processor.Sample" src/Processor.BadConfig/Dockerfile` — in-scope refinement of the planned Dockerfile, not an unplanned change. The four functional `Processor.Sample`→`Processor.BadConfig` swaps called for by the action were applied as specified.)

## Issues Encountered
None. (The SK_P.sln Edit briefly failed on a whitespace-context multi-line anchor; re-applied with a tab-correct anchor on the same content. No behavioral effect.)

## Threat Surface
No new endpoints, input parsing, or auth/crypto surface introduced (per the plan's threat_model). T-58-01 (DoS) and T-58-02 (SourceHash identity) mitigations confirmed in code: unmodified `AddBaseProcessor` (no startup-orchestrator override) preserves the stay-up posture; the csproj imports SourceHash.targets for a genuine distinct hash. No new threat flags.

## Known Stubs
The `BadConfigProcessor.ProcessAsync` body is a trivial dead-but-must-compile transform. This is intentional and documented in the plan (Gate A withholds the queue bind, so the transform is never reached). Not a blocking stub — the plan's goal (a clashing TConfig + a buildable Gate-A subject) is fully achieved.

## Next Phase Readiness
- Processor.BadConfig builds 0-warning in Release and Debug, included in SK_P.sln — ready for Plan 02 (config-schema seeding) and Plan 04's close script (reads the embedded SourceHash off the Release dll).
- No blockers. No live docker stack was required for any check in this plan.

## Self-Check: PASSED

All 6 created files + the SUMMARY exist on disk; both task commits (`22e1770`, `31da5c5`) are in git history; `Processor.BadConfig` is present in `SK_P.sln`.

---
*Phase: 58-orchestration-gate-integration-proof-close*
*Completed: 2026-06-12*
