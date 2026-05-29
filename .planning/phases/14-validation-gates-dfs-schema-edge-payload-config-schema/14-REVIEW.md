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
  - src/BaseApi.Service/Features/Orchestration/WorkflowGraphSnapshot.cs
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
  warning: 0
  info: 4
  status: clean
status: clean
---

# Phase 14: Code Review Report

**Reviewed:** 2026-05-29T00:00:00Z
**Depth:** standard
**Files Reviewed:** 16
**Status:** clean

## Summary

Re-review of the Phase 14 validation-gates implementation (four orchestration
validation gates — cycle DFS, missing-step, schema-edge, payload↔config-schema —
plus the single `OrchestrationValidationException` + 422 handler, the split-out
fallback handler wiring, and the shared `JsonSchemaConfig` SSRF lockdown). The
primary goal was to verify the three warnings from the prior review (WR-01/02/03)
are genuinely resolved and to surface any regressions.

**All three prior warnings are verifiably fixed.** The service project builds
clean (0 warnings, 0 errors). No critical or warning-level issues remain. The
four remaining items below are the same maintainability notes recorded in the
prior review, carried forward at Info severity; none block the phase.

### Verification of prior warnings

**WR-01 — unguarded `JsonSchema.FromText` parse (RESOLVED).**
`PayloadConfigSchemaValidator.cs:53-62` now wraps the schema parse in
`try { JsonSchema.FromText(...) } catch (Exception ex) when (ex is JsonException
or JsonSchemaException)` and translates a parse failure into
`OrchestrationValidationException.PayloadConfigSchema(assignment.Id, ...)` → HTTP
422 instead of letting a raw exception fall through to the fallback handler (500).
The exception-filter pattern compiles correctly (`JsonSchemaException` resolves
via the `using Json.Schema;` import — confirmed by a clean build). A leading
comment explains the invariant (persisted Definitions normally pass the
create-time meta-schema gate) and why the guard exists anyway. This matches the
fix recommended in the prior review.

**WR-02 — untranslated `JsonDocument.Parse(assignment.Payload)` failure (RESOLVED).**
`PayloadConfigSchemaValidator.cs:73-81` now catches `JsonException` around the
payload parse and surfaces it through the same gate exception with the message
"Payload is not valid JSON." The disposal contract is preserved — the parse is
nested inside the outer `try { ... } finally { payloadDoc?.Dispose(); }`, so a
translated throw still disposes the (null) document harmlessly. A malformed
payload now yields a 422 envelope consistent with a schema-mismatch rather than a
500.

**WR-03 — cycle vs schema-edge scope divergence (RESOLVED via documentation).**
The divergence (cycle DFS seeds only from `EntryStepIds`; schema-edge and
payload gates iterate the full `Steps`/`Assignments` sets) is now an explicit,
named **scope contract** documented in two places:
`WorkflowGraphSnapshot.cs:31-50` ("Validation-gate scope contract (WR-03)") and
`CycleDetector.cs:36-42`. The rationale is stated (entry-unreachable orphans
cannot execute and so cannot contribute a runtime cycle; the FK-Restrict
junctions make true dangling edges hard to produce) and the precise extension
recipe is given (sweep `Steps.Keys` not yet in the shared `fullyVisited` set
after the entry-seeded pass). This is option (a) from the prior review's
recommended fix — make the asymmetry an intentional, documented contract. Per
the user's standing memory, the EntryStepIds/NextStepIds junction design is
by-design, which is consistent with treating this as accepted divergence rather
than a bug.

The implementation otherwise remains sound: the iterative (non-recursive)
two-set DFS correctly distinguishes back-edges (cycle) from fan-in/diamond
subgraphs (covered by `CycleDetectionFacts.DiamondDag_Passes`), the SSRF
defense-in-depth is intact, the handler ordering is carefully wired and proven by
`ValidationOrderFacts`, and the L1 snapshot disposal runs on the validation-
failure path (`ValidationOrderFacts.L1Cleanup_RunsOnValidationFailurePath`).

## Info

### IN-01: Offending-payload record properties use camelCase names — relies on serializer policy

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs:77-86`
**Issue:** The offending records declare positional members in camelCase
(`stepChain`, `parentStepId`, `missingChildId`, `childStepId`, `assignmentId`,
`errors`). C# record property names are conventionally PascalCase; these are
lowercased to match the JSON the tests assert (e.g. `MissingStepFacts.cs:87`
reads `offending.GetProperty("parentStepId")`). This works today only because the
serializer emits member names verbatim or applies a camelCase policy that is
idempotent on already-camelCase names. If the global `JsonSerializerOptions`
policy changes (or ProblemDetails serialization diverges from MVC body
serialization), the wire shape could shift silently and break the contract the
tests lock.
**Fix:** Prefer PascalCase members + explicit `[JsonPropertyName("stepChain")]`
attributes (or a documented camelCase naming policy) so the JSON contract is
declared, not incidental. Low priority — current tests pass.

### IN-02: Top-level `results.Errors` fallback branch is plausibly unreachable under `OutputFormat.List`

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs:91-92`
**Issue:** After collecting per-node errors from `results.Details`, the fallback
`if (errorStrings.Count == 0 && results.Errors is { Count: > 0 })` reads
top-level `results.Errors`. With `OutputFormat.List`, validation errors surface
in `Details`; top-level `Errors` is generally populated under
`OutputFormat.Flag`/hierarchical output, so this branch may be dead under the
configured options. `PayloadConfigSchemaFacts.BadPayload...` asserts a non-empty
array but does not distinguish the source. Dead-but-defensive is acceptable.
**Fix:** Either add a comment noting this is a defensive fallback for non-List
output formats, or drop it if List output guarantees `Details` population. No
action required for correctness.

### IN-03: Gates throw on the first failure — offending payload reports a single edge/assignment, not all

**File:** `src/BaseApi.Service/Features/Orchestration/Validation/SchemaEdgeValidator.cs:57` and `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs:93`
**Issue:** Both gates throw on the FIRST offending edge/assignment rather than
aggregating all failures. The XML docs explicitly say "first mismatched edge,"
so this is by design and consistent with the cycle gate's first-cycle behavior
and the locked short-circuit ordering proven in `ValidationOrderFacts`. Recorded
so it is a deliberate, documented decision: a client fixing one mismatch must
re-submit to discover the next.
**Fix:** No change needed unless the product contract wants batch reporting.

### IN-04: `JsonSchemaConfig` SSRF lockdown depends on a member being touched before any evaluation

**File:** `src/BaseApi.Service/Features/Schema/JsonSchemaConfig.cs:22-34`
**Issue:** The SSRF defense (`SchemaRegistry.Global.Fetch = (_,_) => null` + the
dialect pin) lives in a static constructor that only runs when a member of
`JsonSchemaConfig` is first accessed. Every evaluation site must reference
`JsonSchemaConfig.DefaultOptions` to fire the cctor before any `Evaluate` call.
All reviewed evaluation sites (`SchemaDtoValidator.cs:46/96`,
`PayloadConfigSchemaValidator.cs:84`) do this correctly. The risk is purely
maintainability: a future evaluation added elsewhere that forgets the
member-touch idiom would silently evaluate WITHOUT the SSRF lockdown. The code
comment already warns about this.
**Fix:** Consider relocating the global lockdown to a guaranteed-once app-startup
hook (e.g. a static init invoked from `Program.cs` / `AddBaseApi`) so the
protection does not depend on every future call site remembering the member-touch
idiom. Belt-and-suspenders; current call sites are correct.

---

_Reviewed: 2026-05-29T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
