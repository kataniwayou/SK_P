---
phase: 14-validation-gates-dfs-schema-edge-payload-config-schema
plan: 01
subsystem: orchestration-error-path + schema-config
tags: [exception-handler, problem-details, 422, ssrf, json-schema, di-ordering]
requires:
  - Phase 4 IExceptionHandler chain (NotFound/Validation/DbUpdate)
  - Phase 4 CustomizeProblemDetails customizer (correlationId + instance)
  - Phase 8 SchemaDtoValidator (SSRF lockdown source)
  - Phase 9 OrchestrationServiceCollectionExtensions
provides:
  - OrchestrationValidationException (single domain exception, 4 gate factories)
  - OrchestrationValidationExceptionHandler (422 + RFC 7807, fast-bail)
  - AddBaseApiFallbackHandler (split-out catch-all, registered last)
  - JsonSchemaConfig.DefaultOptions (single SSRF source of truth)
affects:
  - 14-02 (cycle/missingStep gates throw OrchestrationValidationException.Cycle/MissingStep)
  - 14-03 (schema-edge gate throws OrchestrationValidationException.SchemaEdge)
  - 14-04 (payload-config gate throws PayloadConfigSchema + consumes JsonSchemaConfig)
tech-stack:
  added: []
  patterns:
    - "Split-Fallback handler ordering: catch-all registered separately, LAST, by composition root"
    - "One domain exception + gate discriminator + co-located offending records (not per-gate subclasses)"
    - "Single static SSRF-config type; consumers reference DefaultOptions to fire the cctor (Pitfall 3)"
key-files:
  created:
    - src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationValidationExceptionHandler.cs
    - src/BaseApi.Service/Features/Schema/JsonSchemaConfig.cs
  modified:
    - src/BaseApi.Core/DependencyInjection/ErrorHandlingServiceCollectionExtensions.cs
    - src/BaseApi.Service/Program.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs
    - src/BaseApi.Service/Features/Schema/SchemaDtoValidator.cs
decisions:
  - "D-04 resolved via split-Fallback option (a-2): FallbackExceptionHandler removed from AddBaseApiErrorHandling; new public AddBaseApiFallbackHandler called last in Program.cs after AddAppFeatures."
  - "ErrorHandlingServiceCollectionExtensions class promoted internal->public (Rule 3) so the public AddBaseApiFallbackHandler extension is visible cross-assembly; AddBaseApiErrorHandling stays internal."
  - "One OrchestrationValidationException (D-01) with 4 static factories + camelCase offending records; gate discriminators cycle|missingStep|schemaEdge|payloadConfigSchema (D-03)."
  - "JsonSchemaConfig (D-05) owns the SSRF lockdown; SchemaDtoValidator references JsonSchemaConfig.DefaultOptions to fire the cctor before evaluation (D-06 / Pitfall 3)."
metrics:
  duration: ~12min
  completed: 2026-05-29
  tasks: 3
  files: 7
---

# Phase 14 Plan 01: Validation 422 Error Path + Shared JsonSchemaConfig Summary

One-liner: Built the OrchestrationValidationException → HTTP 422 + RFC 7807 error path (split-Fallback D-04 ordering, fast-bail handler, `{ errors: { gate, offending } }` envelope) and extracted the SSRF-locked `JsonSchemaConfig` that all three downstream gate plans depend on — all bisect-friendly, suite stays 181/181 GREEN.

## What Was Built

- **Task 1 (refactor) — D-04 split-Fallback:** Removed `AddExceptionHandler<FallbackExceptionHandler>()` from `AddBaseApiErrorHandling`; added a new `public static AddBaseApiFallbackHandler()`; promoted the containing class `internal -> public` (extension visibility across assemblies); `Program.cs` calls it last, after `AddAppFeatures()`. Walk order now: NotFound → Validation → DbUpdate → [domain handlers from AddAppFeatures] → Fallback.
- **Task 2 (feat) — exception + 422 handler:** `OrchestrationValidationException` (single sealed type, 4 static factories `Cycle`/`MissingStep`/`SchemaEdge`/`PayloadConfigSchema`, co-located camelCase `*Offending` records, `ErrorsExtension => { gate, offending }`). `OrchestrationValidationExceptionHandler` mirrors `NotFoundExceptionHandler`: fast-bail `is not ... return false`, `Status422UnprocessableEntity`, sets only Status/Title/Detail/errors (no correlationId/instance — Phase 4 customizer owns those). Registered in `AddOrchestrationFeature` so it lands before the split Fallback.
- **Task 3 (refactor, tdd-keep-green) — JsonSchemaConfig:** New static `JsonSchemaConfig` owns `Dialect.Default = Draft202012` + `SchemaRegistry.Global.Fetch = (_,_) => null` in its static ctor and exposes `DefaultOptions`. Both Schema validators drop their inline lockdown and reference `JsonSchemaConfig.DefaultOptions` in `MetaSchemas.Draft202012.Evaluate(...)`, which fires the cctor before evaluation (Pitfall 3). The pre-existing `ErrorMappingFacts` SSRF `<500ms` guard is the RED canary — it stayed GREEN through the refactor.

## Verification

- `dotnet build` Debug + Release: exit 0, **0 warnings** (TreatWarningsAsErrors).
- Full suite: **181/181 GREEN** (run after Task 1 and after Task 3) — includes `StartCleanupFacts` (Fallback still last-walked → forced non-domain throw stays 500), `ProgramMinimalityFacts` (one composition line added), `ErrorMappingFacts` (SSRF `<500ms` + draft-2020-12 non-regression), and all `Schema*` facts.
- Note: the MTP test runner ignores `--filter` (warning MTP0001) and runs the whole assembly; the targeted facts are a strict subset of the GREEN run.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `ErrorHandlingServiceCollectionExtensions` promoted internal → public**
- **Found during:** Task 1 (build CS1061 — `IServiceCollection` does not contain `AddBaseApiFallbackHandler`).
- **Issue:** A `public static` extension method on an `internal static` class is NOT accessible cross-assembly — the containing class's accessibility governs. `Program.cs` (BaseApi.Service) could not see the method while the class stayed `internal`.
- **Fix:** Changed the class to `public static`; kept `AddBaseApiErrorHandling` `internal` (Core-only) and `AddBaseApiFallbackHandler` `public` (cross-assembly). The plan's `<interfaces>` note anticipated this ("`internal` would be invisible … must be `public static`, mirrors how AddBaseApi is public") but specified only the method visibility; the class promotion is the actual mechanism.
- **Files modified:** src/BaseApi.Core/DependencyInjection/ErrorHandlingServiceCollectionExtensions.cs
- **Commit:** bbefc95

**2. [Rule 3 - Plan-grep alignment] Rephrased SchemaDtoValidator doc/inline comments to clear lockdown tokens**
- **Found during:** Task 3 acceptance-criteria check ("grep `SchemaRegistry.Global.Fetch` / `Dialect.Default` returns ZERO hits in SchemaDtoValidator.cs").
- **Issue:** The executable lockdown was removed, but XML-doc and inline comments still mentioned the literal `Dialect.Default` / `SchemaRegistry.Global.Fetch` tokens, which a literal grep-empty assertion would flag.
- **Fix:** Rephrased the comments to "dialect pin (draft 2020-12)" and "global no-op fetcher" — preserves the educational intent without the literal tokens (Plan 06-01 / 08-01 / 08-02 rephrase precedent). No code change.
- **Files modified:** src/BaseApi.Service/Features/Schema/SchemaDtoValidator.cs
- **Commit:** 3923e31

## TDD Gate Compliance

Task 3 is `tdd="true"` but a *refactor-keep-green* shape: the RED behavior guard (`ErrorMappingFacts.Create_Schema_Invalid_JsonSchema_Returns400_NoOutboundCall`, the `<500ms` SSRF assertion) and the Schema validity facts already exist from Phase 8 and were already GREEN. No new failing test was authored because the contract under test is unchanged — the refactor merely relocates the SSRF lockdown source. The GREEN gate is the post-refactor 181/181 run. This is the documented pattern for "refactor that must not regress an existing guard," not a missing RED gate.

## Threat Surface

No new threat surface beyond the plan's `<threat_model>`. T-14-01 (SSRF) mitigation now centralized in `JsonSchemaConfig`; T-14-02 (422 body info-disclosure) handler carries only Guids/flattened messages; T-14-03 (wrong-handler-claims) fast-bail + split-Fallback last-walked. All three remain mitigated.

## Commits

- bbefc95 `refactor(14-01): split FallbackExceptionHandler out of Core chain (D-04)`
- 8c94bd3 `feat(14-01): OrchestrationValidationException + 422 handler (D-01/D-02/D-03)`
- 3923e31 `refactor(14-01): extract JsonSchemaConfig as single SSRF source (D-05/D-06)`

## Self-Check: PASSED

All 3 created files present; all 3 task commits (bbefc95, 8c94bd3, 3923e31) present in git log.
