# Phase 24 — Deferred / Tracking Items

## SCOPE-BOUNDARY: post-24-02 full-suite "4 failures" — UNDER FINAL VERIFICATION

**Status:** being settled at phase close (NOT a confirmed false alarm — earlier "RESOLVED"
note was based on a misread of a wrapper exit code and has been retracted).

**Timeline of evidence:**
- After 24-02, orchestrator's own from-scratch `dotnet test SK_P.sln -c Debug` exited **1**
  with `Failed: 4, Passed: 307, Total: 311`. (The background-task notification said "exit code 0"
  — that is the wrapper's code, not dotnet's; the captured log shows `EXIT:1`.)
- 24-03 executor characterized the 4 as pre-existing RabbitMQ / DbConcurrency / FluentValidation
  integration flakies outside the Orchestrator namespace.
- 24-05 executor reported a from-scratch rebuild (`rm -rf */obj */bin`) at **309 passed / 0 failed**,
  and separately found a real stale-build trap (AckSemanticsTests compiled against a removed ctor,
  masked by incremental build) — consistent with the project's known stale-build memory.

**Resolution path:** orchestrator runs one authoritative `dotnet clean` + full-suite run with a
TRX report at phase close; if any test fails, the exact failing names are read from the TRX and
classified (24-caused vs pre-existing flaky) before the phase is marked verified. Result recorded
in 24-VERIFICATION.md.

## LOCKED FIX (a) — Stop cleanup-ordering regression [failures #2, #3, #4]

**Decision (operator-confirmed):** Reorder `OrchestrationService.StopAsync` to PROBE root presence
with `KeyExistsAsync` instead of `KeyDeleteAsync`, and let `StopCleanupAsync` own all deletion.

**Root cause:** `StopAsync` deleted the root (`KeyDeleteAsync`, line 286) BEFORE calling
`StopCleanupAsync`, but cleanup reads the root (`StringGetAsync`, RedisL2Cleanup.cs:49) to discover
the per-step keyspace and early-returns when the root is absent (line 50). The pre-delete destroyed
cleanup's input, so per-step keys leaked on every Stop (also breaks the triple-SHA redis `--scan`
BEFORE==AFTER close-gate invariant).

**Fix (1 file, `OrchestrationService.cs` StopAsync loop):**
- Replace `var rootDeleted = await db.KeyDeleteAsync(Root(id))` with
  `var rootPresent = await db.KeyExistsAsync(Root(id))`.
- Keep `if (!rootPresent) continue;` (first-win no-op on absent root — WEBAPI-SUPPRESS-01).
- `StopCleanupAsync(id)` then GETs the present root, BFS-collects per-step keys, and batch-deletes
  the root (unconditional, RedisL2Cleanup.cs:83) + per-step keys (line 84).
- Catch keeps tagging `redisOp = "KeyDeleteAsync"` (stable Stop op name — unchanged).
- Update the loop's inline comment ("KeyDeleteAsync on the root FIRST" → "KeyExistsAsync probe").

**Why correct:** root still deleted (inside cleanup); per-step keys now GC'd; first-win preserved;
symmetric with `StartAsync`'s existing `KeyExistsAsync` first-win probe (line 144); parent-index
SREM behavior unchanged (only called for present roots, as before); TOCTOU race is the same class
already accepted/deferred for Start (ORCH-SCALE-01 / T-24-04).

**Test impact:** 0 test changes. The 3 failing assertions assert the CORRECT end state (per-step
keys deleted post-Stop) and go green once the code matches them. Affected tests:
`StopGateFacts.Stop_AllExist_204`, `StopGateFacts.Stop_MixedBatch_DeletesPresent_NoOpAbsent_204`,
`StopScanFacts.Stop_AfterStart_RemovesRootAndStep_KeepsProcessor`.

**Status:** LOCKED, not yet applied — batch into the 24.1 gap-closure plan with (b)/(c)/(d).
