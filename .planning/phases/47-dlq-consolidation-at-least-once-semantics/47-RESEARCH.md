# Phase 47: DLQ Consolidation + At-Least-Once Semantics — Research

**Researched:** 2026-06-09
**Domain:** .NET 8 / MassTransit 8.5.5 hermetic test verification + traceability auditing (xunit.v3 + NSubstitute + InMemoryTestHarness via Microsoft.Testing.Platform)
**Confidence:** HIGH (all claims verified against source files in this repo via Read/Grep this session)

## Summary

Phase 47 is a **verify-and-document** phase, not a build phase. Reading the existing tests and source confirms the SPEC's premise: nearly all behavior shipped in Phase 36 (`ConsolidatedErrorTransportFilter` → `skp-dlq-1`) and Phase 43 (RETIRE-01 removed `H`/`flag[H]`/`MessageIdentity` from the *execution path*). The deliverable is a `47-DLQ-AUDIT.md` traceability ledger + a small number of gap-fill hermetic tests + a one-paragraph design-doc amendment.

The coverage map below shows that **R2 (REINJECT-data-gone → skp-dlq-1) is already FULLY PROVEN** by `RecoveryDeadLetterFacts.DataGone_reinject_faults_and_routes_to_dead_letter` — no new test needed, only an audit row + a re-tag. The **R1 generic exhaustion → skp-dlq-1** route is already proven generically by `KeeperDlqConsolidationTests` (the `AlwaysFaultsConsumer` throw → Immediate(N) → `ConsolidatedFault` on `Dlq1`). The genuine gaps are: (a) an explicit processor-send-exhaustion framing (likely a thin new fact reusing the same harness, since the existing AlwaysFaults case already proves the *mechanism*), (b) the **duplicate-delivery / no-collapse** fact (R3 — `TypedResultConsumerFacts` proves indistinguishability but never delivers the *same* message twice), (c) the **structural no-dedup reflection guard** (R4 — no standing guard exists), and (d) the **structural no-keeper-dlq source-scan** (R1 structural — no guard exists).

**Primary recommendation:** Write only 4 small things — a processor-send-exhaustion fact (sibling in `KeeperDlqConsolidationTests`), a duplicate-delivery fact (extend `TypedResultConsumerFacts`), one new `AtLeastOnceStructuralFacts.cs` file (reflection guard + source-scan guard), and `47-DLQ-AUDIT.md`. Re-tag the existing `RecoveryDeadLetterFacts` fact `[Trait("Phase","47")]` (or add a 47 trait alongside 46) and cite it for R2. Amend the design doc with a one-paragraph at-least-once guarantee statement (bundle the pending Phase-46 `Payload`-on-`KeeperReinject` note). **The single biggest risk is a self-inflicted false-positive in the structural guards** — see Common Pitfalls.

## User Constraints (from CONTEXT.md)

### Locked Decisions

- **D-01 (audit artifact):** Produce a dedicated `47-DLQ-AUDIT.md` in the phase directory — a standalone traceability ledger, one row per criterion mapping RESIL-02, RESIL-03, and roadmap SC-1/SC-2/SC-3 → its named proving test (file + method). Separate from `47-VALIDATION.md` and the design doc. Verifier checks every row resolves to a real, green test.
- **D-02 (at-least-once statement):** Amend the locked design doc `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` with the at-least-once / no-dedup guarantee statement (v4 path is at-least-once, carries no dedup/idempotency key, tolerates duplicate effects downstream by construction). Design doc is source of truth. **Bundle with the pending Phase-46 `Payload`-on-`KeeperReinject` amendment** if convenient — one edit closes both.
- **D-03 (structural guards):** Reflection + source-scan, right tool per check.
  - **No-dedup type guard** → reflection over Orchestrator + BaseProcessor.Core (execution-path) assemblies asserting no `MessageIdentity` type and no `flag[H]`/dedup-key member survives — mirroring `KeeperDependencyFirewallTests` / `ConsoleDependencyFirewallTests`.
  - **No-keeper-dlq guard** → source-file scan scoped to `src/BaseProcessor.Core/Processing/` and `src/Keeper/Recovery/` asserting neither references `KeeperQueues.DeadLetter` / `"keeper-dlq"` (only the dormant `KeeperRecoveryHandler` may, until Phase 48).
- **D-04 (test placement & seams):** Extend existing test files + reuse kits; structural guards as their own small file.
  - Extend `KeeperDlqConsolidationTests.cs` with a processor send-exhaustion → `skp-dlq-1` case (same in-memory `ConfigureError` harness rig).
  - Extend `RecoveryDeadLetterFacts.cs` to pin REINJECT-data-gone → `skp-dlq-1`.
  - Extend `TypedResultConsumerFacts.cs` with a duplicate-delivery fact (double-`Consume`, effect twice, no throw, no lost branch); mirror for `EntryStepDispatch`.
  - Reuse `RecoveryTestKit` / `DispatchTestKit` for substituted `IDatabase`/`ISendEndpoint`.
  - Structural guards (D-03) in their own new small file (e.g. `AtLeastOnceStructuralFacts.cs`) carrying `[Trait("Phase","47")]`.

### Claude's Discretion

- Exact `47-DLQ-AUDIT.md` table columns/layout (follow VALIDATION.md traceability style).
- Structural-guard file namespace/name; which assemblies the reflection guard loads (parity with firewall tests).
- Precise wording of the design-doc at-least-once amendment.
- Whether the processor-send-exhaustion case is a new `[Fact]` in `KeeperDlqConsolidationTests` or a sibling — keep it in the same harness rig.
- **Whether any of the 5 SCs is already fully proven by an existing test** (then the audit row simply references it — no new test needed; only genuine gaps get new assertions).

### Deferred Ideas (OUT OF SCOPE)

- Removing `keeper-dlq` + the reactive `Fault<T>` recovery path (`KeeperRecoveryHandler`, `FaultEntryStepDispatchConsumer`) — **Phase 48** (RETIRE-03).
- Literal rename `skp-dlq-1` → `_DLQ1` — `skp-dlq-1` IS the canonical single DLQ; `_DLQ1` is roadmap shorthand. No rename.
- Live / real-stack DLQ + at-least-once proof — **Phase 49** (TEST-01..03); Phase 47 is hermetic only.

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| RESIL-02 | Processor and Keeper terminal give-ups route to a single consolidated `_DLQ1` (= `skp-dlq-1`) | `ConsolidatedErrorTransportFilter.Dlq1` const = `"skp-dlq-1"`, wired once via `ConfigureError` in `MessagingServiceCollectionExtensions`. Proven generically by `KeeperDlqConsolidationTests`; per-path by `RecoveryDeadLetterFacts`. Gaps: explicit processor-send-exhaustion framing + structural no-`keeper-dlq` guard. |
| RESIL-03 | Execution path is at-least-once with no dedup/idempotency key; duplicates tolerated | `H`/`flag[H]`/`MessageIdentity` removed from execution path in Phase 43 (verified by grep: only retired-context comments + the BIT-gate `string H` pause-key survive). Gaps: duplicate-delivery no-collapse fact + structural no-dedup reflection guard. |

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Consolidated error transport (`skp-dlq-1`) | BaseConsole.Core messaging middleware | — | `ConfigureError` filter installed once per endpoint; ALL consoles inherit. Not consumer-level. |
| Processor terminal give-up (send-exhaustion) | BaseProcessor.Core (`ProcessorPipeline`) | BaseConsole.Core (error transport) | Pipeline `throw sent.Error!` on exhaustion → MassTransit `Immediate(N)` → inherited consolidated move. |
| Keeper recovery give-up (data-gone) | Keeper.Recovery (`ReinjectConsumer`) | BaseConsole.Core (error transport) | `RecoveryDataGoneException` propagates past consumer → consolidated move. |
| At-least-once / no-dedup invariant | Orchestrator + BaseProcessor.Core execution-path types | — | Invariant is the *absence* of dedup machinery; enforced by structural reflection over these assemblies. |
| Reactive `Fault<T>` recovery (dormant, → `keeper-dlq`) | Keeper.Recovery (`KeeperRecoveryHandler`) | — | The ONE legitimate `keeper-dlq` sender; retired in Phase 48. MUST be excluded from the D-03 source-scan. |

## Standard Stack

No new dependencies. All test infra already present in `tests/BaseApi.Tests`.

### Core (already installed — verified by existing test usings)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| xunit.v3 | 3.2.2 | Test framework (`[Fact]`/`[Theory]`/`[Trait]`, `TestContext.Current.CancellationToken`) | Repo standard; SPEC pins it |
| NSubstitute | 5.3.0 | `Substitute.For<IDatabase>()`, `IConnectionMultiplexer`, `IL2HealthGate` | Used in `RecoveryDeadLetterFacts` |
| MassTransit.Testing | 8.5.5 | `ITestHarness`, `AddMassTransitTestHarness`, `harness.Consumed/Published.Any<T>` | The proven consolidation harness |
| Microsoft.Extensions.DependencyInjection | (repo) | `ServiceCollection().BuildServiceProvider(true)` harness build | Existing rig pattern |

### Reusable Test Assets (cite, do not re-create)
| Asset | Location | Purpose |
|-------|----------|---------|
| `KeeperDlqConsolidationTests.BuildHarness` | `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs:50` | In-memory `ConfigureError` rig — the proven template for send-exhaustion → `skp-dlq-1` |
| `RecoveryDeadLetterFacts.BuildHarness` | `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs:54` | Same rig + `EmptyMux()`/`OpenGate()` for the data-gone case |
| `ConsolidatedErrorTransportFilter.Dlq1` const | `src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs` | The `"skp-dlq-1"` name — **reference the const, never the literal** (D-03 specifics) |
| `KeeperDependencyFirewallTests` reflection idiom | `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs` | `typeof(X).Assembly.GetReferencedAssemblies()` absence-assertion pattern |
| `KeeperContractTests` GetTypes/GetProperty idiom | `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs` | `typeof(T).GetProperty(...)` absence — the right model for the no-dedup *type/member* guard |

### Installation
None. `dotnet build SK_P.sln` must stay 0 warnings / 0 errors.

## Coverage Map (HIGHEST-VALUE OUTPUT)

This is the core research deliverable: for each SPEC requirement and ROADMAP SC, classify ALREADY-PROVEN (cite file:method) vs GAP (new assertion needed).

| SPEC Req | ROADMAP SC | Status | Existing proving test (file:method) | Gap → new assertion |
|----------|-----------|--------|--------------------------------------|---------------------|
| **R1** single-DLQ consolidation (generic exhaustion) | SC-1 | **ALREADY-PROVEN (mechanism)** | `KeeperDlqConsolidationTests.cs:Dlq1_Consolidated` (82); `KeeperDlqConsolidationTests.cs:Keeper_SendFault_RetriesToDlq1` (112) — a throw → `Immediate(N)` → `ConsolidatedFault` on `Dlq1` | Audit row cites these. Mechanism already covers "any console endpoint throw → skp-dlq-1." |
| **R1** processor send-exhaustion → `skp-dlq-1` (explicit framing) | SC-1 | **GAP (thin)** | None named "processor". The generic `AlwaysFaultsConsumer` proves the route; no fact frames it as the *processor pipeline's* `throw sent.Error!` (D-10). | **NEW** sibling `[Fact]` in `KeeperDlqConsolidationTests` (same rig). Likely the framing is an audit reference to the generic case + a named processor-flavored throw. See "Processor send-exhaustion seam." |
| **R1** structural: no v4 path targets `keeper-dlq` | SC-1 | **GAP** | No standing guard. (Grep this session confirms `KeeperRecoveryHandler.cs` is the ONLY `KeeperQueues.DeadLetter` sender under `src/Keeper/Recovery/`.) | **NEW** source-scan in `AtLeastOnceStructuralFacts.cs`. **MUST exclude `KeeperRecoveryHandler.cs`** — see landmine. |
| **R2** REINJECT-data-gone → `skp-dlq-1` | SC-3 | **ALREADY-PROVEN (FULL)** | `RecoveryDeadLetterFacts.cs:DataGone_reinject_faults_and_routes_to_dead_letter` (89) — asserts BOTH `Exception is RecoveryDataGoneException` AND `Consumed.Any<ConsolidatedFault>()` | **No new test.** Re-tag `[Trait("Phase","47")]` (add alongside 46) + audit row. This is the cleanest "already green" win. |
| **R3** at-least-once duplicate-delivery (StepCompleted) | SC-2 | **GAP** | `TypedResultConsumerFacts.cs:Injected_StepCompleted_indistinguishable_from_direct` (172) proves *two equal records → equal effect*, but uses **two separate dispatchers** and one `Consume` each — never the SAME message twice into one consumer, never asserts effect-count==2. | **NEW** fact: double-`Consume` of the same `StepCompleted` into a `StepCompletedConsumer` with ONE `RecordingDispatcher`; assert `dispatcher.Calls.Count == 2`, no throw. |
| **R3** at-least-once duplicate-delivery (EntryStepDispatch) | SC-2 | **GAP** | None. | **NEW** mirror fact via `DispatchTestKit` / `RecoveryTestKit` — deliver same `EntryStepDispatch` twice, assert processing fires twice. (Verify the kit shape — see Open Questions.) |
| **R4** structural no-dedup (no `MessageIdentity`/`flag[H]`/dedup key) | SC-2 | **GAP** | No standing guard. (Phase-43 RETIRE-01 removed them but added no regression guard.) | **NEW** reflection guard in `AtLeastOnceStructuralFacts.cs`. **MUST scope around the legitimate BIT-gate `string H` on `PauseWorkflow`/`ResumeWorkflow`** — see landmine. |
| **R5** traceability audit artifact | SC-1/2/3 | **GAP (the deliverable)** | None. | **NEW** `47-DLQ-AUDIT.md` (D-01). |

**Net new test work: 4 facts + 1 audit doc + 1 design-doc paragraph.** R2 is free (re-tag). R1-generic is free (cite existing). This is a small phase.

## Processor Send-Exhaustion Seam

**Source path verified:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` lines 170-184. Both send owners wrap in `RetryLoop.ExecuteAsync(..., limit, ct)` and on exhaustion do `if (!sent.Succeeded) throw sent.Error!;` (D-10) — the throw propagates to MassTransit's `UseMessageRetry` → consolidated `_error` move:
- `SendResult` (172) → `queue:{OrchestratorQueues.Result}`
- `SendKeeper` (178) → `queue:{KeeperQueues.Recovery}`

**Harness rig confirmed reusable:** `KeeperDlqConsolidationTests.BuildHarness` (line 50) installs the exact production `ConfigureError(GenerateFaultFilter + ConsolidatedErrorTransportFilter)` via `AddConfigureEndpointsCallback`, plus a no-op handler bound to `ConsolidatedErrorTransportFilter.Dlq1` so the moved `ConsolidatedFault` is observable via `harness.Consumed.Any<ConsolidatedFault>()`.

**Assessment:** The existing `AlwaysFaultsConsumer` (a consumer whose `Consume` throws) already proves the *generic* mechanism: any endpoint whose consume throws and exhausts `Immediate(N)` lands in `skp-dlq-1` as a `ConsolidatedFault`. A processor pipeline's `throw sent.Error!` is the *same propagation*. Therefore the "processor-specific" fact is best framed as **a sibling `[Fact]` in the same rig using a throwaway consumer that simulates the processor's send-exhaustion throw** (or, simplest, an audit row that references `Dlq1_Consolidated` with a note that the processor pipeline propagates identically). Do NOT boot a real `ProcessorPipeline` in-harness — that requires Redis/IDatabase/sendProvider wiring the rig doesn't have; a throwing consumer is the proven, hermetic equivalent. `[VERIFIED: ProcessorPipeline.cs:170-184, KeeperDlqConsolidationTests.cs:34-103]`

## Structural Guards (D-03) — Idiom + Landmines

### No-dedup type/member guard (reflection)

**Idiom to mirror** (`KeeperDependencyFirewallTests` / `KeeperContractTests`):
```csharp
// Anchor on a public execution-path type to load each assembly.
var orchestrator   = typeof(Orchestrator.Dispatch.StepDispatcher).Assembly;        // public sealed class — verified
var baseProcessor  = typeof(BaseProcessor.Core.Processing.ProcessorPipeline).Assembly; // public sealed class — verified

// Type-absence: no MessageIdentity type survives anywhere in the execution-path assemblies.
foreach (var asm in new[] { orchestrator, baseProcessor })
    Assert.DoesNotContain(asm.GetTypes(), t => t.Name == "MessageIdentity");
```
`[VERIFIED: StepDispatcher.cs:18 public sealed class; ProcessorPipeline.cs:52 public sealed class]`

**Prefer reflection (type/member absence) over source-scan for the no-dedup guard.** A source-scan for `"flag["` or `.H` would false-positive (see landmine). `GetTypes()` + `GetProperty/GetMember` over loaded assemblies is precise and matches the firewall idiom.

### No-keeper-dlq guard (source-scan)

Scoped to `src/BaseProcessor.Core/Processing/` and `src/Keeper/Recovery/`, asserting neither references `KeeperQueues.DeadLetter` / `"keeper-dlq"`. Resolve the repo root from a known anchor (e.g. walk up from `AppContext.BaseDirectory`, or `[CallerFilePath]`) and enumerate `*.cs`.

```csharp
var offenders = Directory.EnumerateFiles(processingDir, "*.cs")
    .Concat(Directory.EnumerateFiles(recoveryDir, "*.cs"))
    .Where(f => Path.GetFileName(f) != "KeeperRecoveryHandler.cs")   // ⚠ MANDATORY exclusion
    .Where(f => File.ReadAllText(f).Contains("KeeperQueues.DeadLetter")
             || File.ReadAllText(f).Contains("keeper-dlq"))
    .ToList();
Assert.Empty(offenders);
```

## Common Pitfalls (LANDMINES — read before planning)

### Pitfall 1: no-keeper-dlq source-scan false-positives on `KeeperRecoveryHandler.cs`
**What goes wrong:** The D-03 source-scan is scoped to `src/Keeper/Recovery/` — but `KeeperRecoveryHandler.cs` LIVES there and is the legitimate, dormant `keeper-dlq` sender (Phase 48 retires it).
**Verified this session (Grep over `src`):** `KeeperRecoveryHandler.cs` references `KeeperQueues.DeadLetter` at **line 136** (`capDlq`) and **line 173** (`dlq`), plus `keeper-dlq` in comments at lines 28, 133, 172. It is the ONLY source file under `src/Keeper/Recovery/` (and the only file under either scoped dir) that sends to `keeper-dlq`. The other 13 files in `src/Keeper/Recovery/` and all 6 in `src/BaseProcessor.Core/Processing/` are clean.
**How to avoid:** The guard MUST explicitly exclude `KeeperRecoveryHandler.cs` by filename (see code above). Document WHY in a comment (it's the dormant reactive path, retired Phase 48). Without the exclusion the test fails immediately on a legitimate, in-scope-for-Phase-48 file.
`[VERIFIED: Grep DeadLetter|keeper-dlq over src/]`

### Pitfall 2: no-dedup guard false-positives on the BIT-gate `string H`
**What goes wrong:** A naive source-scan for `H` / `.H` / `"flag["` would flag legitimate, non-dedup code.
**Verified this session:** `PauseWorkflow` and `ResumeWorkflow` (`src/Messaging.Contracts/PauseWorkflow.cs:4`, `ResumeWorkflow.cs:4`) carry a **positional `string H`** — this is the BIT-gate **pause/localKey** (the four-tuple composite key), NOT a dedup/idempotency identity. `PauseWorkflowConsumer.cs:25` and `ResumeWorkflowConsumer.cs:26` log `m.H`. Additionally, retired-context COMMENTS mention `flag[H]` in `OrchestratorMetrics.cs:35`, `ProcessorMetrics.cs:38`, `StepDispatcher.cs:14`, `IStepDispatcher.cs:23`, and `KeeperRecoveryHandler.cs`. None are live dedup machinery.
**How to avoid:** Use the **reflection type/member guard** (assert no `MessageIdentity` type; assert no live dedup *member* on execution-path types) — NOT a string scan. If any source-scan is added for `flag[H]`, it must (a) ignore comment lines and (b) exclude the BIT-gate `H` and the metrics comment strings. Reflection sidesteps all of this. `[VERIFIED: PauseWorkflow.cs:4, ResumeWorkflow.cs:4, Grep MessageIdentity|flag[H]|.H over src/]`

### Pitfall 3: hard-coding the `"skp-dlq-1"` literal in new tests
**What goes wrong:** A future rename would silently desync the test from production.
**How to avoid:** Always assert against `ConsolidatedErrorTransportFilter.Dlq1` (the existing tests already do this — `KeeperDlqConsolidationTests.cs:60,147`). `[CITED: CONTEXT.md specifics]`

### Pitfall 4: `dotnet test --filter` is IGNORED (Microsoft.Testing.Platform)
**What goes wrong:** Scoping a run with `dotnet test --filter` silently runs the whole suite.
**How to avoid:** `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=47"` or `--filter-method "*Dlq1*"`. `[CITED: SPEC.md:65, CONTEXT.md:109]`

### Pitfall 5: duplicate-delivery test must use ONE dispatcher, not two
**What goes wrong:** The existing `Injected_..._indistinguishable_from_direct` (172) uses **two separate `RecordingDispatcher`s** and asserts each got one call — that proves *indistinguishability*, NOT *no-collapse*. Copying its shape would not satisfy R3.
**How to avoid:** The new fact must feed the SAME message twice into ONE consumer instance (or two instances sharing ONE dispatcher) and assert `Calls.Count == 2` — proving the second delivery is NOT collapsed/deduped. `[VERIFIED: TypedResultConsumerFacts.cs:172-229]`

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| In-memory error-transport rig | A new harness builder | Clone `KeeperDlqConsolidationTests.BuildHarness` | It already reproduces the exact production `ConfigureError` wiring + observable `Dlq1` sink |
| Substituted Redis/L2 | Hand-rolled fakes | `RecoveryDeadLetterFacts.EmptyMux()` + `RecoveryTestKit`/`DispatchTestKit` | Proven NSubstitute setup for `IDatabase`/`IConnectionMultiplexer`/`IL2HealthGate` |
| DLQ name literal | `"skp-dlq-1"` string | `ConsolidatedErrorTransportFilter.Dlq1` | Single source of truth; rename-safe |
| Assembly reflection guard | Custom Roslyn analyzer | `typeof(X).Assembly.GetTypes()` + `GetProperty` | Matches firewall-test idiom; no new tooling |

## Code Examples (verified patterns from this repo)

### Observable consolidated-DLQ assertion (the proven shape)
```csharp
// Source: tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs:82-103
await harness.Bus.Publish(new AlwaysFaults("x1"), ct);
Assert.True(await harness.Consumed.Any<AlwaysFaults>(ct));
Assert.True(await harness.Published.Any<Fault<AlwaysFaults>>(ct));   // Fault<T> still published
Assert.True(await harness.Consumed.Any<ConsolidatedFault>(ct));      // moved to skp-dlq-1
```

### Data-gone → consolidated DLQ (R2 — ALREADY GREEN)
```csharp
// Source: tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs:108-113
Assert.True(await harness.Consumed.Any<KeeperReinject>(
    f => f.Exception is RecoveryDataGoneException, ct));
Assert.True(await harness.Consumed.Any<ConsolidatedFault>(ct));
```

### Duplicate-delivery no-collapse (R3 — NEW; pattern to write)
```csharp
// Mirror TypedResultConsumerFacts setup, but ONE dispatcher + double Consume:
var dispatcher = new RecordingDispatcher();
var consumer = new StepCompletedConsumer(store, new StepAdvancement(), dispatcher,
    OrchestratorTestStubs.Metrics(), NullLogger<StepCompleted>.Instance);
var msg = new StepCompleted(workflowId, stepId, procId) { CorrelationId = c, ExecutionId = e, EntryId = id };
await consumer.Consume(OrchestratorTestStubs.Context(msg, ct));
await consumer.Consume(OrchestratorTestStubs.Context(msg, ct));   // SAME message twice
Assert.Equal(2, dispatcher.Calls.Count);   // no dedup collapse to 1; no throw
```

## Design-Doc Amendment (D-02) — Exact Location

**File:** `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` (LOCKED — amend, do not rewrite).

**Current state (verified):** The at-least-once model is ALREADY stated:
- Header line 5: *"Delivery model: at-least-once; no dedup / idempotency key (the v3.x `H` + dedup gate are removed); duplicate effects are tolerated downstream."*
- Locked-decisions table line 105: *"No dedup / idempotency key (v3.x `H` + dedup gate removed); at-least-once; duplicates tolerated."*
- Line 112 (A4): *"Single `_DLQ1` for all terminal give-ups; `keeper-dlq` removed."*

**Therefore D-02 is a small, mostly-cross-referencing amendment**, not a from-scratch statement. Recommended: add a dated amendment line near the header (mirroring the `A15` amendment-line style at line 4) that (a) elevates the at-least-once/no-dedup guarantee to an explicit named guarantee statement and (b) cites the Phase-47 proving tests (the `47-DLQ-AUDIT.md`). **Bundle the pending Phase-46 `Payload`-on-`KeeperReinject` amendment** (already verified to exist in the contract — `KeeperContractTests.cs:60-69` asserts `KeeperReinject.Payload : string`, and `RecoveryDeadLetterFacts.cs:102` sets it) — one edit closes both. `[VERIFIED: design doc lines 4,5,19,105,112; KeeperContractTests.cs:60-69]`

## Runtime State Inventory

> Verification phase — no rename/migration. Included for completeness; all categories verified clean for *this phase's* scope.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None — no datastore keys change. The `keeper-dlq` queue still exists (Phase 48 removes it); Phase 47 does not touch topology. | None — verified by SPEC out-of-scope + Grep (only `KeeperRecoveryHandler` sends there). |
| Live service config | None — no production wiring change. `ConfigureError` already installed once in `BaseConsole.Core`. | None. |
| OS-registered state | None. | None. |
| Secrets/env vars | None. | None — verified, no config keys in scope. |
| Build artifacts | None — no project/package rename. `dotnet build SK_P.sln` must stay 0/0. | None. |

## Validation Architecture (Coverage Map → VALIDATION.md derivation)

> `workflow.nyquist_validation: true` (verified in `.planning/config.json`). This is a verification phase, so the validation architecture IS the coverage map.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 + NSubstitute 5.3.0 + MassTransit.Testing 8.5.5 (InMemoryTestHarness) |
| Config file | none — Microsoft.Testing.Platform (`dotnet run` entrypoint in `tests/BaseApi.Tests`) |
| Quick run command | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=47"` |
| Full suite command | `dotnet run --project tests/BaseApi.Tests -c Debug` (note: 2 pre-existing broker-dependent E2E failures are NOT Phase-47 regressions) |

### Phase Requirements → Test Map
| Req / SC | Behavior | Test Type | Automated Command | File Exists? |
|----------|----------|-----------|-------------------|-------------|
| R1 / SC-1 (generic) | exhaustion → `skp-dlq-1` as `ConsolidatedFault` | harness | `... -- --filter-method "*Dlq1_Consolidated*"` | ✅ `KeeperDlqConsolidationTests.cs:82` |
| R1 / SC-1 (processor) | processor send-exhaustion → `skp-dlq-1` | harness | `... -- --filter-method "*ProcessorSendExhaustion*"` | ❌ Wave 0 (sibling in `KeeperDlqConsolidationTests`) |
| R1 / SC-1 (structural) | no v4 path references `keeper-dlq` | source-scan | `... -- --filter-trait "Phase=47"` | ❌ Wave 0 (`AtLeastOnceStructuralFacts.cs`) |
| R2 / SC-3 | data-gone REINJECT → `skp-dlq-1`, not loop | harness | `... -- --filter-method "*DataGone_reinject*"` | ✅ `RecoveryDeadLetterFacts.cs:89` (re-tag Phase 47) |
| R3 / SC-2 (StepCompleted) | same message twice → effect twice, no throw | unit | `... -- --filter-method "*Duplicate*"` | ❌ Wave 0 (extend `TypedResultConsumerFacts.cs`) |
| R3 / SC-2 (EntryStepDispatch) | same dispatch twice → processing twice | unit | `... -- --filter-method "*Duplicate*"` | ❌ Wave 0 (verify `DispatchTestKit` seam) |
| R4 / SC-2 | no `MessageIdentity`/dedup member on exec-path assemblies | reflection | `... -- --filter-trait "Phase=47"` | ❌ Wave 0 (`AtLeastOnceStructuralFacts.cs`) |
| R5 / SC-1,2,3 | audit doc maps every row to a green test | doc (verifier-checked) | n/a (human + verifier review) | ❌ Wave 0 (`47-DLQ-AUDIT.md`) |

### Sampling Rate
- **Per task commit:** `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=47"`
- **Per wave merge:** full hermetic suite (excluding the 2 known broker-dependent E2E failures)
- **Phase gate:** `dotnet build SK_P.sln` 0/0 + all Phase-47 facts green + every `47-DLQ-AUDIT.md` row resolves to a real green test, before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `AtLeastOnceStructuralFacts.cs` — reflection no-dedup guard (R4) + source-scan no-keeper-dlq guard (R1) — `[Trait("Phase","47")]`. **Must exclude `KeeperRecoveryHandler.cs`; must use reflection (not string-scan) for dedup.**
- [ ] Sibling processor-send-exhaustion fact in `KeeperDlqConsolidationTests.cs` (R1) — or audit reference to `Dlq1_Consolidated`.
- [ ] Duplicate-delivery fact(s) in `TypedResultConsumerFacts.cs` (R3) — ONE dispatcher, double-`Consume`, `Count==2`.
- [ ] Re-tag `RecoveryDeadLetterFacts.DataGone_reinject_faults_and_routes_to_dead_letter` with `[Trait("Phase","47")]` (alongside 46) for R2.
- [ ] `47-DLQ-AUDIT.md` (R5).
- *Framework install:* none — all infra present.

## Security Domain

> `security_enforcement` not present in config; treat as enabled. This phase adds NO production code and NO new attack surface — it is hermetic test + doc only.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V5 Input Validation | no | No new input paths; tests only |
| V6 Cryptography | no | None |
| V7 Error Handling / Logging | indirect | `KeeperRecoveryHandler` already logs ids as structured params (T-40-02), never interpolated — unchanged this phase |

No threat patterns introduced. The only doc/log-adjacent note: the design-doc amendment is prose; no secrets. `[VERIFIED: KeeperRecoveryHandler.cs:30-34 security note; no src changes in scope]`

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `DispatchTestKit` provides a reusable seam to deliver `EntryStepDispatch` twice into the processor consumer (CONTEXT.md D-04 names it; not Read this session). | Coverage Map R3 / Validation | LOW — if the kit shape differs, the EntryStepDispatch mirror fact may need a small adapter; the StepCompleted fact is unaffected. Confirm by reading `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs` (+ DispatchTestKit) at plan time. |
| A2 | A throwing-consumer fact is an acceptable hermetic proxy for "processor pipeline send-exhaustion" (vs booting a real `ProcessorPipeline`). | Processor send-exhaustion seam | LOW — SPEC acceptance (line 71) asks a harness fact that "routes the faulted message to skp-dlq-1"; the existing generic case already satisfies the mechanism. If the planner wants a literal `ProcessorPipeline` boot, it needs Redis/sendProvider wiring the rig lacks. |
| A3 | `Orchestrator` and `BaseProcessor.Core` assemblies are loadable via `typeof(StepDispatcher).Assembly` / `typeof(ProcessorPipeline).Assembly` from the test project (both are public sealed classes — verified) and the test project references both. | Structural guard idiom | LOW — firewall tests already load `Keeper`/`BaseConsole.Core` assemblies the same way; references are near-certain. Verify the test `.csproj` references at plan time. |

## Open Questions

1. **`DispatchTestKit` / `RecoveryTestKit` exact seam for double-delivering `EntryStepDispatch`.**
   - What we know: CONTEXT.md D-04 names both kits as reusable; `RecoveryDeadLetterFacts` uses an inline `EmptyMux()`/`OpenGate()` pattern (so a kit may or may not be the same shape).
   - What's unclear: whether `DispatchTestKit` exposes a single-`Consume`-twice entry point or needs an adapter.
   - Recommendation: Read `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs` (and locate `DispatchTestKit`) during planning; if absent/awkward, the EntryStepDispatch duplicate fact can reuse the `RecoveryDeadLetterFacts.BuildHarness` rig and publish the same `KeeperReinject`/dispatch twice.

2. **Whether to add `[Trait("Phase","47")]` to the existing `RecoveryDeadLetterFacts` fact or duplicate it.**
   - What we know: the fact currently carries `[Trait("Phase","46")]` and fully proves R2.
   - Recommendation: ADD a second `[Trait("Phase","47")]` (xunit.v3 supports multiple Trait attributes) so `--filter-trait "Phase=47"` includes it and the audit row resolves — no behavioral duplication.

## Sources

### Primary (HIGH — read this session)
- `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` — the consolidation harness + `Dlq1` assertions
- `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs` — R2 already-green proof
- `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs` — indistinguishability (NOT no-collapse)
- `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs`, `Console/ConsoleDependencyFirewallTests.cs` — reflection idiom
- `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs` — GetTypes/GetProperty idiom + `KeeperReinject.Payload` proof
- `src/Keeper/Recovery/KeeperRecoveryHandler.cs` — the ONLY `keeper-dlq` sender (lines 136, 173) — landmine source
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — send-exhaustion `throw sent.Error!` (170-184)
- `src/Messaging.Contracts/PauseWorkflow.cs`, `ResumeWorkflow.cs` — legitimate BIT-gate `string H` — landmine source
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` — at-least-once already stated (5,105,112); A15 amendment style
- `.planning/REQUIREMENTS.md` (RESIL-02 line 60, RESIL-03 line 61); `.planning/ROADMAP.md` (Phase 47 §498-506); `.planning/config.json`
- Grep `DeadLetter|keeper-dlq` and `MessageIdentity|flag[H]|.H` over `src/` — confirms scope cleanliness + the two landmines

### Secondary / Tertiary
- None — all claims verified in-repo this session.

## Metadata

**Confidence breakdown:**
- Coverage map: HIGH — every cited test read line-by-line this session
- Standard stack: HIGH — usings/versions from existing test files + SPEC
- Pitfalls/landmines: HIGH — both false-positive sources confirmed by Grep + Read
- EntryStepDispatch duplicate seam: MEDIUM — kit not Read (A1)

**Research date:** 2026-06-09
**Valid until:** 2026-07-09 (stable — verification phase, no external/fast-moving deps)
