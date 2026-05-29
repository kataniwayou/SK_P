---
phase: 16-idempotency-concurrency-l1-cleanup-3-green-closeout
verified: 2026-05-29T19:30:00Z
status: passed
score: 12/12 must-haves verified
overrides_applied: 0
---

# Phase 16: Idempotency + Concurrency + L1 Cleanup + 3-GREEN Closeout Verification Report

**Phase Goal:** Start idempotency, concurrent-Start last-write-wins semantics, Stop idempotency, and end-to-end happy-path are all verified against real Postgres + real Redis; the v3.3.0 phase-close gate (3 consecutive GREEN + `psql \l` SHA-256 BEFORE=AFTER + `redis-cli --scan` SHA-256 BEFORE=AFTER) is satisfied.
**Verified:** 2026-05-29T19:30:00Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | REQUIREMENTS.md TEST-REDIS-09 describes the INVERTED Stop contract (root + per-step keys removed, processor keys intact, repeat Stop -> 422) | VERIFIED | Line 154-155: `[x] **TEST-REDIS-09**` contains "root + reachable per-step keys are removed", "per-processor keys remain intact", "non-idempotent by design"; "(amended Phase 16, 2026-05-29 — INVERTED per 15-CONTEXT ORCH-STOP-04/-06)" |
| 2 | ROADMAP.md Phase 16 SC2 no longer claims "Stop is verified to NOT delete any L2 keys" | VERIFIED | Grep for "Stop is verified to NOT delete" returns zero matches; SC2 (line 121) contains "root + reachable per-step keys are removed", "per-processor keys remain intact", "non-idempotent" |
| 3 | The flagged NOTE on ROADMAP.md (Plan 15-05) is resolved | VERIFIED | Grep for "NOTE (flagged by Plan 15-05)" returns zero matches; line 125 contains "RESOLVED (Phase 16 Plan 01, 2026-05-29)" |
| 4 | A full HTTP POST /api/v1/orchestration/start returns 204 and all 3 L2 keyspaces deserialize via System.Text.Json into the typed projection records | VERIFIED | `HappyPathE2EFacts.cs` (162 lines, commit b3f6276): `Start_HappyPath_WritesAllThreeKeyspaces` asserts 204, then deserializes WorkflowRootProjection/StepProjection/ProcessorProjection from Redis; `Assert.Equal(correlationId, root.CorrelationId)` and `StepEntryCondition.Always` enum compare both present |
| 5 | Each HTTP-reachable validation gate returns 422 with RFC 7807 body AND writes zero L2 keys for the failed workflowId (SCAN-confirmed) | VERIFIED | `GateNoWriteFacts.cs` (294 lines, commit 123b442): 3 HTTP facts (cycle/schemaEdge/payloadConfigSchema), each ends with `Assert.Equal(0, await ScanKeyCount(wfId))`; shared `Assert422Gate` helper asserts 422 + `application/problem+json` + `errors.gate`; `DoesNotContain("localhost")` ASVS V7 no-leak present |
| 6 | The missing-step gate's no-write property is resolved (Open Q2) as structurally guaranteed + documented in-code | VERIFIED | `GateNoWriteFacts.cs`: XML doc class block contains "Open Q2 (RESOLVED)", "STRUCTURALLY GUARANTEED", "throw-before-UpsertAsync"; `MissingStepGate_NoWrite_StructurallyGuaranteed` Fact exists; `Assert.True(true)` absent |
| 7 | Start-twice with the same workflowIds reflects the second write (jobId CHANGED, not merely != Guid.Empty) | VERIFIED | `IdempotencyFacts.cs` (181 lines, commit 612b692): `ReStart_SameWorkflow_ReflectsSecondWrite` contains `Assert.NotEqual(firstJobId, secondRoot!.JobId)` |
| 8 | Two parallel POST /start for the same workflowId both return 204 with no crash, and the final root is structurally valid | VERIFIED | `IdempotencyFacts.cs`: `ConcurrentStart_SameWorkflow_BothSucceed_FinalStructurallyValid` contains `Task.WhenAll`, `Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode))`, and `Assert.NotNull(root)` |
| 9 | Post-Stop: root + per-step keys are GONE, processor key is PRESENT (inverted contract) | VERIFIED | `StopScanFacts.cs` (115 lines, commit 2baf725): `Stop_AfterStart_RemovesRootAndStep_KeepsProcessor` asserts `Assert.False` on root + per-step after Stop, `Assert.True` on processor key after Stop |
| 10 | scripts/phase-16-close.ps1 and .sh exist as verbatim copies of phase-12-close.* with only Phase-number header/label edits | VERIFIED | Both files exist (169/140 lines matching phase-12 sizes, commit 8727734); contain "Phase 16", `Sort-Object -CaseSensitive` (ps1), `LC_ALL=C sort` (sh), `0d98b0de` baseline reference, Migrations guard, HEALTH byte-immutable diff guard |
| 11 | The full test suite runs GREEN 3 consecutive times with the same Passed count (deterministic) | VERIFIED | STATE.md `stopped_at`: "Phase 16 close gate PASSED — 3x235 GREEN, dual-SHA HELD"; 16-05-SUMMARY.md records Run 1=235, Run 2=235, Run 3=235 |
| 12 | psql \l SHA-256 BEFORE=AFTER and redis-cli --scan SHA-256 BEFORE=AFTER both held | VERIFIED | 16-05-SUMMARY.md: psql \l BEFORE==AFTER=`37b27e562fe1b6c6544c3f44f375b30cca16bebbf4f4c358910c229605f41441`; redis-cli --scan BEFORE==AFTER=`e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` (empty — zero residual test:cls-* keys); operator-approved baseline evolution from Phase-12 literal (0d98b0de) to evolved live value |

**Score:** 12/12 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `.planning/REQUIREMENTS.md` | TEST-REDIS-09 inverted Stop contract | VERIFIED | Contains "root + reachable per-step", "non-idempotent", "per-processor keys remain intact"; marked `[x]`; stale "NOT delete / post-Stop SCAN matches pre-Stop" absent |
| `.planning/ROADMAP.md` | SC2 inverted; NOTE resolved; SC5 + invariants preserved | VERIFIED | SC2 contains inverted language; "NOTE (flagged by Plan 15-05)" absent; "RESOLVED (Phase 16 Plan 01" present; `FLUSHDB is FORBIDDEN` invariants block intact |
| `tests/BaseApi.Tests/Features/Orchestration/HappyPathE2EFacts.cs` | TEST-REDIS-06 full-HTTP 3-keyspace round-trip | VERIFIED | 162 lines; `[Trait("Phase", "16")]`; `IClassFixture<Phase8WebAppFactory>`; all 3 Deserialize<...Projection> calls present; correlationId + enum assert; "D"-form keys only |
| `tests/BaseApi.Tests/Features/Orchestration/GateNoWriteFacts.cs` | TEST-REDIS-07 consolidated 422 + SCAN-no-write | VERIFIED | 294 lines; 4 facts; `KeysAsync(pattern` present; FLUSHDB/sync KEYS absent; all 3 gate names asserted; 4x `Assert.Equal(0,`; Open Q2 resolved in XML doc |
| `tests/BaseApi.Tests/Features/Orchestration/IdempotencyFacts.cs` | TEST-REDIS-08 sequential D-02 + concurrent D-01 | VERIFIED | 181 lines; `Assert.NotEqual(firstJobId, secondRoot!.JobId)` present; `Task.WhenAll` present; `Assert.All(responses,` present; no FLUSHDB; "D"-form keys |
| `tests/BaseApi.Tests/Features/Orchestration/StopScanFacts.cs` | TEST-REDIS-09 thin confirmatory inverted Stop | VERIFIED | 115 lines; `KeyExistsAsync` present; Assert.False on root + per-step; Assert.True on processor after Stop; "D"-form keys |
| `scripts/phase-16-close.ps1` | Phase 16 dual-SHA + 3-GREEN gate (PowerShell) | VERIFIED | 169 lines; "Phase 16" present; "Phase 12 close gate" absent; `Sort-Object -CaseSensitive` present; `0d98b0de` reference present; Migrations + HEALTH guards present |
| `scripts/phase-16-close.sh` | Phase 16 dual-SHA + 3-GREEN gate (Bash) | VERIFIED | 140 lines; "Phase 16" present; `LC_ALL=C sort` present; Migrations guard present |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| HappyPathE2EFacts | POST /api/v1/orchestration/start | HttpRequestMessage + X-Correlation-Id header | WIRED | `new HttpRequestMessage(HttpMethod.Post, "/api/v1/orchestration/start")` + `req.Headers.Add("X-Correlation-Id", correlationId)` (line 122-126) |
| HappyPathE2EFacts | Redis L2 root/step/processor keys | `_factory.RedisMultiplexer.GetDatabase().StringGetAsync` | WIRED | Three `StringGetAsync` calls with default "D"-form keys; all three `HasValue` asserted |
| GateNoWriteFacts | POST /orchestration/start (gate-failing graph) | HttpClient + Assert.Equal(HttpStatusCode.UnprocessableEntity) | WIRED | Three facts each call PostAsJsonAsync and assert 422 via shared `Assert422Gate` helper |
| GateNoWriteFacts | Redis SCAN {prefix}{wfId}* | `IServer.KeysAsync(pattern:` | WIRED | `ScanKeyCount` helper uses `server.KeysAsync(pattern: $"{prefix}{wfId}*", pageSize: 1000)` |
| IdempotencyFacts | root jobId before/after second Start | Two POSTs + two StringGetAsync | WIRED | `Assert.NotEqual(firstJobId, secondRoot!.JobId)` on lines 141 |
| IdempotencyFacts | Two parallel /start POSTs | `Task.WhenAll(t1, t2)` with two clients | WIRED | Two `HttpClient` instances; both POSTs fired; `Assert.All(responses, ...)` |
| StopScanFacts | Post-Stop key existence check | `KeyExistsAsync` after Stop | WIRED | Six `KeyExistsAsync` calls (3 pre-Stop, 3 post-Stop); inverted assertions match contract |
| scripts/phase-16-close.ps1 | dotnet test (x3) + psql \l SHA + redis-cli --scan SHA | verbatim copy of phase-12-close.ps1 logic | WIRED | `dotnet test` loop, `Passed:` regex, SHA-256 before/after comparison all present |
| phase-16 gate run | all 4 TEST-REDIS req IDs GREEN | new facts from plans 02-04 in the 3x GREEN count | WIRED | 3x235 GREEN confirmed; all four requirement IDs marked `[x]` in REQUIREMENTS.md |

### Data-Flow Trace (Level 4)

Not applicable — this phase produces integration test artifacts only, not application components that render data from a data store. The test files are themselves the "data flow" consumers (they read from real Redis via HTTP-triggered writes and assert actual values).

### Behavioral Spot-Checks

Step 7b: SKIPPED for the gate re-run (operator context note instructs not to re-run the full suite; verify artifacts and consistency instead). Gate run results are captured in STATE.md and 16-05-SUMMARY.md and verified to be consistent with the artifact content.

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| TEST-REDIS-06 | 16-02-PLAN.md | Full Start happy-path: real Postgres + real Redis + 3-keyspace STJ deserialization round-trip | SATISFIED | `HappyPathE2EFacts.cs` implements exactly this; marked `[x]` in REQUIREMENTS.md; 16-02-SUMMARY confirms 228 GREEN run including this fact |
| TEST-REDIS-07 | 16-03-PLAN.md | Each validation gate failure: 422 + RFC 7807 body + SCAN confirms zero L2 keys for failed workflowId | SATISFIED | `GateNoWriteFacts.cs`: 3 HTTP gate facts + structural arm; marked `[x]` in REQUIREMENTS.md; 16-03-SUMMARY confirms 232 GREEN |
| TEST-REDIS-08 | 16-04-PLAN.md | Start idempotency: Start-twice reflects second write; concurrent Start documents last-write-wins | SATISFIED | `IdempotencyFacts.cs`: D-02 sequential (jobId-changed) + D-01 concurrent (both 204 + root round-trips); marked `[x]` in REQUIREMENTS.md |
| TEST-REDIS-09 | 16-01-PLAN.md (doc), 16-04-PLAN.md (test) | Stop existence-gate + L2 cleanup (inverted): Stop-after-Start removes root + per-step, keeps processor intact; repeat Stop -> 422 | SATISFIED | `StopScanFacts.cs` confirms inverted post-Stop key state; REQUIREMENTS.md TEST-REDIS-09 corrected to inverted contract; marked `[x]`; also covered by Phase-15 StopGateFacts/StopCleanupFacts |

All 4 requirement IDs declared across plans 01-04 are accounted for. No orphaned requirements.

### Anti-Patterns Found

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| `scripts/phase-16-close.ps1:69-97` | Fact-count parse fallback `-1` compares equal to itself across 3 runs, silently passing gate if `Passed:` regex fails | Warning | Does not cause false-pass of a RED run (exit code checked separately); only defeats the deterministic count invariant. Gate already ran and passed with real count. Documented in REVIEW WR-01. |
| `scripts/phase-16-close.sh:31` | `docker compose ps --format json \| jq -r '.[].Health'` assumes array; modern Docker Compose v2 emits newline-delimited objects | Warning | Could mis-abort pre-flight on a healthy stack; does not affect test correctness. Gate already ran and passed. Documented in REVIEW WR-02. |
| `scripts/phase-16-close.ps1:31-38` vs `scripts/phase-16-close.sh:30` | PS1 and SH service lists diverge silently (otel-collector listed in PS1 but not SH) | Warning | Pre-flight criteria differ between the two scripts. No impact on test results. Documented in REVIEW WR-03. |
| `tests/BaseApi.Tests/Features/Orchestration/GateNoWriteFacts.cs:285-293` | `MissingStepGate_NoWrite_StructurallyGuaranteed` SCAs a random never-started GUID (near-tautological but non-vacuous) | Info | Does not create a false pass; documents structural guarantee soundly. Documented in REVIEW IN-03. |

No blockers. All warnings are in gate scripts that have already executed and passed; they are maintenance-quality concerns for future reuse. No stubs, placeholders, empty implementations, or hardcoded empty data found in any test file.

### Human Verification Required

None. All phase-close gate criteria were run by the operator and the results captured in STATE.md + 16-05-SUMMARY.md. The gate run is the human checkpoint. All automated artifact checks pass. No further human verification is required to confirm phase goal achievement.

### Gaps Summary

No gaps. All 12 must-haves verified:

- The doc-first amendment (Plan 01) correctly rewrote REQUIREMENTS.md TEST-REDIS-09 and ROADMAP.md SC2 to the inverted Stop contract, resolved the flagged NOTE, and preserved SC5 and the v3.2.0 invariants block.
- The four new integration test classes (HappyPathE2EFacts, GateNoWriteFacts, IdempotencyFacts, StopScanFacts) exist, are substantive (162-294 lines), contain all required assertions, use correct SCAN-only / "D"-form key patterns, and are wired to the real HTTP + Redis stack via `Phase8WebAppFactory`.
- The phase-close gate scripts exist as byte-faithful Phase-12 copies; the gate ran to completion at 3x235 GREEN with dual-SHA BEFORE=AFTER held (both psql \l and redis-cli --scan); EF-migration assertion clean; HEALTH-01..05 byte-immutable.
- All four TEST-REDIS requirement IDs are marked complete in REQUIREMENTS.md and represented in the GREEN suite.

**Minor documentation note (not a gap):** ROADMAP.md plan-list checkboxes for 16-04 and 16-05 remain `[ ]` rather than `[x]`. The actual deliverables exist and the gate has passed; this is a tracking-checkbox omission only, consistent with STATE.md recording "Phase 16 close gate PASSED".

---

_Verified: 2026-05-29T19:30:00Z_
_Verifier: Claude (gsd-verifier)_
