---
phase: 27-execution-round-trip
plan: 01
subsystem: infra
tags: [json-schema, jsonschema-net, ssrf, masstransit, config-binding, dotnet, processor]

# Dependency graph
requires:
  - phase: 26-baseprocessor-core-library-identity-liveness
    provides: "BaseProcessor abstract seam (protected ProcessAsync), ProcessResult minimal record, ProcessorLivenessOptions, BaseProcessor.Core firewalled library"
provides:
  - "ProcessorJsonSchemaValidator.TryValidate — SSRF-locked Json.Schema port into BaseProcessor.Core (null/whitespace skips, unparseable/unresolvable-$ref returns invalid, never crashes)"
  - "ProcessResult(string OutputData) — output-data-only record the consumer is built against"
  - "BaseProcessor.ExecuteAsync — internal framework invoker seam forwarding to the protected ProcessAsync"
  - "ProcessorLivenessOptions.ExecutionDataTtlSeconds — CONFIG-02 TTL knob (key ExecutionDataTtl, default 300)"
affects: [27-02 (EntryStepDispatchConsumer consumes validator + ProcessResult + ExecuteAsync + TTL), 27-03 (startup wiring), 28-sourcehash-sample-e2e]

# Tech tracking
tech-stack:
  added: [JsonSchema.Net (Json.Schema) 9.2.1 PackageReference on BaseProcessor.Core (CPM-pinned)]
  patterns:
    - "Firewall-preserving validator PORT (copy the SSRF pattern, never reference BaseApi.Service)"
    - "internal forwarder seam so a same-assembly consumer reaches a protected abstract member without widening it"
    - "[ConfigurationKeyName] bare-key binding with Seconds-suffixed property + baked default"

key-files:
  created:
    - src/BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs
    - tests/BaseApi.Tests/Processor/ProcessorJsonSchemaValidatorFacts.cs
  modified:
    - src/BaseProcessor.Core/BaseProcessor.Core.csproj
    - src/BaseProcessor.Core/Processing/ProcessResult.cs
    - src/BaseProcessor.Core/Processing/BaseProcessor.cs
    - src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs
    - tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs
    - tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs

key-decisions:
  - "An unresolvable external $ref surfaces as a JsonSchemaException (RefResolutionException) from Evaluate under the SSRF no-op fetcher — caught and turned into a business false so the host never crashes (D-06 + T-27-01), rather than evaluating silently-closed as the plan text assumed."
  - "Seam reached via internal BaseProcessor.ExecuteAsync forwarder; ProcessAsync stays protected abstract (locked decision, BPC-02)."
  - "ExecutionDataTtlSeconds lives on the existing ProcessorLivenessOptions (default 300), distinct from liveness Ttl (CONFIG-02/D-17)."

patterns-established:
  - "PORT-not-share for the Json.Schema SSRF validator (firewall intact, no new shared lib this milestone)"
  - "Guard schema.Evaluate (not just FromText/Parse) so $ref-resolution faults are business failures, never host crashes"

requirements-completed: [EXEC-03, EXEC-04, EXEC-05, CONFIG-02]

# Metrics
duration: 9min
completed: 2026-06-01
---

# Phase 27 Plan 01: Execution Round-Trip Foundation Types Summary

**Ported the SSRF-locked Json.Schema validator into BaseProcessor.Core (TryValidate: skip/invalid/valid, $ref lockdown holds without crashing), firmed ProcessResult to (string OutputData), added the internal BaseProcessor.ExecuteAsync invoker seam, and added the CONFIG-02 ExecutionDataTtl knob — the contracts Plan 02's consumer is built against.**

## Performance

- **Duration:** ~9 min
- **Started:** 2026-06-01T20:45:24Z
- **Completed:** 2026-06-01T20:54:44Z
- **Tasks:** 2
- **Files modified:** 8 (2 created, 6 modified)

## Accomplishments
- Ported `ProcessorJsonSchemaValidator` (firewalled — no BaseApi.Service reference) with the SSRF static ctor (`Dialect.Draft202012` + global no-op `$ref` fetch) and the flat-error flattening, returning a `bool` instead of throwing.
- Proved the SSRF lockdown with 7 plain-xUnit facts, including an external `http://` `$ref` that evaluates closed (no outbound fetch, no host crash).
- Firmed `ProcessResult` to `(string OutputData)` and added the `internal BaseProcessor.ExecuteAsync` forwarder so the same-assembly consumer can invoke the `protected abstract ProcessAsync` without widening it.
- Added `ExecutionDataTtlSeconds` (key `ExecutionDataTtl`, default 300) and extended the options-binding facts (bind + default).

## Task Commits

1. **Task 1: JsonSchema.Net ref + ported SSRF validator + Wave-0 tests** - `7ca6605` (feat)
2. **Task 2: ProcessResult(OutputData) + BaseProcessor invoker seam + CONFIG-02 TTL + tests** - `ba2bf4a` (feat)

**Plan metadata:** (final docs commit — this SUMMARY + STATE + ROADMAP)

## Files Created/Modified
- `src/BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs` - Ported SSRF-locked validator; `TryValidate(definition, data, out errors)`.
- `tests/BaseApi.Tests/Processor/ProcessorJsonSchemaValidatorFacts.cs` - 7 facts: null/whitespace skip, unparseable def/data invalid, valid/invalid data, external-`$ref` SSRF proof.
- `src/BaseProcessor.Core/BaseProcessor.Core.csproj` - Added `<PackageReference Include="JsonSchema.Net" />` (no `Version=`, CPM).
- `src/BaseProcessor.Core/Processing/ProcessResult.cs` - `public sealed record ProcessResult(string OutputData)`.
- `src/BaseProcessor.Core/Processing/BaseProcessor.cs` - Added `internal Task<IReadOnlyList<ProcessResult>> ExecuteAsync(...)` forwarding to `ProcessAsync`.
- `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` - Added `ExecutionDataTtlSeconds` + updated class header (four→five knobs).
- `tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs` - Renamed to five-knobs; assert `ExecutionDataTtl` bind (120) + default (300).
- `tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs` - Test double updated to `new ProcessResult("output")`.

## Decisions Made
- **SSRF `$ref` behaves as a throw, not a silent-closed evaluate.** Under JsonSchema.Net 9.2.1 with the global no-op fetcher, an unresolvable external `$ref` raises `RefResolutionException` (a `JsonSchemaException`) inside `schema.Evaluate` — it does NOT reach the network and does NOT evaluate to a plain `false`. The validator now guards `Evaluate` and converts that to a business `false` with an error message, satisfying both the SSRF lockdown (T-27-01) and D-06 ("never a host crash"). This is the correct, verified behavior; the plan text's "evaluates closed → returns a bool" framing held in spirit (no fetch, returns deterministically) but required the extra guard.
- Kept `ProcessAsync` `protected abstract`; the consumer reaches it via the `internal ExecuteAsync` forwarder (locked seam decision).
- `ExecutionDataTtlSeconds` on the existing `ProcessorLivenessOptions`, default 300.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Guard `schema.Evaluate` against unresolved-`$ref` so the SSRF case does not crash the host**
- **Found during:** Task 1 (validator + tests)
- **Issue:** The plan's `TryValidate` only guarded `FromText` and `JsonDocument.Parse`. The `External_Ref_Evaluates_Closed_Ssrf` fact failed: with the SSRF no-op fetcher, JsonSchema.Net 9.2.1 throws `Json.Schema.RefResolutionException` from `schema.Evaluate` ("Could not resolve 'http://example.com/schema.json'") — an unguarded throw that would crash the consumer, violating D-06 and the T-27-01 mitigation ("port MUST preserve … never crash").
- **Fix:** Wrapped the `Evaluate` call in `try/catch (JsonSchemaException)` → returns `false` with an "unresolved $ref" error message (no outbound fetch occurs — lockdown intact). Tightened the SSRF fact to assert `false` + non-empty errors (deterministic, was previously `ok || !ok`).
- **Files modified:** `src/BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs`, `tests/BaseApi.Tests/Processor/ProcessorJsonSchemaValidatorFacts.cs`
- **Verification:** `ProcessorJsonSchemaValidatorFacts` 7/7 pass (incl. the SSRF case).
- **Committed in:** `7ca6605` (Task 1 commit)

**2. [Rule 3 - Blocking] Update `BaseProcessorSeamFacts` test double for the new `ProcessResult(string)` shape**
- **Found during:** Task 2 (firming ProcessResult)
- **Issue:** `BaseProcessorSeamFacts.TestProcessor.Result = new();` no longer compiles once `ProcessResult` requires a positional `OutputData` argument — would break the pre-existing seam test compile (the plan's own acceptance criterion requires it to "still pass").
- **Fix:** Changed to `new ProcessResult("output")`.
- **Files modified:** `tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs`
- **Verification:** `BaseProcessorSeamFacts` passes against `ProcessResult(string)`.
- **Committed in:** `ba2bf4a` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (1 bug, 1 blocking).
**Impact on plan:** Both necessary for correctness — the SSRF guard is the load-bearing T-27-01 mitigation; the test-double fix keeps the pre-existing seam test green. No scope creep.

## Issues Encountered
- The MTP/xUnit v3 runner ignores the VSTest-style `--filter "FullyQualifiedName~..."` (it runs the whole suite). Scoped runs use the MTP filter `-- --filter-class <FQN>` / `-- --filter-namespace BaseApi.Tests.Processor` instead.
- The full-suite run surfaced one pre-existing, out-of-scope failure: `Orchestrator.ResultConsumeTests.CompletedResult_DispatchesMatchingNextStep_WithCorrectFieldCopy` — a broker-dependent harness test (RabbitMQ "Connection Failed" / "bus … Not Started"). It touches none of the 27-01 files and is logged in `.planning/phases/27-execution-round-trip/deferred-items.md`. The full 22-test Processor slice is GREEN.

## Known Stubs
None — all delivered types are fully implemented. `ProcessResult.OutputData` is consumed by Plan 02's `EntryStepDispatchConsumer` (the next plan), as designed by the interface-first ordering.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Plan 02 (`EntryStepDispatchConsumer`) has its contracts: `ProcessorJsonSchemaValidator.TryValidate`, `ProcessResult(string OutputData)`, `BaseProcessor.ExecuteAsync`, and `ProcessorLivenessOptions.ExecutionDataTtlSeconds`.
- No blockers. The pre-existing broker-dependent `ResultConsumeTests` failure is unrelated and tracked separately.

---
*Phase: 27-execution-round-trip*
*Completed: 2026-06-01*

## Self-Check: PASSED

All 5 listed source files exist on disk; both task commits (`7ca6605`, `ba2bf4a`) exist in git history.
