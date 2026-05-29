---
phase: 15-l2-redis-projection-write-stop-existence-check
plan: 02
subsystem: orchestration-l2-projection
tags: [redis, projection, batch-write, ttl, system-text-json, integration-test]
requires:
  - RedisProjectionKeys + 4 projection record DTOs (Plan 15-01)
  - RedisProjectionOptions.ProcessorKeyTtlDays (Plan 15-01 — D-08 knob)
  - IConnectionMultiplexer Singleton + RedisProjectionOptions bind (Phase 12)
  - WorkflowGraphSnapshot + IWorkflowGraphLoader (Phase 13)
  - Phase8WebAppFactory.RedisMultiplexer / RedisKeyPrefix + RedisFixture SCAN+DEL teardown (Phase 12)
provides:
  - "IRedisProjectionWriter.UpsertAsync(snapshot, correlationId, ct) — widened signature with explicit correlationId (D-01)"
  - "RedisProjectionWriter — filled 3-keyspace CreateBatch write engine (L2-PROJECT-01)"
  - "Processor-only TTL via ProcessorKeyTtlDays; root/step keys no TTL (D-08)"
affects:
  - "Plan 15-04 Start loop (calls UpsertAsync per workflow; will supply the real correlationId)"
  - "Phase 16 (deserialize-asserts the L2 keyspace shapes against real Redis)"
tech-stack:
  added: []
  patterns:
    - "IBatch CreateBatch() + Task.WhenAll fan-out for atomic-ish multi-key SET"
    - "when: When.Always to disambiguate SE.Redis 2.13.1 expiry overload from the Expiration/ValueCondition overload"
    - "FirstOrDefault(...)?.X ?? default for not-guaranteed 1:1 graph relations (step↔assignment)"
    - "Comment-rephrase to satisfy negative-grep acceptance criteria (precedent: 01/06/08 plans)"
    - "Loader-built per-workflow snapshot as the integration-test seam (resolve internal IWorkflowGraphLoader from DI scope)"
key-files:
  created:
    - tests/BaseApi.Tests/Features/Orchestration/RedisProjectionWriterFacts.cs
  modified:
    - src/BaseApi.Service/Features/Orchestration/Projection/IRedisProjectionWriter.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
    - tests/BaseApi.Tests/Features/Orchestration/StartCleanupFacts.cs
decisions:
  - "Step payload uses FirstOrDefault → empty-string fallback because a step is NOT guaranteed an assignment (ENTITY-08); the plan's verbatim .First() crashed valid no-assignment workflows (Rule 1)"
  - "OrchestrationService.StartAsync passes string.Empty correlationId as an interim until Plan 04 wires X-Correlation-Id resolution (Rule 3 unblock)"
  - "when: When.Always added ONLY to the processor StringSetAsync to keep expiry: exactly once while disambiguating the SE.Redis 2.13.1 overload (Rule 1)"
  - "Integration tests drive the real IWorkflowGraphLoader to build the per-workflow snapshot (mirrors WorkflowGraphLoaderFacts) rather than hand-constructing one — exercises real enrichment + payload re-serialization"
metrics:
  duration: ~18min
  completed: 2026-05-29
  tasks: 3
  files: 5
---

# Phase 15 Plan 02: L2 Redis Projection Writer (3-Keyspace Batch Write) Summary

Filled the no-op `RedisProjectionWriter.UpsertAsync` into the L1→L2 write engine: it projects a single-workflow `WorkflowGraphSnapshot` into the three flat Redis keyspaces (root, per-step, per-processor) in one `CreateBatch()` pipeline (L2-PROJECT-01), with a per-processor `ProcessorKeyTtlDays` TTL (D-08), `TimeProvider`-sourced liveness (D-05), and D-03 partial-failure semantics (one MEL warning naming the workflowId, then rethrow). The interface signature was widened to take the explicit `correlationId` param (D-01), keeping the writer HTTP-agnostic.

## What Was Built

- **`IRedisProjectionWriter.UpsertAsync`** — widened to `(WorkflowGraphSnapshot snapshot, string correlationId, CancellationToken ct)` (D-01) with an XML doc stating the writer stays HTTP-agnostic; correlationId is resolved once by `OrchestrationService`.
- **`RedisProjectionWriter`** — ctor injects `IConnectionMultiplexer` + `IOptions<RedisProjectionOptions>` + `TimeProvider` + `ILogger` (all `?? throw ArgumentNullException` guards, OrchestrationService idiom). `UpsertAsync`:
  - builds one shared `LivenessProjection(now, 0, "Pending")` (timestamp from `_clock.GetUtcNow().UtcDateTime`);
  - assembles `WorkflowRootProjection` (EntryStepIds ?? [], Cron, fresh `Guid.NewGuid()` jobId, liveness, correlationId), serialized with default STJ — camelCase pins hold via the Plan 01 `[JsonPropertyName]` records (no source-generated mapper, L2-PROJECT-06);
  - `db.GetDatabase().CreateBatch()` → fan-out `StringSetAsync` for root (no TTL), every step (no TTL), every processor (TTL only here, `when: When.Always`), then `batch.Execute()` + `await Task.WhenAll(tasks)`;
  - `TimeSpan? ttl = days <= 0 ? null : TimeSpan.FromDays(days)` (Pitfall 2; `<=0` disables expiry);
  - partial-failure `try/catch` emits one structured `LogWarning("...{WorkflowId}...")` then rethrows (D-03).
- **`RedisProjectionWriterFacts`** (3 integration facts, `[Trait("Phase","15")]`, `IClassFixture<Phase8WebAppFactory>`, all pass `TestContext.Current.CancellationToken`):
  - `Upsert_Writes_Three_Keyspaces` — seeds a workflow via HTTP (processor w/ input+output schemas → step → assignment → workflow w/ cron), builds the snapshot via the real loader, calls `UpsertAsync`, then reads back + deserializes all 3 keyspaces; asserts root (entryStepIds/cron/jobId/correlationId/liveness), step (entryCondition as int == 4, processorId, payload JSON-value-equality, nextStepIds == []), processor (inputDefinition/outputDefinition non-null + camelCase field names).
  - `ProcessorProjection_Ttl` — `KeyTimeToLiveAsync` positive for the processor key, `null` for root + step keys (D-08 / Pitfall 2).
  - `Upsert_StepWithoutAssignment_ProjectsEmptyPayload` — locks the Rule 1 fix below.

## Tasks Completed

| Task | Name | Commit | Files |
| ---- | ---- | ------ | ----- |
| 1 | Widen IRedisProjectionWriter signature (+correlationId) | 9724eeb | IRedisProjectionWriter.cs |
| 2 | Fill RedisProjectionWriter (3-keyspace batch + TTL + liveness + D-03) | c3fbb48 | RedisProjectionWriter.cs, OrchestrationService.cs |
| 3 | Real-Redis integration facts (+ Rule 1 fix) | d252d34 | RedisProjectionWriterFacts.cs, StartCleanupFacts.cs, RedisProjectionWriter.cs |

## Verification Results

- `dotnet build src/BaseApi.Service -c Release` → succeeded, 0 warnings, 0 errors.
- Full `dotnet test` suite → **Passed: 210, Failed: 0, Skipped: 0** (207 prior + 3 new writer facts). All 29 Orchestration-namespace facts GREEN.
- `RedisProjectionWriterFacts` in isolation (`--filter-class`) → 3/3 GREEN against the live compose Redis (`localhost:6380`).
- `grep "expiry:" RedisProjectionWriter.cs` → exactly 1 (processor only).
- `grep -E "DateTime.UtcNow|Mapperly|JsonStringEnumConverter" RedisProjectionWriter.cs` → 0.
- `grep -E "IServer|\.Keys\(|\bKEYS\b" RedisProjectionWriter.cs` → 0 (L2-PROJECT-07 — writer does not enumerate).
- `grep "string correlationId" IRedisProjectionWriter.cs` → 1.

Note: the runner is Microsoft.Testing.Platform; `dotnet test --filter` is ignored (MTP0001), so the full 210-fact suite runs each time. Isolated per-class runs use the test executable's native `--filter-class` flag. An interim full-suite run showed 4 transient observability failures (collector telemetry-batch timing, documented in Plan 06-02 SUMMARY); they settled to 0 once the collector warmed up — unrelated to this plan's code.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Step payload `.First()` crashes on a valid no-assignment workflow**
- **Found during:** Task 3 (full Orchestration-namespace run — `Start_Returns204`, `DiamondDag_Passes`, `SchemaEdgeNullSide_Passes` all returned 500 instead of their expected 204/422).
- **Issue:** The plan's verbatim field-source line `payload = snapshot.Assignments.Values.First(a => a.StepId == step.Id).Payload` throws `InvalidOperationException: Sequence contains no matching element` when a step has no bound Assignment. A Workflow may carry steps with `AssignmentIds == null` (ENTITY-08), so this is a valid shape. Previously masked because the writer was a no-op; filling it surfaced the crash on every Start that projects an unbound step.
- **Fix:** `FirstOrDefault(a => a.StepId == step.Id)?.Payload ?? string.Empty` — an unbound step projects an empty payload string (`StepProjection.Payload` is a non-nullable string member). Added `Upsert_StepWithoutAssignment_ProjectsEmptyPayload` to lock the behavior.
- **Files modified:** RedisProjectionWriter.cs, RedisProjectionWriterFacts.cs.
- **Commit:** d252d34.

**2. [Rule 1 - Library API mismatch] SE.Redis 2.13.1 `StringSetAsync` overload ambiguity**
- **Found during:** Task 2 build.
- **Issue:** `StringSetAsync(key, value, expiry: ttl)` failed with CS1503 (`cannot convert TimeSpan? to Expiration`) — SE.Redis 2.13.1 added an `Expiration`/`ValueCondition` overload the named `expiry:` arg bound toward.
- **Fix:** Added `when: When.Always` to the processor `StringSetAsync` only (the `Expiration` overload uses `ValueCondition`, not `When`, so this uniquely selects the classic `TimeSpan?` overload). Kept `expiry:` appearing exactly once per the acceptance criterion.
- **Files modified:** RedisProjectionWriter.cs.
- **Commit:** c3fbb48.

**3. [Rule 3 - Blocking] Signature widening broke the StartAsync call site + a test stub**
- **Found during:** Task 2 build (OrchestrationService) and Task 3 build (StartCleanupFacts).
- **Issue:** Widening the interface broke `OrchestrationService.StartAsync`'s 2-arg call (CS7036) and the `StartCleanupFacts.ThrowingRedisProjectionWriter` stub (CS0535).
- **Fix:** `OrchestrationService.StartAsync` now passes `string.Empty` correlationId with a comment that Plan 04 wires the real `X-Correlation-Id` (the writer's own facts exercise it with a real value). Updated the test stub to the widened signature.
- **Files modified:** OrchestrationService.cs, StartCleanupFacts.cs.
- **Commits:** c3fbb48 (OrchestrationService), d252d34 (StartCleanupFacts).

**4. [Rule 3 - Plan-internal consistency] Rephrased doc-comment to satisfy negative-grep**
- **Found during:** Task 2 verification.
- **Issue:** The acceptance criterion requires `rg "...|Mapperly|..." RedisProjectionWriter.cs` → 0, but my XML doc said "NO Mapperly".
- **Fix:** Rephrased to "no source-generated object mapper is used (L2-PROJECT-06)". Same precedent as Plans 01-01 / 06-01 / 08-01.
- **Files modified:** RedisProjectionWriter.cs.
- **Commit:** c3fbb48.

## Notes

- The integration tests drive the real `IWorkflowGraphLoader` to build the per-workflow snapshot (mirroring `WorkflowGraphLoaderFacts`) rather than hand-constructing one — this exercises the real enrichment path and surfaced that `Assignment.Payload` is re-serialized by the create pipeline (`{ "k": "v" }` → `{"k": "v"}`), so the payload assertion compares JSON value-equality (canonicalized), not the raw input literal.
- `ProcessorKeyTtlDays` is the appsettings default (100) in tests — the factory's in-memory config overrides only `Redis:KeyPrefix` + connection strings, so the TTL fact asserts a positive TTL without coupling to the exact day count.
- No new infrastructure or NuGet packages. The `git status` planning-directory churn (mass D/?? on `.planning/phases/*`) is pre-existing and unrelated to this plan; only the 5 plan files above were staged.

## Self-Check: PASSED

- Created file exists: `tests/BaseApi.Tests/Features/Orchestration/RedisProjectionWriterFacts.cs` — FOUND.
- Modified files contain expected edits (widened interface, filled writer, FirstOrDefault, updated stub) — verified via build + greps.
- All task commits exist in git log: 9724eeb, c3fbb48, d252d34 — FOUND.
