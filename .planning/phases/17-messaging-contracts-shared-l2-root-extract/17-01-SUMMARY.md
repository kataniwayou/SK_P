---
phase: 17-messaging-contracts-shared-l2-root-extract
plan: 01
subsystem: messaging-contracts
tags: [contracts, cpm, masstransit, l2-projection, correlation]
requires: []
provides:
  - "Messaging.Contracts leaf class library (pure-POCO, no MassTransit/AspNetCore dependency)"
  - "ICorrelated six-Guid correlation vocabulary"
  - "StartOrchestration / StopOrchestration control records"
  - "CorrelationKeys.LogScope cross-service log-join constant"
  - "WorkflowRootProjection + LivenessProjection moved into Messaging.Contracts.Projections (public, byte-identical wire shape)"
  - "MassTransit + MassTransit.RabbitMQ 8.5.5 CPM pins (no PackageReference)"
  - "BaseApi.Service -> Messaging.Contracts ProjectReference"
affects:
  - "BaseApi.Service (records removed; will not build solution-wide until Plan 02 consumer swaps)"
tech-stack:
  added:
    - "MassTransit 8.5.5 (CPM pin only — no PackageReference this phase)"
    - "MassTransit.RabbitMQ 8.5.5 (CPM pin only)"
  patterns:
    - "Pure-POCO leaf library: Microsoft.NET.Sdk, no ItemGroup, no FrameworkReference"
    - "CPM pin with blocking license comment (Apache-2.0 8.x boundary vs commercial 9.x)"
    - "Verbatim record move = namespace + visibility only; [property: JsonPropertyName] targets preserved"
key-files:
  created:
    - src/Messaging.Contracts/Messaging.Contracts.csproj
    - src/Messaging.Contracts/ICorrelated.cs
    - src/Messaging.Contracts/StartOrchestration.cs
    - src/Messaging.Contracts/StopOrchestration.cs
    - src/Messaging.Contracts/CorrelationKeys.cs
    - src/Messaging.Contracts/Projections/WorkflowRootProjection.cs
    - src/Messaging.Contracts/Projections/LivenessProjection.cs
  modified:
    - Directory.Packages.props
    - SK_P.sln
    - src/BaseApi.Service/BaseApi.Service.csproj
  deleted:
    - src/BaseApi.Service/Features/Orchestration/Projection/WorkflowRootProjection.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/LivenessProjection.cs
decisions:
  - "Flat root namespace Messaging.Contracts for net-new types; Messaging.Contracts.Projections for the two moved (co-located so the nested Liveness ctor param resolves)"
  - "MassTransit pinned in CPM only (D-03/D-12) — no PackageReference until Phase 18+ publisher/consumer wiring"
  - "CorrelationIdMiddleware.cs:52 literal NOT repointed (RESEARCH Open Q1) — would force a BaseApi.Core -> Messaging.Contracts edge for zero behavior change; deferred to Phase 18"
metrics:
  duration: ~2min
  completed: 2026-05-29
---

# Phase 17 Plan 01: Messaging.Contracts + Shared L2 Root Extract Summary

Created the pure-POCO `Messaging.Contracts` leaf library carrying the frozen messaging vocabulary (`ICorrelated`, `StartOrchestration`, `StopOrchestration`, `CorrelationKeys.LogScope`), moved the two L2 read-shape records (`WorkflowRootProjection` + `LivenessProjection`) into it with byte-identical wire shape, added the MassTransit 8.5.5 CPM pins behind a blocking commercial-license comment, and wired the project into the solution + a `BaseApi.Service` ProjectReference.

## What Was Built

**Task 1 (commit 6f02e11):** The leaf project scaffold.
- `Messaging.Contracts.csproj` — `Microsoft.NET.Sdk` (not .Web), no `<ItemGroup>`, no `Microsoft.AspNetCore.App`, no MassTransit reference. Common build props inherit from `Directory.Build.props`.
- `Directory.Packages.props` — `MassTransit` + `MassTransit.RabbitMQ` pinned at `8.5.5` with a `COMMERCIAL` blocking comment marking the Apache-2.0 8.x boundary.
- `SK_P.sln` — project added via `dotnet sln add` (fresh GUID `{97B07C49-...}` + 4 Debug/Release ProjectConfigurationPlatforms rows).
- `BaseApi.Service.csproj` — `ProjectReference` to Messaging.Contracts alongside the existing BaseApi.Core reference.

**Task 2 (commit 3c7a69f):** The contract types.
- `ICorrelated` — six get-only Guids: `CorrelationId`, `ExecutionId`, `WorkflowId`, `StepId`, `ProcessorId`, `EntryId`.
- `StartOrchestration(Guid[] WorkflowIds)` / `StopOrchestration(Guid[] WorkflowIds)` — control records, no correlation field, do not implement `ICorrelated`.
- `CorrelationKeys.LogScope = "CorrelationId"` — hoisted from the `CorrelationIdMiddleware.cs:52` literal (casing load-bearing for the Elasticsearch log join).
- `WorkflowRootProjection` + `LivenessProjection` moved to `Messaging.Contracts.Projections` — `public` (was `internal`), namespace changed, every `[property: JsonPropertyName(...)]` camelCase target preserved verbatim. Both originals removed from `BaseApi.Service` (git tracked the moves as renames at 85–87% similarity).

## Verification Results

- `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Debug` → exit 0, 0 warnings.
- `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Release` → exit 0, 0 warnings.
- `grep -rn 'PackageReference Include="MassTransit' src/` → no matches (zero MassTransit PackageReference repo-wide; D-03 held).
- `SK_P.sln` → new GUID appears in exactly 4 ProjectConfigurationPlatforms rows.
- `dotnet list src/BaseApi.Service/BaseApi.Service.csproj reference` → includes `Messaging.Contracts`.
- Both moved records confirmed absent from `src/BaseApi.Service/Features/Orchestration/Projection/`.

Note (expected, by design): the full solution does NOT build yet — the 8 consumers of `WorkflowRootProjection`/`LivenessProjection` inside `BaseApi.Service` still reference the old namespace and will emit CS0246 until Plan 02 performs the using-swaps. That is Plan 02's safety net, not a defect in this plan. The per-task verify here scopes the build to the leaf project only, as the plan specifies.

## Deviations from Plan

None — plan executed exactly as written.

## Authentication Gates

None.

## Threat Surface

No new surface beyond the plan's `<threat_model>`. T-17-01 (wire-shape integrity) honored via byte-identical verbatim move; T-17-02 (single `"CorrelationId"` const) honored via the hoisted literal with no duplicate; T-17-03 (MassTransit supply chain) honored via the 8.5.5 CPM pin + COMMERCIAL comment + the asserted zero-PackageReference grep. No threat flags.

## Requirements Satisfied

- MSG-CONTRACTS-01: leaf library exists, referenced by BaseApi.Service, no MassTransit/host dependency.
- MSG-CONTRACTS-02: StartOrchestration/StopOrchestration = exactly `Guid[] WorkflowIds`, no correlation field.
- MSG-CONTRACTS-03: ICorrelated = six get-only Guids.
- INFRA-RMQ-01: MassTransit + MassTransit.RabbitMQ pinned 8.5.5 with blocking comment; no PackageReference.
- (toward MSG-CONTRACTS-04): both records now live in Messaging.Contracts with byte-identical wire shape — the using-swap + GREEN proof completes in Plan 02 (partial).

## Commits

- 6f02e11 — feat(17-01): scaffold Messaging.Contracts leaf project + MassTransit CPM pins
- 3c7a69f — feat(17-01): add frozen contract vocabulary + move L2 root read-shape

## Self-Check: PASSED

All 7 source artifacts + the SUMMARY exist on disk; both commits (6f02e11, 3c7a69f) present in git history.
