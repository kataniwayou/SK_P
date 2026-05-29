# Phase 15: L2 Redis projection write + Stop existence check - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-29
**Phase:** 15-l2-redis-projection-write-stop-existence-check
**Areas discussed:** CorrelationId plumbing, L2 projection DTO modeling, Batch write failure semantics, Stop existence-check shape, Stop redesign (L2 cleanup), Start redesign (per-workflow stop-then-build), Liveness & jobId

---

## CorrelationId plumbing

| Option | Description | Selected |
|--------|-------------|----------|
| Accessor in service, param to writer | Inject IHttpContextAccessor into OrchestrationService; resolve once; pass correlationId param to UpsertAsync. Writer HTTP-agnostic; public signatures unchanged. | ✓ |
| Thread param from controller | Controller reads HttpContext.Items, threads correlationId through StartAsync → UpsertAsync. Changes public signature. | |
| New ICorrelationIdAccessor | AsyncLocal-backed abstraction set by middleware. New infra; duplicates HttpContext.Items. | |

**User's choice:** Accessor in service, param to writer (D-01).
**Notes:** Later simplified — single `correlationId` field on root (the start/stop split was dropped), so only Start writes it; Stop's error-path correlationId uses the existing Phase 4 customizer.

---

## L2 projection DTO modeling

| Option | Description | Selected |
|--------|-------------|----------|
| Records + [JsonPropertyName] | Dedicated internal records + shared LivenessProjection; per-field [JsonPropertyName] pins camelCase regardless of options; round-trippable. | ✓ |
| Records, camelCase members | Rely on camelCase C# member names + default options. Unconventional; breaks if a policy is applied. | |
| Anonymous objects inline | new { entryStepIds = ... } in the writer. Not round-trippable; contract scattered. | |

**User's choice:** Records + [JsonPropertyName] (D-02).
**Notes:** Surfaced the trap that System.Text.Json default options serialize PascalCase verbatim, so attributes are required for the locked camelCase field names. Enum-as-int + cron:null fall out of default options.

---

## Batch write failure semantics (Start projection)

| Option | Description | Selected |
|--------|-------------|----------|
| WhenAll, leave partial, log | CreateBatch → Execute → Task.WhenAll; first fault bubbles → 500; partial keys left; one MEL warning naming workflowId. | ✓ |
| WhenAll, no extra logging | Same, but no dedicated partial-state warning. | |
| Best-effort compensating cleanup | SCAN+delete written keys on failure. Adds DELETE on Start path; fragile; contradicts idempotent overwrite. | |

**User's choice:** WhenAll, leave partial, log (D-03).
**Notes:** User asked for an explanation of option 1 before deciding; confirmed after. Self-heals because re-Start now pre-cleans (D-07).

---

## Stop existence-check shape

| Option | Description | Selected |
|--------|-------------|----------|
| Batch + WhenAll, collect all | CreateBatch KeyExistsAsync per root; collect all missing → 422; one round-trip. | ✓ |
| Sequential awaits, collect all | Loop awaits; N round-trips. | |
| Sequential, fail-fast | 422 on first miss; names only one id — contradicts plural contract. | |

**User's choice:** Batch + WhenAll, collect all (D-04). Retained as the existence GATE in front of the new cleanup.

---

## Stop redesign — from existence-check to L2 cleanup

**User instruction (evolved across turns):**
1. (considered) Stop reads value + writes stopCorrelationId + flips liveness.status="Stopping" → **dropped**.
2. (final) "Stop will clean up L2 keys of the workflowIds[]: start with root and follow the steps; never remove the processor keys."

**Resolved forks (all confirmed):**
- Existence gate kept: any missing root → 422 + missing list, no deletion; all exist → cleanup → 204.
- Repeated Stop is non-idempotent → 2nd Stop 422.
- Collect-then-delete via CreateBatch + KeyDeleteAsync (UNLINK-style).
- Partial-failure mid-cleanup → 500; partial left; re-Stop cleans remainder.
- Dangling per-step key during traversal → skip & continue.

**Captured as:** D-06 (+ amendments to ORCH-STOP-04/-06, L2-PROJECT-07, Phase 16 SC2/SC5).

---

## Start redesign — per-workflow stop-then-build loop

**User instruction:** "For simplicity the Start flow: perform stop service then start service, per workflow." + "L3 to L1 to L2 then clean up L1, per workflow."

**Resolved forks (all confirmed):**
1. Existence 404 check stays batch-upfront before the loop.
2. Cleanup-then-build consequence accepted (failed re-Start of an invalid workflow deletes old L2 then 422s).
3. Partial-state across workflows on mid-loop 422 accepted.
4. Shared cleanup routine: tolerant for Start pre-clean, 422-gated for Stop endpoint.
5. Per-workflow gate order preserved (cycle → schema-edge → payload → project).

**Captured as:** D-07 (+ amendments to Phase 13 pipeline, Phase 14 ValidationOrderFacts, ORCH-START-05).

---

## Liveness & jobId

| Decision | Outcome |
|----------|---------|
| liveness.status lifecycle | DEFERRED — stays "Pending" everywhere; no Starting/Stopping this phase. |
| jobId | Guid.NewGuid() generated fresh per Start (was Guid.Empty). |

**Captured as:** D-05.

## Key scheme & processor TTL (follow-up clarification)

**Q: same prefix on all three key types? Is it redundant?**
- Confirmed: one shared `skp:` prefix on all three (`{prefix}{workflowId}`, `{prefix}{workflowId}:{stepId}`, `{prefix}{processorId}`).
- Raised the missing-type-discriminator smell (root vs processor both `{prefix}{guid}`) and offered a segmented `wf:`/`proc:` scheme.

| Option | Description | Selected |
|--------|-------------|----------|
| Flat single `skp:` prefix | No type segment; root & processor both `{prefix}{guid}`; functions (GUIDs don't collide). L2-PROJECT-02 unchanged. | ✓ |
| Segmented `wf:`/`proc:` | Type discriminator per key; processors structurally distinct. Would amend L2-PROJECT-02 + SC1/SC2. | |

**User's choice:** Flat single `skp:` prefix — L2-PROJECT-02 unchanged.

**Processor TTL (D-08):**
- Processor keys never deleted; value-updated every Start; TTL refresh-on-write (every POST /start re-stamps `expiry`).
- Default 100 days, configurable `Redis:ProcessorKeyTtlDays` (int days) on RedisProjectionOptions. `<=0` ⇒ no expiry. Root/step keys carry no TTL.
- Known interaction flagged: Phase 12 RedisFixture.DisposeAsync zero-residual SCAN assertion vs un-expired TTL'd processor keys → fixture/test teardown must explicitly delete keys under the per-class prefix.

## Claude's Discretion

- ReadDto → projection-record assembly details; MEL message wording; DEL vs UNLINK final pick; traversal data structure; liveness.timestamp source (prefer TimeProvider); whether RedisProjectionKeys exists or must be created; `ProcessorKeyTtlDays <= 0` ⇒ no-expiry default.

## Deferred Ideas

- liveness.status Start/Stop lifecycle; stopCorrelationId; per-processor liveness lifecycle; full Stop-side processor eviction; jobId run semantics + real liveness.interval; OBSERV-REDIS-04 Redis metrics.
