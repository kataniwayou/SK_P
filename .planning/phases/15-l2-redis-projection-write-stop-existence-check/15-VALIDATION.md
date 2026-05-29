---
phase: 15
slug: l2-redis-projection-write-stop-existence-check
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-05-29
audited: 2026-05-29
---

# Phase 15 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (BaseApi.Tests) — integration facts against real Postgres + Redis via WebApplicationFactory fixtures |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Orchestration"` |
| **Full suite command** | `dotnet test` |
| **Estimated runtime** | ~depends on Docker compose stack (Postgres + Redis + ES + Prom) |

---

## Sampling Rate

- **After every task commit:** Run the quick (Orchestration-filtered) test command
- **After every plan wave:** Run the full suite command
- **Before `/gsd-verify-work`:** Full suite must be green (3-GREEN cadence per Phase 3 D-18)
- **Max feedback latency:** quick filter < 60s; full suite per stack boot

---

## Per-Task Verification Map

Plan-grained map (each plan's tasks carry their own `<automated>` verify command; the executor ticks per-task status during execution).

| Plan | Wave | Requirements | Test Type | Automated Command | Status |
|------|------|--------------|-----------|-------------------|--------|
| 15-01 | 1 | L2-PROJECT-02/03/04/05/06 | unit (TDD RED→GREEN) | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~RedisProjectionKeysTests\|ProjectionRecordRoundTripTests"` | ✅ green |
| 15-02 | 2 | L2-PROJECT-01/03/04/05/06, ORCH-START-06 | integration | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~RedisProjectionWriterFacts"` | ✅ green |
| 15-03 | 2 | L2-PROJECT-07, ORCH-STOP-03/04 (+02/05/06/07 via 04) | integration | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~StopCleanupFacts"` | ✅ green |
| 15-04 | 3 | ORCH-START-01..08, ORCH-STOP-01..07, OBSERV-REDIS-03 | integration | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~StartLoopFacts\|StopGateFacts"` | ✅ green |
| 15-05 | 4 | OBSERV-REDIS-01/02, L2-PROJECT-06/07 (guards) | integration + negative-grep | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~OrchestrationLogsE2ETests\|RedisDisciplineGuardFacts\|ValidationOrderFacts"` | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky. Executor ticks per-task status during execution; `wave_0_complete` flips true once Plan 15-01's Wave-0 harness is GREEN.*

---

## Wave 0 Requirements

- [x] `RedisProjectionWriterFacts` — root/step/processor keyspace round-trip (L2-PROJECT-01..06, ORCH-START-06) — shipped GREEN (15-02)
- [x] `StopCleanupFacts` — Stop deletes root + per-step, never processors; cycle-safe; dangling-skip; absent no-op (ORCH-STOP-02/03/04, D-06) — shipped GREEN (15-03)
- [x] Processor TTL — covered in `RedisProjectionWriterFacts` (TTL set on processor key via `ProcessorKeyTtlDays`; `<=0` ⇒ no expiry, D-08) — shipped GREEN (15-02)
- [x] Redis-failure facts — RedisConnectionException → 500 + RFC 7807 + correlationId + `redisOp` (ORCH-START-04, ORCH-STOP-07, OBSERV-REDIS-03) — `StartLoopFacts.Start_RedisDown_500`, `StopGateFacts.Stop_RedisDown_500` + `Stop_RedisDown_OnPostGateCleanup_500_KeyExistsAsync` (WR-01 follow-up) — shipped GREEN (15-04)
- [x] `OrchestrationLogsE2ETests` — Redis ops in MEL with X-Correlation-Id round-tripped to Elasticsearch (OBSERV-REDIS-02) — shipped GREEN (15-05)
- [x] Negative-grep guards — no `OpenTelemetry.Instrumentation.StackExchangeRedis`, no `KEYS`/`IServer.Keys()` in production code (OBSERV-REDIS-01, L2-PROJECT-07) — `RedisDisciplineGuardFacts` — shipped GREEN (15-05)
- [x] `RedisFixture.DisposeAsync` teardown — SCAN per-class Guid prefix + `KeyDeleteAsync` + assert-zero (TEST-REDIS-03). Verified post-execution: zero residual `test:cls-*` keys before/after a full suite run (the original 9 `test:cls-deadredis:*` keys were stale dead-port-experiment debris, since cleaned).

*All Wave 0 references shipped GREEN; full suite 227/227 against the live compose stack (×3 runs this session).*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| `redis-cli --scan` SHA-256 BEFORE=AFTER full suite | Phase 16 close gate (carried) | Cross-suite invariant, runs at phase close | Run phase-close gate script (Phase 12 12-08). Spot-checked this session: 0 residual `test:cls-*` keys before/after a full run. |
| Redis-side metrics (latency histograms, command counts) → Prometheus | OBSERV-REDIS-04 | **Deferred — not implemented in v3.3.0** | Future-milestone candidate (documented in REQUIREMENTS.md). No automated test expected; not a coverage gap. |

*Most phase behaviors have automated verification via integration facts. OBSERV-REDIS-04 is the only in-scope-listed requirement without a test — intentionally deferred, not a gap.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (Plan 15-01 RED→GREEN unit harness)
- [x] No watch-mode flags
- [x] Feedback latency < 60s (quick filter)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved 2026-05-29

---

## Validation Audit 2026-05-29

Post-execution Nyquist coverage audit (State A — audited the planning-time strategy against shipped tests).

| Metric | Count |
|--------|-------|
| Requirements in scope | 26 (L2-PROJECT-01..07, ORCH-START-01..08, ORCH-STOP-01..07, OBSERV-REDIS-01..04) |
| COVERED (automated, green) | 25 |
| Deferred (manual-only, by design) | 1 (OBSERV-REDIS-04) |
| Gaps found (actionable) | 0 |
| Tests generated | 0 (no gaps to fill) |
| Escalated | 0 |

**Verdict:** NYQUIST-COMPLIANT. All in-scope requirements have automated verification; full suite 227/227 GREEN against the live compose stack. The single uncovered requirement (OBSERV-REDIS-04, Redis metrics) is an explicitly deferred future-milestone candidate, recorded as manual-only — not a coverage gap. The `gsd-nyquist-auditor` was not spawned because there were no gaps to fill.
