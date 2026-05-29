# Phase 14: Validation gates (DFS + schema-edge + payload-config-schema) - Research

**Researched:** 2026-05-29
**Domain:** .NET 8 / ASP.NET Core domain validation, `IExceptionHandler` chain ordering, JsonSchema.Net (json-everything) draft 2020-12 evaluation, iterative graph traversal
**Confidence:** HIGH (all claims verified against source files in this session; JsonSchema.Net API confirmed against json-everything 9.2.1 docs)

## Summary

Phase 14 fills three pre-wired no-op validator seams (`CycleDetector`, `SchemaEdgeValidator`, `PayloadConfigSchemaValidator`) so a broken workflow Start returns a deterministic HTTP 422 + RFC 7807 at the first failed gate. The orchestrator body (`OrchestrationService.StartAsync` lines 77–84) is already structurally final — Phase 14 fills the three `Validate(snapshot)` call bodies (lines 79–81) and adds ONE new exception type + ONE `IExceptionHandler` that maps it to 422. Every claim in CONTEXT.md about the existing code was verified true with two exceptions flagged below.

The single hardest open mechanism — **D-04 handler ordering** — is now fully resolved by reading the composition root. `AddBaseApiErrorHandling()` runs as call #3 *inside* `AddBaseApi<TDbContext>()`, and `AddAppFeatures()` (which calls `AddOrchestrationFeature()`) runs AFTER it in `Program.cs`. `AddExceptionHandler` walk order == registration order, and `FallbackExceptionHandler` is registered LAST in the Core chain. Therefore appending the orchestration handler inside `AddOrchestrationFeature` registers it AFTER Fallback → Fallback wins → 500. **The working mechanism (Resolution Option (a) from CONTEXT D-04, recommended): Core must expose a registration seam for domain handlers that runs BEFORE the Fallback registration, OR the orchestration handler must be registered before `AddBaseApi` completes.** Concrete recommendation below.

**Primary recommendation:** Add a tiny Core seam — a `static List<Action<IServiceCollection>>` "pre-fallback domain handler" hook invoked inside `AddBaseApiErrorHandling` immediately BEFORE `AddExceptionHandler<FallbackExceptionHandler>()` — and have `AddOrchestrationFeature` register its handler into that hook. This keeps Fallback last-walked, keeps Core orchestration-agnostic, and is the only mechanism that does not require reordering `Program.cs` (which would violate the "Program.cs ≤10 body-lines / no per-concern wiring" invariant). A simpler alternative the planner may prefer: split the Core chain registration so Core exposes `AddBaseApiErrorHandling` WITHOUT Fallback plus a separate `AddBaseApiFallbackHandler()` that `Program.cs`/composition calls LAST — but that touches `Program.cs` and the locked composition order, so the hook seam is preferred. **See "D-04 Resolution" section for the concrete code.**

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Cycle / missing-step detection | API/Backend (Service — `CycleDetector`) | — | Pure in-memory graph traversal over the L1 snapshot; no I/O, no DB |
| Schema-edge equality check | API/Backend (Service — `SchemaEdgeValidator`) | — | Dictionary lookups over snapshot Processors; strict Guid equality |
| Payload↔ConfigSchema validation | API/Backend (Service — `PayloadConfigSchemaValidator`) | — | JsonSchema.Net evaluation in-process; SSRF-locked (no outbound fetch) |
| SSRF-locked schema config | API/Backend (Service — `JsonSchemaConfig`) | — | Shared source-of-truth for `Dialect.Default` + `SchemaRegistry.Global.Fetch` (process-global static) |
| 422 exception → RFC 7807 mapping | API/Backend (Service handler, registered into Core chain) | Core (chain seam) | Domain exception lives in Service (D-11); Core owns the chain + Fallback |
| correlationId + instance injection | API/Backend (Core — Phase 4 `CustomizeProblemDetails`) | — | Already auto-injected on every emission; new handler MUST NOT set these |
| L1 cleanup on failure path | API/Backend (Service — `using` declaration in `StartAsync`) | — | `WorkflowGraphSnapshot.Dispose()` runs on throw via the `using var snapshot` |

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| L1-VALIDATE-01 | Mandatory order existence → cycles → schema-edge → payload; first failure short-circuits 422; L1 cleanup still runs | Order is already structurally locked in `StartAsync` lines 77–82 (existence at 77, cycle 79, schema-edge 80, payload 81). `using var snapshot` (line 78) guarantees Dispose on throw. Each gate THROWS to short-circuit. |
| L1-VALIDATE-02 | Existence gate re-uses v3.2.0 path; ids not in Postgres → 422 | **⚠ CONFLICT (see Assumptions/Open Questions A1):** the existing `ExistenceCheckAsync` throws `NotFoundException` → **404**, NOT 422. Tested by `StartOrchestrationFacts.Start_Returns404_WhenAnyWorkflowIdMissing`. CONTEXT D-12 says "re-uses the v3.2.0 path" (which is 404). The planner MUST resolve: keep 404 (existing locked test) or change to 422 (requirement literal). |
| L1-VALIDATE-03 | Iterative DFS cycle detection, explicit stack, no recursion; offending stepId chain | `CycleDetector.Validate` over `snapshot.Steps[*].NextStepIds` from `Workflow.EntryStepIds[*]`. See "Iterative DFS" section. |
| L1-VALIDATE-04 | Missing next-step gate → 422 with (parentStepId, missingChildId); null NextStepIds terminal | Defense-in-depth per D-08 (`StepNextSteps` FK-Restrict means loader already loaded all referenced steps). Crafted snapshot exercises it. |
| L1-VALIDATE-05 | Schema-edge strict Guid equality over EVERY NextStepIds entry; null-either-side passes | `SchemaEdgeValidator` independent walk; resolve `parent.ProcessorId → Processors[].OutputSchemaId` vs `child.ProcessorId → Processors[].InputSchemaId`. See D-09 mechanics. |
| L1-VALIDATE-06 | Payload↔ConfigSchema via JsonSchema.Net draft 2020-12; null ConfigSchemaId passes | `PayloadConfigSchemaValidator` over `snapshot.Assignments`; resolve `Assignment.StepId → Steps[].ProcessorId → Processors[].ConfigSchemaId → Schemas[].Definition`. See "JsonSchema.Net API". |
| L1-VALIDATE-07 | Shared `JsonSchemaConfig` mirrors Phase 8 SSRF lockdown; must not regress | Extract `Dialect.Default = Dialect.Draft202012` + `SchemaRegistry.Global.Fetch = (_,_) => null` into `JsonSchemaConfig`; refactor `SchemaCreateDtoValidator`/`SchemaUpdateDtoValidator` to consume it. |
| L1-VALIDATE-08 | Per-Start `Dictionary<Guid, JsonSchema>` cache keyed by Schema.Id | Local variable inside `Validate` (validator is Scoped; an instance field would leak across requests). |
| L1-VALIDATE-09 | VALID-21 closes at orchestration-start only; Assignment PUT/POST untouched | No change to Assignment endpoints — verified they only validate "valid JSON". |
| L1-VALIDATE-10 | TEST-03/04 remain deferred; documented | Pure documentation in STATE/SUMMARY. |
| ORCH-START-03 | 422 + RFC 7807 + correlationId + instance + structured `errors` identifying offending ids | New handler sets Status/Title/Detail/`errors`; Phase 4 customizer auto-adds correlationId + instance. |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| JsonSchema.Net | **9.2.1** (pinned in `Directory.Packages.props` line 85) `[VERIFIED: Directory.Packages.props]` | Draft 2020-12 schema parse + evaluate for Payload↔ConfigSchema gate | Already in `BaseApi.Service.csproj` (Phase 8); the only JSON Schema lib in the solution |
| FluentValidation | 12.x (Core csproj) | NOT used by the new gates (gates throw a domain exception, not `ValidationException`) | Existing `ValidationException` → 400; new gate exception is a DISTINCT type → 422 (do not collide) |
| Microsoft.AspNetCore.App (framework ref) | net8.0 | `IExceptionHandler`, `IProblemDetailsService`, `ProblemDetails` | In-box; the existing handler pattern |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| System.Text.Json | in-box | Parse `Assignment.Payload` string → `JsonNode`/`JsonDocument`; parse `Schema.Definition` string | Mirror the existing `JsonDocument.Parse` pattern in `SchemaDtoValidator.cs` |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `JsonSchema.FromText(string)` | `JsonSchema` via `JsonSerializer.Deserialize<JsonSchema>` | `FromText` is the documented one-call parse; Deserialize is equivalent but more verbose. Use `FromText`. |
| `OutputFormat.List` | `OutputFormat.Hierarchical` | List flattens errors to a single-level `Details` collection — exactly what D-03's flat `errors: [string,…]` needs. Use List (mirrors Phase 8). |

**Installation:** No new packages — JsonSchema.Net 9.2.1 already referenced by `BaseApi.Service.csproj` (line 47). `[VERIFIED: BaseApi.Service.csproj]`

**Version verification:** `Directory.Packages.props` line 85: `<PackageVersion Include="JsonSchema.Net" Version="9.2.1" />`. The existing `SchemaDtoValidator.cs` compiles against this version using `Dialect.Default`, `SchemaRegistry.Global.Fetch`, `MetaSchemas.Draft202012.Evaluate(JsonElement, EvaluationOptions)`, `OutputFormat.List`, `EvaluationResults.IsValid` — all confirmed valid 9.2.1 API. `[VERIFIED: source compiles + json-everything docs]`

## D-04 Resolution — IExceptionHandler Chain Ordering (THE #1 mechanism)

### What the code actually shows `[VERIFIED: source]`

1. `BaseApiServiceCollectionExtensions.AddBaseApi<TDbContext>()` (Core) chains 7 calls; **call #3 is `.AddBaseApiErrorHandling()`**.
2. `ErrorHandlingServiceCollectionExtensions.AddBaseApiErrorHandling()` registers, in this exact order (lines 30–33):
   ```
   AddExceptionHandler<NotFoundExceptionHandler>();    // walked 1st
   AddExceptionHandler<ValidationExceptionHandler>();  // walked 2nd
   AddExceptionHandler<DbUpdateExceptionHandler>();    // walked 3rd
   AddExceptionHandler<FallbackExceptionHandler>();    // walked LAST — catch-all, always returns true
   ```
3. `Program.cs` order (lines 7–8): `builder.Services.AddBaseApi<AppDbContext>(...)` FIRST, then `builder.Services.AddAppFeatures()`.
4. `AddAppFeatures()` → `AddOrchestrationFeature()` (the 6th feature call).
5. `app.UseExceptionHandler()` is wired FIRST in `UseBaseApi()` (line 27).

**The constraint, proven:** ASP.NET Core `AddExceptionHandler` registers handlers as a keyed/ordered service collection; `UseExceptionHandler` walks them in REGISTRATION order and the first to return `true` claims the exception. `FallbackExceptionHandler.TryHandleAsync` ALWAYS returns `true` (line 60: `return true; // catch-all: always claimed`). Therefore **any handler registered after Fallback is unreachable.** Because `AddBaseApiErrorHandling` (with Fallback) runs at call #3 inside `AddBaseApi`, and `AddOrchestrationFeature` runs strictly later, **a naive `services.AddExceptionHandler<OrchestrationValidationExceptionHandler>()` inside `AddOrchestrationFeature` is registered AFTER Fallback and will NEVER be reached → the 422 becomes a 500.**

**Empirical confirmation:** `StartCleanupFacts` (Phase 13) forces an `InvalidOperationException` (non-domain) mid-Start and asserts **500** — proving Fallback IS the terminal handler today and DOES claim unrecognized exceptions.

### Recommended mechanism (Resolution Option (a) — a Core pre-Fallback seam)

Core stays orchestration-agnostic but exposes a registration hook that runs BEFORE Fallback:

```csharp
// In BaseApi.Core — ErrorHandlingServiceCollectionExtensions (or a new small seam type)
// Source: derived from existing AddBaseApiErrorHandling structure
internal static class ErrorHandlingServiceCollectionExtensions
{
    // A registration hook collected before Core wires Fallback.
    private static readonly List<Action<IServiceCollection>> _preFallbackHandlers = new();

    // Public seam Service calls to inject a domain handler ahead of Fallback.
    public static IServiceCollection AddPreFallbackExceptionHandler<THandler>(this IServiceCollection services)
        where THandler : class, IExceptionHandler
    {
        services.AddExceptionHandler<THandler>();   // registered NOW — before Fallback if called before AddBaseApiErrorHandling
        return services;
    }
}
```

**Reality check:** the simplest correct realization, given the composition order is LOCKED, is to **register the orchestration handler INSIDE the Core chain itself, immediately before Fallback** — Core cannot reference the Service type, so Core must offer a hook that Service populates BEFORE `AddBaseApi` runs. Since `Program.cs` calls `AddBaseApi` before `AddAppFeatures`, the cleanest concrete options the planner can choose among:

- **(a-1) Static delegate hook (recommended):** Core's `AddBaseApiErrorHandling` invokes a `static List<Action<IServiceCollection>>` of "domain handler registrars" immediately before `AddExceptionHandler<FallbackExceptionHandler>()`. Service registers its registrar into that static list at module-init / via a Core-exposed `RegisterDomainExceptionHandler(...)` API called from `AddOrchestrationFeature` — BUT note `AddOrchestrationFeature` runs AFTER `AddBaseApiErrorHandling`, so the static list must be populated by a path that runs earlier (e.g., a `ModuleInitializer` in Service, or Core calling back into a known extension point). This is fragile.

- **(a-2) Split Fallback out of the Core chain (cleanest, recommended for this codebase):** Change `AddBaseApiErrorHandling` to register NotFound/Validation/DbUpdate but NOT Fallback, and add a separate `AddBaseApiFallbackHandler()` that the composition root calls LAST (after `AddAppFeatures`). Then `AddOrchestrationFeature` appends its 422 handler normally (it lands after DbUpdate, before Fallback). This requires a ONE-LINE change in `Program.cs` (add `builder.Services.AddBaseApiFallbackHandler();` after `AddAppFeatures()`), which is a composition-order edit, not per-concern wiring — acceptable. **This is the most robust mechanism and keeps Fallback provably last-walked.** Recommend the planner pick this unless the Program.cs edit is deemed to violate a hard invariant.

- **(b) Composition-root reorder:** Register the orchestration handler before `AddBaseApi`. Rejected — `AddOrchestrationFeature` depends on types/registrations from `AddBaseApi`, and reordering risks ValidateOnBuild.

**Planner decision required:** pick (a-2) [recommended] or the static-hook variant. Both keep Fallback last-walked and the orchestration handler reachable. `[VERIFIED: composition order from Program.cs + BaseApiServiceCollectionExtensions]`

### Handler shape to mirror `[VERIFIED: NotFoundExceptionHandler.cs]`

```csharp
// Mirror NotFoundExceptionHandler exactly. Source: src/BaseApi.Core/Exceptions/Handlers/NotFoundExceptionHandler.cs
public sealed class OrchestrationValidationExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _pdSvc;
    public OrchestrationValidationExceptionHandler(IProblemDetailsService pdSvc) => _pdSvc = pdSvc;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        if (exception is not OrchestrationValidationException ex) return false;   // Pitfall 6: bail FAST

        httpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title  = ex.Title,            // gate-specific (D-02)
            Detail = ex.Message,
            Extensions = { ["errors"] = ex.ErrorsExtension },   // D-03 { gate, offending }
        };
        // DO NOT set correlationId or instance — Phase 4 CustomizeProblemDetails injects both.
        return await _pdSvc.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext, ProblemDetails = problem, Exception = exception,
        });
    }
}
```

**Critical:** the handler must NOT set `Extensions["correlationId"]` or `ProblemDetails.Instance` — `AddProblemDetails.CustomizeProblemDetails` (lines 18–26 of `ErrorHandlingServiceCollectionExtensions.cs`) does this for EVERY emission. `[VERIFIED: source]`

## JsonSchema.Net (json-everything 9.2.1) API — Payload↔ConfigSchema

### Parse a schema string → `JsonSchema` `[CITED: docs.json-everything.net/api/JsonSchema.Net/JsonSchema]`
```csharp
// FromText is the documented one-call parse. Signature (9.x):
public static JsonSchema FromText(string jsonText, BuildOptions? buildOptions = null,
                                  Uri? baseUri = null, JsonDocumentOptions? jsonOptions = null);
JsonSchema schema = JsonSchema.FromText(schemaDefinitionString);
```

### Evaluate a payload against it `[CITED: docs + VERIFIED against SchemaDtoValidator.cs]`
```csharp
// Evaluate takes a JsonElement (or JsonNode in 9.x overloads) + EvaluationOptions.
// Mirror Phase 8: OutputFormat.List for a flat error list.
public EvaluationResults Evaluate(JsonElement instance, EvaluationOptions options);

using var payloadDoc = JsonDocument.Parse(assignment.Payload);
EvaluationResults results = schema.Evaluate(
    payloadDoc.RootElement,
    JsonSchemaConfig.DefaultOptions);   // { OutputFormat = OutputFormat.List } + SSRF-locked statics
```

### Flatten errors → `List<string>` for D-03 `[CITED: EvaluationResults class docs]`
`EvaluationResults` properties (9.2.1): `IsValid` (bool), `Details` (`List<EvaluationResults>` — flat under List format), `Errors` (`Dictionary<string,string>` keyword→message; **may be null when a node has no errors**), `InstanceLocation` (`JsonPointer`), `EvaluationPath` (`JsonPointer`).

```csharp
if (!results.IsValid)
{
    var errorStrings = results.Details
        .Where(d => d.Errors is { Count: > 0 })
        .SelectMany(d => d.Errors!.Select(kv => $"{d.InstanceLocation}: {kv.Value}"))
        .ToList();
    // Fallback if Details is empty but top-level has Errors:
    if (errorStrings.Count == 0 && results.Errors is { Count: > 0 })
        errorStrings = results.Errors.Select(kv => $"{results.InstanceLocation}: {kv.Value}").ToList();
    throw OrchestrationValidationException.PayloadConfigSchema(assignment.Id, errorStrings);
}
```
**Note:** `Errors` is NULLABLE per node — always null-guard (`is { Count: > 0 }`). With `OutputFormat.List`, error-bearing leaves are flattened into `Details`; the top-level `Errors` is typically empty. Include the top-level fallback for robustness. `[CITED: OutputFormat enum docs — "List format reduces errors to a flat list"]`

### Shared `JsonSchemaConfig` (D-05) `[VERIFIED: SchemaDtoValidator.cs is the lift source]`
```csharp
// New: BaseApi.Service — Features/Schema (or a Service-level Validation folder).
// Single source of SSRF truth. The static ctor sets process-global state ONCE.
public static class JsonSchemaConfig
{
    static JsonSchemaConfig()
    {
        Dialect.Default = Dialect.Draft202012;            // library default is V1, not 2020-12
        SchemaRegistry.Global.Fetch = (_, _) => null;     // SSRF defense-in-depth — no outbound $ref fetch
    }
    public static EvaluationOptions DefaultOptions { get; } =
        new() { OutputFormat = OutputFormat.List };
}
```
**Refactor target:** `SchemaCreateDtoValidator` static ctor (lines 25–31) currently sets both globals inline; replace with a touch of `JsonSchemaConfig` (e.g., `_ = JsonSchemaConfig.DefaultOptions;` to trigger the static ctor, or reference the type so its cctor runs). `SchemaUpdateDtoValidator` relies on the same per-type static state being set on first touch (documented in its summary, lines 73–78). The `PayloadConfigSchemaValidator` consumes `JsonSchemaConfig.DefaultOptions`.

**SSRF non-regression (D-06):** the `<500ms` assertion lives in `tests/BaseApi.Tests/Integration/ErrorMappingFacts.cs::Create_Schema_Invalid_JsonSchema_Returns400_NoOutboundCall` (line 153). It posts `{"type":"not-a-real-type","$ref":"https://attacker.example/schema.json"}` and asserts `sw.ElapsedMilliseconds < 500` (line 203). The refactor MUST keep `SchemaRegistry.Global.Fetch = (_,_) => null` set before any schema validation runs — because `JsonSchemaConfig` is a static class, its cctor runs on first member access; ensure the Schema validators trigger it (they will, since they reference `JsonSchemaConfig.DefaultOptions`). `[VERIFIED: ErrorMappingFacts.cs]`

## Architecture Patterns

### System Architecture Diagram (Start request validation flow)
```
POST /api/v1/orchestration/start  [List<Guid> workflowIds]
        │
        ▼
OrchestrationService.StartAsync
        │
        ├─(1)─► ExistenceCheckAsync ──fail──► NotFoundException → 404  [⚠ A1: req says 422]
        │            (validate ids rules → 400; missing → 404)
        ▼
   using var snapshot = loader.LoadL1Async(...)   ◄── L1 in-memory snapshot (5 dicts)
        │                                              Dispose() runs on ANY throw below
        ├─(3)─► CycleDetector.Validate ──cycle──►   OrchestrationValidationException(cycle)        ─┐
        │                              ──missing─►   OrchestrationValidationException(missingStep)  │
        ▼                                                                                            │
        ├─(4)─► SchemaEdgeValidator.Validate ──►     OrchestrationValidationException(schemaEdge)   │ ─► IExceptionHandler chain
        ▼                                                                                            │      NotFound → Validation → DbUpdate
        ├─(5)─► PayloadConfigSchemaValidator ──►      OrchestrationValidationException(payload...)   │      → [NEW] OrchestrationValidationExceptionHandler → 422 + RFC 7807
        ▼                                                                                            │      → Fallback (500, last-walked)
        └─(6)─► redisProjectionWriter.UpsertAsync (no-op until Phase 15)                            ─┘
        │
        ▼  snapshot.Dispose() (using) — clears 5 dicts, IsDisposed=true, logs "L1 snapshot disposed"
   204 No Content
```
First gate to throw wins (short-circuit); the `using` guarantees L1 cleanup on every path.

### Recommended file layout (Claude's discretion on exact names)
```
src/BaseApi.Service/Features/Orchestration/
├── OrchestrationValidationException.cs          # NEW (D-01/D-11) — one type, gate discriminator + typed offending
├── OrchestrationValidationExceptionHandler.cs   # NEW (D-02/D-11) — 422 handler, mirrors NotFoundExceptionHandler
├── Validation/
│   ├── CycleDetector.cs                          # FILL body (D-07/D-08)
│   ├── SchemaEdgeValidator.cs                     # FILL body (D-09)
│   └── PayloadConfigSchemaValidator.cs           # FILL body (D-10)
└── (JsonSchemaConfig.cs in Features/Schema or a Service-level Validation folder — D-05)
src/BaseApi.Core/Exceptions/Handlers/
└── (Fallback-split seam OR pre-fallback hook — per D-04 resolution)
```

### Pattern: one exception type, gate discriminator (D-01) `[VERIFIED: NotFoundException.cs is the mirror]`
```csharp
public sealed class OrchestrationValidationException : Exception
{
    public string Gate { get; }          // "cycle" | "missingStep" | "schemaEdge" | "payloadConfigSchema"
    public string Title { get; }         // gate-specific RFC 7807 title
    public object Offending { get; }      // D-03 gate-specific record
    public object ErrorsExtension => new { gate = Gate, offending = Offending };

    private OrchestrationValidationException(string gate, string title, string detail, object offending)
        : base(detail) { Gate = gate; Title = title; Offending = offending; }

    public static OrchestrationValidationException Cycle(IReadOnlyList<Guid> stepChain) => /* ... */;
    public static OrchestrationValidationException MissingStep(Guid parent, Guid missingChild) => /* ... */;
    public static OrchestrationValidationException SchemaEdge(Guid parent, Guid child) => /* ... */;
    public static OrchestrationValidationException PayloadConfigSchema(Guid assignmentId, IReadOnlyList<string> errors) => /* ... */;
}
```

### Anti-Patterns to Avoid
- **Recursion in cycle detection** — `StackOverflowException` is uncatchable by `IExceptionHandler` (the whole process dies). Explicit stack/list ONLY. `[CITED: REQUIREMENTS L1-VALIDATE-03]`
- **Per-gate exception subclasses** — D-01 mandates ONE type + gate discriminator (mirrors `NotFoundException`).
- **Instance-field schema cache** — the validators are DI-Scoped; an instance field would leak parsed schemas across requests. Cache is a LOCAL inside `Validate` (D-10).
- **Setting correlationId/instance in the new handler** — Phase 4 customizer already does it; double-setting risks divergence.
- **Registering the orchestration handler after Fallback** — unreachable (the entire D-04 issue).
- **A shared graph-walk abstraction** — explicitly deferred (D-07 / Phase 13 D-03); the loader BFS, cycle DFS, and edge walk have differing semantics.

## Iterative DFS Cycle Detection (D-07, L1-VALIDATE-03)

**Input:** `snapshot.Steps` (`Dictionary<Guid, StepReadDto>`), each `StepReadDto.NextStepIds` (`List<Guid>?`); start from every `Workflow.EntryStepIds[*]` across `snapshot.Workflows`. `[VERIFIED: StepDtos.cs line 51 NextStepIds is List<Guid>?; WorkflowDtos.cs line 62 EntryStepIds is List<Guid>?]`

**Mechanism (explicit stack, no recursion):**
- Use an explicit `Stack<(Guid step, IEnumerator<Guid> children)>` OR a stack of frames carrying enough to reconstruct the path (Claude's discretion per CONTEXT line 58).
- Maintain a `HashSet<Guid> onStack` (current DFS path) and a `HashSet<Guid> fullyVisited` (completed subtrees) for correct cycle detection across shared subgraphs.
- **Cycle:** when about to visit a child already in `onStack` → reconstruct `stepChain` from the current path and throw `Cycle(stepChain)`.
- **Missing-step:** for each `nextId` in `step.NextStepIds`, if `!snapshot.Steps.ContainsKey(nextId)` → throw `MissingStep(parentStepId, nextId)` (D-08). Note REQUIREMENTS phrases the check as the missing-step gate per L1-VALIDATE-04; CONTEXT D-07 folds it into `CycleDetector`.
- **Terminal:** `null` or empty `NextStepIds` → step has no children, passes.

**Note on the CONTEXT D-07 wording:** CONTEXT says "On each visit, check `visited.Contains(stepId)` BEFORE adding → if already present, throw cycle." A single `visited` set conflates "seen on this path" with "seen anywhere" and will FALSE-POSITIVE on shared subgraphs (a diamond DAG: A→B, A→C, B→D, C→D is acyclic but D is reached twice). **The correct algorithm needs two sets (`onStack` + `fullyVisited`), not one** — see Open Question A2. The loader's BFS uses a single `visited` list deliberately (it only needs termination, not cycle-vs-DAG discrimination), which is why the loader CANNOT reject cycles (D-08) — that is this gate's job.

**Edge cases:**
- Multiple entry steps: run DFS seeded from ALL `EntryStepIds[*]`, sharing `fullyVisited` across seeds (a node fully cleared from one entry need not be re-walked).
- Self-loop (A→A): A appears in `onStack` when its own child A is examined → cycle of `[A, A]` or `[A]`.
- Shared subgraph / diamond: must NOT be flagged as a cycle (requires two-set algorithm).
- Terminal `null`/empty `NextStepIds`: passes.

## Schema-Edge Validator (D-09, L1-VALIDATE-05)

For every parent step in `snapshot.Steps`, for EVERY `childId` in `parent.NextStepIds` (not just the first):
- Resolve `parentOut = snapshot.Processors[parent.ProcessorId].OutputSchemaId` (`Guid?`).
- Resolve `childIn = snapshot.Processors[child.ProcessorId].InputSchemaId` (`Guid?`).
- If `parentOut is null || childIn is null` → edge PASSES (Phase 10 source/sink/unconfigured).
- Else require `parentOut == childIn` (strict Guid equality); on mismatch throw `SchemaEdge(parent.Id, child.Id)`.

`[VERIFIED: ProcessorDtos.cs lines 48–50 — InputSchemaId/OutputSchemaId/ConfigSchemaId are all Guid?; StepDtos.cs line 50 ProcessorId is Guid]`. Independent walk — does NOT share the cycle DFS (D-07).

## Payload↔ConfigSchema Validator (D-10, L1-VALIDATE-06/08)

For every `assignment` in `snapshot.Assignments.Values`:
- Resolve `step = snapshot.Steps[assignment.StepId]` → `proc = snapshot.Processors[step.ProcessorId]` → `cfgId = proc.ConfigSchemaId` (`Guid?`).
- If `cfgId is null` → PASS (no schema to validate against).
- Else `def = snapshot.Schemas[cfgId.Value].Definition` (string); parse to `JsonSchema` via the per-Start cache; evaluate `assignment.Payload`; on `!IsValid` throw `PayloadConfigSchema(assignment.Id, flattenedErrors)`.

`[VERIFIED: AssignmentDtos.cs lines 43–44 — StepId is Guid, Payload is string; SchemaDtos.cs line 38 Definition is string]`

**Per-Start cache (D-10/L1-VALIDATE-08):** `var schemaCache = new Dictionary<Guid, JsonSchema>();` as a LOCAL inside `Validate`; `schemaCache.TryGetValue(cfgId, out var s)` else `s = JsonSchema.FromText(def); schemaCache[cfgId] = s;`. Keyed by `Schema.Id` (== `cfgId`). Never an instance field (validator is Scoped).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| JSON Schema validation | Custom validator | `JsonSchema.Net` `Evaluate` | Draft 2020-12 is a large spec; the lib is already in use + SSRF-locked |
| Error flattening | Custom tree walk | `OutputFormat.List` → `Details[*].Errors` | List format flattens for you |
| RFC 7807 + correlationId/instance | Manual JSON | `IProblemDetailsService.TryWriteAsync` + Phase 4 customizer | Already wired; consistent shape |
| Exception→HTTP mapping | try/catch in controller | `IExceptionHandler` chain | Established pattern; centralizes status codes |

**Key insight:** every primitive this phase needs already exists in the codebase — the work is wiring (handler ordering) and three pure-function gate bodies, not new infrastructure.

## Runtime State Inventory

> N/A — Phase 14 is code-only (fill seam bodies + add one exception/handler + extract a static config). No rename/refactor of stored data, no migrations, no new SQL entities/columns (PROJECT.md v3.3.0 locked constraint). The D-05 refactor of `SchemaCreateDtoValidator`/`SchemaUpdateDtoValidator` is a source-only change (no persisted state). **Nothing in any runtime-state category — verified by: no DB schema change, no env/secret rename, no OS registration, no build-artifact rename.**

## Common Pitfalls

### Pitfall 1: Orchestration 422 handler unreachable behind Fallback
**What goes wrong:** Appending `AddExceptionHandler<OrchestrationValidationExceptionHandler>()` in `AddOrchestrationFeature` registers it after Fallback → 500 instead of 422.
**Why:** `AddBaseApiErrorHandling` (with Fallback last) runs at `AddBaseApi` call #3, before `AddAppFeatures`.
**How to avoid:** Split Fallback into a separate `AddBaseApiFallbackHandler()` called last (recommended), or a Core pre-Fallback hook. See D-04 Resolution.
**Warning signs:** A multi-failure integration test asserting 422 returns 500; `StartCleanupFacts` pattern (forced throw → 500) is the canary.

### Pitfall 2: Single-`visited`-set cycle detection false-positives on DAGs
**What goes wrong:** A diamond/shared-subgraph workflow is wrongly rejected as a cycle.
**Why:** One `visited` set cannot distinguish "on current path" from "seen in a sibling branch."
**How to avoid:** Two sets — `onStack` (current path) + `fullyVisited` (completed). Only `onStack` membership = cycle.
**Warning signs:** Valid multi-entry / fan-in workflows return 422 cycle.

### Pitfall 3: SSRF regression from cctor not firing
**What goes wrong:** After extracting `JsonSchemaConfig`, the Schema validators no longer set `SchemaRegistry.Global.Fetch` because the static ctor never runs → `<500ms` test could regress (outbound $ref fetch).
**Why:** Static ctors run on first member access; if the validator stops touching the type, the lockdown never applies.
**How to avoid:** Ensure both Schema validators AND `PayloadConfigSchemaValidator` reference `JsonSchemaConfig.DefaultOptions` (or call a `JsonSchemaConfig.EnsureConfigured()` no-op) so the cctor runs. Keep `ErrorMappingFacts` green throughout (bisect-friendly commit, D-06).
**Warning signs:** `Create_Schema_Invalid_JsonSchema_Returns400_NoOutboundCall` exceeds 500ms or the field-level "Definition" error disappears.

### Pitfall 4: Nullable `EvaluationResults.Errors`
**What goes wrong:** `results.Errors.Select(...)` throws NRE when a node has no errors.
**Why:** `Errors` is `Dictionary<string,string>` but is null/empty on passing nodes; under List format the error-bearing nodes are in `Details`.
**How to avoid:** Guard with `d.Errors is { Count: > 0 }`; flatten over `Details`, with a top-level `Errors` fallback.

### Pitfall 5: Handler claims the wrong exception
**What goes wrong:** New handler returns true for non-orchestration exceptions, or `ValidationExceptionHandler` (FluentValidation→400) is shadowed.
**Why:** Missing the Pitfall-6 fast-bail `is not OrchestrationValidationException → return false`.
**How to avoid:** First line must be `if (exception is not OrchestrationValidationException ex) return false;` (mirror NotFound/Validation handlers).

## Code Examples

### Mirror of the fast-bail handler `[VERIFIED: ValidationExceptionHandler.cs / NotFoundExceptionHandler.cs]`
```csharp
// Source: src/BaseApi.Core/Exceptions/Handlers/ValidationExceptionHandler.cs line 43
if (exception is not OrchestrationValidationException ex) return false;  // Pitfall 6
httpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
```

### Existing SSRF lockdown (lift source) `[VERIFIED: SchemaDtoValidator.cs lines 25–31]`
```csharp
static SchemaCreateDtoValidator()
{
    Dialect.Default = Dialect.Draft202012;             // VALID-08
    SchemaRegistry.Global.Fetch = (_, _) => null;      // VALID-09 SSRF defense-in-depth
}
```

### Existing evaluate+flatten template `[VERIFIED: SchemaDtoValidator.cs lines 54–63]`
```csharp
var results = MetaSchemas.Draft202012.Evaluate(
    doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
if (!results.IsValid) { /* surface error */ }
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| JsonSchema.Net `ValidationResults` / `Validate()` (pre-4.x) | `EvaluationResults` / `Evaluate()` + `OutputFormat` | json-everything 4.x+ | The codebase already uses the modern API (9.2.1); no migration needed |
| Library default dialect = Draft V1 | Must set `Dialect.Default = Draft202012` explicitly | ongoing | Confirmed still required in 9.2.1 (existing code + docs) |

**Deprecated/outdated:** none relevant — solution is on current JsonSchema.Net 9.2.1.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Existence gate stays **404** (existing `NotFoundException` path), despite L1-VALIDATE-02 literal "→ 422" | Phase Requirements / diagram | If the requirement's 422 is literal, the planner must change the existence path AND update the locked `StartOrchestrationFacts.Start_Returns404_*` tests — a v3.2.0-behavior change. Needs user/planner confirmation. |
| A2 | Correct cycle detection requires TWO sets (`onStack` + `fullyVisited`), not the single `visited` set CONTEXT D-07 describes | Iterative DFS | Single-set impl false-positives on DAGs (diamonds/fan-in). If the test corpus has no shared subgraphs the single set "works" but is incorrect. Recommend two-set; confirm with planner. |
| A3 | `JsonSchema.FromText(string)` parses `Schema.Definition` without throwing for already-validated schemas | JsonSchema.Net API | Definitions are meta-validated at Schema-create time (Phase 8), so a stored Definition is a valid draft-2020-12 schema; `FromText` should not throw. If a malformed Definition somehow persisted, `FromText` could throw → would hit Fallback (500). Low risk given Phase 8 gate. |

## Open Questions (RESOLVED)

> All three resolved during plan-phase (2026-05-29). See CONTEXT.md D-13/D-14 and Plan 14-01/14-02.

1. **Existence gate status code (A1): 404 or 422? → RESOLVED: keep 404 (D-13, user decision).**
   - What we know: existing `ExistenceCheckAsync` throws `NotFoundException` → 404, locked by `StartOrchestrationFacts` tests; CONTEXT D-12 says "re-uses the v3.2.0 path."
   - What's unclear: L1-VALIDATE-02 and ORCH-START-03 literal wording say "→ 422."
   - **Resolution (D-13):** Existence gate stays **404** (re-uses v3.2.0 path); only the THREE new structural gates return 422. `StartOrchestrationFacts` 404 assertion stays GREEN. L1-VALIDATE-02 satisfied by re-use + first-in-order short-circuit.

2. **Two-set vs single-set cycle algorithm (A2). → RESOLVED: two-set (D-14).**
   - **Resolution (D-14):** Implement two-set (`onStack` + `fullyVisited`); add a diamond/fan-in DAG test that proves no false-positive (Plan 14-02). Single-`visited` sketch in D-07 is corrected.

3. **D-04 mechanism (a-2 split-Fallback vs static hook). → RESOLVED: split-Fallback.**
   - **Resolution:** Split Fallback into `AddBaseApiFallbackHandler()` called last in the composition root — keeps Fallback provably last-walked, lets `AddOrchestrationFeature` append its 422 handler before it (Plan 14-01 Task 1).

## Environment Availability

> SKIPPED for external tooling — Phase 14 adds no new external dependencies (no new packages; JsonSchema.Net 9.2.1 already present). Integration tests require the existing Postgres + Redis fixtures (`Phase8WebAppFactory`), which are already operational from Phases 12–13.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (xUnit v3 style — `TestContext.Current.CancellationToken`) + `WebApplicationFactory<Program>` `[VERIFIED: StartOrchestrationFacts.cs, StartCleanupFacts.cs]` |
| Config file | none — convention-based; `[Trait("Phase", "14")]` per phase |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "Phase=14"` |
| Full suite command | `dotnet test` (requires Postgres + Redis compose stack up) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| L1-VALIDATE-01 | Multi-failure workflow → FIRST gate's 422 (order proof) | integration | `dotnet test --filter "Phase=14"` | ❌ Wave 0 |
| L1-VALIDATE-01 | L1 cleanup (`IsDisposed`+empty dicts) runs on validation-failure path | integration | mirror `StartCleanupFacts` pattern (recording loader) | ❌ Wave 0 |
| L1-VALIDATE-02 | Missing workflow id → 404 (per A1) with offending id | integration | already exists: `StartOrchestrationFacts.Start_Returns404_*` | ✅ (reuse) |
| L1-VALIDATE-03 | Cycle workflow → 422 with `stepChain` | integration | new cycle fact | ❌ Wave 0 |
| L1-VALIDATE-03 | Diamond/DAG → NOT flagged as cycle (A2 guard) | integration | new DAG fact | ❌ Wave 0 |
| L1-VALIDATE-04 | Missing NextStepId → 422 `(parent, missingChild)` (crafted snapshot, white-box) | unit/white-box | resolve `CycleDetector` from DI scope + crafted snapshot | ❌ Wave 0 |
| L1-VALIDATE-05 | Schema-edge mismatch → 422 `(parent, child)`; null-side passes | integration | new schema-edge fact | ❌ Wave 0 |
| L1-VALIDATE-06 | Payload fails ConfigSchema → 422 `assignmentId` + errors; null ConfigSchemaId passes | integration | new payload fact (seed Schema+Processor.ConfigSchemaId+Assignment) | ❌ Wave 0 |
| L1-VALIDATE-07 | SSRF `<500ms` + draft-2020-12 still green after refactor | integration | existing `ErrorMappingFacts.Create_Schema_Invalid_JsonSchema_Returns400_NoOutboundCall` | ✅ (must stay green) |
| L1-VALIDATE-08 | Single Schema referenced by N assignments parsed once | unit/white-box | counting/instrumented test OR assert via timing/behavior | ❌ Wave 0 |
| L1-VALIDATE-09 | Assignment PUT/POST still "valid JSON only" | integration | existing `AssignmentsIntegrationTests` (must stay green) | ✅ (reuse) |
| L1-VALIDATE-10 | TEST-03/04 deferred — documentation | n/a | STATE/SUMMARY note | n/a |

### Sampling Rate
- **Per task commit:** `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "Phase=14"` plus the SSRF guard `--filter "FullyQualifiedName~ErrorMappingFacts"` (keep D-06 green every commit).
- **Per wave merge:** full `dotnet test` (Postgres + Redis up).
- **Phase gate:** full suite green (142 v3.2.0 baseline + v3.3.0 additions) before `/gsd-verify-work`; v3.2.0 invariants (SSRF `<500ms`, RFC 7807, Assignment "valid JSON only") MUST NOT regress.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Features/Orchestration/CycleDetectionFacts.cs` — L1-VALIDATE-03 (cycle 422 + DAG non-false-positive)
- [ ] `tests/BaseApi.Tests/Features/Orchestration/MissingStepFacts.cs` — L1-VALIDATE-04 (white-box crafted snapshot)
- [ ] `tests/BaseApi.Tests/Features/Orchestration/SchemaEdgeFacts.cs` — L1-VALIDATE-05
- [ ] `tests/BaseApi.Tests/Features/Orchestration/PayloadConfigSchemaFacts.cs` — L1-VALIDATE-06 + L1-VALIDATE-08 (cache)
- [ ] `tests/BaseApi.Tests/Features/Orchestration/ValidationOrderFacts.cs` — L1-VALIDATE-01 (first-gate short-circuit + L1 cleanup on validation-failure path; reuse `StartCleanupFacts` recording-loader pattern with a domain-exception-throwing setup)
- [ ] Seeding helper: extend the `Processor→Step→Workflow` HTTP seeding (in `StartOrchestrationFacts`/`WorkflowGraphLoaderFacts`) to also seed Schemas + Assignments + multi-step graphs with `NextStepIds` (junctions via Step/Workflow PUT/POST).
- [ ] No framework install needed — xUnit + `Phase8WebAppFactory` already present.

## Security Domain

> `security_enforcement` not present in config.json → treated as enabled. Phase 14's security surface is narrow and centers on the SSRF non-regression.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No auth in v3.x (PROJECT.md out of scope) |
| V3 Session Management | no | Stateless API |
| V4 Access Control | no | No authz layer this milestone |
| V5 Input Validation | yes | The three gates ARE input validation of the workflow graph; JsonSchema.Net evaluates Assignment payloads |
| V6 Cryptography | no | None introduced |
| V13 SSRF / external refs | yes | `SchemaRegistry.Global.Fetch = (_,_) => null` — no outbound `$ref` fetch; `JsonSchemaConfig` is the single source of truth |
| V12 DoS / resource | yes | Iterative (no recursion) cycle detection prevents `StackOverflowException`; per-Start parse cache bounds repeated parsing |

### Known Threat Patterns
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| SSRF via external `$ref` in a stored Schema Definition | Information Disclosure / Tampering | `SchemaRegistry.Global.Fetch = (_,_) => null`; `<500ms` regression assertion (D-06) |
| Crafted cyclic/deep workflow → stack overflow DoS | Denial of Service | Iterative DFS with explicit stack (L1-VALIDATE-03); loader BFS already terminates on cycles |
| Information leak via 500 stack trace | Information Disclosure | New handler emits typed 422 (no stack); Fallback already strips stack from body (Phase 4 D-12) |
| Unbounded schema re-parse | Denial of Service | Per-Start `Dictionary<Guid, JsonSchema>` cache (L1-VALIDATE-08) |

## Project Constraints (from CLAUDE.md / config)
- No root `CLAUDE.md` found in working directory. `[VERIFIED: file does not exist]`
- MEMORY.md note: Workflow/Step M2M (`EntryStepIds`/`AssignmentIds`/`NextStepIds`) are FK-enforced junctions enriched by the loader on the L1 path — NOT entity members. The gates read them off the snapshot DTOs (already enriched). `[VERIFIED: WorkflowGraphLoader.cs lines 110–123]`
- PROJECT.md v3.3.0 locked: validation order is mandatory; schema-edge is strict `Schema.Id` equality with null-passes; NO new SQL entities/columns.
- v3.2.0 invariants that MUST NOT regress (ROADMAP Phase 14): Phase 8 SSRF lockdown + `<500ms`; Phase 4 RFC 7807 + X-Correlation-Id + SQLSTATE→HTTP; Phase 6 FluentValidation 12 manual `ValidateAsync` (no `AddFluentValidation`); Assignment-PUT/POST "valid JSON only"; Mapperly RMG codes; byte-identical `psql \l` SHA-256.

## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01** One new exception type (`OrchestrationValidationException`) carrying a gate discriminator + typed offending payload; mirrors `NotFoundException` (one exception → one handler → typed extensions), NOT per-gate subclasses.
- **D-02** One new `IExceptionHandler` → HTTP 422 RFC 7807, reached BEFORE Core `FallbackExceptionHandler`. Handler does NOT set `correlationId`/`instance` (Phase 4 customizer does). Sets only Status (422), Title (gate-specific), Detail, `errors`.
- **D-03** `errors` extension = `{ gate, offending }`; per-gate offending: cycle→`{ stepChain }`, missingStep→`{ parentStepId, missingChildId }`, schemaEdge→`{ parentStepId, childStepId }`, payloadConfigSchema→`{ assignmentId, errors[] }`.
- **D-04** Fallback registers LAST; `AddExceptionHandler` order == walk order; the orchestration handler (in Service per D-11) cannot simply be appended after `AddBaseApiErrorHandling`. Pick (a) register before Core Fallback in composition, or (b) Core pre-Fallback seam — keep Fallback last-walked + handler reachable. (RESEARCH resolves: recommend split-Fallback `AddBaseApiFallbackHandler()` last.)
- **D-05** Shared `JsonSchemaConfig` in BaseApi.Service (near Schema or a Service `Validation` folder) = single SSRF source of truth (`Dialect.Default = Draft202012` + `SchemaRegistry.Global.Fetch = (_,_) => null`), exposes default `EvaluationOptions`. Refactor both Schema validators to consume it; `PayloadConfigSchemaValidator` consumes the same.
- **D-06** Phase 8 SSRF defense MUST NOT regress — `<500ms` assertion + draft-2020-12 stay GREEN. Bisect-friendly commit.
- **D-07** `CycleDetector.Validate` = ONE iterative DFS (explicit stack/list, NO recursion) over `Steps[*].NextStepIds` from each `EntryStepIds[*]`; check visited before adding → cycle; unknown NextStepId → missingStep. null/empty NextStepIds = terminal. `SchemaEdgeValidator` does its OWN independent edge walk; no shared traversal abstraction.
- **D-08** missing-step is defense-in-depth (FK-Restrict on `StepNextSteps`), but L1-VALIDATE-04 mandates it — implement + test via crafted snapshot.
- **D-09** Schema-edge: for EVERY entry in `parent.NextStepIds`, `parent.ProcessorId→OutputSchemaId` vs `child.ProcessorId→InputSchemaId`, strict Guid equality; null either side passes; mismatch → schemaEdge `(parentStepId, childStepId)`.
- **D-10** Payload↔ConfigSchema: iterate `Assignments`; resolve `StepId→ProcessorId→ConfigSchemaId→Definition`; parse + validate `Payload` (draft 2020-12, D-05 options); null ConfigSchemaId passes; failure → payloadConfigSchema `assignmentId` + flattened JsonSchema.Net error strings. Per-Start `Dictionary<Guid, JsonSchema>` keyed by Schema.Id — a LOCAL inside `Validate` (Scoped seam; never an instance field).
- **D-11** New exception + handler live in `BaseApi.Service.Features.Orchestration` (registered into Core chain per D-04); `JsonSchemaConfig` in BaseApi.Service near Schema; BaseApi.Core stays generic (no orchestration types promoted to Core).
- **D-12** VALID-21 closes ONLY at orchestration-start; Assignment-PUT/POST remain "valid JSON only"; TEST-03/04 remain deferred.

### Claude's Discretion
- Exact class/file names, offending-payload record types, gate discriminator string-vs-enum.
- The precise composition-root mechanism satisfying D-04 (RESEARCH recommends split-Fallback `AddBaseApiFallbackHandler()` last).
- Whether the per-Start cache is an inline local or a tiny private helper struct.
- Whether the DFS stack stores raw `Guid` step ids or `(parent, child)` frames to reconstruct `stepChain`.

### Deferred Ideas (OUT OF SCOPE)
- Redis (L2) projection WRITE + Stop-as-EXISTS — Phase 15.
- Idempotency / concurrency / end-to-end happy-path / 3-GREEN closeout — Phase 16.
- VALID-21 at Assignment HTTP-write (PUT/POST) — FUTURE-VALID-21-HTTP-WRITE; never in v3.3.0.
- Schema-edge STRUCTURAL compatibility (subset/canonical) — strict `Schema.Id` equality only this milestone.
- Shared graph-walk abstraction (loader/cycle/edge walks) — still not built (D-07 confirms separate walks).
- Promotion of `OrchestrationValidationException`/handler/`JsonSchemaConfig` to BaseApi.Core — until a second consumer surfaces.

## Sources

### Primary (HIGH confidence)
- Source files (all read this session): `ErrorHandlingServiceCollectionExtensions.cs`, `BaseApiServiceCollectionExtensions.cs`, `BaseApiApplicationBuilderExtensions.cs`, `OrchestrationServiceCollectionExtensions.cs`, `AppFeatures.cs`, `Program.cs`, `OrchestrationService.cs`, `WorkflowGraphSnapshot.cs`, `WorkflowGraphLoader.cs`, `CycleDetector.cs`, `SchemaEdgeValidator.cs`, `PayloadConfigSchemaValidator.cs`, `SchemaDtoValidator.cs`, `NotFoundException.cs`, `NotFoundExceptionHandler.cs`, `ValidationExceptionHandler.cs`, `FallbackExceptionHandler.cs`, all five `*Dtos.cs`, `Directory.Packages.props`, `BaseApi.Service.csproj`.
- Tests read: `StartCleanupFacts.cs`, `StartOrchestrationFacts.cs`, `WorkflowGraphLoaderFacts.cs`, `ErrorMappingFacts.cs` (SSRF `<500ms`).
- Planning: `14-CONTEXT.md`, `REQUIREMENTS.md` §L1-VALIDATE-01..10 + §ORCH-START-03, `ROADMAP.md` §Phase 14, `config.json`.
- json-everything docs (Context7 `/websites/json-everything_net`): JsonSchema.FromText, JsonSchema.Evaluate, EvaluationResults class (Errors/Details/IsValid/InstanceLocation), OutputFormat enum (Flag/List/Hierarchical), evaluation basics. `[CITED: docs.json-everything.net]`

### Secondary (MEDIUM confidence)
- none requiring cross-verification — all claims tied to source or official docs.

### Tertiary (LOW confidence)
- none.

## Metadata

**Confidence breakdown:**
- D-04 mechanism: HIGH — composition order verified in `Program.cs` + `BaseApiServiceCollectionExtensions`; Fallback-last verified in source + the Phase 13 `StartCleanupFacts` 500 assertion.
- JsonSchema.Net API: HIGH — existing code compiles against 9.2.1 with these exact members; docs confirm `FromText`/`Evaluate`/`EvaluationResults.Errors`/`OutputFormat.List`.
- Snapshot input contract: HIGH — every field type/name verified against the five `*Dtos.cs`.
- Gate algorithms: HIGH for mechanics; MEDIUM-flagged for the single-vs-two-set cycle nuance (A2 — a correctness concern the planner should resolve).
- Existence-gate status code: MEDIUM — A1 conflict between locked tests (404) and requirement literal (422).

**Research date:** 2026-05-29
**Valid until:** 2026-06-28 (stable — internal codebase + pinned dependency; JsonSchema.Net pinned at 9.2.1)
