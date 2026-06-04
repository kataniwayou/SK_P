using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;

namespace BaseConsole.Core.Messaging;

/// <summary>
/// Bus-wide inbound consume filter (LOG-02). For an <see cref="IExecutionCorrelated"/> message it
/// opens a MEL log scope carrying the five execution ids under the <see cref="ExecutionLogScope"/>
/// keys (each Guid.Empty value skipped, and an empty-string EntryId skipped — D-03) so OTel IncludeScopes serializes them as
/// <c>attributes.&lt;Key&gt;</c>. It does NOT touch CorrelationId — that stays owned by the unchanged
/// <see cref="InboundCorrelationConsumeFilter{T}"/> (D-01). Any non-IExecutionCorrelated message
/// passes through untouched (D-03). Open-generic for the same DI/registration reasons as the
/// correlation filter. Security (T-18-04): ids are placed only as scope VALUES under fixed keys,
/// never interpolated into a message template; they are server-minted Guids.
/// </summary>
public sealed class InboundExecutionScopeConsumeFilter<T>(
    ILogger<InboundExecutionScopeConsumeFilter<T>> logger) : IFilter<ConsumeContext<T>> where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        if (context.Message is not IExecutionCorrelated ec)
        {
            await next.Send(context);   // D-03 pass-through no-op
            return;
        }

        var state = new Dictionary<string, object>();
        if (ec.WorkflowId  != Guid.Empty) state[ExecutionLogScope.WorkflowId]  = ec.WorkflowId.ToString();
        if (ec.StepId      != Guid.Empty) state[ExecutionLogScope.StepId]      = ec.StepId.ToString();
        if (ec.ProcessorId != Guid.Empty) state[ExecutionLogScope.ProcessorId] = ec.ProcessorId.ToString();
        if (ec.ExecutionId != Guid.Empty) state[ExecutionLogScope.ExecutionId] = ec.ExecutionId.ToString();
        if (!string.IsNullOrEmpty(ec.EntryId)) state[ExecutionLogScope.EntryId] = ec.EntryId;

        using (logger.BeginScope(state))
            await next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("execution-scope-in");
}
