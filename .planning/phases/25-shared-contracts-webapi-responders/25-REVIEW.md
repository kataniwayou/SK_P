---
phase: 25-shared-contracts-webapi-responders
reviewed: 2026-06-01T00:00:00Z
depth: standard
files_reviewed: 15
files_reviewed_list:
  - src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
  - src/BaseApi.Service/Composition/ResponderMessaging.cs
  - src/BaseApi.Service/Features/Processor/Responders/GetProcessorBySourceHashConsumer.cs
  - src/BaseApi.Service/Features/Schema/Responders/GetSchemaDefinitionConsumer.cs
  - src/BaseApi.Service/Program.cs
  - src/Messaging.Contracts/ProcessorQueries.cs
  - src/Messaging.Contracts/ProcessorQueues.cs
  - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
  - src/Messaging.Contracts/Projections/LivenessStatus.cs
  - src/Messaging.Contracts/Projections/ProcessorProjection.cs
  - tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs
  - tests/BaseApi.Tests/Messaging/BaseApiCoreFirewallTests.cs
  - tests/BaseApi.Tests/Messaging/ProcessorResponderTests.cs
  - tests/BaseApi.Tests/Messaging/SchemaResponderTests.cs
  - tests/BaseApi.Tests/Projection/LivenessStatusTests.cs
findings:
  critical: 0
  warning: 0
  info: 3
  total: 3
status: issues_found
---

# Phase 25: Code Review Report

**Reviewed:** 2026-06-01T00:00:00Z
**Depth:** standard
**Files Reviewed:** 15
**Status:** issues_found

## Summary

Phase 25 delivers leaf shared contracts in `Messaging.Contracts` (relocated public `ProcessorProjection`, `L2ProjectionKeys.ExecutionData` builder, `LivenessStatus.Healthy`, six RPC request/response records, `ProcessorQueues` constants) plus two WebApi bus responders wired through two optional seams on `AddBaseApiMessaging`. The implementation is high quality and the architectural concerns called out in the task brief all check out cleanly:

- **Dependency firewall preserved.** `MessagingServiceCollectionExtensions` adds only MassTransit-typed hooks (`Action<IBusRegistrationConfigurator>`, `Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>`); Core never names a concrete consumer type. The concrete `BaseApi.Service` consumer types are bound only in `ResponderMessaging` (the Service layer). `BaseApiCoreFirewallTests` adds a reflection-based regression guard against `BaseApi.Service`/`BaseConsole.Core` references.
- **Publish-only default intact.** Both seams default `null`; the `null`-conditional invokes mean a hookless call is behaviorally identical to the Phase-19 join (no `AddConsumer`, no `ConfigureEndpoints` auto-naming).
- **Degraded health cap untouched.** `MinimalFailureStatus = HealthStatus.Degraded` block and the deliberate non-override of `o.Tags` are preserved.
- **Dual-response consumer logic correct.** Both consumers respond `*Found` on the hit path and translate the backing-service `NotFoundException` to `*NotFound` echoing the request key. Verified against the real `ProcessorService.GetBySourceHashAsync` (throws `NotFoundException` on null/empty/whitespace and on row-miss) and the `ProcessorReadDto` field order (`Id, InputSchemaId, OutputSchemaId, ConfigSchemaId` projection is correct). Both round-trips are proven by in-memory harness tests over real services + seeded EF-InMemory.

No bugs, security issues, or correctness defects found. The three Info items below are minor consistency/robustness observations, none blocking.

## Info

### IN-01: `RespondAsync` on the Found path sits inside the `NotFoundException` catch scope

**File:** `src/BaseApi.Service/Features/Processor/Responders/GetProcessorBySourceHashConsumer.cs:22-32`, `src/BaseApi.Service/Features/Schema/Responders/GetSchemaDefinitionConsumer.cs:20-29`

**Issue:** The `try` block wraps both the service call AND the success-path `RespondAsync<*Found>(...)`. If a `NotFoundException` were ever thrown by anything other than the intended lookup (e.g. a future refactor of the mapper, or a nested call inside `RespondAsync` serialization paths), it would be silently translated into a spurious `*NotFound` response, masking the real failure. Today this is purely theoretical — `RespondAsync` and the field projection do not throw `NotFoundException` — so there is no live bug.

**Fix:** Optionally tighten the catch to cover only the lookup, then respond outside the try:
```csharp
ProcessorReadDto p;
try { p = await processors.GetBySourceHashAsync(context.Message.SourceHash, context.CancellationToken); }
catch (NotFoundException)
{
    await context.RespondAsync<ProcessorIdentityNotFound>(new ProcessorIdentityNotFound(context.Message.SourceHash));
    return;
}
await context.RespondAsync<ProcessorIdentityFound>(
    new ProcessorIdentityFound(p.Id, p.InputSchemaId, p.OutputSchemaId, p.ConfigSchemaId));
```
Leaving as-is is acceptable given the current call surface; this is a defensive-style note only.

### IN-02: `LivenessStatus.Healthy` literal is not reused by `LivenessStatusTests`

**File:** `tests/BaseApi.Tests/Projection/LivenessStatusTests.cs:17`

**Issue:** The pin test asserts `Assert.Equal("Healthy", LivenessStatus.Healthy)`. This correctly pins the const to a literal (intentional — a contract-pin test should hardcode the expected byte string rather than reference the symbol under test, otherwise it would tautologically pass). Noting only that this is the intended pattern and matches the sibling `L2ProjectionKeysTests` style. No change needed.

**Fix:** None — this is the correct pin-test idiom. Recorded for completeness so it is not mistaken for a tautology defect in a later review.

### IN-03: `Root` uses `:D` format specifier while `Processor` uses bare interpolation

**File:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:33,37`

**Issue:** `Root` renders `$"{Prefix}{workflowId:D}"` (explicit `:D`) while `Processor` renders `$"{Prefix}{processorId}"` (bare). These are byte-identical because `Guid.ToString()` defaults to "D" — and the XML doc plus `Root_And_Processor_Are_ByteIdentical_For_Same_Guid` test explicitly assert this. The inconsistency is intentional/documented but a reader scanning the file could misread it as a discrepancy.

**Fix:** Optional — apply `:D` uniformly (`$"{Prefix}{processorId:D}"`) and to `Step` for self-documenting symmetry, or leave as-is since the doc comment already explains the equivalence. Cosmetic only; the existing tests already lock the byte output.

---

_Reviewed: 2026-06-01T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
