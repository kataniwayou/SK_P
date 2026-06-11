---
phase: 53
slug: model-b-teardown
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-11
updated: 2026-06-11
---

# Phase 53 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Source: `53-RESEARCH.md` § Validation Architecture. Every success criterion + the D-01 end-state
> invariant maps to a HERMETIC fact (reflection or source-scan, NO host boot), mirroring the verified
> `ReactivePathRetiredFacts` / `ModelBContractsRetiredFacts` idiom. The only non-hermetic check is the
> OPTIONAL live throw-spike (A1), whose core secure behavior is also asserted hermetically by FACT 6.
>
> **Audited 2026-06-11 (State A):** all 8 hermetic facts GREEN (`ModelBContractsRetiredFacts`,
> 8/8, 429ms) + Release/Debug 0-warning build. No MISSING automated gaps. nyquist_compliant: true.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (repo-pinned; `[Fact]` / `[Trait("Phase","53")]`) |
| **Config file** | none custom — standard xUnit discovery under `tests/BaseApi.Tests` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --no-build -- --filter-class "BaseApi.Tests.Resilience.ModelBContractsRetiredFacts"` |
| **Full suite command** | `dotnet test SK_P.sln -c Release` |
| **Estimated runtime** | quick ~0.4s (8 hermetic facts, no host) · full suite per repo baseline |

> Note: the repo uses the Microsoft Testing Platform (MTP) runner, which IGNORES the VSTest
> `--filter` arg (warning MTP0001). Use the MTP-native `-- --filter-class` / `-- --filter-trait "Phase=53"` form.

---

## Sampling Rate

- **After every task commit:** Run the hermetic guard class (fast, ~0.4s)
- **After every plan wave:** Run `dotnet test SK_P.sln` (full hermetic suite)
- **Before `/gsd-verify-work`:** Full Release + Debug build 0-warning AND full suite green
- **Max feedback latency:** ~15 seconds (hermetic facts)

---

## Per-Task Verification Map

| Criterion / Req | Behavior | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|-----------------|----------|------------|-----------------|-----------|-------------------|-------------|--------|
| SC-1 (composite key + UPDATE/CLEANUP gone) | reflection: no `CompositeBackup` builder; no `KeeperUpdate`/`KeeperCleanup`/`BackupOptions` types | — | N/A | unit (reflection) | `...--filter-class ...ModelBContractsRetiredFacts` | ✅ FACT 1/2/3 (+4 survivor) | ✅ green |
| SC-2 / RETIRE-03 (5→3 collapse) | reflection: keeper consumes EXACTLY `KeeperReinject`/`Inject`/`Delete`; no 4th/5th | — | N/A | unit (reflection) | same suite | ✅ FACT 5 (added 53-01) | ✅ green |
| SC-2 / RETIRE-03 (no remnant) | reflection: composite-backup-key BUILDER gone + Model-B type absence (stronger than a bare source substring — Pitfall 2) | — | N/A | unit (reflection) | same suite | ✅ FACT 1/2/3 | ✅ green |
| D-01 end-state (exec + ALL orchestrator endpoints) | source-scan: zero `UseMessageRetry(`/error-transport CALL pattern under `src/Orchestrator/Consumers` + `src/BaseProcessor.Core/Startup` (call, NOT bare word) | T-53-Tamper / T-53-Regress | poison-send → broker redelivery, no DLQ | unit (source-scan) | same suite | ✅ FACT 6 (GREEN after 53-02/03) | ✅ green |
| D-03 (filter keeper-only) | source-scan: `ConfigureError` global application absent from `BaseConsole.Core` `MessagingServiceCollectionExtensions`; keeper-local only | T-53-Topology | only keeper dead-letters | unit (source-scan) | same suite | ✅ FACT 7 | ✅ green |
| D-07 (dead guard removed) | source-scan: `Ignore<WorkflowRootNotFoundException>` gone from both Start/Stop definitions (pure teardown — no DLQ seam added) | — | missing-root keeps log+ack | unit (source-scan) | same suite | ✅ FACT 8 | ✅ green |
| SC-3 (0-warning Release+Debug) | clean build both configs | — | N/A | build gate | `dotnet build SK_P.sln -c Release` && `-c Debug` | n/a — toolchain | ✅ green (0/0 both) |
| D-04 / A1 (throw → redelivery) | live: throwing exec-path consumer redelivers, produces no `skp-dlq-1` traffic | T-53-DoS | no silent message loss | integration (live, OPTIONAL) | reuse keeper SustainedOutage live harness | — manual-only | ⬜ manual-only (Phase 54) |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

> D-04's core secure behavior (no error pipeline → default nack-requeue, no silent ack-discard) is
> asserted hermetically by FACT 6 (zero error-transport on the exec path). The live throw-spike (A1)
> is OPTIONAL confirmation of the runtime redelivery and is folded into Phase 54's live close gate.

---

## Wave 0 Requirements

- [x] Extend `tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs` — 5→3 reflection fact (SC-2 / RETIRE-03) — **FACT 5**
- [x] Composite-backup-key BUILDER remnant covered by reflection (FACT 1) + Model-B type-absence (FACT 2/3) — stronger than a source substring (Pitfall 2)
- [x] Add D-01 end-state standing guard — no `UseMessageRetry`/error-transport under exec-path + system-command source dirs — **FACT 6**
- [x] Add D-03 scoping fact — error filter absent from `BaseConsole.Core` global callback, keeper-local only — **FACT 7**
- [x] Add D-07 dead-guard fact — `Ignore<WorkflowRootNotFoundException>` removed from both Start/Stop definitions — **FACT 8**
- [ ] (Optional) live throw-spike reusing the keeper SustainedOutage harness — confirms A1 (deferred to Phase 54)
- [x] All reuse the existing `RepoRoot()` `[CallerFilePath]` anchor + `Directory.Exists` false-pass guard

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Poison-send redelivery (A1) produces no DLQ traffic | D-04 / A1 | No-error-pipeline nack-requeue is broker-runtime behavior | If broker available: bind throwing exec-path consumer, assert redelivery + zero `skp-dlq-1`. Otherwise confirmed in Phase 54's live close gate. Core behavior already covered hermetically by FACT 6. |

*Hermetic source-scan + reflection facts carry SC-1/SC-2/SC-3/D-01/D-03/D-07 with no broker. The live item above is OPTIONAL confirmation and otherwise falls to Phase 54's live proof.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 15s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** validated 2026-06-11

---

## Validation Audit 2026-06-11

| Metric | Count |
|--------|-------|
| Criteria audited | 8 |
| COVERED (automated, green) | 7 |
| Manual-only (optional, Phase-54) | 1 |
| MISSING | 0 |

State A audit. Cross-referenced each criterion against the implemented `ModelBContractsRetiredFacts`
class (8 facts, all `[Trait("Phase","53")]`). Ran the class live: 8/8 GREEN (429ms). Release + Debug
builds 0-warning (SC-3). No automated gaps — every requirement-bearing criterion (SC-1, SC-2 / RETIRE-03,
D-01, D-03, D-07, SC-3) has green hermetic coverage. The single manual-only item (live A1 throw-spike)
is optional and its core secure behavior is already hermetically asserted by FACT 6; it is folded into
Phase 54's live close gate. No `gsd-nyquist-auditor` spawn required; no new test files generated.
