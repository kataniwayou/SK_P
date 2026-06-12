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

---

## 57-03 follow-up: SchemaDefinitionFreezeFacts now RUN — one Plan-04 freeze fact FAILS (out of scope for 57-03)

**Logged:** 2026-06-12 (during 57-03 execution — the deferred runtime proof above).

**What:** With Plan 57-03 having landed `ProcessorContext.ConfigDefinition`, the `BaseApi.Tests` assembly
compiles and the deferred freeze facts were executed. Result:
- `Frozen_Definition_Mutation_Returns_409` — GREEN.
- `Unreferenced_Draft_Definition_Edit_Returns_200` — GREEN.
- **`NameDescription_Edit_On_Referenced_Schema_Returns_200` — FAILS:**
  `Assert.Equal() Expected: OK, Actual: Conflict` at `SchemaDefinitionFreezeFacts.cs:126`. A
  Name/Description-only PUT on a *referenced* schema returns **409 Conflict** instead of **200 OK**.

**Root cause (scope = Plan 57-04, NOT 57-03):** the failure is entirely in Plan 57-04's freeze override
(`SchemaService.UpdateAsync` + `SchemaDefinitionFrozenException`, commit `371acf1`). D-07 requires that only
a *changed* `Definition` on a referenced schema is frozen — Name/Description edits with an unchanged
Definition must pass (200). The override appears to flag a clash even when the incoming `Definition` is
effectively unchanged — a likely Definition-equality/normalization bug (raw-string `Ordinal` compare tripped
by whitespace/canonicalization, or the DTO carrying a re-serialized Definition that differs byte-for-byte
from the persisted one).

**Why NOT fixed by 57-03:** Plan 57-03 owns CFG-03/04/06/07 in `BaseProcessor.Core` (the Loop B fetch +
Gate A + decouple). The freeze path (CFG-10) is Plan 57-04's surface; its files were never touched by
57-03's two commits (`7275c7f`, `d0f636f`) and `371acf1` predates them. Per the executor SCOPE BOUNDARY,
a pre-existing failure in unrelated files is logged, not fixed.

**Action required (Plan 57-04 follow-up / verifier):** fix the Definition-change detection in
`SchemaService.UpdateAsync` so a Name/Description-only edit on a referenced schema returns 200. Re-run
`--filter-class "*SchemaDefinitionFreezeFacts*"` → all 3 GREEN. Plan 57-04's REQUIREMENTS checkbox for
CFG-10 should NOT be considered fully proven until this passes.
