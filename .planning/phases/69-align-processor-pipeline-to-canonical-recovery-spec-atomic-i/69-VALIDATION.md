---
phase: 69
slug: align-processor-pipeline-to-canonical-recovery-spec-atomic-i
status: approved
nyquist_compliant: true
wave_0_complete: true
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
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-method "*PipelineForward*"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` |
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
| 69-01 | 01 | 1 | Spec §4.3 atomic write | T-69-01 | Index+data write is all-or-nothing; partial state never observed | unit | `dotnet test -c Release -- --filter-method "*Completed_AllocationBeforeData*" "*IndexTtl_IsRandom*"` | ✅ `PipelineForwardFacts.Completed_AllocationBeforeData` + `IndexTtl_IsRandom_BetweenDataTtl_And_` | ✅ green |
| 69-01 | 01 | 1 | Spec §10 INFRA-01 no-drop | T-69-03 | Atomic-write exhaustion → single INJECT, never a silent drop | unit | `dotnet test -c Release -- --filter-method "*AtomicWriteFault_Inject*"` | ✅ `PipelineForwardFacts.AtomicWriteFault_Inject` (+ `PipelinePostFacts.WriteFault_Inject`) | ✅ green |
| 69-02 | 02 | 2 | Spec §4.3 gated cleanup | T-69-02 | Cleanup tail skipped when any item escalated → no processor/keeper index race | unit | `dotnet test -c Release -- --filter-method "*EscalatedItem_SkipsCleanup*" "*HappyTail_DeletesSource*"` | ✅ `PipelineForwardFacts.EscalatedItem_SkipsCleanup` (+ `HappyTail_DeletesSource` contrast) | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky. Post-execution audit (2026-06-16): the planned migration landed — `SlotWriteFault_Drop` was inverted into `AtomicWriteFault_Inject` (drop→single-INJECT), and `Completed_AllocationBeforeData` / `IndexTtl_IsRandom_*` were retargeted to the single `ScriptEvaluateAsync` ARGV shape. All three validation dimensions are COVERED by present, green facts (5/5 dimension facts pass; 21/21 across the touched Forward/Post/Recovery/Inject suites).*

---

## Wave 0 Requirements

- [x] Migrate existing facts `SlotWriteFault_Drop`, `Completed_AllocationBeforeData`, `IndexTtl_IsRandom_*` to assert the single atomic `ScriptEvaluateAsync` ARGV shape (covered by Plan 01 Task 1 + Task 3).
- [x] New fact: atomic-write exhaustion (index OR data) → exactly one `BuildInject`/`SendKeeper`, no drop (covered by Plan 01 Task 3 `AtomicWriteFault_Inject`).
- [x] New fact: any item escalated → forward `DeleteTerminalAsync` cleanup tail is NOT called (covered by Plan 02 Task 2 `EscalatedItem_SkipsCleanup`).
- [x] Reuse existing `DispatchTestKit.cs` NSubstitute `IDatabase` fault-injection harness — no new framework install (confirmed by PATTERNS.md analog map).

*Existing infrastructure (`BaseApi.Tests`, `DispatchTestKit.cs`, `FakeRedis.cs`) covers all phase requirements — no install needed.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| End-to-end no-drop under a real transient L2 outage | Spec §10 INFRA-01 | Requires the live close-gate / fault-injection harness (Phase 67/68), out of this phase's unit scope | Covered by the live-proof phases; this phase proves it at the unit level via fault-injected `IDatabase` |

*All in-scope phase behaviors have automated unit verification; live-outage proof is delegated to the existing live-proof phases.*

---

## Validation Audit 2026-06-16

| Metric | Count |
|--------|-------|
| Gaps found | 0 |
| Resolved | 0 |
| Escalated | 0 |

State A post-execution audit: cross-referenced all 3 validation dimensions against the implemented test facts in `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs` (+ `PipelinePostFacts.cs`). Every dimension is COVERED by a present, green fact — no MISSING/PARTIAL gaps, so no test generation was required. nyquist_compliant remains `true`. The one Manual-Only item (live-outage no-drop proof) stays delegated to the Phase 67/68 live-proof harness by design.

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < quick-run seconds
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved 2026-06-16
