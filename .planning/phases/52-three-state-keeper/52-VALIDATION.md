---
phase: 52
slug: three-state-keeper
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-11
---

# Phase 52 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from 52-RESEARCH.md §Validation Architecture.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (project-pinned) + NSubstitute + MassTransit.Testing `ITestHarness` |
| **Config file** | none (xUnit auto-discovery); facts under `tests/BaseApi.Tests/Keeper/` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper"` |
| **Full suite command** | `dotnet test` (must be 0-warning) |
| **Estimated runtime** | ~30–60 seconds (keeper-scoped quick run) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper"`
- **After every plan wave:** Run `dotnet test` (Debug)
- **Before `/gsd-verify-work`:** Full suite green at Release + Debug, **0 warnings**
- **Max feedback latency:** ~60 seconds (quick keeper-scoped run)

---

## Per-Task Verification Map

| Requirement | Behavior | Test Type | Automated Command | File Exists | Status |
|-------------|----------|-----------|-------------------|-------------|--------|
| KEEP-01 | REINJECT present → sends `EntryStepDispatch` w/ `Payload` to `queue:{proc}` | unit (consumer + CapturingSendProvider) | `dotnet test --filter "FullyQualifiedName~ReinjectConsumerFacts"` | ✅ keep present-path fact | ⬜ pending |
| KEEP-01 | REINJECT absent/empty → silent drop + counter, NO send, NO throw (D-06/D-07) | unit | `dotnet test --filter "FullyQualifiedName~ReinjectConsumerFacts"` | ❌ W0 — rewrite old "throws" fact | ⬜ pending |
| KEEP-02 | INJECT → write `L2[entryId]` → send `StepCompleted` → delete `L2[deleteEntryId]` **in order** | unit (FakeRedis / `Received()` ordering) | `dotnet test --filter "FullyQualifiedName~InjectConsumerFacts"` | ❌ W0 — new file | ⬜ pending |
| KEEP-03 | DELETE deletes key; absent → no-op | unit | `dotnet test --filter "FullyQualifiedName~DeleteConsumerFacts"` | ✅ exists — add absent-key drop assertion | ⬜ pending |
| KEEP-04 | Gate-closed → endpoint stopped → message NOT consumed (accumulates); gate-open → consumed/drained | integration (`ITestHarness` Stop/Start + Consumed assertions) | `dotnet test --filter "FullyQualifiedName~KeeperPauseAccumulate"` | ❌ W0 — new fact | ⬜ pending |
| KEEP-04 | `BitHealthLoop` drives Stop on unhealthy edge, Start on healthy edge | unit (fake endpoint handle) | `dotnet test --filter "FullyQualifiedName~BitHealthLoop"` | ✅ exists — extend with Stop/Start call-count asserts | ⬜ pending |
| KEEP-05 | Dlq1 mode: exhausted op → routes to `skp-dlq-1` (ConsolidatedFault) | integration (`ITestHarness`) | `dotnet test --filter "FullyQualifiedName~RecoveryDeadLetter"` | ✅ exists — adapt to op-exhaustion (not data-gone) | ⬜ pending |
| KEEP-05 | SustainedOutage mode: exhausted op → requeue/hold, NO dead-letter | integration (`ITestHarness`) | `dotnet test --filter "FullyQualifiedName~SustainedOutage"` | ❌ W0 — new fact | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` — KEEP-02 write→send→delete ordering
- [ ] Extend `ReinjectConsumerFacts.cs` — absent-path now DROP + counter (KEEP-01 / D-06 / D-07); delete the old "throws" assertion
- [ ] New pause/accumulate integration fact (`KeeperPauseAccumulate...`) — KEEP-04 (endpoint Stop → no consume → Start → drain)
- [ ] Extend `BitHealthLoopTests.cs` with a fake endpoint handle — KEEP-04 driver (Stop on unhealthy edge, Start on healthy)
- [ ] New SustainedOutage fact — KEEP-05 hold/requeue mode (assert NO `ConsolidatedFault`, message redelivered)
- [ ] Adapt `RecoveryDeadLetterFacts.cs` — repurpose data-gone→dead-letter to op-exhaustion→dead-letter (Dlq1), since data-gone is now a drop (D-06)
- [ ] (optional) `KeeperMetrics` fact — confirm `keeper_reinject_dropped` increments (mirrors `ProcessorMetricsFacts`)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live `skp-dlq-1` broker queue + TTL, real partitioner serialization | KEEP-05 | Hermetic in-memory harness proves routing/fault/drop **shape**, not broker-literal queues | Deferred to Phase 54 TEST-01 (RealStack live triple-SHA E2E) |

---

## Validation Sign-Off

- [ ] All tasks have automated verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
