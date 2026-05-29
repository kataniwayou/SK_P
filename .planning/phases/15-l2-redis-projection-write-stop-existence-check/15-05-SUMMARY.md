---
phase: 15-l2-redis-projection-write-stop-existence-check
plan: 05
subsystem: orchestration-l2-projection
tags: [observability, e2e, correlationid, elasticsearch, redis, discipline-guard, negative-grep, docs-reconcile, observ-redis]
requires:
  - "OrchestrationService live Start/Stop pipeline + OBSERV-REDIS-03 op-name seam (Plan 15-04)"
  - "RedisProjectionWriter Redis-op log points the correlationId threads through (Plan 15-02)"
  - "Phase11WebAppFactory — OTLP -> collector -> Elasticsearch E2E wiring + Phase8WebAppFactory RedisMultiplexer/RedisKeyPrefix (Phase 11 / 12)"
  - "ElasticsearchTestClient.PollEsForLog + EsIndexNames.CorrelationIdFieldPath (Phase 11 Wave 0)"
  - "ValidationOrderFacts seeding helpers (Phase 14 Plan 14-05)"
provides:
  - "OrchestrationLogsE2ETests — OBSERV-REDIS-02: a real Start round-trips X-Correlation-Id to Elasticsearch via the MEL AsyncLocal scope (T-15-17)"
  - "RedisDisciplineGuardFacts — 3 forbidden-pattern guards: No_OtelRedis_Package_Referenced (OBSERV-REDIS-01/T-15-18), No_Keys_Enumeration_In_Projection (L2-PROJECT-07/T-15-19), No_Mapperly_For_Json_In_Projection (L2-PROJECT-06)"
  - "ValidationOrderFacts per-workflow first-failure SCOPE amendment + new cross-workflow partial-state fact (D-07)"
  - "REQUIREMENTS.md + ROADMAP.md Phase 15 text reconciled to the shipped Stop-deletes / non-idempotent-422 / processor-TTL / jobId-NewGuid behavior; Phase 16 SC2/SC5 inversion flagged"
affects:
  - "Phase 16 (its SC2/SC5 facts must invert: post-Stop root+step gone, processor intact — flagged in ROADMAP)"
tech-stack:
  added: []
  patterns:
    - "Comment-stripped source-grep guard facts (strip /* */ + // + /// before regex) so doc-comments that NAME a forbidden pattern to forbid it do not trip the guard (Plan 03-01 verify-script precedent)"
    - "Repo-root resolution by walking up from AppContext.BaseDirectory to SK_P.sln (PackageAuditTests / ComposeYamlFacts precedent)"
    - "E2E mirror of SchemasLogsE2ETests for a different write path (Start instead of Schema POST) keeping the corrId/PollEsForLog/Observability-collection structure verbatim"
    - "Cross-workflow partial-state assertion: per-workflow loop projects A's L2 root before B's gate throws -> assert A's root key EXISTS alongside the 422 (D-07)"
    - "Doc amendment with inline (amended Phase 15, 2026-05-29) markers + historical was/now phrasing for reversed requirements"
key-files:
  created:
    - tests/BaseApi.Tests/Observability/OrchestrationLogsE2ETests.cs
    - tests/BaseApi.Tests/Features/Orchestration/RedisDisciplineGuardFacts.cs
  modified:
    - tests/BaseApi.Tests/Features/Orchestration/ValidationOrderFacts.cs
    - .planning/REQUIREMENTS.md
    - .planning/ROADMAP.md
decisions:
  - "Guard facts strip C# comments before pattern-matching: IRedisL2Cleanup.cs's XML doc literally contains 'NO KEYS / IServer.Keys()' to FORBID them; a naive grep would false-positive. The No_Keys guard matches IServer / .Keys( / .KeysAsync( / bare KEYS in CODE only, tolerating Dictionary.Keys property access."
  - "No_Mapperly_For_Json guard is scoped to the Projection/ folder ONLY (per plan). The Loading/WorkflowGraphLoader.cs legitimately uses Mapperly ToRead for L1 entity->DTO enrichment (D-05) — out of scope; the L2 records serialize via plain System.Text.Json (L2-PROJECT-06)."
  - "Existing within-workflow gate-order facts (CycleBeforeSchemaEdge / SchemaEdgeBeforePayload) were NOT changed — they test a SINGLE workflow failing two gates; the Phase 15 amendment only changed cross-workflow SCOPE. Added ONE new fact for the cross-workflow partial-state semantics rather than rewriting the order facts."
  - "L2-PROJECT-03 amended line keeps a 'the earlier Guid.Empty placeholder is superseded' phrase — the active spec is Guid.NewGuid(); the Guid.Empty mention is historical context, satisfying the acceptance criterion (line no longer claims jobId == Guid.Empty)."
  - "OBSERV-REDIS-04 (Redis metrics) left DEFERRED — documented, not implemented, per plan."
metrics:
  duration: ~20min
  completed: 2026-05-29
  tasks: 3
  files: 5
---

# Phase 15 Plan 05: Observability E2E + Discipline Guards + Doc Reconciliation Summary

Closed the cross-cutting observability + forbidden-pattern + documentation surface for Phase 15. Added an E2E test proving a real Start round-trips the inbound `X-Correlation-Id` through OTLP → collector → Elasticsearch (OBSERV-REDIS-02 / T-15-17), three pure source-grep discipline guards enforcing the banned Redis-projection patterns (no OTel-Redis package, no `KEYS`/`IServer.Keys()` enumeration in the Orchestration feature, no Mapperly-for-JSON in the Projection folder), amended the Phase 14 `ValidationOrderFacts` to the Phase 15 per-workflow first-failure scope (with a new cross-workflow partial-state fact), and reconciled `REQUIREMENTS.md` + `ROADMAP.md` Phase 15 text to the shipped Stop-deletes / non-idempotent-422 / processor-TTL / `jobId=Guid.NewGuid()` behavior — plus a flag on Phase 16 that its SC2/SC5 facts are now inverted. OBSERV-REDIS-04 (Redis metrics) is documented as deferred, not implemented.

## What Was Built

- **`OrchestrationLogsE2ETests`** (OBSERV-REDIS-02) — mirrors `SchemasLogsE2ETests` VERBATIM in structure (`[Trait("Phase","15")]` + `[Trait("Category","E2E")]` + `[Collection("Observability")]`, `IClassFixture<Phase11WebAppFactory>`, per-test unique `corrId = $"{Guid.NewGuid():N}"`, `X-Correlation-Id` header, `PollEsForLog` on `EsIndexNames.CorrelationIdFieldPath`). Swaps the Schema POST for a minimal known-good orchestration graph (processor → single step → workflow, no schemas → no schema-edge/payload gate to satisfy) seeded via the public entity endpoints, then `POST /api/v1/orchestration/start` → 204, then polls ES for a log doc carrying the corrId. Asserts the hit is non-null and the corrId round-tripped; per CHECKER WARNING #7, asserts on the correlationId, NOT the version string. Uses `TestContext.Current.CancellationToken`.
- **`RedisDisciplineGuardFacts`** — `[Trait("Phase","15")]`, no fixture, 3 pure `[Fact]`s:
  - `No_OtelRedis_Package_Referenced` (OBSERV-REDIS-01 / T-15-18) — asserts neither `Directory.Packages.props` nor any `.csproj` references `OpenTelemetry.Instrumentation.StackExchangeRedis`, and no loaded assembly carries that simple name (defense-in-depth).
  - `No_Keys_Enumeration_In_Projection` (L2-PROJECT-07 / T-15-19) — scans every `.cs` under `src/.../Features/Orchestration/`, strips comments, asserts no `IServer` / `.Keys(` / `.KeysAsync(` / bare `KEYS` call shape (tolerating `Dictionary.Keys` property access).
  - `No_Mapperly_For_Json_In_Projection` (L2-PROJECT-06) — scans `.cs` under `Features/Orchestration/Projection/`, strips comments, asserts no `Mapperly` / `[Mapper]` / `.ToRead(`.
  - Repo root resolved once by walking up from `AppContext.BaseDirectory` to `SK_P.sln`.
- **`ValidationOrderFacts`** (Phase 14 amendment) — class-level XML doc rewritten to describe the Phase 15 per-workflow loop (existence over ALL ids first, then workflow A fully processed before B is loaded; within-workflow gate order UNCHANGED; cross-workflow partial state ACCEPTED, D-07). Added `PerWorkflowScope_FirstValid_SecondCycle_ProjectsFirst_Fails422`: a request `[A, B]` where A is a valid single-step graph and B has a back-edge cycle → 422 with `errors.gate == "cycle"` AND A's L2 root key EXISTS (A was projected before B failed). The existing `CycleBeforeSchemaEdge` / `SchemaEdgeBeforePayload` single-workflow gate-order facts were preserved verbatim.
- **`REQUIREMENTS.md`** — amended (with inline `(amended Phase 15, 2026-05-29)` markers): L2-PROJECT-01 (+processor TTL, widened writer signature), L2-PROJECT-03 (`jobId = Guid.NewGuid()`, `correlationId` name unchanged), L2-PROJECT-05 (processor TTL, never deleted by Stop), L2-PROJECT-07 (GET-and-follow traversal; KEYS still forbidden), ORCH-START-05 (delete-then-write pre-clean), ORCH-STOP-04 (REVERSED — Stop deletes root+step, never processor), ORCH-STOP-06 (CHANGED — non-idempotent, repeat → 422).
- **`ROADMAP.md`** — Phase 15 Goal + SC1 (jobId NewGuid) + SC3 (EXISTS gate → 422-no-delete OR cleanup → 204, non-idempotent) + SC5 (GET-and-follow, OrchestrationLogsE2ETests) rewritten; the superseding `> NOTE` removed (text now reconciled). Added a `> NOTE (flagged by Plan 15-05)` under Phase 16 SC that SC2/SC5 are now inverted (post-Stop root+step gone, processor intact) — Phase 16 owns its own SC/fact edit.

## Tasks Completed

| Task | Name | Commit | Files |
| ---- | ---- | ------ | ----- |
| 1 | OrchestrationLogs E2E (OBSERV-REDIS-02) + Redis-discipline guard facts | febdb29 | OrchestrationLogsE2ETests.cs, RedisDisciplineGuardFacts.cs |
| 2 | Amend ValidationOrderFacts to per-workflow first-failure scope | f5f8c7f | ValidationOrderFacts.cs |
| 3 | Reconcile REQUIREMENTS.md + ROADMAP.md Phase 15 to CONTEXT amendments | 74a297a | REQUIREMENTS.md, ROADMAP.md |

## Verification Results

- `dotnet build tests/BaseApi.Tests -c Debug` → succeeded, **0 warnings, 0 errors** (both after Task 1 and after Task 2).
- `RedisDisciplineGuardFacts` (`--filter-class`) → **3/3 GREEN** (921ms).
- `ValidationOrderFacts` (`--filter-class`) → **5/5 GREEN** (3.96s) against live compose Postgres + Redis.
- `OrchestrationLogsE2ETests` (`--filter-class`) → **1/1 GREEN** (18.4s) against the live OTLP → collector → Elasticsearch stack — the corrId round-tripped.
- Full Orchestration namespace slice (`--filter-namespace BaseApi.Tests.Features.Orchestration`) → **44/44 GREEN** (3.5s) — no regression across the feature (includes the 3 new guards + 5 ValidationOrderFacts).
- Acceptance greps:
  - `OpenTelemetry.Instrumentation.StackExchangeRedis` in `Directory.Packages.props` → **0 matches** (guard premise confirmed).
  - `Guid.Empty` in `REQUIREMENTS.md` → the only hit is the L2-PROJECT-03 line's historical "earlier `Guid.Empty` placeholder is superseded" phrase; the active jobId spec is `Guid.NewGuid()`.
  - `NO DELETE` / `Stop is idempotent` in `ROADMAP.md` Phase 15 → **0 matches** (SC3 rewritten).
  - `ProcessorKeyTtlDays` in `REQUIREMENTS.md` → present on L2-PROJECT-01 and L2-PROJECT-05.

Note: the runner is Microsoft.Testing.Platform; `dotnet test --filter` is ignored (MTP0001), so per-class/per-namespace runs use the test executable's native `--filter-class` / `--filter-namespace` flags (precedent: Plans 15-02 / 15-03 / 15-04 SUMMARY). The 422 stack trace visible in the ValidationOrderFacts / Orchestration-slice console output is the EXPECTED cycle-validation exception being logged by the request pipeline, not a test failure (all summaries report 0 failed).

## Deviations from Plan

None — plan executed as written. The three tasks landed exactly as specified; the guards, E2E, fact amendment, and doc reconciliation all matched the acceptance criteria on the first verification pass. No Rule 1-4 deviations were needed.

## Notes

- **Pre-existing overlapping guard.** `tests/BaseApi.Tests/Composition/BaseApiCompositionFacts.cs` already has `Solution_Csproj_Does_NOT_Reference_OpenTelemetry_StackExchangeRedis` (csproj-scope OBSERV-REDIS-01 enforcement from an earlier phase). The new `No_OtelRedis_Package_Referenced` guard is broader (also checks `Directory.Packages.props` + loaded assemblies) and lives in the Orchestration feature test folder per this plan's instruction. The overlap is intentional belt-and-suspenders, not a duplicate to remove.
- **OBSERV-REDIS-04 deferred.** Redis metrics are documented as out-of-scope for v3.3.0 (no implementation), per the plan objective.
- The `.planning/phases/*` git-status churn (mass D/??) is pre-existing and unrelated to this plan; only the 5 files above were staged across the three task commits.

## Threat Surface

All four register threats from the plan's `<threat_model>` are addressed:
- **T-15-17** (Redis ops not traceable) — `OrchestrationLogsE2ETests` proves the corrId reaches ES via the MEL AsyncLocal scope after a Start; no action-at-a-distance.
- **T-15-18** (OTel-Redis tracing re-introduced) — `No_OtelRedis_Package_Referenced` negative-grep on `Directory.Packages.props` + all csproj + loaded assemblies.
- **T-15-19** (KEYS/IServer.Keys() re-introduced) — `No_Keys_Enumeration_In_Projection` comment-stripped source guard over the Orchestration feature.
- **T-15-20** (correlationId in ES log doc) — accepted: correlationId is a non-secret, ASCII-sanitized request identifier intended for traceability.

No NEW security-relevant surface beyond the plan's threat model.

## Self-Check: PASSED

- Created files exist: `tests/BaseApi.Tests/Observability/OrchestrationLogsE2ETests.cs`, `tests/BaseApi.Tests/Features/Orchestration/RedisDisciplineGuardFacts.cs` — FOUND.
- Modified files contain expected edits (ValidationOrderFacts per-workflow doc + new fact; REQUIREMENTS amendments with markers; ROADMAP SC rewrites + Phase 16 note) — verified via build + greps + GREEN test runs.
- All task commits exist in git log: febdb29, f5f8c7f, 74a297a — FOUND.
