---
phase: 39
slug: keeper-observability-real-stack-e2e-close-gate
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-06
---

# Phase 39 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Falsifiable signals derived from 39-RESEARCH.md `## Validation Architecture`.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | Microsoft.Testing.Platform (MTP) + xUnit (`tests/BaseApi.Tests`) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (MTP entrypoint) |
| **Quick run command** | `dotnet build -c Release` (0-warning) then class-filtered MTP run |
| **Full suite command** | `pwsh scripts/phase-39-close.ps1` (3×GREEN + triple-SHA, RealStack live) |
| **Estimated runtime** | Build ~tens of seconds; full close gate ~minutes (3× live suite) |

---

## Sampling Rate

- **After every task commit:** `dotnet build -c Release` (0-warning) + unit/class-filtered MTP for touched code
- **After every plan wave:** Run the relevant RealStack fact(s) live (`KeeperRecovery_*`)
- **Before `/gsd-verify-work`:** `scripts/phase-39-close.ps1` must exit 0 (3×GREEN + triple-SHA net-zero)
- **Max feedback latency:** build+unit < ~60s; full live gate is the final pre-verify gate

---

## Per-Task Verification Map

> Populated during planning (plan/task IDs) and execution. Signals below come from RESEARCH `## Validation Architecture`.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| TBD | TBD | TBD | KMET-01 | — | `Keeper` meter registered (MeterName const ↔ AddMeter symmetry, IMeterFactory) | unit/build | `dotnet build -c Release` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | KMET-02 | — | throughput/outcome counters emit with `fault_type`/`ProcessorId`/`reason`, no `workflowId` | e2e scrape | `KeeperRecovery_*` live | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | KMET-03 | — | `keeper_in_flight` UpDownCounter + `keeper_recovery_duration` histogram scrapable | e2e scrape | `KeeperRecovery_*` live | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | TEST-01 | — | recover-both-paths E2E asserts `keeper_*` series + labels after live recover | e2e | `KeeperRecovery_RecoversBothPaths` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | TEST-02 | — | give-up E2E asserts `keeper_dlq_pushed` + message in `keeper-dlq` | e2e | `KeeperRecovery_GivesUp_ParksToDlq` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | TEST-03 | — | `phase-39-close.ps1` 3×GREEN + triple-SHA + both DLQs depth==0 + scratch scan-clean | gate | `pwsh scripts/phase-39-close.ps1` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] Confirm transitive `System.Diagnostics.DiagnosticSource` version supports `InstrumentAdvice<double>` / `HistogramBucketBoundaries` on net8.0 (one `dotnet list package --include-transitive` check) — fallback is OTel `AddView` ExplicitBucketBoundaries.
- [ ] Confirm Prometheus suffix mapping with one live scrape (Counter→`_total`, Histogram→`_count`/`_sum`/`_bucket` + `_seconds` if unit="s", UpDownCounter→bare gauge) so test queries match the chosen histogram unit.

*Existing test infrastructure (`PrometheusTestClient`, `KeeperRecoveryE2ETests`, `phase-33-close.ps1` clone source) covers all other phase requirements.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| `keeper_in_flight` gauge transient value | KMET-03 | UpDownCounter is point-in-time; value may have settled to 0 by scrape time | Best-effort E2E assertion that the series exists; exact transient value not asserted |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references (DiagnosticSource version + histogram suffix)
- [ ] No watch-mode flags
- [ ] Feedback latency acceptable (build+unit fast; live gate is final pre-verify)
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
