---
phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume
asvs_level: 1
verified: 2026-06-09
result: SECURED
threats_closed: 12
threats_open: 0
---

# Security Verification — Phase 46

**Phase:** 46 — Keeper 5-State Recovery + Orchestrator Per-Item Consume
**ASVS Level:** 1
**Threats Closed:** 12 / 12
**Threats Open:** 0

---

## Threat Verification

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-46-01 | Tampering — KeeperReinject.Payload on the wire | accept | CLOSED | Internal bus only; the processor is the sole producer (same trust domain). No external input crosses this boundary. Acceptance rationale is sound: the payload rode EntryStepDispatch before this phase; no new external surface is introduced. |
| T-46-02 | Information Disclosure — Payload on recovery envelope | accept | CLOSED | The same internal broker already carried Payload on EntryStepDispatch (Phase 44). ReinjectConsumer.cs confirms the field is read from the already-trusted KeeperReinject message and used only for reconstruction. No new exposure. Acceptance rationale is sound. |
| T-46-03 | DoS — Poison message looping on keeper-recovery | mitigate | CLOSED | `RecoveryConsumerBase.cs:66-68` — every L2 op + Send passes through `Guard`/`Guard<T>` which calls `RetryLoop.ExecuteAsync` and re-throws `outcome.Error` on exhaustion. Gate-wait bounded by linked CTS at `RecoveryConsumerBase.cs:43` (`CancelAfter(TimeSpan.FromSeconds(recoveryOptions.Value.GateWaitSeconds))`). Exhausted throws propagate to the endpoint `UseMessageRetry` registered at `UpdateConsumerDefinition.cs:47`. ConsolidatedErrorTransportFilter (inherited, BaseConsole.Core) routes dead-letters to skp-dlq-1 — no per-consumer ConfigureError present (grep confirms doc-comment references only). |
| T-46-04 | DoS — Indefinite gate-closed hold | mitigate | CLOSED | `RecoveryConsumerBase.cs:42-53` — linked CTS created with `CancelAfter(TimeSpan.FromSeconds(recoveryOptions.Value.GateWaitSeconds))` (default 300s). `catch (OperationCanceledException) when (cts.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)` throws `RecoveryGateTimeoutException` (transient marker), which routes through the endpoint `UseMessageRetry` at `UpdateConsumerDefinition.cs:47`. `RecoveryOptions.cs:12` confirms the 300s default. |
| T-46-05 | Tampering/correctness — Murmur3 partition collision | accept | CLOSED | Accepted: Murmur3 is a non-crypto distribution hash; collision only co-schedules unrelated execs into the same partition slot (ordering optimization). True identity is the full 4-tuple L2 key (`PartitionKey` at `UpdateConsumerDefinition.cs:69-70`). No correctness impact. Acceptance rationale is sound. |
| T-46-06 | DoS-adjacent — Duplicate recovery effects (at-least-once) | accept | CLOSED | Accepted by design: at-least-once delivery semantics are an explicit requirement (REQUIREMENTS.md:54,72). INJECT (write-then-delete) and REINJECT (re-dispatch) are idempotent-enough for the recovery path. No dedup key introduced. Acceptance rationale is sound. |
| T-46-07 | DoS — Poison result message looping (Orchestrator) | mitigate | CLOSED | `StepCompletedConsumerDefinition.cs:40` — single-owner `endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))` on `orchestrator-result`. L1-miss is a graceful `return` (business-ack) at `TypedResultConsumer.cs:68-75` — it cannot loop. Three sibling definitions (`StepFailedConsumerDefinition`, `StepCancelledConsumerDefinition`, `StepProcessingConsumerDefinition`) are confirmed intentional no-ops (no `ConfigureConsumer` override body). |
| T-46-08 | Spoofing — Keeper-INJECT'd StepCompleted indistinguishable from direct | accept | CLOSED | This is the INTENDED ORCH-01 design requirement. `TypedResultConsumer.cs:24-26` documents this explicitly: "A Keeper-INJECT'd StepCompleted is byte-indistinguishable from a direct processor completion — both are the same record, land on the same queue, and are processed by the same StepCompletedConsumer." Both arrive on the trusted internal `orchestrator-result` queue. Acceptance rationale is sound. |
| T-46-09 | Tampering/correctness — Wrong-outcome advancement via the Outcome knob | mitigate | CLOSED | `TypedResultConsumer.cs:54` — `protected abstract StepOutcome Outcome { get; }` is compile-time per subclass. `StepCompletedConsumer.cs:25` pins `StepOutcome.Completed`; equivalent one-liners in each sibling. `TypedResultConsumer.cs:82` — `advancement.SelectNext(Outcome, completed, wf.Steps)` is pure int-match + dictionary lookup. No runtime status field to tamper. Unit tests (TypedResultConsumerFacts, Theory over all four subclasses) pin each subclass's routing per 46-04-SUMMARY.md. |
| T-46-10 | DoS — Poison message looping on keeper-recovery (endpoint) | mitigate | CLOSED | Same evidence as T-46-03: `UpdateConsumerDefinition.cs:47` registers the single-owner `UseMessageRetry(Immediate(Limit))`; transient `RecoveryGateTimeoutException` rides this same retry; after exhaustion the inherited `ConsolidatedErrorTransportFilter` dead-letters to skp-dlq-1. Grep over `src/Keeper/Recovery` confirms no `ConfigureError`/`SetQueueArgument` in live code (only doc-comment references). |
| T-46-11 | Tampering/correctness — Partition hash collision (duplicate of T-46-05) | accept | CLOSED | Same rationale as T-46-05. Non-crypto distribution hash; collision is a scheduling optimization, not an identity or correctness threat. The full 4-tuple L2 key is the identity anchor. Acceptance rationale is sound. |
| T-46-12 | Repudiation/correctness — Double-registered endpoint middleware | mitigate | CLOSED | Single-owner definition pattern verified: `UpdateConsumerDefinition.cs:47,57-61` is the sole site registering `UseMessageRetry` and all five `UsePartitioner<T>` calls on `keeper-recovery`. `ReinjectConsumerDefinition.cs:22`, `InjectConsumerDefinition.cs:22`, `DeleteConsumerDefinition.cs:22`, `CleanupConsumerDefinition.cs:22` all have intentional no-op `ConfigureConsumer` bodies (comment only). Grep over `src/Keeper/Recovery` confirms `UsePartitioner` and `UseMessageRetry` appear only in `UpdateConsumerDefinition.cs` live code. |

---

## Unregistered Threat Flags

None. No `## Threat Flags` section was present in SUMMARY.md files for this phase. All threats in scope were registered in the threat register.

---

## Accepted Risks Log

| Threat ID | Accepted Risk | Rationale |
|-----------|--------------|-----------|
| T-46-01 | KeeperReinject.Payload tamper on the wire | Internal bus, trusted producer (own processor). Payload schema validation is Phase-44 scope. No new external surface. |
| T-46-02 | Payload exposure on recovery envelope | Same broker already carried Payload on EntryStepDispatch. No new disclosure surface. |
| T-46-05 | Murmur3 partition hash collision | Non-crypto distribution hash; identity is the full 4-tuple L2 key. Collision is a scheduling optimization only. |
| T-46-06 | Duplicate recovery effects (at-least-once delivery) | Explicit requirement (REQUIREMENTS.md:54,72). INJECT/REINJECT are idempotent-enough; no dedup required. |
| T-46-08 | Keeper-INJECT'd StepCompleted indistinguishable from direct | INTENDED ORCH-01 design requirement. Both on the trusted internal result queue. |
| T-46-11 | Partition hash collision (T-46-05 duplicate) | Same rationale as T-46-05. |
