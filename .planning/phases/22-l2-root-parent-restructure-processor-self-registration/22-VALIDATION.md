---
phase: 22
slug: l2-root-parent-restructure-processor-self-registration
status: approved
nyquist_compliant: true
wave_0_complete: true
created: 2026-05-31
reconstructed: true
---

# Phase 22 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> **Reconstructed retroactively** from PLAN/SUMMARY/VERIFICATION artifacts (State B) — the phase
> executed and closed (triple-SHA gate exit 0, 3×271 GREEN) before this file existed.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (3.2.2) on Microsoft.Testing.Platform (MTP) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (`UseMicrosoftTestingPlatformRunner=true`, `OutputType=Exe`) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -- --filter-class "*ProcessorLivenessFacts"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (real-stack: requires Postgres + Redis + RabbitMQ up) |
| **Close gate** | `scripts/phase-22-close.ps1` (3× full suite GREEN + triple-SHA psql/redis/rabbitmq BEFORE==AFTER) |
| **Estimated runtime** | ~3m30s per full suite run (271 tests); quick filtered runs ~seconds |

*MTP filter note: the class filter is passed after `--` as `--filter-class "*ClassName"`; this is not the legacy VSTest `--filter` syntax.*

---

## Sampling Rate

- **After every task commit:** Run the relevant `--filter-class` quick command for the task's test class.
- **After every plan wave:** Run the full suite (`dotnet test`).
- **Before `/gsd-verify-work`:** Full suite must be green.
- **Phase close:** `scripts/phase-22-close.ps1` — 3× GREEN + triple-SHA invariants held.
- **Max feedback latency:** ~210 seconds (full real-stack suite).

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 22-01-01 | 01 | 1 | L2PREFIX-01, L2IDX-01 | T-22-01 | Prefix is a compile-time `const`; no caller-supplied input feeds key construction | unit | `dotnet test … -- --filter-class "*L2ProjectionKeysTests"` | ✅ | ✅ green |
| 22-01-02 | 01 | 1 | L2PREFIX-01 | T-22-01 | Both thin forwarders carry no-prefix signatures (HARDEN-03 single source) | unit | `dotnet test … -- --filter-class "*L2ProjectionKeysTests"` | ✅ | ✅ green |
| 22-01-03 | 01 | 1 | L2IDX-01 | T-22-01 | `ParentIndex()` golden asserts bare `"skp:"`; per-class-prefix fact removed | unit | `dotnet test … -- --filter-class "*L2ProjectionKeysTests"` | ✅ | ✅ green |
| 22-02-01 | 02 | 2 | L2PREFIX-01 | T-22-03 | Reader consumers drop `OrchestratorRedisOptions`; read via no-prefix `Root(Guid)` | unit | `dotnet test … -- --filter-class "*StartStopConsumerAckTests"` | ✅ | ✅ green |
| 22-02-02 | 02 | 2 | L2PREFIX-01 | T-22-03 | `OrchestratorRedisOptions` + its DI + `Redis:KeyPrefix` config deleted (reader) | unit | `dotnet test … -- --filter-class "*StartStopConsumerAckTests"` | ✅ | ✅ green |
| 22-03-01 | 03 | 2 | L2PREFIX-01 | T-22-07 | `KeyPrefix` removed from `RedisProjectionOptions` + BaseApi.Service appsettings | unit | `dotnet test … -- --filter-class "*RedisProjectionOptionsBindingFacts"` | ✅ | ✅ green |
| 22-03-02 | 03 | 2 | L2IDX-01, PROC-NOCREATE-01 | T-22-08 | Writer SADDs `wf.Id:D` into parent index; writes **zero** processor keys | integration | `dotnet test … -- --filter-class "*RedisProjectionWriterFacts"` | ✅ | ✅ green |
| 22-03-03 | 03 | 2 | L2IDX-01 | T-22-07 | Cleanup SREMs wf id, hoisted above absent-root early-return (idempotent GC) | integration | `dotnet test … -- --filter-class "*StopCleanupFacts"` | ✅ | ✅ green |
| 22-04-01 | 04 | 3 | PROC-LIVE-01 | T-22-12 | `ProcessorNotLive` factory → gate `"processorLiveness"`, offending = `(procId, reason)` only (no leakage) | integration | `dotnet test … -- --filter-class "*ProcessorLivenessFacts"` | ✅ | ✅ green |
| 22-04-02 | 04 | 3 | PROC-LIVE-01 | T-22-09 | Validator: absent → 422 `"absent"`; stale (`timestamp+interval*2 ≤ now`) → 422 `"stale"`; fail-safe reject | integration | `dotnet test … -- --filter-class "*ProcessorLivenessFacts"` | ✅ | ✅ green |
| 22-04-03 | 04 | 3 | PROC-LIVE-01, PROC-EDGE-01 | T-22-09 | Gate wired after sync trio, before Upsert; `SchemaEdgeValidator` byte-unchanged | integration | `dotnet test … -- --filter-class "*SchemaEdgeFacts"` | ✅ | ✅ green |
| 22-05-01 | 05 | 4 | L2PREFIX-01 | T-22-14 | `RedisFixture` known-key cleanup (Track/TrackedKeys); **no** `skp:*` wildcard SCAN | integration | `dotnet test … -- --filter-class "*RedisFixtureFacts"` | ✅ | ✅ green |
| 22-05-02 | 05 | 4 | L2PREFIX-01, L2IDX-01, PROC-NOCREATE-01 | T-22-14 | Appsettings KeyPrefix-absent negative; writer SMEMBERS + zero-processor-key; cleanup SREM; gate 422 arm | integration | `dotnet test … -- --filter-class "*AppsettingsFacts"` | ✅ | ✅ green |
| 22-05-03 | 05 | 4 | PROC-LIVE-01 | T-22-09 | `ProcessorLivenessFacts`: 204 all-live / 422 absent / 422 stale / 422 malformed (WR-01) | integration | `dotnet test … -- --filter-class "*ProcessorLivenessFacts"` | ✅ | ✅ green |
| 22-05-04 | 05 | 4 | L2IDX-01 | T-22-14 | `ParentIndexCollection` (`DisableParallelization=true`) serializes all parent-index classes | integration | `dotnet test … -- --filter-class "*GateNoWriteFacts"` | ✅ | ✅ green |
| 22-05-05 | 05 | 4 | all 5 | — | Triple-SHA close gate: 3×271 GREEN + psql/redis/rabbitmq BEFORE==AFTER, exit 0 | gate | `pwsh scripts/phase-22-close.ps1` | ✅ | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

`dotnet test …` abbreviates `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj`.

---

## Wave 0 Requirements

*Existing infrastructure covers all phase requirements.* The xUnit v3 / MTP harness, `Phase8WebAppFactory`/`HarnessWebAppFactory` real-stack fixtures, and `RedisFixture` were all in place from prior phases. Phase 22 added test classes (`ProcessorLivenessFacts`, `ParentIndexCollection`) within that existing infrastructure — no new framework install was required.

---

## Requirement → Coverage Summary

| Requirement | Acceptance Criterion | Covering Test(s) | Status |
|-------------|----------------------|------------------|--------|
| **L2IDX-01** — Workflow parent index | After Starting N workflows, `SMEMBERS` parent index = those N wf IDs | `RedisProjectionWriterFacts` (SMEMBERS contains `wf.Id:D`), `StopCleanupFacts` (SADD-seed → SREM-after-Stop), `L2ProjectionKeysTests` (`ParentIndex()=="skp:"` golden) | COVERED |
| **L2PREFIX-01** — Hardcoded prefix const | `const Prefix`; no configurable prefix read in `src/`; neither appsettings has `Redis:KeyPrefix` | `L2ProjectionKeysTests`, `RedisProjectionKeysTests` (no-prefix builders), `AppsettingsFacts` (KeyPrefix-absent negative), `RedisProjectionOptionsBindingFacts` (no KeyPrefix facts) | COVERED |
| **PROC-NOCREATE-01** — Writer creates zero processor keys | After Start with M processors, writer created zero `{prefix}{procId}` keys | `RedisProjectionWriterFacts` (`KeyExists(Processor(procId))==false`), `GateNoWriteFacts` (`ProcessorLivenessGate_Returns422_AndWritesNoKeys`) | COVERED |
| **PROC-LIVE-01** — Processor existence + liveness at Start | 204 all-live; 422 absent; 422 stale (`timestamp+interval*2 ≤ now`) | `ProcessorLivenessFacts` (204 all-live / 422 absent / 422 stale / 422 malformed = 5/5), `GateNoWriteFacts` (processorLiveness 422 arm) | COVERED |
| **PROC-EDGE-01** — Edge-schema validation preserved | `SchemaEdgeValidator` tests remain green, no behavioral change | `SchemaEdgeFacts` (GREEN in close gate; validator byte-unchanged per `git diff --stat`) | COVERED |

---

## Manual-Only Verifications

*All phase behaviors have automated verification.*

The one historical human-verification item — **WR-01** (malformed external processor registration producing 500 instead of 422) — was **RESOLVED** (commit `3ec9b64`) and is now pinned by an automated regression test (`ProcessorLivenessFacts.MalformedProcessorRegistration_Returns422`, theory: `{"liveness":null}` + non-JSON). It is therefore no longer a manual verification.

The VERIFICATION report's "Behavioral Spot-Checks (Step 7b)" were marked SKIPPED in favor of the close gate (3×271 GREEN + triple-SHA), which is the authoritative real-stack behavioral confirmation — not a manual gap.

---

## Validation Sign-Off

- [x] All tasks have automated verify (xUnit v3 facts) — no Wave 0 dependencies required
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references — none (no gaps; existing infra sufficient)
- [x] No watch-mode flags (MTP one-shot `dotnet test` runs)
- [x] Feedback latency < 210s (full real-stack suite)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved 2026-05-31 (reconstructed — all 5 requirements COVERED, close gate exit 0)

---

## Validation Audit 2026-05-31

| Metric | Count |
|--------|-------|
| Requirements audited | 5 |
| COVERED | 5 |
| PARTIAL | 0 |
| MISSING | 0 |
| Gaps found | 0 |
| Tests generated | 0 (full coverage already present) |
| Escalated | 0 |

**Result:** Phase 22 is Nyquist-compliant. Every SPEC requirement maps to an existing, named, GREEN automated test; the triple-SHA close gate exited 0 with 3×271 GREEN. No gaps required filling — VALIDATION.md reconstructed from artifacts (State B) for the audit trail.

---

*Phase: 22-l2-root-parent-restructure-processor-self-registration*
*Reconstructed: 2026-05-31*
