---
phase: 33
slug: fault-recovery-spike-de-risk
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-05
---

# Phase 33 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Spike phase: the deliverable is a RealStack proof. The hermetic half (compile + non-RealStack suite) is autonomously runnable; the LIVE half (`FaultRecoverySpikeE2ETests` + close gate) is an operator-gated `autonomous:false` run requiring the rebuilt v3.7.0 compose stack.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 + `WebApplicationFactory` (`RealStackWebAppFactory`) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet build SK_P.sln -c Debug` (compile gate) + `dotnet test SK_P.sln -c Debug --filter-not-trait "Category=RealStack"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests -- --filter-class "*FaultRecoverySpikeE2ETests"` (RealStack — needs live stack) |
| **Estimated runtime** | hermetic ~60–120s · live E2E ~2–4 min (cron-minute waits + ES ingest settle) |

---

## Sampling Rate

- **After every task commit:** `dotnet build SK_P.sln -c Debug` + the hermetic `--filter-not-trait "Category=RealStack"` suite (must stay GREEN; zero regression).
- **After every plan wave:** full hermetic suite + Release 0-warning build.
- **Before phase close:** the LIVE `FaultRecoverySpikeE2ETests` + `phase-33-close.ps1` (operator-gated) — 3× GREEN + triple-SHA BEFORE==AFTER.
- **Max feedback latency:** ~120s (hermetic); live run is operator-cadence.

---

## Per-Task Verification Map

> Scaffold — gsd-planner / gsd-nyquist-auditor populate exact Task IDs once plans exist. Observable signals per success criterion:

| Success Criterion | Requirement | Observable Signal (Nyquist sample) | Test Type |
|-------------------|-------------|------------------------------------|-----------|
| SC1 — pub/sub bind both fault types; command-faults NOT delivered | INTAKE-01 | in-test consumer capture count ≥1 for `Fault<EntryStepDispatch>` + `Fault<ExecutionResult>`; capture count ==0 for published `Fault<Start/Stop>Orchestration` over settle window | E2E (live) |
| SC2 — extract inner msg + 6-id tuple + `H` | INTAKE-02 | asserted non-empty `correlationId/workflowId/stepId/processorId/entryId/executionId` + `H` read from `context.Message.Message`; ES log opened from inner-message scope | E2E (live) |
| SC3 — re-inject to origin by type, exactly-once | INTAKE-04 | `PollEsForLog` "step output written" effect appears exactly once per re-injected identity; correct origin endpoint (`queue:{procId:D}` / `orchestrator-result`) | E2E (live) |
| SC4 — duplicate collapses on receiver `flag[H]`; `_error` decision recorded | PROBE-06 | second re-inject → hit-count probe == 1 (no extra effect); `flag[H]`/`flag[m.H]` gates unchanged (grep); `_error` retention recorded in SUMMARY | E2E (live) + doc |
| Net-zero | — | `redis-cli --scan` SHA BEFORE==AFTER; `skp:data:*`/`skp:flag:*` registered to `L2KeysToCleanup` | close gate |

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` — the spike proof (cloned from `IdempotentExactlyOnceE2ETests`)
- [ ] Existing `RealStackWebAppFactory` / `PollEsForLog` / liveness-poll helpers cover all infrastructure — no new fixtures needed.

*Existing infrastructure covers all phase requirements beyond the one new test file.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live `Fault<T>` trip → recover → re-inject → collapse | INTAKE-01/02/04, PROBE-06 | Requires rebuilt processor-sample/orchestrator/baseapi containers (embedded SourceHash must match host build) + full live compose stack; not runnable in a non-interactive executor | `docker compose up -d --build processor-sample orchestrator baseapi` → `dotnet test tests/BaseApi.Tests -- --filter-class "*FaultRecoverySpikeE2ETests"` (expect GREEN) → `pwsh -NoProfile -File ./scripts/phase-33-close.ps1` (expect GATE_EXIT=0). Read GATE_*_EXIT from the gate output, not the bg-task wrapper exit. |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s (hermetic)
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
