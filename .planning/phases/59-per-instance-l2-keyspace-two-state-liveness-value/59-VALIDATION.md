---
phase: 59
slug: per-instance-l2-keyspace-two-state-liveness-value
status: approved
nyquist_compliant: true
wave_0_complete: false
created: 2026-06-13
---

# Phase 59 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (Facts/Theory; hermetic, no real Redis/stack) |
| **Config file** | existing `tests/BaseApi.Tests` project |
| **Quick run command** | `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Release -warnaserror` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests` |
| **Estimated runtime** | ~seconds (hermetic unit tests) |

---

## Sampling Rate

- **After every task commit:** Run the task's `<automated>` command (Release build or targeted `dotnet test --filter`)
- **After every plan wave:** Run `dotnet test tests/BaseApi.Tests`
- **Before `/gsd-verify-work`:** Full suite green AND `dotnet build -c Release -warnaserror && dotnet build -c Debug -warnaserror` both 0-warning (SC-5 phase gate — both configs)
- **Max feedback latency:** seconds (no E2E, no watch-mode)

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 59-01-01 | 01 | 1 | STATE-01 | T-59-01 | `Unhealthy` const + `SchemaOutcome` SoT added additively | build | `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Release -warnaserror` | ❌ W0 | ⬜ pending |
| 59-01-02 | 01 | 1 | KEY-04/STATE-01/STATE-02 | T-59-02 | `ProcessorLivenessEntry` + `Create` factory: any Fail⇒Unhealthy, null⇒Success | build | `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Release -warnaserror` | ❌ W0 | ⬜ pending |
| 59-01-03 | 01 | 1 | KEY-04/STATE-01/STATE-02 | T-59-03 | shape test: no `inputDefinition`/`outputDefinition` keys; factory invariant theory | unit | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~ProcessorLivenessEntry"` | ❌ W0 | ⬜ pending |
| 59-02-01 | 02 | 1 | KEY-01/KEY-02 | T-59-04 | `PerInstance`/`InstanceIndex` builders; legacy `Processor(Guid)` left in place | build | `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Release -warnaserror` | ❌ W0 | ⬜ pending |
| 59-02-02 | 02 | 1 | KEY-03 | T-59-05 | shared `InstanceId.Resolve()` SoT; byte-identical chain (`ToString("N")` fallback) | build | `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Release -warnaserror` | ❌ W0 | ⬜ pending |
| 59-02-03 | 02 | 1 | KEY-01/KEY-02/KEY-03 | T-59-06 | golden key-string pins (`skp:proc:` prefix) + resolver facts | unit | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~L2ProjectionKeys\|FullyQualifiedName~InstanceIdResolver"` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `ProcessorLivenessEntryFacts.cs` (new) — shape/absence test + factory invariant theory (KEY-04, STATE-01, STATE-02)
- [ ] `InstanceIdResolverFacts.cs` (new) — resolver chain facts (KEY-03)
- [ ] `L2ProjectionKeysTests.cs` (modified) — golden pins for new `PerInstance`/`InstanceIndex` builders (KEY-01, KEY-02)

*All validation is hermetic (no real Redis/stack) per RESEARCH §Validation Architecture.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| — | — | — | — |

*All phase behaviors have automated (hermetic) verification — contract-surface phase, no runtime integration.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < ~10s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved 2026-06-13
