---
phase: 20
slug: correlation-propagation-proof-synthetic-harness-triple-sha-c
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-30
---

# Phase 20 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Validation Architecture detail lives in `20-RESEARCH.md` `## Validation Architecture` — this file is the executable sampling contract the planner/executor fill in.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (.NET 8) — `tests/BaseApi.Tests` |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "Category!=RealStack"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests` (real-stack tests need Docker stack up) |
| **Estimated runtime** | TBD by planner (in-memory fast; TEST-RMQ-02 real-stack slower) |

---

## Sampling Rate

- **After every task commit:** Run quick run command (in-memory harness tests)
- **After every plan wave:** Run full suite command
- **Before `/gsd-verify-work`:** Full suite green + close gate 3× GREEN
- **Max feedback latency:** TBD by planner

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| _filled by planner_ | | | | | | | | | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- _filled by planner_ — see `20-RESEARCH.md` Open Question #2 (two-bus fan-out idiom) flagged as a Wave 0 spike candidate.

*If none: "Existing infrastructure covers all phase requirements."*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| _filled by planner_ | | | |

*Phase 20 absorbs the Phase 19 human-UAT correlation item into automated TEST-RMQ-02 — aim for zero manual-only verifications.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency target set
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
