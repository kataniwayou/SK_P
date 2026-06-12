---
phase: 57-startup-config-schema-fetch-gate-a
plan: 01
subsystem: testing
tags: [system-text-json, jsonschema-net, gate-a, config-schema, tdd, xunit-v3, webapplicationfactory]

# Dependency graph
requires:
  - phase: 56-typed-base-config-seam
    provides: "ProcessorConfig abstract record + the single cached ProcessorConfig.SerializerOptions binding contract Gate A models"
provides:
  - "BLOCKING spike verdicts for the STJ Type-Clash Rule Table rows #13/#5/#8/#22 — all three [ASSUMED] high-risk verdicts CONFIRMED as CLASH against the real ProcessorConfig.SerializerOptions"
  - "ConfigSchemaCoverageFacts: 4 GREEN spike facts (permanent rule-table ground-truth) + an 18-row table-driven covers theory RED against the Plan-02 ConfigSchemaCoverageCheck.Evaluate signature"
  - "SchemaDefinitionFreezeFacts: 3 WebApplicationFactory integration facts (CFG-10) RED on the not-yet-built freeze override + records the SC-5 frozen-once-referenced TOCTOU mechanism"
  - "Inverted SchemaResolutionFacts (config IS now fetched, CFG-03/04) + extended DispatchBindSequenceFacts (Gate A clash + null-skip, CFG-06/07) RED on the not-yet-existing ProcessorContext.ConfigDefinition"
  - "Wave-0 ConfigSchemaCoverageCheck stub (CFG-07 null-skip only) so the spike compiles+runs; InternalsVisibleTo BaseApi.Tests on BaseProcessor.Core"
affects: [57-02 covers-checker, 57-03 Gate A wiring + ConfigDefinition, 57-04 freeze override]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Wave-0 Deserialize spike: drive crafted JSON through the EXACT cached SerializerOptions instance to empirically ground [ASSUMED] type-clash verdicts before the checker is written"
    - "Compile-RED / runtime-RED seams: reference the not-yet-existing Plan-02/03 symbols (ConfigSchemaCoverageCheck.Evaluate, ProcessorContext.ConfigDefinition) so every later task has a pre-existing automated verify (Nyquist)"
    - "Internal-checker test access via InternalsVisibleTo rather than a public DI surface"

key-files:
  created:
    - "tests/BaseApi.Tests/Processor/ConfigSchemaCoverageFacts.cs"
    - "tests/BaseApi.Tests/Features/Schema/SchemaDefinitionFreezeFacts.cs"
    - "src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs (Wave-0 stub)"
  modified:
    - "tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs (inverted)"
    - "tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs (extended)"
    - "src/BaseProcessor.Core/BaseProcessor.Core.csproj (InternalsVisibleTo)"

key-decisions:
  - "A1 (#13 string-enum -> CLR enum): CONFIRMED CLASH — Plan 02 keeps the #13 CLASH verdict"
  - "A2a (#5 number/fractional -> int) + A2b (#8 string -> numeric): CONFIRMED CLASH — Plan 02 keeps #5/#8 CLASH"
  - "A3 (#22 nullable-union null -> non-nullable value type): CONFIRMED CLASH — Plan 02 keeps #22 CLASH"
  - "Added a Wave-0 ConfigSchemaCoverageCheck stub + InternalsVisibleTo so the BLOCKING spike actually compiles+runs GREEN while the covers theory stays RED (the plan's two acceptance demands were otherwise mutually exclusive in one compilation unit)"

patterns-established:
  - "Spike-grounds-rule-table: empirical Deserialize verdict precedes the structural-walk implementation"
  - "Compile-RED test seam against a stubbed Plan-N+1 signature for Nyquist coverage"

requirements-completed: []  # Wave-0 seams only — CFG-03..07/10 are turned GREEN by Plans 02-04; not completed here.

# Metrics
duration: 31min
completed: 2026-06-12
---

# Phase 57 Plan 01: Wave-0 STJ Spike + Test Seams Summary

**The 3 high-risk [ASSUMED] STJ type-clash verdicts (string-enum->CLR-enum, number->int / string->number, null->non-nullable value-type) are all EMPIRICALLY CONFIRMED as CLASH against the real `ProcessorConfig.SerializerOptions`, and all four Gate A / freeze test files stand RED against the not-yet-built production code.**

## BLOCKING Spike Verdicts (read by Plan 02 to lock the rule table)

The four spike `[Fact]`s in `ConfigSchemaCoverageFacts` drive crafted JSON through the EXACT cached `ProcessorConfig.SerializerOptions` instance (case-insensitive, ignore-unknown, NO naming policy, NO `NumberHandling`, NO `JsonStringEnumConverter`). All four ran GREEN — confirming every assumption:

| Assumption | Rule-table row | Spike fact | Crafted input | Observed | Verdict for Plan 02 |
|------------|----------------|------------|---------------|----------|---------------------|
| A1 | #13 string-enum → CLR `enum` | `Spike_A1_StringEnumSchema_To_ClrEnum` | `{"Mode":"A"}` → `record C(SpikeEnum Mode)` | `JsonException` thrown | **CONFIRMED CLASH** — keep #13 CLASH (no `JsonStringEnumConverter` registered; STJ binds enums numerically by default). This is the highest-value clash. |
| A2a | #5 `number` (fractional) → `int` | `Spike_A2a_NumberSchema_To_Int` | `{"N":3.14}` → `record C(int N)` | `JsonException` thrown | **CONFIRMED CLASH** — keep #5 CLASH (fractional JSON number does not bind to integral CLR). |
| A2b | #8 `string` → numeric | `Spike_A2b_StringToNumber` | `{"N":"abc"}` → `record C(int N)` | `JsonException` thrown | **CONFIRMED CLASH** — keep #8 CLASH (`NumberHandling.AllowReadingFromString` not set). |
| A3 | #22 nullable-union `null` → non-nullable value type | `Spike_A3_Null_To_NonNullableValueType` | `{"N":null}` → `record C(int N)` | `JsonException` thrown | **CONFIRMED CLASH** — keep #22 CLASH (schema-valid `null` does not bind to non-nullable `int`). |

**Net: NO rule-table corrections required.** Plan 02 locks rows #13/#5/#8/#22 as CLASH exactly as RESEARCH §"STJ Type-Clash Rule Table" assumed. The positional-record coverage Open Question (#3) is also incidentally confirmed — the spike's positional `record C(int N) : ProcessorConfig` deserializes through STJ's parameterized ctor without issue, so enumerating public instance properties covers positional records (Plan 02 name-mapping is sound).

## Performance

- **Duration:** 31 min
- **Started:** 2026-06-12T19:32:18Z
- **Completed:** 2026-06-12T20:03:29Z
- **Tasks:** 3 (TDD seams)
- **Files modified:** 6 (3 created, 3 modified)

## Accomplishments
- BLOCKING spike confirms all 3 high-risk STJ verdicts as CLASH — Plan 02's rule table is now empirically grounded, not `[ASSUMED]`.
- `ConfigSchemaCoverageFacts`: 4 GREEN spike facts + 18-row covers theory (9 covered=true RED against the stub, 9 covered=false GREEN, + null-skip GREEN).
- `SchemaDefinitionFreezeFacts`: compiles + RED (Frozen mutation got `Actual: OK` vs `Expected: Conflict`); 2 success-path facts GREEN; class XML doc records the SC-5 TOCTOU mechanism.
- `SchemaResolutionFacts` inverted (config IS queried, `ConfigDefinition` asserted) + `DispatchBindSequenceFacts` extended (Gate A clash + null-skip) — both RED only on the missing `ConfigDefinition` member.

## Task Commits

1. **Task 1: STJ spike + ConfigSchemaCoverageFacts RED** — `8d340e4` (test)
2. **Task 2: SchemaDefinitionFreezeFacts RED + TOCTOU mechanism** — `72cc068` (test)
3. **Task 3: invert SchemaResolutionFacts + extend DispatchBindSequenceFacts RED** — `da0496c` (test)

## Files Created/Modified
- `tests/BaseApi.Tests/Processor/ConfigSchemaCoverageFacts.cs` — spike facts + table-driven covers theory (CFG-05).
- `tests/BaseApi.Tests/Features/Schema/SchemaDefinitionFreezeFacts.cs` — WAF freeze integration facts (CFG-10) + SC-5 mechanism record.
- `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs` — Wave-0 stub of the Plan-02 checker (CFG-07 null-skip only).
- `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs` — inverted to assert config IS fetched (CFG-03/04).
- `tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs` — extended with Gate A clash + null-skip + capturing logger (CFG-06/07).
- `src/BaseProcessor.Core/BaseProcessor.Core.csproj` — `InternalsVisibleTo` BaseApi.Tests.

## RED-State Map (what Plans 02-04 turn GREEN)

| Test | RED kind | Resolved by | Unresolved symbol / failing assertion |
|------|----------|-------------|----------------------------------------|
| `ConfigSchemaCoverageFacts.Covers_Matches_RuleTable` (9 covered=true rows) | runtime (stub returns clash) | Plan 02 | `ConfigSchemaCoverageCheck.Evaluate` structural walk |
| `SchemaDefinitionFreezeFacts.Frozen_Definition_Mutation_Returns_409` | runtime (gets 200) | Plan 04 | `SchemaService.UpdateAsync` freeze override + 409 handler |
| `SchemaResolutionFacts` (lines 141, 187) | compile (CS1061) | Plan 02/03 | `ProcessorContext.ConfigDefinition` |
| `DispatchBindSequenceFacts` (RecordingContext proxy, line 88) | compile (CS1061) | Plan 02/03 | `ProcessorContext.ConfigDefinition` + Gate A clash wiring |

These are PLANNED inversions/extensions per RESEARCH §"State of the Art", not breakage.

## Decisions Made
- All three spike verdicts CONFIRMED CLASH (see table) — Plan 02 changes nothing in the rule table.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Added a Wave-0 ConfigSchemaCoverageCheck stub + InternalsVisibleTo**
- **Found during:** Task 1
- **Issue:** The plan's acceptance demanded BOTH (a) the 4 spike `[Fact]`s PASS via `dotnet test` (requires the assembly to compile) AND (b) the covers `[Theory]` reference the not-yet-existing `ConfigSchemaCoverageCheck` (which prevents compilation). These are mutually exclusive in one compilation unit — the assembly would not build, so the BLOCKING spike could never run.
- **Fix:** Created `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs` as a Wave-0 STUB honoring only the CFG-07 null-skip contract (`null` → `(true, null)`; any non-null definition → a placeholder `(false, "...not yet implemented (Plan 57-02)")`). Added `InternalsVisibleTo("BaseApi.Tests")` to `BaseProcessor.Core.csproj` so the internal checker is referenceable. This makes the assembly compile → spike runs GREEN → rule table grounded; the 9 covered=true theory rows stay RED at runtime against the placeholder, mirroring Task 2's "compiles, RED at runtime" pattern exactly. Plan 02 replaces the stub body with the real JsonSchema.Net structural walk.
- **Files modified:** `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs` (new), `src/BaseProcessor.Core/BaseProcessor.Core.csproj`
- **Verification:** `dotnet build tests/BaseApi.Tests` succeeds; the 4 spike facts + 9 covered=false rows + null-skip = 14 PASS, the 9 covered=true rows FAIL (RED).
- **Committed in:** `8d340e4` (Task 1 commit)

**2. [Rule 1 - Bug] Unique SourceHash per seeded ProcessorEntity in the freeze facts**
- **Found during:** Task 2
- **Issue:** First freeze-fact run crashed in `SeedReferencingProcessorAsync` with a `DbUpdateException` — a constant `SourceHash` (`'a'*64`) collided with `uq_processor_source_hash` (PERSIST-14) when two facts seeded into the shared test DB, so `Frozen_Definition_Mutation_Returns_409` failed for the WRONG reason (seed crash, not the intended 200-vs-409 gap).
- **Fix:** Generate a unique 64-char lowercase-hex `SourceHash` per seeded processor (`Guid.NewGuid("N") + Guid.NewGuid("N")`).
- **Files modified:** `tests/BaseApi.Tests/Features/Schema/SchemaDefinitionFreezeFacts.cs`
- **Verification:** Re-run → `Frozen_Definition_Mutation_Returns_409` now fails with `Expected: Conflict, Actual: OK` (correct RED); the 2 success-path facts PASS.
- **Committed in:** `72cc068` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (1 blocking, 1 bug)
**Impact on plan:** Both auto-fixes were essential to make the plan's own acceptance criteria achievable — the stub unblocks the BLOCKING spike, and the unique-SourceHash fix makes the freeze fact fail for the intended reason. No scope creep; no production behavior added (the stub is a null-skip-only placeholder Plan 02 replaces).

## Issues Encountered
- `dotnet test --filter "FullyQualifiedName~..."` (VSTest syntax) is ignored by this xUnit v3 / Microsoft.Testing.Platform setup — it ran the full 561-test suite instead of the slice. Resolved by running targeted classes via `dotnet run --no-build -- --filter-class "*Name*"` / `--filter-method "*Name*"` (MTP native filter). Per-task verification used this to confirm the spike (4 GREEN) and the freeze RED in isolation.

## Known Stubs
- `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs` — Wave-0 STUB. `Evaluate` honors only CFG-07 (null → covered); every non-null definition returns a placeholder clash `(false, "ConfigSchemaCoverageCheck not yet implemented (Plan 57-02).")`. INTENTIONAL: Plan 57-02 replaces the body with the real JsonSchema.Net structural walk grounded by this plan's spike. The covers theory's 9 covered=true rows are RED against this stub by design (Nyquist seam).

## Next Phase Readiness
- Plan 02 can lock the rule table immediately: rows #13/#5/#8/#22 = CLASH (all confirmed, no corrections), and replace the `ConfigSchemaCoverageCheck` stub body to turn the 9 covered=true theory rows GREEN.
- Plan 03 adds `ProcessorContext.ConfigDefinition` (+ `IProcessorContext` getter + `SetDefinition` 3rd `if`) + the Gate A call site → turns the SchemaResolutionFacts + DispatchBindSequenceFacts CS1061 RED GREEN.
- Plan 04 adds the `SchemaService.UpdateAsync` freeze override + `SchemaDefinitionFrozenException` + handler → turns `Frozen_Definition_Mutation_Returns_409` GREEN.

## Self-Check: PASSED

- All 3 created files + the stub + SUMMARY exist on disk.
- All 3 task commits exist: `8d340e4`, `72cc068`, `da0496c`.

---
*Phase: 57-startup-config-schema-fetch-gate-a*
*Completed: 2026-06-12*
