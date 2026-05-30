using MassTransit;
using Messaging.Contracts;

namespace BaseConsole.Core.Messaging;

/// <summary>
/// Bus-wide outbound send filter (CORR-02 / D-01). Stamps the ambient correlation id onto the
/// MassTransit envelope (<c>context.CorrelationId</c>) for every <see cref="ICorrelated"/>
/// message whose ambient id parses as a <see cref="Guid"/>. The record body is never mutated —
/// <see cref="ICorrelated"/> stays get-only (D-01).
/// </summary>
public sealed class OutboundCorrelationSendFilter<T>(ICorrelationAccessor accessor)
    : IFilter<SendContext<T>> where T : class
{
    public Task Send(SendContext<T> context, IPipe<SendContext<T>> next)
    {
        if (context.Message is ICorrelated && Guid.TryParse(accessor.Get(), out var id))
            context.CorrelationId = id;   // envelope, not body (D-01)
        return next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("correlation-out-send");
}
