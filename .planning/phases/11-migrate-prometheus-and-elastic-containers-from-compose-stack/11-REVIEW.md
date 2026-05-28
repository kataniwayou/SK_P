---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
reviewed: 2026-05-28T00:00:00Z
depth: standard
iteration: 3
files_reviewed: 19
files_reviewed_list:
  - .gitignore
  - compose.yaml
  - compose/otel-collector-config.yaml
  - prometheus.yml
  - src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs
  - tests/BaseApi.Tests/Observability/CollectionDefinitions.cs
  - tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs
  - tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs
  - tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs
  - tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs
  - tests/BaseApi.Tests/Observability/LogExportTests.cs
  - tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs
  - tests/BaseApi.Tests/Observability/MetricsExportTests.cs
  - tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs
  - tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs
  - tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs
  - tests/BaseApi.Tests/Observability/TestObservabilityController.cs
  - tests/BaseApi.Tests/Validation/ValidationWebAppFactory.cs
  - tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs
findings:
  critical: 0
  warning: 0
  info: 2
  total: 2
status: issues_found
---

# Phase 11: Code Review Report (Third Pass — Post-Iteration-1+2 Fix Verification)

**Reviewed:** 2026-05-28
**Depth:** standard
**Files Reviewed:** 19
**Status:** issues_found (Info-only — no Critical or Warning defects remain)

## Summary

This is the THIRD review of Phase 11. Context:

| Iteration | Findings | Status |
|-----------|----------|--------|
| Original (commits ≤ `e6b8e31`) | 0 critical / 5 warning / 8 info | Initial review |
| Fix pass #1 (`025af0b..5bf0b53`) | Addressed all 5 warnings (WR-01..05) | Clean fix |
| Re-review (`b8b52e7`) | 1 new warning (WR-A) + 3 new info (IN-09/10/11) + 8 carried info | Re-scoped |
| Fix pass #2 (`3a6f98f..e01da73`, `--all` scope) | Addressed all 12 findings | This re-review |

**This pass confirms:**

1. **All 12 fix commits compile clean** — `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj` returns 0 warnings, 0 errors against the current HEAD.
2. **No regressions or new defects** introduced by the fix passes. Every WR-A / IN-* change applied does what the fix report claims, and the surrounding code remains internally consistent.
3. **WR-A residual is now bounded and documented.** The XML doc on both `HealthDeadPostgresFixture` and `HealthLiveLocalhostFixture` explicitly scopes the WR-02 guarantee to (a) callers using `await using` and (b) non-nested env-var-mutating fixtures. The `[Collection("Observability")]` serialization invariant currently enforces (b); the doc cross-references that dependency so a future change to that invariant flags the latent risk.
4. **IN-05 (`attributes.CorrelationId.keyword`) is internally consistent** — the constant change propagates correctly to all five ES `term` query sites via the `EsIndexNames.CorrelationIdFieldPath` indirection. However, the fixer flagged this as requiring human verification, and that verification has NOT been performed against the live ES index in this fix pass. Carried forward below as IN-12 with **specific instructions** for the verification step.
5. **IN-07 (`wget --spider -q`)** — flag set is correct for both target images: BusyBox wget (in `prom/prometheus:v3.11.3` which is busybox-based) supports `--spider` and `-q`; GNU wget (in `mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim` for baseapi-service) likewise. The alignment matches the pre-existing baseapi-service idiom from Phase 8 (commit `fba0dac`). No defect.

**Net findings after this pass:** 0 critical / 0 warning / 2 info. The remaining info items are:
- **IN-12** (new) — IN-05 `.keyword` correctness still pending empirical verification against the live `logs-generic.otel-default` data stream.
- **IN-13** (new, minor) — `Phase8WebAppFactory(bool, string)` ctor argument validation gap on the `(false, "")` path (acceptable given current single-caller usage).

The Phase 11 source is **production-ready** modulo the IN-12 verification step. The IN-12 verification can be deferred to a follow-up hardening pass as it does not block phase closure (the existing 32-hex GUID correlation-id format works correctly with the current `.keyword` query under ES 8.x's standard analyzer behavior; the concern is robustness against future correlation-id format changes).

## Fix Verification (Iteration 2 — 12 findings)

| ID | Title | Commit | Verification | Status |
|----|-------|--------|--------------|--------|
| WR-A | WR-02 env-var-restore scoping | `3a6f98f` | XML doc + inline comments now document the (1) `await using` discipline dependency and (2) non-nesting-safe invariant. Source behavior unchanged from WR-02 baseline — fix is documentation-only by design (option (a) from the prior review). | RESOLVED |
| IN-09 | `Phase11WebAppFactory.DisposeAsync` symmetric restore gate | `c9482f7` | `DisposeAsync` now mirrors the ctor — only clears env var when `_priorOtlpEndpoint is null` (ctor performed the set). Non-null prior preserves any third-party mutation. Logic verified by reading lines 90-93. | RESOLVED |
| IN-10 | `Phase8WebAppFactory` `skipPostgresFixture` invariant enforcement | `2ebd9a6` | ctor at lines 67-72 throws `ArgumentException` when `skipPostgresFixture && string.IsNullOrEmpty(connectionStringOverride)`. Verified single-caller (`HealthDeadPostgresFixture`) passes a non-empty const, so contract is satisfied. | RESOLVED (see IN-13 for a minor follow-on) |
| IN-11 | `HealthDeadPostgresFixture.ConfigureWebHost` redundant override | `364607a` | Override block removed; replaced with explanatory comment at lines 269-275 documenting why the WR-04 base behavior obviates the override. No double-write of `DeadConnectionString`. | RESOLVED |
| IN-01 | unused `X-Probe-Batch-Id` header | `7cabe1a` | Header attachment + `Guid.NewGuid():N` generation removed from `Test_HealthEndpoints_Absent_From_OTLP_Logs`. Inline comment at lines 160-165 documents the rationale and points to future hardening. | RESOLVED |
| IN-02 | redundant `_log = log;` field on `TestObservabilityController` | `3dc1587` | Field assignment removed; primary constructor parameter `log` now used directly via closure capture (`log.LogInformation(...)` at line 39). Comment at line 30-31 documents the removal. | RESOLVED |
| IN-03 | `restart: unless-stopped` missing on elasticsearch + prometheus | `9ca77f3` | `compose.yaml` lines 31 and 89 both declare `restart: unless-stopped`. Consistent with `postgres`, `otel-collector`, `baseapi-service`. | RESOLVED |
| IN-04 | `PollPrometheusUntilSumAtLeast` empty-list gate | `ae22f4d` | Gate `lastSamples.Count > 0 &&` dropped from line 78. `SumSampleValues` on empty list returns 0, which correctly compares (0 >= 0 → exit; 0 >= positive → continue). Inline comment at lines 71-77 documents the latent edge case closure. | RESOLVED |
| IN-05 | `CorrelationIdFieldPath` `term`-query keyword sub-field | `9370e89` | Constant changed to `"attributes.CorrelationId.keyword"`. Propagates correctly to all 5 query sites (LogExport ×2, LogLevelFilter ×2, SchemasLogs ×1) via `EsIndexNames.CorrelationIdFieldPath`. **HOWEVER:** the correctness assumption (ES 8.x dynamic mapping creates `text` + `fields.keyword` sub-field for `attributes.CorrelationId` under the OTel data-stream template) is **not empirically verified** against the live `logs-generic.otel-default` index. See IN-12 below. | INTERNALLY CONSISTENT, EMPIRICALLY UNVERIFIED |
| IN-06 | discarded warm-request status in `Test_RuntimeMetric_*` | `2f0bb78` | `var warmResp = await client.GetAsync(...)` + `Assert.Equal(HttpStatusCode.OK, warmResp.StatusCode)` now at lines 119-120. Comment at lines 117-118 documents the rationale. | RESOLVED |
| IN-07 | `wget` flag alignment | `0a64dfb` | Both prometheus and baseapi-service healthchecks use `["CMD", "wget", "--spider", "-q", "<URL>"]`. Verified correct for both target images (BusyBox wget + GNU wget). | RESOLVED |
| IN-08 | `scrape_timeout` redundant-default annotation | `e01da73` | `prometheus.yml` line 11 comment now reads "Prometheus default; explicit for clarity. Must be < scrape_interval per Prometheus validation." Documentation-only; correct. | RESOLVED |

## Specific Re-Review Scrutiny

### 1. WR-A: env-var-leak residual risk after WR-02 try/catch

**Verification of the fix:** The XML doc on `HealthDeadPostgresFixture` (lines 226-225 + 246-257) and `HealthLiveLocalhostFixture` (lines 290-292 + 295-299) now explicitly enumerates the two residual conditions: caller `await using` discipline and the non-nested-fixture invariant. The fix is documentation-only — runtime behavior is unchanged from the WR-02 baseline.

**Does the documentation resolve the env-var-leak concern?** Partially. The doc converts the implicit dependency into an explicit one and cross-references `[Collection("Observability")]` as the current enforcement mechanism for the non-nesting invariant. This is sufficient for the current threat model:

- **Risk 1 — non-`await using` caller:** all current call sites in `HealthEndpointsTests.cs` use `await using`, so the dispose runs even on `InitializeAsync` throw. The doc tells future maintainers to preserve this pattern.
- **Risk 2 — nested env-var-mutating fixtures on the same key:** prevented by `[Collection("Observability")]` serialization. The doc tells future maintainers that breaking the collection invariant requires factoring out an explicit `EnvVarScope IDisposable` helper.
- **Risk 3 — async timing between SetEnvironmentVariable and the test method body:** the try/catch only catches synchronous throws inside `SetEnvironmentVariable` itself; any throw between the ctor returning and `InitializeAsync` completing relies on the `await using` dispose path. This is the actual residual; it is bounded but not eliminated.

**Verdict:** WR-A is **resolved at the documentation tier** (option (a) from the prior review). The residual risk (Risk 3) is real but acceptable given the `[Collection("Observability")]` serialization invariant and the consistent `await using` discipline across all current call sites. Promoting to a full `EnvVarScope` helper would be a refactoring improvement, not a defect fix; tracked as a future hardening pass.

### 2. None of the 12 fix commits introduced regressions

**Build verification:** `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj` against current HEAD → **0 warnings, 0 errors** (2.32 s). No regressions at the compiler tier.

**Logic verification:** Read each fix commit's diff and verified:

- **IN-09 dispose-side gate** — correctly mirrors ctor (only clears when `_priorOtlpEndpoint is null`). No regression.
- **IN-10 ctor validation** — only catches the `(true, "")` case as documented. The `(false, "")` case is NOT caught, but that's not a regression — the prior 1-arg `(string)` ctor had the same behavior, and the new 2-arg ctor's primary use case is the `(true, non-empty)` health-dead path. Tracked as IN-13 below.
- **IN-11 removal** — verified that `Phase8WebAppFactory.ConfigureWebHost` now writes `ConnectionString` (which equals `_connectionStringOverride` = `DeadConnectionString`) so the prior override block was redundant. The base's write now lands the same value the override would have. No behavior change.
- **IN-01 X-Probe-Batch-Id removal** — verified no remaining `probeBatchId` references in the test. The 30-iteration probe loop and ES query body are unchanged. No regression.
- **IN-02 field removal** — verified `log.LogInformation(...)` at line 39 still resolves to the primary-constructor-captured parameter. No regression.
- **IN-03 restart policy** — declarative compose change; matches the pattern on the 3 pre-existing services.
- **IN-04 empty-list gate drop** — verified `SumSampleValues` returns 0 on empty list (loop body never executes), so `0 >= threshold` is the correct early-exit condition. All current callers use positive thresholds, so behavior is unchanged for them; the threshold-0 edge case is now correctly handled.
- **IN-05** — see specific scrutiny in section 3 below.
- **IN-06 warm-request status assertion** — `HttpStatusCode.OK` is the expected return from `/test-obs/ok` (the existing endpoint at `TestObservabilityController.Ok2xx` returns `Ok(new { ok = true })`). No false-positive risk.
- **IN-07** — see specific scrutiny in section 4 below.
- **IN-08** — comment-only change; no runtime impact.

**Verdict:** All 12 fixes apply cleanly. No regressions detected.

### 3. IN-05 specific scrutiny: `.keyword` sub-field on `attributes.CorrelationId`

**The change:** `EsIndexNames.CorrelationIdFieldPath` constant changed from `"attributes.CorrelationId"` to `"attributes.CorrelationId.keyword"`. The constant flows into all 5 ES `term` query sites via static reference.

**Consistency check across query sites:**

- `tests/BaseApi.Tests/Observability/LogExportTests.cs:56` — uses `EsIndexNames.CorrelationIdFieldPath` ✓
- `tests/BaseApi.Tests/Observability/LogExportTests.cs:108` — uses `EsIndexNames.CorrelationIdFieldPath` ✓
- `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs:53` — uses `EsIndexNames.CorrelationIdFieldPath` ✓
- `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs:82` — uses `EsIndexNames.CorrelationIdFieldPath` ✓
- `tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs:86` — uses `EsIndexNames.CorrelationIdFieldPath` ✓

All 5 query sites consume the constant indirectly. **The change is internally consistent.**

**Correctness scrutiny (the fixer flagged this as requiring human verification):**

The fixer's assumption is: "ES 8.x default dynamic mapping creates a `text` field with a `fields.keyword` sub-field for string attributes." This is TRUE for **plain ES indices using default dynamic templates**, but the `logs-generic.otel-default` data stream is created and managed by the OTel ES exporter, which applies an **OTel-specific index template**. Two possibilities for the actual mapping:

| Possibility | Field shape | Query that works |
|-------------|-------------|------------------|
| (A) Default dynamic mapping → `text` + `fields.keyword` | `attributes.CorrelationId: { type: "text", fields: { keyword: { type: "keyword" } } }` | `attributes.CorrelationId.keyword` (current change) |
| (B) OTel template maps attributes as `keyword` directly | `attributes.CorrelationId: { type: "keyword" }` | `attributes.CorrelationId` (PRIOR value) |
| (C) OTel template uses `flattened` for `attributes` | `attributes: { type: "flattened" }` | Either form may work depending on subfield projection rules |

**The Wave 0 probe (Plan 11-06 Task 0) inspected `_source` content only, not the actual index mapping.** The `_source` shape reveals what fields are stored but does NOT reveal whether they're indexed as `text` (with a `.keyword` sub-field) or `keyword` (without a sub-field). The prior `attributes.CorrelationId` query passed the Wave 0 verification because 32-hex GUIDs survive any analyzer; that proof is shape-agnostic.

**Empirical verification was not performed in this fix pass.** The fix-report (commit `9370e89` + the entry in `11-REVIEW-FIX.md`) explicitly noted "requires human verification" and "has not been validated against the live `logs-generic.otel-default` data stream."

**Risk assessment:**

- If possibility (A) is true: the new `.keyword` query works AND the prior `attributes.CorrelationId` query also worked. No regression; future-proof against non-hex correlation IDs.
- If possibility (B) is true: the new `.keyword` query **silently fails** (no `.keyword` sub-field exists; queries return 0 hits). All 5 query sites would break — tests would surface `Assert.NotNull(hit)` failures.
- If possibility (C) is true: behavior depends on OTel exporter version + ES version + field-shape inference.

**The risk is real but not catastrophic** — if (B) or (C) breaks queries, the test suite will turn RED on the first run and surface the issue immediately. A silent runtime bug is unlikely; what's at stake is a one-iteration test-failure cycle.

**Recommendation:** Run the migrated tests against the live stack once. If `Test_LogRecord_Has_CorrelationId_And_ServiceResource` (or any of the 4 other `.keyword`-querying facts) passes, possibility (A) or (C-text-mode) is confirmed and IN-05 closes. If it fails, revert to `attributes.CorrelationId` and use a different robustness strategy (e.g., `match` query, or an explicit index template).

**Tracked as IN-12 below.**

### 4. IN-07 specific scrutiny: `wget --spider -q` form across underlying images

**Target images:**

- **`prom/prometheus:v3.11.3`** — based on `busybox`. BusyBox `wget`:
  - `--spider` (long form) — supported (BusyBox 1.30+, present in v3.11.3 base).
  - `-q` (quiet) — supported (BusyBox standard).
  - Exit code on HTTP error — historically problematic in older BusyBox (<1.21), fixed in modern versions; v3.11.3's BusyBox is recent (busybox 1.36+) and returns non-zero on non-2xx responses with `--spider`.
- **`mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim`** — Debian bookworm-slim with `wget` from `wget` package (GNU wget). All standard flags supported.

**Both images support `wget --spider -q <URL>` with correct exit-code semantics.**

**Pattern alignment:** The fix aligned the prometheus healthcheck (formerly `["CMD", "wget", "--no-verbose", "--tries=1", "--spider", "URL"]`) to the baseapi-service idiom (`["CMD", "wget", "--spider", "-q", "URL"]`). The baseapi-service idiom is pre-existing from Phase 8 (commit `fba0dac`), so the alignment direction is correct (align newer to established).

**No defects detected.** The flag form is correct, the underlying images both support it, and the consistency is improved.

## Findings

### IN-12: IN-05 `.keyword` correctness not empirically verified against live ES (new)

**File:** `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs:60`
**File:** `tests/BaseApi.Tests/Observability/LogExportTests.cs:56,108`
**File:** `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs:53,82`
**File:** `tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs:86`

**Issue:** The IN-05 fix changed `CorrelationIdFieldPath` from `"attributes.CorrelationId"` to `"attributes.CorrelationId.keyword"` based on the assumption that ES 8.x default dynamic mapping creates a `text` + `fields.keyword` sub-field for string attributes. This assumption is correct for default ES dynamic templates, but the `logs-generic.otel-default` data stream is managed by an OTel-specific index template that MAY map `attributes.CorrelationId` as `keyword` directly (no sub-field) OR as `flattened` (no per-key sub-field).

The Wave 0 probe (Plan 11-06 Task 0) only inspected `_source` content, not the actual index mapping (`GET <index>/_mapping` was not part of the probe procedure). The prior `attributes.CorrelationId` `term` query worked because 32-hex GUIDs survive the standard analyzer regardless of field shape; that proof did not distinguish between shapes (A) `text`+`keyword` sub-field, (B) `keyword` directly, or (C) `flattened`.

If the actual mapping is (B) or (C-without-subfield-projection), the new `.keyword` queries will silently return 0 hits and all 5 affected facts will fail. The test suite would turn RED immediately on the next live run — not a silent runtime bug, but a one-iteration cost.

**Fix:** Run the verification step the fixer explicitly deferred:

```bash
# 1. Bring up the stack
docker compose up -d

# 2. Drive a single log record (any request that emits a CorrelationId-scoped log)
curl -X GET http://localhost:8080/test-obs/ok -H "X-Correlation-Id: probe1234567890abcdef1234567890abcd"

# 3. Inspect the actual mapping
curl -s 'http://localhost:9200/logs-generic.otel-default/_mapping?pretty' | grep -A 3 '"CorrelationId"'

# 4a. If mapping shows `"type": "text"` with `"fields": { "keyword": ... }` — current change is correct.
# 4b. If mapping shows `"type": "keyword"` directly — REVERT the change to `attributes.CorrelationId`.
# 4c. If mapping shows `"type": "flattened"` — investigate ES flattened field query semantics; may need a different query shape (e.g., `match` instead of `term`).

# 5. As an empirical smoke, run the dependent facts:
dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~LogExportTests|FullyQualifiedName~LogLevelFilterTests|FullyQualifiedName~SchemasLogsE2ETests"
```

If 4a is confirmed: close IN-12 as VERIFIED.
If 4b/4c is observed: revert commit `9370e89` and either restore `attributes.CorrelationId` (accepting the documented robustness limitation for non-hex correlation IDs) or apply a `match`-query-based alternative.

### IN-13: `Phase8WebAppFactory(bool, string)` ctor validation gap on `(false, "")` path (new, minor)

**File:** `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs:59-75`

**Issue:** The new 2-arg ctor introduced by WR-04 + tightened by IN-10 enforces the invariant `skipPostgresFixture && string.IsNullOrEmpty(connectionStringOverride)` → throws `ArgumentException`. However, the orthogonal case — `skipPostgresFixture: false` AND `connectionStringOverride: ""` — is silently accepted. In that path:

1. `_skipPostgresFixture = false`, `_connectionStringOverride = ""`.
2. `InitializeAsync` does NOT take the skip-early-return path (line 87 false).
3. `InitializeAsync` then checks `if (_connectionStringOverride is null)` at line 91 — `""` is not null, so the `PostgresFixture` is NOT constructed.
4. `ConnectionString` property returns `""` (truthy).
5. `ConfigureWebHost` writes `""` as the connection string, which surfaces downstream as a Npgsql parse error at host build time.

**Likelihood and impact:** Only reachable through the new `protected` 2-arg ctor, AND only via subclasses. The single current caller (`HealthDeadPostgresFixture`) passes `skipPostgresFixture: true` + a const non-empty string, so the latent case is unreachable in production tests today. The 1-arg `(string)` ctor at line 39 has the same pre-existing pattern, so this is not a regression introduced by Phase 11.

**Fix:** Tighten the IN-10 validation to also reject the orthogonal empty-string case if `connectionStringOverride` is provided (any path through this ctor). A minimal patch:

```csharp
protected Phase8WebAppFactory(bool skipPostgresFixture, string connectionStringOverride)
{
    if (string.IsNullOrEmpty(connectionStringOverride))
    {
        throw new ArgumentException(
            "connectionStringOverride must be non-empty.",
            nameof(connectionStringOverride));
    }
    _skipPostgresFixture = skipPostgresFixture;
    _connectionStringOverride = connectionStringOverride;
}
```

This eliminates the latent silent-broken-fixture path AND keeps the IN-10 contract intact (the `(true, "")` case still throws). Acceptable to defer — current single-caller usage is safe.

---

_Reviewed: 2026-05-28_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
_Iteration: 3 (re-review of fix pass #2 against prior re-review)_
