---
phase: 71
slug: orchestrator-recovery-pipeline
status: planned
nyquist_compliant: true
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
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (MTP entrypoint: OutputType=Exe + UseMicrosoftTestingPlatformRunner) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -- --filter-method "*<Facts>*"` (NOTE: `--filter` is silently ignored under MTP — use `-- --filter-method`) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (~638 tests) |
| **Estimated runtime** | targeted ~seconds; full suite minutes (set precisely during Wave 0) |

---

## Sampling Rate

- **After every task commit:** Run the targeted `-- --filter-method` set for the touched facts
- **After every plan wave:**
  - Wave 1 (rename): FULL suite (`dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj`) — the rename must compile + ALL existing facts green
  - Wave 2/3: the `*OrchestratorContract*` / `*OrchestratorResultPipeline*` / `*OrchestratorInjectConsumer*` / `*OrchestratorReinjectConsumer*` / `*DeleteInvariant*` subsets
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** targeted subsets resolve in seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 71-01-01 | 01 | 1 | ORCV-06 | T-71-01 | Rename compiles green; ReinjectConsumerDefinition + KeeperDelete untouched | compile | `dotnet build SK_P4.sln -c Debug` / `-c Release` (0-warning) | partial (existing facts) | ⬜ pending |
| 71-01-02 | 01 | 1 | ORCV-06 | T-71-02 | Full suite green post-rename; D-10 5-arg StringSetAsync stub present | full | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` | partial (existing facts) | ⬜ pending |
| 71-02-01 | 02 | 2 | ORCV-06 | T-71-04 | Contracts compile; implement IKeeperRecoverable; STJ default | compile | `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj` | ❌ W0 | ⬜ pending |
| 71-02-02 | 02 | 2 | ORCV-06 | T-71-03 | Round-trip + IKeeperRecoverable + origin-agnostic PartitionGuid | unit | `dotnet test ... -- --filter-method "*OrchestratorContract*"` | ❌ W0 | ⬜ pending |
| 71-03-00 | 03 | 3 | ORCV-01..05 | — | Wave-0 test kit + fact scaffolds; project compiles | compile | `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj` | ❌ W0 | ⬜ pending |
| 71-03-01 | 03 | 3 | ORCV-01,02,03,04,05 | T-71-05,06,07,08 | Gate; single atomic Lua FORWARD w/ JSON tuple + GET/SET copy (TTL in ARGV); dispatch+retire; GATE-01 gated 2-key cleanup; 3-way RECOVERY; only deleter is cleanup tail | unit | `dotnet test ... -- --filter-method "*OrchestratorResultPipeline*"` | ❌ W0 | ⬜ pending |
| 71-03-02 | 03 | 3 | ORCV-01 | T-71-05 | Pipeline invoked from TypedResultConsumer seam on context.MessageId w/ null-guard | compile+unit | `dotnet build SK_P4.sln` + `... -- --filter-method "*TypedResultConsumer*"` | partial | ⬜ pending |
| 71-04-01 | 04 | 3 | ORCV-06,07 | T-71-09,10,11 | 2 consumers extend RecoveryConsumerBase; outcome factory only branch; bind on keeper-recovery; ExcludeFromConfigureEndpoints; zero KeyDeleteAsync | compile | `dotnet build SK_P4.sln -c Debug` / `-c Release` | ❌ W0 | ⬜ pending |
| 71-04-02 | 04 | 3 | ORCV-06,07 | T-71-09,12 | Copy+dispatch fact; outcome->IStepResult factory (4 outcomes); both consumers never delete (both overloads + positive co-assert) | unit | `... -- --filter-method "*OrchestratorInjectConsumer*"` / `"*OrchestratorReinjectConsumer*"` / `"*DeleteInvariant*"` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] Plan 01 Task 2 adds the `RecoveryTestKit.Db()` 5-arg `StringSetAsync` stub (D-10 / 70-REVIEW WR-01) before any consumer-binding fact runs
- [ ] Plan 03 Task 0 creates `OrchestratorPipelineTestKit.cs` (full IDatabase stub surface) + enumerates the Forward/Recovery fact targets
- [ ] Plan 04 Task 2 extends `RecoveryTestKit.CapturingSendProvider` to capture the boxed `object` Send (StepFailed/StepCancelled/StepProcessing + OrchestratorReinject re-send) if not already generic
- [ ] Confirm targeted `-- --filter-method` invocations resolve (small test count, not ~638) for every new `Orchestrator*` fact

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live close-gate net-zero proof of orchestrator recovery (real-stack fault injection) | (deferred — Future Requirements) | Requires the running compose stack + ~50min close-gate protocol; deferred to v8.0.0 7-scenario harness | Out of scope for this phase — phase proves recovery at the hermetic/unit level. `SC2RecoveryPathsE2ETests` (rename-updated in Plan 01) is the live E2E hook for the future milestone. |

*All in-scope phase behaviors (ORCV-01..ORCV-07) have automated hermetic verification.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or a Wave 0 dependency
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (test kits + stubs created before the facts that need them)
- [x] No watch-mode flags (every command is a single-shot `dotnet build`/`dotnet test`)
- [x] All targeted commands use `-- --filter-method` (never `--filter`)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** planned (pending execution)
