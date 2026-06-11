---
phase: 50-contracts-slot-array-l2-key-reshape
reviewed: 2026-06-11T00:00:00Z
depth: standard
files_reviewed: 11
files_reviewed_list:
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - src/Keeper/Program.cs
  - src/Keeper/Recovery/InjectConsumer.cs
  - src/Keeper/Recovery/RecoveryConsumerBase.cs
  - src/Keeper/Recovery/ReinjectConsumerDefinition.cs
  - src/Messaging.Contracts/KeeperInject.cs
  - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
  - tests/BaseApi.Tests/Contracts/KeeperContractTests.cs
  - tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs
  - tests/BaseApi.Tests/Keeper/RecoveryPartitionFacts.cs
  - tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs
findings:
  critical: 0
  warning: 1
  info: 3
  total: 4
status: issues_found
---

# Phase 50: Code Review Report

**Reviewed:** 2026-06-11T00:00:00Z
**Depth:** standard
**Files Reviewed:** 11
**Status:** issues_found

## Summary

Phase 50 is a contract-reshape phase. Plan 50-01 additively added `L2ProjectionKeys.MessageIndex`
and the `KeeperInject` A18 INJECT id-set (`EntryId`/`Data`/`DeleteEntryId`). Plan 50-02 deleted the
v4.0.0 Model-B surface (`KeeperUpdate`/`KeeperCleanup`/`CompositeBackup`/`BackupOptions`), re-homed
the single-owner recovery-endpoint definition onto `ReinjectConsumerDefinition`, and reduced the
`InjectConsumer` body and `ProcessorPipeline` Post mechanics to intentional shape-preserving no-op
stubs (real A18 bodies land in Phases 51/52 per the locked build order).

The change set is clean, internally consistent, and well-covered by tests. The contract records,
the `IKeeperRecoverable` partition marker, the deterministic partition-key derivation, and the
no-op recovery wiring all line up with their pinning tests. The intentional `InjectConsumer` and
`ProcessorPipeline` Post no-op stubs are by-design ("dark-but-compiling") and are NOT flagged.

No security issues, injection vectors, hardcoded secrets, or correctness bugs were found in the
changed surface. The findings below are one consistency/maintainability warning and three minor
info items. None block the phase.

## Warnings

### WR-01: `KeeperInject` is constructed via builder but never delivered, so the new A18 id-set is silently dropped on the wire until Phase 52

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:240-241`
**Issue:** `BuildInject` (the only constructor of `KeeperInject` in production) sets only the 5-id
base and `ExecutionId`. The three new A18 fields added in Plan 50-01 — `EntryId`, `Data`,
`DeleteEntryId` — are left at their defaults (`Guid.Empty`, `""`, `Guid.Empty`). When the
output-write exhausts and `SendKeeper(BuildInject(...))` fires, the `InjectConsumer` that receives
it is the no-op stub (`Task.CompletedTask`), so the empty id-set is harmless TODAY. But the field
is on the wire now: if the Phase-52 INJECT body lands before `BuildInject` is updated to populate
`EntryId`/`Data`/`DeleteEntryId`, the consumer will silently operate on empty values (write
`L2[Guid.Empty]`, delete `Guid.Empty`) instead of dead-lettering. This is the exact "additive field
added but producer not wired" seam the cross-phase build order creates.

This is a Warning rather than Info because the dark-but-compiling stub masks the gap: there is no
compile error and no test failure pinning that `BuildInject` must populate the A18 id-set, so the
regression is invisible until live recovery exercises it.

**Fix:** Either (a) add a one-line code comment at `BuildInject` explicitly stating the A18 id-set
is intentionally unpopulated until Phase 52 wires the producer (mirrors the existing Phase-50
markers at lines 140-141 and 150-151), or (b) add a pinning test asserting that once the Phase-52
INJECT body exists, `BuildInject` populates `EntryId`/`Data`/`DeleteEntryId`. Recommended minimal
change — a comment so the Phase-52 author sees the producer-side TODO at the call site:
```csharp
private static KeeperInject BuildInject(EntryStepDispatch d, ProcessItem item) =>
    // Phase-50: A18 id-set (EntryId/Data/DeleteEntryId) left at defaults; the INJECT consumer is a
    // no-op stub until Phase 52, which must also populate these here before the body reads them.
    new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = item.ExecutionId };
```

## Info

### IN-01: `L2ProjectionKeys.Step` / `Processor` use bare interpolation while `Root` / `ExecutionData` / `MessageIndex` use the explicit `:D` specifier

**File:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:36-38`
**Issue:** `Root` (line 34), `ExecutionData` (line 42), and the newly added `MessageIndex` (line 48)
all use the explicit `{guid:D}` specifier, while `Step` (line 36) and `Processor` (line 38) use bare
`{guid}` interpolation. The class doc-comment (lines 11-13) calls out that `Root` "makes this
explicit with the `:D` format specifier (byte-identical to a bare interpolation)." The output is
identical because default `Guid.ToString()` is the "D" format, so this is purely stylistic — but the
mixed convention is a latent foot-gun: a future reader could "normalize" one builder to "N" format
and silently desynchronize the writer/reader the file exists to keep in lockstep. `Step`/`Processor`
are pre-existing (not Phase-50 lines), so this is noted for awareness, not as a phase regression.
**Fix:** For consistency with the file's stated convention, apply `:D` to `Step` and `Processor`
(output-identical, no test impact): `$"{Prefix}{workflowId:D}:{stepId:D}"` and
`$"{Prefix}{processorId:D}"`.

### IN-02: `MessageIndex` is added to the contract but has no production caller yet

**File:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:48`
**Issue:** `MessageIndex` is added additively (Plan 50-01) and is exercised only by
`L2ProjectionKeysTests` and `ModelBContractsRetiredFacts`. It has no production caller in the
reviewed surface (the slot-array allocation that uses it lands in Phase 51). This is expected for a
contract-first reshape phase and matches the documented build order — recorded only so the unused
public builder is not mistaken for dead code. No action required this phase.
**Fix:** None — wired in Phase 51 per plan.

### IN-03: `InjectConsumer` retains the full `RecoveryConsumerBase` ctor-injection surface while its body is a no-op

**File:** `src/Keeper/Recovery/InjectConsumer.cs:16-22`
**Issue:** `InjectConsumer` still ctor-injects `redis`, `sendProvider`, `gate`, `retryOptions`, and
`recoveryOptions` through the base, but its `HandleAsync` is `Task.CompletedTask` and uses none of
them directly. This is correct and intentional — the shape-preserving stub must keep the base
contract so Phase 52 can fill in the body without touching DI wiring or `Program.cs`, and the gate
wait in `RecoveryConsumerBase.Consume` still runs. Recorded only to confirm the unused-dependency
surface is by-design, not an oversight. No action required.
**Fix:** None — intentional dark-but-compiling stub; the dependencies are consumed by the Phase-52 body.

---

_Reviewed: 2026-06-11T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
