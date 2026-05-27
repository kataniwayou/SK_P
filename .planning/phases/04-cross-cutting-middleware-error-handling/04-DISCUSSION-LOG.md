# Phase 4: Cross-Cutting Middleware + Error Handling - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-27
**Phase:** 04-cross-cutting-middleware-error-handling
**Areas discussed:** Pipeline order + correlation generation, IExceptionHandler shape + ProblemDetails customization, SQLSTATE + constraint-name extraction, Cross-phase scope (FluentValidation + [ApiController] + NotFoundException), Verification structure

---

## Area Selection (Round 0)

| Gray Area | Selected |
|-----------|----------|
| Pipeline order + correlation generation | ✓ |
| IExceptionHandler shape + ProblemDetails customization | ✓ |
| SQLSTATE + constraint-name extraction | ✓ |
| Cross-phase scope: FluentValidation, [ApiController], NotFoundException | ✓ |

All 4 selected (multiselect).

---

## Verification Structure (single-select, batched with Area Selection)

| Option | Description | Selected |
|--------|-------------|----------|
| Build + verify split, real Postgres (Recommended — match Phase 3) | Plan 04-01 wires middleware/handlers (autonomous); Plan 04-02 verification (autonomous:false checkpoint) with WebApplicationFactory + per-class throwaway Postgres DBs | ✓ |
| Single plan, all-in-one | One autonomous plan ships everything | |
| Three plans (Core / Service / verify split) | More granular but heavier orchestration | |
| You decide | Defer to planner | |

**User's choice:** Build + verify split (Phase 3 pattern)

---

## Round 1 — Top-of-batch decisions

### Pipeline order: `UseExceptionHandler` vs `UseCorrelationId` first

| Option | Description | Selected |
|--------|-------------|----------|
| UseExceptionHandler FIRST, UseCorrelationId SECOND (Recommended) | .NET standard. ExceptionHandler wraps try/catch around everything below; CorrelationId middleware runs inside the wrapper. | ✓ |
| UseCorrelationId FIRST, UseExceptionHandler SECOND | Inverse; less common. | |
| Both before UseRouting; order between them irrelevant | Defer to planner | |

**User's choice:** UseExceptionHandler FIRST

---

### ProblemDetails customization mechanism

| Option | Description | Selected |
|--------|-------------|----------|
| services.AddProblemDetails(opts => opts.CustomizeProblemDetails = ctx => …) (Recommended) | .NET 8 modern pattern. One callback injects correlationId + instance into every Problem body. | ✓ |
| Custom IProblemDetailsService implementation | Replace framework service entirely. Heavier. | |
| Set correlationId in each IExceptionHandler manually | Error-prone; misses framework-emitted ProblemDetails. | |

**User's choice:** AddProblemDetails callback

---

### SQLSTATE detection + constraint-name parsing location

| Option | Description | Selected |
|--------|-------------|----------|
| Static PostgresExceptionMapper helper in BaseApi.Core/Persistence/Exceptions/ (Recommended) | Centralized TryMap helper; unit-testable in isolation; thin DbUpdateExceptionHandler shell | ✓ |
| Inline in DbUpdateExceptionHandler.TryHandleAsync | All logic in handler; fewer files but harder to test | |
| Two separate handlers (one per SQLSTATE) | Splits FK and Unique handlers; chain-order dependency | |

**User's choice:** Static PostgresExceptionMapper helper

---

### Cross-phase: FluentValidation + [ApiController] — ship or defer

| Option | Description | Selected |
|--------|-------------|----------|
| Ship ValidationException handler + [ApiController] config now (Recommended) | Phase 4 closes ERROR-03 + ERROR-10 defensively; Phase 6 only adds .AddFluentValidation() | ✓ |
| Defer both to Phase 6/7 | Smaller Phase 4 but leaves requirements visibly unfinished | |
| Ship ValidationException now, defer [ApiController] to Phase 7 | Hybrid; closes ERROR-03 only | |

**User's choice:** Ship both now

---

## Round 2 — Implementation detail decisions

### Correlation ID string format

**Initial question:** `Guid.NewGuid().ToString("N")` vs `"D"` vs `Activity.RootId`

| Option | Description | Selected |
|--------|-------------|----------|
| Guid.NewGuid().ToString("N") — 32-char hex no dashes (Recommended) | Compact, visually distinct from entity Ids (which always render as "D") | ✓ |
| Guid.NewGuid().ToString("D") — 36-char with dashes | Visual consistency with entity Ids everywhere | |
| Activity.Current?.RootId or W3C traceparent | OTel trace ID; defers to Phase 5 wiring | |

**User clarification before answering:** Asked whether `"D"` format would cause editing needed when copy-pasting entity Ids between webapi GET responses and Elasticsearch. Claude clarified that entity Id serialization is fixed at `"D"` format by System.Text.Json regardless of the correlation ID format choice, and that this question only governs the correlation header string — not entity Ids. After the clarification:

**User's choice:** `Guid.NewGuid().ToString("N")` (Option 1)

---

### IExceptionHandler chain shape

| Option | Description | Selected |
|--------|-------------|----------|
| Multiple handlers via TryHandleAsync chain (Recommended) | NotFound / Validation / DbUpdate / Fallback handlers, each < 50 lines, independently testable | ✓ |
| Single GlobalExceptionHandler with type-switch | Fewer files but grows linearly; harder to unit-test branches | |

**User's choice:** Multiple handlers

---

### DbUpdateConcurrencyException placement (D-03a from Phase 3)

| Option | Description | Selected |
|--------|-------------|----------|
| Subsume under DbUpdateExceptionHandler with early-return (Recommended) | Single handler catches both via inheritance; one test class; xmin not exposed | ✓ |
| Separate ConcurrencyExceptionHandler registered before DbUpdate | Per-class responsibility but chain-order dependency becomes load-bearing | |

**User's choice:** Subsume under DbUpdateExceptionHandler

---

### NotFoundException constructor shape

| Option | Description | Selected |
|--------|-------------|----------|
| NotFoundException(string resourceType, object id) — message auto-composed (Recommended) | Stores both as properties; handler surfaces both as ProblemDetails.Extensions | ✓ |
| NotFoundException(string message) — caller composes | Simpler but inconsistent messages and no extensions | |
| NotFoundException<TEntity>(object id) — generic | Type-safe but generic-noise at call site | |

**User's choice:** `NotFoundException(string resourceType, object id)`

---

## Round 3 — Wrap up

| Option | Selected |
|--------|----------|
| I'm ready for context (Recommended) | ✓ |
| Explore test infrastructure shape | |
| Explore logging/observability handoff to Phase 5 | |
| Explore something else | |

**User's choice:** Ready for context. Proceed to CONTEXT.md write + plan-phase.

---

## Claude's Discretion (deferred to research/planning)

- BeginScope dictionary vs KeyValuePair shape (recommend Dictionary, verify against .NET 8 OTel-scope conventions in Phase 5)
- Test endpoint registration mechanism inside WebApplicationFactory<Program> (test controller via assembly part vs. Minimal API stubs)
- Exact regex pattern for ERROR-11 constraint-name parsing (starting patterns in D-08; verify against Phase 8 constraint conventions)
- Stack-trace logging level (default LogError; planner may downgrade specific expected exception types)

## Deferred Ideas

None — discussion stayed within Phase 4 scope. Items mentioned that are out-of-scope-but-have-their-own-phase: OTel wiring (Phase 5), health probes (Phase 5), AddFluentValidation (Phase 6), AddBaseApi/UseBaseApi extensions (Phase 7), concrete controllers/AppDbContext/migrations (Phase 7/8).
