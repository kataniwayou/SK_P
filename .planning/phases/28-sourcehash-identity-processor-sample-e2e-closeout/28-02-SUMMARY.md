---
phase: 28-sourcehash-identity-processor-sample-e2e-closeout
plan: 02
subsystem: infra
tags: [docker, multistage, compose, sourcehash, cross-os-reproducibility, processor-sample, dotnet8, healthcheck]

# Dependency graph
requires:
  - phase: 28-sourcehash-identity-processor-sample-e2e-closeout
    plan: 01
    provides: "Processor.Sample concrete + SourceHash.targets embedding the reproducible 64-hex (ab923430…3219a8) via reflection-read assembly metadata"
provides:
  - "src/Processor.Sample/Dockerfile — multistage net8.0 sdk:8.0-bookworm-slim build → aspnet:8.0-bookworm-slim runtime, port 8082, wget-enabled health probe; the SourceHash.targets compute/emit runs INSIDE the Linux publish"
  - "compose.yaml processor-sample service — joins the host stack mirroring the orchestrator tier, with a baseapi-service service_healthy depends_on (identity-over-the-bus) + short Processor__ExecutionDataTtl: 5 (close-gate hygiene)"
  - "ComposeYamlFacts +3 facts guarding the processor-sample block (service block, baseapi-service healthy dependency, short ExecutionDataTtl)"
  - "scripts/verify-sourcehash-reproducible.ps1 — dual-build (host SDK vs Linux Docker) embedded-hash equality verifier; PROVED host==docker byte-identical"
affects: [28-03-e2e-roundtrip, 28-04-closeout]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Multistage Dockerfile mirrors the Orchestrator analog — selective csproj-first restore-cache COPY closure (Messaging.Contracts + BaseConsole.Core + BaseProcessor.Core + Processor.Sample), then COPY src/ src/, then publish (T-28-06: no secrets copied)"
    - "aspnet:8.0 runtime base (NOT runtime:8.0) — BaseConsole.Core's FrameworkReference Microsoft.AspNetCore.App needs the ASP.NET Core shared framework for the embedded Kestrel health listener"
    - "wget installed as root BEFORE USER app — the slim aspnet image ships neither wget nor curl, so the compose wget --spider /health/ready healthcheck idiom needs it explicitly installed"
    - "Cross-OS build reproducibility PROVEN by a dual-build verifier (host SDK build vs Linux Docker build) asserting byte-equal embedded SourceHash — Pitfall-1 normalizations (forward-slash paths + LF content) held with zero SourceHash.targets changes"

key-files:
  created:
    - src/Processor.Sample/Dockerfile
    - scripts/verify-sourcehash-reproducible.ps1
  modified:
    - compose.yaml
    - tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs

key-decisions:
  - "Cross-OS SourceHash reproducibility is PROVEN: host SDK build == Linux Docker build, both ab923430c6bf6301fec974ef6feb1f51f847a1e35e097d3a95694892353219a8 (byte-identical). This is the single highest-risk gate of the phase (RESEARCH §A4 / Pitfall 1) and it GATES Plan 03's E2E — the E2E reflects the host-built hash, registers it as the DB row, while the live container runs the Linux-Docker-built hash; divergence would mean identity never resolves and the liveness-gated Start fails silently."
  - "NO SourceHash.targets changes were needed — Plan 01's Pitfall-1 normalizations (per-file SHA-256 over LF-normalized UTF-8 content, folded in forward-slash ordinal path-sort order) held across Windows-host and Linux-Docker builds on the first dual-build run."
  - "compose processor-sample carries TWO orchestrator-divergences: baseapi-service service_healthy depends_on (the processor resolves identity over the bus from the WebApi responder — orchestrator does not) and Processor__ExecutionDataTtl: 5 so the round-trip's skp:data:* keys self-expire before the Plan-04 close-gate AFTER snapshot (Pitfall 4)."
  - "aspnet:8.0-bookworm-slim runtime base + explicit wget install — required for the embedded Kestrel health listener and the wget --spider /health/ready compose healthcheck respectively."

requirements-completed: [SAMPLE-02]

# Metrics
duration: 3min
completed: 2026-06-02
---

# Phase 28 Plan 02: Processor.Sample Dockerfile + Compose Tier + Cross-OS SourceHash Reproducibility Summary

**Processor.Sample ships a multistage net8.0 Dockerfile (sdk → aspnet, port 8082, wget healthcheck) and a compose processor-sample tier (baseapi-service healthy depends_on + short ExecutionDataTtl), guarded by +3 ComposeYamlFacts — and the load-bearing risk gate is closed: a dual-build verifier PROVES the Windows-host SDK build and the Linux-Docker build embed the byte-identical SourceHash, with zero SourceHash.targets changes.**

## Performance

- **Duration:** ~3 min (per-task implementation; checkpoint-gated)
- **Tasks:** 2 (Task 1 auto; Task 2 blocking human-verify — APPROVED)
- **Files:** 4 (2 created, 2 modified)

## Accomplishments
- **`src/Processor.Sample/Dockerfile`** — multistage net8.0 image mirroring `src/Orchestrator/Dockerfile`: `sdk:8.0-bookworm-slim` build stage with a selective csproj-first restore-cache COPY closure (Messaging.Contracts + BaseConsole.Core + BaseProcessor.Core + Processor.Sample) → `dotnet publish -c Release /p:UseAppHost=false`, then an `aspnet:8.0-bookworm-slim` runtime stage with `wget` installed before `USER app`, `ASPNETCORE_URLS=http://+:8082`, `EXPOSE 8082`, `ENTRYPOINT ["dotnet", "Processor.Sample.dll"]`. The SourceHash.targets compute/emit runs INSIDE this Linux publish — the locus of the Pitfall-1 cross-OS reproducibility constraint.
- **`compose.yaml` processor-sample service** — `container_name: sk-processor-sample`, `restart: unless-stopped`, the orchestrator-tier env block (RabbitMq guest/guest, Redis connection string, OTLP endpoint), plus the two divergences: `depends_on: baseapi-service: { condition: service_healthy }` (identity over the bus) and `Processor__ExecutionDataTtl: "5"` (close-gate hygiene). Healthcheck is the established `["CMD", "wget", "--spider", "-q", "http://localhost:8082/health/ready"]` form (interval 10s / timeout 3s / retries 5 / start_period 30s). `docker compose config --quiet` exits 0.
- **`tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` +3 facts** — `ComposeYaml_Has_ProcessorSample_Service_Block`, `ComposeYaml_ProcessorSample_DependsOn_BaseApi_Healthy`, `ComposeYaml_ProcessorSample_Sets_Short_ExecutionDataTtl`, reusing the existing `ComposeYamlContent()` / `Assert.Matches(new Regex(...))` idiom. ComposeYaml filter: Passed 394 / Failed 0.
- **`scripts/verify-sourcehash-reproducible.ps1`** — dual-build verifier: builds Processor.Sample on the host SDK and reflects the embedded SourceHash from `Processor.Sample.dll`, builds the Linux Docker image (`docker build -f src/Processor.Sample/Dockerfile`) and extracts the embedded SourceHash from the published dll inside the image, then asserts byte-equality. It RAN end-to-end and exited 0.

## The Highest-Risk Gate — PROVEN (Task 2, APPROVED)

The dual-build verifier ran and the two embedded hashes are byte-identical:

```
HOST   SourceHash = ab923430c6bf6301fec974ef6feb1f51f847a1e35e097d3a95694892353219a8
DOCKER SourceHash = ab923430c6bf6301fec974ef6feb1f51f847a1e35e097d3a95694892353219a8
MATCH — host == docker.
```

This closes the phase's single highest-risk item (RESEARCH §A4 / Pitfall 1, threat T-28-05). The host-built hash that Plan 03's E2E reflects and registers as the Processor DB row EQUALS the Linux-Docker-built hash the live container runs — so identity resolution will succeed, the container will reach Healthy, and the liveness-gated Start will pass. A divergence here would have surfaced as a silent E2E hang on Start (container logs looping "Processor row not yet registered for hash"). **This plan gates Plan 03.**

## Task Commits

1. **Task 1: Processor.Sample Dockerfile + compose processor-sample tier + ComposeYamlFacts +3** — `bbd04ea` (feat). Verified: `docker compose config --quiet` exit 0; ComposeYaml filter Passed 394 / Failed 0.
2. **Task 2: cross-OS SourceHash reproducibility verifier (blocking human-verify gate)** — `34b4dd8` (feat). Script ran, exit 0, host==docker byte-identical. Checkpoint APPROVED by the operator.

## Decisions Made
- **Cross-OS reproducibility is PROVEN** (host == docker == `ab923430…3219a8`). Gates Plan 03's E2E.
- **No SourceHash.targets changes** — the Plan-01 Pitfall-1 normalizations (LF content + forward-slash ordinal path sort) held cross-OS on the first dual-build.
- **aspnet (not runtime) base** — BaseConsole.Core's `FrameworkReference Microsoft.AspNetCore.App` (embedded Kestrel health listener) needs the ASP.NET Core shared framework.
- **wget installed before `USER app`** — slim aspnet ships no wget/curl; the compose `wget --spider /health/ready` healthcheck needs it.
- **compose divergences from the orchestrator analog** — `baseapi-service` healthy depends_on (identity over the bus) + short `Processor__ExecutionDataTtl: 5` (close-gate hygiene, Pitfall 4).

## Deviations from Plan

None — both tasks executed exactly as written. The highest-risk dual-build gate passed on the first run with no SourceHash.targets remediation required (the Pitfall-1 normalizations from Plan 01 held cross-OS). No Rule 1/2/3 auto-fixes; no architectural (Rule 4) escalations.

## Requirements
- **SAMPLE-02** — satisfied: Processor.Sample ships a multistage Dockerfile and joins the compose stack mirroring the Orchestrator tier.
- **IDENT-02** — already satisfied in Plan 01 (the reproducible-on-incremental-build embed); this plan's dual-build additionally PROVES it is reproducible cross-OS (host == Docker), the property Plan 03 depends on. (28-02 frontmatter lists IDENT-02; REQUIREMENTS.md already marks it Complete from 28-01.)

## Threat Surface
- **T-28-05** (cross-OS hash divergence) — mitigated and PROVEN by the dual-build verifier (exit 0, byte-equal).
- **T-28-06** (Dockerfile build-context COPY) — mitigated: selective csproj-first restore layer then `COPY src/ src/`; no secrets / no .env / no credentials copied (dev guest/guest broker creds come from compose env, not the image).
- No new threat surface beyond the plan's `<threat_model>`.

## Next Phase Readiness
- The cross-OS reproducibility guarantee Plan 03 depends on is now PROVEN. Plan 03 (real-stack SampleRoundTripE2ETests — genuine embedded hash, no synthetic liveness seed, truthful liveness-gated Start) and Plan 04 (phase-28-close gate) are unblocked. Phase 28 = 2/4 plans.

---
*Phase: 28-sourcehash-identity-processor-sample-e2e-closeout*
*Completed: 2026-06-02*

## Self-Check: PASSED

Both created files exist on disk (`src/Processor.Sample/Dockerfile`, `scripts/verify-sourcehash-reproducible.ps1`); both modified files present (`compose.yaml`, `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs`). Both task commits present in git history: `bbd04ea` (Dockerfile + compose + facts), `34b4dd8` (reproducibility verifier).
