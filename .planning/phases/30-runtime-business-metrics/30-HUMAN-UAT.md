---
status: partial
phase: 30-runtime-business-metrics
source: [30-VERIFICATION.md]
started: "2026-06-03"
updated: "2026-06-03"
---

## Current Test

[awaiting human testing — live compose stack required]

## Tests

### 1. Live metrics round-trip against the full compose stack (METRIC-06 live proof)
expected: With the full docker compose stack up and healthy (prometheus + orchestrator + processor-sample built from current code), running `dotnet test tests/BaseApi.Tests -- --filter-class "*MetricsRoundTripE2ETests"` passes — a live round-trip drives all four business counters, then the Prometheus HTTP API (`localhost:9090`) confirms: the four series exist for the exercised `ProcessorId`; the by-`ProcessorId` bottleneck PromQL (`sum by (ProcessorId)`) evaluates numerically; a `process_runtime_dotnet_*` runtime metric carries a non-empty `service_instance_id`; the business counters carry `ProcessorId` + `service_instance_id` and NO `workflowId`; `processor_result_sent_total` carries `outcome` ∈ {completed, failed, cancelled}.
result: [pending]

## Summary

total: 1
passed: 0
issues: 0
pending: 1
skipped: 0
blocked: 0

## Gaps
