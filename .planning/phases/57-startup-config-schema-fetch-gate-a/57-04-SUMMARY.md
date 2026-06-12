---
phase: 57-startup-config-schema-fetch-gate-a
plan: 04
subsystem: webapi-schema-crud
tags: [cfg-10, toctou, frozen-once-referenced, rfc-7807, iexceptionhandler, ef-core, cross-entity-query]

# Dependency graph
requires:
  - phase: 57-startup-config-schema-fetch-gate-a (plan 01)
    provides: "SchemaDefinitionFreezeFacts — the 3 WebApplicationFactory RED integration facts this plan turns GREEN (once the shared test assembly compiles)"
provides:
  - "Frozen-once-referenced enforcement: SchemaService.UpdateAsync rejects a Definition change on any schema referenced by a ProcessorEntity FK (Input/Output/ConfigSchemaId) with SchemaDefinitionFrozenException -> HTTP 409 RFC-7807"
  - "BaseService.UpdateAsync is now virtual (additive) — the SchemaService override hook"
  - "SchemaDefinitionFrozenException (domain) + SchemaDefinitionFrozenExceptionHandler (409, registered ahead of FallbackExceptionHandler)"
  - "Closes the Gate-A<->Gate-B TOCTOU window by construction (T-57-08): the schema id a processor validated at startup always denotes the same Definition Gate B later reads"
affects: [58-real-stack-orchestration-gate-proof]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "virtual base verb + single derived override layering a precondition in FRONT of the inherited verb order (freeze check runs before base.UpdateAsync because the mapper mutates the pre-mutation Definition)"
    - "cross-entity referenced-query via DbContext.Set<ProcessorEntity>().AnyAsync(...) — keeps IRepository at its locked 5 methods"
    - "domain exception -> IExceptionHandler -> RFC-7807 409, registered inside AddSchemaFeature so it lands ahead of the LAST-registered FallbackExceptionHandler (walk order == registration order)"

key-files:
  created:
    - "src/BaseApi.Service/Features/Schema/SchemaDefinitionFrozenException.cs"
    - "src/BaseApi.Service/Features/Schema/SchemaDefinitionFrozenExceptionHandler.cs"
  modified:
    - "src/BaseApi.Core/Services/BaseService.cs (UpdateAsync -> virtual)"
    - "src/BaseApi.Service/Features/Schema/SchemaService.cs (UpdateAsync override + freeze check)"
    - "src/BaseApi.Service/Features/Schema/SchemaServiceCollectionExtensions.cs (handler registration)"

key-decisions:
  - "Freeze check gated on !string.Equals(existing.Definition, dto.Definition, StringComparison.Ordinal) so Name/Description-only edits on a referenced schema return 200 (D-07)"
  - "Freeze check runs BEFORE base.UpdateAsync (which mutates the entity via the mapper, losing the pre-mutation Definition); does NOT route through SyncJunctionsAsync"
  - "Handler sets only Status/Title/Detail (409); correlationId + Instance left to the CustomizeProblemDetails customizer (D-02); body carries only the schema Guid (T-57-09 safe-disclosure)"
  - "All three ProcessorEntity FK roles count as referenced (D-06 uniform); AssignmentEntity has no direct schema FK (RESEARCH A5) so the ProcessorEntity AnyAsync is sufficient"

requirements-completed: [CFG-10]

# Metrics
duration: 3min
completed: 2026-06-12
---

# Phase 57 Plan 04: Frozen-Once-Referenced Schema Definition Gate (CFG-10) Summary

**A schema's `Definition` freezes the moment any `ProcessorEntity` FK (Input/Output/ConfigSchemaId) references it — `SchemaService.UpdateAsync` rejects a body change on a referenced schema with `SchemaDefinitionFrozenException` -> RFC-7807 409, closing the Gate-A<->Gate-B TOCTOU window by construction (D-05/D-06/D-08), while Name/Description and unreferenced-draft edits flow through (D-07/D-06).**

## What Was Built

1. **`BaseService.UpdateAsync` -> `virtual`** (`src/BaseApi.Core/Services/BaseService.cs`) — additive; the sole override is `SchemaService`. No behavior change for any other entity. XML-doc notes the CFG-10 rationale.

2. **`SchemaDefinitionFrozenException`** (NEW) — sealed domain `Exception` carrying only the schema `Guid` (information-disclosure guard, mirrors `NotFoundException` IN-02).

3. **`SchemaDefinitionFrozenExceptionHandler`** (NEW) — `IExceptionHandler` mirroring `OrchestrationValidationExceptionHandler` (422 -> 409, the `DbUpdateExceptionHandler` state-conflict framing). Fast-bails on foreign exceptions; sets only Status/Title/Detail; leaves correlationId + Instance to the customizer.

4. **`SchemaService.UpdateAsync` override** — loads the persisted row (`AsNoTracking`); if `dto.Definition` differs (`StringComparison.Ordinal`) AND any `ProcessorEntity` FK references the id (`DbContext.Set<ProcessorEntity>().AnyAsync(p => p.InputSchemaId == id || p.OutputSchemaId == id || p.ConfigSchemaId == id)`), throws `SchemaDefinitionFrozenException(id)`. Otherwise falls through to `base.UpdateAsync` (Name/Description edits, unchanged-Definition edits, NotFound->404, unreferenced-draft edits all preserved).

5. **Handler registration** — `services.AddExceptionHandler<SchemaDefinitionFrozenExceptionHandler>()` inside `AddSchemaFeature` (runs via `AddAppFeatures`), landing after the Core handlers and before the LAST-registered `FallbackExceptionHandler`.

## Task Commits

1. **Task 1: UpdateAsync virtual + exception + 409 handler** — `207cb3d` (feat)
2. **Task 2: SchemaService.UpdateAsync freeze override + handler registration** — `371acf1` (feat)

## Verification

- **Production builds:** `src/BaseApi.Core` + `src/BaseApi.Service` build clean in BOTH Debug and Release — 0 warnings (the standing build gate).
- **Acceptance grep (all PASS):**
  - `BaseService.cs` -> `public virtual async Task<TRead> UpdateAsync`.
  - `SchemaDefinitionFrozenException.cs` -> `public sealed class SchemaDefinitionFrozenException : Exception` + `public Guid SchemaId`; ctor message contains only the Guid.
  - `SchemaDefinitionFrozenExceptionHandler.cs` -> `if (exception is not SchemaDefinitionFrozenException` (fast-bail) + `StatusCodes.Status409Conflict`; NO `correlationId`/`Instance` assignment.
  - `SchemaService.cs` -> `public override async Task<SchemaReadDto> UpdateAsync`, `AnyAsync`, `p.InputSchemaId == id || p.OutputSchemaId == id || p.ConfigSchemaId == id`, `throw new SchemaDefinitionFrozenException(id)`, `string.Equals(existing.Definition, dto.Definition, StringComparison.Ordinal)`.
  - `SchemaServiceCollectionExtensions.cs` -> `AddExceptionHandler<SchemaDefinitionFrozenExceptionHandler>()`.

## Deferred Issues

**`SchemaDefinitionFreezeFacts` runtime GREEN verification deferred to Plan 57-03 landing.**

The shared `tests/BaseApi.Tests` assembly does not compile until Plan 57-03 lands
`ProcessorContext.ConfigDefinition`. Plan 57-01 seeded compile-RED references to that not-yet-existing
member (`SchemaResolutionFacts.cs:141,187`, `DispatchBindSequenceFacts.cs:88` — per the 57-01-SUMMARY
RED-State Map, resolved by Plan 02/03, NOT Plan 04). Plan 57-03 (wave 2, owns `ConfigDefinition`) had
not executed when this plan (57-04, wave 1) ran. The test assembly's ONLY compile errors are those 3
`ConfigDefinition` references — none touch the 57-04 production surface. The freeze logic is independent
of `ConfigDefinition` (it queries the persisted DB row + the ProcessorEntity FK), so the sole blocker is
assembly compilation, not behavior.

**Action once 57-03 lands:** run
`dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-class "*SchemaDefinitionFreezeFacts*"`
and confirm all 3 facts GREEN (`Frozen_Definition_Mutation_Returns_409`,
`NameDescription_Edit_On_Referenced_Schema_Returns_200`, `Unreferenced_Draft_Definition_Edit_Returns_200`).
Recorded in `deferred-items.md`.

## Deviations from Plan

None — plan executed exactly as written. The freeze-facts verification could not be run only because of
a cross-plan wave-ordering artifact (the shared test assembly's Plan-03 compile-RED seam); no production
code deviated from the plan, and no other plan's owned files were modified. Logged as a Deferred Issue
above + `deferred-items.md`.

## Threat Model Coverage

| Threat ID | Disposition | Status |
|-----------|-------------|--------|
| T-57-08 (TOCTOU Gate A<->B) | mitigate | Closed by construction — referenced-schema Definition cannot change (409). |
| T-57-09 (info disclosure in 409 body) | mitigate | Body carries only the schema Guid + generic message; referenced-check is an internal `AnyAsync` boolean. |
| T-57-10 (handler walk-order regression) | mitigate | Handler fast-bails on foreign exceptions; registered ahead of `FallbackExceptionHandler`. |

No new threat surface beyond the plan's `<threat_model>`.

## Self-Check: PASSED

- All 2 created files + 3 modified files + SUMMARY exist on disk.
- Both task commits exist: `207cb3d`, `371acf1`.
- Production builds clean (Debug + Release, 0 warnings) for `BaseApi.Core` + `BaseApi.Service`.
- Test-suite GREEN verification deferred to Plan 57-03 (shared-assembly compile blocker) — see Deferred Issues.
