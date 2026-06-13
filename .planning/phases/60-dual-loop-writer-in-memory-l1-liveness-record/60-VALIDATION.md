---
phase: 60
slug: dual-loop-writer-in-memory-l1-liveness-record
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-13
---

# Phase 60 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (`TestContext.Current.CancellationToken`, `IClassFixture<T>`) |
| **Config file** | none custom — standard `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "Phase=60"` (after tagging new classes `[Trait("Phase","60")]`) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Estimated runtime** | ~Phase-60 filter <30s; full suite minutes (Redis facts need compose up on localhost:6380) |

Two test families are reused:
- **Pure-hermetic (no stack):** options binding (`ProcessorOptionsBindingFacts`), orchestrator-driven facts using the in-memory MassTransit harness + `FakeTimeProvider` + NSubstitute stubs (`IdentityResolutionFacts`, `SchemaResolutionFacts`, `DispatchBindSequenceFacts`).
- **Real-Redis-on-localhost:6380 (`RedisFixture`):** liveness write-behavior facts (`LivenessHeartbeatFacts`) with `FakeTimeProvider` driving the beat loop and net-zero key tracking.

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "Phase=60"`
- **After every plan wave:** Run `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj`
- **Before `/gsd-verify-work`:** Full suite green + 0-warning Release AND Debug (`-warnaserror`)
- **Max feedback latency:** ~30 seconds (Phase-60 filter)

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| W0 | — | 0 | L1-01 | — | N/A | unit | `--filter "FullyQualifiedName~ProcessorLivenessStateFacts"` | ❌ W0 | ⬜ pending |
| W0 | — | 0 | LOOP-03/04 | — | TTL never lapses below floor | integration | `--filter "FullyQualifiedName~ProcessorLivenessWriterFacts"` | ❌ W0 | ⬜ pending |
| W0 | — | 0 | STATE-03/LOOP-01 | — | replica visible as `unhealthy`, never absent | integration | `--filter "FullyQualifiedName~StartupUnhealthyWriteFacts"` | ❌ W0 | ⬜ pending |
| — | — | — | LOOP-03 | — | `StartupInterval` binds (default 30) | unit | `--filter "FullyQualifiedName~ProcessorOptionsBindingFacts"` | ✅ extend | ⬜ pending |
| — | — | — | LOOP-02/04 | — | frozen `healthy` refresh on new key + L1, TTL=max(20,30)=30 | integration | `--filter "FullyQualifiedName~LivenessHeartbeatFacts"` | ✅ modify | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

### Observable behaviors (Nyquist sampling)

1. **Per-instance key present + `status=Unhealthy`** during the post-identity resolution window (GET `PerInstance(id, instId)` → deserialize → `Status == LivenessStatus.Unhealthy`). [STATE-03/LOOP-01]
2. **Summary reflects progress:** one schema unresolved ⇒ that summary field = `FAIL`; once resolved ⇒ `SUCCESS`. [LOOP-01/D-04]
3. **Index SADD:** `SMEMBERS InstanceIndex(id)` contains `instanceId` after first write; idempotent across both loops (count stays 1). [LOOP-04/D-15]
4. **TTL banding:** `KeyTimeToLiveAsync(PerInstance) ∈ (ttl-grace, ttl]`, ttl=30 (heartbeat) / 60 (startup). [LOOP-04/D-13]
5. **Recorded interval:** `entry.Interval == StartupIntervalSeconds` (startup) / `== IntervalSeconds` (heartbeat). [LOOP-03/D-12]
6. **Frozen healthy:** post-Healthy beats write `status=Healthy` + all-SUCCESS summary; timestamp advances per beat (monotonic via `FakeTimeProvider.Advance`). [LOOP-02/D-14]
7. **L1 == L2:** the `ProcessorLivenessEntry` in the L1 holder equals the value just SET to L2. [L1-01/D-09]
8. **L1 publication across threads:** `IProcessorLivenessState.Current` returns the last `Update`d entry (volatile read). [L1-01/D-10]
9. **Old key NOT written:** no `skp:{id}` (flat `Processor` key) created by either loop after the swap. [D-05]
10. **Resilience:** startup/heartbeat write against a dead Redis logs-and-continues; host does not crash, resolution still reaches Healthy. [Discretion/heartbeat-precedent]

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Processor/ProcessorLivenessStateFacts.cs` — L1 holder Update/Current (L1-01) — NEW, pure-hermetic.
- [ ] `tests/BaseApi.Tests/Processor/ProcessorLivenessWriterFacts.cs` — shared writer: PerInstance SET + TTL banding + index SADD idempotency (LOOP-03/04) — NEW, `RedisFixture` (track both keys; SREM/track the index SET member for net-zero).
- [ ] `tests/BaseApi.Tests/Processor/StartupUnhealthyWriteFacts.cs` — orchestrator inline `unhealthy` write + summary-progress + resilience (STATE-03/LOOP-01) — NEW, harness + Redis (or stubbed `IConnectionMultiplexer`).
- [ ] MODIFY `tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs` — swap assertions to `PerInstance` key + `ProcessorLivenessEntry` (frozen Healthy) + L1 update + index SADD.
- [ ] MODIFY `tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs` — add `StartupInterval` (six knobs).
- [ ] MODIFY the 3 `new ProcessorStartupOrchestrator(...)` facts for the grown ctor (`IdentityResolutionFacts`, `SchemaResolutionFacts`, `DispatchBindSequenceFacts`).
- [ ] MODIFY `tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs` — assert the new L1 holder (+ writer) registration descriptors.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live RealStack proof of cross-replica visibility | (deferred) | RealStack/live proof + close gate is Phase 62, not 60 | N/A this phase — hermetic + RedisFixture cover all Phase-60 behaviors |

*All Phase-60 behaviors have automated verification (unit + RedisFixture integration). The cross-replica live proof is explicitly Phase 62.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 30s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
