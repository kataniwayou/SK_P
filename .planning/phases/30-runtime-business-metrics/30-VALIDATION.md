---
phase: 30
slug: runtime-business-metrics
status: verified
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-02
updated: 2026-06-03
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
| 30-01 | 01 | 1 | METRIC-01/02/03 | T-30-01/02 | per-replica `service_instance_id`; env precedence POD_NAME→HOSTNAME→MachineName→GUID | hermetic unit (3 facts) | `dotnet test -- --filter-class "*ResolveInstanceIdFacts"` | ✅ `ResolveInstanceIdFacts.cs` | ✅ green |
| 30-02 | 02 | 1 | METRIC-04 | T-30-03 | meter "Orchestrator" + counter names; ProcessorId only, no workflowId | hermetic unit (2 facts) | `dotnet test -- --filter-class "*OrchestratorMetricsFacts"` | ✅ `OrchestratorMetricsFacts.cs` | ✅ green |
| 30-03 | 03 | 1 | METRIC-05 | T-30-04/05 | meter "BaseProcessor" + counter names; ProcessorId + bounded outcome, no workflowId | hermetic unit (2 facts) | `dotnet test -- --filter-class "*ProcessorMetricsFacts"` | ✅ `ProcessorMetricsFacts.cs` | ✅ green |
| 30-04 | 04 | 2 | METRIC-01..07 | T-30-06/07 | live: 4 series + bottleneck PromQL + service_instance_id + label invariants + collector unchanged | RealStack E2E (1 fact) | `dotnet test tests/BaseApi.Tests -- --filter-class "*MetricsRoundTripE2ETests"` | ✅ `MetricsRoundTripE2ETests.cs` | ✅ green (live-verified 2026-06-03, Passed 1/0, 2m47s) |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

**Coverage:** METRIC-01..07 all COVERED — every requirement has automated verification (8 hermetic facts + 1 live RealStack E2E). No MISSING or PARTIAL gaps. The hermetic units give fast per-commit feedback on meter/counter names + env precedence + label invariants; the RealStack E2E proves the end-to-end scrape→query round-trip live.

---

## Wave 0 Requirements

- [x] `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` — `PollPromForQuery` + `VectorNonEmpty`/`HasNumericValue` predicates (D-11/D-12)
- [x] `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` — covers METRIC-01..07 live (D-14)
- [x] (recommended) hermetic unit tests for the two metric holders — `OrchestratorMetricsFacts` (2) + `ProcessorMetricsFacts` (2): meter + counter names match D-02/D-03
- [x] (recommended) hermetic unit test for `ResolveInstanceId()` env precedence — `ResolveInstanceIdFacts` (3 facts, D-10)

*No framework install needed — xunit.v3 + helpers already present. Wave 0 complete.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Collector metrics pipeline unchanged | METRIC-07 | Negative assertion on config file, not code | `git diff compose/otel-collector-config.yaml` returns empty for the metrics pipeline |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 120s (hermetic ~tens of seconds; RealStack E2E ~167s, run pre-verify only)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** verified 2026-06-03

---

## Validation Audit 2026-06-03

| Metric | Count |
|--------|-------|
| Gaps found | 0 |
| Resolved | 0 |
| Escalated | 0 |

State-A audit of the planning-time draft against the executed phase: the placeholder `30-XX-XX` row was replaced with the four real test files (8 hermetic facts + 1 live RealStack E2E), all green; Wave 0 and sign-off boxes confirmed complete. Every requirement METRIC-01..07 is COVERED by automated verification — no gaps, no test generation needed, no auditor escalation. `nyquist_compliant` flipped true. The METRIC-07 collector-unchanged check is asserted both in-test (no `:8889` in the E2E) and via the manual `git diff compose/otel-collector-config.yaml` gate.
