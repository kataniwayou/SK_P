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
/// <b>Open-generic by design.</b> The scoped/DI registration surface
/// (<c>UseConsumeFilter(typeof(InboundCorrelationConsumeFilter&lt;&gt;), ctx)</c>) requires a generic
/// type definition — MassTransit resolves a closed instance per consumed message type from the
/// container. Implementing <c>IFilter&lt;ConsumeContext&lt;T&gt;&gt;</c> (not the non-generic
/// <c>IFilter&lt;ConsumeContext&gt;</c>) mirrors the two outbound open-generic filters and is the
/// shape MassTransit 8.5.5 accepts for a bus-wide scoped consume filter.
/// </para>
///
/// <para>
/// Security (T-18-04): the inbound id is treated as opaque untrusted text — placed only as a
/// scope VALUE under the fixed key, never interpolated into a message template. The
/// <see cref="Guid.NewGuid"/> fallback bounds the value when the envelope id is absent.
/// </para>
/// </summary>
public sealed class InboundCorrelationConsumeFilter<T>(
    ICorrelationAccessor accessor, ILogger<InboundCorrelationConsumeFilter<T>> logger)
    : IFilter<ConsumeContext<T>> where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        var corrId = context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString();
        accessor.Set(corrId);
        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = corrId }))
            await next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("correlation-in");
}
