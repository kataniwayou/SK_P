---
phase: 51
slug: processor-forward-recovery-pipeline
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-11
---

# Phase 51 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit + NSubstitute (.NET 8, hermetic plain-object facts) |
| **Config file** | existing test project (Phase-44 fixture: `DispatchTestKit` / `CapturingSendProvider`) |
| **Quick run command** | `dotnet test --filter "FullyQualifiedName~ProcessorPipeline" --nologo` |
| **Full suite command** | `dotnet build -c Release -warnaserror; dotnet test --nologo` |
| **Estimated runtime** | ~60 seconds |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test --filter "FullyQualifiedName~ProcessorPipeline" --nologo`
- **After every plan wave:** Run `dotnet build -c Release -warnaserror; dotnet test --nologo`
- **Before `/gsd-verify-work`:** Full suite must be green AND build 0-warning (Release + Debug)
- **Max feedback latency:** 60 seconds

---

## Per-Task Verification Map

> Filled by the planner from the final task breakdown. Source-of-truth requirement→test map is in 51-RESEARCH.md `## Validation Architecture`.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| {N}-01-01 | 01 | 1 | REQ-{XX} | — | N/A | unit | `{command}` | ✅ / ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `PipelineForwardFacts` — hermetic facts for the FORWARD pass (SLOT-01..03, INFRA-01/02, FWD-01..03)
- [ ] `PipelineRecoveryFacts` — hermetic facts for the RECOVERY pass (RECOV-01..03, REINJECT⊻source-delete)
- [ ] `DispatchTestKit` HASH-fake extensions — `HGETALL` / `HSET slot=guid.empty` / `KeyExpire` fakes for slot-array assertions

*Existing Phase-44 plain-object fixture (`DispatchTestKit`, `CapturingSendProvider`) covers the pipeline construction seam; Wave 0 extends it for the slot-array HASH surface.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live forward + recovery round-trip proof | TEST-01/02 | Requires running Redis + bus (deferred) | Out of scope — Phase 54 (live proof + close gate) |

*Hermetic facts cover all Phase-51 forward/recovery routing branches; live proof is intentionally scoped OUT to Phase 54.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
