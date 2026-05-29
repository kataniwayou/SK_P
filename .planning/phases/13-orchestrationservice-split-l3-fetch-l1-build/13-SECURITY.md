---
phase: 13
slug: orchestrationservice-split-l3-fetch-l1-build
status: verified
threats_open: 0
asvs_level: 1
created: 2026-05-29
---

# Phase 13 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| client → OrchestrationController | Untrusted `List<Guid>` Start/Stop body crosses here; gated upstream by the auto-discovered `WorkflowIdsValidator` (FluentValidation) — unchanged by this phase. | Workflow GUIDs |
| OrchestrationService → BaseDbContext (Postgres) | Internal trusted boundary; read-only `AsNoTracking` batch reads (5 entity tables + 3 junctions). No write surface, no migration, no schema change. | Entity rows (no PII fields) |
| loader (BFS) → Postgres | Read-only per-wave `AsNoTracking` step/junction reads during graph traversal. | Step ids + junction rows |
| test assembly → internal seams | `InternalsVisibleTo("BaseApi.Tests")` lets tests resolve `IWorkflowGraphLoader` + substitute internal doubles. Same-solution, same trust domain. | Internal types (test-only) |
| ConfigureTestServices doubles → pipeline | Throwing writer + recording loader are test-only substitutions; never ship in production DI. | None (test-only) |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-13-01 | Tampering | Existence-check `Where(w => ids.Contains(w.Id))` | accept (inherited control) | Parameterized EF Core LINQ → Npgsql `= ANY(@ids)`; no string concatenation. Same query as pre-split v3.2.0 code; no new injection surface. | closed |
| T-13-02 | Information Disclosure | `NotFoundException(nameof(WorkflowEntity), join(missing))` → RFC 7807 body | accept (inherited control) | Error body echoes only GUIDs (no DB internals/PII); shape byte-locked by existing facts. Unchanged by the split. | closed |
| T-13-03 | Denial of Service | No new input surface in the structural-split plan (13-01) | accept (n/a this plan) | No traversal loop added in 13-01; the cyclic-graph DoS surface is introduced in 13-02 and mitigated there (T-13-05). | closed |
| T-13-04 | Elevation of Privilege | New internal seams + `InternalsVisibleTo("BaseApi.Tests")` | accept | `InternalsVisibleTo` exposes internals ONLY to the test assembly (same-solution trust domain) — standard test-access pattern (Phase 8/12 precedent). Seams stay `internal`; no production privilege change. | closed |
| T-13-05 | Denial of Service | `LoadStepsBreadthFirstAsync` over a cyclic graph (A→B→A) | mitigate | **VERIFIED:** `List<Guid> visited` guard (`WorkflowGraphLoader.cs:142`) skips already-visited step ids before enqueuing the next wave (`:151`, `:171`) and `break`s when a wave loads nothing new (`:152`) → BFS terminates on cycles while the cycle-rejecting validator is still a no-op. | closed |
| T-13-06 | Tampering | All `Where(ids.Contains(x.Id))` batch reads | accept (inherited control) | Parameterized EF Core LINQ → Npgsql `= ANY(@ids)`; no string concatenation. Same parameterization as the v3.2.0 existence check. | closed |
| T-13-07 | Denial of Service | Unbounded graph SIZE (very large workflow) | accept (out of scope) | No graph-size gate beyond the existing 1 MB Assignment-payload cap. Per RESEARCH §Security: out of P13 scope; flag for Phase 14+ if needed. Documented in Accepted Risks. | closed |
| T-13-08 | Information Disclosure | `WorkflowGraphSnapshot.Dispose()` diagnostic log line | mitigate | **VERIFIED:** `Logger.LogDebug("L1 snapshot disposed")` (`WorkflowGraphSnapshot.cs:51`) is a fixed string literal — NO interpolation, NO entity payloads/PII (Security V7). | closed |
| T-13-09 | Tampering | Test doubles registered via ConfigureTestServices | accept | Doubles exist ONLY in the test assembly under `WithWebHostBuilder`; production `AddOrchestrationFeature` registrations are unaffected. No production code-path change. | closed |
| T-13-10 | Denial of Service | Cycle-termination verification fact (A→B→A) | mitigate (verification) | **VERIFIED:** the SC5 cycle fact (`WorkflowGraphLoaderFacts.cs`) uses a `Task.WhenAny` timeout guard so a regression in the `visited` guard fails the test rather than hanging CI — this fact IS the DoS-mitigation verification for T-13-05. | closed |
| T-13-11 | Information Disclosure | 500 error body from the forced-throw cleanup test | accept | The forced `InvalidOperationException` surfaces as a generic RFC 7807 500 with no payload/PII (existing handler behavior); the test asserts disposal, not body contents. No new disclosure surface. | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-13-01 | T-13-01, T-13-06 | SQL injection on id-filter reads is mitigated by EF Core/Npgsql parameterization (`= ANY(@ids)`); inherited control from v3.2.0, no new surface. | User | 2026-05-29 |
| AR-13-02 | T-13-02, T-13-11 | RFC 7807 error bodies echo only GUIDs / generic 500s — no DB internals or PII. Byte-locked by existing facts. | User | 2026-05-29 |
| AR-13-03 | T-13-04, T-13-09 | `InternalsVisibleTo("BaseApi.Tests")` + test-only DI doubles are confined to the same-solution test trust domain; no production privilege/path change. | User | 2026-05-29 |
| AR-13-04 | T-13-07 | Unbounded workflow-graph SIZE has no dedicated gate in v3.3.0 (only the 1 MB Assignment-payload cap). Explicitly out of P13 scope per RESEARCH §Security; revisit in Phase 14+ if a graph-size DoS limit is warranted. | User | 2026-05-29 |

*Accepted risks do not resurface in future audit runs.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-05-29 | 11 | 11 | 0 | /gsd-secure-phase (artifact-derived, code-verified) |

Audit method: State B (no prior SECURITY.md). Threat register built from the `<threat_model>` blocks of 13-01/02/03-PLAN.md + 13-03-SUMMARY threat flags (none new). The 3 `mitigate` threats (T-13-05, T-13-08, T-13-10) were verified against the live codebase (`WorkflowGraphLoader.cs:142/151/152/159/171`, `WorkflowGraphSnapshot.cs:51`, `WorkflowGraphLoaderFacts.cs` timeout guard) — corroborated by the passing phase verification (9/9) and code review (`13-REVIEW.md`, 0 critical). The 8 `accept` threats are documented in the Accepted Risks Log above. No open threats; no auditor escalation required.

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-05-29
