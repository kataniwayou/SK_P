---
phase: 19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier
plan: 04
subsystem: infra
tags: [rabbitmq, docker-compose, dockerfile, masstransit, healthcheck, orchestrator]

# Dependency graph
requires:
  - phase: 19-01
    provides: slim body-carried ICorrelated reconciliation (compile prerequisite for Orchestrator + WebApi bus streams)
  - phase: 19-02
    provides: runnable Orchestrator console (thin shell, instance-unique fan-out endpoint, /health/ready hard-on-broker)
  - phase: 19-03
    provides: WebApi publish-only bus join (Start/Stop publish, Degraded bus health)
provides:
  - rabbitmq:4.1.8-management-alpine compose service (sk-rabbitmq) with rabbitmq-diagnostics -q ping healthcheck
  - runnable orchestrator compose service (sk-orchestrator) built from src/Orchestrator/Dockerfile, hard-gated on rabbitmq + redis service_healthy
  - baseapi-service rabbitmq depends_on service_healthy + RabbitMq__* env (WebApi Start/Stop hard-dep)
  - src/Orchestrator/Dockerfile — net8.0 multi-stage build on the aspnet:8.0 runtime (with wget for the healthcheck)
  - first runnable Orchestrator stack verified live-healthy (D-09)
affects: [phase-20, correlation-proof, triple-sha-closeout, test-rmq]

# Tech tracking
tech-stack:
  added: [rabbitmq:4.1.8-management-alpine compose service, Orchestrator container image (sk_p-orchestrator)]
  patterns:
    - "Console-container healthcheck idiom: aspnet runtime ships no wget/curl — apt-get install wget (root, before USER app) to make the wget --spider /health/ready check exec"
    - "Fan-out broker tier: rabbitmq-diagnostics -q ping healthcheck + start_period 40s; dependents gate on condition: service_healthy"

key-files:
  created:
    - src/Orchestrator/Dockerfile
  modified:
    - compose.yaml

key-decisions:
  - "Orchestrator runtime image installs wget so the compose wget --spider /health/ready healthcheck can execute (aspnet:8.0-bookworm-slim ships neither wget nor curl)"
  - "Orchestrator /health/ready is hard-on-broker: depends_on rabbitmq service_healthy + the healthcheck enforce it; container goes healthy only after the bus starts"
  - "baseapi-service Start/Stop path is a hard-dep on a healthy broker (depends_on rabbitmq: service_healthy); CRUD readiness stays soft (Degraded) per MSG-WEBAPI-04"

patterns-established:
  - "Dotnet aspnet runtime container healthcheck requires explicit wget/curl install — the slim runtime images do not ship them"
  - "Compose-DNS host (rabbitmq) + guest/guest creds via __-delimited env for both runnable services; host-side 5673 offset is NOT used inside the network"

requirements-completed: [INFRA-RMQ-02, INFRA-RMQ-03]

# Metrics
duration: ~25min (incl. operator checkpoint + continuation fix)
completed: 2026-05-30
---

# Phase 19 Plan 04: RabbitMQ Tier + Runnable Orchestrator Stack Summary

**Live RabbitMQ compose tier + a runnable Orchestrator container (built from a net8.0 aspnet:8.0 multi-stage Dockerfile) + the WebApi Start/Stop broker hard-dep — the first runnable Orchestrator stack, verified live-healthy after a wget-in-runtime healthcheck fix.**

## Performance

- **Duration:** ~25 min (Tasks 1-2 by prior agent + operator checkpoint + continuation fix/re-verify)
- **Completed:** 2026-05-30
- **Tasks:** 3 (2 auto + 1 blocking human-verify checkpoint)
- **Files modified:** 2 (compose.yaml, src/Orchestrator/Dockerfile)

## Accomplishments
- `rabbitmq:4.1.8-management-alpine` service (sk-rabbitmq) with the `rabbitmq-diagnostics -q ping` healthcheck, host ports 5673:5672 + 15673:15672, guest/guest.
- A runnable `orchestrator` service (sk-orchestrator) built from `src/Orchestrator/Dockerfile`, hard-gated on rabbitmq + redis `service_healthy`, carrying RabbitMq__* + ConnectionStrings__Redis + Orchestrator__InstanceId env.
- The WebApi `baseapi-service` now hard-depends on a healthy broker for the Start/Stop path (`depends_on rabbitmq: service_healthy`) and carries RabbitMq__* env.
- `src/Orchestrator/Dockerfile` — net8.0 multi-stage build on the `aspnet:8.0-bookworm-slim` runtime (BaseConsole.Core's FrameworkReference Microsoft.AspNetCore.App needs the ASP.NET Core shared framework for the embedded Kestrel health listener); port 8081.
- **Live stack verified healthy:** sk-rabbitmq AND sk-orchestrator both report `healthy`; the orchestrator's instance-unique fan-out queues (`StartOrchestration` / `StopOrchestration` for `orchestrator-1`) bind on the broker.

## Task Commits

1. **Task 1: rabbitmq + baseapi depends_on/env + orchestrator service in compose.yaml** - `d624c6e` (feat)
2. **Task 2: src/Orchestrator/Dockerfile (net8.0 multi-stage, aspnet:8.0 runtime)** - `c3d3c30` (feat)
3. **Task 3: human-verify checkpoint (live stack health)** - operator-run; FAILED first (sk-orchestrator unhealthy), then PASSED after the deviation fix below.

**Deviation fix:** `e4fcf67` (fix) — install wget in the Orchestrator runtime image.
**In-progress marker (prior agent):** `b7553e0` (docs).

## Files Created/Modified
- `src/Orchestrator/Dockerfile` - net8.0 multi-stage build (sdk:8.0 build stage → aspnet:8.0 runtime), with `apt-get install wget` before `USER app` so the /health/ready healthcheck can exec.
- `compose.yaml` - rabbitmq service + orchestrator service + baseapi-service rabbitmq depends_on/env.

## Decisions Made
- **wget in the runtime image:** the established compose `["CMD", "wget", "--spider", ...]` healthcheck idiom requires wget present in the container; the slim aspnet runtime ships neither wget nor curl, so it must be apt-installed.
- **Orchestrator /health/ready hard-on-broker** was kept as-is — the depends_on + the (now-working) healthcheck together enforce that the container goes healthy only once the MassTransit bus connects.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Install wget in the Orchestrator runtime image so the /health/ready healthcheck can execute**
- **Found during:** Task 3 (blocking human-verify checkpoint — operator brought up the live stack).
- **Issue:** sk-orchestrator was marked UNHEALTHY even though the app started cleanly (logs: "Now listening on: http://0.0.0.0:8081", "Application started", bus connected). Root cause: `mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim` ships NEITHER wget NOR curl (verified empirically — `command -v wget`/`command -v curl` both absent). The compose healthcheck `["CMD", "wget", "--spider", "-q", "http://localhost:8081/health/ready"]` failed to exec (`exec: "wget": executable file not found in $PATH`), so Docker flagged the container unhealthy though /health/ready would return 200. The plan's acceptance criterion "the live stack comes up healthy" is unmeetable without this.
- **Fix:** Added a single clean apt layer in the runtime stage BEFORE `USER app` (apt-get needs root): `apt-get update && apt-get install -y --no-install-recommends wget && rm -rf /var/lib/apt/lists/*`. All other runtime lines (ASPNETCORE_URLS, EXPOSE 8081, ENTRYPOINT) left intact.
- **Files modified:** src/Orchestrator/Dockerfile
- **Verification:** Rebuilt with `docker compose up -d --build orchestrator`; polled health — both sk-rabbitmq and sk-orchestrator reported `healthy` within ~5s of the poll (start_period 30s elapsed during build). Health log shows `exit=0`. Fan-out queues bound on the broker.
- **Committed in:** `e4fcf67`

---

**Total deviations:** 1 auto-fixed (1 blocking, Rule 3)
**Impact on plan:** The fix is the minimal in-scope change (Phase 19 owns this Dockerfile) required to make the established healthcheck idiom work. No scope creep — the app behavior, ports, and entrypoint are unchanged.

## Checkpoint Evidence (Task 3, post-fix)

```
$ docker inspect --format '{{.State.Health.Status}}' sk-rabbitmq       → healthy
$ docker inspect --format '{{.State.Health.Status}}' sk-orchestrator   → healthy
$ docker inspect ... sk-orchestrator health log                        → exit=0
$ docker exec sk-rabbitmq rabbitmqctl list_queues name | grep orchestrator
  orchestrator
  StopOrchestrationorchestrator-1
  StartOrchestrationorchestrator-1
```

Stack torn down with `docker compose down` (no `-v` — volumes preserved; the triple-SHA leak gate is Phase 20 / TEST-RMQ-05).

Zero-warning build re-confirmed post-fix: `dotnet build SK_P.sln -c Release` → 0 Warning(s) / 0 Error(s) (the Dockerfile change does not affect the .NET build; confirmed nothing regressed).

## Findings (Out-of-Scope Tech Debt)

**baseapi-service healthcheck carries the identical latent defect.** `compose.yaml` line ~230 uses the same `["CMD", "wget", "--spider", "-q", "http://localhost:8080/health/ready"]` idiom, but `baseapi-service` is built from the **root `Dockerfile`** (out of Phase 19 scope) which also installs no wget/curl. Whenever that container is health-gated, Docker will mark it unhealthy the same way the orchestrator was — even though the app would return 200. This was NOT observed during this checkpoint because the operator's verification brought up only rabbitmq + orchestrator (baseapi-service was not health-gated in this run). Flagged for the user/verifier as out-of-scope tech debt: install wget in the root Dockerfile runtime stage (mirroring the e4fcf67 fix) in a later phase. NOT fixed here — Phase 19 does not own the root Dockerfile.

## Issues Encountered
- The blocking human-verify checkpoint failed on first operator run (orchestrator unhealthy) — root-caused by the operator to the missing wget binary, fixed in continuation (see Deviation 1).

## User Setup Required
None - no external service configuration required (dev-only guest/guest broker creds via compose env; documented dev-only posture per the threat register).

## Next Phase Readiness
- First runnable Orchestrator stack is live-healthy: broker tier + runnable container + WebApi broker hard-dep all in place. INFRA-RMQ-02/03 closed.
- Phase 19 execution complete (4/4 plans). Ready for Phase 20 (correlation-propagation proof, synthetic harness, triple-SHA closeout including the `rabbitmqctl list_queues` leak gate).
- **Carry-forward tech debt:** root Dockerfile baseapi-service healthcheck needs the same wget install (see Findings).

## Self-Check: PASSED

- FOUND: src/Orchestrator/Dockerfile
- FOUND: compose.yaml
- FOUND: 19-04-SUMMARY.md
- FOUND commits: d624c6e (Task 1), c3d3c30 (Task 2), e4fcf67 (fix)

---
*Phase: 19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier*
*Completed: 2026-05-30*
