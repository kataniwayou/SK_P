---
phase: 31
slug: idempotent-execution-exactly-once-effect
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-04
---

# Phase 31 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `31-RESEARCH.md` § Validation Architecture (Nyquist enabled).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (`tests/BaseApi.Tests`) + NSubstitute + MassTransit.Testing |
| **Config file** | none separate — project-level; RealStack/E2E gated by `[Trait("Category","RealStack")]` / `"E2E"` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "Category!=RealStack&Category!=E2E"` (hermetic only) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests` (incl. real-stack; requires the live compose stack up) |
| **Close gate** | `phase-31-close.ps1` (clone existing): 3-consecutive-GREEN full run + triple-SHA BEFORE==AFTER over `skp:data:*` + `skp:flag:*` |
| **Estimated runtime** | ~30s hermetic; real-stack E2E minutes (compose up) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/BaseApi.Tests --filter "Category!=RealStack&Category!=E2E"` (hermetic)
- **After every plan wave:** Full hermetic + integration suite
- **Before `/gsd-verify-work`:** Full suite (incl. real-stack) green + `phase-31-close.ps1` 3xGREEN + triple-SHA
- **Max feedback latency:** ~30 seconds (hermetic tier)

---

## Per-Task Verification Map

> Task IDs finalized by the planner; this maps the 8 SPEC requirements to their validation tier and representative command. The planner MUST attach an `<automated>` verify (or a Wave 0 dependency) to each task, with no 3 consecutive tasks lacking automated verification.

| Req | Plan area | Wave | Requirement | Test Type | Automated Command (representative) | File Exists | Status |
|-----|-----------|------|-------------|-----------|-------------------------------------|-------------|--------|
| req-1 | identity/hash helper | 0/1 | Deterministic H, executionId-invariant | unit | `dotnet test --filter "FullyQualifiedName~HashHelper"` | ❌ W0 (new) | ⬜ pending |
| req-7 | L2 key builders + RetryOptions | 0/1 | 64-hex key golden + configurable retry | unit | `dotnet test --filter "FullyQualifiedName~KeyBuilder\|RetryOptions"` | ❌ W0 (new) | ⬜ pending |
| req-2 | WorkflowFireJob entry-step EntryId | 1 | entry EntryId=hash(corr,stepId); source via InputDefinition==null | unit+integration | `dotnet test --filter "FullyQualifiedName~FireDispatch"` | ✅ (update) | ⬜ pending |
| req-3 | processor two-level write | 1 | content-addressed blobs+manifest; empty→terminal | unit+integration | `dotnet test --filter "FullyQualifiedName~DispatchOutputWrite"` | ✅ (update) | ⬜ pending |
| req-4 | effect-first CAS dedup (both hops) | 1 | drop on Ack; crash-window re-produces collapsed dup | integration | `dotnet test --filter "FullyQualifiedName~DedupCas"` | ❌ W0 (new) | ⬜ pending |
| req-5 | merge correctness | 1 | distinct-output→distinct H; identical→collapse | integration | `dotnet test --filter "FullyQualifiedName~Merge"` | ❌ W0 (new) | ⬜ pending |
| req-6 | manifest fan-out | 1 | N×M dispatch; redeliver→same H→no extra | integration | `dotnet test --filter "FullyQualifiedName~ManifestFanout"` | ❌ W0 (new) | ⬜ pending |
| req-8 | live exactly-once proof | 2 | merge topology + induced retry → zero downstream dup | E2E | `dotnet test --filter "Category=RealStack&FullyQualifiedName~ExactlyOnce"` | ❌ W0 (clone) | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Contracts/HashHelperGoldenFacts.cs` — H determinism + executionId-invariance + key-builder golden + SourceHash parity (req-1, req-7)
- [ ] `tests/BaseApi.Tests/Processor/EffectFirstDedupFacts.cs` — CAS property (`StringSet When.Exists` called once) + crash-window collapsed-duplicate (req-4)
- [ ] `tests/BaseApi.Tests/Orchestrator/ManifestFanoutFacts.cs` — N×M fan-out + redeliver dedup + empty→terminal (req-3, req-6)
- [ ] `tests/BaseApi.Tests/Orchestrator/MergeCollapseFacts.cs` — distinct-H vs collapse (req-5)
- [ ] `tests/BaseApi.Tests/Orchestrator/RetryOptionsBindFacts.cs` — appsettings bind + attempt count (req-7 / D-10)
- [ ] `tests/BaseApi.Tests/Orchestrator/IdempotentExactlyOnceE2ETests.cs` — clone of `SampleRoundTripE2ETests` with merge topology + induced duplicate (req-8)
- [ ] `phase-31-close.ps1` — clone close gate; extend scan-clean to `skp:flag:*` + 64-hex `skp:data:*` (D-12)
- [ ] UPDATE existing `EntryId`-as-`Guid` assertions across the ~12 test files inventoried in RESEARCH § Pitfall 1, as part of the Guid→string wave-0 task

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| None | — | All phase behaviors have automated verification (hermetic/integration/E2E tiers) | — |

*All phase behaviors have automated verification.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references (7 new/clone files + ~12 test updates)
- [ ] No watch-mode flags
- [ ] Feedback latency < 30s (hermetic)
- [ ] `nyquist_compliant: true` set in frontmatter (after planner attaches verify to every task)

**Approval:** pending
