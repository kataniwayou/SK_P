---
phase: 57-startup-config-schema-fetch-gate-a
plan: 03
subsystem: api
tags: [gate-a, config-schema, startup-orchestrator, masstransit, system-text-json, jsonschema-net, dependency-injection, tdd]

# Dependency graph
requires:
  - phase: 57-startup-config-schema-fetch-gate-a (plan 01)
    provides: "Inverted SchemaResolutionFacts + extended DispatchBindSequenceFacts (Gate A clash/null-skip) RED against the not-yet-existing ProcessorContext.ConfigDefinition; InternalsVisibleTo BaseApi.Tests"
  - phase: 57-startup-config-schema-fetch-gate-a (plan 02)
    provides: "ConfigSchemaCoverageCheck.Evaluate(string?, Type) -> (bool Covered, string? ClashDetail) — the production schema⊨TConfig covers-checker this plan calls"
  - phase: 56-typed-base-config-seam
    provides: "BaseProcessor<TConfig> + ProcessorConfig.SerializerOptions binding contract"
provides:
  - "ProcessorContext.ConfigDefinition + IProcessorContext getter + SetDefinition 3rd branch (ConfigSchemaId -> ConfigDefinition, D-12/D-14)"
  - "ProcessorStartupOrchestrator Loop B fetches the non-null ConfigSchemaId definition (3rd iteration, CFG-03/04) and runs Gate A after Loop B + before the dispatch bind (CFG-05 call site, D-13)"
  - "Decoupled completion: gate.MarkReady on BOTH clash + pass/skip paths; MarkHealthy + endpoint bind ONLY on pass/skip (D-09 stay-up fail posture)"
  - "IConfigTypeProvider seam + BaseProcessorConfigTypeProvider default (resolves TConfig via GenericTypeArguments[0], no captive scoped instance — RESEARCH Pitfall 4); registered in the composition root"
affects: [58 (real-stack orchestration-gate proof CFG-08/09), 57-04 freeze-override follow-up (deferred freeze fact failure surfaced)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Stubbable type-resolution seam (IConfigTypeProvider mirroring ISourceHashProvider): supply the concrete TConfig Type without a ctor-coupled captive scoped BaseProcessor instance — read GenericTypeArguments[0] once and cache (process-stable Type)"
    - "Decoupled startup completion: gate.MarkReady (readiness) is separated from context.MarkHealthy (liveness) so a terminal Gate A clash stays up-but-not-Healthy (no K8s crash-loop) while withholding the dispatch bind"

key-files:
  created:
    - "src/BaseProcessor.Core/Configuration/IConfigTypeProvider.cs"
    - "src/BaseProcessor.Core/Configuration/BaseProcessorConfigTypeProvider.cs"
  modified:
    - "src/BaseProcessor.Core/Identity/ProcessorContext.cs (ConfigDefinition + SetDefinition 3rd if)"
    - "src/BaseProcessor.Core/Identity/IProcessorContext.cs (ConfigDefinition getter + WR-03 list + docs; lift D-05 carve-out)"
    - "src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs (Loop B 3rd iteration + Gate A + decouple + ctor param)"
    - "src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs (register IConfigTypeProvider)"
    - "tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs (StubConfigTypeProvider + valid-JSON-Schema defs)"
    - "tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs (StubConfigTypeProvider + clash-fact assertion fix)"
    - "tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs (StubConfigTypeProvider + GateAStubConfig helper)"
    - "tests/BaseApi.Tests/Processor/FakeProcessorContext.cs (ConfigDefinition member)"
    - "tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs (StubContext ConfigDefinition member)"

key-decisions:
  - "TConfig wiring = Option (b) IConfigTypeProvider singleton (NOT option a IServiceProvider-in-ctor). Cleaner DI, mirrors ISourceHashProvider, no captive-dependency risk; the default impl resolves BaseProcessor in a throwaway scope, reads only GenericTypeArguments[0], and caches the Type."
  - "Ctor signature change: ProcessorStartupOrchestrator gains an IConfigTypeProvider parameter (inserted after MeterProviderHolder, before IOptions). All three test constructions updated to pass a stub."
  - "Independent `if`s in SetDefinition (not else-if) — a shared schema id populates all matching slots, idempotent (RESEARCH Pattern 1 edge case)."

patterns-established:
  - "IConfigTypeProvider: the config-Type analog of ISourceHashProvider — stubbable startup seam returning a process-stable Type"
  - "MarkReady/MarkHealthy decoupling for a stay-up-but-not-Healthy terminal failure posture"

requirements-completed: [CFG-03, CFG-04, CFG-06, CFG-07]

# Metrics
duration: 20min
completed: 2026-06-12
---

# Phase 57 Plan 03: Startup Config-Schema Fetch + Gate A Wiring Summary

**Loop B now fetches the config-schema definition onto `ProcessorContext.ConfigDefinition` and the startup orchestrator runs Gate A (`ConfigSchemaCoverageCheck.Evaluate`) after Loop B and before the dispatch bind — a clash stays up (`gate.MarkReady`) but never Healthy (no `MarkHealthy`, no bind, one Error log, terminal), turning the Plan-01 cross-wave RED facts GREEN.**

## Performance

- **Duration:** 20 min
- **Started:** 2026-06-12T20:23:26Z
- **Completed:** 2026-06-12T20:43:59Z
- **Tasks:** 2
- **Files modified:** 11 (2 created, 9 modified)

## Accomplishments
- Added `ConfigDefinition` to `ProcessorContext` + `IProcessorContext` and routed `SetDefinition`'s 3rd independent `if` (`ConfigSchemaId` → `ConfigDefinition`), lifting the D-05 "never read the config schema id" carve-out (CFG-03). This resolved the three cross-wave compile-RED references (`SchemaResolutionFacts.cs:141,187`, `DispatchBindSequenceFacts.cs:88`) so the whole `tests/BaseApi.Tests` assembly compiles.
- Extended Loop B to a 3rd iteration over `context.ConfigSchemaId`, reusing the verbatim `GetSchemaDefinition` dual-response + retry; a null config id null-skips (CFG-07 fetch-side), a missing definition stays transient (CFG-04).
- Inserted Gate A AFTER Loop B and BEFORE the dispatch bind (D-13): `ConfigSchemaCoverageCheck.Evaluate(context.ConfigDefinition, configType.Get())`. On clash → one `LogError` (processor id + ConfigSchemaId + ClashDetail, D-10) + `gate.MarkReady()` + terminal `return` (no bind, no `MarkHealthy`, D-09/D-11). On pass/skip → bind → `MarkHealthy` → `MarkReady` (Healthy).
- Wired the concrete `TConfig` Type via a new `IConfigTypeProvider` seam (option b) + a default `BaseProcessorConfigTypeProvider` that reads `GetType().BaseType!.GenericTypeArguments[0]` once without holding a scoped instance (Pitfall 4); registered in the composition root next to `ISourceHashProvider`.
- D-14 honored: `ConfigDefinition` lives on the context only — `ProcessorLivenessHeartbeat` / the L2 `ProcessorProjection` are untouched.

## Task Commits

1. **Task 1: Add ConfigDefinition to ProcessorContext + IProcessorContext, route SetDefinition 3rd branch** — `7275c7f` (feat)
2. **Task 2: Extend Loop B + insert Gate A + decouple MarkReady from MarkHealthy + IConfigTypeProvider wiring** — `d0f636f` (feat)

_TDD note: this `type: execute` plan's tasks are `tdd="true"`, but the RED `test(...)` gates predate it — they are Plan 57-01's commits `da0496c` (inverted SchemaResolutionFacts + extended DispatchBindSequenceFacts) and `8d340e4` (ConfigSchemaCoverageFacts). This plan supplies the GREEN feat gates above. No REFACTOR commit needed._

## Files Created/Modified
- `src/BaseProcessor.Core/Configuration/IConfigTypeProvider.cs` — NEW stubbable seam returning the concrete `TConfig` Type for Gate A.
- `src/BaseProcessor.Core/Configuration/BaseProcessorConfigTypeProvider.cs` — NEW default: resolves the author `BaseProcessor` in a throwaway scope, reads `GenericTypeArguments[0]`, caches the process-stable Type (no captive instance).
- `src/BaseProcessor.Core/Identity/ProcessorContext.cs` — `ConfigDefinition` auto-property + 3rd independent `if` in `SetDefinition`.
- `src/BaseProcessor.Core/Identity/IProcessorContext.cs` — `ConfigDefinition` getter, WR-03 property list + Loop-B/ConfigSchemaId/SetDefinition doc updates.
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — Loop B 3rd iteration; Gate A call + clash/return; decoupled `MarkReady`/`MarkHealthy`; `IConfigTypeProvider` ctor param; class-doc updates.
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` — registers `IConfigTypeProvider` → `BaseProcessorConfigTypeProvider`.
- `tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs` — `StubConfigTypeProvider` helper + `GateAStubConfig`/`GateAMode` (a CLR-enum `Mode` for the row-#13 clash); ctor call updated.
- `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs` — pass `StubConfigTypeProvider`; responder now returns VALID JSON Schema defs (so Gate A passes); assertions compare the schema string.
- `tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs` — pass `StubConfigTypeProvider(typeof(GateAStubConfig))`; clash fact now asserts an empty completion log + `gate.IsReady`.
- `tests/BaseApi.Tests/Processor/FakeProcessorContext.cs`, `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs` — added the new `ConfigDefinition` interface member.

## Decisions Made
- **TConfig wiring = Option (b)** `IConfigTypeProvider` singleton, not option (a) `IServiceProvider` in the ctor. Rationale: cleaner DI surface, mirrors the existing `ISourceHashProvider` seam, and the provider resolves the (possibly Scoped) `BaseProcessor` in a throwaway scope reading only `GenericTypeArguments[0]` — eliminating the captive-dependency risk (Pitfall 4) entirely rather than relying on call-site discipline.
- **Resulting ctor change:** `ProcessorStartupOrchestrator(...)` gains `IConfigTypeProvider configType` inserted after `MeterProviderHolder` and before `IOptions<ProcessorLivenessOptions>`. All three direct test constructions (`SchemaResolutionFacts`, `DispatchBindSequenceFacts` ×2 helpers, `IdentityResolutionFacts`) were updated to pass a stub.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] SchemaResolutionFacts config definition was unparseable, so Gate A would reject it and withhold Healthy**
- **Found during:** Task 2
- **Issue:** The Plan-01 `CapturingSchemaResponder` returned `"def-for-{id:N}"` (a bare, non-JSON string) as every definition, and the facts assert `context.IsHealthy`. Once Gate A runs on the config definition, an unparseable string is a terminal clash (`Covered=false`) → `MarkHealthy` withheld → `Assert.True(context.IsHealthy)` would fail / `AdvanceUntilAsync(IsHealthy)` would spin to timeout. (Confirmed `JsonSchema.FromText("def-for-...")` throws via a throwaway probe.)
- **Fix:** The responder now returns a VALID parseable Draft-2020-12 object schema carrying the id in `$comment` (`{"type":"object","$comment":"def-for-{id:N}"}`) via a shared `DefFor(id)` helper; the three `def-for-{...:N}` equality assertions compare against `DefFor(...)`. An empty-object schema has no both-present property → Gate A passes → the processor reaches Healthy, preserving the facts' actual subject (config IS fetched/stored/retried — CFG-03/04), not Gate A.
- **Files modified:** `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs`
- **Verification:** Both `SchemaResolutionFacts` facts GREEN; processor reaches Healthy; config id queried + `ConfigDefinition` stored.
- **Committed in:** `d0f636f` (Task 2 commit)

**2. [Rule 1 - Bug] DispatchBindSequenceFacts clash assertion `["ready"]` was unachievable by the recording model**
- **Found during:** Task 2
- **Issue:** The Plan-01 clash fact asserted the ordered completion log equals `["ready"]`, intending it to prove `gate.MarkReady()` fired. But in the recording fakes `"ready"` is appended ONLY by the `RecordingHandle.Ready` continuation on the BIND path; `gate.MarkReady()` is a separate `StartupGate` latch that never writes to the log. On the clash path the completion block never runs, so the log is genuinely `[]`, not `["ready"]` — the assertion could never pass.
- **Fix:** Changed the clash fact to assert the real observable contract: `Assert.Empty(log)` (no connect/ready/markhealthy — the completion block was skipped) AND `Assert.True(gate.IsReady)` (gate.MarkReady DID fire → readiness green → no K8s crash-loop, the T-57-05 mitigation). The driver now also returns the `StartupGate`. The null-skip fact and both pass-path facts are unchanged and still GREEN.
- **Files modified:** `tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs`
- **Verification:** `GateA_Clash_Withholds_MarkHealthy_And_Bind` GREEN (empty log, gate ready, no bind, not Healthy, exactly one Error log); pass-path + null-skip facts unregressed.
- **Committed in:** `d0f636f` (Task 2 commit)

**3. [Rule 3 - Blocking] Two other IProcessorContext fakes needed the new ConfigDefinition member**
- **Found during:** Task 2
- **Issue:** Adding `ConfigDefinition` to `IProcessorContext` broke `FakeProcessorContext` and `ProcessorIdEnricherTests.StubContext` (CS0535 — interface member not implemented).
- **Fix:** Added the `ConfigDefinition` member to both fakes.
- **Files modified:** `tests/BaseApi.Tests/Processor/FakeProcessorContext.cs`, `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs`
- **Verification:** Test assembly compiles 0-error.
- **Committed in:** `d0f636f` (Task 2 commit)

---

**Total deviations:** 3 auto-fixed (2 bugs in Plan-01 test seams, 1 blocking interface-member break). No production-behavior scope creep — the orchestrator behavior is exactly as planned (D-09/D-11/D-12/D-13/D-14); the bug fixes corrected test assertions that were unachievable against the real checker.

## Deferred Issues

**`SchemaDefinitionFreezeFacts.NameDescription_Edit_On_Referenced_Schema_Returns_200` fails (Plan 57-04 defect, out of scope).** With the assembly now compiling, the deferred CFG-10 freeze facts were executed for the first time: `Frozen_Definition_Mutation_Returns_409` and `Unreferenced_Draft_Definition_Edit_Returns_200` are GREEN, but the Name/Description-only edit on a referenced schema returns **409 instead of 200** (`SchemaDefinitionFreezeFacts.cs:126`). The root cause is entirely in Plan 57-04's freeze override (`SchemaService.UpdateAsync`, commit `371acf1`) — a Definition-change-detection / normalization bug violating D-07 (only a *changed* Definition is frozen). This is NOT in any file 57-03 touched and predates this plan; per the SCOPE BOUNDARY it is logged to `deferred-items.md`, not fixed. Owner: Plan 57-04 follow-up / verifier. CFG-10 should not be considered fully proven until this passes.

## Verification

- **BaseProcessor.Core build:** 0-warning in BOTH Debug and Release.
- **Gate A slice (this plan's owned facts):** `SchemaResolutionFacts` + `DispatchBindSequenceFacts` + `ConfigSchemaCoverageFacts` = **29/29 GREEN** (RED → GREEN turn for the Plan-01 inverted/extended/coverage facts).
- **Full hermetic suite** (`--filter-not-trait "Category=RealStack"`): **557 / 558 succeeded**, 0 skipped. The single failure is the out-of-scope pre-existing Plan-04 freeze fact above (`NameDescription_Edit_On_Referenced_Schema_Returns_200`). The Phase-56 baseline was 530/530; the suite has since grown (Plan-01 added the Gate A / freeze facts), so the comparable count is 557 green of 558 with the lone deferred Plan-04 failure.

## Known Stubs
None — `ConfigSchemaCoverageCheck` is the real walk (Plan 02); this plan adds only the production wiring + the test-side `StubConfigTypeProvider` (a legitimate test double, mirroring the existing `StubConnector`/`StubMeterProviderHolder`).

## Threat Flags
None — no new network endpoint, auth path, file access, or schema-write surface. Gate A reads an in-memory schema string + a CLR Type (no `Evaluate`, no external `$ref` — T-57-03 preserved). The three threat-register mitigations (T-57-05 readiness-green-on-clash, T-57-06 no-bind-on-clash, T-57-07 structural-only Error log) are implemented and asserted by the clash fact.

## Issues Encountered
- `JsonSchema.Net` 9.x parse semantics: confirmed via a throwaway probe that `JsonSchema.FromText` on a bare non-JSON string throws — which is why the unparseable `def-for-*` placeholder in `SchemaResolutionFacts` had to become a valid schema (Deviation 1).
- The clash-fact log model (`gate.MarkReady` is a separate latch, not a log entry) drove Deviation 2.

## Next Phase Readiness
- Gate A is fully wired: config def fetched (CFG-03/04), clash → never Healthy + terminal + logged (CFG-06), null skip → Healthy (CFG-07). Phase 58's real-stack proof (a config-incompatible processor blocked 422 at orchestration-start, CFG-08/09) can build on the existing liveness gate — a clash means no `skp:{id}` L2 key.
- **Blocker for CFG-10 sign-off:** the Plan-04 freeze fact failure must be fixed before the phase's CFG-10 requirement is fully proven (see Deferred Issues / `deferred-items.md`).

## Self-Check: PASSED

---
*Phase: 57-startup-config-schema-fetch-gate-a*
*Completed: 2026-06-12*
