# Phase 33: Fault-Recovery Spike (De-Risk) - Context

**Gathered:** 2026-06-05
**Status:** Ready for planning

<domain>
## Phase Boundary

**Prove — do not build.** Phase 33 de-risks the load-bearing assumption of the whole v3.7.0 Keeper milestone before any Keeper code is committed: that a published `Fault<EntryStepDispatch>` / `Fault<ExecutionResult>` can be

1. **consumed via pub/sub** by an external subscriber (binding all producing replicas, no per-`{procId}_error`-queue binding) while command-faults (`Fault<StartOrchestration>` / `Fault<StopOrchestration>`) are demonstrably **NOT** delivered,
2. **unwrapped** to its inner message + full 6-id `IExecutionCorrelated` tuple + `H` from `Fault<T>.Message` (since `Fault<T>` is not itself `IExecutionCorrelated`),
3. **re-injected directly to its origin endpoint by type** — `queue:{processorId:D}` for dispatch, `queue:orchestrator-result` for result — with exactly-once downstream effect and no orchestrator round-trip, and
4. **collapsed on a deliberate duplicate** by the receiver's surviving Phase-31 `flag[H]` / `flag[m.H]` dedup gate (Keeper needs no dedup of its own).

Plus: **record** the `{procId}_error` retention decision.

In scope: the proof + the recorded decision. Out of scope (later phases): the real `Keeper` console (34), production fault-intake/log-scope (35), the L2 probe loop + DLQs (36), orchestrator pause/resume (37), Keeper metrics + E2E + close gate (38).

</domain>

<decisions>
## Implementation Decisions

### Spike Vehicle & Disposition
- **D-01:** Vehicle = **one throwaway-but-kept RealStack xUnit E2E test**, `FaultRecoverySpikeE2ETests`, marked `[Trait("Category","RealStack")]` (excluded from the hermetic suite; only runs against the live compose stack). Cloned from `IdempotentExactlyOnceE2ETests` — reuses its entire rig: genuine embedded-SourceHash reflection, GET-or-create Processor row, truthful liveness poll, `PollEsForLog`, `RealStackWebAppFactory` host overrides, and net-zero `skp:*` teardown.
- **D-02:** The only genuinely new machinery vs the clone-source is a **short-lived in-test `IBusControl`** connected to the live `sk-rabbitmq`, registering `IConsumer<Fault<EntryStepDispatch>>` + `IConsumer<Fault<ExecutionResult>>` on a temporary endpoint. (Mirrors the existing file's short-lived-`IBusControl`-to-`Send` trick — here used to *catch* the faults instead of send.)
- **D-03:** **No production Keeper code in Phase 33.** The `Keeper` console is Phase 34. Findings land in `33-SUMMARY` (and feed `33-VERIFICATION`).
- **D-04:** Disposition = **keep** the test as a standing RealStack regression guard for the bind → unwrap → re-inject-by-type → `flag[H]`-collapse contract that all of Phases 34–38 depend on. Cheap and RealStack-gated, so not deleted after findings are captured.

### Fault-Trip Realism
- **D-05:** Trip **both** fault types **LIVE** via deterministic infra-fault induction (no purely synthetic envelopes if avoidable), using the proven WRONGTYPE recipe symmetrically:
  - `Fault<EntryStepDispatch>`: pre-seed the processor's output content-address key (`skp:data:{hash}`) as a Redis **LIST**, so the processor's `StringSetAsync` output write throws `WRONGTYPE` every attempt → `Immediate(N)` exhausts → MassTransit publishes the fault. (Exactly the reverted `CancelledCircuitBreakerE2ETests` recipe.)
  - `Fault<ExecutionResult>`: poison the orchestrator `ResultConsumer`'s own a-priori-computable Redis key (the `flag[m.H]` flip) the same WRONGTYPE way.
- **D-06:** Documented **fallback for the result type only** — if a clean deterministic live trip of the orchestrator-result path proves fiddly, publish a synthetic `Fault<ExecutionResult>` to still prove bind + unwrap + re-inject-to-`orchestrator-result`. The novel risk (pub/sub bind, `.Message.Message` double-unwrap, re-inject-by-type, `flag[H]` collapse) is fully exercised by the dispatch trip; the result trip mainly proves the *second endpoint/type* works. **Research item:** the exact orchestrator-result poison surface.
- **D-07:** Re-inject forwards the **extracted `Fault<T>.Message` instance verbatim** (same `H`, no hand-reconstruction from the 6 ids) to its origin endpoint by type via `GetSendEndpoint(...)` + **`Send`** (NOT `Publish`, NO orchestrator round-trip). Forwarding verbatim guarantees the receiver's gate sees the identical `H` — the whole point of the collapse proof.
- **D-08:** Exactly-once + duplicate-collapse proof = re-inject the same extracted message **twice**, then assert **one** downstream effect via `PollEsForLog` + a hit-count probe over a settle window (the live inverse of the historical `StepB4 ×2` over-execution bug). The receiver's surviving Phase-31 `flag[H]` (processor) / `flag[m.H]` (orchestrator) gate drops the second delivery.

### Negative Command-Fault Proof
- **D-09:** **Active synthetic negative** — publish a `Fault<StartOrchestration>` + `Fault<StopOrchestration>` to the broker and assert the spike's two execution-fault consumers record **zero** captures over a settle window. This directly tests that the bindings are **type-scoped** (RabbitMQ topology routes `Fault<T>` by message type), beyond the structural fact that the spike never binds command-fault consumers.
  - Rejected: structural-only (tautological); organic-trip (`Start`'s main exception `WorkflowRootNotFoundException` is `Ignore`d, so it won't Fault — high induction cost for little extra signal).

### `_error` Retention
- **D-10:** Keep `{procId}_error` as the **TTL'd forensic copy** that consolidates **source-agnostically into DLQ-1** (built in Phase 36 per INTAKE-03 / DLQ-02) — **never** Keeper's worklist (Keeper recovers off the `Fault<T>` pub/sub stream). The operator triage axis is **mechanism** (DLQ-1 = forensic-that-TTLs-away; DLQ-2 `keeper-dlq` = L2-probe give-up alert), **not** origin component (processor vs orchestrator is irrelevant to the operator). Phase 33 **records** this decision only — no `_error` topology change in this phase.

### Observability (explicitly untouched)
- **D-11:** **No metric work in Phase 33** — pure mechanism spike (user direction: "leave it as is"). No producer-side `*_exhausted` counter is added or designed here; the Phase-32-reverted `workflow_cancelled` stays gone. All Keeper exhaustion/attempt observability is Keeper-side and scoped to **Phase 38** (KMET-01..03) + **Phase 35** (KMET-04 logs). The current exhaustion signals — `processor_dispatch_consumed` delta (`= N+1` on full exhaustion), `{procId}_error` depth, and the runtime `GetRetryAttempt()` value — are left as-is and **not** asserted by the spike.

### Claude's Discretion
- Re-inject plumbing details (the `GetSendEndpoint` URI construction, the short-lived `IBusControl` lifecycle/teardown), settle-window durations, the exact `PollEsForLog` query shape, and which single processor/workflow topology the spike seeds — all builder choices within the established precedent rig.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents (researcher, planner) MUST read these before planning or implementing.**

### Phase scope & requirements
- `.planning/ROADMAP.md` — Phase 33 section: goal, 4 success criteria, requirement map.
- `.planning/REQUIREMENTS.md` — in-scope: **INTAKE-01** (pub/sub bind both fault types, command-faults not consumed), **INTAKE-02** (6-id + `H` extraction from `Fault<T>.Message` + log-scope), **INTAKE-04** (re-inject to origin endpoint by type), **PROBE-06** (no Keeper dedup; rides receiver `flag[H]`). Downstream context: INTAKE-03, DLQ-02/03 (DLQ consolidation the spike's `_error` decision feeds), KMET-01..04 (Keeper metrics, Phase 38/35 — explicitly out of 33).

### Spike vehicle (clone source + rig)
- `tests/BaseApi.Tests/Orchestrator/IdempotentExactlyOnceE2ETests.cs` — **the clone source.** Already reconstructs a dispatch, `Send`s it twice to `queue:{procId:D}`, proves `flag[H]` collapse via `PollEsForLog` + hit-count probe, with net-zero `skp:data:*`/`skp:flag:*` teardown. ~80% of the spike's machinery; the short-lived `IBusControl` pattern to copy.
- `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — the base round-trip rig (embedded SourceHash reflection, GET-or-create Processor, liveness poll, `RealStackWebAppFactory`).
- `tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs` — correlation-propagation + ES-log-assertion precedent (HTTP → Redis L2 → fan-out → correlated orchestrator log).

### Reverted Phase-32 proofs (recover from git — proven patterns to resurrect)
- `IConsumer<Fault<EntryStepDispatch>>` pub/sub binding + `context.Message.Message.WorkflowId` double-unwrap: introduced `33b1d8b` (feat 32-06 `FaultUnscheduleConsumer`), reverted `c046cc8` (32.1-01).
- WRONGTYPE deterministic live-trip recipe: `a6c6825` (test 32-07 `CancelledCircuitBreakerE2ETests`), deleted `f325a5f` (32.1-01).
- `GetRetryAttempt() == Limit` exhaustion boundary + `Fault.Message.WorkflowId` round-trip probes: Phase 32-01 (`998dd49` `RetryAttemptNumberingFacts`, `3aca386` `FaultConsumerBindingFacts`), deleted `f325a5f` (32.1-01).

### Wire contracts & endpoints (Messaging.Contracts leaf)
- `src/Messaging.Contracts/EntryStepDispatch.cs`, `src/Messaging.Contracts/ExecutionResult.cs` — the two inner messages wrapped by `Fault<T>` (`Fault<T>` itself is a MassTransit framework type, not in this leaf).
- `src/Messaging.Contracts/IExecutionCorrelated.cs` + `src/Messaging.Contracts/ICorrelated.cs` — the 6-id tuple (correlationId, workflowId, stepId, processorId, entryId, executionId) the spike extracts.
- `src/Messaging.Contracts/Hashing/MessageIdentity.cs` — `ComputeH(...)` / the deterministic identity `H` the receiver dedups on.
- `src/Messaging.Contracts/StartOrchestration.cs` + `src/Messaging.Contracts/StopOrchestration.cs` — the command messages whose faults must NOT be delivered (D-09 negative proof).
- `src/Messaging.Contracts/OrchestratorQueues.cs` + `src/Messaging.Contracts/ProcessorQueues.cs` — origin endpoint name constants (`orchestrator-result`, `queue:{processorId:D}`) for re-inject-by-type.
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `skp:data:{...}` / `skp:flag:{...}` builders (WRONGTYPE poison target + net-zero teardown scan).
- `src/Messaging.Contracts/Configuration/RetryOptions.cs` — the `Immediate(N)` budget that exhausts to publish the fault.

### Fault origins (producers — to understand the trip surface)
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` — processor consumer; output `skp:data` write (WRONGTYPE target) + `flag[H]` dedup gate; `Immediate(N)` bound at `ProcessorStartupOrchestrator`.
- `src/Orchestrator/Consumers/ResultConsumer.cs` — orchestrator result consumer on `queue:orchestrator-result`; `flag[m.H]` flip (result-side WRONGTYPE target).
- `src/Orchestrator/Dispatch/StepDispatcher.cs` — the `Send` (not Publish) to `queue:{processorId:D}` precedent.

### Metrics holders (referenced only to confirm "leave as is" — D-11)
- `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs`, `src/Orchestrator/Observability/OrchestratorMetrics.cs` — the two existing meters; NO `*_exhausted` counter exists, none added in 33.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`IdempotentExactlyOnceE2ETests`** — clone target; the duplicate-`Send`-to-`queue:{procId:D}` + `flag[H]`-collapse + net-zero teardown already exist. Spike adds fault *capture* + *re-inject*, not new infra.
- **Short-lived in-test `IBusControl`** (already used in that file to `Send`) — the vehicle for registering the two `Fault<T>` consumers against live `sk-rabbitmq`.
- **`RealStackWebAppFactory` + `PollEsForLog` + liveness poll + `L2KeysToCleanup`** — the whole RealStack harness is reusable verbatim.
- **Reverted Phase-32 `FaultUnscheduleConsumer` + `CancelledCircuitBreakerE2ETests` + `FaultConsumerBindingFacts`** — proven binding, double-unwrap, and WRONGTYPE-trip code, recoverable from git (refs above).

### Established Patterns
- `Fault<T>` is consumed by binding `IConsumer<Fault<T>>` (MassTransit auto-publishes it on `Immediate(N)` exhaustion); inner message reached via `context.Message.Message` (double `.Message`).
- Re-inject = resolve `GetSendEndpoint` for the origin queue + `Send` (NOT `Publish`); endpoint names live in `OrchestratorQueues` / `ProcessorQueues`.
- Receiver idempotency = effect-first `flag[H]` `Pending → Ack` CAS (Phase 31), surviving and reused — Keeper/spike adds nothing.
- Metrics convention (for context only): meter-per-process, snake_case, no `_total` suffix, `processorId` tag, `service_instance_id` resource label.

### Integration Points
- The spike's in-test bus connects to the same live RabbitMQ the processor/orchestrator publish faults on; re-inject targets the live `queue:{procId:D}` / `queue:orchestrator-result` the running containers consume.
- WRONGTYPE poison + net-zero teardown both key off `L2ProjectionKeys` against the live Redis (`skp:` prefix).

</code_context>

<specifics>
## Specific Ideas

- The spike is the **live inverse of the `StepB4 ×2` over-execution bug** — a re-injected duplicate carrying the same `H` must produce zero extra downstream effect.
- "To the operator it doesn't matter which processor or the orchestrator" — the two DLQs split by **mechanism** (forensic-TTL vs probe-give-up-alert), never by origin component (D-10).
- "Leave it as is" on metrics — Phase 33 adds/asserts no instruments; exhaustion observability stays exactly where it is today (D-11).

</specifics>

<deferred>
## Deferred Ideas

- **Producer-side `*_exhausted` business metric** (and emitting `GetRetryAttempt()` as a tag/log field) — raised during discussion; explicitly **left as-is** per user direction. The milestone covers exhaustion/attempt observability Keeper-side only (`keeper_fault_consumed`, `keeper_l2_probe_failed`, DLQ depths — KMET-02/03, Phase 38). If a producer-side exhaustion rate signal is ever wanted, it is a future-milestone candidate, not v3.7.0.

*No reviewed-but-deferred todos (none matched this phase).*

</deferred>

---

*Phase: 33-fault-recovery-spike-de-risk*
*Context gathered: 2026-06-05*
