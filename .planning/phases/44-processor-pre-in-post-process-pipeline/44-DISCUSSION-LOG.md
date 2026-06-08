# Phase 44: Processor Pre/In/Post-Process Pipeline - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-08
**Phase:** 44-processor-pre-in-post-process-pipeline
**Areas discussed:** In-Process seam shape, Status exception design, Old-seam migration, Retry loop mechanism

---

## In-Process seam shape

### Seam parameter types
| Option | Description | Selected |
|--------|-------------|----------|
| Both raw string (JSON) | validatedData/payload as `string`; author deserializes. Matches current seam + design doc. | ✓ |
| Pre-parsed JsonElement | Framework parses once; author navigates STJ. | |
| Generic typed `<TInput,TConfig>` | Framework deserializes to author types. | |

**User's choice:** Both raw string (JSON).

### Return-item type
| Option | Description | Selected |
|--------|-------------|----------|
| New record + outcome enum | `ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId)`, author-constructed. | ✓ |
| Record + factory helpers | Same record via `ProcessItem.Completed(data)` auto-minting executionId. | |
| Repurpose ProcessResult | Extend the existing output-only record. | |

**User's choice:** New record + outcome enum (author mints executionId directly).

### Method name
| Option | Description | Selected |
|--------|-------------|----------|
| Keep ProcessAsync | Reuse name, new signature. | ✓ |
| InProcessAsync | Name the pipeline stage. | |
| TransformAsync | Describe the author action. | |

**User's choice:** Keep `ProcessAsync`.

---

## Status exception design

### Exception structure
| Option | Description | Selected |
|--------|-------------|----------|
| Abstract base + 3 subclasses | `ProcessStatusException(status)` + Processing/Failed/CancelledException; single catch for the family. | ✓ |
| Three independent types | No shared base; three catch arms. | |
| One exception + status enum | Single type, status as ctor arg. | |

**User's choice:** Abstract base + 3 subclasses.

### Exception message
| Option | Description | Selected |
|--------|-------------|----------|
| Yes — all carry a message | Each status exception takes a message → matching result field. | ✓ |
| Only failed/cancelled | Processing carries none (no wire field). | |
| You decide from the records | Map per record field availability. | |

**User's choice:** All carry an author message (maps to the matching `Step*` record field where one exists).

---

## Old-seam migration

### Old seam handling
| Option | Description | Selected |
|--------|-------------|----------|
| Clean break | Delete old ProcessAsync + ProcessResult; no shim. | ✓ |
| Compat adapter | Keep old seam via adapter over the new pipeline. | |

**User's choice:** Clean break.

### Processor.Sample
| Option | Description | Selected |
|--------|-------------|----------|
| Migrate it in-phase | Update to new seam; worked example. | ✓ |
| Minimal stub only | Smallest passthrough; defer rich example. | |

**User's choice:** Migrate in-phase as the worked example.

---

## Retry loop mechanism

### Retry implementation
| Option | Description | Selected |
|--------|-------------|----------|
| Shared retry helper | One `RetryLoop.ExecuteAsync` wrapping all L2 ops + sends, surfacing exhaustion for terminal routing. | ✓ |
| Inline loops per site | Hand-rolled at each call site. | |

**User's choice:** Shared retry helper.

### _DLQ1 reach on send-exhaustion
| Option | Description | Selected |
|--------|-------------|----------|
| Throw → bus error queue (defer _DLQ1) | Propagate so MassTransit dead-letters to existing `_error`; _DLQ1 is Phase 47. | ✓ |
| Target _DLQ1 now | Wire consolidated _DLQ1 this phase. | |

**User's choice:** Throw → bus error queue; defer `_DLQ1` consolidation to Phase 47.

---

## Claude's Discretion

- Pipeline code decomposition (consumer-inline vs dedicated pipeline-runner class).
- `ProcessorJsonSchemaValidator` reuse for Pre input + Post output validation.
- `RetryLoop` helper namespace/signature and how exhaustion is surfaced.
- Keeper message id-set wiring (follows design doc §Processor round trip verbatim).

## Deferred Ideas

- `_DLQ1` consolidation — Phase 47 (A4).
- Keeper recovery consumer that processes the 5 states — Phase 46.
- Keeper BIT health gate + global pause/resume — Phase 45.
