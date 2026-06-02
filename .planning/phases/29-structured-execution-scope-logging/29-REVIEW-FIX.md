---
phase: 29-structured-execution-scope-logging
fixed_at: 2026-06-02T00:00:00Z
review_path: .planning/phases/29-structured-execution-scope-logging/29-REVIEW.md
iteration: 1
findings_in_scope: 3
fixed: 1
skipped: 2
status: partial
---

# Phase 29: Code Review Fix Report

**Fixed at:** 2026-06-02T00:00:00Z
**Source review:** .planning/phases/29-structured-execution-scope-logging/29-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 3 (WR-01, WR-02, WR-03)
- Fixed: 1
- Skipped: 2

## Fixed Issues

### WR-01: `RecordingResultConsumer.Received` is a static mutable list — cross-test pollution if tests run in parallel

**Files modified:** `tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs`
**Commit:** 249bfdf
**Applied fix:** Changed `RecordingResultConsumer` from holding a `public static readonly List<ExecutionResult> Received = new()` to using a primary constructor that accepts a `List<ExecutionResult> received` parameter. The test method now creates a local `var received = new List<ExecutionResult>()`, registers the consumer as a singleton via `.AddSingleton(new RecordingResultConsumer(received))`, and all references to `RecordingResultConsumer.Received` in the test body were replaced with the local `received` variable. The previous `Clear()` call at the start of the test was removed (no longer needed — the list starts empty per test instance). Verified GREEN: 1 passed, 0 failed via `dotnet test -- --filter-class "*EntryStepDispatchRuntimeScopeTests"`.

## Skipped Issues

### WR-02: `prometheus` service silently skipped if unhealthy — health-check exception list is incomplete

**File:** `scripts/phase-29-close.ps1:143`
**Reason:** False positive. The close gate already ran to `exit 0` (3x405 GREEN + triple-SHA held) with `prometheus` present in `$services` as a required healthy service. This proves the project's `docker-compose.yml` DOES define a `healthcheck:` for the prometheus service so `docker compose ps` returns `"healthy"`. Adding `prometheus` to the health-exemption list would be actively harmful: it would allow a dead prometheus to pass the gate silently. No change applied.
**Original issue:** The pre-flight health loop exempts only `otel-collector` from the `health -ne 'healthy'` abort guard, and the reviewer flagged that the official Prometheus image ships without a `HEALTHCHECK` instruction — meaning if no custom health-check is defined, prometheus would always report empty health and cause `exit 2` on every run.

### WR-03: `InboundExecutionScopeConsumeFilter` opens scope with `ProcessorId` — potential duplicate with `ProcessorIdLogEnricher`

**File:** `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs:28-36`
**Reason:** Benign — no safe fix exists. The `InboundExecutionScopeConsumeFilter` is bus-wide and runs on BOTH consoles (orchestrator and processor). `ProcessorId` scoping via the filter is required on the orchestrator console, which has no `ProcessorIdLogEnricher`; removing it from the filter would lose coverage on the orchestrator side. On the processor side, the duplicate value is always identical (`IProcessorContext.Id` == the dispatch message's `ProcessorId`), and the live ES document confirmed `attributes.ProcessorId` resolves to a usable scalar GUID in practice. Any proposed fix (console-specific branching, or dropping one of the two sources) adds complexity or loses coverage for a non-issue. The live close gate (3x405 GREEN) confirms no observable Elasticsearch term-query breakage from the current behavior.
**Original issue:** On the processor console, `ProcessorId` would appear twice in the final OTel export — once from the MEL scope flattened via `IncludeScopes`, and once from `ProcessorIdLogEnricher.OnEnd` appending directly to `LogRecord.Attributes` — potentially causing Elasticsearch to see an array rather than a scalar for the `attributes.ProcessorId` field.

---

_Fixed: 2026-06-02T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
