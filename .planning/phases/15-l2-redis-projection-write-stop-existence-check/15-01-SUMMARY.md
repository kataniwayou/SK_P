---
phase: 15-l2-redis-projection-write-stop-existence-check
plan: 01
subsystem: orchestration-l2-projection
tags: [redis, projection, serialization, system-text-json, config]
requires:
  - RedisProjectionOptions (Phase 12 — KeyPrefix/Serialization)
  - StepEntryCondition enum (Phase 8)
  - InternalsVisibleTo("BaseApi.Tests") on BaseApi.Service (Phase 9)
provides:
  - RedisProjectionKeys (single source of truth for 3 flat L2 key formats — L2-PROJECT-02)
  - WorkflowRootProjection / StepProjection / ProcessorProjection / LivenessProjection (camelCase-pinned record DTOs — L2-PROJECT-03/04/05/06)
  - RedisProjectionOptions.ProcessorKeyTtlDays (TTL config knob — D-08)
affects:
  - Downstream Phase 15 writer/cleanup plans (consume key formats + record shapes)
  - Phase 16 (deserialize-asserts the locked camelCase shapes)
tech-stack:
  added: []
  patterns:
    - "[property: JsonPropertyName] on positional-record members (Pitfall 1 guard)"
    - "Flat single-prefix L2 key scheme with no type discriminator (D-02)"
    - "Comment-rephrase to satisfy negative-grep acceptance criteria (precedent: 06-01/08-01/08-02)"
key-files:
  created:
    - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/LivenessProjection.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/WorkflowRootProjection.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/StepProjection.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs
    - tests/BaseApi.Tests/Features/Orchestration/Projection/RedisProjectionKeysTests.cs
    - tests/BaseApi.Tests/Features/Orchestration/Projection/ProjectionRecordRoundTripTests.cs
  modified:
    - src/BaseApi.Core/Configuration/RedisProjectionOptions.cs
    - src/BaseApi.Service/appsettings.json
    - src/BaseApi.Service/appsettings.Development.json
decisions:
  - "Reused existing InternalsVisibleTo for internal RedisProjectionKeys access from tests (no new IVT needed)"
  - "Rephrased StepProjection doc-comment to drop literal 'JsonStringEnumConverter' token, satisfying the T-15-03 negative-grep acceptance criterion while preserving educational intent"
  - "Round-trip tests assert scalar pins + sequence equality (List<Guid> contents) rather than whole-record Equals, since record equality on a List member is reference-based"
metrics:
  duration: ~10min
  completed: 2026-05-29
  tasks: 3
  files: 10
---

# Phase 15 Plan 01: L2 Projection Foundation (Keys + Records + TTL Knob) Summary

RedisProjectionKeys flat key formatter (L2-PROJECT-02) plus four `[property: JsonPropertyName]`-pinned projection record DTOs (L2-PROJECT-03/04/05/06) and the `ProcessorKeyTtlDays` config knob (D-08), with a Wave-0 RED→GREEN unit harness proving key formats and default-STJ camelCase round-trips.

## What Was Built

- **`RedisProjectionKeys`** — `internal static` formatter; single source of truth for the three flat L2 key formats (`{prefix}{wf}`, `{prefix}{wf}:{step}`, `{prefix}{proc}`). Flat single-prefix scheme with NO discriminator — Root and Processor are byte-identical for the same prefix+GUID (D-02), disambiguated only by GUID namespace. Hyphenated ("D") GUID rendering.
- **Four projection record DTOs** — `LivenessProjection`, `WorkflowRootProjection`, `StepProjection`, `ProcessorProjection`, all `internal sealed record` with `[property: JsonPropertyName]` on every positional member (Pitfall 1 guard). `entryCondition` serializes as int (no string-enum converter); processor fields are exactly `inputDefinition`/`outputDefinition`.
- **`RedisProjectionOptions.ProcessorKeyTtlDays`** — `int`, default 100, bound automatically via the existing `cfg.GetSection("Redis")` bind. Added to both `appsettings.json` (full Redis section) and `appsettings.Development.json` (new Redis section, TTL-only — Pitfall A: that file had no Redis section).
- **Two unit-test classes** (`[Trait("Phase","15")]`): `RedisProjectionKeysTests` (5 facts) and `ProjectionRecordRoundTripTests` (8 facts) — assert literal key strings, camelCase keys, `entryCondition:4`, `cron:null`, `nextStepIds:[]`, `inputDefinition:null`, and value round-trips under default `JsonSerializerOptions`.

## Tasks Completed

| Task | Name | Commits | Files |
| ---- | ---- | ------- | ----- |
| 1 | RedisProjectionKeys + key-format tests (TDD) | da1d5bd (RED test), 83c31a9 (GREEN) | RedisProjectionKeys.cs, RedisProjectionKeysTests.cs |
| 2 | 4 projection record DTOs + STJ round-trip tests (TDD) | 1626cdb (RED test), 5ab4cc6 (GREEN) | 4 record files, ProjectionRecordRoundTripTests.cs |
| 3 | ProcessorKeyTtlDays + both appsettings | 5e476c6 | RedisProjectionOptions.cs, appsettings.json, appsettings.Development.json |

## TDD Gate Compliance

Both TDD tasks followed RED→GREEN. Task 1: RED commit da1d5bd (compile failure — `RedisProjectionKeys` undefined), GREEN commit 83c31a9. Task 2: RED commit 1626cdb (compile failure — record types undefined), GREEN commit for the 4 records. No REFACTOR commits needed.

## Verification Results

- `dotnet build src/BaseApi.Service -c Release` → succeeded, 0 warnings, 0 errors.
- `dotnet build src/BaseApi.Core -c Release` → succeeded, 0 warnings, 0 errors.
- `dotnet test tests/BaseApi.Tests` (filter ignored by MTP; full suite ran) → Passed: 207, Failed: 0, Skipped: 0. The 13 new Phase-15 facts (5 key + 8 round-trip) are GREEN; prior 194 facts unchanged.
- `rg "JsonStringEnumConverter|Mapperly|ToRead" src/.../Projection/` → 0 matches.
- `[property: JsonPropertyName(` present in all 4 record files (15 total occurrences).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Plan-internal consistency] Rephrased doc-comment to satisfy negative-grep**
- **Found during:** Task 2 verification.
- **Issue:** The acceptance criterion / T-15-03 mitigation requires `rg "JsonStringEnumConverter" src/.../Projection/` to return 0 matches, but my `StepProjection.cs` doc-comment contained the literal token "no `JsonStringEnumConverter` anywhere".
- **Fix:** Rephrased to "no string-enum converter is registered anywhere" — preserves the educational intent and the L2-PROJECT-04 reference while satisfying grep-empty. Follows the established precedent (Plans 06-01, 08-01, 08-02 rephrased comments for the same reason).
- **Files modified:** StepProjection.cs (doc-comment only; no behavior change).
- **Commit:** folded into the Task 2 GREEN production commit (records test already GREEN regardless).

## Notes

- The `--filter` flag passed to `dotnet test` is ignored under Microsoft.Testing.Platform (MTP0001 informational warning), so the full 207-fact suite runs each time; all pass, so the per-filter acceptance is satisfied transitively.
- No new infrastructure or NuGet packages; reused Phase-12 `RedisProjectionOptions` and the Phase-9 `InternalsVisibleTo`.

## Self-Check: PASSED

All 7 created files exist on disk; all 3 modified files contain the expected edits; all task commits (da1d5bd, 83c31a9, 1626cdb, 5ab4cc6, 5e476c6) exist in git log.
