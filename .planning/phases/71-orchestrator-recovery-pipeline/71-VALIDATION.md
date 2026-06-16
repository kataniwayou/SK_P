---
phase: 71
slug: orchestrator-recovery-pipeline
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-16
---

# Phase 71 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 / Microsoft.Testing.Platform (MTP) |
| **Config file** | per-test-project `.csproj` (MTP entrypoint) |
| **Quick run command** | `dotnet test <project> -- --filter-method "*<Facts>*"` (NOTE: `--filter` is silently ignored under MTP — use `-- --filter-method`) |
| **Full suite command** | `dotnet test` (solution) |
| **Estimated runtime** | ~TBD (set during Wave 0) |

---

## Sampling Rate

- **After every task commit:** Run the targeted `-- --filter-method` set for the touched facts
- **After every plan wave:** Run the full suite for the touched test projects
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** TBD seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 71-XX-XX | XX | X | ORCV-XX | — | {expected behavior} | unit | `{command}` | ✅ / ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

*Filled by the planner / gsd-nyquist-auditor from the RESEARCH.md Validation Architecture section (ORCV-01..ORCV-07 → test map).*

---

## Wave 0 Requirements

- [ ] Confirm targeted `-- --filter-method` invocations resolve for the new `Orchestrator*` facts
- [ ] `RecoveryTestKit.cs` 5-arg `StringSetAsync` stub (D-10 / 70-REVIEW WR-01) present before consumer binding facts run

*If none: "Existing infrastructure covers all phase requirements."*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| {behavior} | ORCV-XX | {reason} | {steps} |

*If none: "All phase behaviors have automated verification."*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < TBD s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
