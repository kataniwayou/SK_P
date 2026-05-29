---
phase: 13-orchestrationservice-split-l3-fetch-l1-build
reviewed: 2026-05-29T00:00:00Z
depth: standard
files_reviewed: 14
files_reviewed_list:
  - src/BaseApi.Service/Features/Orchestration/Loading/IWorkflowGraphLoader.cs
  - src/BaseApi.Service/Features/Orchestration/Loading/WorkflowGraphLoader.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/IRedisProjectionWriter.cs
  - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs
  - src/BaseApi.Service/Features/Orchestration/Validation/CycleDetector.cs
  - src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs
  - src/BaseApi.Service/Features/Orchestration/Validation/SchemaEdgeValidator.cs
  - src/BaseApi.Service/Features/Orchestration/WorkflowGraphSnapshot.cs
  - src/BaseApi.Service/Properties/AssemblyInfo.cs
  - tests/BaseApi.Tests/Features/Orchestration/StartCleanupFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/WorkflowGraphLoaderFacts.cs
findings:
  critical: 0
  warning: 1
  info: 3
  total: 4
status: issues_found
---

# Phase 13: Code Review Report

**Reviewed:** 2026-05-29T00:00:00Z
**Depth:** standard
**Files Reviewed:** 14
**Status:** issues_found

## Summary

Phase 13 splits `OrchestrationService` into a thin pipeline orchestrator plus a real
L3-to-L1 graph loader (`WorkflowGraphLoader`), four no-op validator/writer seams, a
transient disposable `WorkflowGraphSnapshot`, and supporting DI wiring and tests. The
code is clean, well-documented, and the design decisions (internal ctor + factory
registration, `using`-declaration disposal on the throw path, `List`-keyed visited guard
for cycle termination) are correctly implemented and matched by the tests.

Cross-referenced all junction entity field names (`StepNextSteps.StepId/NextStepId`,
`WorkflowEntrySteps.WorkflowId/StepId`, `WorkflowAssignments.WorkflowId/AssignmentId`),
the `ProcessorEntity` nullable schema-FK names, the nullable `StepReadDto.NextStepIds`
target of the `with { ... }` enrichment, and the `WorkflowIdsValidator` cascade — all
consistent with the loader and orchestrator usage. The disposal contract is verified by
`StartCleanupFacts` and the BFS termination by `WorkflowGraphLoaderFacts`.

No critical issues. One warning concerns the O(n) `List.Contains` membership guard in the
BFS (a correctness-adjacent DoS surface, not pure performance). Three informational items
relate to a benign record-equality footgun, a redundant DI registration in a test, and a
duplicate-load edge case in the BFS.

## Warnings

### WR-01: BFS visited/dedup guard uses O(n) `List.Contains` on a graph-sized list

**File:** `src/BaseApi.Service/Features/Orchestration/Loading/WorkflowGraphLoader.cs:142,151,159,171`
**Issue:** The `visited` guard is a `List<Guid>` and membership is tested with
`visited.Contains(id)` inside `LoadStepsBreadthFirstAsync` (lines 151 and 171). Each
`Contains` is O(n) over a list that grows to the size of the reachable step set, making
the per-wave filtering O(n²) over the whole graph. The code comments and the linked
plan/memory note explicitly justify the `List` choice as REQ-mandated ("keyed on StepId
— NOT a `HashSet`") and the test `LoadL1Async_Terminates_ForCyclicGraph` confirms the
guard does terminate the BFS on a cycle, so this is correct, not a bug.

However, this method is the documented DoS-mitigation surface (T-13-05 / T-13-10). On a
large but legitimate workflow graph the O(n²) scan is precisely the kind of input-driven
quadratic cost a hostile or accidentally-huge graph could exploit, which crosses from
"pure performance (out of scope)" into a denial-of-service correctness concern. Pure
algorithmic performance is out of v1 scope, but the security framing warrants flagging.

**Fix:** If the REQ truly mandates a `List` for the *return/ordering* semantics, keep the
`List` for ordering but add a parallel `HashSet<Guid>` purely for the membership test, so
termination and dedup stay O(1) per id without changing the documented `List`-keyed
contract:
```csharp
var visited = new List<Guid>();            // ordering / REQ-keyed surface
var visitedSet = new HashSet<Guid>();      // O(1) membership only
...
var toLoad = currentWave.Where(id => !visitedSet.Contains(id)).Distinct().ToList();
...
foreach (var id in loadedIds) { visited.Add(id); visitedSet.Add(id); }
...
currentWave = nextRows.Select(j => j.NextStepId)
    .Where(id => !visitedSet.Contains(id))
    .Distinct().ToList();
```
If the `List` requirement is in fact arbitrary and only termination matters, prefer a
plain `HashSet<Guid>` for `visited`. Either way, confirm the REQ intent before changing
since the comment states the `List` choice is explicit.

## Info

### IN-01: `WorkflowGraphSnapshot.Logger` participates in record value-equality

**File:** `src/BaseApi.Service/Features/Orchestration/WorkflowGraphSnapshot.cs:32`
**Issue:** `Logger` is a *positional* record parameter, so despite the XML doc stating it
"is NOT a data member" and "does not participate in value-equality," the compiler-generated
`Equals`/`GetHashCode` for a positional record DO include positional parameters. The
`init`-only dictionary properties (lines 34-38) are the members that are correctly excluded
from equality (declared-property, not positional). The doc comment has the inclusion
backwards. In practice this is harmless — snapshots are never compared or used as dictionary
keys anywhere in the codebase (verified across all 12 files referencing the type) — so this
is informational only, but the comment is misleading for future maintainers.

**Fix:** Either correct the comment to state that `Logger` *does* participate in the
generated equality (and that this is acceptable because snapshots are never compared), or,
if value-equality is genuinely undesired for a transient disposable, drop the record's
positional equality by making `Logger` a non-positional constructor-assigned field on a
plain `sealed class`, or seal equality off. Given the type is never compared, the
lowest-risk action is fixing the comment.

### IN-02: BFS records `nextStepLookup` keyed only on loaded steps; children-of-already-visited handled implicitly

**File:** `src/BaseApi.Service/Features/Orchestration/Loading/WorkflowGraphLoader.cs:162-172`
**Issue:** `nextRows` is queried for `loadedIds` (the steps loaded in the current wave),
and `nextStepLookup` is populated only for those. Because a step is loaded exactly once
(on first visit) and its outgoing junction rows are fetched in that same wave, every
loaded step gets its complete child set recorded — the logic is correct. Worth noting for
clarity: if a `StepNextSteps` row pointed at a `NextStepId` with no matching `StepEntity`
(a dangling junction FK), that child id would still appear in a parent's `NextStepIds`
enrichment but would be absent from `snapshot.Steps`. The `OnDelete(Restrict)` FK on the
junction (per `StepNextSteps` docs) prevents this in production, so no fix is required;
flagging only so the invariant is explicit.

**Fix:** None required. Optionally add a guard/assertion in a future validator phase that
every `NextStepId` resolves to a loaded step, if defense-in-depth against schema drift is
desired.

### IN-03: Redundant `AddScoped<WorkflowGraphLoader>()` registration in test

**File:** `tests/BaseApi.Tests/Features/Orchestration/StartCleanupFacts.cs:122`
**Issue:** `services.AddScoped<WorkflowGraphLoader>()` registers the concrete loader as
itself so the recording wrapper can resolve it on line 126. This is functionally fine, but
the production DI already registers `AddScoped<IWorkflowGraphLoader, WorkflowGraphLoader>()`
(interface-keyed only, so the concrete-self resolution would otherwise fail) — the extra
line is the deliberate and necessary bridge. It reads as possibly-redundant at a glance
because the interface mapping above it also points at `WorkflowGraphLoader`. Minor
readability note only.

**Fix:** Optionally add a one-line comment clarifying that the concrete-type registration
is required because production only registers the interface mapping, so
`GetRequiredService<WorkflowGraphLoader>()` on line 126 would otherwise throw. No behavioral
change needed.

---

_Reviewed: 2026-05-29T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
