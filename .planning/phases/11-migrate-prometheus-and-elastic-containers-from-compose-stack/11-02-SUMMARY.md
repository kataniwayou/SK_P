---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
plan: 02
subsystem: infra
tags: [compose, docker, elasticsearch, prometheus, otel-collector, dev-stack, image-pin, healthcheck, depends-on]

# Dependency graph
requires:
  - phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
    provides: Plan 11-01 amended REQUIREMENTS.md (commit 7041adb) — INFRA-06 extended + INFRA-08 introduced + OBSERV-12 superseded; this plan implements the compose-stack shape declared by INFRA-08 + INFRA-06's Phase 11 amendment clauses
  - phase: 02-postgres-and-docker-compose
    provides: postgres service block + named pgdata volume + baseapi-service depends_on skeleton + compose.yaml at repo root — extended in place this plan
  - phase: 05-observability-and-health-probes
    provides: otel-collector service block + collector-config bind-mount + distroless NOTE block + ports 4317/4318/13133 + tests/.otel-out file-exporter UID workaround (user:0:0 override) — this plan bumps the image to 0.152.0, adds port 8889, and drops the file-exporter workaround
  - phase: 08-entity-build-out-migrations-docker-runtime-tests
    provides: baseapi-service block with build:./Dockerfile + env vars + healthcheck + depends_on chain (postgres + otel-collector) — extended in place this plan with elasticsearch + prometheus entries
provides:
  - compose.yaml declares 5 services in postgres -> elasticsearch -> otel-collector -> prometheus -> baseapi-service order (4 backend services + the app)
  - New elasticsearch service block — image docker.elastic.co/elasticsearch/elasticsearch:8.15.5, container_name sk-elasticsearch, single-node + no auth + no TLS + 512m heap dev posture, port 9200:9200 only (9300 not exposed), curl healthcheck wait_for_status=yellow with start_period 60s per RESEARCH Pitfall 6 (verbatim from sk2_1)
  - New prometheus service block — image prom/prometheus:v3.11.3, container_name sk-prometheus, command --config.file + --web.enable-lifecycle, bind-mount ./prometheus.yml:/etc/prometheus/prometheus.yml:ro (file created by Plan 11-04), port 9090:9090, wget healthcheck on /-/healthy, depends_on otel-collector service_started (verbatim from sk2_1)
  - Mutated otel-collector service — image bumped 0.95.0 -> 0.152.0 (D-09 — 0.152.0 gives current mapping.mode semantics for the native ES exporter); port 8889:8889 added to ports list (D-14 Prom-scrape endpoint exposed to host); ./tests/.otel-out:/var/otel-out bind-mount + user:0:0 override DROPPED (D-14 — file exporter goes away in Plan 11-03 rewire, UID workaround obsolete); collector-config bind-mount byte-preserved; distroless NOTE block preserved (RESEARCH Pitfall 3 — 0.152.0 image is still distroless)
  - Extended baseapi-service.depends_on — gained elasticsearch + prometheus entries (both gated by service_healthy per D-15) alongside existing postgres: service_healthy + otel-collector: service_started; final 4-entry chain in postgres -> otel-collector -> elasticsearch -> prometheus order
  - Single atomic compose-only commit (a3c0b20) — commit #2 of the Phase 11 sequence, modifies exactly 1 file (compose.yaml), 71 insertions / 15 deletions; offline `docker compose config --quiet` validation passes
affects: [11-03 (collector config rewire — references new image 0.152.0 + the elasticsearch/prometheus exporter targets via compose-internal DNS), 11-04 (prometheus.yml creation — resolves the bind-mount declared this plan), 11-05+ (test migration — Phase11WebAppFactory references the 4-backend stack shape declared here), 11-06+ (helpers + E2E tests — host-side curls against 9200/9090/8889/13133 land on the host-port mappings declared this plan)]

# Tech tracking
tech-stack:
  added:
    - "docker.elastic.co/elasticsearch/elasticsearch:8.15.5 — net-new container in the sk_p compose stack; dev posture (single-node, no auth, no TLS, ephemeral)"
    - "prom/prometheus:v3.11.3 — net-new container in the sk_p compose stack; dev posture (no remote_write, no recording/alerting rules, ephemeral)"
  patterns:
    - "Image-bump-with-comment-block convention — bumped otel-collector-contrib 0.95.0 -> 0.152.0 with inline rationale comment block citing Phase 11 D-09 + the load-bearing reason (current `mapping.mode` semantics + native ES + Prom exporters)"
    - "Bind-mount-precedes-file-creation pattern — compose.yaml declares ./prometheus.yml bind-mount as commit #2 of the phase; the file it mounts is created as commit #3 (Plan 11-04). Backend smoke verification is therefore DEFERRED to post-Wave-3 (cannot pass at Plan 11-02 alone). Pattern documented in this SUMMARY's `Notes` section for future phases."
    - "Verbatim-from-sibling-repo pattern — both elasticsearch + prometheus service blocks are byte-for-byte copies of sk2_1's docker-compose.yml entries (lines 41-72 + 77-100 respectively), with only the comment headers adapted to reference Phase 11 D-NN markers. Pattern preserved verbatim across plans 11-03 (collector config) and 11-04 (prometheus.yml) where sk2_1's existing artifacts are the canonical reference."
    - "Container-name-collision-mutually-exclusive pattern (Pitfall 4) — container_name literals (sk-elasticsearch, sk-prometheus, sk-otel-collector) are deliberately reused from sk2_1, making the two stacks mutually exclusive on a single Docker daemon. Pre-flight check documented in Task 5 + observed-and-resolved at the checkpoint attempt."

key-files:
  created: []
  modified:
    - "compose.yaml — 71 insertions / 15 deletions: added elasticsearch service block (D-10/D-12 verbatim from sk2_1 + Pitfalls 4/6 comment block); mutated otel-collector service block (image bump 0.95.0 -> 0.152.0, port 8889 added, ./tests/.otel-out bind-mount + user:0:0 override dropped, distroless NOTE preserved); added prometheus service block (D-11/D-13 verbatim from sk2_1 + Pitfalls 3/4 comment block); extended baseapi-service.depends_on with elasticsearch + prometheus entries gated by service_healthy (D-15)"

key-decisions:
  - "Backend smoke verification at Task 5 checkpoint was DEFERRED to post-Wave-3 per orchestrator instruction. Plan 11-02 declares ./prometheus.yml:/etc/prometheus/prometheus.yml:ro bind-mount on the new prometheus service, but Plan 11-04 (Wave 3) is responsible for creating that file. Attempted compose-up at the checkpoint failed deterministically with `failed to mount ... prometheus.yml: not a directory: Are you trying to mount a directory onto a file (or vice-versa)?` because Docker auto-creates a directory when the host path does not exist. Full backend smoke probe sequence (compose up --wait + ES + Prom + collector :8889/metrics + collector :13133 host-side curls per Task 5 steps 3-5) will run from the orchestrator after Plan 11-04 lands prometheus.yml. The offline `docker compose -f compose.yaml config --quiet` validation gate DID pass at commit time (exit 0)."
  - "Container-name collision with sibling sk2_1 stack (RESEARCH Pitfall 4) WAS observed at the Task 5 pre-flight check and resolved cleanly: `docker compose -f C:/Users/UserL/source/repos/sk2_1/docker-compose.yml down` freed sk-elasticsearch + sk-prometheus + sk-mongodb; `docker compose -f compose.yaml down` (old 0.95.0 collector + postgres state) was also brought down before the up attempt. Pattern proven; future Phase 11 plans + the post-Wave-3 verification can apply the same teardown sequence."
  - "Atomic-commit contract honored — single compose.yaml-only commit (a3c0b20) modifies exactly 1 file with 71/15 lines insert/delete. Forensic property: commit is independently revertable; later plans (11-03 collector config, 11-04 prometheus.yml, 11-05+ tests) each ship as their own atomic commits. Bisect-friendly per Phase 10 D-02 precedent."
  - "Image bump 0.95.0 -> 0.152.0 inline rationale — the bump is load-bearing because 0.95.0 predates the `mapping.mode` field on the native elasticsearch exporter (D-09); without the bump, Plan 11-03's collector-config rewire to `mapping.mode: none` would fail to load. The bump comment block in compose.yaml lines 51-56 documents this dependency inline for future readers."
  - "user:0:0 + ./tests/.otel-out bind-mount BOTH dropped together (D-14) — the file-exporter UID-mismatch Windows workaround was a Phase 5 Plan 05-01 deviation. With the file exporter going away in Plan 11-03's collector-config rewire, the entire workaround is obsolete. Removing both lines together (instead of in two commits) keeps the collector service block consistent at every commit-state — the workaround was only meaningful when paired with the bind-mount it was working around."
  - "elasticsearch + prometheus depends_on gating chosen as service_healthy (NOT service_started) per D-15 — both backends declare healthchecks (ES curl /_cluster/health?wait_for_status=yellow; Prom wget /-/healthy), so baseapi-service waits for actual readiness rather than just process-up. The 60s ES start_period (Pitfall 6) means compose-up blocks ~60-90s on a cold dev host; acceptable per sk2_1 precedent + D-15."

patterns-established:
  - "Bind-mount-precedes-file-creation deferral pattern — when an atomic compose-mutation commit declares a bind-mount whose host-side file is created by a later plan, the compose-up smoke verification gate is DEFERRED to post-file-creation (not failed at the early commit). The offline `docker compose config --quiet` gate still runs and is sufficient for the atomic commit to land. The deferred verification runs from the orchestrator after the file-creating plan ships."
  - "Image-bump rationale-comment-block convention — every image-version bump in compose.yaml is paired with an inline comment block citing the Phase D-NN decision + the load-bearing reason for the bump (e.g., `0.95.0 predates mapping.mode field`). Future readers can grep `Phase NN D-MM — bumped` to find every image bump's rationale without leaving the file."
  - "Pre-flight collision check at checkpoint (Pitfall 4) — `docker ps --filter name=...` + `docker ps --filter publish=...` BEFORE compose-up to detect sibling-stack ownership of container names or host ports. Mutually-exclusive stacks (sk_p vs sk2_1 here) require teardown of the holding stack first; the check is part of the Wave-0 ritual any time a new service slots into the stack."

requirements-completed: [INFRA-06, INFRA-08]
# INFRA-06 (Phase 11 amendment) — compose-stack declarations for 4 services + extended baseapi-service.depends_on chain — IMPLEMENTED in compose.yaml this plan
# INFRA-08 (new this phase) — per-service shape detail (elasticsearch image + env + port + healthcheck; prometheus image + command + bind-mount + port + healthcheck; otel-collector image bump + port 8889 addition + bind-mount/user override removal) — IMPLEMENTED in compose.yaml this plan
# Note: INFRA-06 + INFRA-08 are STRUCTURALLY complete (compose.yaml declares the shape, offline `docker compose config --quiet` validates). Behavioral verification (compose up --wait reaches all-healthy in <120s + backend smoke probes) is DEFERRED to post-Wave-3 per orchestrator instruction (prometheus.yml dependency chicken-and-egg with Plan 11-04). The deferred verification will run from the orchestrator after Plan 11-04 lands; if it fails, this SUMMARY will be amended with a fix-forward deviation entry.

# Metrics
duration: ~15min
completed: 2026-05-28
---

# Phase 11 Plan 02: Compose-Stack Backend Mutation Summary

**Single atomic compose.yaml commit lands the Phase 11 4-backend dev stack — adds elasticsearch (8.15.5) + prometheus (v3.11.3) service blocks verbatim from sk2_1, bumps otel-collector-contrib to 0.152.0 with port 8889 + dropped file-exporter UID workaround, and extends baseapi-service.depends_on to gate on all 4 healthchecked backends.**

## Performance

- **Duration:** ~15 min (Tasks 1-4 sequential mutations + Task 5 checkpoint attempt + collision-clear teardowns + Task 6 atomic commit)
- **Started:** 2026-05-28T11:54Z (immediately after Plan 11-01 commit landed)
- **Completed:** 2026-05-28T12:10Z (Task 6 commit a3c0b20)
- **Tasks:** 6 (5 sequential mutations + 1 deferred-checkpoint + 1 atomic commit; Task 5 was DEFERRED per orchestrator instruction)
- **Files modified:** 1 (`compose.yaml`)

## Accomplishments

- **elasticsearch service block added** between postgres and otel-collector — image `docker.elastic.co/elasticsearch/elasticsearch:8.15.5`, container_name `sk-elasticsearch`, 4 env vars (`discovery.type=single-node`, `xpack.security.enabled=false`, `xpack.security.enrollment.enabled=false`, `ES_JAVA_OPTS=-Xms512m -Xmx512m`), port `9200:9200`, healthcheck `curl -fs 'http://localhost:9200/_cluster/health?wait_for_status=yellow&timeout=5s' || exit 1` with `start_period: 60s`. Verbatim from sk2_1 docker-compose.yml lines 41-72 with comment header adapted to cite Phase 11 D-10/D-12 + RESEARCH Pitfalls 4/6.
- **otel-collector service mutated** — image bumped from `otel/opentelemetry-collector-contrib:0.95.0` to `0.152.0` with inline rationale comment block (D-09 — 0.152.0 gives current `mapping.mode` semantics for the native ES + Prom exporters); port `8889:8889` added to the ports list between `4318:4318` and `13133:13133` (D-14 — Prom-scrape endpoint exposed to host); `./tests/.otel-out:/var/otel-out` bind-mount DROPPED; `user: "0:0"` override DROPPED (D-14 — file exporter goes away in Plan 11-03, UID workaround obsolete). `./compose/otel-collector-config.yaml:/etc/otel-collector-config.yaml:ro` bind-mount preserved byte-identical. Distroless NOTE block preserved (RESEARCH Pitfall 3 — 0.152.0 image is still distroless; opening line updated to `Phase 5 Plan 05-01 deviation + Phase 11 0.152.0 verification`).
- **prometheus service block added** between otel-collector and baseapi-service — image `prom/prometheus:v3.11.3`, container_name `sk-prometheus`, command `--config.file=/etc/prometheus/prometheus.yml --web.enable-lifecycle`, bind-mount `./prometheus.yml:/etc/prometheus/prometheus.yml:ro` (file created by Plan 11-04), port `9090:9090`, wget healthcheck on `/-/healthy`, depends_on `otel-collector: service_started` per D-13. Verbatim from sk2_1 docker-compose.yml lines 77-100 with comment header adapted to cite Phase 11 D-11/D-13 + RESEARCH Pitfalls 3/4.
- **baseapi-service.depends_on extended** with 2 new entries — `elasticsearch: condition: service_healthy` + `prometheus: condition: service_healthy` appended after the existing `postgres: service_healthy` + `otel-collector: service_started` entries. Final 4-entry chain in `postgres -> otel-collector -> elasticsearch -> prometheus` depends-on order with consistent 6-space service-name + 8-space condition indentation matching the existing pattern (D-15).
- **Offline config validation** — `docker compose -f compose.yaml config --quiet` exits 0 (YAML valid; resolved service graph parses across all 5 services + 1 named volume).
- **Single atomic commit** `a3c0b20` with verbatim subject `feat(compose): add elasticsearch + prometheus services; bump otel-collector to 0.152.0; extend baseapi-service depends_on chain` modifying exactly 1 file (`compose.yaml`); 71 insertions / 15 deletions; no accidental file deletions (`git diff --diff-filter=D HEAD~1 HEAD` empty).

## Task Commits

Per Plan 11-02's atomic-commit contract (success criteria #9), this plan ships as ONE atomic compose-only commit. All file mutations from Tasks 1-4 are sub-edits of a single forensic-friendly commit; Task 5 is a verification-only checkpoint that produces no commit; Task 6 is the single commit point.

1. **Task 1: Add elasticsearch service block between postgres and otel-collector** — uncommitted at task boundary (rolled into Task 6 commit per atomic-compose-commit contract)
2. **Task 2: Mutate otel-collector service per D-09 + D-14 (image bump + port 8889 + drop tests/.otel-out + drop user:0:0 + distroless NOTE preserved)** — uncommitted at task boundary (rolled into Task 6 commit)
3. **Task 3: Add prometheus service block between otel-collector and baseapi-service** — uncommitted at task boundary (rolled into Task 6 commit)
4. **Task 4: Extend baseapi-service.depends_on with elasticsearch + prometheus entries** — uncommitted at task boundary (rolled into Task 6 commit)
5. **Task 5: Wave-0 host-port + container-name collision check + compose-up smoke (manual checkpoint)** — DEFERRED to post-Wave-3 per orchestrator instruction (see `Deviations from Plan` below). Pre-flight collision check WAS executed and resolved (sibling sk2_1 stack brought down; old sk_p stack also brought down). Offline `docker compose config --quiet` validation gate DID pass. The compose-up smoke + backend probes (steps 3-5 of Task 5) are deferred because the prometheus service's `./prometheus.yml` bind-mount cannot resolve until Plan 11-04 creates the file.
6. **Task 6: Commit compose.yaml mutation as commit #2 of the Phase 11 sequence** — `a3c0b20` (feat)

**Plan metadata:** TBD — committed by execute-plan agent after SUMMARY + STATE updates.

_Note: Plan 11-02 deliberately ships as ONE atomic compose-only commit per success criteria #9 ("Single git commit `feat(compose): ...` exists at HEAD; modifies exactly 1 file (compose.yaml)"). Tasks 1-4 are sequential file mutations; Task 5 is verification-only; Task 6 is the single commit point. Same atomic-commit pattern as Plan 11-01._

## Files Created/Modified

- `compose.yaml` — 71 insertions / 15 deletions. Single source-of-truth Docker Compose declaration for the sk_p dev stack, now declaring 5 services (postgres + elasticsearch + otel-collector + prometheus + baseapi-service) in dep-on order. Adds elasticsearch + prometheus service blocks verbatim from sk2_1 with Phase 11 D-NN-cited comment headers; mutates otel-collector (image bump 0.95.0 -> 0.152.0; port 8889 added; tests/.otel-out bind-mount + user:0:0 override dropped; collector-config bind-mount + distroless NOTE preserved); extends baseapi-service.depends_on to 4 entries (postgres + otel-collector + elasticsearch + prometheus with service_healthy / service_started gating per D-15).

## Decisions Made

All decisions inherited verbatim from Phase 11 CONTEXT.md D-09 through D-15. The only execution-time judgment call is the Task 5 deferral, captured below in `Deviations from Plan`.

## Deviations from Plan

### Deferred Verification (orchestrator-directed)

**1. [Orchestrator-directed deferral — NOT a deviation rule classification] Task 5 backend smoke probes deferred to post-Wave-3**

- **Found during:** Task 5 (Wave-0 host-port + container-name collision check + compose-up smoke checkpoint)
- **Issue:** Plan 11-02's new `prometheus` service declares `./prometheus.yml:/etc/prometheus/prometheus.yml:ro` bind-mount (line 94 in the post-commit compose.yaml), but the host-side `./prometheus.yml` file does not yet exist — Plan 11-04 (Wave 3) is responsible for creating it. When the orchestrator attempted `docker compose -f compose.yaml up -d --wait` at the Task 5 checkpoint, Docker auto-created a `prometheus.yml` directory (Docker's default behavior when a host-bind-mount source path does not exist) and prometheus failed to start with: `failed to mount "/path/prometheus.yml": not a directory: Are you trying to mount a directory onto a file (or vice-versa)? Check if the specified host path exists`.
- **Resolution:** User responded "skip-verify, commit anyway"; orchestrator confirmed the full backend smoke probe sequence (Task 5 steps 3-5 — `docker compose up -d --wait --timeout 120` + ES `/_cluster/health` curl + Prom `/-/healthy` curl + collector `:8889/metrics` curl + collector `:13133/` curl) will run AFTER Plan 11-04 lands prometheus.yml.
- **Resolution-time work that DID complete:**
  - Pre-flight collision check (Task 5 step 1) DID execute — sibling sk2_1 stack was holding `sk-elasticsearch` + `sk-prometheus` + `sk-mongodb` container names; `docker compose -f C:/Users/UserL/source/repos/sk2_1/docker-compose.yml down` cleared them cleanly.
  - Old sk_p stack (0.95.0 collector + postgres) was also brought down (`docker compose -f compose.yaml down`) to free `sk-otel-collector` container name + the postgres state from the prior compose graph.
  - Offline config validation (Task 5 step 2) DID execute and pass — `docker compose -f compose.yaml config --quiet` exits 0 (YAML valid; resolved 5-service graph parses).
- **Files modified:** None (verification-only checkpoint produces no file changes).
- **Committed in:** N/A (verification step; the Task 6 commit subsumes Tasks 1-4 file mutations only).
- **Classification rationale:** This is NOT a Rule 1/2/3 auto-fix or a Rule 4 architectural change. It is an orchestrator-directed defer-and-continue decision based on the inherent chicken-and-egg between Plan 11-02 (declares the bind-mount) and Plan 11-04 (creates the bound file). Plan 11-02's atomic-commit contract is preserved; the deferred verification will run when the file-creating plan ships.

---

**Total deviations:** 0 auto-fixed; 1 verification-deferred-by-orchestrator (Task 5 backend smoke probes)
**Impact on plan:** Atomic compose-only commit lands per spec; offline `docker compose config --quiet` validation passes; backend behavioral verification deferred to post-Wave-3. No scope creep; no file content deviates from plan-as-written.

## Issues Encountered

- **Sibling-stack container_name collision** — sk2_1 stack was holding `sk-elasticsearch` + `sk-prometheus` + `sk-mongodb` at Task 5 pre-flight. Resolved cleanly with `docker compose -f C:/Users/UserL/source/repos/sk2_1/docker-compose.yml down` (RESEARCH Pitfall 4 mutually-exclusive-stacks-on-single-daemon pattern). Documented as expected behavior in Task 5's how-to-verify block; not a deviation.
- **Old sk_p stack still up** — `sk-otel-collector` (0.95.0) + `sk-postgres` were holding their container names from a prior compose-up. Resolved with `docker compose -f compose.yaml down`. Expected behavior; not a deviation.
- **Prometheus bind-mount chicken-and-egg** — see `Deviations from Plan` above (Task 5 deferral); not strictly an issue, more an inter-plan sequencing detail that the user + orchestrator resolved by choosing "skip-verify, commit anyway."

## Self-Check: PASSED

**File existence verification:**
- FOUND: `compose.yaml` (modified — 71 +/15 - at commit a3c0b20)
- FOUND: `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-02-SUMMARY.md` (this file)

**Commit verification:**
- FOUND: `a3c0b20` (subject: `feat(compose): add elasticsearch + prometheus services; bump otel-collector to 0.152.0; extend baseapi-service depends_on chain`)
- `git show --stat HEAD` lists exactly 1 file changed (compose.yaml)
- `git diff --diff-filter=D HEAD~1 HEAD` empty (no accidental file deletions)

**Plan-level grep gates (all PASS at commit a3c0b20):**
- `grep -E "^  elasticsearch:$" compose.yaml` returns 1 ✓
- `grep "docker.elastic.co/elasticsearch/elasticsearch:8.15.5" compose.yaml` returns 1 ✓
- `grep "container_name: sk-elasticsearch" compose.yaml` returns 1 ✓
- `grep "discovery.type=single-node" compose.yaml` returns 1 ✓
- `grep "xpack.security.enabled=false" compose.yaml` returns 1 ✓
- `grep "xpack.security.enrollment.enabled=false" compose.yaml` returns 1 ✓
- `grep "ES_JAVA_OPTS=-Xms512m -Xmx512m" compose.yaml` returns 1 ✓
- `grep "9200:9200" compose.yaml` returns 1 ✓
- `grep "wait_for_status=yellow" compose.yaml` returns 1 ✓
- `grep "start_period: 60s" compose.yaml` returns 1 ✓
- `grep "otel/opentelemetry-collector-contrib:0.152.0" compose.yaml` returns 1 ✓
- `! grep "otel/opentelemetry-collector-contrib:0.95.0" compose.yaml` (old image gone) ✓
- `! grep -E "^    user: \"0:0\"$" compose.yaml` (UID override removed) ✓
- `! grep "./tests/.otel-out:/var/otel-out" compose.yaml` (file exporter bind-mount removed) ✓
- `grep "./compose/otel-collector-config.yaml:/etc/otel-collector-config.yaml:ro" compose.yaml` returns 1 (collector-config bind-mount preserved) ✓
- `grep -c "8889:8889" compose.yaml` returns 1 ✓
- `grep -c "4317:4317" compose.yaml` returns 1 ✓
- `grep -c "4318:4318" compose.yaml` returns 1 ✓
- `grep -c "13133:13133" compose.yaml` returns 1 ✓
- `grep "Phase 11 D-09" compose.yaml` returns 1 ✓
- `grep "distroless" compose.yaml` returns at least 1 (Pitfall 3 stance preserved) ✓
- `grep -E "^  prometheus:$" compose.yaml` returns 1 ✓
- `grep "prom/prometheus:v3.11.3" compose.yaml` returns 1 ✓
- `grep "container_name: sk-prometheus" compose.yaml` returns 1 ✓
- `grep "./prometheus.yml:/etc/prometheus/prometheus.yml:ro" compose.yaml` returns 1 ✓
- `grep "9090:9090" compose.yaml` returns 1 ✓
- `grep "/-/healthy" compose.yaml` returns 1 ✓
- `grep "--web.enable-lifecycle" compose.yaml` returns 1 ✓
- `grep -A 12 -E "^    depends_on:$" compose.yaml | grep -E "^      (postgres|otel-collector|elasticsearch|prometheus):$" | wc -l` returns 4 (baseapi-service depends on all four backends) ✓
- `grep -E "^  (postgres|elasticsearch|otel-collector|prometheus|baseapi-service):$" compose.yaml | wc -l` returns 5 ✓
- `docker compose -f compose.yaml config --quiet` exits 0 ✓

**Plan success_criteria coverage (all PASS at commit a3c0b20 — except success criteria #7 + #8 which are DEFERRED):**
- #1 compose.yaml declares 5 services in dep-on order ✓
- #2 elasticsearch service shape ✓
- #3 otel-collector mutations ✓
- #4 prometheus service shape ✓
- #5 baseapi-service.depends_on 4 entries with service_healthy gating ✓
- #6 `docker compose config --quiet` exits 0 ✓
- #7 `docker compose up -d --wait --timeout 120` exits 0 within 120s — **DEFERRED to post-Wave-3 (prometheus.yml dependency)**
- #8 Backend smoke probes succeed — **DEFERRED to post-Wave-3 (prometheus.yml dependency)**
- #9 Single git commit; modifies exactly 1 file; working tree clean post-commit ✓

## User Setup Required

None — compose-only commit. No external service configuration required at this step. Backend behavioral verification (deferred per Task 5 deferral above) will be orchestrator-driven after Plan 11-04 lands prometheus.yml.

## Next Phase Readiness

Plan 11-03 (collector-config rewire) is unblocked: it can reference the new `otel/opentelemetry-collector-contrib:0.152.0` image (which gives current `mapping.mode` semantics for the native ES + Prom exporters per D-09) + the new `8889` port mapping (D-14) + the absence of the file-exporter UID workaround (file exporter goes away in 11-03; the workaround was already dropped this plan in anticipation).

Plan 11-04 (prometheus.yml creation) is unblocked: it can reference the bind-mount declared this plan (`./prometheus.yml:/etc/prometheus/prometheus.yml:ro`) and place its created file at the repo-root path the bind-mount expects. When 11-04 lands, the orchestrator will run the deferred Task 5 backend smoke probe sequence (per the orchestrator's stated intent at the deferral checkpoint).

Plans 11-05+ (test migration + helpers + E2E) can reference the 4-backend stack shape declared this plan: host-port mappings `9200` (ES) + `9090` (Prom) + `8889` (collector Prom-scrape) + `13133` (collector health_check) are all in place for host-side test polling; compose-internal DNS (`elasticsearch:9200`, `prometheus:9090`, `otel-collector:8889`) is available for in-container Phase11WebAppFactory use cases.

The forensic property holds: the compose stack is now ahead of the collector config (Plan 11-03) + prometheus.yml (Plan 11-04) + the code strip (Plan 11-04+) + the test migration (11-05+), and the single compose-only commit (a3c0b20) independently reverts without leaving the spec or any subsequent work out of sync.

---
*Phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack*
*Plan: 02*
*Completed: 2026-05-28*
