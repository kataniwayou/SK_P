---
phase: 56
slug: typed-base-config-seam
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-12
validated: 2026-06-12
---

# Phase 56 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 on Microsoft.Testing.Platform (MTP) — hermetic test suite |
| **Config file** | none — existing `tests/BaseApi.Tests` project |
| **Quick run command** | MTP runner, native executable: `…/BaseApi.Tests.exe --filter-method "*SampleProcessorFacts*" --report-xunit-trx` |
| **Full suite command** | `dotnet build SK_P.sln` (0-warning gate) then MTP runner `--filter-not-trait Category=RealStack --report-xunit-trx`, parse TRX outcome |
| **Estimated runtime** | full hermetic suite ~3m11s (530 facts); quick slice ~60s |

> ⚠ **Tooling note (executor-confirmed):** under xUnit v3 + MTP, `dotnet test --filter` is silently ignored (`MTP0001`) and the MSBuild integration suppresses the console summary. Validate via the **native MTP runner** with `--report-xunit-trx` and parse the TRX `passed`/`failed` counts — do **not** rely on `dotnet test --filter` console output. `Category=RealStack` requires live infra and is excluded from the hermetic gate.

---

## Sampling Rate

- **After every task commit:** Run quick run command (affected facts)
- **After every plan wave:** Run full suite command
- **Before `/gsd-verify-work`:** Full suite must be green + `dotnet build -c Release` 0-warning
- **Max feedback latency:** 90 seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Test Fact (file:line) | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-----------------------|--------|
| 56-01 | 01 | 1 | CFG-01 | — | typed config deserialized and passed to the author seam (`BaseProcessor<TConfig>.ProcessAsync`) — `Processor.Sample` round-trips | unit | `SampleProcessorFacts` typed-seam facts (`SampleProcessorFacts.cs`, 3 facts) | ✅ green |
| 56-01 | 01 | 1 | CFG-02 | T-56-02 | empty/whitespace payload → `null` config before deserialize (D-04 guard) | unit | `ProcessAsync_Null_Config_Falls_Back_To_Fixed_Token` (`SampleProcessorFacts.cs:75`) | ✅ green |
| 56-01 | 01 | 1 | CFG-01 | — | old raw-string `ProcessAsync(string,string)` seam removed (clean break) | unit + scan | `BaseProcessorSeamFacts` (`BaseProcessorSeamFacts.cs`) + 0 `src` matches | ✅ green |
| 56-02 | 02 | 2 | CFG-02 | T-56-01 | malformed payload → uncaught `JsonException` → pipeline catch-all → **exactly one** `StepFailed`, no Keeper send (Req 4a — uncovered before this phase) | unit | `MalformedPayload_DeserFailure_Emits_Single_StepFailed` (`PipelineInFacts.cs:47`) | ✅ green |
| 56-02 | 02 | 2 | CFG-01, CFG-02 | — | 0-warning dual-config build + green hermetic suite (close gate) | suite | `dotnet build SK_P.sln` (Release+Debug, 0 warn) + MTP full suite **530/530** | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

> Every observable phase behavior (deser-success / deser-failure→StepFailed / empty→null config / sample round-trip / 0-warning build) maps to an automated fact that ran green in the close gate (TRX: passed=530, failed=0).

---

## Wave 0 Requirements

- [x] New fact: deser-failure (malformed payload) through a real `BaseProcessor<TConfig>` → exactly one `StepFailed` (Req 4a) — **delivered** as `MalformedPayload_DeserFailure_Emits_Single_StepFailed` (`PipelineInFacts.cs:47`)
- [x] Updated `SampleProcessorFacts` — typed-seam signature + payload object shape — **delivered** (3 facts incl. typed round-trip + null-config fallback)

*Both Wave 0 coverage items closed during execution; no MISSING references remain.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| — | — | — | All phase behaviors have automated verification. |

*All phase behaviors have automated verification.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 90s (quick slice) — full close gate ~3m11s by design
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** validated 2026-06-12 — all CFG-01/CFG-02 behaviors automated and green.

---

## Validation Audit 2026-06-12

| Metric | Count |
|--------|-------|
| Requirements audited | 2 (CFG-01, CFG-02) |
| Behaviors mapped | 5 |
| COVERED (green) | 5 |
| PARTIAL | 0 |
| MISSING | 0 |
| Gaps found | 0 |
| Resolved | 0 (no gaps — pre-execution Wave 0 items delivered in-phase) |
| Escalated | 0 |

Audited in State A against the executed phase. No `gsd-nyquist-auditor` spawn required — zero gaps. Full hermetic suite green (530/530, TRX `outcome=Completed`); 0-warning Release+Debug build.
