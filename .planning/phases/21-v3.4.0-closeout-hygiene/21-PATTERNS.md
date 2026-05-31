# Phase 21: v3.4.0 Closeout Hygiene - Pattern Map

**Mapped:** 2026-05-31
**Files analyzed:** 4 (1 new, 3 modified — incl. 1 doc-only)
**Analogs found:** 4 / 4

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | NEW shared contract (`public static` key helper) | wire-contract source-of-truth (writer + reader) | `src/Messaging.Contracts/CorrelationKeys.cs` (shape) + `RedisProjectionKeys.cs` (content) | exact |
| `src/BaseApi.Service/.../Projection/RedisProjectionKeys.cs` | forwarder shim (`internal static`) | writer-side of L2 wire contract | `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` (becomes-forwarder twin) | exact |
| `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` | forwarder shim (`internal static`) | reader-side of L2 wire contract | `src/BaseApi.Service/.../RedisProjectionKeys.cs` (becomes-forwarder twin) | exact |
| `tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs` | test (doc-comment fix only) | n/a (prose-only, line ~31) | — | n/a (D-06 doc-nit) |

**Note on csproj:** `src/Messaging.Contracts/Messaging.Contracts.csproj` needs NO change. It is a pure-POCO leaf (`RootNamespace`/`AssemblyName` = `Messaging.Contracts`, `GenerateDocumentationFile=true`, no `ItemGroup`). Both hosts already reference it (BaseApi.Service since Phase 17, Orchestrator since Phase 19). The new file drops into the existing `Projections/` folder with zero wiring change.

## Pattern Assignments

### `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (NEW — shared contract, `public static`)

This file is a **merge** of two analogs: copy the *class shape / leaf conventions* from `CorrelationKeys.cs`, and copy the *three builder bodies + authoritative XML scheme doc* verbatim from the writer's current `RedisProjectionKeys.cs`. Per D-04, `Root` adopts the explicit `:D` format (the reader's current form), which is byte-identical to the writer's bare interpolation.

**Analog 1 — leaf shape & file conventions:** `src/Messaging.Contracts/CorrelationKeys.cs` (full file, lines 1-8)
```csharp
namespace Messaging.Contracts;

/// <summary>Cross-service correlation log-scope key. MUST equal the literal
/// CorrelationIdMiddleware uses so OTel IncludeScopes serializes one Elasticsearch attribute.</summary>
public static class CorrelationKeys
{
    public const string LogScope = "CorrelationId";
}
```
Pattern to copy: file-scoped namespace, single XML `<summary>` stating the cross-service contract obligation, `public static class`, zero dependencies (no `using` directives), no config access. NEW file uses namespace `Messaging.Contracts.Projections` (see Analog 2).

**Analog 2 — namespace + folder neighbor:** `src/Messaging.Contracts/Projections/WorkflowRootProjection.cs` (lines 1-3)
```csharp
using System.Text.Json.Serialization;

namespace Messaging.Contracts.Projections;
```
Pattern to copy: the new key class sits beside the L2 *value* shape under `namespace Messaging.Contracts.Projections;` (D-02 cohesion — key shape next to value shape). The new file needs NO `using` (no JSON attributes — only `string`/`Guid`, both in implicit usings).

**Analog 3 — builder bodies + authoritative scheme doc (MOVE VERBATIM):** `src/BaseApi.Service/.../RedisProjectionKeys.cs` (lines 3-25)
```csharp
/// <summary>
/// Single source of truth for the three L2 (Redis) projection key formats (L2-PROJECT-02).
/// <para>
/// The scheme is FLAT: a single configured prefix followed by GUID(s), with NO type
/// discriminator (D-02). Consequently <see cref="Root"/> and <see cref="Processor"/> produce
/// byte-identical strings for the same prefix + GUID — they are disambiguated only by their
/// GUID namespace (a workflow id is never a processor id). GUIDs render in the default
/// <c>Guid.ToString()</c> ("D") format — hyphenated — NOT the "N" (32-digit) format.
/// </para>
/// <list type="bullet">
///   <item><description>Root: <c>{prefix}{workflowId}</c></description></item>
///   <item><description>Step: <c>{prefix}{workflowId}:{stepId}</c></description></item>
///   <item><description>Processor: <c>{prefix}{processorId}</c></description></item>
/// </list>
/// </summary>
internal static class RedisProjectionKeys
{
    public static string Root(string prefix, Guid workflowId) => $"{prefix}{workflowId}";

    public static string Step(string prefix, Guid workflowId, Guid stepId) => $"{prefix}{workflowId}:{stepId}";

    public static string Processor(string prefix, Guid processorId) => $"{prefix}{processorId}";
}
```
Pattern to copy: move this XML summary (the authoritative flat-scheme spec, per code_context) and all three expression-bodied builders into the new class. **D-04 change for `Root` only:** in the new shared class write `$"{prefix}{workflowId:D}"` (explicit `:D`) instead of the bare `$"{prefix}{workflowId}"`. `Step` and `Processor` are copied **byte-for-byte unchanged**. Change `internal static class RedisProjectionKeys` → `public static class L2ProjectionKeys`. `prefix` stays a method parameter on every builder (D-05 — leaf has no config access).

**Correctness guard (specifics + D-04):** the produced *string* must be byte-identical, not just source text. `$"{prefix}{workflowId:D}"` and `$"{prefix}{workflowId}"` both render the hyphenated "D" form; the reader already used `:D`. Verify by asserting `L2ProjectionKeys.Root("skp:", id)` equals the pre-refactor output.

---

### `src/BaseApi.Service/.../Projection/RedisProjectionKeys.cs` (MODIFIED — writer forwarder, `internal static`)

**Current contents (full file, lines 1-25):** shown verbatim in Analog 3 above. This is the writer that currently owns all three builders.

**Analog (becomes-forwarder pattern):** `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` — the reader twin that undergoes the identical `internal static` → forwarder conversion. After refactor both classes have the same shape: keep the `internal static class` + namespace + call signatures, replace each body with a delegation to `L2ProjectionKeys`. Forwarder target form:
```csharp
public static string Root(string prefix, Guid workflowId) => L2ProjectionKeys.Root(prefix, workflowId);
public static string Step(string prefix, Guid workflowId, Guid stepId) => L2ProjectionKeys.Step(prefix, workflowId, stepId);
public static string Processor(string prefix, Guid processorId) => L2ProjectionKeys.Processor(prefix, processorId);
```
**Constraints (D-03):** class stays `internal static class RedisProjectionKeys` in `namespace BaseApi.Service.Features.Orchestration.Projection;` — name/namespace/signatures unchanged so the two callers below are untouched. Add `using Messaging.Contracts.Projections;` (already a referenced assembly — `RedisProjectionWriter.cs` line 4 and `RedisL2Cleanup.cs` line 3 already import it). Per Claude's discretion: keep a short XML summary pointing at `L2ProjectionKeys` as the source of truth, replacing the now-moved full scheme doc.

**Untouched writer callers (must remain behavior-identical):**
- `src/BaseApi.Service/.../Projection/RedisProjectionWriter.cs` — calls `RedisProjectionKeys.Root` (line 80), `.Step` (line 97), `.Processor` (line 112). Already imports `Messaging.Contracts.Projections` (line 4).
- `src/BaseApi.Service/.../Projection/RedisL2Cleanup.cs` — calls `RedisProjectionKeys.Root` (lines 43, 77) and `.Step` (line 63). Already imports `Messaging.Contracts.Projections` (line 3).

---

### `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` (MODIFIED — reader forwarder, `internal static`)

**Current contents (full file, lines 1-19):**
```csharp
namespace Orchestrator.Messaging;

/// <summary>
/// L2 root key shape — duplicated from BaseApi.Service <c>RedisProjectionKeys</c> (which is
/// <c>internal</c> there, so the orchestrator cannot reference it; hoist-vs-duplicate resolved
/// to duplicate per Claude's discretion, 19-RESEARCH Open Question 3).
/// ...
/// </summary>
internal static class OrchestratorL2Keys
{
    public static string Root(string prefix, Guid workflowId) => $"{prefix}{workflowId:D}";
}
```

**Analog (becomes-forwarder pattern):** `src/BaseApi.Service/.../RedisProjectionKeys.cs` (the writer twin). Identical conversion. Forwarder target form:
```csharp
public static string Root(string prefix, Guid workflowId) => L2ProjectionKeys.Root(prefix, workflowId);
```
**Constraints (D-03):** class stays `internal static class OrchestratorL2Keys` in `namespace Orchestrator.Messaging;` — name/namespace/signature unchanged. Add `using Messaging.Contracts.Projections;`. Per Claude's discretion: collapse the now-stale "duplicated from BaseApi.Service" XML doc (lines 3-15) into a short summary pointing at `L2ProjectionKeys` (the duplication it documents no longer exists). Reader still owns only `Root` — it does NOT gain `Step`/`Processor` forwarders (only the writer side needs those today; the shared class carries all three for the Processor milestone per D-01).

**Untouched reader callers (must remain behavior-identical):**
- `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` — `StringGetAsync(OrchestratorL2Keys.Root(...))`.
- `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` — reader caller.

---

### `tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs` (MODIFIED — doc-comment only, D-06)

**No code/analog.** Single prose fix at line 31. Current stale text:
```
///   <item>seeds + projects the workflow's L2 root into the SAME host Redis the orchestrator reads
///   (the Start path writes <c>skp:wf:{id}:root</c>) so the orchestrator reaches the SUCCESS seam;</item>
```
The claimed shape `skp:wf:{id}:root` is wrong; the actual produced key is `skp:{wfId}` (the flat `{prefix}{workflowId}` form — see `L2ProjectionKeys.Root`, matches cleanup + writer). Correct the parenthetical prose to the flat shape. **No behavior change**; this test stays GREEN and is the regression net (D-07).

## Shared Patterns

### Pure-POCO leaf convention (`Messaging.Contracts`)
**Source:** `src/Messaging.Contracts/CorrelationKeys.cs` + `Messaging.Contracts.csproj`
**Apply to:** the new `L2ProjectionKeys.cs`
```csharp
// File-scoped namespace, single contract-obligation <summary>, public static, ZERO dependencies.
namespace Messaging.Contracts; // (new file uses .Projections)
public static class CorrelationKeys { public const string LogScope = "CorrelationId"; }
```
The csproj inherits all common props from `Directory.Build.props`, has no `ItemGroup`, and sets `GenerateDocumentationFile=true` — so the new file MUST carry XML doc (CS1591 is only suppressed at project level via `NoWarn`). No MassTransit/AspNetCore references; `prefix` is a parameter, never config (D-05).

### Forwarder-shim conversion (`internal static` key class → delegate)
**Source:** both `RedisProjectionKeys.cs` and `OrchestratorL2Keys.cs` (the two twins, post-refactor)
**Apply to:** both modified key classes
```csharp
// Keep class name + namespace + method signatures (callers untouched).
// Replace each body with: => L2ProjectionKeys.<Builder>(<same args>);
// Add: using Messaging.Contracts.Projections;
// Keep a one-line XML summary pointing at L2ProjectionKeys as source of truth.
```

### Byte-identical-key correctness guard
**Source:** D-04 + specifics (CONTEXT.md)
**Apply to:** all three builders, verified at refactor time
Assert the *produced string* (not source text) is unchanged: `L2ProjectionKeys.Root("skp:", id)` must equal the pre-refactor writer output `$"skp:{id}"` and reader output `$"skp:{id:D}"`. Optional cheap unit test (Claude's discretion): assert the writer forwarder and reader forwarder return identical strings for the same `(prefix, id)` — the unit-time guard that would have caught the original drift.

## No Analog Found

None — every file has a strong same-leaf or twin analog already in the codebase. This is a pure code-move refactor, not greenfield.

## Metadata

**Analog search scope:** `src/Messaging.Contracts/` (leaf shape + folder neighbor), `src/BaseApi.Service/Features/Orchestration/Projection/` (writer + callers), `src/Orchestrator/Messaging/` + `src/Orchestrator/Consumers/` (reader + callers), `tests/BaseApi.Tests/Orchestrator/` (doc-fix site).
**Files scanned:** 7 (CONTEXT.md, CorrelationKeys.cs, WorkflowRootProjection.cs, RedisProjectionKeys.cs, OrchestratorL2Keys.cs, RedisProjectionWriter.cs, RedisL2Cleanup.cs, CorrelationPropagationE2ETests.cs, Messaging.Contracts.csproj).
**Pattern extraction date:** 2026-05-31
