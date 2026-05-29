---
phase: 13
slug: orchestrationservice-split-l3-fetch-l1-build
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-29
---

# Phase 13 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (.NET 8) |
| **Config file** | existing test project (BaseApi.Tests) |
| **Quick run command** | `dotnet test --filter FullyQualifiedName~Orchestration` |
| **Full suite command** | `dotnet test` |
| **Estimated runtime** | ~TBD (planner confirms against existing suite) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet build` + targeted unit test
- **After every plan wave:** Run `dotnet test --filter FullyQualifiedName~Orchestration`
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** TBD seconds

---

## Per-Task Verification Map

> Populated during planning/execution. Source: RESEARCH.md `## Validation Architecture` (maps the 5 Success Criteria to test types).

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| TBD | — | — | — | — | — | — | — | — | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] Confirm `[assembly: InternalsVisibleTo("BaseApi.Tests")]` on `BaseApi.Service` (RESEARCH Assumption A1 — needed for white-box loader-resolution SC3 test and internal seam doubles)

*Populated during planning. If none: "Existing infrastructure covers all phase requirements."*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| TBD | — | — | — |

*If none: "All phase behaviors have automated verification."*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency target set
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
