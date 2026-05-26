---
phase: 02-postgres-docker-compose
plan: 01
subsystem: infra
tags: [docker, docker-compose, postgres, postgres-17-alpine, pg_isready, named-volume, dotenv, gitignore, appsettings]

# Dependency graph
requires:
  - phase: 01-repository-scaffold
    provides: "src/BaseApi.Service with appsettings.json (base) and appsettings.Development.json (dev override) — Phase 1 D-14 left Port=5432 in Development as a placeholder to be reconciled in Phase 2"
  - phase: 01-repository-scaffold
    provides: ".gitignore with the 'SK_P project additions (CONTEXT.md D-15)' section pattern that this plan extends with a D-06 section"
  - phase: 01-repository-scaffold
    provides: "README.md with Prereqs + Quickstart structure that this plan extends with a 'Local Postgres (Docker)' H3 subsection under Quickstart"
provides:
  - "compose.yaml at repo root: postgres:17-alpine with pg_isready healthcheck, 5433:5432 host port mapping, pgdata named-volume, and baseapi-service skeleton with depends_on: postgres: condition: service_healthy"
  - ".env at repo root with committed dev defaults POSTGRES_DB=stepsdb, POSTGRES_USER=postgres, POSTGRES_PASSWORD=postgres (D-04; PROJECT.md Out of Scope makes the password slot honest about deferred secrets)"
  - ".gitignore: D-06 section ignoring .env.local + *.env.local (does NOT ignore .env)"
  - "README.md: 'Local Postgres (Docker)' H3 subsection documenting bring-up, healthcheck wait, psql one-liner on host port 5433, down/down -v semantics, and the .env.local override pattern"
  - "src/BaseApi.Service/appsettings.Development.json: ConnectionStrings:Postgres Port=5432 -> Port=5433 (Phase 1 D-14 carry-forward closed)"
affects: [02-02-verification-smoke, 03-persistence-base, 08-runtime-and-tests]

# Tech tracking
tech-stack:
  added: [docker-compose-v2, postgres:17-alpine, pg_isready]
  patterns:
    - "deferred-marker convention: future-phase scaffolding is declared NOW with a verbatim 'Phase N (REQ-ID) will set this to: ...' comment that the future phase greps for"
    - "section-comment-then-rule in .gitignore: '# Section header (CONTEXT.md D-XX)' followed by the literal rules; preserves Phase 1 attribution convention"
    - "Quickstart H3 subsection under H2 in README for service-specific dev workflows"

key-files:
  created:
    - "compose.yaml"
    - ".env"
  modified:
    - ".gitignore"
    - "README.md"
    - "src/BaseApi.Service/appsettings.Development.json"

key-decisions:
  - "compose.yaml at repo root (Docker Compose v2 default filename — NOT docker-compose.yml) per D-10"
  - "postgres:17-alpine image pin (floating major-minor for security patches; NOT 17.6-alpine, NOT 17 Debian) per D-12"
  - "Host port 5433 maps to container port 5432 (avoids native-PG collision per Pitfall 25) per D-01"
  - "pg_isready healthcheck VERBATIM from Pitfall 24: interval 5s, timeout 5s, retries 10, start_period 5s with $$ escaping for in-container env-var resolution per D-13"
  - ".env committed with dev defaults per D-04; Out of Scope (auth/secrets to v2) makes this honest"
  - ".gitignore ignores .env.local + *.env.local but NOT .env per D-06"
  - "baseapi-service block PRESENT with depends_on: postgres: condition: service_healthy but build: line COMMENTED OUT until Phase 8 lands the Dockerfile (INFRA-05) per D-08"
  - "restart: unless-stopped on BOTH services per D-09"
  - "appsettings.json (base) NOT modified — Host=postgres;Port=5432 is the Docker-internal path Phase 8 will consume per D-07"

patterns-established:
  - "Deferred-marker pattern propagates to YAML: the compose `# Phase 8 (INFRA-05) will set this to: build: { context: ., dockerfile: src/BaseApi.Service/Dockerfile }` mirrors the Directory.Packages.props 'pinned now, consumed later' convention from Phase 1"
  - "JSON-discipline check (Pitfall 30): every appsettings edit verified via PowerShell ConvertFrom-Json + no // or /* comments — same shape Phase 1 SC#4 used"
  - "Environment-variable interpolation in compose: ${VAR} without :?required when a committed .env always supplies the value (D-05); :?required would mislead future readers about secret discipline"

requirements-completed: [INFRA-06, INFRA-07]

# Metrics
duration: 3min
completed: 2026-05-26
---

# Phase 2 Plan 1: Postgres + Docker Compose Build Summary

**compose.yaml + committed .env land postgres:17-alpine with pg_isready healthcheck, 5433->5432 host mapping, pgdata named volume, and a Phase-8-deferred baseapi-service skeleton with depends_on: service_healthy.**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-05-26T19:33:53Z
- **Completed:** 2026-05-26T19:36:25Z
- **Tasks:** 3
- **Files created:** 2 (`compose.yaml`, `.env`)
- **Files modified:** 3 (`.gitignore`, `README.md`, `src/BaseApi.Service/appsettings.Development.json`)

## Accomplishments

- `compose.yaml` synthesized verbatim per CONTEXT.md D-01..D-13: postgres:17-alpine image, `5433:5432` quoted port mapping, pg_isready healthcheck (Pitfall 24 verbatim — `interval: 5s`, `timeout: 5s`, `retries: 10`, `start_period: 5s`, double-`$$` escape so env vars resolve in-container), `pgdata` named volume mounted at `/var/lib/postgresql/data`, top-level `volumes: pgdata:` declaration with no driver overrides, `baseapi-service` block with `depends_on: postgres: condition: service_healthy` (closes ROADMAP SC#4 declaratively) and the verbatim D-08 commented-build marker line, `restart: unless-stopped` on both services
- `.env` committed at repo root with the three dev-default lines per D-04; `git check-ignore .env` confirms it is NOT ignored
- `.gitignore` appended with the `CONTEXT.md D-06` section ignoring both `.env.local` (canonical) and `*.env.local` (defensive glob); base `.env` deliberately NOT ignored
- `README.md` Quickstart now has a `### Local Postgres (Docker)` H3 subsection between the existing Quickstart powershell fence and the `## Project Layout` H2 — documents `docker compose up -d postgres`, the healthcheck wait, the psql one-liner on host port 5433 (with rationale: avoid native-PG collision; container still listens on 5432 internally), both `docker compose down` (preserves pgdata) and `docker compose down -v` (wipes pgdata), and the `.env.local` override pattern with the `docker compose --env-file .env.local up -d postgres` one-liner
- `src/BaseApi.Service/appsettings.Development.json` single-character patch: `Port=5432` -> `Port=5433` (D-02); all other connection-string tokens preserved (Host=localhost, Database=stepsdb, Username=postgres, Password=postgres, Maximum Pool Size=20, Timeout=15); file remains valid JSON with no comments (Pitfall 30); Logging + OpenTelemetry sections untouched
- `src/BaseApi.Service/appsettings.json` confirmed UNCHANGED (D-07 negative assertion): `Host=postgres;Port=5432` preserved verbatim; `Port=5433` absent

## Task Commits

Each task was committed atomically:

1. **Task 1: Write compose.yaml + .env at repo root** - `8ef3c4e` (feat)
2. **Task 2: Append .gitignore D-06 section + insert README 'Local Postgres (Docker)' H3** - `6e4e444` (docs)
3. **Task 3: Patch appsettings.Development.json Port=5432 -> Port=5433** - `692b1d5` (fix)

## Files Created/Modified

- `compose.yaml` (NEW) - Docker Compose v2 declaration: postgres:17-alpine + pg_isready healthcheck + 5433:5432 port + pgdata named volume + baseapi-service skeleton with depends_on: service_healthy + commented Phase-8 build marker
- `.env` (NEW) - Committed dev-default env vars consumed by compose ${POSTGRES_*} interpolation
- `.gitignore` (MODIFIED) - Appended D-06 section: `.env.local` + `*.env.local` ignored; base `.env` deliberately not ignored
- `README.md` (MODIFIED) - Inserted `### Local Postgres (Docker)` H3 under `## Quickstart` documenting compose up/down/psql/override workflow
- `src/BaseApi.Service/appsettings.Development.json` (MODIFIED) - One-character port edit: 5432 -> 5433 in `ConnectionStrings:Postgres`

## Decisions Made

None new — all decisions were locked in CONTEXT.md (D-01..D-13, D-16) and implemented verbatim. The plan's `<must_haves>` truths and the task-level acceptance criteria were the source of truth; this plan made zero discretionary choices.

## Deviations from Plan

None - plan executed exactly as written.

All three task `<automated>` verifiers exited 0 on first run. The only minor operational note: PowerShell verifier scripts were materialized to temporary `.tmp_verify_taskN.ps1` files at repo root (then deleted before the corresponding commit) because the Bash tool's heredoc piping into `powershell.exe -Command` requires double-layer escaping for `$$`, `\b`, and `(?m)` regex anchors. The actual verifier logic from the plan was executed verbatim; only the dispatch mechanism (file vs `-Command`) differed. No files outside the planned change set were touched, no scope creep, no security/correctness fixes needed.

## Issues Encountered

None - the locked CONTEXT.md decisions and PATTERNS.md synthesis block produced files that passed all 12+ regex/grep assertions on the first write. The Phase 1 D-14 carry-forward (Port=5432 -> 5433) was a one-character edit with a clean before/after diff.

## User Setup Required

None - no external service configuration required. The `.env` file ships dev defaults; `docker compose up -d postgres` is the only command a developer needs to bring Postgres up locally (verified declaratively in this plan; behaviorally in Plan 02-02).

## Next Phase Readiness

- **Plan 02-02 (verification + smoke) is ready to execute.** All structural promises are in source: SC#1 (postgres:17-alpine + pg_isready healthcheck), SC#3 (named-volume `pgdata` persistence semantics), and SC#4 (depends_on: postgres: condition: service_healthy declared on baseapi-service) are structurally true. SC#2 (psql -h localhost -p 5433 -U postgres -d stepsdb succeeds) is structurally reachable because `.env` defaults match the connection-string components in appsettings.Development.json.
- **Phase 3 (Persistence Base)** can now assume a reachable local Postgres on `localhost:5433` with the `stepsdb` database and `postgres` superuser when a developer runs `docker compose up -d postgres`. The named volume `pgdata` is the persistence boundary for the eventual `InitialCreate` migration (Phase 3+ writes the schema; Phase 8 runs it on startup per PERSIST-09).
- **Phase 8 (INFRA-05)** can grep the verbatim D-08 marker `# Phase 8 (INFRA-05) will set this to: build: { context: ., dockerfile: src/BaseApi.Service/Dockerfile }` in `compose.yaml` to find and uncomment the `build:` line when the Dockerfile lands. The `restart: unless-stopped` + `depends_on: postgres: condition: service_healthy` skeleton is already wired and need not be re-authored.
- **No blockers or concerns.** Threat model T-02-01..T-02-06 dispositions hold: T-02-05 (`.env.local` accidental commit) is the only `mitigate` and was implemented via the `.gitignore` D-06 section.

## Self-Check: PASSED

Verification of claimed artifacts:

- `compose.yaml` exists at repo root: FOUND (35 lines, contains `postgres:17-alpine`, `"5433:5432"`, `pg_isready`, `pgdata:/var/lib/postgresql/data`, `condition: service_healthy`, D-08 commented marker verbatim)
- `.env` exists at repo root: FOUND (3 lines: POSTGRES_DB=stepsdb, POSTGRES_USER=postgres, POSTGRES_PASSWORD=postgres); `git check-ignore .env` returns non-zero (file NOT ignored — correct per D-04)
- `.gitignore` contains literal `.env.local` and `*.env.local` under a `CONTEXT.md D-06` attribution header: FOUND; does NOT contain literal `.env`: confirmed
- `README.md` contains `### Local Postgres (Docker)` H3 with `docker compose up -d postgres`, `psql -h localhost -p 5433 -U postgres -d stepsdb`, `docker compose down -v`, and `docker compose --env-file .env.local`: FOUND
- `src/BaseApi.Service/appsettings.Development.json` `ConnectionStrings.Postgres` matches `Port=5433\b` and does NOT match `Port=5432\b`: confirmed via ConvertFrom-Json round-trip; all six other tokens (Host=localhost, Database=stepsdb, Username=postgres, Password=postgres, Maximum Pool Size=20, Timeout=15) preserved
- `src/BaseApi.Service/appsettings.json` (base) UNCHANGED: contains `Host=postgres;Port=5432`, does NOT contain `Port=5433` (D-07 preserved); not in `git status` working tree

Commits verified in `git log --oneline -5`:

- FOUND: `8ef3c4e feat(02-01): add Docker Compose postgres:17-alpine + .env dev defaults`
- FOUND: `6e4e444 docs(02-01): document local Postgres compose workflow + ignore .env.local`
- FOUND: `692b1d5 fix(02-01): reconcile appsettings.Development.json Postgres port to 5433`

All claimed files exist on disk with the claimed content; all claimed commits are present in `git log`; no untracked files remain outside the planned change set.

---
*Phase: 02-postgres-docker-compose*
*Completed: 2026-05-26*
