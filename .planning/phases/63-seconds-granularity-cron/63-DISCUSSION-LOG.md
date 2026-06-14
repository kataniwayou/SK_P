# Phase 63: Seconds-Granularity Cron - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-14
**Phase:** 63-seconds-granularity-cron
**Areas discussed:** Format detection, Single source of truth, Granularity floor, Test & message scope

---

## Area Selection

| Option | Description | Selected |
|--------|-------------|----------|
| Format detection | 5-field vs 6-field parsing strategy; must be consistent across CronInterval + validator | ✓ |
| Single source of truth | Shared resolver placement given cross-assembly split + anti-desync culture | ✓ |
| Granularity floor | Accept any 6-field vs impose a minimum interval | ✓ |
| Test & message scope | Confirm test additions + error-message update | ✓ |

**User's choice:** All four areas.

---

## Format Detection

| Option | Description | Selected |
|--------|-------------|----------|
| Token-count switch | Split on whitespace; 6→IncludeSeconds, 5→Standard, else invalid. Deterministic, no exception-as-control-flow. | ✓ |
| Try-6-then-5 fallback | Parse IncludeSeconds, catch CronFormatException, retry Standard. Exception-as-control-flow; can mask malformed input. | |
| Try-5-then-6 fallback | Parse Standard first, fall back to IncludeSeconds. Same tradeoff, legacy-biased. | |

**User's choice:** Token-count switch.
**Notes:** Cronos has no auto-detect, so the format must be selected explicitly. Token count chosen for determinism and to avoid exceptions-as-control-flow.

---

## Single Source of Truth

> Decisive finding presented before the question: the Orchestrator has a hard firewall (D-08) forbidding any reference to `BaseApi.*`, so `Messaging.Contracts` is the *only* assembly both call sites can share, and it has no Cronos dependency today.

| Option | Description | Selected |
|--------|-------------|----------|
| Pure detector in Contracts | `IsSecondsForm(expr)` token-count helper in Messaging.Contracts, NO Cronos; both sites pick CronFormat then Parse locally. | ✓ |
| Full resolver in Contracts | Add Cronos to Messaging.Contracts + a detect-and-parse resolver. Strongest guarantee; pulls parser into the contracts leaf. | |
| Parallel impls + guard test | Inline token-count in both sites; a cross-assembly fact test asserts identical accept/reject. Relies on test, not structure. | |

**User's choice:** Pure detector in Contracts.
**Notes:** Centralizes only the format-selection rule (the desync-prone part) while keeping the frozen-vocabulary contracts leaf free of a third-party parser. The local `Cronos.Parse` stays at each call site.

---

## Granularity Floor

| Option | Description | Selected |
|--------|-------------|----------|
| No floor | Accept any valid 6-field cron incl. every-1s. Matches scope ('enable 6-field, add no policy'). | ✓ |
| Add a minimum floor | Reject sub-N-second crons. Adds product policy beyond scope; needs threshold + rule + tests. | |

**User's choice:** No floor.
**Notes:** Workflow under test uses `*/30`. A floor would be a new scheduling-policy capability, out of v8.0.0 scope.

---

## Test & Message Scope (multi-select)

| Option | Description | Selected |
|--------|-------------|----------|
| Extend CronIntervalTests | Add `*/30 * * * * *` case (IntervalSeconds==30 + strictly-future UTC). | ✓ |
| Add validator unit tests | None exist today; add Create+Update: 5-field OK, 6-field OK, malformed rejected. | ✓ |
| Detector unit test | Unit-test the new pure Messaging.Contracts detector. | ✓ |
| Update error message | '5-field cron' → '5- or 6-field cron' in both validators. | ✓ |

**User's choice:** All four.
**Notes:** The validator cron rule has no dedicated unit test today — this phase closes that gap.

---

## Claude's Discretion

- Detector name/namespace/signature within Messaging.Contracts.
- Refactor of the duplicated `BeValidStandardCron` (Create vs Update) to route through the shared detector.
- Test data tables, naming, unit-vs-integration placement of new validator tests.
- Whitespace edge-case handling in the detector.

## Deferred Ideas

- Minimum-interval floor / sub-second rate guard — considered and declined (D-06); would be its own scoped policy change if ever needed.
