---
phase: 62
slug: live-proof-close-gate
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-13
---

# Phase 62 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 on Microsoft.Testing.Platform (MTP) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | compiled `BaseApi.Tests.exe --filter-not-trait Category=RealStack` (hermetic — NOT `dotnet test --filter`, which MTP0001 ignores) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release --no-build` (live, RealStack included — requires the v7 stack up) |
| **Estimated runtime** | hermetic ~tens of seconds; live close gate ~50 min/run (N=3 cadence, operator-gated) |

---

## Sampling Rate

- **After every task commit:** Run the hermetic suite (`--filter-not-trait Category=RealStack`) — proves new/retagged tests COMPILE and the hermetic suite stays green (D-14).
- **After every plan wave:** `dotnet build SK_P.sln -c Release` AND `-c Debug` both 0-warning + hermetic suite green.
- **Before `/gsd-verify-work`:** Build gate green both configs; `scripts/phase-62-close.ps1` exists and is AST-valid; new RealStack tests COMPILE (excluded from hermetic by `Category=RealStack`).
- **Phase gate (operator):** live N=3×GREEN close run + the multi-container lifecycle runbook (`62-HUMAN-UAT.md`). TEST-01/02/03 stay unticked until the operator records GREEN.
- **Max feedback latency:** hermetic < 60s; live gate is operator-scheduled (not in the per-task loop).

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 62-01-xx | 01 | 1 | TEST-01 | T-62-tamper | SREM fabricated index members in teardown (no index pollution) | xUnit RealStack | full-suite run; new fabricated-key keyspace/gate test | ❌ W0 (new test) | ⬜ pending |
| 62-01-xx | 01 | 1 | TEST-02 | T-62-V5/V7 | malformed value → 422 not 500; 422 reason is counts-only, no instanceIds/secrets | xUnit RealStack | full-suite run; new gate ≥1-healthy / 422 test (D-04 fabricated keys) | ❌ W0 (new test) | ⬜ pending |
| 62-01-xx | 01 | 1 | TEST-02 (probe verdict) | T-62-V7 | summary Data carries only SchemaOutcome strings, no secrets | hermetic (clock) | `LivenessWatchdogHealthCheckTests` (REUSE — Phase 61) | ✅ exists | ⬜ pending |
| 62-02-xx | 02 | 1 | TEST-01 / TEST-03 | T-62-spoof | rebuilt v7 images; SourceHash `^[a-f0-9]{64}$` validated; live poll | infra (compose) | `compose.yaml` `processor-sample` → `deploy.replicas:2`; stack composes | ❌ W0 (reshape) | ⬜ pending |
| 62-03-xx | 03 | 2 | TEST-03 | T-62-repud | clean-keyspace BEFORE-dirty trap; N=3 identical-fact-count Smell-A guard | operator gate | `pwsh -File scripts/phase-62-close.ps1` (clone of 58 + D-09 prefix exclusion) | ❌ W0 (clone) | ⬜ pending |
| 62-03-xx | 03 | 2 | TEST-01 / TEST-02 (live) | T-62-spoof | lifecycle proofs on real containers; probe via `docker exec` | manual/operator | `62-HUMAN-UAT.md` runbook (mirror `58-HUMAN-UAT.md`) | ❌ W0 (new runbook) | ⬜ pending |
| 62-0x-xx | retag | 1 | TEST-03 (regression) | — | full v5/v6 regression carried forward | xUnit RealStack | retag SC1/SC2/SC3 + GateAComposition `[Trait("Phase","62")]` (D-10) | ❌ W0 (retag) | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

*Task IDs are placeholders — the planner assigns final `{phase}-{plan}-{task}` IDs.*

---

## Wave 0 Requirements

- [ ] New xUnit RealStack test(s) for the deterministic gate verdicts + keyspace assertions (D-04 fabricated keys) — `[Trait("Category","RealStack")]` + `[Trait("Phase","62")]` (covers TEST-01 deterministic, TEST-02 gate).
- [ ] Fabricated-key seeding helper co-located with `PollForHealthyLivenessAsync` (writes per-instance keys + SADD index directly to host Redis using `ProcessorLivenessEntry.Create` / `L2ProjectionKeys`).
- [ ] `scripts/phase-62-close.ps1` — clone of `phase-58-close.ps1` with the D-09 prefix exclusion (`$_ -notmatch '^skp:proc:'`) + retitle (covers TEST-03). AST-valid + exists (D-14).
- [ ] `62-HUMAN-UAT.md` operator runbook (mirror `58-HUMAN-UAT.md` structure) — lifecycle proofs (2-replica self-register, restart→unhealthy via durable startup write, dead→TTL-expiry+SREM, `docker exec` `/health/live` probe) + N=3 GREEN record block.
- [ ] Retag SC1/SC2/SC3 + GateAComposition `[Trait("Phase","58")]` → `[Trait("Phase","62")]` (D-10).
- [ ] `compose.yaml` `processor-sample` reshape: drop `container_name`, add `deploy.replicas:2` (D-01).

**REUSE (no Wave-0 work):** `LivenessWatchdogHealthCheckTests` (probe verdict, RF-02), `RealStackWebAppFactory` + seed/poll helpers + net-zero teardown, `RealStackNetZeroSweepFixture`, `Processor.BadConfig` profile-gated tier (D-05 durably-broken replica).

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Two REAL replicas self-register distinct per-instance keys + SADD index | TEST-01 | Genuinely multi-container; the xUnit harness does NOT drive processor lifecycle (D-03) | `docker compose up -d --build` (2 replicas); `redis-cli SMEMBERS skp:proc:{procId}` shows 2 members; each `skp:proc:{procId}:{instanceId}` GET shows `status=Healthy` |
| Restarting/never-healthy replica observable as `unhealthy` (never absent) | TEST-01 | Requires a real durably-broken container reaching the startup loop (D-05; reuse `Processor.BadConfig` per RF-01) | bring up `--profile badconfig`; `redis-cli GET skp:proc:{badId}:{instanceId}` shows `status=Unhealthy` |
| Dead replica's key TTL-expires + is lazily SREM'd | TEST-01 | Requires real `docker stop` + wait past TTL (healthy replica = 30s budget per RF-03; broken startup key = 60s) | `docker stop` a healthy replica; wait >30s; GET→null; trigger orchestration-start read; `SMEMBERS` shrinks |
| Self-watchdog probe live-wired (`/health/live` verdict + summary) | TEST-02 | Probe is in-process on internal port 8082, no published port on scaled tier (D-02); target a HEALTHY replica (RF-02 note) | `docker exec <healthy-replica> wget -qO- localhost:8082/health/live` → verdict + per-schema summary |
| N=3 consecutive GREEN + triple-SHA net-zero close gate | TEST-03 | ~50 min/run; requires the full rebuilt v7 stack up healthy (D-15) | `pwsh -File scripts/phase-62-close.ps1` against the clean 2-replica steady state; record GREEN |

*Hermetic verdict math for the stale-L1 probe IS automated (RF-02 — `LivenessWatchdogHealthCheckTests`, FakeTimeProvider). Only the live-wiring proof is manual.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify (hermetic suite + build gate cover the per-task loop; live proofs are explicitly operator-gated)
- [ ] Wave 0 covers all MISSING references (new tests, close script, runbook, retags, compose reshape)
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s (hermetic)
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
