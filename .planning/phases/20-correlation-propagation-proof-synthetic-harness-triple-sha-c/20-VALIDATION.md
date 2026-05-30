---
phase: 20
slug: correlation-propagation-proof-synthetic-harness-triple-sha-c
status: planned
nyquist_compliant: true
wave_0_complete: false
created: 2026-05-30
---

# Phase 20 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Validation Architecture detail lives in `20-RESEARCH.md` `## Validation Architecture`.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 (.NET 8) — `tests/BaseApi.Tests` |
| **Config file** | none (CPM + standard xUnit); `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release --filter "Category!=RealStack"` (hermetic; in-memory + dead-host only) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` (real-stack TEST-RMQ-02 needs the full Docker stack up) |
| **Estimated runtime** | in-memory tests < 30s each; full suite ~3-4 min (v3.3.0 baseline ~3m17s) + TEST-RMQ-02 ES poll up to 30s |

---

## Sampling Rate

- **After every task commit:** Run the per-task `--filter-class <TheTestClass>` command (in-memory tests < 30s).
- **After every plan wave:** Run the quick run command (hermetic suite); after Wave 3, run the full suite with the stack up.
- **Before `/gsd-verify-work`:** Full suite green + close gate 3× GREEN (Plan 20-04).
- **Max feedback latency:** < 30s per task (hermetic); ~4 min per wave merge.

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 20-01-T1 | 01 | 1 | CORR-04, TEST-RMQ-02 (enabler) | T-20-03 | Structured `{CorrelationId}` log property, no template injection | unit/build | `dotnet test ... --filter-class *StartStopConsumerAckTests` + `*OrchestrationServicePublishTests` | StopOrchestrationConsumer.cs, StartStopConsumerAckTests.cs, OrchestrationService.cs, OrchestrationServicePublishTests.cs | ⬜ pending |
| 20-01-T2 | 01 | 1 | TEST-RMQ-02 (enabler) | T-20-02 | Port is non-secret; creds still via cfg.Require key-only | build/regression | `dotnet build -c Release` + `*HealthEndpointsTests` | MessagingServiceCollectionExtensions.cs | ⬜ pending |
| 20-01-T3 | 01 | 1 | TEST-RMQ-02 (enabler), close-gate prereq | T-20-01 | `--no-install-recommends` minimal apt layer; pinned base image | build | `dotnet build tests/BaseApi.Tests -c Release` (harness API resolves) | HarnessWebAppFactory.cs, Dockerfile | ⬜ pending |
| 20-02-T1 | 02 | 2 | TEST-RMQ-01, TEST-RMQ-04 | — | In-memory hermetic; temporary/auto-delete, no global purge | in-memory | `dotnet test ... --filter-class *FanOutBroadcastTests` | FanOutBroadcastTests.cs | ⬜ pending |
| 20-02-T2 | 02 | 2 | CORR-03 | — | In-memory hermetic; ambient envelope stamp | in-memory | `dotnet test ... --filter-class *OutboundFilterSyntheticTests` | OutboundFilterSyntheticTests.cs | ⬜ pending |
| 20-02-T3 | 02 | 2 | TEST-RMQ-03 | T-20-04, T-20-05 | Secret-free /health/ready body; env-var capture+restore | integration (dead-host) | `dotnet test ... --filter-class *HealthEndpointsTests` | HealthEndpointsTests.cs | ⬜ pending |
| 20-03-T1 | 03 | 3 | TEST-RMQ-02, CORR-04 | T-20-08 | Env-var capture+restore; RealStack-tagged | build | `dotnet build tests/BaseApi.Tests -c Release` | CorrelationPropagationE2ETests.cs | ⬜ pending |
| 20-03-T2 | 03 | 3 | TEST-RMQ-02, CORR-04 | T-20-06, T-20-07 | Opaque correlation id as scope value (inherited) | real-stack E2E | `dotnet test ... --filter-class *CorrelationPropagationE2ETests` (stack up) | CorrelationPropagationE2ETests.cs | ⬜ pending |
| 20-04-T1 | 04 | 4 | TEST-RMQ-04, TEST-RMQ-05 | T-20-09, T-20-10 | `-1` false-green fixed (integrity hardening); names-only snapshot | script-parse | `pwsh ParseFile scripts/phase-20-close.ps1` | phase-20-close.ps1 | ⬜ pending |
| 20-04-T2 | 04 | 4 | TEST-RMQ-04, TEST-RMQ-05 | T-20-09, T-20-10 | same as T1 (bash parity) | script-parse | `bash -n scripts/phase-20-close.sh` | phase-20-close.sh | ⬜ pending |
| 20-04-T3 | 04 | 4 | TEST-RMQ-04, TEST-RMQ-05 | T-20-11 | dev-only guest/guest; host-side gate | gate (3×GREEN + triple-SHA) | `pwsh -File scripts/phase-20-close.ps1` (full stack up) | — (operator checkpoint) | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

Resolved inline during Plan 20-01 / 20-02 (not as a separate spike plan — each is a small, low-risk verification with a documented fallback):

- **A1 (`RemoveMassTransit()` surface in 8.5.5)** — resolved in Plan 20-01 Task 3 while writing HarnessWebAppFactory; fallback = manual `RemoveAll` of MT descriptors + hosted service. Recorded in 20-01-SUMMARY.
- **A2 (two-bus fan-out idiom)** — resolved in Plan 20-02 Task 1 (single-harness/two-distinct-consumer-types vs two providers); fallback = two `IServiceProvider` harnesses. Recorded in 20-02-SUMMARY.
- **A3 / OQ#1 (RabbitMq:Port for the in-process WebApi → host 5673)** — resolved in Plan 20-01 Task 2 (added a `RabbitMq:Port` read defaulting to 5672; the in-process WebApi sets 5673). This is the HIGH item flagged before TEST-RMQ-02 — landed BEFORE Plan 20-03.
- **A4 (D-07 structured-property serialization to ES)** — resolved in Plan 20-03 Task 2; fallback = query the published doc by message TEXT. Recorded in 20-03-SUMMARY.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| (none) | — | Phase 20 absorbs the Phase 19 human-UAT correlation item into automated TEST-RMQ-02 | The only human touchpoint is the Plan 20-04 close-gate operator checkpoint, which CONFIRMS an automated gate result (3×GREEN + triple-SHA) rather than performing a manual verification. |

*Zero manual-only verifications — the Phase 19 human-UAT correlation proof is now automated as TEST-RMQ-02.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies (20-03-T2 marked MISSING — depends on stack-up close gate in Plan 20-04)
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (A1/A2/A3/A4 resolved inline with fallbacks)
- [x] No watch-mode flags
- [x] Feedback latency target set (< 30s per task; ~4 min per wave)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** planned (executor confirms green during execution)
