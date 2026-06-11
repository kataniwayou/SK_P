# Phase 53: Model-B Teardown - Context

**Gathered:** 2026-06-11
**Status:** Ready for planning

<domain>
## Phase Boundary

Reach the A18 end-state: remove the last Model-B recovery machinery and reconcile the global retry rule so the system is buildable on the slot-array path alone. **Scout finding that reframes this phase:** the Model-B *contracts and consumers are already deleted* (Phase 50 removed `UPDATE`/`CLEANUP`/`BackupOptions`/composite `corr:wf:proc:exec` key; Phase 52 left the keeper recovery consumer at exactly 3 states — `Reinject`/`Inject`/`Delete`). So SC-1/SC-2 are **largely verification**.

The genuinely-unfinished work is the **Phase-52 D-08 deferral**: A18 §Global-rules (`UseMessageRetry = none` / `_error` routing disabled, send-exhaust → throw → broker redelivery) is still **unwired** — the processor keep-latch (`ProcessorStartupOrchestrator.cs:180`) and every Orchestrator `ConsumerDefinition` still carry `UseMessageRetry(Immediate(N)) → skp-dlq-1` (the Phase-44 D-09 pragmatic latch, shipped through v4.0.0).

This discussion locks HOW the teardown + retry reconciliation land. **A18 is the LOCKED source of truth** — the decisions below align the code TO A18; they do not change A18 (no doc amendment needed).

**In scope:** processor + orchestrator retry/`_error` end-state rewire; scope the consolidated-error filter to the keeper endpoint; RETIRE-03 remnant-sweep + end-state guard.
**Out of scope:** the keeper recovery endpoint (its gate-aware Dlq1/SustainedOutage policy is Phase-52 settled, unchanged); any new recovery *behavior*; the live close gate (Phase 54).

</domain>

<decisions>
## Implementation Decisions

### Retry / `_error` end-state (the D-08 reconciliation)
- **D-01:** **Enforce A18 literally for the execution/forward path.** Processor + Orchestrator endpoints move to `UseMessageRetry = none` + `_error` disabled; a send that exhausts its in-code `RetryLoop` **throws → broker redelivery** (no dead-letter). This aligns the as-built `Immediate(N)→skp-dlq-1` latch (Phase-44 D-09) to A18 §Global-rules — **no A18 amendment** (A18 already states `=none`/`_error`-disabled globally and carves out the keeper's configurable policy separately).
- **D-02:** **Reach = processor execution/forward path + ALL Orchestrator consumers** (result `StepCompleted`/`StepFailed`/`StepCancelled`/`StepProcessing`, `Start`/`Stop`, `Pause`/`Resume` + `PauseAll`/`ResumeAll`). The **keeper recovery endpoint is excluded** — it keeps its own retry/dead-letter wiring driven by `RecoveryOptions.ExhaustionPolicy`.
- **D-03:** **`skp-dlq-1` sheds the global producer set.** Scope `ConsolidatedErrorTransportFilter` + `GenerateFaultFilter` (today wired globally in `BaseConsole.Core` via `AddConfigureEndpointsCallback`) so they no longer apply to every endpoint. After Phase 53, the *processor execution/forward path* and the *system-command* control endpoints (Pause/Resume/PauseAll/ResumeAll) never dead-letter. **Refined by D-07 (2026-06-11):** `skp-dlq-1` is **NOT** strictly keeper-only — it also receives the user-triggered `WorkflowRootNotFoundException` business fault from Start/Stop. Post-Phase-53 producer set = **keeper Dlq1 + Start/Stop `WorkflowRootNotFoundException`**. The Phase-54 close-gate baseline MUST reflect this two-producer set, not "keeper-only."
- **D-04:** **Unbounded requeue spin on poison sends is accepted.** With no retry and no dead-letter, a permanently-failing send (e.g. orchestrator unreachable on the bus — *not* an L2 outage, so the BIT gate does not pause it) redelivers indefinitely — the same "accepted spin" tradeoff as the keeper's SustainedOutage mode. Chosen deliberately over parking.
- **D-05:** **Keeper unchanged.** Gate-aware two-mode behavior is Phase-52 settled and is NOT re-touched: gate-CLOSED → non-destructive for both modes (consumption pauses, messages accumulate/requeue, no DLQ); gate-OPEN → Dlq1 sends-then-exhausts → `skp-dlq-1`, SustainedOutage parks/requeues (no DLQ). This decision only establishes *why* the keeper's `_error`/`skp-dlq-1` wiring survives while the rest sheds it.

### OQ-1 resolved — user-triggered vs. system-command DLQ disposition (resolves research Open Question 1)
- **D-07 (2026-06-11, user ruling):** The dividing line for the Start/Stop `Ignore<WorkflowRootNotFoundException>` disposition (which dies when the bus-retry block is stripped) is **user-triggered vs. autonomous/system**:
  - **Start / Stop = USER-TRIGGERED.** A human is watching and immediately notices whether orchestration started or failed to respond. A `WorkflowRootNotFoundException` here is a **terminal business failure that must be preserved and visible** → **dead-letter to `skp-dlq-1`** (no spin, no park). Preserves today's `Ignore → _error → ConsolidatedErrorTransportFilter → skp-dlq-1` outcome, minus the Phase-44 bus-retry latch.
  - **Pause / Resume / PauseAll / ResumeAll = SYSTEM COMMANDS** (autonomous orchestration, no human watching) → **pure A18**: `UseMessageRetry=none`, `_error` disabled, throw → broker redelivery. **NO DLQ carve-out.**
  - **Processor execution/forward path = pure A18** (D-01, unchanged).
- **Scope is exact:** the carve-out is **`WorkflowRootNotFoundException` on the Start + Stop consumers ONLY** — exactly one exception type on exactly two endpoints. Every other endpoint sheds its retry/`_error` wiring.
- **Mechanism (planner finalizes the seam):** lean toward an **explicit catch in `StartOrchestrationConsumer`/`StopOrchestrationConsumer`** that routes `WorkflowRootNotFoundException` to `skp-dlq-1` and acks — so the Start/Stop endpoint stays free of the blanket `UseMessageRetry` + global error pipeline (A18-clean) while this one terminal business exception still dead-letters. Alternative (a *scoped* error filter retained on that endpoint) is acceptable if the planner finds it cleaner, but the explicit-catch path is preferred. The planner MUST also enumerate any sibling control-plane business exceptions on Start/Stop and confirm none silently regress.
- **This is the one place the teardown is NOT purely subtractive** — it adds explicit terminal-fault handling to replace the implicit `Ignore`-via-retry behavior that A18's global rule removes.

### RETIRE-03 verification
- **D-06:** **Extend the existing guard + lock the new end-state.** Add to `tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs` (the Phase-50 guard that already covers contract-level retirement): (a) a 5→3 "exactly these recovery-state consumers survive" reflection assertion, (b) a broader source/string remnant sweep across execution-path assemblies incl. a `corr:wf:proc:exec` literal scan. **Plus** a new standing guard asserting the processor + orchestrator endpoints carry **no** `UseMessageRetry`/`_error` wiring — so the D-01 invariant cannot silently regress. Mirrors the repo's `ReactivePathRetiredFacts`/`ModelBContractsRetiredFacts` negative-guard idiom.

### Claude's Discretion (research/planner HOW)
- **Exact MT 8.5.5 throw→redelivery mechanism** without the delayed-exchange plugin (removed Phase 24.1): plain nack-requeue vs. a never-exhausting large-finite `Immediate` retry (the trick Phase 52 used for keeper SustainedOutage). Researcher/planner to verify against the installed assembly.
- **How to scope `ConsolidatedErrorTransportFilter` + `GenerateFaultFilter` to one endpoint** (D-03) — they are currently registered via a global `AddConfigureEndpointsCallback` in `BaseConsole.Core`; the planner decides the scoping seam (per-endpoint opt-in vs. keeper-local apply).
- **Teardown commit ordering/atomicity** — bisect-friendly sequencing of the wiring removal vs. the guard additions.
- Exact hermetic-fact decomposition proving the new end-state.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Source of Truth (LOCKED)
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` → **"Recovery Re-architecture (A18)"** — specifically **§Global rules (lines 141-144)** (`_error` routing disabled / `UseMessageRetry = none` / send-op throw→broker-redelivery / L2-op routed per-site), **§Keeper — 3 states (lines 205-221)** (the 3 surviving states + gate-open-only + configurable exhaustion policy), **§Invariants (lines 223-227)**. Everything above A18 (A15 result contract, A14 BIT gate + pause/resume, A16 at-least-once, A4 single `skp-dlq-1`) holds unchanged.

### Requirements & Roadmap
- `.planning/REQUIREMENTS.md` — **RETIRE-03** (5-state→3-state collapse + no Model-B remnant survives a source/reflection sweep). RETIRE-01/02 landed Phase 50; remnant-verified here.
- `.planning/ROADMAP.md` → **Phase 53** section — the 3 success criteria.

### Predecessor context (the D-08 deferral chain)
- `.planning/phases/52-three-state-keeper/52-CONTEXT.md` — **D-08** (keeper-endpoint-local scope only; the processor keep-latch, the global `UseMessageRetry=none` rule, and RETIRE-03 explicitly deferred to Phase 53) and the keeper's settled Dlq1/SustainedOutage policy.
- `.planning/phases/51-processor-forward-recovery-pipeline/51-CONTEXT.md` — code_context note flagging the A18 `UseMessageRetry=none` reconciliation against the Phase-44 outer-latch wiring (deferred to this phase).
- `.planning/phases/50-contracts-slot-array-l2-key-reshape/50-CONTEXT.md` — the contract-level Model-B deletions already landed.

### Code to rewrite / touch (this phase)
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` (line ~180) — the processor keep-latch `cfg.UseMessageRetry(r => r.Immediate(retryLimit))` to remove (D-01).
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (~lines 57, 311-344) + `EntryStepDispatchConsumer.cs` — send-propagation `throw sent.Error!` + the `→ _error` doc-comments to reconcile to the new end-state.
- `src/Orchestrator/Consumers/*ConsumerDefinition.cs` — every definition's `endpointConfigurator.UseMessageRetry(...)` (Start/Stop, StepCompleted/Failed/Cancelled/Processing, PauseWorkflow/PauseAll owners) to strip (D-01/D-02).
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` (~lines 50-61) + `src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs` — the global `GenerateFaultFilter` + `ConsolidatedErrorTransportFilter` registration to scope to the keeper endpoint only (D-03).
- `src/Keeper/Recovery/RecoveryEndpointBinder.cs` + `ReinjectConsumerDefinition.cs` — the keeper's retained, config-driven retry/dead-letter wiring (reference only — NOT changed; D-05).
- `tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs` — the Phase-50 guard to EXTEND (D-06); `ReactivePathRetiredFacts.cs` — the negative-guard idiom to mirror for the new end-state guard.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`ModelBContractsRetiredFacts`** (Phase 50) — already guards composite-backup-key absence, `KeeperUpdate`/`KeeperCleanup` type absence, `BackupOptions` absence, and the `MessageIndex`/`ExecutionData` survivors. The D-06 extension lands here; its xml-doc already says "the full source + reflection remnant sweep (RETIRE-03) is Phase 53."
- **`ReactivePathRetiredFacts`** (Phase 48) — the verified reflection/source-scan negative-guard idiom (no host boot) to mirror for the new no-`UseMessageRetry`/`_error` end-state guard.
- **`RetryLoop`** (`BaseConsole.Core.Resilience`) — the in-code per-op/per-send retry that survives unchanged; only the *outer bus* `UseMessageRetry` latch is removed. Send-exhaust still throws; what the throw lands in changes (no dead-letter → redelivery).
- **Keeper `RecoveryEndpointBinder`** — the worked example of an endpoint that branches retry/dead-letter on config; reference for how the keeper keeps `skp-dlq-1` after D-03 scoping.

### Established Patterns
- **Per-endpoint `UseMessageRetry` ownership** — on shared endpoints only ONE `ConsumerDefinition` registers retry (the others are intentional no-ops). Removal must account for this single-owner pattern across the orchestrator's shared result + pause/resume endpoints.
- **Global endpoint-callback wiring** — `BaseConsole.Core`'s `AddConfigureEndpointsCallback` applies `GenerateFaultFilter` + `ConsolidatedErrorTransportFilter` to every endpoint; scoping it to one endpoint (D-03) is the load-bearing mechanical change.
- **Standing negative-guard tests** — the repo locks every retirement (`ReactivePathRetiredFacts`, `ModelBContractsRetiredFacts`) with reflection/source-scan facts; D-06 continues this.

### Integration Points
- Processor dispatch endpoint (`ProcessorStartupOrchestrator`) and Orchestrator shared consumer endpoints lose their outer latch; the keeper recovery endpoint retains its config-driven wiring — the scoping seam between them is the key integration boundary.
- `skp-dlq-1` (declared in `BaseConsole.Core`, x-message-ttl 7d) survives but its only producer becomes the keeper Dlq1 path — the Phase-54 close-gate triple-SHA baseline should reflect keeper-only DLQ traffic.

</code_context>

<specifics>
## Specific Ideas

- The user's explicit end-state matrix (verbatim intent, refined by D-07): **Processor execution/forward path + system-command control endpoints (Pause/Resume/PauseAll/ResumeAll)** = `UseMessageRetry=none` + `_error` disabled + send-exhaust→throw→broker-redelivery; **Start/Stop (user-triggered)** = same retry teardown, BUT `WorkflowRootNotFoundException` → terminal `skp-dlq-1` (no spin, no park); **Keeper** = config-driven — gate-open Dlq1 (exhaust→`skp-dlq-1`) **or** SustainedOutage (park, no DLQ); gate-closed non-destructive for both. `skp-dlq-1` post-Phase-53 producers = **keeper Dlq1 + Start/Stop `WorkflowRootNotFoundException`** (per D-07; the earlier "keeper-only" framing is superseded).
- This is a code-to-doc alignment (enforce the LOCKED A18 literally), NOT a doc amendment — distinguishing it from the rejected "ratify the as-built latch" path.
- The unbounded poison-send requeue spin (D-04) is a *deliberately accepted* tradeoff, parallel to the keeper SustainedOutage spin — call it out in the plan as an accepted residual, not an oversight.

</specifics>

<deferred>
## Deferred Ideas

- **Live proof + N×GREEN triple-SHA close gate** (TEST-01/02) → **Phase 54**. The close-gate `skp-dlq-1` baseline should account for keeper-only DLQ traffic.
- None of the above is scope creep — Phase 54 is the locked successor phase of the same milestone.

None — discussion stayed within phase scope.

</deferred>

---

*Phase: 53-model-b-teardown*
*Context gathered: 2026-06-11*
