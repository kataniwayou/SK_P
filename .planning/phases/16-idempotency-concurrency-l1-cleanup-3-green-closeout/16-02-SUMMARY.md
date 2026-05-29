---
phase: 16-idempotency-concurrency-l1-cleanup-3-green-closeout
plan: 02
subsystem: orchestration-tests
tags: [redis, l2-projection, integration-test, full-http, e2e]
requires:
  - "POST /api/v1/orchestration/start (OrchestrationService Start loop, Phase 15)"
  - "RedisProjectionWriter.UpsertAsync 3-keyspace projection (Phase 15)"
  - "Phase8WebAppFactory.RedisMultiplexer / RedisKeyPrefix accessors (Phase 12/16)"
  - "Locked projection records (WorkflowRootProjection/StepProjection/ProcessorProjection)"
provides:
  - "TEST-REDIS-06 full-HTTP happy-path 3-keyspace round-trip fact (HappyPathE2EFacts)"
  - "Positive SCAN control for plan 03 (the same keys ARE findable in D-form)"
affects:
  - "tests/BaseApi.Tests/Features/Orchestration/"
tech-stack:
  added: []
  patterns:
    - "Full-HTTP Start (SendAsync + X-Correlation-Id header) then read L2 back via RedisMultiplexer"
    - "System.Text.Json round-trip deserialization against internal projection records (InternalsVisibleTo)"
    - "Default 'D' GUID-form Redis keys ($\"{prefix}{wfId}\") — never :N"
key-files:
  created:
    - "tests/BaseApi.Tests/Features/Orchestration/HappyPathE2EFacts.cs"
  modified: []
decisions:
  - "Reused the RICH seed chain (Schema→Processor(in/out)→Step→Workflow) from RedisProjectionWriterFacts so the processor key's InputDefinition/OutputDefinition are non-null; no Assignment needed (workflow AssignmentIds null) since the fact does not assert step payload."
  - "Drove the full HTTP Start path (HttpRequestMessage + X-Correlation-Id) rather than calling UpsertAsync in isolation — exercises the per-workflow Start loop and correlationId plumbing end-to-end (distinct from RedisProjectionWriterFacts)."
  - "EntryCondition asserted via enum compare (StepEntryCondition.Always, int-backed ==4), never a string, matching the no-string-enum-converter shape."
metrics:
  duration: ~4min
  completed: 2026-05-29
---

# Phase 16 Plan 02: HappyPathE2EFacts — full-HTTP Start + 3-keyspace round-trip Summary

TEST-REDIS-06: a new dedicated full-HTTP fact that POSTs to `/api/v1/orchestration/start` with an `X-Correlation-Id` header, asserts 204, then round-trips all three L2 keyspaces (root / per-step / per-processor) through `System.Text.Json` against the locked projection records — proving the happy path produces a structurally complete, deserializable 3-keyspace projection through the real public API.

## What Was Built

- **`tests/BaseApi.Tests/Features/Orchestration/HappyPathE2EFacts.cs`** (1 fact, `Start_HappyPath_WritesAllThreeKeyspaces`):
  - Seeds a valid graph via the public entity HTTP API: two Schemas → a Processor with both Input/Output schemas → a terminal Step (`NextStepIds: null`) → a Workflow with that step as entry.
  - POSTs `[wfId]` to `/api/v1/orchestration/start` via `HttpRequestMessage` carrying `X-Correlation-Id`; asserts `204 No Content`.
  - Reads all three L2 keyspaces back from `_factory.RedisMultiplexer.GetDatabase()` using default "D"-form keys (`$"{prefix}{wfId}"`, `$"{prefix}{wfId}:{stepId}"`, `$"{prefix}{procId}"`):
    - **Root** → `WorkflowRootProjection`: `EntryStepIds` contains the seeded step; `JobId != Guid.Empty`; `CorrelationId == correlationId` sent; `Liveness.Status == "Pending"`.
    - **Step** → `StepProjection`: `ProcessorId == procId`; `NextStepIds` empty (terminal → `[]`, not null); `EntryCondition == StepEntryCondition.Always` (enum compare).
    - **Processor** → `ProcessorProjection`: `InputDefinition`/`OutputDefinition` non-null (schemas seeded); `Liveness.Status == "Pending"`.

## Verification

- `dotnet test --configuration Release --filter "FullyQualifiedName~HappyPathE2EFacts"` → the MTP runner ran the full suite (VSTest filter ignored under Microsoft.Testing.Platform, warning MTP0001) and reported **Passed: 228, Failed: 0** in 3m13s against the live compose stack (real Postgres + real Redis). The new fact is included in that GREEN total (227 prior + 1 new).
- Acceptance-criteria grep guards all pass:
  - All three `JsonSerializer.Deserialize<...Projection>` present (lines 137 / 147 / 156).
  - GUID-FORMAT GUARD: zero `:N` matches in key-building expressions (the `{Guid.NewGuid():N}` literals are header/name values, not Redis keys).
  - `Assert.Equal(correlationId, root.CorrelationId)` present (correlationId round-trips).
  - `StepEntryCondition.Always` enum compare present (no `"Always"` string assertion).
- `git status` shows exactly one new file added by this plan; no existing `src/` or `tests/` file modified. (Pre-existing unrelated planning-file moves/deletions in the working tree predate this plan and were left untouched.)

## Deviations from Plan

None — plan executed exactly as written. The single fact described in `<behavior>` was implemented verbatim; the assertion on workflow `Cron` was not added (the fact seeds no cron and the `<behavior>` block did not require it).

## TDD Gate Compliance

This plan adds a NEW integration fact against ALREADY-SHIPPED production behavior (Phase 15 Start loop + projection writer). There is no new production code to drive — the RED→GREEN cycle has no production delta. The fact passed GREEN on its first run because it exercises existing, already-correct behavior. Committed as a single `test(16-02)` commit (no separate `feat` follows because no production change was needed).

## Self-Check: PASSED

- FOUND: `tests/BaseApi.Tests/Features/Orchestration/HappyPathE2EFacts.cs`
- FOUND: commit `b3f6276`
