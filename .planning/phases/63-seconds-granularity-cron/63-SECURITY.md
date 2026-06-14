---
phase: 63
slug: seconds-granularity-cron
status: verified
threats_open: 0
asvs_level: 1
created: 2026-06-14
---

# Phase 63 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| client → API (`POST`/`PUT /api/v1/workflows`) | The untrusted cron string crosses here. `BeValidStandardCron` (both Create + Update validators) is the V5 Input Validation gate that accepts/rejects before the expression is stored and later scheduled. Primary in-scope surface for the phase. | Cron expression string (5- or 6-field) |
| (internal, no new boundary) | `CronFieldForm` detector and `CronInterval` scheduler math consume an already-validated/stored cron string. No new external input boundary; the UTC `nowUtc` contract is the only correctness precondition (preserved, D-07). | In-process string |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-63-01 | Tampering (malformed input) | `CronFieldForm.FieldCount` tokenization | mitigate | `IsNullOrWhiteSpace` guard → 0; `Split((char[]?)null, RemoveEmptyEntries)` collapses whitespace/tabs without throwing; non-5/6 counts → `IsValidFieldCount` false. `CronFieldForm.cs:21-23`, `:18`. String never eval'd/shelled. | closed |
| T-63-02 | Information Disclosure | contracts-leaf dependency surface | accept | Pure `System` string logic — no Cronos `PackageReference` in `Messaging.Contracts.csproj` (no `ItemGroup`), no `using Cronos` in `CronFieldForm.cs`. Dependency surface NOT expanded (D-03/D-05). | closed |
| T-63-03 | Tampering (malformed → exception) | `CronInterval.{NextOccurrence,IntervalSeconds}` `CronExpression.Parse` | accept | Scheduler only parses already-validated crons (gated by T-63-05). Format chosen up front via `CronFieldForm.IsSecondsForm`, never catch-retry. No `try/catch` control-flow added. `CronInterval.cs:28-29,39-43`. | closed |
| T-63-04 | Denial of Service (fast-cron `* * * * * *` = 1s) | scheduler fire cadence | accept (by design) | D-06 intentionally declines a minimum-interval floor this milestone. No floor exists in `CronInterval.cs`; sub-second rate-guarding is the explicitly-deferred validation-policy change. Documented product decision. | closed |
| T-63-05 | Tampering (malformed/oversized input) | `BeValidStandardCron` (both `WorkflowCreateDtoValidator` + `WorkflowUpdateDtoValidator`) | mitigate | `IsValidFieldCount` rejects non-5/6 up front without throwing (D-02), then ONE guarded `CronExpression.Parse(expr, format)` inside `try/catch (CronFormatException)` → 422/400. No bare `Parse(expr)`. Both validators byte-identical + tested (D-09). `WorkflowDtoValidator.cs:67-70,117-121`. | closed |
| T-63-06 | Denial of Service (1s cron now accepted) | validator acceptance set | accept (by design) | D-06 declines a minimum-interval floor; accepting a 1s cron is a deliberate product decision. No floor in either validator. Documented and bounded. | closed |
| T-63-07 | Repudiation / misleading UX (stale message) | validator `.WithMessage` text | mitigate | Both messages updated (D-11) to "valid 5- or 6-field cron expression" so a valid 6-field cron is not rejected with stale "5-field only" text. `WorkflowDtoValidator.cs:53,106`. | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-63-01 | T-63-04, T-63-06 | Minimum-interval floor declined this milestone (D-06). Accepting a 1s/sub-minute cron is a deliberate product decision required to enable the 30s-cadence fan-out workflow; a sub-second rate guard is a future validation-policy change. LOW severity, ASVS L1, no HIGH. | User (D-06) | 2026-06-14 |
| AR-63-02 | T-63-02 | The contracts leaf (`Messaging.Contracts`) stays Cronos-free by design (D-03/D-05) — the detector is pure string logic. This structurally *reduces* the dependency attack surface rather than expanding it; accepted as out-of-scope/by-design. | User (D-03/D-05) | 2026-06-14 |

*Accepted risks do not resurface in future audit runs.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-06-14 | 7 | 7 | 0 | gsd-security-auditor (sonnet) |

Notes: 3 `mitigate` threats (T-63-01, T-63-05, T-63-07) verified present with exact file:line evidence against committed code; 4 `accept` threats confirmed by absence of the forbidden patterns (no min-interval floor, no Cronos dependency, no catch-retry control-flow) and alignment with documented product decisions D-03/D-05/D-06. No implementation gaps. All threats LOW severity (ASVS L1 V5); no injection surface (cron parsed by Cronos, never eval'd/shelled).

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-06-14
