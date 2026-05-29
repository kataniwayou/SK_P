using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using Microsoft.Extensions.Logging;

namespace BaseApi.Service.Features.Orchestration;

/// <summary>
/// Transient L1 (in-memory) read-model of a workflow graph (L1-BUILD-01).
/// Five flat <see cref="Dictionary{TKey,TValue}"/> projections of the requested
/// workflows' entities, built inside <c>OrchestrationService.StartAsync</c> and
/// discarded at the end of the request via a <c>using</c> declaration.
/// <para>
/// <b>Disposal contract (L1-BUILD-05 / D-04):</b> <see cref="Dispose"/> is
/// idempotent — it clears all five dictionaries, flips <see cref="IsDisposed"/> to
/// <c>true</c>, and emits the literal <c>ILogger.LogDebug("L1 snapshot disposed")</c>
/// at the moment of disposal. The snapshot OWNS an injected
/// <see cref="ILogger{WorkflowGraphSnapshot}"/> (passed by the loader, which already
/// holds one) so the diagnostic line lives exactly where disposal happens.
/// </para>
/// <para>
/// The <c>Logger</c> is a positional record member but is NOT a data member — it is a
/// non-data dependency and does not participate in value-equality over the five
/// dictionaries. The dictionary references are <c>init</c>-only; <see cref="Dispose"/>
/// mutates their CONTENTS via <c>.Clear()</c> (legal) rather than null-ing the
/// references (CS8852 — Pitfall 2). <see cref="IsDisposed"/> is a separate mutable
/// auto-property, not a positional member.
/// </para>
/// </summary>
internal sealed record WorkflowGraphSnapshot(ILogger<WorkflowGraphSnapshot> Logger) : IDisposable
{
    public Dictionary<Guid, WorkflowReadDto>   Workflows   { get; init; } = new();
    public Dictionary<Guid, AssignmentReadDto> Assignments { get; init; } = new();
    public Dictionary<Guid, StepReadDto>       Steps       { get; init; } = new();
    public Dictionary<Guid, ProcessorReadDto>  Processors  { get; init; } = new();
    public Dictionary<Guid, SchemaReadDto>     Schemas     { get; init; } = new();

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed) return;
        Workflows.Clear();
        Assignments.Clear();
        Steps.Clear();
        Processors.Clear();
        Schemas.Clear();
        IsDisposed = true;
        Logger.LogDebug("L1 snapshot disposed");   // D-04 literal — emitted at the moment of disposal
    }
}
