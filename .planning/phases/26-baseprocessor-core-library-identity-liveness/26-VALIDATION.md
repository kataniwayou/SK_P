---
phase: 26
slug: baseprocessor-core-library-identity-liveness
status: draft
nyquist_compliant: true
wave_0_complete: false
created: 2026-06-01
---

# Phase 26 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Seeded from `26-RESEARCH.md` §"Validation Architecture".

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 (Microsoft.Testing.Platform runner) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (`OutputType=Exe`, `UseMicrosoftTestingPlatformRunner=true`, `TestingPlatformDotnetTestSupport=true`) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -- --filter-class "*Processor*"` (MTP filter via `-- --filter-class`) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Estimated runtime** | ~30–60 seconds (Redis round-trip + in-memory harness slices) |

> No new test project. Add `BaseProcessor.Core` as a `ProjectReference` to the existing `tests/BaseApi.Tests` project (mirrors Phase 18 `BaseConsole.Core` / Phase 19 `Orchestrator` — `BaseApi.Tests.csproj:119-127`). Place tests under `tests/BaseApi.Tests/Processor/`.

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -- --filter-class "*Processor*"` (new processor slice).
- **After every plan wave:** Run `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (full suite — guards no regression in existing console/orchestrator/CRUD suites).
- **Before `/gsd-verify-work`:** Full suite must be green.
- **Max feedback latency:** ~60 seconds.

---

## Per-Task Verification Map

> Requirement-keyed seed. The planner maps each row to concrete Task IDs (`26-NN-NN`) during planning; every phase requirement below MUST land on at least one task with an `<automated>` verify or a Wave 0 dependency.

| Requirement | Behavior | Threat Ref | Secure Behavior | Test Type | Automated Command (`--filter-class`) | File Exists | Status |
|-------------|----------|------------|-----------------|-----------|--------------------------------------|-------------|--------|
| BPC-01 / BPC-03 / CONFIG-01(part) | `AddBaseProcessor` registers full graph (Redis, health, bus, request clients, orchestrator, heartbeat) + removes `StartupCompletionService` | — | Fail-fast `cfg.Require` names missing key, not value | unit (descriptor inspection) | `*AddBaseProcessorFacts*` | ❌ W0 | ⬜ pending |
| BPC-02 / D-12 | `BaseProcessor` abstract class compiles; test double overrides `ProcessAsync`; DI-resolves | — | N/A | unit | `*BaseProcessorSeamFacts*` | ❌ W0 | ⬜ pending |
| IDENT-03 | `AssemblyMetadataSourceHashProvider` reads attribute; throws when absent; stub injects known hash | — | Fail-fast throw when SourceHash absent | unit | `*SourceHashProviderFacts*` | ❌ W0 | ⬜ pending |
| IDENT-04 / RPC-04 | Loop A: harness responder returns NotFound×2 then Found → context populated; retry asserted | T-26 (Spoofing: only-Healthy gate downstream) | Host never crashes on timeout/not-found (boot-before-register tolerated) | integration (in-memory harness) | `*IdentityResolutionFacts*` | ❌ W0 | ⬜ pending |
| SCHEMA-01 / SCHEMA-02 | Loop B: non-null Ids resolved (retry→found); null Ids skipped (no request sent); config Id never queried | — | Absent definition is never a failure (by design) | integration (harness) | `*SchemaResolutionFacts*` | ❌ W0 | ⬜ pending |
| LIVE-01 / LIVE-02 / LIVE-04 / LIVE-06 | Heartbeat writes `skp:{id:D}` only when Healthy; not before; whole-value `SET` with TTL; lock-free | T-26 (Spoofing) | Non-Healthy replica writes nothing → orchestrator sees `absent` | integration (FakeTimeProvider + RedisFixture) | `*LivenessHeartbeatFacts*` | ❌ W0 | ⬜ pending |
| LIVE-05 (closed loop) | Written JSON deserializes via `JsonSerializer.Deserialize<ProcessorProjection>` AND passes the real `ProcessorLivenessValidator` as "live"; advance clock past `interval×2` → reader sees `stale` | T-26 (Tampering: typed record can't emit wrong shape) | Reader maps malformed → 422 (unchanged) | integration (RedisFixture + real validator) | `*LivenessReaderRoundTripFacts*` | ❌ W0 | ⬜ pending |
| LIVE-03 | `interval` written == configured `IntervalSeconds` (seconds) | — | N/A | unit/assertion (within round-trip) | `*LivenessReaderRoundTripFacts*` | ❌ W0 | ⬜ pending |
| D-11 (resilience) | Redis fault on a beat → warning logged, loop continues, host stays up | T-26 (DoS: Redis bounce) | Log-and-continue; soft-dep `abortConnect=false`; host never crashes | integration (dead-Redis multiplexer) | `*LivenessResilienceFacts*` | ❌ W0 | ⬜ pending |
| CONFIG-01 | `ProcessorLivenessOptions` binds `Interval`/`Ttl` independently; retry timeout + backoff cap bind | T-26 (Info Disclosure / DoS) | `cfg.Require` names key only; bounded backoff cap (~30s) | unit | `*ProcessorOptionsBindingFacts*` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] Add `<ProjectReference Include="..\..\src\BaseProcessor.Core\BaseProcessor.Core.csproj" />` to `tests/BaseApi.Tests/BaseApi.Tests.csproj`.
- [ ] `tests/BaseApi.Tests/Processor/` test folder + a harness fixture mirroring `ConsoleCorrelationFilterTests`'s `AddMassTransitTestHarness`, with a **sequenceable** stub responder for `GetProcessorBySourceHash` / `GetSchemaDefinition` (NotFound→NotFound→Found).
- [ ] Reuse the existing `RedisFixture` (`localhost:6380`, known-key cleanup via `Track`) for the L2 round-trip — **track `skp:{testProcessorId:D}`** so teardown deletes it (triple-SHA close-gate discipline).
- [ ] `FakeTimeProvider` registration in heartbeat tests (already pinned: `Microsoft.Extensions.TimeProvider.Testing 8.10.0`).
- [ ] (Wave 0 confirmation) A harness round-trip test confirming the `exchange:`-scheme request-client URI + 8.5.5 `GetResponse<TFound,TNotFound>()` overload (research assumption A5/A2 — MEDIUM confidence, no in-repo precedent).

*Framework install: none — xunit.v3 + MassTransit test harness + FakeTimeProvider + StackExchange.Redis + NSubstitute all already referenced.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| — | — | — | All phase behaviors have automated verification (the writer↔reader closed loop is asserted in-test against the real `ProcessorLivenessValidator`; real-broker E2E is deferred to Phase 28). |

*All Phase 26 behaviors have automated verification.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
