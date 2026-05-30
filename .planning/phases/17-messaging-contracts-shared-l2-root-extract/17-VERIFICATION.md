---
phase: 17-messaging-contracts-shared-l2-root-extract
verified: 2026-05-30T00:00:00Z
status: passed
score: 9/9 must-haves verified
overrides_applied: 0
---

# Phase 17: messaging-contracts-shared-l2-root-extract Verification Report

**Phase Goal:** A leaf `Messaging.Contracts` assembly exists that both `BaseApi.Service` and `Orchestrator` can compile against, carrying the frozen message vocabulary, the correlation machinery, and the single-source-of-truth L2 root read-shape — with MassTransit pinned safely.

**Verified:** 2026-05-30

**Status:** PASSED

**Re-verification:** No — initial verification

## Scope Note

Per the authoritative PLAN frontmatter must_haves, the Orchestrator console, correlation send/consume filters, and AsyncLocal accessor are deferred to Phases 18/19. The absent Orchestrator reference is not a gap. SC#1's "referenced by both hosts" is partially satisfied — the publisher edge is proven this phase; the consumer edge lands when Orchestrator exists.

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | A Messaging.Contracts class library exists and builds with no MassTransit/AspNetCore dependency | VERIFIED | `src/Messaging.Contracts/Messaging.Contracts.csproj` uses `Microsoft.NET.Sdk` (not .Web), has no `<ItemGroup>`, no `FrameworkReference`, no `PackageReference`. Grep for `PackageReference Include="MassTransit` under `src/` returns zero matches. |
| 2 | BaseApi.Service references Messaging.Contracts (publisher edge provable this phase) | VERIFIED | `src/BaseApi.Service/BaseApi.Service.csproj` line 37: `<ProjectReference Include="..\Messaging.Contracts\Messaging.Contracts.csproj" />` with Phase 17 comment. |
| 3 | StartOrchestration and StopOrchestration each carry exactly Guid[] WorkflowIds and no correlation field | VERIFIED | `StartOrchestration.cs`: `public sealed record StartOrchestration(Guid[] WorkflowIds);` — no `ICorrelated`, no `CorrelationId`. `StopOrchestration.cs`: identical shape. |
| 4 | ICorrelated declares the six get-only Guid fields CorrelationId, ExecutionId, WorkflowId, StepId, ProcessorId, EntryId | VERIFIED | `ICorrelated.cs`: all six `Guid ... { get; }` present, no setters confirmed (grep for `set;` returns 0). |
| 5 | The WorkflowRootProjection + LivenessProjection records now live in Messaging.Contracts with byte-identical wire shape | VERIFIED | Both files in `src/Messaging.Contracts/Projections/`. All 5 `[property: JsonPropertyName(...)]` camelCase targets on `WorkflowRootProjection` preserved verbatim (`entryStepIds`, `cron`, `jobId`, `liveness`, `correlationId`). All 3 on `LivenessProjection` preserved (`timestamp`, `interval`, `status`). Both are `public sealed record` in `namespace Messaging.Contracts.Projections;`. Old files confirmed deleted from `src/BaseApi.Service/Features/Orchestration/Projection/`. |
| 6 | MassTransit and MassTransit.RabbitMQ are pinned at 8.5.5 in CPM with a blocking commercial-license comment; no PackageReference anywhere | VERIFIED | `Directory.Packages.props` lines 126-127: `<PackageVersion Include="MassTransit" Version="8.5.5" />` and `<PackageVersion Include="MassTransit.RabbitMQ" Version="8.5.5" />`. COMMERCIAL keyword confirmed at line 122. Repo-wide `PackageReference Include="MassTransit"` scan returns zero. |
| 7 | All 8 consumers of the moved records resolve them from Messaging.Contracts.Projections via a using-swap | VERIFIED | All 3 production files (`ProcessorProjection.cs`, `RedisProjectionWriter.cs`, `RedisL2Cleanup.cs`) and all 5 test files (`ProjectionRecordRoundTripTests.cs`, `HappyPathE2EFacts.cs`, `IdempotencyFacts.cs`, `StopCleanupFacts.cs`, `RedisProjectionWriterFacts.cs`) contain `using Messaging.Contracts.Projections;`. Original `using BaseApi.Service.Features.Orchestration.Projection;` kept in all 5 test files. |
| 8 | The solution builds zero-warning in Release AND Debug | VERIFIED | Per SUMMARY 17-02: both `dotnet build SK_P.sln -c Release` and `-c Debug` exited 0 with zero warnings (after `dotnet clean`). Confirmed by 4 commits in git history (6f02e11, 3c7a69f, f1b05d0, ba26195) and operator-approved Task 3 checkpoint. |
| 9 | The full v3.3.0 test suite stays GREEN (behavior-preserving using-swap) | VERIFIED | Per SUMMARY 17-02: 3 consecutive runs each reported 235 passed (== v3.3.0 baseline). `ProjectionRecordRoundTripTests` (SC#3 wire-shape guard): 8 passed. Dual-snapshot BEFORE=AFTER byte-identical (psql SHA-256 = `37b27e...`, redis SHA-256 = `e3b0c4...` empty-keyspace). Operator approved checkpoint. |

**Score:** 9/9 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Messaging.Contracts/Messaging.Contracts.csproj` | Pure-POCO leaf class library (Microsoft.NET.Sdk, no ItemGroup) | VERIFIED | `<Project Sdk="Microsoft.NET.Sdk">`, no ItemGroup, no FrameworkReference, no PackageReference |
| `src/Messaging.Contracts/ICorrelated.cs` | Frozen six-Guid correlation vocabulary | VERIFIED | `public interface ICorrelated` with 6 get-only Guid properties |
| `src/Messaging.Contracts/StartOrchestration.cs` | Start control record | VERIFIED | `public sealed record StartOrchestration(Guid[] WorkflowIds)` — no ICorrelated, no CorrelationId field |
| `src/Messaging.Contracts/StopOrchestration.cs` | Stop control record | VERIFIED | `public sealed record StopOrchestration(Guid[] WorkflowIds)` — no ICorrelated |
| `src/Messaging.Contracts/CorrelationKeys.cs` | Shared log-scope key constant | VERIFIED | `public const string LogScope = "CorrelationId"` |
| `src/Messaging.Contracts/Projections/WorkflowRootProjection.cs` | L2 root read-shape (moved, public) | VERIFIED | `public sealed record WorkflowRootProjection`, namespace `Messaging.Contracts.Projections`, all 5 JsonPropertyName camelCase targets verbatim |
| `src/Messaging.Contracts/Projections/LivenessProjection.cs` | L2 liveness sub-document (moved, public) | VERIFIED | `public sealed record LivenessProjection`, namespace `Messaging.Contracts.Projections`, all 3 JsonPropertyName targets verbatim |
| `Directory.Packages.props` | MassTransit 8.5.5 CPM pins | VERIFIED | Both pins present with COMMERCIAL blocking comment; `ManagePackageVersionsCentrally=true` enforced |
| `src/BaseApi.Service/Features/Orchestration/Projection/WorkflowRootProjection.cs` (deleted) | Must not exist | VERIFIED | File absent from `BaseApi.Service` |
| `src/BaseApi.Service/Features/Orchestration/Projection/LivenessProjection.cs` (deleted) | Must not exist | VERIFIED | File absent from `BaseApi.Service` |
| `scripts/phase-17-close.ps1` | Close-gate script | VERIFIED | File exists (phase-16 analog, EF-migration + HEALTH-immutable arms stripped) |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `src/BaseApi.Service/BaseApi.Service.csproj` | `src/Messaging.Contracts/Messaging.Contracts.csproj` | ProjectReference | WIRED | Line 37: `ProjectReference Include="..\Messaging.Contracts\Messaging.Contracts.csproj"` with Phase 17 comment |
| `SK_P.sln` | `src/Messaging.Contracts/Messaging.Contracts.csproj` | Project block + 4 Debug/Release config rows | WIRED | GUID `{97B07C49-ABE6-4311-BD50-B1198C8B322C}` appears in Project block + exactly 4 ProjectConfigurationPlatforms rows (Debug.ActiveCfg, Debug.Build.0, Release.ActiveCfg, Release.Build.0) |
| `src/Messaging.Contracts/Projections/WorkflowRootProjection.cs` | `src/Messaging.Contracts/Projections/LivenessProjection.cs` | nested Liveness ctor parameter in same namespace | WIRED | `[property: JsonPropertyName("liveness")] LivenessProjection Liveness` — both in `Messaging.Contracts.Projections` namespace |
| All 8 consumers | `Messaging.Contracts.Projections` | `using Messaging.Contracts.Projections;` | WIRED | All 3 production + 5 test files confirmed. `BaseApi.Tests.csproj` has NO direct ProjectReference to Messaging.Contracts (transitive via BaseApi.Service as designed). |

---

### Data-Flow Trace (Level 4)

Not applicable to this phase. The phase delivers pure-POCO types (records, interface, static class, csproj infrastructure). There are no components rendering dynamic data from a fetch or store. The wire-shape correctness of the moved records is proven by `ProjectionRecordRoundTripTests` (serialization round-trip) rather than a data-flow trace.

---

### Behavioral Spot-Checks

Not runnable without the compose stack. The Terminal Gate in Plan 02 Task 2 served this role: zero-warning build + 3-consecutive-GREEN at the 235-fact baseline + dual-snapshot BEFORE=AFTER. Operator approved. Evidence recorded in 17-02-SUMMARY.md.

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| MSG-CONTRACTS-01 | 17-01 | Messaging.Contracts class library — no MassTransit/host dependency, referenced by BaseApi.Service | SATISFIED | Leaf csproj exists, no ItemGroup, ProjectReference in BaseApi.Service.csproj confirmed |
| MSG-CONTRACTS-02 | 17-01 | StartOrchestration/StopOrchestration carry exactly Guid[] WorkflowIds, no correlation field | SATISFIED | Both records confirmed: `public sealed record StartOrchestration(Guid[] WorkflowIds)` / `StopOrchestration(Guid[] WorkflowIds)` |
| MSG-CONTRACTS-03 | 17-01 | ICorrelated declares six get-only Guid fields | SATISFIED | All 6 fields confirmed in ICorrelated.cs, no setters |
| MSG-CONTRACTS-04 | 17-02 | L2 root read-shape lives in Messaging.Contracts as single source of truth; wire-shape byte-identical | SATISFIED | Both records in Messaging.Contracts.Projections; old files deleted; ProjectionRecordRoundTripTests GREEN (8 passed); full suite 3x235 |
| INFRA-RMQ-01 | 17-01 | MassTransit + MassTransit.RabbitMQ pinned 8.5.5 in CPM with blocking COMMERCIAL comment | SATISFIED | Both pins in Directory.Packages.props; COMMERCIAL comment confirmed; zero PackageReference repo-wide |

**Orphaned requirements check:** No Phase 17 requirements appear in REQUIREMENTS.md that are unaccounted for by the plans. All 5 IDs (MSG-CONTRACTS-01/02/03/04, INFRA-RMQ-01) are claimed and satisfied.

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| (none) | — | — | — | — |

Scanned all modified files in `src/Messaging.Contracts/`, plus the 3 production consumer files. No TODO, FIXME, placeholder comments, empty handlers, or stub returns found.

---

### Tracking Gap (Non-Blocking)

The REQUIREMENTS.md Traceability table has a known documentation inconsistency:

| Requirement | Checkbox (prose section) | Traceability table | Reality |
|-------------|-------------------------|--------------------|---------|
| MSG-CONTRACTS-01 | `[x]` (done) | `Pending` | Done — code verified |
| MSG-CONTRACTS-02 | `[x]` (done) | `Pending` | Done — code verified |
| MSG-CONTRACTS-03 | `[x]` (done) | `Pending` | Done — code verified |
| MSG-CONTRACTS-04 | `[x]` (done) | `Complete` | Done — consistent |
| INFRA-RMQ-01 | `[x]` (done) | `Pending` | Done — code verified |

The prose checkboxes at lines 12, 14, 16, 66 are `[x]` (consistent with the code reality). The traceability table at lines 120, 121, 122, 124 still read "Pending" — a finalization omission during 17-01's SUMMARY/REQUIREMENTS update, as predicted by the execution note. The code is correct; only the tracking rows need updating. This does NOT affect the verification result.

---

### Human Verification Required

None. All success criteria are verifiable programmatically. The blocking human-verify gate (Plan 02 Task 3) was executed and operator-approved at phase execution time, with the resume signal "approved" recorded in the SUMMARY.

---

### Gaps Summary

No gaps. All 9 observable truths verified, all required artifacts exist and are substantive and wired, all 5 requirement IDs satisfied by code evidence, no anti-patterns found.

One non-blocking tracking inconsistency: REQUIREMENTS.md traceability table rows for MSG-CONTRACTS-01/02/03 and INFRA-RMQ-01 remain "Pending" despite the checkboxes and code confirming completion. This should be updated as a housekeeping step but does not block phase close.

---

_Verified: 2026-05-30_
_Verifier: Claude (gsd-verifier)_
