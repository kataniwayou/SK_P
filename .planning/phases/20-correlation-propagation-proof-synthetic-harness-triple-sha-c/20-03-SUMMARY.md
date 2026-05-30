---
plan: 20-03
phase: 20-correlation-propagation-proof-synthetic-harness-triple-sha-c
status: complete
completed: 2026-05-30
tasks_completed: 2
tasks_total: 2
key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs
  modified:
    - compose.yaml
---

# Plan 20-03 Summary — Real-Stack Correlation-Propagation Proof (CORR-04 / TEST-RMQ-02)

## What was proven

The capstone cross-process correlation proof — the automated form of the Phase 19
human-UAT item — now passes against the **full live Docker stack**. A real
`POST /api/v1/orchestration/start` carrying an `X-Correlation-Id` header drives an
in-process WebApi (`sk-api`) pointed at the host stack (RabbitMQ `localhost:5673`,
host Redis `6380`, host Postgres `5433`, host otel-collector `4317`). The test asserts
the **body-minted `CorrelationId` Guid (G1)** surfaces in BOTH:
- the **orchestrator container** seam doc (`"Scheduler job start (seam)"`, `service.name=orchestrator`), and
- the **WebApi published** doc (`"Published StartOrchestration"`, `service.name=sk-api`),

in Elasticsearch under `attributes.CorrelationId`, AND that G1 is **distinct from the HTTP
`X-Correlation-Id`** — proving the per-stage handoff (not one value across all hops).

**Verified: 2× consecutive GREEN** (`dotnet test … -- --filter-class *CorrelationPropagationE2ETests`,
15s each) against rabbitmq + elasticsearch + otel-collector + the rebuilt orchestrator container.

## Tasks

1. **RealStackWebAppFactory** — points the in-process WebApi at the host stack via env-var-in-ctor
   (`RabbitMq__Host=localhost`, `RabbitMq__Port=5673`, host Redis/Postgres, `OTEL_EXPORTER_OTLP_ENDPOINT`),
   restoring all prior values in `DisposeAsync`; tagged `Category=RealStack`, `[Collection("Observability")]`.
2. **The E2E `[Fact]`** — seeds Processor→Step→Workflow, drives Start (→204), polls ES for the seam
   doc and the published doc, asserts equality (same G1) + distinctness (G1 ≠ httpCorr).

## Deviations (all real bugs found & fixed — this was the deferred-hard Phase-19 item)

This plan started from a complete-but-uncommitted leftover test (an earlier aborted run). It
**built but did not pass**; three genuine bugs were debugged to green:

- **D-1 (infra, `compose.yaml`):** the orchestrator service was **missing `OTEL_EXPORTER_OTLP_ENDPOINT`**.
  The OTLP exporter is wired bare (`AddOtlpExporter()` with no endpoint → reads the standard env var,
  else defaults to `localhost:4317` = the container itself). `baseapi-service` set it; the orchestrator
  did not, so its logs never reached ES (ES doc count 0). The `OpenTelemetry:Endpoint` appsettings key
  is **dead config**, consumed nowhere in code. Fix: add the env var (compose-DNS `otel-collector:4317`).
  Verified ES count 0 → docs after fix. (commit `b9d274e`)
- **D-2 (test, KeyPrefix):** `Phase8WebAppFactory` forces `Redis:KeyPrefix=test:cls-<ns>` for parallel
  isolation, but the orchestrator container reads `skp:`. The WebApi wrote the L2 root under
  `test:…:wf:{id}:root` while the orchestrator looked under `skp:wf:{id}:root` → it logged
  `"absent from L2 — business failure, acking"` instead of the success seam (the D-08 "seed the success
  path" trap). Fix: override `Redis:KeyPrefix=skp:` in `RealStackWebAppFactory.ConfigureWebHost`
  (after `base`, so it wins).
- **D-3 (test, ES query + extractor):** (a) queried `match_phrase` on `body`, but otel maps the message
  under the nested **`body.text`** object (not phrase-searchable) → poll always timed out. Rewrote to
  `term` on `attributes.WorkflowId` (seam) and `attributes.CorrelationId` (published), mirroring the
  proven `OrchestrationLogsE2ETests`; message text asserted in C# via `GetRawText()`. (b) `PollEsForLog`
  returns the whole ES **hit** (`{_index,_source,…}`); the extractor read `attributes` at top level
  (always null) — fixed to descend into `_source` and handle array-valued attribute fields. (commit `5cb50fb`)

## Resolved host endpoints
broker `localhost:5673` (compose 5673→5672) · Redis `localhost:6380` · Postgres `localhost:5433` ·
OTLP `http://localhost:4317` via `OTEL_EXPORTER_OTLP_ENDPOINT` (the app's exporter is bare; the standard
env var is the live knob, NOT `OpenTelemetry:Endpoint`).

## Requirements
CORR-04 and TEST-RMQ-02 → **Done**. (Also repaired pre-existing duplicate/garbage corruption in the
REQUIREMENTS.md traceability table left by earlier-wave sed edits.)

## Notes for 20-04 (close gate)
- The full stack (postgres, redis, rabbitmq, elasticsearch, otel-collector, prometheus, orchestrator)
  is up and healthy. `baseapi-service` is intentionally NOT started (WebApi runs in-process for tests).
- TEST-RMQ-02 is `Category=RealStack` — the hermetic quick-run filter (`Category!=RealStack`) excludes it;
  the close gate must run it explicitly (stack up) for the milestone-closing proof.
- TEST-RMQ-05 remains Pending (the live-broker `rabbitmqctl` queue-list arm, landed in 20-04).
