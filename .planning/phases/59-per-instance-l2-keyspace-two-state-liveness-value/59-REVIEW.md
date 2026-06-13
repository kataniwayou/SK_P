---
phase: 59-per-instance-l2-keyspace-two-state-liveness-value
reviewed: 2026-06-13T00:00:00Z
depth: standard
files_reviewed: 8
files_reviewed_list:
  - src/Messaging.Contracts/Identity/InstanceId.cs
  - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
  - src/Messaging.Contracts/Projections/LivenessStatus.cs
  - src/Messaging.Contracts/Projections/ProcessorLivenessEntry.cs
  - src/Messaging.Contracts/Projections/SchemaOutcome.cs
  - tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs
  - tests/BaseApi.Tests/Features/Orchestration/Projection/ProcessorLivenessEntryFacts.cs
  - tests/BaseApi.Tests/Identity/InstanceIdResolverFacts.cs
findings:
  critical: 0
  warning: 0
  info: 3
  total: 3
status: issues_found
---

# Phase 59: Code Review Report

**Reviewed:** 2026-06-13T00:00:00Z
**Depth:** standard
**Files Reviewed:** 8
**Status:** issues_found

## Summary

Phase 59 adds the per-instance L2 keyspace and the two-state liveness contract surface
(KEY-01 through KEY-04, STATE-01/02, D-04). The changes are pure contract/SoT additions in the
`Messaging.Contracts` leaf assembly: a shared `InstanceId.Resolve` SoT, two new `L2ProjectionKeys`
builders (`PerInstance`, `InstanceIndex`), a new `Unhealthy` const on `LivenessStatus`, a new
`SchemaOutcome` const class, and the new liveness-only `ProcessorLivenessEntry` record with a
`Create` invariant-enforcement factory. Tests are hermetic (no real stack) and pin the byte-exact
key strings, the JSON shape (no definition fields, load-bearing `[property: JsonPropertyName]`), the
`Create` status-derivation invariant, and the `InstanceId.Resolve` env precedence.

Overall assessment: clean. No bugs, no security issues. The code follows the established SoT/static-const
pattern, the invariant-enforcement design (`Create` as the only sanctioned construction path) is sound,
and the test coverage maps directly to the phase requirement IDs. All findings below are Info-level
observations about consistency and test-metadata, none of which affect correctness.

I verified the cross-references that the doc comments assert: `LivenessProjection` exists
(`src/Messaging.Contracts/Projections/LivenessProjection.cs`) and matches the referenced shape, and the
`InstanceId.Resolve` body is byte-identical to the existing observability copy in
`BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` (`POD_NAME → HOSTNAME →
MachineName → GUID(N)`), confirming the KEY-03 "one mechanism" claim. `ProcessorLivenessEntry` has no
consumers yet (writer/reader land in Phase 60/61), so this is contract-only surface area.

## Info

### IN-01: Unreachable GUID fallback in `InstanceId.Resolve` (matches locked observability copy)

**File:** `src/Messaging.Contracts/Identity/InstanceId.cs:17-21`
**Issue:** The final `?? Guid.NewGuid().ToString("N")` branch follows `Environment.MachineName`, which
is a non-nullable `string` and is effectively never null at runtime. The GUID fallback is therefore
statically unreachable as a coalescing branch. This is NOT a defect: the doc comment (lines 4-9) and the
phase intent (KEY-03) explicitly require this body to be byte-identical to the two existing observability
copies, and the existing copy carries the same dead-but-documented branch
(`ObservabilityServiceCollectionExtensions.cs:113` — "GUID is the documented final fallback (D-10)").
**Fix:** No change recommended — preserving byte-identical parity with the existing SoT copies is the
explicit design goal. Flagging only so the intentional dead branch is on record for the Phase-60+ dedup
that the doc comment defers.

### IN-02: `Step` key builder omits the `:D` format specifier its siblings use

**File:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:38`
**Issue:** `Step` interpolates `$"{Prefix}{workflowId}:{stepId}"` with bare Guids, while every other
Guid-keyed builder in the same file (`Root`, `PerInstance`, `InstanceIndex`, `ExecutionData`,
`MessageIndex`) uses an explicit `:D` specifier. Output is byte-identical because the default
`Guid.ToString()` is the "D" (hyphenated) format — confirmed by the pinning test
(`L2ProjectionKeysTests.cs:35-40`) — so there is no behavioral bug. This is pre-existing (Phase 22),
not introduced by Phase 59, but it is now the lone inconsistency in a file whose entire purpose is a
single canonical key shape.
**Fix:** For consistency with the surrounding builders, make the format explicit:
```csharp
public static string Step(Guid workflowId, Guid stepId) => $"{Prefix}{workflowId:D}:{stepId:D}";
```
Optional / cosmetic; defer if churn on a Phase-22 line is undesirable.

### IN-03: `L2ProjectionKeysTests` tagged `[Trait("Phase", "22")]` but now carries Phase-59 facts

**File:** `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs:14,49-64`
**Issue:** The class trait is `Phase 22`, but Phase 59 added the `PerInstance` (KEY-01),
`InstanceIndex` (KEY-02), and `PerInstance_Is_Prefixed_By_Its_InstanceIndex` facts (lines 49-64) plus the
`Instance` constant (line 20). Phase-based test selection/reporting on `Phase=59` will not surface these
new facts, and the class XML doc (lines 7-13) still describes only the Phase-22 scope. By contrast, the
two genuinely new test files are correctly tagged `[Trait("Phase", "59")]`.
**Fix:** Either add a second trait to the new facts (xUnit allows method-level `[Trait]`), e.g.
`[Trait("Phase", "59")]` on the KEY-01/KEY-02 facts, or note in the class doc that Phase 59 extended this
pinning suite. Low priority — affects test discoverability/reporting only, not test execution.

---

_Reviewed: 2026-06-13T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
