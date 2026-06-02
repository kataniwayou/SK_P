---
phase: 30
slug: runtime-business-metrics
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-02
---

# Phase 30 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Source of truth for the test map is `30-RESEARCH.md` § Validation Architecture.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 (+ xunit.v3.assert, xunit.runner.visualstudio; MTP runner) |
| **Config file** | per-project; `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test --filter "Category!=RealStack"` (hermetic) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests -- --filter-class "*MetricsRoundTripE2ETests"` (RealStack — needs compose up) |
| **Estimated runtime** | hermetic ~tens of seconds; RealStack E2E ≥120s budget (scrape→export→scrape latency) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test --filter "Category!=RealStack"` (hermetic — includes new holder/precedence unit tests)
- **After every plan wave:** hermetic suite + (if compose up) `MetricsRoundTripE2ETests`
- **Before `/gsd-verify-work`:** Full suite green; `git diff compose/otel-collector-config.yaml` empty (METRIC-07 gate)
- **Max feedback latency:** ~120 seconds (RealStack E2E poll budget)

---

## Per-Task Verification Map

See `30-RESEARCH.md` § Validation Architecture → "Phase Requirements → Test Map" for the authoritative
REQ→test mapping (METRIC-01..07). The planner fills concrete `{N}-{plan}-{task}` IDs here during planning.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 30-XX-XX | XX | 1 | METRIC-01..06 | — | bounded labels only (no workflowId) | RealStack E2E | `MetricsRoundTripE2ETests` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` — `PollPromForQuery` (D-11/D-12)
- [ ] `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` — covers METRIC-01..06 (D-14)
- [ ] (recommended) hermetic unit test for the two metric holders (meter + counter names match D-02/D-03)
- [ ] (recommended) hermetic unit test for `ResolveInstanceId()` env precedence (D-10)

*No framework install needed — xunit.v3 + helpers already present.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Collector metrics pipeline unchanged | METRIC-07 | Negative assertion on config file, not code | `git diff compose/otel-collector-config.yaml` returns empty for the metrics pipeline |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
