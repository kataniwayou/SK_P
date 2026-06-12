# Phase 57 — Deferred Items

## 57-04: SchemaDefinitionFreezeFacts runtime verification deferred to Plan 03 landing

**Logged:** 2026-06-12 (during 57-04 execution)

**What:** The `SchemaDefinitionFreezeFacts` integration facts (the 3 facts 57-04 turns GREEN —
`Frozen_Definition_Mutation_Returns_409`, `NameDescription_Edit_On_Referenced_Schema_Returns_200`,
`Unreferenced_Draft_Definition_Edit_Returns_200`) could NOT be executed at 57-04 completion.

**Why:** The shared `tests/BaseApi.Tests` assembly does not compile until Plan 57-03 lands
`ProcessorContext.ConfigDefinition`. Plan 57-01 seeded compile-RED references to that not-yet-existing
member in `SchemaResolutionFacts.cs:141,187` and `DispatchBindSequenceFacts.cs:88` (per 57-01-SUMMARY
RED-State Map, those are resolved by Plan 02/03, not Plan 04). Plan 57-03 (wave 2, owns
`ProcessorContext.ConfigDefinition`) had not yet executed when 57-04 (wave 1) ran — a wave-ordering
artifact, since 57-04's `depends_on` is only `[57-01]`.

**Production status (57-04 is correct and complete):**
- `src/BaseApi.Core` + `src/BaseApi.Service` build clean in BOTH Debug and Release, 0 warnings.
- The test assembly's ONLY compile errors are the 3 `ProcessorContext.ConfigDefinition` references above —
  none touch the 57-04 production surface (`SchemaService`, `SchemaDefinitionFrozenException`,
  `SchemaDefinitionFrozenExceptionHandler`, `SchemaServiceCollectionExtensions`).

**Action required (not by 57-04):** Once Plan 57-03 lands `ProcessorContext.ConfigDefinition`, the
`BaseApi.Tests` assembly will compile. Re-run:
`dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-class "*SchemaDefinitionFreezeFacts*"`
to confirm all 3 freeze facts are GREEN. This is the deferred runtime proof of CFG-10 for this plan.
The freeze logic itself is independent of `ConfigDefinition` (it queries the persisted DB row + the
ProcessorEntity FK), so the only blocker is assembly compilation, not behavior.
