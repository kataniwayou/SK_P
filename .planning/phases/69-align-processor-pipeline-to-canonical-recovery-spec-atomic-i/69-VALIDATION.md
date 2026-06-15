---
phase: 69
slug: align-processor-pipeline-to-canonical-recovery-spec-atomic-i
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-16
---

# Phase 69 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit.v3 / Microsoft.Testing.Platform (MTP) |
| **Config file** | none — existing `BaseApi.Tests` project |
| **Quick run command** | `dotnet test --no-build -- --filter-method "*PipelineForward*"` |
| **Full suite command** | `dotnet test` |
| **Estimated runtime** | ~quick: seconds · full: minutes |

> NOTE (project memory): `dotnet test --filter` is silently ignored under xUnit.v3/MTP — it runs the whole suite. Targeted runs MUST use `-- --filter-method`. The planner must encode this in acceptance criteria.

---

## Sampling Rate

- **After every task commit:** Run the quick command for the touched fact class
- **After every plan wave:** Run the full suite command
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** quick run latency (seconds)

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 69-01-* | 01 | 1 | Spec §4.3 atomic write | T-69-01 | Index+data write is all-or-nothing; partial state never observed | unit | `dotnet test --no-build -- --filter-method "*PipelineForward*"` | ❌ W0 (assertions inverted to script ARGV shape) | ⬜ pending |
| 69-01-* | 01 | 1 | Spec §10 INFRA-01 no-drop | T-69-01 | Atomic-write exhaustion → single INJECT, never a silent drop | unit | `dotnet test --no-build -- --filter-method "*PipelineForward*"` | ❌ W0 (new fact) | ⬜ pending |
| 69-02-* | 02 | 2 | Spec §4.3 gated cleanup | T-69-02 | Cleanup tail skipped when any item escalated → no processor/keeper index race | unit | `dotnet test --no-build -- --filter-method "*PipelineForward*"` | ❌ W0 (new fact) | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky. Exact Task IDs are assigned by the planner; rows above map the validation dimensions to the two new test facts plus the migration of the three existing facts (`SlotWriteFault_Drop`, `Completed_AllocationBeforeData`, `IndexTtl_IsRandom_*`) to the single `ScriptEvaluateAsync` shape.*

---

## Wave 0 Requirements

- [ ] Migrate existing facts `SlotWriteFault_Drop`, `Completed_AllocationBeforeData`, `IndexTtl_IsRandom_*` to assert the single atomic `ScriptEvaluateAsync` ARGV shape (they currently encode the 3-separate-ops shape and will go red on the change).
- [ ] New fact: atomic-write exhaustion (index OR data) → exactly one `BuildInject`/`SendKeeper`, no drop.
- [ ] New fact: any item escalated → forward `DeleteTerminalAsync` cleanup tail is NOT called.
- [ ] Reuse existing `DispatchTestKit.cs` NSubstitute `IDatabase` fault-injection harness — no new framework install.

*Existing infrastructure (`BaseApi.Tests`, `DispatchTestKit.cs`, `FakeRedis.cs`) covers all phase requirements — no install needed.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| End-to-end no-drop under a real transient L2 outage | Spec §10 INFRA-01 | Requires the live close-gate / fault-injection harness (Phase 67/68), out of this phase's unit scope | Covered by the live-proof phases; this phase proves it at the unit level via fault-injected `IDatabase` |

*All in-scope phase behaviors have automated unit verification; live-outage proof is delegated to the existing live-proof phases.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < quick-run seconds
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
