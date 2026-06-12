---
phase: 57-startup-config-schema-fetch-gate-a
verified: 2026-06-13T00:00:00Z
status: passed
score: 5/5 must-haves verified
overrides_applied: 0
deferred:
  - truth: "An orchestration whose graph includes a config-incompatible processor is blocked at orchestration-start with 422 via ProcessorLivenessValidator"
    addressed_in: "Phase 58"
    evidence: "REQUIREMENTS.md CFG-08/CFG-09 mapped to Phase 58; CONTEXT.md explicitly lists CFG-08/09 as out-of-scope; ROADMAP Phase 58 success criteria: 'RealStack E2E: a config-incompatible (never-Healthy) processor blocks orchestration start with 422'"
---

# Phase 57: Startup Config-Schema Fetch + Gate A — Verification Report

**Phase Goal:** At startup the processor fetches the `ConfigSchemaId` definition (extending Loop B at `ProcessorStartupOrchestrator.cs`, lifting the D-05 "never read the config schema id" carve-out) and stores it on `ProcessorContext`; Gate A then validates that the concrete config type *covers* that definition. On incompatibility the processor never reaches Healthy (withholds `MarkHealthy`, terminal — not retried); a missing definition stays transient (retry, boot-before-register); a null `ConfigSchemaId` skips Gate A. The spec-locked TOCTOU policy (frozen-once-referenced schema immutability) closes the schema-mutation window between this startup check and a later orchestration-start Gate B check.
**Verified:** 2026-06-13
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths (ROADMAP Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| SC1 | When `ConfigSchemaId` is non-null, startup fetches the config-schema definition over the bus and stores it on the processor context; a missing definition is transient (startup loop retries on `SchemaDefinitionNotFound`/timeout exactly as for input/output — boot-before-register tolerated) | VERIFIED | `ProcessorStartupOrchestrator.cs:140` loops over `{ InputSchemaId, OutputSchemaId, ConfigSchemaId }`; `ProcessorContext.SetDefinition` routes `ConfigSchemaId` to `ConfigDefinition`; `SchemaResolutionFacts.LoopB_Resolves_Input_Output_And_Config` asserts `context.ConfigDefinition == DefFor(configId)` and covers per-Id retry via `SchemaCapture.NextIsNotFound`; hermetic suite 26/26 green (slice) / 561 passed |
| SC2 | Gate A validates that the concrete config type covers the fetched config-schema definition (every payload valid under `ConfigSchemaId` deserializes into the config type — direction/fidelity locked during spec) | VERIFIED | `ConfigSchemaCoverageCheck.cs` (441 lines) implements the `schema ⊨ TConfig` structural walk; `ConfigSchemaCoverageFacts` 18-row `[Theory]` covers CLASH rows (#13, #5, #8, #17, #20, #22, #26, nested, unparseable) and FINE rows (#1, #7, #14, #16, #19, #21, #23, #24, #25) + null-skip; plan-01 spike confirmed the 3 highest-risk ASSUMED verdicts empirically; all rows green |
| SC3 | On a Gate A incompatibility the processor never reaches Healthy — `MarkHealthy` is withheld, the heartbeat no-ops, no `skp:{id}` L2 key is written, the reason is logged, and the incompatibility is terminal (not retried) | VERIFIED | `ProcessorStartupOrchestrator.cs:185-192`: on `!coverage.Covered` → `LogError` (processor id + ConfigSchemaId + ClashDetail) → `gate.MarkReady()` → `return` (no `MarkHealthy`, no bind); `DispatchBindSequenceFacts.GateA_Clash_Withholds_MarkHealthy_And_Bind` asserts `Assert.Empty(log)`, `gate.IsReady`, `connector.BoundQueueName is null`, `!context.IsHealthy`, and exactly one Error log entry |
| SC4 | A processor with a null `ConfigSchemaId` skips Gate A entirely and reaches Healthy on identity + input/output definitions alone | VERIFIED | `ProcessorStartupOrchestrator.cs:140`: null `ConfigSchemaId` null-skipped by the `if (schemaId is not { } id) continue;` guard; `ConfigSchemaCoverageCheck.Evaluate` returns `(true, null)` for null definition (line 66-67); `DispatchBindSequenceFacts.GateA_NullConfigSchemaId_Skips_And_Reaches_Healthy` asserts `log == ["connect","ready","markhealthy"]`, bound, `IsHealthy` |
| SC5 | The config-schema definition mutation window between startup Gate A and later Gate B is closed by the spec-locked TOCTOU policy, with a test recording the chosen mechanism | VERIFIED | `SchemaService.UpdateAsync` override rejects a `Definition` change on a referenced schema with `SchemaDefinitionFrozenException` → `SchemaDefinitionFrozenExceptionHandler` → HTTP 409 RFC-7807; `SchemaDefinitionFreezeFacts` 3/3 green (`Frozen_Definition_Mutation_Returns_409`, `NameDescription_Edit_On_Referenced_Schema_Returns_200`, `Unreferenced_Draft_Definition_Edit_Returns_200`); mechanism recorded in the class XML doc (`frozen-once-referenced`); two orchestrator fixes (commits `22f5fec`, `64d483c`) addressed D-07 false-409 on canonical-JSON normalization and number-token canonicalization |

**Score:** 5/5 truths verified

---

### Deferred Items

Items not yet met but explicitly addressed in a later milestone phase.

| # | Item | Addressed In | Evidence |
|---|------|-------------|----------|
| 1 | Real-stack E2E: config-incompatible processor blocked 422 at orchestration-start; config-compatible processor reaches Healthy and orchestrations start normally | Phase 58 | REQUIREMENTS.md CFG-08/CFG-09 mapped to Phase 58; CONTEXT.md §"Requirements" explicitly excludes CFG-08/09; ROADMAP Phase 58 success criteria state "RealStack E2E: config-incompatible processor blocked 422 via ProcessorLivenessValidator" |

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs` | Gate A schema⊨TConfig structural-walk covers-checker | VERIFIED | 441 lines; `public static (bool Covered, string? ClashDetail) Evaluate(string? configDefinition, Type configType)` at line 62; null-skip at line 66-67; `JsonSchema.FromText` parse gate + `JsonNode` walk; no `.Evaluate(` call (SSRF-safe) |
| `src/BaseProcessor.Core/Configuration/IConfigTypeProvider.cs` | Stubbable seam returning the concrete TConfig Type | VERIFIED | 22 lines; `public interface IConfigTypeProvider { Type Get(); }` |
| `src/BaseProcessor.Core/Configuration/BaseProcessorConfigTypeProvider.cs` | Default: resolves TConfig via GenericTypeArguments[0], no captive scoped instance | VERIFIED | 52 lines; double-checked locking; resolves BaseProcessor in throwaway scope, caches only the process-stable Type |
| `src/BaseProcessor.Core/Identity/ProcessorContext.cs` | `ConfigDefinition` property + `SetDefinition` 3rd branch | VERIFIED | `public string? ConfigDefinition { get; private set; }` at line 57; `if (schemaId == ConfigSchemaId) ConfigDefinition = definition;` at line 85-86; independent `if` (not else-if) |
| `src/BaseProcessor.Core/Identity/IProcessorContext.cs` | `ConfigDefinition` getter; D-05 carve-out removed from docs | VERIFIED | `string? ConfigDefinition { get; }` at line 61; no "never resolved" / "D-05" text remaining on ConfigSchemaId; WR-03 property list includes `ConfigDefinition` |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` | Loop B config fetch + Gate A call + decoupled MarkReady/MarkHealthy + IConfigTypeProvider ctor param | VERIFIED | Line 140: `new[] { context.InputSchemaId, context.OutputSchemaId, context.ConfigSchemaId }`; line 184: `ConfigSchemaCoverageCheck.Evaluate(context.ConfigDefinition, configType.Get())`; lines 185-192: clash path `LogError` + `gate.MarkReady()` + `return` (no bind, no MarkHealthy); lines 211-212: pass path `MarkHealthy()` then `gate.MarkReady()` |
| `src/BaseApi.Service/Features/Schema/SchemaDefinitionFrozenException.cs` | Domain exception carrying only the schema Guid | VERIFIED | `public sealed class SchemaDefinitionFrozenException : Exception`; `public Guid SchemaId`; ctor message contains only Guid (no DB internals) |
| `src/BaseApi.Service/Features/Schema/SchemaDefinitionFrozenExceptionHandler.cs` | IExceptionHandler → 409 RFC-7807; fast-bail on foreign exceptions | VERIFIED | Fast-bail at line 35: `if (exception is not SchemaDefinitionFrozenException ex) return false;`; `StatusCodes.Status409Conflict`; no `correlationId`/`Instance` assignment |
| `src/BaseApi.Service/Features/Schema/SchemaService.cs` | `UpdateAsync` override with freeze-on-referenced check; `DefinitionChanged` using canonical JSON | VERIFIED | `public override async Task<SchemaReadDto> UpdateAsync` at line 41; `AnyAsync(p => p.InputSchemaId == id \|\| p.OutputSchemaId == id \|\| p.ConfigSchemaId == id)` at line 55-56; `DefinitionChanged` at line 73; `WriteCanonical` with number normalization at lines 100-133 |
| `src/BaseApi.Core/Services/BaseService.cs` | `UpdateAsync` marked virtual | VERIFIED | `public virtual async Task<TRead> UpdateAsync` at line 113 |
| `src/BaseApi.Service/Features/Schema/SchemaServiceCollectionExtensions.cs` | `AddExceptionHandler<SchemaDefinitionFrozenExceptionHandler>()` registered | VERIFIED | Line 31: `services.AddExceptionHandler<SchemaDefinitionFrozenExceptionHandler>();` with ordering comment |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `ProcessorStartupOrchestrator` Loop B | `context.SetDefinition(ConfigSchemaId, def)` | `foreach` over `{ Input, Output, Config }` | WIRED | Line 140 iterates all three; line 157 calls `context.SetDefinition(id, def!.Message.Definition)` |
| `ProcessorStartupOrchestrator` (after Loop B, before bind) | `ConfigSchemaCoverageCheck.Evaluate` | `coverage = Evaluate(context.ConfigDefinition, configType.Get())` | WIRED | Line 184: `var coverage = ConfigSchemaCoverageCheck.Evaluate(context.ConfigDefinition, configType.Get());` |
| Gate A clash path | `gate.MarkReady()` WITHOUT `context.MarkHealthy()` | `if (!coverage.Covered) { ... gate.MarkReady(); return; }` | WIRED | Lines 186-192: LogError → gate.MarkReady() → return; `context.MarkHealthy()` is NOT called on this path |
| `SchemaService.UpdateAsync` override | `DbContext.Set<ProcessorEntity>().AnyAsync(...)` | referenced-query when Definition changes | WIRED | Lines 55-56: `await DbContext.Set<ProcessorEntity>().AsNoTracking().AnyAsync(p => p.InputSchemaId == id \|\| p.OutputSchemaId == id \|\| p.ConfigSchemaId == id, ct)` |
| `SchemaDefinitionFrozenException` | `SchemaDefinitionFrozenExceptionHandler` → 409 | `AddExceptionHandler<SchemaDefinitionFrozenExceptionHandler>()` | WIRED | Registered in `SchemaServiceCollectionExtensions.cs:31`; fast-bail in handler at line 35 |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `ProcessorStartupOrchestrator` | `context.ConfigDefinition` | `GetSchemaDefinition` bus request + `context.SetDefinition` | Yes — fetched from real `GetSchemaDefinitionConsumer` (DB-backed) over the bus | FLOWING |
| `ConfigSchemaCoverageCheck.Evaluate` | `configDefinition` (string) | `context.ConfigDefinition` passed at call site (line 184) | Yes — the definition fetched from Loop B is passed directly | FLOWING |
| `SchemaService.UpdateAsync` | `existing.Definition` | `DbContext.Set<SchemaEntity>().AsNoTracking().FirstOrDefaultAsync(...)` (line 43-44) | Yes — real EF Core query against DB | FLOWING |

---

### Behavioral Spot-Checks

Step 7b SKIPPED — production services (RabbitMQ bus, PostgreSQL) are not running in this verification environment. The hermetic test suite (MassTransit in-memory harness + WebApplicationFactory with real testcontainers) provides equivalent behavioral coverage per VALIDATION.md; all 26 Gate A + freeze facts are GREEN per the 57-03-SUMMARY authoritative run report and the commit message for `64d483c` ("Covers + freeze facts: 26/26 green").

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|---------|
| CFG-03 | 57-03 | Startup fetches config-schema definition over bus and stores it on processor context | SATISFIED | `ProcessorStartupOrchestrator.cs:140` Loop B 3rd iteration; `ProcessorContext.ConfigDefinition`; `SchemaResolutionFacts.LoopB_Resolves_Input_Output_And_Config` green |
| CFG-04 | 57-03 | Missing config-schema definition is transient — startup loop retries on `SchemaDefinitionNotFound`/timeout | SATISFIED | Loop B retry body is shared (lines 147-173); per-Id `SchemaCapture.NextIsNotFound` retry proven by `SchemaResolutionFacts` |
| CFG-05 | 57-02 | Gate A validates concrete config type covers fetched config-schema definition | SATISFIED | `ConfigSchemaCoverageCheck.Evaluate` implements full structural walk; 18-row `[Theory]` green; spike-confirmed CLASH verdicts encoded |
| CFG-06 | 57-03 | On Gate A incompatibility processor never reaches Healthy; MarkHealthy withheld; terminal; reason logged | SATISFIED | Orchestrator clash path: LogError → gate.MarkReady() → return; `GateA_Clash_Withholds_MarkHealthy_And_Bind` asserts no bind, not Healthy, gate.IsReady, single Error log |
| CFG-07 | 57-02 + 57-03 | Null `ConfigSchemaId` skips Gate A entirely and processor reaches Healthy | SATISFIED | null-skip in `Evaluate` (line 66-67); null `ConfigSchemaId` guard in Loop B (line 142); `GateA_NullConfigSchemaId_Skips_And_Reaches_Healthy` green |
| CFG-10 | 57-04 | TOCTOU window closed: config-schema definition mutation window between Gate A and Gate B is closed | SATISFIED | `SchemaService.UpdateAsync` freeze override + `SchemaDefinitionFrozenException` + 409 handler; all 3 `SchemaDefinitionFreezeFacts` green; two follow-up fixes (`22f5fec`, `64d483c`) closed D-07 false-409 regressions |
| CFG-08 | (none — not Phase 57) | Orchestration blocked 422 with config-incompatible processor (real-stack E2E) | DEFERRED | Mapped to Phase 58 in REQUIREMENTS.md; explicitly out-of-scope in CONTEXT.md |
| CFG-09 | (none — not Phase 57) | Config-compatible processor reaches Healthy, orchestrations start normally (real-stack E2E) | DEFERRED | Mapped to Phase 58 in REQUIREMENTS.md; explicitly out-of-scope in CONTEXT.md |

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `ConfigSchemaCoverageCheck.cs:379-381` | 379 | `items` array (tuple) form not walked; `prefixItems` not followed | Info | Conservative — tuple-item clashes are missed but never false-positive; config schemas rarely use tuple validation; documented in REVIEW.md IN-01 |
| `ConfigSchemaCoverageCheck.cs:98-121` | 98 | `$ref`/`allOf`/`oneOf`/`anyOf` composition keywords not followed | Info | Conservative — composition-path clashes are missed (FINE default); SSRF-safety by design; documented in REVIEW.md IN-02 |
| `ConfigSchemaCoverageCheck.cs:130-145` | 130 | `BuildClrLookup` last-writer-wins on duplicate JSON name; iteration order on shadowed properties undefined | Info | Non-blocking for current `ProcessorConfig : ProcessorConfig` hierarchy (base declares zero fields); documented in REVIEW.md IN-03 |

No Blocker-level anti-patterns. No stub/placeholder code in production artifacts. No TODO/FIXME in the critical path. No hardcoded empty returns in data-serving paths.

**Note on ROADMAP plan checkbox:** The ROADMAP shows `57-03-PLAN.md` as `[ ]` (unchecked). This is a stale documentation tracking artifact — Plan 57-03 was fully executed: commits `7275c7f` and `d0f636f` exist, `57-03-SUMMARY.md` records completion with `requirements-completed: [CFG-03, CFG-04, CFG-06, CFG-07]`, and the hermetic suite GREEN confirms the implementation. The checkbox was not updated in ROADMAP.md after execution; it does not represent a missing implementation.

---

### Human Verification Required

None. All phase behaviors have automated verification. The end-to-end real-stack proof (config-incompatible processor blocked 422 at orchestration-start) is Phase 58 (CFG-08/09), deliberately deferred.

---

### Gaps Summary

No gaps. All 5 ROADMAP Success Criteria are verified against the actual codebase:

- SC1 (CFG-03/04): Loop B 3rd iteration fetches config definition; per-Id transient retry proven hermetically.
- SC2 (CFG-05): `ConfigSchemaCoverageCheck.Evaluate` structural walk with 18 rule-table rows green; spike-grounded CLASH verdicts.
- SC3 (CFG-06): Clash path withholds `MarkHealthy`, skips bind, fires `gate.MarkReady()`, logs single Error; terminal (no retry).
- SC4 (CFG-07): Null `ConfigSchemaId` skips Gate A; processor reaches Healthy on normal pass path.
- SC5 (CFG-10): Frozen-once-referenced enforcement in `SchemaService.UpdateAsync`; 3/3 freeze facts green; two follow-up fixes closed D-07 edge cases (jsonb canonical-JSON normalization + number-token folding, WR-01/WR-02).

CFG-08/CFG-09 are out-of-scope (Phase 58, real-stack orchestration-gate proof) and correctly deferred.

---

_Verified: 2026-06-13_
_Verifier: Claude (gsd-verifier)_
