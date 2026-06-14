---
status: complete
phase: 63-seconds-granularity-cron
source: [63-01-SUMMARY.md, 63-02-SUMMARY.md, 63-03-SUMMARY.md]
started: 2026-06-14
updated: 2026-06-14
---

## Current Test

[testing complete]

## Tests

### 1. Workflow create accepts 6-field seconds cron
expected: `POST /api/v1/workflows` with `CronExpression="*/30 * * * * *"` is accepted (2xx) instead of being rejected 400/422 as "non-5-field". 5-field standard crons still accepted.
result: skipped
reason: "No human verification required (user directive). Behavior hermetically proven by `WorkflowCronValidatorTests` (6-field accept against WorkflowCreateDtoValidator) — 8/8 green. Live API exercise deferred to the milestone clean-state stack (Phase 65)."

### 2. Workflow update accepts 6-field seconds cron
expected: `PUT /api/v1/workflows/{id}` with `CronExpression="*/30 * * * * *"` is accepted (2xx). The Update validator behaves byte-identically to Create.
result: skipped
reason: "No human verification required (user directive). Proven by `WorkflowCronValidatorTests` (6-field accept against WorkflowUpdateDtoValidator, both validators tested per D-09) — green. Live API exercise deferred to Phase 65."

### 3. 5-field standard cron still accepted (regression)
expected: Create/Update with `CronExpression="0 0 * * *"` still returns 2xx — the change is purely additive, no 5-field regression.
result: skipped
reason: "No human verification required (user directive). Covered by `WorkflowCronValidatorTests` 5-field-accept cases and `CronIntervalTests` retained 5-field interval fact (`*/5 * * * *` → 300) — green."

### 4. Invalid / wrong field-count cron rejected with accurate message
expected: Create with a malformed (`"not a cron"`) or wrong-count (`"* * *"`, 4 tokens) expression returns 400/422, and the user-facing message reflects the accepted set ("valid 5- or 6-field cron expression"), not stale "5-field only" text.
result: skipped
reason: "No human verification required (user directive). Covered by `WorkflowCronValidatorTests` malformed/wrong-count reject cases + `.WithMessage` text (D-11), and `CronFieldFormTests` `IsValidFieldCount(\"* * *\")==false` — green. Reject-before-parse (no exception-as-control-flow) per D-02."

### 5. Orchestrator fires a 6-field seconds workflow every ~30s (end-to-end)
expected: A scheduled workflow with `*/30 * * * * *` fires roughly every 30 seconds (≈10 triggers over a 5-minute window) against the live stack.
result: skipped
reason: "Deferred by design — this is the milestone-level live E2E proof (Phases 65–68 fan-out seeder + analyzer), explicitly out of scope for Phase 63 per 63-VALIDATION.md. The underlying sub-minute math is hermetically proven here: `CronIntervalTests` `IntervalSeconds(\"*/30 * * * * *\") == 30` and strictly-future + `Kind=Utc` next-occurrence — green."

## Summary

total: 5
passed: 0
issues: 0
pending: 0
skipped: 5
blocked: 0

## Gaps

[none — no issues reported; all behaviors covered by automated hermetic tests (21/21 green) or scheduled milestone E2E (Phases 65–68)]
