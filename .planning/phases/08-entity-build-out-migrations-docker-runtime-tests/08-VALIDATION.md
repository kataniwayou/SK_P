---
phase: 8
slug: entity-build-out-migrations-docker-runtime-tests
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-27
---

# Phase 8 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 + WebApplicationFactory<Program> |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (Phase 1) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "Category=Smoke&Entity={EntityName}"` (per-entity smoke; planner pins exact filter strings in 08-02..08-06) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Estimated runtime** | ~25-30 seconds for the 30 new facts; ~60s for the full 128-fact suite |

---

## Sampling Rate

- **After every task commit:** Run the quick command for the entity / surface touched.
- **After every plan wave:** Run the full suite command.
- **Before `/gsd-verify-work`:** Full suite must be green; Phase 3 D-18 cadence (3 consecutive GREEN runs) is enforced by Plan 08-08.
- **Max feedback latency:** ~30 seconds per quick run; ~60 seconds per full suite.

---

## Per-Task Verification Map

> Populated by the planner during PLAN.md generation. Each task acceptance criteria must reference either an `<automated>` verify command (xUnit fact or compile-time check) or a Wave 0 dependency. Per the Phase 8 RESEARCH.md `## Validation Architecture` section, every one of the 41 REQ-IDs is mapped to a concrete evidence file + assertion.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| TBD | TBD | TBD | TBD | TBD | TBD | TBD | TBD | TBD | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

> Phase 8 has no Wave 0 in the dependency sense — all infrastructure was completed in Phases 1-7. The "Wave A" foundation plan (`08-01-PLAN.md`) lands the test composition plumbing (`Phase8WebAppFactory`) BEFORE Wave B entity smoke tests run. Treat Wave A artifacts as the equivalent of "Wave 0" for sampling purposes.

- [ ] `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` — composition root for entity smoke tests (Wave A 08-01)
- [ ] `Dockerfile` at repo root (Wave A 08-01)
- [ ] `compose.yaml` updates — drop `phase-8` profile, switch to `build: .`, add env + ports + healthcheck (Wave A 08-01)
- [ ] `REQUIREMENTS.md` amendment — move TEST-03 + TEST-04 to v2 (Wave A 08-01)
- [ ] `.config/dotnet-tools.json` — pin `dotnet-ef 8.0.27` as local tool (Wave A 08-01)
- [ ] `src/BaseApi.Service/BaseApi.Service.csproj` — add `Microsoft.EntityFrameworkCore.Design` PackageReference with `PrivateAssets="all"` (Wave A 08-01 or Wave C 08-07; planner picks)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| `docker compose up` produces a functional 2-service stack (Postgres + service) | SC#1 | Composition smoke test — Docker layer not exercised inside `dotnet test` | After Wave A: `docker compose up --build`, then `curl http://localhost:8080/health/ready` returns 200, `curl http://localhost:8080/api/v1/schemas` returns 200 + `[]`. Stop with `docker compose down -v` (volumes purged for the next BEFORE/AFTER snapshot). |
| Byte-identical `psql \l` BEFORE/AFTER snapshot | Phase 3 D-15 / SC#1 | DB-state proof for no-leak guarantee; xUnit can't snapshot the DB-list output | Plan 08-08 procedure: capture `psql -U postgres -h localhost -p 5433 -l > before.txt`, run full suite, capture `... > after.txt`, `diff before.txt after.txt` must be empty. |
| 3 consecutive GREEN runs of full suite | Phase 3 D-18 / SC#6 | Regression cadence — flakiness shows up across repetition, not within one run | Plan 08-08: run `dotnet test` three times in a row, all three must exit 0. |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave A dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave A (08-01) covers all MISSING references for Wave B + Wave C
- [ ] No watch-mode flags (no `--watch` in any verify command)
- [ ] Feedback latency < 60 seconds per full suite run
- [ ] `nyquist_compliant: true` set in frontmatter once planner finishes the Per-Task Verification Map

**Approval:** pending
