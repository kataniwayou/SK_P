---
phase: 51
slug: processor-forward-recovery-pipeline
status: planned
nyquist_compliant: true
wave_0_complete: false
created: 2026-06-11
---

# Phase 51 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit + NSubstitute (.NET 8, hermetic plain-object facts) |
| **Config file** | existing test project (Phase-44 fixture: `DispatchTestKit` / `CapturingSendProvider`) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Pipeline" --nologo` |
| **Full suite command** | `dotnet build SK_P.sln -c Release --nologo; dotnet test tests/BaseApi.Tests --nologo` |
| **Estimated runtime** | ~60 seconds |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Pipeline" --nologo`
- **After every plan wave:** Run `dotnet build SK_P.sln -c Release --nologo; dotnet build SK_P.sln -c Debug --nologo; dotnet test tests/BaseApi.Tests --nologo`
- **Before `/gsd-verify-work`:** Full suite must be green AND build 0-warning (Release + Debug)
- **Max feedback latency:** 60 seconds

---

## Per-Task Verification Map

> Source-of-truth requirement→test map is in 51-RESEARCH.md `## Validation Architecture`.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 51-01-01 | 01 | 0 | SLOT-01 (config) / D-04/05/06 | T-51-01 | N/A | unit (build) | `dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj -c Debug --nologo` | ✅ | ⬜ pending |
| 51-01-02 | 01 | 0 | SLOT-01 (config) / D-04/05 | T-51-01 | options bind | unit | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~ProcessorOptionsBindingFacts" --nologo` | ❌ W0 | ⬜ pending |
| 51-02-01 | 02 | 1 | (scaffold for FWD/SLOT/INFRA) | — | test fakes | unit (build) | `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --nologo` | ❌ W0 | ⬜ pending |
| 51-02-02 | 02 | 1 | SLOT-01, SLOT-02, INFRA-01, INFRA-02, FWD-01, FWD-02, FWD-03, D-07/08/09/10 | T-51-03/04/05/06 | alloc-before-data, WR-01 retired, null fail-fast, full INJECT id-set | unit (build) | `dotnet build SK_P.sln -c Release --nologo` | ✅ (src) | ⬜ pending |
| 51-02-03 | 02 | 1 | FWD-01, FWD-02, FWD-03, SLOT-01, SLOT-02, INFRA-01, INFRA-02 | T-51-04/05/06 | REINJECT⊻delete, slot-before-data order, INJECT id-set | unit | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~PipelineForwardFacts" --nologo` | ❌ W0 | ⬜ pending |
| 51-02-04 | 02 | 1 | Open-Q1 (UseMessageRetry) | T-51-07 | dead-letter sink retained | checkpoint:decision | (human) keep-latch vs apply-none | n/a | ⬜ pending |
| 51-03-01 | 03 | 2 | SLOT-03, RECOV-01, RECOV-02, RECOV-03, D-03 | T-51-08/09/10/12 | send-before-retire, REINJECT⊻delete, fresh exec, not-exist≠fault | unit (build) | `dotnet build SK_P.sln -c Release --nologo` | ✅ (src) | ⬜ pending |
| 51-03-02 | 03 | 2 | RECOV-01, RECOV-02, RECOV-03, SLOT-03, D-03, D-09, D-10 | T-51-08/09/10/11/12 | temp-list outcomes, send-before-retire, fresh exec, null fail-fast | unit | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~PipelineRecoveryFacts\|FullyQualifiedName~EntryStepDispatchConsumerFacts" --nologo` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

**Coverage:** every one of the 11 requirement IDs (SLOT-01/02/03, INFRA-01/02, FWD-01/02/03, RECOV-01/02/03) has at least one hermetic fact (SC-5 — forward + recovery both proven). No 3 consecutive tasks lack an `<automated>` verify.

---

## Wave 0 Requirements

- [ ] `PipelineForwardFacts` — hermetic facts for the FORWARD pass (FWD-01/02/03, SLOT-01/02, INFRA-01/02) — created in plan 02 task 3
- [ ] `PipelineRecoveryFacts` — hermetic facts for the RECOVERY pass (RECOV-01/02/03, SLOT-03, D-03) — created in plan 03 task 2
- [ ] `EntryStepDispatchConsumerFacts` — D-10 null-MessageId fact — created in plan 03 task 2
- [ ] `DispatchTestKit` HASH-fake extensions — `KeyExistsAsync` / `HashSetAsync` / `KeyExpireAsync` / `HashGetAllAsync` fakes + `SlotOptions` + `messageId` plumbing — plan 02 task 1 (forward) + plan 03 task 2 (recovery)
- [ ] `SlotArrayOptions` bind facts — plan 01 task 2
- [ ] Adapt the four existing `Pipeline{Pre,In,Post,EndDelete}Facts` to the `RunAsync(d, messageId, ct)` signature + the new ctor arg + the removed `finally` — plan 02 task 1

*Existing Phase-44 plain-object fixture (`DispatchTestKit`, `CapturingSendProvider`) covers the pipeline construction seam; Wave 0 extends it for the slot-array HASH surface. NOTE: Wave 0 here is interleaved — plan 01 is the standalone Wave-0 options scaffold; the test-fixture scaffolding (DispatchTestKit HASH fakes + the new fact files) is co-located with the pipeline rewrite (plans 02/03) because the ctor/signature change forces the fixture and the facts to move with the production code in the same file-ownership wave.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live forward + recovery round-trip proof | TEST-01/02 | Requires running Redis + bus (deferred) | Out of scope — Phase 54 (live proof + close gate) |
| UseMessageRetry latch-vs-none decision | Open-Q1 | Architectural choice requiring user confirmation | `checkpoint:decision` in plan 02 task 4 (keep-latch recommended/[ASSUMED]) |

*Hermetic facts cover all Phase-51 forward/recovery routing branches; live proof is intentionally scoped OUT to Phase 54.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** planned (pending execution)
