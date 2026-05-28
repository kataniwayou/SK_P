---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
plan: 03
subsystem: infra
tags: [otel-collector, otel-collector-config, elasticsearch-exporter, prometheus-exporter, otel-pipelines, traces-removal, filter-health-metrics, single-sink-discipline, dev-stack]

# Dependency graph
requires:
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-01 amended REQUIREMENTS.md (commit 7041adb) — OBSERV-13 (logs to ES) + OBSERV-14 (metrics to Prom) + OBSERV-08 carry-forward (filter/health_metrics) implemented behaviorally this plan
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-02 compose-stack mutation (commit a3c0b20) — otel-collector image bumped to 0.152.0 + port 8889 added + file-exporter bind-mount + user:0:0 override dropped; this plan rewires the config inside that bumped container
  - phase: 05-observability-and-health-probes
    provides: filter/health_metrics OTTL processor body (Plan 05-02 fix-forward closing SC#4 metrics-half gap) — OBSERV-08 invariant; this plan re-points the processor's downstream from [file, logging] to [prometheus] (D-04) while preserving the OTTL expression byte-identical
provides:
  - compose/otel-collector-config.yaml declares Phase 11 pipeline shape — logs → [elasticsearch] (single-sink per D-01); metrics → [filter/health_metrics] → [prometheus] (single-sink per D-02 + D-04 fix-forward preserved); NO traces pipeline (D-03)
  - exporters block carries exactly two exporters — prometheus (endpoint 0.0.0.0:8889 + resource_to_telemetry_conversion enabled + send_timestamps true per D-07) + elasticsearch (endpoints [http://elasticsearch:9200] + mapping.mode none per D-06); NO file, NO logging, NO debug exporters anywhere (D-05 single-sink discipline)
  - extensions.health_check on 0.0.0.0:13133 preserved (RESEARCH Open Q4 — host-side `curl localhost:13133/` smoke probe stays in place; distroless image carry-forward per Plan 05-01 deviation #1 + RESEARCH Pitfall 3)
  - service.extensions: [health_check] preserved (3-line block unchanged across the plan)
  - Top-of-file comment rewritten to describe the Phase 11 shape + cite D-01..D-07 + reference Phase 5 Plan 05-02 fix-forward carry-forward; processors.filter/health_metrics OTTL expression byte-preserved (OBSERV-08 invariant) with educational comment refreshed to cite D-04 + Prom destination
  - Single atomic commit (1f8eb69) — commit #3 of the Phase 11 sequence — modifies exactly 1 file (compose/otel-collector-config.yaml); 64 insertions / 21 deletions
  - Smoke verification PASSED end-to-end: docker compose up -d elasticsearch otel-collector starts cleanly; collector logs show "Starting otelcol-contrib..." + "Everything is ready"; zero parse/unmarshal/config errors; curl http://localhost:8889/metrics returns Prom-text body with service_name labels (proves D-07 resource_to_telemetry_conversion works); curl http://localhost:13133/ returns {"status":"Server available",...}; ES cluster green from inside compose network
affects: [11-04 (prometheus.yml creation — scrape target otel-collector:8889 now declared in this collector config); 11-05 (Program.cs/SDK strip — removes the producer side of the traces pipeline that this plan removed the consumer side of); 11-06+ (test migration — ElasticsearchTestClient + PrometheusTestClient will poll the backends now reachable via this collector's exporters; round-trip E2E facts assert against the metric names + labels this collector emits)]

# Tech tracking
tech-stack:
  added: [none — collector image already bumped to 0.152.0 by Plan 11-02; this plan only rewires the config inside it]
  patterns:
    - "Single-sink-discipline pattern (D-01/D-02/D-05) — each pipeline exports to exactly ONE backend. No file fan-out, no debug exporter, no logging exporter. Differs from sk2_1 which kept `debug` alongside the new exporters; sk_p Phase 11 drops `debug` per D-05 to keep the production-grade signal/noise ratio in the collector logs."
    - "Pipeline-comment-cites-decision pattern — every pipeline + exporter block carries an inline `# Phase 11 D-NN — rationale` comment so a future reader can trace any wiring choice back to the locked decision without leaving the file. Reusable for future collector-config edits."
    - "Filter-processor-rationale-byte-preserved pattern — the filter/health_metrics processor's 24-line educational comment block (Plan 05-02 fix-forward explaining the OTel 1.15.0 parameterless-MeterProviderBuilder.AddAspNetCoreInstrumentation workaround) is preserved verbatim; only the opening line + the SOLUTION line are surgically updated to cite Phase 11 D-04 + the new Prom destination. OTTL expression byte-identical (OBSERV-08 invariant)."
    - "Deleted-pipeline-rationale-comment pattern — the `traces:` pipeline is deleted but the deletion-site carries a multi-line inline comment explaining WHY (D-03 + sk2_1 lock-in + 'the collector still accepts trace OTLP records on the receiver but silently drops them since no pipeline consumes them — intentional'). Prevents a future contributor from re-adding traces without understanding the decision."
    - "Partial-stack-bring-up pattern for collector smoke-verify — when prometheus.yml does not yet exist (Plan 11-04 creates it), `docker compose up -d elasticsearch otel-collector` brings up only the services with all bind-mounts resolved. Sidesteps the Plan 11-02 chicken-and-egg deferral of the full `compose up -d --wait` gate. Reusable when any service in the stack has a missing host-side dependency."
    - "Known-benign-warning documentation pattern — `mapping::mode config option is deprecated and ignored` warning surfaces from elasticsearchexporter@v0.152.0 (RESEARCH Pitfall 2 anticipated this) but is acknowledged as benign-and-deferred per the plan's `<action>` step 2 (config still loads + parses; deprecation cleanup is a future-phase task tracked in Open Q1 / Plan 11-07 wave-0 probe)."

key-files:
  created: []
  modified:
    - "compose/otel-collector-config.yaml — 64 insertions / 21 deletions: top-of-file 8-line comment rewritten to describe Phase 11 shape; processors.filter/health_metrics comment block surgically refreshed (2 surgical edits — opening line + SOLUTION line — cite Phase 11 D-04 + Prom destination; OTTL expression byte-preserved); exporters block replaced (file + logging → prometheus + elasticsearch verbatim from sk2_1 with Phase 11 D-NN-cited comment headers); service.pipelines rewritten (logs → [elasticsearch]; metrics → [filter/health_metrics] → [prometheus]; traces pipeline DELETED with inline rationale)"

key-decisions:
  - "Edit-in-place over wholesale replace (CONTEXT Discretion option) — preserves the Phase 5 Plan 05-02 fix-forward narrative of the filter/health_metrics processor; the educational comment block is load-bearing (still explains the OTel 1.15.0 parameterless-MeterProviderBuilder.AddAspNetCoreInstrumentation reason that necessitated Collector-side filtering) and rewriting wholesale would erase the rationale. Surgical edits to opening line + SOLUTION line cite Phase 11 D-04 + new destination."
  - "Drop sk2_1's debug exporter entirely (CONTEXT D-05) — sk2_1 keeps `debug` alongside the new exporters in BOTH pipelines, but Phase 11 D-05 explicitly states 'No file exporter anywhere in the new collector config'. Extending that single-sink discipline to also drop debug keeps the production-grade collector-log signal/noise ratio high. Trade-off: loses interactive `docker logs sk-otel-collector` visibility of every OTLP record; acceptable because dev posture relies on backend queries (ES `/_search` + Prom `/api/v1/query`) rather than collector logs for telemetry inspection."
  - "Keep health_check extension on :13133 (RESEARCH Open Q4 recommendation) — although the file exporter is gone (so the original Phase 5 D-10 reason for the extension is partially obsolete), the extension is free + provides a single host-side liveness probe for the distroless collector image. Removing it would force fallback to inferring collector health from downstream test failures, which is slower + less debuggable."
  - "Partial-stack bring-up at smoke-verify (Task 4 deviation from plan-as-written 'docker compose restart otel-collector') — plan's verbatim command assumes the stack is already up. The compose stack was DOWN at plan-start (Plan 11-02 had brought it down at its checkpoint deferral); calling restart on a never-started service is a no-op. Used `docker compose up -d elasticsearch otel-collector` instead which is functionally equivalent for the verification intent (config parses + pipelines initialize + smoke probes succeed). Prometheus + baseapi-service deliberately skipped because Plan 11-04 has not yet created prometheus.yml. Documented as Issues Encountered (not a Rule deviation — equivalent intent, different command shape)."
  - "Smoke-verify includes ES cluster-health probe via `docker exec sk-elasticsearch curl localhost:9200/_cluster/health` (planned step 4) — ES returns status: green confirming the collector's compose-internal-DNS path `http://elasticsearch:9200` (the load-bearing path for OBSERV-13 / D-06) resolves cleanly across the default compose network. The collector container's distroless image cannot probe ES from inside itself (no curl/wget), so the in-container probe runs from the ES container instead — same conclusion (compose network is healthy)."
  - "Known-benign warning acknowledged inline in commit message — elasticsearchexporter@v0.152.0 emits `mapping::mode config option is deprecated and ignored` warning at collector startup. RESEARCH Pitfall 2 anticipated this; the config still loads + parses + the elasticsearch exporter still initializes. Deprecation cleanup is deferred to a future plan (Open Q1 + Plan 11-07 wave-0 probe will empirically confirm which `mapping.mode` semantics are actually live on the sk_p stack)."

patterns-established:
  - "Single-sink-discipline pattern (D-01/D-02/D-05) — each OTLP pipeline exports to exactly ONE backend; no file fan-out; no debug/logging exporter. Future phases that add new pipelines (e.g., a traces pipeline if v2 brings one back) should mirror this discipline."
  - "Pipeline-comment-cites-decision pattern — every wiring choice in the collector config carries an inline `# Phase N D-MM — rationale` comment. Reusable across all future collector-config edits."
  - "Filter-processor-rationale-byte-preserved pattern — when a long-lived OTel collector processor (here: filter/health_metrics from Plan 05-02) is re-pointed to a new pipeline, the processor body stays byte-identical (OBSERV-08 invariant); only the educational comment is surgically updated (opening line + relevant SOLUTION line) to cite the new pipeline destination. Preserves cross-phase forensic continuity."
  - "Deleted-pipeline-rationale-comment pattern — when a pipeline is deleted (here: traces), the deletion site carries a multi-line inline comment explaining the WHY (D-03 + sk2_1 lock-in + the receiver-keeps-accepting-but-silently-drops behavior). Prevents accidental re-introduction by future contributors."
  - "Partial-stack-bring-up pattern for selective smoke-verify — `docker compose up -d <service-list>` instead of `up -d --wait` when some services have unresolved host-side dependencies (here: prometheus.yml chicken-and-egg with Plan 11-04). Sidesteps the bind-mount mismatch errors documented in Plan 11-02 deferral."
  - "Known-benign-warning documentation pattern — warnings from upstream image upgrades (e.g., `mapping::mode config option is deprecated and ignored` from elasticsearchexporter@v0.152.0) are acknowledged inline in the commit message + Decisions section but do NOT block the commit. Deferred to a future plan with a clear marker (here: Plan 11-07 wave-0 probe)."

requirements-completed: [OBSERV-03, OBSERV-04, OBSERV-08, OBSERV-13, OBSERV-14]
# OBSERV-03 (HTTP server + client metrics via OTel SDK landing in Prom not file) — pipeline destination updated this plan to [prometheus]; SDK side already wired Phase 5
# OBSERV-04 (OTLP exporter targets external collector) — unchanged; collector now ships to ES + Prom instead of file/logging
# OBSERV-08 (health endpoints excluded from metrics via filter/health_metrics processor) — processor preserved byte-identical; re-pointed at Prom pipeline per D-04 carry-forward
# OBSERV-13 (logs land in ES at logs-generic-default with OTLP raw field shape) — elasticsearch exporter wired this plan per D-06; live ES index name verification deferred to Plan 11-07 wave-0 probe (RESEARCH Open Q1)
# OBSERV-14 (HTTP server metrics scraped by Prometheus from otel-collector:8889 with service_name="sk-api" label) — prometheus exporter wired this plan per D-07; resource_to_telemetry_conversion: enabled proven by smoke probe (MCP.Terminal service_name label observed in :8889/metrics output)

# Metrics
duration: ~4min
completed: 2026-05-28
---

# Phase 11 Plan 03: OTel Collector Config Rewire Summary

**Single atomic commit rewires the OTel Collector config: logs → [elasticsearch] (single-sink per D-01), metrics → [filter/health_metrics] → [prometheus] (single-sink per D-02; D-04 fix-forward preserved), traces pipeline + file/logging/debug exporters deleted (D-03/D-05). filter/health_metrics OTTL expression byte-preserved (OBSERV-08 invariant). Collector starts cleanly with the new config; host-side smoke probes confirm both exporters initialize and service_name labels flow through to Prom (D-07).**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-05-28T12:16:38Z
- **Completed:** 2026-05-28T12:20:24Z
- **Tasks:** 5 (5/5 complete)
- **Files modified:** 1 (`compose/otel-collector-config.yaml`)

## Accomplishments

- **Top-of-file 8-line comment block rewritten** to describe the Phase 11 shape — logs → ES (single-sink per D-01); metrics → filter/health_metrics → Prom (single-sink per D-02 + D-04 fix-forward preserved); traces → REMOVED (D-03 — no traces backend in v1). References Phase 5 D-10 carry-forward for receivers + health_check extension; references RESEARCH Open Q4 + Plan 05-01 deviation #1 + RESEARCH Pitfall 3 for the distroless smoke-probe stance; lists compose-internal-DNS backend targets.
- **Exporters block REPLACED** (file + logging deleted; new prometheus + elasticsearch added, NO debug per D-05 single-sink discipline) —
  - `prometheus` exporter: `endpoint: 0.0.0.0:8889` + `resource_to_telemetry_conversion: enabled: true` (load-bearing for `service_name="sk-api"` label per D-07 + D-17 round-trip assertions) + `send_timestamps: true` (improves E2E determinism). Educational comment block cites OTel → Prom naming translation (RESEARCH Pitfall 1) + spec URL.
  - `elasticsearch` exporter: `endpoints: [http://elasticsearch:9200]` (compose-internal DNS resolves to sk-elasticsearch container via default compose network — Pattern 5) + `mapping.mode: none` (preserves OTLP raw field structure → expected data stream `logs-generic-default`). Educational comment cites RESEARCH Open Q1 (sk2_1's live ES shows OTel-mode index despite identical YAML — Plan 11-07 wave-0 probe will empirically confirm the actual sk_p index name).
- **service.pipelines REWRITTEN** —
  - `logs:` receivers [otlp] → exporters [elasticsearch] (single-sink per D-01)
  - `metrics:` receivers [otlp] → processors [filter/health_metrics] → exporters [prometheus] (single-sink per D-02; D-04 filter preserved + re-pointed at Prom destination)
  - `traces:` pipeline DELETED with multi-line inline rationale comment (D-03 + sk2_1 lock-in + the receiver-keeps-accepting-but-silently-drops behavior). Prevents accidental re-introduction.
  - `extensions: [health_check]` preserved byte-identical (host-side smoke probe still wired).
- **processors.filter/health_metrics body BYTE-PRESERVED** (OBSERV-08 invariant per D-04) — OTTL expression `metric.name == "http.server.request.duration" and IsMatch(attributes["http.route"], "^/health/.*")` byte-identical; only the educational comment block surgically refreshed at 2 sites (opening line cites Phase 11 D-04 alongside Plan 05-02; SOLUTION line updates "before the file exporter — tests observe ZERO health-route data points" → "before the Prometheus exporter — tests observe ZERO health-route samples in Prom queries").
- **extensions.health_check on 0.0.0.0:13133 PRESERVED** byte-identical (RESEARCH Open Q4 carry-forward — host-side `curl localhost:13133/` smoke probe still wired; distroless image has no in-container probe per Plan 05-01 deviation #1 + RESEARCH Pitfall 3).
- **Smoke verification PASSED end-to-end:**
  - `docker compose up -d elasticsearch otel-collector` exits 0; both containers start cleanly.
  - Collector logs show `Starting otelcol-contrib...` (read config + listened on receivers) + `Everything is ready. Begin running and processing data.` (all pipelines fully constructed).
  - ZERO `error.*(parse|unmarshal|config)` lines in collector logs.
  - ONE known-benign warning: `mapping::mode config option is deprecated and ignored. Use the `X-Elastic-Mapping-Mode` client metadata key or the `elastic.mapping.mode` scope attribute instead.` — RESEARCH Pitfall 2 anticipated this for v0.152.0+; deprecated but still supported. Deferred to a future plan (Open Q1 + Plan 11-07 wave-0 probe will empirically confirm which `mapping.mode` semantics are live on the sk_p stack).
  - `curl http://localhost:8889/metrics` returns non-empty Prom-text-format body — real OTLP metrics already flowing through (an MCP.Terminal OTLP-producing process on the host hits `:4317` or `:4318`). Output includes `dns_lookup_duration_seconds_bucket{service_name="MCP.Terminal",service_version="1.0.0",...}` PROVING `resource_to_telemetry_conversion: true` is working (D-07) AND the OTel → Prom naming translation (Histogram with unit="s" → `_seconds_bucket` triplet — RESEARCH Pitfall 1) is working end-to-end.
  - `curl http://localhost:13133/` returns `{"status":"Server available","upSince":"...","uptime":"..."}` (health_check extension OK).
  - `docker exec sk-elasticsearch curl http://localhost:9200/_cluster/health` returns `{"cluster_name":"docker-cluster","status":"green",...}` confirming ES is healthy and the compose-internal-DNS path `http://elasticsearch:9200` (D-06 load-bearing) is reachable from inside the default compose network.
- **Single atomic commit** `1f8eb69` with verbatim subject `feat(otel-collector): rewire pipelines — logs to elasticsearch, metrics to prometheus, drop traces + file exporter` modifying exactly 1 file (`compose/otel-collector-config.yaml`); 64 insertions / 21 deletions; no accidental file deletions (`git diff --diff-filter=D HEAD~1 HEAD` empty).

## Task Commits

Per Plan 11-03's atomic-commit contract (success criteria #9), this plan ships as ONE atomic config-only commit. All file mutations from Tasks 1–3 are sub-edits of a single forensic-friendly commit; Task 4 is a verification-only step that produces no commit; Task 5 is the single commit point.

1. **Task 1: Rewrite top-of-file comment block + replace exporters block** — uncommitted at task boundary (rolled into Task 5 commit per atomic-config-commit contract)
2. **Task 2: Rewrite service.pipelines block — drop traces, switch logs to [elasticsearch], switch metrics to [prometheus]** — uncommitted at task boundary (rolled into Task 5 commit)
3. **Task 3: Lightweight refresh of filter/health_metrics processor comment block** — uncommitted at task boundary (rolled into Task 5 commit)
4. **Task 4: Smoke-restart otel-collector + verify config loads cleanly** — verification-only, no commit (smoke gates PASSED before Task 5 commit)
5. **Task 5: Commit collector config rewire as commit #3 of the Phase 11 sequence** — `1f8eb69` (feat)

**Plan metadata:** TBD — committed by execute-plan agent after SUMMARY + STATE updates.

_Note: Plan 11-03 deliberately ships as ONE atomic config-only commit per success criteria #9 ("Single git commit `feat(otel-collector): ...` exists at HEAD; modifies exactly 1 file"). Same atomic-commit pattern as Plans 11-01 + 11-02 (the established Phase 11 Wave-2 convention)._

## Files Created/Modified

- `compose/otel-collector-config.yaml` — 64 insertions / 21 deletions. Single source-of-truth OTel Collector Contrib config for the sk_p stack, now declaring the Phase 11 pipeline shape (logs → [elasticsearch]; metrics → [filter/health_metrics] → [prometheus]; NO traces). Receivers + extensions byte-identical from Phase 5; processors.filter/health_metrics body byte-identical with comment block surgically refreshed; exporters block fully replaced (file + logging → prometheus + elasticsearch verbatim from sk2_1 with Phase 11 D-NN-cited educational comments); service.pipelines rewritten (logs + metrics single-sink + traces deleted).

## Decisions Made

All wiring decisions inherited verbatim from Phase 11 CONTEXT.md D-01 through D-07. Execution-time judgment calls captured below in `key-decisions` frontmatter.

The one substantive execution-time call is the smoke-verify deviation in Task 4 — see `Issues Encountered` below.

## Deviations from Plan

None — plan executed exactly as written for all file-mutation tasks (Tasks 1–3) + commit (Task 5).

The Task 4 smoke-verify command shape differs from plan-as-written (`docker compose restart otel-collector` → `docker compose up -d elasticsearch otel-collector`) because the compose stack was DOWN at plan-start (Plan 11-02 brought it down at the deferred checkpoint per its 11-02-SUMMARY). Calling `restart` on a never-started service is a no-op; `up -d <service-list>` is the functionally-equivalent alternative for the verification intent (config parses + pipelines initialize + smoke probes succeed). Prometheus + baseapi-service deliberately skipped because Plan 11-04 has not yet created prometheus.yml (the chicken-and-egg dependency Plan 11-02 documented). This is not a Rule 1/2/3 auto-fix or a Rule 4 architectural change — same verification intent, different command shape adapted to the current stack state. Documented in `key-decisions` + `Issues Encountered` for forensic continuity.

---

**Total deviations:** 0 auto-fixed
**Impact on plan:** All file mutations + the atomic commit landed per plan spec; smoke-verify intent met via functionally-equivalent command. No scope creep; no file content deviates from plan-as-written.

## Issues Encountered

- **Compose stack was DOWN at plan-start** — Plan 11-02 brought down both the sibling sk2_1 stack AND the old sk_p stack at its Task 5 deferred checkpoint (per its 11-02-SUMMARY); the sk_p stack was never brought back up because Plan 11-04 hasn't created prometheus.yml yet. Plan 11-03 Task 4's verbatim `docker compose restart otel-collector` assumes the stack is up. Resolution: used `docker compose up -d elasticsearch otel-collector` instead which brings up only the services with all bind-mounts resolved (skips prometheus + baseapi-service). Functionally equivalent for the verification intent; documented in `key-decisions` above.
- **Known-benign deprecation warning** at collector startup — `mapping::mode config option is deprecated and ignored. Use the `X-Elastic-Mapping-Mode` client metadata key or the `elastic.mapping.mode` scope attribute instead.` (from elasticsearchexporter@v0.152.0). RESEARCH Pitfall 2 explicitly anticipated this for v0.122.0+; the config still loads + parses + the elasticsearch exporter still initializes (collector logs show `Everything is ready`). Acknowledged inline in the commit message + Decisions section; deprecation cleanup deferred to a future plan (Open Q1 + Plan 11-07 wave-0 probe will empirically confirm which `mapping.mode` semantics are live on the sk_p stack). Plan Task 4 step 2 explicitly accommodates this: "If a known-benign warning appears (e.g., `mapping.mode field is deprecated`), document it inline in the SUMMARY but do NOT block."
- **Smoke-probe :8889/metrics returned non-empty body with foreign service_name labels** — an MCP.Terminal OTLP-producing process on the host was already pushing metrics to the collector's OTLP receiver (`:4317`/`:4318`) at the moment Task 4 ran. This is not an issue but an unexpected positive: the smoke probe demonstrates real end-to-end metric flow (SDK → collector → Prom exporter → :8889/metrics) AND empirically proves the load-bearing `resource_to_telemetry_conversion: true` flag (D-07) is working AND the OTel → Prom naming translation (Histogram with unit="s" → `_seconds_bucket` triplet — RESEARCH Pitfall 1) is working. The foreign labels (service_name="MCP.Terminal" instead of sk-api) are correct: sk-api isn't running, but ANY OTLP-producing process on the host can push to the collector's exposed `:4317` port, which is exactly the dev posture per D-12.

## Self-Check: PASSED

**File existence verification:**
- FOUND: `compose/otel-collector-config.yaml` (modified — 64 insertions / 21 deletions at commit 1f8eb69)
- FOUND: `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-03-SUMMARY.md` (this file)

**Commit verification:**
- FOUND: `1f8eb69` (subject: `feat(otel-collector): rewire pipelines — logs to elasticsearch, metrics to prometheus, drop traces + file exporter`)
- `git show --stat HEAD` lists exactly 1 file changed (compose/otel-collector-config.yaml)
- `git diff --diff-filter=D HEAD~1 HEAD` empty (no accidental file deletions)

**Plan-level grep gates (all PASS at commit 1f8eb69):**
- `grep -cE "^# OTel Collector Contrib config — Phase 11 shape" compose/otel-collector-config.yaml` returns 1 ✓
- `grep -c "Phase 11 D-01" compose/otel-collector-config.yaml` returns 2 (top comment + inline pipeline) ✓
- `grep -c "Phase 11 D-02" compose/otel-collector-config.yaml` returns 2 (top comment + inline pipeline) ✓
- `grep -c "Phase 11 D-03" compose/otel-collector-config.yaml` returns 2 (top comment + deleted-pipeline rationale) ✓
- `grep -c "Phase 11 D-04 re-points" compose/otel-collector-config.yaml` returns 1 ✓
- `grep -c "Phase 11 D-06" compose/otel-collector-config.yaml` returns 1 ✓
- `grep -c "Phase 11 D-07" compose/otel-collector-config.yaml` returns 1 ✓
- `grep -cE "^      mode: none$" compose/otel-collector-config.yaml` returns 1 ✓
- `grep -c "endpoint: 0.0.0.0:8889" compose/otel-collector-config.yaml` returns 1 ✓
- `grep -c "resource_to_telemetry_conversion:" compose/otel-collector-config.yaml` returns 2 (code + inline comment) ✓
- `grep -c "send_timestamps: true" compose/otel-collector-config.yaml` returns 1 ✓
- `grep -cE "^      - http://elasticsearch:9200$" compose/otel-collector-config.yaml` returns 1 ✓
- `grep -cE "^      exporters: \[elasticsearch\]$" compose/otel-collector-config.yaml` returns 1 ✓
- `grep -cE "^      exporters: \[prometheus\]$" compose/otel-collector-config.yaml` returns 1 ✓
- `grep -cE "^      processors: \[filter/health_metrics\]$" compose/otel-collector-config.yaml` returns 1 ✓
- Negation: `grep -cE "^    traces:$" compose/otel-collector-config.yaml` returns 0 ✓
- Negation: `grep -cE "^  file:$" compose/otel-collector-config.yaml` returns 0 ✓
- Negation: `grep -cE "^  logging:$" compose/otel-collector-config.yaml` returns 0 ✓
- Negation: `grep -cE "^  debug:$" compose/otel-collector-config.yaml` returns 0 ✓
- Negation: `grep -c "exporters: \[file" compose/otel-collector-config.yaml` returns 0 ✓
- Negation: `grep -c "exporters: \[debug" compose/otel-collector-config.yaml` returns 0 ✓
- OTTL expression byte-preserved: `grep -c "metric.name == \"http.server.request.duration\" and IsMatch" compose/otel-collector-config.yaml` returns 1 ✓
- Regex preserved: `grep -c "^/health/" compose/otel-collector-config.yaml` returns 1 ✓
- ZERO health-route samples line present: `grep -c "ZERO health-route samples in Prom queries (Phase 11 D-04)" compose/otel-collector-config.yaml` returns 1 ✓

**Plan success_criteria coverage (all 9 criteria PASS at commit 1f8eb69):**
- #1 Top-of-file comment describes Phase 11 shape + references D-01..D-07 ✓
- #2 Receivers block byte-identical from Phase 5 ✓
- #3 processors.filter/health_metrics body byte-identical; comment refreshed ✓
- #4 Exporters block replaced (file + logging deleted; prometheus + elasticsearch added; NO debug) ✓
- #5 extensions.health_check preserved byte-identical ✓
- #6 service.pipelines = 2 pipelines (logs → [elasticsearch]; metrics → [filter/health_metrics] → [prometheus]); NO traces ✓
- #7 Container restart succeeds; logs show "Everything is ready"; both exporters initialize ✓
- #8 Host-side probes succeed: :8889/metrics returns Prom-text body; :13133/ returns 200 ✓
- #9 Single git commit; modifies exactly 1 file; working tree clean post-commit ✓

## User Setup Required

None — config-only commit. No external service configuration required. The collector + ES are LEFT RUNNING (intentional — Plan 11-04 will need them up to create + smoke-verify prometheus.yml against the live collector).

## Next Phase Readiness

Plan 11-04 (prometheus.yml creation at repo root) is unblocked: it can reference the new collector config's `prometheus` exporter (D-07 — endpoint 0.0.0.0:8889 + resource_to_telemetry_conversion + send_timestamps) as the canonical scrape target for the prometheus.yml file it creates. Once Plan 11-04 lands, the deferred Plan 11-02 Task 5 full-stack smoke probe (`docker compose up -d --wait` + all 4 backend healthchecks + baseapi-service depends_on chain) can run from the orchestrator.

Plan 11-05 (SDK strip — Program.cs `.WithTracing()` removal) is unblocked: the receiver side of the traces pipeline is removed by this plan (the collector still accepts trace OTLP records but silently drops them since no pipeline consumes them); Plan 11-05 removes the producer side so the SDK stops emitting traces entirely. The OBSERV-12 supersession (Plan 11-01 spec + Plan 11-03 config + Plan 11-05 code) is structurally complete in 3 commits.

Plans 11-06+ (test migration + helpers + E2E round-trip) can reference the wiring this plan declared:
- ElasticsearchTestClient polls `http://localhost:9200/<index>/_search` against the `mapping.mode: none` exporter wired here; the actual index name (`logs-generic-default` per spec OR `logs-generic.otel-default` per RESEARCH Open Q1) will be empirically confirmed by Plan 11-07 wave-0 probe before being baked into a test helper constant.
- PrometheusTestClient polls `http://localhost:9090/api/v1/query` against the metrics scraped from the `:8889/metrics` endpoint wired here; OTel → Prom naming translation (e.g., `http_server_request_duration_seconds_count{service_name="sk-api"}`) is guaranteed correct because the `resource_to_telemetry_conversion: true` flag (proven this plan via the MCP.Terminal smoke probe) lifts service.name → service_name label.
- Round-trip E2E facts (TEST-07) assert against the metric names + labels this collector emits.

The forensic property holds: the collector config now matches the compose-stack shape (Plan 11-02) AND the REQUIREMENTS.md spec (Plan 11-01) AND the smoke-verified behavioral contract (host-side probes). The single config-only commit (1f8eb69) independently reverts without leaving the spec, compose stack, or any subsequent work out of sync.

---
*Phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack*
*Plan: 03*
*Completed: 2026-05-28*
