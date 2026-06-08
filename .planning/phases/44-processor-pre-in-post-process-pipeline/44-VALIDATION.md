---
phase: 44
slug: processor-pre-in-post-process-pipeline
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-08
---

# Phase 44 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `44-RESEARCH.md` § Validation Architecture.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 (Microsoft.Testing.Platform) + NSubstitute + MassTransit.Testing (in-memory harness) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (+ `xunit.runner.json` parallelism cap) |
| **Quick run command** | `dotnet test tests\BaseApi.Tests\BaseApi.Tests.csproj --filter-not-trait Category=RealStack` |
| **Full suite command** | `dotnet test SK_P.sln --filter-not-trait Category=RealStack` |
| **Estimated runtime** | ~30s for the hermetic Processor facts subset |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests\BaseApi.Tests\BaseApi.Tests.csproj --filter-not-trait Category=RealStack` (hermetic; <30s)
- **After every plan wave:** Run `dotnet test SK_P.sln --filter-not-trait Category=RealStack` + Release 0-warning build
- **Before `/gsd-verify-work`:** Full hermetic suite GREEN + 0-warning Release build
- **Max feedback latency:** 30 seconds

> RealStack E2E (full round-trip + recovery, live RabbitMQ/Redis) is **Phase 49**, not this phase. Phase 44 proves the five terminals hermetically.

---

## Per-Task Verification Map

| Req ID | Behavior | Test Type | Automated Command (filter) | File Exists | Status |
|--------|----------|-----------|----------------------------|-------------|--------|
| PIPE-02 | `Guid.Empty` skips read (empty validatedData, no end-delete) | unit | `--filter-method *SourceStep_Skip*` | ❌ W0 | ⬜ pending |
| PIPE-02 | Redis-exception read exhausted → `KeeperReinject`, no end-delete | unit | `--filter-method *ReadFault_Reinject*` | ❌ W0 | ⬜ pending |
| PIPE-02 | absent/empty key (A2) read exhausted → `KeeperReinject` | unit | `--filter-method *AbsentKey_Reinject*` | ❌ W0 | ⬜ pending |
| PIPE-03 | input-schema validation fail → `StepFailed` + end-delete runs | unit | `--filter-method *InputInvalid_Failed*` | ❌ W0 | ⬜ pending |
| PIPE-04 | author returns N `ProcessItem` → N results | unit | `--filter-method *MultiItem*` | ❌ W0 | ⬜ pending |
| PIPE-05 | `Failed`/`Cancelled`/`Processing` exception → matching Step* record, batch aborts | unit | `--filter-method *StatusException*` | ❌ W0 | ⬜ pending |
| PIPE-05 | unexpected `Exception` ⇒ `StepFailed` | unit | `--filter-method *UnexpectedException_Failed*` | ❌ W0 | ⬜ pending |
| PIPE-06 | completed item → `KeeperUpdate`, write no-TTL, `KeeperCleanup` on success | unit | `--filter-method *PostCompleted_UpdateCleanup*` | ❌ W0 | ⬜ pending |
| PIPE-06 | output-write exhausted → item `failed(infra)` → `KeeperInject` | unit | `--filter-method *WriteFault_Inject*` | ❌ W0 | ⬜ pending |
| PIPE-07 | completed result carries entryId+executionId; infra → `KeeperInject` | unit | `--filter-method *CompletedCarriesIds*` | ❌ W0 | ⬜ pending |
| PIPE-08 | end-delete `finally` deletes on happy/business-fail/In-exception; exhaust → `KeeperDelete` | unit | `--filter-method *EndDelete*` | ❌ W0 | ⬜ pending |
| PIPE-08 | end-delete SKIPPED on REINJECT + `Guid.Empty` | unit | `--filter-method *EndDelete_Skipped*` | ❌ W0 | ⬜ pending |
| RESIL-01 | `RetryLoop` runs exactly `Limit` immediate attempts then surfaces exhaustion | unit | `--filter-method *RetryLoop_Exhausts*` | ❌ W0 | ⬜ pending |
| RESIL-01 | send through `RetryLoop`; send-exhaustion propagates (→ `_error` via bus) | unit | `--filter-method *SendExhaust_Propagates*` | ❌ W0 | ⬜ pending |
| D-09 | bus `UseMessageRetry` reads `Retry:Limit`; no double-retry of L2/send | unit/contract | `--filter-method *RetryReconcile*` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

### 5 ROADMAP Success Criteria → tests
1. **SC1** (Pre read + REINJECT + Guid.Empty skip + input-validation business-Failed) → the four PIPE-02/03 facts.
2. **SC2** (In try/catch, status → one result, abort) → the PIPE-05 status + unexpected-exception facts.
3. **SC3** (Post completed: UPDATE, write no-TTL, write-exhaust→failed(infra), CLEANUP) → PIPE-06 facts.
4. **SC4** (routing: not-infra→result carrying ids, infra→INJECT, N→N) → PIPE-07 facts.
5. **SC5** (end-delete finally, skip on REINJECT/Guid.Empty, exhaust→DELETE, shared Retry:Limit) → PIPE-08 + RESIL-01 facts.

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Processor/PipelinePreFacts.cs` — REINJECT (exception + absent/empty), Guid.Empty skip, input-validation Failed (PIPE-02/03)
- [ ] `tests/BaseApi.Tests/Processor/PipelineInFacts.cs` — status-exception mapping + unexpected⇒failed + abort (PIPE-05)
- [ ] `tests/BaseApi.Tests/Processor/PipelinePostFacts.cs` — UPDATE/write-no-TTL/CLEANUP/INJECT/routing (PIPE-06/07)
- [ ] `tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs` — finally semantics, skip paths, DELETE (PIPE-08)
- [ ] `tests/BaseApi.Tests/Processor/RetryLoopFacts.cs` — exhaustion count + outcome surfacing (RESIL-01)
- [ ] **Extend `DispatchTestKit`:** `CapturingKeeperSendProvider` (capture `IKeeperRecoverable` sends by Uri), a `FakeProcessor` overload returning `List<ProcessItem>` / throwing a `ProcessStatusException`, and a `KeyDeleteAsync`-faulting / absent-key Redis fake. (`PresentReadWriteFaultL2` already covers write-fault.)
- [ ] **Retire/rewrite** `DispatchAckSemanticsFacts.BusinessFailure_DoesNotThrow` (absent input is now REINJECT, not StepFailed) and `DispatchInputFacts` (input absence semantics changed).

*Existing infra that carries over: `DispatchTestKit` fakes, `OrchestratorTestStubs.InfraFaultL2/AbsentL2`, `FakeProcessorContext` (settable `InputDefinition`/`OutputDefinition`), `BuildResultHarness` in-memory MassTransit harness.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Full live round-trip + Keeper recovery | (cross-phase) | Requires live RabbitMQ + Redis | Deferred to Phase 49 RealStack E2E — out of scope for Phase 44 |

*All Phase-44 behaviors have automated hermetic verification; only the cross-phase live round-trip is manual/deferred.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 30s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
