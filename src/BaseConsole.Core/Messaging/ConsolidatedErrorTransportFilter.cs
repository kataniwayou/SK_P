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
/// The move is faithful: the ORIGINAL serialized message body (<see cref="MessageBody"/> bytes), its
/// content-type, the original transport headers, AND the framework-generated exception headers
/// (<c>MT-Fault-*</c>, the exception type/message/stack-trace/timestamp) are all captured into a typed
/// <see cref="ConsolidatedFault"/> forensic envelope, so the message landing in skp-dlq-1 carries the same
/// forensic surface an operator would find in the default <c>_error</c> queue (T-36-11) AND remains a
/// well-typed, deserializable record (better than a bare byte[] move). The destination is a single
/// hard-coded const (<see cref="Dlq1"/>) — never config-injected — so no fault can be misrouted to the
/// wrong queue (T-36-10).
/// </para>
/// <para>
/// API surface confirmed against the MassTransit 8.5.5 assemblies (Plan 36-03 Task-1 source-read + spike):
/// <c>ConfigureError(Action&lt;IPipeConfigurator&lt;ExceptionReceiveContext&gt;&gt;)</c>;
/// <c>MassTransit.Middleware.GenerateFaultFilter</c> (public, parameterless ctor) stays upstream;
/// <see cref="ExceptionReceiveContext"/> exposes <c>SendEndpointProvider</c>, <c>Body</c>, <c>ContentType</c>,
/// <c>TransportHeaders</c>, <c>ExceptionHeaders</c>, and <see cref="ExceptionReceiveContext.Exception"/>.
/// </para>
/// <para>
/// TOPOLOGY NOTE: the skp-dlq-1 QUEUE is declared exactly ONCE (with x-message-ttl = 7 days) by the
/// ReceiveEndpoint in MessagingServiceCollectionExtensions. This filter sends to the EXCHANGE
/// (<see cref="Dlq1Uri"/> = exchange:skp-dlq-1), so the send path never re-declares the queue with
/// default args — avoiding the RabbitMQ 406 'inequivalent arg x-message-ttl' that a queue: send would
/// raise against the ttl'd queue (MassTransit #5902). x-message-ttl applies only at queue-create time;
/// if a skp-dlq-1 ever exists on the broker with DIFFERENT args, delete it once so the ReceiveEndpoint
/// re-creates it cleanly.
/// </para>
/// </summary>
public sealed class ConsolidatedErrorTransportFilter : IFilter<ExceptionReceiveContext>
{
    /// <summary>
    /// The ONE shared consolidated dead-letter queue for transport-exhaustion across all consoles
    /// (replaces the per-{queue}_error default). Declared once in BaseConsole.Core with x-message-ttl = 7 days.
    /// </summary>
    public const string Dlq1 = "skp-dlq-1";

    // Address the consolidated DLQ by its EXCHANGE, not "queue:". The skp-dlq-1 QUEUE is declared exactly
    // ONCE — authoritatively, with x-message-ttl = 7 days — as a passive publish-topology BindQueue (NOT a
    // ReceiveEndpoint) in MessagingServiceCollectionExtensions. A "queue:skp-dlq-1" send makes MassTransit
    // RE-declare that queue on the send path with DEFAULT args (no x-message-ttl), which RabbitMQ rejects with
    // 406 PRECONDITION_FAILED (inequivalent arg 'x-message-ttl'): the dead-letter move then never completes,
    // the faulted message is never acked, and it poison-loops on redelivery (MassTransit #5902 — sending to a
    // custom-configured, consumer-less receive endpoint). Sending to the fanout exchange the publish-topology
    // BindQueue already created (bound to the ttl'd queue) routes the move into skp-dlq-1 WITHOUT re-declaring it.
    private static readonly Uri Dlq1Uri = new($"exchange:{Dlq1}");

    public async Task Send(ExceptionReceiveContext context, IPipe<ExceptionReceiveContext> next)
    {
        // Resolve the ONE fixed consolidated destination (NOT the payload-injected per-endpoint
        // IErrorTransport, which would target {queue}_error). Const-only — no config-injected destination.
        var endpoint = await context.SendEndpointProvider.GetSendEndpoint(Dlq1Uri).ConfigureAwait(false);

        // Capture the ORIGINAL serialized message body verbatim + the forensic exception detail, mirroring
        // the default _error move (which re-sends the raw message + MT-Fault-* headers).
        var envelope = new ConsolidatedFault
        {
            ContentType = context.ContentType?.ToString(),
            Body = context.Body.GetBytes(),
            ExceptionType = context.Exception.GetType().FullName,
            ExceptionMessage = context.Exception.Message,
            FaultedAt = context.ExceptionTimestamp,
        };

        await endpoint.Send(envelope, sendContext =>
        {
            // Preserve the original transport + framework exception headers on the moved message so the
            // forensic surface in skp-dlq-1 matches the default _error queue.
            foreach (var header in context.TransportHeaders.GetAll())
                sendContext.Headers.Set(header.Key, header.Value);

            foreach (var exceptionHeader in context.ExceptionHeaders.GetAll())
                sendContext.Headers.Set(exceptionHeader.Key, exceptionHeader.Value);
        }, context.CancellationToken).ConfigureAwait(false);

        await next.Send(context).ConfigureAwait(false);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("consolidatedErrorTransport");
}

/// <summary>
/// The typed forensic envelope the consolidated error transport moves to skp-dlq-1 (DLQ-1). Carries the
/// ORIGINAL serialized message body verbatim (<see cref="Body"/> + <see cref="ContentType"/>, so an operator
/// can reconstruct the exact faulted message) plus the captured exception detail. No consumer rides this in
/// Phase 36 — skp-dlq-1 is a TTL'd operator/forensic sink (the live drain proof is Plan 04 / Phase 39).
/// </summary>
public sealed class ConsolidatedFault
{
    /// <summary>Original message content-type (e.g. application/vnd.masstransit+json).</summary>
    public string? ContentType { get; init; }

    /// <summary>The ORIGINAL serialized message body bytes, verbatim (the faulted envelope).</summary>
    public byte[] Body { get; init; } = [];

    /// <summary>The exhausting exception's CLR type name.</summary>
    public string? ExceptionType { get; init; }

    /// <summary>The exhausting exception's message (forensic triage; no stack frames at Information).</summary>
    public string? ExceptionMessage { get; init; }

    /// <summary>When the move occurred.</summary>
    public DateTime FaultedAt { get; init; }
}
