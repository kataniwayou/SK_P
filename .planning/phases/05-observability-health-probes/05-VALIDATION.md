---
phase: 5
slug: observability-health-probes
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-27
---

# Phase 5 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Filled in by gsd-planner from RESEARCH.md `## Validation Architecture` section.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 + Testcontainers.PostgreSql (.NET 8) |
| **Config file** | `tests/BaseApi.Service.Tests/BaseApi.Service.Tests.csproj` + `tests/BaseApi.Service.Tests/Observability/CollectionDefinitions.cs` |
| **Quick run command** | `dotnet test tests/BaseApi.Service.Tests --filter "FullyQualifiedName~Observability" --no-restore` |
| **Full suite command** | `dotnet test --no-restore` |
| **Estimated runtime** | ~90 seconds (cold Testcontainers boot ~30s; Observability collection serialized) |

---

## Sampling Rate

- **After every task commit:** Run quick command for the touched area (`--filter "FullyQualifiedName~<Area>"`)
- **After every plan wave:** Run full suite command
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** ~90 seconds (cold), ~15 seconds (warm, single class)

---

## Per-Task Verification Map

> Filled in by gsd-planner. Each task in 05-01-PLAN.md and 05-02-PLAN.md gets a row here mapping
> task ID → REQ-ID → threat ref → automated command → file-exists check.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| _populated by gsd-planner_ | | | | | | | | | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

> Wave 0 = stubs / fixtures / framework install. Populated by gsd-planner.

- [ ] `tests/BaseApi.Service.Tests/Observability/CollectionDefinitions.cs` — `[CollectionDefinition("Observability", DisableParallelization = true)]` to prevent `.otel-out/telemetry.jsonl` interleave (Risk 2 from RESEARCH.md)
- [ ] `tests/BaseApi.Service.Tests/Observability/OtlpFileExporterFixture.cs` — IAsyncLifetime fixture that spins up the otel-collector container with file exporter mount and tears it down
- [ ] `tests/BaseApi.Service.Tests/Health/HealthEndpointsFixture.cs` — IAsyncLifetime fixture using `WebApplicationFactory<Program>` + Testcontainers Postgres
- [ ] `tests/integration/otel-collector-config.yaml` — minimal Collector pipeline (OTLP gRPC :4317 in → file `/data/telemetry.jsonl` out)
- [ ] Wave 0 verifies stubs compile and fixtures bootstrap before Wave 1 implementation

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| OTLP exporter does NOT block app shutdown when Collector is unreachable | OBSERV-04 | Hard to assert in xUnit without flaky timeouts | Stop `otel-collector` container, `docker compose up sk-api`, send a request, then `docker compose stop sk-api` — must exit within OTel's BatchExportProcessor `ExporterTimeout` (~30s) and not hang |
| Resource attributes appear in Jaeger/Tempo when ops points Collector at real backend | OBSERV-05 | Out-of-band — requires real observability backend | Document in README how to point `otel-collector` exporters at Jaeger; smoke-test before v1 release |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references (Observability collection fixture, Health fixture, Collector config)
- [ ] No watch-mode flags (no `dotnet watch`)
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter after planner fills in per-task rows

**Approval:** pending
