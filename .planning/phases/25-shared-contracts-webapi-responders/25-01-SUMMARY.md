---
phase: 25-shared-contracts-webapi-responders
plan: 01
subsystem: api
tags: [messaging-contracts, redis-l2-keys, request-response, stj, masstransit-leaf, processor-projection]

# Dependency graph
requires:
  - phase: 21-v3.4.0-closeout-hygiene
    provides: shared L2ProjectionKeys (Root/Step/Processor/ParentIndex builders + const Prefix)
  - phase: 17-messaging-contracts
    provides: Messaging.Contracts leaf library (plain-POCO, no MassTransit), OrchestratorQueues SoT template, LivenessProjection
provides:
  - public ProcessorProjection record relocated into Messaging.Contracts.Projections (single shared type for WebApi + Phase 26 processor)
  - L2ProjectionKeys.ExecutionData(entryId) builder => skp:data:{entryId:D} (first key with a data: discriminator)
  - LivenessStatus.Healthy = "Healthy" shared const (Path-1 liveness SoT)
  - GetProcessorBySourceHash/ProcessorIdentityFound/ProcessorIdentityNotFound records (RPC-01 contract half)
  - GetSchemaDefinition/SchemaDefinitionFound/SchemaDefinitionNotFound records (RPC-02 contract half)
  - ProcessorQueues.IdentityQuery/SchemaQuery queue-name constants (RPC-03 contract half)
affects: [26-baseprocessor-core, 27-execution-round-trip, plan 25-02 webapi-responders]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Plain leaf request/response record pairs (dual found/not-found responses) for bus RPC, no [JsonPropertyName], no MassTransit usings"
    - "data: discriminator segment in an L2 key (skp:data:{guid}) — first key namespace distinct from the flat Root/Processor scheme"

key-files:
  created:
    - src/Messaging.Contracts/Projections/ProcessorProjection.cs
    - src/Messaging.Contracts/Projections/LivenessStatus.cs
    - src/Messaging.Contracts/ProcessorQueries.cs
    - src/Messaging.Contracts/ProcessorQueues.cs
    - tests/BaseApi.Tests/Projection/LivenessStatusTests.cs
  modified:
    - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
    - tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs (DELETED — relocated to leaf)

key-decisions:
  - "ProcessorProjection moved verbatim (only namespace + internal->public changed); [property:] JsonPropertyName targets preserved byte-identical so STJ field mapping is unchanged"
  - "ProcessorLivenessValidator needed NO edit — it already imported Messaging.Contracts.Projections (for LivenessProjection); its BaseApi.Service.Features.Orchestration.Projection using stays because RedisProjectionKeys still lives there"
  - "Leaf stays MassTransit-free: records are plain POCOs (default STJ), no PackageReference added to Messaging.Contracts.csproj"
  - "ProcessorQueues uses bare short-names (no queue:/exchange: scheme prefix) — the Phase 26 sender prepends exchange:"

patterns-established:
  - "Dual request/response record contract (Get* request -> *Found/*NotFound responses) for clean client pattern-match"
  - "L2 key data: discriminator namespace via ExecutionData builder"

requirements-completed: [CONTRACT-01, CONTRACT-02, CONTRACT-03, RPC-01, RPC-02, RPC-03]

# Metrics
duration: ~25min
completed: 2026-06-01
---

# Phase 25 Plan 01: Shared Contracts (Leaf Vocabulary) Summary

**Landed the leaf shared-contract vocabulary in Messaging.Contracts — public ProcessorProjection relocation, the skp:data: ExecutionData L2 key builder, the "Healthy" liveness const, and six request/response records + two queue-name constants — all plain-POCO with the leaf staying MassTransit-free.**

## Performance

- **Duration:** ~25 min (wall clock 11 min; two full 3.5-min real-stack test passes ran in the background)
- **Started:** 2026-06-01T17:00:24Z
- **Completed:** 2026-06-01T17:32Z
- **Tasks:** 3
- **Files modified:** 8 (5 created, 2 modified, 1 deleted/relocated)

## Accomplishments
- `ProcessorProjection` is now a single public record in `Messaging.Contracts.Projections` — the old internal `BaseApi.Service` type is deleted; writer (Phase 26) and reader (WebApi) can no longer desync on `inputDefinition`/`outputDefinition`/`liveness` field names. Round-trip pin GREEN against the moved type.
- `L2ProjectionKeys.ExecutionData(entryId)` => `skp:data:{entryId:D}` — first key with a `data:` discriminator, golden + Root/Processor distinctness pins GREEN.
- `LivenessStatus.Healthy = "Healthy"` shared const, pin GREEN.
- Six request/response records (RPC-01/02) + `ProcessorQueues.IdentityQuery`/`SchemaQuery` constants (RPC-03) compile in the leaf; no MassTransit dependency introduced.

## Task Commits

Each task was committed atomically (TDD tasks show test -> feat):

1. **Task 1 (TDD RED): ExecutionData + LivenessStatus pins** - `205f13d` (test)
2. **Task 1 (TDD GREEN): ExecutionData builder + LivenessStatus const** - `c188e2c` (feat)
3. **Task 2: request/response records + queue constants** - `8759b13` (feat)
4. **Task 3: relocate ProcessorProjection to leaf as public** - `34224e5` (feat — git-detected rename, 66% similarity)

_Task 3's round-trip behavior gate (`ProjectionRecordRoundTripTests` ProcessorProjection facts) pre-existed and stayed GREEN against the moved public type — no new RED commit was needed for it._

## Files Created/Modified
- `src/Messaging.Contracts/Projections/ProcessorProjection.cs` - relocated public record (verbatim lift; namespace + internal->public only)
- `src/Messaging.Contracts/Projections/LivenessStatus.cs` - `Healthy` const SoT
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` - added `ExecutionData` builder + XML-doc bullet
- `src/Messaging.Contracts/ProcessorQueries.cs` - six plain request/response records
- `src/Messaging.Contracts/ProcessorQueues.cs` - `IdentityQuery`/`SchemaQuery` constants
- `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs` - ExecutionData golden + distinctness facts
- `tests/BaseApi.Tests/Projection/LivenessStatusTests.cs` - `Healthy` pin
- `src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs` - DELETED (relocated)

## Decisions Made
- **ProcessorLivenessValidator required no edit.** The plan flagged a possible using-line swap, but the file already imported `Messaging.Contracts.Projections` (for `LivenessProjection`) AND keeps `BaseApi.Service.Features.Orchestration.Projection` for `RedisProjectionKeys` (line 33). `ProcessorProjection` simply re-resolves from the leaf namespace — both usings remain in use, no unused-using warning.
- **All 13 test reference sites compiled unchanged.** Every file already imported `Messaging.Contracts.Projections` (because they construct `LivenessProjection`), so `ProcessorProjection` re-resolved there automatically. The full `SK_P.sln -c Debug` build produced zero errors and zero warnings (warnings are build-errors here via `TreatWarningsAsErrors`), proving no `.Projection` using became unused. No test-file edits were necessary.
- Verbatim record lift preserves the `[property: JsonPropertyName]` targets byte-identical (mitigates T-25-01-01).

## Deviations from Plan

None - plan executed exactly as written. (The plan anticipated a using-line swap in the validator and edits across 13 test files; the actual blast radius was zero source edits beyond the move because every reference site already imported the leaf projection namespace. This is narrower than the plan's worst-case, not a deviation from intent.)

## Issues Encountered
None. The Task 3 commit registered as a git rename (66% similarity) capturing both the delete and the new file; verified via `git diff --diff-filter=D` that no unexpected files were deleted.

## Threat Surface
No new external surface. T-25-01-01 (STJ field-mapping tampering on the move) mitigated by the verbatim lift + the pre-existing round-trip pin asserting `inputDefinition`/`outputDefinition`/`liveness` preserved. No new HTTP or bus endpoints (responder host lands in Plan 02).

## Known Stubs
None. All five types are concrete passive contract definitions with no placeholder data, empty returns, or unwired data sources.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Plan 02 (WebApi responders) is unblocked: the six request/response records + `ProcessorQueues` constants are the contract half it binds its `ReceiveEndpoint`s and request clients against.
- Phase 26 (BaseProcessor.Core) can consume the public `ProcessorProjection`, `ExecutionData` key, and `LivenessStatus.Healthy` directly.

## Self-Check: PASSED

- All 5 created files present on disk; relocated `BaseApi.Service` ProcessorProjection confirmed deleted.
- All 4 task commits present in git history (`205f13d`, `c188e2c`, `8759b13`, `34224e5`).

---
*Phase: 25-shared-contracts-webapi-responders*
*Completed: 2026-06-01*
