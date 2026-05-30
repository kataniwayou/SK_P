using MassTransit;
using Messaging.Contracts;

namespace BaseConsole.Core.Messaging;

/// <summary>
/// Bus-wide outbound publish filter (CORR-02 / D-01). Identical body to the send filter against
/// <see cref="PublishContext{T}"/>: stamps the ambient correlation id onto the envelope
/// (<c>context.CorrelationId</c>) for every <see cref="ICorrelated"/> message whose ambient id
/// parses as a <see cref="Guid"/>. The record body is never mutated — <see cref="ICorrelated"/>
/// stays get-only (D-01).
/// </summary>
public sealed class OutboundCorrelationPublishFilter<T>(ICorrelationAccessor accessor)
    : IFilter<PublishContext<T>> where T : class
{
    public Task Send(PublishContext<T> context, IPipe<PublishContext<T>> next)
    {
        if (context.Message is ICorrelated && Guid.TryParse(accessor.Get(), out var id))
            context.CorrelationId = id;   // envelope, not body (D-01)
        return next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("correlation-out-publish");
}
