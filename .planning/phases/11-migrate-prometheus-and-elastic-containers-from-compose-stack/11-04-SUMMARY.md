---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
plan: 04
subsystem: infra
tags: [prometheus, scrape-config, otel-collector, dev-stack, verbatim-from-sk2_1, single-static-target, no-auth, no-relabel]

# Dependency graph
requires:
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-02 compose-stack mutation (commit a3c0b20) — added prometheus service block at lines 86-100 of compose.yaml with bind-mount `./prometheus.yml:/etc/prometheus/prometheus.yml:ro` at line 94; this plan creates the host-side file that bind-mount resolves
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-01 amended REQUIREMENTS.md (commit 7041adb) — INFRA-08 (per-service shape details) + OBSERV-14 (HTTP server metrics scraped by Prometheus from otel-collector:8889 with service_name="sk-api" label); this plan implements the Prom-scrape-config half of INFRA-08 + the scrape-target half of OBSERV-14
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-03 collector-config rewire (commit 1f8eb69) — collector's prometheus exporter listens on 0.0.0.0:8889 with resource_to_telemetry_conversion enabled (D-07); this plan's static target `otel-collector:8889` points at exactly that endpoint
provides:
  - New file `prometheus.yml` at sk_p repo root (NOT in `compose/` — flat layout per D-08 + sk2_1 verbatim) — declares the Phase 11 Prometheus scrape config: single static target `otel-collector:8889` on metrics_path `/metrics`, 15s scrape_interval / 15s evaluation_interval / 10s scrape_timeout (verbatim from sk2_1 with Phase 11 D-07 / D-08-cited comment header)
  - Single scrape_configs job named `otel-collector` — one static_configs entry with one targets entry pointing at compose-internal DNS `otel-collector:8889`; metrics_path explicit at `/metrics`
  - NO authentication blocks (no basic_auth, no bearer_token), NO tls_config, NO metric_relabel_configs — dev posture per D-08 + sk2_1 lock-in. Negative-assertion comments preserved verbatim from plan body for future-reader cross-reference
  - Standalone Prometheus container (declared by Plan 11-02 at compose.yaml lines 86-100) now scrapes the collector's `/metrics` endpoint every 15 s; `/api/v1/query` on host port 9090 is available for test code to probe
  - Single atomic commit (b40299c) — commit #4 of the Phase 11 sequence — modifies exactly 1 new file (`prometheus.yml`); 30 insertions / 0 deletions
  - Backend smoke verification PASSED end-to-end: `docker compose up -d prometheus` brings up sk-prometheus cleanly; container logs show "Server is ready to receive web requests" + "Loading configuration file" without parse/invalid-config errors; `curl http://localhost:9090/api/v1/targets` returns the otel-collector job with `"health":"up"` and `lastError:""`; sample query `up{job="otel-collector"}` returns a non-empty result vector with value `"1"` confirming end-to-end ingestion
affects: [11-05 (Program.cs/SDK strip — once the SDK no longer registers traces and stops emitting them, Prom continues scraping metrics through this config unchanged); 11-06+ (test migration + helpers + E2E — PrometheusTestClient will use the localhost:9090 /api/v1/query endpoint exposed by this scrape config + the prometheus service host-port from Plan 11-02); deferred Plan 11-02 Task 5 backend smoke verification (the orchestrator will now run the full compose-up smoke probe sequence — ES /_cluster/health + Prom /-/healthy + collector :8889/metrics + collector :13133/ — since the prometheus.yml bind-mount chicken-and-egg is now resolved)]

# Tech tracking
tech-stack:
  added:
    - "prometheus.yml — net-new file at sk_p repo root (verbatim from sk2_1 per D-08); single-source-of-truth scrape config for the Phase 11 metrics pipeline; bind-mounted read-only into sk-prometheus via compose.yaml line 94"
  patterns:
    - "Verbatim-from-sibling-repo pattern (continued) — this plan is the third verbatim-mirror commit of Phase 11 (after Plan 11-02's elasticsearch + prometheus service blocks + Plan 11-03's elasticsearch + prometheus exporter blocks). sk2_1's prometheus.yml is byte-for-byte the source; only the top-of-file comment header was rewritten to cite Phase 11 D-07 + D-08 markers (sk2_1's `Phase 12 INFR-07` + `CONTEXT.md D-07` labels are sk2_1-internal and would not trace back to sk_p's decision documents)."
    - "Negative-assertion-comments pattern — three inline comments (`# NO authentication`, `# NO tls_config`, `# No metric_relabel_configs`) document the ABSENCE of config blocks that a future contributor might assume should be present in a production scrape config. The comments are load-bearing for cross-reference to D-08 dev posture + sk2_1 lock-in; their existence creates a tension with a literal grep gate but matches the plan's <action> verbatim content. Resolution: the must_haves invariants are SEMANTIC (no functional YAML keys for auth/TLS/relabel), satisfied by the comments-only mentions."
    - "Bind-mount-precedes-file-creation deferral RESOLVED — Plan 11-02 declared the `./prometheus.yml:/etc/prometheus/prometheus.yml:ro` bind-mount as commit #2, deferring backend smoke verification to post-Wave-3. This plan ships the host-side file that bind-mount resolves, closing the chicken-and-egg gap. The orchestrator can now run the full compose-up smoke probe sequence from the Plan 11-02 deferral."
    - "Partial-stack bring-up (continued from Plan 11-03) — `docker compose up -d prometheus` brings up only the prometheus service against the already-running ES + collector from Plan 11-03's smoke verification. Plan-as-written said `restart prometheus` but the service was not yet running (created for the first time on first up); functionally equivalent to a fresh start."

key-files:
  created:
    - "prometheus.yml — 30 lines: top-of-file 6-line comment header cites Phase 11 D-07 + D-08 + sk2_1 lock-in; global block declares 15s scrape_interval + 15s evaluation_interval + 10s scrape_timeout (D-08 verbatim); scrape_configs declares one job `otel-collector` with one static_configs target `otel-collector:8889` on metrics_path `/metrics`; 3 negative-assertion inline comments document the absence of auth/TLS/relabel blocks (D-08 dev posture verbatim from sk2_1)."
  modified: []

key-decisions:
  - "File location at sk_p repo root, NOT in `compose/` (D-08 + sk2_1 verbatim layout lock-in) — the bind-mount in compose.yaml at line 94 is `./prometheus.yml:/etc/prometheus/prometheus.yml:ro`; Compose evaluates `./` relative to compose.yaml's directory = repo root. Placing it at `compose/prometheus.yml` would require updating the bind-mount path AND would diverge from sk2_1's flat layout. Plan 11-02 already chose the verbatim layout when declaring the bind-mount; this plan honors that choice."
  - "Comment header rewritten to cite Phase 11 D-07 + D-08 (sk2_1's `Phase 12 INFR-07` + `CONTEXT.md D-07` rewritten) — sk2_1's labels are sk2_1-internal; a sk_p reader of prometheus.yml needs to trace back to sk_p's decision documents, not sk2_1's. The rewrite preserves the educational intent of every comment line while making cross-references work in the sk_p planning hierarchy. First instance of `Phase N D-MM` cross-reference convention applied to a configuration file in this repo (compose.yaml + collector-config.yaml already used the convention in Plans 11-02 + 11-03)."
  - "Used `docker compose up -d prometheus` instead of plan-as-written `restart prometheus` (Rule 3 fix-forward) — at plan-start the prometheus service had never been started (Plan 11-02 deferred the compose-up at the file-mount chicken-and-egg checkpoint; Plan 11-03 used a partial-stack bring-up of only ES + collector). `restart` on a never-started service is undefined behavior in compose v2; `up -d` for a never-started service is the canonical first-start. Functionally equivalent for verification intent; the service started cleanly with the new bind-mounted config on the first attempt. Plan 11-03 established the partial-stack bring-up precedent."
  - "Pre-existing empty directory `prometheus.yml/` removed before writing the file (Rule 3 fix-forward) — Plan 11-02's deferred compose-up attempt created an empty directory at `prometheus.yml` (Docker's default behavior when a bind-mount source path does not exist). Removing the directory was a blocking issue (Write would fail because the path resolved to a directory). `rmdir prometheus.yml` cleared it; the subsequent Write succeeded. Plan 11-02 SUMMARY explicitly documented this artifact would need cleanup."
  - "Smoke verification proves end-to-end ingestion via `up{job=\"otel-collector\"}` query (not just `/api/v1/targets` health check) — the targets endpoint shows scrape SETUP succeeded (Prom can reach the target's DNS + port), but the query against `up` confirms Prom actually scraped, parsed the response, and ingested the data point. The single-vector result `value:[<ts>,\"1\"]` proves Prom's scraper, parser, and TSDB ingestion path are all live. This is a stronger gate than the plan's targets endpoint check alone and aligns with the Phase 11 D-17 round-trip-verification mindset."

patterns-established:
  - "Bind-mount-precedes-file-creation deferral RESOLUTION pattern — when an earlier plan declared a bind-mount whose host-side file is created by a later plan (deferring backend smoke verification), the later plan's atomic commit closes the deferral. The orchestrator's post-Wave-N run of the deferred verification is now safe; the file-mount chicken-and-egg is resolved. Pattern reusable for any future cross-plan bind-mount dependencies."
  - "Pre-existing empty-directory cleanup pattern — when Docker auto-creates a directory at a bind-mount source path during a failed compose-up attempt, the directory must be removed before the host-side file can be written. Add `rmdir <path>` to the Task action before the Write tool call. Plan 11-02 SUMMARY documented this would happen; this plan exercised the cleanup pattern."
  - "Negative-assertion-comments-in-config-file pattern — three inline `# NO ... — dev mode` comments in prometheus.yml document the ABSENCE of auth/TLS/relabel blocks that a future contributor might assume should be present in a production config. The comments make the dev-posture decision visible inside the file itself (not just in the surrounding plan + REQUIREMENTS docs). Reusable for any v1 dev-posture config file."
  - "End-to-end ingestion verification via Prom HTTP API + sample query — `curl /api/v1/query?query=up{job=\"...\"}` returning a non-empty result vector with value `1` is a stronger gate than `/api/v1/targets` alone (which only confirms scrape SETUP). Pattern reusable for any future Prom-related smoke verification."

requirements-completed: [INFRA-08, OBSERV-14]
# INFRA-08 (compose ES + Prom + collector bump — per-service shape detail incl. prometheus.yml scrape config) — Plan 11-02 implemented the compose-stack declarations; this plan implements the prometheus.yml content that the Plan 11-02 bind-mount expects. INFRA-08 is now fully implemented behaviorally (compose declares the shape; prometheus.yml provides the scrape config; smoke verification confirms end-to-end).
# OBSERV-14 (HTTP server metrics scraped by Prometheus from otel-collector:8889 with service_name="sk-api" label) — Plan 11-03 wired the collector's prometheus exporter on 0.0.0.0:8889 with resource_to_telemetry_conversion enabled; this plan declares the scrape config that points Prom at that endpoint. Smoke verification confirms the scrape path: `up{job="otel-collector"}` returns value "1" proving Prom is scraping the collector. The `service_name="sk-api"` label half awaits Plan 11-05+'s SDK strip + Phase11 E2E test (the sk-api service has not yet emitted real OTLP metrics this plan; the smoke verified Prom-side scrape ingestion works). OBSERV-14 is STRUCTURALLY complete; behavioral verification of the service_name="sk-api" label specifically lands when the SDK starts emitting under Plans 11-05+.

# Metrics
duration: ~3min
completed: 2026-05-28
---

# Phase 11 Plan 04: Prometheus Scrape Config Summary

**Single atomic commit creates `prometheus.yml` at the sk_p repo root with the verbatim sk2_1 scrape configuration (single static target `otel-collector:8889`, 15s interval, no auth, no relabels) — completes the Prom side of the metrics pipeline and resolves Plan 11-02's bind-mount chicken-and-egg deferral.**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-05-28T12:28:24Z
- **Completed:** 2026-05-28T12:31:17Z
- **Tasks:** 3 (3/3 complete)
- **Files modified:** 1 new (`prometheus.yml`); 0 modified

## Accomplishments

- **`prometheus.yml` created at sk_p repo root** (NOT in `compose/`) — verbatim sk2_1 content with Phase 11 D-07 + D-08-cited comment header. 30 lines total: 6-line top-of-file comment block; 4-line `global` block (`scrape_interval: 15s`, `evaluation_interval: 15s`, `scrape_timeout: 10s` per D-08); 16-line `scrape_configs` block (single job `otel-collector`, single static target `'otel-collector:8889'`, explicit `metrics_path: '/metrics'`); 3 negative-assertion inline comments documenting the absence of auth/TLS/relabel blocks (D-08 dev posture).
- **Prometheus container started cleanly with the new config** — `docker compose up -d prometheus` exits 0; sk-prometheus container starts. Container logs show:
  - `level=INFO msg="Loading configuration file" filename=/etc/prometheus/prometheus.yml`
  - `level=INFO msg="Completed loading of configuration file" ... totalDuration=8.808226ms`
  - `level=INFO msg="Server is ready to receive web requests."`
  - ZERO `level=ERROR` lines; ZERO `parsing YAML file` or `invalid configuration` matches; ZERO scrape-error lines against `otel-collector:8889`.
- **Targets endpoint shows healthy scrape** — `curl http://localhost:9090/api/v1/targets` returns the otel-collector job with `"health":"up"`, `"lastError":""`, `"scrapeUrl":"http://otel-collector:8889/metrics"`, `"scrapeInterval":"15s"`, `"scrapeTimeout":"10s"`. `droppedTargets` empty.
- **End-to-end ingestion verified via sample query** — `curl "http://localhost:9090/api/v1/query?query=up{job=\"otel-collector\"}"` returns `{"status":"success","data":{"resultType":"vector","result":[{"metric":{"__name__":"up","instance":"otel-collector:8889","job":"otel-collector"},"value":[1779971440.545,"1"]}]}}`. The value `"1"` proves Prom successfully scraped the collector, parsed its `/metrics` response, and ingested the data point.
- **Single atomic commit** `b40299c` with verbatim subject `feat(prometheus): add scrape config for otel-collector:8889 (verbatim from sk2_1)` modifying exactly 1 new file (`prometheus.yml`); 30 insertions / 0 deletions; no accidental file deletions (`git diff --diff-filter=D HEAD~1 HEAD` empty).
- **Phase 11 backend stack now fully wired end-to-end** — SDK (Phase 5) emits OTLP → collector (Plan 11-03 config) ships logs to ES + metrics to Prom-exporter on `:8889` → Prom (Plan 11-02 service + this plan's scrape config) scrapes the collector every 15 s. The deferred Plan 11-02 Task 5 backend smoke verification (compose-up + ES + Prom + collector probes) is now unblocked and the orchestrator can run it post-Plan-11-04 commit per its stated intent.

## Task Commits

Each task was committed atomically (3 tasks; Tasks 1 + 2 are file-mutation + verification-only checkpoints, rolled into the single Task 3 commit per the plan's atomic-commit contract):

1. **Task 1: Create prometheus.yml at the sk_p repo root** — uncommitted at task boundary (rolled into Task 3 commit; file mutation only)
2. **Task 2: Smoke-restart prometheus + confirm scrape succeeds** — uncommitted at task boundary (verification-only; no file changes)
3. **Task 3: Commit prometheus.yml as commit #4 of the Phase 11 sequence** — `b40299c` (feat)

**Plan metadata:** TBD — committed by execute-plan agent after SUMMARY + STATE updates.

_Note: Plan 11-04 ships as ONE atomic single-file commit per success criteria #8 ("Single git commit ... exists at HEAD; modifies exactly 1 new file (prometheus.yml); working tree clean post-commit"). Task 1 is a file mutation; Task 2 is verification-only; Task 3 is the single commit point. Same atomic-commit pattern as Plan 11-01 + Plan 11-02 + Plan 11-03._

## Files Created/Modified

- `prometheus.yml` — NEW file at sk_p repo root. 30 lines / 30 insertions / 0 deletions. Single source-of-truth Prometheus scrape config for the Phase 11 metrics pipeline. Declares one `otel-collector` job with one static target `otel-collector:8889` on metrics_path `/metrics`; 15s scrape_interval + 15s evaluation_interval + 10s scrape_timeout (D-08 verbatim); no authentication / TLS / metric_relabel_configs blocks (dev posture per D-08 + sk2_1 lock-in). Bind-mounted into sk-prometheus container at `/etc/prometheus/prometheus.yml:ro` per compose.yaml line 94 (Plan 11-02).

## Decisions Made

All structural decisions inherited verbatim from Phase 11 CONTEXT.md D-07 + D-08 + sk2_1's `C:/Users/UserL/source/repos/sk2_1/prometheus.yml`. Execution-time judgment calls:

- **`docker compose up -d prometheus` instead of `restart prometheus`** (Rule 3 fix-forward documented below). Plan-as-written said `restart` but the prometheus service had never been started; `up -d` is the canonical first-start.
- **Pre-existing empty directory `prometheus.yml/` removed before writing** (Rule 3 fix-forward documented below). Plan 11-02's deferred compose-up created the empty directory; cleanup required before the file could be written.
- **Negative-assertion comments preserved verbatim** (matches plan `<action>` body content; tension with the literal grep gate resolved by treating must_haves invariants as SEMANTIC — no functional YAML keys for auth/TLS/relabel — satisfied by comments-only mentions). See Deviations below.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Removed Docker-created empty directory `prometheus.yml/` before writing the host-side file**

- **Found during:** Task 1 (Create prometheus.yml at the sk_p repo root)
- **Issue:** Plan 11-02's deferred compose-up attempt (at Plan 11-02 Task 5 checkpoint) had triggered Docker's default behavior of auto-creating a directory at a bind-mount source path that does not exist. The artifact was a 0-byte empty directory at `C:/Users/UserL/source/repos/SK_P/prometheus.yml/` left over from that compose-up failure. Plan 11-02 SUMMARY explicitly documented this would need cleanup before Plan 11-04 could create the file (`failed to mount "/path/prometheus.yml": not a directory` was the symptom; the resolution Plan 11-02 SUMMARY anticipated was "delete the directory then have Plan 11-04 land the file"). The Write tool initially failed with `EISDIR: illegal operation on a directory, read 'C:\\Users\\UserL\\source\\repos\\SK_P\\prometheus.yml'` because the path resolved to a directory.
- **Fix:** Ran `rmdir prometheus.yml` to remove the empty directory; verified absence via `ls prometheus.yml` returning "No such file or directory"; then Write succeeded.
- **Files modified:** N/A (filesystem cleanup; no source file mutation)
- **Verification:** `ls prometheus.yml` reported "No such file or directory" before the Write; after Write, the file exists and is readable.
- **Committed in:** N/A (filesystem cleanup is not a source-file change; the subsequent Write commit at `b40299c` captures the new file but not the directory deletion)

**2. [Rule 3 - Plan command shape mismatch] Used `docker compose up -d prometheus` instead of plan-as-written `restart prometheus`**

- **Found during:** Task 2 (Smoke-restart prometheus + confirm scrape succeeds)
- **Issue:** Plan-as-written Task 2 step 1 said `docker compose -f compose.yaml restart prometheus`. At plan-start, the prometheus service had never been started. Plan 11-02's compose-up was deferred at the bind-mount chicken-and-egg checkpoint; Plan 11-03 used a partial-stack bring-up of only `elasticsearch` + `otel-collector` (deliberately skipping prometheus + baseapi-service because prometheus.yml did not exist yet). So at this plan's start, `docker compose ps` showed only `sk-elasticsearch` (Up healthy) + `sk-otel-collector` (Up) — no `sk-prometheus` container. `docker compose restart` on a never-started service is a no-op / undefined behavior in compose v2.
- **Fix:** Used `docker compose -f compose.yaml up -d prometheus` instead (canonical first-start command). Functionally equivalent for the verification intent (config parses + container starts + scrape succeeds). Plan 11-03 established the partial-stack-bring-up precedent for this exact situation.
- **Files modified:** N/A (compose command shape change; no source file mutation)
- **Verification:** Command exits 0; container starts cleanly; logs show "Server is ready to receive web requests"; subsequent `/api/v1/targets` + `up{job="otel-collector"}` smoke probes succeed.
- **Committed in:** N/A (command shape change; verification-only step produces no commit)

**3. [Rule 1 - Plan-internal-inconsistency] Negative-assertion comments preserved verbatim despite literal grep gate**

- **Found during:** Task 1 (Create prometheus.yml — running the plan's verify gates)
- **Issue:** The plan's `<verify><automated>` gate at line 147 includes `! grep -E "basic_auth|bearer_token|tls_config|metric_relabel_configs" prometheus.yml (no auth blocks, no relabel configs)`. However, the plan's `<action>` body at lines 124-138 prescribes verbatim file content containing three inline negative-assertion comments: `# NO authentication — dev mode`, `# NO tls_config — dev mode`, `# No metric_relabel_configs — we want raw OTel → Prometheus name translation`. The latter two contain `tls_config` and `metric_relabel_configs` literal substrings in comment text. The plan's verbatim content thus structurally contradicts its own literal grep gate. The must_haves invariants are SEMANTIC ("NO authentication blocks (basic_auth, bearer_token, tls_config) — dev posture per D-08" + "NO metric_relabel_configs — raw OTel → Prometheus name translation"), describing the absence of FUNCTIONAL YAML keys, not the absence of comment text mentioning them.
- **Fix:** Followed the plan's `<action>` verbatim content (kept the negative-assertion comments). Confirmed the SEMANTIC invariant via `grep -vE "^\\s*#" prometheus.yml | grep -E "basic_auth|bearer_token|tls_config|metric_relabel_configs"` returning EXIT:1 — no functional YAML key declarations of any of those blocks exist; only the negative-assertion comments mention them. This matches the must_haves invariant intent. Plan 06-01 + Plan 08-01 + Plan 10-02 set the precedent: when a plan's verify-gate grep conflicts with its own `<action>` verbatim content, the `<action>` content takes precedence and the SUMMARY documents the tension.
- **Files modified:** `prometheus.yml` (the negative-assertion comments are part of the verbatim content; not a separate fix)
- **Verification:** Functional-block check `grep -vE "^\\s*#" prometheus.yml | grep -E "basic_auth|bearer_token|tls_config|metric_relabel_configs"` returns EXIT:1 (no functional YAML key declarations); all 11 positive verify gates pass (Phase 11 D-08 comment header / global block / scrape_interval / scrape_timeout / scrape_configs / job_name / target / metrics_path).
- **Committed in:** `b40299c` (file content is the plan-verbatim content)

---

**Total deviations:** 3 auto-fixed (2 Rule 3 blocking-fixes; 1 Rule 1 plan-internal-inconsistency)
**Impact on plan:** All three deviations preserve plan intent. The two Rule 3 fix-forwards (empty-directory cleanup + up-vs-restart command shape) are cross-plan sequencing artifacts that Plan 11-02 SUMMARY already anticipated. The Rule 1 plan-internal-inconsistency follows the Phase 6/8/10 precedent (plan `<action>` verbatim content takes precedence over literal grep gates; SUMMARY documents the tension). No scope creep; no file content deviates from plan-as-written.

## Issues Encountered

- **Pre-existing empty directory `prometheus.yml/`** at the bind-mount source path. Plan 11-02's deferred compose-up created it; this plan's first Write attempt failed with `EISDIR`. Resolved cleanly via `rmdir prometheus.yml` then re-Write. Plan 11-02 SUMMARY anticipated this would need cleanup; documented as Rule 3 deviation above.
- **Prometheus service never started before this plan** (Plan 11-02 deferred compose-up; Plan 11-03 partial-stack bring-up of ES + collector only). Plan-as-written `restart` command was inappropriate for a never-started service. Resolved via `up -d` instead. Documented as Rule 3 deviation above.
- **Negative-assertion-comments vs. literal grep gate tension** in plan body. Resolved by treating must_haves invariants as semantic (no functional YAML keys) rather than literal (no occurrences of the strings). Documented as Rule 1 deviation above. Plan 06-01 + 08-01 + 10-02 precedent followed.

## Self-Check: PASSED

**File existence verification:**
- FOUND: `prometheus.yml` (NEW at sk_p repo root — 30 lines, 30 insertions at commit b40299c)
- FOUND: `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-04-SUMMARY.md` (this file)

**Commit verification:**
- FOUND: `b40299c` (subject: `feat(prometheus): add scrape config for otel-collector:8889 (verbatim from sk2_1)`)
- `git show --stat HEAD` lists exactly 1 file added (prometheus.yml; 30 insertions)
- `git diff --diff-filter=D HEAD~1 HEAD` empty (no accidental file deletions)

**Plan-level grep gates (all PASS at commit b40299c):**
- `test -f prometheus.yml` exits 0 ✓
- `grep "Phase 11 D-08" prometheus.yml` returns 1 ✓
- `grep -c "Phase 11 D-07" prometheus.yml` returns 2 (at least 1 required) ✓
- `grep -E "^global:$" prometheus.yml` returns 1 ✓
- `grep "scrape_interval: 15s" prometheus.yml` returns 1 ✓
- `grep "scrape_timeout: 10s" prometheus.yml` returns 1 ✓
- `grep -E "^scrape_configs:$" prometheus.yml` returns 1 ✓
- `grep "job_name: 'otel-collector'" prometheus.yml` returns 1 ✓
- `grep "'otel-collector:8889'" prometheus.yml` returns 1 ✓
- `grep "metrics_path: '/metrics'" prometheus.yml` returns 1 ✓
- `! grep -vE "^\s*#" prometheus.yml | grep -E "basic_auth|bearer_token|tls_config|metric_relabel_configs"` (no FUNCTIONAL auth/TLS/relabel YAML keys — semantic invariant) ✓

**Backend smoke verification (Task 2 gates — all PASS):**
- `docker compose -f compose.yaml up -d prometheus` exits 0 ✓
- `docker logs --tail 60 sk-prometheus 2>&1 | grep "Server is ready to receive web requests"` returns 1 ✓
- `docker logs --tail 60 sk-prometheus 2>&1 | grep -iE "error.*parsing YAML|invalid configuration"` returns nothing (EXIT:1) ✓
- `curl -fs http://localhost:9090/api/v1/targets` exits 0 with `"health":"up"` for the otel-collector target ✓
- `curl -fs "http://localhost:9090/api/v1/query?query=up{job=\"otel-collector\"}"` returns a non-empty result vector with value `"1"` ✓

**Plan success_criteria coverage (all PASS at commit b40299c):**
- #1 `prometheus.yml` exists at the sk_p repo root (NOT in `compose/`) ✓
- #2 Body is verbatim from sk2_1 with comment header rewritten to reference Phase 11 D-07 + D-08 ✓
- #3 Single scrape_configs job `otel-collector` with one static target `otel-collector:8889` on metrics_path `/metrics` ✓
- #4 No authentication / TLS / metric_relabel_configs blocks (dev posture per D-08) — semantic invariant satisfied; comments-only mentions are educational ✓
- #5 After Prom container restart, logs show "Server is ready to receive web requests" with no config parse errors ✓
- #6 After one scrape cycle (~15s), `curl http://localhost:9090/api/v1/targets` shows the otel-collector job with `"health":"up"` ✓
- #7 Sample query `up{job="otel-collector"}` returns a result vector confirming Prom successfully scraped the collector ✓
- #8 Single git commit `feat(prometheus): add scrape config for otel-collector:8889 (verbatim from sk2_1)` exists at HEAD; modifies exactly 1 new file (prometheus.yml); working tree clean post-commit (tracked files clean; pre-existing untracked items not in scope) ✓

## User Setup Required

None — single-file commit + smoke verification. No external service configuration required at this step. The orchestrator will run the deferred Plan 11-02 Task 5 backend smoke probe sequence (compose up -d --wait + ES `/_cluster/health` + Prom `/-/healthy` + collector `:8889/metrics` + collector `:13133/` curls) after this plan completes, per the orchestrator's stated intent at the Plan 11-02 deferral checkpoint.

## Next Phase Readiness

**Plan 11-02 deferred Task 5 backend smoke verification is unblocked.** The orchestrator can now run the full sequence:
- `docker compose -f compose.yaml up -d --wait --timeout 120` should exit 0 (all 5 services reach healthy: postgres + elasticsearch + otel-collector + prometheus + baseapi-service depends on the others)
- `curl http://localhost:9200/_cluster/health` should return status: green or yellow
- `curl http://localhost:9090/-/healthy` should return 200 with "Prometheus Server is Healthy"
- `curl http://localhost:8889/metrics` should return Prom-text-format metric output (collector's prometheus exporter)
- `curl http://localhost:13133/` should return `{"status":"Server available",...}` (collector's health_check extension)

**Plan 11-05 (Program.cs/SDK strip)** is unblocked: the entire Phase 11 backend infrastructure (compose stack + collector config + prometheus.yml) is now in place. Plan 11-05's SDK-side `.WithTracing()` removal + traces-pipeline-already-removed (Plan 11-03 D-03) creates the consumer side of OBSERV-12's supersedure. Once Plan 11-05 lands, the sk-api service can be brought up and the full SDK → collector → ES + Prom round-trip is live.

**Plans 11-06+ (test migration + helpers + E2E tests)** are unblocked at infrastructure level: the 4-backend stack (ES + Prom + collector + baseapi-service) is fully wired; host-port mappings (`9200` + `9090` + `8889` + `13133`) are all reachable; compose-internal DNS (`elasticsearch:9200`, `prometheus:9090`, `otel-collector:8889`) is live for in-container test consumers.

**Phase 11 backend stack now fully wired end-to-end:** SDK (Phase 5 + Plan 11-05 traces-strip) emits OTLP → collector (Plan 11-03 config) ships logs to ES + metrics to Prom-exporter on `:8889` → Prom (Plan 11-02 service + Plan 11-04 scrape config) scrapes the collector every 15 s → ES + Prom HTTP APIs (host ports 9200 + 9090) available for test polling. The forensic property holds: each Phase 11 commit (7041adb, a3c0b20, 1f8eb69, b40299c) is independently revertable.

---
*Phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack*
*Plan: 04*
*Completed: 2026-05-28*
