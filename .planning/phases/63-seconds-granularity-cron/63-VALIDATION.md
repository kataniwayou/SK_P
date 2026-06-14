---
phase: 63
slug: seconds-granularity-cron
status: verified
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-14
updated: 2026-06-14
---

# Phase 63 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `63-RESEARCH.md` § Validation Architecture (SC#1–4 → falsifiable assertions).
> **Audited post-execution 2026-06-14 — all requirements COVERED by green automated tests.**

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 on Microsoft.Testing.Platform (`[Fact]`/`[Theory]`/`[InlineData]`) |
| **Config file** | `tests/BaseApi.Tests/xunit.runner.json` |
| **Quick run command** | `./tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-class "BaseApi.Tests.Contracts.CronFieldFormTests" --filter-class "BaseApi.Tests.Orchestrator.CronIntervalTests" --filter-class "BaseApi.Tests.Validation.WorkflowCronValidatorTests"` |
| **Full suite command** | `dotnet build SK_P.sln -c Debug` then run `BaseApi.Tests.exe` |
| **0-warning build (SC#4)** | `dotnet build SK_P.sln -c Release` AND `-c Debug` (TreatWarningsAsErrors inherited from Directory.Build.props) |
| **Estimated runtime** | ~0.3–1.5s (3 cron classes, 21 tests) |

> **Note (mechanics):** xUnit v3 runs on Microsoft.Testing.Platform here — the VSTest `dotnet test --filter "FullyQualifiedName~..."` syntax shown in the original strategy is **silently ignored**. Use the native MTP `--filter-class` on the built test executable (above) for filtered runs.

---

## Sampling Rate

- **After every task commit:** Run `{quick run command}` (cron-scoped `--filter-class`, < 2s)
- **After every plan wave:** Run the full `BaseApi.Tests.exe`
- **Before `/gsd-verify-work`:** Full suite green AND Release + Debug build 0-warning
- **Max feedback latency:** ~2 seconds (filtered) / seconds (full)

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command (MTP) | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------------|-------------|--------|
| detector | 63-01 | 1 | CRON-01/02 (D-03/D-10) | T-63-01 | Input field-count gate; no parser in contracts leaf | unit | `BaseApi.Tests.exe --filter-class "...CronFieldFormTests"` | ✅ | ✅ green (8/8) |
| cron-interval | 63-02 | 2 | CRON-01 (D-04/D-08) | T-63-03 | UTC-only Cronos input preserved (Kind=Utc) | unit | `BaseApi.Tests.exe --filter-class "...CronIntervalTests"` | ✅ | ✅ green (5/5) |
| validators | 63-03 | 2 | CRON-02 (D-04/D-09/D-11) | T-63-05/07 | Malformed/wrong field-count still rejected (422/400) | unit | `BaseApi.Tests.exe --filter-class "...WorkflowCronValidatorTests"` | ✅ | ✅ green (8/8) |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Success Criterion → Test Map (authoritative)

| SC | Behavior | Test Type | Exact Assertion | Test File | Exists? |
|----|----------|-----------|-----------------|-----------|---------|
| SC#1 | Sub-minute interval math (UTC) | unit | `CronInterval.IntervalSeconds("*/30 * * * * *", pinnedUtc) == 30` | `tests/BaseApi.Tests/Orchestrator/CronIntervalTests.cs` | ✅ green |
| SC#1 | Next-occurrence strictly-future + `Kind=Utc` | unit | `n = NextOccurrence("*/30 * * * * *", nowUtc); NotNull(n); Equal(Utc, n.Value.Kind); True(n.Value > nowUtc)` | same file | ✅ green |
| SC#1 (regression) | 5-field interval still correct | unit | existing `IntervalSeconds("*/5 * * * *", ...) == 300` | same file | ✅ retained |
| SC#2 | 6-field accepted by Create validator | unit | `new WorkflowCreateDtoValidator().Validate(dto{Cron="*/30 * * * * *"}).IsValid == true` | `tests/BaseApi.Tests/Validation/WorkflowCronValidatorTests.cs` | ✅ green |
| SC#2 | 6-field accepted by Update validator | unit | same assertion against `WorkflowUpdateDtoValidator` | same file | ✅ green |
| SC#3 | 5-field still accepted (both validators) | unit | `Validate({Cron="0 0 * * *"}).IsValid == true` for Create + Update | same file | ✅ green |
| SC#2/3 (negative) | Malformed / wrong field-count rejected | unit | `Validate({Cron="not a cron"}).IsValid == false`; `"* * *"` (4-token) `.IsValid == false` | same file | ✅ green |
| D-10 | Detector rule pinned | unit | `IsSecondsForm("*/30 * * * * *")==true`; `IsSecondsForm("0 0 * * *")==false`; `IsValidFieldCount("* * *")==false`; whitespace 6-token → seconds | `tests/BaseApi.Tests/Contracts/CronFieldFormTests.cs` | ✅ green |
| SC#4 | 0-warning build + green hermetic suite | build + suite | `dotnet build SK_P.sln -c Debug` exit 0 (0 warnings); 21/21 cron tests green | — | ✅ verified |

---

## Wave 0 Requirements

- [x] `src/Messaging.Contracts/CronFieldForm.cs` — the shared detector (D-03). Net-new production file, pure token-count, NO Cronos. Modeled on `Messaging.Contracts/Projections/L2ProjectionKeys.cs`.
- [x] `tests/BaseApi.Tests/Contracts/CronFieldFormTests.cs` — detector unit test (D-10). Modeled on `L2ProjectionKeysTests.cs`. 8/8 green.
- [x] `tests/BaseApi.Tests/Validation/WorkflowCronValidatorTests.cs` — validator unit tests, Create + Update (D-09). 8/8 green.
- [x] Extend `tests/BaseApi.Tests/Orchestrator/CronIntervalTests.cs` — `*/30` case (D-08). 5/5 green.
- [x] No framework install needed — xunit.v3 + FakeTimeProvider already present.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| — | — | — | — |

*All phase behaviors have automated verification. The "~10 triggers over a 5-minute window" observation in SC#1 is the milestone-level E2E proof (Phases 65–68); this phase proves the underlying sub-minute math hermetically via the `== 30` unit fact (green).*

---

## Validation Audit 2026-06-14

| Metric | Count |
|--------|-------|
| Requirements audited | 2 (CRON-01, CRON-02) |
| Tasks mapped | 3 (detector, cron-interval, validators) |
| Gaps found | 0 |
| Resolved | 0 (all COVERED pre-audit) |
| Escalated (manual-only) | 0 |

All three task groups COVERED by green automated unit tests (21/21 passing, re-run against committed code). No MISSING or PARTIAL requirements; no gap-fill auditor run required. SC#4 0-warning build confirmed via full-solution `dotnet build SK_P.sln -c Debug` (0 warnings). Phase is Nyquist-compliant.

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 30s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** verified 2026-06-14
