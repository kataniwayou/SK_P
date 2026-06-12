---
phase: 57-startup-config-schema-fetch-gate-a
plan: 02
subsystem: api
tags: [system-text-json, jsonschema-net, gate-a, config-schema, structural-walk, reflection, tdd]

# Dependency graph
requires:
  - phase: 57-startup-config-schema-fetch-gate-a (plan 01)
    provides: "Wave-0 ConfigSchemaCoverageCheck stub + InternalsVisibleTo + the spike-CONFIRMED CLASH verdicts for rows #13/#5/#8/#22 + the 18-row Covers_Matches_RuleTable theory (RED against the stub)"
  - phase: 56-typed-base-config-seam
    provides: "ProcessorConfig abstract record + the single cached ProcessorConfig.SerializerOptions binding contract Gate A models"
provides:
  - "ConfigSchemaCoverageCheck.Evaluate(string?, Type) — the production Gate A schema⊨TConfig structural-walk covers-checker (CFG-05)"
  - "A JsonNode-tree schema introspection (SchemaKeywords) that reads properties/type/enum/items directly — the JsonSchema.Net 9.2.1 keyword object model the research assumed does NOT exist in the installed version"
  - "The locked STJ Type-Clash Rule Table encoded as code: #13/#5/#8/#22 CLASH (spike-confirmed); #1/#7/#14/#21/#23/#24/#25/#16/#19 + nested + unparseable verified GREEN"
affects: [57-03 Gate A wiring (calls Evaluate from the orchestrator after Loop B), 57-04 freeze override]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Schema-as-JSON structural walk: parse-validate via JsonSchema.FromText (Gate B reuse) THEN introspect the same definition as a System.Text.Json JsonNode tree (the parsed JsonSchema is opaque in 9.2.1)"
    - "Reverse-direction covers check (schema ⊨ TConfig): reflect TConfig honoring [JsonPropertyName]+OrdinalIgnoreCase and flag only a both-present property whose schema-valid values would fail STJ binding (D-02)"
    - "Deviation-rule recovery: a research-cited library API that does not exist in the pinned version → re-derive an equivalent, more robust approach (raw-JSON walk) and verify against the same locked rule table"

key-files:
  created: []
  modified:
    - "src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs (Wave-0 stub → real structural walk, 421 lines)"

key-decisions:
  - "JsonSchema.Net 9.2.1 removed the keyword object model (TypeKeyword/PropertiesKeyword) + the GetProperties()/GetType()/GetEnum()/GetItems() accessors the research/plan assumed (assumption A6 was wrong for the pinned version). Introspected the schema as a JsonNode tree instead; JsonSchema.FromText is kept as the parse/validity gate (shared with Gate B)."
  - "No rule-table corrections — all 18 CoverRows + null-skip GREEN with the Plan-01 spike-CONFIRMED CLASH verdicts (#13/#5/#8/#22) exactly as locked."
  - "BuildClrLookup uses last-writer-wins on a duplicate JSON name rather than ToDictionary (which throws) so Gate A can NEVER throw (D-02); a duplicate is an author error STJ would also reject."

patterns-established:
  - "Schema-as-JSON walk: a JSON Schema is itself JSON, so declared keywords are read off the JsonObject form when the schema library exposes no keyword accessors"
  - "Pure structural walk = SSRF-safe by construction: no Evaluate, no external \$ref resolution (T-57-03)"

requirements-completed: []  # CFG-05/CFG-07 are exercised GREEN here, but the phase requirement is only fully satisfied once 57-03 wires Evaluate into the orchestrator. Not marking complete to avoid over-claiming a partially-wired requirement.

# Metrics
duration: 63min
completed: 2026-06-12
---

# Phase 57 Plan 02: Gate A ConfigSchemaCoverageCheck Structural Walk Summary

**Gate A's `schema ⊨ TConfig` covers-check is now a real structural walk: it parses the fetched config-schema, reflects `TConfig` under the exact `ProcessorConfig.SerializerOptions` name+type rules, and returns `(Covered, ClashDetail)` — turning all 18 `Covers_Matches_RuleTable` rows + the null-skip fact GREEN, with the spike-locked CLASH verdicts for string-enum→CLR-enum (#13), number→int (#5), string→int (#8), and nullable-null→non-nullable-value-type (#22).**

## Performance

- **Duration:** 63 min
- **Started:** 2026-06-12T23:09:39Z
- **Completed:** 2026-06-12T20:13:00Z (clock skew between the SDK init timestamp and local `date` on this host; ~63 min wall by task activity)
- **Tasks:** 1 (TDD GREEN — the RED `test` commit `8d340e4` predates this plan, from 57-01)
- **Files modified:** 1

## Accomplishments
- Replaced the Wave-0 null-skip stub with the production Gate A covers-checker: parse → name-map → recursive STJ clash walk (CFG-05), with the CFG-07 null-skip preserved.
- Encoded the LOCKED STJ Type-Clash Rule Table; verified every row from `ConfigSchemaCoverageFacts.CoverRows` (9 covered=true, 9 covered=false) + null-skip GREEN against the real internal `Evaluate`.
- Recurses into nested objects and array element schemas (D-04); schema-only and TConfig-only properties stay FINE (D-02); unparseable definitions are a terminal non-throwing clash; SSRF-safe (no `Evaluate`, no external `$ref` — T-57-03).
- Confirmed and worked around a stale research/plan API assumption: JsonSchema.Net 9.2.1 has no keyword accessors (see Deviations).

## Task Commits

1. **Task 1: Implement ConfigSchemaCoverageCheck.Evaluate — parse + name-map + recursive STJ clash walk** — `25c2dd9` (feat)

**Plan metadata:** (final docs commit — see git log)

_TDD note: this `type: tdd` plan's RED gate is the `test(...)` commit `8d340e4` from Plan 57-01 (which added the `ConfigSchemaCoverageFacts` theory RED against the stub). This plan supplies the GREEN gate (`feat` `25c2dd9`). No REFACTOR commit was needed — the first GREEN was clean._

## Files Created/Modified
- `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs` — Wave-0 stub replaced by the real 421-line structural-walk checker. `Evaluate(string?, Type) → (bool Covered, string? ClashDetail)`. Private helpers: `WalkObject`, `BuildClrLookup`, `ClassifyProperty`, `ClassifyScalar`, `ClassifyNestedValue`, numeric/collection/object type predicates, and a nested `SchemaKeywords` class that reads `properties`/`type`/`enum`/`items` off the schema's `JsonNode` tree.

## Verification

- **Build:** `BaseProcessor.Core` compiles 0-warning in both Debug and Release.
- **Rule-table GREEN:** an isolated probe project (referencing the built `BaseProcessor.Core.dll`, invoking the internal `Evaluate` via reflection) replicated all 18 `CoverRows` + the null-skip fact exactly as `ConfigSchemaCoverageFacts` asserts them. Result: **18/18 + null-skip = ALL GREEN**, including:
  - CLASH: #13 (`string-enum`→`SpikeEnum`), #5 (`number`→`Int32`), #8 (`string`→`Int32`), #17 (`array`→scalar), #20 (`object`→scalar), #22 (`null`→`Int32`), #26 (`["string","integer"]`→`Int32`), nested clash, unparseable.
  - FINE: #7, #1, #14, #21, #23, #24, #25, #19 (nested ok), #16 (array→`List<int>` ok), null-skip.
  - Clash details name the property + schema-type vs CLR-type, e.g. `property 'Mode': schema string-enum clashes with CLR SpikeEnum`.
- The probe was a throwaway verification artifact in the OS temp dir (never in the repo); it was deleted after the run.

## Why the test assembly cannot be run directly yet (expected cross-wave ordering)

`tests/BaseApi.Tests` does NOT compile yet — exactly 3 errors, all `CS1061: ProcessorContext does not contain a definition for 'ConfigDefinition'`:
- `Processor/SchemaResolutionFacts.cs:141`, `:187`
- `Processor/DispatchBindSequenceFacts.cs:88`

These are the Plan-01 compile-RED seams that **Plan 57-03 (Wave 2)** resolves by adding `ProcessorContext.ConfigDefinition` (+ `IProcessorContext` getter + the 3rd `SetDefinition` branch). Per the plan's explicit instruction I did NOT add `ConfigDefinition` myself (that is 57-03's task). My own files — `ConfigSchemaCoverageCheck.cs` and `ConfigSchemaCoverageFacts.cs` — produce **zero** compiler errors; the only blocker to running the full xUnit slice is the cross-wave `ConfigDefinition` symbol. The isolated probe is therefore the faithful GREEN signal for the rows this plan owns, and `Covers_Matches_RuleTable` + `NullDefinition_Is_Skip_Covered` will run GREEN unchanged the moment 57-03 lands `ConfigDefinition`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Research/plan-cited JsonSchema.Net keyword-accessor API does not exist in the pinned version**
- **Found during:** Task 1
- **Issue:** The plan's `<interfaces>` block + RESEARCH Pattern 3 specify the structural walk using `schema.GetType()` → `SchemaValueType?`, `schema.GetProperties()` → `IReadOnlyDictionary<string,JsonSchema>`, `propSchema.GetEnum()`, `propSchema.GetItems()` (the fluent keyword object model, with `TypeKeyword`/`PropertiesKeyword`/etc.). RESEARCH assumption A6 claimed these were stable since 4.0.0/5.2.0 and that "9.2.1 is far past both." Inspecting the actual pinned `JsonSchema.Net` 9.2.1 (`Directory.Packages.props:90`) via its shipped XML doc + type list shows the **keyword object model was removed in the 8.x rewrite**: `JsonSchema` in 9.2.1 is opaque — its only members are `FromText`, `Evaluate`, `Build`, `BuildNode`, `FindSubschema`, `BoolValue`, `BaseUri`, `Root`. The `TypeKeyword`/`PropertiesKeyword`/`EnumKeyword`/`ItemsKeyword` types and the `GetProperties()/GetType()/GetEnum()/GetItems()` accessors do **not** exist; there is no supported way to enumerate keywords off a parsed `JsonSchema`. Writing the cited code would not compile.
- **Fix:** Kept `JsonSchema.FromText` as the parse/validity gate (preserving the Gate B reuse, the `key_links` pattern requirement, and the unparseable→terminal-clash + null-skip contracts), then introspected the **same definition text as a `System.Text.Json` `JsonNode` tree** — a JSON Schema is itself JSON, so `properties`/`type`/`enum`/`items` are read directly off the `JsonObject`. A private nested `SchemaKeywords` class encapsulates these reads and maps the `type` tokens to the still-present `Json.Schema.SchemaValueType` enum, so the rest of the walk (`ClassifyScalar`, recursion, the rule table) is unchanged in spirit. This is strictly more robust: it depends only on the JSON shape, not on a version-specific object model.
- **Acceptance-criteria impact:** the plan's grep-based acceptance checks for literal `GetProperties()`/`GetEnum()`/`GetItems()` call-sites are NOT satisfiable on this version (those methods don't exist). The *intent* of each grep is satisfied semantically: properties/enum/items enumeration is performed by `SchemaKeywords.GetProperties`/`HasStringEnum`/`GetItems`; `[JsonPropertyName]` mapping and `StringComparer.OrdinalIgnoreCase` are present and grep-able; `JsonSchema.FromText` is present and grep-able; there is correctly **no** `.Evaluate(` call on a `JsonSchema` instance (SSRF-safe — Pitfall 3 / T-57-03); the null-skip `return (true, null)` is present. The real acceptance gate — all `ConfigSchemaCoverageFacts` rows GREEN — is met (see Verification).
- **Files modified:** `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs`
- **Verification:** isolated probe → 18/18 + null-skip GREEN; 0-warning Debug+Release build.
- **Committed in:** `25c2dd9` (Task 1).

---

**Total deviations:** 1 auto-fixed (1 blocking — a stale library-API assumption re-derived to an equivalent, more robust approach). No scope change; the public `Evaluate` signature and every behavioral contract (D-01/D-02/D-04, CFG-05/CFG-07, T-57-03) are exactly as planned.

## Decisions Made
- Walk the schema as a `JsonNode` tree (JsonSchema.Net 9.2.1 exposes no keyword accessors); keep `JsonSchema.FromText` as the shared parse/validity gate.
- No rule-table corrections — the Plan-01 spike-CONFIRMED CLASH verdicts (#13/#5/#8/#22) are encoded verbatim and all rows pass.
- `BuildClrLookup` is last-writer-wins (not `ToDictionary`) so Gate A never throws on a pathological duplicate JSON name.

## Known Stubs
None — the Wave-0 stub is fully replaced by the production walk.

## Threat Flags
None — no new network endpoint, auth path, file access, or schema-write surface is introduced. The walk is read-only over an in-memory schema string and a CLR `Type`; it never calls `Evaluate` and never resolves an external `$ref` (T-57-03 mitigation, as planned).

## Next Phase Readiness
- **Plan 57-03** can now call `ConfigSchemaCoverageCheck.Evaluate(context.ConfigDefinition, ConcreteConfigType())` from the orchestrator after Loop B and before the bind, add `ProcessorContext.ConfigDefinition` (+ `IProcessorContext` getter + 3rd `SetDefinition` branch), and turn the 3 cross-wave `CS1061` RED references GREEN — at which point the full `BaseApi.Tests` slice (`ConfigSchemaCoverageFacts` included) compiles and runs GREEN in CI.
- **Plan 57-04** is unaffected by this plan (separate freeze-override surface).

## Self-Check: PASSED

- `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs` exists (421 lines, ≥ 80 required).
- Task 1 commit `25c2dd9` exists in `git log`.
- No files deleted by the commit.
- 0-warning Debug+Release build of `BaseProcessor.Core`.

---
*Phase: 57-startup-config-schema-fetch-gate-a*
*Completed: 2026-06-12*
