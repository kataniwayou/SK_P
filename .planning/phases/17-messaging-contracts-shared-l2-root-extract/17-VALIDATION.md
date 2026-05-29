---
phase: 17
slug: messaging-contracts-shared-l2-root-extract
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-30
---

# Phase 17 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 `3.2.2` under Microsoft.Testing.Platform (MTP) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (`OutputType=Exe` + `UseMicrosoftTestingPlatformRunner` + `TestingPlatformDotnetTestSupport`) |
| **Quick run command** | `dotnet build SK_P.sln -c Debug` + `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter-class *ProjectionRecordRoundTripTests` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Estimated runtime** | ~28-35 seconds (full suite, historical) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet build SK_P.sln -c Debug` (catches CS0246 missed-swap + CS1591 missing-doc immediately) + affected projection tests (`--filter-class *ProjectionRecordRoundTripTests`)
- **After every plan wave:** Run full `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` + zero-warning `dotnet build SK_P.sln -c Release`
- **Before `/gsd-verify-work`:** Full suite must be green 3× consecutively + zero-warning Release AND Debug + byte-identical Redis (`redis-cli --scan`) and psql (`\l`) snapshots BEFORE=AFTER
- **Max feedback latency:** ~35 seconds

---

## Per-Task Verification Map

> Task IDs are placeholders until plans are written; the planner populates exact IDs. Threat refs map to the Security Domain section of RESEARCH.md (data-integrity + supply-chain, not attacker-driven).

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 17-01-* | 01 | 1 | INFRA-RMQ-01 / MSG-CONTRACTS-01 | supply-chain (MassTransit v9) | v8.5.5 pinned, NO PackageReference anywhere | build/grep | `grep -E "MassTransit.*8\.5\.5" Directory.Packages.props` + zero `MassTransit` PackageReference grep | ❌ W0 | ⬜ pending |
| 17-01-* | 01 | 1 | MSG-CONTRACTS-01 | — | new leaf has no MassTransit/AspNetCore dep | build/grep | grep new csproj: no `MassTransit`, no `Microsoft.AspNetCore.App` FrameworkReference | ❌ W0 | ⬜ pending |
| 17-01-* | 01 | 1 | MSG-CONTRACTS-01 | — | `BaseApi.Service` references `Messaging.Contracts` | build assertion | `dotnet list src/BaseApi.Service/BaseApi.Service.csproj reference` includes `Messaging.Contracts` | ❌ W0 | ⬜ pending |
| 17-0?-* | ? | ? | MSG-CONTRACTS-02 | — | Start/Stop = exactly `Guid[] WorkflowIds`, no correlation | compile + review (optional reflection test) | compiler + review; optional `--filter-class *` shape test | ❌ W0 (optional) | ⬜ pending |
| 17-0?-* | ? | ? | MSG-CONTRACTS-03 | — | `ICorrelated` = six get-only Guids | compile + review (optional reflection test) | compiler + review; optional shape test | ❌ W0 (optional) | ⬜ pending |
| 17-0?-* | ? | ? | MSG-CONTRACTS-04 | Tampering (wire integrity) | root + liveness wire shape byte-identical (camelCase, null `cron`, nested liveness keys, round-trip) | unit (serialization) | `dotnet test ... --filter-class *ProjectionRecordRoundTripTests` | ✅ | ⬜ pending |
| 17-0?-* | ? | ? | MSG-CONTRACTS-04 | Tampering (wire integrity) | writer + cleanup still produce/consume root after using-swap | integration | existing `RedisProjectionWriterFacts`, `HappyPathE2EFacts`, `IdempotencyFacts`, `StopCleanupFacts` | ✅ | ⬜ pending |
| 17-0?-* | ? | ? | SC#5 (cross-cutting) | — | zero-warning Release + Debug; v3.3.0 suite GREEN | build + full suite | `dotnet build SK_P.sln -c Release` + `-c Debug` + 3× `dotnet test` | ✅ | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] CPM-pin + forbidden-`PackageReference` grep assertion for MassTransit (mirror Plan 12-01 negative-grep) — proves INFRA-RMQ-01 (pin present) AND MSG-CONTRACTS-01 (no PackageReference)
- [ ] `dotnet list reference` assertion that `BaseApi.Service` references `Messaging.Contracts`
- [ ] New-csproj dependency-ceiling grep (no `MassTransit`, no `Microsoft.AspNetCore.App` FrameworkReference) — proves MSG-CONTRACTS-01 / D-01
- [ ] (Optional, low value) reflection/shape tests for `StartOrchestration`/`StopOrchestration` (`Guid[] WorkflowIds`, no correlation) and `ICorrelated` (six get-only Guids) — compiler + review already constrain these
- No new framework install needed — xUnit v3 stack already present
- **No gap for the wire-shape guard:** `ProjectionRecordRoundTripTests.cs` already covers SC#3 / MSG-CONTRACTS-04 and must simply stay GREEN through the using-swap

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| `Orchestrator` references `Messaging.Contracts` | MSG-CONTRACTS-01 (SC#1 "both hosts") | `Orchestrator` does not exist yet (Phase 19) — the "referenced by both" criterion is partly forward-looking; only the `BaseApi.Service` reference is provable this phase | Confirm in Phase 19 that `Orchestrator.csproj` adds the ProjectReference; this phase verifies only the `BaseApi.Service` edge |
| Byte-identical Redis/psql snapshots BEFORE=AFTER | SC#5 (behavior-preserving) | Requires running services + comparing live state dumps | `redis-cli --scan` + psql `\l` (or established close-gate script) before and after the move; diff must be empty |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 35s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
