---
phase: 35-fault-intake-correlation
reviewed: 2026-06-05T00:00:00Z
depth: standard
files_reviewed: 13
files_reviewed_list:
  - src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs
  - src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs
  - src/Keeper/Consumers/FaultEntryStepDispatchConsumerDefinition.cs
  - src/Keeper/Consumers/FaultExecutionResultConsumer.cs
  - src/Keeper/Consumers/FaultExecutionResultConsumerDefinition.cs
  - src/Keeper/Program.cs
  - src/Messaging.Contracts/ExecutionLogScope.cs
  - tests/BaseApi.Tests/BaseApi.Tests.csproj
  - tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs
  - tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs
  - tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs
  - tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs
  - tests/BaseApi.Tests/Orchestrator/KeeperFaultIntakeE2ETests.cs
findings:
  critical: 0
  warning: 0
  info: 2
  total: 2
status: issues_found
---

# Phase 35: Code Review Report

**Reviewed:** 2026-06-05
**Depth:** standard
**Files Reviewed:** 13
**Status:** issues_found (Info only)

## Summary

Reviewed the Phase-35 fault-intake-correlation slice: the two new `Fault<T>`
observe-and-ack consumers (`FaultEntryStepDispatchConsumer`,
`FaultExecutionResultConsumer`) plus their definitions, the
`ExecutionLogScope.BuildState` refactor, Keeper's `Program.cs` wiring, and the
five test files (four hermetic, one RealStack-trait E2E).

The implementation is correct and well-targeted. Specifically verified against
the supplied domain context:

- **`ExecutionLogScope.BuildState` is a byte-identical refactor.** Compared
  against the pre-phase inline dict in `InboundExecutionScopeConsumeFilter.cs`
  at `1a71990`: same five fields in the same order, same `!= Guid.Empty` skip
  rule for the four Guids, same `!string.IsNullOrEmpty(EntryId)` skip, same
  `.ToString()` projection, and CorrelationId still absent. The filter now
  delegates to it with no behavioral change.
- **The manual `CorrelationKeys.LogScope` scope is correct and required.**
  `Fault<T>` implements neither `IExecutionCorrelated` nor `ICorrelated`, so the
  bus-wide `InboundCorrelationConsumeFilter` cannot recover the propagated id and
  mints a fresh Guid. Both consumers correctly re-open the scope from the
  double-unwrapped `context.Message.Message.CorrelationId.ToString()`, matching
  `CorrelationKeys.LogScope == "CorrelationId"` and the `D`-format used at the ES
  readback. Not redundant.
- **Exception text handling is safe.** Only `ex?.ExceptionType` and `ex?.Message`
  are surfaced, as structured template parameters (never interpolated into the
  template string). `StackTrace` is never read — the log-injection / attribute-DoS
  threat (T-35-04/05) is correctly avoided. The `Exceptions is { Length: > 0 }`
  guard safely handles an empty/null exception array.
- **Endpoint/retry topology is correct.** Both definitions bind the same stable
  durable `keeper-fault-recovery` endpoint; the retry middleware is registered by
  exactly one definition (the sibling's `ConfigureConsumer` is an intentional
  documented no-op), avoiding the per-endpoint double-registration pitfall.
- **Tests are sound.** The capturing-provider rig correctly asserts both the
  manual CorrelationId scope and the 5-id `BuildState` scope, including the
  Guid.Empty/empty-EntryId skip case. The RealStack E2E is correctly trait-gated
  out of the hermetic suite and brackets its `IBusControl` in try/finally with
  net-zero teardown.

No correctness or security defects found. Two low-priority Info observations follow.

## Info

### IN-01: `inner = context.Message.Message` is dereferenced without a null guard

**File:** `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs:33,36` and
`src/Keeper/Consumers/FaultExecutionResultConsumer.cs:34,37`

**Issue:** Both consumers do `var inner = context.Message.Message;` then
immediately dereference `inner.CorrelationId` and pass `inner` to
`ExecutionLogScope.BuildState(inner)`. If a `Fault<T>` ever arrived with a null
inner `Message`, this throws `NullReferenceException`. In practice MassTransit
populates `Fault<T>.Message` from the faulted message and the inner type is a
non-nullable record, so this is not a live bug — and because these are
observe-and-ack consumers on a retried endpoint, an NRE would route to
Immediate(N) retry then DLQ rather than silently losing data. Noted only as a
defensive-hardening observation, not a required change for Phase 35.

**Fix (optional):** Guard and early-ack a degenerate envelope, e.g.:
```csharp
var inner = context.Message.Message;
if (inner is null)
{
    logger.LogWarning("Keeper fault intake: {FaultType} arrived with null inner message — acking.",
        nameof(EntryStepDispatch));
    return Task.CompletedTask;
}
```
Equivalently, `ExecutionLogScope.BuildState` could `ArgumentNullException.ThrowIfNull(ec)`
to fail fast with a clear message instead of an opaque NRE.

### IN-02: Two consumers duplicate the unwrap + dual-scope + log body verbatim

**File:** `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs:31-45` and
`src/Keeper/Consumers/FaultExecutionResultConsumer.cs:32-46`

**Issue:** The two `Consume` bodies are identical except for the inner generic
type and the `nameof(...)` `FaultType` label — same double-unwrap, same manual
CorrelationId scope, same `BuildState` scope, same log template. This is
acceptable for two small observe-and-ack consumers, but Phase 36 adds re-inject
logic to both, at which point the duplication becomes a divergence risk (a fix
applied to one and not the other).

**Fix (optional, defer to Phase 36):** Extract a shared helper that takes the
inner `IExecutionCorrelated`, the first `ExceptionInfo`, and the `FaultType`
label and performs the dual-scope + log, e.g. a static
`KeeperFaultLog.Emit(logger, inner, ex, faultType)` in `Keeper.Consumers`. Keeping
it as-is for Phase 35 is reasonable given the deliberately minimal observe-and-ack
scope.

---

_Reviewed: 2026-06-05_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
