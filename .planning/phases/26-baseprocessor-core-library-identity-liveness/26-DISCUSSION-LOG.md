# Phase 26: BaseProcessor.Core — Library, Identity & Liveness - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-01
**Phase:** 26-baseprocessor-core-library-identity-liveness
**Areas discussed:** Startup orchestration & health gate, Retry posture, Shared identity/context state, Heartbeat resilience, Verification strategy, Abstract seam surface
**Format:** Prose confirm-loop (per user preference — no menu prompts). Six gray areas presented with recommendations; user confirmed all six in one pass.

---

## Startup orchestration & health-gate semantics

| Option | Description | Selected |
|--------|-------------|----------|
| Single `BackgroundService`, gate `/startup` on Healthy | Two-loop startup in one hosted orchestrator; re-point `MarkReady` from bare host-start to "identity + definitions resolved", mirroring `Orchestrator/Program.cs` | ✓ |
| Bare host-start readiness (leave base gate as-is) | `/startup` flips at host start regardless of identity resolution | |

**User's choice:** Confirmed recommendation — gate `/startup` (and `/ready`) on Healthy; mirror the Orchestrator `StartupCompletionService`-removal pattern.
**Notes:** `/live` stays dependency-independent (untouched). Until Healthy, no liveness key is written. → CONTEXT D-01, D-02.

---

## Retry posture for identity / schema resolution loops

| Option | Description | Selected |
|--------|-------------|----------|
| Unbounded retry + bounded exponential backoff, configurable | Retry on timeout AND typed not-found until resolved; cap backoff (~30s); short per-request timeout (~5–10s); knobs in appsettings | ✓ |
| Bounded retry then fail/crash | Give up after N attempts | |

**User's choice:** Confirmed — unbounded + capped backoff; retry on both timeout and not-found; backoff cap + per-request timeout appsettings-configurable.
**Notes:** Boot-before-register is the driving case — the operator may not have registered the Processor DB row yet; the processor must keep trying. → CONTEXT D-04.

---

## Shared identity / context state

| Option | Description | Selected |
|--------|-------------|----------|
| Single mutable singleton `IProcessorContext` holder | Holds resolved Id + 3 schema Ids + input/output definitions + Healthy flag; read by heartbeat worker now and Phase 27 consumer later | ✓ |
| Thread results through constructors | No shared holder; pass resolution outputs down call chains | |

**User's choice:** Confirmed — one `IProcessorContext` singleton as source of truth.
**Notes:** This is the seam between Phase 26 (heartbeat) and Phase 27 (`queue:{processorId:D}` consumer + validation). → CONTEXT D-06.

---

## Heartbeat worker resilience

| Option | Description | Selected |
|--------|-------------|----------|
| Log-and-continue on Redis fault | A failed beat is logged; the worker keeps beating; missed beats slide the key to stale (orchestrator sees absent — correct) | ✓ |
| Crash host on heartbeat write fault | Treat a Redis write failure as fatal | |

**User's choice:** Confirmed — log-and-continue, never crash the host on a heartbeat Redis fault (Redis is soft-dep).
**Notes:** Writes only when the context Healthy flag is set; blind whole-value SET, sliding TTL, lock-free. → CONTEXT D-07, D-08, D-10, D-11.

---

## Verification strategy (standalone, this phase)

| Option | Description | Selected |
|--------|-------------|----------|
| Reflection-behind-a-seam + in-memory bus harness | `ISourceHashProvider` seam (default = reflection over AssemblyMetadata) stubbed in tests; MassTransit in-memory harness drives Loop A/B; assert exact L2 JSON round-trips through `ProcessorLivenessValidator` | ✓ |
| Defer all proof to Phase 28 (real stack) | Wait for the SourceHash embed target + Processor.Sample before testing | |

**User's choice:** Confirmed — reflection-behind-a-seam + in-memory harness; no real broker / SourceHash embed target needed this phase (mirrors Phase 18's standalone validation).
**Notes:** SourceHash embed target + concrete Sample are Phase 28; this phase reads whatever assembly metadata provides, behind a stubbable seam. → CONTEXT D-13.

---

## Abstract seam surface — now or Phase 27?

| Option | Description | Selected |
|--------|-------------|----------|
| Declare seam now, invoke in Phase 27 | Declare the abstract base class + `abstract ProcessAsync(...)` signature + `ProcessResult` now (stable class shape + test double); Phase 27 wires the consumer that calls it | ✓ |
| Defer the abstract method entirely to Phase 27 | No abstract surface until the consumer exists | |

**User's choice:** Confirmed — declare the seam now, invoke in 27. BPC-02 (subclass + one abstract method) is satisfied structurally this phase.
**Notes:** → CONTEXT D-12.

---

## Claude's Discretion

- Exact namespaces/class names/file layout under `src/BaseProcessor.Core/`.
- Exact `IProcessorContext` member shape and Healthy-signal mechanism (flag vs enum vs TCS).
- One hosted service vs two (orchestrator + heartbeat); appsettings key names/defaults for retry + `Interval`/`Ttl`.
- Whether `AddBaseProcessor` composes `AddBaseConsole`/`AddBaseConsoleMessaging` internally or expects the concrete `Program.cs` to call them.

## Deferred Ideas

- Execution round-trip (consumer + `ProcessAsync` invocation + L2 data write + `ExecutionResult`) — Phase 27.
- SourceHash MSBuild embed target — Phase 28.
- Concrete `Processor.Sample` + Dockerfile/compose + real-stack E2E close gate — Phase 28.
- Config re-validation, execution-data cleanup-on-read, on-wire step-to-step data, real transform logic — out of scope (milestone).
