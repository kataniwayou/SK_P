---
status: passed
phase: 30-runtime-business-metrics
source: [30-VERIFICATION.md]
started: "2026-06-03"
updated: "2026-06-03"
---

## Current Test

[all tests passed — live compose stack run 2026-06-03]

## Tests

### 1. Live metrics round-trip against the full compose stack (METRIC-06 live proof)
expected: With the full docker compose stack up and healthy (prometheus + orchestrator + processor-sample built from current code), running `dotnet test tests/BaseApi.Tests -- --filter-class "*MetricsRoundTripE2ETests"` passes — a live round-trip drives all four business counters, then the Prometheus HTTP API (`localhost:9090`) confirms: the four series exist for the exercised `ProcessorId`; the by-`ProcessorId` bottleneck PromQL (`sum by (ProcessorId)`) evaluates numerically; a `process_runtime_dotnet_*` runtime metric carries a non-empty `service_instance_id`; the business counters carry `ProcessorId` + `service_instance_id` and NO `workflowId`; `processor_result_sent_total` carries `outcome` ∈ {completed, failed, cancelled}.
result: passed — `dotnet test tests/BaseApi.Tests -- --filter-class "*MetricsRoundTripE2ETests"` = Passed 1 / Failed 0 (2m47s) against the full live compose stack (orchestrator + baseapi-service + processor-sample rebuilt to current Phase 30 code; prometheus :9090 healthy). All in-test assertions held: four business series present for the exercised ProcessorId, by-ProcessorId bottleneck PromQL evaluated numerically, a process_runtime_dotnet_* metric carried a non-empty service_instance_id, business counters carried ProcessorId + service_instance_id and NO workflowId, processor_result_sent_total carried outcome ∈ {completed,failed,cancelled}, collector config unchanged.

## Summary

total: 1
passed: 1
issues: 0
pending: 0
skipped: 0
blocked: 0

## Gaps
