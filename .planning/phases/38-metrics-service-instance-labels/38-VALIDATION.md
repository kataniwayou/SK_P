---
phase: 38
slug: metrics-service-instance-labels
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-06
---

# Phase 38 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Source signals: `38-RESEARCH.md` → ## Validation Architecture.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 3.2.2 (`xunit.v3`, `xunit.v3.assert`) — `Directory.Packages.props:121-123` |
| **Config file** | per-project; `[Collection("Observability")]` serializes env-mutating tests |
| **Quick run command** | `dotnet test --filter-not-trait "Category=RealStack"` (hermetic only) |
| **Full suite command** | full compose stack up + `dotnet test` (includes `[Trait("Category","RealStack")]`) |
| **Estimated runtime** | hermetic ~tens of seconds; RealStack scrape assertions poll 90–120s (Prom pull latency) |

---

## Sampling Rate

- **After every task commit:** Run hermetic suite (`--filter-not-trait "Category=RealStack"`) — covers holder + contract + logs-resource assertions.
- **After every plan wave:** Full hermetic suite green.
- **Before `/gsd-verify-work`:** RealStack `MetricsRoundTripE2ETests` (resolved `service_name` + instance-id across families) + Phase-35 SC3 ES (logs bare) + static grep gate, all green.
- **Max feedback latency:** hermetic < 60s; RealStack scrape assertions allow 90–120s poll budget (SDK 60s export + 15s scrape).

---

## Per-Task Verification Map

> Filled by the planner/executor as tasks are defined. Each row maps a task to an observable MLBL signal.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 38-01-01 | 01 | 1 | MLBL-03 | — | N/A | unit | `dotnet test --filter-not-trait "Category=RealStack"` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Observable Signals (per MLBL requirement)

| Req | Observable signal | Test type | Where |
|-----|-------------------|-----------|-------|
| MLBL-01 | `service_name="{name}_{version}"` on a runtime, an HTTP, and a business series; NO bare-name series for the 4 services | RealStack scrape | extend `MetricsRoundTripE2ETests` + `MetricsExportTests` |
| MLBL-02 | non-empty `service_instance_id` on runtime + HTTP + business series | RealStack scrape | `MetricsRoundTripE2ETests` (add HTTP + business family checks) |
| MLBL-03 (i) | `ProcessorIdentityFound` has Name/Version; responder populates | hermetic unit | new contract/responder test |
| MLBL-03 (ii) | live processor business series `service_name = {db.Name}_{db.Version}` | RealStack | `MetricsRoundTripE2ETests` |
| MLBL-03 (iii) | appsettings `Service:Name`/`Service:Version` keys retained | static / config-load | grep `src/Processor.Sample/appsettings.json` |
| MLBL-03 (iv) | pre-resolve series `service_name = processor-sample_3.5.0`; swaps on resolve | hermetic holder test (robust) | `MeterProviderHolderFacts` (NEW) |
| MLBL-04 | logs resource `service.name` has no `_{version}` suffix; Phase-35 ES `service.name="keeper"` passes | hermetic resource attr + RealStack ES | logs-resource assertion + Phase-35 SC3 |
| MLBL-05 | zero bare `service_name="<name>"` literals for the 4 services; updated Phase-11 assertion passes | static grep + RealStack | grep gate + `MetricsExportTests`/`SchemasMetricsE2ETests` |

**Highest-value new test (race-free swap proof):** hermetic `MeterProviderHolderFacts` — build holder with known instance id + placeholder name; assert provider #1 resource `service.name="processor-sample_3.5.0"` + captured instance id; `SwapTo("db-name_9.9.9")`; assert new provider resource `service.name="db-name_9.9.9"` + SAME instance id; assert provider #1 disposed. Proves MLBL-03 (iv)+(ii) mechanics + instance-id preservation without the live boot-window race.

---

## Wave 0 Requirements

- [ ] `tests/.../MeterProviderHolderFacts.cs` — hermetic swap proof (NEW; highest value)
- [ ] Contract/responder test for `ProcessorIdentityFound` Name/Version population (NEW)
- [ ] Update `StubContext` + any `IProcessorContext` fake for the 2 new members (else CS0535 compile break)
- [ ] Extend `MetricsRoundTripE2ETests` with `service_name="{name}_{version}"` assertions across families
- [ ] Logs-resource bare-`service.name` hermetic assertion (MLBL-04)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| (none expected) | — | Phase is hermetic + scrape-assertion provable; no live operator step required (SPEC constraint). | — |

*All phase behaviors have automated verification (hermetic + RealStack scrape). No live operator verification required.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s (RealStack scrape budget)
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
