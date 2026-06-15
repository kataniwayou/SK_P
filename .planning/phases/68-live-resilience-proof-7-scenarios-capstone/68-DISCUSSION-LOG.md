# Phase 68: Live Resilience Proof — 7 Scenarios (Capstone) - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-15
**Phase:** 68-live-resilience-proof-7-scenarios-capstone
**Areas discussed:** Fault params (rows 03-07), Sweep runner, Evidence artifact, Flake/re-run policy

---

## Fault params (dwell + injection for TEST-03..07)

| Option | Description | Selected |
|--------|-------------|----------|
| Uniform 45s/N=4 for all | Mirror the proven TEST-02 recipe across rows 03-07 — true "just data," simplest. | ✓ |
| Per-class tuned dwell/N | Tune dwell + injection per fault class (keeper longer for liveness lease; redis/rabbitmq earlier for reconnect/redelivery room). | |
| Discuss each scenario's recipe | Walk through each row's targetContainers/dwell/N together. | |

**User's choice:** Uniform 45s/N=4 for all.
**Notes:** 45s already spans ≥1 full 30s cron fire while the tier is dead (D-08 disruption
guarantee); remaining ~135s window + 120s analyzer drain gives recovery room. Captured a
planner-verify (D-01b): a non-PASS on a stateful tier is a real finding, not a blind dwell-bump.

---

## Sweep runner (drive all 7 + failure handling)

| Option | Description | Selected |
|--------|-------------|----------|
| Thin wrapper script, run-all + collect | New scripts/phase-68 wrapper loops the harness over all 7, runs ALL even on failure, aggregates verdicts. | ✓ |
| Thin wrapper script, fail-fast | Wrapper loops 7 but stops at first non-PASS. | |
| Manual sequential, no new script | Document 7 individual harness invocations in a UAT/runbook; no wrapper. | |

**User's choice:** Thin wrapper script, run-all + collect.
**Notes:** Wrapper exit non-zero if ANY scenario non-PASS; numeric order TEST-01→07
(baseline-first per Phase 67 D-10); re-uses the harness verbatim, no harness machinery change.

---

## Evidence artifact (proof-of-done for "all 7 PASS")

| Option | Description | Selected |
|--------|-------------|----------|
| Roll-up summary + 7 JSONs | Keep the 7 analyzer-reports + emit one capstone summary (7-row PASS/FAIL table). | ✓ |
| 7 individual JSONs only | Rely on the 7 reports + the sweep exit code; no new aggregate. | |

**User's choice:** Roll-up summary + 7 JSONs.
**Notes:** Summary derived from the 7 JSON reports + each harness exit code — adds no new
scoring. Format/path = Claude's discretion (close-script style: JSON + human table).

---

## Flake / re-run policy (acceptance standard for "proven")

| Option | Description | Selected |
|--------|-------------|----------|
| Re-run allowed on infra-abort only | PASS required on a clean run; re-run permitted+documented only on a distinct INFRA exit code (10-70), never on a verdict FAIL (1). | ✓ |
| First clean run must PASS | Strictest — any non-PASS (infra or verdict) is a finding, not a retry. | |
| Bounded auto-retry in the runner | Runner auto-retries N times on infra-abort codes. | |

**User's choice:** Re-run allowed on infra-abort only.
**Notes:** No auto-retry in the runner (operator re-invokes the single failed scenario on an
infra-abort). The wrapper distinguishes infra-abort vs verdict-FAIL in its roll-up. Verdict
FAIL is always a real finding to investigate. Keeps "no human verification of correctness" intact.

## Claude's Discretion

- Roll-up summary file format + path + console rendering.
- Wrapper internals (exit-code capture, infra-abort vs verdict-FAIL tagging, log location).
- Optional single-id-subset arg on the wrapper (default all-7).

## Deferred Ideas

None — per-class dwell tuning and runner auto-retry were both considered and rejected in favor
of the uniform recipe (D-01) and operator-initiated infra-abort re-runs (D-04) respectively.

## Reconciliation flagged (planner-verify, not a decision)

- The harness `--filter-method "*Analyze_HappyPath_Window_Yields_Pass*"` + its IN-04 comment
  ("for a fault scenario a FAIL verdict is the EXPECTED outcome") is stale for this capstone:
  a recovered fault run must assert PASS. Confirm/rename during planning.
