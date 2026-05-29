---
phase: 13
slug: orchestrationservice-split-l3-fetch-l1-build
status: planned
nyquist_compliant: true
wave_0_complete: false
created: 2026-05-29
---

# Phase 13 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (.NET 8) — `TestContext.Current.CancellationToken`, `[Fact]`, `IClassFixture<>` |
| **Config file** | existing test project (BaseApi.Tests) — convention-based |
| **Integration fixture** | `Phase8WebAppFactory : WebAppFactory, IAsyncLifetime` (real Postgres via PostgresFixture + Redis via RedisFixture) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Orchestration"` |
| **Full suite command** | `dotnet test` (solution root) |
| **Estimated runtime** | full suite ~18-30s warm (per prior phase SUMMARYs); Orchestration subset ~few seconds |

---

## Sampling Rate

- **After every task commit:** `dotnet build` (RMG drift guards + compile = build-half) + targeted test.
- **After every plan wave:** `dotnet test --filter "FullyQualifiedName~Orchestration"` (Phase 9 + Phase 13 facts together — proves no Start/Stop 204/400/404 regression).
- **Before `/gsd-verify-work`:** full `dotnet test` green ×3 consecutive (Phase 3 D-18 cadence) + `psql \l` SHA-256 BEFORE=AFTER (no writes this phase) + `redis-cli --scan` SHA-256 BEFORE=AFTER (writer is no-op).
- **Max feedback latency:** ~30s (full suite warm).

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 13-01-T1 | 13-01 | 1 | ORCH-SPLIT-02, L1-BUILD-01 | T-13-04 | InternalsVisibleTo limited to test assembly; snapshot Dispose contract scaffolded | build + grep | `dotnet build src/BaseApi.Service/BaseApi.Service.csproj` | snapshot+seams NEW | ⬜ pending |
| 13-01-T2 | 13-01 | 1 | ORCH-SPLIT-01 | — | mappers relocated, no IDE0052 suppressions | build + grep | `dotnet build src/BaseApi.Service/BaseApi.Service.csproj` | loader NEW (empty impl) | ⬜ pending |
| 13-01-T3 | 13-01 | 1 | ORCH-SPLIT-03, ORCH-SPLIT-04 | T-13-01, T-13-02 | parameterized id-set query; locked NotFoundException error shape | integration (existing facts) | `dotnet test --filter "FullyQualifiedName~Orchestration"` | Start/StopOrchestrationFacts (adapt-green) | ⬜ pending |
| 13-02-T1 | 13-02 | 2 | L1-BUILD-02, L1-BUILD-03 | T-13-06, T-13-08 | AsNoTracking parameterized batch reads; data-free log line | build + grep | `dotnet build src/BaseApi.Service/BaseApi.Service.csproj` | WorkflowGraphLoader.cs (filled) | ⬜ pending |
| 13-02-T2 | 13-02 | 2 | L1-BUILD-04 | T-13-05, T-13-07 | `visited` List guard guarantees BFS termination on cyclic graph | build + grep (behavior proven in 13-03) | `dotnet build src/BaseApi.Service/BaseApi.Service.csproj` | WorkflowGraphLoader.cs (BFS helper) | ⬜ pending |
| 13-03-T1 | 13-03 | 3 | L1-BUILD-03, L1-BUILD-04 | T-13-10 | cycle fact uses timeout guard (DoS-termination verification) | integration | `dotnet test --filter "FullyQualifiedName~WorkflowGraphLoaderFacts"` | WorkflowGraphLoaderFacts.cs NEW | ⬜ pending |
| 13-03-T2 | 13-03 | 3 | L1-BUILD-05 | T-13-09, T-13-11 | test-only doubles; forced-throw 500 body PII-free | integration (THE gate) | `dotnet test --filter "FullyQualifiedName~StartCleanupFacts"` | StartCleanupFacts.cs NEW | ⬜ pending |
| 13-03-T3 | 13-03 | 3 | all (regression) | — | read-only invariant; no package/schema change | full suite ×3 | `dotnet test` | existing + new | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [x] `[assembly: InternalsVisibleTo("BaseApi.Tests")]` confirmed ABSENT on `BaseApi.Service` (VERIFIED this session — no `InternalsVisibleTo` in src/BaseApi.Service or its csproj). **Planned as Plan 13-01 Task 1** (new `src/BaseApi.Service/Properties/AssemblyInfo.cs`). Needed for SC3 white-box loader-resolution + SC4 internal seam doubles.
- [x] Test files MISSING — created as Wave 3 (Plan 13-03): `WorkflowGraphLoaderFacts.cs` (SC3/SC5), `StartCleanupFacts.cs` (SC4). The `<automated>` for the SC3/SC4/SC5 truths in 13-01/13-02 point forward to these Wave 3 facts; the build/grep checks in Waves 1-2 provide per-task sampling continuity in the meantime.
- No framework install needed — xUnit v3 + `Phase8WebAppFactory` already present.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| (none) | — | — | All phase behaviors have automated verification. |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify (every task has a build or test command)
- [x] Wave 0 covers all MISSING references (InternalsVisibleTo → 13-01-T1; test files → Wave 3)
- [x] No watch-mode flags
- [x] Feedback latency target set (~30s)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** planned — ready for execution
