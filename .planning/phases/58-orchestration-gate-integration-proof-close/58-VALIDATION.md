---
phase: 58
slug: orchestration-gate-integration-proof-close
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-13
---

# Phase 58 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (xunit.v3 3.2.2) on Microsoft.Testing.Platform (MTP) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (`OutputType=Exe`, `UseMicrosoftTestingPlatformRunner=true`) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-not-trait "Category=RealStack"` |
| **Full suite command** | `pwsh -File scripts/phase-58-close.ps1` (operator-gated live N=3 GREEN; runs RealStack E2E unfiltered) |
| **Estimated runtime** | ~30s hermetic quick run · ~50 min/run live close gate |

---

## Sampling Rate

- **After every task commit:** Run `dotnet build SK_P.sln -c Release` + hermetic `dotnet test ... -- --filter-not-trait "Category=RealStack"` (RealStack E2E excluded — they need the live stack but MUST compile)
- **After every plan wave:** Both-config (Release+Debug) 0-warning build + full hermetic suite green
- **Before `/gsd-verify-work`:** Build gate green (D-11, autonomously-verifiable); live N=3 GREEN is operator-gated (D-12)
- **Max feedback latency:** ~30s hermetic; live gate ~50 min/run (operator-gated)

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 58-W0 | — | 0 | CFG-08, CFG-09 | — | N/A | build/compile | `dotnet build SK_P.sln -c Release` + `-c Debug` (0-warning) | ❌ W0 (new project + tests + script) | ⬜ pending |
| 58-CFG-08 | TBD | live | CFG-08 | — | Config-incompatible processor never latches Healthy → orchestration-start blocked 422 (no false-negative pass) | RealStack E2E (operator-gated) | full-suite via `phase-58-close.ps1` (N=3) — asserts (a) Gate A clash log in ES, (b) `skp:{badId}` stably absent, (c) Start 422 | ❌ W0 — new Gate-A test | ⬜ pending |
| 58-CFG-09 | TBD | live | CFG-09 | — | Config-compatible processor passes Gate A → Healthy → liveness written → orchestration starts (Gate A not a false-positive blocker) | RealStack E2E (operator-gated) | full-suite via `phase-58-close.ps1` (N=3) — asserts Gate A passes (no clash log + key appears), `skp:{sampleId}` present, Start 204 | ❌ W0 — new Gate-A test / extend Sample seed | ⬜ pending |
| 58-REG | TBD | live | CFG-08/09 (regression) | — | v5 recovery SCs still hold end-to-end (milestone "seal everything") | RealStack E2E | full-suite via `phase-58-close.ps1` | ✅ SC1/2/3 exist — retag `[Trait("Phase","58")]` only | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `src/Processor.BadConfig/` project (csproj + Dockerfile + Program.cs + `BadConfig.cs` + `BadConfigProcessor.cs` + appsettings.json) — distinct embedded SourceHash subject for CFG-08
- [ ] `SK_P.sln` — add `Processor.BadConfig` project
- [ ] `compose.yaml` — `processor-badconfig` service behind a Compose profile (D-04)
- [ ] New Gate-A composition E2E test(s) — CFG-08 (incompatible→clash-log+absent+422) and CFG-09 (compatible→Healthy→204); `[Trait("Category","RealStack")]` + `[Trait("Phase","58")]`. Covers CFG-08, CFG-09
- [ ] Two-schema seed helpers (GET-or-create by sentinel Name — schemas have NO uniqueness constraint, so filter-by-Name then reuse-or-POST, never PUT/409) — extend `SeedProcessorAsync` / add `SeedConfigSchemaAsync`; flip Sample seed `ConfigSchemaId: null` → compatible non-null
- [ ] `scripts/phase-58-close.ps1` — clone of `phase-55-close.ps1` + D-09 deltas (two-schema/two-processor CREATE-IF-ABSENT seed, version verify `3.5.0`, badconfig-profile bring-up)
- [ ] `58-HUMAN-UAT.md` runbook — operator N=3 GREEN-run record (mirror `55-HUMAN-UAT.md`)
- [ ] SC1/SC2/SC3 retag `[Trait("Phase","55")]` → `[Trait("Phase","58")]` (mechanical)
- [ ] No framework install needed — xUnit v3 / MTP infra already present

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live N=3 consecutive-GREEN close gate + triple-SHA (psql/redis/rabbitmq) BEFORE==AFTER net-zero | CFG-08, CFG-09 | Requires the rebuilt v6 docker stack incl. the badconfig profile; embedded SourceHash must match host build or liveness gate false-passes/times out; ~50 min/run | Run `pwsh -File scripts/phase-58-close.ps1` from a clean redis keyspace; record N=3 GREEN + triple-SHA equality + `skp-dlq-1` depth==0 + `skp:msg:*` count==0 in `58-HUMAN-UAT.md` (D-12) |

*CFG-08/09 stay unticked until the operator's GREEN run.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify (build gate) or Wave 0 dependencies, or are operator-gated with a HUMAN-UAT runbook entry
- [ ] Sampling continuity: build gate runs after every task commit
- [ ] Wave 0 covers all MISSING references (new project, compose service, Gate-A tests, two-schema seed, close script, UAT runbook, SC retag)
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s for hermetic build gate
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
