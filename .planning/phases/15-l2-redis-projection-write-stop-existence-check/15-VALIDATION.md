---
phase: 15
slug: l2-redis-projection-write-stop-existence-check
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-29
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

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| TBD (planner fills) | — | — | L2-PROJECT-* / ORCH-* / OBSERV-REDIS-* | — | — | integration | `dotnet test ...` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky. The planner/executor completes this map per task.*

---

## Wave 0 Requirements

- [ ] `RedisProjectionWriterFacts` / `OrchestrationStartProjectionFacts` — root/step/processor keyspace round-trip (L2-PROJECT-01..06, ORCH-START-01..08)
- [ ] `OrchestrationStopCleanupFacts` — Stop deletes root + per-step, never processors; 204/422; non-idempotent re-Stop (ORCH-STOP-01..07, D-06)
- [ ] `ProcessorTtlFacts` — TTL set/refresh-on-write; `<=0` ⇒ no expiry (D-08)
- [ ] `RedisFailureFacts` — RedisConnectionException → 500 + RFC 7807 + correlationId (ORCH-START-04, ORCH-STOP-07, OBSERV-REDIS-03)
- [ ] `RedisLogsE2ETests` — Redis ops in MEL with X-Correlation-Id (OBSERV-REDIS-02, extends Phase 11 SchemasLogsE2ETests)
- [ ] Negative-grep guards — no `OpenTelemetry.Instrumentation.StackExchangeRedis`, no `KEYS`/`IServer.Keys()` in production code (OBSERV-REDIS-01, L2-PROJECT-07)
- [ ] `RedisFixture.DisposeAsync` teardown extension — SCAN per-class prefix + `KeyDeleteAsync` to sweep TTL'd processor keys (TEST-REDIS amendment)

*Planner refines exact test class/file names.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| `redis-cli --scan` SHA-256 BEFORE=AFTER full suite | Phase 16 close gate (carried) | Cross-suite invariant, runs at phase close | Run phase-close gate script (Phase 12 12-08) |

*Most phase behaviors have automated verification via integration facts.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s (quick filter)
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
