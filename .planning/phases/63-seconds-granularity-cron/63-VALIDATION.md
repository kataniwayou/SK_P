---
phase: 63
slug: seconds-granularity-cron
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-14
---

# Phase 63 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `63-RESEARCH.md` § Validation Architecture (SC#1–4 → falsifiable assertions).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 (`[Fact]`/`[Theory]`/`[InlineData]`) |
| **Config file** | `tests/BaseApi.Tests/xunit.runner.json` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~CronInterval|FullyQualifiedName~CronFieldForm|FullyQualifiedName~WorkflowCron"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **0-warning build (SC#4)** | `dotnet build -c Release` AND `dotnet build -c Debug` (TreatWarningsAsErrors inherited from Directory.Build.props — build fails on any warning) |
| **Estimated runtime** | ~30 seconds (quick) / suite-dependent (full) |

---

## Sampling Rate

- **After every task commit:** Run `{quick run command}` (cron-scoped filter, < 30s)
- **After every plan wave:** Run `{full suite command}`
- **Before `/gsd-verify-work`:** Full suite green AND Release + Debug build 0-warning
- **Max feedback latency:** ~30 seconds (quick filter)

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| detector | — | 1 | CRON-01/02 (D-03/D-10) | — | Input field-count gate; no parser in contracts leaf | unit | `dotnet test --filter "FullyQualifiedName~CronFieldForm"` | ❌ W0 | ⬜ pending |
| cron-interval | — | 2 | CRON-01 (D-04/D-08) | — | UTC-only Cronos input preserved (Kind=Utc) | unit | `dotnet test --filter "FullyQualifiedName~CronInterval"` | ✅ (extend) | ⬜ pending |
| validators | — | 2 | CRON-02 (D-04/D-09/D-11) | T-V5 / — | Malformed/wrong field-count still rejected (422/400) | unit | `dotnet test --filter "FullyQualifiedName~WorkflowCron"` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*
*Wave numbers are indicative — final wave assignment is set by the planner in PLAN.md (detector is the Wave 0 / first-wave dependency of both call sites).*

---

## Success Criterion → Test Map (authoritative)

| SC | Behavior | Test Type | Exact Assertion | Test File | Exists? |
|----|----------|-----------|-----------------|-----------|---------|
| SC#1 | Sub-minute interval math (UTC) | unit (FakeTimeProvider) | `CronInterval.IntervalSeconds("*/30 * * * * *", pinnedUtc) == 30` | `tests/BaseApi.Tests/Orchestrator/CronIntervalTests.cs` (extend, D-08) | ❌ W0 |
| SC#1 | Next-occurrence strictly-future + `Kind=Utc` | unit | `n = NextOccurrence("*/30 * * * * *", nowUtc); NotNull(n); Equal(Utc, n.Value.Kind); True(n.Value > nowUtc)` | same file | ❌ W0 |
| SC#1 (regression) | 5-field interval still correct | unit | existing `IntervalSeconds("*/5 * * * *", ...) == 300` | same file | ✅ retain |
| SC#2 | 6-field accepted by Create validator | unit | `new WorkflowCreateDtoValidator().Validate(dto{Cron="*/30 * * * * *"}).IsValid == true` | `tests/BaseApi.Tests/Validation/WorkflowCronValidatorTests.cs` (new, D-09) | ❌ W0 |
| SC#2 | 6-field accepted by Update validator | unit | same assertion against `WorkflowUpdateDtoValidator` | same file | ❌ W0 |
| SC#3 | 5-field still accepted (both validators) | unit | `Validate({Cron="0 0 * * *"}).IsValid == true` for Create + Update | same file | ❌ W0 |
| SC#2/3 (negative) | Malformed / wrong field-count rejected | unit | `Validate({Cron="not a cron"}).IsValid == false`; `"* * *"` (4-token) `.IsValid == false` | same file | ❌ W0 |
| D-10 | Detector rule pinned | unit | `IsSecondsForm("*/30 * * * * *")==true`; `IsSecondsForm("0 0 * * *")==false`; `IsValidFieldCount("* * *")==false`; whitespace `" */30   *  * * * "` → seconds | `tests/BaseApi.Tests/Contracts/CronFieldFormTests.cs` (new, model on `L2ProjectionKeysTests.cs`) | ❌ W0 |
| SC#4 | 0-warning build + green hermetic suite | build + suite | `dotnet build -c Release` exit 0; `dotnet build -c Debug` exit 0; full `dotnet test` green | — | gate |

---

## Wave 0 Requirements

- [ ] `src/Messaging.Contracts/CronFieldForm.cs` — the shared detector (D-03). Net-new production file, pure token-count, NO Cronos. Model on `Messaging.Contracts/Projections/L2ProjectionKeys.cs`.
- [ ] `tests/BaseApi.Tests/Contracts/CronFieldFormTests.cs` — detector unit test (D-10). Model on `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs`.
- [ ] `tests/BaseApi.Tests/Validation/WorkflowCronValidatorTests.cs` — validator unit tests, Create + Update (D-09). No validator cron test exists today.
- [ ] Extend `tests/BaseApi.Tests/Orchestrator/CronIntervalTests.cs` — `*/30` case (D-08).
- [ ] No framework install needed — xunit.v3 + FakeTimeProvider already present.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| — | — | — | — |

*All phase behaviors have automated verification. The "~10 triggers over a 5-minute window" observation in SC#1 is the milestone-level E2E proof (Phases 65–68); this phase proves the underlying sub-minute math hermetically via the `== 30` unit fact.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 30s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
