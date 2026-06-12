---
phase: 57
slug: startup-config-schema-fetch-gate-a
status: ready
nyquist_compliant: true
wave_0_complete: false
created: 2026-06-12
---

# Phase 57 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (`TestContext.Current.CancellationToken`) + NSubstitute + `Microsoft.Extensions.Time.Testing.FakeTimeProvider` + MassTransit `ITestHarness` + ASP.NET Core `WebApplicationFactory` (`Phase8WebAppFactory`, real Postgres+Redis testcontainers) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~ConfigSchemaCoverageFacts \| FullyQualifiedName~SchemaDefinitionFreezeFacts \| FullyQualifiedName~SchemaResolutionFacts \| FullyQualifiedName~DispatchBindSequenceFacts"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests --filter-not-trait "Category=RealStack"` |
| **Estimated runtime** | quick ~20–30s (the freeze facts boot a testcontainer); full hermetic suite ~baseline (530/530 at Phase-56 gate) |

---

## Sampling Rate

- **After every task commit:** Run the **quick run command** (Gate A + freeze slice — sub-30s).
- **After every plan wave:** Run the **full suite command** (full hermetic suite).
- **Before `/gsd-verify-work`:** Full hermetic suite GREEN + Release+Debug 0-warning. (RealStack E2E — config-incompatible processor blocked 422 — is **Phase 58**, NOT this phase.)
- **Max feedback latency:** ~30 seconds (quick slice).

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 57-01-01 | 01 | 0 | CFG-05 | T-57-01 | Spike grounds the STJ rule table that closes T-56-03 Tampering gap | unit (pure + Deserialize spike) | `dotnet test --filter "FullyQualifiedName~ConfigSchemaCoverageFacts.Spike"` | ❌ W0 (creates it) | ⬜ pending |
| 57-01-02 | 01 | 0 | CFG-10 | T-57-02 | 409 body pins safe-disclosure contract (Guid only) | integration (WAF, RED) | `dotnet build tests/BaseApi.Tests -c Debug` (compiles RED) | ❌ W0 (creates it) | ⬜ pending |
| 57-01-03 | 01 | 0 | CFG-03/04/06/07 | — | invert/extend facts to post-Gate-A contract (RED) | unit (harness, RED) | `dotnet build tests/BaseApi.Tests -c Debug` (expected CS errors on ConfigDefinition) | ⚠️ invert/extend | ⬜ pending |
| 57-02-01 | 02 | 1 | CFG-05, CFG-07 | T-57-03 / T-57-04 | structural walk only — no Evaluate, no external `$ref` (SSRF lockdown holds) | unit (table-driven) | `dotnet test --filter "FullyQualifiedName~ConfigSchemaCoverageFacts"` | ✅ (Plan 01) | ⬜ pending |
| 57-03-01 | 03 | 2 | CFG-03 | — | ConfigDefinition on context only, not in L2 projection (D-14) | unit (build gate) | `dotnet build src/BaseProcessor.Core -c Debug` | ✅ (Plan 01 inverted) | ⬜ pending |
| 57-03-02 | 03 | 2 | CFG-03/04/06/07 | T-57-05 / T-57-06 / T-57-07 | clash → MarkReady-only (no crash-loop), no queue bind, log carries no payload values | unit (harness) | `dotnet test --filter "FullyQualifiedName~SchemaResolutionFacts \| FullyQualifiedName~DispatchBindSequenceFacts"` | ✅ (Plan 01 inverted/extended) | ⬜ pending |
| 57-04-01 | 04 | 1 | CFG-10 | T-57-09 / T-57-10 | exception carries Guid only; handler fast-bails (no foreign claim) | unit (build gate) | `dotnet build src/BaseApi.Service -c Debug` | n/a (build) | ⬜ pending |
| 57-04-02 | 04 | 1 | CFG-10 | T-57-08 / T-57-09 | referenced Definition edit frozen (TOCTOU closed); 409 body safe-disclosure | integration (WAF) | `dotnet test --filter "FullyQualifiedName~SchemaDefinitionFreezeFacts"` | ✅ (Plan 01) | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

**Sampling continuity check:** No 3 consecutive tasks lack an automated verify. Plan 01 tasks create the seams (spike GREEN immediately; the rest compile/RED as a deliberate failing baseline). Plans 02/03/04 each turn a pre-existing automated test GREEN. ✅

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Processor/ConfigSchemaCoverageFacts.cs` — NEW. Table-driven pure-unit tests over the STJ rule table (CFG-05) + 4 `[ASSUMED]`-grounding `Deserialize` spike facts (A1 string-enum→CLR-enum, A2a number→int, A2b string→number, A3 null→non-nullable value-type) driving the REAL `ProcessorConfig.SerializerOptions`. **BLOCKING** — the spike verdicts lock the rule table Plan 02 implements.
- [ ] `tests/BaseApi.Tests/Features/Schema/SchemaDefinitionFreezeFacts.cs` — NEW. WebApplicationFactory (`Phase8WebAppFactory`) integration: frozen-Definition mutation → 409; Name/Desc edit on referenced schema → 200; unreferenced-draft Definition edit → 200. RECORDS the chosen TOCTOU mechanism (frozen-once-referenced) for ROADMAP SC-5.
- [ ] `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs` — MODIFY (invert). `:145` `Assert.DoesNotContain(configId, …)` → `Assert.Contains`; add `context.ConfigDefinition` assertion; rename fact (drop "Never_Config"). Covers CFG-03/04.
- [ ] `tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs` — MODIFY (extend). Add Gate A clash case (`log == ["ready"]`, `BoundQueueName is null`, `!IsHealthy`, one Error log) + null-skip case. Covers CFG-06/07.
- [ ] Framework install: **none** — all test deps already present (xUnit v3, NSubstitute, FakeTimeProvider, MassTransit ITestHarness, WebApplicationFactory + testcontainers).

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| (none) | — | — | — |

*All phase behaviors have automated verification. The end-to-end "config-incompatible processor blocked 422 at orchestration start" RealStack proof is **Phase 58** (CFG-08/09), deliberately out of this phase's scope.*

---

## Success-Criterion → Observable Signal Map (ROADMAP SC 1–5)

| Success criterion | Observable signal | Seam / Test |
|-------------------|-------------------|-------------|
| SC1 — fetch config def + store; missing def transient | config Id queried (>1× before Found via `NextIsNotFound`); `context.ConfigDefinition == "def-for-{configId:N}"` | `SchemaResolutionFacts.LoopB_Resolves_Input_Output_And_Config` |
| SC2 — Gate A covers check | both-present clash → `Covered=false`; schema-only/TConfig-only → `Covered=true`; recursive | `ConfigSchemaCoverageFacts` [Theory] + spikes |
| SC3 — clash → never Healthy (skp key absent) | ordered log `["ready"]` only; `BoundQueueName is null`; `!IsHealthy`; one Error log; terminal (no retry) → no `MarkHealthy` → heartbeat no-ops → no `skp:{id}` L2 key | `DispatchBindSequenceFacts.GateA_Clash_Withholds_MarkHealthy_And_Bind` |
| SC4 — null ConfigSchemaId → skip → Healthy | ordered log `["connect","ready","markhealthy"]`; bound; `IsHealthy` | `DispatchBindSequenceFacts.GateA_NullConfigSchemaId_Skips_And_Reaches_Healthy` |
| SC5 — TOCTOU closed, mechanism recorded | referenced-schema Definition edit → 409 + RFC-7807 + correlation echo; mechanism (frozen-once-referenced) documented in the fact | `SchemaDefinitionFreezeFacts` (all 3) |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (the 2 new files + 2 inversions)
- [x] No watch-mode flags
- [x] Feedback latency < 30s (quick slice)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved 2026-06-12
