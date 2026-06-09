---
phase: 47
slug: dlq-consolidation-at-least-once-semantics
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-09
---

# Phase 47 — Validation Strategy

> Per-phase validation contract. This is a **verification phase** — the validation architecture IS the coverage map (existing-test-that-proves-it vs new gap-fill assertion per requirement/SC).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 + NSubstitute 5.3.0 + MassTransit.Testing 8.5.5 (InMemoryTestHarness) |
| **Config file** | none — Microsoft.Testing.Platform (`dotnet run` entrypoint in `tests/BaseApi.Tests`) |
| **Runner** | `dotnet test --filter` is IGNORED; scope via `dotnet run --project tests/BaseApi.Tests -- --filter-trait`/`--filter-method` |
| **Quick run command** | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=47"` |
| **Full suite command** | `dotnet run --project tests/BaseApi.Tests -c Debug` (2 pre-existing broker-dependent E2E failures are NOT Phase-47 regressions) |
| **Estimated runtime** | ~30–60 seconds (hermetic unit + in-memory harness) |

---

## Sampling Rate

- **After every task commit:** `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=47"`
- **After every plan wave:** full hermetic suite (excluding the 2 known broker-dependent E2E failures)
- **Phase gate:** `dotnet build SK_P.sln` 0/0 + all Phase-47 facts green + every `47-DLQ-AUDIT.md` row resolves to a real green test
- **Max feedback latency:** ~60 seconds

*Live / real-stack DLQ + at-least-once proof is Phase 49 (TEST-01..03) — out of scope here.*

---

## Per-Task Verification Map (Coverage Map)

| Req / SC | Behavior | Test Type | Test | Automated Command | Status |
|----------|----------|-----------|------|-------------------|--------|
| R1 / SC-1 (generic) | exhaustion → `skp-dlq-1` as `ConsolidatedFault` | harness | `KeeperDlqConsolidationTests.Dlq1_Consolidated` (exists) | `... --filter-method "*Dlq1_Consolidated*"` | ✅ already proven |
| R1 / SC-1 (processor) | processor send-exhaustion → `skp-dlq-1` | harness | sibling fact in `KeeperDlqConsolidationTests` (NEW) | `... --filter-method "*ProcessorSendExhaustion*"` | ❌ W0 |
| R1 / SC-1 (structural) | no v4 path references `keeper-dlq` | source-scan | `AtLeastOnceStructuralFacts` (NEW) | `... --filter-trait "Phase=47"` | ❌ W0 |
| R2 / SC-3 | data-gone REINJECT → `skp-dlq-1`, not loop | harness | `RecoveryDeadLetterFacts.DataGone_reinject_faults_and_routes_to_dead_letter` (exists; re-tag Phase 47) | `... --filter-method "*DataGone_reinject*"` | ✅ already proven |
| R3 / SC-2 (StepCompleted) | same message twice → effect twice, no throw, no lost branch | unit | extend `TypedResultConsumerFacts` (NEW) | `... --filter-method "*Duplicate*"` | ❌ W0 |
| R3 / SC-2 (EntryStepDispatch) | same dispatch twice → processing twice | unit/harness | extend `TypedResultConsumerFacts`/recovery kit (NEW) | `... --filter-method "*Duplicate*"` | ❌ W0 |
| R4 / SC-2 | no `MessageIdentity`/dedup member on exec-path assemblies | reflection | `AtLeastOnceStructuralFacts` (NEW) | `... --filter-trait "Phase=47"` | ❌ W0 |
| R5 / SC-1,2,3 | audit doc maps every row to a green test | doc (verifier-checked) | `47-DLQ-AUDIT.md` (NEW) | human + verifier review | ❌ W0 |

*Status: ✅ already proven (cite in audit) · ❌ W0 (new gap-fill) · the planner assigns Task IDs.*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/.../AtLeastOnceStructuralFacts.cs` — **(R4)** reflection no-dedup guard (no `MessageIdentity` type / no dedup member on Orchestrator + BaseProcessor.Core assemblies) **+ (R1)** source-scan no-`keeper-dlq` guard over `src/BaseProcessor.Core/Processing/` + `src/Keeper/Recovery/`. `[Trait("Phase","47")]`.
  - **LANDMINE 1:** the source-scan MUST exclude `KeeperRecoveryHandler.cs` (it lives in `src/Keeper/Recovery/` and is the legitimate dormant `keeper-dlq` sender until Phase 48).
  - **LANDMINE 2:** the no-dedup guard MUST use **reflection** (type/member absence), NOT a string-scan — `PauseWorkflow`/`ResumeWorkflow` carry a positional `string H` (BIT-gate pause key, not dedup) and retired-context comments contain `flag[H]`; a string-scan false-positives.
- [ ] Sibling processor-send-exhaustion fact in `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` — **(R1)** (or, if `Dlq1_Consolidated` already covers it generically, an audit reference instead of a new test).
- [ ] Duplicate-delivery fact(s) in `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs` — **(R3)** ONE dispatcher, double-`Consume`, assert effect `Count == 2` (distinct from the existing two-dispatcher indistinguishability test).
- [ ] Re-tag `RecoveryDeadLetterFacts.DataGone_reinject_faults_and_routes_to_dead_letter` with `[Trait("Phase","47")]` — **(R2)** (alongside the existing Phase-46 trait).
- [ ] `47-DLQ-AUDIT.md` — **(R5)** maps every Req/SC row to its proving test (file:method).
- *Framework install:* none — all infra present.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live message actually lands in broker `skp-dlq-1` (TTL applied) | RESIL-02 | RabbitMQ-transport behavior (x-message-ttl) — in-memory harness can't exercise it | Deferred to Phase 49 close gate (TEST-01..03); hermetic tests prove the consolidated route + const at config level |

*All other phase behaviors have hermetic automated verification.*

---

## Validation Sign-Off

- [ ] Every Req/SC has an automated verify (existing-proven cited OR new gap-fill) or is the audit-doc itself
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all gap-fill references; both landmines honored
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
