---
phase: 27
slug: execution-round-trip
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-01
---

# Phase 27 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 (mirror BaseProcessor.Core test project) |
| **Config file** | none — existing BaseProcessor.Core.Tests project; Wave 0 adds test files |
| **Quick run command** | `dotnet test tests/BaseProcessor.Core.Tests --filter-class <FullyQualifiedClass>` |
| **Full suite command** | `dotnet test tests/BaseProcessor.Core.Tests` |
| **Estimated runtime** | ~TBD seconds (planner/executor to confirm against existing suite) |

> Driver: MassTransit in-memory `ITestHarness` for the dispatch consumer + a real/fake Redis (mirror the P26 test setup — executor to confirm whether the existing suite uses Testcontainers, a fake `IConnectionMultiplexer`, or a local instance). Runtime `ConnectReceiveEndpoint` bind sequencing may not be exercisable under the in-memory harness — the consumer's `Consume` logic is tested via the harness independently of the bind mechanism; the bind-then-Healthy sequencing is deferred to the Phase 28 real-stack E2E (flag from RESEARCH.md).

---

## Sampling Rate

- **After every task commit:** Run the quick (per-class) command for the touched area
- **After every plan wave:** Run the full suite command
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** TBD seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| _to be filled by planner_ | | | EXEC-01..10 / CONFIG-02 | | | unit / harness | | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

> Per RESEARCH.md "## Validation Architecture" — Wave-0 test files to create (planner finalizes the list against the plan breakdown). Indicative set:

- [ ] Dispatch consumer happy-path (input read + ProcessAsync + output write + one-by-one send + ack) — EXEC-02/04/05/06/07/09
- [ ] Input-missing → single `Failed` before ProcessAsync (non-empty inputDefinition, no L2 data) — EXEC-03
- [ ] Input validation skip on empty definition — EXEC-02/03
- [ ] Output-validation failure → `Failed`, nothing written; mixed batch — EXEC-05
- [ ] Empty result list → ack only, no message — EXEC-08
- [ ] Caught exception → single `Failed` with message; `OperationCanceledException` → `Cancelled` — EXEC-08
- [ ] Id minting + chaining: minted output entryId → `ExecutionResult.EntryId`; per-result `executionId`; `Guid.Empty` on Failed/Cancelled — EXEC-05/06/13
- [ ] Inherited body `CorrelationId` flows onto every `ExecutionResult` — EXEC-10
- [ ] Ported Json.Schema validator (SSRF-locked options + parse-guard + error flattening) — EXEC-03/05
- [ ] CONFIG-02 execution-data TTL applied on output write — CONFIG-02

*Planner replaces this indicative list with concrete `{padded_phase}-{NN}-{NN}` task-mapped file paths.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Durable competing-consumer bind only-once-Healthy + Healthy-after-bind ordering on a real broker | EXEC-01 | Runtime `ConnectReceiveEndpoint` against live RabbitMQ is not exercisable under the in-memory harness | Deferred to Phase 28 real-stack E2E round-trip proof |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < TBDs
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
