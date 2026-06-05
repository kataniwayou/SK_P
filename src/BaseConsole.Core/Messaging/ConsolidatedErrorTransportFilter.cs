using MassTransit;

namespace BaseConsole.Core.Messaging;

/// <summary>
/// DLQ-1 (D-05/06): consolidate EVERY console's Immediate(N) transport exhaustion into one shared
/// skp-dlq-1 (x-message-ttl = 7 days), replacing the per-{queue}_error default. GenerateFaultFilter is
/// kept upstream so the Fault&lt;T&gt; pub/sub stream Keeper rides keeps publishing.
/// <para>
/// This is a custom <see cref="IFilter{T}"/> over <see cref="ExceptionReceiveContext"/> installed via
/// <c>ConfigureError</c> in the once-per-endpoint <c>AddConfigureEndpointsCallback</c> (BaseConsole.Core,
/// all three consoles). It mirrors MassTransit's default <c>ErrorTransportFilter</c> move — but instead of
/// resolving the per-endpoint <c>IErrorTransport</c> (which targets <c>{queue}_error</c>) from the context
/// payload, it forwards the ORIGINAL faulted message to ONE fixed <see cref="Dlq1"/> send endpoint.
/// </para>
/// <para>
/// The move is faithful: the raw <see cref="MessageBody"/> bytes are forwarded verbatim with the original
/// <c>ContentType</c>, the original transport headers, AND the framework-generated exception headers
/// (<c>MT-Fault-*</c> on <see cref="ExceptionReceiveContext.ExceptionHeaders"/>) so the message landing in
/// skp-dlq-1 carries the same forensic surface an operator would find in the default <c>_error</c> queue
/// (T-36-11). The destination is a single hard-coded const (<see cref="Dlq1"/>) — never config-injected —
/// so no fault can be misrouted to the wrong queue (T-36-10).
/// </para>
/// <para>
/// API surface confirmed against the MassTransit 8.5.5 assemblies (Plan 36-03 Task-1 source-read + spike):
/// <c>ConfigureError(Action&lt;IPipeConfigurator&lt;ExceptionReceiveContext&gt;&gt;)</c>;
/// <c>MassTransit.Middleware.GenerateFaultFilter</c> (public, parameterless ctor) stays upstream;
/// <see cref="ExceptionReceiveContext"/> exposes <c>SendEndpointProvider</c>, <c>Body</c>, <c>ContentType</c>,
/// <c>TransportHeaders</c>, and <c>ExceptionHeaders</c>.
/// </para>
/// <para>
/// OPERATOR NOTE (Pitfall 2, recorded for Plan 04 / Phase 39 — NOT code): x-message-ttl applies only at
/// queue-create time; if a skp-dlq-1 ever existed on the broker WITHOUT the TTL, it must be deleted once
/// before the corrected declaration takes effect.
/// </para>
/// </summary>
public sealed class ConsolidatedErrorTransportFilter : IFilter<ExceptionReceiveContext>
{
    /// <summary>
    /// The ONE shared consolidated dead-letter queue for transport-exhaustion across all consoles
    /// (replaces the per-{queue}_error default). Declared once in BaseConsole.Core with x-message-ttl = 7 days.
    /// </summary>
    public const string Dlq1 = "skp-dlq-1";

    private static readonly Uri Dlq1Uri = new($"queue:{Dlq1}");

    public async Task Send(ExceptionReceiveContext context, IPipe<ExceptionReceiveContext> next)
    {
        // Resolve the ONE fixed consolidated destination (NOT the payload-injected per-endpoint
        // IErrorTransport, which would target {queue}_error). Const-only — no config-injected destination.
        var endpoint = await context.SendEndpointProvider.GetSendEndpoint(Dlq1Uri).ConfigureAwait(false);

        // Forward the ORIGINAL faulted message body verbatim, preserving content-type, the original
        // transport headers, and the framework exception headers (MT-Fault-*) so the forensic surface in
        // skp-dlq-1 mirrors the default _error move.
        var body = context.Body.GetBytes();

        await endpoint.Send(body, sendContext =>
        {
            sendContext.ContentType = context.ContentType;

            foreach (var header in context.TransportHeaders.GetAll())
                sendContext.Headers.Set(header.Key, header.Value);

            foreach (var exceptionHeader in context.ExceptionHeaders.GetAll())
                sendContext.Headers.Set(exceptionHeader.Key, exceptionHeader.Value);
        }, context.CancellationToken).ConfigureAwait(false);

        await next.Send(context).ConfigureAwait(false);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("consolidatedErrorTransport");
}
