# Phase 27: Execution Round-Trip - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-01
**Phase:** 27-execution-round-trip
**Areas discussed:** Queue-bind sequencing, Schema validation reuse, ProcessResult shape, Id minting & chaining

---

## Gray-area selection

| Option | Description | Selected |
|--------|-------------|----------|
| Queue-bind sequencing | EXEC-01 dynamic ConnectReceiveEndpoint vs MarkHealthy vs first liveness write ordering | ✓ |
| Schema validation reuse | EXEC-03/05 Json.Schema + SSRF-locked options across the BaseApi.Service firewall; "empty definition" semantics | ✓ |
| ProcessResult shape | EXEC-04/05/08 output-string-only vs outcome-carrying; outcome ownership | ✓ |
| Id minting & chaining | EXEC-05/06/10 minted output entryId → ExecutionResult.EntryId chain; NewId vs Guid | ✓ |

**User's choice:** All four areas selected.

---

## Queue-bind sequencing (EXEC-01)

| Option | Description | Selected |
|--------|-------------|----------|
| Bind-then-MarkHealthy | Startup orchestrator `ConnectReceiveEndpoint("queue:{Id:D}")` (durable, Immediate(3)) → `await Ready` → `MarkHealthy()`; heartbeat's IsHealthy gate makes the first L2 "Healthy" write necessarily follow the bind | ✓ |
| Static endpoint / other ordering | Rejected — queue name unknown until Loop A resolves identity; can't be a static startup endpoint | |

**User's choice:** Bind-then-MarkHealthy (D-01..D-04).
**Notes:** Consumer registered WITHOUT a static auto-bound endpoint (gotcha flagged for planner). Restart safety falls out — not-Healthy → no bind → dispatches sit in the durable queue.

---

## Schema validation reuse + "empty" semantics (EXEC-02/03/05)

| Option | Description | Selected |
|--------|-------------|----------|
| Port minimal SSRF-locked validator into BaseProcessor.Core | Add Json.Schema pkg; mirror JsonSchemaConfig.DefaultOptions + parse-guard + error flattening; keeps processor↔WebApi firewall intact | ✓ |
| Extract shared Schema.Validation lib | Rejected for now — new shared project + migration churn this milestone avoids; noted as deferred refactor | |

**User's choice:** Port (D-05). "Empty definition" = null OR whitespace-only → skip (D-06). Input read from `L2[data(entryId)]`, existence-checked; Payload is config never input (D-07).
**Notes:** Unparseable non-empty definition → Failed via parse-guard, not a crash.

---

## ProcessResult shape + outcome ownership (EXEC-04/05/06/08)

| Option | Description | Selected |
|--------|-------------|----------|
| Output-string only; framework owns ALL outcomes | `record ProcessResult(string OutputData)`; Completed/Failed/Cancelled all framework-determined (output-validation, caught exception, token, empty-list ack) | ✓ |
| Outcome-carrying ProcessResult | Rejected — concrete could emit business-Failed/Cancelled; breaks minimal seam (BPC-02) | |

**User's choice:** Output-string only (D-08/D-09/D-10).
**Sub-fork — per-item business-failure within a batch:** User confirmed **DEFER** (POC scope; a throwing ProcessAsync fails the whole dispatch as one Failed).

---

## Id minting & round-trip chaining (EXEC-05/06/10)

| Option | Description | Selected |
|--------|-------------|----------|
| Minted output entryId → ExecutionResult.EntryId | WorkflowId/StepId/ProcessorId inherited; CorrelationId copied from dispatch body; ExecutionId minted per-result (NewId.NextGuid); EntryId = new output L2 key → next step reads it as input (chain through L2) | ✓ |

**User's choice:** Confirmed the chain linkage + NewId.NextGuid (D-11/D-12).
**Sub-fork — Failed/Cancelled EntryId:** User confirmed **Guid.Empty** (no output minted; ExecutionId still minted). Echoing inbound input entryId on failure NOT done (D-13).

---

## CONFIG-02 (folded in)

Execution-data L2 TTL = a distinct configurable seconds knob, separate from the liveness Ttl, in the `Processor` options section. Exact key/class = Claude's discretion (D-17). User confirmed.

## Claude's Discretion

- File/class layout for the dispatch consumer, ported validator, result-builder/sender.
- Consumer registration mechanism to suppress static auto-binding.
- Business-vs-infra classification of an L2 output-write fault (mirror WorkflowLifecycle.IsBusiness).
- CONFIG-02 TTL key name / default / options class.
- Test strategy (in-memory harness + real/fake Redis).

## Deferred Ideas

- Per-item business-failure within a batch.
- Shared Schema.Validation library extraction.
- Echoing inbound input entryId onto Failed/Cancelled results.
- SourceHash embed, Processor.Sample, Dockerfile/compose, real-stack E2E + close gate (Phase 28).
- Config re-validation, cleanup-on-read, on-wire output forwarding, real transform logic (out of scope this milestone).
