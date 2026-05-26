# Phase 2: Postgres + Docker Compose - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-26
**Phase:** 02-postgres-docker-compose
**Areas discussed:** Host port mapping, Postgres init defaults (DB / user / password / .env), BaseApi.Service block in compose this phase, Compose file structure
**Areas defaulted (not interactively asked):** Healthcheck cadence (adopted Pitfall 24 values), Verification plan structure (adopted Phase 1's 01-03 pattern)

---

## Gray Area Selection

| Option | Description | Selected |
|--------|-------------|----------|
| Host port mapping (5432 vs 5433) | Pitfall 25 collision avoidance; Phase 1 file ramifications | ✓ |
| Postgres init defaults (DB, user, password, .env) | POSTGRES_DB/USER/PASSWORD source-of-truth | ✓ |
| BaseApi.Service block in compose this phase | Dockerfile is Phase 8; how to satisfy SC#4 now | ✓ |
| Compose file structure | Single file vs override split; modern vs legacy filename | ✓ |

**User's choice:** All four selected.

---

## Host Port Mapping

| Option | Description | Selected |
|--------|-------------|----------|
| 5433:5432 + amend appsettings.Dev | Bind host 5433 to container 5432, avoiding local PG collision (Pitfall 25). Update appsettings.Development.json Port 5432 → 5433 as a Phase 1 carry-forward. | ✓ |
| 5432:5432 keep Phase 1 file as-is | Simplest if no local Postgres runs. Risks Pitfall 25. | |
| Configurable via .env (HOST_PORT default 5433) | ${POSTGRES_HOST_PORT:-5433}:5432 with per-machine override. More complexity now for a future flexibility. | |
| No host port at all | Drops `ports:`. Breaks ROADMAP SC#2 (psql from host). | |

**User's choice:** 5433:5432 + amend appsettings.Dev.
**Notes:** Becomes Phase 2 D-01/D-02. The Phase 1 D-14 amendment is documented as a Phase 1 carry-forward, not a deviation — D-14 explicitly stated "Phase 2 finalizes the compose values."

---

## Postgres Init Defaults

| Option | Description | Selected |
|--------|-------------|----------|
| .env file with dev defaults | Committed .env with POSTGRES_DB=stepsdb, POSTGRES_USER=postgres, POSTGRES_PASSWORD=postgres; .env.local gitignored for per-dev overrides. Honest about "auth/secrets deferred to v2." | ✓ |
| Hardcoded in compose, no .env | Literals in compose. Simplest. No env-var indirection. | |
| ${VAR:?required} no default | Forces every dev to set env vars before `docker compose up`. Highest discipline but contradicts "auth deferred to v2." | |
| Dedicated `steps` user, not superuser | Slightly stronger separation. v2 hardening territory. | |

**User's choice:** .env file with dev defaults.
**Notes:** Becomes Phase 2 D-04/D-05/D-06. POSTGRES_USER stays as the `postgres` superuser for v1 — dedicated app user is a v2 hardening item alongside WR-01.

### Follow-up: appsettings.json + .env reconciliation

| Option | Description | Selected |
|--------|-------------|----------|
| Keep hardcoded baseline in appsettings | appsettings.json keeps Phase 1 literal connection string matching .env values. WR-01 stays open as a v2 item. | ✓ |
| Connection string from env at runtime | .NET reads ConnectionStrings__Postgres from env, password slot from ${POSTGRES_PASSWORD}. Pulls forward v2 secrets work. | |
| Drop credentials from base appsettings entirely | Strongest WR-01 fix; largest blast radius (Phase 3+ CI configuration changes). | |

**User's choice:** Keep hardcoded baseline.
**Notes:** Becomes Phase 2 D-07. WR-01 stays a deferred v2 item; Phase 2 does not pull it forward.

---

## BaseApi.Service Block in Compose This Phase

| Option | Description | Selected |
|--------|-------------|----------|
| Declare with depends_on + commented build | Full depends_on relationship (satisfies SC#4); build line present but commented with "Phase 8 will fill this in." `docker compose up baseapi-service` will fail loudly until Phase 8. | ✓ |
| Declare under a 'service' profile, postgres-only by default | Compose profiles gate the service block off by default. Cleaner default UX; slightly advanced compose feature. | |
| Defer service block entirely to Phase 8 | Phase 2 ships only the postgres block. Breaks SC#4 in Phase 2 — pushes acceptance criterion to Phase 8. | |

**User's choice:** Declare with depends_on + commented build.
**Notes:** Becomes Phase 2 D-08/D-09. Adds `restart: unless-stopped` on both services as a one-line locked decision so planner doesn't have to think about it.

---

## Compose File Structure

| Option | Description | Selected |
|--------|-------------|----------|
| Single compose.yaml (modern filename) | One file at repo root; the Compose v2 default filename. Holds postgres + commented service + named volume. | ✓ |
| Legacy docker-compose.yml (single file) | Same content, older filename. Slightly broader tool/IDE recognition. No functional difference for Compose v2. | |
| compose.yaml + compose.override.yml split | Base + dev-only override layer. Cleaner separation; +1 file to maintain. | |

**User's choice:** Single compose.yaml.
**Notes:** Becomes Phase 2 D-10. Override split is deferred to Phase 8 if needed.

---

## User-Raised Clarification: "verify that db generate guid for id automatically"

| Option | Description | Selected |
|--------|-------------|----------|
| Log as Phase 3 deferred topic | Add to 02-CONTEXT.md `<deferred>` so /gsd-discuss-phase 3 surfaces it as a gray area alongside BaseEntity / BaseDbContext. | ✓ |
| Discuss now and lock in 02-CONTEXT.md | Treat as Phase 2 decision because the compose container is what makes any choice testable. | |
| Decide now AND add init script if needed | Same plus a Phase 2 init.sql for pgcrypto/uuid-ossp (unnecessary on PG 17 — built-in gen_random_uuid). | |

**User's choice:** Log as Phase 3 deferred topic.
**Notes:** Recorded in 02-CONTEXT.md `<deferred>`. Phase 2 needs no init script regardless because PG 17 has `gen_random_uuid()` built in.

---

## Defaulted (Not Interactively Discussed)

| Area | Default Applied | Source |
|------|-----------------|--------|
| Healthcheck cadence | `interval: 5s, timeout: 5s, retries: 10, start_period: 5s` (Pitfall 24 verbatim) | `.planning/research/PITFALLS.md` § Pitfall 24 |
| Verification plan structure | Separate verification plan mirroring Phase 1's 01-03 pattern (autonomous: false, evidence-only docs commits, SUMMARY with command-output excerpts) | `.planning/phases/01-repository-scaffold/01-03-PLAN.md` + `01-03-SUMMARY.md` |
| Postgres image tag | `postgres:17-alpine` (floating 17-major-minor, currently 17.6) — not `:17.6-alpine` (locks out patches), not `:17` (Debian, 5× larger) | STACK.md + PROJECT.md locked stack |
| Volume driver | Compose default (`local`) — no explicit driver/options specified | None — picked for portability |

---

## Claude's Discretion

- README content beyond the quickstart additions: separate "Local dev workflow" section vs folded into existing quickstart — planner's call.
- Whether to include a Compose top-level `name:` field — defaulting to no (Compose derives project name from directory `SK_P`).
- Service ordering in compose.yaml — irrelevant for behavior.

---

## Deferred Ideas

- **Phase 3:** Guid generator side (client `Guid.NewGuid()` vs DB `gen_random_uuid()` default) — surfaced by user during Phase 2 discussion, deferred to Phase 3 alongside `BaseEntity` / `BaseDbContext`.
- **v2:** WR-01 (connection-string password in committed JSON) and dedicated `steps` app user — held until auth/secrets work lands.
- **Phase 5:** Healthcheck cadence tuning if OTel metrics show probe noise.
- **Phase 5:** Optional `Npgsql.OpenTelemetry 8.0.4` for DB tracing if Postgres latency becomes a concern.
- **Phase 8:** Uncomment `build:` in compose.yaml + ship `src/BaseApi.Service/Dockerfile` (INFRA-05).
- **Phase 8:** `compose.override.yml` split if Phase 8 needs dev/prod compose separation.
