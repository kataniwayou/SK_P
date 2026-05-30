using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;

namespace BaseConsole.Core.Messaging;

/// <summary>
/// Bus-wide inbound consume filter (CORR-01). Reads the correlation id off the MassTransit
/// envelope, publishes it into the ambient <see cref="ICorrelationAccessor"/>, and opens a MEL
/// log scope under <see cref="CorrelationKeys.LogScope"/> (the literal <c>"CorrelationId"</c>)
/// so OTel <c>IncludeScopes</c> serializes one shared correlation attribute end-to-end.
///
/// <para>
/// Security (T-18-04): the inbound id is treated as opaque untrusted text — placed only as a
/// scope VALUE under the fixed key, never interpolated into a message template. The
/// <see cref="Guid.NewGuid"/> fallback bounds the value when the envelope id is absent.
/// </para>
/// </summary>
public sealed class InboundCorrelationConsumeFilter(
    ICorrelationAccessor accessor, ILogger<InboundCorrelationConsumeFilter> logger)
    : IFilter<ConsumeContext>
{
    public async Task Send(ConsumeContext context, IPipe<ConsumeContext> next)
    {
        var corrId = context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString();
        accessor.Set(corrId);
        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = corrId }))
            await next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("correlation-in");
}
