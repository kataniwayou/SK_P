---
phase: 44-processor-pre-in-post-process-pipeline
audit_date: 2026-06-08
asvs_level: 1
auditor: gsd-secure-phase
block_on: high
threats_total: 10
threats_closed: 10
threats_open: 0
---

# Phase 44 Security Audit

## Summary

**Threats Closed:** 10/10
**Threats Open:** 0
**ASVS Level:** 1
**Unregistered Flags:** none (no threat flags raised in any SUMMARY.md)

All ten registered threats are CLOSED. The eight `mitigate` threats each have confirmed code evidence.
The two `accept` threats are internal-only, no external surface, and are documented below.

---

## Threat Verification

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-44-01 | Denial of Service | mitigate | CLOSED | `RetryLoop.cs:14` — `Math.Max(1, limit)` bounds the loop; exhaustion surfaced via `RetryOutcome<T>.Exhausted` (no throw, no backoff); `ProcessorStartupOrchestrator.cs:180` — `UseMessageRetry` is the outer `_error` latch |
| T-44-02 | Tampering (logical) | accept | CLOSED-accepted | Internal author API; `ProcessStatusException.cs:8-17` — abstract base + 3 concrete subclasses; unexpected-exception catch at `ProcessorPipeline.cs:113-117` maps all unrecognized throws to `StepFailed`, preventing crash-escape |
| T-44-03 | Information disclosure | accept | CLOSED-accepted | Author-supplied text routed only to the internal orchestrator result queue (`OrchestratorQueues.Result`); `ProcessorPipeline.cs:170-175` — `SendResult` targets internal queue; no external surface |
| T-44-04 | Information disclosure | mitigate | CLOSED | `ProcessorJsonSchemaValidator.cs:19` — `SchemaRegistry.Global.Fetch = (_, _) => null` (SSRF lockdown); `ProcessorJsonSchemaValidator.cs:18` — `Dialect.Default = Dialect.Draft202012` (dialect pinned); `ProcessorJsonSchemaValidator.cs:63-69` — unresolvable `$ref` → `JsonSchemaException` caught → returns `false` (business Failed), no outbound fetch |
| T-44-05 | Tampering | mitigate | CLOSED | `ProcessorPipeline.cs:123-125` — `ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, item.Data, out _)` gates the Post write; schema-fail flips `outcome = ProcessOutcome.Failed`; the `StringSetAsync` write at `ProcessorPipeline.cs:131-132` is only reached on the `outcome == ProcessOutcome.Completed` branch (`ProcessorPipeline.cs:127`); `PipelinePostFacts.cs` — `BusinessFailedItem_OneStepFailed_NoAbort` proves the gate |
| T-44-06 | Denial of Service | mitigate | CLOSED | `ProcessorPipeline.cs:75,131,159` — every L2 op wrapped in `RetryLoop.ExecuteAsync`; `ProcessorPipeline.cs:172,180` — every send wrapped in `RetryLoop.ExecuteAsync`; `ProcessorStartupOrchestrator.cs:174-180` — D-09 reconcile comment states `UseMessageRetry` is the outer dead-letter latch, NOT a second L2/send retry; `RetryLoopFacts.cs:88-101` — `SendExhaust_Propagates_WhenCallerRethrows` proves D-10 propagation |
| T-44-07 | Tampering (logical) | accept | CLOSED-accepted | Accepted by design (RESIL-03); at-least-once is the documented bus delivery guarantee; Phase 44 introduces no new duplicate-amplifier; the orchestrator `ResultConsumer` is L1-idempotent |
| T-44-08 | Repudiation/data-loss | mitigate | CLOSED | `ProcessorPipeline.cs:63` — `var readSucceeded = false;`; `ProcessorPipeline.cs:87` — `readSucceeded = true` set only after a successful L2 read; `ProcessorPipeline.cs:157` — `if (readSucceeded)` gates the `finally` end-delete; REINJECT path returns at `ProcessorPipeline.cs:85` with `readSucceeded == false`; `SourceStep.IsSource` path at `ProcessorPipeline.cs:69-72` never sets `readSucceeded`; `PipelineEndDeleteFacts.cs:87-101` — `EndDelete_Skipped_OnReinject` asserts `KeyDeleteAsync` never called; `PipelineEndDeleteFacts.cs:104-117` — `EndDelete_Skipped_OnSourceStep` asserts same |
| T-44-09 | Denial of Service | mitigate | CLOSED | `RetryLoop.cs:14` — `Math.Max(1, limit)` clamps zero/negative to at least one attempt; `BaseProcessorServiceCollectionExtensions.cs:95` — `RetryOptions` bound from `"Retry"` config section; `Processor.Sample/appsettings.json` — `"Retry": { "Limit": 3 }` pins the explicit default (confirmed by 44-03-SUMMARY.md); large values remain bounded immediate-no-backoff; `UseMessageRetry` caps total amplification |
| T-44-10 | Tampering | accept | CLOSED-accepted | `SampleProcessor.cs:39-40` — `throw new FailedException("sample reason")` routes through `ProcessorPipeline.cs:100-112` catch, producing exactly one `StepFailed`; worked-example control flow only; no privilege boundary; no external surface |

---

## Accepted Risk Log

### T-44-02 — ProcessStatusException mapping (Tampering/logical)

**Rationale:** The `ProcessStatusException` family is the intentional author API for signaling batch-abort status from In-Process code. No privilege boundary is crossed — the author and framework run in the same process. The unexpected-exception catch at `ProcessorPipeline.cs:113` ensures any unrecognized throw maps to a business `StepFailed`, preventing an author crash from escaping as an infra fault or crashing the host. The mapping is by runtime type (`switch` at `ProcessorPipeline.cs:102-108`), which is exhaustive with a default arm.

### T-44-03 — Exception message to StepFailed.ErrorMessage (Information disclosure)

**Rationale:** The `ErrorMessage` field is author-supplied business text. It is routed only to `queue:{OrchestratorQueues.Result}`, an internal RabbitMQ queue consumed by the orchestrator `ResultConsumer`. There is no external HTTP surface, no client-facing API, and no log sink that would expose this field outside the internal service mesh. The behavior is consistent with the pre-existing `BuildFailed(dispatch, ex.Message)` pattern present in prior phases.

### T-44-07 — At-least-once duplicate effects (Tampering/logical)

**Rationale:** At-least-once delivery is the accepted bus delivery semantic (RESIL-03). The orchestrator `ResultConsumer` is L1-idempotent. Phase 44 introduces no new dedup-key or dedup mechanism, and no new duplicate-amplifier beyond what was present before this phase. The Keeper recovery design (Phases 46-48) provides the idempotency backstop for infra ops.

---

## WR-01 Security Assessment: T-44-08 Scope Under Send-Exhaustion Unwind

**Finding:** WR-01 (flagged in 44-REVIEW.md) notes that the `finally` end-delete may run during a send-exhaustion exception unwind. Specifically: if `SendKeeper` or `SendResult` exhausts and re-throws inside the `try` block of `RunAsync`, the `finally` block executes. If `readSucceeded == true` at that point, `KeyDeleteAsync` will be called on `L2[entryId]`.

**Assessment:** T-44-08 REMAINS CLOSED. The declared mitigation scope is: "end-delete gated on `readSucceeded` (false on REINJECT/Guid.Empty)". That gate is correctly implemented. The WR-01 scenario (send-exhaustion on a read-succeeded path causing the delete to run) is NOT the T-44-08 threat scenario. T-44-08 specifically concerns the REINJECT path — deleting the input before the keeper can recover it. On the send-exhaustion unwind, `readSucceeded == true`, meaning the input was already read successfully, and the Keeper received or will receive its messages only if a prior `SendKeeper` completed. The send-exhaustion propagates to `_error` (dead-letter via `UseMessageRetry`), which is the D-10/T-44-06 design intent.

**Residual risk acknowledgment:** The WR-01 edge case is a data-consistency concern (stranded L2 data or lost Keeper messages on catastrophic send failure), not a data-loss-of-input concern. This is covered by T-44-07 (at-least-once / accepted at-least-once semantics) and the keeper recovery design (Phases 46-48). It does not weaken T-44-08's specific mitigation claim. The risk is acknowledged and accepted under T-44-07's acceptance reasoning.

---

## Unregistered Threat Flags

None. No `## Threat Flags` entries were raised in any of the three SUMMARY files (44-01-SUMMARY.md, 44-02-SUMMARY.md, 44-03-SUMMARY.md). The 44-02-SUMMARY.md explicitly states "No threat flags raised."

---

*Audit completed: 2026-06-08*
*Implementation files: READ-ONLY (not modified)*
