# Phase 47: DLQ Consolidation + At-Least-Once Semantics — Specification

**Created:** 2026-06-09
**Ambiguity score:** 0.13 (gate: ≤ 0.20)
**Requirements:** 5 locked

## Goal

Prove — via automated tests plus a traceability audit — that the v4 execution path **already** satisfies single-DLQ consolidation (every processor and Keeper terminal give-up lands in the one `skp-dlq-1`) and at-least-once semantics (no dedup/idempotency key; duplicates reproduce their effect, no lost branch, no crash); fill any coverage gap with new hermetic tests. No production behavior change, no queue rename, no teardown.

## Background

Most of this phase's stated scope already shipped in earlier phases — this phase verifies and documents it rather than building it:

- **`skp-dlq-1` is already the single consolidated DLQ.** `BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs` (`const Dlq1 = "skp-dlq-1"`) is installed once via `ConfigureError` in `MessagingServiceCollectionExtensions.cs`'s once-per-endpoint `AddConfigureEndpointsCallback`, replacing the per-`{queue}_error` default across all consoles. Every `Immediate(N)` transport exhaustion — including Phase-46's recovery throws and the processor's send-exhaustion — already routes here.
- **At-least-once / no-dedup is already true.** `H` / `flag[H]` / CAS / `MessageIdentity` were removed in Phase 43 (RETIRE-01, compile-forced). The execution path carries no dedup key today.
- **REINJECT-data-gone already terminates at `skp-dlq-1`.** Phase 46's `ReinjectConsumer` throws the deliberate `RecoveryDataGoneException` (absent/empty L2 read) which propagates to the consolidated error transport; `RecoveryDeadLetterFacts` exercises it.
- **The separate `keeper-dlq` (`KeeperQueues.DeadLetter`) is sent to by exactly one thing:** `KeeperRecoveryHandler.cs` (lines 136, 173) — the **dormant reactive `Fault<T>` recovery path** (`FaultEntryStepDispatchConsumer`, still registered additively). The v4 path (processor pipeline + Phase-46 recovery consumers) never references `keeper-dlq`. Removing `keeper-dlq` and the reactive path is **Phase 48** (RETIRE-03).

The gap this phase closes is **proof**, not implementation: there is no single test set or audit that asserts the consolidation + at-least-once invariants hold across the v4 path, and at least the processor send-exhaustion→`skp-dlq-1` route and an explicit duplicate-delivery (no-collapse) behavior may lack a dedicated assertion.

## Requirements

1. **Single-DLQ consolidation verified (RESIL-02)**: Every v4 terminal give-up routes to the one `skp-dlq-1`, and no v4 give-up path targets a second DLQ.
   - Current: `skp-dlq-1` consolidation is wired in `BaseConsole.Core` and exercised piecemeal (Keeper give-up via `RecoveryDeadLetterFacts`), but no test asserts the processor send-exhaustion route, and there is no single structural assertion that the v4 path never targets `keeper-dlq`.
   - Target: a harness test proves processor send-exhaustion routes the faulted message to `skp-dlq-1`; a structural test asserts that no v4 give-up path (processor pipeline, Keeper recovery consumers) references `KeeperQueues.DeadLetter` (only the dormant reactive `KeeperRecoveryHandler` may).
   - Acceptance: `dotnet run --project tests/BaseApi.Tests -- --filter-method "*Dlq1*"` (or the phase trait) passes a processor-send-exhaustion→`skp-dlq-1` fact; a structural fact fails if any file under `src/BaseProcessor.Core/Processing/` or `src/Keeper/Recovery/` references `KeeperQueues.DeadLetter`/`"keeper-dlq"`.

2. **REINJECT-data-gone terminal verified (RESIL-02 / roadmap SC-3)**: The data-gone case terminates deterministically at `skp-dlq-1` rather than looping.
   - Current: Phase-46 `RecoveryDeadLetterFacts` asserts `RecoveryDataGoneException` is thrown on absent/empty read; no test pins that this exception routes to `skp-dlq-1` specifically (vs being swallowed/looped).
   - Target: a test asserts the data-gone marker propagates to the consolidated error transport (`skp-dlq-1`) — reusing/extending `RecoveryDeadLetterFacts`.
   - Acceptance: a fact asserts a REINJECT with an absent/empty `L2[entryId]` results in the message reaching `skp-dlq-1` (harness) or the `RecoveryDataGoneException` propagating past the consumer (not caught) — pass/fail.

3. **At-least-once duplicate-delivery verified (RESIL-03)**: The same message delivered twice reproduces its effect — no collapse, no lost branch, no crash.
   - Current: `TypedResultConsumerFacts` proves indistinguishability of an INJECT'd vs direct completion but does not explicitly deliver the SAME message twice and assert both effects occur.
   - Target: a harness test delivers the same `StepCompleted` (and an `EntryStepDispatch`) twice and asserts BOTH reproduce their effect (advancement / processing happens twice), with no thrown exception and no dropped branch.
   - Acceptance: a duplicate-delivery fact asserts effect-count == 2 for two identical deliveries (no dedup collapse to 1), and the consumer does not throw — pass/fail.

4. **Structural no-dedup assertion (RESIL-03)**: No dedup/idempotency machinery exists on the execution path.
   - Current: H/flag[H]/MessageIdentity were removed in Phase 43, but no standing test guards against their reintroduction.
   - Target: a structural/reflection test asserts no execution-path type references `H`, `flag[H]`, `MessageIdentity`, or a dedup key.
   - Acceptance: a structural fact fails if `MessageIdentity` exists as a type, or if `"flag["` / a dedup-key pattern appears in the orchestrator/processor execution-path source — pass/fail.

5. **Traceability audit artifact**: A single document maps each requirement and roadmap success criterion to its proving test.
   - Current: no consolidated audit exists; proof is scattered across phase test files.
   - Target: an audit doc (in the phase dir) maps RESIL-02, RESIL-03, and roadmap SC-1/SC-2/SC-3 each to ≥1 named proving test (file + method), and records the documented at-least-once guarantee statement (or links to where it lives).
   - Acceptance: the audit doc exists, every RESIL id and roadmap SC has ≥1 named test reference, and no row is left "unproven".

## Boundaries

**In scope:**
- A traceability audit doc mapping RESIL-02 / RESIL-03 / roadmap SC-1–3 → proving tests (file + method).
- New/gap-fill hermetic tests: processor send-exhaustion → `skp-dlq-1`; REINJECT-data-gone → `skp-dlq-1` (reuse/extend `RecoveryDeadLetterFacts`); duplicate-delivery (no-collapse) for `StepCompleted` and `EntryStepDispatch`; structural no-dedup assertion; structural "no v4 path targets `keeper-dlq`" assertion.
- A documented at-least-once guarantee statement (design-doc section or equivalent) recording that the v4 execution path is at-least-once with no dedup and duplicates are tolerated downstream.

**Out of scope:**
- Renaming `skp-dlq-1` → `_DLQ1` — `skp-dlq-1` IS the canonical single DLQ; `_DLQ1` is the roadmap's shorthand for it. No rename (would churn `BaseConsole.Core`, all consoles, and close-gate SHA snapshots for no behavioral gain).
- Removing `keeper-dlq` / the reactive `Fault<T>` recovery path (`KeeperRecoveryHandler`, `FaultEntryStepDispatchConsumer`) — **Phase 48** (RETIRE-03); `keeper-dlq`'s only sender is that dormant path.
- Any production code change to the consolidation or dedup machinery — already shipped (Phase 36 `ConsolidatedErrorTransportFilter`; Phase 43 RETIRE-01). If verification uncovers a genuine gap, it is surfaced as a finding (and a minimal gap-fix), not a silent re-architecture.
- Live / real-stack proof of the DLQ + at-least-once behavior — **Phase 49** (TEST-01..03); Phase 47 is hermetic only.

## Constraints

- **Hermetic only:** unit + MassTransit `InMemoryTestHarness` (xunit.v3 3.2.2 + NSubstitute 5.3.0); no live Redis/RabbitMQ (that is Phase 49). A bare full run still shows the 2 pre-existing broker-dependent E2E failures — those are not Phase-47 regressions.
- **Test runner:** Microsoft.Testing.Platform — `dotnet test --filter` is ignored; scope with `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait/--filter-method`.
- **Verify-only:** no production code change is expected for RESIL-02/03. The only production touch permitted is a minimal gap-fix if a verification reveals a real defect, which must be surfaced explicitly (not folded silently into "tests").
- `dotnet build SK_P.sln` must remain 0 warnings / 0 errors.

## Acceptance Criteria

- [ ] A harness test asserts processor send-exhaustion routes the faulted message to `skp-dlq-1`.
- [ ] A test asserts REINJECT-data-gone (`RecoveryDataGoneException`) terminates at `skp-dlq-1`, not a loop.
- [ ] A duplicate-delivery test asserts the same `StepCompleted` (and `EntryStepDispatch`) delivered twice reproduces its effect twice, with no exception and no lost branch.
- [ ] A structural test fails if any execution-path type references `H` / `flag[H]` / `MessageIdentity` / a dedup key.
- [ ] A structural test fails if any v4 give-up path (`src/BaseProcessor.Core/Processing/`, `src/Keeper/Recovery/`) references `KeeperQueues.DeadLetter` / `"keeper-dlq"`.
- [ ] An audit doc maps RESIL-02, RESIL-03, and roadmap SC-1/SC-2/SC-3 each to ≥1 named proving test (file + method), with no unproven row.
- [ ] The at-least-once guarantee is recorded in a documented statement.
- [ ] `dotnet build SK_P.sln` is 0/0 and all new Phase-47 tests are green; no production code change beyond an explicitly-surfaced gap-fix (if any).

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                        |
|--------------------|-------|------|--------|--------------------------------------------------------------|
| Goal Clarity       | 0.90  | 0.75 | ✓      | Verify+document two invariants; no prod change, no rename    |
| Boundary Clarity   | 0.90  | 0.70 | ✓      | 47/48 boundary locked (keeper-dlq + reactive path → 48)      |
| Constraint Clarity | 0.80  | 0.65 | ✓      | Hermetic only; live proof → Phase 49                         |
| Acceptance Criteria| 0.85  | 0.70 | ✓      | 8 pass/fail checks incl. structural + duplicate-delivery     |
| **Ambiguity**      | 0.13  | ≤0.20| ✓      |                                                              |

Status: ✓ = met minimum

## Interview Log

| Round | Perspective           | Question summary                                  | Decision locked                                                                 |
|-------|-----------------------|--------------------------------------------------|---------------------------------------------------------------------------------|
| 1     | Researcher            | RESIL-02 primary deliverable, given skp-dlq-1 exists | Verify-only consolidation — skp-dlq-1 IS the single _DLQ1, no rename             |
| 1     | Researcher            | keeper-dlq removal: Phase 47 or 48?              | Defer to Phase 48 (its only user is the dormant reactive path); 47 doesn't touch it |
| 1     | Researcher            | RESIL-03 build vs verify-only?                   | Verify-only — H/flag[H]/CAS already removed in Phase 43; no prod change          |
| 2     | Boundary Keeper       | Concrete test/artifact deliverable?             | Audit doc + reuse Phase-46 tests + fill gaps (send-exhaustion, duplicate-delivery) |
| 2     | Seed Closer           | RESIL-03 falsifiable proof shape?                | Duplicate-delivery harness test (no-collapse) + structural no-dedup assertion    |

---

*Phase: 47-dlq-consolidation-at-least-once-semantics*
*Spec created: 2026-06-09*
*Next step: /gsd-discuss-phase 47 — implementation decisions (test seams, audit-doc format, where the at-least-once statement lives)*
