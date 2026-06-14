---
phase: 66-prometheus-es-analyzer-pass-fail-engine
security_audit: true
asvs_level: standard
audited: 2026-06-14
auditor: gsd-secure-phase
status: SECURED
threats_open: 0
threats_total: 11
---

# Phase 66 Security Audit

**Phase:** 66 — prometheus-es-analyzer-pass-fail-engine
**ASVS Level:** standard
**Threats Closed:** 11 / 11
**Threats Open:** 0

## Threat Verification

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-66-01 | Tampering | mitigate | CLOSED | PassFailEngineFacts.cs:43,54,69,83,99,117 — all six decision branches (Complete/Missing/Duplicate/Reconcile-fail/Reconcile-pass/DormantCounter) exercised with hermetic synthetic inputs; engine is a plain object with no live-stack dependency |
| T-66-02 | Info Disclosure | accept | CLOSED | See Accepted Risks below |
| T-66-03 | DoS (self) | mitigate | CLOSED | RunTrace.FromLabels (RunTrace.cs:54) operates solely on an IReadOnlyList<string>; no field-presence assumption. PassFailEngine.cs operates on DistinctLabels (HashSet) and delta doubles; no unguarded member access |
| T-66-04 | DoS (false RED) | mitigate | CLOSED | ElasticsearchTestClient.cs:145 — `if (!resp.IsSuccessStatusCode) return results;`; :147-149 — TryGetProperty("hits") + TryGetProperty("hits") + ValueKind==Array guard; ElasticsearchTestClientFacts.cs:145 — SearchAllHits_EmptyHits_YieldsZeroGroups fact asserts zero groups, no exception |
| T-66-05 | Tampering | mitigate | CLOSED | AnalyzerE2ETests.cs:145-158 — BuildStepSearchBody is a static raw-string template; only EsIndexNames consts (StepLabelFieldPath, WindowTimestampFieldPath) and ISO-8601 window timestamps are interpolated; EsIndexNames.cs:87,100 — field paths are direct constants, no caller-controlled concat |
| T-66-06 | Tampering | mitigate | CLOSED | EsIndexNames.cs:87 — `StepLabelFieldPath = "attributes.StepLabel"` (no .keyword); :100 — `SumFieldPath = "attributes.Sum"` (no .keyword); grep for `const string.*\.keyword` returns zero matches in EsIndexNames.cs; .keyword appears only in doc-comment warnings |
| T-66-07 | Tampering | mitigate | CLOSED | AnalyzerE2ETests.cs:70 — `ScenarioIdPattern = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled)`; :83-85 — `ScenarioIdPattern.IsMatch(scenarioId)` asserted TRUE before any path operation; :87-89 — reportPath composed under fixed `analyzer-reports` dir only after passing validation |
| T-66-08 | Tampering | mitigate | CLOSED | AnalyzerE2ETests.cs:145-158 — ES body is a static `$$"""..."""` raw-string template, only validated window timestamps interpolated; PromQL routed through PrometheusTestClient.cs:214 — `Uri.EscapeDataString(promql)` applied to all PromQL; no hand-rolled concat over untrusted input |
| T-66-09 | DoS (false RED) | mitigate | CLOSED | AnalyzerE2ETests.cs:197-202 — `TryGetProperty("_source")`, `TryGetProperty("attributes")`, `TryGetProperty("CorrelationId")`, `TryGetProperty("StepLabel")` with `continue` on miss; :232-233 — `TryGetInt32` then `GetString`+parse for Sum; SearchAllHits returns [] on non-success (ElasticsearchTestClient.cs:145); PrometheusTestClient.cs:216,224 — `EnsureSuccessStatusCode` + `Assert.Fail` on non-success Prom response (loud fail, not false pass) |
| T-66-10 | Info Disclosure | accept | CLOSED | See Accepted Risks below |
| T-66-11 | Repudiation | mitigate | CLOSED | AnalyzerE2ETests.cs:130 — `await File.WriteAllTextAsync(reportPath, json, ct)` on line 130; Assert.True(report.Verdict == Verdict.Pass ...) on line 134 — write precedes assert; same `report` object serialized and asserted, artifact and exit code always agree |

## Accepted Risks

### T-66-02 — Information Disclosure: AnalyzerReport record
- **Data carried:** correlationIds (random GUIDs) + per-step integer Sums (operational telemetry)
- **PII/secrets present:** None. CorrelationIds are system-generated identifiers; Sums are computed numeric values from the fan-out workflow. No user data, credentials, or secret values are written.
- **Storage:** Local read-only test artifact written under `AppContext.BaseDirectory/analyzer-reports/`; not transmitted over the network; not persisted to any shared store.
- **Path-traversal guard:** Present — scenarioId validated against `^[A-Za-z0-9_-]+$` before path composition (T-66-07).
- **Risk accepted:** Low-value local test target. Accepted as documented in Plan 03 threat register.

### T-66-10 — Information Disclosure: JSON report artifact
- **Data carried:** correlationIds + Sums + Prometheus counter delta values (operational telemetry)
- **PII/secrets present:** None. All values are system telemetry (dispatched counts, step completion counts, windowed Prom deltas). No user data, credentials, or secret values.
- **Storage:** Fixed local test output directory; not transmitted; the same path-traversal guard from T-66-07 applies.
- **Risk accepted:** Low-value local target. Accepted as documented in Plan 03 threat register.

## Unregistered Threat Flags

None. All threat flags from SUMMARY.md `## Threat Surface` sections map directly to registered threat IDs (T-66-04, T-66-06, T-66-07, T-66-08, T-66-09, T-66-11).

## Review Findings (Non-Security, From 66-REVIEW.md)

The code reviewer identified four warnings (WR-01 through WR-04) and five info items (IN-01 through IN-05). These are correctness/robustness concerns in the RealStack fixture, not security gaps:

- WR-01: Float exact-equality in reconciliation (`DispatchSentDelta == triggerCount`) — fragile under future non-integer counters. Not a security issue; fails closed.
- WR-02: Poll-to-stable accepts transient empty ES result as "stable" — may produce spurious false RED. Not a security issue.
- WR-03: `DrainMs` constant reused for two semantically different timeouts. Maintenance trap only.
- WR-04: `triggerCount == 0` produces vacuous Pass. Not a security issue; precondition failure, not adversarial.

These are open as correctness issues for Phase 67/68, not security threats. No security threats were identified by the reviewer.

---
*Audit completed: 2026-06-14*
*Scope: test-harness-only phase (no production source); all implementation files are READ-ONLY*
