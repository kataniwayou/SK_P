---
phase: 14-validation-gates-dfs-schema-edge-payload-config-schema
reviewed: 2026-05-29T00:00:00Z
depth: standard
files_reviewed: 16
files_reviewed_list:
  - src/BaseApi.Core/DependencyInjection/ErrorHandlingServiceCollectionExtensions.cs
  - src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs
  - src/BaseApi.Service/Program.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationValidationExceptionHandler.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs
  - src/BaseApi.Service/Features/Schema/JsonSchemaConfig.cs
  - src/BaseApi.Service/Features/Schema/SchemaDtoValidator.cs
  - src/BaseApi.Service/Features/Orchestration/Validation/CycleDetector.cs
  - src/BaseApi.Service/Features/Orchestration/Validation/SchemaEdgeValidator.cs
  - src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs
  - tests/BaseApi.Tests/Features/Orchestration/CycleDetectionFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/MissingStepFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/SchemaEdgeFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/PayloadConfigSchemaFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/ValidationOrderFacts.cs
findings:
  critical: 0
  warning: 3
  info: 4
  total: 7
status: issues_found
---

# Phase 14: Code Review Report

**Reviewed:** 2026-05-29T00:00:00Z
**Depth:** standard
**Files Reviewed:** 16
**Status:** issues_found

## Summary

Reviewed the Phase 14 validation-gates implementation: the four orchestration
validation gates (cycle DFS, missing-step, schema-edge, payload↔config-schema),
the single `OrchestrationValidationException` + its 422 handler, the split-out
fallback handler wiring, and the shared `JsonSchemaConfig` SSRF lockdown. The
code is well-structured, the iterative (non-recursive) two-set DFS is correct,
the SSRF defense-in-depth is sound, and the DI/handler-ordering is carefully
documented. No critical security or correctness defects were found.

The findings below are robustness gaps and maintainability notes. The three
warnings concern reachability scope differences between gates, an unguarded
`JsonSchema.FromText` parse, and a snapshot-mutation/dispose-ordering edge in
the cycle detector that could in principle surface a confusing chain on
pathological input. None block the phase; each is a small hardening opportunity.

## Warnings

### WR-01: `JsonSchema.FromText` parse is unguarded — a non-schema Definition row would surface as HTTP 500, not a domain 422

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs:47`
**Issue:** `schema = JsonSchema.FromText(schemaDto.Definition);` is called with no
try/catch. The sibling create/update validators (`SchemaDtoValidator.cs`) wrap
parsing in `try { JsonDocument.Parse(...) } catch (JsonException ...)` precisely
because a malformed Definition is a real possibility. Here the assumption is that
every persisted `Schema.Definition` already passed the create-time meta-schema
gate, so it must be parseable. That holds today, but it couples this gate's
correctness to an invariant enforced elsewhere: a Definition written by a future
code path that bypasses `SchemaCreateDtoValidator`, or a row mutated directly in
the DB, would throw a raw `Json.Schema`/`JsonException` here. That exception is
not an `OrchestrationValidationException`, so the domain handler bails (Pitfall 6)
and it falls through to `FallbackExceptionHandler` → HTTP 500 during a validation
Start that should arguably be a 422/clear error. Note the payload parse on line 54
IS inside a try (though only `finally`-disposed, see WR-02), but the schema parse
on line 47 is outside any guard.
**Fix:** Wrap the schema parse and translate a parse failure into a domain error
(or a deliberately-chosen status), e.g.:
```csharp
if (!schemaCache.TryGetValue(cfgId.Value, out var schema))
{
    if (!snapshot.Schemas.TryGetValue(cfgId.Value, out var schemaDto)) continue;
    try
    {
        schema = JsonSchema.FromText(schemaDto.Definition);
    }
    catch (Exception ex) when (ex is JsonException or Json.Schema.JsonSchemaException)
    {
        throw OrchestrationValidationException.PayloadConfigSchema(
            assignment.Id,
            new[] { $"Config schema '{cfgId.Value}' is not a valid JSON Schema." });
    }
    schemaCache[cfgId.Value] = schema;
}
```
If a stored-schema-corruption is genuinely "impossible by construction" and a 500
is the intended signal, add a one-line comment stating that so a future reader
does not mistake the omission for an oversight (it reads as inconsistent next to
the guarded create-side path).

### WR-02: `JsonDocument.Parse(assignment.Payload)` parse failure is not translated — only disposed

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs:54`
**Issue:** The `try { ... } finally { payloadDoc?.Dispose(); }` block guarantees
disposal but does NOT catch a `JsonException` from `JsonDocument.Parse`. If an
`Assignment.Payload` is not well-formed JSON (e.g. an empty string, or a value
that the Assignment create-side does not constrain to valid JSON), `Parse` throws
`JsonException`, which — like WR-01 — is not an `OrchestrationValidationException`
and lands as HTTP 500. A malformed payload is conceptually a payload-conformance
failure and should produce the same 422 envelope as a schema-mismatch. Whether
this is reachable depends on whether the Assignment create-side already enforces
"Payload must be valid JSON"; that invariant is not visible in the reviewed files.
**Fix:** Catch `JsonException` around the parse and surface it through the same
gate exception:
```csharp
try
{
    payloadDoc = JsonDocument.Parse(assignment.Payload);
}
catch (JsonException)
{
    throw OrchestrationValidationException.PayloadConfigSchema(
        assignment.Id, new[] { "Payload is not valid JSON." });
}
```
If Assignment payloads are guaranteed valid JSON by an upstream validator, add a
comment citing it (consistent with the convention used elsewhere in this phase).

### WR-03: Cycle gate and schema-edge gate scope differ (entry-reachable vs all steps) — unreachable cycles are not caught before the schema-edge walk runs over all edges

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/CycleDetector.cs:41` and `src/BaseApi.Service/Features/Orchestration/Validation/SchemaEdgeValidator.cs:31`
**Issue:** `CycleDetector.Validate` seeds the DFS only from `Workflow.EntryStepIds`
(line 43), so a step that exists in `snapshot.Steps` but is NOT reachable from any
entry step is never visited by the cycle/missing-step gate. `SchemaEdgeValidator`,
by contrast, iterates `snapshot.Steps.Values` — EVERY step — including
entry-unreachable ones (line 31). The same asymmetry applies to
`PayloadConfigSchemaValidator`, which iterates `snapshot.Assignments.Values`. The
net effect: an orphaned subgraph containing a cycle would pass the cycle gate
(never seeded) yet still be edge-walked by the schema-edge gate. This is not a
crash and the FK-Restrict junction makes a true dangling edge hard to produce via
the API, but the gates do not agree on "what is in scope," which can yield a
surprising result (e.g. a cycle that the cycle gate structurally cannot see, while
a sibling gate validates the same nodes). Whether the loader only ever populates
`Steps` with entry-reachable nodes is the load-bearing assumption — that is not
verifiable from the reviewed files.
**Fix:** Make the scope contract explicit and consistent. Either (a) document in
`WorkflowGraphSnapshot` that `Steps`/`Assignments` contain ONLY entry-reachable
nodes (so all gates share one scope), or (b) seed the cycle DFS from every node in
`snapshot.Steps` (using the shared `fullyVisited` set this already costs nothing
extra) so the cycle gate covers the same node set the other two gates walk:
```csharp
foreach (var workflow in snapshot.Workflows.Values)
    foreach (var entryId in workflow.EntryStepIds ?? Enumerable.Empty<Guid>()) { ... }
// then sweep any step not yet fully visited (orphan subgraphs):
foreach (var stepId in snapshot.Steps.Keys)
    if (!fullyVisited.Contains(stepId)) RunDfs(snapshot, stepId, fullyVisited);
```

## Info

### IN-01: Offending-payload record properties use camelCase names — relies on serializer policy, fragile across config changes

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs:77-86`
**Issue:** The offending records declare positional members in camelCase
(`stepChain`, `parentStepId`, `missingChildId`, `childStepId`, `assignmentId`,
`errors`). C# record property names are conventionally PascalCase; these are
lowercased to match the JSON the tests assert (e.g. `MissingStepFacts.cs:87`
reads `offending.GetProperty("parentStepId")`). This works today only because the
serializer is either emitting member names verbatim or applying a camelCase
policy that happens to be idempotent on already-camelCase names. If the global
`JsonSerializerOptions` policy changes (or ProblemDetails serialization differs
from MVC body serialization), the wire shape silently shifts and the contract the
tests lock would break. Using non-standard casing to encode wire intent hides the
contract from the type system.
**Fix:** Prefer PascalCase members + explicit `[JsonPropertyName("stepChain")]`
attributes (or a documented camelCase naming policy) so the JSON contract is
declared, not incidental. Low priority — current tests pass — but worth pinning
the intent.

### IN-02: Error-string fallback branch (results.Errors top-level) is plausibly unreachable / untested

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs:64-65`
**Issue:** After collecting per-node errors from `results.Details`, there is a
fallback: `if (errorStrings.Count == 0 && results.Errors is { Count: > 0 })`
that reads top-level `results.Errors`. With `OutputFormat.List`, validation
errors are surfaced in `Details`; the top-level `Errors` collection is generally
populated under `OutputFormat.Flag`/hierarchical output, so this branch may be
dead under the configured options. The tests only exercise the `Details` path
(`PayloadConfigSchemaFacts.BadPayload...` asserts a non-empty array but does not
distinguish the source). Dead-but-defensive is acceptable, but it is unverified.
**Fix:** Either add a comment noting this is a defensive fallback for output
formats other than List, or drop it if List output guarantees `Details`
population. No action required for correctness.

### IN-03: Throw-on-first-failure means the offending payload reports a single edge/assignment, not all — confirm this is the intended contract

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/SchemaEdgeValidator.cs:57` and `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs:66`
**Issue:** Both gates throw on the FIRST offending edge/assignment rather than
aggregating all failures. The XML docs explicitly say "first mismatched edge,"
so this is by design and consistent with the cycle gate's first-cycle behavior
and the locked short-circuit ordering proven in `ValidationOrderFacts`. Flagging
only so it is a recorded, deliberate decision: a client fixing one schema-edge
mismatch must re-submit to discover the next. No change needed unless the product
contract wants batch reporting.

### IN-04: `JsonSchemaConfig` static-ctor SSRF lockdown depends on a member being touched before any evaluation — implicit cross-file coupling

**File:** `src/BaseApi.Service/Features/Schema/JsonSchemaConfig.cs:22-34`
**Issue:** The SSRF defense (`SchemaRegistry.Global.Fetch = (_,_) => null` and the
dialect pin) lives in a static constructor that only runs when a member of
`JsonSchemaConfig` is first accessed. Every evaluation site is required to
reference `JsonSchemaConfig.DefaultOptions` to fire the cctor before any
`Evaluate` call. The reviewed evaluation sites (`SchemaDtoValidator.cs:46/96`,
`PayloadConfigSchemaValidator.cs:57`) all do this correctly. The risk is purely
maintainability: a future evaluation added elsewhere that forgets to reference a
`JsonSchemaConfig` member would silently evaluate WITHOUT the SSRF lockdown (the
library default fetch behavior would apply). The code comment already warns about
this, which is good.
**Fix:** Consider relocating the global lockdown to a guaranteed-once app-startup
hook (e.g. a static init invoked from `Program.cs` / `AddBaseApi`) so the
protection does not depend on every future call site remembering the
member-touch idiom. Belt-and-suspenders; current call sites are correct.

---

_Reviewed: 2026-05-29T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
