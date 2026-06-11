---
phase: 53-model-b-teardown
reviewed: 2026-06-11T00:00:00Z
depth: standard
files_reviewed: 14
files_reviewed_list:
  - src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
  - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
  - src/Keeper/Recovery/RecoveryEndpointBinder.cs
  - src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs
  - src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs
  - src/Orchestrator/Consumers/ResumeAllConsumerDefinition.cs
  - src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs
  - src/Orchestrator/Consumers/StepCompletedConsumerDefinition.cs
  - src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs
  - tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs
  - tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs
  - tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs
  - tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs
findings:
  critical: 0
  warning: 1
  info: 4
  total: 5
status: issues_found
---

# Phase 53: Code Review Report

**Reviewed:** 2026-06-11
**Depth:** standard
**Files Reviewed:** 14 (15 in config — `ModelBContractsRetiredFacts.cs` is counted once; all 14 distinct source paths reviewed plus their cross-referenced siblings)
**Status:** issues_found

## Summary

This is a clean, well-executed teardown of the legacy Model-B retry/`_error` transport wiring. The intended behavior — removing outer-bus `UseMessageRetry` from orchestrator consumer definitions, removing the dispatch keep-latch, and relocating the `ConfigureError` filter pair to keeper-local — is fully and correctly carried out. The full solution builds with 0 warnings / 0 errors, all consumer-definition DI registrations still resolve (MassTransit instantiates the now-parameterless `ConsumerDefinition` types via the container), the keeper-local `GenerateFaultFilter` + `ConsolidatedErrorTransportFilter` pair compiles and resolves, and the in-code `RetryLoop` ownership in `ProcessorPipeline` is intact. The new `ModelBContractsRetiredFacts` source-scan guards (FACT 6/7/8) correctly assert the post-teardown end-state.

No bugs, security issues, resource/lifetime defects, or broken DI were found in the in-scope files. The findings below are all teardown-completeness / documentation-drift issues: the retry removal left several **stale cross-reference comments in sibling files** that still describe a retry-ownership model that no longer exists, plus one **now-dead DI registration** (`RetryOptions` in the orchestrator) whose anchoring comment is now false. None of these affect runtime correctness, but they are exactly the "dangling reference" class this teardown review is meant to surface, and a reader reasoning about the retry posture from these comments would be actively misled.

Per the phase brief, the **absence** of retry/error-transport wiring is the intended behavior and is NOT flagged as a defect.

## Warnings

### WR-01: Dead `RetryOptions` DI registration left behind by the orchestrator retry teardown

**File:** `src/Orchestrator/Program.cs:28-29` (out of review scope, but the dead registration is a direct consequence of the in-scope changes to all five orchestrator `*ConsumerDefinition` files)
**Issue:** Phase 53 changed `PauseAllConsumerDefinition`, `PauseWorkflowConsumerDefinition`, `StartOrchestrationConsumerDefinition`, and `StopOrchestrationConsumerDefinition` to parameterless constructors, removing every `IOptions<RetryOptions>` injection in the orchestrator. After this teardown, `RetryOptions` is no longer consumed by ANY orchestrator component (`git grep RetryOptions` in `src/Orchestrator` now matches only doc-comments and the registration line itself). Yet `Program.cs:29` still registers it:

```csharp
// RetryOptions defaults (Immediate(3)). The 3 orchestrator ConsumerDefinitions inject IOptions<RetryOptions>.
builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));
```

The comment ("The 3 orchestrator ConsumerDefinitions inject `IOptions<RetryOptions>`") is now factually false — zero definitions inject it — and the registration is dead. This is the dangling-reference / incomplete-teardown class the review targets. It is a Warning rather than Info because it is a behavioral lie about the wiring that survives in production config code, not merely a stale doc-comment.
**Fix:** Remove the now-dead registration and its comment from `Program.cs` (verify no other orchestrator component reads `RetryOptions` first — current grep shows none):

```csharp
// (delete lines 28-29) — Phase-53 removed all orchestrator UseMessageRetry; RetryOptions
// is no longer consumed on the orchestrator side.
```

If a future phase intends to keep the `"Retry"` config section reserved, replace the false comment with an explicit "registered for forward-compat, currently unconsumed (Phase-53)" note instead of leaving the misleading one.

## Info

### IN-01: Stale "retry owned by StepCompletedConsumerDefinition" comments in the three sibling result definitions

**File:** `src/Orchestrator/Consumers/StepFailedConsumerDefinition.cs:11-14,24`; `StepCancelledConsumerDefinition.cs:24`; `StepProcessingConsumerDefinition.cs:24` (siblings of the in-scope `StepCompletedConsumerDefinition.cs`)
**Issue:** `StepCompletedConsumerDefinition` (in scope) had its `UseMessageRetry` removed and is now an intentional no-op. But its three siblings still carry comments such as "endpoint-level retry for `orchestrator-result` is owned solely by `StepCompletedConsumerDefinition`" and "Intentional no-op: endpoint-level retry owned by `StepCompletedConsumerDefinition` (Pitfall 4)". No definition on the `orchestrator-result` endpoint registers retry anymore, so these "owned by" references point at a behavior that no longer exists.
**Fix:** Update the three sibling comments to match the new posture, e.g. "Intentional no-op: NO bus retry on `orchestrator-result` (Phase-53 D-01) — send-exhaust throws → broker redelivery." Functionally inert; documentation-only.

### IN-02: Stale "retry owned by Pause def" comment in ResumeWorkflowConsumerDefinition

**File:** `src/Orchestrator/Consumers/ResumeWorkflowConsumerDefinition.cs:9-11,25` (sibling of in-scope `PauseWorkflowConsumerDefinition.cs`)
**Issue:** `PauseWorkflowConsumerDefinition` (in scope) lost its `UseMessageRetry`, but `ResumeWorkflowConsumerDefinition` still states "It deliberately does NOT register a second `UseMessageRetry`: ... the Pause definition already owns retry for this shared endpoint (RESEARCH §5)" and line 25 "retry owned by Pause def on the shared endpoint". There is no longer any retry owner on `orchestrator-pauseresume`. Note the in-scope `ResumeAllConsumerDefinition.cs` was correctly updated for the same situation — this sibling was missed.
**Fix:** Align `ResumeWorkflowConsumerDefinition` with the already-corrected `ResumeAllConsumerDefinition` wording: "serial; no bus retry on this endpoint (Phase-53 D-01)."

### IN-03: ConsolidatedErrorTransportFilter comment still attributes skp-dlq-1 declaration to a ReceiveEndpoint

**File:** `src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs:51-58` (not in review scope; cross-referenced by the in-scope `MessagingServiceCollectionExtensions.cs` and `RecoveryEndpointBinder.cs`)
**Issue:** The filter comment says the skp-dlq-1 queue "is declared exactly ONCE ... by the **ReceiveEndpoint** in MessagingServiceCollectionExtensions." The in-scope `MessagingServiceCollectionExtensions.cs` (lines 88-98, with the explanatory block at 63-87) now declares skp-dlq-1 via a passive `Publish<ConsolidatedFault>` → `BindQueue` publish-topology declaration, explicitly NOT a `ReceiveEndpoint` (that change predates this phase but the in-scope file's own comments document it). The filter's "by the ReceiveEndpoint" wording is therefore stale and contradicts the now-authoritative topology comment in the file this phase touched. Behavior is correct (the filter still sends to `exchange:skp-dlq-1`); only the explanatory reference is wrong.
**Fix:** Update the `ConsolidatedErrorTransportFilter` comment to "...declared exactly ONCE — as a passive publish-topology BindQueue (NOT a ReceiveEndpoint) — in MessagingServiceCollectionExtensions."

### IN-04: RecoveryEndpointBinder import comment groups GenerateFaultFilter under MassTransit.Middleware without confirming the namespace

**File:** `src/Keeper/Recovery/RecoveryEndpointBinder.cs:3`
**Issue:** The `using MassTransit.Middleware;` comment was extended to "... + GenerateFaultFilter", implying `GenerateFaultFilter` lives in `MassTransit.Middleware`. The build confirms the type resolves (so this is not a compile defect), but the in-repo `ConsolidatedErrorTransportFilter` lives in `BaseConsole.Core.Messaging` (imported separately on line 1), and `GenerateFaultFilter` is a MassTransit framework type whose exact namespace is version-sensitive. The annotation is plausibly correct but unverified-by-comment; if a future MassTransit bump relocates it, the grouped comment becomes a misleading breadcrumb.
**Fix:** Low priority. Optionally pin the source explicitly (e.g. confirm and note "GenerateFaultFilter — MassTransit.Middleware, 8.5.5") or drop the type name from the `using` comment and rely on the call-site comment at lines 102-112, which already explains the filter pair.

---

_Reviewed: 2026-06-11_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
