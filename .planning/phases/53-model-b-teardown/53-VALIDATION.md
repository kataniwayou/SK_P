---
phase: 53
slug: model-b-teardown
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-11
---

# Phase 53 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Source: `53-RESEARCH.md` § Validation Architecture. Every success criterion + the D-01 end-state
> invariant maps to a HERMETIC fact (reflection or source-scan, NO host boot), mirroring the verified
> `ReactivePathRetiredFacts` / `ModelBContractsRetiredFacts` idiom. The only non-hermetic checks are the
> OPTIONAL live throw-spike (A1) and the D-07 DLQ-routing behavior (gated on a broker).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (repo-pinned; `[Fact]` / `[Trait("Phase","53")]`) |
| **Config file** | none custom — standard xUnit discovery under `tests/BaseApi.Tests` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~RetiredFacts"` |
| **Full suite command** | `dotnet test SK_P.sln -c Release` |
| **Estimated runtime** | quick ~5–15s (hermetic facts, no host) · full suite per repo baseline |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~RetiredFacts"` (fast, hermetic)
- **After every plan wave:** Run `dotnet test SK_P.sln` (full hermetic suite)
- **Before `/gsd-verify-work`:** Full Release + Debug build 0-warning AND full suite green
- **Max feedback latency:** ~15 seconds (hermetic facts)

---

## Per-Task Verification Map

| Criterion / Req | Behavior | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|-----------------|----------|------------|-----------------|-----------|-------------------|-------------|--------|
| SC-1 (composite key + UPDATE/CLEANUP gone) | reflection: no `CompositeBackup` builder; no `KeeperUpdate`/`KeeperCleanup`/`BackupOptions` types | — | N/A | unit (reflection) | `dotnet test ...~ModelBContractsRetiredFacts` | ✅ FACT 1–3 exist (Phase 50) | ⬜ pending |
| SC-2 / RETIRE-03 (5→3 collapse) | reflection: keeper consumes EXACTLY `KeeperReinject`/`Inject`/`Delete`; no 4th/5th | — | N/A | unit (reflection) | `dotnet test ...~ModelBContractsRetiredFacts` | ❌ W0 — add 5→3 fact | ⬜ pending |
| SC-2 / RETIRE-03 (no remnant) | source-scan: no composite-backup-key **BUILDER** usage under `src/**/*.cs` (NOT bare `corr:wf:proc:exec` substring — Pitfall 2) | — | N/A | unit (source-scan) | `dotnet test ...~ModelBContractsRetiredFacts` | ❌ W0 — add scoped sweep | ⬜ pending |
| D-01 end-state (exec + ALL orchestrator endpoints) | source-scan: zero `UseMessageRetry(`/`ConfigureError` **call** pattern under `src/Orchestrator/Consumers` + `src/BaseProcessor.Core/Startup` (match the call, NOT the bare word — survives in 6 doc-comments) | — | poison-send → broker redelivery, no DLQ | unit (source-scan) | `dotnet test ...~RetiredFacts` (or new `ExecutionPathEndStateFacts`) | ❌ W0 — new standing guard | ⬜ pending |
| D-03 (filter keeper-only) | source-scan: `ConfigureError`/`ConsolidatedErrorTransportFilter` reachable ONLY under `src/Keeper/`; absent from `BaseConsole.Core` `MessagingServiceCollectionExtensions` global callback | — | only keeper dead-letters | unit (source-scan) | same suite | ❌ W0 | ⬜ pending |
| D-07 (dead guard removed) | source-scan: `Ignore<WorkflowRootNotFoundException>` gone from both Start/Stop definitions (pure teardown — no DLQ seam added) | — | missing-root keeps log+ack | unit (source-scan) | `dotnet test ...~RetiredFacts` | ❌ W0 | ⬜ pending |
| D-04 / A1 (throw → redelivery) | live: throwing exec-path consumer redelivers, produces no `skp-dlq-1` traffic | T-53-DoS | no silent message loss | integration (live, OPTIONAL) | reuse keeper SustainedOutage live harness | ❌ W0 (optional; broker-gated) | ⬜ pending |
| SC-3 (0-warning Release+Debug) | clean build both configs | — | N/A | build gate | `dotnet build SK_P.sln -c Release` && `-c Debug` | n/a — toolchain | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] Extend `tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs` — 5→3 reflection fact (SC-2 / RETIRE-03)
- [ ] Add scoped composite-backup-key-**BUILDER** source sweep (NOT bare `corr:wf:proc:exec` substring — Pitfall 2) (SC-2)
- [ ] Add D-01 end-state standing guard — no `UseMessageRetry`/`ConfigureError` under exec-path + system-command source dirs (here or sibling `ExecutionPathEndStateFacts.cs`)
- [ ] Add D-03 scoping fact — error filter reachable only under `src/Keeper/`, absent from `BaseConsole.Core` global callback
- [ ] Add D-07 dead-guard fact — `Ignore<WorkflowRootNotFoundException>` removed from both Start/Stop definitions (pure teardown; no DLQ seam)
- [ ] (Optional) live throw-spike reusing the keeper SustainedOutage harness — confirms A1
- All reuse the existing `RepoRoot()` `[CallerFilePath]` anchor + `Directory.Exists` false-pass guard from `ReactivePathRetiredFacts`

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Poison-send redelivery (A1) produces no DLQ traffic | D-04 / A1 | No-error-pipeline nack-requeue is broker-runtime behavior | If broker available: bind throwing exec-path consumer, assert redelivery + zero `skp-dlq-1`. Otherwise confirmed Phase 54. |

*Hermetic source-scan + reflection facts carry SC-1/SC-2/SC-3/D-01/D-03/D-07 with no broker. The live item above is OPTIONAL confirmation and otherwise falls to Phase 54's live proof.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 15s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
