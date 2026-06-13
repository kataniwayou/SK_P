---
phase: 61
slug: 1-healthy-orchestration-start-gate-self-watchdog-probe
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-13
---

# Phase 61 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (.NET 8) + NSubstitute + FluentAssertions |
| **Config file** | none — existing `tests/BaseApi.Tests` + processor/console test projects |
| **Quick run command** | `dotnet test SK_P.sln --filter "Trait=Phase&Phase=61" -c Debug` |
| **Full suite command** | `dotnet test SK_P.sln -c Debug` (RealStack E2E require live Docker stack) |
| **Estimated runtime** | ~Phase-61 trait facts seconds; full hermetic suite ~minutes |

---

## Sampling Rate

- **After every task commit:** Run the Phase=61 trait filter
- **After every plan wave:** Run the full hermetic suite (excluding live-broker RealStack E2E)
- **Before `/gsd-verify-work`:** Full hermetic suite green + `dotnet build SK_P.sln -c Release` 0 warnings
- **Max feedback latency:** ~60 seconds for the trait filter

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| TBD | TBD | TBD | GATE-01/02/03 | — | no info-disclosure in 422 detail | unit + integration | `dotnet test --filter "Phase=61"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | PROBE-01/02 | — | probe reports stale/null L1 as Unhealthy | unit | `dotnet test --filter "Phase=61"` | ❌ W0 | ⬜ pending |

*Planner fills the concrete task IDs/plans/waves. Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] New Phase=61-trait hermetic test classes for the ≥1-healthy gate (NSubstitute `IDatabase` unit level) + the self-watchdog probe (fabricated stale/fresh/null `IProcessorLivenessState`).
- [ ] Re-point existing `ProcessorLivenessFacts` (real-Redis integration) onto the `SMEMBERS`→`GET`-each per-instance scheme.
- [ ] Add `PerInstance`/`InstanceIndex` golden-string pins to `RedisProjectionKeysTests` (A1 — not present today).

*Existing xUnit infrastructure covers the framework; new test classes are additive.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live two-replica ≥1-healthy admit / none-qualify 422 + stale-L1 probe fail | GATE-*/PROBE-* (proof) | Requires real Redis + two live processor replicas | Deferred to **Phase 62** RealStack E2E + close gate |

*Hermetic automated verification covers all Phase-61 behaviors; the live proof is Phase 62.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
