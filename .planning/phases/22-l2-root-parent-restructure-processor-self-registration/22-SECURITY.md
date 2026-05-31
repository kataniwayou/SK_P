---
phase: 22-l2-root-parent-restructure-processor-self-registration
audited: 2026-05-31
asvs_level: default
block_on: default
status: SECURED
threats_closed: 16
threats_total: 16
threats_open: 0
---

# Phase 22 Security Audit Report

**Phase Goal:** L2 Redis projection parent-index restructure + processor self-registration liveness gate.
**Audited:** 2026-05-31
**ASVS Level:** default
**Result:** SECURED — all 16 threats closed.

---

## Threat Verification

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-22-01 | Tampering | mitigate | CLOSED | `L2ProjectionKeys.cs:28` — `public const string Prefix = "skp:";`; all three builders (`Root`, `Step`, `Processor`) are parameterless; no `string prefix` parameter exists; segments are typed `Guid` via `:D` / default interpolation. No caller input feeds the prefix. |
| T-22-02 | Information disclosure | accept | CLOSED | Keys expose only GUIDs and the literal `"skp:"`. No secrets or PII reachable from key strings. Accepted: low risk. |
| T-22-03 | Tampering | mitigate | CLOSED | `OrchestratorRedisOptions.cs` deleted (file absent — Glob returns no matches); `Program.cs` contains no `Redis:KeyPrefix` read; `Orchestrator/appsettings.json` has no `Redis` section; both consumers call `OrchestratorL2Keys.Root(workflowId)` with no prefix arg. Config-injection path eliminated. |
| T-22-04 | Information disclosure | accept | CLOSED | Both `StartOrchestrationConsumer` and `StopOrchestrationConsumer` log only `workflowId` (no projection payload). Behavior unchanged this plan. Accepted: low risk. |
| T-22-05 | Tampering | mitigate | CLOSED | `RedisProjectionWriter.cs:84` — `batch.SetAddAsync(RedisProjectionKeys.ParentIndex(), wf.Id.ToString("D"))`. `RedisL2Cleanup.cs:45` — `db.SetRemoveAsync(RedisProjectionKeys.ParentIndex(), workflowId.ToString("D"))`. Both sides render the member as typed `Guid.ToString("D")`. SREM is hoisted above the absent-root early-return (line 50). No free-form string from any external caller reaches the SET. |
| T-22-06 | Denial of service | accept | CLOSED | Growth bounded by operator-Started workflows; SADD is idempotent. SREM-in-flow implemented (Phase 22, not deferred to 23 as originally planned). Accepted: low risk for single-operator deployment. |
| T-22-07 | Tampering | mitigate | CLOSED | `RedisProjectionOptions.cs` — `KeyPrefix` property absent (only `ProcessorKeyTtlDays` + `Serialization`); `BaseApi.Service/appsettings.json` Redis section contains no `KeyPrefix`; `OrchestrationService` no longer injects `IOptions<RedisProjectionOptions>` for prefix reads; all key construction routes through the compile-time const. |
| T-22-08 | Elevation of privilege | accept | CLOSED | Processor write loop removed from `RedisProjectionWriter` (PROC-NOCREATE-01 comment at line 104–106 confirms; no `StringSetAsync(RedisProjectionKeys.Processor(…))` in the method). Write surface reduced. Accepted: no new privilege path. |
| T-22-09 | Spoofing | mitigate | CLOSED | `ProcessorLivenessValidator.cs:41–52` — deserializes to typed `ProcessorProjection`; `try/catch (JsonException)` maps malformed JSON to 422 "malformed"; `projection?.Liveness is not { } liveness` guard maps null liveness to 422 "malformed". A far-past timestamp or zero interval yields `deadline <= now` → 422 "stale". Fail-safe to reject in all forged/malformed cases — never fail-open. |
| T-22-10 | Tampering | mitigate | CLOSED | `ProcessorLivenessValidator.cs:34–35` — absent key throws `ProcessorNotLive(proc.Id, "absent")`; `ProcessorLivenessValidator.cs:55–57` — `deadline <= now` throws `ProcessorNotLive(proc.Id, "stale")`. No code path passes an unhealthy/absent processor. |
| T-22-11 | Denial of service (fail-open) | mitigate (UPGRADED from accept) | CLOSED | WR-01 fix applied in commit `3ec9b64`. `ProcessorLivenessValidator.cs:41–52` — `try/catch (JsonException)` + `projection?.Liveness is not { } liveness` guard; both malformed shapes throw `ProcessorNotLive(procId, "malformed")` → 422. Regression test `ProcessorLivenessFacts.MalformedProcessorRegistration_Returns422` (Theory: `{"liveness":null}` + `"not-json-at-all"`) asserts 422 with `gate=="processorLiveness"` and `reason=="malformed"`. A `RedisException` from the GET still propagates as 500 (correct). Malformed external registration no longer produces 500. |
| T-22-12 | Information disclosure | mitigate | CLOSED | `OrchestrationValidationException.cs:96–97` — `ProcessorLivenessOffending(Guid procId, string reason)` carries only processor GUID + fixed reason string (`"absent"/"stale"/"malformed"`). No stack traces, connection strings, or internal type names. Consistent with the T-14-02 guard on the same class. |
| T-22-13 | Information disclosure | mitigate | CLOSED | `OrchestrationService.cs:154–158` — `catch (RedisException ex) { ex.Data["redisOp"] = "UpsertAsync"; throw; }` wraps the liveness GETs. Only the stable op name `"UpsertAsync"` surfaces in the 500 body; no connection details. |
| T-22-14 | Tampering | mitigate | CLOSED | `RedisFixture.cs:46,54,70–75` — `ConcurrentBag<RedisKey> TrackedKeys` + `Track(RedisKey)` + `KeyDeleteAsync(TrackedKeys.Distinct().ToArray())` in `DisposeAsync`. No `skp:*` wildcard SCAN. `ProcessorLivenessFacts` carries `[Collection("ParentIndex")]` and SREMs its wf ids. Triple-SHA BEFORE==AFTER gate confirmed in 22-VERIFICATION.md (3×271 GREEN). |
| T-22-15 | Denial of service | mitigate | CLOSED | Each parent-index-touching test class (`RedisProjectionWriterFacts`, `StopCleanupFacts`, `GateNoWriteFacts`, `ProcessorLivenessFacts`) is in `[Collection("ParentIndex")]` (non-parallel) and SREMs its own workflow ids. `ProcessorLivenessFacts.cs:109–110` — `SremWorkflowAsync` in every `finally` block. Triple-SHA gate: redis-cli --scan BEFORE==AFTER confirmed (22-VERIFICATION.md). |
| T-22-16 | Spoofing | accept | CLOSED | Test-seeded liveness JSON is test-only, confined to the throwaway keyspace (unique GUIDs), and exercises the validator's fail-safe boundary deliberately. Accepted: low risk. |

---

## Accepted Risks Log

| Threat ID | Category | Rationale |
|-----------|----------|-----------|
| T-22-02 | Information disclosure | Keys expose only GUIDs + fixed literal "skp:" — no secrets or PII. |
| T-22-04 | Information disclosure | Consumer behavior unchanged; logs only WorkflowId. No new surface. |
| T-22-06 | Denial of service | Growth bounded by active workflows; SADD idempotent; SREM-in-flow implemented this phase. Single-operator deployment. |
| T-22-08 | Elevation of privilege | Removing the processor write loop REDUCES write surface. No new privilege path introduced. |
| T-22-16 | Spoofing | Test-only; throwaway keyspace; unique GUIDs ensure isolation. |

---

## Unregistered Flags

None. All threats in SUMMARY.md `## Threat Flags` (WR-01) mapped to T-22-11 and are resolved.

---

## Non-Blocking Info Items (from 22-REVIEW.md — not security gaps)

| ID | File | Description |
|----|------|-------------|
| IN-01 | `RedisProjectionOptions.cs:16–18` | `ProcessorKeyTtlDays` XML doc still references "Refresh-on-write: every Start re-SETs processor keys" — stale post-PROC-NOCREATE-01. No behavioral impact. |
| IN-02 | `RedisProjectionWriter.cs:40,51` | `IOptions<RedisProjectionOptions>` injected but `_options` never read in `UpsertAsync`. Inert dead dependency. |
| IN-03 | `OrchestrationService.cs:251` | Stop-path Redis faults tagged `"KeyExistsAsync"` even when faulting op is a delete. Intentional convention; no security impact. |
| IN-04 | `ProcessorLivenessValidator.cs:55` | `liveness.Interval * 2` is unchecked `int` multiply; overflow at >1.07e9s is fail-safe (over-rejects, never over-accepts). Practically unreachable. |

These items are non-blocking documentation/cleanup concerns and do not affect the security posture.

---

_Audited: 2026-05-31_
_Auditor: Claude (gsd-security-auditor)_
