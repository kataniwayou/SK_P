---
phase: 28
slug: sourcehash-identity-processor-sample-e2e-closeout
status: complete
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-02
updated: 2026-06-02
---

# Phase 28 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `28-RESEARCH.md` §Validation Architecture. Audited against the implemented
> codebase 2026-06-02 — all requirements COVERED by automated verification.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (`[Fact]`, `[Trait]`, `[Collection]`) + NSubstitute + MassTransit.Testing in-memory harness |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "Category!=RealStack"` (hermetic) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release` (RealStack live) |
| **Estimated runtime** | hermetic ~30s; RealStack live multi-minute (container round-trip) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test --filter "Category!=RealStack"` (hermetic unit + MSBuild-reflection facts; sub-30s)
- **After every plan wave:** Full hermetic suite + `dotnet build SK_P.sln -c Release` (zero-warning, `TreatWarningsAsErrors`)
- **Before `/gsd-verify-work`:** Full suite (incl. RealStack) green
- **Phase gate:** `scripts/phase-28-close.ps1` — 3-consecutive-GREEN full suite (RealStack live) + triple-SHA BEFORE=AFTER
- **Max feedback latency:** ~30 seconds (hermetic tier)

---

## Per-Task Verification Map

> Audited 2026-06-02 against the implemented codebase. All requirements COVERED.
> Evidence: full suite 395 facts GREEN ×3 consecutive (close gate exit 0); cross-OS reproducibility
> proven (host==docker `ab923430…`).

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| T1 | 28-01 | 1 | IDENT-01 | — | Hash SHA-256 lowercase 64-hex, LF-normalized, ordinal-sorted, correct file scope | unit + build | `SourceHashEmbedFacts` reflects built `.dll`; asserts `^[a-f0-9]{64}$` | ✅ `SourceHashEmbedFacts.cs` | ✅ green |
| T2 | 28-01 | 1 | IDENT-01 | — | Excludes generated / BaseConsole.Core / Messaging.Contracts | unit | `SourceHashEmbedFacts` asserts excluded file NOT in `@(ImplFiles)` (DumpImplFiles target) | ✅ `SourceHashEmbedFacts.cs` | ✅ green |
| T3 | 28-01 | 1 | IDENT-02 | T-28-09 (forged hash) | `[assembly: AssemblyMetadata("SourceHash",…)]` on `Processor.Sample.dll` | unit (reflection) | `SourceHashEmbedFacts` reflects built assembly; asserts attribute present + 64-hex | ✅ `SourceHashEmbedFacts.cs` | ✅ green |
| T4 | 28-02 | 2 | IDENT-02 | T-28-05 | Cross-OS reproducibility (dev build == Docker build hash) | build/integration (script) | `scripts/verify-sourcehash-reproducible.ps1` — dual-build + byte-equality (PROVEN host==docker `ab923430…`) | ✅ `verify-sourcehash-reproducible.ps1` | ✅ green |
| T5 | 28-01 | 1 | SAMPLE-01 | — | `Processor.Sample` boots, `ProcessAsync` returns 1 deterministic dummy | unit | `SampleProcessorFacts` invokes `ProcessAsync` → `Assert.Single` + `"processor-sample-ok"` | ✅ `SampleProcessorFacts.cs` | ✅ green |
| T6 | 28-01 | 1 | SAMPLE-01 | — | Concrete carries no infra (only overrides ProcessAsync) | static/unit | `SampleProcessor : BaseProcessorBase`, `Program.cs` 3-line wiring only | ✅ `SampleProcessorFacts.cs` | ✅ green |
| T7 | 28-02 | 2 | SAMPLE-02 | T-28-06 (compose drift) | compose has `processor-sample` service mirroring orchestrator | unit (file regex) | `ComposeYamlFacts` (+3 facts) | ✅ `ComposeYamlFacts.cs` | ✅ green |
| T8 | 28-03 | 3 | TEST-01 | T-28-08/09/10 | Live round-trip + truthful liveness-gated Start | E2E (RealStack) | `SampleRoundTripE2ETests` (live 1/1, 44.9s) | ✅ `SampleRoundTripE2ETests.cs` | ✅ green |
| T9 | 28-04 | 4 | TEST-02 | T-28-11/12/13 | 3-GREEN + triple-SHA BEFORE=AFTER incl. new keys | gate script | `scripts/phase-28-close.ps1` (exit 0: 395×3 + triple-SHA held) | ✅ `phase-28-close.ps1` | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [x] `src/BaseProcessor.Core/SourceHash.targets` — inline RoslynCodeTaskFactory task + two targets (compute/emit) — IDENT-01/02
- [x] `src/Processor.Sample/` project skeleton (csproj, Program.cs, SampleProcessor.cs, appsettings.json, Dockerfile) — SAMPLE-01/02
- [x] `tests/BaseApi.Tests/Processor/SourceHashEmbedFacts.cs` — reflect built `Processor.Sample.dll`; attribute + 64-hex + exclusion — IDENT-01/02
- [x] `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` — ProcessAsync returns 1 deterministic result — SAMPLE-01
- [x] `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` — EXTENDED for `processor-sample` (+3 facts) — SAMPLE-02
- [x] `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — live round-trip + truthful liveness gate — TEST-01
- [x] `scripts/phase-28-close.ps1` — phase-22 analog + `processor-sample` in `$services` + steady-state `skp:{id:D}` / `skp:data:*` teardown — TEST-02
- [x] `scripts/verify-sourcehash-reproducible.ps1` — dual-build hash-reproducibility harness (Windows SDK vs Linux Docker), A4 highest-risk gap — IDENT-02 (PROVEN host==docker)
- [x] `SK_P.sln` — `Processor.Sample` project added
- [x] `compose.yaml` — `processor-sample` service added

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| *(none)* | — | — | Cross-OS hash reproducibility (originally manual-only) was AUTOMATED in Plan 02 as `scripts/verify-sourcehash-reproducible.ps1` (dual host-SDK + Linux-Docker build, byte-equality assertion) and proven (host==docker `ab923430…`). No remaining manual-only behaviors. |

*All phase behaviors have automated verification.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 30s (hermetic tier)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved — 2026-06-02 (9/9 requirements automated; 0 manual-only; 0 gaps)

---

## Validation Audit 2026-06-02

| Metric | Count |
|--------|-------|
| Gaps found | 0 |
| Resolved | 0 |
| Escalated | 0 |
| Requirements COVERED | 6/6 (IDENT-01, IDENT-02, SAMPLE-01, SAMPLE-02, TEST-01, TEST-02) |

State-A audit of the planning draft against the implemented codebase. Every requirement maps to an
existing, green automated test/artifact (see Per-Task Map). The cross-OS reproducibility behavior the
draft listed as manual-only was automated in Plan 02 and proven. Full suite 395 facts GREEN ×3 consecutive
(`phase-28-close.ps1` exit 0). No tests needed to be generated. Phase 28 is Nyquist-compliant.
