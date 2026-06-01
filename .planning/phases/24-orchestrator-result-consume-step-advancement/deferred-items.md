# Phase 24 — Deferred / Tracking Items

## RESOLVED: the "4 failures" — identified, in-scope, and routed to Phase 24.1

**Status:** RESOLVED (diagnosis complete). Phase 24 = **FAILED verification**; the fix is the
gap-closure phase **24.1** (`.planning/phases/24.1-gating-redesign-l2-dedup-gate-removal/`).

**Final diagnosis** (authoritative clean-build run: exit 1, `Failed: 4, Passed: 331, Total: 335`;
TRX-confirmed names): all 4 failures are in `BaseApi.Tests.Features.Orchestration.*` — **in-scope,
not flaky** (the earlier "pre-existing flakies" characterization was wrong; so was a transient
"309/0" misread of a wrapper exit code, now retracted).

| Test | Cause |
|------|-------|
| `StopGateFacts.Stop_AllExist_204` | Stop deletes root before cleanup → per-step keys leak (ordering regression) |
| `StopGateFacts.Stop_MixedBatch_DeletesPresent_NoOpAbsent_204` | same ordering regression |
| `StopScanFacts.Stop_AfterStart_RemovesRootAndStep_KeepsProcessor` | same ordering regression |
| `StartLoopFacts.ReStart_Removes_Orphan_Step` | asserts superseded Phase-22 overwrite-GC; first-win makes re-Start a no-op |

Two clean-build traps were also found and fixed/committed this session (commits `43c74fa`,
`7e95f81`): `ExecutionResult` ctor arity (CS1729) and an uncommitted `using`-alias.

**Resolution:** all of (a) Stop-ordering, (b) orphan test, (c) WR-01, (d) WR-02 are folded into the
**24.1 gating redesign** — see `24.1-SPEC.md` / `24.1-CONTEXT.md` / `24.1-01-PLAN.md`. The locked
fix (a) below is ABSORBED by 24.1 R3 (atomic discover-then-delete).

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

**Status:** SUPERSEDED by 24.1 R3 — the probe-not-delete change is now part of the atomic
discover-then-delete Stop (one `CreateBatch`). See `24.1-01-PLAN.md` Task 1+2. Decisions (b)/(c)/(d)
are all locked in `24.1-CONTEXT.md` (D-24.1-04 orphan-dissolved, D-24.1-05 gate removal, D-24.1-06
terminal guard).
