---
phase: 12-redis-infra-composition-healthcheck-di-registration
plan: 02
subsystem: infra
tags: [redis, compose, healthcheck, docker, infra]

# Dependency graph
requires:
  - phase: 02-postgres-docker-compose
    provides: compose.yaml stack + D-01 5433:5432 collision-avoidance port-mapping precedent
  - phase: 11-migrate-prometheus-and-elasticsearch
    provides: sk-* container_name convention + healthcheck cadence template + baseapi-service depends_on chain
provides:
  - "sk-redis container on host port 6380 (Plan 12-05 RedisFixture connect target)"
  - "Host port 6379 GUARANTEED UNBOUND by the 6380:6379 mapping (Plan 12-06 HealthDeadRedisFixture dead-port)"
  - "Compose-internal DNS hostname `redis` resolving to the Redis container (baseapi-service env)"
  - "`docker exec sk-redis redis-cli --scan` valid command (Plan 12-08 phase-close gate)"
  - "ConnectionStrings__Redis env var on baseapi-service (D-04 defensive override)"
affects: [12-05-redisfixture, 12-06-healthdeadredisfixture, 12-08-phase-close]

# Tech tracking
tech-stack:
  added: [redis:7.4.9-alpine]
  patterns:
    - "5th compose tier (redis) following Phase 11 sk-* container_name + healthcheck cadence convention"
    - "CMD-form healthcheck (NOT CMD-SHELL) for Alpine BusyBox quoting-hazard avoidance (RESEARCH Pitfall 5)"
    - "host:container port mirror (6380:6379) for collision-avoidance + deliberate dead-port reservation"

key-files:
  created: []
  modified: [compose.yaml]

key-decisions:
  - "redis:7.4.9-alpine pinned (RSALv2/SSPLv1 7.4 line, NOT 8.0+ AGPLv3) — INFRA-REDIS-01"
  - "6380:6379 host:container mapping mirrors Phase 2 Postgres 5433:5432 — D-01 collision-avoidance + reserves host 6379 unbound for Plan 12-06 dead-port"
  - "persistence disabled via command [redis-server, --save, \"\", --appendonly, no] + no volumes entry — D-03 (L2 rebuildable from L3)"
  - "ConnectionStrings__Redis = redis:6379,abortConnect=false,connectTimeout=5000 (compose DNS hostname, abortConnect=false boot-safety, no allowAdmin) — D-04 / T-12-02-04"

patterns-established:
  - "Redis as 5th compose stack tier with INFRA-REDIS-02 5s/3s/10/5s healthcheck cadence"

requirements-completed: [INFRA-REDIS-01, INFRA-REDIS-02]

# Metrics
duration: ~4min (plus Docker Desktop cold-boot wait)
completed: 2026-05-29
---

# Phase 12 Plan 02: Redis Infra Composition + Healthcheck Summary

**sk-redis landed as the 5th compose tier (redis:7.4.9-alpine, host 6380→container 6379, no-persistence, redis-cli ping healthcheck) wired into baseapi-service via depends_on service_healthy + ConnectionStrings__Redis env var — verified healthy and PONG-responsive with host port 6379 confirmed unbound.**

## Performance

- **Duration:** ~4 min (plus Docker Desktop cold-boot wait for runtime verification)
- **Started:** 2026-05-29T03:52:41Z
- **Completed:** 2026-05-29T03:56:34Z
- **Tasks:** 1
- **Files modified:** 1 (compose.yaml — 42 insertions, 0 deletions)

## Accomplishments

- New `redis` service block (redis:7.4.9-alpine, container_name sk-redis, ports 6380:6379, command `["redis-server", "--save", "", "--appendonly", "no"]`, healthcheck `["CMD", "redis-cli", "ping"]` at 5s/3s/10/5s) inserted after prometheus, before baseapi-service, with the D-01/D-02/D-03 + INFRA-REDIS-01 inline comment block.
- `ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"` added to baseapi-service.environment directly after ConnectionStrings__Postgres (D-04).
- `redis: condition: service_healthy` added to baseapi-service.depends_on alongside the existing 4 entries.
- Empirically verified the full runtime contract (see Verification Evidence below).

## Task Commits

1. **Task 1: Add redis service block + baseapi-service env var + depends_on entry** - `dd39ee3` (feat)

**Plan metadata:** (this commit — docs: complete plan)

## Files Created/Modified

- `compose.yaml` - Added redis service tier + baseapi-service ConnectionStrings__Redis env var + baseapi-service depends_on redis entry. 42 insertions, 0 deletions (additions only; no existing service block modified — T-12-02-06).

## The 3 Edits (exact)

- **EDIT (a):** New `redis:` service block (with 19-line D-01/D-02/D-03 + INFRA-REDIS-01 comment header) inserted between the `prometheus:` block and the `baseapi-service:` block.
- **EDIT (b):** `ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"` (+ 2-line D-04 comment) inserted after the `ConnectionStrings__Postgres` line in `baseapi-service.environment`.
- **EDIT (c):** `redis:` / `condition: service_healthy` (6/8-space indent) appended to `baseapi-service.depends_on` after the `prometheus` entry.

## Verification Evidence

- **`docker compose config`** → exit 0 (schema-valid YAML; validated offline before daemon boot, re-confirmed after).
- **`docker pull redis:7.4.9-alpine`** → success. Image RepoDigest SHA (immutability traceability): `redis@sha256:6ab0b6e7381779332f97b8ca76193e45b0756f38d4c0dcda72dbb3c32061ab99`.
- **`docker compose up -d redis`** → sk-redis Created → Started (exit 0).
- **`docker inspect --format '{{.State.Health.Status}}' sk-redis`** → `healthy` on first 3s poll (well inside the 30s window).
- **`docker compose ps redis --format json`** → `"Health":"healthy"`, `"Status":"Up 15 seconds (healthy)"`, `"Ports":"0.0.0.0:6380->6379/tcp, [::]:6380->6379/tcp"`, `"Image":"redis:7.4.9-alpine"`.
- **`docker exec sk-redis redis-cli ping`** → `PONG`.
- **`Test-NetConnection localhost -Port 6379`** → `TcpTestSucceeded=False` (host 6379 UNBOUND — defends Plan 12-06 HealthDeadRedisFixture dead-port assumption, Pitfall 3).
- **`Test-NetConnection localhost -Port 6380`** → `TcpTestSucceeded=True` (host 6380 reachable — Plan 12-05 RedisFixture target).
- **`git diff --numstat compose.yaml`** → `42 0 compose.yaml` (additions only; no deletions, no reformatting).
- **HEALTH invariant:** `git diff src/BaseApi.Core/Health/StartupCompletionService.cs` and `git diff src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` both EMPTY (D-05/D-06 byte-immutable preserved).
- **Negative assertions (all FALSE/absent in compose.yaml):** `redisdata:` named volume, `--requirepass`, `allowAdmin=true`, `CMD-SHELL.*redis-cli`, `image: redis:8`.

## Decisions Made

None beyond plan-as-written. The image tag was kept at the plan's verbatim `redis:7.4.9-alpine` (no newer 7.4.x patch substitution was needed; the tag pulled successfully and is current at edit time).

## Deviations from Plan

None - plan executed exactly as written. All 3 edits applied verbatim; image tag unchanged.

## Issues Encountered

- **Docker daemon not running at execution start.** The runtime acceptance criteria (compose up, health poll, PONG, port-unbound proof, image SHA) require the Docker daemon, which was offline. Resolved by launching Docker Desktop and polling for the Linux engine (ready at ServerVersion 29.3.1 within ~the wait window), then executing all runtime checks. Static/regex criteria had already passed offline. No code change resulted; this was an environment-readiness step, not a deviation.
- **Bash tool `$`-sigil stripping for inline PowerShell** (known from prior plans). Worked around by writing ephemeral `.ps1` verification scripts and invoking via `-File`, then deleting them before the task commit (none left untracked).

## Note on anonymous volume

`docker compose ps` reports `LocalVolumes:1` for sk-redis. This is the redis image's own `VOLUME /data` declaration creating an anonymous Docker volume at runtime — it is NOT a `redisdata:` named volume in compose.yaml (D-03 forbids a *declared* persistence volume; compose.yaml has none, verified). Combined with `--save "" --appendonly no`, Redis writes nothing to that mount, so persistence remains effectively disabled per D-03 intent.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Plan 12-05 unblocked: RedisFixture can connect to localhost:6380.
- Plan 12-06 unblocked: HealthDeadRedisFixture can use the unbound host 6379 as the dead-port (empirically confirmed unbound).
- Plan 12-08 unblocked: `docker exec sk-redis redis-cli --scan` is now a valid phase-close gate command.
- sk-redis container is left running as the live infrastructure this plan delivers.

## Self-Check: PASSED

- FOUND: `.planning/phases/12-redis-infra-composition-healthcheck-di-registration/12-02-SUMMARY.md`
- FOUND: commit `dd39ee3` (Task 1 — feat compose.yaml redis additions)

---
*Phase: 12-redis-infra-composition-healthcheck-di-registration*
*Completed: 2026-05-29*
