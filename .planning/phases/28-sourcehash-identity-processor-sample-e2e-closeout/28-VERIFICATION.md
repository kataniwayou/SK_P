---
phase: 28-sourcehash-identity-processor-sample-e2e-closeout
verified: 2026-06-02T00:00:00Z
status: passed
score: 6/6 must-haves verified
overrides_applied: 0
---

# Phase 28: SourceHash Identity + Processor.Sample + E2E Closeout — Verification Report

**Phase Goal:** The deterministic build-time SourceHash identity is embedded into the assembly, the first concrete `Processor.Sample` exists and joins the compose stack, and a real-stack E2E proves the live orchestrator→Processor.Sample→orchestrator round-trip and the liveness-gated Start — all behind the 3-GREEN / triple-SHA close gate.
**Verified:** 2026-06-02
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | IDENT-01: SourceHash.targets embeds `[assembly: AssemblyMetadata("SourceHash", <64-hex>)]` via a two-target compute/emit split (no dropped attribute on incremental builds) | VERIFIED | `src/BaseProcessor.Core/SourceHash.targets` exists; `ComputeSourceHash` target (incremental, Inputs/Outputs stamp) + `EmitSourceHashAttribute` target (always-run, reads stamp back) both hook `BeforeTargets="CoreGenerateAssemblyInfo;CoreCompile"`; `sourcehash.stamp` used in both WriteLinesToFile and ReadLinesFromFile |
| 2 | IDENT-02: The hash is lowercase 64-hex, computed by ordinal path sort + LF-normalized content, and is reproducible cross-OS (host == Docker) | VERIFIED | Algorithm confirmed in task inline: `Replace('\\','/')`, ordinal sort, `Replace("\r\n","\n").Replace("\r","\n")`, per-file SHA-256, final SHA-256 → `b.ToString("x2")`; `scripts/verify-sourcehash-reproducible.ps1` ran and exited 0: host `ab923430…3219a8` == docker `ab923430…3219a8` (byte-identical) |
| 3 | SAMPLE-01: `Processor.Sample` is a thin concrete — `SampleProcessor` overrides only `ProcessAsync` returning one deterministic `ProcessResult`; `Program.cs` is `AddBaseProcessor` + one registration; no infra code | VERIFIED | `SampleProcessor.cs`: `public sealed class SampleProcessor : BaseProcessorBase`, single `new ProcessResult("processor-sample-ok")`; `Program.cs`: `AddBaseConsoleObservability` + `AddBaseProcessor` + `AddSingleton<BaseProcessorBase, SampleProcessor>()`, no direct `AddBaseConsole`/`AddBaseConsoleMessaging` calls; `SourceHashEmbedFacts` + `SampleProcessorFacts` hermetic facts both GREEN |
| 4 | SAMPLE-02: `Processor.Sample` ships a multistage Dockerfile and joins the compose stack mirroring the Orchestrator tier (with `baseapi-service` healthy depends_on + short `ExecutionDataTtl`) | VERIFIED | `src/Processor.Sample/Dockerfile` exists: `sdk:8.0-bookworm-slim` build stage → `aspnet:8.0-bookworm-slim` runtime, wget installed before `USER app`, `EXPOSE 8082`, `ENTRYPOINT ["dotnet", "Processor.Sample.dll"]`; `compose.yaml` has `processor-sample` service with `container_name: sk-processor-sample`, `depends_on: baseapi-service: condition: service_healthy`, `Processor__ExecutionDataTtl: "5"` |
| 5 | TEST-01: A real-stack E2E proves the live orchestrator→Processor.Sample→orchestrator round-trip and the truthful liveness-gated Start | VERIFIED | `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` exists; reflects genuine `AssemblyMetadataAttribute` (no `RandomSha256Hex`/`SHA256`/`SeedHostProcessorLive`); polls `L2ProjectionKeys.Processor(procId)` for real heartbeat; drives `POST /api/v1/orchestration/start` asserting `204 NoContent`; asserts `skp:data:*` output key + ES seam log; net-zero teardown; SUMMARY records live run: Passed 1 / Failed 0 (44.9s) |
| 6 | TEST-02: The phase-close gate holds the 3-GREEN / triple-SHA BEFORE==AFTER discipline, with `processor-sample` added to the pre-flight health list | VERIFIED | `scripts/phase-28-close.ps1` exists; header "Phase 28 close gate — v3.5.0"; `processor-sample` in `$services`; all three SHA captures present (`psql -U postgres -lqt`, `redis-cli --scan`, `rabbitmqctl -q list_queues`); no `FLUSHDB`; idempotent GET-or-create seed resolves chicken-and-egg; SUMMARY records gate exit 0: 395 facts GREEN x3, triple-SHA held (`b48ce783…`, `56e9e516…`, `67a92f45…`) |

**Score:** 6/6 truths verified

---

## Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseProcessor.Core/SourceHash.targets` | Inline RoslynCodeTaskFactory task + two-target compute/emit | VERIFIED | 131 lines; `RoslynCodeTaskFactory`, ordinal sort, LF-normalize, `AssemblyMetadataAttribute`, `sourcehash.stamp` WriteLinesToFile + ReadLinesFromFile |
| `src/Processor.Sample/Processor.Sample.csproj` | Worker console project importing `SourceHash.targets` | VERIFIED | `<Import Project="..\BaseProcessor.Core\SourceHash.targets" />`, `MassTransit.RabbitMQ`, `appsettings.json` Content copy, no `Version=` on PackageReferences |
| `src/Processor.Sample/SampleProcessor.cs` | Single concrete `: BaseProcessor` overriding `ProcessAsync` | VERIFIED | `public sealed class SampleProcessor : BaseProcessorBase`, one `new ProcessResult("processor-sample-ok")`, no infra code |
| `src/Processor.Sample/Program.cs` | Thin Generic-Host shell with `AddBaseProcessor` | VERIFIED | `AddBaseConsoleObservability` + `AddBaseProcessor` + `AddSingleton<BaseProcessorBase, SampleProcessor>()`, no direct base extension calls |
| `src/Processor.Sample/appsettings.json` | Service config with name `processor-sample`, port 8082, Processor section | VERIFIED | `"Name": "processor-sample"`, `"Version": "3.5.0"`, `"Port": 8082`, `Processor` section with all five keys |
| `src/Processor.Sample/Dockerfile` | Multistage net8.0 sdk→aspnet image, port 8082, wget | VERIFIED | `sdk:8.0-bookworm-slim` build, `aspnet:8.0-bookworm-slim` runtime, wget installed before `USER app`, `EXPOSE 8082`, `ENTRYPOINT ["dotnet", "Processor.Sample.dll"]` |
| `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` | Unit fact: single deterministic ProcessResult | VERIFIED | Two `[Fact]`s, reflection-invoked `ProcessAsync`, `Assert.Single(result)`, `Assert.Equal("processor-sample-ok", only.OutputData)` |
| `tests/BaseApi.Tests/Processor/SourceHashEmbedFacts.cs` | Reflection fact: 64-hex embed + exclusion (no recompute) | VERIFIED | Reflects `AssemblyMetadataAttribute` off `typeof(SampleProcessor).Assembly`; `Assert.Matches(new Regex("^[a-f0-9]{64}$"), hash)`; exclusion fact via `DumpImplFiles` target; `DoesNotContain("/BaseConsole.Core/", ...)`, `DoesNotContain("/Messaging.Contracts/", ...)`; no `SHA256`/`ComputeHash` |
| `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` | Real-stack E2E proving round-trip + truthful liveness gate | VERIFIED | `[Trait("Category","RealStack")]`, genuine hash reflected, no synthetic seed, polls `L2ProjectionKeys.Processor`, asserts `skp:data:*` + ES seam log, net-zero teardown |
| `scripts/phase-28-close.ps1` | Triple-SHA / 3-GREEN close gate with `processor-sample` | VERIFIED | Header "Phase 28 close gate — v3.5.0", `processor-sample` in `$services`, three SHA captures, idempotent seed, no FLUSHDB, no stale "Phase 22" label (the one match is a comment reference to the analog script name) |
| `scripts/verify-sourcehash-reproducible.ps1` | Dual-build (host SDK vs Linux Docker) embedded-hash equality check | VERIFIED | Builds host publish, builds Docker image, extracts dll via `docker create`+`docker cp`, reflects `AssemblyMetadataAttribute` via PE reader, asserts byte-equality; ran and exited 0 |
| `compose.yaml` (modified) | `processor-sample` service joining the host stack | VERIFIED | `container_name: sk-processor-sample`, `depends_on: baseapi-service: condition: service_healthy`, `Processor__ExecutionDataTtl: "5"`, healthcheck `wget --spider -q http://localhost:8082/health/ready` |
| `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` (modified) | +3 compose-guard facts for processor-sample | VERIFIED | `ComposeYaml_Has_ProcessorSample_Service_Block`, `ComposeYaml_ProcessorSample_DependsOn_BaseApi_Healthy`, `ComposeYaml_ProcessorSample_Sets_Short_ExecutionDataTtl` — all using existing `ComposeYamlContent()` helper + `Assert.Matches(new Regex(...))` idiom |

---

## Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `Processor.Sample.csproj` | `SourceHash.targets` | `<Import Project="..\BaseProcessor.Core\SourceHash.targets" />` | WIRED | Confirmed at line 40 of csproj |
| `EmitSourceHashAttribute` target | `Processor.Sample.dll [assembly: AssemblyMetadata]` | `<AssemblyAttribute>` item → SDK `GenerateAssemblyInfo` | WIRED | `AssemblyAttribute` ItemGroup in `EmitSourceHashAttribute` target; hook is `BeforeTargets="CoreGenerateAssemblyInfo"` (not just `CoreCompile`) — critical fix that ensures embed lands before SDK collects attributes |
| `Program.cs` | `SampleProcessor` | `AddSingleton<BaseProcessorBase, SampleProcessor>()` | WIRED | Confirmed at line 17 of Program.cs |
| `SampleRoundTripE2ETests` | `Processor.Sample.dll` embedded SourceHash | `typeof(global::Processor.Sample.SampleProcessor).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()` | WIRED | Line 98-100 of test file |
| `SampleRoundTripE2ETests` | real processor-sample `skp:{id:D}` | poll host Redis (NOT seed) via `PollForHealthyLivenessAsync(procId, ct)` → `L2ProjectionKeys.Processor(procId)` | WIRED | Lines 114, 176 of test file; `SeedHostProcessorLive` confirmed absent (0 matches) |
| `SampleRoundTripE2ETests` | `POST /api/v1/orchestration/start` | `client.PostAsJsonAsync("/api/v1/orchestration/start", ...)` | WIRED | Line 123-125 of test file |
| `compose processor-sample` | `baseapi-service healthy` | `depends_on: baseapi-service: condition: service_healthy` | WIRED | Confirmed in compose.yaml lines 238-239 |
| `scripts/phase-28-close.ps1` | `processor-sample` service | `$services` pre-flight list + idempotent GET-or-create seed | WIRED | Line 136 (`$services` array), lines 86-111 (seed) |

---

## Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `SampleRoundTripE2ETests` | `hash` (SourceHash) | `typeof(SampleProcessor).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()...Value` | Yes — reads the genuine build-time embedded attribute | FLOWING |
| `SampleRoundTripE2ETests` | `procId` | `SeedProcessorAsync` → GET-or-create `/api/v1/processors/by-source-hash/{hash}` | Yes — reads real DB row via WebApi | FLOWING |
| `SampleRoundTripE2ETests` | `newDataKey` | `PollForNewExecutionDataKeyAsync` → `server.Keys(pattern: "skp:data:*")` on host Redis | Yes — polls real Redis keyspace | FLOWING |
| `SampleRoundTripE2ETests` | ES seam log | `ElasticsearchTestClient.PollEsForLog` with term on `WorkflowId` + `service.name=orchestrator` | Yes — reads real ES log from live otel pipeline | FLOWING |
| `SourceHashEmbedFacts` | `hash` | `typeof(SampleProcessor).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()` | Yes — reads real embedded attribute; no recompute | FLOWING |
| `SampleProcessorFacts` | `result` | reflection-invoked `ProcessAsync("any-input", "any-config", ct)` | Yes — invokes real implementation | FLOWING |

---

## Behavioral Spot-Checks

Step 7b: The close gate is operator-run (requires live Docker stack); it cannot be re-invoked here without the full compose stack. All runnable static checks were verified by code inspection and commit history instead.

| Behavior | Check | Result |
|----------|-------|--------|
| SourceHash.targets is valid XML with RoslynCodeTaskFactory | Code read — UsingTask + two Target elements confirmed | PASS |
| No stale "Phase 22" label in close gate header | Single match is a comment reference to analog script, not a label | PASS |
| No FLUSHDB in close gate | 0 matches | PASS |
| No `SeedHostProcessorLive` in E2E | 0 matches | PASS |
| No `RandomSha256Hex`/`SHA256`/`ComputeHash` in E2E | 0 matches | PASS |
| All 10 task commits present in git log | `49cff0a 7b78088 46e1e60 41bdf89 bbd04ea 34b4dd8 9320128 3f8d975 13400f7 327b1bb` all confirmed | PASS |
| Gate exit evidence: 395 facts GREEN x3, triple-SHA held | Recorded in 28-04-SUMMARY.md frontmatter with exact SHA-256 values | PASS (human-authorized) |
| Cross-OS reproducibility: host == docker | `ab923430…3219a8` == `ab923430…3219a8` recorded in 28-02-SUMMARY.md | PASS (human-authorized) |

---

## Requirements Coverage

| Requirement | Phase | Source Plan | Description | Status | Evidence |
|-------------|-------|-------------|-------------|--------|----------|
| IDENT-01 | 28 | Plan 01 | MSBuild target computes SourceHash — SHA-256, 64-hex, LF-normalized, ordinal path sort, BaseProcessor.Core + concrete only | SATISFIED | `SourceHash.targets` implements the exact algorithm; `SourceHashEmbedFacts.ImplFiles_Fold_Excludes_OutOfScope_And_Generated_Files` asserts exclusions |
| IDENT-02 | 28 | Plan 01 + Plan 02 | Hash embedded as `AssemblyMetadata` attribute; no stale hash on incremental builds; reproducible cross-OS | SATISFIED | Two-target stamp split prevents dropped attribute; dual-build verifier ran exit 0 with identical hashes |
| SAMPLE-01 | 28 | Plan 01 | `Processor.Sample` — first concrete console with minimal POC `ProcessAsync` | SATISFIED | `SampleProcessor.cs` overrides only `ProcessAsync`; `SampleProcessorFacts` proves single deterministic result |
| SAMPLE-02 | 28 | Plan 02 | `Processor.Sample` ships multistage Dockerfile + compose tier | SATISFIED | `Dockerfile` confirmed; compose `processor-sample` service confirmed |
| TEST-01 | 28 | Plan 03 | Real-stack E2E: live round-trip + truthful liveness-gated Start | SATISFIED | `SampleRoundTripE2ETests` confirmed; live run Passed 1 / Failed 0 (44.9s) |
| TEST-02 | 28 | Plan 04 | Phase-close gate: 3-GREEN + triple-SHA BEFORE==AFTER | SATISFIED | `phase-28-close.ps1` confirmed; gate exit 0; 395 facts x3; all three SHAs held |

All 6 Phase 28 requirements satisfied. REQUIREMENTS.md traceability table confirms all are marked Complete.

---

## Anti-Patterns Found

No blockers or warnings found in Phase 28 artifacts.

| File | Pattern | Severity | Assessment |
|------|---------|----------|------------|
| `SourceHashEmbedFacts.cs` | `SHA256` / `ComputeHash` | Checked — 0 occurrences | Not a stub: the test READS via reflection, never recomputes |
| `SampleProcessor.cs` | `return null` / `return {}` | Checked — 0 occurrences | Single concrete `ProcessResult("processor-sample-ok")` returned |
| `phase-28-close.ps1` | `Phase 22` string | Info — 1 occurrence | Comment-only reference to the analog script (`scripts/phase-22-close.ps1`); header correctly reads "Phase 28 close gate — v3.5.0" |
| `SK_P.sln` | Pre-existing UTF-8 BOM | Info (pre-existing) | BOM was present before Phase 28; the Edit tool preserved existing bytes and introduced no new BOM; documented in 28-01-SUMMARY Deviations |

---

## Human Verification Required

None. All critical runtime behaviors were authorized by the operator at two human-verify checkpoints:

1. Plan 02 Task 2 (blocking gate): `scripts/verify-sourcehash-reproducible.ps1` ran to completion, exited 0, host == docker hash. Operator typed "approved."
2. Plan 04 Task 2 (blocking gate): `scripts/phase-28-close.ps1` ran to completion, exited 0, 395 facts GREEN x3, triple-SHA BEFORE==AFTER held. Operator authorized.

Both checkpoint results are recorded in the respective SUMMARY files with exact hash values and fact counts.

---

## Gaps Summary

None. All 6 must-haves are verified. All required artifacts exist, are substantive, and are correctly wired. Data flows through the full stack: genuine embedded SourceHash → DB row → container identity resolution → liveness key → Start gate → dispatch → L2 output key → ES seam log. The close gate passed with 395 facts GREEN across 3 consecutive full-live-suite runs and triple-SHA BEFORE==AFTER held for psql, redis, and rabbitmq.

---

_Verified: 2026-06-02_
_Verifier: Claude (gsd-verifier)_
