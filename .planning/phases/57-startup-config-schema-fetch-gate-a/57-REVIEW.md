---
phase: 57-startup-config-schema-fetch-gate-a
reviewed: 2026-06-13T00:00:00Z
depth: standard
files_reviewed: 17
files_reviewed_list:
  - src/BaseApi.Core/Services/BaseService.cs
  - src/BaseApi.Service/Features/Schema/SchemaDefinitionFrozenException.cs
  - src/BaseApi.Service/Features/Schema/SchemaDefinitionFrozenExceptionHandler.cs
  - src/BaseApi.Service/Features/Schema/SchemaService.cs
  - src/BaseApi.Service/Features/Schema/SchemaServiceCollectionExtensions.cs
  - src/BaseProcessor.Core/Configuration/BaseProcessorConfigTypeProvider.cs
  - src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs
  - src/BaseProcessor.Core/Configuration/IConfigTypeProvider.cs
  - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
  - src/BaseProcessor.Core/Identity/IProcessorContext.cs
  - src/BaseProcessor.Core/Identity/ProcessorContext.cs
  - src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
  - tests/BaseApi.Tests/Features/Schema/SchemaDefinitionFreezeFacts.cs
  - tests/BaseApi.Tests/Processor/ConfigSchemaCoverageFacts.cs
  - tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs
  - tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs
  - tests/BaseApi.Tests/Processor/FakeProcessorContext.cs
findings:
  critical: 0
  warning: 2
  info: 5
  total: 7
status: issues_found
---

# Phase 57: Code Review Report

**Reviewed:** 2026-06-13
**Depth:** standard
**Files Reviewed:** 17
**Status:** issues_found

## Summary

Reviewed the Phase 57 Gate A surface: the `ConfigSchemaCoverageCheck` structural walk
(`schema ⊨ TConfig`), the `ProcessorStartupOrchestrator` Gate A wiring (MarkReady/MarkHealthy
decoupling + skipped bind on clash), the `SchemaService` frozen-once-referenced 409 gate with the
new canonical-JSON `DefinitionChanged` comparison, and the supporting DI/context plumbing + tests.

Overall the implementation is sound and well-aligned with its decision record. The STJ type-clash
rule table is applied conservatively (D-02: when ambiguous, FINE), so the walk's error mode is
biased toward false-NEGATIVES (missed clash) rather than false-POSITIVES (a healthy processor wedged
into the never-serve state) — the correct bias for a startup gate that should not crash-loop. The
Gate A wiring correctly decouples `MarkReady` (fires on both paths, no K8s crash-loop) from
`MarkHealthy` + endpoint bind (pass/skip only), and is terminal with no retry. The freeze query
covers all three FK roles. SSRF-safety holds: the walk never calls `Evaluate` and never resolves a
`$ref`.

Two Warnings concern semantic-comparison completeness in two places where the canonicalization /
type-walk does not fully model the downstream normalizer's behavior — both can produce a wrong
verdict on a narrow input class. No Critical issues. The remaining Info items are coverage gaps in
the structural walk that are conservative-by-design (they fail FINE, never falsely clash) plus minor
robustness notes.

## Warnings

### WR-01: Canonical-JSON compare does not normalize number tokens — residual jsonb false-positive 409

**File:** `src/BaseApi.Service/Features/Schema/SchemaService.cs:90-122`
**Issue:** `Canonicalize` fixes the two normalization axes the doc-comment calls out (insignificant
whitespace, object-key order) by recursively sorting keys and re-emitting compact JSON. For the
default scalar case it does `element.WriteTo(writer)`, which re-emits the number using STJ's parse of
the *raw request token*. Postgres `jsonb`, however, also normalizes **numbers** (it stores numeric
canonically — trailing zeros dropped, exponent form folded, e.g. `3.140` → `3.14`, `1e2` → `100`).
So a referenced schema whose only delta from the stored body is a numeric-format change
(`{"x":1.0}` re-PUT as `{"x":1.00}`, or `{"x":1e3}` vs `{"x":1000}`) will canonicalize to two
different strings here while jsonb sees them as identical — `DefinitionChanged` returns `true` and
the call wrongly 409s a no-op Definition edit. This is the *same* class of D-07 regression the fix
targeted, just on the number axis rather than the whitespace/key-order axis.

In a JSON *Schema* body the most realistic carriers are `minimum`/`maximum`/`multipleOf`/`default`
numeric keywords. Low frequency, but it is a real false-positive on a path the fix was specifically
hardening.
**Fix:** Normalize numbers in `WriteCanonical` so the canonical form matches jsonb's. The cheapest
robust option is to re-emit through `decimal`/`double` round-trip when the kind is `Number`:
```csharp
case JsonValueKind.Number:
    // Fold numeric formatting to match jsonb's canonical numeric storage so a format-only
    // re-PUT (1.0 vs 1.00, 1e3 vs 1000) is not seen as a Definition change (D-07).
    if (element.TryGetDecimal(out var dec))
        writer.WriteNumberValue(dec);
    else
        writer.WriteNumberValue(element.GetDouble());
    break;
```
(Add this case before the `default:` branch. `decimal` first preserves precision for the common
fixed-point case; `double` is the fallback for values outside decimal range.) Alternatively, accept
the residual gap explicitly in the doc-comment as out-of-scope, since the practical blast radius is
small — but the current comment claims the compare is canonical, which over-states it.

### WR-02: `type:"object"` → `Dictionary<,>` recurses into CLR infrastructure properties, risking a false clash

**File:** `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs:245-249, 294-300`
**Issue:** `IsBindableObject` treats a `Dictionary<TKey,TValue>` (and other map types) as a bindable
object: it is not a primitive/string/known-scalar, and `TryGetEnumerableElementType` excludes it
(its `IEnumerable<T>` element is `KeyValuePair<,>`, lines 339-341), so `IsBindableObject` returns
`true`. STJ binds a JSON object into a `Dictionary<string,T>` by *key→value*, NOT by reflecting CLR
properties — but `ClassifyScalar`'s Object case then calls `WalkObject(propSchema, dictType)`, and
`BuildClrLookup(dictType)` enumerates the dictionary's *infrastructure* properties (`Count`, `Keys`,
`Values`, `Comparer`, `Item`). If the config schema declares a `properties` entry whose name
case-insensitively collides with one of those (e.g. a schema property literally named `Count` or
`Keys` typed as something non-int), `ClassifyProperty` will compare the schema type against
`Dictionary.Count`'s `int` / `Keys`'s collection type and can emit a spurious CLASH — wedging the
processor into the never-serve state for a config that would actually bind fine.

Probability is low (schema property named exactly `Count`/`Keys`/`Values`/`Comparer`/`Item`), but
the failure mode is the high-cost one (false-positive → processor never serves), which is exactly
the bias the rest of the file works to avoid.
**Fix:** Treat dictionary/map types as their own scalar-ish "binds-from-object" leaf that does NOT
recurse property-by-property. Add a dictionary detector and short-circuit the Object case:
```csharp
case SchemaValueType.Object:
    if (IsDictionary(effective))
        return null;                       // STJ binds key→value, not by CLR props — cannot clash structurally
    if (IsBindableObject(effective))
        return WalkObject(propSchema, effective);
    return Detail(name, "object", declared);
```
where `IsDictionary` checks for `IDictionary<,>` / `IReadOnlyDictionary<,>` in the implemented
interfaces. (Recursing into the dictionary *value* schema vs `TValue` would be the fully-correct
move, but returning FINE preserves the conservative no-false-positive bias and is the minimal fix.)

## Info

### IN-01: `items` array (tuple) form is silently not walked

**File:** `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs:240-243, 364-366`
**Issue:** `SchemaKeywords.GetItems` returns the `items` node as-is and the Array case only recurses
when it `is JsonObject` (single-schema form). The Draft-2020-12 tuple form (`"items":[{...},{...}]`,
or `prefixItems`) is therefore not walked — a clash inside a tuple-positional element schema would
slip past Gate A. This is conservative (fails FINE, never false-positives) and config schemas rarely
use tuple validation, so it is informational, not a bug. Worth a one-line code comment noting the
deliberate gap so a future reader does not assume full `items` coverage.
**Fix:** Optionally handle the `JsonArray` form by walking each element schema against the same
element type, or add a comment: `// tuple-form items ([...]) and prefixItems are not walked (conservative — FINE).`

### IN-02: `$ref` / `allOf` / `oneOf` / `anyOf` composition keywords are not walked

**File:** `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs:98-121, 356-404`
**Issue:** `WalkObject` reads only the literal `properties`/`type`/`enum`/`items` keywords. A schema
that factors its shape through `$ref`, `allOf`, `$defs`, or a `oneOf`/`anyOf` union is not followed,
so a both-present clash reachable only through a composition keyword is missed. This is by design
(SSRF-safety + the documented "declared keywords only" scope) and is conservative (FINE), but the
class comment advertises a "pure structural walk … recurses into nested objects and array element
schemas" without stating the composition-keyword limitation. Note it so the scope is unambiguous.
**Fix:** Add to the class doc-comment: composition keywords (`$ref`/`allOf`/`oneOf`/`anyOf`/`$defs`)
are intentionally not followed — the walk is purely the inline `properties`/`type`/`enum`/`items`
tree (conservative: an unwalked branch is FINE).

### IN-03: `BuildClrLookup` last-writer-wins on duplicate JSON name can pick the wrong property

**File:** `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs:130-145`
**Issue:** The comment correctly explains why last-writer-wins is chosen over `ToDictionary` (never
throw — D-02). One subtlety: `GetProperties(BindingFlags.Public | BindingFlags.Instance)` returns
base-class properties too, and on a `new`-shadowed or inherited+redeclared property the *iteration
order* of `Type.GetProperties` is not contractually guaranteed, so "last writer" may bind the schema
name to the base member rather than the derived one. For the closed `TConfig : ProcessorConfig`
hierarchy (the base declares zero fields — confirmed in `ProcessorConfig.cs`) this cannot bite today.
Informational — flag only so it is not assumed safe if `ProcessorConfig` ever gains a property.
**Fix:** If `ProcessorConfig` ever declares JSON-bound properties, prefer the most-derived
declaration explicitly (e.g. order by `DeclaringType` depth) rather than relying on reflection order.

### IN-04: Gate A clash leaves input/output definitions fetched but the processor permanently dark — confirm intended

**File:** `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:184-192`
**Issue:** On a Gate A clash the orchestrator logs one Error, calls `gate.MarkReady()`, and returns —
terminal, no retry, by design (D-09/D-11). Because the definition is immutable once referenced
(the freeze gate), no retry can ever succeed, so the terminal return is correct. The single Error log
is the only operator signal; there is no metric/heartbeat-visible "incompatible" state distinct from
"absent" (the orchestration-start gate just sees no `skp:{id}` key → 422). This matches the decision
record but is worth confirming against ops expectations: a clashing processor is indistinguishable at
the orchestrator from a never-booted one. No code change implied.
**Fix:** None required — verify D-10's single-Error-log is sufficient operator signal, or consider a
dedicated metric/log marker for "Gate A incompatible" vs "absent" if observability needs the
distinction.

### IN-05: `DefinitionChanged` treats unparseable JSON as changed — relies on validator upstream

**File:** `src/BaseApi.Service/Features/Schema/SchemaService.cs:73-87`
**Issue:** The `catch (JsonException) => true` fallback is the documented conservative choice (prefer
freezing over silent bypass). It only catches `JsonException`; the `existing.Definition` value comes
from the DB (jsonb, always valid JSON) and `dto.Definition` is validated by the DTO validator before
this method runs, so the catch is effectively unreachable in normal flow — correct and safe. Minor
note: if `dto.Definition` validation is ever relaxed, a non-JSON incoming body would be force-frozen
(409) rather than surfacing a clearer 422 validation error. Acceptable as-is.
**Fix:** None required — behavior is intentional and safe given the upstream validator ordering.

---

_Reviewed: 2026-06-13_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
