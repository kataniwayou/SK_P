---
phase: 48
slug: v3-x-teardown
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-09
---

# Phase 48 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> **Deletion-phase framing:** the proof is **absence + green-suite + 0-warning-build**, not new-behavior tests. "Did we remove it" is proven by (a) the build compiling with the reactive surface gone, (b) the negative-guard facts asserting absence, (c) the full hermetic suite staying green (no v4 regression).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (in-repo, `tests/BaseApi.Tests`) |
| **Config file** | none — convention-based |
| **Quick run command** | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=48"` |
| **Full suite command** | `dotnet test SK_P.sln` (hermetic; default Category, no RealStack trait) |
| **Estimated runtime** | full hermetic suite ~ tens of seconds (530 facts at Phase 47) |

---

## Sampling Rate

- **After every task commit:** `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=48"` (new guards) + `dotnet build SK_P.sln -c Debug` (must compile — the build-before-teardown invariant)
- **After every plan wave:** `dotnet test SK_P.sln` (full hermetic suite green)
- **Before `/gsd-verify-work` (phase gate, D-03):** hermetic suite GREEN ×3 consecutive + `dotnet build SK_P.sln -c Release` AND `-c Debug` at 0 warnings
- **Max feedback latency:** < 60 seconds (quick guard run)

---

## Per-Task Verification Map

> Task IDs assigned by the planner. Requirement/SC → test mapping is fixed by research §6 + Validation Architecture; tasks attach to these.

| Req / SC | Behavior (absence) | Threat Ref | Test Type | Automated Command | File Exists | Status |
|----------|--------------------|------------|-----------|-------------------|-------------|--------|
| SC-1 / RETIRE-01 | No `MessageIdentity`/dedup member on the execution path | — | reflection guard | `... --filter-method "*No_dedup_machinery_on_execution_path*"` | ✅ Phase-47 green (cite-existing) | ⬜ pending |
| SC-2 / RETIRE-02 | `ExecutionData` builder is GUID-`entryId`-only; no `*Manifest*` type | — | reflection/source guard | `... --filter-trait "Phase=48"` | ❌ W0 (new SC-2 fact) | ⬜ pending |
| SC-3 / RETIRE-03 | No `Fault<EntryStepDispatch>`/`Fault<StepCompleted>` consumer on the Keeper assembly | T-48-01 | reflection guard | `... --filter-trait "Phase=48"` | ❌ W0 (new FACT 1) | ⬜ pending |
| SC-3 / RETIRE-03 | No `keeper-fault-recovery`/`keeper-dlq` literal reachable in `src/Keeper/` | T-48-01 | source-scan guard | `... --filter-trait "Phase=48"` | ❌ W0 (new FACT 2) | ⬜ pending |
| SC-3 / RETIRE-03 | `KeeperQueues` has no `FaultRecovery`/`DeadLetter` field; has `Recovery` | — | reflection guard | `... --filter-trait "Phase=48"` | ❌ W0 (new FACT 3) | ⬜ pending |
| SC-1 (widen) | Phase-47 `keeper-dlq` scan passes UNCONDITIONALLY (`KeeperRecoveryHandler.cs` exclusion removed) | — | source-scan guard | `... --filter-method "*No_v4_give_up_path_references_keeper_dlq*"` | ✅ Phase-47 (edit to drop exclusion) | ⬜ pending |
| SC-4 | Full hermetic suite GREEN ×3 + Release AND Debug 0-warning build | — | suite + build | `dotnet test SK_P.sln` ×3; `dotnet build SK_P.sln -c Release` + `-c Debug` | n/a (gate) | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs` (name at planner discretion) — the Phase-48 `[Trait("Phase","48")]` negative-guard fact class: FACT 1 (no `Fault<T>` consumer on the Keeper assembly), FACT 2 (no `keeper-fault-recovery`/`keeper-dlq` source literal in `src/Keeper/`), FACT 3 (`KeeperQueues` const absence/presence), and the SC-2 `ExecutionData`-Guid-only assertion. Covers SC-2 + SC-3. Mirrors `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs`.
- [ ] Edit `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` — remove the `KeeperRecoveryHandler.cs` exclusion `.Where(...)` line (research §6); re-confirm the scan stays green post-teardown.
- [ ] No new framework install — xUnit + BCL reflection already in `BaseApi.Tests`.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| — | — | — | — |

*All phase behaviors have automated verification.* (Live real-stack proof is explicitly deferred to Phase 49 per D-03 — not a Phase-48 manual item.)

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references (the new guard fact class)
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
