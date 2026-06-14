---
phase: 67
slug: fault-injection-harness
status: verified
threats_open: 0
asvs_level: 1
created: 2026-06-14
---

# Phase 67 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.
>
> **Scope note.** Phase 67 added zero `src/**` product code. The attack surface is confined to:
> `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` (env-var seam) and
> `scripts/phase-67-harness.ps1` (docker / psql / REST / file-path orchestration).

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| harness env → test fixture | Harness sets `SCENARIO_ID` / `WINDOW_START_UTC` / `WINDOW_END_UTC`; fixture reads them and uses `SCENARIO_ID` to compose a filesystem report path | Env-supplied strings; `SCENARIO_ID` reaches `Path.Combine`; `WINDOW_*_UTC` reach Prometheus query parameters |
| harness → postgres (psql exec) | Harness runs a SQL statement inside the postgres container as superuser via `docker compose exec` | SQL query text; wf-id returned |
| harness → docker engine (compose stop/start/down) | Harness can stop/start/down containers | Compose service names sourced from the in-script scenario table |
| harness → baseapi HTTP (localhost) | Harness POSTs the resolved workflow id to the activation endpoint | Bare GUID array over loopback HTTP |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| 01/T-67-03 | Tampering — path traversal | `scenarioId` → `analyzer-reports/{scenarioId}.json` path composition (`AnalyzerE2ETests.cs`) | mitigate | `ScenarioIdPattern` (`^[A-Za-z0-9_-]+$`) validates the env-supplied `SCENARIO_ID` at line 94, **before** `Path.Combine` at line 99; guard was not weakened | closed |
| 01/T-67-04 | DoS / correctness | malformed `WINDOW_*_UTC` env value | accept | `TryParseUtc` (lines 77-82) falls back to `UtcNow` on any unparseable value — degrades gracefully, never crashes; documented in Accepted Risks Log | closed |
| 02/T-67-01 | Injection — SQLi | psql sentinel wf-id lookup (STEP D, `phase-67-harness.ps1`) | mitigate | `SELECT` is a static string literal at line 196; `-ScenarioId` is never concatenated into SQL | closed |
| 02/T-67-02 | Tampering / unscoped destruction | docker control crash (STEP F, `phase-67-harness.ps1`) | mitigate | Crash targets only `$scenario.targetContainers` (lines 266, 273, 286), drawn from the in-script `[ordered]` table; `$Scenarios.Contains($ScenarioId)` validates before any docker op (line 95); unknown id aborts `exit 64` (line 97); `docker compose down` has no `-v` (line 350) | closed |
| 02/T-67-03 | Information disclosure | activation HTTP call (`phase-67-harness.ps1`) | accept | Hardcoded `http://localhost:8080` loopback (line 209); bare GUID-array body; no auth secret; no external network; documented in Accepted Risks Log | closed |
| 02/T-67-04 | DoS — window starvation | observe-loop / health-wait (`phase-67-harness.ps1`) | mitigate | Observe-loop bounded by `$windowDeadline` (300 s, line 238/250); health-wait bounded by `$svcDeadline` / `$orchDeadline` (90 s each, lines 164, 288); stuck signal aborts with `exit 60` (lines 256, 300) rather than hanging | closed |
| 03/T-67-05 | Tampering — operational | running harness against live stack (`phase-67-harness.ps1`) | accept | Both invocations use only in-table ids (`TEST-01`/`TEST-02`), validated by `$Scenarios.Contains`; crash scoped to `processor-sample`; `docker compose down` (no `-v`) preserves volumes; operator-launched local dev stack; documented in Accepted Risks Log | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-67-01 | 01/T-67-04 | A malformed `WINDOW_START_UTC` or `WINDOW_END_UTC` env value causes `TryParseUtc` to return `false`, and the fixture falls back to `DateTimeOffset.UtcNow` for the affected timestamp. This degrades the window-pinned scoring path to the standalone live-snapshot default rather than crashing the test run. The harness emits round-trip `o`-format timestamps via PowerShell `.ToString("o")`, so malformed input is not normally reachable in practice. | GSD secure auditor | 2026-06-14 |
| AR-67-02 | 02/T-67-03 | The activation POST is made to hardcoded `http://localhost:8080` (loopback only). The request body is a bare JSON GUID array resolved from the postgres `workflows` table; it contains no auth secret or sensitive credential. The endpoint is not reachable from outside the local machine. Risk surface is limited to the local development stack and accepted as low. | GSD secure auditor | 2026-06-14 |
| AR-67-03 | 03/T-67-05 | Running the harness against a live stack uses only the two in-table scenario ids (`TEST-01`, `TEST-02`), validated by `$Scenarios.Contains` before any destructive op. The crash is scoped strictly to `processor-sample` as declared in the table. `docker compose down` (never with `-v`) preserves all data volumes. This is an operator-launched local dev tooling script with no CI/CD or production exposure; the residual operational risk is accepted. | GSD secure auditor | 2026-06-14 |

---

## Evidence Notes

### 01/T-67-03 — Path-traversal guard (CLOSED)

File: `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`

- Line 68: `private static readonly Regex ScenarioIdPattern = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);`
- Line 88: `var scenarioId = Environment.GetEnvironmentVariable("SCENARIO_ID") ?? DefaultScenarioId;`
- Lines 93-95: `Assert.True(ScenarioIdPattern.IsMatch(scenarioId), ...)` — guard runs before any path composition.
- Line 99: `var reportPath = Path.Combine(reportsDir, $"{scenarioId}.json");` — path composed only after guard passes.
- Line 184: second `Path.Combine` for the `.txt` companion also follows the same guarded `scenarioId`.

The whitelist rejects any value containing `/`, `\`, `.`, `..`, `%`, or other traversal characters. The guard is present, runs before `Path.Combine`, and was not weakened.

### 01/T-67-04 — Malformed window timestamp fallback (CLOSED / ACCEPTED)

File: `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`

- Lines 77-82: `TryParseUtc` wraps `DateTimeOffset.TryParse` with `AssumeUniversal | AdjustToUniversal`; returns `false` on null/empty/malformed input.
- Lines 115-117: both `WINDOW_START_UTC` and `WINDOW_END_UTC` are passed through `TryParseUtc`; `windowPinned` is `false` if either fails.
- Lines 119, 139: when `windowPinned` is `false`, `windowStartUtc` and `snapshotUtc` fall back to `DateTimeOffset.UtcNow` — no crash, no assertion failure from parsing.

### 02/T-67-01 — Static psql literal (CLOSED)

File: `scripts/phase-67-harness.ps1`

- Line 196: `-c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'"` — literal string; `$ScenarioId`, `$wfId`, or any operator input is never concatenated into this value.

### 02/T-67-02 — Scoped docker control (CLOSED)

File: `scripts/phase-67-harness.ps1`

- Lines 95-98: `$Scenarios.Contains($ScenarioId)` guard runs before any docker op; unknown id exits 64.
- Line 99: `$scenario = $Scenarios[$ScenarioId]` — the bound scenario object is the only source for `targetContainers`.
- Lines 266, 273, 286: `foreach ($svc in $scenario.targetContainers)` — the loop iterates only over the table-declared list; `$ScenarioId` string is never passed to `docker compose stop/start`.
- Line 350: `docker compose down | Out-Null` — no `-v` flag; volumes preserved.
- Confirmed: `grep 'compose down -v'` returns zero matches.

### 02/T-67-03 — Loopback-only HTTP activation (CLOSED / ACCEPTED)

File: `scripts/phase-67-harness.ps1`

- Line 209: `-Uri 'http://localhost:8080/api/v1/orchestration/start'` — hardcoded loopback; not parameterised from user input.

### 02/T-67-04 — Bounded poll loops (CLOSED)

File: `scripts/phase-67-harness.ps1`

- Line 238: `$windowDeadline = (Get-Date).AddSeconds(300)` — observe-loop deadline.
- Line 250: `while ((Get-Date) -lt $windowDeadline)` — loop exits when deadline reached.
- Line 256: `exit 60` on N-fires timeout — aborts loud.
- Line 164: `$orchDeadline = (Get-Date).AddSeconds(90)` — orchestrator health-wait deadline.
- Line 288: `$svcDeadline = (Get-Date).AddSeconds(90)` — per-service crash-recovery health-wait deadline.
- Lines 175, 300: `exit 25` / `exit 60` on deadline breach — never hangs.

### 03/T-67-05 — Operational run scoping (CLOSED / ACCEPTED)

Plan 03 adds no code. Both reference invocations pass only `TEST-01` and `TEST-02`, which are validated by the `$Scenarios.Contains` guard from Plan 02. See AR-67-03.

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-06-14 | 7 | 7 | 0 | GSD secure auditor (claude-sonnet-4-6) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-06-14
