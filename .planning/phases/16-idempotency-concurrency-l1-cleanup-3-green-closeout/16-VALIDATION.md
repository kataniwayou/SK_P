---
phase: 16
slug: idempotency-concurrency-l1-cleanup-3-green-closeout
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-29
---

# Phase 16 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (.NET) |
| **Config file** | none — existing `tests/BaseApi.Tests` project covers all phase requirements |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Orchestration"` |
| **Full suite command** | `dotnet test` |
| **Estimated runtime** | ~TBD seconds (real Postgres + real Redis boot) |

---

## Sampling Rate

- **After every task commit:** Run quick run command (scoped to the touched fact class)
- **After every plan wave:** Run full suite command
- **Before `/gsd-verify-work`:** Full suite must be green (this phase's gate requires 3 consecutive GREEN)
- **Max feedback latency:** TBD seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| _populated during planning_ | | | | | | | | | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

*Populated during planning. Likely: "Existing infrastructure (Phase8WebAppFactory + RedisFixture) covers all phase requirements."*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| _populated during planning_ | | | |

*If none: "All phase behaviors have automated verification."*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < TBDs
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
