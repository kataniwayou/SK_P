---
phase: 28
slug: sourcehash-identity-processor-sample-e2e-closeout
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-02
---

# Phase 28 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `28-RESEARCH.md` §Validation Architecture.

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

> Task IDs assigned during planning. Requirement → behavior → test-type mapping (from research) below; the planner/executor populate Task ID, Plan, Wave columns.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| TBD | TBD | TBD | IDENT-01 | — | Hash SHA-256 lowercase 64-hex, LF-normalized, ordinal-sorted, correct file scope | unit + build | `dotnet build src/Processor.Sample` then reflect `.dll`; assert `^[a-f0-9]{64}$` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | IDENT-01 | — | Excludes generated / BaseConsole.Core / Messaging.Contracts | unit | assert excluded file NOT in `@(ImplFiles)` (dump item list via target) | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | IDENT-02 | T-28 (forged hash) | `[assembly: AssemblyMetadata("SourceHash",…)]` on `Processor.Sample.dll` | unit (reflection) | reflect built assembly; assert attribute present + 64-hex | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | IDENT-02 | — | Incremental: edit impl `.cs` → hash changes; no change → attribute still present | build | scripted clean/edit/incremental build + reflect (Pitfall 2) | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | IDENT-02 | — | Cross-OS reproducibility (dev build == Docker build hash) | build/integration | dual-build script comparing embedded hashes (A4 — highest risk) | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | SAMPLE-01 | — | `Processor.Sample` boots, `ProcessAsync` returns 1 deterministic dummy | unit | xUnit on `SampleProcessor.ProcessAsync` returns single fixed `ProcessResult` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | SAMPLE-01 | — | Concrete carries no infra (only overrides ProcessAsync) | static/unit | assert `SampleProcessor : BaseProcessor`, no extra DI registrations | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | SAMPLE-02 | T-28 (compose drift) | compose has `processor-sample` service mirroring orchestrator | unit (file regex) | extend `ComposeYamlFacts` | ✅ extend `ComposeYamlFacts.cs` | ⬜ pending |
| TBD | TBD | TBD | TEST-01 | — | Live round-trip + truthful liveness-gated Start | E2E (RealStack) | `SampleRoundTripE2ETests` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | TEST-02 | — | 3-GREEN + triple-SHA BEFORE=AFTER incl. new keys | gate script | `scripts/phase-28-close.ps1` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `src/BaseProcessor.Core/SourceHash.targets` — inline RoslynCodeTaskFactory task + two targets (compute/emit) — IDENT-01/02
- [ ] `src/Processor.Sample/` project skeleton (csproj, Program.cs, SampleProcessor.cs, appsettings.json, Dockerfile) — SAMPLE-01/02
- [ ] `tests/BaseApi.Tests/Processor/SourceHashEmbedFacts.cs` — reflect built `Processor.Sample.dll`; attribute + 64-hex + incremental behavior — IDENT-02
- [ ] `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` — ProcessAsync returns 1 deterministic result — SAMPLE-01
- [ ] `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` — EXTEND for `processor-sample` — SAMPLE-02
- [ ] `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — live round-trip + truthful liveness gate — TEST-01
- [ ] `scripts/phase-28-close.ps1` — copy phase-22 + add `processor-sample` to `$services` + extend teardown for `skp:{id:D}` / `skp:data:*` — TEST-02
- [ ] Dual-build hash-reproducibility verification harness (Windows SDK vs Linux Docker build) — A4, highest-risk gap
- [ ] `SK_P.sln` — add `Processor.Sample` project
- [ ] `compose.yaml` — add `processor-sample` service

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Cross-OS hash reproducibility (dev build == container build) | IDENT-02 | Requires building on two OS targets (Windows SDK + Linux Docker) and diffing the embedded attribute; not expressible as a single in-process assertion | Wave 0: `dotnet build` on host, extract embedded `SourceHash`; `docker build` the Processor.Sample image, extract its embedded `SourceHash`; assert equal. Automate as a script if feasible. |

*All other phase behaviors have automated verification.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 30s (hermetic tier)
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
