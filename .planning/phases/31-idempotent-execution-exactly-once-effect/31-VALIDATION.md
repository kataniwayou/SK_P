---
phase: 31
slug: idempotent-execution-exactly-once-effect
status: verified
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-04
audited: 2026-06-05
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
| **Quick run command** | `dotnet test tests/BaseApi.Tests -- --filter-not-trait "Category=RealStack" --filter-not-trait "Category=E2E"` (hermetic only) — NOTE: xUnit v3 runs under Microsoft.Testing.Platform; the VSTest `--filter "Category!=..."` form is **silently ignored** (MTP0001 warning) and runs the full suite. Use the `-- --filter-not-trait` passthrough. |
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

| Req | Plan area | Wave | Requirement | Test Type | Test File / Class | File Exists | Status |
|-----|-----------|------|-------------|-----------|-------------------|-------------|--------|
| req-1 | identity/hash helper | 0/1 | Deterministic H, executionId-invariant | unit | `Contracts/HashHelperGoldenFacts.cs` (12 facts) | ✅ | ✅ green |
| req-7 | L2 key builders + RetryOptions | 0/1 | 64-hex key golden + configurable retry | unit | `Contracts/HashHelperGoldenFacts.cs` (key golden) + `Orchestrator/RetryOptionsBindFacts.cs` (3 facts) | ✅ | ✅ green |
| req-2 | WorkflowFireJob entry-step EntryId | 1 | entry EntryId=hash(corr,stepId); source via InputDefinition==null | unit+integration | `Orchestrator/FireDispatchTests.cs` | ✅ | ✅ green |
| req-3 | processor two-level write | 1 | content-addressed blobs+manifest; empty→terminal | unit+integration | `Processor/DispatchOutputWriteFacts.cs` + `Processor/EffectFirstDedupFacts.cs` | ✅ | ✅ green |
| req-4 | effect-first CAS dedup (both hops) | 1 | drop on Ack; crash-window re-produces collapsed dup | integration | `Processor/EffectFirstDedupFacts.cs` (5 facts) | ✅ | ✅ green |
| req-5 | merge correctness | 1 | distinct-output→distinct H; identical→collapse | integration | `Orchestrator/MergeCollapseFacts.cs` | ✅ | ✅ green |
| req-6 | manifest fan-out | 1 | N×M dispatch; redeliver→same H→no extra | integration | `Orchestrator/ManifestFanoutFacts.cs` | ✅ | ✅ green |
| req-8 | live exactly-once proof | 2 | merge topology + induced retry → zero downstream dup | E2E (RealStack tier) | `Orchestrator/IdempotentExactlyOnceE2ETests.cs` — `MergeTopology_InducedRedelivery_ProducesExactlyOnceDownstreamEffect` | ✅ | ✅ green (LIVE — 3×GREEN per 31-VERIFICATION.md; requires compose stack up) |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

> **Audit note (2026-06-05):** hermetic/integration tier re-run after the secure-phase log-template edit — **445 passed / 0 failed** (`-- --filter-not-trait Category=RealStack --filter-not-trait Category=E2E`). req-1…7 all green. req-8 is an automated E2E in the RealStack tier (not manual) — proven live 3×GREEN on 2026-06-04 (446/0 each); its close-gate redis net-zero clause (req-8b) was RESOLVED in Phase 31.1. A full-suite run with the compose stack DOWN shows 4 red — all in the RealStack/E2E tier (environmental, not regressions).

---

## Wave 0 Requirements

- [x] `tests/BaseApi.Tests/Contracts/HashHelperGoldenFacts.cs` — H determinism + executionId-invariance + key-builder golden + SourceHash parity (req-1, req-7) — 12/12 green
- [x] `tests/BaseApi.Tests/Processor/EffectFirstDedupFacts.cs` — CAS property (`StringSet When.Exists` called once) + crash-window collapsed-duplicate (req-4) — 5/5 green
- [x] `tests/BaseApi.Tests/Orchestrator/ManifestFanoutFacts.cs` — N×M fan-out + redeliver dedup + empty→terminal (req-3, req-6) — green
- [x] `tests/BaseApi.Tests/Orchestrator/MergeCollapseFacts.cs` — distinct-H vs collapse (req-5) — green
- [x] `tests/BaseApi.Tests/Orchestrator/RetryOptionsBindFacts.cs` — appsettings bind + attempt count (req-7 / D-10) — 3/3 green
- [x] `tests/BaseApi.Tests/Orchestrator/IdempotentExactlyOnceE2ETests.cs` — clone of `SampleRoundTripE2ETests` with merge topology + induced duplicate (req-8) — green LIVE (3×GREEN)
- [x] `phase-31-close.ps1` — clone close gate; extend scan-clean to `skp:flag:*` + 64-hex `skp:data:*` (D-12) — present (req-8b net-zero RESOLVED in Phase 31.1)
- [x] UPDATE existing `EntryId`-as-`Guid` assertions across the ~12 test files inventoried in RESEARCH § Pitfall 1, as part of the Guid→string wave-0 task — done (hermetic suite 445/0)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| None | — | All phase behaviors have automated verification (hermetic/integration/E2E tiers) | — |

*All phase behaviors have automated verification.*

---

## Validation Audit 2026-06-05

| Metric | Count |
|--------|-------|
| Requirements | 8 |
| COVERED (automated, green) | 8 |
| PARTIAL | 0 |
| MISSING | 0 |
| Gaps found | 0 |
| Resolved | 0 (none needed) |
| Escalated | 0 |

**State A audit.** All 8 SPEC requirements map to existing automated tests; the pre-execution draft's "❌ W0 (new)" / "⬜ pending" entries are now realized. Hermetic/integration tier re-run after the secure-phase log-template edit: **445 passed / 0 failed** (2m 22s). req-8 (live exactly-once) verified live 3×GREEN per 31-VERIFICATION.md; net-zero hygiene clause RESOLVED in Phase 31.1. No gaps → no auditor spawn, no test generation required. No manual-only verifications.

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (7 new/clone files + ~12 test updates) — all realized
- [x] No watch-mode flags
- [x] Feedback latency < 30s (hermetic per-class; full hermetic tier ~2.4 min)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** verified 2026-06-05
