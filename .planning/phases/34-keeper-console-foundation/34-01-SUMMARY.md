---
phase: 34-keeper-console-foundation
plan: 01
subsystem: infra
tags: [keeper, masstransit, csproj, sln, cpm, messaging-contracts, console]

# Dependency graph
requires:
  - phase: 18-baseconsole-core
    provides: BaseConsole.Core reusable Generic-Host base (ProjectReference target)
  - phase: 17-messaging-contracts
    provides: Messaging.Contracts leaf assembly (home for the durable queue-name const)
  - phase: 19-orchestrator-console
    provides: Orchestrator.csproj + SK_P.sln entry — the byte-near clone analog for Keeper
provides:
  - "KeeperQueues.FaultRecovery = \"keeper-fault-recovery\" durable queue-name const in Messaging.Contracts"
  - "src/Keeper/Keeper.csproj — OutputType=Exe console shell with the leaner reference closure (no BaseApi.*/scheduler/cron-math)"
  - "SK_P.sln registration of the Keeper project (3-block edit; fresh GUID nested under src)"
affects: [34-02-keeper-console-body, 34-03-compose-and-tests, 35-keeper-fault-consumers]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Reference-firewall console csproj: clone Orchestrator, strip scheduler/cron packages, keep only BaseConsole.Core + Messaging.Contracts ProjectReferences"
    - "Durable shared queue-name const in Messaging.Contracts (mirrors OrchestratorQueues) survives the Phase-35 placeholder swap"

key-files:
  created:
    - src/Messaging.Contracts/KeeperQueues.cs
    - src/Keeper/Keeper.csproj
  modified:
    - SK_P.sln

key-decisions:
  - "Named the const FaultRecovery for its enduring Phase-35 role (D-08 discretion); placeholder message type stays LOCAL to Keeper (Plan 02), only the const goes to Contracts"
  - "Keeper GUID {D4E5F6A7-8B9C-4D0E-A1F2-3456789ABCDE} — genuinely fresh, console-type GUID {FAE04EC0-...} reused (same family as Orchestrator)"
  - "Reworded csproj comments to avoid the literal forbidden tokens (Quartz/Cronos/BaseApi/BaseProcessor/Version=) so the Plan-03 KeeperDependencyFirewallTests literal-grep + the plan's csproj acceptance greps both hold"

patterns-established:
  - "Pattern: leaner-than-Orchestrator console csproj — Sdk=Microsoft.NET.Sdk, Exe, MassTransit(+RabbitMQ) only, two ProjectReferences, CPM-clean"
  - "Pattern: enduring durable queue-name const in Messaging.Contracts as the Phase-35-stable endpoint name"

requirements-completed: [KEEP-01]

# Metrics
duration: 3min
completed: 2026-06-05
---

# Phase 34 Plan 01: Keeper Project Skeleton Summary

**Durable `KeeperQueues.FaultRecovery = "keeper-fault-recovery"` const in Messaging.Contracts plus the leaner-than-Orchestrator `Keeper.csproj` (OutputType=Exe, reference firewall: no BaseApi.*/scheduler/cron-math) registered in SK_P.sln via the coordinated 3-block edit.**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-06-05T14:24:08Z
- **Completed:** 2026-06-05T14:26:34Z
- **Tasks:** 3
- **Files modified:** 3 (2 created, 1 modified)

## Accomplishments
- Created the enduring `KeeperQueues.FaultRecovery` queue-name const in `Messaging.Contracts` (the Phase-35-stable competing-consumer endpoint name) — contracts assembly builds 0 warnings / 0 errors.
- Created `src/Keeper/Keeper.csproj`: OutputType=Exe console shell cloned from Orchestrator, dropping the scheduler + cron-math packages (D-07) and referencing ONLY `BaseConsole.Core` + `Messaging.Contracts` (reference firewall T-34-01) — CPM-clean, `dotnet restore` succeeds.
- Registered Keeper in `SK_P.sln` with the coordinated 3-block edit (Project decl + 4 ProjectConfigurationPlatforms + 1 NestedProjects under the `src` folder); fresh GUID consistent ×6; `dotnet sln list` shows `src\Keeper\Keeper.csproj`; no existing project entries disturbed.

## Task Commits

Each task was committed atomically (scoped paths only — the in-progress `.planning/` archive deletions + untracked `launchSettings.json`/`psql-*.txt` left untouched, NOT staged, NOT reverted):

1. **Task 1: KeeperQueues const in Messaging.Contracts** - `2de461e` (feat)
2. **Task 2: Keeper.csproj (leaner reference closure)** - `9838b04` (feat)
3. **Task 3: Register Keeper in SK_P.sln (3-block edit)** - `8dd36a9` (feat)

## Files Created/Modified
- `src/Messaging.Contracts/KeeperQueues.cs` - Static class with the single durable `FaultRecovery` queue-name const (mirrors `OrchestratorQueues`).
- `src/Keeper/Keeper.csproj` - Keeper console project: Exe, MassTransit + MassTransit.RabbitMQ, `BaseConsole.Core` + `Messaging.Contracts` ProjectReferences, appsettings Content copy, `NoWarn CS1591`. No scheduler/cron/BaseApi/BaseProcessor surface.
- `SK_P.sln` - Keeper project registered (3 blocks); GUID `{D4E5F6A7-8B9C-4D0E-A1F2-3456789ABCDE}` ×6.

## Decisions Made
- Const named `FaultRecovery` for its enduring Phase-35 role (D-08 discretion / RESEARCH OQ-2); the throwaway placeholder message type stays LOCAL to Keeper in Plan 02 — only the const lives in Contracts.
- Keeper GUID is genuinely fresh (verified absent in the file before insertion); console project-type GUID `{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}` reused (same family as Orchestrator/Messaging.Contracts/BaseConsole.Core).

## Deviations from Plan

**1. [Rule 2 - Missing Critical] Reworded Keeper.csproj comments to drop forbidden literal tokens**
- **Found during:** Task 2 (Keeper.csproj)
- **Issue:** The first draft cloned the Orchestrator csproj comment style, which mentions `Quartz`/`Cronos`/`BaseApi`/`BaseProcessor`/`Version=` in explanatory prose. The plan's csproj acceptance criteria require the file to NOT contain those literal strings, and the Plan-03 `KeeperDependencyFirewallTests` plus any source-grep firewall guard match literally. Leaving the tokens in comments would make a correctness/firewall acceptance grep falsely fail.
- **Fix:** Reworded the comments to convey the same intent without the forbidden literals (e.g. "omits the scheduler + cron-math packages", "no coupling to the API or processor projects", "no version attribute"). No structural/reference change — the actual ItemGroups already excluded all forbidden references.
- **Files modified:** src/Keeper/Keeper.csproj
- **Verification:** `grep -c` for each of Quartz/Cronos/BaseApi/BaseProcessor/`Version=`/`<TargetFramework`/`<Nullable`/TreatWarningsAsErrors == 0; required strings (`<OutputType>Exe</OutputType>`, RootNamespace/AssemblyName Keeper, CS1591, both ProjectReferences) present; `dotnet restore` succeeds.
- **Committed in:** `9838b04` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 missing critical — firewall/acceptance grep compliance)
**Impact on plan:** Comment-only rewording; zero behavioral/reference change. No scope creep.

## Issues Encountered
None — all three verification commands passed first try (contracts build 0/0, restore succeeds, sln list shows Keeper).

## User Setup Required
None — no external service configuration required.

## Next Phase Readiness
- Plan 02 (Keeper console body: Program.cs, appsettings.json, placeholder consumer + definition + local message type) builds directly against this csproj. After Plan 02 lands `Program.cs` + `appsettings.json`, `dotnet build src/Keeper -c Release` should be 0-warning.
- Plan 03 (compose tier + Keeper tests incl. `KeeperDependencyFirewallTests`) consumes the registered sln entry and the reference-firewall closure established here.
- No blockers. `dotnet build src/Keeper` intentionally fails until Plan 02 adds a compilable entrypoint (expected; restore is the gate for this plan).

## Threat Surface
No new surface beyond the plan's `<threat_model>`: T-34-01 (reference firewall) mitigated by the csproj closure + the forthcoming Plan-03 reflection guard; T-34-02 (queue-name const, accept) is a non-secret topology label. No network/auth/file/schema surface introduced.

## Self-Check: PASSED

- Files: `src/Messaging.Contracts/KeeperQueues.cs`, `src/Keeper/Keeper.csproj`, `SK_P.sln`, `.planning/phases/34-keeper-console-foundation/34-01-SUMMARY.md` — all FOUND.
- Commits: `2de461e`, `9838b04`, `8dd36a9` — all FOUND.

---
*Phase: 34-keeper-console-foundation*
*Completed: 2026-06-05*
