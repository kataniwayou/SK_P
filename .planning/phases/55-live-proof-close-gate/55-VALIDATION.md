---
phase: 55
slug: live-proof-close-gate
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-12
---

# Phase 55 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> **Build-before-proof split (D-08/D-09):** the hermetic build gate is the autonomously-verifiable deliverable; the live N×GREEN run is operator-gated.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (`xunit.v3`, CPM) on Microsoft.Testing.Platform |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (+ `xunit.runner.json` for capped parallelism) |
| **Quick run command** | `dotnet run --project tests/BaseApi.Tests -c Release -- --filter-not-trait Category=RealStack` (hermetic; RealStack EXCLUDED but must compile) |
| **Full suite command (the live gate)** | `pwsh -File scripts/phase-55-close.ps1` (runs the full suite ×3 against the rebuilt v5 stack) |
| **Phase-scoped live SC run** | `dotnet run --project tests/BaseApi.Tests -- --filter-trait "Phase=55"` |
| **Estimated runtime** | hermetic ~unchanged from current suite; live gate ~minutes ×3 GREEN |

> **MTP note (Landmine):** `dotnet test --filter` is IGNORED under Microsoft.Testing.Platform (MTP0001). Filtered runs MUST use `dotnet run --project ... -- --filter-trait`/`--filter-not-trait`. The close gate runs the unfiltered full suite live.

---

## Sampling Rate

- **After every task commit (autonomous, D-08):** Run quick command → 0 failed (new/adapted RealStack facts COMPILE and are EXCLUDED, not run) AND `dotnet build SK_P.sln -c Release` and `-c Debug` both 0-warning.
- **After every plan wave:** Full hermetic suite green + both build configs 0-warning + `scripts/phase-55-close.ps1` parses (`pwsh -NoProfile -Command "& { . ./scripts/phase-55-close.ps1 }"` parse-check).
- **Before `/gsd-verify-work`:** Hermetic suite green; both build configs 0-warning; close script syntactically valid.
- **Phase gate (operator, D-09):** `pwsh -File scripts/phase-55-close.ps1` against the rebuilt v5 stack → exit 0 at **N=3 consecutive GREEN**, identical `Passed` fact count (Smell-A guard, D-10), triple-SHA BEFORE==AFTER, `skp-dlq-1` depth==0, `skp:msg:*` count==0. Record in `55-HUMAN-UAT.md`, then tick TEST-01/02.
- **Max feedback latency:** hermetic ≈ suite runtime per commit; live gate is operator-cadenced (not autonomous).

---

## Per-Task Verification Map

> Plan/wave/task IDs are filled by the planner. This maps each phase requirement to its proving observation.

| Requirement | Behavior proven | Type | Live observation (what proves it) | Automated command | File Exists | Status |
|-------------|-----------------|------|-----------------------------------|-------------------|-------------|--------|
| TEST-01 | Forward pass: index-before-data + advance | RealStack E2E | Fresh `skp:msg:{messageId}` HASH (slot write) AND fresh `skp:data:*` key, ordered alloc-before-data; ES orchestrator advance seam | `--filter-trait "Phase=55"` → `SC1RoundTripE2ETests` | ❌ W0 (adapt SC1) | ⬜ pending |
| TEST-01 | A19 net-zero at end-of-message | RealStack E2E | BOTH `skp:data:{entryId}` AND `skp:msg:{messageId}` gone (two-key `DEL`) | same | ❌ W0 (adapt SC1) | ⬜ pending |
| TEST-01 | Keeper REINJECT data-present | RealStack E2E | Re-injected `EntryStepDispatch` lands on `queue:{ProcessorId:D}` (depth ≥ 1) | `--filter-trait "Phase=55"` → `SC2...` | ❌ W0 (rewrite SC2) | ⬜ pending |
| TEST-01 | Keeper REINJECT data-gone | RealStack E2E | Origin queue stays empty AND `skp-dlq-1` does NOT increment (silent drop; `keeper_reinject_dropped`) | same | ❌ W0 (rewrite SC2) | ⬜ pending |
| TEST-01 | Keeper INJECT | RealStack E2E | `L2[m.EntryId]=m.Data` written + `L2[m.DeleteEntryId]` deleted; `StepCompleted` emitted | same | ❌ W0 (rewrite SC2) | ⬜ pending |
| TEST-01 | Keeper DELETE (A19 both-key) | RealStack E2E | BOTH `skp:data:{entryId}` AND `skp:msg:{messageId}` gone after ONE `DEL` (v5-NEW vs v4 source-only) | same | ❌ W0 (rewrite SC2) | ⬜ pending |
| TEST-01 | Organic recovery pass (`if exist L2[messageId]`) | RealStack E2E | Pre-seeded slot array → re-fire → completed re-sent (fresh `NewId` exec) → slot retired to `Guid.Empty` → two-key DEL net-zero | same | ❌ W0 (NEW test, D-03) | ⬜ pending |
| TEST-01 | BIT-gate pause/resume across outage (A14) | RealStack E2E | ES `Global PauseAll`/`Global ResumeAll` seams; no new `skp:data:*` during paused window | `--filter-trait "Phase=55"` → `SC3...` (serial collection) | ⚠️ W0 (retag SC3 only) | ⬜ pending |
| TEST-02 | Net-zero triple-SHA | Close-gate script | `psql \l` / `redis --scan` / `rabbitmq list_queues name` SHA BEFORE==AFTER | `pwsh -File scripts/phase-55-close.ps1` (exit 0) | ❌ W0 (clone phase-49-close.ps1) | ⬜ pending |
| TEST-02 | Active index reclaim (A19) | Close-gate script | `skp:msg:*` count==0 (D-06c additive) + `skp-dlq-1` depth==0 | same | ❌ W0 (add count==0 block) | ⬜ pending |
| TEST-02 | 0-warning Release+Debug | Build gate | `dotnet build SK_P.sln -c Release` AND `-c Debug` exit 0 (TreatWarningsAsErrors) | (within the gate; also the autonomous D-08 deliverable) | ✅ existing build | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `scripts/phase-55-close.ps1` — clone of `phase-49-close.ps1` with D-06(a) composite removal + D-06(c) `skp:msg:*` count==0 (covers TEST-02)
- [ ] `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs` — retag `Phase=55`, add `skp:msg:{messageId}` assertion + A19 net-zero (covers TEST-01 forward)
- [ ] `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` — retag `Phase=55`, rewrite for 3-state + both-key DELETE + add organic recovery test (covers TEST-01 recovery/keeper)
- [ ] `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs` — retag `Phase=55` only (covers TEST-01 BIT gate)
- [ ] `.planning/phases/55-live-proof-close-gate/55-HUMAN-UAT.md` — operator runbook (clone `49-HUMAN-UAT.md` structure; covers the D-09 gate)
- [ ] Remove the dead v4 composite-sweep teardown block (`GAP-49-8`) from all three SC factories (Landmine 3)

*No new test framework install — the existing xUnit v3 / MTP infrastructure covers all phase requirements.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live N=3×GREEN triple-SHA close run | TEST-01, TEST-02 | Requires the rebuilt v5 docker stack (`docker compose up -d --build baseapi-service orchestrator processor-sample keeper`); v5 wire contract is a BREAKING change — a mixed-version deploy mis-deserializes and the embedded SourceHash must match the host build or the liveness gate false-passes/times out. Cannot run in CI/autonomous context. | Follow `55-HUMAN-UAT.md`: rebuild stack → `pwsh -File scripts/phase-55-close.ps1` → confirm exit 0 with 3 consecutive GREEN at identical Passed count, triple-SHA BEFORE==AFTER, `skp-dlq-1`==0, `skp:msg:*`==0; record SHAs + Passed count; tick TEST-01/02. |

> The autonomous deliverable (D-08) IS automated: 0-warning Release+Debug build + RealStack tests COMPILE + close script parses. Only the live GREEN run (D-09) is operator-gated.

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency bounded by hermetic suite runtime (live gate operator-cadenced)
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
