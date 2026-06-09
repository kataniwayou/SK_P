# Phase 47: DLQ Consolidation + At-Least-Once Semantics - Context

**Gathered:** 2026-06-09
**Status:** Ready for planning

<domain>
## Phase Boundary

A **verify-and-document** phase: prove — via hermetic tests plus a traceability audit — that the v4 execution path **already** satisfies single-DLQ consolidation (RESIL-02: every processor and Keeper terminal give-up lands in the one `skp-dlq-1`) and at-least-once semantics (RESIL-03: no dedup/idempotency key; duplicates reproduce their effect, no lost branch, no crash). Most of the behavior shipped in Phase 36 (`ConsolidatedErrorTransportFilter` → `skp-dlq-1`) and Phase 43 (RETIRE-01 removed `H`/`flag[H]`/`MessageIdentity`); this phase closes the **proof + documentation** gap, not an implementation gap. No production behavior change, no queue rename, no teardown.

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**5 requirements are locked.** See `47-SPEC.md` for full requirements, boundaries, and acceptance criteria.

Downstream agents MUST read `47-SPEC.md` before planning or implementing. Requirements are not duplicated here.

**In scope (from SPEC.md):**
- A traceability audit doc mapping RESIL-02 / RESIL-03 / roadmap SC-1–3 → proving tests (file + method).
- New/gap-fill hermetic tests: processor send-exhaustion → `skp-dlq-1`; REINJECT-data-gone → `skp-dlq-1` (reuse/extend `RecoveryDeadLetterFacts`); duplicate-delivery (no-collapse) for `StepCompleted` and `EntryStepDispatch`; structural no-dedup assertion; structural "no v4 path targets `keeper-dlq`" assertion.
- A documented at-least-once guarantee statement.

**Out of scope (from SPEC.md):**
- Renaming `skp-dlq-1` → `_DLQ1` — `skp-dlq-1` IS the canonical single DLQ; `_DLQ1` is roadmap shorthand. No rename.
- Removing `keeper-dlq` / the reactive `Fault<T>` recovery path — **Phase 48** (RETIRE-03).
- Any production code change to the consolidation or dedup machinery — already shipped; a real gap surfaces as a finding + minimal fix, not a re-architecture.
- Live / real-stack proof — **Phase 49** (TEST-01..03); Phase 47 is hermetic only.

</spec_lock>

<decisions>
## Implementation Decisions

### Audit artifact (D-01)
- **D-01:** **Produce a dedicated `47-DLQ-AUDIT.md` in the phase directory.** A standalone traceability ledger with one row per criterion mapping RESIL-02, RESIL-03, and roadmap SC-1/SC-2/SC-3 → its named proving test (file + method). Keeps the DLQ/at-least-once audit separate from `47-VALIDATION.md` (Nyquist coverage) and from the design doc (a spec, not a test ledger). This is the phase's primary human-readable deliverable; the verifier checks every row resolves to a real, green test.

### At-least-once guarantee statement (D-02)
- **D-02:** **Amend the locked design doc `docs/design/2026-06-08-processor-keeper-recovery-redesign.md`** with the at-least-once / no-dedup guarantee statement (the v4 execution path is at-least-once, carries no dedup/idempotency key, and tolerates duplicate effects downstream by construction). The design doc is the source of truth; this matches the Phase-46 design-doc-amendment pattern. **Bundle this amendment with the still-pending Phase-46 `Payload`-on-`KeeperReinject` amendment** (already noted as user-owned in 46-CONTEXT `<deferred>`) if convenient — one doc edit closes both.

### Structural guards (D-03)
- **D-03:** **Reflection + source-scan, right tool per check.**
  - **No-dedup type guard** → reflection over the Orchestrator + BaseProcessor.Core (execution-path) assemblies asserting no `MessageIdentity` type and no `flag[H]`/dedup-key member survives — mirroring the existing firewall-test reflection pattern (`KeeperDependencyFirewallTests`, `ConsoleDependencyFirewallTests`).
  - **No-keeper-dlq guard** → a source-file scan scoped to `src/BaseProcessor.Core/Processing/` and `src/Keeper/Recovery/` asserting neither directory references `KeeperQueues.DeadLetter` / `"keeper-dlq"` (only the dormant reactive `KeeperRecoveryHandler` may, until Phase 48). Source-scan is the natural fit for a directory-scoped "must not reference" assertion.

### Test placement & seams (D-04)
- **D-04:** **Extend existing test files + reuse kits; structural guards as their own small file.**
  - Extend `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` with a **processor send-exhaustion → `skp-dlq-1`** case (its in-memory `ConfigureError` harness is the proven template).
  - Extend `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs` to pin **REINJECT-data-gone → `skp-dlq-1`** (the `RecoveryDataGoneException` propagating to the consolidated transport, not a loop).
  - Extend `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs` with a **duplicate-delivery** fact: deliver the same `StepCompleted` twice (double-`Consume`) and assert the effect occurs twice (no collapse), no throw, no lost branch; mirror for `EntryStepDispatch` via the recovery/dispatch kit.
  - Reuse `RecoveryTestKit` / `DispatchTestKit` for substituted `IDatabase`/`ISendEndpoint`.
  - Put the **structural guards** (D-03) in their own new small file (e.g. `tests/BaseApi.Tests/.../AtLeastOnceStructuralFacts.cs`) carrying a `[Trait("Phase","47")]`.

### Claude's Discretion
- Exact `47-DLQ-AUDIT.md` table columns/layout (follow the VALIDATION.md traceability-table style).
- The structural-guard test file's namespace/name and which assemblies the reflection guard loads (keep parity with the existing firewall tests).
- The precise wording of the design-doc at-least-once amendment.
- Whether the processor send-exhaustion case is a new `[Fact]` in `KeeperDlqConsolidationTests` or a sibling fact — keep it in the same harness rig.
- Whether any of the 5 SCs is already fully proven by an existing test (then the audit row simply references it — no new test needed; only genuine gaps get new assertions).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements & design (source of truth)
- `.planning/phases/47-dlq-consolidation-at-least-once-semantics/47-SPEC.md` — **Locked requirements — MUST read before planning.** The 5 requirements, boundaries, and pass/fail acceptance criteria.
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` — locked design; §"Locked decisions" A4 (single `_DLQ1`) and the at-least-once/no-dedup model. **To be amended (D-02)** with the at-least-once guarantee statement.

### Requirements & roadmap
- `.planning/REQUIREMENTS.md` — **RESIL-02** (line 60: single consolidated `_DLQ1`), **RESIL-03** (line 61: at-least-once, no dedup, duplicates tolerated).
- `.planning/ROADMAP.md` §"Phase 47: DLQ Consolidation + At-Least-Once Semantics" — the 3 success criteria are the verification target.

### Existing code this phase verifies (read before planning)
- `src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs` — the consolidation: `const Dlq1 = "skp-dlq-1"`, the `ConsolidatedFault` envelope. ALL transport-exhaustion already routes here.
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` — `ConfigureError` (GenerateFaultFilter + ConsolidatedErrorTransportFilter) wired once in the per-endpoint `AddConfigureEndpointsCallback`. No per-consumer error config anywhere.
- `src/Messaging.Contracts/KeeperQueues.cs` — `DeadLetter = "keeper-dlq"` (DLQ-2, no TTL); the const the v4 paths must NOT reference (only the reactive path may).
- `src/Keeper/Recovery/KeeperRecoveryHandler.cs` — lines 136, 173: the ONLY `keeper-dlq` sender; the dormant reactive `Fault<T>` path (Phase 48 retires it). The structural no-keeper-dlq guard must scope AROUND this file (it's allowed here).
- `src/Keeper/Recovery/*` + `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — the v4 give-up paths (throw → inherited `skp-dlq-1`); the dirs the no-keeper-dlq source-scan covers.

### Test templates to reuse/extend
- `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` — in-memory `ConfigureError` harness proving exhaustion → `skp-dlq-1` as `ConsolidatedFault`; the template for the processor send-exhaustion case (D-04).
- `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs` — extend for REINJECT-data-gone → `skp-dlq-1` (D-04).
- `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs` — extend for duplicate-delivery / no-collapse (D-04).
- `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs`, `tests/BaseApi.Tests/Console/ConsoleDependencyFirewallTests.cs` — the reflection-based structural-guard pattern to mirror (D-03).
- `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs` (+ `DispatchTestKit`) — substituted `IDatabase`/`ISendEndpoint`/fake gate kits to reuse.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`KeeperDlqConsolidationTests` harness rig** — clones `KeeperProbeLoopTests`' in-memory `ITestHarness` builder + the `ConfigureError` pipeline; directly extensible with a processor send-exhaustion case.
- **`RecoveryDeadLetterFacts` / `RecoveryTestKit`** — Phase-46 Keeper give-up + data-gone facts and the substituted-`IDatabase`/`ISendEndpoint` kit.
- **Firewall reflection tests** (`KeeperDependencyFirewallTests`, `ConsoleDependencyFirewallTests`, `StepResultContractTests`) — the assembly-reflection structural-assertion idiom.
- **`ConsolidatedErrorTransportFilter.Dlq1` const** — the single-source `skp-dlq-1` name to assert against (never hard-code the literal in new tests; reference the const).

### Established Patterns
- **Single consolidated error transport** — one `ConfigureError` in `BaseConsole.Core`; per-consumer error config is the anti-pattern (already absent across all consoles).
- **Topology split by mechanism** — `skp-dlq-1` (DLQ-1, TTL'd transport-exhaustion sink) vs `keeper-dlq` (DLQ-2, no-TTL probe give-up, reactive-path-only).
- **Hermetic harness + deferred live proof** — `KeeperDlqConsolidationTests` proves consolidation in-memory; the live broker proof (TTL applied, message in the real queue) is the Phase-49 close gate.
- **Traceability docs** — `VALIDATION.md` precedent for SC→test tables; `47-DLQ-AUDIT.md` follows the same shape.

### Integration Points
- New tests live in `tests/BaseApi.Tests/` (Microsoft.Testing.Platform runner — `dotnet test --filter` is ignored; scope via `dotnet run --project tests/BaseApi.Tests -- --filter-trait/--filter-method`). Tag new Phase-47 facts `[Trait("Phase","47")]`.
- The at-least-once amendment edits `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` (the only production-adjacent write; no source code change).

</code_context>

<specifics>
## Specific Ideas

- The phase may need **zero production code** — it is verification + documentation. Any production touch is permitted ONLY as an explicitly-surfaced minimal gap-fix if a test reveals a real defect (SPEC constraint).
- Bundle the D-02 at-least-once design-doc amendment with the **pending Phase-46 `Payload`-on-`KeeperReinject` amendment** (46-CONTEXT `<deferred>`) — one design-doc edit closes both user-owned amendments.
- Reference `ConsolidatedErrorTransportFilter.Dlq1` (not the `"skp-dlq-1"` literal) in assertions so a future rename can't silently desync the tests.

</specifics>

<deferred>
## Deferred Ideas

- **Removing `keeper-dlq` + the reactive `Fault<T>` recovery path** (`KeeperRecoveryHandler`, `FaultEntryStepDispatchConsumer`) — **Phase 48** (RETIRE-03); `keeper-dlq`'s only sender is that dormant path.
- **Literal rename `skp-dlq-1` → `_DLQ1`** — explicitly out of scope (SPEC): `skp-dlq-1` IS the canonical single DLQ; the rename would churn `BaseConsole.Core` + all consoles + close-gate SHA snapshots for no behavioral gain. Revisit only if a literal `_DLQ1` queue name is later required.
- **Live / real-stack DLQ + at-least-once proof** — **Phase 49** (TEST-01..03); Phase 47 is hermetic only.

</deferred>

---

*Phase: 47-dlq-consolidation-at-least-once-semantics*
*Context gathered: 2026-06-09*
