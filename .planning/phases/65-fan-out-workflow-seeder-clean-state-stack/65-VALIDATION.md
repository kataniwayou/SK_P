---
phase: 65
slug: fan-out-workflow-seeder-clean-state-stack
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-14
---

# Phase 65 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (.NET 8) for the seeder fixture; PowerShell scripts for reset/bring-up |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (existing) |
| **Quick run command** | `dotnet test SK_P.sln -c Debug --filter "FullyQualifiedName~FanOutSeeder"` |
| **Full suite command** | `dotnet test SK_P.sln -c Debug` (hermetic) + `dotnet test --filter "Category=RealStack"` (live stack up) |
| **Estimated runtime** | ~30-60s for the seeder fixture against a live stack |

---

## Sampling Rate

- **After every task commit:** Run the quick run command (or `dotnet build SK_P.sln -c Release` for the 0-warning gate on non-test tasks)
- **After every plan wave:** Run the full hermetic suite
- **Before `/gsd-verify-work`:** Seeder fixture green against a live stack; reset + bring-up scripts exercised end-to-end
- **Max feedback latency:** 60 seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 65-01-* | 01 | 1 | WF-01, WF-02 | — / — | N/A (test infra, no auth surface) | integration (RealStack) | `dotnet test --filter "FullyQualifiedName~FanOutSeeder"` | ❌ W0 | ⬜ pending |
| 65-02-* | 02 | 2 | ENV-02 | — / — | N/A | script + integration | `pwsh scripts/phase-65-reset.ps1` then re-run seeder fixture | ❌ W0 | ⬜ pending |
| 65-03-* | 03 | 1 | ENV-01 | — / — | N/A | script | `pwsh scripts/phase-65-up.ps1` (asserts 10 healthy, 0 badconfig) | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky · ❌ W0 = artifact created by this phase (Wave 0 of its own plan)*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Orchestrator/FanOutSeeder*.cs` — the new self-verifying seeder fixture (extends existing `SeedProcessorAsync`/`SeedStepAsync`/`SeedWorkflowAsync` helpers + adds `SeedAssignmentAsync`, reverse-topo edge wiring, Npgsql `SELECT count(*)` acceptance assertions, run-twice idempotency `[Fact]`)
- [ ] `scripts/phase-65-reset.ps1` — FLUSHALL + heal-wait + psql DELETE + processor-set assertion
- [ ] `scripts/phase-65-up.ps1` — `docker compose up -d` + health-wait + zero-badconfig assertion

*Existing infrastructure (xUnit, RealStack trait, host-override env vars, close-script PowerShell conventions) covers the framework — only the three net-new artifacts are added.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| 10 service types converge healthy on a real Docker host | ENV-01 | Requires a live Docker daemon (not hermetic) | Run `pwsh scripts/phase-65-up.ps1`; confirm exit 0 + the asserted health summary |
| Two consecutive reset→seed cycles yield disjoint correlationId sets | ENV-02 | Full attribution proof spans Phases 66/68 observation; this phase proves the baseline (no leftover `skp:data:*`/`skp:msg:*` at seed time) | Run reset, then seed, then `redis-cli --scan skp:data:*` returns empty pre-seed |

*The WF-01/WF-02 row-count + idempotency acceptance is fully automated inside the seeder fixture.*

---

## Validation Sign-Off

- [ ] All tasks have an automated verify command or are Wave-0 artifact-creation tasks
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references (the 3 net-new artifacts)
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
