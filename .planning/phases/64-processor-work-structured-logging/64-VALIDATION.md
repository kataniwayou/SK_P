---
phase: 64
slug: processor-work-structured-logging
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-14
---

# Phase 64 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (.NET) — hermetic facts |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~SampleProcessorFacts"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Estimated runtime** | ~30–60 seconds (hermetic, no external deps) |

---

## Sampling Rate

- **After every task commit:** Run the quick command (`SampleProcessorFacts` filter)
- **After every plan wave:** Run the full suite command
- **Before `/gsd-verify-work`:** Full suite green + `dotnet build -c Release` and `-c Debug` both 0-warning
- **Max feedback latency:** ~60 seconds

---

## Per-Task Verification Map

> Derived by the planner from RESEARCH.md → "## Validation Architecture". Each Success Criterion and PROC requirement maps to a concrete hermetic fact or build assertion.

| Success Criterion / Req | Validates | Test Type | Automated Command | Status |
|-------------------------|-----------|-----------|-------------------|--------|
| SC-1 / PROC-01 | `SampleConfig(int Number, string? Label)` deserializes from `{ "number": N, "label": "Step_*" }` via `ProcessorConfig.SerializerOptions` | unit | `dotnet test --filter "FullyQualifiedName~SampleProcessorFacts"` | ⬜ pending |
| SC-2 / PROC-02 | `ProcessAsync` sum = `Number + Random.Shared.Next(0,100)`; result in `[Number, Number+99]`; D-04 JSON `{ number, label }` string in `ProcessItem.Data` | unit | `dotnet test --filter "FullyQualifiedName~SampleProcessorFacts"` | ⬜ pending |
| SC-3 / PROC-03 | Exactly one `LogInformation` per execution with structured `{StepLabel}`=`config.Label` verbatim + `{Sum}`; ids come from ambient scope (NOT asserted here — D-09) | unit | `dotnet test --filter "FullyQualifiedName~SampleProcessorFacts"` | ⬜ pending |
| SC-4 | Solution builds 0-warning Release + Debug; hermetic suite green | build | `dotnet build -c Release` && `dotnet build -c Debug` && full suite | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] Extend `CapturingLogger.Log` (test harness) to capture `state` as `IReadOnlyList<KeyValuePair<string,object?>>` so facts can assert `{StepLabel}`/`{Sum}` values (per RESEARCH.md finding #3) — if not already present.

*Existing xUnit infrastructure (`tests/BaseApi.Tests`) covers all phase requirements otherwise.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| End-to-end ES aggregation by `correlationId` | PROC-03 | Requires the live stack + ES (Phase 65/66 concern) | Out of scope for this hermetic phase — ambient-scope id wiring is proven out-of-phase by existing consume-filter facts |

*Hermetic-suite behaviors all have automated verification.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
