---
phase: 22-l2-root-parent-restructure-processor-self-registration
verified: 2026-05-31T12:00:00Z
status: passed
score: 5/5 must-haves verified
overrides_applied: 0
gaps: []
human_verification: []
resolved:
  - finding: WR-01
    resolution: "RESOLVED (not deferred) — operator-authorized fix applied in commit 3ec9b64. ProcessorLivenessValidator now wraps the deserialize in try/catch (JsonException) and guards the deserialized shape (projection?.Liveness is not { } liveness); both malformed paths throw OrchestrationValidationException.ProcessorNotLive(procId, \"malformed\") → 422 instead of NRE/JsonException escaping the redisOp catch as a 500. Regression test ProcessorLivenessFacts.MalformedProcessorRegistration_Returns422 (theory: {\"liveness\":null} + non-JSON) asserts 422 with errors.gate=='processorLiveness' and reason=='malformed'. ProcessorLivenessFacts run GREEN (5/5: 3 original + 2 malformed theory cases); BaseApi.Service + BaseApi.Tests build 0W/0E."
---

# Phase 22: L2 Root-Parent Restructure + Processor Self-Registration — Verification Report

**Phase Goal:** The L2 Redis projection gains a parent index (single Redis SET enumerating active workflow IDs), the key prefix becomes a hardcoded compile-time constant, the orchestration Start flow stops creating processor L2 entries and instead validates each participating processor's self-registered entry for existence and timestamp-based liveness, failing with 422 on absence or staleness.
**Verified:** 2026-05-31T12:00:00Z
**Status:** passed
**Re-verification:** Yes — WR-01 resolved post-review (operator-authorized fix, commit 3ec9b64)

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | All L2 key construction flows through `L2ProjectionKeys` with a compile-time `const Prefix = "skp:"` | VERIFIED | `L2ProjectionKeys.cs:28` — `public const string Prefix = "skp:";` confirmed. No `string prefix` parameter on any builder. Both forwarders (`RedisProjectionKeys.cs`, `OrchestratorL2Keys.cs`) delegate to `L2ProjectionKeys` with no prefix argument. |
| 2 | A `ParentIndex()` builder returns the bare prefix as the parent-index SET key | VERIFIED | `L2ProjectionKeys.cs:30` — `public static string ParentIndex() => Prefix;`. `RedisProjectionKeys.cs:13` — forwarder present. `L2ProjectionKeysTests.cs:22-25` — golden test asserts `ParentIndex() == "skp:"`. |
| 3 | On `StartOrchestration`, the writer SADDs the workflow id into the parent-index SET | VERIFIED | `RedisProjectionWriter.cs:84` — `tasks.Add(batch.SetAddAsync(RedisProjectionKeys.ParentIndex(), wf.Id.ToString("D")));`. `RedisProjectionWriterFacts.cs:207-209` — integration test asserts `SetMembersAsync(ParentIndex())` contains `wf.Id.ToString("D")` after Upsert. |
| 4 | The writer creates ZERO processor keys on Start; `ProcessorProjection` entries are owned by external self-registration | VERIFIED | `RedisProjectionWriter.cs:104-106` — comment explicitly notes the write loop was removed (PROC-NOCREATE-01). No `ProcessorProjection` write code anywhere in the method. `RedisProjectionWriterFacts.cs:211-214` — integration test asserts `KeyExistsAsync(Processor(procId))` is false after Upsert. |
| 5 | `StopCleanupAsync` SREMs the workflow id from the parent-index SET (hoisted above absent-root early return) | VERIFIED | `RedisL2Cleanup.cs:45` — `await db.SetRemoveAsync(RedisProjectionKeys.ParentIndex(), workflowId.ToString("D"));` — hoisted before the `if (rootJson.IsNullOrEmpty) return;` early-return at line 50. `StopCleanupFacts.cs:101-103` — integration test seeds the parent index and asserts the wf id is absent from `SMEMBERS` after Stop. |
| 6 | The prefix is NOT a configurable `Redis:KeyPrefix` value in either `appsettings.json` | VERIFIED | `BaseApi.Service/appsettings.json` — Redis section contains only `ProcessorKeyTtlDays` and `Serialization`; no `KeyPrefix` key. `Orchestrator/appsettings.json` — Redis section is absent entirely. Grep of `src/` for `KeyPrefix` in `.cs` files returns only one stale comment in `RedisServiceCollectionExtensions.cs:61`. `AppsettingsFacts.cs:51-58` — negative-assertion test `Appsettings_Redis_Section_Has_No_KeyPrefix` confirms this in the test suite. |
| 7 | `ProcessorLivenessValidator` gates Start, throwing `OrchestrationValidationException` (422) on absent or stale processor L2 entries | VERIFIED | `ProcessorLivenessValidator.cs:33-42` — reads `skp:{procId}`, throws `ProcessorNotLive(proc.Id, "absent")` if null/empty, evaluates `deadline = timestamp + interval*2`; throws `ProcessorNotLive(proc.Id, "stale")` if `deadline <= now`. `OrchestrationService.cs:150-158` — validator called after the three sync gates, before `UpsertAsync`. `ProcessorLivenessFacts.cs` — three integration tests confirm 204 all-live, 422 absent, 422 stale. |
| 8 | `ProcessorLivenessValidator` is wired into DI and the `OrchestrationService` constructor | VERIFIED | `OrchestrationServiceCollectionExtensions.cs:63,76` — `ProcessorLivenessValidator` injected into the factory lambda and registered as `AddScoped`. `OrchestrationService.cs:56,76,90` — field declared, ctor param present, null-guarded. |
| 9 | `SchemaEdgeValidator` is byte-unchanged; its tests remain green | VERIFIED | `SchemaEdgeValidator.cs` — logic is identical to pre-phase description (null-side passes, strict Guid equality on both non-null). The REVIEW notes it as unchanged. `SchemaEdgeFacts.cs` is in the ParentIndex collection but was only amended to seed live processors (required by the new liveness gate, not a behavioral change to edge validation). Close gate ran 3×271 GREEN including SchemaEdgeFacts. |
| 10 | `OrchestrationValidationException.ProcessorNotLive` produces `gate=="processorLiveness"` with `{procId, reason}` offending payload | VERIFIED | `OrchestrationValidationException.cs:76-81` — `ProcessorNotLive` factory method produces gate `"processorLiveness"`, title `"Participating processor is not live"`, offending `ProcessorLivenessOffending(procId, reason)`. `ProcessorLivenessFacts.cs:181-184, 221` — integration tests assert `errors.gate=="processorLiveness"` and `reason` values. |

**Score:** 5/5 requirement truths verified (all 5 spec requirements implemented and tested)

---

## Requirement-by-Requirement Verification

### L2IDX-01 — Workflow Parent Index

**Acceptance criteria from SPEC:**
- After Starting N workflows, `SMEMBERS` of the parent index returns exactly those N workflow IDs.

**Verdict: VERIFIED**

- `RedisProjectionWriter.cs:84` — `batch.SetAddAsync(RedisProjectionKeys.ParentIndex(), wf.Id.ToString("D"))` on every Start.
- `RedisL2Cleanup.cs:45` — `db.SetRemoveAsync(RedisProjectionKeys.ParentIndex(), workflowId.ToString("D"))` on every Stop/cleanup (hoisted above absent-root early-return).
- `RedisProjectionWriterFacts.cs:207-209` — SMEMBERS assertion in the integration test.
- `StopCleanupFacts.cs:84,101-103` — seeds with SADD, asserts SREM-after-Stop.
- Note per SPEC scope: "Removal-on-Stop is Phase 23" is superseded — the SREM was implemented in this phase (Plan 03) as part of the cleanup routine. This is an in-scope addition that satisfies the acceptance criterion.

---

### L2PREFIX-01 — Hardcoded Prefix Constant

**Acceptance criteria from SPEC:**
- A grep of `src/` shows zero reads of a configurable key-prefix.
- Both `appsettings.json` files no longer contain `Redis:KeyPrefix`.
- All L2 keys still resolve through the single shared `L2ProjectionKeys`.

**Verdict: VERIFIED**

- `L2ProjectionKeys.cs:28` — `public const string Prefix = "skp:";`
- `RedisProjectionKeys.cs` — all 4 builders forward to `L2ProjectionKeys` with no prefix arg.
- `OrchestratorL2Keys.cs` — `Root(Guid)` forwards to `L2ProjectionKeys.Root(workflowId)` with no prefix arg.
- `src/` grep for `KeyPrefix` in `.cs` files: one stale comment in `RedisServiceCollectionExtensions.cs:61` ("KeyPrefix and Serialization.JsonOptions are the only fields") — this is a stale doc comment but NOT a key-prefix read into key construction. No configurable prefix feeds into any key builder.
- `BaseApi.Service/appsettings.json` — no `KeyPrefix` key in Redis section.
- `Orchestrator/appsettings.json` — no Redis section at all (OrchestratorRedisOptions deleted in commit `18e7f87`).
- `OrchestratorRedisOptions.cs` — deleted (commit `18e7f87`).

Note: the stale comment in `RedisServiceCollectionExtensions.cs:61` ("KeyPrefix and Serialization.JsonOptions are the only fields") is a documentation error flagged as IN-01 in the REVIEW, but it does not affect behavior — it is not a configurable-prefix read path.

---

### PROC-NOCREATE-01 — Writer Stops Creating Processor L2 Entries

**Acceptance criteria from SPEC:**
- After a Start with M participating processors, the writer has created zero `{prefix}{procId}` keys.

**Verdict: VERIFIED**

- `RedisProjectionWriter.cs:104-106` — comment explicitly states the processor write loop was removed.
- No `ProcessorProjection` write, no `ProcessorKeyTtlDays` TTL write, no `SetAsync` call for processor keys anywhere in `UpsertAsync`.
- `RedisProjectionWriterFacts.cs:211-214` — integration test asserts `db.KeyExistsAsync(L2ProjectionKeys.Processor(procId))` is `false` after Upsert.
- `GateNoWriteFacts.cs:267-295` — `ProcessorLivenessGate_Returns422_AndWritesNoKeys` fact verifies zero L2 keys written when the gate fires.

---

### PROC-LIVE-01 — Processor Existence + Liveness Validation at Start

**Acceptance criteria from SPEC:**
- Start returns 204 when all participating processors exist and `timestamp + interval*2 > now`.
- Start returns 422 when any participating processor's entry is absent.
- Start returns 422 when any participating processor's entry is stale (`timestamp + interval*2 <= now`).

**Verdict: VERIFIED (programmatically confirmed; WR-01 malformed-entry edge case now RESOLVED — see below)**

- `ProcessorLivenessValidator.cs:33-42` — reads `skp:{procId}`, absent-check, `deadline = timestamp.AddSeconds(interval*2)`, stale check.
- `OrchestrationService.cs:150-158` — validator called in Start loop, after sync trio, before UpsertAsync.
- `ProcessorLivenessFacts.cs`:
  - `AllProcessorsLive_Returns204` (line 115) — both processors seeded with `LivenessProjection(now, 300, "Live")` → asserts `HttpStatusCode.NoContent`.
  - `AbsentProcessor_Returns422` (line 155) — one processor not seeded → asserts `UnprocessableEntity`, `gate=="processorLiveness"`, `reason=="absent"`, `procId` matches.
  - `StaleProcessor_Returns422` (line 196) — stale seed `LivenessProjection(now.AddDays(-1), 0, "Live")` → asserts `UnprocessableEntity`, `reason=="stale"`.
- Close gate: 3×271 GREEN confirms all three facts pass in the real-stack suite.

**WR-01 RESOLVED (commit 3ec9b64):** The malformed-external-registration gap is closed. `ProcessorLivenessValidator` now wraps the deserialize in `try/catch (JsonException)` and guards the deserialized shape with `if (projection?.Liveness is not { } liveness)`; both malformed shapes (`{"liveness":null}` / invalid JSON) throw `OrchestrationValidationException.ProcessorNotLive(proc.Id, "malformed")` → 422 instead of NRE/JsonException escaping the `redisOp` catch as a 500. A `RedisException` from the GET still propagates to the `redisOp` catch and stays a 500 — only the parse/shape failure becomes a 422. Regression test `ProcessorLivenessFacts.MalformedProcessorRegistration_Returns422` (theory: `{"liveness":null}` + non-JSON) asserts 422 with `errors.gate=="processorLiveness"` and `reason=="malformed"`. The class runs GREEN (5/5).

---

### PROC-EDGE-01 — Edge-Schema Validation Preserved

**Acceptance criteria from SPEC:**
- Existing `SchemaEdgeValidator` tests remain green with no behavioral change.

**Verdict: VERIFIED**

- `SchemaEdgeValidator.cs` — logic is identical to the pre-phase description in the SPEC Background section. No changes to the validation algorithm.
- `SchemaEdgeFacts.cs` — amended only to seed live processors (required because the new liveness gate runs after SchemaEdgeValidator; without seeding, a valid-schema graph would fail at the liveness gate, not the schema gate). The edge-schema logic itself is unchanged.
- Close gate: 3×271 GREEN, SchemaEdgeFacts included.

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | `const Prefix + ParentIndex() + no-prefix builders` | VERIFIED | 37 lines; `const Prefix = "skp:"`, `ParentIndex()`, `Root(Guid)`, `Step(Guid, Guid)`, `Processor(Guid)` all present. |
| `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs` | Writer-side forwarders incl. `ParentIndex()` | VERIFIED | 4 forwarders to `L2ProjectionKeys`; `ParentIndex()` present. |
| `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` | Reader-side `Root(Guid)` forwarder | VERIFIED | Single `Root(Guid)` forwarder; no prefix param. |
| `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` | SADD parent index + no processor write | VERIFIED | `SetAddAsync(ParentIndex(), wf.Id:D)` at line 84; no processor write loop. |
| `src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs` | SREM parent index hoisted above absent-root return | VERIFIED | `SetRemoveAsync(ParentIndex(), wfId:D)` at line 45 — before `if rootJson.IsNullOrEmpty return` at line 50. |
| `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` | Async existence + liveness gate | VERIFIED | 45 lines; absent check + `timestamp + interval*2 > now` stale check; throws `ProcessorNotLive(procId, reason)`. |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` | `ProcessorLivenessValidator` wired into Start | VERIFIED | Lines 56, 76, 90, 150-158 — field, ctor param, null-guard, call site in Start loop. |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs` | `ProcessorNotLive` factory + `ProcessorLivenessOffending` record | VERIFIED | Lines 76-81, 97 — factory method `gate="processorLiveness"`, record `(procId, reason)`. |
| `src/BaseApi.Service/appsettings.json` | No `Redis:KeyPrefix` | VERIFIED | Redis section contains only `ProcessorKeyTtlDays` and `Serialization`. |
| `src/Orchestrator/appsettings.json` | No `Redis:KeyPrefix` | VERIFIED | No Redis section present; `OrchestratorRedisOptions` deleted in commit `18e7f87`. |
| `tests/BaseApi.Tests/Features/Orchestration/ProcessorLivenessFacts.cs` | 204 / 422-absent / 422-stale facts | VERIFIED | 229 lines; three facts confirmed; direct L2 seed pattern; `[Collection("ParentIndex")]`. |
| `tests/BaseApi.Tests/Features/Orchestration/ParentIndexCollection.cs` | Non-parallel xUnit collection for parent-index classes | VERIFIED | `[CollectionDefinition("ParentIndex", DisableParallelization = true)]` present. |
| `tests/BaseApi.Tests/Composition/RedisFixture.cs` | Known-key cleanup; no `skp:*` SCAN | VERIFIED | `ConcurrentBag<RedisKey> TrackedKeys` + `Track(key)` + `KeyDeleteAsync(TrackedKeys)` in `DisposeAsync`. No wildcard SCAN. |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `RedisProjectionKeys.cs` | `L2ProjectionKeys.cs` | Static forwarder | WIRED | All 4 builders delegate to `L2ProjectionKeys.*`; grep confirmed no `string prefix` param anywhere. |
| `OrchestratorL2Keys.cs` | `L2ProjectionKeys.cs` | Static forwarder | WIRED | `Root(Guid workflowId) => L2ProjectionKeys.Root(workflowId)`. |
| `RedisProjectionWriter.cs` | `RedisProjectionKeys.ParentIndex()` | `batch.SetAddAsync` on Start | WIRED | Line 84: `batch.SetAddAsync(RedisProjectionKeys.ParentIndex(), wf.Id.ToString("D"))`. |
| `RedisL2Cleanup.cs` | `RedisProjectionKeys.ParentIndex()` | `db.SetRemoveAsync` on Stop | WIRED | Line 45: `db.SetRemoveAsync(RedisProjectionKeys.ParentIndex(), workflowId.ToString("D"))` — hoisted. |
| `OrchestrationService.StartAsync` | `ProcessorLivenessValidator.ValidateAsync` | Awaited call in Start loop | WIRED | Lines 150-158: called after sync validators, before UpsertAsync; Redis faults tagged. |
| `OrchestrationServiceCollectionExtensions` | `ProcessorLivenessValidator` | `sp.GetRequiredService<ProcessorLivenessValidator>()` | WIRED | Lines 63 (factory inject) + 76 (registration). |
| `ProcessorLivenessFacts` | `L2ProjectionKeys.Processor(procId)` | Direct L2 seed via `db.StringSetAsync` | WIRED | Line 104: seed pattern `db.StringSetAsync(L2ProjectionKeys.Processor(procId), JsonSerializer.Serialize(projection))`. |
| `RedisProjectionWriterFacts` | `RedisProjectionKeys.ParentIndex()` | `db.SetMembersAsync` assertion | WIRED | Lines 207-209: SMEMBERS assertion after Upsert. |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|--------------|--------|-------------------|--------|
| `ProcessorLivenessValidator` | `raw` (StringGet result) | `db.StringGetAsync(RedisProjectionKeys.Processor(proc.Id))` | Yes — reads real Redis key written by external registrant (simulated by test seed) | FLOWING |
| `RedisProjectionWriter.UpsertAsync` | `wf` (WorkflowGraphSnapshot) | `snapshot.Workflows.Values.Single()` from `IWorkflowGraphLoader.LoadL1Async` | Yes — populated from L1 Postgres query | FLOWING |
| Parent index SET `skp:` | `wf.Id.ToString("D")` | `SetAddAsync` from writer | Yes — GUID from loaded workflow entity | FLOWING |

---

### Behavioral Spot-Checks

Step 7b: SKIPPED — verifying these behaviors requires a running compose stack (real Redis, real Postgres). The close gate (3×271 GREEN + triple-SHA BEFORE==AFTER) serves as the authoritative behavioral confirmation.

---

### Requirements Coverage

| Requirement | Source Plan | Status | Evidence |
|-------------|------------|--------|---------|
| L2IDX-01 — Workflow parent index | 22-01, 22-03, 22-05 | SATISFIED | SADD in writer; SREM in cleanup; SMEMBERS assertion in writer facts and cleanup facts. |
| L2PREFIX-01 — Hardcoded prefix constant | 22-01, 22-02, 22-03, 22-05 | SATISFIED | `const Prefix = "skp:"` in `L2ProjectionKeys`; configurable prefix removed from both option classes and both appsettings; negative assertion test in `AppsettingsFacts`. |
| PROC-NOCREATE-01 — Writer stops creating processor L2 entries | 22-03, 22-05 | SATISFIED | Processor write loop deleted from writer; zero-processor-key assertion in `RedisProjectionWriterFacts`. |
| PROC-LIVE-01 — Processor existence + liveness validation at Start | 22-04, 22-05 | SATISFIED | `ProcessorLivenessValidator` exists, is wired, throws 422. Three acceptance-path integration tests GREEN. WR-01 malformed-entry 500-vs-422 edge case is a hardening gap (see Human Verification). |
| PROC-EDGE-01 — Edge-schema validation preserved | 22-05 | SATISFIED | `SchemaEdgeValidator` unchanged; SchemaEdgeFacts GREEN in close gate. |

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `RedisServiceCollectionExtensions.cs` | 61 | Stale comment: "KeyPrefix and Serialization.JsonOptions are the only fields" — `KeyPrefix` was removed in Phase 22 (L2PREFIX-01); `RedisProjectionOptions` no longer has a `KeyPrefix` property | Info (IN-01 from REVIEW) | No behavioral impact; documentation misleads future readers about `RedisProjectionOptions` fields. |
| `RedisProjectionOptions.cs` | 17-18 | `ProcessorKeyTtlDays` XML doc still says "Refresh-on-write: every Start re-SETs processor keys with this expiry" — no longer true since PROC-NOCREATE-01 removed the write loop | Info (IN-01 from REVIEW) | Actively misleading: the field is dead config (nothing reads it post-PROC-NOCREATE-01). |
| `RedisProjectionWriter.cs` | 40-53 | `IOptions<RedisProjectionOptions>` injected but `_options` never read in `UpsertAsync` | Info (IN-02 from REVIEW) | Inert dead dependency — dead code, not a functional gap. |
| `ProcessorLivenessValidator.cs` | 37-40 | ~~`JsonSerializer.Deserialize<ProcessorProjection>(raw!)!` + `projection.Liveness` dereference — no null-guard~~ | Warning (WR-01) — RESOLVED (commit 3ec9b64) | Fixed: try/catch (JsonException) + `projection?.Liveness is not { } liveness` guard; both malformed paths → 422 `reason=="malformed"`. Regression test added. |

---

### Human Verification — RESOLVED

#### 1. WR-01: Malformed processor registration produces 500 not 422 — RESOLVED (commit 3ec9b64)

**Status:** RESOLVED (not deferred). Operator-authorized fix applied and verified by automated regression test (no human runtime observation needed — the test asserts the HTTP status code directly). Resolution option (a) was taken.

The original finding is preserved below for the audit trail.

**Test:**
1. Ensure the real stack is up (BaseApi.Service + Redis).
2. HTTP-seed a Processor → Step → Workflow via the API.
3. Manually write a malformed processor L2 entry: `redis-cli SET skp:{procId} '{"inputDefinition":null,"outputDefinition":null,"liveness":null}'`
4. POST `/api/v1/orchestration/start` with the workflow id.

**Expected (per SPEC PROC-LIVE-01 contract):** 422 Unprocessable Entity with `errors.gate == "processorLiveness"` and a reason such as `"absent"` or `"malformed"` — because a null/absent `liveness` is effectively "not live".

**Actual (by code inspection):** `JsonSerializer.Deserialize<ProcessorProjection>(raw!)!` succeeds (STJ does not enforce non-nullable annotations without `RespectNullableAnnotations`). `projection.Liveness` is `null`. `liveness.Timestamp` throws `NullReferenceException`, which is NOT a `RedisException`, so it falls through the `catch (RedisException)` block and reaches the fallback handler as **500 Internal Server Error**.

**Why human:** The review flagged this as WR-01 (Warning). It cannot be verified programmatically because it requires runtime observation of the HTTP status code. The fix is straightforward (guard the deserialized shape — see REVIEW WR-01 for the patch), but whether this gap is acceptable for Phase 22's scope boundary (external registrants may send malformed JSON; processors are not yet implemented) is a product decision.

**Suggested fix from REVIEW:**
```csharp
ProcessorProjection? projection;
try
{
    projection = JsonSerializer.Deserialize<ProcessorProjection>(raw!);
}
catch (JsonException)
{
    throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "malformed");
}
if (projection?.Liveness is not { } liveness)
    throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "malformed");
var deadline = liveness.Timestamp.AddSeconds(liveness.Interval * 2);
```

**Resolution options:**
- (a) Apply the fix (makes PROC-LIVE-01 robust against malformed external data, consistent 422 contract) — recommended.
- (b) Accept the 500 for Phase 22 scope (external processors are not yet implemented; malformed entries are out of scope) — add an override entry to this file.

---

### Gaps Summary

No blocking gaps. All five SPEC requirements are implemented, wired, and tested. The close gate exited 0 with 3×271 GREEN and triple-SHA BEFORE==AFTER held.

The one review warning (WR-01) has been RESOLVED (commit 3ec9b64): a malformed external processor registration (null `liveness` field or invalid JSON) now produces HTTP 422 with `reason=="malformed"` — consistent with the PROC-LIVE-01 contract — instead of the previous 500. A regression test (`ProcessorLivenessFacts.MalformedProcessorRegistration_Returns422`) pins this behavior; `BaseApi.Service` + `BaseApi.Tests` build 0W/0E and the `ProcessorLivenessFacts` class runs GREEN (5/5). Status upgraded to `passed`.

Three info-level items (stale doc comment in `RedisServiceCollectionExtensions`, stale XML doc on `ProcessorKeyTtlDays`, dead `IOptions` injection in `RedisProjectionWriter`) are non-blocking and do not affect goal achievement.

---

_Verified: 2026-05-31T12:00:00Z_
_Verifier: Claude (gsd-verifier)_
