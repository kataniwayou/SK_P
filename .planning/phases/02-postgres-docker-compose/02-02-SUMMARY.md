---
phase: 02-postgres-docker-compose
plan: 02
subsystem: infra
tags: [verification, docker-compose, postgres-17-alpine, named-volume, healthcheck, phase-2-acceptance, compose-v5-strict-validation]

# Dependency graph
requires:
  - "02-01: compose.yaml (postgres:17-alpine + pg_isready healthcheck + pgdata volume + baseapi-service depends_on stub), .env (POSTGRES_DB/USER/PASSWORD dev defaults), .gitignore (.env.local ignore), README.md (Local Postgres subsection), src/BaseApi.Service/appsettings.Development.json (Port=5433)"
provides:
  - "Phase 2 SC#1 GREEN: postgres:17-alpine healthy via `docker compose up -d postgres` + `docker compose ps`"
  - "Phase 2 SC#2 GREEN: host-side psql connect to localhost:5433 successful via `docker run --rm --network host postgres:17-alpine psql`; both stepsdb and postgres databases listed"
  - "Phase 2 SC#3 GREEN: pgdata named-volume persistence verified across `docker compose down` (no -v) + `docker compose up`"
  - "Phase 2 SC#4 GREEN: `docker compose --profile phase-8 config` shows resolved depends_on -> postgres -> condition: service_healthy; D-08 commented-build marker intact"
  - "Phase 1 D-14 carry-forward closed: appsettings.Development.json Port=5432 -> Port=5433"
  - "INFRA-06 + INFRA-07 closed"
  - "Compose v5.1.1 strict-validation fix-forward against Plan 02-01: profiles + placeholder image pattern lets stub services declare depends_on without breaking default `compose config/up/down`"
affects: [03-ef-core-base, 05-observability, 08-entities]

# Tech tracking
tech-stack:
  added: [docker-compose-v5, postgres-17-alpine-verified, pg_isready-runtime, compose-profiles]
  patterns:
    - "Compose-config-as-truth: verify resolved compose graph via `docker compose config` rather than grepping raw YAML — catches env-interpolation failures + YAML parse errors"
    - "Profile-gated future services: services that lack image/build until a future phase are tagged with `profiles: [phase-N]` + a non-existent placeholder image; default ops skip them, explicit ops still fail loudly preserving deferral semantics"
    - "Negative-assertion deferral: `docker compose up baseapi-service` MUST fail with image-pull-failure (was no-image/no-build pre-fix) until Phase 8 (INFRA-05) lands the Dockerfile (D-08)"
    - "Named-volume persistence proof: round-trip via `docker compose exec ... INSERT` + `docker compose down` (no -v) + `up` + `SELECT` is the only way to verify the Pitfall 26 contract in execution"
    - "Host-side TCP fallback: when host has no psql client, `docker run --rm --network host postgres:17-alpine psql -h localhost -p 5433 ...` proves host-side reachability through the published port (NOT the same as `docker compose exec` which bypasses the port mapping)"

key-files:
  created:
    - .planning/phases/02-postgres-docker-compose/02-02-SUMMARY.md
  modified:
    - compose.yaml (fix-forward against Plan 02-01 — profiles + placeholder image)

key-decisions:
  - "postgres reported healthy in ~13s after container start (well within ~55s healthcheck upper bound)"
  - "host-side psql path used: `docker run --rm --network host postgres:17-alpine psql -h localhost -p 5433` (psql not on Windows host PATH); this still passes through the published port 5433 — a true host-side connection, not a container-shell exec"
  - "D-15 cleanup ran in full: smoke table dropped, `docker compose down -v` exit 0, sk_p_pgdata volume removed verbatim"
  - "Compose v5.1.1 strict-validation surfaced a Plan 02-01 defect (baseapi-service block lacking image/build broke ALL compose commands); fix-forward via `profiles: [phase-8]` + placeholder image baseapi-service:phase-8-placeholder"

patterns-established:
  - "Phase 2 verification mirrors Phase 1 (Plan 01-03) shape: `autonomous: false`, near-empty `files_modified`, evidence commits, verbatim command output in SUMMARY, deviation-attribution via `fix(02-01): ...` against the build plan"

requirements-completed: [INFRA-06, INFRA-07]

# Metrics
duration: 5min
completed: 2026-05-26
---

# Phase 02 Plan 02: Acceptance Verification Summary

**Plan 02 ran the full Phase 2 acceptance battery (`docker compose up`, psql connect via `docker run --network host`, named-volume persistence round-trip, depends_on graph resolution) against the artifacts Plan 02-01 landed. All four ROADMAP success criteria are GREEN; the two INFRA requirements (INFRA-06, INFRA-07) are closed; Phase 2 ships. One fix-forward against Plan 02-01 was required to make Compose v5.1.1 accept the deferred baseapi-service block (`fix(02-01): gate baseapi-service behind phase-8 profile + placeholder image`).**

**Plan:** 02-postgres-docker-compose / 02-02
**Executed:** 2026-05-26T19:41:03Z — 2026-05-26T19:46:38Z
**Result:** GREEN — Phase 2 complete, all 4 ROADMAP success criteria verified

## Performance

- **Duration:** ~5 min wall-clock (mostly first-pull of postgres:17-alpine layers — ~45s — and two healthcheck polls)
- **Started:** 2026-05-26T19:41:03Z
- **Completed:** 2026-05-26T19:46:38Z
- **Tasks:** 6 (Task 1 SC#1 health; Task 2 SC#2 psql; Task 3 SC#3 persistence; Task 4 SC#4 depends_on; Task 5a cleanup; Task 5b SUMMARY)
- **Files modified by this plan:** 1 (`compose.yaml` — fix-forward against Plan 02-01). One file written: this SUMMARY.

## Phase 2 Success Criteria — Verification (verbatim evidence)

### SC#1: `docker compose up postgres` brings up postgres:17-alpine and pg_isready reports healthy

**Command:** `docker compose up -d postgres`

**Verbatim output (final lines after image-pull and container start):**

```
 Image postgres:17-alpine Pulling
 ... (layer-pull progression, ~45s for first-pull) ...
 cc1000ae6428 Pull complete 0B
 ef78454bf4ab Pull complete 0B
 8f4a56ba6668 Pull complete 0B
 21901c73dfc8 Pull complete 0B
 23b001932a6f Pull complete 0B
 c4a52e206a71 Pull complete 0B
 Image postgres:17-alpine Pulled
 Network sk_p_default Creating
 Network sk_p_default Created
 Volume sk_p_pgdata Creating
 Volume sk_p_pgdata Created
 Container sk_p-postgres-1 Creating
 Container sk_p-postgres-1 Created
 Container sk_p-postgres-1 Starting
 Container sk_p-postgres-1 Started
Exit code: 0
Duration: 45s (includes first-pull of all postgres:17-alpine layers)
```

**Health poll (5s after up returned):**

```
[poll 1 @ 6s] Up 13 seconds (healthy)
Healthy=1 after 6s of polling
```

**Final `docker compose ps postgres`:**

```
NAME              IMAGE                COMMAND                  SERVICE    CREATED          STATUS                    PORTS
sk_p-postgres-1   postgres:17-alpine   "docker-entrypoint.s…"   postgres   20 seconds ago   Up 19 seconds (healthy)   0.0.0.0:5433->5432/tcp, [::]:5433->5432/tcp
```

**Image identity (D-12 honored):** `postgres:17-alpine` (verified verbatim via `docker compose ps postgres --format '{{.Image}}'`)
**First-healthy time:** ~13s after container start (healthcheck cadence: 5s interval × up to 10 retries + 5s start_period = ~55s upper bound; observed time well within margin)

**SC#1: GREEN**

### SC#2: Connecting from the host with psql succeeds

**Path used:** `docker run --rm --network host postgres:17-alpine psql ...` (host had no native `psql` on PATH; this fallback STILL routes through the published port 5433 on the host, so it is a true host-side TCP connection — NOT `docker compose exec`, which bypasses the port mapping and would be a weaker proof)

**Command:** `docker run --rm --network host -e PGPASSWORD=postgres postgres:17-alpine psql -h localhost -p 5433 -U postgres -d stepsdb -c "\l"`

**Verbatim output:**

```
                                                    List of databases
   Name    |  Owner   | Encoding | Locale Provider |  Collate   |   Ctype    | Locale | ICU Rules |   Access privileges
-----------+----------+----------+-----------------+------------+------------+--------+-----------+-----------------------
 postgres  | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           |
 stepsdb   | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           |
 template0 | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           | =c/postgres          +
           |          |          |                 |            |            |        |           | postgres=CTc/postgres
 template1 | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           | =c/postgres          +
           |          |          |                 |            |            |        |           | postgres=CTc/postgres
(4 rows)

Exit code: 0
```

Both the ROADMAP-required `stepsdb` (the env-var-created DB) AND `postgres` (the default DB) are listed.

**JSON-discipline + Port re-check (mirrors Phase 1 SC#4 style):**

- `src/BaseApi.Service/appsettings.Development.json`: valid JSON (ConvertFrom-Json exit 0); `ConnectionStrings.Postgres = "Host=localhost;Port=5433;Database=stepsdb;Username=postgres;Password=postgres;Maximum Pool Size=20;Timeout=15"`; contains `Port=5433\b` (D-02 closed); does NOT contain `Port=5432\b`
- `src/BaseApi.Service/appsettings.json` (base): valid JSON; `ConnectionStrings.Postgres = "Host=postgres;Port=5432;Database=stepsdb;Username=postgres;Password=postgres;Maximum Pool Size=20;Timeout=15"`; contains literal `Host=postgres;Port=5432` (D-07 preserved); does NOT contain `Port=5433`

**SC#2: GREEN** (D-02 + D-07 invariants both hold)

### SC#3: `docker compose down` (no -v) preserves rows in the pgdata named volume

**Round-trip outputs (verbatim):**

1. **CREATE + INSERT (exit 0):**
   ```
   CREATE TABLE
   INSERT 0 1
   ```
2. **Pre-down SELECT (exit 0):**
   ```
   1:phase-2-sc3-persistence-proof
   ```
3. **`docker compose down` (NO -v) — exit 0:**
   ```
    Container sk_p-postgres-1 Stopping
    Container sk_p-postgres-1 Stopped
    Container sk_p-postgres-1 Removing
    Container sk_p-postgres-1 Removed
    Network sk_p_default Removing
    Network sk_p_default Removed
   ```
   Note the absence of any `Volume sk_p_pgdata Removed` line — the named volume was NOT touched (D-11 / Pitfall 26 contract verified).
4. **`docker volume ls` post-down — `sk_p_pgdata` PRESENT in listing:**
   ```
   ... (other unrelated volumes) ...
   sk2_1_mongo-data
   sk_p_pgdata
   ```
5. **`docker compose up -d postgres` re-up (exit 0):**
   ```
    Network sk_p_default Creating
    Network sk_p_default Created
    Container sk_p-postgres-1 Creating
    Container sk_p-postgres-1 Created
    Container sk_p-postgres-1 Starting
    Container sk_p-postgres-1 Started
   ```
   Re-up duration: 1s for `docker compose up` invocation; container reported `Up 11 seconds (healthy)` on the very first poll (~5s).
6. **Post-up SELECT — exit 0, SAME row, SAME marker:**
   ```
   1:phase-2-sc3-persistence-proof
   ```

The marker text `phase-2-sc3-persistence-proof` written before `down` was readable verbatim after `up` — proving INFRA-07 (named-volume persistence across `down`/`up`).

**SC#3: GREEN**

### SC#4: compose declares baseapi-service with depends_on: postgres: condition: service_healthy

**Command:** `docker compose --profile phase-8 config` (the `phase-8` profile is the post-fix-forward gate documented under Deviations below; without `--profile phase-8` the default config resolves to just postgres, which is the intended dev workflow per D-08)

**Verbatim output (resolved baseapi-service section):**

```yaml
services:
  baseapi-service:
    profiles:
      - phase-8
    depends_on:
      postgres:
        condition: service_healthy
        required: true
    image: baseapi-service:phase-8-placeholder
    networks:
      default: null
    restart: unless-stopped
```

The chain `depends_on -> postgres -> condition: service_healthy` is verbatim in the resolved graph (Compose adds `required: true` as the canonicalised default — SC#4 is concerned with the `condition` value, which is exactly `service_healthy`).

**`docker compose --profile phase-8 config --services` (exit 0):**
```
postgres
baseapi-service
```

**D-08 commented-build marker check (line 25 of compose.yaml):**
```
25:    # Phase 8 (INFRA-05) will set this to: build: { context: ., dockerfile: src/BaseApi.Service/Dockerfile }
```
Present verbatim — Phase 8 (INFRA-05) handoff intact.

**Negative assertion — `docker compose up -d baseapi-service` MUST fail (D-08 deferral):**
```
 Image baseapi-service:phase-8-placeholder Pulling
 Image baseapi-service:phase-8-placeholder Error pull access denied for baseapi-service, repository does not exist or may require 'docker login'
Error response from daemon: pull access denied for baseapi-service, repository does not exist or may require 'docker login'
Exit code: 1
```

The failure mode is **image-pull-failure** (the placeholder tag `baseapi-service:phase-8-placeholder` does not exist on any registry by design) — semantically equivalent to the pre-fix-forward "no image / no build" failure but routed through a different Compose validation path. D-08's "MUST fail loudly until Phase 8" intent is preserved.

**SC#4: GREEN**

## Phase 2 Requirements Closed

| ID | Requirement | Closed by |
|----|-------------|-----------|
| INFRA-06 | `docker-compose.yml` defines `postgres:17-alpine` with `pg_isready` healthcheck plus `BaseApi.Service` with `depends_on: postgres: condition: service_healthy` | Plan 02-01 (declaration) + Plan 02-02 SC#1 + SC#4 (behavior verification) + this plan's fix-forward (Compose v5.1.1 compatibility) |
| INFRA-07 | Postgres data persisted in a named volume across `docker-compose down/up` | Plan 02-01 (D-11 named volume declaration) + Plan 02-02 SC#3 (round-trip proof) |

## Files Created (Phase 2 totals)

**Plan 02-01:** 5 files (1 new compose.yaml + 1 new .env + 3 modified: .gitignore, README.md, src/BaseApi.Service/appsettings.Development.json)
**Plan 02-02:** 1 file modified (compose.yaml — fix-forward) + 1 file written (this SUMMARY)

**Total:** 6 files committed in Phase 2 (2 new + 3 modified by Plan 02-01 + 1 modified by Plan 02-02 fix-forward + 1 SUMMARY).

## Cleanup Status (per D-15)

**Cleanup ran in full:**

- `_smoke_persistence` dropped via `docker compose exec -T postgres psql -U postgres -d stepsdb -c 'DROP TABLE IF EXISTS _smoke_persistence;'` — exit 0, `DROP TABLE` confirmed in output
- `docker compose down -v` — exit 0, verbatim output:
  ```
   Container sk_p-postgres-1 Stopping
   Container sk_p-postgres-1 Stopped
   Container sk_p-postgres-1 Removing
   Container sk_p-postgres-1 Removed
   Network sk_p_default Removing
   Volume sk_p_pgdata Removing
   Volume sk_p_pgdata Removed
   Network sk_p_default Removed
  ```
- Post-`down -v` `docker volume ls` no longer shows `sk_p_pgdata` (only `sk2_1_mongo-data` from an unrelated project remains). Phase 3 starts with a clean pgdata slate.

## Decisions Made / Deviations from Plan

### Deviation 1 — [Rule 3 — Blocking] Compose v5.1.1 strict service-block validation broke ALL `docker compose` commands

- **Found during:** Task 1 Step 1 (pre-flight defensive `docker compose down`)
- **Issue:** Every `docker compose` invocation (up, down, config, ps) failed with:
  ```
  service "baseapi-service" has neither an image nor a build context specified: invalid compose project
  ```
- **Root cause:** Docker Compose v5.1.1 (the version installed on this Windows 11 host as confirmed in the critical-context block) enforces strict validation that every declared service must have either `image:` or `build:`. The original Plan 02-01 compose.yaml relied on the pre-v5 leniency where a service block with only `depends_on:` + `restart:` + a commented `build:` line was tolerated. CONTEXT.md D-08 was authored assuming the pre-v5 behavior (it specifies "Running `docker compose up baseapi-service` MUST fail loudly" but did not anticipate that ALL compose commands would refuse to parse). This blocked Tasks 1, 3, 4, and 5a (every command requiring `docker compose`).
- **Fix:** Add `profiles: ["phase-8"]` and `image: baseapi-service:phase-8-placeholder` to the baseapi-service block in compose.yaml.
  - `profiles: ["phase-8"]` excludes the service from default `docker compose up/down/config` operations — restoring the dev workflow Plan 02-01 intended (`docker compose up` brings up only postgres).
  - `image: baseapi-service:phase-8-placeholder` satisfies the v5 parser's "must have image or build" rule. The tag does NOT exist on any registry, so `docker compose up baseapi-service` STILL fails loudly — at image-pull rather than at YAML-validation — preserving D-08's negative-assertion intent.
  - The D-08 commented-build marker `# Phase 8 (INFRA-05) will set this to: build: { context: ., dockerfile: src/BaseApi.Service/Dockerfile }` is preserved verbatim. Phase 8 (INFRA-05) will: (a) remove the `profiles: ["phase-8"]` line, (b) remove the `image: baseapi-service:phase-8-placeholder` line, (c) uncomment the `build:` directive per the marker.
  - SC#4 verification adapted: instead of `docker compose config baseapi-service`, use `docker compose --profile phase-8 config baseapi-service` (or `... config` without targeting). The resolved depends_on chain is verbatim what SC#4 / INFRA-06 requires.
- **Files modified:** `compose.yaml` (one service block updated; 7 lines added — 2 functional + 5 comment).
- **Attribution:** Plan 02-01 — the original compose.yaml shipped a service block that v5.1.1 refuses to parse. Plan 02-02 verification surfaced it, exactly mirroring the Phase 1 Plan 01-03 deviation-attribution discipline (Phase 1 found 4 such Plan 01-01/01-02 defects).
- **Commit:** `0acb0bc` — `fix(02-01): gate baseapi-service behind phase-8 profile + placeholder image`

### Other deviations

None. The remaining tasks (1, 2, 3, 4, 5a, 5b) ran exactly as authored in the plan once the fix-forward landed. The psql path adaptation (using `docker run --rm --network host postgres:17-alpine psql` instead of native host `psql`) is explicitly anticipated in the critical-context block as the fallback for hosts that lack psql on PATH — not a deviation.

## Self-Check: PASSED

**File existence:**
- FOUND: `C:/Users/UserL/source/repos/SK_P/.planning/phases/02-postgres-docker-compose/02-02-SUMMARY.md`
- FOUND: `C:/Users/UserL/source/repos/SK_P/compose.yaml` (modified by fix-forward)
- FOUND: `C:/Users/UserL/source/repos/SK_P/.env` (unchanged)
- FOUND: `C:/Users/UserL/source/repos/SK_P/src/BaseApi.Service/appsettings.Development.json` (Port=5433 verified)
- FOUND: `C:/Users/UserL/source/repos/SK_P/src/BaseApi.Service/appsettings.json` (Port=5432 verified, D-07 preserved)

**Commit verification:**
- FOUND: `0acb0bc fix(02-01): gate baseapi-service behind phase-8 profile + placeholder image`
- FOUND: `5e116e1 docs(02-01): complete plan 02-01 postgres-compose build`
- FOUND: `692b1d5 fix(02-01): reconcile appsettings.Development.json Postgres port to 5433`
- FOUND: `6e4e444 docs(02-01): document local Postgres compose workflow + ignore .env.local`
- FOUND: `8ef3c4e feat(02-01): add Docker Compose postgres:17-alpine + .env dev defaults`

**Behavioral checks (verified during this plan's execution):**
- `docker compose up -d postgres` — exit 0, postgres healthy in ~13s after container start
- `docker run --rm --network host postgres:17-alpine psql -h localhost -p 5433 -U postgres -d stepsdb -c '\l'` — exit 0, returns stepsdb + postgres + template0 + template1 (4 rows)
- Smoke-table round-trip persists across `down`/`up` (verified — `1:phase-2-sc3-persistence-proof` returned post-up)
- `docker compose --profile phase-8 config baseapi-service` shows resolved depends_on with `condition: service_healthy` (verified)
- compose.yaml D-08 commented marker present verbatim at line 25 (verified via `Grep`)
- `docker compose up -d baseapi-service` fails with image-pull-failure (exit 1) — verified, intentional per D-08
- `docker compose down -v` removed `sk_p_pgdata` volume (exit 0; volume absent from post-cleanup `docker volume ls`) — verified

All four ROADMAP Phase 2 success criteria verified GREEN. INFRA-06 and INFRA-07 closed.

## Next: Phase 3 — EF Core Persistence Base

Phase 2 ships a Postgres 17 container reachable from the host on 5433 with a `pg_isready` healthcheck and verified named-volume persistence. The compose file declares the `baseapi-service` depends_on relationship that Phase 8 (when it lands the Dockerfile per INFRA-05) will activate by removing the `phase-8` profile + placeholder image and uncommenting the `build:` directive.

Phase 3 (`/gsd-plan-phase 3` next) builds `BaseEntity`, `BaseDbContext`, `AuditInterceptor`, snake_case convention, and the generic `Repository<T>` against this Postgres before any migration is generated. The pgdata named volume verified here is the persistence boundary for the future `InitialCreate` migration (Phase 8 runs it on startup per PERSIST-09).

---
*Phase: 02-postgres-docker-compose*
*Completed: 2026-05-26*
