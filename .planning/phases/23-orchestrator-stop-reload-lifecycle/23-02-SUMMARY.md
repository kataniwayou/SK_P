---
phase: 23-orchestrator-stop-reload-lifecycle
plan: 02
subsystem: orchestrator-scheduling
tags: [cpm, quartz, l2-keys, forwarders, wave-0]
requires:
  - Messaging.Contracts L2ProjectionKeys (ParentIndex/Step — Phase 21/22)
  - Orchestrator console + OrchestratorL2Keys.Root forwarder (Phase 19/21)
provides:
  - Quartz + Quartz.Extensions.Hosting 3.18.1 CPM pins
  - Orchestrator PackageReference to Quartz.Extensions.Hosting (Quartz types resolvable)
  - OrchestratorL2Keys.ParentIndex() forwarder (parent-index SET key reader)
  - OrchestratorL2Keys.Step() forwarder (per-step key reader)
affects:
  - Plan 03 (L1 store + scheduling — needs Quartz)
  - Plan 04 (hydration + consumers — needs ParentIndex/Step readers)
tech-stack:
  added:
    - "Quartz 3.18.1"
    - "Quartz.Extensions.Hosting 3.18.1"
  patterns:
    - "CPM: version pinned only in Directory.Packages.props; PackageReference has no Version="
    - "Thin key forwarders delegate to shared L2ProjectionKeys (HARDEN-03 single source of truth)"
key-files:
  created: []
  modified:
    - Directory.Packages.props
    - src/Orchestrator/Orchestrator.csproj
    - src/Orchestrator/Messaging/OrchestratorL2Keys.cs
decisions:
  - "Pin BOTH Quartz and Quartz.Extensions.Hosting for auditability even though the latter transitively brings the former (repo CPM convention)"
  - "Single PackageReference to Quartz.Extensions.Hosting suffices (transitively brings Quartz + Quartz.Extensions.DependencyInjection)"
metrics:
  duration: ~4min
  completed: 2026-05-31
---

# Phase 23 Plan 02: Quartz Pin + L2 Key Forwarders Summary

Landed the load-bearing Wave-0 dependency (Quartz 3.18.1, CPM-pinned + referenced by the Orchestrator) and the two reader key-forwarders (`ParentIndex()`, `Step()`) that all downstream scheduling (Plan 03) and hydration (Plan 04) code requires — forwarders delegate to the shared `L2ProjectionKeys`, never inlining the `skp:` format.

## What Was Built

**Task 1 — Quartz CPM pin + reference (commit 42c10ee):**
- `Directory.Packages.props`: added `PackageVersion Include="Quartz" Version="3.18.1"` and `PackageVersion Include="Quartz.Extensions.Hosting" Version="3.18.1"` (placed after the Cronos domain-validator pin). Both pinned for auditability; exact version (no floating range) per T-23-03 supply-chain mitigation.
- `src/Orchestrator/Orchestrator.csproj`: added `PackageReference Include="Quartz.Extensions.Hosting"` (no `Version=` — CPM convention) to the existing MassTransit ItemGroup. Transitively brings `Quartz` + `Quartz.Extensions.DependencyInjection`.
- Verified: `dotnet restore` + `dotnet build` exit 0 with 0 Warning(s) / 0 Error(s); Quartz types now resolvable for Plan 03.

**Task 2 — L2 key reader forwarders (commit a6a3cbd):**
- `src/Orchestrator/Messaging/OrchestratorL2Keys.cs`: added `public static string ParentIndex() => L2ProjectionKeys.ParentIndex();` and `public static string Step(Guid workflowId, Guid stepId) => L2ProjectionKeys.Step(workflowId, stepId);` next to the existing `Root` forwarder. Class stays `internal static`; existing `using Messaging.Contracts.Projections;` already covers `L2ProjectionKeys`.
- No inlined key format (no literal `"skp:"`, no `$"{`) — forwards to the single source of truth (HARDEN-03 / T-23-04 key-collision mitigation).
- Verified: `dotnet build` exit 0 with 0 Warning(s) / 0 Error(s).

## Acceptance Criteria

- ✓ `Directory.Packages.props` contains `PackageVersion Include="Quartz" Version="3.18.1"`
- ✓ `Directory.Packages.props` contains `PackageVersion Include="Quartz.Extensions.Hosting" Version="3.18.1"`
- ✓ `src/Orchestrator/Orchestrator.csproj` contains `PackageReference Include="Quartz.Extensions.Hosting"` with no `Version=`
- ✓ `OrchestratorL2Keys.cs` exposes `Root` + `ParentIndex` + `Step`, all forwarding to `L2ProjectionKeys`; no inline format string
- ✓ `dotnet build src/Orchestrator/Orchestrator.csproj` exits 0 with 0 Warning(s) / 0 Error(s)

## Deviations from Plan

None — plan executed exactly as written.

## Authentication Gates

None.

## Threat Surface

No new threat surface beyond the plan's `<threat_model>`. T-23-03 (Quartz supply chain) mitigated via exact CPM pin; T-23-04 (key collision) mitigated via forwarder delegation with no inline format.

## Self-Check: PASSED

- All 3 modified files present on disk.
- SUMMARY.md present.
- Both task commits (42c10ee, a6a3cbd) found in git history.
