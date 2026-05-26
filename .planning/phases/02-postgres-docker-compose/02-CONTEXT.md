# Phase 2: Postgres + Docker Compose - Context

**Gathered:** 2026-05-26
**Status:** Ready for planning

<domain>
## Phase Boundary

Stand up a local Postgres 17 container via Docker Compose with persistent storage and a `pg_isready` healthcheck so the service (Phase 8) can declare a health-gated dependency on it. This phase produces `compose.yaml` + `.env` + a small README update + the `appsettings.Development.json` host-port reconciliation that Phase 1 D-14 explicitly deferred to this phase. No application-level code changes (no EF Core, no controllers, no migrations — those are Phase 3+).

Out of this phase: `BaseEntity` / `BaseDbContext` / migrations / `Repository<T>` (Phase 3), the production `Dockerfile` for `BaseApi.Service` (Phase 8, INFRA-05), any auth / secrets handling beyond dev-default env vars (deferred to v2 per PROJECT.md Out of Scope), the actual database schema (Phase 3 generates it via EF Core migrations, Phase 8 plugs in the entity DbSets and runs `InitialCreate`).

</domain>

<decisions>
## Implementation Decisions

### Host Port + Phase 1 Carry-Forward

- **D-01:** Postgres container exposes `5433:5432` (host 5433 → container 5432) to avoid colliding with any local Postgres install on the developer machine (Pitfall 25). Standard dev-experience pattern.
- **D-02:** Amend `src/BaseApi.Service/appsettings.Development.json` `ConnectionStrings:Postgres` from `Host=localhost;Port=5432` to `Host=localhost;Port=5433`. This is a documented Phase 1 D-14 carry-forward (D-14 stated "Phase 2 finalizes the compose values"). Phase 1's `appsettings.json` (base) is NOT touched — it uses `Host=postgres;Port=5432` (Docker-network internal) which remains correct.
- **D-03:** README quickstart updated with the host-port choice and a one-liner `psql -h localhost -p 5433 -U postgres -d stepsdb` example so devs don't trip over the port mapping. Document the rationale (avoid collision with native local PG) inline.

### Postgres Init Values + Env Var Strategy

- **D-04:** Ship a committed `.env` file at repo root with dev-only defaults:
  - `POSTGRES_DB=stepsdb`
  - `POSTGRES_USER=postgres` (use Postgres superuser for v1 dev; dedicated app user is a v2 hardening item alongside the auth/secrets work)
  - `POSTGRES_PASSWORD=postgres` (plain dev value — honest about "auth/secrets deferred to v2" per PROJECT.md Out of Scope)
- **D-05:** Compose references the values via `${POSTGRES_DB}` / `${POSTGRES_USER}` / `${POSTGRES_PASSWORD}` (no `:?required` form because the `.env` file always supplies them — `:?required` would be misleading discipline given that the values are not real secrets).
- **D-06:** Add `.env.local` to `.gitignore` for per-developer overrides (e.g., a dev who wants a different `POSTGRES_HOST_PORT`). Docker Compose auto-loads `.env`; developers wanting an override can copy `.env` to `.env.local` and edit, BUT note Compose does NOT auto-load `.env.local` — the override pattern is `docker compose --env-file .env.local up`. Document this in README.
- **D-07:** The connection string in `appsettings.json` (base) keeps its Phase 1 hardcoded baseline (`Host=postgres;Port=5432;Database=stepsdb;Username=postgres;Password=postgres`) — the values match the `.env` defaults so the API container will reach Postgres when Phase 8 wires the Dockerfile. The code review WR-01 (working password in committed JSON) is acknowledged as a v2 hardening item, NOT fixed in Phase 2. No `${VAR}` substitution at the .NET config layer in v1.

### BaseApi.Service Block In Compose

- **D-08:** `compose.yaml` declares the `baseapi-service` block with the full `depends_on: postgres: condition: service_healthy` relationship to satisfy ROADMAP SC#4. The `build:` line is PRESENT but COMMENTED OUT with the marker `# Phase 8 (INFRA-05) will set this to: build: { context: ., dockerfile: src/BaseApi.Service/Dockerfile }`. Running `docker compose up baseapi-service` MUST fail loudly until Phase 8 lands the Dockerfile — this is intentional. Running `docker compose up postgres` (or `docker compose up` with the postgres block resolvable) brings up only the database, which is the Phase 2 dev workflow.
- **D-09:** Add a `restart: unless-stopped` policy on the `baseapi-service` block alongside the commented build line so the policy is in place when Phase 8 uncomments. Postgres gets `restart: unless-stopped` too. This is a one-line decision recorded here so the planner doesn't have to think about it.

### Compose File Structure

- **D-10:** Single `compose.yaml` at repo root (modern Docker Compose v2 default filename — not legacy `docker-compose.yml`). One file containing: `services.postgres`, `services.baseapi-service` (with commented build), `volumes.pgdata`. No `compose.override.yml` in Phase 2 — defer the dev/prod split decision until Phase 8 when the Dockerfile + production-shape considerations land.
- **D-11:** Volume declaration: explicit named volume `pgdata` mapped to `/var/lib/postgresql/data` in the postgres service. No anonymous volumes. (Pitfall 26: `docker compose down` preserves the volume; only `down -v` wipes it.) The volume is declared at the top-level `volumes:` key with no driver/options (use the Compose default `local` driver) for maximum portability.
- **D-12:** Postgres image tag pinned to `postgres:17-alpine` (the floating major-minor tag per STACK.md research — currently resolves to 17.6 as of 2026-05). NOT `postgres:17.6-alpine` (would lock out automatic security/patch updates). NOT `postgres:17` (Debian variant — 5× larger image). Locked decision; matches PROJECT.md and STACK.md.

### Healthcheck (defaulted, not deeply discussed)

- **D-13:** Postgres healthcheck adopts the Pitfall 24 example verbatim:
  ```yaml
  healthcheck:
    test: ["CMD-SHELL", "pg_isready -U $$POSTGRES_USER -d $$POSTGRES_DB"]
    interval: 5s
    timeout: 5s
    retries: 10
    start_period: 5s
  ```
  Rationale: 5s interval is fast enough for local dev startup feedback; 10 retries with 5s interval = ~50s upper bound before declaring unhealthy, which comfortably exceeds Alpine Postgres cold-start time (~2-4s observed). `$$` escapes Compose interpolation so the env var resolves inside the container at exec time.

### Verification Plan (defaulted, not deeply discussed)

- **D-14:** Phase 2 mirrors Phase 1's pattern: a dedicated verification plan (e.g., `02-XX-PLAN.md` named "verification + smoke") that writes ZERO source files and executes commands against the ROADMAP success criteria:
  - `docker compose up -d postgres` and wait for `docker compose ps` to report `healthy` (SC#1)
  - `psql -h localhost -p 5433 -U postgres -d stepsdb -c "\l"` lists default database (SC#2)
  - `docker compose exec postgres psql -U postgres -d stepsdb -c "CREATE TABLE _smoke_persistence (id int); INSERT INTO _smoke_persistence VALUES (1);"`, then `docker compose down` (no `-v`), then `docker compose up -d postgres`, then `psql ... -c "SELECT * FROM _smoke_persistence"` returns 1 row (SC#3 — named-volume persistence)
  - `docker compose config baseapi-service | grep -A2 depends_on` shows the `condition: service_healthy` link (SC#4 — declared relationship, no need to actually start the service)
- **D-15:** Final cleanup step in the verification plan: drop the `_smoke_persistence` table and tear down with `docker compose down -v` so subsequent phases start clean. Document in SUMMARY whether cleanup ran or was skipped.

### Plan Structure Hint (Claude's Discretion via planner)

- **D-16:** This phase likely needs 2 plans: (1) compose.yaml + .env + .gitignore amend + README update + appsettings.Development.json port amend, (2) verification + smoke battery (D-14/15). Planner can split further if useful but the foundation work for compose is small enough that a single build plan + a single verify plan should suffice. NOT a hard requirement — planner has discretion.

### Claude's Discretion

- README content beyond the quickstart additions: planner picks whether to document a separate "Local dev workflow" section or fold compose instructions into the existing quickstart.
- Whether to include a Compose-level `name:` field (top-level project name) is open — defaulting to no, which means Compose derives the project name from the parent directory (`SK_P`).
- Order of services in `compose.yaml` (postgres first vs baseapi-service first) — irrelevant for behavior; pick whichever reads better.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase Boundary + Acceptance Criteria
- `.planning/ROADMAP.md` § Phase 2 — Goal, Depends on, Requirements list, 4 Success Criteria.
- `.planning/REQUIREMENTS.md` — INFRA-06 (compose declares postgres + service depends_on condition: service_healthy), INFRA-07 (named volume persistence across down/up).

### Prior Phase Locks
- `.planning/PROJECT.md` — Out of Scope (auth/secrets v2), locked stack (Postgres 17), Key Decisions table.
- `.planning/phases/01-repository-scaffold/01-CONTEXT.md` — D-14 (Phase 2 finalizes compose values; localhost:5432 placeholder is the explicit handoff), D-15 (.gitignore baseline), D-16 (README structure).
- `.planning/phases/01-repository-scaffold/01-03-SUMMARY.md` — Phase 1 verification battery pattern that this phase mirrors (separate plan, evidence commits, command-output excerpts).
- `.planning/phases/01-repository-scaffold/01-REVIEW.md` — WR-01 (hardcoded postgres password in base appsettings) is the v2 hardening item Phase 2 explicitly does NOT resolve.

### Stack + Pitfalls
- `.planning/research/STACK.md` § PostgreSQL row — `postgres:17-alpine` is the locked pin; rationale for not picking `:18-alpine` or Debian variants.
- `.planning/research/PITFALLS.md` § Pitfall 24 — `depends_on` + healthcheck cadence (the verbatim source for D-13).
- `.planning/research/PITFALLS.md` § Pitfall 25 — host port collision (the rationale for D-01 picking 5433).
- `.planning/research/PITFALLS.md` § Pitfall 26 — named volume persistence (the rationale for D-11 + the SC#3 verification step).
- `.planning/research/PITFALLS.md` § Pitfall 39 — connection-string secrets in appsettings.json (the rationale for acknowledging WR-01 as a v2 hardening item, not fixing it now).

### Feature Map
- `.planning/research/FEATURES.md` § "Connection string from configuration with environment override" + § "Database health check on readiness probe" — confirms the Phase 5 Health probe will consume the Postgres reachability set up here; Phase 2 only needs to make Postgres reachable, not wire the probe.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `src/BaseApi.Service/appsettings.json` (Phase 1) — already has the `ConnectionStrings:Postgres` shape with `Host=postgres;Port=5432;...;Username=postgres;Password=postgres`. Phase 2 changes nothing in this file; Phase 8 (when the API container actually runs) consumes it.
- `src/BaseApi.Service/appsettings.Development.json` (Phase 1) — has `Host=localhost;Port=5432;...`. Phase 2 changes ONLY the port: `5432` → `5433`.
- `.gitignore` (Phase 1) — dotnet-flavor baseline. Phase 2 adds `.env.local` and (defensively) `*.env.local`.
- `README.md` (Phase 1) — has Prereqs + Quickstart sections. Phase 2 adds a "Local Postgres (Docker)" subsection under Quickstart.

### Established Patterns
- Phase 1's 01-03-PLAN.md verification pattern: a dedicated plan with `autonomous: false`, evidence-only commits (`docs(...)` not `feat(...)`), SUMMARY documents command output verbatim, Deviations section captures any in-flight fixes. Phase 2's verification plan should follow the same shape.
- Phase 1's deviation-checkpoint pattern: when the verification gate surfaces a scaffold defect (Phase 1 found 4), they get fixed in place via `fix(<source-plan>)` commits with user-approved checkpoints. Apply the same pattern if Phase 2 surfaces (e.g.) a compose-syntax issue or a port-mapping collision on the dev machine.

### Integration Points
- The compose `baseapi-service` block depends on Phase 8's `src/BaseApi.Service/Dockerfile` (INFRA-05). The commented `build:` line is a deliberate handoff marker for Phase 8.
- The compose `pgdata` named volume is the persistence boundary for Phase 3's `InitialCreate` migration (Phase 8 runs it on startup per PERSIST-09). Phase 2 only proves persistence; Phase 3+ writes the schema.

</code_context>

<specifics>
## Specific Ideas

- Pitfall 24's example values are adopted verbatim for the healthcheck (not tuned) because the project hasn't yet observed real cold-start times; tuning is a Phase 5 (Observability) follow-up if metrics show waste.
- The `5433:5432` mapping is the only port choice that is BOTH defensive against local PG installs AND keeps the inside-container API config at the canonical `5432` (which is what every Postgres tutorial assumes).
- The `.env` file is committed because it contains NO real secrets (PROJECT.md Out of Scope makes this explicit). When v2 introduces an auth boundary, the password slot will move out of `.env` and into a real secret manager — that's a v2 migration concern, NOT a Phase 2 concern.

</specifics>

<deferred>
## Deferred Ideas

### To Phase 3 (EF Core Persistence Base) discussion
- **Guid generator side for `BaseEntity.Id`** — Client-generated (`Guid.NewGuid()` in C#) vs DB-generated (`gen_random_uuid()` Postgres default) vs hybrid. PG 17 has `gen_random_uuid()` built in (no `pgcrypto` extension needed), so Phase 2 needs NO init script regardless of the eventual Phase 3 choice. REQUIREMENTS.md ENTITY-01 / PERSIST-06 specify "Guid → uuid" but are silent on the generator side — this is a real Phase 3 gray area. Surface in `/gsd-discuss-phase 3`.

### To v2 (next milestone) — NOT Phase 2 work
- **Code review WR-01 follow-up** — Move the connection-string password out of base `appsettings.json` and into env-var-supplied secrets (envvar `ConnectionStrings__Postgres` or a secret manager). Requires resolving "what secret manager" and likely coincides with v2's auth boundary work. PROJECT.md Out of Scope explicitly defers auth/secrets to v2, so this is correctly held.
- **Dedicated `steps` app user (not Postgres superuser)** — Reduce blast radius if the app password leaks. Same v2 timing as WR-01.

### To Phase 5 (Observability) discussion
- **Healthcheck cadence tuning** — If OTel metrics show Postgres healthcheck noise dominates the request histogram (Pitfall 14 mentions this class of issue), revisit D-13's 5s interval. Defer until metrics are available.
- **Postgres-side OpenTelemetry instrumentation** — STACK.md mentions `Npgsql.OpenTelemetry 8.0.4` is the optional companion if DB tracing becomes a Phase 5+ priority. Phase 2 takes no action.

### To Phase 8 (Entity Build-Out + Migrations + Docker Runtime + Tests)
- **`baseapi-service` block uncomment + Dockerfile** — Phase 8 lands `src/BaseApi.Service/Dockerfile` (INFRA-05) and uncomments the `build:` line in `compose.yaml`. The depends_on relationship lands now (D-08); only the build directive deferred.
- **`compose.override.yml` for dev convenience** — If Phase 8 needs dev/prod compose split (e.g., live-reload volume mounts), introduce an override layer then. Phase 2 ships a single file.

</deferred>

---

*Phase: 02-postgres-docker-compose*
*Context gathered: 2026-05-26*
