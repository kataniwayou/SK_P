---
phase: 67
slug: fault-injection-harness
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-14
---

# Phase 67 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
>
> **Nature of this phase:** an ops/test harness (`scripts/phase-67-harness.ps1`) that
> composes already-proven artifacts. Its "tests" are (a) the harness's own fail-loud
> per-step self-checks with distinct exit codes (V1–V11, from 67-RESEARCH.md §Validation
> Architecture), and (b) the two reference runs — TEST-01 no-fault baseline then a
> TEST-02-shaped processor crash (D-10) — which ARE the phase's validation cohort.
> There is no new product code; the only compile-checked change is the ~10-line env-var
> seam in the Phase 66 analyzer test fixture (D-16).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (`dotnet test` RealStack fixtures) + PowerShell harness self-checks; no new framework |
| **Config file** | none — uses existing `tests/BaseApi.Tests` project + `compose.yaml` |
| **Quick run command** | `dotnet test --filter "Category=RealStack&FullyQualifiedName~Analyzer"` (analyzer fixture still green with D-16 defaults) |
| **Full suite command** | `pwsh scripts/phase-67-harness.ps1 -ScenarioId TEST-01` then `-ScenarioId TEST-02` (the two reference runs) |
| **Estimated runtime** | ~8–10 min per reference run (5-min window + ~2-min drain + bring-up/reset); ~20 min both + teardown |

---

## Sampling Rate

- **After every task commit:** Run the cheapest relevant self-check — for the env-var seam task, `dotnet test --filter ~Analyzer` (confirms Phase 66 still passes with defaults); for harness-step tasks, a targeted dry-run of that step (e.g. invoke the psql lookup / activation gate against an up stack).
- **After every plan wave:** Run the affected reference run end-to-end (`phase-67-harness.ps1 -ScenarioId TEST-01`).
- **Before `/gsd-verify-work`:** Both reference runs complete fully automated (no human step) and each produces an `analyzer-reports/{scenarioId}.json` + verdict; TEST-01 expected PASS, TEST-02 expected PASS (non-PASS = real finding to investigate per D-11, not a harness defect).
- **Max feedback latency:** single self-check < 15s; a full reference run ~10 min.

---

## Per-Task Verification Map

> Maps to the harness self-checks V1–V11 (67-RESEARCH.md). Plan/task IDs are indicative —
> the planner sets final IDs. "Automated Command" is what proves the step worked.

| Check | Step | Requirement | Threat Ref | Secure/Correct Behavior | Test Type | Automated Command / Signal | Status |
|-------|------|-------------|------------|--------------------------|-----------|-----------------------------|--------|
| V1 | Bring-up | FAULT-03 | — | all 10 service types healthy, 0 badconfig | infra gate | `phase-65-up.ps1` exit 0 (else harness exit 10) | ⬜ pending |
| V2 | Reset | FAULT-03 | — | FLUSHALL + liveness key reappears + row DELETE + ≥1 processor replica | infra gate | `phase-65-reset.ps1` exit 0 (else exit 20) | ⬜ pending |
| V3 | Seed | FAULT-01 | — | 1 wf / 9 steps / 8 edges / 9 assignments, idempotent | fixture | `dotnet test ~FanOutSeeder` exit 0 (else exit 30) | ⬜ pending |
| V4 | Wf-id resolve | FAULT-01 | T-67-psql | exactly one non-empty GUID for `v8-fanout-proof` | infra gate | psql `-tA SELECT id` non-empty (else exit 40) | ⬜ pending |
| V5 | Activation gate | FAULT-01 | — | `POST /api/v1/orchestration/start` returns **204** | http gate | `Invoke-WebRequest` StatusCode == 204 (else exit 50) | ⬜ pending |
| V6 | Baseline firing | FAULT-01 | — | cron actually fires: `orchestrator_dispatch_sent_total` delta reaches N=4 pre-injection | poll | Prom query delta ≥ 4 within pre-injection window (else exit 60) | ⬜ pending |
| V7 | Fault injected | FAULT-02 | — | whole `processor-sample` tier DOWN during dwell | infra op | `docker compose stop processor-sample` exit 0; `compose ps` shows 0 running (else exit 60) | ⬜ pending |
| V8 | Recovery | FAULT-02 | — | both replicas back to Health=healthy before window close | health-wait | `docker compose start` exit 0 + NDJSON health loop both healthy (else exit 60) | ⬜ pending |
| V9 | Verdict produced | FAULT-03 | — | `analyzer-reports/{scenarioId}.json` exists; analyzer exit captured (0=PASS, non-0=FAIL) | fixture | `dotnet test ~Analyzer` with `SCENARIO_ID`/`WINDOW_*` env set; report file present | ⬜ pending |
| V10 | Teardown | FAULT-03 | — | `docker compose down`, no lingering containers (volumes+images kept) | infra gate | `docker compose down` exit 0 (else exit 70, non-fatal) | ⬜ pending |
| V11 | End-to-end automation | FAULT-03 | — | whole sequence ran with NO `Read-Host` / no human prompt | design | grep harness for `Read-Host` → none; full run completes unattended | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] None — existing infrastructure covers all phase requirements. The harness composes
      `phase-65-up.ps1` / `phase-65-reset.ps1` / `dotnet test ~FanOutSeeder` / `~Analyzer`
      (all proven, self-verifying) and adds only the new orchestrator script + the D-16
      env-var seam. No new test framework or fixture scaffolding needed.

*The D-16 env-var seam (`AnalyzerE2ETests.cs`) must keep its `const`/`UtcNow` fallback so the
standalone `dotnet test ~Analyzer` (Phase 66 regression) stays green — verify that first.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| TEST-02 crash run produces a verdict either way | FAULT-02, FAULT-03 | Requires a real 5-min window + a live docker fault; cannot be unit-tested | Run `pwsh scripts/phase-67-harness.ps1 -ScenarioId TEST-02`; confirm it injects the processor crash mid-window, recovers, drains, and writes `analyzer-reports/TEST-02.json` with a verdict. Verdict value is a finding (D-11), not a harness pass/fail. |
| Full unattended automation | FAULT-03 | "no human step" is observable only by running the whole sequence | Launch the harness and confirm it runs clean→seed→activate→inject→observe→analyze→teardown with zero prompts. |

*The two reference runs are the validation cohort; both are operator-launched, fully automated thereafter.*

---

## Validation Sign-Off

- [ ] Every harness step has a fail-loud self-check with a distinct exit code (V1–V11)
- [ ] Sampling continuity: no 3 consecutive harness steps without an automated success signal
- [ ] Wave 0 covers all MISSING references (none required)
- [ ] No watch-mode flags / no interactive prompts (`Read-Host`) in the harness
- [ ] D-16 env-var seam preserves Phase 66 standalone green (defaults fallback verified)
- [ ] Both reference runs (TEST-01, TEST-02) produce an analyzer report + verdict, no human step
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
